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
    public const string SayName = "say";
    public const string EndTurnName = "end_turn";
    public const string QuestionParameter = "question";
    public const string IntentParameter = "intent";
    public const string MessageParameter = "message";
    public const string ReasonParameter = "reason";

    /// <summary>
    /// The optional structured-speech field carried by every turn-taking tool. A character that wants to
    /// speak while it acts, asks or passes puts the exact words here, in its ONE model call.
    /// </summary>
    /// <remarks>
    /// Speech used to be recoverable only two ways: a separate <c>say</c> call, or — when a small model
    /// replied in prose without calling anything — a heuristic that hunted for quoted text near a speech
    /// verb. That heuristic could not be made right. It needed a growing list of verbs, it read
    /// <c>the blade called "Goblin's Bite"</c> as somebody speaking, and it could not see speech that used
    /// no verb at all. Declaring speech as a field makes the character's own reply authoritative: what is
    /// in <c>utterances</c> is spoken, and nothing else is, whatever quotation marks appear elsewhere.
    /// </remarks>
    public const string UtterancesParameter = "utterances";

    /// <summary>
    /// The optional structured addressee that rides with <see cref="UtterancesParameter"/>: the one character
    /// the words were aimed at. Declared, never inferred.
    /// </summary>
    /// <remarks>
    /// Two v0.8 actions - threatening an enemy and steadying an ally - are defined as speech aimed at one
    /// person, so "who was this said to" becomes a mechanical question. The alternative is reading the words
    /// for a name, which is exactly the kind of hand-built language parsing this project keeps deleting: it
    /// cannot tell "Vark, you are finished" from "Vark is finished", and it breaks the moment a character
    /// addresses somebody by a nickname. Declaring it makes the speaker authoritative about their own aim.
    /// It stays OPTIONAL: a line called to the whole room has no addressee, and a character that omits it
    /// simply leaves the Dungeon Master's binding of the target unchallenged. What it can never do is
    /// silently disagree - an action aimed at somebody other than the declared addressee is refused.
    /// </remarks>
    public const string AddressedToParameter = "addressed_to";

    private const string UtterancesSchema = $$"""
        "{{UtterancesParameter}}": {
          "type": "array",
          "description": "Anything you say ALOUD as you do this, if anything. Put the exact words you speak here — not a description of speaking, and not any other quoted text. Everyone still alive in the room hears them. Leave this out entirely if you say nothing. Speaking costs you nothing, and you may say at most one thing in a turn.",
          "items": {
            "type": "string",
            "description": "The exact words you speak aloud. For example: 'Elara, get whatever is in that chest — I will hold off the captain.'"
          }
        },
        "{{AddressedToParameter}}": {
          "type": "string",
          "description": "If you are speaking to ONE person in particular — threatening them, warning them, steadying them, answering them — put their name here, exactly as it is given to you. Leave it out when you are calling to the room as a whole. Everyone still hears you either way."
        }
        """;

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
            },
            {{UtterancesSchema}}
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
            },
            {{UtterancesSchema}}
          },
          "required": ["{{IntentParameter}}"]
        }
        """),
        returnJsonSchema: null);

    /// <summary>
    /// Lets a character speak aloud to the whole room. It does not consume the turn, so a character can
    /// speak and then still ask, act or end its turn; the harness enforces the once-per-turn limit and
    /// rejects empty or oversized messages.
    /// </summary>
    public static readonly AIFunctionDeclaration Say = AIFunctionFactory.CreateDeclaration(
        SayName,
        "Say something out loud. Everyone still alive in the room hears it. Speaking does not use up your turn, and you may speak at most once per turn.",
        ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "{{MessageParameter}}": {
              "type": "string",
              "description": "The exact words you speak aloud, as you would say them. For example: 'Elara, get whatever is in that chest — I'll hold off the captain.'"
            }
          },
          "required": ["{{MessageParameter}}"]
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
            },
            {{UtterancesSchema}}
          },
          "required": ["{{ReasonParameter}}"]
        }
        """),
        returnJsonSchema: null);

    public static readonly IReadOnlyList<AITool> All = [AskDm, TakeAction, Say, EndTurn];

    /// <summary>The tool names, used to recognise a tool call a model wrote as prose.</summary>
    public static readonly IReadOnlyList<string> Names = [AskDmName, TakeActionName, SayName, EndTurnName];
}
