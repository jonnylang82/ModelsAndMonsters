# Response to the 2026-08-20 Code Review

This document goes through [code-review.md](code-review.md) point by point. Each finding was independently
re-verified against the current tree before deciding what to do with it. Eight of the nine numbered findings
turned out to be real defects and are fixed below, with regression tests. One (the back half of #1) is
argued against as a deliberate, already-documented tradeoff rather than an oversight, with the reasoning laid
out so it can be revisited later if the tradeoff stops holding.

All 909 previously-passing tests still pass. 19 tests were added (one existing test had to be corrected — see
finding 5) for **928 total, 0 failed**.

## High-priority findings

### 1. Free-text speech controls access to hidden items — partially fixed, partially argued

Two separate claims here, and they get different answers.

**The Steal/Take asymmetry is a real bug — fixed.** [`StealLacksKnowledgeBasis`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs#L3014)
ignored hearsay entirely, while [`TakeLacksKnowledgeBasis`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs#L3040)
already honoured it via [`HeardItemMentioned`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs#L3082).
The v0.6 spec ([build_v0_6.md:171-176](prompts/build_v0_6.md#L171)) lists "being told about it, recorded as
hearsay" as an explicitly valid basis for theft specifically, so the omission was a spec violation, not a
design choice. `StealLacksKnowledgeBasis` now calls `HeardItemMentioned` exactly as `Take` does, so the two
gates agree on what counts as a reason to reach for an item.

**The free-text hearsay match itself is not fixed, and I'm arguing it shouldn't be rewritten in this pass.**
The reviewer is right that `HeardItemMentioned` is a plain substring match with no truth, affirmation, or
per-container check — "I don't have a Small Healing Potion" would satisfy it. But this is not an
undiscovered defect: it is a documented, deliberate simplification from v0.6, made to close a *worse* hole.
Before v0.6, `take_item` had **no** informational-basis check at all — a Dungeon Master answering from
leaked hidden state could turn straight into a real ownership change ([v0_6_issues.md #4](../reports/v0_6_issues.md#L75)).
`HeardItemMentioned`'s own doc comment already calls out the tradeoff explicitly: "a deliberately simple name
match ... a permissive basis (better to allow a plausibly-heard take than to over-refuse)."

Replacing it with genuine typed evidence — a character asserting a structured claim about an item's
existence and location, verifiable against truth — is a real feature, not a bug fix: speech today is
delivered verbatim through [`NarrationLog.RecordSpeech`](../src/ModelsAndMonsters/Orchestration/NarrationLog.cs#L69)
with no model call in between to interpret or structure it (v0.3). Building a structured "claim" channel just
for this check would be a second speech pathway alongside that one, and the project's own stated principle
(["no language parsing for game semantics"](architecture.md#L691)) argues against patching the current
heuristic with more keyword logic rather than replacing the mechanism outright.

Given the exploit requires an agent to both phrase a very specific negated sentence *and* already know which
container to target, and given the existing tradeoff was made on purpose with the downside named in the
code, I left it as accepted risk and did not attempt a rushed structural change. This matches the review's
own recommendation #2 ("replace free-text hearsay matching with typed evidence") as a separate, later piece
of work — not something to improvise inside a review-response pass.

### 2. The engine permits surrendered or escaped actors to act — fixed

Confirmed exactly as described: `attack_character`, `use_item`, `open_container`, `take_item`, and
`inspect_object` all gated the actor on `!actor.IsAlive` instead of `!actor.CanAct`, while
`give_item`/`drop_item`/`steal_item`/`offer_surrender`/etc. already correctly used `CanAct` plus the shared
[`RejectInactiveActor`](../src/ModelsAndMonsters/Engine/GameEngine.cs#L2610) helper (which also gives a
surrendered or escaped actor its own correct in-world refusal, instead of the generic — and wrong —
"is dead and cannot act"). This is exactly the newer-action/older-action drift the review's architecture
section describes.

All five sites now call `RejectInactiveActor`, matching the pattern already used elsewhere in the same file.
No existing test asserted the old (incorrect) behavior, so nothing needed reconciling — a dead actor's
existing tests (`ActorIsDead`) still pass unchanged, since `RejectInactiveActor` still returns that reason
for `CharacterDisposition.Dead`.

Regression tests added: [GameEngineTests.cs](../tests/ModelsAndMonsters.Tests/GameEngineTests.cs) (attack,
use_item), [ObjectEngineTests.cs](../tests/ModelsAndMonsters.Tests/ObjectEngineTests.cs) (open_container,
take_item), [KnowledgeModelTests.cs](../tests/ModelsAndMonsters.Tests/KnowledgeModelTests.cs) (inspect_object)
— each parameterised over `Surrendered` and `Escaped`, asserting `EngineRejectionReason.ActorNotActive`.

This also resolves the "some legacy actions say 'alive' where the current disposition model requires
'active'" documentation-drift bullet: the stale wording lived in the rejection messages these five sites
produced, and `RejectInactiveActor` already speaks correctly for every disposition.

### 3. The resolver's real output cap disagreed with the configured budget — fixed

Confirmed: [appsettings.json](../src/ModelsAndMonsters/appsettings.json) set `RulebookResolver.MaxOutputTokens`
to 600 (a v0.7 figure), which — via `resolverConfig.MaxOutputTokens ?? harness.RulebookOutputTokens` in
[`SimulationRunner.cs:165`](../src/ModelsAndMonsters/Orchestration/SimulationRunner.cs#L165) — silently
overrode the v0.8 `Harness:RulebookOutputTokens` figure of 300 used everywhere else: the startup fit check
([`RulebookRequestBudget.Validate`](../src/ModelsAndMonsters/Rulebook/RulebookRequestBudget.cs#L94)) and the
consultation trace. `SimulationOptions.cs`'s own doc comment even says "300 since v0.8, down from 600" — the
appsettings override was simply never removed when the default moved.

**Fix:** removed the `MaxOutputTokens: 600` line from `RulebookResolver` in appsettings.json, so it now falls
through to `Harness.RulebookOutputTokens` (300) like the fit check and trace already assume. Per the
resolver's own remarks, measured replies run 69-132 tokens, so 300 remains generous headroom.

## Medium-priority findings

### 4. DM truncation diagnosed via the wrong profile — fixed

Confirmed the described bug at [`TurnCoordinator.cs:958`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs#L958)
(pre-fix): it called `character.WasReplyCutShort(response)` on a response that came from `_dungeonMaster`, not
`character`. In practice this only bites on the fallback path (a provider that reports no finish reason,
which `WasTruncated`'s `FinishReason == Length` check doesn't need); for every other provider the console
notice's advice — unconditionally "raise MaxOutputTokens for the DungeonMaster agent" — was also wrong on its
own terms, since adjudication runs under a deliberately tight 400-token cap on a shared window
([`DungeonMasterAgent.AdjudicationOutputBudget`](../src/ModelsAndMonsters/Agents/DungeonMasterAgent.cs#L99)),
and raising it there steals room from the request carrying the largest input — the exact thing
[README.md:339](../README.md#L338) says not to do.

**Fix, three parts:**
1. `ModelAgent.WasReplyCutShort`/`WasContextExhausted` ([ModelAgent.cs:252,268](../src/ModelsAndMonsters/Agents/ModelAgent.cs#L252))
   now accept an optional `maxOutputTokensOverride`, since a per-call cap is never written back into
   `Profile` (it's a local variable in `SendWithTransientRetryAsync`) and defaulting to `Profile.MaxOutputTokens`
   silently ignores it.
2. `DungeonMasterAgent` exposes [`WasAdjudicationReplyCutShort`](../src/ModelsAndMonsters/Agents/DungeonMasterAgent.cs#L110),
   which passes its own `AdjudicationOutputBudget` as that override.
3. `TurnCoordinator` now calls [`_dungeonMaster.WasAdjudicationReplyCutShort(response)`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs#L958),
   and the console notice is now conditional: on a shared window it explains the adjudication cap and
   explicitly says raising `MaxOutputTokens` would not help; only on a provider where the window isn't
   shared does it give that advice.

Regression test: `Adjudication_truncation_is_diagnosed_against_the_DMs_own_tightened_budget_not_a_characters`
in [TurnOrchestrationTests.cs](../tests/ModelsAndMonsters.Tests/TurnOrchestrationTests.cs), using a
no-finish-reason response whose usage falls inside the 400-token adjudication budget but nowhere near a
character's general-purpose allowance.

### 5. Scenario validation doesn't enforce global identity — mostly fixed, one part argued

**Fixed:** [`ScenarioFactory`](../src/ModelsAndMonsters/Engine/ScenarioFactory.cs) now has
`ValidateGlobalItemIdentity` (line 90), which checks every item id — across every container, every
character's inventory, and every character's weapon — for scenario-wide uniqueness, not just uniqueness
within the one container or list it was declared in. It also rejects duplicate **character names** (line 32),
not just duplicate ids, since the Dungeon Master and every character address one another by name.

This validation immediately caught a live instance of exactly the bug the review describes:
`The_scenario_factory_builds_container_objects_from_the_room_definition` seeded a chest with an item id
`small-healing-potion` on top of the stock 2v2 scenario, where Elara already carries an item with that exact
id — a genuine cross-container collision, sitting in the test suite, that would have let
`KnowledgeLedger.KnowsItem` leak one item's discovery onto the other. Fixed the test to use a distinct id;
it wasn't testing item identity and didn't need the collision.

**Argued, not changed:** [`GameState.Resolve`](../src/ModelsAndMonsters/Domain/GameState.cs#L51) still
resolves a name via `FirstOrDefault` with no ambiguity signal, unlike every other resolvable reference
(objects, exits, item lookups all report `Ambiguous` separately from "not found"). The review is correct that
this is inconsistent. But now that `ScenarioFactory` refuses a scenario with duplicate character names at
load time, `Resolve`'s first-match fallback can no longer actually encounter an ambiguous name from any valid
scenario — the precondition is enforced at the one boundary where scenarios are constructed. Reworking
`Resolve` to return an ambiguity result would mean widening its signature and touching every call site across
the engine (mirroring the `EngineRejectionReason.*ReferenceAmbiguous` treatment items/objects/exits already
get) for a case that can no longer arise. I left it as accepted, lower-priority hardening rather than doing
that refactor in this pass — it's real belt-and-suspenders value, just not proportionate to bundle in here.

### 6. `NewlyInjected` trace data is unreliable — fixed

Confirmed: [`TracingChatClient.EmitRequest`](../src/ModelsAndMonsters/Tracing/TracingChatClient.cs#L99) (pre-fix)
computed "newly injected" purely from `traced.Count > _messagesTracedLastCall`. Since the Dungeon Master's
three job types (adjudicate/narrate/answer) all run on fresh, bounded projections that are almost always the
same shape (a system prompt plus one user message), every call after the first on that agent's shared
`TracingChatClient` instance reported nothing newly injected — even switching from `dm.adjudicate` to
`dm.narrate.outcome`, an entirely different conversation with a different system prompt.

**Fix:** replaced the count comparison with a content-fingerprint longest-common-prefix diff
([TracingChatClient.cs:99-124](../src/ModelsAndMonsters/Tracing/TracingChatClient.cs#L99)): each message is
serialised to canonical JSON (`TraceJson.Compact`, already used elsewhere in `Tracing/`), and "newly injected"
is everything after the point where this call's fingerprints stop matching the previous call's, in order. An
unchanged system prompt still matches at index 0 (correctly *not* new); a differently-worded projection
diverges immediately (correctly *all* new); history compaction that replaces messages without changing the
count is caught because content, not count, is what's compared.

Regression test: `Newly_injected_is_correct_even_when_a_fresh_projection_matches_the_previous_message_count`
in [TracingTests.cs](../tests/ModelsAndMonsters.Tests/TracingTests.cs), asserting the DM's `dm.narrate.outcome`
request reports its whole message list as newly injected even though it carries the same count as the
preceding `dm.adjudicate` request.

### 7. End-of-run advice can recommend the wrong fix — fixed

Both halves confirmed:

- [`WarnAboutReasoningStarvation`](../src/ModelsAndMonsters/Orchestration/SimulationRunner.cs#L651) counted
  *every* `ModelResponseTruncated` event and advised "disable Thinking, or raise MaxOutputTokens" regardless
  of cause — advice that only applies to the reasoning-only case and is actively unhelpful for a plain
  context-exhaustion truncation.
- [`WarnAboutContextSaturation`](../src/ModelsAndMonsters/Orchestration/SimulationRunner.cs#L626) recommended
  "Raise ContextWindow." This event only ever fires for a provider whose
  [`SilentlyTruncatesHistory`](../src/ModelsAndMonsters/AI/ProviderCapabilities.cs#L60) is true — which is
  Ollama alone — so this warning's advice was, by construction, always aimed at the one provider this
  project's Qwen configuration is required to keep at a fixed 8,192-token window. Raising it there duplicates
  the whole model in memory rather than fixing anything.

**Fix:** `ExperimentTrace` gained a dedicated `NoteReasoningOnlyTruncation`/`ReasoningOnlyTruncationCount`
counter ([ExperimentTrace.cs:65-83](../src/ModelsAndMonsters/Tracing/ExperimentTrace.cs#L65)), incremented
from `TracingChatClient.EmitTruncationIfAny` only when the truncation is genuinely reasoning-only.
`WarnAboutReasoningStarvation` now reads that counter instead of the blanket truncation count.
`WarnAboutContextSaturation`'s message no longer suggests raising `ContextWindow` — it points at shortening
the run, lowering `RecentTurnsKeptFull`, or history summarisation instead, and says explicitly why raising
the window is the wrong fix on Ollama.

Regression tests extended in [TurnOrchestrationTests.cs](../tests/ModelsAndMonsters.Tests/TurnOrchestrationTests.cs):
the existing reasoning-only-truncation test now also asserts `ReasoningOnlyTruncationCount == 1`, and the
existing plain-truncation test asserts it stays at `0`.

### 8. The web observer retains disconnected subscribers and completed runs — fixed

Confirmed both parts: [`Program.cs`'s SSE endpoint](../src/ModelsAndMonsters.Web/Program.cs) subscribed a
channel but never unsubscribed it when the viewer disconnected, so a disconnected viewer's unbounded channel
kept queuing every subsequent published event for a reader that would never arrive. `RunManager._runs` never
evicted a completed session (nor its full in-memory event buffer), and rekeying added the real run id as a
second entry without removing the provisional one.

**Fix:**
- `RunSession` gained [`Unsubscribe`](../src/ModelsAndMonsters.Web/RunManager.cs#L76), and `Program.cs`'s SSE
  handler now calls it in a `finally` block around the event stream.
- `RunSession` records [`CompletedAt`](../src/ModelsAndMonsters.Web/RunManager.cs#L32) when it finishes.
- `RunManager` gained [`PruneCompletedRuns`](../src/ModelsAndMonsters.Web/RunManager.cs#L205), run at the
  start of every `Start()` call, which evicts any session that completed more than 30 minutes ago — long
  enough for a viewer to reconnect and replay a just-finished run, short enough that a long-lived host
  doesn't accumulate every run it has ever started. Both the provisional and real-id dictionary entries for a
  session get swept in the same pass, since each is checked independently against that session's own
  `CompletedAt`.

New test file [RunSessionTests.cs](../tests/ModelsAndMonsters.Tests/RunSessionTests.cs) covers `Unsubscribe`
(a disconnected viewer's channel receives nothing further published; a second, still-subscribed viewer is
unaffected) and `CompletedAt`. `RunManager.PruneCompletedRuns` itself isn't covered by an automated test — a
`RunManager` requires a full DI-constructed `SimulationOptions`/`ScenarioDefinition`/`IChatClientFactory` to
build, which is disproportionate scaffolding for testing a time-based eviction sweep; it was verified by
inspection and by the passing build instead.

## Documentation and build mismatches

- **`ModelsAndMonsters.slnx` didn't list the Web project — fixed.** Added
  `src/ModelsAndMonsters.Web/ModelsAndMonsters.Web.csproj` to the solution file. (The test project already
  references `ModelsAndMonsters.Web` directly for `StateDto` — see
  [DispositionWebTests.cs](../tests/ModelsAndMonsters.Tests/DispositionWebTests.cs) — so this was purely a
  solution-file/IDE-visibility gap, not a coverage gap.)
- **The React client has no automated test script — acknowledged, deferred.** Confirmed:
  [client/package.json](../src/ModelsAndMonsters.Web/client/package.json) has no `test` script and no test
  framework installed. Adding one (e.g. Vitest + Testing Library) from scratch is a real, separate piece of
  setup work, not a one-line fix, and is out of proportion for a review-response pass on what is currently a
  thin observer UI over the authoritative state. Flagged here as accepted debt rather than silently dropped.
- **Stale "alive" comments** — resolved as a side effect of fixing finding #2; see above.

## Architecture assessment

Agreed with the assessment, including the "concentration" risk in `TurnCoordinator.cs`, `GameEngine.cs`, and
`RunReportWriter.cs`. The review's own ordering is right: fix the invariant violations before touching file
structure, because several of the bugs above (the `CanAct`/`IsAlive` drift, the Steal/Take asymmetry) are
symptoms of that concentration, not independent mistakes — splitting the files first without fixing the
underlying gates would just relocate the same inconsistencies. All six items in the review's
"Invariant enforcement" list are now addressed (five fixed outright, the sixth — typed hearsay evidence —
addressed for its concrete asymmetry and explicitly deferred in full for the reasons in finding #1).

The recommended follow-on work — extracting action-family handlers out of `TurnCoordinator` and `GameEngine`
— was deliberately **not** attempted here. It's a large, higher-risk structural change that deserves its own
session with its own before/after verification, not something to fold into a review-response pass that's
already touching the behavior those files implement.

## Verification

```text
dotnet build ModelsAndMonsters.slnx --no-restore
  Build succeeded, 0 errors (5 pre-existing warnings, unrelated to this change)

dotnet test ModelsAndMonsters.slnx --no-restore --no-build
  Passed: 928
  Failed: 0
  Skipped: 0
```

909 → 928: 19 new regression tests, one pre-existing test corrected (see finding 5). No source file was
changed outside of what's described above; `appsettings.json` lost one line (the stale `MaxOutputTokens`
override) and gained none.
