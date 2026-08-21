using System.Text.Json;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;

namespace ModelsAndMonsters.Tracing;

// ---------------------------------------------------------------------------------------------
// Shared shapes
// ---------------------------------------------------------------------------------------------

/// <summary>A chat message as it crossed our IChatClient boundary.</summary>
public sealed record TracedMessage
{
    public required string Role { get; init; }

    public string? AuthorName { get; init; }

    /// <summary>Concatenated text of the message, for readability. Full detail is in <see cref="Contents"/>.</summary>
    public string? Text { get; init; }

    public required IReadOnlyList<TracedContent> Contents { get; init; }
}

/// <summary>One content part of a message: text, reasoning, a tool call, or a tool result.</summary>
public sealed record TracedContent
{
    public required string Type { get; init; }

    public string? Text { get; init; }

    public string? CallId { get; init; }

    public string? Name { get; init; }

    public IReadOnlyDictionary<string, object?>? Arguments { get; init; }

    public object? Result { get; init; }

    public string? Detail { get; init; }
}

/// <summary>A tool definition exposed to a model call, including its JSON schema.</summary>
public sealed record TracedToolDefinition
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public JsonElement? JsonSchema { get; init; }
}

/// <summary>The sampling configuration we asked for, and what the provider could not honour.</summary>
public sealed record TracedChatOptions
{
    public string? ModelId { get; init; }

    public float? Temperature { get; init; }

    public float? TopP { get; init; }

    public int? TopK { get; init; }

    public int? MaxOutputTokens { get; init; }

    public long? Seed { get; init; }

    /// <summary>
    /// Penalties as SENT. Null means none was sent and the model's own default applied — which is not the
    /// same as zero, and is exactly the case that hid a 1.5 presence penalty on every local call.
    /// </summary>
    public float? PresencePenalty { get; init; }

    public float? FrequencyPenalty { get; init; }

    /// <summary>Input context window requested for this call, when the provider accepts one.</summary>
    public int? ContextWindow { get; init; }

    /// <summary>Unified reasoning effort requested for this call (none/low/medium/high/max), when set.</summary>
    public string? Effort { get; init; }

    /// <summary>Whether reasoning was requested on/off via the legacy toggle, when the provider accepts it.</summary>
    public bool? Thinking { get; init; }

    public string? ToolMode { get; init; }

    /// <summary>Options requested by the profile that this provider does not support, and were dropped.</summary>
    public IReadOnlyList<string> UnsupportedOptionsDropped { get; init; } = [];
}

public sealed record TracedUsage
{
    public long? InputTokenCount { get; init; }

    public long? OutputTokenCount { get; init; }

    public long? TotalTokenCount { get; init; }

    public IReadOnlyDictionary<string, long>? AdditionalCounts { get; init; }
}

public sealed record TracedFunctionCall
{
    public required string CallId { get; init; }

    public required string Name { get; init; }

    public IReadOnlyDictionary<string, object?>? Arguments { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Model interaction
// ---------------------------------------------------------------------------------------------

public sealed record ModelRequestPayload
{
    public required string AgentName { get; init; }

    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    /// <summary>Why the call was made, e.g. "character.turn", "dm.adjudicate", "dm.narrate.outcome".</summary>
    public required string Purpose { get; init; }

    public required string CallId { get; init; }

    /// <summary>The system prompt, extracted for readability. It is also present in <see cref="Messages"/>.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>The complete message collection sent to the model, in order.</summary>
    public required IReadOnlyList<TracedMessage> Messages { get; init; }

    /// <summary>The newest message(s) injected for this call, i.e. what is new since the previous call.</summary>
    public IReadOnlyList<TracedMessage> NewlyInjected { get; init; } = [];

    public required TracedChatOptions RequestedOptions { get; init; }

    /// <summary>
    /// Which transient-retry attempt this call is, counting from 1. Anything above 1 is a re-send after a
    /// provider failure, drawing at a raised temperature and an offset seed — see
    /// <see cref="Agents.ModelAgent"/>. Recorded so a retry is legible as a retry rather than as an
    /// unexplained change in the sampling options.
    /// </summary>
    public int Attempt { get; init; } = 1;

    public required IReadOnlyList<TracedToolDefinition> Tools { get; init; }
}

public sealed record ModelResponsePayload
{
    public required string AgentName { get; init; }

    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    public required string Purpose { get; init; }

    public required string CallId { get; init; }

    public string? ResponseId { get; init; }

    /// <summary>The model id reported by the provider, which may differ from the requested one.</summary>
    public string? ReportedModelId { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public string? FinishReason { get; init; }

    public required IReadOnlyList<TracedMessage> Messages { get; init; }

    public string? Text { get; init; }

    public required IReadOnlyList<TracedFunctionCall> ToolCalls { get; init; }

    public TracedUsage? Usage { get; init; }

    /// <summary>Provider metadata, flattened to strings. Never includes credentials or raw HTTP data.</summary>
    public IReadOnlyDictionary<string, string?>? ProviderMetadata { get; init; }

    public required double ElapsedMilliseconds { get; init; }
}

/// <summary>
/// A response cut short by the output-token limit. Recorded separately from the response itself
/// because a truncated reply is not a considered answer, and silently treating it as one produces
/// misleading downstream behaviour.
/// </summary>
public sealed record ModelTruncatedPayload
{
    public required string AgentName { get; init; }

    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    public required string Purpose { get; init; }

    public required string CallId { get; init; }

    public long? OutputTokenCount { get; init; }

    public int? MaxOutputTokensRequested { get; init; }

    /// <summary>False when the truncation cost us the tool call entirely.</summary>
    public required bool HadToolCalls { get; init; }

    /// <summary>True when the whole output was a reasoning block, leaving no tool call and no prose.</summary>
    public bool ReasoningOnly { get; init; }

    public required string Effect { get; init; }
}

/// <summary>
/// A request whose reported input size fell well below what we sent — evidence the provider silently
/// discarded part of the conversation.
/// </summary>
/// <remarks>
/// Detected by comparing an estimate of what we sent against the input size the response reports,
/// rather than against a configured window. Ollama gives no truncation signal and may truncate below
/// the nominal window, so the sent-versus-received gap is the only dependable evidence. Once this
/// fires, the application no longer owns the whole conversation the model actually saw.
/// </remarks>
public sealed record ContextSaturationPayload
{
    public required string AgentName { get; init; }

    public required string Purpose { get; init; }

    public required string CallId { get; init; }

    /// <summary>Rough estimate of what we sent.</summary>
    public required int EstimatedSentTokens { get; init; }

    /// <summary>Input size the provider reported processing.</summary>
    public required long ReportedInputTokens { get; init; }

    /// <summary>Estimated tokens dropped before the model saw them.</summary>
    public required int EstimatedDroppedTokens { get; init; }

    public required int MessagesSent { get; init; }

    /// <summary>The configured window, when one was set. Recorded for reference, not used to detect.</summary>
    public int? ConfiguredContextWindow { get; init; }

    public required string Effect { get; init; }
}

public sealed record ModelErrorPayload
{
    public required string AgentName { get; init; }

    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    public required string Purpose { get; init; }

    public required string CallId { get; init; }

    /// <summary>Which transient-retry attempt failed, counting from 1. See <see cref="ModelRequestPayload.Attempt"/>.</summary>
    public int Attempt { get; init; } = 1;

    public required string ExceptionType { get; init; }

    public required string Message { get; init; }

    public string? StackTrace { get; init; }

    public required double ElapsedMilliseconds { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Manual tool orchestration
// ---------------------------------------------------------------------------------------------

public sealed record ToolCallDispatchPayload
{
    public required string AgentName { get; init; }

    public required string CallId { get; init; }

    public required string ToolName { get; init; }

    public IReadOnlyDictionary<string, object?>? Arguments { get; init; }

    /// <summary>What the application decided to do with this call, in application terms.</summary>
    public required string DispatchDecision { get; init; }
}

public sealed record ToolCallResultPayload
{
    public required string AgentName { get; init; }

    public required string CallId { get; init; }

    public required string ToolName { get; init; }

    /// <summary>Exactly what was handed back to the model as the tool result.</summary>
    public required object? Result { get; init; }
}

/// <summary>
/// Records a tool call the model wrote as prose and the harness recovered into a real call. Keeps the
/// original text so the model's actual (non-calling) behaviour stays visible even though the harness
/// went ahead and dispatched the intended call.
/// </summary>
public sealed record ToolCallRecoveredPayload
{
    public required string AgentName { get; init; }

    public required string CallId { get; init; }

    public required string ToolName { get; init; }

    public required string RecoveredArgument { get; init; }

    /// <summary>The full prose the model actually returned, verbatim.</summary>
    public required string OriginalText { get; init; }
}

/// <summary>
/// A character wrote a spoken line in prose (a quoted utterance) rather than calling <c>say</c>. The
/// attempted words are captured so a report never concludes the character stayed silent, but they are not
/// delivered — the character is nudged to speak properly.
/// </summary>
/// <summary>
/// Words a character tried to say after it had already spoken this turn. They are recorded and never
/// delivered, so a report can show that the character tried rather than implying it chose silence.
/// </summary>
public sealed record SpeechNotHeardPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    /// <summary>The words that were not delivered. Never reaches any other character.</summary>
    public required string Unheard { get; init; }

    /// <summary>How many times this character had already spoken this turn.</summary>
    public required int SpeechActsAlready { get; init; }

    /// <summary>The per-turn allowance in force, so the record explains itself without the config to hand.</summary>
    public required int Allowance { get; init; }
}

public sealed record UnstructuredSpeechAttemptPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    /// <summary>The quoted utterance detected in the prose. Recorded, never delivered to anyone.</summary>
    public required string AttemptedText { get; init; }

    /// <summary>Whether the reply that carried it was cut off at the output-token limit.</summary>
    public required bool WasTruncated { get; init; }
}

/// <summary>
/// The intent parser's reading of a prose reply: the character's raw words and the tool calls extracted from
/// them, so the parse can be audited against what the character actually wrote.
/// </summary>
public sealed record IntentParsedPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    /// <summary>The character's raw prose, exactly as the parser received it.</summary>
    public required string Prose { get; init; }

    /// <summary>The tool calls the parser produced, in dispatch order (say/ask before the turn-ending action).</summary>
    public required IReadOnlyList<string> ExtractedCalls { get; init; }
}

/// <summary>
/// A character's older history was compressed into a running summary. Carries the estimated token size before
/// and after and the summary text, so the compression and its effect on context size can be audited.
/// </summary>
/// <summary>
/// History shed from a character's conversation after its request filled the context window, recorded so a
/// run can be audited for whether reclaiming actually recovered room or the request is simply too large.
/// </summary>
public sealed record ContextRoomReclaimedPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required int EstimatedTokensBefore { get; init; }

    public required int EstimatedTokensAfter { get; init; }

    public int Round { get; init; }

    public int Turn { get; init; }
}

public sealed record HistorySummarisedPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required int EstimatedTokensBefore { get; init; }

