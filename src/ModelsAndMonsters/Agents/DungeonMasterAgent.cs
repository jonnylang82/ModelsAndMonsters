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
/// first, so the engine — not conversation memory — is always the source of truth.
/// </para>
/// <para>
/// By default the DM does not carry one ever-growing conversation. Each task runs on a
/// <em>projection</em>: the system prompt, a fresh state snapshot, and only the immediately relevant
/// context. This is deliberate. The DM re-embeds a full authoritative state block in every task, so a
/// single accumulating conversation grows quickly and, at a local model's context window, gets
/// silently truncated by the provider. It also caused a measured classification problem: with the full
/// narration history attached, adjudication dropped from correct-every-time to wrong-every-time,
/// because narrating puts the model into prose mode. The complete record of what the DM did still
/// lives in the trace — every projection's messages and response are recorded there. Set
/// <c>ProjectDungeonMasterContext</c> to false to run the old single-conversation behaviour for
/// comparison.
/// </para>
/// <para>
/// Tools are supplied only by <see cref="ProposeActionAsync"/>. Narration and answering are toolless
/// calls, which makes it structurally impossible for the DM to change the world while describing it.
/// </para>
/// </remarks>
public sealed class DungeonMasterAgent : ModelAgent
{
    public const string AgentIdentifier = "DungeonMaster";

    private readonly PromptLibrary _prompts;
    private readonly bool _useProjections;

    // The adjudication projection persists between the proposal call and its tool-result answer, so a
    // field rather than a local. Reset at the start of each new adjudication.
    private AgentConversation? _adjudication;

    // Whether the DM has narrated at least once, so updates after the opening can be told to describe
    // only the new development. Deliberately a flag, not the previous text: quoting the last narration
    // back to the model made it echo that narration instead of narrating the new event.
    private bool _hasNarrated;

    public DungeonMasterAgent(
        AgentModelProfile profile,
        TracingChatClient client,
        PromptLibrary prompts,
        bool projectContext = true)
        : base(AgentIdentifier, profile, client, prompts.Render("dungeon-master.system"))
    {
        _prompts = prompts;
        _useProjections = projectContext;
    }

    /// <summary>True when the DM runs each task on a projection rather than one growing conversation.</summary>
    public bool UsesProjections => _useProjections;

    /// <summary>A fresh conversation seeded with only the system prompt.</summary>
    private AgentConversation NewProjection() => new(AgentName, Conversation.SystemPrompt);

    /// <summary>Where narration and answering run: a fresh projection, or the shared conversation.</summary>
    private AgentConversation NarrationContext() => _useProjections ? NewProjection() : Conversation;

    /// <summary>Where adjudication runs: the current isolated projection, or the shared conversation.</summary>
    private AgentConversation AdjudicationContext() =>
        _useProjections ? _adjudication ??= NewProjection() : Conversation;

    /// <summary>Converts authoritative state into prose for everyone in the room.</summary>
    public Task<string> NarrateAsync(
        string authoritativeState,
        string context,
        string purpose,
        CancellationToken cancellationToken) =>
        NarrateProjectedAsync(
            _prompts.Render("dungeon-master.narrate", new Dictionary<string, string?>
            {
                ["state"] = authoritativeState,
                ["context"] = context
            }),
            purpose,
            cancellationToken);

    /// <summary>
    /// Narrates what the engine actually did. Called only after the engine result has been handed back
    /// to the DM as the tool result, so the DM is describing a fact rather than deciding one.
    /// </summary>
    public Task<string> NarrateOutcomeAsync(
        string actorName,
        string engineResultSummary,
        string authoritativeState,
        CancellationToken cancellationToken) =>
        NarrateProjectedAsync(
            _prompts.Render("dungeon-master.outcome", new Dictionary<string, string?>
            {
                ["actor"] = actorName,
                ["result"] = engineResultSummary,
                ["state"] = authoritativeState
            }),
            "dm.narrate.outcome",
            cancellationToken);

    /// <summary>
    /// Narrates an accepted object interaction — a container opened, or an item taken from it. Like
    /// <see cref="NarrateOutcomeAsync"/> it runs only after the engine has applied and reported the
    /// change, so the DM describes a fact. When a container has just been opened, the reported contents
    /// are now public and may be named.
    /// </summary>
    public Task<string> NarrateObjectOutcomeAsync(
        string actorName,
        string engineResultSummary,
        string transition,
        string authoritativeState,
        CancellationToken cancellationToken) =>
        NarrateProjectedAsync(
            _prompts.Render("dungeon-master.object-outcome", new Dictionary<string, string?>
            {
                ["actor"] = actorName,
                ["result"] = engineResultSummary,
                ["transition"] = transition,
                ["state"] = authoritativeState
            }),
            "dm.narrate.object-outcome",
            cancellationToken);

