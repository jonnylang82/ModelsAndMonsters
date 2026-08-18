using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Engine;

/// <summary>Why the authoritative engine refused to apply an otherwise well-formed action.</summary>
public enum EngineRejectionReason
{
    UnknownActor,
    ActorIsDead,
    UnknownTarget,
    TargetIsDead,
    TargetIsSelf,
    ActorHasNoWeapon,
    WeaponNotPossessed,
    ItemNotPossessed,
    ItemHasNoSupportedEffect,
    ItemTargetNotSupported,

    // Object and container interaction (v0.3).
    UnknownContainer,
    ContainerReferenceAmbiguous,
    ContainerAlreadyOpen,
    ContainerClosed,
    ItemNotInContainer,
    ItemReferenceAmbiguous,

    UnsupportedAction
}

/// <summary>
/// The outcome of submitting a <see cref="GameAction"/> to the engine, including full before/after
/// state snapshots. v0.1 state is tiny, so whole snapshots are traced rather than diffs.
/// </summary>
public sealed record EngineResult
{
    public required GameAction Action { get; init; }

    public required bool Accepted { get; init; }

    public EngineRejectionReason? RejectionReason { get; init; }

    /// <summary>Plain explanation suitable for feeding back to the Dungeon Master.</summary>
    public string? RejectionMessage { get; init; }

    public required GameState StateBefore { get; init; }

    /// <summary>Identical to <see cref="StateBefore"/> when the action was rejected.</summary>
    public required GameState StateAfter { get; init; }

    public ActionOutcome? Outcome { get; init; }

    /// <summary>
    /// Every random draw this action made, in order. Empty for actions that consult no randomness
    /// (item use) and for rejected actions (validation happens before any roll). The orchestration
    /// layer traces these so no draw is recorded only as its final effect.
    /// </summary>
    public IReadOnlyList<RngDraw> RngDraws { get; init; } = [];

    public static EngineResult Reject(GameAction action, GameState state, EngineRejectionReason reason, string message) =>
        new()
        {
            Action = action,
            Accepted = false,
            RejectionReason = reason,
            RejectionMessage = message,
            StateBefore = state,
            StateAfter = state
        };

    public static EngineResult Accept(GameAction action, GameState before, GameState after, ActionOutcome outcome,
        IReadOnlyList<RngDraw>? rngDraws = null) =>
        new()
        {
            Action = action,
            Accepted = true,
            StateBefore = before,
            StateAfter = after,
            Outcome = outcome,
            RngDraws = rngDraws ?? []
        };
}
