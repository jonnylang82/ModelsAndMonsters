using System.Text;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Knowledge;

namespace ModelsAndMonsters.Orchestration;

/// <summary>
/// The complete, deterministic set of facts one character's question may be answered from — and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a Dungeon Master model handed the whole authoritative state will, sooner or later,
/// answer from it: naming a closed container's contents, saying a consumed potion is still carried, or
/// promising a tactical manoeuvre the world has no way to resolve. Weaker models did all three. The fix is not
/// a firmer instruction — it is to stop giving the model the material. The projection is built here, from
/// current state plus the asking character's own knowledge ledger, and the DM's job is reduced from
/// <em>deciding</em> what is true to <em>rephrasing</em> what it was handed.
/// </para>
/// <para>
/// <see cref="OmittedHiddenFacts"/> is the one part never sent to the model: it records what the projection
/// deliberately left out, so a run's trace can show that the boundary held rather than asserting it.
/// </para>
/// </remarks>
public sealed record AnswerFacts
{
    public required string AskingCharacterId { get; init; }

    public required string AskingCharacterName { get; init; }

    public required string Question { get; init; }

    /// <summary>The world version the projection was taken at, so a stale answer can be identified.</summary>
    public required int WorldVersion { get; init; }

    /// <summary>Facts about the asker themselves: their own condition, weapon, belongings and statuses.</summary>
    public IReadOnlyList<string> AboutYourself { get; init; } = [];

    /// <summary>What anyone standing in the room can see right now — the current, authoritative, public picture.</summary>
    public IReadOnlyList<string> PlainlyVisible { get; init; } = [];

    /// <summary>What this character has discovered for themselves, as an observation made at a stated moment.</summary>
    public IReadOnlyList<string> KnownFirstHand { get; init; } = [];

    /// <summary>What this character has only been told by somebody else. A claim, never a verified fact.</summary>
    public IReadOnlyList<string> Hearsay { get; init; } = [];

    /// <summary>
    /// The complete closed list of things this character could actually attempt right now. It is what makes a
    /// grounded answer to "what can I do here?" possible without inventing an affordance the engine lacks.
    /// </summary>
    public IReadOnlyList<string> Affordances { get; init; } = [];

    /// <summary>Explicit statements of what this world does not represent at all, so no answer can promise it.</summary>
    public IReadOnlyList<string> WorldLimits { get; init; } = [];

    /// <summary>
    /// What the projection deliberately withheld, for the trace only. This is NEVER included in
    /// <see cref="Render"/> and never reaches a model.
    /// </summary>
    public IReadOnlyList<string> OmittedHiddenFacts { get; init; } = [];

    /// <summary>
    /// The model-facing projection: everything the answer may draw on, and nothing else. It contains no
    /// hidden fact, no other character's private knowledge, and no exact number.
    /// </summary>
    public string Render()
    {
        var builder = new StringBuilder();
        Section(builder, $"WHAT IS TRUE OF {AskingCharacterName.ToUpperInvariant()} RIGHT NOW", AboutYourself);
        Section(builder, "WHAT ANYONE IN THE ROOM CAN SEE RIGHT NOW", PlainlyVisible);
        Section(builder, $"WHAT {AskingCharacterName.ToUpperInvariant()} HAS FOUND OUT FIRST-HAND (an observation, as of the moment stated)", KnownFirstHand);
        Section(builder, $"WHAT {AskingCharacterName.ToUpperInvariant()} HAS ONLY BEEN TOLD (a claim someone made, NOT verified)", Hearsay);
        Section(builder, $"EVERYTHING {AskingCharacterName.ToUpperInvariant()} COULD ACTUALLY ATTEMPT (the complete list — nothing else is possible)", Affordances);
        Section(builder, "WHAT THIS WORLD DOES NOT HAVE AT ALL", WorldLimits);
        return builder.ToString().TrimEnd();
    }

