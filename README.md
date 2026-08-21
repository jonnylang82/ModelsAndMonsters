# Models & Monsters — v0.9

A small experimental harness for autonomous LLM characters interacting inside a deterministic fantasy
world through an LLM Dungeon Master.

This is a technical proof of concept, not a game. It proves one architecture:

```text
Character ──intent──▶ Rulebook Resolver ──guidance──▶ Dungeon Master ──tool call──▶ Game Engine
```

The characters decide intent, in natural language, through four tools (`ask_dm`, `say`, `take_action`,
`end_turn`) and never see an engine action. A stateless **Rulebook Resolver** reads the intent against the
(small, whole) rulebook of versioned rule cards and returns structured guidance — which action(s) it could
be, and under what rules — without ever seeing live game state. The **Dungeon Master** binds that guidance to
the authoritative snapshot and calls exactly one narrowed engine tool, or rejects the attempt in-world.
The **engine** is the only source of truth: it validates, resolves (rolling any dice itself), and reports
back what actually happened. Nothing a model says is ever trusted as fact.

The project has grown release by release from a single 1v1 duel (v0.1) into the current slice:

- **v0.2** — multi-actor orchestration: per-character agents with isolated histories, teams, a team
  terminal condition, fixed turn order with dead-actor skips, unambiguous targeting by stable id.
- **v0.3** — public speech (`say`) and a contested container (open/take), so characters can coordinate
  or lie to each other out loud.
- **v0.4** — hidden information: character-specific knowledge separate from authoritative state, close
  inspection, and a strict first-hand-vs-hearsay-vs-undiscovered boundary the DM must respect.
- **v0.5** — non-lethal outcomes: a character `Disposition` (Active/Surrendered/Escaped/Dead), a room
  exit, and `surrender`/`open_exit`/`escape_encounter` — so a team can win an encounter without every
  opponent dying, decided entirely through in-character choices and public speech, never a mechanic.
- **v0.6** — inventory transfers (`give_item`/`drop_item`/`steal_item`, the last with one seeded RNG
  roll and always publicly noticed; both take and steal gated by an authoritative informational-basis
  check) and the **Rulebook Resolver**: the DM's per-action rules moved out of its system prompt into
  small versioned rule cards, the whole (small) set sent to a stateless resolver per intent, so the DM's
  own prompt stays a compact "constitution" instead of growing with every new action.
