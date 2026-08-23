using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// Drives the whole <see cref="SimulationRunner"/> through a scripted 2v2 encounter — no model service
/// — so the fixed turn order, the team terminal condition and the generated report are exercised
/// exactly as a live run would produce them. Combat is deterministic (every attack lands, no glancing)
/// so the scripted decisions alone decide the course of the fight.
/// </summary>
public sealed class MultiActorRunTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"mm-2v2-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_scripted_2v2_encounter_runs_in_fixed_turn_order_to_a_team_victory_and_a_full_report()
    {
        var summary = await RunScriptedEncounterAsync();

        // The heroes win once both goblins are dead, in two rounds.
        Assert.Contains("Heroes win", summary.TerminalCondition, StringComparison.Ordinal);
        Assert.Equal(2, summary.RoundsPlayed);
        Assert.False(summary.FinalState.RequireById(TestWorld.VarkId).IsAlive);
        Assert.False(summary.FinalState.RequireById(TestWorld.SkritId).IsAlive);
        Assert.True(summary.FinalState.RequireById(TestWorld.RowanId).IsAlive);
        Assert.True(summary.FinalState.RequireById(TestWorld.ElaraId).IsAlive);

        // Test #2: the fixed turn order — Rowan, Elara, Vark, Skrit — repeats each round, and the second
        // round stops the moment the goblins are wiped, so only the two heroes act in it.
        var turnStarts = ReadTraceData(summary.OutputDirectory, "TurnStarted")
            .Select(d => d.GetProperty("CharacterName").GetString())
            .ToList();
        Assert.Equal(["Rowan", "Elara", "Vark", "Skrit", "Rowan", "Elara"], turnStarts);

        // Every accepted attack recorded its target resolution against a stable id.
        var resolutions = ReadTraceData(summary.OutputDirectory, "TargetResolved")
            .Select(d => d.GetProperty("ResolvedTargetId").GetString())
            .ToList();
        Assert.Contains(TestWorld.VarkId, resolutions);
        Assert.Contains(TestWorld.SkritId, resolutions);

        // Every landed attack drew twice (hit-check and glancing-check), each fully recorded.
        var draws = ReadTraceData(summary.OutputDirectory, "RngDraw").ToList();
        Assert.NotEmpty(draws);
        Assert.All(draws, d =>
        {
            Assert.False(string.IsNullOrEmpty(d.GetProperty("Purpose").GetString()));
            Assert.True(d.TryGetProperty("RawRoll", out _));
            Assert.True(d.TryGetProperty("SequenceBefore", out _));
            Assert.True(d.TryGetProperty("SequenceAfter", out _));
        });

        // Test #13: the report reconstructs the encounter — all four agents, the teams and their result,
        // targeting, and RNG detail.
        var report = File.ReadAllText(RunReportWriter.ReportPath(summary.OutputDirectory));
        foreach (var name in new[] { "Rowan", "Elara", "Vark", "Skrit", "DungeonMaster" })
        {
            Assert.Contains(name, report, StringComparison.Ordinal);
        }

        Assert.Contains("## Teams", report, StringComparison.Ordinal);
        Assert.Contains("Heroes", report, StringComparison.Ordinal);
        Assert.Contains("Goblins", report, StringComparison.Ordinal);
        Assert.Contains("## Per-agent activity", report, StringComparison.Ordinal);
        Assert.Contains("Heroes win", report, StringComparison.Ordinal);
        Assert.Contains("attack.hit-check", report, StringComparison.Ordinal);
        Assert.Contains("TargetResolved", report, StringComparison.Ordinal);

        // A configured seed reads as fixed (the boolean is serialised lowercase, so this guards the
        // report against comparing it as the string "True"), and resolved targets show their stable id.
        Assert.Contains("(fixed)", report, StringComparison.Ordinal);
        Assert.Contains($"Resolved to:** Vark (`{TestWorld.VarkId}`)", report, StringComparison.Ordinal);
        Assert.DoesNotContain("the reference did not resolve", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_runs_with_the_same_seed_reach_the_same_final_state()
    {
        var first = await RunScriptedEncounterAsync();
        var second = await RunScriptedEncounterAsync();

        foreach (var id in new[] { TestWorld.RowanId, TestWorld.ElaraId, TestWorld.VarkId, TestWorld.SkritId })
        {
            Assert.Equal(
                first.FinalState.RequireById(id).Health,
                second.FinalState.RequireById(id).Health);
        }

        Assert.Equal(first.TerminalCondition, second.TerminalCondition);
    }

    // ------------------------------------------------------------------------------------------

    private async Task<SimulationSummary> RunScriptedEncounterAsync()
    {
        var scenario = TestWorld.TwoVsTwoScenario();
        // Reduce goblin health so two rounds of attacks wipe them, keeping the script short.
        scenario.Characters.First(c => c.Id == TestWorld.VarkId).MaxHealth = 6;
        scenario.Characters.First(c => c.Id == TestWorld.SkritId).MaxHealth = 4;

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
                Seed = 12345,
                MaxRounds = 4,
                MaxConsecutiveIdleRounds = 3,
                MaxQuestionsPerTurn = 2,
                MaxActionAttemptsPerTurn = 3,
                MaxModelCallsPerTurn = 8,
                // These scripted runs exercise the deterministic core in fixed turn order; the prose-fallback
                // intent parser and the history summariser play no part (every scripted reply is a clean tool
                // call, and the runs are short), and switching them off keeps the runner from asking the
                // scripted factory for parser/summariser clients it has no script for.
                UseIntentParser = false,
                SummariseHistory = false,
                // Likewise the rulebook resolver: this scripted run exercises the deterministic core, not the
                // v0.6 rulebook stage, so switch it off rather than script a resolver client for every attempt.
                EnableRulebookResolver = false,
                // The end-of-run storyteller is exercised by its own tests; switch it off here rather than
                // script an EncounterSummariser client this run has no use for.
                GenerateEncounterStory = false,
                // Same reasoning for the per-round recap: it is an extra DM call at each round's end, which this
                // fixed scripted sequence has no reply for. Its own behaviour is covered elsewhere.
                NarrateRoundSummaries = false,
                RunOutputDirectory = _directory
            },
            // Deterministic damage: both bands of the quality draw are silenced, so every landed blow is a
            // plain solid hit. The draw is still taken, so the seeded sequence is unchanged either way.
            Combat = new CombatOptions { GlancingBlowChance = 0, CriticalHitChance = 0 }
        };

        var factory = new ScriptedChatClientFactory(BuildClients());
        var prompts = PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));

        var runner = new SimulationRunner(
            Options.Create(options),
            Options.Create(scenario),
            factory,
            prompts,
            new RecordingConsole());

        return await runner.RunAsync();
    }

    private static Dictionary<string, ScriptedChatClient> BuildClients()
    {
        // The Dungeon Master's calls in strict order: opening narration, then for each accepted attack
        // an adjudication (the tool call) and an outcome narration.
        var dungeonMaster = new ScriptedChatClient(
            ScriptedChatClient.Text("The lantern gutters over four figures in the flooded cellar; steel is already drawn."),

            ScriptedChatClient.Call("dm-r1", DungeonMasterTools.AttackCharacterName,
                ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
            ScriptedChatClient.Text("Rowan's longsword scores a line across the captain's arm."),

            ScriptedChatClient.Call("dm-e1", DungeonMasterTools.AttackCharacterName,
                ("attacker", "Elara"), ("target", "Skrit"), ("weapon", "Iron Mace")),
            ScriptedChatClient.Text("Elara's mace cracks into the smaller goblin's ribs."),

            ScriptedChatClient.Call("dm-v1", DungeonMasterTools.AttackCharacterName,
                ("attacker", "Vark"), ("target", "Rowan"), ("weapon", "Notched Sabre")),
            ScriptedChatClient.Text("Vark's sabre rings off Rowan's mail."),

            ScriptedChatClient.Call("dm-s1", DungeonMasterTools.AttackCharacterName,
                ("attacker", "Skrit"), ("target", "Elara"), ("weapon", "Crude Spear")),
            ScriptedChatClient.Text("Skrit's spear scratches a red line across Elara's forearm."),

            ScriptedChatClient.Call("dm-r2", DungeonMasterTools.AttackCharacterName,
                ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
            ScriptedChatClient.Text("Rowan's blade drives home and the captain folds into the water."),

            ScriptedChatClient.Call("dm-e2", DungeonMasterTools.AttackCharacterName,
                ("attacker", "Elara"), ("target", "Skrit"), ("weapon", "Iron Mace")),
            ScriptedChatClient.Text("Elara's mace fells the last goblin where it stands."));

        var rowan = new ScriptedChatClient(
            ScriptedChatClient.Call("r1", CharacterTools.TakeActionName, ("intent", "I bring my longsword down on the captain, Vark.")),
            ScriptedChatClient.Call("r2", CharacterTools.TakeActionName, ("intent", "I strike Vark again before he can recover.")));

        var elara = new ScriptedChatClient(
            ScriptedChatClient.Call("e1", CharacterTools.TakeActionName, ("intent", "I swing my mace at the grunt, Skrit.")),
            ScriptedChatClient.Call("e2", CharacterTools.TakeActionName, ("intent", "I bring my mace down on Skrit once more.")));

        var vark = new ScriptedChatClient(
            ScriptedChatClient.Call("v1", CharacterTools.TakeActionName, ("intent", "I slash at the swordsman Rowan.")));

        var skrit = new ScriptedChatClient(
            ScriptedChatClient.Call("s1", CharacterTools.TakeActionName, ("intent", "I jab my spear at the cleric.")));

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
