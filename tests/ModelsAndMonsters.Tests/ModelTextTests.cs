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
    [InlineData("`take_action(I strike the goblin.)`", "take_action", "I strike the goblin.")]
    [InlineData("take_action(I strike)", "take_action", "I strike")]
    [InlineData("Hmm. ask_dm(Does it look hurt?)", "ask_dm", "Does it look hurt?")]
    [InlineData("end_turn(I have no strength left, and I wait.)", "end_turn", "I have no strength left, and I wait.")]
    [InlineData("```\ntake_action(I lunge at Vark)\n```", "take_action", "I lunge at Vark")]
    public void A_tool_call_written_as_prose_is_recovered(string text, string name, string argument)
    {
        var recovered = ModelText.TryRecoverToolCall(text, CharacterTools.Names);

        Assert.NotNull(recovered);
        Assert.Equal(name, recovered.Value.Name);
        Assert.Equal(argument, recovered.Value.Argument);
    }

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
