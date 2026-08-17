namespace ModelsAndMonsters.Presentation;

/// <summary>
/// The human-readable view of the game.
/// </summary>
/// <remarks>
/// Deliberately narrow. Prompts, message histories, tool arguments, state snapshots and trace events
/// belong in the run files, not on screen; there is no method here that could print them.
/// </remarks>
public interface IGameConsole
{
    void RunHeader(string runId, string scenarioName, string outputDirectory);

    void RoundHeader(int round);

    void DungeonMaster(string text);

    void CharacterAsks(string characterName, string question);

    void CharacterActs(string characterName, string intent);

    /// <summary>A character choosing to do nothing this turn.</summary>
    void CharacterPasses(string characterName, string reason);

    /// <summary>The Dungeon Master telling a character an attempt did not happen.</summary>
    void CharacterRefused(string characterName, string explanation);

    void Notice(string text);

    void Ending(string text);
}
