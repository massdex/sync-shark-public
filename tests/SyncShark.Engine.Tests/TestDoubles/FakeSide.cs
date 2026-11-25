using Massdex.SyncShark.Engine.Dropbox;

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
    /// <summary>Previously selected endpoint identities supplied to mutations.</summary>
    public List<string> Mutations { get; }=[];
    private int nextId;
    public Dictionary<string,Exception> RecycleRefusals { get; } = new(PathNames.Comparer);
    public Dictionary<string,Exception> ReadFailures { get; } = new(PathNames.Comparer);
    /// <summary>Content-only faults leave listing and metadata reads intact.</summary>
    public Dictionary<string,Exception> PublishFailures { get; } = new(PathNames.Comparer);
    /// <summary>Run after destination staging succeeds, before control returns to the caller.</summary>
    public Action? AfterStage { get; set; }
    public Dictionary<string,Exception> ContentFailures { get; } = new(PathNames.Comparer);
    /// <summary>Script an uncertain or refused move without changing the selected entry.</summary>
    public Queue<RemovalResult> MoveOutcomes { get; } = new();
    internal Action<Entry>? AfterPublish { get; set; }
    internal Action<Entry>? AfterDelete { get; set; }
    /// <summary>Runs after a successful move with the final entry and its new path.</summary>
    internal Action<Entry,string>? AfterMove { get; set; }
    internal ChangePage? IdleBoundary { get; set; }
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
    // Real Dropbox (and Local) refuse a create whose ancestor path is a file: path/conflict/file_ancestor -> ClashNameTaken.
    private void RequireParentFolders(string path)
    {
        foreach (var ancestor in PathNames.Ancestors(path))
            if (Items.TryGetValue(ancestor, out var at) && !at.Entry.IsFolder)
                throw new ItemFailedException(path, TextKey.ClashNameTaken, null);
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
            bool InListing(string path)=>path!=under && (under.Length==0 || PathNames.IsSelfOrUnder(path,under)) && (options.Recursive || PathNames.Same(PathNames.Parent(path),under));
            if(ReadFailures.TryGetValue(under,out var rootError)) throw rootError;
            if (under.Length != 0 && (!Items.TryGetValue(under, out var root) || !root.Entry.IsFolder))
                throw new InventoryIncompleteException(under, new ChangedUnderneathException(under));
            var unreadable=new PrefixSet();
            foreach (var item in Items.Values.OrderBy(v => v.Entry.Path,StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();var path=item.Entry.Path;var key=PathNames.Key(path);
                if(!InListing(path) || unreadable.Covers(key)) continue;
                if(ReadFailures.ContainsKey(path)) { if(item.Entry.IsFolder) unreadable.Add(key);yield return new(path,item.Entry with {Skip=nameof(TextKey.SkipUnreadable)}); }
                else yield return new(path,item.Entry);
            }
            if(options.IncludeDeleted)
                foreach(var path in DeletedHistory.Keys.Where(InListing)) {ct.ThrowIfCancellationRequested();yield return new(path,null);}
            options.CursorAcquired?.Invoke("terminal");
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
        if(ContentFailures.TryGetValue(entry.Path,out var error)) throw error;
        var current = Items[entry.Path];
        if (current.Entry != entry) throw new ChangedUnderneathException(entry.Path);
        return Task.FromResult<Stream>(new MemoryStream(current.Bytes, false));
    }
    /// <summary>ContentHash operation on the supplied facts.</summary>
    public Task<string> ContentHashAsync(Entry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(ContentHashAsync));
        if(ContentFailures.TryGetValue(entry.Path,out var error)) throw error;
        using var h = new ContentHash(); h.Append(Current(entry).Bytes); return Task.FromResult(h.Finish());
    }
    /// <summary>Stage operation on the supplied facts.</summary>
    public Task<IStaged> StageAsync(string path, Stream content, Entry source, Entry? selectedTarget, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(StageAsync)); RequireParentFolders(path);
        return Copy();
        async Task<IStaged> Copy()
        {
            using var stream=new MemoryStream(); await content.CopyToAsync(stream,ct); var bytes=stream.ToArray();
            Stage staged;
            if (Side!=Side.Local) staged=new Stage(path,bytes,source);
            else
            {
                var id=$"stage-{++nextId}";var at=path+"."+id;using var hash=new ContentHash();hash.Append(bytes);
                var proof=new StageProof(at,"volume",id,bytes.LongLength,hash.Finish());
                var stamped=source with {Path=at,Id=id,Size=bytes.LongLength,ContentHash=proof.ContentHash,Revision=string.Empty};
                staged=new Stage(path,bytes,stamped,DiscardUnarmedAsync){Proof=proof};
                stages.Add(at,staged);
            }
            AfterStage?.Invoke();
            return staged;
        }
    }
    /// <summary>Publish operation on the supplied facts.</summary>
    public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(PublishAsync)); var stage = (Stage)staged;
        if(PublishFailures.TryGetValue(stage.Path,out var failure)) throw failure;
        RequireParentFolders(stage.Path);
        if (expected != null) { Mutations.Add(expected.Id);var old = Current(expected); History.Add(old); }
        else if (Items.ContainsKey(stage.Path)) throw new PathOccupiedException(stage.Path);
        using var digest = new ContentHash(); digest.Append(stage.Bytes);
        var entry = stage.Source with { Path = stage.Path, Id = stage.Proof?.FileId ?? expected?.Id ?? $"{Side}-{++nextId}", Revision = $"r{++nextId}", ContentHash = digest.Finish() };
        if(stage.Proof is { } proof && stages.TryGetValue(proof.Path,out var held) && held.Proof==proof) stages.Remove(proof.Path);
        Items[stage.Path] = (entry, stage.Bytes.ToArray()); AfterPublish?.Invoke(entry); return Task.FromResult(new RemovalResult(RemovalOutcome.Published, entry, null));
    }
    /// <summary>EnsureFolder operation on the supplied facts.</summary>
    public Task<RemovalResult> EnsureFolderAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(EnsureFolderAsync)); RequireParentFolders(path);
        if (Items.TryGetValue(path,out var old)) return old.Entry.IsFolder ? Task.FromResult(new RemovalResult(RemovalOutcome.Published,old.Entry,null)) : throw new PathOccupiedException(path);
        var folder = Folder(path); Seed(folder, []); return Task.FromResult(new RemovalResult(RemovalOutcome.Published,folder,null));
    }
    /// <summary>Delete operation on the supplied facts.</summary>
    public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(DeleteAsync)); Mutations.Add(expected.Id);var old = Current(expected);
        if (RecycleRefusals.TryGetValue(expected.Path, out var failure)) throw failure;
        var children = Items.Values.Where(v => expected.IsFolder && PathNames.IsUnderKey(PathNames.Key(v.Entry.Path), PathNames.Key(expected.Path))).ToArray();
        foreach (var child in children) { History.Add(child); Items.Remove(child.Entry.Path); }
        History.Add(old); Items.Remove(expected.Path); AfterDelete?.Invoke(expected);
        var removed = new RemovalResult(RemovalOutcome.Removed, null, null);
        return Task.FromResult(Side == Side.Dropbox
            ? removed with { Reply = new JobReply.Terminal([(old.Entry, null, null)]) }
            : removed with { RecycledVictim = new(selection?.Proof.VolumeId ?? Side.ToString(), expected.Path, old.Entry), Recycled = expected.IsFolder && selection != null ? Contents() : null });

        async IAsyncEnumerable<Listed> Contents()
        {
            await Task.CompletedTask;
            foreach (var child in children) yield return new(child.Entry.Path, child.Entry);
        }
    }
    // Immediate fake deletes mutate the selected entry directly; there is no accepted job to persist an origin on.
    public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, ActionKind origin, CancellationToken ct) =>
        DeleteAsync(expected,selection,ct);
    /// <summary>Move operation on the supplied facts.</summary>
    public Task<RemovalResult> MoveAsync(Entry expected, string newPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(MoveAsync)); Mutations.Add(expected.Id);Current(expected);
        if (MoveOutcomes.TryDequeue(out var scripted))
        {
            if (scripted.Outcome is not (RemovalOutcome.NoEffect or RemovalOutcome.Unknown))
                throw new InvalidOperationException("Script only refused or uncertain moves; successful moves must update the endpoint.");
            return Task.FromResult(scripted);
        }
        if (!PathNames.Same(expected.Path, newPath) && Items.ContainsKey(newPath)) throw new PathOccupiedException(newPath);
        var moving = Items.Values.Where(v => v.Entry.Path == expected.Path || expected.IsFolder && PathNames.IsUnderKey(PathNames.Key(v.Entry.Path), PathNames.Key(expected.Path))).ToArray();
        foreach (var v in moving) Items.Remove(v.Entry.Path);
        foreach (var v in moving) Seed(v.Entry with { Path = newPath + v.Entry.Path[expected.Path.Length..] }, v.Bytes);
        var final = Items[newPath].Entry; AfterMove?.Invoke(final, newPath);
        return Task.FromResult(new RemovalResult(RemovalOutcome.Published,final,null));
    }
    /// <summary>ReadById operation on the supplied facts.</summary>
    public Task<(IdentityLocation Location, bool InScope)?> ReadByIdAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(ReadByIdAsync)); var hit = Items.Values.FirstOrDefault(v => v.Entry.Id == id).Entry;
        if(hit is null && Side==Side.Local && History.LastOrDefault(v=>v.Entry.Id==id).Entry is {} victim)
            return Task.FromResult<(IdentityLocation Location,bool InScope)?>((new("volume",victim.Path,victim),false));
        return Task.FromResult<(IdentityLocation Location,bool InScope)?>(hit is null ? null : (new(Side.ToString(), hit.Path, hit), true));
    }
    /// <summary>Seeded in-scope names per file identity; a one-name case must be seeded, never derived from the item map.</summary>
    public Dictionary<string,IReadOnlyList<string>> LinkNames { get; } = new(StringComparer.Ordinal);
    /// <summary>NamesById operation on the supplied facts: null for a folder and for any identity not seeded.</summary>
    public Task<IReadOnlyList<string>?> NamesByIdAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(NamesByIdAsync));
        if(Side!=Side.Local || Items.Values.Any(v=>v.Entry.Id==id && v.Entry.IsFolder)) return Task.FromResult<IReadOnlyList<string>?>(null);
        return Task.FromResult(LinkNames.TryGetValue(id,out var names) ? names : null);
    }
    /// <summary>CreateFolderExclusive operation on the supplied facts.</summary>
    public Task<RemovalResult> CreateFolderExclusiveAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(CreateFolderExclusiveAsync)); RequireParentFolders(path);
        if (Items.ContainsKey(path)) throw new PathOccupiedException(path);
        var folder = Folder(path); Seed(folder, []); return Task.FromResult(new RemovalResult(RemovalOutcome.Published,folder,null));
    }
    /// <summary>ReadChanges operation on the supplied facts.</summary>
    public Task SettleJournalAsync(JournalPosition position,CancellationToken ct) { ct.ThrowIfCancellationRequested(); Requests.Count(nameof(SettleJournalAsync));return Task.CompletedTask; }
    public Task<ChangePage> ReadChangesAsync(string? cursor, JournalPosition? position, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(ReadChangesAsync));
        return Task.FromResult(Pages.Dequeue());
    }
    /// <summary>Recover operation on the supplied facts.</summary>
    public Task<Recovery?> RecoverAsync(IdentityLocation actual, Entry version, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(RecoverAsync));
        if(Side==Side.Local && actual.Domain=="volume" && actual.Entry==version && History.LastOrDefault(v=>v.Entry==version) is { Entry:not null } retained && !version.IsFolder)
            return Task.FromResult<Recovery?>(new Recovery.File(version,new MemoryStream(retained.Bytes,false)));
        var scripted = Recoverable.GetValueOrDefault((actual.Entry.Id,version.Revision,version.ContentHash));
        return Task.FromResult(scripted is Recovery.File file
            ? file with { Bytes = new MemoryStream(((MemoryStream)file.Bytes).ToArray(), false) }
            : scripted);
    }
    private static readonly DropboxSide BatchJobs = new(Scenarios.ItemChecks.Pair(),()=>throw new InvalidOperationException(),null!,null!);
    /// <summary>The production batch-job map, plus the lone-write and v1 route spellings scripted jobs carry.</summary>
    public ActionKind JobOperation(AcceptedJob job) => job.Route switch
    {
        DropboxProtocol.Delete => ActionKind.DeleteDropbox,
        "files/move_batch" => ActionKind.MoveDropbox,
        "files/upload_session/finish_batch/check" => ActionKind.Upload,
        _ => BatchJobs.JobOperation(job)
    };
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
    /// <inheritdoc/>
    public Task<DeletionEvidence> DeletedAsync(string path, bool folder, CancellationToken ct)
    {
        if(folder && !DeletedHistory.ContainsKey(path))
        {
            var ancestor=DeletedHistory.Keys.Where(k=>PathNames.IsSelfOrUnder(path,k)).OrderByDescending(k=>k.Length).FirstOrDefault();
            if(ancestor!=null){ct.ThrowIfCancellationRequested();Requests.Count(nameof(DeletedAsync));return Task.FromResult<DeletionEvidence>(new DeletionEvidence.DeletedPath(path,ancestor));}
        }
        return DeletedAsync(path,ct);
    }
    /// <inheritdoc/>
    public async Task<ChangePage> ReadChangesAsync(string? cursor, JournalPosition? position, (string? Cursor,JournalPosition? Position) until, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool Reached(string? token,JournalPosition? at)=>Side==Side.Local
            ? at!=null && until.Position is {} stop && at.JournalId==stop.JournalId && at.NextUsn>=stop.NextUsn
            : until.Cursor!=null && token==until.Cursor;
        if(Reached(cursor,position)) return new ChangePage([],cursor,position){CaughtUp=true};
        var page=await ReadChangesAsync(cursor,position,ct);
        return page with { CaughtUp=!page.ContinuityLost && (Reached(page.Cursor,page.Position) || Side==Side.Dropbox && page.CaughtUp) };
    }
    /// <inheritdoc/>
    public Task<(string? Cursor,JournalPosition? Position)> BoundaryAsync(CancellationToken ct) => Task.FromResult(((Pages.LastOrDefault() ?? IdleBoundary)?.Cursor,(Pages.LastOrDefault() ?? IdleBoundary)?.Position));
}
