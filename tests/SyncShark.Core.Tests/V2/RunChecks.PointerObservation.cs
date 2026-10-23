namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task PointerObservationRetryAsync(string folder)
    {
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var item = fx.A.Seed("pointer.txt", "same bytes");
        fx.B.Seed(item.Path, "same bytes");
        await FirstSyncAsync(fx.Run(), fx);
        var before = store.ByObject(fx.Pair.A.RootId, item.ObjectId)! with { Flags = ItemFlags.OwnedPointer };
        MappingSnapshot(store, fx.Pair.A, Side.A, before);
        var inventory = new UnobservedPointerInventory(fx.A, item.Path);
        fx.Provider.AddInventory(fx.Pair.A, inventory);
        var writesA = fx.WriterA.Calls;
        var writesB = fx.WriterB.Calls;
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var retry = store.SyncRetryAt(fx.Pair.PairId, item.Path);
        Require(retry is not null && retry.LastError.StartsWith(OwnedPointerProof.ObservationMessage, StringComparison.Ordinal),
            "unobserved native metadata creates the existing durable observation retry");
        var notice = store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == "sync-retry" && n.Path == item.Path);
        Require(store.ByObject(fx.Pair.A.RootId, item.ObjectId) == before,
            "incomplete native observation preserves the exact durable entity rather than inferring absence");
        // Force the existing retry due without a sleep, then execute the real subsequent run.
        store.RequestOwnedPointerObservationRetriesNow(fx.Pair.PairId, OwnedPointerProof.ObservationMessage);
        var readsBefore = inventory.MetadataReads;
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(inventory.MetadataReads > readsBefore, "the next run re-observes due metadata instead of stranding it");
        var repeated = store.OpenNotices(fx.Pair.PairId).Where(n => n.ReasonKey == "sync-retry" && n.Path == item.Path).ToArray();
        Require(repeated.Length == 1 && repeated[0].NoticeId == notice.NoticeId,
            "repeated unobserved metadata keeps one deduplicated actionable notice");
        Require(inventory.HashCalls == 0 && fx.WriterA.Calls == writesA && fx.WriterB.Calls == writesB,
            "unobserved metadata cannot hash, upload or delete on either side");
        Require(!store.OpenNotices(fx.Pair.PairId).Any(n => n.Storage?.Operation == "pointer-verify"),
            "observation retry is not false pointer verification debt");
    }

    /// <summary>F2 regression: an Ignore on the observation summary outlives only the failures it silenced.
    /// Ignore, then a scan with zero transient failures, then a new outage must produce an active notice.</summary>
    internal static async Task PointerObservationIgnoreExpiryAsync(string folder)
    {
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var count = StorageNotices.NoticeAggregateLimit + 1;
        var first = Enumerable.Range(0, count).Select(index => $"first-{index:D2}.txt").ToArray();
        var second = Enumerable.Range(0, count).Select(index => $"second-{index:D2}.txt").ToArray();
        foreach (var path in first.Concat(second))
        {
            fx.A.Seed(path, "same bytes");
            fx.B.Seed(path, "same bytes");
        }
        await FirstSyncAsync(fx.Run(), fx);
        var inventory = new UnobservedPointerInventory(fx.A, first);
        fx.Provider.AddInventory(fx.Pair.A, inventory);

        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var summary = store.OpenNotices(fx.Pair.PairId).Single(notice =>
            notice.ReasonKey == NoticeChoices.PointerObservationSummary);
        Require(summary.NoticeId is not null &&
            store.OwnedPointerObservationSummaryState(fx.Pair.PairId) == ObservationSummaryState.Open,
            "an above-limit observation outage raises one open pair summary");
        store.CloseNotice(summary.NoticeId!, NoticeChoices.Ignore);
        Require(store.OwnedPointerObservationSummaryState(fx.Pair.PairId) == ObservationSummaryState.Ignored,
            "Ignore silences the pair summary while the failure continues");

        // The same continuing failure stays suppressed: no per-file flood, no reopening, debt untouched.
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(store.OwnedPointerObservationSummaryState(fx.Pair.PairId) == ObservationSummaryState.Ignored &&
            !store.OpenNotices(fx.Pair.PairId).Any(notice =>
                notice.ReasonKey is "sync-retry" or NoticeChoices.PointerObservationSummary),
            "the same continuing failure stays suppressed without per-file notices");
        Require(store.SyncRetryAt(fx.Pair.PairId, first[0]) is { LastError.Length: > 0 },
            "suppression leaves every entity observation retry in place");

        // A scan that observes no transient failure retires the ignore.
        inventory.Fail = false;
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(store.OwnedPointerObservationSummaryState(fx.Pair.PairId) == ObservationSummaryState.None,
            "an ignored summary expires once a scan observes no transient failure");

        // A later, unrelated outage must raise an actionable notice again rather than stay suppressed.
        inventory.Paths = second;
        inventory.Fail = true;
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var raised = store.OpenNotices(fx.Pair.PairId).Single(notice =>
            notice.ReasonKey == NoticeChoices.PointerObservationSummary);
        Require(raised.NoticeId is not null &&
            store.OwnedPointerObservationSummaryState(fx.Pair.PairId) == ObservationSummaryState.Open,
            "a new failure after recovery raises a new active pair summary instead of staying suppressed");
        Require(!store.OpenNotices(fx.Pair.PairId).Any(notice =>
                notice.ReasonKey == "sync-retry" && notice.Path.Length > 0),
            "the fresh summary still replaces per-file transient notices");
        Require(store.SyncRetryAt(fx.Pair.PairId, second[0]) is { LastError.Length: > 0 },
            "the fresh summary keeps each new entity retry durable");
    }

    private sealed class UnobservedPointerInventory(FakeEndpoint inner, params string[] paths) : IInventorySource
    {
        /// <summary>False models the outage clearing: the endpoints are listed normally, with no incomplete scope.</summary>
        internal bool Fail { get; set; } = true;
        internal string[] Paths { get; set; } = paths;
        internal int MetadataReads { get; private set; }
        internal int HashCalls { get; private set; }
        private bool Hidden(string candidate) => Fail && Paths.Contains(candidate, StringComparer.Ordinal);
        public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> progress, CancellationToken ct)
        {
            MetadataReads++;
            var snapshot = await inner.FullAsync(gen, include, progress, ct);
            if (!Fail) return snapshot;
            return snapshot with { Items = snapshot.Items.Where(item => !Hidden(item.Path)).ToArray(),
                IncompleteSubtrees = Paths,
                IncompleteReasons = Paths.ToDictionary(path => path, _ => OwnedPointerProof.ObservationMessage),
                IncludesPath = candidate => !Hidden(candidate) && include(candidate) };
        }
        public async Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct)
        {
            MetadataReads++;
            var delta = await inner.ChangesSinceAsync(cursor, gen, include, ct);
            if (!Fail) return delta;
            return delta with { Changes = delta.Changes.Where(change => !Hidden(change.Path)).ToArray(),
                IncompleteSubtrees = Paths,
                IncompleteReasons = Paths.ToDictionary(path => path, _ => OwnedPointerProof.ObservationMessage) };
        }
        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct)
        {
            HashCalls++;
            throw new IOException("Unobserved native metadata must never authorize content hashing.");
        }
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            inner.DeletionTimesAsync(paths, ct);
    }
}
