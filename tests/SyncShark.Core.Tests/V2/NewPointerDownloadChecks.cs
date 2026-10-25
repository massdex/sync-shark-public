namespace Massdex.SyncShark.V2;

internal static class NewPointerDownloadChecks
{
    internal static async Task RunAsync()
    {
        var remote = new ItemState("folder/file.bin", ItemKind.File, "remote-id", "revision-1",
            new string('a', 64), 8_000_000_000, DateTimeOffset.UnixEpoch.AddDays(1), ItemFlags.None);
        NewPointerDownload.RequireExactRemote(remote, remote with { Path = "Folder/File.bin" });
        var action = new SyncAction("new-pointer", 19, ActionKind.CopyNew, Side.A, remote.Path,
            null, remote, null, false, null);
        var source = new MetadataOnlySource(remote);
        var creations = 0;
        var enabled = true;
        var creator = new NewPointerDownload(_ => enabled, null, _ => null!,
            (confirmed, path, provider, ct) =>
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

        // N1: a pair-level cause is an ordinary download, reported once, with no metadata or native call.
        var pairNotices = 0;
        var blockedCreations = 0;
        var blocked = new NewPointerDownload(_ => true, new StoragePairException("binding", "registration overlaps this pair"),
            _ => { blockedCreations++; return null!; },
            (_, _, _, _) => { blockedCreations++; throw new InvalidOperationException("a blocked pair must not create a pointer"); },
            _ => pairNotices++);
        var observations = source.Observations;
        Require(await blocked.TryCreateAsync(action, source, CancellationToken.None) is null &&
            pairNotices == 1 && blockedCreations == 0 && source.Observations == observations && source.ByteCalls == 0,
            "blocked pair falls back to ordinary bytes with one pair notice and no native effect");
        Require(await blocked.TryCreateAsync(action, source, CancellationToken.None) is null &&
            pairNotices == 1 && blockedCreations == 0,
            "the pair notice is written once per endpoint/run, not once per file");

        // A provider that disappears after the Open decision is also a pair cause, before any pointer exists.
        var acquireNotices = 0;
        var providerLost = new StoragePairException("provider", "the Cloud Files host is unavailable");
        var raced = new NewPointerDownload(_ => true, null,
            _ => throw providerLost,
            (_, _, _, _) => throw new InvalidOperationException("no pointer after a provider cause"),
            _ => acquireNotices++);
        observations = source.Observations;
        Require(await raced.TryCreateAsync(action, source, CancellationToken.None) is null &&
            acquireNotices == 1 && source.Observations == observations + 1 && source.ByteCalls == 0,
            "provider loss before creation falls back to ordinary bytes with one pair notice");
        Require(await raced.TryCreateAsync(action, source, CancellationToken.None) is null && acquireNotices == 1,
            "a repeated provider cause never duplicates the pair notice");

        // A native creation failure may already have created a pointer: it stays an actionable item failure.
        var nativeNotices = 0;
        var unsafeNative = new NewPointerDownload(_ => true, null, _ => null!,
            (_, _, _, _) => throw new IOException("placeholder creation failed after a native effect"),
            _ => nativeNotices++);
        await FailsAsync(() => unsafeNative.TryCreateAsync(action, source, CancellationToken.None));
        Require(source.ByteCalls == 0 && nativeNotices == 0,
            "an unsafe native effect is actionable and never blindly falls back to bytes");
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
