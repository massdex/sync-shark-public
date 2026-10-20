namespace Massdex.SyncShark.V2;

internal static class NewPointerDownloadChecks
{
    internal static async Task RunAsync()
    {
        var remote = new ItemState("folder/file.bin", ItemKind.File, "remote-id", "revision-1",
            new string('a', 64), 8_000_000_000, DateTimeOffset.UnixEpoch.AddDays(1), ItemFlags.None);
        var action = new SyncAction("new-pointer", 19, ActionKind.CopyNew, Side.A, remote.Path,
            null, remote, null, false, null);
        var source = new MetadataOnlySource(remote);
        var creations = 0;
        var enabled = true;
        var creator = new NewPointerDownload(_ => enabled, (confirmed, path, ct) =>
        {
            creations++;
            Require(confirmed == remote && path == remote.Path, "exact metadata reaches native creation");
            return Task.FromResult(confirmed with { ObjectId = "local-id", VersionId = null, Flags = ItemFlags.OwnedPointer });
        });
        var result = await creator.TryCreateAsync(action, source, CancellationToken.None);
        Require(result?.Flags == ItemFlags.OwnedPointer && creations == 1 && source.Observations == 1 &&
            source.ByteCalls == 0, "new online-only remote file is created without downloading bytes");
        enabled = false;
        Require(await creator.TryCreateAsync(action, source, CancellationToken.None) is null && creations == 1,
            "local policy uses ordinary download path");
        enabled = true;
        Require(await creator.TryCreateAsync(action with { Kind = ActionKind.Update, ExpectedTarget = remote },
            source, CancellationToken.None) is null && creations == 1,
            "existing hydrated file is never eligible for direct pointer creation");
        foreach (var changed in new[] { remote with { VersionId = "revision-2" }, remote with { Hash = new string('b', 64) },
            remote with { Size = 1 }, remote with { ObjectId = "other" }, remote with { Mtime = remote.Mtime.AddSeconds(1) },
            remote with { VersionId = null }, remote with { Hash = null } })
        {
            source.Current = changed;
            await FailsAsync(() => creator.TryCreateAsync(action, source, CancellationToken.None));
        }
        Require(creations == 1 && source.ByteCalls == 0, "metadata mismatch never publishes or downloads");
        source.Current = remote;
        var failed = new NewPointerDownload(_ => true, (_, _, _) => throw new IOException("provider unavailable: retry"));
        await FailsAsync(() => failed.TryCreateAsync(action, source, CancellationToken.None));
        Require(source.ByteCalls == 0, "native failure propagates to the ordinary action notice, not a false success");
    }

    private static async Task FailsAsync(Func<Task<ItemState?>> run)
    {
        try { await run(); }
        catch (Exception error) when (error is IOException or ExpectedTargetMismatchException) { return; }
        throw new InvalidOperationException("Expected pointer creation to fail.");
    }
    private static void Require(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }

    private sealed class MetadataOnlySource(ItemState current) : IEndpointWriter, ISyncDirectDownloadSource
    {
        internal ItemState Current = current;
        internal int Observations;
        internal int ByteCalls;
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct)
        { Observations++; return Task.FromResult<ItemState?>(Current); }
        public Task<SyncDownloadResult> DownloadToAsync(ItemState item, Stream stream, CancellationToken ct)
        { ByteCalls++; throw new InvalidOperationException("Must not download a pointer."); }
        public Task<Stream> OpenReadAsync(ItemState item, CancellationToken ct)
        { ByteCalls++; throw new InvalidOperationException("Must not open pointer content."); }
        public Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(SyncAction a, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<RecoveryRef> PreserveAsync(ItemState item, string id, CancellationToken ct) => throw new NotSupportedException();
    }
}
