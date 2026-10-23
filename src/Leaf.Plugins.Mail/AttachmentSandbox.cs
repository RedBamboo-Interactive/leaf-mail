using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Leaf.Plugins.Mail;

/// <summary>
/// Runs attachment text extraction in the extractor helper, one process per extraction, inside a
/// Windows job object with hard limits. Glyph amplification cannot be bounded inside PdfPig (a
/// 65 KB PDF can make content parsing allocate gigabytes), so PDF, HTML, calendar and message
/// parsing never run in the kernel process. Plain text and CSV, which are only decoded up to the
/// text cap, stay in process.
/// <para>
/// The helper is resolved from the plugin's own directory, never from PATH. It is started with a
/// minimal environment (no execution token or other secrets), assigned to the job before it
/// receives any bytes, fed the attachment on stdin and read back as bounded JSON on stdout.
/// Any job limit, the wall clock, or a cancelled request kills the whole job; a limit reports
/// <c>too_complex</c>, and a missing, crashing, or misbehaving helper reports <c>unreadable</c>.
/// Nothing is written to disk.
/// </para>
/// </summary>
public sealed class ExtractorSandbox(string? helperPath)
{
    public const string HelperFolder = "extractor";
    public const string HelperFile = "Leaf.Plugins.Mail.Extractor.exe";
    private const string PluginAssemblyFile = "Leaf.Plugins.Mail.dll";

    /// <summary>Committed memory of the whole job (the helper's GC heap limit is 75% of it).</summary>
    public const long JobMemoryLimit = 512L * 1024 * 1024;
    /// <summary>User-mode CPU time of the whole job.</summary>
    public static readonly TimeSpan CpuTimeLimit = TimeSpan.FromSeconds(15);
    /// <summary>Wall clock from start to exit, including runtime start-up.</summary>
    public static readonly TimeSpan WallClockLimit = TimeSpan.FromSeconds(15);
    /// <summary>The job may hold exactly one process: the helper cannot start another.</summary>
    public const int ActiveProcessLimit = 1;
    /// <summary>At most this many extractions run at once, process-wide.</summary>
    public const int MaxConcurrentExtractions = 2;

    private const int KilledExitCode = unchecked((int)0xC0DE0001);
    /// <summary>The helper's exit code for an out-of-memory it caught. Its GC heap is capped at 75% of
    /// the job limit, so a helper that dies above 70% of the limit is counted as out of memory too.</summary>
    private const int OutOfMemoryExitCode = 3;
    private static readonly SemaphoreSlim Gate = new(MaxConcurrentExtractions, MaxConcurrentExtractions);
    private static int _active;
    private static int _maxObserved;

    public string? HelperPath => helperPath;

    /// <summary>The wall clock for one run; shortened only by tests.</summary>
    internal TimeSpan WallClock { get; init; } = WallClockLimit;

    /// <summary>Highest number of extractions observed running at once (diagnostics and tests).</summary>
    public static int MaxObservedConcurrency => Volatile.Read(ref _maxObserved);

    internal static void ResetObservedConcurrency() => Interlocked.Exchange(ref _maxObserved, 0);

    /// <summary>
    /// The helper beside the loaded plugin. A package keeps the plugin in <c>backend/</c>. A workspace
    /// checkout is loaded from the newest <c>Leaf.Plugins.Mail.dll</c> under <c>src/**/bin/</c> (the
    /// kernel's rule), and the helper is copied beside it at build time. The kernel loads plugins
    /// from a stream, so the assembly has no location; when it does (tests), that wins.
    /// </summary>
    public static ExtractorSandbox Resolve(string? pluginDirectory)
        => ResolveFrom(typeof(ExtractorSandbox).Assembly.Location, pluginDirectory);

