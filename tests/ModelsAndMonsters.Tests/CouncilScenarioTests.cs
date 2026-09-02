using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Engine;

namespace ModelsAndMonsters.Tests;

public sealed class CouncilScenarioTests
{
    [Fact]
    public void Council_scenario_builds_with_the_rescue_objective_and_detained_guest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scenario_council.json");
        Assert.True(File.Exists(path), $"Expected the Council scenario at {path}.");

        var scenario = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build()
            .GetSection(ScenarioDefinition.SectionName).Get<ScenarioDefinition>();
        Assert.NotNull(scenario);

        var state = ScenarioFactory.CreateInitialState(scenario!);
        var heroes = state.Characters.Where(c => c.Role == CharacterRole.Hero).ToList();
        var wardens = state.Characters.Where(c => c.Team == "Wardens").ToList();

        Assert.Equal(6, heroes.Count);
        Assert.Equal(2, wardens.Count);
        Assert.Equal(
            ["Deacon", "Emma Nightveil", "Mirabel Tipton", "Nema Mistsong", "Pipix Thistlegrin", "Scott Suncrest"],
            heroes.Select(c => c.Name).OrderBy(name => name).ToArray());

        var objective = Assert.Single(scenario!.Objectives);
        Assert.Equal("rescue-deacon", objective.Id);
        Assert.Equal("Rescue Deacon", objective.Title);

        var deacon = state.RequireById("hero-deacon");
        Assert.Equal(CharacterDisposition.Detained, deacon.Disposition);
        Assert.True(deacon.IsAlive);
        Assert.True(deacon.IsPresent);
        Assert.False(deacon.CanAct);
        Assert.False(deacon.IsCombatTarget);

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
