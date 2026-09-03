using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Rulebook;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The rule catalog and the whole-rulebook retriever. There is no semantic routing: every card is sent to the
/// resolver on every call, whatever the intent, so a keyword index can never hide the one action an intent
/// needs — selecting the action is the resolver's job. The count/size limits are a hard ceiling that fails
/// visibly if the catalog outgrows a bounded request, rather than a budget that silently trims cards.
/// </summary>
public sealed class RulebookRetrievalTests
{
    private static readonly RuleCatalog Catalog = new();

    // Comfortably above the current catalog, matching the shipped defaults.
    private const int Ceiling = 40;
    private const int CharCeiling = 32000;

    [Fact]
    public void Every_card_has_the_required_fields_and_a_stable_content_version()
    {
        Assert.NotEmpty(Catalog.AllCards);
        Assert.All(Catalog.AllCards, card =>
        {
            Assert.False(string.IsNullOrWhiteSpace(card.RuleId));
            Assert.False(string.IsNullOrWhiteSpace(card.ActionName));
            Assert.False(string.IsNullOrWhiteSpace(card.Description));
            Assert.StartsWith("v1-", card.Version, StringComparison.Ordinal);
        });

        // The version is a pure function of content, so re-reading the same card yields the same version.
        var steal = Catalog.Find("inventory.steal")!;
        Assert.Equal(steal.Version, Catalog.Find("inventory.steal")!.Version);

        // Every supported engine action has a card, so the resolver can always cite a real rule.
        foreach (var action in DungeonMasterTools.EngineActionsByName.Keys)
        {
            Assert.Contains(Catalog.AllCards, c => string.Equals(c.ActionName, action, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    // Clear intents, terse intents, empty intents — and, crucially, keyword-free paraphrases that contain
    // none of the action's own words: the point of removing keyword routing is that the retriever hands the
    // resolver the same complete rulebook regardless, so no phrasing can hide a card.
    [InlineData("I bring my longsword down on the goblin")]
    [InlineData("I seize the small healing potion from the open medicine case.")]
    [InlineData("I would rather be anywhere but this cellar, so I make for the way the goblins first came in and put it all behind me.")]
    [InlineData("Enough. I lower everything and want no more part in any of this.")]
    [InlineData("hmm")]
    [InlineData("")]
    public void The_retriever_sends_the_whole_rulebook_on_every_call(string intent)
    {
        var retriever = new RuleRetriever(Catalog, Ceiling, CharCeiling);

        var result = retriever.Retrieve(intent);

        // Every card, every time — no subset, no fallback, no trimming.
        Assert.Equal(Catalog.AllCards.Count, result.SelectedCards.Count);
        Assert.False(result.Trimmed);
        foreach (var card in Catalog.AllCards)
        {
            Assert.Contains(result.SelectedCards, c => c.RuleId == card.RuleId);
        }
    }

    [Fact]
    public void The_request_stays_the_same_size_whatever_the_intent()
    {
        var retriever = new RuleRetriever(Catalog, Ceiling, CharCeiling);

        // The resolver request depends only on the (fixed) rulebook, never on the intent — so it cannot grow
        // with the length of the encounter, which is the whole point of the stateless bounded consultation.
        var a = retriever.Retrieve("I attack").TotalInputChars;
        var b = retriever.Retrieve("I do something long and rambling and full of unrelated words " + new string('x', 500)).TotalInputChars;

        Assert.Equal(a, b);
        Assert.True(a <= CharCeiling);
    }

    [Theory]
    // The reviewer's semantic-overreach case: "I grip my spear tighter and take a threatening step forward"
    // must NOT become attack_character. The resolver reads the combat.attack card to classify, so the card
    // must state plainly that a threat, an advance, a readied weapon or posturing with no blow is not an
    // attack. These pin the exclusion text the resolver relies on, so it cannot silently regress.
    [InlineData("threatening")]
    [InlineData("posturing")]
    [InlineData("readying")]
    [InlineData("intimidat")]
    [InlineData("no blow")]
    public void The_attack_card_states_that_posturing_and_intimidation_are_not_an_attack(string phrase)
    {
        var attack = Catalog.Find("combat.attack")!;
        var text = attack.Description + " " + string.Join(" ", attack.Preconditions) + " " + string.Join(" ", attack.Exclusions);

        Assert.Contains(phrase, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_catalog_that_exceeds_the_card_count_ceiling_fails_visibly_rather_than_trimming()
    {
        // A ceiling below the catalog size must throw at construction, not silently drop cards — a dropped
        // card is exactly the failure mode (a relevant action hidden from the resolver) this design prevents.
        var ex = Assert.Throws<InvalidOperationException>(() => new RuleRetriever(Catalog, maxCards: 5, maxInputChars: CharCeiling));
        Assert.Contains("never trims", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"{Catalog.AllCards.Count} cards", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_catalog_that_exceeds_the_input_size_ceiling_fails_visibly_rather_than_trimming()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new RuleRetriever(Catalog, maxCards: Ceiling, maxInputChars: 500));
        Assert.Contains("never trims", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
