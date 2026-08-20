using Microsoft.Extensions.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Knowledge;
using ModelsAndMonsters.Presentation;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Rulebook;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Orchestration;

/// <summary>
/// Runs one character's turn, and is the single place where tool calls are inspected and dispatched.
/// </summary>
/// <remarks>
/// <para>
/// The whole sequence is written out by hand on purpose: model response → requested tool → argument
/// capture → application dispatch → tool result → result returned to the model → next model response.
/// No automatic function-invocation middleware is used anywhere, because watching that sequence
/// is the point of the experiment.
/// </para>
/// <para>
/// Every tool call the model makes is answered exactly once, including calls that are refused or
/// arrive after the turn is already decided. Leaving one unanswered would corrupt the agent's history.
/// </para>
/// </remarks>
public sealed class TurnCoordinator
{
    private readonly IGameEngine _engine;
    private readonly DungeonMasterAgent _dungeonMaster;
    private readonly PromptLibrary _prompts;
    private readonly WorldStateFormatter _formatter;
    private readonly NarrationLog _narrationLog;
    private readonly KnowledgeLedger _knowledge;
    private readonly ExperimentTrace _trace;
    private readonly IGameConsole _console;
    private readonly HarnessOptions _limits;
    private readonly IntentParser? _intentParser;
    private readonly HistorySummariser? _summariser;
    private readonly Rulebook.RulebookConsultant? _rulebook;

    /// <summary>
    /// The acting character's information view for the adjudication currently in flight, so the ruling can
    /// be traced with the exact view the Dungeon Master was given. Set at the start of each adjudication;
    /// safe as a field because orchestration is strictly sequential.
    /// </summary>
    private string? _actingKnowledgeView;

    /// <summary>
    /// The rulebook consultation id for the adjudication currently in flight (v0.6), so the DM ruling,
    /// inventory interaction and item-provenance events can be cross-referenced to the resolver record. Set
    /// at the start of each adjudication that consults the rulebook; null when none ran.
    /// </summary>
    private string? _currentConsultationId;

    /// <summary>
    /// The public-channel entry for the most recent thing the acting character said on the turn currently
    /// being played, so a surrender offer made on the same turn can be associated with the plea, argument or
    /// threat that carried it. Reset at the start of every turn. The speech supplies the persuasion; the offer
    /// supplies the enforceable terms, and the two are never conflated.
    /// </summary>
    private NarrationEntry? _speechThisTurn;

    public TurnCoordinator(
        IGameEngine engine,
        DungeonMasterAgent dungeonMaster,
        PromptLibrary prompts,
        WorldStateFormatter formatter,
        NarrationLog narrationLog,
        KnowledgeLedger knowledge,
        ExperimentTrace trace,
        IGameConsole console,
        HarnessOptions limits,
        IntentParser? intentParser = null,
        HistorySummariser? summariser = null,
        Rulebook.RulebookConsultant? rulebook = null,
        IAnswerFactsProjector? answerFacts = null)
    {
        _engine = engine;
        _dungeonMaster = dungeonMaster;
        _prompts = prompts;
        _formatter = formatter;
        _narrationLog = narrationLog;
        _knowledge = knowledge;
        _trace = trace;
        _console = console;
        _limits = limits;
        _intentParser = intentParser;
        _summariser = summariser;
        _rulebook = rulebook;

        // The answer projection is deterministic and needs nothing but the ledger and the public channel, so
        // it is built here by default rather than being another thing every caller has to wire up. Injecting
        // one is for tests that want to observe or substitute the projection.
        _answerFacts = answerFacts ?? new AnswerFactsProjector(knowledge, narrationLog);
    }

    private readonly IAnswerFactsProjector _answerFacts;

    // -----------------------------------------------------------------------------------------
    // Narration
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Gives the Dungeon Master a fresh authoritative snapshot and records the narration it returns on
    /// the public channel. Nobody receives it until their next turn begins.
    /// </summary>
    public async Task<NarrationEntry> NarrateSituationAsync(string context, string purpose, CancellationToken cancellationToken)
    {
        var stateText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        var narration = await _dungeonMaster.NarrateAsync(stateText, context, purpose, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(narration))
        {
            narration = "(The Dungeon Master said nothing.)";
        }

        var entry = _narrationLog.Record(purpose, narration);

        _trace.Emit(TraceEventType.Narration, new NarrationPayload
        {
            Purpose = purpose,
            StateSuppliedToDungeonMaster = stateText,
            ContextSuppliedToDungeonMaster = context,
            Narration = narration,
            NarrationId = entry.Id,
            IntendedRecipients = LivingRecipients()
        }, DungeonMasterAgent.AgentIdentifier);

        _console.DungeonMaster(narration);
        return entry;
    }

    // -----------------------------------------------------------------------------------------
    // Turn loop
    // -----------------------------------------------------------------------------------------

