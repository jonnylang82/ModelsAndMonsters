# Models & Monsters v0.8 — Morale and Combat Volatility

Implement v0.8 as a focused vertical slice introducing:

- encounter-scoped fear and visible morale;
- critical hits as the counterpart to glancing blows;
- explicit intimidation and reassurance actions;
- behavioural pressure toward survival without removing model agency;
- complete UI, tracing, reporting, and deterministic testing;
- a bounded investigation into reducing Rulebook Resolver token usage.

Inspect the current implementation first and fit these changes into its existing architecture and naming. Preserve the deterministic engine, explicit orchestration, per-agent history, knowledge projection, structured speech, rulebook boundary, and `Microsoft.Extensions.AI` model abstraction.

Do not add unrelated gameplay features.

---

## Architectural Rules

The engine remains authoritative.

Models choose intent. They do not decide:

- whether intimidation succeeds;
- whether a hit is critical;
- how much fear changes;
- whether a status applies;
- whether surrender or escape succeeds;
- what another character knows.

Do not use regexes, keywords, verb lists, sentiment analysis, or other hand-built language parsing to infer speech, intimidation, reassurance, fear, surrender, or rule relevance.

When semantic information is needed, require it explicitly in structured model output.

Spoken wording is roleplay. It must not secretly change mechanical odds based on particular words or phrases.

Do not add a model call merely to classify speech or determine whether something sounded intimidating.

---

# 1. Fear

Add an encounter-scoped integer fear value to each character:

```text
Minimum: 0
Maximum: 5
Initial value: 0 unless explicitly seeded by a scenario
Scared threshold: 3
```

Fear is authoritative engine state.

The exact fear value is available to:

- the character themselves;
- the experiment harness;
- tracing, reports, and the diagnostic UI.

Other characters should not receive the exact number through their game-state projection. They can observe the public `Scared` status once the threshold is reached.

## Fear increases

Increase fear by one when:

1. A surviving character receives a critical hit.
2. A surviving character takes damage from one attack equal to at least 25% of their maximum health.
3. A character transitions from not outnumbered to outnumbered.
4. A character is the target of a successful `intimidate_character` action.

A critical hit that also meets the large-hit threshold causes only one fear increase. Record both contributing reasons if useful, but do not double-count the same resolved attack.

Outnumbering is based only on active combatants. Dead, surrendered, escaped, absent, or otherwise inactive characters do not count.

An outnumbering increase must occur only on the transition from `false` to `true`, not every round or turn. If parity is genuinely restored and the character later becomes outnumbered again, a new transition may cause another increase.

Clamp all changes to the 0–5 range.

## Scared status

At Fear 3 or above, apply a public `Scared` status.

When fear falls below 3, remove it publicly.

The status should be observable by everyone present and flow through the existing public-event and character-knowledge systems.

Do not make `Scared` directly force an action or choose an action through RNG.

Project a strong but non-binding instruction to the affected character along these lines:

> You are scared and increasingly concerned with survival. Seriously consider escaping, surrendering, defending, seeking reassurance, or otherwise protecting your life, but the choice remains yours.

Other characters should receive only an in-world observation such as:

> Vark looks scared and increasingly concerned with survival.

Do not expose internal terminology, numeric modifiers, stable IDs, or machinery language in character-facing prose.

Fear should not initially impose an attack penalty. Avoid creating an automatic combat death spiral in this version.

---

# 2. Fear Recovery

Fear must have meaningful counterplay.

Reduce fear by one when:

1. A frightened character lands a critical hit.
2. An ally successfully uses `steady_ally` on the character.
3. Vark’s existing `Rally Grunt` ability is used on its target; retain its current combat effect and additionally reduce the target’s fear by one.

A critical hit can therefore increase the surviving target’s fear and reduce the attacker’s fear during the same resolution.

If the target dies from the critical hit, do not apply fear to the dead target.

All fear changes must state their cause and produce the appropriate `Scared` transition when crossing the threshold.

---

# 3. Steady Ally

Add an explicit structured action:

```text
steady_ally
```

It:

- targets one living, present, active ally;
- cannot target the actor;
- consumes the actor’s action;
- reduces the target’s fear by one;
- performs no RNG draw;
- has no effect when the target is already at zero fear;
- cannot be combined with an attack, movement, item action, or another ability.

Require the character response to contain an explicit structured public utterance addressed to the ally. Validate the presence and recipient structurally.

Do not inspect the wording to decide whether it was sufficiently reassuring.

Narration should describe the ally being steadied without exposing the numeric fear score.

---

# 4. Intimidation

Add an explicit structured action:

```text
intimidate_character
```

It:

- targets one living, present, active enemy;
- consumes the actor’s action;
- must be accompanied by an explicit structured public utterance addressed to that enemy;
- performs one deterministic and fully traced RNG check;
- increases the target’s fear by one on success;
- changes nothing on failure;
- never directly forces surrender, escape, item transfer, disarming, or a skipped turn.

Allow only one intimidation attempt from a particular actor against a particular target per encounter. Remove the affordance after that attempt so models are not encouraged to repeat it.

Use state-derived modifiers only. Start with:

```text
Base chance:                         35%
Per existing point of target fear: +10%
Target is currently outnumbered:   +15%
Target is badly wounded:           +10%
Intimidator is Scared:             -10%
Minimum effective chance:           10%
Maximum effective chance:           90%
```

Use the project’s existing health-band definition for “badly wounded”; do not create a competing interpretation.

Success is determined by the project’s existing percentage-roll convention.

The spoken threat must have no mechanical modifier based on eloquence, length, vocabulary, punctuation, or model identity.

Trace:

- actor and target;
- structured utterance reference;
- base chance;
- every modifier and its authoritative source;
- effective chance;
- raw roll;
- result;
- resulting fear change;
- any `Scared` transition.

---

# 5. Critical Hits

Extend the existing attack-quality system so glancing and critical hits are opposite outcomes of the same quality draw.

After an attack passes its hit check, use one quality draw:

| Raw quality roll | Result |
| --- | --- |
| 1–25 | Glancing blow |
| 26–75 | Solid hit |
| 76–100 | Critical hit |

Do not add a separate critical draw after the existing glancing draw. Replace or generalise the existing quality check so one raw draw selects among all three candidates.

Apply the quality modifier to the existing post-armour damage calculation:

```text
Glancing: half post-armour damage, using the existing rounding rule
Solid: normal post-armour damage
Critical: double post-armour damage
```

Preserve existing minimum-damage and armour behaviour unless a change is strictly required to support the multiplier.

A critical hit should:

- use distinct engine output and narration;
- participate in the existing injury system;
- increase a surviving target’s fear by one;
- reduce the attacker’s fear by one;
- appear distinctly in the UI and reports.

The equal glancing and critical bands deliberately create more volatility and increase average post-armour damage slightly, helping encounters resolve faster without simply raising every weapon’s damage.

---

# 6. RNG and Reproduction

Use the existing `IRng` abstraction for every new draw.

Ensure every RNG draw records its seed/state, purpose, candidates, raw roll, modifiers, effective threshold, and result. Otherwise behavioural comparisons will become much harder to reproduce.

Critical-hit and intimidation draws must be replayable from the run seed.

Do not use ambient randomness, `Random.Shared`, timestamps, GUIDs, or model output to determine mechanical results.

Changing fear without RNG must still create a trace event describing the deterministic cause.

---

# 7. Speech and Structured Intent

Use the existing structured utterance mechanism.

Do not rediscover speech by looking for:

- quotation marks;
- speech verbs;
- threats;
- exclamation marks;
- names near quoted spans;
- emotionally charged words.

`intimidate_character` and `steady_ally` must refer to explicitly structured speech produced in the same character response.

If the structured action and utterance disagree about the target, reject or retry through the existing bounded structured-output path. Do not guess from prose.

Keep any legacy text fallback isolated, traced, non-authoritative, and unused on the normal path.

---

# 8. Knowledge and Visibility

Integrate fear with the current character-specific knowledge system.

Requirements:

- Exact personal fear is projected to the owning character.
- Exact fear values are withheld from opponents.
- `Scared` becoming active or inactive is a public observable event.
- Intimidation attempts and their visible success or failure are public.
- Mechanical percentages and rolls must not appear in ordinary character-facing narration.
- The DM must narrate only from projected outcome facts.
- A fluent but unsupported emotional claim must not mutate fear.

The diagnostic UI and generated reports may show full authoritative values because they are experiment artefacts rather than character knowledge.

---

# 9. Surrender and Escape Interaction

Fear provides behavioural context but does not bypass the existing surrender or escape protocols.

A scared character must still:

- make a concrete surrender offer;
- offer an actual concession;
- wait for the named recipient to accept;
- open an exit before escaping where required;
- use the correct structured action.

Intimidation must not manufacture an offer or acceptance.

Surrender remains voluntary model intent followed by deterministic engine validation.

Escape remains voluntary model intent followed by deterministic engine validation.

Do not add an automatic “panic and flee” mechanic in v0.8.

---

# 10. Rulebook Updates

Add concise rule cards covering:

- fear and the `Scared` threshold;
- fear gain and recovery;
- `intimidate_character`;
- `steady_ally`;
- glancing, solid, and critical attack quality;
- the fact that fear influences character context but never forces surrender or escape.

Keep normative rules concise. Avoid filling cards with repeated prose examples where structured statements would suffice.

Update tool descriptions and affordances so characters and the DM receive only actions currently available to them.

---

# 11. Rulebook Efficiency Investigation

The Rulebook Resolver currently consumes approximately 6,700 input tokens per consultation, sends around 18 cards, and cites approximately 1.1 cards. Both local and hosted runs show that this is structural rather than provider-specific.

Investigate reducing this cost without weakening correctness.

This is an evidence-gathering and bounded-prototype task. Do not blindly replace the current production path.

Evaluate these candidate approaches:

1. **Structured routing**
   When an action family is already explicitly known, select cards through declared action-to-rule metadata rather than language parsing.

2. **Embedding retrieval**
   Embed each rule card once, embed an incoming rules question, retrieve a small top-K set, and expand it through explicitly declared related-rule links.

3. **Compact semantic index**
   Present a model with card IDs, titles, and short summaries, ask it to select relevant IDs, then resolve using only the selected full cards.

4. **On-demand consultation**
   Give the DM the lean core rules and allow an explicit `check_rules` call only when detailed rules guidance is required, matching the way a human DM consults a rulebook.

Do not use regexes, keyword lists, verb lists, synonym dictionaries, or manually expanded phrase tags for rule selection.

## Evaluation corpus

Build a labelled corpus from captured consultations and targeted test cases covering at least:

- attack versus ability use;
- inspect versus open versus take;
- give versus drop versus steal;
- open an exit versus escape through it;
- surrender offer versus surrender acceptance;
- intimidation versus ordinary speech;
- steadying an ally versus ordinary communication;
- compound actions;
- stale ownership;
- ambiguous and unsupported intentions.

Do not treat previously cited cards as unquestionable ground truth. They are useful evidence, but manually identify the rule cards actually required to make the correct decision in the labelled cases.

## Measurements

For each strategy, report:

- required-card recall;
- final resolver-decision agreement with the labelled expectation;
- incorrect supported/unsupported decisions;
- average cards supplied;
- average input tokens;
- model calls;
- latency;
- fallback frequency.

Target at least a 60% reduction in average resolver input tokens while retaining every required rule in the labelled corpus.

## Safety

Any production retrieval strategy must:

- trace selected cards and the selection method;
- trace similarity scores, model-selected IDs, or structured reasons as appropriate;
- include declared related rules;
- fall back to the full bounded rulebook when confidence is insufficient;
- cache static card embeddings or selections separately from state-sensitive answers;
- keep the engine authoritative;
- remain behind configuration until proven.

Do not add a production embedding dependency merely to claim completion if no configured and testable embedding provider exists. In that case, leave a clean abstraction or prototype and provide an honest findings report.

Produce a short repository document summarising:

- current baseline;
- approaches evaluated;
- measurements;
- correctness risks;
- recommended next implementation;
- whether always consulting the rulebook remains justified.

---

# 12. Web UI

Update the existing UI without redesigning it.

