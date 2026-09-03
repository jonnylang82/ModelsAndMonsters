# Council rules adapter

This fork uses Spencer Clark's bounded engine, not a complete D&D simulator. Character sheets supply
identity and selected numbers; a prepared spell or equipment entry is **not executable** until it has
an engine handler, a rule card, and tests. Models propose actions; the engine alone rolls and changes state.

## Source policy

Jon supplied local D&D Beyond PDF exports under `The-Council/knwlg/DnD`. The older **Basic Rules**
Spellcasting, Spells, Combat and Equipment exports govern the mechanics below. The newer DM exports
inform encounter design only. Do not silently mix newer spell versions with older spells.
Page numbers below are PDF pages, not printed book page numbers. These are original implementation
notes, not reproduced rulebook text; the source PDFs are not redistributed in this repository.

| Local PDF title (D&D Beyond export) | Pages consulted | Use in this implementation |
| --- | --- | --- |
| Basic Rules … Spellcasting | 1–4 | Spell resources, targets, concentration and action timing; departures listed below. |
| Basic Rules … Spells | 46, 51–52, 65, 73, 96–97 | Divine Favor, Faerie Fire, Healing Word, Mage Hand review, Sleep. |
| Basic Rules … Combat | 1 | Six-second round as the basis for a ten-round, one-minute spell duration. |
| Basic Rules … Equipment | 8, 10 | Caltrops require entry into a ground area and movement effects; deferred. |
| Basic Rules … Building Combat Encounters | 1 | Difficulty guidance; not a claim that this homebrew encounter has a D&D CR rating. |
| DM’s Toolbox … Basic Rules | 1–2 | Encounter variety and difficulty as design considerations, not imported XP math. |
| The Basics … Basic Rules | 3 | Conversation, exploration and combat are different modes; meeting wardens is not an order to attack. |
| Magic Items … Basic Rules | 5–6 | Explicit consumption/charges and spell effects; possessing an item does not invent an effect. |

## First supported sheet spells

| Character | Ability | Authoritative effect |
| --- | --- | --- |
| Nema | Sleep | Roll 5d8. One chosen opponent sleeps if its current health fits that pool and it is not sleep-immune. No damage or surrender. |
| Pipix | Healing Word | Heal self or one wounded living ally by 1d4 + casting modifier (minimum 1), capped at maximum health; no constructs/undead. |
| Pipix | Faerie Fire | One opponent makes a d20 Dexterity save against the caster's spell DC. Failure applies an outline giving attacks +15 hit chance. No fire damage. |
| Scott | Divine Favor | While concentrating, landed weapon hits add 1d4 radiant damage after armour; Firebolt gets no bonus. Casting does not also attack. |
| Everyone | Wake Sleeper | Spend a turn shaking a sleeping ally awake, without damage or healing. Reaching out breaks the waker's cover. |

Sleep ends after ten rounds, on actual damage, or on Wake Sleeper. It stops the target's actions and
concentration, and ends their active guarding relationship. It does not release Deacon, remove the
sleeping warden from the rescue condition, or end when its caster dies. Sleep immunity is explicit
scenario data (Pipix's elven immunity and the Iron Curator's immunity are configured).

Faerie Fire and Divine Favor require concentration for up to ten rounds. A new concentration spell
ends the previous one, even if the new target resists. Damage triggers a Constitution save against
the greater of 10 or half the damage, rounded down; failure ends concentration. Death, surrender,
escape, sleep and stun also end it. Every spell/save/damage die is recorded in the existing RNG trace.

## Deliberate adaptations and limits

- **Charges, not full spell slots:** each new spell has one independent encounter charge. Invalid
  targets cost nothing; resistance or an insufficient Sleep roll spends the charge. No shared slot
  pool, upcasting, preparation changes, rest recovery or character-level eligibility checks yet.
- **One action per turn:** Healing Word and Divine Favor take the whole turn here, despite their
  D&D bonus-action timing. No action-plus-bonus-action, reactions or interrupted casting yet.
- **Single target:** Sleep does not allocate an area pool among creatures; Faerie Fire has no cube,
  friendly fire, range geometry, invisibility or line-of-sight calculation. These are explicit adapters.
- **Existing combat math:** percent hit chance, fixed weapon damage and armour reduction remain.
  The +15 outline/sleep attack benefit substitutes for advantage. Existing quality/critical rules
  remain; Divine Favor adds one d4 after weapon quality, rather than full D&D critical-dice rules.
- **Sleep is bounded incapacity:** no actions or guarding, but full unconscious-condition mechanics
  and perception filtering are not implemented. Existing public event memory is unchanged.
- **No death saves/revival:** zero health still means death under the original engine; healing cannot
  revive the dead or target detained/escaped/surrendered characters.
- **Resources are explicit:** spells cannot be created by narration or by merely listing them in a
  character-sheet JSON file. Other prepared spells remain unsupported.

## Items and existing abilities

Healing consumables can now be administered to a living, active, present ally. The item is removed from
the **user's** inventory; only the recipient heals. Full-health use is refused without consuming it.
Holding a bandage ready is not use. Recharging focus items remain self-only and are not consumed.

Emma's Firebolt, Mirabel's Dirty Strike, Scott's Guard Ally and Nema's Rally remain supported. The
spellbook/focus recharge is retained demo homebrew, **not** a D&D rule that an ordinary spellcasting
focus restores slots. Firebolt is still the demo's charged armour-ignoring attack, not a rules-complete
unlimited Fire Bolt cantrip. Tools, caltrops and inert items do not get invented mechanical effects.

Caltrops need movement/ground hazards first. Mage Hand needs remote object manipulation and limits.
Entangle needs restraints, saves and escape actions. Those, the remaining sheet spells, full equipment
effects and a second room are separate follow-ups, not features this patch claims to provide.

## Play and diagnostics

The default run cap is **20 rounds** (not extra actions within a round). More time does not fix loops
by itself. The character prompt no longer calls everyone outside the party an enemy to aim at.
Questions remain bounded to one per turn; they need not wait for an action to fail.

When the router selects self-defense, it also supplies the Guard Ally rule so the DM can distinguish
protecting a named companion from protecting oneself. No automatic conversion based on keywords.
For zero-damage attacks, the shared narration uses the engine's result rather than a potentially
invented wound. A zero-damage critical no longer creates injury-related morale changes. Round recaps
are still model-generated and audience-only, so their fidelity remains something to watch.

The 8192-token model context limit is unchanged. New spell selection cards are concise; their detailed
effects are supplied after selection. The fallback still sends every card, subject to a tested context
budget, rather than silently dropping rules. The card-count ceiling is 40; this is not a larger token window.

Run the full test suite, then start a **new** live run. Watch whether Scott actually guards allies,
Nema/Pipix choose non-weapon options, Emma recharges when appropriate, attacks bounce honestly off
armour, and the party can rescue Deacon within the cap. A unit test proves mechanics, not model strategy.
