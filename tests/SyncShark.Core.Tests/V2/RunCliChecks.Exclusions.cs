namespace Massdex.SyncShark.V2;

/// <summary>
/// 'folders include' and 'folders exclude' after a pair exists: the exclusion is saved, the reviewed prepare is
/// narrowed without another review (2026-10-18 follow-up), no file is touched (decision 58), and bad paths are usage errors.
/// </summary>
internal static partial class RunCliChecks
{
    public static async Task NarrowingChecksAsync()
    {
        await Cli_SqliteNarrowingPreservesReviewAsync(ignore: false);
        await Cli_SqliteNarrowingPreservesReviewAsync(ignore: true);
        await Cli_SqliteWideningClearsReviewAsync(ignore: false);
        await Cli_SqliteWideningClearsReviewAsync(ignore: true);
        await Cli_SqliteNarrowingDoesNotRevalidateStaleScopeAsync();
        await Cli_SqliteUnicodeExclusionAsync();
        await Cli_NarrowingBatchesAndCrashAsync();
        await Cli_NewIgnoreProtectsReviewedAncestorAsync();
        await Cli_NarrowingFolderIgnoreRetainsSameNamedFileAsync();
        Console.WriteLine("v2 narrowing checks passed.");
    }

    internal static async Task Cli_NarrowingBatchesAndCrashAsync()
    {
        foreach (var crash in new[] { false, true })
        {
            var root = TempRoot();
            try
            {
                using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
                var fx = Build(store: store);
                for (var i = 0; i < 600; i++) fx.B.Seed((i % 2 == 0 ? "cut/" : "keep/") + i + ".txt", "bytes");
                var runtime = Runtime(fx, root);
                const string choices = """{"oneSided":[{"prefix":"","side":"Dropbox","action":"remove"}]}""";
                await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
                var before = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)!;
                var generation = store.ReadPrepare(fx.Pair.PairId)!.Generation;
                var batches = 0;
                store.NarrowingBatchForTest = () =>
                {
                    batches++;
                    Require(store.ReadPrepare(fx.Pair.PairId)?.Digest == SqliteStateStore.NarrowingDigest && !before.Database!.ReviewedKeys.Any(),
                        "each intermediate narrowing batch is detectably stale, never half-trusted");
                    if (crash) throw new IOException("simulated narrowing interruption");
                };
                if (crash)
                {
                    var error = await RefusalAsync(() => runtime.SetSelectionAsync(fx.Pair.PairId, "cut", "exclude", CancellationToken.None));
                    Require(error is IOException && batches == 1 && store.ReadPrepare(fx.Pair.PairId)?.Digest == SqliteStateStore.NarrowingDigest,
                        "interrupted narrowing has no published authorization");
                    using var reopened = SqliteStateStore.Open(root, Settings, TimeProvider.System);
                    Require(reopened.ReadPrepare(fx.Pair.PairId)?.Digest == SqliteStateStore.NarrowingDigest &&
                        FirstSyncChoicesState.Load(root, fx.Pair.PairId, reopened) is null,
                        "cold reopen cannot trust interrupted narrowed rows");
                }
                else
                {
                    await runtime.SetSelectionAsync(fx.Pair.PairId, "cut", "exclude", CancellationToken.None);
                    var after = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)!;
                    Require(batches > 1 && store.ReadPrepare(fx.Pair.PairId)!.Generation == generation &&
                        after.DeleteVictimCount == 300 && after.Database!.ReviewedKeys.Count == 300,
                        "narrowing deletes in bounded batches without rewriting survivor generations");
                    Require(!before.Database!.ReviewedKeys.Any(), "old digest-bound lazy authorization remains invalid after publication");
                }
            }
            finally { Cleanup(root); }
        }
    }

    private static async Task RunExclusionUpdateChecksAsync()
    {
        await Cli_ExcludeUpdate_NarrowsPrepareAndTouchesNoFilesAsync();
        await Cli_SqliteNarrowingPreservesReviewAsync(ignore: false);
        await Cli_SqliteWideningClearsReviewAsync(ignore: false);
        await Cli_SqliteNarrowingDoesNotRevalidateStaleScopeAsync();
        await Cli_SqliteUnicodeExclusionAsync();
        await Cli_IncludeUpdate_RemovesSavedExclusionAsync();
        await Cli_Exclusions_RoundTripThroughVerbsAsync();
        await Cli_Exclusions_RefuseInvalidPathAsync();
    }

    // Adding an exclusion only removes work: retain the review and leave both copies untouched.
    private static async Task Cli_ExcludeUpdate_NarrowsPrepareAndTouchesNoFilesAsync()
    {
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(store: store);
            fx.A.Seed("keep-out", "", ItemKind.Folder);
            fx.A.Seed("keep-out/f.txt", "local");
            fx.B.Seed("keep-out", "", ItemKind.Folder);
            fx.B.Seed("keep-out/f.txt", "cloudy");
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None);
            Require(FirstSyncChoicesState.Load(root, fx.Pair.PairId, fx.Store) is not null,
                "a prepare is retained before the exclusion change");
            var data = await runtime.SetSelectionAsync(fx.Pair.PairId, "keep-out/", "exclude",
                CancellationToken.None);
            Require(!data.Included && data.RelativePath == "keep-out" && data.SelectionRuleCount == 1,
                "the exclusion is saved normalized without a trailing '/' and reported");
            Require(fx.Store.Pair(fx.Pair.PairId)?.Excluded.SequenceEqual(["keep-out"]) == true,
                "the pair's saved definition carries the new exclusion");
            Require(FirstSyncChoicesState.Load(root, fx.Pair.PairId, fx.Store) is not null,
                "narrowing retains the reviewed prepare rather than requiring a full scan");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
                "adding an exclusion makes no file effects");
            Require((await fx.A.ObserveAsync("keep-out/f.txt", CancellationToken.None))?.Size == 5 &&
                (await fx.B.ObserveAsync("keep-out/f.txt", CancellationToken.None))?.Size == 6,
                "both copies stay exactly as they were");
            var applied = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, null, true, null,
                _ => { }, CancellationToken.None);
            Require(applied.Failed == 0 && applied.FirstSyncComplete &&
                fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
                "the original review applies without reprepare or touching excluded content");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_ExcludeUpdate_NarrowsPrepareAndTouchesNoFiles");
    }

    // RQ-021/025/040/041/043, decisions 54/67/71: narrowing filters persisted authorization,
    // not just the display. Use the real SQLite prepare and CLI mutation, with no synthetic review rows.
    private static async Task Cli_SqliteNarrowingPreservesReviewAsync(bool ignore)
    {
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(store: store);
            foreach (var prefix in new[] { "cut", "cut-sibling" })
            {
                fx.A.Seed(prefix + "/held.txt", "local held");
                fx.A.Seed(prefix + "/matched.txt", "same");
                fx.B.Seed(prefix + "/matched.txt", "same");
                fx.B.Seed(prefix + "/remove", "", ItemKind.Folder);
                fx.B.Seed(prefix + "/remove/victim.txt", "reviewed victim");
                fx.B.Seed(prefix + "/remove/cache", "", ItemKind.Folder);
                fx.B.Seed(prefix + "/remove/cache/private.txt", "private");
            }
            // Keep prepare evidence pending, then resolve apply without synthetic retry backoffs.
            fx.Provider.AddInventory(fx.Pair.B, new MissedDeletionEvidenceInventory(fx.B)
                { ResolveDeleted = DateTimeOffset.MinValue });
            var runtime = Runtime(fx, root);
            await runtime.SaveIgnoresAsync(fx.Pair.PairId, "FolderName:cache", CancellationToken.None);
            const string choices = """{"oneSided":[{"prefix":"","side":"Dropbox","action":"remove"}]}""";
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            var before = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)!;
            var digest = before.Digest;
            var scope = before.InventoryScope;
            var cursors = before.InventoryCursors.ToArray();
            // Snapshot before publication replaces the generation behind the lazy SQL views.
            var reviewedBefore = before.Database!.ReviewedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reviewedExpected = reviewedBefore.Where(key =>
                key.StartsWith("A:cut-sibling/", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("B:cut-sibling/", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keepVictims = before.DeleteVictims.Where(key => key.StartsWith("B:cut-sibling/", StringComparison.Ordinal)).ToArray();
            var keepHeld = before.HeldDeletions.Where(item => item.Path.StartsWith("cut-sibling/", StringComparison.Ordinal)).ToArray();
            var keepMatches = before.Matched.Where(item => item.Path.StartsWith("cut-sibling/", StringComparison.Ordinal)).ToArray();
            var keepBudgets = before.CarriedIgnoredDeletes.Where(item => item.Path.StartsWith("cut-sibling/", StringComparison.Ordinal)).ToArray();
            Require(before.Database is not null && before.DeleteVictims.Count > keepVictims.Length && keepVictims.Length > 0 &&
                before.HeldDeletions.Count == 2 && keepHeld.Length == 1 && before.Matched.Count == 2 && keepMatches.Length == 1 &&
                before.CarriedIgnoredDeletes.Count == 2 && keepBudgets.Length == 1 && cursors.Length > 0 && scope is not null,
                "SQLite narrowing fixture includes victims, held rules, matched stamps, carried budgets and pending cursors");
            if (ignore)
                await DispatchAsync(runtime, ["--cli", "ignores", "set", "--pair", fx.Pair.PairId,
                    "--rules", "FolderName:cache;FolderName:CUT", "--yes"]);
            else
                await DispatchAsync(runtime, ["--cli", "folders", "exclude", "--pair", fx.Pair.PairId,
                    "--path", "CUT/", "--yes"]);
            var after = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)
                ?? throw new InvalidOperationException("Narrowing must retain the SQLite prepare.");
            Require(after.Database is not null && after.Digest != digest && store.ReadPrepare(fx.Pair.PairId)?.Digest == after.Digest,
                "narrowing durably replaces the digest without dropping the SQLite review");
            Require(after.DeleteVictims.SequenceEqual(keepVictims) && after.DeleteVictimCount == keepVictims.Length &&
                after.HeldDeletions.SequenceEqual(keepHeld) && after.Matched.SequenceEqual(keepMatches) &&
                after.CarriedIgnoredDeletes.SequenceEqual(keepBudgets),
                "only cut's victims, held paths, matches and carried budgets disappear; prefix sibling survives exactly");
            var database = after.Database!;
            var reviewedAfter = database.ReviewedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Require(reviewedAfter.IsProperSubsetOf(reviewedBefore) && reviewedAfter.SetEquals(reviewedExpected),
                "the complete narrowed authorization set is a strict subset containing exactly the preserved sibling's keys");
            Require(!database.ReviewedKeys.Contains("A:cut/held.txt") &&
                !database.ReviewedKeys.Contains("B:cut/remove/victim.txt") &&
                !database.ContainsHeld(new HeldDeletion("cut/held.txt", Side.A)) &&
                !database.MatchLookup.ContainsKey("cut/matched.txt") &&
                database.Budget("B:cut/remove") is null &&
                database.ContainsHeld(keepHeld.Single()) &&
                database.ReviewedKeys.Contains("B:cut-sibling/remove/victim.txt"),
                "SQL authorization indexes are filtered with their display collections");
            Require(after.InventoryScope == fx.Run().InventoryScope(store.Pair(fx.Pair.PairId)!) && after.InventoryScope != scope &&
                after.InventoryCursors.SequenceEqual(cursors) &&
                FirstSyncChoicesState.PendingInventoryCursors(after, scope!).Count == 0 &&
                FirstSyncChoicesState.PendingInventoryCursors(after, after.InventoryScope!).Count == cursors.Length &&
                string.IsNullOrEmpty(store.Cursor(fx.Pair.B.RootId)),
                "pending cursor is retained under the new scope only, never prematurely promoted");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "narrowing makes no endpoint effects");
            const string changed = """{"oneSided":[{"prefix":"","side":"Dropbox","action":"copy"}]}""";
            var refusal = await RefusalAsync(() => Runtime(fx, root).ApplyFirstSyncAsync(fx.Pair.PairId,
                changed, false, null, _ => { }, CancellationToken.None));
            Require(refusal is SyncCliReviewException && refusal.Message.Contains("choices differ", StringComparison.Ordinal) &&
                fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 &&
                FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)?.Digest == after.Digest,
                "changed choices are still rejected before effects and leave the narrowed review intact");
            var result = await Runtime(fx, root).ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null,
                _ => { }, CancellationToken.None);
            Console.WriteLine("NARROWED APPLY " + System.Text.Json.JsonSerializer.Serialize(new
            {
                ignore, result, callsA = fx.WriterA.Calls, callsB = fx.WriterB.Calls,
                remainingSiblingVictim = await fx.B.ObserveAsync("cut-sibling/remove/victim.txt", CancellationToken.None),
                narrowedVictim = await fx.B.ObserveAsync("cut/remove/victim.txt", CancellationToken.None),
                narrowedPrivate = await fx.B.ObserveAsync("cut/remove/cache/private.txt", CancellationToken.None),
                narrowedHeld = await fx.A.ObserveAsync("cut/held.txt", CancellationToken.None),
                narrowedMatchA = await fx.A.ObserveAsync("cut/matched.txt", CancellationToken.None),
                narrowedMatchB = await fx.B.ObserveAsync("cut/matched.txt", CancellationToken.None),
                notices = store.UnresolvedNotices(fx.Pair.PairId).ToArray()
            }));
            Require(result.Failed == 0 && await fx.B.ObserveAsync("cut-sibling/remove/victim.txt", CancellationToken.None) is null &&
                await fx.B.ObserveAsync("cut/remove/victim.txt", CancellationToken.None) is not null &&
                await fx.B.ObserveAsync("cut/remove/cache/private.txt", CancellationToken.None) is not null &&
                await fx.A.ObserveAsync("cut/held.txt", CancellationToken.None) is not null &&
                await fx.A.ObserveAsync("cut/matched.txt", CancellationToken.None) is not null &&
                await fx.B.ObserveAsync("cut/matched.txt", CancellationToken.None) is not null,
                "same choices apply remaining reviewed work, leaving all narrowed content untouched");
        }
        finally { Cleanup(root); }
        Console.WriteLine("PASS Cli_SqliteNarrowingPreservesReview_" + (ignore ? "Ignore" : "Exclusion"));
    }

    // RQ-030/040: narrowing must not launder a checkpoint made stale by another coverage change.
    private static async Task Cli_SqliteNarrowingDoesNotRevalidateStaleScopeAsync()
    {
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(store: store);
            fx.B.Seed("cut/file.txt", "untouched");
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None);
            var before = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)!;
            Require(before.InventoryCursors.Count > 0 && before.InventoryScope is not null,
                "stale-scope fixture starts with a pending checkpoint");
            store.SavePair(new PairDef("p2", "other pair",
                new RootDef("rc", SyncProviders.Local, null, "C:\\other", "id:rc"),
                new RootDef("rd", SyncProviders.Dropbox, "acc", "/other", "id:rd"), [], [], true));
            var unchangedScope = fx.Run().InventoryScope(store.Pair(fx.Pair.PairId)!);
            Require(before.InventoryScope == unchangedScope &&
                FirstSyncChoicesState.PendingInventoryCursors(before, unchangedScope).Count == before.InventoryCursors.Count,
                "an unrelated pair leaves coverage and the pending checkpoint valid");
            store.SavePair(new PairDef("p3", "nested pair",
                new RootDef("re", SyncProviders.Local, null, "C:\\a\\nested", "id:re"),
                new RootDef("rf", SyncProviders.Dropbox, "acc", "/nested-other", "id:rf"), [], [], true));
            var staleScope = fx.Run().InventoryScope(store.Pair(fx.Pair.PairId)!);
            Require(before.InventoryScope != staleScope &&
                FirstSyncChoicesState.PendingInventoryCursors(before, staleScope).Count == 0,
                "another pair changes coverage and invalidates the old checkpoint");
            await runtime.SetSelectionAsync(fx.Pair.PairId, "cut", "exclude", CancellationToken.None);
            var after = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)
                ?? throw new InvalidOperationException("Narrowing should retain review even with a stale cursor.");
            Require(after.Digest != before.Digest &&
                FirstSyncChoicesState.PendingInventoryCursors(after, fx.Run().InventoryScope(store.Pair(fx.Pair.PairId)!)).Count == 0 &&
                string.IsNullOrEmpty(store.Cursor(fx.Pair.B.RootId)) && fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
                "narrowing retains review but never revalidates or promotes the stale inventory cursor");
        }
        finally { Cleanup(root); }
        Console.WriteLine("PASS Cli_SqliteNarrowingDoesNotRevalidateStaleScope");
    }

    // Decision 50 / RQ-039: SQL filtering uses ordinal-ignore-case, not ASCII-only SQLite NOCASE.
    private static async Task Cli_SqliteUnicodeExclusionAsync()
    {
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(store: store);
            fx.B.Seed("Äpfel/Über.txt", "protected");
            fx.B.Seed("Äpfel-extra/Über.txt", "reviewed");
            const string choices = """{"oneSided":[{"prefix":"","side":"Dropbox","action":"remove"}]}""";
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            var before = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)!;
            Require(before.DeleteVictims.Count == 2, "Unicode fixture reviews both delete victims");
            await runtime.SetSelectionAsync(fx.Pair.PairId, "äpfel/", "exclude", CancellationToken.None);
            var after = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)!;
            Require(after.DeleteVictims.SequenceEqual(["B:Äpfel-extra/Über.txt"]) &&
                !after.Database!.ReviewedKeys.Contains("B:ÄPFEL/ÜBER.TXT"),
                "Unicode case exclusion drops the exact subtree and not its prefix sibling");
            var result = await Runtime(fx, root).ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null,
                _ => { }, CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete &&
                await fx.B.ObserveAsync("Äpfel/Über.txt", CancellationToken.None) is not null &&
                await fx.B.ObserveAsync("Äpfel-extra/Über.txt", CancellationToken.None) is null,
                "Unicode exclusion is honored during apply with the original choices");
        }
        finally { Cleanup(root); }
        Console.WriteLine("PASS Cli_SqliteUnicodeExclusion");
    }

    private static async Task Cli_SqliteWideningClearsReviewAsync(bool ignore)
    {
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(store: store);
            var runtime = Runtime(fx, root);
            if (ignore) await runtime.SaveIgnoresAsync(fx.Pair.PairId, "FolderName:cut", CancellationToken.None);
            else await runtime.SetSelectionAsync(fx.Pair.PairId, "cut", "exclude", CancellationToken.None);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None);
            Require(store.ReadPrepare(fx.Pair.PairId) is not null, "widening starts with a SQLite prepare");
            // Replacing an ignore with a different one adds AND removes: not a strict narrowing.
            if (ignore) await runtime.SaveIgnoresAsync(fx.Pair.PairId, "FolderName:other", CancellationToken.None);
            else await runtime.SetSelectionAsync(fx.Pair.PairId, "cut", "include", CancellationToken.None);
            Require(store.ReadPrepare(fx.Pair.PairId) is null && FirstSyncChoicesState.Load(root, fx.Pair.PairId, store) is null,
                "widening clears SQLite review rather than reauthorizing newly included paths");
            var refusal = await RefusalAsync(() => runtime.ApplyFirstSyncAsync(fx.Pair.PairId, null, true, null,
                _ => { }, CancellationToken.None));
            Require(refusal is SyncCliReviewException && fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
                "apply after widening still requires review before effects");
        }
        finally { Cleanup(root); }
        Console.WriteLine("PASS Cli_SqliteWideningClearsReview_" + (ignore ? "Ignore" : "Exclusion"));
    }

    // Removing one exclusion widens the scope and invalidates the prepare; other exclusions remain intact.
    private static async Task Cli_IncludeUpdate_RemovesSavedExclusionAsync()
    {
        var fx = Build();
        fx.Store.SavePair(fx.Pair with { Excluded = ["keep-out", "docs/old"] });
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None);
            Require(FirstSyncChoicesState.Load(root, fx.Pair.PairId, fx.Store) is not null, "a prepare is retained");
            var data = await runtime.SetSelectionAsync(fx.Pair.PairId, "KEEP-OUT", "include",
                CancellationToken.None);
            Require(data.Included && data.SelectionRuleCount == 1,
                "the removal is reported with the remaining rule count");
            Require(fx.Store.Pair(fx.Pair.PairId)?.Excluded.SequenceEqual(["docs/old"]) == true,
                "only the exact exclusion is removed, ignoring case");
            Require(FirstSyncChoicesState.Load(root, fx.Pair.PairId, fx.Store) is null,
                "removing an exclusion also invalidates the prepare");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "removing an exclusion makes no file effects");
            // Removing the path again is a no-op, so a usable prepare would survive it.
            var again = await runtime.SetSelectionAsync(fx.Pair.PairId, "keep-out", "include",
                CancellationToken.None);
            Require(again.SelectionRuleCount == 1, "including an already-included path changes nothing");
            // There are no inclusion exceptions in core v2: a parent exclusion still hides the path, so saying
            // 'included' would be a lie and is refused instead.
            var refusal = await RefusalAsync(() => runtime.SetSelectionAsync(fx.Pair.PairId, "docs/old/sub",
                "include", CancellationToken.None));
            Require(refusal is SyncCliUsageException &&
                refusal.Message.Contains("still excluded by the parent folder", StringComparison.Ordinal),
                "including below a still-excluded parent is refused with a clear usage error");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_IncludeUpdate_RemovesSavedExclusion");
    }

    // The same two verbs the operator uses reach the v2 runtime through the registry and save the same state.
    private static async Task Cli_Exclusions_RoundTripThroughVerbsAsync()
    {
        var fx = Build();
        var root = TempRoot();
        try
        {
            var runtime = Runtime(fx, root);
            var excluded = (SyncCliSelectionData)await DispatchAsync(runtime,
                ["--cli", "folders", "exclude", "--pair", fx.Pair.PairId, "--path", "keep-out/", "--yes"]);
            Require(!excluded.Included && excluded.SelectionRuleCount == 1,
                "'folders exclude' is answered by the v2 runtime");
            Require(fx.Store.Pair(fx.Pair.PairId)?.Excluded.SequenceEqual(["keep-out"]) == true,
                "the verb saves the exclusion");
            var included = (SyncCliSelectionData)await DispatchAsync(runtime,
                ["--cli", "folders", "include", "--pair", fx.Pair.PairId, "--path", "keep-out", "--yes"]);
            Require(included.Included && included.SelectionRuleCount == 0,
                "'folders include' is answered by the v2 runtime");
            Require(fx.Store.Pair(fx.Pair.PairId)?.Excluded.Count == 0, "the verb saves the removal");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "the verb round-trip makes no file effects");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Exclusions_RoundTripThroughVerbs");
    }

    // A path that is not pair-relative and normalized is refused before anything is saved.
    private static async Task Cli_Exclusions_RefuseInvalidPathAsync()
    {
        var fx = Build();
        var runtime = Runtime(fx, TempRoot());
        foreach (var bad in new[] { "/lead", "back\\slash", "a/../b", "a/./b", "a//b", "", "/" })
        {
            var refusal = await RefusalAsync(() => DispatchAsync(runtime,
                ["--cli", "folders", "exclude", "--pair", fx.Pair.PairId, "--path", bad, "--yes"]));
            Require(refusal is SyncCliUsageException && refusal.Message.Contains("--path", StringComparison.Ordinal),
                $"the path '{bad}' is refused with a clear --path usage error");
        }
        Require(fx.Store.Pair(fx.Pair.PairId)?.Excluded.Count == 0, "no refused path is saved");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "no refused path makes a file effect");
        Console.WriteLine("PASS Cli_Exclusions_RefuseInvalidPath");
    }
}
