namespace ModelsAndMonsters.Configuration;

/// <summary>Root options bound from the "ModelsAndMonsters" configuration section.</summary>
public sealed class SimulationOptions
{
    public const string SectionName = "ModelsAndMonsters";

    public ProvidersOptions Providers { get; set; } = new();

    public AgentsOptions Agents { get; set; } = new();

    public HarnessOptions Harness { get; set; } = new();

    public CombatOptions Combat { get; set; } = new();
}

/// <summary>Tunable combat parameters that are not per-character.</summary>
public sealed class CombatOptions
{
    /// <summary>Chance out of 100 that a landed hit is a glancing blow (half damage). 0 disables them.</summary>
    public int GlancingBlowChance { get; set; } = 25;
}

public sealed class ProvidersOptions
{
    public OllamaProviderOptions Ollama { get; set; } = new();

    public OpenAIProviderOptions OpenAI { get; set; } = new();

    public AnthropicProviderOptions Anthropic { get; set; } = new();
}

public sealed class OllamaProviderOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";
}

public sealed class OpenAIProviderOptions
{
    /// <summary>Environment variable consulted for the API key. Never place the key itself here.</summary>
    public string ApiKeyEnvironmentVariable { get; set; } = "OPENAI_API_KEY";

    /// <summary>
    /// Optional key supplied through user secrets. Configuration files in the repository must not set
    /// this; it exists so <c>dotnet user-secrets</c> works without an environment variable.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Optional override for OpenAI-compatible endpoints.</summary>
    public string? Endpoint { get; set; }
}

public sealed class AnthropicProviderOptions
{
    /// <summary>Environment variable consulted for the API key. Never place the key itself here.</summary>
    public string ApiKeyEnvironmentVariable { get; set; } = "ANTHROPIC_API_KEY";

    /// <summary>
    /// Optional key supplied through user secrets. Configuration files in the repository must not set
    /// this; it exists so <c>dotnet user-secrets</c> works without an environment variable.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Optional base-URL override (e.g. a proxy or gateway).</summary>
    public string? Endpoint { get; set; }
}

public sealed class AgentsOptions
{
    /// <summary>
    /// Common defaults every agent inherits. A per-agent entry overrides only the fields it sets, so a
    /// clean multi-actor baseline can put the shared model here once and leave the character entries
    /// empty, while still allowing any single character to be configured independently later.
    /// </summary>
    public AgentProfileOptions Default { get; set; } = new();

    public AgentProfileOptions DungeonMaster { get; set; } = new();

    /// <summary>
    /// Per-character overrides, keyed by character id. Any character absent here runs on
    /// <see cref="Default"/> alone. Orchestration never assumes a shared hero or monster profile — each
    /// character resolves its own profile from Default plus its own entry.
    /// </summary>
    public Dictionary<string, AgentProfileOptions> Characters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// v0.1 role defaults, retained for backward compatibility with the one-versus-one configuration
    /// and its tests. The v0.2 runner resolves characters by id from <see cref="Characters"/> over
    /// <see cref="Default"/> and does not read these.
    /// </summary>
    public AgentProfileOptions Hero { get; set; } = new();

    public AgentProfileOptions Monster { get; set; } = new();
}

/// <summary>Per-agent model and sampling configuration. Every agent is configured independently.</summary>
public sealed class AgentProfileOptions
{
    /// <summary>Blank means "inherit from the defaults"; the resolved profile must end up with a provider.</summary>
    public string Provider { get; set; } = "";

    public string ModelId { get; set; } = "";

    public float? Temperature { get; set; }

    public float? TopP { get; set; }

    public int? TopK { get; set; }

    public int? MaxOutputTokens { get; set; }

    // The model sampling seed is not configured per agent. It is derived from the run's master seed
    // (Harness.Seed) so a whole run — game rolls and all three agents — replays from one number.

    /// <summary>
    /// Input context window in tokens. Sent to Ollama as <c>num_ctx</c>, and used by the harness to
    /// warn when a request is close enough to the limit that the provider may be discarding history.
    /// </summary>
    public int? ContextWindow { get; set; }

