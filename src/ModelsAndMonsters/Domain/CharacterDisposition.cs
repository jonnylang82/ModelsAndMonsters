namespace ModelsAndMonsters.Domain;

/// <summary>
/// How a character stands in relation to the encounter. This replaces the v0.4 assumption that every
/// living character is an active combatant: a character may still be alive yet have left the fight, by
/// yielding or by fleeing through an exit.
/// </summary>
/// <remarks>
/// Disposition is the single authoritative domain type for a character's standing. The old boolean
/// <see cref="Character.IsAlive"/> and the new presence/target predicates all derive from it rather than
/// from independent flags, so the state can never contradict itself (a character cannot be both "dead"
/// and "an active combatant"). See <see cref="Character"/> for the derivations.
/// </remarks>
public enum CharacterDisposition
{
    /// <summary>Alive, present, taking turns, and a valid combat target — the ordinary fighting state.</summary>
    Active,

    /// <summary>Alive and still present, but has yielded: takes no further turns and cannot be attacked in v0.5.</summary>
    Surrendered,

    /// <summary>Alive but gone: left the encounter through an exit, so it takes no turns and cannot be reached.</summary>
    Escaped,

    /// <summary>Not alive. Uses the existing death, injury and inventory-on-death behaviour.</summary>
    Dead,

    /// <summary>Alive and present but confined: takes no turns and cannot be targeted until released.</summary>
    Detained
}
