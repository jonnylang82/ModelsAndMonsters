namespace ModelsAndMonsters.Domain;

/// <summary>
/// A persistent, descriptive injury. Injuries have no mechanical effect in v0.1; they exist to
/// prove that descriptive state can persist in the authoritative model and re-enter later prompts.
/// </summary>
/// <param name="Description">Human readable description, e.g. "Minor cut to left arm".</param>
/// <param name="InflictedOnRound">The round on which the injury was recorded, or 0 when seeded.</param>
public sealed record Injury(string Description, int InflictedOnRound = 0);
