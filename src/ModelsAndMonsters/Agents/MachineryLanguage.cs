using System.Text.RegularExpressions;

namespace ModelsAndMonsters.Agents;

/// <summary>
/// A final defensive lint over text about to be spoken to a character, matching only vocabulary that belongs
/// to the machine and could not occur in the fiction: tool names, stable identifiers, and the harness's own
/// nouns for itself (the engine, the rulebook, a hit chance).
/// </summary>
/// <remarks>
/// <para>
/// This used to be the primary means of keeping refusals and answers in the fiction, and it grew a synonym at
/// a time — "not allowed", "another character", "the facts", "a living ally", "item transfer" — because every
/// run found a phrasing it did not have. That approach cannot converge: the space of ways to describe a rule
/// in English is unbounded, while the space of phrases it could wrongly catch in ordinary fiction is not.
/// Broad matches also cost more than they saved. Each catch triggered a rephrasing model call, and a rewrite
/// is free to be worse than what it replaced — live runs produced refusals that invented obstacles which were
/// not true and narrated events inside a refusal that by definition changes nothing.
/// </para>
/// <para>
/// The fix was structural, not lexical. Engine refusals now render deterministically from the rejection code
/// and its bound facts (<see cref="ModelsAndMonsters.Engine.InWorldRefusal"/>), so the common paths are
/// in-world by construction and never reach this class at all. Answers are already bounded by the
/// <c>AnswerFacts</c> projection. What remains here is a lint for the one path still made of model prose —
/// the Dungeon Master's own <c>reject_action</c> wording — and it now matches only terms that are
/// unmistakably machine: an ordinary sentence of fiction can no longer trip it by accident.
/// </para>
/// <para>
/// Formatting is deliberately <em>not</em> handled here. Markdown in a spoken line is a presentation defect
/// with a deterministic fix, so it is cleaned by <see cref="ModelText.StripPresentationMarkup"/> rather than
/// triggering a rewrite of the words themselves.
/// </para>
/// </remarks>
public static partial class MachineryLanguage
{
    /// <summary>
    /// True when the text contains vocabulary that only exists inside the harness — a tool name, a stable
    /// id, or one of the machine's own nouns. Never true for ordinary in-world prose.
    /// </summary>
    public static bool IsLeak(string? text) =>
        !string.IsNullOrWhiteSpace(text) && MachineryPattern().IsMatch(text);

    // Three closed groups, all of them things that exist only in the implementation:
    //   1. the harness's nouns for itself and its numbers;
    //   2. stable identifiers, which are generated and can never occur in speech;
    //   3. tool names, which are snake_case and equally impossible in speech.
    // Every entry here is a term with no in-world meaning. Nothing is included because it *often* signals a
    // leak — that judgement is what made the old list grow without ever becoming reliable.
    [GeneratedRegex(
        // 1. The machine talking about itself. "Rulebook" and "engine" are its own names; "hit chance",
        // "world version", "status effect", "turn cost" and "disposition" are its own quantities, none of
        // which any person in a cellar has a word for.
        @"\brulebook\b|\bthe engine\b|\bgame engine\b|\bhit chance\b|\bworld version\b|\bstatus effect\b" +
        @"|\bturn cost\b|\bdisposition\b|\bd100\b|\battack roll\b|\bdamage roll\b|\bglancing roll\b" +
        // v0.8 adds its own quantities. "Fear" is a perfectly ordinary word and is NOT banned; a fear
        // SCORE, LEVEL or VALUE is the harness naming its own number, and nobody in a cellar has a word for
        // "double damage" either. "Critical" is deliberately absent: a critical blow is a thing a person can
        // see, and banning the word would push narration into worse phrasings for no gain.
        @"|\bquality roll\b|\bfear (?:score|level|value|points?)\b|\bscared status\b" +
        @"|\b(?:double|half|full) damage\b" +
        // 2. Stable identifiers: the counter ids the harness mints, the ability ids, and the rulebook hash.
        @"|\b(?:offer|agreement|status|guard|intimidation)-\d+\b|\bcorpse-[a-z0-9-]+\b|\brulebook-[0-9a-f]{6,}\b" +
        @"|\bguard-ally\b|\bhealing-prayer\b|\brally-grunt\b|\bdirty-strike\b" +
        // 3. Tool names. snake_case is not a thing anybody says out loud.
        @"|\b(?:attack_character|use_item|use_ability|open_container|take_item|inspect_object|open_exit" +
        @"|escape_encounter|offer_surrender|accept_surrender|give_item|drop_item|steal_item|reject_action" +
        @"|intimidate_character|steady_ally|ask_dm|take_action|end_turn)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MachineryPattern();
}