    /// <summary>
    /// Reasoning effort, unified across providers: one of <c>none</c>, <c>low</c>, <c>medium</c>,
    /// <c>high</c>, <c>max</c> (aliases <c>off</c>/<c>xhigh</c> accepted). This is the cross-provider knob
    /// — the harness maps it to Ollama's <c>think</c> level, and to an OpenAI/Anthropic reasoning-effort
    /// request. Null leaves the model default; <c>none</c> (recommended for this harness) disables
    /// reasoning so narration and tool calls come back directly. Supersedes <see cref="Thinking"/> when
    /// both are set. Ignored by models that do not reason. See <see cref="AI.AgentModelProfile.Effort"/>.
    /// </summary>
    public string? Effort { get; set; }

    /// <summary>
    /// Legacy on/off reasoning toggle, retained for backward compatibility. Prefer <see cref="Effort"/>,
    /// which works across all three providers; this only reaches Ollama (dropped elsewhere). Null leaves
    /// the model default; false gives direct prose and tool calls; true needs a large MaxOutputTokens.
    /// </summary>
    public bool? Thinking { get; set; }

    /// <summary>Optional per-agent endpoint override, e.g. a second Ollama host.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// When true, force the model to call a tool (rather than answer in prose) on tool-bearing calls.
    /// Honoured only by providers that support a forced tool choice; ignored (and reported dropped) on
    /// Ollama's native endpoint. See <see cref="AI.AgentModelProfile.ForceToolChoice"/>.
    /// </summary>
    public bool? ForceToolChoice { get; set; }

    /// <summary>
    /// When true, temperature, top-p and top-k are dropped rather than sent. Some models forbid all
    /// sampling parameters (Anthropic Opus 4.7+ 400s on any of them, steering with an effort parameter
    /// and the prompt instead). Left null, the harness omits them automatically for such models; set it
    /// explicitly to force the behaviour either way. See <see cref="AI.AgentModelProfile.OmitSampling"/>.
    /// </summary>
    public bool? OmitSampling { get; set; }

    /// <summary>
    /// Resolves this entry against a base of common defaults: every field this entry leaves unset is
    /// taken from <paramref name="baseOptions"/>. The result is the concrete profile actually used, so
    /// the value recorded for each agent is exactly what its model calls were made with.
    /// </summary>
    public AgentProfileOptions Overlay(AgentProfileOptions baseOptions)
    {
        ArgumentNullException.ThrowIfNull(baseOptions);

        return new AgentProfileOptions
        {
            Provider = FirstNonBlank(Provider, baseOptions.Provider),
            ModelId = FirstNonBlank(ModelId, baseOptions.ModelId),
            Temperature = Temperature ?? baseOptions.Temperature,
            TopP = TopP ?? baseOptions.TopP,
            TopK = TopK ?? baseOptions.TopK,
            MaxOutputTokens = MaxOutputTokens ?? baseOptions.MaxOutputTokens,
            ContextWindow = ContextWindow ?? baseOptions.ContextWindow,
            Effort = string.IsNullOrWhiteSpace(Effort) ? baseOptions.Effort : Effort,
            Thinking = Thinking ?? baseOptions.Thinking,
            Endpoint = string.IsNullOrWhiteSpace(Endpoint) ? baseOptions.Endpoint : Endpoint,
            ForceToolChoice = ForceToolChoice ?? baseOptions.ForceToolChoice,
            OmitSampling = OmitSampling ?? baseOptions.OmitSampling
        };
    }

    private static string FirstNonBlank(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
}

/// <summary>
/// Harness protections. These are engineering limits on badly behaved models, not in-world game rules.
/// </summary>
public sealed class HarnessOptions
{
    /// <summary>
    /// The master run seed. When set, the whole run is deterministic: the game rolls and each agent's
    /// model sampling derive from it, so the same seed reproduces the same run. When null, a random
    /// master is generated and recorded in run.json, so even a "random" run can be replayed by setting
    /// this to the recorded value. Use a fixed seed for testing and leave it blank for real runs.
    /// </summary>
    public long? Seed { get; set; }

    public int MaxRounds { get; set; } = 8;

