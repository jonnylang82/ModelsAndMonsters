using System.Collections.Immutable;
using System.Text.Json;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.9 environmental cover vertical slice at the engine level: seeding, taking and leaving cover,
/// exclusive capacity, the three-way outcome of an attack against a covered character (direct hit, cover
/// interception, ordinary miss) at exact boundaries, durability and destruction, deliberate object damage,
/// and every way cover ends — voluntarily, as a side effect of an exposing action, by destruction, or by the
/// occupant leaving active play. These are the mechanical guarantees the v0.9 spec asks for: deterministic,
/// no live model involved, and independent of whether any particular run's characters choose to use cover.
/// </summary>
public sealed class CoverEngineTests
{
    private static GameEngine Engine(IRng? rng = null, CombatRules? rules = null, params Character[] characters) =>
        TestWorld.EngineWithCover(rng: rng, rules: rules, characters: characters);

    private static GameState StateWithExitAndCover(EncounterExit exit, WorldObject cover, params Character[] characters) => new()
    {
        Room = new Room("flooded-cellar", "Flooded Cellar", "A cramped, flooded cellar.", ImmutableArray<string>.Empty)
        {
            Exits = [exit],
            Objects = [cover]
        },
        Characters = [.. characters],
        Version = 0
    };

    // ------------------------------------------------------------------------------------------
    // Environmental state
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_scenario_seeds_one_valid_cover_object()
    {
        var scenario = TestWorld.TwoVsTwoScenario();
        scenario.Room.Cover =
        [
            new CoverDefinition
            {
                Id = TestWorld.WorkbenchId,
                Name = "Overturned Mill Workbench",
                Description = "A heavy mill workbench.",
                Capacity = 1,
                HitChanceModifier = -20,
                MaximumDurability = 2,
                Armour = 2
            }
        ];

        var state = ScenarioFactory.CreateInitialState(scenario);

        var cover = Assert.Single(state.Room.Objects.OfType<CoverObject>());
        Assert.Equal(TestWorld.WorkbenchId, cover.Id);
        Assert.Equal(1, cover.Capacity);
        Assert.Equal(-20, cover.HitChanceModifier);
        Assert.Equal(2, cover.MaximumDurability);
        Assert.Equal(2, cover.CurrentDurability);
        Assert.Equal(2, cover.Armour);
        Assert.Null(cover.CurrentOccupantId);
        Assert.Equal(EnvironmentalObjectState.Intact, cover.State);
        Assert.True(cover.ProvidesCover);
        Assert.True(cover.CanProvideCover);
        Assert.True(cover.HasSpareCapacity);
    }