    internal static ExtractorSandbox ResolveFrom(string? assemblyLocation, string? pluginDirectory)
    {
        if (!string.IsNullOrEmpty(assemblyLocation) && Path.IsPathFullyQualified(assemblyLocation))
            return In(Path.GetDirectoryName(assemblyLocation));
        if (string.IsNullOrEmpty(pluginDirectory) || !Path.IsPathFullyQualified(pluginDirectory)) return new(null);
        var backend = Path.Combine(pluginDirectory, "backend");
        if (Directory.Exists(backend)) return In(backend);
        var src = Path.Combine(pluginDirectory, "src");
        if (!Directory.Exists(src)) return new(null);
        var sep = Path.DirectorySeparatorChar;
        var newest = Directory.EnumerateFiles(src, PluginAssemblyFile, SearchOption.AllDirectories)
            .Where(f => f.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)
                && !f.Contains($"{sep}ref{sep}", StringComparison.OrdinalIgnoreCase)
                && !f.Contains($"{sep}refint{sep}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return In(newest == null ? null : Path.GetDirectoryName(newest));

        static ExtractorSandbox In(string? directory)
        {
            if (directory == null) return new(null);
            var path = Path.GetFullPath(Path.Combine(directory, HelperFolder, HelperFile));
            return new(File.Exists(path) ? path : null);
        }
    }

    /// <summary>Plain text and CSV are decoded in process; everything else with a reader runs in the helper.</summary>
    public static bool RunsInHelper(string kind) => kind is "application/pdf" or "text/html" or "text/calendar" or "message/rfc822";

    /// <summary>
    /// Extraction behind a process-wide gate, honouring the request's cancellation. The gate is
    /// held until the helper has exited, so a cancelled request cannot leave work running.
    /// </summary>
    public async Task<ExtractedAttachment> ExtractAsync(byte[] bytes, string mimeType, string? filename, string? charset, CancellationToken ct)
    {
        var kind = AttachmentExtractor.Kind(mimeType, filename, bytes.AsSpan(0, Math.Min(bytes.Length, 8)));
        if (!RunsInHelper(kind)) return AttachmentExtractor.Extract(bytes, mimeType, filename, charset, ct);

        await Gate.WaitAsync(ct);
        var now = Interlocked.Increment(ref _active);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxObserved)) && Interlocked.CompareExchange(ref _maxObserved, now, seen) != seen) { }
        try { return await RunAsync(bytes, mimeType, filename, charset, kind, ct); }
        finally
        {
            Interlocked.Decrement(ref _active);
            Gate.Release();
        }
    }

    private static ExtractedAttachment Unreadable(string kind, string detail, SandboxStats? stats = null)
        => new(AttachmentStatus.Unreadable, "", false, false, kind, detail, Sandbox: stats);

    private async Task<ExtractedAttachment> RunAsync(byte[] bytes, string mimeType, string? filename, string? charset, string kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (helperPath == null || !File.Exists(helperPath))
            return Unreadable(kind, "The attachment reader is not installed");
        if (!OperatingSystem.IsWindows())
            return Unreadable(kind, "The attachment reader needs Windows job objects");

        JobObject job;
        try { job = JobObject.Create(); }
        catch (Win32Exception) { return Unreadable(kind, "The attachment reader could not be isolated"); }
        using var _ = job;

        var clock = Stopwatch.StartNew();
        using var process = new Process { StartInfo = StartInfo(helperPath) };
        try
        {
            if (!process.Start()) return Unreadable(kind, "The attachment reader could not be started");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return Unreadable(kind, "The attachment reader could not be started");
        }

        // The helper blocks on stdin until it has the bytes, so it is inside the job before it sees any.
        try { job.Assign(process); }
        catch (Win32Exception)
        {
            job.Terminate(KilledExitCode);
            try { process.Kill(); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            return Unreadable(kind, "The attachment reader could not be isolated");
        }

        string? limit = null;
        var oversized = false;
        using var deadline = new CancellationTokenSource(WallClock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        using (stop.Token.Register(() => job.Terminate(KilledExitCode)))
        {
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, AttachmentWire.MaxResponseBytes, () =>
            {
                oversized = true;
                job.Terminate(KilledExitCode);
            });
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try
            {
                await AttachmentWire.WriteRequestAsync(process.StandardInput.BaseStream, bytes, mimeType, filename, charset, stop.Token);
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            var response = await output;
            try { await errors; } catch (IOException) { }

            clock.Stop();
            var (peak, cpu) = job.Accounting();
            ct.ThrowIfCancellationRequested();
            var exitCode = process.ExitCode;
            if (deadline.IsCancellationRequested) limit = "time";
            else if (cpu >= CpuTimeLimit) limit = "time";
            else if (exitCode == OutOfMemoryExitCode || (exitCode != 0 && peak >= JobMemoryLimit * 7 / 10)) limit = "memory";
            var stats = new SandboxStats(peak, cpu, clock.Elapsed, exitCode, limit);

            if (limit != null) return AttachmentExtractor.TooComplex(kind, limit) with { Sandbox = stats };
            if (oversized) return Unreadable(kind, "The attachment reader returned too much data", stats);
            if (exitCode != 0 || response == null) return Unreadable(kind, "The attachment reader stopped unexpectedly", stats);
            return AttachmentWire.ReadResult(response) is { } result
                ? result with { Sandbox = stats }
                : Unreadable(kind, "The attachment reader returned an invalid answer", stats);
        }
    }

    internal static ProcessStartInfo StartInfo(string helperPath)
    {
        var info = new ProcessStartInfo(helperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ErrorDialog = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(helperPath)!,
        };
        // Nothing of the kernel's environment is inherited: no execution token, no credentials.
        info.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "windir" })
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        if (DotnetRoot() is { } root) info.Environment["DOTNET_ROOT"] = root;
        info.Environment["DOTNET_EnableDiagnostics"] = "0";
        info.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        info.Environment["DOTNET_gcServer"] = "0";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return info;
    }

    /// <summary>The shared .NET install the kernel itself runs on, so the helper uses the same runtime.</summary>
    private static string? DotnetRoot()
    {
        var runtime = RuntimeEnvironment.GetRuntimeDirectory(); // ...\dotnet\shared\Microsoft.NETCore.App\9.0.x\
        var root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        return Directory.Exists(Path.Combine(root, "host", "fxr")) ? root : null;
    }

    /// <summary>Reads to the end; past <paramref name="max"/> bytes calls <paramref name="exceeded"/> and returns null.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int max, Action exceeded)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > max)
                {
                    exceeded();
                    while (await stream.ReadAsync(chunk) > 0) { } // drain until the helper is gone
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }
        }
        catch (IOException) { return null; }
        return buffer.ToArray();
    }
}

