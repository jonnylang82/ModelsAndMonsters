using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Prompts;
using ThinkValue = OllamaSharp.Models.Chat.ThinkValue;

namespace ModelsAndMonsters.Tests;

public sealed class ModelConfigurationTests
{
    [Fact]
    public void Every_agent_can_be_configured_independently()
    {
        var agents = new AgentsOptions
        {
            DungeonMaster = new AgentProfileOptions { Provider = "Ollama", ModelId = "llama3.1", Temperature = 0.3f },
            Hero = new AgentProfileOptions { Provider = "Ollama", ModelId = "granite4.1:8b", Temperature = 0.9f, TopK = 40 },
            Monster = new AgentProfileOptions { Provider = "OpenAI", ModelId = "gpt-4.1-mini" }
        };

        var dungeonMaster = AgentModelProfile.FromOptions("DungeonMaster", agents.DungeonMaster);
        var hero = AgentModelProfile.FromOptions("Aric", agents.Hero);
        var monster = AgentModelProfile.FromOptions("Grik", agents.Monster);

        Assert.Equal(ModelProvider.Ollama, dungeonMaster.Provider);
        Assert.Equal(0.3f, dungeonMaster.Temperature);
        Assert.Equal("granite4.1:8b", hero.ModelId);
        Assert.Equal(40, hero.TopK);
        Assert.Equal(ModelProvider.OpenAI, monster.Provider);

        // The sampling seed is not read from per-agent config; the runner derives it from the master.
        Assert.Null(monster.Seed);
    }