    public async Task<TurnResult> RunTurnAsync(CharacterAgent character, int round, int turn, CancellationToken cancellationToken)
    {
        _trace.SetPosition(round, turn, character.Name);

        var self = _engine.State.RequireById(character.CharacterId);
        if (!self.CanAct)
        {
            // Only an active character takes a turn. Anyone dead, surrendered or escaped is skipped without
            // any model call, and the skip is traced with its disposition so the fixed turn order stays
            // visible and uncorrupted in the record rather than a turn simply going missing.
            var (reason, notice) = self.Disposition switch
            {
                CharacterDisposition.Surrendered =>
                    ($"{character.Name} has surrendered and takes no further turns.",
                     $"{character.Name} has surrendered; their turn is skipped."),
                CharacterDisposition.Escaped =>
                    ($"{character.Name} has escaped the encounter and takes no further turns.",
                     $"{character.Name} has escaped; their turn is skipped."),
                _ =>
                    ($"{character.Name} is dead and cannot take a turn.",
                     $"{character.Name} lies fallen; their turn passes.")
            };

            _trace.Emit(TraceEventType.TurnSkipped, new TurnSkippedPayload
            {
                CharacterId = character.CharacterId,
                CharacterName = character.Name,
                Team = self.Team,
                Reason = reason,
                Disposition = self.Disposition.ToString()
            });

            _console.Notice(notice);

            return new TurnResult
            {
                CharacterId = character.CharacterId,
                CharacterName = character.Name,
                Outcome = TurnOutcome.Skipped,
                QuestionsAsked = 0,
                ActionAttempts = 0,
                ModelCalls = 0
            };
        }

        // Start-of-turn upkeep before anything is rendered or asked: a status whose rule fires now must be gone
        // before this character sees their own state, or they would be told they still have a guard that has
        // in fact just fallen away.
        ApplyUpkeep(_engine.BeginActorTurn(character.CharacterId, round, turn), "turn-start", character.Name);

        _speechThisTurn = null;
        self = _engine.State.RequireById(character.CharacterId);

        var selfState = _formatter.FormatCharacterSelfState(self, _engine.State);
        var pendingNarration = _narrationLog.TakeUndelivered(character.CharacterId);

        if (pendingNarration.Count > 0)
        {
            _trace.Emit(TraceEventType.NarrationDelivered, new NarrationDeliveredPayload
            {
                NarrationId = pendingNarration[^1].Id,
                Narration = string.Join("\n\n", pendingNarration.Select(n => n.Text)),
                DeliveredTo = [character.CharacterId],
                DeliveryMechanism = "turn-context"
            });
        }

        // A short, deliberately projected reminder of what this character has discovered first-hand. It is
        // bounded (a handful of current facts), never the whole lifetime knowledge history, so injecting it
        // every turn does not grow the context without limit.
        var knowledgeSummary = CharacterKnowledgeView.RenderSelfSummary(character.CharacterId, _knowledge, _engine.State);

        character.BeginTurn(_prompts.Render("character.turn", new Dictionary<string, string?>
        {
            ["name"] = character.Name,
            ["state"] = selfState,
            ["knowledge"] = string.IsNullOrWhiteSpace(knowledgeSummary)
                ? "You have discovered nothing in particular beyond what anyone here can plainly see."
                : knowledgeSummary,
            ["narration"] = pendingNarration.Count == 0
                ? "Nothing has changed since you last looked."
                : string.Join("\n\n", pendingNarration.Select(n => n.Text))
        }));

        // Where this character's history stands now, with the turn's context injected but before any reply.
        // When the turn resolves, everything after this mark is compacted back to the clean calls, so the
        // failed prose replies and nudges a turn accumulates do not pile up and fill the context window later.
        var historyMark = character.MarkHistory();

        _trace.Emit(TraceEventType.TurnStarted, new TurnStartedPayload
        {
            CharacterId = character.CharacterId,
            CharacterName = character.Name,
            SelfStateBlock = selfState,
            NarrationsDelivered = [.. pendingNarration.Select(n => n.Id)],
            StateAtTurnStart = _engine.State
        });

        var questionsAsked = 0;
        var actionAttempts = 0;
        var speechActs = 0;
        var modelCalls = 0;
        var outcome = TurnOutcome.AbandonedAtLimit;
        string? acceptedAction = null;

        while (true)
        {
            if (modelCalls >= _limits.MaxModelCallsPerTurn)
            {
                EmitLimit(nameof(HarnessOptions.MaxModelCallsPerTurn), _limits.MaxModelCallsPerTurn,
                    $"{character.Name}'s turn was abandoned without a resolved action.");
                break;
            }

            var response = await character.DecideAsync(cancellationToken).ConfigureAwait(false);
            modelCalls++;

            var calls = ModelAgent.GetToolCalls(response);
            if (calls.Count == 0)
            {
                // No tool call has two very different causes: the model ignored its protocol, or it ran out
                // of output budget mid-reply. A truncated reply may be incomplete, so it is never parsed.
                var truncated = character.WasReplyCutShort(response);
                var contextExhausted = character.WasContextExhausted(response);

                // A full window cannot be argued with. Appending another nudge to a conversation that has
                // already filled the context makes the next attempt strictly worse — a live run spent three
                // consecutive retries this way, each one adding a "your reply was cut off" message to a
                // request that had no room left, and every one of them came back truncated. Reclaim the room
                // first: shed this turn's failed prose, then fold older turns into the running summary.
                if (contextExhausted)
                {
                    await ReclaimContextRoomAsync(character, historyMark, cancellationToken).ConfigureAwait(false);
                }

                if (!truncated && _intentParser is not null && _limits.UseIntentParser)
                {
                    // Prose-fallback: read the reply into the say/ask/act calls it implies and dispatch them
                    // as if the character had made them — one reply can carry a spoken line AND an action, so
                    // the turn resolves in one pass instead of a nudge loop. Always yields at least one call.
                    calls = await ParseProseIntoCallsAsync(character, response, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Parser off, or a truncated reply: record a prose speech attempt and nudge, recover a
                    // prose tool call, or nudge for a clean one. Null means the character was nudged — ask again.
                    var recovered = TryRecoverOrNudge(character, response, truncated, contextExhausted);
                    if (recovered is null)
                    {
                        continue;
                    }

                    calls = [recovered];
                }
            }

            var turnEnded = false;

            foreach (var call in calls)
            {
                if (turnEnded)
                {
                    // The turn is already decided. Whatever else the model asked for is DISCARDED: it never
                    // reaches the world, the public transcript or the knowledge ledger. This is what stops a
                    // model attacking and then emitting an unprocessed theft declaration as though it acted
                    // twice. The call is still answered, because leaving one unanswered corrupts the history.
                    EmitPostResolutionDiscarded(character, acceptedAction, "tool-call", call.Name,
                        DescribeCall(call));
                    RecordToolResult(character, call,
                        "Your turn was already resolved by what you did; this was set aside and did not happen.");
                    continue;
                }

                // Speech the character declared in this very call, delivered before the call it rides on —
                // a warning is only worth anything if it lands before the blow it warns about. This is the
                // authoritative speech path: what is in `utterances` is spoken, and quotation marks anywhere
                // else are just punctuation.
                if (call.Name is CharacterTools.AskDmName or CharacterTools.TakeActionName or CharacterTools.EndTurnName)
                {
                    speechActs += DeliverDeclaredUtterances(character, call, round, turn, speechActs);
                }

                switch (call.Name)
                {
                    case CharacterTools.AskDmName:
                    {
                        if (questionsAsked >= _limits.MaxQuestionsPerTurn)
                        {
                            EmitLimit(nameof(HarnessOptions.MaxQuestionsPerTurn), _limits.MaxQuestionsPerTurn,
                                $"{character.Name} was told to stop asking and act.");
                            DispatchAndRecord(character, call,
                                "You have asked all you can for now. Attempt something.",
                                "refused-question-limit");
                            break;
                        }

                        questionsAsked++;
                        await HandleAskDmAsync(character, call, questionsAsked, cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    case CharacterTools.TakeActionName:
                    {
                        // Every attempt we were handed is adjudicated — a decision we requested is never
                        // refused unheard just because it is the last one allowed. The cap governs whether
                        // we ask for a *further* decision, not whether we honour this one; once it is
                        // reached on a failed attempt we stop rather than request one more only to discard
                        // it. (A model may also emit several take_action calls in one reply; the second and
                        // later are caught by the turn-ended guard above once this one resolves the turn.)
                        actionAttempts++;
                        var attempt = await HandleTakeActionAsync(character, call, cancellationToken).ConfigureAwait(false);

                        if (attempt.ConsumesTurn)
                        {
                            turnEnded = true;
                            outcome = TurnOutcome.ActionResolved;
                            acceptedAction = attempt.Action?.Describe();
                        }
                        else if (actionAttempts >= _limits.MaxActionAttemptsPerTurn)
                        {
                            EmitLimit(nameof(HarnessOptions.MaxActionAttemptsPerTurn), _limits.MaxActionAttemptsPerTurn,
                                $"{character.Name}'s turn was abandoned after too many failed attempts.");
                            turnEnded = true;
                            outcome = TurnOutcome.AbandonedAtLimit;
                        }

                        break;
                    }

                    case CharacterTools.SayName:
                    {
                        if (speechActs >= _limits.MaxSpeechActsPerTurn)
                        {
                            EmitLimit(nameof(HarnessOptions.MaxSpeechActsPerTurn), _limits.MaxSpeechActsPerTurn,
                                $"{character.Name} had already spoken this turn and was not heard again.");
                            DispatchAndRecord(character, call,
                                "You have already spoken this turn. Act, ask, or end your turn.",
                                "refused-speech-limit");
                            break;
                        }

                        // Speaking never ends the turn: the character may still ask, act or end afterwards.
                        var spoke = await HandleSayAsync(character, call, round, turn, speechActs + 1, cancellationToken)
                            .ConfigureAwait(false);
                        if (spoke)
                        {
                            speechActs++;
                        }

                        break;
                    }

                    case CharacterTools.EndTurnName:
                    {
                        await HandleEndTurnAsync(character, call, cancellationToken).ConfigureAwait(false);
                        turnEnded = true;
                        outcome = TurnOutcome.EndedByCharacter;
                        break;
                    }

                    default:
                    {
                        _trace.Emit(TraceEventType.ToolCallError, new ToolCallErrorPayload
                        {
                            AgentName = character.Name,
                            CallId = call.CallId,
                            ToolName = call.Name,
                            Arguments = ChatTraceMapper.MapArguments(call.Arguments),
                            Error = "Character requested a tool it was never given."
                        });

                        DispatchAndRecord(character, call,
                            "You have no such capability. Use ask_dm or take_action.",
                            "rejected-unknown-tool");
                        break;
                    }
                }
            }

            if (turnEnded)
            {
                // Trailing prose in the same reply as an accepted action is discarded too, for the same reason:
                // a declaration the world never resolved must not read as something that happened.
                var trailing = ModelText.Clean(response);
                if (!string.IsNullOrWhiteSpace(trailing) && outcome == TurnOutcome.ActionResolved)
                {
                    EmitPostResolutionDiscarded(character, acceptedAction, "trailing-text", null, trailing);
                }

                break;
            }
        }

        // End-of-turn upkeep: statuses whose rule fires now fall away, and an offer this character was the
        // named recipient of and did not take up lapses. Both are the engine's decisions, not narration's.
        ApplyUpkeep(_engine.EndActorTurn(character.CharacterId, round, turn), "turn-end", character.Name);

        // The turn is over: shed the failed prose replies and nudges it took to get here, keeping the clean
        // tool calls and their results. The full exchange, including every discarded attempt, stays in the trace.
        character.CompactTurnHistory(historyMark);

        // Then, if the character's history has grown past budget, fold its older turns into a running summary
        // so a long fight does not fill the context window with legitimate history the prune cannot touch.
        if (_summariser is not null && _limits.SummariseHistory)
        {
            await MaybeSummariseHistoryAsync(character, cancellationToken).ConfigureAwait(false);
        }

        var result = new TurnResult
        {
            CharacterId = character.CharacterId,
            CharacterName = character.Name,
            Outcome = outcome,
            QuestionsAsked = questionsAsked,
            ActionAttempts = actionAttempts,
            SpeechActs = speechActs,
            ModelCalls = modelCalls,
            AcceptedAction = acceptedAction
        };

        if (outcome == TurnOutcome.AbandonedAtLimit)
        {
            // Say so in the transcript: a silently missing turn reads as a bug to anyone watching.
            _console.Notice($"{character.Name} hesitates, and the moment passes.");
        }

        _trace.Emit(TraceEventType.TurnEnded, new TurnEndedPayload
        {
            CharacterId = result.CharacterId,
            CharacterName = result.CharacterName,
            Result = result.Outcome.ToString(),
            QuestionsAsked = result.QuestionsAsked,
            ActionAttempts = result.ActionAttempts,
            SpeechActs = result.SpeechActs,
            ModelCalls = result.ModelCalls,
            AcceptedAction = result.AcceptedAction
        });

        return result;
    }

    // -----------------------------------------------------------------------------------------
    // ask_dm
    // -----------------------------------------------------------------------------------------

    private async Task HandleAskDmAsync(
        CharacterAgent character,
        FunctionCallContent call,
        int questionNumber,
        CancellationToken cancellationToken)
    {
        var question = ToolArguments.GetString(call, CharacterTools.QuestionParameter);

        _trace.Emit(TraceEventType.ToolCallDispatched, new ToolCallDispatchPayload
        {
            AgentName = character.Name,
            CallId = call.CallId,
            ToolName = call.Name,
            Arguments = ChatTraceMapper.MapArguments(call.Arguments),
            DispatchDecision = question is null
                ? "Rejected: no question text supplied."
                : "Forwarded to the Dungeon Master as a private question. Does not consume the turn."
        });

        if (question is null)
        {
            RecordToolResult(character, call, "You did not actually say anything. Ask a real question, or act.");
            return;
        }

        _console.CharacterAsks(character.Name, question);

        _trace.Emit(TraceEventType.CharacterQuestion, new CharacterQuestionPayload
        {
            CharacterId = character.CharacterId,
            CharacterName = character.Name,
            Question = question,
            QuestionNumberThisTurn = questionNumber
        });

        // The Dungeon Master is NOT given the authoritative state for this task. A deterministic projection
        // works out the complete set of facts this character may be answered from — current permitted
        // knowledge, what anyone present can see, the closed list of what they could actually attempt, and
        // explicit statements of what this world does not represent — and the DM's job is reduced to saying
        // that back naturally. Withholding the material is what makes an ungrounded answer impossible, where
        // a prompt telling the model not to invent one measurably was not.
        var facts = _answerFacts.Project(_engine.State, character.CharacterId, question);
        var factsText = facts.Render();

        // The knowledge view is still recorded alongside, because it is the informational basis the projection
        // was built from and the record should show both.
        var knowledgeView = CharacterKnowledgeView.RenderForDungeonMaster(
            character.CharacterId, character.Name, _knowledge, _narrationLog, _engine.State);

        _trace.Emit(TraceEventType.AnswerFactsProjected, new AnswerFactsPayload
        {
            CharacterId = character.CharacterId,
            CharacterName = character.Name,
            Question = question,
            ProjectedFacts = factsText,
            OmittedHiddenFacts = facts.RenderOmitted(),
            ProjectedFactCount = facts.AboutYourself.Count + facts.PlainlyVisible.Count
                + facts.KnownFirstHand.Count + facts.Hearsay.Count + facts.Affordances.Count
                + facts.WorldLimits.Count,
            OmittedFactCount = facts.OmittedHiddenFacts.Count,
            AffordanceCount = facts.Affordances.Count,
            WorldVersion = facts.WorldVersion
        }, DungeonMasterAgent.AgentIdentifier);

        var rawAnswer = await _dungeonMaster
            .AnswerFromFactsAsync(character.Name, factsText, question, cancellationToken)
            .ConfigureAwait(false);

        var answer = string.IsNullOrWhiteSpace(rawAnswer) ? "You cannot tell." : rawAnswer;

        // Guarantee the answer reaches the character in character. A weaker instruct model as DM parrots
        // the projection's scaffolding back ("you directly know…", "you have not been told…", state words
        // in bold); a prompt cannot reliably stop it, so a leaked answer is caught here and rephrased once.
        answer = await InWorldAnswerAsync(character, answer, cancellationToken).ConfigureAwait(false);

        _trace.Emit(TraceEventType.DungeonMasterAnswer, new DungeonMasterAnswerPayload
        {
            CharacterId = character.CharacterId,
            CharacterName = character.Name,
            Question = question,
            Answer = answer,
            RawAnswer = rawAnswer,
            AskingCharacterKnowledge = knowledgeView,
            ProjectedFacts = factsText,
            OmittedHiddenFacts = facts.RenderOmitted(),
            WorldVersion = _engine.State.Version
        }, DungeonMasterAgent.AgentIdentifier);

        _console.DungeonMaster(answer);

        // Private: this answer enters only the asking character's history.
        RecordToolResult(character, call, answer);
    }

    // -----------------------------------------------------------------------------------------
    // end_turn
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A character choosing to do nothing. No engine action is involved, but the world still has to
    /// describe it so the other character can perceive that they held back.
    /// </summary>
    private async Task HandleEndTurnAsync(
        CharacterAgent character,
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        var reason = ToolArguments.GetString(call, CharacterTools.ReasonParameter)
            ?? "They do nothing.";

        _trace.Emit(TraceEventType.ToolCallDispatched, new ToolCallDispatchPayload
        {
            AgentName = character.Name,
            CallId = call.CallId,
            ToolName = call.Name,
            Arguments = ChatTraceMapper.MapArguments(call.Arguments),
            DispatchDecision = "Character chose to do nothing. The turn ends with no engine action."
        });

        _console.CharacterPasses(character.Name, reason);

        _trace.Emit(TraceEventType.CharacterPassed, new CharacterPassedPayload
        {
            CharacterId = character.CharacterId,
            CharacterName = character.Name,
            Reason = reason
        });

        var stateText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        var narration = await _dungeonMaster
            .NarratePassAsync(character.Name, reason, stateText, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(narration))
        {
            narration = $"{character.Name} does nothing.";
        }

        var entry = _narrationLog.Record("character-passed", narration);
        entry.MarkDeliveredTo(character.CharacterId);

        _trace.Emit(TraceEventType.Narration, new NarrationPayload
        {
            Purpose = "character-passed",
            StateSuppliedToDungeonMaster = stateText,
            ContextSuppliedToDungeonMaster = reason,
            Narration = narration,
            NarrationId = entry.Id,
            IntendedRecipients = LivingRecipients()
        }, DungeonMasterAgent.AgentIdentifier);

        _trace.Emit(TraceEventType.NarrationDelivered, new NarrationDeliveredPayload
        {
            NarrationId = entry.Id,
            Narration = narration,
            DeliveredTo = [character.CharacterId],
            DeliveryMechanism = "end_turn tool result"
        });

        _console.DungeonMaster(narration);
        RecordToolResult(character, call, narration);
    }

    // -----------------------------------------------------------------------------------------
    // say
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A character speaking aloud. Speech is a public event routed by the orchestrator, not adjudicated:
    /// the words are delivered verbatim, with attribution, and never paraphrased by a Dungeon Master
    /// model call. It reaches every other living character through the same bounded public channel as
    /// narration, so nobody hears it until their own next turn. Returns true when a valid message was
    /// delivered, false when it was rejected at the harness boundary (empty or oversized).
    /// </summary>
    private async Task<bool> HandleSayAsync(
        CharacterAgent character,
        FunctionCallContent call,
        int round,
        int turn,
        int speechIndex,
        CancellationToken cancellationToken)
    {
        var message = ToolArguments.GetString(call, CharacterTools.MessageParameter);

        // Reject empty or unreasonably large messages at the harness boundary, without delivering them.
        var rejection = message switch
        {
            null => "You did not actually say anything aloud. Speak real words, or do something.",
            _ when message.Length > _limits.MaxSpeechCharacters =>
                "That is far too much to call out across a room mid-fight. Say it in a sentence or two.",
            _ => null
        };

        _trace.Emit(TraceEventType.ToolCallDispatched, new ToolCallDispatchPayload
        {
            AgentName = character.Name,
            CallId = call.CallId,
            ToolName = call.Name,
            Arguments = ChatTraceMapper.MapArguments(call.Arguments),
            DispatchDecision = rejection is null
                ? "Delivered verbatim to every other living character in the room. Does not consume the turn."
                : $"Rejected at the harness boundary: {rejection}"
        });

        if (rejection is not null)
        {
            RecordToolResult(character, call, rejection);
            return false;
        }

        DeliverSpeech(character, message!.Trim(), round, turn, speechIndex);

        // Answer the say tool so the speaker's history stays valid; its own words are already present in
        // that history as the tool-call argument, so the result is only an acknowledgement.
        RecordToolResult(character, call,
            "Your words carry across the room. You may still ask, act, or end your turn.");
        return true;
    }

    /// <summary>
    /// Delivers one spoken line to the room. Shared by the <c>say</c> tool and by structured
    /// <c>utterances</c> declared alongside an action, so both reach the world by exactly one path.
    /// </summary>
    private void DeliverSpeech(CharacterAgent character, string spoken, int round, int turn, int speechIndex)
    {
        // The stored public-channel text already carries attribution, so recipients read the speaker's
        // exact words rather than a paraphrase, and delivery reuses the narration path unchanged.
        var entry = _narrationLog.RecordSpeech(character.CharacterId, $"{character.Name} says:\n\"{spoken}\"");

        // The speaker has just said it; it must not be re-delivered to them as "newly heard" next turn.
        entry.MarkDeliveredTo(character.CharacterId);

        // Remembered for this turn only, so a surrender offer made after speaking can point at the plea,
        // argument or threat that carried it. The speech is never the terms — only the argument for them.
        _speechThisTurn = entry;

        var self = _engine.State.FindById(character.CharacterId);
        var recipients = LivingRecipientsExcept(character.CharacterId);

        _console.CharacterSpeaks(character.Name, spoken);

        _trace.Emit(TraceEventType.CharacterSpeech, new CharacterSpeechPayload
        {
            SpeakerId = character.CharacterId,
            SpeakerName = character.Name,
            SpeakerTeam = self?.Team ?? "",
            Message = spoken,
            Round = round,
            Turn = turn,
            SpeechIndexWithinTurn = speechIndex,
            Recipients = recipients,
            DeliveryMechanism = "public-channel (delivered at each recipient's next turn)",
            NarrationId = entry.Id
        });
    }

    /// <summary>
    /// Delivers the speech a character declared in the <c>utterances</c> field of the very call that carries
    /// its action, question or pass. Returns how many lines were spoken, so the caller can keep the
    /// once-per-turn limit. No extra model call: the words came from the reply already in hand.
    /// </summary>
    /// <remarks>
    /// This is the authoritative speech path. A character says what it puts in <c>utterances</c> and nothing
    /// else — quotation marks anywhere in its intent text are just punctuation, whether they hold the name of
    /// a blade or a line of dialogue, and speech needs no verb to be recognised because nothing is being
    /// recognised. The heuristic that used to infer speech from quoted text near a speech verb survives only
    /// as a fallback for a reply that carried no tool call at all, and traces itself when it fires.
    /// </remarks>
    private int DeliverDeclaredUtterances(
        CharacterAgent character, FunctionCallContent call, int round, int turn, int speechActsSoFar)
    {
        // Speech is prose: a comma inside a spoken line is punctuation, never a separator, so a bare
        // string arrives as ONE utterance rather than being chopped at every comma.
        var declared = ToolArguments.GetStringList(call, CharacterTools.UtterancesParameter, splitLooseStrings: false);
        if (declared.Count == 0)
        {
            return 0;
        }

        // Several declared lines are ONE breath, not several turns of speaking. Joining them keeps the
        // once-per-turn rule exactly as it was while not throwing away half of what a character said: a live
        // run hit the speech limit ten times, every one of them silently discarding the rest of a reply,
        // because models naturally write two short sentences ("Rowan, the flank!" / "I have him.") where the
        // harness counts speech acts. Dropping the second half is a worse answer than delivering both.
        var lines = declared
            .Select(raw => raw?.Trim() ?? "")
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count == 0)
        {
            return 0;
        }

        if (speechActsSoFar >= _limits.MaxSpeechActsPerTurn)
        {
            EmitLimit(nameof(HarnessOptions.MaxSpeechActsPerTurn), _limits.MaxSpeechActsPerTurn,
                $"{character.Name} had already spoken this turn and was not heard again.");
            return 0;
        }

        var spoken = string.Join(" ", lines);
        if (spoken.Length > _limits.MaxSpeechCharacters)
        {
            _trace.Emit(TraceEventType.ToolCallDispatched, new ToolCallDispatchPayload
            {
                AgentName = character.Name,
                CallId = call.CallId,
                ToolName = call.Name,
                DispatchDecision = "Declared speech rejected at the harness boundary: too long to call across a room."
            });
            return 0;
        }

        DeliverSpeech(character, spoken, round, turn, speechActsSoFar + 1);
        return 1;
    }

    // -----------------------------------------------------------------------------------------
    // take_action
    // -----------------------------------------------------------------------------------------

    private async Task<ActionAttemptOutcome> HandleTakeActionAsync(
        CharacterAgent character,
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        var intent = ToolArguments.GetString(call, CharacterTools.IntentParameter);

        _trace.Emit(TraceEventType.ToolCallDispatched, new ToolCallDispatchPayload
        {
            AgentName = character.Name,
            CallId = call.CallId,
            ToolName = call.Name,
            Arguments = ChatTraceMapper.MapArguments(call.Arguments),
            DispatchDecision = intent is null
                ? "Rejected: no intent text supplied."
                : "Sent to the Dungeon Master for adjudication."
        });

        if (intent is null)
        {
            var empty = new ActionAttemptOutcome
            {
                Category = ActionResolutionCategory.DmProtocolFailure,
                MessageToCharacter = "You did not say what you were trying to do. Try again, and be specific."
            };
            RecordToolResult(character, call, empty.MessageToCharacter);
            return empty;
        }

        _console.CharacterActs(character.Name, intent);

        var outcome = await AdjudicateAsync(character, intent, cancellationToken).ConfigureAwait(false);

        var messageToCharacter = outcome.ConsumesTurn
            ? outcome.MessageToCharacter
            : $"Your attempt did not happen. {outcome.MessageToCharacter} You may try something else.";

        RecordToolResult(character, call, messageToCharacter);
        return outcome;
    }

    /// <summary>
    /// The Dungeon Master interprets the intent, and — if it translates into a supported action — the
    /// application submits that action to the engine and feeds the engine's verdict back to the DM.
    /// </summary>
    private async Task<ActionAttemptOutcome> AdjudicateAsync(
        CharacterAgent character,
        string intent,
        CancellationToken cancellationToken)
    {
        var stateText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);

        // The DM adjudicates within the acting character's information boundary: it may allow an action
        // taken on a basis of first-hand knowledge or of hearsay, but it must reject a specific hidden fact
        // the character has neither seen nor been told, and never substitute stale knowledge for current
        // truth. The view is recorded on the adjudication so the ruling's informational basis is auditable.
        _actingKnowledgeView = CharacterKnowledgeView.RenderForDungeonMaster(
            character.CharacterId, character.Name, _knowledge, _narrationLog, _engine.State);

        // Every take_action is automatically preceded by a bounded, stateless rulebook consultation (v0.6):
        // the resolver never sees live state, and its validated guidance narrows the DM to a small candidate
        // tool set. When no rulebook is wired (the v0.5 path) the DM gets the full engine tool surface.
        string? guidanceForDm = null;
        IReadOnlyList<AITool>? candidateTools = null;
        _currentConsultationId = null;
        if (_rulebook is not null)
        {
            var consultation = await _rulebook
                .ConsultAsync(character.CharacterId, character.Name, intent, cancellationToken)
                .ConfigureAwait(false);
            _currentConsultationId = consultation.ConsultationId;
            guidanceForDm = RenderGuidanceForDm(consultation);
            candidateTools = consultation.CandidateTools;

            // The resolver narrows the DM to the actions the rulebook says fit an intent *like* this one. That
            // is exactly right for rules that are about the deed, and exactly wrong for the two rules whose
            // meaning depends on the state of the room, which the resolver never sees. Widen the set for
            // those two — the DM does hold the snapshot, so given the tool it can rule correctly.
            (candidateTools, var widening) = WidenCandidateToolsForState(character, candidateTools);
            if (widening is not null)
            {
                guidanceForDm += Environment.NewLine + widening;
            }
        }

        var response = await _dungeonMaster
            .ProposeActionAsync(stateText, character.Name, _actingKnowledgeView, intent, cancellationToken,
                guidanceForDm, candidateTools)
            .ConfigureAwait(false);

        var calls = ModelAgent.GetToolCalls(response);

        for (var retry = 0; calls.Count == 0 && retry < _limits.MaxAdjudicationRetries; retry++)
        {
            var truncated = character.WasReplyCutShort(response);
            if (truncated)
            {
                _console.Notice(
                    "The Dungeon Master's ruling hit the output-token limit. " +
                    "Consider raising MaxOutputTokens for the DungeonMaster agent.");
            }

            _trace.Emit(TraceEventType.ToolCallError, new ToolCallErrorPayload
            {
                AgentName = DungeonMasterAgent.AgentIdentifier,
                ToolName = "(none)",
                Error = truncated
                    ? "Dungeon Master's ruling was truncated at the output-token limit before a tool call was produced."
                    : $"Dungeon Master adjudicated without calling a tool " +
                      $"(finish reason: {response.FinishReason?.Value ?? "none reported"})."
            }, DungeonMasterAgent.AgentIdentifier);

            response = await _dungeonMaster.RetryProposeActionAsync(character.Name, cancellationToken)
                .ConfigureAwait(false);
            calls = ModelAgent.GetToolCalls(response);
        }

        if (calls.Count == 0)
        {
            var text = ModelText.Clean(response);

            // If the DM wrote its tool call as text instead of calling it, keep the reason it intended
            // and never let the raw JSON reach the character as if it were narration.
            var messageToCharacter = text switch
            {
                _ when string.IsNullOrWhiteSpace(text) => "Your attempt comes to nothing.",
                _ when ModelText.LooksStructured(text) =>
                    ModelText.TryExtractJsonField(text, "reason", "explanation", "message")
                        ?? "Your attempt comes to nothing.",
                _ => text
            };

            var outcome = new ActionAttemptOutcome
            {
                Category = ActionResolutionCategory.DmProtocolFailure,
                MessageToCharacter = messageToCharacter
            };

            // The trace keeps the DM's original text verbatim, so the leaked-JSON case stays visible.
            EmitAdjudication(character, intent, outcome.Category, messageToCharacter, null, text);
            _console.CharacterRefused(character.Name, outcome.MessageToCharacter);
            return outcome;
        }

        var primary = calls[0];

        // Answer any surplus calls straight away: no further model call may happen while an
        // outstanding tool call is unanswered.
        foreach (var surplus in calls.Skip(1))
        {
            _trace.Emit(TraceEventType.ToolCallDispatched, new ToolCallDispatchPayload
            {
                AgentName = DungeonMasterAgent.AgentIdentifier,
                CallId = surplus.CallId,
                ToolName = surplus.Name,
                Arguments = ChatTraceMapper.MapArguments(surplus.Arguments),
                DispatchDecision = "Ignored: only one action may be adjudicated per attempt."
            }, DungeonMasterAgent.AgentIdentifier);

            RecordDungeonMasterToolResult(surplus, "Ignored. Exactly one action is adjudicated at a time.");
        }

        return primary.Name switch
        {
            DungeonMasterTools.RejectActionName =>
                await HandleDungeonMasterRejection(character, intent, primary, cancellationToken).ConfigureAwait(false),
            DungeonMasterTools.AttackCharacterName or DungeonMasterTools.UseItemName
                or DungeonMasterTools.OpenContainerName or DungeonMasterTools.TakeItemName
                or DungeonMasterTools.InspectObjectName or DungeonMasterTools.OpenExitName
                or DungeonMasterTools.EscapeEncounterName or DungeonMasterTools.OfferSurrenderName
                or DungeonMasterTools.AcceptSurrenderName or DungeonMasterTools.UseAbilityName
                or DungeonMasterTools.DefendName
                or DungeonMasterTools.GiveItemName or DungeonMasterTools.DropItemName
                or DungeonMasterTools.StealItemName =>
                await HandleEngineActionAsync(character, intent, primary, cancellationToken).ConfigureAwait(false),
            _ => HandleUnknownDungeonMasterTool(character, intent, primary)
        };
    }

    /// <summary>
    /// Renders one rulebook consultation into the request-scoped guidance block handed to the Dungeon Master.
    /// Supported guidance names the candidate action(s) and their abstract rules to bind; an unsupported or
    /// failed consultation instructs an in-world rejection, so the DM fails safe rather than inventing an action.
    /// </summary>
    private static string RenderGuidanceForDm(RulebookConsultationResult consultation)
    {
        if (consultation.HasSupportedGuidance && consultation.Guidance is { } g)
        {
            var lines = new List<string>
            {
                $"The rulebook supports this kind of intent. Candidate action(s): {string.Join(", ", g.CandidateActions)}."
            };
            if (g.CitedRules.Count > 0)
            {
                // The cited rule ids are what tell the DM WHICH ability a use_ability candidate means — several
                // abilities share that one tool, and the rule id is the only thing distinguishing them.
                lines.Add($"Rule(s) cited: {string.Join(", ", g.CitedRules.Select(c => c.RuleId))}. " +
                          "Where a rule names a specific ability id, bind that exact ability.");
            }
            if (g.RequiredBindings.Count > 0)
            {
                lines.Add($"Bindings to fill from the state: {string.Join("; ", g.RequiredBindings)}.");
            }
            if (g.Preconditions.Count > 0)
            {
                lines.Add($"Preconditions the engine will check: {string.Join("; ", g.Preconditions)}.");
            }
            if (!string.IsNullOrWhiteSpace(g.TurnCost))
            {
                lines.Add($"Turn cost: {g.TurnCost}.");
            }
            if (!string.IsNullOrWhiteSpace(g.Visibility))
            {
                lines.Add($"Visibility: {g.Visibility}.");
            }
            lines.Add("Bind exactly one of the candidate actions to the state with exact snapshot names and call it. " +
                      "If none actually fits the state or this character's knowledge, call reject_action instead.");
            return string.Join("\n", lines);
        }

        if (consultation.Outcome == RulebookOutcome.Unsupported && consultation.Guidance is { } u)
        {
            return $"The rulebook has no action that resolves this intent: {u.UnsupportedReason} " +
                   "Reject the attempt in-world (category unsupported) with a short in-world reason. Do not invent an action.";
        }

        // Retrieval, resolver or malformed-guidance failure: fail safe with a rejection.
        var detail = consultation.FailureDetail is { } d ? $" ({d})" : "";
        return $"The rulebook could not provide usable guidance for this intent{detail}. " +
               "Reject the attempt in-world with a short in-world reason. Do not invent an action.";
    }

    private async Task<ActionAttemptOutcome> HandleDungeonMasterRejection(
        CharacterAgent character, string intent, FunctionCallContent call, CancellationToken cancellationToken)
    {
        var rawCategory = ToolArguments.GetString(call, DungeonMasterTools.CategoryParameter);
        var reason = ToolArguments.GetString(call, DungeonMasterTools.ReasonParameter)
            ?? "That is not something that can happen here.";

        // An unrecognised category falls back to "unsupported": it is the more informative of the two
        // for the experiment, and neither ever fabricates a success.
        var category = string.Equals(rawCategory, DungeonMasterTools.ImpossibleCategory, StringComparison.OrdinalIgnoreCase)
            ? ActionResolutionCategory.DmImpossible
            : ActionResolutionCategory.DmUnsupported;

        _trace.Emit(TraceEventType.ToolCallDispatched, new ToolCallDispatchPayload
        {
            AgentName = DungeonMasterAgent.AgentIdentifier,
            CallId = call.CallId,
            ToolName = call.Name,
            Arguments = ChatTraceMapper.MapArguments(call.Arguments),
            DispatchDecision = $"Recorded as {category}. No engine call made."
        }, DungeonMasterAgent.AgentIdentifier);

        RecordDungeonMasterToolResult(call, "Refusal recorded. The character may attempt something else.");

        // Guarantee the reason reaches the character in-world, rephrasing it if it leaked the machinery.
        reason = await InWorldRejectionAsync(character, reason, cancellationToken).ConfigureAwait(false);

        EmitAdjudication(character, intent, category, reason, null, null);
        _console.CharacterRefused(character.Name, reason);

        return new ActionAttemptOutcome
        {
            Category = category,
            MessageToCharacter = reason
        };
    }

    private async Task<ActionAttemptOutcome> HandleEngineActionAsync(
        CharacterAgent character,
        string intent,
        FunctionCallContent call,
        CancellationToken cancellationToken)
    {
        GameAction action;
        try
        {
            action = BuildAction(call, character);
        }
        catch (ArgumentException ex)
        {
            _trace.Emit(TraceEventType.ToolCallError, new ToolCallErrorPayload
            {
                AgentName = DungeonMasterAgent.AgentIdentifier,
                CallId = call.CallId,
                ToolName = call.Name,
                Arguments = ChatTraceMapper.MapArguments(call.Arguments),
                Error = ex.Message,
                ExceptionType = ex.GetType().Name
            }, DungeonMasterAgent.AgentIdentifier);

            RecordDungeonMasterToolResult(call, $"The world could not read that action: {ex.Message}");

            var failure = new ActionAttemptOutcome
            {
                Category = ActionResolutionCategory.DmProtocolFailure,
                MessageToCharacter = "Something goes wrong and the moment slips away."
            };

            EmitAdjudication(character, intent, failure.Category, ex.Message, null, null);
            _console.CharacterRefused(character.Name, failure.MessageToCharacter);
            return failure;
        }

        // The Rulebook Resolver is stateless by design: it reads the intent and the cards and never sees the
        // world. So it cannot tell a theft from an ACCEPTANCE — "I take Rowan's purse, telling him he may
        // live" is a textbook acceptance of pending terms and reads, card-only, as a snatch. In a live v0.7
        // run that cost the release its whole point: three offers were made, `accept_surrender` was never
        // once among the DM's candidate tools, and each grab of the promised tribute counted as a hostile
        // act that destroyed the very offer it was accepting. No card wording can fix that, because the
        // component choosing the card has no way to know an offer exists. State-dependent meaning has to be
        // decided where the state lives, so it is decided here, deterministically.
        if (RedirectsToAcceptance(character, action, out var acceptance, out var acceptedOffer))
        {
            _trace.Emit(TraceEventType.ToolCallDispatched, new ToolCallDispatchPayload
            {
                AgentName = DungeonMasterAgent.AgentIdentifier,
                CallId = call.CallId,
                ToolName = call.Name,
                Arguments = ChatTraceMapper.MapArguments(call.Arguments),
                DispatchDecision =
                    $"Redirected to {DungeonMasterTools.AcceptSurrenderName} ({acceptedOffer!.Id}): the actor is the named " +
                    "recipient of a pending offer and reached for what that offer promises, which is an acceptance, not a theft."
            }, DungeonMasterAgent.AgentIdentifier);

            action = acceptance!;
        }

        // Looting the fallen is a take from their body, never a theft: an item on a dead character has already
        // moved into their corpse container, so the steal rule's own exclusion applies and the engine refuses
        // it. The resolver cannot know the target is dead either, so in the same live run a goblin burned its
        // whole turn on three rewordings of "loot the gold from dead Rowan" and hit the attempt limit, while
        // one refusal even said the belongings were "already within reach for anyone to take freely".
        if (RedirectsToCorpseLoot(action, out var corpseTake, out var corpseName))
        {
            _trace.Emit(TraceEventType.ToolCallDispatched, new ToolCallDispatchPayload
            {
                AgentName = DungeonMasterAgent.AgentIdentifier,
                CallId = call.CallId,
                ToolName = call.Name,
                Arguments = ChatTraceMapper.MapArguments(call.Arguments),
                DispatchDecision =
                    $"Redirected to {DungeonMasterTools.TakeItemName}: {corpseName} is dead, so what they carried lies " +
                    "with their body and is taken from it rather than stolen from them."
            }, DungeonMasterAgent.AgentIdentifier);

            action = corpseTake!;
        }

        // A theft must have a legitimate informational basis: the thief cannot steal an item it has no way of
        // knowing the target carries. This is enforced deterministically here, before the engine (and before
        // any RNG), from the thief's own knowledge ledger — never by revealing the target's hidden inventory.
        // The prompt guides the Dungeon Master to refuse such steals; this guard guarantees it.
        if (action is StealItemAction stealAction && StealLacksKnowledgeBasis(character, stealAction, out var basisReason))
        {
            _trace.Emit(TraceEventType.ToolCallDispatched, new ToolCallDispatchPayload
            {
                AgentName = DungeonMasterAgent.AgentIdentifier,
                CallId = call.CallId,
                ToolName = call.Name,
                Arguments = ChatTraceMapper.MapArguments(call.Arguments),
                DispatchDecision = "Not submitted to the engine: the thief has no informational basis to know the target carries that item. No RNG drawn."
            }, DungeonMasterAgent.AgentIdentifier);

            RecordDungeonMasterToolResult(call, "The character has no way of knowing the target carries such a thing; the attempt does not happen.");

            EmitInventoryInteraction(character, action, engineResult: null, rejectionReason: "NoInformationalBasis");

            var basis = await InWorldRejectionAsync(character, basisReason, cancellationToken).ConfigureAwait(false);
            EmitAdjudication(character, intent, ActionResolutionCategory.DmUnsupported, basis, action, null);
            _console.CharacterRefused(character.Name, basis);

            return new ActionAttemptOutcome
            {
                Category = ActionResolutionCategory.DmUnsupported,
                MessageToCharacter = basis,
                Action = action
            };
        }

        // Taking a specific item from a container likewise requires a legitimate basis to identify what is
        // inside it: opening or inspecting it, knowing it from before the fight, being told, or seeing the item
        // in the open. This is the authoritative backstop the reviewer asked for — even if the Dungeon Master
        // slips and names a hidden item, the take is refused here rather than becoming a real state change.
        if (action is TakeItemAction takeAction && TakeLacksKnowledgeBasis(character, takeAction, out var takeReason))
        {
            _trace.Emit(TraceEventType.ToolCallDispatched, new ToolCallDispatchPayload
            {
                AgentName = DungeonMasterAgent.AgentIdentifier,
                CallId = call.CallId,
                ToolName = call.Name,
                Arguments = ChatTraceMapper.MapArguments(call.Arguments),
                DispatchDecision = "Not submitted to the engine: the character has no informational basis to know that item is inside the container."
            }, DungeonMasterAgent.AgentIdentifier);

            RecordDungeonMasterToolResult(call, "The character has no way of knowing that is in there to reach for; the attempt does not happen.");

            var takeBasis = await InWorldRejectionAsync(character, takeReason, cancellationToken).ConfigureAwait(false);
            EmitAdjudication(character, intent, ActionResolutionCategory.DmUnsupported, takeBasis, action, null);
            _console.CharacterRefused(character.Name, takeBasis);

            return new ActionAttemptOutcome
            {
                Category = ActionResolutionCategory.DmUnsupported,
                MessageToCharacter = takeBasis,
                Action = action
            };
        }

        _trace.Emit(TraceEventType.ToolCallDispatched, new ToolCallDispatchPayload
        {
            AgentName = DungeonMasterAgent.AgentIdentifier,
            CallId = call.CallId,
            ToolName = call.Name,
            Arguments = ChatTraceMapper.MapArguments(call.Arguments),
            DispatchDecision = $"Translated to {action.Describe()} and submitted to the engine."
        }, DungeonMasterAgent.AgentIdentifier);

        // Record how the natural-language target was resolved to a specific character before the engine
        // decides the outcome, so targeting is auditable and never silently retargets.
        EmitTargetResolution(character, action, engineResultStateBefore: _engine.State);

        // The charges a limited ability had BEFORE the attempt, so a refused use is provably one that spent
        // nothing — the record must show the charge untouched, not merely say it was.
        var abilityChargesBefore = action is UseAbilityAction pendingAbility
            ? _engine.State.FindById(character.CharacterId)?.FindAbility(pendingAbility.AbilityRef)?.RemainingUses
            : null;

        // The engine, not the Dungeon Master, decides what actually happens.
        var engineResult = _engine.Execute(action);

        // Every random draw the engine made, recorded in full before its consequence, so behaviour can
        // be compared and reproduced from the trace alone rather than inferred from the final result.
        foreach (var draw in engineResult.RngDraws)
        {
            _trace.Emit(TraceEventType.RngDraw, draw);
        }

        _trace.Emit(TraceEventType.EngineAction, new EngineActionPayload
        {
            ActionType = action.ActionType,
            Action = action,
            Accepted = engineResult.Accepted,
            RejectionReason = engineResult.RejectionReason?.ToString(),
            RejectionMessage = engineResult.RejectionMessage,
            Outcome = engineResult.Outcome,
            OutcomeSummary = engineResult.Outcome?.Summary,
            StateBefore = engineResult.StateBefore,
            StateAfter = engineResult.StateAfter
        });

        // Object interactions get an extra, object-specific trace row alongside the generic engine action,
        // so container/item ids, the world-version transition and both halves of a transfer are explicit.
        EmitObjectInteraction(character, action, engineResult);

        // Exit interactions likewise get an exit-specific row, recording the open/closed transition and the
        // validation result for opening or escaping — accepted or rejected alike.
        EmitExitInteraction(character, action, engineResult);

        // Inventory transfers get an inventory-specific row (bindings, ownership before/after, turn/RNG), and
        // a successful movement additionally emits an item-provenance event so the item's journey is traceable.
        EmitInventoryInteraction(character, action, engineResult, rejectionReason: null);
        EmitItemProvenance(character, action, engineResult);

        // Status transitions and surrender-offer transitions the action caused, recorded before their
        // consequences so a modifier is never known only by its effect on a number.
        EmitStatusEvents(engineResult.StatusEvents, action.ActionType);
        EmitOfferTransitions(engineResult.OfferTransitions);

        // An ability attempt is recorded whether it was accepted or refused, with the charges either side, so
        // "a refused use spends no charge" is visible rather than asserted.
        EmitAbilityUsed(character, action, engineResult, abilityChargesBefore);

        // A redirected blow gets its own row naming both the intended and the authoritative target, and the
        // draw count, so it is provable that a guard added no roll of its own.
        EmitAttackRedirection(engineResult);

        var resultForDungeonMaster = engineResult.Accepted
            ? engineResult.Outcome!.Summary
            : $"REJECTED BY THE WORLD: {engineResult.RejectionMessage}";

        RecordDungeonMasterToolResult(call, resultForDungeonMaster);

        if (!engineResult.Accepted)
        {
            // Rendered from the rejection CODE and the names already bound in the action — not from the
            // engine's operator-facing message, and not by asking a model to turn that message into fiction.
            // The old path did both, and every run found another phrasing the leak detector did not have,
            // while the rewrites that followed a catch invented obstacles and narrated events inside a
            // refusal. A closed set of reasons renders deterministically, cannot leak, and costs no call.
            var explanation = InWorldRefusal.Render(
                engineResult.RejectionReason!.Value,
                character.Name,
                InWorldRefusal.SubjectFor(action, engineResult.RejectionReason!.Value, engineResult.StateBefore));

            EmitAdjudication(character, intent, ActionResolutionCategory.EngineRejected, explanation, action, null);
            _console.CharacterRefused(character.Name, explanation);

            return new ActionAttemptOutcome
            {
                Category = ActionResolutionCategory.EngineRejected,
                MessageToCharacter = explanation,
                Action = action,
                EngineResult = engineResult
            };
        }

        // Accepted. Each kind of action delivers its own outcome, because who learns what now differs by
        // action: a blow is narrated to the whole room; opening reveals contents only to the opener; a
        // visible take is a public event; a close inspection yields a private observation. The message
        // returned is exactly what the acting character receives as its tool result.
        var messageToCharacter = engineResult.Outcome switch
        {
            OpenContainerOutcome open => await DeliverContainerOpenedAsync(character, open, cancellationToken).ConfigureAwait(false),
            TakeItemOutcome take => DeliverItemTaken(character, take, await NarrateObjectAsync(character, take, cancellationToken).ConfigureAwait(false)),
            InspectObjectOutcome inspect => await DeliverInspectionAsync(character, inspect, cancellationToken).ConfigureAwait(false),
            OpenExitOutcome openExit => await DeliverExitOpenedAsync(character, openExit, cancellationToken).ConfigureAwait(false),
            EscapeOutcome escape => await DeliverEscapeAsync(character, escape, engineResult, cancellationToken).ConfigureAwait(false),
            OfferSurrenderOutcome offer => await DeliverOfferAsync(character, offer, cancellationToken).ConfigureAwait(false),
            AcceptSurrenderOutcome accepted => await DeliverAcceptedSurrenderAsync(character, accepted, engineResult, cancellationToken).ConfigureAwait(false),
            GuardAllyOutcome guard => await DeliverAbilityOutcomeAsync(character, guard, "ability-guard-ally", cancellationToken).ConfigureAwait(false),
            HealingPrayerOutcome heal => await DeliverAbilityOutcomeAsync(character, heal, "ability-healing-prayer", cancellationToken).ConfigureAwait(false),
            RallyOutcome rally => await DeliverAbilityOutcomeAsync(character, rally, "ability-rally", cancellationToken).ConfigureAwait(false),
            DefendOutcome defend => await DeliverAbilityOutcomeAsync(character, defend, "combat-defend", cancellationToken).ConfigureAwait(false),
            GiveItemOutcome give => await DeliverGiveAsync(character, give, cancellationToken).ConfigureAwait(false),
            DropItemOutcome drop => await DeliverDropAsync(character, drop, cancellationToken).ConfigureAwait(false),
            StealItemOutcome steal => await DeliverStealAsync(character, steal, cancellationToken).ConfigureAwait(false),
            _ => await DeliverCombatOutcomeAsync(character, engineResult, cancellationToken).ConfigureAwait(false)
        };

        EmitAdjudication(character, intent, ActionResolutionCategory.EngineAccepted, null, action, null);

        return new ActionAttemptOutcome
        {
            Category = ActionResolutionCategory.EngineAccepted,
            MessageToCharacter = messageToCharacter,
            Action = action,
            EngineResult = engineResult
        };
    }

    // -----------------------------------------------------------------------------------------
    // Outcome delivery — one path per kind, because who learns what now differs by action
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A combat outcome (a blow or a heal): narrated to the whole room. The acting character hears it at
    /// once as its tool result; everyone else hears it when their own turn begins. No private channel.
    /// </summary>
    private async Task<string> DeliverCombatOutcomeAsync(CharacterAgent character, EngineResult engineResult, CancellationToken cancellationToken)
    {
        var stateAfterText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);

        // The character whose turn it is is always the one who acted, so name them explicitly to the DM:
        // a strong "the hero attacks the monster" prior otherwise makes some models invert the actor.
        var narration = await _dungeonMaster
            .NarrateOutcomeAsync(character.Name, engineResult.Outcome!.Summary, stateAfterText, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(narration))
        {
            narration = engineResult.Outcome!.Summary;
        }

        // A lethal blow is a disposition change (Active -> Dead), recorded alongside surrender and escape so
        // every way a character leaves active combat shares one auditable event trail.
        if (engineResult.Outcome is AttackOutcome { TargetDied: true } killed)
        {
            var team = _engine.State.FindById(killed.TargetId)?.Team ?? "";
            EmitDispositionChanged(killed.TargetId, killed.TargetName, team,
                CharacterDisposition.Active, CharacterDisposition.Dead, "killed in combat",
                engineResult, exitId: null, LivingRecipients());
        }

        RecordPublicNarration("action-outcome", stateAfterText, engineResult.Outcome!.Summary, narration, character);
        _console.DungeonMaster(narration);
        return narration;
    }

