using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Engine;

/// <summary>
/// The minimal deterministic game engine.
/// </summary>
/// <remarks>
/// <para>
/// Attack rule: the attacker rolls d100; the attack lands when the roll is at or under the attacker's
/// hit chance, otherwise it misses. A landed hit rolls again for a glancing blow (half damage). Base
/// damage is <c>max(0, weaponDamage - targetArmour)</c>. All rolls come from the injected
/// <see cref="IRng"/>, so a run is reproducible from its seed and every roll is traced.
/// </para>
/// <para>Death: health clamps at 0 and a character is dead at 0.</para>
/// <para>Healing: <c>newHealth = min(maxHealth, health + healingAmount)</c> and the item is consumed. No roll.</para>
/// <para>
/// Injuries are recorded by a single deliberately trivial rule (crossing half health, or dying) so
/// that persistent descriptive state demonstrably survives into later prompts. It is not intended to
/// be an injury system.
/// </para>
/// </remarks>
public sealed class GameEngine : IGameEngine
{
    private readonly IRng _rng;
    private readonly CombatRules _combatRules;
    private GameState _state;

    public GameEngine(GameState initialState, IRng rng, CombatRules combatRules)
    {
        _state = initialState;
        _rng = rng;
        _combatRules = combatRules;
    }

    public GameState State => _state;

    public int CurrentRound { get; set; }

    public EngineResult Execute(GameAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var result = action switch
        {
            AttackCharacterAction attack => ResolveAttack(attack),
            UseItemAction useItem => ResolveUseItem(useItem),
            _ => EngineResult.Reject(action, _state, EngineRejectionReason.UnsupportedAction,
                $"The engine has no handler for action type '{action.ActionType}'.")
        };

        if (result.Accepted)
        {
            _state = result.StateAfter;
        }

        return result;
    }

    private EngineResult ResolveAttack(AttackCharacterAction action)
    {
        var state = _state;

        var attacker = state.Resolve(action.AttackerRef);
        if (attacker is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.AttackerRef}' in the room.");
        }

        if (!attacker.IsAlive)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ActorIsDead,
                $"{attacker.Name} is dead and cannot act.");
        }

        var target = state.Resolve(action.TargetRef);
        if (target is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownTarget,
                $"There is no character called '{action.TargetRef}' in the room.");
        }

        if (string.Equals(target.Id, attacker.Id, StringComparison.OrdinalIgnoreCase))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.TargetIsSelf,
                $"{attacker.Name} cannot attack themselves.");
        }

        if (!target.IsAlive)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.TargetIsDead,
                $"{target.Name} is already dead.");
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

        var weapon = attacker.Weapon;
        var healthBefore = target.Health;

        // The attack is a valid, accepted action; the rolls decide whether it lands and how hard.
        var hitRoll = _rng.RollPercent();
        var hit = hitRoll <= attacker.HitChance;

        if (!hit)
        {
            // A miss changes nothing, but the turn is still spent, so it is an accepted action whose
            // state is unchanged (state before == state after, version untouched).
            var missOutcome = new AttackOutcome
            {
                AttackerId = attacker.Id,
                AttackerName = attacker.Name,
                TargetId = target.Id,
                TargetName = target.Name,
                WeaponName = weapon.Name,
                WeaponDamage = weapon.Damage,
                TargetArmour = target.Armour,
                HitRoll = hitRoll,
                HitChance = attacker.HitChance,
                Hit = false,
                GlancingRoll = null,
                GlancingChance = _combatRules.GlancingBlowChance,
                Glancing = false,
                BaseDamage = 0,
                DamageDealt = 0,
                TargetHealthBefore = healthBefore,
                TargetHealthAfter = healthBefore,
                TargetMaxHealth = target.MaxHealth,
                TargetDied = false
            };

            return EngineResult.Accept(action, state, state, missOutcome);
        }

        var glancingRoll = _rng.RollPercent();
        var glancing = glancingRoll <= _combatRules.GlancingBlowChance;

        var baseDamage = Math.Max(0, weapon.Damage - target.Armour);
        var damage = glancing ? CombatRules.GlancingDamage(baseDamage) : baseDamage;
        var healthAfter = Math.Max(0, healthBefore - damage);
        var died = healthAfter <= 0;

        var injury = DetermineInjury(target, weapon, damage, healthBefore, healthAfter, died);
        var updatedTarget = target with
        {
            Health = healthAfter,
            Injuries = injury is null ? target.Injuries : target.Injuries.Add(injury)
        };

        var after = state.WithCharacter(updatedTarget) with { Version = state.Version + 1 };

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
            HitChance = attacker.HitChance,
            Hit = true,
            GlancingRoll = glancingRoll,
            GlancingChance = _combatRules.GlancingBlowChance,
            Glancing = glancing,
            BaseDamage = baseDamage,
            DamageDealt = damage,
            TargetHealthBefore = healthBefore,
            TargetHealthAfter = healthAfter,
            TargetMaxHealth = target.MaxHealth,
            TargetDied = died,
            InjuryInflicted = injury?.Description
        };

        return EngineResult.Accept(action, state, after, outcome);
    }

    private EngineResult ResolveUseItem(UseItemAction action)
    {
        var state = _state;

        var actor = state.Resolve(action.ActorRef);
        if (actor is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.UnknownActor,
                $"There is no character called '{action.ActorRef}' in the room.");
        }

        if (!actor.IsAlive)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ActorIsDead,
                $"{actor.Name} is dead and cannot act.");
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

            if (!string.Equals(target.Id, actor.Id, StringComparison.OrdinalIgnoreCase))
            {
                return EngineResult.Reject(action, state, EngineRejectionReason.ItemTargetNotSupported,
                    "The engine can only apply an item to the character using it. Using items on other characters is not supported.");
            }
        }

        var item = actor.FindItem(action.ItemRef);
        if (item is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ItemNotPossessed,
                $"{actor.Name} is not carrying '{action.ItemRef}'.");
        }

        if (!item.IsHealingItem)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ItemHasNoSupportedEffect,
                $"The engine has no supported effect for '{item.Name}'.");
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

        var outcome = new HealOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            ItemName = item.Name,
            HealingAmount = healing,
            HealthBefore = healthBefore,
            HealthAfter = healthAfter,
            MaxHealth = actor.MaxHealth
        };

        return EngineResult.Accept(action, state, after, outcome);
    }

    private static System.Collections.Immutable.ImmutableArray<InventoryItem> RemoveFirst(Character actor, InventoryItem item)
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
