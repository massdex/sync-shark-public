namespace Massdex.SyncShark.V2;

internal static class DateRepairSeamChecks
{
    internal static async Task RunAsync(string root)
    {
        var directory = Path.Combine(root, "date-seams");
        Directory.CreateDirectory(directory);
        var state = Path.Combine(directory, "state");
        using var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        IDateRepairStore dates = store;
        var pair = new PairDef("date-seams", "Date seams",
            new RootDef("a", SyncProviders.Local, "", directory, "unused"),
            new RootDef("b", SyncProviders.Dropbox, "account", "/test", "id:root"), [], [], true);
        store.SavePair(pair);
        var list = Path.Combine(directory, "exclude.txt");
        var missing = Path.Combine(directory, "missing.txt");
        // Creating both sequences must be inert, even for nonexistent files.
        var lazyExclusions = DateRepairInputs.Exclusions(missing);
        var lazyProvenance = DateRepairInputs.Provenance([new DateProvenanceSource(missing, "")]);
        LocalChecks.Require(lazyExclusions is not null && lazyProvenance is not null, "file inputs are lazy");
        foreach (var text in new[] { "/rooted", "C:relative", "../unsafe" })
        {
            File.WriteAllText(list, text);
            var error = await Failure(DateRepairInputs.Exclusions(list), []);
            if (text == "../unsafe")
                LocalChecks.Require(error is ArgumentException, "unsafe segment keeps ArgumentException rather than usage translation");
            else
                LocalChecks.Require(error is SyncCliUsageException { ExitCode: SyncCliExitCode.Usage } &&
                    error.Message == "--exclude-list must contain relative pair paths, one per line.", "rooted and colon paths retain usage text and code");
        }
        LocalChecks.Require(await Failure(DateRepairInputs.Exclusions(missing), []) is IOException, "missing exclusions retain IO failure");
        LocalChecks.Require(await Failure([], DateRepairInputs.Provenance([new DateProvenanceSource(missing, "")])) is IOException, "missing provenance retains IO failure");
        LocalChecks.Require(!Directory.Exists(Path.Combine(directory, "v2", "logs")), "invalid inputs never create a report");
        var enumerated = false;
        IEnumerable<string> InvalidOrder()
        {
            enumerated = true;
            yield return "unused";
        }
        Exception? rangeError = null;
        try
        {
            await DateRepair.RunAsync(store, pair, (_, _) => throw new InvalidOperationException(), directory,
                true, [], default, InvalidOrder(), [], false, StorageFailureClassifier.IsStateDamage,
                StorageFailureClassifier.IsDirectStateDamage, StorageFailureClassifier.IsBusy,
                localDate: new DateOnly(2026, 1, 1), from: new DateOnly(2026, 1, 1));
        }
        catch (Exception error) { rangeError = error; }
        LocalChecks.Require(rangeError is SyncCliUsageException && !enumerated, "range validation still precedes input enumeration");

        File.WriteAllLines(list, ["", "  ", "Parent/Child", "parent/child"]);
        dates.PrepareDateRepairExclusions(DateRepairInputs.Exclusions(list));
        LocalChecks.Require(dates.DateRepairExcluded("PARENT/CHILD") && !dates.DateRepairExcluded("Parent"),
            "prepared exclusions preserve normalized case-insensitive exact matching");
        var stamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var local = new ItemState("Child", ItemKind.File, "local-id", null,
            LocalChecks.BlockHash(LocalChecks.Bytes("same")), 4, stamp, ItemFlags.None);
        DateProvenanceChecks.Seed(store, pair, local, state);
        dates.PrepareDateWriteEvidence(pair.PairId, [], false);
        LocalChecks.Require(dates.DateWriteSkipReason(Side.A, local, local.Hash) is null, "closed restore notice remains evidence");
        dates.PrepareDateWriteEvidence(pair.PairId, [], true);
        LocalChecks.Require(dates.DateWriteSkipReason(Side.A, local, local.Hash) == "required Sync Shark provenance log match unavailable",
            "required logs cannot fall back to a closed restore notice");
        foreach (var delta in new[] { -1L, 0L, 2 * TimeSpan.TicksPerSecond, 2 * TimeSpan.TicksPerSecond + 1 })
        {
            dates.PrepareDateWriteEvidence(pair.PairId, [(local.Path, stamp.AddTicks(delta))], true);
            LocalChecks.Require(dates.DateLogCorrelated(local) == (delta >= 0 && delta <= 2 * TimeSpan.TicksPerSecond),
                "prepared provenance preserves inclusive two-second correlation");
        }

        var cloud = local with { ObjectId = "id:child", Mtime = stamp.AddYears(-2) };
        var generation = store.BeginGeneration("a", "test");
        store.CommitResult(pair.PairId, generation,
            new SyncAction("child", 0, ActionKind.Adopt, Side.A, local.Path, null, cloud, local, false, "child"),
            new ActionResult("child", Outcome.Done, local, cloud, null, null));
        var baseline = store.Baselines(pair.PairId, ["child"]).Single();
        var primary = new IOException("original repair failure");
        var rollback = new IOException("rollback failure");
        Exception? caught = null;
        var calls = new List<DateTimeOffset>();
        try
        {
            dates.RepairDate(baseline, Side.A, cloud.Mtime, value =>
            {
                calls.Add(value);
                throw calls.Count == 1 ? primary : rollback;
            }, local.Mtime, null, local.Hash);
        }
        catch (Exception error) { caught = error; }
        LocalChecks.Require(ReferenceEquals(caught, primary) &&
            Equals(primary.Data["DateRepairRollbackError"], rollback.ToString()) &&
            calls.SequenceEqual(new[] { cloud.Mtime, local.Mtime }) &&
            store.Baselines(pair.PairId, ["child"]).Single() == baseline,
            "repair rollback retains original exception, rollback detail and baseline");

        foreach (var code in new[] { 11, 26, 5, 6, 1 })
        {
            var direct = new Microsoft.Data.Sqlite.SqliteException("synthetic", code);
            var damage = code is 11 or 26;
            LocalChecks.Require(DateRepair.ItemFailure(direct, _ => false, StorageFailureClassifier.IsDirectStateDamage) == !damage,
                "direct damage fallback remains when the deep predicate returns false");
            foreach (var error in new Exception[] { direct, new IOException("wrapped", direct), new AggregateException(direct) })
                LocalChecks.Require(DateRepair.ItemFailure(error, StorageFailureClassifier.IsStateDamage,
                    StorageFailureClassifier.IsDirectStateDamage) == !damage, "deep date damage classification is retained");
            LocalChecks.Require(StorageFailureClassifier.IsBusy(direct) == (code is 5 or 6) &&
                !StorageFailureClassifier.IsBusy(new IOException("wrapped", direct)), "page busy predicate stays shallow");
        }
        foreach (var error in new Exception[] { new OperationCanceledException(), new OutOfMemoryException(),
            new StateDamagedException("synthetic"), new SyncCliBusyException("synthetic") })
            LocalChecks.Require(!DateRepair.ItemFailure(error, _ => false, StorageFailureClassifier.IsDirectStateDamage),
                "date non-SQLite exclusions remain independent of the deep predicate");

        async Task<Exception?> Failure(IEnumerable<string> exclusions, IEnumerable<(string Path, DateTimeOffset Occurred)> rows)
        {
            try
            {
                await DateRepair.RunAsync(store, pair, (_, _) => throw new InvalidOperationException(), directory,
                    true, [], default, exclusions, rows, true, StorageFailureClassifier.IsStateDamage,
                    StorageFailureClassifier.IsDirectStateDamage, StorageFailureClassifier.IsBusy);
                return null;
            }
            catch (Exception error) { return error; }
        }
    }
}
