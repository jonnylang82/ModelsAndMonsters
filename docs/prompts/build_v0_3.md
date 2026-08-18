# Models & Monsters v0.3 — Communication and the Contested Chest

Extend the existing Models & Monsters v0.2 implementation with one tightly scoped vertical slice combining:

1. Public character-to-character communication.
2. Persistent object and container interaction.

The demonstration scenario remains:

> Rowan and Elara versus Vark and Skrit.

Add one contested chest containing one healing potion. The purpose of v0.3 is to test whether autonomous characters can communicate a simple plan and interact coherently with shared persistent state while combat continues.

Preserve the existing architecture, explicit orchestration, team combat, RNG, tracing, context-window protection, conversation isolation, model abstraction, report generation, and test coverage.

Do not redesign working v0.2 systems or expand into a general adventure engine.

## Existing character protocol

Characters currently interact using:

```text
ask_dm(question)
take_action(intent)
end_turn(reason)
```

Add:

```text
say(message)
```

Characters must continue to communicate through these natural in-world tools. They must not receive engine tools or structured object-action functions directly.

## Public speech

`say(message)` represents the current character speaking aloud.

For v0.3, all speech is public to everyone alive in the room.

Examples:

```text
say("Elara, get whatever is in that chest. I'll hold off the captain.")
```

```text
say("Skrit, keep them away from my chest!")
```

### Speech rules

- Speech does not consume the character’s action.
- A character may speak at most once per turn.
- Make the limit configurable as `MaxSpeechActsPerTurn`, defaulting to `1`.
- After speaking, the same character may still:
  - Ask the DM a question.
  - Attempt an action.
  - End its turn.
- Speaking still requires a character model call and therefore counts towards the existing model-call safety limit.
- Speech cannot occur outside the speaker’s turn.
- Speech does not trigger immediate responses from other agents.
- Other characters may respond naturally when their own turns arrive.
- Dead characters do not speak or receive new speech.
- Once the encounter reaches a terminal condition, no further speech occurs.
- Reject empty or unreasonably large messages at the harness boundary.
- Speech must contain spoken words, not third-person narration or an attempt to mutate the world.

The speaker may address somebody by name, but everybody alive in the single room can hear it.

Do not add a recipient argument, whispering, private speech, distance-based hearing, languages, interruptions, reactions, persuasion rolls, or commands that mechanically compel another character.

### Speech is not authoritative

A spoken statement is a character’s claim, belief, instruction, threat, or opinion.

For example:

> “The captain is nearly dead!”

does not change or override authoritative engine state.

Recipients should remember that Rowan said this, not treat it as a new engine fact. Fresh authoritative state injection must continue to win over anything characters say.

### Speech delivery

Route speech explicitly through the orchestrator as a public event.

Do not make another DM model call merely to paraphrase speech. Preserve the speaker’s words verbatim and deliver them with clear attribution:

```text
Rowan says:
"Elara, get whatever is in that chest. I'll hold off the captain."
```

The DM remains the information broker, but exact spoken dialogue does not require model interpretation.

Deliver the speech to:

- The Dungeon Master’s application-owned history.
- The speaker’s history.
- Every other living character in the room.

Do not trigger recipient model calls immediately. Include newly heard speech in their bounded context when their next turns occur.

## Object model

Add the minimum reusable domain representation required for this scenario.

Use or extend existing domain types rather than creating a parallel item system.

Conceptually support:

```text
WorldObject
Container
Item
```

The exact class structure should fit the existing codebase and remain simple.

A container needs enough state to represent:

```text
Id
Name
Description
IsOpen
Contents
```

The room must be able to contain world objects.

Do not build inheritance hierarchies or generic property systems unless the existing design naturally calls for them.

## The contested chest

Add one chest to the v0.2 room.

```text
Name:
Old Iron-Bound Chest

Initial state:
Closed

Lock:
None — it opens normally

Contents:
One Small Healing Potion
```

The chest is visible to everyone from the beginning.

While it is closed:

- Characters can see the chest.
- Characters cannot see its contents.
- The DM knows its authoritative contents.
- The DM must not reveal the contents as visible fact.

Once it is open:

- Its contents become publicly observable.
- Later public narration may mention the visible potion.
- Any living character may attempt to take the potion.

