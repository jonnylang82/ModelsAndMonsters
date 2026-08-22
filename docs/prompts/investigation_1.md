# Spec — refresh the rule-selection corpus, then add an `ActionRouting` selector

**Status:** implementation spec. Nothing in the shipped path changes: `RulebookSelectionMode` stays
`WholeRulebook` by default, and the new strategy arrives behind that switch like every other one.

**Written against:** v0.10. `RuleSelectionMode` = `WholeRulebook | CompactIndex | Embedding | StructuredRouting`,
plus the `Oracle` measurement-only strategy.

**Tasks are ordered and each gates the next. Report the numbers at the end of Tasks 0, 1 and 6 before
continuing.** Three separate things are being measured and they must not be allowed to land together, or a
result cannot be attributed to any of them.

---

## What is being tested, and why it is not just another retrieval mode

Every existing selector maps **intent → cards** in one hop. `CompactIndex` does it with a model reading card
summary lines; `Embedding` does it with cosine over card text. Both retrieve documents directly.

The new one inserts a bounded intermediate: **intent → engine action → cards**. The model is shown the
engine's *action surface*, never the rulebook, and returns one action label plus the actions it is ruling out.
Code expands that to cards from metadata declared on the cards themselves.

Two claims, separable:

1. **Correctness.** Naming what an intent is *not* recovers the cases similarity cannot reach — the cases lost
   so far are all the same shape: the card the decision turns on is the one the intent never mentions.
2. **Scale.** The index is bounded by the engine's action surface (~15, frozen at v0.10), not by the rulebook.
   `CompactIndex`'s index grows a line per card and does not survive a real rulebook; this one does.

Claim 2 is the reason to build it. Claim 1 may be satisfiable more cheaply — which is what Task 1 exists to
find out.

---

## Prerequisites — confirm these shapes before writing anything

This spec was written from `CompactIndexSelector.cs`, `reports/rulebook-efficiency.md` and the run reports.
Several types are inferred. **Read the real definitions and adjust this spec's naming to match the codebase,
not the other way round.** Confirm:

- `IRuleSelector` — exact signature. Assumed `RuleSelectionMode Mode { get; }` and
  `Task<RuleSelection> SelectAsync(string intent, CancellationToken ct)`.
- `RuleSelection` — full member list, and how `Reasons` is populated and rendered.
- `RuleSelectionSupport.Build` / `.Fallback` / `.BuildIndex` — exact parameters, and **where declared
  related-rule link expansion happens** (inside `Build`, or in the selector?). The new selector must reuse it,
  never reimplement it.
- `RuleCard` — its fields. **Does a card already carry the engine action it governs?** Guidance names candidate
  actions and `RulebookConsultant.Hydrate` fills descriptive fields from the cited card, so something already
  links card to action. If that link exists, Task 2 shrinks to one new field.
- `IRuleRepository` — how to enumerate all cards, not just `Find(id)`.
- `RuleSelectionCorpus` — case id, intent text, required card ids, expected decision, clarity band, family.
- The `--rulebook-eval` entry point — how it enumerates strategies, builds each, and writes
  `reports/rulebook-efficiency-measurements.md`.
- `ProviderCapabilities` — whether a structured-output / JSON-schema capability row already exists.

If any assumption is wrong, **stop and report it** rather than working around it.

---

## Task 0 — refresh the corpus, then re-baseline

The labelled corpus dates from v0.8. Four cards and a batch of action-set rules have landed since, and none
of them are covered. This is not only a coverage gap — it **biases every strategy comparison in the same
direction**, because a selector is never penalised for dropping a card no case requires. The v0.9/v0.10 cards
sit in the whole-rulebook baseline inflating what it pays, while carrying zero recall risk for anything that
narrows.

The gap also falls exactly where the hard boundaries are. `combat.defend` vs `environment.take-cover` is the
sharpest distinction in the project — found by live probing, fixed by card authoring in `v0_9_issues.md` #1 —
and there is no case for it. The corpus's own stated principle, *every family represented by the contrast that
makes it hard*, is currently violated for the newest and most confusable cards.

