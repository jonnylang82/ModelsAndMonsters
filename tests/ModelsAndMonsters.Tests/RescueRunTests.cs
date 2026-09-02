using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Prompts;

namespace ModelsAndMonsters.Tests;

public sealed class RescueRunTests
{
    [Fact]
    public async Task Full_run_continues_after_guard_defeat_and_guest_hears_release_before_extracting()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mm-rescue-{Guid.NewGuid():N}");
        try
        {
            var scenario = TestWorld.TwoVsTwoScenarioWithExit();
            scenario.Characters.Single(c => c.Id == TestWorld.ElaraId).StartingDisposition = "Detained";
            scenario.Characters.Single(c => c.Id == TestWorld.VarkId).Health = 1;
            scenario.Characters.Single(c => c.Id == TestWorld.SkritId).StartingDisposition = "Surrendered";
            scenario.Rescue = new RescueDefinition
            {
                DetaineeId = TestWorld.ElaraId, GuardTeam = TestWorld.GoblinsTeam, ExitId = TestWorld.StairDoorId
            };
            var dm = new ScriptedChatClient(
                ScriptedChatClient.Text("Elara is confined behind a gate; Vark guards it."),
                ScriptedChatClient.Call("attack", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
                ScriptedChatClient.Text("Vark falls."),
                ScriptedChatClient.Call("open", DungeonMasterTools.OpenExitName,
                    ("actor", "Elara"), ("exit", TestWorld.StairDoorId)),
                ScriptedChatClient.Text("Elara opens the stair."),
                ScriptedChatClient.Text("Rowan waits."),
                ScriptedChatClient.Call("escape", DungeonMasterTools.EscapeEncounterName,
                    ("actor", "Elara"), ("exit", TestWorld.StairDoorId)),
                ScriptedChatClient.Text("Elara reaches safety."));
            var elara = new ScriptedChatClient(
                ScriptedChatClient.Call("open", CharacterTools.TakeActionName, ("intent", "I open the stair.")),
                ScriptedChatClient.Call("leave", CharacterTools.TakeActionName, ("intent", "I escape through the stair.")));
            var factory = new ScriptedChatClientFactory(new Dictionary<string, ScriptedChatClient>(StringComparer.OrdinalIgnoreCase)
            {
                [DungeonMasterAgent.AgentIdentifier] = dm,
                ["Rowan"] = new(ScriptedChatClient.Call("hit", CharacterTools.TakeActionName, ("intent", "I strike Vark with my longsword.")),
                    ScriptedChatClient.Call("pass", CharacterTools.EndTurnName, ("reason", "I wait for Elara."))),
                ["Elara"] = elara, ["Vark"] = new(), ["Skrit"] = new()
            });
            var options = new SimulationOptions
            {
                Agents = new AgentsOptions { Default = new AgentProfileOptions { Provider = "Ollama", ModelId = "scripted" } },
                Harness = new HarnessOptions
                {
                    Seed = 999, MaxRounds = 3, MaxConsecutiveIdleRounds = 3, RunOutputDirectory = directory,
                    UseIntentParser = false, EnableRulebookResolver = false, SummariseHistory = false,
                    GenerateEncounterStory = false, NarrateRoundSummaries = false
                },
                Combat = new CombatOptions { GlancingBlowChance = 0 }
            };
            var runner = new SimulationRunner(options, scenario, factory,
                PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates")),
                new RecordingConsole(), null);
            var summary = await runner.RunAsync();
            Assert.Equal(2, summary.RoundsPlayed);
            Assert.Contains("Rescue complete", summary.TerminalCondition);
            Assert.Equal(CharacterDisposition.Escaped, summary.FinalState.RequireById(TestWorld.ElaraId).Disposition);
            Assert.Contains(elara.Requests[0], m => m.Text?.Contains("detention gate unlocks", StringComparison.Ordinal) == true);
            var trace = File.ReadAllText(Path.Combine(summary.OutputDirectory, "trace.jsonl"));
            Assert.Contains("rescue.release", trace);
            Assert.Contains("Rescued", trace);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
