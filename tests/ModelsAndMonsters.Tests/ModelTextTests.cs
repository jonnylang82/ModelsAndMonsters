using ModelsAndMonsters.Agents;

namespace ModelsAndMonsters.Tests;

public sealed class ModelTextTests
{
    [Theory]
    [InlineData("{\"name\": \"reject_action\"}", true)]
    [InlineData("```json\n{}\n```", true)]
    [InlineData("You cannot fly.", false)]
    [InlineData("", false)]
    public void Structured_text_is_recognised(string text, bool expected) =>
        Assert.Equal(expected, ModelText.LooksStructured(text));

    [Fact]
    public void A_reason_is_recovered_from_a_tool_call_written_as_text()
    {
        const string leaked =
            "{\"name\": \"reject_action\", \"parameters\": {\"category\": \"unsupported\", " +
            "\"reason\": \"There is nowhere in this cramped room to open distance.\"}}";

        Assert.Equal(
            "There is nowhere in this cramped room to open distance.",
            ModelText.TryExtractJsonField(leaked, "reason", "explanation"));
    }

    [Fact]
    public void Extraction_returns_null_when_no_field_matches_or_the_text_is_not_json()
    {
        Assert.Null(ModelText.TryExtractJsonField("{\"category\": \"unsupported\"}", "reason"));
        Assert.Null(ModelText.TryExtractJsonField("just prose", "reason"));
    }

    [Theory]
    // Form 1: name(argument).
    [InlineData("`take_action(I strike the goblin.)`", "take_action", "I strike the goblin.")]
    [InlineData("take_action(I strike)", "take_action", "I strike")]
    [InlineData("Hmm. ask_dm(Does it look hurt?)", "ask_dm", "Does it look hurt?")]
    [InlineData("end_turn(I have no strength left, and I wait.)", "end_turn", "I have no strength left, and I wait.")]
    [InlineData("```\ntake_action(I lunge at Vark)\n```", "take_action", "I lunge at Vark")]
    // Form 1b: name "argument" with no parentheses — the qwen-family prose shape.
    [InlineData("say \"Elara, look out!\"", "say", "Elara, look out!")]
    [InlineData("ask_dm: \"Does it look hurt?\"", "ask_dm", "Does it look hurt?")]
    [InlineData("say = \"Hold the line!\"", "say", "Hold the line!")]
    // An opening quote fixes the closing quote, so apostrophes inside do not truncate the message.
    [InlineData("say \"Elara, you're bleeding! Stay back!\"", "say", "Elara, you're bleeding! Stay back!")]
    // Curly quotes are accepted too.
    [InlineData("say “Hold the line!”", "say", "Hold the line!")]
    public void A_tool_call_written_as_prose_is_recovered(string text, string name, string argument)
    {
        var recovered = ModelText.TryRecoverToolCall(text, CharacterTools.Names);

        Assert.NotNull(recovered);
        Assert.Equal(name, recovered.Value.Name);
        Assert.Equal(argument, recovered.Value.Argument);
    }

    [Fact]
    public void A_say_written_as_prose_after_a_monologue_is_recovered_verbatim()
    {
        // Vark's exact real-world reply that the old paren-only recovery missed, sending him into a
        // nudge loop: an in-character monologue followed by a parenthesis-free say "...".
        const string reply =
            "My eyes lock onto Elara, her ribs wrapped in cloth. I need to keep the intruders away from my chest.\n\n" +
            "say \"Elara, you're bleeding! Stay back and let me handle these pests!\"";

        var recovered = ModelText.TryRecoverToolCall(reply, CharacterTools.Names);

        Assert.NotNull(recovered);
        Assert.Equal("say", recovered.Value.Name);
        Assert.Equal("Elara, you're bleeding! Stay back and let me handle these pests!", recovered.Value.Argument);
    }

    [Theory]
    // A shout at another character written as prose instead of calling say — the real qwen/granite shape.
    [InlineData("I shout: \"Vark! Don't stand there gawking, decide now.\"", "Vark! Don't stand there gawking, decide now.")]
    [InlineData("I'm pressed to the wall. I shout at him: \"Vark, help me or stay silent!\"", "Vark, help me or stay silent!")]
    [InlineData("say \"Elara, get behind me now\"", "Elara, get behind me now")]
    [InlineData("Skrit yells, \"We must seize the moment before Rowan moves\"", "We must seize the moment before Rowan moves")]
    [InlineData("I whisper to her: “Stay close and watch the captain”", "Stay close and watch the captain")]
    public void An_attempted_spoken_line_in_prose_is_extracted(string text, string expected) =>
        Assert.Equal(expected, ModelText.TryExtractSpokenAttempt(text));

    [Theory]
    // Not speech: no quote at all, a tool call written as prose (no speech verb before the quote), or a cry
    // of fewer than three words, so an action reply is never mistaken for an attempt to talk.
    [InlineData("I bring my sword down hard on the goblin's shoulder.")]
    [InlineData("take_action(\"I bring my sword down on the goblin\")")]
    [InlineData("ask_dm(\"Is the goblin wounded?\")")]
    [InlineData("I swing my axe and yell \"Die!\"")]
    [InlineData("")]
    [InlineData(null)]
    public void Non_speech_prose_is_not_mistaken_for_a_spoken_line(string? text) =>
        Assert.Null(ModelText.TryExtractSpokenAttempt(text));

    [Fact]
    public void A_tool_call_written_as_json_is_recovered()
    {
        var recovered = ModelText.TryRecoverToolCall(
            "{\"name\": \"take_action\", \"arguments\": {\"intent\": \"I bring my sword down on Vark.\"}}",
            CharacterTools.Names);

        Assert.NotNull(recovered);
        Assert.Equal("take_action", recovered.Value.Name);
        Assert.Equal("I bring my sword down on Vark.", recovered.Value.Argument);
    }

    [Theory]
    [InlineData("I raise my sword and wait for an opening.")] // pure prose, no call
    [InlineData("I want to take_action here, eventually.")]   // tool name but no argument parens
    [InlineData("")]
    public void Prose_without_a_recoverable_call_returns_null(string text)
    {
        Assert.Null(ModelText.TryRecoverToolCall(text, CharacterTools.Names));
    }
}
