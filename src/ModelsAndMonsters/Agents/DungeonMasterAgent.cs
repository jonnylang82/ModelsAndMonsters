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

    // The Dungeon Master's rules are modular: one shared CORE (identity, authoritative state, the shape of
    // the world) plus a rules block per job. When projecting, each task runs on a fresh projection seeded
    // with only CORE + that job's rules, so the hot adjudication path never carries the narration/answering
    // rules it does not use (and vice versa) — which keeps each call well inside the context window. When
    // not projecting (the A/B comparison mode) everything shares one conversation carrying the full combined
    // prompt, so that mode is unchanged.
    private readonly string _narrateSystem;
    private readonly string _answerSystem;
    private readonly string _adjudicateSystem;

    // The adjudication projection persists between the proposal (or mapping) call and its tool-result
    // answer, so a field rather than a local. Reset at the start of each new adjudication.
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
        : base(AgentIdentifier, profile, client, ComposeFull(prompts))
    {
        _prompts = prompts;
        _useProjections = projectContext;

        var core = prompts.Render("dungeon-master.core");
        _narrateSystem = Join(core, prompts.Render("dungeon-master.rules-narrate"));
        _answerSystem = Join(core, prompts.Render("dungeon-master.rules-answer"));
        _adjudicateSystem = Join(core, prompts.Render("dungeon-master.rules-adjudicate"));
    }

    /// <summary>True when the DM runs each task on a projection rather than one growing conversation.</summary>
    public bool UsesProjections => _useProjections;

    /// <summary>The full combined system prompt: CORE plus all three job rule blocks, used when not projecting.</summary>
    private static string ComposeFull(PromptLibrary prompts) => Join(
        prompts.Render("dungeon-master.core"),
        prompts.Render("dungeon-master.rules-narrate"),
        prompts.Render("dungeon-master.rules-answer"),
        prompts.Render("dungeon-master.rules-adjudicate"));

    /// <summary>Joins prompt fragments with a single blank line between them.</summary>
    private static string Join(params string[] parts) => string.Join("\n\n", Array.ConvertAll(parts, p => p.TrimEnd()));

    /// <summary>A fresh conversation seeded with the given system prompt.</summary>
    private AgentConversation NewProjection(string systemPrompt) => new(AgentName, systemPrompt);

    /// <summary>Where narration runs: a fresh CORE+narration projection, or the shared conversation.</summary>
    private AgentConversation NarrateContext() => _useProjections ? NewProjection(_narrateSystem) : Conversation;

    /// <summary>Where answering runs: a fresh CORE+answering projection, or the shared conversation.</summary>
    private AgentConversation AnswerContext() => _useProjections ? NewProjection(_answerSystem) : Conversation;

    /// <summary>Where adjudication runs: the current isolated CORE+adjudication projection, or the shared conversation.</summary>
    private AgentConversation AdjudicationContext() =>
        _useProjections ? _adjudication ??= NewProjection(_adjudicateSystem) : Conversation;

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

    /// <summary>
    /// Answers one character's question within that character's information boundary. The DM holds
    /// omniscient state, but <paramref name="characterKnowledge"/> is what that character actually knows —
    /// first-hand and by hearsay — and the answer must respect it: direct knowledge may be confirmed,
    /// hearsay must be described as something another character said, and what the character has not
    /// observed must not be revealed just because the DM can see it.
    /// </summary>
    public async Task<string> AnswerQuestionAsync(
        string authoritativeState,
        string characterName,
        string characterKnowledge,
        string question,
        CancellationToken cancellationToken)
    {
        // Answers come from authoritative state, not from remembered prior questions, so each runs on
        // its own fresh context and carries no continuity forward.
        var conversation = AnswerContext();
        conversation.AppendUser(_prompts.Render("dungeon-master.answer", new Dictionary<string, string?>
        {
            ["state"] = authoritativeState,
            ["character"] = characterName,
            ["knowledge"] = characterKnowledge,
            ["question"] = question
        }));

        var response = await CallModelAsync(conversation, "dm.answer", tools: null, cancellationToken).ConfigureAwait(false);
        return ModelText.Clean(response);
    }

    /// <summary>
    /// Narrates a public event the whole room sees — an exit opened, a surrender, an escape — from the
    /// engine's reported summary. Like the other outcome narrations it runs only after the engine has applied
    /// and reported the change, so the DM describes a fact rather than deciding one.
    /// </summary>
    public Task<string> NarratePublicEventAsync(
        string actorName,
        string engineResultSummary,
        string authoritativeState,
        CancellationToken cancellationToken) =>
        NarrateProjectedAsync(
            _prompts.Render("dungeon-master.public-event", new Dictionary<string, string?>
            {
                ["actor"] = actorName,
                ["result"] = engineResultSummary,
                ["state"] = authoritativeState
            }),
            "dm.narrate.public-event",
            cancellationToken);

    /// <summary>
    /// Narrates a close inspection to the room. Public: it says only that the character examined the object.
    /// The findings are delivered privately by the orchestration layer and are never given to this call, so
    /// this narration cannot leak them.
    /// </summary>
    public Task<string> NarrateInspectionAsync(
        string actorName,
        string objectName,
        string authoritativeState,
        CancellationToken cancellationToken) =>
        NarrateProjectedAsync(
            _prompts.Render("dungeon-master.inspect-outcome", new Dictionary<string, string?>
            {
                ["actor"] = actorName,
                ["object"] = objectName,
                ["state"] = authoritativeState
            }),
            "dm.narrate.inspect-outcome",
            cancellationToken);

    /// <summary>
    /// Asks the DM to translate a character's natural-language intent into exactly one tool call.
    /// The response is returned raw: interpreting and dispatching it is the orchestrator's job.
    /// </summary>
    public Task<ChatResponse> ProposeActionAsync(
        string authoritativeState,
        string characterName,
        string characterKnowledge,
        string intent,
        CancellationToken cancellationToken)
    {
        // Each attempt starts fresh, so one adjudication cannot colour the next.
        _adjudication = _useProjections ? NewProjection(_adjudicateSystem) : null;

        var conversation = AdjudicationContext();
        conversation.AppendUser(_prompts.Render("dungeon-master.adjudicate", new Dictionary<string, string?>
        {
            ["state"] = authoritativeState,
            ["character"] = characterName,
            ["knowledge"] = characterKnowledge,
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
        var conversation = NarrateContext();
        conversation.AppendUser(_prompts.Render("dungeon-master.rephrase-rejection", new Dictionary<string, string?>
        {
            ["character"] = characterName,
            ["reason"] = leakedReason
        }));

        var response = await CallModelAsync(conversation, "dm.rephrase.rejection", tools: null, cancellationToken).ConfigureAwait(false);
        return ModelText.Clean(response);
    }

    /// <summary>
    /// Rewrites a question answer that broke character — narrating the character's knowledge state ("you
    /// directly know", "you have not been told"), naming the machinery, or using markdown — back into a
    /// plain spoken reply. Like the rejection rephrase it runs on a fresh, toolless projection: it only
    /// restates a string in-world, revealing exactly what the original revealed and nothing more.
    /// </summary>
    public async Task<string> RephraseAnswerInWorldAsync(
        string leakedAnswer,
        string characterName,
        CancellationToken cancellationToken)
    {
        var conversation = AnswerContext();
        conversation.AppendUser(_prompts.Render("dungeon-master.rephrase-answer", new Dictionary<string, string?>
        {
            ["character"] = characterName,
            ["answer"] = leakedAnswer
        }));

        var response = await CallModelAsync(conversation, "dm.rephrase.answer", tools: null, cancellationToken).ConfigureAwait(false);
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
        var conversation = NarrateContext();

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
