namespace Massdex.SyncShark.V2;

/// <summary>An incremental set of changes since a cursor.</summary>
public sealed record Delta(string RootId, Side Side, long Gen, string CursorBefore, string CursorAfter,
    IReadOnlyList<Change> Changes, bool CursorInvalid, IReadOnlyList<string> IncompleteSubtrees)
{
    public bool JournalBacklog { get; init; }
    public IReadOnlyDictionary<string, string> IncompleteReasons { get; init; } = new Dictionary<string, string>();
    // Local partial refreshes use the same evidence for displaced objects as for missing rows.
    internal Snapshot? DisplacementEvidence { get; init; }
    internal Action<string>? ChangedObject { get; init; }

    /// <summary>Held paths whose retry bound expired; these no longer retain the side's cursor.</summary>
    public IReadOnlySet<string> ReleasedDeletionEvidence { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Unresolved deletion lookups, excluded from Changes and replayed from the retained cursor.</summary>
    public IReadOnlyDictionary<string, string> PendingDeletionEvidence { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>The scanned scope, also protecting excluded descendants of a deleted folder.</summary>
    public Func<string, bool>? IncludesPath { get; init; }
}
