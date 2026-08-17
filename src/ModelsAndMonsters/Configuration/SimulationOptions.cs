namespace ModelsAndMonsters.Configuration;

/// <summary>Root options bound from the "ModelsAndMonsters" configuration section.</summary>
public sealed class SimulationOptions
{
    public const string SectionName = "ModelsAndMonsters";

    public ProvidersOptions Providers { get; set; } = new();

    public AgentsOptions Agents { get; set; } = new();

    public HarnessOptions Harness { get; set; } = new();
}

public sealed class ProvidersOptions
{
    public OllamaProviderOptions Ollama { get; set; } = new();

    public OpenAIProviderOptions OpenAI { get; set; } = new();
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

public sealed class AgentsOptions
{
    public AgentProfileOptions DungeonMaster { get; set; } = new();

    public AgentProfileOptions Hero { get; set; } = new();

    public AgentProfileOptions Monster { get; set; } = new();
}

/// <summary>Per-agent model and sampling configuration. Every agent is configured independently.</summary>
public sealed class AgentProfileOptions
{
    public string Provider { get; set; } = "Ollama";

    public string ModelId { get; set; } = "";

    public float? Temperature { get; set; }

    public float? TopP { get; set; }

    public int? TopK { get; set; }

    public int? MaxOutputTokens { get; set; }

    public long? Seed { get; set; }

    /// <summary>
    /// Input context window in tokens. Sent to Ollama as <c>num_ctx</c>, and used by the harness to
    /// warn when a request is close enough to the limit that the provider may be discarding history.
    /// </summary>
    public int? ContextWindow { get; set; }

    /// <summary>
    /// Whether a reasoning model should think before answering. Null leaves the model default; false
    /// (recommended for this harness) gives direct prose and tool calls; true needs a large
    /// MaxOutputTokens. Ignored by models that do not reason.
    /// </summary>
    public bool? Thinking { get; set; }

    /// <summary>Optional per-agent endpoint override, e.g. a second Ollama host.</summary>
    public string? Endpoint { get; set; }
}

/// <summary>
/// Harness protections. These are engineering limits on badly behaved models, not in-world game rules.
/// </summary>
public sealed class HarnessOptions
{
    public int MaxRounds { get; set; } = 8;

    /// <summary>Hard ceiling on model calls a single character may make in one turn.</summary>
    public int MaxModelCallsPerTurn { get; set; } = 10;

    public int MaxQuestionsPerTurn { get; set; } = 3;

    public int MaxActionAttemptsPerTurn { get; set; } = 3;

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

    public string RunOutputDirectory { get; set; } = "runs";
}
