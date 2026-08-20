using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Rulebook;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.8 rulebook cards, and the report sections that render morale, threats and attack quality out of a
/// trace. The report is built from artefacts alone, so these drive it from hand-written trace lines.
/// </summary>
public sealed class MoraleReportTests
{
    private static readonly RuleCatalog Catalog = new();

    private static RuleCard Card(string id) => Catalog.Find(id)!;

    // ------------------------------------------------------------------------------------------
    // The new rule cards
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_two_new_actions_each_have_a_card_bound_to_their_engine_tool()
    {
        Assert.Equal(DungeonMasterTools.IntimidateCharacterName, Card("combat.intimidate").ActionName);
        Assert.Equal(DungeonMasterTools.SteadyAllyName, Card("combat.steady-ally").ActionName);
    }

    [Fact]
    public void The_threat_card_states_that_the_words_carry_no_mechanical_weight()
    {
        var card = Card("combat.intimidate");

        Assert.Contains("never by the wording of the threat", card.RngRequirement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exactly one seeded draw", card.RngRequirement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(card.Exclusions, e => e.Contains("only frightens", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(card.Exclusions, e => e.Contains("twice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_steadying_card_rules_out_self_targeting_and_promises_no_mechanical_edge()
    {
        var card = Card("combat.steady-ally");

        Assert.Equal("none: no dice are rolled at all", card.RngRequirement);
        Assert.Contains(card.Exclusions, e => e.Contains("yourself", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(card.Exclusions, e => e.Contains("odds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_morale_card_says_plainly_that_fear_compels_nothing()
    {
        var card = Card("combat.morale");

        Assert.True(card.IsReference);
        Assert.Contains(card.Exclusions, e =>
            e.Contains("fear chooses for nobody", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(card.Exclusions, e =>
            e.Contains("penalty to hitting or defending", StringComparison.OrdinalIgnoreCase));

        // The gain and recovery rules are stated once, on this card, in structured form.
        Assert.Contains("Rises by one", card.SuccessBehaviour, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Falls by one", card.SuccessBehaviour, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_attack_card_now_describes_one_quality_roll_and_not_two()
    {
        var card = Card("combat.attack");

        Assert.Contains("ONE quality roll", card.RngRequirement, StringComparison.Ordinal);
        Assert.Contains("never a roll per kind", card.RngRequirement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("critical (double)", card.RngRequirement, StringComparison.OrdinalIgnoreCase);

        // The card is kept SHORT on purpose: the resolver used to echo it back, and a long card is a long
        // reply. v0.8's first draft of this field was 255 characters and truncated half the resolver replies
        // in a live run at the output cap. Hydration removed the echo; the brevity is belt and braces.
        Assert.True(card.RngRequirement.Length < 210,
            $"The attack card's RNG line is {card.RngRequirement.Length} characters — it is the most-cited card in the book.");
    }

    [Fact]
    public void The_two_speech_actions_each_lead_with_the_side_they_are_aimed_at()
    {
        // A live probe routed "I catch Elara's eye and tell her to hold the line" to INTIMIDATION: the two
        // cards read alike, because both are "speak to one named person" and neither led with who. Each now
        // opens with the side, and names the other as what it is not. Card authoring, not keywords — but it
        // is the property that made the difference, so it is pinned.
        var intimidate = Card("combat.intimidate");
        var steady = Card("combat.steady-ally");

        Assert.StartsWith("AIMED AT AN ENEMY", intimidate.Description, StringComparison.Ordinal);
        Assert.StartsWith("AIMED AT A COMPANION", steady.Description, StringComparison.Ordinal);
        Assert.Contains("steadying rule", intimidate.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("intimidation rule", steady.Description, StringComparison.OrdinalIgnoreCase);

        // And the summaries the compact index shows carry the same lead, since that index is all a
        // selection strategy ever sees of them.
        Assert.StartsWith("AIMED AT AN ENEMY", intimidate.Summary, StringComparison.Ordinal);
        Assert.StartsWith("AIMED AT A COMPANION", steady.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_attack_card_says_where_a_levelled_weapon_with_no_blow_actually_goes()
    {
        // Excluding something is not enough — the resolver kept "I level my sabre and tell him what is
        // coming" as an attack until the exclusion named its destination, exactly as accept-surrender had
        // to claim the reaching-for-the-tribute phrasing in v0.7.
        var exclusions = string.Join(" ", Card("combat.attack").Exclusions);

        Assert.Contains("is the intimidation rule", exclusions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_combat_family_is_kept_together_and_never_trails_the_book()
    {
        // Position in the prompt is not identity — the rulebook version hashes ids in sorted order — but it
        // is what the resolver reads, and two cards that must be told apart from each other should not be
        // the last thing before the refusal.
        var ids = Catalog.AllCards.Select(c => c.RuleId).ToList();

        Assert.True(ids.IndexOf("combat.intimidate") > ids.IndexOf("combat.defend"));
        Assert.True(ids.IndexOf("combat.steady-ally") < ids.IndexOf("ability.guard-ally"));
        Assert.Equal(RuleCatalog.RejectRuleId, ids[^1]);
    }

    [Fact]
    public void No_card_leaks_a_fear_number_to_a_character_facing_surface()
    {
        // The morale card is allowed to state the scale, because it is a rule. No OTHER card may, because a
        // card's text reaches the guidance the Dungeon Master reads — every field of it, since hydration
        // copies the consequence fields across even though the resolver is not shown them.
        foreach (var card in Catalog.AllCards.Where(c => c.RuleId != "combat.morale"))
        {
            var everything = string.Join(" ",
                card.Summary, card.Description, card.TurnCost, card.RngRequirement, card.Visibility,
                card.SuccessBehaviour, card.FailureBehaviour,
                string.Join(" ", card.RequiredBindings), string.Join(" ", card.Preconditions),
                string.Join(" ", card.Exclusions));

            Assert.DoesNotContain("0 to 5", everything, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------------------------------------
    // The report sections
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Wraps a bare game state the way a real <c>final-state.json</c> does. The fixtures used to pass the
    /// state unwrapped, which is exactly the shape the report's fear lookup wrongly expected — so the two
    /// mistakes agreed with each other and the report shipped listing only the characters whose fear moved.
    /// A fixture that does not match the artefact tests nothing.
    /// </summary>
    private static string Snapshot(string state) =>
        $$"""
        {"RunId":"test-run","CompletedAt":"2026-08-20T00:00:10Z","TerminalCondition":"Heroes win.",
         "RoundsPlayed":3,"TraceEventCount":3,"State":{{state}}}
        """.ReplaceLineEndings(" ");

    private static string RenderReport(IEnumerable<string> traceLines, string finalState)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mm-v08-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllLines(Path.Combine(directory, "trace.jsonl"), traceLines);
            File.WriteAllText(Path.Combine(directory, "final-state.json"), finalState);
            return File.ReadAllText(RunReportWriter.Write(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Line(long sequence, string eventType, string actor, int round, int turn, string data) =>
        $$"""{"Sequence":{{sequence}},"LineNumber":{{sequence}},"Timestamp":"2026-08-20T00:00:0{{sequence % 10}}Z","EventType":"{{eventType}}","Actor":"{{actor}}","Round":{{round}},"Turn":{{turn}},"Data":{{data}}}""";

    [Fact]
    public void Every_character_is_listed_even_when_their_nerve_never_moved()
    {
        // The table takes the union of "had a fear event" and "is in the final state", so a broken final-state
        // read does not empty it — it silently narrows it to whoever happened to break. A live gpt-5.4 run
        // listed two of four characters and read as though the other two had been deliberately excluded.
        var report = RenderReport(
        [
            Line(1, "FearChanged", "Vark", 2, 5, """
                {"CharacterId":"goblin-vark","CharacterName":"Vark","Team":"Goblins","Cause":"BecameOutnumbered",
                 "CauseDetail":"the odds turned","Delta":1,"FearBefore":0,"FearAfter":1,"Absorbed":false,
                 "ScaredTransition":"None","ScaredAfter":false,"RngConsulted":false,"Round":2,"Turn":5,
                 "WorldVersion":6,"PublicRecipients":[]}
                """.Replace("\r\n", " ").Replace("\n", " "))
        ], Snapshot("""
            {"Version":9,"Characters":[
              {"Name":"Rowan","Health":9,"MaxHealth":12,"Fear":0},
              {"Name":"Elara","Health":7,"MaxHealth":10,"Fear":0},
              {"Name":"Vark","Health":3,"MaxHealth":12,"Fear":1},
              {"Name":"Skrit","Health":0,"MaxHealth":6,"Fear":0}]}
            """.Replace("\r\n", " ").Replace("\n", " ")));

        // The one whose fear moved, and the three whose did not — including the dead, who keep the nerve they
        // died with rather than dropping out of the record.
        Assert.Contains("| Vark | 0 | 1 | 1 |", report, StringComparison.Ordinal);
        Assert.Contains("| Rowan | 0 | 0 | 0 | never | n/a |", report, StringComparison.Ordinal);
        Assert.Contains("| Elara | 0 | 0 | 0 | never | n/a |", report, StringComparison.Ordinal);
        Assert.Contains("| Skrit | 0 | 0 | 0 | never | n/a |", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_morale_summary_reports_initial_final_peak_and_the_threshold_crossings()
    {
        var report = RenderReport(
        [
            Line(1, "FearChanged", "Vark", 1, 3, """
                {"CharacterId":"goblin-vark","CharacterName":"Vark","Team":"Goblins","Cause":"CriticalHitReceived",
                 "CauseDetail":"a critical blow from Rowan","Delta":1,"FearBefore":0,"FearAfter":1,"Absorbed":false,
                 "ScaredTransition":"None","ScaredAfter":false,"RngConsulted":false,"Round":1,"Turn":3,"WorldVersion":4,
                 "PublicRecipients":[]}
                """.Replace("\r\n", " ").Replace("\n", " ")),
            Line(2, "FearChanged", "Vark", 2, 7, """
                {"CharacterId":"goblin-vark","CharacterName":"Vark","Team":"Goblins","Cause":"Intimidated",
                 "CauseDetail":"Rowan's open threat told","Delta":1,"FearBefore":2,"FearAfter":3,"Absorbed":false,
                 "ScaredTransition":"BecameScared","ScaredAfter":true,"RngConsulted":true,"Round":2,"Turn":7,
                 "WorldVersion":9,"PublicRecipients":["hero-rowan","goblin-vark"]}
                """.Replace("\r\n", " ").Replace("\n", " ")),
            Line(3, "TurnEnded", "Vark", 3, 11, """
                {"CharacterId":"goblin-vark","CharacterName":"Vark","Result":"ActionResolved",
                 "AcceptedAction":"OfferSurrender(offerer=Vark, recipient=Rowan)"}
                """.Replace("\r\n", " ").Replace("\n", " "))
        ], Snapshot("""{"Version":10,"Characters":[{"Name":"Vark","Health":4,"MaxHealth":12,"Fear":3}]}"""));

        Assert.Contains("## Morale summary", report, StringComparison.Ordinal);
        Assert.Contains("| Vark | 0 | 3 | 3 | round 2, turn 7 | never |", report, StringComparison.Ordinal);

        Assert.Contains("### Fear changes by cause", report, StringComparison.Ordinal);
        Assert.Contains("| CriticalHitReceived | 1 | +1 | 0 |", report, StringComparison.Ordinal);
        Assert.Contains("| Intimidated | 1 | +1 | 0 |", report, StringComparison.Ordinal);

        // The transcript reads a threshold crossing as something anybody would notice, and a change below
        // it as the private bookkeeping it is.
        Assert.Contains("Vark's nerve goes, and everyone present can see it", report, StringComparison.Ordinal);
        Assert.Contains("Vark's nerve: 0 → 1", report, StringComparison.Ordinal);

        // And what was actually chosen under the pressure — the figure the experiment is about.
        Assert.Contains("### Turns taken while scared", report, StringComparison.Ordinal);
        Assert.Contains("OfferSurrender(offerer=Vark, recipient=Rowan)", report, StringComparison.Ordinal);
        Assert.Contains("nothing here shows that fear caused any of it", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_intimidation_summary_shows_the_derivation_and_the_words_that_did_not_affect_it()
    {
        var report = RenderReport(
        [
            Line(1, "IntimidationAttempted", "Rowan", 2, 5, """
                {"AttemptId":"intimidation-1","ActorId":"hero-rowan","ActorName":"Rowan","TargetId":"goblin-vark",
                 "TargetName":"Vark","AssociatedSpeechEventId":4,"AssociatedSpeech":"Your friend bled out in seconds.",
                 "BaseChance":35,"Modifiers":["target-fear +10 from goblin-vark (order 1, retained)"],
                 "ModifierSources":[],"EffectiveChance":45,"Roll":22,"Succeeded":true,"TargetFearBefore":1,
                 "TargetFearAfter":2,"ScaredTransition":"None","Round":2,"Turn":5,"WorldVersionBefore":8,
                 "WorldVersionAfter":9,"PublicRecipients":["hero-rowan","goblin-vark"]}
                """.Replace("\r\n", " ").Replace("\n", " ")),
            Line(2, "AllySteadied", "Skrit", 3, 9, """
                {"ActorId":"goblin-skrit","ActorName":"Skrit","TargetId":"goblin-vark","TargetName":"Vark",
                 "AssociatedSpeechEventId":11,"AssociatedSpeech":"Stand up. They bleed too.","SpeechAddressedToId":"goblin-vark",
                 "TargetFearBefore":3,"TargetFearAfter":2,"NoEffect":false,"ScaredTransition":"RecoveredFromScared",
                 "Round":3,"Turn":9,"WorldVersionBefore":12,"WorldVersionAfter":13,"PublicRecipients":[]}
                """.Replace("\r\n", " ").Replace("\n", " "))
        ], Snapshot("""{"Version":13,"Characters":[{"Name":"Vark","Health":6,"MaxHealth":12,"Fear":2}]}"""));

        Assert.Contains("## Intimidation and reassurance", report, StringComparison.Ordinal);
        Assert.Contains("**1** threat(s) made, **1** of which told", report, StringComparison.Ordinal);
        Assert.Contains("- Base chance: 35", report, StringComparison.Ordinal);
        Assert.Contains("- Effective chance: 45; raw roll: 22", report, StringComparison.Ordinal);
        Assert.Contains("Your friend bled out in seconds.", report, StringComparison.Ordinal);
        Assert.Contains("reached the odds nowhere", report, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("### Allies steadied", report, StringComparison.Ordinal);
        Assert.Contains("steadied, and no longer scared", report, StringComparison.Ordinal);

        // Both read distinctly in the transcript, and neither claims anything beyond what happened.
        Assert.Contains("Rowan threatens Vark openly, and it tells", report, StringComparison.Ordinal);
        Assert.Contains("keeps their weapon, their belongings and their turn", report, StringComparison.Ordinal);
        Assert.Contains("Skrit spends the turn steadying Vark", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_attack_quality_summary_counts_the_bands_and_names_the_critical_hits()
    {
        static string Attack(long sequence, string quality, int damage, bool died, string fearChanges) =>
            Line(sequence, "EngineAction", "Rowan", 1, (int)sequence,
                "{\"ActionType\":\"attack_character\",\"Accepted\":true,\"Outcome\":{"
                + "\"OutcomeType\":\"attack\",\"AttackerName\":\"Rowan\",\"TargetName\":\"Vark\",\"Hit\":true,"
                + $"\"Quality\":\"{quality}\",\"DamageDealt\":{damage},"
                + $"\"TargetDied\":{(died ? "true" : "false")},\"FearChanges\":{fearChanges}" + "}}");

        var report = RenderReport(
        [
            Attack(1, "Glancing", 2, false, "[]"),
            Attack(2, "Solid", 3, false, "[]"),
            Attack(3, "Critical", 6, false,
                """[{"CharacterName":"Vark","Before":0,"After":1},{"CharacterName":"Rowan","Before":2,"After":1}]"""),
            Attack(4, "Critical", 6, true, "[]")
        ], Snapshot("""{"Version":6,"Characters":[{"Name":"Vark","Health":0,"MaxHealth":12,"Fear":1}]}"""));

        Assert.Contains("## Attack quality", report, StringComparison.Ordinal);
        Assert.Contains("| Glancing | 1 | 2 | 2.0 |", report, StringComparison.Ordinal);
        Assert.Contains("| Solid | 1 | 3 | 3.0 |", report, StringComparison.Ordinal);
        Assert.Contains("| Critical | 2 | 12 | 6.0 |", report, StringComparison.Ordinal);

        Assert.Contains("### Critical hits", report, StringComparison.Ordinal);
        Assert.Contains("Vark 0→1; Rowan 2→1", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rulebook_efficiency_section_reports_the_configured_mode_and_the_fallbacks()
    {
        var report = RenderReport(
        [
            Line(1, "RulebookConsultation", "Rowan", 1, 1, """
                {"ConsultationId":"rb-1","ActingCharacterName":"Rowan","RawIntent":"I strike Vark.",
                 "Outcome":"Supported","CacheHit":false,"CardCount":6,"TotalRequestChars":13000,"InputTokens":3000,
                 "CitedRules":["combat.attack@v1-abc"],"SelectionMode":"CompactIndex","SelectionModelCalls":1,
                 "SelectionDirectRuleIds":["combat.attack"],"SelectionExpandedRuleIds":["combat.morale"],
                 "MaxCardsConfigured":32,"MaxInputCharsConfigured":32000,"OutputTokenLimitConfigured":600}
                """.Replace("\r\n", " ").Replace("\n", " ")),
            Line(2, "RulebookConsultation", "Elara", 1, 2, """
                {"ConsultationId":"rb-2","ActingCharacterName":"Elara","RawIntent":"I do something odd.",
                 "Outcome":"Supported","CacheHit":false,"CardCount":21,"TotalRequestChars":36000,"InputTokens":8000,
                 "CitedRules":[],"SelectionMode":"CompactIndex","SelectionModelCalls":1,
                 "SelectionFallback":"the index selection returned nothing parseable, so the whole rulebook was sent",
                 "MaxCardsConfigured":32,"MaxInputCharsConfigured":32000,"OutputTokenLimitConfigured":600}
                """.Replace("\r\n", " ").Replace("\n", " "))
        ], Snapshot("""{"Version":2,"Characters":[]}"""));

        Assert.Contains("## Rulebook efficiency", report, StringComparison.Ordinal);
        Assert.Contains("Configured selection mode(s): **CompactIndex**", report, StringComparison.Ordinal);
        Assert.Contains("Selection fallbacks to the full bounded rulebook: **1**", report, StringComparison.Ordinal);
        Assert.Contains("reports/rulebook-efficiency.md", report, StringComparison.Ordinal);
    }
}
