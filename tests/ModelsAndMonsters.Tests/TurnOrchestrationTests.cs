using Microsoft.Extensions.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// Exercises the manual tool-dispatch loop end to end with scripted models. No network involved.
/// </summary>
public sealed class TurnOrchestrationTests
{
    private static ScriptedChatClient AcceptedAttackDungeonMaster() => new(
        ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
            ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
        ScriptedChatClient.Text("Your blade opens a gash across the goblin's shoulder."));

    [Fact]
    public async Task A_character_tool_call_is_captured_and_dispatched_by_the_application()
    {
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName,
                ("intent", "I bring my sword down on the goblin."))),
            new ScriptedChatClient());

        var result = await harness.RunHeroTurn();

        var dispatches = harness.Sink.Payloads<ToolCallDispatchPayload>(TraceEventType.ToolCallDispatched).ToList();
        Assert.Contains(dispatches, d => d.ToolName == CharacterTools.TakeActionName && d.AgentName == "Aric");
        Assert.Contains(dispatches, d => d.ToolName == DungeonMasterTools.AttackCharacterName);

        // The captured argument is the character's own words, unchanged.
        var takeAction = dispatches.First(d => d.ToolName == CharacterTools.TakeActionName);
        Assert.Equal("I bring my sword down on the goblin.", takeAction.Arguments!["intent"]);
        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
    }

    [Fact]
    public async Task Asking_the_dungeon_master_does_not_consume_the_turn()
    {
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Text("It looks wary, and favours one leg."),
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("Your blade opens a gash across its shoulder.")),
            new ScriptedChatClient(
                ScriptedChatClient.Call("h-1", CharacterTools.AskDmName, ("question", "Does it look hurt?")),
                ScriptedChatClient.Call("h-2", CharacterTools.TakeActionName, ("intent", "I strike at its leg."))),
            new ScriptedChatClient());

        var stateBefore = harness.Engine.State;
        var result = await harness.RunHeroTurn();

        Assert.Equal(1, result.QuestionsAsked);
        Assert.Equal(1, result.ActionAttempts);
        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);

        // The question alone changed nothing; the action that followed did.
        Assert.Equal(stateBefore.Version + 1, harness.Engine.State.Version);

        // The answer reached the asking character as a tool result.
        var answer = harness.Sink.Payloads<ToolCallResultPayload>(TraceEventType.ToolCallResult)
            .First(r => r.ToolName == CharacterTools.AskDmName);
        Assert.Equal("It looks wary, and favours one leg.", answer.Result);
    }

    [Fact]
    public async Task A_question_alone_never_ends_the_turn()
    {
        // Two questions and no action: the turn can only end by hitting a harness limit.
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(ScriptedChatClient.Text("Wary."), ScriptedChatClient.Text("Still wary.")),
            new ScriptedChatClient(
                ScriptedChatClient.Call("h-1", CharacterTools.AskDmName, ("question", "Is it hurt?")),
                ScriptedChatClient.Call("h-2", CharacterTools.AskDmName, ("question", "Is it afraid?")),
                ScriptedChatClient.Call("h-3", CharacterTools.AskDmName, ("question", "Again?"))),
            new ScriptedChatClient(),
            new HarnessOptions { MaxQuestionsPerTurn = 2, MaxActionAttemptsPerTurn = 3, MaxModelCallsPerTurn = 3 });

        var result = await harness.RunHeroTurn();

        Assert.Equal(TurnOutcome.AbandonedAtLimit, result.Outcome);
        Assert.Equal(2, result.QuestionsAsked);
        Assert.Equal(0, harness.Engine.State.Version);
    }

    [Fact]
    public async Task A_dungeon_master_rejection_lets_the_character_try_again()
    {
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.RejectActionName,
                    ("category", "unsupported"), ("reason", "You could throw it, but nothing here can resolve that.")),
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("Your blade bites deep.")),
            new ScriptedChatClient(
                ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I throw my sword at it.")),
                ScriptedChatClient.Call("h-2", CharacterTools.TakeActionName, ("intent", "I strike it with my sword."))),
            new ScriptedChatClient());

        var result = await harness.RunHeroTurn();

        Assert.Equal(2, result.ActionAttempts);
        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);

        var adjudications = harness.Sink.Payloads<DmAdjudicationPayload>(TraceEventType.DmAdjudication).ToList();
        Assert.Equal(ActionResolutionCategory.DmUnsupported.ToString(), adjudications[0].Category);
        Assert.Equal(ActionResolutionCategory.EngineAccepted.ToString(), adjudications[1].Category);

        // The rejected attempt never reached the engine.
        Assert.Single(harness.Sink.OfType(TraceEventType.EngineAction));
    }

    [Fact]
    public async Task An_impossible_action_is_categorised_separately_from_an_unsupported_one()
    {
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.RejectActionName,
                    ("category", "impossible"), ("reason", "You have no means of flight.")),
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("The blow lands.")),
            new ScriptedChatClient(
                ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I fly to the ceiling.")),
                ScriptedChatClient.Call("h-2", CharacterTools.TakeActionName, ("intent", "I strike it."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var first = harness.Sink.Payloads<DmAdjudicationPayload>(TraceEventType.DmAdjudication).First();
        Assert.Equal(ActionResolutionCategory.DmImpossible.ToString(), first.Category);
        Assert.Equal("I fly to the ceiling.", first.Intent);
    }

    [Fact]
    public async Task An_engine_rejection_is_returned_through_the_dungeon_master_and_the_character_retries()
    {
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                // The DM names a weapon the hero does not carry: the engine, not the DM, catches it.
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Warhammer")),
                ScriptedChatClient.Text("You reach for a hammer you are not carrying."),
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("Your sword lands squarely.")),
            new ScriptedChatClient(
                ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I smash it with my hammer.")),
                ScriptedChatClient.Call("h-2", CharacterTools.TakeActionName, ("intent", "I cut at it with my sword."))),
            new ScriptedChatClient());

        var result = await harness.RunHeroTurn();

        var engineActions = harness.Sink.Payloads<EngineActionPayload>(TraceEventType.EngineAction).ToList();
        Assert.Equal(2, engineActions.Count);
        Assert.False(engineActions[0].Accepted);
        Assert.True(engineActions[1].Accepted);

        // The rejected attempt left the world untouched; only the accepted one changed it.
        Assert.Equal(engineActions[0].StateBefore.Version, engineActions[0].StateAfter.Version);
        Assert.Equal(1, harness.Engine.State.Version);
        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);

        var categories = harness.Sink.Payloads<DmAdjudicationPayload>(TraceEventType.DmAdjudication)
            .Select(a => a.Category).ToList();
        Assert.Equal(
            [ActionResolutionCategory.EngineRejected.ToString(), ActionResolutionCategory.EngineAccepted.ToString()],
            categories);
    }

    [Fact]
    public async Task An_accepted_action_ends_the_turn_immediately()
    {
        // The character asks for an action and a question in the same reply. Once the action is
        // accepted the turn is over, and the surplus call is still answered so the history stays valid.
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(ScriptedChatClient.Calls(
                ScriptedChatClient.CallContent("h-1", CharacterTools.TakeActionName, ("intent", "I strike it.")),
                ScriptedChatClient.CallContent("h-2", CharacterTools.AskDmName, ("question", "Did that work?")))),
            new ScriptedChatClient());

        var result = await harness.RunHeroTurn();

        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
        Assert.Equal(1, result.ModelCalls);
        Assert.Equal(0, result.QuestionsAsked);

        // Both calls were answered exactly once.
        var results = harness.Sink.Payloads<ToolCallResultPayload>(TraceEventType.ToolCallResult)
            .Where(r => r.AgentName == "Aric").ToList();
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.CallId == "h-2" && r.Result!.ToString()!.StartsWith("Ignored", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hero_and_monster_conversation_histories_stay_isolated()
    {
        const string heroSecret = "It keeps glancing nervously towards the exit.";

        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Text(heroSecret),
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("Your blade opens a gash across its shoulder."),
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Grik"), ("target", "Aric"), ("weapon", "Rusty Axe")),
                ScriptedChatClient.Text("The axe glances off Aric's mail.")),
            new ScriptedChatClient(
                ScriptedChatClient.Call("h-1", CharacterTools.AskDmName, ("question", "Can I tell if it is frightened?")),
                ScriptedChatClient.Call("h-2", CharacterTools.TakeActionName, ("intent", "I strike at it."))),
            new ScriptedChatClient(
                ScriptedChatClient.Call("m-1", CharacterTools.TakeActionName, ("intent", "I swing my axe at him."))));

        await harness.RunHeroTurn();
        await harness.RunMonsterTurn();

        var heroHistory = Flatten(harness.Hero);
        var monsterHistory = Flatten(harness.Monster);
        var dungeonMasterHistory = Flatten(harness.DungeonMaster);

        // The hero's private exchange is in the hero's history and the DM's, and nowhere else.
        Assert.Contains(heroSecret, heroHistory, StringComparison.Ordinal);
        Assert.Contains(heroSecret, dungeonMasterHistory, StringComparison.Ordinal);
        Assert.DoesNotContain(heroSecret, monsterHistory, StringComparison.Ordinal);
        Assert.DoesNotContain("Can I tell if it is frightened?", monsterHistory, StringComparison.Ordinal);

        // Nor does the monster learn the hero's intent text, only what the DM narrated publicly.
        Assert.DoesNotContain("I strike at it.", monsterHistory, StringComparison.Ordinal);
        Assert.Contains("gash across its shoulder", monsterHistory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_character_receives_only_its_own_exact_state()
    {
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var turnContext = harness.HeroClient.Requests[0].Last().Text;

        Assert.Contains("You are Aric.", turnContext, StringComparison.Ordinal);
        Assert.Contains("10 / 10", turnContext, StringComparison.Ordinal);
        Assert.Contains("Iron Sword", turnContext, StringComparison.Ordinal);

        // No authoritative numbers about the opponent leak into the character's context.
        Assert.DoesNotContain("Grik", turnContext, StringComparison.Ordinal);
        Assert.DoesNotContain("8 / 8", turnContext, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Characters_are_given_only_their_own_three_tools_and_never_engine_tools()
    {
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var heroTools = harness.HeroClient.RequestOptions[0]!.Tools!.Select(t => t.Name).ToList();
        Assert.Equal(
            [CharacterTools.AskDmName, CharacterTools.TakeActionName, CharacterTools.EndTurnName],
            heroTools);

        var dungeonMasterTools = harness.DungeonMasterClient.RequestOptions[0]!.Tools!.Select(t => t.Name).ToList();
        Assert.Equal(
            [DungeonMasterTools.AttackCharacterName, DungeonMasterTools.UseItemName, DungeonMasterTools.RejectActionName],
            dungeonMasterTools);

        // Narration is a toolless call: the DM cannot change the world while describing it.
        Assert.Null(harness.DungeonMasterClient.RequestOptions[1]!.Tools);
    }

    [Fact]
    public async Task A_character_requesting_an_engine_tool_directly_is_refused_and_changes_nothing()
    {
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(
                ScriptedChatClient.Call("h-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Call("h-2", CharacterTools.TakeActionName, ("intent", "I strike it properly."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var errors = harness.Sink.Payloads<ToolCallErrorPayload>(TraceEventType.ToolCallError).ToList();
        Assert.Contains(errors, e => e.AgentName == "Aric" && e.ToolName == DungeonMasterTools.AttackCharacterName);

        // Exactly one engine action happened, and it came from the Dungeon Master.
        Assert.Single(harness.Sink.OfType(TraceEventType.EngineAction));
        Assert.Equal(1, harness.Engine.State.Version);
    }

    [Fact]
    public async Task A_character_replying_without_a_tool_call_is_nudged_rather_than_losing_its_turn()
    {
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(
                ScriptedChatClient.Text("I think I will attack the goblin."),
                ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike it."))),
            new ScriptedChatClient());

        var result = await harness.RunHeroTurn();

        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
        Assert.Equal(2, result.ModelCalls);
        Assert.Contains(
            harness.Sink.Payloads<ToolCallErrorPayload>(TraceEventType.ToolCallError),
            e => e.ToolName == "(none)");
    }

    [Fact]
    public async Task Repeated_failures_stop_at_the_configured_attempt_limit()
    {
        var refusal = ScriptedChatClient.Call("dm", DungeonMasterTools.RejectActionName,
            ("category", "unsupported"), ("reason", "Nothing here can resolve that."));

        var harness = new OrchestrationHarness(
            new ScriptedChatClient(refusal, refusal),
            new ScriptedChatClient(
                ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I throw my sword.")),
                ScriptedChatClient.Call("h-2", CharacterTools.TakeActionName, ("intent", "I throw my boot.")),
                ScriptedChatClient.Call("h-3", CharacterTools.TakeActionName, ("intent", "I throw the bench."))),
            new ScriptedChatClient(),
            new HarnessOptions { MaxQuestionsPerTurn = 2, MaxActionAttemptsPerTurn = 2, MaxModelCallsPerTurn = 8 });

        var result = await harness.RunHeroTurn();

        Assert.Equal(TurnOutcome.AbandonedAtLimit, result.Outcome);
        Assert.Equal(2, result.ActionAttempts);
        Assert.Contains(
            harness.Sink.Payloads<HarnessLimitPayload>(TraceEventType.HarnessLimitReached),
            l => l.Limit == nameof(HarnessOptions.MaxActionAttemptsPerTurn));
        Assert.Equal(0, harness.Engine.State.Version);
    }

    [Fact]
    public async Task The_dungeon_master_is_re_asked_when_it_adjudicates_without_a_tool_call()
    {
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Text("That seems reasonable enough to me."),
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("The blow lands.")),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike."))),
            new ScriptedChatClient(),
            new HarnessOptions { MaxAdjudicationRetries = 1, MaxQuestionsPerTurn = 2, MaxActionAttemptsPerTurn = 3, MaxModelCallsPerTurn = 8 });

        var result = await harness.RunHeroTurn();

        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
        Assert.Contains(
            harness.Sink.Payloads<ToolCallErrorPayload>(TraceEventType.ToolCallError),
            e => e.AgentName == "DungeonMaster" && e.ToolName == "(none)");
    }

    [Fact]
    public async Task A_character_can_end_its_turn_without_acting()
    {
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(ScriptedChatClient.Text("Aric lowers his blade and stands quite still.")),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.EndTurnName,
                ("reason", "I have no strength left, and I stay where I am."))),
            new ScriptedChatClient());

        var result = await harness.RunHeroTurn();

        Assert.Equal(TurnOutcome.EndedByCharacter, result.Outcome);
        Assert.Equal(0, result.ActionAttempts);

        // Choosing to do nothing reaches no engine action and changes nothing.
        Assert.Empty(harness.Sink.OfType(TraceEventType.EngineAction));
        Assert.Equal(0, harness.Engine.State.Version);

        var passed = Assert.Single(harness.Sink.Payloads<CharacterPassedPayload>(TraceEventType.CharacterPassed));
        Assert.Equal("Aric", passed.CharacterName);
        Assert.Contains("no strength left", passed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ending_a_turn_is_narrated_so_the_other_character_can_perceive_it()
    {
        const string pass = "Aric lowers his blade and stands quite still, watching.";

        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Text(pass),
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Grik"), ("target", "Aric"), ("weapon", "Rusty Axe")),
                ScriptedChatClient.Text("The axe bites into Aric's shoulder.")),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.EndTurnName, ("reason", "I wait."))),
            new ScriptedChatClient(ScriptedChatClient.Call("m-1", CharacterTools.TakeActionName,
                ("intent", "I swing at him while he hesitates."))));

        await harness.RunHeroTurn();
        await harness.RunMonsterTurn();

        // The monster learned that the hero held back, through public narration rather than shared history.
        var monsterContext = harness.MonsterClient.Requests[0].Last().Text!;
        Assert.Contains(pass, monsterContext, StringComparison.Ordinal);
        Assert.DoesNotContain("I wait.", monsterContext, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_acting_character_is_forced_as_the_actor_when_the_dungeon_master_names_another()
    {
        // Reproduces an observed failure: a confused character described itself by its opponent's name,
        // and the Dungeon Master translated that faithfully into an attacker who was also the target.
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Grik"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("Your blade opens a gash across the goblin's shoulder.")),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName,
                ("intent", "I bring my sword down on the goblin."))),
            new ScriptedChatClient());

        var result = await harness.RunHeroTurn();

        var correction = Assert.Single(
            harness.Sink.Payloads<AdjudicationCorrectionPayload>(TraceEventType.AdjudicationCorrected));
        Assert.Equal(DungeonMasterTools.AttackerParameter, correction.Parameter);
        Assert.Equal("Grik", correction.DungeonMasterValue);
        Assert.Equal("Aric", correction.CorrectedValue);

        // Corrected to the acting character, so the attack resolves normally against the real target.
        var engineAction = Assert.Single(harness.Sink.Payloads<EngineActionPayload>(TraceEventType.EngineAction));
        Assert.True(engineAction.Accepted);
        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
        Assert.Equal(5, harness.Engine.State.RequireById(TestWorld.MonsterId).Health);
    }

    [Fact]
    public async Task A_character_that_targets_itself_is_rejected_by_the_engine_and_may_try_again()
    {
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Aric"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("You cannot turn your own blade on yourself."),
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("The blade lands.")),
            new ScriptedChatClient(
                ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike at Aric.")),
                ScriptedChatClient.Call("h-2", CharacterTools.TakeActionName, ("intent", "I strike at the goblin."))),
            new ScriptedChatClient());

        var result = await harness.RunHeroTurn();

        var engineActions = harness.Sink.Payloads<EngineActionPayload>(TraceEventType.EngineAction).ToList();
        Assert.Equal(EngineRejectionReason.TargetIsSelf.ToString(), engineActions[0].RejectionReason);
        Assert.True(engineActions[1].Accepted);
        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
    }

    [Fact]
    public async Task Adjudication_runs_on_a_context_free_of_narration_history()
    {
        const string chatter = "It keeps glancing nervously towards the exit.";

        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Text(chatter),
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("Your blade opens a gash across its shoulder.")),
            new ScriptedChatClient(
                ScriptedChatClient.Call("h-1", CharacterTools.AskDmName, ("question", "Is it frightened?")),
                ScriptedChatClient.Call("h-2", CharacterTools.TakeActionName, ("intent", "I strike at it."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var adjudication = harness.Sink.Payloads<ModelRequestPayload>(TraceEventType.ModelRequest)
            .First(r => r.Purpose == "dm.adjudicate");

        // System prompt plus the adjudication task, and nothing else.
        Assert.Equal(2, adjudication.Messages.Count);
        Assert.DoesNotContain(chatter, string.Join("\n", adjudication.Messages.Select(m => m.Text)), StringComparison.Ordinal);

        // Narration keeps its continuity: the outcome call still sees the earlier exchange.
        var narration = harness.Sink.Payloads<ModelRequestPayload>(TraceEventType.ModelRequest)
            .First(r => r.Purpose == "dm.narrate.outcome");
        Assert.Contains(chatter, string.Join("\n", narration.Messages.Select(m => m.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adjudication_can_be_configured_to_share_the_dungeon_masters_history()
    {
        const string chatter = "It keeps glancing nervously towards the exit.";

        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Text(chatter),
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("Your blade opens a gash across its shoulder.")),
            new ScriptedChatClient(
                ScriptedChatClient.Call("h-1", CharacterTools.AskDmName, ("question", "Is it frightened?")),
                ScriptedChatClient.Call("h-2", CharacterTools.TakeActionName, ("intent", "I strike at it."))),
            new ScriptedChatClient(),
            new HarnessOptions
            {
                IsolateAdjudicationContext = false,
                MaxQuestionsPerTurn = 2,
                MaxActionAttemptsPerTurn = 3,
                MaxModelCallsPerTurn = 8
            });

        await harness.RunHeroTurn();

        var adjudication = harness.Sink.Payloads<ModelRequestPayload>(TraceEventType.ModelRequest)
            .First(r => r.Purpose == "dm.adjudicate");

        Assert.True(adjudication.Messages.Count > 2);
        Assert.Contains(chatter, string.Join("\n", adjudication.Messages.Select(m => m.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dead_character_does_not_take_a_turn()
    {
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(),
            new ScriptedChatClient(),
            new ScriptedChatClient(),
            initialState: TestWorld.State(TestWorld.Hero(health: 0), TestWorld.Monster()));

        var result = await harness.RunHeroTurn();

        Assert.Equal(TurnOutcome.Skipped, result.Outcome);
        Assert.Equal(0, harness.HeroClient.CallCount);
    }

    private static string Flatten(ModelAgent agent) =>
        string.Join("\n", agent.Conversation.BuildRequestMessages().SelectMany(Describe));

    private static IEnumerable<string> Describe(ChatMessage message) =>
        message.Contents.Select(content => content switch
        {
            TextContent text => text.Text,
            FunctionCallContent call => $"{call.Name}({string.Join(",", call.Arguments?.Values ?? [])})",
            FunctionResultContent result => result.Result?.ToString() ?? "",
            _ => ""
        });
}
