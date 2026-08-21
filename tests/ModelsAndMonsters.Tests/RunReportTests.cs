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
    public void Cache_read_and_write_tokens_are_surfaced_when_the_usage_reports_them()
    {
        // OpenAI reports cached input automatically; Anthropic reports cache reads and creation writes
        // when prompt caching is used. Both land in the usage's additional counts.
        Directory.CreateDirectory(_directory);
        var trace = Path.Combine(_directory, "trace.jsonl");
        File.WriteAllText(trace,
            "{\"Sequence\":1,\"Timestamp\":\"2026-08-17T00:00:00Z\",\"RunId\":\"r\",\"Round\":1,\"Turn\":1," +
            "\"Actor\":\"DungeonMaster\",\"EventType\":\"ModelResponse\",\"Data\":{\"AgentName\":\"DungeonMaster\"," +
            "\"Usage\":{\"InputTokenCount\":100,\"OutputTokenCount\":20," +
            "\"AdditionalCounts\":{\"CacheReadInputTokens\":500,\"CacheCreationInputTokens\":300}}}}\n");

        var report = File.ReadAllText(RunReportWriter.Write(_directory));

        Assert.Contains("Cached input (read)", report, StringComparison.Ordinal);
        Assert.Contains("500", report, StringComparison.Ordinal);
        Assert.Contains("Cache writes", report, StringComparison.Ordinal);
        Assert.Contains("300", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_models_reasoning_is_folded_into_a_collapsible_thinking_block()
    {
        // Reasoning-on models (Ollama, Anthropic) return their thinking as a reasoning content block;
        // the transcript surfaces it in a <details> so it is present but does not clutter the story.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "trace.jsonl"),
            "{\"Sequence\":1,\"Timestamp\":\"2026-08-17T00:00:00Z\",\"RunId\":\"r\",\"Round\":1,\"Turn\":1," +
            "\"Actor\":\"Aric\",\"EventType\":\"ModelResponse\",\"Data\":{\"AgentName\":\"Aric\"," +
            "\"Messages\":[{\"Role\":\"assistant\",\"Contents\":[" +
            "{\"Type\":\"reasoning\",\"Text\":\"I am a sell-sword; I should strike first.\"}," +
            "{\"Type\":\"functionCall\",\"Name\":\"take_action\"}]}]}}\n");

        var report = File.ReadAllText(RunReportWriter.Write(_directory));

        Assert.Contains("<details><summary>\U0001F4AD Aric — thinking</summary>", report, StringComparison.Ordinal);
        Assert.Contains("> I am a sell-sword; I should strike first.", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_response_that_carries_no_reasoning_adds_no_thinking_block()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "trace.jsonl"),
            "{\"Sequence\":1,\"Timestamp\":\"2026-08-17T00:00:00Z\",\"RunId\":\"r\",\"Round\":1,\"Turn\":1," +
            "\"Actor\":\"Grik\",\"EventType\":\"ModelResponse\",\"Data\":{\"AgentName\":\"Grik\"," +
            "\"Messages\":[{\"Role\":\"assistant\",\"Contents\":[{\"Type\":\"text\",\"Text\":\"No thinking here.\"}]}]}}\n");

        var report = File.ReadAllText(RunReportWriter.Write(_directory));

        Assert.DoesNotContain("\U0001F4AD", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Rendering_a_directory_without_a_trace_fails_clearly()
    {
        Directory.CreateDirectory(_directory);

        var exception = Assert.Throws<FileNotFoundException>(() => RunReportWriter.Write(_directory));
        Assert.Contains("trace.jsonl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_reports_are_written_per_run_one_with_the_full_trace_and_one_without()
    {
        var harness = await RunOneTurnAsync();
        var paths = WriteArtefacts(harness);

        var (full, summary) = RunReportWriter.WriteAll(paths.Directory);

        Assert.Equal(RunReportWriter.ReportPath(paths.Directory), full);
        Assert.Equal(RunReportWriter.SummaryReportPath(paths.Directory), summary);
        Assert.EndsWith("report.md", full, StringComparison.Ordinal);
        Assert.EndsWith("report-summary.md", summary, StringComparison.Ordinal);

        var fullText = File.ReadAllText(full);
        var summaryText = File.ReadAllText(summary);

        // The readable sections appear in both.
        foreach (var section in new[] { "## Run details", "## Per-agent activity", "## Transcript", "## Final state" })
        {
            Assert.Contains(section, fullText, StringComparison.Ordinal);
            Assert.Contains(section, summaryText, StringComparison.Ordinal);
        }

        // Only the full report carries the exhaustive per-event trace; the summary points to it instead.
        Assert.Contains("## Trace\n", fullText.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("Raw event", fullText, StringComparison.Ordinal);
        Assert.DoesNotContain("Raw event", summaryText, StringComparison.Ordinal);
        Assert.Contains("Omitted from this summary", summaryText, StringComparison.Ordinal);

        // The summary is materially smaller — the whole point of it.
        Assert.True(summaryText.Length < fullText.Length,
            $"summary ({summaryText.Length}) should be smaller than the full report ({fullText.Length}).");
    }

    // ------------------------------------------------------------------------------------------
    // Container table vs. environmental objects (v0.9 cover was leaking into the "Container" table)
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// A room's <c>Objects</c> array holds both real containers and cover (v0.9) side by side. The final
    /// container table must show only the former, with its actual contents; cover has no open state or
    /// contents at all and belongs only under Environmental objects, with its Intact/Damaged/Destroyed state
    /// — a cover object leaking into the container table used to render as a permanently closed, empty
    /// "container", which is not what it is.
    /// </summary>
    [Fact]
    public void The_final_container_table_holds_only_real_containers_and_cover_appears_only_as_an_environmental_object()
    {
        var state = TestWorld.StateWith(
            [TestWorld.Chest(), TestWorld.Workbench()],
            TestWorld.Rowan(), TestWorld.Elara(), TestWorld.Vark(), TestWorld.Skrit());
        var paths = WriteMinimalArtefacts(state);

        var report = File.ReadAllText(RunReportWriter.Write(paths.Directory));

        var environmentalSection = report[report.IndexOf("## Environmental objects", StringComparison.Ordinal)..];
        var finalStateSection = report[report.IndexOf("## Final state", StringComparison.Ordinal)..];
        var containerTable = finalStateSection[..finalStateSection.IndexOf("<details>", StringComparison.Ordinal)];

        // Cover is reported once, under Environmental objects, with its own Intact/Damaged/Destroyed state —
        // never "open"/"closed", which means nothing for an object with no contents to hide.
        Assert.Contains("Overturned Mill Workbench", environmentalSection, StringComparison.Ordinal);
        Assert.Contains("Intact", environmentalSection, StringComparison.Ordinal);

        // The container table shows the real container and its contents, and never the cover object at all.
        Assert.Contains("Old Iron-Bound Chest", containerTable, StringComparison.Ordinal);
        Assert.Contains("Small Healing Potion", containerTable, StringComparison.Ordinal);
        Assert.DoesNotContain("Overturned Mill Workbench", containerTable, StringComparison.Ordinal);
    }

    /// <summary>
    /// A room whose objects are all plain containers — no cover at all, the shape every run had before v0.9 —
    /// must still render exactly as before: the container-vs-cover filter must not require a cover object to
    /// be present, or a type discriminator that pre-v0.9 output never had.
    /// </summary>
    [Fact]
    public void A_room_with_only_plain_containers_and_no_typed_environmental_objects_still_renders()
    {
        var state = TestWorld.StateWith([TestWorld.Chest()], TestWorld.Rowan(), TestWorld.Elara());
        var paths = WriteMinimalArtefacts(state);

        var report = File.ReadAllText(RunReportWriter.Write(paths.Directory));

        Assert.DoesNotContain("## Environmental objects", report, StringComparison.Ordinal);
        var finalStateSection = report[report.IndexOf("## Final state", StringComparison.Ordinal)..];
        var containerTable = finalStateSection[..finalStateSection.IndexOf("<details>", StringComparison.Ordinal)];
        Assert.Contains("Old Iron-Bound Chest", containerTable, StringComparison.Ordinal);
        Assert.Contains("Small Healing Potion", containerTable, StringComparison.Ordinal);
    }

    /// <summary>Writes just enough of a run's artefacts (run.json, an empty trace, final-state.json) to render a report over the given room/character state.</summary>
    private RunPaths WriteMinimalArtefacts(GameState state)
    {
        var paths = RunPaths.Create(_directory, DateTimeOffset.UnixEpoch);
        File.WriteAllText(paths.TraceJsonl, "");

        var profile = new AI.AgentModelProfile
        {
            AgentName = "test", Provider = AI.ModelProvider.Ollama, ModelId = "scripted-model"
        };

        RunArtifactWriter.WriteManifest(paths, new RunManifest
        {
            RunId = paths.RunId,
            StartedAt = DateTimeOffset.UnixEpoch,
            ApplicationVersion = "0.1.0.0",
            MachineOperatingSystem = "test",
            Scenario = TestWorld.TwoVsTwoScenario(),
            InitialState = state,
            AgentProfiles = new Dictionary<string, TracedAgentProfile> { ["Rowan"] = TracedAgentProfile.From(profile) },
            PromptVersions = new Dictionary<string, string>(),
            Harness = new HarnessOptions(),
            Seeds = new RunSeedInfo
            {
                MasterSeed = 1,
                SeedWasProvided = true,
                GameSeed = 1,
                AgentSeeds = new Dictionary<string, long>()
            }
        });

        RunArtifactWriter.WriteFinalState(paths, new FinalStateDocument
        {
            RunId = paths.RunId,
            CompletedAt = DateTimeOffset.UnixEpoch,
            TerminalCondition = "test",
            RoundsPlayed = 1,
            TraceEventCount = 0,
            State = state
        });

        return paths;
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
