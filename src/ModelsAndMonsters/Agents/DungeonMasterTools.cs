using Microsoft.Extensions.AI;

namespace ModelsAndMonsters.Agents;

/// <summary>
/// The Dungeon Master's tool surface: the two state-changing engine actions plus a structured refusal.
/// </summary>
/// <remarks>
/// Only the Dungeon Master is ever given these declarations, which is what enforces that no character
/// can reach the engine directly. As with the character tools these are declaration-only; the
/// application inspects the requested call and submits the action to the engine itself.
/// </remarks>
public static class DungeonMasterTools
{
    public const string AttackCharacterName = "attack_character";
    public const string UseItemName = "use_item";
    public const string OpenContainerName = "open_container";
    public const string TakeItemName = "take_item";
    public const string InspectObjectName = "inspect_object";
    public const string OpenExitName = "open_exit";
    public const string EscapeEncounterName = "escape_encounter";
    public const string OfferSurrenderName = "offer_surrender";
    public const string AcceptSurrenderName = "accept_surrender";
    public const string UseAbilityName = "use_ability";
    public const string DefendName = "defend";
    public const string GiveItemName = "give_item";
    public const string DropItemName = "drop_item";
    public const string StealItemName = "steal_item";
    public const string IntimidateCharacterName = "intimidate_character";
    public const string SteadyAllyName = "steady_ally";
    public const string TakeCoverName = "take_cover";
    public const string LeaveCoverName = "leave_cover";
    public const string DamageEnvironmentalObjectName = "damage_environmental_object";
    public const string RejectActionName = "reject_action";

    public const string AttackerParameter = "attacker";
    public const string TargetParameter = "target";
    public const string WeaponParameter = "weapon";
    public const string ActorParameter = "actor";
    public const string ItemParameter = "item";
    public const string ContainerParameter = "container";
    public const string ObjectParameter = "object";
    public const string ExitParameter = "exit";
    public const string RecipientParameter = "recipient";
    public const string ThiefParameter = "thief";
    public const string CategoryParameter = "category";
    public const string ReasonParameter = "reason";
    public const string OffererParameter = "offerer";
    public const string OfferedItemsParameter = "offered_items";
    public const string ForfeitWeaponParameter = "forfeit_weapon";
    public const string OfferParameter = "offer";
    public const string AbilityParameter = "ability";
    public const string CoverParameter = "cover";

    public const string ImpossibleCategory = "impossible";
    public const string UnsupportedCategory = "unsupported";

