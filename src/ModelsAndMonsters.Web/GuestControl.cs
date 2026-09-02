using ModelsAndMonsters.Agents;

namespace ModelsAndMonsters.Web;

/// <summary>A single run's optional guest controller. Commands are accepted only for the displayed pending request.</summary>
public sealed class GuestControl(Action<UiEvent> publish, string? characterId) : ICharacterInput
{
    private readonly object _gate = new();
    private bool _controlled;
    private bool _closed;
    private string? _requestId;
    private TaskCompletionSource<GuestDecision?>? _pending;

    public bool SetControlled(bool controlled)
    {
        lock (_gate)
        {
            if (_closed || characterId is null) return false;
            _controlled = controlled;
            if (!controlled) _pending?.TrySetResult(null);
            PublishState();
            return true;
        }
    }

    public bool Submit(string requestId, GuestDecision decision)
    {
        lock (_gate)
        {
            if (_closed || !_controlled || _requestId != requestId || _pending is null) return false;
            if (string.IsNullOrWhiteSpace(decision.Intent) || decision.Intent.Length > 1200
                || (decision.Speech?.Length ?? 0) > 500) return false;
            return _pending.TrySetResult(decision);
        }
    }

    public async Task<GuestDecision?> ReadAsync(string actorId, CancellationToken cancellationToken)
    {
        TaskCompletionSource<GuestDecision?> pending;
        lock (_gate)
        {
            if (_closed || !_controlled || actorId != characterId) return null;
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = pending;
            _requestId = Guid.NewGuid().ToString("N");
            PublishState();
        }
        try { return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                    _requestId = null;
                    if (!_closed) PublishState();
                }
            }
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _controlled = false;
            _pending?.TrySetResult(null);
            _requestId = null;
            PublishState();
        }
    }

    private void PublishState() => publish(new UiEvent("guestControl", new
    {
        characterId, controlled = _controlled, requestId = _controlled ? _requestId : null,
        closed = _closed
    }));
}
