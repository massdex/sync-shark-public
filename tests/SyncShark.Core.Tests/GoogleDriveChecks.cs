using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// Controlled-HTTP integration checks for the Google Drive provider. The HTTP fixture replaces external
// I/O only: every decision under test runs through the real GoogleDrivePilotClient and, for the
// end-to-end case, the real observation coordinator, planner and pair runner.
internal static class GoogleDriveChecks
{
    internal static async Task RunAsync(string root)
    {
        // Every run owns fresh bounded state, with native test lifecycle cleanup. A prior
        // successful baseline must not turn the next run's first upload into an unchanged scan.
        var fixture = Path.Combine(Path.GetFullPath(root), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try { await RunCoreAsync(fixture); }
        finally { Directory.Delete(fixture, recursive: true); }
    }

    private static async Task RunCoreAsync(string root)
    {
        Directory.CreateDirectory(root);
        GoogleDriveProviderConfiguration.RegisteredClientIdForTest = GoogleDriveProviderConfiguration.PendingClientId;
        await UnboundBuildFailsClosedAsync();
        GoogleDriveProviderConfiguration.RegisteredClientIdForTest = "test-client";
        await ProviderSurfaceAsync(root);
        await AmbiguityAndSafetyAsync(root);
        await TransferAsync(root);
        await ResumableSessionOriginAsync();
        await ResumableAcknowledgementGuardAsync();
        await ProductionTransportRedirectAsync(root);
        await CompletedSessionReadbackAsync();
        await DestinationUploadPoliciesAsync();
        await DownloadMetadataChangesAsync();
        await ConditionalUpdatesAsync();
        await ChangeFeedAsync(root);
        await ConcurrentInventoryPublicationAsync();
        await InventoryPageBoundingAsync();
        await FreshExactLookupAsync();
        await AccountIsolationAsync(root);
        await AccountPersistenceAsync(root);
        await GoogleDriveOAuthChecks.RunAsync();
        await GoogleDriveAccountFlowChecks.RunAsync(Path.Combine(root, "account-flow"));
        await DigestSeamAsync();
        await PlannerIntegrationAsync(root);
        Console.WriteLine("PASS Google Drive provider surface, safety, transfer, change, isolation and planner checks");
    }

    // An explicitly unbound build must refuse even to verify a root. The production public ID is
    // separately registered; this seam retains the missing-registration refusal proof.
    private static async Task UnboundBuildFailsClosedAsync()
    {
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        try
        {
            await Client(server, Binding()).VerifyRootIdentityAsync();
            throw new InvalidOperationException("an unregistered Google client was unexpectedly accepted");
        }
        catch (InvalidOperationException error) when (
            error.Message.Contains("No Google Cloud OAuth client is registered", StringComparison.Ordinal))
        {
            Console.WriteLine("PASS the unregistered build fails closed before any Google Drive call");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Google Drive check failed: " + message);
    }

    private static async Task CompletedSessionReadbackAsync()
    {
        foreach (var mutation in new[] { "none", "digest", "name", "parent", "duplicate" })
        {
            var server = new SyntheticDriveServer { LoseSessionCompletionAfterApply = true };
            server.AddFolder("root-folder", "Pair", null);
            server.AddFolder("other-folder", "Other", null);
            var payload = Enumerable.Range(0, 4096).Select(index => (byte)(index % 113)).ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            SyncUploadSessionCheckpoint? retained = null;
            var client = Client(server, Binding());
            try
            {
                await client.UploadResumableAsync("completed.bin", new MemoryStream(payload), hash,
                    null, null, (checkpoint, _) => { retained = checkpoint; return Task.CompletedTask; });
                throw new InvalidOperationException("The deliberately lost completion unexpectedly returned success.");
            }
            catch (GoogleDriveEffectUncertainException) { }
            Require(server.WriteCalls == 1 && retained is not null,
                "U06 one real session write applied with a retained checkpoint before response loss");
            var node = server.Get(server.IdByName("root-folder", "completed.bin"));
            switch (mutation)
            {
                case "digest": node.Content[0] ^= 1; break;
                case "name": node.Name = "moved.bin"; break;
                case "parent": node.ParentId = "other-folder"; break;
                case "duplicate": server.AddFile("duplicate-id", "completed.bin", "root-folder", payload); break;
            }
            var chunkCalls = server.SessionChunkCalls;
            using var source = new ReadForbiddenStream(payload);
            if (mutation == "none")
            {
                var result = await client.UploadResumableAsync("completed.bin", source, hash, null,
                    retained, (checkpoint, _) => { retained = checkpoint; return Task.CompletedTask; });
                Require(result.FileId == node.Id && result.Size == payload.Length && result.ContentHash == hash &&
                        retained!.ConfirmedOffset == payload.Length,
                    "U06 completed status yields the exact verified result and full acknowledged checkpoint");
            }
            else
            {
                try
                {
                    await client.UploadResumableAsync("completed.bin", source, hash, null, retained,
                        (checkpoint, _) => { retained = checkpoint; return Task.CompletedTask; });
                    throw new InvalidOperationException("A changed completed-session object was accepted.");
                }
                catch (GoogleDriveEffectUncertainException) { }
            }
            Require(server.WriteCalls == 1 && server.SessionChunkCalls == chunkCalls + 1,
                "U06 status-only verification never retransmits bytes or repeats the applied write");
            Console.WriteLine("PASS U06 completed-session status/readback/no-replay " + mutation);
        }
    }

    private static Task DestinationUploadPoliciesAsync()
    {
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        var google = Client(server, Binding());
        using var http = new HttpClient(server);
        var dropbox = new DropboxPilotClient(http, new StubDropboxTokenSource(), "/Pair",
            new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "test-account",
                "test-namespace", "test-namespace", "full_dropbox", DropboxProviderConfiguration.RequiredScopes),
            "id:pair");
        var policies = new[] { SyncUploadLimits.ForStore(dropbox), SyncUploadLimits.ForStore(google) };
        Require(policies[0].MaximumSimpleCreateBytes == 157_286_400 &&
                policies[1].MaximumSimpleCreateBytes == GoogleDrivePilotClient.DefaultResumableThresholdBytes &&
                policies[1].MaximumSimpleReplacementBytes == long.MaxValue,
            "U03 actual adapters publish distinct create and guarded replacement policies");
        foreach (var size in new[] { 150_000_000L, 150_000_001L, 157_286_400L, 157_286_401L })
            foreach (var policy in policies)
                foreach (var replacement in new[] { false, true })
                {
                    var expected = policy.RequiresSession(size, replacement);
                    var options = new SyncAuthorityPlannerOptions(1, "profile", "local-control", "remote-control",
                        RemoteUploadSessionPolicy: policy);
                    var legacyKind = replacement ? SyncAuthorityOperationKind.UpdateRemote : SyncAuthorityOperationKind.CreateRemote;
                    var legacy = SyncAuthorityOperationAllocations.Build("boundary-operation", legacyKind,
                        "boundary.bin", "left-root", "right-root", "file", size, options);
                    Require(legacy.Any(item => item.Kind == SyncAuthorityAllocationKind.UploadSession) == expected,
                        "U03 legacy allocation uses the exact adapter/operation size policy");
                    var target = new SyncAuthorityCloudTargetAllocation("recovery-root",
                        policy.MaximumSimpleCreateBytes, policy.MaximumSimpleReplacementBytes);
                    var cloudOptions = options with
                    {
                        CloudAllocations = new SyncAuthorityCloudAllocationConfiguration("staging", target, target)
                    };
                    foreach (var left in new[] { false, true })
                    {
                        var kind = replacement
                            ? left ? SyncAuthorityOperationKind.UpdateLocal : SyncAuthorityOperationKind.UpdateRemote
                            : left ? SyncAuthorityOperationKind.CreateLocal : SyncAuthorityOperationKind.CreateRemote;
                        var cloud = SyncAuthorityOperationAllocations.Build("boundary-operation", kind,
                            "boundary.bin", "left-root", "right-root", "file", size, cloudOptions);
                        Require(cloud.Any(item => item.Kind == SyncAuthorityAllocationKind.UploadSession) == expected &&
                                cloud.Where(item => item.Kind == SyncAuthorityAllocationKind.UploadSession)
                                    .All(item => item.IntendedParentIdentity == (left ? "left-root" : "right-root")),
                            "U03 both cloud directions use the exact destination/operation limit and root");
                    }
                }
        Console.WriteLine("PASS U03 real-adapter policies and four exact size boundaries in legacy/both cloud directions");
        return Task.CompletedTask;
    }

    private sealed class ReadForbiddenStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Completed-session verification read source bytes.");
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("Completed-session verification read source bytes.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Completed-session verification read source bytes.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Completed-session verification read source bytes.");
    }

