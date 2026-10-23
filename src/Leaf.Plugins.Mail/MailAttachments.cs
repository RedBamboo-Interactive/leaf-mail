using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using MimeKit;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Leaf.Plugins.Mail;

/// <summary>One attachment as listed from the message's MIME parts. <see cref="Id"/> is Gmail's
/// attachmentId, or <c>inline.&lt;partId&gt;</c> for a small part Gmail returned inline.</summary>
public sealed record MailAttachmentInfo(string Id, string? PartId, string? Filename, string MimeType, long Size, bool Inline)
{
    public JsonObject ToJson() => new()
    {
        ["attachmentId"] = Id,
        ["partId"] = PartId,
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
public sealed record ExtractedAttachment(string Status, string Content, bool IsHtml, bool Truncated, string Kind, string? Detail = null, int? Pages = null);

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
    /// <summary>Extracted text kept before sanitizing; the sanitizer caps the result at 20 000.</summary>
    public const int RawTextCap = MailMessageParser.InputCap;

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
            var id = part.AttachmentId ?? (part.Data != null && part.PartId != null ? "inline." + part.PartId : null);
            if (id == null) continue;
            result.Add(new MailAttachmentInfo(id, part.PartId, part.Filename, part.MimeType, part.Size, inline));
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

    public static ExtractedAttachment Extract(byte[] bytes, string mimeType, string? filename, string? charset)
    {
        var kind = Kind(mimeType, filename, bytes.AsSpan(0, Math.Min(bytes.Length, 8)));
        try
        {
            return kind switch
            {
                "application/pdf" => Pdf(bytes, kind),
                "text/plain" or "text/csv" => Text(Decode(bytes, charset), kind, html: false),
                "text/html" => Text(Decode(bytes, charset), kind, html: true),
                "text/calendar" => Text(Calendar(Decode(bytes, charset)), kind, html: false),
                "message/rfc822" => Rfc822(bytes, kind),
                _ => new ExtractedAttachment(AttachmentStatus.Unsupported, "", false, false, kind,
                    $"No text extraction for {kind}"),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

    private static ExtractedAttachment Pdf(byte[] bytes, string kind)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var document = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            var builder = new StringBuilder();
            var truncated = false;
            var pages = document.NumberOfPages;
            var limit = Math.Min(pages, MaxPdfPages);
            if (pages > MaxPdfPages) truncated = true;
            for (var number = 1; number <= limit; number++)
            {
                if (watch.Elapsed > PdfTimeBudget) { truncated = true; break; }
                var page = document.GetPage(number);
                string pageText;
                try { pageText = ContentOrderTextExtractor.GetText(page); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { pageText = page.Text; }
                if (builder.Length > 0 && pageText.Length > 0) builder.Append("\n\n");
                builder.Append(pageText);
                if (builder.Length > RawTextCap) { truncated = true; break; }
            }
            var text = builder.ToString();
            if (text.Length > RawTextCap) text = text[..RawTextCap];
            return string.IsNullOrWhiteSpace(text)
                ? new ExtractedAttachment(AttachmentStatus.NoText, "", false, false, kind,
                    "The PDF has no extractable text (it may be scanned images; OCR is not available)", pages)
                : new ExtractedAttachment(AttachmentStatus.Ok, text, false, truncated, kind,
                    truncated && watch.Elapsed > PdfTimeBudget ? "Stopped at the time limit" : null, pages);
        }
        catch (PdfDocumentEncryptedException)
        {
            return new ExtractedAttachment(AttachmentStatus.Unreadable, "", false, false, kind,
                "The PDF is encrypted or password-protected");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ExtractedAttachment(AttachmentStatus.Unreadable, "", false, false, kind,
                "The PDF is malformed or uses features that cannot be read");
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