Each character card should show:

- current fear level as a compact 0–5 indicator;
- visible `Scared` status;
- existing health, injuries, abilities, and disposition.

The live transcript should distinguish:

- glancing blows;
- solid hits;
- critical hits;
- intimidation attempts and outcomes;
- fear increases and decreases;
- becoming Scared;
- recovering from Scared.

Keep mechanical detail readable but secondary to the narrative.

Characters who surrender, escape, or die must retain their final fear value in the final state and report.

---

# 13. Run Report

Extend the generated report with:

## Morale summary

- initial and final fear per character;
- maximum fear reached;
- whether and when each became Scared;
- whether and when they recovered;
- fear changes grouped by cause;
- action chosen on each turn while Scared.

## Intimidation summary

- attempts;
- actor and target;
- public utterance;
- base chance;
- modifiers;
- raw roll;
- outcome;
- fear change.

## Attack-quality summary

- glancing, solid, and critical counts;
- damage by quality;
- fear changes caused by critical hits;
- critical hits by attacker and recipient.

## Rulebook efficiency

- consultation count;
- average cards and tokens;
- cache behaviour;
- configured selection mode;
- fallback count;
- experimental comparison results where available.

Preserve existing grounding, surrender, inventory, ability, RNG, context-health, and final-state sections.

---

# 14. Tests

Add deterministic tests covering at least:

## Fear

- defaults to zero;
- clamps between zero and five;
- critical hit increases surviving target fear;
- large non-critical hit increases fear;
- a critical large hit increases fear only once;
- dead targets do not gain fear;
- critical attacker recovers one fear;
- entering outnumbered state increases fear once;
- remaining outnumbered does not repeatedly increase fear;
- leaving and later re-entering an outnumbered state can trigger again;
- Scared applies at three;
- Scared is removed below three;
- public events are created only on visible status transitions;
- opponents do not receive exact fear values.

## Critical hits

- quality boundaries at 25/26 and 75/76;
- glancing, solid, and critical damage calculations;
- armour and rounding remain correct;
- exact RNG trace contents;
- replay from a fixed seed produces the same result.

## Intimidation

- all modifiers are calculated from authoritative state;
- success and failure paths;
- effective chance clamps correctly;
- successful intimidation raises fear;
- failure changes no fear;
- one attempt per actor-target pair;
- no speech-content parsing affects the odds;
- missing or mismatched structured speech is rejected before RNG.

## Steady Ally

- valid ally loses one fear;
- cannot target self or enemy;
- cannot reduce below zero;
- consumes the action;
- uses no RNG;
- requires structured speech;
- can remove Scared by crossing below the threshold.

## Regression

- surrender still requires a concrete offer and acceptance;
- fear never automatically surrenders or escapes a character;
- Guard Ally, Defend, Rally Grunt, Dirty Strike, and Healing Prayer retain their existing behaviour;
- object knowledge remains private until publicly revealed;
- old run data can still be rendered or fails gracefully if fear fields are absent;
- all existing tests continue to pass.

Tests must use fake or deterministic clients and RNG implementations. Unit tests must not require live Ollama, OpenAI, or embedding services.

---

# 15. Completion Criteria

The work is complete when:

- the solution builds cleanly;
- all existing and new tests pass;
- fear, Scared, intimidation, reassurance, and critical hits function end to end;
- all new RNG and deterministic state changes are fully traced;
- the UI and report expose the new mechanics;
- no new language-parsing heuristics have been introduced;
- the Rulebook Resolver investigation has produced measured findings;
- any experimental rule-selection path is safely configurable and falls back to the current bounded path;
- application version and relevant prompt/rulebook versions are updated to v0.8.

Run the full automated test suite.

If practical, perform one short smoke run, but do not tune prompts around a single model’s phrasing and do not weaken engine validation to make the smoke run appear successful.

At handoff, report:

- the implemented behaviour;
- important architectural decisions;
- test count and result;
- smoke-run result, if performed;
- rulebook-efficiency measurements;
- any remaining risks or intentionally deferred work.

Do not continue into multiple rooms, persistent campaigns, human-controlled characters, environmental combat, or additional spell systems in this version.
