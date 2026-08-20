# Models and Monsters — Code Review

Reviewed 20 August 2026 against the current working tree.

Overall, the architecture is strong, but the review found three significant boundary defects and several medium-risk observability and configuration problems. All 909 tests pass.

The working tree was already extensively modified and contained untracked v0.8 files when the review began. The findings below therefore describe the current files on disk, not necessarily a committed baseline.

## High-priority findings

### 1. Free-text speech controls access to hidden items

[`TakeLacksKnowledgeBasis`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs#L3054) treats any previously heard speech containing an item's display name as permission to take that item. It does not establish that the statement was true, affirmative, or about this container. Hearing “I don't have a Small Healing Potion” could authorize taking a same-named hidden potion elsewhere.

This directly contradicts the documented rule that prose matching must never determine knowledge or world mutation ([architecture.md](architecture.md#L685)).

There is also an inconsistency in the opposite direction: [`StealLacksKnowledgeBasis`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs#L3003) ignores hearsay entirely, although the v0.6 specification explicitly allows being told about an item ([build_v0_6.md](prompts/build_v0_6.md#L171)).

### 2. The authoritative engine permits surrendered or escaped actors to perform several actions

`Character.IsAlive` includes surrendered and escaped characters, while only `CanAct` means active ([Character.cs](../src/ModelsAndMonsters/Domain/Character.cs#L118)). Nevertheless, attack, use-item, open-container, take-item and inspect-object validate only `IsAlive`, for example [GameEngine.cs](../src/ModelsAndMonsters/Engine/GameEngine.cs#L653) and [GameEngine.cs](../src/ModelsAndMonsters/Engine/GameEngine.cs#L1823).

The normal turn loop masks this by scheduling only active actors, but the engine is documented as the authoritative reusable boundary. A direct engine call can therefore make a surrendered character attack or an escaped character use an item.

### 3. The resolver's real output cap is 600 while budgeting and tracing claim 300

The configured resolver profile sets 600 tokens ([appsettings.json](../src/ModelsAndMonsters/appsettings.json#L41)), while `Harness:RulebookOutputTokens` is 300 ([appsettings.json](../src/ModelsAndMonsters/appsettings.json#L65)). [`SimulationRunner`](../src/ModelsAndMonsters/Orchestration/SimulationRunner.cs#L165) uses 600 for the actual call but passes 300 into the startup fit check and consultation trace.

That contradicts both the option documentation ([SimulationOptions.cs](../src/ModelsAndMonsters/Configuration/SimulationOptions.cs#L372)) and architecture table ([architecture.md](architecture.md#L535)).

On the required 8,192-token Qwen configuration, the guard can accept a request with insufficient room for the actual configured response, defeating the v0.8 context reduction.

## Medium-priority findings

### 4. DM truncation is diagnosed using the character's model profile

[`TurnCoordinator`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs#L958) calls `character.WasReplyCutShort(response)` for a Dungeon Master response. That uses the character's context window and output limit, while DM adjudication has a hard 400-token override ([DungeonMasterAgent.cs](../src/ModelsAndMonsters/Agents/DungeonMasterAgent.cs#L65)).

Providers returning no finish reason can therefore be misclassified. The resulting advice to raise the DM limit also directly contradicts the documented reason the 400-token cap should remain ([README.md](../README.md#L332)).

### 5. Scenario validation does not enforce global stable identity

[`ScenarioFactory`](../src/ModelsAndMonsters/Engine/ScenarioFactory.cs#L157) checks duplicate item IDs only inside each individual container. It does not check inventories, weapons, or different containers against each other. Because `KnowledgeLedger.KnowsItem` is keyed by item ID, observing one item can grant knowledge of another item sharing its ID.

Duplicate character names are also accepted, and [`GameState.Resolve`](../src/ModelsAndMonsters/Domain/GameState.cs#L51) silently selects the first matching name rather than reporting ambiguity.

### 6. `NewlyInjected` model-trace data is unreliable

[`TracingChatClient`](../src/ModelsAndMonsters/Tracing/TracingChatClient.cs#L103) derives newly injected messages solely from the change in message count. Fresh DM projections commonly contain the same number of entirely new messages, so every call after the first reports nothing newly injected. History compaction can similarly replace messages without increasing the count.

Full message traces remain available, but this diagnostic field does not satisfy its stated purpose.

### 7. End-of-run context advice can recommend the wrong fix

[`WarnAboutReasoningStarvation`](../src/ModelsAndMonsters/Orchestration/SimulationRunner.cs#L636) counts every `ModelResponseTruncated` event, not just `ReasoningOnly`, and advises raising output limits. [`WarnAboutContextSaturation`](../src/ModelsAndMonsters/Orchestration/SimulationRunner.cs#L618) recommends raising `ContextWindow`.

Both conflict with the project's central 8,192-token residency requirement and the README's instruction to distinguish input saturation from output exhaustion ([README.md](../README.md#L315)).

### 8. The web observer retains disconnected subscribers and completed runs

[`RunSession.Subscribe`](../src/ModelsAndMonsters.Web/RunManager.cs#L42) adds an unbounded channel, but the SSE endpoint does not unsubscribe it when a viewer disconnects ([Program.cs](../src/ModelsAndMonsters.Web/Program.cs#L65)). Later events accumulate without a reader until the run completes.

Completed sessions are never evicted from `_runs`, and rekeying retains both provisional and real IDs. A long-lived observer host will continually grow memory.

## Documentation and build mismatches

- [`ModelsAndMonsters.slnx`](../ModelsAndMonsters.slnx) does not list the web project. It happens to compile during tests because the test project references it, which is fragile build coverage. The React client is not built or tested by the .NET solution and has no automated test script.
- Several comments still describe older behavior—for example, some legacy actions say “alive” where the current disposition model requires “active.” That drift appears to be the source of the engine-validation inconsistency.

## Architecture assessment

The best parts are genuinely good:

- Explicit model → orchestration → deterministic engine separation.
- Immutable authoritative state and typed actions and outcomes.
- Stateless rulebook resolver and projected DM contexts.
- Per-character knowledge boundaries and toolless narration.
- Seeded engine randomness and unusually comprehensive trace and report artifacts.
- Excellent deterministic test coverage.

The main structural risk is concentration. [`TurnCoordinator.cs`](../src/ModelsAndMonsters/Orchestration/TurnCoordinator.cs#L29) is about 4,000 lines, [`GameEngine.cs`](../src/ModelsAndMonsters/Engine/GameEngine.cs#L33) about 2,700, and [`RunReportWriter.cs`](../src/ModelsAndMonsters/Tracing/RunReportWriter.cs#L21) about 3,400. Actor validation, knowledge policy, tool dispatch, retries, diagnostics, narration and tracing are consequently repeated or interleaved.

The bugs above are characteristic of that: a newer action uses `CanAct`, an older action uses `IsAlive`; one knowledge gate uses prose, another does not.

Invariant enforcement should be addressed before broader refactoring:

1. Centralize engine actor validation.
2. Replace free-text hearsay matching with typed evidence.
3. Establish one actual resolver output budget used by configuration, calls, fit checks and tracing.
4. Validate global scenario identities and ambiguous character names at startup.
5. Base truncation diagnostics on the exact agent profile and per-call options.
6. Bound and evict web run state and explicitly unsubscribe disconnected viewers.

After those fixes, action-family handlers could be extracted incrementally from the two large classes without a wholesale rewrite.

## Verification

The following read-only checks were performed:

```text
dotnet test ModelsAndMonsters.slnx --no-restore --verbosity minimal

Passed: 909
Failed: 0
Skipped: 0
Duration: 3 seconds
```

`git diff --check` found no whitespace errors, only existing LF/CRLF conversion warnings.

No live model calls or frontend build were performed. No source, configuration, or existing documentation files were changed as part of the review; this document was added later at the user's request.
