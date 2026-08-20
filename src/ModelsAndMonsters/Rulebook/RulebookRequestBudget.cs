using ModelsAndMonsters.AI;
using ModelsAndMonsters.Prompts;

namespace ModelsAndMonsters.Rulebook;

/// <summary>
/// Checks, at startup, that a whole-rulebook resolver request actually fits the resolver model's context
/// window with room left to answer in.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the failure it catches is silent and looks like something else. A request that fills
/// the window does not error: the provider accepts it, generates until the window is full, and returns
/// <c>FinishReason: length</c> with a handful of tokens. Downstream that surfaces as unparseable JSON, then
/// as malformed guidance, then as an in-world refusal of a perfectly ordinary action — four steps from the
/// cause. In a v0.8 live run the resolver's request reached ~8,065 tokens against an 8,192-token window,
/// leaving about 127 tokens to reply in, and 5 of 57 consultations died this way.
/// </para>
/// <para>
/// The existing <see cref="RuleRetriever"/> ceilings guard the same class of problem against a number
/// somebody typed (<c>RulebookMaxInputChars</c>). This guards it against the number that actually decides
/// it — the model's own window — and fails the same way, loudly at construction, because a rulebook that
/// cannot be answered from is a configuration error and not a runtime condition to limp through.
/// </para>
/// </remarks>
public static class RulebookRequestBudget
{
    /// <summary>
    /// Characters of intent assumed on top of the cards. Real intents run to a couple of hundred characters;
    /// this is deliberately generous so the check fails before a long one does.
    /// </summary>
    public const int AssumedIntentChars = 600;

    /// <summary>
    /// The smallest reply worth reserving room for. Post-hydration replies measured 69–132 tokens, so this
    /// is roughly double the worst observed — enough that an ambiguous intent citing two rules, or an
    /// unsupported answer carrying a reason, still has somewhere to go.
    /// </summary>
    public const int MinimumOutputReserveTokens = 250;

    /// <summary>What a whole-rulebook request costs, and whether it fits.</summary>
    public sealed record Estimate(int RequestTokens, int ContextWindowTokens, int OutputReserveTokens)
    {
        /// <summary>Tokens left to generate into after the request. Negative means the request alone overflows.</summary>
        public int HeadroomTokens => ContextWindowTokens - RequestTokens;

        public bool Fits => HeadroomTokens >= OutputReserveTokens;
    }

    /// <summary>
    /// Measures a whole-rulebook request against the resolver's window. Returns null when nothing actually
    /// bounds the request — a hosted model whose real window the harness does not know is not something to
    /// guess about.
    /// </summary>
    /// <remarks>
    /// It reads <see cref="AgentModelProfile.BindingContextWindow"/> rather than the configured
    /// <c>ContextWindow</c> for the reason the summary gives. A hosted resolver inheriting the Ollama-tuned
    /// 8,192 would otherwise be measured against a ceiling its provider never applies, and this guard stops
    /// the run before it starts — refusing a request the model would have answered without difficulty.
    /// </remarks>
    public static Estimate? Measure(
        IRuleRepository repository, PromptLibrary prompts, AgentModelProfile profile, int configuredOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.BindingContextWindow is not { } window || window <= 0)
        {
            return null;
        }

        var systemPrompt = prompts.Render("rulebook.resolver.system", new Dictionary<string, string?>
        {
            ["schema"] = RuleGuidanceSchema.Description
        });

        var cards = string.Join("\n\n", repository.AllCards.Select(c => c.ToResolverBlock()));
        var request = prompts.Render("rulebook.resolve", new Dictionary<string, string?>
        {
            ["intent"] = new string('x', AssumedIntentChars),
            ["cards"] = cards
        });

        var tokens = ContextTruncation.EstimateTokensForCharacters(systemPrompt.Length + request.Length);
        var reserve = Math.Max(MinimumOutputReserveTokens, configuredOutputTokens);
        return new Estimate(tokens, window, reserve);
    }

    /// <summary>
    /// Throws when the whole rulebook cannot be sent to this resolver with room to answer. Does nothing when
    /// the window is unknown, or when it fits.
    /// </summary>
    public static void Validate(
        IRuleRepository repository, PromptLibrary prompts, AgentModelProfile profile, int configuredOutputTokens)
    {
        if (Measure(repository, prompts, profile, configuredOutputTokens) is not { Fits: false } estimate)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The rulebook does not fit the Rulebook Resolver's context window. A whole-rulebook request is " +
            $"~{estimate.RequestTokens} tokens against a {estimate.ContextWindowTokens}-token window, leaving " +
            $"{estimate.HeadroomTokens} to reply in, and a reply needs at least {estimate.OutputReserveTokens}. " +
            "The provider will NOT error on this — it returns a truncated reply that surfaces as malformed " +
            "guidance and an in-world refusal, so it is caught here instead. Shorten the rule cards, lower " +
            "RulebookOutputTokens, or select fewer cards per consultation (Harness:RulebookSelectionMode). " +
            "Raising the resolver's ContextWindow is the wrong fix: it forces a separate Ollama runner with " +
            "its own full copy of the model weights.");
    }
}
