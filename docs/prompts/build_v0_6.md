# Models & Monsters v0.6 — Inventory Transfers, Rulebook Resolver and Comparison Harness

Extend the existing **Models & Monsters** implementation to v0.6.

This is an experimental framework for autonomous LLM characters interacting inside a deterministic fantasy world. Preserve the existing architecture and behaviour from earlier versions unless this prompt explicitly changes it.

The purpose of v0.6 is to:

1. Support the inventory actions models already naturally attempt:
   - giving an item;
   - dropping an item;
   - stealing an item.
2. Reduce the Dungeon Master’s growing rules context by introducing a separate, bounded rulebook-resolution call.
3. Strengthen item ownership and provenance tracing.
4. Add a minimal repeatable model and parameter comparison harness.

Do not use this release to introduce another large gameplay system.

---

# Existing Principles

Continue to enforce these principles:

- The game engine is the authoritative source of truth.
- Models decide intent; they do not directly mutate state.
- The Dungeon Master translates natural-language intent into a supported engine action.
- Orchestration remains explicit in our own code.
- Each character owns its own conversation history.
- Characters do not see other characters’ private histories.
- Hidden information and character-specific knowledge remain enforced.
- All model requests and responses are traced to files.
- `end_turn` remains a valid deliberate null action.
- The engine, not any model, performs RNG.
- Stable IDs are used when binding actors, objects, items and targets.
- The observer UI displays events but does not become authoritative game state.

Continue using:

- .NET 10
- `Microsoft.Extensions.AI`
- OllamaSharp for Ollama-hosted models
- the existing OpenAI integration for hosted models
- the existing application and test-project structure
- explicit orchestration without an external agent framework

Start by inspecting the current implementation and adapting to its established names and patterns. Do not needlessly replace working v0.5 architecture.

---

# Part One — Inventory Transfers

Add three authoritative engine actions:

```text
give_item
drop_item
steal_item
```

These are genuine state-changing turn actions. The DM may select them only when they match the acting character’s natural-language intent.

## Item Ownership Invariant

At every authoritative state boundary, an item must exist in exactly one location.

Possible locations include:

- a character inventory;
- an existing container;
- the room’s ground-loot collection;
- another established item location already supported by the application.

An item must never simultaneously exist:

- in two inventories;
- in an inventory and a container;
- in an inventory and on the ground;
- in any other duplicate locations.

Use stable item IDs for all transfers.

Every transfer must be atomic. If validation fails, leave ownership and location unchanged.

If the current model has no suitable ground-loot representation, introduce a simple room-level ground collection or an always-open ground-loot container. Do not create a parallel inventory system.

Dropped items must subsequently be recoverable through the existing item-taking interaction, subject to the usual visibility and presence rules.

---

# `give_item`

Conceptual action:

```text
give_item(giver_id, recipient_id, item_id)
```

The current actor is the giver. Do not allow the DM to move an item on behalf of a different giver.

Validation:

- The giver is the current active actor.
- The giver is alive, active and present in the room.
- The recipient is alive and present in the same room.
- The giver currently owns the item.
- The item is an ordinary inventory item.
- The item is not currently an equipped weapon.
- All IDs resolve against authoritative state.

Behaviour:

- Transfer the item atomically from giver to recipient.
- No RNG is required.
- Giving consumes the giver’s turn.
- The transfer is a public, observable event.
- Giving is allowed between allies or opponents.
- Recipient consent is not modelled in v0.6.
- The recipient may react during a later turn by using, giving or dropping the item.

Do not invent an offer/accept negotiation system.

---

# `drop_item`

Conceptual action:

```text
drop_item(actor_id, item_id)
```

Validation:

- The actor is the current active actor.
- The actor is alive and present.
- The actor currently owns the item.
- The item is an ordinary inventory item.
- The item is not currently an equipped weapon.

Behaviour:

