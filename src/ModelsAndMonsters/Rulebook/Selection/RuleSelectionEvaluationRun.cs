using System.Text;
using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Rulebook.Selection;

/// <summary>
/// The one-shot measurement behind <c>--rulebook-eval</c>: runs the labelled corpus through every strategy
/// and writes a markdown record of what each cost and what each lost.
/// </summary>
/// <remarks>
/// It is a measurement, not part of a run: it starts no encounter, touches no engine and writes nothing into
/// <c>runs/</c>. The compact-index strategy needs a model, so it is included only when one is supplied —
/// without it the offline strategies are still measured and the record says plainly which figures are
/// missing, rather than quietly reporting three strategies as though they were four.
/// </remarks>
public static class RuleSelectionEvaluationRun
{
    public static async Task<string> RunAsync(
        PromptLibrary prompts,
        AgentModelProfile? indexProfile,
        IChatClient? indexClient,
        int topK,
        CancellationToken cancellationToken,
        IRuleEmbedder? embedder = null,
        string? embedderName = null)
    {
        var catalog = new RuleCatalog();

        // Fail the eval before measuring anything if the corpus no longer covers the catalog — a card no case
        // requires, or an engine action no case routes to. This is what stops the v0.9/v0.10 staleness recurring.
        var coverage = RuleSelectionCoverage.Check(catalog);

        var evaluator = new RuleSelectionEvaluator(catalog, prompts);
        var measurements = new List<StrategyMeasurement>();

        measurements.Add(await evaluator
            .MeasureAsync("WholeRulebook (baseline, shipped)", new WholeRulebookSelector(catalog), cancellationToken)
            .ConfigureAwait(false));

        measurements.Add(await evaluator
            .MeasureAsync("Oracle (answer key — the ceiling, not a strategy)", new OracleRuleSelector(catalog), cancellationToken)
            .ConfigureAwait(false));

        measurements.Add(await evaluator
            .MeasureAsync("StructuredRouting (no declared family available)",
                new StructuredRoutingSelector(catalog, _ => null), cancellationToken)
            .ConfigureAwait(false));

        measurements.Add(await evaluator
            .MeasureAsync($"Embedding (offline trigram prototype, top-{topK})",
                new EmbeddingRuleSelector(catalog, new HashingRuleEmbedder(), topK), cancellationToken)
            .ConfigureAwait(false));

        if (embedder is not null)
        {
            var real = await evaluator
                .MeasureAsync($"Embedding ({embedderName ?? embedder.SpaceId}, top-{topK})",
                    new EmbeddingRuleSelector(catalog, embedder, topK), cancellationToken)
                .ConfigureAwait(false);

            // A provider that turned out to be unreachable falls back on every case. Reporting that as a
            // strategy measurement would be reporting the baseline under another name.
            if (real.Fallbacks < real.Cases)
            {
                measurements.Add(real);
            }
        }

        StrategyMeasurement? compact = null;
        StrategyMeasurement? routing = null;
        if (indexProfile is not null && indexClient is not null)
        {
            // The selection call is traced like any other model call, into a sink that goes nowhere: this is a
            // measurement, and a measurement must not write a run trace. Both model-backed selectors run on the
            // resolver's own profile, so the comparison is between STRATEGIES rather than between models.
            var trace = new ExperimentTrace("rulebook-eval", new NullTraceSink());
            var tracing = new TracingChatClient(indexClient, indexProfile, trace);

            var compactSelector = new CompactIndexSelector(indexProfile, tracing, catalog, cacheEnabled: false);
            compact = await evaluator
                .MeasureAsync($"CompactIndex ({indexProfile.Provider}:{indexProfile.ModelId})", compactSelector, cancellationToken)
                .ConfigureAwait(false);
            measurements.Add(compact);

            var routingSelector = new ActionRoutingSelector(indexProfile, tracing, catalog, cacheEnabled: false);
            routing = await evaluator
                .MeasureAsync($"ActionRouting ({indexProfile.Provider}:{indexProfile.ModelId})", routingSelector, cancellationToken)
                .ConfigureAwait(false);
            measurements.Add(routing);
        }

        return Render(catalog, coverage, measurements, compact, routing, indexProfile);
    }

