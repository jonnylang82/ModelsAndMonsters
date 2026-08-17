using System.ClientModel;
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

        var openAIClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
        return openAIClient.GetChatClient(profile.ModelId).AsIChatClient();
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
}
