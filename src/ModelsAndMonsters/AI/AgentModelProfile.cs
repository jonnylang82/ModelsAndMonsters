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

    /// <summary>
    /// Whether a reasoning model should think before answering.
    /// </summary>
    /// <remarks>
    /// Null leaves the model's default. For this harness the useful setting is usually false: a
    /// reasoning model left thinking spends its whole output budget on a private reasoning block and
    /// emits no visible prose or tool call within a normal token limit, so narration comes back empty.
    /// Set false to get direct prose and tool calls; set true only with a large <see cref="MaxOutputTokens"/>.
    /// Harmless on models that do not reason — they ignore it.
    /// </remarks>
    public bool? Thinking { get; init; }

    /// <summary>
    /// Input context window in tokens.
    /// </summary>
    /// <remarks>
    /// Worth setting explicitly. Ollama silently discards the oldest messages once a request exceeds
    /// this, reporting only the post-truncation count in <c>prompt_eval_count</c>, so an application
    /// that believes it owns the whole conversation can be quietly wrong. Declaring the window lets the
    /// harness notice when it is being reached instead of guessing.
    /// </remarks>
    public int? ContextWindow { get; init; }

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
            ContextWindow = options.ContextWindow,
            Thinking = options.Thinking,
            Endpoint = string.IsNullOrWhiteSpace(options.Endpoint) ? null : options.Endpoint.Trim()
        };
    }
}
