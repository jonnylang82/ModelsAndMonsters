using System.Text;
using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Prompts;

/// <summary>
/// Renders authoritative state as text for prompts.
/// </summary>
/// <remarks>
/// Two audiences, deliberately different. The Dungeon Master gets everything, including exact numbers.
/// A character gets exact information about itself only, and learns about anyone else purely through
/// the DM's narration.
/// </remarks>
public sealed class WorldStateFormatter
{
    private readonly PromptLibrary _prompts;

    public WorldStateFormatter(PromptLibrary prompts)
    {
        _prompts = prompts;
    }

    /// <summary>The full authoritative snapshot handed to the Dungeon Master before every task.</summary>
    public static string FormatAuthoritativeState(GameState state)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"ROOM: {state.Room.Name}");
        if (!string.IsNullOrWhiteSpace(state.Room.Description))
        {
            builder.AppendLine(state.Room.Description);
        }

        if (state.Room.Features.Length > 0)
        {
            builder.AppendLine("Features present:");
            foreach (var feature in state.Room.Features)
            {
                builder.AppendLine($"- {feature}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("CHARACTERS:");

        foreach (var character in state.Characters)
        {
            builder.AppendLine();
            builder.AppendLine($"{character.Name} (id: {character.Id}, {character.Role.ToString().ToLowerInvariant()}) - {(character.IsAlive ? "alive" : "DEAD")}");
            builder.AppendLine($"  Health: {character.Health} / {character.MaxHealth}");
            builder.AppendLine($"  Armour: {character.Armour}");
            builder.AppendLine($"  Weapon: {FormatWeapon(character.Weapon)}");
            builder.AppendLine($"  Inventory: {FormatInventory(character.Inventory)}");
            builder.AppendLine($"  Injuries: {FormatInjuries(character.Injuries)}");
            builder.AppendLine($"  Abilities: {(character.Abilities.Length == 0 ? "none" : string.Join(", ", character.Abilities))}");
        }

        builder.AppendLine();
        builder.AppendLine("ACTIONS THE WORLD CAN RESOLVE: attack_character, use_item. Nothing else exists.");

        return builder.ToString().TrimEnd();
    }

    /// <summary>The exact self-knowledge block a character receives at the start of its turn.</summary>
    public string FormatCharacterSelfState(Character character) =>
        _prompts.Render("character.state", new Dictionary<string, string?>
        {
            ["name"] = character.Name,
            ["health"] = character.Health.ToString(),
            ["max_health"] = character.MaxHealth.ToString(),
            ["armour"] = character.Armour.ToString(),
            ["injuries"] = FormatBulletList(character.Injuries.Select(i => i.Description)),
            ["weapon"] = character.Weapon is null
                ? "None"
                : $"{character.Weapon.Name}\nDamage: {character.Weapon.Damage}",
            ["inventory"] = FormatBulletList(character.Inventory.Select(FormatItem)),
            ["abilities"] = FormatBulletList(character.Abilities)
        }).TrimEnd();

    private static string FormatWeapon(Weapon? weapon) =>
        weapon is null ? "none" : $"{weapon.Name} (damage {weapon.Damage})";

    private static string FormatInventory(IReadOnlyList<InventoryItem> inventory) =>
        inventory.Count == 0 ? "empty" : string.Join(", ", inventory.Select(FormatItem));

    private static string FormatInjuries(IReadOnlyList<Injury> injuries) =>
        injuries.Count == 0 ? "none" : string.Join("; ", injuries.Select(i => i.Description));

    private static string FormatItem(InventoryItem item) =>
        item.HealingAmount is { } healing ? $"{item.Name} (restores {healing} health)" : item.Name;

    private static string FormatBulletList(IEnumerable<string> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? "- None" : string.Join("\n", list.Select(v => $"- {v}"));
    }
}
