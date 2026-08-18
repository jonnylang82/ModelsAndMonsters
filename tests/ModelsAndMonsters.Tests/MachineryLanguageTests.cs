using ModelsAndMonsters.Agents;

namespace ModelsAndMonsters.Tests;

public sealed class MachineryLanguageTests
{
    [Theory]
    // The exact leaks seen in live runs, plus common machinery phrasings.
    [InlineData("You are simply moving and watching; that is not an action the world can resolve here.")]
    [InlineData("You cannot shove the chest; the world can only resolve a direct weapon strike, using an item on yourself.")]
    [InlineData("That attempt is not supported.")]
    [InlineData("The engine has no handler for that.")]
    [InlineData("There is no way to resolve that here.")]
    [InlineData("It cannot be resolved by the world.")]
    // Knowledge-view scaffolding a weak instruct model (granite4.1) parroted into its ANSWERS, plus markdown.
    [InlineData("Rowan directly knows that the two supply cases are shut and grime-covered.")]
    [InlineData("You have not been told anything about their contents.")]
    [InlineData("Skrit, you cannot see inside; nor has anyone told you what lies within.")]
    [InlineData("You have not inspected the case closely, so its markings are unknown to you.")]
    [InlineData("The case is **CLOSED** and covered in grime.")]
    [InlineData("- Vark is **lightly wounded**.")]
    // Rejection variants that slipped past the first detector (the "a" defeats a bare "not supported").
    [InlineData("That is not a supported action in this world.")]
    [InlineData("You must land a direct hit to resolve the encounter.")]
    [InlineData("Shoving is not a permitted action here.")]
    // Take/container mechanics leaked in a live run's refusals: the one-at-a-time rule, "a separate action",
    // the open-before-take precondition, and "you can only act on what you know is there".
    [InlineData("You cannot snatch a specific item from a container without first opening it, and even then you must take one item at a time.")]
    [InlineData("That is a separate action from opening or securing the container.")]
    [InlineData("You can only act on things you know are there, and the potion remains hidden from your sight.")]
    public void Machinery_language_is_detected(string text) =>
        Assert.True(MachineryLanguage.IsLeak(text));

    [Theory]
    // Ordinary in-world lines must pass through untouched — the detector matches framing-specific wording
    // and markup, not everyday words a person would use. This covers refusals and question answers alike.
    [InlineData("There is nowhere in this cramped room to open real distance.")]
    [InlineData("You have no way to leave the ground, so the ceiling is beyond you.")]
    [InlineData("Your blade only skids off the wet stone and finds no purchase.")]
    [InlineData("You could throw it, but nothing here comes of that.")]
    [InlineData("The two cases stand shut before you, their markings caked in grime.")]
    [InlineData("You saw a potion inside earlier, but you cannot tell if it is still there.")]
    [InlineData("You have not looked closely enough to read the faded mark.")]
    [InlineData("The goblin looks wounded, favouring one side.")]
    [InlineData("")]
    [InlineData(null)]
    public void In_world_lines_are_not_flagged(string? text) =>
        Assert.False(MachineryLanguage.IsLeak(text));
}
