# Models & Monsters — Build the v0.1 Chassis

Build the first working implementation of **Models & Monsters**, a small experimental framework for autonomous LLM characters interacting inside a deterministic fantasy game world through an LLM Dungeon Master.

This is a **technical proof of concept**, not a game product and not an attempt to implement Dungeons & Dragons.

The purpose of this first pass is to establish the architecture, model abstraction, explicit orchestration, conversation ownership, deterministic game engine, and complete experiment tracing.

Do **not** expand the game beyond the minimum required to prove one complete Hero → DM → Engine → Monster → DM → Engine loop.

---

# Technical Constraints

Use:

- .NET 10
- Console application
- One application project
- One xUnit test project
- `Microsoft.Extensions.AI` as the model abstraction
- `OllamaSharp` underneath for Ollama-hosted models
- OpenAI-hosted models through the corresponding `Microsoft.Extensions.AI` / OpenAI integration
- Dependency injection / Generic Host where useful

Do not introduce:

- Semantic Kernel
- Microsoft Agent Framework
- AutoGen
- Any external agent orchestration framework
- A database
- A web UI
- A REST API
- A message broker

The orchestration is part of the experiment and must remain explicit in our own code.

---

# Core Concept

Characters are inhabitants of the game world.

They do **not** know about game-engine functions.

They communicate only with the Dungeon Master in natural language.

The flow is:

```text
Character
    │
    │ natural-language intent
    ▼
Dungeon Master
    │
    │ structured tool call
    ▼
Game Engine
```

The game engine is the authoritative source of truth.

The models decide intent.

The Dungeon Master interprets intent.

The game engine decides what actually happens.

Only the Dungeon Master may invoke state-changing game-engine actions.

---

# v0.1 Actors

There are exactly three AI agents:

1. Dungeon Master
2. Hero
3. Monster

Hero and Monster use the same underlying character-agent implementation, with different character definitions/prompts.

Do not special-case Hero and Monster orchestration unless genuinely necessary.

---

# Character Agent Protocol

Each character agent has exactly two conceptual tools available to it:

```text
ask_dm(question)
take_action(intent)
```

Both arguments are free-form natural-language strings.

For example:

```text
ask_dm(
    "Does the goblin look badly wounded?"
)
```

or:

```text
take_action(
    "I feint to the goblin's left and then slash at its injured side with my sword."
)
```

Characters must **not** receive game-engine tools such as:

```text
AttackCharacter
UseItem
GetHealth
GetInventory
```

They should not know the implementation surface.

Characters should know the kinds of things a person in the world might reasonably try, but their prompt should encourage creative natural-language actions rather than selection from a fixed menu.

A character may ask multiple questions before attempting an action.

Only a successfully resolved action consumes the turn.

If an action is rejected, the character receives the rejection/explanation and may attempt another action.

Include a configurable safety limit on repeated question/action loops so a badly behaved model cannot loop forever.

This limit is a harness protection, not an in-world game rule.

---

# Character Knowledge

At the beginning of its turn, a character receives exact information about itself.

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

Abilities:
- None

Current situation:

"The goblin staggers backwards from your last attack..."
```

Characters may receive exact information about:

- Their identity
- Their own health
- Their own injuries
- Their own equipment
- Their own inventory
- Their own abilities

They should generally **not** receive raw authoritative numeric state for opponents.

For example, the engine may know:

```text
Goblin Health = 2 / 8
```

but the Hero should instead receive something like:

> "The goblin is badly wounded and struggling to remain upright."

The Dungeon Master's narration is part of the character's perception of the world.

---

# Character Configuration

Design character definitions so they can eventually contain:

- Name
- Backstory
- Personality
- Wants
- Needs
- Fears
- Equipment
- Inventory
- Abilities
- Model configuration

For v0.1, only populate what is actually useful.

Do not build a complex character-definition system yet.

---

# Model Configuration

Each agent must be independently configurable.

Create an appropriate model/profile abstraction supporting at least:

```text
Provider
ModelId
Temperature
TopP
TopK
MaxOutputTokens
Seed
```

Allow:

```text
Dungeon Master → one model/configuration
Hero           → another model/configuration
Monster        → another model/configuration
```

The same provider/model may of course be used for all three.

The implementation must allow switching between:

- Ollama models
- OpenAI-hosted models

without changing agent/orchestration code.

Use `Microsoft.Extensions.AI`'s `IChatClient` abstraction throughout agent-facing code.

Provider-specific construction/configuration should be isolated behind a small factory/resolver.

Do not leak OllamaSharp or OpenAI-specific client types through the rest of the application.

Not every provider necessarily supports every sampling option identically.

Handle this cleanly rather than assuming complete parity.

The trace should record the configuration we requested for each call.

---

# Conversation Ownership

The application owns all conversation history.

Do not use provider-owned threads, assistants, sessions, or persistent conversation APIs.

Each agent has its own independent conversation history:

```text
Dungeon Master history
Hero history
Monster history
```

The histories must remain isolated.

## Character histories

A character should remember:

- What it previously perceived
- Questions it asked the DM
- Answers the DM gave it
- Actions it attempted
- Rejections it received
- Results/outcomes communicated to it

A character must **not** automatically receive another character's private conversation with the Dungeon Master.

For example:

```text
Hero → DM:
"Can I tell if the goblin is frightened?"

