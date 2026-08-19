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
            OpenExitAction openExit => ResolveOpenExit(openExit),
            EscapeEncounterAction escape => ResolveEscapeEncounter(escape),
            SurrenderAction surrender => ResolveSurrender(surrender),
            GiveItemAction give => ResolveGiveItem(give),
            DropItemAction drop => ResolveDropItem(drop),
            StealItemAction steal => ResolveStealItem(steal),
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

        // Only an active combatant can be struck. A character who is dead, has surrendered, or has escaped
        // is out of the fight, and each is refused with its own reason so the DM can explain it in-world.
        if (!target.IsCombatTarget)
        {
            return target.Disposition switch
            {
                CharacterDisposition.Surrendered => EngineResult.Reject(action, state,
                    EngineRejectionReason.TargetHasSurrendered,
                    $"{target.Name} has surrendered and is no longer a part of the fight."),
                CharacterDisposition.Escaped => EngineResult.Reject(action, state,
                    EngineRejectionReason.TargetHasEscaped,
                    $"{target.Name} has fled the encounter and is no longer here to strike."),
                _ => EngineResult.Reject(action, state, EngineRejectionReason.TargetIsDead,
                    $"{target.Name} is already dead.")
            };
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
            // Death is recorded as an explicit disposition change, not left to derive from health alone, so
            // the authoritative state names it and an orchestration-layer disposition trace can key off it.
            Disposition = died ? CharacterDisposition.Dead : target.Disposition,
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
                IsCorpse = true,
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
        var after = state.WithExit(opened) with { Version = state.Version + 1 };

        var outcome = new OpenExitOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            ExitId = exit.Id,
            ExitName = exit.Name,
            DestinationDescription = exit.DestinationDescription
        };

        return EngineResult.Accept(action, state, after, outcome);
    }

    /// <summary>
    /// Passes a character through an already-open exit, setting their disposition to
    /// <see cref="CharacterDisposition.Escaped"/> and recording which exit they used. No randomness, no
    /// escape roll, no opportunity attack and no pursuit — an open exit is simply walked through. Their
    /// health, inventory and equipment are untouched; they are alive, just gone.
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
        var after = state.WithCharacter(escaped) with { Version = state.Version + 1 };

        var outcome = new EscapeOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            ExitId = exit.Id,
            ExitName = exit.Name,
            DestinationDescription = exit.DestinationDescription
        };

        return EngineResult.Accept(action, state, after, outcome);
    }

    /// <summary>
    /// Sets an active character's disposition to <see cref="CharacterDisposition.Surrendered"/>. No
    /// randomness and unilateral: it needs no opponent approval, roll or prior demand. It changes only the
    /// disposition — the character keeps their weapon and inventory, and nothing is disarmed or transferred.
    /// </summary>
    private EngineResult ResolveSurrender(SurrenderAction action)
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

        var surrendered = actor with { Disposition = CharacterDisposition.Surrendered };
        var after = state.WithCharacter(surrendered) with { Version = state.Version + 1 };

        var outcome = new SurrenderOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name
        };

        return EngineResult.Accept(action, state, after, outcome);
    }

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

        if (string.Equals(recipient.Id, giver.Id, StringComparison.OrdinalIgnoreCase))
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

        var item = giver.FindItem(action.ItemRef);
        if (item is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ItemNotPossessed,
                $"{giver.Name} is not carrying '{action.ItemRef}'.");
        }

        // One new state carries both halves of the transfer, then a single version increment: the item can
        // never momentarily exist in both inventories or in neither.
        var strippedGiver = giver with { Inventory = RemoveFirst(giver, item) };
        var carryingRecipient = recipient with { Inventory = recipient.Inventory.Add(item) };
        var after = state
            .WithCharacter(strippedGiver)
            .WithCharacter(carryingRecipient) with { Version = state.Version + 1 };

        var outcome = new GiveItemOutcome
        {
            GiverId = giver.Id,
            GiverName = giver.Name,
            RecipientId = recipient.Id,
            RecipientName = recipient.Name,
            ItemId = item.Id,
            ItemName = item.Name
        };

        return EngineResult.Accept(action, state, after, outcome);
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

        var item = actor.FindItem(action.ItemRef);
        if (item is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ItemNotPossessed,
                $"{actor.Name} is not carrying '{action.ItemRef}'.");
        }

        var (roomWithGround, ground) = PlaceOnGround(state.Room, item);
        var strippedActor = actor with { Inventory = RemoveFirst(actor, item) };
        var after = state.WithCharacter(strippedActor) with { Room = roomWithGround, Version = state.Version + 1 };

        var outcome = new DropItemOutcome
        {
            ActorId = actor.Id,
            ActorName = actor.Name,
            ItemId = item.Id,
            ItemName = item.Name,
            GroundContainerName = ground.Name
        };

        return EngineResult.Accept(action, state, after, outcome);
    }

    /// <summary>
    /// Resolves an attempted theft: the current actor trying to take one ordinary inventory item from another
    /// active character. This is the one inventory action that consults randomness — exactly one seeded d100
    /// draw against the configured base theft chance decides it. The attempt is always publicly noticed and
    /// consumes the turn whether it succeeds or fails; on success the item moves atomically from target to
    /// thief, on failure ownership is unchanged (and, like a missed attack, the state and its version are
    /// untouched). Validation runs before the draw, so a rejected attempt consults no randomness at all.
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

        if (string.Equals(target.Id, thief.Id, StringComparison.OrdinalIgnoreCase))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.TargetIsSelf,
                $"{thief.Name} cannot steal from themselves.");
        }

        // Both must be active and present: a surrendered, escaped or dead character is out of the fight and
        // cannot be pickpocketed in v0.6.
        if (!target.CanAct)
        {
            return target.Disposition switch
            {
                CharacterDisposition.Surrendered => EngineResult.Reject(action, state,
                    EngineRejectionReason.TargetHasSurrendered,
                    $"{target.Name} has surrendered and is no longer part of the fight to steal from."),
                CharacterDisposition.Escaped => EngineResult.Reject(action, state,
                    EngineRejectionReason.TargetHasEscaped,
                    $"{target.Name} has fled the encounter and is no longer here."),
                _ => EngineResult.Reject(action, state, EngineRejectionReason.TargetIsDead,
                    $"{target.Name} is dead.")
            };
        }

        if (NamesEquippedWeapon(target, action.ItemRef))
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.EquippedWeaponCannotBeTransferred,
                $"{target.Name}'s {target.Weapon!.Name} is their equipped weapon and cannot be stolen in the middle of a fight.");
        }

        var item = target.FindItem(action.ItemRef);
        if (item is null)
        {
            return EngineResult.Reject(action, state, EngineRejectionReason.ItemNotPossessed,
                $"{target.Name} is not carrying '{action.ItemRef}'.");
        }

        // Exactly one draw, taken only after every validation has passed. The base chance is a flat
        // configurable number; v0.6 applies no modifiers, but the record is shaped to carry them.
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
            ItemName = item.Name,
            BaseChance = baseChance,
            Modifiers = modifiers,
            EffectiveChance = effectiveChance,
            Roll = roll,
            Succeeded = succeeded
        };

        if (!succeeded)
        {
            // Nothing changes hands. Like a missed attack, the state and its version are untouched, but the
            // turn is spent, so the attempt is an accepted action whose before-state equals its after-state.
            return EngineResult.Accept(action, state, state, outcome, [draw]);
        }

        var strippedTarget = target with { Inventory = RemoveFirst(target, item) };
        var carryingThief = thief with { Inventory = thief.Inventory.Add(item) };
        var after = state
            .WithCharacter(strippedTarget)
            .WithCharacter(carryingThief) with { Version = state.Version + 1 };

        return EngineResult.Accept(action, state, after, outcome, [draw]);
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
    /// ordinary inventory item. Equipped weapons live outside the inventory and are never transferable in
    /// v0.6, so a reference to one is refused with its own reason rather than a generic "not carrying it".
    /// </summary>
    private static bool NamesEquippedWeapon(Character character, string itemRef)
    {
        if (character.Weapon is null || string.IsNullOrWhiteSpace(itemRef))
        {
            return false;
        }

        var needle = itemRef.Trim();
        return string.Equals(character.Weapon.Name, needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The shared refusal for an actor who cannot act on the three v0.5 disposition/exit actions. Only an
    /// active character reaches the engine as an actor in normal play (the turn loop skips the rest), so
    /// this is a defensive guard; it still names the specific reason for the record.
    /// </summary>
    private static EngineResult RejectInactiveActor(GameAction action, GameState state, Character actor) =>
        actor.Disposition switch
        {
            CharacterDisposition.Dead => EngineResult.Reject(action, state, EngineRejectionReason.ActorIsDead,
                $"{actor.Name} is dead and cannot act."),
            CharacterDisposition.Surrendered => EngineResult.Reject(action, state, EngineRejectionReason.ActorNotActive,
                $"{actor.Name} has already surrendered and takes no further part in the fight."),
            CharacterDisposition.Escaped => EngineResult.Reject(action, state, EngineRejectionReason.ActorNotActive,
                $"{actor.Name} has already left the encounter."),
            _ => EngineResult.Reject(action, state, EngineRejectionReason.ActorNotActive,
                $"{actor.Name} cannot act right now.")
        };

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
