# Models & Monsters v0.4 — Hidden Information and Character Knowledge

Extend the existing Models & Monsters v0.3 implementation with a tightly scoped hidden-information and character-specific knowledge vertical slice.

The purpose of v0.4 is to answer:

> Can characters discover information privately, communicate it to allies, distinguish observation from hearsay, and act without receiving knowledge they could not realistically possess?

Preserve the existing architecture, 2v2 combat, communication, object interaction, inventory, death-looting behaviour, explicit orchestration, model abstraction, RNG, context protection, tracing, reports and tests.

Do not redesign working v0.3 systems.

## Core behavioural change

Being in the same room does not mean every character automatically learns everything that another character sees.

In particular:

- Everyone may see that Elara opened a chest.
- Only Elara automatically sees what is inside it.
- Rowan does not learn its contents merely because he is also in the room.
- Rowan may learn by inspecting the open chest himself.
- Rowan may hear Elara describe the contents using `say`.
- Hearing Elara’s statement is not the same as directly verifying it.
- When an item is removed and visibly held, that removal may become publicly observable.

The application must distinguish:

```text
Objective world truth
Publicly observable facts
Character-specific direct knowledge
Statements heard from other characters
```

## Existing character protocol

Do not add another character-facing tool.

Characters continue using:

```text
ask_dm(question)
say(message)
take_action(intent)
end_turn(reason)
```

Add `inspect_object` only to the Dungeon Master’s engine-action surface. Characters request it naturally through `take_action`:

```text
take_action("I wipe the grime from the chest and examine its markings closely.")
```

## Knowledge model

Add a small application-owned character knowledge model.

Use a structure broadly equivalent to:

```text
KnowledgeFact
- Id
- SubjectId
- FactType
- Value or Description

CharacterKnowledge
- CharacterId
- FactId
- Source
- LearnedAtRound
- LearnedAtTurn
- ObservedWorldVersion
```

Suitable sources include:

```text
Backstory
DirectInspection
OpenedContainer
PublicEvent
```

Keep this deliberately small. Do not build a general ontology, inference engine, confidence-scoring system or knowledge graph.

Knowledge facts should use stable IDs so their discovery and delivery can be traced reliably.

Examples:

```text
fact: medicine-case-marking
subject: shrine-medicine-case
description: The faded shrine mark identifies this as a case intended for medicinal supplies.
```

```text
fact: medicine-case-contents-v1
subject: shrine-medicine-case
description: When Elara opened the case, it contained a Small Healing Potion.
observed-world-version: 4
```

Knowledge records are observations made at a particular world version. They must not magically update when the world later changes.

For example:

- Elara sees a potion inside the chest.
- Skrit later removes it.
- Elara’s historical observation remains true as something she previously saw.
- Current authoritative state says the chest is now empty.
- Later prompts should not present the old observation as guaranteed current truth.

## Spoken information is hearsay

Do not automatically convert public speech into authoritative `CharacterKnowledge`.

If Elara says:

> “The shrine-marked case contains a healing potion.”

Rowan should remember:

> Elara said that the shrine-marked case contains a healing potion.

He must not receive an engine-authenticated fact saying that he directly observed the potion.

Speech remains verbatim dialogue in character history and trace.

The Dungeon Master must distinguish these cases when answering Rowan:

- Direct knowledge: “You saw a healing potion inside.”
- Hearsay: “Elara told you that it contained a healing potion.”
- Unknown: “You have not seen what is inside.”

A character may act on hearsay. Rowan can attempt to take a potion Elara told him about, but the engine still validates the current authoritative state when the action occurs.

Characters may lie, misunderstand or become outdated naturally, but do not add deception mechanics or intentional lying prompts in this pass.

## Information views

Build explicit information views for model calls.

### Dungeon Master authoritative view

The DM may receive:

- Complete current world state.
- All objects and container contents.
- All character knowledge records.
- Relevant public events.
- The current acting or asking character’s information view.

The full state exists so the DM can adjudicate correctly. It does not grant permission to reveal everything.

### Character information view

A character receives:

- Exact current information about itself.
- Current public facts.
- Public narration delivered to it.
- Its own direct knowledge records.
- Speech it has heard.
- Its own private questions and DM answers.
- Its own actions, rejections and results.

A character must not receive:

- Another character’s private inspection result.
- Another character’s private DM answer.
- Closed-container contents it has not discovered.
- Knowledge facts belonging only to somebody else.
- Omniscient object state.

