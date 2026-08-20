# Models & Monsters v0.9 — Environment as Authoritative State

Build v0.9 as the final planned feature phase before the project is frozen for its accompanying technical write-up.

The goal is to prove that environmental features can participate in the same authoritative state, structured actions, deterministic resolution, character knowledge, narration, tracing, UI, and reporting systems already used for characters, items, abilities, morale, surrender, and escape.

Implement one deliberately bounded vertical slice:

- shared environmental cover;
- exclusive occupancy;
- attacks modified by cover;
- cover intercepting attacks;
- destructible cover;
- explicit entry, exit, and object-damage actions.

Do not build a general physics engine or expand into multiple rooms, campaigns, or human-controlled characters.

Inspect the existing implementation first and fit the work into its current architecture and naming.

---

# Architectural Principles

The engine remains authoritative.

Models may decide to:

- take cover;
- remain behind cover;
- expose themselves by acting;
- leave cover;
- attack the cover protecting another character.

Models do not decide:

- whether cover is available;
- who currently occupies it;
- whether an action breaks cover;
- the hit-chance modifier;
- whether cover intercepted a blow;
- object durability;
- whether cover was destroyed;
- whether an occupant was exposed.

Do not use regexes, keyword lists, phrase matching, verb lists, or other hand-built language parsing to infer environmental interaction.

Expose explicit structured actions and affordances.

Do not add a generic free-text `interact_with_environment` tool that permits the DM to invent mechanical outcomes.

---

# 1. Authoritative Environmental Objects

Extend the room state with typed environmental objects distinct from:

- descriptive room features;
- containers;
- exits;
- dropped-item ground containers.

Do not treat cover as a container merely because existing containers already have state.

The exact domain design should follow existing conventions, but the cover object must represent at least:

```text
Id
Name
Description
State
ProvidesCover
Capacity
CurrentOccupantId
HitChanceModifier
MaximumDurability
CurrentDurability
Armour
```

Required state:

```text
Intact
Damaged
Destroyed
```

Derived state is acceptable where appropriate, but it must serialize clearly into final state and traces.

Avoid designing a universal environmental-object hierarchy beyond what this vertical slice requires. Prefer a clean capability or typed-object design that can later support other environmental features without implementing them now.

---

# 2. Scenario Object

Add one obvious cover object to **The Two Supply Cases** scenario, such as an overturned heavy mill table or substantial mill workbench.

It must be described clearly enough that characters naturally understand it can provide protection.

Seed it with approximately:

```text
Capacity:             1 character
HitChanceModifier:   -20
MaximumDurability:    2
CurrentDurability:    2
Armour:               2
State:                Intact
Occupant:             none
```

Exact naming may be adjusted to fit the scenario, but preserve these initial mechanical values unless testing exposes a clear problem.

The existing flavour-only room features may remain. Convert or replace one appropriate feature with the authoritative cover object rather than silently allowing both a flavour object and a separate mechanical duplicate.

Everyone present can see:

- the cover object;
- whether it is intact or damaged;
- who is behind it;
- when it intercepts a blow;
- when someone leaves it;
- when it is destroyed.

No hidden-information mechanic is required for this object.

---

# 3. Take Cover

Add an explicit structured action:

```text
take_cover
```

It:

- targets one environmental object with the cover capability;
- requires the actor to be living, present, active, and able to act;
- requires the object to be intact or damaged, not destroyed;
- requires spare capacity;
- consumes the actor’s action;
- performs no RNG draw;
- creates an authoritative occupancy relationship;
- applies a public `InCover` status linked to the object;
- is rejected if the actor already occupies that cover;
- is rejected if the object is full;
- is rejected if the object cannot provide cover.

Capacity must be enforced by the engine. Two characters cannot occupy capacity-one cover even if a model describes them squeezing together.

The affordance must disappear when the object is full or destroyed.

Narration should describe the physical action without exposing IDs, numeric modifiers, capacity fields, or internal status machinery.

---

# 4. Remaining in Cover and Leaving It

Cover persists until one of these conditions occurs:

- the occupant explicitly leaves;
- the occupant completes an accepted action marked as breaking cover;
- the cover is destroyed;
- the occupant dies;
- the occupant surrenders;
- the occupant escapes;
- the occupant becomes absent or otherwise leaves active play.

Do not remove cover merely because a model produced an invalid or rejected action.

For actions that break cover:

1. validate that the proposed action can proceed;
2. release cover immediately before resolving that accepted action;
3. trace and publicly narrate the exposure;
4. resolve the action normally.

This prevents a rejected tool call from mutating authoritative state.

## Explicit leave action

Add:

```text
leave_cover
```

A standalone `leave_cover`:

- requires the actor to occupy cover;
- releases the occupancy;
- removes `InCover`;
- consumes the action;
- performs no RNG draw.

