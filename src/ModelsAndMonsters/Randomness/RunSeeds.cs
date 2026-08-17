using System.Text;

namespace ModelsAndMonsters.Randomness;

/// <summary>
/// Turns one master run seed into all the seeds a run needs.
/// </summary>
/// <remarks>
/// A run has a single master seed. When it is set, the run is fully deterministic. When it is blank,
/// a random master is generated and recorded, so even a "random" run replays exactly by setting that
/// number. From the master, each consumer (the game RNG, and each agent's model sampling) gets its own
/// seed by deterministic derivation, so the seeds are stable across runs of the same master yet differ
/// from one another.
/// </remarks>
public static class RunSeeds
{
    // Well-known derivation keys, so the mapping from master to per-consumer seed is fixed.
    public const string GameKey = "game";
    public const string DungeonMasterKey = "agent:dungeon-master";
    public const string HeroKey = "agent:hero";
    public const string MonsterKey = "agent:monster";

    /// <summary>A fresh, process-random master seed for a run with no configured seed.</summary>
    public static long NewRandomMaster() => Random.Shared.NextInt64(1, long.MaxValue);

    /// <summary>
    /// Derives a stable, distinct seed for <paramref name="key"/> from the master. Uses FNV-1a over the
    /// key mixed with the master, so different keys give well-separated seeds and the same (master, key)
    /// always gives the same result.
    /// </summary>
    public static long Derive(long master, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(key))
        {
            hash ^= b;
            hash *= prime;
        }

        hash ^= unchecked((ulong)master);
        hash *= prime;

        // Keep it positive so it reads cleanly in traces; the sign carries no information.
        return (long)(hash & 0x7FFF_FFFF_FFFF_FFFF);
    }
}
