using Microsoft.Extensions.AI;

namespace ModelsAndMonsters.AI;

/// <summary>
/// Resolves an <see cref="IChatClient"/> for an agent profile.
/// </summary>
/// <remarks>
/// This is the only seam in the application that knows about OllamaSharp or the OpenAI SDK. Swapping
/// an agent between providers is a configuration change; no agent or orchestration code moves.
/// </remarks>
public interface IChatClientFactory
{
    IChatClient Create(AgentModelProfile profile, string? runId = null);
}