DM → Hero:
"Yes. It keeps glancing nervously toward the exit."
```

The Monster should not receive that exchange simply because it happened.

The Monster only learns things through:

- Its own interactions
- The Dungeon Master's public situation narration
- Other information the harness intentionally makes perceptible to it

## Dungeon Master history

The Dungeon Master has its own conversation history containing the interactions it has handled.

It may therefore remember prior character questions, attempted actions, rulings and narration.

However, authoritative game state must always come from the engine rather than relying on the DM's memory.

---

# Authoritative State Injection

Before asking the Dungeon Master to narrate or adjudicate, provide it with a fresh authoritative snapshot containing whatever state it requires.

The DM may know more than an individual character.

The engine remains the source of truth even if the DM's conversation history says something inconsistent.

For v0.1, prefer passing authoritative state to the DM over creating lots of state-query tools.

We can introduce explicit query tools later if they become useful.

---

# Dungeon Master Responsibilities

The Dungeon Master has three responsibilities.

## 1. Narration

Convert authoritative engine state into natural descriptive prose.

The narration should communicate observable information without simply dumping structured state.

Example:

Engine:

```text
Goblin:
Health = 2 / 8
```

DM:

> "The goblin is bleeding heavily now and can barely keep its axe raised."

Narration should describe:

- The room
- Visible actors
- Visible equipment
- Relevant injuries
- Recent action outcomes
- Current battle situation

Keep prose reasonably concise.

This is a simulation harness, not a fantasy-novel generator.

## 2. Questions

Answer character questions using:

- Authoritative game state
- What that character could reasonably perceive or know

Avoid leaking private/hidden information.

v0.1 does not need a sophisticated knowledge model, but structure the code so character-specific perception can evolve later.

## 3. Action Interpretation

Translate a character's natural-language intention into a supported game-engine action.

The DM should not invent mechanical outcomes.

For example:

Character:

> "I drive my sword into the goblin."

DM interprets this as:

```text
AttackCharacter(
    attacker = Hero,
    target = Goblin,
    weapon = IronSword
)
```

The engine decides the result.

---

# Action Rejection

Support structured rejection.

Conceptually distinguish:

```text
DM_IMPOSSIBLE
DM_UNSUPPORTED
ENGINE_REJECTED
ENGINE_ACCEPTED
```

## DM_IMPOSSIBLE

The requested action is not plausible given the known world/character.

Example:

> "I fly onto the ceiling."

when the character has no means of flight.

No engine call is required.

## DM_UNSUPPORTED

The action is reasonable in the fictional world but the current game engine cannot represent it.

Example:

> "I wrap my cloak around the goblin's head."

This is important experimental data.

Do not silently reinterpret wildly unsupported actions as something else merely to make them succeed.

## ENGINE_REJECTED

The DM translated the intent into a supported action, but the authoritative engine rejected it.

Example:

The DM attempts to use a weapon that the actor does not actually possess.

## ENGINE_ACCEPTED

The action was accepted, resolved and state changed accordingly.

Use proper types/enums rather than magic strings where appropriate.

---

# Manual Tool Orchestration

Do **not** use automatic recursive tool-execution middleware for the core agent loop.

We want explicit application code controlling:

```text
model response
→ tool request
→ tool argument capture
→ application dispatch
→ tool result
→ result returned to model
→ subsequent model response
```

It is fine to use the function/tool abstractions provided by `Microsoft.Extensions.AI`.

However, our application must explicitly inspect and invoke requested tools.

Do not hide the sequence inside agent-framework or automatic function-invocation middleware.

The purpose is full observability and control.

---

# Minimal Game Engine

Implement the smallest useful deterministic engine.

## Character State

Something broadly equivalent to:

```text
CharacterState

