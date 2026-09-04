using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Rulebook;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The rulebook stage driven through the real <see cref="TurnCoordinator"/> with a scripted resolver: every
/// take_action is preceded by an automatic consultation, the DM is narrowed to the candidate action plus
/// rejection, an unsupported intent becomes a rejection, the engine still rejects invalid bindings, guidance
/// never accumulates in the DM's history, and the resolver request stays bounded as the encounter grows.
/// </summary>
public sealed class RulebookOrchestrationTests
{
    private static readonly RuleCatalog Catalog = new();

    private static string AttackGuidanceJson()
    {
        var attack = Catalog.Find("combat.attack")!;
        return $$"""
        { "supported": true, "candidateActions": ["attack_character"],
          "citedRules": [ { "ruleId": "combat.attack", "version": "{{attack.Version}}" } ],
          "requiredBindings": ["attacker", "target", "weapon"], "preconditions": ["target is another active character"],
          "turnCost": "consumes the turn", "rngSpecification": "hit and glancing rolls", "visibility": "public",
          "successBehaviour": "damage applied", "failureBehaviour": "a miss changes nothing" }
        """;
    }

    private static string UnsupportedGuidanceJson() =>
        """
        { "supported": false, "candidateActions": [], "citedRules": [],
          "unsupportedReason": "there is no way to throw a weapon in this world" }
        """;