    public required int EstimatedTokensAfter { get; init; }

    /// <summary>
    /// The effective budget that triggered this trim — derived from the agent's context window, output budget
    /// and measured prompt overhead, so the full request stays inside the window. Defaults to 0 for callers
    /// that do not compute one.
    /// </summary>
    public int EffectiveBudget { get; init; }

    /// <summary>The measured non-message prompt overhead (tool schemas + chat template) folded into the budget.</summary>
    public int MeasuredPromptOverhead { get; init; }

    /// <summary>The recap that replaced the older turns.</summary>
    public required string Summary { get; init; }
}

public sealed record ToolCallErrorPayload
{
    public required string AgentName { get; init; }

    public string? CallId { get; init; }

    public required string ToolName { get; init; }

    public IReadOnlyDictionary<string, object?>? Arguments { get; init; }

    public required string Error { get; init; }

    public string? ExceptionType { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Game-level events
// ---------------------------------------------------------------------------------------------

public sealed record CharacterQuestionPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required string Question { get; init; }

    public required int QuestionNumberThisTurn { get; init; }
}

public sealed record DungeonMasterAnswerPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required string Question { get; init; }

    public required string Answer { get; init; }

    /// <summary>
    /// The exact information view the Dungeon Master was given for this character — what it directly knew
    /// and what it had only heard — preserved so the record can show why an answer was, or was not, allowed
    /// to reveal something. Private to the asking character; the answer must respect this boundary.
    /// </summary>
    public string? AskingCharacterKnowledge { get; init; }

    /// <summary>
    /// The complete deterministic <c>AnswerFacts</c> projection the Dungeon Master was given, and nothing
    /// else. It replaces the authoritative state block for this task: the DM rephrases these facts rather
    /// than deciding what is true, so an answer that names something outside them is visibly ungrounded.
    /// </summary>
    public string? ProjectedFacts { get; init; }

    /// <summary>What the projection withheld from the model. Recorded here so the boundary is provable.</summary>
    public string? OmittedHiddenFacts { get; init; }

    /// <summary>The DM's raw reply before any in-world correction, when a correction was applied.</summary>
    public string? RawAnswer { get; init; }

    public string Visibility => "private";

    public int WorldVersion { get; init; }
}

public sealed record CharacterPassedPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required string Reason { get; init; }
}

/// <summary>
/// One public utterance. The verbatim message plus the exact set of living recipients make it possible
/// to verify from the trace alone that speech reached everyone alive in the room and nobody else.
/// </summary>
public sealed record CharacterSpeechPayload
{
    /// <summary>
    /// The one character the speaker DECLARED they were speaking to, resolved to an id, or null when they
    /// were calling to the room. Structural: it comes from the speaker's own field, never from reading the
    /// words for a name. It is what lets a threat or a word of encouragement be bound to one person without
    /// any language parsing.
    /// </summary>
    public string? AddressedToId { get; init; }

    public string? AddressedToName { get; init; }

    public required string SpeakerId { get; init; }

    public required string SpeakerName { get; init; }

    public required string SpeakerTeam { get; init; }

    /// <summary>The speaker's exact words, preserved verbatim and never paraphrased by a model.</summary>
    public required string Message { get; init; }

    public required int Round { get; init; }

    public required int Turn { get; init; }

    /// <summary>Which utterance this is within the speaker's current turn, 1-based.</summary>
    public required int SpeechIndexWithinTurn { get; init; }

    /// <summary>The living characters other than the speaker who are intended to hear this.</summary>
    public required IReadOnlyList<string> Recipients { get; init; }

    public required string DeliveryMechanism { get; init; }

    /// <summary>The public-channel entry id, so the delivery to each recipient can be cross-referenced.</summary>
    public required int NarrationId { get; init; }
}

