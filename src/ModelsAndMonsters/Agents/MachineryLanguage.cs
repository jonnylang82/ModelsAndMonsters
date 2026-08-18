using System.Text.RegularExpressions;

namespace ModelsAndMonsters.Agents;

/// <summary>
/// Detects when text describes the harness's machinery — the engine, the rules, or what "can be
/// resolved" — rather than the fiction.
/// </summary>
/// <remarks>
/// The Dungeon Master's system prompt forbids this, but a local model does not reliably obey it: across
/// several live runs its refusals still said things like "the world cannot resolve that" and even listed
/// the supported actions. A prompt alone cannot guarantee it, so a rejection reason is checked against
/// this detector before it reaches a character, and rephrased in-world when it trips.
/// </remarks>
public static partial class MachineryLanguage
{
    /// <summary>True when the text names the world's rules/engine or what it can or cannot resolve.</summary>
    public static bool IsLeak(string? text) =>
        !string.IsNullOrWhiteSpace(text) && MachineryPattern().IsMatch(text);

    // Focused on phrases that only ever describe the machinery, so an ordinary in-world refusal ("there is
    // nowhere to back away to", "your arms have no strength left") never trips it. Erring toward catching
    // is cheap here — a false positive only costs one unnecessary rephrase — but false positives are kept
    // rare by matching machinery-specific wording, not everyday words.
    [GeneratedRegex(
        @"the world can|the world has no way|no way to resolve|to be resolved|be resolved by|can be resolved|only resolve|not supported|unsupported|isn't supported|the engine\b|the game (engine|system|rules)|what the world can|the world's rules|action the world",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MachineryPattern();
}