### 0.1 Add the missing cases

Check what v0.9/v0.10 actually added before writing these; the list below is inferred from the release notes.
Follow the existing naming and structure.

**Cover (v0.9) — contrasts, not easy cases:**

| Case | Intent shape | Note |
| --- | --- | --- |
| `defend-vs-take-cover` | *"I brace behind the workbench"* | **The** case. Requires both cards; naming a real object is what decides it. |
| `take-cover-plain` | unambiguous shelter | The baseline the contrast is measured against. |
| `damage-object-vs-attack` | *"I bring my sabre down hard on the workbench"* | Requires the object-damage card **and** the attack card — ruling out a strike at a person is the decision. |
| `leave-cover-vs-exposing-action` | an ordinary action that vacates cover as a side effect | Tests whether the cover rule is pulled when the intent never mentions it. |

**v0.10 closure batch:**

| Case | Intent shape | Note |
| --- | --- | --- |
| `weapon-only-surrender` | terms forfeiting only the weapon, nothing carried | Valid since v0.10, invalid before. |
| `trophy-weapon-take` | lifting a forfeited sabre off the floor | Takeable, never equippable. |
| `unsupported-equip` | *"I pick up the sabre and fight with it"* | Deliberately unsupported. |
| `unsupported-force-container` | *"I smash the crate open"* | Contrast against `damage_environmental_object`: an ordinary container is never a damage target. |
| `demand-vs-offer` | *"Drop your weapon or die"* | Speech, binding nobody. The v0.7 defect where a demand was recorded as the speaker's own surrender. |

`unsupported-throw` is optional — v0.10 named throwing as permanently refused, though `unsupported-disarm`
already covers that family.

**Phrasings from traces, labels by hand.** Real wordings exist for most of these in the v0.9/v0.10 field notes
and run traces — harvest them, because invented phrasings are unrepresentative in exactly the way the corpus
is supposed to guard against. **Derive the required cards from the rules yourself.** Do not infer labels from
what past runs cited: cited cards are evidence of what a model reached for, not of what a decision turned on,
and inferring from them would score every strategy against the baseline's habits — including the v0.7 failure
where an acceptance was cited as a theft.

### 0.2 Record what the corpus was labelled against

Store the rulebook content hash the corpus was last labelled against, and surface it in the measurements
table. Every published figure should say which catalog it describes. The current table says *21 cards,
`rulebook-24bb318aa8`*; live runs send 25.

### 0.3 Re-baseline and report

Re-run `--rulebook-eval` on the refreshed corpus and report the full table for **all existing strategies**.
This supersedes the published one; the baseline has already moved (21 → 25 cards, measured 5,260 →
~6,300–6,585 live resolver tokens per call) before any new strategy exists.

Also re-derive the **clarity distribution** (currently 15 clear / 13 ambiguous / 5 unsupported). Cases skewed
toward contrasts will shift it, and `rulebook-efficiency.md` §4's on-demand projection
(`0.55 × 1,913 ≈ 1,050` tokens) is computed from it — that estimate needs re-deriving, not leaving stale.

Note in the write-up that one case now moves recall by ~2.4 points rather than 3.0. Modest, and worth stating
rather than implying the corpus became sturdy.

**Report before continuing.**

---

## Task 1 — the control: exclusions in the existing index

Without this, a good result from the new selector cannot be attributed. The new index will carry exclusions;
`CompactIndex`'s index does not. If the new selector wins, we would not know whether the gain came from the
routing indirection or simply from putting exclusions in front of the model.

**Change:** in `RuleSelectionSupport.BuildIndex`, extend each index line with the card's exclusions — the
boundary, not just the purpose:

```
container.take — lifting from an open container, the floor, or a body. NOT from a living
                 person (inventory.steal); NOT the tribute a pending offer promised you
                 (encounter.accept-surrender).
```

Source the exclusion text from the card's own exclusion field if one exists. Do not hand-write a second copy
that can drift from the card.