Id
Name

MaxHealth
Health

Armour

Weapon

Inventory

Injuries

Alive
```

Use sensible .NET types and domain modelling rather than matching this pseudo-schema literally.

## Weapon

At minimum:

```text
Name
Damage
```

## Attack Rule

Use:

```text
damage = max(0, weaponDamage - targetArmour)

targetHealth -= damage
```

An accepted attack always hits.

No RNG.

No attack roll.

No critical hits.

No dodge.

No distance.

No line of sight.

No initiative.

No bonus actions.

No reactions.

No D&D AC rules.

## Death

A character is dead when:

```text
Health <= 0
```

Clamp health sensibly if appropriate.

## Healing

A simple healing item may be included if it remains trivial:

```text
newHealth = min(maxHealth, currentHealth + healingAmount)
```

and the item is removed/consumed.

If adding healing starts expanding scope, omit it from this first pass.

---

# Injuries

Allow injuries to exist as persistent descriptive state.

They do not need mechanical effects in v0.1.

Do not build an elaborate procedural injury system.

It is sufficient to prove that something such as:

```text
Minor cut to left arm
```

can exist in authoritative state and be included in later prompts.

If generating injuries introduces unnecessary complexity, seed one or keep injuries empty initially.

---

# Room

There is exactly one room.

No movement model.

No coordinates.

No range.

No line of sight.

Everyone in the room can interact with everyone else unless another rule explicitly prevents it.

Represent the room in a way that can later grow to contain objects, descriptions and private/public information, but do not implement that complexity now.

---

# v0.1 Scenario

Create one hard-coded or configuration-seeded scenario.

Example:

## Hero

```text
Name: Aric

Health: 10
Max Health: 10

Armour: 1

Weapon:
Iron Sword
Damage: 4

Personality:
Brave, direct, somewhat protective.

Goal:
Survive and defeat or otherwise overcome the goblin.
```

## Monster

```text
Name: Grik

Health: 8
Max Health: 8

Armour: 1

Weapon:
Rusty Axe
Damage: 3

Personality:
Cowardly, spiteful and opportunistic.

Goal:
Drive the intruder away and survive.
```

Exact wording and numbers may be adjusted if there is a good implementation reason.

Do not add extra actors.

---

# Turn Sequence

Use fixed ordering:

```text
Hero
Monster
Hero
Monster
...
```

No initiative roll.

A round is one Hero turn followed by one Monster turn.

At a high level:

```text
START

Engine creates initial state

Engine → DM
authoritative state

DM
describes initial situation

HERO TURN

Harness → Hero
- Hero's own exact state
- Hero prompt/personality
- Latest relevant DM narration

Hero may:
- ask_dm(...)
- ask_dm(...)
- ...
- take_action(...)

DM interprets action

If DM rejects:
    return explanation to Hero
    Hero may try again

If DM produces supported game action:
    Engine validates it

If Engine rejects:
    return result through DM to Hero
    Hero may try again

If Engine resolves action:
    persist updated state
    DM narrates result
    Hero turn ends

MONSTER TURN

Same structure using Monster's independent conversation/history

After Monster's completed action:

Engine → DM
fresh authoritative state

DM describes latest situation

Continue until terminal condition.
```

Initially the terminal condition can simply be one actor reaching zero health.

---

# Tracing

Tracing is a first-class requirement.

We want to be able to reconstruct why every decision was made.

Do **not** emit all of this to the console.

The console should remain suitable for eventually displaying the readable game interaction.

Detailed experiment traces must be written to files.

Create something conceptually equivalent to:

```text
runs/
    <run-id>/
        run.json
        trace.jsonl
        final-state.json
