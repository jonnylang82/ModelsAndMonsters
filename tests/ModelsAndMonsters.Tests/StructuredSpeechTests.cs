using Microsoft.Extensions.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// Speech declared by the character in its own call, rather than inferred from its prose.
/// </summary>
/// <remarks>
/// The old arrangement hunted for quoted text near a speech verb. It could not be made right: it needed a
/// verb list that grew every run, it read <c>the blade called "Goblin's Bite"</c> as somebody speaking, and
/// it was blind to speech phrased without a verb at all. Declaring speech as a field removes the guessing —
/// what the character puts in <c>utterances</c> is spoken, and nothing else is — and costs no extra model
/// call, because the words arrive in the same reply as the action.
/// </remarks>
public sealed class StructuredSpeechTests
{
    private static ChatResponse ActAndSay(string id, string intent, params string[] utterances) =>
        ScriptedChatClient.Call(id, CharacterTools.TakeActionName,
            (CharacterTools.IntentParameter, intent),
            (CharacterTools.UtterancesParameter, utterances));

    private static OrchestrationHarness HarnessFor(
        ScriptedChatClient hero, ScriptedChatClient? dm = null, HarnessOptions? limits = null) =>
        new(dm ?? new ScriptedChatClient(
                ScriptedChatClient.Call("dm-1", DungeonMasterTools.AttackCharacterName,
                    ("attacker", "Aric"), ("target", "Grik"), ("weapon", "Iron Sword")),
                ScriptedChatClient.Text("Aric's blade bites deep.")),
            hero,
            new ScriptedChatClient(),
            limits);

    [Fact]
    public async Task Declared_speech_is_heard_without_any_speech_verb_anywhere()
    {
        // No "say", no "shout", no quotation marks in the intent — nothing a detector could have keyed on.
        // The field is the declaration, so the words are spoken because the character said they were.
        var harness = HarnessFor(new ScriptedChatClient(
            ActAndSay("h-1", "I bring my sword down on Grik's shoulder.", "Elara, get behind me.")));

        await harness.RunHeroTurn();

        var speech = Assert.Single(harness.Sink.Payloads<CharacterSpeechPayload>(TraceEventType.CharacterSpeech));
        Assert.Equal("Aric", speech.SpeakerName);
        Assert.Equal("Elara, get behind me.", speech.Message);

        // And it really reached the room, not merely the trace.
        Assert.Contains(harness.Console.Lines, l => l == "says:Aric:Elara, get behind me.");
        Assert.Contains(TestWorld.MonsterId, speech.Recipients);
    }

    [Fact]
    public async Task Declared_speech_is_delivered_before_the_action_it_rides_on()
    {
        // A warning is worth nothing if it lands after the blow it warns about.
        var harness = HarnessFor(new ScriptedChatClient(
            ActAndSay("h-1", "I bring my sword down on Grik's shoulder.", "Elara, get behind me.")));

        await harness.RunHeroTurn();

        var saidAt = harness.Console.Lines.FindIndex(l => l.StartsWith("says:", StringComparison.Ordinal));
        var actedAt = harness.Console.Lines.FindIndex(l => l.StartsWith("acts:", StringComparison.Ordinal));
        Assert.True(saidAt >= 0 && actedAt >= 0);
        Assert.True(saidAt < actedAt, "Declared speech must reach the room before the action it accompanies.");
    }

    [Fact]
    public async Task Quoted_text_in_the_intent_is_never_spoken()
    {
        // "the blade called 'Goblin's Bite'" is a name, not a line of dialogue. With speech declared rather
        // than detected, this needs no rule of its own: nothing was declared, so nothing is said.
        var harness = HarnessFor(new ScriptedChatClient(
            ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName,
                (CharacterTools.IntentParameter,
                    "I lift the blade called \"Goblin's Bite\" from the case and test its weight."))));

        await harness.RunHeroTurn();

