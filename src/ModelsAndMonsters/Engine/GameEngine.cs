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
            OpenContainerAction open => ResolveOpenContainer(open),
            TakeItemAction take => ResolveTakeItem(take),
            InspectObjectAction inspect => ResolveInspectObject(inspect),
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
        var draws = new List<RngDraw>(2);

        // The attack is a valid, accepted action; the rolls decide whether it lands and how hard.
        var hitSequenceBefore = _rng.DrawCount;
        var hitRoll = _rng.RollPercent();
        var hit = hitRoll <= attacker.HitChance;
        draws.Add(new RngDraw
        {
            Purpose = "attack.hit-check",
            ActionType = action.ActionType,
            ActorId = attacker.Id,
            ActorName = attacker.Name,
            TargetId = target.Id,
            TargetName = target.Name,
            OutcomeSelected = "hit or miss",
            Sides = 100,
            RangeMin = 1,
            RangeMax = 100,
            RawRoll = hitRoll,
            Threshold = attacker.HitChance,
            Comparison = $"roll {hitRoll} {(hit ? "<=" : ">")} hit chance {attacker.HitChance}",
            Result = hit ? "hit" : "miss",
            Seed = _rng.Seed,
            SequenceBefore = hitSequenceBefore,
            SequenceAfter = _rng.DrawCount
        });

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

            return EngineResult.Accept(action, state, state, missOutcome, draws);
        }

        var glancingSequenceBefore = _rng.DrawCount;
        var glancingRoll = _rng.RollPercent();
        var glancing = glancingRoll <= _combatRules.GlancingBlowChance;
        draws.Add(new RngDraw
        {
            Purpose = "attack.glancing-check",
            ActionType = action.ActionType,
            ActorId = attacker.Id,
            ActorName = attacker.Name,
            TargetId = target.Id,
            TargetName = target.Name,
            OutcomeSelected = "glancing or solid",
            Sides = 100,
            RangeMin = 1,
            RangeMax = 100,
            RawRoll = glancingRoll,
            Threshold = _combatRules.GlancingBlowChance,
            Comparison = $"roll {glancingRoll} {(glancing ? "<=" : ">")} glancing chance {_combatRules.GlancingBlowChance}",
            Result = glancing ? "glancing" : "solid",
            Seed = _rng.Seed,
            SequenceBefore = glancingSequenceBefore,
            SequenceAfter = _rng.DrawCount
        });

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

        // A character who dies carrying items leaves them as a lootable, already-open corpse container in
        // the room, so nothing a character was holding becomes permanently unreachable. This composes with
        // the existing container system: others loot it with the ordinary take_item action. No randomness.
        Container? corpse = null;
        GameState after;
        if (died && updatedTarget.Inventory.Length > 0)
        {
            corpse = new Container
            {
                Id = $"corpse-{updatedTarget.Id}",
                Name = $"{updatedTarget.Name}'s body",
                Description = $"The fallen body of {updatedTarget.Name}, its belongings within reach.",
                IsOpen = true,
                Contents = updatedTarget.Inventory
            };

            var strippedTarget = updatedTarget with { Inventory = [] };
            var roomWithCorpse = state.Room with { Objects = state.Room.Objects.Add(corpse) };
            after = state.WithCharacter(strippedTarget) with { Room = roomWithCorpse, Version = state.Version + 1 };
        }
        else
        {
            after = state.WithCharacter(updatedTarget) with { Version = state.Version + 1 };
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
            InjuryInflicted = injury?.Description,
            CorpseContainerName = corpse?.Name,
            DroppedItems = corpse is null ? [] : [.. corpse.Contents.Select(i => i.Name)]
        };

        return EngineResult.Accept(action, state, after, outcome, draws);
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

        if (!actor.IsAlive)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ActorIsDead,
                $"{actor.Name} is dead and cannot act.");
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
        var after = state.WithContainer(opened) with { Version = state.Version + 1 };

        var outcome = new OpenContainerOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            ContainerId = container.Id,
            ContainerName = container.Name,
            RevealedContents = [.. opened.Contents.Select(i => i.Name)]
        };

        return EngineResult.Accept(action, state, after, outcome);
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

        if (!actor.IsAlive)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ActorIsDead,
                $"{actor.Name} is dead and cannot act.");
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

        var (item, ambiguousItem) = ResolveContainedItem(container, action.ItemRef);
        if (ambiguousItem)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ItemReferenceAmbiguous,
                $"'{action.ItemRef}' matches more than one thing inside the {container.Name}; it is not clear which is meant.");
        }

        if (item is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ItemNotInContainer,
                $"There is no '{action.ItemRef}' inside the {container.Name}.");
        }

        var index = container.Contents.IndexOf(item);
        var emptiedContainer = container with { Contents = container.Contents.RemoveAt(index) };
        var carryingActor = actor with { Inventory = actor.Inventory.Add(item) };

        // One new state carries both halves of the transfer, then a single version increment.
        var after = state
            .WithContainer(emptiedContainer)
            .WithCharacter(carryingActor) with { Version = state.Version + 1 };

        var outcome = new TakeItemOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            ContainerId = container.Id,
            ContainerName = container.Name,
            ItemId = item.Id,
            ItemName = item.Name,
            RemainingContents = [.. emptiedContainer.Contents.Select(i => i.Name)]
        };

        return EngineResult.Accept(action, state, after, outcome);
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

        if (!actor.IsAlive)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ActorIsDead,
                $"{actor.Name} is dead and cannot act.");
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
                ? [.. container.Contents.Select(i => i.Name)]
                : []
        };

        // No mutation and no version change: inspection is observation, not action-on-the-world.
        return EngineResult.Accept(action, state, state, outcome);
    }

    /// <summary>
    /// Resolves an item reference against a container's contents, reporting ambiguity rather than
    /// guessing — the same discipline the engine applies to characters and objects.
    /// </summary>
    private static (InventoryItem? Item, bool Ambiguous) ResolveContainedItem(Container container, string itemRef)
    {
        if (string.IsNullOrWhiteSpace(itemRef))
        {
            return (null, false);
        }

        var needle = itemRef.Trim();

        var byId = container.Contents.FirstOrDefault(i => string.Equals(i.Id, needle, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return (byId, false);
        }

        var byName = container.Contents
            .Where(i => string.Equals(i.Name, needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return byName.Count switch
        {
            0 => (null, false),
            1 => (byName[0], false),
            _ => (null, true)
        };
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
