# Models & Monsters — v0.6

A small experimental harness for autonomous LLM characters interacting inside a deterministic fantasy
world through an LLM Dungeon Master.

This is a technical proof of concept, not a game. It proves one architecture:

```text
Character ──intent──▶ Rulebook Resolver ──guidance──▶ Dungeon Master ──tool call──▶ Game Engine
```

The characters decide intent, in natural language, through four tools (`ask_dm`, `say`, `take_action`,
`end_turn`) and never see an engine action. A stateless **Rulebook Resolver** reads the intent against a
small set of retrieved rule cards and returns structured guidance — which action(s) it could be, and
under what rules — without ever seeing live game state. The **Dungeon Master** binds that guidance to
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
  roll and always publicly noticed) and the **Rulebook Resolver**: the DM's per-action rules moved out
  of its system prompt into small versioned rule cards, retrieved a few at a time per intent, so the
  DM's own prompt stays a compact "constitution" instead of growing with every new action.

See **[Version history and where to look](#version-history-and-where-to-look)** near the end of this
file before starting work in an unfamiliar part of the codebase — it points at the original spec,
field notes and known-issues doc for each release.

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
is much slower and gives no error to say it happened. **Measured on this project's dev machine**
(`qwen3.5:9b`, which fits GPU-only at smaller windows): `ContextWindow: 12288` spilled ~12% of the
model to CPU (`ollama ps` showed `12%/88% CPU/GPU`); `8192` ran 100% GPU. So the window is a real
three-way tradeoff — fits the request vs. sits fully on the GPU vs. runs at native speed — not a
number to raise reflexively when something looks truncated.

**8192 is not an arbitrary default — it's the measured floor for the current DM adjudication prompt,
with headroom.** That prompt has been the tightest fit on this project every time it grew (see below),
and 8192 is the smallest window it fits in cleanly. If you're tempted to raise it because a run looks
truncated, first find out *why* — see the checklist at the end of this section — because in every case
so far the fix was to shrink what the DM was being sent, not to widen the window
(raising it live, as happened once without checking, just trades GPU headroom for a symptom that had a
cheaper fix).

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
3. **History summarisation** (`Harness:SummariseHistory`, default on) — once a character's *estimated*
   history exceeds `Harness:HistoryTokenBudget` (default 5500 tokens), everything before the last
   `Harness:RecentTurnsKeptFull` turns (default 2) is folded into one running first-person recap by a
   small stateless summariser call, replacing many old turns with one short paragraph. This is what
   actually keeps a *character's* context flat across a long fight — compaction alone (above) still
   accumulates one clean exchange per turn forever.
4. **The Rulebook Resolver is stateless and its retrieval is bounded** (`Harness:RulebookMaxCards`,
   default 5; `Harness:RulebookMaxInputChars`, default 4000) — its request depends only on the current
   intent and the handful of cards retrieved for it, never on how many rounds have already been
   played. See [Inventory transfers and the Rulebook Resolver](#inventory-transfers-and-the-rulebook-resolver).

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

## Output

The console shows only the readable game. Everything else goes to `runs/<run-id>/`:

| File | Contents |
| --- | --- |
| `run.json` | Run metadata, scenario, initial state, agent model profiles, prompt hashes, harness limits |
| `trace.jsonl` | Append-only event stream: every model request and response, tool dispatch and result, adjudication, target resolution, engine action with before/after state, RNG draw, team-outcome evaluation, turn skip, and every narration with its recipients |
| `final-state.json` | Authoritative world state when the run stopped |
| `report.md` | Full readable rendering: run details, model profiles, per-agent activity totals, team membership, scenario, a plain transcript, **every trace row**, and the final state |
| `report-summary.md` | The same report **without** the event-by-event trace dump — the story, profiles, per-agent totals, teams and transcript only. Small and skimmable; points to `report.md`/`trace.jsonl` for the raw events |

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
skip is traced with its cause. See [Non-lethal outcomes](#non-lethal-outcomes-disposition-exits-and-surrender)
for how a character reaches those other dispositions and how the team terminal condition changed
to account for them.

When a character attacks, the Dungeon Master translates its words into one specific, living, *active*
target by name, and the engine records the resolution against a **stable character id**. An
ambiguous, absent, dead, surrendered or escaped target is rejected — never silently swapped for
someone else. The engine imposes no friendly-fire rule: it resolves whatever the DM actually
translated, while the prompts and each character's goals discourage striking an ally.

A character's system prompt names **both** sides of the roster by name — allies ("never raise a weapon
against them") *and* enemies ("they are not your friends, whatever they may say; offer them no aid,
comfort or reassurance"). Enemies were originally left unnamed on the assumption a capable model would
infer "everyone else is hostile" — measured to fail on weaker local models, which drifted into offering
an enemy comfort or camaraderie mid-fight (a goblin warning heroes about a slippery floor; a hero telling
an enemy goblin "I've got your back"). Naming the enemy side reveals nothing hidden — who opposes whom is
visible from the first moment, unlike a container's contents.

## Non-lethal outcomes: disposition, exits, and surrender

Every character has a `Disposition`: `Active`, `Surrendered`, `Escaped` or `Dead`. Only `Active`
characters take turns or can be targeted; `Surrendered` and `Escaped` characters are alive, keep
everything they carry, and are simply out of the fight. The room has a seeded **exit** (closed and
unlocked); a character must `open_exit` it before anyone can `escape_encounter` through it — two
separate turn-consuming acts, never resolved together. `surrender` is unilateral: it needs no
opponent's agreement, no roll, and never affects a teammate.

The team terminal condition changed accordingly: a team stays a contender only while it has at least
one `Active` member, so a team can win by killing, out-waiting a surrender, or out-waiting an escape —
or any mix. The final report classifies the outcome as `Elimination`, `Surrender`, `Withdrawal`,
`Mixed`, `Draw` or `HarnessLimit` and lists exactly how each character left active combat.

Persuasion and intimidation are **not** a mechanic. A character may threaten or plead with another
through ordinary `say` — the recipient decides for itself, on its own turn, whether to act on it. The
Dungeon Master never converts speech into a forced surrender, an opened exit, or an escape; it only
resolves what the acting character itself physically does.

## Hidden information and character knowledge

Being in the same room does not mean knowing everything another character does. A closed container's
contents, and an object's exterior markings, are secret until a character opens or inspects it for
themselves; opening is a *public* fact (everyone sees the lid go up) but the *contents* stay private to
whoever actually looked. A `KnowledgeLedger`, separate from authoritative `GameState`, tracks who has
learned what fact, how (backstory, direct inspection, opening, or a public event) and at what world
version — so a character's knowledge can become stale without being silently rewritten when the world
changes. Speech is never promoted into this ledger: something another character *said* stays hearsay,
distinguishable in every prompt from something the character verified first-hand. The Dungeon Master is
always handed the acting (or asking) character's exact knowledge view before it answers or adjudicates,
and is expected to rule strictly inside it.

## Inventory transfers and the Rulebook Resolver

A character can `give_item` (to anyone present, ally or foe — consent is not modelled), `drop_item`
(onto the room's ground-loot collection, an always-open container any present character can `take_item`
from afterwards), or attempt `steal_item` (one seeded RNG roll, `Combat:BaseStealChance` default 40%,
**always publicly noticed win or lose**). An equipped weapon can never be given, dropped or stolen. A
thief needs a legitimate reason to know the item exists — having seen it carried, taken, given, dropped,
or been told of it; the engine will not let a character reach for something it has no way of knowing is
there. Every item movement is atomic (it exists in exactly one place before and after) and fully traced,
so an item's whole journey through a run can be reconstructed from the report.

Rather than growing the Dungeon Master's system prompt with every new action's rules, v0.6 introduced a
**Rulebook Resolver**: before each `take_action` is adjudicated, a small deterministic retriever
(`RuleRetriever`) selects a bounded set of relevant **rule cards** (`RulebookMaxCards`, default 5, each
under `RulebookMaxInputChars`), and a separate, **stateless** low-temperature model call
(`Agents:RulebookResolver`) reads only the raw intent plus those cards — never live state, never
history, never hidden knowledge — and returns structured guidance: which action(s) the intent could be,
under which cited rule card(s), or that nothing fits. The Dungeon Master is then handed only the
narrowed tool set the guidance selected (plus `reject_action`), never the full action surface, and
binds that guidance to the actual authoritative state. Guidance can be cached
(`RulebookCacheEnabled`) but a cache key always includes the rule cards' versions, and nothing
encounter-specific (bindings, targets, RNG results) is ever cached. The whole stage can be disabled with
`Harness:EnableRulebookResolver: false` to fall back to the DM adjudicating directly, useful for
scripted tests that don't want to stand up a resolver client. Every consultation — cards retrieved,
cards actually sent, the resolver's raw request/response, validation outcome, cache hit/miss — is
traced, and the report's **Rulebook Consultations** section shows the supported/unsupported split and
confirms request size stays flat across a run rather than growing with the number of rounds played.

## Randomness and seeds

Combat now rolls dice. Each character has a hidden `HitChance` (scenario stat, default 75): an attack
rolls d100 and lands when the roll is at or under the chance, otherwise it misses. A landed hit rolls
again for a glancing blow (`Combat:GlancingBlowChance`, default 25), which deals half damage. A theft
attempt (`steal_item`) rolls once against `Combat:BaseStealChance`. Misses, glancing blows and theft
chances are narrated only in-world, never shown to characters or narrated as a number. All rolls go
through a single `IRng`, so nothing is random except through it.

One master seed governs the whole run — the dice and every agent's model sampling (the Dungeon Master,
each character, and — when enabled — the intent parser, history summariser and Rulebook Resolver):

- **`Harness:Seed` set** → fully deterministic. The same seed reproduces the same run (verified: identical rolls across runs). Use this for testing.
- **`Harness:Seed` blank** → a random master is generated, printed, and recorded. Real runs use this.

Either way the seed is recorded in `run.json` (master, derived game seed, and each agent's derived
seed) and printed at startup, so **any run — including a random one — replays by setting `Harness:Seed`
to the recorded value.** The per-agent seeds are derived from the master, so they are fixed under a
fixed run, random under a random run, and always distinct from one another.

Every RNG draw is traced in full — not just the final hit-or-miss. Each `RngDraw` event records the
action and actor/target that caused it, what it was selecting (hit-or-miss, glancing-or-solid), the
candidate range, the raw roll, the modifier (the hit or glancing chance) applied, the result, the
seed, and the generator's sequence position before and after the draw. That is enough to reconstruct
and compare any draw in isolation, and two runs with the same accepted actions and seed produce
identical engine outcomes and final state.

## Layout

```text
src/ModelsAndMonsters/
    Agents/          conversations, character + DM agents, declaration-only tools
    AI/              model profiles (with per-agent inheritance), provider capabilities, chat client factory
    Configuration/   options and scenario definitions (teams, per-character profiles, exits, rulebook flags)
    Domain/          characters (with team + disposition), weapons, items, injuries, room, exits, game state
    Engine/          game actions, results, combat rules, RNG draw records, team terminal condition, the engine
    Knowledge/       the knowledge ledger — facts, sources, per-character learned records, backstory seeding
    Orchestration/   simulation runner (fixed turn order, team terminal check), turn coordinator, narration log
    Presentation/    console output
    Prompts/         prompt library, state formatting, templates (DM prompt is modular: core + a rules block
                     per job — see "Version history" below for why)
    Randomness/      IRng (with sequence position), seeded rng, master-seed + per-agent derivation
    Rulebook/         rule cards, retrieval, the resolver, guidance validation and caching (v0.6)
    Tracing/         trace sink, event models, tracing chat client, run artefacts, report writer
src/ModelsAndMonsters.Web/   live SSE-driven observer UI (React client under client/)
tests/ModelsAndMonsters.Tests/
docs/prompts/        the original build spec for each release (build_v0_1.md … build_v0_6.md)
reports/             field notes and known-issues write-ups per release — see below
```

## Version history and where to look

Each release started from a spec in `docs/prompts/build_v0_<n>.md` — read the spec for the version
you're extending *before* changing its area, since prompts and validation are often tuned around
exact wording that isn't obvious from the code alone.

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
