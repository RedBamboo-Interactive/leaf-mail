using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Leaf.Plugins.Mail;

/// <summary>Gmail returned a non-retryable or exhausted failure. The message never echoes the body.</summary>
public sealed class GmailApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public bool HistoryExpired => Status == HttpStatusCode.NotFound;
    public bool Transient => (int)Status == 429 || (int)Status >= 500;
}

/// <summary>The attachment exceeds the download cap; nothing beyond the cap was kept.</summary>
public sealed class AttachmentTooLargeException() : Exception("The attachment exceeds the download limit");

public interface IMailAccessTokens
{
    Task<string> GetAccessTokenAsync(CancellationToken ct);
    Task InvalidateAsync(CancellationToken ct = default);
}

/// <summary>Retry/backoff policy, injectable so tests do not sleep.</summary>
public sealed class MailBackoff(Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const int MaxAttempts = 4;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public static TimeSpan For(int attempt, TimeSpan? retryAfter)
    {
        var exponential = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
        var wait = retryAfter is { } hinted && hinted > exponential ? hinted : exponential + jitter;
        return wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait;
    }

    public Task WaitAsync(int attempt, TimeSpan? retryAfter, CancellationToken ct)
        => _delay(For(attempt, retryAfter), ct);
}

public sealed record GmailHistoryPage(IReadOnlyList<string> AddedMessageIds, string? HistoryId);

public sealed record GmailProfile(string EmailAddress, string HistoryId);

/// <summary>
/// Read-only Gmail API client. Every call is a GET (or a batch of GETs); there is no
/// code path that sends, modifies, or deletes mail, and the token only carries
/// gmail.readonly.
/// </summary>
public sealed class GmailClient(HttpClient http, GoogleEndpoints endpoints, IMailAccessTokens tokens, MailBackoff backoff)
{
    public const int BatchSize = 50;
    internal const string MetadataHeaders = "metadataHeaders=From&metadataHeaders=To&metadataHeaders=Subject&metadataHeaders=Date";
    private static readonly Regex IdShape = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    public static bool IsValidId(string? id) => id != null && IdShape.IsMatch(id);

