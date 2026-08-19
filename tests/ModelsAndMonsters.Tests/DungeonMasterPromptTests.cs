using Microsoft.Extensions.AI;
using ModelsAndMonsters.Agents;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The Dungeon Master's system prompt is modular: a shared core plus a per-job rules block, composed so the
/// hot adjudication path never carries the narration/answering rules it does not use (and vice versa). These
/// tests pin that composition down so the split cannot silently regress back into one monolithic prompt.
/// </summary>
public sealed class DungeonMasterPromptTests
{
    private static string SystemOf(IReadOnlyList<ChatMessage> request) =>
        request.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";

    [Fact]
    public async Task The_dungeon_master_system_prompt_is_composed_per_job()
    {
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("Aric's sword bites into the goblin's shoulder.")),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike Grik."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var requests = harness.DungeonMasterClient.Requests;
        var adjudicate = SystemOf(requests[0]);   // the adjudication call carries tools
        var narrate = SystemOf(requests[^1]);      // the outcome narration is the last DM call

        // The hot adjudication path carries the adjudication rules but NOT the narration/answering rules.
        Assert.Contains("Decide in this order", adjudicate, StringComparison.Ordinal);
        Assert.DoesNotContain("# Narration rules", adjudicate, StringComparison.Ordinal);
        Assert.DoesNotContain("# Answering rules", adjudicate, StringComparison.Ordinal);

        // The narration call carries the narration rules but NOT the adjudication decision procedure.
        Assert.Contains("# Narration rules", narrate, StringComparison.Ordinal);
        Assert.DoesNotContain("Decide in this order", narrate, StringComparison.Ordinal);

        // Both share the core (the shape of the world), and the narration call is far leaner than the
        // adjudication one because it sheds the large decision procedure.
        Assert.Contains("The shape of this world", adjudicate, StringComparison.Ordinal);
        Assert.Contains("The shape of this world", narrate, StringComparison.Ordinal);
        Assert.True(narrate.Length < adjudicate.Length,
            $"narration prompt ({narrate.Length}) should be leaner than adjudication prompt ({adjudicate.Length})");
    }
}
