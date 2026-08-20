using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Engine;

/// <summary>
/// Renders an engine rejection as a sentence spoken to the character, deterministically, from the rejection
/// <em>code</em> and a small set of bound names — never from model prose.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the previous arrangement had the causality backwards. The engine produced an
/// operator-facing message ("Vark is not carrying 'Vial of Goblin Salve'"), a model was asked to turn that
/// into fiction, and a growing regex was asked to notice when the model had instead explained the machinery.
/// Every run found another phrasing the regex did not have, and the rewrite that followed a catch was
/// sometimes worse than the leak — inventing obstacles that were not true, or narrating events inside a
/// refusal that by definition changes nothing.
/// </para>
/// <para>
/// A rejection is not free-form information. It is a closed set of reasons, each with a handful of bound
/// facts the character is entitled to know: who they named, what they reached for. Rendering from the code
/// makes the in-world wording a property of the code rather than a property of whichever model ran, so it
/// cannot leak, cannot invent, cannot narrate an event, and costs no model call. The detector downstream
/// becomes what it should always have been: a lint that never fires on this path.
/// </para>
/// </remarks>
public static class InWorldRefusal
{
    /// <summary>
    /// The line a character is told when the world refuses something and no more specific wording applies.
    /// Deliberately says only that nothing came of it — the honest thing, and true of every refusal.
    /// </summary>
    public const string Generic = "Nothing comes of that, and the moment passes.";

