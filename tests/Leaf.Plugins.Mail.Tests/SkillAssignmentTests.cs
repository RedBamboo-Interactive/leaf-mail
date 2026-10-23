using System.Text.Json.Nodes;
using Leaf.Plugins.Mail;
using Leaf.Sdk;
using Xunit;

namespace Leaf.Plugins.Mail.Tests;

public sealed class SkillAssignmentTests
{
    private const string OtherSkill = "d9dc6cfa-06ea-4e12-896f-416e2fa2a3fe";
    private const string AgentA = "a1000000-0000-0000-0000-000000000001";
    private const string AgentB = "b2000000-0000-0000-0000-000000000002";

    private sealed record World(MemoryEntities Entities, MailAgentSkills Skills, LeafEntity Skill, LeafEntity Plugin);

    private static World Create(bool seedSkill = true)
    {
        var entities = new MemoryEntities();
        var plugin = entities.Add("plugin", "mail", new JsonObject());
        entities.PluginEntityId = plugin.Id;
        var skill = seedSkill
            ? entities.AddWithId(Guid.Parse("949dfba3-0e3d-595d-8bf1-e1c0b64c11d8"), "agent-skill", "mail",
                new JsonObject { ["owner_plugin"] = plugin.Id.ToString() })
            : entities.Add("agent-skill", "other", new JsonObject());
        entities.AddWithId(Guid.Parse(AgentA), "agent", "nova", new JsonObject { ["skills"] = new JsonArray(OtherSkill) });
        entities.AddWithId(Guid.Parse(AgentB), "agent", "muse", new JsonObject());
        return new World(entities, new MailAgentSkills(entities), skill, plugin);
    }

    private static async Task<string[]> SkillsOf(MemoryEntities entities, string agentId)
        => ((await entities.GetAsync(Guid.Parse(agentId)))!.Data["skills"] as JsonArray ?? [])
            .Select(n => n!.GetValue<string>()).ToArray();

    [Fact]
    public async Task Binding_adds_the_mail_skill_once_and_keeps_other_skills()
    {
        var w = Create();
        var first = Assert.Single(await w.Skills.SyncAsync(null, AgentA, Harness.Owner, default));
        Assert.Equal(SkillAssignmentStatus.Added, first.Status);
        Assert.Equal([OtherSkill, w.Skill.Id.ToString()], await SkillsOf(w.Entities, AgentA));

        var again = Assert.Single(await w.Skills.SyncAsync(AgentA, AgentA, Harness.Owner, default));
        Assert.Equal(SkillAssignmentStatus.AlreadyPresent, again.Status);
        Assert.Equal([OtherSkill, w.Skill.Id.ToString()], await SkillsOf(w.Entities, AgentA));
        Assert.Null((await w.Entities.GetAsync(Guid.Parse(AgentA)))!.Data["owner_plugin"]); // ownership stamp untouched
    }

    [Theory]
    [InlineData("949dfba3-0e3d-595d-8bf1-e1c0b64c11d8")]
    [InlineData("949DFBA3-0E3D-595D-8BF1-E1C0B64C11D8")]
    [InlineData("mail")]
    public async Task A_hand_assigned_skill_by_id_or_slug_counts_as_already_present(string reference)
    {
        var w = Create();
        await w.Entities.PatchAsync(Guid.Parse(AgentA), new JsonObject { ["skills"] = new JsonArray(OtherSkill, reference) });
        Assert.Equal(SkillAssignmentStatus.AlreadyPresent, Assert.Single(await w.Skills.SyncAsync(null, AgentA, Harness.Owner, default)).Status);
        Assert.Equal([OtherSkill, reference], await SkillsOf(w.Entities, AgentA));
    }

    [Fact]
    public async Task Switching_agents_moves_the_skill_and_unbinding_removes_it()
    {
        var w = Create();
        await w.Entities.PatchAsync(Guid.Parse(AgentA), new JsonObject { ["skills"] = new JsonArray(OtherSkill, "mail", w.Skill.Id.ToString()) });

        var moved = await w.Skills.SyncAsync(AgentA, AgentB, Harness.Owner, default);
        Assert.Equal([(AgentA, SkillAssignmentStatus.Removed), (AgentB, SkillAssignmentStatus.Added)], moved.Select(a => (a.AgentId, a.Status)));
        Assert.Equal([OtherSkill], await SkillsOf(w.Entities, AgentA)); // every reference to the mail skill, nothing else
        Assert.Equal([w.Skill.Id.ToString()], await SkillsOf(w.Entities, AgentB));

        var unbound = Assert.Single(await w.Skills.SyncAsync(AgentB, null, Harness.Owner, default));
        Assert.Equal(SkillAssignmentStatus.Removed, unbound.Status);
        Assert.Empty(await SkillsOf(w.Entities, AgentB));
        Assert.Equal(SkillAssignmentStatus.NotPresent, Assert.Single(await w.Skills.SyncAsync(AgentB, null, Harness.Owner, default)).Status);
    }

    [Fact]
    public async Task An_agent_owned_by_another_plugin_is_reported_not_written()
    {
        var w = Create();
        var foreign = w.Entities.AddWithId(Guid.NewGuid(), "agent", "seeded", new JsonObject
        {
            ["owner_plugin"] = Guid.NewGuid().ToString(), ["skills"] = new JsonArray(OtherSkill),
        });
        var result = Assert.Single(await w.Skills.SyncAsync(null, foreign.Id.ToString(), Harness.Owner, default));
        Assert.Equal(SkillAssignmentStatus.ManualRequired, result.Status);
        Assert.Equal([OtherSkill], await SkillsOf(w.Entities, foreign.Id.ToString()));
    }

    [Fact]
    public async Task Another_users_confidential_agent_and_a_missing_skill_are_left_alone()
    {
        var w = Create();
        var hidden = w.Entities.AddWithId(Guid.NewGuid(), "agent", "private", new JsonObject
        {
            ["confidential"] = true, ["owner_id"] = "33333333-3333-3333-3333-333333333333",
        });
        Assert.Equal(SkillAssignmentStatus.AgentMissing, Assert.Single(await w.Skills.SyncAsync(null, hidden.Id.ToString(), Harness.Owner, default)).Status);

        var noSkill = Create(seedSkill: false);
        Assert.Equal(SkillAssignmentStatus.SkillMissing, Assert.Single(await noSkill.Skills.SyncAsync(null, AgentA, Harness.Owner, default)).Status);
        Assert.Equal([OtherSkill], await SkillsOf(noSkill.Entities, AgentA));
    }

    [Fact]
    public async Task A_user_made_skill_named_mail_is_not_mistaken_for_the_plugin_skill()
    {
        var entities = new MemoryEntities();
        entities.Add("plugin", "mail", new JsonObject());
        entities.Add("agent-skill", "mail", new JsonObject { ["owner_plugin"] = Guid.NewGuid().ToString() });
        entities.AddWithId(Guid.Parse(AgentA), "agent", "nova", new JsonObject());
        var result = Assert.Single(await new MailAgentSkills(entities).SyncAsync(null, AgentA, Harness.Owner, default));
        Assert.Equal(SkillAssignmentStatus.SkillMissing, result.Status);
    }
}
