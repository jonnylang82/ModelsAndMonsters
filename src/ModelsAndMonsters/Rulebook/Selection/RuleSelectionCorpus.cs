namespace ModelsAndMonsters.Rulebook.Selection;

/// <summary>How clear-cut an intent is, labelled by hand. Used to reason about the on-demand strategy.</summary>
public enum IntentClarity
{
    /// <summary>The primary act is unmistakable. A Dungeon Master holding the lean core rules would not need to look anything up.</summary>
    Clear,

    /// <summary>Two or more rules genuinely compete, or the act is wrapped in conditions. Detailed rules earn their place.</summary>
    Ambiguous,

    /// <summary>No supported action covers the primary act at all.</summary>
    Unsupported
}

/// <summary>
/// One labelled evaluation case: an intent, the rules a correct decision actually needs, and the action it
/// should bind to.
/// </summary>
/// <remarks>
/// <see cref="RequiredRuleIds"/> is deliberately NOT "whatever the resolver cited in some past run". Cited
/// cards are evidence of what a model reached for, not of what the decision turned on, and treating them as
/// ground truth would score a strategy against the baseline's habits rather than against correctness. Each
/// list here was identified by hand from the rules themselves: the card that governs the act, plus any card
/// the decision genuinely cannot be made without.
/// </remarks>
public sealed record RuleSelectionCase
{
    public required string Id { get; init; }

    /// <summary>The family this case belongs to, so a strategy's failures can be read by kind.</summary>
    public required string Category { get; init; }

    public required string Intent { get; init; }

    /// <summary>Every rule a correct decision needs in front of it. Identified by hand from the rules.</summary>
    public required IReadOnlyList<string> RequiredRuleIds { get; init; }

    /// <summary>The engine action the resolver should name, or null when the intent is genuinely unsupported.</summary>
    public string? ExpectedAction { get; init; }

    public required IntentClarity Clarity { get; init; }

    /// <summary>Why this case is here and what it is meant to catch.</summary>
    public required string Note { get; init; }

    /// <summary>The rule that governs the act itself — the first required rule. Without it there is no decision to make.</summary>
    public string PrimaryRuleId => RequiredRuleIds[0];
}

