using System.Globalization;
using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>An in-memory ISide for Executor and Run checks: entries by path, with call counts for mutations.</summary>
public sealed class FakeSide : ISide
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Exception> _failures = new();
    private readonly Dictionary<string, (Exception Error, int Walks)> _walkFailures = new();
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Dictionary<string, byte[]> _content = new();
    private readonly bool _local;
    private long _nextId;

    public FakeSide(Side side)
    {
        Side = side;
        _local = side == Massdex.SyncShark.Engine.Side.Local;
    }

    public Side Side { get; }
    public int StagesDisposed { get; private set; }

    /// <summary>Whether a folder ensure also creates missing ancestors, the seeding shortcut most checks use. False
    /// behaves like the native Local adapter, which fails rather than recreate a vanished parent (B-140A R1).</summary>
    public bool CreatesMissingParents { get; set; } = true;

    /// <summary>Whether a Local publish over a file or a Local delete verifies the removal proof's content hash against
    /// the stored bytes, as the native adapter does (LocalSide.RequireSelection and VerifySelectedAsync): a missing
    /// proof throws, a proof bound to another entry or other bytes refuses as changed underneath. Off by default, so
    /// existing checks keep their metadata-only fake.</summary>
    public bool VerifiesRemovalProof { get; set; }

    // Caller holds _gate.
    private void VerifyProofLocked(string key, Entry expected, Entry current, RemovalSelection? selection)
    {
        if (!VerifiesRemovalProof || !_local || current.IsFolder) return;
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Proof.Expected != expected) throw new ChangedUnderneathException(expected.Path);
        var actual = DropboxContentHash.Compute(_content.GetValueOrDefault(key) ?? []);
        if (actual != selection.Proof.ContentHash)
            throw new ChangedUnderneathException(current.Path) { ObservedLocal = current with { ContentHash = actual } };
    }

    /// <summary>Puts an entry (any Skip, Id or Revision) and its content in place, as the checks need it; parents are
    /// not created.</summary>
    public void Seed(Entry entry, byte[]? content)
    {
        lock (_gate)
        {
            var key = PathNames.Key(entry.Path);
            _entries[key] = entry;
            if (content is null) _content.Remove(key);
            else _content[key] = content;
        }
    }

    /// <summary>The next call that touches path throws error (once).</summary>
    public void FailNext(string path, Exception error)
    {
        lock (_gate) _failures[PathNames.Key(path)] = error;
    }

    /// <summary>A walk yielding path fails right after it, once earlierWalks walks have passed it, with no directory
    /// reported as acquired.</summary>
    public void FailWalkAfter(string path, Exception error, int earlierWalks = 0)
    {
        lock (_gate) _walkFailures[PathNames.Key(path)] = (error, earlierWalks);
    }

    private void ThrowIfFailing(params string[] paths)
    {
        lock (_gate)
            foreach (var path in paths)
                if (_failures.Remove(PathNames.Key(path), out var error)) throw error;
    }

    public async IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ThrowIfFailing(under);
        var underKey = PathNames.Key(under);
        List<Entry> snapshot;
        // Filter on the stored keys: recomputing a key per entry made every folder capture cost the whole tree.
        lock (_gate)
            snapshot = _entries.Where(item => IsUnder(item.Key, underKey, options.Recursive))
                .Select(item => item.Value).ToList();
        await Task.CompletedTask;
        foreach (var entry in snapshot.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            yield return new Listed(entry.Path, entry);
            lock (_gate)
                if (_walkFailures.TryGetValue(PathNames.Key(entry.Path), out var failure))
                {
                    if (failure.Walks == 0)
                    {
                        _walkFailures.Remove(PathNames.Key(entry.Path));
                        throw failure.Error;
                    }
                    _walkFailures[PathNames.Key(entry.Path)] = (failure.Error, failure.Walks - 1);
                }
        }
        if (options.DirectoryAcquired is not { } acquired || !options.Recursive) yield break;
        ct.ThrowIfCancellationRequested();
        Entry? root;
        lock (_gate) root = _entries.GetValueOrDefault(underKey);
        foreach (var folder in Descended(root, snapshot)) acquired(folder.Path, folder.Id);
    }

    // Only directories the walk truly descends: never one skipped, or under one skipped, and never the pair root.
    private static IEnumerable<Entry> Descended(Entry? root, IEnumerable<Entry> listed)
    {
        var skipped = new PrefixSet();
        foreach (var entry in (root is null ? listed : listed.Prepend(root)).OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            if (!entry.IsFolder || skipped.Covers(PathNames.Key(entry.Path))) continue;
            if (entry.Skip is not null) skipped.Add(PathNames.Key(entry.Path));
            else yield return entry;
        }
    }

    public Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfFailing(path);
        lock (_gate)
        {
            if (!_entries.TryGetValue(PathNames.Key(path), out var entry)) return Task.FromResult<Entry?>(null);
            var parent = ParentOf(path);
            if (parent.Length > 0 &&
                (!_entries.TryGetValue(PathNames.Key(parent), out var folder) || !folder.IsFolder))
                return Task.FromResult<Entry?>(null);
            return Task.FromResult<Entry?>(entry);
        }
    }

    public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfFailing(entry.Path);
        lock (_gate)
        {
            if (!_content.TryGetValue(PathNames.Key(entry.Path), out var content))
                throw new ChangedUnderneathException(entry.Path);
            return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }

    public Task<string> ContentHashAsync(Entry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfFailing(entry.Path);
        byte[] content;
        lock (_gate)
        {
            if (!_content.TryGetValue(PathNames.Key(entry.Path), out var stored))
                throw new ChangedUnderneathException(entry.Path);
            content = stored;
        }
        return Task.FromResult(DropboxContentHash.Compute(content));
    }

    public async Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfFailing(path);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        return new Staged(path, buffer.ToArray(), source, this);
    }

    public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfFailing(staged.Path);
        if (staged is not Staged stage) throw new ArgumentException(nameof(staged));
        lock (_gate)
        {
            var key = PathNames.Key(stage.Path);
            _entries.TryGetValue(key, out var current);
            if (expected is null)
            {
                if (current is not null) throw new PathOccupiedException(stage.Path);
            }
            else if (current is null || !Matches(current, expected))
            {
                throw new ChangedUnderneathException(stage.Path);
            }
            else VerifyProofLocked(key, expected, current, selection);

            if (Side == Side.Local && expected is { IsFolder: true })
            {
                if (selection is not null) throw new ArgumentException(null, nameof(selection));
                RemoveSubtree(key);
                current = null;
            }
            var entry = NewFile(stage, current);
            _entries[key] = entry;
            _content[key] = stage.Content;
            return Task.FromResult(new RemovalResult(RemovalOutcome.Published, null, entry, null));
        }
    }

    public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfFailing(path);
        lock (_gate) return Task.FromResult(EnsureFolderLocked(path));
    }

    public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfFailing(expected.Path);
        lock (_gate)
        {
            var key = PathNames.Key(expected.Path);
            if (!_entries.TryGetValue(key, out var current) || !MatchesDelete(current, expected))
                throw new ChangedUnderneathException(expected.Path);
            VerifyProofLocked(key, expected, current, selection);
            RemoveSubtree(key);
            return Task.FromResult(new RemovalResult(RemovalOutcome.Removed, null, null, null));
        }
    }

    public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfFailing(expected.Path, newPath);
        lock (_gate)
        {
            var key = PathNames.Key(expected.Path);
            if (!_entries.TryGetValue(key, out var current)) throw new ChangedUnderneathException(expected.Path);
            var destination = PathNames.Key(newPath);
            if (destination != key && _entries.ContainsKey(destination)) throw new PathOccupiedException(newPath);

            var moved = current with { Path = newPath };
            _content.TryGetValue(key, out var movedContent);
            var children = _entries
                .Where(p => p.Key != key && p.Key.StartsWith(key + PathNames.PathSeparator, StringComparison.Ordinal))
                .Select(p => (Entry: p.Value, Content: _content.GetValueOrDefault(p.Key)))
                .ToList();

            RemoveSubtree(key);
            _entries[destination] = moved;
            if (movedContent is not null) _content[destination] = movedContent;
            foreach (var (child, content) in children)
            {
                var movedChild = child with { Path = newPath + child.Path[current.Path.Length..] };
                var childKey = PathNames.Key(movedChild.Path);
                _entries[childKey] = movedChild;
                if (content is not null) _content[childKey] = content;
            }
            return Task.FromResult(moved);
        }
    }

    public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
        IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct)
    {
        if (Side == Side.Local) throw new NotSupportedException();
        ct.ThrowIfCancellationRequested();
        if (submission.Phase != FolderSubmission.SubmissionPhase.Armed || effects.Count == 0)
            throw new ArgumentException(null, nameof(effects));
        var paths = new string[effects.Count];
        for (var ordinal = 0; ordinal < effects.Count; ordinal++)
            paths[ordinal] = WirePath(effects[ordinal]);
        ThrowIfFailing(paths);
        lock (_gate)
        {
            var results = new FolderMemberResult[effects.Count];
            for (var ordinal = 0; ordinal < effects.Count; ordinal++)
                results[ordinal] = RemoveMember(effects[ordinal], ordinal);
            return Task.FromResult<FolderSubmitResult>(new FolderSubmitResult.Terminal(results));
        }
    }

    // The in-memory provider completes every batch synchronously and issues no job id, so an observation names a
    // job it never had; DropboxWriter maps that same provider refusal to this cause.
    public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
        RequestAttempt attempt, CancellationToken ct)
    {
        if (Side == Side.Local) throw new NotSupportedException();
        ct.ThrowIfCancellationRequested();
        throw new ItemFailedException(string.Empty, TextKey.FailDropboxDeleteJobInvalid, null);
    }

    // Only a recursive folder or a conditional file is a wire member (DropboxWriter's WireEntry).
    private static string WirePath(FolderRemovalEffect effect)
    {
        var expected = effect.Expected ?? throw new ArgumentException(null, nameof(effect));
        var shaped = effect.Kind switch
        {
            FolderEffectKind.RecursiveFolder => expected.IsFolder,
            FolderEffectKind.ConditionalFile => !expected.IsFolder && expected.Revision.Length != 0,
            _ => false
        };
        if (!shaped) throw new ArgumentException(null, nameof(effect));
        return expected.Path;
    }

    private FolderMemberResult RemoveMember(FolderRemovalEffect effect, int ordinal)
    {
        var expected = effect.Expected!;
        var key = PathNames.Key(expected.Path);
        if (!_entries.TryGetValue(key, out var current))
            return new FolderMemberResult.Unknown(ordinal, TextKey.FailChanged);
        if (effect.Kind == FolderEffectKind.RecursiveFolder ? !current.IsFolder : current.Revision != expected.Revision)
            return new FolderMemberResult.Unknown(ordinal, TextKey.FailChanged);
        RemoveSubtree(key);
        return new FolderMemberResult.Receipt(ordinal, expected);
    }

    private static bool IsUnder(string pathKey, string underKey, bool recursive)
    {
        if (underKey.Length == 0)
            return recursive || pathKey.IndexOf(PathNames.PathSeparator, StringComparison.Ordinal) < 0;
        var prefix = underKey + PathNames.PathSeparator;
        if (!pathKey.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return recursive || pathKey.IndexOf(PathNames.PathSeparator, prefix.Length, StringComparison.Ordinal) < 0;
    }

    private static string ParentOf(string path)
    {
        var separator = path.LastIndexOf(PathNames.PathSeparator, StringComparison.Ordinal);
        return separator < 0 ? "" : path[..separator];
    }

    private Entry EnsureFolderLocked(string path)
    {
        var key = PathNames.Key(path);
        if (_entries.TryGetValue(key, out var existing))
        {
            if (!existing.IsFolder) throw new PathOccupiedException(path);
            return existing;
        }
        var parent = ParentOf(path);
        if (parent.Length > 0)
        {
            if (CreatesMissingParents) EnsureFolderLocked(parent);
            else if (!(_entries.TryGetValue(PathNames.Key(parent), out var above) && above.IsFolder))
                throw new ChangedUnderneathException(path);
        }
        var folder = new Entry(path, true, 0, default, NextId(), "", "", null);
        _entries[key] = folder;
        return folder;
    }

    private Entry NewFile(Staged stage, Entry? current)
    {
        var id = !_local && current is not null && !current.IsFolder ? current.Id : NextId();
        return _local
            ? new Entry(stage.Path, false, stage.Content.Length, stage.Source.Modified, id, "", "", null)
            : new Entry(stage.Path, false, stage.Content.Length, stage.Source.Modified, id, NextId(),
                DropboxContentHash.Compute(stage.Content), null);
    }

    private bool Matches(Entry current, Entry expected) =>
        current.IsFolder == expected.IsFolder &&
        (_local
            ? current.Id == expected.Id && current.Size == expected.Size && current.Modified == expected.Modified
            : current.Revision == expected.Revision);

    // Dropbox folders have no delete precondition beyond the path existing (section 2.5; the real request sends only path).
    private bool MatchesDelete(Entry current, Entry expected) =>
        !_local && expected.IsFolder || Matches(current, expected);

    private void RemoveSubtree(string key)
    {
        var prefix = key + PathNames.PathSeparator;
        foreach (var child in _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _entries.Remove(child);
            _content.Remove(child);
        }
        _entries.Remove(key);
        _content.Remove(key);
    }

    private string NextId() => (++_nextId).ToString(CultureInfo.InvariantCulture);

    private sealed class Staged(string path, byte[] content, Entry source, FakeSide owner) : IStaged
    {
        public string Path { get; } = path;
        public bool ConsumesSourceOnPublish => false;
        internal byte[] Content { get; } = content;
        internal Entry Source { get; } = source;
        public ValueTask DisposeAsync()
        {
            lock (owner._gate) owner.StagesDisposed++;
            return default;
        }
    }
}
