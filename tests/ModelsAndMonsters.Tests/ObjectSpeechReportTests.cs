using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The generated report must be able to reconstruct a whole v0.3 encounter — the dialogue, the container
/// state transitions and who ended up owning the potion — purely from the run artefacts. This drives a
/// real speak-open-take run through the coordinator, writes its artefacts, and renders the report.
/// </summary>
public sealed class ObjectSpeechReportTests : IDisposable
{
    private const string Plan = "Elara, get whatever is in that chest. I'll hold off the captain.";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"mm-v03-report-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task The_report_reconstructs_speech_container_state_and_item_ownership()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                // Rowan opens the chest.
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.OpenContainerName,
                    ("actor", "Rowan"), ("container", "Old Iron-Bound Chest")),
                ScriptedChatClient.Text("Rowan heaves the lid up; a small vial glints inside."),
                // Elara takes the potion.
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.TakeItemName,
                    ("actor", "Elara"), ("container", "Old Iron-Bound Chest"), ("item", "Small Healing Potion")),
                ScriptedChatClient.Text("Elara scoops the vial from the chest and tucks it into her robes.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Call("r-1", CharacterTools.SayName, ("message", Plan)),
                    ScriptedChatClient.Call("r-2", CharacterTools.TakeActionName, ("intent", "I heave the old chest open.")))),
                ("Elara", new ScriptedChatClient(
                    ScriptedChatClient.Call("e-1", CharacterTools.TakeActionName, ("intent", "I take the vial from the open chest."))))),
            initialState: TestWorld.TwoVsTwoStateWithChest());

        await harness.RunTurn("Rowan", round: 1, turn: 1);
        await harness.RunTurn("Elara", round: 1, turn: 2);

        var report = File.ReadAllText(RunReportWriter.Write(WriteArtefacts(harness).Directory));

        // Speech: reconstructed in the transcript, verbatim and attributed.
        Assert.Contains($"**Rowan says:** \"{Plan}\"", report, StringComparison.Ordinal);

        // Initial container state and contents, in the authoritative scenario section.
        Assert.Contains("Objects in the room", report, StringComparison.Ordinal);
        Assert.Contains("Old Iron-Bound Chest", report, StringComparison.Ordinal);

        // The communication-and-objects summary shows the utterance and the two accepted object actions.
        Assert.Contains("Communication and object interaction", report, StringComparison.Ordinal);
        Assert.Contains("`open_container`", report, StringComparison.Ordinal);
        Assert.Contains("`take_item`", report, StringComparison.Ordinal);

        // Final state: the chest ended open and empty, and Elara — not the chest — now owns the potion.
        // The container row proves the polymorphic Container fields (IsOpen, Contents) survived into the
        // final-state artefact rather than being dropped to the base type.
        var finalSection = report[report.IndexOf("## Final state", StringComparison.Ordinal)..];
        Assert.Contains("Small Healing Potion", finalSection, StringComparison.Ordinal);
        Assert.Contains("Old Iron-Bound Chest", finalSection, StringComparison.Ordinal);
        Assert.Matches(@"Old Iron-Bound Chest \| open", finalSection);

        // Per-character speech is tallied for Rowan.
        Assert.Contains("Speeches", report, StringComparison.Ordinal);
    }

    private RunPaths WriteArtefacts(MultiActorHarness harness)
    {
        var paths = RunPaths.Create(_directory, DateTimeOffset.UnixEpoch);

        using (var sink = new JsonlTraceSink(paths.TraceJsonl))
        {
            foreach (var traceEvent in harness.Sink.Events)
            {
                sink.Write(traceEvent);
            }
        }

        var scenario = TestWorld.TwoVsTwoScenarioWithChest();
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
            ApplicationVersion = "0.3.0.0",
            MachineOperatingSystem = "test",
            Scenario = scenario,
            InitialState = ScenarioFactory.CreateInitialState(scenario),
            AgentProfiles = new Dictionary<string, TracedAgentProfile>
            {
                ["DungeonMaster"] = TracedAgentProfile.From(profile)
            },
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
            RoundsPlayed = 1,
            TraceEventCount = harness.Sink.Events.Count,
            State = harness.Engine.State
        });

        return paths;
    }
}
