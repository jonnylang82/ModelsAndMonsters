using System.Text.Json;
using ModelsAndMonsters.Domain;

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
