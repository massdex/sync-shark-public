using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>Run-scoped virtual effects over write-denying, read-through endpoints. Only changed paths are retained.</summary>
internal sealed class RehearsalEndpoints(IEndpointProvider real, RehearsalOverlay storage, Action<RehearsalEffect> record,
    IStateStore? store = null) : IEndpointProvider, IAsyncDisposable
{
    private readonly PlanOnlyEndpoints _reads = new(real);
    private readonly ConcurrentDictionary<string, Overlay> _roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LocalGapWorker> _workers = new(StringComparer.Ordinal);
    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workers.Values) await worker.DisposeAsync();
    }
    public async Task PrepareAsync(PairDef pair, CancellationToken ct)
    {
        await _reads.PrepareAsync(pair, ct);
        Open(pair.A, Side.A); Open(pair.B, Side.B);
    }
    private void Open(RootDef root, Side side)
    {
        var overlay = _roots.GetOrAdd(root.RootId, _ => root.Provider switch
        {
            SyncProviders.Local => new LocalOverlay(root, side, storage, record),
            SyncProviders.Dropbox => new CloudOverlay(root, side, storage, record),
            _ => new Overlay(root, side, storage, record)
        });
        overlay.Reader = _reads.Writer(root);
        overlay.Inventory = _reads.Inventory(root);
        if (overlay.Inventory is LocalInventory local && local.GapIo is { } realGap && store is ILocalGapStore gaps)
        {
            var io = new RehearsalGapIo(realGap, storage, store);
            local.GapIo = io;
            if (!_workers.TryGetValue(root.RootId, out var worker))
                _workers[root.RootId] = worker = new LocalGapWorker(gaps, io);
            local.GapWorker = worker;
        }
    }
    public IInventorySource Inventory(RootDef root) => _roots[root.RootId];
    public IEndpointWriter Writer(RootDef root) => _roots[root.RootId];
    internal Task<bool> HasUploadSpaceAsync(RootDef root, long need, CancellationToken ct) =>
        _reads.HasUploadSpaceAsync(root, need, ct);

    internal static IInventorySource Unwrap(IInventorySource inventory) => inventory is Overlay overlay ? overlay.Inventory : inventory;
    internal static void Acknowledge(IInventorySource inventory)
    {
        if (inventory is Overlay overlay) overlay.Acknowledge();
    }
    internal static Delta Apply(IInventorySource inventory, Delta delta, Func<string, bool> include, bool echo = false) =>
        inventory is Overlay overlay ? echo ? overlay.Echo(overlay.Apply(delta, include), include) : overlay.Apply(delta, include) : delta;

    internal static async Task HydrateAsync(IInventorySource inventory, DropboxInventory cloud,
        IReadOnlyList<string> prefixes, Func<IReadOnlyList<DropboxChange>, Task> accept, CancellationToken ct)
    {
        if (inventory is not Overlay overlay) { await cloud.HydratePrefixesAsync(prefixes, accept, ct); return; }
        await cloud.HydratePrefixesAsync(prefixes, page => accept(page.Where(row => !overlay.Try(row.Path, out _)).ToArray()), ct);
        foreach (var prefix in prefixes.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(prefix => !prefixes.Any(parent => !PairPath.Same(prefix, parent) && PairPath.IsSelfOrUnder(prefix, parent))))
            foreach (var page in overlay.StoredItems(prefix).Chunk(256))
                await accept(page.Select(item => new DropboxChange(item.Path, DropboxItems.ToEntry(item))).ToArray());
    }

    private class Overlay(RootDef root, Side side, RehearsalOverlay storage, Action<RehearsalEffect> record) : IEndpointWriter, IInventorySource, ISourceAbsenceProbe, IRecoveryRecordReader, IRecoveryAvailability
    {
        // A null value is an explicit tombstone, never a cache miss. Keyed to the root entity, not the run/pair.
        private long _acknowledged, _observed;
        internal void Acknowledge() => _acknowledged = _observed;
        internal Delta Echo(Delta delta, Func<string, bool> include)
        {
            var through = storage.Version;
            var result = delta with { Changes = delta.Changes.Concat(storage.Since(root.RootId, _acknowledged, through)
                .Where(change => include(change.Path))).ToArray() };
            _observed = through;
            return result;
        }
        public string? RecoveryUnavailable => (Reader as IRecoveryAvailability)?.RecoveryUnavailable;
        public IEndpointWriter Reader { get; set; } = null!;
        public IInventorySource Inventory { get; set; } = null!;
        private static string Id(string value) => "rehearsal-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        private string TargetId(string path)
        {
            if (root.Provider != SyncProviders.Local) return Id(root.RootId + ":" + path);
            var volume = root.RootObjectId.StartsWith("id:local:", StringComparison.Ordinal)
                ? LocalItems.ParseObjectId(root.RootObjectId).Volume : 0;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(root.RootId + ":" + path));
            return SyncLocalEndpointStore.ObjectId(volume, BitConverter.ToUInt64(hash, 0) | (1UL << 63));
        }
        internal bool Try(string path, out ItemState? item) => storage.Try(root.RootId, path, out item);
        internal IEnumerable<ItemState> StoredItems(string subtree) => storage.Items(root.RootId, subtree);
        protected void Save(string kind, string path, ItemState? state, ItemState? victim = null)
        {
            storage.Save(root.RootId, path, state, victim);
            var described = state ?? victim;
            record(new(kind, side.ToString(), root.Provider, path, described?.Size ?? 0, described?.Hash));
        }
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => Try(path, out var item)
            ? Task.FromResult(item) : Reader.ObserveAsync(path, ct);
        public Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct) =>
            Try(path, out var current) ? Task.FromResult<bool?>(current is null) :
            ((ISourceAbsenceProbe)Reader).IsSourceAbsentAsync(historical, path, ct);
        public Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct) => ObserveAsync(path, ct);
        public Task<bool?> IsHistoricalSourcePresentAsync(ItemState historical, CancellationToken ct) =>
            Try(historical.Path, out var current) ? Task.FromResult<bool?>(current?.ObjectId == historical.ObjectId) :
            ((ISourceAbsenceProbe)Reader).IsHistoricalSourcePresentAsync(historical, ct);
        public Task<ItemState> DescribeRecoveryAsync(RecoveryRecord recovery, CancellationToken ct)
        {
            if (recovery.Location.StartsWith("rehearsal:", StringComparison.Ordinal))
                return Task.FromResult(new ItemState(recovery.Path, ItemKind.File, Id(recovery.ActionId), recovery.VersionId,
                    recovery.Hash, recovery.Size, recovery.Created, ItemFlags.None));
            return ((IRecoveryRecordReader)Reader).DescribeRecoveryAsync(recovery, ct);
        }
        public async Task<Stream> OpenRecoveryRecordAsync(RecoveryRecord recovery, CancellationToken ct)
        {
            await DescribeRecoveryAsync(recovery, ct);
            return Stream.Null;
        }
        public async Task<Stream> OpenReadAsync(ItemState item, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // Local metadata-only probes intentionally omit hashes; use the production observation
            // for that side. Cloud metadata includes the hash and must never open a download stream.
            var actual = Try(item.Path, out var simulated) ? simulated : root.Provider == SyncProviders.Local
                ? await Reader.ObserveAsync(item.Path, ct)
                : await ((ISourceAbsenceProbe)Reader).ObserveSourceMetadataAsync(item.Path, ct);
            if (actual is null || actual.Kind != item.Kind || actual.ObjectId != item.ObjectId ||
                actual.VersionId != item.VersionId || actual.Size != item.Size || actual.Hash != item.Hash)
                throw new ExpectedTargetMismatchException("The rehearsal source changed since planning.");
            // All sources are metadata-only, including real Dropbox files. Never download content.
            return Stream.Null;
        }
        protected async Task CheckTargetAsync(SyncAction action, string path, CancellationToken ct)
        {
            var actual = await ObserveAsync(path, ct);
            var expected = action.ExpectedTarget;
            if ((actual is null) != (expected is null) || actual is not null && expected is not null &&
                (actual.Kind != expected.Kind || actual.ObjectId != expected.ObjectId || actual.VersionId != expected.VersionId ||
                 actual.Hash != expected.Hash || actual.Size != expected.Size))
                throw new ExpectedTargetMismatchException("The rehearsal target changed since planning.");
        }
        public async Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await CheckTargetAsync(action, action.Path, ct);
            var source = action.Source ?? throw new InvalidOperationException("A simulated put requires source metadata.");
            var state = source with { Path = action.Path, ObjectId = TargetId(action.Path),
                VersionId = root.Provider == SyncProviders.Local ? null : Id(action.ActionId + ":" + source.Hash),
                Flags = ItemFlags.None, IdentityJson = null };
            Save(action.Kind.ToString(), action.Path, state);
            return state;
        }
        public async Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct)
        {
            var from = action.FromPath ?? throw new InvalidOperationException("Move requires a source path.");
            await CheckTargetAsync(action, from, ct);
            var state = await ObserveAsync(from, ct) ?? throw new FileNotFoundException("Move source is absent.");
            if (!PairPath.Same(from, action.Path) && await ObserveAsync(action.Path, ct) is not null)
                throw new ExpectedTargetMismatchException("The rehearsal move destination is occupied.");
            if (state.Kind == ItemKind.Folder)
                await ReadSubtreeAsync(from, page =>
                {
                    foreach (var child in page.Where(child => !PairPath.Same(child.Path, from)))
                    {
                        Save("MoveFrom", child.Path, null, child);
                        var moved = child with { Path = action.Path + child.Path[from.Length..] };
                        Save("Move", moved.Path, moved);
                    }
                    return Task.CompletedTask;
                }, ct);
            Save("MoveFrom", from, null, state);
            state = state with { Path = action.Path };
            Save("Move", action.Path, state);
            return state;
        }
        public async Task DeleteAsync(SyncAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await CheckTargetAsync(action, action.Path, ct);
            if (action.ExpectedTarget?.Kind == ItemKind.Folder)
                await ReadSubtreeAsync(action.Path, page =>
                {
                    foreach (var child in page.Where(child => !PairPath.Same(child.Path, action.Path))) Save("Delete", child.Path, null, child);
                    return Task.CompletedTask;
                }, ct);
            Save("Delete", action.Path, null, action.ExpectedTarget);
        }
        public async Task<ItemState> MkDirAsync(string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (await ObserveAsync(path, ct) is { } existing)
            {
                if (existing.Kind != ItemKind.Folder) throw new ExpectedTargetMismatchException("The folder destination is occupied by a file.");
                return existing;
            }
            var state = new ItemState(path, ItemKind.Folder, TargetId(path), null, null, 0,
                DateTimeOffset.UnixEpoch, ItemFlags.None);
            Save("MkDir", path, state);
            return state;
        }
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            record(new("Preserve", side.ToString(), root.Provider, victim.Path, victim.Size, victim.Hash));
            return Task.FromResult(new RecoveryRef(side, "rehearsal:" + Id(actionId), victim.VersionId, victim.Hash ?? "", victim.Size));
        }
        public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> progress, CancellationToken ct)
        {
            var through = storage.Version;
            var snapshot = await Inventory.FullAsync(gen, include, progress, ct);
            _observed = through;
            return snapshot with { Items = snapshot.Items.Where(item => !Try(item.Path, out _))
                .Concat(storage.Items(root.RootId).Where(item => include(item.Path))).ToArray() };
        }
        public async Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct)
        {
            var delta = await Inventory.ChangesSinceAsync(cursor, gen, include, ct);
            return Echo(Apply(delta, include), include);
        }
        internal Delta Apply(Delta delta, Func<string, bool> include)
        {
            // Replace real feed observations with virtual metadata; Echo adds only virtual changes
            // since the last successfully committed scan, just as a provider feed echoes real writes.
            return delta with { Changes = delta.Changes.Select(change =>
            {
                if (!Try(change.Path, out var state)) return change;
                return state is null ? change with { Kind = ChangeKind.Delete, State = null } :
                    change with { Kind = ChangeKind.Upsert, State = state, ObjectId = state.ObjectId, DeletedUtc = null };
            }).Where(change => include(change.Path)).ToArray() };
        }
        public async Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct)
        {
            var result = new Dictionary<string, string>(await Inventory.HashAsync(paths.Where(path => !Try(path, out _)).ToArray(), ct), StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths) if (Try(path, out var item) && item?.Hash is { } hash) result[path] = hash;
            return result;
        }
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            Inventory.DeletionTimesAsync(paths.Where(path => !Try(path, out _)).ToArray(), ct);
        public async Task VerifyPendingAsync(Func<string, bool> include, Action<string> touched, CancellationToken ct)
        {
            await Inventory.VerifyPendingAsync(path => include(path) && !Try(path, out _), touched, ct);
            // ObserveOnly forbids journal resizing, but rehearsal must still exercise the independent
            // verification worker. That worker reads content and writes only the isolated store.
            if (Inventory is LocalInventory local) local.GapWorker?.Start(root);
        }
        public async Task ReadSubtreeAsync(string path, Func<IReadOnlyList<ItemState>, Task> accept, CancellationToken ct, IProgress<string>? progress = null)
        {
            if (Try(path, out var simulated) && simulated is null) return;
            if (!Try(path, out _) || await Reader.ObserveAsync(path, ct) is not null)
                await Inventory.ReadSubtreeAsync(path, page => accept(page.Where(item => !Try(item.Path, out _)).ToArray()), ct, progress);
            foreach (var page in storage.Items(root.RootId, path).Chunk(256)) await accept(page);
        }
    }
    // Only final endpoint effects are virtual. Capability selection, admission, intent checkpoints,
    // preserve-after-verification and whole-folder result bookkeeping stay in the ordinary Executor.
    private sealed class LocalOverlay(RootDef root, Side side, RehearsalOverlay storage, Action<RehearsalEffect> record)
        : Overlay(root, side, storage, record), ILocalFreeSpaceAdmission, ISyncDirectDownloadTarget
    {
        public Task<FreeSpaceAdmission> AdmitFreeSpaceAsync(SyncAction action, CancellationToken ct) =>
            ((ILocalFreeSpaceAdmission)Reader).AdmitFreeSpaceAsync(action, ct);

        public async Task<ItemState> PutDownloadedAsync(SyncAction action, ISyncDirectDownloadSource source,
            Func<SyncDownloadStage, CancellationToken, Task>? preservedAfterVerify, CancellationToken ct)
        {
            await CheckTargetAsync(action, action.Path, ct);
            var planned = action.Source ?? throw new InvalidOperationException("Download requires source metadata.");
            if (planned.Kind != ItemKind.File || planned.Hash is null)
                throw new ArgumentException("A local download requires a file source with a content hash.", nameof(action));
            var stage = new SyncDownloadStage("rehearsal:" + action.ActionId, "rehearsal:" + action.ActionId,
                action.RecoveryFirst ? "rehearsal:recovery:" + action.ActionId : "", false);
            if (preservedAfterVerify is not null) await preservedAfterVerify(stage, ct);
            await source.DownloadToAsync(planned, Stream.Null, ct);
            if (preservedAfterVerify is not null) await preservedAfterVerify(stage with { Verified = true }, ct);
            return await PutAsync(action, Stream.Null, ct);
        }

        public Task<ActionResult> ResumeDownloadAsync(SyncAction action, ProviderEffectCheckpoint checkpoint,
            CancellationToken ct) => Task.FromResult(new ActionResult(action.ActionId, Outcome.Uncertain,
                null, null, null, "not simulated: retained direct-download checkpoint reconciliation"));
    }

    private sealed class CloudOverlay(RootDef root, Side side, RehearsalOverlay storage, Action<RehearsalEffect> record)
        : Overlay(root, side, storage, record), ISyncDirectDownloadSource, IFolderTreeDeleter
    {
        public async Task<SyncDownloadResult> DownloadToAsync(ItemState item, Stream destination, CancellationToken ct)
        {
            await using var content = await OpenReadAsync(item, ct);
            return new SyncDownloadResult(item.ObjectId, item.VersionId ?? "", item.Size, item.Hash ?? "");
        }

        public async Task<IReadOnlyList<FolderTreeOutcome>> DeleteFolderTreesAsync(string scanCursor,
            IReadOnlyList<FolderTree> trees, Func<IReadOnlyList<FolderTree>, string, Task> beforeSubmit, CancellationToken ct)
        {
            var accepted = new List<FolderTree>();
            var outcomes = new List<FolderTreeOutcome>();
            foreach (var tree in trees)
            {
                var expected = tree.Victims.Append(tree.Folder).ToDictionary(item => PathKey.Of(item.Path));
                var changed = false;
                var seen = 0;
                await ReadSubtreeAsync(tree.Folder.Path, page =>
                {
                    foreach (var item in page)
                    {
                        if (PairPath.Same(item.Path, tree.Folder.Path)) continue;
                        seen++;
                        if (!expected.TryGetValue(PathKey.Of(item.Path), out var victim) ||
                            victim.Kind != item.Kind || victim.ObjectId != item.ObjectId || victim.VersionId != item.VersionId ||
                            victim.Hash != item.Hash || victim.Size != item.Size) changed = true;
                    }
                    return Task.CompletedTask;
                }, ct);
                var folder = await ObserveAsync(tree.Folder.Path, ct);
                changed |= folder?.ObjectId != tree.Folder.ObjectId || seen != tree.Victims.Count;
                if (changed) outcomes.Add(FolderTreeOutcome.Of(tree, FolderTreeStatus.NotSubmitted,
                    "The rehearsal folder changed after the scan."));
                else accepted.Add(tree);
            }
            if (accepted.Count > 0)
            {
                await beforeSubmit(accepted, "rehearsal:whole-folder");
                foreach (var tree in accepted)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var victim in tree.Victims) Save("Delete", victim.Path, null, victim);
                    Save("DeleteWholeFolder", tree.Folder.Path, null, tree.Folder);
                    outcomes.Add(FolderTreeOutcome.Of(tree, FolderTreeStatus.Completed));
                }
            }
            return outcomes;
        }

        public Task<IReadOnlyList<FolderTreeOutcome>> ReconcileFolderTreesAsync(string scanCursor,
            IReadOnlyList<FolderTree> trees, string recoveryState, Func<string, Task> checkpoint, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FolderTreeOutcome>>(trees.Select(tree => FolderTreeOutcome.Of(tree,
                FolderTreeStatus.Uncertain, "not simulated: retained whole-folder checkpoint reconciliation")).ToArray());
    }
}

internal sealed record RehearsalEffect(string Kind, string Side, string Provider, string Path, long Size, string? Hash);
