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
            builder.AppendLine("Scenery (descriptive only — the world cannot resolve any interaction with these):");
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
            builder.AppendLine($"  Condition: {DescribeCondition(character)}");
            builder.AppendLine($"  Currently holding: {FormatWeapon(character.Weapon)}");
            builder.AppendLine($"  Carrying: {FormatInventory(character.Inventory)}");
            builder.AppendLine($"  Injuries: {FormatInjuries(character.Injuries)}");
            builder.AppendLine($"  Abilities: {(character.Abilities.Length == 0 ? "none" : string.Join(", ", character.Abilities))}");
        }

        if (state.Room.Objects.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("OBJECTS IN THE ROOM:");
            foreach (var worldObject in state.Room.Objects)
            {
                builder.AppendLine();
                AppendObject(builder, worldObject);
            }
        }

        builder.AppendLine();
        builder.AppendLine("STATE NOTES:");
        builder.AppendLine("- Condition is already a description, not a number. There are no hit points, health totals or armour values to reveal — state condition only in words.");
        builder.AppendLine("- There is no position, distance, facing or movement state. No character has a location; everyone is already within reach of everyone else. Do not describe or track distance, approaching, or backing away.");
        builder.AppendLine("- The only state that exists is what is listed above: each character's condition, the weapon they hold, what they carry, their injuries, and the room's objects.");
        if (state.Room.Objects.OfType<Container>().Any(c => !c.IsOpen))
        {
            builder.AppendLine("- A CLOSED container hides its contents. Its listed contents are authoritative knowledge for you alone: never reveal, name, hint at, or narrate what is inside a closed container, even if a character asks directly.");
        }
        if (state.Room.Objects.OfType<Container>().Any())
        {
            builder.AppendLine("- A container being open or closed is public: everyone in the room sees which, and you may always say so. Its contents are not. Opening does NOT make them public: only the character who opened it, who knew what it held from before the fight, who has since inspected it while open, who saw an item carried out of it, or who was told, knows what is inside. Being in the room is not enough. When you answer or adjudicate for a character, you are told exactly what THAT character knows; never hand them contents they have not discovered, even for an open container.");
            builder.AppendLine("- An exterior marking on a container is legible only to a character who spends a turn inspecting it closely. Never reveal a marking in an answer or narration; it is discovered only through inspection, and then only by the one inspecting.");
        }
        builder.AppendLine("- ACTIONS THE WORLD CAN RESOLVE: attack_character, use_item, open_container, take_item, inspect_object. Nothing else exists.");

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Writes one world object into the authoritative block. A closed container shows its contents as
    /// knowledge for the Dungeon Master only; an open one shows them as plainly visible to everyone.
    /// </summary>
    private static void AppendObject(StringBuilder builder, WorldObject worldObject)
    {
        if (worldObject is not Container container)
        {
            builder.AppendLine($"{worldObject.Name} (id: {worldObject.Id}) - {worldObject.Description}");
            return;
        }

        var contents = container.Contents.Length == 0
            ? "nothing"
            : string.Join(", ", container.Contents.Select(i => i.Name));

        if (container.IsOpen)
        {
            builder.AppendLine($"{container.Name} (id: {container.Id}) - OPEN.");
            builder.AppendLine(
                $"  Authoritative contents (a character knows these ONLY if they opened it, inspected it while open, " +
                $"saw an item taken from it, or were told — being open does NOT reveal them to everyone): {contents}.");
        }
        else
        {
            builder.AppendLine($"{container.Name} (id: {container.Id}) - CLOSED. Nobody in the room can see inside it.");
            builder.AppendLine($"  Authoritative contents (FOR YOU ONLY — do not reveal to anyone who has not discovered them): {contents}.");
        }

        if (container.ExteriorClue is { } clue)
        {
            builder.AppendLine(
                "  Exterior marking (FOR YOU ONLY — cannot be read from the general room description; legible only " +
                $"to a character who spends a turn inspecting it closely, and then only to that character): {clue}");
        }
    }

    /// <summary>
    /// The exact self-knowledge block a character receives at the start of its turn, including an explicit
    /// list of who is still alive on each side. Naming the current living allies and enemies every turn is
    /// deliberate: a character that loses track of its own side strikes a friend, and the Dungeon Master
    /// translates that confusion faithfully. The list is dynamic — only the living appear — so it also
    /// tells the character who has already fallen.
    /// </summary>
    public string FormatCharacterSelfState(Character character, GameState state)
    {
        var allies = state.Characters
            .Where(c => c.IsAlive
                        && !string.Equals(c.Id, character.Id, StringComparison.OrdinalIgnoreCase)
                        && character.IsAllyOf(c))
            .Select(c => c.Name)
            .ToList();

        var enemies = state.Characters
            .Where(c => c.IsAlive && !character.IsAllyOf(c))
            .Select(c => c.Name)
            .ToList();

        return _prompts.Render("character.state", new Dictionary<string, string?>
        {
            ["name"] = character.Name,
            ["allies"] = allies.Count == 0 ? "none — you stand alone" : string.Join(", ", allies),
            ["enemies"] = enemies.Count == 0 ? "none left standing" : string.Join(", ", enemies),
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
    }

    /// <summary>
    /// Turns exact health into a descriptive band. The Dungeon Master narrates wounds and never needs
    /// raw numbers; handing it a band instead of a total makes it structurally unable to leak one, which
    /// is more reliable than instructing a small model not to. The engine keeps the exact value.
    /// </summary>
    private static string DescribeCondition(Character character)
    {
        if (!character.IsAlive)
        {
            return "dead";
        }

        var fraction = character.MaxHealth <= 0 ? 1.0 : (double)character.Health / character.MaxHealth;
        return fraction switch
        {
            >= 0.999 => "unhurt",
            >= 0.75 => "lightly wounded",
            >= 0.45 => "wounded",
            >= 0.20 => "badly wounded",
            _ => "barely standing, close to death"
        };
    }

    private static string FormatWeapon(Weapon? weapon) =>
        weapon is null ? "none" : weapon.Name;

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
