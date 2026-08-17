using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace ModelsAndMonsters.AI;

/// <summary>The <see cref="ChatOptions"/> built for a call, plus anything the provider could not honour.</summary>
public sealed record ResolvedChatOptions(ChatOptions Options, ImmutableArray<string> UnsupportedOptionsDropped);

/// <summary>Builds provider-appropriate <see cref="ChatOptions"/> from an agent's profile.</summary>
public static class ChatOptionsFactory
{
    public static ResolvedChatOptions Create(AgentModelProfile profile, IReadOnlyList<AITool>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var capabilities = ProviderCapabilities.For(profile.Provider);
        var dropped = ImmutableArray.CreateBuilder<string>();

        var options = new ChatOptions
        {
            ModelId = profile.ModelId
        };

        if (profile.Temperature is { } temperature)
        {
            if (capabilities.SupportsTemperature)
            {
                options.Temperature = temperature;
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.Temperature));
            }
        }

        if (profile.TopP is { } topP)
        {
            if (capabilities.SupportsTopP)
            {
                options.TopP = topP;
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.TopP));
            }
        }

        if (profile.TopK is { } topK)
        {
            if (capabilities.SupportsTopK)
            {
                options.TopK = topK;
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.TopK));
            }
        }

        if (profile.MaxOutputTokens is { } maxOutputTokens)
        {
            if (capabilities.SupportsMaxOutputTokens)
            {
                options.MaxOutputTokens = maxOutputTokens;
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.MaxOutputTokens));
            }
        }

        if (profile.Seed is { } seed)
        {
            if (capabilities.SupportsSeed)
            {
                options.Seed = seed;
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.Seed));
            }
        }

        if (profile.ContextWindow is { } contextWindow)
        {
            if (capabilities.SupportsContextWindow)
            {
                ApplyOllamaContextWindow(options, contextWindow);
            }
            else
            {
                // Not a failure: the provider simply fixes the window per model. The profile value is
                // still used by the harness to warn about approaching it.
                dropped.Add(nameof(AgentModelProfile.ContextWindow));
            }
        }

        if (profile.Thinking is { } thinking)
        {
            if (capabilities.SupportsThinkingToggle)
            {
                ApplyOllamaThinking(options, thinking);
            }
            else
            {
                dropped.Add(nameof(AgentModelProfile.Thinking));
            }
        }

        if (tools is { Count: > 0 })
        {
            options.Tools = [.. tools];

            // Tools are declaration-only and are dispatched by our own orchestration code. Nothing in
            // the pipeline is allowed to invoke them, so no automatic-invocation middleware is used.
            options.ToolMode = ChatToolMode.Auto;
        }

        return new ResolvedChatOptions(options, dropped.ToImmutable());
    }

    /// <summary>
    /// Sets Ollama's <c>num_ctx</c>. This is a provider-specific detail, and it stays here in the AI
    /// layer rather than reaching agents or orchestration.
    /// </summary>
    private static void ApplyOllamaContextWindow(ChatOptions options, int contextWindow) =>
        options.AddOllamaOption(OllamaSharp.Models.OllamaOption.NumCtx, contextWindow);

    /// <summary>Sets Ollama's top-level <c>think</c> flag, which enables or disables reasoning.</summary>
    private static void ApplyOllamaThinking(ChatOptions options, bool thinking) =>
        options.AddOllamaOption(OllamaSharp.Models.OllamaOption.Think, thinking);
}
