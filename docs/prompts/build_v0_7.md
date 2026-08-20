# Models & Monsters v0.7 — Terms & Tactics

Extend the existing **Models & Monsters** implementation to v0.7.

Preserve established architecture and working v0.6 behaviour unless this prompt explicitly changes it.

v0.7 has four principal goals:

1. Replace unilateral surrender with negotiated, costly surrender.
2. Add a controlled persuasion and intimidation vertical slice through surrender negotiations.
3. Add a small abilities and spells vertical slice.
4. Add authoritative, mechanically meaningful status effects.

This release must also fix the state-grounding and action-protocol weaknesses exposed by the GPT-4.1 mini and Qwen runs.

Do not introduce multiple rooms or persistent campaigns in this release.

---

# Existing Principles

Continue to enforce:

- The game engine is the authoritative source of truth.
- Models decide intent but cannot mutate state directly.
- The Rulebook Resolver interprets intent against bounded rules.
- The DM binds rule guidance to current permitted state.
- The engine validates and resolves every state-changing action.
- Every character owns a separate conversation history.
- Hidden information and character-specific knowledge remain enforced.
- Public speech remains distinct from authoritative fact.
- Orchestration remains explicit in our own code.
- All model requests and responses are traced to files.
- `end_turn` remains a valid deliberate null action.
- Stable IDs are used for actors, items, abilities, effects, agreements and objects.
- The observer UI is a projection of authoritative state.
- Provider-specific unsupported model options remain explicitly traced.
- The Rulebook Resolver remains a separate stateless model call.

Continue using:

- .NET 10
- `Microsoft.Extensions.AI`
- OllamaSharp for Ollama-hosted models
- the existing OpenAI and Anthropic integrations
- the existing application and test-project structure
- the existing deterministic RNG abstraction
- the existing run/report infrastructure

Inspect the current implementation first and adapt to its established patterns. Do not replace working architecture unnecessarily.

Set application and artifact version metadata to `0.7.0.0`.

---

# Part One — Negotiated Surrender

Remove the existing unilateral surrender behaviour.

A character must not make itself immediately untargetable simply by declaring surrender.

Surrender now requires:

1. A concrete offer from the surrendering character.
2. Immediate, enforceable consideration.
3. Acceptance by a named opponent.
4. Atomic enforcement of the accepted terms.

The high-level state flow is:

```text
Active
  → makes pending surrender offer
  → remains Active and targetable
  → recipient accepts, ignores or rejects through their actions
      → accepted: terms execute and character becomes Surrendered
      → ignored/rejected/expired: character remains Active
```

---

# Surrender Offer

Add an authoritative action conceptually equivalent to:

```text
offer_surrender(
    offerer_id,
    recipient_id,
    offered_item_ids,
    forfeit_weapon)
```

The exact API shape may follow existing conventions.

Validation:

- The offerer is the current active actor.
- The offerer is alive and present.
- The recipient is an active, living, present opponent.
- The offerer and recipient are on opposing teams.
- Every offered item is currently owned by the offerer.
- Every offered item is transferable.
- Stable item IDs are used.
- No item is duplicated or transferred when the offer is merely created.
- The offer contains at least one immediate concession.

Valid concessions are:

- one or more coin purses;
- one or more ordinary inventory items;
- forfeiture of the equipped weapon;
- or a combination.

An empty offer such as “please spare me” is not a supported surrender offer.

Offering surrender consumes the offerer’s turn.

Creating an offer does not:

- transfer any assets;
- disarm the offerer yet;
- change the offerer’s disposition;
- prevent attacks;
- guarantee acceptance.

The offerer remains Active and targetable while the offer is pending.

Only one pending surrender offer may exist per offerer.

---

# Pending Surrender Offer

Add an authoritative entity similar to:

```text
SurrenderOffer
- Id
- OffererId
- RecipientId
- OfferedItemIds
- ForfeitWeapon
- CreatedRound
- CreatedTurn
- ExpiresAfterRecipientTurn
- AssociatedSpeechEventId
- State
```

Suggested states:

```text
Pending
Accepted
Rejected
Expired
Invalidated
```

