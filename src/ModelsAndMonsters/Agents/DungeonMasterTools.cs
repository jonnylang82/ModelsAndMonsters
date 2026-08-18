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
    public const string RejectActionName = "reject_action";

    public const string AttackerParameter = "attacker";
    public const string TargetParameter = "target";
    public const string WeaponParameter = "weapon";
    public const string ActorParameter = "actor";
    public const string ItemParameter = "item";
    public const string ContainerParameter = "container";
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

    public static readonly IReadOnlyList<AITool> All = [AttackCharacter, UseItem, OpenContainer, TakeItem, RejectAction];
}
