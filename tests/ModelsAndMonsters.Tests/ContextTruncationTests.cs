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

    [Theory]
    // The measured case, verbatim from a live qwen run: input 8152 + output 40 filled an 8192 window while
    // the 600-token output budget sat almost untouched. The harness reported "truncated at the output-token
    // limit" and advised raising MaxOutputTokens — which shares the same window, so it would have made the
    // request fail sooner.
    [InlineData(8152, 40, 8192, 600, true)]
    [InlineData(8183, 9, 8192, 600, true)]
    [InlineData(8100, 92, 8192, 600, true)]
    // A genuine output-limit stop: the window has room to spare and the budget is spent.
    [InlineData(4000, 600, 8192, 600, false)]
    [InlineData(4000, 580, 8192, 600, false)]
    // Filling the window while also spending the budget is an output limit, not an exhausted context.
    [InlineData(7592, 600, 8192, 600, false)]
    // Unknown window or missing usage cannot be judged, so it is never claimed.
    [InlineData(8152, 40, 0, 600, false)]
    public void A_full_context_window_is_told_apart_from_a_spent_output_budget(
        long input, long output, int window, int maxOutput, bool expected) =>
        Assert.Equal(expected, ContextTruncation.WasContextExhausted(
            input, output, window == 0 ? null : window, maxOutput));

    [Theory]
    // A reply that spent essentially its whole output budget was cut off at that budget, whatever the
    // provider said — which for some providers is nothing at all.
    [InlineData(1500, 1500, true)]
    [InlineData(1495, 1500, true)]
    [InlineData(900, 1500, false)]
    [InlineData(40, 600, false)]
    public void A_spent_output_budget_is_recognised_from_usage_alone(long output, int cap, bool expected) =>
        Assert.Equal(expected, ContextTruncation.WasOutputBudgetSpent(output, cap));

    [Fact]
    public void An_unknown_budget_or_usage_never_claims_a_spent_one()
    {
        Assert.False(ContextTruncation.WasOutputBudgetSpent(null, 1500));
        Assert.False(ContextTruncation.WasOutputBudgetSpent(1500, null));
        Assert.False(ContextTruncation.WasOutputBudgetSpent(1500, 0));
    }

    [Fact]
    public void Missing_usage_never_claims_an_exhausted_context()
    {
        Assert.False(ContextTruncation.WasContextExhausted(null, 40, 8192, 600));
        Assert.False(ContextTruncation.WasContextExhausted(8152, null, 8192, 600));
        Assert.False(ContextTruncation.WasContextExhausted(8152, 40, null, 600));
    }

    [Fact]
    public void A_full_window_with_no_output_budget_set_still_counts_as_exhausted()
    {
        // With no cap configured there is nothing to compare the output against, so filling the window is
        // the whole of the evidence — and it is enough.
        Assert.True(ContextTruncation.WasContextExhausted(8152, 40, 8192, null));
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
        // used unchanged by default — this is exactly the flat ceiling `UnboundedHistoryOnHostedModels`
        // exists to lift, see the next test.
        Assert.Equal(5500, ContextTruncation.EffectiveHistoryBudget(5500, contextWindow: null, maxOutputTokens: 1500, observedPromptOverhead: 2000));
    }

    [Fact]
    public void Unbounded_without_window_lifts_the_flat_ceiling_only_when_no_window_is_known()
    {
        // The whole point of the setting: a hosted profile with EnforceContextWindowOnHostedModels off has no
        // BindingContextWindow, so without this flag it still summarised at the flat configured number — a
        // local-model-sized ceiling reintroduced on exactly the run meant to be exempt from one.
        Assert.Equal(int.MaxValue, ContextTruncation.EffectiveHistoryBudget(
            5500, contextWindow: null, maxOutputTokens: 1500, observedPromptOverhead: 2000, unboundedWithoutWindow: true));

        // It must not change anything once a real window IS known — the derived, window-based budget still
        // wins; "unbounded" only ever applies to the "no window known" branch.
        var derived = ContextTruncation.EffectiveHistoryBudget(
            5500, contextWindow: 8192, maxOutputTokens: 1500, observedPromptOverhead: 2000, unboundedWithoutWindow: true);
        Assert.Equal(
            ContextTruncation.EffectiveHistoryBudget(5500, contextWindow: 8192, maxOutputTokens: 1500, observedPromptOverhead: 2000),
            derived);
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
