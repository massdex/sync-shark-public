namespace Massdex.SyncShark.V2;

/// <summary>CLI first-sync binding checks (decision 54): the digest, the retained prepare and the remove limit.</summary>
internal static partial class RunCliChecks
{
    private static async Task RunFirstSyncBindingChecksAsync()
    {
        await Cli_Choices_DigestIsUnambiguousAsync();
        Cli_Choices_EquivalentShareDigest();
        Cli_Choices_HeldDeletionDigest();
        Cli_Choices_HeldDeletionReviewGuard();
        await Cli_Choices_FailedPrepareAuthorizesNothingAsync();
        await Cli_Choices_MalformedPrepareAuthorizesNothingAsync();
        await Cli_Choices_UnreadablePrepareMeansPrepareAgainAsync();
        await Cli_Choices_TransientlyLockedPrepareIsRetriedAsync();
        await Cli_Choices_PersistentlyLockedPrepareIsDiscardedAsync();
        await Cli_Choices_CorruptPrepareIsNotRetriedAsync();
        await Cli_Choices_LegacyImportRetriesTransientLockAsync();
        await Cli_Choices_UnrelatedIoFailurePropagatesAsync();
        await Cli_Choices_RemoveMayNotGrowAsync();
        await Cli_Choices_CopyMayGrowAsync();
    }

    // Two documents whose old line-joined text was identical: the tuple digest tells them apart, so an apply
    // with the second is refused after a prepare with the first.
    private static async Task Cli_Choices_DigestIsUnambiguousAsync()
    {
        var fx = Build();
        const string first = """{"paths":{"a":"upload","b":"download"}}""";
        const string second = """{"a=A\nw:b":"download"}""";
        var root = TempRoot();
        try
        {
            Require(DigestOf(fx, first) != DigestOf(fx, second), "a path holding separators cannot forge a digest");
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, first);
            Require(await RefusedApplyAsync(runtime, fx, second), "the forged document is refused as different");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Choices_DigestIsUnambiguous");
    }

    // Document order, key order, side order, side-name case and a trailing '/' do not change the digest. Path
    // spelling is kept as written, so letters that only case folding equates ('ſ' upper-cases to 'S', which
    // OrdinalIgnoreCase does not match) stay distinct choices with distinct digests.
    private static void Cli_Choices_EquivalentShareDigest()
    {
        var fx = Build();
        var one = DigestOf(fx, """
            {"paths":{"X.txt":"upload"},
             "oneSided":[{"prefix":"a/","side":"Local","action":"remove"},
                         {"prefix":"a","side":"Dropbox","action":"copy"},
                         {"prefix":"b","side":"Local","action":"copy"}]}
            """);
        var two = DigestOf(fx, """
            {"oneSided":[{"prefix":"b/","side":"local","action":"copy"},
                         {"prefix":"a","side":"dropbox","action":"copy"},
                         {"prefix":"a","side":"LOCAL","action":"remove"}],
             "paths":{"X.txt":"upload"}}
            """);
        Require(one == two, "equivalent choices share one digest");
        Require(DigestOf(fx, """{"oneSided":[{"prefix":"ſ/","side":"Local","action":"remove"}]}""") !=
            DigestOf(fx, """{"oneSided":[{"prefix":"S/","side":"Local","action":"remove"}]}"""),
            "'ſ/' and 'S/' have different digests");
        Require(FirstSyncChoices.Parse("""
            {"oneSided":[{"prefix":"ſ","side":"Local","action":"remove"},{"prefix":"S","side":"Local","action":"copy"}]}
            """, fx.Pair).OneSided.Count == 2, "'ſ' and 'S' are distinct choices in one document");
        Console.WriteLine("PASS Cli_Choices_EquivalentShareDigest");
    }