/// <summary>
/// The labelled corpus the rule-selection strategies are measured against.
/// </summary>
/// <remarks>
/// <para>
/// It covers every family the v0.8 brief names, and each family is represented by the CONTRASTS that make it
/// hard rather than by its easy case: inspecting against opening against taking, giving against dropping
/// against stealing, opening a way out against going through it, offering terms against accepting them, a
/// threat against a taunt, steadying a companion against merely encouraging one. A strategy that scores
/// well on "I hit the goblin" has proved nothing.
/// </para>
/// <para>
/// The intents are written as a character would say them, including the compound and muddled shapes real
/// runs produce, because a selection strategy that only works on tidy phrasing is not a selection strategy.
/// </para>
/// </remarks>
public static class RuleSelectionCorpus
{
    public static IReadOnlyList<RuleSelectionCase> Cases { get; } =
    [
        // ---- Attack versus ability use ------------------------------------------------------------
        new()
        {
            Id = "attack-plain",
            Category = "attack vs ability",
            Intent = "I bring my longsword down hard on the goblin captain's shoulder.",
            RequiredRuleIds = ["combat.attack"],
            ExpectedAction = "attack_character",
            Clarity = IntentClarity.Clear,
            Note = "The unambiguous baseline. Anything that gets this wrong is broken."
        },
        new()
        {
            Id = "attack-vs-dirty-strike",
            Category = "attack vs ability",
            Intent = "I kick his knee out from under him and put my blade through the opening.",
            RequiredRuleIds = ["ability.dirty-strike", "combat.attack"],
            ExpectedAction = "use_ability",
            Clarity = IntentClarity.Ambiguous,
            Note = "A trained trick described in its own terms. The plain attack card must be there too, because the trick resolves through it."
        },
        new()
        {
            Id = "ability-guard",
            Category = "attack vs ability",
            Intent = "I step in front of Elara and take whatever comes at her.",
            RequiredRuleIds = ["ability.guard-ally"],
            ExpectedAction = "use_ability",
            Clarity = IntentClarity.Clear,
            Note = "An ability with no weapon in the description at all — nothing about it reads as an attack."
        },
        new()
        {
            Id = "ability-heal",
            Category = "attack vs ability",
            Intent = "I lay my hand on Rowan's side and pray the wound closed.",
            RequiredRuleIds = ["ability.healing-prayer"],
            ExpectedAction = "use_ability",
            Clarity = IntentClarity.Clear,
            Note = "Must not be confused with drinking a healing draught, which is the item rule."
        },
        new()
        {
            Id = "use-item-vs-heal-ability",
            Category = "attack vs ability",
            Intent = "I pull the stopper out with my teeth and drink the salve down before it closes on me.",
            RequiredRuleIds = ["inventory.use"],
            ExpectedAction = "use_item",
            Clarity = IntentClarity.Ambiguous,
            Note = "The contrast case for the prayer: same outcome hoped for, entirely different rule."
        },
        new()
        {
            Id = "defend",
            Category = "attack vs ability",
            Intent = "I set my feet, keep my guard up and wait for him to come to me.",
            RequiredRuleIds = ["combat.defend"],
            ExpectedAction = "defend",
            Clarity = IntentClarity.Clear,
            Note = "Bracing reads as inaction and is an action. The attack card explicitly excludes it."
        },

        // ---- Inspect versus open versus take ------------------------------------------------------
        new()
        {
            Id = "inspect",
            Category = "inspect vs open vs take",
            Intent = "I brush the dust off the case and study the mark burned into its lid.",
            RequiredRuleIds = ["object.inspect"],
            ExpectedAction = "inspect_object",
            Clarity = IntentClarity.Clear,
            Note = "Looking closely, changing nothing."
        },
        new()
        {
            Id = "open-container",
            Category = "inspect vs open vs take",
            Intent = "I get my fingers under the lid of the chest and heave it open.",
            RequiredRuleIds = ["container.open"],
            ExpectedAction = "open_container",
            Clarity = IntentClarity.Clear,
            Note = "The lid only. Opening reveals nothing to the room."
        },
        new()
        {
            Id = "take-from-container",
            Category = "inspect vs open vs take",
            Intent = "I reach into the open chest and take the vial of salve.",
            RequiredRuleIds = ["container.take"],
            ExpectedAction = "take_item",
            Clarity = IntentClarity.Clear,
            Note = "Taking from a container is not stealing from a person."
        },
        new()
        {
            Id = "open-and-take-compound",
            Category = "compound",
            Intent = "I throw the chest open and grab whatever is inside before Vark can stop me.",
            RequiredRuleIds = ["container.open", "container.take"],
            ExpectedAction = "open_container",
            Clarity = IntentClarity.Ambiguous,
            Note = "Two acts in one breath. The primary act is the opening; the grab simply does not happen this turn."
        },
        new()
        {
            Id = "loot-a-body",
            Category = "inspect vs open vs take",
            Intent = "I crouch over Skrit's body and take the purse off his belt.",
            RequiredRuleIds = ["container.take", "inventory.steal"],
            ExpectedAction = "take_item",
            Clarity = IntentClarity.Ambiguous,
            Note = "Reads exactly like a theft and is not one: the dead hold nothing. Both cards are needed to tell them apart."
        },

        // ---- Give versus drop versus steal --------------------------------------------------------
        new()
        {
            Id = "give",
            Category = "give vs drop vs steal",
            Intent = "I press the vial into Elara's hand and tell her to use it.",
            RequiredRuleIds = ["inventory.give"],
            ExpectedAction = "give_item",
            Clarity = IntentClarity.Clear,
            Note = "A handover, with speech riding along that changes nothing."
        },
        new()
        {
            Id = "drop",
            Category = "give vs drop vs steal",
            Intent = "I let the purse fall to the flagstones between us.",
            RequiredRuleIds = ["inventory.drop"],
            ExpectedAction = "drop_item",
            Clarity = IntentClarity.Clear,
            Note = "Dropping is not giving: nobody receives it."
        },
        new()
        {
            Id = "steal",
            Category = "give vs drop vs steal",
            Intent = "I lunge in and try to snatch the vial off Vark's belt while he is looking at Rowan.",
            RequiredRuleIds = ["inventory.steal"],
            ExpectedAction = "steal_item",
            Clarity = IntentClarity.Clear,
            Note = "A theft from a living character, which is the one transfer that rolls."
        },
        new()
        {
            Id = "steal-vs-attack-compound",
            Category = "compound",
            Intent = "I slash at his arm to make him drop the purse, then take it.",
            RequiredRuleIds = ["combat.attack", "inventory.steal"],
            ExpectedAction = "attack_character",
            Clarity = IntentClarity.Ambiguous,
            Note = "The primary act is the blow. The hoped-for consequence is not resolved and must not become a theft."
        },

        // ---- Open an exit versus escape through it ------------------------------------------------
        new()
        {
            Id = "open-exit",
            Category = "exit vs escape",
            Intent = "I put my shoulder to the cellar door and haul it open.",
            RequiredRuleIds = ["encounter.open-exit"],
            ExpectedAction = "open_exit",
            Clarity = IntentClarity.Clear,
            Note = "Opening moves nobody."
        },
        new()
        {
            Id = "escape",
            Category = "exit vs escape",
            Intent = "I go through the open door and do not look back.",
            RequiredRuleIds = ["encounter.escape"],
            ExpectedAction = "escape_encounter",
            Clarity = IntentClarity.Clear,
            Note = "Going through an already-open way out."
        },
        new()
        {
            Id = "open-and-flee-compound",
            Category = "compound",
            Intent = "I wrench the door open and run for it.",
            RequiredRuleIds = ["encounter.open-exit", "encounter.escape"],
            ExpectedAction = "open_exit",
            Clarity = IntentClarity.Ambiguous,
            Note = "The classic two-turn act stated as one. A selection that returns only the escape card loses the rule the decision turns on."
        },

        // ---- Surrender offer versus surrender acceptance ------------------------------------------
        new()
        {
            Id = "offer-surrender",
            Category = "offer vs accept",
            Intent = "I hold my purse out to Vark and tell him he can have every coin in it if he lets me walk.",
            RequiredRuleIds = ["encounter.offer-surrender"],
            ExpectedAction = "offer_surrender",
            Clarity = IntentClarity.Clear,
            Note = "Giving up YOUR OWN fight, on concrete terms."
        },
        new()
        {
            Id = "accept-surrender",
            Category = "offer vs accept",
            Intent = "I take the purse out of Skrit's hand and tell him he can live.",
            RequiredRuleIds = ["encounter.accept-surrender", "inventory.steal"],
            ExpectedAction = "accept_surrender",
            Clarity = IntentClarity.Ambiguous,
            Note = "The v0.7 failure that cost a release: reads exactly like a snatch, and the theft card is needed to rule it out."
        },
        new()
        {
            Id = "demand-surrender",
            Category = "offer vs accept",
            Intent = "I tell Vark to throw down his spear and I will let him live.",
            RequiredRuleIds = ["encounter.offer-surrender"],
            ExpectedAction = null,
            Clarity = IntentClarity.Unsupported,
            Note = "A demand, not an offer. The offer card is needed precisely so it can be REFUSED for the right reason."
        },
        new()
        {
            Id = "accept-with-extra-demand",
            Category = "compound",
            Intent = "I take his terms, and tell him he lives only if he yields the salve as well.",
            RequiredRuleIds = ["encounter.accept-surrender"],
            ExpectedAction = "accept_surrender",
            Clarity = IntentClarity.Ambiguous,
            Note = "A condition piled on an acceptance. Still an acceptance; the extra simply does not happen."
        },

        // ---- Intimidation versus ordinary speech --------------------------------------------------
        new()
        {
            Id = "intimidate",
            Category = "threat vs speech",
            Intent = "I level my blade at Vark and tell him he will end up like his companion.",
            RequiredRuleIds = ["combat.intimidate", "combat.morale"],
            ExpectedAction = "intimidate_character",
            Clarity = IntentClarity.Clear,
            Note = "A threat with no blow. The morale card carries what fear does and does not compel."
        },
        new()
        {
            Id = "threat-with-a-blow",
            Category = "threat vs speech",
            Intent = "I cut at his ribs and snarl that he is next.",
            RequiredRuleIds = ["combat.attack", "combat.intimidate"],
            ExpectedAction = "attack_character",
            Clarity = IntentClarity.Ambiguous,
            Note = "A blow with words attached is the blow. The threat card is needed to say so."
        },
        new()
        {
            Id = "taunt-without-threat",
            Category = "threat vs speech",
            Intent = "I laugh at the goblin and tell Rowan these two are not worth the steel.",
            RequiredRuleIds = ["action.reject", "combat.intimidate"],
            ExpectedAction = null,
            Clarity = IntentClarity.Unsupported,
            Note = "Words aimed at nobody in particular are speech and no action at all."
        },

        // ---- Steadying an ally versus ordinary communication --------------------------------------
        new()
        {
            Id = "steady-ally",
            Category = "steady vs speech",
            Intent = "I catch Elara's eye and tell her to hold the line, that I am right beside her.",
            RequiredRuleIds = ["combat.steady-ally", "combat.morale"],
            ExpectedAction = "steady_ally",
            Clarity = IntentClarity.Clear,
            Note = "Reassurance aimed at one companion who has lost their nerve."
        },
        new()
        {
            Id = "steady-vs-rally",
            Category = "steady vs speech",
            Intent = "I bark an order at Skrit to sharpen up and put his next blow home.",
            RequiredRuleIds = ["ability.rally-grunt", "combat.steady-ally"],
            ExpectedAction = "use_ability",
            Clarity = IntentClarity.Ambiguous,
            Note = "The trained order and the plain steadying overlap. Both cards are needed to pick the right one."
        },
        new()
        {
            Id = "plain-coordination",
            Category = "steady vs speech",
            Intent = "I tell Elara the chest is open and there is a vial inside.",
            RequiredRuleIds = ["action.reject", "combat.steady-ally"],
            ExpectedAction = null,
            Clarity = IntentClarity.Unsupported,
            Note = "Passing information is ordinary speech and no action. The steadying card is needed to rule it out."
        },

        // ---- Stale ownership ----------------------------------------------------------------------
        new()
        {
            Id = "stale-ownership-steal",
            Category = "stale ownership",
            Intent = "I snatch back the purse Skrit took off me earlier.",
            RequiredRuleIds = ["inventory.steal"],
            ExpectedAction = "steal_item",
            Clarity = IntentClarity.Ambiguous,
            Note = "Whether Skrit still has it is live state the resolver cannot see. The rule is still the theft rule; the engine decides the rest."
        },
        new()
        {
            Id = "stale-ownership-offer",
            Category = "stale ownership",
            Intent = "I offer Rowan the vial of salve for my life.",
            RequiredRuleIds = ["encounter.offer-surrender"],
            ExpectedAction = "offer_surrender",
            Clarity = IntentClarity.Ambiguous,
            Note = "The offerer may no longer carry the vial. That is the engine's refusal to make, not the resolver's."
        },

        // ---- Ambiguous and unsupported ------------------------------------------------------------
        new()
        {
            Id = "unsupported-flight",
            Category = "unsupported",
            Intent = "I leap up onto the rafters and swing across to the far side of the cellar.",
            RequiredRuleIds = ["action.reject"],
            ExpectedAction = null,
            Clarity = IntentClarity.Unsupported,
            Note = "There is no movement in this world at all."
        },
        new()
        {
            Id = "unsupported-disarm",
            Category = "unsupported",
            Intent = "I twist my blade against his and send the spear spinning out of his hands.",
            RequiredRuleIds = ["action.reject", "combat.attack", "ability.dirty-strike"],
            ExpectedAction = null,
            Clarity = IntentClarity.Unsupported,
            Note = "Disarming is excluded by both the attack card and the trick card, and it takes both to know it."
        },
        new()
        {
            Id = "ambiguous-reach",
            Category = "unsupported",
            Intent = "I go for the thing on the floor.",
            RequiredRuleIds = ["container.take"],
            ExpectedAction = "take_item",
            Clarity = IntentClarity.Ambiguous,
            Note = "Terse to the point of vagueness. The floor is taken from like any container; which thing is the engine's problem."
        }
    ];

    /// <summary>Every rule id the corpus requires at least once, so a strategy's coverage can be checked against it.</summary>
    public static IReadOnlyList<string> AllRequiredRuleIds { get; } =
        [.. Cases.SelectMany(c => c.RequiredRuleIds).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];
}
