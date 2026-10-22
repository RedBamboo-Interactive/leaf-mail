using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Leaf.Plugins.Mail;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Mail.Tests;

internal sealed class MemoryEntities : IEntityStore
{
    private readonly ConcurrentDictionary<Guid, LeafEntity> _items = new();

    public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_items.TryGetValue(id, out var e) ? Clone(e) : null);

    public Task<LeafEntity?> GetBySlugAsync(string typeSlug, string slug, CancellationToken ct = default)
        => Task.FromResult(_items.Values.FirstOrDefault(e => e.TypeSlug == typeSlug && e.Slug == slug) is { } e ? Clone(e) : null);

    public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default)
        => Task.FromResult(_items.Values.FirstOrDefault(e => e.Slug == slug) is { } e ? Clone(e) : null);

    public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default)
    {
        var rows = _items.Values.Where(e => e.TypeSlug == query.TypeSlug)
            .Where(e => query.DataEquals == null || query.DataEquals.All(kv =>
                e.Data[kv.Key] is JsonValue v && v.ToString() == kv.Value?.ToString()))
            .Take(query.Limit).Select(Clone).ToArray();
        return Task.FromResult<IReadOnlyList<LeafEntity>>(rows);
    }

    public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default)
        => Task.FromResult(Put(new LeafEntity(Guid.NewGuid(), typeSlug, Guid.NewGuid().ToString("N"), name,
            data?.DeepClone() as JsonObject ?? [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));

    public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default)
    {
        var existing = _items.Values.FirstOrDefault(e => e.TypeSlug == typeSlug && e.Slug == slug);
        var entity = new LeafEntity(existing?.Id ?? Guid.NewGuid(), typeSlug, slug, name,
            data?.DeepClone() as JsonObject ?? [], existing?.CreatedAt ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        return Task.FromResult(Put(entity));
    }

    public Task<LeafEntity> PatchAsync(Guid id, JsonObject dataPatch, string? name = null, CancellationToken ct = default)
    {
        var existing = _items[id];
        var data = existing.Data.DeepClone().AsObject();
        foreach (var (key, value) in dataPatch) data[key] = value?.DeepClone();
        return Task.FromResult(Put(existing with { Data = data, Name = name ?? existing.Name, UpdatedAt = DateTimeOffset.UtcNow }));
    }

    public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default)
        => Task.FromResult(Put(_items[id] with { Data = data.DeepClone().AsObject() }));

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        _items.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public LeafEntity Add(string type, string slug, JsonObject data)
        => Put(new LeafEntity(Guid.NewGuid(), type, slug, slug, data, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));

    private LeafEntity Put(LeafEntity entity)
    {
        _items[entity.Id] = entity;
        return Clone(entity);
    }

    private static LeafEntity Clone(LeafEntity e) => e with { Data = e.Data.DeepClone().AsObject() };
}

internal sealed class MemoryRecords : IRecordStreams
{
    public readonly ConcurrentDictionary<string, JsonObject> Once = new();

