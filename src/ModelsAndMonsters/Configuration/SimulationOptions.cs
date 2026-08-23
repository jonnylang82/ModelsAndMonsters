using ModelsAndMonsters.Engine;

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

/// <summary>Tunable combat and engine-probability parameters that are not per-character.</summary>
public sealed class CombatOptions
{
    /// <summary>
    /// The size of the LOW band of the single attack-quality draw, out of 100: a landed blow glances (half
    /// damage) when the raw quality roll is at or under this. 0 removes the band.
    /// </summary>
    public int GlancingBlowChance { get; set; } = 25;

    /// <summary>
    /// The size of the HIGH band of the same draw, out of 100: a landed blow is critical (double damage) when
    /// the raw quality roll is above <c>100 - CriticalHitChance</c>. 0 removes the band. With both bands at 25
    /// the draw reads 1-25 glancing, 26-75 solid, 76-100 critical.
    /// </summary>
    public int CriticalHitChance { get; set; } = 25;

    /// <summary>
    /// Base chance out of 100 that an open threat frightens its target, before the state-derived modifiers.
    /// Nothing about the wording of the threat ever changes it.
    /// </summary>
    public int BaseIntimidationChance { get; set; } = IntimidationRules.DefaultBaseChance;

    /// <summary>
    /// Base chance out of 100 that an attempted theft succeeds, before any modifier (v0.6 applies none).
    /// The one probability the inventory system consults; deliberately a flat configurable number rather
    /// than a derived skill, as v0.6 adds no stat system.
    /// </summary>
    public int BaseStealChance { get; set; } = 40;
}

public sealed class ProvidersOptions
{
    public OllamaProviderOptions Ollama { get; set; } = new();

    public OpenAIProviderOptions OpenAI { get; set; } = new();

    public AnthropicProviderOptions Anthropic { get; set; } = new();

    public OpenRouterProviderOptions OpenRouter { get; set; } = new();
}

public sealed class OllamaProviderOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Environment variable consulted for an Ollama API key. Only needed to reach Ollama Cloud
    /// (Endpoint <c>https://ollama.com</c>); a local Ollama needs none, so this is unset in effect there.
    /// Never place the key itself here.
    /// </summary>
    public string ApiKeyEnvironmentVariable { get; set; } = "OLLAMA_API_KEY";

    /// <summary>
    /// Optional Ollama Cloud key supplied through user secrets. When a key is resolved (from here or the
    /// environment variable) it is sent as a bearer token, which is what turns the plain local client into
    /// an Ollama Cloud one. Configuration files in the repository must not set it.
    /// </summary>
    public string? ApiKey { get; set; }
}

public sealed class OpenRouterProviderOptions
{
    /// <summary>Environment variable consulted for the API key. Never place the key itself here.</summary>
    public string ApiKeyEnvironmentVariable { get; set; } = "OPENROUTER_API_KEY";

    /// <summary>
    /// Optional key supplied through user secrets. Configuration files in the repository must not set
    /// this; it exists so <c>dotnet user-secrets</c> works without an environment variable.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>OpenRouter's OpenAI-compatible base URL. The chat-completions path is appended by the client.</summary>
    public string Endpoint { get; set; } = "https://openrouter.ai/api/v1";
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
    /// The Rulebook Resolver's independently configurable model profile (v0.6). Overlaid on
    /// <see cref="Default"/> like any agent, so it can run on its own provider, model and — importantly — a
    /// low temperature for stable, reproducible rule guidance. Left empty, it inherits the shared default.
    /// </summary>
    public AgentProfileOptions RulebookResolver { get; set; } = new();

    /// <summary>
    /// The Encounter Summariser's independently configurable model profile. Overlaid on <see cref="Default"/>
    /// like any agent, so it inherits the run's provider/model unless overridden — but it is exactly the
    /// agent an operator would want to point at a different model or different sampling: it writes a short
    /// dramatic story from the encounter's public transcript at the very end of a run, once, statelessly, and
    /// nothing it produces is read by the game or by any other agent. See <see cref="Agents.EncounterSummariser"/>.
    /// </summary>
    public AgentProfileOptions EncounterSummariser { get; set; } = new();

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

    /// <summary>
    /// Flat penalty on tokens already seen recently. **Leave at 0 for this harness unless you have a reason
    /// not to.** Null does NOT mean "off" — it means the model's own default applies, and Qwen ships a chat
    /// default of 1.5 that penalises exactly the exact-string copying tool calling depends on. See
    /// <see cref="AI.AgentModelProfile.PresencePenalty"/>. Dropped on Anthropic, which has no equivalent.
    /// </summary>
    public float? PresencePenalty { get; set; }

