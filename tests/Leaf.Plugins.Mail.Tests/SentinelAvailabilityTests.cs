using System.Net;
using System.Text.Json.Nodes;
using Leaf.Plugins.Mail;
using Xunit;

namespace Leaf.Plugins.Mail.Tests;

/// <summary>"If the Sentinel is down, inward is rejected": no mail content leaves Mail without it.</summary>
public sealed class SentinelAvailabilityTests
{
    private static async Task<Harness> DownAsync(string state)
    {
        var h = new Harness(sentinelInstalled: state != "not_installed");
        await h.ConfigureAsync();
        h.TokenOk();
        if (state is "disabled" or "tripped" or "mismatch") await h.SetSentinelStatusAsync(state);
        if (state == "status_unknown")
            await h.Entities.PatchAsync(h.SentinelPluginEntity!.Id, new JsonObject { ["status"] = "not-a-guid" });
        return h;
    }

    private static readonly Func<Harness, Task>[] ContentRoutes =
    [
        h => h.Service.SearchAsync("subject:receipt", 20, default),
        h => h.Service.MessageAsync("m1", Harness.OwnerCaller, default),
        h => h.Service.ThreadAsync("t1", Harness.OwnerCaller, default),
        h => h.Service.AttachmentsAsync("m7", default),
        h => h.Service.AttachmentTextAsync("m7", "1", Harness.OwnerCaller, default),
    ];

    [Theory]
    [InlineData("not_installed")]
    [InlineData("disabled")]
    [InlineData("tripped")]
    [InlineData("mismatch")]
    [InlineData("status_unknown")]
    public async Task Every_content_route_refuses_with_503_and_status_reports_it(string state)
    {
        var h = await DownAsync(state);
        foreach (var route in ContentRoutes)
        {
            var fault = await Assert.ThrowsAsync<MailFault>(() => route(h));
            Assert.Equal(("sentinel_unavailable", 503), (fault.Code, fault.Status));
            Assert.Contains($"({state})", fault.Detail);
        }
        Assert.DoesNotContain(h.Google.Requests, r => r.Url.StartsWith(Harness.Gmail, StringComparison.Ordinal));

        var status = await h.Service.StatusAsync(default);
        Assert.False(status["sentinel"]!["available"]!.GetValue<bool>());
        Assert.Equal(state, status["sentinel"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reads_resume_once_the_sentinel_is_enabled_again()
    {
        var h = await DownAsync("disabled");
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/messages/m1?format=full",
            Harness.MessageJson("m1", "Shop <shop@example.com>", "Receipt", "INBOX"));
        await Assert.ThrowsAsync<MailFault>(() => h.Service.MessageAsync("m1", Harness.OwnerCaller, default));

        await h.SetSentinelStatusAsync("enabled");
        var result = await h.Service.MessageAsync("m1", Harness.OwnerCaller, default);
        Assert.Equal("Receipt", result["metadata"]!["subject"]!.GetValue<string>());
        var status = await h.Service.StatusAsync(default);
        Assert.True(status["sentinel"]!["available"]!.GetValue<bool>());
        Assert.Null(status["sentinel"]!["reason"]);
    }

    [Fact]
    public async Task Availability_is_cached_briefly_and_fails_closed_when_the_status_cannot_be_read()
    {
        var entities = new MemoryEntities();
        var enabled = entities.Add("plugin-status", "enabled", new JsonObject());
        var disabled = entities.Add("plugin-status", "disabled", new JsonObject());
        var plugin = entities.Add("plugin", MailSentinel.SentinelPluginId, new JsonObject { ["status"] = enabled.Id.ToString() });
        var sentinel = new MailSentinel(new FakeReview(), entities: entities);

        Assert.True((await sentinel.AvailabilityAsync(default)).Available);
        await entities.PatchAsync(plugin.Id, new JsonObject { ["status"] = disabled.Id.ToString() });
        Assert.True((await sentinel.AvailabilityAsync(default)).Available); // within the 5 s cache
        Assert.False((await new MailSentinel(new FakeReview(), entities: entities).AvailabilityAsync(default)).Available);

        entities.FailRead = true;
        Assert.Equal(new SentinelAvailability(false, "status_unknown"),
            await new MailSentinel(new FakeReview(), entities: entities).AvailabilityAsync(default));
    }

    private static void History(Harness h, string historyId, params string[] ids)
    {
        var added = new JsonArray(ids.Select(id => (JsonNode)new JsonObject
        {
            ["messagesAdded"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["id"] = id, ["threadId"] = "t" + id } }),
        }).ToArray());
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/history", new JsonObject { ["history"] = added, ["historyId"] = historyId }.ToJsonString());
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/labels/INBOX", """{"id":"INBOX","messagesUnread":1}""");
    }

    [Fact]
    public async Task New_mail_live_notices_are_skipped_while_the_sentinel_is_down_but_health_notices_go_out()
    {
        var h = await DownAsync("disabled");
        History(h, "1010", "m1");
        h.Batch(new() { ["m1"] = Harness.MessageJson("m1", "Example Sender <sender@example.com>", "Your login link", "INBOX", "UNREAD") });

        Assert.Equal(SyncResult.Synced, await h.Service.SyncOnceAsync(default));
        Assert.Single(h.Records.Once); // still recorded by id, so it is never announced later either
        Assert.Empty(h.Live);

        // A health notice carries no mail content and is still published.
        await h.Service.MarkNeedsReauthAsync("invalid_grant", "revoked", default);
        var health = Assert.Single(h.Live);
        Assert.Equal("Gmail access stopped working. Reconnect in Settings → Mail.", health.Content);
    }

    [Fact]
    public async Task New_mail_live_notices_go_out_when_the_sentinel_is_up()
    {
        var h = await DownAsync("enabled");
        History(h, "1010", "m1");
        h.Batch(new() { ["m1"] = Harness.MessageJson("m1", "Example Sender <sender@example.com>", "Your login link", "INBOX", "UNREAD") });
        await h.Service.SyncOnceAsync(default);
        Assert.Contains("Your login link", Assert.Single(h.Live).Content);
    }
}
