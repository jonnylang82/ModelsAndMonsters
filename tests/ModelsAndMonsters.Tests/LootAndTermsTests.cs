using Microsoft.Extensions.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Rulebook;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// Two places where the fiction and the state kept coming apart, both found in live runs and both fixed by
/// telling the truth about state rather than by asking a model to try harder.
/// </summary>
public sealed class LootAndTermsTests
{
    private static readonly RuleCatalog Catalog = new();

    private static RuleCard Card(string id) => Catalog.Find(id)!;

    // ------------------------------------------------------------------------------------------
    // Looting the fallen
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Looting_the_dead_is_claimed_by_the_take_card()
    {
        // The card summary already said "or off the floor or a body", but the DESCRIPTION — the part the
        // resolver reads to decide what an intent is — mentioned only containers and the floor. So an intent
        // like "I pick up the flask that spilled from Rowan's body" matched nothing, and the resolver
        // answered unsupported. Across two live runs that cost eight refused attempts and, on one of them,
        // five refusals inside a single round from two different characters.
        var take = Card("container.take");

        Assert.Contains("BODY", take.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("floor", take.Description, StringComparison.OrdinalIgnoreCase);

        // The bindings have to name it too, or the DM has a rule it cannot fill in.
        Assert.Contains(take.RequiredBindings, b => b.Contains("body", StringComparison.OrdinalIgnoreCase));

        // And the precondition must not read as though a body were something that has to be opened first.
        Assert.Contains(take.Preconditions, p =>
            p.Contains("open", StringComparison.OrdinalIgnoreCase)
            && p.Contains("body", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_two_grab_cards_disagree_about_the_dead_in_the_right_direction()
    {
        // Both cards describe taking something that is not yours. Only one of them may claim a corpse, or
        // the resolver picks whichever it saw first: a live run had a goblin burn a whole turn on three
        // rewordings of "loot the gold from dead Rowan", every one routed to a theft the engine refuses.
        var take = Card("container.take");

        Assert.Contains(take.Exclusions, e =>
            e.Contains("DEAD", StringComparison.Ordinal)
            && e.Contains("taken from it", StringComparison.OrdinalIgnoreCase));

        // Theft is narrowed to the living in the same breath, so the two do not overlap.
        Assert.Contains(take.Exclusions, e => e.Contains("LIVING", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------
    // Accepting terms nobody offered
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Acceptance_is_withdrawn_when_no_terms_are_on_the_table()
    {
        // The failure this prevents: a character says "I accept his offer and take the purse" when no offer
        // was ever recorded — because the offerer handed the goods over while merely TALKING about terms,
        // which resolves as an ordinary give. The rulebook reads the words and answers "acceptance", quite
        // correctly; whether terms actually stand is state it never sees.
        //
        // Left available, the Dungeon Master reaches for accept_surrender and has to invent the offer id to
        // fill the binding. A live run did exactly that three times in one turn — inventing "offer-1" each
        // time, the engine refusing each time with UnknownOffer — and the encounter hit the round limit
        // unresolved with the character's last turn spent entirely on it.
        var acceptVersion = new RuleCatalog().Find("encounter.accept-surrender")!.Version;
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.RejectActionName,
                    ("reason", "Nothing was promised to you."), ("category", "impossible")),
                ScriptedChatClient.Text("Nothing comes of it."),
                ScriptedChatClient.Text("Nothing comes of it."),
                ScriptedChatClient.Text("Rowan holds his ground.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName,
                    (CharacterTools.IntentParameter, "I accept the captain's offer and take his purse.")),
                ScriptedChatClient.Call("r-2", CharacterTools.EndTurnName)))),
            // No SurrenderOffers on the state: nothing has been offered to anyone.
            initialState: TestWorld.V07State(),
            rules: CombatRules.NoGlancing,
            scenario: TestWorld.V07Scenario(),
            rulebookResolverClient: new ScriptedChatClient(ScriptedChatClient.Text(
                $$"""
                { "supported": true, "candidateActions": ["accept_surrender"],
                  "citedRules": [ { "ruleId": "encounter.accept-surrender", "version": "{{acceptVersion}}" } ] }
                """)));

        await harness.RunTurn("Rowan", round: 2, turn: 5);

        // The rulebook named acceptance; the state withdrew it, because there is nothing to accept.
        var consultation = Assert.Single(harness.Sink.Payloads<RulebookConsultationPayload>(TraceEventType.RulebookConsultation));
        Assert.Contains(DungeonMasterTools.AcceptSurrenderName, consultation.DmCandidateTools);

        var dmTools = harness.DungeonMasterClient.RequestOptions[0]!.Tools!.Select(t => t.Name).ToList();
        Assert.DoesNotContain(DungeonMasterTools.AcceptSurrenderName, dmTools);

        // And the DM is told why in state terms, so its refusal can be honest rather than a guess.
        var request = string.Join(" ", harness.DungeonMasterClient.Requests[0]
            .Where(m => m.Role == ChatRole.User)
            .Select(m => m.Text));
        Assert.Contains("STATE THE RULEBOOK COULD NOT SEE", request, StringComparison.Ordinal);
        Assert.Contains("nobody has offered", request, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Acceptance_survives_when_terms_really_do_stand()
    {
        // The other half, and the reason this is a state check rather than a blanket removal: withdrawing it
        // whenever the rulebook looks unsure would break the case the widening was built for.
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

        var acceptVersion = new RuleCatalog().Find("encounter.accept-surrender")!.Version;
        var harness = new MultiActorHarness(
            new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AcceptSurrenderName,
                    ("recipient", "Rowan"), ("offer", "offer-1")),
                ScriptedChatClient.Text("Rowan takes the purse and lets him live.")),
            MultiActorHarness.Clients(("Rowan", new ScriptedChatClient(
                ScriptedChatClient.Call("r-1", CharacterTools.TakeActionName,
                    (CharacterTools.IntentParameter, "I accept the captain's offer and take his purse."))))),
            initialState: TestWorld.V07State() with { SurrenderOffers = [offer] },
            rules: CombatRules.NoGlancing,
            scenario: TestWorld.V07Scenario(),
            rulebookResolverClient: new ScriptedChatClient(ScriptedChatClient.Text(
                $$"""
                { "supported": true, "candidateActions": ["accept_surrender"],
                  "citedRules": [ { "ruleId": "encounter.accept-surrender", "version": "{{acceptVersion}}" } ] }
                """)));

        await harness.RunTurn("Rowan", round: 2, turn: 5);

        var dmTools = harness.DungeonMasterClient.RequestOptions[0]!.Tools!.Select(t => t.Name).ToList();
        Assert.Contains(DungeonMasterTools.AcceptSurrenderName, dmTools);
        Assert.Equal(CharacterDisposition.Surrendered, harness.Engine.State.RequireById(TestWorld.VarkId).Disposition);
    }
}