    /// <summary>Hard ceiling on model calls a single character may make in one turn.</summary>
    public int MaxModelCallsPerTurn { get; set; } = 10;

    public int MaxQuestionsPerTurn { get; set; } = 3;

    public int MaxActionAttemptsPerTurn { get; set; } = 3;

    /// <summary>
    /// How many times a character may speak aloud in one turn. Defaults to 1. Speaking does not consume
    /// the turn, so this is the only thing bounding how much a character can say before it must act or end.
    /// </summary>
    public int MaxSpeechActsPerTurn { get; set; } = 1;

    /// <summary>
    /// Upper bound on the length of a single spoken message, in characters. A message beyond this is
    /// rejected at the harness boundary without being delivered, so one runaway reply cannot flood every
    /// other character's context. Empty messages are always rejected regardless of this value.
    /// </summary>
    public int MaxSpeechCharacters { get; set; } = 600;

    /// <summary>Extra attempts allowed when the DM answers an adjudication without calling a tool.</summary>
    public int MaxAdjudicationRetries { get; set; } = 1;

    /// <summary>
    /// When true, the Dungeon Master runs each task (narration, answering, adjudication) on a
    /// purpose-specific projection — the system prompt, a fresh state snapshot and only the immediately
    /// relevant context — rather than one ever-growing conversation. This keeps the DM's per-call input
    /// bounded (it otherwise re-embeds a full state block every call and saturates the context window)
    /// and keeps adjudication out of prose mode. Measured to matter: with the full narration history
    /// attached, one traced adjudication was misclassified every time; on a clean context it was correct
    /// every time. Set false to run the old single-conversation behaviour for comparison.
    /// </summary>
    public bool ProjectDungeonMasterContext { get; set; } = true;

    /// <summary>
    /// How many consecutive rounds may pass with nothing taking effect before the encounter is
    /// declared a stalemate. Stops two characters grinding to the round limit when neither can, or
    /// wants to, do anything the world can resolve.
    /// </summary>
    public int MaxConsecutiveIdleRounds { get; set; } = 2;

    /// <summary>
    /// When true, a character reply that carries no structured tool call is checked for one written as
    /// prose (e.g. <c>take_action(I strike the goblin)</c>) and, if found, that call is dispatched as if
    /// the model had made it. Off by default so the raw tool-calling behaviour stays observable; turn it
    /// on to let prose-prone small models participate. Every recovery is traced, so it never hides what
    /// the model actually produced.
    /// </summary>
    public bool RecoverTextToolCalls { get; set; }

    /// <summary>
    /// When true, a non-truncated character reply that carries no structured tool call is handed to a
    /// stateless, zero-temperature intent parser that reads the prose into the say / ask_dm / take_action
    /// calls it implies — one reply can yield several (a spoken line AND an action), so the turn resolves in
    /// one pass instead of nudging the character to reformat one call at a time. Supersedes the
    /// <see cref="RecoverTextToolCalls"/> recovery and the speech-retry nudge for those replies; a truncated
    /// reply is still nudged, never parsed. Every parse is traced.
    /// </summary>
    public bool UseIntentParser { get; set; } = true;

    /// <summary>
    /// When true, a character whose estimated history exceeds <see cref="HistoryTokenBudget"/> has its older
    /// turns folded into a running summary, keeping only the last <see cref="RecentTurnsKeptFull"/> turns in
    /// full. Stops a long fight filling the context window with legitimate (non-cruft) history that the
    /// per-turn prune cannot touch; the recent turns stay verbatim so immediate continuity is intact. Every
    /// summarisation is traced.
    /// </summary>
    public bool SummariseHistory { get; set; } = true;

    /// <summary>
    /// The estimated request-token size, per character, past which older turns are summarised. Set well below
    /// the context window so a trim happens before the provider starts silently discarding history.
    /// </summary>
    public int HistoryTokenBudget { get; set; } = 5500;

    /// <summary>How many of a character's most recent turns are kept verbatim when older ones are summarised.</summary>
    public int RecentTurnsKeptFull { get; set; } = 2;

    public string RunOutputDirectory { get; set; } = "runs";
}
