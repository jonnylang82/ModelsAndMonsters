using ModelsAndMonsters.AI;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Rulebook.Selection;

/// <summary>
/// The single place that decides what a configured <see cref="RuleSelectionMode"/> actually builds.
/// </summary>
/// <remarks>
/// It exists because it was duplicated once and the copies disagreed. The <c>--rulebook-probe</c> diagnostic
/// built its consultant by hand with no selector at all, so it reported whole-rulebook routing no matter what
/// <c>RulebookSelectionMode</c> said — a diagnostic quietly answering a different question than the one asked
/// of it, which is worse than having no diagnostic. Both the runner and the probe now come through here, so
/// they cannot drift apart again.
/// </remarks>
public static class RuleSelectorFactory
{
    /// <summary>
    /// Builds the configured strategy, or null for the shipped path of sending the whole bounded rulebook
    /// every time.
    /// </summary>
    /// <param name="createClient">
    /// Supplies a traced chat client for a strategy that needs one. Only <see cref="RuleSelectionMode.CompactIndex"/>
    /// calls it, so a caller with no model available can pass one that throws.
    /// </param>
    /// <remarks>
    /// An unrecognised mode is treated as the default rather than throwing: a typo in configuration should
    /// leave a run on the safe, proven path, not stop it.
    /// </remarks>
    public static IRuleSelector? Create(
        HarnessOptions harness,
        ProvidersOptions providers,
        IRuleRepository catalog,
        AgentModelProfile resolverProfile,
        Func<AgentModelProfile, TracingChatClient> createClient)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(catalog);

        if (!Enum.TryParse<RuleSelectionMode>(harness.RulebookSelectionMode, ignoreCase: true, out var mode))
        {
            return null;
        }

        return mode switch
        {
            RuleSelectionMode.CompactIndex => new CompactIndexSelector(
                resolverProfile,
                createClient(resolverProfile),
                catalog,
                harness.RulebookSelectionCacheEnabled),

            // Intent -> engine action -> cards. Like CompactIndex it runs on the resolver's own profile and
            // makes one stateless, cached selection call; unlike it, the model reads the action surface, not
            // the rulebook, and an invalid label is unrepresentable where the provider constrains decoding.
            RuleSelectionMode.ActionRouting => new ActionRoutingSelector(
                resolverProfile,
                createClient(resolverProfile),
                catalog,
                harness.RulebookSelectionCacheEnabled),

            // A blank embedding model falls back to the offline trigram prototype, which is a lexical
            // measure and NOT a semantic one — enough to exercise top-K, declared-link expansion and the
            // confidence fallback, and not enough to draw a conclusion from.
            RuleSelectionMode.Embedding => new EmbeddingRuleSelector(
                catalog,
                string.IsNullOrWhiteSpace(harness.RulebookEmbeddingModel)
                    ? new HashingRuleEmbedder()
                    : new OllamaRuleEmbedder(providers.Ollama.Endpoint, harness.RulebookEmbeddingModel),
                harness.RulebookSelectionTopK),

            // Routing needs an action family the caller already knows. Ordinary pre-adjudication consultation
            // knows none — that is the whole question being asked — so it declares none and the selector
            // correctly falls back rather than inferring one from the intent's words.
            RuleSelectionMode.StructuredRouting => new StructuredRoutingSelector(catalog, _ => null),

            _ => null
        };
    }
}