    /// <summary>The withheld facts, rendered for the trace. Never sent to a model.</summary>
    public string RenderOmitted() =>
        OmittedHiddenFacts.Count == 0
            ? "(nothing was withheld: this character could perceive or knew everything relevant.)"
            : string.Join("\n", OmittedHiddenFacts.Select(f => $"- {f}"));

    private static void Section(StringBuilder builder, string title, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        builder.AppendLine(title + ":");
        foreach (var line in lines)
        {
            builder.AppendLine($"- {line}");
        }

        builder.AppendLine();
    }
}

/// <summary>
/// Builds the bounded fact set a character's question may be answered from. Deterministic: no model is
/// involved, and the same state and ledger always produce the same projection.
/// </summary>
public interface IAnswerFactsProjector
{
    AnswerFacts Project(GameState state, string characterId, string question);
}

/// <summary>
/// The deterministic <see cref="AnswerFacts"/> projection.
/// </summary>
/// <remarks>
/// Every line it emits comes from one of three places: current authoritative state that anyone in the room
/// could see, the asking character's own knowledge ledger, or the fixed list of what the engine can actually
/// resolve. Nothing is inferred, nothing is guessed, and anything the character has no basis for is moved to
/// <see cref="AnswerFacts.OmittedHiddenFacts"/> rather than quietly included. This is not a theorem prover
/// over natural language: the question is carried along for the record and for the DM's phrasing, but the
/// projection itself does not try to work out what the question is "about".
/// </remarks>
public sealed class AnswerFactsProjector : IAnswerFactsProjector
{
    private readonly KnowledgeLedger _knowledge;
    private readonly NarrationLog _narrationLog;

    public AnswerFactsProjector(KnowledgeLedger knowledge, NarrationLog narrationLog)
    {
        _knowledge = knowledge;
        _narrationLog = narrationLog;
    }

    public AnswerFacts Project(GameState state, string characterId, string question)
    {
        ArgumentNullException.ThrowIfNull(state);

        var asker = state.FindById(characterId);
        var omitted = new List<string>();

        if (asker is null)
        {
            return new AnswerFacts
            {
                AskingCharacterId = characterId,
                AskingCharacterName = characterId,
                Question = question,
                WorldVersion = state.Version,
                WorldLimits = WorldLimitLines
            };
        }

        return new AnswerFacts
        {
            AskingCharacterId = asker.Id,
            AskingCharacterName = asker.Name,
            Question = question,
            WorldVersion = state.Version,
            AboutYourself = ProjectSelf(state, asker),
            PlainlyVisible = ProjectVisible(state, asker, omitted),
            KnownFirstHand = ProjectKnown(state, asker, omitted),
            Hearsay = ProjectHearsay(asker),
            Affordances = ProjectAffordances(state, asker),
            WorldLimits = WorldLimitLines,
            OmittedHiddenFacts = omitted
        };
    }

    /// <summary>
    /// The asker's own current facts. A character always knows their own condition, what is in their hand and
    /// what is at their belt — and, crucially, this comes from CURRENT state, so something they used up is
    /// simply not here. Condition is a band, never a number: no answer can leak one it was never given.
    /// </summary>
    private static IReadOnlyList<string> ProjectSelf(GameState state, Character asker)
    {
        var lines = new List<string>
        {
            $"You are {asker.Name}, and you are {DescribeCondition(asker)}.",
            asker.Weapon is null
                ? "You hold no weapon at all — you cannot strike anyone."
                : $"You hold your {asker.Weapon.Name}."
        };

        lines.Add(asker.Inventory.Length == 0
            ? "You carry nothing else at all — nothing at your belt, nothing in your hands."
            : $"You carry: {NaturalJoin([.. asker.Inventory.Select(DescribeItem)])}. That is everything; anything you " +
              "once had and is not in that list is gone.");

        if (asker.Injuries.Length > 0)
        {
            lines.Add($"Wounds you are carrying: {string.Join("; ", asker.Injuries.Select(i => i.Description))}.");
        }

        foreach (var ability in asker.Abilities)
        {
            lines.Add(ability.HasChargeLeft
                ? $"You can still use {ability.Name} ({ability.DescribeUses()})."
                : $"You have already used {ability.Name} in this fight and have nothing left of it.");
        }

        // The asker's own nerve, with the exact figure: it is theirs, and this is one of the two places it is
        // ever projected (the other is their turn context).
        lines.Add(asker.IsScared
            ? $"Your nerve has gone: fear {asker.Fear} out of {FearRules.Maximum}, and it shows on you. You are " +
              "increasingly concerned with survival — escaping, yielding, guarding, or asking a companion to " +
              "steady you are all worth weighing — but nothing forces your hand."
            : $"Your nerve: fear {asker.Fear} out of {FearRules.Maximum}.");

        foreach (var status in state.StatusesOn(asker.Id))
        {
            if (status.Kind == StatusEffectKind.Scared)
            {
                continue;
            }

            var source = state.FindById(status.SourceCharacterId)?.Name ?? status.SourceCharacterId;
            lines.Add($"Affecting you now: {status.Describe()} (put there by {source}).");
        }

        return lines;
    }

