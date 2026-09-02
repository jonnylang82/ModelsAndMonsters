namespace ModelsAndMonsters.Agents;

/// <summary>Optional human input at a decision boundary. Null delegates this decision to the existing AI.</summary>
public interface ICharacterInput
{
    Task<GuestDecision?> ReadAsync(string characterId, CancellationToken cancellationToken);
}

public sealed record GuestDecision(string Intent, string? Speech = null, bool Pass = false);