    public async Task<GmailProfile> GetProfileAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/profile", ct);
        var root = doc.RootElement;
        return new GmailProfile(
            root.GetProperty("emailAddress").GetString() ?? throw new GmailApiException(HttpStatusCode.BadGateway, "Gmail profile has no address"),
            root.GetProperty("historyId").GetString() ?? throw new GmailApiException(HttpStatusCode.BadGateway, "Gmail profile has no history id"));
    }

    public async Task<long?> GetInboxUnreadAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/labels/INBOX", ct);
        return doc.RootElement.TryGetProperty("messagesUnread", out var unread) && unread.TryGetInt64(out var n) ? n : null;
    }

    /// <summary>users.history.list (messageAdded) from <paramref name="startHistoryId"/>, all pages.
    /// A 404 means the start id expired; callers resync.</summary>
    public async Task<GmailHistoryPage> ListHistoryAsync(string startHistoryId, int maxIds, CancellationToken ct)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        string? historyId = null;
        do
        {
            var path = $"/history?startHistoryId={Uri.EscapeDataString(startHistoryId)}&historyTypes=messageAdded&maxResults=500"
                + (pageToken == null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var doc = await GetJsonAsync(path, ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("historyId", out var h)) historyId = h.GetString();
            if (root.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in history.EnumerateArray())
                {
                    if (!entry.TryGetProperty("messagesAdded", out var added) || added.ValueKind != JsonValueKind.Array) continue;
                    foreach (var item in added.EnumerateArray())
                    {
                        if (item.TryGetProperty("message", out var message)
                            && message.TryGetProperty("id", out var id)
                            && id.GetString() is { } value && IsValidId(value) && seen.Add(value))
                            ids.Add(value);
                    }
                }
            }
            pageToken = root.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (pageToken != null && ids.Count < maxIds);
        return new GmailHistoryPage(ids.Take(maxIds).ToArray(), historyId);
    }

    /// <summary>users.messages.list with Gmail search syntax; returns message ids only.</summary>
    public async Task<IReadOnlyList<string>> ListMessageIdsAsync(string query, int max, CancellationToken ct)
    {
        var ids = new List<string>();
        string? pageToken = null;
        do
        {
            var pageSize = Math.Min(500, max - ids.Count);
            var path = $"/messages?q={Uri.EscapeDataString(query)}&maxResults={pageSize}"
                + (pageToken == null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var doc = await GetJsonAsync(path, ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                foreach (var message in messages.EnumerateArray())
                    if (message.TryGetProperty("id", out var id) && id.GetString() is { } value && IsValidId(value))
                        ids.Add(value);
            pageToken = root.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (pageToken != null && ids.Count < max);
        return ids.Take(max).ToArray();
    }

    /// <summary>One message as Gmail JSON (caller disposes). <paramref name="format"/> is metadata or full.</summary>
    public Task<JsonDocument> GetMessageAsync(string id, string format, CancellationToken ct)
    {
        if (!IsValidId(id)) throw new ArgumentException("Invalid message id");
        var suffix = format == "metadata" ? "?format=metadata&" + MetadataHeaders : "?format=full";
        return GetJsonAsync($"/messages/{id}{suffix}", ct);
    }

    private static readonly Regex AttachmentIdShape = new("^[A-Za-z0-9_-]{1,2048}$", RegexOptions.Compiled);

    /// <summary>users.messages.attachments.get: the content of an externalized part (MessagePartBody).</summary>
    public Task<JsonDocument> GetAttachmentAsync(string messageId, string attachmentId, CancellationToken ct)
    {
        if (!IsValidId(messageId) || !AttachmentIdShape.IsMatch(attachmentId))
            throw new ArgumentException("Invalid message or attachment id");
        return GetJsonAsync($"/messages/{messageId}/attachments/{attachmentId}", ct);
    }

    /// <summary>
    /// users.messages.attachments.get, decoded to bytes in memory. The response is read with a
    /// hard bound (base64url of <paramref name="maxBytes"/> plus envelope), so a part whose size
    /// field understates its content still cannot exceed the cap. Nothing touches disk.
    /// </summary>
    public async Task<byte[]> GetAttachmentBytesAsync(string messageId, string attachmentId, long maxBytes, CancellationToken ct)
    {
        if (!IsValidId(messageId) || !AttachmentIdShape.IsMatch(attachmentId))
            throw new ArgumentException("Invalid message or attachment id");
        var maxResponse = maxBytes / 3 * 4 + 8 + 64 * 1024;
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            endpoints.GmailBase + $"/messages/{messageId}/attachments/{attachmentId}"), ct);
        if (response.Content.Headers.ContentLength > maxResponse) throw new AttachmentTooLargeException();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxResponse) throw new AttachmentTooLargeException();
            buffer.Write(chunk, 0, read);
        }
        string? data;
        try
        {
            using var doc = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            data = doc.RootElement.TryGetProperty("data", out var d) ? d.GetString() : null;
        }
        catch (JsonException) { throw new GmailApiException(HttpStatusCode.BadGateway, "Gmail returned an unreadable attachment"); }
        finally { Array.Clear(buffer.GetBuffer()); }
        if (string.IsNullOrEmpty(data)) return [];
        var bytes = DecodeBase64Url(data);
        if (bytes.Length > maxBytes) { Array.Clear(bytes); throw new AttachmentTooLargeException(); }
        return bytes;
    }

    internal static byte[] DecodeBase64Url(string data)
    {
        var normalized = data.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(normalized); }
        catch (FormatException) { throw new GmailApiException(HttpStatusCode.BadGateway, "Gmail returned an undecodable attachment"); }
    }

    public Task<JsonDocument> GetThreadAsync(string id, CancellationToken ct)
    {
        if (!IsValidId(id)) throw new ArgumentException("Invalid thread id");
        return GetJsonAsync($"/threads/{id}?format=full", ct);
    }

    /// <summary>
    /// Batched users.messages.get (format=metadata) through Gmail's multipart batch
    /// endpoint, at most <see cref="BatchSize"/> per HTTP request. Throttled parts are
    /// retried with backoff; parts that still fail are skipped (a 404 means the message
    /// was deleted meanwhile).
    /// </summary>
    public async Task<IReadOnlyList<MailMetadata>> GetMetadataBatchAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var results = new Dictionary<string, MailMetadata>(StringComparer.Ordinal);
        foreach (var chunk in ids.Where(IsValidId).Distinct(StringComparer.Ordinal).Chunk(BatchSize))
        {
            IReadOnlyList<string> pending = chunk;
            for (var attempt = 0; pending.Count > 0; attempt++)
            {
                var (parsed, retry) = await SendBatchAsync(pending, ct);
                foreach (var item in parsed) results[item.Id] = item;
                if (retry.Count == 0) break;
                if (attempt + 1 >= MailBackoff.MaxAttempts)
                    throw new GmailApiException((HttpStatusCode)429, "Gmail kept throttling batched message reads");
                await backoff.WaitAsync(attempt, null, ct);
                pending = retry;
            }
        }
        return ids.Where(results.ContainsKey).Select(id => results[id]).ToArray();
    }

    private async Task<(List<MailMetadata> Parsed, List<string> Retry)> SendBatchAsync(
        IReadOnlyList<string> ids, CancellationToken ct)
    {
        var boundary = "mail_batch_" + Guid.NewGuid().ToString("N");
        var body = new StringBuilder();
        for (var i = 0; i < ids.Count; i++)
        {
            body.Append("--").Append(boundary).Append("\r\n")
                .Append("Content-Type: application/http\r\n")
                .Append("Content-ID: <item").Append(i).Append(">\r\n\r\n")
                .Append("GET /gmail/v1/users/me/messages/").Append(ids[i])
                .Append("?format=metadata&").Append(MetadataHeaders).Append("\r\n\r\n");
        }
        body.Append("--").Append(boundary).Append("--\r\n");

        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, endpoints.GmailBatchUrl)
            {
                Content = new StringContent(body.ToString(), Encoding.UTF8),
            };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed; boundary={boundary}");
            return request;
        }, ct);

        var parsed = new List<MailMetadata>();
        var retry = new List<string>();
        var answered = new HashSet<int>();
        var responseBoundary = response.Content.Headers.ContentType?.Parameters
            .FirstOrDefault(p => p.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"')
            ?? throw new GmailApiException(HttpStatusCode.BadGateway, "Gmail batch response had no boundary");
        var reader = new MultipartReader(responseBoundary, await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadNextSectionAsync(ct) is { } section)
        {
            var index = ParseContentIdIndex(section.Headers?.GetValueOrDefault("Content-ID").ToString());
            using var sr = new StreamReader(section.Body, Encoding.UTF8);
            var raw = await sr.ReadToEndAsync(ct);
            if (index is not { } i || i < 0 || i >= ids.Count) continue;
            answered.Add(i);
            var (status, json) = SplitHttpResponse(raw);
            if (status == 200 && json != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    parsed.Add(MailMessageParser.Metadata(doc.RootElement));
                }
                catch (JsonException) { }
            }
            else if (status == 429 || status >= 500 || status == 0)
            {
                retry.Add(ids[i]);
            }
        }
        for (var i = 0; i < ids.Count; i++)
            if (!answered.Contains(i)) retry.Add(ids[i]);
        return (parsed, retry);
    }

    internal static int? ParseContentIdIndex(string? contentId)
    {
        if (string.IsNullOrEmpty(contentId)) return null;
        var match = Regex.Match(contentId, @"item(\d+)>?\s*$");
        return match.Success && int.TryParse(match.Groups[1].Value, out var index) ? index : null;
    }

    /// <summary>Splits an application/http part ("HTTP/1.1 200 OK", headers, blank line, body).</summary>
    internal static (int Status, string? Body) SplitHttpResponse(string raw)
    {
        var normalized = raw.Replace("\r\n", "\n");
        var firstLineEnd = normalized.IndexOf('\n');
        var statusLine = firstLineEnd < 0 ? normalized : normalized[..firstLineEnd];
        var parts = statusLine.Split(' ', 3);
        var status = parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : 0;
        var bodyStart = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        return (status, bodyStart < 0 ? null : normalized[(bodyStart + 2)..].Trim());
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, endpoints.GmailBase + path), ct);
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException)
        {
            throw new GmailApiException(HttpStatusCode.BadGateway, "Gmail returned an unreadable response");
        }
    }

    /// <summary>
    /// Sends with a fresh bearer, backing off exponentially on 429/5xx (honoring
    /// Retry-After) and refreshing once on 401. Returns only successful responses.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        var refreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            var token = await tokens.GetAccessTokenAsync(ct);
            using var request = factory();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt + 1 >= MailBackoff.MaxAttempts)
                    throw new GmailApiException(HttpStatusCode.ServiceUnavailable, "Gmail could not be reached");
                await backoff.WaitAsync(attempt, null, ct);
                continue;
            }

            if (response.IsSuccessStatusCode) return response;
            var status = response.StatusCode;
            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            response.Dispose();

            if (status == HttpStatusCode.Unauthorized && !refreshed)
            {
                refreshed = true;
                await tokens.InvalidateAsync(ct);
                continue;
            }
            if (((int)status == 429 || (int)status >= 500) && attempt + 1 < MailBackoff.MaxAttempts)
            {
                await backoff.WaitAsync(attempt, retryAfter, ct);
                continue;
            }
            throw new GmailApiException(status, status switch
            {
                HttpStatusCode.NotFound => "Gmail resource not found",
                HttpStatusCode.Unauthorized => "Gmail rejected the access token",
                HttpStatusCode.Forbidden => "Gmail denied access (check the Gmail API is enabled and the scope was granted)",
                (HttpStatusCode)429 => "Gmail rate limit exceeded",
                _ => $"Gmail request failed ({(int)status})",
            });
        }
    }
}
