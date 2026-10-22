using System.Security.Claims;
using System.Text.Json;

namespace Leaf.Plugins.Mail;

public sealed class MailFault(string code, int status) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public enum MailCallerKind { OwnerUser, Agent, OwnerApp }

/// <summary>Owner and bound agent as configured in <c>mail-config</c>; both are exact GUIDs.</summary>
public sealed record MailPolicy(string? OwnerUserId, string? AgentId);

/// <summary>Verifies a Nova discussion id belongs to the owner and bound agent.</summary>
public delegate Task<bool> DiscussionOwnershipCheck(string discussionId, string ownerUserId, string agentId, CancellationToken ct);

/// <summary>
/// Strict caller identity, read from host-validated claims only (never a decoded bearer,
/// never loopback). Modeled on Body's <c>BodyCaller</c>, with an explicit public/Discord
/// exclusion:
/// <list type="bullet">
/// <item>LocalDefault and anonymous principals are rejected; <c>sub</c> must be a GUID.</item>
/// <item>A plain user session must be the configured owner.</item>
/// <item>An execution token must name the owner as beneficiary and either the bound agent
/// (running inside an owned private Nova discussion) or Mail's own browser app.</item>
/// <item>Any external-conversation / Discord / Commons marker denies, and agent executions
/// whose signed origin entrypoint is not a Nova discussion route (e.g. <c>/delegate</c>,
/// automations, external conversations) are denied, so provenance minted on behalf of
/// a public session cannot shed the marker by delegation.</item>
/// </list>
/// </summary>
public sealed record MailCaller(MailCallerKind Kind, string UserId, string? AgentId, string? ExecutionId)
{
    public const string NovaAppId = "nova";
    public const string MailAppId = "mail";
    public const string NovaDiscussionRoutePrefix = "/api/apps/nova/discussions/";

