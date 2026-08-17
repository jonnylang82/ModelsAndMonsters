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

    /// <summary>Everything sent into our <c>IChatClient</c> abstraction for one logical call.</summary>
    ModelRequest,

    /// <summary>Everything the model returned for that call.</summary>
    ModelResponse,

    /// <summary>A model call threw.</summary>
    ModelError,

    /// <summary>The application inspected a requested tool call and decided how to dispatch it.</summary>
    ToolCallDispatched,

    /// <summary>The result the application handed back to the model for a tool call.</summary>
    ToolCallResult,

    /// <summary>A tool call could not be dispatched (unknown tool, malformed arguments, handler threw).</summary>
    ToolCallError,

    CharacterQuestion,
    DungeonMasterAnswer,

    /// <summary>The DM's ruling on a character's natural-language intent.</summary>
    DmAdjudication,

    /// <summary>A structured action submitted to the engine, with before/after state.</summary>
    EngineAction,

    Narration,
    NarrationDelivered,

    /// <summary>A harness protection limit stopped a loop.</summary>
    HarnessLimitReached,

    RunCompleted,
    RunFailed
}
