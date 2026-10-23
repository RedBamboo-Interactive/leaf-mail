using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using MimeKit;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Leaf.Plugins.Mail;

/// <summary>
/// One attachment as listed from the message's MIME parts. <see cref="PartId"/> is the stable key:
/// it is part of the message structure. Gmail's <see cref="AttachmentId"/> is an opaque handle that
/// is not documented as stable and is observed to change between messages.get calls, so it is
/// re-read from a fresh fetch before every download. Small parts Gmail returns inline have none.
/// </summary>
public sealed record MailAttachmentInfo(string PartId, string? AttachmentId, string? Filename, string MimeType, long Size, bool Inline,
    string? Charset = null, string? InlineData = null)
{
    public JsonObject ToJson() => new()
    {
        ["partId"] = PartId,
        ["attachmentId"] = AttachmentId,
        ["filename"] = Filename,
        ["mimeType"] = MimeType,
        ["size"] = Size,
        ["inline"] = Inline,
    };
}

public static class AttachmentStatus
{
    public const string Ok = "ok";
    public const string Unsupported = "unsupported";
    public const string Unreadable = "unreadable";
    public const string NoText = "no_text";
    public const string TooLarge = "too_large";
}

/// <param name="Content">Plain text, or HTML when <see cref="IsHtml"/> (sanitized afterwards).</param>
public sealed record ExtractedAttachment(string Status, string Content, bool IsHtml, bool Truncated, string Kind, string? Detail = null,
    int? Pages = null, PdfStats? Stats = null);

/// <summary>What a PDF extraction cost (diagnostics and tests; never returned over HTTP).</summary>
public sealed record PdfStats(long DecodedBytes, long AllocatedBytes, TimeSpan Elapsed, int HiddenGlyphs);

/// <summary>
/// Read-only attachment text extraction, entirely in memory. Nothing is executed, rendered, or
/// handed to an external program, and nothing is written to disk. PDF text comes from PdfPig
/// (no OCR: an image-only PDF is <c>no_text</c>) under page and time limits; message/rfc822 is
/// parsed by MimeKitLite; calendars are unfolded into readable lines.
/// </summary>
public static class AttachmentExtractor
{
    public const long MaxDownloadBytes = 15L * 1024 * 1024;
    public const int MaxPdfPages = 300;
    public static readonly TimeSpan PdfTimeBudget = TimeSpan.FromSeconds(10);
    /// <summary>Total bytes all stream filters of one PDF may produce.</summary>
    public const long MaxPdfDecodedBytes = 32L * 1024 * 1024;
    /// <summary>Total bytes the extraction thread may allocate for one PDF.</summary>
    public const long MaxPdfAllocatedBytes = 512L * 1024 * 1024;
    /// <summary>At most this many extractions run at once, process-wide.</summary>
    public const int MaxConcurrentExtractions = 2;
    private static readonly SemaphoreSlim Gate = new(MaxConcurrentExtractions, MaxConcurrentExtractions);

