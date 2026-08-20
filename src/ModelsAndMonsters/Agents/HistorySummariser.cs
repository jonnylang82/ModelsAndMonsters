using ModelsAndMonsters.AI;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Agents;

/// <summary>
/// Compresses the older part of a character's history into a short first-person recap, so a long fight does
/// not grow that character's context without bound. A character's recent turns are kept in full; everything
/// before them (including any earlier recap) is folded into one running summary.
/// </summary>
/// <remarks>
/// Like the intent parser, it is stateless: each call builds a fresh context from the rendered older history
/// and returns plain prose, never a tool call. It runs at a low temperature for a steady, faithful recap and
/// never accumulates the encounter's history itself.
/// </remarks>
public sealed class HistorySummariser : ModelAgent
{
    public const string AgentIdentifier = "HistorySummariser";

    public HistorySummariser(AgentModelProfile profile, TracingChatClient client, PromptLibrary prompts)
        : base(AgentIdentifier, profile, client, prompts.Render("character.summarise-history.system"))
    {
    }

    /// <summary>
    /// Folds the rendered older history (which may itself open with an earlier recap) into a fresh, compact
    /// recap. Returns the recap text; empty if the model produced nothing usable, in which case the caller
    /// leaves the history untrimmed rather than dropping it.
    /// </summary>
    public async Task<string> SummariseAsync(string olderHistory, CancellationToken cancellationToken)
    {
        var conversation = new AgentConversation(AgentName, Conversation.SystemPrompt);
        conversation.AppendUser(olderHistory);

        var response = await CallModelAsync(conversation, "character.summarise-history", tools: null, cancellationToken)
            .ConfigureAwait(false);

        return ModelText.Clean(response);
    }
}
