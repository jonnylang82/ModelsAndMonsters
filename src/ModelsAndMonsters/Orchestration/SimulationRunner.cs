using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Knowledge;
using ModelsAndMonsters.Presentation;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Randomness;
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
/// Composes and runs one complete simulation: seed the world, introduce it, then take turns in a fixed
/// order until one team has no living members or a harness limit stops the encounter.
/// </summary>
/// <remarks>
/// Everything with a run lifetime — the trace sink, the engine, every agent, the narration log — is
/// created here so a run is self-contained and leaves one directory of evidence behind. There is no
/// shared hero or monster profile: each of the four characters resolves its own model profile and its
/// own derived seed from its id, and every resolved profile is recorded.
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

        // One master seed governs the whole run. When none is configured, generate a random one and
        // record it, so a random run is still replayable by setting Harness.Seed to the recorded value.
        var seedWasProvided = harness.Seed.HasValue;
        var masterSeed = harness.Seed ?? RunSeeds.NewRandomMaster();
        var gameSeed = RunSeeds.Derive(masterSeed, RunSeeds.GameKey);

        var initialState = ScenarioFactory.CreateInitialState(_scenario);
        var engine = new GameEngine(initialState, new SeededRng(gameSeed), new CombatRules(_options.Combat.GlancingBlowChance));

        // The application-owned knowledge ledger, seeded with the scenario's private backstory knowledge.
        // It is separate from authoritative game state: game state is current mechanical truth, this is the
        // growing record of who has observed what.
        var knowledge = new KnowledgeLedger();

        // The Dungeon Master and every character inherit the shared defaults, then apply their own
        // overrides; the resolved profile is what its model calls actually use, so that is what is recorded.
        var defaults = _options.Agents.Default;
        var dungeonMasterProfile = AgentModelProfile.FromOptions(
                DungeonMasterAgent.AgentIdentifier, _options.Agents.DungeonMaster.Overlay(defaults))
            with { Seed = RunSeeds.Derive(masterSeed, RunSeeds.DungeonMasterKey) };

        var characterProfiles = _scenario.Characters.ToDictionary(
            c => c.Id,
            c => ResolveCharacterProfile(c, defaults, masterSeed),
            StringComparer.OrdinalIgnoreCase);

        var agentSeeds = new Dictionary<string, long>
        {
            [DungeonMasterAgent.AgentIdentifier] = dungeonMasterProfile.Seed!.Value
        };
        foreach (var character in _scenario.Characters)
        {
            agentSeeds[character.Name] = characterProfiles[character.Id].Seed!.Value;
        }

        var seeds = new RunSeedInfo
        {
            MasterSeed = masterSeed,
            SeedWasProvided = seedWasProvided,
            GameSeed = gameSeed,
            AgentSeeds = agentSeeds
        };

        // Every agent gets its own client wrapper, its own profile and its own conversation.
        var clients = new List<IChatClient>();
        try
        {
            var dungeonMaster = new DungeonMasterAgent(
                dungeonMasterProfile,
                CreateTracingClient(dungeonMasterProfile, trace, clients),
                _prompts,
                harness.ProjectDungeonMasterContext);

            var characterPrompts = new CharacterPromptFactory(_prompts);

            // Fixed turn order: the scenario's character order, repeated every round.
            var turnOrder = _scenario.Characters
                .Select(definition =>
                {
                    var profile = characterProfiles[definition.Id];
                    return new CharacterAgent(
                        definition, profile, CreateTracingClient(profile, trace, clients),
                        characterPrompts.CreateSystemPrompt(definition, _scenario.Characters));
                })
                .ToList();

            var profilesForManifest = new Dictionary<string, TracedAgentProfile>
            {
                [DungeonMasterAgent.AgentIdentifier] = TracedAgentProfile.From(dungeonMasterProfile)
            };
            foreach (var agent in turnOrder)
            {
                profilesForManifest[agent.Name] = TracedAgentProfile.From(agent.Profile);
            }

            WriteManifest(paths, startedAt, initialState, profilesForManifest, seeds);

            trace.Emit(TraceEventType.RunStarted, new RunStartedPayload
            {
                RunId = paths.RunId,
                ScenarioId = _scenario.Id,
                OutputDirectory = paths.Directory,
                MasterSeed = seeds.MasterSeed,
                SeedWasProvided = seeds.SeedWasProvided,
                GameSeed = seeds.GameSeed
            });

            trace.Emit(TraceEventType.ScenarioSeeded, initialState);

            // Seed and trace the private backstory knowledge before play begins, so the record shows who
            // knew what from the outset and nobody else silently inherits it.
            var seed = KnowledgeSeeder.Seed(knowledge, _scenario, initialState);
            foreach (var fact in seed.CreatedFacts)
            {
                KnowledgeTracing.FactCreated(trace, fact, Knowledge.KnowledgeSource.Backstory, "seed", "harness");
            }

            foreach (var record in seed.LearnedRecords)
            {
                var fact = knowledge.FindFact(record.FactId);
                if (fact is null)
                {
                    continue;
                }

                var name = initialState.FindById(record.CharacterId)?.Name ?? record.CharacterId;
                KnowledgeTracing.FactLearned(trace, fact, record, name, "private", [record.CharacterId], "seed", "harness");
            }

            _console.RunHeader(paths.RunId, _scenario.Name, paths.Directory);
            _console.Notice(seedWasProvided
                ? $"Run seed: {masterSeed} (fixed)."
                : $"Run seed: {masterSeed} (random). {seeds.ReplayHint}");
            _console.Notice(DescribeTeams(initialState));

            var narrationLog = new NarrationLog();
            var formatter = new WorldStateFormatter(_prompts);
            var coordinator = new TurnCoordinator(
                engine, dungeonMaster, _prompts, formatter, narrationLog, knowledge, trace, _console, harness);

            return await RunLoopAsync(coordinator, engine, trace, paths, turnOrder, cancellationToken)
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

    private AgentModelProfile ResolveCharacterProfile(CharacterDefinition character, AgentProfileOptions defaults, long masterSeed)
    {
        var overrides = _options.Agents.Characters.TryGetValue(character.Id, out var configured)
            ? configured
            : new AgentProfileOptions();

        return AgentModelProfile.FromOptions(character.Name, overrides.Overlay(defaults))
            with { Seed = RunSeeds.Derive(masterSeed, RunSeeds.AgentKey(character.Id)) };
    }

    private void WriteReport(RunPaths paths)
    {
        try
        {
            var (full, summary) = RunReportWriter.WriteAll(paths.Directory);
            _console.Notice($"Report written to {full} (full) and {summary} (summary, no trace)");
        }
        catch (Exception ex)
        {
            // Reporting is a convenience over artefacts that are already safely on disk. It must never
            // replace the real outcome of the run, including a real exception on its way out.
            _console.Notice($"Could not write reports: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<SimulationSummary> RunLoopAsync(
        TurnCoordinator coordinator,
        IGameEngine engine,
        ExperimentTrace trace,
        RunPaths paths,
        IReadOnlyList<CharacterAgent> turnOrder,
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
        TerminalConditionResult? ending = null;

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

            // Fixed order, repeated each round. A dead actor is skipped inside the turn (no model call);
            // the terminal condition is checked after every turn, and the encounter stops the instant a
            // team is eliminated rather than finishing the round.
            var anythingHappened = false;
            foreach (var character in turnOrder)
            {
                var before = TerminalCondition.Evaluate(engine.State);
                if (before.IsOver)
                {
                    ending = before;
                    break;
                }

                turnNumber++;
                var turn = await coordinator.RunTurnAsync(character, round, turnNumber, cancellationToken)
                    .ConfigureAwait(false);

                anythingHappened |= turn.Outcome == TurnOutcome.ActionResolved;

                var after = TerminalCondition.Evaluate(engine.State);
                EmitTeamOutcome(trace, $"after {character.Name}'s turn", after);
                if (after.IsOver)
                {
                    ending = after;
                    break;
                }
            }

            if (ending is { IsOver: true })
            {
                terminalCondition = ending.Description;
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
                    Effect = "The encounter was stopped because no team could change anything."
                }, "harness");
                break;
            }

            // No round-end recap: each turn already narrates its own outcome, so a "where things stand"
            // narration here only restates the blow that was just described.
        }

        trace.SetPosition(roundsPlayed, turnNumber, "harness");

        var finalState = engine.State;

        // A final team-outcome evaluation, so the record ends with the standings that decided it even
        // when the run stopped on a harness limit rather than an elimination.
        EmitTeamOutcome(trace, "final", ending ?? TerminalCondition.Evaluate(finalState));

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

        WarnAboutContextSaturation(trace);
        WarnAboutReasoningStarvation(trace);
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

    private static void EmitTeamOutcome(ExperimentTrace trace, string trigger, TerminalConditionResult outcome) =>
        trace.Emit(TraceEventType.TeamOutcomeEvaluated, new TeamOutcomePayload
        {
            Trigger = trigger,
            IsOver = outcome.IsOver,
            Standings = [.. outcome.Standings.Select(s => new TeamStandingPayload
            {
                Team = s.Team,
                Living = s.Living,
                Total = s.Total
            })],
            WinningTeams = outcome.WinningTeams,
            EliminatedTeams = outcome.EliminatedTeams,
            Description = outcome.Description
        }, "harness");

    /// <summary>
    /// Surfaces silent history loss once, at the end, rather than interrupting the transcript. If this
    /// fires, some replies were formed from a conversation the provider had already trimmed.
    /// </summary>
    private void WarnAboutContextSaturation(ExperimentTrace trace)
    {
        var saturated = trace.CountOf(TraceEventType.ContextWindowSaturated);
        if (saturated == 0)
        {
            return;
        }

        _console.Notice(
            $"WARNING: on {saturated} model call(s) the provider reported processing far fewer input " +
            "tokens than we sent. It silently discarded the oldest messages. Raise ContextWindow, " +
            "shorten the run, or trim what each call sends.");
    }

    /// <summary>
    /// Warns when a reasoning model spent its whole output budget thinking. The symptom otherwise is
    /// only "the Dungeon Master said nothing", which reads as a bug rather than a configuration issue.
    /// </summary>
    private void WarnAboutReasoningStarvation(ExperimentTrace trace)
    {
        var starved = trace.CountOf(TraceEventType.ModelResponseTruncated);
        if (starved == 0)
        {
            return;
        }

        _console.Notice(
            $"WARNING: {starved} model call(s) hit the output-token limit. If an agent uses a reasoning " +
            "model, its thinking consumes the output budget and can leave no visible reply — set that " +
            "agent's Thinking to false, or raise its MaxOutputTokens.");
    }

    private static string DescribeTeams(GameState state)
    {
        var teams = state.Teams()
            .Select(team => $"{team}: {string.Join(", ", state.Characters.Where(c => Same(c.Team, team)).Select(c => c.Name))}");
        return $"Teams — {string.Join(" | ", teams)}.";
    }

    private static string SummariseEnding(GameState state, string terminalCondition)
    {
        var lines = state.Characters.Select(c => c.IsAlive
            ? $"{c.Name} ({c.Team}) survives with {c.Health} of {c.MaxHealth} health."
            : $"{c.Name} ({c.Team}) has fallen.");

        return $"{terminalCondition}\n{string.Join("\n", lines)}";
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private TracingChatClient CreateTracingClient(AgentModelProfile profile, ExperimentTrace trace, List<IChatClient> owned)
    {
        var inner = _chatClientFactory.Create(profile);
        var tracing = new TracingChatClient(inner, profile, trace);
        owned.Add(tracing);
        return tracing;
    }

    private void WriteManifest(
        RunPaths paths,
        DateTimeOffset startedAt,
        GameState initialState,
        IReadOnlyDictionary<string, TracedAgentProfile> agentProfiles,
        RunSeedInfo seeds) =>
        RunArtifactWriter.WriteManifest(paths, new RunManifest
        {
            RunId = paths.RunId,
            StartedAt = startedAt,
            ApplicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            MachineOperatingSystem = Environment.OSVersion.VersionString,
            Scenario = _scenario,
            InitialState = initialState,
            AgentProfiles = agentProfiles,
            PromptVersions = _prompts.Versions,
            Harness = _options.Harness,
            Seeds = seeds
        });
}
