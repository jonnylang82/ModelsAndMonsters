using ModelsAndMonsters.AI;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Agents;

/// <summary>
/// Writes a short, grounded account of one finished encounter's opening and its fight, from a deterministic
/// <see cref="EncounterStoryBrief"/> alone — never a private answer, a refused attempt, or a harness
/// notice, and never the raw public transcript. Runs once, at the end of a run, statelessly: nothing it
/// produces feeds back into the game or any other agent, and the ending is never its to write (the harness
/// appends <see cref="EncounterStoryBrief.RenderEndingParagraph"/> deterministically afterward).
/// </summary>
/// <remarks>
/// Deliberately independent of every other agent's configuration. This is the one place in the harness where
/// a model repeating itself, taking liberties with phrasing, or reaching for an unusual word is exactly what
/// is wanted — the opposite of what every tool-calling agent needs — so it is expected to run its own
/// provider, model and sampling profile (<c>Agents:EncounterSummariser</c>), tuned for a good read rather
/// than for structural fidelity.
/// </remarks>
public sealed class EncounterSummariser : ModelAgent
{
    public const string AgentIdentifier = "EncounterSummariser";

    public EncounterSummariser(AgentModelProfile profile, TracingChatClient client, PromptLibrary prompts)
        : base(AgentIdentifier, profile, client, prompts.Render("encounter.summarise.system"))
    {
        _prompts = prompts;
    }

    private readonly PromptLibrary _prompts;

    /// <summary>
    /// Writes the story. Returns empty if the model produced nothing usable, in which case the caller simply
    /// has no story to show or write — the run's real artefacts are unaffected either way.
    /// </summary>
    public async Task<string> SummariseAsync(
        string scenarioName,
        EncounterStoryBrief brief,
        CancellationToken cancellationToken)
    {
        var task = _prompts.Render("encounter.summarise", new Dictionary<string, string?>
        {
            ["scenario_name"] = scenarioName,
            ["scenario_premise"] = brief.ScenarioPremise,
            ["roster"] = brief.Roster,
            ["brief"] = brief.RenderEventsForModel()
        });

        var conversation = new AgentConversation(AgentName, Conversation.SystemPrompt);
        conversation.AppendUser(task);

        var response = await CallModelAsync(conversation, "encounter.summarise", tools: null, cancellationToken)
            .ConfigureAwait(false);

        return ModelText.Clean(response);
    }
}
