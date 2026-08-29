using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>An RNG that draws the lowest value every time — so an attack always lands (and lands hard).</summary>
file sealed class AlwaysLowRng : IRng
{
    public long Seed => 0;
    public long DrawCount { get; private set; }
    public int Roll(int sides) { DrawCount++; return 1; }
}

/// <summary>
/// Demanding an opponent's surrender at the engine level (v0.11). This is the mirror of an offer, pointed the
/// other way: a winner telling an opponent to yield. It exists because pressing an opponent to surrender used
/// to have no representable form and collapsed into <c>offer_surrender</c>, recording the WINNER as the one who
/// gave up. A demand is pressure only: it moves nothing, binds nobody, and above all never touches the
/// demander's own standing.
/// </summary>
public sealed class DemandSurrenderEngineTests
{
    private static GameEngine Engine(IRng? rng = null, params Character[] characters) =>
        TestWorld.V07Engine(rng, CombatRules.NoGlancing, characters);

    private static string DemandId(EngineResult result) => ((DemandSurrenderOutcome)result.Outcome!).DemandId;

    [Fact]
    public void A_demand_records_a_pending_demand_and_never_makes_the_demander_the_one_who_yields()
    {
        var engine = Engine();
        engine.BeginActorTurn(TestWorld.VarkId, round: 1, turn: 3);

        var result = engine.Execute(new DemandSurrenderAction("Vark", "Rowan"));

        Assert.True(result.Accepted);
        var demand = Assert.Single(engine.State.PendingDemands());
        Assert.Equal(TestWorld.VarkId, demand.DemanderId);
        Assert.Equal(TestWorld.RowanId, demand.TargetId);

        // The whole point: the demander is NOT recorded as surrendering. No offer was created, nobody yielded,
        // and Vark keeps his standing and his weapon entirely.
        Assert.Empty(engine.State.SurrenderOffers);
        Assert.Empty(engine.State.SurrenderAgreements);
        var vark = engine.State.RequireById(TestWorld.VarkId);
        Assert.Equal(CharacterDisposition.Active, vark.Disposition);
        Assert.NotNull(vark.Weapon);

        // And the target is untouched — nothing was compelled or moved.
        var rowan = engine.State.RequireById(TestWorld.RowanId);
        Assert.Equal(CharacterDisposition.Active, rowan.Disposition);
        Assert.NotNull(rowan.Weapon);

        // The transition out is a creation, not a resolution.
        var transition = Assert.Single(result.DemandTransitions);
        Assert.Equal(SurrenderDemandState.Pending, transition.New);
    }

    [Fact]
    public void A_demand_must_name_an_opposing_active_target()
    {
        var engine = Engine();

        var ally = engine.Execute(new DemandSurrenderAction("Vark", "Skrit"));
        Assert.False(ally.Accepted);
        Assert.Equal(EngineRejectionReason.RecipientIsNotAnOpponent, ally.RejectionReason);

        var self = engine.Execute(new DemandSurrenderAction("Vark", "Vark"));
        Assert.False(self.Accepted);
        Assert.Equal(EngineRejectionReason.RecipientIsSelf, self.RejectionReason);

        var unknown = engine.Execute(new DemandSurrenderAction("Vark", "Nobody"));
        Assert.False(unknown.Accepted);
        Assert.Equal(EngineRejectionReason.UnknownRecipient, unknown.RejectionReason);

        Assert.Empty(engine.State.PendingDemands());
    }

    [Fact]
    public void A_demand_against_a_target_no_longer_in_the_fight_is_refused()
    {
        var surrenderedRowan = TestWorld.RowanV07() with { Disposition = CharacterDisposition.Surrendered };
        var engine = Engine(null, surrenderedRowan, TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        var result = engine.Execute(new DemandSurrenderAction("Vark", "Rowan"));

        Assert.False(result.Accepted);
        Assert.Empty(engine.State.PendingDemands());
    }

    [Fact]
    public void Only_one_demand_may_be_pending_per_demander()
    {
        var engine = Engine();
        engine.BeginActorTurn(TestWorld.VarkId, round: 1, turn: 3);
        Assert.True(engine.Execute(new DemandSurrenderAction("Vark", "Rowan")).Accepted);

        var second = engine.Execute(new DemandSurrenderAction("Vark", "Elara"));

        Assert.False(second.Accepted);
        Assert.Equal(EngineRejectionReason.DuplicatePendingOffer, second.RejectionReason);
        Assert.Single(engine.State.PendingDemands());
    }

    [Fact]
    public void A_demand_the_target_does_not_answer_lapses_at_the_end_of_their_turn()
    {
        var engine = Engine();
        engine.BeginActorTurn(TestWorld.VarkId, round: 1, turn: 3);
        var demand = engine.Execute(new DemandSurrenderAction("Vark", "Rowan"));
        engine.EndActorTurn(TestWorld.VarkId, round: 1, turn: 3);

        // It survives the demander's own turn ending.
        Assert.Equal(SurrenderDemandState.Pending, engine.State.FindDemand(DemandId(demand))!.State);

        // Rowan takes his turn and fights on; at the end of it the demand lapses, having compelled nothing.
        engine.BeginActorTurn(TestWorld.RowanId, round: 2, turn: 5);
        var upkeep = engine.EndActorTurn(TestWorld.RowanId, round: 2, turn: 5);

        Assert.Equal(SurrenderDemandState.Expired, engine.State.FindDemand(DemandId(demand))!.State);
        var transition = Assert.Single(upkeep.DemandTransitions);
        Assert.Equal(SurrenderDemandState.Expired, transition.New);

        // Nothing moved: the target still holds everything.
        Assert.NotNull(engine.State.RequireById(TestWorld.RowanId).Weapon);
    }

    [Fact]
    public void A_demand_is_invalidated_when_a_party_leaves_active_play()
    {
        // A target one blow from death, so the next hit surely kills — leaving no chance for the demand to
        // simply expire at the end of the target's own turn first.
        var frailRowan = TestWorld.RowanV07() with { Health = 1, Armour = 0 };
        var engine = Engine(new AlwaysLowRng(), frailRowan, TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        engine.BeginActorTurn(TestWorld.VarkId, round: 1, turn: 3);
        var demand = engine.Execute(new DemandSurrenderAction("Vark", "Rowan"));
        engine.EndActorTurn(TestWorld.VarkId, round: 1, turn: 3);
        Assert.Equal(SurrenderDemandState.Pending, engine.State.FindDemand(DemandId(demand))!.State);

        // Before Rowan ever gets his turn, Skrit strikes him down. A demand whose target is out of the fight
        // can mean nothing more, and is quietly invalidated — never left lingering as still pending.
        engine.BeginActorTurn(TestWorld.SkritId, round: 1, turn: 4);
        var kill = engine.Execute(new AttackCharacterAction("Skrit", "Rowan",
            engine.State.RequireById(TestWorld.SkritId).Weapon!.Name));
        Assert.True(kill.Accepted);
        Assert.Equal(CharacterDisposition.Dead, engine.State.RequireById(TestWorld.RowanId).Disposition);

        Assert.Equal(SurrenderDemandState.Invalidated, engine.State.FindDemand(DemandId(demand))!.State);
        Assert.Contains(kill.DemandTransitions, t => t.New == SurrenderDemandState.Invalidated);
    }
}
