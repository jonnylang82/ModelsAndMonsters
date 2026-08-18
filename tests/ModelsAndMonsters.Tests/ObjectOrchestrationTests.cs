using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// Container interaction driven through the real <see cref="TurnCoordinator"/>: a character expresses a
/// natural-language intent, the Dungeon Master translates it into one engine action, and the coordinator
/// traces the object interaction. Covers the turn-consuming opens and takes, the atomic transfer, the
/// rejections, and the compound-action rule that opening and taking cannot both happen in one breath.
/// </summary>
public sealed class ObjectOrchestrationTests
{
    // ------------------------------------------------------------------------------------------
    // Opening through the coordinator consumes the turn and is traced (test #15)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Opening_the_chest_consumes_the_turn_and_records_an_accepted_object_interaction()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.OpenContainerName,
                    ("actor", "Rowan"), ("container", "Old Iron-Bound Chest")),
                ScriptedChatClient.Text("Rowan throws back the lid; a small vial glints inside.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName,
                    ("intent", "I heave open the old chest."))))),
            initialState: TestWorld.TwoVsTwoStateWithChest());

        var result = await harness.RunTurn("Rowan");

        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
        Assert.True(harness.Engine.State.Room.Objects.OfType<Container>().Single().IsOpen);

        var interaction = Assert.Single(harness.Sink.Payloads<ObjectInteractionPayload>(TraceEventType.ObjectInteraction));
        Assert.Equal("open_container", interaction.ActionType);
        Assert.Equal("accepted", interaction.ValidationResult);
        Assert.Equal(TestWorld.RowanId, interaction.ActorId);
        Assert.False(interaction.ContainerOpenBefore);
        Assert.True(interaction.ContainerOpenAfter);
        Assert.Equal(interaction.WorldVersionBefore + 1, interaction.WorldVersionAfter);
    }

    // ------------------------------------------------------------------------------------------
    // Taking through the coordinator transfers ownership atomically (test #18) and is fully traced
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Taking_the_potion_moves_it_into_the_actor_inventory_and_traces_both_halves()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.TakeItemName,
                    ("actor", "Elara"), ("container", "Old Iron-Bound Chest"), ("item", "Small Healing Potion")),
                ScriptedChatClient.Text("Elara snatches the vial from the open chest and tucks it away.")),
            MultiActorHarness.Clients(
                ("Elara", new ScriptedChatClient(ScriptedChatClient.Call("e-1", CharacterTools.TakeActionName,
                    ("intent", "I grab the potion from the open chest."))))),
            initialState: TestWorld.TwoVsTwoStateWithChest(TestWorld.Chest(open: true)));

        await harness.RunTurn("Elara");

        // Ownership transferred: the chest is empty and Elara now carries the potion.
        Assert.Empty(harness.Engine.State.Room.Objects.OfType<Container>().Single().Contents);
        Assert.Contains(harness.Engine.State.RequireById(TestWorld.ElaraId).Inventory, i => i.IsHealingItem);

        var interaction = Assert.Single(harness.Sink.Payloads<ObjectInteractionPayload>(TraceEventType.ObjectInteraction));
        Assert.Equal("take_item", interaction.ActionType);
        Assert.Equal("accepted", interaction.ValidationResult);
        // Both halves of the transfer are captured: the container losing it and the actor gaining it.
        Assert.Contains("Small Healing Potion", interaction.ContainerContentsBefore!);
        Assert.Empty(interaction.ContainerContentsAfter!);
        Assert.DoesNotContain("Small Healing Potion", interaction.ActorInventoryBefore!);
        Assert.Contains("Small Healing Potion", interaction.ActorInventoryAfter!);
    }

    // ------------------------------------------------------------------------------------------
    // Rejections flow back as object interactions (tests #16, #17 at the orchestration level)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Opening_an_already_open_chest_is_traced_as_a_rejected_object_interaction()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.OpenContainerName,
                    ("actor", "Rowan"), ("container", "Old Iron-Bound Chest")),
                // The engine rejects it, so the DM is asked to explain the rejection in-world...
                ScriptedChatClient.Text("The lid is already up; there is nothing to open."),
                // ...and then to narrate Rowan choosing to stand down.
                ScriptedChatClient.Text("Rowan lets the lid be and sets himself.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName, ("intent", "I open the chest.")),
                    ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "Already open."))))),
            initialState: TestWorld.TwoVsTwoStateWithChest(TestWorld.Chest(open: true)));

        await harness.RunTurn("Rowan");

        var interaction = Assert.Single(harness.Sink.Payloads<ObjectInteractionPayload>(TraceEventType.ObjectInteraction));
        Assert.Equal("rejected", interaction.ValidationResult);
        Assert.Equal(nameof(Engine.EngineRejectionReason.ContainerAlreadyOpen), interaction.RejectionReason);

        // The world version never moved.
        Assert.Equal(interaction.WorldVersionBefore, interaction.WorldVersionAfter);
    }

    // ------------------------------------------------------------------------------------------
    // Compound open-and-take cannot perform both operations (test #21)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_compound_open_and_take_is_refused_and_a_single_open_then_succeeds()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                // First intent asks to open AND grab in one breath: the DM refuses it, changing nothing.
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.RejectActionName,
                    ("category", DungeonMasterTools.UnsupportedCategory),
                    ("reason", "You can get the lid up, but searching it and lifting something out will take another moment.")),
                // The retry is a single action — opening — which the world resolves.
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.OpenContainerName,
                    ("actor", "Elara"), ("container", "Old Iron-Bound Chest")),
                ScriptedChatClient.Text("Elara wrenches the lid up; a vial rests inside.")),
            MultiActorHarness.Clients(
                ("Elara", new ScriptedChatClient(
                    ScriptedChatClient.Call("e-1", CharacterTools.TakeActionName, ("intent", "I open the chest and grab the potion.")),
                    ScriptedChatClient.Call("e-2", CharacterTools.TakeActionName, ("intent", "I open the chest."))))),
            initialState: TestWorld.TwoVsTwoStateWithChest());

        await harness.RunTurn("Elara");

        // Exactly one engine action happened — the open — and the potion is still in the chest, unclaimed.
        var chest = harness.Engine.State.Room.Objects.OfType<Container>().Single();
        Assert.True(chest.IsOpen);
        Assert.Contains(chest.Contents, i => i.IsHealingItem);
        Assert.Empty(harness.Engine.State.RequireById(TestWorld.ElaraId).Inventory);

        // The compound intent was refused without an engine call; only the single open reached the engine.
        var interaction = Assert.Single(harness.Sink.Payloads<ObjectInteractionPayload>(TraceEventType.ObjectInteraction));
        Assert.Equal("open_container", interaction.ActionType);
        Assert.Equal("accepted", interaction.ValidationResult);

        var adjudications = harness.Sink.Payloads<DmAdjudicationPayload>(TraceEventType.DmAdjudication).ToList();
        Assert.Contains(adjudications, a => a.Category == ActionResolutionCategory.DmUnsupported.ToString());
    }

    // ------------------------------------------------------------------------------------------
    // Closed-container contents are not exposed as publicly visible or character-visible state (test #14)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_closed_container_hides_its_contents_from_the_character_facing_state()
    {
        var formatter = new WorldStateFormatter(
            PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates")));

        // Elara does not hold the potion; the chest that holds it is closed.
        var state = TestWorld.TwoVsTwoStateWithChest();
        var elara = state.RequireById(TestWorld.ElaraId);
        var selfState = formatter.FormatCharacterSelfState(elara, state);

        // Her own self-knowledge block never mentions what is locked away in a closed chest.
        Assert.DoesNotContain("Small Healing Potion", selfState, StringComparison.Ordinal);
        // ...but it does now name the living allies and enemies, so she cannot lose track of her side.
        Assert.Contains("Rowan", selfState, StringComparison.Ordinal); // ally
        Assert.Contains("Vark", selfState, StringComparison.Ordinal);  // enemy
    }

    [Fact]
    public void The_authoritative_block_marks_a_closed_containers_contents_as_the_dungeon_masters_secret()
    {
        var state = TestWorld.TwoVsTwoStateWithChest();

        var block = WorldStateFormatter.FormatAuthoritativeState(state);

        // The DM is told the chest is closed and that its contents are for the DM alone, not to be revealed.
        Assert.Contains("CLOSED", block, StringComparison.Ordinal);
        Assert.Contains("FOR YOU ONLY", block, StringComparison.Ordinal);
        Assert.Contains("do not reveal", block, StringComparison.OrdinalIgnoreCase);

        // Once opened, the contents are still the DM's to hold: being open does not make them public, so the
        // block must not describe them as visible to everyone, only as known to those who observed them.
        var openBlock = WorldStateFormatter.FormatAuthoritativeState(TestWorld.TwoVsTwoStateWithChest(TestWorld.Chest(open: true)));
        Assert.Contains("OPEN", openBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("in plain view", openBlock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("being open does NOT reveal them to everyone", openBlock, StringComparison.Ordinal);
    }
}
