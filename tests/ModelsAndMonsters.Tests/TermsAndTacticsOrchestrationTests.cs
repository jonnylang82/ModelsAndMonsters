using Microsoft.Extensions.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Knowledge;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Randomness;
using ModelsAndMonsters.Rulebook;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.7 slice driven through the real <see cref="TurnCoordinator"/>: abilities and statuses, the grounded
/// answer projection, the post-resolution output guard, and the negotiation corrections the harness makes on
/// the Dungeon Master's behalf. Only the model is scripted; the engine, tracing, knowledge ledger and prompts
/// are real.
/// </summary>
public sealed class TermsAndTacticsOrchestrationTests
{
    private static ChatResponse Act(string id, string intent) =>
        ScriptedChatClient.Call(id, CharacterTools.TakeActionName, ("intent", intent));

    private static ChatResponse DmUseAbility(string actor, string ability, string? target = null) =>
        target is null
            ? ScriptedChatClient.Call("dm-ability", DungeonMasterTools.UseAbilityName, ("actor", actor), ("ability", ability))
            : ScriptedChatClient.Call("dm-ability", DungeonMasterTools.UseAbilityName,
                ("actor", actor), ("ability", ability), ("target", target));

    private static ChatResponse DmDefend(string actor) =>
        ScriptedChatClient.Call("dm-defend", DungeonMasterTools.DefendName, ("actor", actor));

    private static ChatResponse DmAttack(string attacker, string target, string weapon) =>
        ScriptedChatClient.Call("dm-attack", DungeonMasterTools.AttackCharacterName,
            ("attacker", attacker), ("target", target), ("weapon", weapon));

    private static MultiActorHarness Harness(
        ScriptedChatClient dungeonMaster,
        Dictionary<string, ScriptedChatClient> characters,
        GameState? state = null,
        IRng? rng = null,
        CombatRules? rules = null,
        HarnessOptions? limits = null) =>
        new(dungeonMaster, characters, limits, state ?? TestWorld.V07State(),
            rng, rules ?? CombatRules.NoGlancing, TestWorld.V07Scenario());

