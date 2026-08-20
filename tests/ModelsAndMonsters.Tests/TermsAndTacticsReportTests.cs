using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The v0.7 report sections — surrender negotiation, persuasion and intimidation, ability activity, the status
/// timeline, state-grounding health and the extended item provenance — rendered from a synthetic run directory,
/// so the report writer is validated deterministically without a live simulation.
/// </summary>
public sealed class TermsAndTacticsReportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mm-v07report-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string Render(string traceLines, string finalState = "{}")
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "trace.jsonl"), traceLines);
        File.WriteAllText(Path.Combine(_directory, "final-state.json"), finalState);
        return File.ReadAllText(RunReportWriter.Write(_directory));
    }

    private static string Line(long seq, int round, int turn, string actor, string eventType, string data) =>
        $"{{\"Sequence\":{seq},\"Timestamp\":\"2026-08-19T00:00:00Z\",\"RunId\":\"r\",\"Round\":{round},\"Turn\":{turn}," +
        $"\"Actor\":\"{actor}\",\"EventType\":\"{eventType}\",\"Data\":{data}}}\n";

    private const string OfferMade =
        "{\"OfferId\":\"offer-1\",\"OffererId\":\"goblin-vark\",\"OffererName\":\"Vark\",\"RecipientId\":\"hero-rowan\"," +
        "\"RecipientName\":\"Rowan\",\"OfferedItemIds\":[\"purse-vark\"],\"OfferedItemNames\":[\"Small Purse of Gold Coins\"]," +
        "\"ForfeitWeapon\":true,\"WeaponName\":\"Notched Sabre\",\"AssociatedSpeechEventId\":7," +
        "\"AssociatedSpeech\":\"Vark says:\\n\\\"Take the gold, human, and let me crawl out.\\\"\"," +
        "\"Round\":2,\"Turn\":7,\"BattleStateSummary\":\"Heroes: Rowan (unhurt), Elara (wounded) | Goblins: Vark (badly wounded), Skrit (dead)\"," +
        "\"NothingTransferred\":true,\"OffererRemainsTargetable\":true,\"PublicRecipients\":[\"hero-rowan\"]}";

    // ------------------------------------------------------------------------------------------
    // Surrender negotiation
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_negotiation_section_reports_every_offer_its_terms_and_how_it_was_answered()
    {
        var trace =
            Line(1, 2, 7, "Vark", "SurrenderOfferMade", OfferMade) +
            Line(2, 2, 9, "harness", "SurrenderOfferResolved",
                "{\"OfferId\":\"offer-1\",\"OffererId\":\"goblin-vark\",\"OffererName\":\"Vark\",\"RecipientId\":\"hero-rowan\"," +
                "\"RecipientName\":\"Rowan\",\"PreviousState\":\"Pending\",\"NewState\":\"Accepted\",\"Cause\":\"accepted by Rowan\"," +
                "\"CreatedRound\":2,\"CreatedTurn\":7,\"ResolvedRound\":2,\"ResolvedTurn\":9,\"TurnsToRespond\":2,\"AssetsTransferred\":true}") +
            Line(3, 2, 9, "Rowan", "SurrenderAgreementRecorded",
                "{\"AgreementId\":\"agreement-1\",\"OfferId\":\"offer-1\",\"OffererId\":\"goblin-vark\",\"OffererName\":\"Vark\"," +
                "\"AcceptedById\":\"hero-rowan\",\"AcceptedByName\":\"Rowan\",\"TransferredItemIds\":[\"purse-vark\"]," +
                "\"TransferredItemNames\":[\"Small Purse of Gold Coins\"],\"ForfeitedWeaponId\":\"weapon-notched-sabre\"," +
                "\"ForfeitedWeaponName\":\"Notched Sabre\",\"WeaponDisposition\":\"laid on the floor, where it can be taken as a trophy\"," +
                "\"OffererDisarmed\":true,\"AcceptedRound\":2,\"AcceptedTurn\":9,\"AssociatedSpeechEventId\":7}");

        var report = Render(trace);

        Assert.Contains("## Surrender negotiation", report, StringComparison.Ordinal);
        Assert.Contains("| offer-1 | 2.7 | Vark | Rowan |", report, StringComparison.Ordinal);
        Assert.Contains("Small Purse of Gold Coins and their Notched Sabre", report, StringComparison.Ordinal);
        Assert.Contains("| accepted | 2 | yes |", report, StringComparison.Ordinal);

        // The agreement table names what actually moved and that the offerer was disarmed.
        Assert.Contains("**Surrender agreements struck**", report, StringComparison.Ordinal);
        Assert.Contains("| agreement-1 | 2.9 | Vark | Rowan |", report, StringComparison.Ordinal);
        Assert.Contains("laid on the floor", report, StringComparison.Ordinal);
    }

    [Fact]
    public void An_offer_nobody_accepted_reports_that_nothing_changed_hands()
    {
        var trace =
            Line(1, 2, 7, "Vark", "SurrenderOfferMade", OfferMade) +
            Line(2, 3, 11, "harness", "SurrenderOfferResolved",
                "{\"OfferId\":\"offer-1\",\"OffererId\":\"goblin-vark\",\"OffererName\":\"Vark\",\"RecipientId\":\"hero-rowan\"," +
                "\"RecipientName\":\"Rowan\",\"PreviousState\":\"Pending\",\"NewState\":\"Expired\"," +
                "\"Cause\":\"the named recipient completed a turn without accepting\",\"CreatedRound\":2,\"CreatedTurn\":7," +
                "\"ResolvedRound\":3,\"ResolvedTurn\":11,\"TurnsToRespond\":4,\"AssetsTransferred\":false}");

        var report = Render(trace);

        Assert.Contains("| expired | 4 | no |", report, StringComparison.Ordinal);
        Assert.Contains("**No offer was accepted: nothing changed hands through negotiation.**", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_persuasion_section_is_descriptive_and_claims_no_causation()
    {
        var trace = Line(1, 2, 7, "Vark", "SurrenderOfferMade", OfferMade);

        var report = Render(trace);

        Assert.Contains("## Persuasion and intimidation", report, StringComparison.Ordinal);
        Assert.Contains("**nothing here shows that speech caused an acceptance**", report, StringComparison.Ordinal);

        // The speech, the assets and the odds it was made under — the descriptive record the release asks for.
        Assert.Contains("Take the gold, human, and let me crawl out.", report, StringComparison.Ordinal);
        Assert.Contains("Visible battle state: Heroes: Rowan (unhurt)", report, StringComparison.Ordinal);
        Assert.Contains("not accepted", report, StringComparison.Ordinal);

        // And no statistic is offered beyond the two raw counts.
        Assert.DoesNotContain("significan", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Offers made: **1**; accepted: **0**.", report, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // Ability activity and the status timeline
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_ability_section_reports_attempts_outcomes_and_charges_including_refusals()
    {
        var trace =
            Line(1, 1, 1, "Rowan", "AbilityUsed",
                "{\"ActorId\":\"hero-rowan\",\"ActorName\":\"Rowan\",\"AbilityId\":\"guard-ally\",\"AbilityName\":\"Guard Ally\"," +
                "\"Category\":\"Technique\",\"TargetId\":\"hero-elara\",\"TargetName\":\"Elara\",\"ValidationResult\":\"accepted\"," +
                "\"StatusesApplied\":[\"Guarding\",\"Guarded\"],\"RngConsulted\":false,\"WorldVersionBefore\":0," +
                "\"WorldVersionAfter\":1,\"TurnConsumed\":true}") +
            Line(2, 1, 2, "Elara", "AbilityUsed",
                "{\"ActorId\":\"hero-elara\",\"ActorName\":\"Elara\",\"AbilityId\":\"healing-prayer\",\"AbilityName\":\"Healing Prayer\"," +
                "\"Category\":\"Spell\",\"TargetId\":\"hero-elara\",\"TargetName\":\"Elara\",\"ValidationResult\":\"accepted\"," +
                "\"RemainingUsesBefore\":1,\"RemainingUsesAfter\":0,\"HealingPerformed\":4,\"StatusesApplied\":[]," +
                "\"RngConsulted\":false,\"WorldVersionBefore\":1,\"WorldVersionAfter\":2,\"TurnConsumed\":true}") +
            Line(3, 1, 3, "Vark", "AbilityUsed",
                "{\"ActorId\":\"goblin-vark\",\"ActorName\":\"Vark\",\"AbilityId\":\"rally-grunt\",\"AbilityName\":\"Rally Grunt\"," +
                "\"Category\":\"Command\",\"TargetId\":\"goblin-vark\",\"TargetName\":\"Vark\",\"ValidationResult\":\"rejected\"," +
                "\"RejectionReason\":\"AbilityTargetIsSelf\",\"RemainingUsesBefore\":1,\"RemainingUsesAfter\":1," +
                "\"StatusesApplied\":[],\"RngConsulted\":false,\"WorldVersionBefore\":2,\"WorldVersionAfter\":2,\"TurnConsumed\":false}") +
            Line(4, 1, 4, "Skrit", "AbilityUsed",
                "{\"ActorId\":\"goblin-skrit\",\"ActorName\":\"Skrit\",\"AbilityId\":\"dirty-strike\",\"AbilityName\":\"Dirty Strike\"," +
                "\"Category\":\"Trick\",\"TargetId\":\"hero-rowan\",\"TargetName\":\"Rowan\",\"ValidationResult\":\"accepted\"," +
                "\"RemainingUsesBefore\":1,\"RemainingUsesAfter\":0,\"StatusesApplied\":[\"OffBalance\"],\"RngConsulted\":true," +
                "\"WorldVersionBefore\":2,\"WorldVersionAfter\":3,\"TurnConsumed\":true}") +
            Line(5, 1, 4, "Skrit", "StatusApplied",
                "{\"StatusId\":\"status-3\",\"Kind\":\"OffBalance\",\"Transition\":\"Applied\",\"Cause\":\"left off balance\"," +
                "\"SourceCharacterId\":\"goblin-skrit\",\"SourceCharacterName\":\"Skrit\",\"TargetCharacterId\":\"hero-rowan\"," +
                "\"TargetCharacterName\":\"Rowan\",\"AppliedRound\":1,\"AppliedTurn\":4,\"Modifier\":-15," +
                "\"ExpiryRule\":\"EndOfTargetNextTurn\",\"Visibility\":\"Public\",\"Round\":1,\"Turn\":4,\"WorldVersion\":3}") +
            Line(6, 2, 6, "Vark", "AttackRedirected",
                "{\"AttackerId\":\"goblin-vark\",\"AttackerName\":\"Vark\",\"IntendedTargetId\":\"hero-elara\"," +
                "\"IntendedTargetName\":\"Elara\",\"AuthoritativeTargetId\":\"hero-rowan\",\"AuthoritativeTargetName\":\"Rowan\"," +
                "\"TargetArmourUsed\":2,\"TargetHealthBefore\":14,\"TargetHealthAfter\":12,\"RngDrawCount\":2,\"Round\":2,\"Turn\":6}") +
            Line(7, 2, 7, "Rowan", "AbilityUsed",
                "{\"ActorId\":\"hero-rowan\",\"ActorName\":\"Rowan\",\"AbilityId\":\"defend\",\"AbilityName\":\"Defend\"," +
                "\"Category\":\"BasicAction\",\"ValidationResult\":\"accepted\",\"StatusesApplied\":[\"Defending\"]," +
                "\"RngConsulted\":false,\"WorldVersionBefore\":3,\"WorldVersionAfter\":4,\"TurnConsumed\":true}") +
            Line(8, 2, 8, "Skrit", "EngineAction",
                "{\"ActionType\":\"attack_character\",\"Accepted\":true,\"Outcome\":{\"DefendReduction\":1}}");

        var finalState =
            "{\"State\":{\"Characters\":[{\"Name\":\"Elara\",\"Abilities\":[{\"Name\":\"Healing Prayer\",\"Category\":\"Spell\"," +
            "\"MaxUses\":1,\"RemainingUses\":0},{\"Name\":\"Defend\",\"Category\":\"BasicAction\"}]}],\"Room\":{\"Objects\":[]}}}";

        var report = Render(trace, finalState);

        Assert.Contains("## Ability activity", report, StringComparison.Ordinal);
        Assert.Contains("Ability uses attempted: **5** — 4 accepted, 1 refused.", report, StringComparison.Ordinal);
        Assert.Contains("Healing performed: **4** health restored", report, StringComparison.Ordinal);
        Assert.Contains("Guard redirections: **1**", report, StringComparison.Ordinal);
        Assert.Contains("Dirty Strike / OffBalance applications: **1**", report, StringComparison.Ordinal);
        Assert.Contains("Defend uses: **1**; damage turned aside by a raised guard: **1**", report, StringComparison.Ordinal);

        // A refused use is visible as having spent nothing.
        Assert.Contains("refused: AbilityTargetIsSelf (no charge spent)", report, StringComparison.Ordinal);

        // The final charge table shows what was left, with unlimited abilities named as such.
        Assert.Contains("**Ability charges at the end of the run**", report, StringComparison.Ordinal);
        Assert.Contains("| Elara | Healing Prayer | Spell | 0 of 1 |", report, StringComparison.Ordinal);
        Assert.Contains("| Elara | Defend | BasicAction | unlimited |", report, StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_timeline_reports_application_duration_consumption_and_the_draw_it_fed()
    {
        var trace =
            Line(1, 1, 3, "Vark", "StatusApplied",
                "{\"StatusId\":\"status-1\",\"Kind\":\"Rallied\",\"Transition\":\"Applied\",\"Cause\":\"Vark rallied Skrit\"," +
                "\"SourceCharacterId\":\"goblin-vark\",\"SourceCharacterName\":\"Vark\",\"TargetCharacterId\":\"goblin-skrit\"," +
                "\"TargetCharacterName\":\"Skrit\",\"AppliedRound\":1,\"AppliedTurn\":3,\"Modifier\":15," +
                "\"ExpiryRule\":\"EndOfTargetNextTurn\",\"Visibility\":\"Public\",\"Round\":1,\"Turn\":3,\"WorldVersion\":1}") +
            Line(2, 1, 4, "Skrit", "StatusConsumed",
                "{\"StatusId\":\"status-1\",\"Kind\":\"Rallied\",\"Transition\":\"Consumed\"," +
                "\"Cause\":\"folded into Skrit's attack hit chance\",\"SourceCharacterId\":\"goblin-vark\"," +
                "\"SourceCharacterName\":\"Vark\",\"TargetCharacterId\":\"goblin-skrit\",\"TargetCharacterName\":\"Skrit\"," +
                "\"AppliedRound\":1,\"AppliedTurn\":3,\"Modifier\":15,\"ExpiryRule\":\"EndOfTargetNextTurn\"," +
                "\"Visibility\":\"Public\",\"Round\":1,\"Turn\":4,\"WorldVersion\":2,\"AffectedRngPurpose\":\"attack.hit-check\"}") +
            Line(3, 2, 5, "Rowan", "StatusExpired",
                "{\"StatusId\":\"status-2\",\"Kind\":\"Defending\",\"Transition\":\"Expired\"," +
                "\"Cause\":\"reached its expiry at the start of the holder's next turn\",\"SourceCharacterId\":\"hero-rowan\"," +
                "\"SourceCharacterName\":\"Rowan\",\"TargetCharacterId\":\"hero-rowan\",\"TargetCharacterName\":\"Rowan\"," +
                "\"AppliedRound\":1,\"AppliedTurn\":1,\"Modifier\":-1,\"ExpiryRule\":\"StartOfTargetNextTurn\"," +
                "\"Visibility\":\"Public\",\"Round\":2,\"Turn\":5,\"WorldVersion\":3}");

        var report = Render(trace);

        Assert.Contains("## Status timeline", report, StringComparison.Ordinal);
        Assert.Contains("| 1.3 | Rallied | Applied | Vark | Skrit | +15 |", report, StringComparison.Ordinal);
        // One turn held, and the draw it fed named explicitly.
        Assert.Contains("| 1.4 | Rallied | Consumed | Vark | Skrit | +15 | 1 | EndOfTargetNextTurn | attack.hit-check |", report, StringComparison.Ordinal);
        Assert.Contains("| Rallied | 1 | 1 | 0 | 0 |", report, StringComparison.Ordinal);
        Assert.Contains("| Defending | 0 | 0 | 1 | 0 |", report, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // State-grounding health
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_grounding_section_reports_projections_withheld_facts_and_discarded_output()
    {
        var trace =
            Line(1, 1, 1, "Rowan", "CharacterQuestion",
                "{\"CharacterId\":\"hero-rowan\",\"CharacterName\":\"Rowan\",\"Question\":\"What is in the case?\",\"QuestionNumberThisTurn\":1}") +
            Line(2, 1, 1, "DungeonMaster", "AnswerFactsProjected",
                "{\"CharacterId\":\"hero-rowan\",\"CharacterName\":\"Rowan\",\"Question\":\"What is in the case?\"," +
                "\"ProjectedFacts\":\"...\",\"OmittedHiddenFacts\":\"- the contents of the case\",\"ProjectedFactCount\":30," +
                "\"OmittedFactCount\":3,\"AffordanceCount\":12,\"WorldVersion\":0,\"FullStateWithheld\":true}") +
            Line(3, 1, 1, "DungeonMaster", "AdjudicationCorrected",
                "{\"ToolName\":\"ask_dm\",\"Parameter\":\"answer\",\"DungeonMasterValue\":\"You directly know the case is shut.\"," +
                "\"CorrectedValue\":\"The lid is down, and you have never had it up.\",\"Justification\":\"broke character\"}") +
            Line(4, 1, 2, "Elara", "EngineAction",
                "{\"ActionType\":\"take_item\",\"Accepted\":false,\"RejectionReason\":\"ItemNotInContainer\"}") +
            Line(5, 1, 3, "Skrit", "InventoryInteraction",
                "{\"ActionType\":\"steal_item\",\"ActorName\":\"Skrit\",\"ValidationResult\":\"rejected\"," +
                "\"RejectionReason\":\"NoInformationalBasis\",\"RngConsulted\":false}") +
            Line(6, 1, 4, "Skrit", "PostResolutionOutputDiscarded",
                "{\"CharacterId\":\"goblin-skrit\",\"CharacterName\":\"Skrit\",\"ResolvedAction\":\"AttackCharacter(...)\"," +
                "\"DiscardedKind\":\"tool-call\",\"ToolName\":\"take_action\",\"DiscardedContent\":\"And I cut her purse free.\"," +
                "\"StateUnchanged\":true,\"Round\":1,\"Turn\":4}") +
            Line(7, 1, 5, "harness", "HarnessLimitReached",
                "{\"Limit\":\"MaxActionAttemptsPerTurn\",\"Value\":3,\"Effect\":\"turn abandoned\"}");

        var report = Render(trace);

        Assert.Contains("## State-grounding health", report, StringComparison.Ordinal);
        Assert.Contains("Character questions asked: **1**; answered from a bounded projection: **1**.", report, StringComparison.Ordinal);
        Assert.Contains("facts deliberately withheld: 3.0", report, StringComparison.Ordinal);
        Assert.Contains("Authoritative state withheld from the answering call: **1 of 1**.", report, StringComparison.Ordinal);
        Assert.Contains("Answers the harness had to rephrase", report, StringComparison.Ordinal);
        Assert.Contains("ItemNotInContainer ×1", report, StringComparison.Ordinal);
        Assert.Contains("Reaches refused for want of an informational basis (before the engine, before any roll): **1**.", report, StringComparison.Ordinal);
        Assert.Contains("Post-resolution output discarded: **1** — 1 further tool call(s)", report, StringComparison.Ordinal);
        Assert.Contains("Turns abandoned on an action or model-call limit: **1**.", report, StringComparison.Ordinal);

        // The discarded content is reproduced verbatim, with what had already resolved the turn.
        Assert.Contains("And I cut her purse free.", report, StringComparison.Ordinal);
        Assert.Contains("AttackCharacter(...)", report, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // Item provenance distinguishes surrender tribute and weapon forfeiture
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Provenance_distinguishes_giving_dropping_theft_looting_tribute_and_forfeiture_with_stable_ids()
    {
        var trace =
            Line(1, 1, 1, "Rowan", "InventoryInteraction",
                "{\"ActionType\":\"give_item\",\"ActorName\":\"Rowan\",\"ValidationResult\":\"accepted\"}") +
            Provenance(2, "Flask of Strong Wine", "flask-strong-wine", "give_item", "hero-rowan", "hero-elara", rng: false) +
            Provenance(3, "Bundle of Damp Rags", "bundle-of-damp-rags", "drop_item", "goblin-vark", "room-floor", rng: false) +
            Provenance(4, "Small Purse of Gold Coins", "purse-elara", "steal_item", "hero-elara", "goblin-skrit", rng: true) +
            Provenance(5, "Small Purse of Gold Coins", "purse-skrit", "take_item", "corpse-goblin-skrit", "hero-rowan", rng: false) +
            Provenance(6, "Small Purse of Gold Coins", "purse-vark", "surrender_tribute", "goblin-vark", "hero-rowan", rng: false) +
            Provenance(7, "Notched Sabre", "weapon-notched-sabre", "weapon_forfeiture", "goblin-vark", "room-floor", rng: false);

        var report = Render(trace);

        // Each kind is named as itself; tribute is not "given" and a forfeited weapon is not "dropped".
        Assert.Contains("| given |", report, StringComparison.Ordinal);
        Assert.Contains("| dropped |", report, StringComparison.Ordinal);
        Assert.Contains("| stolen |", report, StringComparison.Ordinal);
        Assert.Contains("| taken (container, body or floor) |", report, StringComparison.Ordinal);
        Assert.Contains("| surrender tribute |", report, StringComparison.Ordinal);
        Assert.Contains("| weapon forfeiture |", report, StringComparison.Ordinal);

        // Three identically named purses are told apart only by their stable ids, which is why the column exists.
        Assert.Contains("`purse-elara`", report, StringComparison.Ordinal);
        Assert.Contains("`purse-skrit`", report, StringComparison.Ordinal);
        Assert.Contains("`purse-vark`", report, StringComparison.Ordinal);
    }

    private static string Provenance(long seq, string itemName, string itemId, string actionType, string from, string to, bool rng) =>
        Line(seq, 1, (int)seq, "harness", "ItemProvenance",
            $"{{\"ItemId\":\"{itemId}\",\"ItemName\":\"{itemName}\",\"ActionType\":\"{actionType}\"," +
            $"\"PreviousOwnerOrLocation\":\"{from}\",\"NewOwnerOrLocation\":\"{to}\",\"RngInvolved\":{(rng ? "true" : "false")}}}");

    // ------------------------------------------------------------------------------------------
    // Final state
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_final_state_section_shows_statuses_the_offer_ledger_and_the_agreements()
    {
        var finalState =
            "{\"State\":{\"Room\":{\"Name\":\"Flooded Cellar\",\"Objects\":[]},\"Characters\":[" +
            "{\"Name\":\"Vark\",\"Team\":\"Goblins\",\"Health\":3,\"MaxHealth\":12,\"Disposition\":\"Surrendered\"," +
            "\"Inventory\":[],\"Injuries\":[],\"Abilities\":[]}]," +
            "\"Statuses\":[{\"Id\":\"status-4\",\"Kind\":\"Defending\",\"SourceCharacterId\":\"hero-rowan\"," +
            "\"TargetCharacterId\":\"hero-rowan\",\"Modifier\":-1,\"ExpiryRule\":\"StartOfTargetNextTurn\"}]," +
            "\"SurrenderOffers\":[{\"Id\":\"offer-1\",\"OffererId\":\"goblin-vark\",\"RecipientId\":\"hero-rowan\"," +
            "\"OfferedItemIds\":[\"purse-vark\"],\"ForfeitWeapon\":true,\"State\":\"Accepted\",\"ResolutionCause\":\"accepted by Rowan\"}]," +
            "\"SurrenderAgreements\":[{\"Id\":\"agreement-1\",\"OfferId\":\"offer-1\",\"OffererId\":\"goblin-vark\"," +
            "\"AcceptedById\":\"hero-rowan\",\"TransferredItemIds\":[\"purse-vark\"],\"ForfeitedWeaponId\":\"weapon-notched-sabre\"," +
            "\"AcceptedRound\":2,\"AcceptedTurn\":9}]}}";

        var report = Render(Line(1, 1, 1, "harness", "RoundStarted", "{\"Round\":1}"), finalState);

        Assert.Contains("**Status effects still standing**", report, StringComparison.Ordinal);
        Assert.Contains("| Defending | hero-rowan | hero-rowan | -1 | StartOfTargetNextTurn |", report, StringComparison.Ordinal);
        Assert.Contains("**Surrender offers (the complete ledger)**", report, StringComparison.Ordinal);
        Assert.Contains("| offer-1 | goblin-vark | hero-rowan | purse-vark | yes | Accepted |", report, StringComparison.Ordinal);
        Assert.Contains("**Surrender agreements (durable evidence)**", report, StringComparison.Ordinal);
        Assert.Contains("| agreement-1 | offer-1 | goblin-vark | hero-rowan | purse-vark | weapon-notched-sabre | 2.9 |", report, StringComparison.Ordinal);

        // A surrendered character with no weapon is named as disarmed, not as someone who never carried one.
        Assert.Contains("none — disarmed on surrender", report, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // Transcript rendering
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_transcript_shows_a_discarded_tool_call_but_not_a_discarded_stretch_of_prose()
    {
        // A verbose model writes its thinking as text alongside a perfectly good tool call, on nearly every
        // turn. Both are discarded and both are traced, but only the surplus call — a model trying to act
        // twice — belongs in the readable story.
        var trace =
            Line(1, 1, 1, "harness", "RoundStarted", "{\"Round\":1}") +
            Line(2, 1, 4, "Skrit", "PostResolutionOutputDiscarded",
                "{\"CharacterId\":\"goblin-skrit\",\"CharacterName\":\"Skrit\",\"ResolvedAction\":\"AttackCharacter(...)\"," +
                "\"DiscardedKind\":\"tool-call\",\"ToolName\":\"take_action\",\"DiscardedContent\":\"And I cut her purse free.\"," +
                "\"StateUnchanged\":true,\"Round\":1,\"Turn\":4}") +
            Line(3, 1, 4, "Skrit", "PostResolutionOutputDiscarded",
                "{\"CharacterId\":\"goblin-skrit\",\"CharacterName\":\"Skrit\",\"ResolvedAction\":\"AttackCharacter(...)\"," +
                "\"DiscardedKind\":\"trailing-text\",\"DiscardedContent\":\"I weigh my chances against the big one.\"," +
                "\"StateUnchanged\":true,\"Round\":1,\"Turn\":4}");

        var report = Render(trace);
        var transcriptStart = report.IndexOf("## Transcript", StringComparison.Ordinal);
        var transcriptEnd = report.IndexOf("## Trace", transcriptStart, StringComparison.Ordinal);
        var transcript = report[transcriptStart..transcriptEnd];

        Assert.Contains("And I cut her purse free.", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("I weigh my chances against the big one.", transcript, StringComparison.Ordinal);

        // Both are still counted and both are still in the state-grounding table, so nothing is hidden.
        var grounding = report[report.IndexOf("## State-grounding health", StringComparison.Ordinal)..];
        Assert.Contains("Post-resolution output discarded: **2** — 1 further tool call(s)", grounding, StringComparison.Ordinal);
        Assert.Contains("I weigh my chances against the big one.", grounding, StringComparison.Ordinal);
    }

    [Fact]
    public void The_transcript_reads_an_offer_and_an_acceptance_as_different_events()
    {
        var trace =
            Line(1, 1, 1, "harness", "RoundStarted", "{\"Round\":1}") +
            Line(2, 2, 7, "Vark", "SurrenderOfferMade", OfferMade) +
            Line(3, 2, 9, "Rowan", "SurrenderAgreementRecorded",
                "{\"AgreementId\":\"agreement-1\",\"OfferId\":\"offer-1\",\"OffererId\":\"goblin-vark\",\"OffererName\":\"Vark\"," +
                "\"AcceptedById\":\"hero-rowan\",\"AcceptedByName\":\"Rowan\",\"TransferredItemIds\":[\"purse-vark\"]," +
                "\"TransferredItemNames\":[\"Small Purse of Gold Coins\"],\"ForfeitedWeaponName\":\"Notched Sabre\"," +
                "\"WeaponDisposition\":\"laid on the floor\",\"OffererDisarmed\":true,\"AcceptedRound\":2,\"AcceptedTurn\":9}");

        var report = Render(trace);

        var transcript = report[report.IndexOf("## Transcript", StringComparison.Ordinal)..];

        // The offer says plainly that nothing has happened yet.
        Assert.Contains("Nothing has changed hands; Vark is still armed and still a target", transcript, StringComparison.Ordinal);
        // The acceptance says what actually moved.
        Assert.Contains("Rowan accepts Vark's surrender", transcript, StringComparison.Ordinal);
        Assert.Contains("Vark is disarmed and out of the fight", transcript, StringComparison.Ordinal);
    }
}
