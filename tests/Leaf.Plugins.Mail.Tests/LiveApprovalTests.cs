using System.Net;
using System.Text.Json.Nodes;
using Leaf.Plugins.Mail;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Mail.Tests;

/// <summary>New-mail LIVE notices require the owner's explicit approval (Nova OwnerApprovedSummary).</summary>
public sealed class LiveApprovalTests
{
    private static void NewMail(Harness h)
    {
        h.TokenOk();
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/labels/INBOX", """{"messagesUnread":1}""");
        h.Google.Json(HttpMethod.Get, Harness.Gmail + "/history", """{"history":[{"messagesAdded":[{"message":{"id":"m1"}}]}],"historyId":"1010"}""");
        h.Batch(new() { ["m1"] = Harness.MessageJson("m1", "Example Sender <sender@example.com>", "Hello", "INBOX") });
    }

    [Fact]
    public async Task Approval_is_off_by_default_and_new_mail_is_neither_projected_nor_announced_on_the_bus()
    {
        var h = new Harness();
        await h.ConfigureAsync(approveLive: false);
        NewMail(h);

        Assert.Equal(SyncResult.Synced, await h.Service.SyncOnceAsync(default));
        Assert.Single(h.Records.Once); // still recorded for sync
        Assert.Empty(h.Live);
        Assert.Empty(h.Events.Published);
        Assert.Equal(LiveApproval.Off, await h.Store.GetLiveApprovalAsync());
        Assert.False((await h.Service.StatusAsync(default))["live_approved"]!.GetValue<bool>());
        Assert.False(MailData.Boolean((await h.Store.GetConfigAsync()).Entity.Data, "live_approved"));
    }

    [Fact]
    public async Task Approved_new_mail_is_projected_as_an_owner_approved_summary()
    {
        var h = new Harness();
        await h.ConfigureAsync(approveLive: false);
        var approval = await h.Store.SetLiveApprovalAsync(true, Harness.Owner);
        NewMail(h);

        await h.Service.SyncOnceAsync(default);
        var projection = Assert.Single(h.Live);
        Assert.Equal(PluginLiveDisclosure.OwnerApprovedSummary, projection.Recipient!.Disclosure);
        Assert.Equal(Harness.Owner, projection.Recipient.OwnerUserId);
        Assert.Equal(Harness.Agent, projection.Recipient.AgentId);
        Assert.Contains("> Subject: Hello", projection.Content);
        Assert.Equal(Harness.Owner, approval.ApprovedBy);
        Assert.True((await h.Service.StatusAsync(default))["live_approved"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Health_notices_use_the_approval_when_on_and_ConfidentialOnly_otherwise()
    {
        foreach (var approved in new[] { false, true })
        {
            var h = new Harness();
            await h.ConfigureAsync(approveLive: approved);
            h.Google.Json(HttpMethod.Post, "https://oauth2.googleapis.com/token", """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
            Assert.Equal(SyncResult.NeedsReauth, await h.Service.SyncOnceAsync(default));
            var health = Assert.Single(h.Live);
            Assert.Equal(approved ? PluginLiveDisclosure.OwnerApprovedSummary : PluginLiveDisclosure.ConfidentialOnly,
                health.Recipient!.Disclosure);
            Assert.DoesNotContain("example.com", health.Content);
        }
    }

    [Fact]
    public async Task Revoking_or_replacing_the_approval_drops_notices_queued_under_it()
    {
        var h = new Harness();
        await h.ConfigureAsync(approveLive: true);
        h.Events.OnPublish = null; // hold the nonce events instead of projecting immediately
        NewMail(h);
        await h.Service.SyncOnceAsync(default);
        var queued = Assert.Single(h.Events.Published);

        await h.Store.SetLiveApprovalAsync(false, Harness.Owner);
        Assert.Null(await h.Outbox.ProjectAsync(queued, default));

        // Re-approval mints a new id: a notice queued under the earlier approval stays dropped.
        var h2 = new Harness();
        await h2.ConfigureAsync(approveLive: true);
        h2.Events.OnPublish = null;
        NewMail(h2);
        await h2.Service.SyncOnceAsync(default);
        var queued2 = Assert.Single(h2.Events.Published);
        await h2.Store.SetLiveApprovalAsync(false, Harness.Owner);
        await h2.Store.SetLiveApprovalAsync(true, Harness.Owner);
        Assert.Null(await h2.Outbox.ProjectAsync(queued2, default));
    }

    [Fact]
    public async Task Approval_switched_on_outside_mail_is_reverted_and_never_used()
    {
        var h = new Harness();
        await h.ConfigureAsync(approveLive: false);
        Assert.Equal(LiveApproval.Off, await h.Store.GetLiveApprovalAsync()); // pins "off"
        var config = await h.Store.GetConfigAsync();
        // What the owning agent could do through PATCH /api/entities on the confidential config.
        await h.Entities.PatchAsync(config.Entity.Id, new JsonObject
        {
            ["live_approved"] = true,
            ["live_approval_id"] = "forged",
            ["live_approved_by"] = Harness.Owner,
            ["live_approved_at"] = DateTimeOffset.UtcNow.ToString("O"),
        });

        Assert.Equal(LiveApproval.Off, await h.Store.GetLiveApprovalAsync());
        Assert.False(MailData.Boolean((await h.Store.GetConfigAsync()).Entity.Data, "live_approved"));
        NewMail(h);
        await h.Service.SyncOnceAsync(default);
        Assert.Empty(h.Live);
    }

    [Fact]
    public async Task Only_the_owner_can_set_the_approval_and_generic_patches_cannot()
    {
        var h = new Harness();
        await h.ConfigureAsync(approveLive: false);
        Assert.Equal("owner_required", (await Assert.ThrowsAsync<MailFault>(() =>
            h.Store.SetLiveApprovalAsync(true, "33333333-3333-3333-3333-333333333333"))).Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Store.PatchConfigAsync(new JsonObject { ["live_approved"] = true }));
        Assert.Equal(LiveApproval.Off, await h.Store.GetLiveApprovalAsync());
    }

    [Fact]
    public async Task An_approval_recorded_for_a_previous_owner_is_not_valid()
    {
        var approval = new LiveApproval(true, "id", "33333333-3333-3333-3333-333333333333", null);
        Assert.False(approval.ValidFor(Harness.Owner));
        Assert.True(approval.ValidFor("33333333-3333-3333-3333-333333333333"));
        Assert.False((approval with { ApprovalId = "" }).ValidFor("33333333-3333-3333-3333-333333333333"));
        await Task.CompletedTask;
    }
}
