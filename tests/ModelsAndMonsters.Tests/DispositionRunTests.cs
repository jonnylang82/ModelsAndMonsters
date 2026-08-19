using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// A whole scripted encounter that ends without wiping the losing team: Vark is killed while Skrit escapes
/// through the cellar door, so the Goblins lose all active members by two different routes. Exercises the
/// disposition-based terminal condition, the Mixed classification, the immediate stop on resolution, and the
/// report's exit-state / disposition / outcome sections — end to end through the real <see cref="SimulationRunner"/>.
/// </summary>
public sealed class DispositionRunTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mm-v05-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Vark_dies_and_Skrit_escapes_so_the_heroes_win_a_mixed_resolution()
    {
        var summary = await RunMixedEncounterAsync();

        // Heroes win, and the losing team left by two routes at once — the Mixed classification.
        Assert.Contains("Heroes win", summary.TerminalCondition, StringComparison.Ordinal);
        Assert.False(summary.FinalState.RequireById(TestWorld.VarkId).IsAlive);

        var skrit = summary.FinalState.RequireById(TestWorld.SkritId);
        Assert.True(skrit.IsAlive);
        Assert.Equal(ModelsAndMonsters.Domain.CharacterDisposition.Escaped, skrit.Disposition);
        Assert.Equal(TestWorld.StairDoorId, skrit.EscapedThroughExitId);

        // Test #24: resolution stops the encounter immediately. Vark dies on Rowan's round-2 turn, so Elara's
        // round-2 turn never starts — the turn order ends mid-round rather than finishing it.
        var turnStarts = ReadTraceData(summary.OutputDirectory, "TurnStarted")
            .Select(d => d.GetProperty("CharacterName").GetString())
            .ToList();
        Assert.Equal(["Rowan", "Elara", "Vark", "Skrit", "Rowan"], turnStarts);

        // The final team-outcome evaluation classifies the win as Mixed and names how each goblin left.
        var finalOutcome = ReadTraceData(summary.OutputDirectory, "TeamOutcomeEvaluated")
            .Last(d => d.GetProperty("Trigger").GetString() == "final");
        Assert.Equal("Mixed", finalOutcome.GetProperty("Outcome").GetString());
        var resolution = finalOutcome.GetProperty("ResolutionSummary").GetString() ?? "";
        Assert.Contains("Vark was killed.", resolution, StringComparison.Ordinal);
        Assert.Contains("Skrit escaped through the Cellar Stair Door.", resolution, StringComparison.Ordinal);

        // Test #34: the report distinguishes the dead from the escaped, and never calls Skrit "fallen".
        var report = File.ReadAllText(RunReportWriter.ReportPath(summary.OutputDirectory));
        Assert.Contains("## Outcome", report, StringComparison.Ordinal);
        Assert.Contains("Classification", report, StringComparison.Ordinal);
        Assert.Contains("Mixed", report, StringComparison.Ordinal);
        Assert.Contains("## Exit state", report, StringComparison.Ordinal);
        Assert.Contains("Cellar Stair Door", report, StringComparison.Ordinal);
        Assert.Contains("## Non-combat outcomes", report, StringComparison.Ordinal);
        // The disposition column carries the two non-lethal states, and the escaped location is outside.
        Assert.Contains("Escaped", report, StringComparison.Ordinal);
        Assert.Contains("Outside the encounter", report, StringComparison.Ordinal);
        Assert.Contains("Skrit escaped through the Cellar Stair Door.", report, StringComparison.Ordinal);
    }

    private async Task<SimulationSummary> RunMixedEncounterAsync()
    {
        var scenario = TestWorld.TwoVsTwoScenarioWithExit();
        // Two of Rowan's longsword blows (3 damage each past armour) kill the captain across the two rounds.
        scenario.Characters.First(c => c.Id == TestWorld.VarkId).MaxHealth = 6;

        var options = new SimulationOptions
        {
            Agents = new AgentsOptions
            {
                Default = new AgentProfileOptions { Provider = "Ollama", ModelId = "scripted-model" },
                DungeonMaster = new AgentProfileOptions { Temperature = 0.2f },
                Characters = new Dictionary<string, AgentProfileOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [TestWorld.RowanId] = new(),
                    [TestWorld.ElaraId] = new(),
                    [TestWorld.VarkId] = new(),
                    [TestWorld.SkritId] = new()
                }
            },
            Harness = new HarnessOptions
            {
                Seed = 999,
                MaxRounds = 4,
                MaxConsecutiveIdleRounds = 3,
                MaxQuestionsPerTurn = 2,
                MaxActionAttemptsPerTurn = 3,
                MaxModelCallsPerTurn = 8,
                UseIntentParser = false,
                SummariseHistory = false,
                // The v0.6 rulebook stage is exercised by its own tests; this scripted run tests the
                // deterministic core, so switch it off rather than script a resolver client per attempt.
                EnableRulebookResolver = false,
                RunOutputDirectory = _directory
            },
            Combat = new CombatOptions { GlancingBlowChance = 0 }
        };

        var factory = new ScriptedChatClientFactory(BuildClients());
        var prompts = PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));

        var runner = new SimulationRunner(
            Options.Create(options), Options.Create(scenario), factory, prompts, new RecordingConsole());

        return await runner.RunAsync();
    }

    private static Dictionary<string, ScriptedChatClient> BuildClients()
    {
        // The DM's calls in strict order across the whole encounter: opening narration, then a tool call and
        // an outcome narration for each accepted action (attack, pass, open_exit, escape).
        var dungeonMaster = new ScriptedChatClient(
            ScriptedChatClient.Text("The lantern gutters over four figures in the flooded cellar; a shut door waits at the stairs."),

            // Round 1 — Rowan wounds Vark.
            ScriptedChatClient.Call("dm-r1", DungeonMasterTools.AttackCharacterName,
                ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
            ScriptedChatClient.Text("Rowan's longsword opens a deep line across the captain's arm."),

            // Elara holds and guards.
            ScriptedChatClient.Text("Elara sets herself at Rowan's shoulder, mace ready."),

            // Vark hauls the door open.
            ScriptedChatClient.Call("dm-v1", DungeonMasterTools.OpenExitName,
                ("actor", "Vark"), ("exit", "Cellar Stair Door")),
            ScriptedChatClient.Text("Vark wrenches the cellar door open; the dark stairs gape beyond."),

            // Skrit bolts through it.
            ScriptedChatClient.Call("dm-s1", DungeonMasterTools.EscapeEncounterName,
                ("actor", "Skrit"), ("exit", "Cellar Stair Door")),
            ScriptedChatClient.Text("Skrit scrambles through the open door and is gone up the stairs."),

            // Round 2 — Rowan finishes Vark.
            ScriptedChatClient.Call("dm-r2", DungeonMasterTools.AttackCharacterName,
                ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
            ScriptedChatClient.Text("Rowan's blade drives home and the captain folds into the black water."));

        var rowan = new ScriptedChatClient(
            ScriptedChatClient.Call("r1", CharacterTools.TakeActionName, ("intent", "I bring my longsword down on the captain, Vark.")),
            ScriptedChatClient.Call("r2", CharacterTools.TakeActionName, ("intent", "I strike Vark again before he can recover.")));

        var elara = new ScriptedChatClient(
            ScriptedChatClient.Call("e1", CharacterTools.EndTurnName, ("reason", "I hold beside Rowan and guard.")));

        var vark = new ScriptedChatClient(
            ScriptedChatClient.Call("v1", CharacterTools.TakeActionName, ("intent", "I wrench the cellar door open.")));

        var skrit = new ScriptedChatClient(
            ScriptedChatClient.Call("s1", CharacterTools.TakeActionName, ("intent", "I bolt through the open door and get clear.")));

        return new Dictionary<string, ScriptedChatClient>(StringComparer.OrdinalIgnoreCase)
        {
            [DungeonMasterAgent.AgentIdentifier] = dungeonMaster,
            ["Rowan"] = rowan,
            ["Elara"] = elara,
            ["Vark"] = vark,
            ["Skrit"] = skrit
        };
    }

    private static IEnumerable<JsonElement> ReadTraceData(string runDirectory, string eventType)
    {
        foreach (var line in File.ReadLines(Path.Combine(runDirectory, "trace.jsonl")))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var root = JsonDocument.Parse(line).RootElement;
            if (root.TryGetProperty("EventType", out var type) &&
                type.GetString() == eventType &&
                root.TryGetProperty("Data", out var data))
            {
                yield return data.Clone();
            }
        }
    }
}
