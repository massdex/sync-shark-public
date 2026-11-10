using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Mirrors StoredFolderPlan over the in-memory store: the same phases, terminal-read checks, accepted inputs,
/// SQLite BINARY orders and saved-binding joins, with the plan's rows held in memory instead of plan scratch.</summary>
internal sealed class InMemoryFolderPlan : FolderPlan
{
    private readonly InMemoryStateStore _store;
    private readonly string _pairId;
    private readonly string _rootKey;
    private readonly string _rootNativeKey;
    private readonly StoredText _text = new();
    private readonly SortedSet<string> _capturedKeys;
    private readonly Dictionary<string, Entry> _captured = new(StringComparer.Ordinal);
    private readonly List<Classified> _classified = [];
    private readonly SortedSet<string> _survivorKeys;
    private readonly HashSet<string> _selectedKeys = new(StringComparer.Ordinal);
    private Entry[] _inventory = [];
    private Classified[] _ordered = [];
    private Classified[] _deepestSelected = [];
    private Phase _phase;
    private Classified? _current;
    private bool _marked;
    private bool _hasSurvivors;
    private bool _deleteWhole;

    internal InMemoryFolderPlan(InMemoryStateStore store, string pairId, Row folder, Side deletingFrom)
    {
        _store = store;
        _pairId = pairId;
        _capturedKeys = new(_text);
        _survivorKeys = new(_text);
        Folder = folder;
        DeletingFrom = deletingFrom;
        var rootPath = folder.PathOn(deletingFrom)!;
        _rootNativeKey = _text.Stored(PathNames.Key(rootPath));
        _rootKey = _text.Stored(PathNames.KeyFor(deletingFrom, rootPath, PathNames.Key(rootPath)));
    }

    public Row Folder { get; private set; }
    public Side DeletingFrom { get; }

    public bool DeleteWhole
    {
        get
        {
            Require(Phase.Completed);
            return _deleteWhole;
        }
    }

    public void PrepareRemoval(Row observed)
    {
        Require(Phase.Captured);
        if (observed.EntryOn(DeletingFrom) is not { IsFolder: true } root ||
            _text.Stored(PathNames.KeyFor(DeletingFrom, root.Path)) != _rootKey ||
            _text.Stored(PathNames.Key(root.Path)) != _rootNativeKey)
            throw new InvalidOperationException();
        Folder = observed;
    }

