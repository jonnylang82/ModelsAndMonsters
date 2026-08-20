# Rulebook Resolver efficiency — findings

**v0.8 · bounded investigation · no production path changed**

The Rulebook Resolver sends the whole rulebook on every consultation. This document records what that
actually costs, what four alternatives cost, what each of them loses, and which one is worth implementing
next. Nothing in the shipped path changed: `RulebookSelectionMode` defaults to `WholeRulebook`, and every
alternative is behind that switch.

The numbers here are reproducible:

```bash
dotnet run --project src/ModelsAndMonsters -- --rulebook-eval reports/rulebook-efficiency-measurements.md
```

That writes the raw table to `reports/rulebook-efficiency-measurements.md`. It measures the offline
strategies with no services at all, and measures the two model-backed strategies against whatever provider
is configured — the figures below came from local Ollama (`qwen3.5:9b` for selection, `nomic-embed-text`
for embeddings), the same configuration the harness runs on.

---

## 1. The baseline

| | |
| --- | --- |
| Cards in the rulebook | 21 (v0.7 had 18; v0.8 added intimidation, steadying and morale) |
| Cards sent per consultation | 21 — all of them, every time |
| Cards actually cited | ~1.1 in captured runs |
| Estimated resolver input, as v0.8 first shipped | 8,202 tokens per consultation |
| Estimated resolver input, after the fix in §4a | **5,260 tokens** per consultation |
| Model calls | 1 |

Both figures are this harness's own characters-to-tokens estimate over the complete request — resolver
system prompt, request template, intent and cards. It is trustworthy: a v0.8 live run on Ollama reported
**7,793 input tokens per resolver call** over 15 consultations against an estimate of 7,927 for that card
set, under 2% high.

**The first figure was not merely expensive, it was broken.** An 8,192-token window minus an ~8,065-token
request leaves about 127 tokens to reply in. A provider does not error on that — it generates until the
window is full and returns `FinishReason: length`, which arrives downstream as unparseable JSON, then
malformed guidance, then an in-world refusal of a perfectly ordinary action, four steps from the cause. A
12-round live run lost **5 of 57 consultations** exactly this way, every one of them with
`input + output == 8192` on the nose. §4a is what fixed it.

Even at 5,260 the shape of the waste is unchanged: roughly 90% of what the resolver reads is cards it will
not cite.

The cost is structural, not provider-specific. It does not grow with the encounter (the resolver is
stateless — that is the point of it), but it is paid on every single `take_action` that misses the guidance
cache.

## 2. The labelled corpus

33 cases across 10 families, in `RuleSelectionCorpus`. Every family is represented by the **contrast** that
makes it hard rather than by its easy case: inspect against open against take, give against drop against
steal, opening a way out against going through it, offering terms against accepting them, a threat against
a taunt, steadying a companion against merely talking to one. It includes compound intents, stale-ownership
intents, and genuinely unsupported ones.

**Required cards were identified by hand from the rules, not taken from what past runs cited.** Cited cards
are evidence of what a model reached for, not of what a decision turned on; scoring strategies against them
would measure agreement with the baseline's habits rather than correctness. Several labels require a card
the intent never mentions — `accept-surrender` requires the *theft* card, because ruling out a theft is how
you know the grab was an acceptance, and that is exactly the distinction a live v0.7 run got wrong three
times in one encounter.

Clarity distribution: **15 clear (45%)**, **13 ambiguous (39%)**, **5 unsupported (15%)**.

## 3. Measurements

The resolver is *modelled* rather than called: a perfect resolver constrained only by what it was shown —
right whenever the governing card was supplied, unsupported when it was not. That isolates the variable
under test (what selection costs in correctness) instead of measuring one model's mood, and it makes every
agreement figure an **upper bound**.

