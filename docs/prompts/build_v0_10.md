# Models & Monsters — Final Action-Set Closure Batch

Complete one final, tightly scoped batch before freezing the project for its technical write-up.

This work should close the most misleading gaps found in `reports/action-set-gaps.md` without turning the demo into a general RPG system.

Implement:

1. weapon-backed surrender through the existing offer/accept protocol;
2. reliable access to existing environmental-object destruction;
3. truthful and minimally useful handling of forfeited weapons;
4. clearer guidance around compound deeds, demands, and non-positional movement.

Do not add throwing, disarming, forced movement, damageable containers, weapon equipping, or any other new gameplay family.

Inspect the current implementation first and adapt these requirements to its existing architecture and terminology.

---

# 1. Preserve the Bilateral Surrender Design

Do **not** add a unilateral `yield` action that immediately grants the protected `Surrendered` disposition.

A character must not be able to become untargetable without giving the named opponent a chance to accept or refuse.

Extend the existing `offer_surrender` action instead.

A valid surrender offer must contain at least one concrete concession:

```text
one or more carried items
OR
forfeitWeapon = true
OR
both
```

An empty item list is therefore valid when `forfeitWeapon=true`.

An offer with:

```text
items = []
forfeitWeapon = false
```

must remain invalid because it offers no concession.

## Weapon-only surrender

Support intents such as:

> “I hold my sabre out by the flat and offer to lay it down if you spare me.”

This should create a pending surrender offer with weapon forfeiture.

Before acceptance:

- the weapon remains equipped;
- no item or weapon moves;
- the offerer remains active;
- the offerer remains targetable;
- the offerer continues taking turns;
- only the named recipient may accept;
- the ordinary expiry rules remain in force.

Narration must describe this as an offer or visible gesture of intended submission, not falsely claim that the weapon has already moved.

On acceptance:

- the existing surrender transition occurs;
- the weapon is forfeited;
- the weapon is placed on the room floor;
- the surrenderer becomes disarmed;
- the surrenderer leaves active combat;
- the accepting character receives any promised items;
- the agreement and provenance records are durable.

## Item-backed surrender

Improve the existing structured action guidance so intents such as:

> “I offer Rowan the goblin salve in exchange for my life.”

naturally map to `offer_surrender`, not `give_item`, ordinary speech, or `DmImpossible`.

Do this through clear tool descriptions, structured affordances, prompts, and concise rulebook guidance.

Do not introduce regexes, keyword lists, synonym tables, or language-parsing fallbacks.

## Required tests

Cover:

- item-only surrender offer;
- weapon-only surrender offer;
- combined item-and-weapon offer;
- rejection of an offer containing no concession;
- weapon-only offer leaves the weapon equipped before acceptance;
- weapon moves only upon acceptance;
- rejecting or ignoring the offer moves nothing;
- expiry moves nothing;
- only the named recipient may accept;
- no unilateral immunity or disposition change;
- existing surrender behaviour remains intact.

---

# 2. Make Environmental Destruction Reachable

`damage_environmental_object` already exists and must be reliably available when a valid destructible environmental object is present.

A character intent such as:

> “I strike the Overturned Mill Workbench with my iron mace to batter it down.”

should be representable through the existing structured action and deterministic engine path.

Review:

- the character’s available-action projection;
- the tool schema and description;
- DM adjudication guidance;
- relevant rulebook cards;
- target projection and target resolution;
- action metadata concerning exposure from cover.

Ensure the model can see:

- that the object is a valid target;
- that damaging it is a complete action;
- that the action damages the object rather than its occupant;
- that it uses the existing deterministic environmental-object damage rules.

Do not add natural-language routing code.

Do not treat ordinary containers as destructible environmental objects.

For an unlocked container, the supported action remains `open_container`. Guidance may make this distinction clear, but the engine must not infer it by parsing phrases such as “pry open” or “smash open.”

Add focused tests proving:

- a valid environmental object is included in the action’s target affordances;
- the structured action reaches the correct engine path;
- a cover object can be damaged and destroyed;
- a container is not silently accepted as an environmental-damage target;
- no character damage is invented when attacking the object;
- the actor’s cover exposure behaviour remains correct.

---

# 3. Forfeited Weapons as Unequipped Trophies

Remove the contradiction where surrender output says a weapon lies on the floor “where anyone can pick it up,” while the item action set refuses to pick it up.

Implement the smallest coherent weapon-looting slice.

## Loose weapon state

When an equipped weapon is forfeited through surrender, place a corresponding loose weapon item on the room floor.

It must retain:

- stable identity;
- name and description;
- provenance;
- its origin as a weapon;
- its current owner or location.

