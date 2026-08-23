using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Knowledge;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Randomness;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The deterministic <see cref="AnswerFacts"/> projection (v0.7): the complete set of facts a character's
/// question may be answered from, and nothing else.
/// </summary>
/// <remarks>
/// This is the boundary that replaces "please do not invent an answer" with "there is nothing to invent from".
/// Weaker models were measured answering from hidden state, saying consumed items were still carried, and
/// promising tactical manoeuvres the engine cannot resolve. Each of those is a test here, asserted against the
/// projection rather than against a model's behaviour, because the projection is what makes them impossible.
/// </remarks>
public sealed class AnswerFactsTests
{
    private static (AnswerFactsProjector Projector, KnowledgeLedger Ledger, NarrationLog Log) Build()
    {
        var ledger = new KnowledgeLedger();
        var log = new NarrationLog();
        return (new AnswerFactsProjector(ledger, log), ledger, log);
    }

    private static GameState V07State(params Character[] characters) =>
        TestWorld.V07State(exitOpen: false, characters);

    private static string Rendered(AnswerFacts facts) => facts.Render();

    // ------------------------------------------------------------------------------------------
    // Current state supersedes remembered state
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_consumed_item_is_absent_from_the_current_facts_and_named_as_gone()
    {
        var (projector, ledger, _) = Build();
        var engine = TestWorld.V07Engine(new SeededRng(1), CombatRules.NoGlancing);

        // Everyone can see what Vark openly carries, including the salve.
        var possession = ledger.GetOrAddItemPossessionFact("goblin-salve", "Vial of Goblin Salve", "Vark", 0);
        ledger.Learn(TestWorld.RowanId, possession.Fact.Id, KnowledgeSource.PublicEvent, 1, 1, 0);

        var before = Rendered(projector.Project(engine.State, TestWorld.RowanId, "What is the goblin carrying?"));
        Assert.Contains("Vial of Goblin Salve", before, StringComparison.Ordinal);

        // Vark drinks it. The knowledge record still exists, but the item does not.
        Assert.True(engine.Execute(new UseItemAction("Vark", "goblin-salve")).Accepted);

        var after = projector.Project(engine.State, TestWorld.RowanId, "What is the goblin carrying?");
        var text = Rendered(after);
        Assert.Contains("no longer exists anywhere", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Vark is openly carrying Vial of Goblin Salve", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Current_ownership_supersedes_a_historical_record_of_it()
    {
        var (projector, ledger, _) = Build();
        var engine = TestWorld.V07Engine(new SeededRng(1), CombatRules.NoGlancing);

        var possession = ledger.GetOrAddItemPossessionFact("purse-vark", "Small Purse of Gold Coins", "Vark", 0);
        ledger.Learn(TestWorld.RowanId, possession.Fact.Id, KnowledgeSource.PublicEvent, 1, 1, 0);

        // The purse changes hands. The old record says Vark carries it; current state says Skrit does.
        Assert.True(engine.Execute(new GiveItemAction("Vark", "Skrit", "purse-vark")).Accepted);

        var facts = projector.Project(engine.State, TestWorld.RowanId, "Who has the goblin captain's purse?");

        Assert.Contains(facts.KnownFirstHand, line => line.Contains("Right now, Skrit is carrying it", StringComparison.Ordinal));
        Assert.Contains(facts.PlainlyVisible, line => line.Contains("Skrit is openly carrying", StringComparison.Ordinal));
        Assert.DoesNotContain(facts.PlainlyVisible, line => line.Contains("Vark is openly carrying", StringComparison.Ordinal));
    }

    [Fact]
    public void A_remembered_container_observation_is_paired_with_what_has_changed_since()
    {
        var (projector, ledger, _) = Build();
        var state = TestWorld.StateWith([TestWorld.MedicineCase(open: true)],
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());
        var engine = new GameEngine(state, new SeededRng(1), CombatRules.NoGlancing);

        var contents = ledger.GetOrAddContentsFact(TestWorld.MedicineCaseId, "Faded Shrine Medicine Case",
            [TestWorld.HealingPotion(5)], 0);
        ledger.Learn(TestWorld.RowanId, contents.Fact.Id, KnowledgeSource.OpenedContainer, 1, 1, 0);

        var unchanged = projector.Project(engine.State, TestWorld.RowanId, "Is the potion still in the case?");
        Assert.Contains(unchanged.KnownFirstHand, line => line.Contains("still holds", StringComparison.Ordinal));

        // Elara takes it out; Rowan's memory is now stale, and the projection says exactly that.
        Assert.True(engine.Execute(new TakeItemAction("Elara", TestWorld.MedicineCaseId, "Small Healing Potion")).Accepted);

        var stale = projector.Project(engine.State, TestWorld.RowanId, "Is the potion still in the case?");
        Assert.Contains(stale.KnownFirstHand, line => line.Contains("no longer in it", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------
    // Hidden information stays hidden — and is recorded as withheld
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Hidden_container_contents_are_absent_from_the_projection_and_recorded_as_withheld()
    {
        var (projector, _, _) = Build();
        var state = TestWorld.StateWith([TestWorld.MedicineCase(), TestWorld.MillCrate()],
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        var facts = projector.Project(state, TestWorld.RowanId, "What is in the case?");
        var text = Rendered(facts);

        // Not a word of the contents reaches the model.
        Assert.DoesNotContain("Small Healing Potion", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Bundle of Damp Rags", text, StringComparison.Ordinal);
        Assert.Contains("never seen inside", text, StringComparison.Ordinal);

        // And the omission is recorded, so the boundary is provable rather than asserted.
        Assert.Contains(facts.OmittedHiddenFacts, f => f.Contains("contents of the Faded Shrine Medicine Case", StringComparison.Ordinal));
        Assert.DoesNotContain("Small Healing Potion", facts.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_open_container_is_reported_as_open_yet_its_contents_stay_hidden_from_someone_who_never_looked()
    {
        var (projector, _, _) = Build();
        var state = TestWorld.StateWith([TestWorld.MedicineCase(open: true)],
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        var text = Rendered(projector.Project(state, TestWorld.RowanId, "Is the case open?"));

        // The truth about the lid is public and must never be denied to explain ignorance of the contents.
        Assert.Contains("it is OPEN", text, StringComparison.Ordinal);
        Assert.Contains("never seen inside", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Small Healing Potion", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unexamined_exterior_marking_is_withheld()
    {
        var (projector, _, _) = Build();
        var state = TestWorld.StateWith([TestWorld.MedicineCase()],
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        var facts = projector.Project(state, TestWorld.RowanId, "What do the markings on the case say?");

        Assert.DoesNotContain(TestWorld.MedicineClue, Rendered(facts), StringComparison.Ordinal);
        Assert.Contains(facts.OmittedHiddenFacts, f => f.Contains("exterior marking", StringComparison.Ordinal));
        Assert.Contains("too worn to make out", Rendered(facts), StringComparison.Ordinal);
    }

    [Fact]
    public void An_examined_marking_becomes_answerable()
    {
        var (projector, ledger, _) = Build();
        var state = TestWorld.StateWith([TestWorld.MedicineCase()],
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        var marking = ledger.GetOrAddMarkingFact(TestWorld.MedicineCaseId, TestWorld.MedicineClue);
        ledger.Learn(TestWorld.RowanId, marking.Fact.Id, KnowledgeSource.DirectInspection, 1, 1, 0);

        Assert.Contains(TestWorld.MedicineClue, Rendered(projector.Project(state, TestWorld.RowanId, "What is that mark?")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void No_exact_number_appears_in_the_projection_at_all()
    {
        var (projector, _, _) = Build();
        var state = V07State();

        var facts = projector.Project(state, TestWorld.ElaraId, "How badly hurt is the captain?");
        var text = Rendered(facts);

        // Conditions are bands, never totals: nothing the model could read a health value out of.
        Assert.Contains("wounded", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12/12", text, StringComparison.Ordinal);
        Assert.DoesNotContain("armour 2", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(facts.OmittedHiddenFacts, f => f.Contains("every exact number", StringComparison.Ordinal));
    }

    [Fact]
    public void Another_characters_private_knowledge_is_never_projected()
    {
        var (projector, ledger, _) = Build();
        var state = TestWorld.StateWith([TestWorld.MedicineCase()],
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        // Vark alone knows what his own case holds.
        var contents = ledger.GetOrAddContentsFact(TestWorld.MedicineCaseId, "Faded Shrine Medicine Case",
            [TestWorld.HealingPotion(5)], 0);
        ledger.Learn(TestWorld.VarkId, contents.Fact.Id, KnowledgeSource.Backstory, 0, 0, 0);

        var rowansFacts = Rendered(projector.Project(state, TestWorld.RowanId, "What does the goblin know?"));

        Assert.DoesNotContain("Small Healing Potion", rowansFacts, StringComparison.Ordinal);

        // Vark's own projection does contain it, which is what makes the boundary meaningful rather than blanket.
        Assert.Contains("Small Healing Potion", Rendered(projector.Project(state, TestWorld.VarkId, "What is in my case?")),
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // Public facts stay available
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Public_facts_remain_available_the_standing_weapons_exits_and_offers()
    {
        var (projector, _, _) = Build();
        var engine = TestWorld.V07Engine(new SeededRng(1), CombatRules.NoGlancing);
        Assert.True(engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: true)).Accepted);

        var text = Rendered(projector.Project(engine.State, TestWorld.ElaraId, "How does it stand?"));

        Assert.Contains("Rowan (on your side) is still fighting", text, StringComparison.Ordinal);
        Assert.Contains("Vark (against you) is still fighting", text, StringComparison.Ordinal);
        Assert.Contains("Vark holds a Notched Sabre", text, StringComparison.Ordinal);
        Assert.Contains("Cellar Stair Door is SHUT", text, StringComparison.Ordinal);
        // The offer and its terms are public — everyone present heard them — and so is whose decision it is.
        Assert.Contains("offered to give up the fight to Rowan", text, StringComparison.Ordinal);
        Assert.Contains("still fighting and can still be struck", text, StringComparison.Ordinal);
        Assert.Contains("Rowan's decision alone", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_characters_own_condition_belongings_and_statuses_are_always_projected()
    {
        var (projector, _, _) = Build();
        var engine = TestWorld.V07Engine(new SeededRng(1), CombatRules.NoGlancing);
        Assert.True(engine.Execute(new DefendAction("Elara")).Accepted);

        var facts = projector.Project(engine.State, TestWorld.ElaraId, "How am I placed?");
        var text = Rendered(facts);

        Assert.Contains("You hold your Iron Mace", text, StringComparison.Ordinal);
        Assert.Contains("Small Purse of Gold Coins", text, StringComparison.Ordinal);
        Assert.Contains("defending", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Healing Prayer", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Hearsay_is_projected_as_a_claim_and_never_as_knowledge()
    {
        var (projector, _, log) = Build();
        var state = V07State();

        var speech = log.RecordSpeech(TestWorld.VarkId, "Vark says:\n\"The shrine case holds a draught, human.\"");
        speech.MarkDeliveredTo(TestWorld.RowanId);

        var facts = projector.Project(state, TestWorld.RowanId, "What is in the case?");

        Assert.Contains(facts.Hearsay, line => line.Contains("shrine case holds a draught", StringComparison.Ordinal));
        Assert.Contains("ONLY BEEN TOLD", Rendered(facts), StringComparison.Ordinal);
        Assert.DoesNotContain(facts.KnownFirstHand, line => line.Contains("draught", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------
    // Affordances: the closed list, and the absences stated flatly
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Unsupported_positional_mechanics_are_stated_as_absent_rather_than_promised()
    {
        var (projector, _, _) = Build();

        var facts = projector.Project(V07State(), TestWorld.RowanId, "Can I back away and flank the captain?");
        var text = Rendered(facts);

        Assert.Contains("no position, distance, facing, movement or spacing of any kind", text, StringComparison.Ordinal);
        Assert.Contains("no backing away", text, StringComparison.Ordinal);
        Assert.Contains("no flanking", text, StringComparison.Ordinal);
        Assert.Contains("no knocking down, no tripping", text, StringComparison.Ordinal);

        // v0.9: cover is the one named exception to the no-positioning rule, but V07State() seeds none, so
        // nothing in the affordance list may offer it here.
        Assert.Contains("environmental cover", text, StringComparison.Ordinal);
        Assert.DoesNotContain(facts.Affordances, a => a.Contains("flank", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(facts.Affordances, a => a.Contains("back away", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(facts.Affordances, a => a.Contains("take cover", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_available_Guard_Ally_affordance_is_explained_in_full()
    {
        var (projector, _, _) = Build();

        var facts = projector.Project(V07State(), TestWorld.RowanId, "Is there anything I can do to protect Elara?");

        var guard = Assert.Single(facts.Affordances, a => a.StartsWith("Guard Ally", StringComparison.Ordinal));
        Assert.Contains("on Elara", guard, StringComparison.Ordinal);
        Assert.Contains("the next blow an enemy aims at them lands on the guardian", guard, StringComparison.Ordinal);
    }

    [Fact]
    public void A_spent_ability_is_reported_as_unavailable_rather_than_offered()
    {
        var (projector, _, _) = Build();
        var engine = TestWorld.V07Engine(new SeededRng(1), CombatRules.NoGlancing);
        Assert.True(engine.Execute(new UseAbilityAction("Elara", AbilityCatalog.HealingPrayerId, "Elara")).Accepted);

        var facts = projector.Project(engine.State, TestWorld.ElaraId, "Can I heal Rowan?");

        Assert.Contains(facts.Affordances, a => a.Contains("already used up in this fight", StringComparison.Ordinal));
        Assert.Contains(facts.AboutYourself, a => a.Contains("have nothing left of it", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unarmed_character_is_told_plainly_that_they_cannot_strike()
    {
        var (projector, _, _) = Build();
        var disarmed = TestWorld.VarkV07() with { Weapon = null };
        var state = V07State(TestWorld.RowanV07(), TestWorld.ElaraV07(), disarmed, TestWorld.SkritV07());

        var facts = projector.Project(state, TestWorld.VarkId, "Can I still fight?");

        Assert.Contains(facts.Affordances, a => a.StartsWith("Strike somebody: NO", StringComparison.Ordinal));
        Assert.Contains(facts.AboutYourself, a => a.Contains("You hold no weapon at all", StringComparison.Ordinal));
    }

    [Fact]
    public void The_affordance_list_offers_negotiated_surrender_and_only_the_recipient_an_acceptance()
    {
        var (projector, _, _) = Build();
        var engine = TestWorld.V07Engine(new SeededRng(1), CombatRules.NoGlancing);
        Assert.True(engine.Execute(new OfferSurrenderAction("Vark", "Rowan", ["purse-vark"], ForfeitWeapon: true)).Accepted);

        var rowan = projector.Project(engine.State, TestWorld.RowanId, "What are my options?");
        var elara = projector.Project(engine.State, TestWorld.ElaraId, "What are my options?");

        // The named recipient may take it up; nobody else may.
        Assert.Contains(rowan.Affordances, a => a.StartsWith("Take up Vark's offer", StringComparison.Ordinal));
        Assert.DoesNotContain(elara.Affordances, a => a.StartsWith("Take up", StringComparison.Ordinal));

        // And an offer of one's own is always on the list, with its cost stated.
        Assert.Contains(elara.Affordances, a => a.Contains("Offer to give up the fight to ONE of them", StringComparison.Ordinal));
        Assert.Contains(elara.Affordances, a => a.Contains("does not protect you", StringComparison.Ordinal));
    }

    [Fact]
    public void The_affordance_list_closes_itself_so_nothing_else_can_be_offered()
    {
        var (projector, _, _) = Build();

        var facts = projector.Project(V07State(), TestWorld.SkritId, "What can I do?");

        Assert.Equal("There is nothing else. No other kind of action exists in this world.", facts.Affordances[^1]);
    }

    // ------------------------------------------------------------------------------------------
    // Attack outcomes are answerable, and a guard relationship never leaves its partner unnamed
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Reproduces a live v0.10 misanswer: Rowan guards Elara, Elara attacks Vark directly (a guard never
    /// redirects a blow aimed at an opponent), the engine confirms the blow landed, and Vark asks whether it
    /// did. Before this fix, nothing in Vark's projected facts confirmed the attack happened at all — the
    /// Dungeon Master had only the ambiguously-worded guard statuses to answer from, and guessed wrong.
    /// </summary>
    [Fact]
    public void An_attack_that_lands_is_a_fact_its_target_can_be_answered_from()
    {
        var (projector, ledger, _) = Build();
        var engine = TestWorld.V07Engine(new SeededRng(1), CombatRules.NoGlancing);

        Assert.True(engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara")).Accepted);

        var attack = engine.Execute(new AttackCharacterAction("Elara", "Vark", "Iron Mace"));
        Assert.True(attack.Accepted);
        var outcome = Assert.IsType<AttackOutcome>(attack.Outcome);
        Assert.True(outcome.Hit);
        Assert.False(outcome.Redirected); // Rowan's guard shields Elara; it can never redirect a blow aimed at Vark.

        var worldVersion = engine.State.Version;
        var fact = ledger.GetOrAddAttackFact(outcome.AttackerId, outcome.AttackerName, outcome.TargetId, outcome.TargetName,
            outcome.WeaponName, outcome.Hit, outcome.Redirected, outcome.IntendedTargetName, worldVersion);
        ledger.Learn(TestWorld.VarkId, fact.Fact.Id, KnowledgeSource.PublicEvent, 3, 11, worldVersion);

        var facts = projector.Project(engine.State, TestWorld.VarkId,
            "Did Elara's blow land on Vark, or did Rowan's intervention block it?");

        Assert.Contains(facts.KnownFirstHand,
            line => line.Contains("Elara", StringComparison.Ordinal) && line.Contains("landed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missed_attack_is_also_a_fact_its_target_can_be_answered_from()
    {
        var (projector, ledger, _) = Build();
        var engine = TestWorld.V07Engine(new SeededRng(1), CombatRules.NoGlancing, TestWorld.RowanV07(),
            TestWorld.ElaraV07(), TestWorld.VarkV07(hitChance: 0), TestWorld.SkritV07());

        var attack = engine.Execute(new AttackCharacterAction("Vark", "Rowan", "Notched Sabre"));
        var outcome = Assert.IsType<AttackOutcome>(attack.Outcome);
        Assert.False(outcome.Hit);

        var worldVersion = engine.State.Version;
        var fact = ledger.GetOrAddAttackFact(outcome.AttackerId, outcome.AttackerName, outcome.TargetId, outcome.TargetName,
            outcome.WeaponName, outcome.Hit, outcome.Redirected, outcome.IntendedTargetName, worldVersion);
        ledger.Learn(TestWorld.RowanId, fact.Fact.Id, KnowledgeSource.PublicEvent, 1, 1, worldVersion);

        var facts = projector.Project(engine.State, TestWorld.RowanId, "Did the goblin's blow land on me?");

        Assert.Contains(facts.KnownFirstHand,
            line => line.Contains("Vark", StringComparison.Ordinal) && line.Contains("missed", StringComparison.Ordinal));
    }

    /// <summary>
    /// The mirror case: a blow genuinely redirected by a guard names who actually took it, never leaving the
    /// intended target thinking the blow simply vanished or landed on them anyway.
    /// </summary>
    [Fact]
    public void A_redirected_attack_names_who_actually_took_the_blow()
    {
        var (projector, ledger, _) = Build();
        var engine = TestWorld.V07Engine(new SeededRng(1), CombatRules.NoGlancing);

        Assert.True(engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara")).Accepted);

        var attack = engine.Execute(new AttackCharacterAction("Vark", "Elara", "Notched Sabre"));
        var outcome = Assert.IsType<AttackOutcome>(attack.Outcome);
        Assert.True(outcome.Redirected);
        Assert.Equal(TestWorld.RowanId, outcome.TargetId);

        var worldVersion = engine.State.Version;
        var fact = ledger.GetOrAddAttackFact(outcome.AttackerId, outcome.AttackerName, outcome.TargetId, outcome.TargetName,
            outcome.WeaponName, outcome.Hit, outcome.Redirected, outcome.IntendedTargetName, worldVersion);
        ledger.Learn(TestWorld.ElaraId, fact.Fact.Id, KnowledgeSource.PublicEvent, 1, 1, worldVersion);

        var facts = projector.Project(engine.State, TestWorld.ElaraId, "Did Vark's blow land on me?");

        Assert.Contains(facts.KnownFirstHand, line =>
            line.Contains("Rowan", StringComparison.Ordinal) &&
            line.Contains("instead", StringComparison.Ordinal) &&
            line.Contains("Elara", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half of the same v0.10 fix: a guard relationship must name its actual partner rather than
    /// leaving "them"/"this character" to be resolved by whoever happens to be reading it. Regression test
    /// for the exact wording a live run sent to a model that then misattributed the guard to the wrong
    /// character entirely.
    /// </summary>
    [Fact]
    public void A_guard_relationship_names_its_own_partner_rather_than_an_unscoped_pronoun()
    {
        var (projector, _, _) = Build();
        var engine = TestWorld.V07Engine(new SeededRng(1), CombatRules.NoGlancing);
        Assert.True(engine.Execute(new UseAbilityAction("Rowan", AbilityCatalog.GuardAllyId, "Elara")).Accepted);

        var facts = projector.Project(engine.State, TestWorld.VarkId,
            "Did Elara's blow land on Vark, or did Rowan's intervention block it?");

        Assert.Contains(facts.PlainlyVisible, line => line.Contains("Rowan is standing guard over Elara", StringComparison.Ordinal));
        Assert.Contains(facts.PlainlyVisible, line => line.Contains("Elara is guarded by Rowan", StringComparison.Ordinal));

        // Never phrased so vaguely that the guard could be misread as concerning anyone but Elara.
        Assert.DoesNotContain(facts.PlainlyVisible, line => line.Contains("an ally", StringComparison.Ordinal));
        Assert.DoesNotContain(facts.PlainlyVisible, line => line.Contains("aimed at them", StringComparison.Ordinal));
        Assert.DoesNotContain(facts.PlainlyVisible, line => line.Contains("this character", StringComparison.Ordinal));
    }

    [Fact]
    public void The_projection_never_contains_the_withheld_facts_it_records()
    {
        var (projector, _, _) = Build();
        var state = TestWorld.StateWith([TestWorld.MedicineCase()],
            TestWorld.RowanV07(), TestWorld.ElaraV07(), TestWorld.VarkV07(), TestWorld.SkritV07());

        var facts = projector.Project(state, TestWorld.RowanId, "What is in there?");

        Assert.NotEmpty(facts.OmittedHiddenFacts);
        var rendered = facts.Render();
        // The withheld list is for the trace alone, and its own text never leaks into what the model is sent.
        Assert.DoesNotContain("has no way of knowing", rendered, StringComparison.Ordinal);
        Assert.Contains("has no way of knowing", facts.RenderOmitted(), StringComparison.Ordinal);
    }
}
