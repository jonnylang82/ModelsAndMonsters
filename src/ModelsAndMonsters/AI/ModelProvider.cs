namespace ModelsAndMonsters.AI;

/// <summary>
/// The model hosts v0.1 can talk to. Provider-specific client types are confined to
/// <see cref="ChatClientFactory"/>; nothing else in the application knows these apart.
/// </summary>
public enum ModelProvider
{
    Ollama,
    OpenAI,
    Anthropic
}
