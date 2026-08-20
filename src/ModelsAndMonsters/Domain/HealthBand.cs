namespace ModelsAndMonsters.Domain;

/// <summary>
/// The project's one health-band classification, extracted so that a rule which needs to ask "is this
/// character badly wounded?" reads the same definition the prompts and the answer projection already use.
/// </summary>
/// <remarks>
/// The thresholds are exactly the ones the three prose renderers carried before v0.8; only the classification
/// moved here. The renderers keep their own wording (a character's self-state says "barely standing, close to
/// death" where the battle-state summary says "barely standing"), because those phrasings are audience
/// choices, not rules.
/// </remarks>
public enum HealthBand
{
    Unhurt,
    LightlyWounded,
    Wounded,
    BadlyWounded,

    /// <summary>Worse than badly wounded: still standing, but only just.</summary>
    BarelyStanding,

    Dead
}

public static class HealthBands
{
    /// <summary>Classifies a character by the share of their maximum health they have left.</summary>
    public static HealthBand Of(Character character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return character.IsAlive ? Of(character.Health, character.MaxHealth) : HealthBand.Dead;
    }

    /// <summary>Classifies a health fraction. A character with no maximum health is treated as unhurt.</summary>
    public static HealthBand Of(int health, int maxHealth)
    {
        if (health <= 0)
        {
            return HealthBand.Dead;
        }

        var fraction = maxHealth <= 0 ? 1.0 : (double)health / maxHealth;
        return fraction switch
        {
            >= 0.999 => HealthBand.Unhurt,
            >= 0.75 => HealthBand.LightlyWounded,
            >= 0.45 => HealthBand.Wounded,
            >= 0.20 => HealthBand.BadlyWounded,
            _ => HealthBand.BarelyStanding
        };
    }

    /// <summary>
    /// True when a character is at least badly wounded — the existing "badly wounded" band OR the worse band
    /// below it. A mechanic that gives an edge against the badly wounded must also give it against the
    /// barely-standing, or a character one blow from death would be a harder mark than one merely bloodied.
    /// </summary>
    public static bool IsBadlyWoundedOrWorse(Character character) =>
        Of(character) is HealthBand.BadlyWounded or HealthBand.BarelyStanding;
}
