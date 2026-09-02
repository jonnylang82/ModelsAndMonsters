using System.Collections.Immutable;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Engine;

/// <summary>
/// The minimal deterministic game engine.
/// </summary>
/// <remarks>
/// <para>
/// Attack rule: the attacker rolls d100; the attack lands when the roll is at or under the attacker's
/// EFFECTIVE hit chance — their own chance plus every status modifier, applied in a fixed order — otherwise
/// it misses. A landed hit rolls again for a glancing blow (half damage). Base damage is
/// <c>max(0, weaponDamage - targetArmour)</c>, and a Defending target then turns aside its reduction, floored
/// at zero. All rolls come from the injected <see cref="IRng"/>, so a run is reproducible from its seed and
/// every roll is traced with its base chance, its modifiers and its result.
/// </para>
/// <para>Death: health clamps at 0 and a character is dead at 0.</para>
/// <para>Healing: <c>newHealth = min(maxHealth, health + healingAmount)</c>. No roll, whether from an item or a spell.</para>
/// <para>
/// Injuries are recorded by a single deliberately trivial rule (crossing half health, or dying) so
/// that persistent descriptive state demonstrably survives into later prompts. It is not intended to
/// be an injury system.
/// </para>
/// <para>
/// Status effects, surrender offers and surrender agreements are authoritative engine state, kept on
/// <see cref="GameState"/>. Every status has an exact expiry rule, swept deterministically by
/// <see cref="BeginActorTurn"/> and <see cref="EndActorTurn"/>; no status is ever created by narration, and
/// none can be created by a model.
/// </para>
/// </remarks>
public sealed class GameEngine : IGameEngine
{
    private readonly IRng _rng;
    private readonly CombatRules _combatRules;
    private GameState _state;

    // Monotonic counters behind the stable ids the engine mints. They are plain counters, not GUIDs, so a
    // fixed-seed run produces identical ids as well as identical rolls and is byte-for-byte comparable.
    private int _statusCounter;
    private int _relationshipCounter;
    private int _offerCounter;
    private int _demandCounter;
    private int _agreementCounter;
    private int _intimidationCounter;

    public GameEngine(GameState initialState, IRng rng, CombatRules combatRules)
    {
        _rng = rng;
        _combatRules = combatRules;

        // The outnumbering latch is seeded from the opening state and raises no fear. Fear rises on the
        // TRANSITION into being outnumbered, and a character who is already outnumbered when the fight starts
        // has not made that transition — the scenario decided the odds, not the fight.
        _state = SeedScaredStatuses(SeedOutnumbering(initialState));
    }

    public GameState State => _state;

    public int CurrentRound { get; set; }

    /// <summary>
    /// The global turn number currently being played, stamped onto every status the engine applies. Set by
    /// <see cref="BeginActorTurn"/>; expiry requires a strictly later turn, so a status can never expire on
    /// the very turn it was applied.
    /// </summary>
    public int CurrentTurn { get; set; }

    public EngineResult Execute(GameAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var result = action switch
        {
            AttackCharacterAction attack => ResolveAttack(attack),
            UseItemAction useItem => ResolveUseItem(useItem),
            OpenContainerAction open => ResolveOpenContainer(open),
            TakeItemAction take => ResolveTakeItem(take),
            InspectObjectAction inspect => ResolveInspectObject(inspect),
            OpenExitAction openExit => ResolveOpenExit(openExit),
            EscapeEncounterAction escape => ResolveEscapeEncounter(escape),
            OfferSurrenderAction offer => ResolveOfferSurrender(offer),
            AcceptSurrenderAction accept => ResolveAcceptSurrender(accept),
            DemandSurrenderAction demand => ResolveDemandSurrender(demand),
            UseAbilityAction ability => ResolveUseAbility(ability),
            IntimidateCharacterAction intimidate => ResolveIntimidate(intimidate),
            SteadyAllyAction steady => ResolveSteadyAlly(steady),
            DefendAction defend => ResolveUseAbility(new UseAbilityAction(defend.ActorRef, AbilityCatalog.DefendId)),
            GiveItemAction give => ResolveGiveItem(give),
            PresentItemAction present => ResolvePresentItem(present),
            DropItemAction drop => ResolveDropItem(drop),
            StealItemAction steal => ResolveStealItem(steal),
            TakeCoverAction takeCover => ResolveTakeCover(takeCover),
            LeaveCoverAction leaveCover => ResolveLeaveCover(leaveCover),
            DamageEnvironmentalObjectAction damageObject => ResolveDamageEnvironmentalObject(damageObject),
            _ => EngineResult.Reject(action, _state, EngineRejectionReason.UnsupportedAction,
                $"The engine has no handler for action type '{action.ActionType}'.")
        };

        if (result.Accepted)
        {
            // Who is outnumbered is a consequence of the action, not part of it: a death, a surrender or an
            // escape can turn the odds against somebody who was not even involved. Reconciling here, once,
            // means every accepted action gets the same treatment and no handler has to remember to do it.
            result = ReconcileOutnumbering(result);
            _state = result.StateAfter;
        }

        return result;
    }

    // ===============================================================================================
    // Morale
    // ===============================================================================================

    /// <summary>
    /// Moves one character's fear by a clamped amount, records the change with its cause, and keeps the
    /// public <see cref="StatusEffectKind.Scared"/> status exactly in step with the threshold.
    /// </summary>
    /// <remarks>
    /// This is the ONLY way fear ever moves. Every caller states a cause from the closed
    /// <see cref="FearChangeCause"/> set, so a change without a roll is still fully explained by the record,
    /// and a change the clamp absorbed is recorded as having been absorbed rather than quietly vanishing.
    /// The status is applied and removed here and nowhere else, which is what makes it impossible for the
    /// public shadow to disagree with the number it shadows.
    /// </remarks>
    private GameState ApplyFear(
        GameState state,
        string characterId,
        int delta,
        FearChangeCause cause,
        string causeDetail,
        string? sourceCharacterId,
        string? relatedActionType,
        List<StatusEvent> statusEvents,
        List<FearChange> fearChanges)
    {
        if (delta == 0 || state.FindById(characterId) is not { } character)
        {
            return state;
        }

        var before = character.Fear;
        var after = FearRules.Clamp(before + delta);
        state = state.WithCharacter(character with { Fear = after });

        var transition = ScaredTransition.None;
        if (!FearRules.IsScared(before) && FearRules.IsScared(after))
        {
            transition = ScaredTransition.BecameScared;
            var scared = NewStatus(StatusEffectKind.Scared, characterId, characterId, modifier: 0,
                StatusExpiryRule.WhileConditionHolds);
            state = state.WithStatus(scared);
            statusEvents.Add(new StatusEvent(StatusEventKind.Applied, scared,
                $"{character.Name} lost their nerve ({causeDetail})"));
        }
        else if (FearRules.IsScared(before) && !FearRules.IsScared(after))
        {
            transition = ScaredTransition.RecoveredFromScared;
            foreach (var held in state.StatusesOn(characterId).Where(x => x.Kind == StatusEffectKind.Scared).ToList())
            {
                state = state.WithoutStatuses([held.Id]);
                statusEvents.Add(new StatusEvent(StatusEventKind.Removed, held,
                    $"{character.Name} got their nerve back ({causeDetail})"));
            }
        }

        fearChanges.Add(new FearChange
        {
            CharacterId = character.Id,
            CharacterName = character.Name,
            Cause = cause,
            CauseDetail = causeDetail,
            Delta = delta,
            Before = before,
            After = after,
            SourceCharacterId = sourceCharacterId,
            SourceCharacterName = sourceCharacterId is null ? null : state.FindById(sourceCharacterId)?.Name,
            RelatedActionType = relatedActionType,
            Transition = transition
        });

        return state;
    }

    /// <summary>
    /// Whether a character is outnumbered right now: strictly more ACTIVE enemies than active characters on
    /// their own side, counting themselves. Dead, surrendered, escaped and absent characters count for
    /// nobody, and a character who is not active themselves is never outnumbered.
    /// </summary>
    private static bool IsOutnumberedNow(GameState state, Character character)
    {
        if (!character.CanAct)
        {
            return false;
        }

        var enemies = state.Characters.Count(c => c.CanAct && !character.IsAllyOf(c));
        var ownSide = state.Characters.Count(c => c.CanAct && character.IsAllyOf(c));
        return enemies > ownSide;
    }

    /// <summary>
    /// Gives the public Scared status to anybody a scenario seeded at or above the threshold.
    /// </summary>
    /// <remarks>
    /// The status is normally applied by the CHANGE that crosses the threshold, so a character who simply
    /// starts frightened would otherwise be scared with nothing on them to show it — the number and its
    /// public shadow would disagree from the first turn. Seeding it here is the only place the status is
    /// created outside <see cref="ApplyFear"/>, and it raises no fear and emits no event: nothing happened,
    /// the scenario just said so.
    /// </remarks>
    private GameState SeedScaredStatuses(GameState state)
    {
        foreach (var character in state.Characters)
        {
            if (!character.IsScared || state.StatusOn(character.Id, StatusEffectKind.Scared) is not null)
            {
                continue;
            }

            state = state.WithStatus(NewStatus(StatusEffectKind.Scared, character.Id, character.Id,
                modifier: 0, StatusExpiryRule.WhileConditionHolds));
        }

        return state;
    }

    /// <summary>Sets every character's outnumbering latch to match the state, raising no fear. Used once, at construction.</summary>
    private static GameState SeedOutnumbering(GameState state)
    {
        foreach (var character in state.Characters)
        {
            var outnumbered = IsOutnumberedNow(state, character);
            if (outnumbered != character.IsOutnumbered)
            {
                state = state.WithCharacter(state.RequireById(character.Id) with { IsOutnumbered = outnumbered });
            }
        }

        return state;
    }

    /// <summary>
    /// Brings every outnumbering latch up to date after an accepted action, and raises fear by one for each
    /// character who has just crossed from not outnumbered to outnumbered.
    /// </summary>
    /// <remarks>
    /// A latch, not a per-round check: standing outnumbered for five rounds is the same fact five times over
    /// and frightens nobody further. Parity genuinely restored clears the latch, so a character who is
    /// outnumbered again later has made a fresh transition and may be frightened again.
    /// </remarks>
    private EngineResult ReconcileOutnumbering(EngineResult result)
    {
        var state = result.StateAfter;
        var ids = state.Characters.Select(c => c.Id).ToList();

        List<StatusEvent>? statusEvents = null;
        List<FearChange>? fearChanges = null;
        var changed = false;

        foreach (var id in ids)
        {
            var character = state.RequireById(id);

            // Only those still in the fight. A character who has died, yielded or fled keeps the latch they
            // left with: they are not outnumbered by anybody any more, and resetting it would rewrite the
            // world version for a fact about somebody who is no longer playing.
            if (!character.CanAct)
            {
                continue;
            }

            var outnumbered = IsOutnumberedNow(state, character);
            if (outnumbered == character.IsOutnumbered)
            {
                continue;
            }

            state = state.WithCharacter(character with { IsOutnumbered = outnumbered });
            changed = true;

            if (!outnumbered)
            {
                continue;
            }

            statusEvents ??= [.. result.StatusEvents];
            fearChanges ??= [.. result.FearChanges];
            state = ApplyFear(state, id, +1, FearChangeCause.BecameOutnumbered,
                "the odds turned: more enemies still fighting than allies", sourceCharacterId: null,
                result.Action.ActionType, statusEvents, fearChanges);
        }

        if (!changed)
        {
            return result;
        }

        return result with
        {
            StateAfter = state with { Version = state.Version + 1 },
            StatusEvents = statusEvents ?? result.StatusEvents,
            FearChanges = fearChanges ?? result.FearChanges
        };
    }

    /// <summary>
    /// Resolves one attempt to frighten an opponent: validation, one fully traced draw against a chance
    /// derived only from the authoritative state, and — on a success — exactly one point of fear.
    /// </summary>
    /// <remarks>
    /// Nothing about the threat's wording reaches the odds. The spoken line is required to exist (and, when
    /// the speaker declared an addressee, to be aimed at this target) and is then carried only as an id: the
    /// engine never reads it. Every attempt, successful or not, is recorded against the actor-target pair, so
    /// the affordance is spent whichever way the die falls.
    /// </remarks>
    private EngineResult ResolveIntimidate(IntimidateCharacterAction action)
    {
        var state = _state;

        var actor = state.Resolve(action.ActorRef);
        if (actor is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ActorRef}' in the room.");
        }

        if (!actor.CanAct)
        {
            return EngineResult.Reject(action, state,
                actor.IsAlive ? EngineRejectionReason.ActorNotActive : EngineRejectionReason.ActorIsDead,
                $"{actor.Name} is not an active combatant and cannot act.");
        }

