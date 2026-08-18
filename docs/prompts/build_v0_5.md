# Models & Monsters v0.5 — Yield or Escape

Extend the existing Models & Monsters v0.4 implementation with non-lethal encounter outcomes.

The purpose of v0.5 is to answer:

> Can autonomous characters use communication, personality, fear and self-preservation to end an encounter through surrender or escape rather than fighting until every opponent is dead?

Implement one tightly scoped vertical slice containing:

1. A single usable exit.
2. Individual surrender.
3. Individual escape.
4. Team outcomes based on active combatants.
5. Persuasion and intimidation through existing public speech.

Preserve the existing combat, teams, communication, object interaction, hidden information, character knowledge, seeded RNG, context protection, tracing, reports and web UI.

Do not redesign working v0.4 systems.

## Core design principle

Persuasion and intimidation are not new engine mechanics.

Characters already have `say(message)`. They may use speech to threaten, bargain with, reassure or plead with another character.

The recipient independently decides what to do on its own turn.

The Dungeon Master does not roll to determine whether speech changes somebody’s mind, and speech cannot directly mutate character disposition.

Models decide whether to:

- Continue fighting.
- Surrender.
- Open the exit.
- Escape.
- Ignore the speaker.
- Respond with further speech.

The engine authoritatively applies whichever physical or disposition-changing action the character actually chooses.

## Existing character protocol

Do not add new character-facing tools.

Characters continue using:

```text
ask_dm(question)
say(message)
take_action(intent)
end_turn(reason)
```

Characters express surrender, opening the exit and escaping through natural-language `take_action` calls:

```text
take_action("I lower my sabre and yield. I will fight no more.")
```

```text
take_action("I wrench open the cellar door.")
```

```text
take_action("I run through the open stair door and escape the cellar.")
```

The Dungeon Master translates these into engine actions.

## Character disposition

Replace the assumption that every living character remains an active combatant with an explicit disposition:

```text
Active
Surrendered
Escaped
Dead
```

Use an enum or equivalent proper domain type.

### Active

An active character:

- Is alive.
- Is present in the encounter.
- Receives turns.
- May ask questions, speak, act or end its turn.
- Is a valid combat target.

### Surrendered

A surrendered character:

- Is alive.
- Remains physically present.
- Has stopped fighting.
- Receives no further turns.
- Is skipped without a model call.
- Is not a valid combat target in v0.5.
- Retains its inventory and weapon.
- Does not automatically cause teammates to surrender.

Treat surrendered characters as removed from combat. This is a v0.5 harness simplification; do not introduce prisoner violence, executions or resumed combat.

Do not narrate a surrendered character dropping its weapon unless the engine models an actual weapon transfer. Prefer:

> Vark lowers his sabre and yields, making no further attempt to fight.

### Escaped

An escaped character:

- Is alive.
- Is no longer present in the encounter.
- Receives no further turns.
- Is skipped without a model call.
- Cannot be targeted.
- Retains its inventory and equipment.
- Records which exit it used.
- Does not automatically cause teammates to escape.

### Dead

A dead character:

- Is not alive.
- Is no longer active.
- Receives no further turns.
- Cannot be targeted.
- Continues to use the existing death, injury and inventory-on-death behaviour.

### Derived state

Avoid contradictory booleans.

Conceptually:

```text
IsAlive =
    Disposition != Dead

IsPresent =
    Disposition is Active or Surrendered

CanAct =
    Disposition == Active

IsCombatTarget =
    Disposition == Active
```

If existing compatibility properties remain, derive them from disposition rather than storing conflicting state independently.

## The cellar exit

Add one exit to the existing Flooded Cellar.

```text
Id:
cellar-stair-door

Name:
Cellar Stair Door

Description:
A heavy wooden door at the foot of the stairs leading out of the flooded cellar.

Initial state:
Closed

Locked:
No

Destination:
Outside the encounter
```

The exit is publicly visible from the opening narration.

The project still has no persistent position or distance model. Under the existing one-room abstraction, every active character can attempt to interact with the exit.

Passing through the exit removes a character from the encounter. Do not create or simulate a second room.

Use a small domain representation broadly equivalent to:

```text
EncounterExit
- Id
- Name
- Description
- IsOpen
- DestinationDescription
```

Do not force exits into the container hierarchy if that creates an unnatural abstraction.

## New DM-to-engine actions