```

Use a unique run ID.

Timestamp may form part of the directory/file naming.

---

# run.json

Record run-level metadata including:

- Run ID
- Start time
- Application version if readily available
- Scenario definition
- Initial authoritative world state
- Dungeon Master model/profile
- Hero model/profile
- Monster model/profile
- Relevant prompt/version identifiers
- Harness limits/configuration

Do not record secrets.

---

# trace.jsonl

Use an append-only JSON Lines event stream.

Every important event should have:

```text
Sequence
Timestamp
RunId
Round
Turn
Actor
EventType
```

plus event-specific data.

Capture enough information to reconstruct the complete interaction.

At minimum record:

## Model request

- Which agent
- Provider
- Model ID
- Complete message collection sent to the model
- System prompt
- Conversation history as actually sent
- Newly injected state/context
- Requested model options
- Tool definitions exposed to that call
- Tool JSON schemas where available/relevant

## Model response

- Complete returned assistant content
- Tool calls requested
- Tool-call IDs
- Tool names
- Complete tool arguments
- Finish reason if available
- Usage information if available
- Provider/model metadata if available
- Elapsed time

## Tool execution

- Tool requested
- Arguments received
- Application dispatch decision
- Tool execution result
- Errors/exceptions

## DM adjudication

- Original character intent
- Rejection category if rejected
- Rejection reason
- Structured game action if translated

## Engine action

- Requested structured action
- Validation result
- State before
- Result
- State after

For tiny v0.1 state, recording full before/after snapshots is preferable to clever diffs.

## Narration

- State/context supplied to DM
- Narration returned
- Which actors subsequently received that narration

## Exceptions/retries

Record failures without losing the preceding trace.

---

# Trace Architecture

Do not implement tracing as random `ILogger` statements scattered through the codebase.

Create an explicit experiment trace abstraction, for example:

```text
ITraceSink
JsonlTraceSink
```

and appropriate structured trace event types.

It may also make sense to use a delegating/wrapping `IChatClient` for capturing raw logical model requests and responses.

Use whatever design is cleanest.

Normal operational logging and experiment tracing are separate concerns.

The experiment trace is part of the application's data output.

---

# Important Tracing Boundary

Capture what enters and leaves our `IChatClient` abstraction.

We are interested in the logical model interaction visible to the application.

Do not attempt to capture:

- HTTP authorization headers
- API keys
- Provider credentials
- Other secrets

Do not build HTTP wire-level interception unless genuinely required.

---

# Prompts

Keep prompts in dedicated files or clearly separated resources rather than embedding large prompt strings throughout orchestration classes.

At minimum have distinct prompts for:

```text
Dungeon Master
Character Agent
```

The Hero and Monster can use the same base character prompt plus their individual character definition.

Prompts should be easy to edit and version later because prompt comparison will likely become part of the experiment.

---

# Suggested Code Organisation

This is guidance, not a mandated namespace structure:

```text
ModelsAndMonsters/
    Agents/
        CharacterAgent
        DungeonMasterAgent
        AgentConversation

    AI/
        AgentModelProfile
        ChatClientFactory
        provider configuration

    Domain/
        Character
        Weapon
        InventoryItem
        Injury
        Room
        GameState

    Engine/
        GameEngine
        game actions
        action results

    Orchestration/
        SimulationRunner
        TurnCoordinator
        character/DM interaction loop

    Prompts/
        dungeon-master prompt
        character prompt

    Tracing/
        ITraceSink
        JsonlTraceSink
        trace event models
        optional tracing chat-client wrapper

    Program.cs
```

Keep it one application project.

Folders/namespaces are enough.

Do not split this into multiple production class libraries.

---

# Tests

Create a separate xUnit test project.

The deterministic engine should be thoroughly unit-testable without any model calls.

At minimum test:

- Attack damage calculation
- Armour reduces damage
- Damage cannot become negative
- Health changes correctly
- Death condition
- Invalid actor/target/weapon scenarios as applicable
- Engine rejection does not mutate state
- Accepted actions mutate state exactly once

Test orchestration pieces using fake/stub `IChatClient` implementations rather than requiring live Ollama/OpenAI calls.

At minimum demonstrate that:

- Character tool calls can be captured and manually dispatched
- `ask_dm` does not consume a turn
- Rejected `take_action` allows another attempt
- Accepted action ends the character's turn
- Hero and Monster conversation histories remain isolated
- Trace events are emitted for model calls and tool execution

Do not require external model services for the normal test suite.

---

# Configuration

Use configuration/appsettings/environment variables sensibly for things such as:

- Provider selection
- Ollama endpoint
- Ollama model
- OpenAI model
- OpenAI API key via environment/user secrets
- Sampling settings
- Run output directory
- Harness retry/question limits

Do not commit credentials.

It should be easy to configure something conceptually like:

```text
DungeonMaster:
    Provider: Ollama
    Model: qwen...

