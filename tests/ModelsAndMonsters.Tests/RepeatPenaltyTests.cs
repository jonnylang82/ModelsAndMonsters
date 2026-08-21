using ModelsAndMonsters.AI;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The classic llama.cpp/Ollama <c>repeat_penalty</c> (v0.10, added for the Encounter Summariser) — a
/// different knob from <see cref="AgentModelProfile.PresencePenalty"/>/<see cref="AgentModelProfile.FrequencyPenalty"/>,
/// which are OpenAI's shape. There is no <c>ChatOptions</c> field for it, so it travels as a raw Ollama
/// option and follows the same drop-and-report discipline as every other sampling knob: sent where the
/// provider takes it, dropped and recorded everywhere else.
/// </summary>
public sealed class RepeatPenaltyTests
{
    private static AgentModelProfile Profile(ModelProvider provider, float? repeatPenalty) => new()
    {
        AgentName = "EncounterSummariser",
        Provider = provider,
        ModelId = "test-model",
        RepeatPenalty = repeatPenalty
    };

    [Fact]
    public void Ollama_takes_the_repeat_penalty_as_a_raw_provider_option()
    {
        var resolved = ChatOptionsFactory.Create(Profile(ModelProvider.Ollama, 1.15f));

        Assert.DoesNotContain(nameof(AgentModelProfile.RepeatPenalty), resolved.UnsupportedOptionsDropped);
        var additional = resolved.Options.AdditionalProperties;
        Assert.NotNull(additional);
        Assert.Contains(additional!, kv => kv.Value is float f && Math.Abs(f - 1.15f) < 0.0001f);
    }

    [Theory]
    [InlineData(ModelProvider.OpenAI)]
    [InlineData(ModelProvider.Anthropic)]
    public void Providers_with_no_equivalent_drop_it_and_report_the_drop(ModelProvider provider)
    {
        var resolved = ChatOptionsFactory.Create(Profile(provider, 1.15f));

        Assert.Contains(nameof(AgentModelProfile.RepeatPenalty), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void Leaving_it_unset_sends_nothing()
    {
        var resolved = ChatOptionsFactory.Create(Profile(ModelProvider.Ollama, null));

        Assert.DoesNotContain(nameof(AgentModelProfile.RepeatPenalty), resolved.UnsupportedOptionsDropped);
        Assert.Null(resolved.Options.AdditionalProperties);
    }

    [Fact]
    public void It_is_inherited_from_the_defaults_and_overridable_per_agent()
    {
        var defaults = new AgentProfileOptions { Provider = "Ollama", ModelId = "qwen3.5:9b", RepeatPenalty = 1.15f };

        var inherited = AgentModelProfile.FromOptions("Rowan", new AgentProfileOptions().Overlay(defaults));
        Assert.Equal(1.15f, inherited.RepeatPenalty);

        var overridden = AgentModelProfile.FromOptions(
            "EncounterSummariser", new AgentProfileOptions { RepeatPenalty = 1.3f }.Overlay(defaults));
        Assert.Equal(1.3f, overridden.RepeatPenalty);
    }

    [Fact]
    public void The_runs_manifest_records_what_was_configured()
    {
        var traced = TracedAgentProfile.From(Profile(ModelProvider.Ollama, 1.15f));

        Assert.Equal(1.15f, traced.RepeatPenalty);
    }

    [Fact]
    public void The_shipped_configuration_sets_a_creative_writing_repeat_penalty_for_the_summariser()
    {
        var config = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));

        var summariser = config.RootElement
            .GetProperty("ModelsAndMonsters").GetProperty("Agents").GetProperty("EncounterSummariser");

        Assert.True(summariser.TryGetProperty("RepeatPenalty", out var penalty));
        Assert.True(penalty.GetDouble() > 1.0, "A creative-writing repeat penalty should discourage verbatim repetition.");
    }
}

