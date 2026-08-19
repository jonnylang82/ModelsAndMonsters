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
    public const string SurrenderName = "surrender";
    public const string GiveItemName = "give_item";
    public const string DropItemName = "drop_item";
    public const string StealItemName = "steal_item";
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

    public static readonly AIFunctionDeclaration Surrender = AIFunctionFactory.CreateDeclaration(
        SurrenderName,
        "Resolve a character yielding and taking no further part in the fight. Use only when the ACTING character gives up their OWN fight — never when they merely tell someone else to surrender.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ActorParameter}}": { "type": "string", "description": "Name of the character who is surrendering, exactly as given in the authoritative state. This is always the character whose intent you are adjudicating." }
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

    public static readonly AIFunctionDeclaration RejectAction = AIFunctionFactory.CreateDeclaration(
        RejectActionName,
        "Refuse the stated intent because it cannot happen. Use this whenever the intent is not a direct weapon strike or an item use.",
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
              "description": "One or two sentences, addressed to the character, explaining why the attempt did not happen."
            }
          },
          "required": ["{{CategoryParameter}}", "{{ReasonParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly IReadOnlyList<AITool> All =
    [
        AttackCharacter, UseItem, OpenContainer, TakeItem, InspectObject, OpenExit, EscapeEncounter,
        Surrender, GiveItem, DropItem, StealItem, RejectAction
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
            [SurrenderName] = Surrender,
            [GiveItemName] = GiveItem,
            [DropItemName] = DropItem,
            [StealItemName] = StealItem
        };
}
