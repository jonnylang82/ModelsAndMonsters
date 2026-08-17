using ModelsAndMonsters.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Presentation;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Randomness;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>Collects trace events in memory so tests can assert on what was recorded.</summary>
internal sealed class RecordingTraceSink : ITraceSink
{
    public List<TraceEvent> Events { get; } = [];

    public void Write(TraceEvent traceEvent) => Events.Add(traceEvent);

    public IEnumerable<TraceEvent> OfType(TraceEventType type) => Events.Where(e => e.EventType == type);

    public IEnumerable<T> Payloads<T>(TraceEventType type) => OfType(type).Select(e => e.Data).OfType<T>();

    public void Dispose()
    {
    }
}

internal sealed class RecordingConsole : IGameConsole
{
    public List<string> Lines { get; } = [];

    public void RunHeader(string runId, string scenarioName, string outputDirectory) => Lines.Add($"header:{runId}");

    public void RoundHeader(int round) => Lines.Add($"round:{round}");

    public void DungeonMaster(string text) => Lines.Add($"dm:{text}");

    public void CharacterAsks(string characterName, string question) => Lines.Add($"asks:{characterName}:{question}");

    public void CharacterActs(string characterName, string intent) => Lines.Add($"acts:{characterName}:{intent}");

    public void CharacterPasses(string characterName, string reason) => Lines.Add($"passes:{characterName}:{reason}");

    public void CharacterRefused(string characterName, string explanation) => Lines.Add($"refused:{characterName}:{explanation}");

    public void Notice(string text) => Lines.Add($"notice:{text}");

    public void Ending(string text) => Lines.Add($"ending:{text}");
}

/// <summary>
/// Wires a real <see cref="TurnCoordinator"/>, real agents, a real engine and real tracing against
/// scripted models. Only the model is fake.
/// </summary>
internal sealed class OrchestrationHarness
{
    private static readonly PromptLibrary SharedPrompts =
        PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));

    public OrchestrationHarness(
        ScriptedChatClient dungeonMasterClient,
        ScriptedChatClient heroClient,
        ScriptedChatClient monsterClient,
        HarnessOptions? limits = null,
        GameState? initialState = null)
    {
        DungeonMasterClient = dungeonMasterClient;
        HeroClient = heroClient;
        MonsterClient = monsterClient;
        Limits = limits ?? new HarnessOptions { MaxQuestionsPerTurn = 2, MaxActionAttemptsPerTurn = 3, MaxModelCallsPerTurn = 8 };

        // Orchestration tests assert on damage, so the engine always lands full hits.
        Engine = new GameEngine(
            initialState ?? TestWorld.State(TestWorld.Hero(), TestWorld.Monster()),
            new SeededRng(1),
            CombatRules.NoGlancing);
        Trace = new ExperimentTrace("test-run", Sink);

        var scenario = TestWorld.Scenario();
        var heroDefinition = scenario.Characters[0];
        var monsterDefinition = scenario.Characters[1];
        var characterPrompts = new CharacterPromptFactory(SharedPrompts);

        DungeonMaster = new DungeonMasterAgent(
            Profile(DungeonMasterAgent.AgentIdentifier),
            new TracingChatClient(dungeonMasterClient, Profile(DungeonMasterAgent.AgentIdentifier), Trace),
            SharedPrompts,
            Limits.ProjectDungeonMasterContext);

        Hero = new CharacterAgent(
            heroDefinition,
            Profile(heroDefinition.Name),
            new TracingChatClient(heroClient, Profile(heroDefinition.Name), Trace),
            characterPrompts.CreateSystemPrompt(heroDefinition, scenario.Characters));

        Monster = new CharacterAgent(
            monsterDefinition,
            Profile(monsterDefinition.Name),
            new TracingChatClient(monsterClient, Profile(monsterDefinition.Name), Trace),
            characterPrompts.CreateSystemPrompt(monsterDefinition, scenario.Characters));

        Coordinator = new TurnCoordinator(
            Engine, DungeonMaster, SharedPrompts, new WorldStateFormatter(SharedPrompts),
            NarrationLog, Trace, Console, Limits);
    }

    public RecordingTraceSink Sink { get; } = new();

    public RecordingConsole Console { get; } = new();

    public NarrationLog NarrationLog { get; } = new();

    public ExperimentTrace Trace { get; }

    public GameEngine Engine { get; }

    public HarnessOptions Limits { get; }

    public DungeonMasterAgent DungeonMaster { get; }

    public CharacterAgent Hero { get; }

    public CharacterAgent Monster { get; }

    public TurnCoordinator Coordinator { get; }

    public ScriptedChatClient DungeonMasterClient { get; }

    public ScriptedChatClient HeroClient { get; }

    public ScriptedChatClient MonsterClient { get; }

    public Task<TurnResult> RunHeroTurn(int round = 1, int turn = 1) =>
        Coordinator.RunTurnAsync(Hero, round, turn, CancellationToken.None);

    public Task<TurnResult> RunMonsterTurn(int round = 1, int turn = 2) =>
        Coordinator.RunTurnAsync(Monster, round, turn, CancellationToken.None);

    /// <summary>Declared context window, so saturation detection has something to compare against.</summary>
    public const int TestContextWindow = 8192;

    private static AgentModelProfile Profile(string agentName) => new()
    {
        AgentName = agentName,
        Provider = ModelProvider.Ollama,
        ModelId = "scripted-model",
        Temperature = 0.5f,
        TopK = 20,
        Seed = 42,
        ContextWindow = TestContextWindow
    };
}