    public static readonly AIFunctionDeclaration AttackCharacter = AIFunctionFactory.CreateDeclaration(
        AttackCharacterName,
        "Resolve one character striking another with the weapon they are carrying. Use only for a direct melee strike.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{AttackerParameter}}": { "type": "string", "description": "Name of the character making the attack, exactly as given in the authoritative state." },
            "{{TargetParameter}}":   { "type": "string", "description": "Name of the character being attacked, exactly as given in the authoritative state." },
            "{{WeaponParameter}}":   { "type": "string", "description": "Name of the weapon the attacker is carrying, exactly as given in the authoritative state." }
          },
          "required": ["{{AttackerParameter}}", "{{TargetParameter}}", "{{WeaponParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration UseItem = AIFunctionFactory.CreateDeclaration(
        UseItemName,
        "Resolve a character using an item from their own inventory on themselves. The item is consumed.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}": { "type": "string", "description": "Name of the character using the item, exactly as given in the authoritative state." },
            "{{ItemParameter}}":  { "type": "string", "description": "Name of the item, exactly as given in that character's inventory." },
            "{{TargetParameter}}": { "type": "string", "description": "Optional. Only the character using the item is supported." }
          },
          "required": ["{{ActorParameter}}", "{{ItemParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration OpenContainer = AIFunctionFactory.CreateDeclaration(
        OpenContainerName,
        "Resolve a character opening a closed container that is in the room. Use only for opening — not for taking anything out.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}":     { "type": "string", "description": "Name of the character opening the container, exactly as given in the authoritative state." },
            "{{ContainerParameter}}": { "type": "string", "description": "Name of the container, exactly as given in the authoritative state." }
          },
          "required": ["{{ActorParameter}}", "{{ContainerParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration TakeItem = AIFunctionFactory.CreateDeclaration(
        TakeItemName,
        "Resolve a character taking one item out of an already-open container in the room, into their own inventory.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}":     { "type": "string", "description": "Name of the character taking the item, exactly as given in the authoritative state." },
            "{{ContainerParameter}}": { "type": "string", "description": "Name of the container the item is taken from, exactly as given in the authoritative state." },
            "{{ItemParameter}}":      { "type": "string", "description": "Name of the item being taken, exactly as it appears in the container's visible contents." }
          },
          "required": ["{{ActorParameter}}", "{{ContainerParameter}}", "{{ItemParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration InspectObject = AIFunctionFactory.CreateDeclaration(
        InspectObjectName,
        "Resolve a character examining an object in the room closely — studying its markings, wiping off grime, peering at an already-open container's contents. Use for looking, not for opening or taking. It consumes the turn and reveals what is found only to the one inspecting.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}":  { "type": "string", "description": "Name of the character examining the object, exactly as given in the authoritative state." },
            "{{ObjectParameter}}": { "type": "string", "description": "Name of the object being examined, exactly as given in the authoritative state." }
          },
          "required": ["{{ActorParameter}}", "{{ObjectParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration OpenExit = AIFunctionFactory.CreateDeclaration(
        OpenExitName,
        "Resolve a character opening a closed exit (such as a door) so it can be passed through. Use only for getting the exit open — not for going through it. Opening is a separate act from leaving.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}": { "type": "string", "description": "Name of the character opening the exit, exactly as given in the authoritative state." },
            "{{ExitParameter}}": { "type": "string", "description": "Name of the exit, exactly as given in the authoritative state." }
          },
          "required": ["{{ActorParameter}}", "{{ExitParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration EscapeEncounter = AIFunctionFactory.CreateDeclaration(
        EscapeEncounterName,
        "Resolve a character passing through an already-open exit to leave the encounter, abandoning the fight. Use only when the exit is open and the character is going through it.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}": { "type": "string", "description": "Name of the character leaving through the exit, exactly as given in the authoritative state." },
            "{{ExitParameter}}": { "type": "string", "description": "Name of the open exit they pass through, exactly as given in the authoritative state." }
          },
          "required": ["{{ActorParameter}}", "{{ExitParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration OfferSurrender = AIFunctionFactory.CreateDeclaration(
        OfferSurrenderName,
        "Resolve the ACTING character offering to give up the fight to one named opponent on concrete terms. The terms MUST include at least one carried item, unless the offerer carries nothing at all — then the weapon in hand alone is enough. The rule is that nothing is held back. This only puts the offer on the table: nothing changes hands, nobody is disarmed, and the offerer stays an active, targetable combatant until that opponent accepts on their own turn. The offer MUST promise something enforceable; a bare plea to be spared is not an offer.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{OffererParameter}}": { "type": "string", "description": "Name of the character offering to give up the fight — always the character whose intent you are adjudicating — exactly as given in the authoritative state." },
            "{{RecipientParameter}}": { "type": "string", "description": "Name of the ONE opposing character the terms are offered to, exactly as given in the authoritative state. Only they can accept." },
            "{{OfferedItemsParameter}}": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Names of the ordinary inventory items the offerer promises to hand over, exactly as they appear in the offerer's inventory. REQUIRED whenever the offerer is carrying anything: an offer that holds possessions back is refused. Leave empty only for an offerer whose inventory is truly empty. Never list the equipped weapon here — use forfeit_weapon for that."
            },
            "{{ForfeitWeaponParameter}}": { "type": "boolean", "description": "True when the offerer promises to give up the weapon in THEIR OWN hand as part of the terms. False when the intent is about a weapon somebody ELSE holds — demanding that an opponent throw down their blade is not this action at all." }
          },
          "required": ["{{OffererParameter}}", "{{RecipientParameter}}", "{{ForfeitWeaponParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration AcceptSurrender = AIFunctionFactory.CreateDeclaration(
        AcceptSurrenderName,
        "Resolve the ACTING character taking up a pending offer of surrender that was made TO THEM. Use only when the acting character is the named recipient of that offer and chooses to accept it. The promised assets move and the offerer yields; nothing is negotiated further.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{RecipientParameter}}": { "type": "string", "description": "Name of the character accepting the offer — always the character whose intent you are adjudicating — exactly as given in the authoritative state." },
            "{{OfferParameter}}": { "type": "string", "description": "The stable id of the pending offer being accepted, exactly as listed in the authoritative state (for example 'offer-1')." }
          },
          "required": ["{{RecipientParameter}}", "{{OfferParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration UseAbility = AIFunctionFactory.CreateDeclaration(
        UseAbilityName,
        "Resolve the ACTING character using one of their own trained abilities, on themselves or on another character. Use only an ability listed against that character in the authoritative state, named by its exact id, and only while it has uses left.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}": { "type": "string", "description": "Name of the character using the ability — always the character whose intent you are adjudicating — exactly as given in the authoritative state." },
            "{{AbilityParameter}}": { "type": "string", "description": "The stable id of the ability, exactly as listed against that character in the authoritative state (for example 'guard-ally')." },
            "{{TargetParameter}}": { "type": "string", "description": "Name of the character the ability is aimed at, exactly as given in the authoritative state. Omit for an ability that needs no target." }
          },
          "required": ["{{ActorParameter}}", "{{AbilityParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration Defend = AIFunctionFactory.CreateDeclaration(
        DefendName,
        "Resolve the ACTING character bracing behind their guard instead of striking — standing their ground, keeping their guard up, preparing to parry or turn a blow. Every active character can do this, as often as they like; it costs the whole turn and softens the next blow that lands on them.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}": { "type": "string", "description": "Name of the character bracing — always the character whose intent you are adjudicating — exactly as given in the authoritative state." }
          },
          "required": ["{{ActorParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration GiveItem = AIFunctionFactory.CreateDeclaration(
        GiveItemName,
        "Resolve the ACTING character handing one of their own ordinary inventory items to another character present in the room. Use only when the acting character gives away an item they carry; the recipient may be an ally or an enemy. Not for an equipped weapon.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}":     { "type": "string", "description": "Name of the character giving the item — always the character whose intent you are adjudicating — exactly as given in the authoritative state." },
            "{{RecipientParameter}}": { "type": "string", "description": "Name of the character receiving the item, exactly as given in the authoritative state." },
            "{{ItemParameter}}":      { "type": "string", "description": "Name of the item being given, exactly as it appears in the giver's inventory." }
          },
          "required": ["{{ActorParameter}}", "{{RecipientParameter}}", "{{ItemParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration DropItem = AIFunctionFactory.CreateDeclaration(
        DropItemName,
        "Resolve the ACTING character dropping one of their own ordinary inventory items onto the floor, where anyone may later pick it up. Not for an equipped weapon.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}": { "type": "string", "description": "Name of the character dropping the item — always the character whose intent you are adjudicating — exactly as given in the authoritative state." },
            "{{ItemParameter}}":  { "type": "string", "description": "Name of the item being dropped, exactly as it appears in the actor's inventory." }
          },
          "required": ["{{ActorParameter}}", "{{ItemParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration StealItem = AIFunctionFactory.CreateDeclaration(
        StealItemName,
        "Resolve the ACTING character trying to snatch one ordinary inventory item from another active character. The attempt is always noticed and may fail. Use only for an item the thief has a legitimate reason to know the target carries. Equipped weapons cannot be stolen.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ThiefParameter}}":  { "type": "string", "description": "Name of the character attempting the theft — always the character whose intent you are adjudicating — exactly as given in the authoritative state." },
            "{{TargetParameter}}": { "type": "string", "description": "Name of the character being stolen from, exactly as given in the authoritative state." },
            "{{ItemParameter}}":   { "type": "string", "description": "Name of the item being stolen, exactly as it appears in the target's inventory." }
          },
          "required": ["{{ThiefParameter}}", "{{TargetParameter}}", "{{ItemParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration IntimidateCharacter = AIFunctionFactory.CreateDeclaration(
        IntimidateCharacterName,
        "Resolve the ACTING character openly threatening ONE enemy still fighting, to frighten them. Use only when the character SPOKE a threat aloud on this turn and no blow was struck: a threat that comes with a blow is the attack, not this. Success only makes the target more afraid; it never disarms them, never takes anything from them, never makes them yield or flee, and never costs them a turn. Each character may try this on each enemy once in the whole fight.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}":  { "type": "string", "description": "Name of the character making the threat - always the character whose intent you are adjudicating - exactly as given in the authoritative state." },
            "{{TargetParameter}}": { "type": "string", "description": "Name of the ONE opposing character being threatened, exactly as given in the authoritative state. It must be the same person the spoken threat was addressed to." }
          },
          "required": ["{{ActorParameter}}", "{{TargetParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration SteadyAlly = AIFunctionFactory.CreateDeclaration(
        SteadyAllyName,
        "Resolve the ACTING character spending their whole turn steadying ONE companion who has lost their nerve - a word of reassurance, encouragement or an order to hold. Use only when the character SPOKE to that companion aloud on this turn and did nothing else: it cannot be combined with a blow, a movement, an item or an ability. It makes the companion less afraid and nothing more; it heals nothing and changes no odds.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}":  { "type": "string", "description": "Name of the character doing the steadying - always the character whose intent you are adjudicating - exactly as given in the authoritative state." },
            "{{TargetParameter}}": { "type": "string", "description": "Name of the ONE companion being steadied, exactly as given in the authoritative state. Never the acting character themselves, and it must be the same person the spoken words were addressed to." }
          },
          "required": ["{{ActorParameter}}", "{{TargetParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration TakeCover = AIFunctionFactory.CreateDeclaration(
        TakeCoverName,
        "Resolve the ACTING character moving behind one environmental object with the cover capability and taking shelter there — ducking behind something solid rather than standing exposed. Use only when the character is not already behind that same cover and it is not full or destroyed. It costs the whole turn and makes no roll; the character stays there until they leave, are exposed by another action, or the cover is destroyed.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}": { "type": "string", "description": "Name of the character taking cover — always the character whose intent you are adjudicating — exactly as given in the authoritative state." },
            "{{CoverParameter}}": { "type": "string", "description": "Name of the environmental cover object, exactly as given in the authoritative state." }
          },
          "required": ["{{ActorParameter}}", "{{CoverParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration LeaveCover = AIFunctionFactory.CreateDeclaration(
        LeaveCoverName,
        "Resolve the ACTING character deliberately stepping out from the cover they occupy, with nothing else attempted. Use only when the character currently occupies cover and chooses to leave it as the whole of their turn — not when they are also striking, reaching for something, or otherwise acting, since an accepted exposing action already vacates cover as part of resolving it without a separate turn.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}": { "type": "string", "description": "Name of the character stepping out — always the character whose intent you are adjudicating — exactly as given in the authoritative state." }
          },
          "required": ["{{ActorParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration DamageEnvironmentalObject = AIFunctionFactory.CreateDeclaration(
        DamageEnvironmentalObjectName,
        "Resolve the ACTING character deliberately striking one present, non-destroyed environmental object (such as a piece of cover) with the weapon in hand, rather than striking a character. Use only for a deliberate blow AT the object itself, not for an ordinary attack against a covered character (that is attack_character; cover there is applied automatically). No roll is made. Refused against the very cover the acting character currently occupies.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}": { "type": "string", "description": "Name of the character striking the object — always the character whose intent you are adjudicating — exactly as given in the authoritative state." },
            "{{ObjectParameter}}": { "type": "string", "description": "Name of the environmental object being struck, exactly as given in the authoritative state." }
          },
          "required": ["{{ActorParameter}}", "{{ObjectParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration RejectAction = AIFunctionFactory.CreateDeclaration(
        RejectActionName,
        "Refuse the stated intent because it cannot happen. A last resort: use it only when NONE of the other actions offered to you fits the primary physical deed the character described. Nothing whatsoever happens in the world when you call this.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{CategoryParameter}}": {
              "type": "string",
              "enum": ["{{ImpossibleCategory}}", "{{UnsupportedCategory}}"],
              "description": "'impossible' if this character simply could not do it. 'unsupported' if a person could genuinely try it but the world has no way to resolve it."
            },
            "{{ReasonParameter}}": {
              "type": "string",
              "description": "One or two sentences, addressed to the character, explaining why the attempt did not happen. Nothing happened, so narrate nothing: no blow landing, no contact, no impact, no reaction, no object moving. Never write a sentence like \"your blade strikes his shoulder, but...\" — if a blow would land, this is not a refusal. Say only what the character feels stopping them, and never name the world, its rules or which actions exist."
            }
          },
          "required": ["{{CategoryParameter}}", "{{ReasonParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly IReadOnlyList<AITool> All =
    [
        AttackCharacter, UseItem, OpenContainer, TakeItem, InspectObject, OpenExit, EscapeEncounter,
        OfferSurrender, AcceptSurrender, UseAbility, Defend, GiveItem, DropItem, StealItem,
        IntimidateCharacter, SteadyAlly, TakeCover, LeaveCover, DamageEnvironmentalObject, RejectAction
    ];

    /// <summary>
    /// Every engine-action tool by name — the whole set minus the rejection. Used to map the rulebook
    /// resolver's candidate action names to the concrete tool declarations exposed for one adjudication.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, AIFunctionDeclaration> EngineActionsByName =
        new Dictionary<string, AIFunctionDeclaration>(StringComparer.OrdinalIgnoreCase)
        {
            [AttackCharacterName] = AttackCharacter,
            [UseItemName] = UseItem,
            [OpenContainerName] = OpenContainer,
            [TakeItemName] = TakeItem,
            [InspectObjectName] = InspectObject,
            [OpenExitName] = OpenExit,
            [EscapeEncounterName] = EscapeEncounter,
            [OfferSurrenderName] = OfferSurrender,
            [AcceptSurrenderName] = AcceptSurrender,
            [UseAbilityName] = UseAbility,
            [DefendName] = Defend,
            [GiveItemName] = GiveItem,
            [DropItemName] = DropItem,
            [StealItemName] = StealItem,
            [IntimidateCharacterName] = IntimidateCharacter,
            [SteadyAllyName] = SteadyAlly,
            [TakeCoverName] = TakeCover,
            [LeaveCoverName] = LeaveCover,
            [DamageEnvironmentalObjectName] = DamageEnvironmentalObject
        };
}