**Measure:** re-run `--rulebook-eval` on the refreshed corpus and report `CompactIndex`'s required-card
recall, cases-complete, decision agreement, avg cards and avg selection tokens, before and after.

**Report before starting Task 2.** If exclusion-carrying summaries reach full recall, that is a finding in its
own right — the 90.9% → 97% gap was index wording, not retrieval method — and it changes what the rest of this
spec is for.

Keep the change either way. Both selectors must be measured with exclusions present, so the only variable
between them is the indirection.

---

## Task 2 — card metadata: which action a card governs, and which it must be told apart from

Two fields on `RuleCard`, both **declared at the card's own declaration site** — the discipline
`GameAction.ExposesActor` already uses, not a lookup table maintained alongside the catalog, which drifts.

| Field | Meaning |
| --- | --- |
| `GovernsAction` | The engine action this card rules on. One per card. A card that genuinely governs none must say so explicitly rather than being left null by default. |
| `DistinguishedFrom` | The actions this card must be told apart from — the ones a reader needs in front of them to be sure this is the right card. |

`DistinguishedFrom` is the load-bearing one. Seed it from exclusions already written in card prose. At minimum,
from what live runs have proven confusable:

- `container.take` ⟷ `inventory.steal` (dead vs living)
- `container.take` ⟷ `encounter.accept-surrender` (a grab of promised tribute *is* the acceptance)
- `encounter.accept-surrender` ⟷ `inventory.steal` (ruling out a snatch is how you know it was an acceptance)
- `combat.defend` ⟷ `environment.take-cover` (naming a real object decides it)
- `combat.attack` ⟷ `damage_environmental_object` (a person vs a thing)
- `combat.attack` ⟷ speech-only acts (posture with no blow described is speech or nothing)
- `item.use` ⟷ the healing ability (a salve is an item, a prayer is an ability)

Declared directionally, **traversed symmetrically** at expansion: if `accept-surrender` declares `steal`, then
routing to `steal` also surfaces `accept-surrender`. One edge, declared once, avoids two half-maintained lists.

**Derive, do not duplicate.** The action → cards map is `repository.Cards.GroupBy(c => c.GovernsAction)` —
computed from the catalog and structurally unable to disagree with it.

---

## Task 3 — `ActionRoutingSelector`

`ActionRoutingSelector : ModelAgent, IRuleSelector`, in `Rulebook/Selection/`. New enum member
`RuleSelectionMode.ActionRouting`.

Model it closely on `CompactIndexSelector`: same construction, same cache shape and key
(`{RulebookVersion}|{intent.Trim()}`), same "a cache hit reports zero calls and zero tokens", same
`tools: null` stateless call, same paranoid fallback posture. Differences below.

### 3.1 The index

Built from card metadata, not card summaries. One line per **engine action**, carrying what it is and what it
is not:

```
attack_character — a weapon or fist making contact with a person.
    NOT a threatening step, an advance, brandishing, or any posture with no blow described.
    NOT a deliberate strike at an object.

take_cover — getting behind a named environmental object in the room.
    Naming a real object decides it, whatever defensive word accompanies it.
    NOT the character's own defensive stance with no object.
```

Compose the NOT clauses from `DistinguishedFrom` plus the governing cards' own exclusion text. Expose the
composed index as a public property (as `CompactIndexSelector.Index` is) so the offline measurement can size a
request exactly rather than estimating.

**Budget:** target ≤ 400 tokens against `CompactIndex`'s ~904. If it lands materially above, say so — the
token saving is half the case for the strategy.

### 3.2 The output contract

```json
{ "action": "<action-id>", "ruleOut": ["<action-id>", "..."], "confidence": "clear" | "unclear" }
```

- `action` — the primary deed, one value from the closed action set.
- `ruleOut` — actions that must be excluded to be sure. **This is the field doing the work embeddings could
  not.** May be empty.
- `confidence` — `unclear` when the intent could plausibly be two of these, or none.

