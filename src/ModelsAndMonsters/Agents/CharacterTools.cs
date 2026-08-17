using Microsoft.Extensions.AI;

namespace ModelsAndMonsters.Agents;

/// <summary>
/// The only two tools a character agent ever sees. Both take free-form natural language.
/// </summary>
/// <remarks>
/// These are <see cref="AIFunctionDeclaration"/>s, not invocable functions. They carry a name,
/// description and schema and nothing else, so there is no implementation for any middleware to call
/// even if some were introduced by accident. The application dispatches them by hand.
/// </remarks>
public static class CharacterTools
{
    public const string AskDmName = "ask_dm";
    public const string TakeActionName = "take_action";
    public const string EndTurnName = "end_turn";
    public const string QuestionParameter = "question";
    public const string IntentParameter = "intent";
    public const string ReasonParameter = "reason";

    public static readonly AIFunctionDeclaration AskDm = AIFunctionFactory.CreateDeclaration(
        AskDmName,
        "Ask the Dungeon Master something about what you can currently perceive. Asking does not use up your turn.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{QuestionParameter}}": {
              "type": "string",
              "description": "The question you want to ask, in your own words. For example: 'Does the goblin look badly wounded?'"
            }
          },
          "required": ["{{QuestionParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly AIFunctionDeclaration TakeAction = AIFunctionFactory.CreateDeclaration(
        TakeActionName,
        "Attempt one concrete thing, described in your own words. Your turn ends only if it actually takes effect.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{IntentParameter}}": {
              "type": "string",
              "description": "What you attempt to do, as you would say it. For example: 'I bring my sword down hard on the goblin's shoulder.'"
            }
          },
          "required": ["{{IntentParameter}}"]
        }
        """),
        returnJsonSchema: null);

    /// <summary>
    /// Lets a character choose to do nothing. Without it, a character who has decided to hold back,
    /// give up or wait has no way to say so, and burns its whole turn on attempts the world refuses.
    /// </summary>
    public static readonly AIFunctionDeclaration EndTurn = AIFunctionFactory.CreateDeclaration(
        EndTurnName,
        "Choose to do nothing this turn. Use this when holding still, waiting, giving up or standing down is genuinely what you want to do. This ends your turn.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{ReasonParameter}}": {
              "type": "string",
              "description": "Why you do nothing, in your own words. For example: 'I have no strength left, and I stay where I am.'"
            }
          },
          "required": ["{{ReasonParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly IReadOnlyList<AITool> All = [AskDm, TakeAction, EndTurn];
}
