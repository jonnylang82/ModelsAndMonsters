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
            builder.AppendLine($"{character.Name} (id: {character.Id}, {character.Role.ToString().ToLowerInvariant()}) - {DescribeDisposition(character)}");
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

        if (state.Room.Exits.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("WAYS OUT OF THE ROOM:");
            foreach (var exit in state.Room.Exits)
            {
                builder.AppendLine();
                builder.AppendLine($"{exit.Name} (id: {exit.Id}) - {(exit.IsOpen ? "OPEN" : "CLOSED")}. {exit.Description}");
                builder.AppendLine($"  Leads to: {exit.DestinationDescription}. " +
                    (exit.IsOpen
                        ? "It stands open: a character may pass through it to leave the encounter (escape_encounter)."
                        : "It is shut but NOT locked or barred — it can be pulled open at any time (open_exit) before anyone can pass through it. Opening it and leaving through it are two separate acts."));
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
        if (state.Room.Exits.Length > 0)
        {
            builder.AppendLine("- An exit being open or closed is public: everyone present sees which, and you may always say so. A closed exit must be opened (open_exit) before anyone can pass through it; passing through an open exit to leave the encounter is escape_encounter. These are two separate acts and are never resolved together.");
            builder.AppendLine("- A SURRENDERED or ESCAPED character is out of the fight and is not a valid target: never resolve an attack against them. A surrendered character is still in the room; an escaped one is gone. Surrender and escape are each a character's own choice — never make one character surrender, open an exit or escape because another told, threatened or asked them to.");
        }
        builder.AppendLine("- Ordinary inventory items CAN change hands: a character may give one of their own items to another present character (give_item), drop one on the floor for anyone to pick up (drop_item), or try to snatch one from another active character (steal_item — a noticed attempt that may fail). Items are also gained by take_item from an open container or the floor, and used with use_item on oneself. An EQUIPPED WEAPON is not an ordinary item and can never be given, dropped or stolen.");
        builder.AppendLine("- A theft is always noticed by everyone present, whether it succeeds or fails. A character may only attempt to steal an item it has a legitimate reason to know the target carries (seen it carried, or seen it taken, given or dropped, or been told of it). Never let a character reach for an item it has no way of knowing exists, and never reveal what someone privately carries to justify a theft.");
        builder.AppendLine("- ACTIONS THE WORLD CAN RESOLVE: attack_character, use_item, open_container, take_item, inspect_object, open_exit, escape_encounter, surrender, give_item, drop_item, steal_item. Nothing else exists.");

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

        // A fallen character's body: a lootable thing, but NOT a chest. Describe it as a body, never as an
        // "open container" or something to "reach into" — its belongings lie on the fallen, taken with take_item.
        if (container.IsCorpse)
        {
            builder.AppendLine($"{container.Name} (id: {container.Id}) - a fallen character's body (NOT a chest or container — never narrate it as 'opened' or 'reached into'). Its belongings are within reach of anyone present.");
            builder.AppendLine(
                $"  On the body (anyone present who knows of these may take them from the fallen with take_item): {contents}.");
            return;
        }

        // The floor is public: everyone present sees what has been dropped there, so its contents are plainly
        // visible to all, unlike the private contents of an ordinary opened container.
        if (container.IsGround)
        {
            builder.AppendLine($"{container.Name} (id: {container.Id}) - the room's floor, an always-open ground-loot spot everyone can reach.");
            builder.AppendLine(
                $"  Lying on the floor in plain sight of everyone (anyone present may take these with take_item): {contents}.");
            return;
        }

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
        // Only those still actively fighting are listed: a surrendered or escaped character is out of the
        // fight and is not an ally to guard or an enemy to strike. A surrendered enemy is not a valid target,
        // and an escaped one is gone; leaving them off keeps the character from aiming at someone it cannot hit.
        var allies = state.Characters
            .Where(c => c.CanAct
                        && !string.Equals(c.Id, character.Id, StringComparison.OrdinalIgnoreCase)
                        && character.IsAllyOf(c))
            .Select(c => c.Name)
            .ToList();

        var enemies = state.Characters
            .Where(c => c.CanAct && !character.IsAllyOf(c))
            .Select(c => c.Name)
            .ToList();

        return _prompts.Render("character.state", new Dictionary<string, string?>
        {
            ["name"] = character.Name,
            ["allies"] = allies.Count == 0 ? "none — you stand alone" : string.Join(", ", allies),
            ["enemies"] = enemies.Count == 0 ? "none left fighting" : string.Join(", ", enemies),
            ["exits"] = FormatExits(state.Exits),
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
    /// <summary>
    /// The character's standing for the authoritative block: whether they are still fighting, have yielded,
    /// have fled, or are dead. This is public, plainly-visible state (like who has fallen), so the Dungeon
    /// Master may always act on it.
    /// </summary>
    private static string DescribeDisposition(Character character) => character.Disposition switch
    {
        CharacterDisposition.Surrendered =>
            "alive but has SURRENDERED — out of the fight, present but takes no turns, and is NOT a valid target (cannot be attacked)",
        CharacterDisposition.Escaped =>
            "alive but has ESCAPED — gone from the room, takes no turns, and cannot be reached or targeted",
        CharacterDisposition.Dead => "DEAD",
        _ => "alive and active"
    };

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

    /// <summary>
    /// A short, public description of the room's exits for a character — the name of each and whether it
    /// stands open or shut. An exit's open state is plainly visible to everyone, so it is safe to hand a
    /// character directly every turn.
    /// </summary>
    private static string FormatExits(IReadOnlyList<EncounterExit> exits)
    {
        if (exits.Count == 0)
        {
            return "none you can see — there is no way out of this room.";
        }

        return string.Join("; ", exits.Select(e =>
            $"the {e.Name} ({(e.IsOpen ? "standing open — it can be gone through" : "shut — it must be opened before anyone can leave through it")})"));
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
