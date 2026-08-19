using Microsoft.Extensions.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Rulebook;

/// <summary>How a rulebook consultation resolved.</summary>
public enum RulebookOutcome
{
    /// <summary>The rulebook supports the intent; the guidance narrows the DM to one or a few candidate actions.</summary>
    Supported,

    /// <summary>The rulebook has no action that resolves the intent; the DM is exposed only the rejection.</summary>
    Unsupported,

    /// <summary>Rule retrieval itself failed (rare — the retriever is deterministic and does not normally throw).</summary>
    RetrievalFailure,

    /// <summary>The resolver model call failed (a provider error).</summary>
    ResolverFailure,

    /// <summary>The resolver returned something, but it could not be parsed or failed validation.</summary>
    MalformedGuidance
}

/// <summary>Tunable bounds for a consultation, recorded on every consultation trace so limits are visible.</summary>
public sealed record RulebookConsultationOptions(int MaxCards, int MaxInputChars, int OutputTokenLimit, bool CacheEnabled);

/// <summary>The result of one consultation, handed to the coordinator to bind (or to reject safely).</summary>
public sealed record RulebookConsultationResult
{
    public required string ConsultationId { get; init; }

    public required RulebookOutcome Outcome { get; init; }

    /// <summary>The validated guidance, or null on any failure.</summary>
    public RuleGuidance? Guidance { get; init; }

    /// <summary>The engine tools exposed to the Dungeon Master for this request, always including reject_action.</summary>
    public required IReadOnlyList<AITool> CandidateTools { get; init; }

    public required bool CacheHit { get; init; }

    public string? FailureDetail { get; init; }

    /// <summary>True when the DM should still be asked to bind an action; false when it should only reject.</summary>
    public bool HasSupportedGuidance => Outcome == RulebookOutcome.Supported && Guidance is { Supported: true };
}

/// <summary>
/// Runs the bounded rulebook-resolution stage for one intent, deterministically and automatically (every
/// take_action gets one — the Dungeon Master never decides whether it needs help). It retrieves a small
/// candidate set of rule cards, consults a cache, calls the stateless resolver on a miss, validates the
/// untrusted guidance, narrows the Dungeon Master's tool surface accordingly, and traces the whole
/// consultation. It fails safely: any failure yields a rejection-only tool surface rather than an invented
/// action, and the engine remains authoritative regardless.
/// </summary>
public sealed class RulebookConsultant
{
    private readonly IRuleRepository _repository;
    private readonly IRuleRetriever _retriever;
    private readonly IRulebookResolver _resolver;
    private readonly RuleGuidanceValidator _validator;
    private readonly IRuleGuidanceCache _cache;
    private readonly ExperimentTrace _trace;
    private readonly RulebookConsultationOptions _options;

    public RulebookConsultant(
        IRuleRepository repository,
        IRuleRetriever retriever,
        IRulebookResolver resolver,
        RuleGuidanceValidator validator,
        IRuleGuidanceCache cache,
        ExperimentTrace trace,
        RulebookConsultationOptions options)
    {
        _repository = repository;
        _retriever = retriever;
        _resolver = resolver;
        _validator = validator;
        _cache = cache;
        _trace = trace;
        _options = options;
    }

    public async Task<RulebookConsultationResult> ConsultAsync(
        string characterId, string characterName, string intent, CancellationToken cancellationToken)
    {
        var consultationId = $"rb-{Guid.NewGuid():N}";

        RuleRetrievalResult retrieval;
        try
        {
            retrieval = _retriever.Retrieve(intent);
        }
        catch (Exception ex)
        {
            return Fail(consultationId, characterId, characterName, intent, RulebookOutcome.RetrievalFailure,
                $"Rule retrieval failed: {ex.GetType().Name}: {ex.Message}",
                retrieval: null, resolver: null, cacheHit: false, guidance: null, validation: "n/a");
        }

        var resolverIdentity = $"{_resolver.Profile.Provider}:{_resolver.Profile.ModelId}";
        var cacheKey = RuleGuidanceCacheKey.Build(intent, retrieval.SelectedCards, resolverIdentity, RuleGuidanceSchema.Version);

        // Cache first: an identical intent over identical cards, model and schema reuses the abstract answer.
        if (_options.CacheEnabled && _cache.TryGet(cacheKey, out var cached))
        {
            var stampedCache = cached with { ConsultationId = consultationId };
            return Complete(consultationId, characterId, characterName, intent, retrieval, resolver: null,
                cacheHit: true, guidance: stampedCache, validation: "valid (cached)");
        }

        var resolverResult = await _resolver.ResolveAsync(intent, retrieval.SelectedCards, cancellationToken).ConfigureAwait(false);

        if (resolverResult.ModelCallFailed)
        {
            return Fail(consultationId, characterId, characterName, intent, RulebookOutcome.ResolverFailure,
                "The rulebook resolver model call failed.", retrieval, resolverResult, cacheHit: false,
                guidance: null, validation: "n/a");
        }

        if (resolverResult.Parsed is null)
        {
            return Fail(consultationId, characterId, characterName, intent, RulebookOutcome.MalformedGuidance,
                "The resolver response could not be parsed as guidance.", retrieval, resolverResult, cacheHit: false,
                guidance: null, validation: "invalid: unparseable");
        }

        var validation = _validator.Validate(resolverResult.Parsed);
        if (!validation.IsValid)
        {
            return Fail(consultationId, characterId, characterName, intent, RulebookOutcome.MalformedGuidance,
                validation.Reason, retrieval, resolverResult, cacheHit: false,
                guidance: null, validation: $"invalid: {validation.Reason}");
        }

        var guidance = validation.Guidance with { ConsultationId = consultationId };
        if (_options.CacheEnabled)
        {
            _cache.Set(cacheKey, guidance);
        }

        return Complete(consultationId, characterId, characterName, intent, retrieval, resolverResult,
            cacheHit: false, guidance: guidance, validation: "valid");
    }