    /// <summary>
    /// The current public picture: who is present and in what standing, what each holds, what each is openly
    /// carrying that the asker has any basis to know of, the visible status effects, the room's objects and
    /// their open state, the floor, the ways out, and every surrender offer on the table. Anything the asker
    /// has no basis for is withheld and recorded as withheld.
    /// </summary>
    private IReadOnlyList<string> ProjectVisible(GameState state, Character asker, List<string> omitted)
    {
        var lines = new List<string>();

        foreach (var other in state.Characters.Where(c => !string.Equals(c.Id, asker.Id, StringComparison.OrdinalIgnoreCase)))
        {
            var side = asker.IsAllyOf(other) ? "on your side" : "against you";

            var standing = other.Disposition switch
            {
                CharacterDisposition.Dead => $"{other.Name} ({side}) is dead.",
                CharacterDisposition.Escaped => $"{other.Name} ({side}) has gone out through the way out and is no longer in the room; they cannot be reached.",
                CharacterDisposition.Surrendered => $"{other.Name} ({side}) has given up the fight on agreed terms: still here, still alive, but out of it, and cannot be struck.",
                _ => $"{other.Name} ({side}) is still fighting and is {DescribeCondition(other)}."
            };
            lines.Add(standing);

            if (other.IsPresent)
            {
                lines.Add(other.Weapon is null
                    ? $"{other.Name} holds no weapon — their hands are empty."
                    : $"{other.Name} holds a {other.Weapon.Name}.");

                var known = new List<string>();
                foreach (var item in other.Inventory)
                {
                    if (_knowledge.KnowsItem(asker.Id, item.Id))
                    {
                        known.Add(item.Name);
                    }
                    else
                    {
                        omitted.Add($"that {other.Name} is carrying the {item.Name} — {asker.Name} has no way of knowing it exists.");
                    }
                }

                if (known.Count > 0)
                {
                    lines.Add($"{other.Name} is openly carrying {NaturalJoin(known)}.");
                }

                foreach (var status in state.StatusesOn(other.Id))
                {
                    // Somebody else's nerve is answered by what a face shows and nothing more. The number
                    // behind it is not observable, so it is not projected — asking about it can never reveal it.
                    if (status.Kind == StatusEffectKind.Scared)
                    {
                        lines.Add($"{other.Name} looks scared and increasingly concerned with survival.");
                        continue;
                    }

                    var source = state.FindById(status.SourceCharacterId)?.Name ?? status.SourceCharacterId;
                    lines.Add($"{other.Name} is {status.Describe()} (put there by {source}).");
                }
            }
        }

        foreach (var worldObject in state.Room.Objects)
        {
            if (worldObject is CoverObject cover)
            {
                lines.Add(DescribeCoverForAnswer(cover, state));
                continue;
            }

            if (worldObject is not Container container)
            {
                lines.Add($"There is {worldObject.Name} in the room.");
                continue;
            }

            if (container.IsGround)
            {
                lines.Add(container.Contents.Length == 0
                    ? "Nothing is lying on the floor."
                    : $"Lying on the floor in plain sight of everyone, and free for anyone to pick up: " +
                      $"{NaturalJoin([.. container.Contents.Select(i => i.Name)])}.");
                continue;
            }

            if (container.IsCorpse)
            {
                lines.Add(container.Contents.Length == 0
                    ? $"{container.Name} lies where they fell, with nothing left on it."
                    : $"{container.Name} lies where they fell. Its belongings are within anyone's reach: " +
                      $"{NaturalJoin([.. container.Contents.Select(i => i.Name)])}.");
                continue;
            }

            lines.Add($"The {container.Name} stands within reach, and it is {(container.IsOpen ? "OPEN" : "SHUT")}.");

            var knowsContents = container.Contents.All(i => _knowledge.KnowsItemInContainer(asker.Id, container.Id, i.Id))
                                && _knowledge.RecordsFor(asker.Id).Any(r =>
                                    _knowledge.FindFact(r.FactId) is { FactType: FactType.ContainerContents } fact
                                    && string.Equals(fact.SubjectId, container.Id, StringComparison.OrdinalIgnoreCase));

            if (knowsContents)
            {
                lines.Add(container.Contents.Length == 0
                    ? $"You have looked in the {container.Name} yourself, and it holds nothing."
                    : $"You have looked in the {container.Name} yourself: it holds " +
                      $"{NaturalJoin([.. container.Contents.Select(i => i.Name)])}.");
            }
            else
            {
                lines.Add($"You have never seen inside the {container.Name}, so you cannot tell what is in it — " +
                          $"whether it is shut or standing open makes no difference to that.");
                omitted.Add($"the contents of the {container.Name} — {asker.Name} has never looked inside it.");
            }

            if (container.ExteriorClue is not null
                && !_knowledge.Knows(asker.Id, $"{container.Id}-marking"))
            {
                lines.Add($"The {container.Name} has markings on it, but they are too worn to make out from where you " +
                          "stand; you would have to spend a turn examining it closely.");
                omitted.Add($"the exterior marking on the {container.Name} — {asker.Name} has not examined it closely.");
            }
        }

        foreach (var exit in state.Room.Exits)
        {
            lines.Add(exit.IsOpen
                ? $"The {exit.Name} stands OPEN, and anyone can walk out through it to {exit.DestinationDescription}."
                : $"The {exit.Name} is SHUT — not locked, not barred, just shut. Anyone can pull it open, but nobody " +
                  "can pass through it until they do.");
        }

        foreach (var offer in state.PendingOffers())
        {
            var offerer = state.FindById(offer.OffererId)?.Name ?? offer.OffererId;
            var recipient = state.FindById(offer.RecipientId)?.Name ?? offer.RecipientId;
            var toYou = string.Equals(offer.RecipientId, asker.Id, StringComparison.OrdinalIgnoreCase);
            lines.Add(
                $"{offerer} has offered to give up the fight to {recipient}, promising {DescribeTerms(state, offer)}. " +
                $"Nothing has changed hands and {offerer} is still fighting and can still be struck. " +
                (toYou
                    ? "It is yours to take or leave, this turn only."
                    : $"It is {recipient}'s decision alone; you cannot take it up."));
        }

        foreach (var agreement in state.SurrenderAgreements)
        {
            var offerer = state.FindById(agreement.OffererId)?.Name ?? agreement.OffererId;
            var accepter = state.FindById(agreement.AcceptedById)?.Name ?? agreement.AcceptedById;
            lines.Add($"{offerer} gave up the fight to {accepter} on agreed terms, and is out of it for good.");
        }

        // Exact numbers are never in the projection at all, so no answer can leak one it was never handed.
        omitted.Add("every exact number — health totals, armour values, damage figures and chances to hit — which nobody in the world can perceive.");
        omitted.Add("what any other character privately knows, plans, or has asked in private.");

        return lines;
    }