Add exactly three supported engine actions:

```text
open_exit(actor, exit)
escape_encounter(actor, exit)
surrender(actor)
```

Characters never see these functions.

## `open_exit`

Validate:

- The actor exists.
- The actor is `Active`.
- The exit exists in the current room.
- The exit reference is unambiguous.
- The exit is currently closed.

On acceptance:

- Set the exit to open.
- Increment world-state version.
- Consume the actor’s turn.
- Record the complete state transition.
- Make the opening a public event.
- Deliver the public narration to every present living character.

Example:

> Skrit pulls open the heavy cellar door, exposing the dark stairs beyond.

Reject without mutation if:

- The actor is not active.
- The exit does not exist.
- The exit is already open.
- The reference is ambiguous.

Use an in-world rejection:

> The cellar door already stands open.

Do not use RNG.

## `escape_encounter`

Validate:

- The actor exists.
- The actor is `Active`.
- The exit exists.
- The exit is open.
- The actor is still present in the encounter.

On acceptance:

- Set the actor’s disposition to `Escaped`.
- Record the exit ID and destination description.
- Increment world-state version.
- Consume the actor’s turn.
- Remove the actor from future turn processing and targeting.
- Make the escape a public event.
- Re-evaluate the team outcome immediately.

Example:

> Skrit bolts through the open cellar door and disappears up the stairs, abandoning the fight.

Reject without mutation if the exit is closed:

> The cellar door is still shut; you cannot escape through it.

Do not add:

- Escape rolls.
- Opportunity attacks.
- Pursuit.
- Movement speed.
- Blocking the exit.
- Closing or locking the door.
- Re-entering the encounter.

## `surrender`

Validate:

- The actor exists.
- The actor is `Active`.

On acceptance:

- Set the actor’s disposition to `Surrendered`.
- Increment world-state version.
- Consume the actor’s turn.
- Remove the actor from future active turns.
- Make the surrender a public event.
- Re-evaluate the team outcome immediately.

Example:

> Vark lowers his sabre and yields, making no further attempt to fight.

Surrender is unilateral. It does not require:

- Opponent approval.
- A persuasion roll.
- An intimidation check.
- A prior demand.
- Negotiated terms.
- Surrender by the rest of the team.

A character may speak before surrendering:

```text
say("Spare me and I will tell you what I know.")
take_action("I lower my weapon and surrender.")
```

The spoken promise is not mechanically enforced in v0.5.

Do not treat `end_turn` as surrender. A character using `end_turn` remains `Active` and receives another turn next round.

## Team outcome

Stop using “all opposing characters are dead” as the only terminal condition.

After every action that can change health or disposition, calculate each team’s active membership.

A team remains an active contender while it has at least one character with:

```text
Disposition == Active
```

The encounter ends when no more than one team has active characters remaining.

### One active team remains

That team wins control of the encounter.

Examples:

```text
Heroes win: Goblins has no active combatants remaining.
```

```text
Goblins win: Heroes has no active combatants remaining.
```

The resolution summary must explain what happened to each opposing character:

```text
Vark was killed.
Skrit escaped through the Cellar Stair Door.
```

or:

```text
Vark surrendered.
Skrit surrendered.
```

### No active teams remain

Handle this explicitly as:

```text
Draw: no active combatants remain.
```

This should be rare, but the engine must represent it correctly.

### Outcome categories

Record an outcome classification such as:

```text
Elimination
Surrender
Withdrawal
Mixed
Draw
HarnessLimit
```

Use:

- `Elimination` when every defeated-team member is dead.
- `Surrender` when surrender accounts for the defeated team’s removal from active combat.
- `Withdrawal` when escape accounts for it.
- `Mixed` when dead, surrendered and escaped outcomes are combined.
- `Draw` when no active team remains.
- `HarnessLimit` for existing round or idle termination.

Do not force a single misleading label when individual outcomes differ. Preserve the full per-character breakdown.

## Turn sequencing

Only `Active` characters receive turns.

Before each scheduled turn:

- Read the character’s current disposition.
- Skip `Surrendered`, `Escaped` and `Dead` characters.
- Do not make a model call for skipped characters.
- Trace the skip and disposition.
- Keep the existing fixed turn order for remaining active characters.

After every accepted attack, surrender or escape:

1. Persist updated state.
2. Evaluate team outcome.
3. Stop immediately if terminal.
4. Do not allow another character to act after terminal resolution.

## Character prompts

Update character prompts so models understand these possibilities naturally.

Every character should know:

- There is a visible closed cellar door leading out.
- The door must be opened before anybody can leave through it.
- Escaping removes them from this fight and preserves their life.
- Surrendering means yielding and taking no further part in the fight.
- Surrender and escape are legitimate choices.
- `end_turn` is only temporary inaction.
- Survival does not require fighting until death.
- Other characters cannot be forced to surrender through speech.
- Threats and promises may influence another character’s later decision, but the choice remains theirs.

Keep this in-world. Do not mention disposition enums, terminal conditions or engine tools.

Include a prompt principle such as:

> You value your stated goals and survival according to your personality. If the fight becomes hopeless, you may surrender or seek escape rather than continuing until death. You are not required to be suicidal.

Do not impose deterministic health thresholds.

## Character motivations

Preserve the existing characters while adding small motivations that make non-combat outcomes plausible.

### Rowan

- Prefers accepting a genuine surrender to unnecessary killing.
- May demand surrender from a badly wounded or isolated opponent.
- Will not abandon Elara merely to escape alone unless survival has become desperate.

### Elara

- Values mercy.
- May offer a wounded opponent the chance to yield.
- May urge Rowan to escape if both heroes are close to defeat.
- Does not want to die merely to clear the cellar.

### Vark

- Uses threats and intimidation to protect his possessions and authority.
- Strongly resists surrender while Skrit remains loyal and active.
- May surrender or escape if isolated, critically wounded or clearly defeated.
- Values survival more than honour.

### Skrit

- Is highly sensitive to Vark’s survival and behaviour.
- May surrender or flee if Vark dies, surrenders or escapes.
- May obey Vark’s shouted instruction to open the door or run.
- May abandon Vark if frightened enough.

These are behavioural motivations, not scripted rules.

## Persuasion and intimidation

Do not add:

```text
persuade
intimidate
roll_charisma
force_surrender
```

Persuasion and intimidation happen through ordinary `say(message)` calls.

Examples:

```text
"Skrit, Vark is dead. Yield now and you will live."
```

```text
"Elara can barely stand. Drop your mace or the next strike ends you."
```

```text
"Open the door, Skrit. We leave now."
```

A speech event:

- Does not change disposition.
- Does not open the exit.
- Does not end the encounter.
- Is delivered to all present living characters under the existing public-speech rules.
- May affect later model decisions through conversation history.

Continue distinguishing speech from authoritative facts. A promise of mercy is something a character said, not an engine-enforced contract.

## Dungeon Master adjudication

Extend the supported DM action surface to:

```text
attack_character
use_item
inspect_object
open_container
take_item
open_exit
escape_encounter
surrender
reject_action
```

The DM must:

- Translate a clear yielding intent to `surrender`.
- Translate opening the cellar door to `open_exit`.
- Translate passing through the open exit to `escape_encounter`.
- Reject attempted escape through a closed exit without mutation.
- Never turn a threat or demand into another character’s surrender.
- Never decide that persuasion succeeded.
- Never surrender an entire team from one actor’s intent.
- Never narrate escape until the engine accepts it.
- Never narrate a dropped weapon unless engine state records it.
- Use current disposition and authoritative exit state.
- Continue respecting actor identity, teams, knowledge and information boundaries.
- Continue using in-world rejection language.

### Intent distinctions

Examples:

```text
"I tell Skrit to surrender."
```

This is speech, not surrender by the acting character.

```text
"I surrender."
```

This becomes `surrender(actor)`.

```text
"I run for the closed door."
```

This cannot escape. Depending on the clear core intent, translate it to opening the door or reject it with an in-world explanation. Do not open and escape in one action.

```text
"I open the door and flee."
```

This is a compound action and must not perform both operations. Explain that the door must first be opened, allowing the character to retry.

```text
"I run through the open door."
```

This becomes `escape_encounter(actor, exit)`.

## Information and knowledge

Exit state and disposition changes are public facts.

When the door opens, every present living character directly learns that it is open.

When somebody surrenders or escapes, every present living character directly learns that outcome.

Use the existing knowledge system and record appropriate `PublicEvent` knowledge facts.

Do not leak:

- A character’s private intention to flee before they act or speak.
- Internal model reasoning.
- Private DM questions.
- A promise or threat as though it were guaranteed truth.

