namespace Massdex.SyncShark.V2;

// R2-2: a released deletion-evidence survivor always ends in an explicit decision. It is never
// retried forever, and a folder or flagged survivor never receives deletion authority.
internal static partial class RunChecks
{
    internal static async Task ReleasedSurvivorChecksAsync()
    {
        await ReleasedSurvivor_HashlessFileAsync(false);
        await ReleasedSurvivor_HashlessFileAsync(true);
        await ReleasedSurvivor_FolderAsync();
        await ReleasedSurvivor_FlaggedAsync();
        Ignore_ReportsExclusion();
    }

    private static async Task ReleasedSurvivor_HashlessFileAsync(bool restore)
    {
        var fx = Build();
        var local = fx.A.Seed("hashless.txt", "survivor");
        // The real local store can hold a scan row without a content hash; its observation then has none either.
        fx.A.InventoryWithoutHashes = true;
        fx.Provider.AddWriter(fx.Pair.A, new HashlessObserver(fx.WriterA));
        fx.Store.MarkFirstSyncDone(fx.Pair.PairId);
        fx.Store.SaveSyncRetry(fx.Pair.PairId, new(local.Path, "deletion-evidence", 3, "history unavailable",
            DateTimeOffset.MinValue, Held: new HeldDeletion(local.Path, Side.A)));
        fx.Provider.AddInventory(fx.Pair.B, new RecoveryEvidenceInventory(fx.B));
        var run = fx.Run();
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Deferred == 0 && fx.Store.CountSyncRetries(fx.Pair.PairId) == 0,
            "a released survivor without a hash becomes a decision, not a forever retry");
        Require(!fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.ReasonKey == "sync-retry"),
            "a hashless released survivor never falls back to the generic retry notice");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
            "the hashless decision holds both directions before the user chooses");
        var notice = fx.Store.OpenNotices(fx.Pair.PairId)
            .Single(item => item.Path == local.Path && item.ReasonKey == "deletion-review");
        Require(NoticeChoices.Options(notice).Select(option => option.Choice).SequenceEqual(
            new[] { NoticeChoices.DeleteReviewed, NoticeChoices.KeepRestore, NoticeChoices.Ignore }),
            "a hashless ordinary file still offers delete, keep-restore and ignore");
        var decisionRow = fx.Store.SyncRetryAt(fx.Pair.PairId, local.Path);
        var proposal = decisionRow?.ProposedActions is { Count: 1 } proposed ? proposed[0] : null;
        Require(decisionRow?.Kind == "decision" &&
            proposal is { Kind: ActionKind.Delete, RecoveryFirst: true } stamped && stamped.ExpectedTarget is not null,
            "the hashless delete proposal stays stamped and Recovery-first");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!,
            restore ? NoticeChoices.KeepRestore : NoticeChoices.DeleteReviewed, CancellationToken.None);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        if (!restore)
            Require(fx.WriterA.Preserves == 1 && fx.WriterA.Deletes == 1 && fx.WriterB.Deletes == 0 &&
                await fx.A.ObserveAsync(local.Path, CancellationToken.None) is null,
                "the hashless released delete preserves the survivor before removing it");
        else
            Require(fx.WriterA.Deletes == 0 && fx.WriterB.Puts == 1 &&
                (await fx.A.ObserveAsync(local.Path, default))?.Hash == local.Hash &&
                (await fx.B.ObserveAsync(local.Path, default))?.Hash == local.Hash,
                "the hashless survivor's Keep choice restores matching bytes without deleting local content");
        Console.WriteLine("PASS ReleasedSurvivor_HashlessFile");
    }

    private static async Task ReleasedSurvivor_FolderAsync()
    {
        var fx = Build();
        var folder = fx.A.Seed("gone", "", ItemKind.Folder);
        fx.Store.MarkFirstSyncDone(fx.Pair.PairId);
        fx.Store.SaveSyncRetry(fx.Pair.PairId, new(folder.Path, "deletion-evidence", 2, "history unavailable",
            DateTimeOffset.MinValue, Held: new HeldDeletion(folder.Path, Side.A)));
        var run = fx.Run();
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Deferred == 0 && fx.Store.CountSyncRetries(fx.Pair.PairId) == 0,
            "a released folder survivor becomes a decision, not a forever retry");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
            "the released folder decision holds both directions before the user chooses");
        var notice = fx.Store.OpenNotices(fx.Pair.PairId)
            .Single(item => item.Path == folder.Path && item.ReasonKey == "deletion-review");
        Require(NoticeChoices.Options(notice).Select(option => option.Choice).SequenceEqual(
            new[] { NoticeChoices.KeepRestore, NoticeChoices.Ignore }),
            "a released folder survivor offers keep-restore and ignore only, never delete authority");
        await ExpectAsync<SyncRunRefusedException>(() => run.ApplyRetryNoticeChoiceAsync(
            fx.Pair.PairId, notice.NoticeId!, NoticeChoices.DeleteReviewed, CancellationToken.None));
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, folder.Path) is { Kind: "decision" } &&
            fx.WriterA.Deletes == 0, "a folder survivor cannot be given deletion authority");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.KeepRestore,
            CancellationToken.None);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(await fx.B.ObserveAsync(folder.Path, CancellationToken.None) is { Kind: ItemKind.Folder } &&
            fx.WriterB.MkDirs == 1 && fx.WriterA.Deletes == 0,
            "keep-restore recreates the folder on Dropbox without deleting the survivor");
        Console.WriteLine("PASS ReleasedSurvivor_Folder");
    }

    private static async Task ReleasedSurvivor_FlaggedAsync()
    {
        var fx = Build();
        var local = fx.A.Seed("flagged.txt", "survivor");
        var flags = ItemFlags.Placeholder;
        fx.Provider.AddInventory(fx.Pair.A, new FlaggedInventory(fx.A, local.Path, flags));
        fx.Provider.AddWriter(fx.Pair.A, new FlaggedWriter(fx.WriterA, local.Path, flags));
        fx.Store.MarkFirstSyncDone(fx.Pair.PairId);
        fx.Store.SaveSyncRetry(fx.Pair.PairId, new(local.Path, "deletion-evidence", 3, "history unavailable",
            DateTimeOffset.MinValue, Held: new HeldDeletion(local.Path, Side.A)));
        fx.Provider.AddInventory(fx.Pair.B, new RecoveryEvidenceInventory(fx.B));
        var run = fx.Run();
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Deferred == 0 && fx.Store.CountSyncRetries(fx.Pair.PairId) == 0,
            "a released flagged survivor becomes a decision, not a forever retry");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
            "the flagged released decision holds both directions before the user chooses");
        var notice = fx.Store.OpenNotices(fx.Pair.PairId)
            .Single(item => item.Path == local.Path && item.ReasonKey == "deletion-review");
        Require(NoticeChoices.Options(notice).Select(option => option.Choice).SequenceEqual(
            new[] { NoticeChoices.KeepRestore, NoticeChoices.Ignore }),
            "a released flagged survivor offers keep-restore and ignore only, never delete authority");
        await ExpectAsync<SyncRunRefusedException>(() => run.ApplyRetryNoticeChoiceAsync(
            fx.Pair.PairId, notice.NoticeId!, NoticeChoices.DeleteReviewed, CancellationToken.None));
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.KeepRestore,
            CancellationToken.None);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(fx.WriterA.Deletes == 0 && fx.WriterA.Preserves == 0 && fx.WriterB.Puts == 0 &&
            fx.WriterB.Deletes == 0 && fx.WriterB.MkDirs == 0 &&
            await fx.A.ObserveAsync(local.Path, CancellationToken.None) is not null,
            "the safety guards refuse delete and restore writes over a protected flagged survivor");
        Console.WriteLine("PASS ReleasedSurvivor_Flagged");
    }

    private static void Ignore_ReportsExclusion()
    {
        var result = NoticeChoices.Result(NoticeChoices.Ignore);
        Require(result.Contains("Excluded", StringComparison.Ordinal) &&
            !result.Contains("dismissed", StringComparison.OrdinalIgnoreCase),
            "ignore reports the permanent pair exclusion it actually creates");
        var notice = new Notice("p1", 40, NoticeKind.Info, "ignored.txt", "retry", "sync-retry");
        Require(NoticeChoices.Options(notice).Single(option => option.Choice == NoticeChoices.Ignore)
            .Label.Contains("exclude", StringComparison.OrdinalIgnoreCase),
            "the ignore option says it excludes the path, not that it leaves scheduling alone");
        Console.WriteLine("PASS Ignore_ReportsExclusion");
    }

    /// <summary>A local writer whose observation has no content hash, as an unhashed store row does.</summary>
    private sealed class HashlessObserver(IEndpointWriter inner) : IEndpointWriter, ISourceAbsenceProbe
    {
        public Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct) =>
            ((ISourceAbsenceProbe)inner).IsSourceAbsentAsync(historical, path, ct);
        public Task<bool?> IsHistoricalSourcePresentAsync(ItemState historical, CancellationToken ct) =>
            ((ISourceAbsenceProbe)inner).IsHistoricalSourcePresentAsync(historical, ct);
        public Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct) =>
            ((ISourceAbsenceProbe)inner).ObserveSourceMetadataAsync(path, ct);

        public async Task<ItemState?> ObserveAsync(string path, CancellationToken ct)
        {
            var item = await inner.ObserveAsync(path, ct).ConfigureAwait(false);
            return item is null ? null : item with { Hash = null };
        }

        public Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct) =>
            inner.PutAsync(a, content, ct);

        public Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct) => inner.MoveAsync(a, ct);

        public Task DeleteAsync(SyncAction a, CancellationToken ct) => inner.DeleteAsync(a, ct);

        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => inner.MkDirAsync(path, ct);

        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) =>
            inner.PreserveAsync(victim, actionId, ct);

        public Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct) => inner.OpenReadAsync(s, ct);
    }

    /// <summary>Reports one path as flagged, in inventory and observation, as a protected local entry does.</summary>
    private sealed class FlaggedWriter(IEndpointWriter inner, string path, ItemFlags flags) : IEndpointWriter, ISourceAbsenceProbe
    {
        public Task<bool?> IsSourceAbsentAsync(ItemState? historical, string observed, CancellationToken ct) =>
            ((ISourceAbsenceProbe)inner).IsSourceAbsentAsync(historical, observed, ct);
        public Task<bool?> IsHistoricalSourcePresentAsync(ItemState historical, CancellationToken ct) =>
            ((ISourceAbsenceProbe)inner).IsHistoricalSourcePresentAsync(historical, ct);
        public async Task<ItemState?> ObserveSourceMetadataAsync(string observed, CancellationToken ct)
        {
            var item = await ((ISourceAbsenceProbe)inner).ObserveSourceMetadataAsync(observed, ct).ConfigureAwait(false);
            return item is null ? null : Flag(item);
        }

        private ItemState Flag(ItemState item) => PairPath.Same(item.Path, path) ? item with { Flags = flags } : item;

        public async Task<ItemState?> ObserveAsync(string observed, CancellationToken ct)
        {
            var item = await inner.ObserveAsync(observed, ct).ConfigureAwait(false);
            return item is null ? null : Flag(item);
        }

        public Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct) =>
            inner.PutAsync(a, content, ct);

        public Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct) => inner.MoveAsync(a, ct);

        public Task DeleteAsync(SyncAction a, CancellationToken ct) => inner.DeleteAsync(a, ct);

        public Task<ItemState> MkDirAsync(string directory, CancellationToken ct) => inner.MkDirAsync(directory, ct);

        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) =>
            inner.PreserveAsync(victim, actionId, ct);

        public Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct) => inner.OpenReadAsync(s, ct);
    }

    /// <summary>Reports one path as flagged through the inventory as well as through the writer.</summary>
    private sealed class FlaggedInventory(IInventorySource inner, string path, ItemFlags flags) : IInventorySource
    {
        private ItemState Flag(ItemState item) => PairPath.Same(item.Path, path) ? item with { Flags = flags } : item;

        public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> progress,
            CancellationToken ct)
        {
            var snapshot = await inner.FullAsync(gen, include, progress, ct).ConfigureAwait(false);
            return snapshot with { Items = snapshot.Items.Select(Flag).ToArray() };
        }

        public async Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include,
            CancellationToken ct)
        {
            var delta = await inner.ChangesSinceAsync(cursor, gen, include, ct).ConfigureAwait(false);
            return delta with
            {
                Changes = delta.Changes.Select(change => change.State is not null
                    ? change with { State = Flag(change.State) } : change).ToArray()
            };
        }

        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            inner.HashAsync(paths, ct);

        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
            CancellationToken ct) => inner.DeletionTimesAsync(paths, ct);
    }
}
