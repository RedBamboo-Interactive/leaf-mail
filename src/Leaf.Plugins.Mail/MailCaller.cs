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

/// <summary>
/// Caller identity, read from host-validated claims only (never a decoded bearer, never
/// loopback). Modeled on Body's <c>BodyCaller</c>:
/// <list type="bullet">
/// <item>LocalDefault and anonymous principals are rejected; <c>sub</c> must be a GUID.</item>
/// <item>A plain user session must be the configured owner.</item>
/// <item>An execution token must name the owner as beneficiary and either the bound agent or
/// Mail's own browser app as actor.</item>
/// </list>
/// Any execution of the bound agent on behalf of the owner may read: private discussions,
/// Discord and Commons sessions, automations, delegated sessions. There is no per-surface
/// segregation; the protection is the Sentinel pass on every body, attachment and header,
/// whose verdict is returned to the calling session. Agent-actor, user-beneficiary tokens are
/// minted only by the kernel: plugin-minted tokens carry a system beneficiary and no actor
/// entity id, and browser-app tokens carry the app as actor.
/// </summary>
public sealed record MailCaller(MailCallerKind Kind, string UserId, string? AgentId, string? ExecutionId)
{
    /// <summary>Host-issued "admin" role on a plain user session (required for the first owner claim).</summary>
    public bool IsAdmin { get; init; }

    /// <summary>The discussion(s) named in the signed execution context (audit provenance only).</summary>
    public IReadOnlyList<string> DiscussionIds { get; init; } = [];

    public const string MailAppId = "mail";

    /// <summary>
    /// Identity gate shared by every route: explicit authentication, GUID subject, and a
    /// well-formed execution identity when one is present. Settings routes then call
    /// <see cref="RequireOwnerUser"/>.
    /// </summary>
    public static MailCaller From(ClaimsPrincipal principal, MailPolicy policy)
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
            if (policy.OwnerUserId is not null && !SameId(subject, policy.OwnerUserId))
                throw new MailFault("owner_required", 403);
            return new(MailCallerKind.OwnerUser, subject!, null, null) { IsAdmin = Roles(principal).Contains("admin") };
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
        // The bound agent, from whichever surface or app it runs in (Nova discussion, Discord,
        // Commons, automation, delegation).
        if (execution.ActorKind == "agent")
        {
            var agent = execution.ActorEntityId;
            if (!Guid.TryParse(agent, out _)) throw new MailFault("agent_entity_identity_required", 403);
            if (policy.AgentId is null || !SameId(agent, policy.AgentId)) throw new MailFault("bound_agent_required", 403);
            return new(MailCallerKind.Agent, subject!, agent, execution.ExecutionId) { DiscussionIds = execution.DiscussionIds };
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

    /// <summary>Roles as the host issues them: one JSON-array claim or repeated claims.</summary>
    internal static IReadOnlyList<string> Roles(ClaimsPrincipal principal)
    {
        var values = principal.FindAll("roles").Select(c => c.Value).ToArray();
        if (values.Length == 1 && values[0].StartsWith('['))
        {
            try { return JsonSerializer.Deserialize<string[]>(values[0]) ?? []; }
            catch (JsonException) { return []; }
        }
        return values;
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
        IReadOnlyList<string> BrowserRoutes);

    internal static ParsedExecution Parse(string? raw)
    {
        using var json = JsonDocument.Parse(raw ?? "null");
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("execution identity missing");
        var app = root.GetProperty("app");
        var actor = root.GetProperty("actor");
        var beneficiary = root.GetProperty("beneficiary");
        var discussions = new List<string>();
        var browserRoutes = new List<string>();
        foreach (var context in root.GetProperty("context").EnumerateArray())
        {
            var kind = Str(context, "kind") ?? throw new InvalidOperationException("context kind missing");
            var id = Str(context, "id");
            var route = Str(context, "route");
            if (kind == "discussion")
            {
                if (id is not { Length: > 0 and <= 128 }) throw new InvalidOperationException("discussion id invalid");
                discussions.Add(id);
            }
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
            browserRoutes);
    }

    private static string? Str(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
