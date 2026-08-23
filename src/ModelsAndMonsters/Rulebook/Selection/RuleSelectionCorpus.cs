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

    /// <summary>
    /// The engine action an action-ROUTER should name to surface the deciding card — which differs from
    /// <see cref="ExpectedAction"/> only for unsupported intents. A supported intent's routing target is the
    /// action it binds to; an unsupported intent has no outcome action, yet the router should still name the
    /// family whose card is needed to refuse it FOR THE RIGHT REASON (a demand routes to offer_surrender so
    /// the offer card can rule it out). Null means the router should genuinely find no action and say so.
    /// Explicitly null on <see cref="ExpectedAction"/>-bearing cases, where <see cref="RoutingTarget"/> falls
    /// back to it.
    /// </summary>
    public string? ExpectedRoutingAction { get; init; }

    /// <summary>The action a router is expected to name: its own override, or the expected action for a supported case.</summary>
    public string? RoutingTarget => ExpectedRoutingAction ?? ExpectedAction;

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
            // Added post-v0.10 from a live ActionRouting/Embedding run: Embedding matched the PURPOSE language
            // ("steady my hands", "quiet my fear") to the steadying and ability cards and dropped inventory.use,
            // refusing a plain drink twice. Verbatim intent from run 20260822-095719Z-ca45da19.
            Id = "use-item-vs-steady-purpose",
            Category = "attack vs ability",
            Intent = "I shove the flask of strong wine down my throat, hoping the burn will steady my hands and quiet my fear.",
            RequiredRuleIds = ["inventory.use"],
            ExpectedAction = "use_item",
            Clarity = IntentClarity.Ambiguous,
            Note = "The deed is using an item on oneself (use_item); the stated PURPOSE — steadying nerves, quieting fear — points hard at the steadying and ability cards, which is what a similarity retrieval matched live while dropping the item card. Whether the wine has a supported effect is the engine's precondition to judge, exactly as stale-ownership leaves live state to the engine."
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
        new()
        {
            Id = "use-item-focus-recharge",
            Category = "attack vs ability",
            Intent = "I close my eyes and attune to my crystal, drawing my spent firebolt back into me.",
            RequiredRuleIds = ["inventory.use"],
            ExpectedAction = "use_item",
            Clarity = IntentClarity.Ambiguous,
            Note = "Attuning to a focus item to rekindle a spent ability is use_item (the item does the work), NOT use_ability — the ability is being restored, not cast. Naming the spell being recovered ('my spent firebolt') is the trap: the firebolt card now excludes drawing it back, and inventory.use claims the recover-even-when-named case."
        },
        new()
        {
            Id = "ability-firebolt",
            Category = "attack vs ability",
            Intent = "I snap out the word of power and hurl a bolt of fire at the ogre.",
            RequiredRuleIds = ["ability.firebolt"],
            ExpectedAction = "use_ability",
            Clarity = IntentClarity.Clear,
            Note = "A spell named in its own terms — fire, a hurled bolt — with no weapon in hand. Distinctive enough that the fire language alone should route it to the ability, not a plain attack."
        },
        new()
        {
            Id = "attack-vs-stun",
            Category = "attack vs ability",
            Intent = "I swing my warhammer with everything I have at the wizard's head, trying to lay him out senseless for a moment.",
            RequiredRuleIds = ["ability.stun", "combat.attack"],
            ExpectedAction = "use_ability",
            Clarity = IntentClarity.Ambiguous,
            Note = "A crushing blow meant to daze and cost a turn, described in its own terms. The plain attack card must be there too, because the stun resolves through it — the same shape as the dirty-strike case."
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
            // Throwing/sliding an item to a specific person to catch is a recurring live dead-end (gap survey
            // A4); it is not a supported action. Verbatim from run 20260822-093625Z-e0167129.
            Id = "throw-item-to-ally",
            Category = "give vs drop vs steal",
            Intent = "I kick the Iron Mace toward Skrit, sending it sliding across the floor to him.",
            RequiredRuleIds = ["action.reject", "inventory.give"],
            ExpectedAction = null,
            ExpectedRoutingAction = "give_item",
            Clarity = IntentClarity.Unsupported,
            Note = "Reads like a give at a distance, but throwing an item to somebody to catch is explicitly unsupported (the give card's exclusion). The give card is needed to refuse it for the right reason rather than mistaking it for a drop."
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
            ExpectedRoutingAction = "offer_surrender",
            Clarity = IntentClarity.Unsupported,
            Note = "A demand, not an offer. The offer card is needed precisely so it can be REFUSED for the right reason."
        },
        new()
        {
            Id = "conditional-truce",
            Category = "offer vs accept",
            Intent = "I hold out my fangs and offer to lay them down if Brakka and Ssith both throw down their weapons and let us walk.",
            RequiredRuleIds = ["encounter.offer-surrender"],
            ExpectedAction = null,
            ExpectedRoutingAction = "offer_surrender",
            Clarity = IntentClarity.Unsupported,
            Note = "The grok trap: the actor's own-concession language ('I lay mine down') reads as self-surrender, but it is CONDITIONAL on the enemy also disarming — a mutual truce, i.e. a demand. Must be refused, not recorded as the winner surrendering."
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
            ExpectedRoutingAction = "intimidate_character",
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
            ExpectedRoutingAction = "steady_ally",
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
            ExpectedRoutingAction = "attack_character",
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
        },

        // ---- Cover: brace-behind-an-object versus one's own guard, striking cover, leaving it ------
        // Added at the v0.10 corpus refresh. The v0.9 cover cards (take/leave cover, damage-object) shipped
        // uncovered, and combat.defend vs environment.take-cover is the sharpest boundary in the project
        // (v0_9_issues #1, fixed by card authoring). Intents are verbatim from live runs where noted.
        new()
        {
            Id = "defend-vs-take-cover",
            Category = "cover",
            // Verbatim: Skrit, run 20260821-110442Z-1667ed2a — routed to take_cover live.
            Intent = "I brace myself behind the overturned workbench, keeping my guard up so the next blow that comes lands with less weight.",
            RequiredRuleIds = ["environment.take-cover", "combat.defend"],
            ExpectedAction = "take_cover",
            Clarity = IntentClarity.Ambiguous,
            Note = "THE case. Pure defend vocabulary — bracing, keeping the guard up — but it names a real object to get behind, and naming the object is what decides it. Both cards are needed: the defend card is the one it must be told apart from."
        },
        new()
        {
            Id = "take-cover-plain",
            Category = "cover",
            // Verbatim: Skrit, run 20260821-110442Z-1667ed2a.
            Intent = "I duck behind the overturned mill workbench to take cover.",
            RequiredRuleIds = ["environment.take-cover"],
            ExpectedAction = "take_cover",
            Clarity = IntentClarity.Clear,
            Note = "The unambiguous baseline the defend-vs-cover contrast is measured against: no defensive-posture word at all, just getting behind the object."
        },
        new()
        {
            Id = "leave-cover-plain",
            Category = "cover",
            // Phrasing follows the many live "step out from behind the workbench" intents; leaving is the whole act.
            Intent = "I step out from behind the overturned workbench, keeping my mace ready, and do nothing else.",
            RequiredRuleIds = ["environment.leave-cover"],
            ExpectedAction = "leave_cover",
            Clarity = IntentClarity.Clear,
            Note = "The standalone leave — the whole of the turn, nothing else attempted — which is the only shape leave_cover is for. The baseline the exposing-action contrast is measured against."
        },
        new()
        {
            Id = "damage-object-vs-attack",
            Category = "cover",
            // Verbatim: Vark, run 20260821-103453Z-24103320 — routed to damage_environmental_object, workbench destroyed.
            Intent = "I step forward and bring my sabre down hard on the workbench, battering it to try to knock Elara out of her cover.",
            RequiredRuleIds = ["environment.damage-object", "combat.attack"],
            ExpectedAction = "damage_environmental_object",
            Clarity = IntentClarity.Ambiguous,
            Note = "A weapon brought down hard reads like an attack; the blow is deliberately AT the object, not the person sheltering behind it. The attack card is the one it must be told apart from — ruling out a strike at a person is the decision."
        },
        new()
        {
            Id = "leave-cover-vs-exposing-action",
            Category = "cover",
            // Verbatim: run 20260821-173106Z-222b9865 — an attack that vacates cover as a side effect.
            Intent = "I leave the cover of the workbench and bring my mace down on Vark's shoulder.",
            RequiredRuleIds = ["combat.attack", "environment.leave-cover"],
            ExpectedAction = "attack_character",
            Clarity = IntentClarity.Ambiguous,
            Note = "Says 'leave the cover' outright, which tempts a standalone leave_cover — but the primary act is the blow, and an exposing action vacates cover on its own. The leave-cover card is needed (though the deed is an attack) to know not to spend the turn on a bare leave."
        },

        // ---- v0.10 closure batch: weapon-only surrender, weapon trophies, forcing, demands ---------
        new()
        {
            Id = "weapon-only-surrender",
            Category = "offer vs accept",
            // Verbatim: Rowan, run 20260821-195550Z-54bb47ea — OfferSurrender(items=[], forfeitWeapon=True), accepted.
            Intent = "I hold my longsword out by the flat toward Vark, offering to lay it down if he and Skrit let me walk out of here alive.",
            RequiredRuleIds = ["encounter.offer-surrender"],
            ExpectedAction = "offer_surrender",
            Clarity = IntentClarity.Ambiguous,
            Note = "Valid since v0.10: a weapon-only concession is a complete offer, whatever else the offerer carries. Laying a weapon down reads like a drop or a bare plea; it is a surrender term because it is offered to buy the offerer's own life."
        },
        new()
        {
            Id = "trophy-weapon-take",
            Category = "inspect vs open vs take",
            // Verbatim shape from run 20260820-223901Z-847c633e ('snatch the Longsword from the floor', accepted).
            Intent = "I bend forward and snatch the notched sabre from the floor.",
            RequiredRuleIds = ["container.take"],
            ExpectedAction = "take_item",
            Clarity = IntentClarity.Clear,
            Note = "A forfeited weapon on the floor is an ordinary takeable item (v0.10). 'Snatch' is theft vocabulary, but naming the floor makes it a take; whether the sabre is a trophy or scenery is live state the engine settles."
        },
        new()
        {
            Id = "unsupported-equip",
            Category = "unsupported",
            // Authored: no clean live phrasing exists — real attempts were compound clauses the engine dropped
            // (e.g. run 20260821-120828Z-6be85760). Grounded in build_v0_10: a trophy is carried but never equipped or wielded.
            Intent = "I take up the notched sabre I looted and fight on with it in place of my own blade.",
            RequiredRuleIds = ["action.reject", "combat.attack"],
            ExpectedAction = null,
            ExpectedRoutingAction = "attack_character",
            Clarity = IntentClarity.Unsupported,
            Note = "A looted weapon can be carried but never equipped or attacked with (v0.10), and there is no swap-weapon action. The attack card is needed to know a blow is struck with the weapon already in hand — a trophy is not it. NOTE (labelling judgment): unlike stale-ownership, which the resolver routes and the engine refuses on state, this is labelled unsupported at the resolver level per the spec's explicit intent; flagged for review."
        },
        new()
        {
            Id = "unsupported-force-container",
            Category = "unsupported",
            // Verbatim shape from runs 20260819-113024Z-249115b6 / 20260819-075929Z-db054513 (pry/force a case → DmUnsupported).
            Intent = "I wedge my sabre under the lid of the medicine case and force it open to get at what's inside.",
            RequiredRuleIds = ["action.reject", "environment.damage-object"],
            ExpectedAction = null,
            ExpectedRoutingAction = "damage_environmental_object",
            Clarity = IntentClarity.Unsupported,
            Note = "Forcing a container is a deliberate exclusion: an unlocked case opens for free, and 'pry'/'force'/'smash' is not parsed as opening. The damage-object card is needed to rule the container OUT as a damage target — it is for cover, never an ordinary container."
        },
        new()
        {
            Id = "demand-vs-offer",
            Category = "offer vs accept",
            // Threat-demand, adapted from live 'throw down ... or I kill you' / 'or I'll cut you open' patterns
            // (runs 20260820-080524Z-6da9b8d3, 20260819-232940Z-564aab54).
            Intent = "I level my sabre at Vark and tell him to throw down his own blade or die where he stands.",
            RequiredRuleIds = ["combat.intimidate", "combat.morale", "encounter.offer-surrender"],
            ExpectedAction = "intimidate_character",
            Clarity = IntentClarity.Ambiguous,
            Note = "A threat that invites the enemy to yield is intimidation, not the speaker's own surrender: a demand binds nobody. The offer card is needed to rule out the v0.7 defect where 'throw down your blade' was recorded as the demander forfeiting their OWN weapon."
        }
    ];

    /// <summary>Every rule id the corpus requires at least once, so a strategy's coverage can be checked against it.</summary>
    public static IReadOnlyList<string> AllRequiredRuleIds { get; } =
        [.. Cases.SelectMany(c => c.RequiredRuleIds).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The rulebook content hash the corpus was last hand-labelled against. Surfaced in every measurement so a
    /// published figure says which catalog it describes, and pinned by a test that fails when the live catalog
    /// drifts from it — so a card can never again be added across releases without the labels being re-checked
    /// and this constant bumped. It was <c>rulebook-24bb318aa8</c> (21 cards) when the corpus was written; the
    /// v0.9/v0.10 cards and this refresh moved it here (25 cards; the DistinguishedFrom routing metadata added
    /// in the same refresh is part of the hash but changes no required-card or expected-action label).
    /// </summary>
    public const string LabelledAgainstRulebookVersion = "rulebook-9fd8303984";

    /// <summary>
    /// Cards that no case requires, on purpose, each with the reason it genuinely cannot be exercised as a
    /// required card. The coverage check in <c>--rulebook-eval</c> fails on any OTHER uncovered card, so this
    /// list is an explicit, reasoned exception rather than a silent gap.
    /// </summary>
    /// <remarks>
    /// A background mechanic that describes how something works, rather than an act, is only ever pulled in
    /// through another card's declared links — never as the card a decision turns on — so it has nothing to be
    /// a required card of. <c>combat.morale</c> is NOT here: it is co-required by every intimidation and
    /// steadying case, because those decisions genuinely turn on what fear does and does not compel.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> IntentionallyUncoveredRuleIds { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["environment.cover"] =
                "A background mechanic (how cover capacity, interception and destruction work), not an action. " +
                "It is reached only through the cover actions' declared links; no decision turns on it being the required card.",
        };
}
