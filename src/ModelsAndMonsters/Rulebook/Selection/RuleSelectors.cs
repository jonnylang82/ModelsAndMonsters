namespace ModelsAndMonsters.Rulebook.Selection;

/// <summary>
/// Shared machinery for every strategy: turning a set of chosen ids into a card list, expanding it through
/// declared related-rule links, and building the whole-rulebook fallback.
/// </summary>
/// <remarks>
/// The expansion lives here rather than in each strategy so that no strategy can accidentally skip it. It
/// follows declared links exactly once — a card's own links, not the links of the cards those pull in —
/// which keeps the expanded set small and bounded rather than transitively closing over most of the book.
/// </remarks>
public static class RuleSelectionSupport
{
    /// <summary>
    /// Builds a selection from a set of directly chosen ids: expands through declared links, always includes
    /// the rejection card, and returns the cards in catalog order.
    /// </summary>
    public static RuleSelection Build(
        IRuleRepository repository,
        RuleSelectionMode mode,
        IReadOnlyList<string> selectedIds,
        IReadOnlyList<string> reasons,
        int modelCalls = 0,
        long? inputTokens = null,
        long? outputTokens = null,
        double latencyMs = 0,
        IReadOnlyList<string>? droppedLabels = null,
        IReadOnlyList<string>? linkExpandOnly = null)
    {
        var direct = new List<string>();
        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in selectedIds)
        {
            if (repository.Find(id) is { } card && chosen.Add(card.RuleId))
            {
                direct.Add(card.RuleId);
            }
        }

        // Declared related rules, one hop. This is what stops a confident top-1 answering "escape" without
        // the rule that says the door has to be opened first. When linkExpandOnly is given, only those ids
        // follow their links — a selector can expand its PRIMARY pick without every also-ran (a ruled-out
        // action, say) dragging in its own related closure and fanning the set out to the whole book.
        var expandFrom = linkExpandOnly is null
            ? null
            : new HashSet<string>(linkExpandOnly, StringComparer.OrdinalIgnoreCase);
        var expanded = new List<string>();
        foreach (var id in direct.ToList())
        {
            if (expandFrom is not null && !expandFrom.Contains(id))
            {
                continue;
            }

            foreach (var related in repository.Find(id)!.RelatedRuleIds)
            {
                if (repository.Find(related) is { } card && chosen.Add(card.RuleId))
                {
                    expanded.Add(card.RuleId);
                }
            }
        }

        // The refusal is always available, or an unsupported intent has no card to be refused under.
        if (chosen.Add(repository.RejectCard.RuleId))
        {
            expanded.Add(repository.RejectCard.RuleId);
        }

        return new RuleSelection
        {
            Mode = mode,
            Cards = [.. repository.AllCards.Where(c => chosen.Contains(c.RuleId))],
            DirectlySelectedRuleIds = direct,
            ExpandedRuleIds = expanded,
            SelectionReasons = reasons,
            ModelCalls = modelCalls,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            LatencyMs = latencyMs,
            DroppedLabels = droppedLabels ?? []
        };
    }

    /// <summary>The whole bounded rulebook, marked as a fallback with the reason it was needed and its kind.</summary>
    public static RuleSelection Fallback(
        IRuleRepository repository,
        RuleSelectionMode mode,
        string reason,
        RuleSelectionFallbackKind kind,
        int modelCalls = 0,
        long? inputTokens = null,
        long? outputTokens = null,
        double latencyMs = 0,
        IReadOnlyList<string>? droppedLabels = null) => new()
        {
            Mode = mode,
            Cards = repository.AllCards,
            DirectlySelectedRuleIds = [.. repository.AllCards.Select(c => c.RuleId)],
            FellBack = true,
            FallbackReason = reason,
            FallbackKind = kind,
            ModelCalls = modelCalls,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            LatencyMs = latencyMs,
            DroppedLabels = droppedLabels ?? []
        };

    /// <summary>
    /// The compact index: one line per card, in catalog order, each carrying the card's own exclusions — the
    /// boundary as well as the purpose.
    /// </summary>
    /// <remarks>
    /// The exclusions are here as a deliberate control (v0.10 investigation Task 1): a summary line tells a
    /// model what a card is FOR and says much less about what it is NOT, and the cases a compact selection
    /// loses are exactly the ones whose decision turns on a card the intent must be told APART from — the theft
    /// card behind an acceptance, the cover card behind a brace. Putting the boundary in front of the model
    /// costs selection tokens; whether it buys back that recall is measured in
    /// <c>reports/rulebook-efficiency.md</c>. It is kept present for every model-read index so the only variable
    /// between the compact-index and action-routing selectors is the routing indirection, not the exclusions.
    /// </remarks>
    public static string BuildIndex(IRuleRepository repository) =>
        string.Join("\n", repository.AllCards.Select(c => c.ToIndexLine(includeExclusions: true)));
}

