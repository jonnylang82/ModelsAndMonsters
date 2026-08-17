using System.Text;
using ModelsAndMonsters.Configuration;

namespace ModelsAndMonsters.Prompts;

/// <summary>
/// Builds a character's system prompt from the shared character prompt plus that character's own
/// definition. The Hero and the Monster differ only in this text and in their model profile.
/// </summary>
public sealed class CharacterPromptFactory
{
    private readonly PromptLibrary _prompts;

    public CharacterPromptFactory(PromptLibrary prompts)
    {
        _prompts = prompts;
    }

    public string CreateSystemPrompt(CharacterDefinition definition) =>
        _prompts.Render("character.system", new Dictionary<string, string?>
        {
            ["name"] = definition.Name,
            ["persona"] = FormatPersona(definition)
        });

    /// <summary>Only populated persona fields appear, so v0.1 definitions can stay sparse.</summary>
    private static string FormatPersona(CharacterDefinition definition)
    {
        var builder = new StringBuilder();
        var persona = definition.Persona;

        Append(builder, "Who you are", persona.Backstory);
        Append(builder, "Your nature", persona.Personality);
        Append(builder, "What you want", persona.Wants);
        Append(builder, "What you need", persona.Needs);
        Append(builder, "What you fear", persona.Fears);
        Append(builder, "What you are trying to achieve right now", persona.Goal);

        return builder.Length == 0 ? "" : builder.ToString().TrimEnd();
    }

    private static void Append(StringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine($"**{label}:** {value.Trim()}");
        builder.AppendLine();
    }
}
