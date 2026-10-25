using System.Text.Json.Nodes;
using Leaf.Plugins.Mail;
using Xunit;

namespace Leaf.Plugins.Mail.Tests;

/// <summary>
/// Mail's config and account through install, claim, bind and unbind, written through a store that
/// refuses what the kernel refuses (confidential data naming an agent but no owner). The config is
/// sealed until claimed, then owner-only, which the kernel accepts and lists to the owner alone.
/// </summary>
public sealed class ConfidentialOwnershipTests
{
    private static async Task<(JsonObject Config, JsonObject? Account)> StoredAsync(Harness h)
        => ((await h.Entities.GetBySlugAsync(MailData.ConfigType, MailData.ConfigSlug))!.Data,
            (await h.Entities.GetBySlugAsync(MailData.AccountType, MailData.AccountSlug))?.Data);

    [Fact]
    public async Task A_fresh_install_keeps_its_config_and_account_sealed()
    {
        var h = new Harness();
        await h.Store.EnsureConfigAsync();
        var policy = await h.Store.GetPolicyAsync();
        Assert.Null(policy.OwnerUserId);
        await h.Store.UpsertAccountAsync(new JsonObject { ["status"] = MailStatus.Disconnected });

        var (config, account) = await StoredAsync(h);
        foreach (var data in new[] { config, account! })
        {
            Assert.True(MailData.Boolean(data, "confidential"));
            Assert.Null(MailData.String(data, "owner_id"));
            Assert.Null(MailData.String(data, "owner_agent_id"));
        }
    }

    [Fact]
    public async Task The_claim_makes_the_config_and_account_confidential_to_the_owner_alone()
    {
        var h = new Harness();
        await h.Store.EnsureConfigAsync();
        await h.Store.UpsertAccountAsync(new JsonObject { ["status"] = MailStatus.Disconnected });

        var policy = await h.Store.ClaimOwnerAsync(Harness.Owner, isAdmin: true);

        Assert.Equal(new MailPolicy(Harness.Owner, null), policy);
        var (config, account) = await StoredAsync(h);
        foreach (var data in new[] { config, account! })
        {
            Assert.True(MailData.Boolean(data, "confidential"));
            Assert.Equal(Harness.Owner, MailData.String(data, "owner_id"));
            Assert.Null(MailData.String(data, "owner_agent_id"));
        }
        Assert.Equal(1, h.Entities.CompareExchangeCount);
    }

    [Fact]
    public async Task Binding_then_unbinding_the_agent_leaves_both_owner_only()
    {
        var h = new Harness();
        await h.ConfigureAsync();
        var (bound, boundAccount) = await StoredAsync(h);
        Assert.Equal(Harness.Agent, MailData.String(bound, "owner_agent_id"));
        Assert.Equal(Harness.Agent, MailData.String(boundAccount, "owner_agent_id"));

        var (previous, next) = await h.Store.SetAgentAsync(null);

        Assert.Equal(Harness.Agent, previous);
        Assert.Equal(new MailPolicy(Harness.Owner, null), next);
        var (config, account) = await StoredAsync(h);
        foreach (var data in new[] { config, account! })
        {
            Assert.True(MailData.Boolean(data, "confidential"));
            Assert.Equal(Harness.Owner, MailData.String(data, "owner_id"));
            Assert.Null(MailData.String(data, "owner_agent_id"));
        }
        Assert.Equal(new MailPolicy(Harness.Owner, null), await h.Store.GetPolicyAsync());
    }

    [Fact]
    public async Task The_test_store_refuses_an_agent_without_an_owner_as_the_kernel_does()
    {
        var h = new Harness();
        var config = await h.Store.EnsureConfigAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Entities.PatchAsync(config.Entity.Id,
            new JsonObject { ["owner_agent_id"] = Harness.Agent }));
    }
}
