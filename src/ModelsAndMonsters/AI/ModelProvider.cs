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
    OpenRouter,

    /// <summary>
    /// LM Studio's local OpenAI-compatible chat-completions endpoint. This is separate from the generic
    /// hosted providers because LM Studio accepts a request-level <c>reasoning_effort</c> control and needs
    /// no API key by default.
    /// </summary>
    LMStudio,

    /// <summary>
    /// Unsloth Studio — a local model server (default <c>http://localhost:8888</c>) that exposes both an
    /// OpenAI-compatible and an Anthropic-compatible API. Reached through the OpenAI SDK's CHAT-COMPLETIONS
    /// client, the same pattern as <see cref="OpenRouter"/>, pointed at its local base URL and carrying a
    /// bearer token from an environment variable (or user secrets) the same way every other hosted provider
    /// does.
    /// </summary>
    UnslothStudio
}
