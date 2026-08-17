using System.Collections.Immutable;

namespace ModelsAndMonsters.Domain;

/// <summary>
/// The single v0.1 room. There is no movement, no coordinates and no line of sight: everyone in the
/// room can interact with everyone else.
/// </summary>
/// <param name="Features">
/// Purely descriptive scenery. Nothing in the engine can act on features yet — they exist so the DM
/// has something to describe, and so characters have plausible reasons to attempt actions the engine
/// does not support (which is useful experimental data).
/// </param>
public sealed record Room(
    string Id,
    string Name,
    string Description,
    ImmutableArray<string> Features);
