using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// Negotiated surrender at the engine level (v0.7). Giving up is no longer something a character can do to
/// itself: it takes a concrete offer from one side and acceptance from a named opponent on the other, and only
/// acceptance moves anything or changes anyone's standing. These are the deterministic guarantees the whole
/// persuasion slice rests on, independent of any model's judgement.
/// </summary>
public sealed class SurrenderNegotiationEngineTests
{
    private static GameEngine Engine(IRng? rng = null, params Character[] characters) =>
        TestWorld.V07Engine(rng, CombatRules.NoGlancing, characters);

    private static string OfferId(EngineResult result) => ((OfferSurrenderOutcome)result.Outcome!).OfferId;

    // ------------------------------------------------------------------------------------------
    // Making an offer
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void An_empty_offer_promising_nothing_is_rejected()
    {
        var engine = Engine();
        var versionBefore = engine.State.Version;

        var result = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", [], ForfeitWeapon: false));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.OfferHasNoConcession, result.RejectionReason);
        Assert.Equal(versionBefore, engine.State.Version);
        Assert.Empty(engine.State.SurrenderOffers);
    }

    [Fact]
    public void An_offer_must_name_an_opposing_active_recipient()
    {
        var engine = Engine();

        var ally = engine.Execute(new OfferSurrenderAction("Vark", "Skrit", ["purse-vark"], ForfeitWeapon: false));
        Assert.False(ally.Accepted);
        Assert.Equal(EngineRejectionReason.RecipientIsNotAnOpponent, ally.RejectionReason);

        var self = engine.Execute(new OfferSurrenderAction("Vark", "Vark", ["purse-vark"], ForfeitWeapon: false));
        Assert.False(self.Accepted);
        Assert.Equal(EngineRejectionReason.RecipientIsSelf, self.RejectionReason);

        var unknown = engine.Execute(new OfferSurrenderAction("Vark", "Nobody", ["purse-vark"], ForfeitWeapon: false));
        Assert.False(unknown.Accepted);
        Assert.Equal(EngineRejectionReason.UnknownRecipient, unknown.RejectionReason);
    }

    [Fact]
    public void An_offer_to_a_recipient_who_has_left_the_fight_is_rejected()
    {
        var surrenderedRowan = TestWorld.RowanV07() with { Disposition = CharacterDisposition.Surrendered };
        var engine = Engine(null, surrenderedRowan, TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        var result = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.TargetHasSurrendered, result.RejectionReason);
    }