    // ------------------------------------------------------------------------------------------
    // Abilities through the coordinator
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Guard_Ally_resolves_publicly_and_records_both_halves_of_the_status()
    {
        var harness = Harness(
            new ScriptedChatClient(
                DmUseAbility("Rowan", AbilityCatalog.GuardAllyId, "Elara"),
                ScriptedChatClient.Text("Rowan plants himself in front of Elara, blade up.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                Act("r-1", "I put myself in front of Elara and take whatever comes at her.")))));

        var turn = await harness.RunTurn("Rowan", round: 1, turn: 1);

        Assert.Equal(TurnOutcome.ActionResolved, turn.Outcome);

        // Both halves of the relationship were applied and traced, with the source named on each.
        var applied = harness.Sink.Payloads<StatusEffectPayload>(TraceEventType.StatusApplied).ToList();
        Assert.Equal(2, applied.Count);
        Assert.Contains(applied, s => s.Kind == nameof(StatusEffectKind.Guarding) && s.TargetCharacterName == "Rowan");
        Assert.Contains(applied, s => s.Kind == nameof(StatusEffectKind.Guarded) && s.TargetCharacterName == "Elara");
        Assert.All(applied, s => Assert.Equal("Rowan", s.SourceCharacterName));
        Assert.All(applied, s => Assert.Equal(AbilityCatalog.GuardAllyId, s.SourceAbilityId));
        Assert.Single(applied.Select(s => s.RelationshipId).Distinct());

        // The ability itself is recorded as accepted, with the charge column untouched (it is unlimited).
        var used = Assert.Single(harness.Sink.Payloads<AbilityUsedPayload>(TraceEventType.AbilityUsed));
        Assert.Equal("accepted", used.ValidationResult);
        Assert.Equal("Guard Ally", used.AbilityName);
        Assert.Equal("Elara", used.TargetName);
        Assert.Null(used.RemainingUsesAfter);
        Assert.False(used.RngConsulted);
        Assert.True(used.TurnConsumed);
    }

    [Fact]
    public async Task A_refused_ability_use_is_traced_with_its_charge_untouched()
    {
        var harness = Harness(
            new ScriptedChatClient(
                // Elara's prayer aimed at an unwounded Rowan: refused, and the one charge must survive.
                DmUseAbility("Elara", AbilityCatalog.HealingPrayerId, "Rowan"),
                ScriptedChatClient.Text("There is no wound there for the prayer to close."),
                ScriptedChatClient.Text("Elara lowers her hand and says nothing more.")),
            MultiActorHarness.Clients(("Elara", new ScriptedChatClient(
                Act("e-1", "I say the prayer over Rowan's shoulder."),
                ScriptedChatClient.Call("e-2", CharacterTools.EndTurnName, ("reason", "I save it, then."))))));

        await harness.RunTurn("Elara", round: 1, turn: 2);

        var used = Assert.Single(harness.Sink.Payloads<AbilityUsedPayload>(TraceEventType.AbilityUsed));
        Assert.Equal("rejected", used.ValidationResult);
        Assert.Equal(nameof(EngineRejectionReason.TargetAlreadyAtFullHealth), used.RejectionReason);
        Assert.Equal(1, used.RemainingUsesBefore);
        Assert.Equal(1, used.RemainingUsesAfter);
        Assert.False(used.TurnConsumed);

        // And the authoritative charge really is intact.
        Assert.Equal(1, harness.Engine.State.RequireById(TestWorld.ElaraId)
            .FindAbility(AbilityCatalog.HealingPrayerId)!.RemainingUses);
    }

    [Fact]
    public async Task A_redirected_blow_is_traced_with_both_targets_and_the_ordinary_draw_count()
    {
        var state = TestWorld.V07State();
        var harness = Harness(
            new ScriptedChatClient(
                DmUseAbility("Rowan", AbilityCatalog.GuardAllyId, "Elara"),
                ScriptedChatClient.Text("Rowan steps over Elara."),
                DmAttack("Vark", "Elara", "Notched Sabre"),
                ScriptedChatClient.Text("The sabre comes down and Rowan wears it in her place.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(Act("r-1", "I shield Elara."))),
                ("Vark", new ScriptedChatClient(Act("v-1", "I cut at the cleric.")))),
            state,
            new ScriptedRng(ScriptedRng.Hits, ScriptedRng.Solid),
            CombatRules.Default);

        await harness.RunTurn("Rowan", round: 1, turn: 1);
        await harness.RunTurn("Vark", round: 1, turn: 3);

        var redirect = Assert.Single(harness.Sink.Payloads<AttackRedirectedPayload>(TraceEventType.AttackRedirected));
        Assert.Equal("Vark", redirect.AttackerName);
        Assert.Equal("Elara", redirect.IntendedTargetName);
        Assert.Equal("Rowan", redirect.AuthoritativeTargetName);
        Assert.Equal(2, redirect.TargetArmourUsed);          // the guardian's armour, not the guarded ally's
        Assert.Equal(2, redirect.RngDrawCount);              // one to hit, one for glancing. Nothing extra.

        // Elara took nothing at all.
        Assert.Equal(6, harness.Engine.State.RequireById(TestWorld.ElaraId).Health);

        // And the target resolution recorded the intent honestly rather than silently retargeting.
        var resolution = Assert.Single(harness.Sink.Payloads<TargetResolutionPayload>(TraceEventType.TargetResolved));
        Assert.Equal("Elara", resolution.ResolvedTargetName);
    }

    [Fact]
    public async Task Defend_resolves_through_its_own_tool_and_expires_at_the_start_of_the_next_turn()
    {
        var harness = Harness(
            new ScriptedChatClient(
                DmDefend("Rowan"),
                ScriptedChatClient.Text("Rowan sets his feet and brings his guard up."),
                ScriptedChatClient.Text("Rowan waits, watching them both.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                Act("r-1", "I set my feet and keep my guard up."),
                ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "I hold where I am."))))));

        await harness.RunTurn("Rowan", round: 1, turn: 1);
        Assert.NotNull(harness.Engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.Defending));

        // Rowan's next turn begins: upkeep expires the guard before he sees his own state.
        await harness.RunTurn("Rowan", round: 2, turn: 5);

        Assert.Null(harness.Engine.State.StatusOn(TestWorld.RowanId, StatusEffectKind.Defending));
        var expired = Assert.Single(harness.Sink.Payloads<StatusEffectPayload>(TraceEventType.StatusExpired));
        Assert.Equal(nameof(StatusEffectKind.Defending), expired.Kind);
        Assert.Contains("turn-upkeep (turn-start)", expired.RelatedAction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_characters_own_state_names_its_abilities_remaining_uses_and_live_statuses()
    {
        var state = TestWorld.V07State();
        var harness = Harness(
            new ScriptedChatClient(ScriptedChatClient.Text("Elara steadies herself.")),
            MultiActorHarness.Clients(("Elara", new ScriptedChatClient(
                ScriptedChatClient.Call("e-1", CharacterTools.EndTurnName, ("reason", "I catch my breath."))))),
            state.WithStatus(new StatusEffectInstance
            {
                Id = "status-fixture",
                Kind = StatusEffectKind.Guarded,
                SourceCharacterId = TestWorld.RowanId,
                TargetCharacterId = TestWorld.ElaraId,
                AppliedRound = 1,
                AppliedTurn = 1,
                Modifier = 0,
                ExpiryRule = StatusExpiryRule.StartOfSourceNextTurn,
                RelationshipId = "guard-fixture"
            }));

        await harness.RunTurn("Elara", round: 1, turn: 2);

        var turnStart = Assert.Single(harness.Sink.Payloads<TurnStartedPayload>(TraceEventType.TurnStarted));
        Assert.Contains("Healing Prayer (1 use left)", turnStart.SelfStateBlock, StringComparison.Ordinal);
        Assert.Contains("Rowan is standing over you", turnStart.SelfStateBlock, StringComparison.Ordinal);
        Assert.Contains("Nobody has offered terms", turnStart.SelfStateBlock, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // Grounded answers: the Dungeon Master is not given the state to answer from
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_answering_request_carries_the_projection_and_not_the_authoritative_state()
    {
        var state = TestWorld.StateWith([TestWorld.MedicineCase()],
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());
        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Text("You have never had that lid up; from where you stand it may as well be solid."),
                ScriptedChatClient.Text("Rowan waits.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.AskDmName, ("question", "What is inside that case?")),
                ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "Nothing to be gained."))))),
            state);

        await harness.RunTurn("Rowan", round: 1, turn: 1);