- Transfer the item atomically to the room’s ground-loot location.
- No RNG is required.
- Dropping consumes the actor’s turn.
- The action is public and observable.
- The item retains the same stable ID.
- The dropped item can subsequently be taken using the established interaction path.

Do not silently destroy or recreate the item.

---

# `steal_item`

Conceptual action:

```text
steal_item(thief_id, target_id, item_id)
```

Validation:

- The thief is the current active actor.
- The thief and target are both active, alive and present in the same room.
- The target currently owns the item.
- The item is an ordinary inventory item.
- Equipped weapons cannot be stolen in v0.6.
- The item ID is valid at the moment the action is resolved.
- The thief has a valid informational basis for identifying the item.

A character must not steal an item it has no reason to know exists. A valid informational basis could include:

- seeing the item carried;
- witnessing the item being taken, given or dropped;
- inspecting an open container before it was taken;
- being told about it, recorded as hearsay.

Do not leak hidden container contents or private inventory knowledge merely to make the action bind successfully.

## Theft Resolution

Theft consumes the actor’s turn whether it succeeds or fails.

Use exactly one seeded RNG draw for the theft result.

Introduce a small configurable base theft chance, such as:

```text
BaseStealChance = 40%
```

Use the project’s existing configuration conventions.

Do not invent Dexterity, Perception, Awareness or a general skill system. Only apply modifiers if an already-existing authoritative rule clearly supports them.

Record:

- the base chance;
- every applied modifier;
- the final effective chance;
- the raw RNG result;
- success or failure.

On success:

- Transfer the item atomically from target to thief.

On failure:

- Do not change item ownership.

In v0.6, every theft attempt is noticed. The attempt and result are public facts visible to everyone present.

Do not implement:

- secret successful theft;
- a separate detection roll;
- stealth;
- automatic retaliation;
- automatic damage;
- relationship statistics;
- status effects;
- disarming or equipped-weapon theft.

Characters may autonomously react to the public theft event during later turns.

Ensure every RNG draw records its seed/state, purpose, candidates, raw roll, modifiers, and result. Otherwise the new hit chance will make behavioural comparisons much harder to reproduce.

---

# Item Provenance

Extend tracing and reporting so every successful item movement records:

- item ID and display name;
- previous owner or location;
- new owner or location;
- action type;
- acting character;
- target or recipient where applicable;
- round and turn;
- whether RNG was involved;
- associated RNG trace ID where applicable;
- rulebook consultation ID;
- reason or source action.

Provenance is an event history, not a second source of authoritative ownership.

The final report should make it possible to reconstruct an item’s journey through the encounter.

---

# Knowledge and Visibility

Giving, dropping and attempted theft are public events.

All eligible characters presently in the room should learn the appropriate facts through the existing knowledge system.

This includes:

- who gave which visible item to whom;
- who dropped which visible item and where;
- who attempted to steal;
- the target of the theft;
- the item involved, provided its identity was legitimately observable;
- whether the theft succeeded.

Preserve the existing distinction between:

- directly observed facts;
- information received through speech or hearsay;
- hidden or unknown facts.

Escaped characters do not receive later room events.

Surrendered characters who remain physically present may observe public events, even though they do not take turns.

Do not make every character omniscient merely because the event is present in the global trace.

---

# Part Two — Bounded Rulebook Resolver

The Dungeon Master’s system prompt is becoming too large as more actions and restrictions are added.

Introduce a separate rulebook-resolution stage that resembles a real DM consulting a rulebook.

For v0.6, rulebook consultation is automatic for every character `take_action` request.

Do not rely on the DM deciding whether it needs help. A confidently incorrect DM would otherwise skip the lookup.

The high-level flow becomes:

```text
Character natural-language intent
    → deterministic rule-card retrieval
    → stateless Rulebook Resolver model call
    → structured rule guidance
    → Dungeon Master binds guidance to current authoritative state
    → engine validates and resolves the selected action
```

