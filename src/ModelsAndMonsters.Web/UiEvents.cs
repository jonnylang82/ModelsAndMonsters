using ModelsAndMonsters.Domain;

namespace ModelsAndMonsters.Web;

/// <summary>
/// One message pushed to a live viewer over SSE. <see cref="Type"/> tells the client how to render the
/// <see cref="Payload"/> — text events append to the transcript, structured events update the character
/// cards and the combat ticker. The set is deliberately curated: the raw model requests/responses stay in
/// the file trace and are never streamed.
/// </summary>
public sealed record UiEvent(string Type, object? Payload)
{
    // Text-stream events (from the game console).
    public static UiEvent RunHeader(string runId, string scenario) => new("runHeader", new { runId, scenario });
    public static UiEvent Round(int round) => new("round", new { round });
    public static UiEvent Narration(string text) => new("narration", new { text });
    public static UiEvent PrivateObservation(string character, string text) => new("privateObservation", new { character, text });
    public static UiEvent Asks(string character, string text) => new("asks", new { character, text });
    public static UiEvent Acts(string character, string text) => new("acts", new { character, text });
    public static UiEvent Speaks(string character, string text) => new("speaks", new { character, text });
    public static UiEvent Passes(string character, string text) => new("passes", new { character, text });
    public static UiEvent Refused(string character, string text) => new("refused", new { character, text });
    public static UiEvent Notice(string text) => new("notice", new { text });
    public static UiEvent Ending(string text) => new("ending", new { text });

    // Structured events (from the trace) that drive the cards and combat view.
    public static UiEvent State(StateDto state) => new("state", state);
    public static UiEvent TurnStarted(string character) => new("turnStarted", new { character });
    public static UiEvent Attack(AttackDto attack) => new("attack", attack);

    // A character leaving active combat other than by death, so the transcript can read it distinctly.
    public static UiEvent Surrendered(string character) => new("surrendered", new { character });
    public static UiEvent Escaped(string character, string exit) => new("escaped", new { character, exit });
    public static UiEvent ExitOpened(string character, string exit) => new("exitOpened", new { character, exit });

    public static UiEvent Completed(
        string terminalCondition, string? outcome, IReadOnlyList<string> winningTeams, IReadOnlyList<string> survivors) =>
        new("completed", new { terminalCondition, outcome, winningTeams, survivors });
}

/// <summary>A character as the cards render it.</summary>
public sealed record CharacterDto(
    string Id,
    string Name,
    string Team,
    string Role,
    int Health,
    int MaxHealth,
    int Armour,
    string? Weapon,
    IReadOnlyList<string> Inventory,
    IReadOnlyList<string> Injuries,
    bool Alive,
    string Disposition);

/// <summary>
/// A room object as the object panel renders it: its name and, for a container, whether it stands open —
/// both plainly visible to anyone in the room. Contents are deliberately absent; those are private
/// knowledge, not a public property of the object.
/// </summary>
public sealed record ObjectDto(string Id, string Name, bool IsContainer, bool IsOpen);

/// <summary>An exit as the room-state panel renders it: its name and whether it stands open — both public.</summary>
public sealed record ExitDto(string Id, string Name, bool IsOpen);

/// <summary>A snapshot of every character, room object and exit, sent whenever the authoritative state advances.</summary>
public sealed record StateDto(
    int Version, IReadOnlyList<CharacterDto> Characters, IReadOnlyList<ObjectDto> Objects, IReadOnlyList<ExitDto> Exits)
{
    public static StateDto From(GameState state) => new(
        state.Version,
        [.. state.Characters.Select(c => new CharacterDto(
            c.Id, c.Name, c.Team, c.Role.ToString(), c.Health, c.MaxHealth, c.Armour,
            c.Weapon?.Name,
            [.. c.Inventory.Select(i => i.Name)],
            [.. c.Injuries.Select(i => i.Description)],
            c.IsAlive,
            c.Disposition.ToString()))],
        [.. state.Room.Objects.Select(o => new ObjectDto(
            o.Id, o.Name, o is Container, o is Container { IsOpen: true }))],
        [.. state.Room.Exits.Select(e => new ExitDto(e.Id, e.Name, e.IsOpen))]);
}

/// <summary>One resolved attack, for the combat ticker and card damage flashes.</summary>
public sealed record AttackDto(
    string Attacker,
    string Target,
    bool Hit,
    bool Glancing,
    int Damage,
    int TargetHealth,
    int TargetMaxHealth,
    bool Died);