    private sealed class StubDropboxTokenSource : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("synthetic-access-token");
    }

    private static async Task DownloadMetadataChangesAsync()
    {
        foreach (var change in new[] { "metadata", "content", "revision", "name", "parent", "trash", "missing-revision", "permission", "mime" })
        {
            var server = new SyntheticDriveServer();
            server.AddFolder("root-folder", "Pair", null);
            var payload = Encoding.UTF8.GetBytes("exact reviewed download bytes");
            server.AddFile("download-file", "download.txt", "root-folder", payload);
            var client = Client(server, Binding());
            var reviewed = (await client.ListEntriesAsync()).Single(item => item.Id == "download-file");
            server.AfterMediaRead = node =>
            {
                node.HeadRevisionOverride = node.HeadRevisionId;
                node.Version++;
                switch (change)
                {
                    case "content": node.Content = Encoding.UTF8.GetBytes("different reviewed download!"); break;
                    case "revision": node.HeadRevisionOverride = "new-binary-revision"; break;
                    case "name": node.Name = "renamed.txt"; break;
                    case "parent": node.ParentId = "other-folder"; break;
                    case "trash": node.Trashed = true; break;
                    case "missing-revision": node.HeadRevisionOverride = string.Empty; break;
                    case "permission": node.CanDownload = false; break;
                    case "mime": node.MimeType = "text/plain"; break;
                }
            };
            using var destination = new MemoryStream();
            if (change == "metadata")
            {
                var result = await client.DownloadAsync(reviewed, destination);
                Require(result.Revision == reviewed.Revision && destination.ToArray().SequenceEqual(payload),
                    "metadata-only version change preserves exact reviewed binary bytes");
            }
            else
                await ExpectBlockedAsync("download changed " + change, () => client.DownloadAsync(reviewed, destination));
            Require(server.WriteCalls == 0, "download verification never writes remote state");
        }
    }

    private static async Task ConditionalUpdatesAsync()
    {
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        server.AddFile("file-one", "update.txt", "root-folder", Encoding.UTF8.GetBytes("old"));
        server.AddFile("file-two", "other.txt", "root-folder", Encoding.UTF8.GetBytes("other"));
        using var http = new HttpClient(server);
        var client = new GoogleDrivePilotClient(http, new StubTokenSource(), Binding(), "root-folder", "/Pair",
            capabilities: GoogleDriveCapabilities.Baseline);
        var reviewed = await client.GetFileAsync("file-one");
        var other = await client.GetFileAsync("file-two");
        Require(reviewed.Version == other.Version && reviewed.Revision != other.Revision,
            "Google per-file version counters bind their stable file identity");
        var payload = Encoding.UTF8.GetBytes("updated through the real conditional adapter");
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        await ExpectBlockedAsync("same-counter different-file update", () => client.UploadAsync("update.txt",
            new MemoryStream(payload), digest, other.Revision));
        Require(server.WriteCalls == 0, "different stable identity rejected before a write");
        var result = await client.UploadAsync("update.txt", new MemoryStream(payload), digest, reviewed.Revision);
        Require(server.Get("file-one").Content.SequenceEqual(payload) && result.FileId == "file-one" &&
            result.Revision != reviewed.Revision, "guarded current update preserves id and verifies bytes/version");
        await ExpectBlockedAsync("stale reviewed conditional update", () => client.UploadAsync("update.txt",
            new MemoryStream(payload), digest, reviewed.Revision));
        reviewed = await client.GetFileAsync("file-one");
        server.OmitConditionalEtag = true;
        var writes = server.WriteCalls;
        await ExpectBlockedAsync("missing strong guard", () => client.UploadAsync("update.txt",
            new MemoryStream(payload), digest, reviewed.Revision));
        Require(server.WriteCalls == writes, "missing ETag rejected before effect");
        server.OmitConditionalEtag = false;
        server.RaceBeforeConditionalWrite = true;
        await ExpectBlockedAsync("concurrent edit at effect boundary", () => client.UploadAsync("update.txt",
            new MemoryStream(payload), digest, reviewed.Revision));
        Require(server.ConditionalRejections == 1 && server.WriteCalls == writes &&
            Encoding.UTF8.GetString(server.Get("file-one").Content) == "concurrent edit",
            "server guard rejects stale write and preserves concurrent bytes without retry");
        reviewed = await client.GetFileAsync("file-one");
        var moved = await client.MoveAsync(reviewed.ToRemoteEntry(), "renamed.txt");
        Require(moved.RelativePath == "renamed.txt" && moved.Id == reviewed.FileId &&
            server.Get("file-one").Name == "renamed.txt", "conditional rename preserves exact identity");
        server.RaceBeforeConditionalWrite = true;
        writes = server.WriteCalls;
        await ExpectBlockedAsync("concurrent edit before trash", () => client.DeleteAsync(moved));
        Require(server.WriteCalls == writes && !server.Get("file-one").Trashed && server.ConditionalRejections == 2,
            "stale trash guard preserves concurrent file without retry");
        await client.DeleteAsync((await client.GetFileAsync("file-one")).ToRemoteEntry());
        Require(server.Get("file-one").Trashed, "current guarded file moves to recoverable trash");
        var folder = server.AddFolder("nested", "Nested", "root-folder");
        var folderRevision = (await client.GetFileAsync(folder.Id)).Revision;
        await ExpectBlockedAsync("unprotected folder subtree trash", () => client.DeleteAsync(
            new PilotRemoteEntry("Nested", PilotEntryKind.Directory, folder.Id, folderRevision, 0, null)));
        Console.WriteLine("PASS production conditional-update baseline, stale guards, rename and recoverable trash");
    }

    private static async Task ExpectBlockedAsync(string label, Func<Task> action)
    {
        try
        {
            await action();
            throw new InvalidOperationException(label + " was unexpectedly accepted");
        }
        catch (GoogleDriveBlockedException blocked)
        {
            Console.WriteLine("PASS " + label + " refused: " + blocked.Message.Substring(0,
                Math.Min(90, blocked.Message.Length)));
        }
    }

    private static GoogleDriveConnectionBinding Binding(string permissionId = "perm-account-one",
        bool sharedDrives = false) => new GoogleDriveConnectionBinding(
        ClientId: "test-client", AccountKey: GoogleDriveConnectionBinding.CreateAccountKey(permissionId),
        PermissionId: permissionId, Email: permissionId + "@example.invalid", DisplayName: "Drive User",
        Scopes: [GoogleDriveProviderConfiguration.DriveScope],
        CredentialReference: "massdex/account-access/v1/google-drive/sync-shark-test/production/token",
        SharedDrivesEnabled: sharedDrives).Normalize();

    private static GoogleDrivePilotClient Client(SyntheticDriveServer server,
        GoogleDriveConnectionBinding binding, string rootFolderId = "root-folder",
        bool allowSharedDrives = false, long? resumableThreshold = null, int? chunkSize = null,
        GoogleDriveConnectionBinding? account = null)
    {
        var http = new HttpClient(server);
        return new GoogleDrivePilotClient(http, new StubTokenSource(), binding, rootFolderId, "/Pair",
            allowSharedDrives: allowSharedDrives, resumableThresholdBytes: resumableThreshold,
            resumableChunkSizeBytes: chunkSize,
            capabilities: GoogleDriveCapabilities.Baseline.WithSharedDrives(allowSharedDrives) with { AtomicConditionalWrites = false });
    }

    private static async Task ProviderSurfaceAsync(string root)
    {
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        server.AddFolder("child-folder", "Child", "root-folder");
        server.AddFile("blob-1", "notes.txt", "root-folder", Encoding.UTF8.GetBytes("hello drive"));
        server.AddFile("blob-deep", "deep.txt", "child-folder", Encoding.UTF8.GetBytes("nested"));
        server.AddNativeDocument("doc-1", "Plan", "root-folder");
        server.AddShortcut("short-1", "Linked", "root-folder", "target-1");

        var client = Client(server, Binding());
        await client.VerifyRootIdentityAsync();
        var inventory = await client.ListInventoryAsync();
        Require(inventory.Ambiguities.Count == 0, "a clean inventory has no ambiguity");
        Require(inventory.Entries.Any(item => item.RelativePath == "Child" &&
            item.Kind == GoogleDriveEntryKind.Folder), "a nested folder is inventoried by derived path");
        Require(inventory.Entries.Any(item => item.RelativePath == "Child/deep.txt"),
            "derived paths follow the real parent chain");
        Require(inventory.Entries.Any(item => item.RelativePath == "notes.txt" &&
            item.Kind == GoogleDriveEntryKind.Blob && item.Sha256 is not null), "a blob file keeps its digest");
        Require(inventory.Entries.Single(item => item.RelativePath == "Plan").Kind ==
            GoogleDriveEntryKind.NativeDocument, "a Google-native document is classified as native");
        Require(inventory.Entries.Single(item => item.RelativePath == "Linked").Kind ==
            GoogleDriveEntryKind.Shortcut, "a shortcut is classified as a shortcut");
        Require(inventory.Entries.Single(item => item.RelativePath == "Linked").ShortcutTargetId == "target-1",
            "a shortcut retains its target id");

        var entries = await client.ListEntriesAsync();
        var native = entries.Single(item => item.RelativePath == "Plan");
        await ExpectBlockedAsync("native document download", () => client.DownloadAsync(native,
            new MemoryStream()));
        var shortcut = entries.Single(item => item.RelativePath == "Linked");
        await ExpectBlockedAsync("shortcut mutation", () => client.DeleteAsync(shortcut));

        var caps = client.Capabilities;
        Require(!caps.FileRequests && !caps.AccountRewind && !caps.AtomicConditionalWrites,
            "Drive capabilities are honest about file requests, rewind and conditional writes");
        Require(caps.UnsupportedUserOutcomes().Count >= 3, "unsupported outcomes are explained to the user");
        Require(GoogleDriveCapabilities.Baseline.AtomicConditionalWrites,
            "production conditional capability is enabled after real Google PUT/PATCH stale412 acceptance");
        Require(client.ProviderCallCount > 0, "provider calls are counted");
        Console.WriteLine("PASS Google Drive inventory classification, capability honesty and call counting");
    }

    private static async Task AmbiguityAndSafetyAsync(string root)
    {
        // T1: two different objects collapse to one relative path.
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        server.AddFile("dup-a", "dup.txt", "root-folder", Encoding.UTF8.GetBytes("first"));
        server.AddFile("dup-b", "dup.txt", "root-folder", Encoding.UTF8.GetBytes("second"));
        var client = Client(server, Binding());
        var inventory = await client.ListInventoryAsync();
        Require(inventory.Ambiguities.Count == 1, "duplicate sibling names are reported as ambiguity");
        Require(inventory.Ambiguities[0].FileIds.Count == 2 &&
            inventory.Ambiguities[0].FileIds.Contains("dup-a") &&
            inventory.Ambiguities[0].FileIds.Contains("dup-b"), "both duplicate ids are retained");
        await ExpectBlockedAsync("ambiguous inventory", () => client.ListEntriesAsync());

        // T16: an incomplete corpus search is never proof of absence.
        var incomplete = new SyntheticDriveServer { IncompleteSearch = true };
        incomplete.AddFolder("root-folder", "Pair", null);
        incomplete.AddFile("blob-x", "only.txt", "root-folder", Encoding.UTF8.GetBytes("x"));
        await ExpectBlockedAsync("incomplete inventory",
            () => Client(incomplete, Binding()).ListEntriesAsync());

        // T12: adopt one existing folder by id, refuse when a file occupies the name.
        var folders = new SyntheticDriveServer();
        folders.AddFolder("root-folder", "Pair", null);
        folders.AddFolder("existing-folder", "Child", "root-folder");
        var folderClient = Client(folders, Binding());
        var adopted = await folderClient.CreateDirectoryAsync("Child");
        Require(adopted.Id == "existing-folder", "the existing folder is adopted by stable id");
        Require(folders.CreateFolderCalls == 0, "adopting an existing folder creates nothing");
        var nested = await folderClient.CreateDirectoryAsync("Child/Deep");
        Require(folders.CreateFolderCalls == 1 &&
            folders.Get(nested.Id!).ParentId == "existing-folder",
            "only the missing child folder is created inside the adopted folder");
        var occupied = new SyntheticDriveServer();
        occupied.AddFolder("root-folder", "Pair", null);
        occupied.AddFile("file-1", "Child", "root-folder", Encoding.UTF8.GetBytes("not a folder"));
        await ExpectBlockedAsync("folder name occupied by a file",
            () => Client(occupied, Binding()).CreateDirectoryAsync("Child"));

        // T13: a stale reviewed version is refused and no write reaches the provider.
        var stale = new SyntheticDriveServer();
        stale.AddFolder("root-folder", "Pair", null);
        stale.AddFile("blob-1", "notes.txt", "root-folder", Encoding.UTF8.GetBytes("v1"));
        var staleClient = Client(stale, Binding());
        await staleClient.ListEntriesAsync();
        var reviewed = await staleClient.GetFileAsync("blob-1");
        stale.BumpVersion("blob-1");
        var payload = Encoding.UTF8.GetBytes("v2");
        await ExpectBlockedAsync("stale-version update", () => staleClient.UploadAsync("notes.txt",
            new MemoryStream(payload), Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            reviewed.Version));
        Require(stale.WriteCalls == 0, "no content write reached Google Drive after a stale-version refusal");

        // Interleaving proof: the reviewed revision still matches at the pre-read, a competing change
        // happens before any mutation could be attempted, and the adapter must refuse with zero requests
        // beyond its read-only probes. No content write may reach the endpoint at all.
        var interleave = new SyntheticDriveServer();
        interleave.AddFolder("root-folder", "Pair", null);
        interleave.AddFile("blob-1", "notes.txt", "root-folder", Encoding.UTF8.GetBytes("v1"));
        var interleaveClient = Client(interleave, Binding());
        await interleaveClient.ListEntriesAsync();
        var matching = await interleaveClient.GetFileAsync("blob-1");
        interleave.BumpVersion("blob-1");
        var interleavePayload = Encoding.UTF8.GetBytes("concurrent edit wins");
        try
        {
            await interleaveClient.UploadAsync("notes.txt", new MemoryStream(interleavePayload),
                Convert.ToHexString(SHA256.HashData(interleavePayload)).ToLowerInvariant(),
                matching.Version);
            throw new InvalidOperationException("a matching-revision update was unexpectedly attempted");
        }
        catch (GoogleDriveBlockedException blocked) when (
            blocked.Message == GoogleDrivePilotClient.DriveUpdateUnsupported)
        {
            Require(interleave.WriteCalls == 0,
                "an existing-item update must issue zero write requests, including parent-folder creation");
            Require(interleave.Get("blob-1").Content.SequenceEqual(Encoding.UTF8.GetBytes("v1")),
                "the concurrent edit must be preserved untouched");
            Console.WriteLine("PASS a matching-revision update is refused before any write and the concurrent edit survives");
        }

        // Create-only upload is refused when the destination already exists.
        var occupiedUpload = new SyntheticDriveServer();
        occupiedUpload.AddFolder("root-folder", "Pair", null);
        occupiedUpload.AddFile("blob-1", "notes.txt", "root-folder", Encoding.UTF8.GetBytes("v1"));
        var bytes = Encoding.UTF8.GetBytes("new");
        await ExpectBlockedAsync("create-only upload over existing content",
            () => Client(occupiedUpload, Binding()).UploadAsync("notes.txt", new MemoryStream(bytes),
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), null));
        Require(occupiedUpload.WriteCalls == 0, "create-only refusal sends no write");

        // T10: delete is trash-only; permanent deletion is explicitly unavailable.
        var trash = new SyntheticDriveServer();
        trash.AddFolder("root-folder", "Pair", null);
        trash.AddFile("blob-1", "notes.txt", "root-folder", Encoding.UTF8.GetBytes("v1"));
        var trashClient = Client(trash, Binding());
        await trashClient.ListEntriesAsync();
        var trashWrites = trash.WriteCalls;
        try
        {
            await trashClient.DeleteAsync((await trashClient.GetFileAsync("blob-1")).ToRemoteEntry());
            throw new InvalidOperationException("a revision-bound trash effect was unexpectedly attempted");
        }
        catch (GoogleDriveBlockedException blocked) when (
            blocked.Message.Contains("atomic guard", StringComparison.Ordinal))
        {
            Require(!trash.Get("blob-1").Trashed && trash.WriteCalls == trashWrites,
                "a revision-bound trash must be refused with zero remote writes");
            Console.WriteLine("PASS a revision-bound trash is refused before any write");
        }
        var trashedEntry = (await trashClient.GetFileAsync("blob-1")).ToRemoteEntry();
        await ExpectBlockedAsync("permanent delete",
            () => trashClient.DeletePermanentlyAsync(trashedEntry));

        // T9: shared drive capability is explicit, never silently My Drive.
        var shared = new SyntheticDriveServer();
        shared.AddFolder("root-folder", "Pair", null, "shared-drive-1");
        shared.AddFile("blob-1", "notes.txt", "root-folder", Encoding.UTF8.GetBytes("v1"));
        await ExpectBlockedAsync("shared drive without capability",
            () => Client(shared, Binding(), allowSharedDrives: false).VerifyRootIdentityAsync());
        var allowed = Client(shared, Binding(sharedDrives: true), allowSharedDrives: true);
        await allowed.VerifyRootIdentityAsync();
        Require((await allowed.ListEntriesAsync()).Count == 1,
            "a capability-enabled shared drive root inventories its children");
        Console.WriteLine("PASS Google Drive ambiguity, incomplete-corpus, stale-version, create-only, trash and shared-drive safety");
    }

    private static async Task TransferAsync(string root)
    {
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        var client = Client(server, Binding());
        var payload = new byte[512 * 1024];
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index % 251);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var upload = await client.UploadAsync("nested/file.bin", new MemoryStream(payload), sha, null);
        Require(upload.ContentHash == sha, "the uploaded digest matches the intended content");
        Require(server.Get(upload.FileId).Size == payload.Length, "the uploaded size is confirmed");
        Require(server.CreatedFolders.Contains("nested"), "missing parent folders are created for the upload");

        // Download verifies the digest and rejects a mismatching body.
        var destination = new MemoryStream();
        // Real execution receives a path-bound inventory entry, not the pathless
        // object-metadata response. Keep the selected-folder membership guard active.
        var reviewed = (await client.ListEntriesAsync()).Single(item => item.Id == upload.FileId);
        var downloaded = await client.DownloadAsync(reviewed, destination);
        Require(downloaded.Size == payload.Length && destination.ToArray().SequenceEqual(payload),
            "download returns the exact reviewed bytes");
        server.CorruptDownloadFlag["blob-corrupt"] = true;
        server.AddFile("blob-corrupt", "corrupt.bin", "root-folder", payload);
        var corrupt = (await client.ListEntriesAsync()).Single(item => item.Id == "blob-corrupt");
        try
        {
            await client.DownloadAsync(corrupt, new MemoryStream());
            throw new InvalidOperationException("a mismatching download was unexpectedly accepted");
        }
        catch (IOException) { Console.WriteLine("PASS a digest-mismatching download is rejected before commit"); }

        // Range download returns only the requested window after an exact range acknowledgement.
        var rangeTarget = new MemoryStream();
        var range = await client.DownloadRangeAsync(reviewed, 1024, 4096, rangeTarget);
        Require(range.Length == 4096 && rangeTarget.Length == 4096, "range download returns the exact window");

        // T5: the server reports fewer received bytes than were sent; the client resumes from that offset.
        var resumable = new SyntheticDriveServer { ShortChunkAcknowledgement = true };
        resumable.AddFolder("root-folder", "Pair", null);
        var large = new byte[1024 * 1024];
        for (var index = 0; index < large.Length; index++) large[index] = (byte)(index % 97);
        var largeSha = Convert.ToHexString(SHA256.HashData(large)).ToLowerInvariant();
        var checkpoints = new List<SyncUploadSessionCheckpoint>();
        var resumableClient = Client(resumable, Binding(), resumableThreshold: 256 * 1024, chunkSize: 256 * 1024);
        var resumed = await resumableClient.UploadResumableAsync("big.bin", new MemoryStream(large), largeSha,
            null, null, (checkpoint, _) => { checkpoints.Add(checkpoint); return Task.CompletedTask; });
        Require(resumed.ContentHash == largeSha, "the resumed upload lands with the intended digest");
        Require(resumable.BodyFor(resumed.FileId).SequenceEqual(large),
            "the resumable upload body is byte-exact after a short acknowledgement");
        Require(checkpoints.Any(item => item.ConfirmedOffset > 0 && item.ConfirmedOffset < large.Length),
            "the client persisted the server-reported offset, not a locally assumed one");

        // T17: an unusable retained session is refused rather than silently restarted mid-effect.
        var expired = new SyntheticDriveServer { SessionGone = true };
        expired.AddFolder("root-folder", "Pair", null);
        var expiredClient = Client(expired, Binding(), resumableThreshold: 256 * 1024, chunkSize: 256 * 1024);
        try
        {
            await expiredClient.UploadResumableAsync("big.bin", new MemoryStream(large), largeSha, null,
                new SyncUploadSessionCheckpoint(
                    "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=session-gone", 0),
                (_, _) => Task.CompletedTask);
            throw new InvalidOperationException("an unusable resumable session was unexpectedly accepted");
        }
        catch (GoogleDriveEffectUncertainException)
        {
            Console.WriteLine("PASS an unusable retained resumable session fails closed for reconciliation");
        }

        // T4: rate pressure is retried with bounded backoff and then succeeds.
        var throttled = new SyntheticDriveServer { RateLimitOnce = true };
        throttled.AddFolder("root-folder", "Pair", null);
        throttled.AddFile("blob-1", "notes.txt", "root-folder", Encoding.UTF8.GetBytes("v1"));
        var throttledClient = Client(throttled, Binding());
        var listed = await throttledClient.ListEntriesAsync();
        Require(listed.Count == 1, "a rate-limited read is retried and succeeds");
        Require(throttled.RateLimitHits == 1, "the fixture observed exactly one rate-limit response");

        // T3: a revoked account surfaces as a reauthorisation requirement.
        var revoked = new SyntheticDriveServer { Unauthorized = true };
        revoked.AddFolder("root-folder", "Pair", null);
        try
        {
            await Client(revoked, Binding()).ListEntriesAsync();
            throw new InvalidOperationException("a revoked account was unexpectedly accepted");
        }
        catch (GoogleDriveApiException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
        {
            Require(error.SafeReason.Contains("reconnect", StringComparison.OrdinalIgnoreCase),
                "the revoked-account reason asks for reconnection");
            Console.WriteLine("PASS a revoked Google account maps to a reauthorisation requirement");
        }
        Console.WriteLine("PASS Google Drive upload, download, range, resume, expiry, rate-limit and revocation behaviour");
    }

    private static async Task ChangeFeedAsync(string root)
    {
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        server.AddFile("blob-1", "notes.txt", "root-folder", Encoding.UTF8.GetBytes("v1"));
        var client = Client(server, Binding());
        await client.ListEntriesAsync();

        // The documented bootstrap is GET changes/startPageToken. The fixture asserts the method.
        var cursor = await client.GetLatestCursorAsync();
        Require(server.StartPageTokenMethod == HttpMethod.Get,
            "the change cursor is bootstrapped with the documented GET method");
        Require(cursor.CorpusKind == "user", "a My Drive cursor is bound to the user corpus");
        Require(cursor.CorpusId == "perm-account-one", "the cursor records the account corpus identity");

        server.RecordChange("blob-1", removed: false);
        server.RecordChange("blob-gone", removed: true);
        var page = await client.ContinueChangesAsync(cursor.PageToken);
        Require(page.Entries.Count == 2, "both the modification and the removal are reported");
        Require(page.Entries.Any(item => item.EventKind == "google-drive-removed"), "a removal is a distinct event kind");
        var removedEvidence = page.Entries.Single(item => item.EventKind == "google-drive-removed").EvidenceJson;
        Require(removedEvidence.Contains("removed", StringComparison.Ordinal),
            "the removal evidence records that the change feed did not say why the item went away");

        // T6: a removal event is verified by identity, and the verification result is a typed
        // notFound that states both possibilities instead of claiming a deletion.
        var removalClient = Client(server, Binding());
        try
        {
            await removalClient.GetFileAsync("blob-gone");
            throw new InvalidOperationException("a missing item was unexpectedly readable");
        }
        catch (GoogleDriveApiException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            Require(error.SafeReason.Contains("does not exist", StringComparison.Ordinal) &&
                error.SafeReason.Contains("cannot read", StringComparison.Ordinal),
                "notFound states both possibilities instead of claiming deletion");
            Console.WriteLine("PASS a removal event is verified and notFound never becomes deletion authority");
        }

        // T2: an invalid page token fails as a typed provider error and never advances the token.
        var invalid = new SyntheticDriveServer { InvalidPageToken = true };
        invalid.AddFolder("root-folder", "Pair", null);
        var invalidClient = Client(invalid, Binding());
        await invalidClient.ListEntriesAsync();
        try
        {
            await invalidClient.ContinueChangesAsync("stale-token");
            throw new InvalidOperationException("an invalid page token was unexpectedly accepted");
        }
        catch (GoogleDriveApiException error) when (error.StatusCode == HttpStatusCode.Gone)
        {
            Console.WriteLine("PASS an invalid change page token fails closed with a typed provider error");
        }

        // Cursor/account isolation: a Drive cursor identity can never be reused across accounts or corpora.
        var other = Client(server, Binding("perm-account-two"));
        var otherCursor = await other.GetLatestCursorAsync();
        Require(otherCursor.CorpusId != cursor.CorpusId, "two accounts produce different corpus identities");
        Require(otherCursor.IdentityJson != cursor.IdentityJson, "the cursor binding JSON differs per account");
        Console.WriteLine("PASS Google Drive change bootstrap, removal evidence, invalid token and cursor isolation");
    }

    private static async Task ConcurrentInventoryPublicationAsync()
    {
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        var alpha = server.AddFolder("alpha", "Alpha", "root-folder");
        server.AddFolder("beta", "Beta", "root-folder");
        server.AddFile("alpha-file", "same.txt", "alpha", [1]);
        server.AddFile("beta-file", "same.txt", "beta", [2]);
        server.RecordChange("alpha-file", false);
        server.RecordChange("beta-file", false);
        var client = Client(server, Binding());
        await client.ListEntriesAsync();
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootResponses = 0;
        server.AfterReadResponseForTest = async (kind, token) =>
        {
            if (kind == "root-list" && Interlocked.Increment(ref rootResponses) == 1)
            {
                firstReady.SetResult();
                await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            }
        };
        var first = client.ListInventoryAsync();
        await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Require(client.LastInventory!.Entries.Any(item => item.RelativePath == "Alpha/same.txt"),
                "a paused private inventory does not clear the published path snapshot");
            alpha.Name = "Gamma"; // The first root response is already an exact independent byte body.
            var second = await client.ListInventoryAsync();
            Require(second.Entries.Any(item => item.RelativePath == "Gamma/same.txt") &&
                    client.LastInventory == second, "the second complete inventory publishes atomically");
            var page = await client.ContinueChangesAsync("start-token-1");
            Require(page.Entries.Any(item => item.RelativePath == "Gamma/same.txt") &&
                    page.Entries.Any(item => item.RelativePath == "Beta/same.txt"),
                "same-basename change hints bind distinct provider IDs, not an arbitrary path");
        }
        finally { releaseFirst.TrySetResult(); }
        var original = await first;
        Require(original.Entries.Any(item => item.RelativePath == "Alpha/same.txt") &&
                original.Entries.All(item => item.RelativePath != "Gamma/same.txt") &&
                client.LastInventory == original, "each caller retains its own complete inventory, not a mixed publication");
        server.AfterReadResponseForTest = null;
        await Task.WhenAll(Enumerable.Range(0, 24).Select(async _ =>
        {
            var inventory = await client.ListInventoryAsync();
            var changes = await client.ContinueChangesAsync("start-token-1");
            Require(inventory.Entries.Count == 4 && changes.Entries.Count == 2 &&
                    changes.Entries.Any(item => item.RelativePath == "Gamma/same.txt") &&
                    changes.Entries.Any(item => item.RelativePath == "Beta/same.txt"),
                "concurrent same-client inventories and change reads expose only complete ID-bound snapshots");
        }));
        Console.WriteLine("PASS U04 transport-barrier independent snapshots and 24 concurrent inventories/change reads");
    }

    private static async Task FreshExactLookupAsync()
    {
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        for (var folder = 0; folder < 100; folder++)
        {
            server.AddFolder("folder-id-" + folder, "folder-" + folder, "root-folder");
            for (var file = 0; file < 10; file++)
                server.AddFile("file-" + folder + "-" + file, "f-" + file + ".txt", "folder-id-" + folder, [1]);
        }
        var client = Client(server, Binding());
        await client.ListEntriesAsync();
        var changed = server.Get("file-0-0");
        changed.Content = [1, 2, 3];
        changed.Version++;
        var before = server.RequestCalls;
        var entry = await client.ReadEntryAsync("FOLDER-0/f-0.txt", "file-0-0");
        Require(entry.Size == 3 && entry.Revision == JsonSerializer.Serialize(new[] { "file-0-0", "2" }) &&
                server.RequestCalls - before <= 12,
            "fresh exact lookup reads the changed object and selected ancestry, not cached metadata or 100 unrelated subtrees");
        before = server.RequestCalls;
        Require(await client.FindEntryAtPathAsync("folder-0/missing.txt") is null &&
                server.RequestCalls - before <= 12, "complete parent evidence proves exact leaf absence without traversing the root");
        Require(await client.FindEntryAtPathAsync("missing-parent/missing.txt") is null,
            "an explicitly absent intermediate component is distinct from an unreadable component");
        await RefusedAsync("wrong exact provider ID", () => client.ReadEntryAsync("folder-0/f-0.txt", "file-0-1"));
        server.Unauthorized = true;
        try
        {
            await client.FindEntryAtPathAsync("folder-0/missing.txt");
            throw new InvalidOperationException("authorization failure became path absence");
        }
        catch (GoogleDriveApiException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { }
        finally { server.Unauthorized = false; }
        server.AddFolder("case-alias", "FOLDER-0", "root-folder");
        await RefusedAsync("case-equivalent lookup ancestor", () => client.FindEntryAtPathAsync("folder-0/f-0.txt"));
        server.Get("case-alias").Trashed = true;
        server.AddFile("unicode-a", "é.txt", "folder-id-0", [1]);
        server.AddFile("unicode-b", "e\u0301.txt", "folder-id-0", [1]);
        await RefusedAsync("Unicode-equivalent destination", () => client.FindEntryAtPathAsync("folder-0/é.txt"));
        var moved = false;
        server.AfterReadResponseForTest = (kind, _) =>
        {
            if (kind == "other-list" && !moved) { changed.ParentId = "foreign-folder"; moved = true; }
            return Task.CompletedTask;
        };
        try { await RefusedAsync("lookup object moved after listing", () => client.ReadEntryAsync("folder-0/f-0.txt", changed.Id)); }
        finally { server.AfterReadResponseForTest = null; changed.ParentId = "folder-id-0"; }
        var incomplete = new SyntheticDriveServer { IncompleteSearch = true };
        incomplete.AddFolder("root-folder", "Pair", null);
        await RefusedAsync("incomplete parent cannot prove absence", () => Client(incomplete, Binding()).FindEntryAtPathAsync("missing.txt"));
        Require(server.WriteCalls == 0 && incomplete.WriteCalls == 0, "all exact-item and rejected absence checks are read-only");
        Console.WriteLine("PASS U05 fresh bounded lookup, no unrelated subtree traversal, drift/ID/auth/ambiguity/incomplete refusals");

        static async Task RefusedAsync(string label, Func<Task> action)
        {
            var rejected = false;
            try { await action(); }
            catch (Exception error) when (error is IOException or InvalidDataException) { rejected = true; }
            Require(rejected, label + " must be rejected, not treated as a usable exact item or absence");
        }
    }

    private static async Task AccountIsolationAsync(string root)
    {
        await AccountIsolationBodyAsync(root);
    }

    private static async Task AccountPersistenceAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Account persistence checks require Windows state authority.");
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "accounts", "state", "instance.lock"));
        var first = Binding("persisted-one");
        var second = Binding("persisted-two");
        var store = new GoogleDriveConnectionStore(appLock.StateAuthority);
        await store.SaveAsync(first);
        await store.SaveAsync(second);
        var reopened = new GoogleDriveConnectionStore(appLock.StateAuthority);
        Require(reopened.LoadActiveBindings().Count == 2 &&
                reopened.Load(first.ConnectionId)?.PermissionId == first.PermissionId &&
                reopened.Load(second.ConnectionId)?.PermissionId == second.PermissionId,
            "both persisted accounts are readable through exact account selection after reopening");
        await reopened.SaveAsync(second with { DisplayName = "Updated second account" });
        Require(reopened.Load(first.ConnectionId)?.DisplayName == first.DisplayName &&
                reopened.Load(second.ConnectionId)?.DisplayName == "Updated second account",
            "updating the additional account preserves the primary account");
        await reopened.SaveAsync(first with { DisplayName = "Updated primary account" });
        Require(reopened.LoadActiveBindings().Count == 2 &&
                reopened.Load(second.ConnectionId)?.DisplayName == "Updated second account",
            "updating the primary account preserves the additional account");
        var before = JsonSerializer.Serialize(reopened.Load());
        foreach (var invalid in new[]
        {
            second with { AccountKey = first.AccountKey, ConnectionId = first.ConnectionId },
            second with { ClientId = "other-client" },
            first with { AdditionalConnections = [first] },
            first with { AdditionalConnections = [second with { AdditionalConnections = [Binding("nested")] }] }
        })
        {
            try
            {
                await reopened.SaveAsync(invalid);
                throw new InvalidOperationException("an invalid account binding was persisted");
            }
            catch (InvalidDataException) { }
            Require(JsonSerializer.Serialize(reopened.Load()) == before,
                "invalid account, client, duplicate or nested bindings leave the complete store unchanged");
        }
        await reopened.RemoveAdditionalAsync(second.ConnectionId);
        Require(reopened.LoadActiveBindings().Count == 1 && reopened.Load(second.ConnectionId) is null &&
                reopened.Load(first.ConnectionId)?.PermissionId == first.PermissionId,
            "removing the additional account preserves the primary account");
        Console.WriteLine("PASS two persisted Google accounts reopen, update and remove independently; invalid bindings preserve state");
    }

    // The provider-aware content-identity seam: Dropbox identity bytes are unchanged, a Drive identity
    // carries its algorithm, and two different algorithms are never treated as equal.
    private static async Task DigestSeamAsync()
    {
        Require(SyncContentIdentity.AlgorithmFor("dropbox") == SyncContentIdentity.DropboxBlockSha256 &&
            SyncContentIdentity.AlgorithmFor("google-drive") == SyncContentIdentity.Sha256,
            "each provider selects its own content-identity algorithm");
        Require(SyncContentIdentity.IdentityStampFor("dropbox") is null &&
            SyncContentIdentity.IdentityStampFor("google-drive") == SyncContentIdentity.Sha256,
            "only the non-Dropbox provider stamps the algorithm into persisted identity");
        var lastWrite = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc);
        var dropboxIdentity = SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File, 0x1234,
            0x5678, lastWrite);
        var dropboxIdentityAgain = SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File, 0x1234,
            0x5678, lastWrite, null, null, null);
        Require(string.Equals(dropboxIdentity, dropboxIdentityAgain, StringComparison.Ordinal),
            "Dropbox local identity bytes are unchanged by the seam");
        var driveIdentity = SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File, 0x1234,
            0x5678, lastWrite, null, null, SyncContentIdentity.IdentityStampFor("google-drive"));
        Require(driveIdentity.Contains("contentAlgorithm", StringComparison.Ordinal) &&
            !string.Equals(driveIdentity, dropboxIdentity, StringComparison.Ordinal),
            "a Drive identity binds the algorithm it was produced with");

        var small = Encoding.UTF8.GetBytes("small content");
        var dropboxSmall = await SyncContentIdentity.ComputeAsync("dropbox", new MemoryStream(small));
        var driveSmall = await SyncContentIdentity.ComputeAsync("google-drive", new MemoryStream(small));
        Require(dropboxSmall == DropboxContentHash.Compute(small),
            "the Dropbox path still uses the 4 MiB block hash");
        Require(driveSmall == Convert.ToHexString(SHA256.HashData(small)).ToLowerInvariant(),
            "the Drive path uses whole-file SHA-256");

        var multi = new byte[(4 * 1024 * 1024) + 12345];
        for (var index = 0; index < multi.Length; index++) multi[index] = (byte)(index % 251);
        var dropboxMulti = await SyncContentIdentity.ComputeAsync("dropbox", new MemoryStream(multi));
        var driveMulti = await SyncContentIdentity.ComputeAsync("google-drive", new MemoryStream(multi));
        Require(driveMulti == Convert.ToHexString(SHA256.HashData(multi)).ToLowerInvariant(),
            "a multi-block file hashes as whole-file SHA-256 for Drive");
        Require(!string.Equals(dropboxMulti, driveMulti, StringComparison.Ordinal),
            "multi-block bytes produce different identities per algorithm");

        var empty = Array.Empty<byte>();
        var dropboxEmpty = await SyncContentIdentity.ComputeAsync("dropbox", new MemoryStream(empty));
        var driveEmpty = await SyncContentIdentity.ComputeAsync("google-drive", new MemoryStream(empty));
        Require(string.Equals(dropboxEmpty, driveEmpty, StringComparison.Ordinal),
            "an empty file coincidentally hashes identically in both schemes, which is why the algorithm " +
            "must travel with the hash");
        Require(!SyncContentIdentity.IdentitiesMatch(SyncContentIdentity.DropboxBlockSha256, dropboxEmpty,
                SyncContentIdentity.Sha256, driveEmpty),
            "identical digest strings from different algorithms are never treated as equal");
        Require(!SyncContentIdentity.IdentitiesMatch(null, driveSmall, SyncContentIdentity.Sha256, driveSmall),
            "a missing algorithm is unknown, never equality");
        Require(SyncContentIdentity.IdentitiesMatch(SyncContentIdentity.Sha256, driveSmall,
                SyncContentIdentity.Sha256, driveSmall),
            "the same algorithm with the same digest still matches");
        try
        {
            SyncContentIdentity.RequireSameAlgorithm("dropbox", "google-drive", "cross-pair move");
            throw new InvalidOperationException("a cross-provider identity comparison was unexpectedly allowed");
        }
        catch (GoogleDriveBlockedException)
        {
            Console.WriteLine("PASS provider-aware content identity: Dropbox bytes preserved, Drive stamped, " +
                "cross-algorithm never equal");
        }

        // Persisted-identity binding: a retained identity from the other provider is refused, a legacy
        // Dropbox identity stays valid for a Dropbox pair, and a Drive identity is valid for a Drive pair.
        var driveStamped = SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File, 0x1234, 0x5678,
            lastWrite, null, null, SyncContentIdentity.IdentityStampFor("google-drive"));
        var legacyDropbox = SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File, 0x1234, 0x5678,
            lastWrite);
        SyncAuthorityLocalVersionIdentity.RequireIdentityAlgorithm(legacyDropbox, "dropbox");
        SyncAuthorityLocalVersionIdentity.RequireIdentityAlgorithm(driveStamped, "google-drive");
        try
        {
            SyncAuthorityLocalVersionIdentity.RequireIdentityAlgorithm(legacyDropbox, "google-drive");
            throw new InvalidOperationException("a Dropbox-stamped retained identity was accepted for Drive");
        }
        catch (GoogleDriveBlockedException)
        {
            Console.WriteLine("PASS a retained identity authored with another provider is refused");
        }
        try
        {
            SyncAuthorityLocalVersionIdentity.RequireIdentityAlgorithm(driveStamped, "dropbox");
            throw new InvalidOperationException("a Drive-stamped retained identity was accepted for Dropbox");
        }
        catch (GoogleDriveBlockedException)
        {
            Console.WriteLine("PASS a retained Drive identity is refused for a Dropbox pair");
        }
    }

    private static async Task AccountIsolationBodyAsync(string root)


    {
        await Task.Yield();
        var first = Binding("perm-account-one");
        var second = Binding("perm-account-two");
        Require(first.ConnectionId != second.ConnectionId, "two Google accounts have distinct connection ids");
        Require(first.AccountKey != second.AccountKey, "two Google accounts have distinct account keys");
        Require(DropboxConnectionBinding.CreateConnectionId("account-one") != first.ConnectionId,
            "Dropbox and Google connection identities never collide");
        Require(SyncProviders.Normalize(null) == SyncProviders.Dropbox &&
            SyncProviders.Normalize("Google-Drive") == SyncProviders.GoogleDrive,
            "provider names normalize without changing the Dropbox literal");
        Require(SyncProviders.IsKnown("dropbox") && SyncProviders.IsKnown("google-drive") &&
            !SyncProviders.IsKnown("some-other"), "only registered providers are accepted as active");

        // Retained provider records keep the capability profile honest for each provider.
        var googleRecord = first.ToProviderRecord();
        Require(googleRecord.Provider == "google-drive" && googleRecord.Capabilities.StableObjectIds &&
            !googleRecord.Capabilities.FileRequestsWrite && googleRecord.Capabilities.ConditionalWrites &&
            googleRecord.Capabilities.SharingRead && googleRecord.Capabilities.SharingWrite &&
            googleRecord.Capabilities.Revisions && googleRecord.Capabilities.DeletedItems &&
            googleRecord.Capabilities.NativeDocuments && googleRecord.Capabilities.Shortcuts,
            "the Google provider record exposes honest capabilities");
        var dropboxRecord = new ProviderCapabilityFlags(true, true, true, true, true, true, true, true, true,
            true, true, false, true, true);
        Require(dropboxRecord.ConditionalWrites && !dropboxRecord.NativeDocuments && !dropboxRecord.SharedDrives,
            "the Dropbox capability profile is unchanged by the new provider fields");

        // Cursor records must not be interchangeable between corpora.
        var userCursor = new GoogleDriveChangeCursor("user", "perm-account-one", "token-one");
        var driveCursor = new GoogleDriveChangeCursor("drive", "shared-drive-1", "token-two");
        Require(userCursor.IdentityJson != driveCursor.IdentityJson && userCursor.PageToken != driveCursor.PageToken,
            "user and shared-drive cursors are distinguishable");
        Console.WriteLine("PASS multi-account and multi-provider identity isolation");
    }

    private static async Task PlannerIntegrationAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The real pair-runner integration check requires Windows.");
        var local = Path.Combine(root, "planner-local");
        Directory.CreateDirectory(local);
        await File.WriteAllTextAsync(Path.Combine(local, "upload.txt"), "planned by the real engine");

        var server = new SyntheticDriveServer();
        // The retained pair record validates its provider root identity with the existing folder-id
        // shape, so the Drive root id uses that same opaque form.
        server.AddFolder("id:root-folder", "Pair", null);
        server.AddFolder("id:recovery-folder", "Recovery", null);
        // U05: unrelated complete subtrees are identical at both endpoints. They
        // belong in the planning inventory, never in every file's exact checks.
        for (var folder = 0; folder < 100; folder++)
        {
            var name = "unused-" + folder;
            var parentId = "id:unused-folder-" + folder;
            server.AddFolder(parentId, name, "id:root-folder");
            var localFolder = Directory.CreateDirectory(Path.Combine(local, name)).FullName;
            for (var file = 0; file < 10; file++)
            {
                var fileName = "file-" + file + ".bin";
                server.AddFile("id:unused-file-" + folder + "-" + file, fileName, parentId, [1]);
                await File.WriteAllBytesAsync(Path.Combine(localFolder, fileName), [1]);
            }
        }
        using var lease = PilotDirectoryLease.Open(local);
        var pair = new SyncPair("drive-pair-0000000000000001", "Drive pair", local,
            lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, "/Pair", "id:root-folder", ConnectionId: Binding().ConnectionId,
            Provider: SyncProviders.GoogleDrive);
        pair = pair with
        {
            ControlNamespace = new SyncControlNamespaceBinding("drive-installation", pair.Id,
                SyncControlNamespace.BindLocalForTest(lease, "drive-installation", pair.Id)),
            RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Recovery", "id:recovery-folder",
                Binding().AccountKey)
        };
        var db = new SyncAuthorityDatabase(Path.Combine(root, "drive-authority.db"));
        await db.InitializeAsync("drive-installation", "drive-profile", "drive-check", "{}");
        await db.RegisterPairAsync(pair.Id, 1);
        var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        await db.RetainPairDefinitionAsync(pair, true);
        await db.RegisterRootBindingAsync("drive-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { lease.Identity.VolumeSerialNumber, lease.Identity.FileId }), local);
        await db.RegisterRootBindingAsync("drive-remote", pair.Id, "dropbox", 1,
            JsonSerializer.Serialize(new { folderId = "id:root-folder" }), pair.DropboxRoot);

        var remote = new GoogleDrivePilotClient(new HttpClient(server), new StubTokenSource(), Binding(),
            "id:root-folder", "/Pair", capabilities: GoogleDriveCapabilities.Baseline);
        var effectCaptureStarted = false;
        var completedEffects = 0;
        KeyValuePair<string, int>[] effectLookupSnapshot = [];
        Task CaptureScheduledEffect(SyncAuthorityLiveAction action)
        {
            if (action.Stage == "Starting" && !effectCaptureStarted)
            {
                Require(remote.LastInventory!.Entries.Count >= 1100,
                    "the actual planner first consumes the complete unrelated tree");
                effectCaptureStarted = true;
                server.ParentListCalls.Clear();
            }
            if (action.Stage == "Completed")
            {
                completedEffects++;
                // Normal sync re-observes after the batch to establish convergence.
                // Measure actual scheduled effects, not that required next inventory.
                effectLookupSnapshot = server.ParentListCalls.ToArray();
            }
            return Task.CompletedTask;
        }
        void VerifyScheduledLookups(string operation, int expectedEffects)
        {
            var unrelated = effectLookupSnapshot.Where(item => item.Key.StartsWith("id:unused-folder-",
                StringComparison.Ordinal)).Sum(item => item.Value);
            var calls = effectLookupSnapshot.Sum(item => item.Value);
            var detail = "captureStarted=" + effectCaptureStarted + "; completed=" + completedEffects +
                "/" + expectedEffects + "; parent list calls=" + calls + "; unrelated subtree calls=" + unrelated +
                "; parents=" + string.Join(",", effectLookupSnapshot.OrderBy(item => item.Key)
                    .Select(item => item.Key + ":" + item.Value));
            Console.WriteLine("U05 scheduled " + operation + ": " + detail);
            Require(effectCaptureStarted && completedEffects == expectedEffects && unrelated == 0 && calls <= 50,
                "scheduled " + operation + " uses fresh selected-parent evidence, not unrelated subtree listings; " + detail);
            Console.WriteLine("PASS U05 scheduled " + operation + "; complete planning entries>=1100");
        }
        var observer = new SyncAuthorityObservationCoordinator(pair, remote, db, "drive-local", "drive-remote");
        await observer.ObserveAsync("initial");
        await observer.ObserveAsync("initial");
        await new SyncAuthorityInitialPlanner(db).AdoptAsync(pair.Id);
        var options = new SyncAuthorityPlannerOptions(1, "drive-profile", "drive-control", "drive-control",
            EnableCreateEffects: true, EnableMoveEffects: true,
            RemoteUploadSessionPolicy: SyncUploadLimits.ForStore(remote));
        var plan = await new SyncAuthorityReconciliationPlanner(db, options, admission: admission).PlanAsync(pair.Id);
        Require(plan.Any(item => item.RelativePath == "upload.txt"),
            "the real planner observes the local file against the live Drive inventory");
        await db.SetPairStateAsync(pair.Id, "active");
        // With the provider-aware content identity seam in place, the same journey now completes: the
        // local scanner hashes with the Drive algorithm, the reviewed identity matches, and the real
        // runner uploads through the Drive adapter.
        await db.SetPairStateAsync(pair.Id, "active");
        var created = await new SyncAuthorityPairRunner(pair, remote, db, "drive-local", "drive-remote",
            options, new PilotRunControl(), onExecution: CaptureScheduledEffect, admission: admission).RunAsync("manual");
        VerifyScheduledLookups("create", 1);
        Require(created.Decisions.Any(item => item.RelativePath == "upload.txt" &&
            item.Kind == SyncDecisionKind.Uploaded),
            "the real runner uploads the new local file through the Drive adapter");
        Require(server.Names("id:root-folder").Contains("upload.txt"),
            "the controlled Google Drive endpoint holds the uploaded file");
        Require(server.BodyFor(server.IdByName("id:root-folder", "upload.txt"))
                .SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(local, "upload.txt"))),
            "the uploaded bytes are exact");
        Console.WriteLine("PASS real observation, planner, executor and runner integration uploads to Drive");

        // An unchanged second scan plans no new remote effect and writes nothing.
        var writesAfterCreate = server.WriteCalls;
        await new SyncAuthorityObservationCoordinator(pair, remote, db, "drive-local", "drive-remote")
            .ObserveAsync("manual");
        var secondPlan = await new SyncAuthorityReconciliationPlanner(db, options, admission: admission).PlanAsync(pair.Id);
        Require(secondPlan.All(item => item.OperationId is null),
            "an unchanged second scan produces no queued operation");
        await new SyncAuthorityPairRunner(pair, remote, db, "drive-local", "drive-remote", options,
            new PilotRunControl(), admission: admission).RunAsync("manual");
        Require(server.WriteCalls == writesAfterCreate,
            "an unchanged second scan performs no remote write");
        Console.WriteLine("PASS an unchanged second Drive scan is stable and effect-free");

        // Remote-to-local download through the same real runner.
        var remoteBytes = Encoding.UTF8.GetBytes("created in the cloud");
        server.AddFile("remote-file-1", "remote.txt", "id:root-folder", remoteBytes);
        server.AddFile("remote-file-2", "remote-two.txt", "id:root-folder", remoteBytes);
        await new SyncAuthorityObservationCoordinator(pair, remote, db, "drive-local", "drive-remote")
            .ObserveAsync("manual");
        effectCaptureStarted = false;
        completedEffects = 0;
        effectLookupSnapshot = [];
        var downloaded = await new SyncAuthorityPairRunner(pair, remote, db, "drive-local", "drive-remote", options,
            new PilotRunControl(), onExecution: CaptureScheduledEffect, admission: admission).RunAsync("manual");
        VerifyScheduledLookups("two downloads", 2);
        var localDownload = Path.Combine(local, "remote.txt");
        Require(File.Exists(localDownload), "the real runner downloads the new remote file");
        Require((await File.ReadAllBytesAsync(localDownload)).SequenceEqual(remoteBytes),
            "the downloaded bytes are exact");
        Require(downloaded.Decisions.Any(item => item.RelativePath == "remote.txt" && item.Kind == SyncDecisionKind.Downloaded) &&
                downloaded.Decisions.Any(item => item.RelativePath == "remote-two.txt" && item.Kind == SyncDecisionKind.Downloaded) &&
                (await File.ReadAllBytesAsync(Path.Combine(local, "remote-two.txt"))).SequenceEqual(remoteBytes),
            "two independent files complete through the real operation scheduler and shared Google adapter");
        Console.WriteLine("PASS real runner downloads a Drive file to the local pair");

        var changedBytes = Encoding.UTF8.GetBytes("updated locally through the real runner");
        await File.WriteAllBytesAsync(Path.Combine(local, "upload.txt"), changedBytes);
        await new SyncAuthorityObservationCoordinator(pair, remote, db, "drive-local", "drive-remote").ObserveAsync("manual");
        effectCaptureStarted = false;
        completedEffects = 0;
        effectLookupSnapshot = [];
        var changed = await new SyncAuthorityPairRunner(pair, remote, db, "drive-local", "drive-remote", options,
            new PilotRunControl(), onExecution: CaptureScheduledEffect, admission: admission).RunAsync("manual");
        VerifyScheduledLookups("guarded replacement", 1);
        Require(changed.Decisions.Any(item => item.RelativePath == "upload.txt" && item.Kind == SyncDecisionKind.Uploaded),
            "the real runner performs a revision-bound replacement");
        Require(server.BodyFor(server.IdByName("id:root-folder", "upload.txt")).SequenceEqual(changedBytes),
            "runner replacement exact bytes");
        Require(server.Names("id:recovery-folder").Count > 0, "runner preserved the replaced remote file in Recovery");
        Console.WriteLine("PASS real runner guarded replacement retains Recovery and updates exact bytes");

        // U03-C: actual source length exceeds the former Dropbox-based branch.
        // One bounded NTFS file is streamed; no giant source array or fake checkpoint.
        // The real planner and replacement executor must retain the predecessor,
        // issue one guarded request, record success and finish without a session.
        var largeSize = SyncUploadLimits.DropboxSingleRequestBytes + 1;
        await using (var largeSource = new FileStream(Path.Combine(local, "upload.txt"), FileMode.Create,
                         FileAccess.ReadWrite, FileShare.Read))
        {
            largeSource.SetLength(largeSize);
            largeSource.Position = largeSize - 1;
            largeSource.WriteByte(42);
            await largeSource.FlushAsync();
        }
        string largeHash;
        await using (var largeSource = File.OpenRead(Path.Combine(local, "upload.txt")))
            largeHash = Convert.ToHexString(await SHA256.HashDataAsync(largeSource)).ToLowerInvariant();
        await new SyncAuthorityObservationCoordinator(pair, remote, db, "drive-local", "drive-remote")
            .ObserveAsync("manual");
        var largePlan = await new SyncAuthorityReconciliationPlanner(db, options, admission: admission).PlanAsync(pair.Id);
        var largeOperationId = largePlan.Single(item => item.RelativePath == "upload.txt" &&
            item.Kind == SyncAuthorityPlannerOutcomeKind.Operation).OperationId!;
        var beforeLarge = await db.LoadOperationAsync(largeOperationId);
        Require(beforeLarge.Kind == SyncAuthorityOperationKind.UpdateRemote &&
                beforeLarge.Allocations.All(item => item.Kind != SyncAuthorityAllocationKind.UploadSession),
            "U03-C real large Google replacement plan does not reserve a fictitious session");
        var largeResult = await new SyncAuthorityUpdateRemoteExecutor(pair, remote, db)
            .ExecuteAsync(largeOperationId, "google-large-replacement");
        var afterLarge = await db.LoadOperationAsync(largeOperationId);
        var largeReceipt = await db.LoadLatestEffectReceiptAsync(largeOperationId,
            afterLarge.Attempts.OrderBy(item => item.AttemptNumber).Last().AttemptId);
        var largeBody = server.BodyFor(server.IdByName("id:root-folder", "upload.txt"));
        Require(largeResult.Status == SyncAuthorityOperationStatus.Completed &&
                afterLarge.Status == SyncAuthorityOperationStatus.Completed &&
                largeReceipt?.Certainty == "known_success" &&
                largeBody.LongLength == largeSize && largeBody[^1] == 42 &&
                Convert.ToHexString(SHA256.HashData(largeBody)).ToLowerInvariant() == largeHash,
            "U03-C real streamed large replacement completes with exact bytes and known-success receipt");
        Require(server.Names("id:recovery-folder").Any(name =>
                server.BodyFor(server.IdByName("id:recovery-folder", name)).SequenceEqual(changedBytes)),
            "U03-C the exact losing predecessor remains in its bound Recovery root");
        Console.WriteLine("PASS U03-C actual 157286401-byte guarded Google replacement completes; exact predecessor retained");

        // The same content path succeeds once the reviewed identity is the Drive digest scheme.
        var payload = Encoding.UTF8.GetBytes("planned by the real engine");
        var driveDigest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var direct = Client(server, Binding(), rootFolderId: "id:root-folder");
        var uploaded = await direct.UploadAsync("direct.txt", new MemoryStream(payload), driveDigest, null);
        Require(uploaded.ContentHash == driveDigest && server.Names("id:root-folder").Contains("direct.txt"),
            "the same upload completes when the reviewed identity is the Drive whole-file digest");
        try
        {
            await direct.UploadAsync("other.txt", new MemoryStream(payload),
                "0000000000000000000000000000000000000000000000000000000000000000", null);
            throw new InvalidOperationException("a mismatching reviewed identity was unexpectedly accepted");
        }
        catch (GoogleDriveBlockedException blocked) when (
            blocked.Message.Contains("whole-file SHA-256", StringComparison.Ordinal))
        {
            Require(GoogleDrivePilotClient.ProviderDigestIntegrationGap.Contains("whole-file SHA-256",
                StringComparison.Ordinal), "the refusal names the exact shared-core digest gap");
            Console.WriteLine("PASS a mismatching reviewed identity is refused before any write");
        }
    }

    private static async Task ResumableSessionOriginAsync()
    {
        var bytes = new byte[512 * 1024];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index % 97);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string[] rejected =
        [
            "https://foreign.example/upload/drive/v3/files?upload_id=opaque",
            "http://www.googleapis.com/upload/drive/v3/files?upload_id=opaque",
            "https://www.googleapis.com.foreign.example/upload/drive/v3/files?upload_id=opaque",
            "https://userinfo@www.googleapis.com/upload/drive/v3/files?upload_id=opaque",
            "https://www.googleapis.com:8443/upload/drive/v3/files?upload_id=opaque",
            "https://www.googleapis.com/upload/drive/v3/files?upload_id=opaque#fragment",
            "https://www.googleapis.com/drive/v3/files?upload_id=opaque",
            "/upload/drive/v3/files?upload_id=opaque"
        ];
        foreach (var restored in new[] { false, true })
        foreach (var session in rejected)
        {
            var server = new SyntheticDriveServer { SessionLocationForTest = restored ? null : session };
            server.AddFolder("root-folder", "Pair", null);
            var tokens = new StubTokenSource();
            using var http = new HttpClient(server);
            var client = new GoogleDrivePilotClient(http, tokens, Binding(), "root-folder", "/Pair",
                resumableThresholdBytes: 256 * 1024, resumableChunkSizeBytes: 256 * 1024);
            var persisted = 0;
            using var source = new MemoryStream(bytes);
            try
            {
                await client.UploadResumableAsync("origin.bin", source, hash, null,
                    restored ? new SyncUploadSessionCheckpoint(session, 0) : null,
                    (_, _) => { persisted++; return Task.CompletedTask; });
                throw new InvalidOperationException("An unsupported Drive session origin was accepted.");
            }
            catch (GoogleDriveBlockedException error)
            {
                Require(server.ForeignRequestCalls == 0 && server.SessionChunkCalls == 0 &&
                        server.WriteCalls == 0 && persisted == 0,
                    "U01 rejects the unsupported session before transport, file content or checkpoint persistence");
                Require(!error.Message.Contains(session, StringComparison.Ordinal) &&
                        !error.Message.Contains("synthetic-access-token", StringComparison.Ordinal) &&
                        !error.Message.Contains("upload_id=", StringComparison.Ordinal),
                    "U01 rejection does not disclose the session, query or credential");
                if (restored) Require(tokens.Calls == 0 && server.RequestCalls == 0 && source.Position == 0,
                    "U01 restored-session rejection precedes token acquisition and every provider read");
            }
        }

        // Interrupt after a real acknowledged prefix, then resume the exact
        // canonical session through the public adapter and verify all bytes.
        var positive = new SyntheticDriveServer();
        positive.AddFolder("root-folder", "Pair", null);
        var positiveClient = Client(positive, Binding(), resumableThreshold: 256 * 1024, chunkSize: 256 * 1024);
        SyncUploadSessionCheckpoint? retained = null;
        try
        {
            using var initial = new MemoryStream(bytes);
            await positiveClient.UploadResumableAsync("origin.bin", initial, hash, null, null,
                (checkpoint, _) =>
                {
                    retained = checkpoint;
                    if (checkpoint.ConfirmedOffset > 0)
                        throw new OperationCanceledException("Synthetic interruption after acknowledged prefix.");
                    return Task.CompletedTask;
                });
            throw new InvalidOperationException("The retained-session input was not interrupted.");
        }
        catch (OperationCanceledException) { }
        Require(retained is { ConfirmedOffset: > 0 } && positive.WriteCalls == 0,
            "U01 canonical session retains an actual acknowledged prefix before any complete file");
        using var resume = new MemoryStream(bytes);
        var uploaded = await positiveClient.UploadResumableAsync("origin.bin", resume, hash, null, retained,
            (_, _) => Task.CompletedTask);
        Require(positive.WriteCalls == 1 && positive.BodyFor(uploaded.FileId).SequenceEqual(bytes) &&
                uploaded.ContentHash == hash && positive.ForeignRequestCalls == 0,
            "U01 canonical retained session resumes to exact verified bytes with one file write");
    }

    private static async Task ProductionTransportRedirectAsync(string root)
    {
        await using var cli = SyncCliRuntime.Acquire(Path.Combine(root, "transport-state", "instance.lock"));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var http = (HttpClient)typeof(SyncCliRuntime).GetField("_httpClient", flags)!.GetValue(cli)!;
        var handler = (HttpClientHandler)typeof(HttpMessageInvoker).GetField("_handler", flags)!.GetValue(http)!;
        Require(!handler.AllowAutoRedirect, "U01 actual standalone CLI transport disables automatic redirects");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var url = "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var serve = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
                await using var stream = connection.GetStream();
                var header = new StringBuilder();
                var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (header.Length >= 8192 || await stream.ReadAsync(one, deadline.Token) != 1)
                        throw new IOException("The bounded redirect input did not receive its complete request.");
                    header.Append((char)one[0]);
                }
                var reply = Encoding.ASCII.GetBytes("HTTP/1.1 307 Temporary Redirect\r\nLocation: " +
                    url + "/unexpected-target\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(reply, deadline.Token);
                await stream.FlushAsync(deadline.Token);
            }, deadline.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, url + "/initial")
                { Content = new StringContent("nonsecret isolated redirect input") };
            using var response = await http.SendAsync(request, deadline.Token);
            await serve;
            Require(response.StatusCode == HttpStatusCode.TemporaryRedirect && !listener.Pending(),
                "U01 real native CLI content transport returns307 without following the Location");
        }
        finally { listener.Stop(); }
    }

    private sealed class StubTokenSource : IGoogleDriveAccessTokenSource
    {
        internal int Calls { get; private set; }
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult("synthetic-access-token");
        }
    }

    // U07/U08: the acknowledgement Drive returns is the only authoritative resumable offset, and a
    // session that stops advancing must fail closed instead of resending content forever.
    private static async Task ResumableAcknowledgementGuardAsync()
    {
        var payload = new byte[1024 * 1024];
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index % 149);
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        await EmptyAcknowledgementRestartsFromZeroAsync(payload, hash);
        await RegressingAcknowledgementUsesTheServerOffsetAsync(payload, hash);
        await ShortAcknowledgementResendsFromTheServerOffsetAsync(payload, hash);
        await RefusedAcknowledgementsSendNoFurtherContentAsync(payload, hash);
        await DuplicateAcknowledgementIsRefusedAsync(payload, hash);
        await WholeSourceAcknowledgementStillNeedsACompletionAsync(payload, hash);
        await RepeatedAcknowledgementsStopAtTheAllowanceAsync(payload, hash);
        await OscillatingAcknowledgementsStopAtTheAllowanceAsync(payload, hash);
        await IncreasingAcknowledgementsBeyondTheAllowanceSucceedAsync();
        await CreatedCompletionSendsNoFurtherContentAsync(payload, hash);
        Console.WriteLine("PASS U07/U08 resumable acknowledgement authority, refusal and no-progress guard");
    }

    // The retained checkpoint claims 4096 bytes, but the server acknowledges nothing at all: the
    // absent header is the authoritative "zero received" signal, so the next chunk starts at zero.
    private static async Task EmptyAcknowledgementRestartsFromZeroAsync(byte[] payload, string hash)
    {
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        var handler = new SessionAcknowledgingHandler(server) { RemoveStatusRangeHeader = true };
        var client = HandlerClient(handler);
        var session = await OpenSessionWithoutContentAsync(client, "empty-ack.bin", payload, hash);
        handler.ResetObservations();
        var notices = new List<SyncUploadSessionCheckpoint>();
        var result = await client.UploadResumableAsync("empty-ack.bin", new MemoryStream(payload), hash, null,
            new SyncUploadSessionCheckpoint(session, 4096), (checkpoint, _) =>
            {
                notices.Add(checkpoint);
                return Task.CompletedTask;
            });
        Require(handler.StatusContentRanges.Count == 1 && handler.ChunkContentRanges.Count == 4 &&
                handler.ChunkContentRanges[0] == "bytes 0-262143/1048576",
            "U07 an absent acknowledgement restarts the content from zero instead of the retained 4096 offset");
        Require(notices.Count > 0 && notices[0].ConfirmedOffset == 0,
            "U07 the empty acknowledgement is persisted as offset zero before any content is sent");
        Require(result.ContentHash == hash && result.Size == payload.Length &&
                server.BodyFor(result.FileId).SequenceEqual(payload),
            "U07 the restarted session still lands byte- and hash-exact with one file write");
    }

    // The retained claim is one chunk ahead of what the server actually holds, so the server's lower
    // acknowledgement is a regression and must drive the resent offset.
    private static async Task RegressingAcknowledgementUsesTheServerOffsetAsync(byte[] payload, string hash)
    {
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        var handler = new SessionAcknowledgingHandler(server);
        var client = HandlerClient(handler);
        var retained = await InterruptAfterFirstChunkAsync(client, "regress.bin", payload, hash);
        handler.ResetObservations();
        var notices = new List<SyncUploadSessionCheckpoint>();
        var result = await client.UploadResumableAsync("regress.bin", new MemoryStream(payload), hash, null,
            new SyncUploadSessionCheckpoint(retained.SessionId, retained.ConfirmedOffset + 256 * 1024),
            (checkpoint, _) =>
            {
                notices.Add(checkpoint);
                return Task.CompletedTask;
            });
        Require(handler.StatusContentRanges.Count == 1 &&
                handler.StatusContentRanges[0] == "bytes */1048576" &&
                handler.ChunkContentRanges.Count > 0 &&
                handler.ChunkContentRanges[0] == "bytes 262144-524287/1048576",
            "U07 a regressing acknowledgement resends from the server offset, not from the retained claim");
        Require(notices.Count > 0 && notices[0].ConfirmedOffset == retained.ConfirmedOffset,
            "U07 the authoritative lower offset is persisted before that content is resent");
        Require(result.ContentHash == hash && server.BodyFor(result.FileId).SequenceEqual(payload),
            "U07 the resend from the server offset still completes byte- and hash-exact");
    }

    private static async Task ShortAcknowledgementResendsFromTheServerOffsetAsync(byte[] payload, string hash)
    {
        var server = new SyntheticDriveServer { ShortChunkAcknowledgement = true };
        server.AddFolder("root-folder", "Pair", null);
        var handler = new SessionAcknowledgingHandler(server);
        var client = HandlerClient(handler);
        var notices = new List<SyncUploadSessionCheckpoint>();
        var result = await client.UploadResumableAsync("short.bin", new MemoryStream(payload), hash, null, null,
            (checkpoint, _) =>
            {
                notices.Add(checkpoint);
                return Task.CompletedTask;
            });
        Require(handler.ChunkContentRanges.Count > 1 &&
                handler.ChunkContentRanges[0] == "bytes 0-262143/1048576" &&
                handler.ChunkContentRanges[1] == "bytes 131072-393215/1048576",
            "U08 a short acknowledgement resends from the acknowledged offset, not from the sent end");
        Require(notices.Any(item => item.ConfirmedOffset == 131072) &&
                result.ContentHash == hash && server.BodyFor(result.FileId).SequenceEqual(payload),
            "U08 the short acknowledgement is retained and the upload still lands byte-exact");
    }

    // Every refused form must stop the upload before another chunk body, keep the published file
    // absent and leave the retained offset at its last proven value.
    private static async Task RefusedAcknowledgementsSendNoFurtherContentAsync(byte[] payload, string hash)
    {
        string[] refused =
        [
            " ",
            "bytes=",
            "bytes=0",
            "bytes=0-abc",
            "bytes=0- 5",
            "bytes=-5",
            "bytes=4096-8191",
            "bytes=0-1023, 2048-3071",
            "bytes=0-5-9",
            "bytes=0-99999999999999999999",
            "bytes=0-1048576",
            "items=0-5"
        ];
        foreach (var forced in refused)
        {
            var server = new SyntheticDriveServer();
            server.AddFolder("root-folder", "Pair", null);
            var handler = new SessionAcknowledgingHandler(server)
            {
                ForcedChunkRangeByCall = call => call == 1 ? forced : null
            };
            var client = HandlerClient(handler);
            var session = await OpenSessionWithoutContentAsync(client, "refused.bin", payload, hash);
            handler.ResetObservations();
            var notices = new List<SyncUploadSessionCheckpoint>();
            try
            {
                await client.UploadResumableAsync("refused.bin", new MemoryStream(payload), hash, null,
                    new SyncUploadSessionCheckpoint(session, 0), (checkpoint, _) =>
                    {
                        notices.Add(checkpoint);
                        return Task.CompletedTask;
                    });
                throw new InvalidOperationException("the acknowledgement '" + forced + "' was unexpectedly accepted");
            }
            catch (GoogleDriveEffectUncertainException error)
            {
                Require(handler.ForcedInjections == 1 && handler.ChunkBodyCalls == 1 && server.WriteCalls == 0 &&
                        notices.All(item => item.ConfirmedOffset == 0),
                    "U08 '" + forced + "' refuses before another content send, writes nothing and persists no offset");
                Require(!error.Message.Contains(session, StringComparison.Ordinal) &&
                        !error.Message.Contains("upload_id=", StringComparison.Ordinal),
                    "U08 the refusal for '" + forced + "' does not disclose the session or its query");
            }
        }
    }

    private static async Task DuplicateAcknowledgementIsRefusedAsync(byte[] payload, string hash)
    {
        // Two Range header values cannot be reconciled into one authoritative offset.
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        var handler = new SessionAcknowledgingHandler(server) { DuplicateChunkRangeHeader = true };
        var client = HandlerClient(handler);
        var session = await OpenSessionWithoutContentAsync(client, "duplicate.bin", payload, hash);
        handler.ResetObservations();
        try
        {
            await client.UploadResumableAsync("duplicate.bin", new MemoryStream(payload), hash, null,
                new SyncUploadSessionCheckpoint(session, 0), (_, _) => Task.CompletedTask);
            throw new InvalidOperationException("a duplicated range acknowledgement was unexpectedly accepted");
        }
        catch (GoogleDriveEffectUncertainException)
        {
            Require(handler.ChunkBodyCalls == 1 && server.WriteCalls == 0,
                "U08 a duplicated range acknowledgement refuses before another content send");
        }
    }

    // A last byte equal to the source end is a valid acknowledgement, but a 308 session that claims
    // the whole source still has no completion, so it must fail closed without another send.
    private static async Task WholeSourceAcknowledgementStillNeedsACompletionAsync(byte[] payload, string hash)
    {
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        var handler = new SessionAcknowledgingHandler(server)
        {
            ForcedChunkRangeByCall = call => call == 1 ? "bytes=0-1048575" : null
        };
        var client = HandlerClient(handler);
        var session = await OpenSessionWithoutContentAsync(client, "boundary.bin", payload, hash);
        handler.ResetObservations();
        try
        {
            await client.UploadResumableAsync("boundary.bin", new MemoryStream(payload), hash, null,
                new SyncUploadSessionCheckpoint(session, 0), (_, _) => Task.CompletedTask);
            throw new InvalidOperationException("a whole-source acknowledgement without a completion was accepted");
        }
        catch (GoogleDriveEffectUncertainException error) when (
            error.Message.Contains("without a completion response", StringComparison.Ordinal))
        {
            Require(handler.ChunkBodyCalls == 1 && server.WriteCalls == 0,
                "U08 a whole-source acknowledgement is accepted without another content send and without a write");
        }
    }

    private static async Task RepeatedAcknowledgementsStopAtTheAllowanceAsync(byte[] payload, string hash)
    {
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        var handler = new SessionAcknowledgingHandler(server)
        {
            ForwardNoChunkContent = true,
            ForcedChunkRangeByCall = _ => "bytes=0-262143"
        };
        var client = HandlerClient(handler);
        var notices = new List<SyncUploadSessionCheckpoint>();
        try
        {
            await client.UploadResumableAsync("repeat.bin", new MemoryStream(payload), hash, null, null,
                (checkpoint, _) =>
                {
                    notices.Add(checkpoint);
                    return Task.CompletedTask;
                });
            throw new InvalidOperationException("repeated identical acknowledgements were unexpectedly accepted");
        }
        catch (GoogleDriveEffectUncertainException error)
        {
            // One advancing acknowledgement, then eight that repeat the same offset.
            Require(handler.ChunkBodyCalls == 9 && server.WriteCalls == 0 &&
                    notices.Count > 0 && notices[^1].ConfirmedOffset == 262144,
                "U08 eight repeated non-advancing acknowledgements end the upload with the last proven offset retained");
            Require(!error.Message.Contains("upload_id=", StringComparison.Ordinal),
                "U08 the no-progress refusal does not disclose the session");
        }
    }

    private static async Task OscillatingAcknowledgementsStopAtTheAllowanceAsync(byte[] payload, string hash)
    {
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        var handler = new SessionAcknowledgingHandler(server)
        {
            ForwardNoChunkContent = true,
            ForcedChunkRangeByCall = call => call % 2 == 1 ? "bytes=0-262143" : "bytes=0-131071"
        };
        var client = HandlerClient(handler);
        var notices = new List<SyncUploadSessionCheckpoint>();
        try
        {
            await client.UploadResumableAsync("oscillate.bin", new MemoryStream(payload), hash, null, null,
                (checkpoint, _) =>
                {
                    notices.Add(checkpoint);
                    return Task.CompletedTask;
                });
            throw new InvalidOperationException("oscillating acknowledgements were unexpectedly accepted");
        }
        catch (GoogleDriveEffectUncertainException)
        {
            Require(handler.ChunkBodyCalls == 9 && server.WriteCalls == 0 &&
                    notices.Count > 0 && notices[^1].ConfirmedOffset == 262144,
                "U08 eight oscillating non-advancing acknowledgements end the upload with the last proven offset retained");
        }
    }

    private static async Task IncreasingAcknowledgementsBeyondTheAllowanceSucceedAsync()
    {
        var payload = new byte[3 * 1024 * 1024];
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index % 173);
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        var handler = new SessionAcknowledgingHandler(server);
        var client = HandlerClient(handler);
        var notices = new List<SyncUploadSessionCheckpoint>();
        var result = await client.UploadResumableAsync("long.bin", new MemoryStream(payload), hash, null, null,
            (checkpoint, _) =>
            {
                notices.Add(checkpoint);
                return Task.CompletedTask;
            });
        Require(handler.ChunkContentRanges.Count == 12 &&
                notices.Count(item => item.ConfirmedOffset > 0 && item.ConfirmedOffset < payload.Length) == 11,
            "U08 eleven strictly increasing acknowledgements beyond the allowance never trigger the guard");
        Require(result.ContentHash == hash && result.Size == payload.Length &&
                server.BodyFor(result.FileId).SequenceEqual(payload),
            "U08 the long upload lands byte- and hash-exact with no chunk resent");
    }

    private static async Task CreatedCompletionSendsNoFurtherContentAsync(byte[] payload, string hash)
    {
        var server = new SyntheticDriveServer();
        server.AddFolder("root-folder", "Pair", null);
        var handler = new SessionAcknowledgingHandler(server) { CompleteWithCreated = true };
        var client = HandlerClient(handler);
        var result = await client.UploadResumableAsync("created.bin", new MemoryStream(payload), hash, null, null,
            (_, _) => Task.CompletedTask);
        Require(handler.ChunkContentRanges.Count == 4 && result.ContentHash == hash &&
                server.BodyFor(result.FileId).SequenceEqual(payload),
            "U08 a created (201) completion is accepted without resending any chunk");
    }

    // U09/U10: the recursive inventory is bounded per folder, refuses a cycle and keeps the existing
    // incompleteSearch meaning without publishing a partial inventory.
    private static async Task InventoryPageBoundingAsync()
    {
        var empty = new PagedInventoryHandler();
        empty.Script("root-folder",
            new PagedInventoryHandler.Page(PagedInventoryHandler.File("note-1", "note.txt", "root-folder"), 1),
            new PagedInventoryHandler.Page(string.Empty, 2),
            new PagedInventoryHandler.Page(PagedInventoryHandler.Folder("alpha", "Alpha", "root-folder"), null));
        empty.Script("alpha", new PagedInventoryHandler.Page(string.Empty, null));
        var progressed = await HandlerClient(empty).ListInventoryAsync();
        Require(progressed.CorpusComplete && empty.ListingCalls == 4 &&
                progressed.Entries.Any(item => item.RelativePath == "note.txt") &&
                progressed.Entries.Any(item => item.RelativePath == "Alpha"),
            "U09 a finite empty intermediate page continues the same folder's enumeration");

        var cycle = new PagedInventoryHandler();
        cycle.Script("root-folder",
            new PagedInventoryHandler.Page(PagedInventoryHandler.File("note-1", "note.txt", "root-folder"), 1),
            new PagedInventoryHandler.Page(string.Empty, 2),
            new PagedInventoryHandler.Page(string.Empty, 1));
        var cycleClient = HandlerClient(cycle);
        await ExpectBlockedAsync("a cycling page token", () => cycleClient.ListInventoryAsync());
        Require(cycleClient.LastInventory is null && cycle.ListingCalls == 3,
            "U09 a cycling page token stops the enumeration and publishes no inventory");

        var unbounded = new PagedInventoryHandler
        {
            UnscriptedPage = (_, index) => index > GoogleDrivePilotClient.MaximumInventoryPagesPerFolder + 4
                ? new PagedInventoryHandler.Page(string.Empty, null)
                : new PagedInventoryHandler.Page(string.Empty, index + 1)
        };
        unbounded.Script("root-folder", new PagedInventoryHandler.Page(string.Empty, 1));
        var unboundedClient = HandlerClient(unbounded);
        var bounded = false;
        try
        {
            await unboundedClient.ListInventoryAsync();
        }
        catch (GoogleDriveBlockedException)
        {
            bounded = true;
        }
        Require(bounded && unbounded.ListingCalls == GoogleDrivePilotClient.MaximumInventoryPagesPerFolder &&
                unboundedClient.LastInventory is null,
            "U09 the per-folder page bound is enforced without a progress callback and publishes no inventory");

        var incomplete = new PagedInventoryHandler();
        incomplete.Script("root-folder",
            new PagedInventoryHandler.Page(PagedInventoryHandler.File("note-1", "note.txt", "root-folder"),
                null, true));
        var incompleteClient = HandlerClient(incomplete);
        var partial = await incompleteClient.ListInventoryAsync();
        Require(!partial.CorpusComplete && incompleteClient.LastInventory == partial &&
                partial.Entries.Count == 1,
            "U10 an incomplete search is still published with its incompleteness retained");
        await ExpectBlockedAsync("an incomplete inventory", () => incompleteClient.ListEntriesAsync());
        Console.WriteLine("PASS U09/U10 bounded per-folder inventory paging, cycle refusal and incomplete-corpus retention");
    }

    private static GoogleDrivePilotClient HandlerClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new GoogleDrivePilotClient(http, new StubTokenSource(), Binding(), "root-folder", "/Pair",
            resumableThresholdBytes: 256 * 1024, resumableChunkSizeBytes: 256 * 1024,
            capabilities: GoogleDriveCapabilities.Baseline.WithSharedDrives(false) with
            {
                AtomicConditionalWrites = false
            });
    }

    // Creates a real session and stops before any chunk, with the fixture holding zero bytes for it.
    private static async Task<string> OpenSessionWithoutContentAsync(GoogleDrivePilotClient client, string name,
        byte[] payload, string hash)
    {
        var session = string.Empty;
        try
        {
            await client.UploadResumableAsync(name, new MemoryStream(payload), hash, null, null,
                (checkpoint, _) =>
                {
                    if (checkpoint.ConfirmedOffset == 0 && session.Length == 0) session = checkpoint.SessionId;
                    throw new OperationCanceledException("Synthetic stop once the session exists.");
                });
            throw new InvalidOperationException("the session-only upload unexpectedly completed");
        }
        catch (OperationCanceledException) { }
        Require(session.Length > 0, "U07 the session identifier was retained before any content was sent");
        return session;
    }

    private static async Task<(string SessionId, long ConfirmedOffset)> InterruptAfterFirstChunkAsync(
        GoogleDrivePilotClient client, string name, byte[] payload, string hash)
    {
        SyncUploadSessionCheckpoint? retained = null;
        try
        {
            await client.UploadResumableAsync(name, new MemoryStream(payload), hash, null, null,
                (checkpoint, _) =>
                {
                    retained = checkpoint;
                    if (checkpoint.ConfirmedOffset > 0)
                        throw new OperationCanceledException("Synthetic interruption after the first acknowledgement.");
                    return Task.CompletedTask;
                });
            throw new InvalidOperationException("the first-chunk interruption unexpectedly completed");
        }
        catch (OperationCanceledException) { }
        Require(retained is { ConfirmedOffset: > 0 },
            "U07 the first chunk was acknowledged before the interruption");
        return (retained!.SessionId, retained.ConfirmedOffset);
    }

    // Wraps the synthetic Drive endpoint so a test can control the acknowledgement Drive returns for
    // a resumable session. Only external input is replaced: the offset decision, the persistence
    // order and the no-progress guard all remain the production client's.
    private sealed class SessionAcknowledgingHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        private int _acknowledgements;
        internal int ForcedInjections;

        internal bool RemoveStatusRangeHeader { get; set; }
        internal bool ForwardNoChunkContent { get; set; }
        internal bool CompleteWithCreated { get; set; }
        internal bool DuplicateChunkRangeHeader { get; set; }
        internal Func<int, string?>? ForcedChunkRangeByCall { get; set; }
        internal int ChunkBodyCalls { get; private set; }
        internal int StatusCalls { get; private set; }
        internal List<string> ChunkContentRanges { get; } = [];
        internal List<string> StatusContentRanges { get; } = [];

        internal void ResetObservations()
        {
            _acknowledgements = 0;
            ForcedInjections = 0;
            ChunkBodyCalls = 0;
            StatusCalls = 0;
            ChunkContentRanges.Clear();
            StatusContentRanges.Clear();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var contentRange = request.Content?.Headers.TryGetValues("Content-Range", out var values) == true
                ? values.FirstOrDefault()
                : null;
            var status = contentRange is not null &&
                         contentRange.StartsWith("bytes */", StringComparison.Ordinal);
            var chunk = contentRange is not null && !status;
            if (status)
            {
                StatusCalls++;
                StatusContentRanges.Add(contentRange!);
            }
            else if (chunk)
            {
                ChunkBodyCalls++;
                ChunkContentRanges.Add(contentRange!);
            }
            var response = chunk && ForwardNoChunkContent
                ? new HttpResponseMessage((HttpStatusCode)308) { Content = new StringContent(string.Empty) }
                : await new HttpMessageInvoker(inner).SendAsync(request, cancellationToken);
            if ((int)response.StatusCode == 308)
            {
                if (chunk) _acknowledgements++;
                if (status && RemoveStatusRangeHeader) response.Headers.Remove("Range");
                var forced = chunk ? ForcedChunkRangeByCall?.Invoke(_acknowledgements) : null;
                if (forced is not null)
                {
                    ForcedInjections++;
                    response.Headers.Remove("Range");
                    response.Headers.TryAddWithoutValidation("Range", forced);
                    if (!DuplicateChunkRangeHeader)
                    {
                        var injected = response.Headers.TryGetValues("Range", out var ranges) ? ranges.Count() : 0;
                        if (injected != 1)
                            throw new InvalidOperationException("the forced chunk acknowledgement produced " + injected + " Range values instead of one");
                    }
                }
                if (chunk && DuplicateChunkRangeHeader)
                    response.Headers.TryAddWithoutValidation("Range", "bytes=0-262143");
            }
            else if (chunk && CompleteWithCreated && response.IsSuccessStatusCode)
            {
                response.StatusCode = HttpStatusCode.Created;
            }
            return response;
        }
    }

    // Scriptable multi-page Drive listing endpoint. Only HTTP responses are synthetic: the production
    // inventory loop decides when to continue, when a token repeats and when to refuse.
    private sealed class PagedInventoryHandler : HttpMessageHandler
    {
        internal sealed record Page(string Files, int? NextIndex, bool Incomplete = false);

        private readonly Dictionary<string, List<Page>> _folders = new(StringComparer.Ordinal);

        internal Func<string, int, Page?>? UnscriptedPage { get; set; }
        internal int ListingCalls { get; private set; }
        // The fixture speaks JSON, so the double-quote character is composed instead of escaped.
        private const char Q = '"';

        internal void Script(string folderId, params Page[] pages) => _folders[folderId] = [.. pages];

        internal static string Folder(string id, string name, string parent) =>
            "{" + Q + "id" + Q + ":" + Q + id + Q + "," + Q + "name" + Q + ":" + Q + name + Q +
            "," + Q + "mimeType" + Q + ":" + Q + "application/vnd.google-apps.folder" + Q +
            "," + Q + "parents" + Q + ":[" + Q + parent + Q + "]," + Q + "trashed" + Q + ":false," +
            Q + "version" + Q + ":" + Q + "1" + Q + "}";

        internal static string File(string id, string name, string parent) =>
            "{" + Q + "id" + Q + ":" + Q + id + Q + "," + Q + "name" + Q + ":" + Q + name + Q +
            "," + Q + "mimeType" + Q + ":" + Q + "application/octet-stream" + Q +
            "," + Q + "parents" + Q + ":[" + Q + parent + Q + "]," + Q + "trashed" + Q + ":false," +
            Q + "size" + Q + ":" + Q + "4" + Q + "," + Q + "version" + Q + ":" + Q + "1" + Q + "}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            if (!path.EndsWith("/files", StringComparison.Ordinal))
            {
                var folderId = path[(path.LastIndexOf('/') + 1)..];
                return Task.FromResult(Json(Folder(folderId, "Pair", "root-parent")));
            }
            ListingCalls++;
            var query = ParseQuery(uri.Query);
            var filter = query.TryGetValue("q", out var value) ? value : string.Empty;
            var from = filter.IndexOf('\'');
            var to = filter.IndexOf("' in parents", StringComparison.Ordinal);
            var listed = from >= 0 && to > from ? filter[(from + 1)..to] : string.Empty;
            var index = query.TryGetValue("pageToken", out var token) && token.StartsWith('p') &&
                        int.TryParse(token[1..], out var parsed)
                ? parsed
                : 0;
            var pages = _folders.TryGetValue(listed, out var scripted) ? scripted : new List<Page>();
            var page = index < pages.Count ? pages[index] : UnscriptedPage?.Invoke(listed, index);
            if (page is null) return Task.FromResult(Json("{" + Q + "files" + Q + ":[]}"));
            var next = page.NextIndex is int nextIndex
                ? "," + Q + "nextPageToken" + Q + ":" + Q + "p" + nextIndex + Q
                : string.Empty;
            var incomplete = page.Incomplete ? "," + Q + "incompleteSearch" + Q + ":true" : string.Empty;
            return Task.FromResult(Json("{" + Q + "files" + Q + ":[" + page.Files + "]" + next + incomplete + "}"));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var index = part.IndexOf('=');
                if (index <= 0) continue;
                result[Uri.UnescapeDataString(part[..index])] = Uri.UnescapeDataString(part[(index + 1)..]);
            }
            return result;
        }
    }
}