    [Fact]
    public void A_scenario_with_no_cover_still_builds_and_reads_normally()
    {
        var state = ScenarioFactory.CreateInitialState(TestWorld.TwoVsTwoScenario());

        Assert.Empty(state.Room.Objects.OfType<CoverObject>());
        // Older-shaped state (no cover at all) resolves an attack exactly as before v0.9.
        var engine = new GameEngine(state, new SeededRng(1), CombatRules.NoGlancing);
        var result = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));
        Assert.True(result.Accepted);
        Assert.Null(((AttackOutcome)result.Outcome!).CoverId);
    }

    [Fact]
    public void Cover_serialises_into_state_with_a_type_discriminator_and_survives_a_round_trip()
    {
        var state = TestWorld.TwoVsTwoStateWithCover(TestWorld.Workbench(currentDurability: 1, occupantId: TestWorld.RowanId));

        var json = JsonSerializer.Serialize(state, TraceJson.Indented);
        Assert.Contains("\"$type\": \"cover\"", json, StringComparison.Ordinal);

        var roundTripped = JsonSerializer.Deserialize<GameState>(json, TraceJson.Indented);
        var cover = Assert.Single(roundTripped!.Room.Objects.OfType<CoverObject>());
        Assert.Equal(TestWorld.WorkbenchId, cover.Id);
        Assert.Equal(1, cover.CurrentDurability);
        Assert.Equal(TestWorld.RowanId, cover.CurrentOccupantId);
        Assert.Equal(EnvironmentalObjectState.Damaged, cover.State);
    }

    [Fact]
    public void Destroyed_cover_remains_present_in_the_room_rather_than_being_removed()
    {
        var engine = Engine(rng: new SeededRng(1), rules: CombatRules.NoGlancing,
            characters: [TestWorld.Rowan(), TestWorld.Vark()]);

        // Destroy it deliberately (2 durability, longsword damage 5 minus armour 2 = 3 > 2, one blow suffices).
        var result = engine.Execute(new DamageEnvironmentalObjectAction("Rowan", TestWorld.WorkbenchId));

        Assert.True(result.Accepted);
        var cover = engine.State.Room.Objects.OfType<CoverObject>().Single();
        Assert.Equal(EnvironmentalObjectState.Destroyed, cover.State);
        Assert.Equal(0, cover.CurrentDurability);
    }

    // ------------------------------------------------------------------------------------------
    // Taking cover
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Taking_cover_applies_InCover_records_occupancy_and_draws_no_dice()
    {
        var engine = Engine(characters: [TestWorld.Rowan(), TestWorld.Vark()]);
        var before = engine.State.Version;

        var result = engine.Execute(new TakeCoverAction("Rowan", TestWorld.WorkbenchId));

        Assert.True(result.Accepted);
        Assert.Empty(result.RngDraws);
        var outcome = Assert.IsType<TakeCoverOutcome>(result.Outcome);
        Assert.Equal(TestWorld.WorkbenchId, outcome.CoverId);

        var status = engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.InCover);
        Assert.NotNull(status);
        Assert.Equal(StatusExpiryRule.WhileConditionHolds, status!.ExpiryRule);
        Assert.Equal(TestWorld.WorkbenchId, status.RelatedObjectId);

        var cover = engine.State.Room.Objects.OfType<CoverObject>().Single();
        Assert.Equal(TestWorld.RowanId, cover.CurrentOccupantId);
        Assert.Equal(TestWorld.RowanId, engine.State.CoverOccupiedBy(TestWorld.RowanId)?.CurrentOccupantId);
        Assert.True(engine.State.Version > before);
    }

    [Fact]
    public void A_second_character_is_refused_when_capacity_one_cover_is_full()
    {
        var engine = Engine(characters: [TestWorld.Rowan(), TestWorld.Elara(), TestWorld.Vark()]);
        Assert.True(engine.Execute(new TakeCoverAction("Rowan", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new TakeCoverAction("Elara", TestWorld.WorkbenchId));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.CoverFull, result.RejectionReason);
        // Rowan's occupancy is untouched by the refused attempt.
        Assert.Equal(TestWorld.RowanId, engine.State.Room.Objects.OfType<CoverObject>().Single().CurrentOccupantId);
    }

    [Fact]
    public void Destroyed_cover_cannot_be_occupied()
    {
        var engine = Engine(rng: new SeededRng(1), rules: CombatRules.NoGlancing,
            characters: [TestWorld.Rowan(), TestWorld.Vark()]);
        Assert.True(engine.Execute(new DamageEnvironmentalObjectAction("Rowan", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new TakeCoverAction("Vark", TestWorld.WorkbenchId));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.CoverCannotBeUsed, result.RejectionReason);
    }

    [Fact]
    public void A_non_cover_object_cannot_be_occupied()
    {
        var engine = new GameEngine(
            TestWorld.StateWith([TestWorld.Chest()], TestWorld.Rowan()), new SeededRng(1), CombatRules.NoGlancing);

        var result = engine.Execute(new TakeCoverAction("Rowan", TestWorld.ChestId));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.UnknownCover, result.RejectionReason);
    }

    [Fact]
    public void An_actor_already_behind_the_named_cover_is_refused()
    {
        var engine = Engine(characters: [TestWorld.Rowan()]);
        Assert.True(engine.Execute(new TakeCoverAction("Rowan", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new TakeCoverAction("Rowan", TestWorld.WorkbenchId));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.AlreadyInThatCover, result.RejectionReason);
    }

    [Fact]
    public void An_ambiguous_cover_reference_is_refused_rather_than_guessed()
    {
        var barrelA = TestWorld.Workbench() with { Id = "barrel-a", Name = "Barrel" };
        var barrelB = TestWorld.Workbench() with { Id = "barrel-b", Name = "Barrel" };
        var engine = new GameEngine(
            TestWorld.StateWith([barrelA, barrelB], TestWorld.Rowan()), new SeededRng(1), CombatRules.NoGlancing);

        var result = engine.Execute(new TakeCoverAction("Rowan", "Barrel"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.CoverReferenceAmbiguous, result.RejectionReason);
    }

    [Fact]
    public void Unavailable_cover_gives_no_take_cover_affordance()
    {
        var full = TestWorld.Workbench(occupantId: TestWorld.SkritId);
        Assert.False(full.HasSpareCapacity);
        var destroyed = TestWorld.Workbench(currentDurability: 0);
        Assert.False(destroyed.CanProvideCover);
        Assert.False(destroyed.HasSpareCapacity);
    }

    // ------------------------------------------------------------------------------------------
    // Leaving cover, and exposure as a side effect
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Explicit_leave_releases_occupancy_and_draws_no_dice()
    {
        var engine = Engine(characters: [TestWorld.Rowan()]);
        Assert.True(engine.Execute(new TakeCoverAction("Rowan", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new LeaveCoverAction("Rowan"));

        Assert.True(result.Accepted);
        Assert.Empty(result.RngDraws);
        Assert.IsType<LeaveCoverOutcome>(result.Outcome);
        Assert.Null(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.InCover));
        Assert.Null(engine.State.Room.Objects.OfType<CoverObject>().Single().CurrentOccupantId);
    }

    [Fact]
    public void Leaving_cover_while_occupying_none_is_refused()
    {
        var engine = Engine(characters: [TestWorld.Rowan()]);

        var result = engine.Execute(new LeaveCoverAction("Rowan"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.NotInCover, result.RejectionReason);
    }

    [Fact]
    public void An_accepted_attack_releases_the_attackers_own_cover_before_resolving()
    {
        var engine = Engine(rng: new SeededRng(1), rules: CombatRules.NoGlancing,
            characters: [TestWorld.Rowan(), TestWorld.Vark()]);
        Assert.True(engine.Execute(new TakeCoverAction("Rowan", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));

        Assert.True(result.Accepted);
        Assert.Null(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.InCover));
        Assert.Null(engine.State.Room.Objects.OfType<CoverObject>().Single().CurrentOccupantId);
        // Exposure adds no draw of its own: still exactly the ordinary hit + quality draws.
        Assert.Equal(2, result.RngDraws.Count);
    }

    [Fact]
    public void A_rejected_action_does_not_release_cover()
    {
        var engine = Engine(characters: [TestWorld.Rowan()]);
        Assert.True(engine.Execute(new TakeCoverAction("Rowan", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Rowan", "Longsword"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.TargetIsSelf, result.RejectionReason);
        Assert.NotNull(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.InCover));
        Assert.Equal(TestWorld.RowanId, engine.State.Room.Objects.OfType<CoverObject>().Single().CurrentOccupantId);
    }

    [Fact]
    public void Defending_and_using_an_item_on_oneself_do_not_break_cover()
    {
        var engine = Engine(characters: [TestWorld.Elara(health: 6, inventory: [TestWorld.HealingPotion()])]);
        Assert.True(engine.Execute(new TakeCoverAction("Elara", TestWorld.WorkbenchId)).Accepted);

        Assert.True(engine.Execute(new UseAbilityAction("Elara", AbilityCatalog.DefendId)).Accepted);
        Assert.NotNull(engine.State.StatusOn(TestWorld.ElaraId, StatusEffectKind.InCover));

        Assert.True(engine.Execute(new UseItemAction("Elara", "Small Healing Potion")).Accepted);
        Assert.NotNull(engine.State.StatusOn(TestWorld.ElaraId, StatusEffectKind.InCover));
        Assert.Equal(TestWorld.ElaraId, engine.State.Room.Objects.OfType<CoverObject>().Single().CurrentOccupantId);
    }

    [Fact]
    public void Guard_Ally_breaks_the_guardians_own_cover()
    {
        var engine = Engine(characters:
            [TestWorld.WithAbilities(TestWorld.Rowan(), AbilityCatalog.GuardAllyId), TestWorld.Elara()]);
        Assert.True(engine.Execute(new TakeCoverAction("Rowan", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara"));

        Assert.True(result.Accepted);
        Assert.Null(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.InCover));
        Assert.Null(engine.State.Room.Objects.OfType<CoverObject>().Single().CurrentOccupantId);
        // The guard relationship itself still stands.
        Assert.NotNull(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.Guarding));
    }

    [Fact]
    public void Healing_Prayer_on_oneself_does_not_break_cover()
    {
        var engine = Engine(characters:
            [TestWorld.WithAbilities(TestWorld.Elara(health: 6), AbilityCatalog.HealingPrayerId), TestWorld.Rowan(health: 10)]);
        Assert.True(engine.Execute(new TakeCoverAction("Elara", TestWorld.WorkbenchId)).Accepted);

        Assert.True(engine.Execute(new UseAbilityAction("Elara", AbilityCatalog.HealingPrayerId, "Elara")).Accepted);

        Assert.NotNull(engine.State.StatusOn(TestWorld.ElaraId, StatusEffectKind.InCover));
    }

    [Fact]
    public void Healing_Prayer_on_an_ally_breaks_the_casters_cover()
    {
        var engine = Engine(characters:
            [TestWorld.WithAbilities(TestWorld.Elara(), AbilityCatalog.HealingPrayerId), TestWorld.Rowan(health: 10)]);
        Assert.True(engine.Execute(new TakeCoverAction("Elara", TestWorld.WorkbenchId)).Accepted);

        Assert.True(engine.Execute(new UseAbilityAction("Elara", AbilityCatalog.HealingPrayerId, "Rowan")).Accepted);

        Assert.Null(engine.State.StatusOn(TestWorld.ElaraId, StatusEffectKind.InCover));
        Assert.Null(engine.State.Room.Objects.OfType<CoverObject>().Single().CurrentOccupantId);
    }

    [Fact]
    public void Death_releases_cover()
    {
        var engine = Engine(rng: new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid), rules: CombatRules.NoGlancing,
            characters: [TestWorld.Elara(health: 1), TestWorld.Vark()]);
        Assert.True(engine.Execute(new TakeCoverAction("Elara", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new AttackCharacterAction("Vark", "Elara", "Notched Sabre"));

        Assert.True(result.Accepted);
        Assert.True(((AttackOutcome)result.Outcome!).TargetDied);
        Assert.Null(engine.State.Room.Objects.OfType<CoverObject>().Single().CurrentOccupantId);
    }

    [Fact]
    public void Surrender_releases_the_offerers_cover()
    {
        var rowan = TestWorld.Rowan() with { Inventory = [TestWorld.Purse("rowan")] };
        var engine = Engine(characters: [rowan, TestWorld.Vark()]);
        Assert.True(engine.Execute(new TakeCoverAction("Rowan", TestWorld.WorkbenchId)).Accepted);

        var offered = engine.Execute(new OfferSurrenderAction("Rowan", "Vark", ["Small Purse of Gold Coins"], ForfeitWeapon: false));
        Assert.True(offered.Accepted);
        var offerId = ((OfferSurrenderOutcome)offered.Outcome!).OfferId;

        var accepted = engine.Execute(new AcceptSurrenderAction("Vark", offerId));

        Assert.True(accepted.Accepted);
        Assert.Null(engine.State.Room.Objects.OfType<CoverObject>().Single().CurrentOccupantId);
    }

    [Fact]
    public void Accepting_someone_elses_surrender_does_not_release_the_accepters_own_cover()
    {
        var vark = TestWorld.Vark() with { Inventory = [TestWorld.Purse("vark")] };
        var engine = Engine(characters: [TestWorld.Rowan(), vark]);
        Assert.True(engine.Execute(new TakeCoverAction("Rowan", TestWorld.WorkbenchId)).Accepted);

        var offered = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["Small Purse of Gold Coins"], ForfeitWeapon: false));
        Assert.True(offered.Accepted);
        var offerId = ((OfferSurrenderOutcome)offered.Outcome!).OfferId;

        var accepted = engine.Execute(new AcceptSurrenderAction("Rowan", offerId));

        Assert.True(accepted.Accepted);
        Assert.NotNull(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.InCover));
        Assert.Equal(TestWorld.RowanId, engine.State.Room.Objects.OfType<CoverObject>().Single().CurrentOccupantId);
    }

    [Fact]
    public void Escape_releases_cover()
    {
        var state = StateWithExitAndCover(TestWorld.StairDoor(open: true), TestWorld.Workbench(),
            TestWorld.Skrit(), TestWorld.Rowan());
        var engine = new GameEngine(state, new SeededRng(1), CombatRules.NoGlancing);
        Assert.True(engine.Execute(new TakeCoverAction("Skrit", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new EscapeEncounterAction("Skrit", TestWorld.StairDoorId));

        Assert.True(result.Accepted);
        Assert.Null(engine.State.Room.Objects.OfType<CoverObject>().Single().CurrentOccupantId);
    }

    // ------------------------------------------------------------------------------------------
    // Covered attacks: exact boundaries
    // ------------------------------------------------------------------------------------------
    //
    // Attacker hit chance 70, cover modifier -20: pre-cover chance 70, covered chance 50.
    //   roll <= 50            -> direct hit
    //   50 < roll <= 70        -> cover interception
    //   roll > 70              -> ordinary miss

    private GameEngine CoveredAttackEngine(IRng rng, int attackerHitChance = 70, CombatRules? rules = null)
    {
        var target = TestWorld.Elara(hitChance: 100);
        var engine = Engine(rng, rules ?? CombatRules.NoGlancing, TestWorld.Rowan(hitChance: attackerHitChance), target);
        Assert.True(engine.Execute(new TakeCoverAction("Elara", TestWorld.WorkbenchId)).Accepted);
        return engine;
    }

    [Fact]
    public void A_roll_at_the_covered_threshold_is_a_direct_hit()
    {
        var engine = CoveredAttackEngine(new ScriptedRng(50, ScriptedRng.Solid));

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Elara", "Longsword"));
        var attack = (AttackOutcome)result.Outcome!;

        Assert.True(attack.Hit);
        Assert.False(attack.InterceptedByCover);
        Assert.Equal(2, result.RngDraws.Count); // hit + quality
        Assert.Equal(70, attack.PreCoverHitChance);
        Assert.Equal(50, attack.HitChance);
        Assert.Equal(-20, attack.CoverHitChanceModifier);
        Assert.True(attack.TargetHealthAfter < attack.TargetHealthBefore);
        Assert.Equal(2, engine.State.Room.Objects.OfType<CoverObject>().Single().CurrentDurability); // untouched
    }

    [Fact]
    public void A_roll_one_past_the_covered_threshold_is_intercepted_not_a_direct_hit()
    {
        var engine = CoveredAttackEngine(new ScriptedRng(51));

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Elara", "Longsword"));
        var attack = (AttackOutcome)result.Outcome!;

        Assert.False(attack.Hit);
        Assert.True(attack.InterceptedByCover);
        Assert.Single(result.RngDraws); // hit-check only; no quality draw
        Assert.Equal(attack.TargetHealthBefore, attack.TargetHealthAfter);
        Assert.Equal(1, attack.CoverDurabilityAfter);
        Assert.Equal(2, attack.CoverDurabilityBefore);
        Assert.False(attack.CoverDestroyed);
    }

    [Fact]
    public void A_roll_at_the_pre_cover_threshold_is_still_intercepted()
    {
        var engine = CoveredAttackEngine(new ScriptedRng(70));

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Elara", "Longsword"));
        var attack = (AttackOutcome)result.Outcome!;

        Assert.True(attack.InterceptedByCover);
        Assert.Single(result.RngDraws);
    }

    [Fact]
    public void A_roll_one_past_the_pre_cover_threshold_is_an_ordinary_miss()
    {
        var engine = CoveredAttackEngine(new ScriptedRng(71));

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Elara", "Longsword"));
        var attack = (AttackOutcome)result.Outcome!;

        Assert.False(attack.Hit);
        Assert.False(attack.InterceptedByCover);
        Assert.Single(result.RngDraws); // hit-check only; no quality draw
        Assert.Equal(attack.TargetHealthBefore, attack.TargetHealthAfter);
        // The ordinary miss changed nothing about the cover.
        Assert.Equal(2, engine.State.Room.Objects.OfType<CoverObject>().Single().CurrentDurability);
        Assert.Equal(2, attack.CoverDurabilityBefore);
        Assert.Equal(2, attack.CoverDurabilityAfter);
    }

    [Fact]
    public void Modifier_order_applies_status_modifiers_before_cover_and_clamps_each_stage()
    {
        // Skrit (rallied +15) attacks Elara (in cover, -20). Base 70 + 15 = 85 pre-cover; 85 - 20 = 65 covered.
        var target = TestWorld.Elara(hitChance: 100);
        var attacker = TestWorld.Skrit(hitChance: 70);
        var rallySource = TestWorld.WithAbilities(TestWorld.Vark(), AbilityCatalog.RallyGruntId);
        var engine = Engine(new ScriptedRng(65, ScriptedRng.Solid), CombatRules.NoGlancing, attacker, target, rallySource);
        Assert.True(engine.Execute(new TakeCoverAction("Elara", TestWorld.WorkbenchId)).Accepted);
        Assert.True(engine.Execute(new UseAbilityAction("Vark", AbilityCatalog.RallyGruntId, "Skrit")).Accepted);

        var result = engine.Execute(new AttackCharacterAction("Skrit", "Elara", "Crude Spear"));
        var attack = (AttackOutcome)result.Outcome!;

        Assert.Equal(85, attack.PreCoverHitChance);
        Assert.Equal(65, attack.HitChance);
        Assert.True(attack.Hit); // roll 65 <= covered 65: a direct hit right at the boundary

        // Clamping: a hit chance that would exceed 100 pre-cover clamps at 100 before cover is applied.
        var highBase = TestWorld.Elara(hitChance: 100);
        var attacker2 = TestWorld.Skrit(hitChance: 95);
        var rallySource2 = TestWorld.WithAbilities(TestWorld.Vark(), AbilityCatalog.RallyGruntId);
        var engine2 = Engine(new ScriptedRng(90), CombatRules.NoGlancing, attacker2, highBase, rallySource2);
        Assert.True(engine2.Execute(new TakeCoverAction("Elara", TestWorld.WorkbenchId)).Accepted);
        Assert.True(engine2.Execute(new UseAbilityAction("Vark", AbilityCatalog.RallyGruntId, "Skrit")).Accepted);
        var result2 = engine2.Execute(new AttackCharacterAction("Skrit", "Elara", "Crude Spear"));
        var attack2 = (AttackOutcome)result2.Outcome!;
        Assert.Equal(100, attack2.PreCoverHitChance); // 95 + 15 = 110, clamped to 100
        Assert.Equal(80, attack2.HitChance); // 100 - 20
        Assert.True(attack2.InterceptedByCover); // roll 90 > 80 but <= 100
    }

    [Fact]
    public void A_covered_effective_chance_clamped_to_zero_is_always_intercepted_or_missed_never_a_direct_hit()
    {
        // Base 10, cover -20 -> covered chance clamps to 0. Every roll (1-100) exceeds 0, so no direct hit
        // is possible; whether it counts as intercepted depends only on the pre-cover threshold (10).
        var target = TestWorld.Elara(hitChance: 100);
        var attacker = TestWorld.Rowan(hitChance: 10);
        var engine = Engine(new ScriptedRng(5), CombatRules.NoGlancing, attacker, target);
        Assert.True(engine.Execute(new TakeCoverAction("Elara", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Elara", "Longsword"));
        var attack = (AttackOutcome)result.Outcome!;

        Assert.False(attack.Hit);
        Assert.Equal(0, attack.HitChance);
        Assert.Equal(10, attack.PreCoverHitChance);
        Assert.True(attack.InterceptedByCover); // 5 <= 10
    }

    [Fact]
    public void A_direct_hit_can_still_be_critical_and_a_covered_direct_hit_can_still_be_defended()
    {
        var target = TestWorld.Elara(hitChance: 100);
        var engine = Engine(new ScriptedRng(50, ScriptedRng.Critical), CombatRules.Default,
            TestWorld.Rowan(hitChance: 70), target);
        Assert.True(engine.Execute(new TakeCoverAction("Elara", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new AttackCharacterAction("Rowan", "Elara", "Longsword"));
        var attack = (AttackOutcome)result.Outcome!;

        Assert.True(attack.Hit);
        Assert.Equal(AttackQuality.Critical, attack.Quality);
        Assert.Equal((5 - 2) * 2, attack.DamageDealt); // longsword 5, Elara armour 2, doubled

        // Now with Elara defending: a direct hit against a covered, defending target still turns aside damage.
        var target2 = TestWorld.Elara(hitChance: 100);
        var engine2 = Engine(new ScriptedRng(50, ScriptedRng.Solid), CombatRules.NoGlancing,
            TestWorld.Rowan(hitChance: 70), target2);
        Assert.True(engine2.Execute(new TakeCoverAction("Elara", TestWorld.WorkbenchId)).Accepted);
        Assert.True(engine2.Execute(new UseAbilityAction("Elara", AbilityCatalog.DefendId)).Accepted);

        var result2 = engine2.Execute(new AttackCharacterAction("Rowan", "Elara", "Longsword"));
        var attack2 = (AttackOutcome)result2.Outcome!;
        Assert.True(attack2.Hit);
        Assert.Equal(1, attack2.DefendReduction);
        Assert.Equal((5 - 2) - 1, attack2.DamageDealt);
    }

    // ------------------------------------------------------------------------------------------
    // Durability and destruction from interception
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Two_interceptions_damage_then_destroy_two_durability_cover_and_expose_the_occupant()
    {
        var target = TestWorld.Elara(hitChance: 100);
        var engine = Engine(new ScriptedRng(60, 60), CombatRules.NoGlancing, TestWorld.Rowan(hitChance: 70), target);
        Assert.True(engine.Execute(new TakeCoverAction("Elara", TestWorld.WorkbenchId)).Accepted);

        var first = engine.Execute(new AttackCharacterAction("Rowan", "Elara", "Longsword"));
        var firstAttack = (AttackOutcome)first.Outcome!;
        Assert.True(firstAttack.InterceptedByCover);
        Assert.False(firstAttack.CoverDestroyed);
        Assert.Equal(1, firstAttack.CoverDurabilityAfter);
        Assert.Equal(EnvironmentalObjectState.Damaged, engine.State.Room.Objects.OfType<CoverObject>().Single().State);
        Assert.NotNull(engine.State.StatusOn(TestWorld.ElaraId, StatusEffectKind.InCover));

        var second = engine.Execute(new AttackCharacterAction("Rowan", "Elara", "Longsword"));
        var secondAttack = (AttackOutcome)second.Outcome!;
        Assert.True(secondAttack.InterceptedByCover);
        Assert.True(secondAttack.CoverDestroyed);
        Assert.Equal(0, secondAttack.CoverDurabilityAfter);

        var cover = engine.State.Room.Objects.OfType<CoverObject>().Single();
        Assert.Equal(EnvironmentalObjectState.Destroyed, cover.State);
        Assert.Null(cover.CurrentOccupantId);
        Assert.Null(engine.State.StatusOn(TestWorld.ElaraId, StatusEffectKind.InCover));
        Assert.False(cover.CanProvideCover);

        // Neither interception ever touched Elara's own health.
        Assert.Equal(target.Health, engine.State.RequireById(TestWorld.ElaraId).Health);
    }

    // ------------------------------------------------------------------------------------------
    // Deliberate object damage
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Deliberate_damage_uses_weapon_damage_minus_armour_with_a_floor_of_one_and_draws_no_dice()
    {
        var engine = Engine(characters: [TestWorld.Rowan(), TestWorld.Vark()]); // longsword 5, cover armour 2

        var result = engine.Execute(new DamageEnvironmentalObjectAction("Rowan", TestWorld.WorkbenchId));

        Assert.True(result.Accepted);
        Assert.Empty(result.RngDraws);
        var outcome = (DamageEnvironmentalObjectOutcome)result.Outcome!;
        Assert.Equal(3, outcome.DamageApplied); // 5 - 2
        Assert.Equal(2, outcome.DurabilityBefore);
        Assert.Equal(0, outcome.DurabilityAfter); // clamped at zero, not negative
        Assert.True(outcome.Destroyed);
    }

    [Fact]
    public void A_weak_weapon_still_deals_at_least_one_damage()
    {
        var weakling = TestWorld.Rowan() with { Weapon = new Weapon("Dagger", 1) };
        var engine = Engine(characters: [weakling]); // cover armour 2: 1 - 2 would be negative without the floor

        var result = engine.Execute(new DamageEnvironmentalObjectAction("Rowan", TestWorld.WorkbenchId));

        var outcome = (DamageEnvironmentalObjectOutcome)result.Outcome!;
        Assert.Equal(1, outcome.DamageApplied);
        Assert.Equal(1, outcome.DurabilityAfter);
        Assert.False(outcome.Destroyed);
    }

    [Fact]
    public void Deliberate_damage_requires_a_held_weapon()
    {
        var unarmed = TestWorld.Rowan() with { Weapon = null };
        var engine = Engine(characters: [unarmed]);

        var result = engine.Execute(new DamageEnvironmentalObjectAction("Rowan", TestWorld.WorkbenchId));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ActorHasNoWeapon, result.RejectionReason);
    }

    [Fact]
    public void Deliberate_damage_cannot_also_damage_an_occupant()
    {
        var occupant = TestWorld.Elara();
        var healthBefore = occupant.Health;
        var engine = Engine(characters: [TestWorld.Rowan(), occupant]);
        Assert.True(engine.Execute(new TakeCoverAction("Elara", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new DamageEnvironmentalObjectAction("Rowan", TestWorld.WorkbenchId));

        Assert.True(result.Accepted);
        var outcome = (DamageEnvironmentalObjectOutcome)result.Outcome!;
        Assert.True(outcome.Destroyed);
        Assert.Equal("Elara", outcome.ExposedOccupantName);
        Assert.Equal(healthBefore, engine.State.RequireById(TestWorld.ElaraId).Health);
    }

    [Fact]
    public void Deliberate_damage_breaks_the_actors_own_different_cover_first()
    {
        var barrel = TestWorld.Workbench() with { Id = "barrel", Name = "Barrel" };
        var engine = new GameEngine(
            TestWorld.StateWith([TestWorld.Workbench(), barrel], TestWorld.Rowan()),
            new SeededRng(1), CombatRules.NoGlancing);
        Assert.True(engine.Execute(new TakeCoverAction("Rowan", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new DamageEnvironmentalObjectAction("Rowan", "barrel"));

        Assert.True(result.Accepted);
        var outcome = (DamageEnvironmentalObjectOutcome)result.Outcome!;
        Assert.True(outcome.SelfCoverBroken);
        Assert.Null(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.InCover));
        Assert.Null(engine.State.Room.Objects.OfType<CoverObject>().First(c => c.Id == TestWorld.WorkbenchId).CurrentOccupantId);
    }

    [Fact]
    public void Deliberate_damage_rejects_an_already_destroyed_object()
    {
        var engine = Engine(characters: [TestWorld.Rowan()]);
        Assert.True(engine.Execute(new DamageEnvironmentalObjectAction("Rowan", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new DamageEnvironmentalObjectAction("Rowan", TestWorld.WorkbenchId));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ObjectAlreadyDestroyed, result.RejectionReason);
    }

    [Fact]
    public void Deliberate_damage_rejects_an_object_with_no_cover_capability()
    {
        var engine = new GameEngine(
            TestWorld.StateWith([TestWorld.Chest()], TestWorld.Rowan()), new SeededRng(1), CombatRules.NoGlancing);

        var result = engine.Execute(new DamageEnvironmentalObjectAction("Rowan", TestWorld.ChestId));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ObjectCannotBeDamaged, result.RejectionReason);
    }

    [Fact]
    public void Deliberate_damage_rejects_an_unknown_object()
    {
        var engine = Engine(characters: [TestWorld.Rowan()]);

        var result = engine.Execute(new DamageEnvironmentalObjectAction("Rowan", "a wall that does not exist"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.UnknownObject, result.RejectionReason);
    }

    [Fact]
    public void An_ambiguous_object_reference_for_damage_is_refused_rather_than_guessed()
    {
        var barrelA = TestWorld.Workbench() with { Id = "barrel-a", Name = "Barrel" };
        var barrelB = TestWorld.Workbench() with { Id = "barrel-b", Name = "Barrel" };
        var engine = new GameEngine(
            TestWorld.StateWith([barrelA, barrelB], TestWorld.Rowan()), new SeededRng(1), CombatRules.NoGlancing);

        var result = engine.Execute(new DamageEnvironmentalObjectAction("Rowan", "Barrel"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ObjectReferenceAmbiguous, result.RejectionReason);
    }

    [Fact]
    public void Deliberate_damage_rejects_striking_the_cover_the_actor_currently_occupies()
    {
        var engine = Engine(characters: [TestWorld.Rowan()]);
        Assert.True(engine.Execute(new TakeCoverAction("Rowan", TestWorld.WorkbenchId)).Accepted);

        var result = engine.Execute(new DamageEnvironmentalObjectAction("Rowan", TestWorld.WorkbenchId));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.CannotDamageOwnCover, result.RejectionReason);
        Assert.NotNull(engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.InCover));
    }

    // ------------------------------------------------------------------------------------------
    // Regression: existing combat mechanics are unaffected when no cover is involved
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void An_ordinary_uncovered_attack_is_unaffected_by_the_v0_9_changes()
    {
        var engine = TestWorld.Engine(new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid), CombatRules.NoGlancing,
            TestWorld.Hero(), TestWorld.Monster());

        var result = engine.Execute(new AttackCharacterAction(TestWorld.HeroId, TestWorld.MonsterId, "Iron Sword"));
        var attack = (AttackOutcome)result.Outcome!;

        Assert.True(result.Accepted);
        Assert.True(attack.Hit);
        Assert.Null(attack.CoverId);
        Assert.Null(attack.PreCoverHitChance);
        Assert.False(attack.InterceptedByCover);
        Assert.Equal(attack.HitChance, attack.BaseHitChance); // no modifier of any kind, exactly as pre-v0.9
    }
}
