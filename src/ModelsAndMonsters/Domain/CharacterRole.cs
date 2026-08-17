namespace ModelsAndMonsters.Domain;

/// <summary>
/// Distinguishes the two v0.1 actors. Roles carry no mechanical meaning: Hero and Monster use the
/// same character-agent implementation and the same engine rules. The role only drives turn order
/// and the terminal condition.
/// </summary>
public enum CharacterRole
{
    Hero,
    Monster
}
