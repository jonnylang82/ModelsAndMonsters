using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            ModelProvider.OpenRouter => CreateOpenRouterClient(profile),
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

        // A local Ollama needs no key; Ollama Cloud (Endpoint https://ollama.com) needs a bearer token. When a
        // key is resolved we hand OllamaApiClient an HttpClient carrying the Authorization header — the only
        // difference between the local and cloud clients — otherwise the plain endpoint constructor is used
        // exactly as before. The HttpClient is created per agent (a handful per run) and lives as long as the
        // run does; there is nothing to dispose separately for a process that ends with the run.
        var apiKey = ResolveOllamaApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var http = new HttpClient { BaseAddress = uri };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return new OllamaApiClient(http, profile.ModelId);
        }

        // OllamaApiClient implements IChatClient explicitly; returning it as IChatClient is what
        // erases the SDK type for the rest of the application.
        return new OllamaApiClient(uri, profile.ModelId);
    }

    /// <summary>
    /// OpenRouter through the OpenAI SDK's CHAT-COMPLETIONS client (not the Responses API, which OpenRouter
    /// does not serve). OpenRouter is OpenAI-compatible and needs no separate SDK: it is the OpenAI client with
    /// OpenRouter's base URL and key.
    /// </summary>
    private IChatClient CreateOpenRouterClient(AgentModelProfile profile)
    {
        var apiKey = ResolveOpenRouterApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Agent '{profile.AgentName}' is configured for OpenRouter but no API key was found. " +
                $"Set the {_providers.OpenRouter.ApiKeyEnvironmentVariable} environment variable, or run " +
                "'dotnet user-secrets set ModelsAndMonsters:Providers:OpenRouter:ApiKey <key>'.");
        }

        var endpoint = profile.Endpoint ?? _providers.OpenRouter.Endpoint;
        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            clientOptions.Endpoint = new Uri(endpoint);
        }

        // Reasoning is OpenRouter's own `reasoning` request object, which the standard OpenAI chat request the
        // SDK builds cannot express. When the agent configures an effort, a pipeline policy injects that object
        // into the request body — this is where Effort becomes real for OpenRouter (ChatOptionsFactory leaves
        // reasoning entirely to the client for this provider). A mandatory-reasoning model may ignore or reject
        // a disable; that is the model's nature, not something the harness can override.
        if (BuildOpenRouterReasoning(profile.Effort) is { } reasoning)
        {
            clientOptions.AddPolicy(new OpenRouterReasoningPolicy(reasoning), PipelinePosition.PerCall);
        }

        // Chat completions, not the Responses API: GetChatClient posts to {endpoint}/chat/completions, which is
        // the endpoint OpenRouter implements. AsIChatClient erases the SDK type for the rest of the application.
        var openAIClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
        return openAIClient.GetChatClient(profile.ModelId).AsIChatClient();
    }

    /// <summary>
    /// Maps the unified <see cref="ReasoningEffort"/> to OpenRouter's <c>reasoning</c> request object, whose
    /// vocabulary is a superset of ours. <see cref="ReasoningEffort.None"/> becomes <c>{ "enabled": false }</c>
    /// — the documented disable, and deliberately NOT <c>{ "effort": "none" }</c>, which OpenRouter says a
    /// mandatory-reasoning model rejects. The graduated levels map straight across, with
    /// <see cref="ReasoningEffort.ExtraHigh"/> taking OpenRouter's top <c>max</c>. Null (unconfigured) returns
    /// null so nothing is injected and the model keeps its own default.
    /// </summary>
    public static JsonObject? BuildOpenRouterReasoning(ReasoningEffort? effort) => effort switch
    {
        null => null,
        ReasoningEffort.None => new JsonObject { ["enabled"] = false },
        ReasoningEffort.Low => new JsonObject { ["effort"] = "low" },
        ReasoningEffort.Medium => new JsonObject { ["effort"] = "medium" },
        ReasoningEffort.High => new JsonObject { ["effort"] = "high" },
        ReasoningEffort.ExtraHigh => new JsonObject { ["effort"] = "max" },
        _ => null
    };

    /// <summary>
    /// Injects OpenRouter's <c>reasoning</c> object into the JSON body of each chat-completions request. The
    /// SDK has no field for it, so it is added at the transport level. Only a body that carries <c>messages</c>
    /// (a chat request) is touched, and a body that cannot be parsed is left exactly as it was.
    /// </summary>
    private sealed class OpenRouterReasoningPolicy(JsonObject reasoning) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Inject(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Inject(message);
            return ProcessNextAsync(message, pipeline, currentIndex);
        }

        private void Inject(PipelineMessage message)
        {
            if (message.Request?.Content is not { } content)
            {
                return;
            }

            using var buffer = new MemoryStream();
            content.WriteTo(buffer);

            if (TryInjectOpenRouterReasoning(Encoding.UTF8.GetString(buffer.ToArray()), reasoning, out var modified))
            {
                message.Request.Content = BinaryContent.Create(BinaryData.FromString(modified));
            }
        }
    }

    /// <summary>
    /// Adds the OpenRouter <c>reasoning</c> object to a chat-completions request body. Returns false, leaving
    /// the body untouched, for anything that is not a JSON object carrying <c>messages</c> — so a body the
    /// client did not build, or one that cannot be parsed, is passed through unchanged.
    /// </summary>
    public static bool TryInjectOpenRouterReasoning(string requestBody, JsonObject reasoning, out string modifiedBody)
    {
        modifiedBody = requestBody;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(requestBody);
        }
        catch (JsonException)
        {
            return false;
        }

        if (root is not JsonObject body || !body.ContainsKey("messages"))
        {
            return false;
        }

        body["reasoning"] = reasoning.DeepClone();
        modifiedBody = body.ToJsonString();
        return true;
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

    /// <summary>User secrets first, then environment. Keys are never read from configuration files.</summary>
    private string? ResolveOpenRouterApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_providers.OpenRouter.ApiKey))
        {
            return _providers.OpenRouter.ApiKey;
        }

        var variable = _providers.OpenRouter.ApiKeyEnvironmentVariable;
        return string.IsNullOrWhiteSpace(variable) ? null : Environment.GetEnvironmentVariable(variable);
    }

    /// <summary>
    /// User secrets first, then environment; null when neither is set (a local Ollama, which needs no key).
    /// Keys are never read from configuration files.
    /// </summary>
    private string? ResolveOllamaApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_providers.Ollama.ApiKey))
        {
            return _providers.Ollama.ApiKey;
        }

        var variable = _providers.Ollama.ApiKeyEnvironmentVariable;
        return string.IsNullOrWhiteSpace(variable) ? null : Environment.GetEnvironmentVariable(variable);
    }
}
