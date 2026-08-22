using System.Text;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Prompts;

namespace ModelsAndMonsters.Rulebook.Selection;

/// <summary>What one strategy scored over the whole labelled corpus.</summary>
public sealed record StrategyMeasurement
{
    public required string Strategy { get; init; }

    public required int Cases { get; init; }

    /// <summary>
    /// The share of required cards that were actually supplied, averaged per case. 1.0 means no case ever
    /// lost a rule the decision needed.
    /// </summary>
    public required double RequiredCardRecall { get; init; }

    /// <summary>How many cases got EVERY required card. The figure that matters: a case is right or it is not.</summary>
    public required int CasesWithEveryRequiredCard { get; init; }

    /// <summary>Cases where the modelled resolver decision matched the label.</summary>
    public required int DecisionAgreements { get; init; }

    /// <summary>Cases the modelled resolver called supported that the label says are not.</summary>
    public required int IncorrectlySupported { get; init; }

    /// <summary>Cases the modelled resolver called unsupported that the label says are supported.</summary>
    public required int IncorrectlyUnsupported { get; init; }

    public required double AverageCardsSupplied { get; init; }

    /// <summary>Estimated resolver input tokens per consultation: system prompt, request template, intent and cards.</summary>
    public required double AverageResolverInputTokens { get; init; }

    /// <summary>Estimated tokens the SELECTION step itself spent, per consultation. 0 for a strategy that makes no call.</summary>
    public required double AverageSelectionInputTokens { get; init; }

    /// <summary>Total estimated input tokens per consultation, selection and resolution together.</summary>
    public double AverageTotalInputTokens => AverageResolverInputTokens + AverageSelectionInputTokens;

    /// <summary>Model calls per consultation, counting the selection call and the resolution call.</summary>
    public required double ModelCallsPerConsultation { get; init; }

    /// <summary>Measured wall-clock for the selection step only, per consultation. 0 for a strategy that makes no call.</summary>
    public required double AverageSelectionLatencyMs { get; init; }

    public required int Fallbacks { get; init; }

    /// <summary>Fallbacks where the model understood the format but the answer could not narrow safely (unclear, or an action outside the set). The boundaries.</summary>
    public required int SemanticFallbacks { get; init; }

    /// <summary>Fallbacks where the reply could not be parsed or the call threw. The machinery.</summary>
    public required int MechanicalFallbacks { get; init; }

    /// <summary>The ids of the cases where a required card was missing, so a failure can be looked at rather than averaged away.</summary>
    public required IReadOnlyList<string> CasesMissingRequiredCards { get; init; }

    /// <summary>Per-case outcome, so the report can build the action-label confusion table and the named-hard-case grid.</summary>
    public required IReadOnlyList<CaseOutcome> CaseOutcomes { get; init; }

    public double DecisionAgreementRate => Cases == 0 ? 0 : (double)DecisionAgreements / Cases;

    public double FullRecallRate => Cases == 0 ? 0 : (double)CasesWithEveryRequiredCard / Cases;
}

/// <summary>What one strategy did on one labelled case — enough to read a confusion table and a hard-case grid.</summary>
public sealed record CaseOutcome
{
    public required string Id { get; init; }

    public required string Category { get; init; }

    /// <summary>The action a router was expected to name (the case's <see cref="RuleSelectionCase.RoutingTarget"/>).</summary>
    public required string? ExpectedRoutingAction { get; init; }

    /// <summary>The action the strategy actually named, or null for a strategy that does not route by action.</summary>
    public required string? ActualActionLabel { get; init; }

    /// <summary>True when a required card was not supplied for this case.</summary>
    public required bool MissingRequired { get; init; }

    public required bool FellBack { get; init; }

    public required RuleSelectionFallbackKind? FallbackKind { get; init; }
}

/// <summary>
/// Runs the labelled corpus through one or more selection strategies and measures what each costs and what
/// each loses.
/// </summary>
/// <remarks>
/// <para>
/// The resolver is MODELLED rather than called, and the model is deliberately generous: a perfect resolver
/// that is constrained only by what it was shown. Given the card that governs the act, it names the right
/// action; without it, it can only answer unsupported, because it cannot cite a card it never saw. That
/// isolates exactly the variable under test — what selection costs in correctness — instead of measuring the
/// resolver model's mood on the day. It also means every agreement figure here is an UPPER BOUND: a real
/// resolver can do no better than one that is right whenever it was shown the right card.
/// </para>
/// <para>
/// Token figures are estimated from characters at the harness's own ratio, and cover the whole resolver
/// request — system prompt, request template, intent and cards — so they are comparable with the provider's
/// reported input sizes rather than being a card-only number that flatters every strategy equally.
/// </para>
/// </remarks>
public sealed class RuleSelectionEvaluator
{
    private readonly IRuleRepository _repository;
    private readonly int _fixedResolverOverheadChars;
    private readonly Func<string, string> _renderResolverRequest;

