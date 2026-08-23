# Rulebook selection — measured comparison

Rulebook: **25** cards, **rulebook-a77b3336d2**.
Corpus: **45** labelled cases across **11** families.

| Strategy | Required-card recall | Cases with every required card | Decision agreement | Wrongly supported | Wrongly unsupported | Avg cards | Avg resolver tokens | Avg selection tokens | Model calls | Fallbacks |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| WholeRulebook (baseline, shipped) | 100.0% | 45/45 | 45/45 (100%) | 0 | 0 | 25.0 | 6386 | 0 | 1.0 | 0/45 |
| Oracle (answer key — the ceiling, not a strategy) | 100.0% | 45/45 | 45/45 (100%) | 0 | 0 | 4.8 | 2132 | 0 | 1.0 | 0/45 |
| StructuredRouting (no declared family available) | 100.0% | 45/45 | 45/45 (100%) | 0 | 0 | 25.0 | 6386 | 0 | 1.0 | 45/45 |
| Embedding (offline trigram prototype, top-3) | 56.3% | 21/45 | 26/45 (58%) | 0 | 19 | 8.6 | 3186 | 0 | 1.0 | 0/45 |
| Embedding (Ollama:nomic-embed-text, top-3) | 87.8% | 36/45 | 40/45 (89%) | 0 | 5 | 7.7 | 2761 | 0 | 1.0 | 0/45 |
| CompactIndex (Ollama:qwen3.5:9b) | 96.7% | 42/45 | 44/45 (98%) | 0 | 1 | 7.5 | 2802 | 2372 | 2.0 | 0/45 |
| ActionRouting (Ollama:qwen3.5:9b) | 98.9% | 44/45 | 45/45 (100%) | 0 | 0 | 7.2 | 2713 | 975 | 2.0 | 0/45 |

> **Why StructuredRouting equals WholeRulebook (25.0 cards, 0% saving).** StructuredRouting only narrows the book when the *caller* declares an action family for the request. Ordinary consultation supplies a free-text intent with no action decided yet — deciding it is the resolver's job — so no family is ever declared and the selector falls back to the whole rulebook on every case (hence 45/45 fallbacks). It is included to make the point explicit: routing on declared metadata alone is inapplicable to a free-text intent. **ActionRouting is StructuredRouting *with* a classifier** — it spends one model call (975 selection tokens) to derive the family, and that is what takes it from 25.0 cards down to 7.2.


## Reduction against the baseline

| Strategy | Avg total input tokens | Reduction |
| --- | --- | --- |
| WholeRulebook (baseline, shipped) | 6386 | 0.0% |
| Oracle (answer key — the ceiling, not a strategy) | 2132 | 66.6% |
| StructuredRouting (no declared family available) | 6386 | 0.0% |
| Embedding (offline trigram prototype, top-3) | 3186 | 50.1% |
| Embedding (Ollama:nomic-embed-text, top-3) | 2761 | 56.8% |
| CompactIndex (Ollama:qwen3.5:9b) | 5174 | 19.0% |
| ActionRouting (Ollama:qwen3.5:9b) | 3687 | 42.3% |

CompactIndex and ActionRouting ran against **Ollama:qwen3.5:9b** (temperature 0.1), one selection call per case. Selection step averaged **5132 ms** (CompactIndex) and **2460 ms** (ActionRouting).

## Cases that lost a required rule

- **Embedding (offline trigram prototype, top-3)**: attack-plain, attack-vs-dirty-strike, ability-guard, use-item-vs-heal-ability, use-item-vs-steady-purpose, defend, inspect, open-and-take-compound, loot-a-body, drop, steal, throw-item-to-ally, steal-vs-attack-compound, open-and-flee-compound, intimidate, threat-with-a-blow, steady-ally, steady-vs-rally, plain-coordination, stale-ownership-steal, unsupported-disarm, ambiguous-reach, damage-object-vs-attack, unsupported-force-container
- **Embedding (Ollama:nomic-embed-text, top-3)**: use-item-vs-heal-ability, use-item-vs-steady-purpose, steal-vs-attack-compound, intimidate, threat-with-a-blow, plain-coordination, leave-cover-vs-exposing-action, unsupported-equip, unsupported-force-container
- **CompactIndex (Ollama:qwen3.5:9b)**: plain-coordination, damage-object-vs-attack, unsupported-force-container
- **ActionRouting (Ollama:qwen3.5:9b)**: plain-coordination

## Corpus clarity distribution

Evidence for the on-demand strategy: how often an intent is clear-cut enough that a Dungeon Master holding only lean core rules would not need to look anything up.

- Clear: **18** (40%)
- Ambiguous: **19** (42%)
- Unsupported: **8** (18%)

