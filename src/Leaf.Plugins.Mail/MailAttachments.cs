using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using MimeKit;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
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
    /// <summary>Extraction stopped at a safe processing limit (memory, time, decompressed size).</summary>
    public const string TooComplex = "too_complex";
}

/// <param name="Content">Plain text, or HTML when <see cref="IsHtml"/> (sanitized afterwards).</param>
/// <param name="Signals">Suspicious traits of the document that were kept in the text, as stable slugs.</param>
/// <param name="Sandbox">What the out-of-process run cost, when there was one (diagnostics and tests; never returned over HTTP).</param>
public sealed record ExtractedAttachment(string Status, string Content, bool IsHtml, bool Truncated, string Kind, string? Detail = null,
    int? Pages = null, PdfStats? Stats = null, IReadOnlyList<string>? Signals = null, SandboxStats? Sandbox = null);

/// <param name="PeakJobMemory">Peak committed memory of the helper's job object.</param>
/// <param name="Limit">The job limit that stopped the helper (<c>memory</c>, <c>time</c>), if any.</param>
public sealed record SandboxStats(long PeakJobMemory, TimeSpan CpuTime, TimeSpan Wall, int ExitCode, string? Limit);

/// <summary>What a PDF extraction cost (diagnostics and tests; never returned over HTTP).</summary>
public sealed record PdfStats(long DecodedBytes, long AllocatedBytes, TimeSpan Elapsed, PdfGlyphCounts Glyphs);

/// <summary>
/// Glyphs a reader may not see. Only <see cref="Removed"/> ones are dropped from the text: invisible
/// (render mode 3/7) text with no image beneath it. The rest stay in the text and are flagged: an OCR
/// layer over a scanned image is how searchable scans work, and white or tiny text is common in
/// real statements (white headers on coloured bars), so deleting it damages ordinary documents.
/// </summary>
public sealed record PdfGlyphCounts(int Removed = 0, int OcrLayer = 0, int White = 0, int Tiny = 0, int OffPage = 0)
{
    public PdfGlyphCounts Add(PdfGlyphCounts other) => new(Removed + other.Removed, OcrLayer + other.OcrLayer,
        White + other.White, Tiny + other.Tiny, OffPage + other.OffPage);
}

internal enum GlyphClass { Visible, Removed, OcrLayer, White, Tiny, OffPage }