        var answering = harness.DungeonMasterClient.Requests[0];
        var task = string.Join("\n", answering.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        var whole = string.Join("\n", answering.Select(m => m.Text));

        // The task the DM was handed carries no authoritative snapshot at all: the markers of the state block
        // are absent. (The DM's own constitution is in the system message and is not state.)
        Assert.DoesNotContain("STATE NOTES:", task, StringComparison.Ordinal);
        Assert.DoesNotContain("Authoritative contents", task, StringComparison.Ordinal);
        Assert.DoesNotContain("CHARACTERS:", task, StringComparison.Ordinal);

        // Nor do the hidden contents reach it by any route, in any message.
        Assert.DoesNotContain("Small Healing Potion", whole, StringComparison.Ordinal);

        // What it does carry is the projection, including the closed affordance list and the world's absences.
        Assert.Contains("FACTS Rowan MAY BE ANSWERED FROM", task, StringComparison.Ordinal);
        Assert.Contains("No other kind of action exists in this world", task, StringComparison.Ordinal);
        Assert.Contains("no position, distance, facing, movement or spacing", task, StringComparison.Ordinal);

        var projected = Assert.Single(harness.Sink.Payloads<AnswerFactsPayload>(TraceEventType.AnswerFactsProjected));
        Assert.True(projected.FullStateWithheld);
        Assert.True(projected.AffordanceCount > 0);
        Assert.Contains("contents of the Faded Shrine Medicine Case", projected.OmittedHiddenFacts, StringComparison.Ordinal);

        // The answer event records both the projection and what was withheld from it.
        var answer = Assert.Single(harness.Sink.Payloads<DungeonMasterAnswerPayload>(TraceEventType.DungeonMasterAnswer));
        Assert.NotNull(answer.ProjectedFacts);
        Assert.NotNull(answer.OmittedHiddenFacts);
    }

