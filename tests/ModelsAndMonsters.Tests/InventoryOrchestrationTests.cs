using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.6 inventory transfers driven through the real <see cref="TurnCoordinator"/>: a character states an
/// intent, the (scripted) Dungeon Master translates it into give/drop/steal, and the coordinator moves the
/// item, delivers the public knowledge to exactly the eligible observers, records provenance, and — for a
/// theft — enforces the informational-basis guard before any RNG is consulted.
/// </summary>
public sealed class InventoryOrchestrationTests
{
    private static InventoryItem Rope() => TestWorld.Rope();

    /// <summary>A 2v2 state whose members carry the given inventories, matching the harness's scenario ids.</summary>
    private static GameState State(
        IEnumerable<InventoryItem>? rowan = null,
        IEnumerable<InventoryItem>? elara = null,
        IEnumerable<InventoryItem>? vark = null,
        Character? skritOverride = null) =>
        TestWorld.State(
            TestWorld.Rowan() with { Inventory = [.. rowan ?? []] },
            TestWorld.Elara(health: 6) with { Inventory = [.. elara ?? []] },
            TestWorld.Vark() with { Inventory = [.. vark ?? []] },
            skritOverride ?? TestWorld.Skrit());

    // ------------------------------------------------------------------------------------------
    // give_item
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Giving_through_the_coordinator_moves_the_item_consumes_the_turn_and_records_provenance()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.GiveItemName,
                    ("actor", "Rowan"), ("recipient", "Elara"), ("item", "Rope")),
                ScriptedChatClient.Text("Rowan presses the coil of rope into Elara's hands.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName,
                    ("intent", "I hand my rope to Elara."))))),
            initialState: State(rowan: [Rope()]));

        var result = await harness.RunTurn("Rowan");

        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
        Assert.Empty(harness.Engine.State.RequireById(TestWorld.RowanId).Inventory);
        Assert.Contains(harness.Engine.State.RequireById(TestWorld.ElaraId).Inventory, i => i.Id == "rope");

        var interaction = Assert.Single(harness.Sink.Payloads<InventoryInteractionPayload>(TraceEventType.InventoryInteraction));
        Assert.Equal("give_item", interaction.ActionType);
        Assert.Equal("accepted", interaction.ValidationResult);
        Assert.Equal(TestWorld.RowanId, interaction.OwnerBefore);
        Assert.Equal(TestWorld.ElaraId, interaction.OwnerAfter);
        Assert.False(interaction.RngConsulted);
        Assert.True(interaction.TurnConsumed);

        var provenance = Assert.Single(harness.Sink.Payloads<ItemProvenancePayload>(TraceEventType.ItemProvenance));
        Assert.Equal("rope", provenance.ItemId);
        Assert.Equal(TestWorld.RowanId, provenance.PreviousOwnerOrLocation);
        Assert.Equal(TestWorld.ElaraId, provenance.NewOwnerOrLocation);
        Assert.False(provenance.RngInvolved);
    }

    [Fact]
    public async Task A_give_is_learned_by_present_characters_but_not_by_an_escaped_one()
    {
        var escapedSkrit = TestWorld.Skrit() with { Disposition = CharacterDisposition.Escaped, EscapedThroughExitId = "cellar-stair-door" };

        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.GiveItemName,
                    ("actor", "Rowan"), ("recipient", "Elara"), ("item", "Rope")),
                ScriptedChatClient.Text("Rowan hands the rope across.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName,
                    ("intent", "I hand my rope to Elara."))))),
            initialState: State(rowan: [Rope()], skritOverride: escapedSkrit));

        await harness.RunTurn("Rowan");

        var delivery = Assert.Single(harness.Sink.Payloads<PublicFactDeliveredPayload>(TraceEventType.PublicFactDelivered)
            .Where(p => p.SourceEvent == "give_item"));
        Assert.Contains(TestWorld.RowanId, delivery.Recipients);
        Assert.Contains(TestWorld.ElaraId, delivery.Recipients);
        Assert.Contains(TestWorld.VarkId, delivery.Recipients);
        Assert.DoesNotContain(TestWorld.SkritId, delivery.Recipients); // escaped: receives no further room events
    }

    // ------------------------------------------------------------------------------------------
    // drop_item
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dropping_through_the_coordinator_puts_the_item_on_the_floor_and_records_provenance()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.DropItemName, ("actor", "Rowan"), ("item", "Rope")),
                ScriptedChatClient.Text("The rope slaps down into the black water at Rowan's feet.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName,
                    ("intent", "I let the rope fall at my feet."))))),
            initialState: State(rowan: [Rope()]));

        await harness.RunTurn("Rowan");

        var ground = harness.Engine.State.Room.Objects.OfType<Container>().Single(c => c.IsGround);
        Assert.Contains(ground.Contents, i => i.Id == "rope");

        var provenance = Assert.Single(harness.Sink.Payloads<ItemProvenancePayload>(TraceEventType.ItemProvenance));
        Assert.Equal("drop_item", provenance.ActionType);
        Assert.Equal(TestWorld.RowanId, provenance.PreviousOwnerOrLocation);
        Assert.Equal(Container.GroundId, provenance.NewOwnerOrLocation);
    }

    // ------------------------------------------------------------------------------------------
    // steal_item — with a legitimate informational basis
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_theft_of_a_known_item_rolls_and_on_success_moves_it_and_is_publicly_noticed()
    {
        // Vark openly carries the rope, so the seeding makes it known to everyone — Rowan has a basis. A roll
        // of 40 lands against the default base chance of 40.
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.StealItemName,
                    ("thief", "Rowan"), ("target", "Vark"), ("item", "Rope")),
                ScriptedChatClient.Text("Rowan's hand darts out and comes back with the rope.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName,
                    ("intent", "I snatch the rope off Vark's belt."))))),
            initialState: State(vark: [Rope()]),
            rng: new ScriptedRng(40));

        var result = await harness.RunTurn("Rowan");

        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
        Assert.Contains(harness.Engine.State.RequireById(TestWorld.RowanId).Inventory, i => i.Id == "rope");
        Assert.Empty(harness.Engine.State.RequireById(TestWorld.VarkId).Inventory);

        // Exactly one steal draw, and everyone present learns of the theft.
        Assert.Single(harness.Sink.OfType(TraceEventType.RngDraw));
        var delivery = Assert.Single(harness.Sink.Payloads<PublicFactDeliveredPayload>(TraceEventType.PublicFactDelivered)
            .Where(p => p.SourceEvent == "steal_item"));
        Assert.Contains(TestWorld.VarkId, delivery.Recipients);

        var provenance = Assert.Single(harness.Sink.Payloads<ItemProvenancePayload>(TraceEventType.ItemProvenance));
        Assert.Equal("steal_item", provenance.ActionType);
        Assert.True(provenance.RngInvolved);
    }

    [Fact]
    public async Task A_failed_theft_of_a_known_item_leaves_ownership_but_still_spends_the_turn_and_is_noticed()
    {
        // A roll of 41 misses against the default base chance of 40.
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.StealItemName,
                    ("thief", "Rowan"), ("target", "Vark"), ("item", "Rope")),
                ScriptedChatClient.Text("Vark twists away, the rope still his.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName,
                    ("intent", "I lunge for the rope on Vark's belt."))))),
            initialState: State(vark: [Rope()]),
            rng: new ScriptedRng(41));

        var result = await harness.RunTurn("Rowan");

        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome); // the turn is spent even though the theft failed
        Assert.Contains(harness.Engine.State.RequireById(TestWorld.VarkId).Inventory, i => i.Id == "rope");
        Assert.Empty(harness.Engine.State.RequireById(TestWorld.RowanId).Inventory);

        // Nothing moved, so there is no provenance event, but the public attempt was still delivered.
        Assert.Empty(harness.Sink.OfType(TraceEventType.ItemProvenance));
        Assert.Single(harness.Sink.OfType(TraceEventType.RngDraw));
        Assert.Contains(harness.Sink.Payloads<PublicFactDeliveredPayload>(TraceEventType.PublicFactDelivered),
            p => p.SourceEvent == "steal_item");
    }

    // ------------------------------------------------------------------------------------------
    // steal_item — without an informational basis (the hidden-item guard)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stealing_an_item_the_thief_has_no_way_of_knowing_about_is_refused_before_any_roll()
    {
        // Vark secretly carries the rope, and knowledge seeding is OFF, so Rowan has no record of it. The
        // theft must be refused on informational grounds — with no engine call and, crucially, no RNG draw.
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.StealItemName,
                    ("thief", "Rowan"), ("target", "Vark"), ("item", "Rope")),
                // The refusal does not consume the turn, so Rowan then ends it — the DM narrates that pass.
                ScriptedChatClient.Text("Rowan thinks better of it and holds his place.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName, ("intent", "I snatch the rope from Vark.")),
                    ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "No use."))))),
            initialState: State(vark: [Rope()]),
            rng: new ScriptedRng(), // throws if any draw is taken
            seedKnowledge: false);

        var result = await harness.RunTurn("Rowan");

        // The item never moved and no randomness was consulted.
        Assert.Contains(harness.Engine.State.RequireById(TestWorld.VarkId).Inventory, i => i.Id == "rope");
        Assert.Empty(harness.Sink.OfType(TraceEventType.RngDraw));
        Assert.Empty(harness.Sink.OfType(TraceEventType.EngineAction));

        var adjudication = Assert.Single(harness.Sink.Payloads<DmAdjudicationPayload>(TraceEventType.DmAdjudication)
            .Where(a => a.Category == nameof(ActionResolutionCategory.DmUnsupported)));
        Assert.Contains("steal_item", adjudication.TranslatedAction!.ToString(), StringComparison.OrdinalIgnoreCase);

        // A rejected theft is recorded as an inventory interaction with the informational-basis reason.
        var interaction = Assert.Single(harness.Sink.Payloads<InventoryInteractionPayload>(TraceEventType.InventoryInteraction));
        Assert.Equal("rejected", interaction.ValidationResult);
        Assert.Equal("NoInformationalBasis", interaction.RejectionReason);
        Assert.False(interaction.RngConsulted);
    }
}
