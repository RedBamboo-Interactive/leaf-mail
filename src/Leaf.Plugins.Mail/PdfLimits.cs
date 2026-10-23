using System.Diagnostics;
using System.IO.Compression;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace Leaf.Plugins.Mail;

/// <summary>A PDF exceeded a safe processing limit; <see cref="Exception.Message"/> names which.</summary>
public sealed class PdfLimitExceededException(string limit) : Exception(limit);

/// <summary>
/// Per-extraction resource budget, owned by the single worker thread that runs the extraction:
/// a cap on bytes produced by stream filters, a cap on bytes allocated by that thread, a wall
/// clock deadline, and the request's cancellation. Checked inside every filter decode and
/// between pages.
/// </summary>
public sealed class PdfBudget(long maxDecodedBytes, long maxAllocatedBytes, TimeSpan time, CancellationToken ct)
{
    private readonly long _startAllocated = GC.GetAllocatedBytesForCurrentThread();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _decoded;

    public long MaxDecodedBytes => maxDecodedBytes;
    public long DecodedBytes => _decoded;
    public long Remaining => maxDecodedBytes - _decoded;
    public long AllocatedBytes => GC.GetAllocatedBytesForCurrentThread() - _startAllocated;
    public TimeSpan Elapsed => _clock.Elapsed;

    public void Check()
    {
        ct.ThrowIfCancellationRequested();
        if (_clock.Elapsed > time) throw new PdfLimitExceededException("time");
        if (AllocatedBytes > maxAllocatedBytes) throw new PdfLimitExceededException("memory");
    }

    public void Reserve(long bytes)
    {
        if (bytes > Remaining) throw new PdfLimitExceededException("decompressed size");
    }

    public void Add(long bytes) => _decoded += bytes;
}

/// <summary>
/// Wraps PdfPig's filters so no stream is decompressed beyond the budget. Before PdfPig's own
/// filter runs, the output size of Flate, LZW and RunLength data is measured with a counting
/// decoder that uses constant memory and stops one byte past the remaining budget; only a stream
/// that fits is handed to PdfPig (which keeps its predictors and leniency). Nested filter chains
/// are checked stage by stage. Image codecs that expand data (CCITT, JBIG2) are refused: text
/// extraction never needs them.
/// </summary>
internal sealed class BoundedFilterProvider(PdfBudget budget) : IFilterProvider
{
    private readonly IFilterProvider _inner = DefaultFilterProvider.Instance;

    public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary) => Wrap(_inner.GetFilters(dictionary));
    public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names) => Wrap(_inner.GetNamedFilters(names));
    public IReadOnlyList<IFilter> GetAllFilters() => Wrap(_inner.GetAllFilters());

    private IReadOnlyList<IFilter> Wrap(IReadOnlyList<IFilter> filters)
        => filters.Select(f => f is BoundedFilter ? f : (IFilter)new BoundedFilter(f, budget)).ToArray();

    private sealed class BoundedFilter(IFilter inner, PdfBudget budget) : IFilter
    {
        public bool IsSupported => inner.IsSupported;

        public Memory<byte> Decode(Memory<byte> input, DictionaryToken streamDictionary, IFilterProvider filterProvider, int filterIndex)
        {
            budget.Check();
            var limit = budget.Remaining;
            var bound = inner switch
            {
                FlateFilter => DecodeBounds.Flate(input.Span, limit),
                LzwFilter => DecodeBounds.Lzw(input.Span, DecodeBounds.EarlyChange(streamDictionary, filterIndex), limit),
                RunLengthFilter => DecodeBounds.RunLength(input.Span, limit),
                Ascii85Filter or AsciiHexDecodeFilter => input.Length, // output is smaller than input
                DctDecodeFilter or JpxDecodeFilter => input.Length,     // passed through undecoded
                CcittFaxDecodeFilter or Jbig2DecodeFilter =>
                    throw new PdfLimitExceededException("image decoding"),
                _ => throw new PdfLimitExceededException("unknown filter"),
            };
            budget.Reserve(bound);
            var output = inner.Decode(input, streamDictionary, filterProvider, filterIndex);
            budget.Add(output.Length);
            budget.Check();
            return output;
        }
    }
}

/// <summary>Constant-memory output-size measurement for PDF stream filters.</summary>
internal static class DecodeBounds
{
    /// <summary>Bytes Flate data inflates to; past the limit, the first count above it (one read). Accepts zlib-wrapped and raw
    /// deflate, as PdfPig does; corrupt data counts what decodes before the error.</summary>
    public static long Flate(ReadOnlySpan<byte> input, long limit)
    {
        var data = input.ToArray(); // compressed input only; already bounded by the attachment cap
        var zlib = Count(() => new ZLibStream(new MemoryStream(data, writable: false), CompressionMode.Decompress), limit);
        if (zlib > limit) return zlib;
        var skipped = data.Length > 2
            ? Count(() => new DeflateStream(new MemoryStream(data, 2, data.Length - 2, writable: false), CompressionMode.Decompress), limit)
            : 0;
        if (skipped > limit) return skipped;
        var raw = Count(() => new DeflateStream(new MemoryStream(data, writable: false), CompressionMode.Decompress), limit);
        return Math.Max(zlib, Math.Max(skipped, raw));
    }

    private static long Count(Func<Stream> open, long limit)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        try
        {
            using var stream = open();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > limit) return total;
            }
        }
        catch (InvalidDataException) { }
        return total;
    }

    /// <summary>PDF LZW (MSB-first, 9-12 bit codes, 256 clear, 257 end) output length, counting
    /// entry lengths only; past the limit, the first count above it.</summary>
    public static long Lzw(ReadOnlySpan<byte> input, int earlyChange, long limit)
    {
        var length = new int[4096];
        for (var i = 0; i < 256; i++) length[i] = 1;
        int next = 258, width = 9, previous = -1;
        long total = 0, bitBuffer = 0;
        var bits = 0;
        foreach (var b in input)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bits += 8;
            while (bits >= width)
            {
                var code = (int)((bitBuffer >> (bits - width)) & ((1 << width) - 1));
                bits -= width;
                if (code == 256) { next = 258; width = 9; previous = -1; continue; }
                if (code == 257) return total;
                int entry;
                if (code < next) entry = length[code];
                else if (code == next && previous >= 0) entry = length[previous] + 1;
                else return total; // corrupt: PdfPig stops too
                total += entry;
                if (total > limit) return total;
                if (previous >= 0 && next < 4096) length[next++] = length[previous] + 1;
                previous = code;
                if (next + earlyChange >= (1 << width) && width < 12) width++;
            }
        }
        return total;
    }

    /// <summary>RunLengthDecode output length (at most 128:1); past the limit, the first count above it.</summary>
    public static long RunLength(ReadOnlySpan<byte> input, long limit)
    {
        long total = 0;
        var i = 0;
        while (i < input.Length)
        {
            var n = input[i++];
            if (n == 128) break;
            if (n < 128) { total += n + 1; i += n + 1; }
            else { total += 257 - n; i++; }
            if (total > limit) return total;
        }
        return total;
    }

    public static int EarlyChange(DictionaryToken stream, int filterIndex)
    {
        if (!stream.Data.TryGetValue("DecodeParms", out var parms) && !stream.Data.TryGetValue("DP", out parms)) return 1;
        if (parms is ArrayToken array) parms = filterIndex < array.Data.Count ? array.Data[filterIndex] : null;
        return parms is DictionaryToken dict && dict.Data.TryGetValue("EarlyChange", out var value) && value is NumericToken n
            ? (n.Int == 0 ? 0 : 1) : 1;
    }
}
