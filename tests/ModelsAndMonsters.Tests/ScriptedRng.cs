using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// An <see cref="IRng"/> that returns pre-scripted rolls in order, so a test can force exact combat
/// outcomes. Each attack consumes one roll for the hit check and, if it hits, one for the glancing
/// check. Running out of scripted rolls throws, so an under-specified test fails loudly rather than
/// silently.
/// </summary>
internal sealed class ScriptedRng : IRng
{
    private readonly Queue<int> _rolls;

    public ScriptedRng(params int[] rolls)
    {
        _rolls = new Queue<int>(rolls);
    }

    public long Seed => 0;

    public int Roll(int sides)
    {
        if (_rolls.Count == 0)
        {
            throw new InvalidOperationException("ScriptedRng ran out of rolls; the test needs more scripted values.");
        }

        return _rolls.Dequeue();
    }

    /// <summary>A hit roll low enough to land against any positive hit chance.</summary>
    public const int Hits = 1;

    /// <summary>A hit roll high enough to miss against any hit chance below 100.</summary>
    public const int Misses = 100;

    /// <summary>A glancing roll low enough to glance against any positive glancing chance.</summary>
    public const int Glances = 1;

    /// <summary>A glancing roll high enough to be a solid hit against any glancing chance below 100.</summary>
    public const int Solid = 100;
}
