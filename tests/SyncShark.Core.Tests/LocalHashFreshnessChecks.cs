using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

/// <summary>
/// Focused proof for M5. A retained (volume, physical file identity, size, last-write)
/// tuple is not freshness authority, so a complete, restarted or uncertain local inventory hashes
/// the held bytes even when a retained content hash matches the whole tuple. Only the complete
/// continuous USN/watcher index may reuse unchanged entries, and a missing, restarted, overflowed
/// or discontinuous journal falls back to a complete held-root content scan. Real NTFS files and
/// real production scanner/index/cache/authority decisions; a fixture supplies journal
/// observations only, never the decision under test.
/// </summary>
internal static class LocalHashFreshnessChecks
{
    public static async Task RunAsync(string folder)
    {
        if (Directory.Exists(folder)) throw new IOException("Use a fresh local-hash-freshness fixture.");
        Directory.CreateDirectory(folder);
        VersionIdentityCompatibility();
        await StaleTupleRefusalAsync(folder);
        await ContinuousJournalReuseAsync(folder);
        Console.WriteLine("PASS local hash freshness focused suite complete");
    }

    private static async Task StaleTupleRefusalAsync(string folder)
    {
        var root = Path.Combine(folder, "full-scan");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "edit.txt");
        await File.WriteAllTextAsync(path, "AAAA");
        using var lease = PilotDirectoryLease.Open(root);
        var pair = Pair(lease, "full-scan");
        var authority = await DatabaseAsync(folder, "full-scan");
        await RegisterAsync(authority, pair, lease, "full-scan");

        // 1. A complete/fresh inventory hashes the held bytes; no retained tuple is consulted.
        var seed = await SyncLocalInventoryIndex.ScanAsync(pair, lease, "initial", null);
        var retained = seed.Entries["edit.txt"];
        Require(!seed.Incremental && seed.FilesHashed == 1 && seed.HashesReused == 0,
            "A fresh or restarted local inventory hashes every held file instead of reusing a retained tuple");
        Require(retained.FileId is not null && retained.VolumeSerialNumber is not null &&
                retained.ContentHash == SyncContentIdentity.Compute(pair.Provider, Encoding.UTF8.GetBytes("AAAA")),
            "The complete scan reports the content hash of the held bytes");

        // 2. Retain an object_version row whose whole (volume, file identity, size, last-write) tuple
        // matches this file exactly while its hash describes superseded bytes.
        var staleHash = new string('b', 64);
        var fingerprint = new PilotLocalFileFingerprint("edit.txt", retained.VolumeSerialNumber!.Value,
            retained.FileId!.Value, retained.Size, retained.LastWriteUtc);
        await RegisterVersionAsync(authority, pair, retained, staleHash);
        var cache = await authority.LoadLocalContentHashCacheAsync(retained.VolumeSerialNumber!.Value,
            SyncContentIdentity.AlgorithmFor(pair.Provider));
        Require(await cache.ResolveAsync(fingerprint, default) == staleHash,
            "The retained row matches the whole physical tuple, so only the corrected decision can refuse it");

