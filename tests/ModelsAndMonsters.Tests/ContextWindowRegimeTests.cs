using ModelsAndMonsters.AI;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// Which context window a run is actually budgeted against, and the switch that levels it across providers.
/// </summary>
/// <remarks>
/// <para>
/// Only Ollama takes a window as a request parameter. A hosted provider sets its own, far larger, per model,
/// and never sees the configured number — <see cref="ChatOptionsFactory"/> drops it. Budgeting history
/// summarisation against it there compacts a conversation to fit a limit that does not exist.
/// </para>
/// <para>
/// A live gpt-5.4 run made the cost concrete: every agent inherited <c>ContextWindow: 8192</c> from the
/// Ollama-tuned defaults, and in six rounds the harness summarised eleven times — roughly every other turn —
/// replacing what characters said and did with a recap, and paying a model call for each, against a ceiling
/// the provider would never have enforced. Nothing anywhere said so; the setting was simply dropped from the
/// request and forgotten.
/// </para>
/// </remarks>
public sealed class ContextWindowRegimeTests
{
    private static AgentModelProfile Profile(ModelProvider provider, bool enforce = false) => new()
    {
        AgentName = "Rowan",
        Provider = provider,
        ModelId = "test-model",
        ContextWindow = 8192,
        MaxOutputTokens = 1500,
        EnforceContextWindowOnHostedModels = enforce
    };

    [Fact]
    public void On_Ollama_the_configured_window_binds_because_the_provider_is_given_it()
    {
        Assert.Equal(8192, Profile(ModelProvider.Ollama).BindingContextWindow);
    }

    [Theory]
    [InlineData(ModelProvider.OpenAI)]
    [InlineData(ModelProvider.Anthropic)]
    public void On_a_hosted_provider_the_configured_window_binds_nothing_by_default(ModelProvider provider)
    {
        var profile = Profile(provider);

        // The configured value is still recorded — it is what the operator asked for, and run.json should
        // say so — but it is not what anything budgets against.
        Assert.Equal(8192, profile.ContextWindow);
        Assert.Null(profile.BindingContextWindow);
    }

    [Theory]
    [InlineData(ModelProvider.OpenAI)]
    [InlineData(ModelProvider.Anthropic)]
    public void The_switch_holds_a_hosted_model_to_the_configured_window(ModelProvider provider)
    {
        // What a like-for-like comparison needs: a hosted model that never has to forget anything is not
        // answering the same question as a local one working inside 8k.
        Assert.Equal(8192, Profile(provider, enforce: true).BindingContextWindow);
    }

    [Fact]
    public void With_no_window_configured_nothing_binds_whatever_the_switch_says()
    {
        var profile = Profile(ModelProvider.Ollama) with { ContextWindow = null, EnforceContextWindowOnHostedModels = true };

        Assert.Null(profile.BindingContextWindow);
    }

    [Fact]
    public void The_history_budget_follows_the_binding_window_not_the_configured_one()
    {
        // The consequence that matters. Same configured budget, same configured window, same output reserve
        // — and a hosted agent keeps the whole configured budget while a local one is cut down to fit 8k.
        const int configured = 6000;

        var local = ContextTruncation.EffectiveHistoryBudget(
            configured, Profile(ModelProvider.Ollama).BindingContextWindow, 1500, ContextTruncation.DefaultPromptOverheadTokens);
        var hosted = ContextTruncation.EffectiveHistoryBudget(
            configured, Profile(ModelProvider.OpenAI).BindingContextWindow, 1500, ContextTruncation.DefaultPromptOverheadTokens);

        Assert.True(local < configured, $"A local agent inside an 8k window must be trimmed below {configured}; got {local}.");
        Assert.Equal(configured, hosted);

        // And with the switch on, the hosted agent is held to exactly the same budget as the local one.
        var levelled = ContextTruncation.EffectiveHistoryBudget(
            configured, Profile(ModelProvider.OpenAI, enforce: true).BindingContextWindow, 1500,
            ContextTruncation.DefaultPromptOverheadTokens);
        Assert.Equal(local, levelled);
    }

    [Fact]
    public void A_length_finish_on_a_hosted_model_is_never_diagnosed_as_a_full_window()
    {
        // WasContextExhausted asks whether input and output together filled the window. Against a window the
        // provider never applied, a request that happens to land near 8,192 tokens would be reported as
        // context exhaustion — and the advice that follows ("send less") is the opposite of the truth when
        // the real window is many times larger and the output budget is what was hit.
        Assert.False(ContextTruncation.WasContextExhausted(
            inputTokens: 8000, outputTokens: 192, Profile(ModelProvider.OpenAI).BindingContextWindow, 1500));

        // The same numbers on Ollama are exactly what exhaustion looks like.
        Assert.True(ContextTruncation.WasContextExhausted(
            inputTokens: 8000, outputTokens: 192, Profile(ModelProvider.Ollama).BindingContextWindow, 1500));
    }

    [Fact]
    public void The_runs_own_artefact_records_which_regime_it_played_under()
    {
        // Otherwise the only record is a console line that scrolls away, and a reader comparing two runs
        // months apart has to reconstruct the regime from the provider name plus a setting kept elsewhere.
        var hosted = TracedAgentProfile.From(Profile(ModelProvider.OpenAI));
        Assert.Equal(8192, hosted.ContextWindow);
        Assert.Null(hosted.BindingContextWindow);

        var local = TracedAgentProfile.From(Profile(ModelProvider.Ollama));
        Assert.Equal(8192, local.BindingContextWindow);

        var levelled = TracedAgentProfile.From(Profile(ModelProvider.OpenAI, enforce: true));
        Assert.Equal(8192, levelled.BindingContextWindow);
    }

    [Fact]
    public void The_harness_default_leaves_hosted_runs_unconstrained()
    {
        // The default is the safe reading of a configured window: apply it where it is real. Turning it on is
        // a deliberate choice about what is being compared, so it must be off unless asked for.
        Assert.False(new HarnessOptions().EnforceContextWindowOnHostedModels);

        var options = new AgentProfileOptions { Provider = "OpenAI", ModelId = "gpt-5.4", ContextWindow = 8192 };
        Assert.Null(AgentModelProfile.FromOptions("Rowan", options).BindingContextWindow);
        Assert.Equal(8192, AgentModelProfile.FromOptions("Rowan", options, true).BindingContextWindow);
    }
}
