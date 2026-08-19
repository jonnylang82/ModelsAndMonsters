using System.Text;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Knowledge;

namespace ModelsAndMonsters.Orchestration;

/// <summary>
/// Projects one character's information view into text — deliberately, and for one recipient at a time.
/// </summary>
/// <remarks>
/// <para>
/// This is the boundary that keeps hidden information hidden. A character never receives another
/// character's private observation, and the Dungeon Master, though it holds omniscient state, is handed
/// the acting or asking character's view so it can answer within that character's information boundary
/// rather than from what it can see.
/// </para>
/// <para>
/// Two renderings, for two audiences. <see cref="RenderSelfSummary"/> gives a character a short
/// first-person reminder of what it has discovered, for its own turn. <see cref="RenderForDungeonMaster"/>
/// gives the Dungeon Master the same character's view split into what it directly knows and what it has
/// merely heard — because the DM must be able to say "you saw a potion" and "Elara told you there was a
/// potion" differently, and must never turn the second into the first.
/// </para>
/// </remarks>
public static class CharacterKnowledgeView
{
    /// <summary>
    /// A first-person reminder of what this character has discovered first-hand, for injection into its
    /// own turn context. Empty when it has learned nothing beyond what anyone in the room can see. Hearsay
    /// is deliberately excluded: things others said already reach the character verbatim on the public
    /// channel, and must not be restated here as if they were the character's own knowledge.
    /// </summary>
    public static string RenderSelfSummary(string characterId, KnowledgeLedger ledger, GameState state)
    {
        var records = ledger.RecordsFor(characterId).ToList();
        if (records.Count == 0)
        {
            return "";
        }

        var builder = new StringBuilder();
        foreach (var record in records)
        {
            var fact = ledger.FindFact(record.FactId);
            if (fact is null)
            {
                continue;
            }

            builder.AppendLine($"- {SelfLine(fact, record, SubjectName(fact.SubjectId, state))}");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// The character's information view for the Dungeon Master: what it directly knows (first-hand, with
    /// how and when), and separately what it has only heard others say (hearsay). The DM answers and
    /// adjudicates within this view, never leaking another character's private knowledge or the omniscient
    /// current state beyond what this character could actually possess.
    /// </summary>
    public static string RenderForDungeonMaster(
        string characterId,
        string characterName,
        KnowledgeLedger ledger,
        NarrationLog narrationLog,
        GameState state)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"WHAT {characterName} DIRECTLY KNOWS (observed first-hand — treat as true, but as of the moment stated):");
        var records = ledger.RecordsFor(characterId).ToList();
        if (records.Count == 0)
        {
            builder.AppendLine("- Nothing beyond what anyone standing in the room can plainly see.");
        }
        else
        {
            foreach (var record in records)
            {
                var fact = ledger.FindFact(record.FactId);
                if (fact is null)
                {
                    continue;
                }

                builder.AppendLine($"- {DungeonMasterLine(fact, record)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine($"WHAT {characterName} HAS ONLY HEARD OTHERS SAY (hearsay — this is a claim someone made, NOT something {characterName} has verified):");
        var heard = narrationLog.SpeechHeardBy(characterId);
        if (heard.Count == 0)
        {
            builder.AppendLine("- Nothing.");
        }
        else
        {
            foreach (var entry in heard)
            {
                builder.AppendLine($"- {SingleLine(entry.Text)}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string SelfLine(KnowledgeFact fact, CharacterKnowledge record, string subjectName) => fact.FactType switch
    {
        FactType.ContainerExteriorMarking => record.Source == KnowledgeSource.Backstory
            ? $"You know the {subjectName} and its markings: {fact.Description}"
            : $"You examined the {subjectName} closely and recognised: {fact.Description}",
        FactType.ContainerContents => record.Source switch
        {
            KnowledgeSource.Backstory => $"{fact.Description} It is yours, so you know what it holds.",
            KnowledgeSource.OpenedContainer => $"{fact.Description} You saw this yourself when you opened it — though that was a moment ago, and it may have changed since.",
            _ => $"{fact.Description} You saw this when you looked inside — it may have changed since."
        },
        FactType.ContainerOpened => $"{fact.Description} You saw it happen — though what is inside it you know only if you looked.",
        FactType.ItemRemoved => $"{fact.Description} You saw it happen.",
        FactType.ItemGiven or FactType.ItemDropped or FactType.ItemTheftAttempted => $"{fact.Description} You saw it happen.",
        FactType.ItemPossession => fact.Description,
        _ => fact.Description
    };

    private static string DungeonMasterLine(KnowledgeFact fact, CharacterKnowledge record)
    {
        // An openly-carried item is a plainly-visible public fact, not something learned by an event or known
        // from before — say so directly, so the DM never treats it as prior or private knowledge.
        var how = fact.FactType == FactType.ItemPossession
            ? "plainly visible — carried openly on the person"
            : record.Source switch
            {
                KnowledgeSource.Backstory => "known from before the fight began",
                KnowledgeSource.DirectInspection => "by inspecting it closely",
                KnowledgeSource.OpenedContainer => "by opening it and looking inside",
                KnowledgeSource.PublicEvent => "seen happen in the open",
                _ => "observed"
            };

        var when = record.LearnedAtRound > 0 ? $"round {record.LearnedAtRound}" : "before play";
        var version = fact.FactType == FactType.ContainerContents
            ? $", observed at world version {record.ObservedWorldVersion}"
            : "";
        return $"{fact.Description} ({how}, {when}{version})";
    }

    private static string SubjectName(string subjectId, GameState state) =>
        state.Room.Objects.FirstOrDefault(o => string.Equals(o.Id, subjectId, StringComparison.OrdinalIgnoreCase))?.Name
            ?? subjectId;

    private static string SingleLine(string text) =>
        text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
}
