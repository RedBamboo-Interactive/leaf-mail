using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Leaf.Plugins.Mail;

public sealed record MailAddress(string? Name, string? Address);

/// <summary>Message headers, labels, and Gmail's snippet. Returned live; never a body.</summary>
public sealed record MailMetadata(
    string Id,
    string? ThreadId,
    MailAddress From,
    IReadOnlyList<MailAddress> To,
    string? Subject,
    DateTimeOffset? Date,
    IReadOnlyList<string> LabelIds,
    string? Snippet)
{
    /// <summary>From, Subject and snippet as Gmail sent them, before cleaning; screened, never returned.</summary>
    public (string? From, string? Subject, string? Snippet) Raw { get; init; }

    /// <summary>
    /// The only shape written to kernel records: no sender, recipient, subject, or snippet.
    /// Kernel stream reads are not bound by Mail's caller policy, so content stays live-only.
    /// </summary>
    public JsonObject ToRecordJson() => new()
    {
        ["id"] = Id,
        ["threadId"] = ThreadId,
        ["labelIds"] = new JsonArray(LabelIds.Select(l => (JsonNode)JsonValue.Create(l)).ToArray()),
        ["internalDate"] = Date?.ToUnixTimeMilliseconds(),
    };

    /// <param name="includeSnippet">False when a body is returned: Gmail's snippet can carry
    /// hidden preheader text the Sentinel never saw.</param>
    public JsonObject ToJson(bool includeSnippet = true) => includeSnippet ? Full() : WithoutSnippet(Full());

    private static JsonObject WithoutSnippet(JsonObject json) { json.Remove("snippet"); return json; }

    private JsonObject Full() => new()
    {
        ["id"] = Id,
        ["threadId"] = ThreadId,
        ["from"] = new JsonObject { ["name"] = From.Name, ["address"] = From.Address },
        ["to"] = new JsonArray(To.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["address"] = a.Address }).ToArray()),
        ["subject"] = Subject,
        ["date"] = Date?.ToString("O"),
        ["labelIds"] = new JsonArray(LabelIds.Select(l => (JsonNode)JsonValue.Create(l)).ToArray()),
        ["snippet"] = Snippet,
    };
}

public sealed record MailLink(int Index, string Href, string? Text);

public sealed record SanitizedBody(string Text, bool Truncated, string Source, IReadOnlyList<MailLink> Links);

/// <summary>
/// Gmail message JSON → metadata and sanitized body. Reusable for phase-2 attachment
/// handling through <see cref="Parts"/>, which flattens the MIME tree.
///
/// Hostile-input budget: decoded bodies are capped at <see cref="InputCap"/> characters
/// before any processing, HTML is converted by a single linear pass (no backtracking
/// regex over the document), every remaining regex is non-backtracking with a match
/// timeout, and a timeout yields "body unavailable" instead of an error.
/// </summary>
public static class MailMessageParser
{
    public const int BodyCap = 20_000;
    public const int LinkCap = 50;
    public const int InputCap = 512 * 1024;
    public const int MaxFilenameLength = 255;

    /// <summary>The untrusted display fields as Gmail sent them, before cleaning, for screening.</summary>
    public static (string? From, string? Subject, string? Snippet) RawFields(JsonElement message)
    {
        var headers = Headers(message.TryGetProperty("payload", out var payload) ? payload : default);
        var snippet = message.TryGetProperty("snippet", out var s) ? WebUtility.HtmlDecode(s.GetString() ?? "") : null;
        return (headers.GetValueOrDefault("from"), headers.GetValueOrDefault("subject"), snippet);
    }