    public async Task CaptureAsync(IAsyncEnumerable<Listed> source, CancellationToken ct)
    {
        Require(Phase.New);
        _phase = Phase.Capturing;
        IAsyncEnumerator<Listed>? iterator = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var pairKnown = _store.Pair(_pairId) is not null;
            iterator = source.GetAsyncEnumerator(ct);
            while (await iterator.MoveNextAsync().ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var item = iterator.Current;
                if (IssuePolicy.ProtectsSubtree(item.Entry?.Skip))
                    throw new InventoryIncompleteException(Text.Get(Text.ReadFailedOn(DeletingFrom),
                        (TextArg.Path, item.Path), (TextArg.Message, "")), null);
                Apply(item, pairKnown);
            }
            var completed = iterator;
            iterator = null;
            await completed.DisposeAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _inventory = _capturedKeys.Select(key => _captured[key]).ToArray();
            _phase = Phase.Captured;
        }
        catch
        {
            if (iterator is not null)
            {
                try { await iterator.DisposeAsync().ConfigureAwait(false); }
                catch { /* Preserve the capture failure. */ }
            }
            Dispose();
            throw;
        }
    }

    // ScanEntries.Apply into the scan table: the event's path is stored, mtime is UTC ticks, and the pair, id, rev
    // and hash columns are NOT NULL, so a row the table refuses fails the capture. Every text column reads back as
    // stored text; a packed id or hash has no surrogates, so it is unchanged.
    private void Apply(Listed item, bool pairKnown)
    {
        var key = _text.Stored(PathNames.Key(item.Path));
        var entry = item.Entry;
        if (entry is not { IsFolder: true } && Descendants(_capturedKeys, key).ToList() is { Count: > 0 } replaced)
            foreach (var under in replaced)
            {
                _capturedKeys.Remove(under);
                _captured.Remove(under);
            }
        if (entry is null)
        {
            _capturedKeys.Remove(key);
            _captured.Remove(key);
            return;
        }
        if (!pairKnown || entry.Id is null || entry.Revision is null || entry.ContentHash is null)
            throw new InvalidDataException();
        _capturedKeys.Add(key);
        _captured[key] = entry with { Path = _text.Stored(item.Path), Modified = new DateTimeOffset(entry.Modified.UtcTicks, TimeSpan.Zero) };
    }

    public IEnumerable<Entry> Entries(CancellationToken ct)
    {
        Require(Phase.Captured);
        for (var index = 0; ; index++)
        {
            Require(Phase.Captured);
            ct.ThrowIfCancellationRequested();
            if (index == _inventory.Length) yield break;
            yield return _inventory[index];
        }
    }

    public IEnumerable<(string Key, string NativeKey, Entry? Entry, SyncedEntry? Synced)> Children(CancellationToken ct)
    {
        Require(Phase.Captured);
        _phase = Phase.Classifying;
        var exhausted = false;
        try
        {
            // One classification snapshot of the bindings under the root, joined on the deleting side's key.
            var saved = _store.SyncedUnder(_pairId, DeletingFrom, _rootKey);
            var byKey = new Dictionary<string, SyncedEntry>(StringComparer.Ordinal);
            foreach (var binding in saved) byKey.TryAdd(_text.Stored(binding.KeyOn(DeletingFrom)), binding);
            var present = _capturedKeys.Where(key => key != _rootNativeKey)
                .Select(key => new Classified(key, key, _captured[key], byKey.GetValueOrDefault(key)));
            // PlanSaved: a Dropbox plan streams Local-keyed bindings before explicit Dropbox overrides.
            var absent = byKey.Where(pair => !_captured.ContainsKey(pair.Key))
                .OrderBy(pair => DeletingFrom == Side.Dropbox && pair.Value.DropboxKey != pair.Value.LocalKey)
                .ThenBy(pair => pair.Key, _text)
                .Select(pair => new Classified(pair.Key,
                    _text.Stored(PathNames.Key(pair.Value.PathOn(DeletingFrom))), null, pair.Value));
            foreach (var group in new[] { present, absent })
                foreach (var candidate in Terminated(group))
                {
                    Require(Phase.Classifying);
                    ct.ThrowIfCancellationRequested();
                    if (candidate is null) break;
                    _current = candidate;
                    _marked = false;
                    yield return (candidate.Key, candidate.NativeKey, candidate.Entry, candidate.Synced);
                    if (!_marked) throw new InvalidOperationException();
                    _current = null;
                }
            _phase = Phase.Classified;
            exhausted = true;
        }
        finally
        {
            _current = null;
            if (!exhausted && _phase != Phase.Disposed) _phase = Phase.Faulted;
        }
    }

    public void MarkClassified(string key, string nativeKey, FolderChild child, bool followUp, CancellationToken ct)
    {
        Require(Phase.Classifying);
        ct.ThrowIfCancellationRequested();
        if (_current is not { } current || _marked || key != current.Key || nativeKey != current.NativeKey ||
            child.OnDeletingSide != current.Entry || child.Synced != current.Synced)
            throw new InvalidOperationException();
        // The classified table's verdict CHECK refuses the insert, and a failed insert discards the plan.
        if (!Enum.IsDefined(child.Verdict))
        {
            Dispose();
            throw new InvalidDataException();
        }
        _classified.Add(current with { Id = _classified.Count + 1, Verdict = child.Verdict, FollowUp = followUp });
        if (child.Verdict == FolderVerdict.Survives) _survivorKeys.Add(current.Key);
        _marked = true;
        _hasSurvivors |= child.Verdict == FolderVerdict.Survives;
    }

    public void PrepareBranches(CancellationToken ct)
    {
        Require(Phase.Classified);
        ct.ThrowIfCancellationRequested();
        if (!_hasSurvivors) throw new InvalidOperationException();
        _phase = Phase.Prepared;
    }

    public IEnumerable<(long Id, string Key, FolderChild Child)> RemovedChildren(CancellationToken ct)
    {
        Require(Phase.Prepared);
        _phase = Phase.Selecting;
        var exhausted = false;
        try
        {
            var depths = _classified.Where(row => row.Verdict == FolderVerdict.Removed)
                .GroupBy(row => PathNames.Depth(row.Key)).OrderBy(group => group.Key)
                .Select(group => group.OrderBy(row => row.Order, _text).ToArray());
            foreach (var depth in Terminated(depths))
            {
                Require(Phase.Selecting);
                ct.ThrowIfCancellationRequested();
                if (depth is null) break;
                foreach (var row in Terminated(depth))
                {
                    Require(Phase.Selecting);
                    ct.ThrowIfCancellationRequested();
                    if (row is null) break;
                    _current = row;
                    _marked = false;
                    yield return (row.Id, row.Key, new FolderChild(row.Entry, row.Synced, FolderVerdict.Removed));
                    _current = null;
                }
            }
            _phase = Phase.Selected;
            exhausted = true;
        }
        finally
        {
            _current = null;
            if (!exhausted && _phase != Phase.Disposed) _phase = Phase.Faulted;
        }
    }

    public bool HasSurvivorAtOrBelow(string key, CancellationToken ct)
    {
        Require(Phase.Selecting);
        ct.ThrowIfCancellationRequested();
        return _survivorKeys.Contains(key) || Descendants(_survivorKeys, key).Any();
    }

    public bool HasSelectedAncestor(string key, CancellationToken ct)
    {
        Require(Phase.Selecting);
        ct.ThrowIfCancellationRequested();
        while (PathNames.Parent(key) is var parent && PathNames.IsUnderKey(key, parent))
        {
            key = parent;
            if (key == _rootKey) break;
            if (_selectedKeys.Contains(key)) return true;
        }
        return false;
    }

    public void SelectBranch(long id, CancellationToken ct)
    {
        Require(Phase.Selecting);
        ct.ThrowIfCancellationRequested();
        if (_current is not { } current || current.Id != id || _marked) throw new InvalidOperationException();
        _classified[(int)id - 1] = _classified[(int)id - 1] with { Selected = true };
        _selectedKeys.Add(current.Key);
        _marked = true;
    }

    public void Complete(bool deleteWhole, CancellationToken ct)
    {
        if (deleteWhole)
        {
            Require(Phase.Classified);
            if (_hasSurvivors) throw new InvalidOperationException();
        }
        else Require(Phase.Selected);
        try
        {
            ct.ThrowIfCancellationRequested();
            _ordered = _classified.OrderBy(row => row.Order, _text).ToArray();
            _deepestSelected = _ordered.Where(row => row.Selected)
                .OrderByDescending(row => PathNames.Depth(row.Key)).ToArray();
            _deleteWhole = deleteWhole;
            _phase = Phase.Completed;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool ForgetTouchesUnreadable(CancellationToken ct)
    {
        Require(Phase.Completed);
        try
        {
            return _store.ForgetTouchesUnreadable(_pairId, ForgetRoots(), ct);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IEnumerable<FrozenFolderEntry> FrozenEvidence(int pageSize, CancellationToken ct)
    {
        Require(Phase.Completed);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        return FrozenRows(pageSize, ct);
    }

    private IEnumerable<FrozenFolderEntry> FrozenRows(int pageSize, CancellationToken ct)
    {
        for (var start = 0; ; start += pageSize)
        {
            Require(Phase.Completed);
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(pageSize, _ordered.Length - start);
            if (count <= 0) yield break;
            for (var index = start; index < start + count; index++)
            {
                Require(Phase.Completed);
                ct.ThrowIfCancellationRequested();
                var row = _ordered[index];
                yield return new FrozenFolderEntry(index + 1, row.Key, row.NativeKey, row.Entry, row.Synced,
                    row.Verdict, row.FollowUp, row.Selected);
            }
        }
    }

    public IEnumerable<FolderChild> Branches(CancellationToken ct)
    {
        Require(Phase.Completed);
        return _deleteWhole ? Array.Empty<FolderChild>() : FrozenChildren(_deepestSelected, FolderVerdict.Removed, ct);
    }

    public IEnumerable<FolderChild> Survivors(CancellationToken ct)
    {
        Require(Phase.Completed);
        return _deleteWhole ? Array.Empty<FolderChild>() : FrozenChildren(
            _ordered.Where(row => row.Verdict == FolderVerdict.Survives && row.FollowUp), FolderVerdict.Survives, ct);
    }

    // StoredFolderPlan.FrozenChildren: an empty partial result is not cancelled, so the token is read after the
    // terminal check only for a row that exists.
    private IEnumerable<FolderChild> FrozenChildren(IEnumerable<Classified> rows, FolderVerdict verdict, CancellationToken ct)
    {
        foreach (var row in Terminated(rows))
        {
            Require(Phase.Completed);
            if (row is null) yield break;
            ct.ThrowIfCancellationRequested();
            yield return new FolderChild(row.Entry, row.Synced, verdict);
        }
    }

    // StoredFolderPlan.ForgetRoots: the whole root or each selected branch, with its other side only through a binding.
    private IEnumerable<(Side Side, string Key)> ForgetRoots()
    {
        var roots = _deleteWhole
            ? new[] { (Key: _rootKey, Synced: Folder.Synced) } : _deepestSelected.Select(row => (row.Key, row.Synced));
        foreach (var (key, saved) in roots)
        {
            yield return (DeletingFrom, saved?.KeyOn(DeletingFrom) ?? key);
            if (saved is not null) yield return (PathNames.Other(DeletingFrom), saved.KeyOn(PathNames.Other(DeletingFrom)));
        }
    }

    // Every stored stream ends on a read that finds nothing; null stands for that read so it gets the same checks.
    private static IEnumerable<T?> Terminated<T>(IEnumerable<T> items) where T : class =>
        items.Append<T?>(null);

    private static IEnumerable<string> Descendants(SortedSet<string> keys, string key)
    {
        var (low, high) = PathNames.DescendantBounds(key);
        return keys.GetViewBetween(low, high).Where(under => under != high);
    }

    private void Require(Phase phase)
    {
        ObjectDisposedException.ThrowIf(_phase == Phase.Disposed, this);
        if (_phase != phase) throw new InvalidOperationException();
    }

    public void Dispose()
    {
        _phase = Phase.Disposed;
        _current = null;
    }

    private sealed record Classified(string Key, string NativeKey, Entry? Entry, SyncedEntry? Synced)
    {
        public long Id { get; init; }
        public FolderVerdict Verdict { get; init; }
        public bool FollowUp { get; init; }
        public bool Selected { get; init; }
        public (string Key, string NativeKey, bool Present) Order => (Key, NativeKey, Entry is not null);
    }

    /// <summary>Text as SQLite holds it: Microsoft.Data.Sqlite binds a string through SQLitePCLRaw's
    /// sqlite3_bind_text(string), which encodes with Encoding.UTF8 (a lone surrogate becomes U+FFFD), and BINARY
    /// compares those bytes.</summary>
    private sealed class StoredText : IComparer<string>, IComparer<(string Key, string NativeKey, bool Present)>
    {
        // Only text with surrogates can change on the round trip, so only it pays for the encode.
        [return: NotNullIfNotNull(nameof(text))]
        public string? Stored(string? text) =>
            text is not null && text.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF') >= 0
                ? Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)) : text;

        // UTF-8 bytes order as their scalar values do, and a lone surrogate enumerates as U+FFFD as Encoding.UTF8
        // writes it, so comparing runes gives the BINARY order without encoding either operand.
        public int Compare(string? x, string? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);
            var left = x.EnumerateRunes();
            var right = y.EnumerateRunes();
            while (true)
            {
                var more = left.MoveNext();
                if (more != right.MoveNext()) return more ? 1 : -1;
                if (!more) return 0;
                var order = left.Current.Value.CompareTo(right.Current.Value);
                if (order != 0) return order;
            }
        }

        public int Compare((string Key, string NativeKey, bool Present) x, (string Key, string NativeKey, bool Present) y)
        {
            var key = Compare(x.Key, y.Key);
            if (key != 0) return key;
            var native = Compare(x.NativeKey, y.NativeKey);
            return native != 0 ? native : x.Present.CompareTo(y.Present);
        }
    }

    private enum Phase
    {
        New,
        Capturing,
        Captured,
        Classifying,
        Classified,
        Prepared,
        Selecting,
        Selected,
        Completed,
        Faulted,
        Disposed
    }
}
