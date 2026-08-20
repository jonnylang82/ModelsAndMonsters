# Models & Monsters

> **Codename:** Models & Monsters
> **Status:** Tech demo / proof of concept
> **Initial scope:** v0.1

## Concept

**Models & Monsters** is an experiment in placing autonomous AI agents inside a small, persistent, rules-bound fantasy world.

Rather than giving agents direct access to game mechanics, each character experiences the world through a Dungeon Master agent.

Characters decide what they want to do in natural language. The Dungeon Master interprets those intentions and translates supported actions into calls against a conventional deterministic game engine.

The game engine remains the authoritative source of truth.

The initial goal is not to build a complete game or reproduce Dungeons & Dragons rules. It is to create the smallest possible environment in which independently configured AI characters can perceive a situation, make decisions based on their personality and circumstances, interact through another AI agent, and cause persistent changes to a shared world.

---

# Core Principle

Characters should behave like **inhabitants of the world**, not clients of a game API.

A character might say:

> "I raise my shield and swing my sword at the goblin."

It should not say:

> `AttackCharacter(targetId: 17, weaponId: 4)`

The interaction is:

```text
Character
    │
    │ Natural-language intent
    ▼
Dungeon Master
    │
    │ Structured game action
    ▼
Game Engine
```

Only the Dungeon Master interacts with the game engine.

The models decide **intent**.

The Dungeon Master interprets that intent.

The engine decides **reality**.

---

# Responsibilities

## Game Engine

The game engine is conventional software and owns all authoritative state.

It is responsible for:

- Characters
- Health
- Equipment
- Inventory
- Abilities
- Rooms
- Objects
- Combat state
- Turn order
- Persistent world state
- Action validation
- Mechanical outcomes

LLMs cannot directly modify game state.

For v0.1, the game rules should be deliberately tiny and deterministic.

For example:

```text
damage = max(0, weapon_damage - armour_value)

target_health = target_health - damage
```

There is initially:

- No hit chance
- No dice
- No critical hits
- No movement distance
- No line of sight
- No action/bonus/reaction economy
- No full D&D ruleset

If an accepted attack happens, it hits.

Complexity is added only when it becomes useful.

---

## Dungeon Master

The Dungeon Master is the only AI agent with access to game-engine actions.

It has three main responsibilities.

### 1. Narration

The engine supplies the Dungeon Master with authoritative world state.

The Dungeon Master converts that state into a natural description of the room and current situation.

Characters receive the description rather than the underlying game state.

For example, the engine may know:

```text
Goblin:
Health = 2 / 8
Weapon = Rusty Axe
Armour = 1
```

The hero might instead hear:

> "The goblin is badly wounded now. Blood runs down one side of its leather armour and it struggles to keep its axe raised."

The narration therefore becomes part of the character's perception of the world.

### 2. Questions

Characters may ask the Dungeon Master questions before choosing an action.

For example:

> "Does the goblin look badly injured?"

> "Is there anything nearby I could use as a weapon?"

> "Does the door appear locked?"

The Dungeon Master answers using the world information available to it while avoiding revealing information the character could not reasonably know.

Questions do not consume the character's action.

### 3. Action Interpretation

Characters describe what they want to attempt.

For example:

> "I charge the goblin and slash at it with my sword."

The Dungeon Master decides whether it can translate that intention into a supported game-engine action.

If so, it invokes the appropriate tool.

If not, it explains why and allows the character to choose another action.

The Dungeon Master does **not** determine the mechanical outcome of an accepted game action.

The engine does.

---

# Characters

Heroes and monsters use the same fundamental agent model.

A character has:

- Identity
- Backstory
- Personality
- Wants
- Needs
- Fears
- Abilities
- Equipment
- Inventory
- Current health
- Persistent injuries
- Model
- Temperature
- Top P
- Top K
- Other model-specific settings

Different characters may therefore use different models and inference settings.

A monster is not controlled by the Dungeon Master.

It is its own autonomous character agent.

Conceptually:

```text
Hero ─────┐
          │
Monster ──┼──► Dungeon Master ──► Game Engine
          │
Human ────┘
```

A future human-controlled character should use the same interaction pathway as an AI-controlled character.

From the world's perspective, the human is simply another character.

---

# Character Knowledge

Characters should know **what they are capable of**, but should not know the underlying tool surface.