    /// <summary>
    /// What the asker has discovered for themselves, rendered as an observation made at a moment — and paired
    /// with where the thing observed actually is NOW, when it can be resolved. This is the line between
    /// remembering and knowing: current ownership always supersedes a historical record of it, and something
    /// that has since been used up is stated as gone rather than left implied.
    /// </summary>
    private IReadOnlyList<string> ProjectKnown(GameState state, Character asker, List<string> omitted)
    {
        var lines = new List<string>();

        foreach (var record in _knowledge.RecordsFor(asker.Id))
        {
            var fact = _knowledge.FindFact(record.FactId);
            if (fact is null)
            {
                continue;
            }

            switch (fact.FactType)
            {
                case FactType.ContainerExteriorMarking:
                    lines.Add($"You have made out the marking on it: {fact.Description}");
                    break;

                case FactType.ContainerContents:
                {
                    var current = CurrentContentsSentence(state, fact);
                    lines.Add($"{fact.Description} That is what you saw; {current}");
                    break;
                }

                case FactType.ItemPossession or FactType.ItemRemoved or FactType.ItemGiven
                    or FactType.ItemDropped or FactType.ItemTheftAttempted:
                {
                    var whereNow = LocateItem(state, fact.SubjectId);
                    lines.Add(whereNow is null
                        ? $"You knew of the {ItemName(state, fact.SubjectId)}, but it no longer exists anywhere — it has " +
                          "been used up and is gone."
                        : $"{fact.Description} Right now, {whereNow}");
                    break;
                }

                default:
                    lines.Add(fact.Description);
                    break;
            }
        }

        if (lines.Count == 0)
        {
            lines.Add("Nothing beyond what anyone standing in this room can plainly see.");
        }

        return lines;
    }