    /// <summary>
    /// Extraction off the request thread, behind a process-wide gate, honouring the request's
    /// cancellation. The gate is held until the worker has actually finished, so a cancelled
    /// request cannot leave unbounded work running behind it.
    /// </summary>
    public static async Task<ExtractedAttachment> ExtractAsync(byte[] bytes, string mimeType, string? filename, string? charset, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        var work = Task.Run(() =>
        {
            var now = Interlocked.Increment(ref _active);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxObserved)) && Interlocked.CompareExchange(ref _maxObserved, now, seen) != seen) { }
            try { return Extract(bytes, mimeType, filename, charset, ct); }
            finally { Interlocked.Decrement(ref _active); }
        }, CancellationToken.None);
        try { return await work; }
        finally
        {
            // Release only once the worker has finished, even if the caller was cancelled.
            await work.ContinueWith(_ => { }, TaskScheduler.Default);
            Gate.Release();
        }
    }

    private static int _active;
    private static int _maxObserved;

    /// <summary>Highest number of extractions observed running at once (diagnostics and tests).</summary>
    public static int MaxObservedConcurrency => Volatile.Read(ref _maxObserved);

    internal static void ResetObservedConcurrency() => Interlocked.Exchange(ref _maxObserved, 0);
    /// <summary>Extracted text kept before sanitizing; the sanitizer caps the result at 20 000.</summary>
    public const int RawTextCap = MailMessageParser.InputCap;

    private static readonly System.Text.RegularExpressions.Regex PartIdShape = new(@"^[0-9]{1,6}(\.[0-9]{1,6}){0,8}$");

    public static bool IsPartId(string? value) => value != null && PartIdShape.IsMatch(value);

    /// <summary>
    /// Resolves the route's reference against a fresh listing: a partId (the stable key), the
    /// legacy <c>inline.&lt;partId&gt;</c> form, or a Gmail attachmentId that still matches this fetch.
    /// </summary>
    public static MailAttachmentInfo? Find(IReadOnlyList<MailAttachmentInfo> items, string reference)
    {
        if (reference.StartsWith("inline.", StringComparison.Ordinal)) reference = reference[7..];
        return IsPartId(reference)
            ? items.FirstOrDefault(i => i.PartId == reference)
            : items.FirstOrDefault(i => i.AttachmentId == reference);
    }

    public static IReadOnlyList<MailAttachmentInfo> List(System.Text.Json.JsonElement message)
    {
        var payload = message.TryGetProperty("payload", out var p) ? p : default;
        var body = MailMessageParser.BodyPart(message);
        var result = new List<MailAttachmentInfo>();
        foreach (var part in MailMessageParser.Parts(payload))
        {
            if (ReferenceEquals(part, body) || part == body) continue;
            var inline = part.Disposition?.StartsWith("inline", StringComparison.OrdinalIgnoreCase) == true
                || (part.ContentId != null && part.MimeType.StartsWith("image/", StringComparison.Ordinal));
            var isAttachment = part.IsAttachment || (inline && part.MimeType.StartsWith("image/", StringComparison.Ordinal));
            if (!isAttachment) continue;
            if (part.PartId == null || (part.AttachmentId == null && part.Data == null)) continue;
            result.Add(new MailAttachmentInfo(part.PartId, part.AttachmentId, part.Filename, part.MimeType, part.Size, inline,
                part.Charset, part.Data));
        }
        return result;
    }

    /// <summary>The effective type: the declared MIME type, refined by extension or magic bytes
    /// only when the declaration is generic.</summary>
    public static string Kind(string mimeType, string? filename, ReadOnlySpan<byte> head)
    {
        var mime = mimeType.ToLowerInvariant();
        if (mime is "application/octet-stream" or "binary/octet-stream" or "application/x-download" or "")
        {
            if (head.StartsWith("%PDF-"u8)) return "application/pdf";
            var ext = Path.GetExtension(filename ?? "").ToLowerInvariant();
            mime = ext switch
            {
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".csv" => "text/csv",
                ".htm" or ".html" => "text/html",
                ".ics" => "text/calendar",
                ".eml" => "message/rfc822",
                _ => mime,
            };
        }
        return mime;
    }

    public static ExtractedAttachment Extract(byte[] bytes, string mimeType, string? filename, string? charset, CancellationToken ct = default)
    {
        var kind = Kind(mimeType, filename, bytes.AsSpan(0, Math.Min(bytes.Length, 8)));
        try
        {
            return kind switch
            {
                "application/pdf" => Pdf(bytes, kind, ct),
                "text/plain" or "text/csv" => Text(Decode(bytes, charset), kind, html: false),
                "text/html" => Text(Decode(bytes, charset), kind, html: true),
                "text/calendar" => Text(Calendar(Decode(bytes, charset)), kind, html: false),
                "message/rfc822" => Rfc822(bytes, kind),
                _ => new ExtractedAttachment(AttachmentStatus.Unsupported, "", false, false, kind,
                    $"No text extraction for {kind}"),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return new ExtractedAttachment(AttachmentStatus.Unreadable, "", false, false, kind,
                $"The {kind} attachment could not be read");
        }
    }

    private static ExtractedAttachment Text(string text, string kind, bool html)
    {
        var truncated = text.Length > RawTextCap;
        if (truncated) text = text[..RawTextCap];
        return string.IsNullOrWhiteSpace(text)
            ? new ExtractedAttachment(AttachmentStatus.NoText, "", html, false, kind, "The attachment contains no text")
            : new ExtractedAttachment(AttachmentStatus.Ok, text, html, truncated, kind);
    }

    internal static string Decode(byte[] bytes, string? charset)
    {
        Encoding encoding = Encoding.UTF8;
        if (charset != null)
        {
            try { encoding = Encoding.GetEncoding(charset); } catch (ArgumentException) { }
        }
        // A BOM wins over the declared charset, as browsers do.
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false), encoding, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[RawTextCap + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        var text = new string(buffer, 0, read);
        Array.Clear(buffer);
        return text;
    }

    /// <summary>
    /// PDF text under hard limits: every stream filter is bounded by <see cref="BoundedFilterProvider"/>
    /// (decompression bombs are refused before they inflate), the extraction thread's allocations
    /// and the wall clock are budgeted, cancellation is honoured, and nothing is rendered.
    /// </summary>
    private static ExtractedAttachment Pdf(byte[] bytes, string kind, CancellationToken ct)
    {
        var budget = new PdfBudget(MaxPdfDecodedBytes, MaxPdfAllocatedBytes, PdfTimeBudget, ct);
        var builder = new StringBuilder();
        var truncated = false;
        int? pages = null;
        var hiddenGlyphs = 0;
        PdfStats Stats() => new(budget.DecodedBytes, budget.AllocatedBytes, budget.Elapsed, hiddenGlyphs);
        try
        {
            using var document = PdfDocument.Open(bytes, new ParsingOptions
            {
                UseLenientParsing = true,
                SkipMissingFonts = true,
                FilterProvider = new BoundedFilterProvider(budget),
            });
            pages = document.NumberOfPages;
            var limit = Math.Min(pages.Value, MaxPdfPages);
            if (pages > MaxPdfPages) truncated = true;
            for (var number = 1; number <= limit; number++)
            {
                budget.Check();
                var page = document.GetPage(number);
                var (pageText, hidden) = PageText(page);
                hiddenGlyphs += hidden;
                if (builder.Length > 0 && pageText.Length > 0) builder.Append("\n\n");
                builder.Append(pageText);
                if (builder.Length > RawTextCap) { truncated = true; break; }
            }
            return Finish(null);
        }
        catch (OperationCanceledException) { throw; }
        catch (PdfDocumentEncryptedException)
        {
            return new ExtractedAttachment(AttachmentStatus.Unreadable, "", false, false, kind,
                "The PDF is encrypted or password-protected", pages, Stats());
        }
        catch (PdfLimitExceededException ex)
        {
            // Text gathered before the limit is still useful; otherwise the PDF is refused.
            return builder.Length > 0
                ? Finish($"Stopped at the {ex.Message} limit")
                : new ExtractedAttachment(AttachmentStatus.Unreadable, "", false, false, kind,
                    $"The PDF exceeds the safe processing limits ({ex.Message})", pages, Stats());
        }
        catch (Exception ex) when (ex is OutOfMemoryException or InsufficientExecutionStackException)
        {
            return new ExtractedAttachment(AttachmentStatus.Unreadable, "", false, false, kind,
                "The PDF exceeds the safe processing limits (memory)", pages, Stats());
        }
        catch (Exception)
        {
            return new ExtractedAttachment(AttachmentStatus.Unreadable, "", false, false, kind,
                "The PDF is malformed or uses features that cannot be read", pages, Stats());
        }

        ExtractedAttachment Finish(string? stopped)
        {
            if (stopped != null) truncated = true;
            var text = builder.ToString();
            if (text.Length > RawTextCap) text = text[..RawTextCap];
            var notes = new List<string>();
            if (stopped != null) notes.Add(stopped);
            if (hiddenGlyphs > 0)
                notes.Add($"{hiddenGlyphs} hidden character(s) removed (invisible, white, tiny or off-page text)");
            var detail = notes.Count > 0 ? string.Join("; ", notes) : null;
            return string.IsNullOrWhiteSpace(text)
                ? new ExtractedAttachment(AttachmentStatus.NoText, "", false, false, kind,
                    detail ?? "The PDF has no extractable text (it may be scanned images; OCR is not available)", pages, Stats())
                : new ExtractedAttachment(AttachmentStatus.Ok, text, false, truncated, kind, detail, pages, Stats());
        }
    }

    /// <summary>
    /// A glyph a reader cannot see: render mode Neither (3) or NeitherClip (7), white fill in a
    /// fill mode, a font smaller than 1pt, or a glyph entirely outside the crop box.
    /// </summary>
    internal static bool IsHiddenGlyph(Letter letter, PdfRectangle visibleArea)
    {
        if (letter.RenderingMode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip) return true;
        if (letter.PointSize is > 0 and < 1) return true;
        var box = letter.BoundingBox;
        if (box.Right < visibleArea.Left || box.Left > visibleArea.Right || box.Top < visibleArea.Bottom || box.Bottom > visibleArea.Top)
            return true;
        var fills = letter.RenderingMode is TextRenderingMode.Fill or TextRenderingMode.FillClip;
        if (fills && letter.FillColor is { } color)
        {
            var (r, g, b) = color.ToRGBValues();
            if (r > 0.98 && g > 0.98 && b > 0.98) return true;
        }
        return false;
    }

    /// <summary>The page in reading order; when glyphs are hidden, rebuilt from visible ones only.</summary>
    private static (string Text, int Hidden) PageText(Page page)
    {
        var area = page.CropBox.Bounds;
        var letters = page.Letters;
        var visible = letters.Where(letter => !IsHiddenGlyph(letter, area)).ToList();
        var hidden = letters.Count - visible.Count;
        if (hidden == 0)
        {
            try { return (ContentOrderTextExtractor.GetText(page), 0); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException or PdfLimitExceededException))
            { return (page.Text, 0); }
        }
        var lines = NearestNeighbourWordExtractor.Instance.GetWords(visible)
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .GroupBy(word => Math.Round(word.BoundingBox.Bottom / 3.0))
            .OrderByDescending(line => line.Key)
            .Select(line => string.Join(' ', line.OrderBy(word => word.BoundingBox.Left).Select(word => word.Text)));
        return (string.Join('\n', lines), hidden);
    }

    private static ExtractedAttachment Rfc822(byte[] bytes, string kind)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var message = MimeMessage.Load(new ParserOptions { MaxMimeDepth = 16 }, stream);
        var head = new StringBuilder();
        void Header(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                head.Append(name).Append(": ").Append(MailMessageParser.Cap(value, 512)).Append('\n');
        }
        Header("From", message.From.ToString());
        Header("To", message.To.ToString());
        Header("Date", message.Date == default ? null : message.Date.ToString("O"));
        Header("Subject", message.Subject);
        if (message.TextBody is { } text)
            return Text(head + "\n" + text, kind, html: false);
        if (message.HtmlBody is { } html)
            // Headers as escaped HTML so the whole item goes through one HTML sanitizer pass.
            return Text($"<pre>{System.Net.WebUtility.HtmlEncode(head.ToString())}</pre>{html}", kind, html: true);
        return Text(head.ToString(), kind, html: false);
    }

    private static readonly string[] CalendarProperties =
        ["SUMMARY", "DTSTART", "DTEND", "DUE", "LOCATION", "ORGANIZER", "ATTENDEE", "DESCRIPTION", "URL", "STATUS"];

    /// <summary>RFC 5545 line unfolding and text unescaping; components become readable blocks.</summary>
    internal static string Calendar(string ics)
    {
        var unfolded = new List<string>();
        foreach (var raw in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && unfolded.Count > 0)
                unfolded[^1] += raw[1..];
            else unfolded.Add(raw);
        }
        var output = new StringBuilder();
        foreach (var line in unfolded)
        {
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                var component = line[6..].Trim().ToUpperInvariant();
                if (component is "VEVENT" or "VTODO" or "VJOURNAL")
                    output.Append(output.Length > 0 ? "\n" : "").Append(component[1..].ToLowerInvariant() switch
                    {
                        "event" => "Event", "todo" => "To-do", _ => "Note",
                    }).Append('\n');
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon];
            var semicolon = name.IndexOf(';');
            var property = (semicolon < 0 ? name : name[..semicolon]).ToUpperInvariant();
            if (!CalendarProperties.Contains(property)) continue;
            var value = line[(colon + 1)..]
                .Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
            if (property is "ORGANIZER" or "ATTENDEE" && value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                value = value[7..];
            output.Append(char.ToUpperInvariant(property[0])).Append(property[1..].ToLowerInvariant())
                .Append(": ").Append(value).Append('\n');
        }
        return output.ToString();
    }
}