/// <summary>
/// A container or item interaction, accepted or rejected. Carries the object-specific ids and the world
/// version transition that the generic <see cref="EngineActionPayload"/> does not surface directly, plus
/// compact before/after snapshots that make a successful transfer's two halves — the container losing the
/// item and the actor gaining it — explicit in one row.
/// </summary>
public sealed record ObjectInteractionPayload
{
    public required string ActorId { get; init; }

    /// <summary>"open_container" or "take_item".</summary>
    public required string ActionType { get; init; }

    /// <summary>The object acted on (the container), by id, when it resolved.</summary>
    public string? ObjectId { get; init; }

    public string? ContainerId { get; init; }

    public string? ItemId { get; init; }

    /// <summary>"accepted" when the engine applied it, "rejected" otherwise.</summary>
    public required string ValidationResult { get; init; }

    public string? RejectionReason { get; init; }

    public required int WorldVersionBefore { get; init; }

    public required int WorldVersionAfter { get; init; }

    public bool? ContainerOpenBefore { get; init; }

    public bool? ContainerOpenAfter { get; init; }

    public IReadOnlyList<string>? ContainerContentsBefore { get; init; }

    public IReadOnlyList<string>? ContainerContentsAfter { get; init; }

    public IReadOnlyList<string>? ActorInventoryBefore { get; init; }

    public IReadOnlyList<string>? ActorInventoryAfter { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Knowledge and information channels (v0.4)
// ---------------------------------------------------------------------------------------------

/// <summary>
/// A close inspection of an object: who examined what, whether the container was open, and which facts (if
/// any) it yielded. <see cref="LearnedSomethingNew"/> is false when the inspector already knew everything a
/// closer look could reveal.
/// </summary>
public sealed record ObjectInspectedPayload
{
    public required string ActorId { get; init; }

    public required string ActorName { get; init; }

    public required string ObjectId { get; init; }

    public required string ObjectName { get; init; }

    public required bool WasOpen { get; init; }

    /// <summary>Ids of the facts the inspection surfaced (whether or not they were new to this inspector).</summary>
    public required IReadOnlyList<string> DiscoveredFactIds { get; init; }

    public required bool LearnedSomethingNew { get; init; }

    public required int WorldVersion { get; init; }
}

/// <summary>
/// A discoverable fact minted in the ledger for the first time. Emitted once per fact, so the trace holds
/// exactly one authoritative definition of each fact's id, subject, type and description.
/// </summary>
public sealed record KnowledgeFactCreatedPayload
{
    public required string FactId { get; init; }

    public required string SubjectId { get; init; }

    public required string FactType { get; init; }

    public required string Description { get; init; }

    public required int WorldVersion { get; init; }

    /// <summary>The kind of discovery that first minted the fact (backstory, inspection, opening, public event).</summary>
    public required string CreatedBySource { get; init; }

    /// <summary>The action or event that produced it, e.g. "open_container", "take_item", "seed".</summary>
    public string? RelatedAction { get; init; }
}

/// <summary>
/// A character learned a fact. Denormalised (it repeats the fact's detail) so the discovery timeline and
/// the per-character knowledge tables can be reconstructed from this one event without joining back to the
/// creation event. Hearsay never produces one of these.
/// </summary>
public sealed record KnowledgeFactLearnedPayload
{
    public required string FactId { get; init; }

    public required string SubjectId { get; init; }

    public required string FactType { get; init; }

    public required string Description { get; init; }

    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required string Source { get; init; }

    public required int Round { get; init; }

    public required int Turn { get; init; }

    public required int ObservedWorldVersion { get; init; }

    /// <summary>"private" for a first-hand discovery, "public" for something learned in the open.</summary>
    public required string Visibility { get; init; }

    /// <summary>Everyone the underlying delivery reached; a single-name list for a private discovery.</summary>
    public required IReadOnlyList<string> Recipients { get; init; }

    public string? RelatedAction { get; init; }
}

/// <summary>
/// A private observation delivered to exactly one character — an inspection result or the contents seen on
/// opening. It records the recipient, the facts it conveyed and the world version, and it is by construction
/// never delivered to anyone else.
/// </summary>
public sealed record PrivateObservationDeliveredPayload
{
    public required string RecipientId { get; init; }

    public required string RecipientName { get; init; }

    /// <summary>The exact private text handed to the recipient.</summary>
    public required string Observation { get; init; }

    public required IReadOnlyList<string> RelatedFactIds { get; init; }

    public required int WorldVersion { get; init; }

    public string Visibility => "private";

    /// <summary>The action that produced the observation, e.g. "inspect_object", "open_container".</summary>
    public required string SourceEvent { get; init; }
}

/// <summary>
/// A publicly observable fact delivered to everyone alive in the room — a visible item being carried off.
/// The full recipient set is recorded so it is provable from the trace that a public event reached exactly
/// the living and nobody else.
/// </summary>
public sealed record PublicFactDeliveredPayload
{
    /// <summary>The public statement delivered, e.g. "Skrit removed the Small Healing Potion and now carries it."</summary>
    public required string Fact { get; init; }

    public required IReadOnlyList<string> Recipients { get; init; }

    public required IReadOnlyList<string> RelatedFactIds { get; init; }

    public required int WorldVersion { get; init; }

    public string Visibility => "public";

    /// <summary>The action that produced the public fact, e.g. "take_item".</summary>
    public required string SourceEvent { get; init; }
}

/// <summary>
/// Records where the harness overrode a Dungeon Master tool argument on structural grounds, so the
/// correction is never invisible in the experiment.
/// </summary>
public sealed record AdjudicationCorrectionPayload
{
    public required string ToolName { get; init; }

    public required string Parameter { get; init; }

    public string? DungeonMasterValue { get; init; }

    public required string CorrectedValue { get; init; }

    public required string Justification { get; init; }
}

public sealed record DmAdjudicationPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    /// <summary>The character's original natural-language intent, verbatim.</summary>
    public required string Intent { get; init; }

    public required string Category { get; init; }

    public string? Reason { get; init; }

    /// <summary>The structured action the DM translated the intent into, when it produced one.</summary>
    public object? TranslatedAction { get; init; }

    public string? DungeonMasterText { get; init; }

    /// <summary>
    /// The acting character's information view supplied to the Dungeon Master for this ruling — its
    /// first-hand knowledge and its hearsay — preserved so it is reconstructable why an action was allowed
    /// on a basis of direct knowledge or hearsay, or refused as something the character could not know.
    /// </summary>
    public string? ActingCharacterKnowledge { get; init; }

    /// <summary>
    /// The rulebook consultation whose guidance the Dungeon Master bound for this ruling (v0.6). Links the
    /// adjudication to the retrieval/resolver record; null when no consultation ran (e.g. legacy paths).
    /// </summary>
    public string? ConsultationId { get; init; }
}

public sealed record EngineActionPayload
{
    public required string ActionType { get; init; }

    public required object Action { get; init; }

    public required bool Accepted { get; init; }

    public string? RejectionReason { get; init; }

    public string? RejectionMessage { get; init; }

    public object? Outcome { get; init; }

    public string? OutcomeSummary { get; init; }

    public required GameState StateBefore { get; init; }

    public required GameState StateAfter { get; init; }
}

public sealed record NarrationPayload
{
    public required string Purpose { get; init; }

    /// <summary>The authoritative state text supplied to the DM for this narration.</summary>
    public required string StateSuppliedToDungeonMaster { get; init; }

    /// <summary>Any extra instruction/context supplied alongside the state.</summary>
    public string? ContextSuppliedToDungeonMaster { get; init; }

    public required string Narration { get; init; }

    public required int NarrationId { get; init; }

