using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The end-of-run storyteller driven through the whole <see cref="SimulationRunner"/> — no model service —
/// so the wiring itself is proven: the summariser is called once, after the encounter is fully decided, on a
/// deterministic <see cref="EncounterStoryBrief"/> built from the engine's own trace and final state, never
/// the raw public transcript; the model's two sections are followed by a deterministically appended Ending;
/// the story reaches the console/UI and <c>story.md</c>; and the call is traced with what it was built from.
/// </summary>
public sealed class EncounterStoryRunTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"mm-story-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private const string InventedTale =
        "## Opening\nTwo old rivals meet in a flooded cellar.\n\n## The Encounter\nSteel met steel until only one side stood.";

    [Fact]
    public async Task The_story_is_generated_once_after_the_encounter_ends_and_written_to_console_and_file()
    {
        var (summary, console) = await RunScriptedEncounterAsync();

        // A local model's own sampling call for this can visibly take longer than anything else in the
        // run; without a line announcing it, a run that has already printed its ending and then goes
        // quiet reads as hung rather than as still working.
        Assert.Contains(console.Lines, l => l.Contains("Generating the encounter's story", StringComparison.Ordinal));
        var generatingIndex = console.Lines.FindIndex(l => l.Contains("Generating the encounter's story", StringComparison.Ordinal));

        // The model's own text is followed by a deterministically appended Ending — never written by the
        // model, so it can never disagree with the terminal state.
        Assert.Contains(console.Lines, l => l.StartsWith($"story:{InventedTale}", StringComparison.Ordinal)
            && l.Contains("## Ending", StringComparison.Ordinal));

        var storyPath = Path.Combine(summary.OutputDirectory, "story.md");
        Assert.True(File.Exists(storyPath), "story.md must be written alongside the rest of the run's artefacts.");
        var written = await File.ReadAllTextAsync(storyPath);
        Assert.Contains(InventedTale, written, StringComparison.Ordinal);
        Assert.Contains("## Ending", written, StringComparison.Ordinal);
        Assert.Contains(summary.RunId, written, StringComparison.Ordinal);

        var storyEvents = ReadTraceData(summary.OutputDirectory, "EncounterStoryGenerated").ToList();
        var traced = Assert.Single(storyEvents);
        var tracedStory = traced.GetProperty("Story").GetString();
        Assert.StartsWith(InventedTale, tracedStory, StringComparison.Ordinal);
        Assert.Contains("## Ending", tracedStory, StringComparison.Ordinal);
        Assert.True(traced.GetProperty("BriefEventsTotal").GetInt32() > 0);
        Assert.False(traced.GetProperty("BriefTrimmed").GetBoolean());

        // The console output is generated after the ending is announced, so the story reads as the last
        // substantive thing the run says — and the "generating" notice arrives before the story itself,
        // in that same order, so it never becomes a hint after the fact.
        var storyIndex = console.Lines.FindIndex(l => l.StartsWith("story:", StringComparison.Ordinal));
        var endingIndex = console.Lines.FindIndex(l => l.StartsWith("ending:", StringComparison.Ordinal));
        Assert.True(storyIndex > endingIndex, "The story must be delivered after the encounter's own ending summary.");
        Assert.True(generatingIndex > endingIndex && generatingIndex < storyIndex,
            "The 'generating' notice must land between the ending and the finished story.");
    }

    [Fact]
    public async Task Disabling_the_flag_generates_no_story_and_needs_no_scripted_client()
    {
        var (summary, console) = await RunScriptedEncounterAsync(generateStory: false, scriptSummariser: false);

        Assert.DoesNotContain(console.Lines, l => l.StartsWith("story:", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(summary.OutputDirectory, "story.md")));
        Assert.Empty(ReadTraceData(summary.OutputDirectory, "EncounterStoryGenerated"));
    }

    /// <summary>
    /// A convenience over a run that has already fully and successfully completed: a failure in the
    /// summariser call itself — a model error here scripted as an empty client with nothing queued to
    /// answer with — must never turn a good run into a failed one. The run's own outcome, its trace, and
    /// its authoritative final state are exactly what they would have been without the story feature at all.
    /// </summary>
    [Fact]
    public async Task A_story_generation_failure_never_affects_run_completion_or_authoritative_state()
    {
        var (summary, console) = await RunScriptedEncounterAsync(generateStory: true, summariserThrows: true);

        Assert.Equal("Heroes win: Goblins has no active combatants remaining.", summary.TerminalCondition);
        Assert.Equal(CharacterDisposition.Dead, summary.FinalState.Characters.First(c => c.Id == TestWorld.VarkId).Disposition);
        Assert.Equal(CharacterDisposition.Dead, summary.FinalState.Characters.First(c => c.Id == TestWorld.SkritId).Disposition);

        Assert.Contains(console.Lines, l => l.StartsWith("notice:Could not generate the encounter story", StringComparison.Ordinal));
        Assert.DoesNotContain(console.Lines, l => l.StartsWith("story:", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(summary.OutputDirectory, "story.md")));
        Assert.Empty(ReadTraceData(summary.OutputDirectory, "EncounterStoryGenerated"));

        // The rest of the run's artefacts are unaffected — the report still renders over what is really there.
        Assert.True(File.Exists(Path.Combine(summary.OutputDirectory, "report.md")));
        Assert.True(File.Exists(Path.Combine(summary.OutputDirectory, "final-state.json")));
    }

    // ------------------------------------------------------------------------------------------
    // The output-token budget is ONE figure for every provider, never tuned down for a local model the
    // way it is elsewhere in this harness — this call has neither of the reasons that tightness exists
    // for (it runs once per whole run, not once per turn, and it is not sharing a window with a large
    // input). An earlier local-only figure (900) was the wrong instinct: a live Claude Sonnet run
    // measured FinishReason:length at exactly 900/900 output tokens with only 2,367 of a vastly larger
    // input window used — no window pressure at all, only a local-only cap applied somewhere it did not
    // belong. Same shape as the three earlier "local accommodation became a global rule" bugs the README
    // documents, so the fix is to stop distinguishing rather than to distinguish more precisely.
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Ollama")]
    [InlineData("Anthropic")]
    public async Task Every_provider_gets_the_same_generous_output_budget_regardless_of_window(string provider)
    {
        var (summary, _) = await RunScriptedEncounterAsync(provider: provider, contextWindow: 8192);

        Assert.Equal(3000, EncounterSummariserMaxOutputTokens(summary.OutputDirectory));
    }

    private async Task<(SimulationSummary Summary, RecordingConsole Console)> RunScriptedEncounterAsync(
        bool generateStory = true, bool scriptSummariser = true, string provider = "Ollama", int? contextWindow = null,
        bool summariserThrows = false)
    {
        var scenario = TestWorld.TwoVsTwoScenario();
        scenario.Characters.First(c => c.Id == TestWorld.VarkId).MaxHealth = 1;
        scenario.Characters.First(c => c.Id == TestWorld.SkritId).MaxHealth = 1;

        var options = new SimulationOptions
        {
            Agents = new AgentsOptions
            {
                Default = new AgentProfileOptions { Provider = provider, ModelId = "scripted-model", ContextWindow = contextWindow },
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
                Seed = 4242,
                MaxRounds = 2,
                MaxConsecutiveIdleRounds = 3,
                MaxQuestionsPerTurn = 2,
                MaxActionAttemptsPerTurn = 3,
                MaxModelCallsPerTurn = 8,
                UseIntentParser = false,
                SummariseHistory = false,
                EnableRulebookResolver = false,
                GenerateEncounterStory = generateStory,
                RunOutputDirectory = _directory
            },
            Combat = new CombatOptions { GlancingBlowChance = 0, CriticalHitChance = 0 }
        };

        var clients = BuildClients(scriptSummariser, summariserThrows);
        var factory = new ScriptedChatClientFactory(clients);
        var prompts = PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));
        var console = new RecordingConsole();

        var runner = new SimulationRunner(Options.Create(options), Options.Create(scenario), factory, prompts, console);

        var summary = await runner.RunAsync();
        return (summary, console);
    }

    private static Dictionary<string, ScriptedChatClient> BuildClients(bool scriptSummariser, bool summariserThrows = false)
    {
        // One round: Rowan kills Vark, Elara kills Skrit — the encounter ends inside round 1.
        var dungeonMaster = new ScriptedChatClient(
            ScriptedChatClient.Text("Four figures face off in the flooded cellar."),

            ScriptedChatClient.Call("dm-r1", DungeonMasterTools.AttackCharacterName,
                ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
            ScriptedChatClient.Text("Rowan's longsword ends it for the captain."),

            ScriptedChatClient.Call("dm-e1", DungeonMasterTools.AttackCharacterName,
                ("attacker", "Elara"), ("target", "Skrit"), ("weapon", "Iron Mace")),
            ScriptedChatClient.Text("Elara's mace fells the last goblin where it stands."));

        var rowan = new ScriptedChatClient(
            ScriptedChatClient.Call("r1", CharacterTools.TakeActionName, ("intent", "I bring my longsword down on Vark.")));
        var elara = new ScriptedChatClient(
            ScriptedChatClient.Call("e1", CharacterTools.TakeActionName, ("intent", "I bring my mace down on Skrit.")));

        var clients = new Dictionary<string, ScriptedChatClient>(StringComparer.OrdinalIgnoreCase)
        {
            [DungeonMasterAgent.AgentIdentifier] = dungeonMaster,
            ["Rowan"] = rowan,
            ["Elara"] = elara,
            ["Vark"] = new ScriptedChatClient(),
            ["Skrit"] = new ScriptedChatClient()
        };

        if (summariserThrows)
        {
            // Nothing queued: the harness's own scripted client throws on any call, standing in for a real
            // model error (a provider outage, a malformed reply) without depending on any specific exception.
            clients[EncounterSummariser.AgentIdentifier] = new ScriptedChatClient();
        }
        else if (scriptSummariser)
        {
            clients[EncounterSummariser.AgentIdentifier] = new ScriptedChatClient(ScriptedChatClient.Text(InventedTale));
        }

        return clients;
    }

    private static int? EncounterSummariserMaxOutputTokens(string runDirectory)
    {
        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDirectory, "run.json"))).RootElement;
        var profile = root.GetProperty("AgentProfiles").GetProperty(EncounterSummariser.AgentIdentifier);
        return profile.TryGetProperty("MaxOutputTokens", out var tokens) && tokens.ValueKind != JsonValueKind.Null
            ? tokens.GetInt32()
            : null;
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
