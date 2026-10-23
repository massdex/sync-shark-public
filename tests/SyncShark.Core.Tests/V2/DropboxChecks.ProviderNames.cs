using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    public static async Task ProviderNamesAsync(string root)
    {
        Directory.CreateDirectory(root);
        _root = root;
        foreach (var reasonShape in new[] { false, true })
            await ProviderNamesRunAsync(Path.Combine(root, reasonShape ? "reason" : "path"), reasonShape);
        await QuotaHoldAsync(Path.Combine(root, "quota"), explicitRetry: false);
        await QuotaHoldAsync(Path.Combine(root, "quota-explicit"), explicitRetry: true);
        await QuotaStagingGateAsync();
        ProviderNamesStaleSourceBatch(Path.Combine(root, "stale-source"));
        await ProviderNamesWritesAsync();
        await RunChecks.ProviderNamesTransientAsync(Path.Combine(root, "parallel"));
        await ProviderNamesLocalAsync(Path.Combine(root, "ntfs"));
        Console.WriteLine("PASS provider refused names: persistence, no retry, first sync, notices, safety, transient retry, rename, write unions");
    }

    private static async Task ProviderNamesRunAsync(string root, bool reasonShape)
    {
        using var h = new Harness();
        h.Wire.UploadRefusalUsesReason = reasonShape;
        var settings = StorageChecks.Fast with { MaxWorkers = 1, ProgressInterval = TimeSpan.Zero };
        var localRoot = new RootDef("ra", SyncProviders.Local, null, "C:\\synthetic", "id:local");
        var pair = new PairDef("p1", "names", localRoot, Root(), [], [], true);
        var a = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        var writerA = new CountingWriter(a);
        var originalFolder = a.Seed("folder", "", ItemKind.Folder);
        var originals = Enumerable.Range(0, 25).Select(i => a.Seed($"folder/refused-{i}.txt", "keep " + i)).ToArray();
        foreach (var item in originals) h.Wire.NameWriteErrors[item.Path] = "disallowed_name";
        var ordinary = a.Seed("ordinary.txt", "retry me");
        // Another real per-entry rejection must not be mistaken for a forbidden name.
        h.Wire.NameWriteErrors["ordinary.txt"] = "insufficient_space";
        using (var store = SqliteStateStore.Open(root, settings, TimeProvider.System))
        {
            store.SavePair(pair);
            var run = MakeRun(store);
            var plan = await run.PreviewFirstSyncAsync("p1", new RecordingProgress(), CancellationToken.None);
            var report = await run.ApplyAsync("p1", plan, new RecordingProgress(), CancellationToken.None);
            Require(store.Live("ra").Count(item => item.Flags.HasFlag(ItemFlags.ProviderNameRefused)) == 25,
                "only exact disallowed_name results persist as item flags");
            Require(report.Failed == 1 && report.Skipped == 25 && report.Uploaded == 0,
                "25 name refusals are skipped; a typed quota rejection remains a failed retry, never a successful transfer");
            Require(store.FirstSyncDone("p1") && store.SyncRetryAt("p1", "ordinary.txt") is not null,
                "decision 85: an ordinary retryable rejection is retained without blocking first sync");
            Require(store.Baselines("p1").All(b => !b.A.Path.StartsWith("folder/refused-")),
                "refused names never fabricate a synced baseline");
            var notices = store.OpenNotices("p1").Where(n => n.ReasonKey == ProviderNameRefusedException.NoticeKey).ToArray();
            Require(notices.Length == 25 && notices.All(n => n.Repair is not null &&
                NoticeChoices.Options(n).Any(option => option.Choice == NoticeChoices.Rename)),
                "each refused entity gets one identity-bound actionable notice");
            Require(writerA.Calls == 0 && h.Wire.WholeFolderDeletes == 0 &&
                !h.Wire.Calls.Any(call => call.Contains("delete") || call.Contains("move")),
                "name handling never writes, moves or deletes the local source or deletes Dropbox content");
            foreach (var original in originals)
                Require(await a.ObserveAsync(original.Path, CancellationToken.None) == original,
                    "refusal leaves source identity, path, timestamps and hash unchanged");
        }
        // Reopen the real SQLite store; a local rescan and a content change must preserve the mark.
        originals[0] = a.Seed(originals[0].Path, "new content, same name");
        h.Wire.NameWriteErrors.Remove("ordinary.txt");
        using (var store = SqliteStateStore.Open(root, settings, TimeProvider.System))
        {
            // Real Dropbox replays our own successful writes after the pre-apply cursor.
            // This wire scripts feeds explicitly: apply 1 created only the folder, not any file.
            var observed = store.Cursor(pair.B.RootId)
                ?? throw new InvalidOperationException("Expected promoted first-sync cursor.");
            h.Wire.SeedChangePage(observed, [h.Wire.ChangeOf("folder")], false, observed + "-own-folder");
            var run = MakeRun(store);
            h.Wire.QuotaFull = true;
            var beforeQuotaCheck = h.Wire.Calls.Count(call => call == "/2/users/get_space_usage");
            var paused = await run.IncrementalAsync("p1", new RecordingProgress(), CancellationToken.None);
            Require(paused.Uploaded == 0 && h.Wire.AttemptedWritePaths.Count(path => path == "ordinary.txt") == 1,
                "persisted quota hold sends no upload bytes on later runs");
            Require(h.Wire.Calls.Count(call => call == "/2/users/get_space_usage") == beforeQuotaCheck + 1,
                "quota is rechecked only once in a run");
            var quotaNotice = store.OpenNotices("p1").Single(n => n.ReasonKey == DropboxQuota.NoticeKey);
            Require(!store.OpenNotices("p1").Any(n => n.Path == "ordinary.txt" && n.ReasonKey == "sync-retry"),
                "one pair-level quota notice replaces per-file retry notices");
            await run.ApplyRetryNoticeChoiceAsync("p1", quotaNotice.NoticeId!, NoticeChoices.Ignore, default);
            Require(store.Pair("p1")!.Excluded.Count == 0 &&
                store.NoticeById("p1", quotaNotice.NoticeId!)?.Notice.UploadSuspended == true,
                "Ignore dismisses only the quota message; it neither excludes nor releases uploads");
            h.Wire.QuotaFull = false;
            var report = await run.IncrementalAsync("p1", new RecordingProgress(), CancellationToken.None);
            Require(store.NoticeById("p1", quotaNotice.NoticeId!) is { Closed: true, Notice.UploadSuspended: false },
                "real quota probe releases an ignored hold without manually resetting retry timestamps");
            Require(originals.All(item => h.Wire.AttemptedWritePaths.Count(path => path == item.Path) == 1),
                "reopened ordinary sync and content changes do not retry refused names");
            Require(report.Failed == 0 && report.Skipped == 0 && report.Uploaded == 1 && store.FirstSyncDone("p1"),
                "ordinary file retries successfully and refused items do not block FirstSyncComplete");
            Require(h.Wire.AttemptedWritePaths.Count(path => path == "ordinary.txt") == 2 &&
                originals.All(item => h.Wire.AttemptedWritePaths.Count(path => path == item.Path) == 1),
                "another run retries only the ordinary failure");
            Require(store.OpenNotices("p1").Count(n => n.ReasonKey == ProviderNameRefusedException.NoticeKey) == 25,
                "one notice per refused entity remains visible without duplicates");
            Require(report.Deleted == 0 && writerA.Calls == 0 &&
                !store.OpenNotices("p1").Any(n => n.ReasonKey == "folder-delete-carried-ignored"),
                "resumed inventory never deletes or writes locally or proposes carrying refused children away");
            Require(await a.ObserveAsync("folder", CancellationToken.None) == originalFolder,
                "refused children's parent folder survives resumed first sync");
            foreach (var original in originals)
                Require(await a.ObserveAsync(original.Path, CancellationToken.None) == original,
                    "every refused child survives resumed first sync unchanged");
            // Apply 2 uploaded ordinary.txt. The next committed cursor must replay that own write too.
            var committed = store.Cursor(pair.B.RootId)
                ?? throw new InvalidOperationException("Expected promoted first-sync cursor.");
            h.Wire.SeedChangePage(committed, [h.Wire.ChangeOf("ordinary.txt")], false, committed + "-own-upload");
            var renamed = originals[1];
            await a.MoveAsync(new SyncAction("rename", 9, ActionKind.Move, Side.A, "folder/renamed.txt", renamed.Path,
                null, renamed, false, null), CancellationToken.None);
            var incremental = await run.IncrementalAsync("p1", new RecordingProgress(), CancellationToken.None);
            Require(incremental.Deleted == 0 && writerA.Calls == 0 &&
                await a.ObserveAsync("ordinary.txt", CancellationToken.None) == ordinary &&
                !store.OpenNotices("p1").Any(n => n.ReasonKey == "folder-delete-carried-ignored"),
                "own uploaded file survives the next incremental run with no local deletion");
            Require(h.Wire.AttemptedWritePaths.Contains("folder/renamed.txt") &&
                !store.Live("ra").Single(item => item.Path == "folder/renamed.txt").Flags.HasFlag(ItemFlags.ProviderNameRefused),
                "renamed item naturally loses its old name's refusal and uploads");
        }

        SyncRun MakeRun(IStateStore store)
        {
            var provider = new FakeEndpointProvider().Add(pair.A, a).AddWriter(pair.A, writerA)
                .AddInventory(pair.B, new DropboxInventory(Root(), Side.B, h.Client, store, "p1"))
                .AddWriter(pair.B, h.Writer());
            return new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission()), provider,
                settings, TimeProvider.System);
        }
    }

    private static void ProviderNamesStaleSourceBatch(string root)
    {
        using var store = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        var localRoot = new RootDef("ra", SyncProviders.Local, null, "C:\\synthetic", "id:local");
        store.SavePair(new PairDef("p1", "names", localRoot, Root(), [], [], true));
        var a = new FakeEndpoint(Side.A, "ra");
        var missing = a.Seed("never-indexed/missing.txt", "keep");
        var changed = a.Seed("changed.txt", "original");
        var replacement = changed with { ObjectId = "replacement-id" };
        var ordinary = a.Seed("ordinary.txt", "copy");
        var refused = a.Seed("refused.txt", "keep");
        var gen = store.BeginGeneration("ra", "snapshot");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "c", [replacement, ordinary, refused], []));
        var actions = new[] { missing, changed, ordinary, refused }.Select((item, i) =>
            new SyncAction("stale-" + i, 9, ActionKind.CopyNew, Side.B, item.Path, null,
                item, null, false, "stale-key-" + i)).ToArray();
        store.RecordIntents("p1", actions);
        var copied = ordinary with { ObjectId = "copied-id" };
        store.CommitResults("p1", gen, actions.Select((action, i) => (action,
            i == 2 ? new ActionResult(action.ActionId, Outcome.Done, null, copied, null, null) :
                new ActionResult(action.ActionId, Outcome.Skipped, null, null, null,
                    ProviderNameRefusedException.Detail) { ProviderNameRefused = true })).ToArray());
        Require(store.UnresolvedIntents("p1").Count == 0,
            "missing and changed refused sources retire their intents without rolling back the group");
        Require(store.Live("ra").Single(item => item.Path == changed.Path) == replacement &&
            !store.Live("ra").Any(item => item.Path == missing.Path),
            "stale refusals neither mark replacement identities nor recreate absent items");
        Require(store.Live("ra").Single(item => item.Path == refused.Path).Flags.HasFlag(ItemFlags.ProviderNameRefused),
            "an unchanged refused source still receives its item flag");
        var baselines = store.Baselines("p1");
        Require(baselines.Count == 1 && baselines[0].A.ObjectId == ordinary.ObjectId &&
            baselines[0].B?.ObjectId == copied.ObjectId,
            "another result in the batch commits its baseline and refusals create none");
        var warnings = store.OpenNotices("p1").Where(n => n.ReasonKey == "provider-name-refused-source-changed").ToArray();
        Require(warnings.Length == 2 && warnings.Any(n => n.Path == missing.Path) &&
            warnings.Any(n => n.Path == changed.Path), "each stale refusal raises a source-change warning");
    }

    private static async Task ProviderNamesWritesAsync()
    {
        using var h = new Harness();
        h.Wire.NameWriteErrors["refused"] = "disallowed_name";
        await ExpectNameRefusedAsync(() => h.Writer().MkDirAsync("refused", CancellationToken.None));
        h.Wire.SeedFile("old", "keep");
        var old = (await h.Writer().ObserveAsync("old", CancellationToken.None))!;
        await ExpectNameRefusedAsync(() => h.Writer().MoveAsync(new SyncAction("move", 9, ActionKind.Move,
            Side.B, "refused", "old", old with { Path = "refused" }, old, false, null), CancellationToken.None));
        Require(await h.Writer().ObserveAsync("old", CancellationToken.None) == old,
            "a refused move retains the original Dropbox item");
        var movesBefore = h.Wire.Calls.Count(call => call.Contains("move", StringComparison.Ordinal));
        try
        {
            await h.Writer().MoveAsync(new SyncAction("repair", 31, ActionKind.Move, Side.B, "safe", "old",
                null, old with { VersionId = "stale-revision" }, false, null) { RequireMoveRevision = true }, default);
            throw new InvalidOperationException("Expected stale revision rejection");
        }
        catch (ExpectedTargetMismatchException) { }
        Require(h.Wire.Calls.Count(call => call.Contains("move", StringComparison.Ordinal)) == movesBefore,
            "explicit repair checks revision before sending a provider move");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("keep"));
        try
        {
            await h.Client.UploadAsync("refused", stream, DropboxContentHash.Compute(stream.ToArray()), null);
            throw new InvalidOperationException("Expected single upload rejection");
        }
        catch (DropboxApiException error)
        {
            Require(error.WriteErrorTag == "disallowed_name", "single-request error.reason tag is preserved");
        }
        foreach (var json in new[]
        {
            "{\".tag\":\"path\",\"reason\":{\".tag\":\"disallowed_name\"}}",
            "{\".tag\":\"path\",\"path\":{\".tag\":\"disallowed_name\"}}",
            "{\".tag\":\"to\",\"to\":{\".tag\":\"disallowed_name\"}}"
        })
        {
            using var document = JsonDocument.Parse(json);
            Require(DropboxPilotClient.WriteErrorTag(document.RootElement) == "disallowed_name",
                "structured destination write union is recognized");
        }
        foreach (var wrapper in new[] { "path_lookup", "path_write" })
        foreach (var tag in new[] { "not_found", "conflict", "restricted_content", "malformed_path" })
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [".tag"] = wrapper, [wrapper] = new Dictionary<string, object> { [".tag"] = tag }
            }));
            var mapped = DropboxItems.MapWriteError(new DropboxApiException(System.Net.HttpStatusCode.Conflict, "lookup refused")
                { WriteErrorTag = DropboxPilotClient.WriteErrorTag(document.RootElement) });
            Require(tag is "not_found" or "conflict" ? mapped is ExpectedTargetMismatchException : mapped is ProviderWriteBlockedException,
                "only conditional lookup/write conflicts replan; permanent errors remain noticed failures");
        }
        foreach (var json in new[] { "\"path\"", "\"to\"", "null", "[]", "{\".tag\":\"path\",\"reason\":[]}" })
        {
            using var document = JsonDocument.Parse(json);
            Require(DropboxPilotClient.WriteErrorTag(document.RootElement) is null,
                "malformed write unions cannot authorize a refusal or throw during error handling");
        }
        foreach (var tag in new[] { "malformed_path", "insufficient_space", "conflict", "other" })
        {
            var mapped = DropboxItems.MapWriteError(new DropboxApiException(System.Net.HttpStatusCode.Conflict,
                "disallowed_name appears in untrusted text") { WriteErrorTag = tag });
            Require(mapped is not ProviderNameRefusedException, "normal errors and message substrings never become ignored");
        }
    }

    private static async Task ExpectNameRefusedAsync(Func<Task<ItemState>> action)
    {
        try { await action(); }
        catch (ProviderNameRefusedException) { return; }
        throw new InvalidOperationException("Expected structured provider name refusal");
    }
}
