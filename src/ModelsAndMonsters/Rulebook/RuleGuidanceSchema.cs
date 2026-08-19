namespace ModelsAndMonsters.Rulebook;

/// <summary>
/// The response-schema contract for the Rulebook Resolver. Kept in one place so its version participates in
/// the cache key: if the schema changes, cached guidance produced under the old schema is not reused.
/// </summary>
public static class RuleGuidanceSchema
{
    /// <summary>The schema version, part of every cache key.</summary>
    public const string Version = "guidance-v1";

    /// <summary>The schema, described in the plain JSON shape the resolver is asked to return.</summary>
    public const string Description = """
        Respond with ONLY a single JSON object (no prose, no markdown fences) of exactly this shape:
        {
          "supported": true | false,
          "candidateActions": ["<engine action name>", ...],   // the action(s) the intent could bind to; one, or a few if genuinely ambiguous; empty if unsupported
          "citedRules": [ { "ruleId": "<id>", "version": "<version>" }, ... ],  // the rules you relied on, with the exact version shown on each card; empty if unsupported
          "requiredBindings": ["..."],       // what the Dungeon Master must fill from state (from the cited rule)
          "preconditions": ["..."],          // abstract preconditions the engine will check
          "turnCost": "...",                 // e.g. "consumes the turn"
          "rngSpecification": "...",         // e.g. "one seeded draw" or "none"
          "visibility": "...",               // who learns what
          "successBehaviour": "...",
          "failureBehaviour": "...",
          "unsupportedReason": "..."         // only when supported is false; otherwise omit or null
        }
        Every supported recommendation MUST cite at least one rule id and version copied exactly from the cards
        supplied. Only use action names that appear on the supplied cards. You judge abstract rules only — you
        do not see the live encounter, so never decide whether the specific action is currently valid, never
        invent an action that is not on a card, and never decide an outcome.
        """;
}
