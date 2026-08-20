using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Agents;

/// <summary>
/// A stateless, zero-temperature parser that turns a character's free prose — written when it narrated
/// instead of calling a tool — into the structured say / ask_dm / take_action calls it implies. One prose
/// reply can yield several (a spoken line AND an action), which is what lets a character talk and act in one
/// breath instead of being nudged for one call at a time.
/// </summary>
/// <remarks>
/// It carries no conversation: each parse builds a fresh context — the parse rules plus that one prose reply
/// — so it is cheap, reproducible, and never sees or accumulates the encounter's history. It runs at
/// temperature zero for a stable reading, and is a fallback for prose only: the character still holds and
/// calls the real tools; this steps in when a reply arrived as text.
/// </remarks>
public sealed class IntentParser : ModelAgent
{
    public const string AgentIdentifier = "IntentParser";

    public IntentParser(AgentModelProfile profile, TracingChatClient client, PromptLibrary prompts)
        : base(AgentIdentifier, profile, client, prompts.Render("character.parse-intent.system"))
    {
    }

    /// <summary>
    /// Reads one prose reply into the tool calls it implies, in the order the model emitted them (the caller
    /// orders speech before the turn-ending action). Empty when the prose implies nothing callable.
    /// </summary>
    public async Task<IReadOnlyList<FunctionCallContent>> ParseAsync(string prose, CancellationToken cancellationToken)
    {
        // A fresh, single-use conversation: the parser is context-free by design and never inherits the
        // character's growing history — that is what keeps it cheap and its reading stable.
        var conversation = new AgentConversation(AgentName, Conversation.SystemPrompt);
        conversation.AppendUser(prose);

        var response = await CallModelAsync(conversation, "character.parse-intent", CharacterTools.All, cancellationToken)
            .ConfigureAwait(false);

        return GetToolCalls(response);
    }
}
