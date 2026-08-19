# Models & Monsters — v0.2 Session Issues (Found & Fixed)

Problems that were both **encountered and resolved** during this session, in the order they came
up. Each entry is a real failure hit live (a 400, a wrong assertion, a build error) with the fix
that landed for it. Anything left unresolved, or that only re-confirmed/tidied a fix from an earlier
session, is intentionally left out.

---

### 1. Report booleans compared as strings always failed

**Problem:** `RunReportWriter` checked flags like `SeedWasProvided` with `Text(...) == "True"`. .NET
serializes JSON booleans lowercase (`true`/`false`), so the comparison was always false — seeds
always looked "not provided" in the report regardless of the actual run.

**Resolution:** Added an `IsTrue()` helper that reads the `JsonValueKind` directly instead of
string-comparing, and switched the boolean checks (`SeedWasProvided`, `Resolved`) to use it.

---

### 2. Anthropic caching test couldn't read the cache marker

**Problem:** A test needed to assert the leading system message was marked for prompt caching, but
the SDK's `GetCacheControl` reader is `internal` — inaccessible from the test project (`CS1061`).

**Resolution:** Used the marker's public footprint instead — `content.AdditionalProperties is {
Count: > 0 }` — as the observable signal, and exposed it via `AnthropicPromptCachingChatClient.
HasCacheControl` for tests to call.

---

### 3. Anthropic haiku rejected temperature + top_p together

**Problem:** A live run against `claude-haiku-4-5` 400'd — Anthropic accepts only one of
`temperature`/`top_p` per request, but the shared `Default` profile set both.

**Resolution:** Added `ProviderCapabilities.AllowsTemperatureAndTopPTogether` (false for Anthropic);
when both are configured, `ChatOptionsFactory` keeps temperature and drops top_p rather than sending
an invalid combination.

---

### 4. Opus 4.8 / Claude 5 reject all sampling parameters

**Problem:** A run against `claude-opus-4-8` 400'd with "temperature is deprecated for this model."
The first regex fix only matched `"opus"`, so it missed the wider Claude 5 family (`sonnet-5`, …)
which forbids sampling the same way.

**Resolution:** `ModelForbidsSampling` generalized to `opus-4-[7-9]` OR
`claude-[a-z]+-(?:[5-9]|\d\d)\b`, with a per-agent `OmitSampling` override. Sampling is dropped
(and reported) rather than sent for those models.

---

### 5. Context-saturation warnings were false positives on OpenAI and small Ollama prompts

**Problem:** The "history was silently truncated" detector fired on providers that don't actually
truncate (OpenAI rejects an over-long request instead), and produced noise on small Ollama prompts
from tokenizer-estimate slack.

**Resolution:** Gated the detector behind `ProviderCapabilities.SilentlyTruncatesHistory` (false on
OpenAI/Anthropic) and raised the Ollama noise floor (`MinimumDroppedTokens` 300 → 500).

---

### 6. Anthropic prompt caching looked broken (0 cache reads reported)

**Problem:** The trace and report showed cache *writes* but never cache *reads*, making working
caching look like pure waste. Root cause: the Anthropic client library surfaces
`cache_creation_input_tokens` but not `cache_read_input_tokens` into the standard usage object.

**Resolution:** `AnthropicPromptCachingChatClient.RecoverCacheReadCount` reads the count off
`response.RawRepresentation` (the raw Anthropic `Message`) and folds it into
`usage.AdditionalCounts`, so the report shows both sides. Confirmed against the Anthropic dashboard
(~50% cost cut on a cached run).

---

### 7. Anthropic thinking rejected custom sampling and forced tool choice

**Problem:** Setting `Effort: "medium"` on an Anthropic agent 400'd — `` `temperature` may only be
set to 1 when thinking ``. Extended thinking on Anthropic disallows custom sampling and forced tool
use; the harness's normal Claude-5 sampling guard didn't cover 4.x models (which normally accept
sampling and only forbid it once thinking is switched on).

**Resolution:** `ChatOptionsFactory` now detects when Anthropic thinking is actually engaged
(`Effort > None` on a non-modern-Claude model) and folds it into `omitSampling`, and drops any
`ForceToolChoice` for that call.

---

### 8. The M.E.AI Anthropic adapter can't express reasoning on Claude 5 / Opus 4.7+

**Problem:** With sampling fixed, the next Anthropic run 400'd on a `claude-sonnet-5` actor: `"thinking.type.enabled" is not supported for this model. Use "thinking.type.adaptive" and
"output_config.effort"`. The installed adapter (Anthropic pkg 12.40.0 / M.E.AI 10.9.0) only emits
the legacy `thinking.type.enabled` shape, which the modern Claude models reject outright.

**Resolution:** `ChatOptionsFactory` detects modern-Claude models (the same regex as #4) and drops
a raised `Effort` there instead of sending an unsupported request shape; `Effort: none` still works
everywhere since it doesn't touch thinking at all.

---

### 9. `claude-sonnet-4-8` does not exist

**Problem:** A run configured with `ModelId: "claude-sonnet-4-8"` failed with `AnthropicNotFoundException` — Anthropic's `-4-8` suffix only exists for Opus; Sonnet goes `...-4-5` then
jumps straight to `-5`.

**Resolution:** Identified the typo and corrected the config to `claude-sonnet-4-5`, which is a real
model and (per #10 below) exercises the reasoning-with-tools path correctly.

---

### 10. OpenAI non-reasoning models rejected the `reasoning_effort` argument outright

**Problem:** A run with `gpt-4o-mini` as the Dungeon Master and `Effort: "none"` 400'd on the very
first call: `Unrecognized request argument supplied: reasoning_effort`. General-purpose OpenAI
models reject the argument even when the value would be a no-op.

**Resolution:** Added `IsOpenAIReasoningModel` (matches the o-series and GPT-5+) and gated effort on
it — on a non-reasoning OpenAI model, `Effort: none` is omitted entirely (nothing sent) and a raised
effort is dropped-and-reported.

---

### 11. OpenAI rejected `reasoning_effort` together with function tools

**Problem:** Even on a genuine reasoning model (`gpt-5.6-sol`), the first tool-bearing call 400'd:
`Function tools with reasoning_effort are not supported for gpt-5.6-sol in
/v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'`.
Every in-game call carries tools, so this made OpenAI reasoning effectively unusable via chat
completions.