/// <summary>
/// The current production path: send the whole (small) rulebook every time and let the resolver select.
/// </summary>
/// <remarks>
/// It is the baseline every other strategy is measured against, and it is the fallback every other strategy
/// drops to. It cannot hide a card, it makes no model call of its own, and its cost is a constant — which is
/// exactly the property that makes it a safe floor and exactly the cost the investigation is trying to beat.
/// </remarks>
public sealed class WholeRulebookSelector : IRuleSelector
{
    private readonly IRuleRepository _repository;

    public WholeRulebookSelector(IRuleRepository repository) => _repository = repository;

    public RuleSelectionMode Mode => RuleSelectionMode.WholeRulebook;

    public Task<RuleSelection> SelectAsync(string intent, CancellationToken cancellationToken) =>
        Task.FromResult(new RuleSelection
        {
            Mode = Mode,
            Cards = _repository.AllCards,
            DirectlySelectedRuleIds = [.. _repository.AllCards.Select(c => c.RuleId)],
            SelectionReasons = ["the whole rulebook is sent; the resolver does all the selection"]
        });
}

/// <summary>
/// Selects by DECLARED action-to-rule metadata, for a caller that already knows which action family it is
/// asking about.
/// </summary>
/// <remarks>
/// <para>
/// This is not, and must never become, a way of guessing an action family from an intent. It reads no
/// language at all: it is given an action name and returns the cards declared against it, expanded through
/// their declared links. It exists for the on-demand <c>check_rules</c> shape, where the Dungeon Master is
/// already holding a candidate action and wants the detail for it.
/// </para>
/// <para>
/// Given no known family — which is the ordinary pre-adjudication case — it falls back to the whole
/// rulebook, because it has nothing to go on and guessing is precisely what it is forbidden to do.
/// </para>
/// </remarks>
public sealed class StructuredRoutingSelector : IRuleSelector
{
    private readonly IRuleRepository _repository;
    private readonly Func<string, string?> _declaredActionFor;

    /// <param name="declaredActionFor">
    /// Supplies the action family the caller already knows for this request, or null when it knows none.
    /// A caller that would have to READ the intent to answer must return null.
    /// </param>
    public StructuredRoutingSelector(IRuleRepository repository, Func<string, string?> declaredActionFor)
    {
        _repository = repository;
        _declaredActionFor = declaredActionFor;
    }

    public RuleSelectionMode Mode => RuleSelectionMode.StructuredRouting;

    public Task<RuleSelection> SelectAsync(string intent, CancellationToken cancellationToken)
    {
        var action = _declaredActionFor(intent);
        if (string.IsNullOrWhiteSpace(action))
        {
            return Task.FromResult(RuleSelectionSupport.Fallback(_repository, Mode,
                "no action family was declared for this request, and the router never infers one from language",
                RuleSelectionFallbackKind.Semantic));
        }

        var matches = _repository.AllCards
            .Where(c => string.Equals(c.ActionName, action, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.RuleId)
            .ToList();

        if (matches.Count == 0)
        {
            return Task.FromResult(RuleSelectionSupport.Fallback(_repository, Mode,
                $"no card is declared against action '{action}'", RuleSelectionFallbackKind.Semantic));
        }

        return Task.FromResult(RuleSelectionSupport.Build(_repository, Mode, matches,
            [$"declared route: action '{action}' -> {string.Join(", ", matches)}"]));
    }
}
