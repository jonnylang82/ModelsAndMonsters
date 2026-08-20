using ModelsAndMonsters.Agents;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The machinery lint after it stopped being the primary means of keeping text in the fiction.
/// </summary>
/// <remarks>
/// The old detector grew a synonym per run — "not allowed", "another character", "the facts", "a living
/// ally" — and still kept missing new phrasings, because the ways to describe a rule in English are
/// unbounded. Worse, each catch triggered a rephrasing model call, and live runs showed those rewrites
/// inventing obstacles that were not true and narrating events inside refusals that change nothing.
/// The common paths are now in-world by construction (see <see cref="InWorldRefusalTests"/>), so this is
/// only a final lint over the one remaining prose path, matching nothing but unmistakable machine
/// vocabulary. These tests therefore assert BOTH halves of that contract: the machine terms are caught, and
/// the rule-flavoured English that used to be caught is deliberately, explicitly, no longer caught.
/// </remarks>
public sealed class MachineryLanguageTests
{
    [Theory]
    // 1. The harness's own nouns for itself and its numbers. Nobody in a cellar has a word for these.
    [InlineData("Consult the rulebook before you try that.")]
    [InlineData("The engine has no handler for that.")]
    [InlineData("Your hit chance is lower while you are off balance.")]
    [InlineData("That would change the world version.")]
    [InlineData("The blow leaves a status effect on them until their next turn.")]
    [InlineData("The turn cost of that is your whole turn.")]
    [InlineData("Your disposition is unchanged by that.")]
    [InlineData("You would need a d100 for that.")]
    [InlineData("The attack roll missed.")]
    // 2. Stable identifiers, which are minted by the harness and can never occur in speech.
    [InlineData("You may accept offer-1 on your turn.")]
    [InlineData("That was settled under agreement-2.")]
    [InlineData("Look inside corpse-goblin-skrit for the purse.")]
    [InlineData("That would need the guard-ally technique, which is not yours.")]
    [InlineData("You have no healing-prayer left.")]
    // 3. Tool names. snake_case is not a thing anybody says out loud.
    [InlineData("Use accept_surrender to take those terms.")]
    [InlineData("You should call take_action instead.")]
    [InlineData("That is a steal_item, not a take_item.")]
    // v0.8: the machine's own morale and quality vocabulary.
    [InlineData("Use intimidate_character on the captain.")]
    [InlineData("That would be a steady_ally, not speech.")]
    [InlineData("Your fear score is now three.")]
    [InlineData("His fear level rises by one.")]
    [InlineData("The Scared status is now on you.")]
    [InlineData("The quality roll came up critical.")]
    [InlineData("That blow deals double damage.")]
    [InlineData("It was resolved as intimidation-2.")]
    public void Unmistakable_machine_vocabulary_is_caught(string text) =>
        Assert.True(MachineryLanguage.IsLeak(text));

    [Theory]
    // Every one of these WAS caught by the old detector and is deliberately no longer. They are rule-flavoured
    // English, not machine vocabulary: a person could say any of them, and the paths that used to produce them
    // no longer generate text at all — engine refusals render from their rejection code instead. Keeping them
    // as matches is what made the list grow without ever converging, and what made ordinary fiction unsafe.
    [InlineData("That attempt is not supported.")]
    [InlineData("Shoving is not a permitted action here.")]
    [InlineData("Two actions in one breath are not allowed here.")]
    [InlineData("Striking and snatching cannot be combined; choose one.")]
    [InlineData("You have no such abilities left to call on.")]
    [InlineData("There is no way to force an item out of another character's possession by shouting.")]
    [InlineData("You cannot demand Vark surrender; only a character may offer their own surrender.")]
    [InlineData("Only an actual blow or item transfer can be made here.")]
    [InlineData("Your blade can only brace against yourself or a living ally.")]
    [InlineData("That is a separate action from opening or securing the container.")]
    [InlineData("You can only act on things you know are there.")]
    [InlineData("You have not been told anything about their contents.")]
    [InlineData("Elara is wounded, but the facts give you no detail beyond that.")]
    public void Rule_flavoured_english_is_no_longer_treated_as_a_leak(string text) =>
        Assert.False(MachineryLanguage.IsLeak(text));

