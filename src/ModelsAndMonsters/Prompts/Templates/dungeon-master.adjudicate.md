TASK: ADJUDICATION

Authoritative world state:

{{state}}

What {{character}} actually knows right now (their information basis for this attempt):

{{knowledge}}

{{character}} states this intent:

"{{intent}}"

RULEBOOK GUIDANCE (already looked up for you — bind it to the state above; do not repeat or mention it to the character):

{{guidance}}

Bind the guidance to the authoritative state and call **exactly one** tool from the ones you have been given. If the guidance offers a supported action and it fits the state and {{character}}'s knowledge, fill that action's bindings with exact snapshot names and call it. If the guidance is unsupported, or nothing it offers actually fits, call `reject_action` with the right category and a short in-world reason.

When an intent bundles a deed with a hoped-for result, or strings two deeds together, resolve **the primary physical deed** — the first thing the character's body actually does — and nothing else. "I cut at his arm to make him drop the purse" is a blow; whether anything falls is not yours to grant and simply does not happen. "I strike him and grab the vial" is one blow, and no grab. Call the single action for that deed. Do **not** refuse a whole intent because part of it reaches past what the world can do, and do not tell the character their attempt was two things or that it cannot be combined — just resolve the deed they led with. This holds when the deed they led with is **not** a blow: "I snatch the mace from the floor and swing it" is the snatch — call `take_item`, never an attack with a weapon the character is not yet holding. It holds equally when the lead deed is a **move or an opening**: "I shove the door open and step through, fleeing" leads with the open — call `open_exit`, not an escape through a door still shut; "I dart behind the workbench and grab the mace" leads with taking cover — call `take_cover`, not the grab the new spot would set up. Resolve the opener the character led with and let the follow-through wait for a later turn; never refuse the whole intent because its second deed needs the first to have landed.

A **named ability the character actually has IS the primary deed**, never a hoped-for result to be stripped away. When the intent invokes one of the acting character's own listed abilities — a Dirty Strike, a Stunning Blow, a Firebolt — and `use_ability` is among the tools you were given, call `use_ability` and bind that exact ability, **even when a weapon blow is described in the same breath** ("I bring my maul down on his head to stun him with a Stunning Blow", "I slash his arm and kick his knee with a Dirty Strike"). Some abilities simply ARE a weapon blow with an extra effect the character has trained: the blow is how the ability is delivered, not a separate attack that discards it, and the effect it names is real, not the kind of hoped-for result the rule above sets aside. Fall back to plain `attack_character` only when the intent names no ability of theirs, or `use_ability` was not offered to you.

When a character names a specific item to take, take **that** item, from wherever it actually lies — the floor, an open container, or a fallen body — even when they point at the wrong place ("the purse on the floor" when it is really on a body). Never grab a *different* item just because it happens to sit where they pointed; if the item they named is nowhere they can reach, refuse and say where it is rather than substituting something else.

A character may act on something they only heard — let them try, and let the world decide what comes of it. But if they name a specific hidden thing they have neither seen, discovered, nor been told about, they cannot know it is there: refuse it in-world as something they have no way of knowing. Never substitute what you can see for what the character actually knows.
