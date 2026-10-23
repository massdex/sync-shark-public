namespace Massdex.SyncShark.V2;

/// <summary>
/// Focused checks for <see cref="IRecoveryRecordReader"/>: immutable reads, ownership refusals, stale versions,
/// create-only restoration and collision refusal. All effects use isolated local fixtures or a fake cloud wire.
/// </summary>
internal static class RecoveryReaderChecks
{
    public static async Task RunAsync()
    {
        await DropboxRecoveryRecordReadAsync().ConfigureAwait(false);
        await LocalRecoveryRecordReadAsync().ConfigureAwait(false);
        await ReadOnlyLocalObservationAsync().ConfigureAwait(false);
        await RecoveryAdapterChecks.RunAsync().ConfigureAwait(false);
    }

    private static async Task ReadOnlyLocalObservationAsync()
    {
        var root = LocalChecks.FreshRoot("recovery-readonly-" + Guid.NewGuid().ToString("N"));
        LocalChecks.Seed(root, "current.txt", "current bytes");
        using var lease = PilotDirectoryLease.Open(root);
        var binding = new SyncLocalEndpointBinding(root, lease.Identity.VolumeSerialNumber, lease.Identity.FileId);
        var before = Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal).ToArray();
        var current = await SyncLocalEndpointStore.ObserveReadOnlyAsync(binding, "current.txt", SyncProviders.Local,
            CancellationToken.None);
        var absent = await SyncLocalEndpointStore.ObserveReadOnlyAsync(binding, "absent.txt", SyncProviders.Local,
            CancellationToken.None);
        Require(current is { ContentHash: not null } && absent is null &&
            Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal).SequenceEqual(before),
            "read-only Recovery observes present/absent content without a control namespace or any created file");
    }

    private static async Task DropboxRecoveryRecordReadAsync()
    {
        var directory = LocalChecks.FreshRoot("recovery-reader-dropbox-" + Guid.NewGuid().ToString("N"));
        using var wire = new DropboxWire();
        using var http = new HttpClient(wire);
        var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", Binding(), DropboxWire.RootId);
        var writer = new DropboxWriter(new RootDef("rb", SyncProviders.Dropbox, "acc", "/Pilot", DropboxWire.RootId),
            Side.B, client, Path.Combine(directory, "spool"), V2Settings.Default, pairId: LocalChecks.PairId);
        var modified = new DateTimeOffset(2026, 10, 12, 7, 0, 0, TimeSpan.Zero);
        wire.SeedFile("file.txt", "retained cloud bytes", modified);
        var victim = (await writer.ObserveAsync("file.txt", CancellationToken.None))!;
        wire.RetainCurrentRevision("file.txt");
        var reference = await writer.PreserveAsync(victim, "act-cloud", CancellationToken.None);
        wire.EditFile("file.txt", "current winner bytes");
        // The fake wire exposes retained history through this one bounded metadata response.
        wire.RevisionResponseOverride = new
        {
            is_deleted = false,
            entries = new object[]
            {
                new Dictionary<string, object>
                {
                    [".tag"] = "file",
                    ["id"] = victim.ObjectId,
                    ["rev"] = reference.VersionId!,
                    ["size"] = reference.Size,
                    ["content_hash"] = reference.Hash,
                    ["server_modified"] = "2026-10-12T07:00:00Z",
                    ["path_display"] = "/Pilot/file.txt"
                }
            }
        };
        var record = Record("cloud", "act-cloud", Side.B, victim.Path, reference.Location, reference.VersionId,
            reference.Hash, reference.Size);
        var reader = (IRecoveryRecordReader)writer;
        var described = await reader.DescribeRecoveryAsync(record, CancellationToken.None);
        Require(described == new ItemState(victim.Path, ItemKind.File, victim.ObjectId, reference.VersionId,
                reference.Hash, reference.Size, modified, ItemFlags.None),
            "Dropbox describes the exact retained revision at the recorded path");
        Require((await RecoveryRecordPreviews.DescribeDropboxAsync(client, LocalChecks.PairId, Side.B, record,
                CancellationToken.None)) == described,
            "Dropbox preview helper describes without a writable writer");
        int RevisionCalls()
        {
            lock (wire.Calls) return wire.Calls.Count(path => path == "/2/files/list_revisions");
        }
        var describedCalls = RevisionCalls();
        await using (var bytes = await reader.OpenRecoveryRecordAsync(record, described, CancellationToken.None))
        {
            using var text = new StreamReader(bytes);
            Require(await text.ReadToEndAsync() == "retained cloud bytes" &&
                    RevisionCalls() == describedCalls,
                "opening from already-described metadata reuses that revision with no second list_revisions request");
        }
        await Expect<InvalidDataException>(() =>
            reader.OpenRecoveryRecordAsync(record, described with { VersionId = "rev-not-recorded" },
                CancellationToken.None));
        await Expect<InvalidDataException>(() =>
            reader.OpenRecoveryRecordAsync(record, described with { Hash = "not-the-recorded-hash" },
                CancellationToken.None));
        Require(RevisionCalls() == describedCalls,
            "a supplied revision or hash that differs from the durable row is rejected before any provider request");
        var plainCalls = RevisionCalls();
        await using (var bytes = await reader.OpenRecoveryRecordAsync(record, CancellationToken.None))
        {
            using var text = new StreamReader(bytes);
            Require(await text.ReadToEndAsync() == "retained cloud bytes",
                "Dropbox opens the immutable historical revision bytes");
        }
        Require(RevisionCalls() == plainCalls + 1,
            "a plain open makes exactly one historical metadata describe");
        Require(wire.AttemptedWritePaths.Count == 0, "Dropbox Recovery reads never send a provider write");
        var copyPath = "file (restored 2026-10-20).txt";
        var action = new SyncAction("restore-cloud", 0, ActionKind.CopyNew, Side.B, copyPath, null,
            described, null, false, null);
        await using (var bytes = await reader.OpenRecoveryRecordAsync(record, CancellationToken.None))
        {
            var restored = await writer.PutAsync(action, bytes, CancellationToken.None);
            Require(restored.Hash == record.Hash && restored.Size == record.Size && !string.IsNullOrEmpty(restored.VersionId),
                "Dropbox restores retained bytes through verified add-only upload");
        }
        wire.EditFile(copyPath, "new current bytes");
        var changed = await writer.ObserveAsync(copyPath, CancellationToken.None);
        var refused = false;
        await using (var bytes = await reader.OpenRecoveryRecordAsync(record, CancellationToken.None))
        {
            try { await writer.PutAsync(action, bytes, CancellationToken.None); }
            catch (Exception error) when (error is DropboxApiException or ExpectedTargetMismatchException or IOException)
            { refused = true; }
        }
        Require(refused && (await writer.ObserveAsync(copyPath, CancellationToken.None))?.Hash == changed?.Hash,
            "Dropbox add-only restore cannot overwrite a newly occupied destination");
        await Expect<FileNotFoundException>(() =>
            reader.DescribeRecoveryAsync(record with { VersionId = "rev-that-is-gone" }, CancellationToken.None));
        await Expect<InvalidDataException>(() =>
            reader.DescribeRecoveryAsync(record with { Side = Side.A }, CancellationToken.None));
        await Expect<InvalidDataException>(() =>
            reader.DescribeRecoveryAsync(record with { PairId = "otherpair01" }, CancellationToken.None));
        await Expect<InvalidDataException>(() =>
            reader.DescribeRecoveryAsync(record with { Location = "another.txt" }, CancellationToken.None));
    }

    private static async Task LocalRecoveryRecordReadAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("recovery-reader-local-" + Guid.NewGuid().ToString("N")).ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "victim.txt", "archived local bytes");
        var victim = (await setup.Writer.ObserveAsync("victim.txt", CancellationToken.None))!;
        var reference = await setup.Writer.PreserveAsync(victim, "act-local", CancellationToken.None);
        Require(!File.Exists(Path.Combine(setup.Root, "victim.txt")) && File.Exists(reference.Location),
            "the local fixture archived the displaced bytes");
        var record = Record("local", "act-local", Side.A, victim.Path, reference.Location, null, reference.Hash,
            reference.Size);
        var reader = (IRecoveryRecordReader)setup.Writer;
        var described = await reader.DescribeRecoveryAsync(record, CancellationToken.None);
        Require(described.Kind == ItemKind.File && described.Path == victim.Path &&
            described.Hash == reference.Hash && described.Size == reference.Size && described.VersionId is null &&
            described.ObjectId.StartsWith("id:local:", StringComparison.Ordinal),
            "local describes the exact retained archive with its own NTFS identity");
        Require(RecoveryRecordPreviews.DescribeLocal(LocalChecks.PairId, Side.A, setup.Recovery, record,
                CancellationToken.None) == described,
            "local preview helper describes without opening a writable writer");
        await using (var bytes = await reader.OpenRecoveryRecordAsync(record, described, CancellationToken.None))
        {
            using var text = new StreamReader(bytes);
            Require(await text.ReadToEndAsync() == "archived local bytes",
                "local opens from already-described metadata after the same ownership and ACL checks");
        }
        await Expect<InvalidDataException>(() =>
            reader.OpenRecoveryRecordAsync(record, described with { Hash = "not-the-recorded-hash" },
                CancellationToken.None));
        Require(File.Exists(reference.Location),
            "a mismatched supplied local hash reads nothing and keeps the archive");
        await using (var bytes = await reader.OpenRecoveryRecordAsync(record, CancellationToken.None))
        {
            using var text = new StreamReader(bytes);
            Require(await text.ReadToEndAsync() == "archived local bytes",
                "local opens the verified archive bytes");
        }
        Require(File.Exists(reference.Location), "opening a local Recovery copy never removes it");
        var restore = new SyncAction("restore-local", 0, ActionKind.CopyNew, Side.A, victim.Path, null,
            described, null, false, null);
        await using (var bytes = await reader.OpenRecoveryRecordAsync(record, CancellationToken.None))
        {
            var restored = await setup.Writer.PutAsync(restore, bytes, CancellationToken.None);
            Require(restored.Hash == record.Hash && restored.Size == record.Size && File.Exists(reference.Location),
                "local create-only restore verifies content and retains the archive");
        }
        LocalChecks.Seed(setup.Root, victim.Path, "new current bytes");
        await using (var bytes = await reader.OpenRecoveryRecordAsync(record, CancellationToken.None))
            await Expect<ExpectedTargetMismatchException>(() => setup.Writer.PutAsync(restore, bytes, CancellationToken.None));
        Require(await File.ReadAllTextAsync(Path.Combine(setup.Root, victim.Path)) == "new current bytes",
            "local restore cannot overwrite a current different item");
        // TOCTOU: a user creates the destination after the restore observed it absent and before the create-only
        // publish. The real local endpoint must leave that file byte-for-byte untouched and write nothing.
        Require(await setup.Writer.ObserveAsync("race.txt", CancellationToken.None) is null,
            "the restore destination is observed absent before the race window");
        var raceCreated = false;
        var raceWriter = new RaceLocalWriter(setup.Writer, () =>
        {
            if (raceCreated) return;
            raceCreated = true;
            LocalChecks.Seed(setup.Root, "race.txt", "user-created bytes");
        });
        var raceAction = new SyncAction("restore-race", 0, ActionKind.CopyNew, Side.A, "race.txt", null,
            described, null, false, null);
        await using (var bytes = await reader.OpenRecoveryRecordAsync(record, CancellationToken.None))
            await Expect<ExpectedTargetMismatchException>(() =>
                raceWriter.PutAsync(raceAction, bytes, CancellationToken.None));
        Require(raceCreated && await File.ReadAllTextAsync(Path.Combine(setup.Root, "race.txt")) ==
                "user-created bytes",
            "a file created between observe and put is never overwritten by a create-only restore");
        await Expect<InvalidDataException>(() =>
            reader.DescribeRecoveryAsync(record with { PairId = "otherpair01" }, CancellationToken.None));
        await Expect<InvalidDataException>(() =>
            reader.DescribeRecoveryAsync(record with { Side = Side.B }, CancellationToken.None));
        await Expect<InvalidDataException>(() =>
            reader.DescribeRecoveryAsync(record with { VersionId = "rev-1" }, CancellationToken.None));
        await Expect<InvalidDataException>(() =>
            reader.DescribeRecoveryAsync(record with { Location = Path.Combine(setup.Recovery, "other.recovery") },
                CancellationToken.None));
        // A row naming an operation with no archive is stale: it creates and deletes nothing.
        var missingAction = "missing-operation";
        var missingPath = Path.Combine(setup.Recovery,
            SyncLocalRecovery.RecoveryFolderPrefix + LocalChecks.PairId,
            LocalChecks.OperationId(missingAction) + ".recovery");
        var stale = record with { ActionId = missingAction, Location = missingPath };
        await Expect<FileNotFoundException>(() => reader.DescribeRecoveryAsync(stale, CancellationToken.None));
        Require(!File.Exists(missingPath) && File.Exists(reference.Location),
            "a stale local Recovery row creates and deletes nothing");
    }

    /// <summary>Creates the destination in the window between a restore's observe and its create-only put.</summary>
    private sealed class RaceLocalWriter(IEndpointWriter inner, Action beforePut) : IEndpointWriter
    {
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => inner.ObserveAsync(path, ct);
        public Task<Stream> OpenReadAsync(ItemState state, CancellationToken ct) => inner.OpenReadAsync(state, ct);
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct)
        {
            beforePut();
            return inner.PutAsync(action, content, ct);
        }
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => inner.MoveAsync(action, ct);
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => inner.DeleteAsync(action, ct);
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => inner.MkDirAsync(path, ct);
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) =>
            inner.PreserveAsync(victim, actionId, ct);
    }

    private static RecoveryRecord Record(string id, string actionId, Side side, string path, string location,
        string? versionId, string hash, long size)
    {
        var now = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        return new RecoveryRecord("rc-" + id, LocalChecks.PairId, actionId, side, path, location, versionId, hash,
            size, now, now.AddDays(30));
    }

    private static DropboxConnectionBinding Binding() => new(
        DropboxProviderConfiguration.RegisteredClientId, "account-id", "root-namespace", "home-namespace",
        DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes);

    private static async Task Expect<TException>(Func<Task> operation) where TException : Exception
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (TException)
        {
            Console.WriteLine("PASS rejects via " + typeof(TException).Name);
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