Do not introduce a general character-specific knowledge system in this pass. Opening the chest publicly reveals its contents to everyone alive in the room.

## Observation

Do not add an `inspect_object` engine action.

Characters use `ask_dm` for ordinary perception:

```text
ask_dm("Does the chest appear to be open?")
```

```text
ask_dm("Can I see what is inside the chest?")
```

```text
ask_dm("Does the chest have a visible lock?")
```

The DM answers from authoritative state and observable information.

Asking does not mutate the object or consume the turn.

## New engine actions

Add exactly two DM-to-engine actions:

```text
open_container(actor, container)
take_item(actor, container, item)
```

Characters never see these functions. They continue to express natural-language intentions through `take_action`.

### `open_container`

Validate:

- The actor exists and is alive.
- The container exists in the actor’s room.
- The container is currently closed.

On acceptance:

- Set `IsOpen` to `true`.
- Increment world-state version consistently.
- Record the state transition.
- End the actor’s turn.
- Ask the DM to narrate only what the engine reports.
- Publicly deliver the accepted outcome and newly visible contents.

Reject without mutation if:

- The actor is invalid or dead.
- The container does not exist.
- The container is already open.
- The reference is ambiguous.

No RNG is used.

### `take_item`

Validate:

- The actor exists and is alive.
- The container exists.
- The container is open.
- The requested item currently exists inside that container.
- The item is not already owned by somebody else.

On acceptance, atomically:

1. Remove the item from the container.
2. Add it to the actor’s inventory.
3. Increment world-state version consistently.
4. Record the complete transfer.
5. End the actor’s turn.
6. Ask the DM to narrate only the accepted transfer.

Reject without mutation if validation fails.

Two characters must never acquire the same item, even if both previously perceived it inside the chest.

Continue using the existing `use_item` action after the potion has entered a character’s inventory. Do not add using items directly from containers.

## Compound actions

Opening the chest and taking its contents are two separate state-changing actions.

An intent such as:

> “I open the chest and grab the potion.”

must not silently perform both actions or discard half of the intent.

The DM should reject the compound action with a concise in-world explanation, allowing the character to retry with one immediate action:

> “You can get the chest open, but searching it and retrieving something will take another moment.”

The rejection must not open the chest, reveal its contents, move an item, or otherwise mutate the fiction.

## Scenario incentives

Preserve the existing v0.2 characters, teams, combat rules, turn order and model profiles.

Make only small scenario or character-goal changes needed to encourage natural use of the new features.

### Rowan

Add a preference to protect Elara and encourage her to secure useful supplies while he engages dangerous opponents.

Do not instruct him mechanically to call `say` or use the chest.

### Elara

Begin Elara injured enough that a healing potion would be useful, while leaving her fully capable of participating.

Add a desire to secure supplies that could keep the heroes alive.

Do not introduce cleric spells, magical healing, or healing another character.

### Vark

Make it clear in Vark’s identity that the chest and its contents belong to him and that he expects Skrit to help protect them.

### Skrit

Preserve his desire for Vark’s approval, while allowing fear and self-preservation to compete with Vark’s orders.

These should be character motivations, not scripted decisions.

## Turn and terminal rules

Preserve the existing fixed v0.2 order:

```text
Rowan
Elara
Vark
Skrit
```

Opening a container consumes the turn.

Taking an item consumes the turn.

Speaking does not consume the turn.

The existing team terminal conditions remain authoritative:

- Heroes win when no goblins remain alive.
- Goblins win when no heroes remain alive.

Chest ownership is not a terminal condition.

Continue skipping dead actors without model calls.

## Dungeon Master adjudication

Extend the DM’s supported action interpretation to include:

```text
attack_character
use_item
open_container
take_item
reject_action
```

The DM must:

- Use stable actor, container and item references.
- Never confuse a character with another team member.
- Never silently substitute another object or item.
- Never claim a container opened unless the engine accepted it.
- Never claim an item was taken unless the engine transferred it.
- Never reveal closed-container contents as observable information.
- Never invent a lock, trap, key, hidden compartment or additional item.
- Continue stripping unsupported hoped-for effects from otherwise valid attacks.
- Reject unsupported actions without mutating the fiction.

Fresh authoritative state remains the source of truth.

## Context management

Communication will increase every character’s history.

Preserve the existing context-window guard and bounded history projections.