        var target = state.Resolve(action.TargetRef);
        if (target is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownTarget,
                $"There is no character called '{action.TargetRef}' in the room.");
        }

        if (Same(target.Id, actor.Id))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.TargetIsSelf,
                $"{actor.Name} cannot threaten themselves.");
        }

        if (actor.IsAllyOf(target))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.TargetIsNotAnOpponent,
                $"{target.Name} fights on {actor.Name}'s own side; a threat is for an enemy.");
        }

        if (!target.IsCombatTarget)
        {
            return RejectUnavailableTarget(action, state, target, "threaten");
        }

        // Structural speech validation, before any draw: the threat has to have actually been spoken this
        // turn, and — when the speaker named who they were speaking to — it has to have been aimed here.
        // Neither check reads a single word of what was said.
        if (action.AssociatedSpeechEventId is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.IntimidationRequiresSpeech,
                $"{actor.Name} made no threat aloud; there is nothing for {target.Name} to be threatened by.");
        }

        if (action.SpeechAddressedToId is { } addressed && !Same(addressed, target.Id))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.SpeechAddressedToSomebodyElse,
                $"{actor.Name} spoke to somebody other than {target.Name}.");
        }

        if (state.HasAttemptedIntimidation(actor.Id, target.Id))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.AlreadyAttemptedIntimidation,
                $"{actor.Name} has already tried to frighten {target.Name} in this fight; that card is played.");
        }

        // ---- Modifiers, every one of them read from the authoritative state, in a fixed order. ----
        var baseChance = _combatRules.BaseIntimidationChance;
        var modifiers = new List<RngModifier>();
        var order = 1;

        if (target.Fear > 0)
        {
            modifiers.Add(new RngModifier("target-fear", target.Id,
                target.Fear * IntimidationRules.PerTargetFearPoint, order++, Consumed: false));
        }

        if (target.IsOutnumbered)
        {
            modifiers.Add(new RngModifier("target-outnumbered", target.Id,
                IntimidationRules.TargetOutnumbered, order++, Consumed: false));
        }

        if (HealthBands.IsBadlyWoundedOrWorse(target))
        {
            modifiers.Add(new RngModifier("target-badly-wounded", target.Id,
                IntimidationRules.TargetBadlyWounded, order++, Consumed: false));
        }

        if (actor.IsScared)
        {
            modifiers.Add(new RngModifier("intimidator-scared", actor.Id,
                IntimidationRules.IntimidatorScared, order++, Consumed: false));
        }

        var effectiveChance = IntimidationRules.Clamp(baseChance + modifiers.Sum(m => m.Value));

        var sequenceBefore = _rng.DrawCount;
        var roll = _rng.RollPercent();
        var succeeded = roll <= effectiveChance;
        var draw = new RngDraw
        {
            Purpose = "intimidation.check",
            ActionType = action.ActionType,
            ActorId = actor.Id,
            ActorName = actor.Name,
            TargetId = target.Id,
            TargetName = target.Name,
            OutcomeSelected = "the threat tells or it does not",
            Sides = 100,
            RangeMin = 1,
            RangeMax = 100,
            RawRoll = roll,
            BaseChance = baseChance,
            Modifiers = [.. modifiers.Select(m => m.Note)],
            ModifierDetails = modifiers,
            Threshold = effectiveChance,
            Comparison = $"roll {roll} {(succeeded ? "<=" : ">")} effective chance {effectiveChance} " +
                         $"(clamped to {IntimidationRules.MinimumChance}-{IntimidationRules.MaximumChance})",
            Result = succeeded ? "intimidated" : "unmoved",
            Seed = _rng.Seed,
            SequenceBefore = sequenceBefore,
            SequenceAfter = _rng.DrawCount
        };

        var statusEvents = new List<StatusEvent>();
        var fearChanges = new List<FearChange>();

        var after = state.WithIntimidationAttempt(new IntimidationAttempt
        {
            Id = $"intimidation-{++_intimidationCounter}",
            ActorId = actor.Id,
            TargetId = target.Id,
            Round = CurrentRound,
            Turn = CurrentTurn,
            BaseChance = baseChance,
            EffectiveChance = effectiveChance,
            Roll = roll,
            Succeeded = succeeded,
            AssociatedSpeechEventId = action.AssociatedSpeechEventId
        });

        if (succeeded)
        {
            after = ApplyFear(after, target.Id, +1, FearChangeCause.Intimidated,
                $"{actor.Name}'s open threat told", actor.Id, action.ActionType, statusEvents, fearChanges);
        }

        after = after with { Version = state.Version + 1 };

        var outcome = new IntimidateOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            TargetId = target.Id,
            TargetName = target.Name,
            BaseChance = baseChance,
            Modifiers = modifiers,
            EffectiveChance = effectiveChance,
            Roll = roll,
            Succeeded = succeeded,
            AssociatedSpeechEventId = action.AssociatedSpeechEventId,
            FearChanges = fearChanges
        };

        return EngineResult.Accept(action, state, after, outcome, [draw], statusEvents, offerTransitions: null, fearChanges);
    }

    /// <summary>
    /// Resolves one character spending their whole turn steadying an ally: validation, one point of fear
    /// removed, and no draw at all. Reassurance is deterministic in v0.8.
    /// </summary>
    /// <remarks>
    /// An ally already unafraid is not refused: the turn is spent and nothing moves, which is exactly what
    /// spending a turn on unnecessary reassurance should cost. The change is still recorded, marked as
    /// absorbed by the floor, so the record shows the action happening and achieving nothing rather than
    /// showing nothing at all.
    /// </remarks>
    private EngineResult ResolveSteadyAlly(SteadyAllyAction action)
    {
        var state = _state;

        var actor = state.Resolve(action.ActorRef);
        if (actor is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ActorRef}' in the room.");
        }

        if (!actor.CanAct)
        {
            return EngineResult.Reject(action, state,
                actor.IsAlive ? EngineRejectionReason.ActorNotActive : EngineRejectionReason.ActorIsDead,
                $"{actor.Name} is not an active combatant and cannot act.");
        }

        var target = state.Resolve(action.TargetRef);
        if (target is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownTarget,
                $"There is no character called '{action.TargetRef}' in the room.");
        }

        if (Same(target.Id, actor.Id))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.TargetIsSelf,
                $"{actor.Name} cannot steady themselves; this is something done for a companion.");
        }

        if (!actor.IsAllyOf(target))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.TargetIsNotAnAlly,
                $"{target.Name} does not fight on {actor.Name}'s side.");
        }

        if (!target.CanAct || !target.IsPresent)
        {
            return RejectUnavailableTarget(action, state, target, "steady");
        }

        if (action.AssociatedSpeechEventId is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.IntimidationRequiresSpeech,
                $"{actor.Name} said nothing aloud, so there were no words for {target.Name} to take heart from.");
        }

        if (action.SpeechAddressedToId is { } addressed && !Same(addressed, target.Id))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.SpeechAddressedToSomebodyElse,
                $"{actor.Name} spoke to somebody other than {target.Name}.");
        }

        var statusEvents = new List<StatusEvent>();
        var fearChanges = new List<FearChange>();
        var wasScared = target.IsScared;

        var after = ApplyFear(state, target.Id, -1, FearChangeCause.SteadiedByAlly,
            $"{actor.Name} steadied them", actor.Id, action.ActionType, statusEvents, fearChanges);

        after = after with { Version = state.Version + 1 };

        var outcome = new SteadyAllyOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            TargetId = target.Id,
            TargetName = target.Name,
            AssociatedSpeechEventId = action.AssociatedSpeechEventId,
            FearChanges = fearChanges,
            NoLongerScared = wasScared && !after.RequireById(target.Id).IsScared
        };

        return EngineResult.Accept(action, state, after, outcome, rngDraws: null, statusEvents,
            offerTransitions: null, fearChanges);
    }

    // ===============================================================================================
    // Turn upkeep — deterministic status expiry and surrender-offer lapsing
    // ===============================================================================================

    /// <summary>
    /// Start-of-turn upkeep for one actor: expires the statuses whose rule fires at the start of this
    /// actor's turn (the guard relationship they were sustaining, and their own Defending). Uses no
    /// randomness, and only bumps the world version when something actually fell away.
    /// </summary>
    public TurnUpkeep BeginActorTurn(string actorId, int round, int turn)
    {
        CurrentRound = round;
        CurrentTurn = turn;

        var due = _state.Statuses.Where(status => status.AppliedTurn < turn && status.ExpiryRule switch
        {
            StatusExpiryRule.StartOfSourceNextTurn => Same(status.SourceCharacterId, actorId),
            StatusExpiryRule.StartOfTargetNextTurn => Same(status.TargetCharacterId, actorId),
            _ => false
        }).ToList();

        var events = new List<StatusEvent>();
        var state = ExpireStatuses(_state, due, "reached its expiry at the start of the holder's next turn", events);

        // A character stunned on an earlier turn loses this one entirely. The Stunned status never expires on
        // the turn clock (WhileConditionHolds, so it is not in `due` above); it is consumed HERE, in the same
        // breath that it costs the turn, so a stun is worth exactly one skipped turn and no more. The character
        // stays alive, present and a valid target — only the turn is taken.
        var incapacitated = false;
        var stun = state.StatusesOn(actorId).FirstOrDefault(s => s.Kind == StatusEffectKind.Stunned && s.AppliedTurn < turn);
        if (stun is not null)
        {
            var name = state.FindById(actorId)?.Name ?? actorId;
            state = state.WithoutStatuses([stun.Id]);
            events.Add(new StatusEvent(StatusEventKind.Consumed, stun with { Consumed = true },
                $"{name} lost the turn to being stunned, and the daze cleared"));
            incapacitated = true;
        }

        if (events.Count > 0)
        {
            _state = state with { Version = state.Version + 1 };
        }

        return new TurnUpkeep { StatusEvents = events, ActorIncapacitated = incapacitated };
    }

    /// <summary>
    /// End-of-turn upkeep for one actor: expires the statuses whose rule fires at the end of this actor's
    /// turn (an unused Rallied or OffBalance they were carrying), and lapses any surrender offer this actor
    /// was the named recipient of and did not accept. Uses no randomness.
    /// </summary>
    public TurnUpkeep EndActorTurn(string actorId, int round, int turn)
    {
        CurrentRound = round;
        CurrentTurn = turn;

        var due = _state.Statuses
            .Where(status => status.AppliedTurn < turn
                             && status.ExpiryRule == StatusExpiryRule.EndOfTargetNextTurn
                             && Same(status.TargetCharacterId, actorId))
            .ToList();

        var statusEvents = new List<StatusEvent>();
        var state = ExpireStatuses(_state, due, "reached its expiry at the end of the holder's next turn", statusEvents);

        // An offer the recipient did not act on lapses the moment they finish a turn. It transfers nothing;
        // the offerer may propose better terms on a later turn.
        var transitions = new List<OfferTransition>();
        foreach (var offer in state.PendingOffersTo(actorId).ToList())
        {
            if (!offer.ExpiresAfterRecipientTurn || offer.CreatedTurn >= turn)
            {
                continue;
            }

            state = TransitionOffer(state, offer, SurrenderOfferState.Expired,
                "the named recipient completed a turn without accepting", round, turn, transitions);
        }

        // A demand the target did not answer lapses the moment they finish a turn — exactly like an offer.
        // It compelled nothing; the target simply chose not to yield, and the pressure is spent.
        var demandTransitions = new List<DemandTransition>();
        foreach (var demand in state.PendingDemandsAgainst(actorId).ToList())
        {
            if (demand.CreatedTurn >= turn)
            {
                continue;
            }

            state = TransitionDemand(state, demand, SurrenderDemandState.Expired,
                "the target completed a turn without yielding", round, turn, demandTransitions);
        }

        if (statusEvents.Count > 0 || transitions.Count > 0 || demandTransitions.Count > 0)
        {
            _state = state with { Version = state.Version + 1 };
        }

        return new TurnUpkeep
        {
            StatusEvents = statusEvents,
            OfferTransitions = transitions,
            DemandTransitions = demandTransitions
        };
    }

    // ===============================================================================================
    // Combat
    // ===============================================================================================

    private EngineResult ResolveAttack(AttackCharacterAction action)
    {
        var state = _state;

        var attacker = state.Resolve(action.AttackerRef);
        if (attacker is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.AttackerRef}' in the room.");
        }

        if (!attacker.CanAct)
        {
            return RejectInactiveActor(action, state, attacker);
        }

        var target = state.Resolve(action.TargetRef);
        if (target is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownTarget,
                $"There is no character called '{action.TargetRef}' in the room.");
        }

        if (Same(target.Id, attacker.Id))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.TargetIsSelf,
                $"{attacker.Name} cannot attack themselves.");
        }

        // Only an active combatant can be struck. A character who is dead, has surrendered, or has escaped
        // is out of the fight, and each is refused with its own reason so the DM can explain it in-world.
        if (!target.IsCombatTarget)
        {
            return RejectUnavailableTarget(action, state, target, "strike");
        }

        if (attacker.Weapon is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ActorHasNoWeapon,
                $"{attacker.Name} is not carrying a weapon.");
        }

        if (!attacker.HasWeaponNamed(action.WeaponRef))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.WeaponNotPossessed,
                $"{attacker.Name} does not have '{action.WeaponRef}'. {attacker.Name} is carrying {attacker.Weapon.Name}.");
        }

        return ResolveWeaponStrike(action, state, attacker, target, attacker.Weapon, ability: null);
    }

    /// <summary>
    /// The one and only weapon-strike resolution, shared by <c>attack_character</c> and by any ability that
    /// performs a weapon attack.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is deliberately shared so an ability-driven blow (Dirty Strike) can never become a parallel combat
    /// path with its own dice: exactly one hit draw, and — only on a hit — exactly one QUALITY draw, whichever
    /// route reached here. Guard redirection consumes no draw at all; it changes who the one draw is resolved
    /// against, before it is made.
    /// </para>
    /// <para>
    /// Order of resolution: redirect (guard), modify the hit chance (Rallied, OffBalance — both consumed
    /// whatever the result), roll to hit, roll ONCE for quality (glancing, solid or critical — never a
    /// glancing roll followed by a separate critical roll), subtract armour, apply the quality multiplier,
    /// then subtract the target's Defending reduction (consumed only by a blow that actually lands), and
    /// finally move morale: a surviving target frightened by a critical or heavy blow, and an attacker
    /// steadied by landing a critical one.
    /// </para>
    /// </remarks>
    private EngineResult ResolveWeaponStrike(
        GameAction action,
        GameState state,
        Character attacker,
        Character intendedTarget,
        Weapon weapon,
        AbilityDefinition? ability,
        bool ignoresArmour = false)
    {
        var statusEvents = new List<StatusEvent>();
        var offerTransitions = new List<OfferTransition>();
        var demandTransitions = new List<DemandTransition>();
        var draws = new List<RngDraw>(2);
        var consumedStatusIds = new List<string>();

        // ---- The attacker's own cover, if any, breaks the instant they swing (v0.9) — before the roll, and
        // whether the swing lands, is intercepted or misses. A no-op when the attacker is not sheltering
        // behind anything. Computed as a delta against `state` and folded into `after` below, never mutating
        // `state` itself: `state` stays the true snapshot from before this whole action, so the trace shows
        // the attacker still behind cover in StateBefore and exposed only in StateAfter. ----
        var selfExposure = ExposeIfInCover(state, attacker.Id);

        // ---- Guard redirection: who the blow actually lands on. No draw is made or spent here. ----
        var target = intendedTarget;
        var redirected = false;
        var guarded = state.StatusOn(intendedTarget.Id, StatusEffectKind.Guarded);
        if (guarded is not null && !attacker.IsAllyOf(intendedTarget))
        {
            var guardian = state.FindById(guarded.SourceCharacterId);
            if (guardian is not null && guardian.CanAct && guardian.IsPresent && !Same(guardian.Id, attacker.Id))
            {
                target = guardian;
                redirected = true;
                foreach (var half in LinkedStatuses(state, guarded))
                {
                    consumedStatusIds.Add(half.Id);
                    statusEvents.Add(new StatusEvent(StatusEventKind.Consumed, half with { Consumed = true },
                        $"turned aside one blow aimed at {intendedTarget.Name}"));
                }
            }
            else
            {
                // The guardian can no longer sustain it (they left active play between application and now).
                foreach (var half in LinkedStatuses(state, guarded))
                {
                    consumedStatusIds.Add(half.Id);
                    statusEvents.Add(new StatusEvent(StatusEventKind.Removed, half,
                        "the guardian could no longer sustain the guard"));
                }
            }
        }

        // ---- Hit-chance modifiers, applied in a fixed order so the effective chance is reproducible. ----
        var modifiers = new List<RngModifier>();
        var order = 1;
        foreach (var kind in HitChanceModifierOrder)
        {
            if (state.StatusOn(attacker.Id, kind) is not { } status)
            {
                continue;
            }

            modifiers.Add(new RngModifier(kind.ToString(), status.SourceCharacterId, status.Modifier, order++, Consumed: true));
            consumedStatusIds.Add(status.Id);
            statusEvents.Add(new StatusEvent(StatusEventKind.Consumed, status with { Consumed = true },
                $"folded into {attacker.Name}'s attack hit chance"));
        }

        var baseHitChance = attacker.HitChance;
        var preCoverEffectiveHitChance = Math.Clamp(baseHitChance + modifiers.Sum(m => m.Value), 0, 100);
        var healthBefore = target.Health;

        // ---- Environmental cover (v0.9): does the TARGET currently shelter behind something intact or
        // damaged? Looked up fresh from state, never cached on the status, so a modifier always reflects the
        // cover's current durability. Destroyed cover (should the invariant ever slip) provides nothing. ----
        var targetInCover = state.StatusOn(target.Id, StatusEffectKind.InCover);
        var cover = targetInCover is null ? null : state.CoverOccupiedBy(target.Id);
        if (cover is { CanProvideCover: false })
        {
            cover = null;
        }

        var coveredEffectiveHitChance = preCoverEffectiveHitChance;
        var drawModifiers = modifiers;
        if (cover is not null)
        {
            var coverModifier = new RngModifier("Cover", cover.Id, cover.HitChanceModifier, modifiers.Count + 1, Consumed: false);
            drawModifiers = [.. modifiers, coverModifier];
            coveredEffectiveHitChance = Math.Clamp(preCoverEffectiveHitChance + cover.HitChanceModifier, 0, 100);
        }

        // ---- The one hit draw. Three outcomes when cover is present: a DIRECT HIT (roll beats the covered
        // chance), a COVER INTERCEPTION (roll would have beaten the pre-cover chance but not the covered one —
        // the cover, not luck, is what saved the target), or an ORDINARY MISS (roll beats neither). Without
        // cover this collapses to the ordinary two-outcome draw, unchanged from before v0.9. ----
        var hitSequenceBefore = _rng.DrawCount;
        var hitRoll = _rng.RollPercent();
        var hit = hitRoll <= coveredEffectiveHitChance;
        var interceptedByCover = !hit && cover is not null && hitRoll <= preCoverEffectiveHitChance;
        var hitResult = hit ? "hit" : interceptedByCover ? "intercepted-by-cover" : "miss";
        var hitComparison = cover is null
            ? $"roll {hitRoll} {(hit ? "<=" : ">")} effective hit chance {coveredEffectiveHitChance}"
            : hit
                ? $"roll {hitRoll} <= covered effective hit chance {coveredEffectiveHitChance} (pre-cover chance was {preCoverEffectiveHitChance}) — a direct hit despite cover"
                : interceptedByCover
                    ? $"roll {hitRoll} > covered effective hit chance {coveredEffectiveHitChance} but <= pre-cover effective hit chance {preCoverEffectiveHitChance} — {cover.Name} intercepted the blow"
                    : $"roll {hitRoll} > pre-cover effective hit chance {preCoverEffectiveHitChance} (covered chance was {coveredEffectiveHitChance}) — an ordinary miss; the cover changed nothing";
        draws.Add(new RngDraw
        {
            Purpose = "attack.hit-check",
            ActionType = action.ActionType,
            ActorId = attacker.Id,
            ActorName = attacker.Name,
            TargetId = target.Id,
            TargetName = target.Name,
            OutcomeSelected = cover is null ? "hit or miss" : "direct hit, cover interception, or ordinary miss",
            Sides = 100,
            RangeMin = 1,
            RangeMax = 100,
            RawRoll = hitRoll,
            BaseChance = baseHitChance,
            Modifiers = [.. drawModifiers.Select(m => m.Note)],
            ModifierDetails = drawModifiers,
            Threshold = coveredEffectiveHitChance,
            Comparison = hitComparison,
            Result = hitResult,
            Seed = _rng.Seed,
            SequenceBefore = hitSequenceBefore,
            SequenceAfter = _rng.DrawCount
        });

        // ---- A cover interception: the blow never reaches the target. No quality draw — nothing landed.
        // Exactly one point of durability is lost, ignoring the cover's own armour (that only applies to
        // deliberate damage_environmental_object). Reducing durability to zero destroys the cover and exposes
        // whoever was behind it, in the same event. ----
        CoverObject? coverAfter = null;
        var coverDestroyedNow = false;
        if (interceptedByCover)
        {
            var newDurability = Math.Max(0, cover!.CurrentDurability - 1);
            coverAfter = cover with { CurrentDurability = newDurability };
            coverDestroyedNow = coverAfter.State == EnvironmentalObjectState.Destroyed;
            if (coverDestroyedNow)
            {
                coverAfter = coverAfter with { CurrentOccupantId = null };
                consumedStatusIds.Add(targetInCover!.Id);
                statusEvents.Add(new StatusEvent(StatusEventKind.Consumed, targetInCover with { Consumed = true },
                    $"{cover.Name} was destroyed and {target.Name} was exposed"));
            }
        }

        int? qualityRoll = null;
        var quality = AttackQuality.Solid;
        var baseDamage = 0;
        var damage = 0;
        var defendReduction = 0;
        var healthAfter = healthBefore;
        var died = false;
        Injury? injury = null;
        string? statusApplied = null;

        if (hit)
        {
            // ---- The one quality draw, taken only because the blow landed. It selects among all three
            // outcomes at once: glancing and critical are opposite ends of a single roll, never two rolls. ----
            var qualitySequenceBefore = _rng.DrawCount;
            var roll = _rng.RollPercent();
            qualityRoll = roll;
            quality = _combatRules.QualityFor(roll);
            draws.Add(new RngDraw
            {
                Purpose = "attack.quality-check",
                ActionType = action.ActionType,
                ActorId = attacker.Id,
                ActorName = attacker.Name,
                TargetId = target.Id,
                TargetName = target.Name,
                OutcomeSelected = $"glancing, solid or critical ({_combatRules.DescribeQualityBands()})",
                Sides = 100,
                RangeMin = 1,
                RangeMax = 100,
                RawRoll = roll,
                Threshold = _combatRules.GlancingBlowChance,
                Comparison = _combatRules.DescribeQualityComparison(roll, quality),
                Result = quality.ToString().ToLowerInvariant(),
                Seed = _rng.Seed,
                SequenceBefore = qualitySequenceBefore,
                SequenceAfter = _rng.DrawCount
            });

            // Magical fire ignores armour entirely (Firebolt); every other blow is reduced by it as usual.
            baseDamage = ignoresArmour ? weapon.Damage : Math.Max(0, weapon.Damage - target.Armour);
            damage = CombatRules.DamageFor(quality, baseDamage);

            // Defending applies last: after armour and after glancing, never below zero, and consumed by any
            // blow that lands — including one that armour had already reduced to nothing.
            if (state.StatusOn(target.Id, StatusEffectKind.Defending) is { } defending)
            {
                defendReduction = Math.Min(Math.Abs(defending.Modifier), damage);
                damage -= defendReduction;
                consumedStatusIds.Add(defending.Id);
                statusEvents.Add(new StatusEvent(StatusEventKind.Consumed, defending with { Consumed = true },
                    $"turned aside {defendReduction} damage from a blow that landed"));
            }

            healthAfter = Math.Max(0, healthBefore - damage);
            died = healthAfter <= 0;
            injury = DetermineInjury(target, weapon, damage, healthBefore, healthAfter, died);

            if (!died && ability?.EffectKind == AbilityEffectKind.StrikeAndOffBalance)
            {
                statusApplied = StatusEffectKind.OffBalance.ToString();
            }
            else if (!died && ability?.EffectKind == AbilityEffectKind.StrikeAndStun)
            {
                statusApplied = StatusEffectKind.Stunned.ToString();
            }
        }

        var fearChanges = new List<FearChange>();

        // ---- Build the new state. A miss can still change state: a consumed status, a spent charge, a
        // rejected offer. Nothing is assumed unchanged just because no damage was dealt. ----
        var after = state.WithoutStatuses(consumedStatusIds);
        var changed = consumedStatusIds.Count > 0;

        if (selfExposure.Occurred)
        {
            after = ApplyExposure(after, selfExposure, $"{attacker.Name} broke cover to strike", statusEvents);
            changed = true;
        }

        if (coverAfter is not null)
        {
            after = after.WithCover(coverAfter);
            changed = true;
        }

        if (hit)
        {
            var updatedTarget = target with
            {
                Health = healthAfter,
                // Death is recorded as an explicit disposition change, not left to derive from health alone, so
                // the authoritative state names it and an orchestration-layer disposition trace can key off it.
                Disposition = died ? CharacterDisposition.Dead : target.Disposition,
                Injuries = injury is null ? target.Injuries : target.Injuries.Add(injury)
            };
            after = after.WithCharacter(updatedTarget);
            changed = true;
        }

        // ---- Morale. A blow that frightens does so exactly once, whatever combination of reasons applies:
        // a critical hit that ALSO took a quarter of the target's health is one terrifying blow, not two.
        // A dead target is never frightened; there is nobody left to frighten. ----
        if (hit && !died)
        {
            var large = damage >= FearRules.LargeHitThreshold(target.MaxHealth) && damage > 0;
            var critical = quality == AttackQuality.Critical;
            if (critical || large)
            {
                var (cause, detail) = (critical, large) switch
                {
                    (true, true) => (FearChangeCause.CriticalAndLargeHitReceived,
                        $"a critical blow from {attacker.Name} that also took at least a quarter of their health"),
                    (true, false) => (FearChangeCause.CriticalHitReceived,
                        $"a critical blow from {attacker.Name}"),
                    _ => (FearChangeCause.LargeHitReceived,
                        $"one blow from {attacker.Name} taking at least a quarter of their health")
                };

                after = ApplyFear(after, target.Id, +1, cause, detail, attacker.Id, action.ActionType,
                    statusEvents, fearChanges);
                changed = true;
            }
        }

        // Landing a critical blow steadies the one who landed it, whether or not it killed. This is the
        // counterplay to fear: a frightened character can fight their way back out of it.
        if (hit && quality == AttackQuality.Critical)
        {
            var attackerBefore = after.FindById(attacker.Id);
            if (attackerBefore is { Fear: > FearRules.Minimum })
            {
                after = ApplyFear(after, attacker.Id, -1, FearChangeCause.LandedCriticalHit,
                    $"landed a critical blow on {target.Name}", target.Id, action.ActionType,
                    statusEvents, fearChanges);
                changed = true;
            }
        }

        // The ability's charge is spent on an accepted use, hit or miss. A miss still costs the chance: that is
        // what makes a once-per-encounter trick a real decision rather than a free re-roll.
        if (ability is not null && !ability.IsUnlimited)
        {
            var holder = after.FindById(attacker.Id)!;
            if (holder.FindAbility(ability.Id) is { RemainingUses: > 0 } held)
            {
                after = after.WithCharacter(holder.WithAbilityCharges(ability.Id, held.RemainingUses - 1));
                changed = true;
            }
        }

        // A landed blow may leave a lasting status on the target, depending on the ability that delivered it.
        // Never stacked: an existing instance of the same status stands. `statusApplied` is only set on a hit
        // that did not kill, so reaching either branch already implies both.
        if (statusApplied is not null && ability is not null)
        {
            if (ability.EffectKind == AbilityEffectKind.StrikeAndOffBalance
                && after.StatusOn(target.Id, StatusEffectKind.OffBalance) is null)
            {
                var offBalance = NewStatus(StatusEffectKind.OffBalance, attacker.Id, target.Id,
                    -ability.EffectValue, StatusExpiryRule.EndOfTargetNextTurn, ability.Id);
                after = after.WithStatus(offBalance);
                statusEvents.Add(new StatusEvent(StatusEventKind.Applied, offBalance,
                    $"{attacker.Name} left {target.Name} off balance with {ability.Name}"));
                changed = true;
            }
            else if (ability.EffectKind == AbilityEffectKind.StrikeAndStun
                     && after.StatusOn(target.Id, StatusEffectKind.Stunned) is null)
            {
                // Stunned never expires on the turn clock (WhileConditionHolds): BeginActorTurn consumes it in
                // the same breath that it forces the target's skipped turn, so a stun costs exactly one turn.
                var stunned = NewStatus(StatusEffectKind.Stunned, attacker.Id, target.Id,
                    0, StatusExpiryRule.WhileConditionHolds, ability.Id);
                after = after.WithStatus(stunned);
                statusEvents.Add(new StatusEvent(StatusEventKind.Applied, stunned,
                    $"{attacker.Name} left {target.Name} reeling and stunned with {ability.Name}"));
                changed = true;
            }
        }

        // A struck offerer's offer is rejected when the blow came from the recipient's side; and an offerer
        // who strikes while their own offer stands has abandoned it.
        var (afterOffers, offersChanged) = ApplyHostility(after, attacker,
            redirected ? [intendedTarget.Id, target.Id] : [target.Id], offerTransitions);
        after = afterOffers;
        changed |= offersChanged;

        // A dead character sustains nothing: their statuses fall away, any offer involving them can no longer
        // be enforced, and what they carried becomes lootable on their body.
        Container? corpse = null;
        if (died)
        {
            var (purged, purgeChanged) = PurgeForInactive(after, target.Id, "was killed", statusEvents, offerTransitions, demandTransitions);
            after = purged;
            changed |= purgeChanged;

            var fallen = after.FindById(target.Id)!;
            if (fallen.Inventory.Length > 0)
            {
                corpse = new Container
                {
                    Id = $"corpse-{fallen.Id}",
                    Name = $"{fallen.Name}'s body",
                    Description = $"The fallen body of {fallen.Name}, its belongings within reach.",
                    IsOpen = true,
                    IsCorpse = true,
                    Contents = fallen.Inventory
                };

                after = after
                    .WithCharacter(fallen with { Inventory = [] }) with
                {
                    Room = after.Room with { Objects = after.Room.Objects.Add(corpse) }
                };
            }
        }

        if (changed)
        {
            after = after with { Version = state.Version + 1 };
        }

        var outcome = new AttackOutcome
        {
            AttackerId = attacker.Id,
            AttackerName = attacker.Name,
            TargetId = target.Id,
            TargetName = target.Name,
            WeaponName = weapon.Name,
            WeaponDamage = weapon.Damage,
            TargetArmour = target.Armour,
            HitRoll = hitRoll,
            HitChance = coveredEffectiveHitChance,
            BaseHitChance = baseHitChance,
            HitModifiers = drawModifiers,
            Hit = hit,
            GlancingRoll = qualityRoll,
            GlancingChance = _combatRules.GlancingBlowChance,
            CriticalChance = _combatRules.CriticalHitChance,
            Quality = quality,
            BaseDamage = baseDamage,
            DamageDealt = damage,
            DefendReduction = defendReduction,
            TargetHealthBefore = healthBefore,
            TargetHealthAfter = healthAfter,
            TargetMaxHealth = target.MaxHealth,
            TargetDied = died,
            InjuryInflicted = injury?.Description,
            CorpseContainerName = corpse?.Name,
            DroppedItems = corpse is null ? [] : [.. corpse.Contents.Select(i => i.DisplayName)],
            ViaAbilityId = ability?.Id,
            ViaAbilityName = ability?.Name,
            IgnoredArmour = ignoresArmour,
            StatusApplied = statusApplied,
            StatusAppliedNote = statusApplied switch
            {
                nameof(StatusEffectKind.OffBalance) =>
                    $"The blow left {target.Name} off balance — their next attack is markedly less likely to land.",
                nameof(StatusEffectKind.Stunned) =>
                    $"The blow left {target.Name} stunned and reeling — they will lose their entire next turn.",
                _ => null
            },
            IntendedTargetId = intendedTarget.Id,
            IntendedTargetName = intendedTarget.Name,
            Redirected = redirected,
            FearChanges = fearChanges,
            CoverId = cover?.Id,
            CoverName = cover?.Name,
            PreCoverHitChance = cover is null ? null : preCoverEffectiveHitChance,
            CoverHitChanceModifier = cover?.HitChanceModifier,
            InterceptedByCover = interceptedByCover,
            CoverDurabilityBefore = cover?.CurrentDurability,
            // Falls back to the unchanged value on a direct hit or an ordinary miss, so "before" and "after"
            // are always both present or both absent together whenever cover was involved at all.
            CoverDurabilityAfter = coverAfter?.CurrentDurability ?? cover?.CurrentDurability,
            CoverDestroyed = coverDestroyedNow
        };

        return EngineResult.Accept(action, state, after, outcome, draws, statusEvents, offerTransitions, fearChanges,
            demandTransitions: demandTransitions);
    }

    /// <summary>
    /// The fixed order hit-chance modifiers are applied in. Fixed, not discovered, so two runs with the same
    /// statuses reach the same effective chance in the same recorded order.
    /// </summary>
    private static readonly StatusEffectKind[] HitChanceModifierOrder =
        [StatusEffectKind.Rallied, StatusEffectKind.OffBalance];

    private EngineResult ResolveUseItem(UseItemAction action)
    {
        var state = _state;

        var actor = state.Resolve(action.ActorRef);
        if (actor is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ActorRef}' in the room.");
        }

        if (!actor.CanAct)
        {
            return RejectInactiveActor(action, state, actor);
        }

        // v0.1 only supports using an item on oneself.
        if (!string.IsNullOrWhiteSpace(action.TargetRef))
        {
            var target = state.Resolve(action.TargetRef);
            if (target is null)
            {
                return EngineResult.Reject(action, state, EngineRejectionReason.UnknownTarget,
                    $"There is no character called '{action.TargetRef}' in the room.");
            }

            if (!Same(target.Id, actor.Id))
            {
                return EngineResult.Reject(action, state, EngineRejectionReason.ItemTargetNotSupported,
                    "The engine can only apply an item to the character using it. Using items on other characters is not supported.");
            }
        }

        var (item, ambiguousItem) = actor.ResolveItem(action.ItemRef);
        if (ambiguousItem)
        {
            return RejectAmbiguousItem(action, state, actor.Inventory, action.ItemRef, $"that {actor.Name} is carrying");
        }

        if (item is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ItemNotPossessed,
                $"{actor.Name} is not carrying '{action.ItemRef}'.");
        }

        // A focus item recharges a spent ability rather than healing, and is NOT consumed — focusing is a
        // repeatable turn whose only cost is the turn itself.
        if (item.IsFocusItem)
        {
            return ResolveFocusItem(action, state, actor, item);
        }

        if (!item.IsHealingItem)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ItemHasNoSupportedEffect,
                $"The engine has no supported effect for '{item.DisplayName}'.");
        }

        var healing = item.HealingAmount!.Value;
        var healthBefore = actor.Health;
        var healthAfter = Math.Min(actor.MaxHealth, healthBefore + healing);

        var updatedActor = actor with
        {
            Health = healthAfter,
            Inventory = RemoveFirst(actor, item)
        };

        var after = state.WithCharacter(updatedActor) with { Version = state.Version + 1 };

        // Consuming an item that was promised in a pending surrender offer makes that offer unenforceable.
        var transitions = new List<OfferTransition>();
        after = InvalidateOffersPromising(after, actor.Id, item.Id, "a promised item was consumed", transitions);

        var outcome = new HealOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            ItemName = item.DisplayName,
            HealingAmount = healing,
            HealthBefore = healthBefore,
            HealthAfter = healthAfter,
            MaxHealth = actor.MaxHealth
        };

        return EngineResult.Accept(action, state, after, outcome, offerTransitions: transitions);
    }

    /// <summary>
    /// Resolves focusing through a focus item (v0.11): the actor spends the whole turn to restore one spent
    /// charge of their own primary limited ability. No randomness. The item is not consumed, so it can be
    /// used again on a later turn. Refused — with the turn NOT spent — when the actor has no limited ability
    /// currently below its maximum, exactly as healing at full health is refused rather than wasted.
    /// </summary>
    private EngineResult ResolveFocusItem(UseItemAction action, GameState state, Character actor, InventoryItem item)
    {
        // "Primary" is the first limited ability, in the character's own authored order, that has a spent
        // charge to restore. Unlimited abilities (Defend, Guard Ally) have nothing to recharge and are skipped.
        var depleted = actor.Abilities.FirstOrDefault(a => a.MaxUses is { } max && a.RemainingUses < max);
        if (depleted is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.NoDepletedAbilityToRestore,
                $"{actor.Name} has no spent power for the {item.DisplayName} to rekindle right now.");
        }

        var before = depleted.RemainingUses ?? 0;
        var restored = Math.Min(depleted.MaxUses!.Value, before + 1);
        var after = state.WithCharacter(actor.WithAbilityCharges(depleted.AbilityId, restored))
            with { Version = state.Version + 1 };

        var outcome = new FocusOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            ItemName = item.DisplayName,
            AbilityId = depleted.AbilityId,
            AbilityName = depleted.Name,
            UsesBefore = before,
            UsesAfter = restored,
            MaxUses = depleted.MaxUses.Value
        };

        return EngineResult.Accept(action, state, after, outcome);
    }

    // ===============================================================================================
    // Negotiated surrender
    // ===============================================================================================

    /// <summary>
    /// Creates a pending surrender offer. No randomness. It consumes the offerer's turn and changes nothing
    /// else: no asset moves, the offerer is not disarmed, their disposition is untouched, and they remain an
    /// active, targetable combatant. Only the named recipient may accept it, and only on their own turn.
    /// </summary>
    private EngineResult ResolveOfferSurrender(OfferSurrenderAction action)
    {
        var state = _state;

        var offerer = state.Resolve(action.OffererRef);
        if (offerer is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.OffererRef}' in the room.");
        }

        if (!offerer.CanAct)
        {
            return RejectInactiveActor(action, state, offerer);
        }

        var recipient = state.Resolve(action.RecipientRef);
        if (recipient is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownRecipient,
                $"There is no character called '{action.RecipientRef}' in the room to offer terms to.");
        }

        if (Same(recipient.Id, offerer.Id))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.RecipientIsSelf,
                $"{offerer.Name} cannot offer terms to themselves.");
        }

        if (!recipient.CanAct)
        {
            return RejectUnavailableTarget(action, state, recipient, "offer terms to");
        }

        if (offerer.IsAllyOf(recipient))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.RecipientIsNotAnOpponent,
                $"{recipient.Name} fights on {offerer.Name}'s own side. Terms of surrender are offered to an opponent.");
        }

        if (state.PendingOfferFrom(offerer.Id) is { } existing)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.DuplicatePendingOffer,
                $"{offerer.Name} has already offered terms to {state.FindById(existing.RecipientId)?.Name ?? existing.RecipientId} " +
                "and that offer has not been answered yet.");
        }

        // Every promised item must be an ordinary item the offerer owns right now, named once.
        var offeredItems = new List<InventoryItem>();
        foreach (var itemRef in action.OfferedItemRefs)
        {
            if (NamesEquippedWeapon(offerer, itemRef))
            {
                return EngineResult.Reject(action, state, EngineRejectionReason.OfferedItemNotTransferable,
                    $"{offerer.Name}'s {offerer.Weapon!.Name} is the weapon in their hand, not an ordinary item. " +
                    "Giving it up is a weapon forfeiture, promised as part of the terms rather than listed as an item.");
            }

            var (item, ambiguousItem) = offerer.ResolveItem(itemRef);
            if (ambiguousItem)
            {
                return RejectAmbiguousItem(action, state, offerer.Inventory, itemRef,
                    $"that {offerer.Name} is carrying, so the terms do not say what is being promised");
            }

            if (item is null)
            {
                return EngineResult.Reject(action, state, EngineRejectionReason.OfferedItemNotOwned,
                    $"{offerer.Name} is not carrying '{itemRef}' and cannot promise it.");
            }

            if (offeredItems.Any(i => Same(i.Id, item.Id)))
            {
                return EngineResult.Reject(action, state, EngineRejectionReason.OfferedItemNotOwned,
                    $"{offerer.Name} cannot promise the {item.DisplayName} twice in the same offer.");
            }

            offeredItems.Add(item);
        }

        if (action.ForfeitWeapon && offerer.Weapon is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.OfferedWeaponNotHeld,
                $"{offerer.Name} holds no weapon to give up.");
        }

        // Terms must promise SOMETHING concrete: one or more carried items, the weapon in hand, or both.
        // Either concession alone is enough — v0.7 additionally required a weapon-only offer to come from a
        // character carrying nothing else at all, to close a parsing artefact where a demand for somebody
        // ELSE's surrender (which has no representable form in this action) collapsed into "no items,
        // forfeit_weapon true" and got recorded as the speaker's own surrender. A live run made four such
        // offers, one of them a WINNING character saying "I offer him his life if he throws down his sabre".
        //
        // v0.10 lifts that extra narrowing: a genuine weapon-only surrender ("I hold my sabre out by the
        // flat and offer to lay it down if you spare me") must work whatever else the offerer carries, and
        // the misread-demand shape it was guarding against is now closed at the guidance layer instead — the
        // rulebook card and character prompt teach that a demand for someone ELSE's surrender is only speech,
        // and the one-mechanical-deed-per-turn contract stops a struck-out demand from being read as this
        // action at all. Requiring an unrelated engine gate for a language-understanding problem was also
        // the wrong layer for it, and it re-created the very trap the v0.7 comment above once described:
        // a character down to their weapon alone still has exactly one thing left to give.
        if (offeredItems.Count == 0 && !action.ForfeitWeapon)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.OfferHasNoConcession,
                $"{offerer.Name} offered nothing concrete. A plea to be spared is only words: terms of " +
                "surrender must promise something enforceable — a carried item, the weapon in hand, or both.");
        }

        var offer = new SurrenderOffer
        {
            Id = $"offer-{++_offerCounter}",
            OffererId = offerer.Id,
            RecipientId = recipient.Id,
            OfferedItemIds = [.. offeredItems.Select(i => i.Id)],
            ForfeitWeapon = action.ForfeitWeapon,
            CreatedRound = CurrentRound,
            CreatedTurn = CurrentTurn,
            AssociatedSpeechEventId = action.AssociatedSpeechEventId,
            State = SurrenderOfferState.Pending
        };

        var after = state.WithOffer(offer) with { Version = state.Version + 1 };

        var outcome = new OfferSurrenderOutcome
        {
            OfferId = offer.Id,
            OffererId = offerer.Id,
            OffererName = offerer.Name,
            RecipientId = recipient.Id,
            RecipientName = recipient.Name,
            OfferedItemNames = [.. offeredItems.Select(i => i.DisplayName)],
            ForfeitWeapon = action.ForfeitWeapon,
            WeaponName = action.ForfeitWeapon ? offerer.Weapon!.Name : null,
            AssociatedSpeechEventId = action.AssociatedSpeechEventId
        };

        return EngineResult.Accept(action, state, after, outcome,
            offerTransitions: [new OfferTransition(offer, SurrenderOfferState.Pending, SurrenderOfferState.Pending, "offer made")]);
    }

    /// <summary>
    /// Records a pressure-only demand that one named opponent give up the fight — the mirror of an offer,
    /// pointed the other way. It moves nothing, disarms nobody and changes no disposition: the DEMANDER stays
    /// armed, active and targetable, and the TARGET keeps everything. The demand is surfaced to the target on
    /// their next turn (see <see cref="Prompts.WorldStateFormatter"/>) and lapses at the end of it. No
    /// randomness. A character may have only one demand pending at a time, exactly as with an offer.
    /// </summary>
    private EngineResult ResolveDemandSurrender(DemandSurrenderAction action)
    {
        var state = _state;

        var demander = state.Resolve(action.DemanderRef);
        if (demander is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.DemanderRef}' in the room.");
        }

        if (!demander.CanAct)
        {
            return RejectInactiveActor(action, state, demander);
        }

        var target = state.Resolve(action.TargetRef);
        if (target is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownRecipient,
                $"There is no character called '{action.TargetRef}' in the room to demand surrender of.");
        }

        if (Same(target.Id, demander.Id))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.RecipientIsSelf,
                $"{demander.Name} cannot demand their own surrender.");
        }

        if (!target.CanAct)
        {
            return RejectUnavailableTarget(action, state, target, "demand surrender of");
        }

        if (demander.IsAllyOf(target))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.RecipientIsNotAnOpponent,
                $"{target.Name} fights on {demander.Name}'s own side. A surrender is demanded of an opponent, not an ally.");
        }

        if (state.PendingDemandFrom(demander.Id) is { } existing)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.DuplicatePendingOffer,
                $"{demander.Name} has already demanded that {state.FindById(existing.TargetId)?.Name ?? existing.TargetId} " +
                "yield, and that demand has not been answered yet.");
        }

        var demand = new SurrenderDemand
        {
            Id = $"demand-{++_demandCounter}",
            DemanderId = demander.Id,
            TargetId = target.Id,
            CreatedRound = CurrentRound,
            CreatedTurn = CurrentTurn,
            AssociatedSpeechEventId = action.AssociatedSpeechEventId,
            State = SurrenderDemandState.Pending
        };

        var after = state.WithDemand(demand) with { Version = state.Version + 1 };

        var outcome = new DemandSurrenderOutcome
        {
            DemandId = demand.Id,
            DemanderId = demander.Id,
            DemanderName = demander.Name,
            TargetId = target.Id,
            TargetName = target.Name,
            AssociatedSpeechEventId = action.AssociatedSpeechEventId
        };

        return EngineResult.Accept(action, state, after, outcome,
            demandTransitions: [new DemandTransition(demand, SurrenderDemandState.Pending, SurrenderDemandState.Pending, "demand made")]);
    }

    /// <summary>
    /// Accepts a pending surrender offer addressed to the current actor, enforcing the whole agreement in one
    /// atomic state change: every promised item moves, the offerer is disarmed, the promised weapon lands on
    /// the room's floor, the offer becomes Accepted, a durable agreement is recorded, and the offerer's
    /// disposition becomes Surrendered with their combat statuses swept away. No randomness, and no partial
    /// acceptance: if any promised asset is no longer there, nothing moves at all.
    /// </summary>
    private EngineResult ResolveAcceptSurrender(AcceptSurrenderAction action)
    {
        var state = _state;

        var recipient = state.Resolve(action.RecipientRef);
        if (recipient is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.RecipientRef}' in the room.");
        }

        if (!recipient.CanAct)
        {
            return RejectInactiveActor(action, state, recipient);
        }

        var offer = state.FindOffer(action.OfferRef);
        if (offer is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownOffer,
                $"There is no offer of surrender called '{action.OfferRef}' on the table.");
        }

        if (!Same(offer.RecipientId, recipient.Id))
        {
            var named = state.FindById(offer.RecipientId)?.Name ?? offer.RecipientId;
            return EngineResult.Reject(action, state, EngineRejectionReason.OfferNotAddressedToActor,
                $"Those terms were offered to {named}, not to {recipient.Name}. Only {named} may take them up.");
        }

        if (!offer.IsPending)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.OfferNoLongerPending,
                $"That offer is no longer open ({offer.State.ToString().ToLowerInvariant()}).");
        }

        var offerer = state.FindById(offer.OffererId);
        if (offerer is null || !offerer.CanAct || !offerer.IsPresent)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.OffererNotAvailable,
                $"{offerer?.Name ?? offer.OffererId} is no longer in a position to give up the fight on those terms.");
        }

        // Every promised asset must still be the offerer's. Acceptance is all-or-nothing.
        var promised = new List<InventoryItem>();
        foreach (var itemId in offer.OfferedItemIds)
        {
            var item = offerer.Inventory.FirstOrDefault(i => Same(i.Id, itemId));
            if (item is null)
            {
                return EngineResult.Reject(action, state, EngineRejectionReason.PromisedAssetNoLongerAvailable,
                    $"{offerer.Name} no longer has everything they promised, so the terms cannot be honoured. " +
                    "Nothing changes hands.");
            }

            promised.Add(item);
        }

        if (offer.ForfeitWeapon && offerer.Weapon is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.PromisedAssetNoLongerAvailable,
                $"{offerer.Name} no longer holds the weapon they promised to give up, so the terms cannot be honoured. " +
                "Nothing changes hands.");
        }

        var statusEvents = new List<StatusEvent>();
        var transitions = new List<OfferTransition>();

        // ---- One atomic application of the whole agreement. ----
        var strippedOfferer = offerer;
        var receivingRecipient = recipient;
        foreach (var item in promised)
        {
            strippedOfferer = strippedOfferer with { Inventory = RemoveFirst(strippedOfferer, item) };
            receivingRecipient = receivingRecipient with { Inventory = receivingRecipient.Inventory.Add(item) };
        }

        // A character who has yielded does not stand there with a raised weapon: acceptance disarms them. The
        // weapon becomes an inert ground item keeping its own stable id, so no second weapon entity is created
        // and a looter takes a trophy rather than an equipped weapon (v0.7 has no equipping).
        var forfeitedWeapon = strippedOfferer.Weapon;
        var room = state.Room;
        Container? ground = null;
        if (forfeitedWeapon is not null)
        {
            strippedOfferer = strippedOfferer with { Weapon = null };
            (room, ground) = PlaceOnGround(room, forfeitedWeapon.AsForfeitedItem());
        }

        strippedOfferer = strippedOfferer with { Disposition = CharacterDisposition.Surrendered };

        var agreement = new SurrenderAgreement
        {
            Id = $"agreement-{++_agreementCounter}",
            OfferId = offer.Id,
            OffererId = offerer.Id,
            AcceptedById = recipient.Id,
            TransferredItemIds = [.. promised.Select(i => i.Id)],
            ForfeitedWeaponId = offer.ForfeitWeapon ? forfeitedWeapon?.Id : null,
            AcceptedRound = CurrentRound,
            AcceptedTurn = CurrentTurn,
            AssociatedSpeechEventId = offer.AssociatedSpeechEventId
        };

        var after = state
            .WithCharacter(strippedOfferer)
            .WithCharacter(receivingRecipient) with { Room = room };

        after = TransitionOffer(after, offer, SurrenderOfferState.Accepted,
            $"accepted by {recipient.Name}", CurrentRound, CurrentTurn, transitions);
        after = after.WithAgreement(agreement);

        // Yielding ends this character's fight: their combat statuses fall away and any other offer or demand
        // they were party to can no longer be answered.
        var demandTransitions = new List<DemandTransition>();
        var (purged, _) = PurgeForInactive(after, offerer.Id, "surrendered", statusEvents, transitions, demandTransitions, exceptOfferId: offer.Id);
        after = purged with { Version = state.Version + 1 };

        var outcome = new AcceptSurrenderOutcome
        {
            OfferId = offer.Id,
            AgreementId = agreement.Id,
            OffererId = offerer.Id,
            OffererName = offerer.Name,
            AccepterId = recipient.Id,
            AccepterName = recipient.Name,
            TransferredItemNames = [.. promised.Select(i => i.DisplayName)],
            ForfeitedWeaponName = forfeitedWeapon?.Name,
            GroundContainerName = ground?.Name,
            WeaponWasPromised = offer.ForfeitWeapon,
            AssociatedSpeechEventId = offer.AssociatedSpeechEventId
        };

        return EngineResult.Accept(action, state, after, outcome,
            statusEvents: statusEvents, offerTransitions: transitions, demandTransitions: demandTransitions);
    }

    // ===============================================================================================
    // Abilities
    // ===============================================================================================

    /// <summary>
    /// Resolves one ability use. Validation runs first and completely — target eligibility, charges, whether
    /// the effect would do anything at all — so a rejected use spends no charge, consumes no turn and, for an
    /// ability that strikes, draws no dice. Only then is the ability dispatched to its concrete handler.
    /// </summary>
    private EngineResult ResolveUseAbility(UseAbilityAction action)
    {
        var state = _state;

        var actor = state.Resolve(action.ActorRef);
        if (actor is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ActorRef}' in the room.");
        }

        if (!actor.CanAct)
        {
            return RejectInactiveActor(action, state, actor);
        }

        var definition = AbilityCatalog.Resolve(action.AbilityRef);
        if (definition is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownAbility,
                $"{actor.Name} knows no such trick, prayer or technique.");
        }

        // Defend is a plain combat option everybody has, so it needs no entry on a character's ability list.
        var held = actor.FindAbility(definition.Id);
        if (held is null && definition.EffectKind != AbilityEffectKind.Defend)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.AbilityNotHeld,
                $"{actor.Name} has never learned {definition.InWorldName}.");
        }

        if (held is not null && !held.HasChargeLeft)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.AbilityHasNoUsesLeft,
                $"{actor.Name} has already managed {definition.InWorldName} once here, and has nothing left in them for it again.");
        }

        var resolution = ResolveAbilityTarget(action, state, actor, definition);
        if (resolution.Rejection is { } rejection)
        {
            return rejection;
        }

        var target = resolution.Target!;

        return definition.EffectKind switch
        {
            AbilityEffectKind.GuardAlly => ResolveGuardAlly(action, state, actor, target, definition, held),
            AbilityEffectKind.Heal => ResolveHealingAbility(action, state, actor, target, definition, held),
            AbilityEffectKind.Rally => ResolveRally(action, state, actor, target, definition, held),
            AbilityEffectKind.Defend => ResolveDefend(action, state, actor, definition),
            AbilityEffectKind.StrikeAndOffBalance => ResolveAbilityStrike(action, state, actor, target, definition),
            AbilityEffectKind.StrikeAndStun => ResolveAbilityStrike(action, state, actor, target, definition),
            AbilityEffectKind.Firebolt => ResolveFirebolt(action, state, actor, target, definition),
            _ => EngineResult.Reject(action, state, EngineRejectionReason.UnsupportedAction,
                $"The engine has no handler for the {definition.Name} effect.")
        };
    }

    /// <summary>The outcome of resolving an ability's target: the character it applies to, or the refusal.</summary>
    private readonly record struct AbilityTargetResolution(Character? Target, EngineResult? Rejection);

    /// <summary>
    /// Resolves and validates an ability's target against its <see cref="AbilityTargetRule"/>. Each rule
    /// refuses with its own reason so the Dungeon Master can explain the refusal in-world, and no rule is
    /// ever satisfied by a character who is dead, has surrendered or has escaped.
    /// </summary>
    private static AbilityTargetResolution ResolveAbilityTarget(
        UseAbilityAction action, GameState state, Character actor, AbilityDefinition definition)
    {
        EngineResult Reject(EngineRejectionReason reason, string message) =>
            EngineResult.Reject(action, state, reason, message);

        if (definition.TargetRule == AbilityTargetRule.SelfOnly)
        {
            if (!string.IsNullOrWhiteSpace(action.TargetRef))
            {
                var named = state.Resolve(action.TargetRef);
                if (named is null || !Same(named.Id, actor.Id))
                {
                    return new AbilityTargetResolution(null, Reject(EngineRejectionReason.AbilityTargetNotAllowed,
                        $"{definition.InWorldName} is something {actor.Name} does for themselves; it cannot be aimed at anyone else."));
                }
            }

            return new AbilityTargetResolution(actor, null);
        }

        if (string.IsNullOrWhiteSpace(action.TargetRef))
        {
            if (definition.TargetRule == AbilityTargetRule.SelfOrAlly)
            {
                return new AbilityTargetResolution(actor, null);
            }

            return new AbilityTargetResolution(null, Reject(EngineRejectionReason.AbilityTargetRequired,
                $"{definition.InWorldName} has to be aimed at someone, and nobody was named."));
        }

        var target = state.Resolve(action.TargetRef);
        if (target is null)
        {
            return new AbilityTargetResolution(null, Reject(EngineRejectionReason.UnknownTarget,
                $"There is no character called '{action.TargetRef}' in the room."));
        }

        var isSelf = Same(target.Id, actor.Id);

        switch (definition.TargetRule)
        {
            case AbilityTargetRule.OtherAlly when isSelf:
                return new AbilityTargetResolution(null, Reject(EngineRejectionReason.AbilityTargetIsSelf,
                    $"{definition.InWorldName} is for a companion, not for {actor.Name} themselves."));

            case AbilityTargetRule.Opponent when isSelf:
                return new AbilityTargetResolution(null, Reject(EngineRejectionReason.AbilityTargetIsSelf,
                    $"{actor.Name} cannot turn {definition.InWorldName} on themselves."));

            case AbilityTargetRule.OtherAlly or AbilityTargetRule.SelfOrAlly when !isSelf && !actor.IsAllyOf(target):
                return new AbilityTargetResolution(null, Reject(EngineRejectionReason.AbilityTargetNotAlly,
                    $"{target.Name} does not fight at {actor.Name}'s side, and {definition.InWorldName} is for a companion."));

            case AbilityTargetRule.Opponent when actor.IsAllyOf(target):
                return new AbilityTargetResolution(null, Reject(EngineRejectionReason.AbilityTargetNotOpponent,
                    $"{target.Name} fights on {actor.Name}'s own side; {definition.InWorldName} is meant for an enemy."));
        }

        if (!target.CanAct || !target.IsPresent)
        {
            var verb = definition.TargetRule == AbilityTargetRule.Opponent ? "use that on" : "reach";
            return new AbilityTargetResolution(null, RejectUnavailableTarget(action, state, target, verb));
        }

        return new AbilityTargetResolution(target, null);
    }

    /// <summary>
    /// Creates the linked Guarding/Guarded relationship. No randomness: the redirection itself happens later,
    /// inside an ordinary attack, and adds no draw.
    /// </summary>
    private EngineResult ResolveGuardAlly(
        UseAbilityAction action, GameState state, Character guardian, Character ally,
        AbilityDefinition definition, CharacterAbility? held)
    {
        if (state.StatusOn(guardian.Id, StatusEffectKind.Guarding) is not null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.AbilityAlreadyActive,
                $"{guardian.Name} is already standing over a companion and cannot guard two at once.");
        }

        if (state.StatusOn(ally.Id, StatusEffectKind.Guarded) is { } existing)
        {
            var other = state.FindById(existing.SourceCharacterId)?.Name ?? existing.SourceCharacterId;
            return EngineResult.Reject(action, state, EngineRejectionReason.TargetAlreadyGuarded,
                $"{other} is already standing over {ally.Name}; there is no room for a second guard.");
        }

        var relationshipId = $"guard-{++_relationshipCounter}";
        var guarding = NewStatus(StatusEffectKind.Guarding, guardian.Id, guardian.Id, 0,
            StatusExpiryRule.StartOfSourceNextTurn, definition.Id, relationshipId);
        var guardedStatus = NewStatus(StatusEffectKind.Guarded, guardian.Id, ally.Id, 0,
            StatusExpiryRule.StartOfSourceNextTurn, definition.Id, relationshipId);

        // Standing over a companion means leaving whatever the guardian was sheltering behind (v0.9).
        var selfExposure = ExposeIfInCover(state, guardian.Id);

        var after = SpendCharge(state.WithStatus(guarding).WithStatus(guardedStatus), guardian.Id, definition, held);

        var statusEvents = new List<StatusEvent>
        {
            new(StatusEventKind.Applied, guarding, $"{guardian.Name} took up a guard over {ally.Name}"),
            new(StatusEventKind.Applied, guardedStatus, $"{ally.Name} is guarded by {guardian.Name}")
        };
        after = ApplyExposure(after, selfExposure, $"{guardian.Name} broke cover to stand over {ally.Name}", statusEvents);

        after = after with { Version = state.Version + 1 };

        var outcome = new GuardAllyOutcome
        {
            GuardianId = guardian.Id,
            GuardianName = guardian.Name,
            AllyId = ally.Id,
            AllyName = ally.Name,
            RelationshipId = relationshipId
        };

        return EngineResult.Accept(action, state, after, outcome, statusEvents: statusEvents);
    }

    /// <summary>
    /// Restores a fixed amount of health to the caster or an ally. No randomness. Refused, with the charge
    /// left untouched, on a target who is already whole — a wasted prayer is not a spent one.
    /// </summary>
    private EngineResult ResolveHealingAbility(
        UseAbilityAction action, GameState state, Character caster, Character target,
        AbilityDefinition definition, CharacterAbility? held)
    {
        if (target.Health >= target.MaxHealth)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.TargetAlreadyAtFullHealth,
                $"{target.Name} bears no wound for {definition.InWorldName} to close.");
        }

        var healthBefore = target.Health;
        var healthAfter = Math.Min(target.MaxHealth, healthBefore + definition.EffectValue);

        var after = state.WithCharacter(target with { Health = healthAfter });
        after = SpendCharge(after, caster.Id, definition, held);

        // Praying over oneself needs no reach; praying over a companion does — that breaks the caster's own
        // cover (v0.9). A prayer worked on oneself while sheltering stays sheltered.
        var statusEvents = new List<StatusEvent>();
        if (!Same(caster.Id, target.Id))
        {
            after = ApplyExposure(after, ExposeIfInCover(state, caster.Id),
                $"{caster.Name} broke cover to lay hands on {target.Name}", statusEvents);
        }

        after = after with { Version = state.Version + 1 };

        var remaining = after.FindById(caster.Id)?.FindAbility(definition.Id)?.RemainingUses ?? 0;

        var outcome = new HealingPrayerOutcome
        {
            CasterId = caster.Id,
            CasterName = caster.Name,
            TargetId = target.Id,
            TargetName = target.Name,
            AbilityName = definition.Name,
            HealingAmount = definition.EffectValue,
            HealthBefore = healthBefore,
            HealthAfter = healthAfter,
            MaxHealth = target.MaxHealth,
            RemainingUses = remaining
        };

        return EngineResult.Accept(action, state, after, outcome, statusEvents: statusEvents);
    }

    /// <summary>Applies <see cref="StatusEffectKind.Rallied"/> to an ally. No randomness when applied.</summary>
    private EngineResult ResolveRally(
        UseAbilityAction action, GameState state, Character commander, Character ally,
        AbilityDefinition definition, CharacterAbility? held)
    {
        if (state.StatusOn(ally.Id, StatusEffectKind.Rallied) is not null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.AbilityAlreadyActive,
                $"{ally.Name} is already steadied and cannot be steadied twice over.");
        }

        var status = NewStatus(StatusEffectKind.Rallied, commander.Id, ally.Id, definition.EffectValue,
            StatusExpiryRule.EndOfTargetNextTurn, definition.Id);

        var statusEvents = new List<StatusEvent>
        {
            new(StatusEventKind.Applied, status, $"{commander.Name} rallied {ally.Name}")
        };

        var after = SpendCharge(state.WithStatus(status), commander.Id, definition, held);

        // v0.8: a barked order steadies the nerve as well as the arm. The hit-chance effect above is exactly
        // what it always was; this is added alongside it, so Rally Grunt keeps every bit of its old behaviour.
        var fearChanges = new List<FearChange>();
        var wasScared = ally.IsScared;
        after = ApplyFear(after, ally.Id, -1, FearChangeCause.RallyGrunt,
            $"{commander.Name} barked an order that steadied them", commander.Id, action.ActionType,
            statusEvents, fearChanges);

        after = after with { Version = state.Version + 1 };

        var remaining = after.FindById(commander.Id)?.FindAbility(definition.Id)?.RemainingUses ?? 0;

        var outcome = new RallyOutcome
        {
            CommanderId = commander.Id,
            CommanderName = commander.Name,
            AllyId = ally.Id,
            AllyName = ally.Name,
            AbilityName = definition.Name,
            Modifier = definition.EffectValue,
            RemainingUses = remaining,
            FearChanges = fearChanges,
            NoLongerScared = wasScared && !after.RequireById(ally.Id).IsScared
        };

        return EngineResult.Accept(action, state, after, outcome, rngDraws: null, statusEvents,
            offerTransitions: null, fearChanges);
    }

    /// <summary>Applies <see cref="StatusEffectKind.Defending"/> to the actor. Repeatable, and never random.</summary>
    private EngineResult ResolveDefend(
        UseAbilityAction action, GameState state, Character actor, AbilityDefinition definition)
    {
        if (state.StatusOn(actor.Id, StatusEffectKind.Defending) is not null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.AbilityAlreadyActive,
                $"{actor.Name} already has their guard up; there is nothing more to brace.");
        }

        var status = NewStatus(StatusEffectKind.Defending, actor.Id, actor.Id, -definition.EffectValue,
            StatusExpiryRule.StartOfTargetNextTurn, definition.Id);

        var after = state.WithStatus(status) with { Version = state.Version + 1 };

        var outcome = new DefendOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            Reduction = definition.EffectValue
        };

        return EngineResult.Accept(action, state, after, outcome, statusEvents:
            [new StatusEvent(StatusEventKind.Applied, status, $"{actor.Name} braced behind their guard")]);
    }

    /// <summary>
    /// An ability that strikes with the weapon in hand. It routes straight into the shared weapon-strike
    /// resolution, so it makes exactly the ordinary attack draws and no others.
    /// </summary>
    private EngineResult ResolveAbilityStrike(
        UseAbilityAction action, GameState state, Character actor, Character target, AbilityDefinition definition)
    {
        if (actor.Weapon is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ActorHasNoWeapon,
                $"{actor.Name} is not carrying a weapon, and {definition.InWorldName} is delivered with one.");
        }

        return ResolveWeaponStrike(action, state, actor, target, actor.Weapon, definition);
    }

    /// <summary>
    /// A firebolt: a magical ranged strike that needs no weapon in hand. It routes into the very same
    /// weapon-strike resolution as every other blow — so it makes exactly the ordinary attack draws (one to
    /// hit, one for quality on a hit) and interacts with cover, defending, criticals and morale identically —
    /// but through a synthetic "weapon" carrying the spell's own fire damage, and with armour ignored.
    /// </summary>
    private EngineResult ResolveFirebolt(
        UseAbilityAction action, GameState state, Character caster, Character target, AbilityDefinition definition)
    {
        var bolt = new Weapon(definition.Name, definition.EffectValue);
        return ResolveWeaponStrike(action, state, caster, target, bolt, definition, ignoresArmour: true);
    }

    // ===============================================================================================
    // Containers, objects and exits
    // ===============================================================================================

    /// <summary>
    /// Opens a closed container. No randomness: opening either succeeds or is refused on authoritative
    /// state alone, so its result carries no draws and the combat generator is never advanced.
    /// </summary>
    private EngineResult ResolveOpenContainer(OpenContainerAction action)
    {
        var state = _state;

        var actor = state.Resolve(action.ActorRef);
        if (actor is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ActorRef}' in the room.");
        }

        if (!actor.CanAct)
        {
            return RejectInactiveActor(action, state, actor);
        }

        var resolution = state.ResolveObject(action.ContainerRef);
        if (resolution.Ambiguous)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ContainerReferenceAmbiguous,
                $"'{action.ContainerRef}' could mean more than one thing in the room; it is not clear which is meant.");
        }

        if (resolution.Object is not Container container)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownContainer,
                $"There is no container called '{action.ContainerRef}' in the room.");
        }

        if (container.IsOpen)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ContainerAlreadyOpen,
                $"The {container.Name} is already open.");
        }

        var opened = container with { IsOpen = true };
        var statusEvents = new List<StatusEvent>();
        var after = ApplyExposure(state.WithContainer(opened), ExposeIfInCover(state, actor.Id),
            $"{actor.Name} broke cover to open the {container.Name}", statusEvents);
        after = after with { Version = state.Version + 1 };

        var outcome = new OpenContainerOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            ContainerId = container.Id,
            ContainerName = container.Name,
            RevealedContents = [.. opened.Contents.Select(i => i.DisplayName)]
        };

        return EngineResult.Accept(action, state, after, outcome, statusEvents: statusEvents);
    }

    /// <summary>
    /// Moves one item from an open container into the actor's inventory as a single atomic change. No
    /// randomness. Because the removal and the addition are one new state, a second character attempting
    /// the same item afterwards finds it gone and is refused — two characters can never both acquire it.
    /// </summary>
    private EngineResult ResolveTakeItem(TakeItemAction action)
    {
        var state = _state;

        var actor = state.Resolve(action.ActorRef);
        if (actor is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ActorRef}' in the room.");
        }

        if (!actor.CanAct)
        {
            return RejectInactiveActor(action, state, actor);
        }

        var resolution = state.ResolveObject(action.ContainerRef);
        if (resolution.Ambiguous)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ContainerReferenceAmbiguous,
                $"'{action.ContainerRef}' could mean more than one thing in the room; it is not clear which is meant.");
        }

        if (resolution.Object is not Container container)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownContainer,
                $"There is no container called '{action.ContainerRef}' in the room.");
        }

        if (!container.IsOpen)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ContainerClosed,
                $"The {container.Name} is closed; nothing can be taken from it until it is opened.");
        }

        var (item, ambiguousItem) = container.ResolveItem(action.ItemRef);
        if (ambiguousItem)
        {
            return RejectAmbiguousItem(action, state, container.Contents, action.ItemRef, $"inside the {container.Name}");
        }

        if (item is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ItemNotInContainer,
                $"There is no '{action.ItemRef}' inside the {container.Name}.");
        }

        var index = container.Contents.IndexOf(item);
        var emptiedContainer = container with { Contents = container.Contents.RemoveAt(index) };
        var carryingActor = actor with { Inventory = actor.Inventory.Add(item) };

        // Both halves of the transfer, plus the actor's own cover breaking if they occupy any (v0.9), in one
        // new state, then a single version increment.
        var statusEvents = new List<StatusEvent>();
        var after = ApplyExposure(
            state.WithContainer(emptiedContainer).WithCharacter(carryingActor),
            ExposeIfInCover(state, actor.Id), $"{actor.Name} broke cover to reach for the {item.DisplayName}", statusEvents);
        after = after with { Version = state.Version + 1 };

        var outcome = new TakeItemOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            ContainerId = container.Id,
            ContainerName = container.Name,
            ItemId = item.Id,
            ItemName = item.DisplayName,
            RemainingContents = [.. emptiedContainer.Contents.Select(i => i.DisplayName)]
        };

        return EngineResult.Accept(action, state, after, outcome, statusEvents: statusEvents);
    }

    /// <summary>
    /// Resolves a close inspection of an object. No randomness and no mutation: the inspection reports what
    /// could be discovered — an exterior marking, and, for an open container, the current contents — and the
    /// orchestration layer turns that into private knowledge and a private observation. State before equals
    /// state after and the version is untouched, so an inspection consumes a turn without changing the world.
    /// </summary>
    private EngineResult ResolveInspectObject(InspectObjectAction action)
    {
        var state = _state;

        var actor = state.Resolve(action.ActorRef);
        if (actor is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ActorRef}' in the room.");
        }

        if (!actor.CanAct)
        {
            return RejectInactiveActor(action, state, actor);
        }

        var resolution = state.ResolveObject(action.ObjectRef);
        if (resolution.Ambiguous)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ObjectReferenceAmbiguous,
                $"'{action.ObjectRef}' could mean more than one thing in the room; it is not clear which is meant.");
        }

        if (resolution.Object is not { } worldObject)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownObject,
                $"There is no object called '{action.ObjectRef}' in the room.");
        }

        // Something is discoverable only if the object bears an exterior marking, or is an open container
        // whose current contents can be observed. An object with neither has nothing a closer look reveals.
        var container = worldObject as Container;
        var hasSomethingToDiscover = container?.ExteriorClue is not null || container is { IsOpen: true };

        if (!hasSomethingToDiscover)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.NothingToInspect,
                $"A closer look at the {worldObject.Name} turns up nothing a glance did not already give you.");
        }

        var outcome = new InspectObjectOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            ObjectId = worldObject.Id,
            ObjectName = worldObject.Name,
            IsContainer = container is not null,
            IsOpen = container is { IsOpen: true },
            ExteriorClue = container?.ExteriorClue,
            CurrentContents = container is { IsOpen: true }
                ? [.. container.Contents.Select(i => i.DisplayName)]
                : []
        };

        // Ordinarily no mutation and no version change at all: inspection is observation, not
        // action-on-the-world. The one exception is v0.9's own cover: leaning out to examine something breaks
        // it, so an inspector who occupies cover is exposed as a real side effect of an accepted inspection.
        var statusEvents = new List<StatusEvent>();
        var after = ApplyExposure(state, ExposeIfInCover(state, actor.Id),
            $"{actor.Name} broke cover to examine the {worldObject.Name}", statusEvents);
        if (statusEvents.Count > 0)
        {
            after = after with { Version = state.Version + 1 };
        }

        return EngineResult.Accept(action, state, after, outcome, statusEvents: statusEvents);
    }

    /// <summary>
    /// Opens a closed exit. No randomness: opening either succeeds or is refused on authoritative state
    /// alone, so its result carries no draws and the combat generator is never advanced. Opening only
    /// changes the door from shut to open; it never moves anyone through it.
    /// </summary>
    private EngineResult ResolveOpenExit(OpenExitAction action)
    {
        var state = _state;

        var actor = state.Resolve(action.ActorRef);
        if (actor is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ActorRef}' in the room.");
        }

        if (!actor.CanAct)
        {
            return RejectInactiveActor(action, state, actor);
        }

        var resolution = state.ResolveExit(action.ExitRef);
        if (resolution.Ambiguous)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ExitReferenceAmbiguous,
                $"'{action.ExitRef}' could mean more than one way out; it is not clear which is meant.");
        }

        if (resolution.Exit is not { } exit)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownExit,
                $"There is no exit called '{action.ExitRef}' in the room.");
        }

        if (exit.IsOpen)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ExitAlreadyOpen,
                $"The {exit.Name} already stands open.");
        }

        var opened = exit with { IsOpen = true };
        var statusEvents = new List<StatusEvent>();
        var after = ApplyExposure(state.WithExit(opened), ExposeIfInCover(state, actor.Id),
            $"{actor.Name} broke cover to haul the {exit.Name} open", statusEvents);
        after = after with { Version = state.Version + 1 };

        var outcome = new OpenExitOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            ExitId = exit.Id,
            ExitName = exit.Name,
            DestinationDescription = exit.DestinationDescription
        };

        return EngineResult.Accept(action, state, after, outcome, statusEvents: statusEvents);
    }

    /// <summary>
    /// Passes a character through an already-open exit, setting their disposition to
    /// <see cref="CharacterDisposition.Escaped"/> and recording which exit they used. No randomness, no
    /// escape roll, no opportunity attack and no pursuit — an open exit is simply walked through. Their
    /// health, inventory and equipment are untouched; they are alive, just gone. Leaving does end anything
    /// they were sustaining: their statuses fall away and any offer they were party to is unenforceable.
    /// </summary>
    private EngineResult ResolveEscapeEncounter(EscapeEncounterAction action)
    {
        var state = _state;

        var actor = state.Resolve(action.ActorRef);
        if (actor is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ActorRef}' in the room.");
        }

        if (!actor.CanAct)
        {
            return RejectInactiveActor(action, state, actor);
        }

        var resolution = state.ResolveExit(action.ExitRef);
        if (resolution.Ambiguous)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ExitReferenceAmbiguous,
                $"'{action.ExitRef}' could mean more than one way out; it is not clear which is meant.");
        }

        if (resolution.Exit is not { } exit)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownExit,
                $"There is no exit called '{action.ExitRef}' in the room.");
        }

        if (!exit.IsOpen)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ExitClosed,
                $"The {exit.Name} is still shut; you cannot escape through it until it is open.");
        }

        var escaped = actor with
        {
            Disposition = CharacterDisposition.Escaped,
            EscapedThroughExitId = exit.Id
        };

        var statusEvents = new List<StatusEvent>();
        var transitions = new List<OfferTransition>();
        var demandTransitions = new List<DemandTransition>();
        var after = state.WithCharacter(escaped);
        (after, _) = PurgeForInactive(after, actor.Id, "escaped the encounter", statusEvents, transitions, demandTransitions);
        after = after with { Version = state.Version + 1 };

        var outcome = new EscapeOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            ExitId = exit.Id,
            ExitName = exit.Name,
            DestinationDescription = exit.DestinationDescription
        };

        return EngineResult.Accept(action, state, after, outcome,
            statusEvents: statusEvents, offerTransitions: transitions, demandTransitions: demandTransitions);
    }

    // ===============================================================================================
    // Inventory transfers
    // ===============================================================================================

    /// <summary>
    /// Resolves the current actor giving one of its ordinary inventory items to another character present in
    /// the room. No randomness: the transfer is one atomic state change (the item leaves the giver and enters
    /// the recipient in a single new state), so validation either passes and the item moves, or fails and
    /// nothing changes. The giver is always the current actor; the engine never moves an item for someone else.
    /// </summary>
    private EngineResult ResolveGiveItem(GiveItemAction action)
    {
        var state = _state;

        var giver = state.Resolve(action.GiverRef);
        if (giver is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.GiverRef}' in the room.");
        }

        if (!giver.CanAct)
        {
            return RejectInactiveActor(action, state, giver);
        }

        var recipient = state.Resolve(action.RecipientRef);
        if (recipient is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownRecipient,
                $"There is no character called '{action.RecipientRef}' in the room to give anything to.");
        }

        if (Same(recipient.Id, giver.Id))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.RecipientIsSelf,
                $"{giver.Name} cannot give an item to themselves.");
        }

        // The recipient must be alive and physically here to take it. A surrendered ally is still present and
        // may be handed something; only the dead and the escaped are out of reach.
        if (!recipient.IsAlive || !recipient.IsPresent)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.RecipientNotPresent,
                $"{recipient.Name} is not here to take anything.");
        }

        if (NamesEquippedWeapon(giver, action.ItemRef))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.EquippedWeaponCannotBeTransferred,
                $"{giver.Name}'s {giver.Weapon!.Name} is their equipped weapon, not an item that can be handed over.");
        }

        var (item, ambiguousItem) = giver.ResolveItem(action.ItemRef);
        if (ambiguousItem)
        {
            return RejectAmbiguousItem(action, state, giver.Inventory, action.ItemRef, $"that {giver.Name} is carrying");
        }

        if (item is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ItemNotPossessed,
                $"{giver.Name} is not carrying '{action.ItemRef}'.");
        }

        // Both halves of the transfer, plus the giver's own cover breaking if they occupy any (v0.9) — handing
        // something to someone else means reaching toward them — in one new state, then a single version
        // increment: the item can never momentarily exist in both inventories or in neither.
        var strippedGiver = giver with { Inventory = RemoveFirst(giver, item) };
        var carryingRecipient = recipient with { Inventory = recipient.Inventory.Add(item) };
        var statusEvents = new List<StatusEvent>();
        var after = ApplyExposure(
            state.WithCharacter(strippedGiver).WithCharacter(carryingRecipient),
            ExposeIfInCover(state, giver.Id), $"{giver.Name} broke cover to hand over the {item.DisplayName}", statusEvents);
        after = after with { Version = state.Version + 1 };

        var transitions = new List<OfferTransition>();
        after = InvalidateOffersPromising(after, giver.Id, item.Id, "a promised item was given away", transitions);

        var outcome = new GiveItemOutcome
        {
            GiverId = giver.Id,
            GiverName = giver.Name,
            RecipientId = recipient.Id,
            RecipientName = recipient.Name,
            ItemId = item.Id,
            ItemName = item.DisplayName
        };

        return EngineResult.Accept(action, state, after, outcome, statusEvents: statusEvents, offerTransitions: transitions);
    }

    /// <summary>Resolves showing an inventory item to another present character without transferring it.</summary>
    private EngineResult ResolvePresentItem(PresentItemAction action)
    {
        var state = _state;
        var actor = state.Resolve(action.ActorRef);
        if (actor is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ActorRef}' in the room.");
        }

        if (!actor.CanAct)
        {
            return RejectInactiveActor(action, state, actor);
        }

        var recipient = state.Resolve(action.RecipientRef);
        if (recipient is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownRecipient,
                $"There is no character called '{action.RecipientRef}' in the room to show anything to.");
        }

        if (Same(recipient.Id, actor.Id))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.RecipientIsSelf,
                $"{actor.Name} cannot present an item to themselves.");
        }

        if (!recipient.IsAlive || !recipient.IsPresent)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.RecipientNotPresent,
                $"{recipient.Name} is not here to see anything.");
        }

        var (item, ambiguousItem) = actor.ResolveItem(action.ItemRef);
        if (ambiguousItem)
        {
            return RejectAmbiguousItem(action, state, actor.Inventory, action.ItemRef,
                $"that {actor.Name} is carrying");
        }

        if (item is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ItemNotPossessed,
                $"{actor.Name} is not carrying '{action.ItemRef}'.");
        }

        var outcome = new PresentItemOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            RecipientId = recipient.Id,
            RecipientName = recipient.Name,
            ItemId = item.Id,
            ItemName = item.DisplayName
        };

        return EngineResult.Accept(action, state, state, outcome);
    }

    /// <summary>
    /// Resolves the current actor dropping one of its ordinary inventory items onto the room's ground-loot
    /// location. No randomness. The item keeps its stable id and moves atomically from the actor's inventory
    /// to the floor, where it can afterwards be taken through the ordinary <c>take_item</c> interaction. The
    /// floor container is created the first time anything is dropped and reused thereafter.
    /// </summary>
    private EngineResult ResolveDropItem(DropItemAction action)
    {
        var state = _state;

        var actor = state.Resolve(action.ActorRef);
        if (actor is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ActorRef}' in the room.");
        }

        if (!actor.CanAct)
        {
            return RejectInactiveActor(action, state, actor);
        }

        if (NamesEquippedWeapon(actor, action.ItemRef))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.EquippedWeaponCannotBeTransferred,
                $"{actor.Name}'s {actor.Weapon!.Name} is their equipped weapon, not an item that can be dropped.");
        }

        var (item, ambiguousItem) = actor.ResolveItem(action.ItemRef);
        if (ambiguousItem)
        {
            return RejectAmbiguousItem(action, state, actor.Inventory, action.ItemRef, $"that {actor.Name} is carrying");
        }

        if (item is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ItemNotPossessed,
                $"{actor.Name} is not carrying '{action.ItemRef}'.");
        }

        var (roomWithGround, ground) = PlaceOnGround(state.Room, item);
        var strippedActor = actor with { Inventory = RemoveFirst(actor, item) };
        var after = state.WithCharacter(strippedActor) with { Room = roomWithGround, Version = state.Version + 1 };

        var transitions = new List<OfferTransition>();
        after = InvalidateOffersPromising(after, actor.Id, item.Id, "a promised item was dropped", transitions);

        var outcome = new DropItemOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            ItemId = item.Id,
            ItemName = item.DisplayName,
            GroundContainerName = ground.Name
        };

        return EngineResult.Accept(action, state, after, outcome, offerTransitions: transitions);
    }

    /// <summary>
    /// Resolves an attempted theft: the current actor trying to take one ordinary inventory item from another
    /// active character. This is the one inventory action that consults randomness — exactly one seeded d100
    /// draw against the configured base theft chance decides it. The attempt is always publicly noticed and
    /// consumes the turn whether it succeeds or fails; on success the item moves atomically from target to
    /// thief. Validation runs before the draw, so a rejected attempt consults no randomness at all.
    /// </summary>
    private EngineResult ResolveStealItem(StealItemAction action)
    {
        var state = _state;

        var thief = state.Resolve(action.ThiefRef);
        if (thief is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ThiefRef}' in the room.");
        }

        if (!thief.CanAct)
        {
            return RejectInactiveActor(action, state, thief);
        }

        var target = state.Resolve(action.TargetRef);
        if (target is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownTarget,
                $"There is no character called '{action.TargetRef}' in the room.");
        }

        if (Same(target.Id, thief.Id))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.TargetIsSelf,
                $"{thief.Name} cannot steal from themselves.");
        }

        // Both must be active and present: a surrendered, escaped or dead character is out of the fight and
        // cannot be pickpocketed.
        if (!target.CanAct)
        {
            return RejectUnavailableTarget(action, state, target, "steal from");
        }

        if (NamesEquippedWeapon(target, action.ItemRef))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.EquippedWeaponCannotBeTransferred,
                $"{target.Name}'s {target.Weapon!.Name} is their equipped weapon and cannot be stolen in the middle of a fight.");
        }

        var (item, ambiguousItem) = target.ResolveItem(action.ItemRef);
        if (ambiguousItem)
        {
            return RejectAmbiguousItem(action, state, target.Inventory, action.ItemRef, $"that {target.Name} is carrying");
        }

        if (item is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ItemNotPossessed,
                $"{target.Name} is not carrying '{action.ItemRef}'.");
        }

        // Exactly one draw, taken only after every validation has passed. The base chance is a flat
        // configurable number; the record is shaped to carry modifiers, and v0.7 applies none to a theft.
        var baseChance = _combatRules.BaseStealChance;
        var modifiers = new List<string>();
        var effectiveChance = Math.Clamp(baseChance, 0, 100);

        var sequenceBefore = _rng.DrawCount;
        var roll = _rng.RollPercent();
        var succeeded = roll <= effectiveChance;
        var draw = new RngDraw
        {
            Purpose = "steal.attempt",
            ActionType = action.ActionType,
            ActorId = thief.Id,
            ActorName = thief.Name,
            TargetId = target.Id,
            TargetName = target.Name,
            OutcomeSelected = "theft succeeds or fails",
            Sides = 100,
            RangeMin = 1,
            RangeMax = 100,
            RawRoll = roll,
            BaseChance = baseChance,
            Modifiers = modifiers,
            Threshold = effectiveChance,
            Comparison = $"roll {roll} {(succeeded ? "<=" : ">")} effective steal chance {effectiveChance}",
            Result = succeeded ? "stolen" : "failed",
            Seed = _rng.Seed,
            SequenceBefore = sequenceBefore,
            SequenceAfter = _rng.DrawCount
        };

        var outcome = new StealItemOutcome
        {
            ThiefId = thief.Id,
            ThiefName = thief.Name,
            TargetId = target.Id,
            TargetName = target.Name,
            ItemId = item.Id,
            ItemName = item.DisplayName,
            BaseChance = baseChance,
            Modifiers = modifiers,
            EffectiveChance = effectiveChance,
            Roll = roll,
            Succeeded = succeeded
        };

        // Reaching into an opponent's belt is a hostile act, so it rejects any offer that opponent had on the
        // table with the thief's side — whether the grab came off or not. Reaching also breaks the thief's own
        // cover (v0.9), whether the grab comes off or not — exactly like a missed attack, an attempt is
        // exposure regardless of its result.
        var transitions = new List<OfferTransition>();
        var statusEvents = new List<StatusEvent>();
        var after = state;
        var (afterHostility, hostilityChanged) = ApplyHostility(after, thief, [target.Id], transitions);
        after = afterHostility;
        after = ApplyExposure(after, ExposeIfInCover(state, thief.Id),
            $"{thief.Name} broke cover to reach for the {item.DisplayName}", statusEvents);
        var selfExposed = statusEvents.Count > 0;

        if (!succeeded)
        {
            if (hostilityChanged || selfExposed)
            {
                after = after with { Version = state.Version + 1 };
                return EngineResult.Accept(action, state, after, outcome, [draw], statusEvents: statusEvents, offerTransitions: transitions);
            }

            // Nothing changes hands. Like a missed attack, the state and its version are untouched, but the
            // turn is spent, so the attempt is an accepted action whose before-state equals its after-state.
            return EngineResult.Accept(action, state, state, outcome, [draw]);
        }

        var strippedTarget = after.FindById(target.Id)! with { Inventory = RemoveFirst(after.FindById(target.Id)!, item) };
        var carryingThief = after.FindById(thief.Id)! with { Inventory = after.FindById(thief.Id)!.Inventory.Add(item) };
        after = after
            .WithCharacter(strippedTarget)
            .WithCharacter(carryingThief) with { Version = state.Version + 1 };

        after = InvalidateOffersPromising(after, target.Id, item.Id, "a promised item was stolen", transitions);

        return EngineResult.Accept(action, state, after, outcome, [draw], statusEvents: statusEvents, offerTransitions: transitions);
    }

    // ===============================================================================================
    // Environmental cover (v0.9)
    // ===============================================================================================

    /// <summary>
    /// Resolves a character taking shelter behind one environmental object with the cover capability. No
    /// randomness. Capacity is enforced authoritatively here: a second character cannot occupy capacity-one
    /// cover, whatever a model describes them doing.
    /// </summary>
    private EngineResult ResolveTakeCover(TakeCoverAction action)
    {
        var state = _state;

        var actor = state.Resolve(action.ActorRef);
        if (actor is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ActorRef}' in the room.");
        }

        if (!actor.CanAct)
        {
            return RejectInactiveActor(action, state, actor);
        }

        var resolution = state.ResolveObject(action.CoverRef);
        if (resolution.Ambiguous)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.CoverReferenceAmbiguous,
                $"'{action.CoverRef}' could mean more than one thing in the room; it is not clear which is meant.");
        }

        if (resolution.Object is not CoverObject cover)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownCover,
                $"There is no cover called '{action.CoverRef}' in the room.");
        }

        if (!cover.CanProvideCover)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.CoverCannotBeUsed,
                $"The {cover.Name} is destroyed and offers no shelter.");
        }

        if (Same(cover.CurrentOccupantId, actor.Id))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.AlreadyInThatCover,
                $"{actor.Name} is already behind the {cover.Name}.");
        }

        if (!cover.HasSpareCapacity)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.CoverFull,
                $"There is no room left behind the {cover.Name}.");
        }

        // Occupying cover is exclusive, so taking this one first releases whatever the actor already occupied
        // (never reachable with the one cover object v0.9 seeds, but the invariant — behind at most one thing
        // at a time — has to hold in general).
        var previousExposure = ExposeIfInCover(state, actor.Id);

        var status = NewStatus(StatusEffectKind.InCover, actor.Id, actor.Id, 0,
            StatusExpiryRule.WhileConditionHolds, relatedObjectId: cover.Id);

        var statusEvents = new List<StatusEvent>
        {
            new(StatusEventKind.Applied, status, $"{actor.Name} took cover behind {cover.Name}")
        };

        var after = ApplyExposure(state, previousExposure,
            $"{actor.Name} left it to take cover behind {cover.Name} instead", statusEvents);
        after = after.WithCover(cover with { CurrentOccupantId = actor.Id }).WithStatus(status);
        after = after with { Version = state.Version + 1 };

        var outcome = new TakeCoverOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            CoverId = cover.Id,
            CoverName = cover.Name
        };

        return EngineResult.Accept(action, state, after, outcome, statusEvents: statusEvents);
    }

    /// <summary>
    /// Resolves a character deliberately stepping out from behind the cover they occupy, with nothing else
    /// attempted. No randomness. Distinct from cover ending as a side effect of some other accepted action —
    /// see <see cref="ExposeIfInCover"/> — which needs no separate turn.
    /// </summary>
    private EngineResult ResolveLeaveCover(LeaveCoverAction action)
    {
        var state = _state;

        var actor = state.Resolve(action.ActorRef);
        if (actor is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ActorRef}' in the room.");
        }

        if (!actor.CanAct)
        {
            return RejectInactiveActor(action, state, actor);
        }

        var exposure = ExposeIfInCover(state, actor.Id);
        if (!exposure.Occurred)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.NotInCover,
                $"{actor.Name} is not sheltering behind anything to step out of.");
        }

        var statusEvents = new List<StatusEvent>();
        var after = ApplyExposure(state, exposure, $"{actor.Name} deliberately stepped out from cover", statusEvents);
        after = after with { Version = state.Version + 1 };

        var outcome = new LeaveCoverOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            CoverId = exposure.ReleasedCover?.Id ?? "",
            CoverName = exposure.ReleasedCover?.Name ?? "cover"
        };

        return EngineResult.Accept(action, state, after, outcome, statusEvents: statusEvents);
    }

    /// <summary>
    /// Resolves a character deliberately striking one present, non-destroyed environmental object with the
    /// weapon in hand, rather than a character. No hit or quality roll — a stationary room object does not
    /// dodge — so damage is the deterministic <c>max(1, weapon damage - object armour)</c>. Breaks the
    /// actor's own cover, when they occupy any, before the blow lands; refused against the very cover the
    /// actor currently occupies. Reducing durability to zero destroys the object and, if anyone was
    /// sheltering there, exposes them as part of the same event.
    /// </summary>
    private EngineResult ResolveDamageEnvironmentalObject(DamageEnvironmentalObjectAction action)
    {
        var state = _state;

        var actor = state.Resolve(action.ActorRef);
        if (actor is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ActorRef}' in the room.");
        }

        if (!actor.CanAct)
        {
            return RejectInactiveActor(action, state, actor);
        }

        if (actor.Weapon is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ActorHasNoWeapon,
                $"{actor.Name} is not carrying a weapon.");
        }

        var resolution = state.ResolveObject(action.ObjectRef);
        if (resolution.Ambiguous)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ObjectReferenceAmbiguous,
                $"'{action.ObjectRef}' could mean more than one thing in the room; it is not clear which is meant.");
        }

        if (resolution.Object is not CoverObject target)
        {
            return resolution.Found
                ? EngineResult.Reject(action, state, EngineRejectionReason.ObjectCannotBeDamaged,
                    $"There is nothing about the {resolution.Object!.Name} that a blow can do anything to.")
                : EngineResult.Reject(action, state, EngineRejectionReason.UnknownObject,
                    $"There is no object called '{action.ObjectRef}' in the room.");
        }

        if (target.State == EnvironmentalObjectState.Destroyed)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ObjectAlreadyDestroyed,
                $"The {target.Name} is already wreckage; there is nothing left to strike.");
        }

        if (Same(target.CurrentOccupantId, actor.Id))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.CannotDamageOwnCover,
                $"{actor.Name} will not tear apart the {target.Name} while sheltering behind it.");
        }

        var weapon = actor.Weapon;
        var durabilityBefore = target.CurrentDurability;
        var damage = Math.Max(1, weapon.Damage - target.Armour);
        var durabilityAfter = Math.Max(0, durabilityBefore - damage);
        var destroyed = durabilityAfter == 0;
        var exposedOccupantId = destroyed ? target.CurrentOccupantId : null;
        var exposedOccupant = exposedOccupantId is null ? null : state.FindById(exposedOccupantId);

        var updatedObject = target with
        {
            CurrentDurability = durabilityAfter,
            CurrentOccupantId = destroyed ? null : target.CurrentOccupantId
        };

        var statusEvents = new List<StatusEvent>();
        var after = state.WithCover(updatedObject);

        if (destroyed && exposedOccupantId is not null
            && state.StatusOn(exposedOccupantId, StatusEffectKind.InCover) is { } occupantInCover)
        {
            after = after.WithoutStatuses([occupantInCover.Id]);
            statusEvents.Add(new StatusEvent(StatusEventKind.Consumed, occupantInCover with { Consumed = true },
                $"the {target.Name} was destroyed and {exposedOccupant?.Name ?? exposedOccupantId} was exposed"));
        }

        // The actor's own cover (if any — necessarily a different object than the one just struck, since
        // striking the actor's own cover was refused above) breaks the instant they swing.
        var selfExposure = ExposeIfInCover(state, actor.Id);
        after = ApplyExposure(after, selfExposure, $"{actor.Name} broke cover to strike the {target.Name}", statusEvents);

        after = after with { Version = state.Version + 1 };

        var outcome = new DamageEnvironmentalObjectOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            WeaponName = weapon.Name,
            WeaponDamage = weapon.Damage,
            ObjectId = target.Id,
            ObjectName = target.Name,
            ObjectArmour = target.Armour,
            DamageApplied = damage,
            DurabilityBefore = durabilityBefore,
            DurabilityAfter = durabilityAfter,
            Destroyed = destroyed,
            SelfCoverBroken = selfExposure.Occurred,
            ExposedOccupantName = destroyed ? exposedOccupant?.Name ?? exposedOccupantId : null
        };

        return EngineResult.Accept(action, state, after, outcome, statusEvents: statusEvents);
    }

    // ===============================================================================================
    // Status, offer and shared helpers
    // ===============================================================================================

    /// <summary>Mints a new status instance with a stable, run-reproducible id.</summary>
    private StatusEffectInstance NewStatus(
        StatusEffectKind kind, string sourceId, string targetId, int modifier,
        StatusExpiryRule expiry, string? abilityId = null, string? relationshipId = null,
        string? relatedObjectId = null) => new()
        {
            Id = $"status-{++_statusCounter}",
            Kind = kind,
            SourceCharacterId = sourceId,
            TargetCharacterId = targetId,
            AppliedRound = CurrentRound,
            AppliedTurn = CurrentTurn,
            Modifier = modifier,
            ExpiryRule = expiry,
            Visibility = StatusVisibility.Public,
            RelationshipId = relationshipId,
            SourceAbilityId = abilityId,
            RelatedObjectId = relatedObjectId
        };

    /// <summary>
    /// Every status in the same linked relationship as the given one, including itself. A paired effect must
    /// always be removed on both sides at once, so a Guarded ally can never be left guarded by nobody.
    /// </summary>
    private static IReadOnlyList<StatusEffectInstance> LinkedStatuses(GameState state, StatusEffectInstance status) =>
        status.RelationshipId is null
            ? [status]
            : [.. state.Statuses.Where(s => string.Equals(s.RelationshipId, status.RelationshipId, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// What breaking a character's own cover releases, computed as a delta against a snapshot rather than
    /// applied to it — see <see cref="ExposeIfInCover"/>.
    /// </summary>
    private readonly record struct CoverExposure(StatusEffectInstance? ReleasedStatus, CoverObject? ReleasedCover)
    {
        /// <summary>True when the character was actually sheltering behind something.</summary>
        public bool Occurred => ReleasedStatus is not null;
    }

    /// <summary>
    /// Whether <paramref name="actorId"/> currently occupies cover and, if so, what releasing it would mean:
    /// the <c>InCover</c> status to remove, and the cover object (with the occupant cleared) to write back.
    /// Deliberately a pure query — it does NOT mutate <paramref name="state"/> — because <c>state</c> in a
    /// Resolve method is the action's true "before" snapshot, reused as <see cref="EngineResult.StateBefore"/>;
    /// the caller folds the returned delta into the "after" state it is already building, atomically with the
    /// rest of that action's own effect (v0.9 §4: "release cover immediately before resolving that accepted
    /// action" — immediately before the *result*, not before validation, and never for a rejected attempt,
    /// since a Resolve method only reaches this point once every precondition of its own action has passed).
    /// A no-op, returning an empty <see cref="CoverExposure"/>, when the actor occupies nothing.
    /// </summary>
    private static CoverExposure ExposeIfInCover(GameState state, string actorId)
    {
        var inCover = state.StatusOn(actorId, StatusEffectKind.InCover);
        return inCover is null
            ? default
            : new CoverExposure(inCover, state.CoverOccupiedBy(actorId));
    }

    /// <summary>
    /// Folds a computed <see cref="CoverExposure"/> into a state already being assembled as an action's
    /// "after" result: removes the released <c>InCover</c> status, clears the released cover's occupant, and
    /// records the transition. A no-op, returning <paramref name="after"/> unchanged, when nothing was
    /// occupied.
    /// </summary>
    private static GameState ApplyExposure(GameState after, CoverExposure exposure, string cause, List<StatusEvent> statusEvents)
    {
        if (!exposure.Occurred)
        {
            return after;
        }

        after = after.WithoutStatuses([exposure.ReleasedStatus!.Id]);
        if (exposure.ReleasedCover is not null)
        {
            after = after.WithCover(exposure.ReleasedCover with { CurrentOccupantId = null });
        }

        statusEvents.Add(new StatusEvent(StatusEventKind.Consumed, exposure.ReleasedStatus with { Consumed = true }, cause));
        return after;
    }

    /// <summary>
    /// Removes a set of statuses as expired, expanding each to its whole linked relationship and recording one
    /// <see cref="StatusEventKind.Expired"/> event per instance removed.
    /// </summary>
    private static GameState ExpireStatuses(
        GameState state, IReadOnlyList<StatusEffectInstance> due, string cause, List<StatusEvent> events)
    {
        if (due.Count == 0)
        {
            return state;
        }

        var removing = new Dictionary<string, StatusEffectInstance>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in due)
        {
            foreach (var half in LinkedStatuses(state, status))
            {
                removing[half.Id] = half;
            }
        }

        foreach (var status in removing.Values)
        {
            events.Add(new StatusEvent(StatusEventKind.Expired, status, cause));
        }

        return state.WithoutStatuses(removing.Keys);
    }

    /// <summary>
    /// Sweeps everything a character can no longer sustain or answer for once they leave active play: every
    /// status on or from them (with the paired half of any linked relationship), and every pending surrender
    /// offer they were party to. Returns the new state and whether anything actually changed.
    /// </summary>
    private (GameState State, bool Changed) PurgeForInactive(
        GameState state,
        string characterId,
        string cause,
        List<StatusEvent> statusEvents,
        List<OfferTransition> offerTransitions,
        List<DemandTransition> demandTransitions,
        string? exceptOfferId = null)
    {
        var changed = false;

        var affected = state.Statuses
            .Where(s => Same(s.TargetCharacterId, characterId) || Same(s.SourceCharacterId, characterId))
            .ToList();

        if (affected.Count > 0)
        {
            var removing = new Dictionary<string, StatusEffectInstance>(StringComparer.OrdinalIgnoreCase);
            foreach (var status in affected)
            {
                foreach (var half in LinkedStatuses(state, status))
                {
                    removing[half.Id] = half;
                }
            }

            foreach (var status in removing.Values)
            {
                statusEvents.Add(new StatusEvent(StatusEventKind.Removed, status,
                    $"the character it depended on {cause}"));
            }

            state = state.WithoutStatuses(removing.Keys);
            changed = true;
        }

        // Cover occupancy (v0.9) is authoritative on the object, not only on the InCover status swept above —
        // a character who leaves active play while sheltering must be cleared from the object itself too.
        if (state.CoverOccupiedBy(characterId) is { } occupiedCover)
        {
            state = state.WithCover(occupiedCover with { CurrentOccupantId = null });
            changed = true;
        }

        foreach (var offer in state.PendingOffers().ToList())
        {
            if (exceptOfferId is not null && Same(offer.Id, exceptOfferId))
            {
                continue;
            }

            if (!Same(offer.OffererId, characterId) && !Same(offer.RecipientId, characterId))
            {
                continue;
            }

            state = TransitionOffer(state, offer, SurrenderOfferState.Invalidated,
                $"a party to the offer {cause}", CurrentRound, CurrentTurn, offerTransitions);
            changed = true;
        }

        // A demand can mean nothing once either the demander or the target leaves active play: the pressure
        // has no one to answer it, or no one to answer to. It is quietly invalidated, exactly like an offer.
        foreach (var demand in state.PendingDemands().ToList())
        {
            if (!Same(demand.DemanderId, characterId) && !Same(demand.TargetId, characterId))
            {
                continue;
            }

            state = TransitionDemand(state, demand, SurrenderDemandState.Invalidated,
                $"a party to the demand {cause}", CurrentRound, CurrentTurn, demandTransitions);
            changed = true;
        }

        return (state, changed);
    }

    /// <summary>
    /// Applies the consequences of a hostile act: an offer made by the character who was struck is rejected
    /// when the blow came from the recipient's side, and an offerer who strikes while their own offer stands
    /// has abandoned it. Returns the new state and whether any offer changed.
    /// </summary>
    private (GameState State, bool Changed) ApplyHostility(
        GameState state, Character aggressor, IReadOnlyList<string> victimIds, List<OfferTransition> transitions)
    {
        var changed = false;

        foreach (var victimId in victimIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var offer in state.PendingOffers().Where(o => Same(o.OffererId, victimId)).ToList())
            {
                var recipient = state.FindById(offer.RecipientId);
                var fromRecipientSide = Same(aggressor.Id, offer.RecipientId)
                    || (recipient is not null && recipient.IsAllyOf(aggressor));
                if (!fromRecipientSide)
                {
                    continue;
                }

                state = TransitionOffer(state, offer, SurrenderOfferState.Rejected,
                    $"{aggressor.Name} answered the offer with a hostile act", CurrentRound, CurrentTurn, transitions);
                changed = true;
            }
        }

        foreach (var offer in state.PendingOffers().Where(o => Same(o.OffererId, aggressor.Id)).ToList())
        {
            state = TransitionOffer(state, offer, SurrenderOfferState.Invalidated,
                $"{aggressor.Name} took a hostile act while their own offer stood", CurrentRound, CurrentTurn, transitions);
            changed = true;
        }

        return (state, changed);
    }

    /// <summary>
    /// Invalidates any pending offer in which <paramref name="ownerId"/> promised the item that has just left
    /// their hands. An offer whose terms can no longer be honoured must not stay on the table pretending it can.
    /// </summary>
    private GameState InvalidateOffersPromising(
        GameState state, string ownerId, string itemId, string cause, List<OfferTransition> transitions)
    {
        foreach (var offer in state.PendingOffers().Where(o => Same(o.OffererId, ownerId)).ToList())
        {
            if (!offer.OfferedItemIds.Any(id => Same(id, itemId)))
            {
                continue;
            }

            state = TransitionOffer(state, offer, SurrenderOfferState.Invalidated, cause,
                CurrentRound, CurrentTurn, transitions);
        }

        return state;
    }

    /// <summary>Moves one offer out of Pending, recording the transition for the trace.</summary>
    private static GameState TransitionOffer(
        GameState state,
        SurrenderOffer offer,
        SurrenderOfferState newState,
        string cause,
        int round,
        int turn,
        List<OfferTransition> transitions)
    {
        var updated = offer with
        {
            State = newState,
            ResolutionCause = cause,
            ResolvedRound = round,
            ResolvedTurn = turn
        };

        transitions.Add(new OfferTransition(updated, offer.State, newState, cause));
        return state.WithUpdatedOffer(updated);
    }

    private static GameState TransitionDemand(
        GameState state,
        SurrenderDemand demand,
        SurrenderDemandState newState,
        string cause,
        int round,
        int turn,
        List<DemandTransition> transitions)
    {
        var updated = demand with
        {
            State = newState,
            ResolutionCause = cause,
            ResolvedRound = round,
            ResolvedTurn = turn
        };

        transitions.Add(new DemandTransition(updated, demand.State, newState, cause));
        return state.WithUpdatedDemand(updated);
    }

    /// <summary>Spends one charge of a limited ability. Unlimited abilities and untracked holds are untouched.</summary>
    private static GameState SpendCharge(GameState state, string characterId, AbilityDefinition definition, CharacterAbility? held)
    {
        if (held is null || definition.IsUnlimited || held.RemainingUses is not > 0)
        {
            return state;
        }

        var holder = state.FindById(characterId);
        return holder is null
            ? state
            : state.WithCharacter(holder.WithAbilityCharges(definition.Id, held.RemainingUses - 1));
    }

    /// <summary>
    /// Adds an item to the room's ground-loot container, creating that container the first time anything is
    /// dropped and appending to it thereafter. Returns the new room and the resulting ground container.
    /// </summary>
    private static (Room Room, Container Ground) PlaceOnGround(Room room, InventoryItem item)
    {
        var objects = room.Objects;
        for (var index = 0; index < objects.Length; index++)
        {
            if (objects[index] is Container { IsGround: true } existing)
            {
                var updated = existing with { Contents = existing.Contents.Add(item) };
                return (room with { Objects = objects.SetItem(index, updated) }, updated);
            }
        }

        var ground = Container.Ground([item]);
        return (room with { Objects = objects.Add(ground) }, ground);
    }

    /// <summary>
    /// True when <paramref name="itemRef"/> names the character's currently equipped weapon rather than an
    /// ordinary inventory item. Equipped weapons live outside the inventory and are never transferable by
    /// giving, dropping or theft, so a reference to one is refused with its own reason rather than a generic
    /// "not carrying it". The one way a weapon leaves a hand is an accepted surrender.
    /// </summary>
    private static bool NamesEquippedWeapon(Character character, string itemRef)
    {
        if (character.Weapon is null || string.IsNullOrWhiteSpace(itemRef))
        {
            return false;
        }

        var needle = itemRef.Trim();
        return string.Equals(character.Weapon.Name, needle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(character.Weapon.Id, needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The shared refusal for an actor who cannot act. Only an active character reaches the engine as an
    /// actor in normal play (the turn loop skips the rest), so this is a defensive guard; it still names the
    /// specific reason for the record.
    /// </summary>
    private static EngineResult RejectInactiveActor(GameAction action, GameState state, Character actor) =>
        actor.Disposition switch
        {
            CharacterDisposition.Dead => EngineResult.Reject(action, state, EngineRejectionReason.ActorIsDead,
                $"{actor.Name} is dead and cannot act."),
            CharacterDisposition.Surrendered => EngineResult.Reject(action, state, EngineRejectionReason.ActorNotActive,
                $"{actor.Name} has already given up the fight and takes no further part in it."),
            CharacterDisposition.Escaped => EngineResult.Reject(action, state, EngineRejectionReason.ActorNotActive,
                $"{actor.Name} has already left the encounter."),
            _ => EngineResult.Reject(action, state, EngineRejectionReason.ActorNotActive,
                $"{actor.Name} cannot act right now.")
        };

    /// <summary>
    /// The shared refusal for a reference that names more than one item, naming the qualified alternatives
    /// so the next attempt can pick one. <paramref name="where"/> completes "matches more than one thing
    /// ...", e.g. "inside the chest" or "that Vark is carrying".
    /// </summary>
    /// <remarks>
    /// Listing the alternatives is the point of the refusal, not decoration. A bare "that is ambiguous"
    /// hands a model back the same words it just used with no way forward, and the qualified display name is
    /// precisely the reference that would resolve. This is the reply the Dungeon Master deliberated its whole
    /// output budget away for want of, when Rowan reached for one of Vark's two identical purses.
    /// </remarks>
    private static EngineResult RejectAmbiguousItem(
        GameAction action, GameState state, IEnumerable<InventoryItem> among, string itemRef, string where)
    {
        var options = among
            .Where(i => i.MatchesReference(itemRef))
            .Select(i => i.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var choices = options.Count == 0 ? "" : $" - {string.Join(" and ", options)}";
        return EngineResult.Reject(action, state, EngineRejectionReason.ItemReferenceAmbiguous,
            $"'{itemRef}' matches more than one thing {where}{choices}. Name the one that is meant.");
    }

    /// <summary>
    /// The shared refusal for a target who has left active play, with one reason per disposition so the
    /// Dungeon Master can explain the right thing in-world. <paramref name="verb"/> completes the sentence.
    /// </summary>
    private static EngineResult RejectUnavailableTarget(
        GameAction action, GameState state, Character target, string verb) =>
        target.Disposition switch
        {
            CharacterDisposition.Surrendered => EngineResult.Reject(action, state,
                EngineRejectionReason.TargetHasSurrendered,
                $"{target.Name} has given up the fight and is no longer someone to {verb}."),
            CharacterDisposition.Escaped => EngineResult.Reject(action, state,
                EngineRejectionReason.TargetHasEscaped,
                $"{target.Name} has fled the encounter and is no longer here to {verb}."),
            CharacterDisposition.Dead => EngineResult.Reject(action, state, EngineRejectionReason.TargetIsDead,
                $"{target.Name} is dead."),
            _ => EngineResult.Reject(action, state, EngineRejectionReason.AbilityTargetNotAvailable,
                $"{target.Name} cannot be reached right now.")
        };

    private static ImmutableArray<InventoryItem> RemoveFirst(Character actor, InventoryItem item)
    {
        for (var i = 0; i < actor.Inventory.Length; i++)
        {
            if (ReferenceEquals(actor.Inventory[i], item) || actor.Inventory[i] == item)
            {
                return actor.Inventory.RemoveAt(i);
            }
        }

        return actor.Inventory;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The whole injury rule. Deliberately one condition: a wound is recorded when a blow first drops
    /// a character to half health or below, or when it kills them.
    /// </summary>
    private Injury? DetermineInjury(Character target, Weapon weapon, int damage, int healthBefore, int healthAfter, bool died)
    {
        if (damage <= 0)
        {
            return null;
        }

        if (died)
        {
            return new Injury($"Mortal wound inflicted by {weapon.Name}", CurrentRound);
        }

        var half = target.MaxHealth / 2;
        if (healthBefore > half && healthAfter <= half)
        {
            return new Injury($"Deep wound inflicted by {weapon.Name}", CurrentRound);
        }

        return null;
    }
}