    /// <summary>
    /// Penalty scaled by how often a token has already appeared. Null leaves the model default. Dropped on
    /// Anthropic, which has no equivalent.
    /// </summary>
    public float? FrequencyPenalty { get; set; }

    /// <summary>
    /// The classic llama.cpp/Ollama <c>repeat_penalty</c> — a different knob from
    /// <see cref="PresencePenalty"/>/<see cref="FrequencyPenalty"/>. Only Ollama has this raw option; dropped
    /// elsewhere. See <see cref="AI.AgentModelProfile.RepeatPenalty"/>.
    /// </summary>
    public float? RepeatPenalty { get; set; }

    /// <summary>
    /// How many recent tokens <see cref="RepeatPenalty"/> looks back over before it stops discouraging a
    /// repeat. Only Ollama has this raw option; dropped elsewhere. See
    /// <see cref="AI.AgentModelProfile.RepeatLastN"/>.
    /// </summary>
    public int? RepeatLastN { get; set; }

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
            PresencePenalty = PresencePenalty ?? baseOptions.PresencePenalty,
            FrequencyPenalty = FrequencyPenalty ?? baseOptions.FrequencyPenalty,
            RepeatPenalty = RepeatPenalty ?? baseOptions.RepeatPenalty,
            RepeatLastN = RepeatLastN ?? baseOptions.RepeatLastN,
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

    /// <summary>
    /// Whether a configured <c>ContextWindow</c> constrains history summarisation on providers that never
    /// receive it — OpenAI and Anthropic, whose windows are fixed per model and far larger. Off by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Turn it on to level the field. Comparing providers is the point of this harness, and a hosted model
    /// that never has to forget anything is not answering the same question as a local one working inside
    /// 8k — so when the comparison is about how well a model plays a long fight, the handicap belongs on
    /// both sides. When the comparison is about the best each provider can do, leave it off. Either way the
    /// choice is now explicit and recorded in <c>run.json</c>, rather than an accident of which defaults a
    /// hosted profile happened to inherit.
    /// </para>
    /// <para>
    /// Leaving this off does NOT, on its own, make a hosted run keep its history verbatim: see
    /// <see cref="UnboundedHistoryOnHostedModels"/> for the separate flat ceiling that still applies
    /// underneath it.
    /// </para>
    /// </remarks>
    public bool EnforceContextWindowOnHostedModels { get; set; }

    /// <summary>
    /// Whether a hosted agent whose <c>ContextWindow</c> is not binding (<see cref="EnforceContextWindowOnHostedModels"/>
    /// left off) is exempted from <see cref="HistoryTokenBudget"/> entirely, rather than still being
    /// summarised once its history crosses that flat number. Off by default, matching existing behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A real gap, not a hypothetical one: <see cref="AI.ContextTruncation.EffectiveHistoryBudget"/> falls
    /// back to the flat <see cref="HistoryTokenBudget"/> (9000) whenever no window is known to bind a
    /// request — which is exactly the case a hosted profile is in when
    /// <see cref="EnforceContextWindowOnHostedModels"/> is left off. So turning that toggle off stops
    /// <c>ContextWindow</c> from constraining a hosted run, but a Claude or GPT conversation still gets
    /// proactively compacted the moment it crosses ~9000 estimated tokens regardless — a local-model-sized
    /// ceiling reintroduced exactly where the other toggle was meant to remove it.
    /// </para>
    /// <para>
    /// Set this to true to actually let a hosted run keep its full history uncompacted for as long as its own
    /// (far larger) real window allows, to see what an unbounded-memory hosted run looks like. The trade-off
    /// is real: nothing then caps prompt size or cost on a long hosted run — the harness is trusting the
    /// provider's own window entirely, the same way it already does for truncation-detection once
    /// <see cref="EnforceContextWindowOnHostedModels"/> is off.
    /// </para>
    /// </remarks>
    public bool UnboundedHistoryOnHostedModels { get; set; }

    public int MaxRounds { get; set; } = 8;

    /// <summary>Hard ceiling on model calls a single character may make in one turn.</summary>
    public int MaxModelCallsPerTurn { get; set; } = 10;

    public int MaxQuestionsPerTurn { get; set; } = 3;

    public int MaxActionAttemptsPerTurn { get; set; } = 3;