    /// <summary>
    /// Renders a refusal for the given reason, binding only names already visible to the acting character.
    /// </summary>
    /// <param name="reason">The engine's rejection code.</param>
    /// <param name="actorName">The acting character's name.</param>
    /// <param name="subjectName">
    /// The thing the refusal is about when the code needs one — a named target, item, container, object or
    /// exit — already resolved to how the character would refer to it. Null when the code needs none.
    /// </param>
    public static string Render(EngineRejectionReason reason, string actorName, string? subjectName = null)
    {
        var subject = string.IsNullOrWhiteSpace(subjectName) ? null : subjectName.Trim();
        var it = subject is null ? "it" : $"the {subject}";

        return reason switch
        {
            // Who is being acted on.
            EngineRejectionReason.UnknownTarget or EngineRejectionReason.UnknownActor =>
                subject is null
                    ? "There is nobody here by that name."
                    : $"There is nobody here called {subject}.",
            EngineRejectionReason.UnknownRecipient or EngineRejectionReason.RecipientNotPresent
                or EngineRejectionReason.TargetNotPresent =>
                subject is null
                    ? "They are not here to reach."
                    : $"{subject} is not here to reach.",
            // Kept apart from the other self-targeting cases because the generic line is baffling here: a
            // live run answered a character's muddled offer with "You cannot do that to yourself", which
            // tells them nothing about what went wrong with the terms they were trying to put.
            EngineRejectionReason.RecipientIsSelf =>
                "Terms have to be put to somebody on the other side; you cannot bargain with yourself.",
            EngineRejectionReason.TargetIsSelf or EngineRejectionReason.AbilityTargetIsSelf =>
                "You cannot do that to yourself.",
            EngineRejectionReason.TargetIsDead =>
                subject is null ? "They are already dead." : $"{subject} is already dead.",
            EngineRejectionReason.TargetHasSurrendered =>
                subject is null
                    ? "They have given up their fight, and you will not strike them now."
                    : $"{subject} has given up the fight; there is nothing left to do to them.",
            EngineRejectionReason.TargetHasEscaped =>
                subject is null ? "They are gone from this room." : $"{subject} is gone from this room.",
            EngineRejectionReason.ActorIsDead or EngineRejectionReason.ActorNotActive =>
                "You are in no state to do that.",

            // Weapons and items in hand.
            EngineRejectionReason.ActorHasNoWeapon => "You have nothing in your hands to strike with.",
            EngineRejectionReason.WeaponNotPossessed =>
                subject is null ? "That is not the weapon you are holding." : $"You are not holding {it}.",
            EngineRejectionReason.ItemNotPossessed =>
                subject is null ? "You are not carrying that." : $"You are not carrying {it}.",
            EngineRejectionReason.ItemHasNoSupportedEffect =>
                subject is null ? "Nothing comes of using it." : $"Nothing comes of using {it} that way.",
            EngineRejectionReason.ItemTargetNotSupported =>
                "You can only use something like that on yourself.",
            EngineRejectionReason.EquippedWeaponCannotBeTransferred =>
                "The weapon in a fighter's hand stays there; it does not change hands in the middle of a fight.",

            // Containers, objects and the floor.
            EngineRejectionReason.UnknownContainer or EngineRejectionReason.UnknownObject =>
                subject is null ? "There is nothing like that here." : $"There is no {subject} here.",
            EngineRejectionReason.ContainerAlreadyOpen =>
                subject is null ? "It already stands open." : $"{Capitalise(it)} already stands open.",
            EngineRejectionReason.ContainerClosed =>
                subject is null ? "It is shut, and you have not had the lid up." : $"{Capitalise(it)} is still shut.",
            EngineRejectionReason.ItemNotInContainer =>
                subject is null ? "It is not in there." : $"There is no {subject} in there.",
            EngineRejectionReason.NothingToInspect =>
                subject is null ? "A closer look tells you nothing more." : $"A closer look at {it} tells you nothing more.",
            EngineRejectionReason.ContainerReferenceAmbiguous or EngineRejectionReason.ItemReferenceAmbiguous
                or EngineRejectionReason.ObjectReferenceAmbiguous or EngineRejectionReason.ExitReferenceAmbiguous =>
                "There is more than one of those; you would have to be clearer about which.",

            // Ways out.
            EngineRejectionReason.UnknownExit =>
                subject is null ? "There is no such way out of here." : $"There is no {subject} to leave by.",
            EngineRejectionReason.ExitAlreadyOpen =>
                subject is null ? "It already stands open." : $"{Capitalise(it)} already stands open.",
            EngineRejectionReason.ExitClosed =>
                subject is null ? "It is shut; it would have to be hauled open first." : $"{Capitalise(it)} is shut, and would have to be hauled open first.",

            // Terms of surrender.
            EngineRejectionReason.OfferHasNoConcession =>
                "Terms with nothing behind them are only words; you would have to promise something real.",
            EngineRejectionReason.RecipientIsNotAnOpponent =>
                "You would be putting terms to your own side, which settles nothing.",
            EngineRejectionReason.OfferedItemNotOwned =>
                subject is null ? "You cannot promise what you are not carrying." : $"You cannot promise {it}; it is not yours to give.",
            EngineRejectionReason.OfferedItemNotTransferable or EngineRejectionReason.OfferedWeaponNotHeld =>
                "That is not yours to hand over.",
            EngineRejectionReason.DuplicatePendingOffer =>
                "You have already put terms to somebody, and they have not answered yet.",
            EngineRejectionReason.UnknownOffer or EngineRejectionReason.OfferNoLongerPending =>
                "There are no terms standing for you to take up.",
            EngineRejectionReason.OfferNotAddressedToActor =>
                "Those terms were not put to you, and are not yours to take.",
            EngineRejectionReason.OffererNotAvailable =>
                "Whoever offered is in no state to make good on it now.",
            EngineRejectionReason.PromisedAssetNoLongerAvailable =>
                "What was promised is no longer theirs to hand over.",

            // Trained techniques.
            EngineRejectionReason.UnknownAbility or EngineRejectionReason.AbilityNotHeld =>
                "You have never learned to do that.",
            EngineRejectionReason.AbilityHasNoUsesLeft =>
                "You have nothing left in you for that again.",
            EngineRejectionReason.AbilityTargetRequired =>
                "You would have to say who that is for.",
            EngineRejectionReason.AbilityTargetNotAllowed or EngineRejectionReason.AbilityTargetNotAvailable =>
                subject is null ? "You cannot do that for them." : $"You cannot do that for {subject}.",
            EngineRejectionReason.AbilityTargetNotAlly =>
                "That is something you do for a companion, not for a foe.",
            EngineRejectionReason.AbilityTargetNotOpponent =>
                "That is something you do to a foe, not to a companion.",
            EngineRejectionReason.TargetAlreadyAtFullHealth =>
                subject is null ? "They have no wound left for it to close." : $"{subject} has no wound left for it to close.",
            EngineRejectionReason.AbilityAlreadyActive =>
                "You are already doing that.",
            EngineRejectionReason.TargetAlreadyGuarded =>
                subject is null
                    ? "Somebody already stands over them; there is no room for a second."
                    : $"Somebody already stands over {subject}; there is no room for a second.",

            // Threats and reassurance.
            EngineRejectionReason.TargetIsNotAnOpponent =>
                subject is null
                    ? "They fight at your side; there is nothing to frighten them with."
                    : $"{subject} fights at your side; there is nothing to frighten them with.",
            EngineRejectionReason.TargetIsNotAnAlly =>
                subject is null
                    ? "They are no companion of yours; nothing you say will steady them."
                    : $"{subject} is no companion of yours; nothing you say will steady them.",
            EngineRejectionReason.AlreadyAttemptedIntimidation =>
                subject is null
                    ? "You have already tried to put the fear into them once, and they have your measure now."
                    : $"You have already tried to put the fear into {subject} once, and they have your measure now.",
            EngineRejectionReason.IntimidationRequiresSpeech =>
                "You said nothing aloud, and a look alone carries no weight across a fight.",
            EngineRejectionReason.SpeechAddressedToSomebodyElse =>
                subject is null
                    ? "Your words were meant for somebody else, and they landed elsewhere."
                    : $"Your words were meant for somebody else, not {subject}.",

            _ => Generic
        };
    }