    /// <summary>
    /// The living characters this public narration is intended to reach. Each one receives it — the
    /// actor immediately, everyone else as their turn begins — and those deliveries are recorded
    /// separately as <see cref="NarrationDeliveredPayload"/> events.
    /// </summary>
    public IReadOnlyList<string> IntendedRecipients { get; init; } = [];

    /// <summary>"public" for room-wide narration, "private" for an observation meant for one character.</summary>
    public string Visibility { get; init; } = "public";

    /// <summary>The world version the narration describes, so a narration can be tied to a moment.</summary>
    public int WorldVersion { get; init; }

    /// <summary>Ids of any knowledge facts this narration relates to. Empty for ordinary combat narration.</summary>
    public IReadOnlyList<string> RelatedFactIds { get; init; } = [];
}

/// <summary>
/// How the Dungeon Master's natural-language target reference resolved to a specific character. Recorded
/// so targeting is auditable: an accepted attack shows exactly who was hit by stable id, and a bad
/// reference shows that the world refused it rather than silently retargeting.
/// </summary>
public sealed record TargetResolutionPayload
{
    public required string AttackerId { get; init; }

    public required string AttackerName { get; init; }

    /// <summary>The target reference the Dungeon Master supplied, verbatim.</summary>
    public required string RequestedTarget { get; init; }

    public string? ResolvedTargetId { get; init; }

    public string? ResolvedTargetName { get; init; }

    public required bool Resolved { get; init; }

    public bool? TargetAlive { get; init; }

    public string? TargetTeam { get; init; }

    /// <summary>Whether the resolved target is on the attacker's own team, when a target was resolved.</summary>
    public bool? TargetIsAlly { get; init; }

    public required string Note { get; init; }
}

public sealed record TurnSkippedPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required string Team { get; init; }

    public required string Reason { get; init; }

    /// <summary>The character's disposition at the moment the turn was skipped (Surrendered, Escaped or Dead).</summary>
    public string? Disposition { get; init; }
}

/// <summary>
/// A character's disposition changing — the authoritative record of someone leaving active combat, whether
/// by surrender, escape or death. It carries the full before/after so the transition can be reconstructed on
/// its own, and the public recipients who directly learned of it.
/// </summary>
public sealed record DispositionChangedPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required string Team { get; init; }

    public required string PreviousDisposition { get; init; }

    public required string NewDisposition { get; init; }

    /// <summary>What caused the change, e.g. "surrender", "escape_encounter", "killed in combat".</summary>
    public required string Cause { get; init; }

    public required int Round { get; init; }

    public required int Turn { get; init; }

    public required int WorldVersionBefore { get; init; }

    public required int WorldVersionAfter { get; init; }

    /// <summary>The exit used, when the change was an escape; null otherwise.</summary>
    public string? ExitId { get; init; }

    /// <summary>The present living characters who learned of the change as a public fact.</summary>
    public IReadOnlyList<string> PublicRecipients { get; init; } = [];
}

/// <summary>
/// An exit interaction — opening an exit or escaping through one — accepted or rejected. Carries the exit id,
/// the open/closed transition and the world-version change the generic engine-action row does not surface.
/// </summary>
public sealed record ExitInteractionPayload
{
    public required string ActorId { get; init; }

    public string? ExitId { get; init; }

    /// <summary>"open_exit" or "escape_encounter".</summary>
    public required string ActionType { get; init; }

    /// <summary>The exit's open state before the interaction: "open" or "closed". Null when the exit did not resolve.</summary>
    public string? StateBefore { get; init; }

    public string? StateAfter { get; init; }

    /// <summary>"accepted" when the engine applied it, "rejected" otherwise.</summary>
    public required string ValidationResult { get; init; }

    public string? RejectionReason { get; init; }

    public required int WorldVersionBefore { get; init; }

    public required int WorldVersionAfter { get; init; }
}

/// <summary>
/// A take-cover or leave-cover attempt, accepted or rejected (v0.9). Carries the cover-specific id, the
/// occupancy transition and capacity state the generic engine-action row does not surface.
/// </summary>
public sealed record CoverInteractionPayload
{
    public required string ActorId { get; init; }

    public required string ActorName { get; init; }

    public string? CoverId { get; init; }

    public string? CoverName { get; init; }

    /// <summary>"take_cover" or "leave_cover".</summary>
    public required string ActionType { get; init; }

    /// <summary>The occupant before this attempt, by id, or null when the cover was unoccupied.</summary>
    public string? OccupantBefore { get; init; }

    public string? OccupantAfter { get; init; }

    /// <summary>"Intact", "Damaged" or "Destroyed" — the object's condition at the moment of this attempt.</summary>
    public string? ObjectState { get; init; }

    public int? Capacity { get; init; }

    /// <summary>"accepted" when the engine applied it, "rejected" otherwise.</summary>
    public required string ValidationResult { get; init; }

    public string? RejectionReason { get; init; }

    public required int WorldVersionBefore { get; init; }

    public required int WorldVersionAfter { get; init; }

    public IReadOnlyList<string> PublicRecipients { get; init; } = [];
}

/// <summary>
/// An attack whose target was sheltering behind environmental cover, whatever the result (v0.9). Complements
/// the attack's own <c>attack.hit-check</c> <see cref="RngDraw"/> and <c>EngineAction</c> row with the
/// pre-cover and covered effective hit chances, the classification, and any durability effect — so a covered
/// attack is provably distinguishable from an ordinary one from the trace alone.
/// </summary>
public sealed record AttackAgainstCoverPayload
{
    public required string AttackerId { get; init; }

    public required string AttackerName { get; init; }

    public required string TargetId { get; init; }

    public required string TargetName { get; init; }

    public required string CoverId { get; init; }

    public required string CoverName { get; init; }

    public required int PreCoverHitChance { get; init; }

    public required int CoverHitChanceModifier { get; init; }

    public required int CoveredHitChance { get; init; }

    public required int RawRoll { get; init; }

    /// <summary>"DirectHit", "Intercepted" or "OrdinaryMiss".</summary>
    public required string Classification { get; init; }

    /// <summary>True only for a direct hit — the one case where the ordinary quality draw followed.</summary>
    public required bool QualityDrawFollowed { get; init; }

    public required int DurabilityBefore { get; init; }

    public required int DurabilityAfter { get; init; }

    public required bool Destroyed { get; init; }
}

/// <summary>
/// An attempt to deliberately damage an environmental object, accepted or rejected (v0.9). Carries the
/// object-specific weapon/armour arithmetic and durability transition the generic engine-action row does not
/// surface.
/// </summary>
public sealed record EnvironmentalObjectDamagedPayload
{
    public required string ActorId { get; init; }

    public required string ActorName { get; init; }

    public string? WeaponName { get; init; }

    public int? WeaponDamage { get; init; }

    public string? ObjectId { get; init; }

    public string? ObjectName { get; init; }

    public int? ObjectArmour { get; init; }

    public int? DamageApplied { get; init; }

    public int? DurabilityBefore { get; init; }

    public int? DurabilityAfter { get; init; }

    public bool Destroyed { get; init; }

    public bool SelfCoverBroken { get; init; }

    public string? ExposedOccupantName { get; init; }

    /// <summary>"accepted" when the engine applied it, "rejected" otherwise.</summary>
    public required string ValidationResult { get; init; }

    public string? RejectionReason { get; init; }

    public required int WorldVersionBefore { get; init; }

    public required int WorldVersionAfter { get; init; }

    public IReadOnlyList<string> PublicRecipients { get; init; } = [];
}