The most recent public speech made by the offerer during the same turn may be associated with the offer. The speech supplies the plea, argument or threat; the structured offer supplies the enforceable terms.

The speech is not itself authoritative contract state.

The offer and its terms are public facts visible to eligible characters present in the room.

---

# Accepting Surrender

Add an authoritative action conceptually equivalent to:

```text
accept_surrender(recipient_id, offer_id)
```

Validation:

- The recipient is the current actor.
- The recipient is the named recipient of the offer.
- The recipient is active, living and present.
- The offerer remains active, living and present.
- The offer remains Pending.
- Every promised item remains owned by the offerer.
- The promised weapon remains equipped by the offerer if weapon forfeiture was offered.
- The complete agreement can be applied atomically.

Acceptance consumes the recipient’s turn.

On successful acceptance:

1. Transfer all promised inventory items atomically to the recipient.
2. Disarm the offerer.
3. If weapon forfeiture was promised, move the weapon to an authoritative room-ground location.
4. Mark the offer Accepted.
5. Create a durable `SurrenderAgreement`.
6. Change the offerer’s disposition to `Surrendered`.
7. Remove or expire incompatible combat statuses.
8. Re-evaluate the encounter outcome.

The surrendered character is:

- alive;
- still present;
- unable to take turns;
- no longer a valid combat target;
- still able to observe eligible public events.

No partial acceptance is allowed.

If any promised asset is unavailable when acceptance is attempted, reject or invalidate the offer without transferring anything.

---

# Weapon Forfeiture

The current weapon model may require a small authoritative extension.

Every equipped weapon involved in surrender must have a stable identity.

On accepted weapon forfeiture:

- the weapon ceases to be equipped;
- the surrendering character becomes disarmed;
- the weapon moves to room-ground ownership;
- item/asset provenance records the movement;
- no duplicate weapon entity is created.

The weapon may be represented as an inert ground item that can be taken as loot.

Do not implement equipping, re-equipping, disarming attacks or general weapon swapping in v0.7.

A looted weapon remains an inventory trophy rather than becoming an automatically usable equipped weapon.

---

# Rejection and Expiry

Do not require a separate rejection action.

The recipient may:

- publicly reject through `say`;
- take another action;
- attack the offerer;
- ignore the offer.

Any hostile action against the offerer by the recipient or their team rejects the offer.

If the recipient completes their next turn without accepting, the offer expires.

If the recipient dies, escapes, surrenders or becomes unable to respond before acceptance, invalidate the offer.

If the offerer dies, escapes or otherwise leaves active play, invalidate the offer.

Rejection, expiry and invalidation transfer nothing.

The offerer may make a better offer on a later turn.

Do not implement formal counteroffers in v0.7. A recipient may demand better terms through speech, reject the current offer, and wait for the offerer to propose new terms on a later turn.

This keeps negotiation bounded and avoids recursive contract loops.

---

# Surrender Agreement

Create an authoritative record similar to:

```text
SurrenderAgreement
- Id
- OfferId
- OffererId
- AcceptedById
- TransferredItemIds
- ForfeitedWeaponId
- AcceptedRound
- AcceptedTurn
- AssociatedSpeechEventId
```

The agreement is historical evidence and report data. Authoritative ownership remains in game state.

The agreement must make it possible to answer:

- who surrendered;
- who accepted;
- what was offered;
- what was transferred;
- whether a weapon was forfeited;
- what speech accompanied the offer;
- when the agreement occurred.

Do not implement:

- breaking an accepted surrender;
- treachery after acceptance;
- prisoners;
- ransom payable later;
- future service;
- employment;
- promises concerning future rooms;
- secret surrender agreements.

---

# Persuasion and Intimidation Vertical Slice

Persuasion and intimidation become meaningful through surrender negotiation.

Do not add generic `persuade` or `intimidate` engine actions.

Do not introduce social statistics or a generic social RNG roll.

The flow is:

```text
Public plea, argument, promise or threat
    → structured surrender offer
    → recipient evaluates the argument and concrete terms
    → recipient autonomously accepts or continues acting
```

The recipient model should receive:

- the exact public speech;
- the concrete offered assets;
- visible battle state;
- its own personality, goals and fears;
- its own permitted knowledge;
- the fact that acceptance removes an opponent without further combat risk.

The recipient must not receive private reasoning or hidden information.

The engine does not decide whether the terms are “good enough.” The recipient model decides, while the engine enforces only the concrete result.

This counts as the v0.7 persuasion/intimidation vertical slice because the social attempt now has:

- a specific recipient;
- a concrete requested outcome;
- enforceable stakes;
- an autonomous response;
- a durable result.

Do not generalise persuasion to arbitrary world changes.

---

# Part Two — Abilities and Spells

Introduce a small data-driven ability model.

Suggested domain concepts:

```text
AbilityDefinition
CharacterAbility
AbilityUseResult
AbilityEffectKind
```

Each ability should define:

- stable ID;
- name;
- category;
- natural-language description;
- target rule;
- maximum uses per encounter, where applicable;
- remaining uses;
- effect kind;
- effect value;
- whether normal attack RNG is involved;
- visibility;
- associated rule ID.

Ability categories may include:

```text
Technique
Spell
Command
Trick
BasicAction
```

Do not create a general scripting language or arbitrary model-defined effects.

Use concrete engine handlers for the small supported effect set.

Expose a generic engine action conceptually equivalent to:

```text
use_ability(actor_id, ability_id, target_id)
```

A separate `defend` action is acceptable if it fits the existing engine more cleanly.

The Rulebook Resolver identifies the intended ability category. The DM binds it against the actor’s authoritative ability list.

---

# Rowan — Guard Ally

Add a repeatable technique:

```text
Ability ID: guard-ally
Name: Guard Ally
Category: Technique
Uses: unlimited
```

Guard Ally:

- consumes Rowan’s turn;
- targets one other active, living, present ally;
- cannot target Rowan;
- creates a linked Guarding/Guarded relationship;
- lasts until the start of Rowan’s next turn;
- ends after redirecting one eligible attack;
- is public and observable.

When an enemy attacks the guarded ally:

1. Validate that Rowan is still active, alive and present.
2. Redirect the attack to Rowan.
3. Use the attacker’s ordinary attack rules.
4. Resolve the normal hit and glancing RNG exactly once.
5. Use Rowan’s armour, health and statuses.
6. Consume the guard relationship.
7. Trace both the intended target and authoritative redirected target.

Do not make a second attack roll.

Guard Ally does not stack.

Only one guard may protect a given target, and a guarding character may guard only one ally at a time.

If Rowan dies, surrenders, escapes or becomes unable to act, remove the guard relationship.

Guard Ally is deliberately repeatable. Its cost is Rowan’s full turn and the risk of taking the redirected attack.

---

# Elara — Healing Prayer

Add a limited spell:

```text
Ability ID: healing-prayer
Name: Healing Prayer
Category: Spell
Uses: once per encounter
Healing: 4
```

Healing Prayer:

- consumes Elara’s turn;
- targets Elara or one active, living, present ally;
- uses no RNG;
- restores a fixed four health;
- cannot exceed maximum health;
- cannot revive a dead character;
- cannot target an escaped character;
- is public and observable.

Reject use on a fully healthy target.

Do not consume the charge if validation fails.

Consume the charge when the accepted ability resolves.

Do not introduce mana, spell slots or spell preparation.

---

# Vark — Rally Grunt

Add a limited command ability:

```text
Ability ID: rally-grunt
Name: Rally Grunt
Category: Command
Uses: once per encounter
Modifier: +15 hit chance
```

Rally Grunt:

- consumes Vark’s turn;
- targets one other active, living, present ally;
- cannot target Vark;
- creates `Rallied`;
- is public and observable;
- uses no RNG when applied.

`Rallied`:

- adds 15 to the target’s next attack hit chance;
- is consumed by the next attack whether it hits or misses;
- expires at the end of the target’s next turn if unused;
- identifies Vark as its source;
- cannot stack with itself.

Do not consume the ability charge if validation fails.

---

# Skrit — Dirty Strike

Add a limited trick:

```text
Ability ID: dirty-strike
Name: Dirty Strike
Category: Trick
Uses: once per encounter
Effect: OffBalance
```

Dirty Strike:

- consumes Skrit’s turn;
- targets an active, living, present opponent;
- performs one normal weapon attack;
- uses the normal attack and glancing RNG;
- applies normal damage;
- applies `OffBalance` only if the attack hits;
- consumes the ability charge whether the accepted attack hits or misses;
- is public and observable.

`OffBalance`:

- subtracts 15 from the target’s next attack hit chance;
- is consumed by that next attack whether it hits or misses;
- expires at the end of the target’s next turn if unused;
- identifies Skrit as its source;
- cannot stack with itself.

Dirty Strike requires no additional RNG draw beyond normal attack resolution.

---

# Defend

Add a repeatable basic combat action:

```text
defend(actor_id)
```

Defend is available to every active character.

It:

- consumes the actor’s turn;
- applies `Defending`;
- uses no RNG;
- is public and observable;
- gives legitimate meaning to intentions such as:
  - “I brace myself.”
  - “I stand my ground.”
  - “I prepare to parry.”
  - “I keep my guard up.”

`Defending`:

- reduces final damage from the next successful incoming attack by one;
- applies after armour and glancing calculations;
- cannot reduce final damage below zero;
- is consumed by the next successful incoming attack;
- remains if an attack misses;
- expires at the start of the defender’s next turn;
- cannot stack with itself.

Repeated Defend actions are allowed, but each consumes the complete turn.

---

# Part Three — Status Effects

Add authoritative status-effect instances.

Suggested representation:

```text
StatusEffectInstance
- Id
- Kind
- SourceCharacterId
- TargetCharacterId
- AppliedRound
- AppliedTurn
- Modifier
- ExpiryRule
- Consumed
- Visibility
- RelationshipId
```

Initial supported status kinds:

```text
Guarding
Guarded
Rallied
OffBalance
Defending
```

Status effects must:

- use stable IDs;
- be authoritative engine state;
- have exact expiry semantics;
- identify their source;
- be visible in projected character state when appropriate;
- be removed deterministically;
- be included in final state and reports;
- never be invented by narration.

Trace events should include:

```text
StatusApplied
StatusConsumed
StatusExpired
StatusRemoved
AttackRedirected
```

Remove incompatible combat statuses when a character:

- dies;
- surrenders;
- escapes;
- leaves the encounter;
- loses the source required to sustain a linked effect.

Do not implement:

- poison;
- bleeding damage over time;
- paralysis;
- stunned turns;
- arbitrary stacking;
- arbitrary model-created statuses;
- status resistance;
- cleansing;
- area effects.

---

# RNG and Modifiers

Status and ability modifiers must feed into the existing authoritative RNG pipeline.

Example:

```text
Purpose: attack.hit-check
Base hit chance: 70
Rallied: +15
OffBalance: -15
Effective hit chance: 70
Raw roll: 63
Result: hit
```

Every modifier must include:

- status or ability ID;
- source character;
- signed value;
- application order;
- whether the modifier was consumed.

Continue to enforce:

> Ensure every RNG draw records its seed/state, purpose, candidates, raw roll, modifiers, and result. Otherwise the new hit chance will make behavioural comparisons much harder to reproduce.

Guard redirection must not add RNG draws.

Dirty Strike must use ordinary attack draws rather than creating a parallel combat RNG path.

---

# Part Four — Grounded Dungeon Master Answers

Recent weaker-model runs exposed serious boundary problems:

- the DM said unsupported tactical actions were possible;
- the DM revealed or inferred private container contents;
- the DM incorrectly said consumed items were still carried;
- characters trusted those answers and entered retry loops.

Introduce a deterministic grounding stage for character questions.

The flow becomes:

```text
Character question
    → deterministic AnswerFacts projection
    → only current permitted facts and supported affordances
    → DM rephrases AnswerFacts naturally
    → answer delivered to character
```

Suggested abstraction:

```text
IAnswerFactsProjector
AnswerFacts
```

The DM must not receive the entire authoritative state for question answering.

The projection should contain only:

- the asking character’s current permitted knowledge;
- current public facts;
- directly observable current state;
- supported affordances relevant to the question;
- current ownership and item existence where permitted;
- current ability availability;
- current visible status effects;
- explicit statements when the engine has no spatial representation.

Examples:

- If a potion was consumed, it no longer exists as a carried item.
- If the asker never learned a container’s contents, those contents are absent.
- If a character has Guard Ally, the answer may explain that they can spend their turn protecting an ally.
- If no supported guard ability exists, the DM must not promise positional shielding.
- If the world has no detailed distance model, the answer must not invent tactical spacing.

The DM’s job is rephrasing, not adding facts.

Trace:

- original question;
- projected facts;
- omitted hidden facts;
- model request;
- raw answer;
- delivered answer.

Do not attempt to build a general natural-language theorem prover.

---

# Knowledge Validation

Engine validation must independently enforce informational prerequisites.

At minimum:

- `take_item` requires the actor to have a legitimate basis to identify the item and its location.
- `steal_item` requires the actor to know or reasonably believe the target carries the item.
- Surrender offers may include only items the offerer actually owns.
- Acceptance may transfer only items still owned by the offerer.
- Hidden container contents cannot be targeted by name merely because the DM knows them.
- Historical ownership facts cannot override current ownership.
- Consumed items cannot be targeted.

Model history is not authoritative knowledge.

A mistaken DM answer must not be sufficient to bypass engine validation.

---

# Post-Resolution Output

Once a character’s turn-consuming action is accepted and resolved:

- stop processing further actions for that turn;
- discard remaining model tool calls or action text;
- do not place discarded action declarations in the public transcript;
- do not create knowledge facts from discarded output;
- trace the discarded content as `PostResolutionOutputDiscarded`.

Speech emitted before the accepted action remains valid under the existing one-speech-per-turn rule.

This prevents a model from attacking and then emitting an unprocessed theft declaration as though it acted twice.

---

# Rulebook Changes

Replace the old surrender rule card with versioned cards for:

```text
encounter.offer-surrender
encounter.accept-surrender
combat.defend
ability.guard-ally
ability.healing-prayer
ability.rally-grunt
ability.dirty-strike
```

Retain and version existing action cards as appropriate.

The Rulebook Resolver remains a genuine stateless model call.

Continue sending the complete rulebook for now.

Do not reintroduce:

- keyword eligibility;
- intent-tag routing;
- family bonuses;
- fixed fallback candidate sets;
- deterministic semantic classification.

Increase the configured hard limits to accommodate the expanded book, for example:

```text
RulebookMaxCards: 32
RulebookMaxInputChars: 32000
```

Use measured values appropriate to the final cards.

If the complete book exceeds the hard limit:

- fail visibly;
- report the size problem;
- do not silently trim actions.

Do not introduce embeddings or vector retrieval in v0.7.

Keep cards concise and trace:

- complete card count;
- card characters and tokens;
- cited rules;
- consultation latency;
- resolver input and output tokens;
- whether request size remained constant across rounds.

---

# Scenario Changes

Continue using the flooded-cellar scenario.

Every character already carries a distinct stable-ID coin purse. Preserve that setup.

Add the new abilities:

```text
Rowan: Guard Ally
Elara: Healing Prayer
Vark: Rally Grunt
Skrit: Dirty Strike
Everyone: Defend
```

Update character descriptions so they understand their abilities in natural in-world language.

Do not dictate when they must use them.

Make surrender terms and abilities known as available possibilities without forcing their use.

Character motivations should support autonomous evaluation:

- Goblins value gold, survival and avoiding further wounds.
- Rowan values protecting Elara and may accept a disarmed surrender.
- Elara values mercy and preserving life.
- Vark values his survival more than pride when genuinely cornered.
- Skrit values gold but fears isolation.

Do not hard-code surrender decisions in orchestration.

---

# Observer UI

Update the observer UI to display:

- pending surrender offers;
- offered items and coin purses;
- named recipient;
- offer expiry;
- accepted surrender agreements;
- weapon forfeiture;
- ability names and remaining uses;
- current status effects;
- Guarding/Guarded relationships;
- status application and expiry;
- attack redirection;
- Defending damage reduction.

