using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Prompts;

namespace ModelsAndMonsters.Tests;

public sealed class ModelConfigurationTests
{
    [Fact]
    public void Every_agent_can_be_configured_independently()
    {
        var agents = new AgentsOptions
        {
            DungeonMaster = new AgentProfileOptions { Provider = "Ollama", ModelId = "llama3.1", Temperature = 0.3f },
            Hero = new AgentProfileOptions { Provider = "Ollama", ModelId = "granite4.1:8b", Temperature = 0.9f, TopK = 40 },
            Monster = new AgentProfileOptions { Provider = "OpenAI", ModelId = "gpt-4.1-mini", Seed = 7 }
        };

        var dungeonMaster = AgentModelProfile.FromOptions("DungeonMaster", agents.DungeonMaster);
        var hero = AgentModelProfile.FromOptions("Aric", agents.Hero);
        var monster = AgentModelProfile.FromOptions("Grik", agents.Monster);

        Assert.Equal(ModelProvider.Ollama, dungeonMaster.Provider);
        Assert.Equal("granite4.1:8b", hero.ModelId);
        Assert.Equal(ModelProvider.OpenAI, monster.Provider);
        Assert.Equal(7, monster.Seed);
    }

    [Fact]
    public void An_unknown_provider_fails_loudly_at_configuration_time()
    {
        var options = new AgentProfileOptions { Provider = "Anthropic", ModelId = "x" };

        var exception = Assert.Throws<InvalidOperationException>(() => AgentModelProfile.FromOptions("Hero", options));
        Assert.Contains("unknown provider", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sampling_options_a_provider_cannot_honour_are_dropped_and_reported()
    {
        var profile = new AgentModelProfile
        {
            AgentName = "Grik",
            Provider = ModelProvider.OpenAI,
            ModelId = "gpt-4.1-mini",
            Temperature = 0.7f,
            TopK = 40,
            Seed = 5
        };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Null(resolved.Options.TopK);
        Assert.Equal(0.7f, resolved.Options.Temperature);
        Assert.Equal(5, resolved.Options.Seed);
        Assert.Equal(nameof(AgentModelProfile.TopK), Assert.Single(resolved.UnsupportedOptionsDropped));
    }

    [Fact]
    public void Ollama_honours_every_supported_sampling_option()
    {
        var profile = new AgentModelProfile
        {
            AgentName = "Aric",
            Provider = ModelProvider.Ollama,
            ModelId = "llama3.1",
            Temperature = 0.8f,
            TopP = 0.95f,
            TopK = 40,
            MaxOutputTokens = 500,
            Seed = 11
        };

        var resolved = ChatOptionsFactory.Create(profile);

        Assert.Empty(resolved.UnsupportedOptionsDropped);
        Assert.Equal(40, resolved.Options.TopK);
        Assert.Equal(500, resolved.Options.MaxOutputTokens);
    }

    [Fact]
    public void Character_and_dungeon_master_tools_are_declarations_with_no_implementation_to_invoke()
    {
        var allTools = CharacterTools.All.Concat(DungeonMasterTools.All).ToList();

        foreach (var tool in allTools)
        {
            Assert.IsAssignableFrom<AIFunctionDeclaration>(tool);

            // AIFunction is the invocable subtype. These deliberately are not one, so no middleware
            // could execute them even if it were introduced by mistake.
            Assert.IsNotAssignableFrom<AIFunction>(tool);
        }

        Assert.Equal(6, allTools.Count);
    }

    [Fact]
    public void Prompt_templates_are_versioned_by_content()
    {
        var library = PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));

        Assert.Contains("dungeon-master.system", library.Versions.Keys);
        Assert.Contains("character.system", library.Versions.Keys);
        Assert.All(library.Versions.Values, v => Assert.StartsWith("sha256:", v, StringComparison.Ordinal));

        var same = new PromptTemplate("t", "hello {{name}}");
        var different = new PromptTemplate("t", "hello {{name}}!");
        Assert.Equal(same.Version, new PromptTemplate("t", "hello {{name}}").Version);
        Assert.NotEqual(same.Version, different.Version);
        Assert.Equal("hello Aric", same.Render(new Dictionary<string, string?> { ["name"] = "Aric" }));
    }

    [Fact]
    public void Character_system_prompts_carry_the_character_definition_and_nothing_mechanical()
    {
        var library = PromptLibrary.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Prompts", "Templates"));
        var factory = new CharacterPromptFactory(library);
        var definition = TestWorld.Scenario().Characters[0];

        var prompt = factory.CreateSystemPrompt(definition);

        Assert.Contains("You are Aric", prompt, StringComparison.Ordinal);
        Assert.Contains("Brave and direct.", prompt, StringComparison.Ordinal);
        Assert.Contains("ask_dm", prompt, StringComparison.Ordinal);
        Assert.Contains("take_action", prompt, StringComparison.Ordinal);

        // The character must not learn the engine's vocabulary.
        Assert.DoesNotContain("attack_character", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("use_item", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_arguments_are_read_tolerantly_across_casing_and_naming_differences()
    {
        var exact = ScriptedChatClient.CallContent("1", "take_action", ("intent", "I strike."));
        var wrongCase = ScriptedChatClient.CallContent("2", "take_action", ("Intent", "I strike."));
        var wrongName = ScriptedChatClient.CallContent("3", "take_action", ("action", "I strike."));
        var ambiguous = ScriptedChatClient.CallContent("4", "take_action", ("a", "one"), ("b", "two"));

        Assert.Equal("I strike.", ToolArguments.GetString(exact, "intent"));
        Assert.Equal("I strike.", ToolArguments.GetString(wrongCase, "intent"));
        Assert.Equal("I strike.", ToolArguments.GetString(wrongName, "intent"));
        Assert.Null(ToolArguments.GetString(ambiguous, "intent"));
    }
}
