using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The generated report must reconstruct, from the run artefacts alone, the objective container truth and
/// who knew what — with first-hand knowledge kept strictly separate from hearsay (#26). This drives a real
/// inspect → tell → open → take run through the coordinator, writes its artefacts, and renders the report.
/// </summary>
public sealed class KnowledgeReportTests : IDisposable
{
    private const string MedicineCase = "Faded Shrine Medicine Case";
    private const string Potion = "Small Healing Potion";
    private const string Claim = "That shrine-marked case is a medicine case — the potion's ours!";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mm-v04-report-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task The_report_reconstructs_objective_truth_and_per_character_knowledge_separately()
    {
        var scenario = TestWorld.TwoCasesScenario();
        var initial = ScenarioFactory.CreateInitialState(scenario);

        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.InspectObjectName, ("actor", "Elara"), ("object", MedicineCase)),
                ScriptedChatClient.Text("Elara wipes the grime away and studies the case."),
                ScriptedChatClient.Text("Elara stays her hand, eyes on the goblins."),
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.OpenContainerName, ("actor", "Rowan"), ("container", MedicineCase)),
                ScriptedChatClient.Text("Rowan throws back the lid and looks inside."),
                ScriptedChatClient.Call("dm-3", DungeonMasterTools.TakeItemName, ("actor", "Rowan"), ("container", MedicineCase), ("item", Potion)),
                ScriptedChatClient.Text("Rowan lifts the vial free and holds it up.")),
            MultiActorHarness.Clients(
                ("Elara", new ScriptedChatClient(
                    ScriptedChatClient.Call("e-1", CharacterTools.TakeActionName, ("intent", "I study the case's markings.")),
                    ScriptedChatClient.Call("e-2", CharacterTools.SayName, ("message", Claim)),
                    ScriptedChatClient.Call("e-3", CharacterTools.EndTurnName, ("reason", "Said it.")))),
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName, ("intent", "I open the shrine case.")),
                    ScriptedChatClient.Call("r-2", CharacterTools.TakeActionName, ("intent", "I take the potion."))))),
            initialState: initial,
            scenario: scenario);

        await harness.RunTurn("Elara", 1, 1);
        await harness.RunTurn("Elara", 1, 2);
        await harness.RunTurn("Rowan", 2, 3);
        await harness.RunTurn("Rowan", 2, 4);

        var report = File.ReadAllText(RunReportWriter.Write(WriteArtefacts(harness, scenario, initial).Directory));

        // Objective truth: the medicine case began with the potion and ended open and empty.
        var objective = Section(report, "## Objective container state");
        Assert.Contains(MedicineCase, objective, StringComparison.Ordinal);
        Assert.Contains(Potion, objective, StringComparison.Ordinal);
        Assert.Matches(@"Faded Shrine Medicine Case \|[^\n]*\| open \|", objective);

        // A discovery timeline naming who learned what.
        var timeline = Section(report, "## Knowledge timeline");
        Assert.Contains("Elara", timeline, StringComparison.Ordinal);
        Assert.Contains("Rowan", timeline, StringComparison.Ordinal);

        // Per-character knowledge, with hearsay kept separate from first-hand knowledge.
        var perCharacter = Section(report, "## Per-character knowledge");
        Assert.Contains("Directly knew (first-hand):", perCharacter, StringComparison.Ordinal);
        Assert.Contains("Only heard others say", perCharacter, StringComparison.Ordinal);
        // Rowan's first-hand knowledge includes the potion he saw on opening.
        Assert.Contains(Potion, perCharacter, StringComparison.Ordinal);
        // The heard claim is recorded as hearsay, attributed to Elara.
        Assert.Contains(Claim, perCharacter, StringComparison.Ordinal);
    }

    /// <summary>The report text from a heading to the next top-level heading, so a check cannot match elsewhere.</summary>
    private static string Section(string report, string heading)
    {
        var start = report.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Report is missing the '{heading}' section.");
        var next = report.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? report[start..] : report[start..next];
    }

    private RunPaths WriteArtefacts(MultiActorHarness harness, ScenarioDefinition scenario, Domain.GameState initial)
    {
        var paths = RunPaths.Create(_directory, DateTimeOffset.UnixEpoch);

        using (var sink = new JsonlTraceSink(paths.TraceJsonl))
        {
            foreach (var traceEvent in harness.Sink.Events)
            {
                sink.Write(traceEvent);
            }
        }

        var profile = new AI.AgentModelProfile
        {
            AgentName = "DungeonMaster",
            Provider = AI.ModelProvider.Ollama,
            ModelId = "scripted-model",
            Temperature = 0.5f
        };

        RunArtifactWriter.WriteManifest(paths, new RunManifest
        {
            RunId = paths.RunId,
            StartedAt = DateTimeOffset.UnixEpoch,
            ApplicationVersion = "0.4.0.0",
            MachineOperatingSystem = "test",
            Scenario = scenario,
            InitialState = initial,
            AgentProfiles = new Dictionary<string, TracedAgentProfile> { ["DungeonMaster"] = TracedAgentProfile.From(profile) },
            PromptVersions = new Dictionary<string, string> { ["character.system"] = "sha256:abc123" },
            Harness = new HarnessOptions(),
            Seeds = new RunSeedInfo
            {
                MasterSeed = 12345,
                SeedWasProvided = true,
                GameSeed = 999,
                AgentSeeds = new Dictionary<string, long> { ["DungeonMaster"] = 1 }
            }
        });

        RunArtifactWriter.WriteFinalState(paths, new FinalStateDocument
        {
            RunId = paths.RunId,
            CompletedAt = DateTimeOffset.UnixEpoch,
            TerminalCondition = "Ended for the test.",
            RoundsPlayed = 2,
            TraceEventCount = harness.Sink.Events.Count,
            State = harness.Engine.State
        });

        return paths;
    }
}