**Constrain the shape at the decoder where the provider allows it.** Ollama takes a JSON schema as `format`
and llama.cpp constrains decoding to it; OpenAI has strict structured outputs; Anthropic has no JSON mode, so
there it degrades to prompt-plus-parse. Gate on a `ProviderCapabilities` row, dropped-and-reported like every
other uneven capability. Parse-and-fallback remains the backstop, not the only defence.

Put the action ids in the schema as an enum, so an invalid label is unrepresentable where supported and
rejected where not.

### 3.3 Expansion

1. Cards governing `action`.
2. Cards governing each entry in `ruleOut`.
3. Cards reached by `DistinguishedFrom` from (1), traversed symmetrically.
4. Existing declared related-rule link expansion — **reuse `RuleSelectionSupport`'s.**

Record each card's provenance in `Reasons` — routed / ruled-out / distinguished / linked — so a selection can
be read back and audited rather than only counted.

### 3.4 Fallbacks, and the distinction that matters

| Trigger | Kind |
| --- | --- |
| `confidence: "unclear"` | **semantic** |
| `action` names no known action | semantic — the model understood the format and picked outside the set |
| unparseable reply | mechanical |
| model call threw | mechanical |

All four fall back to the whole bounded rulebook. **They must be distinguishable in the trace and in the
report.** Twelve semantic fallbacks means the action boundaries are wrong; twelve mechanical ones mean the
model or the transport is broken. One label for both turns a diagnostic into a number.

Same split as `SpeechNotHeard` vs `HarnessLimitReached` in v0.8 — a rule of the world firing must not read as
something going wrong.

### 3.5 Fix the silent-drop bug while you are here (applies to `CompactIndexSelector` too)

`CompactIndexSelector` filters unknown ids with `ids.Where(id => _repository.Find(id) is not null)`. An
*all*-unknown reply falls back and says so; a **partially** unknown reply keeps the good ids and discards the
invented ones with no record — `Reasons` is built from `known` only. A model returning
`["combat.attack", "combat.parry"]` produces artefacts indistinguishable from one returning
`["combat.attack"]`.

Record dropped labels on the selection in both selectors. The project's own standard, from
`ProviderCapabilities`: dropped and reported, never silently ignored. Hallucinated ids are exactly the signal
needed to judge whether a smaller model can do the routing step.

### 3.6 The no-language-parsing rule

Nothing here infers game semantics from prose. The model returns a label; code validates it against a closed
enum and looks up cards — "validating syntax or exact identifiers", which the rule permits. **No verb lists, no
keyword tables, no phrase matching anywhere in this path.** If a run exposes a phrasing that routes wrongly,
the fix is the action definition or the `DistinguishedFrom` edge, never a synonym.

---

## Task 4 — startup validation

The rulebook already fails at startup rather than trimming silently (`RulebookMaxCards`). Same discipline:

- every engine action is governed by at least one card;
- every `GovernsAction` names a real engine action;
- every `DistinguishedFrom` entry names a real engine action;
- the composed index fits the resolver's startup budget guard.

A card that governs nothing, or an action with no card, fails the run. A quiet gap here is a rule that can
never be selected.

---

## Task 5 — corpus coverage check in `--rulebook-eval`

The staleness in Task 0 happened silently across two releases. Make it impossible to repeat.

`--rulebook-eval` reports, every run:

- every card no case requires;
- every engine action no case routes to;
- the rulebook content hash the corpus was labelled against, and whether it matches the current catalog.

**Fail the eval** on an uncovered card or an uncovered action, rather than warning. Same discipline as
`RulebookMaxCards` throwing at startup: adding a card without adding a case becomes impossible. A warning
would have been ignored for two releases, because it effectively was.

A documented allow-list for a card that genuinely cannot be exercised is acceptable — but it must be an
explicit entry with a reason, not an absence.

---

## Task 6 — `--rulebook-eval` wiring for the new strategy