    /// <summary>
    /// Narrates a character deliberately doing nothing. No engine action is involved; the world simply
    /// has to describe someone holding back so the other character can perceive it.
    /// </summary>
    public Task<string> NarratePassAsync(
        string characterName,
        string reason,
        string authoritativeState,
        CancellationToken cancellationToken) =>
        NarrateProjectedAsync(
            _prompts.Render("dungeon-master.pass", new Dictionary<string, string?>
            {
                ["character"] = characterName,
                ["reason"] = reason,
                ["state"] = authoritativeState
            }),
            "dm.narrate.pass",
            cancellationToken);

    /// <summary>Answers one character's question using what that character could perceive.</summary>
    public async Task<string> AnswerQuestionAsync(
        string authoritativeState,
        string characterName,
        string question,
        CancellationToken cancellationToken)
    {
        // Answers come from authoritative state, not from remembered prior questions, so each runs on
        // its own fresh context and carries no continuity forward.
        var conversation = NarrationContext();
        conversation.AppendUser(_prompts.Render("dungeon-master.answer", new Dictionary<string, string?>
        {
            ["state"] = authoritativeState,
            ["character"] = characterName,
            ["question"] = question
        }));

        var response = await CallModelAsync(conversation, "dm.answer", tools: null, cancellationToken).ConfigureAwait(false);
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
        _adjudication = _useProjections ? NewProjection() : null;

        var conversation = AdjudicationContext();
        conversation.AppendUser(_prompts.Render("dungeon-master.adjudicate", new Dictionary<string, string?>
        {
            ["state"] = authoritativeState,
            ["character"] = characterName,
            ["intent"] = intent
        }));

        return CallModelAsync(conversation, "dm.adjudicate", DungeonMasterTools.All, cancellationToken);
    }

    /// <summary>Re-asks for a tool call after the DM replied with prose instead.</summary>
    public Task<ChatResponse> RetryProposeActionAsync(string characterName, CancellationToken cancellationToken)
    {
        AdjudicationContext().AppendUser(_prompts.Render("dungeon-master.adjudicate-retry", new Dictionary<string, string?>
        {
            ["character"] = characterName
        }));

        return CallModelAsync(AdjudicationContext(), "dm.adjudicate.retry", DungeonMasterTools.All, cancellationToken);
    }

    /// <summary>
    /// Answers a tool call the DM made while adjudicating. Routed to the adjudication context so the
    /// tool call is answered where it was asked, keeping that projection consistent.
    /// </summary>
    public void AppendAdjudicationToolResult(FunctionCallContent call, object? result) =>
        AdjudicationContext().AppendToolResult(call.CallId, result);

    /// <summary>
    /// Rewrites a refusal that leaked the machinery (the rules/engine/what "can be resolved") into an
    /// in-world explanation. Runs on a fresh, toolless projection: it only rephrases a string, so it needs
    /// none of the adjudication context and cannot change the world while doing it.
    /// </summary>
    public async Task<string> RephraseRejectionInWorldAsync(
        string leakedReason,
        string characterName,
        CancellationToken cancellationToken)
    {
        var conversation = NarrationContext();
        conversation.AppendUser(_prompts.Render("dungeon-master.rephrase-rejection", new Dictionary<string, string?>
        {
            ["character"] = characterName,
            ["reason"] = leakedReason
        }));

        var response = await CallModelAsync(conversation, "dm.rephrase.rejection", tools: null, cancellationToken).ConfigureAwait(false);
        return ModelText.Clean(response);
    }

    /// <summary>
    /// Turns an engine rejection into an in-world explanation the character can act on. Runs on the
    /// same adjudication context, so it can see the intent and the engine's verdict it is explaining.
    /// </summary>
    public async Task<string> ExplainEngineRejectionAsync(
        string rejectionReason,
        string characterName,
        CancellationToken cancellationToken)
    {
        var conversation = AdjudicationContext();
        conversation.AppendUser(_prompts.Render("dungeon-master.engine-rejection", new Dictionary<string, string?>
        {
            ["reason"] = rejectionReason,
            ["character"] = characterName
        }));

        var response = await CallModelAsync(conversation, "dm.explain.rejection", tools: null, cancellationToken).ConfigureAwait(false);
        return ModelText.Clean(response);
    }

    /// <summary>
    /// Runs a narration task on a projection. After the opening, it prepends a short note telling the
    /// DM this is a mid-encounter update so it describes only the new development rather than
    /// re-establishing the scene. The note deliberately does not quote the previous narration: doing so
    /// made the model echo that narration verbatim instead of narrating the new event it was given.
    /// </summary>
    private async Task<string> NarrateProjectedAsync(string renderedTask, string purpose, CancellationToken cancellationToken)
    {
        var conversation = NarrationContext();

        var task = _useProjections && _hasNarrated
            ? "This is a mid-encounter update. The scene is already set, so do not re-describe the room " +
              "or restate the standoff. Narrate only the specific new development reported below, and " +
              $"lead with it.\n\n---\n\n{renderedTask}"
            : renderedTask;

        conversation.AppendUser(task);

        var response = await CallModelAsync(conversation, purpose, tools: null, cancellationToken).ConfigureAwait(false);
        var text = ModelText.Clean(response);

        if (!string.IsNullOrWhiteSpace(text))
        {
            _hasNarrated = true;
        }

        return text;
    }
}
