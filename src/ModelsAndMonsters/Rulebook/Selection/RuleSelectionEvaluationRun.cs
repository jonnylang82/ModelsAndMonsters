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
        if (indexProfile is not null && indexClient is not null)
        {
            // The selection call is traced like any other model call, into a sink that goes nowhere: this is a
            // measurement, and a measurement must not write a run trace.
            var trace = new ExperimentTrace("rulebook-eval", new NullTraceSink());
            var tracing = new TracingChatClient(indexClient, indexProfile, trace);
            var selector = new CompactIndexSelector(indexProfile, tracing, catalog, cacheEnabled: false);
            compact = await evaluator
                .MeasureAsync($"CompactIndex ({indexProfile.Provider}:{indexProfile.ModelId})", selector, cancellationToken)
                .ConfigureAwait(false);
            measurements.Add(compact);
        }

        return Render(catalog, measurements, compact, indexProfile);
    }

    private static string Render(
        RuleCatalog catalog,
        IReadOnlyList<StrategyMeasurement> measurements,
        StrategyMeasurement? compact,
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

        if (compact is null)
        {
            builder.AppendLine(
                "> **CompactIndex was not measured in this record.** No model was supplied for the selection " +
                "call, so its recall and its real token cost are unknown here and no figure has been invented " +
                "for it. Re-run with a configured provider to fill this in.");
            builder.AppendLine();
        }
        else if (indexProfile is not null)
        {
            builder.AppendLine(
                $"CompactIndex ran against **{indexProfile.Provider}:{indexProfile.ModelId}** " +
                $"(temperature {indexProfile.Temperature?.ToString() ?? "default"}), one selection call per case, " +
                $"averaging **{compact.AverageSelectionLatencyMs:F0} ms** for the selection step.");
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
        return builder.ToString();
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
