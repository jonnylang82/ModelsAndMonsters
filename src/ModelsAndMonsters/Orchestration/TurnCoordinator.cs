using Microsoft.Extensions.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
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
            NarrationId = entry.Id
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

        var selfState = _formatter.FormatCharacterSelfState(self);
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
                        if (actionAttempts >= _limits.MaxActionAttemptsPerTurn)
                        {
                            EmitLimit(nameof(HarnessOptions.MaxActionAttemptsPerTurn), _limits.MaxActionAttemptsPerTurn,
                                $"{character.Name}'s turn was abandoned after too many failed attempts.");
                            DispatchAndRecord(character, call,
                                "You hesitate too long, and the moment passes.",
                                "refused-attempt-limit");
                            turnEnded = true;
                            outcome = TurnOutcome.AbandonedAtLimit;
                            break;
                        }

                        actionAttempts++;
                        var attempt = await HandleTakeActionAsync(character, call, cancellationToken).ConfigureAwait(false);

                        if (attempt.ConsumesTurn)
                        {
                            turnEnded = true;
                            outcome = TurnOutcome.ActionResolved;
                            acceptedAction = attempt.Action?.Describe();
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
            NarrationId = entry.Id
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
            DungeonMasterTools.RejectActionName => HandleDungeonMasterRejection(character, intent, primary),
            DungeonMasterTools.AttackCharacterName or DungeonMasterTools.UseItemName =>
                await HandleEngineActionAsync(character, intent, primary, cancellationToken).ConfigureAwait(false),
            _ => HandleUnknownDungeonMasterTool(character, intent, primary)
        };
    }

    private ActionAttemptOutcome HandleDungeonMasterRejection(CharacterAgent character, string intent, FunctionCallContent call)
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

        // The engine, not the Dungeon Master, decides what actually happens.
        var engineResult = _engine.Execute(action);

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
        var narration = await _dungeonMaster
            .NarrateOutcomeAsync(character.Name, engineResult.Outcome!.Summary, WorldStateFormatter.FormatAuthoritativeState(_engine.State), cancellationToken)
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
            StateSuppliedToDungeonMaster = WorldStateFormatter.FormatAuthoritativeState(_engine.State),
            ContextSuppliedToDungeonMaster = engineResult.Outcome!.Summary,
            Narration = narration,
            NarrationId = entry.Id
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

    private void EmitLimit(string limit, int value, string effect) =>
        _trace.Emit(TraceEventType.HarnessLimitReached, new HarnessLimitPayload
        {
            Limit = limit,
            Value = value,
            Effect = effect
        });
}
