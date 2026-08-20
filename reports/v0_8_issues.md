# v0.8 issues — found and fixed

Problems discovered **and** resolved while building and live-testing v0.8. Anything still open, and anything
that was already understood from an earlier release, is deliberately not here.

Each entry is what went wrong, why, and what changed. Where a live run is named it is under
`src/ModelsAndMonsters.Web/runs/`.

---

## Rulebook stage

### 1. The whole rulebook stopped fitting the resolver's context window

**Symptom.** `20260820-154542Z-185858ad`: five of 57 consultations came back as malformed guidance, surfacing
four steps downstream as in-world refusals of perfectly ordinary actions.

**Cause.** The v0.8 cards pushed a whole-rulebook request to ~8,065 tokens against an 8,192-token window,
leaving ~127 tokens to answer in. The provider does not error on this — it generates until the window is full
and returns `FinishReason: length` with a handful of tokens.

**Fix.** `RuleCard.ToPromptBlock` became `ToResolverBlock`, rendering only what the resolver needs to *choose*
(header, what it is, preconditions, not supported) and dropping the six fields the consultant can fill in
itself. 31,245 → 18,261 characters; 8,233 → 5,383 tokens; headroom 127 → 2,809. Plus `RulebookRequestBudget`,
a startup guard that fails loudly if the book ever stops fitting again, and the output reserve right-sized
600 → 300.

**Note.** I first reported this as a pre-existing think-block problem. That was wrong; it was context
exhaustion, and the correction is the reason the guard exists.

### 2. Card length *was* the resolver's output budget (self-inflicted)

**Symptom.** Lengthening `combat.attack`'s RNG section from 99 to 255 characters truncated **8 of 16**
resolver replies in a probe.

**Cause.** The `guidance-v1` schema asked the resolver to echo the cited card's descriptive fields back. A
longer card therefore meant a longer required reply — card length and output budget were the same number, and
nothing said so.

**Fix.** `guidance-v2` asks only for `supported`, `candidateActions`, `citedRules` and `unsupportedReason`;
`RulebookConsultant.Hydrate` fills the descriptive fields from the cited card in code. Measured replies fell
to 69–132 tokens. Rule cards can now be written for clarity without a hidden cost.

### 3. Two new cards the resolver could not tell apart

**Symptom.** *"I catch Elara's eye and tell her to hold the line"* routed to `intimidate_character` — a threat
aimed at an ally.

**Cause.** `combat.intimidate` and `combat.steady-ally` are the same shape (speech aimed at one person) and
differ only in *which side* that person is on, which neither card led with.

**Fix.** Card authoring, verified by probe: both cards now open with `AIMED AT AN ENEMY` / `AIMED AT A
COMPANION`, cross-reference each other in their exclusions, and sit beside the rest of the combat family.

### 4. Guidance could smuggle in an action nothing cited supported

**Symptom.** A probe produced `candidateActions: ["give_item"]` citing `combat.attack`. Both lists passed
validation independently and the DM was handed `give_item` as its only tool.

**Cause.** `RuleGuidanceValidator` checked that candidate actions were real engine tools, and separately that
citations existed at the stated version. Nothing checked that the two had anything to do with each other, or
that a cited card had actually been *sent* for that request.

**Fix.** Citations are filtered first, to cards that exist at the cited version **and** were supplied for this
request; candidate actions must then be governed by a surviving citation. The two failure modes are reported
differently, because an invented tool name and a real name no citation supports say different things about
the resolver.

### 5. The resolver probe ignored the configured selection mode

**Symptom.** `--rulebook-probe` reported whole-rulebook routing regardless of `RulebookSelectionMode`.

**Cause.** The probe built its consultant by hand with no selector — a diagnostic quietly answering a
different question than the one asked of it.

**Fix.** `RuleSelectorFactory` is now the single mode → selector mapping, used by both `SimulationRunner` and
the probe, and the probe prints the mode and the cards it chose.

---

## Context and window arithmetic

### 6. History was summarised one step too early