    private RulebookConsultationResult Complete(
        string consultationId, string characterId, string characterName, string intent,
        RuleRetrievalResult retrieval, ResolverResult? resolver, bool cacheHit, RuleGuidance guidance, string validation)
    {
        var outcome = guidance.Supported ? RulebookOutcome.Supported : RulebookOutcome.Unsupported;
        var tools = ToolsFor(guidance);

        EmitConsultation(consultationId, characterId, characterName, intent, retrieval, resolver, cacheHit,
            guidance, validation, outcome, tools, failureDetail: null);

        return new RulebookConsultationResult
        {
            ConsultationId = consultationId,
            Outcome = outcome,
            Guidance = guidance,
            CandidateTools = tools,
            CacheHit = cacheHit
        };
    }

    private RulebookConsultationResult Fail(
        string consultationId, string characterId, string characterName, string intent,
        RulebookOutcome outcome, string? detail, RuleRetrievalResult? retrieval, ResolverResult? resolver,
        bool cacheHit, RuleGuidance? guidance, string validation)
    {
        // Fail safe: expose only the rejection so the DM cannot invent an action, and the engine stays authoritative.
        IReadOnlyList<AITool> tools = [DungeonMasterTools.RejectAction];

        EmitConsultation(consultationId, characterId, characterName, intent, retrieval, resolver, cacheHit,
            guidance, validation, outcome, tools, detail);

        return new RulebookConsultationResult
        {
            ConsultationId = consultationId,
            Outcome = outcome,
            Guidance = guidance,
            CandidateTools = tools,
            CacheHit = cacheHit,
            FailureDetail = detail
        };
    }

    /// <summary>The candidate DM tools for validated guidance: the cited candidate actions plus the rejection.</summary>
    private static IReadOnlyList<AITool> ToolsFor(RuleGuidance guidance)
    {
        var tools = new List<AITool>();
        if (guidance.Supported)
        {
            foreach (var action in guidance.CandidateActions)
            {
                if (DungeonMasterTools.EngineActionsByName.TryGetValue(action, out var declaration))
                {
                    tools.Add(declaration);
                }
            }
        }

        // Rejection is always available, and is the only tool for an unsupported intent.
        tools.Add(DungeonMasterTools.RejectAction);
        return tools;
    }

    private void EmitConsultation(
        string consultationId, string characterId, string characterName, string intent,
        RuleRetrievalResult? retrieval, ResolverResult? resolver, bool cacheHit, RuleGuidance? guidance,
        string validation, RulebookOutcome outcome, IReadOnlyList<AITool> tools, string? failureDetail)
    {
        var cards = retrieval?.SelectedCards ?? [];
        _trace.Emit(TraceEventType.RulebookConsultation, new RulebookConsultationPayload
        {
            ConsultationId = consultationId,
            ActingCharacterId = characterId,
            ActingCharacterName = characterName,
            RawIntent = intent,
            RulebookVersion = _repository.RulebookVersion,
            ConsideredRuleIds = retrieval?.ConsideredRuleIds ?? [],
            CardsSupplied = [.. cards.Select(c => $"{c.RuleId}@{c.Version}")],
            ResolverProvider = _resolver.Profile.Provider.ToString(),
            ResolverModel = _resolver.Profile.ModelId,
            ResolverParameters = ResolverParameters(),
            ResolverRequest = resolver?.RequestText ?? "(not called — cache hit or retrieval failure)",
            ResolverRawResponse = resolver?.RawResponse ?? "(not called — cache hit or retrieval failure)",
            ParsedGuidance = guidance,
            CitedRules = guidance is null ? [] : [.. guidance.CitedRules.Select(c => $"{c.RuleId}@{c.Version}")],
            ValidationOutcome = validation,
            CacheHit = cacheHit,
            InputTokens = resolver?.InputTokens,
            OutputTokens = resolver?.OutputTokens,
            LatencyMs = resolver?.LatencyMs ?? 0,
            DmCandidateTools = [.. tools.Select(t => t.Name)],
            Outcome = outcome.ToString(),
            FailureDetail = failureDetail,
            CardCount = cards.Count,
            CardInputChars = retrieval?.TotalInputChars ?? 0,
            TotalRequestChars = resolver?.RequestChars ?? (retrieval?.TotalInputChars ?? 0),
            MaxCardsConfigured = _options.MaxCards,
            MaxInputCharsConfigured = _options.MaxInputChars,
            OutputTokenLimitConfigured = _options.OutputTokenLimit,
            Trimmed = retrieval?.Trimmed ?? false
        }, characterName);
    }

    private IReadOnlyDictionary<string, string> ResolverParameters()
    {
        var p = _resolver.Profile;
        var map = new Dictionary<string, string>();
        if (p.Temperature is { } t) map["temperature"] = t.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (p.TopP is { } tp) map["topP"] = tp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (p.MaxOutputTokens is { } mo) map["maxOutputTokens"] = mo.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (p.Seed is { } s) map["seed"] = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (p.Effort is { } e) map["effort"] = e.ToString();
        return map;
    }
}
