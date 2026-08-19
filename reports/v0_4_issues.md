# v0.4 Session — Issues Found and Fixed

Problem → fix pairs actually resolved during this session. Anything left unresolved, or
that was a leftover from an earlier session's work, is deliberately excluded.

---

## 1. A single transient Ollama 500 killed an hour-long run

**Problem:** A run died mid-way with `HttpRequestException: 500`. The Ollama server log
showed the model at 5.4k/8k tokens (not context overflow) — qwen3.5 had emitted a
malformed tool-call (`<function>` closed by `</parameter>`), Ollama's own server-side
tool-call parser choked on it, and returned a hard 500 instead of degrading. There was no
retry around the character/DM model call, so one bad sample ended the whole run.

**Fix:** `ModelAgent.CallModelAsync` now retries transient failures — up to 3 attempts,
short backoff, retrying `HttpRequestException` when `StatusCode is null or >= 500`, never
on cancellation, never on a 4xx. A fresh send almost always re-samples cleanly.
[`ModelAgent.cs`](../src/ModelsAndMonsters/Agents/ModelAgent.cs)

---

## 2. Opening a container was modelled as entirely private — the DM lied about it being shut

**Problem:** A live run showed the DM telling a character "the lid is shut" about a case
another character had *visibly* thrown open moments earlier. Root cause: opening a
container only recorded knowledge for the opener; nobody else's info-view knew the case
was open at all, so the DM fabricated "shut" to explain why the asker didn't know the
contents.

**Fix:** Split the fact in two. Opening now mints a **public** `ContainerOpened` fact,
delivered to every living character (mirroring the existing item-removal fact); the
*contents* stay private to whoever actually looked inside. The answer prompt was updated
to treat open/closed as a plainly-visible fact never to be denied, and to say "open but you
can't see inside from here" instead of inventing "shut."
[`KnowledgeLedger.cs`](../src/ModelsAndMonsters/Knowledge/KnowledgeLedger.cs),
[`TurnCoordinator.cs`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs),
[`dungeon-master.answer.md`](../src/ModelsAndMonsters/Prompts/Templates/dungeon-master.answer.md)

---

## 3. A goblin who knew what a case held from backstory was refused for "never looking inside"

