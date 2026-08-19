# Models & Monsters — v0.1 Issues Log

Problems **found and fixed during the v0.1 build session**. Each entry is a problem encountered and how
it was resolved, with the evidence that confirmed the fix. Items left unresolved at the end of the
session (e.g. the smaller/reasoning models occasionally reciting the mechanics inside a refusal, and
qwen3.5's repetitive phrasing) are deliberately excluded.

---

## Build & tests

### 1. Tool schemas declared literal placeholder property names
- **Problem:** the declaration-only tool schemas were built with a `$$"""…"""` raw string, but the
  parameter tokens were triple-braced (`{{{QuestionParameter}}}`), so interpolation never fired and
  every tool declared a JSON property literally named `{question}` / `{intent}`. Live models papered
  over it via a single-argument fallback, hiding the bug.
- **Resolution:** switched the tokens to double braces so interpolation runs; the schemas now declare
  real `question` / `intent` properties. Added a schema-assertion test so it can't silently regress.

### 2. First test run failed on framework-value assumptions
- **Problem:** several tests assumed exact values the framework produces differently — the finish
  reason compared against the string `"ToolCalls"`, a `NewlyInjected` message count, a collection
  equality on dropped options, and an unarmed-hero built with `weapon: null!`.
- **Resolution:** asserted against the real values (`ChatFinishReason.ToolCalls.Value`, the actual
  first-call injection count, `Assert.Single`, an explicit `UnarmedHero()` helper). Suite went green.

---

## Dungeon Master adjudication & perception

### 3. The DM refused ordinary weapon strikes
- **Problem:** an intent like "I swing my axe wildly at Aric" or a strike that began with a movement
  word was refused as `unsupported`, even though a plain weapon blow is exactly what the engine can do.
- **Resolution:** rewrote the adjudication section of the DM prompt around "a strike is a strike" — a
  weapon reaching a character is `attack_character` regardless of flourish or a leading movement verb.
  A cold-context probe went from mixed to **12/12** correct on the strike cases.

### 4. The DM invented distances in a world that has none
- **Problem:** asked how close the goblin was, the DM answered "about 10 feet away," inventing range,
  position and movement the engine does not model.
- **Resolution:** added a "shape of this world" block to the DM prompt (no distance/position/movement;
  everyone is already within reach) and reinforced it in the authoritative-state notes. The invented
  measurements stopped.

### 5. The DM told characters the mechanical action list
- **Problem:** when a character asked "what can I do?", the DM answered by naming the two supported
  actions — breaking the fiction by handing a player the API surface.
- **Resolution:** added an answering rule: never tell a character what the world can or cannot resolve;
  describe what they perceive and leave the choice to them.

### 6. Adjudication misclassified actions as the DM's history grew
- **Problem:** mid-run the DM began refusing clear strikes it had accepted earlier. Replaying the exact
  failing request from the trace showed the cause was **not** drift or randomness: the same adjudication
  scored **4/4 correct on a clean context and 0/4 with the DM's own narration history attached** —
  narrating puts the model in "prose mode," which classifies badly.
- **Resolution:** adjudication now runs on an isolated projection (system prompt + the one task), fresh
  per attempt. A configurable flag preserves the old shared-context behaviour for comparison.

---

## Turn flow & identity

### 7. Characters had no way to stop, so they ground on
- **Problem:** a mortally-wounded goblin repeatedly tried to give up ("I accept my fate and remain
  still…") and the harness treated each attempt as a failed action, looping until the attempt limit.
- **Resolution:** added the `end_turn` tool so a character can deliberately do nothing; it is a valid
  completed turn with no state change, narrated as holding back.

### 8. Two characters who could do nothing ran to the round cap
- **Problem:** when neither character could resolve anything, the encounter ground pointlessly to
  `MaxRounds`.
- **Resolution:** added stalemate detection — after `MaxConsecutiveIdleRounds` with nothing taking
  effect, the run ends with a stalemate terminal condition. Verified live (ended at 2 idle rounds).

### 9. A character attacked itself
- **Problem:** an identity-confused goblin described itself by its opponent's name *and* weapon; the DM
  translated it faithfully and the engine received `attack_character(attacker: Grik, target: Grik,
  weapon: Iron Sword)`. The engine rejected it (`TargetIsSelf`), but nothing upstream prevented it.
- **Resolution:** the harness now forces the acting character (whose turn it is) as the attacker,
  structurally — any correction is recorded as an `AdjudicationCorrected` trace event. Also re-anchored
  the character's own identity in the turn prompt.

---

## Model-output handling

### 10. `FinishReason = Length` was logged but never acted on
- **Problem:** a reply truncated at the output-token limit loses its tool call, which then looked
  identical to "the model ignored its protocol" and was logged as such — blaming the model instead of
  the token budget.
- **Resolution:** truncation is now detected at the chat-client boundary and distinguished from a
  protocol failure, with an accurate trace message, a console notice, and a distinct nudge. A reply that
  is *entirely* a reasoning block is flagged `ReasoningOnly`. Verified live by forcing a tiny budget.

### 11. A reasoning model produced empty narrations
- **Problem:** pointing all agents at `qwen3.5:9b` gave "(The Dungeon Master said nothing.)" — the
  model spent its whole 700-token output budget on a `<think>` block and emitted no prose; the reasoning
  was correctly stripped, leaving nothing.
- **Resolution:** added a per-agent thinking toggle (Ollama's `think` flag) and set it off for the
  harness. Verified: the same narration went from 700 tokens of reasoning / empty to **clean prose in
  ~100 tokens**. A run that hits this now ends with a warning naming the fix.

---

## Context window & memory

### 12. Ollama silently dropped conversation history
- **Problem:** once a request exceeded the context window, Ollama discarded the oldest messages and
  reported only the post-truncation size, with no signal — the application believed it still owned the
  whole conversation. The DM was losing roughly a quarter of its context on long runs.
- **Resolution:** added detection that compares an estimate of what was sent against the input size the
  response reports (the sent-vs-received method), independent of any configured window, raising a trace
  event and an end-of-run warning. It catches the observed `num_ctx/2` half-window truncation that a
  window-based check missed entirely (1026 processed against a 2048 window).

### 13. The DM's context grew without bound
- **Problem:** the DM re-embedded a full authoritative-state block in every task on one ever-growing
  conversation, so its input climbed past 8k and started getting silently truncated within a few rounds.
- **Resolution:** the DM now runs each task on a purpose-specific projection (system prompt + fresh
  state + only the relevant context); the full record still lives in the trace. DM input went from
  **climbing past ~8,155 and truncating to flat ~1.7k–2.4k tokens across a whole run**.

### 14. Exact numbers leaked into narration
- **Problem:** a regression from the projection change — with each narration now a fresh projection
  holding the numeric state block and no prior prose to imitate, a low-temperature model dumped numbers
  ("his health remains at 10").
- **Resolution:** the DM is now given each character's condition as a descriptive band ("badly wounded")
  instead of raw health, so it is structurally unable to leak a number it was never given. Verified: no
  numeric leaks in narration afterward.

### 15. Raw tool-call JSON reached a character
- **Problem:** when the DM emitted a `reject_action` as text instead of a tool call, the raw JSON was
  passed to the character as the refusal reason.
- **Resolution:** added a guard that recovers the intended `reason` from tool-call-shaped text and never
  forwards raw JSON; the original text is still kept verbatim in the trace for diagnosis.

---

## Narration quality

### 16. Narration re-established the whole scene every beat
- **Problem:** the DM restated the room and standoff on every narration (the prompt literally told it to
  "cover who is present, the weapon each holds…"), producing long, repetitive prose.
- **Resolution:** changed the narration rules to "describe only what has changed" (opening establishes;
  updates report only the new development) plus a flag-driven mid-encounter note. Average update
  narration **halved, ~392 → ~177 characters**.

### 17. Round-end / encounter-end narrations duplicated the prior line
- **Problem:** the round-end and encounter-end "situation recap" narrations fired immediately after the
  last per-action narration and, with "describe only what changed," simply restated it — sometimes
  verbatim.
- **Resolution:** removed both recap narrations; per-action outcomes plus the console's round headers and
  ending summary carry the structure. Duplicate adjacent narrations dropped to zero.

### 18. The DM narrated the wrong (previous) action
- **Problem:** the continuity hint quoted the DM's previous narration prominently and said "don't restate
  the above." The model echoed that quote and ignored the new event underneath — narrating the previous
  action, and producing identical back-to-back narrations.
- **Resolution:** removed the quoted-narration hint; a stateless flag now signals "mid-encounter update"
  without giving the model any prior text to echo. The specific new event lives in the task itself.

### 19. The DM credited the wrong actor for a blow
- **Problem:** the engine resolved "Grik hit Aric," the result text said so plainly, yet qwen3.5
  narrated "Aric's Iron Sword strikes Grik" — a strong "the hero does the hitting" prior inverting the
  actor.
- **Resolution:** the outcome-narration task now leads with, and repeats, an explicit actor directive
  naming the character whose turn it is (always the one who acted). Verified by replaying the exact
  buggy seed: **every actor then narrated correctly**, zero inversions across the fight. Locked in with
  a test.

---

## Report generation

### 20. `report.md` was enormous
- **Problem:** the generated report re-embedded every model request's full (growing) message history,
  making it grow with the square of the run length — a four-round run produced ~11.6 MB.
- **Resolution:** capped the raw JSON reproduced per event with a pointer to the exact `trace.jsonl`
  line for the full record. Report dropped to ~0.5 MB while keeping every row.

### 21. The report transcript printed events out of order
- **Problem:** the readable transcript showed the engine result before the character's stated intent,
  because it transcribed the intent from the adjudication event rather than where it was spoken.
- **Resolution:** the intent is now transcribed from the character's dispatch, so the transcript reads
  in the order it happened: character acts → engine resolves → DM narrates. Covered by a test.

---

## Model selection

### 22. Some tool-capable models stop calling tools after a tool result
- **Problem:** several models advertised as tool-capable (e.g. `qwen2.5:7b`, `hermes3:8b`,
  `mistral-nemo:12b`) would make the first tool call but then, after a tool result was appended, drop to
  plain prose — silently burning a character's turn on nothing.
- **Resolution:** probed the local models directly and confirmed which survive a tool result
  (`llama3.1`, `granite4.1`); configured those and documented the finding so model choice for this
  harness is evidence-based rather than assumed.