/// <summary>
/// An environmental object's durability reached zero (v0.9) — a focused companion to whichever row caused it
/// (an <see cref="AttackAgainstCoverPayload"/> interception, or an <see cref="EnvironmentalObjectDamagedPayload"/>
/// deliberate blow), the same relationship <see cref="CharacterSurrenderedPayload"/> and
/// <see cref="CharacterEscapedPayload"/> have to <see cref="DispositionChangedPayload"/>.
/// </summary>
public sealed record EnvironmentalObjectDestroyedPayload
{
    public required string ObjectId { get; init; }

    public required string ObjectName { get; init; }

    /// <summary>"cover-interception" or "deliberate-damage".</summary>
    public required string Cause { get; init; }

    public string? DestroyedByCharacterId { get; init; }

    public string? DestroyedByCharacterName { get; init; }

    public string? ExposedOccupantId { get; init; }

    public string? ExposedOccupantName { get; init; }

    public required int Round { get; init; }

    public required int Turn { get; init; }

    public IReadOnlyList<string> PublicRecipients { get; init; } = [];
}

/// <summary>
/// An inventory transfer attempt — give, drop or steal — accepted or rejected. Complements the generic
/// engine-action row with the requested versus authoritative bindings, ownership before and after, whether
/// the turn was consumed, and any RNG or rulebook linkage, so a transfer can be audited in full.
/// </summary>
public sealed record InventoryInteractionPayload
{
    /// <summary>"give_item", "drop_item" or "steal_item".</summary>
    public required string ActionType { get; init; }

    public required string ActorId { get; init; }

    public required string ActorName { get; init; }

    /// <summary>The recipient (give) or target (steal), by id, when the action names one.</summary>
    public string? CounterpartyId { get; init; }

    public string? CounterpartyName { get; init; }

    /// <summary>The item reference the Dungeon Master supplied, verbatim.</summary>
    public required string RequestedItemRef { get; init; }

    /// <summary>The item id the reference resolved to in authoritative state, when it resolved.</summary>
    public string? ResolvedItemId { get; init; }

    public string? ResolvedItemName { get; init; }

    /// <summary>"accepted" when the engine applied it, "rejected" otherwise.</summary>
    public required string ValidationResult { get; init; }

    public string? RejectionReason { get; init; }

    /// <summary>Who owned/held the item before the interaction — a character id, or a location such as the floor.</summary>
    public string? OwnerBefore { get; init; }

    /// <summary>Who owns/holds the item after the interaction. Unchanged from before on a rejection or a failed theft.</summary>
    public string? OwnerAfter { get; init; }

    public required int WorldVersionBefore { get; init; }

    public required int WorldVersionAfter { get; init; }

    /// <summary>Whether the attempt consumed the actor's turn. True for any accepted action, including a failed theft.</summary>
    public required bool TurnConsumed { get; init; }

    /// <summary>Whether the engine consulted randomness for this interaction (theft only).</summary>
    public required bool RngConsulted { get; init; }

    /// <summary>For a theft, whether it succeeded. Null for give/drop, which never roll.</summary>
    public bool? TheftSucceeded { get; init; }

    /// <summary>The rulebook consultation that guided this action, for cross-referencing.</summary>
    public string? ConsultationId { get; init; }

    /// <summary>Everyone the public knowledge of this transfer was delivered to.</summary>
    public IReadOnlyList<string> VisibilityRecipients { get; init; } = [];
}

/// <summary>
/// A successful item movement recorded as a provenance event — the item, its previous and new
/// owner/location, the action type, the acting character, the recipient or target where applicable, the
/// round and turn, whether RNG was involved and its trace linkage, and the rulebook consultation. Provenance
/// is an event history, not a second source of authoritative ownership; the sequence of these reconstructs
/// an item's whole journey through the encounter.
/// </summary>
public sealed record ItemProvenancePayload
{
    public required string ItemId { get; init; }

    public required string ItemName { get; init; }

    /// <summary>The previous owner (a character id) or location (e.g. a container id, or the floor).</summary>
    public required string PreviousOwnerOrLocation { get; init; }

    /// <summary>The new owner (a character id) or location.</summary>
    public required string NewOwnerOrLocation { get; init; }

    /// <summary>"give_item", "drop_item", "steal_item" or "take_item".</summary>
    public required string ActionType { get; init; }

    public required string ActingCharacterId { get; init; }

    public required string ActingCharacterName { get; init; }

    /// <summary>The recipient (give) or target (steal), where the action names one.</summary>
    public string? CounterpartyId { get; init; }

    public string? CounterpartyName { get; init; }

    public required int Round { get; init; }

    public required int Turn { get; init; }

    public required int WorldVersionBefore { get; init; }

    public required int WorldVersionAfter { get; init; }

    public required bool RngInvolved { get; init; }

    /// <summary>The purpose of the associated RNG draw, when one was made (theft), so the draw can be found.</summary>
    public string? RngTracePurpose { get; init; }

    /// <summary>The rulebook consultation that guided the action, when one applies.</summary>
    public string? ConsultationId { get; init; }

    /// <summary>The source action or reason the movement happened.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// One automatic rulebook consultation for a take_action request (v0.6): the deterministic retrieval, the
/// stateless resolver call and its structured guidance, validation, cache result, request sizes against the
/// configured limits, and the candidate engine tools the guidance narrowed the Dungeon Master down to. The
/// eventual DM action and engine resolution are linked by <see cref="ConsultationId"/> on the DM-adjudication
/// and inventory/provenance events, rather than duplicated here.
/// </summary>
public sealed record RulebookConsultationPayload
{
    public required string ConsultationId { get; init; }

    public required string ActingCharacterId { get; init; }

    public required string ActingCharacterName { get; init; }

    /// <summary>The character's raw natural-language intent — the only encounter-derived text the resolver saw.</summary>
    public required string RawIntent { get; init; }

    public required string RulebookVersion { get; init; }

    /// <summary>Every rule id retrieval considered (positive-scoring) before the bound was applied.</summary>
    public required IReadOnlyList<string> ConsideredRuleIds { get; init; }

    /// <summary>The rule cards actually supplied to the resolver, each as "id@version".</summary>
    public required IReadOnlyList<string> CardsSupplied { get; init; }

    public required string ResolverProvider { get; init; }

    public required string ResolverModel { get; init; }

    /// <summary>The resolver's requested sampling/limit parameters, for reproducibility.</summary>
    public required IReadOnlyDictionary<string, string> ResolverParameters { get; init; }

    /// <summary>The complete resolver request (intent plus cards). Contains no live game state.</summary>
    public required string ResolverRequest { get; init; }

    /// <summary>The complete raw resolver response.</summary>
    public required string ResolverRawResponse { get; init; }

    /// <summary>The parsed, validated guidance, or null on a failure.</summary>
    public object? ParsedGuidance { get; init; }

    /// <summary>The rule ids and versions the guidance cited (after validation filtering).</summary>
    public IReadOnlyList<string> CitedRules { get; init; } = [];

    /// <summary>"valid" or "invalid: &lt;reason&gt;".</summary>
    public required string ValidationOutcome { get; init; }

