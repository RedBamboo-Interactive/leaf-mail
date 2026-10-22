using System.Net;
using System.Text.Json.Nodes;
using Leaf.Plugins.Mail;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Mail.Tests;

public sealed class SyncTests
{
    private static void History(Harness h, string historyId, params string[] ids)
    {
        var added = new JsonArray(ids.Select(id => (JsonNode)new JsonObject
        {
            ["messagesAdded"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["id"] = id, ["threadId"] = "t" + id } }),
        }).ToArray());
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/history", new JsonObject { ["history"] = added, ["historyId"] = historyId }.ToJsonString());
    }

    private static void Common(Harness h)
    {
        h.TokenOk();
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/labels/INBOX", """{"id":"INBOX","messagesUnread":3}""");
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/profile", """{"emailAddress":"user@example.com","historyId":"9000"}""");
    }

    [Fact]
    public async Task New_inbox_mail_is_recorded_by_id_only_and_projected_to_live()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        Common(h);
        History(h, "1010", "m1", "m2");
        h.Batch(new()
        {
            ["m1"] = Harness.MessageJson("m1", "Example Sender <sender@example.com>", "Your login link", "INBOX", "UNREAD"),
            ["m2"] = Harness.MessageJson("m2", "Shop <shop@example.com>", "Sale", "INBOX", "CATEGORY_PROMOTIONS"),
        });

        Assert.Equal(SyncResult.Synced, await h.Service.SyncOnceAsync(default));

        Assert.Equal(2, h.Records.Once.Count);
        // Kernel records carry ids only; the LIVE notice below comes from memory.
        var record = h.Records.Once["messages/m1"];
        Assert.Equal(["id", "threadId", "labelIds", "internalDate"], record.Select(p => p.Key).ToArray());
        Assert.DoesNotContain("login", record.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        var live = Assert.Single(h.Live);
        Assert.Contains("> From: Example Sender\n> Subject: Your login link", live.Content);
        Assert.Equal("mail:new:m1", live.IdempotencyKey);
        Assert.Equal(PluginLiveDisclosure.OwnerApprovedSummary, live.Recipient!.Disclosure);

        var account = (await h.Store.GetAccountAsync())!;
        Assert.Equal("1010", account.HistoryId);
        Assert.Equal(3, account.InboxUnread);
        Assert.NotNull(account.LastSyncAt);

        // Polling the same history again stores and announces nothing new.
        History(h, "1010", "m1");
        await h.Service.SyncOnceAsync(default);
        Assert.Single(h.Live);
        Assert.Contains("history?startHistoryId=1010", h.Google.Requests.Last(r => r.Url.Contains("/history")).Url);
    }

    [Fact]
    public async Task First_connect_backfills_fourteen_days_without_live_events()
    {
        var h = new Harness();
        await h.ConfigureAsync(historyId: null, backfilled: false);
        Common(h);
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/messages?q=", """{"messages":[{"id":"b1"},{"id":"b2"}]}""");
        h.Batch(new()
        {
            ["b1"] = Harness.MessageJson("b1", "A <a@example.com>", "Old 1", "INBOX"),
            ["b2"] = Harness.MessageJson("b2", "B <b@example.com>", "Old 2", "INBOX"),
        });

        Assert.Equal(SyncResult.Backfilled, await h.Service.SyncOnceAsync(default));
        Assert.Equal(2, h.Records.Once.Count);
        Assert.Empty(h.Live);
        Assert.Empty(h.Events.Published);
        Assert.Contains(h.Google.Requests, r => r.Url.Contains("q=newer_than%3A14d"));
        var account = (await h.Store.GetAccountAsync())!;
        Assert.Equal("9000", account.HistoryId);
        Assert.True(account.BackfillComplete);
    }

    [Fact]
    public async Task Expired_history_resyncs_without_live_events_and_resets_history_id()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        Common(h);
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/history", """{"error":{"code":404}}""", HttpStatusCode.NotFound);
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/messages?q=", """{"messages":[{"id":"r1"}]}""");
        h.Batch(new() { ["r1"] = Harness.MessageJson("r1", "A <a@example.com>", "During gap", "INBOX") });

        Assert.Equal(SyncResult.Resynced, await h.Service.SyncOnceAsync(default));
        Assert.Single(h.Records.Once);
        Assert.Empty(h.Live);
        Assert.Equal("9000", (await h.Store.GetAccountAsync())!.HistoryId);
    }

    [Fact]
    public async Task Invalid_grant_sets_needs_reauth_stops_and_emits_exactly_one_health_event()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        h.Google.Json(HttpMethod.Post, "https://oauth2.googleapis.com/token",
            """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", HttpStatusCode.BadRequest);

        Assert.Equal(SyncResult.NeedsReauth, await h.Service.SyncOnceAsync(default));
        var account = (await h.Store.GetAccountAsync())!;
        Assert.Equal(MailStatus.NeedsReauth, account.Status);
        Assert.Equal("invalid_grant", account.TokenHealth);
        Assert.DoesNotContain("refresh-token-1", account.LastError);
        Assert.DoesNotContain("expired or revoked", account.LastError);
        var health = Assert.Single(h.Live);
        Assert.Equal("Gmail access stopped working. Reconnect in Settings → Mail.", health.Content);
        Assert.StartsWith($"mail:health:{account.Entity.Id}:needs_reauth", health.IdempotencyKey);

        // A needs_reauth account no longer polls Google, and repeated triggers emit nothing.
        var calls = h.Google.Requests.Count;
        Assert.Equal(SyncResult.Idle, await h.Service.SyncOnceAsync(default));
        await h.Service.MarkNeedsReauthAsync("invalid_grant", "again", default);
        Assert.Equal(calls, h.Google.Requests.Count);
        Assert.Single(h.Live);
    }

    [Fact]
    public async Task Reconnect_after_needs_reauth_emits_one_recovery_event()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        h.Google.Json(HttpMethod.Post, "https://oauth2.googleapis.com/token", """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        await h.Service.SyncOnceAsync(default);
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/profile", """{"emailAddress":"user@example.com","historyId":"9000"}""");
        h.Google.Json(HttpMethod.Post, "https://oauth2.googleapis.com/revoke", "{}");

        await h.Service.ConnectAsync(Harness.Owner,
            new TokenGrant("access-2", "refresh-token-2", DateTimeOffset.UtcNow.AddHours(1), MailData.ReadonlyScope), default);

        Assert.Equal(2, h.Live.Count);
        Assert.Equal("Gmail access is working again.", h.Live[1].Content);
        Assert.NotEqual(h.Live[0].IdempotencyKey, h.Live[1].IdempotencyKey);
        var account = (await h.Store.GetAccountAsync())!;
        Assert.Equal(MailStatus.Connected, account.Status);
        Assert.Equal("refresh-token-2", account.RefreshToken);
        // Same mailbox: the sync cursor is kept; the superseded refresh token is revoked.
        Assert.Equal("1000", account.HistoryId);
        Assert.Contains(h.Google.Requests, r => r.Url.StartsWith("https://oauth2.googleapis.com/revoke") && r.Body.Contains("refresh-token-1"));
    }

    [Fact]
    public async Task Throttled_requests_back_off_and_retry()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        Common(h);
        var attempts = 0;
        h.Google.On(HttpMethod.Get, Harness.Gmail + "/history", (_, _) =>
            ++attempts < 3
                ? FakeGoogle.Response((HttpStatusCode)429, "{}")
                : FakeGoogle.Response(HttpStatusCode.OK, """{"historyId":"1001"}"""));
        Assert.Equal(SyncResult.Synced, await h.Service.SyncOnceAsync(default));
        Assert.Equal(3, attempts);

        attempts = 0;
        h.Google.On(HttpMethod.Get, Harness.Gmail + "/history", (_, _) => { attempts++; return FakeGoogle.Response(HttpStatusCode.ServiceUnavailable, "{}"); });
        var ex = await Assert.ThrowsAsync<GmailApiException>(() => h.Service.SyncOnceAsync(default));
        Assert.True(ex.Transient);
        Assert.Equal(MailBackoff.MaxAttempts, attempts);
        Assert.True(MailPoller.NextDelay(1) > MailPoller.Interval);
        Assert.Equal(MailPoller.MaxBackoff, MailPoller.NextDelay(20));
    }

    [Fact]
    public async Task Access_token_is_cached_and_refreshed_five_minutes_before_expiry()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        h.Google.Json(HttpMethod.Post, "https://oauth2.googleapis.com/token",
            $$"""{"access_token":"a","expires_in":600,"scope":"{{MailData.ReadonlyScope}}"}""");
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var tokens = new MailTokenCache(h.OAuth, h.Store, clock);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => tokens.GetAccessTokenAsync(default)));
        Assert.Equal(1, tokens.RefreshCount);
        clock.Now += TimeSpan.FromMinutes(4);
        await tokens.GetAccessTokenAsync(default);
        Assert.Equal(1, tokens.RefreshCount);
        clock.Now += TimeSpan.FromMinutes(2);
        await tokens.GetAccessTokenAsync(default);
        Assert.Equal(2, tokens.RefreshCount);
    }

    [Fact]
    public async Task Refresh_returning_a_wider_scope_fails_closed()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        h.Google.Json(HttpMethod.Post, "https://oauth2.googleapis.com/token",
            $$"""{"access_token":"a","expires_in":3600,"scope":"{{MailData.ReadonlyScope}} https://www.googleapis.com/auth/gmail.send"}""");
        Assert.Equal(SyncResult.NeedsReauth, await h.Service.SyncOnceAsync(default));
        Assert.Equal("scope_mismatch", (await h.Store.GetAccountAsync())!.TokenHealth);
    }

    [Fact]
    public async Task Context_fragment_is_counts_only_and_only_for_owner_and_bound_agent()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        var account = (await h.Store.GetAccountAsync())!;
        await h.Store.PatchAccountAsync(account, new JsonObject { ["inbox_unread"] = 3 });
        var extensions = new MailNovaExtensions(new NoExtensions(), h.Store, h.Outbox);

        var fragment = await extensions.ContextAsync(new PluginContextRequest(Harness.Owner, Harness.Agent, "d", "conversation"), default);
        Assert.Equal("Mail connected (user@…): 3 unread in Inbox. Use the mail skill to search or read.", fragment!.Content);
        Assert.DoesNotContain("example.com", fragment.Content);

        Assert.Null(await extensions.ContextAsync(new PluginContextRequest(Harness.Owner, "44444444-4444-4444-4444-444444444444", "d", "conversation"), default));
        Assert.Null(await extensions.ContextAsync(new PluginContextRequest("33333333-3333-3333-3333-333333333333", Harness.Agent, "d", "conversation"), default));
        Assert.Null(await extensions.ContextAsync(new PluginContextRequest(null, null, null, "conversation"), default));

        await h.Store.PatchAccountAsync(account, new JsonObject { ["status"] = MailStatus.NeedsReauth });
        var reauth = await extensions.ContextAsync(new PluginContextRequest(Harness.Owner, Harness.Agent, "d", "heartbeat"), default);
        Assert.StartsWith("Mail needs reconnecting: Settings → Mail", reauth!.Content);
    }

    [Fact]
    public async Task Message_read_returns_untrusted_sanitized_body_and_is_never_stored()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        h.TokenOk();
        var data = Convert.ToBase64String("<p>Hello <a href=\"https://example.com/x\">here</a></p><script>x()</script>"u8.ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/messages/m9?format=full", """
            {"id":"m9","threadId":"t9","labelIds":["INBOX"],"payload":{"mimeType":"text/html",
             "headers":[{"name":"From","value":"A <a@example.com>"},{"name":"Subject","value":"S"},{"name":"Content-Type","value":"text/html; charset=utf-8"}],
             "body":{"data":"DATA"}}}
            """.Replace("DATA", data));
        var result = await h.Service.MessageAsync("m9", default);
        Assert.True(result["untrusted"]!.GetValue<bool>());
        Assert.Equal("Hello here [link 1]", result["body"]!.GetValue<string>());
        Assert.Equal("https://example.com/x", result["links"]![0]!["href"]!.GetValue<string>());
        Assert.Empty(h.Records.Once);
    }

    private sealed class NoExtensions : IPluginExtensions
    {
        public IDisposable RegisterContextProvider(string extensionId, Func<PluginContextRequest, CancellationToken, Task<PluginContextFragment?>> provider) => throw new NotSupportedException();
        public IDisposable RegisterEventProjector(string extensionId, string eventType, Func<Leaf.Sdk.Services.PluginEvent, CancellationToken, Task<PluginLiveEventProjection?>> projector) => throw new NotSupportedException();
        public Task<IReadOnlyList<PluginContextFragment>> CollectContextAsync(string slotId, PluginContextRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public IDisposable SubscribeLiveEvents(string slotId, Func<PluginLiveEventProjection, CancellationToken, Task> handler) => throw new NotSupportedException();
    }
}
