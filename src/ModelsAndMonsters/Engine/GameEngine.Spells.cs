using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Engine;

// Bounded 2014 Basic Rules adaptations. See docs/council-rules.md for deliberate differences.
public sealed partial class GameEngine
{
    private int SpellDie(GameAction action, Character actor, Character target, int sides, string purpose,
        List<RngDraw> draws, int modifier = 0, int threshold = 0)
    {
        var before = _rng.DrawCount;
        var roll = _rng.Roll(sides);
        draws.Add(new RngDraw
        {
            Purpose = purpose, ActionType = action.ActionType, ActorId = actor.Id, ActorName = actor.Name,
            TargetId = target.Id, TargetName = target.Name, Sides = sides, RangeMin = 1, RangeMax = sides,
            RawRoll = roll, Threshold = threshold, OutcomeSelected = purpose,
            Modifiers = [$"modifier {modifier}"], Comparison = $"{roll} + {modifier} = {roll + modifier}; threshold {threshold}",
            Result = (roll + modifier).ToString(), Seed = _rng.Seed, SequenceBefore = before, SequenceAfter = _rng.DrawCount
        });
        return roll + modifier;
    }

    private GameState EndConcentration(GameState state, string casterId, string cause, List<StatusEvent> events)
    {
        var removed = state.Statuses.Where(s => s.RequiresConcentration && Same(s.SourceCharacterId, casterId)).ToList();
        foreach (var status in removed) events.Add(new(StatusEventKind.Removed, status, cause));
        return state.WithoutStatuses(removed.Select(s => s.Id));
    }

    private GameState SpellDamageUpkeep(GameState state, GameAction action, Character target, int damage,
        List<StatusEvent> events, List<RngDraw> draws)
    {
        if (damage <= 0) return state;
        var sleep = state.StatusOn(target.Id, StatusEffectKind.Sleeping);
        if (sleep is not null)
        {
            state = state.WithoutStatuses([sleep.Id]);
            events.Add(new(StatusEventKind.Removed, sleep, $"{target.Name} woke because of damage"));
        }
        if (state.Statuses.Any(s => s.RequiresConcentration && Same(s.SourceCharacterId, target.Id)))
        {
            var dc = Math.Max(10, damage / 2);
            if (!state.RequireById(target.Id).CanAct ||
                SpellDie(action, target, target, 20, "spell.concentration-save", draws, target.ConstitutionSaveModifier, dc) < dc)
                state = EndConcentration(state, target.Id, $"{target.Name} lost concentration after damage", events);
        }
        return state;
    }

