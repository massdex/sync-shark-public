namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Process-local authority and borrower lifetime for one disposable scan partition.</summary>
internal sealed class ScanPartition(ScanRecord record, long pairRow)
{
    internal ScanRecord Record { get; } = record;
    internal long PairRow { get; } = pairRow;
    internal byte[] Key { get; } = record.Token.ToByteArray();
    internal bool Active { get; set; } = true;
    internal bool FreshLocal { get; set; } = true;
    internal bool ResumedHistory { get; set; }
    internal bool IncrementalHistory { get; set; }
    internal int Borrowers { get; set; }

    private readonly SortedSet<string>[] _touched = [new(StringComparer.Ordinal), new(StringComparer.Ordinal)];
    private readonly PrefixSet[] _roots = [new(), new()];

    internal void Touch(Side side, string key, bool subtree)
    {
        if (!Active || Record.Scope is not null) return;
        _touched[(int)side].Add(key);
        if (subtree) _roots[(int)side].Add(key);
    }

    internal bool CanUse(string localKey, string dropboxKey, bool descendants, SyncedEntry? before) =>
        Active && Record.Scope is null && !Touches(Side.Local, localKey, descendants) &&
        !Touches(Side.Dropbox, dropboxKey, descendants) &&
        (before is null || (before.LocalKey == localKey || !Touches(Side.Local, before.LocalKey, descendants)) &&
            (before.DropboxKey == dropboxKey || !Touches(Side.Dropbox, before.DropboxKey, descendants)));

    internal void Guard(StateChange change)
    {
        if (change.ListedScanId != Record.Token) return;
        if (change.ForgetSelf.Count != 0 || change.ForgetSubtrees.Count != 0 ||
            change.ForgetBothSubtrees.Count != 0 || change.StaleBindings.Count != 0 ||
            change.Renames.Any(rename => rename.CounterpartSide is not null || rename.Before is null))
            throw new ArgumentException(null, nameof(change));
        foreach (var entry in change.Record)
            if (!CanUse(entry.LocalKey, entry.DropboxKey, false, null))
                throw new ChangedUnderneathException(entry.LocalPath);
        foreach (var rename in change.Renames)
            if (!CanUse(rename.After.LocalKey, rename.After.DropboxKey, rename.After.IsFolder, rename.Before))
                throw new ChangedUnderneathException(rename.After.LocalPath);
    }

    internal void Touch(SyncedEntry entry, StateChange change, bool retiring = false)
    {
        var roots = retiring && entry.IsFolder;
        if (!roots && change.ListedScanId == Record.Token) return;
        Touch(Side.Local, entry.LocalKey, roots);
        Touch(Side.Dropbox, entry.DropboxKey, roots);
    }

    private bool Touches(Side side, string key, bool descendants)
    {
        var keys = _touched[(int)side];
        return keys.Contains(key) || _roots[(int)side].ResponsibilityOverlaps(key, false) ||
            descendants && (key.Length == 0 ? keys.Count != 0 : PathNames.HasDescendantKey(keys, key));
    }
}
