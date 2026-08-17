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

        // The hero's private exchange came back to the hero, and the DM answered it (recorded in the
        // trace, since the DM keeps no single retained conversation once it projects per task).
        Assert.Contains(heroSecret, heroHistory, StringComparison.Ordinal);
        Assert.Contains(
            harness.Sink.Payloads<DungeonMasterAnswerPayload>(TraceEventType.DungeonMasterAnswer),
            a => a.CharacterName == "Aric" && a.Answer == heroSecret);

        // The monster never sees the hero's private question or answer — only public narration.
        Assert.DoesNotContain(heroSecret, monsterHistory, StringComparison.Ordinal);
        Assert.DoesNotContain("Can I tell if it is frightened?", monsterHistory, StringComparison.Ordinal);
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
    public async Task Each_dungeon_master_task_runs_on_its_own_projection_free_of_prior_task_history()
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

        // Adjudication is a fresh projection: system prompt plus the adjudication task, nothing else.
        var adjudication = harness.Sink.Payloads<ModelRequestPayload>(TraceEventType.ModelRequest)
            .First(r => r.Purpose == "dm.adjudicate");
        Assert.Equal(2, adjudication.Messages.Count);
        Assert.DoesNotContain(chatter, string.Join("\n", adjudication.Messages.Select(m => m.Text)), StringComparison.Ordinal);

        // Outcome narration is also a projection, and does not carry the earlier private Q&A.
        var narration = harness.Sink.Payloads<ModelRequestPayload>(TraceEventType.ModelRequest)
            .First(r => r.Purpose == "dm.narrate.outcome");
        Assert.Equal(2, narration.Messages.Count);
        Assert.DoesNotContain(chatter, string.Join("\n", narration.Messages.Select(m => m.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projected_dungeon_master_context_does_not_grow_across_turns()
    {
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Text("The chamber is cramped and tense."),                          // opening
                ScriptedChatClient.Call("dm-h", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("Aric's blade opens a gash across the goblin's shoulder."),     // hero outcome
                ScriptedChatClient.Call("dm-m", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Grik"), ("target", "Aric"), ("weapon", "Rusty Axe")),
                ScriptedChatClient.Text("The rusty axe bites into Aric's arm.")),                       // monster outcome
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike."))),
            new ScriptedChatClient(ScriptedChatClient.Call("m-1", CharacterTools.TakeActionName, ("intent", "I swing."))));

        await harness.Coordinator.NarrateSituationAsync("Opening.", "opening", CancellationToken.None);
        await harness.RunHeroTurn();
        await harness.RunMonsterTurn();

        // Every DM projection stays at a fixed, small size — the continuity hint is folded into the one
        // task message, so narration and answering are always exactly system + task.
        var dmRequests = harness.Sink.Payloads<ModelRequestPayload>(TraceEventType.ModelRequest)
            .Where(r => r.AgentName == "DungeonMaster")
            .ToList();

        Assert.All(dmRequests, r => Assert.True(
            r.Messages.Count <= 2,
            $"DM request '{r.Purpose}' sent {r.Messages.Count} messages; projections must not accumulate history."));

        // The later monster-outcome narration is no larger than the first opening narration.
        var narrations = dmRequests.Where(r => r.Purpose is "opening" or "dm.narrate.outcome").ToList();
        Assert.Equal(narrations.First().Messages.Count, narrations.Last().Messages.Count);
    }

    [Fact]
    public async Task The_dungeon_master_can_be_configured_to_use_one_growing_conversation()
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
                ProjectDungeonMasterContext = false,
                MaxQuestionsPerTurn = 2,
                MaxActionAttemptsPerTurn = 3,
                MaxModelCallsPerTurn = 8
            });

        await harness.RunHeroTurn();

        // Old behaviour: one growing conversation, so adjudication sees the earlier Q&A.
        var adjudication = harness.Sink.Payloads<ModelRequestPayload>(TraceEventType.ModelRequest)
            .First(r => r.Purpose == "dm.adjudicate");
        Assert.True(adjudication.Messages.Count > 2);
        Assert.Contains(chatter, string.Join("\n", adjudication.Messages.Select(m => m.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_truncated_reply_is_recorded_as_truncation_rather_than_a_protocol_failure()
    {
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(
                ScriptedChatClient.Truncated("I raise my sword and step towards the goblin, thinking that if I"),
                ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike it."))),
            new ScriptedChatClient());

        var result = await harness.RunHeroTurn();

        var truncation = Assert.Single(
            harness.Sink.Payloads<ModelTruncatedPayload>(TraceEventType.ModelResponseTruncated));
        Assert.Equal("Aric", truncation.AgentName);
        Assert.Equal("character.decide", truncation.Purpose);
        Assert.False(truncation.HadToolCalls);
        Assert.Equal(500, truncation.OutputTokenCount);

        // The cause is named accurately: running out of room is not the same as ignoring the protocol.
        var error = Assert.Single(harness.Sink.Payloads<ToolCallErrorPayload>(TraceEventType.ToolCallError));
        Assert.Contains("truncated", error.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("responded without calling", error.Error, StringComparison.OrdinalIgnoreCase);

        // The turn still recovers on the next reply.
        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
        Assert.Contains(harness.Console.Lines, l => l.Contains("output-token limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_truncated_reply_that_kept_its_tool_call_is_still_recorded()
    {
        var truncatedWithCall = new Microsoft.Extensions.AI.ChatResponse(
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant,
                [ScriptedChatClient.CallContent("h-1", CharacterTools.TakeActionName, ("intent", "I strike it."))]))
        {
            FinishReason = Microsoft.Extensions.AI.ChatFinishReason.Length
        };

        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(truncatedWithCall),
            new ScriptedChatClient());

        var result = await harness.RunHeroTurn();

        var truncation = Assert.Single(
            harness.Sink.Payloads<ModelTruncatedPayload>(TraceEventType.ModelResponseTruncated));
        Assert.True(truncation.HadToolCalls);

        // The surviving tool call is still honoured, so the turn resolves normally.
        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
    }

    [Fact]
    public async Task A_reasoning_model_that_produced_only_thinking_is_flagged_distinctly()
    {
        // The failure mode of a reasoning model on a small budget: the whole output is a reasoning
        // block, so there is no tool call and no prose. It must read as a budget/thinking problem, not
        // as the character ignoring its protocol.
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(
                ScriptedChatClient.ReasoningOnly("Thinking Process: I should consider whether to strike or wait..."),
                ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike."))),
            new ScriptedChatClient());

        var result = await harness.RunHeroTurn();

        var truncation = Assert.Single(
            harness.Sink.Payloads<ModelTruncatedPayload>(TraceEventType.ModelResponseTruncated));
        Assert.True(truncation.ReasoningOnly);
        Assert.False(truncation.HadToolCalls);
        Assert.Contains("thinking", truncation.Effect, StringComparison.OrdinalIgnoreCase);

        // The turn still recovers on the next, complete reply.
        Assert.Equal(TurnOutcome.ActionResolved, result.Outcome);
    }

    [Fact]
    public async Task A_normal_reply_records_no_truncation()
    {
        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike."))),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        Assert.Empty(harness.Sink.OfType(TraceEventType.ModelResponseTruncated));
    }

    [Fact]
    public async Task Truncation_is_detected_when_the_provider_reports_processing_far_less_than_we_sent()
    {
        // The blog's method: the provider reporting a tiny input size for a real system-prompt-plus-turn
        // request is the evidence that it silently dropped the rest — no configured window involved.
        var saturated = ScriptedChatClient.WithInputTokens(
            ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike.")),
            reportedInputTokens: 40);

        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(saturated),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        var event_ = Assert.Single(
            harness.Sink.Payloads<ContextSaturationPayload>(TraceEventType.ContextWindowSaturated));
        Assert.Equal("Aric", event_.AgentName);
        Assert.Equal("character.decide", event_.Purpose);
        Assert.Equal(40, event_.ReportedInputTokens);
        Assert.True(event_.EstimatedSentTokens > event_.ReportedInputTokens);
        Assert.Equal(event_.EstimatedSentTokens - 40, event_.EstimatedDroppedTokens);
        Assert.Contains("discarded", event_.Effect, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_truncation_is_reported_when_the_provider_processed_what_we_sent()
    {
        // Reported input at or above what we sent means nothing was dropped.
        var comfortable = ScriptedChatClient.WithInputTokens(
            ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I strike.")),
            reportedInputTokens: 100_000);

        var harness = new OrchestrationHarness(
            AcceptedAttackDungeonMaster(),
            new ScriptedChatClient(comfortable),
            new ScriptedChatClient());

        await harness.RunHeroTurn();

        Assert.Empty(harness.Sink.OfType(TraceEventType.ContextWindowSaturated));
    }

    [Fact]
    public async Task A_dungeon_master_tool_call_written_as_text_never_leaks_raw_json_to_the_character()
    {
        const string leaked =
            "{\"name\": \"reject_action\", \"parameters\": {\"category\": \"unsupported\", " +
            "\"reason\": \"There is nowhere in this cramped room to open distance.\"}}";

        // The DM emits the reject as text on both the first attempt and the retry, so no real tool call
        // is ever produced; the hero then gives up and ends its turn (which the DM narrates).
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Text(leaked),
                ScriptedChatClient.Text(leaked),
                ScriptedChatClient.Text("Aric lowers his blade and holds still.")),
            new ScriptedChatClient(
                ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName, ("intent", "I back away.")),
                ScriptedChatClient.Call("h-2", CharacterTools.EndTurnName, ("reason", "There is nothing to be done."))),
            new ScriptedChatClient(),
            new HarnessOptions
            {
                MaxAdjudicationRetries = 1,
                MaxQuestionsPerTurn = 2,
                MaxActionAttemptsPerTurn = 3,
                MaxModelCallsPerTurn = 6
            });

        await harness.RunHeroTurn();

        // What the character was handed carries the DM's reason, not the JSON.
        var toCharacter = harness.Sink.Payloads<ToolCallResultPayload>(TraceEventType.ToolCallResult)
            .First(r => r.AgentName == "Aric" && r.ToolName == CharacterTools.TakeActionName)
            .Result!.ToString()!;
        Assert.Contains("nowhere in this cramped room", toCharacter, StringComparison.Ordinal);
        Assert.DoesNotContain("reject_action", toCharacter, StringComparison.Ordinal);
        Assert.DoesNotContain("{", toCharacter, StringComparison.Ordinal);

        // The DM's original text is still recorded verbatim in the trace, so the leak stays diagnosable.
        var adjudication = harness.Sink.Payloads<DmAdjudicationPayload>(TraceEventType.DmAdjudication).Last();
        Assert.Contains("reject_action", adjudication.DungeonMasterText!, StringComparison.Ordinal);
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