- **v0.7** — **terms and tactics**: surrender becomes *negotiated and costly* (a concrete offer to one
  named opponent, and their acceptance, atomically enforced) rather than a unilateral declaration; a small
  data-driven **ability** slice (Guard Ally, Healing Prayer, Rally Grunt, Dirty Strike, and `defend` for
  everyone) with authoritative, mechanically meaningful **status effects**; and a deterministic
  **`AnswerFacts` projection** that stops the DM answering questions from state the asking character
  cannot perceive. See [Negotiated surrender](#negotiated-surrender-terms-not-declarations),
  [Abilities and status effects](#abilities-and-status-effects) and
  [Grounded answers](#grounded-answers-answerfacts).
- **v0.8** — **morale and combat volatility**: an encounter-scoped `Fear` value (0–5) with a public
  `Scared` status at 3, moved only by the engine and only for stated reasons; `intimidate_character` and
  `steady_ally`, two actions made of speech aimed at one named person; and **critical hits** as the
  opposite end of the same single quality draw that already produced glancing blows. Fear exerts pressure
  and never chooses — a scared character still has to make a real surrender offer or open a real door. Also
  a measured investigation into the Rulebook Resolver's token cost, in
  [`reports/rulebook-efficiency.md`](reports/rulebook-efficiency.md). See
  [Morale: fear, threats and steadying](#morale-fear-threats-and-steadying) and
  [Attack quality](#attack-quality-one-draw-three-outcomes).
- **v0.9** — **environmental cover**, the final planned feature phase: one authoritative cover object (an
  overturned mill workbench) that a character can take, hold and lose, with exclusive capacity, a
  hit-chance modifier, and its own durability. `take_cover` and `leave_cover` are explicit, RNG-free
  actions; every other action that reaches toward someone or something is structured metadata
  (`GameAction.ExposesActor`) that vacates cover as a side effect of resolving, never a second turn. An
  attack against a covered character makes exactly the one existing hit-check roll, which the engine alone
  resolves into a direct hit, a **cover interception** (the cover, not the roll, saved the target — one
  point of durability lost, no quality draw, no character damage), or an ordinary miss the cover had
  nothing to do with. `damage_environmental_object` lets a character batter the cover itself down instead
  of the person behind it. Reducing durability to zero destroys the object — which stays in the room as
  wreckage rather than disappearing — and exposes whoever was sheltering there. See
  [Environmental cover](#environmental-cover).

**New in v0.8: [`docs/architecture.md`](docs/architecture.md)** — the structural companion to this file.
It maps the components and the five model-driven agents, walks a character's turn end to end through every
stage it passes (with diagrams), lists every configuration lever there is, and names the AI techniques the
project uses with a pointer to where each one lives. Read it first if you are new to the codebase; this
README explains *why* things are the way they are, that document explains *what* they are.

See **[Version history and where to look](#version-history-and-where-to-look)** near the end of this
file before starting work in an unfamiliar part of the codebase — it points at the original spec,
field notes and known-issues doc for each release.

## No Language Parsing for Game Semantics

Do not use regexes, keyword lists, verb lists, phrase matching, or other hand-built natural-language parsing to infer character intent, speech, actions, rules, knowledge, targets, or outcomes.

Semantic information must be supplied explicitly through structured model output and validated against authoritative game state. When a run exposes a new phrasing, fix the structured contract or prompt—not by adding another synonym or pattern.

Text matching is permitted only for:

- validating syntax or exact identifiers;
- formatting cleanup;
- high-confidence defensive lint and telemetry.

It must never determine game semantics or mutate authoritative state. Any unavoidable legacy fallback must be isolated, traced, non-authoritative, and documented for removal.

## Running it

Requires .NET 10 and, by default, a local [Ollama](https://ollama.com) with the model in
`appsettings.json` pulled (`qwen3.5:9b` by default — a reasoning model, run with `Effort: "none"`).

```bash
dotnet run --project src/ModelsAndMonsters
```

Override any setting from the command line, for example a shorter run:

```bash
dotnet run --project src/ModelsAndMonsters -- --ModelsAndMonsters:Harness:MaxRounds=2
```

Tests never touch a model service:

```bash
dotnet test
```

A run is long enough to want backgrounding, so **check how it ended rather than assuming**: the last line
is either `Run complete.` or `Run failed: …`. Both write `report.md` and `report-summary.md` (a failed run
is still evidence), and only a completed one writes `final-state.json` — so the presence of reports does
not mean the run finished. The failure to expect is a provider 5xx that outlives the retry budget; see the
transient-retry note in [Version history](#version-history-and-where-to-look).

```bash
tail -1 run.log   # "Run complete." or "Run failed: ..."
```

## Configuration

`src/ModelsAndMonsters/appsettings.json` configures each agent independently. Switching an agent
between Ollama, OpenAI and Anthropic is a configuration change only — no agent or orchestration code moves.

Every agent — the Dungeon Master, the four characters, and (since v0.6) the Rulebook Resolver —
resolves its own profile. `Default` holds the common baseline; the Dungeon Master, `RulebookResolver`
and each character (keyed by character id) override only the fields they set. Nothing hard-codes a
single shared hero or monster profile, and the resolved profile actually used for each agent's calls
is recorded in `run.json`.

```json
"Agents": {
  "Default":         { "Provider": "Ollama", "ModelId": "qwen3.5:9b", "Temperature": 0.8, "TopK": 40, "ContextWindow": 8192, "Effort": "none" },
  "DungeonMaster":    { "Temperature": 0.2, "MaxOutputTokens": 1700 },
  "RulebookResolver": { "Temperature": 0.1, "MaxOutputTokens": 600 },
  "Characters": {
    "hero-rowan":   {},
    "hero-elara":   {},
    "goblin-vark":  {},
    "goblin-skrit": {}
  }
}
```

An empty character entry inherits `Default` entirely; add any field to configure that one character
independently. Sampling options a provider cannot honour (for example `TopK` on OpenAI) are dropped
rather than silently sent, and the drop is recorded in the trace.

### Models that won't call tools

Small models sometimes understand the protocol but write the call as prose — `` `take_action(I strike
the goblin)` `` — instead of emitting a structured tool call. Measured cause: this harness's immersive
character prompt tips prose-prone models (e.g. `gemma4:e2b`) into pure prose; the same model calls
tools fine under a terse prompt. Two levers, both off by default so raw tool-calling stays observable:

- **`Harness:RecoverTextToolCalls`** (works everywhere, including native Ollama): when a reply has no
  structured call, the harness parses a `take_action(…)` / `ask_dm(…)` / `end_turn(…)` written as prose
  and dispatches it, traced as `ToolCallRecovered` (the raw prose is kept, and a per-agent "Recovered"
  count appears in the report) so it never hides what the model did.
- **Per-agent `ForceToolChoice`** (`ChatToolMode.RequireAny`): tells the provider the model *must* call a
  tool. Ollama's native `/api/chat` **ignores** this (measured), so it's reported as dropped; its
  OpenAI-compatible `/v1` endpoint honours it — force a local model by configuring it as an OpenAI
  provider (`Provider: OpenAI`, `Endpoint: http://localhost:11434/v1`) with `ForceToolChoice: true`.
- **`Harness:UseIntentParser`** (default on): the strongest of the three, and worth reaching for first
  on a prose-prone model. A small model often fuses several intents into one reply — narration, a
  spoken line, *and* an action in one breath ("I step forward. 'Elara, stay close,' I say.") — which
  the two levers above can only recover one call from at a time, so a fused reply just re-fails and
  re-nudges. `IntentParser` is a stateless, zero-temperature agent that reads that prose and returns
  the **several** structured calls it implies (`say` *and* `take_action*` from one reply), dispatched in
  one pass — measured to cut calls-per-turn from 3.00 to 2.12 on a comparable run, and to remove the
  nudge loop that was the largest single filler of a small model's context window (see "Per-turn
  compaction" below). When it finds nothing callable, the whole reply falls back to one `take_action` so
  the turn still progresses. Every parse is traced (`IntentParsed`, prose in → calls out) so it stays
  auditable against what the model actually wrote.

Neither the two harness-side levers nor the intent parser help a model that calls tools but only
*postures* (describes stances instead of striking) — the Dungeon Master correctly rejects non-actions as
unsupported, so that is a roleplay-decisiveness issue, not a tool-calling one.

### Context window: why it's limited, why 8192, and what keeps requests inside it

Set `ContextWindow` per agent. On Ollama it is sent as `num_ctx` — the model's whole working memory
for one request, input and output tokens together. It is not free to raise: `num_ctx` sizes the
model's KV cache, which is allocated up front and lives in GPU memory alongside the model weights. Ask
for more than fits and Ollama doesn't fail — it silently spills part of the model onto the CPU, which
is much slower and gives no error to say it happened. So the window is a real three-way tradeoff — fits
the request vs. sits fully on the GPU vs. runs at native speed — not a number to raise reflexively when
something looks truncated. The measurements are in the next paragraph.

**8192 is not an arbitrary default — it is the largest window that keeps the model wholly on the GPU on
this project's dev machine, and every request is sized to fit inside it.** Measured on an 8 GB card with
`qwen3.5:9b`, via `ollama ps`:

| `num_ctx` | Resident | On GPU |
| --- | --- | --- |
| 8192 | 5.63 GB | **100%** |
| 10240 | 6.35 GB | 88% |
| 12288 | 6.42 GB | 88% |

**There is no middle ground here: the spill cliff sits between 8192 and 10240, and 10240 costs exactly the
same 12% as 12288.** So widening the window is not a small concession on this hardware — it is the whole
concession, for no more room than the next step up. That is why the rule on this project is to shrink the
request, and why v0.7 went looking for bytes rather than tokens when its prompts grew.

v0.7 nearly broke that. A full 12-round run measured its largest `dm.adjudicate` request at ~25,900
characters (≈6,500 tokens) — with the DM's 1,700-token output reserve, past what 8192 can hold. Raising the
default to 12288 was tried, and reverted once the residency above was measured. Three fixes brought it back
under, and the breakdown is worth keeping because the intuitive culprit was the wrong one:

| Block of `dm.adjudicate` | Before | Behaviour |
| --- | --- | --- |
| System prompt (core + adjudication rules) | 9,328 | flat |
| Authoritative state | 8,544 | flat |
| **Asking character's knowledge view** | **759 → 5,529** | **grew every round** |
| Rendered rulebook guidance | ~2,500 | flat |
| Intent + tool schemas | ~800 | flat |

The **rule cards are not in there at all** — those go to the Rulebook Resolver, which is a separate
stateless call. The growth was the character's knowledge view, rendering its whole lifetime ledger. So:

1. **Bounded both knowledge projections** to the most recent facts and hearsay
   (`CharacterKnowledgeView`), stating how many were omitted. This restores the property the DM projections
   were always supposed to have — a per-call size independent of run length.
2. **Cut the state block's notes from 4,877 characters to ~1,100** by deleting what duplicated the DM's own
   constitution. Both blocks are in the *same request*, so the world's rules were being stated twice per
   call; the state block now explains only how to read the snapshot.
3. **Gave adjudication its own output budget** (400 tokens, not the DM's 1,700). Its entire reply is one
   tool call; on Ollama the window covers input and output together, so a prose-sized reserve was holding
   back a fifth of the window from the call with the largest input.

Result, measured on a fresh run at 8192: `dm.adjudicate` peaked at **5,437 reported input tokens** (from
~6,500) with **zero** `ContextWindowSaturated` events — and the tightest fit turned out to be somewhere
else entirely.

#### The tightest fit is not always the one you last changed

With the DM fixed, the same 8192 run still truncated five replies — all of them **characters**, peaking at
**8,160 reported input tokens** against an 8,192 window. `dm.adjudicate` had simply stopped being the
biggest call. Breaking down the largest `character.decide` request (33,139 characters) settled it:

| Block | Chars |
| --- | --- |
| System prompt (four tools, persona, allies, all the standing guidance) | 14,835 |
| Turn context — full self-state, knowledge and narration — **once per kept turn** | 4,250 **× 3** |
| The model's own replies retained in history | ~5,150 |
| Running recap + tool schemas | ~1,900 |

The self-state grew in v0.7 (abilities with their descriptions, live statuses, offers on the table), and
`RecentTurnsKeptFull` was re-sending **two superseded copies of it** on every call. It is now **1**: the
newest turn context supersedes the older ones by definition, and what happened in them survives in the
running recap. Together with a compression pass over the v0.7 additions to the character prompt, that took
the peak character request from 8,160 tokens to comfortably inside the window.

The general lesson, which has now bitten twice: **measure which call is biggest before optimising the one
you just changed.** Both times the intuitive culprit (rule cards; the DM's constitution) was flat, and the
real growth was a projection quietly rendering a whole ledger or a whole history.

Verified on a full 8-round run at the shipped defaults (`qwen3.5:9b`, `ContextWindow: 8192`,
100% GPU-resident), largest reported input per call type:

| Purpose | Peak input tokens | Headroom in 8192 |
| --- | --- | --- |
| `character.decide` | 6,661 | ~1,500 |
| `rulebook.resolve` | 5,996 (bounded, 600-token output cap) | ~1,600 |
| `dm.adjudicate` | 5,356 (400-token output cap) | ~2,400 |
| everything else | ≤ 4,569 | — |

**Zero** `ContextWindowSaturated` events and **zero** truncated replies for the whole run.

When a request *does* exceed the window, Ollama drops the oldest messages and reports only the
post-truncation size in `prompt_eval_count` — nothing in the response says it happened. The
application believes it owns the whole conversation while the model is shown less than was sent. Some
builds truncate at half the nominal window, so trusting the configured number is not enough.

The harness detects this by comparing an estimate of what it sent against the input size the response
reports (the method from ["What the model saw"](https://spencerclark.dev/blog/what-the-model-saw/)):
when the reported size falls well below what was sent, it raises `ContextWindowSaturated` in the trace
and warns at the end of the run. This needs no configured window and catches half-window truncation.
**This is the signal to check before touching `ContextWindow` at all** — count how many saturations a
run logged and which `Purpose` they're on (`dm.adjudicate`, a character's `character.decide`, …); that
tells you what's actually too big, which is almost always a fixable prompt, not a genuine need for
more room.

#### What actively keeps requests bounded, so raising the window is rarely the fix

Four separate mechanisms exist specifically so no request has to grow without limit as a run gets
longer. If a request is still too big, one of these has a gap — that's what to go fix, not the window:

1. **Dungeon Master projections** (below) — every DM task runs on a *fresh* context (system prompt +
   only that task's input), never one conversation accumulating across the whole run. This keeps a DM
   call's size flat regardless of how many rounds have played.
2. **Per-turn compaction** (`AgentConversation.CompactTurn`) — once a character's turn resolves, its
   history is pruned back to only the clean tool calls and their results; the failed prose replies and
   nudges it took to get there are dropped (they stay in `trace.jsonl`, just not in the model's
   context). Runs on every turn, no configuration needed.
3. **History summarisation** (`Harness:SummariseHistory`, default on) — once a character's history
   exceeds its budget, everything before the last `Harness:RecentTurnsKeptFull` turns (**1** since v0.7,
   for the measured reason above) is folded into one running first-person recap by a small stateless
   summariser call, replacing many old turns with one short paragraph. This is what actually keeps a *character's* context flat across a long
   fight — compaction alone (above) still accumulates one clean exchange per turn forever. **The budget is
   derived from the context window, not a fixed number** (v0.6 fix): the message-token estimate omits the
   tool schemas and chat-template scaffolding a tool-bearing call carries — ~2000 tokens on qwen — so
   trimming to a flat `HistoryTokenBudget` (5500) once left the *real* prompt at ~7600 in an 8192 window
   and truncated replies anyway. It now trims to `min(HistoryTokenBudget, ctx − MaxOutputTokens − overhead
   − safety)` (`ContextTruncation.EffectiveHistoryBudget`), where `overhead` is *learned per model* from
   the gap between the provider's reported input size and the estimate (`ModelAgent.ObservedPromptOverheadTokens`).
   So it reserves real output room and self-calibrates; `HistoryTokenBudget` is now an upper cap (raised to
   9000 in v0.7 — at 8192 the derived figure wins anyway, so the cap should not be what binds on a larger
   window). Hosted models (no per-request window) keep the configured budget. Note what this mechanism
   cannot do: it trims *history*, so it never shrinks the fixed cost of a system prompt or a turn context —
   which is why v0.7's character-side fix was `RecentTurnsKeptFull`, not the budget.
4. **The Rulebook Resolver is stateless and its request is constant** — its input is only the current
   intent plus the rule cards, never how many rounds have played. **It sends the *whole* rulebook every
   call** (v0.7: 18 small cards, ~24KB of card text, measured constant to within a character across a
   run), with no keyword routing selecting a subset (see below for why), so `Harness:RulebookMaxCards`
   (32) and `Harness:RulebookMaxInputChars` (32000) are a **hard ceiling that throws at startup** if the
   catalog outgrows a bounded request — not a trimming budget. See [Inventory transfers and the Rulebook
   Resolver](#inventory-transfers-and-the-rulebook-resolver).

None of these shrink a *single, one-off* request that is simply too large on its own — a big system
prompt, or a state block that grows with more characters/objects/exits in the room. That's a fixed
cost paid on every call regardless of how long the run has been going, and it's what actually pushed
this project's default window in the past: v0.5 grew the DM's system prompt (new exit/disposition
rules) until one `dm.adjudicate` call needed more than 8192 tokens on its own (a 12-round run logged
**75 saturations, all on that one call type**, at the then-current window). The fix was to shrink that
one prompt — split it into a small shared core plus a rules block loaded only for the job at hand, and
trim the adjudication rules themselves — which brought the same call back under 8192 with headroom
(verified: 75 saturations → 0, same run, same seed, same window). v0.6 goes further still and moves
the per-action rules out of the DM prompt entirely, into rule cards the Rulebook Resolver retrieves a
few at a time, so the DM's own prompt no longer grows as actions are added at all.

### Truncation has two causes, and they want opposite fixes (v0.7)

A reply that finishes with `Length` was cut off either because it **spent its output budget** or because the
**input had already filled the window**, leaving nothing to generate into. These want opposite remedies, and
the harness reported the wrong one for four runs — advising "raise `MaxOutputTokens`" against usage that read
`input 8152 + output 40 = 8192`, exactly `num_ctx`. On a shared window, reserving more output leaves *less*
room for the prompt that was already too big.

- `ContextTruncation.WasContextExhausted` tells the two apart from usage, and the console gives the matching
  advice. **Believe the usage numbers, not the label.**
- On a context-full truncation the harness now **reclaims** room before re-asking — sheds the turn's failed
  prose, folds older turns into the summary, traces `ContextRoomReclaimed` — because appending a nudge to a
  request that had no room guarantees the retry fails the same way. (Measured before the fix: three
  consecutive failed retries on one turn.)
- **Not every provider reports a finish reason.** An OpenAI run returned 109 responses with `FinishReason`
  null on every one, which left the finish-reason check permanently false. Detection falls back to usage when
  no reason is given.
- **A third cause looks like the first and is neither (v0.8).** The DM's adjudication runs on a small
  400-token cap (`AdjudicationOutputTokens`) **where the window is shared**, because every output token
  reserved is an input token surrendered on the call carrying the largest input. A live qwen run hit that cap
  with 1,500 tokens of window still free, having spent all 400 on visible chain-of-thought and produced no
  tool call. There, raising it would not help — a model thinking out loud fills whatever it is given — so the
  retry path handles it instead: a toolless truncated reply becomes a tool error, and the re-ask recovered in
  63 tokens. **On a hosted provider the cap does not apply at all**: output is budgeted separately from a far
  larger input window, so the reservation costs the request nothing and the cap buys nothing while a
  truncated deliberation costs a worse ruling. Two Haiku runs truncated three adjudications between them,
  each part-way through checking preconditions. `AdjudicationOutputBudget` reads
  `BindingContextWindow` — the same signal as the history budget.
- The **character system prompt is ~45% of every character request**, sent every turn to every character.
  Check its size before anything else: it grew 28% across v0.7 and jammed `character.decide` against the
  ceiling until it was compressed back.

**Checklist before raising `ContextWindow`:**

1. Run with the window you have and check `ContextWindowSaturated` counts and their `Purpose` in the
   trace (or the run's warning at the end).
2. If it's one `Purpose` saturating on every/most calls → that request is oversized on its own regardless
   of run length. Look at what's actually in it (a rendered system prompt template, or the state block
   in `WorldStateFormatter`) and shrink *that*, the same way the v0.5→v0.6 DM prompt work did.
3. If it's a character and it grows worse deeper into a run → check `Harness:SummariseHistory` is on
   and `HistoryTokenBudget` is sane; that mechanism exists precisely to prevent this.
4. Only if neither applies — the content genuinely needs to be that large (e.g. a much bigger
   scenario, many more characters or rule cards) — raise `ContextWindow`, and then re-check with
   `ollama ps` whether the model still fits fully on the GPU at the new size before calling it done.

### `ContextWindow` is a local-model setting, and only binds where the provider takes it (v0.8)

**Only Ollama receives a context window.** It takes `num_ctx` per request; OpenAI and Anthropic fix their
window per model and never see the number — `ChatOptionsFactory` drops it and reports it dropped. So a
`ContextWindow` inherited from `Agents:Default` is real on a local run and **a fiction on a hosted one**.

The fiction was not harmless. A gpt-5.4 run with every agent on the Ollama-tuned `8192` ran **eleven history
summarisations in six rounds** — roughly one every other turn — each replacing what characters actually said
and did with a recap, and each paying a model call, to fit a ceiling the provider would never have enforced.
Nothing said so: the setting was dropped from the request and forgotten.

- `AgentModelProfile.BindingContextWindow` is the window that **actually bounds a request** — the configured
  one where the provider takes it, `null` otherwise. History summarisation, the length-finish diagnosis and
  the rulebook startup guard all read this; `ContextWindow` stays as configured so `run.json` still records
  what was asked for.
- The startup guard had the same bug in reverse: it would have **refused to start** a hosted run whose
  rulebook did not fit a window that model does not have.
- A run now **says which regime it is in** at startup, naming any agent whose configured window is not being
  applied. Both regimes are fine; neither should be arrived at by accident.
- `Harness:EnforceContextWindowOnHostedModels` (default `false`) holds hosted models to the configured window
  anyway. **Turn it on to level the field** — a hosted model that never has to forget anything is not
  answering the same question as a local one working inside 8k, so when the comparison is about playing a
  long fight well, the handicap belongs on both sides. Leave it off when the question is what each provider
  can do at its best.

### The provider's own tool-call parser can 500, and a retry has to actually re-sample (v0.8)

`qwen3.5` emits tool calls as XML, and **Ollama's own parser** sometimes rejects what the model produced —
`element <function> closed by </parameter>` — returning **HTTP 500** rather than degrading to text. This is
upstream and open ([ollama#14834](https://github.com/ollama/ollama/issues/14834), with
[#17276](https://github.com/ollama/ollama/issues/17276) closed as a duplicate). Nothing on our side can
prevent it: the reply never arrives, so a client-side XML salvage has nothing to salvage.

One live run made the whole shape of the problem visible, because it contained **both outcomes at once**:

| Agent | Temperature | Attempts | Result |
| --- | --- | --- | --- |
| Elara (`character.decide`) | 0.8 | 500, 500, **stop** | recovered on attempt 3 |
| IntentParser (`parse-intent`) | 0 → old floor 0.1 | 500, 500, 500 | **run dead at round 5** |

Five 500s against 1,263 successful calls (0.4%), every one the same structural malformation. Three things
followed:

- **A retry must re-sample, and 0.1 is not re-sampling.** The retry moved the seed and lifted the
  temperature to 0.1, which is barely off greedy — the distribution did not move, so the model reproduced
  the identical malformed call three times. The floor now **escalates**: 0.4 on the second attempt, 0.8 on
  the third. The first attempt is untouched, so a clean run still replays identically.
- **An optional component must not be able to end a run.** The intent parser is a convenience that salvages
  a prose reply, and it had no failure path at all: the exception unwound through the turn, the round loop
  and the run. It now returns null on failure and the turn falls back to the nudge path — which is exactly
  how every character was handled before the parser was written.
- **Retries are legible in the trace.** `ModelRequest` and `ModelError` carry an `Attempt` number. The
  resolved sampling options were always recorded (under `RequestedOptions`); what was missing was any
  marker that a call *was* a retry, so telling a re-send from a fresh call meant knowing that the seed is
  offset by the attempt number.

Escalating also changes the request, which matters beyond sampling:
[ollama#17825](https://github.com/ollama/ollama/issues/17825) records that re-sending the **identical**
request after such a 500 could wedge the server outright on a poisoned prompt cache — fixed in
[ollama#17883](https://github.com/ollama/ollama/pull/17883), and only ever reproducible **with thinking
enabled**, which is why this harness (reasoning off everywhere) has never seen it.

### Reasoning written beside a tool call is dropped, not forbidden (v0.8)

With reasoning off, a model thinks on the page: it reaches its tool call through a paragraph of
deliberation, and providers put that text and the call in **one assistant message**. The turn-end prune kept
such a message whole because it "carries a tool call", so the deliberation rode along for the rest of the run
— a median of **310 characters per turn**, on roughly a third of all turns in one live run, in a history the
summariser then pays to compress.

The prune now keeps the call and sheds everything else the message carried. It is **dropped rather than
suppressed at the source** on purpose: that paragraph is how a non-reasoning model reaches its decision, and
forbidding it in the prompt would be asking the model to think less. It has done its work by the time the
call is made; what it must not do is persist.

### A character's voice is a world rule, not a harness limit (v0.8)

`MaxSpeechActsPerTurn` is **2** since v0.8, and the reason is the retry path rather than chattiness. A
character that speaks as part of an action attempt has spent its voice — and when the Dungeon Master refuses
that attempt, it is asked to try again with nothing left to say. Every occurrence in a live run was that
shape: Skrit said *"Elara, this spear isn't going to hurt you!"* with a compound action that was refused,
reworded the action, and had *"Eat this!"* swallowed. The words that went with the failed attempt were really
said and cannot be given back, so the honest fix is to allow the second breath a second attempt implies.

Exceeding the allowance now traces **`SpeechNotHeard`**, not `HarnessLimitReached`. A harness limit means
something went wrong; a character running out of breath is the fiction working. Filing it under the former
made a good run read as a troubled one — three of one run's four "harness limits" were this rule behaving
exactly as designed.

### Sampling parameters you do not set are not off (v0.8)

**A sampling parameter the harness leaves unset takes the model's own default, and a chat-tuned model's
defaults are tuned for conversation — not for producing exact structured output.** That is not a
hypothetical: it was true of every local call this project has ever made, and nobody knew.

A v0.8 Ollama log settles it. The per-agent temperatures reach the model exactly as configured:

```text
215 calls  top_k=40  top_p=0.900  temp=0.200   Dungeon Master
137 calls  top_k=40  top_p=0.950  temp=0.800   characters
131 calls  top_k=40  top_p=0.900  temp=0.100   Rulebook Resolver
 77 calls  top_k=40  top_p=0.900  temp=0.300   History Summariser
 31 calls  top_k=40  top_p=0.900  temp=0.000   Intent Parser
```

…and **all 605 of them** also carried this, which the harness never set and never recorded:

```text
repeat_penalty = 1.000   frequency_penalty = 0.000   presence_penalty = 1.500
```

`presence_penalty = 1.5` is Qwen's recommended default for general chat. A presence penalty exists to stop a
model repeating itself. **This harness depends on the model repeating itself exactly** — tool names, stable
ids, item names copied verbatim out of the state block, structural punctuation. Qwen's own sampling table
puts the value at `1.5` for general tasks and `0.0` for *precise coding* — the one category where output
must be structurally exact, which describes every call made here. Their documentation also warns that a high
value can cause **language mixing**, and a live run closed a spoken line with `】` (U+3011, a CJK lenticular
bracket) where `]` belonged.

Whether that penalty *caused* the malformed tool calls is **not established** — it fits the evidence and it
fits the vendor's own caveat, and it has not been tested. What is now true is that the harness sets the value
instead of inheriting it, and records what it sent, so the question is answerable by changing one number and
re-running under the same seed.

**The second trap is greedy decoding.** `Temperature: 0` looks like the safe choice for a parser or a
classifier. On qwen3.5 it is the opposite: a temperature-0 draw reproduces itself exactly, so a malformed
reply comes back malformed identically however many times you retry. That is how a run died at round 5 — the
intent parser 500'd three times on the same broken XML, while a character sampling at 0.8 shook the same
fault off in the same trace. Qwen never recommends below 0.6 for any task or mode; zero was our number, not
theirs, and the determinism it bought is already provided by the derived seed.

Both are now explicit in `Agents:Default` (`PresencePenalty: 0.0`, `TopK: 20`) and the parser reads at 0.3.
`PresencePenalty` and `FrequencyPenalty` are recorded per agent in `run.json` and per call in the trace's
`RequestedOptions` — so two runs that differ on them no longer produce identical-looking artefacts. Anthropic
has no equivalent parameter, so both are dropped and reported there like any other unsupported option.

**Before blaming a model for malformed output, read the provider's log and check what sampler it actually
ran under.** It is not necessarily what the config says.

### A local-model accommodation must not become a global rule (v0.8)

Three separate defects in this release were the same mistake wearing different clothes, and it is worth
recognising the shape before adding a fourth:

| Accommodation | Why it exists | What it was doing everywhere |
| --- | --- | --- |
| `ContextWindow` bounding history | Ollama takes `num_ctx` | Summarising hosted runs to fit a ceiling the provider never applies |
| The rulebook startup guard | Same window arithmetic | Would have refused to start a hosted run over a window that does not exist |
| `AdjudicationOutputTokens` | On a shared window, output tokens are input tokens surrendered | Truncating hosted adjudications mid-reasoning for no gain |

Each was tuned for `qwen3.5:9b` inside 8k, was correct there, and was silently wrong on a hosted provider
whose input window is far larger and whose output budget is separate. All three now read
`AgentModelProfile.BindingContextWindow` — **the window that actually bounds a request, or null when nothing
the harness knows about does.**

Before adding a knob that compensates for a model, ask which providers the compensation is *true* for, and
gate it on a capability rather than applying it to everyone. `ProviderCapabilities` is the place that
knowledge belongs.

### Dungeon Master context projections

By default (`ProjectDungeonMasterContext: true`) the Dungeon Master does not carry one ever-growing
conversation. Each task — narration, answering, adjudication — runs on a *projection*: the system
prompt, a fresh authoritative snapshot, and only the immediately relevant context (narration keeps its
own last line for continuity; nothing else). This keeps the DM's per-call input flat across a run
(measured ~1.7k–2.4k tokens regardless of length, versus climbing past 8k and truncating) and keeps
adjudication out of "prose mode", which was measured to wreck its classification. The complete record
of every DM call still lives in `trace.jsonl`. Set the flag to `false` to run the old
single-conversation behaviour for comparison.

The DM is given each character's condition as a descriptive band ("badly wounded"), never a raw health
number, so it is structurally unable to leak exact state into narration. The engine keeps the numbers.

### Reasoning models

`Effort` is one knob that controls reasoning across every provider. Set it per agent to one of `none`,
`low`, `medium`, `high`, `max` (aliases `off` and `xhigh` are accepted). The harness maps that single
value to each provider's native mechanism:

| `Effort`         | Ollama (`think`)   | OpenAI / Anthropic (`ChatOptions.Reasoning`) |
| ---------------- | ------------------ | -------------------------------------------- |
| `none` / `off`   | `think: false`     | `ReasoningEffort.None`                        |
| `low`            | `think: "low"`     | `ReasoningEffort.Low`                         |
| `medium`         | `think: "medium"`  | `ReasoningEffort.Medium`                      |
| `high`           | `think: "high"`    | `ReasoningEffort.High`                        |
| `max` / `xhigh`  | `think: "high"`¹   | `ReasoningEffort.ExtraHigh`                   |

¹ Ollama's top think level is `high`, so `max` clamps to it there. On OpenAI and Anthropic the value
rides in the standard `ChatOptions.Reasoning`, which the provider's Microsoft.Extensions.AI adapter
translates to its reasoning-effort request field. It is honoured by all three providers and never
reported dropped; a model that does not reason simply ignores it.

**Not every model grades the levels.** `qwen3.5:9b` accepts `low`/`medium`/`high` without error but
treats thinking as **binary** — any of them just switches it fully on, identical to `high`. So on
this model the levels below `none` are not "a bit of reasoning vs a lot," just on/off; budget for the
full reasoning cost (`MaxOutputTokens` ≥ 1500) whenever effort is anything but `none`. Newer Ollama
models may honour the graduated levels for real — this was only confirmed false for qwen3.5.

For this harness the useful setting is usually `Effort: none`. Left thinking, a reasoning model spends
its **entire** output budget on a private reasoning block and emits no visible prose or tool call within
a normal token limit — the narration comes back empty and the Dungeon Master appears to "say nothing".
Measured on `qwen3.5:9b`: with thinking on and a 700-token budget the reply was 700 tokens of reasoning
and zero prose; with reasoning off the same narration was clean prose in ~100 tokens. If you genuinely
want a reasoning agent, raise `Effort` and raise `MaxOutputTokens` well above the reasoning length
(2500+).

The harness flags this: a reply that is all reasoning is recorded as a truncation with
`ReasoningOnly: true`, and the run ends with a warning naming the fix.

> The older `Thinking: true/false` bool still works but only reaches Ollama (it is dropped and reported
> on the hosted providers); prefer `Effort`, which works everywhere. When both are set, `Effort` wins.

Effort is only accepted on some models, and the harness handles the rest rather than failing:

- **OpenAI** runs through the **Responses API** (`/v1/responses`, not chat completions) so reasoning can
  run alongside function tools — chat completions 400s that combination for the reasoning-default GPT-5
  models. Effort is sent only to the reasoning models (GPT-5+, o-series); the general-purpose ones
  (`gpt-4o`, `gpt-4o-mini`, `gpt-4.1`) reject the argument, so there `none` is omitted and a raised effort
  is dropped-and-reported. On a reasoning model, engaging effort also drops `temperature`/`top_p` (those
  models reject sampling once reasoning is on — exactly like Anthropic thinking, below). The Responses
  client (`OpenAIClient.GetResponsesClient()` + its `AsIChatClient` overload, in `ChatClientFactory`) is
  still marked experimental (`OPENAI001`) in the installed SDK — suppressed locally with a comment; worth
  rechecking on a package upgrade in case it graduates or the API shifts. Reasoning-token counts are not
  currently surfaced in the usage breakdown for OpenAI (they're billed inside output tokens, just not
  broken out) — same gap as Anthropic's cache-read recovery below, unresolved for reasoning tokens.
- **Anthropic** graduated effort reaches the 4.x models but not the modern Claude ones (see the Anthropic
  section).

An OpenAI key is read from the `OPENAI_API_KEY` environment variable, or from user secrets:

```bash
dotnet user-secrets --project src/ModelsAndMonsters set ModelsAndMonsters:Providers:OpenAI:ApiKey <key>
```

Keys are never written to configuration files or to run output.

### Anthropic

Anthropic is the third provider (via the official [`Anthropic`](https://www.nuget.org/packages/Anthropic)
NuGet package, used through its `IChatClient`). Configure an agent with `"Provider": "Anthropic"` and a
Claude model id (`claude-opus-5`, `claude-sonnet-5`, `claude-haiku-4-5`, …). The key comes from
`ANTHROPIC_API_KEY` or user secrets:

```bash
dotnet user-secrets --project src/ModelsAndMonsters set ModelsAndMonsters:Providers:Anthropic:ApiKey <key>
```

> Model-id naming pitfall: the `-4-8`-style suffix only exists for **Opus** (`claude-opus-4-8`).
> Sonnet's line goes `claude-sonnet-4-5` then jumps straight to `claude-sonnet-5` — there is no
> `claude-sonnet-4-8`; that id 404s (`AnthropicNotFoundException`).

Anthropic honours `top_k`, a forced tool choice (`ForceToolChoice`), and reasoning through the unified
`Effort` knob (mapped to `ChatOptions.Reasoning`), but has no request seed or context-window control —
those are dropped and reported, as is the legacy on/off `Thinking` bool (use `Effort` instead). It also
rejects `temperature` and `top_p` in the same request (the harness keeps temperature, drops top_p).
**`MaxOutputTokens` is required** by the API, so always set it (the shared `Default` does).

Turning reasoning **on** (`Effort` above `none`) on Anthropic has two catches:

- **Sampling and forced tool use are disallowed while thinking is active** (`temperature` may only be
  `1`). The harness drops `temperature`/`top_p`/`top_k` and any `ForceToolChoice` for that agent, so a
  4.x model like `claude-haiku-4-5` — which normally accepts a temperature — doesn't 400 when you raise
  its effort. (Claude 5 models drop sampling anyway.) Reasoning also eats the output budget, so raise
  `MaxOutputTokens` well above the default.
- **Graduated effort on the *modern* Claude models isn't wired up yet.** The Anthropic client's
  Microsoft.Extensions.AI adapter maps reasoning to the legacy `thinking.type.enabled`, which Claude 5
  and Opus 4.7+ reject (they require `thinking.type.adaptive` + `output_config.effort`). So on those
  models any effort above `none` is **dropped and reported** rather than sent. `none` works everywhere.
  Effort *is* honoured on the **Anthropic 4.x** models (`claude-sonnet-4-5`, `claude-haiku-4-5`) — put a
  reasoning agent there — as well as on Ollama and OpenAI.

**Opus 4.7 and later** (`claude-opus-4-8`, `claude-opus-5`, …) go further and forbid *all* sampling
parameters — `temperature`, `top_p` and `top_k` each 400 on any non-default value, replaced by an
effort parameter and adaptive thinking. The harness detects these models and drops all three
automatically (reported as usual); steer them with the prompt. Force the behaviour either way with the
per-agent `OmitSampling` flag.

**Prompt caching.** OpenAI caches repeated prefixes automatically; Anthropic does not unless a request
carries explicit `cache_control` breakpoints, so Anthropic clients are wrapped to mark the stable
leading prefix (tool schemas + system prompt, which is identical on every call for an agent). The
Dungeon Master benefits most, since it re-sends the same system prompt on every projection. Cache
reads and cache-creation writes are surfaced in `report.md` under **Run details** (as is OpenAI's
automatic cached input), so the effect is visible. Caching the *growing character histories* is a
further lever not yet wired.

The scenario lives in `src/ModelsAndMonsters/scenario.json`, and prompts are plain markdown in
`src/ModelsAndMonsters/Prompts/Templates/`. Each prompt is content-hashed into `run.json` so a run
can be tied to the exact prompt text that produced it.

### What each model actually does, before you spend an hour on a run (v0.8)

Measured across this release's live runs on the same scenario and the same prompts. Model behaviour is the
main variable in this project, and the failure modes barely overlap — **run any change across at least two
of these before believing it.**

| | `qwen3.5:9b` (Ollama) | `gpt-5.4` | `claude-haiku-4-5` |
| --- | --- | --- | --- |
| Tool-call discipline | Frequently replies in prose; 16 intent-parses in one 11-round run | Clean | Clean — **zero** prose replies across three runs, 100+ calls |
| Provider failures | 500s on its own malformed tool-call XML (~0.4% of calls) | none seen | none seen |
| Adjudication style | Deliberates in prose when uncertain | Calls the tool immediately (largest reply seen: 122 tokens) | Deliberates in headed, bulleted analysis — needs the room |
| Speech | Short, loud, repetitive across rounds | Terse, escalating, almost scripted | Longest and most varied; negotiates unprompted |
| Watch for | Context exhaustion, malformed arguments, repetition loops | Little; it is the quiet one | Output length on adjudication, not truncation |

The two practical consequences: **qwen is the model that finds harness bugs** (every serialisation and
context defect in v0.8 came from it), and **Haiku is the model that finds prompt bugs** (it does what it is
told, so when it does something odd the prompt said so).

## Output

The console shows only the readable game. Everything else goes to `runs/<run-id>/`:

| File | Contents |
| --- | --- |
| `run.json` | Run metadata, scenario, initial state, agent model profiles, prompt hashes, harness limits |
| `trace.jsonl` | Append-only event stream: every model request and response, tool dispatch and result, adjudication, target resolution, engine action with before/after state, RNG draw with its modifiers, status application/consumption/expiry, surrender offer and agreement, ability use, attack redirection, answer projection, discarded post-resolution output, team-outcome evaluation, turn skip, and every narration with its recipients |
| `final-state.json` | Authoritative world state when the run stopped — including live statuses, the whole surrender-offer ledger and every agreement |
| `report.md` | Full readable rendering: run details, model profiles, per-agent activity totals, outcome, surrender negotiation, persuasion and intimidation, ability activity, the status timeline, state-grounding health, inventory and item provenance, rulebook consultations, context health, teams, knowledge, a plain transcript, **every trace row**, and the final state |
| `report-summary.md` | The same report **without** the event-by-event trace dump — the story, profiles, per-agent totals, the analysis sections and transcript only. Small and skimmable; points to `report.md`/`trace.jsonl` for the raw events |

Both reports are written automatically at the end of every run, including a failed one. They are
generated purely from the other three files, so any past run can be re-rendered (this rewrites both
`report.md` and `report-summary.md`):

```bash
dotnet run --project src/ModelsAndMonsters -- --report runs/<run-id>
```

When an agent's reply carries reasoning (Ollama/Anthropic thinking, an OpenAI reasoning model), the
transcript folds it into a collapsible `<details>💭 <agent> — thinking</details>` block right above
the move it led to — in both reports. The text was always in `trace.jsonl` (every provider's
reasoning lands there as a `"reasoning"`-typed content block via Microsoft.Extensions.AI); rendering
it just makes it visible without reading raw JSON. Because it comes from the trace, re-running
`--report` on an old run surfaces it retroactively.

## Characters, teams and turns

A character sees only `ask_dm`, `say`, `take_action` and `end_turn`, and never learns that a game
engine, a rulebook or a Dungeon Master model exists. Asking and speaking do not consume the turn; a
turn ends when something the character attempts takes effect, or when the character chooses to end it.
If nothing takes effect for `MaxConsecutiveIdleRounds` rounds, the encounter is stopped as a stalemate
rather than grinding on to the round limit.

Each of the four characters is a separate agent with its own definition, model profile, conversation
history, self-state and derived seed. Nothing is shared between them: one hero's private question to
the Dungeon Master never enters the other hero's history. Everything a character learns about anyone
else arrives as **public narration** or a **public knowledge fact** — the opening scene, an accepted
action's outcome, speech, a death, a surrender, an escape — deliberately delivered to every character
still *present* that could perceive it and recorded, with its recipients, in the trace. See
[Hidden information and character knowledge](#hidden-information-and-character-knowledge) below for
what "could perceive it" actually bounds.

Turns run in a **fixed order**, repeated each round. Before a turn the actor's `Disposition` is
checked; anyone not `Active` (dead, surrendered or escaped) is skipped without a model call, and the
skip is traced with its cause. See [Non-lethal outcomes](#non-lethal-outcomes-disposition-and-exits)
and [Negotiated surrender](#negotiated-surrender-terms-not-declarations) for how a character reaches
those other dispositions and how the team terminal condition accounts for them.

Since v0.7 a turn is bracketed by **engine upkeep**: `BeginActorTurn` before anything is rendered or
asked (so a status that has just fallen away is gone before the character is told its own state), and
`EndActorTurn` after the turn resolves (expiring an unused modifier, and lapsing any surrender offer this
character was the named recipient of and did not take up). Upkeep is the engine's decision, never
narration's; it consults no randomness, and only advances the world version when something actually
changed, so a turn in which nothing expired is indistinguishable from a pre-v0.7 one.

When a character attacks, the Dungeon Master translates its words into one specific, living, *active*
target by name, and the engine records the resolution against a **stable character id**. An
ambiguous, absent, dead, surrendered or escaped target is rejected — never silently swapped for
someone else. The one thing that can move a blow is a **guard relationship**: an enemy strike aimed at a
guarded ally is redirected onto the guardian, resolved with the guardian's armour and health, and traced
with *both* the intended and the authoritative target — and without a second roll. The engine imposes no
friendly-fire rule: it resolves whatever the DM actually translated (and a guard never turns aside an
ally's blow), while the prompts and each character's goals discourage striking an ally.

A character's system prompt names **both** sides of the roster by name — allies ("never raise a weapon
against them") *and* enemies ("they are not your friends, whatever they may say; offer them no aid,
comfort or reassurance"). Enemies were originally left unnamed on the assumption a capable model would
infer "everyone else is hostile" — measured to fail on weaker local models, which drifted into offering
an enemy comfort or camaraderie mid-fight (a goblin warning heroes about a slippery floor; a hero telling
an enemy goblin "I've got your back"). Naming the enemy side reveals nothing hidden — who opposes whom is
visible from the first moment, unlike a container's contents.

## Non-lethal outcomes: disposition and exits

Every character has a `Disposition`: `Active`, `Surrendered`, `Escaped` or `Dead`. Only `Active`
characters take turns or can be targeted; `Surrendered` and `Escaped` characters are alive and simply out
of the fight. The room has a seeded **exit** (closed and unlocked); a character must `open_exit` it
before anyone can `escape_encounter` through it — two separate turn-consuming acts, never resolved
together.

The team terminal condition is evaluated over `Active` membership: a team stays a contender only while it
has at least one `Active` member, so a team can win by killing, by having a surrender accepted, or by
out-waiting an escape — or any mix. The final report classifies the outcome as `Elimination`,
`Surrender`, `Withdrawal`, `Mixed`, `Draw` or `HarnessLimit` and lists exactly how each character left
active combat.

## Negotiated surrender: terms, not declarations

v0.5's `surrender` was unilateral — a character declared it and became untouchable, keeping everything.
v0.7 removes that action entirely. **Giving up now takes both sides, and costs something.**

**Terms must hold nothing back.** An offer promises at least one carried item; the weapon in hand may be
added alongside it, and stands alone only for a character carrying nothing at all. That rule is not
bookkeeping — it closes a parsing artefact. `offer_surrender` binds the offerer to the *acting* character,
so a demand for somebody else's surrender has no representable form and collapses into exactly one shape:
no items, `forfeit_weapon` true because a weapon was mentioned somewhere in the intent. A live run produced
four offers, all with `forfeit_weapon` true and two with nothing else, one of them a character who was plainly
winning — recorded as *that character* surrendering and giving up their own sword. The
"carrying nothing" exemption matters just as much: the first version of the rule refused a goblin who had
spent the fight giving his possessions away trying to negotiate, and he died the next turn.

```text
Active
  → offer_surrender(recipient, offered items, forfeit weapon?)   ← consumes the offerer's turn
  → still Active, still armed, still a valid target
  → the named recipient accepts / ignores / rejects, on their own turn
      → accepted:  terms enforced atomically, offerer becomes Surrendered and is disarmed
      → otherwise: nothing transfers, and the offerer may propose better terms later
```

An offer must promise something **enforceable and immediate**: one or more coin purses, ordinary
inventory items, forfeiture of the weapon in hand, or a combination. "Please spare me" promises nothing
and is refused (`OfferHasNoConcession`) — it is speech, and speech settles nothing.

What creating an offer deliberately does **not** do: transfer any asset, disarm the offerer, change their
disposition, protect them from attack, or guarantee acceptance. Only one pending offer may exist per
offerer, and only the **named** recipient may accept it. An offer ends in exactly one of five states —
`Pending`, `Accepted`, `Rejected` (the recipient's side answered with a hostile act), `Expired` (the
recipient finished a turn without accepting) or `Invalidated` (a party left active play, or a promised
asset left the offerer's hands). Only `Accepted` moves anything.

Acceptance is **all or nothing**: every promised item moves in one atomic state change, the offerer is
disarmed, a promised weapon lands on the room's floor as an inert trophy keeping its own stable id (there
is no equipping in v0.7, so a looted weapon is an inventory trophy, not a usable one), a durable
`SurrenderAgreement` is recorded, the offerer becomes `Surrendered`, and their combat statuses are swept
away. If any promised asset is missing when acceptance is attempted, nothing moves at all.

**This is the persuasion and intimidation slice**, and it is deliberately not a mechanic. There is no
`persuade` action, no social statistic and no social roll. What a character says with `say` — a plea, an
argument, a threat, a demand for better terms — supplies the *argument*; the structured offer supplies
the *terms*; and the recipient model decides for itself whether the terms are worth it. The offer records
the id of the speech that accompanied it, and the report shows the two side by side without ever claiming
the speech caused the acceptance. The engine enforces only the concrete result.

## Abilities and status effects

Abilities are data-driven, not scripted: an `AbilityCatalog` of `AbilityDefinition`s, each with a stable
id, category, target rule, use limit, one concrete `AbilityEffectKind`, and the rule card that governs
it. A character holds `CharacterAbility` records carrying the charges they have left — authoritative
mutable state, spent only when the engine accepts a use. There is no way to define an ability at runtime
and no model-defined effect; an ability id the book does not know is a **scenario error that fails at
startup**, because a character whose prompt promises something the engine cannot resolve is exactly the
boundary problem this release closes.

| Ability | Who | Category | Uses | What it does |
| --- | --- | --- | --- | --- |
| `guard-ally` | Rowan | Technique | unlimited | Spends the whole turn over one ally; the next **enemy** blow aimed at them is redirected to the guardian, resolved with the guardian's armour and health, **with no extra roll**. Consumed by that one redirection; otherwise falls away at the start of the guardian's next turn. Never stacks on either side. |
| `healing-prayer` | Elara | Spell | 1/encounter | Restores a fixed 4 health to the caster or an ally, capped at maximum, no RNG. Refused — *with the charge intact* — on an unwounded, dead or escaped target. |
| `rally-grunt` | Vark | Command | 1/encounter | Applies `Rallied` (+15 hit chance) to one ally. Consumed by that ally's next attack whether it lands or misses; expires at the end of their next turn if unused. |
| `dirty-strike` | Skrit | Trick | 1/encounter | One **ordinary** weapon attack — the same two draws, no more — that on a hit applies `OffBalance` (−15 hit chance). The charge is spent on hit or miss. |
| `defend` | everyone | BasicAction | unlimited | Spends the turn bracing; `Defending` reduces the next **successful** incoming attack's final damage by 1, applied after armour and glancing, floored at zero. Survives a miss; expires at the start of the defender's next turn. |

`StatusEffectInstance` lives on `GameState`, not on a character, because a linked relationship
(`Guarding` on the guardian, `Guarded` on the ally) spans two characters and must never exist on one side
only. Every instance has a stable id, a named source, a signed modifier, one exact `StatusExpiryRule`, a
`RelationshipId` for its paired half, and public visibility. Expiry is engine upkeep, not narration:
`BeginActorTurn` and `EndActorTurn` sweep exactly the statuses whose rule fires at that boundary
(`StartOfSourceNextTurn`, `StartOfTargetNextTurn`, `EndOfTargetNextTurn`), and a status can never expire
on the turn it was applied. Statuses are removed whenever the character sustaining them dies, surrenders
or escapes. Nothing else exists: no poison, no bleed, no stun, no paralysis, no stacking, no cleansing,
no area effects — and no status a model can invent.

**Modifiers feed the existing RNG pipeline rather than a parallel one.** There is one weapon-strike
resolution, shared by `attack_character` and by any ability that strikes, so an ability-driven blow can
never become a second combat path with its own dice. `RngDraw` now records `BaseChance`, the effective
`Threshold`, and each modifier in structured form (`RngModifier`: source id, source character, signed
value, application order, whether it was consumed) as well as a readable note — so an effective chance
can be recomputed exactly rather than parsed out of prose. Modifier order is *fixed*, not discovered
(`Rallied`, then `OffBalance`), so two runs with the same statuses reach the same recorded order.

## Morale: fear, threats and steadying

Every character carries an encounter-scoped **`Fear`** from 0 to 5. It is authoritative engine state on
`Character.Fear`, it moves only through `GameEngine.ApplyFear`, and every change names a cause from the
closed `FearChangeCause` set and produces a `FearChanged` trace event — including a change the clamp
absorbed, and including changes no die was involved in.

At **3 or above** a character is publicly **`Scared`**. That is a real `StatusEffectInstance` with a new
`StatusExpiryRule.WhileConditionHolds`, so the turn-upkeep sweep never touches it: the engine applies and
removes it as the number crosses the threshold, and nothing else may. The status exists so the public
shadow of fear can never disagree with the number it shadows.

**Fear rises by one** when a surviving character takes a critical hit, when one blow takes at least a
quarter of their maximum health, when they first find themselves outnumbered among the *active*
combatants, and when an enemy's `intimidate_character` tells. A blow that is both critical and heavy
frightens once, recorded as `CriticalAndLargeHitReceived` — the causes are combined, never counted twice.

**Fear falls by one** when a character lands a critical hit of their own, when an ally spends a turn on
`steady_ally`, and when Vark's existing Rally Grunt is used on them (its v0.7 hit-chance effect is
untouched; the steadying is added alongside it).

### The information boundary

This is the part worth being careful about when extending it:

| Who | Gets |
| --- | --- |
| The character themselves | the exact number, in their turn context and in an `AnswerFacts` answer |
| Everyone else present | only the `Scared` status, as "*X* looks scared and increasingly concerned with survival" |
| The Dungeon Master | the `Scared` status in the snapshot, and **no number at all** |
| The trace, report and observer UI | everything — they are experiment artefacts, not characters |

A fear change that does **not** cross the threshold creates no public fact and reaches nobody, which is
what stops an opponent inferring the value by counting events.

### Outnumbering is a latch, not a check

`Character.IsOutnumbered` is authoritative state, reconciled once per accepted action in
`GameEngine.ReconcileOutnumbering`. Fear rises on the *transition* into being outnumbered, so standing
outnumbered for five rounds is one fact five times over and frightens nobody further. Parity genuinely
restored clears the latch, so it can fire again later. It is seeded at construction from the opening state
and raises no fear — a character the scenario placed at bad odds has not made a transition. A character who
has left the fight keeps the latch and the fear they left with.

### Fear compels nothing

This is the design constraint, not a nicety. A scared character is handed a strong, explicitly non-binding
instruction ("Seriously consider escaping, surrendering, defending, seeking reassurance … but the choice
remains yours") and then decides for itself. There is no panic roll, no forced action, no attack penalty,
and no path by which fear surrenders or escapes anybody: yielding still needs a concrete offer *and* an
acceptance, and leaving still needs the door opened and then walked through. `MoraleRegressionTests` holds
that line.

### Two actions made of speech

`intimidate_character` and `steady_ally` are the first actions defined as *speech aimed at one person*.
Recognising that structurally, rather than by reading the words, needed one addition: an optional
**`addressed_to`** field beside `utterances` on the character tools. The speaker declares who they were
talking to; the engine validates that the action's target matches, and refuses a mismatch **before any
draw**. When no addressee is declared, the DM's binding stands unchallenged. Nothing ever reads the words.

Intimidation makes exactly one seeded draw against a base 35%, modified only by state — the target's
existing fear (+10 each), whether they are outnumbered (+15), whether they are badly wounded or worse
(+10, using the project's one `HealthBands` definition), and whether the intimidator is themselves Scared
(−10) — clamped to 10–90. Eloquence, length, punctuation and model identity carry no modifier at all, and
`What_was_said_never_reaches_the_odds` asserts it. Each actor may try it on each enemy **once per
encounter**, spent whether it succeeds or fails, and the affordance is withdrawn from the DM's tool set
once there is nobody left to try it on.

## Attack quality: one draw, three outcomes

v0.7 rolled to hit and then rolled again for a glancing blow. v0.8 replaces that second roll rather than
adding a third: **one quality draw** selects among all three bands.

| Raw quality roll | Result | Post-armour damage |
| --- | --- | --- |
| 1–25 | Glancing | halved (existing rounding) |
| 26–75 | Solid | unchanged |
| 76–100 | Critical | doubled |

Armour is subtracted *before* the multiplier and a Defending status *after* it, so a blow armour stopped
stays stopped and a braced guard still turns aside its point. The draw's purpose in the trace changed from
`attack.glancing-check` to `attack.quality-check`; `CombatOptions.CriticalHitChance` configures the high
band and `0` removes it (`CombatRules.NoGlancing` sets both bands to zero, which is what "deterministic
damage" means in a scripted test).

Note for tests: `ScriptedRng.Solid` is **50**, not 100. Under three bands a maximum roll is a *critical*
hit, so a test meaning "no quality variance" has to say so in the middle of the range.

## Environmental cover

v0.9 is the final planned feature phase, and its purpose is to prove one thing: that a piece of the room
can carry the same authoritative state, structured actions, deterministic resolution, character knowledge,
narration, tracing, UI and reporting already built for characters, items and abilities — without becoming a
physics engine. The design principle stated in the build spec is worth repeating verbatim, because every
choice below follows from it:

> Environmental features are authoritative state with explicit affordances and deterministic consequences,
> not decorative prose that the Dungeon Master may reinterpret freely.

### One typed object, sharing the room's existing resolution

`CoverObject` is a new `WorldObject` subtype (`Domain/WorldObject.cs`), living in the same `Room.Objects`
array as `Container`. That was a deliberate fit to the existing shape rather than a new parallel
collection: `GameState.ResolveObject` already resolves any room object by id or name and already reports
ambiguity instead of guessing, so cover gets that discipline for free. Its mechanical fields —
`ProvidesCover`, `Capacity`, `CurrentOccupantId`, `HitChanceModifier`, `MaximumDurability`,
`CurrentDurability`, `Armour` — are exactly what the build spec asked for; `State` (Intact/Damaged/Destroyed)
is *derived* from durability rather than stored, so the two can never disagree. The shipped scenario seeds
one: an overturned mill workbench, capacity 1, `-20` hit chance, `2/2` durability, armour `2` — replacing
the "Rotten crates" flavour feature so the room does not carry both a decorative object and a mechanical
duplicate of the same thing.

Occupancy is a single slot, not a list, because the one seeded object has a capacity of exactly one;
`Capacity` is still carried as its own field, not derived from the slot, so a later multi-occupant object
has somewhere to grow into without another schema change — nothing in v0.9 exercises a capacity above one,
and no code was written to pretend otherwise.

### A new status, not a hidden mechanic

Occupying cover is `StatusEffectKind.InCover` — a public status, like every other v0.7/v0.8 status, that
lives on `GameState.Statuses` rather than on `Character`. It carries a new `RelatedObjectId` field (the one
addition to `StatusEffectInstance` itself) naming which object it concerns, and it expires
`WhileConditionHolds` — the same rule `Scared` uses — so the turn-upkeep sweep never touches it; only an
explicit removal ends it. No hidden-information mechanic applies to cover at all: everyone present sees the
object, its condition and its occupant, exactly like an exit's open state, never like a closed container's
contents.

### Structured exposure, not language parsing

The build spec's hardest constraint was this: *whether an action breaks cover must be decided from
structured metadata, never by reading what a model wrote.* The answer is `GameAction.ExposesActor`, a
virtual property every action states its own true/false for at the declaration site, next to `ActionType`
and `Describe()`. `attack_character`, `steal_item`, `take_item`, `open_container`, `inspect_object`,
`open_exit`, `damage_environmental_object` and (by the same reasoning, though enforced through the
disposition-driven purge rather than a direct call) `escape_encounter` all expose; `defend`, self-targeted
`use_item`, `offer_surrender`, `accept_surrender`, `intimidate_character` and `steady_ally` do not. Two
actions the spec left unlisted needed a judgement call, made and documented at the point it is made rather
than guessed from wording: `give_item` exposes (reaching toward another character), `drop_item` does not
(nothing leaves your own feet). `use_ability` is generic across five different abilities, so its exposure is
resolved per-ability inside the engine instead of on the action type — Guard Ally always exposes (you cannot
stand over a companion from behind cover), Rally Grunt never does (a shouted order needs no reach), and
Healing Prayer exposes only when the target is someone other than the caster.

The actual mechanism is a query/apply pair in `GameEngine`: `ExposeIfInCover(state, actorId)` reads whether
an actor occupies cover and returns the delta (the status to remove, the cover object to update) *without
mutating the snapshot it was given* — because that snapshot is the action's `EngineResult.StateBefore`, and
the trace has to show the actor still behind cover there, exposed only in `StateAfter`. `ApplyExposure` folds
that delta into whatever "after" state a `Resolve*` method is already building, atomically with the rest of
that action's own effect. Ten call sites wire it in: the shared weapon-strike resolution (covering both
`attack_character` and Dirty Strike in one place), `steal_item`, `take_item`, `open_container`,
`inspect_object`, `open_exit`, `give_item`, `damage_environmental_object`, Guard Ally, and Healing Prayer.
Death, surrender and escape release cover through the existing `PurgeForInactive` sweep instead — extended
with one more check (clear the object's occupant slot alongside the status it already swept) — because those
are already the one place every other status-cleanup for a character leaving active play happens.

### One roll, three outcomes

An attack against a covered character still makes exactly the *existing* single hit-check roll — v0.9 adds
no second draw. What changes is how that one roll is read. The attacker's ordinary effective hit chance
(every existing status modifier, in the existing fixed order) becomes the **pre-cover** chance; the cover's
own `HitChanceModifier` is folded in afterward, clamped again, to reach the **covered** chance:

```text
raw roll <= covered chance                            -> DIRECT HIT   (cover irrelevant this time)
covered chance < raw roll <= pre-cover chance          -> INTERCEPTION (the cover, not luck, saved them)
raw roll > pre-cover chance                            -> ORDINARY MISS (would have missed regardless)
```

Only a direct hit proceeds to the existing quality draw, armour subtraction, Defend reduction and fear
rules — all of it completely unchanged; cover modifies the hit check and nothing downstream of it. An
interception costs the cover exactly one point of durability, ignoring the cover's own armour (that only
applies to *deliberate* damage), makes no quality draw, and does no damage to the character at all — reduced
to zero, it destroys the object and evicts its occupant in the same event. An ordinary miss changes nothing
about the cover, and the report is careful never to credit it: `AttackOutcome` carries `InterceptedByCover`
as a distinct fact from `Hit`, so a covered miss and a cover interception are never conflated in the trace,
the report or the transcript. No damage-prevented *number* is ever reported for an interception, because the
attack never reached the quality draw that would have produced one — reporting one would be inventing data
the engine never had.

### Deliberately damaging the object itself

`damage_environmental_object` is the other new action: a character striking the cover rather than whoever
shelters behind it. It makes no roll at all — a stationary room object does not dodge — so damage is the
deterministic `max(1, weapon damage - object armour)`, distinct from a character's own damage formula
(`max(0, ...)`, which lets armour fully stop a blow) because cover always takes *some* chip damage from a
real weapon. It breaks the striker's own cover first, if they occupy any, and is refused outright against
the very cover the striker currently occupies — battering down your own shelter mid-fight is not a
representable intent.

### What a run actually shows

The run report gained one new section family (`Tracing/RunReportWriter.cs`): an environmental-object summary
table (initial/final state, capacity, final occupant, who destroyed it), an occupancy timeline built entirely
from `InCover` status transitions (so entering, leaving, being exposed, and being evicted by destruction all
read as one timeline without a separate trace-event vocabulary for each), a cover-effectiveness table
(direct hits vs. interceptions vs. ordinary misses, per object), and an object-damage table. Four new trace
event types support this: `CoverInteraction` and `EnvironmentalObjectDamaged` (companion rows to the generic
`EngineAction` row, emitted for accepted *and* rejected attempts alike, the same discipline `ExitInteraction`
already has), `AttackAgainstCover` (the covered-attack companion, always emitted when an attack's target was
sheltering, whatever the result), and the focused `EnvironmentalObjectDestroyed` — the same relationship
`CharacterSurrendered`/`CharacterEscaped` have to `DispositionChanged`.

## Grounded answers: `AnswerFacts`

Weak-model runs exposed a class of failure the DM prompt could not fix: asked a question, the Dungeon
Master answered from state the asking character could not perceive — naming a closed container's
contents, saying a consumed potion was still carried, promising tactical manoeuvres (closing distance,
flanking, backing away) the engine has no representation for. Characters then trusted those answers and
burned turns on attempts the engine refused.

The fix is to stop giving the model the material. For the answering task the DM is **not** given the
authoritative state at all. An `IAnswerFactsProjector` builds a deterministic projection of exactly what
that character may be answered from:

- their own current condition, weapon, belongings, abilities-with-charges and live statuses;
- what anyone in the room can see right now — standings, weapons held, container open state, the floor,
  the exits, and every pending offer with its terms and whose decision it is;
- what they have discovered first-hand, **paired with what has changed since** (an item that no longer
  exists anywhere is stated as gone; current ownership always supersedes a historical record of it);
- what they have only been told, marked as a claim;
- the **complete closed list** of what they could actually attempt, derived from state — a spent ability
  does not appear, a strike does not appear for empty hands, an acceptance appears only for the named
  recipient of a live offer, and the list ends by saying nothing else exists;
- explicit statements of what this world does not have: no position, distance, facing, movement, flanking
  or line of sight; no numbers anybody can perceive; no stun, knockdown, trip, disarm or grapple. Named
  environmental cover is the one deliberate exception (v0.9) — real when the room has it, and stated as
  such, never invented where it does not.

The DM's job is reduced from *deciding what is true* to *saying it naturally*. Anything the projection
withholds is recorded separately on `AnswerFactsProjected` — never sent to any model — so a run's trace
shows the boundary held rather than asserting it. Engine-side validation is unchanged and independent: a
mistaken DM answer still cannot bypass the informational-basis gates on `take_item` and `steal_item`, and
an offer can only promise, and an acceptance only transfer, assets the offerer actually owns.

## Post-resolution output is discarded

Once a turn-consuming action is accepted and resolved, everything else that reply contained is thrown
away: further tool calls, and trailing action text. It never reaches the engine, the public transcript or
the knowledge ledger, and it is recorded as `PostResolutionOutputDiscarded` with the content verbatim and
the action that had already resolved the turn. This stops a model attacking and then emitting an
unprocessed theft declaration as though it had acted twice. Speech made *before* the accepted action, in
the same reply, remains valid under the existing one-speech-per-turn rule.

## Ten live runs, and what they changed

v0.7 was play-tested across ten full encounters and three models — `qwen3.5:9b`, `claude-sonnet-4-6` and
`gpt-5.4` — and almost everything below this line in the architecture sections exists because one of them
broke. The narrative is in [`reports/v0_7_notes.md`](reports/v0_7_notes.md); every problem→fix pair is in
[`reports/v0_7_issues.md`](reports/v0_7_issues.md). Three things are worth carrying forward on their own:

**Run the same build across models, because each one fails somewhere the others do not.** Given identical
code, qwen never named the rule it wanted, Sonnet copied a rendered annotation back as an item id, and GPT-5.4
wrapped the rule in a condition the resolver refused. Each defect was invisible to the other two models. The
starkest case: one qwen run made three offers of surrender and chose `accept_surrender` **zero** times, while
Sonnet accepted twice on the same build — because Sonnet's characters say "I accept Skrit's terms" and qwen's
only ever describe the deed. A capability that works only when the character names the rule it wants is not
working; it is being carried.

**A mechanic that never fires is usually waiting on a persona threshold.** `open_exit` and `escape_encounter`
fired zero times in eight consecutive runs, with every model, though the door is in every state block every
turn — v0.7's negotiated surrender had simply out-competed it (one turn instead of two, and you stay targetable
in between). One written condition in Skrit's backstory — *the moment the captain is out of this, haul the door
open and go through it* — and it fired two runs later, exactly on cue. The same lever had earlier woken
surrender itself, when Vark's yield threshold was loosened. Read the personas before suspecting the engine.

**Six of the ten runs ended with nobody dead.** Surrender, escape and mixed outcomes are now the common case
rather than the exception, which is what the v0.5 and v0.7 slices were built for.

### What is deliberately still open

Recorded so a later session does not mistake any of it for solved. The full list, with evidence, is at the
end of [`reports/v0_7_issues.md`](reports/v0_7_issues.md).

- **A `fear` attribute is the intended next step, and is not built.** Flight is currently a judgement each
  character makes from prose about itself, which is why the exit needed an explicitly written threshold before
  it ever fired once. The shape wanted for a later release is a semi-hidden value that rises from events the
  engine *already sees* — a heavy hit taken, intimidation, one's own side losing ground — crossing into a
  **Scared** status with mechanical weight, in the same authoritative-status machinery v0.7 built for
  Guarding, Rallied and Off Balance. That would make flight a consequence of the fight rather than a line in a
  persona, and would give the intimidation half of the persuasion slice something to push against: right now a
  threat either talks somebody into offering terms or does nothing, with no dial in between. One live run
  produced the emotional curve by accident — *"Captain, don't leave me with them!"* → *"Wait for me,
  Captain!"* → *"Don't cut me! I'm gone!"* — which is exactly what the attribute would compute.
- **`reject_action` is the one remaining path made of model prose**, and it has produced refusals that are
  factually wrong (refusing a legitimate `inspect_object`; misidentifying who was offering terms). Wrong facts,
  not just wrong register, so no detector helps. The structural answer is to narrow what it may assert.
- **Stale item bindings in offers.** The Dungeon Master promised an item the offerer had given away the
  previous turn, against a fresh snapshot showing otherwise. The engine refused correctly and it cost a life.
  Whether terms may be silently narrowed to what the offerer actually holds is an open design question.
- **Friendly fire is permitted by deliberate v0.2 design** and is now surfaced live, but not prevented.
- **Combat resolves slightly too slowly** for the 12-round cap: one run ended with all four alive at 1–5
  health, at roughly 2 damage per attacker per round against 8–14 health pools.
- **Partial terms.** Characters repeatedly negotiate deals covering *some* of the people present ("Your gold
  and your legs, priest. GO. Rowan stays."). The engine models one offerer and one recipient.


## The stateless resolver cannot see state — and two rules depend on it

A second v0.7 play-test (`runs/20260820-080524Z-6da9b8d3`, 12 rounds, 1626 events) found the sharpest
architectural edge in the design so far, and it is worth stating plainly because it is not a bug in any one
component: **the Rulebook Resolver is stateless on purpose, so it cannot resolve any rule whose meaning
depends on the state of the room.** Two of the world's rules are exactly that.

In that run three offers of surrender were made and `accept_surrender` was chosen **zero** times. The
recipient said, in as many words:

> "I take Rowan's small purse of gold coins, **telling him he may live and leave**, then I do the same with
> Elara's purse"

The resolver — which sees the intent and the cards and *nothing else* — cited `inventory.steal` and
`encounter.offer-surrender`, reasoning perfectly well from the rulebook alone that somebody was grabbing a
purse and talking about terms. It cannot know that two offers of that very purse were pending, because
pending offers are state. The DM was then narrowed to `[steal_item, offer_surrender, reject_action]`, so it
could not have accepted even if it wanted to — and the steal counted as a hostile act, which *rejected the
offer it was accepting*. The same blindness refused a goblin three times over "loot the gold from dead
Rowan": nothing can be stolen from a corpse, and the resolver had no way to know the target was dead.

No card wording can fix this. The previous session added exactly the right recognition vocabulary to
`encounter.accept-surrender` — and it changed nothing, because the component choosing the card is not the
component that knows an offer exists. **State-dependent meaning has to be decided where the state lives.**
So it is decided in two places, in the established prompt-for-prevention/guard-for-guarantee pattern:

- **The candidate tool set is widened from state** (`TurnCoordinator.WidenCandidateToolsForState`). When
  terms are pending for the acting character, `accept_surrender` is added to whatever the resolver named,
  with a note to the DM headed `STATE THE RULEBOOK COULD NOT SEE` naming the open offers. When the resolver
  says "theft" and a body lies in the room, `take_item` is added the same way. The resolver stays blind; the
  DM, which *does* hold the snapshot, gets the option. An unsupported intent is never widened, so the
  fail-safe stays closed.
- **The action is redirected deterministically** before it reaches the engine, next to the existing
  informational-basis gates. If the acting character is the named recipient of a pending offer and reaches
  for what that offer promises, the grab *is* the acceptance, whatever tool the model called. If a theft
  names a dead character, it is a take from their body. Both are recorded on the dispatch decision, so the
  correction is auditable rather than silent.

The redirect is deliberately narrow: only the *promised* items, and only for the *named recipient*. Grabbing
anything else the offerer carries is still an ordinary theft and still kills the offer. The direction is the
honest one for the fiction too — you cannot take the tribute and keep fighting, because taking what was
promised *is* agreeing to the terms it was promised under.

Three further findings from the same run:

**A demand was recorded as the speaker's own surrender.** In round 1 a cleric at full health raised her mace
and threatened a goblin — "if he drops his salve and purse now, I'll spare him" — and the world recorded
*her* surrendering and promising *her* purse. That is why the play-test read as "they tried to surrender very
early when not injured": they were not surrendering, they were issuing ultimatums. `encounter.offer-surrender`
now leads with whose fight is being given up, and both the card and the character prompt say that demanding an
opponent yield is speech and nothing else.

**A dead offer never says it is dead.** A character offered terms in round 4, was refused in the same round,
and then spent rounds 9, 10 and 11 doing nothing but bracing "while Vark considers my offer". She was told
once, by narration, and that narration was summarised out of her history; her self-state had quietly gone back
to "you have offered none", which contradicts nothing. A settled offer of the character's own now stays on the
state as `LAPSED` or `DEAD`, with "do not wait for an answer" attached. **State that has to correct a stale
belief must contradict it explicitly — going quiet reads as "still waiting".**

**A body is public, and the take gate did not know it.** The informational-basis gate for `take_item` exempts
the ground (things dropped in plain sight) but treated a corpse as an ordinary container. A body holds only
what its owner was already carrying openly, and everyone watched them fall, so nothing hidden is revealed by
reaching into one. Corpses are now exempt alongside the ground.

## Structure instead of pattern-matching: speech and machinery leakage

Two mechanisms in this harness were built as text heuristics and grew a keyword per run: speech detection
(quoted text near a speech verb) and machinery-leak detection (a regex of rule-flavoured phrases). Both were
refactored to work from structure instead. The recurring symptom was the same in each case — every live run
found a phrasing the list did not have — and so was the diagnosis: **the set of ways to say something in
English is unbounded, so a list of ways is never finished.**

### Speech is declared, not detected

A character now names what it says in an `utterances` field on the same call that carries its action,
question or pass:

```
take_action(intent: "I bring my longsword down on Vark's shoulder.",
            utterances: ["Elara, get behind me!"])
```

What is in `utterances` is spoken; nothing else is. Three consequences follow immediately, and each one was
a defect before:

- **Speech needs no verb.** Nothing is being recognised, so there is nothing to recognise it by.
- **Quoted text in an intent is just punctuation.** `I lift the blade called "Goblin's Bite"` says nothing
  aloud, and neither does `I say nothing and inspect the inscription "Goblin's Bite"` — which contains both
  a speech verb and a quotation, and was exactly the shape the old heuristic keyed on.
- **It costs no extra model call.** The words arrive in the reply that was already being made. A test asserts
  that speaking-and-acting takes exactly the calls that acting alone takes.

Speech is delivered *before* the action it rides on, because a warning is worth nothing after the blow it
warns about. The `say` tool remains for speaking and doing nothing else.

The old heuristic survives as a **narrow, traced fallback** for a reply that carried no tool call at all — an
old or very small model answering in bare prose — so a genuine attempt to talk is recorded rather than read as
silence. Every use raises `UnstructuredSpeechAttempt`, and an inferred line is still never put in the
character's mouth and broadcast; it is recorded and nudged. It was narrowed **by shape rather than by
vocabulary**: the speaker must be first-person ("I shout"), and the quotation must follow the verb with
nothing but an addressee in between. Those two structural requirements settle the blade-called-Goblin's-Bite
case and the inscription case at once — and settle the ones nobody has thought of yet, which a word list
cannot.

### Refusals render from a code, not from prose

An engine refusal used to travel through three components: the engine wrote an operator-facing message ("Vark
is not carrying 'Vial of Goblin Salve'"), a model turned it into fiction, and a regex tried to notice when the
model had explained the machinery instead. That arrangement had the causality backwards, and it cost two model
calls to sometimes produce a worse result than saying nothing — live runs saw rewrites invent obstacles that
were not true and narrate events inside refusals that by definition change nothing.

A rejection is not free-form information. It is a **closed set of reasons**, each with a handful of bound facts
the character is entitled to know. `InWorldRefusal` renders it directly from the `EngineRejectionReason` and
those bindings, resolving ids to the names a character would use. It cannot leak, cannot invent, cannot narrate
an event, and costs no model call at all. A test walks every value of the enum and fails the build if a new
rejection code has no in-world wording of its own.

### What is left of the detector

`MachineryLanguage` is now a final defensive lint over the one path still made of model prose — the Dungeon
Master's own `reject_action` wording. It matches only vocabulary that exists nowhere but the implementation:
tool names, stable identifiers (`offer-1`, `guard-ally`, `corpse-…`), and the machine's own nouns (`rulebook`,
`the engine`, `hit chance`, `world version`, `status effect`, `turn cost`). Everything broad came out —
`not allowed`, `ability`, `another character`, `the facts`, `a living ally`, `item transfer`. The tests assert
**both** halves of that contract: the machine terms are caught, and the rule-flavoured English that used to be
caught is deliberately, explicitly, no longer caught.

When it does fire, the behaviour is bounded: **one** rephrase attempt, the finding and the rewrite both traced
on `AdjudicationCorrected`, and a single deterministic fallback line if the rephrase still leaks. No judge
call, no second opinion, no loop.

**Markdown was separated out entirely.** Formatting is a presentation defect with a deterministic fix, so
`**CLOSED**` is now *cleaned* by `ModelText.StripPresentationMarkup` rather than treated as a semantic leak.
It used to sit in the same regex, which meant a stray asterisk triggered a whole rewriting model call and
risked losing words that were perfectly fine.

## Hidden information and character knowledge

Being in the same room does not mean knowing everything another character does. A closed container's
contents, and an object's exterior markings, are secret until a character opens or inspects it for
themselves; opening is a *public* fact (everyone sees the lid go up) but the *contents* stay private to
whoever actually looked. A `KnowledgeLedger`, separate from authoritative `GameState`, tracks who has
learned what fact, how (backstory, direct inspection, opening, or a public event) and at what world
version — so a character's knowledge can become stale without being silently rewritten when the world
changes. Speech is never promoted into this ledger: something another character *said* stays hearsay,
distinguishable in every prompt from something the character verified first-hand. The Dungeon Master is
always handed the acting character's exact knowledge view before it adjudicates, and is expected to rule
strictly inside it. Since v0.7 the *answering* path goes further and gives the DM no authoritative state at
all — only the bounded projection described in [Grounded answers](#grounded-answers-answerfacts) — because
a prompt boundary the model can see past is one it will eventually step over.

## Inventory transfers and the Rulebook Resolver

A character can `give_item` (to anyone present, ally or foe — consent is not modelled), `drop_item`
(onto the room's ground-loot collection, an always-open container any present character can `take_item`
from afterwards), or attempt `steal_item` (one seeded RNG roll, `Combat:BaseStealChance` default 40%,
**always publicly noticed win or lose**). A fallen character's belongings drop into a lootable **corpse**
container (its own body — narrated as a body, never as an "open container"). An equipped weapon can never
be given, dropped or stolen.

**An item reference that names two items names neither (v0.8).** Characters, objects, exits and container
contents have always reported *ambiguity* separately from *not found*, so the engine could refuse with
"say which one" rather than guess. A character's own inventory did not: it ran a chain of first-match
lookups and silently returned whichever item sat first. A live run found the gap — Vark carried two purses
both reading `Small Purse of Gold Coins`, qualified `(the runt's)` and `(the captain's)`, Rowan tried to
steal "Vark's purse", and the Dungeon Master **spent its entire adjudication output budget reasoning aloud
about which one was meant** instead of calling a tool ("But which purse? The intent doesn't specify"). It had
diagnosed the problem correctly; the engine simply offered it no way to say so, and would have handed over a
purse the reference did not single out. `ItemReference.Resolve` is now the one rule for both inventories and
containers: id first (never ambiguous), then the **qualified** display name — which is what keeps two
identical purses referenceable at all — then the plain name, which is where ambiguity is reported. The
refusal **lists the qualified alternatives**, because a bare "that is ambiguous" hands a model back the same
words it just used.

**Informational-basis gates (both `steal_item` and `take_item`).** A character can only reach for an item
it has a legitimate reason to know is there — having seen it carried/taken/given/dropped, opened or
inspected the container, known it from backstory, or been told of it. This is enforced *authoritatively*,
in the coordinator, **before the engine** (`StealLacksKnowledgeBasis` / `TakeLacksKnowledgeBasis`), not
just by the DM prompt — so **a Dungeon Master model that leaks hidden state cannot turn that leak into a
valid mutation** (a real v0.6 bug: a weak DM told a character what was in a case she'd never looked in,
then `take_item` accepted it). The floor (ground loot, dropped in plain sight) is exempt. Every item
movement is atomic (it exists in exactly one place before and after) and fully traced, so an item's whole
journey through a run can be reconstructed from the report.

Rather than growing the Dungeon Master's system prompt with every new action's rules, v0.6 introduced a
**Rulebook Resolver**: before each `take_action` is adjudicated, a retriever (`RuleRetriever`) gathers the
**rule cards** and a separate, **stateless** low-temperature model call (`Agents:RulebookResolver`) reads
only the raw intent plus those cards — never live state, never history, never hidden knowledge — and
returns structured guidance: which action(s) the intent could be, under which cited rule card(s), or that
nothing fits. **Retrieval is *not* keyword routing: it sends the whole (small) rulebook every time and
lets the model do the semantic selection.** The first design scored cards by keyword and sent a subset,
which repeatedly dropped the one relevant card (an escape intent that never says "escape", a take intent
that says "seize") and produced false refusals — so the routing was removed. `RulebookMaxCards` /
`RulebookMaxInputChars` became a hard ceiling that fails loudly if the catalog outgrows a bounded request,
rather than a budget that silently trims (a trimmed card is a hidden action). If the rulebook ever grows
large enough that sending all of it is genuinely too big, *that* is when to reintroduce real retrieval —
deliberately, not by accident. The Dungeon Master is then handed only the narrowed tool set the guidance
selected (plus `reject_action`), never the full action surface, and binds that guidance to the actual
authoritative state. Guidance can be cached
(`RulebookCacheEnabled`) but a cache key always includes the rule cards' versions, and nothing
encounter-specific (bindings, targets, RNG results) is ever cached. The whole stage can be disabled with
`Harness:EnableRulebookResolver: false` to fall back to the DM adjudicating directly, useful for
scripted tests that don't want to stand up a resolver client. Every consultation — cards retrieved,
cards actually sent, the resolver's raw request/response, validation outcome, cache hit/miss — is
traced, and the report's **Rulebook Consultations** section shows the supported/unsupported split and
confirms request size stays flat across a run rather than growing with the number of rounds played.

**v0.8 stopped asking the resolver to copy the card back.** Its schema (`guidance-v2`) now asks only for
`supported`, `candidateActions`, `citedRules` and `unsupportedReason`; the consultant fills the bindings,
preconditions, turn cost, randomness, visibility and behaviours from the cited cards themselves
(`RulebookConsultant.Hydrate`). That makes the guidance the rulebook's own words rather than a model's
paraphrase of them — and it decouples the reply's length from the cards', which was not theoretical: v0.8
lengthened one card and **8 of 16 resolver replies in the next live run truncated mid-JSON** at the
output cap, each surfacing as a refusal of an ordinary attack.

**The resolver is shown only what an action IS and IS NOT.** Since hydration fills the consequence fields
from the cited card, sending them to the resolver was asking it to read text it structurally cannot use —
six of a card's ten fields. `RuleCard.ToResolverBlock()` renders summary, description, preconditions and
exclusions only, which took the card text from 31,245 to 18,261 characters and a consultation from ~8,200
to **~5,260 estimated input tokens**, with no model call and no recall risk. That mattered for more than
cost: at 8,200 the request left ~127 tokens of an 8,192-token window to reply in, and a live 12-round run
lost 5 of 57 consultations to replies truncated mid-JSON. `RulebookRequestBudget` now refuses to start a run
whose book cannot fit the resolver's window with room to answer, because that failure is silent otherwise.

**v0.8 also added experimental card selection, behind `Harness:RulebookSelectionMode`** (default
`WholeRulebook` — the path described above, unchanged). `CompactIndex` shows a model one line per card
and resolves with only the ids it picks; `Embedding` retrieves a top-K by similarity
(`RulebookEmbeddingModel`, `RulebookSelectionTopK`); `StructuredRouting` selects by declared metadata for
a caller that already knows the action family. Every one of them falls back to the whole bounded rulebook
when it is not confident, and traces its ids, its reasons, its declared-link expansions and any fallback.
The measured comparison — including which of them quietly drops the card a decision turns on — is in
[`reports/rulebook-efficiency.md`](reports/rulebook-efficiency.md).

## Randomness and seeds

Combat now rolls dice. Each character has a hidden `HitChance` (scenario stat, default 75): an attack
rolls d100 and lands when the roll is at or under the chance, otherwise it misses. A landed hit rolls
**once** for quality, and that one roll selects among all three bands — glancing
(`Combat:GlancingBlowChance`, default 25, half damage), solid, or critical
(`Combat:CriticalHitChance`, default 25, double damage). A theft attempt (`steal_item`) rolls once
against `Combat:BaseStealChance`, and an open threat (`intimidate_character`) rolls once against
`Combat:BaseIntimidationChance` plus modifiers read only from the state of the fight. Every one of
these chances is narrated only in-world, never shown to characters or narrated as a number. All rolls
go through a single `IRng`, so nothing is random except through it.

One master seed governs the whole run — the dice and every agent's model sampling (the Dungeon Master,
each character, and — when enabled — the intent parser, history summariser and Rulebook Resolver):

- **`Harness:Seed` set** → fully deterministic. The same seed reproduces the same run (verified: identical rolls across runs). Use this for testing.
- **`Harness:Seed` blank** → a random master is generated, printed, and recorded. Real runs use this.

Either way the seed is recorded in `run.json` (master, derived game seed, and each agent's derived
seed) and printed at startup, so **any run — including a random one — replays by setting `Harness:Seed`
to the recorded value.** The per-agent seeds are derived from the master, so they are fixed under a
fixed run, random under a random run, and always distinct from one another.

Every RNG draw is traced in full — not just the final hit-or-miss. Each `RngDraw` event records the
action and actor/target that caused it, what it was selecting (hit-or-miss, the three quality bands and
their exact ranges, a threat telling or not), the
candidate range, the raw roll, the **base** chance, every **modifier** applied to reach the effective
threshold, the result, the seed, and the generator's sequence position before and after the draw. That is
enough to reconstruct and compare any draw in isolation, and two runs with the same accepted actions and
seed produce identical engine outcomes and final state.

Since v0.7 a status effect can change a hit chance, so a draw's threshold is no longer simply the actor's
own stat. Each modifier is recorded twice over: as a readable note, and as a structured `RngModifier`
carrying the status or ability that caused it, its source character, its signed value, its position in a
**fixed** application order, and whether that draw consumed it. Recording only the effective number would
make a status-modified roll impossible to compare across runs, which is the whole point of the record:

```text
Purpose: attack.hit-check
Base hit chance: 60      (Skrit)
Rallied         +15      from goblin-vark  (order 1, consumed)
Effective:       75
Raw roll:        70  →  hit
```

The engine also mints its ids from monotonic counters rather than GUIDs — `status-3`, `offer-1`,
`agreement-1`, `guard-2` — so a fixed-seed run reproduces the same *ids* as well as the same rolls, and
two traces can be diffed line for line.

## Layout

```text
src/ModelsAndMonsters/
    Agents/          conversations, character + DM agents, declaration-only tools
    AI/              model profiles (with per-agent inheritance), provider capabilities, chat client factory
    Configuration/   options and scenario definitions (teams, per-character profiles, exits, abilities, rulebook flags)
    Domain/          characters (team, disposition, abilities, fear), weapons (with stable ids), items, injuries,
                     room, exits, environmental cover objects (v0.9), status effects (including InCover, v0.9),
                     surrender offers and agreements, intimidation attempts, the one health-band definition
                     (v0.8), the morale rules (v0.8), game state
    Engine/          game actions (each stating its own ExposesActor, v0.9), results, combat rules (the
                     three-band quality draw + intimidation modifiers, v0.8), RNG draw records + modifiers,
                     turn upkeep, team terminal condition, deterministic in-world refusal rendering (v0.7),
                     the engine (including the three-way covered-attack resolution and cover-exposure
                     query/apply pair, v0.9)
    Knowledge/       the knowledge ledger — facts, sources, per-character learned records, backstory seeding
    Orchestration/   simulation runner (fixed turn order, team terminal check), turn coordinator,
                     narration log, the deterministic AnswerFacts projection (v0.7)
    Presentation/    console output
    Prompts/         prompt library, state formatting, templates (DM prompt is modular: core + a rules block
                     per job — see "Version history" below for why)
    Randomness/      IRng (with sequence position), seeded rng, master-seed + per-agent derivation
    Rulebook/        rule cards, retrieval, the resolver, guidance validation and caching (v0.6)
      Selection/     experimental card-selection strategies, the labelled corpus and its evaluator (v0.8) —
                     all behind Harness.RulebookSelectionMode, which defaults to the shipped whole-rulebook path
    Tracing/         trace sink, event models, tracing chat client, run artefacts, report writer
src/ModelsAndMonsters.Web/   live SSE-driven observer UI (React client under client/)
tests/ModelsAndMonsters.Tests/
docs/prompts/        the original build spec for each release (build_v0_1.md … build_v0_8.md)
reports/             field notes and known-issues write-ups per release — see below, plus
                     rulebook-efficiency.md (v0.8): the measured comparison of rule-selection strategies
```

Morale (`Domain/FearRules.cs`) is the third: `Character.Fear` is the value, `StatusEffectKind.Scared` is
its public shadow, and `GameEngine.ApplyFear` is the only thing that moves either. A new reason for fear to
move means a new `FearChangeCause` and a call to that one method — never a second way to write the number.

The ability book (`Domain/Ability.cs`) and the rule catalog (`Rulebook/RuleCatalog.cs`) are the two places
a new ability touches: a definition with one `AbilityEffectKind`, a concrete handler in `GameEngine`, and a
rule card so the resolver can recognise it from a character's own words. Statuses and surrender contracts
live in `Domain/StatusEffect.cs` and `Domain/Surrender.cs`, and hang off `GameState` rather than off a
character, because a paired relationship spans two.

## Version history and where to look

Each release started from a spec in `docs/prompts/build_v0_<n>.md` — read the spec for the version
you're extending *before* changing its area, since prompts and validation are often tuned around
exact wording that isn't obvious from the code alone.

v0.8 also added two more kinds of document:

- [`docs/architecture.md`](docs/architecture.md) — components, the five agents, a character's turn end to
  end, every configuration lever, and the AI techniques used, named. The best starting point for anyone new
  to the codebase.
- [`reports/rulebook-efficiency.md`](reports/rulebook-efficiency.md) — a measured comparison of four
  rule-selection strategies against a labelled corpus, reproducible from
  `dotnet run --project src/ModelsAndMonsters -- --rulebook-eval`. Read it before touching the rulebook
  stage: it says which strategy is worth implementing next and, more usefully, which one looks attractive
  and quietly drops the card a decision turns on.

After each release, two write-ups were kept in `reports/`:

- `v0_<n>_notes.md` — real dialog and emergent behaviour pulled from actual run traces (what the
  models actually did, including funny or surprising moments) — useful for calibrating expectations
  about what a "natural" run looks like on a given model.
- `v0_<n>_issues.md` — problem → fix pairs actually found and resolved while building or live-testing
  that release, each with the root cause and the file(s) touched. **Read this before re-diagnosing a
  symptom that looks familiar** — a surprising number of issues here are model-behaviour quirks
  (a weak DM inventing a name, an unstructured prose reply, a machinery leak) rather than engine bugs,
  and the fix pattern (harness-side correction + prompt hardening, never trust-and-hope) generalises.

A few things worth knowing that aren't obvious from reading any single file:

- **An optional component must never be able to end a run.** The intent parser exists to salvage a reply a
  character malformed; everything it does was survivable before it was added. It had no failure path, so
  when its provider returned 500 the exception unwound through the turn, the round loop and the run
  (v0.8, `reports/v0_8_issues.md` §11). Every convenience component is worth checking for this.
- **Check the provider's sampler log before blaming the model.** A parameter the harness does not set takes
  the model's default, which for a chat-tuned model is tuned for conversation. Every local call in this
  project carried an inherited `presence_penalty = 1.5` — a knob that discourages exactly the exact-string
  repetition tool calling depends on — set by nobody and recorded nowhere until v0.8.
- **A retry that does not genuinely re-sample is not a retry.** The transient-retry path moved the seed and
  lifted temperature to 0.1 — near-greedy — and reproduced the identical malformed reply three times. One
  live run contained both outcomes at once: an agent at 0.8 recovered on its third attempt while the
  temperature-0 agent did not recover at all.
- **Prompt-shaped guarantees are prompts; enforce anything that must hold on structured state.** v0.8 added
  three more deterministic guards for meaning the deciding component cannot see (see §4.4 of
  `docs/architecture.md`), and every one of them exists because a model reasoned correctly from what it had
  and still got the wrong answer.
- **Read `reports/v0_8_notes.md` for what a natural run sounds like** before concluding a model is
  misbehaving. Several things that look like bugs — a goblin attacking his own captain, a character
  announcing that he is muttering to himself — are personas and declared fields working exactly as designed.

- **The Dungeon Master never carries a growing conversation.** Every task — narrate, answer, adjudicate
  — runs on a fresh *projection* (system prompt + only that task's context), because measured evidence
  showed narration/answering history in the DM's context measurably degraded its adjudication accuracy
  (see `ProjectDungeonMasterContext` above). The v0.6 Rulebook Resolver is stateless for the same reason,
  one level further out.
- **A local model's context window is a real, sharp cliff, not a soft degradation.** Ollama silently
  truncates a request that exceeds `num_ctx` (sometimes at *half* the configured value) with no signal
  in the response — the harness detects this by comparing sent-vs-reported input size
  (`ContextWindowSaturated` in the trace) rather than trusting the configured number. When a release
  grows the DM's prompt, re-check this on the smallest model you care about before assuming behaviour
  regressions are a logic bug.
- **A batch of separate live-testing runs can 500 for a reason that has nothing to do with this app:
  changing `ContextWindow` between invocations forces Ollama to reload the model.** `num_ctx` sizes the
  `llama-server` runner process itself, so a different value needs a different runner; mid-reload, this
  app's next request can hit a not-yet-ready runner and get back `500 llm server error` — visible only
  in Ollama's own `server.log`, not this app's trace, and easy to misdiagnose as OOM or a code bug
  (confirmed by log: zero CUDA/allocation errors, just a reload race). Keep `ContextWindow` constant
  across a batch of seed runs, and pre-warm the model once (`OLLAMA_KEEP_ALIVE`) beforehand, to avoid
  it. Raising `OLLAMA_MAX_LOADED_MODELS` is not a fix either: each loaded instance is a **separate
  process with its own full copy of the model weights** plus its own KV cache — there is no
  cross-process weight sharing — so two loaded instances just double VRAM pressure rather than letting
  two context sizes share one resident model.
- **v0.8's prompt additions cost the character context roughly 340 tokens, and the window is the binding
  constraint.** A v0.7 live run peaked at 8,160 tokens of character request against an 8,192 window; the
  smoke run for v0.8 peaked at 7,899 with the nerve section in, which is close enough that the section was
  deliberately trimmed to about half its first draft before release. If you add anything to
  `character.system.md`, measure the peak `InputTokens` in a real run before assuming it is free — and
  trim rather than raising `ContextWindow`, because raising it takes the model out of memory.
- **`--rulebook-probe "<intent>"` asks the live resolver what it makes of one phrasing, in seconds.** Card
  misroutes are otherwise only visible buried in a forty-minute run's trace, and they are the single most
  likely thing to be wrong after adding an action. v0.8 shipped with two: "I catch Elara's eye and tell her
  to hold the line" routed to **intimidation**, and "I level my sabre at Rowan and tell him what is coming"
  routed to **attack** despite the attack card explicitly excluding a levelled weapon that strikes nothing.
  Both were fixed by card authoring, not by keywords — each card now leads with the SIDE it is aimed at and
  names the other as what it is not, the pair moved up beside the rest of the combat family instead of
  trailing at the end of twenty-one cards, and the attack card's exclusion now says where such an intent
  goes rather than only that it is not an attack. Probe after any card edit.
- **A resolver reply can come back malformed because the model wrapped its JSON in a think block**, which
  `ModelText.Clean` strips, leaving a stray `{`. It is pre-existing (v0.7 runs show it too, at a low rate),
  it fails safe as an in-world refusal, and it happens *despite* `Effort: none` and `Thinking: false` being
  correctly applied to that agent — verified in `run.json`. If a specific intent reproduces it, that is the
  cause; look at the raw reply (`--rulebook-probe` prints it for a malformed outcome) before suspecting the
  card or the schema.
- **A weak or small model reliably tries things the engine doesn't support**, and the fix is almost
  always to make the boundary explicit in the prompt/state text (so the model stops discovering it by
  failing repeatedly) rather than to add the mechanic reactively. Past examples: passing items hand to
  hand before `give_item`/`steal_item` existed, blocking someone's escape, inventing a name for the
  room's one exit. If you see a model hammering on the same refused intent across a run, check
  `reports/v0_<n>_issues.md` first — it may already be a known, fixed pattern; if it's new, the
  harness-correction + prompt-hardening pattern used there is the template to follow.
- **Every provider/model combination behaves differently enough to be worth spot-checking live**, not
  just against the test suite. The deterministic tests prove the *mechanics*; they cannot prove a given
  model's prompt-following, tool-call reliability, or context behaviour — that only shows up in a real
  run's trace. A change that passes every test can still change live behaviour materially (verified
  repeatedly this project: prompt wording, prompt size vs context window, and provider choice all move
  the needle on their own).
- **A retry that resends the identical request cannot save a temperature-0 agent.** `ModelAgent`
  retries a transient provider 5xx (Ollama has been seen 500 on its own tool-call XML parser choking on
  a malformed reply it generated) — but at temp 0, sampling is greedy and the seed is ignored, so a
  naive resend reproduces the *exact same* malformed output and 500s again, identically, until the
  retry budget is exhausted (this is exactly how the stateless, temp-0 intent parser and Rulebook
  Resolver can fail). The fix a retry needs is to actually change the draw: floor the temperature to a
  small non-zero value (never *lowering* an agent already above it) and offset the seed by the attempt
  number on retry only — attempt 1 still uses the agent's exact configured profile, so a failure-free
  run still replays identically under a fixed seed.
- **A capable local model (qwen-class, ~8–9B+) is roughly the floor for adversarial multi-actor play; a
  much smaller model (llama3.2:3B tested) is not.** Run as all four characters, a 3B model dissolved an
  explicit hero-vs-goblin death match into a cooperative wound-care circle — goblins doing safety
  warnings for the heroes they were fighting, heroes reassuring the enemy — *even with* the enemy-naming
  prompt above spelling out "offer them no aid." That is a model-capability floor, not a prompt gap:
  pushing the prompt harder to compensate risks making capable models recite adversarial framing like a
  script instead of playing it. If characters are drifting cooperative on a small model, check its
  parameter class before re-tuning the prompt further.
- **Hidden information must be enforced *authoritatively* (a deterministic gate), not just in the DM
  prompt — because a weak DM model will leak.** The single most serious v0.6 bug: a weak DM answered a
  character's question from authoritative hidden state ("the case holds a healing draught") to a character
  who had never looked inside, and `take_item` then accepted her taking it — a prompt-leak became a real
  state mutation. The fix is a pre-engine informational-basis gate in `TurnCoordinator`
  (`TakeLacksKnowledgeBasis` / `StealLacksKnowledgeBasis`) checking the acting character's *own* knowledge
  ledger, so the take/steal is refused with no mutation regardless of what the DM said. General rule: the
  DM prompt is *prevention*; a deterministic gate is the *guarantee*. Model prose (a DM answer) can still
  leak wording, but it can no longer change the world.
- **For a small rule set, "retrieval" is a trap — send it all and let the resolver decide.** v0.6's first
  Rulebook Resolver scored cards by keyword and sent a subset; that repeatedly dropped the one relevant
  card (an escape intent that never says "escape"; a take intent that says "seize") and produced false
  refusals. Removing the routing entirely — send the whole book every call (12 cards at v0.6, 18 at v0.7), with a hard *ceiling*
  that throws rather than a budget that trims — fixed the whole class. Semantic selection is the resolver
  model's job, not a keyword index's. Only reintroduce real retrieval if the book genuinely outgrows a
  bounded request.
- **Budget a character's history against the *real* prompt, not a message-only estimate.** The token
  estimator counts message text but not the tool schemas / chat-template scaffolding a tool-bearing call
  also carries (~2000 tokens on qwen), so summarising to a flat 5500-token "budget" left the real prompt
  at ~7600 in an 8192 window and truncated anyway. Derive the trim target from the window and reserve
  output room, and learn the overhead from the provider's reported input size (see context section, point
  3). This is the same "estimate ≠ what the model saw" lesson as `ContextWindowSaturated`, one layer in.
- **Reviewer-found polish worth keeping in mind when adding fiction:** narration must not leak the engine's
  representation — a corpse is a **body**, not "an open container you reach into" (`Container.IsCorpse`
  drives body-appropriate narration); intimidation/posturing ("a threatening step", "raise my weapon
  ready") must stay speech/unsupported, never silently become `attack_character` (the `combat.attack` card
  now demands *contact*); and what a character openly carries in this no-distance room is an **initial
  public observation** everyone else makes (`KnowledgeSource.PublicEvent`), not "backstory" they knew
  beforehand. Friendly fire is deliberately *permitted* (the world resolves the strike a character
  described) and is recorded as a coherence metric (report's "Allied attacks"), not blocked.
- **A mechanic a model can invoke unilaterally will be invoked unilaterally.** v0.5's `surrender` cost
  nothing: declare it, become untouchable, keep everything. That made "I surrender" the cheapest move in
  the game whenever a fight turned, and made intimidation meaningless — there was nothing to *offer*,
  because yielding was free. v0.7's fix is not a harder prompt but a *price and a counterparty*: an offer
  must promise something the offerer actually owns, it protects them from nothing while it stands, and only
  the named opponent can end their fight. If a new mechanic can be triggered by one model alone with no
  cost, expect it to become the default move, and design the cost in from the start.
- **Withhold the material, don't instruct against using it.** The v0.6 pattern was "the DM prompt is
  prevention; a deterministic gate is the guarantee". v0.7 goes one step further for answering: the DM is
  no longer *given* the authoritative state when it answers a question, only a deterministic projection of
  what the asking character may be told. A model cannot leak a closed container's contents it was never
  handed, cannot say a consumed potion is still carried when the projection derives from current state, and
  cannot promise flanking when the projection states flatly that no such thing exists. The instruction and
  the gate both remain — but the cheapest fix for "the model answered from something it shouldn't have" is
  usually to stop sending it.
- **A closed affordance list is worth more than any "don't invent actions" instruction.** The projection
  ends with "There is nothing else. No other kind of action exists in this world." Everything before it is
  derived from state, so a spent ability is absent, a strike is absent for empty hands, and an acceptance
  appears only for the named recipient of a live offer. That is what lets the DM answer "what can I do
  here?" without either inventing something or retreating into "you cannot tell".
- **An action that used to change nothing on failure may not any more.** A missed attack was a clean
  no-op: state before equalled state after and the version was untouched. With statuses, ability charges
  and pending offers, a *miss* can consume a Rallied modifier, spend a Dirty Strike charge and reject an
  offer. The engine now tracks whether anything actually changed rather than assuming from the outcome, and
  the version is bumped on that. Any release that adds state touched by resolution should re-check every
  "nothing happened" path for the same reason.
- **Turn upkeep has to run before the actor is shown its own state.** Statuses expire at exact turn
  boundaries, and a character is told its live statuses every turn. Expiring after rendering told a
  character it still had a guard that had already fallen away — a lie the model then acted on. `BeginActorTurn`
  runs before anything is rendered or asked; `EndActorTurn` after the turn resolves. Upkeep consults no
  randomness and only bumps the world version when something really changed, so a turn where nothing
  expired is indistinguishable from a pre-v0.7 one and existing tests keep their meaning.
- **A paired status must not be able to exist on one side.** `Guarding` on the guardian and `Guarded` on
  the ally are one relationship, so they live on `GameState` with a shared `RelationshipId` and are always
  removed together — expiring, consumed by a redirection, or swept when either party dies, surrenders or
  escapes. Putting statuses on `Character` would have made "guarded by nobody" representable, and something
  eventually reaches every representable state.
- **A number in a character-facing description gets said out loud.** The Healing Prayer ability description
  originally read "restoring a fixed four health"; the character promptly narrated *"four health restore to
  me"* mid-fight. The rule cards keep the mechanics ("markedly more likely to land"), and the
  character-facing `AbilityDefinition.Description` is deliberately number-free. If a value must be exact
  for the engine, keep it out of the prose a character reads.
- **A setting calibrated for one context window quietly binds on a larger one.** `HistoryTokenBudget` was
  5500 — right for an 8192 window — and is an *upper bound* over a value derived from the window. While a
  12288 window was being trialled it was still the binding constraint, so runs with ample room summarised
  early and lost fidelity for nothing. It is 9000 now, which changes nothing at 8k (the derived figure wins
  there) and stops the cap being what limits a larger window. Check both halves of a `min()` when you widen
  one. Note also what history trimming *cannot* do: it never touches the fixed cost of a system prompt or a
  turn context, which is why v0.7's character-side fit was solved by `RecentTurnsKeptFull`, not the budget.
- **A report that measures the thing wrongly is worse than no measurement.** "Context health" summed each
  traced message's concatenated `Text` *and* the `Contents` blocks that text came from, reporting every
  request at roughly double its real size — precisely the wrong direction when the number exists to judge
  headroom against a context window. It now prefers the concatenated text and falls back to the blocks only
  for a message that has none.
- **Count discarded tool calls apart from discarded prose.** A surplus tool call after the turn resolved is
  a model trying to act twice; loose text alongside an accepted call is usually just flavour. Both are
  discarded and both are traced, but folding them into one "post-resolution output discarded" figure made
  harmless narration read as a protocol breach.
- **Speech will try to do the contract's job, and must be allowed to fail at it.** In live runs a goblin
  captain negotiated aloud in detail — offering his purse on conditions — while still attacking, and a
  hero then tried to "accept" that spoken offer. Nothing was ever pending, because speech is not an offer:
  her reach for the purse resolved as an ordinary (noticed, rolled) theft instead. That is the intended
  shape — the argument is in the speech, the contract is in the structured offer — but it means a run can
  look like a negotiation while no negotiation state exists at all. Check the report's **Surrender
  negotiation** section, not the transcript, for what was actually on the table.
- **An offer bundled with a blow loses to the blow, and both the card and the character need telling.**
  Across four live runs the resolver repeatedly *did* propose `offer_surrender` as a candidate — for intents
  like "I step in front of Elara and take the next blow, while telling Vark once more he can have my
  gold" — and the Dungeon Master correctly picked the physical action, because that is what the character
  said they were doing. Correct adjudication, but the negotiation never became state. The fix was to make
  the exclusivity explicit in both places that decide: the `encounter.offer-surrender` card's turn cost now
  says putting terms on the table is the *entire* action and cannot be combined with a strike, a guard or a
  brace, and the character prompt says the same in their own terms. After that, a badly-wounded Vark with
  his grunt dead offered Elara his purse, his salve *and* his sabre as his whole turn; she accepted three
  turns later; the tribute moved, the sabre landed on the floor as a trophy, and the encounter closed as
  `Mixed` (one killed, one surrendered). A second offer, made to Vark *after* he had already yielded, was
  correctly `Invalidated` rather than left on the table.
- **A weaker model's judgement fails in the refusal text, not in the state.** An 8-round v0.7 run on
  `qwen3.5:4b` (same prompts, same 8192 window) produced **zero** engine rejections, **zero** context
  saturations, **zero** truncated replies and **zero** discarded post-resolution output — but leaked the
  machinery into its in-world refusals **eight times**, and the deterministic detector caught every one:
  *"the world cannot resolve forcing an item into another person's hand"*, *"There is no mechanic for…"*,
  and once the informational-basis rule recited verbatim as bookkeeping (*"You cannot take an item from
  someone's inventory unless you have seen it carried, taken, given, dropped, or been told of it"*). Each
  was rephrased in-world before it reached a character, and each correction is in the trace. Meanwhile all
  13 of its questions were answered from the bounded projection with **no** answer needing correction —
  the DM had nothing ungrounded available to leak. The pattern to expect from a smaller model here is not
  invalid state; it is prose that breaks the fiction, plus hammering on refused intents (this run hit the
  per-turn attempt limit 6 times). Both are bounded by the harness rather than by the model's judgement,
  which is the whole design.
- **A persona's own threshold can hide a whole mechanic.** Vark's inherited v0.6 personality only
  contemplated yielding when "cornered **alone**" — so through two deliberately hopeless runs, with both
  goblins barely standing, neither ever offered terms: each was waiting for the other to fall first. The
  mechanic was working; the characters' stated conditions were unreachable. Loosening that to "badly cut
  and plainly beaten, whether or not the runt is still on his feet" is scenario data, not orchestration,
  and it matches what the release actually asks of the motivations. If a feature never fires autonomously,
  read the personas before suspecting the engine.
- **A rule can be correct and still unreachable.** The v0.7 play-test refused a character three turns running
  while it tried to accept a surrender, because a card that described accepting perfectly never described what
  accepting *looks like* — reaching out and taking the promised thing, which reads exactly like a grab. Write
  the recognition vocabulary from the outside; see
  [What a live v0.7 run showed](#what-a-live-v07-run-showed-and-what-it-changed) for that and four more
  findings from the same run, all fixed.
- **Stale tool descriptions are live instructions.** `reject_action` still told the DM to use it "whenever the
  intent is not a direct weapon strike or an item use" — true in v0.2, wrong for eleven of the fourteen
  actions that exist now, and quietly recruiting refusals in every run since. Tool and parameter descriptions
  are prompt text; they need re-reading whenever the action set grows.
- **Look at what every character reads every turn before blaming the model.** The play-test read as
  gold-obsessed. The cause was one sentence in the encounter summary — seen by all four characters on every
  turn — plus purse language added to all four personas so each would know it had something to trade. v0.6 had
  that language in exactly one persona. Shared prompt text multiplies by the number of readers.
- **A stateless component cannot resolve a state-dependent rule, and no prompt makes it able to.** The
  Rulebook Resolver never sees the world, which is exactly right for rules about the *deed* and exactly wrong
  for the two rules whose meaning is the *situation*: accepting a surrender, and looting a body. A whole live
  run made three offers and accepted none, because the resolver could not know an offer existed and so never
  put `accept_surrender` in front of the Dungeon Master. Adding the vocabulary to the card had changed nothing
  the session before. See
  [The stateless resolver cannot see state](#the-stateless-resolver-cannot-see-state--and-two-rules-depend-on-it).
  The general lesson: when a decision needs information a component is deliberately denied, do not try to
  teach it — move the decision to where the information already is.
- **State that has to correct a stale belief must contradict it explicitly.** A character whose offer had been
  refused kept bracing for three rounds "while Vark considers my offer". She had been told once, in a
  narration that history summarisation later dropped, and her self-state had gone quietly back to "you have
  offered none" — which contradicts nothing. Going silent about a settled thing reads as "still pending".
- **Every decoration you render is a reference a model may hand back.** A Claude-driven run had the Dungeon
  Master pass `"Vial of Goblin Salve (restores 4 health)"` as an item reference, copied verbatim out of the
  state block, and a goblin was told the vial at his own neck was not there. Item lookup now peels trailing
  parentheticals after exact matches fail. If you annotate a name for display, expect to parse that annotation
  back — or do not put it in the name.
- **A stronger model can hide a design weakness rather than reveal it.** Sonnet's characters said "I accept
  Skrit's terms", and the word "accept" carried a state-blind resolver to the right card twice; qwen's only
  ever described the deed, and the same build failed three times running. Both runs were of the same code. A
  capability that only works when the character names the rule it wants is not working — it is being carried.
- **Diagnose the constraint you actually hit, or you will advise the opposite of the fix.** `FinishReason =
  Length` has two causes that share nothing: the output budget was spent, or the input had already filled the
  context window. The harness reported the first for four runs while the second was happening, and told its
  operator to raise `MaxOutputTokens` — which, on a shared window, shrinks the room for the prompt that was
  already too big. Measure `input + output` against the window before naming a cause.
- **A recovery step that grows the input cannot recover from a full input.** Nudging a truncated reply appends
  a message; against a full context window that guarantees the retry fails the same way, and a live turn spent
  three attempts proving it. Recovery from saturation has to shed, not add.
- **The data being in the trace is not the same as anybody seeing it.** Friendly fire was computed
  (`TargetIsAlly`), reported (an **Allied attacks** section), and still went unnoticed live for four runs — a
  guard-ally intent bound as a strike on the guarded ally, saved only by a missed roll. If a signal matters
  while a run is happening, it has to be surfaced while the run is happening.
- **An example in a prompt is a script, not an illustration.** The rephrase prompt offered "the wet stone
  underfoot" and "their own tired arms" as examples of an in-world refusal; across two runs three refusals used
  those exact phrases, including one telling a character the ground gave no purchase a turn before she braced
  on it. If you would not accept a phrase as the answer every time, do not put it in the prompt.
- **A guard that rewrites output can make the output worse.** The machinery-leak detector correctly caught two
  refusals and the rephrasings it produced invented a physical obstacle and narrated an event inside a refusal
  — both worse failures than the leak. A correction step needs its own constraints, not just the ban it
  enforces.
- **A list of ways to say something is never finished.** Both text heuristics in this harness — speech
  detection and machinery-leak detection — grew a keyword per run and still kept missing new phrasings,
  because the space of English phrasings is unbounded while the list is not. The fix in each case was to stop
  matching text and start reading structure: speech became a declared field on the character's own call, and
  refusals became a rendering of the engine's rejection code. See
  [Structure instead of pattern-matching](#structure-instead-of-pattern-matching-speech-and-machinery-leakage).
- **Narrow a heuristic by shape, not by vocabulary.** The legacy speech fallback could not be fixed by
  removing "called" from its verb list — that word appears in real speech too. Requiring a first-person
  speaker and adjacency between the verb and the quotation settled `the blade called "Goblin's Bite"` and
  `I say nothing and inspect the inscription "…"` in one stroke, and settles cases nobody has seen yet.
- **Separate presentation defects from semantic ones.** Markdown in a spoken line has a deterministic fix and
  no meaning at stake; a rules leak has neither. Folding them into one detector meant a stray asterisk bought
  a rewriting model call that could lose good words. Clean the first; lint the second.
- **A stray control character compiles.** A shell escape turned `\b` in a regex literal into an actual
  backspace byte three separate times in one working session. Each time the file built, the pattern parsed,
  and that alternative silently matched nothing — inside a guard whose entire job was to catch things. There
  is now a test that walks every source file and fails on any control character other than tab and newline.
