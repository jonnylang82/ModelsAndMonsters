using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

public sealed class RandomnessTests
{
    [Fact]
    public void The_same_seed_produces_the_same_sequence()
    {
        var a = new SeededRng(20260817);
        var b = new SeededRng(20260817);

        var rollsA = Enumerable.Range(0, 20).Select(_ => a.Roll(100)).ToList();
        var rollsB = Enumerable.Range(0, 20).Select(_ => b.Roll(100)).ToList();

        Assert.Equal(rollsA, rollsB);
    }

    [Fact]
    public void Different_seeds_diverge()
    {
        var one = new SeededRng(1);
        var two = new SeededRng(2);

        var rollsOne = Enumerable.Range(0, 20).Select(_ => one.Roll(100)).ToList();
        var rollsTwo = Enumerable.Range(0, 20).Select(_ => two.Roll(100)).ToList();

        Assert.NotEqual(rollsOne, rollsTwo);
    }

    [Fact]
    public void Rolls_stay_within_bounds()
    {
        var rng = new SeededRng(7);

        Assert.All(Enumerable.Range(0, 500), _ =>
        {
            var roll = rng.Roll(6);
            Assert.InRange(roll, 1, 6);
        });

        Assert.All(Enumerable.Range(0, 500), _ => Assert.InRange(((IRng)rng).RollPercent(), 1, 100));
    }

    [Fact]
    public void Derivation_is_deterministic_for_a_given_master_and_key()
    {
        Assert.Equal(RunSeeds.Derive(100, "agent:hero"), RunSeeds.Derive(100, "agent:hero"));
    }

    [Fact]
    public void Derivation_gives_each_key_a_distinct_seed()
    {
        var game = RunSeeds.Derive(100, RunSeeds.GameKey);
        var dm = RunSeeds.Derive(100, RunSeeds.DungeonMasterKey);
        var hero = RunSeeds.Derive(100, RunSeeds.HeroKey);
        var monster = RunSeeds.Derive(100, RunSeeds.MonsterKey);

        var all = new[] { game, dm, hero, monster };
        Assert.Equal(all.Length, all.Distinct().Count());
    }

    [Fact]
    public void Changing_the_master_changes_every_derived_seed()
    {
        Assert.NotEqual(RunSeeds.Derive(1, RunSeeds.HeroKey), RunSeeds.Derive(2, RunSeeds.HeroKey));
        Assert.NotEqual(RunSeeds.Derive(1, RunSeeds.GameKey), RunSeeds.Derive(2, RunSeeds.GameKey));
    }

    [Fact]
    public void Derived_seeds_are_non_negative_for_readable_traces()
    {
        Assert.True(RunSeeds.Derive(long.MinValue, RunSeeds.GameKey) >= 0);
        Assert.True(RunSeeds.Derive(-1, RunSeeds.HeroKey) >= 0);
        Assert.True(RunSeeds.NewRandomMaster() > 0);
    }

    [Fact]
    public void A_full_run_replays_from_the_master_seed()
    {
        // Deriving the game seed from a fixed master and running the same sequence twice is identical.
        const long master = 555;
        IRng first = new SeededRng(RunSeeds.Derive(master, RunSeeds.GameKey));
        IRng second = new SeededRng(RunSeeds.Derive(master, RunSeeds.GameKey));

        var a = Enumerable.Range(0, 30).Select(_ => first.RollPercent()).ToList();
        var b = Enumerable.Range(0, 30).Select(_ => second.RollPercent()).ToList();

        Assert.Equal(a, b);
    }
}
