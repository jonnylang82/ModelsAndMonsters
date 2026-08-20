using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// What happens when an item reference names more than one thing.
/// </summary>
/// <remarks>
/// Containers, objects and exits have always reported ambiguity so the engine could refuse it; a character's
/// own inventory did not, and silently returned whichever match sat first. A live run walked into the gap:
/// Vark carried two purses both displaying as "Small Purse of Gold Coins" — qualified "(the runt's)" and
/// "(the captain's)" — Rowan tried to steal "Vark's purse", and the Dungeon Master spent its entire output
/// budget deliberating in prose about which one was meant ("But which purse? The intent doesn't specify")
/// instead of calling a tool. It had seen the problem correctly. The engine simply offered it no way to say
/// so, and would have handed over a purse the reference did not single out.
/// </remarks>
public sealed class ItemAmbiguityTests
{
    private static GameEngine EngineWith(GameState state, IRng? rng = null) =>
        new(state, rng ?? new ScriptedRng(), new CombatRules(GlancingBlowChance: 0, BaseStealChance: 40));

    private static InventoryItem Purse(string id, string qualifier) =>
        new(id, "Small Purse of Gold Coins", "A purse.", null, qualifier);

    /// <summary>Two purses that read the same until qualified — the live configuration, near enough.</summary>
    private static Character TwoPurses(Character who) =>
        who with { Inventory = [Purse("purse-runt", "the runt's"), Purse("purse-captain", "the captain's")] };

    // ------------------------------------------------------------------------------------------
    // The resolution rule itself
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_bare_name_that_fits_two_items_resolves_to_neither()
    {
        var vark = TwoPurses(TestWorld.Vark());

        var resolution = vark.ResolveItem("Small Purse of Gold Coins");

        Assert.True(resolution.Ambiguous);
        Assert.False(resolution.Found);
        Assert.Null(resolution.Item);
    }

    [Fact]
    public void A_qualified_name_still_picks_its_own_item()
    {
        // Without this step two identically named items would be permanently unreferenceable, and refusing
        // every attempt would be no better than guessing. The qualifier is what makes the refusal actionable.
        var vark = TwoPurses(TestWorld.Vark());

        Assert.Equal("purse-captain", vark.ResolveItem("Small Purse of Gold Coins (the captain's)").Item?.Id);
        Assert.Equal("purse-runt", vark.ResolveItem("Small Purse of Gold Coins (the runt's)").Item?.Id);
    }

    [Fact]
    public void An_id_is_never_ambiguous_even_when_the_names_collide()
    {
        var vark = TwoPurses(TestWorld.Vark());

        var resolution = vark.ResolveItem("purse-runt");

        Assert.False(resolution.Ambiguous);
        Assert.Equal("purse-runt", resolution.Item?.Id);
    }

    [Fact]
    public void One_item_of_that_name_is_not_ambiguous()
    {
        // The ordinary case has to stay ordinary: ambiguity is about the inventory, not about the wording.
        var vark = TestWorld.Vark() with { Inventory = [Purse("purse-runt", "the runt's"), TestWorld.Rope()] };

        Assert.Equal("purse-runt", vark.ResolveItem("Small Purse of Gold Coins").Item?.Id);
        Assert.Equal("rope", vark.ResolveItem("Rope").Item?.Id);
    }

    [Fact]
    public void A_character_and_a_container_resolve_by_the_same_rule()
    {
        // The two halves used to be separate implementations, and only one of them reported ambiguity. They
        // are now the same function, so a reference cannot mean different things in a chest and in a hand.
        var contents = new[] { Purse("purse-runt", "the runt's"), Purse("purse-captain", "the captain's") };
        var chest = TestWorld.Chest(open: true, contents: contents);
        var vark = TwoPurses(TestWorld.Vark());

        Assert.True(chest.ResolveItem("Small Purse of Gold Coins").Ambiguous);
        Assert.True(vark.ResolveItem("Small Purse of Gold Coins").Ambiguous);
        Assert.Equal("purse-captain", chest.ResolveItem("Small Purse of Gold Coins (the captain's)").Item?.Id);
    }