**Symptom.** `20260820-182529Z-5d637297`: Elara's reply truncated at 225 tokens with a request of 7,967
against an 8,192 window — *after* summarisation had declared her history within budget.

**Cause.** Summarisation ran at turn **end**, so it measured the history alone. The next turn then appended
~1,679 tokens of fresh context — self-state, knowledge, pending narration — on top of a history that had just
been declared to fit. The arithmetic was correct and applied one step too early.

**Fix.** Summarisation moved to turn **start**, after the turn context is injected, so the budget is measured
against the request that will actually be sent. The trim boundary falls on the most recent user message, so
the current turn is always kept verbatim. Five tests in `HistoryBudgetTests`, mutation-checked.

### 7. `ContextWindow` was budgeted against on providers that never receive it

**Symptom.** `20260820-192134Z-9330757c` (gpt-5.4): **eleven history summarisations in six rounds** — roughly
one every other turn — each replacing verbatim history with a recap and paying a model call to do it.

**Cause.** Only Ollama takes a window as a request parameter. OpenAI and Anthropic fix theirs per model and
never see the configured number — `ChatOptionsFactory` drops it. Every agent had inherited
`ContextWindow: 8192` from the Ollama-tuned defaults, so the harness was compacting to fit a ceiling the
provider would never have enforced.

**Fix.** `AgentModelProfile.BindingContextWindow` — the configured window where the provider takes it, `null`
otherwise. History budgeting, the length-finish diagnosis and the rulebook startup guard all read it.
`Harness:EnforceContextWindowOnHostedModels` (default off) restores the old behaviour for a like-for-like
provider comparison. Runs announce the regime at startup and record `BindingContextWindow` per agent in
`run.json`.

### 8. The rulebook startup guard measured against a window that does not exist

Found while fixing #7, and the same bug pointing the other way: `RulebookRequestBudget` read the configured
`ContextWindow`, so a hosted resolver inheriting 8,192 would have been measured against a ceiling its provider
never applies — **refusing to start a run** the model would have answered without difficulty. Its own summary
already said it should skip unknown windows; it just read the wrong field. Now reads
`BindingContextWindow`.

### 9. Reasoning written beside a tool call persisted for the whole run

**Symptom.** `PostResolutionOutputDiscarded` on roughly a third of turns, median 310 characters, max 1,136.

**Cause.** With reasoning off, a model thinks on the page and providers put that text and the tool call in
**one assistant message**. `CompactTurn` kept the message whole because it "carries a tool call", so every
turn's deliberation rode along for the rest of the run in a history the summariser then paid to compress.

**Fix.** The prune keeps the calls and sheds anything else the message carried, rebuilding only when there is
something to shed. Deliberately **not** suppressed at the source: that paragraph is how a non-reasoning model
reaches its decision, and forbidding it in the prompt would be asking the model to think less.

### 10. The adjudication output cap was applied where its justification does not hold

**Symptom.** Three adjudications truncated across two Haiku runs, each part-way through checking preconditions
— one of which shipped a `reject_action` off interrupted reasoning.

**Cause.** `AdjudicationOutputTokens = 400` exists for one reason: on Ollama `num_ctx` covers input and output
together, so reserving the DM's narration-sized allowance takes that room from the request. A hosted provider
budgets output separately from a far larger input window, so the reservation costs the request nothing and the
cap buys nothing — the same local-accommodation-applied-globally mistake as #7.

**Fix.** `AdjudicationOutputBudget` reads `BindingContextWindow`: the tight cap on a shared window, the agent's
own `MaxOutputTokens` otherwise. Measured after (`20260820-213230Z-f227bcaa`): truncations 1 → **0**, mean
adjudication latency **3.37s → 3.35s**, mean output 283 → 308 tokens. Only 2 of 16 adjudications used the new
room — the cap was binding on exactly the minority that needed it most.

---

## Model and provider behaviour

### 11. An Ollama 500 on a malformed tool call killed a run

