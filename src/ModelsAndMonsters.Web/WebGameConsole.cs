using ModelsAndMonsters.Presentation;

namespace ModelsAndMonsters.Web;

/// <summary>
/// An <see cref="IGameConsole"/> that pushes the human-readable transcript to a live viewer instead of
/// stdout — the same narration, speech, questions and answers the CLI prints, as SSE events.
/// </summary>
public sealed class WebGameConsole : IGameConsole
{
    private readonly Action<UiEvent> _publish;
    private readonly Action<string> _onRunId;

    public WebGameConsole(Action<UiEvent> publish, Action<string> onRunId)
    {
        _publish = publish;
        _onRunId = onRunId;
    }

    public void RunHeader(string runId, string scenarioName, string outputDirectory)
    {
        _onRunId(runId);
        _publish(UiEvent.RunHeader(runId, scenarioName));
    }

    public void RoundHeader(int round) => _publish(UiEvent.Round(round));

    public void DungeonMaster(string text) => _publish(UiEvent.Narration(text));

    public void PrivateObservation(string characterName, string observation) =>
        _publish(UiEvent.PrivateObservation(characterName, observation));

    public void CharacterAsks(string characterName, string question) => _publish(UiEvent.Asks(characterName, question));

    public void CharacterActs(string characterName, string intent) => _publish(UiEvent.Acts(characterName, intent));

    public void CharacterSpeaks(string characterName, string message) => _publish(UiEvent.Speaks(characterName, message));

    public void CharacterPasses(string characterName, string reason) => _publish(UiEvent.Passes(characterName, reason));

    public void CharacterRefused(string characterName, string explanation) =>
        _publish(UiEvent.Refused(characterName, explanation));

    public void Notice(string text) => _publish(UiEvent.Notice(text));

    public void Ending(string text) => _publish(UiEvent.Ending(text));
}
