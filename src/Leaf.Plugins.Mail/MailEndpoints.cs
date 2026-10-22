using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Leaf.Plugins.Mail;

/// <summary>
/// Mail's HTTP surface. The kernel owns <c>/settings</c>, <c>/enable</c> and
/// <c>/disable</c>; they are not mapped here. Every route runs behind the strict caller
/// filter; settings and OAuth routes additionally require a plain owner session.
/// </summary>
internal static class MailEndpoints
{
    private const string CallerKey = "Mail.Caller";
    public const int MaxBodyBytes = 16 * 1024;

    public static void Map(RouteGroupBuilder group)
    {
        // Host authentication is required; this filter additionally rejects LocalDefault,
        // other users, non-bound or public/Discord executions. Authorization never relies on
        // identifiers from request payloads.
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var store = http.RequestServices.GetRequiredService<MailStore>();
                var policy = await store.GetPolicyAsync(http.RequestAborted);
                http.Items[CallerKey] = await MailCaller.FromAsync(http.User, policy,
                    store.IsOwnedNovaDiscussionAsync, http.RequestAborted);
                return await next(ctx);
            }
            catch (MailFault fault) { return Error(fault.Code, fault.Code, fault.Status); }
            catch (MailReauthRequiredException ex)
            {
                await http.RequestServices.GetRequiredService<MailService>()
                    .MarkNeedsReauthAsync(ex.Code, ex.Message, CancellationToken.None);
                return Error("mail_needs_reauth", ex.Message, 409);
            }
            catch (MailNotConnectedException) { return Error("mail_not_connected", "Mail is not connected", 409); }
            catch (MailOAuthException ex) { return Error(ex.Code, ex.Message, ex.Code == "unreachable" ? 502 : 400); }
            catch (GmailApiException ex)
            {
                var status = (int)ex.Status switch { 404 => 404, 429 => 429, 403 => 403, _ => 502 };
                return Error("gmail_error", ex.Message, status);
            }
            catch (ArgumentException ex) { return Error("invalid_request", ex.Message, 400); }
            catch (JsonException) { return Error("malformed_request", "Request does not match the contract", 400); }
            catch (BadHttpRequestException) { return Error("malformed_request", "Malformed request", 400); }
        });

        group.MapGet("/", () => Results.Ok(MailDiscovery.Document()));
        group.MapGet("/discover", () => Results.Ok(MailDiscovery.Document()));
        group.MapGet("/openapi.json", () => Results.Ok(MailDiscovery.OpenApi()));

        group.MapGet("/status", async (HttpContext c, MailService service) =>
            Results.Ok(await service.StatusAsync(c.RequestAborted)));

        group.MapGet("/search", async (HttpContext c, MailService service) =>
        {
            var q = c.Request.Query["q"].ToString().Trim();
            if (q.Length is 0 or > 512) throw new ArgumentException("q is required (Gmail search syntax, at most 512 characters)");
            var max = 20;
            if (c.Request.Query.TryGetValue("max", out var rawMax))
            {
                if (!int.TryParse(rawMax, out max) || max < 1 || max > 50)
                    throw new ArgumentException("max must be an integer between 1 and 50");
            }
            return Results.Ok(await service.SearchAsync(q, max, c.RequestAborted));
        });

        group.MapGet("/messages/{id}", async (string id, HttpContext c, MailService service) =>
        {
            if (!GmailClient.IsValidId(id)) throw new ArgumentException("Invalid message id");
            return Results.Ok(await service.MessageAsync(id, c.RequestAborted));
        });

        group.MapGet("/threads/{id}", async (string id, HttpContext c, MailService service) =>
        {
            if (!GmailClient.IsValidId(id)) throw new ArgumentException("Invalid thread id");
            return Results.Ok(await service.ThreadAsync(id, c.RequestAborted));
        });

        // ── Owner-only settings and OAuth ─────────────────────────────────────

        group.MapGet("/agents", async (HttpContext c, MailStore store) =>
        {
            var caller = OwnerUser(c);
            var agents = await store.Entities.QueryAsync(new Leaf.Sdk.EntityQuery { TypeSlug = "agent", Limit = 100 }, c.RequestAborted);
            return Results.Ok(new
            {
                items = agents
                    .Where(a => !MailData.Boolean(a.Data, "confidential")
                        || string.Equals(MailData.String(a.Data, "owner_id"), caller.UserId, StringComparison.OrdinalIgnoreCase))
                    .Select(a => new
                    {
                        id = a.Id,
                        slug = a.Slug,
                        name = a.Name,
                        // Same-origin asset paths only; the picker renders them as the card avatar.
                        avatar = SameOriginPath(MailData.String(a.Data, "avatar_url") ?? MailData.String(a.Data, "avatar")),
                        confidential = MailData.Boolean(a.Data, "confidential"),
                    })
                    .ToArray(),
            });
        });

        group.MapPut("/config", async (HttpContext c, MailStore store, MailService service, MailPoller poller) =>
        {
            var caller = OwnerUser(c);
            var body = await ReadJsonAsync(c);
            var patch = await ValidateConfigAsync(body, caller, store, c.RequestAborted);
            await store.ClaimOwnerAsync(caller.UserId, caller.IsAdmin, c.RequestAborted);
            if (patch.Remove("agent_id", out var agent))
                await store.SetAgentAsync(agent?.GetValue<string>(), c.RequestAborted);
            // Owner-session-only (OwnerUser above): the explicit LIVE approval.
            if (patch.Remove("live_approved", out var approved))
                await store.SetLiveApprovalAsync(approved!.GetValue<bool>(), caller.UserId, c.RequestAborted);
            if (patch.Count > 0) await store.PatchConfigAsync(patch, c.RequestAborted);
            poller.Wake();
            return Results.Ok(await service.StatusAsync(c.RequestAborted));
        });

        group.MapPut("/client", async (HttpContext c, MailStore store, MailService service, MailPoller poller) =>
        {
            var caller = OwnerUser(c);
            var raw = await ReadBodyAsync(c);
            // Accept the downloaded file as the body, or { "json": "<file contents>" }.
            if (JsonNode.Parse(raw) is JsonObject wrapper && wrapper["json"] is JsonValue inner
                && inner.TryGetValue<string>(out var text))
                raw = text;
            var client = GoogleClientJson.Parse(raw);
            // A refresh token is bound to the client that minted it.
            var current = await store.GetConfigAsync(c.RequestAborted);
            if ((await store.GetAccountAsync(c.RequestAborted))?.RefreshToken != null && current.ClientId != client.ClientId)
                throw new MailFault("disconnect_first", 409);
            await store.ClaimOwnerAsync(caller.UserId, caller.IsAdmin, c.RequestAborted);
            await store.PatchConfigAsync(new JsonObject { ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret }, c.RequestAborted);
            poller.Wake();
            return Results.Ok(await service.StatusAsync(c.RequestAborted));
        });

        group.MapDelete("/client", async (HttpContext c, MailStore store, MailService service) =>
        {
            OwnerUser(c);
            var account = await store.GetAccountAsync(c.RequestAborted);
            if (account?.RefreshToken != null) throw new MailFault("disconnect_first", 409);
            await store.PatchConfigAsync(new JsonObject { ["client_id"] = null, ["client_secret"] = null }, c.RequestAborted);
            return Results.Ok(await service.StatusAsync(c.RequestAborted));
        });

        group.MapPost("/oauth/start", async (HttpContext c, MailStore store, MailConsent consent) =>
        {
            var caller = OwnerUser(c);
            await store.ClaimOwnerAsync(caller.UserId, caller.IsAdmin, c.RequestAborted);
            return Results.Ok(await consent.StartAsync(caller.UserId, c.RequestAborted));
        });

        group.MapPost("/oauth/complete", async (HttpContext c, MailConsent consent) =>
        {
            var caller = OwnerUser(c);
            var body = await ReadJsonAsync(c);
            var redirectUrl = MailData.String(body, "redirectUrl")
                ?? throw new ArgumentException("redirectUrl is required");
            if (redirectUrl.Length > 4096) throw new ArgumentException("redirectUrl is too long");
            return Results.Ok(await consent.CompleteFromRedirectUrlAsync(caller.UserId, redirectUrl, c.RequestAborted));
        });

        group.MapPost("/disconnect", async (HttpContext c, MailService service, MailConsent consent) =>
        {
            OwnerUser(c);
            consent.Cancel();
            return Results.Ok(await service.DisconnectAsync(c.RequestAborted));
        });
    }

    private static string? SameOriginPath(string? value)
        => value is { Length: > 1 and <= 512 } && value[0] == '/' && value[1] != '/' && !value.Contains('\\') ? value : null;

    private static MailCaller OwnerUser(HttpContext c)
    {
        var caller = (MailCaller)c.Items[CallerKey]!;
        caller.RequireOwnerUser();
        return caller;
    }

    private static async Task<JsonObject> ValidateConfigAsync(JsonObject body, MailCaller caller, MailStore store, CancellationToken ct)
    {
        var patch = new JsonObject();
        foreach (var (key, value) in body)
        {
            switch (key)
            {
                case "agent_id":
                    var agentId = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                    if (agentId == null) { patch["agent_id"] = null; break; }
                    if (!Guid.TryParse(agentId, out var agentGuid) || !await store.IsAgentAsync(agentId, ct))
                        throw new ArgumentException("agent_id must be an agent entity id");
                    var agent = await store.Entities.GetAsync(agentGuid, ct);
                    if (MailData.Boolean(agent!.Data, "confidential")
                        && !string.Equals(MailData.String(agent.Data, "owner_id"), caller.UserId, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("agent_id must be an agent entity id");
                    patch["agent_id"] = agentGuid.ToString();
                    break;
                case "live_approved":
                    if (value is not JsonValue b || !b.TryGetValue<bool>(out var approved))
                        throw new ArgumentException("live_approved must be a boolean");
                    patch["live_approved"] = approved;
                    break;
                case "live_excluded_categories":
                    if (value is not JsonArray array) throw new ArgumentException("live_excluded_categories must be an array");
                    var categories = array.Select(item => item is JsonValue cv && cv.TryGetValue<string>(out var c) ? c : null).ToArray();
                    if (categories.Any(c => c == null || !MailData.KnownCategories.Contains(c)))
                        throw new ArgumentException("live_excluded_categories accepts only " + string.Join(", ", MailData.KnownCategories));
                    patch["live_excluded_categories"] = new JsonArray(categories.Distinct().Select(c => (JsonNode)JsonValue.Create(c!)).ToArray());
                    break;
                default:
                    throw new ArgumentException($"Unknown setting '{key}'");
            }
        }
        return patch;
    }

    private static async Task<JsonObject> ReadJsonAsync(HttpContext c)
        => JsonNode.Parse(await ReadBodyAsync(c)) as JsonObject ?? throw new ArgumentException("A JSON object is required");

    private static async Task<string> ReadBodyAsync(HttpContext c)
    {
        if (c.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            throw new ArgumentException("Content-Type must be application/json");
        if (c.Request.ContentLength > MaxBodyBytes) throw new ArgumentException("Request body is too large");
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await c.Request.Body.ReadAsync(buffer, c.RequestAborted)) > 0)
        {
            if (memory.Length + read > MaxBodyBytes) throw new ArgumentException("Request body is too large");
            memory.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static IResult Error(string code, string message, int status)
        => Results.Json(new { error = new { code, message } }, statusCode: status);
}
