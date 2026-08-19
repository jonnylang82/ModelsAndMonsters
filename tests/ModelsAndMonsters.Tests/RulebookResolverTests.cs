using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Prompts;
using ModelsAndMonsters.Rulebook;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The stateless Rulebook Resolver: it parses the guidance JSON, carries no history between calls, and its
/// request contains only the raw intent and the rule cards — never live game state, character sheets or
/// private knowledge.
/// </summary>
public sealed class RulebookResolverTests
{
    private static readonly PromptLibrary Prompts =
        PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));

    private static readonly RuleCatalog Catalog = new();

    private static (RulebookResolver Resolver, ScriptedChatClient Client) Build(params ChatResponse[] responses)
    {
        var profile = new AgentModelProfile
        {
            AgentName = "RulebookResolver",
            Provider = ModelProvider.Ollama,
            ModelId = "scripted-model",
            Temperature = 0.1f,
            Seed = 1
        };
        var client = new ScriptedChatClient(responses);
        var trace = new ExperimentTrace("t", new RecordingTraceSink());
        var resolver = new RulebookResolver(profile, new TracingChatClient(client, profile, trace), Prompts);
        return (resolver, client);
    }

    private static string StealGuidanceJson()
    {
        var steal = Catalog.Find("inventory.steal")!;
        return $$"""
        {
          "supported": true,
          "candidateActions": ["steal_item"],
          "citedRules": [ { "ruleId": "inventory.steal", "version": "{{steal.Version}}" } ],
          "requiredBindings": ["thief", "target", "item"],
          "preconditions": ["both active and present"],
          "turnCost": "consumes the turn",
          "rngSpecification": "one seeded draw",
          "visibility": "public",
          "successBehaviour": "item moves to thief",
          "failureBehaviour": "nothing changes hands"
        }
        """;
    }

    [Fact]
    public async Task The_resolver_parses_a_well_formed_guidance_response()
    {
        var (resolver, _) = Build(ScriptedChatClient.Text(StealGuidanceJson()));

        var result = await resolver.ResolveAsync("I snatch the potion from Vark", [Catalog.Find("inventory.steal")!], CancellationToken.None);

        Assert.NotNull(result.Parsed);
        Assert.True(result.Parsed!.Supported);
        Assert.Equal(["steal_item"], result.Parsed.CandidateActions);
        var cited = Assert.Single(result.Parsed.CitedRules);
        Assert.Equal("inventory.steal", cited.RuleId);
    }

    [Fact]
    public async Task The_resolver_tolerates_prose_around_the_json()
    {
        var (resolver, _) = Build(ScriptedChatClient.Text($"Sure — here is the guidance:\n```json\n{StealGuidanceJson()}\n```\nHope that helps."));

        var result = await resolver.ResolveAsync("I snatch the potion", [Catalog.Find("inventory.steal")!], CancellationToken.None);

        Assert.NotNull(result.Parsed);
        Assert.True(result.Parsed!.Supported);
    }

    [Fact]
    public async Task An_unparseable_response_yields_null_guidance_rather_than_throwing()
    {
        var (resolver, _) = Build(ScriptedChatClient.Text("I have no idea, honestly."));

        var result = await resolver.ResolveAsync("I do something", [Catalog.Find("combat.attack")!], CancellationToken.None);

        Assert.Null(result.Parsed);
        Assert.False(result.ModelCallFailed);
    }

    [Fact]
    public async Task Each_resolver_call_is_stateless_and_never_carries_the_previous_call()
    {
        var (resolver, client) = Build(
            ScriptedChatClient.Text(StealGuidanceJson()),
            ScriptedChatClient.Text(StealGuidanceJson()));

        await resolver.ResolveAsync("FIRST-INTENT snatch the rope", [Catalog.Find("inventory.steal")!], CancellationToken.None);
        await resolver.ResolveAsync("SECOND-INTENT give the flask", [Catalog.Find("inventory.give")!], CancellationToken.None);

        Assert.Equal(2, client.Requests.Count);

        // Each request is exactly a system message plus one user message — no accumulated history.
        Assert.Equal(2, client.Requests[1].Count);

        // The second request does not contain the first intent, and vice versa: the conversation is fresh.
        var second = string.Join("\n", client.Requests[1].Select(m => m.Text));
        Assert.DoesNotContain("FIRST-INTENT", second, StringComparison.Ordinal);
        Assert.Contains("SECOND-INTENT", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_resolver_request_contains_no_live_state_or_private_knowledge()
    {
        var (resolver, client) = Build(ScriptedChatClient.Text(StealGuidanceJson()));

        await resolver.ResolveAsync("I strike the goblin captain", [Catalog.Find("combat.attack")!], CancellationToken.None);

        var request = string.Join("\n", client.Requests[0].Select(m => m.Text));

        // The intent and the card are present; live-state and knowledge-view markers are not.
        Assert.Contains("I strike the goblin captain", request, StringComparison.Ordinal);
        Assert.Contains("combat.attack", request, StringComparison.Ordinal);
        Assert.DoesNotContain("CHARACTERS:", request, StringComparison.Ordinal);
        Assert.DoesNotContain("Condition:", request, StringComparison.Ordinal);
        Assert.DoesNotContain("hit chance", request, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DIRECTLY KNOWS", request, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authoritative contents", request, StringComparison.Ordinal);
    }
}
