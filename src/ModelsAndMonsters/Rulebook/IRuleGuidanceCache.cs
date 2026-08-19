using System.Text;

namespace ModelsAndMonsters.Rulebook;

/// <summary>
/// A small cache for abstract rule guidance, keyed strictly enough that a stale or differently-versioned
/// answer is never reused. It stores only abstract guidance — never live game-state decisions, target
/// validity, item ownership, hidden knowledge, RNG outcomes or any encounter-specific bindings.
/// </summary>
public interface IRuleGuidanceCache
{
    bool TryGet(string key, out RuleGuidance guidance);

    void Set(string key, RuleGuidance guidance);
}

/// <summary>
/// Builds the cache key. It includes everything that could change the abstract answer: the normalized intent,
/// the exact rule ids and versions retrieved, the resolver model/profile identity and the response-schema
/// version. Any change to a rule card's version, the schema or the model therefore misses the old entry.
/// </summary>
public static class RuleGuidanceCacheKey
{
    public static string Build(
        string intent,
        IReadOnlyList<RuleCard> selectedCards,
        string resolverIdentity,
        string schemaVersion)
    {
        var normalizedIntent = NormalizeIntent(intent);
        var cards = string.Join(",", selectedCards
            .Select(c => $"{c.RuleId}@{c.Version}")
            .OrderBy(s => s, StringComparer.Ordinal));

        return $"schema={schemaVersion}|model={resolverIdentity}|cards={cards}|intent={normalizedIntent}";
    }

    /// <summary>Lower-cases, collapses whitespace and strips edge punctuation, so trivially different phrasings share a key.</summary>
    public static string NormalizeIntent(string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
        {
            return "";
        }

        var builder = new StringBuilder(intent.Length);
        var lastWasSpace = false;
        foreach (var ch in intent.Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                builder.Append(ch);
                lastWasSpace = false;
            }
        }

        return builder.ToString().Trim('.', '!', '?', ',', ';', ':', ' ', '"', '\'');
    }
}

/// <summary>A plain in-memory cache. Orchestration is sequential, so no locking is needed.</summary>
public sealed class RuleGuidanceCache : IRuleGuidanceCache
{
    private readonly Dictionary<string, RuleGuidance> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string key, out RuleGuidance guidance)
    {
        if (_entries.TryGetValue(key, out var cached))
        {
            // Hand back a copy with no consultation id; each consultation stamps its own fresh id.
            guidance = cached with { ConsultationId = "" };
            return true;
        }

        guidance = null!;
        return false;
    }

    public void Set(string key, RuleGuidance guidance) =>
        _entries[key] = guidance with { ConsultationId = "" };
}