    [Theory]
    // Ordinary in-world lines, which must pass untouched — including ones that brush against the machine's
    // vocabulary without being it ("your resolve", "guard", "a status").
    [InlineData("There is nowhere in this cramped room to open real distance.")]
    [InlineData("Your blade only skids off the wet stone and finds no purchase.")]
    [InlineData("The goblin looks wounded, favouring one side.")]
    [InlineData("You have never learned to stand over a companion and take the blow meant for them.")]
    [InlineData("You set your feet and bring your guard up; the next blow will find less purchase.")]
    [InlineData("Vark holds out his purse and says he will give it up if you let him crawl out.")]
    [InlineData("The captain is already standing over the runt; there is no room for a second guard.")]
    [InlineData("Your resolve holds, and you keep your feet in the cold water.")]
    [InlineData("She is resolved to see this through, whatever it costs her.")]
    [InlineData("Rowan puts himself between the goblins and Elara.")]
    [InlineData("He keeps his grip on it even as the blow lands.")]
    [InlineData("You keep it close, and that is all you need to know.")]
    // v0.8: fear is an ordinary word and a critical blow is a thing a person can see. Neither is banned —
    // banning them would push morale narration into worse phrasings to say the same thing.
    [InlineData("Fear takes him, and his grip on the sabre goes loose.")]
    [InlineData("He is afraid, and he is not hiding it well.")]
    [InlineData("The blow lands critical and opens him from shoulder to ribs.")]
    [InlineData("Something goes out of her, and her eyes go to the door.")]
    [InlineData("Rowan catches her eye and tells her to hold; she steadies.")]
    [InlineData("")]
    [InlineData(null)]
    public void In_world_lines_are_not_flagged(string? text) =>
        Assert.False(MachineryLanguage.IsLeak(text));

    [Theory]
    // Formatting is a PRESENTATION defect, handled by deterministic cleanup rather than by a rewrite. It is
    // explicitly not a semantic leak: "the case is **CLOSED**" says a true and permitted thing badly.
    [InlineData("The case is **CLOSED** and covered in grime.")]
    [InlineData("- Vark is **lightly wounded**.")]
    [InlineData("## What you see")]
    public void Markdown_is_not_a_semantic_leak(string text) =>
        Assert.False(MachineryLanguage.IsLeak(text));

    [Theory]
    [InlineData("The case is **CLOSED** and covered in grime.", "The case is CLOSED and covered in grime.")]
    [InlineData("- Vark is **lightly wounded**.", "Vark is lightly wounded.")]
    [InlineData("## What you see", "What you see")]
    [InlineData("He is *badly* hurt.", "He is badly hurt.")]
    [InlineData("Use the `iron mace`.", "Use the iron mace.")]
    [InlineData("The goblin looks wounded.", "The goblin looks wounded.")]
    public void Markdown_is_cleaned_deterministically_without_changing_the_words(string text, string expected) =>
        Assert.Equal(expected, ModelText.StripPresentationMarkup(text));

    [Fact]
    public void Cleaning_markup_never_needs_a_leak_check_to_run()
    {
        // The point of separating them: a stray asterisk used to trigger a whole rephrasing model call, and a
        // rewrite is free to be worse than what it replaced. Now it is a string operation with no model and
        // no possibility of changing what was said.
        const string formatted = "**Elara** is wounded but still on her feet.";

        Assert.False(MachineryLanguage.IsLeak(formatted));
        Assert.Equal("Elara is wounded but still on her feet.", ModelText.StripPresentationMarkup(formatted));
    }
}
