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
/// <para>
/// A character's <see cref="CharacterDefinition.BackstoryKnowledge"/> names containers it already knows —
/// both that they exist and what they hold — recorded as <see cref="KnowledgeSource.Backstory"/> at world
/// version 0. Nobody else automatically shares it, which is exactly what gives, say, a goblin captain who
/// knows his own cases a reason to tell his ally without the harness scripting that decision.
/// </para>
/// <para>
/// Starting inventory is different: what a character openly carries is plainly visible to everyone else in
/// the small, no-distance room (v0.6 does not model concealment — carried on the person is carried in plain
/// sight). So each starting item is minted as an <see cref="FactType.ItemPossession"/> fact recorded as an
/// initial PUBLIC observation (<see cref="KnowledgeSource.PublicEvent"/>) that every OTHER present character
/// makes at the outset — not as backstory, which would wrongly imply prior personal knowledge. That is what
/// makes an openly carried item knowable to others, giving a would-be thief a legitimate informational basis
/// to attempt a theft; an item held out of sight would simply not be seeded as observed. The owner is not
/// recorded as "observing" their own item — they already carry it, and it is in their own self-state.
/// </para>
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

        // Everyone present can see what everyone else is openly carrying at the start of the encounter — a
        // public observation, not prior knowledge. The owner is not recorded as observing their own item.
        var present = initialState.Characters.Where(c => c.IsPresent).ToList();
        foreach (var owner in initialState.Characters)
        {
            foreach (var item in owner.Inventory)
            {
                var possession = ledger.GetOrAddItemPossessionFact(item.Id, item.Name, owner.Name, 0);
                if (possession.WasCreated)
                {
                    created.Add(possession.Fact);
                }

                foreach (var observer in present)
                {
                    if (string.Equals(observer.Id, owner.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (ledger.Learn(observer.Id, possession.Fact.Id, KnowledgeSource.PublicEvent, 0, 0, 0) is { } record)
                    {
                        learned.Add(record);
                    }
                }
            }
        }

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