Characters do not normally need to spend a separate turn leaving before attacking. An accepted exposed action may automatically vacate cover and then resolve during the same turn.

The cost of cover is therefore:

- one complete action to enter;
- loss of the protection when taking an exposed action.

This avoids trapping weaker models in a repetitive:

```text
take cover → leave cover → attack
```

sequence.

---

# 5. Structured Action Exposure Metadata

Do not determine whether an action breaks cover by inspecting its prose.

Represent this explicitly in structured action metadata or an equivalent deterministic mechanism.

At minimum:

## Actions that break cover

These should expose the actor before resolution:

- `attack_character`;
- `damage_environmental_object`;
- `steal_item`;
- `take_item`;
- opening or inspecting another world object;
- opening an exit;
- escaping;
- Guard Ally;
- Dirty Strike;
- any future action explicitly marked as requiring physical exposure.

## Actions that may retain cover

These may be completed without automatically leaving:

- structured speech;
- private questions;
- `end_turn`;
- `defend`;
- using an item on oneself;
- Healing Prayer on oneself;
- `steady_ally`;
- Rally Grunt;
- making a surrender offer;
- accepting another character’s surrender;
- other actions explicitly marked as not requiring exposure.

If an action targets someone or something outside the cover and its treatment is ambiguous, decide through explicit action metadata and tests—not language parsing.

Do not infer exposure from phrases such as “step out,” “lean around,” or “remain hidden.”

---

# 6. Attacking a Covered Character

When a character attacks an occupant of intact or damaged cover:

1. Calculate the attacker’s effective hit chance using all existing non-cover modifiers.
2. Record this as the pre-cover effective chance.
3. Apply the cover object’s hit-chance modifier.
4. Apply the existing hit-chance clamping rules.
5. Make the ordinary single hit-check RNG draw.

The same roll must distinguish:

## Direct hit

```text
raw roll <= covered effective hit chance
```

The attack hits the character.

Proceed to the existing glancing/solid/critical quality draw and damage resolution.

Cover takes no damage.

## Cover interception

```text
raw roll > covered effective hit chance
and
raw roll <= pre-cover effective hit chance
```

The attack would have hit without cover, but the cover prevented it.

Requirements:

- the occupant takes no damage;
- do not perform an attack-quality draw;
- reduce cover durability by one;
- trace that cover specifically changed the outcome;
- publicly narrate the blow striking or being turned aside by the cover;
- destroy the cover if durability reaches zero.

## Ordinary miss

```text
raw roll > pre-cover effective hit chance
```

The attack would have missed even without cover.

Requirements:

- the occupant takes no damage;
- the cover takes no damage;
- do not perform an attack-quality draw;
- narrate an ordinary miss without falsely crediting the cover.

This causal distinction is essential for tracing and the experiment report.

Cover modifies only the hit check. It does not alter:

- armour;
- damage;
- glancing/solid/critical bands;
- fear directly.

A critical hit can occur only after the attack actually reaches the covered character.

---

# 7. Cover Durability and Destruction

A cover interception reduces durability by one, ignoring object armour.

Update the object’s state:

```text
CurrentDurability == MaximumDurability:
    Intact

0 < CurrentDurability < MaximumDurability:
    Damaged

CurrentDurability == 0:
    Destroyed
```

When cover is destroyed:

- remove the occupant’s `InCover` status;
- clear occupancy;
- publicly expose the occupant;
- remove its `take_cover` affordance;
- stop applying its hit-chance modifier;
- create explicit trace and public knowledge events;
- retain the destroyed object in room and final state.

Do not delete it from the room.

A character exposed by destruction does not lose their next turn and takes no automatic character damage from the destruction in v0.9.

Do not add splinters, falling damage, fire, debris, or other invented effects.

---

# 8. Deliberately Damaging Cover

Add an explicit structured action:

```text
damage_environmental_object
```

It:

- targets one present, non-destroyed environmental object;
- requires the actor to hold a usable weapon;
- consumes the actor’s action;
- breaks the actor’s own cover before resolution;
- does not damage an occupant during the same action;
- performs no hit or quality RNG draw because a stationary room object does not dodge;
- applies deterministic object damage.

Calculate deliberate object damage using:

```text
max(1, weapon damage - object armour)
```

Subtract the result from object durability and clamp at zero.

With the seeded values:

- a longsword may destroy weakened cover quickly;
- lower-damage weapons may require more than one action;
- intercepted attacks may soften the object before deliberate destruction.

Trace:

- actor;
- weapon;
- target object;
- weapon damage;
- object armour;
- resulting object damage;
- durability before and after;
- state transition;
- occupant exposure, if destroyed.

Reject attempts to damage:

- a destroyed object;
- an unknown or absent object;
- an object without the relevant capability;
- the same cover currently occupied by the actor.