/// <summary>
/// Ollama's <c>repeat_last_n</c> (v0.10) — how far back <see cref="AgentModelProfile.RepeatPenalty"/> looks
/// before it stops discouraging a repeat. The wiring is exercised here (Ollama-only, drop-and-report
/// elsewhere, same as every other raw Ollama option); the Encounter Summariser itself does not set a value.
///
/// <para>
/// It was tried twice, at two different sizes, to reach a ~1000-1500 token verbatim repeat-loop that Ollama's
/// 64-token default missed — and made things worse both times. <see cref="int.MaxValue"/> (standing in for
/// llama.cpp's own "-1 = whole context" sentinel, which a live Ollama server rejects outright with an HTTP
/// 400) penalised reusing anything already in the ~4000-token input transcript — periods, common words,
/// character names — producing one unpunctuated cascade that never closed a sentence. A moderate 2000 still
/// broke, differently: the model exhausted safe English vocabulary within its own widened window and drifted
/// into Chinese, then symbols. Three sizes, three distinct failures — the instability sits in the combination
/// of penalties already stacked together (<see cref="AgentModelProfile.RepeatPenalty"/>,
/// <see cref="AgentModelProfile.PresencePenalty"/>, <see cref="AgentModelProfile.FrequencyPenalty"/>), not in
/// this field's size. See <see cref="AgentModelProfile.RepeatLastN"/> for the full history.
/// </para>
/// </summary>
public sealed class RepeatLastNTests
{
    private static AgentModelProfile Profile(ModelProvider provider, int? repeatLastN) => new()
    {
        AgentName = "EncounterSummariser",
        Provider = provider,
        ModelId = "test-model",
        RepeatLastN = repeatLastN
    };

    [Fact]
    public void Ollama_takes_the_repeat_last_n_as_a_raw_provider_option()
    {
        var resolved = ChatOptionsFactory.Create(Profile(ModelProvider.Ollama, 4096));

        Assert.DoesNotContain(nameof(AgentModelProfile.RepeatLastN), resolved.UnsupportedOptionsDropped);
        var additional = resolved.Options.AdditionalProperties;
        Assert.NotNull(additional);
        Assert.Contains(additional!, kv => kv.Value is int i && i == 4096);
    }

    [Theory]
    [InlineData(ModelProvider.OpenAI)]
    [InlineData(ModelProvider.Anthropic)]
    public void Providers_with_no_equivalent_drop_it_and_report_the_drop(ModelProvider provider)
    {
        var resolved = ChatOptionsFactory.Create(Profile(provider, 4096));

        Assert.Contains(nameof(AgentModelProfile.RepeatLastN), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void Leaving_it_unset_sends_nothing()
    {
        var resolved = ChatOptionsFactory.Create(Profile(ModelProvider.Ollama, null));

        Assert.DoesNotContain(nameof(AgentModelProfile.RepeatLastN), resolved.UnsupportedOptionsDropped);
        Assert.Null(resolved.Options.AdditionalProperties);
    }

    [Fact]
    public void The_runs_manifest_records_what_was_configured()
    {
        var traced = TracedAgentProfile.From(Profile(ModelProvider.Ollama, 4096));

        Assert.Equal(4096, traced.RepeatLastN);
    }

    // Two enlarged sizes were each tried and reverted after a live failure (see the class remarks above), so
    // the shipped configuration leaves this null: Ollama's own 64-token default was only ever proven
    // insufficient for the original repeat-loop, never proven harmful, unlike either enlarged value.
    [Fact]
    public void The_shipped_configuration_leaves_it_unset_after_two_enlarged_values_each_made_things_worse()
    {
        var config = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));

        var summariser = config.RootElement
            .GetProperty("ModelsAndMonsters").GetProperty("Agents").GetProperty("EncounterSummariser");

        Assert.True(summariser.TryGetProperty("RepeatLastN", out var lookback));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, lookback.ValueKind);
    }
}
