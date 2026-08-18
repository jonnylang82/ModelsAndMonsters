using System.Text.RegularExpressions;

namespace ModelsAndMonsters.Agents;

/// <summary>
/// Detects when text breaks the fiction by describing the harness's framing — the engine and rules, what
/// "can be resolved", the character's own knowledge bookkeeping ("you directly know", "you have not been
/// told"), or by using answer/document formatting (bold, bullets) rather than spoken words.
/// </summary>
/// <remarks>
/// The Dungeon Master's system prompt forbids this, but a local model does not reliably obey it. Across
/// live runs its refusals said things like "the world cannot resolve that" and listed the supported
/// actions, and — with a weaker instruct model as DM (granite4.1) — its <em>answers</em> parroted the
/// knowledge-view scaffolding back verbatim ("Rowan directly knows… he has not been told…", state words
/// like "**CLOSED**" in bold). A prompt alone cannot guarantee it, so both rejection reasons and question
/// answers are checked against this detector before they reach a character, and rephrased in-world when it
/// trips.
/// </remarks>
public static partial class MachineryLanguage
{
    /// <summary>
    /// True when the text names the machinery (engine/rules/what can be resolved), narrates the character's
    /// knowledge state, or uses markdown formatting — anything a person in the world would never say.
    /// </summary>
    public static bool IsLeak(string? text) =>
        !string.IsNullOrWhiteSpace(text) && MachineryPattern().IsMatch(text);

    // Focused on phrases and markers that only ever describe the framing, so an ordinary in-world line
    // ("there is nowhere to back away to", "the goblin looks wounded") never trips it. Erring toward
    // catching is cheap — a false positive only costs one unnecessary rephrase — and false positives are
    // kept rare by matching framing-specific wording, not everyday words. Three groups:
    //   1. the machinery — the engine, the rules, what the world can/can't resolve;
    //   2. knowledge bookkeeping — the info-view language a weak DM parrots into an answer;
    //   3. formatting — bold markers, which a spoken reply never contains.
    [GeneratedRegex(
        @"the world can|the world has no way|no way to resolve|to be resolved|be resolved by|can be resolved|only resolve|not supported|unsupported|isn't supported|the engine\b|the game (engine|system|rules)|what the world can|the world's rules|action the world" +
        // Further machinery variants seen leaking through refusals: "not a supported action", "resolve the
        // encounter", enumerated permitted-action framings.
        @"|supported action|permitted action|allowed action|resolve the (?:encounter|combat|fight|battle|situation)|list of (?:actions|moves|things you can)" +
        @"|directly knows?|has not been told|have not been told|no one has told|nor has anyone told|has not inspected|have not inspected|has not observed|have not observed" +
        @"|\*\*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MachineryPattern();
}
