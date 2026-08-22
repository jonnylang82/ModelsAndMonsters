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
    /// Whether reasoning can be toggled with a simple on/off request field — the legacy <c>Thinking</c>
    /// bool. Ollama takes a <c>think</c> flag; OpenAI and Anthropic reach reasoning through an effort
    /// level, not a bool, so the bare toggle is reported as unsupported there rather than silently
    /// ignored. The cross-provider path is <see cref="AgentModelProfile.Effort"/>, which every provider
    /// honours (mapped to the think flag on Ollama and to <c>ChatOptions.Reasoning</c> on the others).
    /// </summary>
    public required bool SupportsThinkingToggle { get; init; }

    /// <summary>
    /// Whether the provider honours a forced tool choice (<c>tool_choice: required</c>). Ollama's native
    /// <c>/api/chat</c> silently ignores it (measured: the model still answers in prose), so it is
    /// reported as unsupported; the OpenAI chat-completions API — including Ollama's own OpenAI-compatible
    /// <c>/v1</c> endpoint — does honour it.
    /// </summary>
    public required bool SupportsForcedToolChoice { get; init; }

    /// <summary>
    /// Whether the provider accepts <c>temperature</c> and <c>top_p</c> in the same request. Anthropic
    /// rejects both together ("use only one"), so when both are configured the harness keeps temperature
    /// and drops top_p rather than failing the call.
    /// </summary>
    public required bool AllowsTemperatureAndTopPTogether { get; init; }

    /// <summary>
    /// Whether the provider accepts <c>presence_penalty</c> and <c>frequency_penalty</c>. Ollama and OpenAI
    /// do; Anthropic has no equivalent and rejects them, so they are dropped and reported there rather than
    /// silently ignored.
    /// </summary>
    public required bool SupportsPenalties { get; init; }

    /// <summary>
    /// Whether the provider takes the classic llama.cpp/Ollama <c>repeat_penalty</c> — a raw provider option,
    /// not part of the OpenAI-shaped <c>presence_penalty</c>/<c>frequency_penalty</c> pair. Only Ollama has
    /// this knob; OpenAI and Anthropic have no equivalent request field, so it is dropped and reported there.
    /// </summary>
    public required bool SupportsRepeatPenalty { get; init; }

    /// <summary>
    /// Whether the provider can constrain a reply to a JSON schema at the decoder — a response format the
    /// server enforces token by token, not merely asks for in the prompt. Ollama takes a JSON schema as its
    /// <c>format</c> field and llama.cpp constrains decoding to it; OpenAI has strict structured outputs;
    /// Anthropic has no JSON mode, so there a schema-constrained request degrades to prompt-plus-parse. Gated
    /// like every other uneven capability: where it is unsupported the constraint is dropped and reported, and
    /// tolerant parsing with a whole-rulebook fallback remains the backstop either way.
    /// </summary>
    public required bool SupportsStructuredOutputSchema { get; init; }

    /// <summary>
    /// Whether the provider silently drops the oldest messages when a request exceeds the context window.
    /// Ollama does (and reports only the post-truncation size), which is why the harness infers it from the
    /// sent-versus-reported gap. OpenAI does not: it rejects an over-long request with an error rather than
    /// truncating, so that inference only produces false positives from tokenizer-estimate noise there and
    /// must not run.
    /// </summary>
    public required bool SilentlyTruncatesHistory { get; init; }

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
            SupportsThinkingToggle = true,
            SupportsForcedToolChoice = false,
            AllowsTemperatureAndTopPTogether = true,
            SupportsPenalties = true,
            SupportsRepeatPenalty = true,
            SupportsStructuredOutputSchema = true,
            SilentlyTruncatesHistory = true
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
            SupportsThinkingToggle = false,
            SupportsForcedToolChoice = true,
            AllowsTemperatureAndTopPTogether = true,
            SupportsPenalties = true,
            SupportsRepeatPenalty = false,
            SupportsStructuredOutputSchema = true,
            SilentlyTruncatesHistory = false
        },

        // Anthropic supports top_k and a forced tool choice, but has no request seed, a fixed per-model
        // window, and reaches "extended thinking" through a token budget rather than a simple on/off flag
        // (which this harness does not wire up). max_tokens is required — always set MaxOutputTokens.
        ModelProvider.Anthropic => new ProviderCapabilities
        {
            SupportsTemperature = true,
            SupportsTopP = true,
            SupportsTopK = true,
            SupportsMaxOutputTokens = true,
            SupportsSeed = false,
            SupportsContextWindow = false,
            SupportsThinkingToggle = false,
            SupportsForcedToolChoice = true,
            AllowsTemperatureAndTopPTogether = false,
            SupportsPenalties = false,
            SupportsRepeatPenalty = false,
            // Anthropic has no JSON/structured-output mode the harness wires up; a schema-constrained request
            // degrades to prompt-plus-parse there, dropped-and-reported like every other uneven capability.
            SupportsStructuredOutputSchema = false,
            SilentlyTruncatesHistory = false
        },

        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider.")
    };
}
