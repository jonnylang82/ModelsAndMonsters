using ModelsAndMonsters.Rulebook;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// Validation of untrusted resolver guidance, and the abstract-guidance cache. Guidance that cites a rule
/// that does not exist, or the wrong version, or names an action that is not a real engine tool, is filtered
/// or rejected; the cache key is invalidated by any rule-card version change; and cached guidance carries no
/// live encounter bindings.
/// </summary>
public sealed class RulebookGuidanceTests
{
    private static readonly RuleCatalog Catalog = new();
    private static readonly RuleGuidanceValidator Validator = new(Catalog);

    private static RuleGuidance Supported(string action, string ruleId, string version) => new()
    {
        Supported = true,
        CandidateActions = [action],
        CitedRules = [new CitedRule(ruleId, version)]
    };

    [Fact]
    public void Supported_guidance_that_cites_a_real_rule_at_the_right_version_is_valid()
    {
        var steal = Catalog.Find("inventory.steal")!;
        var validation = Validator.Validate(Supported("steal_item", "inventory.steal", steal.Version));

        Assert.True(validation.IsValid);
        Assert.Equal(["steal_item"], validation.Guidance.CandidateActions);
    }

    // ------------------------------------------------------------------------------------------
    // The candidate action and its citation must correspond (v0.8)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Guidance_naming_an_action_no_cited_rule_governs_is_rejected()
    {
        // The exact shape a live probe produced: a real engine tool, and a real card at a real version, but
        // a card whose action is attack_character. Both halves passed independently before v0.8, and the
        // Dungeon Master was handed give_item as its only tool on the strength of it.
        var attack = Catalog.Find("combat.attack")!;

        var validation = Validator.Validate(Supported("give_item", "combat.attack", attack.Version));

        Assert.False(validation.IsValid);
        Assert.Contains("give_item", validation.Reason!, StringComparison.Ordinal);
        Assert.Contains("cited no rule that governs it", validation.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(validation.Guidance.CandidateActions);
    }

    [Fact]
    public void An_unsupported_candidate_is_dropped_while_a_supported_one_survives()
    {
        // Genuinely ambiguous guidance citing two rules, plus one action neither of them governs.
        var attack = Catalog.Find("combat.attack")!;
        var dirty = Catalog.Find("ability.dirty-strike")!;

        var validation = Validator.Validate(new RuleGuidance
        {
            Supported = true,
            CandidateActions = ["attack_character", "give_item", "use_ability"],
            CitedRules = [new CitedRule("combat.attack", attack.Version), new CitedRule("ability.dirty-strike", dirty.Version)]
        });

        Assert.True(validation.IsValid);
        Assert.Equal(["attack_character", "use_ability"], validation.Guidance.CandidateActions);
    }

    [Fact]
    public void A_background_card_alone_supports_no_action_because_it_governs_none()
    {
        // Morale is context every action resolves under, not an action. Citing only it cannot license one.
        var morale = Catalog.Find("combat.morale")!;

        var validation = Validator.Validate(Supported("intimidate_character", "combat.morale", morale.Version));

        Assert.False(validation.IsValid);
        Assert.Contains("cited no rule that governs it", validation.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------------------
    // A citation must be of a card that was actually supplied (v0.8)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_citation_of_a_card_that_was_not_supplied_is_dropped()
    {
        // Under the whole-rulebook path every card is supplied, so this gap was invisible until a selection
        // strategy sent a subset — and then the resolver cited a card it had never been shown.
        var steal = Catalog.Find("inventory.steal")!;
        var supplied = new[] { Catalog.Find("combat.attack")!, Catalog.RejectCard };

        var validation = Validator.Validate(Supported("steal_item", "inventory.steal", steal.Version), supplied);

        Assert.False(validation.IsValid);
        Assert.Contains("was supplied", validation.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(validation.Guidance.CitedRules);
    }

    [Fact]
    public void A_citation_of_a_card_that_was_supplied_still_passes()
    {
        var steal = Catalog.Find("inventory.steal")!;
        var supplied = new[] { Catalog.Find("inventory.steal")!, Catalog.RejectCard };

        var validation = Validator.Validate(Supported("steal_item", "inventory.steal", steal.Version), supplied);

        Assert.True(validation.IsValid);
        Assert.Equal(["steal_item"], validation.Guidance.CandidateActions);
    }

    [Fact]
    public void A_supplied_rule_cited_at_a_typoed_version_is_accepted_and_the_version_repaired()
    {
        // A live failure mode: the resolver names the RIGHT rule but fat-fingers one hex digit of the version
        // hash (measured: inventory.steal cited @…9f for the real …0f). When the rule WAS supplied it was
        // demonstrably shown, so its real version is authoritative — the correct action is not thrown away over
        // a transcription typo, and the stored citation carries the card's real version, not the typo.
        var steal = Catalog.Find("inventory.steal")!;
        var typoed = steal.Version[..^1] + (steal.Version[^1] == '0' ? '1' : '0');
        Assert.NotEqual(steal.Version, typoed);
        var supplied = new[] { steal, Catalog.RejectCard };

        var validation = Validator.Validate(Supported("steal_item", "inventory.steal", typoed), supplied);

        Assert.True(validation.IsValid);
        Assert.Equal(["steal_item"], validation.Guidance.CandidateActions);
        Assert.Equal(steal.Version, Assert.Single(validation.Guidance.CitedRules).Version);
    }

    [Fact]
    public void Not_knowing_what_was_supplied_falls_back_to_the_catalog_check_alone()
    {
        // Null means "not known", not "nothing was sent" — a caller without that information must not have
        // every citation silently dropped underneath it.
        var steal = Catalog.Find("inventory.steal")!;

        Assert.True(Validator.Validate(Supported("steal_item", "inventory.steal", steal.Version), suppliedCards: null).IsValid);
    }

    [Fact]
    public void Guidance_citing_a_rule_that_does_not_exist_is_rejected_as_malformed()
    {
        var validation = Validator.Validate(Supported("steal_item", "inventory.pickpocket", "v1-deadbeef"));

        Assert.False(validation.IsValid);
        Assert.Contains("cited no rule", validation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guidance_citing_a_real_rule_at_the_wrong_version_is_rejected()
    {
        var validation = Validator.Validate(Supported("steal_item", "inventory.steal", "v1-notaversion"));

        Assert.False(validation.IsValid);
    }

    [Fact]
    public void Guidance_naming_an_action_that_is_not_a_real_engine_tool_is_rejected()
    {
        var steal = Catalog.Find("inventory.steal")!;
        // A real citation, but the candidate action is invented.
        var validation = Validator.Validate(new RuleGuidance
        {
            Supported = true,
            CandidateActions = ["teleport_away"],
            CitedRules = [new CitedRule("inventory.steal", steal.Version)]
        });

        Assert.False(validation.IsValid);
        Assert.Contains("no known engine action", validation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unsupported_recommendation_is_valid_and_carries_a_reason()
    {
        var validation = Validator.Validate(new RuleGuidance { Supported = false });

        Assert.True(validation.IsValid);
        Assert.False(validation.Guidance.Supported);
        Assert.False(string.IsNullOrWhiteSpace(validation.Guidance.UnsupportedReason));
    }

    // ------------------------------------------------------------------------------------------
    // Cache
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_cache_returns_a_stored_entry_on_a_matching_key()
    {
        var cache = new RuleGuidanceCache();
        var key = "k1";
        cache.Set(key, Supported("steal_item", "inventory.steal", "v1"));

        Assert.True(cache.TryGet(key, out var hit));
        Assert.True(hit.Supported);
        Assert.False(cache.TryGet("other", out _));
    }

    [Fact]
    public void The_cache_key_changes_when_a_rule_cards_version_changes()
    {
        var original = Catalog.Find("inventory.steal")!;
        var edited = original with { Description = original.Description + " (edited wording)" };

        // Editing the card changes its content version.
        Assert.NotEqual(original.Version, edited.Version);

        var keyBefore = RuleGuidanceCacheKey.Build("I snatch the potion", [original], "Ollama:m", RuleGuidanceSchema.Version);
        var keyAfter = RuleGuidanceCacheKey.Build("I snatch the potion", [edited], "Ollama:m", RuleGuidanceSchema.Version);

        Assert.NotEqual(keyBefore, keyAfter);
    }

    [Fact]
    public void The_cache_key_folds_trivially_different_phrasings_together_but_separates_the_schema_and_model()
    {
        var steal = Catalog.Find("inventory.steal")!;
        var a = RuleGuidanceCacheKey.Build("I snatch the potion.", [steal], "Ollama:m", RuleGuidanceSchema.Version);
        var b = RuleGuidanceCacheKey.Build("  I SNATCH   the potion  ", [steal], "Ollama:m", RuleGuidanceSchema.Version);
        Assert.Equal(a, b); // normalized intent

        var differentModel = RuleGuidanceCacheKey.Build("I snatch the potion.", [steal], "OpenAI:g", RuleGuidanceSchema.Version);
        Assert.NotEqual(a, differentModel);

        var differentSchema = RuleGuidanceCacheKey.Build("I snatch the potion.", [steal], "Ollama:m", "guidance-v99");
        Assert.NotEqual(a, differentSchema);
    }

    [Fact]
    public void Cached_guidance_carries_only_abstract_fields_and_no_live_encounter_bindings()
    {
        // Structural guarantee: RuleGuidance has no property that could hold a character id, health, target
        // availability or an RNG outcome — only abstract rule fields. This pins that shape down.
        var properties = typeof(RuleGuidance).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("CharacterId", properties);
        Assert.DoesNotContain("TargetId", properties);
        Assert.DoesNotContain("Health", properties);
        Assert.DoesNotContain("Roll", properties);
        Assert.Contains("CandidateActions", properties);
        Assert.Contains("CitedRules", properties);
    }
}