    /// <summary>
    /// The name the refusal should be about for this action and reason, or null when the code needs none.
    /// Kept beside the renderer so a new action binds its subject in one place.
    /// </summary>
    public static string? SubjectFor(GameAction action, EngineRejectionReason reason, GameState state) =>
        reason switch
        {
            EngineRejectionReason.UnknownTarget or EngineRejectionReason.TargetIsDead
                or EngineRejectionReason.TargetHasSurrendered or EngineRejectionReason.TargetHasEscaped
                or EngineRejectionReason.TargetNotPresent or EngineRejectionReason.UnknownRecipient
                or EngineRejectionReason.RecipientNotPresent or EngineRejectionReason.TargetAlreadyAtFullHealth
                or EngineRejectionReason.TargetAlreadyGuarded or EngineRejectionReason.AbilityTargetNotAllowed
                or EngineRejectionReason.AbilityTargetNotAvailable or EngineRejectionReason.TargetIsNotAnOpponent
                or EngineRejectionReason.TargetIsNotAnAlly or EngineRejectionReason.AlreadyAttemptedIntimidation
                or EngineRejectionReason.SpeechAddressedToSomebodyElse => CharacterName(action, state),

            EngineRejectionReason.ItemNotPossessed or EngineRejectionReason.ItemHasNoSupportedEffect
                or EngineRejectionReason.ItemNotInContainer or EngineRejectionReason.OfferedItemNotOwned
                or EngineRejectionReason.WeaponNotPossessed => ItemName(action),

            EngineRejectionReason.UnknownContainer or EngineRejectionReason.ContainerAlreadyOpen
                or EngineRejectionReason.ContainerClosed or EngineRejectionReason.UnknownObject
                or EngineRejectionReason.NothingToInspect => ObjectName(action),

            EngineRejectionReason.UnknownExit or EngineRejectionReason.ExitAlreadyOpen
                or EngineRejectionReason.ExitClosed => ExitName(action),

            _ => null
        };

    private static string? CharacterName(GameAction action, GameState state)
    {
        var reference = action switch
        {
            AttackCharacterAction attack => attack.TargetRef,
            StealItemAction steal => steal.TargetRef,
            GiveItemAction give => give.RecipientRef,
            UseAbilityAction ability => ability.TargetRef,
            IntimidateCharacterAction intimidate => intimidate.TargetRef,
            SteadyAllyAction steady => steady.TargetRef,
            _ => null
        };

        // Prefer the name the world knows over whatever reference was passed, so a refusal never echoes an
        // internal id back at a character.
        return reference is null ? null : state.Resolve(reference)?.Name ?? reference;
    }

    private static string? ItemName(GameAction action) => action switch
    {
        UseItemAction use => use.ItemRef,
        StealItemAction steal => steal.ItemRef,
        GiveItemAction give => give.ItemRef,
        DropItemAction drop => drop.ItemRef,
        TakeItemAction take => take.ItemRef,
        AttackCharacterAction attack => attack.WeaponRef,
        _ => null
    };

    private static string? ObjectName(GameAction action) => action switch
    {
        OpenContainerAction open => open.ContainerRef,
        TakeItemAction take => take.ContainerRef,
        InspectObjectAction inspect => inspect.ObjectRef,
        _ => null
    };

    private static string? ExitName(GameAction action) => action switch
    {
        OpenExitAction open => open.ExitRef,
        EscapeEncounterAction escape => escape.ExitRef,
        _ => null
    };

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
