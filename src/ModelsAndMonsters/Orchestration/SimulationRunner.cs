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
using ModelsAndMonsters.Rulebook;
using ModelsAndMonsters.Rulebook.Selection;
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
    /// <summary>
    /// The intent parser's sampling temperature. Above zero on purpose — see where it is applied — and
    /// below the first transient-retry floor (0.4), so a retry still genuinely moves the draw rather than
    /// re-sending the same one at the same temperature with only a new seed.
    /// </summary>
    public const float IntentParserTemperature = 0.3f;
    private readonly SimulationOptions _options;
    private readonly ScenarioDefinition _scenario;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly PromptLibrary _prompts;
    private readonly IGameConsole _console;
    private readonly ITraceSink? _additionalSink;

    /// <summary>The dependency-injected constructor used by the CLI. No live sink; the trace goes to file only.</summary>
    public SimulationRunner(
        IOptions<SimulationOptions> options,
        IOptions<ScenarioDefinition> scenario,
        IChatClientFactory chatClientFactory,
        PromptLibrary prompts,
        IGameConsole console)
        : this(options.Value, scenario.Value, chatClientFactory, prompts, console, additionalSink: null)
    {
    }

    /// <summary>
    /// Constructs a runner directly, for callers that build one per run — a host serving a live view passes a
    /// per-run <paramref name="console"/> and an <paramref name="additionalSink"/> that pushes events to that
    /// view alongside the file trace.
    /// </summary>
    public SimulationRunner(
        SimulationOptions options,
        ScenarioDefinition scenario,
        IChatClientFactory chatClientFactory,
        PromptLibrary prompts,
        IGameConsole console,
        ITraceSink? additionalSink)
    {
        _options = options;
        _scenario = scenario;
        _chatClientFactory = chatClientFactory;
        _prompts = prompts;
        _console = console;
        _additionalSink = additionalSink;
    }

    public async Task<SimulationSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var harness = _options.Harness;
        var paths = RunPaths.Create(harness.RunOutputDirectory, startedAt);

        // The JSONL file is the authoritative trace; a live view (if attached) receives the same events too.
        var fileSink = new JsonlTraceSink(paths.TraceJsonl);
        ITraceSink sink = _additionalSink is null ? fileSink : new CompositeTraceSink(fileSink, _additionalSink);
        var trace = new ExperimentTrace(paths.RunId, sink);

        // One master seed governs the whole run. When none is configured, generate a random one and
        // record it, so a random run is still replayable by setting Harness.Seed to the recorded value.
        var seedWasProvided = harness.Seed.HasValue;
        var masterSeed = harness.Seed ?? RunSeeds.NewRandomMaster();
        var gameSeed = RunSeeds.Derive(masterSeed, RunSeeds.GameKey);

        var initialState = ScenarioFactory.CreateInitialState(_scenario);
        var engine = new GameEngine(initialState, new SeededRng(gameSeed),
            new CombatRules(
                _options.Combat.GlancingBlowChance,
                _options.Combat.BaseStealChance,
                _options.Combat.CriticalHitChance,
                _options.Combat.BaseIntimidationChance));

        // The application-owned knowledge ledger, seeded with the scenario's private backstory knowledge.
        // It is separate from authoritative game state: game state is current mechanical truth, this is the
        // growing record of who has observed what.
        var knowledge = new KnowledgeLedger();

        // The Dungeon Master and every character inherit the shared defaults, then apply their own
        // overrides; the resolved profile is what its model calls actually use, so that is what is recorded.
        var defaults = _options.Agents.Default;
        var enforceWindow = harness.EnforceContextWindowOnHostedModels;
        var dungeonMasterProfile = AgentModelProfile.FromOptions(
                DungeonMasterAgent.AgentIdentifier, _options.Agents.DungeonMaster.Overlay(defaults), enforceWindow)
            with { Seed = RunSeeds.Derive(masterSeed, RunSeeds.DungeonMasterKey) };

        var characterProfiles = _scenario.Characters.ToDictionary(
            c => c.Id,
            c => ResolveCharacterProfile(c, defaults, masterSeed, enforceWindow),
            StringComparer.OrdinalIgnoreCase);

        // The intent parser reuses the Dungeon Master's model, with reasoning off and a small output budget
        // — it only ever emits a few tool calls. It is context-free, so the window is ample; a derived seed
        // keeps the whole run replayable.
        //
        // It reads at a LOW temperature rather than a greedy zero, which is a deliberate change from v0.7.
        // Greedy decoding on qwen3.5 is fragile in a way this project measured: a run died at round 5 when
        // the parser 500'd three times on the same malformed tool-call XML, because a temperature-0 draw
        // reproduces itself and the old retry floor of 0.1 was not far enough off greedy to escape. In the
        // same trace a character sampling at 0.8 took two 500s and shook them off. Qwen's own published
        // sampling guidance never recommends below 0.6 for any task or mode; zero was our number, not
        // theirs, and it bought a determinism a fixed seed already provides.
        var intentParserProfile = dungeonMasterProfile with
        {
            AgentName = IntentParser.AgentIdentifier,
            Temperature = IntentParserTemperature,
            Effort = ReasoningEffort.None,
            Thinking = false,
            MaxOutputTokens = 500,
            Seed = RunSeeds.Derive(masterSeed, "agent:intent-parser")
        };

        // The history summariser also reuses the DM's model, at a low temperature for a steady recap and a
        // small output budget. Context-free like the parser; a derived seed keeps the run replayable.
        var historySummariserProfile = dungeonMasterProfile with
        {
            AgentName = HistorySummariser.AgentIdentifier,
            Temperature = 0.3f,
            Effort = ReasoningEffort.None,
            Thinking = false,
            MaxOutputTokens = 400,
            Seed = RunSeeds.Derive(masterSeed, "agent:history-summariser")
        };

        // The Rulebook Resolver is an independently configurable agent (its own provider/model may be set),
        // overlaid on the shared defaults. It runs stateless at a low temperature by default with reasoning
        // off and a small output budget — it only ever emits a small JSON object — and a derived seed keeps
        // the whole run replayable.
        var resolverConfig = _options.Agents.RulebookResolver;
        var resolverBase = AgentModelProfile.FromOptions(
            "RulebookResolver", resolverConfig.Overlay(defaults), enforceWindow);
        var rulebookResolverProfile = resolverBase with
        {
            Temperature = resolverConfig.Temperature ?? 0.1f,
            Effort = resolverConfig.Effort is null ? ReasoningEffort.None : resolverBase.Effort,
            Thinking = false,
            MaxOutputTokens = resolverConfig.MaxOutputTokens ?? harness.RulebookOutputTokens,
            Seed = RunSeeds.Derive(masterSeed, "agent:rulebook-resolver")
        };

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

            // The prose-fallback intent parser gets its own stateless client; null when the flag is off, in
            // which case the coordinator keeps the older speech-retry / recovery / nudge path.
            var intentParser = harness.UseIntentParser
                ? new IntentParser(intentParserProfile, CreateTracingClient(intentParserProfile, trace, clients), _prompts)
                : null;

            // The history summariser (its own stateless client), when enabled; null keeps histories untrimmed.
            var historySummariser = harness.SummariseHistory
                ? new HistorySummariser(historySummariserProfile, CreateTracingClient(historySummariserProfile, trace, clients), _prompts)
                : null;

            // The bounded rulebook stage (v0.6): a deterministic catalog + retriever + cache, and the stateless
            // resolver on its own traced client. Null when disabled, in which case the DM gets the full tool set.
            RulebookConsultant? rulebook = null;
            if (harness.EnableRulebookResolver)
            {
                var catalog = new RuleCatalog();
                var retriever = new RuleRetriever(catalog, harness.RulebookMaxCards, harness.RulebookMaxInputChars);
                // Fail loudly if the whole book cannot be sent to this resolver with room to answer in. The
                // provider does not error on an over-full request — it returns a truncated reply that reads
                // downstream as malformed guidance — so the only place to catch it is before the run starts.
                RulebookRequestBudget.Validate(catalog, _prompts, rulebookResolverProfile, harness.RulebookOutputTokens);

                var validator = new RuleGuidanceValidator(catalog);
                var cache = new RuleGuidanceCache();
                var resolver = new RulebookResolver(
                    rulebookResolverProfile,
                    CreateTracingClient(rulebookResolverProfile, trace, clients),
                    _prompts);
                // The card-selection strategy. Null keeps the shipped path (the whole book, every time);
                // anything else is experimental, configured explicitly, and falls back to that same path
                // whenever it is not confident.
                var selector = RuleSelectorFactory.Create(
                    harness, _options.Providers, catalog, rulebookResolverProfile,
                    profile => CreateTracingClient(profile, trace, clients));

                rulebook = new RulebookConsultant(catalog, retriever, resolver, validator, cache, trace,
                    new RulebookConsultationOptions(
                        harness.RulebookMaxCards, harness.RulebookMaxInputChars,
                        harness.RulebookOutputTokens, harness.RulebookCacheEnabled),
                    selector);
            }

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
            if (harness.EnableRulebookResolver)
            {
                profilesForManifest["RulebookResolver"] = TracedAgentProfile.From(rulebookResolverProfile);
            }

            // The derived agents are recorded too. They are not configured directly — each is the Dungeon
            // Master's profile with fixed overrides — which is exactly why they were missing from the
            // manifest, and exactly why they need to be in it: nothing else states what they actually ran
            // with. A run where the intent parser's temperature mattered could not be read without them.
            if (harness.UseIntentParser)
            {
                profilesForManifest[IntentParser.AgentIdentifier] = TracedAgentProfile.From(intentParserProfile);
            }

            if (harness.SummariseHistory)
            {
                profilesForManifest[HistorySummariser.AgentIdentifier] =
                    TracedAgentProfile.From(historySummariserProfile);
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

            // Seed and trace the initial knowledge before play begins, so the record shows who knew what from
            // the outset and nobody silently inherits anything. Two kinds: private backstory knowledge (a
            // character's own containers), and the public observation everyone present makes of what everyone
            // else is openly carrying — the latter must be recorded as a public event, not as backstory.
            var seed = KnowledgeSeeder.Seed(knowledge, _scenario, initialState);
            var presentIds = initialState.Characters.Where(c => c.IsPresent).Select(c => c.Id).ToList();
            foreach (var fact in seed.CreatedFacts)
            {
                var createdBySource = fact.FactType == FactType.ItemPossession
                    ? Knowledge.KnowledgeSource.PublicEvent
                    : Knowledge.KnowledgeSource.Backstory;
                KnowledgeTracing.FactCreated(trace, fact, createdBySource, "seed", "harness");
            }

            foreach (var record in seed.LearnedRecords)
            {
                var fact = knowledge.FindFact(record.FactId);
                if (fact is null)
                {
                    continue;
                }

                var name = initialState.FindById(record.CharacterId)?.Name ?? record.CharacterId;

                // An openly-carried item is a public observation the whole room made at the outset; the
                // backstory container knowledge is private to the one character who began the fight with it.
                var isPublic = fact.FactType == FactType.ItemPossession;
                var visibility = isPublic ? "public" : "private";
                IReadOnlyList<string> recipients = isPublic ? presentIds : [record.CharacterId];
                KnowledgeTracing.FactLearned(trace, fact, record, name, visibility, recipients, "seed", "harness");
            }

            _console.RunHeader(paths.RunId, _scenario.Name, paths.Directory);
            _console.Notice(seedWasProvided
                ? $"Run seed: {masterSeed} (fixed)."
                : $"Run seed: {masterSeed} (random). {seeds.ReplayHint}");
            _console.Notice(DescribeTeams(initialState));
            ReportContextWindowRegime(
                [dungeonMasterProfile, rulebookResolverProfile, .. characterProfiles.Values], enforceWindow);

            var narrationLog = new NarrationLog();
            var formatter = new WorldStateFormatter(_prompts);
            var coordinator = new TurnCoordinator(
                engine, dungeonMaster, _prompts, formatter, narrationLog, knowledge, trace, _console, harness,
                intentParser, historySummariser, rulebook);

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

    private AgentModelProfile ResolveCharacterProfile(
        CharacterDefinition character, AgentProfileOptions defaults, long masterSeed, bool enforceWindow)
    {
        var overrides = _options.Agents.Characters.TryGetValue(character.Id, out var configured)
            ? configured
            : new AgentProfileOptions();

        return AgentModelProfile.FromOptions(character.Name, overrides.Overlay(defaults), enforceWindow)
            with { Seed = RunSeeds.Derive(masterSeed, RunSeeds.AgentKey(character.Id)) };
    }

    /// <summary>
    /// Says out loud when a configured context window is not the one being budgeted against, so the run
    /// starts with the reader knowing which regime it is in.
    /// </summary>
    /// <remarks>
    /// Silence was the problem worth fixing. A hosted run inheriting the Ollama-tuned <c>ContextWindow</c>
    /// summarised its characters' histories every other turn against a ceiling the provider never applied,
    /// and nothing in the console, the trace or the report said so — the setting was simply dropped from
    /// the request and forgotten. Both regimes are legitimate; neither should be arrived at by accident.
    /// </remarks>
    private void ReportContextWindowRegime(IEnumerable<AgentModelProfile> profiles, bool enforceWindow)
    {
        var unbounded = profiles
            .Where(p => p.ContextWindow is > 0 && p.BindingContextWindow is null)
            .Select(p => $"{p.AgentName} ({p.Provider}:{p.ModelId})")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unbounded.Count == 0)
        {
            if (enforceWindow)
            {
                _console.Notice(
                    "Context windows are enforced on all providers, including hosted ones (levelled comparison).");
            }

            return;
        }

        _console.Notice(
            $"ContextWindow is configured but not applied for {unbounded.Count} agent(s) — " +
            $"{string.Join(", ", unbounded)} — because the provider sets its own window per model. " +
            "History will not be summarised to fit it. Set Harness.EnforceContextWindowOnHostedModels " +
            "to hold these agents to the configured window instead.");
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
        // when the run stopped on a harness limit rather than a decision. When the run stopped on a limit
        // the encounter is not terminal, so its classification is HarnessLimit rather than the (Ongoing)
        // reading the pure evaluator would give.
        var finalResult = ending ?? TerminalCondition.Evaluate(finalState);
        var finalOutcome = ending is { IsOver: true } ? finalResult.Outcome : EncounterOutcome.HarnessLimit;
        EmitTeamOutcome(trace, "final", finalResult, finalOutcome);

        _console.Ending(SummariseEnding(finalState, terminalCondition));

        trace.Emit(TraceEventType.RunCompleted, new RunCompletedPayload
        {
            TerminalCondition = terminalCondition,
            RoundsPlayed = roundsPlayed,
            Survivors = [.. finalState.Characters.Where(c => c.IsAlive).Select(c => c.Name)],
            Casualties = [.. finalState.Characters.Where(c => !c.IsAlive).Select(c => c.Name)],
            Outcome = finalOutcome.ToString(),
            WinningTeams = finalResult.WinningTeams,
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

    /// <summary>
    /// Records a team-outcome evaluation. <paramref name="outcomeOverride"/> replaces the pure evaluator's
    /// classification for the final check when the run stopped on a harness limit — there the encounter is
    /// not terminal, so it is classified as <see cref="EncounterOutcome.HarnessLimit"/> rather than Ongoing.
    /// </summary>
    private static void EmitTeamOutcome(
        ExperimentTrace trace, string trigger, TerminalConditionResult outcome, EncounterOutcome? outcomeOverride = null) =>
        trace.Emit(TraceEventType.TeamOutcomeEvaluated, new TeamOutcomePayload
        {
            Trigger = trigger,
            IsOver = outcome.IsOver,
            Standings = [.. outcome.Standings.Select(s => new TeamStandingPayload
            {
                Team = s.Team,
                Living = s.Living,
                Total = s.Total,
                Active = s.Active,
                Surrendered = s.Surrendered,
                Escaped = s.Escaped,
                Dead = s.Dead
            })],
            WinningTeams = outcome.WinningTeams,
            EliminatedTeams = outcome.EliminatedTeams,
            Outcome = (outcomeOverride ?? outcome.Outcome).ToString(),
            Resolutions = [.. outcome.Resolutions.Select(r => new CharacterResolutionPayload
            {
                CharacterId = r.CharacterId,
                CharacterName = r.CharacterName,
                Team = r.Team,
                Disposition = r.Disposition.ToString(),
                ExitName = r.ExitName,
                Summary = r.Summary
            })],
            ResolutionSummary = outcome.Resolutions.Count == 0 ? null : outcome.ResolutionSummary,
            Description = outcome.Description
        }, "harness");

    /// <summary>
    /// Surfaces silent history loss once, at the end, rather than interrupting the transcript. If this
    /// fires, some replies were formed from a conversation the provider had already trimmed.
    /// </summary>
    /// <remarks>
    /// This event fires only for a provider whose <see cref="AI.ProviderCapabilities.SilentlyTruncatesHistory"/>
    /// is true — currently Ollama alone — so every call to this warning is, by construction, about the one
    /// provider this project's Qwen configuration keeps pinned at an 8,192-token window. "Raise
    /// ContextWindow" is therefore never the right advice here: on Ollama it forces a second full copy of
    /// the model's weights into memory rather than trimming anything, which is exactly the residency
    /// constraint the project is built around.
    /// </remarks>
    private void WarnAboutContextSaturation(ExperimentTrace trace)
    {
        var saturated = trace.CountOf(TraceEventType.ContextWindowSaturated);
        if (saturated == 0)
        {
            return;
        }

        _console.Notice(
            $"WARNING: on {saturated} model call(s) the provider reported processing far fewer input " +
            "tokens than we sent — it silently discarded the oldest messages. Raising ContextWindow is the " +
            "wrong fix here: it forces a second full copy of the model's weights into memory. Shorten the " +
            "run, lower RecentTurnsKeptFull, or enable history summarisation instead.");
    }

    /// <summary>
    /// Warns when a reasoning model spent its whole output budget thinking. The symptom otherwise is
    /// only "the Dungeon Master said nothing", which reads as a bug rather than a configuration issue.
    /// </summary>
    /// <remarks>
    /// Counts only <see cref="ExperimentTrace.ReasoningOnlyTruncationCount"/>, not every
    /// <see cref="TraceEventType.ModelResponseTruncated"/> — a plain context-exhaustion truncation is
    /// recorded under the same event type but has nothing to do with reasoning, and "disable Thinking or
    /// raise MaxOutputTokens" is not a fix for it (that advice wants the opposite: send less).
    /// </remarks>
    private void WarnAboutReasoningStarvation(ExperimentTrace trace)
    {
        var starved = trace.ReasoningOnlyTruncationCount;
        if (starved == 0)
        {
            return;
        }

        _console.Notice(
            $"WARNING: {starved} model call(s) spent their entire output budget on invisible reasoning and " +
            "produced no visible reply or tool call. If that agent uses a reasoning model, set its Thinking " +
            "to false, or raise its MaxOutputTokens.");
    }

    private static string DescribeTeams(GameState state)
    {
        var teams = state.Teams()
            .Select(team => $"{team}: {string.Join(", ", state.Characters.Where(c => Same(c.Team, team)).Select(c => c.Name))}");
        return $"Teams — {string.Join(" | ", teams)}.";
    }

    private static string SummariseEnding(GameState state, string terminalCondition)
    {
        var lines = state.Characters.Select(c => c.Disposition switch
        {
            CharacterDisposition.Dead => $"{c.Name} ({c.Team}) has fallen.",
            CharacterDisposition.Surrendered => $"{c.Name} ({c.Team}) surrendered and is out of the fight.",
            CharacterDisposition.Escaped => $"{c.Name} ({c.Team}) escaped the encounter alive.",
            _ => $"{c.Name} ({c.Team}) survives with {c.Health} of {c.MaxHealth} health."
        });

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