| Strategy | Required-card recall | Cases with every required card | Decision agreement | Wrongly unsupported | Avg cards | Avg resolver tokens | Avg selection tokens | Model calls | Fallbacks |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **WholeRulebook** (shipped, after §4a) | 100.0% | 33/33 | 33/33 (100%) | 0 | 21.0 | 5,260 | 0 | 1.0 | 0/33 |
| *Oracle (answer key — the ceiling, not a strategy)* | 100.0% | 33/33 | 33/33 (100%) | 0 | 4.3 | 1,913 | 0 | 1.0 | 0/33 |
| StructuredRouting (no declared family) | 100.0% | 33/33 | 33/33 (100%) | 0 | 21.0 | 5,260 | 0 | 1.0 | **33/33** |
| Embedding — trigram prototype, top-3 | 54.0% | 15/33 | 18/33 (55%) | 15 | 6.8 | 2,699 | 0 | 1.0 | 0/33 |
| Embedding — `nomic-embed-text`, top-3 | 90.9% | 28/33 | 29/33 (88%) | 4 | 7.2 | 2,458 | 0 | 1.0 | 0/33 |
| **CompactIndex** — `qwen3.5:9b` | **97.0%** | **31/33** | **33/33 (100%)** | **0** | 5.7 | **2,194** | 904 | 2.0 | 0/33 |

Token reduction against the baseline:

| Strategy | Resolver tokens | Resolver reduction | Total (selection + resolution) | Total reduction |
| --- | --- | --- | --- | --- |
| *Oracle (ceiling)* | 1,913 | 63.6% | 1,913 | 63.6% |
| Embedding — `nomic-embed-text`, top-3 | 2,458 | **53.3%** | 2,458 | **53.3%** |
| **CompactIndex** — `qwen3.5:9b` | 2,194 | **58.3%** | 3,098 | 41.1% |

Note what §4a did to this table. Against the *original* 8,202-token baseline CompactIndex cut total input by
53%; against the fixed 5,260 baseline it cuts 41%, because its own 904-token selection call is now a much
larger share of a much smaller total. Making the baseline honest took most of the prize off the table — which
is the correct outcome, and a reason to be suspicious of any retrieval benchmark whose baseline nobody
audited.

An earlier measurement put embedding top-5 at 92.4% recall for 49.8% total reduction — better recall than
top-3 and a materially worse saving, with the same decisions wrong. Top-3 is the better trade for embeddings,
and neither is good enough (below).

Latency: the compact-index selection step averaged **1.2 s** on a local 9B model. Embedding selection
averaged well under 100 ms and makes no chat call at all.

## 4a. The fix that was not a selection strategy at all

Before comparing strategies, the largest saving in this whole investigation turned out not to be one.

The resolver's schema used to ask it to copy the cited card's bindings, turn cost, RNG, visibility and
success/failure behaviours back into its reply — all of which the consultant was already holding. v0.8
replaced that with `RulebookConsultant.Hydrate`: the resolver answers with an action and a citation, and the
consultant fills those fields from the cited card itself. That was done to stop a card's *length* becoming
the resolver's *output* budget.

It had a consequence nobody planned. Once the consultant fills those fields, **the resolver is being shown
text it structurally cannot use.** Six of a card's ten fields describe what happens once an action has been
chosen; what the resolver does is choose. So `RuleCard.ToResolverBlock()` now renders only what an action IS
and IS NOT — summary, description, preconditions, exclusions — and the rest travels to the Dungeon Master by
hydration instead.

| | chars |
| --- | --- |
| Card text before | 31,245 |
| Card text after | **18,261** |
| Resolver request | 8,202 → **5,260 tokens** |
| Headroom in an 8k window | 127 → **2,809 tokens** |

**A 36% cut to the shipped path, with no model call, no recall risk, no configuration and nothing lost
downstream.** Required-card recall stays 100% by construction — every card is still sent — and a live probe
of eleven intents across every action family routed correctly afterwards, including two that had *failed*
before the cut because the model had no room to finish its reply.

The lesson generalises past this codebase: before optimising *which* documents a model retrieves, check
whether the ones you send contain anything it can act on.

A startup guard (`RulebookRequestBudget`) now refuses to begin a run whose rulebook cannot fit the
resolver's window with room to answer, because the failure is otherwise silent and looks like a card
authoring problem.