Keep histories independently bounded using the existing context policy.

## Scenario: The Two Supply Cases

Retain Rowan, Elara, Vark and Skrit in the existing one-room 2v2 encounter.

Replace the single contested chest with two closed containers.

### Shrine Medicine Case

```text
Id:
shrine-medicine-case

Name:
Faded Shrine Medicine Case

Initial state:
Closed

Contents:
Small Healing Potion
```

Its exterior is dusty and its markings cannot be understood from the general room description.

A close inspection reveals:

> Beneath the grime is the faded road-shrine mark used for medicinal supplies.

This fact is delivered only to the inspecting character.

### Mill Supply Crate

```text
Id:
mill-supply-crate

Name:
Water-Stained Mill Supply Crate

Initial state:
Closed

Contents:
Bundle of Damp Rags
```

The rags are an ordinary inert item. They do not heal and cannot be used mechanically in this pass.

A close inspection reveals:

> The warped trade mark identifies this as an ordinary mill-supply crate.

Again, deliver this only to the inspecting character.

Both containers are:

- Unlocked.
- Within reach under the existing single-room abstraction.
- Visibly present from the beginning.
- Indistinct enough that their purpose requires close inspection.

Do not add positions or distance mechanics.

## Initial private knowledge

Give Vark direct backstory knowledge of both containers and their initial contents. They belong to him.

Record these as `Backstory` knowledge facts.

Skrit does not automatically share Vark’s knowledge.

Rowan and Elara know only that two closed containers are present.

This creates an immediate reason for Vark to communicate with Skrit without scripting that decision.

Elara’s clerical background should make medicinal supplies desirable, but do not add an inspection bonus or exclusive ability. Any character can discover the exterior markings by spending an action to inspect.

## `inspect_object`

Add a DM-to-engine action:

```text
inspect_object(actor, object)
```

Characters invoke it through natural-language `take_action`.

### Validation

Validate that:

- The actor exists.
- The actor is alive.
- The object exists in the same room.
- The reference resolves unambiguously.
- The object has information that can be discovered by inspection.

### Accepted inspection

An accepted inspection:

- Consumes the character’s turn.
- Does not mutate the physical object.
- Adds appropriate direct knowledge facts to the inspecting character.
- Records the world version at which the observation occurred.
- Returns a private observation to the inspector.
- Produces only a minimal public event saying that the character examined the object.
- Does not reveal the discovered information publicly.
- Uses no RNG in v0.4.

Example public narration:

> Elara wipes grime from the Faded Shrine Medicine Case and examines it closely.

Example private result to Elara:

> Beneath the grime, you recognise the faded road-shrine mark used for medicinal supplies.

Other characters receive the public narration but not the private result.

### Repeat inspection

Repeating an inspection is allowed if current state could have changed—for example, inspecting an open container after an item was removed.

If there is nothing new to discover, the private result may say that the character learns nothing beyond what it already knows.

Do not duplicate identical knowledge records unnecessarily.

## Opening containers

Preserve `open_container`, but change information delivery.

When a character opens a closed container:

### Public result

Everyone alive in the room learns:

- Which character opened it.
- Which container is now open.

The shared public narration must not name its contents.

Example:

> Elara opens the Faded Shrine Medicine Case and looks inside.

### Private result for the opener

The opener directly observes the container’s current contents.

Example:

> Inside, you see a Small Healing Potion.

Add this as direct character knowledge with source `OpenedContainer` and the current world version.

Do not deliver the contents to other characters merely because they received the public opening narration.

### Other characters

Another character may learn the current contents by:

- Inspecting the open container.
- Hearing somebody describe them.
- Observing an item being visibly removed.

They do not learn by automatic narration fan-out.

## Inspecting an open container

Inspecting an open container reveals its current contents privately to the inspector.

Examples:

```text
The open case currently contains a Small Healing Potion.
```

```text
The open case is empty.
```

The result must reflect current authoritative state, not what somebody previously saw or said.

## Taking items

Preserve the existing `take_item` action and validation.

A character may attempt to take an item based on:

- Direct knowledge.
- Hearsay.
- A natural attempt to take whatever it can see after inspecting.

The engine always validates current authoritative state.

### Accepted transfer

When an item is visibly removed from an open container:

- Transfer it atomically.
- Update the world version.
- Record container and inventory changes.
- End the actor’s turn.
- Make the visible removal a public event.