Keep the primary transcript readable.

Detailed rulebook internals remain in diagnostic artifacts rather than the gameplay transcript.

Surrendered characters should visually show:

- surrendered disposition;
- disarmed state;
- transferred tribute;
- inability to act.

---

# Reports

Extend per-run and comparison reports.

## Surrender Negotiation

Report:

- offers made;
- offerer and recipient;
- offered assets;
- associated speech;
- accepted, rejected, expired or invalidated result;
- response time in turns;
- accepted tribute;
- weapon disposition;
- final surrender agreements.

## Persuasion and Intimidation

Report descriptively:

- speech associated with offers;
- accepted versus unaccepted offers;
- visible battle state when the offer was made;
- offered asset value where available.

Do not claim that speech caused acceptance or report statistical significance from one run.

## Ability Activity

Report:

- ability uses attempted;
- accepted and rejected;
- remaining charges;
- targets;
- outcomes;
- healing performed;
- guard redirections;
- Rally modifiers consumed;
- Dirty Strike hits and OffBalance applications;
- Defend uses and prevented damage.

## Status Timeline

Report:

- application;
- source;
- target;
- duration;
- consumption;
- expiry;
- modifier applied;
- affected RNG trace ID.

## State-Grounding Health

Report:

- character questions;
- AnswerFacts projections;
- factual-answer corrections or failures where detectable;
- engine rejections caused by stale or unknown item references;
- post-resolution output discarded;
- action-limit terminations.

## Item Provenance

Distinguish:

- ordinary giving;
- dropping;
- theft;
- corpse looting;
- surrender tribute;
- weapon forfeiture.

Show stable IDs for identically named coin purses.

---

# Tests

Retain and run the complete existing test suite.

Add focused tests for the following.

## Surrender Tests

Test:

- empty surrender offer is rejected;
- offer requires an active opponent recipient;
- offer requires currently owned assets;
- creating an offer transfers nothing;
- creating an offer consumes the turn;
- offerer remains active and targetable;
- accepted offer transfers all items atomically;
- accepted offer disarms the offerer;
- accepted offer creates a durable agreement;
- accepted offer changes disposition to Surrendered;
- acceptance consumes the recipient’s turn;
- stale offered ownership invalidates acceptance;
- failed acceptance transfers nothing;
- hostile action rejects the offer;
- ignored offer expires after the recipient’s turn;
- dead or escaped recipient invalidates the offer;
- dead or escaped offerer invalidates the offer;
- only the named recipient can accept;
- duplicate pending offers are prevented;
- public knowledge is delivered only to eligible observers;
- item and weapon provenance is complete;
- accepted surrender affects team outcome correctly;
- no RNG is used for offer or acceptance.

## Ability Tests

Test:

- Guard Ally is repeatable;
- Guard Ally consumes the turn;
- Guard Ally cannot target self;
- Guard Ally requires an eligible ally;
- the first eligible attack redirects to the guardian;
- redirected attack uses exactly the ordinary RNG draws;
- redirected attack uses guardian armour and health;
- guard is consumed after one redirection;
- unused guard expires at the start of the guardian’s next turn;
- guard disappears if either participant becomes ineligible;
- Healing Prayer works once per encounter;
- Healing Prayer does not revive;
- Healing Prayer rejects a fully healthy target without consuming its charge;
- Rally applies +15 to the next attack;
- Rally is consumed on hit or miss;
- Rally expires after the target’s next turn if unused;
- Dirty Strike uses ordinary attack RNG;
- Dirty Strike applies OffBalance only on a hit;
- Dirty Strike consumes its charge on an accepted miss;
- OffBalance modifies and is consumed by the next attack;
- Defend is repeatable;
- Defend reduces final damage in the specified order;
- Defend remains after a miss;
- Defend expires at the start of the actor’s next turn;
- status effects do not stack with themselves.

## RNG Tests

Test:

- status modifiers appear in draw traces;
- modifier order is deterministic;
- effective chance is reproducible;
- Guard redirection creates no extra draw;
- Dirty Strike creates no additional status roll;
- no RNG occurs for healing, guarding, rallying, defending or surrender negotiation;
- all required seed/state and result fields remain present.

