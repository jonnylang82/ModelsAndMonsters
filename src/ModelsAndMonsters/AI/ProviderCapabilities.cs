namespace ModelsAndMonsters.AI;

/// <summary>
/// Which sampling options a provider can actually honour.
/// </summary>
/// <remarks>
/// Providers do not have parity. Rather than silently sending an option the provider ignores, or
/// pretending every profile applies everywhere, unsupported options are dropped and the drop is
/// recorded in the trace alongside the configuration we asked for.
/// </remarks>
public sealed record ProviderCapabilities
{
    public required bool SupportsTemperature { get; init; }

    public required bool SupportsTopP { get; init; }

    public required bool SupportsTopK { get; init; }

    public required bool SupportsMaxOutputTokens { get; init; }

    public required bool SupportsSeed { get; init; }

    /// <summary>
    /// Whether the caller can choose the input context window. Ollama takes <c>num_ctx</c> per request;
    /// hosted OpenAI models have a fixed window per model and reject anything longer outright.
    /// </summary>
    public required bool SupportsContextWindow { get; init; }

    /// <summary>
    /// Whether reasoning can be toggled with a simple on/off request field. Ollama takes a <c>think</c>
    /// flag; OpenAI's reasoning models use a different mechanism (reasoning effort) that v0.1 does not
    /// wire up, so the toggle is reported as unsupported there rather than silently ignored.
    /// </summary>
    public required bool SupportsThinkingToggle { get; init; }

    public static ProviderCapabilities For(ModelProvider provider) => provider switch
    {
        ModelProvider.Ollama => new ProviderCapabilities
        {
            SupportsTemperature = true,
            SupportsTopP = true,
            SupportsTopK = true,
            SupportsMaxOutputTokens = true,
            SupportsSeed = true,
            SupportsContextWindow = true,
            SupportsThinkingToggle = true
        },

        // OpenAI chat completions has no top_k equivalent, and its context window is fixed per model.
        ModelProvider.OpenAI => new ProviderCapabilities
        {
            SupportsTemperature = true,
            SupportsTopP = true,
            SupportsTopK = false,
            SupportsMaxOutputTokens = true,
            SupportsSeed = true,
            SupportsContextWindow = false,
            SupportsThinkingToggle = false
        },

        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider.")
    };
}