    // A refused (malformed) prepare document still supersedes the earlier prepare, so nothing stays authorized.
    private static async Task Cli_Choices_MalformedPrepareAuthorizesNothingAsync()
    {
        var fx = Build();
        fx.A.Seed("b/y.txt", "two");
        var root = TempRoot();
        const string copy = """{"oneSided":[{"prefix":"b/","side":"Local","action":"copy"}]}""";
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, copy);
            Require(await RefusedPrepareAsync(runtime, fx, "{"), "the malformed second prepare is a usage error");
            Require(await RefusedApplyAsync(runtime, fx, copy), "the malformed prepare leaves no authorization");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "the refused apply makes no writer call");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Choices_MalformedPrepareAuthorizesNothing");
    }

    // A prepare whose preview fails (a side read incompletely, or an endpoint that throws) authorizes nothing,
    // not even the earlier successful prepare it superseded.
    private static async Task Cli_Choices_FailedPrepareAuthorizesNothingAsync()
    {
        const string copy = """{"oneSided":[{"prefix":"b/","side":"Local","action":"copy"}]}""";
        static IInventorySource Failing(Fixture fx, int variant) =>
            variant == 0 ? new ScriptedInventory(fx.A) { Incomplete = true } : new ThrowingInventory();
        for (var variant = 0; variant < 2; variant++)
        {
            var fx = Build();
            fx.A.Seed("b/y.txt", "two");
            var root = TempRoot();
            try
            {
                var runtime = Runtime(fx, root);
                await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, copy);
                fx.Provider.AddInventory(fx.Pair.A, Failing(fx, variant));
                var deltas = fx.A.DeltaCalls;
                var failed = false;
                try
                {
                    await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, copy);
                }
                catch (Exception error) when (error is SyncCliReviewException or IOException or SyncRunPendingVerificationException)
                {
                    failed = true;
                }
                Require(failed, "the second prepare fails in variant " + variant);
                if (variant == 0)
                    Require(fx.A.DeltaCalls > deltas, "the incomplete second prepare resumed A from its saved checkpoint");
                Require(await RefusedApplyAsync(runtime, fx, copy), "a failed prepare leaves no authorization");
                Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "the refused apply makes no writer call");
            }
            finally
            {
                Cleanup(root);
            }
        }
        Console.WriteLine("PASS Cli_Choices_FailedPrepareAuthorizesNothing");
    }

    // A stale temp sibling does not disturb a prepare; a corrupt record means "run prepare again".
    private static async Task Cli_Choices_UnreadablePrepareMeansPrepareAgainAsync()
    {
        var fx = Build();
        fx.A.Seed("b/y.txt", "two");
        var root = TempRoot();
        const string copy = """{"oneSided":[{"prefix":"b/","side":"Local","action":"copy"}]}""";
        var record = Path.Combine(root, "v2", "first-sync-" + fx.Pair.PairId + ".choices.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(record)!);
            await File.WriteAllTextAsync(record + ".0123456789abcdef.tmp", "{half");
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, copy);
            var prepared = FirstSyncChoicesState.Load(root, fx.Pair.PairId, fx.Store)!;
            Require(prepared.InventoryCursors.Count == 2, "corrupt-record fixture retains both root checkpoints");
            // Defensive fixture: seed committed checkpoints too, so discard must clear every reuse path.
            foreach (var cursor in prepared.InventoryCursors)
                fx.Store.CompleteGeneration(fx.Store.BeginGeneration(cursor.RootId, "delta"), cursor.Cursor, true);
            await File.WriteAllTextAsync(record, "{\"digest\":");
            var message = await ApplyRefusalAsync(runtime, fx, copy);
            Require(message?.Contains("run prepare again", StringComparison.Ordinal) == true,
                "a corrupt prepare record is refused with 'run prepare again'");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "a corrupt record authorizes no writer calls");
            var fullA = fx.A.FullCalls;
            var fullB = fx.B.FullCalls;
            var deltaA = fx.A.DeltaCalls;
            var deltaB = fx.B.DeltaCalls;
            var messages = new List<string>();
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, messages.Add, CancellationToken.None, copy);
            Require(fx.A.FullCalls > fullA && fx.B.FullCalls > fullB &&
                fx.A.DeltaCalls == deltaA && fx.B.DeltaCalls == deltaB,
                "a discarded corrupt record forces a full bootstrap on both sides");
            Require(messages.Any(line => line.Contains("Discarding unreadable first-sync prepare", StringComparison.Ordinal)),
                "fresh prepare records why the previous record was discarded");
            await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, copy, false, null, _ => { }, CancellationToken.None);
            Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && !File.Exists(record), "a fresh prepare applies and its record is cleared");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Choices_UnreadablePrepareMeansPrepareAgain");
    }

    // A record held briefly by a scanner is retried; its checkpoints still drive the next prepare, so neither
    // side falls back to a full bootstrap and nothing is discarded.
    private static async Task Cli_Choices_TransientlyLockedPrepareIsRetriedAsync()
    {
        var fx = Build();
        fx.A.Seed("b/y.txt", "two");
        var root = TempRoot();
        const string copy = """{"oneSided":[{"prefix":"b/","side":"Local","action":"copy"}]}""";
        var record = Path.Combine(root, "v2", "first-sync-" + fx.Pair.PairId + ".choices.json");
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, copy);
            var fullA = fx.A.FullCalls;
            var fullB = fx.B.FullCalls;
            var deltaA = fx.A.DeltaCalls;
            var deltaB = fx.B.DeltaCalls;
            var reads = 0;
            var waits = new List<TimeSpan>();
            FirstSyncChoicesState.OpenReadForTest = file => ++reads <= 2
                ? throw new IOException(
                    "The process cannot access the file because it is being used by another process.",
                    unchecked((int)0x80070020))
                : File.OpenRead(file);
            FirstSyncChoicesState.RetryWaitForTest = waits.Add;
            var messages = new List<string>();
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, messages.Add, CancellationToken.None, copy);
            Require(reads == 3, "a transient lock is retried until the record opens");
            Require(waits.SequenceEqual([TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500)]),
                "the retries back off 200 ms then 500 ms");
            Require(messages.All(line => !line.Contains("Discarding unreadable", StringComparison.Ordinal)),
                "a transient lock never discards the record");
            Require(fx.A.FullCalls == fullA && fx.B.FullCalls == fullB &&
                fx.A.DeltaCalls > deltaA && fx.B.DeltaCalls > deltaB,
                "the retried record keeps its checkpoints, so neither side bootstraps in full");
            Require(File.Exists(record), "the retried record is retained");
        }
        finally
        {
            FirstSyncChoicesState.OpenReadForTest = null;
            FirstSyncChoicesState.RetryWaitForTest = null;
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Choices_TransientlyLockedPrepareIsRetried");
    }

    // A record that never opens after the retries is discarded with its reason, and both sides bootstrap in full.
    private static async Task Cli_Choices_PersistentlyLockedPrepareIsDiscardedAsync()
    {
        var fx = Build();
        fx.A.Seed("b/y.txt", "two");
        var root = TempRoot();
        const string copy = """{"oneSided":[{"prefix":"b/","side":"Local","action":"copy"}]}""";
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, copy);
            var fullA = fx.A.FullCalls;
            var fullB = fx.B.FullCalls;
            var deltaA = fx.A.DeltaCalls;
            var deltaB = fx.B.DeltaCalls;
            var reads = 0;
            var waits = new List<TimeSpan>();
            FirstSyncChoicesState.OpenReadForTest = _ =>
            {
                reads++;
                throw new UnauthorizedAccessException("Access to the path is denied.");
            };
            FirstSyncChoicesState.RetryWaitForTest = waits.Add;
            var messages = new List<string>();
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, messages.Add, CancellationToken.None, copy);
            Require(reads == 3, "a persistent lock is tried once and retried twice");
            Require(waits.SequenceEqual([TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500)]),
                "the persistent read backs off 200 ms then 500 ms");
            Require(messages.Any(line => line.Contains("Discarding unreadable first-sync prepare", StringComparison.Ordinal) &&
                line.Contains("UnauthorizedAccessException", StringComparison.Ordinal)),
                "the discard names the persistent read failure");
            Require(fx.A.FullCalls > fullA && fx.B.FullCalls > fullB &&
                fx.A.DeltaCalls == deltaA && fx.B.DeltaCalls == deltaB,
                "an exhausted retry bootstraps both sides in full");
        }
        finally
        {
            FirstSyncChoicesState.OpenReadForTest = null;
            FirstSyncChoicesState.RetryWaitForTest = null;
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Choices_PersistentlyLockedPrepareIsDiscarded");
    }

    // Corruption is not transient: it is discarded on the first read, with no retry wait.
    private static async Task Cli_Choices_CorruptPrepareIsNotRetriedAsync()
    {
        var fx = Build();
        fx.A.Seed("b/y.txt", "two");
        var root = TempRoot();
        const string copy = """{"oneSided":[{"prefix":"b/","side":"Local","action":"copy"}]}""";
        var record = Path.Combine(root, "v2", "first-sync-" + fx.Pair.PairId + ".choices.json");
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, copy);
            await File.WriteAllTextAsync(record, "{\"digest\":");
            var reads = 0;
            var waits = new List<TimeSpan>();
            FirstSyncChoicesState.OpenReadForTest = file =>
            {
                reads++;
                return File.OpenRead(file);
            };
            FirstSyncChoicesState.RetryWaitForTest = waits.Add;
            var messages = new List<string>();
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, messages.Add, CancellationToken.None, copy);
            Require(reads == 1, "a corrupt record is read once");
            Require(waits.Count == 0, "a corrupt record is never retried");
            Require(messages.Any(line => line.Contains("Discarding unreadable first-sync prepare", StringComparison.Ordinal) &&
                line.Contains("unparsable prepare record", StringComparison.Ordinal)),
                "the discard names the parse failure");
        }
        finally
        {
            FirstSyncChoicesState.OpenReadForTest = null;
            FirstSyncChoicesState.RetryWaitForTest = null;
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Choices_CorruptPrepareIsNotRetried");
    }

    // The SQLite-backed legacy import reads through the same retrying open: a record briefly held by a scanner is
    // still imported and retained in the database instead of being lost. The fixture uses its own temporary
    // database, never the live store.
    private static async Task Cli_Choices_LegacyImportRetriesTransientLockAsync()
    {
        var fx = Build(pairId: "p-import");
        fx.A.Seed("b/y.txt", "two");
        var root = TempRoot();
        const string copy = """{"oneSided":[{"prefix":"b/","side":"Local","action":"copy"}]}""";
        var record = Path.Combine(root, "v2", "first-sync-" + fx.Pair.PairId + ".choices.json");
        try
        {
            // The non-SQLite path writes the legacy per-pair JSON record that the import must read.
            await Runtime(fx, root).PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, copy);
            Require(File.Exists(record), "a legacy prepare record exists to import");
            using var store = SqliteStateStore.Open(Path.Combine(root, "state"), StorageChecks.Fast,
                TimeProvider.System);
            store.SavePair(fx.Pair);
            var reads = 0;
            var waits = new List<TimeSpan>();
            FirstSyncChoicesState.OpenReadForTest = file => ++reads <= 2
                ? throw new IOException(
                    "The process cannot access the file because it is being used by another process.",
                    unchecked((int)0x80070020))
                : File.OpenRead(file);
            FirstSyncChoicesState.RetryWaitForTest = waits.Add;
            var imported = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store);
            Require(imported is not null && imported.Database is not null,
                "a transiently locked legacy record is still imported into SQLite");
            Require(reads == 3 && waits.SequenceEqual([TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500)]),
                "the legacy import retries 200 ms then 500 ms");
            Require(store.ReadPrepare(fx.Pair.PairId) is not null,
                "the imported prepare is retained in the database");
        }
        finally
        {
            FirstSyncChoicesState.OpenReadForTest = null;
            FirstSyncChoicesState.RetryWaitForTest = null;
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Choices_LegacyImportRetriesTransientLock");
    }

    // An I/O failure that is not a sharing/lock shape is a real fault, not a transiently unreadable record: it
    // propagates from both prepare and rescan and leaves the reviewed record in place.
    private static async Task Cli_Choices_UnrelatedIoFailurePropagatesAsync()
    {
        var fx = Build();
        fx.A.Seed("b/y.txt", "two");
        var root = TempRoot();
        const string copy = """{"oneSided":[{"prefix":"b/","side":"Local","action":"copy"}]}""";
        var record = Path.Combine(root, "v2", "first-sync-" + fx.Pair.PairId + ".choices.json");
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, copy);
            Require(File.Exists(record), "prepare retains a record to fail over");
            var reads = 0;
            FirstSyncChoicesState.OpenReadForTest = _ =>
            {
                reads++;
                // Win32 21 (device not ready) is neither a sharing violation (32/33) nor a denied access.
                throw new IOException("The device is not ready.", unchecked((int)0x80070015));
            };
            FirstSyncChoicesState.RetryWaitForTest = _ => { };
            var prepareFailure = await RefusalAsync(() => runtime.PrepareFirstSyncAsync(fx.Pair.PairId,
                _ => { }, CancellationToken.None, copy));
            Require(prepareFailure is IOException && reads == 1,
                "prepare propagates a non-sharing I/O failure without retrying");
            Require(File.Exists(record), "a propagated prepare failure leaves the record in place");
            reads = 0;
            var rescanFailure = await RefusalAsync(() => DispatchAsync(runtime,
                ["--cli", "pairs", "rescan", "--pair", fx.Pair.PairId, "--yes"]));
            Require(rescanFailure is IOException && reads == 1,
                "rescan propagates a non-sharing I/O failure without retrying");
            Require(File.Exists(record), "a propagated rescan failure leaves the record in place");
        }
        finally
        {
            FirstSyncChoicesState.OpenReadForTest = null;
            FirstSyncChoicesState.RetryWaitForTest = null;
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Choices_UnrelatedIoFailurePropagates");
    }

    // Decision 54 addendum: a remove that would now delete more files, or more bytes, than prepare reported is
    // refused before any writer call.
    private static async Task Cli_Choices_RemoveMayNotGrowAsync()
    {
        const string remove = """{"oneSided":[{"prefix":"a","side":"Local","action":"remove"}]}""";
        foreach (var newFile in new[] { true, false })
        {
            var fx = Build();
            fx.A.Seed("a/x.txt", "hi");
            fx.A.Seed("outside.txt", "independent reviewed work");
            var writer = new RecordingWriter(fx.A);
            fx.Provider.AddWriter(fx.Pair.A, writer);
            var root = TempRoot();
            try
            {
                var runtime = Runtime(fx, root);
                await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, remove);
                if (newFile) fx.A.Seed("a/new.txt", "n");
                else fx.A.Seed("a/x.txt", "much longer");
                var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, remove, false, null, _ => { }, default);
                Require(result.FirstSyncComplete && result.Failed == 0, "growth does not refuse independent reviewed work");
                var effects = writer.Calls.Where(call => !call.StartsWith("Observe:", StringComparison.Ordinal) &&
                    !call.StartsWith("OpenRead:", StringComparison.Ordinal)).ToArray();
                Require(effects.SequenceEqual(newFile ? new[] { "Preserve:a/x.txt", "Delete:a/x.txt" } : Array.Empty<string>()),
                    "only the unchanged individually reviewed file may be deleted; an enlarged victim gets zero writes");
                Require(fx.WriterB.Puts == 1 && fx.WriterB.Calls == 1 && await fx.B.ObserveAsync("outside.txt", default) is not null,
                    "independent copy proceeds with no unreviewed remote writes");
                Require(await fx.A.ObserveAsync(newFile ? "a/new.txt" : "a/x.txt", default) is not null &&
                    (newFile ? fx.Store.SyncRetryAt(fx.Pair.PairId, "a/new.txt") is not null :
                        fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "a/x.txt" && notice.ReasonKey == "reviewed-folder-grew")),
                    "new or enlarged victim survives with durable retry input or an attention notice");
            }
            finally
            {
                Cleanup(root);
            }
        }
        Console.WriteLine("PASS Cli_Choices_RemoveMayNotGrow");
    }

    // Copies are not limited: a file that appeared under a copy choice after prepare is copied too.
    private static async Task Cli_Choices_CopyMayGrowAsync()
    {
        var fx = Build();
        fx.A.Seed("b/y.txt", "two");
        var root = TempRoot();
        const string copy = """{"oneSided":[{"prefix":"b","side":"Local","action":"copy"}]}""";
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, copy);
            fx.A.Seed("b/z.txt", "three");
            await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, copy, false, null, _ => { }, CancellationToken.None);
            Require(await fx.B.ObserveAsync("b/z.txt", CancellationToken.None) is not null,
                "a copy that grew since prepare still applies");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Choices_CopyMayGrow");
    }

    private static void Cli_Choices_HeldDeletionDigest()
    {
        var choices = FirstSyncChoices.Parse("{}", Build().Pair);
        HeldDeletion[] held = [new("folder", Side.A), new("x.txt", Side.B)];
        var digest = FirstSyncChoices.Digest(choices, [], held);
        Require(digest == FirstSyncChoices.Digest(choices, [], held.Reverse().ToArray()),
            "held list order does not affect digest");
        Require(digest != FirstSyncChoices.Digest(choices, [], held.Take(1).ToArray()),
            "removing a held path changes digest");
        Require(digest != FirstSyncChoices.Digest(choices, [], [held[0] with { Local = Side.B }, held[1]]),
            "changing held local side changes digest");
        Require(digest != FirstSyncChoices.Digest(choices, [], [held[0] with { Path = "other" }, held[1]]),
            "changing held path changes digest");
        Require(digest != FirstSyncChoices.Digest(choices, [], [held[0] with { Rule = "other" }, held[1]]),
            "changing held rule changes digest");
        Require(FirstSyncChoices.Digest(choices, [], [new("a", Side.A, "b\nc")]) !=
            FirstSyncChoices.Digest(choices, [], [new("a\nb", Side.A, "c")]),
            "held paths and rules cannot forge tuple boundaries");
        Console.WriteLine("PASS Cli_Choices_HeldDeletionDigest");
    }

    private static void Cli_Choices_HeldDeletionReviewGuard()
    {
        var root = TempRoot();
        var choices = FirstSyncChoices.Parse("{}", Build().Pair);
        HeldDeletion[] held = [new("folder", Side.A), new("x.txt", Side.B)];
        try
        {
            FirstSyncChoicesState.Save(root, "held", choices, [], ["A:old.txt"], [], held);
            var prepared = FirstSyncChoicesState.Load(root, "held")!;
            Require(prepared is not null && prepared.HeldDeletions.SequenceEqual(held),
                "prepare round-trips the exact original held list and rules");
            Require(prepared!.Digest == FirstSyncChoices.Digest(choices, [], prepared.HeldDeletions),
                "persisted digest binds the original held list");
            Plan Current(params HeldDeletion[] items) => new("held", 1, [], []) { HeldDeletions = items };
            Require(FirstSyncChoicesState.PartitionHeldDeletions(prepared, Current()).UnreviewedHeldDeletions.Count == 0,
                "fully resolved holds introduce no pending paths");
            Require(FirstSyncChoicesState.PartitionHeldDeletions(prepared, Current(held[1])).HeldDeletions.Count == 1,
                "a reviewed subset remains executable");
            Require(FirstSyncChoicesState.PartitionHeldDeletions(prepared,
                Current(held[0] with { Path = "FOLDER" })).HeldDeletions.Count == 1,
                "held path authorization matches path casing consistently");
            foreach (var extra in new[] { new HeldDeletion("new.txt", Side.A), held[0] with { Local = Side.B },
                         held[0] with { Rule = "other" }, new HeldDeletion("folder/child.txt", Side.A) })
            {
                var partitioned = FirstSyncChoicesState.PartitionHeldDeletions(prepared, Current(held[1], extra));
                Require(partitioned.HeldDeletions.SequenceEqual([held[1]]) &&
                    partitioned.UnreviewedHeldDeletions.SequenceEqual([extra]),
                    "new held path, side, rule or descendant stays pending while reviewed work proceeds");
            }
            FirstSyncChoicesState.RefuseUnreviewedDeletes(prepared, ["A:old.txt", "A:FOLDER", "B:x.txt"]);
            var reviewed = FirstSyncChoicesState.ReviewedDeleteKeys(prepared);
            Require(reviewed.SetEquals(["A:old.txt", "A:folder", "B:x.txt"]),
                "execution review includes exact held local victims and existing reviewed deletes");
            foreach (var victim in new[] { "B:folder", "A:folder/child.txt", "A:new.txt" })
            {
                var refused = false;
                try { FirstSyncChoicesState.RefuseUnreviewedDeletes(prepared, [victim]); }
                catch (SyncCliReviewException) { refused = true; }
                Require(refused, "held approval does not authorize opposite side, descendants or new paths");
            }
            var retained = FirstSyncChoicesState.Load(root, "held")!;
            Require(retained.HeldDeletions.SequenceEqual(held) && retained.Digest == prepared.Digest,
                "shrinking current holds never mutates the retained list or digest");
        }
        finally { Cleanup(root); }
        Console.WriteLine("PASS Cli_Choices_HeldDeletionReviewGuard");
    }

    private static string DigestOf(Fixture fx, string json) =>
        FirstSyncChoices.Digest(FirstSyncChoices.Parse(json, fx.Pair), []);

    private static async Task<string?> ApplyRefusalAsync(V2CliRuntime runtime, Fixture fx, string choices)
    {
        try
        {
            await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { }, CancellationToken.None);
        }
        catch (SyncCliReviewException error)
        {
            return error.Message;
        }
        return null;
    }

    /// <summary>An inventory whose endpoint cannot be reached: every read throws.</summary>
    private sealed class ThrowingInventory : IInventorySource
    {
        public Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p,
            CancellationToken ct) => throw Offline();

        public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include,
            CancellationToken ct) => throw Offline();

        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths,
            CancellationToken ct) => throw Offline();

        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
            CancellationToken ct) => throw Offline();

        private static IOException Offline() => new("The endpoint cannot be reached.");
    }
}
