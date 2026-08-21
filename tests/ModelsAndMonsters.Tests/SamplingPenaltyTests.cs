using ModelsAndMonsters.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The presence and frequency penalties: that they are configurable, that they reach the providers which
/// accept them, and that they are recorded.
/// </summary>
/// <remarks>
/// <para>
/// These existed before this release — as the MODEL's defaults, applied to every call, set by nobody and
/// written down nowhere. A v0.8 run's Ollama log carried <c>presence_penalty = 1.5</c> on 605 calls out of
/// 605, inherited from Qwen's recommended chat defaults, while the harness believed it was configuring
/// sampling completely.
/// </para>
/// <para>
/// The value matters because this harness wants the opposite of what a chat default optimises for. A
/// presence penalty exists to stop a model repeating itself; tool calling depends on the model repeating
/// itself exactly — tool names, stable ids, item names copied out of the state block, structural
/// punctuation. Qwen's own documentation warns a high value can cause language mixing, and a live run
/// closed a spoken line with <c>】</c> (U+3011) where <c>]</c> belonged.
/// </para>
/// <para>
/// Whether that penalty caused those failures is not settled here. What is settled is that the harness now
/// sets the value rather than inheriting it, and records what it sent — so the question can be answered by
/// changing one number and comparing two runs.
/// </para>
/// </remarks>
public sealed class SamplingPenaltyTests
{
    private static AgentModelProfile Profile(ModelProvider provider, float? presence = 0f, float? frequency = null) => new()
    {
        AgentName = "Rowan",
        Provider = provider,
        ModelId = "test-model",
        PresencePenalty = presence,
        FrequencyPenalty = frequency
    };

    [Theory]
    [InlineData(ModelProvider.Ollama)]
    [InlineData(ModelProvider.OpenAI)]
    public void A_provider_that_accepts_penalties_is_sent_them(ModelProvider provider)
    {
        var resolved = ChatOptionsFactory.Create(Profile(provider, presence: 0f, frequency: 0.2f));

        Assert.Equal(0f, resolved.Options.PresencePenalty);
        Assert.Equal(0.2f, resolved.Options.FrequencyPenalty);
        Assert.DoesNotContain(nameof(AgentModelProfile.PresencePenalty), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void Zero_is_sent_rather_than_treated_as_absent()
    {
        // The whole point. A null presence penalty leaves the model's default in force, so "off" has to be
        // an explicit zero that actually reaches the provider — not an omission that looks like one.
        var resolved = ChatOptionsFactory.Create(Profile(ModelProvider.Ollama, presence: 0f));

        Assert.Equal(0f, resolved.Options.PresencePenalty);
    }

    [Fact]
    public void Leaving_it_unset_sends_nothing_and_the_trace_says_so()
    {
        // Null is honest: the harness did not choose, so whatever the model ships with applies. The trace
        // records null rather than zero, because recording zero would claim a decision nobody made.
        var resolved = ChatOptionsFactory.Create(Profile(ModelProvider.Ollama, presence: null));

        Assert.Null(resolved.Options.PresencePenalty);
        Assert.Null(ChatTraceMapper.MapOptions(resolved.Options).PresencePenalty);
    }

    [Fact]
    public void Anthropic_has_no_equivalent_so_the_penalties_are_dropped_and_reported()
    {
        var resolved = ChatOptionsFactory.Create(Profile(ModelProvider.Anthropic, presence: 0f, frequency: 0.2f));

        Assert.Null(resolved.Options.PresencePenalty);
        Assert.Null(resolved.Options.FrequencyPenalty);
        Assert.Contains(nameof(AgentModelProfile.PresencePenalty), resolved.UnsupportedOptionsDropped);
        Assert.Contains(nameof(AgentModelProfile.FrequencyPenalty), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void A_model_that_forbids_sampling_drops_the_penalties_with_everything_else()
    {
        // They are sampling parameters and follow the same rule as temperature and top-p.
        var profile = Profile(ModelProvider.OpenAI, presence: 0f) with { OmitSampling = true };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Null(resolved.Options.PresencePenalty);
        Assert.Contains(nameof(AgentModelProfile.PresencePenalty), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void The_penalty_is_inherited_from_the_defaults_and_overridable_per_agent()
    {
        var defaults = new AgentProfileOptions { Provider = "Ollama", ModelId = "qwen3.5:9b", PresencePenalty = 0f };

        var inherited = AgentModelProfile.FromOptions("Rowan", new AgentProfileOptions().Overlay(defaults));
        Assert.Equal(0f, inherited.PresencePenalty);

        var overridden = AgentModelProfile.FromOptions(
            "Elara", new AgentProfileOptions { PresencePenalty = 1.5f }.Overlay(defaults));
        Assert.Equal(1.5f, overridden.PresencePenalty);
    }

    [Fact]
    public void The_runs_manifest_records_what_was_configured()
    {
        // So two runs that differ only in this do not produce identical-looking artefacts.
        var traced = TracedAgentProfile.From(Profile(ModelProvider.Ollama, presence: 0f, frequency: 0.1f));

        Assert.Equal(0f, traced.PresencePenalty);
        Assert.Equal(0.1f, traced.FrequencyPenalty);
    }

    [Fact]
    public void The_intent_parser_never_decodes_greedily()
    {
        // Greedy decoding on qwen3.5 is fragile in a way this project measured: a run died at round 5 when
        // the parser 500'd three times on the identical malformed tool-call XML, because a temperature-0
        // draw reproduces itself exactly. A character sampling at 0.8 shook the same fault off in the same
        // trace. Qwen's own guidance never recommends below 0.6 for any task or mode.
        Assert.True(SimulationRunner.IntentParserTemperature > 0f,
            "A greedy parser cannot escape a bad draw; the seed already provides reproducibility.");

        // And it stays below the first transient-retry floor, so attempt 2 raises the temperature rather
        // than re-sending at the same one with only a moved seed.
        Assert.True(SimulationRunner.IntentParserTemperature < 0.4f,
            "The parser temperature must sit below the first retry floor or the escalation ladder flattens.");
    }

    [Fact]
    public void The_shipped_configuration_turns_the_presence_penalty_off()
    {
        // The defaults every agent inherits. If this ever goes back to null, local runs silently pick the
        // model's chat default back up.
        var config = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));

        var defaults = config.RootElement
            .GetProperty("ModelsAndMonsters").GetProperty("Agents").GetProperty("Default");

        Assert.True(defaults.TryGetProperty("PresencePenalty", out var penalty),
            "Agents:Default must set PresencePenalty explicitly; leaving it unset inherits the model's own default.");
        Assert.Equal(0d, penalty.GetDouble());
    }
}
