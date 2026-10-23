namespace Massdex.SyncShark.V2;

/// <summary>A complete inventory of one root; an empty IncompleteSubtrees means complete.</summary>
public sealed record Snapshot(string RootId, Side Side, long Gen, string CursorAfter,
    IReadOnlyList<ItemState> Items, IReadOnlyList<string> IncompleteSubtrees)
{
    public IReadOnlyDictionary<string, string> IncompleteReasons { get; init; } = new Dictionary<string, string>();
    internal Func<LocalFileAbsence>? OpenAbsenceVerifier { get; init; }
    internal Func<ItemState, FileAbsence>? VerifyAbsence { get; init; }
    // The snapshot writer emits only its actual change set, not unchanged observations.
    internal Action<string>? ChangedObject { get; init; }
    internal Action? AllItemsChanged { get; init; }
    internal Dictionary<string, string> PendingDeletionEvidence { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal bool CanCloseAbsent(ItemState item)
    {
        if (IncompleteSubtrees.Count == 0) return true;
        var evidence = FileAbsence.Unknown;
        try { evidence = VerifyAbsence?.Invoke(item) ?? FileAbsence.Unknown; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (evidence == FileAbsence.Gone) return true;
        PendingDeletionEvidence[item.Path] = evidence == FileAbsence.Present
            ? "The file identity still exists, possibly moved into an unscanned folder. Kept; restore folder access and Retry, or Ignore."
            : "Absence could not be verified by file identity after a partial scan. Kept; restore access and Retry, or Ignore.";
        return false;
    }

    /// <summary>The scanned scope. Outside it, omission is not absence; excluded rows stay untouched.</summary>
    public Func<string, bool>? IncludesPath { get; init; }
}
