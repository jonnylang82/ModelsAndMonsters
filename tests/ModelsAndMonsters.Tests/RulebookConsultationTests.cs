using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Rulebook;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The consultation end to end: retrieval hands the resolver the whole rulebook, and the resolver's choice —
/// not any keyword in the intent — drives which engine tool the Dungeon Master is offered. These tests use a
/// scripted resolver so the selection is deterministic, and deliberately phrase the intents as keyword-free
/// paraphrases (none of the chosen action's own words appear) to show the routing no longer depends on them.
/// </summary>
public sealed class RulebookConsultationTests
{
    private static readonly PromptLibrary Prompts =
        PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));

    private static readonly RuleCatalog Catalog = new();

    private static (RulebookConsultant Consultant, ScriptedChatClient ResolverClient) Build(params ChatResponse[] resolverResponses)
    {
        var profile = new AgentModelProfile
        {
            AgentName = "RulebookResolver",
            Provider = ModelProvider.Ollama,
            ModelId = "scripted-model",
            Temperature = 0.1f,
            Seed = 1
        };
        var trace = new ExperimentTrace("t", new RecordingTraceSink());
        var resolverClient = new ScriptedChatClient(resolverResponses);
        var resolver = new RulebookResolver(profile, new TracingChatClient(resolverClient, profile, trace), Prompts);
        var retriever = new RuleRetriever(Catalog, maxCards: 16, maxInputChars: 16000);
        var consultant = new RulebookConsultant(
            Catalog, retriever, resolver, new RuleGuidanceValidator(Catalog), new RuleGuidanceCache(), trace,
            new RulebookConsultationOptions(MaxCards: 16, MaxInputChars: 16000, OutputTokenLimit: 600, CacheEnabled: false));
        return (consultant, resolverClient);
    }

    /// <summary>Guidance JSON selecting one action and citing its rule at the catalog's current version.</summary>
    private static string GuidanceJson(string action, string ruleId)
    {
        var card = Catalog.Find(ruleId)!;
        return $$"""
        {
          "supported": true,
          "candidateActions": ["{{action}}"],
          "citedRules": [ { "ruleId": "{{ruleId}}", "version": "{{card.Version}}" } ],
          "requiredBindings": [], "preconditions": [], "turnCost": "consumes the turn",
          "rngSpecification": "none", "visibility": "public",
          "successBehaviour": "the engine resolves it", "failureBehaviour": "the engine refuses it"
        }
        """;
    }

    // A flight through the open door, worded with none of the escape card's words — no "run", "flee", "bolt",
    // "escape", "door", "stairs", "get out". A keyword index would never route this to encounter.escape.
    private const string KeywordFreeEscapeIntent =
        "I have no wish to die in this cellar, so I turn my back on the fight and make for the opening the goblins first came through, to put it all behind me.";

    [Fact]
    public async Task The_whole_rulebook_reaches_the_resolver_even_for_a_keyword_free_paraphrase()
    {
        var (consultant, resolverClient) = Build(ScriptedChatClient.Text(GuidanceJson("escape_encounter", "encounter.escape")));

        await consultant.ConsultAsync("goblin-vark", "Vark", KeywordFreeEscapeIntent, CancellationToken.None);

        // Every rule card is present in the resolver's request, so the resolver could choose ANY action — the
        // intent's wording never narrowed what it was allowed to consider.
        var request = string.Join("\n", resolverClient.Requests[0].Select(m => m.Text));
        foreach (var card in Catalog.AllCards)
        {
            Assert.Contains(card.RuleId, request, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_resolver_choice_not_a_keyword_decides_the_candidate_tool()
    {
        var (consultant, _) = Build(ScriptedChatClient.Text(GuidanceJson("escape_encounter", "encounter.escape")));

        var result = await consultant.ConsultAsync("goblin-vark", "Vark", KeywordFreeEscapeIntent, CancellationToken.None);

        Assert.Equal(RulebookOutcome.Supported, result.Outcome);
        Assert.True(result.HasSupportedGuidance);

        // The DM is narrowed to exactly the resolver's chosen action plus the ever-present rejection — driven
        // by the resolver's reading of the paraphrase, not by any word the retriever matched.
        Assert.Equal(["escape_encounter", "reject_action"], result.CandidateTools.Select(t => t.Name));
    }

    [Fact]
    public async Task The_same_keyword_free_intent_routes_to_whatever_action_the_resolver_selects()
    {
        // Identical phrasing, a different resolver choice — the candidate tool follows the resolver, proving
        // the mapping is the model's, not the retriever's.
        var (giveConsultant, _) = Build(ScriptedChatClient.Text(GuidanceJson("give_item", "inventory.give")));
        var give = await giveConsultant.ConsultAsync("hero-rowan", "Rowan", "I want Elara to have the thing I am carrying, so I put it in her hands.", CancellationToken.None);
        Assert.Equal(["give_item", "reject_action"], give.CandidateTools.Select(t => t.Name));

        var (surrenderConsultant, _) = Build(ScriptedChatClient.Text(GuidanceJson("surrender", "encounter.surrender")));
        var surrender = await surrenderConsultant.ConsultAsync("hero-rowan", "Rowan", "I want Elara to have the thing I am carrying, so I put it in her hands.", CancellationToken.None);
        Assert.Equal(["surrender", "reject_action"], surrender.CandidateTools.Select(t => t.Name));
    }

    [Fact]
    public async Task An_unsupported_resolver_verdict_narrows_the_dungeon_master_to_rejection_only()
    {
        var (consultant, _) = Build(ScriptedChatClient.Text("""
            { "supported": false, "unsupportedReason": "No rule resolves flying to the ceiling." }
            """));

        var result = await consultant.ConsultAsync("goblin-vark", "Vark", "I sprout wings and fly up to the rafters.", CancellationToken.None);

        Assert.Equal(RulebookOutcome.Unsupported, result.Outcome);
        Assert.False(result.HasSupportedGuidance);
        Assert.Equal(["reject_action"], result.CandidateTools.Select(t => t.Name));
    }

    [Fact]
    public async Task A_resolver_model_failure_fails_safe_to_rejection_only()
    {
        // The scripted client runs out of responses -> the resolver reports a model-call failure -> the
        // consultant fails safe, exposing only rejection so the DM cannot invent an action.
        var (consultant, _) = Build();

        var result = await consultant.ConsultAsync("goblin-vark", "Vark", "I strike the human", CancellationToken.None);

        Assert.Equal(RulebookOutcome.ResolverFailure, result.Outcome);
        Assert.Equal(["reject_action"], result.CandidateTools.Select(t => t.Name));
    }
}