**Resolution:** Switched the OpenAI client from `GetChatClient(...)` (chat completions) to
`GetResponsesClient().AsIChatClient(...)` (the Responses API) in `ChatClientFactory`, which allows
reasoning effort alongside function tools. `ChatOptionsFactory`'s tools-based restriction was
removed accordingly (`OPENAI001` experimental-API warning suppressed locally with a comment).

---

### 12. OpenAI reasoning models rejected sampling once reasoning was actually engaged

**Problem:** With the Responses switch in place, raising effort to `"medium"` on `gpt-5.6-sol`
400'd: `Unsupported parameter: 'temperature' is not supported with this model`. The `Effort: none`
run had worked because reasoning wasn't engaged yet; `medium` engages it, and OpenAI's reasoning
models reject temperature/top_p the same way Anthropic's thinking does.

**Resolution:** Added `openAiReasoningActive` (OpenAI + a reasoning model + `Effort > None`) and
folded it into `omitSampling`, mirroring the Anthropic fix in #7. Live-verified clean afterward:
`gpt-5.6-sol` NPCs at `Effort: "medium"` completed a full encounter with sampling correctly dropped.

---

### 13. Model reasoning was captured but never shown in the report

**Problem:** Every provider's private reasoning was already being captured into the trace
(`TextReasoningContent` → `TracedContent{Type:"reasoning"}`), but the Markdown report's transcript
never rendered it — so a genuinely useful signal (why a character chose its move) was invisible
without reading raw JSONL.

**Resolution:** Added `TranscribeReasoning`/`ExtractReasoning` to `RunReportWriter`, folding each
call's reasoning into a collapsible `<details>💭 <actor> — thinking</details>` block in the
transcript, in both the full and summary reports. Verified against a real qwen3.5 run (12 rendered
blocks) and confirmed old runs can be re-rendered retroactively via `--report <run-dir>`.
