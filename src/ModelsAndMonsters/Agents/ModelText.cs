using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace ModelsAndMonsters.Agents;

/// <summary>Tidies model prose before it reaches a character or the console.</summary>
public static partial class ModelText
{
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
