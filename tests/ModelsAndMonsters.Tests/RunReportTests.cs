using System.Text.RegularExpressions;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The report is generated purely from the run artefacts, so these tests build a real run directory
/// and render it, without any live simulation.
/// </summary>
public sealed class RunReportTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"mm-report-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_report_covers_run_details_every_trace_row_and_the_final_state()
    {
        var harness = await RunOneTurnAsync();
        var paths = WriteArtefacts(harness);

        var reportPath = RunReportWriter.Write(paths.Directory);
        var report = File.ReadAllText(reportPath);

        Assert.Equal(RunReportWriter.ReportPath(paths.Directory), reportPath);

        // Run details.
        Assert.Contains(paths.RunId, report, StringComparison.Ordinal);
        Assert.Contains("## Run details", report, StringComparison.Ordinal);
        Assert.Contains("scripted-model", report, StringComparison.Ordinal);
        Assert.Contains("sha256:", report, StringComparison.Ordinal);
        Assert.Contains("Grik was defeated.", report, StringComparison.Ordinal);

        // Every trace row is present: one heading per event, numbered in sequence order.
        var headings = Regex.Matches(report, @"^### `(\d{4})` ", RegexOptions.Multiline);
        Assert.Equal(harness.Sink.Events.Count, headings.Count);
        Assert.Equal(
            harness.Sink.Events.Select(e => e.Sequence),
            headings.Select(m => long.Parse(m.Groups[1].Value)));

        // Final state.
        Assert.Contains("## Final state", report, StringComparison.Ordinal);
        Assert.Contains("Complete final state", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_transcribes_the_encounter_in_the_order_it_happened()
    {
        var harness = await RunOneTurnAsync();
        var report = File.ReadAllText(RunReportWriter.Write(WriteArtefacts(harness).Directory));

        var transcript = report[report.IndexOf("## Transcript", StringComparison.Ordinal)..];
        var intent = transcript.IndexOf("I bring my sword down on the goblin.", StringComparison.Ordinal);
        var engine = transcript.IndexOf("**ENGINE**", StringComparison.Ordinal);
        var narration = transcript.IndexOf("gash across the goblin", StringComparison.Ordinal);

        Assert.True(intent > 0 && engine > 0 && narration > 0);

        // The character acts, then the engine resolves it, then the world describes it.
        Assert.True(intent < engine, "The character's intent should precede the engine result.");
        Assert.True(engine < narration, "The engine result should precede the narration of it.");
    }

    [Fact]
    public async Task Oversized_rows_are_truncated_with_a_pointer_to_the_full_trace_line()
    {
        var harness = await RunOneTurnAsync();
        var report = File.ReadAllText(RunReportWriter.Write(WriteArtefacts(harness).Directory));

        // Model requests carry the whole history, so at least one row is too large to reproduce whole.
        Assert.Contains("Raw event (truncated)", report, StringComparison.Ordinal);
        Assert.Contains("of `trace.jsonl`", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Rendering_a_directory_without_a_trace_fails_clearly()
    {
        Directory.CreateDirectory(_directory);

        var exception = Assert.Throws<FileNotFoundException>(() => RunReportWriter.Write(_directory));
        Assert.Contains("trace.jsonl", exception.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------

    private static async Task<OrchestrationHarness> RunOneTurnAsync()
    {
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("Your blade opens a gash across the goblin's shoulder.")),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName,
                ("intent", "I bring my sword down on the goblin."))),
            new ScriptedChatClient(),
            initialState: TestWorld.State(TestWorld.Hero(), TestWorld.Monster(health: 3, armour: 0)));

        await harness.RunHeroTurn();
        return harness;
    }

    private RunPaths WriteArtefacts(OrchestrationHarness harness)
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
            AgentName = "Aric",
            Provider = AI.ModelProvider.Ollama,
            ModelId = "scripted-model",
            Temperature = 0.5f
        };

        RunArtifactWriter.WriteManifest(paths, new RunManifest
        {
            RunId = paths.RunId,
            StartedAt = DateTimeOffset.UnixEpoch,
            ApplicationVersion = "0.1.0.0",
            MachineOperatingSystem = "test",
            Scenario = TestWorld.Scenario(),
            InitialState = ScenarioFactory.CreateInitialState(TestWorld.Scenario()),
            AgentProfiles = new Dictionary<string, TracedAgentProfile>
            {
                ["DungeonMaster"] = TracedAgentProfile.From(profile),
                ["Hero"] = TracedAgentProfile.From(profile),
                ["Monster"] = TracedAgentProfile.From(profile)
            },
            PromptVersions = new Dictionary<string, string> { ["character.system"] = "sha256:abc123" },
            Harness = new HarnessOptions(),
            Seeds = new RunSeedInfo
            {
                MasterSeed = 12345,
                SeedWasProvided = true,
                GameSeed = 999,
                AgentSeeds = new Dictionary<string, long> { ["DungeonMaster"] = 1, ["Hero"] = 2, ["Monster"] = 3 }
            }
        });

        RunArtifactWriter.WriteFinalState(paths, new FinalStateDocument
        {
            RunId = paths.RunId,
            CompletedAt = DateTimeOffset.UnixEpoch,
            TerminalCondition = "Grik was defeated.",
            RoundsPlayed = 1,
            TraceEventCount = harness.Sink.Events.Count,
            State = harness.Engine.State
        });

        return paths;
    }
}