    /// <summary>
    /// An exit was opened. The act is public — everyone present sees the door swing open — so every present
    /// character learns the exit is open as a public fact, and the whole room hears the narration. Opening
    /// moves no one; escaping through it is a separate act.
    /// </summary>
    private async Task<string> DeliverExitOpenedAsync(CharacterAgent character, OpenExitOutcome outcome, CancellationToken cancellationToken)
    {
        var stateAfterText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        var narration = await _dungeonMaster
            .NarratePublicEventAsync(character.Name, outcome.Summary, stateAfterText, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(narration))
        {
            narration = outcome.Summary;
        }

        var worldVersion = _engine.State.Version;
        var fact = _knowledge.GetOrAddExitOpenedFact(outcome.ExitId, outcome.ExitName, worldVersion);
        TraceFactCreatedIfNew(fact, KnowledgeSource.PublicEvent, "open_exit", character.Name);
        var recipients = LivingRecipients();
        DeliverPublicFact(fact.Fact, recipients, worldVersion, "open_exit");

        RecordPublicNarration("exit-opened", stateAfterText, outcome.Summary, narration, character, [fact.Fact.Id]);
        _console.DungeonMaster(narration);
        return narration;
    }

    /// <summary>
    /// A surrender offer was put on the table. Public: everyone present heard the terms, so the offer and its
    /// exact terms become a public fact and the whole room hears the narration.
    /// </summary>
    /// <remarks>
    /// Nothing else happens here, deliberately. No disposition changes, no asset moves, and no
    /// <see cref="TraceEventType.CharacterSurrendered"/> event is emitted: an offer is a proposal, and the
    /// offerer is still an active, targetable combatant. Only <see cref="DeliverAcceptedSurrenderAsync"/> ends
    /// anyone's fight.
    /// </remarks>
    private async Task<string> DeliverOfferAsync(
        CharacterAgent character, OfferSurrenderOutcome outcome, CancellationToken cancellationToken)
    {
        var stateAfterText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        var narration = await _dungeonMaster
            .NarratePublicEventAsync(character.Name, outcome.Summary, stateAfterText, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(narration))
        {
            narration = outcome.Summary;
        }

        var worldVersion = _engine.State.Version;
        var recipients = LivingRecipients();
        var fact = _knowledge.GetOrAddSurrenderOfferFact(
            outcome.OfferId, outcome.OffererName, outcome.RecipientName, outcome.TermsDescription, worldVersion);
        TraceFactCreatedIfNew(fact, KnowledgeSource.PublicEvent, "offer_surrender", character.Name);
        DeliverPublicFact(fact.Fact, recipients, worldVersion, "offer_surrender");

        _trace.Emit(TraceEventType.SurrenderOfferMade, new SurrenderOfferMadePayload
        {
            OfferId = outcome.OfferId,
            OffererId = outcome.OffererId,
            OffererName = outcome.OffererName,
            RecipientId = outcome.RecipientId,
            RecipientName = outcome.RecipientName,
            OfferedItemIds = [.. _engine.State.FindOffer(outcome.OfferId)?.OfferedItemIds ?? []],
            OfferedItemNames = outcome.OfferedItemNames,
            ForfeitWeapon = outcome.ForfeitWeapon,
            WeaponName = outcome.WeaponName,
            AssociatedSpeechEventId = outcome.AssociatedSpeechEventId,
            AssociatedSpeech = SpeechTextFor(outcome.AssociatedSpeechEventId),
            Round = _trace.Round,
            Turn = _trace.Turn,
            BattleStateSummary = DescribeVisibleBattleState(),
            PublicRecipients = recipients
        }, character.Name);

        RecordPublicNarration("surrender-offered", stateAfterText, outcome.Summary, narration, character, [fact.Fact.Id]);
        _console.DungeonMaster(narration);
        return narration;
    }