**Symptom.** `20260820-195930Z-d78984aa` died at round 5. Ollama's own tool-call parser rejected qwen's XML
(`element <function> closed by </parameter>`) and returned **HTTP 500** three times running
([ollama#14834](https://github.com/ollama/ollama/issues/14834), open).

**Cause — two independent defects.** The run contained its own control group: Elara (temperature 0.8) took two
500s and recovered on the third attempt, while the IntentParser (temperature 0, retried at the old flat 0.1
floor) 500'd three times identically. **0.1 is barely off greedy** — the seed moved but the distribution did
not, so the model reproduced the same malformed call. And the intent parser, an *optional* component, had no
failure path at all: the exception unwound through the turn, the round loop and the run.

**Fix.** The retry temperature floor now **escalates** (0.4, then 0.8); attempt 1 is untouched so a clean run
still replays identically. `ParseProseIntoCallsAsync` returns null on failure and the turn falls back to the
nudge path — the mechanism every character used before the parser existed. Confirmed live in
`20260820-203006Z-59f24a04`: two IntentParser 500s, recovery, run completed 11 rounds.

Escalating also varies the *request*, which matters beyond sampling:
[ollama#17825](https://github.com/ollama/ollama/issues/17825) records that re-sending an identical request
after such a 500 could wedge the server on a poisoned prompt cache (fixed in
[#17883](https://github.com/ollama/ollama/pull/17883)) — reproducible only with thinking enabled, which is why
this harness, running reasoning off everywhere, has never seen it.

### 12. A retry was not identifiable as a retry in the trace

Found while diagnosing #11: the resolved sampling options *were* always recorded (under `RequestedOptions`),
but nothing marked a call as a re-send, so telling one from a fresh call meant knowing that the seed is offset
by the attempt number. `ModelRequest` and `ModelError` now carry `Attempt`. Separately, the derived agents
(`IntentParser`, `HistorySummariser`) were absent from `run.json`'s profiles entirely — they are not
configured directly, which is exactly why nothing else stated what they ran with. Both now recorded, and both
gained an `AgentIdentifier` constant instead of a bare string literal in two places each.

### 13. Serialised speech arrays leaked their brackets into the room

**Symptom.** `20260820-203006Z-59f24a04`: two spoken lines delivered as
`["Skrit, hold your line — ..."】` and `["Vark, take this!"`.

**Cause.** `utterances` is declared as an array, but qwen's tool-call wire format is XML, which has **no array
type** — every value arrives as text that merely looks like an array, and whether it is well-formed is down to
the model. An existing repair required a literal `]` to be present. The first case closed with **】 (U+3011)**,
a CJK lenticular bracket the quantised model reached for instead; the second never closed at all.

**Blast radius.** Speech is delivered verbatim by design and lands in every listener's history, so a round-5
line was still in the Dungeon Master's context in round 11.

**Fix.** The signal is now how the value **opens**, not whether it closes: recover the quoted spans regardless
of the closer (including the full-width and lenticular lookalikes), and if nothing is quoted, strip the
scaffolding rather than speak it. This is protocol repair, not language parsing — it never inspects the words
between the quotes.

---

## Engine and rules

### 14. An ambiguous item reference resolved to whichever item came first

**Symptom.** `20260820-190215Z-5f73ad04`: the Dungeon Master spent its entire 400-token adjudication budget
deliberating in prose — *"But which purse? The intent doesn't specify"* — and produced no tool call.

**Cause.** Vark carried two items both displaying as "Small Purse of Gold Coins", and Rowan tried to steal
"Vark's purse". Containers, objects and exits all reported ambiguity so the engine could refuse it;
**character inventories did not** — they ran a chain of first-match lookups and silently returned whichever sat
first. The model had diagnosed the problem correctly; the engine gave it no way to say so.

**Fix.** `ItemReference.Resolve` is the one rule for inventories and containers: id, then qualified display
name (what keeps duplicates referenceable at all), then plain name, which is where ambiguity is reported. Five
engine sites refuse instead of guessing, and the refusal **lists the qualified alternatives** — a bare "that is
ambiguous" hands a model back the same words it just used.

### 15. The Dungeon Master deliberated over references the engine resolves leniently

**Symptom.** `20260820-204809Z-6803966f`: Haiku burned an adjudication reasoning about *"Vark does not carry
'the small purse of gold coins' — Vark carries 'Small Purse of Gold Coins (the captain's)'"*.

**Cause.** With one purse present that reference resolves cleanly, but nothing told the DM so, and it treated a
string mismatch as a possible precondition failure.

**Fix.** Two additions to `dungeon-master.rules-adjudicate.md`: *"a difference between what they called
something and what the snapshot calls it is never by itself a reason to refuse"*, and an explicit
**rule-directly** instruction (*"your reply is the tool call, not your working"*). Adjudication truncations per
adjudication fell from ~20% to ~5%, and to zero once #10 landed.

### 16. Speech spent on a refused action left a character mute

**Symptom.** Three of four harness limits in `20260820-203006Z-59f24a04` were `MaxSpeechActsPerTurn`.

**Cause.** Not chattiness — **the retry path**. A character speaks as part of an action attempt, the DM refuses
the attempt, and the retry has nothing left to say. Skrit said *"Elara, this spear isn't going to hurt you!"*
with a compound action that was refused, reworded the action, and had *"Eat this!"* swallowed.

**Fix.** The words that went with the failed attempt were really said and cannot be given back, so the honest
fix is to allow the second breath a second attempt implies: `MaxSpeechActsPerTurn` is now **2**. Zero
occurrences across the two runs since.

### 17. A world rule was being reported as a harness limit

Found alongside #16. Exceeding the speech allowance emitted `HarnessLimitReached`, which is otherwise reserved
for guards against a misbehaving model (round caps, call caps, attempt caps). A character running out of breath
is the fiction working — filing it under the former made a well-behaved run read as a troubled one. It now
emits **`SpeechNotHeard`**, carrying the words that were not delivered so a report never implies the character
chose silence.

---

## Reporting

### 18. The morale summary listed only characters whose fear had moved

**Symptom.** A four-character run's morale table showed two characters.

**Cause.** `FinalFearByName` read `Characters` from the root of `final-state.json` instead of under its
`State` wrapper, the way every other reader in the file does. It returned nothing, and because the table takes
the **union** of "had a fear event" and "is in the final state", the failure was invisible — the table still
rendered, just narrowed to whoever broke. The test fixtures carried the same wrong shape, which is why nothing
caught it.

**Fix.** The unwrap, plus fixtures that wrap state the way a real artefact does, plus a test asserting all four
characters appear.

### 19. Quality draws were rendered against a meaningless number

**Symptom.** `*[attack.quality-check: rolled 99 vs 25 → critical]*`.

**Cause.** The transcript rendered every RNG draw as `RawRoll vs Threshold`. For a quality draw `Threshold` is
`GlancingBlowChance` — the top of the band at the **opposite end of the roll** from the one that won.

**Fix.** Every draw already authors its own `Comparison`; the transcript renders that instead. And
`DescribeQualityComparison` now names the band with its range: `roll 99 in critical band 76-100`. The
transcript half applies retroactively to existing traces; the band phrasing is written at run time, so only
new runs get it.

### 20. The surrender section still said "in v0.7"

A version reference in report prose, which dates every run artefact that renders it. Removed — it was the only
one in report prose; the rest are code comments, where they are accurate history.

---

## Process

### 21. A green test suite hid a feature that had been removed

While moving summarisation to turn start (#6), a patch script aborted midway: it removed the end-of-turn call
without adding the turn-start one, leaving summarisation reachable only through the context-reclamation path.
**The full 869-test suite passed**, because nothing covered the ordering. It was caught by a compile error in a
new test, not by the suite.

The lesson kept: after that, every fix in this session was **mutation-checked** — revert the change, confirm
the new tests fail, restore. Twice during this session a mutation script failed to apply cleanly and reported a
passing suite that proved nothing; both were caught by verifying the mutation actually landed before trusting
the result.
