using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Engine;

/// <summary>How the encounter was, or would be, decided. Set once the terminal condition is met.</summary>
public enum EncounterOutcome
{
    /// <summary>The encounter is not over: more than one team still has an active fighter.</summary>
    Ongoing,

    /// <summary>Every member of the defeated team is dead.</summary>
    Elimination,

    /// <summary>The defeated team left active combat by surrendering.</summary>
    Surrender,

    /// <summary>The defeated team left active combat by escaping.</summary>
    Withdrawal,

    /// <summary>The defeated team's members left by a combination of death, surrender and escape.</summary>
    Mixed,

    /// <summary>No team has an active fighter left. Rare, but represented explicitly.</summary>
    Draw,

    /// <summary>The run stopped on a round or idle limit before a terminal condition was reached.</summary>
    HarnessLimit
}

/// <summary>
/// One team's membership at the moment the terminal condition was checked, broken down by disposition.
/// </summary>
/// <remarks>
/// It is <see cref="Active"/>, not <see cref="Living"/>, that decides the encounter in v0.5: a team whose
/// only surviving members have surrendered or escaped is no longer an active contender even though those
/// members are alive. <see cref="Living"/> and <see cref="Total"/> are retained so existing standings
/// output keeps its meaning.
/// </remarks>
public sealed record TeamStanding(string Team, int Active, int Surrendered, int Escaped, int Dead)
{
    /// <summary>Members who are alive in any disposition: active, surrendered or escaped.</summary>
    public int Living => Active + Surrendered + Escaped;

    public int Total => Active + Surrendered + Escaped + Dead;

    /// <summary>True while the team still has at least one active fighter — what keeps it in the encounter.</summary>
    public bool IsActiveContender => Active > 0;

    /// <summary>True when the team has no active fighter left, however many of its members remain alive.</summary>
    public bool IsEliminated => Active == 0;
}

/// <summary>What became of one character who has left active combat (dead, surrendered or escaped).</summary>
public sealed record CharacterResolution(
    string CharacterId,
    string CharacterName,
    string Team,
    CharacterDisposition Disposition,
    string? ExitName)
{
    /// <summary>A one-line, in-world account, e.g. "Vark was killed." or "Skrit escaped through the Cellar Stair Door."</summary>
    public string Summary => Disposition switch
    {
        CharacterDisposition.Dead => $"{CharacterName} was killed.",
        CharacterDisposition.Surrendered => $"{CharacterName} surrendered.",
        CharacterDisposition.Escaped => ExitName is null
            ? $"{CharacterName} escaped."
            : $"{CharacterName} escaped through the {ExitName}.",
        _ => $"{CharacterName} is still standing."
    };
}

/// <summary>
/// The outcome of evaluating the team terminal condition over a game state.
/// </summary>
/// <remarks>
/// This is pure: it reads a snapshot and reports whether the encounter is over, which team(s) remain active
/// contenders, which have lost active membership, how the defeated team was removed, and what became of each
/// character who has left the fight. The orchestration layer decides what to do with it.
/// </remarks>
public sealed record TerminalConditionResult
{
    public required bool IsOver { get; init; }

    public required IReadOnlyList<TeamStanding> Standings { get; init; }

    /// <summary>Teams that still have at least one active member when the encounter ended.</summary>
    public required IReadOnlyList<string> WinningTeams { get; init; }

    /// <summary>Teams reduced to no active members (whether by death, surrender or escape).</summary>
    public required IReadOnlyList<string> EliminatedTeams { get; init; }

    /// <summary>How the encounter was decided. <see cref="EncounterOutcome.Ongoing"/> while it is not over.</summary>
    public required EncounterOutcome Outcome { get; init; }

    /// <summary>What became of each character who has left active combat, in state order.</summary>
    public required IReadOnlyList<CharacterResolution> Resolutions { get; init; }

    public required string Description { get; init; }

    /// <summary>A multi-line account of what happened to each character who left the fight; empty when none have.</summary>
    public string ResolutionSummary => string.Join("\n", Resolutions.Select(r => r.Summary));
}