    /// <summary>
    /// How many times a character may speak aloud in one turn. Speaking does not consume the turn, so this
    /// is the only thing bounding how much a character can say before it must act or end.
    /// </summary>
    /// <remarks>
    /// Two, not one, since v0.8, and the reason is the retry path rather than chattiness. A character that
    /// speaks as part of an action attempt has spent its voice — and if the Dungeon Master then refuses that
    /// attempt, the character is asked to try again with nothing left to say. Every occurrence in a live run
    /// was exactly this: Skrit said "Elara, this spear isn't going to hurt you!" with a compound action that
    /// was refused, reworded the action, and had "Eat this!" swallowed. The words that accompanied the failed
    /// attempt were really said, so they cannot be given back; the honest fix is to allow the second breath
    /// that a second attempt implies. Raising it further would let a character talk through a whole turn.
    /// </remarks>
    public int MaxSpeechActsPerTurn { get; set; } = 2;

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
    /// When true, the Dungeon Master speaks a single artistic-but-truthful line recapping each round once its
    /// turns are done. The summary is grounded strictly in that round's public narrations — it is audience-only
    /// (never delivered into any character's knowledge) and adds one bounded DM call per round, so it can be
    /// turned off for token-tight local runs. A round that ends the encounter is left to the ending and the
    /// story rather than also getting a recap.
    /// </summary>
    public bool NarrateRoundSummaries { get; set; } = true;

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
    /// The estimated request-token size, per character, past which older turns are summarised. It is an UPPER
    /// BOUND, not the working figure: the effective budget is derived from the agent's own context window,
    /// output reserve and measured prompt overhead, and the smaller of the two wins
    /// (<see cref="AI.ContextTruncation.EffectiveHistoryBudget"/>).
    /// </summary>
    /// <remarks>
    /// It sits above what an 8k window can hold on purpose. Calibrated to 5500 for an 8192-token window, this
    /// number silently became the binding constraint on a larger window too — so a run with plenty of room
    /// still summarised early and lost fidelity for nothing. The derived figure already protects the window;
    /// this only needs to stop a very large window licensing an unbounded history.
    /// </remarks>
    public int HistoryTokenBudget { get; set; } = 9000;

    /// <summary>
    /// How many of a character's most recent turns are kept verbatim when older ones are summarised.
    /// </summary>
    /// <remarks>
    /// One, not two, since v0.7. Every kept turn carries a full turn context — the character's whole
    /// self-state, its knowledge and the narration it heard — which measured 4,250 characters a turn once
    /// abilities, statuses and surrender offers joined it. Keeping two verbatim therefore re-sent two
    /// superseded copies of that state on every call, and a measured peak character request of 8,160 tokens
    /// against an 8,192-token window (five replies truncated). The newest context supersedes the older ones
    /// by definition; what happened in them survives in the running recap.
    /// </remarks>
    public int RecentTurnsKeptFull { get; set; } = 1;

    /// <summary>
    /// When true, every <c>take_action</c> is preceded by a bounded, stateless rulebook consultation that
    /// narrows the Dungeon Master to a small candidate tool set (v0.6). When false, the DM is given the full
    /// engine tool surface directly (the v0.5 path), which is useful as an A/B comparison.
    /// </summary>
    public bool EnableRulebookResolver { get; set; } = true;

    /// <summary>
    /// A hard CEILING on the rule cards a resolver request may carry — not a trimming budget. The retriever
    /// sends the whole (small) rulebook every time and lets the resolver do the semantic selection; there is
    /// no keyword routing deciding which actions the resolver may consider. If the catalog ever grows past
    /// this many cards the retriever throws at startup (fails visibly) rather than silently dropping cards,
    /// which would risk hiding the one action an intent needs. Set with headroom above the current catalog.
    /// </summary>
    public int RulebookMaxCards { get; set; } = 32;

    /// <summary>A hard CEILING (not a budget) on the total size, in characters, of the cards sent to the resolver. Exceeding it fails visibly at startup rather than trimming.</summary>
    public int RulebookMaxInputChars { get; set; } = 32000;

    /// <summary>
    /// The output-token limit applied to the resolver's reply, and the room reserved for it when checking
    /// that the rulebook fits the resolver's context window.
    /// </summary>
    /// <remarks>
    /// 300 since v0.8, down from 600. The old figure was sized for a schema that asked the resolver to copy
    /// the cited card back; since the consultant hydrates those fields itself, measured replies are 69-132
    /// tokens. On a local model the window covers input and output together, so an oversized reserve is not
    /// free — it is that much less room for the rulebook.
    /// </remarks>
    public int RulebookOutputTokens { get; set; } = 300;

    /// <summary>When true, abstract rule guidance is cached and reused across identical consultations.</summary>
    public bool RulebookCacheEnabled { get; set; } = true;

