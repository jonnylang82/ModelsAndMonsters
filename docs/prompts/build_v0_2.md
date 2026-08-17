# Models & Monsters — Add a 2v2 Multi-Actor Encounter

Extend the existing Models & Monsters implementation from a one-versus-one encounter to a small multi-actor encounter:

> **Fighter and Cleric versus Goblin Captain and Goblin Grunt**

This pass exists to validate multi-actor orchestration, identity handling, targeting, isolated conversation histories, public information delivery, team terminal conditions, and reproducible RNG.

Preserve the existing architecture, explicit orchestration, tracing, provider abstraction, context-window protections, prompts, deterministic engine authority, and report generation.

Do not redesign the application or add unrelated game systems.

## Scope

Add exactly four character agents:

### Heroes

1. Fighter
2. Cleric

### Monsters

3. Goblin Captain
4. Goblin Grunt

Continue to use one Dungeon Master agent.

Each character must be a separate agent instance with:

- Its own character definition.
- Its own model profile.
- Its own conversation history.
- Its own exact self-state injection.
- Its own questions, actions, rejections, results, and public perceptions.

Do not implement shared hero or monster conversation histories.

## Character interaction protocol

Preserve the existing three character tools:

```text
ask_dm(question)
take_action(intent)
end_turn(reason)
```

`end_turn` is an intentional null action. It allows a character to voluntarily complete its turn without changing engine state—for example, when surrendering to fate, hesitating, waiting, or deciding it has no useful action.

An ended turn must:

- Complete the character’s turn.
- Produce no engine-state mutation.
- Record the character’s reason.
- Be visible in the trace and report.
- Never cause the DM to invent movement, reactions, attacks, dropped items, or other new events.

Harness limits must remain as protection against malformed or looping behaviour.

## Scenario

Create a new hard-coded or configuration-seeded scenario suitable for a 2v2 encounter.

Suggested starting definitions follow. Adjust numerical values only if required to fit existing engine types or the current hit/glancing-blow balance.

### Fighter

```text
Name: Rowan
Team: Heroes

Health: 14
Armour: 2

Weapon:
Longsword
Damage: 5

Personality:
Steady, brave and protective. Prefers to engage the most dangerous opponent and shield less martial companions from harm.

Wants:
To defeat the goblins without allowing the cleric to be overwhelmed.

Fears:
Failing to protect a companion.

Goal:
Survive and defeat or otherwise overcome the goblins.
```

### Cleric

```text
Name: Elara
Team: Heroes

Health: 11
Armour: 2

Weapon:
Iron Mace
Damage: 3

Inventory:
Small Healing Potion

Personality:
Compassionate, cautious and resolute. Avoids unnecessary risk but will fight when somebody depends on her.

Wants:
To keep herself and Rowan alive.

Fears:
Being isolated and surrounded.

Goal:
Survive and help defeat or otherwise overcome the goblins.
```

The Cleric is a character identity in this pass, not permission to introduce spells, magical healing, abilities, or a class system.

Use only mechanics already supported by the engine. If item use currently targets only the user, do not add healing of other characters in this pass.

### Goblin Captain

```text
Name: Vark
Team: Goblins

Health: 12
Armour: 2

Weapon:
Notched Sabre
Damage: 4

Personality:
Cunning, domineering and self-preserving. Expects the grunt to take risks while it attacks vulnerable opponents.

Wants:
To kill or drive off the intruders while preserving its own life.

Fears:
Losing authority or being left to fight alone.

Goal:
Survive and defeat or otherwise overcome the heroes.
```

### Goblin Grunt

```text
Name: Skrit
Team: Goblins

Health: 8
Armour: 1

Weapon:
Crude Spear
Damage: 3

Personality:
Reckless, nervous and eager to impress the captain. Acts boldly while the captain appears in control but becomes frightened when isolated.

Wants:
The captain’s approval and a share of anything taken from the intruders.

Fears:
Being abandoned by the captain.

Goal:
Survive and help defeat or otherwise overcome the heroes.
```

## Turn order

Use a fixed initial turn order:

```text
Rowan
Elara
Vark
Skrit
```

Repeat this order each round.

Before starting a turn:

- Confirm that the actor is alive.
- Skip dead actors without making a model call.
- Trace the skip and its reason.
- Do not renumber or corrupt the remaining order.

Check terminal conditions after every accepted engine action.

The encounter ends immediately when either team has no living characters:

```text
Heroes win:
All goblins are dead.

Goblins win:
All heroes are dead.
```

A character voluntarily ending its turn does not itself satisfy a terminal condition.

Retain the configured maximum-round and idle-round protections.

## Targeting

Update the system so an attack can unambiguously target any other living character.

The Dungeon Master must translate natural-language references into a specific character ID.

Requirements:

- The attacker must be the character whose turn is being adjudicated.
- The target must exist.
- The target must be alive.
- The target must not silently change during interpretation.
- Ambiguous references must be rejected or clarified rather than guessed dangerously.
- Never silently substitute a different target merely because the requested target is invalid.
- Engine state and trace events must use stable character IDs, not only display names.

Do not silently reinterpret one character as another because they share a role or team.

Do not add a special prohibition on friendly fire unless the existing engine already has one. The engine should resolve the action that the DM actually translated, while the prompts and character goals should normally discourage attacking allies.