        Assert.Empty(harness.Sink.OfType(TraceEventType.CharacterSpeech));
        Assert.Empty(harness.Sink.OfType(TraceEventType.UnstructuredSpeechAttempt));
        Assert.DoesNotContain(harness.Console.Lines, l => l.StartsWith("says:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Saying_nothing_while_reading_an_inscription_produces_no_utterance()
    {
        // "I say nothing and inspect the inscription…" contains a speech verb AND a quotation, which is
        // exactly what the old heuristic keyed on. Declared speech makes the sentence irrelevant.
        var harness = HarnessFor(new ScriptedChatClient(
            ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName,
                (CharacterTools.IntentParameter,
                    "I say nothing and inspect the inscription \"Goblin's Bite\" on the lid."))));

        await harness.RunHeroTurn();

        Assert.Empty(harness.Sink.OfType(TraceEventType.CharacterSpeech));
        Assert.Empty(harness.Sink.OfType(TraceEventType.UnstructuredSpeechAttempt));
    }

    [Fact]
    public async Task An_empty_utterances_field_speaks_nothing()
    {
        var harness = HarnessFor(new ScriptedChatClient(
            ActAndSay("h-1", "I bring my sword down on Grik's shoulder.")));

        await harness.RunHeroTurn();

        Assert.Empty(harness.Sink.OfType(TraceEventType.CharacterSpeech));
    }

    [Fact]
    public async Task The_structured_path_costs_no_extra_model_call()
    {
        // The whole point: the words arrive in the same reply as the action. Speaking and acting together
        // must take exactly the calls that acting alone takes — one decision from the character, and the
        // Dungeon Master's adjudication plus its narration. No parser, no nudge, no round trip for speech.
        var speaking = HarnessFor(new ScriptedChatClient(
            ActAndSay("h-1", "I bring my sword down on Grik's shoulder.", "Elara, get behind me.")));
        await speaking.RunHeroTurn();

        var silent = HarnessFor(new ScriptedChatClient(
            ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName,
                (CharacterTools.IntentParameter, "I bring my sword down on Grik's shoulder."))));
        await silent.RunHeroTurn();

        Assert.Equal(1, speaking.HeroClient.CallCount);
        Assert.Equal(silent.HeroClient.CallCount, speaking.HeroClient.CallCount);
        Assert.Equal(silent.DungeonMasterClient.CallCount, speaking.DungeonMasterClient.CallCount);

        // And it did speak — the equality above is not passing because nothing happened.
        Assert.Single(speaking.Sink.OfType(TraceEventType.CharacterSpeech));
    }

    [Fact]
    public async Task Several_declared_lines_are_one_breath_and_all_of_them_are_heard()
    {
        // Two short sentences are how anybody talks, and they are one speaking turn — not two. The harness
        // used to deliver the first and silently drop the rest, which a live run hit ten times in nine
        // rounds. Joining keeps the once-per-turn rule exactly as it was without losing half a reply.
        var harness = HarnessFor(new ScriptedChatClient(
            ActAndSay("h-1", "I bring my sword down on Grik's shoulder.",
                "Elara, get behind me.", "And keep clear of the captain!")));

        await harness.RunHeroTurn();

        var speech = Assert.Single(harness.Sink.Payloads<CharacterSpeechPayload>(TraceEventType.CharacterSpeech));
        Assert.Equal("Elara, get behind me. And keep clear of the captain!", speech.Message);

        // Nothing was discarded, so nothing is reported as discarded.
        Assert.DoesNotContain(harness.Sink.Payloads<HarnessLimitPayload>(TraceEventType.HarnessLimitReached),
            p => p.Limit == nameof(HarnessOptions.MaxSpeechActsPerTurn));
    }

    [Fact]
    public async Task A_second_separate_speaking_beyond_the_allowance_is_still_refused()
    {
        // Joining one reply's lines is not licence to exceed the allowance. Pinned to one so the test covers
        // the rule rather than tracking the default.
        var harness = HarnessFor(new ScriptedChatClient(
            ScriptedChatClient.Calls(
                ScriptedChatClient.CallContent("h-1", CharacterTools.SayName,
                    (CharacterTools.MessageParameter, "Elara, get behind me.")),
                ScriptedChatClient.CallContent("h-2", CharacterTools.SayName,
                    (CharacterTools.MessageParameter, "And keep clear of the captain!"))),
            ScriptedChatClient.Call("h-3", CharacterTools.EndTurnName,
                (CharacterTools.ReasonParameter, "Said my piece."))),
            new ScriptedChatClient(ScriptedChatClient.Text("Aric holds his ground.")),
            new HarnessOptions { MaxSpeechActsPerTurn = 1 });

        await harness.RunHeroTurn();

        Assert.Single(harness.Sink.OfType(TraceEventType.CharacterSpeech));
        Assert.Contains(harness.Sink.Payloads<SpeechNotHeardPayload>(TraceEventType.SpeechNotHeard),
            p => p.Unheard == "And keep clear of the captain!");
    }

    [Fact]
    public async Task Speech_declared_while_ending_a_turn_is_still_heard()
    {
        // A character that has nothing left to try may still have something to say.
        var harness = HarnessFor(new ScriptedChatClient(
            ScriptedChatClient.Call("h-1", CharacterTools.EndTurnName,
                (CharacterTools.ReasonParameter, "I have no strength left."),
                (CharacterTools.UtterancesParameter, new[] { "Hold the line without me." }))),
            new ScriptedChatClient(ScriptedChatClient.Text("Aric sags against the wall.")));

        await harness.RunHeroTurn();

        var speech = Assert.Single(harness.Sink.Payloads<CharacterSpeechPayload>(TraceEventType.CharacterSpeech));
        Assert.Equal("Hold the line without me.", speech.Message);
    }

    [Fact]
    public async Task A_serialised_array_of_speech_is_parsed_rather_than_spoken_verbatim()
    {
        // Verbatim from a live run: the model sent its speech as the literal text ["Take it! You may go!"']
        // and the harness said exactly that aloud, brackets and stray quote included, and wrote it into the
        // knowledge ledger. Providers serialise arrays sometimes; the words still have to arrive as words.
        var harness = HarnessFor(new ScriptedChatClient(
            ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName,
                (CharacterTools.IntentParameter, "I bring my sword down on Grik's shoulder."),
                (CharacterTools.UtterancesParameter, "[\"Take it! You may go!\"']"))));