    [Fact]
    public void An_offer_may_only_promise_assets_the_offerer_actually_owns()
    {
        var engine = Engine();

        var notOwned = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-rowan"], ForfeitWeapon: false));
        Assert.False(notOwned.Accepted);
        Assert.Equal(EngineRejectionReason.OfferedItemNotOwned, notOwned.RejectionReason);

        // A promise made twice over is not two concessions.
        var duplicated = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark", "purse-vark"], ForfeitWeapon: false));
        Assert.False(duplicated.Accepted);
        Assert.Equal(EngineRejectionReason.OfferedItemNotOwned, duplicated.RejectionReason);

        // The equipped weapon is not an ordinary item to list: giving it up is a weapon forfeiture.
        var weaponAsItem = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["Notched Sabre"], ForfeitWeapon: false));
        Assert.False(weaponAsItem.Accepted);
        Assert.Equal(EngineRejectionReason.OfferedItemNotTransferable, weaponAsItem.RejectionReason);
    }

    [Fact]
    public void A_character_carrying_nothing_may_still_yield_on_their_weapon_alone()
    {
        // The refinement that keeps the rule from closing the mechanic when it matters most. A live run had
        // Vark spend the fight trying to buy his way out — he gave his own purse away "as part of the deal",
        // handed back a flask he had stolen, and had his salve lifted — so when he finally put real terms on
        // the table he owned nothing but his sabre. The first version of this rule refused him and he died
        // on the next turn. Someone with nothing left is giving everything they have.
        var stripped = TestWorld.VarkV07() with { Inventory = [] };
        var engine = Engine(null, TestWorld.RowanV07(), TestWorld.ElaraV07(), stripped, TestWorld.SkritV07());

        var result = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", [], ForfeitWeapon: true));

        Assert.True(result.Accepted);
        Assert.Single(engine.State.SurrenderOffers);
    }

    [Fact]
    public void A_stripped_character_still_has_to_promise_the_weapon()
    {
        // "Nothing held back" is the rule, not "anyone with an empty pack may yield for free".
        var stripped = TestWorld.VarkV07() with { Inventory = [] };
        var engine = Engine(null, TestWorld.RowanV07(), TestWorld.ElaraV07(), stripped, TestWorld.SkritV07());

        var result = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", [], ForfeitWeapon: false));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.OfferHasNoConcession, result.RejectionReason);
    }

    [Fact]
    public void Weapon_only_terms_are_accepted_even_while_carrying_other_things()
    {
        // v0.7 refused this shape, to close a parsing artefact where a demand for somebody ELSE's surrender
        // (which has no representable form in this action) collapsed into "no items, forfeit_weapon true" and
        // got recorded as the speaker's own surrender. v0.10 lifts that extra narrowing: a genuine weapon-only
        // surrender ("I hold my sabre out by the flat and offer to lay it down if you spare me") must work
        // whatever else the offerer carries — the misread-demand shape is now closed at the guidance layer
        // (non-binding demands, one-mechanical-deed-per-turn), not by an engine gate that also blocked a
        // character who genuinely meant to give up everything but their coin purse.
        var engine = Engine();
        Assert.NotEmpty(TestWorld.VarkV07().Inventory);

        var result = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", [], ForfeitWeapon: true));

        Assert.True(result.Accepted);
        Assert.Empty(result.RngDraws);
        var offer = Assert.Single(engine.State.SurrenderOffers);
        Assert.Empty(offer.OfferedItemIds);
        Assert.True(offer.ForfeitWeapon);
    }

    [Fact]
    public void A_weapon_only_offer_leaves_the_weapon_equipped_and_the_offerer_untouched_before_acceptance()
    {
        var engine = Engine();
        var varkBefore = engine.State.RequireById(TestWorld.VarkId);

        var result = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", [], ForfeitWeapon: true));

        Assert.True(result.Accepted);
        var vark = engine.State.RequireById(TestWorld.VarkId);
        Assert.NotNull(vark.Weapon);
        Assert.False(vark.IsDisarmed);
        Assert.Equal(varkBefore.Weapon!.Id, vark.Weapon!.Id);
        Assert.Equal(varkBefore.Inventory.Select(i => i.Id), vark.Inventory.Select(i => i.Id));
        Assert.Equal(CharacterDisposition.Active, vark.Disposition);
        Assert.True(vark.IsCombatTarget);
        Assert.DoesNotContain(engine.State.Room.Objects.OfType<Container>(), c => c.IsGround);
    }

    [Fact]
    public void A_weapon_only_offer_moves_the_weapon_only_once_accepted()
    {
        var engine = Engine();
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", [], ForfeitWeapon: true));

        var accepted = engine.Execute(new AcceptSurrenderAction("Rowan", OfferId(offer)));

        Assert.True(accepted.Accepted);
        var vark = engine.State.RequireById(TestWorld.VarkId);
        Assert.True(vark.IsDisarmed);
        Assert.Equal(CharacterDisposition.Surrendered, vark.Disposition);

        // Vark's own inventory items were never promised, so they never moved.
        Assert.Equal(TestWorld.VarkV07().Inventory.Select(i => i.Id), vark.Inventory.Select(i => i.Id));

        var ground = Assert.Single(engine.State.Room.Objects.OfType<Container>(), c => c.IsGround);
        var forfeited = Assert.Single(ground.Contents);
        Assert.Equal(TestWorld.VarkV07().Weapon!.Id, forfeited.Id);
    }

    [Fact]
    public void Terms_promising_an_item_are_accepted_with_or_without_the_weapon()
    {
        // The weapon is still offerable — it just cannot stand alone. Both shapes remain valid.
        var withWeapon = Engine();
        Assert.True(withWeapon.Execute(
            new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: true)).Accepted);

        var withoutWeapon = Engine();
        Assert.True(withoutWeapon.Execute(
            new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false)).Accepted);
    }

    [Fact]
    public void Promising_a_weapon_the_offerer_does_not_hold_is_rejected()
    {
        var disarmed = TestWorld.VarkV07() with { Weapon = null };
        var engine = Engine(null, TestWorld.RowanV07(), TestWorld.ElaraV07(), disarmed, TestWorld.SkritV07());

        var result = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: true));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.OfferedWeaponNotHeld, result.RejectionReason);
    }

    [Fact]
    public void Creating_an_offer_transfers_nothing_and_disarms_nobody()
    {
        var engine = Engine();
        var varkBefore = engine.State.RequireById(TestWorld.VarkId);
        var rowanBefore = engine.State.RequireById(TestWorld.RowanId);

        var result = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark", "goblin-salve"], ForfeitWeapon: true));

        Assert.True(result.Accepted);
        var varkAfter = engine.State.RequireById(TestWorld.VarkId);
        var rowanAfter = engine.State.RequireById(TestWorld.RowanId);

        // Every promised thing is still exactly where it was.
        Assert.Equal(varkBefore.Inventory.Select(i => i.Id), varkAfter.Inventory.Select(i => i.Id));
        Assert.Equal(rowanBefore.Inventory.Select(i => i.Id), rowanAfter.Inventory.Select(i => i.Id));
        Assert.NotNull(varkAfter.Weapon);
        Assert.False(varkAfter.IsDisarmed);

        // And no item has appeared on the floor.
        Assert.DoesNotContain(engine.State.Room.Objects.OfType<Container>(), c => c.IsGround);
    }

    [Fact]
    public void Creating_an_offer_leaves_the_offerer_active_targetable_and_undisposed()
    {
        var engine = Engine();

        engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false));

        var vark = engine.State.RequireById(TestWorld.VarkId);
        Assert.Equal(CharacterDisposition.Active, vark.Disposition);
        Assert.True(vark.CanAct);
        Assert.True(vark.IsCombatTarget);

        var strike = engine.Execute(new AttackCharacterAction("Elara", "Vark", "Iron Mace"));
        Assert.True(strike.Accepted);
    }

    [Fact]
    public void Only_one_pending_offer_may_exist_per_offerer()
    {
        var engine = Engine();
        Assert.True(engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false)).Accepted);

        var second = engine.Execute(new OfferSurrenderAction("Vark", "Elara", ["goblin-salve"], ForfeitWeapon: false));

        Assert.False(second.Accepted);
        Assert.Equal(EngineRejectionReason.DuplicatePendingOffer, second.RejectionReason);
        Assert.Single(engine.State.PendingOffers());
    }

    [Fact]
    public void Making_an_offer_and_accepting_one_use_no_randomness_at_all()
    {
        var rng = new SeededRng(1);
        var engine = Engine(rng);

        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: true));
        var accepted = engine.Execute(new AcceptSurrenderAction("Rowan", OfferId(offer)));

        Assert.True(offer.Accepted && accepted.Accepted);
        Assert.Empty(offer.RngDraws);
        Assert.Empty(accepted.RngDraws);
        // The shared combat generator was never advanced, so seeded replay of the fight is unaffected.
        Assert.Equal(0, rng.DrawCount);
    }

    [Fact]
    public void The_offer_records_the_speech_that_accompanied_it_without_treating_it_as_terms()
    {
        var engine = Engine();

        var result = engine.Execute(new OfferSurrenderAction(
            "Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false, AssociatedSpeechEventId: 7));

        var offer = Assert.Single(engine.State.SurrenderOffers);
        Assert.Equal(7, offer.AssociatedSpeechEventId);

        // The terms are the item list and the weapon flag — never the speech.
        Assert.Equal(["purse-vark"], offer.OfferedItemIds.ToArray());
        Assert.False(offer.ForfeitWeapon);
        Assert.True(result.Accepted);
    }

    // ------------------------------------------------------------------------------------------
    // Accepting an offer
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Only_the_named_recipient_can_accept()
    {
        var engine = Engine();
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false));

        var wrongAccepter = engine.Execute(new AcceptSurrenderAction("Elara", OfferId(offer)));

        Assert.False(wrongAccepter.Accepted);
        Assert.Equal(EngineRejectionReason.OfferNotAddressedToActor, wrongAccepter.RejectionReason);
        Assert.Equal(CharacterDisposition.Active, engine.State.RequireById(TestWorld.VarkId).Disposition);
        Assert.Single(engine.State.PendingOffers());
    }

    [Fact]
    public void Accepting_an_unknown_offer_is_rejected()
    {
        var engine = Engine();

        var result = engine.Execute(new AcceptSurrenderAction("Rowan", "offer-99"));

        Assert.False(result.Accepted);
        Assert.Equal(EngineRejectionReason.UnknownOffer, result.RejectionReason);
    }

    [Fact]
    public void Acceptance_transfers_every_promised_item_atomically_and_disarms_the_offerer()
    {
        var engine = Engine();
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark", "goblin-salve"], ForfeitWeapon: true));

        var accepted = engine.Execute(new AcceptSurrenderAction("Rowan", OfferId(offer)));

        Assert.True(accepted.Accepted);
        var vark = engine.State.RequireById(TestWorld.VarkId);
        var rowan = engine.State.RequireById(TestWorld.RowanId);

        // Both promised items moved, and neither exists in two places.
        Assert.Empty(vark.Inventory);
        Assert.Contains(rowan.Inventory, i => i.Id == "purse-vark");
        Assert.Contains(rowan.Inventory, i => i.Id == "goblin-salve");

        // The offerer is disarmed, and the weapon is on the floor keeping its own stable id — one entity, moved.
        Assert.True(vark.IsDisarmed);
        var ground = Assert.Single(engine.State.Room.Objects.OfType<Container>(), c => c.IsGround);
        var forfeited = Assert.Single(ground.Contents);
        Assert.Equal(TestWorld.VarkV07().Weapon!.Id, forfeited.Id);
        Assert.Equal("Notched Sabre", forfeited.Name);
    }

    [Fact]
    public void Acceptance_creates_a_durable_agreement_answering_every_required_question()
    {
        var engine = Engine();
        var offer = engine.Execute(new OfferSurrenderAction(
            "Vark", "Rowan", ["purse-vark"], ForfeitWeapon: true, AssociatedSpeechEventId: 3));

        engine.Execute(new AcceptSurrenderAction("Rowan", OfferId(offer)));

        var agreement = Assert.Single(engine.State.SurrenderAgreements);
        Assert.Equal(TestWorld.VarkId, agreement.OffererId);
        Assert.Equal(TestWorld.RowanId, agreement.AcceptedById);
        Assert.Equal(["purse-vark"], agreement.TransferredItemIds.ToArray());
        Assert.Equal(TestWorld.VarkV07().Weapon!.Id, agreement.ForfeitedWeaponId);
        Assert.Equal(3, agreement.AssociatedSpeechEventId);
        Assert.Equal(OfferId(offer), agreement.OfferId);
    }

    [Fact]
    public void Acceptance_without_a_promised_weapon_records_no_forfeiture_even_though_yielding_disarms()
    {
        var engine = Engine();
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false));

        engine.Execute(new AcceptSurrenderAction("Rowan", OfferId(offer)));

        // A character who has yielded does not stand there with a raised weapon, so it leaves their hand —
        // but it was not a promised term, so the agreement records no forfeiture.
        Assert.True(engine.State.RequireById(TestWorld.VarkId).IsDisarmed);
        Assert.Null(Assert.Single(engine.State.SurrenderAgreements).ForfeitedWeaponId);
    }

    [Fact]
    public void The_narrated_outcome_never_claims_an_unpromised_weapon_was_part_of_the_bargain()
    {
        // v0.10: mandatory disarmament and negotiated tribute are two different facts, and the narration
        // must not blur them — a weapon that fell away only because yielding disarms regardless must not
        // read as though the offerer had struck that bargain.
        var engine = Engine();
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false));

        var accepted = engine.Execute(new AcceptSurrenderAction("Rowan", OfferId(offer)));

        var outcome = (AcceptSurrenderOutcome)accepted.Outcome!;
        Assert.False(outcome.WeaponWasPromised);
        Assert.Equal("Notched Sabre", outcome.ForfeitedWeaponName);
        Assert.DoesNotContain("as promised", outcome.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not part of the bargain", outcome.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DISARMED", outcome.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_narrated_outcome_credits_a_genuinely_promised_weapon_as_promised()
    {
        var engine = Engine();
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: true));

        var accepted = engine.Execute(new AcceptSurrenderAction("Rowan", OfferId(offer)));

        var outcome = (AcceptSurrenderOutcome)accepted.Outcome!;
        Assert.True(outcome.WeaponWasPromised);
        Assert.Contains("as promised", outcome.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not part of the bargain", outcome.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Acceptance_changes_the_offerers_disposition_to_surrendered()
    {
        var engine = Engine();
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false));

        engine.Execute(new AcceptSurrenderAction("Rowan", OfferId(offer)));

        var vark = engine.State.RequireById(TestWorld.VarkId);
        Assert.Equal(CharacterDisposition.Surrendered, vark.Disposition);
        Assert.True(vark.IsAlive);
        Assert.True(vark.IsPresent);
        Assert.False(vark.CanAct);
        Assert.False(vark.IsCombatTarget);

        Assert.Equal(SurrenderOfferState.Accepted, engine.State.FindOffer(OfferId(offer))!.State);
    }

    [Fact]
    public void Stale_offered_ownership_makes_the_acceptance_fail_and_transfers_nothing()
    {
        // A pending offer promising something its offerer does not have. Ordinary play cannot reach this — a
        // promised asset leaving the offerer's hands invalidates the offer at once — so the state is built
        // directly, to prove the acceptance itself re-checks ownership rather than trusting the offer's word.
        var pending = new SurrenderOffer
        {
            Id = "offer-stale",
            OffererId = TestWorld.VarkId,
            RecipientId = TestWorld.RowanId,
            OfferedItemIds = ["a-thing-vark-never-had"],
            ForfeitWeapon = true,
            CreatedRound = 1,
            CreatedTurn = 3,
            State = SurrenderOfferState.Pending
        };

        var state = TestWorld.V07State() with { SurrenderOffers = [pending] };
        var engine = new GameEngine(state, new SeededRng(1), CombatRules.NoGlancing);

        var accepted = engine.Execute(new AcceptSurrenderAction("Rowan", "offer-stale"));

        Assert.False(accepted.Accepted);
        Assert.Equal(EngineRejectionReason.PromisedAssetNoLongerAvailable, accepted.RejectionReason);

        // Nothing moved and nobody yielded: Rowan gained nothing, Vark keeps everything and his sabre.
        var rowan = engine.State.RequireById(TestWorld.RowanId);
        var vark = engine.State.RequireById(TestWorld.VarkId);
        Assert.Equal(["purse-rowan"], rowan.Inventory.Select(i => i.Id).ToArray());
        Assert.Contains(vark.Inventory, i => i.Id == "purse-vark");
        Assert.NotNull(vark.Weapon);
        Assert.Equal(CharacterDisposition.Active, vark.Disposition);
        Assert.Empty(engine.State.SurrenderAgreements);
        Assert.DoesNotContain(engine.State.Room.Objects.OfType<Container>(), c => c.IsGround);
    }

    [Fact]
    public void Consuming_a_promised_item_invalidates_the_pending_offer_at_once()
    {
        var engine = Engine(characters: [TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(health: 6), TestWorld.SkritV07()]);
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["goblin-salve"], ForfeitWeapon: false));

        var used = engine.Execute(new UseItemAction("Vark", "goblin-salve"));

        Assert.True(used.Accepted);
        var updated = engine.State.FindOffer(OfferId(offer))!;
        Assert.Equal(SurrenderOfferState.Invalidated, updated.State);
        Assert.Contains(used.OfferTransitions, t => t.New == SurrenderOfferState.Invalidated);
    }

    // ------------------------------------------------------------------------------------------
    // Rejection, expiry and invalidation — none of which transfers anything
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_hostile_act_by_the_recipient_rejects_the_offer_and_transfers_nothing()
    {
        var engine = Engine();
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: true));

        var struck = engine.Execute(new AttackCharacterAction("Rowan", "Vark", "Longsword"));

        Assert.True(struck.Accepted);
        Assert.Equal(SurrenderOfferState.Rejected, engine.State.FindOffer(OfferId(offer))!.State);
        Assert.Contains(struck.OfferTransitions, t => t.New == SurrenderOfferState.Rejected);
        Assert.DoesNotContain(engine.State.RequireById(TestWorld.RowanId).Inventory, i => i.Id == "purse-vark");
        Assert.NotNull(engine.State.RequireById(TestWorld.VarkId).Weapon);
    }

    [Fact]
    public void A_hostile_act_by_the_recipients_teammate_also_rejects_the_offer()
    {
        var engine = Engine();
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false));

        // Elara, on Rowan's side, strikes the offerer. The offer was made to their side, and their side answered it.
        Assert.True(engine.Execute(new AttackCharacterAction("Elara", "Vark", "Iron Mace")).Accepted);

        Assert.Equal(SurrenderOfferState.Rejected, engine.State.FindOffer(OfferId(offer))!.State);
    }

    [Fact]
    public void An_attack_by_somebody_outside_the_negotiation_does_not_reject_the_offer()
    {
        var engine = Engine();
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false));

        // Skrit attacking a hero is nothing to do with Vark's offer to Rowan, and must not settle it.
        Assert.True(engine.Execute(new AttackCharacterAction("Skrit", "Elara", "Crude Spear")).Accepted);

        Assert.Equal(SurrenderOfferState.Pending, engine.State.FindOffer(OfferId(offer))!.State);
    }

    [Fact]
    public void An_offer_the_recipient_ignores_expires_at_the_end_of_their_turn()
    {
        var engine = Engine();
        engine.BeginActorTurn(TestWorld.VarkId, round: 1, turn: 3);
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false));
        engine.EndActorTurn(TestWorld.VarkId, round: 1, turn: 3);

        // The offer survives the offerer's own turn ending.
        Assert.Equal(SurrenderOfferState.Pending, engine.State.FindOffer(OfferId(offer))!.State);

        // Rowan takes his turn and does something else entirely; at the end of it the offer lapses.
        engine.BeginActorTurn(TestWorld.RowanId, round: 2, turn: 5);
        var upkeep = engine.EndActorTurn(TestWorld.RowanId, round: 2, turn: 5);

        Assert.Equal(SurrenderOfferState.Expired, engine.State.FindOffer(OfferId(offer))!.State);
        var transition = Assert.Single(upkeep.OfferTransitions);
        Assert.Equal(SurrenderOfferState.Expired, transition.New);
        Assert.Equal(2, transition.Offer.ResolvedTurn - transition.Offer.CreatedTurn);
        Assert.DoesNotContain(engine.State.RequireById(TestWorld.RowanId).Inventory, i => i.Id == "purse-vark");
    }

    [Fact]
    public void A_dead_recipient_invalidates_the_offer()
    {
        var engine = Engine(null, TestWorld.RowanV07(health: 1), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false));

        // Skrit kills the recipient before he can answer.
        var killed = engine.Execute(new AttackCharacterAction("Skrit", "Rowan", "Crude Spear"));
        Assert.True(killed.Accepted);
        Assert.Equal(CharacterDisposition.Dead, engine.State.RequireById(TestWorld.RowanId).Disposition);

        Assert.Equal(SurrenderOfferState.Invalidated, engine.State.FindOffer(OfferId(offer))!.State);
        Assert.Contains(killed.OfferTransitions, t => t.New == SurrenderOfferState.Invalidated);
    }

    [Fact]
    public void An_escaped_recipient_invalidates_the_offer()
    {
        var engine = new GameEngine(TestWorld.V07State(exitOpen: true), new SeededRng(1), CombatRules.NoGlancing);
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false));

        var escaped = engine.Execute(new EscapeEncounterAction("Rowan", "Cellar Stair Door"));

        Assert.True(escaped.Accepted);
        Assert.Equal(SurrenderOfferState.Invalidated, engine.State.FindOffer(OfferId(offer))!.State);
    }

    [Fact]
    public void A_dead_offerer_invalidates_the_offer()
    {
        var engine = Engine(null, TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(health: 1), TestWorld.SkritV07());
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false));

        // Elara, not the recipient's decision-maker, cuts the offerer down. The offer dies with him.
        Assert.True(engine.Execute(new AttackCharacterAction("Elara", "Vark", "Iron Mace")).Accepted);
        Assert.Equal(CharacterDisposition.Dead, engine.State.RequireById(TestWorld.VarkId).Disposition);

        var settled = engine.State.FindOffer(OfferId(offer))!;
        Assert.False(settled.IsPending);
        Assert.Empty(engine.State.SurrenderAgreements);
    }

    [Fact]
    public void An_offerer_who_leaves_active_play_cannot_have_their_offer_accepted()
    {
        var engine = new GameEngine(TestWorld.V07State(exitOpen: true), new SeededRng(1), CombatRules.NoGlancing);
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false));
        Assert.True(engine.Execute(new EscapeEncounterAction("Vark", "Cellar Stair Door")).Accepted);

        var accepted = engine.Execute(new AcceptSurrenderAction("Rowan", OfferId(offer)));

        Assert.False(accepted.Accepted);
        // The offer was already invalidated when the offerer left, so the acceptance finds nothing open.
        Assert.Equal(EngineRejectionReason.OfferNoLongerPending, accepted.RejectionReason);
        Assert.Empty(engine.State.SurrenderAgreements);
    }

    [Fact]
    public void An_offer_cannot_be_accepted_twice()
    {
        var engine = Engine();
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false));
        Assert.True(engine.Execute(new AcceptSurrenderAction("Rowan", OfferId(offer))).Accepted);

        var again = engine.Execute(new AcceptSurrenderAction("Rowan", OfferId(offer)));

        Assert.False(again.Accepted);
        Assert.Equal(EngineRejectionReason.OfferNoLongerPending, again.RejectionReason);
        Assert.Single(engine.State.SurrenderAgreements);
    }

    [Fact]
    public void A_better_offer_may_be_made_on_a_later_turn_after_one_lapses()
    {
        var engine = Engine();
        engine.BeginActorTurn(TestWorld.VarkId, 1, 3);
        var first = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["goblin-salve"], ForfeitWeapon: false));
        engine.EndActorTurn(TestWorld.VarkId, 1, 3);
        engine.BeginActorTurn(TestWorld.RowanId, 2, 5);
        engine.EndActorTurn(TestWorld.RowanId, 2, 5);
        Assert.Equal(SurrenderOfferState.Expired, engine.State.FindOffer(OfferId(first))!.State);

        // With the first offer settled, richer terms can be put on the table.
        engine.BeginActorTurn(TestWorld.VarkId, 2, 7);
        var second = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["goblin-salve", "purse-vark"], ForfeitWeapon: true));

        Assert.True(second.Accepted);
        Assert.Equal(2, engine.State.SurrenderOffers.Length);
        Assert.Single(engine.State.PendingOffers());
    }

    // ------------------------------------------------------------------------------------------
    // Consequences for the encounter
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void An_accepted_surrender_removes_the_offerers_combat_statuses()
    {
        var engine = Engine();
        engine.BeginActorTurn(TestWorld.VarkId, 1, 3);
        Assert.True(engine.Execute(new DefendAction("Vark")).Accepted);
        Assert.NotNull(engine.State.StatusOn(TestWorld.VarkId, StatusEffectKind.Defending));

        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: false));
        var accepted = engine.Execute(new AcceptSurrenderAction("Rowan", OfferId(offer)));

        Assert.True(accepted.Accepted);
        Assert.Empty(engine.State.StatusesOn(TestWorld.VarkId));
        Assert.Contains(accepted.StatusEvents, e => e.Kind == StatusEventKind.Removed);
    }

    [Fact]
    public void An_accepted_surrender_that_empties_a_team_ends_the_encounter_as_a_surrender()
    {
        var engine = Engine(null, TestWorld.RowanV07(), TestWorld.ElaraV07(),
            TestWorld.VarkV07(), TestWorld.SkritV07() with { Disposition = CharacterDisposition.Surrendered });

        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: true));
        engine.Execute(new AcceptSurrenderAction("Rowan", OfferId(offer)));

        var outcome = TerminalCondition.Evaluate(engine.State);
        Assert.True(outcome.IsOver);
        Assert.Equal(EncounterOutcome.Surrender, outcome.Outcome);
        Assert.Equal([TestWorld.HeroesTeam], outcome.WinningTeams);
        Assert.All(engine.State.Characters, c => Assert.True(c.IsAlive));
    }

    [Fact]
    public void A_looted_forfeited_weapon_is_an_inventory_trophy_not_an_equipped_weapon()
    {
        var engine = Engine();
        var offer = engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: true));
        engine.Execute(new AcceptSurrenderAction("Rowan", OfferId(offer)));

        var takeFromFloor = engine.Execute(new TakeItemAction("Skrit", Container.GroundId, "Notched Sabre"));

        Assert.True(takeFromFloor.Accepted);
        var skrit = engine.State.RequireById(TestWorld.SkritId);
        Assert.Contains(skrit.Inventory, i => i.Name == "Notched Sabre");
        // It did not become the weapon in its hand: there is no equipping in v0.7.
        Assert.Equal("Crude Spear", skrit.Weapon!.Name);
    }
}