    public required bool CacheHit { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public required double LatencyMs { get; init; }

    /// <summary>The candidate engine tools exposed to the Dungeon Master for this request (always includes reject_action).</summary>
    public required IReadOnlyList<string> DmCandidateTools { get; init; }

    /// <summary>Supported, Unsupported, RetrievalFailure, ResolverFailure or MalformedGuidance.</summary>
    public required string Outcome { get; init; }

    public string? FailureDetail { get; init; }

    // Context protection: sizes against the configured limits, so the request can be shown to stay bounded.
    public required int CardCount { get; init; }

    public required int CardInputChars { get; init; }

    public required int TotalRequestChars { get; init; }

    /// <summary>
    /// How the cards were chosen: "WholeRulebook" for the shipped path, or the experimental strategy's name.
    /// </summary>
    public string SelectionMode { get; init; } = "WholeRulebook";

    /// <summary>The rule ids the strategy itself chose, before declared related-rule expansion.</summary>
    public IReadOnlyList<string> SelectionDirectRuleIds { get; init; } = [];

    /// <summary>The rule ids added purely by following declared related-rule links.</summary>
    public IReadOnlyList<string> SelectionExpandedRuleIds { get; init; } = [];

    /// <summary>Why each chosen id was chosen — a similarity score, a model-chosen id, a declared route.</summary>
    public IReadOnlyList<string> SelectionReasons { get; init; } = [];

    /// <summary>Set when the strategy gave up and sent the whole bounded rulebook, with the reason. Null otherwise.</summary>
    public string? SelectionFallback { get; init; }

    /// <summary>Model calls the SELECTION made, not counting the resolver call that follows it.</summary>
    public int SelectionModelCalls { get; init; }

    public long? SelectionInputTokens { get; init; }

    public long? SelectionOutputTokens { get; init; }

    public double SelectionLatencyMs { get; init; }

    public required int MaxCardsConfigured { get; init; }

    public required int MaxInputCharsConfigured { get; init; }

    public required int OutputTokenLimitConfigured { get; init; }

    /// <summary>Whether retrieval dropped cards to stay within the count or size bound.</summary>
    public required bool Trimmed { get; init; }
}

/// <summary>A character surrendering — a focused, semantic event for the transcript and observer UI.</summary>
public sealed record CharacterSurrenderedPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required string Team { get; init; }

    public required int Round { get; init; }

    public required int Turn { get; init; }

    public IReadOnlyList<string> PublicRecipients { get; init; } = [];
}

/// <summary>A character escaping through an exit — a focused, semantic event for the transcript and observer UI.</summary>
public sealed record CharacterEscapedPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required string Team { get; init; }

    public required string ExitId { get; init; }

    public required string ExitName { get; init; }

    public required string DestinationDescription { get; init; }

    public required int Round { get; init; }

    public required int Turn { get; init; }

    public IReadOnlyList<string> PublicRecipients { get; init; } = [];
}

/// <summary>
/// One team's standing at a terminal-condition check, broken down by disposition so a team defeated by
/// surrender or escape is distinguishable from one wiped out, not merely by a living headcount.
/// </summary>
public sealed record TeamStandingPayload
{
    public required string Team { get; init; }

    /// <summary>Members still alive in any disposition (active + surrendered + escaped).</summary>
    public required int Living { get; init; }

    public required int Total { get; init; }

    /// <summary>Members still actively fighting — what decides whether the team is still a contender.</summary>
    public int Active { get; init; }

    public int Surrendered { get; init; }

    public int Escaped { get; init; }

    public int Dead { get; init; }
}

/// <summary>What became of one character who has left active combat, for the team-outcome record.</summary>
public sealed record CharacterResolutionPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required string Team { get; init; }

    public required string Disposition { get; init; }

    /// <summary>The exit used, when the character escaped; null otherwise.</summary>
    public string? ExitName { get; init; }

    /// <summary>The one-line in-world account, e.g. "Skrit escaped through the Cellar Stair Door."</summary>
    public required string Summary { get; init; }
}

public sealed record TeamOutcomePayload
{
    /// <summary>What prompted the check, e.g. "after Rowan's turn".</summary>
    public required string Trigger { get; init; }

    public required bool IsOver { get; init; }

    public required IReadOnlyList<TeamStandingPayload> Standings { get; init; }

    public IReadOnlyList<string> WinningTeams { get; init; } = [];

    public IReadOnlyList<string> EliminatedTeams { get; init; } = [];

    /// <summary>The outcome classification — Elimination, Surrender, Withdrawal, Mixed, Draw, HarnessLimit or Ongoing.</summary>
    public required string Outcome { get; init; }

    /// <summary>What became of each character who has left active combat, in state order.</summary>
    public IReadOnlyList<CharacterResolutionPayload> Resolutions { get; init; } = [];

    /// <summary>A multi-line account of what happened to each character who left the fight.</summary>
    public string? ResolutionSummary { get; init; }

    public required string Description { get; init; }
}

public sealed record NarrationDeliveredPayload
{
    public required int NarrationId { get; init; }

    public required string Narration { get; init; }

    public required IReadOnlyList<string> DeliveredTo { get; init; }

    public required string DeliveryMechanism { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Harness / run lifecycle
// ---------------------------------------------------------------------------------------------

public sealed record TurnStartedPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    /// <summary>The exact self-state block given to the character this turn.</summary>
    public required string SelfStateBlock { get; init; }

    public required IReadOnlyList<int> NarrationsDelivered { get; init; }

    public required GameState StateAtTurnStart { get; init; }
}

public sealed record TurnEndedPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required string Result { get; init; }

    public required int QuestionsAsked { get; init; }

    public required int ActionAttempts { get; init; }

    public int SpeechActs { get; init; }

    public required int ModelCalls { get; init; }

    public string? AcceptedAction { get; init; }
}

public sealed record RoundStartedPayload
{
    public required int Round { get; init; }

    public required GameState State { get; init; }
}

public sealed record HarnessLimitPayload
{
    public required string Limit { get; init; }

    public required int Value { get; init; }

    public required string Effect { get; init; }
}

public sealed record RunStartedPayload
{
    public required string RunId { get; init; }

    public required string ScenarioId { get; init; }

    public required string OutputDirectory { get; init; }

    /// <summary>The master seed this run derives from, so the trace alone is enough to replay it.</summary>
    public required long MasterSeed { get; init; }

    public required bool SeedWasProvided { get; init; }

    public required long GameSeed { get; init; }
}

public sealed record RunCompletedPayload
{
    public required string TerminalCondition { get; init; }

    public required int RoundsPlayed { get; init; }

    public IReadOnlyList<string> Survivors { get; init; } = [];

    public IReadOnlyList<string> Casualties { get; init; } = [];

    /// <summary>The outcome classification of the whole encounter: Elimination, Surrender, Withdrawal, Mixed, Draw or HarnessLimit.</summary>
    public string? Outcome { get; init; }

    /// <summary>The winning team(s), when the encounter reached a decision rather than a harness limit.</summary>
    public IReadOnlyList<string> WinningTeams { get; init; } = [];

    public required GameState FinalState { get; init; }
}

public sealed record RunFailedPayload
{
    public required string ExceptionType { get; init; }

    public required string Message { get; init; }