## 4. What each approach actually is

### Structured routing — correct, and not applicable here

It selects through declared `ActionName` metadata and reads no language whatsoever. That is its virtue and
its limit: it needs an action family the caller *already knows*, and the ordinary pre-adjudication
consultation is precisely the case where nobody knows it yet — working it out is the question being asked.
Configured for that case it correctly falls back on all 33, which is why its row reads identically to the
baseline.

It is not dead. It is the right mechanism for the on-demand shape below, where the Dungeon Master is already
holding a candidate action and wants the detail for it. Making it work pre-adjudication would mean inferring
a family from the intent's words, which is the class of heuristic this project keeps deleting.

### Embedding retrieval — cheap, fast, and wrong in the places that matter

With a real embedding model it reaches 59% token reduction and 90.9% recall for no chat call and negligible
latency. That is genuinely attractive, and the five cases it lost say why it is not enough:

- `accept-surrender` — "I take the purse out of Skrit's hand and tell him he can live". It retrieved the
  acceptance card but not the theft card, so the resolver would have had nothing in front of it to rule out
  a snatch. **This is the exact failure that cost v0.7 its release.**
- `use-item-vs-heal-ability` — drinking a salve retrieved the prayer alongside it and dropped the item rule.
- `steal-vs-attack-compound`, `intimidate`, `threat-with-a-blow`, `plain-coordination` — the same shape: the
  card the decision turns on is the one the intent does not talk about.

Raising top-K to 5 bought a point and a half of recall, lost the same decisions, and gave back nine points
of the saving. The failure is not a tuning problem: an embedding measures what an intent *resembles*, and what a
decision needs is often the rule it must be *distinguished from*. Declared related-rule links help — they
are what rescue `open-and-flee-compound` — but they cannot rescue a card nothing in the intent points at.

The trigram prototype row is included as a floor, and as evidence that the plumbing (top-K, link expansion,
confidence fallback) works. It is a lexical-overlap measure, not a semantic one, and its 56% recall says
nothing about embedding retrieval in general.

### Compact semantic index — the one that works

One line per card (id, action, summary) is shown to a model, which returns the ids worth reading in full;
those cards are then expanded through declared links and sent to the resolver.

**97.0% recall, 31/33 cases complete, 100% decision agreement, zero incorrect supported/unsupported
decisions, zero fallbacks, and a 58.3% reduction in resolver input tokens.** The model still does all the
semantic work; it just reads 900 tokens of index instead of 5,300 of cards.

Both misses are cases where a card is required in order to RULE SOMETHING OUT rather than to act on:
`accept-surrender` (the theft card, to know the grab was an acceptance) and `plain-coordination` (the
steadying card, to know that passing information is not it). In both the *decision* was still right, and in
both the refusal or ruling would have been less well grounded than the baseline's. That is the shape of this
strategy's residual risk, and it is worth watching in the live comparison below: a summary line tells a model
what a card is *for*, and says much less about what it is *not*.

The honest cost is the second model call. Counting it, total input tokens fall by 41.1%, not 58.3%, and
wall-clock rises by ~1.2 s per uncached consultation on a local 9B model. That trade was clearly worth taking
against the 8,202-token baseline, which did not fit its window at all; against 5,260 with 2,809 tokens of
headroom it is a much closer call, and no longer urgent. A smaller, faster model for the index step is the
obvious lever and is untested.

### On-demand consultation — most promising, least evaluated

Give the Dungeon Master lean core rules and an explicit `check_rules` call, used only when detailed guidance
is genuinely needed. Its cost is `P(consult) × cost-per-consult`, and combined with structured routing (the
DM already knows which action it is considering when it asks) the per-consultation cost approaches the
oracle's 1,913 tokens.