The Rulebook Resolver advises on rules. It does not see or control the live encounter.

The game engine remains authoritative even if the rulebook model or DM makes a mistake.

---

# Rule Cards

Move detailed, action-specific rule prose out of the DM system prompt and into small versioned rule cards.

Suggested rule-card groups include:

```text
combat.attack
inventory.use
inventory.give
inventory.drop
inventory.steal
container.inspect
container.open
container.take
encounter.open-exit
encounter.escape
encounter.surrender
object.inspect
action.reject
```

Adapt these to the actions actually present in the current implementation.

Each rule card should have:

- a stable rule ID;
- a version;
- action name;
- intent tags or phrases;
- concise description;
- required bindings;
- preconditions;
- turn cost;
- RNG requirements;
- visibility rules;
- success behaviour;
- failure behaviour;
- exclusions or unsupported variants.

Keep the cards concise and independently versionable.

Do not send the entire rulebook to the resolver on every request.

---

# Rule Retrieval

Implement a small deterministic retrieval layer, such as:

```text
IRuleRepository
IRuleRetriever
IRulebookResolver
```

Names may be adapted to the existing code style.

Retrieval should use a compact action index, tags or intent catalogue to select a bounded set of likely relevant cards.

Do not introduce:

- a database;
- embeddings;
- a vector store;
- a general RAG platform;
- semantic-search infrastructure.

Retrieval must have a configured maximum number of cards and a maximum input size.

If the intent is ambiguous, retrieve a small candidate set rather than the whole rules collection.

Always include the generic rejection/failure guidance where required.

Trace which cards were considered and which were sent to the Rulebook Resolver.

---

# Rulebook Resolver Model Call

Use `Microsoft.Extensions.AI.IChatClient` for the Rulebook Resolver.

Give it an independently configurable model profile, including:

- provider;
- model ID;
- temperature;
- top-p and other supported parameters;
- maximum output tokens;
- seed where supported.

Use a low-temperature profile by default.

The Rulebook Resolver call is stateless. It must not receive or retain conversation history.

Its input should contain only:

- the character’s raw natural-language action intent;
- the bounded set of relevant rule cards;
- the schema for the required structured response.

It must not receive:

- authoritative game state;
- the full DM conversation;
- character conversation histories;
- private character knowledge;
- hidden object or inventory state;
- current hit points;
- target availability;
- actual RNG state or results.

The resolver answers abstract rule questions. It does not determine whether a specific action is currently valid.

It must not have access to game-engine tools.

---

# Structured Rule Guidance

Return structured guidance similar to:

```text
RuleGuidance
- ConsultationId
- Supported
- CandidateActions
- CitedRuleIdsAndVersions
- RequiredBindings
- Preconditions
- TurnCost
- RngSpecification
- Visibility
- SuccessBehaviour
- FailureBehaviour
- UnsupportedReason
```

Use strict structured output or the project’s established reliable JSON parsing approach.

Every supported recommendation must cite the relevant rule IDs and versions.

If the request is unsupported, the guidance should explain which part is unsupported without inventing a new engine action.

Treat the guidance as untrusted advisory input. Validate its shape, bounds and cited rule IDs before passing it forward.

---

# Dungeon Master Prompt

Reduce the DM system prompt to a compact constitution containing durable responsibilities such as:

- authoritative state always wins;
- act only for the current actor;
- do not invent state, actions or outcomes;
- respect hidden information and character knowledge;
- use stable IDs;
- bind the character’s intent using the supplied rule guidance;
- choose exactly one supported engine action or reject the intent;
- never perform RNG;
- do not reveal orchestration machinery to characters;
- narrate only confirmed engine results.

Do not keep duplicating the detailed action rules in the DM prompt after they have moved into rule cards.

For each action, provide the DM with ephemeral:

- raw character intent;
- current authoritative state relevant to the actor;
- the acting character’s permitted knowledge;
- structured rule guidance;
- only the candidate engine tool or small tool set indicated by the guidance;
- a rejection option.

