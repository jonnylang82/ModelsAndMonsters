using ModelsAndMonsters.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Knowledge;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Randomness;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// Wires a real <see cref="TurnCoordinator"/>, a real engine, a real Dungeon Master and four real
/// character agents against scripted models, so the multi-actor orchestration can be driven a turn at a
/// time without any network. Only the model is fake.
/// </summary>
internal sealed class MultiActorHarness
{
    private static readonly PromptLibrary SharedPrompts =
        PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));

    private readonly Dictionary<string, CharacterAgent> _agentsByName = new(StringComparer.OrdinalIgnoreCase);

    public MultiActorHarness(
        ScriptedChatClient dungeonMasterClient,
        IReadOnlyDictionary<string, ScriptedChatClient> characterClients,
        HarnessOptions? limits = null,
        GameState? initialState = null,
        IRng? rng = null,
        CombatRules? rules = null,
        ScenarioDefinition? scenario = null)
    {
        DungeonMasterClient = dungeonMasterClient;
        CharacterClients = characterClients;
        Limits = limits ?? new HarnessOptions { MaxQuestionsPerTurn = 2, MaxActionAttemptsPerTurn = 3, MaxModelCallsPerTurn = 8 };
        Trace = new ExperimentTrace("test-run", Sink);

        Engine = new GameEngine(
            initialState ?? TestWorld.TwoVsTwoState(),
            rng ?? new SeededRng(1),
            rules ?? CombatRules.NoGlancing);

        var resolvedScenario = scenario ?? TestWorld.TwoVsTwoScenario();
        var definitions = resolvedScenario.Characters;
        var characterPrompts = new CharacterPromptFactory(SharedPrompts);

        // Seed the ledger with any private backstory knowledge the scenario grants, from the same state the
        // engine started with, so tests exercise the real seeding path.
        KnowledgeSeeder.Seed(Ledger, resolvedScenario, Engine.State);

        DungeonMaster = new DungeonMasterAgent(
            Profile(DungeonMasterAgent.AgentIdentifier),
            new TracingChatClient(dungeonMasterClient, Profile(DungeonMasterAgent.AgentIdentifier), Trace),
            SharedPrompts,
            Limits.ProjectDungeonMasterContext);

        foreach (var definition in definitions)
        {
            if (!characterClients.TryGetValue(definition.Name, out var client))
            {
                // A character that is never given a turn in a test needs no scripted responses.
                client = new ScriptedChatClient();
            }

            var agent = new CharacterAgent(
                definition,
                Profile(definition.Name),
                new TracingChatClient(client, Profile(definition.Name), Trace),
                characterPrompts.CreateSystemPrompt(definition, definitions));

            _agentsByName[definition.Name] = agent;
        }

        Coordinator = new TurnCoordinator(
            Engine, DungeonMaster, SharedPrompts, new WorldStateFormatter(SharedPrompts),
            NarrationLog, Ledger, Trace, Console, Limits);
    }

    public RecordingTraceSink Sink { get; } = new();

    public RecordingConsole Console { get; } = new();

    public NarrationLog NarrationLog { get; } = new();

    /// <summary>The knowledge ledger the coordinator uses, seeded from the scenario's backstory knowledge.</summary>
    public KnowledgeLedger Ledger { get; } = new();

    public ExperimentTrace Trace { get; }

    public GameEngine Engine { get; }

    public HarnessOptions Limits { get; }

    public DungeonMasterAgent DungeonMaster { get; }

    public TurnCoordinator Coordinator { get; }

    public ScriptedChatClient DungeonMasterClient { get; }

    public IReadOnlyDictionary<string, ScriptedChatClient> CharacterClients { get; }

    public CharacterAgent Agent(string name) => _agentsByName[name];

    public ScriptedChatClient Client(string name) => CharacterClients[name];

    public Task<TurnResult> RunTurn(string characterName, int round = 1, int turn = 1) =>
        Coordinator.RunTurnAsync(_agentsByName[characterName], round, turn, CancellationToken.None);

    /// <summary>Opening narration on the shared public channel, as the runner does before round one.</summary>
    public Task NarrateOpeningAsync() =>
        Coordinator.NarrateSituationAsync("Opening.", "opening", CancellationToken.None);

    public const int TestContextWindow = 8192;

    private static AgentModelProfile Profile(string agentName) => new()
    {
        AgentName = agentName,
        Provider = ModelProvider.Ollama,
        ModelId = "scripted-model",
        Temperature = 0.5f,
        Seed = 42,
        ContextWindow = TestContextWindow
    };

    /// <summary>Convenience for building the character-client map by name.</summary>
    public static Dictionary<string, ScriptedChatClient> Clients(params (string Name, ScriptedChatClient Client)[] clients) =>
        clients.ToDictionary(c => c.Name, c => c.Client, StringComparer.OrdinalIgnoreCase);
}
