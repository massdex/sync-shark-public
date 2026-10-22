using System.Windows;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    // SS-D12: the synchronous presentation scope. ONE outer entry establishes one validated snapshot and
    // one captured clock for a whole refresh; every nested rendering entry joins that same scope instead of
    // reading the stores again. The scope never crosses an await: it is a plain field pair on the UI thread,
    // it is always disposed in a finally, and nothing is retained between outer refreshes. A failed load
    // throws before any snapshot exists, so an earlier successful projection can never be reused.
    private SyncPresentationSnapshot? _presentationSnapshot;
    private int _presentationScopeDepth;
    private DateTimeOffset _presentationCapturedUtc;
    private IReadOnlyList<WorkspaceAttentionCase>? _presentationAttentionCases;

    /// <summary>The snapshot the current presentation pass established, or null outside a pass.</summary>
    internal SyncPresentationSnapshot? CurrentPresentationSnapshot => _presentationSnapshot;

    /// <summary>
    /// Enters the synchronous presentation scope, or joins the one already open. The snapshot and the
    /// captured current time are established only on the outermost entry.
    /// </summary>
    internal IDisposable OpenPresentationScope()
    {
        if (_presentationScopeDepth == 0)
        {
            // A rejected load throws here and leaves no snapshot, so no earlier successful binding or
            // preference is ever presented as current. Callers surface the rejection through their own path.
            _presentationSnapshot = _authorityHost.LoadPresentationSnapshot();
            _presentationCapturedUtc = DateTimeOffset.UtcNow;
            _presentationAttentionCases = null;
        }
        _presentationScopeDepth++;
        return new PresentationScope(this, outermost: _presentationScopeDepth == 1);
    }

    // One synchronous presentation read, reusing the open scope or opening one bounded scope that is
    // disposed before the result returns. Nothing is memoised outside an open scope.
    private T WithPresentationSnapshot<T>(Func<SyncPresentationSnapshot, T> read)
    {
        if (_presentationSnapshot is { } current) return read(current);
        using var scope = OpenPresentationScope();
        return read(_presentationSnapshot!);
    }

    /// <summary>
    /// The immutable attention result of the current presentation pass. It is derived once per outer scope
    /// when first needed, so the shared Home, scope, attention and tray consumers reuse one evaluation.
    /// Outside a scope the existing builder is materialised for that single read and nothing is retained, so
    /// no lazy iterator escapes and no stale result survives a refresh.
    /// </summary>
    internal IReadOnlyList<WorkspaceAttentionCase> PresentationAttentionCases
    {
        get
        {
            if (_presentationScopeDepth == 0) return BuildAttentionCases().ToArray();
            return _presentationAttentionCases ??= BuildAttentionCases().ToArray();
        }
    }

    /// <summary>The display-only account metadata for the current pass, in the existing picker order.</summary>
    internal IReadOnlyList<SyncAccountBinding> PresentationAccounts =>
        WithPresentationSnapshot(snapshot => snapshot.OrderedAccounts);

    internal SyncAccountBinding? PresentationAccountBinding(string? connectionId) =>
        WithPresentationSnapshot(snapshot => snapshot.Account(connectionId));

    internal bool PresentationAccountPaused(string? connectionId) =>
        WithPresentationSnapshot(snapshot => snapshot.AccountPaused(connectionId, _presentationCapturedUtc));

    internal bool PresentationPairAccountPaused(SyncPair pair) =>
        WithPresentationSnapshot(snapshot => snapshot.PairAccountPaused(snapshot.PairConnectionIds(pair),
            _presentationCapturedUtc));

    internal IReadOnlyList<string> PresentationPairConnectionIds(SyncPair pair) =>
        WithPresentationSnapshot(snapshot => snapshot.PairConnectionIds(pair));

    // The one captured instant of the current pass, so every pause projection in one refresh agrees.
    private DateTimeOffset PresentationNowUtc => _presentationSnapshot is null
        ? DateTimeOffset.UtcNow : _presentationCapturedUtc;

    private UiPreferences PresentationPreferences =>
        WithPresentationSnapshot(snapshot => snapshot.Preferences);

    private sealed class PresentationScope(MainWindow owner, bool outermost) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._presentationScopeDepth--;
            if (!outermost || owner._presentationScopeDepth > 0) return;
            // Nothing is cached across outer refreshes, including a rejected or empty projection.
            owner._presentationSnapshot = null;
            owner._presentationAttentionCases = null;
        }
    }
}