Escaped characters stop receiving new speech, narration and public knowledge from the room.

Surrendered characters remain present and may continue receiving public narration and speech until the encounter ends, but they receive no turns.

## Speech protocol resilience

Preserve and strengthen the existing recovery for characters who attempt to speak in plain text instead of calling `say`.

Record the initial failure as:

```text
UnstructuredSpeechAttempt
```

Then issue a focused retry:

> You attempted to speak aloud. Call `say(message)` now with exactly the words you want everyone to hear. Do not act or narrate anything else yet.

After a successful recovered `say`, return control to the same character so it may still act or end its turn.

Do not silently convert arbitrary prose into public speech.

Retain the original response in the trace.

## Tracing

Add explicit events or equivalent structured data for:

```text
DispositionChanged
ExitInteraction
CharacterEscaped
CharacterSurrendered
TeamOutcomeEvaluated
TurnSkipped
PublicFactDelivered
UnstructuredSpeechAttempt
```

For every disposition change, record:

```text
CharacterId
CharacterName
Team
PreviousDisposition
NewDisposition
Cause
Round
Turn
WorldVersionBefore
WorldVersionAfter
ExitId if applicable
PublicRecipients
```

For every exit interaction, record:

```text
ActorId
ExitId
ActionType
StateBefore
StateAfter
ValidationResult
RejectionReason
WorldVersionBefore
WorldVersionAfter
```

For every team-outcome evaluation, record:

```text
Each team
Active members
Surrendered members
Escaped members
Dead members
IsTerminal
Winning team
Outcome classification
Resolution summary
```

The trace must make it possible to reconstruct exactly why the encounter ended.

## Run report

Extend the report with:

### Exit state

Show:

```text
Cellar Stair Door
Initial state
Final state
Characters who escaped through it
```

### Character outcome

Add disposition to all character tables:

| Character | Team | Health | Disposition | Final location |
|---|---|---:|---|---|

Examples:

```text
Skrit — Escaped — Outside the encounter
Vark — Surrendered — Flooded Cellar
```

### Outcome summary

Distinguish:

- Winner.
- Outcome classification.
- Dead characters.
- Surrendered characters.
- Escaped characters.
- Remaining active characters.

### Transcript

Clearly display:

```text
Skrit opens the Cellar Stair Door.
Skrit escapes through the open door.
Vark surrenders.
Skrit’s turn is skipped because they escaped.
```

Do not describe surrendered or escaped characters as dead or defeated by injury.

Include counts per character for:

```text
Surrender attempts
Accepted surrenders
Escape attempts
Accepted escapes
Exit openings
Persuasive or threatening speeches only if deterministically identifiable
```

Do not use another LLM call merely to classify speech. If speech cannot be classified deterministically, report it as ordinary public speech.

## Web UI

Extend the existing observer UI without redesigning it.

### Exit display

Add a compact room-state element showing:

```text
Cellar Stair Door — Closed
```

or:

```text
Cellar Stair Door — Open
```

Update it live.

### Character cards

Display disposition clearly:

- `Active`: normal card.
- `Surrendered`: subdued but still present, labelled “surrendered.”
- `Escaped`: subdued, labelled “escaped.”
- `Dead`: existing fallen treatment.

Do not label surrendered or escaped characters as fallen.

### Transcript

Use distinct readable event text for:

- Door opening.
- Escape.
- Surrender.
- Team resolution.

### Terminal banner

Show both winner and outcome:

```text
Heroes win — Goblins surrendered
```

```text
Heroes win — Goblins withdrew
```

```text
Heroes win — Vark was killed; Skrit escaped
```

Preserve start, cancel, round status, auto-updating character state and report generation.

## RNG regression requirement

Opening an exit, escaping and surrendering use no RNG in v0.5.

Preserve existing combat RNG unchanged.

**Ensure every RNG draw records its seed/state, purpose, candidates, raw roll, modifiers, and result. Otherwise the new hit chance will make behavioural comparisons much harder to reproduce.**

Seeded replay must continue to produce identical engine outcomes when character decisions remain equivalent.

## Tests

Add or update tests covering at least:

1. The scenario seeds one closed, unlocked exit.
2. Opening the exit changes its state and consumes the turn.
3. Opening an already open exit is rejected without mutation.
4. Opening the exit uses no RNG.
5. Escaping through a closed exit is rejected.
6. Escaping through an open exit sets disposition to `Escaped`.
7. An escaped character retains health, inventory and equipment.
8. An escaped character is no longer present or targetable.
9. An escaped character receives no further turns or model calls.
10. Surrender changes an active character to `Surrendered`.
11. A surrendered character remains alive and present.
12. A surrendered character is no longer active or targetable.
13. A surrendered character receives no further turns or model calls.
14. Surrender does not affect teammates automatically.
15. `end_turn` leaves disposition as `Active`.
16. An individual surrender does not end the encounter while their teammate remains active.
17. An individual escape does not end the encounter while their teammate remains active.
18. The encounter ends when one team has no active characters.
19. A fully dead defeated team produces `Elimination`.
20. A fully surrendered defeated team produces `Surrender`.
21. A fully escaped defeated team produces `Withdrawal`.
22. A dead/escaped/surrendered combination produces `Mixed`.
23. No active teams produces `Draw`.
24. Terminal resolution stops further turns immediately.
25. Speech alone cannot change disposition.
26. A surrender demand is delivered as speech without forcing its recipient.
27. A recipient may independently surrender on its later turn.
28. A compound open-and-escape intent cannot perform both actions.
29. Exit and disposition changes become public knowledge.
30. Escaped characters stop receiving public events.
31. Surrendered characters continue receiving public events while the encounter remains active.
32. Unstructured attempted speech is traced and receives a focused `say` retry.
33. Successful recovered speech does not consume the character’s action.
34. Final state and report distinguish active, surrendered, escaped and dead characters.
35. The web UI renders exit state and every disposition correctly.
36. Existing combat, object, knowledge, speech, inventory, death-looting, RNG, context and report tests continue to pass.

Include deterministic scripted tests for at least these flows:

### Surrender flow

1. Rowan tells Skrit to surrender.
2. Speech alone does not change Skrit.
3. On Skrit’s turn, Skrit chooses to surrender.
4. Skrit becomes `Surrendered`.
5. Skrit’s later turns are skipped.
6. Skrit remains alive in final state.

### Escape flow

1. Vark opens the Cellar Stair Door.
2. The open state becomes public.
3. Skrit escapes through it on a later turn.
4. Skrit becomes `Escaped`.
5. Skrit cannot be targeted.
6. Skrit survives outside the encounter.

### Mixed resolution

1. Vark dies.
2. Skrit escapes.
3. Heroes win.
4. Outcome classification is `Mixed`.
5. Final report lists Vark as dead and Skrit as escaped.

## Explicitly out of scope

Do not add:

- Persuasion or intimidation statistics.
- Charisma or morale rolls.
- Social hit points.
- Forced surrender.
- Group surrender from one actor’s action.
- Negotiated-contract enforcement.
- Prisoners or imprisonment mechanics.
- Executing surrendered characters.
- Resuming combat after surrender.
- Weapon confiscation or disarming on surrender.
- Escape opportunity attacks.
- Pursuit or chasing.
- Exit blocking.
- Closing, locking or barricading the door.
- A second room.
- Persistent travel.
- Campaign consequences.
- Faction reputation.
- Human-controlled characters.
- New model-comparison features.
- New external orchestration frameworks.

## Versioning

Update application, web UI and report metadata to v0.5.

Preserve existing prompt hashes and add hashes for all new or changed prompts.

## Completion criteria

v0.5 is complete when:

- Characters can surrender and remain alive.
- Characters can open the exit and escape.
- Surrendered and escaped characters leave active turn processing.
- Teams can win without killing every opponent.
- Persuasion and intimidation operate through speech rather than mind-control mechanics.
- Models understand that survival through surrender or escape is valid.
- Team outcomes correctly distinguish elimination, surrender, withdrawal and mixed resolution.
- Trace and report output reconstruct exactly how every character left combat.
- The observer UI displays exit state and character disposition live.
- Existing v0.4 behaviour continues to work.
- All existing and new tests pass.

The autonomous demonstration run should encourage—but not force—at least one surrender demand, surrender attempt, exit opening or escape.

If the models choose to fight to the death in a natural run, cover all non-combat paths through deterministic tests and report the natural behaviour honestly.

Stop when this non-combat outcome vertical slice is working. Do not expand into multiple rooms, campaigns or a general social-combat system.
