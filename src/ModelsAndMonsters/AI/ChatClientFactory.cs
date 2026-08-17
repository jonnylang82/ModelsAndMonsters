using System.ClientModel;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelsAndMonsters.Configuration;
using OllamaSharp;
using OpenAI;

namespace ModelsAndMonsters.AI;

/// <summary>
/// Constructs provider-specific chat clients and hands back the shared <see cref="IChatClient"/>
/// abstraction. Provider SDK types never escape this class.
/// </summary>
/// <remarks>
/// Each call returns a fresh client owned by the caller, so agents sharing a model never share a
/// client instance and disposal is unambiguous.
/// </remarks>
public sealed class ChatClientFactory : IChatClientFactory
{
    private readonly ProvidersOptions _providers;

    public ChatClientFactory(IOptions<SimulationOptions> options)
    {
        _providers = options.Value.Providers;
    }

    public IChatClient Create(AgentModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.Provider switch
        {
            ModelProvider.Ollama => CreateOllamaClient(profile),
            ModelProvider.OpenAI => CreateOpenAIClient(profile),
            ModelProvider.Anthropic => CreateAnthropicClient(profile),
            _ => throw new InvalidOperationException($"Unsupported provider '{profile.Provider}'.")
        };
    }

    private IChatClient CreateOllamaClient(AgentModelProfile profile)
    {
        var endpoint = profile.Endpoint ?? _providers.Ollama.Endpoint;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"Agent '{profile.AgentName}' has an invalid Ollama endpoint '{endpoint}'.");
        }

        // OllamaApiClient implements IChatClient explicitly; returning it as IChatClient is what
        // erases the SDK type for the rest of the application.
        return new OllamaApiClient(uri, profile.ModelId);
    }

    private IChatClient CreateOpenAIClient(AgentModelProfile profile)
    {
        var apiKey = ResolveOpenAIApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Agent '{profile.AgentName}' is configured for OpenAI but no API key was found. " +
                $"Set the {_providers.OpenAI.ApiKeyEnvironmentVariable} environment variable, or run " +
                "'dotnet user-secrets set ModelsAndMonsters:Providers:OpenAI:ApiKey <key>'.");
        }

        var clientOptions = new OpenAIClientOptions();
        var endpoint = profile.Endpoint ?? _providers.OpenAI.Endpoint;
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            clientOptions.Endpoint = new Uri(endpoint);
        }

        // The Responses API (/v1/responses) rather than chat completions. It is the endpoint that allows
        // reasoning effort alongside function tools — chat completions 400s that combination for the
        // reasoning-default GPT-5 models — and it serves the general-purpose models (gpt-4o-mini, gpt-4.1,
        // …) just as well, so no per-model split is needed. Reasoning effort still only applies where the
        // model supports it; ChatOptionsFactory decides that.
        var openAIClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);

        // The Responses client and its IChatClient adapter are still marked experimental (OPENAI001) in
        // this SDK version — the API is stable enough to use but "subject to change"; opting in here is
        // deliberate and scoped to this call.
#pragma warning disable OPENAI001
        return openAIClient.GetResponsesClient().AsIChatClient(profile.ModelId);
#pragma warning restore OPENAI001
    }

    private IChatClient CreateAnthropicClient(AgentModelProfile profile)
    {
        var apiKey = ResolveAnthropicApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Agent '{profile.AgentName}' is configured for Anthropic but no API key was found. " +
                $"Set the {_providers.Anthropic.ApiKeyEnvironmentVariable} environment variable, or run " +
                "'dotnet user-secrets set ModelsAndMonsters:Providers:Anthropic:ApiKey <key>'.");
        }

        var endpoint = profile.Endpoint ?? _providers.Anthropic.Endpoint;
        var client = string.IsNullOrWhiteSpace(endpoint)
            ? new AnthropicClient { ApiKey = apiKey }
            : new AnthropicClient { ApiKey = apiKey, BaseUrl = endpoint };

        // AsIChatClient erases the SDK type for the rest of the application. No UseFunctionInvocation():
        // the harness dispatches every tool call by hand, so no automatic-invocation middleware is added.
        // The caching wrapper marks the stable system-prompt prefix for Anthropic prompt caching.
        return new AnthropicPromptCachingChatClient(client.AsIChatClient(profile.ModelId));
    }

    /// <summary>User secrets first, then environment. Keys are never read from configuration files.</summary>
    private string? ResolveOpenAIApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_providers.OpenAI.ApiKey))
        {
            return _providers.OpenAI.ApiKey;
        }

        var variable = _providers.OpenAI.ApiKeyEnvironmentVariable;
        return string.IsNullOrWhiteSpace(variable) ? null : Environment.GetEnvironmentVariable(variable);
    }

    /// <summary>User secrets first, then environment. Keys are never read from configuration files.</summary>
    private string? ResolveAnthropicApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_providers.Anthropic.ApiKey))
        {
            return _providers.Anthropic.ApiKey;
        }

        var variable = _providers.Anthropic.ApiKeyEnvironmentVariable;
        return string.IsNullOrWhiteSpace(variable) ? null : Environment.GetEnvironmentVariable(variable);
    }
}
