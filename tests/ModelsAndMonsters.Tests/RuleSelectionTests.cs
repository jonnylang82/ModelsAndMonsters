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
    public void The_compact_index_is_one_line_per_card_carrying_its_exclusions()
    {
        var index = RuleSelectionSupport.BuildIndex(Catalog);
        var lines = index.Split('\n');

        // One line per card, each a single physical line.
        Assert.Equal(Catalog.AllCards.Count, lines.Length);

        // Each line now carries the card's own boundary, not just its purpose (v0.10 Task 1 control). The
        // text is sourced from the card's exclusion field, so container.take's line names the living-person
        // boundary that a summary alone leaves out.
        var takeLine = Array.Find(lines, l => l.StartsWith("container.take ", StringComparison.Ordinal))!;
        Assert.Contains("NOT:", takeLine, StringComparison.Ordinal);
        Assert.Contains("LIVING", takeLine, StringComparison.Ordinal);

        // Still a genuine reduction against sending the whole book: the index omits every card's full
        // description, preconditions and version header, so even with exclusions present it is smaller than the
        // resolver block set. (The exact selection-token cost is measured in reports/rulebook-efficiency.md.)
        var full = Catalog.AllCards.Sum(c => c.ToResolverBlock().Length);
        Assert.True(index.Length < full,
            $"The index is {index.Length} characters against {full} of full cards — no longer a reduction.");
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

        // And not merely by a hair: the book grows every release, and the margin is what absorbs that. The
        // bar was 1500 while WholeRulebook was the live path; since v0.10 the shipped run uses ActionRouting
        // (appsettings RulebookSelectionMode), which sends only the selected cards — a handful, not all of
        // them — so the WHOLE book is now only the fallback a low-confidence selection drops to, not the
        // request every consultation pays for. v0.11's ability cards (firebolt, stun) spent part of the old
        // margin, and v0.11's demand_surrender card — the 28th action, added because a winner pressing an
        // opponent to yield used to invert into the winner surrendering — spent the rest, taking the fallback
        // below the old 1000 bar. That is the expected cost of growing the action set on an 8k budget, and the
        // bar tracks it down deliberately: ~600 tokens of headroom on a FALLBACK that a live ActionRouting run
        // almost never hits is still ~5x the ~127 that actually truncated replies in the v0.8 run above, and
        // the live selected-card request has several times the room again. If a future card takes it lower,
        // the honest fix is to prune the whole-book fallback or drop it on 8k, not to shave every card thin.
        Assert.True(estimate.HeadroomTokens > 600,
            $"Only {estimate.HeadroomTokens} tokens of headroom — too tight even for the ActionRouting fallback to be safe.");
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
    public async Task A_selection_naming_one_real_rule_among_inventions_keeps_the_real_one_and_records_the_dropped()
    {
        var client = new ScriptedChatClient(
            ScriptedChatClient.Text("""{"ruleIds": ["magic.fireball", "combat.attack"]}"""));

        var selection = await IndexSelector(client).SelectAsync("I hit him", default);

        Assert.False(selection.FellBack);
        Assert.Equal(["combat.attack"], selection.DirectlySelectedRuleIds);

        // The invented id is recorded, not silently discarded (v0.10 Task 3.5): a partially-invented reply
        // must not be indistinguishable from a clean one.
        Assert.Contains("magic.fireball", selection.DroppedLabels);
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
    // Action routing
    // ------------------------------------------------------------------------------------------

    private static ActionRoutingSelector ActionRouter(ScriptedChatClient client, bool cache = false)
    {
        var profile = new AgentModelProfile
        {
            AgentName = "RuleActionRouter",
            Provider = ModelProvider.Ollama,
            ModelId = "scripted-model",
            Temperature = 0.1f
        };
        var trace = new ExperimentTrace("selection-test", new RecordingTraceSink());
        return new ActionRoutingSelector(profile, new TracingChatClient(client, profile, trace), Catalog, cache);
    }

    private static ChatResponse Route(string action, string[] ruleOut, string confidence = "clear")
    {
        var ruleOutJson = string.Join(", ", ruleOut.Select(a => $"\"{a}\""));
        return ScriptedChatClient.Text($$"""{"action": "{{action}}", "ruleOut": [{{ruleOutJson}}], "confidence": "{{confidence}}"}""");
    }

    [Fact]
    public async Task Routing_to_a_known_action_selects_that_actions_cards()
    {
        var selection = await ActionRouter(new ScriptedChatClient(Route("attack_character", [])))
            .SelectAsync("I bring my longsword down on the goblin captain", default);

        Assert.False(selection.FellBack);
        Assert.Equal("attack_character", selection.PrimaryActionLabel);
        Assert.Contains(selection.Cards, c => c.RuleId == "combat.attack");
        Assert.Contains(selection.Cards, c => c.RuleId == RuleCatalog.RejectRuleId);
        Assert.True(selection.Cards.Count < Catalog.AllCards.Count);
        Assert.Equal(1, selection.ModelCalls);
    }

    [Fact]
    public async Task A_ruleOut_pulls_the_ruled_out_actions_cards_the_acceptance_and_theft_pair()
    {
        // The v0.7 case: reaching for the promised tribute reads exactly like a theft, and the theft card is
        // needed to rule it out. Naming steal_item in ruleOut must surface the theft card alongside acceptance.
        var selection = await ActionRouter(new ScriptedChatClient(Route("accept_surrender", ["steal_item"])))
            .SelectAsync("I take the purse out of Skrit's hand and tell him he can live", default);

        Assert.False(selection.FellBack);
        Assert.Contains(selection.Cards, c => c.RuleId == "encounter.accept-surrender");
        Assert.Contains(selection.Cards, c => c.RuleId == "inventory.steal");
    }

    [Fact]
    public async Task DistinguishedFrom_is_traversed_symmetrically()
    {
        // container.take and encounter.accept-surrender both declare they must be told apart from steal_item.
        // Routing to steal_item — declared by neither ON steal — must still surface both, backward along the
        // edge, or the declaration would have to be maintained twice.
        var selection = await ActionRouter(new ScriptedChatClient(Route("steal_item", [])))
            .SelectAsync("I snatch the vial off Vark's belt", default);

        Assert.False(selection.FellBack);
        Assert.Contains(selection.Cards, c => c.RuleId == "inventory.steal");
        Assert.Contains(selection.Cards, c => c.RuleId == "encounter.accept-surrender");
        Assert.Contains(selection.Cards, c => c.RuleId == "container.take");
    }

    [Fact]
    public async Task An_unclear_confidence_falls_back_recorded_as_semantic()
    {
        var selection = await ActionRouter(new ScriptedChatClient(Route("attack_character", [], "unclear")))
            .SelectAsync("I do something the world may not have a word for", default);

        Assert.True(selection.FellBack);
        Assert.Equal(RuleSelectionFallbackKind.Semantic, selection.FallbackKind);
        Assert.Equal(Catalog.AllCards.Count, selection.Cards.Count);
    }

    [Fact]
    public async Task An_unknown_action_label_falls_back_recorded_as_semantic_and_kept()
    {
        var selection = await ActionRouter(new ScriptedChatClient(Route("cast_fireball", [])))
            .SelectAsync("I cast a fireball", default);

        Assert.True(selection.FellBack);
        Assert.Equal(RuleSelectionFallbackKind.Semantic, selection.FallbackKind);
        Assert.Contains("cast_fireball", selection.DroppedLabels);
        // The label the model chose is kept, so the confusion table can show what it wrongly named.
        Assert.Equal("cast_fireball", selection.PrimaryActionLabel);
    }

    [Fact]
    public async Task An_unparseable_routing_reply_falls_back_recorded_as_mechanical()
    {
        var selection = await ActionRouter(new ScriptedChatClient(ScriptedChatClient.Text("probably an attack?")))
            .SelectAsync("I hit him", default);

        Assert.True(selection.FellBack);
        Assert.Equal(RuleSelectionFallbackKind.Mechanical, selection.FallbackKind);
        Assert.Equal(Catalog.AllCards.Count, selection.Cards.Count);
    }

    [Fact]
    public async Task A_thrown_routing_call_falls_back_recorded_as_mechanical()
    {
        // An empty scripted client throws on the first call, standing for a provider error.
        var selection = await ActionRouter(new ScriptedChatClient()).SelectAsync("I hit him", default);

        Assert.True(selection.FellBack);
        Assert.Equal(RuleSelectionFallbackKind.Mechanical, selection.FallbackKind);
        Assert.Contains("failed", selection.FallbackReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_huge_ruleOut_does_not_fan_the_selection_out_to_the_whole_book()
    {
        // The live failure this guards: a model that rules out a dozen actions used to expand — via every
        // ruled-out card's related links — to all 25 cards, silently, not flagged as a fallback.
        string[] manyRuleOut =
        [
            "take_cover", "damage_environmental_object", "intimidate_character", "steady_ally", "use_ability",
            "give_item", "drop_item", "steal_item", "take_item", "open_container", "escape_encounter", "defend"
        ];
        var selection = await ActionRouter(new ScriptedChatClient(Route("attack_character", manyRuleOut)))
            .SelectAsync("I bring my longsword down on the goblin", default);

        Assert.True(selection.Cards.Count < Catalog.AllCards.Count,
            $"A huge ruleOut fanned out to {selection.Cards.Count} of {Catalog.AllCards.Count} cards.");
        Assert.Contains(selection.Cards, c => c.RuleId == "combat.attack");
    }

    [Fact]
    public async Task Related_links_are_followed_only_from_the_routed_action_not_ruled_out_ones()
    {
        // Route to attack, rule out escape. encounter.escape's card is pulled (it was ruled out), but its
        // related link to encounter.open-exit is NOT — only the routed action's cards follow their links.
        var selection = await ActionRouter(new ScriptedChatClient(Route("attack_character", ["escape_encounter"])))
            .SelectAsync("I strike him", default);

        Assert.Contains(selection.Cards, c => c.RuleId == "encounter.escape");
        Assert.DoesNotContain(selection.Cards, c => c.RuleId == "encounter.open-exit");
    }

    [Fact]
    public async Task A_partially_unknown_ruleOut_keeps_the_known_and_records_the_dropped()
    {
        var selection = await ActionRouter(new ScriptedChatClient(Route("attack_character", ["cast_fireball", "combat.parry"])))
            .SelectAsync("I hit him", default);

        Assert.False(selection.FellBack);
        Assert.Contains(selection.Cards, c => c.RuleId == "combat.attack");
        Assert.Contains("cast_fireball", selection.DroppedLabels);
        Assert.Contains("combat.parry", selection.DroppedLabels);
    }

    [Fact]
    public async Task A_routing_cache_hit_makes_no_second_call_and_reports_zero()
    {
        var client = new ScriptedChatClient(Route("attack_character", []));
        var router = ActionRouter(client, cache: true);

        var first = await router.SelectAsync("I hit him", default);
        var second = await router.SelectAsync("I hit him", default);

        Assert.Equal(first.DirectlySelectedRuleIds, second.DirectlySelectedRuleIds);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(0, second.ModelCalls);
        Assert.Null(second.InputTokens);
    }

    // ------------------------------------------------------------------------------------------
    // Action-surface startup validation
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Startup_validation_fails_on_an_engine_action_with_no_governing_card()
    {
        var withoutDefend = Catalog.AllCards
            .Where(c => c.ActionName != DungeonMasterTools.DefendName)
            .ToList();

        var ex = Assert.Throws<InvalidOperationException>(() => ActionSurfaceValidation.Validate(withoutDefend));
        Assert.Contains(DungeonMasterTools.DefendName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("no rule card", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Startup_validation_fails_on_a_DistinguishedFrom_naming_a_nonexistent_action()
    {
        var withBadEdge = Catalog.AllCards
            .Select(c => c.RuleId == "combat.attack" ? c with { DistinguishedFrom = ["fly_away"] } : c)
            .ToList();

        var ex = Assert.Throws<InvalidOperationException>(() => ActionSurfaceValidation.Validate(withBadEdge));
        Assert.Contains("DistinguishedFrom", ex.Message, StringComparison.Ordinal);
        Assert.Contains("fly_away", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_validation_passes_for_the_live_catalog()
    {
        ActionSurfaceValidation.Validate(Catalog.AllCards);
        ActionSurfaceValidation.ValidateIndexFits(ActionRoutingSelector.BuildIndex(Catalog), LocalResolverProfile(), 300);
    }

    [Fact]
    public void The_action_routing_index_fits_the_window_and_is_a_real_reduction()
    {
        var index = ActionRoutingSelector.BuildIndex(Catalog);

        // The real budget: the routing request must fit the resolver's window with room to answer. (The ≤400
        // aspirational target is exceeded — the composed request measures ~1,000 tokens; see
        // reports/rulebook-efficiency.md for the honest reporting of that.)
        ActionSurfaceValidation.ValidateIndexFits(index, LocalResolverProfile(), 300);

        var requestTokens = ContextTruncation.EstimateTokensForCharacters(
            ActionRoutingSelector.SystemPrompt.Length + index.Length + 200 + 200);
        var wholeBookTokens = ContextTruncation.EstimateTokensForCharacters(
            Catalog.AllCards.Sum(c => c.ToResolverBlock().Length));

        // A genuine narrowing: the routing call reads a fraction of what sending the whole book would.
        Assert.True(requestTokens * 2 < wholeBookTokens,
            $"The routing request is ~{requestTokens} tokens against a ~{wholeBookTokens}-token whole book — not a real reduction.");

        // A guard against accidental bloat: the composed request measured ~1,000 tokens.
        Assert.True(requestTokens < 1400, $"The routing request has grown to ~{requestTokens} tokens — investigate.");
    }

    // ------------------------------------------------------------------------------------------
    // Corpus coverage check
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Coverage_fails_on_a_card_no_case_requires()
    {
        // Drop the allow-list entry that saves the background cover-mechanic card: it is now genuinely uncovered.
        var noAllowList = new Dictionary<string, string>();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            RuleSelectionCoverage.CheckCoverage(Catalog, RuleSelectionCorpus.Cases, noAllowList));
        Assert.Contains("environment.cover", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no case requires", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Coverage_fails_on_an_engine_action_no_case_routes_to()
    {
        // Remove the only case that routes to defend; combat.defend stays covered by defend-vs-take-cover, so
        // only the ACTION goes uncovered.
        var withoutDefendCase = RuleSelectionCorpus.Cases.Where(c => c.Id != "defend").ToList();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            RuleSelectionCoverage.CheckCoverage(Catalog, withoutDefendCase, RuleSelectionCorpus.IntentionallyUncoveredRuleIds));
        Assert.Contains("defend", ex.Message, StringComparison.Ordinal);
        Assert.Contains("routes to", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Coverage_passes_for_the_live_corpus_and_catalog()
    {
        var report = RuleSelectionCoverage.Check(Catalog);

        Assert.True(report.HashMatches, $"Corpus labelled against {report.LabelledHash}, catalog is {report.CurrentHash}.");
        Assert.Contains("environment.cover", report.AllowListedCards.Keys);
    }

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
    public void The_corpus_is_still_labelled_against_the_live_catalog()
    {
        // The corpus's required-card and expected-action labels were hand-verified against a specific catalog.
        // When the catalog drifts, those labels must be re-checked and the pin bumped — the same discipline as
        // RulebookMaxCards throwing at startup, so a card can never again be added across releases without the
        // corpus being brought with it. If this fails: re-derive the labels, then set the constant to the value
        // printed below.
        Assert.Equal(RuleSelectionCorpus.LabelledAgainstRulebookVersion, Catalog.RulebookVersion);
    }

    [Fact]
    public void The_corpus_covers_every_family_the_brief_names_and_labels_only_real_rules()
    {
        var categories = RuleSelectionCorpus.Cases.Select(c => c.Category).ToHashSet(StringComparer.Ordinal);

        foreach (var required in new[]
                 {
                     "attack vs ability", "inspect vs open vs take", "give vs drop vs steal",
                     "exit vs escape", "offer vs accept", "threat vs speech", "steady vs speech",
                     "compound", "stale ownership", "unsupported", "cover"
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
