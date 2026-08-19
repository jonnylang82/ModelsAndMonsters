using System.Security.Cryptography;
using System.Text;

namespace ModelsAndMonsters.Rulebook;

/// <summary>
/// One small, self-contained, versioned rule card — the abstract rules for a single action, moved out of the
/// Dungeon Master's system prompt so the DM prompt can stay a compact constitution.
/// </summary>
/// <remarks>
/// A card describes an action <em>in the abstract</em>: what it is, how the intent is recognised, the bindings
/// it needs, its preconditions, its turn cost, its RNG requirement, its visibility, and what it does on
/// success and failure. It carries no live encounter state, no character, no hidden knowledge and no RNG
/// outcome — it is the kind of thing a real rulebook would say, independent of any particular fight. The
/// <see cref="Version"/> is a content hash so a card is independently versionable and any edit changes its
/// version (which in turn invalidates cached guidance that cited the old version).
/// </remarks>
public sealed record RuleCard
{
    /// <summary>Stable rule id, e.g. "inventory.steal" or "combat.attack".</summary>
    public required string RuleId { get; init; }

    /// <summary>The engine action this card governs (a Dungeon Master tool name), or "reject_action" for the generic refusal card.</summary>
    public required string ActionName { get; init; }

    /// <summary>A concise description of what the action is. This is what the resolver reads to recognise the intent — there are no keyword tags routing to the card.</summary>
    public required string Description { get; init; }

    /// <summary>The bindings the Dungeon Master must fill from authoritative state to submit the action.</summary>
    public required IReadOnlyList<string> RequiredBindings { get; init; }

    /// <summary>The abstract preconditions that must hold (checked authoritatively by the engine, not the resolver).</summary>
    public required IReadOnlyList<string> Preconditions { get; init; }

    /// <summary>Whether the action consumes the actor's turn.</summary>
    public required string TurnCost { get; init; }

    /// <summary>The randomness the action requires, if any.</summary>
    public required string RngRequirement { get; init; }

    /// <summary>Who learns what when the action resolves.</summary>
    public required string Visibility { get; init; }

    /// <summary>What happens on success.</summary>
    public required string SuccessBehaviour { get; init; }

    /// <summary>What happens on failure.</summary>
    public required string FailureBehaviour { get; init; }

    /// <summary>Variants explicitly not supported, so the resolver can recognise and refuse them.</summary>
    public required IReadOnlyList<string> Exclusions { get; init; }

    /// <summary>
    /// A short content hash over every field except the version itself, so a card is independently
    /// versionable and any change to its text yields a new version. Computed on access (not cached in a
    /// field) so that a modified copy made with <c>with</c> reports its new content's version, not the
    /// original's — the cost is a small hash over short text, computed only a handful of times per request.
    /// </summary>
    public string Version => ComputeVersion();

    private string ComputeVersion()
    {
        var builder = new StringBuilder();
        builder.Append(RuleId).Append('\n');
        builder.Append(ActionName).Append('\n');
        builder.Append(Description).Append('\n');
        builder.AppendJoin("|", RequiredBindings).Append('\n');
        builder.AppendJoin("|", Preconditions).Append('\n');
        builder.Append(TurnCost).Append('\n');
        builder.Append(RngRequirement).Append('\n');
        builder.Append(Visibility).Append('\n');
        builder.Append(SuccessBehaviour).Append('\n');
        builder.Append(FailureBehaviour).Append('\n');
        builder.AppendJoin("|", Exclusions);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "v1-" + Convert.ToHexStringLower(hash)[..8];
    }

    /// <summary>Renders the card as a compact block for the resolver's request. Contains no live state.</summary>
    public string ToPromptBlock()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"RULE {RuleId} (version {Version}) — action: {ActionName}");
        builder.AppendLine($"  what it is: {Description}");
        builder.AppendLine($"  required bindings: {Join(RequiredBindings)}");
        builder.AppendLine($"  preconditions: {Join(Preconditions)}");
        builder.AppendLine($"  turn cost: {TurnCost}");
        builder.AppendLine($"  rng: {RngRequirement}");
        builder.AppendLine($"  visibility: {Visibility}");
        builder.AppendLine($"  on success: {SuccessBehaviour}");
        builder.AppendLine($"  on failure: {FailureBehaviour}");
        builder.AppendLine($"  not supported: {Join(Exclusions)}");
        return builder.ToString().TrimEnd();
    }

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "none" : string.Join("; ", values);
}