    /// <summary>
    /// How a remembered container observation stands against the container now: unchanged, changed since, or
    /// unverifiable because the asker cannot see inside it any more.
    /// </summary>
    private string CurrentContentsSentence(GameState state, KnowledgeFact fact)
    {
        if (state.Room.Objects.FirstOrDefault(o => string.Equals(o.Id, fact.SubjectId, StringComparison.OrdinalIgnoreCase))
            is not Container container)
        {
            return "that container is no longer here.";
        }

        var currentIds = container.Contents.Select(i => i.Id).OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
        var observedIds = fact.ItemIds.OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();

        if (currentIds.SequenceEqual(observedIds, StringComparer.OrdinalIgnoreCase))
        {
            return "nothing has been taken out of it since, so that still holds.";
        }

        var gone = observedIds.Except(currentIds, StringComparer.OrdinalIgnoreCase)
            .Select(id => ItemName(state, id))
            .ToList();

        return gone.Count == 0
            ? "something has been put in it since."
            : $"{NaturalJoin(gone)} {(gone.Count == 1 ? "is" : "are")} no longer in it.";
    }

    /// <summary>Where an item is right now, in plain terms, or null when it no longer exists anywhere at all.</summary>
    private static string? LocateItem(GameState state, string itemId)
    {
        foreach (var character in state.Characters)
        {
            if (character.Inventory.Any(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase)))
            {
                return $"{character.Name} is carrying it.";
            }

            if (character.Weapon is not null && string.Equals(character.Weapon.Id, itemId, StringComparison.OrdinalIgnoreCase))
            {
                return $"{character.Name} is holding it.";
            }
        }

