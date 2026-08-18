using Microsoft.Extensions.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Presentation;
using ModelsAndMonsters.Prompts;
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
    private readonly ExperimentTrace _trace;
    private readonly IGameConsole _console;
    private readonly HarnessOptions _limits;

    public TurnCoordinator(
        IGameEngine engine,
        DungeonMasterAgent dungeonMaster,
        PromptLibrary prompts,
        WorldStateFormatter formatter,
        NarrationLog narrationLog,
        ExperimentTrace trace,
        IGameConsole console,
        HarnessOptions limits)
    {
        _engine = engine;
        _dungeonMaster = dungeonMaster;
        _prompts = prompts;
        _formatter = formatter;
        _narrationLog = narrationLog;
        _trace = trace;
        _console = console;
        _limits = limits;
    }

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
        if (!self.IsAlive)
        {
            // A dead actor is skipped without any model call, and the skip is traced so the fixed turn
            // order stays visible and uncorrupted in the record rather than a turn simply going missing.
            _trace.Emit(TraceEventType.TurnSkipped, new TurnSkippedPayload
            {
                CharacterId = character.CharacterId,
                CharacterName = character.Name,
                Team = self.Team,
                Reason = $"{character.Name} is dead and cannot take a turn."
            });

            _console.Notice($"{character.Name} lies fallen; their turn passes.");

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

        character.BeginTurn(_prompts.Render("character.turn", new Dictionary<string, string?>
        {
            ["name"] = character.Name,
            ["state"] = selfState,
            ["narration"] = pendingNarration.Count == 0
                ? "Nothing has changed since you last looked."
                : string.Join("\n\n", pendingNarration.Select(n => n.Text))
        }));

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
                // No tool call has two very different causes: the model ignored its protocol, or it
                // ran out of output budget mid-reply. They need different nudges and, more
                // importantly, they must not be confused with each other in the trace.
                var truncated = ModelAgent.WasTruncated(response);

                // A model may understand the protocol but write the call as prose rather than calling
                // it. When recovery is enabled, salvage that call and dispatch it as if it had been made,
                // rather than nudging. Truncated replies are never recovered — they may be incomplete.
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
                            ? "Character's reply was truncated at the output-token limit before any tool call was produced."
                            // Naming the finish reason covers the other ways a reply can end early, such as
                            // a provider content filter, without needing a case for each.
                            : $"Character responded without calling ask_dm, take_action or end_turn " +
                              $"(finish reason: {response.FinishReason?.Value ?? "none reported"})."
                    });

                    if (truncated)
                    {
                        _console.Notice(
                            $"{character.Name}'s reply hit the output-token limit. " +
                            "Consider raising MaxOutputTokens for that agent.");
                    }

                    character.AppendNudge(_prompts.Render(truncated ? "character.truncated" : "character.nudge"));
                    continue;
                }

                // Fall through with the recovered call in hand, dispatched exactly like a real one.
                calls = [recovered];
            }

            var turnEnded = false;

            foreach (var call in calls)
            {
                if (turnEnded)
                {
                    // The turn is already decided, but the model still asked for this. Answer it so the
                    // history stays valid for the next turn.
                    DispatchAndRecord(character, call, "Ignored: the turn ended before this could be handled.",
                        "ignored-turn-already-resolved");
                    continue;
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
                break;
            }
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

        var stateText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        var answer = await _dungeonMaster
            .AnswerQuestionAsync(stateText, character.Name, question, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(answer))
        {
            answer = "You cannot tell.";
        }

        _trace.Emit(TraceEventType.DungeonMasterAnswer, new DungeonMasterAnswerPayload
        {
            CharacterId = character.CharacterId,
            CharacterName = character.Name,
            Question = question,
            Answer = answer
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

        var spoken = message!.Trim();

        // The stored public-channel text already carries attribution, so recipients read the speaker's
        // exact words rather than a paraphrase, and delivery reuses the narration path unchanged.
        var entry = _narrationLog.RecordSpeech(character.CharacterId, $"{character.Name} says:\n\"{spoken}\"");

        // The speaker has just said it; it must not be re-delivered to them as "newly heard" next turn.
        entry.MarkDeliveredTo(character.CharacterId);

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

        // Answer the say tool so the speaker's history stays valid; its own words are already present in
        // that history as the tool-call argument, so the result is only an acknowledgement.
        RecordToolResult(character, call,
            "Your words carry across the room. You may still ask, act, or end your turn.");
        return true;
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
        var response = await _dungeonMaster
            .ProposeActionAsync(stateText, character.Name, intent, cancellationToken)
            .ConfigureAwait(false);

        var calls = ModelAgent.GetToolCalls(response);

        for (var retry = 0; calls.Count == 0 && retry < _limits.MaxAdjudicationRetries; retry++)
        {
            var truncated = ModelAgent.WasTruncated(response);
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
                or DungeonMasterTools.OpenContainerName or DungeonMasterTools.TakeItemName =>
                await HandleEngineActionAsync(character, intent, primary, cancellationToken).ConfigureAwait(false),
            _ => HandleUnknownDungeonMasterTool(character, intent, primary)
        };
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

        var resultForDungeonMaster = engineResult.Accepted
            ? engineResult.Outcome!.Summary
            : $"REJECTED BY THE WORLD: {engineResult.RejectionMessage}";

        RecordDungeonMasterToolResult(call, resultForDungeonMaster);

        if (!engineResult.Accepted)
        {
            var explanation = await _dungeonMaster
                .ExplainEngineRejectionAsync(engineResult.RejectionMessage!, character.Name, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(explanation))
            {
                explanation = engineResult.RejectionMessage!;
            }

            explanation = await InWorldRejectionAsync(character, explanation, cancellationToken).ConfigureAwait(false);

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

        // The character whose turn it is is always the one who acted, so name them explicitly to the
        // DM: a strong "the hero attacks the monster" prior otherwise makes some models invert the actor.
        // Opening a container or taking an item is narrated on its own path, which is free to name the
        // now-visible contents rather than describing a weapon strike.
        var stateAfterText = WorldStateFormatter.FormatAuthoritativeState(_engine.State);
        var isObjectAction = action is OpenContainerAction or TakeItemAction;
        var narration = await (isObjectAction
                ? _dungeonMaster.NarrateObjectOutcomeAsync(character.Name, engineResult.Outcome!.Summary,
                    DescribeObjectTransition(engineResult.Outcome!), stateAfterText, cancellationToken)
                : _dungeonMaster.NarrateOutcomeAsync(character.Name, engineResult.Outcome!.Summary, stateAfterText, cancellationToken))
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(narration))
        {
            narration = engineResult.Outcome!.Summary;
        }

        var entry = _narrationLog.Record("action-outcome", narration);

        // The acting character hears this immediately, as its tool result. Everyone else hears it when
        // their own turn begins.
        entry.MarkDeliveredTo(character.CharacterId);

        _trace.Emit(TraceEventType.Narration, new NarrationPayload
        {
            Purpose = "action-outcome",
            StateSuppliedToDungeonMaster = stateAfterText,
            ContextSuppliedToDungeonMaster = engineResult.Outcome!.Summary,
            Narration = narration,
            NarrationId = entry.Id,
            IntendedRecipients = LivingRecipients()
        }, DungeonMasterAgent.AgentIdentifier);

        _trace.Emit(TraceEventType.NarrationDelivered, new NarrationDeliveredPayload
        {
            NarrationId = entry.Id,
            Narration = narration,
            DeliveredTo = [character.CharacterId],
            DeliveryMechanism = "take_action tool result"
        });

        EmitAdjudication(character, intent, ActionResolutionCategory.EngineAccepted, null, action, null);
        _console.DungeonMaster(narration);

        return new ActionAttemptOutcome
        {
            Category = ActionResolutionCategory.EngineAccepted,
            MessageToCharacter = narration,
            Action = action,
            EngineResult = engineResult
        };
    }

    /// <summary>
    /// The exact before→after change for an object action, handed to the narration so the Dungeon Master
    /// describes only what happened. It is spelled out because a take from an already-open container was
    /// otherwise narrated as the lid being lifted; the transition makes "already open, stays open" explicit.
    /// </summary>
    private static string DescribeObjectTransition(ActionOutcome outcome) => outcome switch
    {
        OpenContainerOutcome o =>
            $"{o.ContainerName} went from CLOSED to OPEN. Nothing was taken out of it. Its contents are now " +
            $"simply in plain view: {(o.RevealedContents.Count == 0 ? "it is empty" : string.Join(", ", o.RevealedContents))}.",
        TakeItemOutcome t =>
            $"{t.ContainerName} was ALREADY OPEN and stays open — it is not opened in this moment and no lid " +
            $"is lifted. The only change is that {t.ActorName} took the {t.ItemName} out of it and now holds it.",
        _ => outcome.Summary
    };

    /// <summary>The neutral in-world line used when even a rephrase still leaks the machinery.</summary>
    private const string GenericInWorldRefusal =
        "Whatever you meant to do there finds no purchase, and nothing comes of it.";

    /// <summary>
    /// Guarantees a rejection reason reaches the character in-world. A prompt cannot reliably stop the
    /// Dungeon Master naming the machinery ("the world cannot resolve that", or even listing the actions),
    /// so a leaked reason is caught here and rephrased once; if the rephrase still leaks, a neutral in-world
    /// line is used instead. The original leak is recorded on an <see cref="TraceEventType.AdjudicationCorrected"/>
    /// event, so nothing the model produced is hidden.
    /// </summary>
    private async Task<string> InWorldRejectionAsync(CharacterAgent character, string reason, CancellationToken cancellationToken)
    {
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
            Justification = "The refusal named the machinery (the rules/engine or what can be resolved); rephrased in-world."
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

            default:
                throw new ArgumentException($"No engine action is mapped to tool '{call.Name}'.");
        }
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
            DungeonMasterText = dungeonMasterText
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
    /// Everyone alive in the room, who is therefore an intended recipient of public narration. Recorded
    /// on the narration event so the audience is explicit, even though each character actually receives
    /// it at a different moment (the actor at once, the others as their own turn begins).
    /// </summary>
    private IReadOnlyList<string> LivingRecipients() =>
        [.. _engine.State.Characters.Where(c => c.IsAlive).Select(c => c.Id)];

    /// <summary>Every living character except the given one — the intended audience for that character's speech.</summary>
    private IReadOnlyList<string> LivingRecipientsExcept(string speakerId) =>
    [
        .. _engine.State.Characters
            .Where(c => c.IsAlive && !string.Equals(c.Id, speakerId, StringComparison.OrdinalIgnoreCase))
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
        if (action is not (OpenContainerAction or TakeItemAction))
        {
            return;
        }

        var before = result.StateBefore;
        var after = result.StateAfter;

        var containerRef = action switch
        {
            OpenContainerAction open => open.ContainerRef,
            TakeItemAction take => take.ContainerRef,
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

    private void EmitLimit(string limit, int value, string effect) =>
        _trace.Emit(TraceEventType.HarnessLimitReached, new HarnessLimitPayload
        {
            Limit = limit,
            Value = value,
            Effect = effect
        });
}
