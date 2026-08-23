using System.Collections.Immutable;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Engine;

/// <summary>Turns a configured <see cref="ScenarioDefinition"/> into an initial authoritative state.</summary>
public static class ScenarioFactory
{
    public static GameState CreateInitialState(ScenarioDefinition scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        if (scenario.Characters.Count == 0)
        {
            throw new InvalidOperationException("The scenario must define at least one character.");
        }

        var duplicateId = scenario.Characters
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateId is not null)
        {
            throw new InvalidOperationException($"Duplicate character id '{duplicateId.Key}' in scenario '{scenario.Id}'.");
        }

        // Names, not just ids, must be unique: the Dungeon Master and every character refer to one another
        // by name, and GameState.Resolve accepts a name as an alternative to an id and returns the first
        // match. A scenario with two same-named characters would make that resolution silently ambiguous
        // rather than reporting it, so it is refused here instead, at the one place that can still name
        // the scenario responsible.
        var duplicateName = scenario.Characters
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateName is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate character name '{duplicateName.Key}' in scenario '{scenario.Id}'. " +
                "Every character must be addressable by a unique name.");
        }

        var duplicateContainerId = scenario.Room.Containers
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateContainerId is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate container id '{duplicateContainerId.Key}' in scenario '{scenario.Id}'.");
        }

        var duplicateExitId = scenario.Room.Exits
            .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateExitId is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate exit id '{duplicateExitId.Key}' in scenario '{scenario.Id}'.");
        }

        // Cover shares Room.Objects with containers (v0.9), so an id must be unique across both, not just
        // within its own list — the same discipline ValidateGlobalItemIdentity already applies to items.
        var duplicateObjectId = scenario.Room.Containers.Select(c => c.Id)
            .Concat(scenario.Room.Cover.Select(c => c.Id))
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateObjectId is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate room object id '{duplicateObjectId.Key}' in scenario '{scenario.Id}'.");
        }

        var room = new Room(
            scenario.Room.Id,
            scenario.Room.Name,
            scenario.Room.Description,
            [.. scenario.Room.Features])
        {
            Objects = [.. scenario.Room.Containers.Select(ToContainer).Cast<WorldObject>()
                .Concat(scenario.Room.Cover.Select(ToCover))],
            Exits = [.. scenario.Room.Exits.Select(ToExit)]
        };

        var characters = scenario.Characters.Select(ToCharacter).ToImmutableArray();

        ValidateGlobalItemIdentity(room, characters);

        return new GameState
        {
            Room = room,
            Characters = characters,
            Version = 0
        };
    }

    /// <summary>
    /// Checks that every item id is unique across the whole scenario — not just within the one container or
    /// inventory it was declared in. <see cref="Knowledge.KnowledgeLedger.KnowsItem"/> tracks what a
    /// character knows purely by item id, so two unrelated items sharing an id (most easily reached when
    /// neither is given an explicit id and both default to the same item name) would make discovering one
    /// silently grant knowledge of the other, wherever it is hidden. Weapons are included for the same
    /// completeness, even though an equipped weapon is never itself secret.
    /// </summary>
    private static void ValidateGlobalItemIdentity(Room room, ImmutableArray<Character> characters)
    {
        var seenAt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void CheckAndRecord(string itemId, string location)
        {
            if (seenAt.TryGetValue(itemId, out var firstLocation))
            {
                throw new InvalidOperationException(
                    $"Item id '{itemId}' is used more than once in the scenario (first in {firstLocation}, " +
                    $"again in {location}). Every item needs a scenario-wide unique id — knowledge of an item " +
                    "is tracked by id, so two items sharing one would leak knowledge of one onto the other.");
            }

            seenAt[itemId] = location;
        }

        foreach (var container in room.Objects.OfType<Container>())
        {
            foreach (var item in container.Contents)
            {
                CheckAndRecord(item.Id, $"container '{container.Id}'");
            }
        }

        foreach (var character in characters)
        {
            foreach (var item in character.Inventory)
            {
                CheckAndRecord(item.Id, $"{character.Id}'s inventory");
            }

            if (character.Weapon is not null)
            {
                CheckAndRecord(character.Weapon.Id, $"{character.Id}'s weapon");
            }
        }
    }

    private static Character ToCharacter(CharacterDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            throw new InvalidOperationException($"Character '{definition.Name}' is missing an id.");
        }

        if (!Enum.TryParse<CharacterRole>(definition.Role, ignoreCase: true, out var role))
        {
            throw new InvalidOperationException(
                $"Character '{definition.Id}' has unknown role '{definition.Role}'. Expected Hero or Monster.");
        }

        var maxHealth = definition.MaxHealth > 0
            ? definition.MaxHealth
            : throw new InvalidOperationException($"Character '{definition.Id}' must have a positive MaxHealth.");

        return new Character
        {
            Id = definition.Id,
            Name = definition.Name,
            Role = role,
            Team = string.IsNullOrWhiteSpace(definition.Team) ? Character.DefaultTeamForRole(role) : definition.Team.Trim(),
            MaxHealth = maxHealth,
            Health = Math.Clamp(definition.Health ?? maxHealth, 0, maxHealth),
            Armour = Math.Max(0, definition.Armour),
            HitChance = Math.Clamp(definition.HitChance, 0, 100),
            Fear = FearRules.Clamp(definition.Fear),
            Weapon = ToWeapon(definition),
            Inventory = [.. definition.Inventory.Select(ToItem)],
            Injuries = [.. definition.Injuries.Select(text => new Injury(text))],
            Abilities = ToAbilities(definition)
        };
    }

    /// <summary>
    /// Builds a character's equipped weapon, giving it the scenario's stable id or a slug of its name. The id
    /// matters because an accepted surrender can move a weapon out of a hand and onto the floor, and that
    /// movement has to name one identity rather than a display name.
    /// </summary>
    private static Weapon? ToWeapon(CharacterDefinition definition) =>
        definition.Weapon is null
            ? null
            : new Weapon(definition.Weapon.Name, definition.Weapon.Damage)
            {
                Id = string.IsNullOrWhiteSpace(definition.Weapon.Id)
                    ? Weapon.SlugFor(definition.Weapon.Name)
                    : definition.Weapon.Id.Trim()
            };

    /// <summary>
    /// Resolves a character's configured abilities against the built-in ability book, with their charges full,
    /// and appends Defend — which every active character has, so leaving it out of a scenario cannot make a
    /// character unable to brace. An ability the book does not know is a scenario error, not a silent no-op:
    /// a character whose prompt promises an ability the engine cannot resolve is exactly the boundary problem
    /// v0.7 is closing.
    /// </summary>
    private static ImmutableArray<CharacterAbility> ToAbilities(CharacterDefinition definition)
    {
        var abilities = new List<CharacterAbility>();

        foreach (var reference in definition.Abilities)
        {
            var resolved = AbilityCatalog.Resolve(reference)
                ?? throw new InvalidOperationException(
                    $"Character '{definition.Id}' lists ability '{reference}', which is not in the ability book. " +
                    $"Known abilities: {string.Join(", ", AbilityCatalog.All.Select(a => a.Id))}.");

            if (abilities.Any(a => string.Equals(a.AbilityId, resolved.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Character '{definition.Id}' lists ability '{resolved.Id}' more than once.");
            }

            abilities.Add(CharacterAbility.From(resolved));
        }

        if (!abilities.Any(a => string.Equals(a.AbilityId, AbilityCatalog.DefendId, StringComparison.OrdinalIgnoreCase)))
        {
            abilities.Add(CharacterAbility.From(AbilityCatalog.Defend));
        }

        return [.. abilities];
    }

    private static Container ToContainer(ContainerDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            throw new InvalidOperationException($"Container '{definition.Name}' is missing an id.");
        }

        var duplicateItemId = definition.Contents
            .Select(i => string.IsNullOrWhiteSpace(i.Id) ? i.Name : i.Id)
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateItemId is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate item id '{duplicateItemId.Key}' inside container '{definition.Id}'.");
        }

        return new Container
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = definition.Description,
            IsOpen = definition.IsOpen,
            Contents = [.. definition.Contents.Select(ToItem)],
            ExteriorClue = string.IsNullOrWhiteSpace(definition.ExteriorClue) ? null : definition.ExteriorClue.Trim()
        };
    }

    private static CoverObject ToCover(CoverDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            throw new InvalidOperationException($"Cover object '{definition.Name}' is missing an id.");
        }

        if (definition.MaximumDurability <= 0)
        {
            throw new InvalidOperationException($"Cover object '{definition.Id}' must have a positive MaximumDurability.");
        }

        return new CoverObject
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = definition.Description,
            Capacity = definition.Capacity,
            HitChanceModifier = definition.HitChanceModifier,
            MaximumDurability = definition.MaximumDurability,
            CurrentDurability = Math.Clamp(definition.CurrentDurability ?? definition.MaximumDurability, 0, definition.MaximumDurability),
            Armour = Math.Max(0, definition.Armour)
        };
    }

    private static EncounterExit ToExit(ExitDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            throw new InvalidOperationException($"Exit '{definition.Name}' is missing an id.");
        }

        return new EncounterExit
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = definition.Description,
            IsOpen = definition.IsOpen,
            DestinationDescription = string.IsNullOrWhiteSpace(definition.DestinationDescription)
                ? "Outside the encounter"
                : definition.DestinationDescription.Trim()
        };
    }

    private static InventoryItem ToItem(ItemDefinition item) => new(
        string.IsNullOrWhiteSpace(item.Id) ? item.Name : item.Id,
        item.Name,
        item.Description,
        item.HealingAmount,
        string.IsNullOrWhiteSpace(item.Qualifier) ? null : item.Qualifier.Trim(),
        RestoresAbilityCharge: item.RestoresAbilityCharge);
}
