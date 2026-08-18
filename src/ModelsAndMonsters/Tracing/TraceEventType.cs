namespace ModelsAndMonsters.Tracing;

/// <summary>
/// The vocabulary of trace events. Serialised as strings so trace files stay readable.
/// </summary>
public enum TraceEventType
{
    RunStarted,
    ScenarioSeeded,
    RoundStarted,
    TurnStarted,
    TurnEnded,

    /// <summary>An actor's turn was skipped because it was not alive; no model call was made.</summary>
    TurnSkipped,

    /// <summary>Everything sent into our <c>IChatClient</c> abstraction for one logical call.</summary>
    ModelRequest,

    /// <summary>Everything the model returned for that call.</summary>
    ModelResponse,

    /// <summary>A model call threw.</summary>
    ModelError,

    /// <summary>
    /// The model stopped because it ran out of output budget rather than because it had finished.
    /// Anything it was part-way through — most importantly a tool call — may be missing or malformed.
    /// </summary>
    ModelResponseTruncated,

    /// <summary>
    /// A request came close enough to the agent's context window that the provider may have discarded
    /// the oldest messages. Ollama does this silently, so it has to be inferred and reported.
    /// </summary>
    ContextWindowSaturated,

    /// <summary>The application inspected a requested tool call and decided how to dispatch it.</summary>
    ToolCallDispatched,

    /// <summary>The result the application handed back to the model for a tool call.</summary>
    ToolCallResult,

    /// <summary>A tool call could not be dispatched (unknown tool, malformed arguments, handler threw).</summary>
    ToolCallError,

    /// <summary>A tool call the model wrote as prose was parsed and dispatched as if it had been called.</summary>
    ToolCallRecovered,

    CharacterQuestion,
    DungeonMasterAnswer,

    /// <summary>
    /// A character spoke aloud to the room. Records the verbatim message and every living recipient, so
    /// the delivery of public speech can be verified independently of the narration channel.
    /// </summary>
    CharacterSpeech,

    /// <summary>A character deliberately chose to do nothing with its turn.</summary>
    CharacterPassed,

    /// <summary>
    /// An attempt to open a container or take an item from one, accepted or rejected. Complements the
    /// generic <see cref="EngineAction"/> row with the object-specific ids and version transition.
    /// </summary>
    ObjectInteraction,

    /// <summary>The harness corrected a tool argument the Dungeon Master got structurally wrong.</summary>
    AdjudicationCorrected,

    /// <summary>The DM's ruling on a character's natural-language intent.</summary>
    DmAdjudication,

    /// <summary>How a natural-language target reference was resolved to a specific character (or not).</summary>
    TargetResolved,

    /// <summary>A structured action submitted to the engine, with before/after state.</summary>
    EngineAction,

    /// <summary>One complete random draw the engine made, with full context for replay and comparison.</summary>
    RngDraw,

    /// <summary>The team terminal condition was evaluated after an accepted action or a turn.</summary>
    TeamOutcomeEvaluated,

    Narration,
    NarrationDelivered,

    /// <summary>A harness protection limit stopped a loop.</summary>
    HarnessLimitReached,

    RunCompleted,
    RunFailed
}