A character may be told:

```text
Weapon:
Iron Sword
Damage: 4

Inventory:
- Healing Potion
- Rope
- Torch

Ability:
Powerful Strike
```

It should not be told:

```text
Available tools:

AttackCharacter
UseItem
TransferItem
InteractWithObject
```

Characters should understand the broad types of things a person in the world might reasonably attempt.

Their prompt may explain that common actions include:

- Attacking
- Using abilities
- Using possessions
- Helping another character
- Interacting with objects in the environment

However, these should be examples rather than a closed action list.

Characters should remain free to be creative.

For example:

> "I throw my shield at the goblin."

> "I try to scare it into surrendering."

> "I wrap my cloak around its head."

> "I kick the brazier towards it."

Some of those actions may not yet be supported.

That is acceptable.

---

# Action Rejection

A character's turn ends only when a game action is successfully accepted and resolved.

If an attempted action cannot be performed, the character may try again.

There are several conceptually different forms of rejection.

## Impossible

The action is not plausible given the character or world.

Example:

> "I fly onto the ceiling."

The character has no ability to fly.

The Dungeon Master may reject this without calling the game engine.

## Unsupported

The action is reasonable in the fictional world but cannot currently be represented by the game's small mechanical surface.

Example:

> "I wrap my cloak around the goblin's eyes."

This may be perfectly plausible, but v0.1 may have no mechanic capable of representing it.

The Dungeon Master explains that the attempted action cannot currently be resolved and allows another attempt.

Unsupported actions are useful data.

Repeated creative requests for the same kinds of interaction may indicate where the game engine should expand next.

## Mechanically Invalid

The Dungeon Master successfully translates the intention into a supported action, but the engine rejects it based on authoritative state.

For example:

```text
Attack(
    actor = Hero,
    target = Goblin,
    weapon = IronSword
)
```

may fail because the hero no longer possesses the sword.

The game engine has the final say.

Useful internal result categories may eventually include:

```text
DM_IMPOSSIBLE
DM_UNSUPPORTED
ENGINE_REJECTED
ENGINE_ACCEPTED
```

---

# Character Turns

A character has two forms of interaction.

## Ask the Dungeon Master

A character may ask questions before acting.

Conceptually:

```text
ask_dm(question)
```

Multiple questions may be allowed during a turn, with a harness-level safety limit if required.

## Attempt an Action

When ready, the character describes one concrete action:

```text
take_action(intent)
```

Only a successfully executed action consumes the character's turn.

If the Dungeon Master or engine rejects the attempt, the character receives an explanation and may choose another action.

---

# Per-Turn Character Context

Each character receives fresh information about itself on every turn.

For example:

```text
You are Aric.

Health:
8 / 10

Injuries:
- Minor cut to left arm

Weapon:
Iron Sword
Damage: 4

Inventory:
- Small Healing Potion
- Rope
- Torch

Abilities:
- Powerful Strike

Current situation:

"The goblin staggers backwards from your previous blow,
clutching its side. Blood darkens its leather armour, but
it still grips its axe and watches you warily.

A brazier burns beside the eastern wall."
```

The character should receive exact information about itself where appropriate.

It should generally receive **descriptive information** about other characters and the environment.

For example, it may know:

> "The goblin appears badly injured."

rather than:

```text
Goblin Health: 2 / 8
```

This makes Dungeon Master narration meaningful rather than cosmetic.

---

# Persistent Injuries

Injuries may initially be descriptive rather than mechanically significant.

For example:

```text
Health:
4 / 10

Injuries:
- Cut to right shoulder
- Bruised ribs
```

These injuries persist in game state and are included in future character context.

A character may therefore change its behaviour because it knows it is injured, even if the engine does not yet apply a numerical injury penalty.

This provides a useful interaction between:

- Structured persistent state
- Natural-language character reasoning

Mechanical effects can be introduced later if useful.

---

# v0.1

The first implementation should prove the interaction architecture rather than game complexity.

## World

One room.

No movement system.

Anyone may interact with anyone or anything in the room unless otherwise prohibited.

## Actors

Three agents:

1. Dungeon Master
2. One Hero
3. One Monster

Hero and Monster are independent character agents.

## Turn Order

Fixed:

```text
Hero
Monster
Hero
Monster
...
```