For example, if the resolver identifies a clear `steal_item` request, expose only:

```text
steal_item
reject_action
```

If the resolver returns a genuinely ambiguous small candidate set, expose only that set plus rejection.

Do not expose every engine tool on every DM call.

Rule guidance is request-scoped and must not accumulate in the DM’s long-term conversation history.

---

# Resolver Failure Behaviour

The system must fail safely.

If rule retrieval, resolver output parsing or rule validation fails:

- do not invent an action;
- do not bypass engine validation;
- produce a traceable rejection or bounded technical failure according to existing harness conventions;
- avoid repeatedly calling the resolver for the same failed character intent;
- allow the turn coordinator to reach a stable outcome rather than looping.

Distinguish in traces between:

- unsupported character intent;
- rule retrieval failure;
- resolver model failure;
- malformed rule guidance;
- DM binding failure;
- engine validation rejection.

---

# Rulebook Caching

A small cache may be implemented for abstract rule guidance.

Any cache key must include:

- normalized intent or intent classification;
- selected rule IDs and versions;
- resolver model/profile identity;
- response-schema version.

Never cache:

- live game-state decisions;
- target validity;
- item ownership;
- hidden knowledge;
- RNG outcomes;
- any answer containing encounter-specific bindings.

Trace cache hits and misses.

Correctness and version safety matter more than cache sophistication.

---

# Rulebook Context Protection

Apply explicit context and output limits to rulebook calls.

Trace:

- number of retrieved cards;
- rule-card input size;
- total request size;
- configured context limit;
- output-token limit;
- actual usage where available;
- whether the request was rejected or trimmed.

The resolver request must remain bounded regardless of encounter length.

It must not grow with the number of rounds already played.

---

# Part Three — Repeatable Comparison Harness

Add a minimal harness for comparing models and model parameters across repeated runs.

This is an experiment runner, not a statistical-analysis product.

It should be possible to define:

- a fixed scenario;
- number of repetitions;
- a deterministic seed schedule;
- character model profiles;
- DM model profile;
- Rulebook Resolver model profile;
- relevant generation parameters;
- output location.

The same experiment configuration should be serializable and reusable.

Every individual run must retain its ordinary run folder and detailed trace.

The experiment should also generate an aggregate machine-readable result and a concise Markdown summary.

---

# Experiment Manifest

Record enough information to reproduce a comparison:

- application version;
- scenario ID and version;
- rulebook version or content hash;
- prompt versions or hashes;
- experiment configuration;
- repetition number;
- encounter RNG seed;
- requested model provider and model ID for every character;
- DM model profile;
- Rulebook Resolver model profile;
- all requested generation parameters;
- configuration values affecting gameplay;
- start and completion timestamps;
- run result and termination reason.

Clearly distinguish requested model parameters from parameters confirmed or supported by a provider.

Do not assume every provider implements every `ChatOptions` setting.

---

# Seed Schedule

Support a deterministic seed schedule so equivalent experiment configurations use the same sequence of encounter seeds.

The experiment runner must record the resolved seed for every run.

Model randomness and engine randomness must remain conceptually distinct.

If a provider does not support model seeding, record that limitation instead of implying complete model-output reproducibility.

The engine RNG trace must still make authoritative game outcomes reproducible from the same accepted actions.

---

# Aggregate Comparison Report

At minimum, aggregate:

- number of attempted, completed and failed runs;
- encounter outcome and outcome type;
- winning or remaining team;
- rounds and turns;
- termination reason;
- damage and combat-action counts;
- surrender and escape attempts;
- speech count;
- inventory give/drop/steal attempts;
- theft successes and failures;
- invalid or rejected actions;
- repeated-action or harness-limit terminations;
- rulebook consultation count;
- rulebook cache hits and misses;
- cited rule-card frequency;
- malformed or failed rulebook responses;
- DM binding failures;
- engine validation rejections after rulebook guidance;
- prompt and completion tokens by role where available;
- latency by character, DM and Rulebook Resolver;
- total model calls by role.