    // ------------------------------------------------------------------------------------------
    // What the engine does with it
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Stealing_an_ambiguously_named_item_is_refused_rather_than_guessed()
    {
        // The RNG throws on any draw, so this also proves the theft is refused BEFORE the dice come out:
        // an ambiguous reference must not spend the actor's one attempt at that target.
        var engine = EngineWith(TestWorld.State(TestWorld.Rowan(), TwoPurses(TestWorld.Vark())));

        var result = engine.Execute(new StealItemAction(TestWorld.RowanId, TestWorld.VarkId, "Small Purse of Gold Coins"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ItemReferenceAmbiguous, result.RejectionReason);
        Assert.Equal(0, engine.State.Version);

        // Both purses are still where they were.
        Assert.Equal(2, engine.State.RequireById(TestWorld.VarkId).Inventory.Length);
        Assert.Empty(engine.State.RequireById(TestWorld.RowanId).Inventory);
    }

    [Fact]
    public void The_refusal_names_the_alternatives_so_the_next_attempt_can_pick_one()
    {
        // The point of the refusal. A bare "that is ambiguous" hands the model back the same words it just
        // used; the qualified display names are precisely the references that would resolve.
        var engine = EngineWith(TestWorld.State(TestWorld.Rowan(), TwoPurses(TestWorld.Vark())));

        var result = engine.Execute(new StealItemAction(TestWorld.RowanId, TestWorld.VarkId, "Small Purse of Gold Coins"));

        Assert.Contains("Small Purse of Gold Coins (the runt's)", result.RejectionMessage, StringComparison.Ordinal);
        Assert.Contains("Small Purse of Gold Coins (the captain's)", result.RejectionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_qualified_theft_of_one_of_two_identical_purses_goes_through()
    {
        // The other half of the contract: naming which one is meant works, and moves exactly that purse.
        var engine = EngineWith(TestWorld.State(TestWorld.Rowan(), TwoPurses(TestWorld.Vark())), new ScriptedRng(40));

        var result = engine.Execute(new StealItemAction(
            TestWorld.RowanId, TestWorld.VarkId, "Small Purse of Gold Coins (the captain's)"));

        Assert.True(result.Accepted);
        var stolen = Assert.Single(engine.State.RequireById(TestWorld.RowanId).Inventory);
        Assert.Equal("purse-captain", stolen.Id);

        var left = Assert.Single(engine.State.RequireById(TestWorld.VarkId).Inventory);
        Assert.Equal("purse-runt", left.Id);
    }

    [Fact]
    public void Giving_an_ambiguously_named_item_is_refused()
    {
        var engine = EngineWith(TestWorld.State(TwoPurses(TestWorld.Rowan()), TestWorld.Elara(health: 6) with { Inventory = [] }));

        var result = engine.Execute(new GiveItemAction(TestWorld.RowanId, TestWorld.ElaraId, "Small Purse of Gold Coins"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ItemReferenceAmbiguous, result.RejectionReason);
        Assert.Equal(0, engine.State.Version);
    }

    [Fact]
    public void Dropping_an_ambiguously_named_item_is_refused_and_leaves_the_floor_clear()
    {
        var engine = EngineWith(TestWorld.State(TwoPurses(TestWorld.Rowan()), TestWorld.Elara(health: 6)));

        var result = engine.Execute(new DropItemAction(TestWorld.RowanId, "Small Purse of Gold Coins"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ItemReferenceAmbiguous, result.RejectionReason);
        Assert.Equal(2, engine.State.RequireById(TestWorld.RowanId).Inventory.Length);
    }

    [Fact]
    public void Using_an_ambiguously_named_item_is_refused()
    {
        // Two vials of the same salve is the case where guessing looks harmless and is not: the wrong one is
        // consumed, and the character's own account of what they are carrying stops matching the state.
        var rowan = TestWorld.Rowan() with
        {
            Health = 4,
            Inventory =
            [
                new InventoryItem("salve-a", "Vial of Goblin Salve", "A vial.", 4, "the runt's"),
                new InventoryItem("salve-b", "Vial of Goblin Salve", "A vial.", 4, "the captain's")
            ]
        };
        var engine = EngineWith(TestWorld.State(rowan, TestWorld.Vark()));

        var result = engine.Execute(new UseItemAction(TestWorld.RowanId, "Vial of Goblin Salve"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ItemReferenceAmbiguous, result.RejectionReason);
        Assert.Equal(4, engine.State.RequireById(TestWorld.RowanId).Health);
        Assert.Equal(2, engine.State.RequireById(TestWorld.RowanId).Inventory.Length);
    }

    [Fact]
    public void An_item_that_is_simply_absent_is_still_reported_as_absent_not_ambiguous()
    {
        // The two refusals mean different things and must not blur into one: "say which" is answerable, and
        // "there is no such thing" is not.
        var engine = EngineWith(TestWorld.State(TwoPurses(TestWorld.Rowan()), TestWorld.Elara(health: 6) with { Inventory = [] }));

        var result = engine.Execute(new GiveItemAction(TestWorld.RowanId, TestWorld.ElaraId, "Silver Crown"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.ItemNotPossessed, result.RejectionReason);
    }
}
