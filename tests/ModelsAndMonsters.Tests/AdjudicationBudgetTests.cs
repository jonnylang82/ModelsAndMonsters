using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// How much room an adjudication is given to answer in, and why that depends on the provider.
/// </summary>
/// <remarks>
/// The tight adjudication cap has exactly one justification, and it is arithmetic: on Ollama the context
/// window covers input and output together, so reserving the DM's full narration-sized allowance for a reply
/// that needs a hundred tokens takes that room away from the request — on the call that already carries the
/// largest input. A hosted provider budgets output separately from its far larger input window, so the same
/// reservation costs the request nothing and the cap buys nothing.
///
/// This is the same asymmetry as <see cref="AgentModelProfile.BindingContextWindow"/>, and it reads the same
/// signal. Applying the cap everywhere was the mirror image of budgeting a hosted model's history against a
/// window it never receives: a local accommodation imposed globally.
/// </remarks>
public sealed class AdjudicationBudgetTests
{
    private const int ConfiguredOutputTokens = 1700;

    private static readonly PromptLibrary Prompts =
        PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));

    private static AgentModelProfile Profile(ModelProvider provider) => new()
    {
        AgentName = DungeonMasterAgent.AgentIdentifier,
        Provider = provider,
        ModelId = "scripted-model",
        MaxOutputTokens = ConfiguredOutputTokens,
        ContextWindow = 8192
    };

    private static async Task<ChatOptions?> AdjudicateWith(ModelProvider provider)
    {
        var profile = Profile(provider);
        var client = new ScriptedChatClient(
            ScriptedChatClient.Call("dm-1", DungeonMasterTools.DefendName, ("actor", "Aric")));
        var agent = new DungeonMasterAgent(
            profile,
            new TracingChatClient(client, profile, new ExperimentTrace("test-run", new RecordingTraceSink())),
            Prompts);

        await agent.ProposeActionAsync(
            "Aric: 10/10. Grik: 8/8.", "Aric", "Aric has seen Grik.", "I raise my guard.", CancellationToken.None);

        return client.RequestOptions[^1];
    }

    [Fact]
    public async Task On_a_shared_window_the_adjudication_is_capped_well_below_the_agents_allowance()
    {
        // Ollama takes num_ctx, so every output token reserved is an input token surrendered.
        var options = await AdjudicateWith(ModelProvider.Ollama);

        Assert.Equal(400, options!.MaxOutputTokens);
    }

    [Theory]
    [InlineData(ModelProvider.OpenAI)]
    [InlineData(ModelProvider.Anthropic)]
    public async Task On_a_hosted_provider_the_adjudication_keeps_the_agents_own_allowance(ModelProvider provider)
    {
        // Nothing is bought by the cap here, and something is lost: two live Haiku runs truncated three
        // adjudications between them, each part-way through checking preconditions it had every right to
        // check. A ruling made on interrupted reasoning is not better than one made on finished reasoning.
        var options = await AdjudicateWith(provider);

        Assert.Equal(ConfiguredOutputTokens, options!.MaxOutputTokens);
    }

    [Fact]
    public async Task Narration_always_keeps_the_full_allowance_whatever_the_provider()
    {
        // The cap was never about narration, which is prose and genuinely needs the room.
        var profile = Profile(ModelProvider.Ollama);
        var client = new ScriptedChatClient(ScriptedChatClient.Text("Aric sets his feet."));
        var agent = new DungeonMasterAgent(
            profile,
            new TracingChatClient(client, profile, new ExperimentTrace("test-run", new RecordingTraceSink())),
            Prompts);

        await agent.NarrateAsync("Aric: 10/10. Grik: 8/8.", "Aric raises his guard.", "opening", CancellationToken.None);

        Assert.Equal(ConfiguredOutputTokens, client.RequestOptions[^1]!.MaxOutputTokens);
    }
}