For v0.4, the potion and damp rags are visually identifiable when removed. Therefore everyone alive may learn:

```text
Skrit removed the Small Healing Potion and now carries it.
```

This public event may add an appropriate `PublicEvent` knowledge fact to recipients.

Opening a container does not reveal its contents globally. Removing and visibly holding an identifiable item does.

### Rejected transfer

Reject without mutation if:

- The container is closed.
- The item is no longer present.
- The actor names an item that was never there.
- The object or item reference is ambiguous.
- Another actor already removed the item.

If the acting character relied on stale knowledge or hearsay, keep the rejection in-world:

> You reach into the open case, but the potion is no longer there.

Do not mention knowledge records, engine state, supported actions or internal mechanics.

## Asking the DM

`ask_dm` must respect the asking character’s information view.

Examples:

### Closed, uninspected container

Question:

> “What is inside the medicine case?”

Answer:

> You cannot see through the closed lid, and you have not examined it closely enough to know.

### Opened by somebody else

Question:

> “What did Elara see inside?”

If Elara has not spoken:

> You saw Elara look inside, but you do not know what she saw.

### Hearsay

If Elara previously spoke:

> Elara said that the case contained a healing potion, but you have not verified it yourself.

### Direct observation

If the asker opened or inspected it:

> When you looked inside, you saw a Small Healing Potion.

### Stale knowledge

If the potion has since been removed:

> You previously saw a potion inside, but Skrit has since taken it.

Answer using the best combination of the character’s direct knowledge, speech history and public events without leaking omniscient current state.

## Dungeon Master adjudication

Extend the supported DM action surface to:

```text
attack_character
use_item
inspect_object
open_container
take_item
reject_action
```

The DM must receive an explicit current-actor knowledge section during adjudication.

The DM must:

- Translate inspection intents to `inspect_object`.
- Avoid treating `ask_dm` as a free close inspection.
- Avoid revealing hidden contents through rejection messages.
- Allow characters to act on something another character told them.
- Reject exact hidden-item knowledge when the actor has no direct or reported informational basis and is clearly hallucinating.
- Never substitute stale knowledge for current engine truth.
- Never turn speech into an engine fact.
- Never expose another character’s knowledge records.
- Preserve the existing identity, team and target protections.

## Narration channels

Distinguish three outputs:

```text
Public narration
Private direct observation
Private DM answer
```

Every generated or templated output must record:

```text
Visibility
Recipients
Source event
Related fact IDs
World version
```

Public narration must contain only public facts.

Private observation may contain facts discovered by the relevant character.

Do not generate one omniscient narration and then deliver it to everyone.

## Character prompts

Update character prompts to explain naturally:

- You do not automatically see everything another person sees.
- If somebody opens a container, you may know it is open without knowing its contents.
- You can closely inspect an object by attempting to examine it.
- You can tell others what you discover using `say`.
- Things other people tell you may be useful, but they are still things they said.
- An earlier observation may become outdated when somebody changes the world.

Do not explain knowledge databases, visibility flags, world versions or engine mechanics.

Do not force characters to speak or inspect.

Use a gentle communication cue:

> If another character’s later choice would benefit from knowing something you discovered, consider telling them before you act again.

## Tracing

Add explicit trace events or equivalent structured data for:

```text
KnowledgeFactCreated
KnowledgeFactLearned
PrivateObservationDelivered
PublicFactDelivered
ObjectInspected
```

Record at least:

```text
FactId
SubjectId
FactType
Value or Description
Source
CharacterId
Round
Turn
WorldVersion
Visibility
Recipients
Related action or event
```

For every narration or answer, preserve the information view supplied to the DM.

It must be possible to reconstruct:

- Objective truth at that moment.
- What each character directly knew.
- What each character had merely heard.
- Which facts were public.
- Why a particular answer was or was not permitted to reveal something.

## Report

Extend the generated report with:

### Objective state

Show the authoritative initial and final contents of both containers.

### Knowledge timeline

Show when facts were discovered and by whom.

Example:

```text
Round 2 — Elara inspected the Faded Shrine Medicine Case.
Private discovery: medicinal-supplies marking.
Delivered to: Elara only.
```

### Per-character knowledge

Include a final table for each character:

| Fact | Source | Learned | Observed world version |
| --- | --- | --- | --- |

Keep heard speech separate from directly verified facts.