    [Fact]
    public async Task A_mistaken_dungeon_master_answer_cannot_bypass_the_engine_knowledge_gate()
    {
        // The scripted Dungeon Master leaks the case's contents in an answer — the exact v0.6 failure — and
        // then translates Rowan's reach for it. The deterministic basis gate refuses the take regardless.
        var state = TestWorld.StateWith([TestWorld.MedicineCase(open: true)],
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());
        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Text("The case holds a small healing potion."),      // the leak
                ScriptedChatClient.Call("dm-take", DungeonMasterTools.TakeItemName,
                    ("actor", "Rowan"), ("container", "Faded Shrine Medicine Case"), ("item", "Small Healing Potion")),
                ScriptedChatClient.Text("Rowan draws his hand back from the case.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.AskDmName, ("question", "What is in the case?")),
                Act("r-2", "I take the small healing potion out of the case."),
                ScriptedChatClient.Call("r-3", CharacterTools.EndTurnName, ("reason", "I cannot lay hands on it."))))),
            state);

        var turn = await harness.RunTurn("Rowan", round: 1, turn: 1);

        // The take never reached the engine, and nothing moved.
        Assert.Empty(harness.Sink.OfType(TraceEventType.EngineAction));
        Assert.Empty(harness.Engine.State.RequireById(TestWorld.RowanId).Inventory.Where(i => i.Id == "small-healing-potion"));
        var container = Assert.Single(harness.Engine.State.Room.Objects.OfType<Container>());
        Assert.Contains(container.Contents, i => i.Id == "small-healing-potion");
        Assert.NotEqual(TurnOutcome.ActionResolved, turn.Outcome);
    }

    [Fact]
    public async Task A_theft_with_no_informational_basis_is_refused_before_any_roll()
    {
        // The ledger is left unseeded, so Skrit has no way of knowing what Elara carries.
        var state = TestWorld.V07State();
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-steal", DungeonMasterTools.StealItemName,
                    ("thief", "Skrit"), ("target", "Elara"), ("item", "purse-elara")),
                ScriptedChatClient.Text("Skrit's hand hovers and comes back empty.")),
            MultiActorHarness.Clients(("Skrit", new ScriptedChatClient(
                Act("s-1", "I cut the cleric's purse from her belt."),
                ScriptedChatClient.Call("s-2", CharacterTools.EndTurnName, ("reason", "I keep my hands to myself."))))),
            initialState: state,
            rng: new ScriptedRng(),   // any draw at all would throw
            scenario: TestWorld.V07Scenario(),
            seedKnowledge: false);

        await harness.RunTurn("Skrit", round: 1, turn: 4);

        Assert.Empty(harness.Sink.OfType(TraceEventType.EngineAction));
        Assert.Empty(harness.Sink.OfType(TraceEventType.RngDraw));
        var interaction = Assert.Single(harness.Sink.Payloads<InventoryInteractionPayload>(TraceEventType.InventoryInteraction));
        Assert.Equal("NoInformationalBasis", interaction.RejectionReason);
        Assert.False(interaction.RngConsulted);
    }

    // ------------------------------------------------------------------------------------------
    // The post-resolution output guard
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Output_after_an_accepted_action_is_discarded_and_creates_no_state_or_knowledge()
    {
        // Skrit attacks and, in the same reply, declares a theft. The attack resolves the turn; the theft is
        // discarded, and must not reach the engine, the ledger or the transcript.
        var harness = Harness(
            new ScriptedChatClient(
                DmAttack("Skrit", "Elara", "Crude Spear"),
                ScriptedChatClient.Text("The spear scrapes across her ribs.")),
            MultiActorHarness.Clients(("Skrit", new ScriptedChatClient(
                ScriptedChatClient.Calls(
                    ScriptedChatClient.CallContent("s-1", CharacterTools.TakeActionName, ("intent", "I jab my spear at the cleric.")),
                    ScriptedChatClient.CallContent("s-2", CharacterTools.TakeActionName, ("intent", "And I cut her purse free as she reels.")))))));

        var turn = await harness.RunTurn("Skrit", round: 1, turn: 4);

        Assert.Equal(TurnOutcome.ActionResolved, turn.Outcome);

        // Exactly one action reached the engine: the attack.
        var engineAction = Assert.Single(harness.Sink.Payloads<EngineActionPayload>(TraceEventType.EngineAction));
        Assert.Equal("attack_character", engineAction.ActionType);

        // The discarded declaration is recorded verbatim, and nothing came of it.
        var discarded = Assert.Single(
            harness.Sink.Payloads<PostResolutionOutputDiscardedPayload>(TraceEventType.PostResolutionOutputDiscarded));
        Assert.Equal("tool-call", discarded.DiscardedKind);
        Assert.Contains("cut her purse free", discarded.DiscardedContent, StringComparison.Ordinal);
        Assert.True(discarded.StateUnchanged);
        Assert.Contains("AttackCharacter", discarded.ResolvedAction, StringComparison.Ordinal);

        // No second adjudication, no inventory interaction, no knowledge fact from the discarded words.
        Assert.Single(harness.Sink.OfType(TraceEventType.DmAdjudication));
        Assert.Empty(harness.Sink.OfType(TraceEventType.InventoryInteraction));
        Assert.Empty(harness.Engine.State.RequireById(TestWorld.SkritId).Inventory.Where(i => i.Id == "purse-elara"));

        // And nothing about it went onto the public channel.
        Assert.DoesNotContain(harness.NarrationLog.Entries, e => e.Text.Contains("purse", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(harness.Console.Lines, l => l.Contains("cut her purse free", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Speech_made_before_the_accepted_action_survives_the_discard()
    {
        var harness = Harness(
            new ScriptedChatClient(
                DmAttack("Skrit", "Elara", "Crude Spear"),
                ScriptedChatClient.Text("The spear scrapes across her ribs.")),
            MultiActorHarness.Clients(("Skrit", new ScriptedChatClient(
                ScriptedChatClient.Calls(
                    ScriptedChatClient.CallContent("s-say", CharacterTools.SayName, ("message", "Captain! The cleric is the weak one!")),
                    ScriptedChatClient.CallContent("s-1", CharacterTools.TakeActionName, ("intent", "I jab my spear at the cleric.")),
                    ScriptedChatClient.CallContent("s-2", CharacterTools.TakeActionName, ("intent", "And I snatch her purse.")))))));

        var turn = await harness.RunTurn("Skrit", round: 1, turn: 4);

        Assert.Equal(1, turn.SpeechActs);
        var speech = Assert.Single(harness.Sink.Payloads<CharacterSpeechPayload>(TraceEventType.CharacterSpeech));
        Assert.Equal("Captain! The cleric is the weak one!", speech.Message);
        Assert.Contains(harness.NarrationLog.Entries, e => e.Text.Contains("the weak one", StringComparison.Ordinal));

        // The trailing action was still discarded.
        Assert.Single(harness.Sink.OfType(TraceEventType.PostResolutionOutputDiscarded));
    }

    // ------------------------------------------------------------------------------------------
    // Negotiation corrections and public bookkeeping
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_offer_reference_the_dungeon_master_paraphrases_is_corrected_when_only_one_offer_is_open()
    {
        var offer = new SurrenderOffer
        {
            Id = "offer-1",
            OffererId = TestWorld.VarkId,
            RecipientId = TestWorld.RowanId,
            OfferedItemIds = ["purse-vark"],
            ForfeitWeapon = true,
            CreatedRound = 1,
            CreatedTurn = 3,
            State = SurrenderOfferState.Pending
        };

        var harness = Harness(
            new ScriptedChatClient(
                // The DM names the offer in prose instead of copying its id.
                ScriptedChatClient.Call("dm-accept", DungeonMasterTools.AcceptSurrenderName,
                    ("recipient", "Rowan"), ("offer", "the goblin captain's offer")),
                ScriptedChatClient.Text("Rowan takes the purse and the sabre, and lets the captain live.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                Act("r-1", "I take his gold and his blade, and tell him he lives.")))),
            TestWorld.V07State() with { SurrenderOffers = [offer] });

        var turn = await harness.RunTurn("Rowan", round: 2, turn: 5);

        Assert.Equal(TurnOutcome.ActionResolved, turn.Outcome);
        var correction = Assert.Single(harness.Sink.Payloads<AdjudicationCorrectionPayload>(TraceEventType.AdjudicationCorrected)
            .Where(c => c.Parameter == DungeonMasterTools.OfferParameter));
        Assert.Equal("the goblin captain's offer", correction.DungeonMasterValue);
        Assert.Equal("offer-1", correction.CorrectedValue);

        Assert.Equal(CharacterDisposition.Surrendered, harness.Engine.State.RequireById(TestWorld.VarkId).Disposition);
        Assert.Single(harness.Sink.Payloads<SurrenderAgreementPayload>(TraceEventType.SurrenderAgreementRecorded));
    }

    [Fact]
    public async Task Reaching_for_the_promised_tribute_is_an_acceptance_even_when_the_dungeon_master_calls_it_a_theft()
    {
        // The Rulebook Resolver never sees the world, so it cannot tell a grab from an acceptance: in a live
        // v0.7 run the recipient of two offers said "I take Rowan's purse, telling him he may live", the
        // resolver cited inventory.steal, and the steal counted as a hostile act that destroyed the very
        // offer being accepted. accept_surrender was never once offered to the Dungeon Master. The redirect
        // is what makes that impossible regardless of which tool the model reaches for.
        var offer = new SurrenderOffer
        {
            Id = "offer-1",
            OffererId = TestWorld.VarkId,
            RecipientId = TestWorld.RowanId,
            OfferedItemIds = ["purse-vark"],
            ForfeitWeapon = false,
            CreatedRound = 1,
            CreatedTurn = 3,
            State = SurrenderOfferState.Pending
        };

        var harness = Harness(
            new ScriptedChatClient(
                // The DM rules it a theft of exactly what the offer promised.
                ScriptedChatClient.Call("dm-steal", DungeonMasterTools.StealItemName,
                    ("thief", "Rowan"), ("target", "Vark"), ("item", "purse-vark")),
                ScriptedChatClient.Text("Rowan takes the captain's purse and tells him he may live.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                Act("r-1", "I take the purse from Vark's hand and tell him he may live.")))),
            TestWorld.V07State() with { SurrenderOffers = [offer] });

        var turn = await harness.RunTurn("Rowan", round: 2, turn: 5);

        Assert.Equal(TurnOutcome.ActionResolved, turn.Outcome);

        // It resolved as the acceptance it was: the offerer is out of the fight, not merely one purse poorer.
        Assert.Equal(CharacterDisposition.Surrendered, harness.Engine.State.RequireById(TestWorld.VarkId).Disposition);
        Assert.Single(harness.Sink.Payloads<SurrenderAgreementPayload>(TraceEventType.SurrenderAgreementRecorded));
        Assert.Equal(SurrenderOfferState.Accepted, harness.Engine.State.FindOffer("offer-1")!.State);

        // No theft happened, so no theft roll was made.
        Assert.Empty(harness.Sink.OfType(TraceEventType.RngDraw));

        // And the redirect is auditable rather than silent.
        var dispatch = harness.Sink.Payloads<ToolCallDispatchPayload>(TraceEventType.ToolCallDispatched)
            .First(p => p.ToolName == DungeonMasterTools.StealItemName);
        Assert.Contains("Redirected to accept_surrender", dispatch.DispatchDecision, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reaching_for_something_the_offer_did_not_promise_is_still_an_ordinary_theft()
    {
        // The redirect is deliberately narrow. Vark promised his purse; grabbing his salve instead is not an
        // acceptance of anything, and it must stay the hostile act that kills the offer.
        var offer = new SurrenderOffer
        {
            Id = "offer-1",
            OffererId = TestWorld.VarkId,
            RecipientId = TestWorld.RowanId,
            OfferedItemIds = ["purse-vark"],
            ForfeitWeapon = false,
            CreatedRound = 1,
            CreatedTurn = 3,
            State = SurrenderOfferState.Pending
        };

        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-steal", DungeonMasterTools.StealItemName,
                    ("thief", "Rowan"), ("target", "Vark"), ("item", "goblin-salve")),
                ScriptedChatClient.Text("Rowan snatches at the vial and comes away with it.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                Act("r-1", "I lunge and snatch the vial of salve from his neck.")))),
            TestWorld.V07State() with { SurrenderOffers = [offer] },
            rng: new ScriptedRng(1));

        var turn = await harness.RunTurn("Rowan", round: 2, turn: 5);

        Assert.Equal(TurnOutcome.ActionResolved, turn.Outcome);
        Assert.Equal(CharacterDisposition.Active, harness.Engine.State.RequireById(TestWorld.VarkId).Disposition);
        Assert.Equal(SurrenderOfferState.Rejected, harness.Engine.State.FindOffer("offer-1")!.State);
        Assert.Empty(harness.Sink.Payloads<SurrenderAgreementPayload>(TraceEventType.SurrenderAgreementRecorded));
    }

    [Fact]
    public async Task The_dungeon_master_is_offered_acceptance_whenever_terms_are_on_the_table_for_the_actor()
    {
        // The resolver is stateless and cannot know an offer exists, so left to itself it never names
        // accept_surrender and the DM is never handed the tool. Whether terms are pending is state, so the
        // orchestration adds the tool from the state — the DM does hold the snapshot and can rule for itself.
        var offer = new SurrenderOffer
        {
            Id = "offer-1",
            OffererId = TestWorld.VarkId,
            RecipientId = TestWorld.RowanId,
            OfferedItemIds = ["purse-vark"],
            ForfeitWeapon = false,
            CreatedRound = 1,
            CreatedTurn = 3,
            State = SurrenderOfferState.Pending
        };

        var stealVersion = new RuleCatalog().Find("inventory.steal")!.Version;
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-accept", DungeonMasterTools.AcceptSurrenderName,
                    ("recipient", "Rowan"), ("offer", "offer-1")),
                ScriptedChatClient.Text("Rowan takes the captain's purse and lets him live.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                Act("r-1", "I take the purse he is holding out and tell him he lives.")))),
            initialState: TestWorld.V07State() with { SurrenderOffers = [offer] },
            rules: CombatRules.NoGlancing,
            scenario: TestWorld.V07Scenario(),
            rulebookResolverClient: new ScriptedChatClient(ScriptedChatClient.Text(
                $$"""
                { "supported": true, "candidateActions": ["steal_item"],
                  "citedRules": [ { "ruleId": "inventory.steal", "version": "{{stealVersion}}" } ] }
                """)));

        var turn = await harness.RunTurn("Rowan", round: 2, turn: 5);

        Assert.Equal(TurnOutcome.ActionResolved, turn.Outcome);

        // The rulebook named only the theft; the state added the acceptance the rulebook could not see.
        var consultation = Assert.Single(harness.Sink.Payloads<RulebookConsultationPayload>(TraceEventType.RulebookConsultation));
        Assert.Equal(["steal_item", "reject_action"], consultation.DmCandidateTools);

        var dmTools = harness.DungeonMasterClient.RequestOptions[0]!.Tools!.Select(t => t.Name).ToList();
        Assert.Contains(DungeonMasterTools.AcceptSurrenderName, dmTools);
        Assert.Contains(DungeonMasterTools.StealItemName, dmTools);

        // And the DM was told why, in a note naming the offer the rulebook could not have known about.
        var request = string.Join(" ", harness.DungeonMasterClient.Requests[0]
            .Where(m => m.Role == ChatRole.User)
            .Select(m => m.Text));
        Assert.Contains("STATE THE RULEBOOK COULD NOT SEE", request, StringComparison.Ordinal);
        Assert.Contains("offer-1", request, StringComparison.Ordinal);

        Assert.Equal(CharacterDisposition.Surrendered, harness.Engine.State.RequireById(TestWorld.VarkId).Disposition);
    }

    [Fact]
    public async Task Acceptance_is_offered_even_when_the_rulebook_refused_the_intent_outright()
    {
        // The one place the fail-safe is deliberately opened. A GPT-driven run had a character say "I take
        // Skrit's purse and tell him he can live" with Skrit's offer standing; the resolver read only the
        // extra demand riding along and answered supported:false, so the DM was handed the rejection alone
        // and told him to wait for a yield that had already happened. Whether an offer exists is state, not
        // a judgement the rulebook is in any position to make.
        var offer = new SurrenderOffer
        {
            Id = "offer-1",
            OffererId = TestWorld.VarkId,
            RecipientId = TestWorld.RowanId,
            OfferedItemIds = ["purse-vark"],
            ForfeitWeapon = false,
            CreatedRound = 1,
            CreatedTurn = 3,
            State = SurrenderOfferState.Pending
        };

        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-accept", DungeonMasterTools.AcceptSurrenderName,
                    ("recipient", "Rowan"), ("offer", "offer-1")),
                ScriptedChatClient.Text("Rowan takes the captain's purse and lets him live.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                Act("r-1", "I take his purse and tell him he can live — and drop the other one and get out.")))),
            initialState: TestWorld.V07State() with { SurrenderOffers = [offer] },
            rules: CombatRules.NoGlancing,
            scenario: TestWorld.V07Scenario(),
            rulebookResolverClient: new ScriptedChatClient(ScriptedChatClient.Text(
                """
                { "supported": false, "candidateActions": [], "citedRules": [],
                  "unsupportedReason": "the rulebook cannot make one character drop what another names" }
                """)));

        var turn = await harness.RunTurn("Rowan", round: 2, turn: 5);

        Assert.Equal(TurnOutcome.ActionResolved, turn.Outcome);

        // The rulebook refused; the state added the acceptance anyway, and said why.
        var consultation = Assert.Single(harness.Sink.Payloads<RulebookConsultationPayload>(TraceEventType.RulebookConsultation));
        Assert.Equal(["reject_action"], consultation.DmCandidateTools);

        var dmTools = harness.DungeonMasterClient.RequestOptions[0]!.Tools!.Select(t => t.Name).ToList();
        Assert.Equal([DungeonMasterTools.AcceptSurrenderName, DungeonMasterTools.RejectActionName], dmTools);

        Assert.Equal(CharacterDisposition.Surrendered, harness.Engine.State.RequireById(TestWorld.VarkId).Disposition);
    }

    [Fact]
    public async Task A_refused_intent_is_never_widened_when_no_terms_are_on_the_table()
    {
        // The fail-safe is opened for exactly one thing and stays shut for everything else: with no offer
        // pending, a refused intent reaches the Dungeon Master as the rejection alone, as it always has.
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-reject", DungeonMasterTools.RejectActionName,
                    ("category", "unsupported"), ("reason", "There is nothing here to climb.")),
                ScriptedChatClient.Text("Rowan looks for a way up and finds none."),
                ScriptedChatClient.Call("dm-pass", DungeonMasterTools.RejectActionName,
                    ("category", "unsupported"), ("reason", "There is nothing here to climb.")),
                ScriptedChatClient.Text("The moment passes.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                Act("r-1", "I climb the wall to get above them."),
                ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "There is no way up."))))),
            initialState: TestWorld.V07State(),
            rules: CombatRules.NoGlancing,
            scenario: TestWorld.V07Scenario(),
            rulebookResolverClient: new ScriptedChatClient(ScriptedChatClient.Text(
                """
                { "supported": false, "candidateActions": [], "citedRules": [],
                  "unsupportedReason": "there is no climbing in this world" }
                """)));

        await harness.RunTurn("Rowan", round: 1, turn: 1);

        var dmTools = harness.DungeonMasterClient.RequestOptions[0]!.Tools!.Select(t => t.Name).ToList();
        Assert.Equal([DungeonMasterTools.RejectActionName], dmTools);
    }

    [Fact]
    public void An_offer_that_came_to_nothing_is_reported_back_to_the_character_who_made_it()
    {
        // Silence reads as "still waiting". In a live v0.7 run a character offered terms in round 4, was
        // refused in the same round, and then spent rounds 9, 10 and 11 bracing "while Vark considers my
        // offer" — her self-state had gone quietly back to "you have offered none" and never contradicted
        // her. So a settled offer stays on the state until something else replaces it.
        var formatter = new WorldStateFormatter(
            PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates")));
        var baseline = TestWorld.V07State();
        var elara = baseline.RequireById(TestWorld.ElaraId);

        var settled = new SurrenderOffer
        {
            Id = "offer-1",
            OffererId = TestWorld.ElaraId,
            RecipientId = TestWorld.VarkId,
            OfferedItemIds = ["purse-elara"],
            ForfeitWeapon = false,
            State = SurrenderOfferState.Expired,
            CreatedRound = 4,
            CreatedTurn = 14,
            ResolvedRound = 4,
            ResolvedTurn = 15
        };

        var expired = formatter.FormatCharacterSelfState(elara,
            baseline with { SurrenderOffers = [settled with { State = SurrenderOfferState.Expired }] });
        Assert.Contains("LAPSED", expired, StringComparison.Ordinal);
        Assert.Contains("Do not wait for an answer", expired, StringComparison.Ordinal);
        Assert.DoesNotContain("you have offered none", expired, StringComparison.OrdinalIgnoreCase);

        var rejected = formatter.FormatCharacterSelfState(elara,
            baseline with { SurrenderOffers = [settled with { State = SurrenderOfferState.Rejected }] });
        Assert.Contains("are DEAD", rejected, StringComparison.Ordinal);

        // A live offer still reads as live, and is never overwritten by an older settled one.
        var live = formatter.FormatCharacterSelfState(elara, baseline with
        {
            SurrenderOffers =
            [
                settled with { State = SurrenderOfferState.Expired },
                settled with { Id = "offer-2", State = SurrenderOfferState.Pending, ResolvedRound = null, ResolvedTurn = null }
            ]
        });
        Assert.Contains("It is not settled", live, StringComparison.Ordinal);
        Assert.DoesNotContain("LAPSED", live, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stealing_from_the_dead_becomes_taking_from_their_body()
    {
        // Nothing can be stolen from a corpse — the engine's own steal rule excludes the dead, and what they
        // carried has already moved into their body. The resolver cannot know the target is dead, so in a
        // live run a goblin spent its whole turn on three rewordings of "loot the gold from dead Rowan" and
        // hit the attempt limit, one refusal even conceding the belongings were "within reach for anyone to
        // take freely". The deed is a take from the body, so that is what the world resolves.
        var corpse = new Container
        {
            Id = $"corpse-{TestWorld.RowanId}",
            Name = "Rowan's body",
            Description = "The fallen body of Rowan, its belongings within reach.",
            IsOpen = true,
            IsCorpse = true,
            Contents = [TestWorld.Purse("rowan")]
        };

        var baseline = TestWorld.V07State();
        var dead = baseline.RequireById(TestWorld.RowanId) with
        {
            Health = 0,
            Disposition = CharacterDisposition.Dead,
            Inventory = []
        };
        var state = baseline.WithCharacter(dead) with
        {
            Room = baseline.Room with { Objects = baseline.Room.Objects.Add(corpse) }
        };

        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-steal", DungeonMasterTools.StealItemName,
                    ("thief", "Skrit"), ("target", "Rowan"), ("item", "purse-rowan")),
                ScriptedChatClient.Text("Skrit scoops the purse out of the water beside the body.")),
            MultiActorHarness.Clients(("Skrit", new ScriptedChatClient(
                Act("s-1", "I loot the gold from dead Rowan.")))),
            state);

        var turn = await harness.RunTurn("Skrit", round: 3, turn: 12);

        Assert.Equal(TurnOutcome.ActionResolved, turn.Outcome);

        // The purse moved from the body to the looter, and no theft roll was made against a dead man.
        Assert.Contains(harness.Engine.State.RequireById(TestWorld.SkritId).Inventory, i => i.Id == "purse-rowan");
        Assert.Empty(harness.Engine.State.Room.Objects.OfType<Container>().Single(c => c.IsCorpse).Contents);
        Assert.Empty(harness.Sink.OfType(TraceEventType.RngDraw));

        var dispatch = harness.Sink.Payloads<ToolCallDispatchPayload>(TraceEventType.ToolCallDispatched)
            .First(p => p.ToolName == DungeonMasterTools.StealItemName);
        Assert.Contains("Redirected to take_item", dispatch.DispatchDecision, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ignored_offer_lapses_at_the_end_of_the_recipients_turn_and_the_room_is_told()
    {
        var offer = new SurrenderOffer
        {
            Id = "offer-1",
            OffererId = TestWorld.VarkId,
            RecipientId = TestWorld.RowanId,
            OfferedItemIds = ["purse-vark"],
            ForfeitWeapon = false,
            CreatedRound = 1,
            CreatedTurn = 3,
            State = SurrenderOfferState.Pending
        };

        var harness = Harness(
            new ScriptedChatClient(ScriptedChatClient.Text("Rowan says nothing and keeps his blade where it is.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.EndTurnName, ("reason", "I do not trust a goblin's word."))))),
            TestWorld.V07State() with { SurrenderOffers = [offer] });

        await harness.RunTurn("Rowan", round: 2, turn: 5);

        Assert.Equal(SurrenderOfferState.Expired, harness.Engine.State.FindOffer("offer-1")!.State);

        var resolved = Assert.Single(harness.Sink.Payloads<SurrenderOfferResolvedPayload>(TraceEventType.SurrenderOfferResolved));
        Assert.Equal(nameof(SurrenderOfferState.Expired), resolved.NewState);
        Assert.False(resolved.AssetsTransferred);
        Assert.Equal(2, resolved.TurnsToRespond);

        // Everybody present learns the terms came to nothing, on the public channel, with no model call for it.
        Assert.Contains(harness.NarrationLog.Entries, e => e.Purpose == "surrender-offer-lapsed");
        var settledFact = Assert.Single(harness.Ledger.Facts, f => f.FactType == FactType.SurrenderOfferSettled);
        Assert.True(harness.Ledger.Knows(TestWorld.VarkId, settledFact.Id));
        Assert.True(harness.Ledger.Knows(TestWorld.SkritId, settledFact.Id));

        // Nothing moved.
        Assert.DoesNotContain(harness.Engine.State.RequireById(TestWorld.RowanId).Inventory, i => i.Id == "purse-vark");
    }

    [Fact]
    public async Task An_offer_made_after_speech_is_associated_with_that_speech_without_it_becoming_terms()
    {
        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-offer", DungeonMasterTools.OfferSurrenderName,
                    ("offerer", "Vark"), ("recipient", "Rowan"), ("offered_items", new[] { "purse-vark" }), ("forfeit_weapon", true)),
                ScriptedChatClient.Text("Vark holds out the purse, sabre reversed in his other hand.")),
            MultiActorHarness.Clients(("Vark", new ScriptedChatClient(
                ScriptedChatClient.Calls(
                    ScriptedChatClient.CallContent("v-say", CharacterTools.SayName,
                        ("message", "Human! Take the gold and the blade, and let me crawl out of here.")),
                    ScriptedChatClient.CallContent("v-1", CharacterTools.TakeActionName,
                        ("intent", "I hold out my purse and my sabre to Rowan to be let go.")))))));

        await harness.RunTurn("Vark", round: 2, turn: 7);

        var made = Assert.Single(harness.Sink.Payloads<SurrenderOfferMadePayload>(TraceEventType.SurrenderOfferMade));
        Assert.NotNull(made.AssociatedSpeechEventId);
        Assert.Contains("let me crawl out of here", made.AssociatedSpeech!, StringComparison.Ordinal);

        // The terms are the concrete assets, never the words.
        Assert.Equal(["purse-vark"], made.OfferedItemIds.ToArray());
        Assert.True(made.ForfeitWeapon);
        Assert.Equal("Notched Sabre", made.WeaponName);
        Assert.False(string.IsNullOrWhiteSpace(made.BattleStateSummary));

        // And nothing has actually happened yet.
        Assert.True(made.NothingTransferred);
        Assert.True(made.OffererRemainsTargetable);
        Assert.Equal(CharacterDisposition.Active, harness.Engine.State.RequireById(TestWorld.VarkId).Disposition);
        Assert.Empty(harness.Sink.OfType(TraceEventType.CharacterSurrendered));
        Assert.Contains(harness.Engine.State.RequireById(TestWorld.VarkId).Inventory, i => i.Id == "purse-vark");
    }

    [Fact]
    public async Task An_accepted_surrender_records_tribute_and_weapon_provenance_distinctly()
    {
        var offer = new SurrenderOffer
        {
            Id = "offer-1",
            OffererId = TestWorld.SkritId,
            RecipientId = TestWorld.ElaraId,
            OfferedItemIds = ["purse-skrit"],
            ForfeitWeapon = true,
            CreatedRound = 1,
            CreatedTurn = 4,
            State = SurrenderOfferState.Pending
        };

        var harness = Harness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-accept", DungeonMasterTools.AcceptSurrenderName,
                    ("recipient", "Elara"), ("offer", "offer-1")),
                ScriptedChatClient.Text("Elara takes the little purse, and the spear clatters into the water.")),
            MultiActorHarness.Clients(("Elara", new ScriptedChatClient(
                Act("e-1", "I take his coin and tell him he can live.")))),
            TestWorld.V07State() with { SurrenderOffers = [offer] });

        await harness.RunTurn("Elara", round: 2, turn: 6);

        var provenance = harness.Sink.Payloads<ItemProvenancePayload>(TraceEventType.ItemProvenance).ToList();

        var tribute = Assert.Single(provenance, p => p.ActionType == "surrender_tribute");
        Assert.Equal("purse-skrit", tribute.ItemId);
        Assert.Equal(TestWorld.SkritId, tribute.PreviousOwnerOrLocation);
        Assert.Equal(TestWorld.ElaraId, tribute.NewOwnerOrLocation);
        Assert.False(tribute.RngInvolved);

        var weapon = Assert.Single(provenance, p => p.ActionType == "weapon_forfeiture");
        Assert.Equal(TestWorld.SkritId, weapon.PreviousOwnerOrLocation);
        Assert.Equal(Container.GroundId, weapon.NewOwnerOrLocation);

        var agreement = Assert.Single(harness.Sink.Payloads<SurrenderAgreementPayload>(TraceEventType.SurrenderAgreementRecorded));
        Assert.True(agreement.OffererDisarmed);
        Assert.Contains("floor", agreement.WeaponDisposition!, StringComparison.OrdinalIgnoreCase);
    }
}
