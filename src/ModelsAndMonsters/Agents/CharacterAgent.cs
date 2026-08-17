using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Agents;

/// <summary>
/// An autonomous inhabitant of the world. The Hero and the Monster are both instances of this class
/// with different definitions, prompts and model profiles — there is no separate monster behaviour.
/// </summary>
/// <remarks>
/// The agent is given only <see cref="CharacterTools"/>. It has no idea the engine exists, cannot name
/// an engine action, and never learns the mechanical result of anything except through the Dungeon
/// Master's words.
/// </remarks>
public sealed class CharacterAgent : ModelAgent
{
    public CharacterAgent(
        CharacterDefinition definition,
        AgentModelProfile profile,
        TracingChatClient client,
        string systemPrompt)
        : base(definition.Name, profile, client, systemPrompt)
    {
        Definition = definition;
    }

    public CharacterDefinition Definition { get; }

    public string CharacterId => Definition.Id;

    public string Name => Definition.Name;

    /// <summary>Injects this turn's exact self-state and the narration this character has not yet heard.</summary>
    public void BeginTurn(string turnContext) => Conversation.AppendUser(turnContext);

    /// <summary>Asks the character what it wants to do, exposing only ask_dm and take_action.</summary>
    public Task<ChatResponse> DecideAsync(CancellationToken cancellationToken) =>
        CallModelAsync("character.decide", CharacterTools.All, cancellationToken);

    /// <summary>Used when the model replied without calling either tool.</summary>
    public void AppendNudge(string text) => Conversation.AppendUser(text);
}
