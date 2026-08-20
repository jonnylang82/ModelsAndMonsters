using System.Net;
using Microsoft.Extensions.AI;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Agents;
using ModelsAndMonsters.Tracing;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The transient-retry path in <see cref="ModelAgent"/>. A provider can 5xx for a passing reason — most
/// often its own tool-call parser rejecting a reply the model malformed — and one bad draw must not end a
/// run. The retry re-samples so it does not simply reproduce the same fault: a measured failure was a
/// temperature-0 agent whose greedy, seed-pinned draw 500'd identically three times, so a retry raises the
/// temperature above zero and moves the seed. These tests pin that behaviour down.
/// </summary>
public sealed class TransientRetryTests
{
    /// <summary>A client that throws a chosen HTTP status for its first N calls, then returns a fixed reply.</summary>
    private sealed class FlakyChatClient(int failuresBeforeSuccess, ChatResponse success, HttpStatusCode status = HttpStatusCode.InternalServerError)
        : IChatClient
    {
        public List<ChatOptions?> RequestOptions { get; } = [];

        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            RequestOptions.Add(options);
            if (CallCount <= failuresBeforeSuccess)
            {
                throw new HttpRequestException("simulated provider failure", inner: null, statusCode: status);
            }

            return Task.FromResult(success);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>A minimal concrete agent exposing one traced model call for the retry path.</summary>
    private sealed class PokeAgent(AgentModelProfile profile, TracingChatClient client)
        : ModelAgent("Poke", profile, client, "system")
    {
        public Task<ChatResponse> Send() => CallModelAsync("test.call", tools: null, CancellationToken.None);
    }

    private static (PokeAgent Agent, FlakyChatClient Client) Build(AgentModelProfile profile, FlakyChatClient flaky)
    {
        var trace = new ExperimentTrace("test-run", new RecordingTraceSink());
        return (new PokeAgent(profile, new TracingChatClient(flaky, profile, trace)), flaky);
    }

    private static AgentModelProfile OllamaProfile(float temperature, long seed) => new()
    {
        AgentName = "Poke",
        Provider = ModelProvider.Ollama,
        ModelId = "scripted-model",
        Temperature = temperature,
        Seed = seed
    };

    [Fact]
    public async Task A_transient_500_is_retried_at_a_raised_temperature_and_a_moved_seed()
    {
        var (agent, flaky) = Build(
            OllamaProfile(temperature: 0f, seed: 100),
            new FlakyChatClient(failuresBeforeSuccess: 1, ScriptedChatClient.Text("recovered")));

        var response = await agent.Send();

        Assert.Equal("recovered", ModelText.Clean(response));
        Assert.Equal(2, flaky.CallCount);

        // First attempt uses the agent's exact profile: greedy (temperature 0) with its pinned seed, so a
        // run with no failures still replays identically.
        Assert.Equal(0f, flaky.RequestOptions[0]!.Temperature);
        Assert.Equal(100, flaky.RequestOptions[0]!.Seed);

        // The retry re-samples: the temperature is floored WELL above zero and the seed is moved, so the
        // draw genuinely moves rather than repeating the same one.
        Assert.Equal(0.4f, flaky.RequestOptions[1]!.Temperature);
        Assert.NotEqual(100, flaky.RequestOptions[1]!.Seed);
    }

    [Fact]
    public async Task The_retry_temperature_escalates_so_a_second_failure_draws_further_from_the_first()
    {
        // The number that killed a run. The floor used to be a flat 0.1, which is barely off greedy: a
        // temperature-0 agent re-sent at 0.1 reproduced the identical malformed tool call three times, Ollama
        // 500'd on each, and the run ended at round 5. In the same trace an agent configured at 0.8 took two
        // 500s and recovered on the third attempt — the mechanism was sound, the floor was not.
        var (agent, flaky) = Build(
            OllamaProfile(temperature: 0f, seed: 100),
            new FlakyChatClient(failuresBeforeSuccess: 2, ScriptedChatClient.Text("recovered")));

        await agent.Send();

        Assert.Equal(3, flaky.CallCount);
        Assert.Equal(0f, flaky.RequestOptions[0]!.Temperature);
        Assert.Equal(0.4f, flaky.RequestOptions[1]!.Temperature);
        Assert.Equal(0.8f, flaky.RequestOptions[2]!.Temperature);

        // Every attempt draws from a different seed as well, so two attempts never share a draw.
        var seeds = flaky.RequestOptions.Select(o => o!.Seed).ToList();
        Assert.Equal(seeds.Count, seeds.Distinct().Count());
    }

    [Fact]
    public async Task Each_attempt_is_recorded_in_the_trace_as_the_attempt_it_was()
    {
        // Without this a retry is invisible except as an unexplained shift in the sampling options, and a
        // reader has to know the seed-offset formula to tell a re-send from a fresh call. Diagnosing the run
        // that died meant doing exactly that.
        var sink = new RecordingTraceSink();
        var trace = new ExperimentTrace("test-run", sink);
        var profile = OllamaProfile(temperature: 0f, seed: 100);
        var flaky = new FlakyChatClient(failuresBeforeSuccess: 2, ScriptedChatClient.Text("recovered"));
        var agent = new PokeAgent(profile, new TracingChatClient(flaky, profile, trace));

        await agent.Send();

        var attempts = sink.Events
            .Where(e => e.EventType == TraceEventType.ModelRequest)
            .Select(e => Assert.IsType<ModelRequestPayload>(e.Data).Attempt)
            .ToList();
        Assert.Equal([1, 2, 3], attempts);

        // The failures say which attempt they were, too, so a trace shows the escalation working or not.
        var failed = sink.Events
            .Where(e => e.EventType == TraceEventType.ModelError)
            .Select(e => Assert.IsType<ModelErrorPayload>(e.Data).Attempt)
            .ToList();
        Assert.Equal([1, 2], failed);
    }

    [Fact]
    public async Task A_retry_never_lowers_a_sampling_agents_temperature_but_still_moves_the_seed()
    {
        var (agent, flaky) = Build(
            OllamaProfile(temperature: 0.8f, seed: 7),
            new FlakyChatClient(failuresBeforeSuccess: 1, ScriptedChatClient.Text("ok")));

        await agent.Send();

        // The floor is a minimum, not an override: an agent already sampling above it keeps its own
        // temperature; only the seed moves, which is enough to re-sample.
        Assert.Equal(0.8f, flaky.RequestOptions[1]!.Temperature);
        Assert.NotEqual(7, flaky.RequestOptions[1]!.Seed);
    }

    [Fact]
    public async Task A_client_error_is_surfaced_at_once_and_never_retried()
    {
        var (agent, flaky) = Build(
            OllamaProfile(temperature: 0f, seed: 1),
            new FlakyChatClient(failuresBeforeSuccess: 1, ScriptedChatClient.Text("unreached"), HttpStatusCode.BadRequest));

        // A 4xx is our own request's fault; hammering it would not help, so it is thrown immediately.
        await Assert.ThrowsAsync<HttpRequestException>(agent.Send);
        Assert.Equal(1, flaky.CallCount);
    }

    [Fact]
    public async Task A_persistent_transient_failure_surfaces_after_the_attempt_limit()
    {
        var (agent, flaky) = Build(
            OllamaProfile(temperature: 0f, seed: 1),
            new FlakyChatClient(failuresBeforeSuccess: 99, ScriptedChatClient.Text("unreached")));

        await Assert.ThrowsAsync<HttpRequestException>(agent.Send);

        // Three attempts total (the original plus two retries), then the failure is allowed to surface.
        Assert.Equal(3, flaky.CallCount);
    }
}
