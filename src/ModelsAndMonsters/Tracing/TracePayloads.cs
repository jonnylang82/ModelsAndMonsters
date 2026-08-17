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
}

public sealed record CharacterPassedPayload
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required string Reason { get; init; }
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
}

/// <summary>
/// One team's standing at a terminal-condition check: how many of its members are still alive.
/// </summary>
public sealed record TeamStandingPayload
{
    public required string Team { get; init; }

    public required int Living { get; init; }

    public required int Total { get; init; }
}

public sealed record TeamOutcomePayload
{
    /// <summary>What prompted the check, e.g. "after Rowan's turn".</summary>
    public required string Trigger { get; init; }

    public required bool IsOver { get; init; }

    public required IReadOnlyList<TeamStandingPayload> Standings { get; init; }

    public IReadOnlyList<string> WinningTeams { get; init; } = [];

    public IReadOnlyList<string> EliminatedTeams { get; init; } = [];

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

    public required GameState FinalState { get; init; }
}

public sealed record RunFailedPayload
{
    public required string ExceptionType { get; init; }

    public required string Message { get; init; }

    public string? StackTrace { get; init; }
}
