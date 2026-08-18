using ModelsAndMonsters.Knowledge;

namespace ModelsAndMonsters.Tracing;

/// <summary>
/// One place that turns knowledge-ledger changes into trace events, so the seeding path and the in-play
/// path emit them identically. Everything a knowledge fact's discovery and delivery needs to be
/// reconstructed — id, subject, type, description, source, character, round, turn, world version,
/// visibility and recipients — is recorded here.
/// </summary>
public static class KnowledgeTracing
{
    public static void FactCreated(
        ExperimentTrace trace,
        KnowledgeFact fact,
        KnowledgeSource createdBySource,
        string? relatedAction,
        string? actor = null) =>
        trace.Emit(TraceEventType.KnowledgeFactCreated, new KnowledgeFactCreatedPayload
        {
            FactId = fact.Id,
            SubjectId = fact.SubjectId,
            FactType = fact.FactType.ToString(),
            Description = fact.Description,
            WorldVersion = fact.WorldVersion,
            CreatedBySource = createdBySource.ToString(),
            RelatedAction = relatedAction
        }, actor);

    public static void FactLearned(
        ExperimentTrace trace,
        KnowledgeFact fact,
        CharacterKnowledge record,
        string characterName,
        string visibility,
        IReadOnlyList<string> recipients,
        string? relatedAction,
        string? actor = null) =>
        trace.Emit(TraceEventType.KnowledgeFactLearned, new KnowledgeFactLearnedPayload
        {
            FactId = fact.Id,
            SubjectId = fact.SubjectId,
            FactType = fact.FactType.ToString(),
            Description = fact.Description,
            CharacterId = record.CharacterId,
            CharacterName = characterName,
            Source = record.Source.ToString(),
            Round = record.LearnedAtRound,
            Turn = record.LearnedAtTurn,
            ObservedWorldVersion = record.ObservedWorldVersion,
            Visibility = visibility,
            Recipients = recipients,
            RelatedAction = relatedAction
        }, actor);
}