The engine may support friendly or hostile destruction. Do not prevent an actor from destroying cover merely because an ally occupies it; record such behaviour as a coherence metric if useful.

---

# 9. Status, Morale, and Other Interactions

`InCover` is a public, object-linked status whose lifetime is governed by the occupancy relationship.

Cover must coexist safely with:

- Defending;
- Guard Ally;
- OffBalance;
- Rallied;
- Scared;
- healing;
- surrender;
- escape;
- death.

Specific requirements:

- Defend may remain active while behind cover.
- Cover and Defend affect different parts of resolution: cover modifies hit chance; Defend reduces damage after a hit.
- Guard Ally breaks the guardian’s cover before applying.
- A covered character who becomes Scared may choose to remain covered, flee, surrender, defend, or act normally.
- Taking cover does not directly change fear.
- A cover interception does not directly change fear.
- A critical hit that reaches a covered character uses the existing v0.8 fear rules.
- Surrender, death, and escape always release occupancy.
- Accepting someone else’s surrender does not automatically remove the accepting character’s cover.

Keep these interactions deterministic and explicitly tested.

---

# 10. Character State and Affordances

Project environmental state to each character using bounded structured facts.

Characters should receive:

- visible environmental object name and description;
- whether it is intact, damaged, or destroyed;
- whether it can currently provide cover;
- who visibly occupies it;
- whether `take_cover`, `leave_cover`, or `damage_environmental_object` is currently available to them.

Do not expose:

- stable object IDs in natural narration;
- raw durability numbers unless existing character-facing design intentionally presents comparable mechanical values;
- internal capability names;
- internal status or action metadata;
- exact hit-chance calculations before an action.

The diagnostic UI and reports may show full authoritative values.

The DM must narrate environmental outcomes only from bounded engine-provided facts.

---

# 11. Rulebook

Add concise rulebook coverage for:

- taking and occupying cover;
- capacity;
- leaving or breaking cover;
- attacks against covered characters;
- the difference between interception and an ordinary miss;
- durability and destruction;
- deliberately damaging environmental objects;
- interaction with Defend, critical hits, surrender, death, and escape.

Keep the new rule cards compact. The whole-rulebook resolver is already expensive.

Do not reopen or replace the completed embedding-retrieval investigation during v0.9.

Preserve its measurements and conclusions honestly. Do not introduce keyword retrieval as a substitute.

The engine remains authoritative over all rulebook guidance.

---

# 12. RNG and Tracing

Use the existing `IRng` abstraction.

Cover must not add an unnecessary RNG draw:

- `take_cover`: no draw;
- `leave_cover`: no draw;
- `damage_environmental_object`: no draw;
- attack against cover: use the existing attack hit-check draw;
- quality draw occurs only when the character is actually hit.

Ensure every RNG draw continues to record its seed/state, purpose, candidates, raw roll, modifiers, effective threshold, and result.

For a covered attack, the trace must include:

- pre-cover effective hit chance;
- cover object and modifier;
- covered effective hit chance;
- raw roll;
- direct hit, interception, or ordinary miss;
- whether a quality draw followed;
- cover durability effect;
- destruction and exposure, if applicable.

Replays from a fixed run seed must reproduce all engine outcomes. Continue reporting when a model provider does not support seeded generation so engine replay is not confused with guaranteed model-output replay.

---

# 13. Web UI

Extend the current UI without redesigning it.

Display environmental objects in a compact room/environment section showing:

- name;
- current state;
- durability;
- cover modifier;
- capacity;
- current occupant;
- destroyed state.

Character cards should display `InCover` and identify the cover object in readable form.

The transcript should distinguish:

- entering cover;
- voluntarily leaving cover;
- exposure caused by another action;
- an ordinary miss;
- a miss specifically caused by cover;
- cover damage;
- cover destruction;
- an occupant becoming exposed;
- deliberate attacks on environmental objects.

Keep authoritative mechanics readable but secondary to the narrative.

The UI must continue to render older runs that do not contain environmental objects.

---

# 14. Run Report

Add a dedicated environmental-state section.

Include:

## Environmental object summary

- initial and final state;
- maximum and final durability;
- capacity;
- final occupant;
- whether destroyed;
- who destroyed it.

## Occupancy timeline

For every entry and exit:

- round and turn;
- character;
- object;
- transition;
- cause: entered, voluntarily left, exposed by action, destroyed, surrendered, escaped, died, or became absent.

## Cover effectiveness

- attacks made against covered targets;
- direct hits despite cover;
- cover interceptions;
- ordinary misses;
- damage prevented;
- durability lost through interceptions;
- attacks where cover changed the outcome.

Do not claim that all damage on an intercepted attack was “prevented” unless the engine can calculate this honestly. At minimum, report that the hit itself was prevented.

## Object damage

