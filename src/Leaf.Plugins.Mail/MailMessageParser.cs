using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Leaf.Plugins.Mail;

public sealed record MailAddress(string? Name, string? Address);

/// <summary>The only message shape Mail persists: headers, labels, and Gmail's snippet. Never a body.</summary>
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
    public JsonObject ToJson() => new()
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
/// </summary>
public static class MailMessageParser
{
    public const int BodyCap = 20_000;
    public const int LinkCap = 50;
    private const int HeaderCap = 512;
    private const int SnippetCap = 400;

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
            Cap(snippet, SnippetCap));
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

    /// <summary>One leaf MIME part. Attachment parts carry a filename and/or attachmentId.</summary>
    public sealed record Part(string MimeType, string? Filename, string? Data, string? AttachmentId, string? Charset, bool IsAttachment);

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
        if (payload.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
        {
            data = body.TryGetProperty("data", out var d) ? d.GetString() : null;
            attachmentId = body.TryGetProperty("attachmentId", out var a) ? a.GetString() : null;
        }
        var headers = Headers(payload);
        var disposition = headers.GetValueOrDefault("content-disposition") ?? "";
        var charset = Regex.Match(headers.GetValueOrDefault("content-type") ?? "", "charset=\"?([A-Za-z0-9_\\-:.]+)", RegexOptions.IgnoreCase) is { Success: true } cm
            ? cm.Groups[1].Value : null;
        var isAttachment = !string.IsNullOrEmpty(filename) || attachmentId != null
            || disposition.StartsWith("attachment", StringComparison.OrdinalIgnoreCase);
        yield return new Part(mime.ToLowerInvariant(), string.IsNullOrEmpty(filename) ? null : filename, data, attachmentId, charset, isAttachment);
    }

    /// <summary>Plain text preferred; else HTML→text with scripts/styles removed. URLs move to links.</summary>
    public static SanitizedBody Body(JsonElement message)
    {
        var payload = message.TryGetProperty("payload", out var p) ? p : default;
        var parts = Parts(payload).Where(part => !part.IsAttachment && part.Data != null).ToArray();
        var plain = parts.FirstOrDefault(part => part.MimeType == "text/plain");
        if (plain != null) return SanitizeText(Decode(plain), "text/plain");
        var html = parts.FirstOrDefault(part => part.MimeType == "text/html");
        if (html != null) return SanitizeHtml(Decode(html));
        return new SanitizedBody("", false, "none", []);
    }

    internal static string Decode(Part part)
    {
        byte[] bytes;
        try
        {
            var data = part.Data!.Replace('-', '+').Replace('_', '/');
            data = data.PadRight(data.Length + (4 - data.Length % 4) % 4, '=');
            bytes = Convert.FromBase64String(data);
        }
        catch (FormatException) { return ""; }
        Encoding encoding = Encoding.UTF8;
        if (part.Charset != null)
        {
            try { encoding = Encoding.GetEncoding(part.Charset); }
            catch (ArgumentException) { }
        }
        return encoding.GetString(bytes);
    }

    private static readonly Regex ScriptLike = new(
        @"<(script|style|head|title|noscript|template|svg|iframe|object)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Comments = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Anchor = new(
        @"<a\b[^>]*?\bhref\s*=\s*(?:""(?<h>[^""]*)""|'(?<h>[^']*)'|(?<h>[^\s>]+))[^>]*>(?<t>.*?)</a\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex BlockBreak = new(
        @"<(br|/p|/div|/tr|/li|/h[1-6]|/table|/blockquote|hr)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"<li\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"<[^>]*>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Url = new(
        @"\b(?:https?://|www\.)[^\s<>""'\]\[(){}]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OtherScheme = new(
        @"\b(?:javascript|data|vbscript|file):[^\s<>""']*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const string LinkMarkerPrefix = "\u0001LINK";

    public static SanitizedBody SanitizeHtml(string html)
    {
        var links = new LinkCollector();
        var text = Comments.Replace(html, " ");
        text = ScriptLike.Replace(text, " ");
        text = Anchor.Replace(text, match =>
        {
            var href = WebUtility.HtmlDecode(match.Groups["h"].Value).Trim();
            var anchorText = Clean(WebUtility.HtmlDecode(Tag.Replace(match.Groups["t"].Value, " ")));
            var marker = links.Add(href, anchorText);
            return $" {anchorText} {marker} ";
        });
        text = BlockBreak.Replace(text, "\n");
        text = ListItem.Replace(text, "\n- ");
        text = Tag.Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        return Finish(text, "text/html", links);
    }

    public static SanitizedBody SanitizeText(string text, string source = "text/plain")
        => Finish(text, source, new LinkCollector());

    private static SanitizedBody Finish(string text, string source, LinkCollector links)
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
        text = Regex.Replace(text, @"[ \t\f\v]+", " ");
        text = Regex.Replace(text, @" *\n *", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        text = Regex.Replace(text, LinkMarkerPrefix + @"(\d+)\u0002", m => $"[link {m.Groups[1].Value}]");
        text = text.Replace(LinkMarkerPrefix + "X\u0002", "[link removed]");
        var truncated = text.Length > BodyCap;
        if (truncated) text = text[..BodyCap];
        return new SanitizedBody(text, truncated, source, links.Items);
    }

    /// <summary>Removes control, zero-width, and bidi-override characters (except newline/tab).</summary>
    internal static string StripInvisible(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (c is '\n' or '\t') { builder.Append(c); continue; }
            if (char.IsControl(c) && c is not ('\u0001' or '\u0002')) continue;
            if (c is >= '​' and <= '‏' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '﻿' or '­')
                continue;
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static string Clean(string value)
        => Regex.Replace(StripInvisible(value).Replace('\n', ' '), @"\s+", " ").Trim();

    internal static string? Cap(string? value, int max)
    {
        if (value == null) return null;
        value = Clean(value);
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    private sealed class LinkCollector
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
                return LinkMarkerPrefix + "X\u0002";
            var normalized = uri.AbsoluteUri;
            var anchor = string.IsNullOrWhiteSpace(text) ? null : Cap(text, 200);
            if (_byHref.TryGetValue(normalized, out var existing))
            {
                var current = _items[existing - 1];
                if (current.Text == null && anchor != null && anchor != normalized)
                    _items[existing - 1] = current with { Text = anchor };
                return LinkMarkerPrefix + existing + "\u0002";
            }
            if (_items.Count >= LinkCap) return LinkMarkerPrefix + "X\u0002";
            var index = _items.Count + 1;
            _items.Add(new MailLink(index, normalized, anchor == normalized ? null : anchor));
            _byHref[normalized] = index;
            return LinkMarkerPrefix + index + "\u0002";
        }
    }
}
