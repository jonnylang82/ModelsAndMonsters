using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Engine;

/// <summary>The living/total headcount of one team at the moment the terminal condition was checked.</summary>
public sealed record TeamStanding(string Team, int Living, int Total)
{
    public bool IsEliminated => Living == 0;
}

/// <summary>
/// The outcome of evaluating the team terminal condition over a game state.
/// </summary>
/// <remarks>
/// This is pure: it reads a snapshot and reports whether the encounter is over, which team(s) survive
/// and which were eliminated. The orchestration layer decides what to do with it (stop the loop, emit a
/// trace event); the engine still owns the state that produced it.
/// </remarks>
public sealed record TerminalConditionResult
{
    public required bool IsOver { get; init; }

    public required IReadOnlyList<TeamStanding> Standings { get; init; }

    /// <summary>Teams that still have at least one living member when the encounter ended.</summary>
    public required IReadOnlyList<string> WinningTeams { get; init; }

    /// <summary>Teams reduced to no living members.</summary>
    public required IReadOnlyList<string> EliminatedTeams { get; init; }

    public required string Description { get; init; }
}

/// <summary>
/// The v0.2 terminal condition: an encounter ends the moment any team has no living members.
/// </summary>
/// <remarks>
/// With exactly two teams this is "all of one side are dead". Written for any number of teams so a
/// single-hero-versus-single-monster v0.1 scenario (two teams of one) resolves under the same rule as a
/// 2v2. A voluntary <c>end_turn</c> changes no health and therefore can never satisfy this on its own.
/// </remarks>
public static class TerminalCondition
{
    public static TerminalConditionResult Evaluate(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var standings = state.Teams()
            .Select(team => new TeamStanding(
                team,
                state.Characters.Count(c => c.IsAlive && Same(c.Team, team)),
                state.Characters.Count(c => Same(c.Team, team))))
            .ToList();

        var eliminated = standings.Where(s => s.IsEliminated).Select(s => s.Team).ToList();
        var surviving = standings.Where(s => !s.IsEliminated).Select(s => s.Team).ToList();

        // A single-team scenario can never reach a team terminal condition; the harness limits stop it.
        var isOver = standings.Count >= 2 && eliminated.Count > 0;

        return new TerminalConditionResult
        {
            IsOver = isOver,
            Standings = standings,
            WinningTeams = isOver ? surviving : [],
            EliminatedTeams = isOver ? eliminated : [],
            Description = Describe(isOver, surviving, eliminated)
        };
    }

    private static string Describe(bool isOver, IReadOnlyList<string> surviving, IReadOnlyList<string> eliminated)
    {
        if (!isOver)
        {
            return "The encounter continues: every team still has a living member.";
        }

        var fallen = string.Join(" and ", eliminated);

        return surviving.Count switch
        {
            0 => $"Mutual defeat: every team has fallen ({fallen}).",
            1 => $"{surviving[0]} win: {fallen} has no living members.",
            _ => $"{string.Join(" and ", surviving)} remain; {fallen} has no living members."
        };
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
