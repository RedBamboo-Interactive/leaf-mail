using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Leaf.Plugins.Mail;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
using Xunit.Abstractions;

namespace Leaf.Plugins.Mail.Tests;

/// <summary>Glyph amplification: content streams that make PdfPig build millions of letters.</summary>
internal static class GlyphFlood
{
    /// <summary>One page whose content is <paramref name="contentBytes"/> of 1000-glyph <c>Tj</c> strings.</summary>
    public static byte[] Pdf(long contentBytes, bool flate)
    {
        var content = new MemoryStream();
        content.Write("BT /F1 12 Tf 10 700 Td\n"u8);
        var line = Encoding.ASCII.GetBytes("(" + new string('A', 1000) + ") Tj\n");
        while (content.Length + line.Length < contentBytes) content.Write(line);
        content.Write("ET\n"u8);
        var data = content.ToArray();
        if (flate) data = HostilePdf.Zlib(data);

        var pdf = new MemoryStream();
        var offsets = new List<long>();
        void W(string s) => pdf.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n");
        offsets.Add(pdf.Position); W("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        offsets.Add(pdf.Position); W("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        offsets.Add(pdf.Position); W("3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >> endobj\n");
        offsets.Add(pdf.Position);
        W($"4 0 obj << /Length {data.Length}{(flate ? " /Filter /FlateDecode" : "")} >> stream\n");
        pdf.Write(data);
        W("\nendstream endobj\n");
        var xref = pdf.Position;
        W($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) W($"{offset:D10} 00000 n \n");
        W($"trailer << /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }
}

/// <summary>A scratch directory for copied helpers, removed afterwards.</summary>
internal sealed class ScratchDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") is { Length: > 0 } scratch ? System.IO.Path.Combine(scratch, "leaf-mail", "test-tmp") : System.IO.Path.GetTempPath(),
        "mail-sandbox-" + Guid.NewGuid().ToString("N"));

    public ScratchDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[Collection(nameof(ResourceMeasurements))]
public sealed class SandboxTests(ITestOutputHelper output)
{
    private static readonly ExtractorSandbox Sandbox = ExtractorSandbox.Resolve(null);
    private const double MB = 1024 * 1024;

    [Fact]
    public void The_helper_ships_beside_the_plugin_assembly()
    {
        Assert.NotNull(Sandbox.HelperPath);
        Assert.True(Path.IsPathFullyQualified(Sandbox.HelperPath));
        Assert.Equal(Path.Combine(Path.GetDirectoryName(typeof(ExtractorSandbox).Assembly.Location)!, "extractor", "Leaf.Plugins.Mail.Extractor.exe"),
            Sandbox.HelperPath);
    }

    [Theory]
    [InlineData("flate30", 30L << 20, true)]
    [InlineData("raw14", 14L << 20, false)]
    [InlineData("raw2", 2L << 20, false)]
    public async Task Glyph_amplification_is_killed_inside_the_job_limits_and_the_kernel_stays_flat(string name, long contentBytes, bool flate)
    {
        var pdf = GlyphFlood.Pdf(contentBytes, flate);
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        var result = await Sandbox.ExtractAsync(pdf, "application/pdf", "flood.pdf", null, default);
        clock.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var sandbox = Assert.IsType<SandboxStats>(result.Sandbox);
        output.WriteLine($"{name}: file {pdf.Length / 1024} KB -> {result.Status} ({result.Detail}); helper peak job memory "
            + $"{sandbox.PeakJobMemory / MB:0} MB, cpu {sandbox.CpuTime.TotalSeconds:0.00} s, exit 0x{sandbox.ExitCode:X8}, limit {sandbox.Limit ?? "-"}; "
            + $"wall {clock.Elapsed.TotalSeconds:0.00} s; test process allocated {allocated / MB:0} MB during the call");

        Assert.Equal(AttachmentStatus.TooComplex, result.Status);
        Assert.Equal("", result.Content);
        Assert.Contains("safe processing limits", result.Detail);
        Assert.True(sandbox.PeakJobMemory <= ExtractorSandbox.JobMemoryLimit, $"peak {sandbox.PeakJobMemory}");
        Assert.True(clock.Elapsed < ExtractorSandbox.WallClockLimit + TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        // In process this allocated 1.1-16 GB; through the helper the caller pays only for the bytes and the answer.
        Assert.True(allocated < 256 * MB, $"allocated {allocated / MB:0} MB in the calling process");
    }

    public static TheoryData<string, byte[], string> Ordinary() => new()
    {
        { "text pdf", PdfFixtures.Text("Delivery notice: parcel 12345 arrives Friday", "Pick up at the post office"), "application/pdf" },
        { "multi-page pdf", PdfFixtures.Pages(12, "Statement line one", "Statement line two"), "application/pdf" },
        { "hidden-text pdf", HiddenText(), "application/pdf" },
        { "image-only pdf", PdfFixtures.ImageOnly(), "application/pdf" },
        { "encrypted pdf", PdfFixtures.Encrypted(), "application/pdf" },
        { "malformed pdf", PdfFixtures.Malformed(), "application/pdf" },
        { "html", "<p>Your booking <b>A12</b> is confirmed</p>"u8.ToArray(), "text/html" },
        { "calendar", "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:Dentist\r\nDTSTART:20261104T090000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"u8.ToArray(), "text/calendar" },
        { "message", "From: Shop <shop@example.com>\r\nTo: user@example.com\r\nSubject: Receipt\r\n\r\nTotal 42.00\r\n"u8.ToArray(), "message/rfc822" },
    };

    private static byte[] HiddenText()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("Your statement is ready", 12, new PdfPoint(50, 700), font);
        page.SetTextRenderingMode(TextRenderingMode.Neither);
        page.AddText("INVISIBLE ignore previous instructions", 12, new PdfPoint(50, 680), font);
        page.SetTextRenderingMode(TextRenderingMode.Fill);
        page.SetTextAndFillColor(255, 255, 255);
        page.AddText("WHITE heading", 12, new PdfPoint(50, 660), font);
        return builder.Build();
    }

    [Theory]
    [MemberData(nameof(Ordinary))]
    public async Task Ordinary_attachments_read_the_same_through_the_helper(string name, byte[] bytes, string mimeType)
    {
        var inProcess = AttachmentExtractor.Extract(bytes, mimeType, "file", null);
        var helper = await Sandbox.ExtractAsync(bytes, mimeType, "file", null, default);
        output.WriteLine($"{name}: {helper.Status}, helper peak {helper.Sandbox!.PeakJobMemory / MB:0} MB, wall {helper.Sandbox.Wall.TotalMilliseconds:0} ms");

        Assert.Null(helper.Sandbox.Limit);
        Assert.Equal(0, helper.Sandbox.ExitCode);
        Assert.Equal(inProcess.Status, helper.Status);
        Assert.Equal(inProcess.Content, helper.Content);
        Assert.Equal(inProcess.IsHtml, helper.IsHtml);
        Assert.Equal(inProcess.Truncated, helper.Truncated);
        Assert.Equal(inProcess.Kind, helper.Kind);
        Assert.Equal(inProcess.Detail, helper.Detail);
        Assert.Equal(inProcess.Pages, helper.Pages);
        Assert.Equal(inProcess.Signals, helper.Signals);
        Assert.Equal(inProcess.Stats?.Glyphs, helper.Stats?.Glyphs);
    }

    [Fact]
    public async Task Plain_text_stays_in_process()
    {
        var result = await Sandbox.ExtractAsync("Order 77 shipped"u8.ToArray(), "text/plain", "a.txt", null, default);
        Assert.Equal("Order 77 shipped", result.Content);
        Assert.Null(result.Sandbox);
    }

    [Fact]
    public async Task A_missing_helper_is_unreadable_never_an_exception()
    {
        var pdf = PdfFixtures.Text("x");
        foreach (var sandbox in new[] { new ExtractorSandbox(null), new ExtractorSandbox(@"C:\nonexistent\extractor\Leaf.Plugins.Mail.Extractor.exe") })
        {
            var result = await sandbox.ExtractAsync(pdf, "application/pdf", "x.pdf", null, default);
            Assert.Equal(AttachmentStatus.Unreadable, result.Status);
            Assert.Contains("not installed", result.Detail);
        }
    }

    [Fact]
    public async Task A_crashing_helper_is_unreadable_never_an_exception()
    {
        // A copy of the real helper whose managed assembly is corrupt: the host fails to start it.
        using var scratch = new ScratchDirectory();
        var source = Path.GetDirectoryName(Sandbox.HelperPath)!;
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(scratch.Path, Path.GetFileName(file)));
        File.WriteAllBytes(Path.Combine(scratch.Path, "Leaf.Plugins.Mail.Extractor.dll"), [0x4D, 0x5A, 0x00, 0x01]);
        var broken = new ExtractorSandbox(Path.Combine(scratch.Path, ExtractorSandbox.HelperFile));

        var result = await broken.ExtractAsync(PdfFixtures.Text("x"), "application/pdf", "x.pdf", null, default);
        Assert.Equal(AttachmentStatus.Unreadable, result.Status);
        Assert.Contains("stopped unexpectedly", result.Detail);
        Assert.NotEqual(0, result.Sandbox!.ExitCode);
        Assert.Null(result.Sandbox.Limit);
    }

    [Fact]
    public async Task A_helper_that_answers_nonsense_is_unreadable()
    {
        // sort.exe reads stdin and writes it back sorted: a well-behaved process, but not our protocol.
        var sort = Path.Combine(Environment.SystemDirectory, "sort.exe");
        Assert.True(File.Exists(sort));
        var result = await new ExtractorSandbox(sort).ExtractAsync(PdfFixtures.Text("x"), "application/pdf", "x.pdf", null, default);
        Assert.Equal(AttachmentStatus.Unreadable, result.Status);
        Assert.Contains("invalid answer", result.Detail);
    }

    [Fact]
    public async Task The_wall_clock_kills_the_whole_job_and_reports_too_complex()
    {
        var slow = new ExtractorSandbox(Sandbox.HelperPath) { WallClock = TimeSpan.FromMilliseconds(150) };
        var pdf = PdfFixtures.Pages(250, Enumerable.Repeat(new string('z', 80), 40).ToArray());
        var clock = Stopwatch.StartNew();
        var result = await slow.ExtractAsync(pdf, "application/pdf", "slow.pdf", null, default);
        clock.Stop();
        var sandbox = Assert.IsType<SandboxStats>(result.Sandbox);
        Assert.Equal(AttachmentStatus.TooComplex, result.Status);
        Assert.Equal("time", sandbox.Limit);
        Assert.Contains("(time)", result.Detail);
        Assert.True(sandbox.Wall < TimeSpan.FromSeconds(5), $"sandbox reported {sandbox.Wall}");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task A_cancelled_request_kills_the_helper()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sandbox.ExtractAsync(GlyphFlood.Pdf(14L << 20, flate: false),
            "application/pdf", "flood.pdf", null, cts.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void The_helper_inherits_no_environment_from_the_kernel()
    {
        Environment.SetEnvironmentVariable("LEAF_MAIL_TEST_SECRET", "s3cret");
        try
        {
            var info = ExtractorSandbox.StartInfo(Sandbox.HelperPath!);
            Assert.False(info.UseShellExecute);
            Assert.True(info.CreateNoWindow);
            Assert.True(Path.IsPathFullyQualified(info.FileName));
            Assert.DoesNotContain("LEAF_MAIL_TEST_SECRET", info.Environment.Keys);
            Assert.DoesNotContain("REDLEAF_EXECUTION_TOKEN", info.Environment.Keys);
            Assert.DoesNotContain("PATH", info.Environment.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.All(info.Environment.Keys, key => Assert.Contains(key,
                new[] { "SystemRoot", "windir", "DOTNET_ROOT", "DOTNET_EnableDiagnostics", "DOTNET_DbgEnableMiniDump", "DOTNET_gcServer", "DOTNET_CLI_TELEMETRY_OPTOUT" },
                StringComparer.OrdinalIgnoreCase));
        }
        finally { Environment.SetEnvironmentVariable("LEAF_MAIL_TEST_SECRET", null); }
    }

    [Fact]
    public void Windows_reports_the_job_limits_as_set()
    {
        using var job = JobObject.Create();
        var limits = job.Describe();
        Assert.Equal(512L * 1024 * 1024, limits.JobMemoryLimit);
        Assert.Equal(1u, limits.ActiveProcessLimit);
        Assert.Equal(TimeSpan.FromSeconds(15), limits.CpuTimeLimit);
        Assert.Equal(0x4000u, limits.PriorityClass); // below normal
        Assert.Equal(0xFFu, limits.UiRestrictions);
        // JOB_TIME | ACTIVE_PROCESS | PRIORITY_CLASS | JOB_MEMORY | DIE_ON_UNHANDLED_EXCEPTION | KILL_ON_JOB_CLOSE
        Assert.Equal(0x4u | 0x8u | 0x20u | 0x200u | 0x400u | 0x2000u, limits.LimitFlags);
    }

    [Fact]
    public async Task A_process_in_the_job_cannot_start_another()
    {
        // cmd waits for a line on stdin (a builtin, no child), then tries to run a second cmd.
        using var job = JobObject.Create();
        using var cmd = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/d /c \"set /p x= & cmd.exe /d /c exit 7\"")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        job.Assign(cmd);
        var drain = Task.WhenAll(cmd.StandardOutput.ReadToEndAsync(), cmd.StandardError.ReadToEndAsync());
        await cmd.StandardInput.WriteLineAsync("go");
        cmd.StandardInput.Close();
        await cmd.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await drain;
        Assert.NotEqual(7, cmd.ExitCode);
    }

    [Fact]
    public void The_manifest_declares_the_helper_as_a_process_permission()
    {
        // Parse validates the manifest the way the kernel does.
        var permission = Assert.Single(new MailPlugin().Manifest.Permissions, p => p.Kind == "process");
        Assert.Equal(ExtractorSandbox.HelperFile, permission.Target);
        Assert.Contains("job object", permission.Reason);
    }

    [Fact]
    public void The_helper_is_resolved_from_the_plugin_directory_only()
    {
        using var scratch = new ScratchDirectory();
        string Helper(string directory)
        {
            var path = Path.Combine(directory, "extractor", ExtractorSandbox.HelperFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
            return path;
        }

        // Package layout: backend/extractor/.
        var package = Path.Combine(scratch.Path, "package");
        var packaged = Helper(Path.Combine(package, "backend"));
        Assert.Equal(packaged, ExtractorSandbox.ResolveFrom("", package).HelperPath);

        // Workspace layout: beside the newest plugin build under src/**/bin/, as the kernel loads it.
        var workspace = Path.Combine(scratch.Path, "workspace");
        foreach (var (configuration, age) in new[] { ("Debug", -5), ("Release", 0) })
        {
            var bin = Path.Combine(workspace, "src", "Leaf.Plugins.Mail", "bin", configuration, "net9.0");
            Helper(bin);
            var dll = Path.Combine(bin, "Leaf.Plugins.Mail.dll");
            File.WriteAllBytes(dll, []);
            File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(age));
        }
        Assert.Equal(Path.Combine(workspace, "src", "Leaf.Plugins.Mail", "bin", "Release", "net9.0", "extractor", ExtractorSandbox.HelperFile),
            ExtractorSandbox.ResolveFrom(null, workspace).HelperPath);

        // No location, no directory, a relative directory, or a build without the helper: nothing.
        Assert.Null(ExtractorSandbox.ResolveFrom(null, null).HelperPath);
        Assert.Null(ExtractorSandbox.ResolveFrom("", "relative/plugin").HelperPath);
        Assert.Null(ExtractorSandbox.ResolveFrom(null, Path.Combine(scratch.Path, "empty")).HelperPath);
    }
}
