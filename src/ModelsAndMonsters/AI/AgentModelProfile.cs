using Microsoft.Extensions.AI;
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

    /// <summary>
    /// Model sampling seed. Not read from per-agent config: it is derived from the run's master seed
    /// and set on the profile by the runner, so the whole run replays from one number.
    /// </summary>
    public long? Seed { get; init; }

    /// <summary>
    /// Reasoning effort, unified across every provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single cross-provider reasoning knob. The harness maps one <see cref="ReasoningEffort"/> to
    /// each provider's native mechanism: Ollama's <c>think</c> level (<c>None</c> → off, otherwise the
    /// matching level), and OpenAI's and Anthropic's reasoning effort through
    /// <see cref="ChatOptions.Reasoning"/>, which their Microsoft.Extensions.AI adapters translate.
    /// </para>
    /// <para>
    /// Null leaves the model's default. For this harness the useful setting is usually
    /// <see cref="ReasoningEffort.None"/>: a reasoning model left thinking spends its whole output budget
    /// on a private reasoning block and emits no visible prose or tool call within a normal token limit,
    /// so narration comes back empty. Set <c>None</c> for direct prose and tool calls; raise it only with
    /// a large <see cref="MaxOutputTokens"/>. Supersedes <see cref="Thinking"/> when both are set.
    /// Harmless on models that do not reason — they ignore it.
    /// </para>
    /// </remarks>
    public ReasoningEffort? Effort { get; init; }

    /// <summary>
    /// Legacy on/off reasoning toggle. Prefer <see cref="Effort"/>, the cross-provider knob; this reaches
    /// only Ollama (dropped and reported elsewhere) and is superseded by <see cref="Effort"/> when both
    /// are set. Null leaves the model default; false gives direct prose and tool calls.
    /// </summary>
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

    /// <summary>
    /// When true, the model is told it MUST call a tool (<c>ChatToolMode.RequireAny</c>) on any call that
    /// offers tools, rather than being free to answer in prose.
    /// </summary>
    /// <remarks>
    /// Only providers that honour a forced tool choice can apply this. Ollama's native <c>/api/chat</c>
    /// silently ignores it (measured), so it is reported as unsupported there; its OpenAI-compatible
    /// <c>/v1</c> endpoint does honour it, so a local model can be forced by configuring it as an OpenAI
    /// provider pointed at <c>http://localhost:11434/v1</c>. Useful for prose-prone models that can call
    /// tools but often don't; <see cref="ModelsAndMonsters.Configuration.HarnessOptions.RecoverTextToolCalls"/>
    /// is the fallback that works even on the native path.
    /// </remarks>
    public bool? ForceToolChoice { get; init; }

    /// <summary>
    /// When true, sampling parameters (temperature, top-p, top-k) are dropped rather than sent. Null lets
    /// the harness decide per model — some models forbid all three (Anthropic Opus 4.7+). See
    /// <see cref="ModelsAndMonsters.Configuration.AgentProfileOptions.OmitSampling"/>.
    /// </summary>
    public bool? OmitSampling { get; init; }

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
            // Seed is not read from config here; the runner derives it from the master seed.
            ContextWindow = options.ContextWindow,
            Effort = ParseEffort(agentName, options.Effort),
            Thinking = options.Thinking,
            Endpoint = string.IsNullOrWhiteSpace(options.Endpoint) ? null : options.Endpoint.Trim(),
            ForceToolChoice = options.ForceToolChoice,
            OmitSampling = options.OmitSampling
        };
    }

    /// <summary>
    /// Parses a configured effort string into a <see cref="ReasoningEffort"/>. Blank means "unset"
    /// (leave the model default). Accepts the five canonical levels plus a few natural aliases so the
    /// config reads well regardless of which provider is in mind. An unrecognised value throws rather
    /// than being silently ignored, matching how an unknown provider is handled.
    /// </summary>
    internal static ReasoningEffort? ParseEffort(string agentName, string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        return effort.Trim().ToLowerInvariant() switch
        {
            "none" or "off" or "false" or "disable" or "disabled" => ReasoningEffort.None,
            "low" or "minimal" => ReasoningEffort.Low,
            "medium" or "med" or "balanced" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "max" or "maximum" or "xhigh" or "extrahigh" or "extra-high" or "extra" => ReasoningEffort.ExtraHigh,
            _ => throw new InvalidOperationException(
                $"Agent '{agentName}' specifies unknown reasoning effort '{effort}'. " +
                "Supported values: none, low, medium, high, max.")
        };
    }
}