    public Task AppendAsync(string stream, JsonObject data, Guid? entityId = null, string? userId = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> AppendOnceAsync(string stream, string operationId, JsonObject data,
        Guid? entityId = null, string? userId = null, CancellationToken ct = default)
        => Task.FromResult(Once.TryAdd(stream + "/" + operationId, data.DeepClone().AsObject()));

    public Task<IReadOnlyList<LeafRecord>> QueryAsync(string stream, RecordQuery? query = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LeafRecord>>([]);
}

internal sealed class MemoryEvents : IPluginEvents
{
    public readonly List<PluginEvent> Published = [];
    public Func<PluginEvent, Task>? OnPublish;

    public async Task PublishAsync(string eventType, JsonObject payload, CancellationToken ct = default)
    {
        var evt = new PluginEvent(eventType, MailPlugin.PluginId, payload.DeepClone().AsObject(), DateTimeOffset.UtcNow);
        lock (Published) Published.Add(evt);
        if (OnPublish != null) await OnPublish(evt);
    }

    public IDisposable Subscribe(string eventType, Func<PluginEvent, Task> handler) => new Nothing();
    private sealed class Nothing : IDisposable { public void Dispose() { } }
}

/// <summary>Stands in for Google over HTTP: routes requests by method + path prefix; no network.</summary>
internal sealed class FakeGoogle : HttpMessageHandler
{
    public readonly List<(HttpMethod Method, string Url, string Body)> Requests = [];
    private readonly List<(HttpMethod Method, string PathPrefix, Func<HttpRequestMessage, string, HttpResponseMessage> Respond)> _routes = [];

    public FakeGoogle On(HttpMethod method, string urlPrefix, Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        _routes.Insert(0, (method, urlPrefix, respond));
        return this;
    }

    public FakeGoogle Json(HttpMethod method, string urlPrefix, string json, HttpStatusCode status = HttpStatusCode.OK)
        => On(method, urlPrefix, (_, _) => Response(status, json));

    public static HttpResponseMessage Response(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public int Count(string urlPrefix) { lock (Requests) return Requests.Count(r => r.Url.StartsWith(urlPrefix, StringComparison.Ordinal)); }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        var url = request.RequestUri!.AbsoluteUri;
        lock (Requests) Requests.Add((request.Method, url, body));
        foreach (var route in _routes)
            if (route.Method == request.Method && url.StartsWith(route.PathPrefix, StringComparison.Ordinal))
                return route.Respond(request, body);
        return Response(HttpStatusCode.NotFound, "{\"error\":{\"code\":404}}");
    }
}

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Builds the plugin's object graph against fakes, mirroring Plugin.ConfigureServices.</summary>
internal sealed class Harness
{
    public const string Owner = "11111111-1111-1111-1111-111111111111";
    public const string Agent = "22222222-2222-2222-2222-222222222222";
    public const string ClientId = "123456789012-abcdefghijklmnop0123456789abcdef.apps.googleusercontent.com";
    public const string ClientSecret = "GOCSPX-test_secret_value";
    public const string Gmail = "https://gmail.googleapis.com/gmail/v1/users/me";

    public readonly MemoryEntities Entities = new();
    public readonly MemoryRecords Records = new();
    public readonly MemoryEvents Events = new();
    public readonly FakeGoogle Google = new();
    public readonly GoogleEndpoints Endpoints = new();
    public readonly MailStore Store;
    public readonly GoogleOAuthClient OAuth;
    public readonly MailTokenCache Tokens;
    public readonly GmailClient Gmail_;
    public readonly MailLiveOutbox Outbox;
    public readonly MailService Service;
    public readonly List<PluginLiveEventProjection> Live = [];

    public Harness()
    {
        var http = new HttpClient(Google);
        Store = new MailStore(Entities);
        OAuth = new GoogleOAuthClient(http, Endpoints);
        Tokens = new MailTokenCache(OAuth, Store);
        Gmail_ = new GmailClient(http, Endpoints, Tokens, new MailBackoff((_, _) => Task.CompletedTask));
        Outbox = new MailLiveOutbox(Events);
        Service = new MailService(Store, Tokens, Gmail_, OAuth, Records, Outbox);
        // Behave like the kernel registry: the projector resolves each nonce event.
        Events.OnPublish = async evt =>
        {
            if (await Outbox.ProjectAsync(evt, default) is { } projection) lock (Live) Live.Add(projection);
        };
    }

    public async Task ConfigureAsync(bool connected = true, string? historyId = "1000", bool backfilled = true)
    {
        await Store.EnsureConfigAsync();
        await Store.PatchConfigAsync(new JsonObject
        {
            ["owner_user_id"] = Owner,
            ["agent_id"] = Agent,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
        });
        if (connected)
            await Store.UpsertAccountAsync(new JsonObject
            {
                ["status"] = MailStatus.Connected,
                ["email"] = "user@example.com",
                ["refresh_token"] = "refresh-token-1",
                ["granted_scope"] = MailData.ReadonlyScope,
                ["history_id"] = historyId,
                ["backfill_complete"] = backfilled,
            });
    }

    public void TokenOk(string access = "access-1")
        => Google.Json(HttpMethod.Post, "https://oauth2.googleapis.com/token",
            $"{{\"access_token\":\"{access}\",\"expires_in\":3599,\"scope\":\"{MailData.ReadonlyScope}\",\"token_type\":\"Bearer\"}}");

    public static string MessageJson(string id, string from, string subject, params string[] labels)
        => new JsonObject
        {
            ["id"] = id,
            ["threadId"] = "t" + id,
            ["labelIds"] = new JsonArray(labels.Select(l => (JsonNode)JsonValue.Create(l)).ToArray()),
            ["snippet"] = "snippet " + id,
            ["internalDate"] = "1767225600000",
            ["payload"] = new JsonObject
            {
                ["headers"] = new JsonArray(
                    new JsonObject { ["name"] = "From", ["value"] = from },
                    new JsonObject { ["name"] = "To", ["value"] = "user@example.com" },
                    new JsonObject { ["name"] = "Subject", ["value"] = subject }),
            },
        }.ToJsonString();

    /// <summary>Serves the Gmail batch endpoint from a map of id → message JSON.</summary>
    public void Batch(Dictionary<string, string> messages)
        => Google.On(HttpMethod.Post, Endpoints.GmailBatchUrl, (request, body) =>
        {
            var ids = System.Text.RegularExpressions.Regex.Matches(body, @"messages/([A-Za-z0-9]+)\?").Select(m => m.Groups[1].Value).ToArray();
            var builder = new StringBuilder();
            for (var i = 0; i < ids.Length; i++)
            {
                builder.Append("--resp\r\nContent-Type: application/http\r\nContent-ID: <response-item").Append(i).Append(">\r\n\r\n");
                if (messages.TryGetValue(ids[i], out var json))
                    builder.Append("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n").Append(json).Append("\r\n");
                else
                    builder.Append("HTTP/1.1 404 Not Found\r\nContent-Type: application/json\r\n\r\n{}\r\n");
            }
            builder.Append("--resp--\r\n");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(builder.ToString(), Encoding.UTF8) };
            response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("multipart/mixed; boundary=resp");
            return response;
        });
}