        // Exercise legacy read compatibility through the real observation store,
        // not merely through a JSON comparator. The old row remains immutable.
        var legacyIdentity = SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File,
            retained.VolumeSerialNumber.Value, retained.FileId.Value, retained.LastWriteUtc,
            contentAlgorithm: SyncContentIdentity.IdentityStampFor(pair.Provider));
        var legacyScan = await authority.BeginScanGenerationAsync(pair.Id,
            "root-local-hash-freshness-full-scan-local", "local", "initial", "{\"scope\":\"complete\"}");
        await authority.AppendObservationEntriesAsync(legacyScan,
            [new SyncAuthorityObservationInput("edit.txt", "file", legacyIdentity, retained.ContentHash,
                retained.Size, JsonSerializer.Serialize(new { path = "edit.txt", identity = legacyIdentity }))]);
        await authority.CompleteScanGenerationAsync(legacyScan);
        var legacyVersion = (await authority.LoadLatestCompleteObservationAsync(pair.Id, "local"))
            .Single(item => item.RelativePath == "edit.txt");

        // 3. Every reachable production consumer must refuse that tuple for a complete inventory.
        var observed = await Coordinator(pair, authority, "full-scan").ObserveAsync("periodic");
        var firstVersion = (await authority.LoadLatestCompleteObservationAsync(pair.Id, "local"))
            .Single(item => item.RelativePath == "edit.txt");
        var retainedIdentity = (await authority.LoadObjectVersionAsync(firstVersion.ObjectVersionId)).IdentityJson;
        Require(firstVersion.ObjectId == legacyVersion.ObjectId &&
                (await authority.LoadObjectVersionAsync(legacyVersion.ObjectVersionId)).IdentityJson == legacyIdentity,
            "Fresh content-qualified observation preserves the legacy logical object and exact old identity bytes");
        Require(observed.LocalFilesHashed == 1 && observed.LocalHashesReused == 0,
            "The production full/periodic inventory hashes the held file and never loads the retained hash cache");
        var rescanned = await SyncLocalInventoryIndex.ScanAsync(pair, lease, "safety-check", null);
        Require(rescanned.Entries["edit.txt"].ContentHash == retained.ContentHash &&
                rescanned.Entries["edit.txt"].ContentHash != staleHash,
            "The complete safety scan reports the held bytes rather than the matching retained tuple");

        // 4. A same-size edit that restores the timestamp keeps the entire physical tuple.
        await File.WriteAllTextAsync(path, "BBBB");
        File.SetLastWriteTimeUtc(path, retained.LastWriteUtc);
        Require(File.GetLastWriteTimeUtc(path) == retained.LastWriteUtc && new FileInfo(path).Length == retained.Size,
            "The fixture restored the exact recorded length and timestamp on the same file identity");
        var edited = await SyncLocalInventoryIndex.ScanAsync(pair, lease, "safety-check", null);
        var editedEntry = edited.Entries["edit.txt"];
        Require(editedEntry.FileId == retained.FileId && editedEntry.VolumeSerialNumber == retained.VolumeSerialNumber &&
                editedEntry.Size == retained.Size && editedEntry.LastWriteUtc == retained.LastWriteUtc,
            "The same-size timestamp-restored edit leaves volume, physical file identity, size and last-write unchanged");
        Require(editedEntry.ContentHash == SyncContentIdentity.Compute(pair.Provider, Encoding.UTF8.GetBytes("BBBB")) &&
                editedEntry.ContentHash != retained.ContentHash && editedEntry.ContentHash != staleHash,
            "The fresh/full scan detects the restored-timestamp edit from the held bytes");
        Require(await cache.ResolveAsync(fingerprint with { LastWriteUtc = editedEntry.LastWriteUtc }, default) == staleHash,
            "The retained tuple still matches the edited file, so the refusal is a decision and not a cache miss");
        var reobserved = await Coordinator(pair, authority, "full-scan").ObserveAsync("periodic");
        Require(reobserved.LocalFilesHashed == 1 && reobserved.LocalHashesReused == 0,
            "The production inventory re-hashes the edited file instead of accepting the matching stale tuple");
        var secondVersion = (await authority.LoadLatestCompleteObservationAsync(pair.Id, "local"))
            .Single(item => item.RelativePath == "edit.txt");
        var previous = await authority.LoadObjectVersionAsync(firstVersion.ObjectVersionId);
        Require(secondVersion.ObjectId == firstVersion.ObjectId &&
                secondVersion.ObjectVersionId != firstVersion.ObjectVersionId &&
                secondVersion.ContentHash == editedEntry.ContentHash &&
                previous.ContentHash == retained.ContentHash && previous.IdentityJson == retainedIdentity,
            "A metadata-preserving byte edit retains two distinct versions of the same object without rewriting history");
        await Coordinator(pair, authority, "full-scan").ObserveAsync("periodic");
        var repeated = (await authority.LoadLatestCompleteObservationAsync(pair.Id, "local"))
            .Single(item => item.RelativePath == "edit.txt");
        Require(repeated.ObjectVersionId == secondVersion.ObjectVersionId,
            "An identical fresh observation reuses the exact content version rather than colliding or inventing another version");
    }

    private static void VersionIdentityCompatibility()
    {
        var time = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var hash = SyncContentIdentity.Compute("dropbox", Encoding.UTF8.GetBytes("AAAA"));
        var differentHash = SyncContentIdentity.Compute("dropbox", Encoding.UTF8.GetBytes("BBBB"));
        var legacy = SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File, 1, 2, time);
        var current = SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File, 1, 2, time,
            contentHash: hash, size: 4);
        Require(SyncAuthorityLocalVersionIdentity.EquivalentWithKnownContent(legacy, current, hash, 4),
            "Legacy identity compatibility requires the same physical bindings and separately verified byte evidence");
        Require(!SyncAuthorityLocalVersionIdentity.EquivalentWithKnownContent(current, current, differentHash, 4) &&
                !SyncAuthorityLocalVersionIdentity.EquivalentWithKnownContent(current, current, hash, 5),
            "Even equal identity JSON refuses contradictory declared content or size evidence");
        var foreignPhysical = SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File, 1, 3, time,
            contentHash: hash, size: 4);
        var cloud = SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File, 1, 2, time,
            "id:cloud", "rev-1", contentHash: hash, size: 4);
        var otherCloud = SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File, 1, 2, time,
            "id:cloud", "rev-2", contentHash: hash, size: 4);
        Require(!SyncAuthorityLocalVersionIdentity.EquivalentWithKnownContent(current, foreignPhysical, hash, 4) &&
                !SyncAuthorityLocalVersionIdentity.EquivalentWithKnownContent(cloud, otherCloud, hash, 4),
            "Content compatibility never weakens a physical file identity or cloud revision fence");
        var directory = SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.Directory, 1, 2, time);
        Require(directory == SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.Directory, 1, 2, time,
                contentHash: hash, size: 4),
            "Directory identities retain the established metadata-only shape");
    }

    private static async Task ContinuousJournalReuseAsync(string folder)
    {
        var root = Path.Combine(folder, "continuity");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "kept.txt"), "kept");
        await File.WriteAllTextAsync(Path.Combine(root, "changed.txt"), "first");
        using var lease = PilotDirectoryLease.Open(root);
        var pair = Pair(lease, "continuity");
        var checkpoint = new NtfsUsnCheckpoint(1, 1, 2, 1);
        IReadOnlyList<NtfsUsnChange> changes = [];
        var continuous = new SyncLocalInventoryIndex.JournalSource(_ => checkpoint, (_, _, _, _) => changes);
        var first = await SyncLocalInventoryIndex.ScanWithJournalAsync(pair, lease, "initial", null, continuous);
        Require(!first.Incremental && first.FilesHashed == 2 && first.HashesReused == 0,
            "A fresh index hashes every held file; a retained tuple is never consulted for a complete inventory");
        var idle = await SyncLocalInventoryIndex.ScanWithJournalAsync(pair, lease, "monitor", null, continuous);
        Require(idle.Incremental && idle.FilesHashed == 0 && idle.PathsInspected == 0 &&
                ReferenceEquals(idle.Entries, first.Entries),
            "A continuous journal/watcher with no relevant change reuses the retained inventory without hashing");
        changes = [new NtfsUsnChange(first.Entries["changed.txt"].FileId!.Value, pair.LocalRootFileId,
            3, 1, FileAttributes.Normal, "changed.txt")];
        await File.WriteAllTextAsync(Path.Combine(root, "changed.txt"), "second");
        var refreshed = await SyncLocalInventoryIndex.ScanWithJournalAsync(pair, lease, "monitor", null, continuous);
        Require(refreshed.Incremental && refreshed.FilesHashed == 1 && refreshed.PathsInspected == 1 &&
                refreshed.HashesReused == 0 && refreshed.Entries.Count == 2,
            "A continuously reported changed path rehashes only that path and keeps the other entry");
        Require(refreshed.Entries["changed.txt"].ContentHash != first.Entries["changed.txt"].ContentHash &&
                refreshed.Entries["kept.txt"].ContentHash == first.Entries["kept.txt"].ContentHash,
            "Only the reported changed path changes content identity; the unchanged entry keeps its retained hash");
        var overflowed = new SyncLocalInventoryIndex.JournalSource(_ => checkpoint, (_, _, _, _) => null);
        var afterOverflow = await SyncLocalInventoryIndex.ScanWithJournalAsync(pair, lease, "monitor", null, overflowed);
        Require(!afterOverflow.Incremental && afterOverflow.FilesHashed == 2 && afterOverflow.PathsInspected == 2,
            "An overflowed or unknown journal result falls back to a complete held-root content scan");
        var safety = await SyncLocalInventoryIndex.ScanWithJournalAsync(pair, lease, "periodic", null, continuous);
        Require(!safety.Incremental && safety.FilesHashed == 2 && safety.PathsInspected == 2,
            "A deliberate full/periodic trigger hashes the whole held root even while the journal is continuous");
    }

    private static SyncPair Pair(PilotDirectoryLease lease, string name) =>
        new("local-hash-freshness-" + name, "Local hash freshness " + name, lease.Path,
            lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/Local hash freshness " + name,
            "id:local-hash-freshness-" + name);

    private static async Task<SyncAuthorityDatabase> DatabaseAsync(string folder, string name)
    {
        var authority = new SyncAuthorityDatabase(Path.Combine(folder, "state-" + name, "authority.db"));
        await authority.InitializeAsync("installation-local-hash-freshness", "profile-local-hash-freshness",
            "local-hash-freshness-checks", "{}");
        return authority;
    }

    private static async Task RegisterAsync(SyncAuthorityDatabase authority, SyncPair pair,
        PilotDirectoryLease lease, string name)
    {
        await authority.RegisterPairAsync(pair.Id, 1);
        await authority.RegisterRootBindingAsync("root-local-hash-freshness-" + name + "-local", pair.Id,
            "local", 1, JsonSerializer.Serialize(new { lease.Identity.VolumeSerialNumber, lease.Identity.FileId }),
            lease.Path);
        await authority.RegisterRootBindingAsync("root-local-hash-freshness-" + name + "-dropbox", pair.Id,
            "dropbox", 1, JsonSerializer.Serialize(new { folderId = pair.DropboxFolderId }), pair.DropboxRoot);
    }

    private static Task RegisterVersionAsync(SyncAuthorityDatabase authority, SyncPair pair,
        PilotLocalEntry retained, string hash)
    {
        var identity = JsonSerializer.Serialize(new
        {
            volumeSerial = retained.VolumeSerialNumber!.Value.ToString("x8"),
            fileId = retained.FileId!.Value.ToString("x16"),
            lastWriteUtc = retained.LastWriteUtc.ToUniversalTime().ToString("O"),
            contentAlgorithm = SyncContentIdentity.AlgorithmFor(pair.Provider)
        });
        return RegisterVersionRowAsync(authority, retained.Size, identity, hash);
    }

    private static async Task RegisterVersionRowAsync(SyncAuthorityDatabase authority, long size,
        string identity, string hash)
    {
        await authority.RegisterObjectAsync("object-local-hash-freshness", "file");
        await authority.RegisterObjectVersionAsync("version-local-hash-freshness",
            "object-local-hash-freshness", "local", identity, hash, size);
    }

    private static SyncAuthorityObservationCoordinator Coordinator(SyncPair pair,
        SyncAuthorityDatabase authority, string name) =>
        new(pair, new FakeSyncRemoteStore(), authority, "root-local-hash-freshness-" + name + "-local",
            "root-local-hash-freshness-" + name + "-dropbox");

    private static void Require(bool pass, string label)
    { if (!pass) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
}