## Conversation and information delivery

Maintain strict character-history isolation.

Private information includes:

- A character’s `ask_dm` question.
- The DM’s direct answer.
- An action rejection.
- An engine rejection.
- Retry or correction instructions.

Private information must be delivered only to the relevant character.

Public information includes:

- Opening room narration.
- Observable accepted action outcomes.
- Observable use of an item.
- Death.
- A public end-turn narration, if one is generated.
- Round-end and encounter-end narration.

Public narration should be deliberately delivered to every living character that could perceive it. Record the recipients in the trace.

Do not copy one character’s private DM conversation into another character’s history.

Preserve the existing bounded-context protections. The full history may remain in the application trace, but only the intended bounded projection should be sent to each model.

## Dungeon Master behaviour

Continue to treat the engine as authoritative.

The DM must:

- Identify the current actor correctly.
- Identify the intended target correctly.
- Use the actor’s actual weapon.
- Avoid confusing equipment between characters.
- Avoid inventing state changes.
- Avoid leaking exact opponent health values.
- Describe several actors concisely and clearly.
- Follow the existing no-position/no-distance rules.
- Translate supported attacks and item uses.
- Reject unsupported intentions without mutating the fiction.

When an attack includes an unsupported hoped-for effect—such as disarming, stunning or frightening—the DM may translate the supported physical strike, but the engine alone determines its actual result. The narration must not claim the extra effect occurred.

## RNG and reproducibility

Continue using the existing `IRng` abstraction and current hit, miss, and glancing-blow rules. Do not replace or substantially rebalance those rules in this pass.

Use a configured seed so the demonstration run can be reproduced.

**Ensure every RNG draw records its seed/state, purpose, candidates, raw roll, modifiers, and result. Otherwise the new hit chance will make behavioural comparisons much harder to reproduce.**

An RNG trace event must make it possible to answer:

- Which action caused the draw?
- Which actor and target were involved?
- What outcome was being selected?
- What candidates or ranges were available?
- What raw value was drawn?
- Which modifiers were applied?
- What final result was selected?
- What RNG state or sequence position preceded and followed the draw?

Do not record only the final hit/miss result.

Two runs using the same scenario, character decisions, configuration, and RNG seed must produce the same engine outcomes and final state.

## Model profiles

Every character must be independently configurable:

```text
Dungeon Master
Rowan
Elara
Vark
Skrit
```

Do not hard-code one shared hero profile or one shared monster profile into orchestration logic.

Profiles may inherit common defaults, but the resolved profile used for every model call must be recorded.

For the initial demonstration, avoid deliberately varying models and parameters merely to create diversity. Establish a clean multi-actor baseline first.

## Tracing and report

Extend the existing trace and generated report to make the 2v2 interaction reconstructable.

Include:

- Team membership.
- Full initial and final state for all four characters.
- Fixed turn order.
- Dead-character turn skips.
- Public narration recipients.
- Private-answer recipients.
- Attacker and target IDs.
- Target-resolution decisions.
- Accepted and rejected actions.
- Voluntary `end_turn` events and reasons.
- Team terminal-condition evaluation.
- Complete RNG draw details.
- Per-agent model profile and usage.
- Per-agent question, action, rejection, pass, token, and latency totals.

The readable transcript must make it obvious who acted, who was targeted, and who received public versus private information.

## Tests

Add or update tests covering at least:

1. The scenario seeds exactly four characters with unique IDs and correct teams.
2. Fixed turn order cycles correctly.
3. Dead actors are skipped without model calls.
4. The encounter continues while each team has at least one living character.
5. The encounter ends immediately when one team has no living characters.
6. Attacks resolve against the intended target.
7. Invalid or dead targets are rejected without silent substitution.
8. Private questions and answers do not leak into another character’s history.
9. Public narration is delivered to all appropriate living characters.
10. `end_turn` completes a turn without changing engine state.
11. Every RNG draw produces a complete trace record.
12. Replaying the same accepted actions with the same RNG seed produces identical outcomes and final state.
13. The generated report includes all four agents, team results, target information, and RNG details.

Preserve all existing tests.

## Explicitly out of scope

Do not add:

- Spells or abilities.
- Status effects.
- Healing another character.
- Character-to-character communication tools.
- Persuasion or intimidation mechanics.
- Fleeing or surrender mechanics.
- Containers, chests or doors.
- Generic environmental-object interaction.
- Additional rooms.
- Campaign persistence.
- Human-controlled characters.
- New UI or API surfaces.
- Model-comparison experiments.
- A new orchestration or agent framework.

Characters may naturally express ideas involving these concepts. Unsupported actions should follow the existing rejection and retry process.

## Completion criteria

This pass is complete when:

- The new 2v2 scenario runs to a valid team terminal condition or configured harness limit.
- All four characters remain distinct throughout prompts, targeting, state and tracing.
- Conversation privacy and public narration delivery behave correctly.
- Dead actors are skipped correctly.
- RNG behaviour is reproducible and completely traced.
- Existing and new tests pass.
- A generated report clearly reconstructs the complete encounter.

Stop at that point. Do not expand the game beyond this multi-actor orchestration milestone.