    private static readonly HashSet<string> PublicContextKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "external-conversation", "external-conversation-generation", "external-generation",
        "external-application", "public-discord-conversation", "discord-conversation",
        "discord-generation", "dm", "guild_channel", "thread", "commons", "commons-thread",
    };

    /// <summary>
    /// Identity gate shared by every route: explicit authentication, GUID subject, and a
    /// well-formed execution identity when one is present. Settings routes then call
    /// <see cref="RequireOwnerUser"/>; read routes call <see cref="AuthorizeReadAsync"/>.
    /// </summary>
    public static async Task<MailCaller> FromAsync(
        ClaimsPrincipal principal, MailPolicy policy, DiscussionOwnershipCheck discussions, CancellationToken ct)
    {
        var identity = principal.Identity;
        var subject = principal.FindFirstValue("sub");
        if (identity?.IsAuthenticated != true
            || identity.AuthenticationType == "LocalDefault"
            || !Guid.TryParse(subject, out var subjectGuid) || subjectGuid == Guid.Empty)
            throw new MailFault("explicit_authentication_required", 401);

        var use = principal.FindFirstValue("token_use");
        if (use is null)
        {
            if (policy.OwnerUserId is null) return new(MailCallerKind.OwnerUser, subject!, null, null);
            if (!SameId(subject, policy.OwnerUserId)) throw new MailFault("owner_required", 403);
            return new(MailCallerKind.OwnerUser, subject!, null, null);
        }
        if (use != "execution") throw new MailFault("execution_identity_invalid", 401);

        // Execution callers act only for a configured owner and bound agent.
        if (policy.OwnerUserId is null || !SameId(subject, policy.OwnerUserId))
            throw new MailFault("owner_required", 403);

        ParsedExecution execution;
        try { execution = Parse(principal.FindFirstValue("execution_identity")); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new MailFault("execution_identity_invalid", 403); }

        if (execution.SchemaVersion != 1) throw new MailFault("execution_identity_version", 403);
        if (execution.BeneficiaryKind != "user" || !SameId(execution.BeneficiaryId, subject))
            throw new MailFault("beneficiary_mismatch", 403);
        if (execution.IsPublicOrExternal) throw new MailFault("public_surface_denied", 403);

        if (execution.ActorKind == "agent")
        {
            if (execution.AppId != NovaAppId) throw new MailFault("owning_actor_required", 403);
            var agent = execution.ActorEntityId;
            if (!Guid.TryParse(agent, out _)) throw new MailFault("agent_entity_identity_required", 403);
            if (policy.AgentId is null || !SameId(agent, policy.AgentId)) throw new MailFault("bound_agent_required", 403);
            if (execution.OriginRoutes.Count == 0
                || execution.OriginRoutes.Any(route => route is null
                    || !route.StartsWith(NovaDiscussionRoutePrefix, StringComparison.Ordinal)))
                throw new MailFault("private_discussion_origin_required", 403);
            if (execution.DiscussionIds.Count == 0) throw new MailFault("private_discussion_required", 403);
            foreach (var discussion in execution.DiscussionIds)
                if (!await discussions(discussion, policy.OwnerUserId, policy.AgentId, ct))
                    throw new MailFault("private_discussion_required", 403);
            return new(MailCallerKind.Agent, subject!, agent, execution.ExecutionId);
        }

        // Mail's own browser app (same shape Body accepts for its app): root execution,
        // browser context on the app's route, actor == app.
        if (execution.ActorKind == "app" && execution.AppId == MailAppId && execution.ActorId == MailAppId
            && execution.ParentExecutionId is null
            && (execution.AppEntityId is null || execution.AppEntityId == execution.ActorEntityId)
            && execution.BrowserRoutes.Any(route => route == "/apps/mail" || route.StartsWith("/apps/mail/", StringComparison.Ordinal)))
            return new(MailCallerKind.OwnerApp, subject!, null, execution.ExecutionId);

        throw new MailFault("owning_actor_required", 403);
    }

    /// <summary>Settings, OAuth and disconnect: a plain owner session only, never an execution token.</summary>
    public void RequireOwnerUser()
    {
        if (Kind != MailCallerKind.OwnerUser) throw new MailFault("owner_user_required", 403);
    }

    private static bool SameId(string? a, string? b)
        => a is not null && b is not null && Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x == y;

    internal sealed record ParsedExecution(
        int SchemaVersion,
        string? ExecutionId,
        string? ParentExecutionId,
        string? AppId,
        string? AppEntityId,
        string? ActorKind,
        string? ActorId,
        string? ActorEntityId,
        string? BeneficiaryKind,
        string? BeneficiaryId,
        IReadOnlyList<string> DiscussionIds,
        IReadOnlyList<string?> OriginRoutes,
        IReadOnlyList<string> BrowserRoutes,
        bool IsPublicOrExternal);

    internal static ParsedExecution Parse(string? raw)
    {
        using var json = JsonDocument.Parse(raw ?? "null");
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("execution identity missing");
        var app = root.GetProperty("app");
        var actor = root.GetProperty("actor");
        var beneficiary = root.GetProperty("beneficiary");
        var discussions = new List<string>();
        var origins = new List<string?>();
        var browserRoutes = new List<string>();
        var isPublic = false;
        foreach (var context in root.GetProperty("context").EnumerateArray())
        {
            var kind = Str(context, "kind") ?? throw new InvalidOperationException("context kind missing");
            var id = Str(context, "id");
            var route = Str(context, "route");
            if (PublicContextKinds.Contains(kind)
                || kind.StartsWith("discord", StringComparison.OrdinalIgnoreCase)
                || kind.StartsWith("external", StringComparison.OrdinalIgnoreCase)
                || kind.Contains("public", StringComparison.OrdinalIgnoreCase)
                || id?.StartsWith("discord:", StringComparison.OrdinalIgnoreCase) == true
                || id?.StartsWith("leaf-commons:", StringComparison.OrdinalIgnoreCase) == true
                || route?.Contains("/external-conversations", StringComparison.OrdinalIgnoreCase) == true)
                isPublic = true;
            if (kind == "discussion")
            {
                if (id is not { Length: > 0 and <= 128 }) throw new InvalidOperationException("discussion id invalid");
                discussions.Add(id);
            }
            if (kind == "origin-entrypoint") origins.Add(route);
            if (kind == "browser" && route != null) browserRoutes.Add(route);
        }
        var parent = root.TryGetProperty("parentExecutionId", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;
        return new ParsedExecution(
            root.GetProperty("schemaVersion").GetInt32(),
            Str(root, "executionId"),
            parent,
            Str(app, "id"),
            Str(app, "entityId"),
            Str(actor, "kind"),
            Str(actor, "id"),
            Str(actor, "entityId"),
            Str(beneficiary, "kind"),
            Str(beneficiary, "id"),
            discussions,
            origins,
            browserRoutes,
            isPublic);
    }

    private static string? Str(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
