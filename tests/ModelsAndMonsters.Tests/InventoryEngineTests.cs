using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.6 inventory-transfer engine — give, drop and steal — tested as ordinary deterministic software
/// with no model involvement. Covers the ownership invariant (an item is always in exactly one place),
/// atomic transfer and rollback, the single seeded theft draw, and the guarantee that give and drop never
/// touch the RNG and that a rejected transfer draws nothing at all.
/// </summary>
public sealed class InventoryEngineTests
{
    // Default to an RNG that throws on the first draw, so any accidental roll during a give/drop, or during a
    // rejected steal, fails the test loudly rather than silently advancing the generator.
    private static GameEngine EngineWith(GameState state, IRng? rng = null) =>
        new(state, rng ?? new ScriptedRng(), new CombatRules(GlancingBlowChance: 0, BaseStealChance: 40));

    private static Character Giver(params InventoryItem[] inventory) => TestWorld.Rowan() with { Inventory = [.. inventory] };
    private static Character Receiver() => TestWorld.Elara(health: 6) with { Inventory = [] };
    private static Character Enemy(params InventoryItem[] inventory) => TestWorld.Vark() with { Inventory = [.. inventory] };

    private static InventoryItem Rope() => TestWorld.Rope();

    // ------------------------------------------------------------------------------------------
    // give_item
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Giving_moves_the_item_atomically_from_giver_to_recipient_and_bumps_the_version()
    {
        var engine = EngineWith(TestWorld.State(Giver(Rope()), Receiver()));

        var result = engine.Execute(new GiveItemAction(TestWorld.RowanId, TestWorld.ElaraId, "Rope"));

        Assert.True(result.Accepted);
        var give = Assert.IsType<GiveItemOutcome>(result.Outcome);
        Assert.Equal("rope", give.ItemId);

        // The item now exists in exactly one inventory: the recipient's, not the giver's.
        Assert.Empty(engine.State.RequireById(TestWorld.RowanId).Inventory);
        var carried = Assert.Single(engine.State.RequireById(TestWorld.ElaraId).Inventory);
        Assert.Equal("rope", carried.Id);
        Assert.Equal(1, engine.State.Version);
    }

