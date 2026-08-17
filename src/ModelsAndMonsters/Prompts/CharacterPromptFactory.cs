using System.Text;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Prompts;

/// <summary>
/// Builds a character's system prompt from the shared character prompt plus that character's own
/// definition. Characters differ only in this text and in their model profile.
/// </summary>
public sealed class CharacterPromptFactory
{
    private readonly PromptLibrary _prompts;

    public CharacterPromptFactory(PromptLibrary prompts)
    {
        _prompts = prompts;
    }

    /// <summary>
    /// Builds the system prompt. When the full roster is supplied, the character is told who its allies
    /// are by name — background knowledge a person plainly has about their own comrades — so a small
    /// model does not lose track of its own side and strike a friend. Enemies are deliberately not named
    /// here; a character still learns who opposes it through the Dungeon Master's narration.
    /// </summary>
    public string CreateSystemPrompt(CharacterDefinition definition, IReadOnlyList<CharacterDefinition>? roster = null) =>
        _prompts.Render("character.system", new Dictionary<string, string?>
        {
            ["name"] = definition.Name,
            ["persona"] = FormatPersona(definition),
            ["allies"] = FormatAllies(definition, roster)
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

    /// <summary>
    /// Names the character's allies from the roster. This is who a person knows they came in with, not
    /// something they must perceive, so it belongs in the standing system prompt. It never names enemies
    /// or reveals anyone's mechanical state.
    /// </summary>
    private static string FormatAllies(CharacterDefinition self, IReadOnlyList<CharacterDefinition>? roster)
    {
        const string alone =
            "You have no companions in this fight. Everyone else here stands against you.";

        if (roster is null || roster.Count == 0)
        {
            return alone;
        }

        var myTeam = EffectiveTeam(self);
        var allies = roster
            .Where(c => !string.Equals(c.Id, self.Id, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(EffectiveTeam(c), myTeam, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Name)
            .ToList();

        if (allies.Count == 0)
        {
            return alone;
        }

        var single = allies.Count == 1;
        return
            $"{NaturalJoin(allies)} {(single ? "fights" : "fight")} at your side — {(single ? "an ally" : "allies")} " +
            "sworn to the same cause as you. Never raise a weapon against " +
            $"{(single ? "them" : "them")}, whatever the confusion of the moment. Everyone else in this room is " +
            "an enemy you have come to fight.";
    }

    private static string EffectiveTeam(CharacterDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.Team))
        {
            return definition.Team.Trim();
        }

        return Enum.TryParse<CharacterRole>(definition.Role, ignoreCase: true, out var role)
            ? Character.DefaultTeamForRole(role)
            : definition.Role;
    }

    private static string NaturalJoin(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))}, and {names[^1]}"
    };

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
