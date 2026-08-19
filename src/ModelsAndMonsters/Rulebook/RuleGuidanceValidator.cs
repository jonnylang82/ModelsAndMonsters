using ModelsAndMonsters.Agents;

namespace ModelsAndMonsters.Rulebook;

/// <summary>The outcome of validating untrusted resolver guidance: whether it is usable, why not, and the sanitised form.</summary>
public sealed record GuidanceValidation(bool IsValid, string? Reason, RuleGuidance Guidance);

/// <summary>
/// Validates the resolver's guidance before it is allowed to influence the Dungeon Master. Guidance is
/// untrusted advisory input: its shape, bounds and cited rule ids/versions are checked, candidate actions are
/// filtered to real engine tools, and cited rules are filtered to ones that actually exist at the cited
/// version. A supported recommendation that survives with no real candidate action or no valid citation is
/// rejected as malformed, so a confidently wrong resolver cannot smuggle in an unsupported or unknown action.
/// </summary>
public sealed class RuleGuidanceValidator
{
    private const int MaxCandidateActions = 6;

    private readonly IRuleRepository _repository;

    public RuleGuidanceValidator(IRuleRepository repository) => _repository = repository;

    public GuidanceValidation Validate(RuleGuidance guidance)
    {
        // Candidate actions must be real engine tools. Unknown names are dropped rather than trusted.
        var candidates = guidance.CandidateActions
            .Where(a => !string.IsNullOrWhiteSpace(a) && DungeonMasterTools.EngineActionsByName.ContainsKey(a.Trim()))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidateActions)
            .ToList();

        // Cited rules must exist at the exact version the resolver claims to have seen.
        var citations = guidance.CitedRules
            .Where(c => !string.IsNullOrWhiteSpace(c.RuleId) && _repository.IsValid(c.RuleId, c.Version))
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

        if (candidates.Count == 0)
        {
            return new GuidanceValidation(false, "Guidance claimed support but named no known engine action.", sanitised);
        }

        if (citations.Count == 0)
        {
            return new GuidanceValidation(false, "Guidance claimed support but cited no rule that exists at the stated version.", sanitised);
        }

        return new GuidanceValidation(true, null, sanitised);
    }
}