        foreach (var container in state.Room.Objects.OfType<Container>())
        {
            if (!container.Contents.Any(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return container.IsGround
                ? "it is lying on the floor, free for anyone to pick up."
                : container.IsCorpse
                    ? $"it is on {container.Name}, within anyone's reach."
                    : $"it is inside the {container.Name}.";
        }

        return null;
    }

    /// <summary>The display name of an item wherever it sits, falling back to its id so a line is never blank.</summary>
    private static string ItemName(GameState state, string itemId)
    {
        foreach (var character in state.Characters)
        {
            var carried = character.Inventory.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
            if (carried is not null)
            {
                return carried.Name;
            }

            if (character.Weapon is not null && string.Equals(character.Weapon.Id, itemId, StringComparison.OrdinalIgnoreCase))
            {
                return character.Weapon.Name;
            }
        }

        foreach (var container in state.Room.Objects.OfType<Container>())
        {
            var inside = container.Contents.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
            if (inside is not null)
            {
                return inside.Name;
            }
        }

        return itemId;
    }

    /// <summary>What the asker has been told, verbatim, and marked as a claim rather than a fact.</summary>
    private IReadOnlyList<string> ProjectHearsay(Character asker)
    {
        var heard = _narrationLog.SpeechHeardBy(asker.Id);
        return heard.Count == 0
            ? ["Nothing. Nobody has told this character anything."]
            : [.. heard.Select(entry => SingleLine(entry.Text))];
    }

    /// <summary>
    /// The complete list of what the asker could actually attempt right now, built from the engine's real
    /// action surface and this character's own state.
    /// </summary>
    /// <remarks>
    /// This is the part that stops an answer promising a manoeuvre the engine cannot resolve. It is a closed
    /// list, derived from state — an ability that is spent does not appear, a strike does not appear for
    /// somebody with empty hands, and an acceptance appears only for the named recipient of a live offer.
    /// </remarks>
    private static IReadOnlyList<string> ProjectAffordances(GameState state, Character asker)
    {
        var lines = new List<string>();
        var enemies = state.Characters.Where(c => c.CanAct && !asker.IsAllyOf(c)).Select(c => c.Name).ToList();
        var allies = state.Characters
            .Where(c => c.CanAct && c.IsAllyOf(asker) && !string.Equals(c.Id, asker.Id, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Name)
            .ToList();

        if (asker.Weapon is null)
        {
            lines.Add("Strike somebody: NO — you hold no weapon, so you cannot attack at all.");
        }
        else if (enemies.Count == 0)
        {
            lines.Add("Strike somebody: there is nobody left fighting against you to strike.");
        }
        else
        {
            lines.Add($"Strike one of them with your {asker.Weapon.Name}: {NaturalJoin(enemies)}. Whether the blow " +
                      "lands is not yours to decide.");
        }

        lines.Add("Set yourself behind your guard instead of striking, so the next blow that lands on you does less " +
                  "harm. Any turn, as often as you like; it costs the whole turn.");

        foreach (var ability in asker.Abilities.Where(a => !string.Equals(a.AbilityId, AbilityCatalog.DefendId, StringComparison.OrdinalIgnoreCase)))
        {
            var definition = AbilityCatalog.Find(ability.AbilityId);
            if (definition is null)
            {
                continue;
            }

            if (!ability.HasChargeLeft)
            {
                lines.Add($"{definition.Name}: already used up in this fight — not available.");
                continue;
            }

            var who = definition.TargetRule switch
            {
                AbilityTargetRule.OtherAlly => allies.Count == 0
                    ? "nobody on your side is left to use it on"
                    : $"on {NaturalJoin(allies)}",
                AbilityTargetRule.SelfOrAlly => allies.Count == 0
                    ? "on yourself"
                    : $"on yourself or {NaturalJoin(allies)}",
                AbilityTargetRule.Opponent => enemies.Count == 0
                    ? "nobody is left fighting you to use it on"
                    : $"on {NaturalJoin(enemies)}",
                _ => "on yourself"
            };

            lines.Add($"{definition.Name} ({ability.DescribeUses()}), {who}: {definition.Description}");
        }

        var healing = asker.Inventory.Where(i => i.IsHealingItem).Select(i => i.Name).ToList();
        lines.Add(healing.Count == 0
            ? "Use something you carry on yourself: nothing you have would do anything."
            : $"Use one of these on yourself, which consumes it: {NaturalJoin(healing)}.");

        if (asker.Inventory.Length > 0)
        {
            lines.Add($"Hand one of the things you carry to somebody here, or let it fall on the floor for anyone to " +
                      $"pick up: {NaturalJoin([.. asker.Inventory.Select(i => i.Name)])}. The weapon in your hand is " +
                      "not one of those things — it cannot be handed over, dropped or stolen.");
        }

        lines.Add("Try to snatch something from somebody, but only something you have a real reason to believe they " +
                  "are carrying. The grab is always noticed, and it may fail.");

        var ownCover = state.CoverOccupiedBy(asker.Id);
        if (ownCover is not null)
        {
            lines.Add($"Deliberately step out from behind {ownCover.Name}, with nothing else attempted. No roll. " +
                      "You do not need to do this before attacking, reaching for something, or opening something — " +
                      "an accepted action like that exposes you automatically as part of doing it.");
        }

        var freeCover = state.Room.Objects.OfType<CoverObject>()
            .Where(c => c.CanProvideCover && c.CurrentOccupantId is null)
            .ToList();
        if (freeCover.Count > 0)
        {
            lines.Add($"Move behind one of these and take real shelter there, which costs your whole turn and makes " +
                      $"no roll: {NaturalJoin([.. freeCover.Select(c => c.Name)])}.");
        }

        var damageableCover = state.Room.Objects.OfType<CoverObject>()
            .Where(c => c.State != EnvironmentalObjectState.Destroyed
                        && !string.Equals(c.CurrentOccupantId, asker.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (asker.Weapon is not null && damageableCover.Count > 0)
        {
            lines.Add($"Deliberately strike one of these with your {asker.Weapon.Name} rather than a person, which " +
                      $"makes no roll: {NaturalJoin([.. damageableCover.Select(c => c.Name)])}. It breaks your own " +
                      "cover first, if you are behind any.");
        }

        var closed = state.Room.Objects.OfType<Container>().Where(c => !c.IsOpen && !c.IsGround && !c.IsCorpse).ToList();
        if (closed.Count > 0)
        {
            lines.Add($"Open one of these, which is a whole turn on its own and takes nothing out of it: " +
                      $"{NaturalJoin([.. closed.Select(c => c.Name)])}.");
        }

        var reachable = state.Room.Objects.OfType<Container>().Where(c => c.IsOpen && c.Contents.Length > 0).ToList();
        if (reachable.Count > 0)
        {
            lines.Add($"Take one particular thing you know is there out of: " +
                      $"{NaturalJoin([.. reachable.Select(c => c.Name)])}. You cannot reach for something you have no " +
                      "way of knowing is in there.");
        }

        lines.Add("Spend a turn examining something in the room closely, to make out a marking or see into something " +
                  "already open. What you notice is yours alone.");

        foreach (var exit in state.Room.Exits)
        {
            lines.Add(exit.IsOpen
                ? $"Walk out through the open {exit.Name} and leave the fight behind, alive."
                : $"Pull the {exit.Name} open — a whole turn, and it gets you nowhere by itself; going through it is a " +
                  "separate turn afterwards.");
        }

        if (enemies.Count > 0)
        {
            lines.Add($"Offer to give up the fight to ONE of them — {NaturalJoin(enemies)} — promising something real " +
                      "you actually carry: coin, an item, or the weapon in your hand. It costs your turn, hands over " +
                      "nothing yet, does not disarm you and does not protect you: you stay a target until that one " +
                      "person agrees on their own turn. A bare plea promising nothing is not an offer and does nothing.");
        }

        var toMe = state.PendingOffersTo(asker.Id).ToList();
        if (toMe.Count > 0)
        {
            foreach (var offer in toMe)
            {
                var offerer = state.FindById(offer.OffererId)?.Name ?? offer.OffererId;
                lines.Add($"Take up {offerer}'s offer and spare them: you get {DescribeTerms(state, offer)}, they are " +
                          "disarmed and out of the fight, and there is no further risk from them. Only you can do it, " +
                          "and only this turn.");
            }
        }

        lines.Add("Speak aloud to the whole room — a threat, a warning, a demand, a plan. It costs no turn, and it " +
                  "changes nothing by itself: nobody is compelled by anything said.");
        lines.Add("Do nothing at all this turn and let the moment pass.");
        lines.Add("There is nothing else. No other kind of action exists in this world.");

        return lines;
    }

    /// <summary>The exact terms of an offer, as the state records them.</summary>
    private static string DescribeTerms(GameState state, SurrenderOffer offer)
    {
        var parts = new List<string>();
        if (offer.OfferedItemIds.Length > 0)
        {
            parts.Add(NaturalJoin([.. offer.OfferedItemIds.Select(id => ItemName(state, id))]));
        }

        if (offer.ForfeitWeapon)
        {
            var weapon = state.FindById(offer.OffererId)?.Weapon?.Name;
            parts.Add(weapon is null ? "the weapon in their hand" : $"their {weapon}");
        }

        return parts.Count == 0 ? "nothing" : string.Join(" and ", parts);
    }

    /// <summary>
    /// What this world does not represent, stated flatly.
    /// </summary>
    /// <remarks>
    /// A weaker Dungeon Master, handed a tactical question with no grounding, invents the grounding: distance
    /// to close, cover to use, a stance to take. Naming the absences explicitly is what makes "you cannot back
    /// away, because there is no away" an answer the model can give instead of making something up.
    /// </remarks>
    private static readonly IReadOnlyList<string> WorldLimitLines =
    [
        "There is no position, distance, facing, movement or spacing of any kind. Nobody has a location; " +
        "everyone in this room is already within arm's reach of everyone else, always. There is no closing in, " +
        "no backing away, no circling, no flanking, no line of sight, no high ground, and no distance to measure " +
        "or gain. The one exception, listed above if this room has any, is named environmental cover — a real " +
        "object a character can move behind for real protection — which works without any distance or " +
        "positioning at all: taking it costs a turn and gives up when the character leaves it, is exposed by " +
        "another action, or the cover is destroyed, never by anyone moving toward or away from anything. Never " +
        "describe distance, closing in or flanking, and never invent a second kind of cover this room does not list.",

        "There are no numbers anybody can perceive: no health totals, no armour values, no damage figures, no " +
        "chances to hit. Wounds are only ever described in words.",

        "The room's scenery — the lantern, the water underfoot, the rotten crates — is description only. " +
        "Nothing in it can be climbed, moved, thrown, tipped over, hidden behind, extinguished or used in any way.",

        "The only conditions that exist are the ones the world itself applies and lists: standing over a " +
        "companion, being stood over, being steadied, being off balance, and having your guard up. There is no " +
        "stunning, no knocking down, no tripping, no disarming a foe, no grappling and no shoving.",

        "Nobody can be forced into anything by words. A threat, a demand or a promise changes nothing until " +
        "somebody chooses to act on it on their own turn."
    ];

    /// <summary>
    /// Cover, for an answer (v0.9). Nothing here is hidden — everyone present can see the object, its
    /// condition and who is behind it — so, unlike a container, none of it is omitted or marked withheld.
    /// </summary>
    private static string DescribeCoverForAnswer(CoverObject cover, GameState state)
    {
        if (cover.State == EnvironmentalObjectState.Destroyed)
        {
            return $"{cover.Name} has been destroyed and is wreckage now; it shelters nobody.";
        }

        var condition = cover.State == EnvironmentalObjectState.Damaged
            ? "damaged, but still gives real protection"
            : "intact";
        var occupant = cover.CurrentOccupantId is null
            ? "Nobody is behind it right now."
            : $"{state.FindById(cover.CurrentOccupantId)?.Name ?? cover.CurrentOccupantId} is behind it right now.";
        return $"{cover.Name} stands within reach, {condition}. {occupant}";
    }

    private static string DescribeItem(InventoryItem item) =>
        item.HealingAmount is not null ? $"{item.DisplayName} (it would mend a wound if you used it)" : item.DisplayName;

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

    private static string NaturalJoin(IReadOnlyList<string> values) => values.Count switch
    {
        0 => "nothing",
        1 => values[0],
        2 => $"{values[0]} and {values[1]}",
        _ => $"{string.Join(", ", values.Take(values.Count - 1))}, and {values[^1]}"
    };

    private static string SingleLine(string text) => text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
}
