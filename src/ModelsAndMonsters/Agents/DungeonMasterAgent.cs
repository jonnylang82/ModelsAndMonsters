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

    /// <summary>
    /// The output budget for an adjudication when the context window is shared between input and output —
    /// far smaller than the agent's configured allowance. See <see cref="AdjudicationOutputBudget"/> for when
    /// it applies at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An adjudication's entire reply is one structured tool call — a handful of short arguments, well under
    /// a hundred tokens even for an offer of surrender with an item list. The DM's configured
    /// <c>MaxOutputTokens</c> is sized for narration prose, and on Ollama the context window covers input and
    /// output together, so leaving it at that figure holds back a fifth of the window from the one call that
    /// carries the largest input. Narration keeps the full allowance.
    /// </para>
    /// <para>
    /// This cap CAN be hit, and the earlier claim here that it could not was wrong. It is hit whenever the
    /// model deliberates in prose instead of calling a tool: a live run spent all 400 tokens on 1,642
    /// characters of visible reasoning ("But which purse? The intent doesn't specify") and produced no tool
    /// call at all, with 1,500 tokens of window still free. On a SHARED window raising the cap does not help
    /// — a model thinking out loud fills whatever it is given, and the extra headroom comes straight out of
    /// the request that already carries the largest input. So there the cap stays and the truncation is
    /// handled rather than avoided: <see cref="ProposeActionAsync"/> turns a toolless truncated reply into a
    /// tool error and re-asks, which recovers in a few dozen tokens. That live occurrence resolved on the
    /// retry.
    /// </para>
    /// <para>
    /// Where the window is NOT shared the same reasoning does not hold, which is why this figure is applied
    /// through <see cref="AdjudicationOutputBudget"/> rather than directly. gpt-5.4 has never come near it —
    /// its largest observed adjudication reply is 122 tokens — so for most hosted models this changes
    /// nothing either way.
    /// </para>
    /// </remarks>
    private const int AdjudicationOutputTokens = 400;

    /// <summary>
    /// The output budget this adjudication actually runs under: the tight cap where the model's context
    /// window covers input and output TOGETHER, and the agent's own configured allowance where it does not.
    /// Null means "use the profile's <c>MaxOutputTokens</c> unchanged".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cap only ever had one justification, and it is arithmetic: on Ollama, <c>num_ctx</c> covers the
    /// prompt and the reply together, so reserving 1,700 tokens for a reply that needs a hundred takes 1,700
    /// tokens away from the request — on the one call that carries the largest input. A hosted provider
    /// budgets output separately from its (far larger) input window, so there the reservation costs the
    /// request nothing at all and the cap is paying for a problem that does not exist. It is the same
    /// asymmetry as <see cref="AI.AgentModelProfile.BindingContextWindow"/>, and it reads the same signal.
    /// </para>
    /// <para>
    /// The cap did acquire a second, incidental use: it caught models that deliberate in prose instead of
    /// calling a tool, which the retry path then recovered. That is worth keeping where the arithmetic
    /// demands the cap anyway, but it is a poor reason to impose it elsewhere — a model whose reasoning is
    /// cut off mid-sentence is more likely to rule badly, not less. Two live Haiku runs truncated three
    /// adjudications between them, each one part-way through checking preconditions it had every right to
    /// check.
    /// </para>
    /// <para>
    /// Lifting it on hosted providers is not free: deliberation costs output tokens and latency, and
    /// adjudication is already the second-largest latency line in a run. What it buys is a ruling made on
    /// finished reasoning rather than interrupted reasoning. Deliberation stays visible either way — a high
    /// output count on <c>dm.adjudicate</c> says it plainly.
    /// </para>
    /// </remarks>
    private int? AdjudicationOutputBudget =>
        Profile.BindingContextWindow is null ? null : AdjudicationOutputTokens;

    /// <summary>
    /// Whether a reply from <see cref="ProposeActionAsync"/> or <see cref="RetryProposeActionAsync"/> was cut
    /// short, checked against the budget actually applied to that call — <see cref="AdjudicationOutputBudget"/>,
    /// not the agent's general-purpose <c>MaxOutputTokens</c>. The two can differ substantially (the profile's
    /// configured allowance is sized for narration prose), and diagnosing an adjudication truncation against
    /// the wrong figure can both miss a real truncation and, on the fallback path used when a provider reports
    /// no finish reason, misjudge one that did not happen.
    /// </summary>
    public bool WasAdjudicationReplyCutShort(ChatResponse response) =>
        WasReplyCutShort(response, AdjudicationOutputBudget);

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

    // The narrowed tool surface for the adjudication currently in flight (v0.6), so a retry re-exposes exactly
    // the same candidate tools the rulebook guidance selected. Null runs the full engine tool set (legacy path).
    private IReadOnlyList<AITool>? _adjudicationTools;

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
    /// Answers one character's question by rephrasing a bounded, deterministic fact projection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Dungeon Master is deliberately <em>not</em> given the authoritative state for this task. It gets
    /// only <paramref name="projectedFacts"/> — the complete set of facts that character may be answered from,
    /// worked out by the harness from current state and that character's own knowledge ledger. Its job is
    /// reduced from deciding what is true to saying it naturally.
    /// </para>
    /// <para>
    /// This is a boundary, not an instruction. Measured weaker-model runs answered from hidden state, said
    /// consumed items were still carried, and promised tactical manoeuvres the engine has no representation
    /// for. A prompt cannot reliably stop that; withholding the material can.
    /// </para>
    /// </remarks>
    public async Task<string> AnswerFromFactsAsync(
        string characterName,
        string projectedFacts,
        string question,
        CancellationToken cancellationToken)
    {
        // Each answer runs on its own fresh context and carries no continuity forward, so a previous answer
        // can never become the source of the next.
        var conversation = AnswerContext();
        conversation.AppendUser(_prompts.Render("dungeon-master.answer", new Dictionary<string, string?>
        {
            ["character"] = characterName,
            ["facts"] = projectedFacts,
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
    /// Asks the DM to translate a character's natural-language intent into exactly one tool call, binding the
    /// supplied rulebook guidance to the current authoritative state. The response is returned raw:
    /// interpreting and dispatching it is the orchestrator's job.
    /// </summary>
    /// <param name="ruleGuidance">
    /// Request-scoped guidance from the rulebook resolver, rendered for the DM. Never accumulates in history —
    /// each adjudication runs on a fresh projection. Null renders a neutral "no guidance" block (legacy path).
    /// </param>
    /// <param name="tools">
    /// The narrowed candidate tool set the guidance selected (plus rejection). Null exposes the full engine
    /// tool surface, preserving the v0.5 behaviour for callers that do not consult the rulebook.
    /// </param>
    public Task<ChatResponse> ProposeActionAsync(
        string authoritativeState,
        string characterName,
        string characterKnowledge,
        string intent,
        CancellationToken cancellationToken,
        string? ruleGuidance = null,
        IReadOnlyList<AITool>? tools = null)
    {
        // Each attempt starts fresh, so one adjudication cannot colour the next — and the rule guidance is part
        // of that fresh projection, so it is request-scoped and never accumulates in the DM's long-term history.
        _adjudication = _useProjections ? NewProjection(_adjudicateSystem) : null;
        _adjudicationTools = tools ?? DungeonMasterTools.All;

        var conversation = AdjudicationContext();
        conversation.AppendUser(_prompts.Render("dungeon-master.adjudicate", new Dictionary<string, string?>
        {
            ["state"] = authoritativeState,
            ["character"] = characterName,
            ["knowledge"] = characterKnowledge,
            ["intent"] = intent,
            ["guidance"] = string.IsNullOrWhiteSpace(ruleGuidance)
                ? "No rulebook guidance was supplied for this attempt; rule on it directly from your constitution."
                : ruleGuidance
        }));

        return CallModelAsync(conversation, "dm.adjudicate", _adjudicationTools, cancellationToken, AdjudicationOutputBudget);
    }

    /// <summary>Re-asks for a tool call after the DM replied with prose instead, re-exposing the same tool surface.</summary>
    public Task<ChatResponse> RetryProposeActionAsync(string characterName, CancellationToken cancellationToken)
    {
        AdjudicationContext().AppendUser(_prompts.Render("dungeon-master.adjudicate-retry", new Dictionary<string, string?>
        {
            ["character"] = characterName
        }));

        return CallModelAsync(AdjudicationContext(), "dm.adjudicate.retry",
            _adjudicationTools ?? DungeonMasterTools.All, cancellationToken, AdjudicationOutputBudget);
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