    /// <summary>
    /// A surrender offer was accepted. Public: everyone present sees the tribute change hands and the offerer
    /// disarmed, so every present character learns it as a public fact. This is where a character's fight
    /// actually ends: the disposition change (Active -> Surrendered), the durable agreement, and the focused
    /// surrender event all belong here, not to the offer.
    /// </summary>
    private async Task<string> DeliverAcceptedSurrenderAsync(
        CharacterAgent character, AcceptSurrenderOutcome outcome, EngineResult engineResult, CancellationToken cancellationToken)
    {
        var stateAfterText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        var narration = await _dungeonMaster
            .NarratePublicEventAsync(character.Name, outcome.Summary, stateAfterText, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(narration))
        {
            narration = outcome.Summary;
        }

        var worldVersion = _engine.State.Version;
        var recipients = LivingRecipients();
        var offererTeam = _engine.State.FindById(outcome.OffererId)?.Team ?? "";

        var fact = _knowledge.GetOrAddSurrenderFact(outcome.OffererId, outcome.OffererName, worldVersion);
        TraceFactCreatedIfNew(fact, KnowledgeSource.PublicEvent, "accept_surrender", character.Name);
        DeliverPublicFact(fact.Fact, recipients, worldVersion, "accept_surrender");

        // Every item that changed hands is a public transfer in its own right, so a later theft or take has a
        // legitimate informational basis for it — exactly as an ordinary give does.
        var offer = _engine.State.SurrenderOffers.FirstOrDefault(o => o.Id == outcome.OfferId);
        var transferredIds = offer is null ? [] : offer.OfferedItemIds;
        for (var index = 0; index < transferredIds.Length && index < outcome.TransferredItemNames.Count; index++)
        {
            var itemFact = _knowledge.GetOrAddGiveFact(
                transferredIds[index], outcome.TransferredItemNames[index],
                outcome.OffererName, outcome.AccepterName, worldVersion);
            TraceFactCreatedIfNew(itemFact, KnowledgeSource.PublicEvent, "accept_surrender", character.Name);
            DeliverPublicFact(itemFact.Fact, recipients, worldVersion, "accept_surrender");
        }

        if (outcome.ForfeitedWeaponName is { } weaponName)
        {
            var weaponId = _engine.State.SurrenderAgreements
                .FirstOrDefault(a => a.Id == outcome.AgreementId)?.ForfeitedWeaponId ?? weaponName;
            var weaponFact = _knowledge.GetOrAddDropFact(weaponId, weaponName, outcome.OffererName, worldVersion);
            TraceFactCreatedIfNew(weaponFact, KnowledgeSource.PublicEvent, "accept_surrender", character.Name);
            DeliverPublicFact(weaponFact.Fact, recipients, worldVersion, "accept_surrender");
        }

        EmitDispositionChanged(outcome.OffererId, outcome.OffererName, offererTeam,
            CharacterDisposition.Active, CharacterDisposition.Surrendered, "accepted surrender",
            engineResult, exitId: null, recipients);

        _trace.Emit(TraceEventType.CharacterSurrendered, new CharacterSurrenderedPayload
        {
            CharacterId = outcome.OffererId,
            CharacterName = outcome.OffererName,
            Team = offererTeam,
            Round = _trace.Round,
            Turn = _trace.Turn,
            PublicRecipients = recipients
        }, outcome.OffererName);

        var agreement = _engine.State.SurrenderAgreements.FirstOrDefault(a => a.Id == outcome.AgreementId);
        _trace.Emit(TraceEventType.SurrenderAgreementRecorded, new SurrenderAgreementPayload
        {
            AgreementId = outcome.AgreementId,
            OfferId = outcome.OfferId,
            OffererId = outcome.OffererId,
            OffererName = outcome.OffererName,
            AcceptedById = outcome.AccepterId,
            AcceptedByName = outcome.AccepterName,
            TransferredItemIds = [.. transferredIds],
            TransferredItemNames = outcome.TransferredItemNames,
            ForfeitedWeaponId = agreement?.ForfeitedWeaponId,
            ForfeitedWeaponName = agreement?.ForfeitedWeaponId is null ? null : outcome.ForfeitedWeaponName,
            WeaponDisposition = outcome.ForfeitedWeaponName is null
                ? "no weapon left the offerer's hand"
                : $"laid on {outcome.GroundContainerName}, where it can be taken as a trophy",
            OffererDisarmed = _engine.State.FindById(outcome.OffererId)?.IsDisarmed ?? false,
            AcceptedRound = agreement?.AcceptedRound ?? _trace.Round,
            AcceptedTurn = agreement?.AcceptedTurn ?? _trace.Turn,
            AssociatedSpeechEventId = outcome.AssociatedSpeechEventId,
            AssociatedSpeech = SpeechTextFor(outcome.AssociatedSpeechEventId),
            PublicRecipients = recipients
        }, character.Name);

        // The tribute and the forfeited weapon are item movements like any other, so each gets a provenance
        // row — distinguishable from an ordinary give by its action type.
        EmitSurrenderProvenance(character, outcome, transferredIds, agreement, engineResult);

        RecordPublicNarration("surrender-accepted", stateAfterText, outcome.Summary, narration, character, [fact.Fact.Id]);
        _console.DungeonMaster(narration);
        return narration;
    }

    /// <summary>
    /// An ability resolved — a guard taken up, a prayer worked, an order barked, a guard braced. All four are
    /// public and observable acts with no hidden component, so the whole room hears the narration and there is
    /// no private channel. The status effects the ability applied are traced separately, from the engine result.
    /// </summary>
    private async Task<string> DeliverAbilityOutcomeAsync(
        CharacterAgent character, ActionOutcome outcome, string purpose, CancellationToken cancellationToken)
    {
        var stateAfterText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        var narration = await _dungeonMaster
            .NarratePublicEventAsync(character.Name, outcome.Summary, stateAfterText, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(narration))
        {
            narration = outcome.Summary;
        }

        RecordPublicNarration(purpose, stateAfterText, outcome.Summary, narration, character);
        _console.DungeonMaster(narration);
        return narration;
    }

    /// <summary>
    /// A character escaped through an open exit. Public: everyone still present sees them go, so every
    /// present character (which no longer includes the escaper) learns it as a public fact. It is a
    /// disposition change (Active -> Escaped) recorded as such, plus a focused escape event.
    /// </summary>
    private async Task<string> DeliverEscapeAsync(
        CharacterAgent character, EscapeOutcome outcome, EngineResult engineResult, CancellationToken cancellationToken)
    {
        var stateAfterText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        var narration = await _dungeonMaster
            .NarratePublicEventAsync(character.Name, outcome.Summary, stateAfterText, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(narration))
        {
            narration = outcome.Summary;
        }

        var worldVersion = _engine.State.Version;
        var team = _engine.State.FindById(character.CharacterId)?.Team ?? "";
        var fact = _knowledge.GetOrAddEscapeFact(character.CharacterId, character.Name, outcome.ExitName, worldVersion);
        TraceFactCreatedIfNew(fact, KnowledgeSource.PublicEvent, "escape_encounter", character.Name);

        // The escaper is already gone, so the present recipients no longer include them.
        var recipients = LivingRecipients();
        DeliverPublicFact(fact.Fact, recipients, worldVersion, "escape_encounter");

        EmitDispositionChanged(character.CharacterId, character.Name, team,
            CharacterDisposition.Active, CharacterDisposition.Escaped, "escape_encounter",
            engineResult, exitId: outcome.ExitId, recipients);

        _trace.Emit(TraceEventType.CharacterEscaped, new CharacterEscapedPayload
        {
            CharacterId = character.CharacterId,
            CharacterName = character.Name,
            Team = team,
            ExitId = outcome.ExitId,
            ExitName = outcome.ExitName,
            DestinationDescription = outcome.DestinationDescription,
            Round = _trace.Round,
            Turn = _trace.Turn,
            PublicRecipients = recipients
        }, character.Name);

        RecordPublicNarration("character-escaped", stateAfterText, outcome.Summary, narration, character, [fact.Fact.Id]);
        _console.DungeonMaster(narration);
        return narration;
    }

    /// <summary>
    /// Delivers a public knowledge fact to every present recipient and records the public delivery — the
    /// shared tail of every v0.5 public event (exit opened, surrender, escape).
    /// </summary>
    private void DeliverPublicFact(
        Knowledge.KnowledgeFact fact, IReadOnlyList<string> recipients, int worldVersion, string sourceEvent)
    {
        foreach (var recipientId in recipients)
        {
            var name = _engine.State.FindById(recipientId)?.Name ?? recipientId;
            LearnAndTrace(recipientId, name, fact, KnowledgeSource.PublicEvent, worldVersion, "public", recipients, sourceEvent);
        }

        _trace.Emit(TraceEventType.PublicFactDelivered, new PublicFactDeliveredPayload
        {
            Fact = fact.Description,
            Recipients = recipients,
            RelatedFactIds = [fact.Id],
            WorldVersion = worldVersion,
            SourceEvent = sourceEvent
        });
    }

    /// <summary>Records a character's disposition change — surrender, escape or death — as one auditable event.</summary>
    private void EmitDispositionChanged(
        string characterId, string characterName, string team,
        CharacterDisposition previous, CharacterDisposition next, string cause,
        EngineResult engineResult, string? exitId, IReadOnlyList<string> publicRecipients) =>
        _trace.Emit(TraceEventType.DispositionChanged, new DispositionChangedPayload
        {
            CharacterId = characterId,
            CharacterName = characterName,
            Team = team,
            PreviousDisposition = previous.ToString(),
            NewDisposition = next.ToString(),
            Cause = cause,
            Round = _trace.Round,
            Turn = _trace.Turn,
            WorldVersionBefore = engineResult.StateBefore.Version,
            WorldVersionAfter = engineResult.StateAfter.Version,
            ExitId = exitId,
            PublicRecipients = publicRecipients
        }, characterName);

