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

    public DungeonMasterAgent(AgentModelProfile profile, TracingChatClient client, PromptLibrary prompts)
        : base(AgentIdentifier, profile, client, prompts.Render("dungeon-master.system"))
    {
        _prompts = prompts;
    }

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
        Conversation.AppendUser(_prompts.Render("dungeon-master.adjudicate", new Dictionary<string, string?>
        {
            ["state"] = authoritativeState,
            ["character"] = characterName,
            ["intent"] = intent
        }));

        return CallModelAsync("dm.adjudicate", DungeonMasterTools.All, cancellationToken);
    }

    /// <summary>Re-asks for a tool call after the DM replied with prose instead.</summary>
    public Task<ChatResponse> RetryProposeActionAsync(string characterName, CancellationToken cancellationToken)
    {
        Conversation.AppendUser(_prompts.Render("dungeon-master.adjudicate-retry", new Dictionary<string, string?>
        {
            ["character"] = characterName
        }));

        return CallModelAsync("dm.adjudicate.retry", DungeonMasterTools.All, cancellationToken);
    }

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
