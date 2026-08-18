using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using ModelsAndMonsters.AI;
using ModelsAndMonsters.Configuration;
using ModelsAndMonsters.Orchestration;
using ModelsAndMonsters.Prompts;

namespace ModelsAndMonsters.Web;

/// <summary>
/// One live run: it holds every event emitted so far (so a viewer that connects mid-run gets the whole
/// story), the set of currently-connected viewers, and the token that cancels the run.
/// </summary>
public sealed class RunSession
{
    private readonly object _gate = new();
    private readonly List<UiEvent> _buffer = [];
    private readonly List<Channel<UiEvent>> _subscribers = [];
    private bool _completed;

    public RunSession(string runId) => RunId = runId;

    public string RunId { get; }

    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>Records an event and fans it out to every connected viewer.</summary>
    public void Publish(UiEvent uiEvent)
    {
        lock (_gate)
        {
            _buffer.Add(uiEvent);
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(uiEvent);
            }
        }
    }

    /// <summary>Subscribes a viewer: it is first replayed the buffered events, then streamed live ones.</summary>
    public ChannelReader<UiEvent> Subscribe()
    {
        var channel = Channel.CreateUnbounded<UiEvent>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate)
        {
            foreach (var buffered in _buffer)
            {
                channel.Writer.TryWrite(buffered);
            }

            if (_completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(channel);
            }
        }

        return channel.Reader;
    }

    /// <summary>Marks the run finished and closes every viewer's stream.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }
}

/// <summary>
/// Starts and tracks live runs. Each run is built with its own <see cref="WebGameConsole"/> and
/// <see cref="WebTraceSink"/> bound to its session, so its narration and structured events flow to that
/// run's viewers alongside the authoritative file trace.
/// </summary>
public sealed class RunManager
{
    private readonly ConcurrentDictionary<string, RunSession> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SimulationOptions _options;
    private readonly ScenarioDefinition _scenario;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly PromptLibrary _prompts;
    private readonly ILogger<RunManager> _logger;

    public RunManager(
        IOptions<SimulationOptions> options,
        IOptions<ScenarioDefinition> scenario,
        IChatClientFactory chatClientFactory,
        PromptLibrary prompts,
        ILogger<RunManager> logger)
    {
        _options = options.Value;
        _scenario = scenario.Value;
        _chatClientFactory = chatClientFactory;
        _prompts = prompts;
        _logger = logger;
    }

    public RunSession? Get(string runId) => _runs.TryGetValue(runId, out var session) ? session : null;

    /// <summary>
    /// Starts a run on a background task and returns its session immediately. The optional scenario id is
    /// reserved for choosing among scenarios later; for now the shipped scenario is always used.
    /// </summary>
    public RunSession Start(string? scenarioId = null)
    {
        // A provisional id so a viewer can subscribe the instant this returns; replaced with the real run id
        // once the runner reports it in the runHeader event.
        var session = new RunSession($"pending-{Guid.NewGuid():N}");
        var scenario = _scenario; // scenario selection is a later increment.

        var console = new WebGameConsole(session.Publish, id => Rekey(session, id));
        var sink = new WebTraceSink(session.Publish);
        var runner = new SimulationRunner(_options, scenario, _chatClientFactory, _prompts, console, sink);

        _runs[session.RunId] = session;

        _ = Task.Run(async () =>
        {
            try
            {
                await runner.RunAsync(session.Cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                session.Publish(UiEvent.Notice("Run cancelled."));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Run {RunId} failed", session.RunId);
                session.Publish(UiEvent.Notice($"Run failed: {ex.GetType().Name}: {ex.Message}"));
            }
            finally
            {
                session.Complete();
            }
        });

        return session;
    }

    public void Cancel(string runId)
    {
        if (_runs.TryGetValue(runId, out var session))
        {
            session.Cancellation.Cancel();
        }
    }

    /// <summary>Re-registers a session under the real run id once the runner reports it.</summary>
    private void Rekey(RunSession session, string realRunId)
    {
        if (!string.Equals(session.RunId, realRunId, StringComparison.OrdinalIgnoreCase))
        {
            _runs[realRunId] = session;
        }
    }
}
