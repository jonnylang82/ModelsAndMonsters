namespace ModelsAndMonsters.Randomness;

/// <summary>
/// The single source of randomness for the game.
/// </summary>
/// <remarks>
/// Every random decision in the engine goes through this interface, so a run is fully reproducible
/// from one seed and every roll can be traced. Tests inject a scripted implementation to force exact
/// outcomes; live runs use <see cref="SeededRng"/>.
/// </remarks>
public interface IRng
{
    /// <summary>The seed this generator was created from, recorded so a run can be replayed.</summary>
    long Seed { get; }

    /// <summary>Rolls a die, returning a value in <c>[1, sides]</c> inclusive.</summary>
    int Roll(int sides);

    /// <summary>Rolls a percentile die, returning a value in <c>[1, 100]</c> inclusive.</summary>
    int RollPercent() => Roll(100);
}
