using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Configuration;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The provider surface, including the v0.11 additions: OpenRouter (an OpenAI-compatible aggregator reached
/// through the chat-completions client) and Ollama Cloud (the Ollama client with a bearer token). These pin
/// the capability shape and the key/endpoint handling — client construction is offline (no network is touched
/// until a call is made), so the missing-key error path and the "a key turns local into cloud" branch are the
/// things worth guarding.
/// </summary>
public sealed class ProviderTests
{
    private static AgentModelProfile Profile(ModelProvider provider, string model = "test/model") => new()
    {
        AgentName = "Rowan",
        Provider = provider,
        ModelId = model
    };

    private static ChatClientFactory FactoryWith(ProvidersOptions providers) =>
        new(Options.Create(new SimulationOptions { Providers = providers }));

    // ------------------------------------------------------------------------------------------
    // Capabilities
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void Every_provider_has_a_capability_profile()
    {
        // ProviderCapabilities.For throws on an unhandled provider, so this catches a new enum value that was
        // added without its arm — the exact mistake OpenRouter could have been.
        foreach (var provider in Enum.GetValues<ModelProvider>())
        {
            _ = ProviderCapabilities.For(provider);
        }
    }

    [Fact]
    public void OpenRouter_mirrors_OpenAI_minus_top_k_and_schema_and_context_window()
    {
        var caps = ProviderCapabilities.For(ModelProvider.OpenRouter);

        Assert.True(caps.SupportsTemperature);
        Assert.True(caps.SupportsTopP);
        Assert.True(caps.SupportsSeed);
        Assert.True(caps.SupportsForcedToolChoice);
        Assert.True(caps.SupportsPenalties);
        Assert.True(caps.AllowsTemperatureAndTopPTogether);

        Assert.False(caps.SupportsTopK);
        Assert.False(caps.SupportsContextWindow);
        Assert.False(caps.SupportsStructuredOutputSchema);
        Assert.False(caps.SilentlyTruncatesHistory);
    }

    // ------------------------------------------------------------------------------------------
    // ChatOptions for OpenRouter (chat completions: sampling normalized, effort model-dependent so dropped)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void OpenRouter_keeps_sampling_but_drops_top_k()
    {
        var profile = Profile(ModelProvider.OpenRouter) with { Temperature = 0.7f, TopP = 0.9f, TopK = 40 };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Equal(0.7f, resolved.Options.Temperature);
        Assert.Equal(0.9f, resolved.Options.TopP);
        Assert.Contains(nameof(AgentModelProfile.TopK), resolved.UnsupportedOptionsDropped);
        Assert.DoesNotContain(nameof(AgentModelProfile.Temperature), resolved.UnsupportedOptionsDropped);
    }

    [Theory]
    [InlineData(ReasoningEffort.None)]
    [InlineData(ReasoningEffort.High)]
    public void OpenRouter_effort_is_applied_client_side_never_as_a_chat_option_or_a_drop(ReasoningEffort effort)
    {
        // Reasoning rides OpenRouter's own `reasoning` object, injected by a client pipeline policy — so
        // ChatOptions carries no reasoning and nothing is reported dropped; the config still takes effect.
        var resolved = ChatOptionsFactory.Create(Profile(ModelProvider.OpenRouter) with { Effort = effort });

        Assert.Null(resolved.Options.Reasoning);
        Assert.DoesNotContain(nameof(AgentModelProfile.Effort), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void The_OpenRouter_reasoning_object_maps_each_effort_to_OpenRouters_vocabulary()
    {
        Assert.Null(ChatClientFactory.BuildOpenRouterReasoning(null));

        // None disables via enabled:false — deliberately NOT effort:"none", which a mandatory model rejects.
        var off = ChatClientFactory.BuildOpenRouterReasoning(ReasoningEffort.None);
        Assert.False(off!["enabled"]!.GetValue<bool>());
        Assert.Null(off["effort"]);

        Assert.Equal("low", Effort(ReasoningEffort.Low));
        Assert.Equal("medium", Effort(ReasoningEffort.Medium));
        Assert.Equal("high", Effort(ReasoningEffort.High));
        Assert.Equal("max", Effort(ReasoningEffort.ExtraHigh));

        static string? Effort(ReasoningEffort e) =>
            ChatClientFactory.BuildOpenRouterReasoning(e)?["effort"]?.GetValue<string>();
    }

    [Fact]
    public void The_reasoning_object_is_injected_into_a_chat_body_and_nothing_else()
    {
        var reasoning = new JsonObject { ["effort"] = "high" };

        // A real chat-completions body gains the reasoning object, keeping everything else.
        var chat = """{"model":"anthropic/claude-sonnet-4.6","messages":[{"role":"user","content":"hi"}]}""";
        Assert.True(ChatClientFactory.TryInjectOpenRouterReasoning(chat, reasoning, out var modified));
        var body = JsonNode.Parse(modified)!.AsObject();
        Assert.Equal("high", body["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("anthropic/claude-sonnet-4.6", body["model"]!.GetValue<string>());
        Assert.NotNull(body["messages"]);

        // A body without messages (not a chat request) is left exactly as it was.
        Assert.False(ChatClientFactory.TryInjectOpenRouterReasoning("""{"foo":1}""", reasoning, out var untouched));
        Assert.Equal("""{"foo":1}""", untouched);

        // Unparseable input is passed through unchanged rather than throwing.
        Assert.False(ChatClientFactory.TryInjectOpenRouterReasoning("not json", reasoning, out var raw));
        Assert.Equal("not json", raw);
    }

    // ------------------------------------------------------------------------------------------
    // ChatClientFactory: key handling (construction is offline, so these do not touch the network)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void OpenRouter_without_a_key_fails_with_a_helpful_message()
    {
        var providers = new ProvidersOptions
        {
            OpenRouter = new OpenRouterProviderOptions { ApiKeyEnvironmentVariable = "OPENROUTER_KEY_ABSENT_FOR_TEST" }
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => FactoryWith(providers).Create(Profile(ModelProvider.OpenRouter)));

        Assert.Contains("OpenRouter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("OPENROUTER_KEY_ABSENT_FOR_TEST", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenRouter_with_a_key_constructs_a_client()
    {
        var providers = new ProvidersOptions { OpenRouter = new OpenRouterProviderOptions { ApiKey = "test-key" } };

        using var client = FactoryWith(providers).Create(Profile(ModelProvider.OpenRouter, "anthropic/claude-sonnet-4.6"));

        Assert.IsAssignableFrom<IChatClient>(client);
    }

    // ------------------------------------------------------------------------------------------
    // Unsloth Studio: an OpenAI-compatible local server reached the same way as OpenRouter, minus
    // OpenRouter's client-side reasoning injection (no per-model reasoning object to inject against).
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void UnslothStudio_mirrors_OpenRouter_capabilities()
    {
        var caps = ProviderCapabilities.For(ModelProvider.UnslothStudio);

        Assert.True(caps.SupportsTemperature);
        Assert.True(caps.SupportsTopP);
        Assert.True(caps.SupportsSeed);
        Assert.True(caps.SupportsForcedToolChoice);
        Assert.True(caps.SupportsPenalties);
        Assert.True(caps.AllowsTemperatureAndTopPTogether);

        Assert.False(caps.SupportsTopK);
        Assert.False(caps.SupportsContextWindow);
        Assert.False(caps.SupportsStructuredOutputSchema);
        Assert.False(caps.SilentlyTruncatesHistory);
    }

    [Fact]
    public void UnslothStudio_keeps_sampling_but_drops_top_k()
    {
        var profile = Profile(ModelProvider.UnslothStudio) with { Temperature = 0.7f, TopP = 0.9f, TopK = 40 };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Equal(0.7f, resolved.Options.Temperature);
        Assert.Equal(0.9f, resolved.Options.TopP);
        Assert.Contains(nameof(AgentModelProfile.TopK), resolved.UnsupportedOptionsDropped);
        Assert.DoesNotContain(nameof(AgentModelProfile.Temperature), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void UnslothStudio_effort_none_is_neither_sent_nor_dropped()
    {
        var resolved = ChatOptionsFactory.Create(
            Profile(ModelProvider.UnslothStudio) with { Effort = ReasoningEffort.None });

        Assert.Null(resolved.Options.Reasoning);
        Assert.DoesNotContain(nameof(AgentModelProfile.Effort), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void UnslothStudio_raised_effort_is_dropped_rather_than_sent()
    {
        // There is no per-model reasoning object to inject the way OpenRouter has, so a raised effort
        // cannot be honoured and must be reported dropped rather than silently sent as a ChatOption that
        // an arbitrary local model may reject.
        var resolved = ChatOptionsFactory.Create(
            Profile(ModelProvider.UnslothStudio) with { Effort = ReasoningEffort.High });

        Assert.Null(resolved.Options.Reasoning);
        Assert.Contains(nameof(AgentModelProfile.Effort), resolved.UnsupportedOptionsDropped);
    }

    [Fact]
    public void UnslothStudio_without_a_key_fails_with_a_helpful_message()
    {
        var providers = new ProvidersOptions
        {
            UnslothStudio = new UnslothStudioProviderOptions { ApiKeyEnvironmentVariable = "UNSLOTH_KEY_ABSENT_FOR_TEST" }
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => FactoryWith(providers).Create(Profile(ModelProvider.UnslothStudio)));

        Assert.Contains("UnslothStudio", ex.Message, StringComparison.Ordinal);
        Assert.Contains("UNSLOTH_KEY_ABSENT_FOR_TEST", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnslothStudio_with_a_key_constructs_a_client()
    {
        var providers = new ProvidersOptions
        {
            UnslothStudio = new UnslothStudioProviderOptions { ApiKey = "test-key" }
        };

        using var client = FactoryWith(providers).Create(Profile(ModelProvider.UnslothStudio, "some-local-model"));

        Assert.IsAssignableFrom<IChatClient>(client);
    }

    [Fact]
    public void A_local_Ollama_needs_no_key_and_a_cloud_key_turns_it_into_a_cloud_client()
    {
        // Local: no key, plain endpoint — the pre-v0.11 behaviour, unchanged.
        using var local = FactoryWith(new ProvidersOptions()).Create(Profile(ModelProvider.Ollama, "qwen3.5:9b"));
        Assert.IsAssignableFrom<IChatClient>(local);

        // Cloud: a key is present, so the client is built over an HttpClient carrying the bearer token, pointed
        // at the cloud endpoint. Construction is offline, so this just proves the branch builds a client.
        var cloud = new ProvidersOptions
        {
            Ollama = new OllamaProviderOptions { Endpoint = "https://ollama.com", ApiKey = "test-key" }
        };
        using var cloudClient = FactoryWith(cloud).Create(Profile(ModelProvider.Ollama, "gpt-oss:120b"));
        Assert.IsAssignableFrom<IChatClient>(cloudClient);
    }
}
