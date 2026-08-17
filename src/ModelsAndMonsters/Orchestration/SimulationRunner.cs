using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Presentation;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Orchestration;

public sealed record SimulationSummary
{
    public required string RunId { get; init; }

    public required string OutputDirectory { get; init; }

    public required string TerminalCondition { get; init; }

    public required int RoundsPlayed { get; init; }

    public required long TraceEventCount { get; init; }

    public required GameState FinalState { get; init; }
}

/// <summary>
/// Composes and runs one complete simulation: seed the world, introduce it, then alternate turns
/// until someone falls or the round limit is reached.
/// </summary>
/// <remarks>
/// Everything with a run lifetime — the trace sink, the engine, the three agents, the narration log —
/// is created here so a run is self-contained and leaves one directory of evidence behind.
/// </remarks>
public sealed class SimulationRunner
{
    private readonly SimulationOptions _options;
    private readonly ScenarioDefinition _scenario;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly PromptLibrary _prompts;
    private readonly IGameConsole _console;

    public SimulationRunner(
        IOptions<SimulationOptions> options,
        IOptions<ScenarioDefinition> scenario,
        IChatClientFactory chatClientFactory,
        PromptLibrary prompts,
        IGameConsole console)
    {
        _options = options.Value;
        _scenario = scenario.Value;
        _chatClientFactory = chatClientFactory;
        _prompts = prompts;
        _console = console;
    }

    public async Task<SimulationSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var harness = _options.Harness;
        var paths = RunPaths.Create(harness.RunOutputDirectory, startedAt);

        var sink = new JsonlTraceSink(paths.TraceJsonl);
        var trace = new ExperimentTrace(paths.RunId, sink);

        var initialState = ScenarioFactory.CreateInitialState(_scenario);
        var engine = new GameEngine(initialState);

        var heroDefinition = RequireCharacter(CharacterRole.Hero);
        var monsterDefinition = RequireCharacter(CharacterRole.Monster);

        var dungeonMasterProfile = AgentModelProfile.FromOptions(DungeonMasterAgent.AgentIdentifier, _options.Agents.DungeonMaster);
        var heroProfile = AgentModelProfile.FromOptions(heroDefinition.Name, _options.Agents.Hero);
        var monsterProfile = AgentModelProfile.FromOptions(monsterDefinition.Name, _options.Agents.Monster);