## Corpus coverage

All **25** cards are required by a case (or allow-listed), and all **19** engine actions are routed to by a case. The eval fails otherwise.

Corpus labelled against **rulebook-a77b3336d2**, which matches the live catalog.

Cards no case requires, on purpose:

- `environment.cover` — A background mechanic (how cover capacity, interception and destruction work), not an action. It is reached only through the cover actions' declared links; no decision turns on it being the required card.

## Avg cards after expansion

The number that decides a narrowing strategy: how many cards the resolver ends up reading. The whole-rulebook baseline sends every card; the Oracle is the ceiling a perfect selection reaches. A routing strategy earns its place at 5–7; at 15+ the exclusions are dense enough that the book is effectively irreducible — a clean negative, to be read as one.

| Strategy | Avg cards after expansion |
| --- | --- |
| WholeRulebook (baseline, shipped) | 25.0 |
| Oracle (answer key — the ceiling, not a strategy) | 4.8 |
| StructuredRouting (no declared family available) | 25.0 |
| Embedding (offline trigram prototype, top-3) | 8.6 |
| Embedding (Ollama:nomic-embed-text, top-3) | 7.7 |
| CompactIndex (Ollama:qwen3.5:9b) | 7.5 |
| ActionRouting (Ollama:qwen3.5:9b) | 7.2 |

## Fallbacks by kind

A fallback is the safety net firing — the whole rulebook sent rather than a guess. SEMANTIC means the model judged the intent unclear or named an action outside the set (the boundaries are wrong); MECHANICAL means the reply could not be parsed or the call threw (the transport is broken). The two demand opposite responses and must never read as one number.

| Strategy | Total | Semantic | Mechanical |
| --- | --- | --- | --- |
| WholeRulebook (baseline, shipped) | 0/45 | 0 | 0 |
| Oracle (answer key — the ceiling, not a strategy) | 0/45 | 0 | 0 |
| StructuredRouting (no declared family available) | 45/45 | 45 | 0 |
| Embedding (offline trigram prototype, top-3) | 0/45 | 0 | 0 |
| Embedding (Ollama:nomic-embed-text, top-3) | 0/45 | 0 | 0 |
| CompactIndex (Ollama:qwen3.5:9b) | 0/45 | 0 | 0 |
| ActionRouting (Ollama:qwen3.5:9b) | 0/45 | 0 | 0 |

## Action-label accuracy (ActionRouting)

Of **44** cases with an expected routing action, the model named the right one on **34** (77%). **0** were routed to no action (the model said unclear, or the reply did not parse) and fell back to the whole rulebook.

Confused pairs (expected → named), most frequent first — each points at a boundary to write:

| Expected | Named | Count | Cases |
| --- | --- | --- | --- |
| use_ability | attack_character | 2 | attack-vs-dirty-strike, ability-guard |
| open_container | take_item | 1 | open-and-take-compound |
| give_item | damage_environmental_object | 1 | throw-item-to-ally |
| attack_character | steal_item | 1 | steal-vs-attack-compound |
| accept_surrender | take_item | 1 | accept-surrender |
| offer_surrender | intimidate_character | 1 | demand-surrender |
| steady_ally | inspect_object | 1 | plain-coordination |
| offer_surrender | give_item | 1 | stale-ownership-offer |
| damage_environmental_object | open_container | 1 | unsupported-force-container |

## Named hard cases

Pass = every required card was supplied. ✓ pass, ✗ a required card was lost.

| Case | WholeRulebook | Oracle | StructuredRouting | Embedding | Embedding | CompactIndex | ActionRouting |
| --- | --- | --- | --- | --- | --- | --- | --- |
| accept-surrender | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| use-item-vs-heal-ability | ✓ | ✓ | ✓ | ✗ | ✗ | ✓ | ✓ |
| steal-vs-attack-compound | ✓ | ✓ | ✓ | ✗ | ✗ | ✓ | ✓ |
| intimidate | ✓ | ✓ | ✓ | ✗ | ✗ | ✓ | ✓ |
| threat-with-a-blow | ✓ | ✓ | ✓ | ✗ | ✗ | ✓ | ✓ |
| plain-coordination | ✓ | ✓ | ✓ | ✗ | ✗ | ✗ | ✗ |
| defend-vs-take-cover | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| damage-object-vs-attack | ✓ | ✓ | ✓ | ✗ | ✓ | ✗ | ✓ |
| leave-cover-vs-exposing-action | ✓ | ✓ | ✓ | ✓ | ✗ | ✓ | ✓ |
| demand-vs-offer | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| unsupported-force-container | ✓ | ✓ | ✓ | ✗ | ✗ | ✗ | ✓ |

