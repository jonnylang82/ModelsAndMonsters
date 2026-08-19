namespace ModelsAndMonsters.Rulebook;

/// <summary>One rule the resolver cited to support its recommendation: the rule id and the exact version it saw.</summary>
public readonly record struct CitedRule(string RuleId, string Version);

/// <summary>
/// The structured, abstract rule guidance the Rulebook Resolver returns for one intent. It is advisory only:
/// it answers "what does the rulebook say about this kind of action", never "is this specific action valid
/// right now" — the engine remains authoritative. It is treated as untrusted input and validated (shape,
/// bounds, cited rule ids and versions) before it is allowed to influence the Dungeon Master.
/// </summary>
public sealed record RuleGuidance
{
    /// <summary>Stamped by the consultant per consultation, not chosen by the model (so a cache hit still gets a fresh id).</summary>
    public string ConsultationId { get; init; } = "";

    /// <summary>Whether the rulebook supports resolving this kind of intent at all.</summary>
    public bool Supported { get; init; }

    /// <summary>The candidate engine actions the intent could bind to — usually one, a small set when genuinely ambiguous.</summary>
    public IReadOnlyList<string> CandidateActions { get; init; } = [];

    /// <summary>The rules cited to support the recommendation, each with the version the resolver was shown.</summary>
    public IReadOnlyList<CitedRule> CitedRules { get; init; } = [];

    public IReadOnlyList<string> RequiredBindings { get; init; } = [];

    public IReadOnlyList<string> Preconditions { get; init; } = [];

    public string TurnCost { get; init; } = "";

    public string RngSpecification { get; init; } = "";

    public string Visibility { get; init; } = "";

    public string SuccessBehaviour { get; init; } = "";

    public string FailureBehaviour { get; init; } = "";

    /// <summary>When unsupported, which part of the intent the rulebook cannot resolve. Null when supported.</summary>
    public string? UnsupportedReason { get; init; }
}
