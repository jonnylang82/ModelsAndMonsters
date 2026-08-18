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
    public void Machinery_language_is_detected(string text) =>
        Assert.True(MachineryLanguage.IsLeak(text));

    [Theory]
    // Ordinary in-world refusals must pass through untouched — the detector matches machinery-specific
    // wording, not everyday words a person would use.
    [InlineData("There is nowhere in this cramped room to open real distance.")]
    [InlineData("You have no way to leave the ground, so the ceiling is beyond you.")]
    [InlineData("Your blade only skids off the wet stone and finds no purchase.")]
    [InlineData("You could throw it, but nothing here comes of that.")]
    [InlineData("")]
    [InlineData(null)]
    public void In_world_refusals_are_not_flagged(string? text) =>
        Assert.False(MachineryLanguage.IsLeak(text));
}
