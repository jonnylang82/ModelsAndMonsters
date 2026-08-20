using ModelsAndMonsters.Web;

namespace ModelsAndMonsters.Tests;

/// <summary>
/// The live-run observer's subscriber bookkeeping, tested without any HTTP or SSE plumbing: a disconnected
/// viewer's channel must stop receiving events, and a completed run must record when it finished so a
/// long-lived host can evict it later (see <see cref="RunManager"/>'s completed-run pruning).
/// </summary>
public sealed class RunSessionTests
{
    [Fact]
    public void An_unsubscribed_viewer_receives_nothing_published_after_it_disconnects()
    {
        var session = new RunSession("run-1");
        var reader = session.Subscribe();

        session.Publish(UiEvent.Notice("before disconnect"));
        Assert.True(reader.TryRead(out _));

        session.Unsubscribe(reader);
        session.Publish(UiEvent.Notice("after disconnect"));

        // Nothing further was queued for this reader — it was actually removed from the subscriber list,
        // not merely left to buffer events nobody will ever read.
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void Unsubscribing_one_viewer_does_not_affect_another()
    {
        var session = new RunSession("run-1");
        var first = session.Subscribe();
        var second = session.Subscribe();

        session.Unsubscribe(first);
        session.Publish(UiEvent.Notice("still live"));

        Assert.False(first.TryRead(out _));
        Assert.True(second.TryRead(out _));
    }

    [Fact]
    public void A_session_records_when_it_completed()
    {
        var session = new RunSession("run-1");
        Assert.Null(session.CompletedAt);

        session.Complete();

        Assert.NotNull(session.CompletedAt);
    }
}