    private EngineResult ResolveCouncilSpell(UseAbilityAction action, GameState state, Character caster,
        Character target, AbilityDefinition definition, CharacterAbility? held)
    {
        var draws = new List<RngDraw>();
        var events = new List<StatusEvent>();
        var transitions = new List<OfferTransition>();
        var after = state;
        var detail = "";
        var kind = definition.EffectKind;
        if (kind == AbilityEffectKind.Wake)
        {
            var sleep = state.StatusOn(target.Id, StatusEffectKind.Sleeping);
            if (sleep is null) return EngineResult.Reject(action, state, EngineRejectionReason.AbilityTargetNotAllowed,
                $"{target.Name} is not asleep.");
            after = state.WithoutStatuses([sleep.Id]);
            after = ApplyExposure(after, ExposeIfInCover(state, caster.Id), "reached out to wake a companion", events);
            events.Add(new(StatusEventKind.Removed, sleep, $"{caster.Name} shook {target.Name} awake"));
            detail = $"{caster.Name} shook {target.Name} awake without injuring them.";
        }
        else if (kind == AbilityEffectKind.HealingWord)
        {
            if (target.IsConstructOrUndead || target.Health >= target.MaxHealth)
                return EngineResult.Reject(action, state, EngineRejectionReason.AbilityTargetNotAllowed,
                    $"Healing Word cannot help {target.Name}: the target must be wounded, living, and neither construct nor undead.");
            var amount = Math.Max(1, SpellDie(action, caster, target, 4, "spell.healing-word", draws, caster.SpellcastingModifier));
            var health = Math.Min(target.MaxHealth, target.Health + amount);
            after = SpendCharge(state.WithCharacter(target with { Health = health }), caster.Id, definition, held);
            after = after with { Version = state.Version + 1 };
            return EngineResult.Accept(action, state, after, new HealingPrayerOutcome
            {
                CasterId = caster.Id, CasterName = caster.Name, TargetId = target.Id, TargetName = target.Name,
                AbilityName = definition.Name, HealingAmount = amount, HealthBefore = target.Health,
                HealthAfter = health, MaxHealth = target.MaxHealth,
                RemainingUses = after.RequireById(caster.Id).FindAbility(definition.Id)?.RemainingUses ?? 0
            }, rngDraws: draws);
        }
        else
        {
            var statusKind = kind switch
            {
                AbilityEffectKind.Sleep => StatusEffectKind.Sleeping,
                AbilityEffectKind.FaerieFire => StatusEffectKind.FaerieFire,
                _ => StatusEffectKind.DivineFavor
            };
            if (state.StatusOn(target.Id, statusKind) is not null)
                return EngineResult.Reject(action, state, EngineRejectionReason.AbilityAlreadyActive,
                    $"{target.Name} is already affected by {definition.Name}.");
            var applies = true;
            if (kind == AbilityEffectKind.Sleep)
            {
                var power = Enumerable.Range(0, 5).Sum(_ => SpellDie(action, caster, target, 8, "spell.sleep-power", draws));
                applies = !target.ImmuneToSleep && power >= target.Health;
                detail = applies ? $"{target.Name} fell asleep, not dead or surrendered. Damage or a waking action ends the slumber."
                    : $"{target.Name} did not fall asleep. The spell was spent but caused no damage or other change.";
            }
            else if (kind == AbilityEffectKind.FaerieFire)
            {
                // A new concentration spell replaces the old one even if the target saves.
                after = EndConcentration(after, caster.Id, $"{caster.Name} cast a new concentration spell", events);
                applies = SpellDie(action, caster, target, 20, "spell.faerie-fire-save", draws,
                    target.DexteritySaveModifier, caster.SpellSaveDC) < caster.SpellSaveDC;
                detail = applies ? $"{target.Name} is outlined in light; attacks against them gain +15 to hit while {caster.Name} concentrates."
                    : $"{target.Name} resisted Faerie Fire. The spell was spent but the target is unchanged.";
            }
            else
            {
                after = EndConcentration(after, caster.Id, $"{caster.Name} cast a new concentration spell", events);
                detail = $"{caster.Name}'s weapon hits now add 1d4 radiant damage while concentrating; no attack happened this turn.";
            }
            if (applies)
            {
                if (kind == AbilityEffectKind.Sleep)
                {
                    after = EndConcentration(after, target.Id, $"{target.Name} fell asleep", events);
                    // A sleeping guardian cannot continue to intercept attacks.
                    var guards = after.Statuses.Where(s => s.Kind is StatusEffectKind.Guarding or StatusEffectKind.Guarded
                        && Same(s.SourceCharacterId, target.Id)).ToList();
                    foreach (var guard in guards) events.Add(new(StatusEventKind.Removed, guard, "guardian fell asleep"));
                    after = after.WithoutStatuses(guards.Select(s => s.Id));
                }
                var status = NewStatus(statusKind, caster.Id, target.Id, kind == AbilityEffectKind.FaerieFire ? 15 : 0,
                    StatusExpiryRule.WhileConditionHolds, definition.Id) with
                {
                    ExpiresAtRound = CurrentRound + 10,
                    RequiresConcentration = kind is AbilityEffectKind.FaerieFire or AbilityEffectKind.DivineFavor
                };
                after = after.WithStatus(status);
                events.Add(new(StatusEventKind.Applied, status, detail));
            }
        }
        if (kind is AbilityEffectKind.Sleep or AbilityEffectKind.FaerieFire)
            (after, _) = ApplyHostility(after, caster, [target.Id], transitions);
        after = SpendCharge(after, caster.Id, definition, held);
        after = after with { Version = state.Version + 1 };
        return EngineResult.Accept(action, state, after, new SpellEffectOutcome
        {
            CasterId = caster.Id, CasterName = caster.Name, TargetId = target.Id, TargetName = target.Name,
            AbilityName = definition.Name, EffectSummary = detail
        }, rngDraws: draws, statusEvents: events, offerTransitions: transitions);
    }
}
