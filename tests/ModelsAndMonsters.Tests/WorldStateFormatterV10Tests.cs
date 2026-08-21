using ModelsAndMonsters.Domain;
using ModelsAndMonsters.Prompts;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// Two v0.10 truthfulness fixes to the rendered state: a carried weapon someone looted from a fallen or
/// surrendered foe must never read as though it were equipped and usable, and a character facing an
/// occupied piece of cover must be able to see that striking the object itself — not the person behind it —
/// is a real, reachable option.
/// </summary>
public sealed class WorldStateFormatterV10Tests
{
    private static readonly PromptLibrary Prompts =
        PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));

    [Fact]
    public void A_carried_weapon_trophy_reads_distinctly_from_an_equipped_weapon_in_both_state_blocks()
    {
        var trophy = new Weapon("Rusty Dagger", 2).AsForfeitedItem();
        var skrit = TestWorld.Skrit() with { Inventory = [trophy] };
        var state = TestWorld.State(TestWorld.Rowan(), skrit);
        var formatter = new WorldStateFormatter(Prompts);

        var selfState = formatter.FormatCharacterSelfState(skrit, state);
        var authoritative = WorldStateFormatter.FormatAuthoritativeState(state);

        foreach (var block in new[] { selfState, authoritative })
        {
            Assert.Contains("Rusty Dagger", block, StringComparison.Ordinal);
            Assert.Contains("trophy", block, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not equipped", block, StringComparison.OrdinalIgnoreCase);
        }

        // Skrit's own equipped weapon is named separately and unambiguously, in both blocks.
        Assert.Contains("Crude Spear", selfState, StringComparison.Ordinal);
        Assert.Contains("Crude Spear", authoritative, StringComparison.Ordinal);
    }

    [Fact]
    public void A_character_facing_occupied_cover_is_told_it_can_be_struck_down_instead()
    {
        var occupied = TestWorld.Workbench(occupantId: TestWorld.VarkId);
        var state = TestWorld.StateWith([occupied], TestWorld.Rowan(), TestWorld.Vark());
        var formatter = new WorldStateFormatter(Prompts);

        var selfState = formatter.FormatCharacterSelfState(TestWorld.Rowan(), state);

        Assert.Contains("already occupied by Vark", selfState, StringComparison.Ordinal);
        Assert.Contains("strike", selfState, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("destroys it outright and leaves Vark exposed", selfState, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------
    // The Dungeon Master's authoritative state must actually state each character's team — the
    // dimension ally/enemy is decided on — not merely claim to (dungeon-master.core.md already said
    // "each has ... a team ... in the authoritative snapshot", but nothing ever rendered it). A live run
    // had the DM refuse Vark steadying his own squadmate Skrit — both goblins, same team — inventing
    // "Skrit is not on your side; he is an enemy goblin" because the state gave it no team signal at all.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_authoritative_state_names_every_characters_team()
    {
        var state = TestWorld.TwoVsTwoState();

        var authoritative = WorldStateFormatter.FormatAuthoritativeState(state);

        Assert.Contains($"Vark (id: {TestWorld.VarkId}, monster, team: {TestWorld.GoblinsTeam})", authoritative, StringComparison.Ordinal);
        Assert.Contains($"Skrit (id: {TestWorld.SkritId}, monster, team: {TestWorld.GoblinsTeam})", authoritative, StringComparison.Ordinal);
        Assert.Contains($"Rowan (id: {TestWorld.RowanId}, hero, team: {TestWorld.HeroesTeam})", authoritative, StringComparison.Ordinal);
    }

    [Fact]
    public void The_authoritative_state_carries_a_team_roster_summary_grouping_squadmates_together()
    {
        var state = TestWorld.TwoVsTwoState();

        var authoritative = WorldStateFormatter.FormatAuthoritativeState(state);

        var teamsLine = authoritative.Split('\n').First(l => l.StartsWith("TEAMS", StringComparison.Ordinal));
        Assert.Contains("Vark", teamsLine, StringComparison.Ordinal);
        Assert.Contains("Skrit", teamsLine, StringComparison.Ordinal);
        Assert.Contains("Rowan", teamsLine, StringComparison.Ordinal);
        Assert.Contains("Elara", teamsLine, StringComparison.Ordinal);
        Assert.Contains(TestWorld.GoblinsTeam, teamsLine, StringComparison.Ordinal);
        Assert.Contains(TestWorld.HeroesTeam, teamsLine, StringComparison.Ordinal);
    }

    [Fact]
    public void The_snapshot_tells_the_dm_that_team_alone_not_role_decides_allies()
    {
        var authoritative = WorldStateFormatter.FormatAuthoritativeState(TestWorld.TwoVsTwoState());

        Assert.Contains("allies exactly when their TEAM matches", authoritative, StringComparison.Ordinal);
        Assert.Contains("never inferred from role", authoritative, StringComparison.OrdinalIgnoreCase);
    }
}
