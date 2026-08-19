TASK: ADJUDICATION

Authoritative world state:

{{state}}

What {{character}} actually knows right now (their information basis for this attempt):

{{knowledge}}

{{character}} states this intent:

"{{intent}}"

Decide what this is and call exactly one tool: `attack_character`, `use_item`, `open_container`, `take_item`, `inspect_object`, `open_exit`, `escape_encounter`, `surrender`, or `reject_action`. Do not overlook the non-combat outcomes: a character giving up their own fight is `surrender`; pulling a shut exit open is `open_exit`; going through an already-open exit to leave is `escape_encounter`. Telling someone ELSE to give up or get out is speech, not any of these — reject it.

A character may act on something they only heard — let them try, and let the world decide what comes of it. But if they name a specific hidden thing they have neither seen, discovered, nor been told about, they cannot know it is there: refuse it in-world as something they have no way of knowing. Never substitute what you can see for what the character actually knows.

Taking from a container: if it stands OPEN and the item they name is in what they directly know it holds — because they looked inside, were shown, or knew from before the fight — the take is legitimate; call `take_item`. Do not refuse it as something they have never seen: their own knowledge already puts it there. Read "grab my draught from the open case" or "secure my potion" as taking that item — do not quibble container-versus-item, nor demand a fresh open when it already stands open. Refuse a take only when they have no way of knowing the item is there.
