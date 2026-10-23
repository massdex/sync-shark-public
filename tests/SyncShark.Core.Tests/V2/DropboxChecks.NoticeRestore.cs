namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    public static async Task NoticeRestoreAsync()
    {
        foreach (var mode in new[] { "swap", "wrong-revision", "bad-download" })
        {
            var directory = LocalChecks.FreshRoot("dropbox-notice-" + mode);
            using var h = new Harness();
            using var store = SqliteStateStore.Open(Path.Combine(directory, "state"), V2Settings.Default, TimeProvider.System);
            var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
            var writer = h.Writer(Path.Combine(directory, "spool"));
            var pair = new PairDef("p1", "restore", new RootDef("ra", SyncProviders.Local, null, "C:\\a", "id:ra"),
                Root(), [], [], true);
            store.SavePair(pair);
            h.Wire.SeedFile("file.txt", "retained cloud bytes");
            var loser = (await writer.ObserveAsync("file.txt", CancellationToken.None))!;
            h.Wire.RetainCurrentRevision("file.txt");
            var recovery = await writer.PreserveAsync(loser, "original", CancellationToken.None);
            // Real conditional Dropbox upload changes the current revision but keeps historical bytes in the fake.
            var localWinner = local.Seed("file.txt", "newer local bytes", mtime: DateTimeOffset.Parse("2026-09-27T00:00:00Z"));
            var original = new SyncAction("original", 4, ActionKind.Update, Side.B, "file.txt", null,
                localWinner, loser, true, "key");
            using var input = await local.OpenReadAsync(localWinner, CancellationToken.None);
            var winner = await writer.PutAsync(original, input, CancellationToken.None);
            // Simulate an older saved notice whose source timestamp differed from Dropbox's metadata.
            winner = winner with { Mtime = (await writer.ObserveAsync("file.txt", CancellationToken.None))!.Mtime.AddDays(-1) };
            Require((await writer.ObserveAsync("file.txt", CancellationToken.None))!.Mtime != winner.Mtime,
                "Dropbox revision identity remains authoritative when the stored source timestamp differs");
            store.AddNotices([new Notice(pair.PairId, 4, NoticeKind.AutoConflict, "file.txt", "newest", "auto-conflict",
                RetainedVersion: new RetainedNoticeVersion(original.ActionId, Side.B, loser, winner))]);
            var noticeId = store.OpenNotices(pair.PairId).Single().NoticeId!;
            if (mode == "wrong-revision") recovery = recovery with { VersionId = "not-the-loser-revision" };
            store.CommitResult(pair.PairId, store.BeginGeneration(pair.B.RootId, "run"), original,
                new ActionResult(original.ActionId, Outcome.Done, localWinner, winner, recovery, null));
            if (mode == "bad-download") h.Wire.DownloadMetadataRevisionOverride = "wrong-download-revision";
            var writes = h.Wire.AttemptedWritePaths.Count;
            var provider = new FakeEndpointProvider().Add(pair.A, local).AddWriter(pair.B, writer);
            var run = new SyncRun(store, new Executor(store, V2Settings.Default, ExecutorChecks.Admission()),
                provider, V2Settings.Default, TimeProvider.System);
            var result = await run.RestoreNoticeVersionAsync(pair.PairId, noticeId, false, CancellationToken.None);
            if (mode != "swap")
            {
                Require((mode != "wrong-revision" || h.Wire.LastDownloadRevision is null) &&
                    result.Outcome == Outcome.Skipped && h.Wire.AttemptedWritePaths.Count == writes &&
                    store.OpenNotices(pair.PairId).Any(n => n.NoticeId == noticeId),
                    "wrong retained/download revision cannot overwrite the Dropbox winner or close its notice");
                continue;
            }
            Require(result.Outcome == Outcome.Done && h.Wire.LastDownloadRevision == loser.VersionId &&
                h.Wire.LastUploadMode == "update" && h.Wire.LastUploadExpectedRevision == winner.VersionId &&
                (await writer.ObserveAsync("file.txt", CancellationToken.None))?.Hash == loser.Hash,
                "Dropbox loser is downloaded by its historical revision and conditionally restores over the exact winner: " +
                $"{result.Outcome} {result.Reason}; download={h.Wire.LastDownloadRevision}; expected={h.Wire.LastUploadExpectedRevision}; winner={winner.VersionId}");
            Require(!store.OpenNotices(pair.PairId).Any(n => n.NoticeId == noticeId) &&
                store.OpenNotices(pair.PairId).Single().RetainedVersion?.Loser.VersionId == winner.VersionId,
                "Dropbox restore closes the original notice and retains the displaced winner revision");
        }
    }
}
