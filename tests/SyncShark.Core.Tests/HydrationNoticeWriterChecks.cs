using System.Runtime.InteropServices;
using Massdex.SyncShark;
using Massdex.SyncShark.V2;

/// <summary>
/// Focused F5/N7/N8/N11 checks: hydration notice persistence is deferred off the Windows I/O path, coalesces and bounds
/// its queues, deduplicates successes, closes a durable notice left by a previous process, keys open notices by the
/// callback's local identity so a rename still closes, reports a pair-level cause once rather than per file, and
/// updates one notice in place per item+operation. Also checks the pure registration-overlap walk, which must never
/// follow a reparse point or guess.
/// </summary>
internal static class HydrationNoticeWriterChecks
{
    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh disposable database directory.");
        Directory.CreateDirectory(root);
        var local = new RootDef("local", SyncProviders.Local, null, root, "root-object");
        using var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System);
        store.EnableStorage();
        store.SavePair(new PairDef("pair", "Pair A", local,
            new RootDef("remote", SyncProviders.Dropbox, "fixture-account", "/Remote", "remote-root"), [], [], true));
        var pair = store.Pair("pair")!;
        var metadata = new StorageNotice(local.RootId, "object-1", pair.B.RootId, "hydration");
        // Regression: "receipt" and "cause" are vocabulary labels, not ShortId.Make's 1-4 letter prefixes.
        // The old Report path threw ArgumentException for "cause" before persistence, then swallowed it.
        var remote = new PilotRemoteEntry("Folder/file.txt", PilotEntryKind.File, "id:file", "rev-1", 4, new string('a', 64));
        Require(StorageNotices.ReceiptScope(remote) == "receipt" + ShortId.Hex16(remote.Id!, remote.Revision!, remote.ContentHash!),
            "receipt scope accepts its long vocabulary label without calling the short-prefix API");

        // The callback path only queues: no placeholder open and no SQLite write before the worker runs. Draining on
        // the calling thread is deterministic; the product worker is the same Process loop, so no timed polling.
        using (var writer = new HydrationNoticeWriter(store, startWorker: false))
        {
            writer.Register(pair);
            writer.Prime("pair"); // no durable notices
            var resolves = 0;
            writer.Report(pair, "Folder/file.txt", new IOException("synthetic hydration failure"), _ =>
            {
                resolves++;
                return metadata;
            });
            Require(writer.PendingEvents == 1, "a failure is queued");
            Require(!store.OpenNotices("pair").Any(notice => notice.Storage is not null),
                "reporting a failure performs no store work on the calling thread");
            writer.Drain();
            Require(writer.PersistedFailures == 1 && writer.FirstPersistenceError is null,
                "the cause label no longer throws a swallowed short-prefix error; the failure is persisted once");
            Require(resolves == 1, "the placeholder is resolved once, off the I/O path");
            Require(writer.Succeeded("pair", "Folder/file.txt", _ => metadata), "a persisted failure tracks its success");
            writer.Drain();
            Require(writer.ClosedNotices == 1, "the success closes the open notice");
            Require(!store.OpenNotices("pair").Any(notice => notice.Storage is not null),
                "failure then success leaves no open notice");
            var writes = writer.PersistedFailures + writer.ClosedNotices;
            Require(!writer.Succeeded("pair", "Folder/never-failed.txt",
                    _ => throw new InvalidOperationException("an untracked success must not resolve a placeholder")),
                "a success without an open notice performs no work after the first pair success");
            Require(writer.PendingEvents == 0, "an untracked success queues nothing");
            writer.Drain();
            Require(writer.PersistedFailures + writer.ClosedNotices == writes, "an untracked success writes nothing");
        }

        // Duplicate failures coalesce per item; the queue is bounded by distinct items, not by the item count.
        using (var writer = new HydrationNoticeWriter(store, startWorker: false))
        {
            writer.Register(pair);
            writer.Report(pair, "Folder/duplicate.txt", new IOException("first"), _ => metadata);
            writer.Report(pair, "Folder/duplicate.txt", new IOException("second"), _ => metadata);
            Require(writer.PendingEvents == 1, "a repeated failure for one item coalesces before persistence");
        }
        using (var writer = new HydrationNoticeWriter(store, startWorker: false))
        {
            for (var index = 0; index < HydrationNoticeWriter.MaximumQueuedEvents + 50; index++)
                writer.Report(pair, "Folder/flood-" + index + ".txt", new IOException("flood"),
                    _ => metadata with { ObjectId = "flood" });
            Require(writer.PendingEvents == HydrationNoticeWriter.MaximumQueuedEvents,
                "the pending queue is bounded by distinct items");
            Require(writer.DroppedEvents == 50, "events beyond the bound are counted, not queued");
        }

        // The real background worker is covered too: a queued barrier completes only after the Task.Run drain has
        // applied every earlier event, so a stopped or faulted worker fails the check instead of hanging it.
        using (var worker = new HydrationNoticeWriter(store))
        {
            worker.Register(pair);
            worker.Prime("pair");
            worker.Report(pair, "Folder/worker.txt", new IOException("worker failure"), _ => metadata);
            await worker.DrainAsync();
            Require(worker.PersistedFailures == 1 && worker.PersistenceErrors == 0,
                "the background worker persists a queued failure before the barrier without a swallowed store error");
            Require(worker.Succeeded("pair", "Folder/worker.txt", _ => metadata), "the worker still tracks the success");
            await worker.DrainAsync();
            Require(worker.ClosedNotices == 1 && worker.PersistenceErrors == 0 &&
                !store.OpenNotices("pair").Any(notice => notice.Storage is not null),
                "the background worker closes the notice before the barrier");
        }

        // The original regression path: an event is queued while no worker exists, then Start() launches the real
        // Task.Run drain. The barrier makes this deterministic and reports a worker that never ran or a swallowed
        // store failure (PersistenceErrors) instead of a wall-clock poll.
        using (var queued = new HydrationNoticeWriter(store, startWorker: false))
        {
            queued.Register(pair);
            queued.Prime("pair");
            queued.Report(pair, "Folder/queued-before-start.txt", new IOException("queued before start"), _ => metadata);
            Require(queued.PendingEvents == 1, "the failure is queued before the worker exists");
            queued.Start();
            await queued.DrainAsync();
            Require(queued.PersistedFailures == 1 && queued.PersistenceErrors == 0,
                "Start after a queued event drains it through the real worker without a swallowed store error");
            Require(queued.Succeeded("pair", "Folder/queued-before-start.txt", _ => metadata),
                "the started worker tracks the queued item's success");
            await queued.DrainAsync();
            Require(queued.ClosedNotices == 1 && queued.PersistenceErrors == 0 &&
                !store.OpenNotices("pair").Any(notice => notice.Storage is not null),
                "the started worker closes the queued item's notice");
        }

        // A notice left open by a previous process closes on the next success, without Retry.
        using (var first = new HydrationNoticeWriter(store, startWorker: false))
        {
            first.Register(pair);
            first.Report(pair, "Folder/restart.txt", new IOException("left open"), _ => metadata);
            first.Drain();
            Require(first.PersistedFailures == 1, "the prior-process fixture failure is persisted");
        }
        Require(store.OpenNotices("pair").Any(notice => notice.Storage is not null),
            "the notice survives the first writer's disposal");
        using (var restarted = new HydrationNoticeWriter(store, startWorker: false))
        {
            restarted.Register(pair);
            Require(restarted.Succeeded("pair", "Folder/restart.txt",
                    _ => throw new InvalidOperationException("the primed notice already names the item")),
                "the first success primes durable notices and closes the one it finds");
            restarted.Drain();
            Require(restarted.ClosedNotices == 1, "the primed success closes its durable notice");
            Require(!store.OpenNotices("pair").Any(notice => notice.Storage is not null),
                "a durable notice from a previous process closes on success");
        }

        // Distinct failures are persisted in bounded worker batches; coalesced successes close each family once.
        using (var writer = new HydrationNoticeWriter(store, startWorker: false))
        {
            writer.Register(pair);
            writer.Prime("pair");
            for (var index = 0; index < 40; index++)
            {
                var path = "Folder/item-" + index + ".txt";
                var item = metadata with { ObjectId = "object-" + index };
                writer.Report(pair, path, new IOException("synthetic failure " + index), _ => item);
            }
            writer.Drain();
            Require(writer.PersistedFailures == 40, "every distinct failure is persisted exactly once");
            Require(store.OpenNotices("pair").Count(notice => notice.Storage is not null) == 40,
                "every distinct failure is visible");
            Require(writer.OpenCacheCount == 40, "the bounded open cache tracks every persisted failure");
            for (var index = 0; index < 40; index++) writer.Succeeded("pair", "Folder/item-" + index + ".txt", null);
            writer.Drain();
            Require(writer.ClosedNotices == 40, "coalesced successes close every notice they saw open");
            Require(!store.OpenNotices("pair").Any(notice => notice.Storage is not null),
                "no item notice remains open after the successes");
        }

        RegistrationOverlap();
        CallbackPath();
        await StorageNoticeLaneChecks.RunAsync(Path.Combine(root, "notice-lane")).ConfigureAwait(false);
        Console.WriteLine("PASS hydration notice writer defers, coalesces, bounds and orders persistence; untracked success " +
            "writes nothing; a durable prior-process notice closes on success; pair causes report once; one notice per " +
            "item+operation updates in place; a renamed callback identity closes the old notice; registered descendant " +
            "overlap is detected");
    }

    // Pure overlap classification over an enumerated registration path list: exact self/ancestor/descendant only,
    // no filesystem walk for a large pair.
    private static void RegistrationOverlap()
    {
        var basis = Path.Combine(Path.GetTempPath(), "overlap");
        var root = Path.Combine(basis, "root");
        var ancestor = Path.Combine(basis, "ancestor");
        var deep = Path.Combine(root, "a", "deep");
        Require(WindowsCloudFilesProvider.RegisteredPathOverlap(root, [root]) is { Relation: "self" },
            "an exact registration is self");
        Require(WindowsCloudFilesProvider.RegisteredPathOverlap(deep, [root]) is { Relation: "ancestor" },
            "a registration above the root is an ancestor overlap");
        Require(WindowsCloudFilesProvider.RegisteredPathOverlap(root, [deep]) is { Relation: "descendant" },
            "a registration below the root is a descendant overlap");
        Require(WindowsCloudFilesProvider.RegisteredPathOverlap(root, [ancestor]) is null,
            "a sibling registration is not an overlap");
        var trailing = root + Path.DirectorySeparatorChar;
        Require(WindowsCloudFilesProvider.RegisteredPathOverlap(trailing, [root]) is { Relation: "self" },
            "a trailing separator does not change the exact match");
    }

    // The callback path is copied synchronously and validated; only the report key changes, never the embedded
    // revision identity. CF_CALLBACK_INFO.NormalizedPath is volume-relative, so it is composed with VolumeDosName.
    private static void CallbackPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "callback-root");
        var original = "Folder/original.bin";
        Require(Callback(null, null, original, root) == original,
            "missing native paths fall back to the embedded path");
        Require(Callback("", null, original, root) == original,
            "an empty callback path falls back to the embedded path");
        Require(Callback("Folder/renamed pointer.bin", null, original, root) == "Folder/renamed pointer.bin",
            "a volume-less root-relative path is accepted defensively");
        Require(Callback("\\Folder\\renamed.bin", null, original, root) == original,
            "a leading-separator path without its volume is not reinterpreted");
        Require(Callback("..\\escape.bin", null, original, root) == original,
            "a traversal callback path is rejected");
        Require(Callback(@"C:\Windows\win.ini", null, original, root) == original,
            "an absolute callback path outside the root is rejected");
        if (OperatingSystem.IsWindows())
        {
            var drive = Path.GetPathRoot(root)!;
            var volume = drive.TrimEnd('\\');
            var relativeRoot = root[drive.Length..];
            Require(Callback("\\" + relativeRoot + "\\Folder\\renamed.bin", volume, original, root) ==
                    "Folder/renamed.bin",
                "a volume-relative callback path is composed with VolumeDosName and resolves inside the root");
            Require(Callback("\\Other\\outside.bin", volume, original, root) == original,
                "a volume-relative path outside the root falls back");
            Require(Callback("\\" + relativeRoot + "\\..\\escape.bin", volume, original, root) == original,
                "a traversal volume-relative path falls back");
        }
    }

    private static string Callback(string? current, string? volume, string embedded, string root)
    {
        var currentPointer = current is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(current);
        var volumePointer = volume is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(volume);
        try
        {
            return WindowsCloudFilesProvider.CallbackRelativePath(currentPointer, volumePointer, embedded, root);
        }
        finally
        {
            if (currentPointer != IntPtr.Zero) Marshal.FreeHGlobal(currentPointer);
            if (volumePointer != IntPtr.Zero) Marshal.FreeHGlobal(volumePointer);
        }
    }

    internal static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
