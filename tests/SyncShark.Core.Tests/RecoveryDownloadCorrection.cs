using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;

internal static class RecoveryDownloadCorrection
{
    internal static async Task RunNativeRangeAsync()
    {
        if (System.Runtime.InteropServices.Marshal.SizeOf<WindowsCloudFilesProvider.Native.SyncPolicies>() != 24 ||
            System.Runtime.InteropServices.Marshal.OffsetOf<WindowsCloudFilesProvider.Native.SyncPolicies>(
                "PlaceholderManagement").ToInt32() != 20)
            throw new Exception("CF_SYNC_POLICIES must match the six-field, 24-byte Windows ABI.");
        Console.WriteLine("PASS CLOUDFILES-ABI SyncPolicies size=24 PlaceholderManagement offset=20 default=0");
        var registrationSize = IntPtr.Size == 8 ? 72 : 44;
        var providerOffset = IntPtr.Size == 8 ? 52 : 28;
        if (System.Runtime.InteropServices.Marshal.SizeOf<WindowsCloudFilesProvider.Native.SyncRegistration>() != registrationSize ||
            System.Runtime.InteropServices.Marshal.OffsetOf<WindowsCloudFilesProvider.Native.SyncRegistration>("ProviderId").ToInt32() != providerOffset)
            throw new Exception("CF_SYNC_REGISTRATION does not match the current native ABI.");
        Console.WriteLine($"PASS CLOUDFILES-ABI SyncRegistration size={registrationSize} ProviderId offset={providerOffset} default=Guid.Empty");
        if (!OperatingSystem.IsWindows() || !WindowsCloudFilesProvider.TryGetPlatformVersion(out _))
            throw new PlatformNotSupportedException("The actual Cloud Files consumer requires Windows.");
        foreach (var reject in new[] { false, true })
        {
            var allocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TestData", "Projects", "sync-shark", "cloud-pro-tests", Guid.NewGuid().ToString("N"));
            var root = RecoveryNativeTestRoot.Create();
            var stage = Path.Combine(allocation, "stage");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(stage);
            using var transform = new DownloadMetadataTransform(new DropboxRangeContractHandler(), metadata =>
            {
                if (reject) metadata["id"] = "id:foreign-object";
            });
            using var http = new HttpClient(transform);
            var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes);
            var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, "id:pilot-folder");
            PilotDirectoryIdentity identity;
            using (var lease = PilotDirectoryLease.Open(root)) identity = lease.Identity;
            var pair = new SyncPair("recovery-range-" + Guid.NewGuid().ToString("N"), "Range correction", root,
                identity.VolumeSerialNumber, identity.FileId, "/Pilot", "id:pilot-folder", FilesOnDemandEnabled: true);
            using var provider = new WindowsCloudFilesProvider(pair, client, stage);
            try
            {
                provider.RegisterAndConnect();
                provider.CreatePlaceholder(DropboxRangeContractHandler.Expected);
                var outcome = await provider.SetAvailabilityAsync("range.bin", keepOnDevice: true, recursive: false);
                if (transform.DownloadCalls == 0) throw new Exception("The native callback never reached the Dropbox parser.");
                if (reject)
                {
                    if (outcome.Failures.Count != 1 || !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(
                            Path.Combine(root, "range.bin"), out var state) || state is null ||
                        state.State.Availability == SyncCloudFileAvailability.AvailableOffline)
                        throw new Exception("Rejected Dropbox metadata falsely marked the placeholder available offline.");
                }
                else
                {
                    if (outcome.Failures.Count != 0) throw new Exception("Native hydration rejected typed FileMetadata.");
                    using var input = File.OpenRead(Path.Combine(root, "range.bin"));
                    var bytes = new byte[4096];
                    input.Position = 4096;
                    input.ReadExactly(bytes);
                    if (bytes.Any(value => value != DropboxRangeContractHandler.RangeByte))
                        throw new Exception("The native callback did not deliver the exact fixture bytes.");
                }
                Console.WriteLine($"PASS DOWNLOAD-CONSUMER native-cloud-files/{(reject ? "foreign-id-rejected" : "typed-no-tag-hydrated")}: actual callback reached DropboxPilotClient; calls={transform.DownloadCalls}");
            }
            finally
            {
                provider.Dispose();
                if (WindowsCloudFilesProvider.IsOwnedSyncRoot(root, pair.Id))
                {
                    var result = WindowsCloudFilesProvider.Native.CfUnregisterSyncRoot(root);
                    if (result < 0) System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(result);
                }
                Directory.Delete(allocation, recursive: true);
                RecoveryNativeTestRoot.Remove(root);
            }
        }
    }

    internal static async Task RunMetadataAsync()
    {
        var cases = new (string Name, bool Success, Action<JsonObject> Change)[]
        {
            ("typed-no-tag", true, _ => { }),
            ("explicit-file-tag", true, m => m[".tag"] = "file"),
            ("lower-path-only", true, m => m.Remove("path_display")),
            ("absent-paths", true, m => { m.Remove("path_display"); m.Remove("path_lower"); }),
            ("null-paths", true, m => { m["path_display"] = null; m["path_lower"] = null; }),
            ("wrong-tag", false, m => m[".tag"] = "folder"),
            ("null-tag", false, m => m[".tag"] = null),
            ("numeric-tag", false, m => m[".tag"] = 42),
            ("wrong-id", false, m => m["id"] = "id:other"),
            ("wrong-rev", false, m => m["rev"] = "other-revision"),
            ("wrong-size", false, m => m["size"] = 123456),
            ("wrong-hash", false, m => m["content_hash"] = new string('f', 64)),
            ("outside-path", false, m => m["path_display"] = "/Outside/other.txt"),
            ("wrong-lower-only", false, m => { m.Remove("path_display"); m["path_lower"] = "/outside/wire.txt"; }),
            ("null-display-wrong-lower", false, m => { m["path_display"] = null; m["path_lower"] = "/outside/wire.txt"; }),
            ("contradictory-paths", false, m => m["path_lower"] = "/pilot/other.txt"),
            ("empty-display", false, m => m["path_display"] = ""),
            ("blank-paths", false, m => { m["path_display"] = " "; m["path_lower"] = " "; }),
            ("numeric-display", false, m => m["path_display"] = 42),
            ("array-lower", false, m => m["path_lower"] = new JsonArray()),
            ("pathless-wrong-id", false, m => { m.Remove("path_display"); m.Remove("path_lower"); m["id"] = "id:other"; }),
            ("pathless-wrong-rev", false, m => { m.Remove("path_display"); m.Remove("path_lower"); m["rev"] = "other-revision"; }),
            ("pathless-wrong-size", false, m => { m.Remove("path_display"); m.Remove("path_lower"); m["size"] = 123456; }),
            ("pathless-wrong-hash", false, m => { m.Remove("path_display"); m.Remove("path_lower"); m["content_hash"] = new string('f', 64); }),
            ("missing-id", false, m => m.Remove("id")),
            ("missing-rev", false, m => m.Remove("rev")),
            ("missing-size", false, m => m.Remove("size")),
            ("missing-hash", false, m => m.Remove("content_hash"))
        };
        foreach (var mode in new[] { "full", "historical", "range" })
        foreach (var test in cases)
        {
            var bytes = Encoding.UTF8.GetBytes("typed metadata actual bytes");
            var hash = await DropboxContentHash.ComputeAsync(new MemoryStream(bytes));
            HttpMessageHandler fixture = mode == "range"
                ? new DropboxRangeContractHandler()
                : new DropboxContractHandler
                {
                    DownloadBytes = bytes, DownloadHash = hash, DownloadRevision = "download-revision",
                    MetadataRevision = "download-revision", ArchiveHash = hash, ArchiveSize = bytes.Length
                };
            using var transform = new DownloadMetadataTransform(fixture, test.Change);
            using var http = new HttpClient(transform);
            var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes);
            var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, "id:pilot-folder");
            var expected = mode == "range" ? DropboxRangeContractHandler.Expected :
                new PilotRemoteEntry("wire.txt", PilotEntryKind.File, "id:wire", "download-revision", bytes.Length, hash);
            await using var destination = new MemoryStream();
            var sentinel = Encoding.UTF8.GetBytes("destination unchanged before metadata acceptance");
            if (!test.Success) await destination.WriteAsync(sentinel);
            Exception? rejected = null;
            try
            {
                if (mode == "range")
                    await client.DownloadRangeAsync(expected, 16, 32, destination);
                else if (mode == "historical")
                    await client.DownloadHistoricalRevisionAsync(expected, destination);
                else
                    await client.DownloadAsync(expected, destination);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or
                                      KeyNotFoundException or JsonException)
            { rejected = e; }
            if (transform.DownloadCalls != 1)
                throw new Exception($"{mode}/{test.Name}: proof did not reach download metadata.");
            if (test.Success)
            {
                if (rejected is not null) throw new Exception($"{mode}/{test.Name}: legitimate response rejected", rejected);
                var actual = destination.ToArray();
                var correct = mode == "range" ? Enumerable.Repeat(DropboxRangeContractHandler.RangeByte, 32).ToArray() : bytes;
                if (!actual.SequenceEqual(correct)) throw new Exception($"{mode}/{test.Name}: wrong downloaded bytes.");
            }
            else if (rejected is null || !destination.ToArray().SequenceEqual(sentinel))
                throw new Exception($"{mode}/{test.Name}: unsafe metadata accepted or destination modified.");
            Console.WriteLine($"PASS DOWNLOAD-METADATA {mode}/{test.Name}: {(test.Success ? "exact bytes accepted" : "rejected before destination write")}");
        }
    }

    internal static async Task RunMarkerAsync()
    {
        const string installation = "installation-marker-01";
        const string ns = "namespace-marker-0001";
        var root = SyncControlNamespace.RemoteContainerPath + "/" + ns;
        var path = root + "/.syncshark-owner.json";
        var bytes = SyncControlNamespace.MarkerBytes(installation, ns);
        var hash = DropboxContentHash.Compute(bytes);
        var binding = new SyncRemoteControlNamespaceBinding(root, "id:control-root", path,
            "id:control-marker", "control-marker", hash);
        foreach (var mode in new[] { "absent", "file", "folder", "null", "number", "absent-path", "null-path" })
        {
            var fixture = new DropboxControlNamespaceMarkerHandler(root, path, bytes, hash, "success");
            using var transform = new DownloadMetadataTransform(fixture, m =>
            {
                if (mode is "file" or "folder") m[".tag"] = mode;
                if (mode == "null") m[".tag"] = null;
                if (mode == "number") m[".tag"] = 42;
                if (mode == "absent-path") { m.Remove("path_display"); m.Remove("path_lower"); }
                if (mode == "null-path") { m["path_display"] = null; m["path_lower"] = null; }
            });
            using var http = new HttpClient(transform);
            var connection = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes);
            var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", connection, "id:pilot-folder");
            var rejected = false;
            try { await client.VerifyControlNamespaceAsync(binding, installation, ns); }
            catch (Exception e) when (e is IOException or InvalidDataException) { rejected = true; }
            if (rejected != (mode is not ("absent" or "file")) || fixture.DownloadCalls != 1 || fixture.PairRootMetadataCalls != 0)
                throw new Exception("Ownership marker discriminator/path proof failed: " + mode);
            Console.WriteLine("PASS MARKER " + mode + (rejected ? ": rejected" : ": exact ownership accepted"));
        }
    }

    private sealed class DownloadMetadataTransform(HttpMessageHandler inner, Action<JsonObject> change)
        : DelegatingHandler(inner)
    {
        internal int DownloadCalls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.RequestUri!.AbsolutePath.EndsWith("/files/download", StringComparison.Ordinal))
            {
                DownloadCalls++;
                var metadata = JsonNode.Parse(response.Headers.GetValues("Dropbox-API-Result").Single())!.AsObject();
                metadata.Remove(".tag");
                change(metadata);
                response.Headers.Remove("Dropbox-API-Result");
                response.Headers.Add("Dropbox-API-Result", metadata.ToJsonString());
            }
            return response;
        }
    }
}

