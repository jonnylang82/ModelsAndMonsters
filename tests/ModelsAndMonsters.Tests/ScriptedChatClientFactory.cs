using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// An <see cref="IChatClientFactory"/> that hands each agent its own scripted client, keyed by agent
/// name, so the whole <see cref="ModelsAndMonsters.Orchestration.SimulationRunner"/> can be driven end
/// to end without a model service.
/// </summary>
internal sealed class ScriptedChatClientFactory : IChatClientFactory
{
    private readonly IReadOnlyDictionary<string, ScriptedChatClient> _clientsByAgent;

    public ScriptedChatClientFactory(IReadOnlyDictionary<string, ScriptedChatClient> clientsByAgent)
    {
        _clientsByAgent = clientsByAgent;
    }

    public IChatClient Create(AgentModelProfile profile, string? runId = null) =>
        _clientsByAgent.TryGetValue(profile.AgentName, out var client)
            ? client
            : throw new InvalidOperationException(
                $"No scripted client was provided for agent '{profile.AgentName}'.");
}
