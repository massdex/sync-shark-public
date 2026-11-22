namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>A synthetic endpoint; unscripted effects fail rather than manufacture a passing result.</summary>
public sealed class FakeSide(Side side) : ISide
{
    public Dictionary<string,DeletionEvidence> DeletedHistory { get; } = new(PathNames.Comparer);
    public (TimeSpan Offset, TimeSpan Uncertainty)? ClockOffset { get; set; }
    public Task<DeletionEvidence> DeletedAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(DeletedAsync));
        return Task.FromResult(DeletedHistory.GetValueOrDefault(path) ?? new DeletionEvidence.NoRetainedHistory(path));
    }
    /// <inheritdoc/>
    public Side Side => side;
    public RequestCounters Requests { get; } = new();
    public Dictionary<string, (Entry Entry, byte[] Bytes)> Items { get; } = new(PathNames.Comparer);
    public void Seed(Entry entry, byte[] bytes) => Items.Add(entry.Path, (entry, bytes.ToArray()));
    public List<(Entry Entry, byte[] Bytes)> History { get; } = [];
    private int nextId;
    public Dictionary<string,Exception> RecycleRefusals { get; } = new(PathNames.Comparer);
    public Dictionary<string,Exception> ReadFailures { get; } = new(PathNames.Comparer);
    public Queue<ChangePage> Pages { get; } = [];
    public Dictionary<string, Queue<JobReply>> JobReplies { get; } = [];
    public Dictionary<(string Id,string Revision,string Hash), Recovery> Recoverable { get; } = [];
    private sealed record Stage(string Path, byte[] Bytes, Entry Source) : IStaged
    {
        public bool ConsumesSourceOnPublish => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private (Entry Entry, byte[] Bytes) Current(Entry expected)
    {
        if (!Items.TryGetValue(expected.Path, out var at) || at.Entry != expected) throw new ChangedUnderneathException(expected.Path);
        return at;
    }
    private Entry Folder(string path) => new(path, true, 0, DateTimeOffset.UnixEpoch, $"{Side}-{++nextId}", "", "", null);
    /// <summary>ReadAll operation on the supplied facts.</summary>
    public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(ReadAllAsync));
        return List();
        async IAsyncEnumerable<Listed> List()
        {
            await Task.CompletedTask;
            foreach (var item in Items.Values.OrderBy(v => v.Entry.Path, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (ReadFailures.TryGetValue(item.Entry.Path,out var error)) throw error;
                if (item.Entry.Path != under && (under.Length == 0 || PathNames.IsSelfOrUnder(item.Entry.Path, under)) &&
                    (options.Recursive || PathNames.Same(PathNames.Parent(item.Entry.Path), under))) yield return new(item.Entry.Path, item.Entry);
            }
        }
    }
    /// <summary>ReadOne operation on the supplied facts.</summary>
    public Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(ReadOneAsync));
        if (ReadFailures.TryGetValue(path,out var error)) throw error;
        return Task.FromResult(Items.TryGetValue(path, out var value) ? value.Entry : null);
    }
    /// <summary>OpenRead operation on the supplied facts.</summary>
    public Task<Stream> OpenReadAsync(Entry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(OpenReadAsync));
        var current = Items[entry.Path];
        if (current.Entry != entry) throw new ChangedUnderneathException(entry.Path);
        return Task.FromResult<Stream>(new MemoryStream(current.Bytes, false));
    }
    /// <summary>ContentHash operation on the supplied facts.</summary>
    public Task<string> ContentHashAsync(Entry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(ContentHashAsync));
        using var h = new ContentHash(); h.Append(Current(entry).Bytes); return Task.FromResult(h.Finish());
    }
    /// <summary>Stage operation on the supplied facts.</summary>
    public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(StageAsync));
        return Copy();
        async Task<IStaged> Copy() { using var bytes = new MemoryStream(); await content.CopyToAsync(bytes, ct); return new Stage(path, bytes.ToArray(), source); }
    }
    /// <summary>Publish operation on the supplied facts.</summary>
    public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(PublishAsync)); var stage = (Stage)staged;
        if (expected != null) { var old = Current(expected); History.Add(old); }
        else if (Items.ContainsKey(stage.Path)) throw new PathOccupiedException(stage.Path);
        using var digest = new ContentHash(); digest.Append(stage.Bytes);
        var entry = stage.Source with { Path = stage.Path, Id = expected?.Id ?? $"{Side}-{++nextId}", Revision = $"r{++nextId}", ContentHash = digest.Finish() };
        Items[stage.Path] = (entry, stage.Bytes.ToArray()); return Task.FromResult(new RemovalResult(RemovalOutcome.Published, null, entry, null));
    }
    /// <summary>EnsureFolder operation on the supplied facts.</summary>
    public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(EnsureFolderAsync));
        if (Items.TryGetValue(path,out var old)) return old.Entry.IsFolder ? Task.FromResult(old.Entry) : throw new PathOccupiedException(path);
        var folder = Folder(path); Seed(folder, []); return Task.FromResult(folder);
    }
    /// <summary>Delete operation on the supplied facts.</summary>
    public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(DeleteAsync)); var old = Current(expected);
        if (RecycleRefusals.TryGetValue(expected.Path, out var failure)) throw failure;
        foreach (var child in Items.Values.Where(v => expected.IsFolder && PathNames.IsUnderKey(PathNames.Key(v.Entry.Path), PathNames.Key(expected.Path))).ToArray()) { History.Add(child); Items.Remove(child.Entry.Path); }
        History.Add(old); Items.Remove(expected.Path); return Task.FromResult(new RemovalResult(RemovalOutcome.Removed, null, null, null));
    }
    /// <summary>Move operation on the supplied facts.</summary>
    public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(MoveAsync)); var old = Current(expected);
        if (!PathNames.Same(expected.Path, newPath) && Items.ContainsKey(newPath)) throw new PathOccupiedException(newPath);
        var moving = Items.Values.Where(v => v.Entry.Path == expected.Path || expected.IsFolder && PathNames.IsUnderKey(PathNames.Key(v.Entry.Path), PathNames.Key(expected.Path))).ToArray();
        foreach (var v in moving) Items.Remove(v.Entry.Path);
        foreach (var v in moving) Seed(v.Entry with { Path = newPath + v.Entry.Path[expected.Path.Length..] }, v.Bytes);
        return Task.FromResult(Items[newPath].Entry);
    }
    /// <summary>ReadById operation on the supplied facts.</summary>
    public Task<(IdentityLocation Location, bool InScope)?> ReadByIdAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(ReadByIdAsync)); var hit = Items.Values.FirstOrDefault(v => v.Entry.Id == id).Entry;
        return Task.FromResult<(IdentityLocation Location,bool InScope)?>(hit is null ? null : (new(Side.ToString(), hit.Path, hit), true));
    }
    /// <summary>CreateFolderExclusive operation on the supplied facts.</summary>
    public Task<Entry> CreateFolderExclusiveAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(CreateFolderExclusiveAsync));
        if (Items.ContainsKey(path)) throw new PathOccupiedException(path);
        var folder = Folder(path); Seed(folder, []); return Task.FromResult(folder);
    }
    /// <summary>ReadChanges operation on the supplied facts.</summary>
    public Task<ChangePage> ReadChangesAsync(string? cursor, JournalPosition? position, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(ReadChangesAsync));
        return Task.FromResult(Pages.Dequeue());
    }
    /// <summary>Recover operation on the supplied facts.</summary>
    public Task<Recovery?> RecoverAsync(IdentityLocation actual, Entry version, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(RecoverAsync));
        return Task.FromResult(Recoverable.GetValueOrDefault((actual.Entry.Id,version.Revision,version.ContentHash)));
    }
    /// <summary>ReadJob operation on the supplied facts.</summary>
    public Task<JobReply> ReadJobAsync(AcceptedJob job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(ReadJobAsync));
        return Task.FromResult(JobReplies[job.JobId].Dequeue());
    }
}