No initiative roll.

## Initial Mechanics

Potential minimum:

### Character

```text
Name
MaxHealth
Health
Armour
Weapon
Inventory
Injuries
Alive
```

### Weapon

```text
Name
Damage
```

### Attack

```text
damage = max(0, weapon.damage - target.armour)

target.health -= damage
```

### Death

```text
health <= 0
```

### Healing Item

Optional for the first version:

```text
health =
    min(maxHealth, health + healingAmount)

remove item from inventory
```

---

# Minimal Game Actions

The initial state-changing tool surface should be deliberately small.

Potential starting point:

```text
AttackCharacter
UseItem
```

Possible early additions:

```text
UseAbility
TransferItem
InteractWithObject
```

The Dungeon Master may receive authoritative state directly from the harness rather than requiring a large collection of query tools.

The first version should optimise for proving:

```text
Character
    ↓
Natural intent
    ↓
Dungeon Master interpretation
    ↓
Structured tool call
    ↓
Engine validation
    ↓
Persistent state change
    ↓
Dungeon Master narration
    ↓
Character perception
```

---

# v0.1 Simulation Loop

```text
START
  │
  ▼
Game Engine creates initial state
  │
  ▼
Engine gives authoritative state to DM
  │
  ▼
DM describes room and situation
  │
  ▼
Hero receives:
- Own state
- Personality / goals
- DM description
  │
  ▼
Hero may ask DM questions
  │
  ▼
Hero attempts one action
  │
  ▼
DM interprets action
  │
  ├── Impossible / Unsupported
  │       │
  │       └── Hero tries again
  │
  ▼
DM invokes game tool
  │
  ▼
Engine validates and resolves
  │
  ├── Rejected
  │       │
  │       └── Hero tries again
  │
  ▼
State updated
  │
  ▼
DM narrates outcome
  │
  ▼
Monster receives:
- Own state
- Personality / goals
- DM description
  │
  ▼
Monster may ask DM questions
  │
  ▼
Monster attempts one action
  │
  ▼
DM interprets action
  │
  ▼
Engine validates and resolves
  │
  ▼
State updated
  │
  ▼
DM narrates latest situation
  │
  └────────────► NEXT ROUND
```

The loop continues until a terminal condition is reached, initially most likely the death of one character.

---

# What v0.1 Is Testing

The initial experiment is not intended to test sophisticated combat strategy.

It is testing whether the interaction model works.

Questions include:

- Can a character understand the Dungeon Master's description of its environment?
- Can it choose sensible actions without seeing a tool list?
- Does personality affect those decisions?
- Can it express intentions clearly enough for another model to interpret?
- Can the Dungeon Master reliably translate natural-language intentions into game actions?
- Can the Dungeon Master avoid inventing mechanical outcomes?
- How often do characters request unsupported actions?
- What kinds of unsupported actions occur repeatedly?
- Do characters respond sensibly when actions are rejected?
- Does descriptive information such as visible injuries affect decisions?
- Can persistent structured state remain coherent over multiple turns?
- Do different models behave substantially differently in the same situation?

---

# Later Directions

Once the minimal loop works, complexity can be introduced gradually.

Possible additions include:

- Multiple heroes ✅
- Multiple monsters ✅
- Items ✅
- Containers ✅
- Chests ✅
- Doors ✅
- Environmental objects
- Abilities and spells ✅
- Status effects ✅
- Character-to-character communication ✅
- Hidden information ✅
- Character-specific knowledge ✅
- Object interaction ✅
- Persuasion and intimidation ✅
- Fleeing and surrender ✅
- Seeded random outcomes ✅
- More than one room
- Persistent campaigns
- Human-controlled characters
- Different models for different characters ✅
- Model and parameter comparisons ✅

Randomness should preferably be **seeded** when introduced so equivalent experiments can be reproduced.

---

# Design Constraint

The project should resist becoming a complete D&D implementation.

The fantasy game exists primarily as a useful environment for experimenting with autonomous agents.

A useful test for every new mechanic is:

> Does this allow the agents to express meaningfully different behaviour?

If not, it probably does not belong in the experiment yet.

---

# One-Line Architecture

> **Autonomous characters express creative intent in natural language; an AI Dungeon Master interprets that intent; a deterministic game engine decides what actually happens.**
