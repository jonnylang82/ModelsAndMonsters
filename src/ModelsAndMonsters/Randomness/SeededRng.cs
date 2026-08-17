namespace ModelsAndMonsters.Randomness;

/// <summary>
/// The production <see cref="IRng"/>: a seeded <see cref="Random"/>. The same seed always produces the
/// same sequence, which is what makes a whole run replayable from a single number.
/// </summary>
public sealed class SeededRng : IRng
{
    private readonly Random _random;

    public SeededRng(long seed)
    {
        Seed = seed;
        // Random takes a 32-bit seed; fold the 64-bit seed into it so the whole value matters.
        _random = new Random(unchecked((int)(seed ^ (seed >> 32))));
    }

    public long Seed { get; }

    public int Roll(int sides)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sides, 1);
        return _random.Next(1, sides + 1);
    }
}