### Transcript visibility

Clearly label:

- Public narration.
- Public speech.
- Private inspection results.
- Private DM questions and answers.
- Publicly observed item transfers.

The readable transcript must make information boundaries obvious without requiring the raw JSON trace.

## Context protection

Preserve the existing bounded context and silent-truncation detection.

Knowledge summaries must be projected deliberately into the relevant character’s context. Do not solve knowledge growth by sending the complete lifetime knowledge/event history on every call.

The application trace remains complete even when older model context is compacted.

## RNG regression requirement

Inspection, knowledge delivery and container observation use no RNG in v0.4.

Preserve existing combat RNG unchanged.

**Ensure every RNG draw records its seed/state, purpose, candidates, raw roll, modifiers, and result. Otherwise the new hit chance will make behavioural comparisons much harder to reproduce.**

Seeded replay must continue to produce identical engine outcomes when character decisions remain equivalent.

## Tests

Add or update tests covering at least:

1. Both containers seed with the correct authoritative contents.
2. Closed-container contents are not public.
3. General room narration does not reveal hidden contents.
4. Opening a container publicly reveals only that it is open.
5. Opening privately reveals current contents to the opener.
6. Another character does not receive the opener’s private observation.
7. Inspecting a closed container reveals configured exterior clues privately.
8. Inspecting an open container reveals current contents privately.
9. Inspection consumes the turn and uses no RNG.
10. Repeat inspection does not create unnecessary duplicate knowledge.
11. Knowledge records include their source and observed world version.
12. A historical observation does not automatically update after state changes.
13. Speech containing a fact remains hearsay rather than direct knowledge.
14. Heard speech is delivered to the correct living recipients.
15. A recipient can act based on hearsay.
16. The DM describes hearsay as something another character said.
17. The DM does not confirm hearsay using omniscient state.
18. A character asking about another character’s private observation receives no leak.
19. Taking an item visibly becomes a public event.
20. Public item removal updates appropriate recipient knowledge.
21. A stale take attempt is rejected without mutation.
22. Rejection text does not reveal hidden information or engine terminology.
23. Vark begins with private backstory knowledge of both containers.
24. Skrit does not automatically inherit Vark’s knowledge.
25. Public and private narration recipients are correctly traced.
26. The report reconstructs objective truth and per-character knowledge separately.
27. Existing speech, object, combat, team, inventory, death-looting, RNG and context tests continue to pass.

Include at least one deterministic scripted test proving this sequence:

1. Elara inspects the shrine-marked case.
2. Only Elara learns its exterior clue.
3. Rowan asks about it and remains unaware.
4. Elara tells Rowan what she discovered.
5. Rowan remembers Elara’s claim but has not directly verified it.
6. Rowan opens the case.
7. Only Rowan directly learns that it contains the potion.
8. Rowan removes the potion.
9. The visible removal becomes public knowledge.

## Explicitly out of scope

Do not add:

- Inspection RNG.
- Perception statistics.
- Confidence scores.
- General logical inference.
- Automatic truth extraction from arbitrary speech.
- Private whispers.
- Deception or persuasion mechanics.
- Languages or hearing distance.
- Locks, keys or traps.
- Hidden compartments.
- Attacking or breaking objects.
- Giving items directly to another character.
- Spells or abilities.
- Status effects.
- Fleeing or surrender.
- Additional rooms.
- Campaign persistence.
- Human-controlled characters.
- New UI or API surfaces.
- Model-comparison experiments.

## Versioning

Update application and report metadata to v0.4.

Preserve prompt hashes and add hashes for new or changed prompts.

## Completion criteria

v0.4 is complete when:

- Container contents no longer become globally known when somebody opens a container.
- The opener receives a private, authoritative observation.
- Other characters must inspect, observe a later public event, or rely on communication.
- Direct knowledge remains distinct from hearsay.
- Knowledge can become stale without overriding current world state.
- DM answers respect the asking character’s information boundary.
- Inspection, opening, taking and speech compose correctly.
- Trace and report output reconstruct who knew what, when, and why.
- Existing v0.3 behaviour continues to work.
- All existing and new tests pass.

The autonomous demonstration run should encourage—but not force—characters to inspect, communicate and act on information.

If the models do not naturally exercise every path, cover the complete knowledge flow with deterministic tests and report the natural run honestly.

Stop when this information-boundary vertical slice is working. Do not expand into further mechanics.
