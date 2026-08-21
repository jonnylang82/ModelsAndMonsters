using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// Who an in-world refusal is about. A refusal that names the wrong person is worse than a vague one: it
/// asserts something the character never claimed and leaves the thing they got wrong untouched.
/// </summary>
/// <remarks>
/// From a live run. Rowan gave his purse to Elara in round 3, in the open, and every character learned it.
/// In round 7 Skrit tried three times to steal that purse from Rowan. The engine refused correctly each
/// time — "Rowan is not carrying 'Small Purse of Gold Coins (Rowan's)'" — but the line delivered to Skrit
/// was "You are not carrying the Small Purse of Gold Coins (Rowan's)". True, irrelevant, and about the
/// wrong person. Nothing in it contradicted his belief that Rowan had the purse, so he repeated the same
/// attempt until the attempt limit ended his turn.
/// </remarks>
public sealed class RefusalSubjectTests
{
    private static GameState State() => TestWorld.State(TestWorld.Rowan(), TestWorld.Vark());

    [Fact]
    public void A_theft_refusal_names_the_person_who_was_supposed_to_have_it()
    {
        var steal = new StealItemAction(TestWorld.RowanId, TestWorld.VarkId, "Rope");

        var possessor = InWorldRefusal.PossessorFor(steal, EngineRejectionReason.ItemNotPossessed, State());
        var line = InWorldRefusal.Render(
            EngineRejectionReason.ItemNotPossessed, "Rowan", subjectName: "Rope", possessorName: possessor);

        Assert.Equal("Vark", possessor);
        Assert.Contains("Vark", line, StringComparison.Ordinal);
        Assert.Contains("Rope", line, StringComparison.Ordinal);

        // The failure being prevented: a second-person sentence about the thief's own pockets.
        Assert.DoesNotContain("You are not carrying", line, StringComparison.Ordinal);
    }

    [Theory]
    // Every other route to this rejection is about the actor's OWN belongings, where the second person is
    // exactly right and naming somebody would be wrong.
    [InlineData("use")]
    [InlineData("drop")]
    [InlineData("give")]
    public void An_action_on_your_own_belongings_still_speaks_in_the_second_person(string kind)
    {
        GameAction action = kind switch
        {
            "use" => new UseItemAction(TestWorld.RowanId, "Rope"),
            "drop" => new DropItemAction(TestWorld.RowanId, "Rope"),
            _ => new GiveItemAction(TestWorld.RowanId, TestWorld.VarkId, "Rope")
        };

        var possessor = InWorldRefusal.PossessorFor(action, EngineRejectionReason.ItemNotPossessed, State());
        var line = InWorldRefusal.Render(
            EngineRejectionReason.ItemNotPossessed, "Rowan", subjectName: "Rope", possessorName: possessor);

        Assert.Null(possessor);
        Assert.Contains("You are not carrying the Rope", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_possessor_is_only_ever_bound_for_the_rejection_it_belongs_to()
    {
        // A theft can be refused for many reasons — the target has fled, the item is an equipped weapon, the
        // thief already tried. None of those are about who was carrying what, and naming a possessor there
        // would put an irrelevant person into the sentence.
        var steal = new StealItemAction(TestWorld.RowanId, TestWorld.VarkId, "Rope");

        Assert.Null(InWorldRefusal.PossessorFor(steal, EngineRejectionReason.TargetHasEscaped, State()));
        Assert.Null(InWorldRefusal.PossessorFor(steal, EngineRejectionReason.EquippedWeaponCannotBeTransferred, State()));
    }

    [Fact]
    public void The_refusal_reads_in_world_and_never_names_the_machinery()
    {
        // It still has to survive the machinery-leak rules: no "the engine", no "not supported", no talk of
        // what the world can resolve.
        var steal = new StealItemAction(TestWorld.RowanId, TestWorld.VarkId, "Rope");
        var line = InWorldRefusal.Render(
            EngineRejectionReason.ItemNotPossessed, "Rowan", "Rope",
            InWorldRefusal.PossessorFor(steal, EngineRejectionReason.ItemNotPossessed, State()));

        foreach (var banned in new[] { "engine", "the world can", "not supported", "rulebook", "tool" })
        {
            Assert.DoesNotContain(banned, line, StringComparison.OrdinalIgnoreCase);
        }
    }
}
