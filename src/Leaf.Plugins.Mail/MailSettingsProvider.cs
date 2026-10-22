using System.Text.Json.Nodes;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Mail;

/// <summary>
/// Kernel settings descriptor (<c>GET /api/extensions/mail/settings</c>). The kernel gates
/// that route on the admin role only, and a LocalDefault localhost caller holds that role,
/// so this descriptor discloses no mailbox address or error text and accepts no writes.
/// Every change goes through Mail's own owner-only routes, which apply the strict caller
/// policy.
/// </summary>
public sealed class MailSettingsProvider(MailStore store) : IPluginSettingsProvider
{
    public string PluginId => MailPlugin.PluginId;

    public async Task<JsonObject> DescribeAsync(string? userId, CancellationToken ct = default)
    {
        var config = await store.GetConfigAsync(ct);
        var account = await store.GetAccountAsync(ct);
        return new JsonObject
        {
            ["id"] = PluginId,
            ["status"] = "enabled",
            ["sourceOfTruth"] = "entities",
            ["discover"] = MailDiscovery.Base + "/discover",
            ["readOnly"] = true,
            ["sections"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "mail",
                    ["label"] = "Mail",
                    ["kind"] = "plugin-panel",
                    ["summary"] = new JsonObject
                    {
                        ["clientConfigured"] = config.ClientConfigured,
                        ["connection"] = account?.Status ?? MailStatus.Disconnected,
                        ["agentBound"] = config.AgentId != null && config.BindingIntact,
                    },
                    ["endpoints"] = new JsonObject
                    {
                        ["status"] = MailDiscovery.Base + "/status",
                        ["client"] = MailDiscovery.Base + "/client",
                        ["config"] = MailDiscovery.Base + "/config",
                        ["oauthStart"] = MailDiscovery.Base + "/oauth/start",
                        ["oauthComplete"] = MailDiscovery.Base + "/oauth/complete",
                        ["disconnect"] = MailDiscovery.Base + "/disconnect",
                    },
                    ["secretPolicy"] = new JsonObject
                    {
                        ["fieldType"] = "secret",
                        ["readable"] = false,
                        ["fields"] = new JsonArray("client_id", "client_secret", "refresh_token"),
                    },
                },
            },
        };
    }

    public Task<JsonObject> WriteAsync(string? userId, string section, JsonObject values, bool validateOnly, CancellationToken ct = default)
        => throw new ArgumentException(
            "Mail settings are written through the owner-only Mail API (PUT /api/extensions/mail/config, PUT /client, POST /oauth/start).");
}
