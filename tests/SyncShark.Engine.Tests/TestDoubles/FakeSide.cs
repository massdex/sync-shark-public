namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>A synthetic endpoint; unscripted effects fail rather than manufacture a passing result.</summary>
public sealed class FakeSide(Side side, Func<StageProof,Task<bool>>? noteOwns = null) : ISide
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
    private readonly Dictionary<string,Stage> stages = new(PathNames.Comparer);
    private sealed record Stage(string Path, byte[] Bytes, Entry Source, Func<StageProof,ValueTask>? Release = null) : IStaged
    {
        public StageProof? Proof { get; init; }
        public bool ConsumesSourceOnPublish => false;
        public ValueTask DisposeAsync() => Proof is {} proof && Release!=null ? Release(proof) : ValueTask.CompletedTask;
    }
    private async ValueTask DiscardUnarmedAsync(StageProof proof)
    {
        if(noteOwns!=null && await noteOwns(proof)) return;
        if(stages.TryGetValue(proof.Path,out var staged) && staged.Proof==proof) stages.Remove(proof.Path);
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
    public Task<IStaged> StageAsync(string path, Stream content, Entry source, Entry? selectedTarget, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(StageAsync));
        return Copy();
        async Task<IStaged> Copy()
        {
            using var stream=new MemoryStream(); await content.CopyToAsync(stream,ct); var bytes=stream.ToArray();
            if (Side!=Side.Local) return new Stage(path,bytes,source);
            var id=$"stage-{++nextId}";var at=path+"."+id;using var hash=new ContentHash();hash.Append(bytes);
            var proof=new StageProof(at,"volume",id,bytes.LongLength,hash.Finish());
            var stamped=source with {Path=at,Id=id,Size=bytes.LongLength,ContentHash=proof.ContentHash,Revision=string.Empty};
            var staged=new Stage(path,bytes,stamped,DiscardUnarmedAsync){Proof=proof};
            stages.Add(at,staged);return staged;
        }
    }
    /// <summary>Publish operation on the supplied facts.</summary>
    public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(PublishAsync)); var stage = (Stage)staged;
        if (expected != null) { var old = Current(expected); History.Add(old); }
        else if (Items.ContainsKey(stage.Path)) throw new PathOccupiedException(stage.Path);
        using var digest = new ContentHash(); digest.Append(stage.Bytes);
        var entry = stage.Source with { Path = stage.Path, Id = expected?.Id ?? $"{Side}-{++nextId}", Revision = $"r{++nextId}", ContentHash = digest.Finish() };
        if(stage.Proof is { } proof && stages.TryGetValue(proof.Path,out var held) && held.Proof==proof) stages.Remove(proof.Path);
        Items[stage.Path] = (entry, stage.Bytes.ToArray()); return Task.FromResult(new RemovalResult(RemovalOutcome.Published, entry, null));
    }
    /// <summary>EnsureFolder operation on the supplied facts.</summary>
    public Task<RemovalResult> EnsureFolderAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(EnsureFolderAsync));
        if (Items.TryGetValue(path,out var old)) return old.Entry.IsFolder ? Task.FromResult(new RemovalResult(RemovalOutcome.Published,old.Entry,null)) : throw new PathOccupiedException(path);
        var folder = Folder(path); Seed(folder, []); return Task.FromResult(new RemovalResult(RemovalOutcome.Published,folder,null));
    }
    /// <summary>Delete operation on the supplied facts.</summary>
    public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(DeleteAsync)); var old = Current(expected);
        if (RecycleRefusals.TryGetValue(expected.Path, out var failure)) throw failure;
        foreach (var child in Items.Values.Where(v => expected.IsFolder && PathNames.IsUnderKey(PathNames.Key(v.Entry.Path), PathNames.Key(expected.Path))).ToArray()) { History.Add(child); Items.Remove(child.Entry.Path); }
        History.Add(old); Items.Remove(expected.Path); return Task.FromResult(new RemovalResult(RemovalOutcome.Removed, null, null));
    }
    /// <summary>Move operation on the supplied facts.</summary>
    public Task<RemovalResult> MoveAsync(Entry expected, string newPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(MoveAsync)); var old = Current(expected);
        if (!PathNames.Same(expected.Path, newPath) && Items.ContainsKey(newPath)) throw new PathOccupiedException(newPath);
        var moving = Items.Values.Where(v => v.Entry.Path == expected.Path || expected.IsFolder && PathNames.IsUnderKey(PathNames.Key(v.Entry.Path), PathNames.Key(expected.Path))).ToArray();
        foreach (var v in moving) Items.Remove(v.Entry.Path);
        foreach (var v in moving) Seed(v.Entry with { Path = newPath + v.Entry.Path[expected.Path.Length..] }, v.Bytes);
        return Task.FromResult(new RemovalResult(RemovalOutcome.Published,Items[newPath].Entry,null));
    }
    /// <summary>ReadById operation on the supplied facts.</summary>
    public Task<(IdentityLocation Location, bool InScope)?> ReadByIdAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(ReadByIdAsync)); var hit = Items.Values.FirstOrDefault(v => v.Entry.Id == id).Entry;
        return Task.FromResult<(IdentityLocation Location,bool InScope)?>(hit is null ? null : (new(Side.ToString(), hit.Path, hit), true));
    }
    /// <summary>CreateFolderExclusive operation on the supplied facts.</summary>
    public Task<RemovalResult> CreateFolderExclusiveAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(CreateFolderExclusiveAsync));
        if (Items.ContainsKey(path)) throw new PathOccupiedException(path);
        var folder = Folder(path); Seed(folder, []); return Task.FromResult(new RemovalResult(RemovalOutcome.Published,folder,null));
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

    /// <inheritdoc/>
    public Task<IStaged> ReopenStageAsync(RemovalSelection armedVictim, StageProof expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(ReopenStageAsync));
        if(Side!=Side.Local || expected.VolumeId!="volume" || !stages.TryGetValue(expected.Path,out var held) || held.Source.IsFolder || held.Proof?.FileId!=expected.FileId || held.Bytes.LongLength!=expected.Size)
            throw new ChangedUnderneathException(expected.Path);
        using var hash=new ContentHash();hash.Append(held.Bytes);
        if(hash.Finish()!=expected.ContentHash)throw new ChangedUnderneathException(expected.Path);
        return Task.FromResult<IStaged>(new Stage(armedVictim.Proof.Expected.Path,held.Bytes.ToArray(),held.Source,DiscardUnarmedAsync){Proof=expected});
    }
    public Task<(DirectoryIdentity? Local, DropboxRootIdentity? Dropbox, DriveEvidence? Drive, DriveType? LocalDriveType)> RootAsync(CancellationToken ct) => Task.FromResult(Root);
    public (DirectoryIdentity? Local, DropboxRootIdentity? Dropbox, DriveEvidence? Drive, DriveType? LocalDriveType) Root { get; set; }
}
