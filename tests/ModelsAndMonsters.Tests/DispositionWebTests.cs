using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Web;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The observer UI re-renders from the authoritative <see cref="StateDto"/> snapshot, so verifying the
/// projection verifies that the live cards and room-state element receive the right data: each character's
/// disposition, and the exit's public open state. The React rendering itself is out of scope for a C# test,
/// but the data it renders from is exactly this (test #35).
/// </summary>
public sealed class DispositionWebTests
{
    [Fact]
    public void The_state_snapshot_carries_every_disposition_and_the_exit_open_state()
    {
        var vark = TestWorld.Vark() with { Disposition = CharacterDisposition.Surrendered };
        var skrit = TestWorld.Skrit() with
        {
            Disposition = CharacterDisposition.Escaped,
            EscapedThroughExitId = TestWorld.StairDoorId
        };
        var state = TestWorld.StateWithExit(TestWorld.StairDoor(open: true),
            TestWorld.Rowan(), TestWorld.Elara(), vark, skrit);

        var dto = StateDto.From(state);

        // Every disposition rides the snapshot the cards render from, so a surrendered or escaped character
        // reads as such rather than as fallen.
        Assert.Equal("Active", dto.Characters.Single(c => c.Name == "Rowan").Disposition);
        Assert.Equal("Surrendered", dto.Characters.Single(c => c.Name == "Vark").Disposition);
        Assert.Equal("Escaped", dto.Characters.Single(c => c.Name == "Skrit").Disposition);
        Assert.True(dto.Characters.Single(c => c.Name == "Vark").Alive);
        Assert.True(dto.Characters.Single(c => c.Name == "Skrit").Alive);

        // The exit's public open state rides the same snapshot, so the room-state element can update live.
        var exit = Assert.Single(dto.Exits);
        Assert.Equal("Cellar Stair Door", exit.Name);
        Assert.True(exit.IsOpen);
    }

    [Fact]
    public void The_state_snapshot_shows_the_exit_closed_and_everyone_active_before_anything_happens()
    {
        var dto = StateDto.From(TestWorld.TwoVsTwoStateWithExit(exitOpen: false));

        Assert.False(Assert.Single(dto.Exits).IsOpen);
        Assert.All(dto.Characters, c => Assert.Equal("Active", c.Disposition));
    }

    [Fact]
    public void The_snapshot_carries_ability_charges_and_the_live_status_badges()
    {
        var engine = TestWorld.V07Engine();
        Assert.True(engine.Execute(new ModelsAndMonsters.Engine.UseAbilityAction(
            "Rowan", AbilityCatalog.GuardAllyId, "Elara")).Accepted);
        Assert.True(engine.Execute(new ModelsAndMonsters.Engine.UseAbilityAction(
            "Elara", AbilityCatalog.HealingPrayerId, "Elara")).Accepted);

        var dto = StateDto.From(engine.State);

        // Charges ride the snapshot, so a spent ability can be struck through on the card.
        var prayer = Assert.Single(dto.Characters.Single(c => c.Name == "Elara").Abilities,
            a => a.Id == AbilityCatalog.HealingPrayerId);
        Assert.Equal(1, prayer.MaxUses);
        Assert.Equal(0, prayer.RemainingUses);

        // An unlimited ability rides it with no maximum, which the card renders as an infinity marker.
        var guard = Assert.Single(dto.Characters.Single(c => c.Name == "Rowan").Abilities,
            a => a.Id == AbilityCatalog.GuardAllyId);
        Assert.Null(guard.MaxUses);

        // The guard relationship reads as a pair: each half names the other side by name.
        var guarding = Assert.Single(dto.Characters.Single(c => c.Name == "Rowan").Statuses);
        Assert.Equal("Guarding", guarding.Kind);
        Assert.Equal("Elara", guarding.PartnerName);
        var guarded = Assert.Single(dto.Characters.Single(c => c.Name == "Elara").Statuses);
        Assert.Equal("Guarded", guarded.Kind);
        Assert.Equal("Rowan", guarded.Source);
    }

    [Fact]
    public void The_snapshot_keeps_a_pending_offer_visibly_distinct_from_an_accepted_surrender()
    {
        var engine = TestWorld.V07Engine();
        var offer = engine.Execute(new ModelsAndMonsters.Engine.OfferSurrenderAction(
            "Vark", "Rowan", ["purse-vark"], ForfeitWeapon: true));
        Assert.True(offer.Accepted);

        var pendingDto = StateDto.From(engine.State);

        // While it is pending: one offer awaiting an answer, no agreement, and the offerer still armed.
        var pending = Assert.Single(pendingDto.PendingOffers);
        Assert.Equal("Vark", pending.Offerer);
        Assert.Equal("Rowan", pending.Recipient);
        Assert.Contains("Small Purse of Gold Coins", pending.Terms, StringComparison.Ordinal);
        Assert.Contains("Notched Sabre", pending.Terms, StringComparison.Ordinal);
        Assert.Empty(pendingDto.Agreements);
        Assert.False(pendingDto.Characters.Single(c => c.Name == "Vark").Disarmed);

        // After acceptance: no pending offer, one settled offer, one agreement, and a disarmed offerer.
        var offerId = ((ModelsAndMonsters.Engine.OfferSurrenderOutcome)offer.Outcome!).OfferId;
        Assert.True(engine.Execute(new ModelsAndMonsters.Engine.AcceptSurrenderAction("Rowan", offerId)).Accepted);

        var acceptedDto = StateDto.From(engine.State);
        Assert.Empty(acceptedDto.PendingOffers);
        Assert.Equal("Accepted", Assert.Single(acceptedDto.SettledOffers).State);
        var agreement = Assert.Single(acceptedDto.Agreements);
        Assert.Equal("Vark", agreement.Offerer);
        Assert.Equal("Rowan", agreement.AcceptedBy);
        Assert.Equal("Notched Sabre", agreement.ForfeitedWeapon);
        Assert.True(acceptedDto.Characters.Single(c => c.Name == "Vark").Disarmed);

        // The forfeited weapon is on the floor, in plain sight, where the ground row renders it.
        Assert.Contains("Notched Sabre", acceptedDto.Ground);
    }
}