    public string? StackTrace { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Negotiated surrender, abilities and status effects (v0.7)
// ---------------------------------------------------------------------------------------------

/// <summary>
/// A surrender offer being put on the table. It records the enforceable terms and, separately, the public
/// speech that accompanied them — because the speech is the persuasion and the terms are the contract, and
/// conflating the two is exactly the mistake this release exists to prevent.
/// </summary>
public sealed record SurrenderOfferMadePayload
{
    public required string OfferId { get; init; }

    public required string OffererId { get; init; }

    public required string OffererName { get; init; }

    public required string RecipientId { get; init; }

    public required string RecipientName { get; init; }

    /// <summary>The stable ids of the ordinary items promised.</summary>
    public IReadOnlyList<string> OfferedItemIds { get; init; } = [];

    public IReadOnlyList<string> OfferedItemNames { get; init; } = [];

    public required bool ForfeitWeapon { get; init; }

    public string? WeaponName { get; init; }

    /// <summary>The public-channel id of the offerer's speech this turn, when they spoke before offering.</summary>
    public int? AssociatedSpeechEventId { get; init; }

    /// <summary>The exact words of that speech, for the persuasion report. Never authoritative contract state.</summary>
    public string? AssociatedSpeech { get; init; }

    public required int Round { get; init; }

    public required int Turn { get; init; }

    /// <summary>The visible battle state when the offer was made, so it can be read against the odds it was made under.</summary>
    public required string BattleStateSummary { get; init; }

    /// <summary>Confirms the offer itself moved nothing: always true for a created offer.</summary>
    public bool NothingTransferred { get; init; } = true;

    /// <summary>Confirms the offerer is still active and targetable while the offer stands.</summary>
    public bool OffererRemainsTargetable { get; init; } = true;

    public IReadOnlyList<string> PublicRecipients { get; init; } = [];
}

/// <summary>A surrender offer leaving Pending: accepted, rejected, expired or invalidated, with its cause.</summary>
public sealed record SurrenderOfferResolvedPayload
{
    public required string OfferId { get; init; }

    public required string OffererId { get; init; }

    public required string OffererName { get; init; }

    public required string RecipientId { get; init; }

    public required string RecipientName { get; init; }

    public required string PreviousState { get; init; }

    public required string NewState { get; init; }

    public required string Cause { get; init; }

    public required int CreatedRound { get; init; }

    public required int CreatedTurn { get; init; }

    public required int ResolvedRound { get; init; }

    public required int ResolvedTurn { get; init; }

    /// <summary>How many turns the offer stood before it was resolved — the recipient's response time.</summary>
    public required int TurnsToRespond { get; init; }

    /// <summary>True only for an accepted offer. Every other resolution transfers nothing at all.</summary>
    public required bool AssetsTransferred { get; init; }
}

/// <summary>The durable record of an accepted surrender, as struck.</summary>
public sealed record SurrenderAgreementPayload
{
    public required string AgreementId { get; init; }

    public required string OfferId { get; init; }

    public required string OffererId { get; init; }

    public required string OffererName { get; init; }

    public required string AcceptedById { get; init; }

    public required string AcceptedByName { get; init; }

    public IReadOnlyList<string> TransferredItemIds { get; init; } = [];

    public IReadOnlyList<string> TransferredItemNames { get; init; } = [];

    /// <summary>The stable id of the forfeited weapon, when weapon forfeiture was a promised term.</summary>
    public string? ForfeitedWeaponId { get; init; }

    public string? ForfeitedWeaponName { get; init; }

    /// <summary>Where the weapon now is: the room's floor, as an inert item that can be looted as a trophy.</summary>
    public string? WeaponDisposition { get; init; }

    public required bool OffererDisarmed { get; init; }

    public required int AcceptedRound { get; init; }

    public required int AcceptedTurn { get; init; }

    public int? AssociatedSpeechEventId { get; init; }

    public string? AssociatedSpeech { get; init; }

    public IReadOnlyList<string> PublicRecipients { get; init; } = [];
}

/// <summary>
/// One ability use, accepted or refused. Refusals matter as much as successes here: a refused use must be
/// visible as having spent no charge, so "the charge is not consumed if validation fails" is auditable.
/// </summary>
public sealed record AbilityUsedPayload
{
    public required string ActorId { get; init; }

    public required string ActorName { get; init; }

    public required string AbilityId { get; init; }

    public required string AbilityName { get; init; }

    public required string Category { get; init; }

    /// <summary>The ability reference the Dungeon Master supplied, before resolution.</summary>
    public string? RequestedAbilityRef { get; init; }

    public string? TargetId { get; init; }

    public string? TargetName { get; init; }

    public required string ValidationResult { get; init; }

    public string? RejectionReason { get; init; }

    /// <summary>Charges left before the attempt, or null for an unlimited ability.</summary>
    public int? RemainingUsesBefore { get; init; }

    /// <summary>Charges left after it, or null for an unlimited ability. Unchanged when the attempt was refused.</summary>
    public int? RemainingUsesAfter { get; init; }

    /// <summary>Health restored, when the ability heals. Null otherwise.</summary>
    public int? HealingPerformed { get; init; }

    /// <summary>The status kinds this use applied, if any.</summary>
    public IReadOnlyList<string> StatusesApplied { get; init; } = [];

    /// <summary>Whether this use made the ordinary attack draws.</summary>
    public required bool RngConsulted { get; init; }

    public required int WorldVersionBefore { get; init; }

    public required int WorldVersionAfter { get; init; }

    public required bool TurnConsumed { get; init; }

    /// <summary>The rulebook consultation that led here, so guidance and outcome can be cross-referenced.</summary>
    public string? ConsultationId { get; init; }
}

/// <summary>
/// One status-effect transition: applied, consumed, expired or removed. Every field the release requires of a
/// status instance is carried, so the whole status timeline can be reconstructed from the trace alone.
/// </summary>
public sealed record StatusEffectPayload
{
    public required string StatusId { get; init; }

    public required string Kind { get; init; }

    public required string Transition { get; init; }

    public required string Cause { get; init; }

    public required string SourceCharacterId { get; init; }

    public string? SourceCharacterName { get; init; }

    public required string TargetCharacterId { get; init; }

    public string? TargetCharacterName { get; init; }

    public required int AppliedRound { get; init; }

    public required int AppliedTurn { get; init; }

    public required int Modifier { get; init; }

    public required string ExpiryRule { get; init; }

    public required string Visibility { get; init; }

    /// <summary>Links the two halves of a paired effect (Guarding/Guarded). Null for an unpaired status.</summary>
    public string? RelationshipId { get; init; }

    /// <summary>The ability that applied it, when one did.</summary>
    public string? SourceAbilityId { get; init; }

    public required int Round { get; init; }

    public required int Turn { get; init; }

    public required int WorldVersion { get; init; }

    /// <summary>The action that caused the transition, e.g. "use_ability" or "turn-upkeep".</summary>
    public string? RelatedAction { get; init; }

    /// <summary>The RNG draw this status modified, when it was folded into one. Null otherwise.</summary>
    public string? AffectedRngPurpose { get; init; }
}

/// <summary>
/// A guard relationship moving an attack from its intended target onto the guardian. It names both targets and
/// records how many draws the whole attack made, so it is provable that redirection added no roll of its own.
/// </summary>
public sealed record AttackRedirectedPayload
{
    public required string AttackerId { get; init; }

    public required string AttackerName { get; init; }

    public required string IntendedTargetId { get; init; }

    public required string IntendedTargetName { get; init; }

    public required string AuthoritativeTargetId { get; init; }

    public required string AuthoritativeTargetName { get; init; }

    /// <summary>The armour the damage was actually applied against — the guardian's, not the intended target's.</summary>
    public required int TargetArmourUsed { get; init; }

    public required int TargetHealthBefore { get; init; }

    public required int TargetHealthAfter { get; init; }

    /// <summary>How many draws this whole attack made. Always the ordinary count: one to hit, plus one on a hit.</summary>
    public required int RngDrawCount { get; init; }

    public required int Round { get; init; }

    public required int Turn { get; init; }
}

/// <summary>
/// The deterministic projection a character's question was answered from, and what it withheld. This is the
/// evidence that the Dungeon Master was never handed the material for an ungrounded answer.
/// </summary>
public sealed record AnswerFactsPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required string Question { get; init; }

    /// <summary>The exact text handed to the Dungeon Master.</summary>
    public required string ProjectedFacts { get; init; }

    /// <summary>What the projection deliberately left out. Never sent to any model.</summary>
    public required string OmittedHiddenFacts { get; init; }

    public required int ProjectedFactCount { get; init; }

    public required int OmittedFactCount { get; init; }

    /// <summary>How many affordances the character actually had — the closed list an answer may draw on.</summary>
    public required int AffordanceCount { get; init; }

    public required int WorldVersion { get; init; }

    /// <summary>Confirms the authoritative state block was NOT sent for this task.</summary>
    public bool FullStateWithheld { get; init; } = true;
}

/// <summary>
/// Model output produced after the turn had already been resolved, discarded rather than acted on. Recorded
/// so a model that attacks and then declares a theft is visible as having done so, without the declaration
/// reaching the world, the transcript or the knowledge ledger.
/// </summary>
public sealed record PostResolutionOutputDiscardedPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    /// <summary>The action that had already resolved the turn.</summary>
    public string? ResolvedAction { get; init; }