Add `ActionRouting` alongside the existing rows, producing every current column: required-card recall, cases
with every required card, decision agreement, wrongly supported, wrongly unsupported, avg cards, avg resolver
tokens, avg selection tokens, model calls, fallbacks.

Then add what the table cannot currently show:

**a) Avg cards after expansion, called out explicitly** against Oracle's 4.3 and `WholeRulebook`'s 25. **This
is the number that decides the strategy.** At 5–7 cards it works. At 15+, the exclusions are dense enough that
the book is effectively irreducible — a clean negative, to be reported as one rather than tuned away.

**b) Fallbacks split semantic / mechanical**, per 3.4.

**c) Action-label accuracy, per class.** The main new diagnostic: for each case, did the model name the right
primary action? Needs an expected action per corpus case — **add it by hand, from the rules, exactly as the
required-card labels were derived**, including for the Task 0 additions. Report as a confusion table over the
~15 actions; the confusable pairs are the actionable output.

**d) The named hard cases, pass/fail per strategy.** The originals — `accept-surrender`,
`use-item-vs-heal-ability`, `steal-vs-attack-compound`, `intimidate`, `threat-with-a-blow`,
`plain-coordination` — plus the new contrasts: `defend-vs-take-cover`, `damage-object-vs-attack`,
`leave-cover-vs-exposing-action`, `demand-vs-offer`, `unsupported-force-container`. These are the cases the
whole exercise exists to recover; an aggregate that hides them is the wrong instrument.

Keep the modelled-resolver convention (right whenever it was shown the governing card) so every agreement
figure stays an upper bound and remains comparable with the published table.

**Report before drawing any conclusion.**

---

## Task 7 — tests

Deterministic, no live models, consistent with the existing suite:

- A scripted reply routing to a known action selects that action's cards.
- A reply with `ruleOut` pulls the ruled-out action's cards — assert directly on the acceptance/theft pair.
- `DistinguishedFrom` is traversed symmetrically.
- `confidence: "unclear"` falls back to the whole rulebook, recorded as **semantic**.
- An unknown action label falls back, recorded as **semantic**.
- Unparseable output falls back, recorded as **mechanical**.
- A thrown model call falls back, recorded as **mechanical**.
- A partially-unknown reply keeps the known label *and records the dropped one* (both selectors).
- A cache hit reports zero model calls and zero tokens.
- Startup validation fails on: an action with no governing card; a `DistinguishedFrom` naming a non-existent
  action.
- The coverage check fails on a card no case requires, and on an action no case routes to.
- The composed index stays within its token budget.

---

## Success criteria

Report these and let them decide; do not tune toward them.

| | Target | Meaning if missed |
| --- | --- | --- |
| Required-card recall | ≥ `CompactIndex` post-Task-1, on the refreshed corpus | Something cheaper already does this better; the case rests on scale alone |
| Wrongly unsupported | 0 | Silent omission is the only failure mode that matters here |
| Avg cards after expansion | 5–7 (Oracle 4.3) | At 15+, the book is irreducible — report as a negative result |
| Avg selection tokens | ≤ 400 (`CompactIndex` ~904) | The token case weakens |
| Action-label accuracy | high, with confusions named | Low means the action definitions need the boundary written, not the strategy abandoning |
| Semantic fallback rate | low, and *named* | High means the action boundaries are wrong, which is useful either way |

**Caveat to carry into the write-up, not to design around:** ~42 cases, so one case moves recall by ~2.4
points, and the modelled resolver makes every agreement figure an upper bound. This ranks strategies; it does
not pin any of them. The real test is `rulebook-efficiency.md`'s recommendation #3 — both modes over the same
seeded encounters, with a live resolver, compared on end-to-end adjudication outcomes.

---

## Out of scope

- Changing the shipped default. `WholeRulebook` stays.
- Feeding `StructuredRouting` from the Dungeon Master (the pull model). Related, separate change.
- Any use of run-trace citations as labels.
- Training, fine-tuning, or embedding anything new.
- A smaller model for the routing step. Worth trying **after** the numbers land, not before.
