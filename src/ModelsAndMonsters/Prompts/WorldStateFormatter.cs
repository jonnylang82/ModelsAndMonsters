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
            builder.AppendLine($"  Abilities: {FormatAbilitiesInline(character.Abilities)}");
            builder.AppendLine($"  Status effects: {FormatStatusesInline(state, character.Id)}");
        }

        AppendSurrenderNegotiation(builder, state);

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

        // These notes are about how to READ the snapshot above — what its fields are and are not. The rules
        // of the world itself live once, in the Dungeon Master's constitution, which every call already
        // carries: repeating them here put the same paragraphs in the same request twice, and the state
        // block's notes had grown to 4,877 characters, more than half the block, on every DM call.
        builder.AppendLine();
        builder.AppendLine("HOW TO READ THIS SNAPSHOT:");
        builder.AppendLine("- Condition is already a description, not a number. There are no hit points, health totals or armour values here to reveal — state condition only in words.");
        builder.AppendLine("- There is no position, distance, facing or movement in this state, because the world has none. Never describe or track distance, approaching, or backing away.");
        builder.AppendLine("- Nothing exists that is not listed above. Every character's condition, weapon, belongings, injuries, abilities-with-uses and status effects; every object; every way out; every offer of surrender and every agreement. If it is not here, it is not in the world.");
        if (state.Room.Objects.OfType<Container>().Any())
        {
            builder.AppendLine("- A container's listed contents, and any exterior marking marked FOR YOU ONLY, are yours alone. Open or closed is public; what is inside is not, and opening does not make it so. You are told exactly what the character you are serving knows — never hand them contents or a marking they have not discovered.");
        }
        builder.AppendLine("- ACTIONS THE WORLD CAN RESOLVE: attack_character, use_item, use_ability, defend, open_container, take_item, inspect_object, open_exit, escape_encounter, offer_surrender, accept_surrender, give_item, drop_item, steal_item, intimidate_character, steady_ally. Nothing else exists.");
        builder.AppendLine("- A Scared status is what a face shows, not a number. There is no morale figure here to reveal, and being Scared compels nobody: a frightened character still chooses, and yielding or leaving still take their own actions.");

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Writes the surrender-negotiation state: every offer still awaiting an answer, and the agreements
    /// already struck. Pending offers are public facts everyone present heard, so they are stated plainly
    /// with their stable ids — the id is what an acceptance has to name.
    /// </summary>
    private static void AppendSurrenderNegotiation(StringBuilder builder, GameState state)
    {
        var pending = state.PendingOffers().ToList();
        if (pending.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("SURRENDER OFFERS AWAITING AN ANSWER (public — everyone present heard the terms):");
            foreach (var offer in pending)
            {
                var offerer = state.FindById(offer.OffererId)?.Name ?? offer.OffererId;
                var recipient = state.FindById(offer.RecipientId)?.Name ?? offer.RecipientId;
                builder.AppendLine(
                    $"{offer.Id} (round {offer.CreatedRound}): {offerer} offered to give up the fight to {recipient}, " +
                    $"promising {DescribeTerms(state, offer)}. NOTHING has changed hands; {offerer} is still ACTIVE and " +
                    $"a valid target. Only {recipient} may accept it (accept_surrender, offer '{offer.Id}'), on their " +
                    "own turn; it lapses at the end of that turn otherwise.");
            }
        }

        var resolved = state.SurrenderOffers.Where(o => !o.IsPending).ToList();
        if (resolved.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("SURRENDER OFFERS ALREADY SETTLED (no longer open — never treat one of these as live):");
            foreach (var offer in resolved)
            {
                var offerer = state.FindById(offer.OffererId)?.Name ?? offer.OffererId;
                var recipient = state.FindById(offer.RecipientId)?.Name ?? offer.RecipientId;
                builder.AppendLine(
                    $"- {offer.Id}: {offerer} to {recipient} — {offer.State.ToString().ToUpperInvariant()}" +
                    (offer.ResolutionCause is null ? "." : $" ({offer.ResolutionCause})."));
            }
        }

        if (state.SurrenderAgreements.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("SURRENDERS ACCEPTED (binding — the one who yielded is out of the fight for good):");
            foreach (var agreement in state.SurrenderAgreements)
            {
                var offerer = state.FindById(agreement.OffererId)?.Name ?? agreement.OffererId;
                var accepter = state.FindById(agreement.AcceptedById)?.Name ?? agreement.AcceptedById;
                var tribute = agreement.TransferredItemIds.Length == 0
                    ? "no items"
                    : string.Join(", ", agreement.TransferredItemIds.Select(id => ItemDisplayName(state, id)));
                var weapon = agreement.ForfeitedWeaponId is null
                    ? "no weapon was promised"
                    : $"the {ItemDisplayName(state, agreement.ForfeitedWeaponId)} was forfeited and now lies on the floor";
                builder.AppendLine(
                    $"- {agreement.Id}: {offerer} yielded to {accepter} on round {agreement.AcceptedRound}. " +
                    $"Tribute: {tribute}. Weapon: {weapon}.");
            }
        }
    }

    /// <summary>The exact terms of an offer, named as the state names them, for the DM to read out or bind against.</summary>
    private static string DescribeTerms(GameState state, SurrenderOffer offer)
    {
        var parts = new List<string>();
        if (offer.OfferedItemIds.Length > 0)
        {
            parts.Add(string.Join(", ", offer.OfferedItemIds.Select(id => ItemDisplayName(state, id))));
        }

        if (offer.ForfeitWeapon)
        {
            var weapon = state.FindById(offer.OffererId)?.Weapon?.Name;
            parts.Add(weapon is null ? "the weapon in their hand" : $"their {weapon}");
        }

        return parts.Count == 0 ? "nothing" : string.Join(" and ", parts);
    }

    /// <summary>
    /// Resolves an item or weapon id to the name it is known by, wherever it currently sits — an inventory, a
    /// container, the floor, or a hand. Falls back to the id, so a record never renders as blank.
    /// </summary>
    private static string ItemDisplayName(GameState state, string id)
    {
        foreach (var character in state.Characters)
        {
            var carried = character.Inventory.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            if (carried is not null)
            {
                return carried.DisplayName;
            }

            if (character.Weapon is not null && string.Equals(character.Weapon.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return character.Weapon.Name;
            }
        }

        foreach (var container in state.Room.Objects.OfType<Container>())
        {
            var inside = container.Contents.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            if (inside is not null)
            {
                return inside.Name;
            }
        }

        return id;
    }

    /// <summary>A character's abilities with their remaining uses, on one line, for the authoritative block.</summary>
    private static string FormatAbilitiesInline(IReadOnlyList<CharacterAbility> abilities) =>
        abilities.Count == 0
            ? "none"
            : string.Join("; ", abilities.Select(a => $"{a.AbilityId} ({a.Name}, {a.DescribeUses()})"));

    /// <summary>The live status effects on one character, on one line, naming each source so it is auditable.</summary>
    private static string FormatStatusesInline(GameState state, string characterId)
    {
        var statuses = state.StatusesOn(characterId).ToList();
        if (statuses.Count == 0)
        {
            return "none";
        }

        return string.Join("; ", statuses.Select(s =>
        {
            var source = state.FindById(s.SourceCharacterId)?.Name ?? s.SourceCharacterId;
            // Nerve is nobody's doing but the fight's, so naming a source for it would read as though somebody
            // put it there. It is reported as what it is: a visible state, with no number attached.
            if (s.Kind == StatusEffectKind.Scared)
            {
                return "Scared — visibly afraid and increasingly concerned with survival. It compels nothing: " +
                       "they still choose their own actions, and yielding or leaving still take their own actions";
            }

            var partner = s.Kind is StatusEffectKind.Guarding
                ? PartnerName(state, s)
                : null;
            var about = partner is null ? "" : $" ({partner})";
            return $"{s.Kind}{about} — {s.Describe()} [from {source}]";
        }));
    }

    /// <summary>The other half of a linked status relationship, by name — who is being guarded, or by whom.</summary>
    private static string? PartnerName(GameState state, StatusEffectInstance status)
    {
        if (status.RelationshipId is null)
        {
            return null;
        }

        var other = state.Statuses.FirstOrDefault(s =>
            string.Equals(s.RelationshipId, status.RelationshipId, StringComparison.OrdinalIgnoreCase) && s.Id != status.Id);
        return other is null ? null : state.FindById(other.TargetCharacterId)?.Name ?? other.TargetCharacterId;
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
            ["abilities"] = FormatAbilitiesForSelf(character.Abilities),
            ["morale"] = FormatMoraleForSelf(character),
            ["statuses"] = FormatStatusesForSelf(character, state),
            ["offers"] = FormatOffersForSelf(character, state)
        }).TrimEnd();
    }

    /// <summary>
    /// A character's own abilities, in their own terms, with what is left of each. Handed to them every turn so
    /// they never have to guess whether a limited ability is spent — a model that guesses wrong burns its turn
    /// on an attempt the engine refuses.
    /// </summary>
    private static string FormatAbilitiesForSelf(IReadOnlyList<CharacterAbility> abilities)
    {
        if (abilities.Count == 0)
        {
            return "- None beyond what anyone can do with a weapon in hand.";
        }

        return string.Join("\n", abilities.Select(a =>
        {
            var definition = AbilityCatalog.Find(a.AbilityId);
            var spent = a.HasChargeLeft ? "" : " — SPENT, you cannot use it again in this fight";
            var uses = a.RemainingUses is null ? "as often as you like" : a.DescribeUses();
            var what = definition is null ? "" : $" {definition.Description}";
            return $"- {a.Name} ({uses}{spent}).{what}";
        }));
    }

    /// <summary>
    /// A character's own nerve, as a plain scale and — once they are Scared — a strong but explicitly
    /// non-binding pull toward staying alive.
    /// </summary>
    /// <remarks>
    /// The exact number goes only here, to the one person entitled to it. The instruction is deliberately
    /// worded as pressure rather than command: fear in v0.8 must never choose an action, and a line that
    /// said "you flee" would hand the mechanic the very agency the design is trying to keep with the model.
    /// The options it lists are the ones the world can actually resolve, so a frightened character is not
    /// pushed toward attempts the engine will refuse.
    /// </remarks>
    private static string FormatMoraleForSelf(Character character)
    {
        var scale = $"{character.Fear} out of {FearRules.Maximum}";
        if (!character.IsScared)
        {
            return character.Fear == FearRules.Minimum
                ? $"Steady. Fear {scale} — nothing has shaken you yet."
                : $"Shaken, but holding. Fear {scale}.";
        }

        return $"SCARED. Fear {scale}.\n" +
               "You are scared and increasingly concerned with survival. Seriously consider escaping, " +
               "surrendering, defending, seeking reassurance from a companion, or otherwise protecting your " +
               "life — but the choice remains yours, and you may still fight if that is what you decide. " +
               "Everyone in the room can see it on you.";
    }

    /// <summary>
    /// The live status effects on this character, phrased for them. These are mechanical facts the world is
    /// enforcing, so a character is told them plainly rather than left to infer them from narration.
    /// </summary>
    private static string FormatStatusesForSelf(Character character, GameState state)
    {
        var lines = new List<string>();

        foreach (var status in state.StatusesOn(character.Id))
        {
            var source = state.FindById(status.SourceCharacterId)?.Name ?? status.SourceCharacterId;
            lines.Add(status.Kind switch
            {
                StatusEffectKind.Guarding =>
                    $"- You are standing over {PartnerName(state, status) ?? "a companion"}: the next blow an enemy " +
                    "aims at them will fall on you instead. It lasts until your next turn begins, or until that one blow.",
                StatusEffectKind.Guarded =>
                    $"- {source} is standing over you: the next blow an enemy aims at you will fall on {source} instead.",
                StatusEffectKind.Rallied =>
                    $"- {source} has steadied you: your next attack is markedly more likely to land. It is used up by " +
                    "that attack, land or miss.",
                StatusEffectKind.OffBalance =>
                    $"- {source} has left you off balance: your next attack is markedly less likely to land. It is used " +
                    "up by that attack, land or miss.",
                StatusEffectKind.Defending =>
                    "- Your guard is up: the next blow that lands on you will do less harm. It falls away when your " +
                    "next turn begins.",
                // Nerve has its own block above, with the exact figure. Repeating it here would say the same
                // thing twice in one prompt and invite the character to narrate a status rather than a feeling.
                StatusEffectKind.Scared =>
                    "- Your nerve has gone, and it shows. Anyone here can see you are afraid.",
                _ => $"- {status.Describe()}"
            });
        }

        return lines.Count == 0 ? "- Nothing is affecting you right now." : string.Join("\n", lines);
    }

    /// <summary>
    /// The surrender offers this character is party to, phrased for them: one they have made and is awaiting an
    /// answer, and any made to them that they alone may take up. Offers are public, so nothing here is hidden
    /// from anybody — but a character has to know the terms and whose decision it is.
    /// </summary>
    private static string FormatOffersForSelf(Character character, GameState state)
    {
        var lines = new List<string>();

        foreach (var offer in state.PendingOffersTo(character.Id))
        {
            var offerer = state.FindById(offer.OffererId)?.Name ?? offer.OffererId;
            lines.Add(
                $"- {offerer} has offered to give up the fight to YOU, promising {DescribeTerms(state, offer)}. " +
                "Nothing has changed hands and they are still fighting. You alone can take it up, and only this " +
                "turn — say plainly that you accept their terms, and the world will hand you what was promised and " +
                "put them out of the fight. Do anything else and the offer lapses.");
        }

        if (state.PendingOfferFrom(character.Id) is { } mine)
        {
            var recipient = state.FindById(mine.RecipientId)?.Name ?? mine.RecipientId;
            lines.Add(
                $"- You have offered to give up the fight to {recipient}, promising {DescribeTerms(state, mine)}. " +
                "It is not settled: you have handed over nothing, you still hold what you carry, and you can still " +
                $"be struck. It is {recipient}'s decision, on their turn.");
        }

        // An offer that came to nothing has to be said out loud, or the offerer waits for an answer that
        // already came. In a live v0.7 run a character offered terms in round 4, was refused in the same
        // round, and then spent rounds 9, 10 and 11 doing nothing but bracing "while Vark considers my
        // offer" — her self-state had silently gone back to "you have offered none", and the one narration
        // that told her had long since been summarised out of her history. State that contradicts a stale
        // belief has to contradict it explicitly; going quiet reads as "still waiting".
        else if (LastResolvedOfferFrom(character.Id, state) is { } settled)
        {
            var recipient = state.FindById(settled.RecipientId)?.Name ?? settled.RecipientId;
            lines.Add(settled.State switch
            {
                SurrenderOfferState.Rejected =>
                    $"- The terms you put to {recipient} are DEAD: they answered with violence instead of taking " +
                    "them. Nothing changed hands, nobody is waiting on anybody, and you are still in this fight. " +
                    "Do not wait for an answer — decide again from where you stand now.",
                SurrenderOfferState.Expired =>
                    $"- The terms you put to {recipient} LAPSED: their turn passed and they did not take them. " +
                    "Nothing changed hands, and you are still in this fight. Do not wait for an answer — you may " +
                    "offer again on better terms, or do something else entirely.",
                _ => $"- Your offer to {recipient} is no longer open."
            });
        }

        foreach (var agreement in state.SurrenderAgreements)
        {
            if (string.Equals(agreement.AcceptedById, character.Id, StringComparison.OrdinalIgnoreCase))
            {
                var offerer = state.FindById(agreement.OffererId)?.Name ?? agreement.OffererId;
                lines.Add($"- You accepted {offerer}'s surrender. They are out of the fight and must not be struck.");
            }
            else if (string.Equals(agreement.OffererId, character.Id, StringComparison.OrdinalIgnoreCase))
            {
                var accepter = state.FindById(agreement.AcceptedById)?.Name ?? agreement.AcceptedById;
                lines.Add($"- You gave up the fight to {accepter} on agreed terms. Your part in the battle is over.");
            }
        }

        return lines.Count == 0 ? "- None. Nobody has offered terms, and you have offered none." : string.Join("\n", lines);
    }

    /// <summary>
    /// The character's own most recently settled offer, when it came to nothing. An accepted offer is not
    /// returned: that is already reported as an agreement, and it ends their part in the fight.
    /// </summary>
    private static SurrenderOffer? LastResolvedOfferFrom(string offererId, GameState state) =>
        state.SurrenderOffers
            .Where(o => string.Equals(o.OffererId, offererId, StringComparison.OrdinalIgnoreCase)
                        && o.State is SurrenderOfferState.Rejected or SurrenderOfferState.Expired)
            .OrderBy(o => o.ResolvedRound ?? 0).ThenBy(o => o.ResolvedTurn ?? 0)
            .LastOrDefault();

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
        item.HealingAmount is { } healing ? $"{item.DisplayName} (restores {healing} health)" : item.DisplayName;

    private static string FormatBulletList(IEnumerable<string> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? "- None" : string.Join("\n", list.Select(v => $"- {v}"));
    }
}
