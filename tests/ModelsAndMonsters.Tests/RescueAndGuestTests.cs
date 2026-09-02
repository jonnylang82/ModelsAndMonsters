using Microsoft.Extensions.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Randomness;
using ModelsAndMonsters.Tracing;
using ModelsAndMonsters.Web;

namespace ModelsAndMonsters.Tests;

public sealed class RescueAndGuestTests
{
    private static GameEngine RescueEngine(params Character[] guards)
    {
        var state = TestWorld.StateWithExit(TestWorld.StairDoor(false),
            [TestWorld.Rowan(), TestWorld.Elara() with { Disposition = CharacterDisposition.Detained }, .. guards]);
        return new GameEngine(state with
        {
            Rescue = new RescueObjective(TestWorld.ElaraId, TestWorld.GoblinsTeam, TestWorld.StairDoorId)
        }, new SeededRng(1), CombatRules.NoGlancing);
    }

    [Fact]
    public void Last_guard_defeated_releases_guest_but_only_extraction_completes_rescue()
    {
        var engine = RescueEngine(TestWorld.Vark() with { Health = 1 });
        var result = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));
        Assert.True(result.Accepted);
        Assert.Equal(TestWorld.ElaraId, result.ReleasedDetaineeId);
        Assert.True(engine.State.RequireById(TestWorld.ElaraId).CanAct);
        Assert.False(TerminalCondition.Evaluate(engine.State).IsOver);
        Assert.Equal("extraction", StateDto.From(engine.State).RescueStage);
        Assert.True(engine.Execute(new OpenExitAction("Elara", TestWorld.StairDoorId)).Accepted);
        Assert.True(engine.Execute(new EscapeEncounterAction("Elara", TestWorld.StairDoorId)).Accepted);
        Assert.Equal(EncounterOutcome.Rescued, TerminalCondition.Evaluate(engine.State).Outcome);
    }

    [Fact]
    public void Another_active_guard_keeps_detention_locked_and_health_totals_include_guest()
    {
        var engine = RescueEngine(TestWorld.Vark() with { Health = 1 }, TestWorld.Skrit());
        engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));
        Assert.Equal(CharacterDisposition.Detained, engine.State.RequireById(TestWorld.ElaraId).Disposition);
        var heroes = TerminalCondition.Evaluate(engine.State).Standings.Single(s => s.Team == "Heroes");
        Assert.Equal(2, heroes.Living);
        Assert.Equal(1, heroes.Detained);
        Assert.False(engine.Execute(new AttackCharacterAction("Skrit", "Elara", "Dagger")).Accepted);
    }

    [Fact]
    public void Accepted_surrender_also_unlocks_the_gate()
    {
        var engine = RescueEngine(TestWorld.Vark() with { Inventory = [TestWorld.Purse("vark")] });
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], true));
        Assert.True(offer.Accepted);
        Assert.Equal(CharacterDisposition.Detained, engine.State.RequireById(TestWorld.ElaraId).Disposition);
        var accepted = engine.Execute(new AcceptSurrenderAction("Rowan", ((OfferSurrenderOutcome)offer.Outcome!).OfferId));
        Assert.True(accepted.Accepted);
        Assert.Equal(TestWorld.ElaraId, accepted.ReleasedDetaineeId);
        Assert.False(TerminalCondition.Evaluate(engine.State).IsOver);
    }

    [Fact]
    public void Leaving_the_detainee_behind_is_failure_not_a_victory()
    {
        var engine = RescueEngine(TestWorld.Vark());
        engine.Execute(new OpenExitAction("Rowan", TestWorld.StairDoorId));
        engine.Execute(new EscapeEncounterAction("Rowan", TestWorld.StairDoorId));
        Assert.Equal(EncounterOutcome.RescueFailed, TerminalCondition.Evaluate(engine.State).Outcome);
    }

    [Theory]
    [InlineData("nonsense", 4)]
    [InlineData("99", 4)]
    [InlineData("Active", 0)]
    [InlineData("Dead", 4)]
    public void Invalid_starting_dispositions_are_rejected(string disposition, int health)
    {
        var scenario = TestWorld.TwoVsTwoScenario();
        scenario.Characters[0].StartingDisposition = disposition;
        scenario.Characters[0].Health = health;
        Assert.Throws<InvalidOperationException>(() => ScenarioFactory.CreateInitialState(scenario));
    }

    [Fact]
    public async Task Human_turn_uses_normal_engine_and_AI_resumes_with_that_history()
    {
        var input = new OneHumanDecision(new GuestDecision("I pull open the cellar stair door."));
        var client = new ScriptedChatClient(ScriptedChatClient.Call("ai-pass", CharacterTools.EndTurnName, ("reason", "I wait.")));
        var harness = new MultiActorHarness(new ScriptedChatClient(
            ScriptedChatClient.Call("open", DungeonMasterTools.OpenExitName, ("actor", "Rowan"), ("exit", TestWorld.StairDoorId)),
            ScriptedChatClient.Text("Rowan opens the stair door."), ScriptedChatClient.Text("Rowan waits.")),
            MultiActorHarness.Clients(("Rowan", client)),
            initialState: TestWorld.StateWithExit(TestWorld.StairDoor(false), TestWorld.Rowan(), TestWorld.Elara(), TestWorld.Vark(), TestWorld.Skrit()),
            characterInput: input);
        var first = await harness.RunTurn("Rowan");
        Assert.Equal(TurnOutcome.ActionResolved, first.Outcome);
        Assert.Equal(0, first.ModelCalls);
        Assert.Equal(0, client.CallCount);
        Assert.True(harness.Engine.State.ResolveExit(TestWorld.StairDoorId).Exit!.IsOpen);
        await harness.RunTurn("Rowan", round: 2, turn: 2);
        Assert.Equal(1, client.CallCount);
        Assert.Contains(harness.Agent("Rowan").Conversation.Messages,
            m => m.Contents.OfType<FunctionCallContent>().Any(c => c.Name == CharacterTools.TakeActionName));
        Assert.Contains(harness.Agent("Rowan").Conversation.Messages, m => m.Role == ChatRole.Tool);
    }

    private sealed class OneHumanDecision(GuestDecision decision) : ICharacterInput
    {
        private bool _used;
        public Task<GuestDecision?> ReadAsync(string characterId, CancellationToken cancellationToken)
        {
            var result = _used ? null : decision;
            _used = true;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task Guest_control_rejects_stale_and_duplicate_commands_and_release_unblocks()
    {
        var events = new List<UiEvent>();
        var control = new GuestControl(events.Add, "deacon");
        Assert.Null(await control.ReadAsync("deacon", CancellationToken.None));
        Assert.True(control.SetControlled(true));
        Assert.Null(await control.ReadAsync("someone-else", CancellationToken.None));
        var wait = control.ReadAsync("deacon", CancellationToken.None);
        var request = System.Text.Json.JsonSerializer.SerializeToElement(events.Last().Payload).GetProperty("requestId").GetString()!;
        Assert.False(control.Submit("stale", new GuestDecision("Open the stair.")));
        Assert.True(control.Submit(request, new GuestDecision("Open the stair.")));
        Assert.False(control.Submit(request, new GuestDecision("Open it twice.")));
        Assert.Equal("Open the stair.", (await wait)!.Intent);
        var next = control.ReadAsync("deacon", CancellationToken.None);
        control.SetControlled(false);
        Assert.Null(await next);
        control.Close();
        Assert.False(control.SetControlled(true));
    }

    [Fact]
    public async Task Cancelling_a_run_unblocks_a_waiting_guest()
    {
        var control = new GuestControl(_ => { }, "deacon");
        control.SetControlled(true);
        using var cts = new CancellationTokenSource();
        var wait = control.ReadAsync("deacon", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task Detained_guest_keeps_round_sized_public_memories_without_model_calls()
    {
        var harness = new MultiActorHarness(new ScriptedChatClient(), MultiActorHarness.Clients(),
            initialState: TestWorld.State(TestWorld.Rowan(), TestWorld.Elara() with
            { Disposition = CharacterDisposition.Detained }, TestWorld.Vark(), TestWorld.Skrit()));
        harness.NarrationLog.Record("public", "Rowan calls through the bars: we are coming for you.");
        await harness.RunTurn("Elara");
        harness.NarrationLog.Record("public", "The guard drops his maul.");
        await harness.RunTurn("Elara", round: 2, turn: 5);
        Assert.Equal(2, harness.Agent("Elara").Conversation.Count);
        Assert.Contains("we are coming", harness.Agent("Elara").Conversation.Messages[0].Text);
        Assert.Contains("drops his maul", harness.Agent("Elara").Conversation.Messages[1].Text);
    }
}
