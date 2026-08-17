using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Engine;

/// <summary>
/// The authoritative, deterministic source of truth. No randomness, no dice, no hit rolls.
/// </summary>
public interface IGameEngine
{
    /// <summary>The current authoritative state.</summary>
    GameState State { get; }

    /// <summary>The round counter, used only to stamp injuries. Set by the orchestration layer.</summary>
    int CurrentRound { get; set; }

    /// <summary>
    /// Validates and, if valid, applies an action. Rejected actions never mutate <see cref="State"/>.
    /// </summary>
    EngineResult Execute(GameAction action);
}