        // Every agent gets its own client wrapper, its own profile and its own conversation.
        var clients = new List<IChatClient>();
        try
        {
            var dungeonMaster = new DungeonMasterAgent(
                dungeonMasterProfile,
                CreateTracingClient(dungeonMasterProfile, trace, clients),
                _prompts,
                harness.IsolateAdjudicationContext);

            var characterPrompts = new CharacterPromptFactory(_prompts);
            var hero = new CharacterAgent(
                heroDefinition, heroProfile, CreateTracingClient(heroProfile, trace, clients),
                characterPrompts.CreateSystemPrompt(heroDefinition));
            var monster = new CharacterAgent(
                monsterDefinition, monsterProfile, CreateTracingClient(monsterProfile, trace, clients),
                characterPrompts.CreateSystemPrompt(monsterDefinition));

            WriteManifest(paths, startedAt, initialState, dungeonMasterProfile, heroProfile, monsterProfile);

            trace.Emit(TraceEventType.RunStarted, new RunStartedPayload
            {
                RunId = paths.RunId,
                ScenarioId = _scenario.Id,
                OutputDirectory = paths.Directory
            });

            trace.Emit(TraceEventType.ScenarioSeeded, initialState);

            _console.RunHeader(paths.RunId, _scenario.Name, paths.Directory);

            var narrationLog = new NarrationLog();
            var formatter = new WorldStateFormatter(_prompts);
            var coordinator = new TurnCoordinator(
                engine, dungeonMaster, _prompts, formatter, narrationLog, trace, _console, harness);

            return await RunLoopAsync(coordinator, engine, trace, paths, hero, monster, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            trace.Emit(TraceEventType.RunFailed, new RunFailedPayload
            {
                ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
                Message = ex.Message,
                StackTrace = ex.StackTrace
            });

            throw;
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }

            // The report is rendered from the finished artefacts, so the trace file must be closed
            // first. It is written even when the run failed, because a failed run is still evidence.
            sink.Dispose();
            WriteReport(paths);
        }
    }

    private void WriteReport(RunPaths paths)
    {
        try
        {
            var reportPath = RunReportWriter.Write(paths.Directory);
            _console.Notice($"Report written to {reportPath}");
        }
        catch (Exception ex)
        {
            // Reporting is a convenience over artefacts that are already safely on disk. It must never
            // replace the real outcome of the run, including a real exception on its way out.
            _console.Notice($"Could not write report.md: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<SimulationSummary> RunLoopAsync(
        TurnCoordinator coordinator,
        IGameEngine engine,
        ExperimentTrace trace,
        RunPaths paths,
        CharacterAgent hero,
        CharacterAgent monster,
        CancellationToken cancellationToken)
    {
        var harness = _options.Harness;
        var roundsPlayed = 0;
        string terminalCondition;

        engine.CurrentRound = 1;
        trace.SetPosition(1, 0, "harness");

        await coordinator.NarrateSituationAsync(
            "This is the opening of the encounter. Set the scene and introduce everyone present.",
            "opening",
            cancellationToken).ConfigureAwait(false);

        var turnNumber = 0;
        var idleRounds = 0;

        while (true)
        {
            var round = roundsPlayed + 1;
            if (round > harness.MaxRounds)
            {
                terminalCondition = $"Round limit reached ({harness.MaxRounds} rounds).";
                trace.Emit(TraceEventType.HarnessLimitReached, new HarnessLimitPayload
                {
                    Limit = nameof(HarnessOptions.MaxRounds),
                    Value = harness.MaxRounds,
                    Effect = "The encounter was stopped before a terminal condition was reached."
                }, "harness");
                break;
            }

            engine.CurrentRound = round;
            roundsPlayed = round;

            trace.SetPosition(round, turnNumber, "harness");
            trace.Emit(TraceEventType.RoundStarted, new RoundStartedPayload { Round = round, State = engine.State });
            _console.RoundHeader(round);

            // Fixed order, no initiative: Hero then Monster.
            var anythingHappened = false;
            foreach (var character in new[] { hero, monster })
            {
                if (IsEncounterOver(engine.State, out _))
                {
                    break;
                }

                turnNumber++;
                var turn = await coordinator.RunTurnAsync(character, round, turnNumber, cancellationToken)
                    .ConfigureAwait(false);

                anythingHappened |= turn.Outcome == TurnOutcome.ActionResolved;
            }

            if (IsEncounterOver(engine.State, out var fallen))
            {
                terminalCondition = $"{fallen!.Name} was defeated.";
                break;
            }

            idleRounds = anythingHappened ? 0 : idleRounds + 1;
            if (idleRounds >= harness.MaxConsecutiveIdleRounds)
            {
                terminalCondition = $"Stalemate: {idleRounds} consecutive rounds in which nothing took effect.";
                trace.Emit(TraceEventType.HarnessLimitReached, new HarnessLimitPayload
                {
                    Limit = nameof(HarnessOptions.MaxConsecutiveIdleRounds),
                    Value = harness.MaxConsecutiveIdleRounds,
                    Effect = "The encounter was stopped because neither character could change anything."
                }, "harness");
                break;
            }

            trace.SetPosition(round, turnNumber, "harness");
            await coordinator.NarrateSituationAsync(
                "The round has ended. Describe where things now stand.",
                "round-end",
                cancellationToken).ConfigureAwait(false);
        }

        trace.SetPosition(roundsPlayed, turnNumber, "harness");

        await coordinator.NarrateSituationAsync(
            "The encounter is over. Describe how it ends.",
            "encounter-end",
            cancellationToken).ConfigureAwait(false);

        var finalState = engine.State;
        _console.Ending(SummariseEnding(finalState, terminalCondition));

        trace.Emit(TraceEventType.RunCompleted, new RunCompletedPayload
        {
            TerminalCondition = terminalCondition,
            RoundsPlayed = roundsPlayed,
            Survivors = [.. finalState.Characters.Where(c => c.IsAlive).Select(c => c.Name)],
            Casualties = [.. finalState.Characters.Where(c => !c.IsAlive).Select(c => c.Name)],
            FinalState = finalState
        }, "harness");

        RunArtifactWriter.WriteFinalState(paths, new FinalStateDocument
        {
            RunId = paths.RunId,
            CompletedAt = DateTimeOffset.UtcNow,
            TerminalCondition = terminalCondition,
            RoundsPlayed = roundsPlayed,
            TraceEventCount = trace.EventCount,
            State = finalState
        });

        _console.Notice($"Run complete. Trace written to {paths.Directory}");

        return new SimulationSummary
        {
            RunId = paths.RunId,
            OutputDirectory = paths.Directory,
            TerminalCondition = terminalCondition,
            RoundsPlayed = roundsPlayed,
            TraceEventCount = trace.EventCount,
            FinalState = finalState
        };
    }

    /// <summary>v0.1 terminal condition: anyone reaching zero health ends the encounter.</summary>
    private static bool IsEncounterOver(GameState state, out Character? fallen)
    {
        fallen = state.Characters.FirstOrDefault(c => !c.IsAlive);
        return fallen is not null;
    }

    private static string SummariseEnding(GameState state, string terminalCondition)
    {
        var lines = state.Characters.Select(c => c.IsAlive
            ? $"{c.Name} survives with {c.Health} of {c.MaxHealth} health."
            : $"{c.Name} has fallen.");

        return $"{terminalCondition}\n{string.Join("\n", lines)}";
    }

    private TracingChatClient CreateTracingClient(AgentModelProfile profile, ExperimentTrace trace, List<IChatClient> owned)
    {
        var inner = _chatClientFactory.Create(profile);
        var tracing = new TracingChatClient(inner, profile, trace);
        owned.Add(tracing);
        return tracing;
    }

    private CharacterDefinition RequireCharacter(CharacterRole role) =>
        _scenario.Characters.FirstOrDefault(c => string.Equals(c.Role, role.ToString(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Scenario '{_scenario.Id}' does not define a {role}.");

    private void WriteManifest(
        RunPaths paths,
        DateTimeOffset startedAt,
        GameState initialState,
        AgentModelProfile dungeonMaster,
        AgentModelProfile hero,
        AgentModelProfile monster) =>
        RunArtifactWriter.WriteManifest(paths, new RunManifest
        {
            RunId = paths.RunId,
            StartedAt = startedAt,
            ApplicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            MachineOperatingSystem = Environment.OSVersion.VersionString,
            Scenario = _scenario,
            InitialState = initialState,
            AgentProfiles = new Dictionary<string, TracedAgentProfile>
            {
                [DungeonMasterAgent.AgentIdentifier] = TracedAgentProfile.From(dungeonMaster),
                ["Hero"] = TracedAgentProfile.From(hero),
                ["Monster"] = TracedAgentProfile.From(monster)
            },
            PromptVersions = _prompts.Versions,
            Harness = _options.Harness
        });
}
