using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Agents;

/// <summary>
/// An autonomous inhabitant of the world. The Hero and the Monster are both instances of this class
/// with different definitions, prompts and model profiles — there is no separate monster behaviour.
/// </summary>
/// <remarks>
/// The agent is given only <see cref="CharacterTools"/>. It has no idea the engine exists, cannot name
/// an engine action, and never learns the mechanical result of anything except through the Dungeon
/// Master's words.
/// </remarks>
public sealed class CharacterAgent : ModelAgent
{
    public CharacterAgent(
        CharacterDefinition definition,
        AgentModelProfile profile,
        TracingChatClient client,
        string systemPrompt)
        : base(definition.Name, profile, client, systemPrompt)
    {
        Definition = definition;
    }

    public CharacterDefinition Definition { get; }

    public string CharacterId => Definition.Id;

    public string Name => Definition.Name;

    /// <summary>Injects this turn's exact self-state and the narration this character has not yet heard.</summary>
    public void BeginTurn(string turnContext) => Conversation.AppendUser(turnContext);

    /// <summary>Asks the character what it wants to do, exposing only ask_dm and take_action.</summary>
    public Task<ChatResponse> DecideAsync(CancellationToken cancellationToken) =>
        CallModelAsync("character.decide", CharacterTools.All, cancellationToken);

    /// <summary>Used when the model replied without calling either tool.</summary>
    public void AppendNudge(string text) => Conversation.AppendUser(text);

    /// <summary>
    /// Swaps this character's most recent (prose) reply for one carrying a structured tool call, when the
    /// harness recovered a call the model wrote as text. Keeps the history valid for the tool result that
    /// follows; the original prose remains in the trace.
    /// </summary>
    public void ReplaceLastReplyWithToolCall(FunctionCallContent call) =>
        Conversation.ReplaceLastMessage(new ChatMessage(ChatRole.Assistant, [call]));

    /// <summary>
    /// Swaps this character's most recent (prose) reply for one carrying several structured tool calls at
    /// once — used when the intent parser reads a spoken line AND an action out of a single prose reply, so
    /// both are dispatched from one turn rather than nudged for one at a time.
    /// </summary>
    public void ReplaceLastReplyWithToolCalls(IReadOnlyList<FunctionCallContent> calls) =>
        Conversation.ReplaceLastMessage(new ChatMessage(ChatRole.Assistant, [.. calls]));

    /// <summary>Records where this character's history stands at turn start, to compact back to when the turn ends.</summary>
    public int MarkHistory() => Conversation.Count;

    /// <summary>Drops this turn's failed prose replies and nudges once the turn has resolved, keeping the clean calls.</summary>
    public void CompactTurnHistory(int mark) => Conversation.CompactTurn(mark);

    /// <summary>A rough estimate of the tokens this character's next request would send, for the trim trigger.</summary>
    public int EstimateHistoryTokens() => ContextTruncation.EstimateSentTokens(Conversation.BuildRequestMessages());

    /// <summary>
    /// The effective summarisation budget for this character: the configured budget, but capped so the full
    /// request — messages, tool scaffolding and room for the reply — stays inside the model's context window.
    /// Derived from this character's own window, output budget and measured prompt overhead, so it self-
    /// calibrates per model rather than trusting a fixed number that ignores the window and the tool overhead.
    /// </summary>
    public int EffectiveHistoryBudget(int configuredBudget) =>
        ContextTruncation.EffectiveHistoryBudget(
            configuredBudget, Profile.BindingContextWindow, Profile.MaxOutputTokens, ObservedPromptOverheadTokens);

    /// <summary>Plans a summary trim keeping the last <paramref name="keepRecentTurns"/> turns full; false if there is nothing older to fold.</summary>
    public bool TryPlanHistorySummary(int keepRecentTurns, out int boundary, out string olderHistory) =>
        Conversation.TryPlanSummaryTrim(keepRecentTurns, out boundary, out olderHistory);

    /// <summary>Folds the older turns into the given running-summary text, keeping the recent turns intact.</summary>
    public void ApplyHistorySummary(int boundary, string summary) => Conversation.ApplySummary(boundary, summary);
}
