namespace ModelsAndMonsters.AI;

/// <summary>
/// The model hosts v0.1 can talk to. Provider-specific client types are confined to
/// <see cref="ChatClientFactory"/>; nothing else in the application knows these apart.
/// </summary>
public enum ModelProvider
{
    Ollama,
    OpenAI,
    Anthropic,

    /// <summary>
    /// OpenRouter (https://openrouter.ai) — an OpenAI-compatible aggregator that routes one key to many
    /// providers' models. Reached through the OpenAI SDK's CHAT-COMPLETIONS client pointed at OpenRouter's
    /// base URL (not the Responses API, which OpenRouter does not serve). Model ids carry an org prefix,
    /// e.g. <c>anthropic/claude-sonnet-4.6</c>, <c>openai/gpt-5.2</c>, <c>deepseek/deepseek-chat</c>.
    /// </summary>
    OpenRouter
}