    /// <summary>True if the text holds characters <see cref="StripInvisible"/> would remove.</summary>
    public static bool HasInvisible(string text)
    {
        foreach (var c in text)
            if ((char.IsControl(c) && c is not ('\n' or '\r' or '\t')) || IsInvisible(c) || char.IsSurrogate(c) && c == '\uDB40')
                return true;
        for (var i = 0; i + 1 < text.Length; i++)
            if (char.IsHighSurrogate(text[i]) != char.IsLowSurrogate(text[i + 1]) && (char.IsHighSurrogate(text[i]) || char.IsLowSurrogate(text[i + 1])))
                return true;
        return false;
    }
    private const int HeaderCap = 512;
    private const int SnippetCap = 400;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private const RegexOptions Linear = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    public static MailMetadata Metadata(JsonElement message)
    {
        var id = message.GetProperty("id").GetString() ?? "";
        var headers = Headers(message.TryGetProperty("payload", out var payload) ? payload : default);
        var labels = message.TryGetProperty("labelIds", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToArray()
            : [];
        DateTimeOffset? date = null;
        if (message.TryGetProperty("internalDate", out var internalDate)
            && long.TryParse(internalDate.GetString(), out var ms))
            date = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        else if (headers.TryGetValue("date", out var dateHeader) && DateTimeOffset.TryParse(dateHeader, out var parsed))
            date = parsed;
        var snippet = message.TryGetProperty("snippet", out var s) ? WebUtility.HtmlDecode(s.GetString() ?? "") : null;
        return new MailMetadata(
            id,
            message.TryGetProperty("threadId", out var t) ? t.GetString() : null,
            ParseAddresses(headers.GetValueOrDefault("from")).FirstOrDefault() ?? new MailAddress(null, null),
            ParseAddresses(headers.GetValueOrDefault("to")).Take(20).ToArray(),
            Cap(headers.GetValueOrDefault("subject"), HeaderCap),
            date,
            labels,
            Cap(snippet, SnippetCap))
        {
            Raw = (headers.GetValueOrDefault("from"), headers.GetValueOrDefault("subject"), snippet),
        };
    }

    public static Dictionary<string, string> Headers(JsonElement payload)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var header in headers.EnumerateArray())
        {
            var name = header.TryGetProperty("name", out var n) ? n.GetString() : null;
            var value = header.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (name != null && value != null && !result.ContainsKey(name)) result[name] = value;
        }
        return result;
    }

    public static IReadOnlyList<MailAddress> ParseAddresses(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return [];
        if (header.Length > 8192) header = header[..8192];
        try
        {
            var collection = new MailAddressCollection();
            collection.Add(header.Replace("\r", " ").Replace("\n", " "));
            return collection.Select(a => new MailAddress(
                string.IsNullOrWhiteSpace(a.DisplayName) ? null : Cap(a.DisplayName, 200),
                Cap(a.Address, 320))).ToArray();
        }
        catch (FormatException)
        {
            return [new MailAddress(Cap(header.Trim(), 200), null)];
        }
    }

    /// <summary>
    /// One leaf MIME part. Gmail sets <c>filename</c> only for attachments; a part whose
    /// content was externalized has no <c>data</c> but an <c>attachmentId</c> (fetch with
    /// <c>messages.attachments.get</c>). <see cref="Size"/> is Gmail's byte count.
    /// </summary>
    public sealed record Part(string MimeType, string? Filename, string? Data, string? AttachmentId,
        string? Charset, bool IsAttachment, long Size = 0, string? PartId = null, string? Disposition = null,
        string? ContentId = null);

    public static IEnumerable<Part> Parts(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) yield break;
        if (payload.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0)
        {
            foreach (var child in parts.EnumerateArray())
                foreach (var leaf in Parts(child))
                    yield return leaf;
            yield break;
        }
        var mime = payload.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "";
        var filename = payload.TryGetProperty("filename", out var f) ? f.GetString() : null;
        string? data = null, attachmentId = null;
        long size = 0;
        if (payload.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
        {
            data = body.TryGetProperty("data", out var d) ? d.GetString() : null;
            attachmentId = body.TryGetProperty("attachmentId", out var a) ? a.GetString() : null;
            if (body.TryGetProperty("size", out var z) && z.TryGetInt64(out var n)) size = n;
        }
        var headers = Headers(payload);
        var disposition = headers.GetValueOrDefault("content-disposition") ?? "";
        var contentType = headers.GetValueOrDefault("content-type") ?? "";
        var charset = CharsetOf(contentType);
        var isAttachment = !string.IsNullOrEmpty(filename)
            || disposition.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase);
        var partId = payload.TryGetProperty("partId", out var pid) ? pid.GetString() : null;
        var contentId = headers.GetValueOrDefault("content-id") ?? headers.GetValueOrDefault("x-attachment-id");
        // Filenames are attacker-chosen: cleaned (controls, bidi, invisible removed) and capped.
        filename = string.IsNullOrWhiteSpace(filename) ? null : Cap(filename, MaxFilenameLength);
        yield return new Part(mime.ToLowerInvariant(), string.IsNullOrEmpty(filename) ? null : filename,
            string.IsNullOrEmpty(data) ? null : data, attachmentId, charset, isAttachment, size,
            string.IsNullOrEmpty(partId) ? null : partId, string.IsNullOrWhiteSpace(disposition) ? null : disposition.Trim(),
            string.IsNullOrWhiteSpace(contentId) ? null : contentId);
    }

    private static string? CharsetOf(string contentType)
    {
        var index = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var value = contentType[(index + 8)..].Trim().Trim('"');
        var end = value.IndexOfAny([';', ' ', '"']);
        if (end >= 0) value = value[..end];
        return value.Length is > 0 and <= 40 ? value : null;
    }

    /// <summary>The body part: first inline text/plain, else first inline text/html (inline or externalized).</summary>
    public static Part? BodyPart(JsonElement message)
    {
        var payload = message.TryGetProperty("payload", out var p) ? p : default;
        var parts = Parts(payload).Where(part => !part.IsAttachment && (part.Data != null || part.AttachmentId != null)).ToArray();
        return parts.FirstOrDefault(part => part.MimeType == "text/plain")
            ?? parts.FirstOrDefault(part => part.MimeType == "text/html");
    }

    /// <summary>Plain text preferred; else HTML→text with scripts/styles removed. URLs move to links.</summary>
    public static SanitizedBody Body(JsonElement message) => Body(BodyPart(message));

    public static SanitizedBody Body(Part? part)
    {
        if (part?.Data == null) return new SanitizedBody("", part != null, part == null ? "none" : "unavailable", []);
        var (text, capped) = Decode(part);
        var result = part.MimeType == "text/html" ? SanitizeHtml(text) : SanitizeText(text, part.MimeType);
        return capped ? result with { Truncated = true } : result;
    }

    public static SanitizedBody Unavailable(string source) => new("", true, "unavailable: " + source, []);

    /// <summary>Base64url decode with the input capped first, so a huge part costs at most the cap.</summary>
    internal static (string Text, bool Capped) Decode(Part part)
    {
        var data = part.Data!;
        // 4 base64 chars per 3 bytes; UTF-8 is at most 4 bytes per char, so this bounds the work.
        var maxChars = (InputCap * 4 / 3 + 4) & ~3;
        var capped = data.Length > maxChars;
        if (capped) data = data[..maxChars];
        byte[] bytes;
        try
        {
            data = data.Replace('-', '+').Replace('_', '/');
            data = data.PadRight(data.Length + (4 - data.Length % 4) % 4, '=');
            bytes = Convert.FromBase64String(data);
        }
        catch (FormatException) { return ("", false); }
        Encoding encoding = Encoding.UTF8;
        if (part.Charset != null)
        {
            try { encoding = Encoding.GetEncoding(part.Charset); }
            catch (ArgumentException) { }
        }
        var text = encoding.GetString(bytes);
        if (text.Length > InputCap) { text = text[..InputCap]; capped = true; }
        return (text, capped);
    }

    private static readonly Regex Url = new(
        @"\b(?:https?://|www\.)[^\s<>""'\]\[(){}]+", RegexOptions.IgnoreCase | Linear, RegexTimeout);
    private static readonly Regex OtherScheme = new(
        @"\b(?:javascript|data|vbscript|file):[^\s<>""']*", RegexOptions.IgnoreCase | Linear, RegexTimeout);
    private static readonly Regex HrefAttribute = new(
        @"\bhref\s*=\s*(?:""[^""]*""|'[^']*'|[^\s""'>]+)", RegexOptions.IgnoreCase | Linear, RegexTimeout);
    private static readonly Regex Spaces = new(@"[ \t\f\v]+", Linear, RegexTimeout);
    private static readonly Regex LineEdges = new(@" *\n *", Linear, RegexTimeout);
    private static readonly Regex BlankLines = new(@"\n{3,}", Linear, RegexTimeout);
    private static readonly Regex Marker = new("\u0001LINK([0-9]+)\u0002", Linear, RegexTimeout);
    private const string RemovedMarker = "\u0001LINKX\u0002";

    private static readonly HashSet<string> RawTextTags = new(StringComparer.Ordinal)
        { "script", "style", "head", "title", "noscript", "template", "svg", "iframe", "object", "textarea" };
    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
        { "br", "p", "div", "tr", "li", "h1", "h2", "h3", "h4", "h5", "h6", "table", "blockquote", "hr", "ul", "ol" };

    public static SanitizedBody SanitizeHtml(string html)
    {
        try
        {
            var links = new LinkCollector();
            var text = WebUtility.HtmlDecode(HtmlToText(StripControls(Cut(html)), links));
            return Finish(text, "text/html", links, html.Length > InputCap);
        }
        catch (RegexMatchTimeoutException) { return Unavailable("text/html"); }
    }

    public static SanitizedBody SanitizeText(string text, string source = "text/plain")
    {
        try { return Finish(StripControls(Cut(text)), source, new LinkCollector(), text.Length > InputCap); }
        catch (RegexMatchTimeoutException) { return Unavailable(source); }
    }

    private static string Cut(string value) => value.Length > InputCap ? value[..InputCap] : value;

    /// <summary>
    /// Single linear pass over the markup. Comments and raw-text elements (script, style,
    /// ...) are skipped, anchors become "text [link N]", block tags become newlines, all
    /// other tags vanish. Every search for a terminator is memoized per needle and only
    /// moves forward, so unterminated decoy tags cannot make the pass quadratic.
    /// </summary>
    internal static string HtmlToText(string html, LinkCollector links)
    {
        var output = new StringBuilder(Math.Min(html.Length, BodyCap * 4));
        var finder = new ForwardFinder(html);
        var anchorStart = -1;
        string? anchorHref = null;
        var i = 0;

        void FlushAnchor()
        {
            if (anchorHref == null) return;
            var raw = output.ToString(anchorStart, output.Length - anchorStart);
            var marker = links.Add(anchorHref, Clean(WebUtility.HtmlDecode(raw)));
            output.Append(' ').Append(marker).Append(' ');
            anchorHref = null;
        }

        while (i < html.Length)
        {
            var next = html.IndexOf('<', i);
            if (next < 0) { output.Append(html, i, html.Length - i); break; }
            output.Append(html, i, next - i);
            i = next;
            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var end = finder.Next("-->", i + 4);
                i = end < 0 ? html.Length : end + 3;
                output.Append(' ');
                continue;
            }
            var close = finder.Next(">", i + 1);
            if (close < 0) break; // unterminated tag: the rest is markup, not text
            var (name, closing) = TagName(html, i + 1, close);
            var tagStart = i + 1;
            i = close + 1;
            if (name.Length == 0) { output.Append(' '); continue; }
            if (!closing && RawTextTags.Contains(name))
            {
                var end = finder.Next("</" + name, i);
                if (end < 0) { i = html.Length; break; }
                var endClose = finder.Next(">", end);
                i = endClose < 0 ? html.Length : endClose + 1;
                output.Append(' ');
                continue;
            }
            if (name == "a")
            {
                FlushAnchor();
                if (!closing && Href(html, tagStart, close) is { } href)
                {
                    anchorHref = href;
                    anchorStart = output.Length;
                }
                output.Append(' ');
                continue;
            }
            if (name == "li" && !closing) output.Append("\n- ");
            else output.Append(BlockTags.Contains(name) ? '\n' : ' ');
        }
        FlushAnchor();
        return output.ToString();
    }

    private static (string Name, bool Closing) TagName(string html, int start, int end)
    {
        var i = start;
        var closing = i < end && html[i] == '/';
        if (closing) i++;
        var nameStart = i;
        while (i < end && i - nameStart < 16 && char.IsAsciiLetterOrDigit(html[i])) i++;
        return (html[nameStart..i].ToLowerInvariant(), closing);
    }

    private static string? Href(string html, int start, int end)
    {
        var match = HrefAttribute.Match(html, start, end - start);
        if (!match.Success) return null;
        var value = match.Value[(match.Value.IndexOf('=') + 1)..].Trim().Trim('"', '\'');
        return WebUtility.HtmlDecode(value).Trim();
    }

    /// <summary>First occurrence of a needle at or after a position; positions only move forward.</summary>
    private sealed class ForwardFinder(string text)
    {
        private readonly Dictionary<string, (int From, int Found)> _cache = new(StringComparer.Ordinal);

        public int Next(string needle, int from)
        {
            if (_cache.TryGetValue(needle, out var hit) && hit.From <= from && (hit.Found < 0 || hit.Found >= from))
                return hit.Found;
            var found = text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            _cache[needle] = (from, found);
            return found;
        }
    }

    private static SanitizedBody Finish(string text, string source, LinkCollector links, bool inputCapped)
    {
        // Bare URLs in text (and any that survived HTML conversion) are lifted out too.
        text = Url.Replace(text, match =>
        {
            var raw = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            var trailing = match.Value[raw.Length..];
            return " " + links.Add(raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + raw : raw, null) + trailing;
        });
        text = OtherScheme.Replace(text, "[link removed]");
        text = StripInvisible(text).Replace(' ', ' ').Replace(' ', ' ').Replace(' ', ' ');
        text = Spaces.Replace(text, " ");
        text = LineEdges.Replace(text, "\n");
        text = BlankLines.Replace(text, "\n\n").Trim();
        text = Marker.Replace(text, m => $"[link {m.Groups[1].Value}]");
        text = text.Replace(RemovedMarker, "[link removed]");
        var truncated = inputCapped || text.Length > BodyCap;
        if (text.Length > BodyCap) text = text[..BodyCap];
        return new SanitizedBody(text, truncated, source, links.Items);
    }

    /// <summary>
    /// Input hygiene before any marker is inserted: removes every C0/C1 control character
    /// except tab and newline (CR is normalized), so the U+0001/U+0002 link markers used
    /// internally cannot be spoofed by message content.
    /// </summary>
    internal static string StripControls(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Replace("\r\n", "\n"))
        {
            if (c is '\n' or '\t') builder.Append(c);
            else if (c == '\r') builder.Append('\n');
            else if (!char.IsControl(c)) builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>
    /// Removes characters a reader cannot see but a model reads: controls (except newline, tab
    /// and Mail's own link markers), zero-width and joiner characters, bidi overrides and
    /// isolates, invisible math operators, soft hyphens, the BOM, and Unicode tag characters
    /// (U+E0000-U+E007F, encoded as surrogate pairs U+DB40 U+DC00-U+DC7F, used for
    /// "ASCII smuggling"). Unpaired surrogates are dropped too.
    /// </summary>
    internal static string StripInvisible(string text)
    {
        var source = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var builder = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= source.Length || !char.IsLowSurrogate(source[i + 1])) continue; // unpaired
                var low = source[i + 1];
                i++;
                if (c == '\uDB40' && low is >= '\uDC00' and <= '\uDC7F') continue; // tag character
                builder.Append(c).Append(low);
                continue;
            }
            if (char.IsLowSurrogate(c)) continue; // unpaired
            if (c is '\n' or '\t') { builder.Append(c); continue; }
            if (char.IsControl(c) && c is not ('\u0001' or '\u0002')) continue;
            if (IsInvisible(c)) continue;
            builder.Append(c);
        }
        return builder.ToString();
    }

    internal static bool IsInvisible(char c)
        => c is >= '\u200B' and <= '\u200F'   // zero-width space/joiners, LRM/RLM
            or >= '\u202A' and <= '\u202E'      // bidi embeddings and overrides
            or >= '\u2060' and <= '\u2064'      // word joiner, invisible operators
            or >= '\u2066' and <= '\u2069'      // bidi isolates
            or '\u00AD' or '\u034F' or '\u061C' or '\u115F' or '\u1160' or '\u17B4' or '\u17B5'
            or '\u180E' or '\u3164' or '\uFEFF' or '\uFFA0';

    private static string Clean(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, 4096));
        var space = false;
        foreach (var c in StripInvisible(StripControls(value)))
        {
            if (char.IsWhiteSpace(c)) { space = builder.Length > 0; continue; }
            if (space) { builder.Append(' '); space = false; }
            builder.Append(c);
        }
        return builder.ToString();
    }

    internal static string? Cap(string? value, int max)
    {
        if (value == null) return null;
        if (value.Length > max * 8) value = value[..(max * 8)];
        value = Clean(value);
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    internal sealed class LinkCollector
    {
        private readonly List<MailLink> _items = [];
        private readonly Dictionary<string, int> _byHref = new(StringComparer.Ordinal);
        public IReadOnlyList<MailLink> Items => _items;

        /// <summary>Returns a body marker. Only absolute http(s) links are kept; others vanish.</summary>
        public string Add(string href, string? text)
        {
            if (href.Length > 2048
                || !Uri.TryCreate(href, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
                return RemovedMarker;
            var normalized = uri.AbsoluteUri;
            var anchor = string.IsNullOrWhiteSpace(text) ? null : Cap(text, 200);
            if (_byHref.TryGetValue(normalized, out var existing))
            {
                var current = _items[existing - 1];
                if (current.Text == null && anchor != null && anchor != normalized)
                    _items[existing - 1] = current with { Text = anchor };
                return "\u0001LINK" + existing + "\u0002";
            }
            if (_items.Count >= LinkCap) return RemovedMarker;
            var index = _items.Count + 1;
            _items.Add(new MailLink(index, normalized, anchor == normalized ? null : anchor));
            _byHref[normalized] = index;
            return "\u0001LINK" + index + "\u0002";
        }
    }
}