    /// <summary>Narrates an object outcome (open/take) to the room, returning the public narration text.</summary>
    private async Task<string> NarrateObjectAsync(CharacterAgent character, ActionOutcome outcome, CancellationToken cancellationToken)
    {
        var stateAfterText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        var narration = await _dungeonMaster
            .NarrateObjectOutcomeAsync(character.Name, outcome.Summary,
                DescribeObjectTransition(outcome), stateAfterText, cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(narration) ? outcome.Summary : narration;
    }

    /// <summary>
    /// Opening a container. The act is public — everyone present sees the lid go up, so every living
    /// character learns the container is open as a public fact. Its contents are a separate matter: the
    /// opener alone directly observes them, recorded as private <c>OpenedContainer</c> knowledge and
    /// delivered as a private observation. Being open does not make the contents public.
    /// </summary>
    private async Task<string> DeliverContainerOpenedAsync(CharacterAgent character, OpenContainerOutcome outcome, CancellationToken cancellationToken)
    {
        var publicNarration = await NarrateObjectAsync(character, outcome, cancellationToken).ConfigureAwait(false);

        var stateAfterText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        RecordPublicNarration("container-opened", stateAfterText, outcome.Summary, publicNarration, character);
        _console.DungeonMaster(publicNarration);

        var worldVersion = _engine.State.Version;

        // Opening is a public act: everyone present can see the lid is up, so every living character learns
        // the container is open. The contents are not part of this fact — only the opener observes those.
        var openedFact = _knowledge.GetOrAddOpenedFact(outcome.ContainerId, outcome.ContainerName, worldVersion);
        TraceFactCreatedIfNew(openedFact, KnowledgeSource.PublicEvent, "open_container", character.Name);
        var openRecipients = LivingRecipients();
        foreach (var recipientId in openRecipients)
        {
            var name = _engine.State.FindById(recipientId)?.Name ?? recipientId;
            LearnAndTrace(recipientId, name, openedFact.Fact, KnowledgeSource.PublicEvent, worldVersion,
                "public", openRecipients, "open_container");
        }
        _trace.Emit(TraceEventType.PublicFactDelivered, new PublicFactDeliveredPayload
        {
            Fact = openedFact.Fact.Description,
            Recipients = openRecipients,
            RelatedFactIds = [openedFact.Fact.Id],
            WorldVersion = worldVersion,
            SourceEvent = "open_container"
        });

        // The opener directly observes the current contents, at the post-open world version.
        var container = FindContainer(outcome.ContainerId);
        var contents = container is null ? [] : container.Contents;
        var fact = _knowledge.GetOrAddContentsFact(outcome.ContainerId, outcome.ContainerName, contents, worldVersion);
        TraceFactCreatedIfNew(fact, KnowledgeSource.OpenedContainer, "open_container", character.Name);
        LearnAndTrace(character.CharacterId, character.Name, fact.Fact, KnowledgeSource.OpenedContainer, worldVersion,
            "private", [character.CharacterId], "open_container");

        var observation = outcome.RevealedContents.Count == 0
            ? $"You look inside the {outcome.ContainerName}. It is empty."
            : $"You look inside the {outcome.ContainerName}. Inside, you see {NaturalJoin(outcome.RevealedContents)}.";
        DeliverPrivateObservation(character, observation, [fact.Fact.Id], worldVersion, "open_container");

        return $"{publicNarration}\n\n{observation}";
    }

    /// <summary>
    /// Taking a visibly identifiable item. The removal is a public event: everyone alive learns that the
    /// item is now carried, and the whole room hears the narration. There is no private channel.
    /// </summary>
    private string DeliverItemTaken(CharacterAgent character, TakeItemOutcome outcome, string publicNarration)
    {
        var stateAfterText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        var worldVersion = _engine.State.Version;

        var fact = _knowledge.GetOrAddRemovalFact(
            outcome.ItemId, outcome.ItemName, outcome.ActorName, outcome.ContainerName, outcome.ContainerId, worldVersion);
        TraceFactCreatedIfNew(fact, KnowledgeSource.PublicEvent, "take_item", character.Name);

        var recipients = LivingRecipients();
        foreach (var recipientId in recipients)
        {
            var name = _engine.State.FindById(recipientId)?.Name ?? recipientId;
            LearnAndTrace(recipientId, name, fact.Fact, KnowledgeSource.PublicEvent, worldVersion, "public", recipients, "take_item");
        }

        _trace.Emit(TraceEventType.PublicFactDelivered, new PublicFactDeliveredPayload
        {
            Fact = fact.Fact.Description,
            Recipients = recipients,
            RelatedFactIds = [fact.Fact.Id],
            WorldVersion = worldVersion,
            SourceEvent = "take_item"
        });

        RecordPublicNarration("item-taken", stateAfterText, outcome.Summary, publicNarration, character, [fact.Fact.Id]);
        _console.DungeonMaster(publicNarration);
        return publicNarration;
    }

    /// <summary>
    /// One character gave an item to another. The transfer is a public event: everyone present sees the item
    /// change hands, so every present character learns who now carries it, and the whole room hears the
    /// narration. There is no private channel and no RNG.
    /// </summary>
    private async Task<string> DeliverGiveAsync(CharacterAgent character, GiveItemOutcome outcome, CancellationToken cancellationToken)
    {
        var stateAfterText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        var narration = await _dungeonMaster
            .NarratePublicEventAsync(character.Name, outcome.Summary, stateAfterText, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(narration))
        {
            narration = outcome.Summary;
        }

        var worldVersion = _engine.State.Version;
        var fact = _knowledge.GetOrAddGiveFact(outcome.ItemId, outcome.ItemName, outcome.GiverName, outcome.RecipientName, worldVersion);
        TraceFactCreatedIfNew(fact, KnowledgeSource.PublicEvent, "give_item", character.Name);
        DeliverPublicFact(fact.Fact, LivingRecipients(), worldVersion, "give_item");

        RecordPublicNarration("item-given", stateAfterText, outcome.Summary, narration, character, [fact.Fact.Id]);
        _console.DungeonMaster(narration);
        return narration;
    }

    /// <summary>
    /// A character dropped an item on the floor. Public: everyone present sees it fall and learns it now lies
    /// on the floor within anyone's reach, and the whole room hears the narration. No private channel, no RNG.
    /// </summary>
    private async Task<string> DeliverDropAsync(CharacterAgent character, DropItemOutcome outcome, CancellationToken cancellationToken)
    {
        var stateAfterText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        var narration = await _dungeonMaster
            .NarratePublicEventAsync(character.Name, outcome.Summary, stateAfterText, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(narration))
        {
            narration = outcome.Summary;
        }

        var worldVersion = _engine.State.Version;
        var fact = _knowledge.GetOrAddDropFact(outcome.ItemId, outcome.ItemName, outcome.ActorName, worldVersion);
        TraceFactCreatedIfNew(fact, KnowledgeSource.PublicEvent, "drop_item", character.Name);
        DeliverPublicFact(fact.Fact, LivingRecipients(), worldVersion, "drop_item");

        RecordPublicNarration("item-dropped", stateAfterText, outcome.Summary, narration, character, [fact.Fact.Id]);
        _console.DungeonMaster(narration);
        return narration;
    }

    /// <summary>
    /// A theft attempt. Public in v0.6: every attempt is noticed, so everyone present learns who tried to
    /// steal what from whom and whether it succeeded, and the whole room hears the narration. Exactly one RNG
    /// draw has already decided it inside the engine; this only delivers the public outcome. No private channel.
    /// </summary>
    private async Task<string> DeliverStealAsync(CharacterAgent character, StealItemOutcome outcome, CancellationToken cancellationToken)
    {
        var stateAfterText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        var narration = await _dungeonMaster
            .NarratePublicEventAsync(character.Name, outcome.Summary, stateAfterText, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(narration))
        {
            narration = outcome.Summary;
        }

        var worldVersion = _engine.State.Version;
        var fact = _knowledge.GetOrAddTheftFact(
            outcome.ItemId, outcome.ItemName, outcome.ThiefName, outcome.TargetName, outcome.Succeeded, worldVersion);
        TraceFactCreatedIfNew(fact, KnowledgeSource.PublicEvent, "steal_item", character.Name);
        DeliverPublicFact(fact.Fact, LivingRecipients(), worldVersion, "steal_item");

        RecordPublicNarration("item-theft", stateAfterText, outcome.Summary, narration, character, [fact.Fact.Id]);
        _console.DungeonMaster(narration);
        return narration;
    }

    /// <summary>
    /// A close inspection. The room learns only that the character examined the object; the inspector alone
    /// receives the discovered marking and (for an open container) the current contents, as private
    /// <c>DirectInspection</c> knowledge and a private observation. No RNG, no mutation.
    /// </summary>
    private async Task<string> DeliverInspectionAsync(CharacterAgent character, InspectObjectOutcome outcome, CancellationToken cancellationToken)
    {
        // Inspection does not change the world, so the observation is at the current version.
        var worldVersion = _engine.State.Version;
        var discoveredFactIds = new List<string>();
        var learnedSomethingNew = false;

        if (outcome.ExteriorClue is { } clue)
        {
            var marking = _knowledge.GetOrAddMarkingFact(outcome.ObjectId, clue);
            TraceFactCreatedIfNew(marking, KnowledgeSource.DirectInspection, "inspect_object", character.Name);
            discoveredFactIds.Add(marking.Fact.Id);
            learnedSomethingNew |= LearnAndTrace(character.CharacterId, character.Name, marking.Fact,
                KnowledgeSource.DirectInspection, worldVersion, "private", [character.CharacterId], "inspect_object");
        }

        if (outcome is { IsContainer: true, IsOpen: true })
        {
            var container = FindContainer(outcome.ObjectId);
            var contents = container is null ? [] : container.Contents;
            var contentsFact = _knowledge.GetOrAddContentsFact(outcome.ObjectId, outcome.ObjectName, contents, worldVersion);
            TraceFactCreatedIfNew(contentsFact, KnowledgeSource.DirectInspection, "inspect_object", character.Name);
            discoveredFactIds.Add(contentsFact.Fact.Id);
            learnedSomethingNew |= LearnAndTrace(character.CharacterId, character.Name, contentsFact.Fact,
                KnowledgeSource.DirectInspection, worldVersion, "private", [character.CharacterId], "inspect_object");
        }

        _trace.Emit(TraceEventType.ObjectInspected, new ObjectInspectedPayload
        {
            ActorId = character.CharacterId,
            ActorName = character.Name,
            ObjectId = outcome.ObjectId,
            ObjectName = outcome.ObjectName,
            WasOpen = outcome.IsOpen,
            DiscoveredFactIds = discoveredFactIds,
            LearnedSomethingNew = learnedSomethingNew,
            WorldVersion = worldVersion
        }, character.Name);

        var stateAfterText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        var publicNarration = await _dungeonMaster
            .NarrateInspectionAsync(character.Name, outcome.ObjectName, stateAfterText, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(publicNarration))
        {
            publicNarration = outcome.Summary;
        }

        RecordPublicNarration("object-inspected", stateAfterText, outcome.Summary, publicNarration, character);
        _console.DungeonMaster(publicNarration);

        var observation = ComposeInspectionObservation(outcome, learnedSomethingNew);
        DeliverPrivateObservation(character, observation, discoveredFactIds, worldVersion, "inspect_object");

        return $"{publicNarration}\n\n{observation}";
    }

    private static string ComposeInspectionObservation(InspectObjectOutcome outcome, bool learnedSomethingNew)
    {
        if (!learnedSomethingNew)
        {
            return $"You examine the {outcome.ObjectName} closely, but learn nothing beyond what you already know.";
        }

        var parts = new List<string> { $"You examine the {outcome.ObjectName} closely." };
        if (outcome.ExteriorClue is { } clue)
        {
            parts.Add(clue);
        }

        if (outcome is { IsContainer: true, IsOpen: true })
        {
            parts.Add(outcome.CurrentContents.Count == 0
                ? $"The {outcome.ObjectName} is currently empty."
                : $"The {outcome.ObjectName} currently holds {NaturalJoin(outcome.CurrentContents)}.");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// The exact before→after change for an object action, handed to the narration so the Dungeon Master
    /// describes only what happened. It is spelled out because a take from an already-open container was
    /// otherwise narrated as the lid being lifted; the transition makes "already open, stays open" explicit.
    /// </summary>
    private string DescribeObjectTransition(ActionOutcome outcome) => outcome switch
    {
        OpenContainerOutcome o =>
            $"{o.ContainerName} went from CLOSED to OPEN and {o.ActorName} looked inside. Nothing was taken out. " +
            $"Do NOT state what is inside: {o.ActorName} can see the contents, but they are {o.ActorName}'s to see, " +
            "not the room's — narrate only that the lid is up and " + $"{o.ActorName} is looking in.",
        TakeItemOutcome t => DescribeTakeTransition(t),
        _ => outcome.Summary
    };

    /// <summary>
    /// The transition hint for a take, phrased for what was taken FROM — an ordinary open container, a fallen
    /// character's body, or the floor. Corpses and the floor are not chests: a body is never "opened" or
    /// "reached into", and the floor holds things simply lying there. Getting this right stops the engine's
    /// container representation ("the open corpse", "reaches into") from leaking into the fiction.
    /// </summary>
    private string DescribeTakeTransition(TakeItemOutcome t)
    {
        var container = FindContainer(t.ContainerId);
        if (container is { IsCorpse: true })
        {
            return $"The {t.ItemName} was on {t.ContainerName} — a fallen character's BODY, not a chest or a " +
                   $"container. Narrate {t.ActorName} taking the {t.ItemName} from the body in plain terms " +
                   $"(stooping over the fallen, lifting it from a belt or slack hand). NEVER call the body " +
                   $"'open' or a 'container', never say {t.ActorName} 'reaches into' or 'opens' it, and never " +
                   $"mention a lid. The only change is that {t.ActorName} now holds the {t.ItemName} in plain sight.";
        }

        if (container is { IsGround: true })
        {
            return $"The {t.ItemName} was lying on {t.ContainerName} in plain sight. Narrate {t.ActorName} " +
                   $"simply picking the {t.ItemName} up off the floor. Do NOT describe a container, a lid, or " +
                   $"reaching inside anything. The only change is that {t.ActorName} now holds it.";
        }

        return $"{t.ContainerName} was ALREADY OPEN and stays open — it is not opened in this moment and no lid " +
               $"is lifted. The only change is that {t.ActorName} took the {t.ItemName} out of it and now holds it " +
               "in plain sight, where everyone can see what it is.";
    }

    /// <summary>
    /// Records a public narration on the shared channel, delivered to the acting character at once and to
    /// everyone else when their own turn begins, and traces both the narration and that first delivery.
    /// </summary>
    private void RecordPublicNarration(
        string purpose,
        string stateText,
        string context,
        string narration,
        CharacterAgent actor,
        IReadOnlyList<string>? relatedFactIds = null)
    {
        var entry = _narrationLog.Record(purpose, narration);
        entry.MarkDeliveredTo(actor.CharacterId);

        _trace.Emit(TraceEventType.Narration, new NarrationPayload
        {
            Purpose = purpose,
            StateSuppliedToDungeonMaster = stateText,
            ContextSuppliedToDungeonMaster = context,
            Narration = narration,
            NarrationId = entry.Id,
            IntendedRecipients = LivingRecipients(),
            Visibility = "public",
            WorldVersion = _engine.State.Version,
            RelatedFactIds = relatedFactIds ?? []
        }, DungeonMasterAgent.AgentIdentifier);

        _trace.Emit(TraceEventType.NarrationDelivered, new NarrationDeliveredPayload
        {
            NarrationId = entry.Id,
            Narration = narration,
            DeliveredTo = [actor.CharacterId],
            DeliveryMechanism = "take_action tool result"
        });
    }

    /// <summary>Delivers a private observation to exactly one character and traces it. Never reaches anyone else.</summary>
    private void DeliverPrivateObservation(
        CharacterAgent character,
        string observation,
        IReadOnlyList<string> relatedFactIds,
        int worldVersion,
        string sourceEvent)
    {
        _console.PrivateObservation(character.Name, observation);

        _trace.Emit(TraceEventType.PrivateObservationDelivered, new PrivateObservationDeliveredPayload
        {
            RecipientId = character.CharacterId,
            RecipientName = character.Name,
            Observation = observation,
            RelatedFactIds = relatedFactIds,
            WorldVersion = worldVersion,
            SourceEvent = sourceEvent
        }, character.Name);
    }

    private void TraceFactCreatedIfNew(KnowledgeLedger.FactResult fact, KnowledgeSource source, string relatedAction, string actor)
    {
        if (fact.WasCreated)
        {
            KnowledgeTracing.FactCreated(_trace, fact.Fact, source, relatedAction, actor);
        }
    }

    /// <summary>
    /// Records a character learning a fact and, when it is genuinely new to them, traces it. Returns whether
    /// it was new, so callers can tell an inspector who found something from one who learned nothing.
    /// </summary>
    private bool LearnAndTrace(
        string characterId,
        string characterName,
        Knowledge.KnowledgeFact fact,
        KnowledgeSource source,
        int observedWorldVersion,
        string visibility,
        IReadOnlyList<string> recipients,
        string relatedAction)
    {
        var record = _knowledge.Learn(characterId, fact.Id, source,
            _trace.Round, _trace.Turn, observedWorldVersion);
        if (record is null)
        {
            return false;
        }

        KnowledgeTracing.FactLearned(_trace, fact, record, characterName, visibility, recipients, relatedAction, characterName);
        return true;
    }

    private Container? FindContainer(string containerId) =>
        _engine.State.Objects.OfType<Container>()
            .FirstOrDefault(c => string.Equals(c.Id, containerId, StringComparison.OrdinalIgnoreCase));

    private static string NaturalJoin(IReadOnlyList<string> values) => values.Count switch
    {
        0 => "nothing",
        1 => values[0],
        2 => $"{values[0]} and {values[1]}",
        _ => $"{string.Join(", ", values.Take(values.Count - 1))}, and {values[^1]}"
    };

    /// <summary>The neutral in-world line used when even a rephrase still leaks the machinery.</summary>
    private const string GenericInWorldRefusal = InWorldRefusal.Generic;

    /// <summary>
    /// Guarantees a rejection reason reaches the character in-world. A prompt cannot reliably stop the
    /// Dungeon Master naming the machinery ("the world cannot resolve that", or even listing the actions),
    /// so a leaked reason is caught here and rephrased once; if the rephrase still leaks, a neutral in-world
    /// line is used instead. The original leak is recorded on an <see cref="TraceEventType.AdjudicationCorrected"/>
    /// event, so nothing the model produced is hidden.
    /// </summary>
    private async Task<string> InWorldRejectionAsync(CharacterAgent character, string reason, CancellationToken cancellationToken)
    {
        // Formatting is cleaned, never rewritten: markdown in a refusal is a presentation defect with a
        // deterministic fix, and sending it round a model risks a worse rewrite of words that were fine.
        reason = ModelText.StripPresentationMarkup(reason);

        if (!MachineryLanguage.IsLeak(reason))
        {
            return reason;
        }

        var rephrased = await _dungeonMaster.RephraseRejectionInWorldAsync(reason, character.Name, cancellationToken)
            .ConfigureAwait(false);

        var corrected = !string.IsNullOrWhiteSpace(rephrased) && !MachineryLanguage.IsLeak(rephrased)
            ? rephrased
            : GenericInWorldRefusal;

        _trace.Emit(TraceEventType.AdjudicationCorrected, new AdjudicationCorrectionPayload
        {
            ToolName = DungeonMasterTools.RejectActionName,
            Parameter = "reason",
            DungeonMasterValue = reason,
            CorrectedValue = corrected,
            Justification = "The refusal named the machinery (a tool name, a stable id, or one of the harness's own nouns); rephrased in-world once, with a neutral line as the fallback."
        }, DungeonMasterAgent.AgentIdentifier);

        return corrected;
    }

    /// <summary>
    /// Guarantees a question answer reaches the character in character. The same detector that catches a
    /// machinery-leaking refusal also catches an answer that parrots the knowledge-view scaffolding ("you
    /// directly know…", "you have not been told…") or uses markdown; a leaked answer is rephrased in-world
    /// once. Unlike a refusal, a leaked answer already respects the information boundary (it just says it
    /// clumsily), so if the rephrase still leaks the original answer is kept rather than a generic line —
    /// losing the perception would be worse than an ugly register. The correction is traced either way.
    /// </summary>
    private async Task<string> InWorldAnswerAsync(CharacterAgent character, string answer, CancellationToken cancellationToken)
    {
        answer = ModelText.StripPresentationMarkup(answer);

        if (!MachineryLanguage.IsLeak(answer))
        {
            return answer;
        }

        var rephrased = await _dungeonMaster.RephraseAnswerInWorldAsync(answer, character.Name, cancellationToken)
            .ConfigureAwait(false);

        var corrected = !string.IsNullOrWhiteSpace(rephrased) && !MachineryLanguage.IsLeak(rephrased)
            ? rephrased
            : answer;

        _trace.Emit(TraceEventType.AdjudicationCorrected, new AdjudicationCorrectionPayload
        {
            ToolName = CharacterTools.AskDmName,
            Parameter = "answer",
            DungeonMasterValue = answer,
            CorrectedValue = corrected,
            Justification = "The answer named the machinery (a tool name, a stable id, or one of the harness's own nouns); rephrased in-world once, keeping the original if the rephrase still leaked."
        }, DungeonMasterAgent.AgentIdentifier);

        return corrected;
    }

    private ActionAttemptOutcome HandleUnknownDungeonMasterTool(CharacterAgent character, string intent, FunctionCallContent call)
    {
        _trace.Emit(TraceEventType.ToolCallError, new ToolCallErrorPayload
        {
            AgentName = DungeonMasterAgent.AgentIdentifier,
            CallId = call.CallId,
            ToolName = call.Name,
            Arguments = ChatTraceMapper.MapArguments(call.Arguments),
            Error = "Dungeon Master requested a tool it was never given."
        }, DungeonMasterAgent.AgentIdentifier);

        RecordDungeonMasterToolResult(call, $"There is no such action as '{call.Name}'.");

        var outcome = new ActionAttemptOutcome
        {
            Category = ActionResolutionCategory.DmProtocolFailure,
            MessageToCharacter = "Nothing comes of the attempt."
        };

        EmitAdjudication(character, intent, outcome.Category, $"Unknown tool '{call.Name}'.", null, null);
        _console.CharacterRefused(character.Name, outcome.MessageToCharacter);
        return outcome;
    }

    /// <summary>Captures the tool arguments and turns them into a structured engine action.</summary>
    private GameAction BuildAction(FunctionCallContent call, CharacterAgent character)
    {
        switch (call.Name)
        {
            case DungeonMasterTools.AttackCharacterName:
                return new AttackCharacterAction(
                    ResolveActingCharacter(call, DungeonMasterTools.AttackerParameter, character),
                    ToolArguments.GetRequiredString(call, DungeonMasterTools.TargetParameter),
                    ToolArguments.GetRequiredString(call, DungeonMasterTools.WeaponParameter));

            case DungeonMasterTools.UseItemName:
                return new UseItemAction(
                    ResolveActingCharacter(call, DungeonMasterTools.ActorParameter, character),
                    ToolArguments.GetRequiredString(call, DungeonMasterTools.ItemParameter),
                    ToolArguments.GetString(call, DungeonMasterTools.TargetParameter));

            case DungeonMasterTools.OpenContainerName:
                return new OpenContainerAction(
                    ResolveActingCharacter(call, DungeonMasterTools.ActorParameter, character),
                    ToolArguments.GetRequiredString(call, DungeonMasterTools.ContainerParameter));

            case DungeonMasterTools.TakeItemName:
                return new TakeItemAction(
                    ResolveActingCharacter(call, DungeonMasterTools.ActorParameter, character),
                    ToolArguments.GetRequiredString(call, DungeonMasterTools.ContainerParameter),
                    ToolArguments.GetRequiredString(call, DungeonMasterTools.ItemParameter));

            case DungeonMasterTools.InspectObjectName:
                return new InspectObjectAction(
                    ResolveActingCharacter(call, DungeonMasterTools.ActorParameter, character),
                    ToolArguments.GetRequiredString(call, DungeonMasterTools.ObjectParameter));

            case DungeonMasterTools.OpenExitName:
                return new OpenExitAction(
                    ResolveActingCharacter(call, DungeonMasterTools.ActorParameter, character),
                    ResolveExitReference(call));

            case DungeonMasterTools.EscapeEncounterName:
                return new EscapeEncounterAction(
                    ResolveActingCharacter(call, DungeonMasterTools.ActorParameter, character),
                    ResolveExitReference(call));

            case DungeonMasterTools.OfferSurrenderName:
                return new OfferSurrenderAction(
                    ResolveActingCharacter(call, DungeonMasterTools.OffererParameter, character),
                    ToolArguments.GetRequiredString(call, DungeonMasterTools.RecipientParameter),
                    ToolArguments.GetStringList(call, DungeonMasterTools.OfferedItemsParameter),
                    ToolArguments.GetBool(call, DungeonMasterTools.ForfeitWeaponParameter),
                    // The associated speech is a harness fact, not something the Dungeon Master supplies: it is
                    // whatever this character actually said aloud on this turn, on the public channel.
                    _speechThisTurn?.Id);

            case DungeonMasterTools.AcceptSurrenderName:
                return new AcceptSurrenderAction(
                    ResolveActingCharacter(call, DungeonMasterTools.RecipientParameter, character),
                    ResolveOfferReference(call, character));

            case DungeonMasterTools.UseAbilityName:
                return new UseAbilityAction(
                    ResolveActingCharacter(call, DungeonMasterTools.ActorParameter, character),
                    ToolArguments.GetRequiredString(call, DungeonMasterTools.AbilityParameter),
                    ToolArguments.GetString(call, DungeonMasterTools.TargetParameter));

            case DungeonMasterTools.DefendName:
                return new DefendAction(
                    ResolveActingCharacter(call, DungeonMasterTools.ActorParameter, character));

            case DungeonMasterTools.GiveItemName:
                return new GiveItemAction(
                    ResolveActingCharacter(call, DungeonMasterTools.ActorParameter, character),
                    ToolArguments.GetRequiredString(call, DungeonMasterTools.RecipientParameter),
                    ToolArguments.GetRequiredString(call, DungeonMasterTools.ItemParameter));

            case DungeonMasterTools.DropItemName:
                return new DropItemAction(
                    ResolveActingCharacter(call, DungeonMasterTools.ActorParameter, character),
                    ToolArguments.GetRequiredString(call, DungeonMasterTools.ItemParameter));

            case DungeonMasterTools.StealItemName:
                return new StealItemAction(
                    ResolveActingCharacter(call, DungeonMasterTools.ThiefParameter, character),
                    ToolArguments.GetRequiredString(call, DungeonMasterTools.TargetParameter),
                    ToolArguments.GetRequiredString(call, DungeonMasterTools.ItemParameter));

            default:
                throw new ArgumentException($"No engine action is mapped to tool '{call.Name}'.");
        }
    }

    /// <summary>
    /// Resolves the exit named by an <c>open_exit</c> or <c>escape_encounter</c> tool call. A weaker Dungeon
    /// Master model sometimes invents an exit name ("doorway to the north") rather than using the one in the
    /// snapshot, which would make the engine reject a genuine attempt to leave. When the room has exactly one
    /// exit the intent can only mean that exit, so an unmatched reference is corrected to it — the same
    /// discipline <see cref="ResolveActingCharacter"/> applies to the actor. The correction is traced, so the
    /// Dungeon Master's original argument is never silently discarded; with two or more exits nothing is
    /// guessed and the engine refuses an unknown or ambiguous reference in-world.
    /// </summary>
    /// <summary>
    /// Resolves the offer named by an <c>accept_surrender</c> tool call. A weaker Dungeon Master model
    /// paraphrases the offer instead of copying its id ("the goblin's offer", "offer from Skrit"), which would
    /// make the engine refuse a genuine acceptance. When the accepting character has exactly one offer awaiting
    /// their answer, the intent can only mean that offer, so an unmatched reference is corrected to it — the
    /// same discipline <see cref="ResolveExitReference"/> and <see cref="ResolveActingCharacter"/> apply. The
    /// correction is traced, so the model's original argument is never silently discarded; with two or more
    /// pending offers nothing is guessed and the engine refuses the unknown reference in-world.
    /// </summary>
    private string ResolveOfferReference(FunctionCallContent call, CharacterAgent character)
    {
        var named = ToolArguments.GetString(call, DungeonMasterTools.OfferParameter) ?? "";
        if (_engine.State.FindOffer(named) is not null)
        {
            return named;
        }

        var pending = _engine.State.PendingOffersTo(character.CharacterId).ToList();
        if (pending.Count == 1)
        {
            _trace.Emit(TraceEventType.AdjudicationCorrected, new AdjudicationCorrectionPayload
            {
                ToolName = call.Name,
                Parameter = DungeonMasterTools.OfferParameter,
                DungeonMasterValue = named,
                CorrectedValue = pending[0].Id,
                Justification =
                    $"'{named}' names no offer; {character.Name} has exactly one offer awaiting their answer, so it can only mean that one."
            }, DungeonMasterAgent.AgentIdentifier);

            return pending[0].Id;
        }

        return named;
    }

    private string ResolveExitReference(FunctionCallContent call)
    {
        var named = ToolArguments.GetRequiredString(call, DungeonMasterTools.ExitParameter);
        var exits = _engine.State.Exits;

        if (exits.Any(e => e.Matches(named)))
        {
            return named;
        }

        if (exits.Length == 1)
        {
            _trace.Emit(TraceEventType.AdjudicationCorrected, new AdjudicationCorrectionPayload
            {
                ToolName = call.Name,
                Parameter = DungeonMasterTools.ExitParameter,
                DungeonMasterValue = named,
                CorrectedValue = exits[0].Name,
                Justification = "The named exit does not match the room's single exit; corrected to it."
            }, DungeonMasterAgent.AgentIdentifier);

            return exits[0].Name;
        }

        return named;
    }

    /// <summary>
    /// The actor of an adjudicated action is always the character whose turn it is.
    /// </summary>
    /// <remarks>
    /// This is a structural fact of the turn system, not an interpretation of intent, so the harness
    /// asserts it rather than trusting the argument. It matters because a character that loses track of
    /// its own identity will describe itself by the wrong name, and the Dungeon Master translates that
    /// confusion faithfully — which previously produced actions where the attacker was also the target.
    /// Any correction is traced, so the DM's original argument is never silently discarded.
    /// </remarks>
    private string ResolveActingCharacter(FunctionCallContent call, string parameter, CharacterAgent character)
    {
        var named = ToolArguments.GetString(call, parameter);

        var namesActingCharacter = named is null
            || string.Equals(named, character.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(named, character.CharacterId, StringComparison.OrdinalIgnoreCase);

        if (!namesActingCharacter)
        {
            _trace.Emit(TraceEventType.AdjudicationCorrected, new AdjudicationCorrectionPayload
            {
                ToolName = call.Name,
                Parameter = parameter,
                DungeonMasterValue = named,
                CorrectedValue = character.Name,
                Justification = $"Only {character.Name} may act on {character.Name}'s turn."
            }, DungeonMasterAgent.AgentIdentifier);
        }

        return character.Name;
    }

    /// <summary>
    /// Records the target-resolution decision for an attack: the reference the Dungeon Master supplied,
    /// the specific living character it names (if any), and whether that character is an ally. The
    /// engine still owns the actual acceptance; this only makes the translation visible, and it never
    /// changes the target — an unresolved or invalid reference is reported, not silently swapped.
    /// </summary>
    private void EmitTargetResolution(CharacterAgent character, GameAction action, GameState engineResultStateBefore)
    {
        if (action is not AttackCharacterAction attack)
        {
            return;
        }

        var attacker = engineResultStateBefore.FindById(character.CharacterId);
        var target = engineResultStateBefore.Resolve(attack.TargetRef);

        string note;
        if (target is null)
        {
            note = $"'{attack.TargetRef}' does not name any character in the room; the world will refuse it.";
        }
        else if (!target.IsAlive)
        {
            note = $"'{attack.TargetRef}' names {target.Name}, who is already dead; the world will refuse it.";
        }
        else if (string.Equals(target.Id, character.CharacterId, StringComparison.OrdinalIgnoreCase))
        {
            note = $"'{attack.TargetRef}' names the attacker; the world will refuse a self-attack.";
        }
        else
        {
            var relationship = attacker is not null && attacker.IsAllyOf(target) ? "an ally" : "an enemy";
            note = $"'{attack.TargetRef}' resolved to {target.Name} (id {target.Id}), {relationship} of {character.Name}.";
        }

        _trace.Emit(TraceEventType.TargetResolved, new TargetResolutionPayload
        {
            AttackerId = character.CharacterId,
            AttackerName = character.Name,
            RequestedTarget = attack.TargetRef,
            ResolvedTargetId = target?.Id,
            ResolvedTargetName = target?.Name,
            Resolved = target is not null,
            TargetAlive = target?.IsAlive,
            TargetTeam = target?.Team,
            TargetIsAlly = target is not null && attacker is not null ? attacker.IsAllyOf(target) : null,
            Note = note
        }, DungeonMasterAgent.AgentIdentifier);

        // Friendly fire is deliberately permitted by the engine (see MultiActorEngineTests): prompts and
        // goals discourage it, the world does not forbid it. But it must never pass unremarked. A live run
        // opened with Rowan saying "I step in front of Elara and take whatever comes at her" — the guard
        // phrasing his own state block suggests — and the Dungeon Master bound it as a strike on Elara; only
        // a missed roll saved her, and nothing in the console or the report said a word about it. The
        // relationship was already computed and thrown away into the trace.
        if (target is not null && attacker is not null && attacker.IsAllyOf(target))
        {
            _console.Notice(
                $"{character.Name} was ruled to be attacking {target.Name}, who fights on the same side. " +
                "The world permits this; check the intent it came from.");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Trace helpers
    // -----------------------------------------------------------------------------------------

    private void EmitAdjudication(
        CharacterAgent character,
        string intent,
        ActionResolutionCategory category,
        string? reason,
        GameAction? action,
        string? dungeonMasterText) =>
        _trace.Emit(TraceEventType.DmAdjudication, new DmAdjudicationPayload
        {
            CharacterId = character.CharacterId,
            CharacterName = character.Name,
            Intent = intent,
            Category = category.ToString(),
            Reason = reason,
            TranslatedAction = action,
            DungeonMasterText = dungeonMasterText,
            ActingCharacterKnowledge = _actingKnowledgeView,
            ConsultationId = _currentConsultationId
        }, DungeonMasterAgent.AgentIdentifier);

    private void DispatchAndRecord(CharacterAgent character, FunctionCallContent call, string result, string decision)
    {
        _trace.Emit(TraceEventType.ToolCallDispatched, new ToolCallDispatchPayload
        {
            AgentName = character.Name,
            CallId = call.CallId,
            ToolName = call.Name,
            Arguments = ChatTraceMapper.MapArguments(call.Arguments),
            DispatchDecision = decision
        });

        RecordToolResult(character, call, result);
    }

    private void RecordToolResult(CharacterAgent character, FunctionCallContent call, object? result)
    {
        character.AppendToolResult(call, result);

        _trace.Emit(TraceEventType.ToolCallResult, new ToolCallResultPayload
        {
            AgentName = character.Name,
            CallId = call.CallId,
            ToolName = call.Name,
            Result = result
        });
    }

    /// <summary>
    /// The fallback for a reply that carried no tool call, used when the intent parser is off or the reply
    /// was truncated: record a prose speech attempt and nudge for say, recover a tool call written as prose
    /// when that is enabled, or nudge for a clean call (distinguishing a truncated reply). Returns the call
    /// to dispatch, or null when the character was nudged and the turn loop should ask it again.
    /// </summary>
    private FunctionCallContent? TryRecoverOrNudge(
        CharacterAgent character, ChatResponse response, bool truncated, bool contextExhausted)
    {
        // A character may write a spoken line as prose ("I shout: \"Vark! decide now…\"") instead of calling
        // say. We never put those inferred words in its mouth and broadcast them; instead we record the
        // attempt — so a report does not read it as silence — and nudge it to call say properly.
        // Never read a cut-off reply as speech. A live run ended a character's reply mid-sentence at
        // "barely standing against the wall with blood welling from the deep wound in his torso." and the
        // extractor took that fragment for an attempt to speak, so the console announced a prose speech
        // nudge on a turn where nobody had tried to say anything. A fragment is not evidence of intent, and
        // the same rule already keeps the intent parser away from truncated replies.
        var spokenAttempt = truncated ? null : ModelText.TryExtractSpokenAttempt(ModelText.Clean(response));
        if (spokenAttempt is not null)
        {
            _trace.Emit(TraceEventType.UnstructuredSpeechAttempt, new UnstructuredSpeechAttemptPayload
            {
                CharacterId = character.CharacterId,
                CharacterName = character.Name,
                AttemptedText = spokenAttempt,
                WasTruncated = truncated
            }, character.Name);

            _console.Notice($"{character.Name} tried to speak in prose; nudged to call say().");
            character.AppendNudge(_prompts.Render("character.speech-retry"));
            return null;
        }

        // A model may understand the protocol but write the call as prose. When recovery is enabled, salvage
        // that call rather than nudging. Truncated replies are never recovered — they may be incomplete.
        var recovered = !truncated && _limits.RecoverTextToolCalls
            ? TryRecoverCharacterToolCall(character, response)
            : null;

        if (recovered is null)
        {
            _trace.Emit(TraceEventType.ToolCallError, new ToolCallErrorPayload
            {
                AgentName = character.Name,
                ToolName = "(none)",
                Error = truncated
                    ? contextExhausted
                        ? $"Character's reply was cut off because the context window was already full " +
                          $"(input {response.Usage?.InputTokenCount} + output {response.Usage?.OutputTokenCount} " +
                          $"filled a {character.Profile.ContextWindow}-token window). The request is too large, " +
                          "not the output budget too small."
                        : "Character's reply was truncated at the output-token limit before any tool call was produced."
                    // Naming the finish reason covers the other ways a reply can end early, such as a
                    // provider content filter, without needing a case for each.
                    : $"Character responded without calling ask_dm, take_action or end_turn " +
                      $"(finish reason: {response.FinishReason?.Value ?? "none reported"})."
            });

            if (truncated)
            {
                // The two causes call for opposite remedies, so they must never share a message. Raising
                // MaxOutputTokens against a full window makes it strictly worse: the window is shared, so a
                // larger output reservation leaves less room for the prompt that was already too big.
                _console.Notice(contextExhausted
                    ? $"{character.Name}'s context window was full before it could answer. " +
                      "Shrink the request (prompt, state block or history) — do NOT raise MaxOutputTokens."
                    : $"{character.Name}'s reply hit the output-token limit. " +
                      "Consider raising MaxOutputTokens for that agent.");
            }

            character.AppendNudge(_prompts.Render(truncated ? "character.truncated" : "character.nudge"));
            return null;
        }

        return recovered;
    }

    /// <summary>
    /// Reads a prose reply through the intent parser into the say/ask/take_action calls it implies, orders
    /// speech and questions before the turn-ending action, and swaps the character's prose reply for those
    /// calls so the history carries structure rather than the discarded prose. When the parser finds nothing
    /// callable, the whole reply is treated as a single take_action so the turn still progresses. Always
    /// returns at least one call.
    /// </summary>
    private async Task<IReadOnlyList<FunctionCallContent>> ParseProseIntoCallsAsync(
        CharacterAgent character, ChatResponse response, CancellationToken cancellationToken)
    {
        var prose = ModelText.Clean(response);
        var parsed = await _intentParser!.ParseAsync(prose, cancellationToken).ConfigureAwait(false);

        // Speak and ask before the action that ends the turn, whatever order the parser emitted them in.
        var ordered = parsed
            .OrderBy(call => call.Name is CharacterTools.TakeActionName or CharacterTools.EndTurnName ? 1 : 0)
            .ToList();

        if (ordered.Count == 0)
        {
            // Nothing callable was found — treat the words as a single action so the turn resolves rather
            // than looping. The reply was non-empty prose, so there is something to attempt.
            ordered =
            [
                new FunctionCallContent(
                    Guid.NewGuid().ToString("N"),
                    CharacterTools.TakeActionName,
                    new Dictionary<string, object?> { [CharacterTools.IntentParameter] = prose })
            ];
        }

        _trace.Emit(TraceEventType.IntentParsed, new IntentParsedPayload
        {
            CharacterId = character.CharacterId,
            CharacterName = character.Name,
            Prose = prose,
            ExtractedCalls = [.. ordered.Select(call => call.Name)]
        });

        // Swap the prose reply for the structured calls, so the tool results that follow have matching calls
        // in the history and the resolved turn carries no discarded prose. The original prose stays in the trace.
        character.ReplaceLastReplyWithToolCalls(ordered);
        return ordered;
    }

    /// <summary>
    /// Makes room in a character's context after its request filled the window, before it is asked again.
    /// Sheds this turn's failed prose replies and nudges, then folds older turns into the running summary.
    /// </summary>
    /// <remarks>
    /// Without this, a full window is self-perpetuating: the harness answers a truncated reply by appending a
    /// nudge, which enlarges the very request that had no room, and the next attempt fails identically. A live
    /// qwen run burned three consecutive retries on one turn exactly that way. Reclaiming is the only response
    /// to a full window that can actually change the outcome.
    /// </remarks>
    private async Task ReclaimContextRoomAsync(
        CharacterAgent character, int historyMark, CancellationToken cancellationToken)
    {
        var before = character.EstimateHistoryTokens();

        character.CompactTurnHistory(historyMark);
        if (_summariser is not null && _limits.SummariseHistory)
        {
            await MaybeSummariseHistoryAsync(character, cancellationToken).ConfigureAwait(false);
        }

        var after = character.EstimateHistoryTokens();
        _trace.Emit(TraceEventType.ContextRoomReclaimed, new ContextRoomReclaimedPayload
        {
            CharacterId = character.CharacterId,
            CharacterName = character.Name,
            EstimatedTokensBefore = before,
            EstimatedTokensAfter = after,
            Round = _trace.Round,
            Turn = _trace.Turn
        }, character.Name);

        if (after >= before)
        {
            // Nothing left to shed: the request is too big even when the history is empty, which is a prompt
            // or state-block problem the run cannot fix for itself. Say so rather than looping in silence.
            _console.Notice(
                $"{character.Name}'s request still fills the context window with nothing left to trim " +
                $"(~{after} tokens). The system prompt or state block is too large for this window.");
        }
    }

    /// <summary>
    /// Folds a character's older turns into a running summary once its estimated history exceeds the budget,
    /// keeping the most recent turns verbatim. A no-op when the history is small enough or there is nothing
    /// older to fold; an empty recap leaves the history untouched rather than dropping it.
    /// </summary>
    private async Task MaybeSummariseHistoryAsync(CharacterAgent character, CancellationToken cancellationToken)
    {
        // The budget is derived from the character's own context window, output budget and measured prompt
        // overhead — not the configured number alone — so summarisation keeps the FULL request (messages plus
        // the tool schemas and chat-template scaffolding the estimate cannot see, plus room for the reply)
        // inside the window. Budgeting against the message-only estimate alone let the real prompt sit right
        // under the window and truncate replies.
        var budget = character.EffectiveHistoryBudget(_limits.HistoryTokenBudget);

        var before = character.EstimateHistoryTokens();
        if (before <= budget)
        {
            return;
        }

        if (!character.TryPlanHistorySummary(_limits.RecentTurnsKeptFull, out var boundary, out var olderHistory))
        {
            return;
        }

        var recap = await _summariser!.SummariseAsync(olderHistory, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(recap))
        {
            // A blank recap would lose the older history for nothing — leave it in place and try again later.
            return;
        }

        character.ApplyHistorySummary(boundary, $"Earlier in this fight, what you remember: {recap}");

        var after = character.EstimateHistoryTokens();
        _trace.Emit(TraceEventType.HistorySummarised, new HistorySummarisedPayload
        {
            CharacterId = character.CharacterId,
            CharacterName = character.Name,
            EstimatedTokensBefore = before,
            EstimatedTokensAfter = after,
            EffectiveBudget = budget,
            MeasuredPromptOverhead = character.ObservedPromptOverheadTokens,
            Summary = recap
        });

        _console.Notice($"{character.Name}'s older memories were summarised (~{before} → ~{after} tokens, budget {budget}).");
    }

    private void RecordDungeonMasterToolResult(FunctionCallContent call, object? result)
    {
        _dungeonMaster.AppendAdjudicationToolResult(call, result);

        _trace.Emit(TraceEventType.ToolCallResult, new ToolCallResultPayload
        {
            AgentName = DungeonMasterAgent.AgentIdentifier,
            CallId = call.CallId,
            ToolName = call.Name,
            Result = result
        }, DungeonMasterAgent.AgentIdentifier);
    }

    /// <summary>
    /// Parses a tool call a character wrote as prose and, if one is found, rewrites the character's last
    /// reply to carry the structured call so the tool result that follows has a matching call in the
    /// history. Returns the synthesised call, or null when nothing recoverable was written.
    /// </summary>
    private FunctionCallContent? TryRecoverCharacterToolCall(CharacterAgent character, ChatResponse response)
    {
        var text = ModelText.Clean(response);
        if (ModelText.TryRecoverToolCall(text, CharacterTools.Names) is not { } parsed)
        {
            return null;
        }

        var callId = $"recovered-{Guid.NewGuid():N}";

        // The argument is unnamed; ToolArguments reads a lone argument regardless of its key, so the
        // dispatch handlers pick it up under whatever parameter each tool expects.
        var call = new FunctionCallContent(callId, parsed.Name,
            new Dictionary<string, object?> { ["argument"] = parsed.Argument });

        character.ReplaceLastReplyWithToolCall(call);

        _trace.Emit(TraceEventType.ToolCallRecovered, new ToolCallRecoveredPayload
        {
            AgentName = character.Name,
            CallId = callId,
            ToolName = parsed.Name,
            RecoveredArgument = parsed.Argument,
            OriginalText = text
        });

        _console.Notice($"{character.Name} wrote its move as text; recovered {parsed.Name}().");
        return call;
    }

    /// <summary>
    /// Everyone still present in the room — active or surrendered — who is therefore an intended recipient
    /// of public narration and public facts. Recorded on the narration event so the audience is explicit,
    /// even though each character actually receives it at a different moment. An escaped character has left
    /// the room and receives nothing further; a surrendered character is still present and keeps learning
    /// public events even though it takes no more turns. (Before v0.5 every living character was present, so
    /// this preserves the old audience exactly.)
    /// </summary>
    private IReadOnlyList<string> LivingRecipients() =>
        [.. _engine.State.Characters.Where(c => c.IsPresent).Select(c => c.Id)];

    /// <summary>Every present character except the given one — the intended audience for that character's speech.</summary>
    private IReadOnlyList<string> LivingRecipientsExcept(string speakerId) =>
    [
        .. _engine.State.Characters
            .Where(c => c.IsPresent && !string.Equals(c.Id, speakerId, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Id)
    ];

    /// <summary>
    /// Records the object-specific detail of an open/take attempt: the container and item ids, the world
    /// version transition, and compact before/after snapshots so a successful transfer shows both the
    /// container losing the item and the actor gaining it. Emitted for accepted and rejected attempts
    /// alike; a no-op for non-object actions.
    /// </summary>
    private void EmitObjectInteraction(CharacterAgent character, GameAction action, EngineResult result)
    {
        if (action is not (OpenContainerAction or TakeItemAction or InspectObjectAction))
        {
            return;
        }

        var before = result.StateBefore;
        var after = result.StateAfter;

        var containerRef = action switch
        {
            OpenContainerAction open => open.ContainerRef,
            TakeItemAction take => take.ContainerRef,
            InspectObjectAction inspect => inspect.ObjectRef,
            _ => null
        };
        var itemRef = action is TakeItemAction t ? t.ItemRef : null;

        var containerBefore = containerRef is null ? null : before.ResolveObject(containerRef).Object as Container;
        var containerId = (result.Outcome as OpenContainerOutcome)?.ContainerId
            ?? (result.Outcome as TakeItemOutcome)?.ContainerId
            ?? containerBefore?.Id;
        var containerAfter = containerId is null
            ? null
            : after.Objects.OfType<Container>()
                .FirstOrDefault(c => string.Equals(c.Id, containerId, StringComparison.OrdinalIgnoreCase));

        var itemId = (result.Outcome as TakeItemOutcome)?.ItemId
            ?? (containerBefore is not null && itemRef is not null ? containerBefore.FindItem(itemRef)?.Id : null);

        var actorBefore = before.FindById(character.CharacterId);
        var actorAfter = after.FindById(character.CharacterId);

        _trace.Emit(TraceEventType.ObjectInteraction, new ObjectInteractionPayload
        {
            ActorId = character.CharacterId,
            ActionType = action.ActionType,
            ObjectId = containerId,
            ContainerId = containerId,
            ItemId = itemId,
            ValidationResult = result.Accepted ? "accepted" : "rejected",
            RejectionReason = result.RejectionReason?.ToString(),
            WorldVersionBefore = before.Version,
            WorldVersionAfter = after.Version,
            ContainerOpenBefore = containerBefore?.IsOpen,
            ContainerOpenAfter = containerAfter?.IsOpen,
            ContainerContentsBefore = containerBefore is null ? null : [.. containerBefore.Contents.Select(i => i.Name)],
            ContainerContentsAfter = containerAfter is null ? null : [.. containerAfter.Contents.Select(i => i.Name)],
            ActorInventoryBefore = actorBefore is null ? null : [.. actorBefore.Inventory.Select(i => i.Name)],
            ActorInventoryAfter = actorAfter is null ? null : [.. actorAfter.Inventory.Select(i => i.Name)]
        }, character.Name);
    }

    /// <summary>
    /// Records the exit-specific detail of an open-exit or escape attempt: the exit id, the open/closed
    /// transition and the world-version change. Emitted for accepted and rejected attempts alike; a no-op
    /// for non-exit actions.
    /// </summary>
    private void EmitExitInteraction(CharacterAgent character, GameAction action, EngineResult result)
    {
        if (action is not (OpenExitAction or EscapeEncounterAction))
        {
            return;
        }

        var before = result.StateBefore;
        var after = result.StateAfter;

        var exitRef = action switch
        {
            OpenExitAction open => open.ExitRef,
            EscapeEncounterAction escape => escape.ExitRef,
            _ => null
        };

        var exitBefore = exitRef is null ? null : before.ResolveExit(exitRef).Exit;
        var exitId = (result.Outcome as OpenExitOutcome)?.ExitId
            ?? (result.Outcome as EscapeOutcome)?.ExitId
            ?? exitBefore?.Id;
        var exitAfter = exitId is null
            ? null
            : after.Exits.FirstOrDefault(e => string.Equals(e.Id, exitId, StringComparison.OrdinalIgnoreCase));

        _trace.Emit(TraceEventType.ExitInteraction, new ExitInteractionPayload
        {
            ActorId = character.CharacterId,
            ExitId = exitId,
            ActionType = action.ActionType,
            StateBefore = exitBefore is null ? null : (exitBefore.IsOpen ? "open" : "closed"),
            StateAfter = exitAfter is null ? null : (exitAfter.IsOpen ? "open" : "closed"),
            ValidationResult = result.Accepted ? "accepted" : "rejected",
            RejectionReason = result.RejectionReason?.ToString(),
            WorldVersionBefore = before.Version,
            WorldVersionAfter = after.Version
        }, character.Name);
    }

    /// <summary>
    /// Whether an attempted theft lacks a legitimate informational basis: the thief holds no knowledge that
    /// the named item exists at all. Resolved from the target's current inventory and the thief's ledger,
    /// without revealing anything — a thief with no basis is refused whether or not the target actually
    /// carries the item. Returns false (basis present, let it proceed to the engine) when the item does not
    /// resolve against the target, so the engine gives the ordinary "not carrying it" refusal instead.
    /// </summary>
    private bool StealLacksKnowledgeBasis(CharacterAgent character, StealItemAction steal, out string reason)
    {
        reason = "You have no way of knowing they are carrying such a thing.";

        var target = _engine.State.Resolve(steal.TargetRef);
        var item = target?.FindItem(steal.ItemRef);
        if (item is null)
        {
            // The item does not resolve against the target; let the engine handle it (unknown target/item),
            // rather than pre-judging a basis for something that may not be there.
            return false;
        }

        return !_knowledge.KnowsItem(character.CharacterId, item.Id);
    }

    /// <summary>
    /// Whether taking a specific item from a container lacks a legitimate informational basis: the character
    /// has no way of knowing that item is inside it. Resolved from the container's current contents and the
    /// character's own knowledge, without revealing anything — a character with no basis is refused whether or
    /// not the item is really there. The floor (ground loot dropped in plain sight) is always fair game. Basis
    /// counts when the character has directly observed the container's contents including this item, knows the
    /// item as a thing in the open (seen carried, taken, given or dropped), or has been told of it (hearsay).
    /// Returns false (let the engine handle it) when the container or item does not resolve.
    /// </summary>
    private bool TakeLacksKnowledgeBasis(CharacterAgent character, TakeItemAction take, out string reason)
    {
        // Phrased as a plain physical fact (you cannot see well enough to grab a particular thing), not as
        // knowledge bookkeeping, so it reads in-world and does not trip the machinery-leak rephrase.
        reason = "You cannot make out anything in there clearly enough to lay a hand on it.";

        if (_engine.State.ResolveObject(take.ContainerRef).Object is not Container container)
        {
            return false;
        }

        // The floor holds only things dropped in plain sight of everyone; anyone present may pick them up.
        // A fallen character's body is the same case: everybody watched them go down, and a body holds only
        // what they were already carrying openly, so nothing hidden is revealed by reaching into it.
        if (container.IsGround || container.IsCorpse)
        {
            return false;
        }

        var item = container.FindItem(take.ItemRef);
        if (item is null)
        {
            // The item does not resolve inside the container; let the engine give the ordinary refusal.
            return false;
        }

        if (_knowledge.KnowsItemInContainer(character.CharacterId, container.Id, item.Id)
            || _knowledge.KnowsItem(character.CharacterId, item.Id)
            || HeardItemMentioned(character.CharacterId, item.Name))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the character has heard another character say the item's name — a hearsay basis to reach for it.
    /// A deliberately simple name match over what this character has heard on the public channel; it is a
    /// permissive basis (better to allow a plausibly-heard take than to over-refuse), the strict floor being
    /// the direct-knowledge and open-item checks alongside it.
    /// </summary>
    private bool HeardItemMentioned(string characterId, string itemName) =>
        !string.IsNullOrWhiteSpace(itemName)
        && _narrationLog.SpeechHeardBy(characterId)
            .Any(entry => entry.Text.Contains(itemName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Records the inventory-specific detail of a give/drop/steal attempt: the requested and resolved item
    /// bindings, ownership before and after, turn consumption, and any RNG/rulebook linkage. Emitted for
    /// accepted and rejected attempts alike; a no-op for non-inventory actions. When <paramref name="engineResult"/>
    /// is null the attempt never reached the engine (a knowledge-basis refusal), carrying the given reason.
    /// </summary>
    private void EmitInventoryInteraction(CharacterAgent character, GameAction action, EngineResult? engineResult, string? rejectionReason)
    {
        if (action is not (GiveItemAction or DropItemAction or StealItemAction))
        {
            return;
        }

        var before = engineResult?.StateBefore ?? _engine.State;
        var after = engineResult?.StateAfter ?? _engine.State;
        var accepted = engineResult?.Accepted ?? false;

        var (counterpartyRef, itemRef) = action switch
        {
            GiveItemAction g => (g.RecipientRef, g.ItemRef),
            StealItemAction s => (s.TargetRef, s.ItemRef),
            DropItemAction d => ((string?)null, d.ItemRef),
            _ => (null, "")
        };

        var counterparty = counterpartyRef is null ? null : before.Resolve(counterpartyRef);
        var resolvedItem = ResolveTransferItem(before, action, counterparty);
        var theftSucceeded = (engineResult?.Outcome as StealItemOutcome)?.Succeeded;

        var ownerBefore = action switch
        {
            GiveItemAction => character.CharacterId,
            DropItemAction => character.CharacterId,
            StealItemAction => counterparty?.Id,
            _ => null
        };

        var ownerAfter = accepted
            ? action switch
            {
                GiveItemAction => counterparty?.Id,
                DropItemAction => Container.GroundId,
                StealItemAction => theftSucceeded == true ? character.CharacterId : counterparty?.Id,
                _ => ownerBefore
            }
            : ownerBefore;

        _trace.Emit(TraceEventType.InventoryInteraction, new InventoryInteractionPayload
        {
            ActionType = action.ActionType,
            ActorId = character.CharacterId,
            ActorName = character.Name,
            CounterpartyId = counterparty?.Id,
            CounterpartyName = counterparty?.Name,
            RequestedItemRef = itemRef,
            ResolvedItemId = resolvedItem?.Id,
            ResolvedItemName = resolvedItem?.Name,
            ValidationResult = accepted ? "accepted" : "rejected",
            RejectionReason = engineResult?.RejectionReason?.ToString() ?? rejectionReason,
            OwnerBefore = ownerBefore,
            OwnerAfter = ownerAfter,
            WorldVersionBefore = before.Version,
            WorldVersionAfter = after.Version,
            TurnConsumed = accepted,
            // Whether a draw actually happened — true only when the engine reached the theft roll, not merely
            // because the action was a steal. A steal the engine rejects on validation rolls nothing.
            RngConsulted = engineResult is { } r && r.RngDraws.Count > 0,
            TheftSucceeded = theftSucceeded,
            ConsultationId = _currentConsultationId,
            VisibilityRecipients = accepted ? LivingRecipients() : []
        }, character.Name);
    }

    /// <summary>Resolves the item a transfer action names, from wherever it currently sits, for the interaction record.</summary>
    private static InventoryItem? ResolveTransferItem(GameState state, GameAction action, Character? counterparty) => action switch
    {
        GiveItemAction g => state.Resolve(g.GiverRef)?.FindItem(g.ItemRef),
        DropItemAction d => state.Resolve(d.ActorRef)?.FindItem(d.ItemRef),
        StealItemAction s => counterparty?.FindItem(s.ItemRef),
        _ => null
    };

    /// <summary>
    /// Records an item-provenance event for a successful movement — give, drop, steal (success) or take —
    /// capturing the item, its previous and new owner/location, the acting character, any counterparty, the
    /// round and turn, and the RNG/rulebook linkage. A no-op for a rejected action, a failed theft (nothing
    /// moved), or a non-movement action, so the provenance history holds exactly the transfers that happened.
    /// </summary>
    private void EmitItemProvenance(CharacterAgent character, GameAction action, EngineResult engineResult)
    {
        if (!engineResult.Accepted)
        {
            return;
        }

        switch (engineResult.Outcome)
        {
            case GiveItemOutcome give:
                EmitProvenance(give.ItemId, give.ItemName, give.GiverId, give.RecipientId, "give_item", character,
                    give.RecipientId, give.RecipientName, engineResult, rngInvolved: false, "gave the item away");
                break;
            case DropItemOutcome drop:
                EmitProvenance(drop.ItemId, drop.ItemName, drop.ActorId, Container.GroundId, "drop_item", character,
                    null, null, engineResult, rngInvolved: false, "dropped the item on the floor");
                break;
            case StealItemOutcome { Succeeded: true } steal:
                EmitProvenance(steal.ItemId, steal.ItemName, steal.TargetId, steal.ThiefId, "steal_item", character,
                    steal.TargetId, steal.TargetName, engineResult, rngInvolved: true, "stole the item");
                break;
            case TakeItemOutcome take:
                EmitProvenance(take.ItemId, take.ItemName, take.ContainerId, take.ActorId, "take_item", character,
                    null, null, engineResult, rngInvolved: false, "took the item from a container");
                break;
        }
    }

    private void EmitProvenance(
        string itemId, string itemName, string previousOwnerOrLocation, string newOwnerOrLocation, string actionType,
        CharacterAgent actor, string? counterpartyId, string? counterpartyName, EngineResult engineResult,
        bool rngInvolved, string reason) =>
        _trace.Emit(TraceEventType.ItemProvenance, new ItemProvenancePayload
        {
            ItemId = itemId,
            ItemName = itemName,
            PreviousOwnerOrLocation = previousOwnerOrLocation,
            NewOwnerOrLocation = newOwnerOrLocation,
            ActionType = actionType,
            ActingCharacterId = actor.CharacterId,
            ActingCharacterName = actor.Name,
            CounterpartyId = counterpartyId,
            CounterpartyName = counterpartyName,
            Round = _trace.Round,
            Turn = _trace.Turn,
            WorldVersionBefore = engineResult.StateBefore.Version,
            WorldVersionAfter = engineResult.StateAfter.Version,
            RngInvolved = rngInvolved,
            RngTracePurpose = rngInvolved ? "steal.attempt" : null,
            ConsultationId = _currentConsultationId,
            Reason = reason
        }, actor.Name);

    // -----------------------------------------------------------------------------------------
    // Turn upkeep, status effects, surrender offers and abilities (v0.7)
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Traces the engine's start- or end-of-turn upkeep, and tells the room about any surrender offer that
    /// lapsed. An offer everybody heard cannot simply vanish: the offerer in particular has to learn that their
    /// terms came to nothing, or they will keep waiting on an answer that is no longer possible.
    /// </summary>
    private void ApplyUpkeep(TurnUpkeep upkeep, string phase, string actorName)
    {
        if (upkeep.IsEmpty)
        {
            return;
        }

        EmitStatusEvents(upkeep.StatusEvents, $"turn-upkeep ({phase})");
        EmitOfferTransitions(upkeep.OfferTransitions);

        foreach (var transition in upkeep.OfferTransitions)
        {
            var offerer = _engine.State.FindById(transition.Offer.OffererId)?.Name ?? transition.Offer.OffererId;
            var recipient = _engine.State.FindById(transition.Offer.RecipientId)?.Name ?? transition.Offer.RecipientId;
            var text = $"{recipient} let {offerer}'s offer of terms pass without taking it up. Nothing changed " +
                       $"hands, and {offerer} is still in the fight.";

            // Delivered verbatim on the public channel, with no Dungeon Master call: this is bookkeeping
            // everyone can see, not a scene to be narrated, and a model call here would only risk embellishing it.
            var entry = _narrationLog.Record("surrender-offer-lapsed", text);
            _trace.Emit(TraceEventType.Narration, new NarrationPayload
            {
                Purpose = "surrender-offer-lapsed",
                StateSuppliedToDungeonMaster = "(none — delivered verbatim by the harness, no model call)",
                ContextSuppliedToDungeonMaster = transition.Cause,
                Narration = text,
                NarrationId = entry.Id,
                IntendedRecipients = LivingRecipients(),
                Visibility = "public",
                WorldVersion = _engine.State.Version
            });

            _console.Notice(text);
        }

        // Anything that fell away at the start of a turn is worth a line in the transcript, so a guard that
        // simply timed out does not look like a bug to somebody watching.
        foreach (var status in upkeep.StatusEvents.Where(e => e.Kind == StatusEventKind.Expired))
        {
            var target = _engine.State.FindById(status.Status.TargetCharacterId)?.Name ?? status.Status.TargetCharacterId;
            _console.Notice($"{target} is no longer {status.Status.ShortLabel} ({status.Cause}).");
        }
    }

    /// <summary>
    /// Traces one status-effect transition per event, carrying every field the status carries. This is the
    /// status timeline: nothing about a status is knowable only from its effect on a number.
    /// </summary>
    private void EmitStatusEvents(IReadOnlyList<StatusEvent> events, string relatedAction)
    {
        foreach (var statusEvent in events)
        {
            var status = statusEvent.Status;
            var eventType = statusEvent.Kind switch
            {
                StatusEventKind.Applied => TraceEventType.StatusApplied,
                StatusEventKind.Consumed => TraceEventType.StatusConsumed,
                StatusEventKind.Expired => TraceEventType.StatusExpired,
                _ => TraceEventType.StatusRemoved
            };

            _trace.Emit(eventType, new StatusEffectPayload
            {
                StatusId = status.Id,
                Kind = status.Kind.ToString(),
                Transition = statusEvent.Kind.ToString(),
                Cause = statusEvent.Cause,
                SourceCharacterId = status.SourceCharacterId,
                SourceCharacterName = _engine.State.FindById(status.SourceCharacterId)?.Name,
                TargetCharacterId = status.TargetCharacterId,
                TargetCharacterName = _engine.State.FindById(status.TargetCharacterId)?.Name,
                AppliedRound = status.AppliedRound,
                AppliedTurn = status.AppliedTurn,
                Modifier = status.Modifier,
                ExpiryRule = status.ExpiryRule.ToString(),
                Visibility = status.Visibility.ToString(),
                RelationshipId = status.RelationshipId,
                SourceAbilityId = status.SourceAbilityId,
                Round = _trace.Round,
                Turn = _trace.Turn,
                WorldVersion = _engine.State.Version,
                RelatedAction = relatedAction,
                AffectedRngPurpose = status.Kind is StatusEffectKind.Rallied or StatusEffectKind.OffBalance
                                     && statusEvent.Kind == StatusEventKind.Consumed
                    ? "attack.hit-check"
                    : null
            });
        }
    }

    /// <summary>
    /// Traces each surrender-offer state transition, and — for one that stops being open — records the public
    /// fact that it came to nothing, so nobody is left believing terms are still on the table.
    /// </summary>
    private void EmitOfferTransitions(IReadOnlyList<OfferTransition> transitions)
    {
        foreach (var transition in transitions)
        {
            var offer = transition.Offer;

            // A freshly created offer is reported by its own event, not as a transition out of Pending.
            if (transition.New == SurrenderOfferState.Pending)
            {
                continue;
            }

            var offerer = _engine.State.FindById(offer.OffererId);
            var recipient = _engine.State.FindById(offer.RecipientId);

            _trace.Emit(TraceEventType.SurrenderOfferResolved, new SurrenderOfferResolvedPayload
            {
                OfferId = offer.Id,
                OffererId = offer.OffererId,
                OffererName = offerer?.Name ?? offer.OffererId,
                RecipientId = offer.RecipientId,
                RecipientName = recipient?.Name ?? offer.RecipientId,
                PreviousState = transition.Previous.ToString(),
                NewState = transition.New.ToString(),
                Cause = transition.Cause,
                CreatedRound = offer.CreatedRound,
                CreatedTurn = offer.CreatedTurn,
                ResolvedRound = offer.ResolvedRound ?? _trace.Round,
                ResolvedTurn = offer.ResolvedTurn ?? _trace.Turn,
                TurnsToRespond = Math.Max(0, (offer.ResolvedTurn ?? _trace.Turn) - offer.CreatedTurn),
                AssetsTransferred = transition.New == SurrenderOfferState.Accepted
            });

            if (transition.New == SurrenderOfferState.Accepted)
            {
                // Acceptance has its own, richer public delivery; nothing more is needed here.
                continue;
            }

            var worldVersion = _engine.State.Version;
            var fact = _knowledge.GetOrAddSurrenderOfferSettledFact(
                offer.Id, offerer?.Name ?? offer.OffererId, recipient?.Name ?? offer.RecipientId,
                transition.New.ToString(), transition.Cause, worldVersion);
            TraceFactCreatedIfNew(fact, KnowledgeSource.PublicEvent, "surrender_offer_settled", "harness");
            DeliverPublicFact(fact.Fact, LivingRecipients(), worldVersion, "surrender_offer_settled");
        }
    }

    /// <summary>
    /// Records one ability attempt, accepted or refused, with the charges either side of it. A refusal is as
    /// important as a success here: the record has to show a refused use spending nothing, rather than the code
    /// merely claiming it does. A no-op for actions that are not ability uses.
    /// </summary>
    private void EmitAbilityUsed(
        CharacterAgent character, GameAction action, EngineResult engineResult, int? chargesBefore)
    {
        var (abilityRef, targetRef) = action switch
        {
            UseAbilityAction use => (use.AbilityRef, use.TargetRef),
            DefendAction => (AbilityCatalog.DefendId, null),
            _ => (null, null)
        };

        if (abilityRef is null)
        {
            return;
        }

        var definition = AbilityCatalog.Resolve(abilityRef);
        var actorAfter = engineResult.StateAfter.FindById(character.CharacterId);
        var chargesAfter = definition is null ? null : actorAfter?.FindAbility(definition.Id)?.RemainingUses;

        var target = engineResult.Outcome switch
        {
            GuardAllyOutcome guard => (guard.AllyId, guard.AllyName),
            HealingPrayerOutcome heal => (heal.TargetId, heal.TargetName),
            RallyOutcome rally => (rally.AllyId, rally.AllyName),
            DefendOutcome defend => (defend.ActorId, defend.ActorName),
            AttackOutcome attack => (attack.TargetId, attack.TargetName),
            _ => (null, null)
        };

        var resolvedTargetId = target.Item1;
        var resolvedTargetName = target.Item2;
        if (resolvedTargetId is null && targetRef is not null)
        {
            var named = engineResult.StateBefore.Resolve(targetRef);
            resolvedTargetId = named?.Id ?? targetRef;
            resolvedTargetName = named?.Name;
        }

        _trace.Emit(TraceEventType.AbilityUsed, new AbilityUsedPayload
        {
            ActorId = character.CharacterId,
            ActorName = character.Name,
            AbilityId = definition?.Id ?? abilityRef,
            AbilityName = definition?.Name ?? abilityRef,
            Category = definition?.Category.ToString() ?? "unknown",
            RequestedAbilityRef = abilityRef,
            TargetId = resolvedTargetId,
            TargetName = resolvedTargetName,
            ValidationResult = engineResult.Accepted ? "accepted" : "rejected",
            RejectionReason = engineResult.RejectionReason?.ToString(),
            RemainingUsesBefore = chargesBefore,
            RemainingUsesAfter = chargesAfter,
            HealingPerformed = engineResult.Outcome is HealingPrayerOutcome healed
                ? healed.HealthAfter - healed.HealthBefore
                : null,
            StatusesApplied = [.. engineResult.StatusEvents
                .Where(e => e.Kind == StatusEventKind.Applied)
                .Select(e => e.Status.Kind.ToString())],
            RngConsulted = engineResult.RngDraws.Count > 0,
            WorldVersionBefore = engineResult.StateBefore.Version,
            WorldVersionAfter = engineResult.StateAfter.Version,
            TurnConsumed = engineResult.Accepted,
            ConsultationId = _currentConsultationId
        }, character.Name);
    }

    /// <summary>
    /// Records a blow a guard relationship moved onto the guardian, naming both the intended and the
    /// authoritative target and how many draws the whole attack made — the evidence that redirection added no
    /// roll of its own. A no-op when no redirection happened.
    /// </summary>
    private void EmitAttackRedirection(EngineResult engineResult)
    {
        if (engineResult.Outcome is not AttackOutcome { Redirected: true } attack)
        {
            return;
        }

        _trace.Emit(TraceEventType.AttackRedirected, new AttackRedirectedPayload
        {
            AttackerId = attack.AttackerId,
            AttackerName = attack.AttackerName,
            IntendedTargetId = attack.IntendedTargetId ?? attack.TargetId,
            IntendedTargetName = attack.IntendedTargetName ?? attack.TargetName,
            AuthoritativeTargetId = attack.TargetId,
            AuthoritativeTargetName = attack.TargetName,
            TargetArmourUsed = attack.TargetArmour,
            TargetHealthBefore = attack.TargetHealthBefore,
            TargetHealthAfter = attack.TargetHealthAfter,
            RngDrawCount = engineResult.RngDraws.Count,
            Round = _trace.Round,
            Turn = _trace.Turn
        }, attack.AttackerName);
    }

    /// <summary>
    /// Records the item movements an accepted surrender caused: each piece of tribute, and the forfeited weapon.
    /// They get their own action types so provenance distinguishes surrender tribute and weapon forfeiture from
    /// an ordinary give or drop.
    /// </summary>
    /// <summary>
    /// Adds the state-dependent actions the stateless resolver cannot know to ask for: accepting a surrender
    /// when terms are on the table for this character, and taking from a body when the grab the resolver saw
    /// may be a looting. Anything already offered is left alone, and nothing is ever removed.
    /// </summary>
    private (IReadOnlyList<AITool>? Tools, string? Guidance) WidenCandidateToolsForState(
        CharacterAgent character, IReadOnlyList<AITool>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return (tools, null);
        }

        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rulebookRefused = names.Count == 1 && names.Contains(DungeonMasterTools.RejectActionName);

        var widened = new List<AITool>(tools);
        var notes = new List<string>();

        // Acceptance is widened in even when the rulebook refused the intent outright, and that is the one
        // place the fail-safe is deliberately opened. A GPT-driven run had a character say "I take Skrit's
        // purse and tell him he can live" — with Skrit's offer standing — and the resolver, reading only the
        // extra demand riding along with it, answered supported:false. The refusal that followed told him to
        // wait for a yield that had already happened. An offer either exists in the state or it does not, so
        // adding this tool cannot invent anything: the DM still has to choose it, and the engine still checks
        // that this character is the named recipient before a single thing moves.
        var offers = _engine.State.PendingOffersTo(character.CharacterId).ToList();
        if (offers.Count > 0 && names.Add(DungeonMasterTools.AcceptSurrenderName))
        {
            widened.Insert(0, DungeonMasterTools.AcceptSurrender);
            var terms = string.Join("; ", offers.Select(o =>
                $"{_engine.State.FindById(o.OffererId)?.Name ?? o.OffererId} ({o.Id})"));
            notes.Add(
                $"STATE THE RULEBOOK COULD NOT SEE: terms are on the table for {character.Name} right now, from {terms}. " +
                $"The rulebook is stateless and does not know this. If {character.Name} is reaching for what an offer " +
                $"promises, or sparing the offerer, that is {DungeonMasterTools.AcceptSurrenderName} on that offer — " +
                "never a theft, and never a hostile act. A demand, threat or condition tacked onto the acceptance " +
                "changes nothing: settle the terms that stand, and let the rest not happen." +
                (rulebookRefused
                    ? $" The rulebook called this intent unsupported, which it cannot judge here — it does not know an " +
                      $"offer is open. If {character.Name} is answering those terms, take them; otherwise still refuse."
                    : ""));
        }

        // The corpse widening stays inside the fail-safe: it only ever refines a grab the rulebook already
        // allowed, so an intent the rulebook refused outright is never handed a second way to act on it.
        if (!rulebookRefused
            && names.Contains(DungeonMasterTools.StealItemName)
            && _engine.State.Room.Objects.OfType<Container>().Any(c => c.IsCorpse)
            && names.Add(DungeonMasterTools.TakeItemName))
        {
            widened.Add(DungeonMasterTools.TakeItem);
            notes.Add(
                $"STATE THE RULEBOOK COULD NOT SEE: a fallen character's body lies here holding what they carried. " +
                $"Nothing can be stolen from the dead; looting a body is {DungeonMasterTools.TakeItemName} from it.");
        }

        return notes.Count == 0 ? (tools, null) : (widened, string.Join(Environment.NewLine, notes));
    }

    /// <summary>
    /// True when a grab is really an acceptance: the acting character is the named recipient of a pending
    /// offer, and the item they reached for is one that offer promises. Returns the equivalent
    /// <see cref="AcceptSurrenderAction"/> so the world resolves what the character meant.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. Only the promised items redirect — reaching for anything else the offerer carries
    /// is still an ordinary theft and still a hostile act that kills the offer. And only the named recipient
    /// redirects: a bystander grabbing at the tribute is not party to the deal. The direction of the redirect
    /// is the honest one for the fiction, too. You cannot take the tribute and keep fighting; taking what was
    /// promised IS agreeing to the terms it was promised under.
    /// </remarks>
    private bool RedirectsToAcceptance(
        CharacterAgent character, GameAction action, out GameAction? acceptance, out SurrenderOffer? offer)
    {
        acceptance = null;
        offer = null;

        var itemRef = action switch
        {
            StealItemAction steal => steal.ItemRef,
            TakeItemAction take => take.ItemRef,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(itemRef))
        {
            return false;
        }

        foreach (var pending in _engine.State.PendingOffersTo(character.CharacterId))
        {
            var offerer = _engine.State.FindById(pending.OffererId);
            if (offerer is null || !PromisesItem(offerer, pending, itemRef))
            {
                continue;
            }

            offer = pending;
            acceptance = new AcceptSurrenderAction(character.CharacterId, pending.Id);
            return true;
        }

        return false;
    }

    /// <summary>True when one of an offer's promised items is the one named, by id or by either name form.</summary>
    private static bool PromisesItem(Character offerer, SurrenderOffer offer, string itemRef)
    {
        var needle = itemRef.Trim();
        foreach (var promisedId in offer.OfferedItemIds)
        {
            if (string.Equals(promisedId, needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var promised = offerer.Inventory.FirstOrDefault(i =>
                string.Equals(i.Id, promisedId, StringComparison.OrdinalIgnoreCase));
            if (promised is not null
                && (string.Equals(promised.DisplayName, needle, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(promised.Name, needle, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a theft names a dead character. Their belongings are already in their corpse container, so
    /// the deed is a take from that body — the same act the fiction describes as looting the fallen.
    /// </summary>
    private bool RedirectsToCorpseLoot(GameAction action, out GameAction? take, out string? corpseName)
    {
        take = null;
        corpseName = null;

        if (action is not StealItemAction steal)
        {
            return false;
        }

        var target = _engine.State.Resolve(steal.TargetRef);
        if (target is null || target.IsAlive)
        {
            return false;
        }

        var corpse = _engine.State.Room.Objects.OfType<Container>()
            .FirstOrDefault(c => c.IsCorpse && c.FindItem(steal.ItemRef) is not null);
        if (corpse is null)
        {
            return false;
        }

        corpseName = target.Name;
        take = new TakeItemAction(steal.ThiefRef, corpse.Id, steal.ItemRef);
        return true;
    }

    private void EmitSurrenderProvenance(
        CharacterAgent character,
        AcceptSurrenderOutcome outcome,
        System.Collections.Immutable.ImmutableArray<string> transferredIds,
        SurrenderAgreement? agreement,
        EngineResult engineResult)
    {
        for (var index = 0; index < transferredIds.Length; index++)
        {
            var name = index < outcome.TransferredItemNames.Count ? outcome.TransferredItemNames[index] : transferredIds[index];
            EmitProvenance(transferredIds[index], name, outcome.OffererId, outcome.AccepterId,
                "surrender_tribute", character, outcome.OffererId, outcome.OffererName, engineResult,
                rngInvolved: false, "handed over as tribute under accepted terms of surrender");
        }

        if (agreement?.ForfeitedWeaponId is { } weaponId && outcome.ForfeitedWeaponName is { } weaponName)
        {
            EmitProvenance(weaponId, weaponName, outcome.OffererId, Container.GroundId,
                "weapon_forfeiture", character, outcome.OffererId, outcome.OffererName, engineResult,
                rngInvolved: false, "forfeited under accepted terms of surrender and laid on the floor");
        }
    }

    /// <summary>
    /// Records model output produced after the turn had already resolved, discarded rather than acted on.
    /// Nothing here reaches the world, the public transcript or the knowledge ledger.
    /// </summary>
    private void EmitPostResolutionDiscarded(
        CharacterAgent character, string? resolvedAction, string kind, string? toolName, string content) =>
        _trace.Emit(TraceEventType.PostResolutionOutputDiscarded, new PostResolutionOutputDiscardedPayload
        {
            CharacterId = character.CharacterId,
            CharacterName = character.Name,
            ResolvedAction = resolvedAction,
            DiscardedKind = kind,
            ToolName = toolName,
            DiscardedContent = content,
            Round = _trace.Round,
            Turn = _trace.Turn
        }, character.Name);

    /// <summary>A tool call rendered for the record: its name and its arguments, so nothing is hidden.</summary>
    private static string DescribeCall(FunctionCallContent call)
    {
        var arguments = call.Arguments is null || call.Arguments.Count == 0
            ? ""
            : string.Join(", ", call.Arguments.Select(a => $"{a.Key}={a.Value}"));
        return $"{call.Name}({arguments})";
    }

    /// <summary>The verbatim words of a public-channel speech entry, for a report. Null when there was none.</summary>
    private string? SpeechTextFor(int? narrationId) =>
        narrationId is null
            ? null
            : _narrationLog.Entries.FirstOrDefault(e => e.Id == narrationId)?.Text;

    /// <summary>
    /// A short, number-free account of how the fight stands right now — who is still up on each side and in
    /// what shape. Recorded with a surrender offer so the offer can be read against the odds it was made under,
    /// which is the descriptive part of the persuasion report.
    /// </summary>
    private string DescribeVisibleBattleState()
    {
        var lines = _engine.State.Teams().Select(team =>
        {
            var members = _engine.State.Characters
                .Where(c => string.Equals(c.Team, team, StringComparison.OrdinalIgnoreCase))
                .Select(c => $"{c.Name} ({DescribeStanding(c)})");
            return $"{team}: {string.Join(", ", members)}";
        });

        return string.Join(" | ", lines);
    }

    private static string DescribeStanding(Character character)
    {
        switch (character.Disposition)
        {
            case CharacterDisposition.Dead:
                return "dead";
            case CharacterDisposition.Surrendered:
                return "surrendered";
            case CharacterDisposition.Escaped:
                return "fled";
        }

        if (character.MaxHealth <= 0)
        {
            return "still fighting";
        }

        var fraction = (double)character.Health / character.MaxHealth;
        return fraction switch
        {
            >= 0.999 => "unhurt",
            >= 0.75 => "lightly wounded",
            >= 0.45 => "wounded",
            >= 0.20 => "badly wounded",
            _ => "barely standing"
        };
    }

    private void EmitLimit(string limit, int value, string effect) =>
        _trace.Emit(TraceEventType.HarnessLimitReached, new HarnessLimitPayload
        {
            Limit = limit,
            Value = value,
            Effect = effect
        });
}
