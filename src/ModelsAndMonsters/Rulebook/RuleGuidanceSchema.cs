namespace ModelsAndMonsters.Rulebook;

/// <summary>
/// The response-schema contract for the Rulebook Resolver. Kept in one place so its version participates in
/// the cache key: if the schema changes, cached guidance produced under the old schema is not reused.
/// </summary>
public static class RuleGuidanceSchema
{
    /// <summary>The schema version, part of every cache key.</summary>
    /// <remarks>
    /// v2 stopped asking the resolver to copy the cited card's bindings, preconditions, turn cost,
    /// randomness, visibility and behaviours back into its reply. The consultant is holding those cards, so
    /// it fills them itself — see <c>RulebookConsultant.Hydrate</c>. Bumped so guidance cached under v1
    /// (which carried a model's paraphrase of those fields) is never reused as though it were the card.
    /// </remarks>
    public const string Version = "guidance-v2";

    /// <summary>The schema, described in the plain JSON shape the resolver is asked to return.</summary>
    public const string Description = """
        Respond with ONLY a single JSON object (no prose, no markdown fences) of exactly this shape, and
        NOTHING else — no bindings, no preconditions, no behaviours. Do not copy the card back:
        {
          "supported": true | false,
          "candidateActions": ["<engine action name>", ...],   // the action(s) the intent could bind to; one, or a few if genuinely ambiguous; empty if unsupported
          "citedRules": [ { "ruleId": "<id>", "version": "<version>" }, ... ],  // the rules you relied on, with the exact version shown on each card; empty if unsupported
          "unsupportedReason": "..."         // only when supported is false; otherwise omit or null
        }
        Every supported recommendation MUST cite at least one rule id and version copied exactly from the cards
        supplied. Only use action names that appear on the supplied cards. You judge abstract rules only — you
        do not see the live encounter, so never decide whether the specific action is currently valid, never
        invent an action that is not on a card, and never decide an outcome.

        Your whole answer is a few lines. The rules themselves are read from the cards you cited, not from
        anything you write, so writing them out changes nothing and risks running out of room mid-answer.
        """;
}
