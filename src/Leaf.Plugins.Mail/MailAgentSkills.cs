using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Mail;

public sealed record SkillAssignment(string AgentId, string Status)
{
    public JsonObject ToJson() => new() { ["agentId"] = AgentId, ["status"] = Status };
}

public static class SkillAssignmentStatus
{
    public const string Added = "added";
    public const string AlreadyPresent = "already_present";
    public const string Removed = "removed";
    public const string NotPresent = "not_present";
    public const string AgentMissing = "agent_missing";
    public const string SkillMissing = "skill_missing";
    /// <summary>The agent is owned by another plugin; the kernel refuses Mail's write.</summary>
    public const string ManualRequired = "manual_required";
    public const string Failed = "failed";
}

/// <summary>
/// Keeps the bound agent's <c>skills</c> in step with the binding: binding adds Mail's own
/// <c>mail</c> agent-skill, unbinding or switching removes it from the previous agent. Only
/// references to that one skill (by id or slug) are ever added or removed; other skills are
/// untouched. Runs only on the owner's verified settings request, for an agent the owner may
/// see, through the kernel's plugin-scoped entity store (validated, versioned, and refused for
/// agents another plugin owns). The write is a shallow patch of <c>skills</c> alone, so the
/// agent's ownership stamp is never changed.
/// </summary>
public sealed class MailAgentSkills(IEntityStore entities, ILogger<MailAgentSkills>? logger = null)
{
    public const string SkillSlug = "mail";
    public const string SkillType = "agent-skill";

    public async Task<IReadOnlyList<SkillAssignment>> SyncAsync(
        string? previousAgentId, string? nextAgentId, string ownerUserId, CancellationToken ct)
    {
        var results = new List<SkillAssignment>();
        var skill = await ResolveSkillAsync(ct);
        if (skill == null)
        {
            foreach (var agent in new[] { previousAgentId, nextAgentId }.Where(a => a != null).Distinct())
                results.Add(new(agent!, SkillAssignmentStatus.SkillMissing));
            return results;
        }
        if (previousAgentId != null && !SameId(previousAgentId, nextAgentId))
            results.Add(await ChangeAsync(previousAgentId, skill, ownerUserId, add: false, ct));
        if (nextAgentId != null)
            results.Add(await ChangeAsync(nextAgentId, skill, ownerUserId, add: true, ct));
        return results;
    }

    /// <summary>Mail's own seeded skill: slug <c>mail</c> and owned by the Mail plugin entity.</summary>
    private async Task<LeafEntity?> ResolveSkillAsync(CancellationToken ct)
    {
        var skill = await entities.GetBySlugAsync(SkillType, SkillSlug, ct);
        var plugin = await entities.GetBySlugAsync("plugin", MailPlugin.PluginId, ct);
        if (skill == null || plugin == null) return null;
        return SameId(MailData.String(skill.Data, "owner_plugin"), plugin.Id.ToString()) ? skill : null;
    }

    private async Task<SkillAssignment> ChangeAsync(string agentId, LeafEntity skill, string ownerUserId, bool add, CancellationToken ct)
    {
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var agent = Guid.TryParse(agentId, out var id) ? await entities.GetAsync(id, ct) : null;
                if (agent?.TypeSlug != "agent" || !OwnerMaySee(agent, ownerUserId))
                    return new(agentId, SkillAssignmentStatus.AgentMissing);
                var current = References(agent.Data);
                var matches = current.Where(r => IsMailSkill(r, skill)).ToArray();
                if (add && matches.Length > 0) return new(agentId, SkillAssignmentStatus.AlreadyPresent);
                if (!add && matches.Length == 0) return new(agentId, SkillAssignmentStatus.NotPresent);
                var next = add
                    ? current.Append(skill.Id.ToString()).ToArray()
                    : current.Where(r => !IsMailSkill(r, skill)).ToArray();

                // Narrow the window for a concurrent edit of this agent: only write if it is
                // unchanged since it was read, otherwise recompute from the fresh copy.
                var check = await entities.GetAsync(agent.Id, ct);
                if (check?.UpdatedAt != agent.UpdatedAt) continue;
                var patch = new JsonObject { ["skills"] = new JsonArray(next.Select(r => (JsonNode)JsonValue.Create(r)).ToArray()) };
                await entities.PatchAsync(agent.Id, patch, ct: ct);
                logger?.LogInformation("Mail skill {Change} for bound agent {Agent}", add ? "added" : "removed", agent.Id);
                return new(agentId, add ? SkillAssignmentStatus.Added : SkillAssignmentStatus.Removed);
            }
            return new(agentId, SkillAssignmentStatus.Failed);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("owned by another plugin", StringComparison.Ordinal))
        {
            return new(agentId, SkillAssignmentStatus.ManualRequired);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning("Mail skill assignment for agent {Agent} failed ({Type})", agentId, ex.GetType().Name);
            return new(agentId, SkillAssignmentStatus.Failed);
        }
    }

    private static string[] References(JsonObject data)
        => data["skills"] is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToArray()
            : [];

    private static bool IsMailSkill(string reference, LeafEntity skill)
        => SameId(reference, skill.Id.ToString()) || string.Equals(reference, skill.Slug, StringComparison.OrdinalIgnoreCase);

    /// <summary>The same visibility rule as GET /agents and the binding validation.</summary>
    private static bool OwnerMaySee(LeafEntity agent, string ownerUserId)
        => !MailData.Boolean(agent.Data, "confidential") || SameId(MailData.String(agent.Data, "owner_id"), ownerUserId);

    private static bool SameId(string? a, string? b)
        => Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x == y;
}
