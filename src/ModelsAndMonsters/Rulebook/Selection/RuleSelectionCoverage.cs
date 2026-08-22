using System.Text;
using ModelsAndMonsters.Agents;

namespace ModelsAndMonsters.Rulebook.Selection;

/// <summary>
/// Checks, every eval run, that the labelled corpus still covers the catalog: every card is required by some
/// case (or explicitly allow-listed), and every engine action is routed to by some case. Fails the eval on a
/// gap rather than warning.
/// </summary>
/// <remarks>
/// The v0.9/v0.10 staleness happened silently across two releases: four cards and a batch of action rules
/// landed with no case, and nothing said so. A warning would have been ignored for two releases because it
/// effectively was. The same discipline as <c>RulebookMaxCards</c> throwing at startup: adding a card, or an
/// action, without a case becomes impossible.
/// </remarks>
public static class RuleSelectionCoverage
{
    /// <summary>What the corpus covers, for the report. Only produced when coverage is complete — a gap throws.</summary>
    public sealed record Report(
        string LabelledHash,
        string CurrentHash,
        bool HashMatches,
        IReadOnlyDictionary<string, string> AllowListedCards,
        int CardsCovered,
        int ActionsCovered);

    public static Report Check(IRuleRepository catalog) =>
        CheckCoverage(catalog, RuleSelectionCorpus.Cases, RuleSelectionCorpus.IntentionallyUncoveredRuleIds);

    /// <summary>
    /// The coverage check with its corpus inputs made explicit, so a test can drive an uncovered card or an
    /// uncovered action without the shipped corpus being complete. <see cref="Check"/> passes the real corpus.
    /// </summary>
    public static Report CheckCoverage(
        IRuleRepository catalog,
        IReadOnlyList<RuleSelectionCase> cases,
        IReadOnlyDictionary<string, string> allowList)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(allowList);

        var required = cases
            .SelectMany(c => c.RequiredRuleIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var uncoveredCards = catalog.AllCards
            .Select(c => c.RuleId)
            .Where(id => !required.Contains(id) && !allowList.ContainsKey(id))
            .ToList();

        var routed = cases
            .Select(c => c.RoutingTarget)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var uncoveredActions = DungeonMasterTools.EngineActionsByName.Keys
            .Where(a => !routed.Contains(a))
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

        if (uncoveredCards.Count > 0 || uncoveredActions.Count > 0)
        {
            var parts = new List<string>();
            if (uncoveredCards.Count > 0)
            {
                parts.Add($"cards no case requires: {string.Join(", ", uncoveredCards)}");
            }

            if (uncoveredActions.Count > 0)
            {
                parts.Add($"engine actions no case routes to: {string.Join(", ", uncoveredActions)}");
            }

            throw new InvalidOperationException(
                "The rule-selection corpus does not cover the catalog — " + string.Join("; ", parts) + ". " +
                "Add a labelled case for each, or, for a card that genuinely cannot be exercised as a required " +
                "card, an explicit entry in RuleSelectionCorpus.IntentionallyUncoveredRuleIds with a reason. " +
                "The eval fails on a gap rather than warning, so a card or action can never again be added " +
                "across releases without the corpus being brought with it.");
        }

        var current = catalog.RulebookVersion;
        return new Report(
            RuleSelectionCorpus.LabelledAgainstRulebookVersion,
            current,
            string.Equals(RuleSelectionCorpus.LabelledAgainstRulebookVersion, current, StringComparison.Ordinal),
            allowList,
            catalog.AllCards.Count,
            DungeonMasterTools.EngineActionsByName.Count);
    }

    /// <summary>Renders the coverage report as the markdown section the measurements document carries.</summary>
    public static string Render(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("## Corpus coverage");
        builder.AppendLine();
        builder.AppendLine(
            $"All **{report.CardsCovered}** cards are required by a case (or allow-listed), and all " +
            $"**{report.ActionsCovered}** engine actions are routed to by a case. The eval fails otherwise.");
        builder.AppendLine();

        if (report.HashMatches)
        {
            builder.AppendLine(
                $"Corpus labelled against **{report.LabelledHash}**, which matches the live catalog.");
        }
        else
        {
            builder.AppendLine(
                $"> **The corpus was labelled against `{report.LabelledHash}`, but the live catalog is " +
                $"`{report.CurrentHash}`.** The figures below describe the live catalog; re-verify the corpus " +
                "labels against it and bump `RuleSelectionCorpus.LabelledAgainstRulebookVersion`.");
        }

        builder.AppendLine();
        if (report.AllowListedCards.Count > 0)
        {
            builder.AppendLine("Cards no case requires, on purpose:");
            builder.AppendLine();
            foreach (var (ruleId, reason) in report.AllowListedCards)
            {
                builder.AppendLine($"- `{ruleId}` — {reason}");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }
}
