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

        var duplicateContainerId = scenario.Room.Containers
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateContainerId is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate container id '{duplicateContainerId.Key}' in scenario '{scenario.Id}'.");
        }

        var room = new Room(
            scenario.Room.Id,
            scenario.Room.Name,
            scenario.Room.Description,
            [.. scenario.Room.Features])
        {
            Objects = [.. scenario.Room.Containers.Select(ToContainer)]
        };

        var characters = scenario.Characters.Select(ToCharacter).ToImmutableArray();

        return new GameState
        {
            Room = room,
            Characters = characters,
            Version = 0
        };
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
            Weapon = definition.Weapon is null ? null : new Weapon(definition.Weapon.Name, definition.Weapon.Damage),
            Inventory = [.. definition.Inventory.Select(ToItem)],
            Injuries = [.. definition.Injuries.Select(text => new Injury(text))],
            Abilities = [.. definition.Abilities]
        };
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

    private static InventoryItem ToItem(ItemDefinition item) => new(
        string.IsNullOrWhiteSpace(item.Id) ? item.Name : item.Id,
        item.Name,
        item.Description,
        item.HealingAmount);
}
