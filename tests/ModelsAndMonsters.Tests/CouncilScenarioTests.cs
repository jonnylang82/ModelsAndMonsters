using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;

namespace ModelsAndMonsters.Tests;

public sealed class CouncilScenarioTests
{
    [Fact]
    public void Council_scenario_builds_with_the_four_player_characters_and_real_engine_abilities()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scenario_council.json");
        Assert.True(File.Exists(path), $"Expected the Council scenario at {path}.");

        var scenario = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build()
            .GetSection(ScenarioDefinition.SectionName).Get<ScenarioDefinition>();
        Assert.NotNull(scenario);

        var state = ScenarioFactory.CreateInitialState(scenario!);
        var heroes = state.Characters.Where(c => c.Role == CharacterRole.Hero).ToList();
        var wardens = state.Characters.Where(c => c.Team == "Wardens").ToList();

        Assert.Equal(5, heroes.Count);
        Assert.Equal(2, wardens.Count);
        Assert.Equal(
            ["Emma Nightveil", "Mirabel Tipton", "Nema Mistsong", "Pipix Thistlegrin", "Scott Suncrest"],
            heroes.Select(c => c.Name).OrderBy(name => name).ToArray());

        Assert.NotNull(state.RequireById("hero-nema").FindAbility(AbilityCatalog.RallyGruntId));
        Assert.NotNull(state.RequireById("hero-mirabel").FindAbility(AbilityCatalog.DirtyStrikeId));
        Assert.NotNull(state.RequireById("hero-emma").FindAbility(AbilityCatalog.FireboltId));
        Assert.NotNull(state.RequireById("hero-pipix").FindAbility(AbilityCatalog.HealingPrayerId));
        Assert.NotNull(state.RequireById("hero-scott").FindAbility(AbilityCatalog.GuardAllyId));

        Assert.Single(state.Room.Objects.OfType<CoverObject>());
        Assert.Equal(2, state.Room.Objects.OfType<Container>().Count());
        Assert.Single(state.Room.Exits);
    }
}