Keep raw values per run as well as useful totals, averages or rates.

Do not claim statistical significance.

A comparison with only a few runs should be labelled descriptive.

---

# Comparison Scope

Support independently selecting profiles for:

- each character;
- the Dungeon Master;
- the Rulebook Resolver.

A small configuration matrix is acceptable if it fits the existing configuration architecture.

Avoid building:

- a large experiment-design language;
- parameter sweeps across every possible combination;
- charts requiring a new frontend framework;
- automatic conclusions about which model is “best”;
- cloud scheduling;
- distributed execution.

Sequential execution is acceptable. Preserve deterministic run ordering.

---

# Observer UI

Update the existing observer UI only as needed to reflect the new state and actions.

Display:

- current inventories;
- visible ground items;
- give, drop and steal events in the transcript;
- theft success or failure;
- item movement without requiring a page refresh;
- existing dispositions and encounter outcome.

Rulebook internals do not need to clutter the gameplay transcript.

A small diagnostic indicator or report link is acceptable, but detailed rulebook requests and responses belong in the run artifacts.

Do not turn the observer UI into a game editor or experiment-analysis dashboard in v0.6.

---

# Tracing

Continue full file-based tracing without dumping sensitive model traffic to the normal console UI.

For every rulebook consultation, trace:

- consultation ID;
- acting character;
- raw intent;
- rulebook version;
- retrieval inputs;
- candidate rule IDs;
- cards actually supplied;
- resolver provider, model and requested parameters;
- complete resolver request;
- complete raw resolver response;
- parsed structured guidance;
- cited rule IDs and versions;
- validation outcome;
- cache hit or miss;
- input/output token usage where available;
- latency;
- DM candidate tools exposed;
- DM action selected;
- eventual engine validation and resolution.

For inventory actions, trace:

- requested bindings;
- authoritative bindings;
- validation result;
- ownership before and after;
- provenance event;
- visibility recipients;
- turn consumption;
- RNG consultation where applicable.

Never silently truncate a model request.

If any provider-side context fitting or history trimming occurs, trace exactly what the model was actually sent.

---

# Reports

Extend the per-run report with concise sections for:

## Rulebook Consultations

- consultation count;
- cache statistics;
- cards retrieved and cited;
- supported versus unsupported intents;
- failures by stage;
- tokens and latency;
- DM-to-engine rejection rate after guidance.

## Inventory Activity

- gives;
- drops;
- theft attempts;
- theft successes and failures;
- final item ownership and location;
- item provenance timeline.

## Context Health

- maximum DM request size;
- maximum Rulebook Resolver request size;
- whether any request approached or exceeded configured limits;
- evidence that resolver requests remain independent of encounter length.

The aggregate experiment report should link or refer to its constituent run folders.

---

# Tests

Add focused automated tests covering the new behaviour.

## Inventory Tests

Test at least:

- successful giving;
- giving consumes a turn;
- giving requires current ownership;
- invalid recipient state is rejected;
- giving an equipped weapon is rejected;
- successful dropping;
- dropping consumes a turn;
- dropped item retains its stable ID;
- dropped item can subsequently be taken;
- successful theft with deterministic RNG;
- failed theft with deterministic RNG;
- theft consumes the turn on success;
- theft consumes the turn on failure;
- theft requires both actors to be active and present;
- theft of an equipped weapon is rejected;
- theft of an unknown hidden item is rejected;
- stale item ownership is rejected;
- failed validation leaves ownership unchanged;
- an item cannot exist in more than one location;
- transfer rollback is atomic;
- public knowledge is delivered only to eligible observers;
- item provenance matches authoritative ownership transitions.

## RNG Tests

Test that:

- the same seed and inputs produce the same theft result;
- exactly one theft-resolution draw occurs;
- no draw occurs for a validation failure;
- no draw occurs for give or drop;
- every draw contains the required trace fields.

## Rulebook Tests

Test at least:

- retrieval returns only a bounded relevant card set;
- the entire rulebook is not sent for a simple request;
- resolver calls are stateless;
- live game state is absent from resolver input;
- private knowledge is absent from resolver input;
- returned rule IDs must exist;
- rule-card versions are validated;
- malformed guidance fails safely;
- unsupported intent becomes a rejection;
- DM tool exposure is narrowed to the candidate action and rejection;
- engine validation still rejects invalid bindings;
- guidance is not accumulated in DM history;
- cache keys are invalidated by rule-card version changes;
- cached guidance contains no live bindings;
- resolver request size does not grow with encounter history.

Use a fake or scripted `IChatClient` for deterministic tests. Unit tests must not require a live Ollama or OpenAI service.

## Comparison Harness Tests

Test at least:

- deterministic seed schedule generation;
- experiment manifest serialization;
- independent character, DM and resolver profiles;
- correct aggregation across several scripted runs;
- failed runs do not prevent later configured runs unless explicitly requested;
- requested and provider-supported parameters remain distinguishable;
- constituent run artifacts are retained;
- aggregate counts agree with the individual run results.

Retain and run the existing test suite.

---

# Demonstration Scenario

Use the established encounter unless a small fixture adjustment is required.

The demonstration should make the new actions plausible by ensuring characters have transferable ordinary inventory items.

Do not force characters to use the actions through orchestration code.

Prompts may make supported actions clear, but characters must autonomously decide what to do.

The completed demonstration evidence should ideally include:

- at least one natural give, drop or steal attempt;
- rulebook guidance for existing combat actions;
- bounded rulebook request sizes across multiple rounds;
- a small repeated experiment using a deterministic seed schedule.

If a single autonomous run does not naturally demonstrate every inventory action, cover the missing cases with scripted tests rather than distorting character behaviour.

---

# Explicitly Out of Scope

Do not add:

- abilities or spells;
- status effects;
- multiple playable rooms;
- persistent campaigns;
- human-controlled characters;
- trading, prices or currency;
- recipient acceptance or refusal mechanics;
- carrying capacity;
- equip or unequip actions;
- equipped-weapon transfer;
- disarming;
- secret theft;
- stealth or detection systems;
- relationship or reputation statistics;
- automatic retaliation;
- a general character skill/stat system;
- a vector database or general RAG platform;
- a rule-authoring UI;
- a full experiment dashboard;
- distributed experiment execution;
- new agent frameworks.

These are potential future releases.

---

# Acceptance Criteria

v0.6 is complete when:

1. Characters can successfully give, drop and attempt to steal ordinary inventory items through natural-language intent.
2. The engine authoritatively validates and atomically resolves all item transfers.
3. Theft uses one reproducible RNG draw and is always publicly noticed.
4. Item ownership remains unique and provenance can be reconstructed.
5. Detailed action rules have been removed from the DM prompt and placed into bounded, versioned rule cards.
6. Every `take_action` request receives a stateless Rulebook Resolver consultation.
7. Rulebook calls contain no live encounter state, histories or private knowledge.
8. The DM receives only relevant guidance and a narrowed engine-tool set.
9. The engine can reject incorrect resolver or DM decisions safely.
10. Rulebook request size remains bounded and does not grow with encounter history.
11. The tracing artifacts show exactly what every model received and returned.
12. A repeatable experiment can run the same scenario with a deterministic seed schedule and independently configured character, DM and resolver profiles.
13. The aggregate report accurately summarizes its constituent runs.
14. Existing behaviour and tests continue to work.
15. The observer UI correctly displays inventory changes and public item-transfer events.
16. The application and artifact metadata identify the release as v0.6.

Stop once this vertical slice is working, tested and traceable. Do not use spare time to begin v0.7 gameplay features.
