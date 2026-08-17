# Models & Monsters — v0.2

A small experimental harness for autonomous LLM characters interacting inside a deterministic fantasy
world through an LLM Dungeon Master.

This is a technical proof of concept, not a game. It proves one architecture:

```text
Character  ──natural-language intent──▶  Dungeon Master  ──structured tool call──▶  Game Engine
```

The characters decide intent. The Dungeon Master interprets it. The engine decides what actually
happens, and is the only source of truth.

**v0.2** extends the original one-versus-one encounter to a **2v2 multi-actor encounter** — a Fighter
and a Cleric against a Goblin Captain and a Goblin Grunt — to validate multi-actor orchestration:
per-character agents with isolated histories, teams and a team terminal condition, a fixed turn order
with dead-actor skips, unambiguous targeting by stable character id, public-versus-private information
delivery, per-character model profiles, and completely traced, reproducible RNG.

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

Every one of the five agents — the Dungeon Master and the four characters — resolves its own profile.
`Default` holds the common baseline; the Dungeon Master and each character (keyed by character id)
override only the fields they set. Nothing hard-codes a single shared hero or monster profile, and the
resolved profile actually used for each agent's calls is recorded in `run.json`.

```json
"Agents": {
  "Default":       { "Provider": "Ollama", "ModelId": "qwen3.5:9b", "Temperature": 0.8, "TopK": 40, "ContextWindow": 16384, "Effort": "none" },
  "DungeonMaster": { "Temperature": 0.2, "MaxOutputTokens": 700 },
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

Neither helps a model that calls tools but only *postures* (describes stances instead of striking) — the
Dungeon Master correctly rejects non-actions as unsupported, so that is a roleplay-decisiveness issue,
not a tool-calling one.

### Context window and silent truncation

Set `ContextWindow` per agent. On Ollama it is sent as `num_ctx`.

When a request exceeds the window, Ollama drops the oldest messages and reports only the
post-truncation size in `prompt_eval_count` — nothing in the response says it happened. The
application believes it owns the whole conversation while the model is shown less than was sent. Some
builds truncate at half the nominal window, so trusting the configured number is not enough.

The harness detects this by comparing an estimate of what it sent against the input size the response
reports (the method from ["What the model saw"](https://spencerclark.dev/blog/what-the-model-saw/)):
when the reported size falls well below what was sent, it raises `ContextWindowSaturated` in the trace
and warns at the end of the run. This needs no configured window and catches half-window truncation.

The Dungeon Master used to reach this first, because it re-embeds a full state block every call. It no
longer does — see below.

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
  models reject sampling once reasoning is on — exactly like Anthropic thinking, below).
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

## Characters, teams and turns

A character sees only `ask_dm`, `take_action` and `end_turn`, and never learns that a game engine
exists. A turn ends when something the character attempts takes effect, or when the character chooses
to end it. If nothing takes effect for `MaxConsecutiveIdleRounds` rounds, the encounter is stopped as
a stalemate rather than grinding on to the round limit.

Each of the four characters is a separate agent with its own definition, model profile, conversation
history, self-state and derived seed. Nothing is shared between them: one hero's private question to
the Dungeon Master never enters the other hero's history. Everything a character learns about anyone
else arrives as **public narration** — the opening scene, an accepted action's outcome, a death, a
pass — deliberately delivered to every living character that could perceive it and recorded, with its
recipients, in the trace.

Turns run in a **fixed order**, repeated each round. Before a turn the actor is checked for life; a
dead actor is skipped without a model call, and the skip is traced. The encounter ends the moment one
**team** has no living members (all goblins dead → heroes win; all heroes dead → goblins win), checked
after every accepted action. A voluntary `end_turn` never ends the encounter by itself.

When a character attacks, the Dungeon Master translates its words into one specific, living target by
name, and the engine records the resolution against a **stable character id**. An ambiguous, absent or
dead target is rejected — never silently swapped for someone else. The engine imposes no friendly-fire
rule: it resolves whatever the DM actually translated, while the prompts and each character's goals
discourage striking an ally.

## Randomness and seeds

Combat now rolls dice. Each character has a hidden `HitChance` (scenario stat, default 75): an attack
rolls d100 and lands when the roll is at or under the chance, otherwise it misses. A landed hit rolls
again for a glancing blow (`Combat:GlancingBlowChance`, default 25), which deals half damage. Misses
and glancing blows are narrated as such; hit chance is never shown to characters or narrated as a
number. All rolls go through a single `IRng`, so nothing is random except through it.

One master seed governs the whole run — the dice and all three agents' model sampling:

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
    Configuration/   options and scenario definitions (teams, per-character profiles)
    Domain/          characters (with teams), weapons, items, injuries, room, game state
    Engine/          game actions, results, combat rules, RNG draw records, team terminal condition, the engine
    Orchestration/   simulation runner (fixed turn order, team terminal check), turn coordinator, narration log
    Presentation/    console output
    Prompts/         prompt library, state formatting, templates
    Randomness/      IRng (with sequence position), seeded rng, master-seed + per-agent derivation
    Tracing/         trace sink, event models, tracing chat client, run artefacts, report writer
tests/ModelsAndMonsters.Tests/
```
