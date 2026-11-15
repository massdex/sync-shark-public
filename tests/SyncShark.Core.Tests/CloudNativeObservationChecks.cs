using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

internal static class CloudNativeObservationChecks
{
    internal static async Task RunAsync(string parent)
    {
        parent = Path.GetFullPath(parent);
        if (!Directory.Exists(parent) || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Supply an existing ordinary admitted fixture directory.");
        var root = Path.Combine(parent, "cloud-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await ExerciseAsync(root); }
        finally
        {
            if (Path.GetDirectoryName(root) != parent || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Fixture cleanup boundary changed.");
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Unexpected fixture reparse member.");
            Directory.Delete(root, true);
        }
    }

    private static async Task ExerciseAsync(string root)
    {
        var local = Path.Combine(root, "local"); Directory.CreateDirectory(local);
        await File.WriteAllTextAsync(Path.Combine(local, "Document"), "local copy must survive");
        await File.WriteAllTextAsync(Path.Combine(local, "ordinary.txt"), "matching bytes");
        using var lease = PilotDirectoryLease.Open(local);
        var pair = new SyncPair("native-pair", "Native", local, lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, "/Fixture", "fixture-root", Provider: SyncProviders.GoogleDrive);
        var db = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        await db.InitializeAsync("native-install", "native-profile", "test", "{}");
        await db.RegisterPairAsync(pair.Id, 1); await db.RetainPairDefinitionAsync(pair, true);
        await db.RegisterRootBindingAsync("native-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(lease.Identity), local);
        await db.RegisterRootBindingAsync("native-cloud", pair.Id, "dropbox", 1,
            "{\"folderId\":\"fixture-root\"}", pair.DropboxRoot);
        var document = Entry("Document", GoogleDriveEntryKind.NativeDocument).ToRemoteEntry();
        var shortcut = Entry("Shortcut", GoogleDriveEntryKind.Shortcut).ToRemoteEntry();
        var unknown = Entry("Unknown", GoogleDriveEntryKind.Unknown).ToRemoteEntry();
        var ordinary = Entry("ordinary.txt", GoogleDriveEntryKind.Blob) with
        { Size = 14, Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("matching bytes"))).ToLowerInvariant() };
        Require(document.TransferBlockedReason is not null && shortcut.TransferBlockedReason is not null &&
            unknown.TransferBlockedReason is not null && ordinary.ToRemoteEntry().TransferBlockedReason is null,
            "real Google projection distinguishes byte files from cloud-native entries");
        foreach (var entry in new[] { document, shortcut, unknown })
        {
            var rejected = false;
            try { WindowsCloudFileIdentity.FromRemote(pair.Id, entry, pair.Provider); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "non-byte entry cannot acquire a Cloud Files placeholder identity");
        }
        var remote = DispatchProxy.Create<ISyncRemoteStore, RemoteInputs>();
        ((RemoteInputs)(object)remote).Entries = [document, shortcut, unknown, ordinary.ToRemoteEntry()];
        var observer = new SyncAuthorityObservationCoordinator(pair, remote, db, "native-local", "native-cloud");
        await observer.ObserveAsync("initial"); await observer.ObserveAsync("initial");
        var cloud = await db.LoadLatestCompleteObservationAsync(pair.Id, "dropbox");
        Require(cloud.Count == 1 && cloud.Single().RelativePath == "ordinary.txt",
            "actual retained byte observations exclude native documents without dropping ordinary files");
        var unavailable = await db.LoadLatestCompleteUncertainPathsAsync(pair.Id, "dropbox");
        Require(unavailable.SetEquals(new[] { "Document", "Shortcut", "Unknown" }),
            "native presence is retained as explicit protected paths, never inferred absent");
        var decisions = await new SyncAuthorityInitialPlanner(db).AdoptAsync(pair.Id);
        Require(decisions.Count(row => row.Kind == SyncDecisionKind.Blocked) == 3 &&
            decisions.Any(row => row.RelativePath == "ordinary.txt" && row.Kind == SyncDecisionKind.Baseline),
            "real initial planner blocks native collisions while adopting matching ordinary bytes");
        Require(await File.ReadAllTextAsync(Path.Combine(local, "Document")) == "local copy must survive" &&
            !File.Exists(Path.Combine(local, "Shortcut")), "no replacement or zero-byte native placeholder is created");
        Console.WriteLine("PASS cloud-native observation: Google projection, CFAPI identity refusal, real SQLite observation and initial-planner containment");
    }
    private static GoogleDriveInventoryEntry Entry(string name, GoogleDriveEntryKind kind) =>
        new(name, kind, "id-" + name, name, 0, null, null, "1", null, null, ["fixture-root"], false,
            kind == GoogleDriveEntryKind.Shortcut ? "target-id" : null, null,
            kind == GoogleDriveEntryKind.NativeDocument ? "application/vnd.google-apps.document" : "application/octet-stream", false);
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidDataException(message); }
    public class RemoteInputs : DispatchProxy
    {
        internal IReadOnlyList<PilotRemoteEntry> Entries { get; set; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            nameof(ISyncRemoteStore.VerifyRootIdentityAsync) => Task.CompletedTask,
            nameof(ISyncRemoteStore.ListEntriesAsync) => Task.FromResult(Entries),
            _ => throw new InvalidOperationException("Unexpected provider effect in observation-only fixture: " + method?.Name)
        };
    }
}
