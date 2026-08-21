# v0.9 field notes — what the models actually did

Pulled from the run traces in `src/ModelsAndMonsters.Web/runs/20260821-*`. Everything quoted here is
verbatim from a real run; nothing is reconstructed. v0.9 added environmental cover — the Overturned Mill
Workbench, one seeded cover object with two capacity slots' worth of shelter and two points of durability —
plus `take_cover`, `leave_cover`, and deliberate `damage_environmental_object` strikes.

---

## The workbench earns its keep

The best possible outcome for a new mechanic is that it fires exactly as designed, unprompted, in the middle
of an ordinary fight. `20260821-113631Z-296012a5` delivered that at round 9: Vark had taken cover behind the
workbench three rounds earlier, Elara swung at him, and the single hit-check roll (66, against a pre-cover
hit chance of 70 and a covered chance of 50) landed in the gap between the two — a hit the cover, not the
roll, turned aside:

> **DM (public):** *"Elara swings her Iron Mace at Vark, but the blow strikes the Overturned Mill Workbench
> instead, splintering the already battered oak as it turns aside the attack. Vark remains safely behind the
> damaged cover, untouched by the impact, while Elara watches the wood give way under the force of her
> strike."*

The workbench took its one point of durability loss and kept sheltering him. No character asked what an
interception would look like; the DM narrated the physics correctly from a plain engine event.

## Vark solves cover the direct way

`20260821-103453Z-24103320`, round 11: Elara has taken shelter behind the workbench, and rather than trade
hit-chance penalties trying to land a blow through it, Vark simply destroys the furniture:

> **Vark:** *"I step forward and bring my sabre down hard on the workbench, battering it to try to knock
> Elara out of her cover."*
>
> **DM (public):** *"Vark's Notched Sabre cleaves through the overturned workbench, shattering the heavy oak
> and iron braces into wreckage. The destruction of the barrier leaves Elara standing fully exposed in the
> flooded cellar, no longer shielded from the room's other combatants."*

This is `damage_environmental_object` used exactly as the design intended — a deliberate, no-roll strike
against a stationary target — and it is the clean tactical inverse of the interception above: instead of
gambling on a hit-check, Vark spent a full turn removing the shelter itself. Elara had asked Vark privately,
one round earlier, whether she was "still conscious and able to move, or is she badly wounded and helpless
behind the workbench" — Vark, or rather the model playing Vark, answered that question with a sabre instead
of words.

## A goblin who never leaves the furniture

`20260821-110442Z-1667ed2a` — the same run that produced the possessor-refusal bug below — is also the
heaviest user of cover in the whole session: 69 references across the transcript. Both goblins cycled in and
out of the workbench all fight, entering to recover and breaking cover only to swing:

> Round 2.8 — Skrit takes cover. Round 3.12 — Skrit breaks cover to strike. Round 5.18 — Elara takes cover.
> Round 6.22 — Elara breaks cover to strike. Round 8.30 — Elara takes cover again. Round 9.34 — Elara breaks
> cover to strike. Round 9.36 — Skrit takes cover. Round 10.40 — Skrit breaks cover to strike.

Textbook peekaboo combat, entirely emergent — nothing in the prompts describes a "pop out and swing" tactic.
The workbench never actually took damage in this run despite eight occupancy changes; the interceptions and
destructions above happened in *different* runs of the same scenario, which is a reasonable spread for a
mechanic gated behind one hit-check roll landing in a specific band.

## The stalemate that named its own gap

The run the session opened around, `20260821-090056Z-b2fe2e94`, ended in a genuine stalemate — nobody ever
took cover, because the opening scene never mentioned the workbench was there. That observation (the user's,
not mine) is what drove the narration-prompt fix described in `v0_9_issues.md`. The same run, though, has a
clean example of the surrender machinery working correctly under pressure: Elara accepted Vark's offer,
took the purse, and left through the Cellar Stair Door — the good case that the accept-surrender fix (also
below) was careful not to break.

## Skrit's three-round fixation

`20260821-110442Z-1667ed2a` again: after Rowan is downed to 3/14 health by a glancing blow, Skrit spends his
entire next turn — three full attempts, back to back — trying to lift a purse Rowan no longer has:

> *"I lunge forward and try to snatch the Small Purse of Gold Coins from Rowan's belt."*
> *"I try to snatch the Small Purse of Gold Coins from Rowan's belt."*
> *"I try to snatch the Small Purse of Gold Coins from Rowan's belt."*

Rowan gave that purse to Elara four rounds earlier, in public, and Skrit had heard it happen. The engine
refused all three attempts correctly — but the refusal text told Skrit the wrong thing (see `v0_9_issues.md`
for the fix), so nothing in what he heard back ever corrected his belief. Three turns burned on a purse that
was never there, purely because the world lied about *why* he was wrong.

## An elaborate hallucination, four times running

`20260821-101224Z-6247a3c7`, round 12, Rowan already fallen: Elara is offered a real, standing surrender —
gold and her life for walking away — and tries to accept it four separate times, inventing more elaborate
phrasing each attempt:

> *"I take the purse, Vark. You have your life and the case; I take my life. Skrit, I leave you for now!"*
> *"I accept your terms, Vark. I take the purse and leave you and the case. Skrit, I leave you!"*
> *"I accept Vark's offer of surrender, taking the captain's purse and leaving him alive, then I run through
> the open Cellar Stair Door to escape."*
> *"I take the captain's purse from Vark, telling him I accept his terms, and I leave him alive, then I run
> through the Cellar Stair Door."*

Between attempts she even asked the DM directly whether the offer was real (*"is Vark's offer of the
captain's purse and my life a valid term of surrender that I can accept?"*) and was told, correctly, that it
was — she just couldn't act on it, because the machinery answering her intent was inventing an offer id
("offer-1") that never matched a real one. Four attempts, one clarifying question, and the encounter still
hit its round limit unresolved. The fix for the underlying bug is in `v0_9_issues.md`.

## Skrit still doesn't know his own name

A callback to a v0.8 note (*"Skrit does not recognise his own purse"*) — `20260821-095322Z-45a78d65` shows
the same pattern surviving into v0.9, this time about a *person*:

> **Skrit says (public):** *"Elara, I got it! Don't swing at Vark!"*
>
> **Skrit:** *"I snatch the Small Purse of Gold Coins from the floor where Vark was just telling me not to
> let it go, clutching it to my chest with both hands."*

Skrit is talking about his own captain's purse in the third person mid-lunge, cheering himself on as though
narrating someone else's heist. Harmless, and arguably good character voice for a small-time grunt — but a
reminder that first-person consistency is still entirely up to the model, not enforced anywhere.