/// <summary>
/// Read-only attachment text extraction, entirely in memory; nothing is executed, rendered, or
/// written to disk. PDF text comes from PdfPig (no OCR: an image-only PDF is <c>no_text</c>) under
/// page, time, and decompression limits; message/rfc822 is parsed by MimeKitLite; calendars are
/// unfolded into readable lines. This file is compiled into both the plugin and the extractor
/// helper: the plugin runs PDF, HTML, calendar and message parsing in the helper, inside a job
/// object (see AttachmentSandbox.cs), and only plain text and CSV in process.
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
        catch (Exception ex) when (ex is OutOfMemoryException or InsufficientExecutionStackException)
        {
            return TooComplex(kind, ex is OutOfMemoryException ? "memory" : "nesting");
        }
        catch (Exception)
        {
            return new ExtractedAttachment(AttachmentStatus.Unreadable, "", false, false, kind,
                $"The {kind} attachment could not be read");
        }
    }

    public static ExtractedAttachment TooComplex(string kind, string limit, int? pages = null, PdfStats? stats = null)
        => new(AttachmentStatus.TooComplex, "", false, false, kind,
            $"The attachment exceeds the safe processing limits ({limit})", pages, stats);

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
        var glyphs = new PdfGlyphCounts();
        PdfStats Stats() => new(budget.DecodedBytes, budget.AllocatedBytes, budget.Elapsed, glyphs);
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
                var (pageText, counts) = PageText(page);
                glyphs = glyphs.Add(counts);
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
                : TooComplex(kind, ex.Message, pages, Stats());
        }
        catch (Exception ex) when (ex is OutOfMemoryException or InsufficientExecutionStackException)
        {
            return TooComplex(kind, ex is OutOfMemoryException ? "memory" : "nesting", pages, Stats());
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
            var (glyphNotes, signals) = GlyphReport(glyphs);
            notes.AddRange(glyphNotes);
            var detail = notes.Count > 0 ? string.Join("; ", notes) : null;
            var noText = budget.ImageCodedContentStreams > 0
                ? "The PDF is image-only (its page content is a JPEG image); OCR is not available"
                : "The PDF has no extractable text (it may be scanned images; OCR is not available)";
            return string.IsNullOrWhiteSpace(text)
                ? new ExtractedAttachment(AttachmentStatus.NoText, "", false, false, kind, detail ?? noText, pages, Stats(), signals)
                : new ExtractedAttachment(AttachmentStatus.Ok, text, false, truncated, kind, detail, pages, Stats(), signals);
        }
    }

    public const string SignalInvisibleTextRemoved = "pdf_invisible_text_removed";
    public const string SignalWhiteText = "pdf_white_text";
    public const string SignalTinyText = "pdf_tiny_text";
    public const string SignalOffPageText = "pdf_off_page_text";

    private static (List<string> Notes, IReadOnlyList<string>? Signals) GlyphReport(PdfGlyphCounts glyphs)
    {
        var notes = new List<string>();
        var signals = new List<string>();
        if (glyphs.Removed > 0)
        {
            notes.Add($"{glyphs.Removed} invisible character(s) removed (render mode 3 text with no image beneath)");
            signals.Add(SignalInvisibleTextRemoved);
        }
        var flagged = new List<string>();
        void Flag(int count, string what, string signal)
        {
            if (count == 0) return;
            flagged.Add($"{count} {what}");
            signals.Add(signal);
        }
        Flag(glyphs.White, "white or near-white", SignalWhiteText);
        Flag(glyphs.Tiny, "below 1pt", SignalTinyText);
        Flag(glyphs.OffPage, "outside the page", SignalOffPageText);
        if (flagged.Count > 0)
            notes.Add($"Possibly hidden text kept and flagged: {string.Join(", ", flagged)} character(s)");
        return (notes, signals.Count > 0 ? signals : null);
    }

    /// <summary>
    /// How visible one glyph is. Invisible render modes (3 Neither, 7 NeitherClip) are the text
    /// layer of a searchable scan when an image lies beneath the glyph, and are kept as such;
    /// with no image beneath they are removed. White or near-white fill, a size below 1pt and a
    /// position outside the crop box are flagged only: a white heading on a coloured bar is
    /// ordinary, and the colour of the background is not known here.
    /// </summary>
    internal static GlyphClass ClassifyGlyph(Letter letter, PdfRectangle visibleArea, IReadOnlyList<PdfRectangle> images)
    {
        var box = letter.BoundingBox;
        if (letter.RenderingMode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip)
            return images.Any(image => Covers(image, box)) ? GlyphClass.OcrLayer : GlyphClass.Removed;
        if (string.IsNullOrWhiteSpace(letter.Value)) return GlyphClass.Visible;
        if (box.Right < visibleArea.Left || box.Left > visibleArea.Right || box.Top < visibleArea.Bottom || box.Bottom > visibleArea.Top)
            return GlyphClass.OffPage;
        if (letter.PointSize is > 0 and < 1) return GlyphClass.Tiny;
        var fills = letter.RenderingMode is TextRenderingMode.Fill or TextRenderingMode.FillClip;
        if (fills && letter.FillColor is { } color)
        {
            var (r, g, b) = color.ToRGBValues();
            if (r >= 0.95 && g >= 0.95 && b >= 0.95) return GlyphClass.White;
        }
        return GlyphClass.Visible;
    }

    /// <summary>The image covers the glyph's centre (with a point of slack for OCR alignment).</summary>
    private static bool Covers(PdfRectangle image, PdfRectangle glyph)
    {
        double x = (glyph.Left + glyph.Right) / 2, y = (glyph.Bottom + glyph.Top) / 2;
        return x >= image.Left - 1 && x <= image.Right + 1 && y >= image.Bottom - 1 && y <= image.Top + 1;
    }

    /// <summary>Bounds of the page's images, read only when the page has invisible text.</summary>
    private static IReadOnlyList<PdfRectangle> ImageBounds(Page page)
    {
        if (page.NumberOfImages == 0) return [];
        try { return page.GetImages().Select(image => image.BoundingBox).ToList(); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException or PdfLimitExceededException))
        {
            // Unreadable image descriptions: fall back to "the page has images".
            return [page.CropBox.Bounds];
        }
    }

    /// <summary>
    /// The page text in content-stream order (PdfPig's <see cref="ContentOrderTextExtractor"/>);
    /// when invisible glyphs are removed, the same algorithm runs over the kept glyphs, so the
    /// reading order never changes.
    /// </summary>
    private static (string Text, PdfGlyphCounts Counts) PageText(Page page)
    {
        var area = page.CropBox.Bounds;
        var letters = page.Letters;
        IReadOnlyList<PdfRectangle>? images = null;
        int removed = 0, ocr = 0, white = 0, tiny = 0, offPage = 0;
        var kept = new List<Letter>(letters.Count);
        foreach (var letter in letters)
        {
            if (letter.RenderingMode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip) images ??= ImageBounds(page);
            switch (ClassifyGlyph(letter, area, images ?? []))
            {
                case GlyphClass.Removed: removed++; continue;
                case GlyphClass.OcrLayer: ocr++; break;
                case GlyphClass.White: white++; break;
                case GlyphClass.Tiny: tiny++; break;
                case GlyphClass.OffPage: offPage++; break;
            }
            kept.Add(letter);
        }
        var counts = new PdfGlyphCounts(removed, ocr, white, tiny, offPage);
        if (removed == 0)
        {
            try { return (ContentOrderTextExtractor.GetText(page), counts); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException or PdfLimitExceededException))
            { return (page.Text, counts); }
        }
        return (ContentOrderText(kept), counts);
    }

    /// <summary>
    /// <see cref="ContentOrderTextExtractor.GetText(Page, bool)"/> (PdfPig 0.1.16, default options)
    /// over a subset of the page's letters, still in content-stream order.
    /// </summary>
    internal static string ContentOrderText(IReadOnlyList<Letter> letters)
    {
        var sb = new StringBuilder();
        Letter? previous = null;
        Letter? nonWhitespacePrevious = null;
        var hasJustAddedWhitespace = false;
        foreach (var letter in letters)
        {
            if (string.IsNullOrEmpty(letter.Value)) continue;
            if (letter.Value == " " && !hasJustAddedWhitespace)
            {
                if (previous != null && IsNewline(previous, letter)) continue;
                sb.Append(' ');
                previous = letter;
                hasJustAddedWhitespace = true;
                continue;
            }
            hasJustAddedWhitespace = false;
            if (previous != null && letter.Value != " ")
            {
                if (nonWhitespacePrevious != null && IsNewline(nonWhitespacePrevious, letter))
                {
                    if (previous.Value == " ") sb.Remove(sb.Length - 1, 1);
                    sb.AppendLine();
                    hasJustAddedWhitespace = true;
                }
                else if (previous.Value != " ")
                {
                    var gap = letter.StartBaseLine.X - previous.EndBaseLine.X;
                    if (UglyToad.PdfPig.Util.WhitespaceSizeStatistics.IsProbablyWhitespace(gap, previous))
                    {
                        sb.Append(' ');
                        hasJustAddedWhitespace = true;
                    }
                }
            }
            sb.Append(letter.Value);
            previous = letter;
            if (!string.IsNullOrWhiteSpace(letter.Value)) nonWhitespacePrevious = letter;
        }
        return sb.ToString();

        static bool IsNewline(Letter previous, Letter letter)
        {
            var minPtSize = Math.Min((int)Math.Round(letter.PointSize), (int)Math.Round(previous.PointSize));
            return Math.Abs(previous.StartBaseLine.Y - letter.StartBaseLine.Y) > minPtSize * 0.9;
        }
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
