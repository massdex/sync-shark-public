namespace Massdex.SyncShark.V2;

/// <summary>All OS mutations are fake. These checks never query or resize a machine's USN journal.</summary>
internal static class JournalSizingChecks
{
    public static async Task RunAsync(string root)
    {
        const long gb = JournalSizing.Minimum;
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var first = JournalSizing.Observe(null, 1, 100, now);
        var sampled = JournalSizing.Observe(first, 1, 2100, now.AddHours(2));
        Require(sampled.BytesPerHour == 1000 && sampled.LongestGapHours == 2, "USN bytes / elapsed hours");
        Require(JournalSizing.Observe(sampled, 1, 3000, now.AddHours(2).AddSeconds(1)) == sampled, "root reopen coalesced");
        Require(JournalSizing.Observe(sampled, 2, 20, now.AddHours(4)).BytesPerHour == 0, "journal replacement resets rate");
        Require(JournalSizing.Observe(sampled, 1, 20, now.AddHours(4)).BytesPerHour == 0, "backwards cursor resets rate");
        Require(JournalSizing.Observe(sampled, 1, 2200, now).Observed == sampled.Observed, "clock rollback ignored");
        Require(JournalSizing.Target(first, 32 * 1024 * 1024, 100 * gb, false) == gb, "initial floor");
        Require(JournalSizing.Target(first, 2 * gb, 100 * gb, false) == 2 * gb, "never shrink");
        Require(JournalSizing.Target(first, 2 * gb, 100 * gb, true) == 4 * gb, "gap doubles size");
        Require(JournalSizing.Target(first, 6 * gb, 1000 * gb, true) == 8 * gb - SyncSharkJournalNative.Delta, "8 GiB cap includes allocation overhead");
        Require(JournalSizing.Target(first, gb, 40 * gb, true) == 2 * gb - SyncSharkJournalNative.Delta, "5 percent cap includes allocation overhead");
        Require(JournalSizing.Target(first, 8 * gb - SyncSharkJournalNative.Delta, 1000 * gb, true) ==
            8 * gb - SyncSharkJournalNative.Delta, "at cap does not create an impossible repeated growth request");
        Require(JournalSizing.Target(first, 4 * gb, 20 * gb, true) == 4 * gb, "low free space never shrinks");
        Require(JournalSizing.Target(first with { Never = true }, gb, 1000 * gb, true) == gb, "Never suppresses growth");
        var busy = first with { BytesPerHour = gb / 100.0 };
        Require(JournalSizing.Target(busy, gb, 1000 * gb, false) > 3 * gb, "seven-day safety window and double headroom");
        Require(JournalSizing.Target(busy with { LongestGapHours = 100 }, gb, 1000 * gb, false) == 6 * gb, "three times longest run gap");
        Require(SyncSharkJournalNative.Target(32 * 1024 * 1024, 100 * gb, gb, true) == gb, "installer baseline is one GiB");
        Require(SyncSharkJournalNative.Target(4 * gb, 100 * gb, gb, true) == 4 * gb, "installer preserves larger journals");
        Require(SyncSharkJournalNative.Delta == 512 * 1024 * 1024, "allocation delta is 512 MiB");
        var wire = SyncDiscoveryClient.JournalRequest(new SyncDiscoveryRoot(@"C:\a", 123, 456), gb, new byte[32]);
        Require(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(8)) == 2 &&
            System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(wire.AsSpan(wire.Length - 8)) == gb,
            "silent request selects allowlisted operation and bounded maximum, without executing it");
        // Build only the encoded allowlisted program; do not run it.
        var command = AttendedElevatedAction.Command(AttendedElevatedAction.JournalGrow, ["C:", "123", gb.ToString()]);
        Require(System.Text.Encoding.Unicode.GetByteCount(command) * 4 / 3 < 30000, "encoded command fits Windows command-line limit");
        var refused = false;
        try { AttendedElevatedAction.Command(AttendedElevatedAction.JournalGrow, ["C:;evil", "123", gb.ToString()]); }
        catch (ArgumentException) { refused = true; }
        Require(refused, "volume arguments cannot inject code");