/// <summary>
/// The v0.5 terminal condition: an encounter ends the moment no more than one team has an active member.
/// </summary>
/// <remarks>
/// This replaces the v0.2 rule ("all of one side are dead") because a team can now leave the fight without
/// dying — by surrendering or escaping. A team remains an active contender only while it has a character
/// whose disposition is <see cref="CharacterDisposition.Active"/>. A voluntary <c>end_turn</c> leaves a
/// character active and so can never satisfy this on its own. Written for any number of teams so a
/// single-hero-versus-single-monster scenario (two teams of one) resolves under the same rule as a 2v2.
/// </remarks>
public static class TerminalCondition
{
    public static TerminalConditionResult Evaluate(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var standings = state.Teams()
            .Select(team => new TeamStanding(
                team,
                Active: state.Characters.Count(c => c.CanAct && Same(c.Team, team)),
                Surrendered: state.Characters.Count(c => c.Disposition == CharacterDisposition.Surrendered && Same(c.Team, team)),
                Escaped: state.Characters.Count(c => c.Disposition == CharacterDisposition.Escaped && Same(c.Team, team)),
                Dead: state.Characters.Count(c => c.Disposition == CharacterDisposition.Dead && Same(c.Team, team))))
            .ToList();

        var eliminated = standings.Where(s => s.IsEliminated).Select(s => s.Team).ToList();
        var surviving = standings.Where(s => s.IsActiveContender).Select(s => s.Team).ToList();

        // A single-team scenario can never reach a team terminal condition; the harness limits stop it. With
        // two or more teams, the encounter ends the moment at most one of them still has an active fighter.
        var isOver = standings.Count >= 2 && surviving.Count <= 1;

        // A complete record of everyone who has left active combat, in state order — the basis for both the
        // resolution summary and the outcome classification.
        var resolutions = state.Characters
            .Where(c => c.Disposition != CharacterDisposition.Active)
            .Select(c => new CharacterResolution(
                c.Id, c.Name, c.Team, c.Disposition, ExitNameFor(state, c.EscapedThroughExitId)))
            .ToList();

        var outcome = ClassifyOutcome(isOver, surviving, eliminated, state);

        return new TerminalConditionResult
        {
            IsOver = isOver,
            Standings = standings,
            WinningTeams = isOver ? surviving : [],
            EliminatedTeams = isOver ? eliminated : [],
            Outcome = outcome,
            Resolutions = resolutions,
            Description = Describe(isOver, outcome, surviving, eliminated)
        };
    }

    /// <summary>
    /// Classifies how the encounter ended from the dispositions of the defeated team's members. Only meaningful
    /// once the encounter is over; while it continues the outcome is <see cref="EncounterOutcome.Ongoing"/>.
    /// </summary>
    private static EncounterOutcome ClassifyOutcome(
        bool isOver, IReadOnlyList<string> surviving, IReadOnlyList<string> eliminated, GameState state)
    {
        if (!isOver)
        {
            return EncounterOutcome.Ongoing;
        }

        if (surviving.Count == 0)
        {
            return EncounterOutcome.Draw;
        }

        // Exactly one active team remains; every member of every other team has left active combat. Classify
        // from how those members left: all one way is a clean category, a combination is Mixed.
        var defeatedDispositions = state.Characters
            .Where(c => eliminated.Contains(c.Team, StringComparer.OrdinalIgnoreCase))
            .Select(c => c.Disposition)
            .ToList();

        var anyDead = defeatedDispositions.Contains(CharacterDisposition.Dead);
        var anySurrendered = defeatedDispositions.Contains(CharacterDisposition.Surrendered);
        var anyEscaped = defeatedDispositions.Contains(CharacterDisposition.Escaped);
        var kinds = (anyDead ? 1 : 0) + (anySurrendered ? 1 : 0) + (anyEscaped ? 1 : 0);

        if (kinds > 1)
        {
            return EncounterOutcome.Mixed;
        }

        if (anyDead)
        {
            return EncounterOutcome.Elimination;
        }

        if (anySurrendered)
        {
            return EncounterOutcome.Surrender;
        }

        return EncounterOutcome.Withdrawal;
    }

    private static string Describe(
        bool isOver, EncounterOutcome outcome, IReadOnlyList<string> surviving, IReadOnlyList<string> eliminated)
    {
        if (!isOver)
        {
            return "The encounter continues: more than one team still has an active fighter.";
        }

        if (outcome == EncounterOutcome.Draw)
        {
            return "Draw: no active combatants remain.";
        }

        var fallen = string.Join(" and ", eliminated);
        return surviving.Count == 1
            ? $"{surviving[0]} win: {fallen} has no active combatants remaining."
            : $"{string.Join(" and ", surviving)} remain; {fallen} has no active combatants remaining.";
    }

    private static string? ExitNameFor(GameState state, string? exitId) =>
        exitId is null ? null : state.Exits.FirstOrDefault(e => Same(e.Id, exitId))?.Name;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
