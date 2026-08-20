using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Rulebook;
using ModelsAndMonsters.Rulebook.Selection;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.8 rule-selection strategies: the shipped whole-rulebook path, the experimental alternatives, and
/// the safety property every one of them has to hold — fall back rather than guess.
/// </summary>
public sealed class RuleSelectionTests
{
    private static readonly RuleCatalog Catalog = new();

    private static readonly PromptLibrary Prompts =
        PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));

    private static CompactIndexSelector IndexSelector(ScriptedChatClient client, bool cache = false)
    {
        var profile = new AgentModelProfile
        {
            AgentName = "RuleIndexSelector",
            Provider = ModelProvider.Ollama,
            ModelId = "scripted-model",
            Temperature = 0.1f
        };
        var trace = new ExperimentTrace("selection-test", new RecordingTraceSink());
        return new CompactIndexSelector(profile, new TracingChatClient(client, profile, trace), Catalog, cache);
    }

    // ------------------------------------------------------------------------------------------
    // Card metadata
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Every_card_declares_a_summary_and_only_declares_related_rules_that_exist()
    {
        foreach (var card in Catalog.AllCards)
        {
            Assert.False(string.IsNullOrWhiteSpace(card.Summary), $"{card.RuleId} has no summary.");
            Assert.DoesNotContain(card.RuleId, card.RelatedRuleIds);

            foreach (var related in card.RelatedRuleIds)
            {
                Assert.NotNull(Catalog.Find(related));
            }
        }
    }

    [Fact]
    public void The_compact_index_is_one_short_line_per_card()
    {
        var index = RuleSelectionSupport.BuildIndex(Catalog);
        var lines = index.Split('\n');

        Assert.Equal(Catalog.AllCards.Count, lines.Length);
        Assert.All(lines, line => Assert.True(line.Length < 220, $"Index line too long to be an index: {line}"));

        // And it is genuinely a fraction of the full book — that is the whole point of it.
        var full = Catalog.AllCards.Sum(c => c.ToResolverBlock().Length);
        Assert.True(index.Length * 5 < full,
            $"The index is {index.Length} characters against {full} of cards — not compact enough to be worth a call.");
    }

    [Fact]
    public void A_reference_card_is_marked_as_a_mechanic_rather_than_an_action()
    {
        var morale = Catalog.Find("combat.morale")!;

        Assert.True(morale.IsReference);
        Assert.Contains("BACKGROUND MECHANIC", morale.ToResolverBlock(), StringComparison.Ordinal);
        Assert.Contains("background mechanic", morale.ToIndexLine(), StringComparison.Ordinal);

        // It is not an engine action, so it can never be offered to the Dungeon Master as one.
        Assert.False(DungeonMasterTools.EngineActionsByName.ContainsKey(morale.ActionName));
    }

    // ------------------------------------------------------------------------------------------
    // The request budget: the rulebook has to fit the resolver's window with room to answer in
    // ------------------------------------------------------------------------------------------

    private static AgentModelProfile LocalResolverProfile(int? contextWindow = 8192) => new()
    {
        AgentName = "RulebookResolver",
        Provider = ModelProvider.Ollama,
        ModelId = "qwen3.5:9b",
        ContextWindow = contextWindow
    };

    [Fact]
    public void The_resolver_is_shown_what_an_action_IS_and_IS_NOT_and_nothing_about_what_follows()
    {
        // Since hydration the consultant fills the consequence fields from the card itself, so sending them
        // to the resolver is asking it to read text it cannot use. They are the majority of a card.
        var block = Catalog.Find("inventory.steal")!.ToResolverBlock();

        Assert.Contains("what it is:", block, StringComparison.Ordinal);
        Assert.Contains("preconditions:", block, StringComparison.Ordinal);
        Assert.Contains("not supported:", block, StringComparison.Ordinal);

        Assert.DoesNotContain("turn cost:", block, StringComparison.Ordinal);
        Assert.DoesNotContain("rng:", block, StringComparison.Ordinal);
        Assert.DoesNotContain("visibility:", block, StringComparison.Ordinal);
        Assert.DoesNotContain("on success:", block, StringComparison.Ordinal);
        Assert.DoesNotContain("on failure:", block, StringComparison.Ordinal);
        Assert.DoesNotContain("required bindings:", block, StringComparison.Ordinal);
    }

    [Fact]
    public void The_whole_rulebook_fits_a_local_8k_window_with_real_room_to_answer_in()
    {
        // The failure this guards is silent: an over-full request does not error, it comes back as a
        // truncated reply that reads downstream as malformed guidance and an in-world refusal. A v0.8 live
        // run had ~127 tokens of headroom and lost 5 of 57 consultations that way.
        var estimate = RulebookRequestBudget.Measure(Catalog, Prompts, LocalResolverProfile(), configuredOutputTokens: 300)!;

        Assert.True(estimate.Fits,
            $"A whole-rulebook request is ~{estimate.RequestTokens} tokens against a {estimate.ContextWindowTokens}-token " +
            $"window, leaving {estimate.HeadroomTokens} to reply in.");

        // And not merely by a hair: the book grows every release, and the margin is what absorbs that.
        Assert.True(estimate.HeadroomTokens > 1500,
            $"Only {estimate.HeadroomTokens} tokens of headroom — too tight for the next card to be safe.");
    }

    [Fact]
    public void A_rulebook_too_large_for_its_window_is_refused_at_startup_rather_than_at_run_time()
    {
        var tiny = LocalResolverProfile(contextWindow: 4096);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            RulebookRequestBudget.Validate(Catalog, Prompts, tiny, configuredOutputTokens: 300));

        Assert.Contains("does not fit", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will NOT error", ex.Message, StringComparison.Ordinal);

        // And it must not suggest the fix that costs a second full copy of the model weights.
        Assert.Contains("Raising the resolver's ContextWindow is the wrong fix", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_context_window_is_not_guessed_at()
    {
        // A hosted model whose window the harness does not know is not something to invent a number for.
        Assert.Null(RulebookRequestBudget.Measure(Catalog, Prompts, LocalResolverProfile(contextWindow: null), 300));
        RulebookRequestBudget.Validate(Catalog, Prompts, LocalResolverProfile(contextWindow: null), 300);
    }

    // ------------------------------------------------------------------------------------------
    // The shipped path
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_whole_rulebook_selector_sends_everything_and_never_falls_back()
    {
        var selection = await new WholeRulebookSelector(Catalog).SelectAsync("anything at all", default);

        Assert.Equal(Catalog.AllCards.Count, selection.Cards.Count);
        Assert.False(selection.FellBack);
        Assert.Equal(0, selection.ModelCalls);
    }

    // ------------------------------------------------------------------------------------------
    // Structured routing
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Structured_routing_returns_the_declared_cards_for_a_known_action_family()
    {
        var selector = new StructuredRoutingSelector(Catalog, _ => DungeonMasterTools.IntimidateCharacterName);

        var selection = await selector.SelectAsync("(the caller already knows)", default);

        Assert.False(selection.FellBack);
        Assert.Equal(["combat.intimidate"], selection.DirectlySelectedRuleIds);

        // The declared links pulled the morale card in — the one the decision turns on and the intent
        // would never have named.
        Assert.Contains("combat.morale", selection.ExpandedRuleIds);
        Assert.Contains(selection.Cards, c => c.RuleId == "combat.morale");
    }

    [Fact]
    public async Task Structured_routing_falls_back_rather_than_inferring_a_family_from_language()
    {
        var selector = new StructuredRoutingSelector(Catalog, _ => null);

        var selection = await selector.SelectAsync("I level my blade at Vark and tell him he is next", default);

        Assert.True(selection.FellBack);
        Assert.Equal(Catalog.AllCards.Count, selection.Cards.Count);
        Assert.Contains("never infers", selection.FallbackReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Structured_routing_falls_back_for_an_action_no_card_declares()
    {
        var selector = new StructuredRoutingSelector(Catalog, _ => "fly_away");

        var selection = await selector.SelectAsync("anything", default);

        Assert.True(selection.FellBack);
    }

    // ------------------------------------------------------------------------------------------
    // The compact index
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_compact_index_selector_resolves_with_only_the_chosen_cards_and_their_declared_links()
    {
        var client = new ScriptedChatClient(ScriptedChatClient.Text("""{"ruleIds": ["combat.intimidate"]}"""));

        var selection = await IndexSelector(client).SelectAsync("I threaten Vark", default);

        Assert.False(selection.FellBack);
        Assert.Equal(["combat.intimidate"], selection.DirectlySelectedRuleIds);
        Assert.Contains("combat.morale", selection.ExpandedRuleIds);

        // The rejection is always present, or an unsupported intent has no card to be refused under.
        Assert.Contains(selection.Cards, c => c.RuleId == RuleCatalog.RejectRuleId);
        Assert.True(selection.Cards.Count < Catalog.AllCards.Count);
        Assert.Equal(1, selection.ModelCalls);
    }

    [Fact]
    public async Task An_unparseable_selection_reply_falls_back_to_the_whole_bounded_rulebook()
    {
        var client = new ScriptedChatClient(ScriptedChatClient.Text("I think probably the attack one?"));

        var selection = await IndexSelector(client).SelectAsync("I hit him", default);

        Assert.True(selection.FellBack);
        Assert.Equal(Catalog.AllCards.Count, selection.Cards.Count);
        Assert.Contains("nothing parseable", selection.FallbackReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_selection_naming_only_unknown_rules_falls_back()
    {
        var client = new ScriptedChatClient(ScriptedChatClient.Text("""{"ruleIds": ["combat.fly", "magic.fireball"]}"""));

        var selection = await IndexSelector(client).SelectAsync("I cast a fireball", default);

        Assert.True(selection.FellBack);
        Assert.Contains("named no rule this catalog holds", selection.FallbackReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_selection_naming_one_real_rule_among_inventions_keeps_the_real_one()
    {
        var client = new ScriptedChatClient(
            ScriptedChatClient.Text("""{"ruleIds": ["magic.fireball", "combat.attack"]}"""));

        var selection = await IndexSelector(client).SelectAsync("I hit him", default);

        Assert.False(selection.FellBack);
        Assert.Equal(["combat.attack"], selection.DirectlySelectedRuleIds);
    }

    [Fact]
    public async Task A_failed_selection_call_falls_back_rather_than_failing_the_consultation()
    {
        // An empty scripted client throws on the first call, standing for a provider error.
        var selection = await IndexSelector(new ScriptedChatClient()).SelectAsync("I hit him", default);

        Assert.True(selection.FellBack);
        Assert.Equal(Catalog.AllCards.Count, selection.Cards.Count);
        Assert.Contains("failed", selection.FallbackReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_selection_cache_is_keyed_on_the_static_index_and_makes_no_second_call()
    {
        var client = new ScriptedChatClient(ScriptedChatClient.Text("""{"ruleIds": ["combat.attack"]}"""));
        var selector = IndexSelector(client, cache: true);

        var first = await selector.SelectAsync("I hit him", default);
        var second = await selector.SelectAsync("I hit him", default);

        Assert.Equal(first.DirectlySelectedRuleIds, second.DirectlySelectedRuleIds);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(0, second.ModelCalls);
    }

    [Theory]
    [InlineData("""{"ruleIds": ["a", "b"]}""", 2)]
    [InlineData("""Here you go: {"ruleIds": ["a"]} — hope that helps.""", 1)]
    [InlineData("```json\n{\"ruleIds\": [\"a\"]}\n```", 1)]
    [InlineData("""{"ruleIds": []}""", 0)]
    [InlineData("""{"somethingElse": ["a"]}""", 0)]
    [InlineData("not json at all", 0)]
    [InlineData("", 0)]
    public void Selection_replies_are_parsed_tolerantly_and_never_throw(string reply, int expected) =>
        Assert.Equal(expected, CompactIndexSelector.TryParseIds(reply).Count);

    // ------------------------------------------------------------------------------------------
    // Embedding retrieval
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unconfigured_embedder_makes_the_selector_fall_back_on_every_call()
    {
        var selector = new EmbeddingRuleSelector(Catalog, new UnavailableRuleEmbedder());

        var selection = await selector.SelectAsync("I hit him", default);

        Assert.True(selection.FellBack);
        Assert.Contains("no embedding provider is configured", selection.FallbackReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_offline_prototype_embedder_retrieves_a_bounded_set_with_scores_recorded()
    {
        var selector = new EmbeddingRuleSelector(Catalog, new HashingRuleEmbedder(), topK: 3);

        var selection = await selector.SelectAsync(
            "I reach into the open chest and take the vial of salve.", default);

        Assert.False(selection.FellBack);
        Assert.True(selection.DirectlySelectedRuleIds.Count <= 3);
        Assert.All(selection.SelectionReasons, reason => Assert.Contains("similarity", reason, StringComparison.Ordinal));
        Assert.True(selection.Cards.Count < Catalog.AllCards.Count);
    }

    [Fact]
    public async Task Embedding_retrieval_falls_back_when_nothing_clears_the_confidence_floor()
    {
        // A floor nothing can reach stands for "this retrieval has no idea", and the whole book is sent.
        var selector = new EmbeddingRuleSelector(Catalog, new HashingRuleEmbedder(), topK: 3, minimumSimilarity: 1.01);

        var selection = await selector.SelectAsync("I hit him", default);

        Assert.True(selection.FellBack);
        Assert.Contains("below the confidence floor", selection.FallbackReason!, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------------------
    // The labelled corpus and its measurement
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_corpus_covers_every_family_the_brief_names_and_labels_only_real_rules()
    {
        var categories = RuleSelectionCorpus.Cases.Select(c => c.Category).ToHashSet(StringComparer.Ordinal);

        foreach (var required in new[]
                 {
                     "attack vs ability", "inspect vs open vs take", "give vs drop vs steal",
                     "exit vs escape", "offer vs accept", "threat vs speech", "steady vs speech",
                     "compound", "stale ownership", "unsupported"
                 })
        {
            Assert.Contains(required, categories);
        }

        foreach (var labelled in RuleSelectionCorpus.Cases)
        {
            Assert.NotEmpty(labelled.RequiredRuleIds);
            Assert.All(labelled.RequiredRuleIds, id => Assert.NotNull(Catalog.Find(id)));

            if (labelled.ExpectedAction is { } action)
            {
                Assert.True(DungeonMasterTools.EngineActionsByName.ContainsKey(action),
                    $"{labelled.Id} expects '{action}', which is not an engine action.");
            }
        }
    }

    [Fact]
    public async Task The_baseline_loses_nothing_and_the_oracle_shows_how_much_of_it_was_unnecessary()
    {
        var evaluator = new RuleSelectionEvaluator(Catalog, Prompts);

        var baseline = await evaluator.MeasureAsync("baseline", new WholeRulebookSelector(Catalog));
        var oracle = await evaluator.MeasureAsync("oracle", new OracleRuleSelector(Catalog));

        // The baseline cannot lose a card, by construction.
        Assert.Equal(1.0, baseline.RequiredCardRecall);
        Assert.Equal(baseline.Cases, baseline.CasesWithEveryRequiredCard);
        Assert.Equal(baseline.Cases, baseline.DecisionAgreements);
        Assert.Equal(0, baseline.IncorrectlySupported);
        Assert.Equal(0, baseline.IncorrectlyUnsupported);

        // The oracle is the ceiling: perfect, and far cheaper — which is what makes the investigation worth
        // running at all. A regression that made the ceiling stop being cheap would be worth knowing about.
        Assert.Equal(1.0, oracle.RequiredCardRecall);
        Assert.True(oracle.AverageTotalInputTokens < baseline.AverageTotalInputTokens * 0.5,
            $"The ceiling costs {oracle.AverageTotalInputTokens:F0} against the baseline's " +
            $"{baseline.AverageTotalInputTokens:F0} — there is no saving left to chase.");
    }

    [Fact]
    public async Task A_strategy_that_falls_back_everywhere_costs_exactly_what_the_baseline_costs()
    {
        var evaluator = new RuleSelectionEvaluator(Catalog, Prompts);

        var baseline = await evaluator.MeasureAsync("baseline", new WholeRulebookSelector(Catalog));
        var routing = await evaluator.MeasureAsync("routing", new StructuredRoutingSelector(Catalog, _ => null));

        // The safety property, stated as a measurement: the worst case of a cheaper strategy is the cost the
        // shipped path already pays, and never a wrong answer.
        Assert.Equal(routing.Cases, routing.Fallbacks);
        Assert.Equal(baseline.AverageResolverInputTokens, routing.AverageResolverInputTokens);
        Assert.Equal(1.0, routing.RequiredCardRecall);
    }
}
