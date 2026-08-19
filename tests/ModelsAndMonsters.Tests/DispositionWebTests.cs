using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Web;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The observer UI re-renders from the authoritative <see cref="StateDto"/> snapshot, so verifying the
/// projection verifies that the live cards and room-state element receive the right data: each character's
/// disposition, and the exit's public open state. The React rendering itself is out of scope for a C# test,
/// but the data it renders from is exactly this (test #35).
/// </summary>
public sealed class DispositionWebTests
{
    [Fact]
    public void The_state_snapshot_carries_every_disposition_and_the_exit_open_state()
    {
        var vark = TestWorld.Vark() with { Disposition = CharacterDisposition.Surrendered };
        var skrit = TestWorld.Skrit() with
        {
            Disposition = CharacterDisposition.Escaped,
            EscapedThroughExitId = TestWorld.StairDoorId
        };
        var state = TestWorld.StateWithExit(TestWorld.StairDoor(open: true),
            TestWorld.Rowan(), TestWorld.Elara(), vark, skrit);

        var dto = StateDto.From(state);

        // Every disposition rides the snapshot the cards render from, so a surrendered or escaped character
        // reads as such rather than as fallen.
        Assert.Equal("Active", dto.Characters.Single(c => c.Name == "Rowan").Disposition);
        Assert.Equal("Surrendered", dto.Characters.Single(c => c.Name == "Vark").Disposition);
        Assert.Equal("Escaped", dto.Characters.Single(c => c.Name == "Skrit").Disposition);
        Assert.True(dto.Characters.Single(c => c.Name == "Vark").Alive);
        Assert.True(dto.Characters.Single(c => c.Name == "Skrit").Alive);

        // The exit's public open state rides the same snapshot, so the room-state element can update live.
        var exit = Assert.Single(dto.Exits);
        Assert.Equal("Cellar Stair Door", exit.Name);
        Assert.True(exit.IsOpen);
    }

    [Fact]
    public void The_state_snapshot_shows_the_exit_closed_and_everyone_active_before_anything_happens()
    {
        var dto = StateDto.From(TestWorld.TwoVsTwoStateWithExit(exitOpen: false));

        Assert.False(Assert.Single(dto.Exits).IsOpen);
        Assert.All(dto.Characters, c => Assert.Equal("Active", c.Disposition));
    }
}
