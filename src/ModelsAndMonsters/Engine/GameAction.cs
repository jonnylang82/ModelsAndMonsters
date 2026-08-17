namespace ModelsAndMonsters.Engine;

/// <summary>
/// A structured, state-changing request against the game engine.
/// </summary>
/// <remarks>
/// Only the Dungeon Master produces these, by translating a character's natural-language intent into
/// one of the deliberately small number of supported actions. Character agents never see this type.
/// References are free-form strings (id or name) because the DM speaks in names; resolving and
/// rejecting them is the engine's job.
/// </remarks>
public abstract record GameAction
{
    public abstract string ActionType { get; }

    /// <summary>Short human-readable form used in traces and console diagnostics.</summary>
    public abstract string Describe();
}

/// <summary>The single combat action supported by v0.1. An accepted attack always hits.</summary>
public sealed record AttackCharacterAction(string AttackerRef, string TargetRef, string WeaponRef) : GameAction
{
    public override string ActionType => "attack_character";

    public override string Describe() => $"AttackCharacter(attacker={AttackerRef}, target={TargetRef}, weapon={WeaponRef})";
}

/// <summary>Consumes an inventory item. v0.1 only understands healing items used on oneself.</summary>
public sealed record UseItemAction(string ActorRef, string ItemRef, string? TargetRef = null) : GameAction
{
    public override string ActionType => "use_item";

    public override string Describe() => $"UseItem(actor={ActorRef}, item={ItemRef}, target={TargetRef ?? "self"})";
}