        await harness.RunHeroTurn();

        var speech = Assert.Single(harness.Sink.Payloads<CharacterSpeechPayload>(TraceEventType.CharacterSpeech));
        Assert.Equal("Take it! You may go!", speech.Message);
    }

    [Theory]
    // Verbatim from one live run, at the two places the previous guard missed. Qwen's tool-call wire format
    // is XML, which has no array type, so an array-typed parameter always arrives as text that merely looks
    // like an array — and whether it is well-formed is down to the model.
    //
    // Closed with a CJK lenticular bracket instead of ']', which a quantised model reaches for:
    [InlineData("[\"Skrit, hold your line!\"\u3011", "Skrit, hold your line!")]
    // Never closed at all:
    [InlineData("[\"Vark, take this!\"", "Vark, take this!")]
    // Full-width closer, the same class of substitution:
    [InlineData("[\"Hold fast!\"\uFF3D", "Hold fast!")]
    public async Task An_array_that_never_closes_properly_is_still_parsed_rather_than_spoken(
        string declared, string expected)
    {
        // The old guard required a literal ']' to be present, so both live cases fell straight through and
        // were spoken to the room with the scaffolding attached. Speech is delivered verbatim by design and
        // lands in every listener's history, so one malformed argument in round 5 was still sitting in the
        // Dungeon Master's context in round 11.
        var harness = HarnessFor(new ScriptedChatClient(
            ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName,
                (CharacterTools.IntentParameter, "I bring my sword down on Grik's shoulder."),
                (CharacterTools.UtterancesParameter, declared))));

        await harness.RunHeroTurn();

        var speech = Assert.Single(harness.Sink.Payloads<CharacterSpeechPayload>(TraceEventType.CharacterSpeech));
        Assert.Equal(expected, speech.Message);
    }

    [Fact]
    public async Task Array_scaffolding_with_nothing_quoted_inside_it_is_stripped_not_spoken()
    {
        // The residual case: an opening bracket and no quoted span to recover. Whatever else happens, the
        // bracket must not end up in a character's mouth.
        var harness = HarnessFor(new ScriptedChatClient(
            ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName,
                (CharacterTools.IntentParameter, "I bring my sword down on Grik's shoulder."),
                (CharacterTools.UtterancesParameter, "[Hold the line"))));

        await harness.RunHeroTurn();

        var speech = Assert.Single(harness.Sink.Payloads<CharacterSpeechPayload>(TraceEventType.CharacterSpeech));
        Assert.Equal("Hold the line", speech.Message);
        Assert.DoesNotContain('[', speech.Message);
    }

    [Fact]
    public async Task A_comma_inside_a_spoken_line_is_punctuation_not_a_separator()
    {
        // A bare string where a list was expected must stay ONE utterance. Splitting on commas would turn
        // "Rowan, take the flank!" into a character saying the word "Rowan" and then an orphaned command.
        var harness = HarnessFor(new ScriptedChatClient(
            ScriptedChatClient.Call("h-1", CharacterTools.TakeActionName,
                (CharacterTools.IntentParameter, "I bring my sword down on Grik's shoulder."),
                (CharacterTools.UtterancesParameter, "Rowan, take the flank, and mind the captain!"))));

        await harness.RunHeroTurn();

        var speech = Assert.Single(harness.Sink.Payloads<CharacterSpeechPayload>(TraceEventType.CharacterSpeech));
        Assert.Equal("Rowan, take the flank, and mind the captain!", speech.Message);
    }

    [Fact]
    public async Task The_legacy_fallback_still_fires_for_a_bare_prose_reply_and_traces_itself()
    {
        // The one path that still infers speech: a reply carrying no tool call whatsoever. It must be
        // visible in the trace whenever it is used, so a run can be audited for how often the harness is
        // guessing rather than being told.
        var harness = new OrchestrationHarness(
            new ScriptedChatClient(ScriptedChatClient.Text("Aric holds his ground.")),
            new ScriptedChatClient(
                ScriptedChatClient.Text("I shout: \"Grik, back off or I'll cut you down!\""),
                ScriptedChatClient.Call("h-2", CharacterTools.EndTurnName,
                    (CharacterTools.ReasonParameter, "Said my piece."))),
            new ScriptedChatClient(),
            new HarnessOptions
            {
                RecoverTextToolCalls = true,
                MaxQuestionsPerTurn = 2,
                MaxActionAttemptsPerTurn = 3,
                MaxModelCallsPerTurn = 8
            });

        await harness.RunHeroTurn();

        var attempt = Assert.Single(
            harness.Sink.Payloads<UnstructuredSpeechAttemptPayload>(TraceEventType.UnstructuredSpeechAttempt));
        Assert.Contains("back off", attempt.AttemptedText, StringComparison.Ordinal);

        // Still never delivered as speech: an inferred line is recorded and nudged, never put in the
        // character's mouth and broadcast.
        Assert.Empty(harness.Sink.OfType(TraceEventType.CharacterSpeech));
    }
}