**`P(consult)` is unknown and cannot be measured without live runs.** The corpus's clarity distribution is
the best evidence available: 45% of intents are clear-cut enough that a DM holding the core rules plausibly
would not need to look anything up. If that transferred exactly, the expected cost would be roughly
`0.55 × 1,913 ≈ 1,050` tokens — an 80% reduction on the fixed baseline. That number is a *projection from a labelled proxy*, not a
measurement, and the risk it hides is the one the whole architecture exists to prevent: a Dungeon Master that
does not know it needs the rulebook does not consult it, and the failure is silent. v0.6 made consultation
automatic precisely because the DM is not a reliable judge of its own uncertainty.

## 5. Correctness risks

1. **Silent omission is the only failure mode that matters.** A cheaper selection that drops a card produces
   a confident answer given without the rule that mattered. Every strategy here falls back to the whole
   bounded rulebook when it is not confident, and every selection is traced with its ids, its reasons, its
   expansions and its fallback — but a strategy that is *confidently wrong* never triggers its own fallback.
   That is what the embedding rows show.
2. **The distinguishing card is invisible to similarity.** Retrieval finds what an intent resembles;
   decisions often need what it must be told apart from. Declared related-rule links are the mitigation and
   they are hand-written, so they are only as good as the links somebody thought to declare.
3. **The upper-bound caveat.** The modelled resolver is right whenever it was shown the right card. A live
   resolver will be worse everywhere, and possibly *differently* worse under a small card set than under the
   full book — a resolver shown five cards may over-commit where one shown twenty-one would have hedged.
   Not measured.
4. **A second model call is a second thing that can fail.** Handled (any failure falls back), but it doubles
   the provider-error surface of the rulebook stage.
5. **Corpus size.** 33 cases. One case is three points of recall. These figures rank the approaches; they do
   not pin any of them to a decimal place.

## 6. Recommendation

**Take the §4a saving, which is already shipped and default. Keep `WholeRulebook` as the default. Treat
CompactIndex as a ready option rather than an urgent one.**

That is a change of emphasis from this document's first draft, and the reason is that the baseline moved. A
36% cut with no model call, no recall risk and no configuration beats a 41% cut that costs a second call and
two points of recall — and the two are not additive in any interesting way, because both are eating the same
waste.

Concretely, in order:

1. **Ship what is here.** The selection-only resolver block and the startup budget guard are on by default.
   `RulebookSelectionMode: CompactIndex` is configurable, traced, cached separately from the answer cache,
   and falls back to the shipped path on any doubt — ready if the book grows again.
2. **Watch the headroom, not the token count.** `RulebookRequestBudget` fails a run whose book cannot fit
   the resolver's window with room to answer. The number to keep an eye on is the 2,809 tokens of headroom,
   because that is what the next few cards will spend.
3. **Run both modes over the same seeded encounters** and compare adjudication outcomes end to end, with a
   real resolver rather than a modelled one. That is the measurement this document cannot make.
4. **Try a small model for the index step.** The selection task is "which of these 21 lines matter" — it does
   not obviously need a 9B model, and a 2B one would cut the 1.2 s and most of the 894 selection tokens.
5. **Then, and only then, consider on-demand.** It has the best cost profile and the worst failure mode, and
   it should not be attempted until there is live evidence about how often a DM knows it needs help.

Do **not** adopt embedding retrieval as the production path on these numbers. Keep `IRuleEmbedder` and the
Ollama implementation — they were worth building, they are measured, and embeddings may yet be the right
*first stage* of a two-stage selection — but a strategy that drops the theft card on an acceptance is
re-introducing the bug v0.7 was released to fix.

## 7. Is always consulting the rulebook still justified?

**Yes, and this investigation strengthens the case rather than weakening it.**

The v0.6 argument was that the Dungeon Master is not a reliable judge of when it needs the rules, so asking
it to decide would produce exactly the silent failures the architecture exists to prevent. Nothing here
contradicts that. What the numbers show is that the *cost* of always consulting was never really the
rulebook being consulted — it was the rulebook being sent whole. A compact index keeps consultation
automatic, keeps the resolver stateless, keeps the engine authoritative, and removes roughly two thirds of
the input cost that made "always" look expensive.

The question worth revisiting is not whether to consult every time. It is whether the *whole book* needs to
be in front of the resolver every time, and the answer to that is now measurably no.
