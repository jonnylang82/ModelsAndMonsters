using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Agents;

/// <summary>
/// The only agent permitted to reach the game engine, and the only thing characters ever talk to.
/// </summary>
/// <remarks>
/// <para>
/// Each method is one job from the DM's system prompt and injects a fresh authoritative snapshot
/// first, so the engine — not this conversation's memory — is always the source of truth.
/// </para>
/// <para>
/// Note that tools are supplied only by <see cref="ProposeActionAsync"/>. Narration and answering are
/// toolless calls, which makes it structurally impossible for the DM to change the world while it is
/// merely describing it.
/// </para>
/// </remarks>
public sealed class DungeonMasterAgent : ModelAgent
{
    public const string AgentIdentifier = "DungeonMaster";

    private readonly PromptLibrary _prompts;
    private readonly bool _isolateAdjudication;
    private AgentConversation? _adjudication;

    public DungeonMasterAgent(
        AgentModelProfile profile,
        TracingChatClient client,
        PromptLibrary prompts,
        bool isolateAdjudicationContext = true)
        : base(AgentIdentifier, profile, client, prompts.Render("dungeon-master.system"))
    {
        _prompts = prompts;
        _isolateAdjudication = isolateAdjudicationContext;
    }

    /// <summary>
    /// The conversation adjudication runs in.
    /// </summary>
    /// <remarks>
    /// Measured on a live run: replaying one traced adjudication returned the correct action 4 times
    /// out of 4 on a clean context and 0 out of 4 with the Dungeon Master's own thirteen-message
    /// history attached — a history that contained no refusals at all, only narration and answers.
    /// Narrating appears to put the model into prose mode, and prose mode classifies badly. Narration
    /// and answering still keep their continuity; only the classification is given a clean slate.
    /// Set <c>IsolateAdjudicationContext</c> to false to compare the two.
    /// </remarks>
    private AgentConversation AdjudicationConversation =>
        _isolateAdjudication ? _adjudication ??= NewAdjudicationConversation() : Conversation;

    private AgentConversation NewAdjudicationConversation() =>
        new(AgentName, Conversation.SystemPrompt);

    /// <summary>True when adjudication runs on a context separate from narration.</summary>
    public bool AdjudicationContextIsIsolated => _isolateAdjudication;

    /// <summary>Converts authoritative state into prose for everyone in the room.</summary>
    public async Task<string> NarrateAsync(
        string authoritativeState,
        string context,
        string purpose,
        CancellationToken cancellationToken)
    {
        Conversation.AppendUser(_prompts.Render("dungeon-master.narrate", new Dictionary<string, string?>
        {
            ["state"] = authoritativeState,
            ["context"] = context
        }));

        var response = await CallModelAsync(purpose, tools: null, cancellationToken).ConfigureAwait(false);
        return ModelText.Clean(response);
    }

    /// <summary>Answers one character's question using what that character could perceive.</summary>
    public async Task<string> AnswerQuestionAsync(
        string authoritativeState,
        string characterName,
        string question,
        CancellationToken cancellationToken)
    {
        Conversation.AppendUser(_prompts.Render("dungeon-master.answer", new Dictionary<string, string?>
        {
            ["state"] = authoritativeState,
            ["character"] = characterName,
            ["question"] = question
        }));

        var response = await CallModelAsync("dm.answer", tools: null, cancellationToken).ConfigureAwait(false);
        return ModelText.Clean(response);
    }

    /// <summary>
    /// Asks the DM to translate a character's natural-language intent into exactly one tool call.
    /// The response is returned raw: interpreting and dispatching it is the orchestrator's job.
    /// </summary>
    public Task<ChatResponse> ProposeActionAsync(
        string authoritativeState,
        string characterName,
        string intent,
        CancellationToken cancellationToken)
    {
        // Each attempt starts fresh, so one adjudication cannot colour the next.
        _adjudication = _isolateAdjudication ? NewAdjudicationConversation() : null;

        AdjudicationConversation.AppendUser(_prompts.Render("dungeon-master.adjudicate", new Dictionary<string, string?>
        {
            ["state"] = authoritativeState,
            ["character"] = characterName,
            ["intent"] = intent
        }));

        return CallModelAsync(AdjudicationConversation, "dm.adjudicate", DungeonMasterTools.All, cancellationToken);
    }

    /// <summary>Re-asks for a tool call after the DM replied with prose instead.</summary>
    public Task<ChatResponse> RetryProposeActionAsync(string characterName, CancellationToken cancellationToken)
    {
        AdjudicationConversation.AppendUser(_prompts.Render("dungeon-master.adjudicate-retry", new Dictionary<string, string?>
        {
            ["character"] = characterName
        }));

        return CallModelAsync(AdjudicationConversation, "dm.adjudicate.retry", DungeonMasterTools.All, cancellationToken);
    }

    /// <summary>
    /// Answers a tool call the DM made while adjudicating. Routed to whichever conversation asked for
    /// it, so every tool call is answered in the context that produced it.
    /// </summary>
    public void AppendAdjudicationToolResult(FunctionCallContent call, object? result) =>
        AdjudicationConversation.AppendToolResult(call.CallId, result);

    /// <summary>
    /// Narrates what the engine actually did. Called only after the engine result has been handed back
    /// to the DM as the tool result, so the DM is describing a fact rather than deciding one.
    /// </summary>
    public async Task<string> NarrateOutcomeAsync(
        string engineResultSummary,
        string authoritativeState,
        CancellationToken cancellationToken)
    {
        Conversation.AppendUser(_prompts.Render("dungeon-master.outcome", new Dictionary<string, string?>
        {
            ["result"] = engineResultSummary,
            ["state"] = authoritativeState
        }));

        var response = await CallModelAsync("dm.narrate.outcome", tools: null, cancellationToken).ConfigureAwait(false);
        return ModelText.Clean(response);
    }

    /// <summary>
    /// Narrates a character deliberately doing nothing. No engine action is involved; the world simply
    /// has to describe someone holding back so the other character can perceive it.
    /// </summary>
    public async Task<string> NarratePassAsync(
        string characterName,
        string reason,
        string authoritativeState,
        CancellationToken cancellationToken)
    {
        Conversation.AppendUser(_prompts.Render("dungeon-master.pass", new Dictionary<string, string?>
        {
            ["character"] = characterName,
            ["reason"] = reason,
            ["state"] = authoritativeState
        }));

        var response = await CallModelAsync("dm.narrate.pass", tools: null, cancellationToken).ConfigureAwait(false);
        return ModelText.Clean(response);
    }

    /// <summary>Turns an engine rejection into an in-world explanation the character can act on.</summary>
    public async Task<string> ExplainEngineRejectionAsync(
        string rejectionReason,
        string characterName,
        CancellationToken cancellationToken)
    {
        Conversation.AppendUser(_prompts.Render("dungeon-master.engine-rejection", new Dictionary<string, string?>
        {
            ["reason"] = rejectionReason,
            ["character"] = characterName
        }));

        var response = await CallModelAsync("dm.explain.rejection", tools: null, cancellationToken).ConfigureAwait(false);
        return ModelText.Clean(response);
    }
}