    [Fact]
    public void An_unknown_provider_fails_loudly_at_configuration_time()
    {
        var options = new AgentProfileOptions { Provider = "Cohere", ModelId = "x" };

        var exception = Assert.Throws<InvalidOperationException>(() => AgentModelProfile.FromOptions("Hero", options));
        Assert.Contains("unknown provider", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("claude-opus-4-8")]  // Opus 4.7+
    [InlineData("claude-opus-4-7")]
    [InlineData("claude-opus-5")]    // Claude 5 family
    [InlineData("claude-sonnet-5")]
    public void Anthropic_no_sampling_models_drop_every_sampling_parameter(string modelId)
    {
        // These forbid temperature, top_p and top_k; all three are dropped and reported, and max_tokens
        // (which the API still requires) is sent.
        var profile = new AgentModelProfile
        {
            AgentName = "Rowan",
            Provider = ModelProvider.Anthropic,
            ModelId = modelId,
            Temperature = 0.8f,
            TopP = 0.95f,
            TopK = 40,
            MaxOutputTokens = 500
        };

        var resolved = ChatOptionsFactory.Create(profile, CharacterTools.All);

        Assert.Null(resolved.Options.Temperature);
        Assert.Null(resolved.Options.TopP);
        Assert.Null(resolved.Options.TopK);
        Assert.Contains(nameof(AgentModelProfile.Temperature), resolved.UnsupportedOptionsDropped);
        Assert.Contains(nameof(AgentModelProfile.TopP), resolved.UnsupportedOptionsDropped);
        Assert.Contains(nameof(AgentModelProfile.TopK), resolved.UnsupportedOptionsDropped);
        Assert.Equal(500, resolved.Options.MaxOutputTokens);
    }

    [Theory]
    [InlineData("claude-haiku-4-5")]  // major 4, minor 5 — not a Claude 5 model
    [InlineData("claude-sonnet-4-5")]
    public void Anthropic_4x_models_keep_their_sampling_parameters(string modelId)
    {
        var profile = new AgentModelProfile
        {
            AgentName = "Rowan",
            Provider = ModelProvider.Anthropic,
            ModelId = modelId,
            Temperature = 0.8f,
            TopK = 40
        };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Equal(0.8f, resolved.Options.Temperature);
        Assert.Equal(40, resolved.Options.TopK);
    }

    [Fact]
    public void OmitSampling_can_be_forced_either_way_overriding_the_model_default()
    {
        // Forced off for a model that would otherwise drop them.
        var forcedOn = ChatOptionsFactory.Create(new AgentModelProfile
        {
            AgentName = "Rowan", Provider = ModelProvider.Anthropic, ModelId = "claude-opus-4-8",
            Temperature = 0.5f, OmitSampling = false
        });
        Assert.Equal(0.5f, forcedOn.Options.Temperature);

        // Forced on for a model that would otherwise keep them.
        var forcedOff = ChatOptionsFactory.Create(new AgentModelProfile
        {
            AgentName = "Rowan", Provider = ModelProvider.Ollama, ModelId = "llama3.1",
            Temperature = 0.5f, OmitSampling = true
        });
        Assert.Null(forcedOff.Options.Temperature);
        Assert.Contains(nameof(AgentModelProfile.Temperature), forcedOff.UnsupportedOptionsDropped);
    }

    [Fact]
    public void Anthropic_keeps_temperature_and_drops_top_p_when_both_are_set()
    {
        var profile = new AgentModelProfile
        {
            AgentName = "Rowan",
            Provider = ModelProvider.Anthropic,
            ModelId = "claude-haiku-4-5",
            Temperature = 0.8f,
            TopP = 0.95f
        };

        var resolved = ChatOptionsFactory.Create(profile);

        // Anthropic rejects both together; temperature is kept, top_p is dropped and reported.
        Assert.Equal(0.8f, resolved.Options.Temperature);
        Assert.Null(resolved.Options.TopP);
        Assert.Contains(nameof(AgentModelProfile.TopP), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void Other_providers_keep_both_temperature_and_top_p()
    {
        foreach (var provider in new[] { ModelProvider.Ollama, ModelProvider.OpenAI })
        {
            var resolved = ChatOptionsFactory.Create(new AgentModelProfile
            {
                AgentName = "Rowan",
                Provider = provider,
                ModelId = "m",
                Temperature = 0.8f,
                TopP = 0.95f
            });

            Assert.Equal(0.8f, resolved.Options.Temperature);
            Assert.Equal(0.95f, resolved.Options.TopP);
            Assert.DoesNotContain(nameof(AgentModelProfile.TopP), resolved.UnsupportedOptionsDropped);
        }
    }

    [Fact]
    public void Anthropic_is_a_recognised_provider_and_its_capabilities_are_applied()
    {
        // A 4.x model that still honours sampling (claude-5 models forbid it). Seed is derived by the
        // runner, not read from options, so set it on the record to test the drop.
        var profile = AgentModelProfile.FromOptions("Elara",
            new AgentProfileOptions
            {
                Provider = "Anthropic",
                ModelId = "claude-sonnet-4-5",
                Temperature = 0.8f,
                TopK = 40,
                MaxOutputTokens = 500,
                ContextWindow = 16384,
                Thinking = false,
                ForceToolChoice = true
            }) with { Seed = 7 };

        Assert.Equal(ModelProvider.Anthropic, profile.Provider);

        var resolved = ChatOptionsFactory.Create(profile, CharacterTools.All);

        // Anthropic honours temperature, top_k and a forced tool choice...
        Assert.Equal(0.8f, resolved.Options.Temperature);
        Assert.Equal(40, resolved.Options.TopK);
        Assert.Equal(500, resolved.Options.MaxOutputTokens);
        Assert.IsType<RequiredChatToolMode>(resolved.Options.ToolMode);

        // ...but not a request seed, a context window, or a simple thinking toggle, which are dropped.
        Assert.Null(resolved.Options.Seed);
        Assert.Contains(nameof(AgentModelProfile.Seed), resolved.UnsupportedOptionsDropped);
        Assert.Contains(nameof(AgentModelProfile.ContextWindow), resolved.UnsupportedOptionsDropped);
        Assert.Contains(nameof(AgentModelProfile.Thinking), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void Sampling_options_a_provider_cannot_honour_are_dropped_and_reported()
    {
        var profile = new AgentModelProfile
        {
            AgentName = "Grik",
            Provider = ModelProvider.OpenAI,
            ModelId = "gpt-4.1-mini",
            Temperature = 0.7f,
            TopK = 40,
            Seed = 5
        };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Null(resolved.Options.TopK);
        Assert.Equal(0.7f, resolved.Options.Temperature);
        Assert.Equal(5, resolved.Options.Seed);
        Assert.Equal(nameof(AgentModelProfile.TopK), Assert.Single(resolved.UnsupportedOptionsDropped));
    }

    [Fact]
    public void Ollama_receives_the_context_window_as_a_native_option()
    {
        var profile = new AgentModelProfile
        {
            AgentName = "Aric",
            Provider = ModelProvider.Ollama,
            ModelId = "llama3.1",
            ContextWindow = 16384
        };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Empty(resolved.UnsupportedOptionsDropped);
        Assert.NotNull(resolved.Options.AdditionalProperties);
        Assert.Contains(
            resolved.Options.AdditionalProperties!,
            p => p.Value is int and 16384 || Equals(p.Value, 16384));
    }

    [Fact]
    public void OpenAI_cannot_be_told_a_context_window_and_the_drop_is_reported()
    {
        var profile = new AgentModelProfile
        {
            AgentName = "Grik",
            Provider = ModelProvider.OpenAI,
            ModelId = "gpt-4.1-mini",
            ContextWindow = 16384
        };

        var resolved = ChatOptionsFactory.Create(profile);

        // The window is fixed per model there, so the request carries nothing; the profile value still
        // survives in the trace so saturation can be judged against it.
        Assert.Equal(nameof(AgentModelProfile.ContextWindow), Assert.Single(resolved.UnsupportedOptionsDropped));
    }

    [Fact]
    public void Ollama_receives_the_thinking_toggle_as_a_native_option()
    {
        var profile = new AgentModelProfile
        {
            AgentName = "DungeonMaster",
            Provider = ModelProvider.Ollama,
            ModelId = "qwen3.5:9b",
            Thinking = false
        };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Empty(resolved.UnsupportedOptionsDropped);
        Assert.NotNull(resolved.Options.AdditionalProperties);
        // The think flag rides in the Ollama-specific additional properties.
        Assert.NotEmpty(resolved.Options.AdditionalProperties!);
    }

    [Fact]
    public void OpenAI_cannot_be_told_a_thinking_flag_and_the_drop_is_reported()
    {
        var profile = new AgentModelProfile
        {
            AgentName = "Grik",
            Provider = ModelProvider.OpenAI,
            ModelId = "gpt-4.1-mini",
            Thinking = false
        };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Contains(nameof(AgentModelProfile.Thinking), resolved.UnsupportedOptionsDropped);
    }

    [Theory]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.High)]
    [InlineData(ReasoningEffort.None)]
    public void Ollama_receives_reasoning_effort_as_a_native_think_level(ReasoningEffort effort)
    {
        var profile = new AgentModelProfile
        {
            AgentName = "DungeonMaster",
            Provider = ModelProvider.Ollama,
            ModelId = "qwen3.5:9b",
            Effort = effort
        };

        var resolved = ChatOptionsFactory.Create(profile);

        // Ollama takes reasoning as its native think flag, so the effort rides in the Ollama-specific
        // additional properties as a ThinkValue — and, being universally honoured, is never dropped.
        Assert.Empty(resolved.UnsupportedOptionsDropped);
        Assert.Null(resolved.Options.Reasoning);
        Assert.NotNull(resolved.Options.AdditionalProperties);
        Assert.Contains(resolved.Options.AdditionalProperties!, p => p.Value is ThinkValue);
    }

    [Theory]
    [InlineData(ModelProvider.OpenAI)]
    [InlineData(ModelProvider.Anthropic)]
    public void Hosted_providers_receive_reasoning_effort_through_chat_options(ModelProvider provider)
    {
        var profile = new AgentModelProfile
        {
            AgentName = "Rowan",
            Provider = provider,
            // A 4.x Anthropic model: its adapter emits thinking.type.enabled, which these accept (the
            // modern Claude models do not — see the modern-Claude test below).
            ModelId = provider == ModelProvider.OpenAI ? "gpt-5.1" : "claude-haiku-4-5",
            MaxOutputTokens = 500,
            Effort = ReasoningEffort.High
        };

        var resolved = ChatOptionsFactory.Create(profile);

        // OpenAI and Anthropic reach reasoning through the standard ChatOptions.Reasoning, which their
        // Microsoft.Extensions.AI adapters translate — no native think flag, and nothing dropped.
        Assert.NotNull(resolved.Options.Reasoning);
        Assert.Equal(ReasoningEffort.High, resolved.Options.Reasoning!.Effort);
        Assert.DoesNotContain(nameof(AgentModelProfile.Effort), resolved.UnsupportedOptionsDropped);
        Assert.DoesNotContain(nameof(AgentModelProfile.Thinking), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void Effort_supersedes_the_legacy_thinking_toggle()
    {
        // Both set: the unified effort wins, so the legacy toggle is neither applied nor reported dropped.
        var profile = new AgentModelProfile
        {
            AgentName = "Grik",
            Provider = ModelProvider.OpenAI,
            ModelId = "gpt-5.1",
            Thinking = true,
            Effort = ReasoningEffort.None
        };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.NotNull(resolved.Options.Reasoning);
        Assert.Equal(ReasoningEffort.None, resolved.Options.Reasoning!.Effort);
        Assert.DoesNotContain(nameof(AgentModelProfile.Thinking), resolved.UnsupportedOptionsDropped);
    }

    [Theory]
    [InlineData("none", ReasoningEffort.None)]
    [InlineData("off", ReasoningEffort.None)]
    [InlineData("low", ReasoningEffort.Low)]
    [InlineData("Medium", ReasoningEffort.Medium)]
    [InlineData("HIGH", ReasoningEffort.High)]
    [InlineData("max", ReasoningEffort.ExtraHigh)]
    [InlineData("xhigh", ReasoningEffort.ExtraHigh)]
    public void Effort_config_string_parses_to_the_matching_level(string configured, ReasoningEffort expected)
    {
        var profile = AgentModelProfile.FromOptions("Elara",
            new AgentProfileOptions { Provider = "Anthropic", ModelId = "claude-opus-4-8", Effort = configured });

        Assert.Equal(expected, profile.Effort);
    }

    [Fact]
    public void A_blank_effort_leaves_the_model_default()
    {
        var profile = AgentModelProfile.FromOptions("Elara",
            new AgentProfileOptions { Provider = "Ollama", ModelId = "qwen3.5:9b", Effort = "  " });

        Assert.Null(profile.Effort);
    }

    [Fact]
    public void An_unknown_effort_string_is_rejected()
    {
        var options = new AgentProfileOptions { Provider = "Ollama", ModelId = "qwen3.5:9b", Effort = "ludicrous" };

        var ex = Assert.Throws<InvalidOperationException>(() => AgentModelProfile.FromOptions("Elara", options));
        Assert.Contains("ludicrous", ex.Message);
    }

    [Fact]
    public void Effort_is_inherited_from_the_defaults_and_can_be_overridden()
    {
        var defaults = new AgentProfileOptions { Provider = "Anthropic", ModelId = "claude-opus-4-8", Effort = "high" };

        // An empty character entry inherits the default effort...
        var inherited = new AgentProfileOptions().Overlay(defaults);
        Assert.Equal("high", inherited.Effort);
        Assert.Equal(ReasoningEffort.High, AgentModelProfile.FromOptions("Rowan", inherited).Effort);

        // ...while a character that sets its own effort keeps it.
        var overridden = new AgentProfileOptions { Effort = "low" }.Overlay(defaults);
        Assert.Equal(ReasoningEffort.Low, AgentModelProfile.FromOptions("Elara", overridden).Effort);
    }

    [Fact]
    public void Enabling_anthropic_thinking_omits_sampling_that_the_model_would_otherwise_accept()
    {
        // haiku-4-5 normally accepts temperature, but Anthropic forbids custom sampling while thinking is
        // on (temperature may only be 1). So a non-None effort must drop it rather than 400 on the call.
        var profile = new AgentModelProfile
        {
            AgentName = "DungeonMaster",
            Provider = ModelProvider.Anthropic,
            ModelId = "claude-haiku-4-5",
            Temperature = 0.2f,
            TopK = 40,
            MaxOutputTokens = 700,
            Effort = ReasoningEffort.Medium
        };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Null(resolved.Options.Temperature);
        Assert.Null(resolved.Options.TopK);
        Assert.Contains(nameof(AgentModelProfile.Temperature), resolved.UnsupportedOptionsDropped);
        Assert.NotNull(resolved.Options.Reasoning);
        Assert.Equal(ReasoningEffort.Medium, resolved.Options.Reasoning!.Effort);
    }

    [Fact]
    public void Anthropic_thinking_off_still_lets_a_4x_model_keep_its_sampling()
    {
        // The mirror of the above: with reasoning off (Effort None), haiku keeps temperature — this is
        // what made the None run succeed where a Medium run first 400'd.
        var profile = new AgentModelProfile
        {
            AgentName = "DungeonMaster",
            Provider = ModelProvider.Anthropic,
            ModelId = "claude-haiku-4-5",
            Temperature = 0.2f,
            MaxOutputTokens = 700,
            Effort = ReasoningEffort.None
        };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Equal(0.2f, resolved.Options.Temperature);
        Assert.DoesNotContain(nameof(AgentModelProfile.Temperature), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void Enabling_anthropic_thinking_drops_a_forced_tool_choice()
    {
        // Extended thinking is incompatible with forced tool use on Anthropic, so the forced choice is
        // dropped (and reported) and the request falls back to Auto rather than 400ing. Uses a 4.x model,
        // where the effort actually turns thinking on.
        var profile = new AgentModelProfile
        {
            AgentName = "Rowan",
            Provider = ModelProvider.Anthropic,
            ModelId = "claude-sonnet-4-5",
            MaxOutputTokens = 1500,
            ForceToolChoice = true,
            Effort = ReasoningEffort.High
        };

        var resolved = ChatOptionsFactory.Create(profile, CharacterTools.All);

        Assert.Contains(nameof(AgentModelProfile.ForceToolChoice), resolved.UnsupportedOptionsDropped);
        Assert.IsType<AutoChatToolMode>(resolved.Options.ToolMode);
    }

    [Fact]
    public void Modern_claude_cannot_express_a_raised_effort_so_it_is_dropped_not_sent()
    {
        // The M.E.AI adapter would emit thinking.type.enabled, which Claude 5 rejects (it needs
        // thinking.type.adaptive + output_config.effort). Rather than 400 the call, a raised effort is
        // dropped and reported — and, because thinking never turns on, a forced tool choice is still fine.
        var profile = new AgentModelProfile
        {
            AgentName = "Rowan",
            Provider = ModelProvider.Anthropic,
            ModelId = "claude-sonnet-5",
            MaxOutputTokens = 1500,
            ForceToolChoice = true,
            Effort = ReasoningEffort.Medium
        };

        var resolved = ChatOptionsFactory.Create(profile, CharacterTools.All);

        Assert.Null(resolved.Options.Reasoning);
        Assert.Contains(nameof(AgentModelProfile.Effort), resolved.UnsupportedOptionsDropped);
        // Thinking stayed off, so the forced tool choice is honoured (not dropped).
        Assert.IsType<RequiredChatToolMode>(resolved.Options.ToolMode);
        Assert.DoesNotContain(nameof(AgentModelProfile.ForceToolChoice), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void OpenAI_non_reasoning_model_omits_effort_none_rather_than_sending_reasoning_effort()
    {
        // gpt-4o-mini rejects the reasoning_effort argument outright, so None (no reasoning wanted anyway)
        // must send nothing — not reasoning_effort=minimal — or the call 400s. Nothing is dropped because
        // a non-reasoning model already satisfies "no reasoning".
        var profile = new AgentModelProfile
        {
            AgentName = "DungeonMaster",
            Provider = ModelProvider.OpenAI,
            ModelId = "gpt-4o-mini",
            Effort = ReasoningEffort.None
        };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Null(resolved.Options.Reasoning);
        Assert.DoesNotContain(nameof(AgentModelProfile.Effort), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void OpenAI_non_reasoning_model_drops_a_raised_effort_it_cannot_honour()
    {
        var profile = new AgentModelProfile
        {
            AgentName = "DungeonMaster",
            Provider = ModelProvider.OpenAI,
            ModelId = "gpt-4o-mini",
            Effort = ReasoningEffort.Medium
        };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Null(resolved.Options.Reasoning);
        Assert.Contains(nameof(AgentModelProfile.Effort), resolved.UnsupportedOptionsDropped);
    }

    [Theory]
    [InlineData("gpt-5.6-sol")]
    [InlineData("gpt-5")]
    [InlineData("o3-mini")]
    public void OpenAI_reasoning_models_receive_the_effort(string modelId)
    {
        var profile = new AgentModelProfile
        {
            AgentName = "Rowan",
            Provider = ModelProvider.OpenAI,
            ModelId = modelId,
            Effort = ReasoningEffort.Low
        };

        // No tools on this call, so effort is honoured (see the with-tools case below).
        var resolved = ChatOptionsFactory.Create(profile);

        Assert.NotNull(resolved.Options.Reasoning);
        Assert.Equal(ReasoningEffort.Low, resolved.Options.Reasoning!.Effort);
        Assert.DoesNotContain(nameof(AgentModelProfile.Effort), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void OpenAI_reasoning_model_keeps_its_effort_even_with_tools_on_the_responses_api()
    {
        // The client uses the Responses API, which allows reasoning effort alongside function tools
        // (chat completions did not). So a reasoning model keeps its effort even when tools are offered.
        var profile = new AgentModelProfile
        {
            AgentName = "Rowan",
            Provider = ModelProvider.OpenAI,
            ModelId = "gpt-5.6-sol",
            Effort = ReasoningEffort.Low
        };

        var resolved = ChatOptionsFactory.Create(profile, CharacterTools.All);

        Assert.NotNull(resolved.Options.Reasoning);
        Assert.Equal(ReasoningEffort.Low, resolved.Options.Reasoning!.Effort);
        Assert.DoesNotContain(nameof(AgentModelProfile.Effort), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void OpenAI_reasoning_model_drops_sampling_once_effort_is_engaged()
    {
        // With reasoning active (effort above None), gpt-5-class models reject temperature/top_p — the
        // same shape as Anthropic thinking — so they are dropped rather than sent to a 400.
        var profile = new AgentModelProfile
        {
            AgentName = "Rowan",
            Provider = ModelProvider.OpenAI,
            ModelId = "gpt-5.6-sol",
            Temperature = 0.8f,
            TopP = 0.95f,
            Effort = ReasoningEffort.Medium
        };

        var resolved = ChatOptionsFactory.Create(profile, CharacterTools.All);

        Assert.Null(resolved.Options.Temperature);
        Assert.Null(resolved.Options.TopP);
        Assert.Contains(nameof(AgentModelProfile.Temperature), resolved.UnsupportedOptionsDropped);
        Assert.NotNull(resolved.Options.Reasoning);
        Assert.Equal(ReasoningEffort.Medium, resolved.Options.Reasoning!.Effort);
    }

    [Fact]
    public void OpenAI_reasoning_model_keeps_sampling_at_effort_none()
    {
        // At None reasoning is not engaged, so temperature is still accepted — the mirror of the above,
        // and what let the Effort:none Responses run through.
        var profile = new AgentModelProfile
        {
            AgentName = "Rowan",
            Provider = ModelProvider.OpenAI,
            ModelId = "gpt-5.6-sol",
            Temperature = 0.8f,
            Effort = ReasoningEffort.None
        };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Equal(0.8f, resolved.Options.Temperature);
        Assert.DoesNotContain(nameof(AgentModelProfile.Temperature), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void Modern_claude_effort_none_is_still_applied_not_dropped()
    {
        // None means "thinking off", which the adapter expresses in a form Claude 5 accepts (the verified
        // Effort:none run proved it), so it is applied rather than dropped.
        var profile = new AgentModelProfile
        {
            AgentName = "Rowan",
            Provider = ModelProvider.Anthropic,
            ModelId = "claude-sonnet-5",
            MaxOutputTokens = 1500,
            Effort = ReasoningEffort.None
        };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.NotNull(resolved.Options.Reasoning);
        Assert.Equal(ReasoningEffort.None, resolved.Options.Reasoning!.Effort);
        Assert.DoesNotContain(nameof(AgentModelProfile.Effort), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void A_forced_tool_choice_is_applied_on_a_provider_that_honours_it()
    {
        // The OpenAI chat-completions API (including Ollama's /v1 endpoint) honours a forced tool choice.
        var profile = new AgentModelProfile
        {
            AgentName = "Rowan",
            Provider = ModelProvider.OpenAI,
            ModelId = "gpt-4.1-mini",
            ForceToolChoice = true
        };

        var resolved = ChatOptionsFactory.Create(profile, CharacterTools.All);

        Assert.IsType<RequiredChatToolMode>(resolved.Options.ToolMode);
        Assert.DoesNotContain(nameof(AgentModelProfile.ForceToolChoice), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void A_forced_tool_choice_is_dropped_and_reported_on_native_ollama()
    {
        // Ollama's native /api/chat ignores tool_choice, so the request stays in Auto and the drop is
        // recorded rather than a forced choice being silently sent and ignored.
        var profile = new AgentModelProfile
        {
            AgentName = "Rowan",
            Provider = ModelProvider.Ollama,
            ModelId = "gemma4:e2b",
            ForceToolChoice = true
        };

        var resolved = ChatOptionsFactory.Create(profile, CharacterTools.All);

        Assert.IsType<AutoChatToolMode>(resolved.Options.ToolMode);
        Assert.Contains(nameof(AgentModelProfile.ForceToolChoice), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void Without_a_forced_tool_choice_the_mode_stays_auto()
    {
        var profile = new AgentModelProfile
        {
            AgentName = "Rowan",
            Provider = ModelProvider.OpenAI,
            ModelId = "gpt-4.1-mini"
        };

        var resolved = ChatOptionsFactory.Create(profile, CharacterTools.All);

        Assert.IsType<AutoChatToolMode>(resolved.Options.ToolMode);
        Assert.DoesNotContain(nameof(AgentModelProfile.ForceToolChoice), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void Ollama_honours_every_supported_sampling_option()
    {
        var profile = new AgentModelProfile
        {
            AgentName = "Aric",
            Provider = ModelProvider.Ollama,
            ModelId = "llama3.1",
            Temperature = 0.8f,
            TopP = 0.95f,
            TopK = 40,
            MaxOutputTokens = 500,
            Seed = 11
        };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Empty(resolved.UnsupportedOptionsDropped);
        Assert.Equal(40, resolved.Options.TopK);
        Assert.Equal(500, resolved.Options.MaxOutputTokens);
    }

    [Fact]
    public void Character_and_dungeon_master_tools_are_declarations_with_no_implementation_to_invoke()
    {
        var allTools = CharacterTools.All.Concat(DungeonMasterTools.All).ToList();

        foreach (var tool in allTools)
        {
            Assert.IsAssignableFrom<AIFunctionDeclaration>(tool);

            // AIFunction is the invocable subtype. These deliberately are not one, so no middleware
            // could execute them even if it were introduced by mistake.
            Assert.IsNotAssignableFrom<AIFunction>(tool);
        }

        // Characters: ask_dm, take_action, say, end_turn (4). Dungeon Master: attack_character, use_item,
        // open_container, take_item, inspect_object, open_exit, escape_encounter, offer_surrender,
        // accept_surrender, use_ability, defend, give_item, drop_item, steal_item, intimidate_character,
        // steady_ally, take_cover, leave_cover, damage_environmental_object, reject_action (20).
        Assert.Equal(24, allTools.Count);
    }

    [Fact]
    public void Prompt_templates_are_versioned_by_content()
    {
        var library = PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));

        // The Dungeon Master prompt is modular: a shared core plus a rules block per job.
        Assert.Contains("dungeon-master.core", library.Versions.Keys);
        Assert.Contains("dungeon-master.rules-adjudicate", library.Versions.Keys);
        Assert.Contains("dungeon-master.rules-narrate", library.Versions.Keys);
        Assert.Contains("dungeon-master.rules-answer", library.Versions.Keys);
        Assert.Contains("character.system", library.Versions.Keys);
        Assert.All(library.Versions.Values, v => Assert.StartsWith("sha256:", v, StringComparison.Ordinal));

        var same = new PromptTemplate("t", "hello {{name}}");
        var different = new PromptTemplate("t", "hello {{name}}!");
        Assert.Equal(same.Version, new PromptTemplate("t", "hello {{name}}").Version);
        Assert.NotEqual(same.Version, different.Version);
        Assert.Equal("hello Aric", same.Render(new Dictionary<string, string?> { ["name"] = "Aric" }));
    }

    [Fact]
    public void Character_system_prompts_carry_the_character_definition_and_nothing_mechanical()
    {
        var library = PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));
        var factory = new CharacterPromptFactory(library);
        var definition = TestWorld.Scenario().Characters[0];

        var prompt = factory.CreateSystemPrompt(definition);

        Assert.Contains("You are Aric", prompt, StringComparison.Ordinal);
        Assert.Contains("Brave and direct.", prompt, StringComparison.Ordinal);
        Assert.Contains("ask_dm", prompt, StringComparison.Ordinal);
        Assert.Contains("take_action", prompt, StringComparison.Ordinal);

        // The character must not learn the engine's vocabulary.
        Assert.DoesNotContain("attack_character", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("use_item", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_character_system_prompt_names_both_its_allies_and_its_enemies()
    {
        var library = PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));
        var factory = new CharacterPromptFactory(library);
        var roster = TestWorld.TwoVsTwoScenario().Characters; // minimal personas that never name other characters

        var skrit = factory.CreateSystemPrompt(roster.First(c => c.Name == "Skrit"), roster);
        // Skrit's ally Vark is named, with an explicit warning not to strike him...
        Assert.Contains("Vark", skrit, StringComparison.Ordinal);
        Assert.Contains("fights at your side", skrit, StringComparison.Ordinal);
        Assert.Contains("Never raise a weapon against", skrit, StringComparison.Ordinal);
        // ...and his enemies Rowan and Elara are named too, with a warning not to give them aid — so a
        // weaker model does not drift into treating an enemy as a companion.
        Assert.Contains("Rowan", skrit, StringComparison.Ordinal);
        Assert.Contains("Elara", skrit, StringComparison.Ordinal);
        Assert.Contains("are your enemies", skrit, StringComparison.Ordinal);
        Assert.Contains("offer them no aid", skrit, StringComparison.Ordinal);

        // Rowan sees the mirror image: Elara at his side, Vark and Skrit named as his enemies.
        var rowan = factory.CreateSystemPrompt(roster.First(c => c.Name == "Rowan"), roster);
        Assert.Contains("Elara", rowan, StringComparison.Ordinal);
        Assert.Contains("Vark", rowan, StringComparison.Ordinal);
        Assert.Contains("Skrit", rowan, StringComparison.Ordinal);
        Assert.Contains("are your enemies", rowan, StringComparison.Ordinal);

        // With no roster (the v0.1 path), a character is simply told it stands alone.
        var solo = factory.CreateSystemPrompt(roster[0]);
        Assert.Contains("no companions", solo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_anthropic_caching_client_marks_the_system_prompt_but_not_the_turn()
    {
        var inner = new ScriptedChatClient(ScriptedChatClient.Text("ok"));
        using var client = new AnthropicPromptCachingChatClient(inner);

        var system = new ChatMessage(ChatRole.System, "You are Rowan, a person living inside a fantasy world.");
        var turn = new ChatMessage(ChatRole.User, "It is your turn.");

        await client.GetResponseAsync([system, turn]);

        // The stable system prompt carries a cache breakpoint; the per-turn message does not.
        Assert.True(AnthropicPromptCachingChatClient.HasCacheControl(system.Contents[0]));
        Assert.False(AnthropicPromptCachingChatClient.HasCacheControl(turn.Contents[0]));
    }

    [Fact]
    public void Tool_arguments_are_read_tolerantly_across_casing_and_naming_differences()
    {
        var exact = ScriptedChatClient.CallContent("1", "take_action", ("intent", "I strike."));
        var wrongCase = ScriptedChatClient.CallContent("2", "take_action", ("Intent", "I strike."));
        var wrongName = ScriptedChatClient.CallContent("3", "take_action", ("action", "I strike."));
        var ambiguous = ScriptedChatClient.CallContent("4", "take_action", ("a", "one"), ("b", "two"));

        Assert.Equal("I strike.", ToolArguments.GetString(exact, "intent"));
        Assert.Equal("I strike.", ToolArguments.GetString(wrongCase, "intent"));
        Assert.Equal("I strike.", ToolArguments.GetString(wrongName, "intent"));
        Assert.Null(ToolArguments.GetString(ambiguous, "intent"));
    }
}