internal static class RecoveryNativeTestRoot
{
    // Native sync roots cannot live under the Parent-managed test allocation on this host.
    // Only the ephemeral sync root moves; durable evidence and stage/database allocations do not.
    internal static string Create()
    {
        var temporaryParent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        var root = Path.Combine(temporaryParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The neutral Cloud Files test root has a reparse ancestor.");
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(65536);
            try
            {
                var hr = CfGetSyncRootInfoByPath(root, 0, buffer, 65536, out _);
                if (hr is not (unchecked((int)0x80070186) or unchecked((int)0x80070178) or unchecked((int)0x80070195)))
                    throw new IOException($"Neutral-root preflight did not prove absence of an existing sync root: 0x{hr:X8}.");
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
            Console.WriteLine("PASS NATIVE-ROOT preflight: fresh ordinary Temp root, all ancestors non-reparse, outside existing sync roots: " + root);
            return root;
        }
        catch { Directory.Delete(root); throw; }
    }

    internal static void Remove(string root)
    {
        var temporaryParent = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"));
        var exact = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(exact), temporaryParent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(exact), "N", out _) ||
            (File.GetAttributes(exact) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing cleanup of an unverified neutral test root.");
        Directory.Delete(exact, recursive: true);
    }

    [System.Runtime.InteropServices.DllImport("CldApi.dll", ExactSpelling = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int CfGetSyncRootInfoByPath(string path, uint infoClass, IntPtr buffer,
        uint capacity, out uint returned);
}
