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

    public static ProviderCapabilities For(ModelProvider provider) => provider switch
    {
        ModelProvider.Ollama => new ProviderCapabilities
        {
            SupportsTemperature = true,
            SupportsTopP = true,
            SupportsTopK = true,
            SupportsMaxOutputTokens = true,
            SupportsSeed = true
        },

        // OpenAI chat completions has no top_k equivalent.
        ModelProvider.OpenAI => new ProviderCapabilities
        {
            SupportsTemperature = true,
            SupportsTopP = true,
            SupportsTopK = false,
            SupportsMaxOutputTokens = true,
            SupportsSeed = true
        },

        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider.")
    };
}