It is no longer equipped.

## Taking a loose weapon

Allow the existing `take_item` action to collect a loose weapon from the floor or a body.

The collected weapon becomes a carried, unequipped trophy in the character’s ordinary inventory.

It must be possible to:

- take it;
- carry it;
- give it as an item;
- drop it;
- steal it using the existing item rules.

It must **not** be possible to:

- equip it;
- swap it for the character’s current weapon;
- attack with it;
- gain its weapon damage;
- dual-wield;
- bypass the existing equipped-weapon rules.

The character’s existing equipped weapon remains authoritative for attacks.

Character-facing state must distinguish naturally between:

```text
Equipped weapon: Longsword
Carried items: Notched Sabre taken as a trophy
```

Avoid machinery wording such as “unequipped slot” in ordinary narration.

If implementing loose weapons through the existing inventory model would require a broad equipment-system rewrite, stop and use the alternative safe outcome:

- remove every claim that the weapon can currently be picked up;
- describe it only as visibly forfeited on the floor;
- document weapon pickup and equipping as deferred.

Do not leave the current contradiction in place. At handoff, state clearly which outcome was implemented and why.

## Required tests

If pickup-as-trophy is implemented, cover:

- forfeited weapon appears on the floor;
- active character can take it;
- it enters inventory without replacing the equipped weapon;
- attacks continue using the equipped weapon;
- attempting to attack with the trophy is rejected;
- it can be given, dropped, and stolen as an ordinary carried item;
- provenance remains correct;
- surrenderer remains disarmed;
- final state, UI, narration, and reports agree.

---

# 4. One Mechanical Deed Per Turn

Strengthen the character-facing action contract:

> Choose exactly one mechanical deed for your turn. Public speech may accompany it, but speech does not add another deed.

Clarify with concise examples:

```text
Allowed:
Attack Vark while shouting a warning.

Not allowed:
Attack Vark and steal his purse.

Allowed:
Give Rowan one item while asking him to spare you.

Not allowed:
Take an item, give it away, and then attack.
```

Do not ban speech alongside actions. Structured speech is an intentional feature.

Do not attempt to split a compound mechanical intent into several actions.

Do not add parsing code to count verbs, clauses, conjunctions, or quoted spans.

The existing structured action remains authoritative. Any structured utterance accompanies it without changing its mechanical meaning.

Where an intent contains several deeds, retain the existing bounded rejection/retry behaviour and give a concise in-world request to choose one.

Update existing rulebook guidance rather than adding multiple redundant cards.

---

# 5. Demands Are Non-Binding Speech

Do not add a new authoritative `demand`, `extort`, or negotiation-contract action.

Characters can already make demands through structured public speech.

Clarify the distinction:

```text
Ordinary demand:
Public speech. It binds nobody.

Threat intended to cause fear:
intimidate_character plus structured speech.

Offer to give up one’s own fight:
offer_surrender.

Acceptance of a pending surrender:
accept_surrender.
```

A demand may accompany one valid mechanical deed.

For example:

> “I attack Vark and shout that he should drop the salve.”

should resolve as:

- one attack;
- one public utterance;
- no item transfer;
- no surrender offer;
- no binding demand.

The DM must not reject a valid deed merely because its accompanying speech contains a demand.

The recipient may react voluntarily on a later turn through the ordinary character-to-character communication system.

Speech wording must not mutate state unless paired with an explicit supported structured action.

Do not inspect the demand’s wording to decide whether it is persuasive, serious, binding, or intimidating.

Add tests proving:

- speech plus one deed resolves the deed;
- the demand itself changes no state;
- no surrender offer or agreement is created;
- no item moves;
- `intimidate_character` remains the only action that performs a fear check;
- surrender remains the only binding negotiated combat outcome.

---

# 6. No-Distance World Guidance

Keep the existing no-distance room model.

Do not add:

- positions;
- coordinates;
- zones;
- adjacency;
- movement points;
- opportunity attacks;
- reach calculations;
- movement actions.

Strengthen the character-facing state and prompt with guidance equivalent to:

> Everyone and every interactable object in this room is already within reach. Moving closer, stepping aside, circling, or repositioning has no mechanical effect. Describe incidental movement as part of one supported deed. If waiting or holding position is your whole intention, Defend or end your turn.

Incidental movement may appear in narration but must not mutate authoritative state.

A sole repositioning intent remains unsupported because the world has no positional state to change. The response should briefly direct the character toward `defend`, `end_turn`, or another available action.

Do not identify movement through regexes or keyword lists.

Do not add a compatibility shim that silently converts arbitrary movement into another action.