**Problem:** Vark knew (from backstory) that the shrine case held a healing potion. Once
the case was open, he tried to take it — and the DM refused him twice, once with leaked
rule-language ("you must take one item at a time... a separate action from opening or
securing"), and once flatly contradicting the knowledge view it had just been handed
("you have never looked inside").

**Fix:** The adjudicate prompt now states plainly that a take is legitimate when the
container is open and the item is in what the character directly knows — backstory
foreknowledge included — and to read "grab my draught from the open case" charitably as a
take rather than nitpicking container-vs-item.
[`dungeon-master.adjudicate.md`](../src/ModelsAndMonsters/Prompts/Templates/dungeon-master.adjudicate.md)

---

## 4. Machinery language leaked through the take refusals above

**Problem:** The two refusals in #3 also leaked harness rule-text ("one item at a time,"
"a separate action from opening or securing," "you can only act on things you know are
there") straight into in-world dialogue.

**Fix:** Extended the existing machinery-language detector with those specific phrases so
they get caught and rephrased before reaching a character.
[`MachineryLanguage.cs`](../src/ModelsAndMonsters/Agents/MachineryLanguage.cs)

---

## 5. Speech-retry nudges were never pruned, and quietly filled the context window

**Problem:** Diagnosing repeated "output-token limit" and "silently discarded oldest
messages" warnings on a long run led to the real cause: every failed prose reply plus its
nudge stayed in a character's history forever. One character burned 10 model calls on a
single turn (a 3× overhead across the run), each failure leaving ~2 messages permanently
behind — this was the dominant filler of the 8,192 context window, not raw conversation
length.

**Fix:** `AgentConversation.CompactTurn` runs once a turn resolves: keeps the clean tool
calls and their results, drops the failed prose replies and nudges. Tool-call/result
pairing is preserved; the full exchange (including discarded attempts) still lives in the
trace. [`AgentConversation.cs`](../src/ModelsAndMonsters/Agents/AgentConversation.cs)

---

## 6. Characters fusing speech + action in one prose reply looped on the old nudge path

**Problem:** Small models naturally write "I step forward. 'Elara, stay close,' I say" —
narration, speech, and action fused in one reply. The nudge-for-say path can only recover
one call at a time, so a fused reply nudged, got fused again, nudged again — one character
hit the speech nudge 13 times in a single run.

**Fix:** Built `IntentParser` — a stateless, zero-temperature agent that reads a prose
reply into the `say`/`ask_dm`/`take_action` calls it implies, dispatching several from one
reply (say **and** act, in one pass) instead of nudging for one at a time. Wired as a
prose-only fallback behind `UseIntentParser` (on by default); when it finds nothing
callable, falls back to treating the whole reply as one `take_action` so the turn still
progresses. Measured: calls-per-turn dropped from 3.00 to 2.12 on a comparable run.
[`IntentParser.cs`](../src/ModelsAndMonsters/Agents/IntentParser.cs)

---

## 7. Long runs still truncated even after the cruft was pruned

**Problem:** With the proxy and prune both in, an 11-round run still hit 21 context-bound
truncations (input+output ≈ 8192 exactly, output as low as 13 tokens) and 14 silent-drop
warnings — all in rounds 8–11. This was *legitimate* history (real turns, ~8,900 tokens
across 43+ messages per character) that neither the prune nor the proxy could touch.

**Fix:** Built `HistorySummariser` — a stateless, low-temperature agent that folds a
character's older turns into a short first-person recap once its estimated history passes
a token budget (default 5,500), keeping the last 2 turns verbatim. The trim boundary always
falls on a turn's opening message, so tool-call/result pairing survives, and a later trim
folds the previous recap in rather than summarising a summary. Measured on the next
comparable run: **0 truncations, 0 saturations** (down from 21 / 14), with 5 summarisations
firing across 9 rounds. [`HistorySummariser.cs`](../src/ModelsAndMonsters/Agents/HistorySummariser.cs)

---

## 8. The first summariser prompt let allegiances drift in the recap itself

**Problem:** After #7 shipped, a run showed a hero addressing an enemy goblin as an ally
("Skrit, did you take that potion? If not, grab it before Vark reaches us both!"). Traced
to the summariser flattening "enemy Skrit strikes you" beats into a neutral "Skrit is
unharmed" — once that reinforcement was compressed away, the model drifted.

**Fix:** The summariser's prompt now explicitly requires carrying each person's side
through the recap, naming them plainly as companion or enemy — "even for someone now
holding something you want" (the exact trap that triggered this).
[`character.summarise-history.system.md`](../src/ModelsAndMonsters/Prompts/Templates/character.summarise-history.system.md)

---

## 9. Characters (mainly on weaker local models) drifted into treating enemies as allies from turn one

**Problem:** Independent of summarisation, dialogue across several runs showed characters
offering enemies comfort and camaraderie — a goblin warning heroes about slippery floors, a
hero reassuring an enemy goblin "I've got your back." Root cause: the character system
prompt named allies by name but deliberately left enemies unnamed, on the assumption a
capable model would infer "everyone else is hostile." Weaker models didn't reliably make
that inference.

**Fix:** `FormatAllies` now names both sides explicitly — allies with the existing
"never raise a weapon against them," and enemies with "they are not your friends, whatever
they may say; offer them no aid, comfort or reassurance." Nothing hidden is revealed by
this (who opposes whom is visible from the first moment; only container contents etc. stay
secret). [`CharacterPromptFactory.cs`](../src/ModelsAndMonsters/Prompts/CharacterPromptFactory.cs)

*(A local 3B model still broke through this fix entirely — judged a model-capability floor,
not a prompt gap, and left as an observation rather than a further prompt change.)*

---

## 10. Wiring the new proxy/summariser agents broke the scripted full-run tests

**Problem:** `SimulationRunner` now always constructs an `IntentParser` and, when enabled,
a `HistorySummariser` client. The two scripted end-to-end tests
(`MultiActorRunTests`) failed with `InvalidOperationException: No scripted client was
provided for agent 'IntentParser'` — the test's scripted client factory had no script for
either new agent.

**Fix:** Those two tests set `UseIntentParser = false` and `SummariseHistory = false` in
their `HarnessOptions` — they exercise the deterministic core in fixed turn order with
scripted, always-clean tool calls, so neither fallback agent is exercised there anyway.
[`MultiActorRunTests.cs`](../tests/ModelsAndMonsters.Tests/MultiActorRunTests.cs)

---

## 11. The web UI host kept running against a stale core build

**Problem:** After the knowledge-model fixes (items #2–#4) landed in the core project, the
already-running `ModelsAndMonsters.Web` process still had the old assemblies loaded — a
running .NET process doesn't pick up rebuilt dependencies — so the UI would have kept
exhibiting the fixed bugs.

**Fix:** Killed the stale host process tree and relaunched `dotnet run` against the fresh
Release build; confirmed healthy via `/api/health` before handing back for UI testing.
