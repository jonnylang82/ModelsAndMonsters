# Rulebook selection — measured comparison

Rulebook: **21** cards, **rulebook-24bb318aa8**.
Corpus: **33** labelled cases across **10** families.

| Strategy | Required-card recall | Cases with every required card | Decision agreement | Wrongly supported | Wrongly unsupported | Avg cards | Avg resolver tokens | Avg selection tokens | Model calls | Fallbacks |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| WholeRulebook (baseline, shipped) | 100.0% | 33/33 | 33/33 (100%) | 0 | 0 | 21.0 | 5260 | 0 | 1.0 | 0/33 |
| Oracle (answer key — the ceiling, not a strategy) | 100.0% | 33/33 | 33/33 (100%) | 0 | 0 | 4.3 | 1913 | 0 | 1.0 | 0/33 |
| StructuredRouting (no declared family available) | 100.0% | 33/33 | 33/33 (100%) | 0 | 0 | 21.0 | 5260 | 0 | 1.0 | 33/33 |
| Embedding (offline trigram prototype, top-3) | 54.0% | 15/33 | 18/33 (55%) | 0 | 15 | 6.8 | 2699 | 0 | 1.0 | 0/33 |
| Embedding (Ollama:nomic-embed-text, top-3) | 90.9% | 28/33 | 29/33 (88%) | 0 | 4 | 7.2 | 2458 | 0 | 1.0 | 0/33 |
| CompactIndex (Ollama:qwen3.5:9b) | 97.0% | 31/33 | 33/33 (100%) | 0 | 0 | 5.7 | 2194 | 904 | 2.0 | 0/33 |


## Reduction against the baseline

| Strategy | Avg total input tokens | Reduction |
| --- | --- | --- |
| WholeRulebook (baseline, shipped) | 5260 | 0.0% |
| Oracle (answer key — the ceiling, not a strategy) | 1913 | 63.6% |
| StructuredRouting (no declared family available) | 5260 | 0.0% |
| Embedding (offline trigram prototype, top-3) | 2699 | 48.7% |
| Embedding (Ollama:nomic-embed-text, top-3) | 2458 | 53.3% |
| CompactIndex (Ollama:qwen3.5:9b) | 3098 | 41.1% |

CompactIndex ran against **Ollama:qwen3.5:9b** (temperature 0.1), one selection call per case, averaging **1187 ms** for the selection step.

## Cases that lost a required rule

- **Embedding (offline trigram prototype, top-3)**: attack-plain, attack-vs-dirty-strike, ability-guard, use-item-vs-heal-ability, inspect, open-and-take-compound, loot-a-body, drop, steal, steal-vs-attack-compound, open-and-flee-compound, intimidate, threat-with-a-blow, steady-ally, plain-coordination, stale-ownership-steal, unsupported-disarm, ambiguous-reach
- **Embedding (Ollama:nomic-embed-text, top-3)**: use-item-vs-heal-ability, steal-vs-attack-compound, intimidate, threat-with-a-blow, plain-coordination
- **CompactIndex (Ollama:qwen3.5:9b)**: accept-surrender, plain-coordination

## Corpus clarity distribution

Evidence for the on-demand strategy: how often an intent is clear-cut enough that a Dungeon Master holding only lean core rules would not need to look anything up.

- Clear: **15** (45%)
- Ambiguous: **13** (39%)
- Unsupported: **5** (15%)

