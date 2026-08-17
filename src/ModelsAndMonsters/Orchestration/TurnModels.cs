using ModelsAndMonsters.Engine;

namespace ModelsAndMonsters.Orchestration;

/// <summary>
/// How an attempted action was resolved. These are the four categories the design calls for, plus one
/// harness-level case for a Dungeon Master that would not follow the protocol.
/// </summary>
public enum ActionResolutionCategory
{
    /// <summary>DM_IMPOSSIBLE — not plausible for this character in this world. No engine call made.</summary>
    DmImpossible,

    /// <summary>DM_UNSUPPORTED — plausible in the fiction, but the engine cannot represent it. Useful data.</summary>
    DmUnsupported,

    /// <summary>ENGINE_REJECTED — translated into a supported action, but the engine refused it.</summary>
    EngineRejected,

    /// <summary>ENGINE_ACCEPTED — resolved, state changed.</summary>
    EngineAccepted,

    /// <summary>The DM did not produce a usable tool call. A harness failure, not a game outcome.</summary>
    DmProtocolFailure
}

/// <summary>The result of one <c>take_action</c> attempt, including what the character was told.</summary>
public sealed record ActionAttemptOutcome
{
    public required ActionResolutionCategory Category { get; init; }

    /// <summary>Exactly what is handed back to the character as its tool result.</summary>
    public required string MessageToCharacter { get; init; }

    public GameAction? Action { get; init; }

    public EngineResult? EngineResult { get; init; }

    public bool ConsumesTurn => Category == ActionResolutionCategory.EngineAccepted;
}

public enum TurnOutcome
{
    /// <summary>The character attempted something that took effect.</summary>
    ActionResolved,

    /// <summary>The character deliberately chose to do nothing. A decision, not a failure.</summary>
    EndedByCharacter,

    /// <summary>A harness limit stopped the turn before anything took effect.</summary>
    AbandonedAtLimit,

    /// <summary>The character was not alive when its turn came around.</summary>
    Skipped
}

public sealed record TurnResult
{
    public required string CharacterId { get; init; }

    public required string CharacterName { get; init; }

    public required TurnOutcome Outcome { get; init; }

    public required int QuestionsAsked { get; init; }

    public required int ActionAttempts { get; init; }

    public required int ModelCalls { get; init; }

    public string? AcceptedAction { get; init; }
}