---

# 7. Correct Existing Output Claims

Audit engine output, narration prompts, UI text, and reports for claims that exceed the supported mechanics.

At minimum, verify:

- a pending weapon-forfeiture offer has not already dropped the weapon;
- surrendered weapons are described consistently with their actual interactability;
- demands are not described as agreements;
- incidental movement is not described as changing reach or position;
- environmental-object attacks do not imply container destruction;
- carrying a trophy weapon does not imply it is equipped;
- surrender acceptance clearly separates negotiated tribute from mandatory disarmament.

Prefer generating common authoritative statements from structured outcome data rather than asking a model to reconstruct them.

Do not expand leakage regexes or synonym lists as part of this audit.

---

# 8. Rulebook and Token Discipline

Amend existing cards where practical rather than adding a separate card for every example.

The rulebook should clearly cover:

- item-backed and weapon-backed surrender;
- pending offers versus completed surrender;
- non-binding demands;
- one mechanical deed per turn;
- environmental-object damage versus opening containers;
- loose weapons as carried trophies, if implemented;
- the absence of equipping and swapping;
- the no-distance room model.

Keep wording concise because whole-rulebook consultation remains the chosen production design.

Do not reopen the embedding retrieval experiment.

Do not add keyword-based retrieval.

Do not add another model call.

---

# 9. Reporting and Diagnostics

Update reports only where necessary to make the final boundaries honest.

Include or preserve:

- concession type for surrender offers;
- whether a weapon was promised;
- whether and when the weapon actually moved;
- loose-weapon provenance and final location;
- environmental-object damage attempts;
- correct rejection reasons for unsupported container damage or weapon equipping;
- action acceptance and rejection metrics.

Update `reports/action-set-gaps.md` or equivalent documentation to distinguish:

- gaps closed by this batch;
- deliberate exclusions;
- future possibilities.

Do not describe every unsupported RPG action as unfinished work.

---

# 10. README Boundaries

Add a concise supported-action boundary to the README.

Document that:

- one mechanical deed is allowed per turn;
- speech may accompany it;
- demands are non-binding;
- surrender requires a pending offer and acceptance;
- weapon forfeiture is a valid surrender concession;
- movement inside the room is descriptive;
- environmental objects can be damaged only when they expose that capability;
- loose weapons may be carried as trophies but cannot be equipped, if that path was implemented.

Explicitly list the intentionally unsupported mechanics:

- throwing objects;
- disarming attacks;
- weapon swapping and equipping;
- damaging ordinary containers;
- positional movement;
- readied reactions;
- improvised physics.

---

# 11. Regression Tests

In addition to the focused tests above, verify:

- ordinary attacks remain unchanged;
- critical, glancing, fear, and Defend interactions remain unchanged;
- environmental cover remains authoritative;
- item giving, dropping, stealing, and taking remain correct;
- surrender item transfer remains atomic;
- character-specific knowledge remains correct;
- structured speech remains authoritative;
- the engine remains authoritative over rulebook guidance;
- older run data remains renderable;
- all existing tests continue to pass.

Tests must not require live Ollama, OpenAI, Anthropic, or embedding services.

---

# 12. Scope Exclusions

Do not add:

- unilateral protected yielding;
- demand contracts;
- item barter contracts outside surrender;
- weapon equipping or switching;
- improvised weapons;
- thrown attacks;
- throwing items to allies;
- disarming;
- forced movement;
- damageable containers;
- positional movement;
- multi-action turns;
- additional environmental systems;
- new natural-language parsing;
- new agent frameworks;
- another rulebook retrieval experiment.

If a tempting implementation requires one of these, document it as future work instead.

---

# 13. Completion Criteria

The batch is complete when:

- weapon-only surrender works through the existing bilateral handshake;
- pending offers move nothing and grant no protection;
- item-for-life intents are clearly supported by the existing surrender action;
- valid environmental-object damage is visible and reachable;
- loose surrendered weapons are handled truthfully;
- one-deed guidance is clear;
- demands remain expressive but non-binding;
- non-positional movement guidance is clear;
- no language-parsing heuristics have been introduced;
- all automated tests pass;
- documentation accurately distinguishes supported mechanics from deliberate exclusions.

Perform one bounded smoke run with a capable hosted model if configured.

Do not repeatedly tune prompts around individual phrases from one run. Fix the structured contract, affordances, and authoritative output.

At handoff, report:

- which gaps were closed;
- how weapon-only surrender behaves;
- how loose weapons are represented;
- how environmental destruction is exposed;
- test count and result;
- smoke-run result;
- remaining deliberate limitations.

Stop after completing this closure batch.
