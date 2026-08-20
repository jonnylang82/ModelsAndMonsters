using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.8 morale rules at the engine boundary: fear, the public Scared threshold, and the two ways fear
/// moves without anybody choosing to move it (a telling blow, and the odds turning).
/// </summary>
public sealed class MoraleEngineTests
{
    // A quality roll in each band, named so the tests read as rules rather than as numbers.
    private const int Glances = ScriptedRng.Glances;
    private const int Solid = ScriptedRng.Solid;
    private const int Critical = ScriptedRng.Critical;

    private static GameEngine Engine(IRng rng, params Character[] characters) =>
        new(TestWorld.V07State(exitOpen: false, characters), rng, CombatRules.Default);

    // ------------------------------------------------------------------------------------------
    // Fear as a value
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Fear_defaults_to_zero_and_nobody_starts_scared()
    {
        var engine = TestWorld.V07Engine();

        foreach (var character in engine.State.Characters)
        {
            Assert.Equal(0, character.Fear);
            Assert.False(character.IsScared);
        }

        Assert.DoesNotContain(engine.State.Statuses, s => s.Kind == StatusEffectKind.Scared);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(9, 5)]
    public void Fear_clamps_between_zero_and_five(int requested, int expected) =>
        Assert.Equal(expected, (TestWorld.Vark() with { Fear = requested }).Fear);

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(5, true)]
    public void Scared_is_derived_from_the_threshold_and_never_stored_separately(int fear, bool scared) =>
        Assert.Equal(scared, (TestWorld.Vark() with { Fear = fear }).IsScared);

    // ------------------------------------------------------------------------------------------
    // Fear from blows
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_critical_hit_frightens_a_surviving_target()
    {
        // Vark: 12 max health, 2 armour, against Rowan's 5-damage longsword. Post-armour 3, doubled to 6 —
        // half of 12, so this is both critical AND large, which is the double-count case below. Give Vark
        // more health so the critical alone is what frightens him.
        var vark = TestWorld.VarkV07(health: 40) with { MaxHealth = 40 };
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, Critical), TestWorld.RowanV07(), vark);

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));

        Assert.True(result.Accepted);
        var change = Assert.Single(result.FearChanges, f => f.CharacterId == TestWorld.VarkId);
        Assert.Equal(FearChangeCause.CriticalHitReceived, change.Cause);
        Assert.Equal(1, change.Delta);
        Assert.Equal(0, change.Before);
        Assert.Equal(1, change.After);
        Assert.Equal(1, engine.State.RequireById(TestWorld.VarkId).Fear);
    }

    [Fact]
    public void A_large_non_critical_hit_frightens_a_surviving_target()
    {
        // Post-armour 3 on a solid hit, against a maximum of 8: three eighths, over the quarter threshold.
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, Solid), TestWorld.RowanV07(), TestWorld.SkritV07());

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Skrit", "Longsword"));

        var change = Assert.Single(result.FearChanges, f => f.CharacterId == TestWorld.SkritId);
        Assert.Equal(FearChangeCause.LargeHitReceived, change.Cause);
        Assert.Equal(1, engine.State.RequireById(TestWorld.SkritId).Fear);
    }

    [Fact]
    public void A_small_solid_hit_frightens_nobody()
    {
        // Post-armour 3 against Vark's 40 maximum: well under a quarter, and not critical.
        var vark = TestWorld.VarkV07(health: 40) with { MaxHealth = 40 };
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, Solid), TestWorld.RowanV07(), vark);

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));

        Assert.Empty(result.FearChanges);
        Assert.Equal(0, engine.State.RequireById(TestWorld.VarkId).Fear);
    }

    [Fact]
    public void A_critical_hit_that_is_also_a_large_hit_frightens_only_once()
    {
        // Post-armour 3, doubled to 6, against Vark's 12 maximum: exactly half, so both reasons apply.
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, Critical), TestWorld.RowanV07(), TestWorld.VarkV07());

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));

        var change = Assert.Single(result.FearChanges, f => f.CharacterId == TestWorld.VarkId);
        Assert.Equal(FearChangeCause.CriticalAndLargeHitReceived, change.Cause);
        Assert.Equal(1, change.Delta);
        Assert.Equal(1, engine.State.RequireById(TestWorld.VarkId).Fear);
    }

    [Fact]
    public void A_dead_target_gains_no_fear()
    {
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, Critical),
            TestWorld.RowanV07(), TestWorld.SkritV07(health: 3));

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Skrit", "Longsword"));

        Assert.True(((AttackOutcome)result.Outcome!).TargetDied);
        Assert.DoesNotContain(result.FearChanges, f => f.CharacterId == TestWorld.SkritId);
        Assert.Equal(0, engine.State.RequireById(TestWorld.SkritId).Fear);
    }

    [Fact]
    public void Landing_a_critical_hit_steadies_a_frightened_attacker()
    {
        var rowan = TestWorld.RowanV07() with { Fear = 4 };
        var vark = TestWorld.VarkV07(health: 40) with { MaxHealth = 40 };
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, Critical), rowan, vark);

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));

        var attacker = Assert.Single(result.FearChanges, f => f.CharacterId == TestWorld.RowanId);
        Assert.Equal(FearChangeCause.LandedCriticalHit, attacker.Cause);
        Assert.Equal(3, engine.State.RequireById(TestWorld.RowanId).Fear);

        // And in the same resolution, the surviving target was frightened. Both directions, one attack.
        Assert.Contains(result.FearChanges, f => f.CharacterId == TestWorld.VarkId && f.Delta > 0);
    }

    [Fact]
    public void An_unafraid_attacker_landing_a_critical_hit_records_no_change_at_the_floor()
    {
        var vark = TestWorld.VarkV07(health: 40) with { MaxHealth = 40 };
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, Critical), TestWorld.RowanV07(), vark);

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));

        // The attacker had nothing to shed, so no pointless clamped-to-zero row is recorded for them.
        Assert.DoesNotContain(result.FearChanges, f => f.CharacterId == TestWorld.RowanId);
        Assert.Equal(0, engine.State.RequireById(TestWorld.RowanId).Fear);
    }

    // ------------------------------------------------------------------------------------------
    // The Scared threshold
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Scared_is_applied_publicly_at_three_and_removed_below_it()
    {
        var vark = TestWorld.VarkV07(health: 40) with { MaxHealth = 40, Fear = 2 };
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, Critical),
            TestWorld.RowanV07(), vark, TestWorld.SkritV07());

        var crossing = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));

        var status = Assert.Single(engine.State.StatusesOn(TestWorld.VarkId), s => s.Kind == StatusEffectKind.Scared);
        Assert.Equal(StatusVisibility.Public, status.Visibility);
        Assert.Equal(StatusExpiryRule.WhileConditionHolds, status.ExpiryRule);
        Assert.Equal(0, status.Modifier);
        Assert.Equal(ScaredTransition.BecameScared,
            crossing.FearChanges.Single(f => f.CharacterId == TestWorld.VarkId).Transition);
        Assert.Contains(crossing.StatusEvents,
            e => e.Kind == StatusEventKind.Applied && e.Status.Kind == StatusEffectKind.Scared);

        // A companion steadying him brings him back under the threshold, and the public status goes with it.
        var recovery = engine.Execute(new SteadyAllyAction("Skrit", "Vark", AssociatedSpeechEventId: 7));

        Assert.True(recovery.Accepted);
        Assert.Equal(2, engine.State.RequireById(TestWorld.VarkId).Fear);
        Assert.False(engine.State.RequireById(TestWorld.VarkId).IsScared);
        Assert.DoesNotContain(engine.State.StatusesOn(TestWorld.VarkId), s => s.Kind == StatusEffectKind.Scared);
        Assert.Equal(ScaredTransition.RecoveredFromScared,
            recovery.FearChanges.Single(f => f.CharacterId == TestWorld.VarkId).Transition);
    }

    [Fact]
    public void A_fear_change_that_does_not_cross_the_threshold_reports_no_transition()
    {
        var vark = TestWorld.VarkV07(health: 40) with { MaxHealth = 40, Fear = 1 };
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, Critical), TestWorld.RowanV07(), vark);

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));

        Assert.Equal(ScaredTransition.None,
            result.FearChanges.Single(f => f.CharacterId == TestWorld.VarkId).Transition);
        Assert.DoesNotContain(engine.State.StatusesOn(TestWorld.VarkId), s => s.Kind == StatusEffectKind.Scared);
    }

    [Fact]
    public void The_scared_status_survives_turn_upkeep_because_it_answers_to_fear_and_not_the_clock()
    {
        var vark = TestWorld.VarkV07() with { Fear = 4 };
        var engine = TestWorld.V07Engine(new SeededRng(1), CombatRules.NoGlancing,
            TestWorld.RowanV07(), vark, TestWorld.SkritV07());

        // Seeded above the threshold, the status is not yet applied — it is applied on a CHANGE. Move fear
        // to make the engine mint it, then run a full turn of upkeep either side.
        engine.Execute(new SteadyAllyAction("Skrit", "Vark", AssociatedSpeechEventId: 1));
        engine.BeginActorTurn(TestWorld.VarkId, round: 2, turn: 5);
        engine.EndActorTurn(TestWorld.VarkId, round: 2, turn: 5);

        Assert.Equal(3, engine.State.RequireById(TestWorld.VarkId).Fear);
        Assert.True(engine.State.RequireById(TestWorld.VarkId).IsScared);
        Assert.Single(engine.State.StatusesOn(TestWorld.VarkId), s => s.Kind == StatusEffectKind.Scared);
    }

    // ------------------------------------------------------------------------------------------
    // Outnumbering
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_character_who_starts_outnumbered_is_not_frightened_by_it()
    {
        // Three goblins against one hero: Rowan is outnumbered from the first moment, and has made no
        // transition into it. The latch is seeded so the fight does not open with a free point of fear.
        var engine = new GameEngine(
            TestWorld.State(TestWorld.Rowan(), TestWorld.Vark(), TestWorld.Skrit(),
                TestWorld.Skrit() with { Id = "goblin-third", Name = "Third" }),
            new SeededRng(1), CombatRules.NoGlancing);

        Assert.True(engine.State.RequireById(TestWorld.RowanId).IsOutnumbered);
        Assert.Equal(0, engine.State.RequireById(TestWorld.RowanId).Fear);
    }

    [Fact]
    public void Becoming_outnumbered_frightens_once_and_staying_outnumbered_does_not_frighten_again()
    {
        // Two on two. Rowan kills Skrit, which leaves Vark alone against two: a transition for Vark alone.
        var engine = Engine(new ScriptedRng(
                ScriptedRng.Hits, Solid,   // Rowan kills Skrit
                ScriptedRng.Hits, Solid),  // Rowan strikes Vark, who is already outnumbered
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(health: 40) with { MaxHealth = 40 },
            TestWorld.SkritV07(health: 3));

        var lethal = engine.Execute(new AttackCharacterAction("Rowan", "Skrit", "Longsword"));

        var vark = engine.State.RequireById(TestWorld.VarkId);
        Assert.True(vark.IsOutnumbered);
        Assert.Equal(1, vark.Fear);
        var change = Assert.Single(lethal.FearChanges, f => f.CharacterId == TestWorld.VarkId);
        Assert.Equal(FearChangeCause.BecameOutnumbered, change.Cause);

        // A second blow leaves him just as outnumbered, and does not frighten him for it again.
        var second = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));

        Assert.DoesNotContain(second.FearChanges, f => f.Cause == FearChangeCause.BecameOutnumbered);
        Assert.Equal(1, engine.State.RequireById(TestWorld.VarkId).Fear);
    }

    [Fact]
    public void Leaving_and_re_entering_an_outnumbered_state_can_frighten_again()
    {
        // Three heroes, three goblins. A goblin falls (goblins outnumbered, +1 each), then a hero falls
        // (parity restored, latch clears), then another goblin falls (outnumbered again, +1 each).
        var heroes = new[]
        {
            TestWorld.Rowan(),
            TestWorld.Rowan(health: 1) with { Id = "hero-b", Name = "Bryn" },
            TestWorld.Rowan() with { Id = "hero-c", Name = "Cass" }
        };
        var goblins = new[]
        {
            TestWorld.Skrit(health: 3),
            TestWorld.Skrit(health: 3) with { Id = "goblin-b", Name = "Grub" },
            TestWorld.Skrit(health: 40) with { Id = "goblin-c", Name = "Nix", MaxHealth = 40 }
        };

        var engine = new GameEngine(
            TestWorld.State([.. heroes, .. goblins]), new ScriptedRng(
                ScriptedRng.Hits, Solid,   // Rowan kills Skrit
                ScriptedRng.Hits, Solid,   // Nix kills Bryn
                ScriptedRng.Hits, Solid),  // Rowan kills Grub
            CombatRules.Default);

        engine.Execute(new AttackCharacterAction("Rowan", "Skrit", "Longsword"));
        Assert.True(engine.State.RequireById("goblin-c").IsOutnumbered);
        Assert.Equal(1, engine.State.RequireById("goblin-c").Fear);

        engine.Execute(new AttackCharacterAction("Nix", "Bryn", "Crude Spear"));
        Assert.False(engine.State.RequireById("goblin-c").IsOutnumbered);
        Assert.Equal(1, engine.State.RequireById("goblin-c").Fear);

        var again = engine.Execute(new AttackCharacterAction("Rowan", "Grub", "Longsword"));
        Assert.True(engine.State.RequireById("goblin-c").IsOutnumbered);
        Assert.Equal(2, engine.State.RequireById("goblin-c").Fear);
        Assert.Contains(again.FearChanges,
            f => f.CharacterId == "goblin-c" && f.Cause == FearChangeCause.BecameOutnumbered);
    }

    [Fact]
    public void Only_active_combatants_count_toward_the_odds()
    {
        // Rowan and Elara against Vark and Skrit, and Skrit surrenders: Vark is alone against two.
        var engine = TestWorld.V07Engine(new SeededRng(1), CombatRules.NoGlancing);

        engine.Execute(new OfferSurrenderAction("Skrit", "Rowan", ["purse-skrit"], ForfeitWeapon: true));
        var accepted = engine.Execute(new AcceptSurrenderAction("Rowan", "offer-1"));

        Assert.True(accepted.Accepted);
        Assert.True(engine.State.RequireById(TestWorld.VarkId).IsOutnumbered);
        Assert.Equal(1, engine.State.RequireById(TestWorld.VarkId).Fear);

        // The one who left keeps the nerve they left with, and the latch is not rewritten for them.
        Assert.Equal(0, engine.State.RequireById(TestWorld.SkritId).Fear);
    }

    // ------------------------------------------------------------------------------------------
    // A departure keeps its final fear
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Leaving_the_fight_keeps_the_number_and_drops_the_visible_state()
    {
        // The engine sweeps a departed character's statuses, so the derived flag has to agree with that or
        // the state contradicts itself: scared, with nothing on them to show it.
        var engine = TestWorld.V07Engine(new SeededRng(1), CombatRules.NoGlancing,
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07() with { Fear = 4 }, TestWorld.SkritV07());

        Assert.True(engine.State.RequireById(TestWorld.VarkId).IsScared);
        Assert.Single(engine.State.StatusesOn(TestWorld.VarkId), s => s.Kind == StatusEffectKind.Scared);

        engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: true));
        Assert.True(engine.Execute(new AcceptSurrenderAction("Rowan", "offer-1")).Accepted);

        var yielded = engine.State.RequireById(TestWorld.VarkId);
        Assert.Equal(4, yielded.Fear);
        Assert.False(yielded.IsScared);
        Assert.DoesNotContain(engine.State.StatusesOn(TestWorld.VarkId), s => s.Kind == StatusEffectKind.Scared);
    }

    [Fact]
    public void A_character_who_dies_keeps_the_fear_they_died_with()
    {
        var skrit = TestWorld.SkritV07(health: 3) with { Fear = 4 };
        var engine = Engine(new ScriptedRng(ScriptedRng.Hits, Solid), TestWorld.RowanV07(), skrit);

        engine.Execute(new AttackCharacterAction("Rowan", "Skrit", "Longsword"));

        var fallen = engine.State.RequireById(TestWorld.SkritId);
        Assert.Equal(CharacterDisposition.Dead, fallen.Disposition);
        Assert.Equal(4, fallen.Fear);
    }
}
