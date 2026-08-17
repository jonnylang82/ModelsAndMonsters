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
}
