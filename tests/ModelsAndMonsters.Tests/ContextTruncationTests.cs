using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The sent-versus-received truncation check, pinned to numbers measured from a real run so the
/// threshold cannot drift away from the behaviour it was tuned against.
/// </summary>
public sealed class ContextTruncationTests
{
    // (estimatedSent, reportedReceived) pairs taken from a real DungeonMaster conversation.
    // The early calls under-estimate (reported is higher), which must never read as truncation;
    // the late calls over-shoot the reported size as history was dropped.
    [Theory]
    [InlineData(1872, 1713, false)] // call 0001: estimate slightly high, nothing dropped
    [InlineData(2519, 2871, false)] // call 0003: estimate low, reported higher
    [InlineData(2888, 2583, false)] // DM pass-narration projection: ~305 estimate noise, not truncation
    [InlineData(4525, 5039, false)] // call 0009: estimate low
    [InlineData(8196, 7989, false)] // call 0020: at the ceiling but within estimation noise
    [InlineData(8882, 8154, true)]  // call 0022: first genuine drop (~728)
    [InlineData(9877, 8173, true)]  // call 0025: ~1704 dropped
    [InlineData(10894, 8185, true)] // call 0028: ~2709 dropped
    public void The_check_separates_estimation_noise_from_real_truncation(int sent, long received, bool expected)
    {
        var truncated = ContextTruncation.WasInputTruncated(sent, received, out var dropped);

        Assert.Equal(expected, truncated);
        Assert.Equal(sent - (int)received, dropped);
    }

    [Fact]
    public void A_half_window_truncation_is_caught_even_though_it_is_nowhere_near_the_nominal_window()
    {
        // Some Ollama setups truncate at num_ctx/2. With an 8192 window that plateaus reported input
        // around 4098 while we keep sending more — the case the old window-based check missed entirely.
        Assert.True(ContextTruncation.WasInputTruncated(estimatedSentTokens: 8000, reportedInputTokens: 4098, out var dropped));
        Assert.Equal(3902, dropped);
    }

    [Fact]
    public void The_estimate_scales_with_message_content()
    {
        var small = ContextTruncation.EstimateSentTokens([new ChatMessage(ChatRole.User, "short")]);
        var large = ContextTruncation.EstimateSentTokens([new ChatMessage(ChatRole.User, new string('x', 4000))]);

        Assert.True(small < 20);
        Assert.InRange(large, 850, 1000); // ~4000 chars / 4.4 chars-per-token (calibrated to qwen's tokenizer)
    }

    [Fact]
    public void Tool_call_arguments_count_towards_the_estimate()
    {
        var withArgs = ContextTruncation.EstimateSentTokens(
        [
            new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("id", "attack_character",
                    new Dictionary<string, object?> { ["attacker"] = "Aric", ["target"] = "Grik", ["weapon"] = "Iron Sword" })])
        ]);

        Assert.True(withArgs > 4);
    }

    [Fact]
    public void The_history_budget_reserves_room_for_the_reply_and_the_tool_overhead_inside_the_window()
    {
        // The real qwen case: an 8192 window, a 1500 output budget, and a ~2000-token tool/template overhead
        // the message estimate cannot see. Budgeting against the configured 5500 alone left the real prompt at
        // ~7600 with no output room; the derived budget must trim earlier so the whole request fits.
        var budget = ContextTruncation.EffectiveHistoryBudget(
            configuredBudget: 5500, contextWindow: 8192, maxOutputTokens: 1500, observedPromptOverhead: 2000);

        // 8192 - 1500 (output) - 2000 (overhead) - 256 (safety) = 4436. So message estimate + overhead + output
        // = 4436 + 2000 + 1500 = 7936 < 8192, leaving genuine output room.
        Assert.Equal(4436, budget);
        Assert.True(budget + 2000 + 1500 < 8192);
    }

    [Fact]
    public void The_history_budget_is_the_configured_value_when_no_window_is_known()
    {
        // Hosted models (OpenAI/Anthropic) do not expose a per-request window here; the configured budget is
        // used unchanged, since those windows are far larger than any history this harness produces.
        Assert.Equal(5500, ContextTruncation.EffectiveHistoryBudget(5500, contextWindow: null, maxOutputTokens: 1500, observedPromptOverhead: 2000));
    }

    [Fact]
    public void The_history_budget_never_exceeds_the_configured_cap_even_with_a_huge_window()
    {
        // A large window does not license an unbounded history — the configured budget is an upper bound.
        Assert.Equal(5500, ContextTruncation.EffectiveHistoryBudget(5500, contextWindow: 131072, maxOutputTokens: 1500, observedPromptOverhead: 2000));
    }

    [Fact]
    public void The_history_budget_stays_positive_even_for_a_tiny_window()
    {
        // A window smaller than the reserves must not produce a negative or zero budget that summarises to nothing.
        var budget = ContextTruncation.EffectiveHistoryBudget(5500, contextWindow: 2048, maxOutputTokens: 1500, observedPromptOverhead: 2000);
        Assert.True(budget >= 1024);
    }
}