Hero:
    Provider: Ollama
    Model: llama...

Monster:
    Provider: OpenAI
    Model: ...
```

without changing source code.

Exact configuration shape is up to you.

---

# Console Output

Keep console output minimal and human-readable.

It may show things such as:

```text
DM:
The goblin tightens its grip on the axe...

Aric:
"I've had enough of you."

DM:
Aric's sword catches the goblin across the shoulder...
```

Do not dump:

- Full system prompts
- JSON tool arguments
- Complete message histories
- Full state snapshots
- Raw trace events

Those belong in the run trace files.

---

# Scope Guardrails

Do not implement:

- Multiple rooms
- Multiple heroes
- Multiple monsters
- Spatial movement
- Maps
- Grid systems
- Line of sight
- Dice
- RNG
- Full D&D rules
- Spell systems
- Complex abilities
- Procedural content
- Character generation
- Persistent database storage
- Web interface
- Save/load UI
- Benchmark scoring
- Automated model judging
- Vector memory
- RAG
- Summarised long-term memory
- Agent frameworks

Do not attempt to make the encounter strategically rich.

The first objective is to prove the communication architecture.

---

# Key Design Rule

Do not solve unsupported character creativity by continually expanding the game engine during this task.

If a character says:

> "I throw my sword at the goblin."

and v0.1 only supports ordinary weapon attacks, it is completely acceptable for the DM to reject this as unsupported.

That behaviour is part of the experiment.

The game should initially expose a deliberately narrow control surface.

---

# Definition of Done

Stop when all of the following are true:

1. The .NET 10 solution builds cleanly.

2. There is one console application project and one xUnit test project.

3. `Microsoft.Extensions.AI` / `IChatClient` is the abstraction used by all agents.

4. OllamaSharp-backed models can be configured.

5. OpenAI-hosted models can be configured through the same agent-facing abstraction.

6. Dungeon Master, Hero and Monster can each have independent model profiles.

7. Hero and Monster use the `ask_dm` / `take_action` protocol.

8. Tool calls are manually orchestrated by our application code.

9. Each of the three agents owns an independent conversation history.

10. Hero and Monster do not automatically see one another's private DM exchanges.

11. The deterministic game engine can resolve the minimal attack mechanic.

12. Only the Dungeon Master can cause game-engine state-changing actions.

13. Rejections are structured and permit the character to retry.

14. A full Hero turn can execute:

```text
Hero
→ natural-language intent
→ DM
→ structured engine action
→ engine
→ state change
→ DM narration
```

15. A Monster turn can execute through the same pattern.

16. At least one complete Hero + Monster round can run against live models.

17. The complete logical request/response/tool/state sequence is captured in structured files.

18. Normal unit tests do not require Ollama or OpenAI connectivity.

19. Console output remains readable rather than diagnostic.

20. No significant gameplay functionality beyond this scope has been added.

---

# Final Deliverable

Once the implementation is complete:

- Run the tests.
- Build the application.
- If an Ollama instance/model is locally available, perform one small live smoke run.
- Do not assume OpenAI credentials are available.
- Inspect the generated trace output and confirm it contains enough information to reconstruct the decisions and state transitions.
- Report:
  - What was implemented
  - Project/file structure
  - How model/provider configuration works
  - How conversation isolation works
  - How manual tool dispatch works
  - What is captured in tracing
  - Tests added and results
  - Any intentional deviations from this prompt
  - Any issues or design questions discovered

Do not continue into v0.2 features.

The desired result of this task is a **small, understandable chassis that we can begin experimenting with**, not a miniature RPG engine.