The application must retain the complete canonical interaction in its trace, while sending each model only its intended bounded projection.

Public speech may be omitted from later model calls by the established context policy, but it must never disappear from the application trace or generated report.

A character’s private DM questions, answers and rejections must remain isolated from other characters.

## RNG regression requirement

The new object and speech systems do not require RNG. Preserve the existing combat RNG behaviour unchanged.

**Ensure every RNG draw records its seed/state, purpose, candidates, raw roll, modifiers, and result. Otherwise the new hit chance will make behavioural comparisons much harder to reproduce.**

Existing seeded replay behaviour must continue to work.

## Tracing

Add explicit trace coverage for communication and object interaction.

### Speech events

Record:

```text
SpeakerId
SpeakerName
SpeakerTeam
Message
Round
Turn
SpeechIndexWithinTurn
Recipients
DeliveryMechanism
```

Make it possible to verify that the message reached every intended living recipient and nobody else.

### Object events

Record:

```text
ActorId
ActionType
ObjectId
ContainerId
ItemId
ValidationResult
RejectionReason
StateBefore
StateAfter
WorldVersionBefore
WorldVersionAfter
```

For successful item transfer, record both the container removal and inventory addition.

### Report

Extend the generated report with:

- Initial room objects and container state.
- Initial container contents in the authoritative scenario section.
- Speech in the readable transcript.
- Speech recipients in detailed trace sections.
- Container openings.
- Item transfers.
- Rejected object interactions.
- Final container contents.
- Final character inventories.
- Per-character speech counts.
- Object-action attempt, acceptance and rejection counts.

Keep private DM questions visually distinct from public speech.

## Tests

Add or update tests covering at least:

1. `say` is exposed to character agents.
2. Speaking does not end the turn.
3. A character may speak only once per turn by default.
4. Empty speech is rejected safely.
5. Speech is delivered verbatim with speaker attribution.
6. Speech reaches every living character in the room.
7. Dead characters do not receive new speech.
8. Speech does not trigger immediate recipient model calls.
9. Speech does not mutate authoritative state.
10. Spoken claims cannot override authoritative state.
11. Private DM exchanges remain isolated after public speech is added.
12. The room seeds exactly one closed chest.
13. The potion begins inside the chest and not in any character inventory.
14. Closed-container contents are not exposed as publicly visible state.
15. Opening the chest changes only its open state and consumes the turn.
16. Opening an already open chest is rejected without mutation.
17. Taking from a closed chest is rejected without mutation.
18. Taking from an open chest atomically transfers the item to inventory.
19. A second attempt to take the same item is rejected.
20. Invalid and ambiguous object references are rejected safely.
21. A compound open-and-take request cannot perform both operations.
22. Object actions use no RNG.
23. Existing combat RNG trace and replay tests continue to pass.
24. The generated report reconstructs speech, object state and item ownership.
25. Existing v0.2 tests continue to pass.

## Explicitly out of scope

Do not add:

- Private speech or whispers.
- Languages or hearing distance.
- Immediate conversational interruptions.
- Character-to-character item giving.
- Persuasion or intimidation mechanics.
- Speech-based mechanical bonuses.
- Locked containers.
- Keys, lockpicking or traps.
- Breaking or attacking objects.
- Putting items into containers.
- Generic object verbs.
- Doors.
- Additional rooms.
- Character-specific knowledge storage.
- Abilities or spells.
- Status effects.
- Healing another character.
- Fleeing or surrender mechanics.
- Campaign persistence.
- Human-controlled characters.
- New UI or API surfaces.
- Model-comparison experiments.

## Completion criteria

v0.3 is complete when:

- Characters can speak publicly without consuming their action.
- Speech is correctly delivered, isolated, traced and reported.
- The chest persists as authoritative shared world state.
- Characters can open it and transfer its potion into an inventory.
- Invalid and competing object interactions are safely rejected.
- Combat, team behaviour, RNG replay and context protection still work.
- Existing and new tests pass.
- A generated report can reconstruct the complete encounter, including dialogue and object-state transitions.

The autonomous demonstration run should ideally contain at least one public speech and one object interaction.

Do not hard-code character decisions merely to force that outcome. If the models ignore the chest, prove the mechanics through deterministic tests and report the natural run honestly.

Stop when this vertical slice is working. Do not expand into further adventure mechanics.