    [Fact]
    public async Task Every_take_action_is_preceded_by_a_consultation_that_narrows_the_dm_to_the_candidate_and_rejection()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
                ScriptedChatClient.Text("Rowan's longsword bites into the captain.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName,
                    ("intent", "I bring my longsword down on Vark."))))),
            rulebookResolverClient: new ScriptedChatClient(ScriptedChatClient.Text(AttackGuidanceJson())));

        await harness.RunTurn("Rowan");

        // A consultation was recorded and it selected the attack action.
        var consultation = Assert.Single(harness.Sink.Payloads<RulebookConsultationPayload>(TraceEventType.RulebookConsultation));
        Assert.Equal("Supported", consultation.Outcome);
        Assert.Equal(["attack_character", "reject_action"], consultation.DmCandidateTools);

        // The DM adjudication call was exposed exactly the candidate action plus rejection — not the whole surface.
        var dmTools = harness.DungeonMasterClient.RequestOptions[0]!.Tools!.Select(t => t.Name).ToList();
        Assert.Equal([DungeonMasterTools.AttackCharacterName, DungeonMasterTools.RejectActionName, CharacterTools.RequestName], dmTools);
    }

    [Fact]
    public async Task An_unsupported_intent_narrows_the_dm_to_rejection_only_and_becomes_a_refusal()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.RejectActionName,
                    ("category", "unsupported"), ("reason", "Your blade is no throwing knife; it stays in your hand.")),
                // The refusal does not consume the turn, so Rowan ends it and the DM narrates that pass.
                ScriptedChatClient.Text("Rowan lowers the blade and holds his ground.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName, ("intent", "I hurl my longsword at Vark.")),
                    ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "No good."))))),
            rulebookResolverClient: new ScriptedChatClient(ScriptedChatClient.Text(UnsupportedGuidanceJson())));

        await harness.RunTurn("Rowan");

        var consultation = Assert.Single(harness.Sink.Payloads<RulebookConsultationPayload>(TraceEventType.RulebookConsultation));
        Assert.Equal("Unsupported", consultation.Outcome);
        Assert.Equal([DungeonMasterTools.RejectActionName], consultation.DmCandidateTools);

        var dmTools = harness.DungeonMasterClient.RequestOptions[0]!.Tools!.Select(t => t.Name).ToList();
        Assert.Equal([DungeonMasterTools.RejectActionName, CharacterTools.RequestName], dmTools);

        Assert.Contains(harness.Sink.Payloads<DmAdjudicationPayload>(TraceEventType.DmAdjudication),
            a => a.Category == nameof(ActionResolutionCategory.DmUnsupported));
    }

    [Fact]
    public async Task The_engine_still_rejects_an_invalid_binding_after_valid_guidance()
    {
        // Vark is already dead; the guidance supports an attack, the DM binds it against Vark, and the engine
        // authoritatively refuses — the rulebook never made the action valid.
        var state = TestWorld.State(TestWorld.Rowan(), TestWorld.Elara(health: 6), TestWorld.Vark(health: 0), TestWorld.Skrit());

        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
                ScriptedChatClient.Text("Rowan checks his swing — the captain is already down."),
                // The refused attack does not consume the turn, so Rowan ends it and the DM narrates the pass.
                ScriptedChatClient.Text("Rowan lowers his blade and steadies himself.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName, ("intent", "I strike the fallen Vark.")),
                    ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName, ("reason", "He's down."))))),
            initialState: state,
            rulebookResolverClient: new ScriptedChatClient(ScriptedChatClient.Text(AttackGuidanceJson())));

        await harness.RunTurn("Rowan");

        var engineAction = Assert.Single(harness.Sink.Payloads<EngineActionPayload>(TraceEventType.EngineAction));
        Assert.False(engineAction.Accepted);
        Assert.Equal("attack_character", engineAction.ActionType);
    }

    [Fact]
    public async Task Rule_guidance_does_not_accumulate_in_the_dm_history_across_adjudications()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
                ScriptedChatClient.Text("A first blow."),
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
                ScriptedChatClient.Text("A second blow.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName, ("intent", "I strike Vark.")),
                    ScriptedChatClient.Call("r-2", CharacterTools.TakeActionName, ("intent", "I strike Vark again."))))),
            rulebookResolverClient: new ScriptedChatClient(
                ScriptedChatClient.Text(AttackGuidanceJson()),
                ScriptedChatClient.Text(AttackGuidanceJson())));

        await harness.RunTurn("Rowan", round: 1, turn: 1);
        await harness.RunTurn("Rowan", round: 2, turn: 5);

        // Each adjudication runs on a fresh projection: the DM adjudication request is always exactly a system
        // message plus one user message, so guidance from an earlier attempt never piles up in later ones.
        var adjudicationRequests = harness.DungeonMasterClient.Requests
            .Where(r => r.Any(m => (m.Text ?? "").Contains("TASK: ADJUDICATION", StringComparison.Ordinal)))
            .ToList();
        Assert.Equal(2, adjudicationRequests.Count);
        Assert.All(adjudicationRequests, r => Assert.Equal(2, r.Count));
    }

    [Fact]
    public async Task The_resolver_request_size_does_not_grow_with_the_length_of_the_encounter()
    {
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
                ScriptedChatClient.Text("A blow."),
                ScriptedChatClient.Call("dm-2", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Rowan"), ("target", "Vark"), ("weapon", "Longsword")),
                ScriptedChatClient.Text("Another blow.")),
            MultiActorHarness.Clients(
                ("Rowan", new ScriptedChatClient(
                    ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName, ("intent", "I strike Vark with my longsword.")),
                    ScriptedChatClient.Call("r-2", CharacterTools.TakeActionName, ("intent", "I strike Vark with my longsword."))))),
            // Cache off, so the resolver is genuinely called both times and its request size can be compared.
            limits: new HarnessOptions
            {
                MaxQuestionsPerTurn = 2, MaxActionAttemptsPerTurn = 3, MaxModelCallsPerTurn = 8,
                RulebookCacheEnabled = false
            },
            rulebookResolverClient: new ScriptedChatClient(
                ScriptedChatClient.Text(AttackGuidanceJson()),
                ScriptedChatClient.Text(AttackGuidanceJson())));

        // Two turns far apart in the fight: the DM and character agents accumulate history between them, but
        // the stateless resolver does not — so its request is identical both times.
        await harness.RunTurn("Rowan", round: 1, turn: 1);
        await harness.RunTurn("Rowan", round: 5, turn: 20);

        var consultations = harness.Sink.Payloads<RulebookConsultationPayload>(TraceEventType.RulebookConsultation).ToList();
        Assert.Equal(2, consultations.Count);

        // Same intent, same cards → identical bounded request size, regardless of how far into the fight it is.
        Assert.Equal(consultations[0].TotalRequestChars, consultations[1].TotalRequestChars);

        // The ceiling governs the CARDS; the whole request is those plus the resolver's system prompt, the
        // request template and the intent. That fixed overhead is a few thousand characters, so the bound is
        // the ceiling plus a stated allowance for it rather than the ceiling exactly.
        const int FixedRequestOverheadAllowance = 6000;
        Assert.True(
            consultations[1].TotalRequestChars <= consultations[1].MaxInputCharsConfigured + FixedRequestOverheadAllowance,
            $"Resolver request was {consultations[1].TotalRequestChars} characters against a card ceiling of " +
            $"{consultations[1].MaxInputCharsConfigured} plus {FixedRequestOverheadAllowance} of fixed overhead.");
    }
}
