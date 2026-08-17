using ModelsAndMonsters.Configuration;

namespace ModelsAndMonsters.AI;

/// <summary>
/// The complete model configuration for one agent. Every agent gets its own profile, so the Dungeon
/// Master, Hero and Monster can run on different providers and different sampling settings.
/// </summary>
public sealed record AgentModelProfile
{
    public required string AgentName { get; init; }

    public required ModelProvider Provider { get; init; }

    public required string ModelId { get; init; }

    public float? Temperature { get; init; }

    public float? TopP { get; init; }

    public int? TopK { get; init; }

    public int? MaxOutputTokens { get; init; }

    public long? Seed { get; init; }

    /// <summary>Optional per-agent endpoint override; falls back to the provider-level endpoint.</summary>
    public string? Endpoint { get; init; }

    public static AgentModelProfile FromOptions(string agentName, AgentProfileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Enum.TryParse<ModelProvider>(options.Provider, ignoreCase: true, out var provider))
        {
            throw new InvalidOperationException(
                $"Agent '{agentName}' specifies unknown provider '{options.Provider}'. " +
                $"Supported providers: {string.Join(", ", Enum.GetNames<ModelProvider>())}.");
        }

        if (string.IsNullOrWhiteSpace(options.ModelId))
        {
            throw new InvalidOperationException($"Agent '{agentName}' is missing a ModelId.");
        }

        return new AgentModelProfile
        {
            AgentName = agentName,
            Provider = provider,
            ModelId = options.ModelId.Trim(),
            Temperature = options.Temperature,
            TopP = options.TopP,
            TopK = options.TopK,
            MaxOutputTokens = options.MaxOutputTokens,
            Seed = options.Seed,
            Endpoint = string.IsNullOrWhiteSpace(options.Endpoint) ? null : options.Endpoint.Trim()
        };
    }
}