## Knowledge and Answer Tests

Test:

- consumed items are absent from current AnswerFacts;
- current ownership supersedes historical ownership;
- hidden container contents are absent;
- public facts remain available;
- unsupported positional mechanics are not promised;
- available Guard Ally affordance may be explained;
- DM answer requests contain no forbidden hidden state;
- `take_item` rejects an unknown item;
- `steal_item` rejects an item without informational basis;
- a mistaken scripted DM answer cannot bypass engine knowledge validation.

## Protocol Tests

Test:

- output after an accepted action is discarded;
- discarded output creates no state or knowledge events;
- discarded output does not appear in the gameplay transcript;
- associated speech before the action remains;
- the trace records discarded content.

## Rulebook Tests

Test:

- every rule card is supplied to the resolver;
- no keyword match determines rule eligibility;
- indirect paraphrases select the correct ability and surrender rules;
- defensive posturing maps to Defend rather than Attack;
- negotiation speech alone does not execute surrender;
- an offer with concrete terms maps to `offer_surrender`;
- acceptance maps to `accept_surrender`;
- compound unsupported actions are handled safely;
- the complete rulebook remains within the configured hard limit;
- exceeding the limit fails visibly rather than trimming;
- resolver context remains independent of encounter length.

Use fake or scripted `IChatClient` instances. Tests must not require live model services.

---

# Demonstration Expectations

Run at least one autonomous demonstration after tests pass.

Do not force particular actions through orchestration.

The demonstration should make it plausible for models to:

- guard an ally;
- heal or defend;
- use a limited ability;
- make a surrender offer when badly wounded;
- evaluate an opponent’s offer;
- loot a corpse if someone dies.

If autonomous behaviour does not exercise every feature, use scripted integration tests for missing branches rather than distorting character prompts.

Run at least one weaker-model configuration, such as GPT-4.1 mini or Qwen 3.5 9B, to verify that engine boundaries remain safe when model judgement is imperfect.

---

# Explicitly Out of Scope

Do not add:

- multiple rooms;
- persistent campaigns;
- human-controlled characters;
- arbitrary persuasion targets;
- generic persuasion/intimidation statistics;
- social RNG rolls;
- formal counteroffers;
- future promises;
- prisoners;
- ransom payable later;
- betrayal after accepted surrender;
- secret agreements;
- general equipping or weapon swapping;
- disarming attacks;
- spell slots;
- mana;
- arbitrary spell creation;
- arbitrary status creation;
- damage-over-time statuses;
- paralysis or stunned turns;
- area-of-effect abilities;
- vector retrieval;
- embeddings;
- a vector database;
- a new agent framework.

---

# Acceptance Criteria

v0.7 is complete when:

1. Surrender requires a concrete, enforceable offer.
2. An offerer remains active and targetable until acceptance.
3. Only the named opponent can accept.
4. Acceptance atomically transfers tribute and disarms the surrenderer.
5. Accepted surrender creates a durable agreement.
6. Rejected, ignored or stale offers transfer nothing.
7. Persuasion and intimidation can influence autonomous acceptance through public speech and concrete terms.
8. Guard Ally is repeatable and redirects exactly one eligible attack.
9. Healing Prayer, Rally Grunt and Dirty Strike enforce their usage limits.
10. Defend provides a valid repeatable defensive action.
11. Status effects apply, modify, consume and expire deterministically.
12. All RNG modifiers are fully reproducible and traced.
13. Character questions are answered from grounded permitted facts.
14. Hidden or stale information cannot bypass engine validation.
15. Extra actions after turn resolution are discarded and traced.
16. The Rulebook Resolver sees the complete expanded rulebook without keyword routing.
17. Resolver request size remains bounded and independent of encounter length.
18. Reports and UI expose negotiation, abilities, statuses and provenance.
19. Existing tests continue to pass.
20. The application and artifacts identify the release as v0.7.

Stop when this bounded vertical slice is working, tested, traceable and demonstrated. Do not begin the multiple-room work intended for a later release.
