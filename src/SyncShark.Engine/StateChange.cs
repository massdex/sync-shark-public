namespace Massdex.SyncShark.Engine;

/// <summary>Everything one committed effect changes, applied atomically by Storage (section 3.5).</summary>
public sealed record StateChange(
    IReadOnlyList<SyncedEntry> Record,
    IReadOnlyList<(Side Side, string Key)> ForgetSelf,
    IReadOnlyList<(Side Side, string Key)> ForgetSubtrees,
    IReadOnlyList<RenameApplied> Renames,
    IReadOnlyList<(IssueKind Kind, TextKey Cause, string Path, IssueSubject Subject)> Raise,
    IReadOnlyList<(string Path, IReadOnlyList<IssueKind> Kinds)> Close,
    IReadOnlyList<IssueItem> FirstRunAcknowledged)
{
    /// <summary>Exact old bindings whose differing provider paths were both observed absent before Record.</summary>
    public IReadOnlyList<SyncedEntry> StaleBindings { get; init; } = [];

    /// <summary>Absent bilateral roots: forget only bindings inside both corresponding subtrees.</summary>
    public IReadOnlyList<(string LocalKey, string DropboxKey)> ForgetBothSubtrees { get; init; } = [];

    /// <summary>The journal boundary of the engine's own synchronous write of the Local file LocalId; Storage keeps
    /// it only on the Record row holding that identity.</summary>
    public (string LocalId, JournalPosition Position)? LocalPublished { get; init; }

    /// <summary>The binding an executor result leaves and whether it proved its path empty on both sides; Close then
    /// spares an incoming clash this result does not resolve. Null closes path-wide (resolutions, effect owners).</summary>
    public (SyncedEntry? Binding, bool Absent)? CloseScope { get; init; }

    /// <summary>Process-local provenance for a listed Record or Adopt, revalidated after its queue wait.</summary>
    internal Guid? ListedScanId { get; init; }

    /// <summary>Row 43a: only identity-preserving case-only respellings of the wait's own saved file, inside an
    /// ordinary in-use wait with no unknown move: each rename's before-image carries the wait's Local identity and key,
    /// and its Dropbox identity and key when the wait records one. It records, forgets, raises, closes and
    /// acknowledges nothing, so the wait, its notice and its first-run work continue to the content attempt (RQ-032, RQ-035).</summary>
    internal bool RespellsWithin(ItemRetryCandidate wait) =>
        wait is { Dispatch: ItemRetryDispatch.OrdinaryItem, Token.Subject.Move: null } && Renames.Count != 0 &&
        Renames.All(rename => rename.Before is { IsFolder: false } before && !rename.After.IsFolder &&
            wait.Token.Subject.Items.Any(item => item.Side == Side.Local) &&
            wait.Token.Subject.Items.All(item => item.Id is { Length: > 0 } && item.Id == before.IdOn(item.Side) &&
                PathNames.KeyFor(item.Side, item.Path) == before.KeyOn(item.Side)) &&
            before.LocalId == rename.After.LocalId && before.DropboxId == rename.After.DropboxId &&
            before.LocalKey == rename.After.LocalKey && before.DropboxKey == rename.After.DropboxKey &&
            PathNames.Same(before.LocalPath, rename.After.LocalPath) &&
            PathNames.Same(before.DropboxPath, rename.After.DropboxPath) &&
            (before.LocalPath != rename.After.LocalPath || before.DropboxPath != rename.After.DropboxPath)) &&
        Record.Count + ForgetSelf.Count + ForgetSubtrees.Count + Raise.Count + Close.Count +
        StaleBindings.Count + ForgetBothSubtrees.Count == 0 && LocalPublished is null && CloseScope is null;

    /// <summary>A change that records, forgets and closes nothing.</summary>
    public static StateChange Empty { get; } = new([], [], [], [], [], [], []);
}
