# Models & Monsters — v0.1

A small experimental harness for autonomous LLM characters interacting inside a deterministic fantasy
world through an LLM Dungeon Master.

This is a technical proof of concept, not a game. v0.1 exists to prove one architecture:

```text
Character  ──natural-language intent──▶  Dungeon Master  ──structured tool call──▶  Game Engine
```

The characters decide intent. The Dungeon Master interprets it. The engine decides what actually
happens, and is the only source of truth.

## Running it

Requires .NET 10 and, by default, a local [Ollama](https://ollama.com) with `llama3.1` and
`granite4.1:8b` pulled.

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
between Ollama and OpenAI is a configuration change only — no agent or orchestration code moves.

```json
"Agents": {
  "DungeonMaster": { "Provider": "Ollama", "ModelId": "llama3.1:latest", "Temperature": 0.4 },
  "Hero":          { "Provider": "Ollama", "ModelId": "llama3.1:latest", "Temperature": 0.8, "TopK": 40 },
  "Monster":       { "Provider": "OpenAI", "ModelId": "gpt-4.1-mini",    "Temperature": 0.9 }
}
```

Sampling options a provider cannot honour (for example `TopK` on OpenAI) are dropped rather than
silently sent, and the drop is recorded in the trace.

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

Set `Thinking: false` for any agent on a reasoning model (qwen3, qwen3.5, deepseek-r1, gemma3/4,
phi4-reasoning, …). On Ollama this sends the `think` flag; it is ignored by models that do not reason.

This is not cosmetic. Left thinking, a reasoning model spends its **entire** output budget on a private
reasoning block and emits no visible prose or tool call within a normal token limit — the narration
comes back empty and the Dungeon Master appears to "say nothing". Measured on `qwen3.5:9b`: with
thinking on and a 700-token budget the reply was 700 tokens of reasoning and zero prose; with
`Thinking: false` the same narration was clean prose in ~100 tokens. If you genuinely want a reasoning
agent, set `Thinking: true` and raise `MaxOutputTokens` well above the reasoning length (2500+).

The harness flags this: a reply that is all reasoning is recorded as a truncation with
`ReasoningOnly: true`, and the run ends with a warning naming the fix.

An OpenAI key is read from the `OPENAI_API_KEY` environment variable, or from user secrets:

```bash
dotnet user-secrets --project src/ModelsAndMonsters set ModelsAndMonsters:Providers:OpenAI:ApiKey <key>
```

Keys are never written to configuration files or to run output.

The scenario lives in `src/ModelsAndMonsters/scenario.json`, and prompts are plain markdown in
`src/ModelsAndMonsters/Prompts/Templates/`. Each prompt is content-hashed into `run.json` so a run
can be tied to the exact prompt text that produced it.

## Output

The console shows only the readable game. Everything else goes to `runs/<run-id>/`:

| File | Contents |
| --- | --- |
| `run.json` | Run metadata, scenario, initial state, agent model profiles, prompt hashes, harness limits |
| `trace.jsonl` | Append-only event stream: every model request and response, every tool dispatch and result, every adjudication, every engine action with before/after state, every narration and who received it |
| `final-state.json` | Authoritative world state when the run stopped |
| `report.md` | Readable rendering of all three: run details, a plain transcript, every trace row, and the final state |

`report.md` is written automatically at the end of every run, including a failed one. It is generated
purely from the other three files, so any past run can be re-rendered:

```bash
dotnet run --project src/ModelsAndMonsters -- --report runs/<run-id>
```

## Characters

A character sees only `ask_dm`, `take_action` and `end_turn`, and never learns that a game engine
exists. A turn ends when something the character attempts takes effect, or when the character chooses
to end it. If nothing takes effect for `MaxConsecutiveIdleRounds` rounds, the encounter is stopped as
a stalemate rather than grinding on to the round limit.

## Layout

```text
src/ModelsAndMonsters/
    Agents/          conversations, character + DM agents, declaration-only tools
    AI/              model profiles, provider capabilities, chat client factory
    Configuration/   options and scenario definitions
    Domain/          characters, weapons, items, injuries, room, game state
    Engine/          game actions, results, the deterministic engine
    Orchestration/   simulation runner, turn coordinator, narration log
    Presentation/    console output
    Prompts/         prompt library, state formatting, templates
    Tracing/         trace sink, event models, tracing chat client, run artefacts
tests/ModelsAndMonsters.Tests/
```
