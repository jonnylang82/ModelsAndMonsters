using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.8 attack-quality draw: one roll selecting among glancing, solid and critical, and the damage
/// arithmetic that follows it.
/// </summary>
public sealed class AttackQualityTests
{
    private static GameEngine Engine(IRng rng, params Character[] characters) =>
        new(TestWorld.State(characters), rng, CombatRules.Default);

    // ------------------------------------------------------------------------------------------
    // The bands
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, AttackQuality.Glancing)]
    [InlineData(25, AttackQuality.Glancing)]
    [InlineData(26, AttackQuality.Solid)]
    [InlineData(50, AttackQuality.Solid)]
    [InlineData(75, AttackQuality.Solid)]
    [InlineData(76, AttackQuality.Critical)]
    [InlineData(100, AttackQuality.Critical)]
    public void The_quality_bands_are_1_to_25_glancing_26_to_75_solid_and_76_to_100_critical(
        int roll, AttackQuality expected) =>
        Assert.Equal(expected, CombatRules.Default.QualityFor(roll));

    [Fact]
    public void The_boundaries_sit_exactly_where_the_rules_say()
    {
        // The four values either side of the two boundaries, stated as the rule rather than derived from it.
        Assert.Equal(AttackQuality.Glancing, CombatRules.Default.QualityFor(25));
        Assert.Equal(AttackQuality.Solid, CombatRules.Default.QualityFor(26));
        Assert.Equal(AttackQuality.Solid, CombatRules.Default.QualityFor(75));
        Assert.Equal(AttackQuality.Critical, CombatRules.Default.QualityFor(76));
    }

    [Fact]
    public void Both_bands_can_be_configured_off_leaving_every_landed_blow_solid()
    {
        foreach (var roll in new[] { 1, 25, 50, 76, 100 })
        {
            Assert.Equal(AttackQuality.Solid, CombatRules.NoGlancing.QualityFor(roll));
        }
    }

    // ------------------------------------------------------------------------------------------
    // Damage
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ScriptedRng.Glances, 2)]  // 5 weapon - 1 armour = 4, halved
    [InlineData(ScriptedRng.Solid, 4)]    // full
    [InlineData(ScriptedRng.Critical, 8)] // doubled
    public void Quality_multiplies_post_armour_damage(int qualityRoll, int expectedDamage)
    {
        var target = TestWorld.Monster(health: 20, maxHealth: 20, armour: 1);
        var attacker = TestWorld.Hero(weapon: new Weapon("Iron Sword", 5));
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, qualityRoll), attacker, target);

        var result = engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));

        var outcome = Assert.IsType<AttackOutcome>(result.Outcome);
        Assert.Equal(4, outcome.BaseDamage);
        Assert.Equal(expectedDamage, outcome.DamageDealt);
    }

    [Fact]
    public void Armour_is_subtracted_before_the_multiplier_so_a_blow_armour_stopped_stays_stopped()
    {
        // 3 weapon against 3 armour: nothing gets through, and doubling nothing is still nothing.
        var target = TestWorld.Monster(health: 20, maxHealth: 20, armour: 3);
        var attacker = TestWorld.Hero(weapon: new Weapon("Iron Sword", 3));
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Critical), attacker, target);

        var outcome = Assert.IsType<AttackOutcome>(engine.Execute(
            new AttackCharacterAction("Aric", "Grik", "Iron Sword")).Outcome);

        Assert.Equal(0, outcome.BaseDamage);
        Assert.Equal(0, outcome.DamageDealt);
        Assert.True(outcome.Critical);
    }

    [Fact]
    public void A_glancing_blow_rounds_half_damage_away_from_zero()
    {
        // 4 weapon - 1 armour = 3; half of 3 rounds up to 2, exactly as it did before v0.8.
        Assert.Equal(2, CombatRules.GlancingDamage(3));
        Assert.Equal(2, CombatRules.GlancingDamage(4));
        Assert.Equal(1, CombatRules.GlancingDamage(1));
        Assert.Equal(0, CombatRules.GlancingDamage(0));
    }

    [Fact]
    public void Defending_is_applied_after_the_quality_multiplier_and_never_below_zero()
    {
        var target = TestWorld.SkritV07(health: 30) with { MaxHealth = 30 };
        var engine = new GameEngine(
            TestWorld.V07State(exitOpen: false, TestWorld.RowanV07(), target),
            new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Critical), CombatRules.Default);

        engine.Execute(new DefendAction("Skrit"));
        var outcome = Assert.IsType<AttackOutcome>(engine.Execute(
            new AttackCharacterAction("Rowan", "Skrit", "Longsword")).Outcome);

        // 5 - 1 armour = 4, doubled to 8, then the guard turns aside 1.
        Assert.Equal(4, outcome.BaseDamage);
        Assert.Equal(1, outcome.DefendReduction);
        Assert.Equal(7, outcome.DamageDealt);
    }

    // ------------------------------------------------------------------------------------------
    // The draw and its trace
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void One_quality_draw_is_taken_on_a_hit_and_none_on_a_miss()
    {
        var attacker = TestWorld.Hero(hitChance: 50);
        var missing = Engine(new ScriptedRng(ScriptedRng.Misses), attacker, TestWorld.Monster());
        var missed = missing.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));
        Assert.Equal(["attack.hit-check"], missed.RngDraws.Select(d => d.Purpose));

        var landing = Engine(new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid), attacker, TestWorld.Monster());
        var landed = landing.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));
        Assert.Equal(["attack.hit-check", "attack.quality-check"], landed.RngDraws.Select(d => d.Purpose));
    }

    [Fact]
    public void The_quality_draw_records_every_field_needed_to_reconstruct_it()
    {
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, 76), TestWorld.Hero(), TestWorld.Monster(health: 20, maxHealth: 20));

        var result = engine.Execute(new AttackCharacterAction("Aric", "Grik", "Iron Sword"));

        var draw = Assert.Single(result.RngDraws, d => d.Purpose == "attack.quality-check");
        Assert.Equal("attack_character", draw.ActionType);
        Assert.Equal(TestWorld.HeroId, draw.ActorId);
        Assert.Equal("Aric", draw.ActorName);
        Assert.Equal(TestWorld.MonsterId, draw.TargetId);
        Assert.Equal("Grik", draw.TargetName);
        Assert.Equal("glancing, solid or critical (1-25 glancing; 26-75 solid; 76-100 critical)", draw.OutcomeSelected);
        Assert.Equal(100, draw.Sides);
        Assert.Equal(1, draw.RangeMin);
        Assert.Equal(100, draw.RangeMax);
        Assert.Equal(76, draw.RawRoll);
        Assert.Equal(25, draw.Threshold);
        // A quality roll lands in a band; it is not measured against a threshold. Describing it as one
        // produced "rolled 99 vs 25 → critical" in the report, where 25 is the top of the GLANCING band at
        // the other end of the roll. The Threshold field is still carried for replay, but the words the
        // report renders come from here.
        Assert.Equal("roll 76 in critical band 76-100", draw.Comparison);
        Assert.Equal("critical", draw.Result);
        Assert.Equal(0, draw.Seed);

        // The generator's position either side, so the draw can be replayed from the run seed.
        Assert.Equal(1, draw.SequenceBefore);
        Assert.Equal(2, draw.SequenceAfter);
    }

    [Fact]
    public void A_fixed_seed_replays_the_same_qualities_and_the_same_fear()
    {
        static (List<AttackQuality> Qualities, List<int> Fear) Play(long seed)
        {
            var engine = new GameEngine(
                TestWorld.V07State(exitOpen: false,
                    TestWorld.RowanV07(), TestWorld.ElaraV07(),
                    TestWorld.VarkV07(health: 60) with { MaxHealth = 60 },
                    TestWorld.SkritV07(health: 60) with { MaxHealth = 60 }),
                new SeededRng(seed), CombatRules.Default);

            var qualities = new List<AttackQuality>();
            for (var i = 0; i < 12; i++)
            {
                var attacker = i % 2 == 0 ? "Rowan" : "Elara";
                var weapon = i % 2 == 0 ? "Longsword" : "Iron Mace";
                var target = i % 4 < 2 ? "Vark" : "Skrit";
                var result = engine.Execute(new AttackCharacterAction(attacker, target, weapon));
                if (result.Outcome is AttackOutcome { Hit: true } outcome)
                {
                    qualities.Add(outcome.Quality);
                }
            }

            return (qualities, [.. engine.State.Characters.Select(c => c.Fear)]);
        }

        var first = Play(20260820);
        var second = Play(20260820);

        Assert.Equal(first.Qualities, second.Qualities);
        Assert.Equal(first.Fear, second.Fear);

        // And the run was actually varied, or the comparison proves nothing.
        Assert.True(first.Qualities.Distinct().Count() > 1,
            $"The seeded run produced only {string.Join(", ", first.Qualities.Distinct())} — it proves nothing about replay.");
    }
}
