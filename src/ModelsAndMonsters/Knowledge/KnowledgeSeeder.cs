using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Knowledge;

/// <summary>What was minted and learned while seeding a scenario's initial private knowledge.</summary>
public sealed record KnowledgeSeedResult(
    IReadOnlyList<KnowledgeFact> CreatedFacts,
    IReadOnlyList<CharacterKnowledge> LearnedRecords);

/// <summary>
/// Seeds the ledger with the private backstory knowledge a scenario grants before play begins.
/// </summary>
/// <remarks>
/// A character's <see cref="CharacterDefinition.BackstoryKnowledge"/> names containers it already knows —
/// both that they exist and what they hold — recorded as <see cref="KnowledgeSource.Backstory"/> at world
/// version 0. Nobody else automatically shares it, which is exactly what gives, say, a goblin captain who
/// knows his own cases a reason to tell his ally without the harness scripting that decision.
/// </remarks>
public static class KnowledgeSeeder
{
    public static KnowledgeSeedResult Seed(KnowledgeLedger ledger, ScenarioDefinition scenario, GameState initialState)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(initialState);

        var created = new List<KnowledgeFact>();
        var learned = new List<CharacterKnowledge>();

        foreach (var character in scenario.Characters)
        {
            foreach (var subjectId in character.BackstoryKnowledge)
            {
                var container = initialState.Room.Objects
                    .OfType<Container>()
                    .FirstOrDefault(c => string.Equals(c.Id, subjectId, StringComparison.OrdinalIgnoreCase));
                if (container is null)
                {
                    // A backstory reference to a container the scenario does not seed is simply ignored;
                    // there is nothing to know about a thing that is not there.
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(container.ExteriorClue))
                {
                    var marking = ledger.GetOrAddMarkingFact(container.Id, container.ExteriorClue);
                    Record(marking, () => ledger.Learn(character.Id, marking.Fact.Id, KnowledgeSource.Backstory, 0, 0, 0));
                }

                var contents = ledger.GetOrAddContentsFact(container.Id, container.Name, container.Contents, 0);
                Record(contents, () => ledger.Learn(character.Id, contents.Fact.Id, KnowledgeSource.Backstory, 0, 0, 0));
            }
        }

        return new KnowledgeSeedResult(created, learned);

        void Record(KnowledgeLedger.FactResult fact, Func<CharacterKnowledge?> learn)
        {
            if (fact.WasCreated)
            {
                created.Add(fact.Fact);
            }

            if (learn() is { } record)
            {
                learned.Add(record);
            }
        }
    }
}