    /// <summary>
    /// How the cards for a consultation are chosen: <c>WholeRulebook</c> (the default and the shipped
    /// production path), <c>CompactIndex</c>, <c>Embedding</c>, <c>StructuredRouting</c> or
    /// <c>ActionRouting</c>.
    /// </summary>
    /// <remarks>
    /// Everything but the default is EXPERIMENTAL and behind this switch on purpose. Each alternative is
    /// required to fall back to the whole bounded rulebook whenever it is not confident, so the worst case is
    /// the baseline's cost rather than an answer given without the rule that mattered — but "the worst case is
    /// survivable" is not the same as "proven", and the measured comparison lives in
    /// <c>reports/rulebook-efficiency.md</c>. <c>StructuredRouting</c> is only meaningful to a caller that
    /// already knows the action family; configured for ordinary pre-adjudication consultation it correctly
    /// falls back every time, because it refuses to infer a family from language.
    /// </remarks>
    public string RulebookSelectionMode { get; set; } = "WholeRulebook";

    /// <summary>How many cards an embedding selection takes before declared related-rule expansion.</summary>
    public int RulebookSelectionTopK { get; set; } = 3;

    /// <summary>
    /// The Ollama embedding model used when the selection mode is <c>Embedding</c>. Blank falls back to the
    /// offline trigram prototype, which is a lexical measure and NOT a semantic one — see
    /// <c>reports/rulebook-efficiency.md</c> for what its numbers do and do not mean.
    /// </summary>
    public string RulebookEmbeddingModel { get; set; } = "";

    /// <summary>
    /// When true, a selection is cached against the static rulebook version, separately from the answer
    /// cache. Card metadata does not change mid-run, so the same intent selects the same cards without
    /// paying for the selection twice; the resolver's answer cache stays independent of it.
    /// </summary>
    public bool RulebookSelectionCacheEnabled { get; set; } = true;

    public string RunOutputDirectory { get; set; } = "runs";

    /// <summary>
    /// When true, a short dramatic story of the encounter is generated once at the end of a completed run,
    /// from the public transcript alone (narration and speech — never private answers, refused attempts, or
    /// harness/compaction notices), printed to the console/UI and written to <c>story.md</c> alongside the
    /// rest of the run's artefacts. Off does not change anything else about the run; the game and every other
    /// agent never read the story.
    /// </summary>
    public bool GenerateEncounterStory { get; set; } = true;

    /// <summary>
    /// The output-token budget for the encounter story. One figure for every provider — deliberately not
    /// tuned separately for a local, window-shared model the way <see cref="RulebookOutputTokens"/> or the
    /// DM's adjudication cap are, because this call has neither of the reasons that tightness exists for
    /// elsewhere: it runs once per whole run rather than once per turn, and it is not fighting a shared
    /// window with a large input the way a per-turn call is. 3000 is chosen to sit safely under an 8192
    /// Ollama window even in the worst case (the ~4096-token transcript budget plus the summariser's own
    /// system prompt and task framing, with margin to spare) while comfortably outlasting what any genuine
    /// four-section short story needs, on any provider.
    /// </summary>
    /// <remarks>
    /// A local-model-only cap here was tried first (900) and was the wrong instinct — the same mistake the
    /// README's "A local-model accommodation must not become a global rule" section already describes three
    /// times, applied a fourth: a live Claude Sonnet run hit exactly 900/900 output tokens
    /// (<c>FinishReason: length</c>) with only 2,367 of a vastly larger input window used — no window
    /// pressure at all, only a local-only tuning cutting the reply short for nothing. Null is not a way to
    /// opt out of a cap entirely: Anthropic's API requires <c>max_tokens</c> on every request (see the
    /// README's Anthropic section), so some explicit figure always has to be sent regardless of provider;
    /// this is deliberately generous enough that it is that figure everywhere, rather than a real limit
    /// anything is expected to hit.
    /// </remarks>
    public int EncounterStoryOutputTokens { get; set; } = 3000;

    /// <summary>
    /// The largest share of the summariser's own context window the public transcript may occupy. The
    /// remainder is headroom for the system prompt, the scenario framing, and the model's own reply — this
    /// agent is expected to stay comfortably inside an 8k-class local window the same as every other agent in
    /// this harness, never a reason to raise one. When the transcript would exceed the derived character
    /// budget, the OLDEST public entries are dropped first (the same discipline <see cref="RecentTurnsKeptFull"/>
    /// applies to a character's own history) and the story is told a note that some of the earliest events
    /// were omitted.
    /// </summary>
    public double EncounterStoryInputBudgetFraction { get; set; } = 0.5;
}
