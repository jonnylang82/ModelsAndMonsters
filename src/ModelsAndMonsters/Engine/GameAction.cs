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

/// <summary>Opens a closed container in the actor's room. Uses no randomness.</summary>
/// <remarks>
/// Opening reveals the contents only to the opener, not to the whole room: the engine reports the contents
/// on the outcome, and the orchestration layer delivers them privately. The mechanical change is only the
/// lid.
/// </remarks>
public sealed record OpenContainerAction(string ActorRef, string ContainerRef) : GameAction
{
    public override string ActionType => "open_container";

    public override string Describe() => $"OpenContainer(actor={ActorRef}, container={ContainerRef})";
}

/// <summary>
/// Examines an object in the room closely, discovering information that cannot be read from the general
/// room description. Uses no randomness and never mutates the object: it consumes the actor's turn and
/// yields a private observation to the inspector. A character requests it through natural-language intent
/// ("I wipe the grime away and study the markings"); only the Dungeon Master ever names this action.
/// </summary>
public sealed record InspectObjectAction(string ActorRef, string ObjectRef) : GameAction
{
    public override string ActionType => "inspect_object";

    public override string Describe() => $"InspectObject(actor={ActorRef}, object={ObjectRef})";
}

/// <summary>
/// Transfers one item from an open container into the actor's inventory. Uses no randomness. The
/// removal and the addition are one atomic state change, so two characters can never both acquire it.
/// </summary>
public sealed record TakeItemAction(string ActorRef, string ContainerRef, string ItemRef) : GameAction
{
    public override string ActionType => "take_item";

    public override string Describe() => $"TakeItem(actor={ActorRef}, container={ContainerRef}, item={ItemRef})";
}