        var folder = Path.Combine(root, "journal-sizing-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock(now);
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, clock))
        {
            store.SavePair(Pair("p1")); store.SavePair(Pair("p2"));
            void Observe(string pair = "p1") => store.ObserveJournal(pair, "C:", 123, 1, 100, 32 * 1024 * 1024, 100 * gb, true, clock.GetUtcNow());
            Observe(); Observe(); Observe("p2");
            var notice = store.OpenNotices(null).Single();
            Require(notice.PairId == "p1" && NoticeChoices.Options(notice).Count == 3, "one volume notice across two pairs");
            var fake = new PendingElevation();
            var apply = JournalSizing.ApplyCoreAsync(store, notice, JournalSizing.Enlarge, fake, CancellationToken.None, _ => [1, 0, 0, 0, gb, SyncSharkJournalNative.Delta, 100 * gb, 123]);
            Require(!apply.IsCompleted && fake.Calls == 1, "resize waits only on explicit choice");
            // An unrelated state operation, and an inventory observation, finish during the outstanding UAC.
            store.SavePair(Pair("p3")); Observe("p2");
            Require(store.Pairs().Count == 3 && store.OpenNotices(null).Count == 1, "outstanding UAC holds no store/run lock");
            var duplicate = false;
            try { await JournalSizing.ApplyCoreAsync(store, notice, JournalSizing.Enlarge, fake, CancellationToken.None, _ => [1, 0, 0, 0, gb, SyncSharkJournalNative.Delta, 100 * gb, 123]); }
            catch (InvalidOperationException) { duplicate = true; }
            Require(duplicate && fake.Calls == 1, "double-click never launches a second UAC");
            fake.Done.SetResult(1);
            try { await apply; throw new Exception("failed resize unexpectedly succeeded"); }
            catch (IOException) { }
            Require(store.OpenNotices(null).Count == 1, "failed resize keeps notice");
            await JournalSizing.ApplyCoreAsync(store, notice, JournalSizing.NotNow, fake, CancellationToken.None, _ => [1, 0, 0, 0, gb, SyncSharkJournalNative.Delta, 100 * gb, 123]);
            Observe(); Require(store.OpenNotices(null).Count == 0 && fake.Calls == 1, "Not now has no UAC and suppresses notices");
            clock.Now = now.AddDays(8); Observe();
            notice = store.OpenNotices(null).Single();
            await JournalSizing.ApplyCoreAsync(store, notice, JournalSizing.Never, fake, CancellationToken.None, _ => [1, 0, 0, 0, gb, SyncSharkJournalNative.Delta, 100 * gb, 123]);
        }
        using (var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, clock))
        {
            reopened.ObserveJournal("p2", "C:", 123, 1, 1000, 32 * 1024 * 1024, 1000 * gb, true, now.AddDays(100));
            Require(reopened.OpenNotices(null).Count == 0, "Never survives process restart and other pairs");
            reopened.ObserveJournal("p2", "C:", 456, 2, 100, 32 * 1024 * 1024, 1000 * gb, true, now.AddDays(101));
            var replacement = reopened.OpenNotices(null).Single();
            Require(replacement.ReasonKey == JournalSizing.Key(456), "replacement volume gets a distinct notice and no inherited refusal");
            var unchanged = new PendingElevation(); unchanged.Done.SetResult(0);
            try
            {
                await JournalSizing.ApplyCoreAsync(reopened, replacement, JournalSizing.Enlarge, unchanged, CancellationToken.None,
                    _ => [2, 0, 0, 0, gb / 2, SyncSharkJournalNative.Delta, 100 * gb, 456]);
                throw new Exception("attended no-op unexpectedly resolved");
            }
            catch (IOException) { }
            Require(reopened.OpenNotices(null).Count == 1, "attended no-op retains notice too");
            int queries = 0;
            var success = new PendingElevation(); success.Done.SetResult(0);
            await JournalSizing.ApplyCoreAsync(reopened, replacement, JournalSizing.Enlarge, success, CancellationToken.None, _ => [2, 0, 0, 0, ++queries * gb, SyncSharkJournalNative.Delta, 100 * gb, 456]);
            Require(success.Calls == 1 && reopened.OpenNotices(null).Count == 0, "successful fake enlargement closes the notice");
        }
        Require(JournalSizing.Observe(first with { LongestGapHours = 100 }, 1, 200, now.AddHours(24)).LongestGapHours == 90,
            "longest gap decays after normal activity");
        using (var gate = new SemaphoreSlim(0, 1))
        {
            Require(!await SyncDiscoveryClient.EnterDiscoveryGateAsync(gate, false, CancellationToken.None), "automatic scan never queues");
            var check = SyncDiscoveryClient.EnterDiscoveryGateAsync(gate, true, CancellationToken.None);
            Require(!check.IsCompleted && SyncDiscoveryClient.Status.Contains("Busy"), "user check waits with a current busy status");
            gate.Release(); Require(await check, "user check resumes when worker is free");
            Require(!await SyncDiscoveryClient.EnterDiscoveryGateAsync(gate, true, CancellationToken.None, TimeSpan.Zero) &&
                SyncDiscoveryClient.Status.Contains("Retry"), "bounded check reports busy retry rather than stale ready");
        }
        using (var oldWorker = new MemoryStream())
            Require(await SyncDiscoveryClient.ReadJournalReplyAsync(oldWorker, CancellationToken.None) == 1, "old worker EOF falls back");
        foreach (uint code in new uint[] { 0, 112, 183 })
        {
            using var reply = new MemoryStream();
            var bytes = new byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, 12);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 5);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), code);
            reply.Write(bytes); reply.Position = 0;
            Require(await SyncDiscoveryClient.ReadJournalReplyAsync(reply, CancellationToken.None) == code, "worker result code survives decoding");
        }
        var retryClock = new Clock(new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero));
        int attempts = 0;
        Task<uint?> Fail(CancellationToken _) { attempts++; return Task.FromResult<uint?>(1); }
        Require(await SyncDiscoveryClient.TryGrowJournalCoreAsync(Fail, retryClock) == 1, "maintenance failure falls back");
        Require(await SyncDiscoveryClient.TryGrowJournalCoreAsync(Fail, retryClock) is null && attempts == 1, "failure cooldown avoids repeated worker starts");
        retryClock.Now = retryClock.Now.AddHours(1);
        Require(await SyncDiscoveryClient.TryGrowJournalCoreAsync(Fail, retryClock) == 1 && attempts == 2, "maintenance retries after one hour");
        retryClock.Now = retryClock.Now.AddHours(1);
        var held = new TaskCompletionSource<uint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = SyncDiscoveryClient.TryGrowJournalCoreAsync(_ => held.Task, retryClock);
        Require(await SyncDiscoveryClient.TryGrowJournalCoreAsync(Fail, retryClock) is null && attempts == 2, "busy maintenance falls back without queuing");
        held.SetResult(0); Require(await holding == 0, "maintenance releases gate after success");
        await SyncDiscoveryClient.ResetAfterSetupAsync();
        using (var store = SqliteStateStore.Open(folder + "-silent", StorageChecks.Fast, clock))
        {
            store.SavePair(Pair("p1")); store.SavePair(Pair("p2"));
            long size = 32 * 1024 * 1024;
            long[] Query(string _) => [1, 0, 100, 0, size, SyncSharkJournalNative.Delta, 100 * gb, 789];
            void ObserveSilent(string pair = "p1") => store.ObserveJournal(pair, "C:", 789, 1, 100, size, 100 * gb, true,
                clock.GetUtcNow(), deferNotice: true);
            ObserveSilent(); ObserveSilent("p2");
            Require(store.OpenNotices(null).Count == 0, "silent resize starts before any notice appears");
            var pending = store.PendingJournal(789)!;
            Require(pending.State.NoticePair == "p1", "pending silent request belongs to volume across pairs");
            var done = new TaskCompletionSource<uint?>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            Task<uint?> Grow(SyncDiscoveryRoot _, long __) { calls++; return done.Task; }
            var rootIdentity = new SyncDiscoveryRoot(@"C:\a", 789, 456);
            var maintenance = JournalSizing.MaintainAsync(store, pending, rootIdentity, Grow, Query);
            await JournalSizing.MaintainAsync(store, pending, rootIdentity, Grow, Query);
            Require(calls == 1 && store.OpenNotices(null).Count == 0, "concurrent roots deduplicate silent work without notices");
            size = gb; done.SetResult(0); await maintenance;
            Require(store.PendingJournal(789) is null && store.OpenNotices(null).Count == 0, "verified silent success never publishes a notice");
            clock.Now = clock.Now.AddDays(2); size = 32 * 1024 * 1024; ObserveSilent();
            pending = store.PendingJournal(789)!;
            await JournalSizing.MaintainAsync(store, pending, rootIdentity, (_, _) => Task.FromResult<uint?>(0), Query);
            Require(store.PendingJournal(789) is not null && store.OpenNotices(null).Single().Detail.Contains("did not grow"),
                "success reply without observed growth leaves a no-growth explanation");
            await JournalSizing.MaintainAsync(store, pending, rootIdentity, (_, _) => Task.FromResult<uint?>(1), Query);
            var notice = store.OpenNotices(null).Single();
            Require(NoticeChoices.Options(notice).Count == 3 && notice.Detail.Contains("failed"), "failure retains UAC Not now Never choices");
            await JournalSizing.MaintainAsync(store, pending, rootIdentity,
                async (_, _) => await SyncDiscoveryClient.ReadJournalReplyAsync(new MemoryStream(), CancellationToken.None), Query);
            Require(store.OpenNotices(null).Count == 1, "old worker fallback keeps one notice");
            await JournalSizing.MaintainAsync(store, pending, rootIdentity, (_, _) => throw new ObjectDisposedException("fake"), Query);
            Require(store.OpenNotices(null).Single().Detail.Contains("ObjectDisposedException"), "unexpected background failure becomes a notice");
        }
        var remembered = first with { LongestGapHours = 720 };
        for (var i = 1; i <= 100; i++) remembered = JournalSizing.Observe(remembered, 1, 100 + i, now.AddMinutes(i));
        Require(remembered.LongestGapHours > 710, "minute observations retain long absence history");
        Require(SyncSharkJournalNative.Target(gb, 40 * gb, 2 * gb, false) == 2 * gb - SyncSharkJournalNative.Delta,
            "attended cap includes allocation overhead");
        Require(SyncSharkJournalNative.Target(gb, 40 * gb, 2 * gb, false, 3 * gb) == gb,
            "attended oversized delta cannot underflow or shrink");
        var legacy = folder + "-v12";
        using (var store = SqliteStateStore.Open(legacy, StorageChecks.Fast, clock)) store.SavePair(Pair("p1"));
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(legacy)))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "DROP TABLE journal_volume; PRAGMA user_version=12;"; cmd.ExecuteNonQuery();
        }
        for (int i = 0; i < 2; i++)
        {
            using var store = SqliteStateStore.Open(legacy, StorageChecks.Fast, clock);
            store.ObserveJournal("p1", "C:", 789, 1, 100, 32 * 1024 * 1024, 100 * gb, true, clock.GetUtcNow(), true);
            Require(store.PendingJournal(789) is not null, "v12 open gains optional table and second open preserves it");
        }
        using (var store = SqliteStateStore.Open(folder + "-results", StorageChecks.Fast, clock))
        {
            store.SavePair(Pair("p1")); store.SavePair(Pair("p2"));
            void Observe(long size = 32 * 1024 * 1024, string pair = "p1", bool gap = true) =>
                store.ObserveJournal(pair, "C:", 789, 1, 100, size, 100 * gb, gap, clock.GetUtcNow(), true);
            long[] Query(string _) => [1, 0, 100, 0, 32 * 1024 * 1024, SyncSharkJournalNative.Delta, 100 * gb, 789];
            var identity = new SyncDiscoveryRoot(@"C:\a", 789, 456);
            Observe(); var pending = store.PendingJournal(789)!;
            await JournalSizing.MaintainAsync(store, pending, identity, (_, _) => Task.FromResult<uint?>(null), Query);
            Require(store.PendingJournal(789) is not null && store.OpenNotices(null).Count == 0, "busy/cooldown publishes no notice");
            await JournalSizing.MaintainAsync(store, pending, identity, (_, _) => Task.FromResult<uint?>(112), Query);
            Require(store.OpenNotices(null).Single().Detail.Contains("Not enough free space") &&
                store.OpenNotices(null).Single().Detail.Contains("Retry"), "disk-full notice explains free-space resolution");
            await JournalSizing.MaintainAsync(store, pending, identity, (_, _) => Task.FromResult<uint?>(183), Query);
            Require(store.PendingJournal(789) is not null && store.OpenNotices(null).Count == 1, "old worker already-large reply cannot clear undersized journal");
            await JournalSizing.MaintainAsync(store, pending, identity, (_, _) => Task.FromResult<uint?>(183),
                _ => [1, 0, 100, 0, gb, SyncSharkJournalNative.Delta, 100 * gb, 789]);
            Require(store.PendingJournal(789) is null && store.OpenNotices(null).Count == 0, "verified capacity resolves pending and notice");
            clock.Now = clock.Now.AddDays(2); Observe(); pending = store.PendingJournal(789)!;
            await JournalSizing.MaintainAsync(store, pending, identity, (_, _) => throw new Exception("must not call worker"),
                _ => [1, 0, 100, 0, gb, SyncSharkJournalNative.Delta, 100 * gb, 789]);
            Require(store.PendingJournal(789) is null, "already-large query resolves without worker");
            clock.Now = clock.Now.AddDays(2); Observe(); pending = store.PendingJournal(789)!;
            Observe(2 * gb); // New larger pending target while the old request is in flight.
            store.FinishSilentJournal(pending, true, "");
            Require(store.PendingJournal(789)!.State.Requested == 4 * gb, "stale completion cannot clear changed request");
            pending = store.PendingJournal(789)!;
            store.SavePair(Pair("p1") with { Enabled = false });
            store.ArchiveAndRemovePair("p1", folder + "-pair-archive", true);
            Observe(2 * gb, "p2");
            Require(store.PendingJournal(789)!.State.NoticePair == "p2", "removed presentation pair falls back to current pair");
            store.FinishSilentJournal(pending, false, "stale failure");
            Require(store.OpenNotices(null).Count == 0, "stale pair completion cannot publish notice");
            store.FinishSilentJournal(store.PendingJournal(789)!, false, "pending failure");
            Require(store.OpenNotices(null).Count == 1, "current pair can publish its failure");
            Observe(4 * gb, "p2", false);
            Require(store.PendingJournal(789) is null && store.OpenNotices(null).Count == 0, "observation clears satisfied request");
        }
        using (var store = SqliteStateStore.Open(folder + "-capacity-floor", StorageChecks.Fast, clock))
        {
            store.SavePair(Pair("p1"));
            long size = gb, allocation = SyncSharkJournalNative.Delta;
            void ObserveCapacity(bool overflow = false) => store.ObserveJournal("p1", "C:", 987, 1, 100,
                size, 1000 * gb, overflow, clock.GetUtcNow(), allocation: allocation);
            long[] Query(string _) => [1, 0, 100, 0, size, allocation, 1000 * gb, 987];
            ObserveCapacity();
            Require(store.PendingJournal(987) is null && store.OpenNotices(null).Count == 0, "correct size has no action");
            allocation = 32 * 1024 * 1024;
            ObserveCapacity(); ObserveCapacity();
            Require(store.OpenNotices(null).Count == 1 && store.OpenNotices(null).Single().Detail.Contains("fsutil usn createjournal"),
                "undersized allocation has one actionable exact-command notice across runs");
            int calls = 0;
            await JournalSizing.MaintainAsync(store, store.PendingJournal(987)!, new SyncDiscoveryRoot(@"C:", 987, 456),
                (_, target) => { calls++; size = target; allocation = SyncSharkJournalNative.Delta; return Task.FromResult<uint?>(0); }, Query);
            Require(calls == 1 && store.OpenNotices(null).Count == 0, "fake helper repairs allocation and closes notice");
            ObserveCapacity(true);
            Require(store.PendingJournal(987)!.State.Requested == 2 * gb, "overflow after growth immediately requests next step");
            size = 2 * gb; ObserveCapacity();
            Require(store.OpenNotices(null).Count == 0 && store.PendingJournal(987) is null, "external growth closes notice on next observation");
            ObserveCapacity(true);
            Require(store.PendingJournal(987)!.State.Requested == 4 * gb, "later overflow grows another bounded step");
            allocation = gb; ObserveCapacity();
            Require(store.OpenNotices(null).Single().Detail.Contains("a=" + gb),
                "repair command preserves larger observed allocation rather than shrinking it");
        }
        Console.WriteLine("Adaptive journal sizing checks passed (fake OS only).");
    }
    private static PairDef Pair(string id) => new(id, id,
        new RootDef("ra", "Local", null, "C:\\a", "id:local:1"),
        new RootDef("rb", "Dropbox", "acc", "/b", "id:dbx:1"), [], [], true);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class PendingElevation : IElevatedAction
    {
        public int Calls;
        public readonly TaskCompletionSource<int> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> RunAsync(string verb, IReadOnlyList<string> args, CancellationToken ct)
        { Calls++; return Done.Task; }
    }
}
