using System.Text.Json;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;
using ModelsAndMonsters.Orchestration;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// <see cref="EncounterStoryBriefBuilder"/> against a fabricated event timeline — no model, no live
/// simulation — proving the brief is grounded: the killer it names is exactly who struck the blow, every
/// disposition stays distinct, a round-limit ending is described as unresolved rather than decided, a
/// rejected attempt never reaches the model's prompt, and cover/surrender events survive into it.
/// </summary>
public sealed class EncounterStoryBriefTests
{
    private static EncounterEventRecord EngineAction(
        string actionType, bool accepted, string? outcomeSummary = null, object? outcome = null)
    {
        var element = JsonSerializer.SerializeToElement(new
        {
            ActionType = actionType,
            Accepted = accepted,
            OutcomeSummary = outcomeSummary,
            Outcome = outcome
        });
        return new EncounterEventRecord("EngineAction", element);
    }

    private static object AttackOutcome(string attacker, string target, bool died) => new
    {
        OutcomeType = "attack",
        AttackerName = attacker,
        TargetName = target,
        TargetDied = died
    };

    private static GameState FinalStateWith(params Character[] characters) =>
        TestWorld.StateWith([], characters);

    private static EncounterStoryBrief Build(
        IReadOnlyList<EncounterEventRecord> events, GameState finalState, EncounterOutcome outcome, int roundsPlayed = 3) =>
        EncounterStoryBriefBuilder.Build(
            events, "The scenario premise.", "Heroes: Rowan, Elara | Goblins: Vark, Skrit.", finalState,
            "Heroes win: Goblins has no active combatants remaining.", outcome, roundsPlayed,
            contextWindow: 8192, inputBudgetFraction: 0.5);

    [Fact]
    public void The_correct_killer_is_supplied()
    {
        var events = new[]
        {
            EngineAction("attack_character", accepted: true, "Rowan's longsword ends it for the captain.",
                AttackOutcome("Rowan", "Vark", died: true)),
            EngineAction("attack_character", accepted: true, "Elara swings and misses Skrit.",
                AttackOutcome("Elara", "Skrit", died: false))
        };
        var finalState = FinalStateWith(
            TestWorld.Rowan(), TestWorld.Elara(), TestWorld.Vark() with { Disposition = CharacterDisposition.Dead },
            TestWorld.Skrit());

        var brief = Build(events, finalState, EncounterOutcome.Elimination);

        var kill = Assert.Single(brief.Kills);
        Assert.Equal("Rowan killed Vark.", kill);
    }

    [Fact]
    public void Dead_surrendered_escaped_and_active_characters_remain_distinct()
    {
        var finalState = FinalStateWith(
            TestWorld.Rowan(),
            TestWorld.Vark() with { Disposition = CharacterDisposition.Dead },
            TestWorld.Skrit() with { Disposition = CharacterDisposition.Surrendered },
            TestWorld.Elara() with { Disposition = CharacterDisposition.Escaped });

        var brief = Build([], finalState, EncounterOutcome.Mixed);

        var ending = brief.RenderEndingParagraph();
        Assert.Contains("Killed: Vark.", ending, StringComparison.Ordinal);
        Assert.Contains("Surrendered: Skrit.", ending, StringComparison.Ordinal);
        Assert.Contains("Escaped: Elara.", ending, StringComparison.Ordinal);
        Assert.Contains("Still standing: Rowan.", ending, StringComparison.Ordinal);

        // Nobody is double-counted under a disposition that is not their own.
        Assert.DoesNotContain("Killed: Skrit", ending, StringComparison.Ordinal);
        Assert.DoesNotContain("Surrendered: Vark", ending, StringComparison.Ordinal);
        Assert.DoesNotContain("Still standing: Vark", ending, StringComparison.Ordinal);
    }

    [Fact]
    public void A_round_limit_result_is_described_as_unresolved()
    {
        var finalState = FinalStateWith(TestWorld.Rowan(health: 2), TestWorld.Vark());

        var brief = Build([], finalState, EncounterOutcome.HarnessLimit, roundsPlayed: 12);

        Assert.True(brief.EndedUnresolvedAtRoundLimit);
        Assert.Contains("unresolved", brief.RenderEndingParagraph(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NO decision reached", brief.RenderEventsForModel(), StringComparison.Ordinal);

        // The harness states plainly who was still standing — never invents a winner it does not have.
        Assert.Contains("Still standing and still fighting: Rowan, Vark.", brief.RenderEndingParagraph(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_decisive_result_is_never_described_as_unresolved()
    {
        var finalState = FinalStateWith(TestWorld.Rowan(), TestWorld.Vark() with { Disposition = CharacterDisposition.Dead });

        var brief = Build([], finalState, EncounterOutcome.Elimination);

        Assert.False(brief.EndedUnresolvedAtRoundLimit);
        Assert.DoesNotContain("unresolved", brief.RenderEndingParagraph(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NO decision reached", brief.RenderEventsForModel(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_attempt_is_absent_from_the_generated_prompt_and_never_counted_as_a_kill()
    {
        var events = new[]
        {
            EngineAction("attack_character", accepted: true, "Rowan hits Vark hard.",
                AttackOutcome("Rowan", "Vark", died: false)),
            EngineAction("attack_character", accepted: false, "REJECTED: Rowan's wild swing at Skrit never lands.",
                AttackOutcome("Rowan", "Skrit", died: true))
        };
        var finalState = FinalStateWith(TestWorld.Rowan(), TestWorld.Vark(), TestWorld.Skrit());

        var brief = Build(events, finalState, EncounterOutcome.Ongoing);

        var prompt = brief.RenderEventsForModel();
        Assert.Contains("Rowan hits Vark hard.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("REJECTED", prompt, StringComparison.Ordinal);
        Assert.Empty(brief.Kills);
    }

    [Fact]
    public void The_story_brief_contains_cover_and_surrender_events()
    {
        var events = new[]
        {
            EngineAction("take_cover", accepted: true, "Rowan ducks behind the Overturned Mill Workbench."),
            EngineAction("offer_surrender", accepted: true, "Vark holds out his purse and offers to yield to Elara.")
        };
        var finalState = FinalStateWith(TestWorld.Rowan(), TestWorld.Elara(), TestWorld.Vark());

        var brief = Build(events, finalState, EncounterOutcome.Ongoing);

        var prompt = brief.RenderEventsForModel();
        Assert.Contains("Overturned Mill Workbench", prompt, StringComparison.Ordinal);
        Assert.Contains("offers to yield", prompt, StringComparison.Ordinal);
    }
}
