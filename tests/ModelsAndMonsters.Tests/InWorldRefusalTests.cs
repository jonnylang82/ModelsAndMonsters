using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// Refusals rendered from the rejection code and its bound facts, rather than written by a model and then
/// policed by a regex.
/// </summary>
/// <remarks>
/// The previous arrangement had three moving parts where one would do: the engine produced an operator-facing
/// message, a model turned it into fiction, and a detector tried to notice when the model had explained the
/// machinery instead. Every run found a phrasing the detector lacked, and the rewrites that followed a catch
/// were sometimes worse than the leak. A rejection is a closed set of reasons with a handful of bound facts,
/// so it renders deterministically — which is why these tests assert that the common paths need no detector
/// coverage at all to come out in-world.
/// </remarks>
public sealed class InWorldRefusalTests
{
    private static GameEngine EngineWith(GameState state) =>
        new(state, new ScriptedRng(), CombatRules.NoGlancing);

    [Theory]
    // Every reason the engine can give must render as something a person could be told. The point is not the
    // exact wording but that no wording anywhere in the set names the machine — and that this is true by
    // construction, for reasons nobody has exercised in a live run yet.
    [InlineData(EngineRejectionReason.ItemNotPossessed)]
    [InlineData(EngineRejectionReason.ContainerClosed)]
    [InlineData(EngineRejectionReason.AbilityHasNoUsesLeft)]
    [InlineData(EngineRejectionReason.AbilityNotHeld)]
    [InlineData(EngineRejectionReason.TargetHasSurrendered)]
    [InlineData(EngineRejectionReason.OfferHasNoConcession)]
    [InlineData(EngineRejectionReason.OfferNotAddressedToActor)]
    [InlineData(EngineRejectionReason.EquippedWeaponCannotBeTransferred)]
    [InlineData(EngineRejectionReason.ExitClosed)]
    [InlineData(EngineRejectionReason.UnsupportedAction)]
    public void Every_rejection_code_renders_in_world_without_the_detector_being_involved(EngineRejectionReason reason)
    {
        var rendered = InWorldRefusal.Render(reason, "Aric", "Iron Sword");

        Assert.False(string.IsNullOrWhiteSpace(rendered));
        Assert.False(MachineryLanguage.IsLeak(rendered), $"'{reason}' rendered machinery: {rendered}");
        Assert.Equal(rendered, ModelText.StripPresentationMarkup(rendered));
    }

    [Fact]
    public void No_rejection_code_is_left_without_a_rendering()
    {
        // A new engine rejection must not silently fall through to the generic line. This is the guard that
        // keeps the renderer honest as the action surface grows.
        var unrendered = Enum.GetValues<EngineRejectionReason>()
            .Where(r => r != EngineRejectionReason.UnsupportedAction)
            .Where(r => InWorldRefusal.Render(r, "Aric", "the case") == InWorldRefusal.Generic)
            .ToList();

        Assert.True(unrendered.Count == 0,
            "These rejection codes have no in-world wording of their own: " + string.Join(", ", unrendered));
    }

    [Theory]
    // The bound fact appears, so a refusal is about the thing the character actually named.
    [InlineData(EngineRejectionReason.ItemNotPossessed, "Vial of Goblin Salve")]
    [InlineData(EngineRejectionReason.UnknownTarget, "Vark")]
    [InlineData(EngineRejectionReason.ContainerClosed, "medicine case")]
    public void A_refusal_names_the_thing_it_is_about(EngineRejectionReason reason, string subject) =>
        Assert.Contains(subject, InWorldRefusal.Render(reason, "Aric", subject), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void A_refusal_reads_the_same_with_no_bound_fact_at_all()
    {
        // Bindings are optional: a code that has no subject, or a caller that cannot supply one, still gets a
        // whole sentence rather than a dangling "the ".
        foreach (var reason in Enum.GetValues<EngineRejectionReason>())
        {
            var rendered = InWorldRefusal.Render(reason, "Aric");
            Assert.False(string.IsNullOrWhiteSpace(rendered));
            Assert.DoesNotContain("the .", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("  ", rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_engine_refusal_reaches_the_character_in_world_with_no_model_call_of_its_own()
    {
        // The path that used to spend a model call turning an operator message into fiction — and then
        // sometimes a second one rephrasing the result. Aric carries no vial, so the engine refuses.
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.UseItemName,
                    ("actor", "Aric"), ("item", "Vial of Goblin Salve")),
                // The only other thing the Dungeon Master is asked for this turn: narrating the pass that
                // follows. If the refusal still cost a call of its own, this response would be consumed by
                // it and the turn would run out of script.
                ScriptedChatClient.Text("Aric's hand closes on nothing.")),
            new ScriptedChatClient(
                ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName,
                    (CharacterTools.IntentParameter, "I drink the goblin salve.")),
                ScriptedChatClient.Call("h-2", CharacterTools.EndTurnName,
                    (CharacterTools.ReasonParameter, "I have nothing to drink."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var adjudication = harness.Sink.Payloads<DmAdjudicationPayload>(TraceEventType.DmAdjudication)
            .Single(a => a.Category == ActionResolutionCategory.EngineRejected.ToString());

        // In-world, about the right thing, and never routed through the leak detector or a rephrase.
        Assert.False(MachineryLanguage.IsLeak(adjudication.Reason));
        Assert.Contains("Vial of Goblin Salve", adjudication.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Sink.OfType(TraceEventType.AdjudicationCorrected));

        // Exactly two Dungeon Master calls: the adjudication, and the narration of the pass that followed.
        // Neither is the refusal — it used to cost one call to phrase and sometimes a second to rephrase.
        Assert.Equal(2, harness.DungeonMasterClient.CallCount);
    }

    [Fact]
    public void The_refusal_never_echoes_an_internal_identifier_back_at_a_character()
    {
        // The Dungeon Master may bind a target by id rather than by name. A refusal about it must still be
        // spoken in the name the character would use.
        var state = TestWorld.State(TestWorld.Hero(), TestWorld.Monster());
        var action = new AttackCharacterAction(TestWorld.HeroId, TestWorld.MonsterId, "Iron Sword");

        var subject = InWorldRefusal.SubjectFor(action, EngineRejectionReason.TargetIsDead, state);

        Assert.Equal("Grik", subject);
        Assert.DoesNotContain(TestWorld.MonsterId, InWorldRefusal.Render(EngineRejectionReason.TargetIsDead, "Aric", subject),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_generic_line_is_the_single_shared_fallback()
    {
        // One neutral line, used both when a code has no specific wording and when a Dungeon Master rephrase
        // still leaks — so a character never meets two different "nothing happened" registers.
        Assert.False(MachineryLanguage.IsLeak(InWorldRefusal.Generic));
        Assert.Equal(InWorldRefusal.Generic, InWorldRefusal.Render(EngineRejectionReason.UnsupportedAction, "Aric"));
    }
}
