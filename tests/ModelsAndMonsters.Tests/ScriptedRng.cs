using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// An <see cref="IRng"/> that returns pre-scripted rolls in order, so a test can force exact combat
/// outcomes. Each attack consumes one roll for the hit check and, if it hits, one for the QUALITY
/// check that selects among glancing, solid and critical. Running out of scripted rolls throws, so an
/// under-specified test fails loudly rather than silently.
/// </summary>
internal sealed class ScriptedRng : IRng
{
    private readonly Queue<int> _rolls;

    public ScriptedRng(params int[] rolls)
    {
        _rolls = new Queue<int>(rolls);
    }

    public long Seed => 0;

    public long DrawCount { get; private set; }

    public int Roll(int sides)
    {
        if (_rolls.Count == 0)
        {
            throw new InvalidOperationException("ScriptedRng ran out of rolls; the test needs more scripted values.");
        }

        DrawCount++;
        return _rolls.Dequeue();
    }

    /// <summary>A hit roll low enough to land against any positive hit chance.</summary>
    public const int Hits = 1;

    /// <summary>A hit roll high enough to miss against any hit chance below 100.</summary>
    public const int Misses = 100;

    /// <summary>A quality roll low enough to glance against any positive glancing band.</summary>
    public const int Glances = 1;

    /// <summary>
    /// A quality roll in the middle band: above any ordinary glancing band and below any ordinary critical
    /// band, so it is a plain solid hit. It is deliberately NOT 100 — under v0.8's three-band quality draw a
    /// maximum roll is a CRITICAL hit, and a test that means "no quality variance" must say so in the middle.
    /// </summary>
    public const int Solid = 50;

    /// <summary>A quality roll high enough to be critical against any positive critical band.</summary>
    public const int Critical = 100;
}
