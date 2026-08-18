namespace ModelsAndMonsters.Orchestration;

/// <summary>What kind of public-channel entry this is: Dungeon Master narration, or a character speaking.</summary>
public enum PublicChannelKind
{
    Narration,
    Speech
}

/// <summary>One entry on the public channel — narration or speech — and who has already heard it.</summary>
public sealed class NarrationEntry
{
    private readonly HashSet<string> _deliveredTo = new(StringComparer.OrdinalIgnoreCase);

    public NarrationEntry(int id, string purpose, string text, PublicChannelKind kind = PublicChannelKind.Narration, string? speakerId = null)
    {
        Id = id;
        Purpose = purpose;
        Text = text;
        Kind = kind;
        SpeakerId = speakerId;
    }

    public int Id { get; }

    public string Purpose { get; }

    /// <summary>The text delivered into a recipient's context, already carrying speaker attribution for speech.</summary>
    public string Text { get; }

    public PublicChannelKind Kind { get; }

    /// <summary>For a speech entry, the id of the character who spoke. Null for narration.</summary>
    public string? SpeakerId { get; }

    public IReadOnlyCollection<string> DeliveredTo => _deliveredTo;

    public bool HasBeenDeliveredTo(string characterId) => _deliveredTo.Contains(characterId);

    public void MarkDeliveredTo(string characterId) => _deliveredTo.Add(characterId);
}

/// <summary>
/// The public channel: narration every character in the room may hear.
/// </summary>
/// <remarks>
/// This exists so that what a character learns is an explicit harness decision rather than a side
/// effect of shared state. Private question-and-answer exchanges never pass through here, so one
/// character cannot learn something merely because another character asked about it.
/// </remarks>
public sealed class NarrationLog
{
    private readonly List<NarrationEntry> _entries = [];

    public IReadOnlyList<NarrationEntry> Entries => _entries;

    public NarrationEntry Record(string purpose, string text)
    {
        var entry = new NarrationEntry(_entries.Count + 1, purpose, text);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>
    /// Records a character speaking on the public channel. The stored text already carries the speaker's
    /// attribution ("Rowan says: …"), so it delivers to other characters through exactly the same bounded
    /// path as narration — no separate delivery mechanism, and no DM model call to paraphrase it.
    /// </summary>
    public NarrationEntry RecordSpeech(string speakerId, string attributedText)
    {
        var entry = new NarrationEntry(_entries.Count + 1, "speech", attributedText, PublicChannelKind.Speech, speakerId);
        _entries.Add(entry);
        return entry;
    }

    /// <summary>
    /// The speech this character has heard: public utterances by <em>someone else</em> that have already
    /// been delivered to it. This is the raw material of hearsay — things others said, which are never the
    /// same as something the character verified for itself, and are surfaced to the Dungeon Master as
    /// reported speech rather than as authoritative knowledge.
    /// </summary>
    public IReadOnlyList<NarrationEntry> SpeechHeardBy(string characterId) =>
    [
        .. _entries.Where(e => e.Kind == PublicChannelKind.Speech
            && !string.Equals(e.SpeakerId, characterId, StringComparison.OrdinalIgnoreCase)
            && e.HasBeenDeliveredTo(characterId))
    ];

    /// <summary>Returns everything this character has not yet heard, and marks it as delivered.</summary>
    public IReadOnlyList<NarrationEntry> TakeUndelivered(string characterId)
    {
        var pending = _entries.Where(e => !e.HasBeenDeliveredTo(characterId)).ToList();
        foreach (var entry in pending)
        {
            entry.MarkDeliveredTo(characterId);
        }

        return pending;
    }
}
