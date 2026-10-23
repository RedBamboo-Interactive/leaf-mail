using System.Security.Claims;
using System.Text.Json.Nodes;
using Leaf.Plugins.Mail;
using Xunit;

namespace Leaf.Plugins.Mail.Tests;

public sealed class CallerTests
{
    private const string Owner = Harness.Owner;
    private const string Agent = Harness.Agent;
    private const string Discussion = "disc-private-1";
    private static readonly MailPolicy Policy = new(Owner, Agent);

    internal static ClaimsPrincipal User(string? sub = Owner, string authType = "Bearer")
        => new(new ClaimsIdentity(sub == null ? [] : [new Claim("sub", sub)], authType));

    internal static ClaimsPrincipal Execution(JsonObject identity, string sub = Owner)
        => new(new ClaimsIdentity([
            new Claim("sub", sub),
            new Claim("token_use", "execution"),
            new Claim("execution_identity", identity.ToJsonString()),
        ], "Bearer"));

    internal static JsonObject NovaAgent(string agent = Agent, string beneficiary = Owner, params JsonObject[] extraContext)
    {
        var context = new JsonArray
        {
            new JsonObject { ["kind"] = "discussion", ["id"] = Discussion },
            new JsonObject { ["kind"] = "session", ["id"] = "s1" },
            new JsonObject { ["kind"] = "origin-entrypoint", ["name"] = "POST", ["route"] = $"/api/apps/nova/discussions/{Discussion}/message" },
            new JsonObject { ["kind"] = "ai-session-provider", ["id"] = "claude" },
        };
        foreach (var item in extraContext) context.Add(item);
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["executionId"] = Guid.NewGuid().ToString(),
            ["parentExecutionId"] = Guid.NewGuid().ToString(),
            ["app"] = new JsonObject { ["id"] = "nova", ["entityId"] = Guid.NewGuid().ToString() },
            ["actor"] = new JsonObject { ["kind"] = "agent", ["id"] = "nova", ["entityId"] = agent },
            ["beneficiary"] = new JsonObject { ["kind"] = "user", ["id"] = beneficiary },
            ["context"] = context,
        };
    }

    private static Task<MailFault> Denied(ClaimsPrincipal principal, MailPolicy? policy = null)
        => Task.FromResult(Assert.Throws<MailFault>(() => MailCaller.From(principal, policy ?? Policy)));

    private static MailCaller Allowed(JsonObject identity)
    {
        var caller = MailCaller.From(Execution(identity), Policy);
        Assert.Equal(MailCallerKind.Agent, caller.Kind);
        Assert.Equal(Agent, caller.AgentId);
        Assert.Equal("owner_user_required", Assert.Throws<MailFault>(caller.RequireOwnerUser).Code);
        return caller;
    }

    /// <summary>Shape of a Nova external-conversation (Discord or Commons) message token.</summary>
    internal static JsonObject DiscordNovaAgent(string agent = Agent, string beneficiary = Owner)
    {
        var identity = NovaAgent(agent, beneficiary);
        identity["context"] = new JsonArray
        {
            new JsonObject { ["kind"] = "external-conversation", ["id"] = "discord:1234:5678" },
            new JsonObject { ["kind"] = "external-conversation-generation", ["id"] = "1" },
            new JsonObject { ["kind"] = "session", ["id"] = "s1" },
            new JsonObject { ["kind"] = "origin-entrypoint", ["name"] = "MESSAGE", ["route"] = "/api/apps/nova/external-conversations/{id}/messages" },
        };
        return identity;
    }

    /// <summary>Shape of a flow/automation node token: app redleaf, actor the agent, beneficiary the user.</summary>
    internal static JsonObject AutomationAgent(string agent = Agent, string beneficiary = Owner)
    {
        var identity = NovaAgent(agent, beneficiary);
        identity["app"] = new JsonObject { ["id"] = "redleaf", ["name"] = "RedLeaf" };
        identity["context"] = new JsonArray
        {
            new JsonObject { ["kind"] = "flow", ["id"] = Guid.NewGuid().ToString() },
            new JsonObject { ["kind"] = "execution", ["id"] = "x1" },
            new JsonObject { ["kind"] = "node", ["id"] = "n1" },
            new JsonObject { ["kind"] = "origin-entrypoint", ["route"] = "automation:abc:nova-session" },
        };
        return identity;
    }

    [Fact]
    public void Owner_session_and_bound_agent_in_a_private_discussion_are_allowed()
    {
        var user = MailCaller.From(User(), Policy);
        Assert.Equal(MailCallerKind.OwnerUser, user.Kind);
        user.RequireOwnerUser();

        Assert.Equal([Discussion], Allowed(NovaAgent()).DiscussionIds);
    }

    [Fact]
    public async Task LocalDefault_and_anonymous_are_denied()
    {
        Assert.Equal("explicit_authentication_required", (await Denied(User(authType: "LocalDefault"))).Code);
        Assert.Equal("explicit_authentication_required", (await Denied(new ClaimsPrincipal(new ClaimsIdentity()))).Code);
        Assert.Equal("explicit_authentication_required", (await Denied(User(sub: null))).Code);
        Assert.Equal("explicit_authentication_required", (await Denied(User(sub: "local-user"))).Code);
        Assert.Equal("explicit_authentication_required", (await Denied(User(sub: Guid.Empty.ToString()))).Code);
    }

    [Fact]
    public async Task Other_users_are_denied()
        => Assert.Equal("owner_required", (await Denied(User("33333333-3333-3333-3333-333333333333"))).Code);

    [Fact]
    public async Task Wrong_beneficiary_is_denied()
    {
        Assert.Equal("beneficiary_mismatch",
            (await Denied(Execution(NovaAgent(beneficiary: "33333333-3333-3333-3333-333333333333")))).Code);
        var system = NovaAgent();
        system["beneficiary"] = new JsonObject { ["kind"] = "system", ["id"] = Owner };
        Assert.Equal("beneficiary_mismatch", (await Denied(Execution(system))).Code);
        // sub is not the owner: denied before identity parsing.
        Assert.Equal("owner_required", (await Denied(Execution(NovaAgent(), sub: "33333333-3333-3333-3333-333333333333"))).Code);
    }

    [Fact]
    public async Task Non_agent_actors_are_denied()
    {
        var service = NovaAgent();
        service["actor"] = new JsonObject { ["kind"] = "service", ["id"] = "commons-envoy" };
        Assert.Equal("owning_actor_required", (await Denied(Execution(service))).Code);

        var otherApp = NovaAgent();
        otherApp["actor"] = new JsonObject { ["kind"] = "app", ["id"] = "nova", ["entityId"] = Guid.NewGuid().ToString() };
        Assert.Equal("owning_actor_required", (await Denied(Execution(otherApp))).Code);

        var user = NovaAgent();
        user["actor"] = new JsonObject { ["kind"] = "user", ["id"] = Owner };
        Assert.Equal("owning_actor_required", (await Denied(Execution(user))).Code);
    }

    [Fact]
    public async Task Other_or_unbound_agents_are_denied()
    {
        Assert.Equal("bound_agent_required", (await Denied(Execution(NovaAgent(agent: "44444444-4444-4444-4444-444444444444")))).Code);
        Assert.Equal("bound_agent_required", (await Denied(Execution(NovaAgent()), new MailPolicy(Owner, null))).Code);
        var slugOnly = NovaAgent();
        slugOnly["actor"]!.AsObject().Remove("entityId");
        Assert.Equal("agent_entity_identity_required", (await Denied(Execution(slugOnly))).Code);
    }

    public static IEnumerable<object[]> ExternalContexts() =>
    [
        [new JsonObject { ["kind"] = "external-conversation", ["id"] = "discord:1234:5678" }],
        [new JsonObject { ["kind"] = "external-conversation", ["id"] = "leaf-commons:abcd" }],
        [new JsonObject { ["kind"] = "external-conversation-generation", ["id"] = "3" }],
        [new JsonObject { ["kind"] = "discord-conversation", ["id"] = "5678" }],
        [new JsonObject { ["kind"] = "public-discord-conversation", ["id"] = "thread-1" }],
        [new JsonObject { ["kind"] = "guild_channel", ["id"] = "5678" }],
        [new JsonObject { ["kind"] = "dm", ["id"] = "5678" }],
        [new JsonObject { ["kind"] = "external-application", ["id"] = "leaf-commons" }],
        [new JsonObject { ["kind"] = "session", ["id"] = "discord:1:2" }],
    ];

    [Theory]
    [MemberData(nameof(ExternalContexts))]
    public void Bound_agent_executions_from_discord_commons_and_external_surfaces_are_allowed(JsonObject marker)
        => Allowed(NovaAgent(extraContext: marker));

    [Fact]
    public void Discord_external_conversation_session_is_allowed()
    {
        // No discussion, external-conversation context, origin on the external-conversations route.
        Assert.Empty(Allowed(DiscordNovaAgent()).DiscussionIds);
    }

    [Theory]
    [InlineData("/api/apps/nova/delegate")]
    [InlineData("automation:abc:nova-session")]
    [InlineData("presence:auto-resume")]
    [InlineData(null)]
    public void Delegated_automation_and_heartbeat_executions_of_the_bound_agent_are_allowed(string? route)
    {
        var identity = NovaAgent();
        var context = identity["context"]!.AsArray();
        var origin = context.Single(c => c!["kind"]!.GetValue<string>() == "origin-entrypoint")!.AsObject();
        if (route == null) context.Remove(origin); else origin["route"] = route;
        Allowed(identity);
    }

    [Fact]
    public void Automation_runs_of_the_bound_agent_are_allowed_whatever_the_app()
        => Allowed(AutomationAgent());

    [Fact]
    public void Agent_executions_without_or_with_any_discussion_are_allowed_and_recorded()
    {
        var none = NovaAgent();
        var context = none["context"]!.AsArray();
        context.Remove(context.Single(c => c!["kind"]!.GetValue<string>() == "discussion"));
        Assert.Empty(Allowed(none).DiscussionIds);

        var more = NovaAgent(extraContext: new JsonObject { ["kind"] = "discussion", ["id"] = "another-discussion" });
        Assert.Equal([Discussion, "another-discussion"], Allowed(more).DiscussionIds);
    }

    [Fact]
    public async Task Every_surface_still_requires_the_bound_agent_and_the_owner_as_beneficiary()
    {
        const string stranger = "33333333-3333-3333-3333-333333333333";
        const string otherAgent = "44444444-4444-4444-4444-444444444444";
        foreach (var shape in new Func<string, string, JsonObject>[] { DiscordNovaAgent, AutomationAgent, (a, b) => NovaAgent(a, b) })
        {
            Assert.Equal("bound_agent_required", (await Denied(Execution(shape(otherAgent, Owner)))).Code);
            Assert.Equal("beneficiary_mismatch", (await Denied(Execution(shape(Agent, stranger)))).Code);
            Assert.Equal("owner_required", (await Denied(Execution(shape(Agent, stranger), sub: stranger))).Code);
            Assert.Equal("bound_agent_required", (await Denied(Execution(shape(Agent, Owner)), new MailPolicy(Owner, null))).Code);
        }
    }

    [Fact]
    public async Task Plugin_minted_system_tokens_cannot_pass_as_the_bound_agent()
    {
        // IExecutionTokens.IssueSystem: sub "system", beneficiary system, actor without an entity id.
        var system = DiscordNovaAgent();
        system["beneficiary"] = new JsonObject { ["kind"] = "system", ["reason"] = "relay" };
        system["actor"] = new JsonObject { ["kind"] = "agent", ["id"] = Agent, ["name"] = "Nova" };
        Assert.Equal("explicit_authentication_required", (await Denied(Execution(system, sub: "system"))).Code);
        Assert.Equal("beneficiary_mismatch", (await Denied(Execution(system))).Code);
        system["beneficiary"] = new JsonObject { ["kind"] = "user", ["id"] = Owner };
        Assert.Equal("agent_entity_identity_required", (await Denied(Execution(system))).Code);
    }

    [Fact]
    public async Task Malformed_or_unversioned_execution_identity_is_denied()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", Owner), new Claim("token_use", "execution"), new Claim("execution_identity", "{not json")], "Bearer"));
        Assert.Equal("execution_identity_invalid", (await Denied(principal)).Code);
        var v2 = NovaAgent();
        v2["schemaVersion"] = 2;
        Assert.Equal("execution_identity_version", (await Denied(Execution(v2))).Code);
        var refresh = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Owner), new Claim("token_use", "refresh")], "Bearer"));
        Assert.Equal("execution_identity_invalid", (await Denied(refresh)).Code);
    }

    [Fact]
    public async Task Execution_tokens_are_denied_when_no_owner_is_configured()
        => Assert.Equal("owner_required", (await Denied(Execution(NovaAgent()), new MailPolicy(null, null))).Code);

    [Fact]
    public async Task Mail_browser_app_root_execution_is_allowed_for_reads_only()
    {
        var appId = Guid.NewGuid().ToString();
        var identity = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["executionId"] = "e1",
            ["app"] = new JsonObject { ["id"] = "mail", ["entityId"] = appId },
            ["actor"] = new JsonObject { ["kind"] = "app", ["id"] = "mail", ["entityId"] = appId },
            ["beneficiary"] = new JsonObject { ["kind"] = "user", ["id"] = Owner },
            ["context"] = new JsonArray { new JsonObject { ["kind"] = "browser", ["route"] = "/apps/mail" } },
        };
        var caller = MailCaller.From(Execution(identity), Policy);
        Assert.Equal(MailCallerKind.OwnerApp, caller.Kind);
        Assert.Throws<MailFault>(caller.RequireOwnerUser);

        identity["parentExecutionId"] = "derived";
        Assert.Equal("owning_actor_required", (await Denied(Execution(identity))).Code);
        identity.Remove("parentExecutionId");
        identity["app"]!["id"] = "nova";
        identity["actor"]!["id"] = "nova";
        identity["context"] = new JsonArray { new JsonObject { ["kind"] = "browser", ["route"] = "/apps/nova" } };
        Assert.Equal("owning_actor_required", (await Denied(Execution(identity))).Code);
    }
}