    private static string Render(
        RuleCatalog catalog,
        RuleSelectionCoverage.Report coverage,
        IReadOnlyList<StrategyMeasurement> measurements,
        StrategyMeasurement? compact,
        StrategyMeasurement? routing,
        AgentModelProfile? indexProfile)
    {
        var baseline = measurements[0];
        var builder = new StringBuilder();

        builder.AppendLine("# Rulebook selection — measured comparison");
        builder.AppendLine();
        builder.AppendLine($"Rulebook: **{catalog.AllCards.Count}** cards, **{catalog.RulebookVersion}**.");
        builder.AppendLine($"Corpus: **{RuleSelectionCorpus.Cases.Count}** labelled cases across " +
                           $"**{RuleSelectionCorpus.Cases.Select(c => c.Category).Distinct().Count()}** families.");
        builder.AppendLine();
        builder.AppendLine(RuleSelectionEvaluator.RenderTable(measurements));
        builder.AppendLine();

        builder.AppendLine("## Reduction against the baseline");
        builder.AppendLine();
        builder.AppendLine("| Strategy | Avg total input tokens | Reduction |");
        builder.AppendLine("| --- | --- | --- |");
        foreach (var m in measurements)
        {
            var reduction = baseline.AverageTotalInputTokens <= 0
                ? 0
                : 1 - (m.AverageTotalInputTokens / baseline.AverageTotalInputTokens);
            builder.AppendLine($"| {m.Strategy} | {m.AverageTotalInputTokens:F0} | {reduction:P1} |");
        }

        builder.AppendLine();

        if (compact is null || routing is null)
        {
            builder.AppendLine(
                "> **The model-backed selectors (CompactIndex, ActionRouting) were not measured in this record.** " +
                "No model was supplied for the selection call, so their recall and real token cost are unknown " +
                "here and no figure has been invented for them. Re-run with a configured provider to fill this in.");
            builder.AppendLine();
        }

        if (indexProfile is not null && (compact is not null || routing is not null))
        {
            builder.AppendLine(
                $"CompactIndex and ActionRouting ran against **{indexProfile.Provider}:{indexProfile.ModelId}** " +
                $"(temperature {indexProfile.Temperature?.ToString() ?? "default"}), one selection call per case. " +
                $"Selection step averaged **{compact?.AverageSelectionLatencyMs ?? 0:F0} ms** (CompactIndex) and " +
                $"**{routing?.AverageSelectionLatencyMs ?? 0:F0} ms** (ActionRouting).");
            builder.AppendLine();
        }

        var failures = measurements
            .Where(m => m.CasesMissingRequiredCards.Count > 0)
            .ToList();
        if (failures.Count > 0)
        {
            builder.AppendLine("## Cases that lost a required rule");
            builder.AppendLine();
            foreach (var m in failures)
            {
                builder.AppendLine($"- **{m.Strategy}**: {string.Join(", ", m.CasesMissingRequiredCards)}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("## Corpus clarity distribution");
        builder.AppendLine();
        builder.AppendLine(
            "Evidence for the on-demand strategy: how often an intent is clear-cut enough that a Dungeon " +
            "Master holding only lean core rules would not need to look anything up.");
        builder.AppendLine();
        foreach (var group in RuleSelectionCorpus.Cases.GroupBy(c => c.Clarity).OrderBy(g => g.Key))
        {
            var share = (double)group.Count() / RuleSelectionCorpus.Cases.Count;
            builder.AppendLine($"- {group.Key}: **{group.Count()}** ({share:P0})");
        }

        builder.AppendLine();
        builder.Append(RuleSelectionCoverage.Render(coverage));
        AppendCardExpansionCallout(builder, measurements);
        AppendFallbackSplit(builder, measurements);
        if (routing is not null)
        {
            AppendActionLabelAccuracy(builder, routing);
        }

        AppendNamedHardCases(builder, measurements);

        return builder.ToString();
    }

    /// <summary>
    /// Task 6a — the number that decides the strategy. Avg cards after expansion, against Oracle's ceiling and
    /// the whole-rulebook baseline. At 5–7 the routing works; at 15+ the book is effectively irreducible.
    /// </summary>
    private static void AppendCardExpansionCallout(StringBuilder builder, IReadOnlyList<StrategyMeasurement> measurements)
    {
        builder.AppendLine("## Avg cards after expansion");
        builder.AppendLine();
        builder.AppendLine(
            "The number that decides a narrowing strategy: how many cards the resolver ends up reading. The " +
            "whole-rulebook baseline sends every card; the Oracle is the ceiling a perfect selection reaches. " +
            "A routing strategy earns its place at 5–7; at 15+ the exclusions are dense enough that the book is " +
            "effectively irreducible — a clean negative, to be read as one.");
        builder.AppendLine();
        builder.AppendLine("| Strategy | Avg cards after expansion |");
        builder.AppendLine("| --- | --- |");
        foreach (var m in measurements)
        {
            builder.AppendLine($"| {m.Strategy} | {m.AverageCardsSupplied:F1} |");
        }

        builder.AppendLine();
    }

    /// <summary>Task 6b — fallbacks split semantic (the boundaries) versus mechanical (the machinery).</summary>
    private static void AppendFallbackSplit(StringBuilder builder, IReadOnlyList<StrategyMeasurement> measurements)
    {
        builder.AppendLine("## Fallbacks by kind");
        builder.AppendLine();
        builder.AppendLine(
            "A fallback is the safety net firing — the whole rulebook sent rather than a guess. SEMANTIC means " +
            "the model judged the intent unclear or named an action outside the set (the boundaries are wrong); " +
            "MECHANICAL means the reply could not be parsed or the call threw (the transport is broken). The two " +
            "demand opposite responses and must never read as one number.");
        builder.AppendLine();
        builder.AppendLine("| Strategy | Total | Semantic | Mechanical |");
        builder.AppendLine("| --- | --- | --- | --- |");
        foreach (var m in measurements)
        {
            builder.AppendLine($"| {m.Strategy} | {m.Fallbacks}/{m.Cases} | {m.SemanticFallbacks} | {m.MechanicalFallbacks} |");
        }

        builder.AppendLine();
    }

    /// <summary>
    /// Task 6c — did the router name the right primary action? Reported as accuracy plus a confusion list of the
    /// pairs it actually got wrong, which is the actionable output (a confusable pair points at the boundary to
    /// write). Only meaningful for a selector that routes by action.
    /// </summary>
    private static void AppendActionLabelAccuracy(StringBuilder builder, StrategyMeasurement routing)
    {
        builder.AppendLine("## Action-label accuracy (ActionRouting)");
        builder.AppendLine();

        var labelled = routing.CaseOutcomes.Where(o => o.ExpectedRoutingAction is not null).ToList();
        var correct = labelled.Count(o =>
            string.Equals(o.ActualActionLabel, o.ExpectedRoutingAction, StringComparison.OrdinalIgnoreCase));
        var unclear = labelled.Count(o => o.ActualActionLabel is null);

        builder.AppendLine(
            $"Of **{labelled.Count}** cases with an expected routing action, the model named the right one on " +
            $"**{correct}** ({(labelled.Count == 0 ? 0 : (double)correct / labelled.Count):P0}). " +
            $"**{unclear}** were routed to no action (the model said unclear, or the reply did not parse) and " +
            "fell back to the whole rulebook.");
        builder.AppendLine();

        var confusions = labelled
            .Where(o => o.ActualActionLabel is not null
                        && !string.Equals(o.ActualActionLabel, o.ExpectedRoutingAction, StringComparison.OrdinalIgnoreCase))
            .GroupBy(o => (Expected: o.ExpectedRoutingAction!, Actual: o.ActualActionLabel!))
            .OrderByDescending(g => g.Count())
            .ToList();

        if (confusions.Count == 0)
        {
            builder.AppendLine("No confusions: every case the model committed to, it routed to the expected action.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("Confused pairs (expected → named), most frequent first — each points at a boundary to write:");
        builder.AppendLine();
        builder.AppendLine("| Expected | Named | Count | Cases |");
        builder.AppendLine("| --- | --- | --- | --- |");
        foreach (var group in confusions)
        {
            builder.AppendLine(
                $"| {group.Key.Expected} | {group.Key.Actual} | {group.Count()} | {string.Join(", ", group.Select(o => o.Id))} |");
        }

        builder.AppendLine();
    }

    /// <summary>
    /// Task 6d — the named hard cases, pass/fail per strategy. These are the cases the whole exercise exists to
    /// recover; an aggregate that hides them is the wrong instrument. Pass = every required card supplied.
    /// </summary>
    private static void AppendNamedHardCases(StringBuilder builder, IReadOnlyList<StrategyMeasurement> measurements)
    {
        string[] hardCases =
        [
            // The originals.
            "accept-surrender", "use-item-vs-heal-ability", "steal-vs-attack-compound", "intimidate",
            "threat-with-a-blow", "plain-coordination",
            // The new contrasts.
            "defend-vs-take-cover", "damage-object-vs-attack", "leave-cover-vs-exposing-action",
            "demand-vs-offer", "unsupported-force-container"
        ];

        builder.AppendLine("## Named hard cases");
        builder.AppendLine();
        builder.AppendLine("Pass = every required card was supplied. ✓ pass, ✗ a required card was lost.");
        builder.AppendLine();

        builder.Append("| Case |");
        foreach (var m in measurements)
        {
            builder.Append(' ').Append(ShortName(m.Strategy)).Append(" |");
        }

        builder.AppendLine();
        builder.Append("| --- |");
        foreach (var _ in measurements)
        {
            builder.Append(" --- |");
        }

        builder.AppendLine();

        foreach (var caseId in hardCases)
        {
            builder.Append("| ").Append(caseId).Append(" |");
            foreach (var m in measurements)
            {
                var outcome = m.CaseOutcomes.FirstOrDefault(o => o.Id == caseId);
                var mark = outcome is null ? "–" : outcome.MissingRequired ? "✗" : "✓";
                builder.Append(' ').Append(mark).Append(" |");
            }

            builder.AppendLine();
        }

        builder.AppendLine();
    }

    /// <summary>A short strategy name for a column header — the leading word before any parenthesis.</summary>
    private static string ShortName(string strategy)
    {
        var paren = strategy.IndexOf('(');
        return (paren > 0 ? strategy[..paren] : strategy).Trim();
    }

    /// <summary>A trace sink that keeps nothing. The measurement traces its calls so nothing is silent, and drops them.</summary>
    private sealed class NullTraceSink : ITraceSink
    {
        public void Write(TraceEvent traceEvent)
        {
        }

        public void Dispose()
        {
        }
    }
}