/// <summary>
/// A Windows job object for one helper process: job-wide committed memory, user CPU time,
/// a single active process, below-normal priority, every UI restriction, death on an unhandled
/// exception (no error dialog), and kill-on-close so disposing the handle ends whatever is left.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private readonly SafeJobHandle _handle;

    private JobObject(SafeJobHandle handle) => _handle = handle;

    public static JobObject Create()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception();
        var job = new JobObject(handle);
        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    PerJobUserTimeLimit = ExtractorSandbox.CpuTimeLimit.Ticks, // 100 ns units
                    LimitFlags = JOB_OBJECT_LIMIT_JOB_TIME | JOB_OBJECT_LIMIT_ACTIVE_PROCESS | JOB_OBJECT_LIMIT_PRIORITY_CLASS
                        | JOB_OBJECT_LIMIT_JOB_MEMORY | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION | JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                    ActiveProcessLimit = ExtractorSandbox.ActiveProcessLimit,
                    PriorityClass = BELOW_NORMAL_PRIORITY_CLASS,
                },
                JobMemoryLimit = (UIntPtr)ExtractorSandbox.JobMemoryLimit,
            };
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                throw new Win32Exception();
            var ui = new JOBOBJECT_BASIC_UI_RESTRICTIONS { UIRestrictionsClass = JOB_OBJECT_UILIMIT_ALL };
            if (!SetInformationJobObject(handle, JobObjectBasicUIRestrictions, ref ui, Marshal.SizeOf<JOBOBJECT_BASIC_UI_RESTRICTIONS>()))
                throw new Win32Exception();
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.SafeHandle)) throw new Win32Exception();
    }

    public void Terminate(int exitCode)
    {
        if (!_handle.IsClosed) TerminateJobObject(_handle, unchecked((uint)exitCode));
    }

    /// <summary>Peak committed memory and total user CPU time of the job so far.</summary>
    public (long PeakJobMemory, TimeSpan CpuTime) Accounting()
    {
        long peak = 0;
        var cpu = TimeSpan.Zero;
        if (QueryInformationJobObject(_handle, JobObjectExtendedLimitInformation, out JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits,
                Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(), IntPtr.Zero))
            peak = (long)limits.PeakJobMemoryUsed;
        if (QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting,
                Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero))
            cpu = TimeSpan.FromTicks(accounting.TotalUserTime);
        return (peak, cpu);
    }

    /// <summary>The limits as Windows reports them back (tests).</summary>
    internal (long JobMemoryLimit, uint LimitFlags, uint ActiveProcessLimit, TimeSpan CpuTimeLimit, uint PriorityClass, uint UiRestrictions) Describe()
    {
        if (!QueryInformationJobObject(_handle, JobObjectExtendedLimitInformation, out JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits,
                Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(), IntPtr.Zero)
            || !QueryInformationJobObject(_handle, JobObjectBasicUIRestrictions, out JOBOBJECT_BASIC_UI_RESTRICTIONS ui,
                Marshal.SizeOf<JOBOBJECT_BASIC_UI_RESTRICTIONS>(), IntPtr.Zero))
            throw new Win32Exception();
        var basic = limits.BasicLimitInformation;
        return ((long)limits.JobMemoryLimit, basic.LimitFlags, basic.ActiveProcessLimit, TimeSpan.FromTicks(basic.PerJobUserTimeLimit),
            basic.PriorityClass, ui.UIRestrictionsClass);
    }

    public void Dispose() => _handle.Dispose();

    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectBasicUIRestrictions = 4;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_JOB_TIME = 0x4;
    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x8;
    private const uint JOB_OBJECT_LIMIT_PRIORITY_CLASS = 0x20;
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x200;
    private const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x400;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x4000;
    /// <summary>Handles, clipboard read/write, system parameters, display settings, global atoms, desktop, exit Windows.</summary>
    private const uint JOB_OBJECT_UILIMIT_ALL = 0xFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_UI_RESTRICTIONS
    {
        public uint UIRestrictionsClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref JOBOBJECT_BASIC_UI_RESTRICTIONS info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeJobHandle job, int infoClass, out JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeJobHandle job, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeJobHandle job, int infoClass, out JOBOBJECT_BASIC_UI_RESTRICTIONS info, int length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
