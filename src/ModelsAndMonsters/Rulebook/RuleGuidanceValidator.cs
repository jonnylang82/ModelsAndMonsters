using ModelsAndMonsters.Agents;

namespace ModelsAndMonsters.Rulebook;

/// <summary>The outcome of validating untrusted resolver guidance: whether it is usable, why not, and the sanitised form.</summary>
public sealed record GuidanceValidation(bool IsValid, string? Reason, RuleGuidance Guidance);

/// <summary>
/// Validates the resolver's guidance before it is allowed to influence the Dungeon Master. Guidance is
/// untrusted advisory input: its shape and bounds are checked, cited rules are filtered to ones that exist at
/// the cited version AND were actually supplied for this request, and candidate actions are filtered to real
/// engine tools that at least one surviving citation supports. A supported recommendation that survives with
/// nothing left is rejected as malformed, so a confidently wrong resolver cannot smuggle in an unsupported,
/// unknown or unsupported-by-its-own-citation action.
/// </summary>
/// <remarks>
/// The correspondence check exists because it was missing and something got through. A live probe produced
/// <c>candidateActions: ["give_item"]</c> citing <c>combat.attack</c> — a real engine tool, and a real card at
/// a real version, but a card whose action is <c>attack_character</c>. Both lists passed independently and
/// nothing asked whether they had anything to do with each other, so the Dungeon Master was handed
/// <c>give_item</c> as its only tool on the strength of a citation that did not support it. It happened to be
/// the right answer; that is luck, not validation.
/// </remarks>
public sealed class RuleGuidanceValidator
{
    private const int MaxCandidateActions = 6;

    private readonly IRuleRepository _repository;

    public RuleGuidanceValidator(IRuleRepository repository) => _repository = repository;

    /// <param name="suppliedCards">
    /// The cards actually sent to the resolver for this request. When given, a citation of anything else is
    /// dropped — which is what turns the resolver's "use ONLY these" instruction from a request into a rule.
    /// Null means "not known", and only the catalog-existence check applies; under the whole-rulebook path
    /// the two are the same set, which is exactly why this gap stayed invisible until a selection strategy
    /// sent a subset.
    /// </param>
    public GuidanceValidation Validate(RuleGuidance guidance, IReadOnlyList<RuleCard>? suppliedCards = null)
    {
        var supplied = suppliedCards is null
            ? null
            : suppliedCards.Select(c => c.RuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Cited rules must name a real card the resolver was actually shown. When the supplied set is KNOWN
        // (the live path always passes it), a cited rule that is in that set was demonstrably put in front of
        // the resolver, so its current version is authoritative and the version STRING the model wrote is not
        // trusted character-for-character: a model transcribing an 8-hex-char hash occasionally fat-fingers a
        // digit (measured live: inventory.steal cited @…45aff99f for the real …45aff90f, container.take
        // @…4102bc90 for the real …4102bc93), and rejecting a correct action over a one-character version typo
        // on a rule that WAS supplied cost ~1% of live consultations. The citation is repaired to the card's
        // real version rather than demanded verbatim. A rule that was NOT supplied is still dropped, so the
        // "use ONLY these" guarantee is untouched. When the supplied set is UNKNOWN (null — no live caller does
        // this), we cannot confirm the rule was shown, so the exact-version guard is kept as the conservative
        // choice. Checked FIRST, because the candidate actions are then checked against these.
        var citations = guidance.CitedRules
            .Select(c => (Cited: c, Card: _repository.Find(c.RuleId)))
            .Where(x => x.Card is not null
                        && (supplied is null
                            ? string.Equals(x.Card!.Version, x.Cited.Version, StringComparison.Ordinal)
                            : supplied.Contains(x.Card!.RuleId)))
            .Select(x => new CitedRule(x.Card!.RuleId, x.Card.Version))
            .Distinct()
            .ToList();

        // The actions those surviving citations actually govern. A background card (morale) governs none, so
        // it can support no candidate — which is correct: it is context, not an action.
        var supportedActions = citations
            .Select(c => _repository.Find(c.RuleId))
            .Where(c => c is not null && !c.IsReference)
            .Select(c => c!.ActionName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Candidate actions must be real engine tools AND supported by something cited. Unknown names and
        // unsupported ones are dropped rather than trusted.
        var named = guidance.CandidateActions
            .Where(a => !string.IsNullOrWhiteSpace(a) && DungeonMasterTools.EngineActionsByName.ContainsKey(a.Trim()))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var candidates = named
            .Where(a => supportedActions.Contains(a))
            .Take(MaxCandidateActions)
            .ToList();

        var sanitised = guidance with { CandidateActions = candidates, CitedRules = citations };

        if (!guidance.Supported)
        {
            // An unsupported recommendation needs no citation; ensure it carries a reason for the DM to voice.
            return new GuidanceValidation(true, null, sanitised with
            {
                UnsupportedReason = string.IsNullOrWhiteSpace(guidance.UnsupportedReason)
                    ? "The rulebook has no action that resolves this intent."
                    : guidance.UnsupportedReason
            });
        }

        if (citations.Count == 0)
        {
            return new GuidanceValidation(false,
                "Guidance claimed support but cited no rule that was supplied at the stated version.", sanitised);
        }

        if (candidates.Count == 0)
        {
            // Distinguish the two ways this happens, because they mean different things about the resolver:
            // an unknown action name is a model inventing a tool, while a real name no citation supports is a
            // model reasoning past the cards it was given.
            return new GuidanceValidation(false, named.Count == 0
                ? "Guidance claimed support but named no known engine action."
                : $"Guidance named {string.Join(", ", named)} but cited no rule that governs it.", sanitised);
        }

        return new GuidanceValidation(true, null, sanitised);
    }
}
