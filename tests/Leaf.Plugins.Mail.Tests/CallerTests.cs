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

    private static Task<bool> OwnedDiscussion(string id, string owner, string agent, CancellationToken ct)
        => Task.FromResult(id == Discussion && owner == Owner && agent == Agent);

    internal static ClaimsPrincipal User(string? sub = Owner, string authType = "Bearer")
        => new(new ClaimsIdentity(sub == null ? [] : [new Claim("sub", sub)], authType));

    internal static ClaimsPrincipal Execution(JsonObject identity, string sub = Owner)
        => new(new ClaimsIdentity([
            new Claim("sub", sub),
            new Claim("token_use", "execution"),
            new Claim("execution_identity", identity.ToJsonString()),
        ], "Bearer"));

    internal static JsonObject PrivateNovaAgent(string agent = Agent, string beneficiary = Owner, params JsonObject[] extraContext)
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

    private static async Task<MailFault> Denied(ClaimsPrincipal principal, MailPolicy? policy = null)
        => await Assert.ThrowsAsync<MailFault>(() => MailCaller.FromAsync(principal, policy ?? Policy, OwnedDiscussion, default));

    [Fact]
    public async Task Owner_session_and_bound_agent_in_owned_private_discussion_are_allowed()
    {
        var user = await MailCaller.FromAsync(User(), Policy, OwnedDiscussion, default);
        Assert.Equal(MailCallerKind.OwnerUser, user.Kind);
        user.RequireOwnerUser();

        var agent = await MailCaller.FromAsync(Execution(PrivateNovaAgent()), Policy, OwnedDiscussion, default);
        Assert.Equal(MailCallerKind.Agent, agent.Kind);
        Assert.Equal(Agent, agent.AgentId);
        Assert.Equal("owner_user_required", Assert.Throws<MailFault>(agent.RequireOwnerUser).Code);
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
            (await Denied(Execution(PrivateNovaAgent(beneficiary: "33333333-3333-3333-3333-333333333333")))).Code);
        var system = PrivateNovaAgent();
        system["beneficiary"] = new JsonObject { ["kind"] = "system", ["id"] = Owner };
        Assert.Equal("beneficiary_mismatch", (await Denied(Execution(system))).Code);
        // sub is not the owner: denied before identity parsing.
        Assert.Equal("owner_required", (await Denied(Execution(PrivateNovaAgent(), sub: "33333333-3333-3333-3333-333333333333"))).Code);
    }

    [Fact]
    public async Task Non_agent_actors_are_denied()
    {
        var service = PrivateNovaAgent();
        service["actor"] = new JsonObject { ["kind"] = "service", ["id"] = "commons-envoy" };
        Assert.Equal("owning_actor_required", (await Denied(Execution(service))).Code);

        var otherApp = PrivateNovaAgent();
        otherApp["actor"] = new JsonObject { ["kind"] = "app", ["id"] = "nova", ["entityId"] = Guid.NewGuid().ToString() };
        Assert.Equal("owning_actor_required", (await Denied(Execution(otherApp))).Code);

        var user = PrivateNovaAgent();
        user["actor"] = new JsonObject { ["kind"] = "user", ["id"] = Owner };
        Assert.Equal("owning_actor_required", (await Denied(Execution(user))).Code);
    }

    [Fact]
    public async Task Other_or_unbound_agents_are_denied()
    {
        Assert.Equal("bound_agent_required", (await Denied(Execution(PrivateNovaAgent(agent: "44444444-4444-4444-4444-444444444444")))).Code);
        Assert.Equal("bound_agent_required", (await Denied(Execution(PrivateNovaAgent()), new MailPolicy(Owner, null))).Code);
        var slugOnly = PrivateNovaAgent();
        slugOnly["actor"]!.AsObject().Remove("entityId");
        Assert.Equal("agent_entity_identity_required", (await Denied(Execution(slugOnly))).Code);
    }

    public static IEnumerable<object[]> PublicContexts() =>
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
    [MemberData(nameof(PublicContexts))]
    public async Task Discord_public_nova_and_external_executions_are_denied(JsonObject marker)
    {
        // Even the bound agent with the owner beneficiary and an owned discussion.
        Assert.Equal("public_surface_denied", (await Denied(Execution(PrivateNovaAgent(extraContext: marker)))).Code);
    }

    [Fact]
    public async Task Discord_external_conversation_session_shape_is_denied()
    {
        // Shape of a Nova external-conversation (Discord) message token: no discussion,
        // external-conversation context, origin on the external-conversations route.
        var identity = PrivateNovaAgent();
        identity["context"] = new JsonArray
        {
            new JsonObject { ["kind"] = "external-conversation", ["id"] = "discord:1234:5678" },
            new JsonObject { ["kind"] = "external-conversation-generation", ["id"] = "1" },
            new JsonObject { ["kind"] = "session", ["id"] = "s1" },
            new JsonObject { ["kind"] = "origin-entrypoint", ["name"] = "MESSAGE", ["route"] = "/api/apps/nova/external-conversations/{id}/messages" },
        };
        Assert.Equal("public_surface_denied", (await Denied(Execution(identity))).Code);
    }

    [Theory]
    [InlineData("/api/apps/nova/delegate")]
    [InlineData("automation:abc:nova-session")]
    [InlineData("presence:auto-resume")]
    [InlineData(null)]
    public async Task Agent_executions_not_originating_from_a_private_nova_discussion_are_denied(string? route)
    {
        // A public session can mint fresh provenance through /delegate with a discussion id
        // and no external marker; the signed origin route still identifies it.
        var identity = PrivateNovaAgent();
        var context = identity["context"]!.AsArray();
        var origin = context.Single(c => c!["kind"]!.GetValue<string>() == "origin-entrypoint")!.AsObject();
        if (route == null) context.Remove(origin); else origin["route"] = route;
        Assert.Equal("private_discussion_origin_required", (await Denied(Execution(identity))).Code);
    }

    [Fact]
    public async Task Agent_execution_without_or_with_an_unowned_discussion_is_denied()
    {
        var none = PrivateNovaAgent();
        var context = none["context"]!.AsArray();
        context.Remove(context.Single(c => c!["kind"]!.GetValue<string>() == "discussion"));
        Assert.Equal("private_discussion_required", (await Denied(Execution(none))).Code);

        var foreign = PrivateNovaAgent(extraContext: new JsonObject { ["kind"] = "discussion", ["id"] = "someone-elses" });
        Assert.Equal("private_discussion_required", (await Denied(Execution(foreign))).Code);
    }

    [Fact]
    public async Task Malformed_or_unversioned_execution_identity_is_denied()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", Owner), new Claim("token_use", "execution"), new Claim("execution_identity", "{not json")], "Bearer"));
        Assert.Equal("execution_identity_invalid", (await Denied(principal)).Code);
        var v2 = PrivateNovaAgent();
        v2["schemaVersion"] = 2;
        Assert.Equal("execution_identity_version", (await Denied(Execution(v2))).Code);
        var refresh = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Owner), new Claim("token_use", "refresh")], "Bearer"));
        Assert.Equal("execution_identity_invalid", (await Denied(refresh)).Code);
    }

    [Fact]
    public async Task Execution_tokens_are_denied_when_no_owner_is_configured()
        => Assert.Equal("owner_required", (await Denied(Execution(PrivateNovaAgent()), new MailPolicy(null, null))).Code);

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
        var caller = await MailCaller.FromAsync(Execution(identity), Policy, OwnedDiscussion, default);
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