    /// <summary>
    /// What kind of output was discarded. "tool-call" is a further action the model asked for once its turn
    /// was already resolved — the case that matters, a model trying to act twice. "trailing-text" is loose
    /// prose in the same reply as the accepted call, which the world never reads as an action; it is recorded
    /// because a report that showed silence there would be lying about what the model produced.
    /// </summary>
    public required string DiscardedKind { get; init; }

    /// <summary>The tool the model asked for, when the discarded output was a call.</summary>
    public string? ToolName { get; init; }

    /// <summary>The discarded content verbatim, so nothing the model produced is hidden.</summary>
    public required string DiscardedContent { get; init; }

    /// <summary>Always true: discarded output never becomes state, knowledge or transcript.</summary>
    public bool StateUnchanged { get; init; } = true;

    public required int Round { get; init; }

    public required int Turn { get; init; }
}

/// <summary>
/// One authoritative change to a character's fear: what moved it, what it was, what it became, and whether
/// that crossed the public Scared threshold.
/// </summary>
/// <remarks>
/// Emitted for every change, whether or not a die was involved. A change the clamp absorbed is emitted too,
/// with <see cref="Absorbed"/> set, so "the ceiling ate it" is visible rather than looking like the rule
/// simply failed to fire.
/// </remarks>
public sealed record FearChangedPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required string Team { get; init; }

    /// <summary>The closed-set cause, e.g. "CriticalHitReceived" or "SteadiedByAlly".</summary>
    public required string Cause { get; init; }

    /// <summary>A short factual note naming the authoritative source of the change. Never narration.</summary>
    public required string CauseDetail { get; init; }

    /// <summary>The signed change requested, before clamping.</summary>
    public required int Delta { get; init; }

    public required int FearBefore { get; init; }

    public required int FearAfter { get; init; }

    /// <summary>True when the clamp absorbed the whole change, so the value did not move.</summary>
    public required bool Absorbed { get; init; }

    /// <summary>"None", "BecameScared" or "RecoveredFromScared".</summary>
    public required string ScaredTransition { get; init; }

    public required bool ScaredAfter { get; init; }

    public string? SourceCharacterId { get; init; }

    public string? SourceCharacterName { get; init; }

    /// <summary>The action type the change happened under, e.g. "attack_character".</summary>
    public string? RelatedActionType { get; init; }

    /// <summary>Whether a random draw decided the change. False for every deterministic cause.</summary>
    public required bool RngConsulted { get; init; }

    public required int Round { get; init; }

    public required int Turn { get; init; }

    public required int WorldVersion { get; init; }

    /// <summary>
    /// Who the visible consequence reached. Only a threshold CROSSING is public; a change that did not cross
    /// it is nobody else's knowledge, so this is empty for those.
    /// </summary>
    public IReadOnlyList<string> PublicRecipients { get; init; } = [];
}

/// <summary>
/// One attempt to frighten an opponent, with every modifier and its authoritative source.
/// </summary>
/// <remarks>
/// <see cref="ModifierSources"/> is the point of the record: it shows that each modifier came from the
/// snapshot — the target's own fear, the odds against them, their wounds, the intimidator's nerve — and that
/// nothing about the spoken threat reached the number. The utterance is carried only as a reference.
/// </remarks>
public sealed record IntimidationAttemptedPayload
{
    public required string AttemptId { get; init; }

    public required string ActorId { get; init; }

    public required string ActorName { get; init; }

    public required string TargetId { get; init; }

    public required string TargetName { get; init; }

    /// <summary>The public-channel id of the spoken threat. The words are roleplay and change no odds.</summary>
    public int? AssociatedSpeechEventId { get; init; }

    /// <summary>The threat as spoken, reproduced for the report. Never read by the engine.</summary>
    public string? AssociatedSpeech { get; init; }

    /// <summary>The addressee the speaker declared, when they declared one. Structural, never parsed from the words.</summary>
    public string? SpeechAddressedToId { get; init; }

    public required int BaseChance { get; init; }

    /// <summary>Each modifier as a readable note, in deterministic application order.</summary>
    public required IReadOnlyList<string> Modifiers { get; init; }

    /// <summary>Each modifier's source, signed value and order, so the effective chance can be recomputed exactly.</summary>
    public required IReadOnlyList<RngModifier> ModifierSources { get; init; }

    public required int EffectiveChance { get; init; }

    public required int Roll { get; init; }

    public required bool Succeeded { get; init; }

    /// <summary>The target's fear either side of the attempt. Equal on a failure, which changes nothing.</summary>
    public required int TargetFearBefore { get; init; }

    public required int TargetFearAfter { get; init; }

    public required string ScaredTransition { get; init; }

    public required int Round { get; init; }

    public required int Turn { get; init; }

    public required int WorldVersionBefore { get; init; }

    public required int WorldVersionAfter { get; init; }

    public IReadOnlyList<string> PublicRecipients { get; init; } = [];
}

/// <summary>One character spending their whole turn steadying an ally. Deterministic: no draw is made.</summary>
public sealed record AllySteadiedPayload
{
    public required string ActorId { get; init; }

    public required string ActorName { get; init; }

    public required string TargetId { get; init; }

    public required string TargetName { get; init; }

    public int? AssociatedSpeechEventId { get; init; }

    public string? AssociatedSpeech { get; init; }

    public string? SpeechAddressedToId { get; init; }

    public required int TargetFearBefore { get; init; }

    public required int TargetFearAfter { get; init; }

    /// <summary>True when the ally was already unafraid, so a whole turn bought nothing.</summary>
    public required bool NoEffect { get; init; }

    public required string ScaredTransition { get; init; }

    public required int Round { get; init; }

    public required int Turn { get; init; }

    public required int WorldVersionBefore { get; init; }

    public required int WorldVersionAfter { get; init; }

    public IReadOnlyList<string> PublicRecipients { get; init; } = [];
}

/// <summary>The Encounter Summariser's one call, and what it was built from.</summary>
public sealed record EncounterStoryPayload
{
    public required string ModelId { get; init; }

    public required string Provider { get; init; }

    public required int BriefEventsTotal { get; init; }

    public required int BriefEventsIncluded { get; init; }

    public required bool BriefTrimmed { get; init; }

    public required string Story { get; init; }
}