    public RuleSelectionEvaluator(IRuleRepository repository, PromptLibrary prompts)
    {
        _repository = repository;

        // The exact fixed cost of a resolver request, taken from the real templates rather than guessed.
        var systemPrompt = prompts.Render("rulebook.resolver.system", new Dictionary<string, string?>
        {
            ["schema"] = RuleGuidanceSchema.Description
        });
        _fixedResolverOverheadChars = systemPrompt.Length;
        _renderResolverRequest = cards => prompts.Render("rulebook.resolve", new Dictionary<string, string?>
        {
            ["intent"] = "",
            ["cards"] = cards
        });
    }

    /// <summary>Runs the whole corpus through one strategy.</summary>
    public async Task<StrategyMeasurement> MeasureAsync(
        string name, IRuleSelector selector, CancellationToken cancellationToken = default)
    {
        var cases = RuleSelectionCorpus.Cases;

        double recallSum = 0, cardsSum = 0, resolverTokensSum = 0, selectionTokensSum = 0, latencySum = 0;
        int fullRecall = 0, agreements = 0, wronglySupported = 0, wronglyUnsupported = 0, fallbacks = 0, calls = 0;
        int semanticFallbacks = 0, mechanicalFallbacks = 0;
        var missing = new List<string>();
        var outcomes = new List<CaseOutcome>();

        foreach (var labelled in cases)
        {
            var selection = await selector.SelectAsync(labelled.Intent, cancellationToken).ConfigureAwait(false);
            var supplied = selection.Cards.Select(c => c.RuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var present = labelled.RequiredRuleIds.Count(id => supplied.Contains(id));
            var missingThis = present != labelled.RequiredRuleIds.Count;
            recallSum += (double)present / labelled.RequiredRuleIds.Count;
            if (!missingThis)
            {
                fullRecall++;
            }
            else
            {
                missing.Add(labelled.Id);
            }

            var decision = ModelResolverDecision(labelled, supplied);
            if (string.Equals(decision, labelled.ExpectedAction, StringComparison.OrdinalIgnoreCase))
            {
                agreements++;
            }
            else if (decision is not null && labelled.ExpectedAction is null)
            {
                wronglySupported++;
            }
            else if (decision is null && labelled.ExpectedAction is not null)
            {
                wronglyUnsupported++;
            }

            cardsSum += selection.Cards.Count;
            resolverTokensSum += EstimateResolverTokens(labelled.Intent, selection.Cards);
            selectionTokensSum += EstimateSelectionTokens(selector, labelled.Intent, selection);
            latencySum += selection.LatencyMs;
            calls += selection.ModelCalls + 1; // the selection call, if any, plus the resolver call itself
            if (selection.FellBack)
            {
                fallbacks++;
                switch (selection.FallbackKind)
                {
                    case RuleSelectionFallbackKind.Semantic:
                        semanticFallbacks++;
                        break;
                    case RuleSelectionFallbackKind.Mechanical:
                        mechanicalFallbacks++;
                        break;
                }
            }

            outcomes.Add(new CaseOutcome
            {
                Id = labelled.Id,
                Category = labelled.Category,
                ExpectedRoutingAction = labelled.RoutingTarget,
                ActualActionLabel = selection.PrimaryActionLabel,
                MissingRequired = missingThis,
                FellBack = selection.FellBack,
                FallbackKind = selection.FallbackKind
            });
        }

        var n = cases.Count;
        return new StrategyMeasurement
        {
            Strategy = name,
            Cases = n,
            RequiredCardRecall = recallSum / n,
            CasesWithEveryRequiredCard = fullRecall,
            DecisionAgreements = agreements,
            IncorrectlySupported = wronglySupported,
            IncorrectlyUnsupported = wronglyUnsupported,
            AverageCardsSupplied = cardsSum / n,
            AverageResolverInputTokens = resolverTokensSum / n,
            AverageSelectionInputTokens = selectionTokensSum / n,
            ModelCallsPerConsultation = (double)calls / n,
            AverageSelectionLatencyMs = latencySum / n,
            Fallbacks = fallbacks,
            SemanticFallbacks = semanticFallbacks,
            MechanicalFallbacks = mechanicalFallbacks,
            CasesMissingRequiredCards = missing,
            CaseOutcomes = outcomes
        };
    }

    /// <summary>
    /// The modelled resolver decision: the labelled action when the card governing the act was supplied, and
    /// null (unsupported) otherwise. An unsupported case answers unsupported however good the selection was —
    /// its required cards are the ones needed to REFUSE it for the right reason, which the recall figure
    /// measures separately.
    /// </summary>
    private static string? ModelResolverDecision(RuleSelectionCase labelled, IReadOnlySet<string> supplied) =>
        labelled.ExpectedAction is not null && supplied.Contains(labelled.PrimaryRuleId)
            ? labelled.ExpectedAction
            : null;

    private double EstimateResolverTokens(string intent, IReadOnlyList<RuleCard> cards)
    {
        var cardText = string.Join("\n\n", cards.Select(c => c.ToResolverBlock()));
        var request = _renderResolverRequest(cardText).Length + intent.Length;
        return ContextTruncation.EstimateTokensForCharacters(_fixedResolverOverheadChars + request);
    }

    /// <summary>
    /// What the SELECTION step itself read, for a strategy that reads anything. The compact index is the only
    /// one that costs input tokens; embedding and routing read no prompt at all.
    /// </summary>
    private static double EstimateSelectionTokens(IRuleSelector selector, string intent, RuleSelection selection)
    {
        if (selection.InputTokens is { } reported)
        {
            return reported;
        }

        return selector switch
        {
            CompactIndexSelector index => ContextTruncation.EstimateTokensForCharacters(
                CompactIndexSelector.SystemPrompt.Length + index.Index.Length + intent.Length + RequestScaffoldChars),
            ActionRoutingSelector router => ContextTruncation.EstimateTokensForCharacters(
                ActionRoutingSelector.SystemPrompt.Length + router.Index.Length + intent.Length + RequestScaffoldChars),
            _ => 0
        };
    }

    private const int RequestScaffoldChars = 200;

    /// <summary>Renders a set of measurements as the markdown table the findings document carries.</summary>
    public static string RenderTable(IReadOnlyList<StrategyMeasurement> measurements)
    {
        var builder = new StringBuilder();
        builder.AppendLine("| Strategy | Required-card recall | Cases with every required card | Decision agreement | Wrongly supported | Wrongly unsupported | Avg cards | Avg resolver tokens | Avg selection tokens | Model calls | Fallbacks |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var m in measurements)
        {
            builder.AppendLine(
                $"| {m.Strategy} | {m.RequiredCardRecall:P1} | {m.CasesWithEveryRequiredCard}/{m.Cases} | " +
                $"{m.DecisionAgreements}/{m.Cases} ({m.DecisionAgreementRate:P0}) | {m.IncorrectlySupported} | " +
                $"{m.IncorrectlyUnsupported} | {m.AverageCardsSupplied:F1} | {m.AverageResolverInputTokens:F0} | " +
                $"{m.AverageSelectionInputTokens:F0} | {m.ModelCallsPerConsultation:F1} | {m.Fallbacks}/{m.Cases} |");
        }

        return builder.ToString();
    }
}

/// <summary>
/// A selector that returns exactly the cards each labelled case declares it needs.
/// </summary>
/// <remarks>
/// This is NOT a strategy and must never be configured for a run — it reads the answer key. It exists to
/// measure the CEILING: the token cost a perfect selection would reach, which is the number every real
/// strategy should be judged against. Quoting a strategy's saving without it would make an easy corpus look
/// like a good strategy.
/// </remarks>
public sealed class OracleRuleSelector : IRuleSelector
{
    private readonly IRuleRepository _repository;
    private readonly Dictionary<string, IReadOnlyList<string>> _byIntent;

    public OracleRuleSelector(IRuleRepository repository)
    {
        _repository = repository;
        _byIntent = RuleSelectionCorpus.Cases.ToDictionary(
            c => c.Intent, c => c.RequiredRuleIds, StringComparer.Ordinal);
    }

    public RuleSelectionMode Mode => RuleSelectionMode.StructuredRouting;

    public Task<RuleSelection> SelectAsync(string intent, CancellationToken cancellationToken) =>
        Task.FromResult(_byIntent.TryGetValue(intent, out var ids)
            ? RuleSelectionSupport.Build(_repository, Mode, ids, ["labelled requirement (answer key)"])
            : RuleSelectionSupport.Fallback(_repository, Mode, "no label for this intent", RuleSelectionFallbackKind.Semantic));
}