    [Fact]
    public void Giving_requires_the_giver_to_currently_own_the_item()
    {
        // Rowan carries nothing; the Rope is in Elara's inventory, not his.
        var engine = EngineWith(TestWorld.State(Giver(), Receiver() with { Inventory = [Rope()] }));

        var result = engine.Execute(new GiveItemAction(TestWorld.RowanId, TestWorld.ElaraId, "Rope"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ItemNotPossessed, result.RejectionReason);
        Assert.Equal(0, engine.State.Version);
    }

    [Fact]
    public void Giving_to_a_recipient_who_is_not_present_is_rejected_and_changes_nothing()
    {
        var deadRecipient = TestWorld.Elara(health: 0);
        var engine = EngineWith(TestWorld.State(Giver(Rope()), deadRecipient));

        var result = engine.Execute(new GiveItemAction(TestWorld.RowanId, TestWorld.ElaraId, "Rope"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.RecipientNotPresent, result.RejectionReason);
        Assert.Single(engine.State.RequireById(TestWorld.RowanId).Inventory);
        Assert.Equal(0, engine.State.Version);
    }

    [Fact]
    public void Giving_to_an_escaped_recipient_is_rejected()
    {
        var escaped = TestWorld.Elara(health: 6) with { Disposition = CharacterDisposition.Escaped, EscapedThroughExitId = "door" };
        var engine = EngineWith(TestWorld.State(Giver(Rope()), escaped));

        var result = engine.Execute(new GiveItemAction(TestWorld.RowanId, TestWorld.ElaraId, "Rope"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.RecipientNotPresent, result.RejectionReason);
    }

    [Fact]
    public void Giving_an_equipped_weapon_is_rejected()
    {
        var engine = EngineWith(TestWorld.State(Giver(Rope()), Receiver()));

        var result = engine.Execute(new GiveItemAction(TestWorld.RowanId, TestWorld.ElaraId, "Longsword"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.EquippedWeaponCannotBeTransferred, result.RejectionReason);
        // The weapon is untouched and no item moved.
        Assert.Equal("Longsword", engine.State.RequireById(TestWorld.RowanId).Weapon!.Name);
        Assert.Single(engine.State.RequireById(TestWorld.RowanId).Inventory);
    }

    [Fact]
    public void Giving_draws_no_randomness()
    {
        // The default throwing RNG guarantees no roll is taken.
        var engine = EngineWith(TestWorld.State(Giver(Rope()), Receiver()));
        var result = engine.Execute(new GiveItemAction(TestWorld.RowanId, TestWorld.ElaraId, "Rope"));

        Assert.True(result.Accepted);
        Assert.Empty(result.RngDraws);
    }

    // ------------------------------------------------------------------------------------------
    // present_item
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Presenting_an_item_is_accepted_without_transferring_it()
    {
        var engine = EngineWith(TestWorld.State(Giver(Rope()), Receiver()));

        var result = engine.Execute(new PresentItemAction(TestWorld.RowanId, TestWorld.ElaraId, "Rope"));

        Assert.True(result.Accepted);
        var presented = Assert.IsType<PresentItemOutcome>(result.Outcome);
        Assert.Equal("rope", presented.ItemId);
        Assert.Contains(engine.State.RequireById(TestWorld.RowanId).Inventory, item => item.Id == "rope");
        Assert.Empty(engine.State.RequireById(TestWorld.ElaraId).Inventory);
        Assert.Equal(0, engine.State.Version);
    }

    [Fact]
    public void Presenting_an_item_the_actor_does_not_carry_is_rejected()
    {
        var engine = EngineWith(TestWorld.State(Giver(), Receiver()));

        var result = engine.Execute(new PresentItemAction(TestWorld.RowanId, TestWorld.ElaraId, "Rope"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ItemNotPossessed, result.RejectionReason);
    }

    // ------------------------------------------------------------------------------------------
    // drop_item
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Dropping_moves_the_item_to_the_ground_keeping_its_stable_id()
    {
        var engine = EngineWith(TestWorld.State(Giver(Rope()), Receiver()));

        var result = engine.Execute(new DropItemAction(TestWorld.RowanId, "Rope"));

        Assert.True(result.Accepted);
        Assert.Empty(result.RngDraws);
        Assert.Empty(engine.State.RequireById(TestWorld.RowanId).Inventory);

        var ground = engine.State.Room.Objects.OfType<Container>().Single(c => c.IsGround);
        Assert.Equal(Container.GroundId, ground.Id);
        Assert.True(ground.IsOpen);
        var onFloor = Assert.Single(ground.Contents);
        Assert.Equal("rope", onFloor.Id); // same stable id as before the drop
    }

    [Fact]
    public void A_dropped_item_can_afterwards_be_taken_from_the_floor()
    {
        var engine = EngineWith(TestWorld.State(Giver(Rope()), Receiver()));

        Assert.True(engine.Execute(new DropItemAction(TestWorld.RowanId, "Rope")).Accepted);

        // Elara takes it from the floor through the ordinary take_item interaction.
        var take = engine.Execute(new TakeItemAction(TestWorld.ElaraId, Container.GroundId, "Rope"));

        Assert.True(take.Accepted);
        var carried = Assert.Single(engine.State.RequireById(TestWorld.ElaraId).Inventory);
        Assert.Equal("rope", carried.Id);
        Assert.Empty(engine.State.Room.Objects.OfType<Container>().Single(c => c.IsGround).Contents);
    }

    [Fact]
    public void Dropping_a_second_item_reuses_the_same_ground_container()
    {
        var engine = EngineWith(TestWorld.State(Giver(Rope(), TestWorld.HealingPotion()), Receiver()));

        Assert.True(engine.Execute(new DropItemAction(TestWorld.RowanId, "Rope")).Accepted);
        Assert.True(engine.Execute(new DropItemAction(TestWorld.RowanId, "Small Healing Potion")).Accepted);

        var grounds = engine.State.Room.Objects.OfType<Container>().Where(c => c.IsGround).ToList();
        Assert.Single(grounds);
        Assert.Equal(2, grounds[0].Contents.Length);
    }

    [Fact]
    public void Dropping_an_equipped_weapon_is_rejected()
    {
        var engine = EngineWith(TestWorld.State(Giver(Rope()), Receiver()));

        var result = engine.Execute(new DropItemAction(TestWorld.RowanId, "Longsword"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.EquippedWeaponCannotBeTransferred, result.RejectionReason);
    }

    // ------------------------------------------------------------------------------------------
    // steal_item
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_theft_that_rolls_at_or_under_the_chance_succeeds_and_moves_the_item()
    {
        // Base chance 40; a roll of 40 lands (roll <= chance).
        var engine = EngineWith(TestWorld.State(TestWorld.Rowan(), Enemy(Rope())), new ScriptedRng(40));

        var result = engine.Execute(new StealItemAction(TestWorld.RowanId, TestWorld.VarkId, "Rope"));

        Assert.True(result.Accepted);
        var steal = Assert.IsType<StealItemOutcome>(result.Outcome);
        Assert.True(steal.Succeeded);
        Assert.Equal(40, steal.BaseChance);
        Assert.Equal(40, steal.EffectiveChance);
        Assert.Equal(40, steal.Roll);

        Assert.Empty(engine.State.RequireById(TestWorld.VarkId).Inventory);
        var stolen = Assert.Single(engine.State.RequireById(TestWorld.RowanId).Inventory);
        Assert.Equal("rope", stolen.Id);
        Assert.Equal(1, engine.State.Version);
    }

    [Fact]
    public void A_theft_that_rolls_over_the_chance_fails_and_moves_nothing_but_still_spends_the_turn()
    {
        var engine = EngineWith(TestWorld.State(TestWorld.Rowan(), Enemy(Rope())), new ScriptedRng(41));

        var result = engine.Execute(new StealItemAction(TestWorld.RowanId, TestWorld.VarkId, "Rope"));

        Assert.True(result.Accepted); // the turn is spent whether or not the theft lands
        var steal = Assert.IsType<StealItemOutcome>(result.Outcome);
        Assert.False(steal.Succeeded);

        // Ownership is unchanged, and — like a missed attack — the version is untouched.
        Assert.Single(engine.State.RequireById(TestWorld.VarkId).Inventory);
        Assert.Empty(engine.State.RequireById(TestWorld.RowanId).Inventory);
        Assert.Equal(0, engine.State.Version);
    }

    [Fact]
    public void A_theft_uses_exactly_one_draw()
    {
        // A single-value ScriptedRng: a second draw would throw. The theft succeeding proves exactly one draw.
        var engine = EngineWith(TestWorld.State(TestWorld.Rowan(), Enemy(Rope())), new ScriptedRng(1));

        var result = engine.Execute(new StealItemAction(TestWorld.RowanId, TestWorld.VarkId, "Rope"));

        Assert.True(result.Accepted);
        Assert.Single(result.RngDraws);
    }

    [Fact]
    public void The_same_seed_and_inputs_produce_the_same_theft_result()
    {
        StealItemOutcome Run() =>
            (StealItemOutcome)EngineWith(TestWorld.State(TestWorld.Rowan(), Enemy(Rope())), new SeededRng(12345))
                .Execute(new StealItemAction(TestWorld.RowanId, TestWorld.VarkId, "Rope")).Outcome!;

        var a = Run();
        var b = Run();
        Assert.Equal(a.Roll, b.Roll);
        Assert.Equal(a.Succeeded, b.Succeeded);
    }

    [Fact]
    public void A_theft_draw_carries_the_full_reproducible_trace_fields()
    {
        var engine = EngineWith(TestWorld.State(TestWorld.Rowan(), Enemy(Rope())), new SeededRng(7));

        var result = engine.Execute(new StealItemAction(TestWorld.RowanId, TestWorld.VarkId, "Rope"));
        var draw = Assert.Single(result.RngDraws);

        Assert.Equal("steal.attempt", draw.Purpose);
        Assert.Equal("steal_item", draw.ActionType);
        Assert.Equal(TestWorld.RowanId, draw.ActorId);
        Assert.Equal(TestWorld.VarkId, draw.TargetId);
        Assert.Equal(40, draw.BaseChance);
        Assert.Equal(40, draw.Threshold);
        Assert.NotNull(draw.Modifiers);
        Assert.InRange(draw.RawRoll, 1, 100);
        Assert.Equal(7, draw.Seed);
        Assert.Equal(draw.SequenceBefore + 1, draw.SequenceAfter);
        Assert.Contains(draw.Result, new[] { "stolen", "failed" });
    }

    [Fact]
    public void Stealing_an_item_the_target_does_not_carry_is_rejected_and_draws_nothing()
    {
        // The throwing RNG proves no draw is taken on a validation failure.
        var engine = EngineWith(TestWorld.State(TestWorld.Rowan(), Enemy()));

        var result = engine.Execute(new StealItemAction(TestWorld.RowanId, TestWorld.VarkId, "Rope"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ItemNotPossessed, result.RejectionReason);
        Assert.Empty(result.RngDraws);
        Assert.Equal(0, engine.State.Version);
    }

    [Fact]
    public void Stealing_an_equipped_weapon_is_rejected_and_draws_nothing()
    {
        var engine = EngineWith(TestWorld.State(TestWorld.Rowan(), Enemy(Rope())));

        var result = engine.Execute(new StealItemAction(TestWorld.RowanId, TestWorld.VarkId, "Notched Sabre"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.EquippedWeaponCannotBeTransferred, result.RejectionReason);
        Assert.Empty(result.RngDraws);
    }

    [Fact]
    public void Stealing_from_a_surrendered_target_is_rejected_and_draws_nothing()
    {
        var surrendered = TestWorld.Vark() with { Disposition = CharacterDisposition.Surrendered, Inventory = [Rope()] };
        var engine = EngineWith(TestWorld.State(TestWorld.Rowan(), surrendered));

        var result = engine.Execute(new StealItemAction(TestWorld.RowanId, TestWorld.VarkId, "Rope"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.TargetHasSurrendered, result.RejectionReason);
        Assert.Empty(result.RngDraws);
    }

    [Fact]
    public void Stealing_from_a_dead_target_is_rejected()
    {
        var dead = TestWorld.Vark(health: 0) with { Inventory = [Rope()] };
        var engine = EngineWith(TestWorld.State(TestWorld.Rowan(), dead));

        var result = engine.Execute(new StealItemAction(TestWorld.RowanId, TestWorld.VarkId, "Rope"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.TargetIsDead, result.RejectionReason);
    }

    // ------------------------------------------------------------------------------------------
    // The ownership invariant: an item is always in exactly one place, and rollback is atomic.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_given_item_never_exists_in_two_inventories_at_once()
    {
        var engine = EngineWith(TestWorld.State(Giver(Rope()), Receiver()));
        engine.Execute(new GiveItemAction(TestWorld.RowanId, TestWorld.ElaraId, "Rope"));

        var everywhere = engine.State.Characters.SelectMany(c => c.Inventory)
            .Concat(engine.State.Room.Objects.OfType<Container>().SelectMany(c => c.Contents))
            .Count(i => i.Id == "rope");
        Assert.Equal(1, everywhere);
    }

    [Fact]
    public void A_rejected_transfer_is_atomic_leaving_both_inventories_and_the_version_untouched()
    {
        // A give to a dead recipient: the giver keeps the item, nobody gains it, the version does not move.
        var dead = TestWorld.Elara(health: 0);
        var engine = EngineWith(TestWorld.State(Giver(Rope()), dead));

        var before = engine.State;
        var result = engine.Execute(new GiveItemAction(TestWorld.RowanId, TestWorld.ElaraId, "Rope"));

        Assert.False(result.Accepted);
        Assert.Same(before, engine.State); // state reference is unchanged on a rejection
        Assert.Single(engine.State.RequireById(TestWorld.RowanId).Inventory);
        Assert.Empty(engine.State.RequireById(TestWorld.ElaraId).Inventory);
    }

    // ------------------------------------------------------------------------------------------
    // Loose weapons carried as trophies (v0.10) — an ordinary item in every way but one: it is never
    // the weapon in anyone's hand, and cannot be fought with.
    // ------------------------------------------------------------------------------------------

    // A distinct weapon name of its own, so stealing or giving it is never mistaken for the giver's or
    // target's own EQUIPPED weapon (Rowan's Longsword, Vark's Notched Sabre) in these fixtures.
    private static InventoryItem TrophyDagger() => new Weapon("Rusty Dagger", 2).AsForfeitedItem();

    [Fact]
    public void A_carried_weapon_trophy_can_be_given_like_any_ordinary_item()
    {
        var engine = EngineWith(TestWorld.State(Giver(TrophyDagger()), Receiver()));

        var result = engine.Execute(new GiveItemAction(TestWorld.RowanId, TestWorld.ElaraId, "Rusty Dagger"));

        Assert.True(result.Accepted);
        Assert.DoesNotContain(engine.State.RequireById(TestWorld.RowanId).Inventory, i => i.Name == "Rusty Dagger");
        var received = Assert.Single(engine.State.RequireById(TestWorld.ElaraId).Inventory);
        Assert.True(received.IsWeaponTrophy);
        // Elara's own equipped weapon is unaffected — the trophy is a carried item, never a swap.
        Assert.Equal("Iron Mace", engine.State.RequireById(TestWorld.ElaraId).Weapon!.Name);
    }

    [Fact]
    public void A_carried_weapon_trophy_can_be_dropped_like_any_ordinary_item()
    {
        var engine = EngineWith(TestWorld.State(Giver(TrophyDagger())));

        var result = engine.Execute(new DropItemAction(TestWorld.RowanId, "Rusty Dagger"));

        Assert.True(result.Accepted);
        Assert.Empty(engine.State.RequireById(TestWorld.RowanId).Inventory);
        var ground = Assert.Single(engine.State.Room.Objects.OfType<Container>(), c => c.IsGround);
        var dropped = Assert.Single(ground.Contents);
        Assert.True(dropped.IsWeaponTrophy);
        Assert.Equal("Rusty Dagger", dropped.Name);
    }

    [Fact]
    public void A_carried_weapon_trophy_can_be_stolen_like_any_ordinary_item()
    {
        var engine = EngineWith(TestWorld.State(TestWorld.Rowan(), Enemy(TrophyDagger())), new ScriptedRng(1));

        var result = engine.Execute(new StealItemAction(TestWorld.RowanId, TestWorld.VarkId, "Rusty Dagger"));

        Assert.True(result.Accepted);
        Assert.DoesNotContain(engine.State.RequireById(TestWorld.VarkId).Inventory, i => i.Name == "Rusty Dagger");
        Assert.Contains(engine.State.RequireById(TestWorld.RowanId).Inventory, i => i.IsWeaponTrophy && i.Name == "Rusty Dagger");
        // Vark keeps the Notched Sabre in his hand throughout: only the loose trophy in his pack moved.
        Assert.Equal("Notched Sabre", engine.State.RequireById(TestWorld.VarkId).Weapon!.Name);
    }

    [Fact]
    public void Attacking_with_a_carried_weapon_trophy_is_rejected()
    {
        // The trophy sits in Rowan's inventory, but his equipped weapon is still the Longsword: naming the
        // trophy as the attack's weapon must be refused exactly as naming any weapon he does not hold would be.
        var engine = EngineWith(TestWorld.State(Giver(TrophyDagger()), Enemy()));

        var result = engine.Execute(new AttackCharacterAction(TestWorld.RowanId, TestWorld.VarkId, "Rusty Dagger"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.WeaponNotPossessed, result.RejectionReason);
        Assert.Equal("Longsword", engine.State.RequireById(TestWorld.RowanId).Weapon!.Name);
    }
}
