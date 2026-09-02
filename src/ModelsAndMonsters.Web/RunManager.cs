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

    public RunSession(string runId, string? guestCharacterId = null)
    {
        RunId = runId;
        Guest = new GuestControl(Publish, guestCharacterId);
    }

    public GuestControl Guest { get; }

    public string RunId { get; }

    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>
    /// When this run finished, so <see cref="RunManager"/> can evict it once nobody could still be replaying
    /// it. Null while the run is live.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; private set; }

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

    /// <summary>
    /// Unsubscribes a viewer that disconnected, so a channel nobody is reading any more stops receiving
    /// events. Without this an unbounded channel left over from a closed SSE connection keeps queuing every
    /// event published for the rest of the run, holding them in memory for a reader that will never arrive.
    /// </summary>
    public void Unsubscribe(ChannelReader<UiEvent> reader)
    {
        lock (_gate)
        {
            _subscribers.RemoveAll(subscriber => subscriber.Reader == reader);
        }
    }

    /// <summary>Marks the run finished and closes every viewer's stream.</summary>
    public void Complete()
    {
        Guest.Close();
        lock (_gate)
        {
            _completed = true;
            CompletedAt = DateTimeOffset.UtcNow;
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
    /// <summary>
    /// How long a finished run stays reachable by id after it completes — long enough for a viewer to
    /// reconnect and replay the ending, short enough that a long-lived host does not accumulate every run
    /// it has ever started. A completed session still holds its whole event buffer in memory, so retaining
    /// every one indefinitely is unbounded growth for no benefit once nobody is going to ask for it again.
    /// </summary>
    private static readonly TimeSpan CompletedRunRetention = TimeSpan.FromMinutes(30);

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
        PruneCompletedRuns();

        // A provisional id so a viewer can subscribe the instant this returns; replaced with the real run id
        // once the runner reports it in the runHeader event.
        var session = new RunSession($"pending-{Guid.NewGuid():N}", _scenario.Rescue?.DetaineeId);
        var scenario = _scenario; // scenario selection is a later increment.

        var console = new WebGameConsole(session.Publish, id => Rekey(session, id));
        var sink = new WebTraceSink(session.Publish);
        var runner = new SimulationRunner(_options, scenario, _chatClientFactory, _prompts, console, sink, session.Guest);

        // Surface the default agent model to the status bar up front, so it shows the moment a viewer connects
        // rather than waiting for the first model response. Individual agents may override it, but the Default
        // profile is what "the model" means at a glance.
        var defaults = _options.Agents.Default;
        session.Publish(UiEvent.Config(
            string.IsNullOrWhiteSpace(defaults.ModelId) ? "(unset)" : defaults.ModelId,
            string.IsNullOrWhiteSpace(defaults.Provider) ? "" : defaults.Provider));
        session.Publish(UiEvent.Objectives(scenario.Objectives));

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

    /// <summary>
    /// Evicts sessions that finished more than <see cref="CompletedRunRetention"/> ago. A rekeyed run is
    /// reachable under both its provisional and real id, and each is its own dictionary entry pointing at the
    /// same <see cref="RunSession"/> — pruning both is just a normal part of this same sweep, since each
    /// entry is checked against that session's own <see cref="RunSession.CompletedAt"/> independently.
    /// </summary>
    private void PruneCompletedRuns()
    {
        var cutoff = DateTimeOffset.UtcNow - CompletedRunRetention;
        foreach (var (id, session) in _runs)
        {
            if (session.CompletedAt is { } completedAt && completedAt < cutoff)
            {
                _runs.TryRemove(id, out _);
            }
        }
    }
}
