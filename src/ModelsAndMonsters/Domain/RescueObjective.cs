namespace ModelsAndMonsters.Domain;

/// <summary>An opt-in rescue checkpoint. Clearing the guards unlocks detention; extraction completes it.</summary>
public sealed record RescueObjective(string DetaineeId, string GuardTeam, string ExitId)
{
    public string Stage(GameState state)
    {
        var detainee = state.RequireById(DetaineeId);
        if (detainee.Disposition == CharacterDisposition.Escaped)
            return detainee.EscapedThroughExitId == ExitId ? "completed" : "failed";
        if (detainee.Disposition == CharacterDisposition.Detained)
            return state.ActiveOnTeam(detainee.Team).Any() ? "detained" : "failed";
        return detainee.CanAct ? "extraction" : "failed";
    }
}