- deliberate damage attempts;
- actor and weapon;
- damage applied;
- durability before and after;
- destruction and occupant exposure.

Preserve all existing sections for:

- morale;
- critical hits;
- intimidation;
- abilities;
- surrender;
- inventory;
- knowledge;
- grounding;
- RNG;
- rulebook efficiency;
- context health;
- final state.

---

# 15. Tests

Add deterministic tests covering at least:

## Environmental state

- scenario seeds one valid cover object;
- older scenario/run state without environmental objects remains readable;
- cover serializes into final state;
- destroyed cover remains present;
- capacity and occupancy are authoritative.

## Taking cover

- valid entry consumes the action;
- applies `InCover`;
- records occupancy;
- no RNG draw occurs;
- a second character is refused when capacity is full;
- destroyed cover cannot be occupied;
- non-cover objects cannot be occupied;
- affordances reflect availability.

## Leaving and exposure

- explicit leave releases occupancy and consumes the action;
- an accepted exposed action releases cover before resolution;
- a rejected action does not release cover;
- death releases cover;
- surrender releases cover;
- escape releases cover;
- absence releases cover;
- accepting another character’s surrender does not release cover unless independently marked as exposing.

## Covered attacks

Test exact boundaries for:

- direct hit;
- cover interception;
- ordinary miss;
- modifier ordering;
- hit-chance clamping;
- no quality draw after interception;
- no quality draw after ordinary miss;
- quality draw after direct hit;
- cover taking durability only on interception;
- direct critical hit against a covered target;
- Defend reducing damage after a direct hit.

## Destruction

- interception damages cover;
- damaged state is represented;
- zero durability destroys it;
- destruction exposes the occupant;
- destroyed cover gives no modifier;
- destroyed cover cannot be reused;
- destruction creates public facts and trace events.

## Deliberate object damage

- requires a held weapon;
- uses weapon damage minus object armour;
- minimum damage is one;
- performs no RNG draw;
- cannot also damage the occupant;
- breaks the attacker’s own cover;
- rejects destroyed or unsupported targets;
- rejects attacking the cover currently occupied by the actor.

## Language and grounding

- no prose parsing is used to infer cover actions;
- structured target IDs remain authoritative;
- narration receives only bounded outcome facts;
- object state changes are public knowledge;
- internal IDs and machinery wording do not leak into ordinary narration.

## Regression

- attack, armour, glancing, solid, and critical calculations remain correct without cover;
- fear behaviour remains unchanged;
- Defend remains correct;
- Guard Ally remains correct;
- surrender and escape remain correct;
- containers, doors, inventory, and object knowledge remain correct;
- all existing tests continue to pass.

Unit and integration tests must not require live Ollama, OpenAI, Anthropic, or embedding services.

---

# 16. Scope Exclusions

Do not add:

- more than one room;
- movement grids, distances, zones, or coordinates;
- persistent campaigns;
- human-controlled characters;
- fire;
- darkness mechanics;
- traps;
- falling objects;
- pushing or forced movement;
- area effects;
- ranged-weapon systems;
- environmental damage to characters;
- general-purpose physics;
- another retrieval or embedding experiment;
- language parsing;
- new agent frameworks.

These belong to possible later work, not the final planned phase.

---

# 17. Documentation and Versioning

Update:

- application version to `0.9.0.0`;
- README feature list to mark environmental objects complete;
- architecture documentation where needed;
- rulebook documentation;
- configuration examples;
- run-report documentation.

Document the central design principle:

> Environmental features are authoritative state with explicit affordances and deterministic consequences, not decorative prose that the Dungeon Master may reinterpret freely.

Briefly document why taking cover costs an action but an exposed attack may vacate it without requiring a separate empty turn.

Preserve the completed rulebook-efficiency findings, including the unsuccessful embedding approach, rather than presenting it as a production success.

---

# 18. Completion Criteria

The work is complete when:

- the solution builds cleanly;
- all existing and new tests pass;
- the scenario contains one authoritative cover object;
- characters can enter, retain, and leave cover;
- exclusive occupancy is enforced;
- cover modifies hit chance;
- hit, interception, and ordinary miss are distinguished correctly;
- cover can be damaged and destroyed;
- destruction exposes its occupant;
- state, knowledge, narration, tracing, UI, and reports agree;
- no language-parsing heuristics have been introduced;
- older run data remains usable;
- application and documentation identify the release as v0.9.

Perform at least one bounded smoke run with a capable hosted model if configuration permits. Do not repeatedly tune prompts to force environmental interaction in a random run; deterministic tests must prove the mechanic even if a particular group of agents chooses not to use the cover.

At handoff, report:

- implemented behaviour;
- important architectural decisions;
- test count and result;
- smoke-run outcome;
- whether cover was used naturally;
- any remaining risks;
- anything intentionally deferred.

Do not continue into the later-phase backlog after completing this work.
