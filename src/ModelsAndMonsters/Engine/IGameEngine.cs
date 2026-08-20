using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Engine;

/// <summary>
/// The authoritative, deterministic source of truth. No randomness, no dice, no hit rolls.
/// </summary>
public interface IGameEngine
{
    /// <summary>The current authoritative state.</summary>
    GameState State { get; }

    /// <summary>The round counter, used to stamp injuries and status effects. Set by the orchestration layer.</summary>
    int CurrentRound { get; set; }

    /// <summary>
    /// The global turn number being played, stamped onto every status the engine applies. Set by
    /// <see cref="BeginActorTurn"/>; a status can never expire on the turn it was applied.
    /// </summary>
    int CurrentTurn { get; set; }

    /// <summary>
    /// Validates and, if valid, applies an action. Rejected actions never mutate <see cref="State"/>.
    /// </summary>
    EngineResult Execute(GameAction action);

    /// <summary>
    /// Start-of-turn upkeep for one actor, before any model is asked anything: expires the status effects
    /// whose rule fires at the start of this actor's turn. Deterministic and never random.
    /// </summary>
    TurnUpkeep BeginActorTurn(string actorId, int round, int turn);

    /// <summary>
    /// End-of-turn upkeep for one actor: expires the status effects whose rule fires at the end of this
    /// actor's turn, and lapses any surrender offer this actor was the named recipient of and did not accept.
    /// Deterministic and never random.
    /// </summary>
    TurnUpkeep EndActorTurn(string actorId, int round, int turn);
}
