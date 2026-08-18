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
    /// Builds the system prompt. When the full roster is supplied, the character is told who fights at its
    /// side and who it has come to fight, both by name — background a person plainly has walking in, so a
    /// weaker model neither strikes a friend nor drifts into treating a named enemy as a companion. Naming
    /// the enemy side reveals nothing hidden: who opposes whom is in plain sight from the first moment.
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
    /// Names, from the roster, who fights at the character's side and who it has come to fight. Both are
    /// background a person plainly has walking in — the enemy stands across the room in plain sight — so
    /// both belong in the standing system prompt. Naming the enemy side, not only the allies, is what keeps
    /// a weaker model from drifting into offering an enemy aid or comfort. It still never reveals anyone's
    /// mechanical state, and the genuinely hidden things (a case's contents) stay hidden.
    /// </summary>
    private static string FormatAllies(CharacterDefinition self, IReadOnlyList<CharacterDefinition>? roster)
    {
        const string alone =
            "You have no companions in this fight. Everyone else here stands against you — offer them no aid.";

        if (roster is null || roster.Count == 0)
        {
            return alone;
        }

        var myTeam = EffectiveTeam(self);
        var others = roster
            .Where(c => !string.Equals(c.Id, self.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var allies = others
            .Where(c => string.Equals(EffectiveTeam(c), myTeam, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Name)
            .ToList();
        var enemies = others
            .Where(c => !string.Equals(EffectiveTeam(c), myTeam, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Name)
            .ToList();

        var builder = new StringBuilder();

        builder.Append(allies.Count > 0
            ? $"{NaturalJoin(allies)} {(allies.Count == 1 ? "fights" : "fight")} at your side — " +
              $"{(allies.Count == 1 ? "an ally" : "allies")} sworn to the same cause as you. Never raise a weapon " +
              "against them, whatever the confusion of the moment."
            : "You have no companions in this fight.");

        if (enemies.Count > 0)
        {
            var single = enemies.Count == 1;
            builder.Append(' ');
            builder.Append(
                $"{NaturalJoin(enemies)} {(single ? "is your enemy" : "are your enemies")} — " +
                $"{(single ? "the one" : "the ones")} you have come to fight. They are not your " +
                $"{(single ? "friend" : "friends")}, whatever they may say; offer them no aid, comfort or " +
                "reassurance, and never mistake an enemy for a companion.");
        }
        else
        {
            builder.Append(" Everyone else in this room is an enemy you have come to fight — offer them no aid.");
        }

        return builder.ToString();
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
