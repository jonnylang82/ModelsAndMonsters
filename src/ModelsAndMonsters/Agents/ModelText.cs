using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace ModelsAndMonsters.Agents;

/// <summary>Tidies model prose before it reaches a character or the console.</summary>
public static partial class ModelText
{
    /// <summary>
    /// True when text looks like a tool call the model wrote as prose instead of calling.
    /// </summary>
    /// <remarks>
    /// Some local models emit their tool call as a JSON blob in the message content. That JSON must
    /// never reach a character as if it were narration, so it is detected and handled rather than shown.
    /// </remarks>
    public static bool LooksStructured(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith("```", StringComparison.Ordinal);
    }

    /// <summary>
    /// Pulls a named string field out of the first JSON object embedded in text, searching nested
    /// objects. Used to recover the DM's "reason" when it emits a reject_action as text rather than a
    /// tool call, so the explanation is preserved instead of leaking raw JSON.
    /// </summary>
    public static string? TryExtractJsonField(string? text, params string[] fieldNames)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            return FindStringField(document.RootElement, fieldNames);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindStringField(JsonElement element, string[] fieldNames)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        fieldNames.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value;
                        }
                    }

                    var nested = FindStringField(property.Value, fieldNames);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindStringField(item, fieldNames);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Recovers a tool call a model wrote as prose instead of calling.
    /// </summary>
    /// <remarks>
    /// Some models (notably small ones under this harness's immersive character prompt) understand the
    /// protocol but emit the call as text — <c>`take_action(I strike the goblin)`</c> or a bare JSON
    /// object — rather than as a structured tool call. This parses the tool name and its single argument
    /// out of that text so the orchestration can dispatch it. It is intentionally conservative: it only
    /// matches one of the <paramref name="knownToolNames"/>, and returns null when nothing parses, so it
    /// never invents a call. The returned argument is unnamed; callers rely on the tolerant single-argument
    /// reading in <see cref="ToolArguments"/> to place it under the tool's real parameter.
    /// </remarks>
    public static (string Name, string Argument)? TryRecoverToolCall(string? text, IReadOnlyCollection<string> knownToolNames)
    {
        if (string.IsNullOrWhiteSpace(text) || knownToolNames.Count == 0)
        {
            return null;
        }

        var cleaned = Clean(text);
        if (cleaned.Length == 0)
        {
            return null;
        }

        // Form 1: name(argument), optionally wrapped in backticks or code fences. The most common shape.
        var alternation = string.Join("|", knownToolNames.Select(Regex.Escape));
        var call = Regex.Match(cleaned, $@"\b({alternation})\s*\(", RegexOptions.IgnoreCase);
        if (call.Success)
        {
            var name = Canonical(knownToolNames, call.Groups[1].Value);
            var open = call.Index + call.Length - 1;
            var close = cleaned.LastIndexOf(')');
            if (close > open)
            {
                var argument = cleaned[(open + 1)..close].Trim().Trim('`', '"', '\'').Trim();
                if (argument.Length > 0)
                {
                    return (name, argument);
                }
            }
        }

        // Form 2: a JSON object naming the tool and carrying its argument under any common key. Tried
        // before the bare-quoted form below, so a JSON tool call is parsed as JSON rather than having the
        // quoted-string matcher pick a stray fragment (like a lone comma) out of its punctuation.
        var jsonName = TryExtractJsonField(cleaned, "name", "tool", "function");
        if (jsonName is not null && knownToolNames.Any(n => string.Equals(n, jsonName, StringComparison.OrdinalIgnoreCase)))
        {
            var argument = TryExtractJsonField(cleaned,
                "intent", "question", "reason", "argument", "value", "input", "text", "content");
            if (!string.IsNullOrWhiteSpace(argument))
            {
                return (Canonical(knownToolNames, jsonName), argument.Trim());
            }
        }

        // Form 3: name "argument", written without parentheses — e.g. say "Elara, look out!" or
        // ask_dm: "Is it wounded?". A common prose shape (notably qwen-family models) that Form 1 misses.
        // The opening quote fixes the closing quote, so apostrophes inside the message (you're, don't) do
        // not truncate it. Straight and curly double quotes are both accepted. This is the loosest form,
        // so it runs last and only on non-structured prose; JSON is handled above and never reaches it.
        if (!LooksStructured(cleaned))
        {
            var quoted = Regex.Match(cleaned,
                $"\\b({alternation})\\b\\s*[:=]?\\s*[\"“](?<arg>[^\"”]+)[\"”]",
                RegexOptions.IgnoreCase);
            if (quoted.Success)
            {
                var name = Canonical(knownToolNames, quoted.Groups[1].Value);
                var argument = quoted.Groups["arg"].Value.Trim();
                if (argument.Length > 0)
                {
                    return (name, argument);
                }
            }
        }

        return null;
    }

    private static string Canonical(IReadOnlyCollection<string> knownToolNames, string matched) =>
        knownToolNames.FirstOrDefault(n => string.Equals(n, matched, StringComparison.OrdinalIgnoreCase)) ?? matched;

    /// <summary>
    /// Detects a spoken line a character wrote as prose instead of calling <c>say</c>, returning the
    /// attempted utterance or null.
    /// </summary>
    /// <remarks>
    /// Small models under the immersive character prompt sometimes reply with first-person prose that
    /// contains a shout at another character — <c>I shout: "Vark! decide now…"</c> — and never call a tool.
    /// This finds such an utterance so the orchestration can record the communication attempt and nudge the
    /// character to speak properly, rather than silently ignoring it. It is deliberately conservative: it
    /// wants a quoted span of at least a few words that is introduced by a speech verb, so an action reply
    /// that merely ends on a battle-cry ("…and yell 'Die!'") and — crucially — a tool call written as prose
    /// (<c>take_action("I strike")</c>, whose quote is not preceded by a speech verb) do not trip it. No
    /// model call — a plain heuristic, so it adds no latency.
    /// </remarks>
    public static string? TryExtractSpokenAttempt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = Clean(text);
        if (cleaned.Length == 0)
        {
            return null;
        }

        foreach (Match match in QuotedSpan().Matches(cleaned))
        {
            var utterance = match.Groups[1].Value.Trim();
            var words = utterance.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (words < 3)
            {
                continue;
            }

            var before = cleaned[..match.Index];
            var tail = before.Length <= 40 ? before : before[^40..];
            if (SpeechVerb().IsMatch(tail))
            {
                return utterance;
            }
        }

        return null;
    }

    // A span between straight or curly double quotes, capturing at least three characters inside.
    [GeneratedRegex("[\"“”]([^\"“”]{3,})[\"“”]")]
    private static partial Regex QuotedSpan();

    // A speech verb close to (immediately before) an opening quote — the strong signal that a quote is an
    // utterance the character meant to say, not incidental quoted text.
    [GeneratedRegex(
        @"\b(say|says|said|shout|shouts|shouted|yell|yells|yelled|call|calls|called|cry|cries|cried|tell|tells|told|whisper|whispers|whispered|hiss|hisses|hissed|snarl|snarls|snarled|growl|growls|growled|bark|barks|barked|roar|roars|roared|declare|declares|announce|announces)\b[^“”""]{0,25}$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SpeechVerb();

    /// <summary>
    /// Returns the assistant's prose. Reasoning models that emit literal think blocks in their text
    /// have them stripped: that content is the model's private working, not something a character in
    /// the world perceived. The unedited text is still recorded in the trace.
    /// </summary>
    public static string Clean(ChatResponse response) => Clean(response.Text);

    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var cleaned = ThinkBlock().Replace(text, "");

        // An unterminated think block means the whole reply is working-out; keep what follows if any.
        var unterminated = cleaned.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (unterminated >= 0)
        {
            cleaned = cleaned[..unterminated];
        }

        return cleaned.Trim();
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();
}
