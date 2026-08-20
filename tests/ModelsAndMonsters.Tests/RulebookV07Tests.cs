using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Rulebook;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.7 rulebook: the negotiated-surrender pair, Defend, and one card per ability — all sent whole to the
/// resolver on every call, with no keyword routing deciding which the resolver may consider.
/// </summary>
/// <remarks>
/// Semantic selection is the resolver model's job, so these tests do not try to prove a model picks the right
/// card. What they prove deterministically is everything that makes the right pick possible: every card
/// reaches the resolver, the candidate tool follows the resolver's choice rather than any word in the intent,
/// each card carries the recognition vocabulary a paraphrase would use, defensive posturing is excluded from
/// attack and claimed by defend, and the whole book stays inside a bounded request whose size does not grow
/// with the encounter.
/// </remarks>
public sealed class RulebookV07Tests
{
    private static readonly RuleCatalog Catalog = new();

    private static readonly HarnessOptions Limits = new();

    private static RuleCard Card(string ruleId) =>
        Catalog.Find(ruleId) ?? throw new InvalidOperationException($"No rule card '{ruleId}'.");

    // ------------------------------------------------------------------------------------------
    // The cards exist, are versioned, and cover exactly the engine's action surface
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("encounter.offer-surrender", DungeonMasterTools.OfferSurrenderName)]
    [InlineData("encounter.accept-surrender", DungeonMasterTools.AcceptSurrenderName)]
    [InlineData("combat.defend", DungeonMasterTools.DefendName)]
    [InlineData("ability.guard-ally", DungeonMasterTools.UseAbilityName)]
    [InlineData("ability.healing-prayer", DungeonMasterTools.UseAbilityName)]
    [InlineData("ability.rally-grunt", DungeonMasterTools.UseAbilityName)]
    [InlineData("ability.dirty-strike", DungeonMasterTools.UseAbilityName)]
    public void Each_new_action_has_its_own_versioned_card_bound_to_the_right_tool(string ruleId, string actionName)
    {
        var card = Card(ruleId);

        Assert.Equal(actionName, card.ActionName);
        Assert.StartsWith("v1-", card.Version, StringComparison.Ordinal);
        Assert.NotEmpty(card.RequiredBindings);
        Assert.NotEmpty(card.Preconditions);
        Assert.NotEmpty(card.Exclusions);
        Assert.False(string.IsNullOrWhiteSpace(card.TurnCost));
        Assert.False(string.IsNullOrWhiteSpace(card.RngRequirement));
        Assert.False(string.IsNullOrWhiteSpace(card.Visibility));
    }

    [Fact]
    public void The_unilateral_surrender_card_is_gone_and_replaced_by_the_negotiated_pair()
    {
        Assert.Null(Catalog.Find("encounter.surrender"));
        Assert.NotNull(Catalog.Find("encounter.offer-surrender"));
        Assert.NotNull(Catalog.Find("encounter.accept-surrender"));
        Assert.DoesNotContain(Catalog.AllCards, c => string.Equals(c.ActionName, "surrender", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_ability_in_the_book_has_a_card_and_the_use_ability_ones_name_their_stable_id()
    {
        foreach (var ability in AbilityCatalog.All)
        {
            var card = Card(ability.RuleId);
            Assert.False(string.IsNullOrWhiteSpace(card.Description));

            // Several abilities share the one use_ability tool, so the card's own text is what tells the
            // Dungeon Master which ability id to bind. Defend has its own tool and needs no id.
            if (card.ActionName == DungeonMasterTools.UseAbilityName)
            {
                Assert.Contains(ability.Id, card.Description, StringComparison.Ordinal);
                Assert.Contains(ability.Id, string.Join(" ", card.RequiredBindings), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Every_engine_action_still_has_a_card_so_the_resolver_can_always_cite_a_real_rule()
    {
        foreach (var action in DungeonMasterTools.EngineActionsByName.Keys)
        {
            Assert.Contains(Catalog.AllCards, c => string.Equals(c.ActionName, action, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ------------------------------------------------------------------------------------------
    // Recognition vocabulary: what makes an indirect paraphrase findable at all
    // ------------------------------------------------------------------------------------------

    [Theory]
    // The words a character actually uses, none of which is the action's own name. The card must carry them,
    // because the resolver reads the card — there is no keyword index to fall back on.
    [InlineData("encounter.offer-surrender", "yielding")]
    [InlineData("encounter.offer-surrender", "begging to be spared")]
    [InlineData("encounter.offer-surrender", "buying their life")]
    [InlineData("encounter.accept-surrender", "sparing them")]
    [InlineData("encounter.accept-surrender", "taking the deal")]
    // The physical description of an acceptance — which a live run refused three turns running as a grab.
    [InlineData("encounter.accept-surrender", "physically taking the promised thing")]
    [InlineData("encounter.accept-surrender", "from their hand or belt")]
    [InlineData("encounter.accept-surrender", "not a theft")]
    [InlineData("combat.defend", "bracing")]
    [InlineData("combat.defend", "standing one's ground")]
    [InlineData("combat.defend", "readying to parry")]
    [InlineData("ability.guard-ally", "stepping in front of")]
    [InlineData("ability.guard-ally", "taking the next blow meant for them")]
    [InlineData("ability.healing-prayer", "laying on hands")]
    [InlineData("ability.rally-grunt", "spurring")]
    [InlineData("ability.dirty-strike", "kicking")]
    [InlineData("ability.dirty-strike", "striking below the belt")]
    public void Each_card_carries_the_vocabulary_an_indirect_paraphrase_would_use(string ruleId, string phrase) =>
        Assert.Contains(phrase, Card(ruleId).Description, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Both_adjudicating_prompts_carry_the_lead_with_the_deed_rule()
    {
        // A live run refused "I drive my sabre into Rowan's arm to make him drop the purse" outright — and
        // resolved near-identical intents as plain blows — so the same intent was inconsistently a refusal or
        // a strike depending on wording. Both halves of adjudication now say the same thing: resolve the
        // leading physical deed, and let the hoped-for consequence simply not happen.
        var prompts = PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));

        var resolver = prompts.Get("rulebook.resolver.system").Content;
        Assert.Contains("primary act", resolver, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never split one turn's intent into two actions", resolver, StringComparison.Ordinal);

        // And the same rule is stated for bargains, which is where a live GPT-driven run broke it: two
        // acceptances carrying an extra demand were called unsupported outright.
        Assert.Contains("applies just as hard to bargains", resolver, StringComparison.OrdinalIgnoreCase);

        var adjudicate = prompts.Get("dungeon-master.adjudicate").Content;
        Assert.Contains("primary physical deed", adjudicate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be combined", adjudicate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_items_sharing_a_name_are_still_told_apart()
    {
        // A live run left a character holding two purses whose whole self-state read "Small Purse of Gold
        // Coins, Small Purse of Gold Coins" — indistinguishable, unreferenceable, and duly forgotten. The
        // scenario now qualifies each purse, and the qualified name resolves to exactly one item.
        var plain = new InventoryItem("purse-rowan", "Small Purse of Gold Coins", "A purse.", null, "Rowan's");
        var other = new InventoryItem("purse-skrit", "Small Purse of Gold Coins", "A purse.", null, "the runt's");

        Assert.Equal("Small Purse of Gold Coins (Rowan's)", plain.DisplayName);
        Assert.Equal("Small Purse of Gold Coins (the runt's)", other.DisplayName);

        // An item with no qualifier reads exactly as before — nothing is decorated that did not need it.
        Assert.Equal("Vial of Goblin Salve", new InventoryItem("vial", "Vial of Goblin Salve", "A vial.", 4).DisplayName);

        var holder = TestWorld.Elara() with { Inventory = [plain, other] };

        // The qualified name picks its own item; the bare name still resolves, to the first, as it always did.
        Assert.Equal("purse-skrit", holder.FindItem("Small Purse of Gold Coins (the runt's)")?.Id);
        Assert.Equal("purse-rowan", holder.FindItem("Small Purse of Gold Coins (Rowan's)")?.Id);
        Assert.Equal("purse-rowan", holder.FindItem("Small Purse of Gold Coins")?.Id);
        Assert.Equal("purse-skrit", holder.FindItem("purse-skrit")?.Id);

        // And the character's own state shows two distinguishable lines rather than the same line twice.
        var formatter = new WorldStateFormatter(
            PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates")));
        var self = formatter.FormatCharacterSelfState(holder, TestWorld.State(holder, TestWorld.Vark()));
        Assert.Contains("Small Purse of Gold Coins (Rowan's)", self, StringComparison.Ordinal);
        Assert.Contains("Small Purse of Gold Coins (the runt's)", self, StringComparison.Ordinal);
    }

    [Theory]
    // Every rendered decoration is a reference a model may hand straight back. The first of these is verbatim
    // from a live Claude-driven run: the Dungeon Master copied the healing annotation out of the state block,
    // the engine answered ItemNotPossessed, and a goblin was told the vial at his neck was not there.
    [InlineData("Vial of Goblin Salve (restores 4 health)", "salve")]
    [InlineData("Vial of Goblin Salve", "salve")]
    [InlineData("salve", "salve")]
    [InlineData("Small Purse of Gold Coins (Rowan's)", "purse-rowan")]
    [InlineData("Small Purse of Gold Coins (Rowan's) (restores 4 health)", "purse-rowan")]
    [InlineData("Small Purse of Gold Coins", "purse-rowan")]
    public void An_item_reference_survives_the_annotations_the_state_renders_onto_it(string reference, string expectedId)
    {
        var holder = TestWorld.Elara() with
        {
            Inventory =
            [
                new InventoryItem("purse-rowan", "Small Purse of Gold Coins", "A purse.", null, "Rowan's"),
                new InventoryItem("salve", "Vial of Goblin Salve", "A vial.", 4)
            ]
        };

        Assert.Equal(expectedId, holder.FindItem(reference)?.Id);
    }

    [Fact]
    public void Every_item_name_the_engine_reports_is_the_one_a_character_would_recognise()
    {
        // A GPT-driven run's provenance showed the same purse as "Small Purse of Gold Coins (Elara's)" when
        // stolen and plain "Small Purse of Gold Coins" when handed over as surrender tribute — the qualifier
        // only reached the paths that had been changed by hand. A qualifier that appears in some reports and
        // not others is worse than none: it makes two names for one purse.
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "ModelsAndMonsters", "Engine", "GameEngine.cs"));

        var offenders = source.Split('\n')
            .Select((line, index) => (Line: line.Trim(), Number: index + 1))
            .Where(l => l.Line.Contains("item.Name", StringComparison.Ordinal)
                        || l.Line.Contains("i => i.Name", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            "The engine must report item names as DisplayName so a qualifier is never dropped: " +
            string.Join("; ", offenders.Select(o => $"line {o.Number}: {o.Line}")));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "README.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    [Fact]
    public void Peeling_an_annotation_never_beats_an_exact_match_on_another_item()
    {
        // Two purses, one qualified reference: peeling must not reach past the item that matched exactly.
        var holder = TestWorld.Elara() with
        {
            Inventory =
            [
                new InventoryItem("purse-skrit", "Small Purse of Gold Coins", "A purse.", null, "the runt's"),
                new InventoryItem("purse-rowan", "Small Purse of Gold Coins", "A purse.", null, "Rowan's")
            ]
        };

        Assert.Equal("purse-rowan", holder.FindItem("Small Purse of Gold Coins (Rowan's)")?.Id);
        Assert.Equal("purse-skrit", holder.FindItem("Small Purse of Gold Coins (the runt's)")?.Id);

        // A reference naming no item at all still resolves to nothing rather than to something plausible.
        Assert.Null(holder.FindItem("Vial of Goblin Salve (restores 4 health)"));
        Assert.Null(holder.FindItem("(restores 4 health)"));
    }

    [Fact]
    public void The_shipped_scenario_qualifies_every_duplicated_item_name()
    {
        // The guarantee behind the fix: if a scenario ever ships two items sharing a name and neither is
        // qualified, characters holding both cannot tell them apart, and this test is where that is caught.
        var path = Path.Combine(AppContext.BaseDirectory, "scenario.json");
        Assert.True(File.Exists(path), $"Expected the shipped scenario at {path}.");
        var scenario = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build()
            .GetSection(ScenarioDefinition.SectionName).Get<ScenarioDefinition>();
        Assert.NotNull(scenario);
        var state = ScenarioFactory.CreateInitialState(scenario!);

        var all = state.Characters.SelectMany(c => c.Inventory)
            .Concat(state.Room.Objects.OfType<Container>().SelectMany(c => c.Contents))
            .ToList();

        var colliding = all.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
        foreach (var group in colliding)
        {
            Assert.All(group, i => Assert.False(string.IsNullOrWhiteSpace(i.Qualifier),
                $"'{i.Name}' ({i.Id}) shares its name with another item but carries no qualifier."));
            Assert.Equal(group.Count(), group.Select(i => i.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        // The four purses are exactly that case, so the loop above is doing real work.
        Assert.Equal(4, all.Count(i => i.Name.Contains("Purse", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void The_refusal_prompts_hand_out_no_stock_excuse_to_copy()
    {
        // The rephrase prompt used to offer "the wet stone underfoot" and "their own tired arms" as examples
        // of an in-world refusal, and the model duly used them: across two live runs three separate refusals
        // told characters the stone gave no purchase and their arms were too heavy — including one to a
        // character who braced successfully on that same stone the very next turn. An example in a prompt is
        // a script, not an illustration.
        var prompts = PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));

        foreach (var name in new[] { "dungeon-master.rephrase-rejection", "dungeon-master.rules-adjudicate" })
        {
            var content = prompts.Get(name).Content;
            Assert.DoesNotContain("wet stone underfoot", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tired arms", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("skids off the wet stone", content, StringComparison.OrdinalIgnoreCase);
        }

        // And both now name the honest alternative — that nothing came of it — plus the ban on inventing one.
        var rephrase = prompts.Get("dungeon-master.rephrase-rejection").Content;
        Assert.Contains("nothing came of it", rephrase, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Invent nothing", rephrase, StringComparison.Ordinal);
        Assert.Contains("no object changing hands", rephrase, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_refusal_is_told_to_narrate_nothing_at_all()
    {
        // Verbatim from a live run's reject_action call: "Your blade strikes Rowan's shoulder, but you cannot
        // force him to drop the purse". A refusal changes nothing, so it may not describe a blow landing.
        var reject = DungeonMasterTools.RejectAction;
        var schema = reject.JsonSchema.ToString();

        Assert.Contains("Nothing happened, so narrate nothing", schema, StringComparison.Ordinal);
        Assert.Contains("no impact", schema, StringComparison.OrdinalIgnoreCase);

        // And the tool's own description no longer invites refusal for anything beyond a strike or an item
        // use — that wording predates eleven of the world's fourteen actions.
        Assert.DoesNotContain("not a direct weapon strike", reject.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("last resort", reject.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reaching_for_a_promised_item_is_sent_to_acceptance_not_to_theft()
    {
        // A recipient accepting an offer describes reaching out and taking the thing promised — which reads
        // exactly like a grab. A live run refused that three turns running, so both grab cards now name the
        // case and point at the acceptance rule, and the acceptance card claims it.
        var steal = Card("inventory.steal");
        var take = Card("container.take");
        var accept = Card("encounter.accept-surrender");

        Assert.Contains(steal.Exclusions, e =>
            e.Contains("pending surrender offer", StringComparison.OrdinalIgnoreCase)
            && e.Contains("ACCEPTING", StringComparison.Ordinal));
        Assert.Contains(take.Exclusions, e =>
            e.Contains("out of another character's hand", StringComparison.OrdinalIgnoreCase)
            && e.Contains("acceptance of that offer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("taking it IS the acceptance", accept.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Defensive_posturing_is_excluded_from_attack_and_claimed_by_defend()
    {
        var attack = Card("combat.attack");
        var defend = Card("combat.defend");

        // The attack card sends bracing away explicitly, naming where it belongs.
        Assert.Contains(attack.Exclusions, e => e.Contains("raising a guard, bracing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(attack.Exclusions, e => e.Contains("defend rule", StringComparison.OrdinalIgnoreCase));

        // And the defend card claims it in the same words a character would use.
        Assert.Contains("Defensive posturing is THIS, never an attack", defend.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Shielding_a_companion_is_excluded_from_attack_and_from_defend()
    {
        Assert.Contains(Card("combat.attack").Exclusions,
            e => e.Contains("guard-ally", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Card("combat.defend").Exclusions,
            e => e.Contains("Protecting somebody else", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Negotiation_speech_alone_is_excluded_from_the_offer_card()
    {
        var offer = Card("encounter.offer-surrender");

        Assert.Contains(offer.Exclusions, e => e.Contains("bare plea", StringComparison.OrdinalIgnoreCase));
        // The test is whether anything is HELD BACK. A character carrying items who promises none of them is
        // the misread-demand shape; a character carrying nothing who promises their sword is giving all they
        // have, and must still be able to yield.
        Assert.Contains(offer.Preconditions, p => p.Contains("holds nothing back", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(offer.Exclusions, e => e.Contains("still carries other things", StringComparison.OrdinalIgnoreCase));

        // And demanding better terms is speech, not a counteroffer action: there are no counteroffers in v0.7.
        Assert.Contains(Card("encounter.accept-surrender").Exclusions,
            e => e.Contains("demanding different terms", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_offer_card_states_that_creating_one_changes_nothing()
    {
        var offer = Card("encounter.offer-surrender");

        Assert.Contains("NOTHING moves", offer.SuccessBehaviour, StringComparison.Ordinal);
        Assert.Contains("nobody is disarmed", offer.SuccessBehaviour, StringComparison.Ordinal);
        Assert.Contains("active, targetable combatant", offer.SuccessBehaviour, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ability_cards_state_that_a_refused_use_spends_no_charge()
    {
        foreach (var ruleId in new[] { "ability.healing-prayer", "ability.rally-grunt", "ability.dirty-strike" })
        {
            Assert.Contains("WITHOUT spending the use", Card(ruleId).FailureBehaviour, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_guard_and_dirty_strike_cards_state_that_no_extra_roll_is_made()
    {
        Assert.Contains("NO extra roll", Card("ability.guard-ally").RngRequirement, StringComparison.Ordinal);
        Assert.Contains("ORDINARY attack rolls and no others", Card("ability.dirty-strike").RngRequirement, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // The whole book is sent, bounded, and never trimmed
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_expanded_rulebook_fits_inside_the_configured_hard_limits_with_headroom()
    {
        var totalChars = Catalog.AllCards.Sum(c => c.ToPromptBlock().Length);

        Assert.True(Catalog.AllCards.Count <= Limits.RulebookMaxCards,
            $"The rulebook has {Catalog.AllCards.Count} cards; the configured ceiling is {Limits.RulebookMaxCards}.");
        Assert.True(totalChars <= Limits.RulebookMaxInputChars,
            $"The rulebook's cards total {totalChars} characters; the configured ceiling is {Limits.RulebookMaxInputChars}.");

        // The measured values, so a future release notices when the book approaches its ceiling.
        Assert.InRange(Catalog.AllCards.Count, 15, Limits.RulebookMaxCards);
        Assert.InRange(totalChars, 5000, Limits.RulebookMaxInputChars);
    }

    [Fact]
    public void The_retriever_still_sends_every_card_including_the_new_ones()
    {
        var retriever = new RuleRetriever(Catalog, Limits.RulebookMaxCards, Limits.RulebookMaxInputChars);

        var result = retriever.Retrieve("I put myself between the captain and Elara and take whatever comes.");

        Assert.Equal(Catalog.AllCards.Count, result.SelectedCards.Count);
        Assert.False(result.Trimmed);
        foreach (var ruleId in new[]
                 {
                     "encounter.offer-surrender", "encounter.accept-surrender", "combat.defend",
                     "ability.guard-ally", "ability.healing-prayer", "ability.rally-grunt", "ability.dirty-strike"
                 })
        {
            Assert.Contains(result.SelectedCards, c => c.RuleId == ruleId);
        }
    }

    [Fact]
    public void A_book_that_outgrew_the_ceiling_fails_visibly_rather_than_trimming_the_new_cards()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new RuleRetriever(Catalog, maxCards: Catalog.AllCards.Count - 1, maxInputChars: Limits.RulebookMaxInputChars));

        Assert.Contains("never trims", ex.Message, StringComparison.Ordinal);
        Assert.Contains("raise the ceiling", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // The resolver's choice — not a keyword — decides the tool, and its request stays bounded
    // ------------------------------------------------------------------------------------------

    private static (RulebookConsultant Consultant, ScriptedChatClient Client) Build(params ChatResponse[] responses)
    {
        var client = new ScriptedChatClient(responses);
        var trace = new ExperimentTrace("test-run", new RecordingTraceSink());
        var profile = new ModelsAndMonsters.AI.AgentModelProfile
        {
            AgentName = "RulebookResolver",
            Provider = ModelsAndMonsters.AI.ModelProvider.Ollama,
            ModelId = "scripted-model",
            Temperature = 0.1f,
            ContextWindow = 8192
        };
        var prompts = PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));
        var resolver = new RulebookResolver(profile, new TracingChatClient(client, profile, trace), prompts);
        var retriever = new RuleRetriever(Catalog, Limits.RulebookMaxCards, Limits.RulebookMaxInputChars);

        return (new RulebookConsultant(Catalog, retriever, resolver, new RuleGuidanceValidator(Catalog),
            new RuleGuidanceCache(), trace,
            new RulebookConsultationOptions(Limits.RulebookMaxCards, Limits.RulebookMaxInputChars, 600, CacheEnabled: false)),
            client);
    }

    private static string GuidanceJson(string action, string ruleId) => $$"""
        {
          "supported": true,
          "candidateActions": ["{{action}}"],
          "citedRules": [ { "ruleId": "{{ruleId}}", "version": "{{Card(ruleId).Version}}" } ],
          "requiredBindings": ["the acting character"],
          "preconditions": ["actor is active"],
          "turnCost": "consumes the turn",
          "rngSpecification": "none",
          "visibility": "public",
          "successBehaviour": "it resolves",
          "failureBehaviour": "refused"
        }
        """;

    [Theory]
    [InlineData("encounter.offer-surrender", DungeonMasterTools.OfferSurrenderName)]
    [InlineData("encounter.accept-surrender", DungeonMasterTools.AcceptSurrenderName)]
    [InlineData("combat.defend", DungeonMasterTools.DefendName)]
    [InlineData("ability.guard-ally", DungeonMasterTools.UseAbilityName)]
    public async Task The_resolver_choice_decides_the_candidate_tool_for_the_new_actions(string ruleId, string expectedTool)
    {
        // One identical, deliberately ambiguous intent. Only the resolver's answer differs, so the tool the
        // Dungeon Master is handed can only be following the model, not any word in the intent.
        const string intent = "I do the thing that seems best here.";
        var (consultant, _) = Build(ScriptedChatClient.Text(GuidanceJson(expectedTool, ruleId)));

        var result = await consultant.ConsultAsync("hero-rowan", "Rowan", intent, CancellationToken.None);

        Assert.Equal(RulebookOutcome.Supported, result.Outcome);
        Assert.Equal([expectedTool, DungeonMasterTools.RejectActionName], result.CandidateTools.Select(t => t.Name));
        Assert.Contains(result.Guidance!.CitedRules, c => c.RuleId == ruleId);
    }

    [Fact]
    public async Task A_compound_unsupported_intent_narrows_the_dungeon_master_to_rejection_only()
    {
        var (consultant, _) = Build(ScriptedChatClient.Text("""
            {
              "supported": false,
              "unsupportedReason": "No rule resolves kicking a foe down the stairs and looting them as one act."
            }
            """));

        var result = await consultant.ConsultAsync("goblin-skrit", "Skrit",
            "I kick the human down the stairs and cut his purse free while he falls.", CancellationToken.None);

        Assert.Equal(RulebookOutcome.Unsupported, result.Outcome);
        Assert.False(result.HasSupportedGuidance);
        Assert.Equal([DungeonMasterTools.RejectActionName], result.CandidateTools.Select(t => t.Name));
    }

    [Fact]
    public async Task Guidance_citing_an_ability_rule_at_a_stale_version_is_refused_as_malformed()
    {
        // The version is a content hash, so an edited card invalidates guidance that cited the old text. A
        // confidently wrong resolver cannot smuggle an action past the validator on a stale citation.
        var (consultant, _) = Build(ScriptedChatClient.Text("""
            {
              "supported": true,
              "candidateActions": ["use_ability"],
              "citedRules": [ { "ruleId": "ability.guard-ally", "version": "v1-deadbeef" } ]
            }
            """));

        var result = await consultant.ConsultAsync("hero-rowan", "Rowan", "I shield Elara.", CancellationToken.None);

        Assert.Equal(RulebookOutcome.MalformedGuidance, result.Outcome);
        Assert.Equal([DungeonMasterTools.RejectActionName], result.CandidateTools.Select(t => t.Name));
    }

    [Fact]
    public async Task The_resolver_request_stays_the_same_size_however_long_the_encounter_has_run()
    {
        var (consultant, client) = Build(
            ScriptedChatClient.Text(GuidanceJson(DungeonMasterTools.DefendName, "combat.defend")),
            ScriptedChatClient.Text(GuidanceJson(DungeonMasterTools.DefendName, "combat.defend")),
            ScriptedChatClient.Text(GuidanceJson(DungeonMasterTools.DefendName, "combat.defend")));

        for (var round = 1; round <= 3; round++)
        {
            await consultant.ConsultAsync("hero-rowan", "Rowan", "I keep my guard up.", CancellationToken.None);
        }

        // Three consultations, three requests, each exactly one system message and one user message: the
        // resolver is stateless, so nothing accumulates and round three costs what round one did.
        Assert.Equal(3, client.CallCount);
        Assert.All(client.Requests, messages => Assert.Equal(2, messages.Count));

        var sizes = client.Requests
            .Select(messages => messages.Sum(m => m.Text?.Length ?? 0))
            .Distinct()
            .ToList();
        Assert.Single(sizes);
    }
}
