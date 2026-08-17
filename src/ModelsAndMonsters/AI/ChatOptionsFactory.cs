using System.Collections.Immutable;
using Microsoft.Extensions.AI;

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

        if (tools is { Count: > 0 })
        {
            options.Tools = [.. tools];

            // Tools are declaration-only and are dispatched by our own orchestration code. Nothing in
            // the pipeline is allowed to invoke them, so no automatic-invocation middleware is used.
            options.ToolMode = ChatToolMode.Auto;
        }

        return new ResolvedChatOptions(options, dropped.ToImmutable());
    }
}
