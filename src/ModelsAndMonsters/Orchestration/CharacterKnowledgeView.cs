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
    /// How many of a character's most recent first-hand facts a projection carries.
    /// </summary>
    /// <remarks>
    /// This bound is the difference between a projection and a transcript. Both views used to render the
    /// character's whole lifetime ledger, so they grew every round — measured on a 12-round v0.7 run, the
    /// Dungeon Master's adjudication knowledge block went from 759 to 5,529 characters, which is the only
    /// part of that request that grows with encounter length and the reason it stopped fitting a small
    /// context window. Recency is what adjudication needs; the complete ledger is in the trace and the
    /// report, and the number of omitted facts is stated so nothing looks like it never happened.
    /// </remarks>
    private const int MaxProjectedFacts = 12;

    /// <summary>How many of the most recent things a character has been told a projection carries.</summary>
    private const int MaxProjectedHearsay = 6;

    /// <summary>
    /// A first-person reminder of what this character has discovered first-hand, for injection into its
    /// own turn context. Empty when it has learned nothing beyond what anyone in the room can see. Hearsay
    /// is deliberately excluded: things others said already reach the character verbatim on the public
    /// channel, and must not be restated here as if they were the character's own knowledge. Bounded to the
    /// most recent <see cref="MaxProjectedFacts"/> facts, so a long fight does not grow every turn context.
    /// </summary>
    public static string RenderSelfSummary(string characterId, KnowledgeLedger ledger, GameState state)
    {
        var all = ledger.RecordsFor(characterId).ToList();
        if (all.Count == 0)
        {
            return "";
        }

        var (records, omitted) = MostRecent(all, MaxProjectedFacts);

        var builder = new StringBuilder();
        if (omitted > 0)
        {
            builder.AppendLine($"- (and {omitted} older thing(s) you found out earlier, no longer front of mind)");
        }

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
    /// The last <paramref name="keep"/> items of a list, with how many were left out. Recency, not
    /// importance: the ledger has no notion of importance, and inventing one would be a guess.
    /// </summary>
    private static (IReadOnlyList<T> Kept, int Omitted) MostRecent<T>(IReadOnlyList<T> all, int keep) =>
        all.Count <= keep ? (all, 0) : ([.. all.Skip(all.Count - keep)], all.Count - keep);

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
        var allRecords = ledger.RecordsFor(characterId).ToList();
        if (allRecords.Count == 0)
        {
            builder.AppendLine("- Nothing beyond what anyone standing in the room can plainly see.");
        }
        else
        {
            var (records, omittedFacts) = MostRecent(allRecords, MaxProjectedFacts);
            if (omittedFacts > 0)
            {
                builder.AppendLine(
                    $"- (the {omittedFacts} oldest of {characterName}'s discoveries are omitted from this " +
                    "projection to keep it bounded; the most recent are below)");
            }

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
        var allHeard = narrationLog.SpeechHeardBy(characterId);
        if (allHeard.Count == 0)
        {
            builder.AppendLine("- Nothing.");
        }
        else
        {
            var (heard, omittedHearsay) = MostRecent(allHeard, MaxProjectedHearsay);
            if (omittedHearsay > 0)
            {
                builder.AppendLine($"- (and {omittedHearsay} earlier thing(s) said, omitted to keep this projection bounded)");
            }

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
