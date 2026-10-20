using System.Collections.Generic;
using System.Threading.Tasks;

namespace Massdex.SyncShark;

// The Rewind state loader. It awaits the authority read instead of blocking the dispatcher, and it
// publishes a response only when the refresh generation that requested it is still the current one, so
// a reentrant refresh or a rapid selection switch can never publish another pair's session.
//
// The synchronous render path reads only this explicitly published snapshot. Every mutation and
// admission decision still reads current authority inside the host, so a stale or absent snapshot is
// never execution authority: PrepareRewindAsync itself refuses when an active checkpoint exists.
public partial class MainWindow
{
    private int _rewindRefreshGeneration;
    private bool _rewindRefreshInFlight;
    private bool _rewindRefreshPublishing;
    private bool _rewindSnapshotPublished;
    private string? _rewindRefreshFailure;
    private IReadOnlyList<SyncRewindSession> _rewindSessionsSnapshot = [];

    private IReadOnlyList<SyncRewindSession> RewindSessionsSnapshot => _rewindSessionsSnapshot;
    private bool RewindSnapshotIsCurrent => _rewindSnapshotPublished && !_rewindRefreshInFlight &&
        _rewindRefreshFailure is null;

    // The fixture can delay/fault a read boundary; it never supplies a session or an admission result.
    internal Func<Task>? BeforeRewindReadForTest { get; set; }

    private void BeginRewindRefresh()
    {
        if (_rewindRefreshInFlight || _rewindRefreshPublishing) return;
        _ = RefreshRewindStateAsync();
    }

    internal async Task RefreshRewindStateAsync()
    {
        var generation = ++_rewindRefreshGeneration;
        _rewindRefreshInFlight = true;
        _rewindSnapshotPublished = false;
        _rewindRefreshFailure = null;
        PublishRewindRefreshRender();
        IReadOnlyList<SyncRewindSession> sessions;
        try
        {
            if (BeforeRewindReadForTest is { } beforeRead) await beforeRead();
            if (generation != _rewindRefreshGeneration) return;
            sessions = await _authorityHost.LoadRewindSessionsAsync();
        }
        catch (Exception exception)
        {
            if (generation != _rewindRefreshGeneration) return;
            _rewindRefreshInFlight = false;
            _rewindRefreshFailure = "The retained Rewind checkpoint could not be read: " + exception.Message;
            PublishRewindRefreshRender();
            return;
        }
        // An older result must not clear a newer read's loading state, even when it succeeded.
        if (generation != _rewindRefreshGeneration) return;
        _rewindRefreshInFlight = false;
        _rewindSessionsSnapshot = sessions;
        _rewindSnapshotPublished = true;
        PublishRewindRefreshRender();
    }

    private void PublishRewindRefreshRender()
    {
        _rewindRefreshPublishing = true;
        try
        {
            RefreshRewindState();
        }
        finally
        {
            _rewindRefreshPublishing = false;
        }
    }
}
