# v0.10 issues — found and fixed

Problems discovered **and** resolved this session, across the v0.10 closure batch, the new Encounter
Summariser feature, and the final grounded-story/report polish. Anything still open, and anything already
understood from an earlier release, is deliberately not here — see `reports/v0_9_issues.md` and earlier for
those. Where a live run is named it is under `src/ModelsAndMonsters.Web/runs/`.

Each entry is what went wrong, why, and what changed.

---

## Authoritative state and narration

### 1. The Dungeon Master invented a teammate as an enemy

**Symptom.** `20260821-153857Z-05f43dcc`: Vark attempted `steady_ally` on his own teammate Skrit. The
Dungeon Master rejected it, narrating a fabricated reason — that Skrit was an enemy goblin, not an ally —
despite the two sharing a team the whole encounter.

**Cause.** `Character.Team` never appeared anywhere in the rendered authoritative state the Dungeon Master
reads. Ally/enemy status was never stated as a fact; the model had to *infer* it from role, species, or
which side of the fight a character looked to be on, and here it inferred wrong and then narrated the wrong
inference as if it were true.

**Fix.** `WorldStateFormatter` now names every character's team on its own line (`{Name} (id, role, team:
{Team}) - {disposition}`), plus a dedicated team-roster summary line (`TEAMS (same team = allies, different
team = opponents): Heroes: Rowan, Elara | Goblins: Vark, Skrit`) and an explicit "how to read" bullet stating
that team, and only team, decides allies. Three new tests in `WorldStateFormatterV10Tests.cs` pin this down.

---

## The Encounter Summariser's sampling recipe

### 2. A local-model output cap truncated a hosted model with room to spare

**Symptom.** `20260821-160342Z-00f83b0e` (Anthropic): the story cut off mid-sentence. Trace showed
`FinishReason: length`, `OutputTokenCount: 900 == MaxOutputTokensRequested: 900`, against only 2,367 of a
vastly larger input window used — no window pressure at all.

**Cause.** `Harness:EncounterStoryOutputTokens` (900) was tuned for Ollama's shared, 8k-class context
window, then applied identically to every provider, including hosted ones whose window this call never
approaches. Exactly the "a local-model accommodation became a global rule" mistake this project's README
already names three other times.

**Fix.** Removed the local/hosted distinction entirely rather than tuning it more precisely — one figure,
raised to 3000, applied to every provider. Sized to sit safely under an 8192 Ollama window with margin, while
being far more than a short story ever needs on any provider. `Every_provider_gets_the_same_generous_output_budget_regardless_of_window`
proves both providers now get the same cap.

### 3. Widening `RepeatLastN` to fix one degenerate loop caused two different, worse ones

**Symptom — the original bug.** `20260821-164214Z-e7cf7d7a`: the model opened a single run-on sentence
roughly a thousand tokens long inside "The Encounter," then — once it scrolled outside Ollama's default
64-token repeat-penalty lookback — regenerated it verbatim, over and over, until it hit the output cap.
`RepeatPenalty` was active the whole time; it simply couldn't see far enough back to notice.

**Fix attempt 1 — `RepeatLastN: -1`.** llama.cpp's own "span the whole context" sentinel. Rejected outright
by a live Ollama server: `20260821-171039Z-7e4ea447` failed to generate any story at all with an HTTP 400,
`Field 'repeat_last_n': Value must be between 0 <= value <= 2147483647, but got -1`.

**Fix attempt 2 — `RepeatLastN: int.MaxValue`.** A concrete stand-in for "whole context" that the server would
actually accept. Broke the very next story anyway: `20260821-173106Z-222b9865` showed the lookback counts
back through the **whole token stream, prompt included**, not just the model's own output — against this
call's own ~4000-token input, spanning the whole context penalised reusing anything already there (periods,
common words, character names). The model spent its entire output budget on one unpunctuated cascade of
ever-more-exotic vocabulary that never once closed a sentence.

**Fix attempt 3 — `RepeatLastN: 2000`.** Sized to comfortably outreach the original ~1000-token repeat
without reaching deep into the input. Still broke, differently: `20260821-175615Z-8cee99e9` opened its
Backstory section with an unpunctuated word-salad cascade that drifted into Chinese mid-sentence, then into
symbols and Roman-numeral-style abbreviations, before oddly recovering to write a perfectly ordinary Setting
paragraph straight after.

**Diagnosis.** Three different sizes, three different flavours of breakage — the instability was never
really about picking the right window size. `RepeatPenalty`, `PresencePenalty` (0.4) and `FrequencyPenalty`
(0.3) were already three separate mechanisms pushing the model away from repetition, at `Temperature: 1.0`
on a heavily quantized 9B model, for a 3000-token generation: plenty of pressure for "safe" vocabulary to run
out before the reply did, and widening `RepeatLastN` just gave the combination more territory to exhaust it
over.

**Final fix.** `RepeatLastN` left unset — Ollama's own 64-token default, proven merely insufficient for the
original loop, never proven harmful the way either enlarged value was. (Superseded a few hours later by the
"Final Polish" task's regrounding of the summariser's whole input and a lower-creativity sampling recipe —
see below — which is what actually addresses the class of failure rather than one symptom of it.)

### 4. A degenerate story broke the encounter into gibberish before recovering, unprompted, mid-run

Folded into #3 above as the evidence for fix attempt 3 — noted separately here because it is a distinct,
striking failure mode in its own right: a model that had drifted into Chinese and then symbol soup, with no
external correction, chose on its own to write a completely ordinary English paragraph for the very next
section. Nothing in the harness detected or intervened; the recovery was the model's alone.

---

## Web UI

### 5. The story event reached the browser but never reached the page

**Symptom.** The first live run with `EncounterSummariser` enabled wrote `story.md` correctly, but nothing
appeared in the running web UI — no error, just silence.

**Cause.** `App.jsx`'s event handler gated every incoming SSE event through a `LOG_KINDS` allowlist `Set`
before adding it to the transcript log. `'story'` was never added to that set, so `if (LOG_KINDS.has(evt.type))
setLog(...)` silently dropped the event even though the server had sent it correctly.

**Fix.** `'story'` is handled explicitly *before* the `LOG_KINDS` check (bypassing it entirely) rather than
added to the allowlist: it stores the story text in its own `story` state slot and pushes a synthetic
`{type: 'storyReady'}` marker to the log instead of the raw text. A "Generating the encounter's story..."
console line was also added on the harness side so a run that has already printed its ending and then goes
quiet for a local model's slower sampling call reads as still working, not hung. A `StoryPopover` component
(backdrop-click and Escape both close it) renders the finished story, with a dependency-free Markdown-to-React
parser — deliberately not `dangerouslySetInnerHTML`, since the story is unmoderated LLM output.

---

## Character prompt gaps

### 6. An unlimited-use ability had no counter-pressure against being spammed

**Symptom.** Across a live run, Rowan spent 7 of 11 successful turns on `guard-ally`, including guarding an
ally at full health while he himself sat at 2 health, visibly scared, with an unused healing flask in his own
inventory.

**Cause.** Two things compounding: Rowan's persona (`scenario.json`) names guarding as his one defining trait
and lists exactly one fear ("failing to protect a companion") with nothing about his own survival; and
`guard-ally` has `MaxUsesPerEncounter: null` (unlimited), same as bracing (`Defend`) — but bracing already
carries an explicit anti-spam line in the shared `character.system.md` ("if you find yourself bracing turn
after turn, that is your sign the waiting has failed"), and guarding had no equivalent.

**Fix.** Added a general paragraph to `character.system.md` (not a scenario-specific edit to Rowan) stating
that protecting a companion who is not in any real danger protects nobody, and that a guardian's own wounds
do not close because the turn was spent on someone else. Chosen over touching Rowan's own persona because it
fixes the actual gap in the shared contract — any character with a protect/aid-type ability gets the same
counter-pressure — rather than patching one NPC. `CharacterPromptContractTests.cs` pins the new guidance down.

---

## The grounded story rebuild (Final Polish)

### 7. A room's environmental-object table mislabelled cover as an empty, closed container

**Symptom.** The final-state report's container table included the Overturned Mill Workbench, shown as
permanently `closed` with no contents.

**Cause.** A room's `Objects` array (v0.9) holds real containers and cover side by side. `WriteFinalContainers`
iterated that whole array unfiltered, so a cover object — which has neither an open/closed state nor
contents — got the container table's defaults for both, producing exactly the misleading row. The correct
`Objective container state` section elsewhere in the same report already filtered correctly (on the presence
of an `IsOpen` field); `WriteFinalContainers` alone had never been given the same filter.

**Fix.** `WriteFinalContainers` now reuses the existing `Containers()` filter. Two new regression tests: one
proving a mixed container+cover room renders cover only under `## Environmental objects` with its real
Intact/Damaged/Destroyed state, and one proving a room with only plain containers (the pre-v0.9 shape, no
type discriminator at all) still renders exactly as before.

### 8. Reading the run's own trace file while its writer was still open threw a sharing violation

**Symptom.** Building the new `EncounterStoryBrief` from `trace.jsonl` — read back from disk immediately
after the encounter ends, before the run's own trace sink is disposed — failed every time with
`IOException: The process cannot access the file '...\trace.jsonl' because it is being used by another
process.`

**Cause.** `JsonlTraceSink` opens its write handle with `FileShare.Read` (by design, so a crashed run still
leaves a readable partial trace) and is not disposed until the whole run finishes — including the story
generation step, which now runs before that disposal. `File.ReadLines`'s default share mode was not
compatible with that still-open writer handle.

**Fix.** `EncounterStoryBriefBuilder.ReadEvents` opens its own read handle explicitly with
`FileShare.ReadWrite`, which tolerates the concurrently open writer. Verified by the full
`EncounterStoryRunTests.cs` suite, which exercises this exact timing through a real `SimulationRunner` run.
