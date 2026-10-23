using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// SS-D17 / SS-D18 / SS-D10 second batch: the scoped native-move proofs. Every dependency is shipped
// production code - the real DropboxPilotClient over a scripted HTTP wire, the real SyncAuthorityDatabase
// over an isolated SQLite file, the real SyncAuthorityMoveRemoteExecutor, MoveRemoteReconciler,
// CrossPairMoveCoordinator and DeleteRemoteExecutor, and real NTFS roots through PilotDirectoryLease and
// PilotLocalScanner. The scripted wire and the shared in-memory remote adapter supply provider input and can
// never replace the production decision under test. Nothing here reaches a live account, credential, network,
// installed database, product state or user pair root, and no value observed here is claimed as a
// live-provider timing measurement.
internal static class NativeMoveScopedProofChecks
{
    private const string InstallationId = "installation-native-move-scoped-proofs";
    private const string CapabilityProfileId = "profile-native-move-scoped-proofs";
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated native-move scoped-proof fixture root.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The native-move scoped proofs require real Windows NTFS file identities.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;

        await CaseAsync("A-dropbox-move-http-path",
            "The real Dropbox move and cross-root move HTTP paths move the retained stable identity exactly " +
            "once with the provider's latest contents, refuse a changed path/identity/kind/root/destination " +
            "before the effect, and issue no inventory request after the final metadata observation",
            DropboxMovePathAsync);
        await CaseAsync("B-dropbox-identity-lookup",
            "The real Dropbox identity lookup answers a classified absent path with false, a relocated same " +
            "identity with true, and every permission, rate, transport, malformed or wrong-identity response " +
            "with an error rather than absence",
            DropboxIdentityLookupAsync);
        await CaseAsync("C-scoped-local-proof",
            "The single-pair executor's local proof observes exactly the one target path and reads no sibling " +
            "or descendant, an unchanged move commits one exact baseline, and changed remote contents retain a " +
            "known move receipt with the actual successor and no synchronization point",
            SinglePairScopedProofAsync);
        await CaseAsync("D-changed-content-reconciliation",
            "A retained known-success receipt with the exact identity and kind at the destination but changed " +
            "contents settles as known-move/content-comparison-pending without a second network move",
            ChangedContentReconciliationAsync);
        await CaseAsync("E-cross-pair-scoped",
            "The cross-pair outbound direction carries changed provider contents, retains its root known-success " +
            "receipt before destination observation, never accepts a divergent baseline, never opens a retry " +
            "from a current path observation, and still permits one fenced retry from a retained " +
            "known-not-applied receipt",
            CrossPairScopedProofAsync);
        await CaseAsync("F-post-delete-identity",
            "Both real post-delete branches ask the one bounded identity question, the unsupported store keeps " +
            "its conservative listing fallback, and a surviving identity still refuses",
            PostDeleteIdentityAsync);
        await CaseAsync("G-exact-version-registration",
            "Exact registration returns the retained canonical row and rejects content or alias collisions atomically",
            ExactVersionRegistrationAsync);
        await CaseAsync("H-applied-version-conflict",
            "An applied move with conflicting version identity retains raw success, including crash/reopen, without replay",
            AppliedVersionConflictAsync);

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "NativeMoveScopedProofChecks (tests/SyncShark.Core.Tests)",
            ["requirements"] = new[] { "SS-D17", "SS-D18", "SS-D10" },
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases,
            ["measurements"] = new Dictionary<string, object?>
            {
                ["provider_request_counts"] = "recorded per case from the scripted wire and the real client's " +
                    "own ProviderCallCount",
                ["inspected_local_path_counts"] = "recorded from the real PilotLocalScanner scan results " +
                    "(PathsInspected / FilesHashed / TotalBytes)",
                ["timing_gap"] = "the interval between the final identity/location check and the provider's one " +
                    "move request is an accepted provider limitation. It is an engineering estimate and remains " +
                    "UNMEASURED for a live provider; no atomic identity-plus-location guarantee is claimed."
            }
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "native-move-scoped-proof-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        var failed = Cases.Count(item => item.TryGetValue("result", out var value) && Equals(value, "FAIL"));
        if (failed > 0)
        {
            foreach (var item in Cases.Where(entry => Equals(entry["result"], "FAIL")))
                Console.WriteLine("FAIL " + item["case"] + ": " + item["error"]);
            Console.WriteLine("FAIL native move scoped proofs: " + failed + " of " + Cases.Count +
                " cases failed; the complete report is in native-move-scoped-proof-checks.json");
            throw new InvalidDataException(failed + " native-move scoped-proof cases failed.");
        }
        Console.WriteLine("PASS native move scoped proofs: " + _assertions + " assertions; the real Dropbox " +
            "move, cross-root move and identity-lookup HTTP paths, the real retained authority journal and the " +
            "real single-pair, cross-pair and deletion executors prove the accepted latest-content move policy " +
            "with bounded scoped local proofs, truthful retained outcomes and no replay.");
    }

    private static async Task CaseAsync(string id, string label, Func<Task> body)
    {
        var before = _assertions;
        try
        {
            await body();
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "PASS", ["assertions"] = _assertions - before
            });
            Console.WriteLine("PASS " + id + ": " + label + " (" + (_assertions - before) + " assertions)");
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "FAIL", ["assertions"] = _assertions - before,
                ["error"] = exception.GetType().Name + ": " + exception.Message
            });
            Console.WriteLine("FAIL " + id + ": " + exception.GetType().Name + ": " + exception.Message);
            // J2 aggregation: an ordinary case failure is recorded and the remaining independent cases still run.
            // The runner reports an overall failure after every case has been attempted.
        }
    }

    private static void Require(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidDataException("Scoped native-move assertion failed: " + label);
    }

    private static string Hash(string role, params string[] parts) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts)))).ToLowerInvariant();

    // ----- A: the real Dropbox move and cross-root move HTTP paths -------------------------------

    private static async Task DropboxMovePathAsync()
    {
        // A1: the provider's revision changed after the reviewed baseline. The stable identity is still moved
        // exactly once and the returned revision is the moved object's latest contents, not a refusal.
        var wire = new MoveWire { SourceRevision = "rev:2" };
        using (var http = new HttpClient(wire))
        {
            var client = DropboxClient(http, "/Pilot", "id:pilot-folder");
            var moved = await client.MoveAsync(Reviewed("source.txt"), "moved.txt");
            Require(moved.Id == "id:source" && moved.RelativePath == "moved.txt" && moved.Revision == "rev:2",
                "A1 the current provider revision is returned as the moved object's latest contents");
            Require(wire.MoveCalls == 1 && wire.LastMoveFrom == "id:source" &&
                    wire.LastMoveTo == "id:pilot-folder/moved.txt",
                "A1 the retained stable ID is moved exactly once to the exact destination path");
            Require(wire.ListFolderCalls == 0 && wire.CreateFolderCalls == 0,
                "A1 no recursive inventory and no parent-setup request was needed for a root-level destination");
            Require(wire.Routes.Count(route => route == "get_metadata:id:source") == 1,
                "A1 the source identity is observed exactly once, at the final check");
            var finalRead = wire.Routes.FindLastIndex(route => route == "get_metadata:id:source");
            Require(finalRead >= 0 && wire.Routes.IndexOf("move_v2") == finalRead + 1,
                "A1 the exact-identity metadata read is immediately before the one move request");
            Require(client.ProviderCallCount == wire.Calls,
                "A1 the measured request count is bound to the real client's own provider call count: " +
                client.ProviderCallCount + " vs " + wire.Calls);
        }

        // A2: the revision changes between the final check and the effect. The move response carries that
        // latest revision and is accepted, because a stable-ID native move carries the latest contents.
        wire = new MoveWire { SourceRevision = "rev:2", MoveResponseRevision = "rev:3" };
        using (var http = new HttpClient(wire))
        {
            var client = DropboxClient(http, "/Pilot", "id:pilot-folder");
            var moved = await client.MoveAsync(Reviewed("source.txt"), "moved.txt");
            Require(moved.Revision == "rev:3" && wire.MoveCalls == 1,
                "A2 a version change between the check and the effect is accepted as the latest contents");
        }

        // A3-A5: a path, identity or kind change before the final check refuses with zero move requests.
        foreach (var change in new[] { "path", "identity", "kind" })
        {
            wire = new MoveWire { SourceRevision = "rev:1", SourceChange = change };
            using var http = new HttpClient(wire);
            var client = DropboxClient(http, "/Pilot", "id:pilot-folder");
            var refused = await RefusalAsync(() => client.MoveAsync(Reviewed("source.txt"), "moved.txt"));
            Require(refused == "DropboxApiException",
                "A " + change + " change before the check refuses as a conflict, saw " + refused);
            Require(wire.MoveCalls == 0,
                "A " + change + " change before the check issued zero move requests");
        }

        // A6-A7: a different identity or path in the move response is an uncertain outcome, never success.
        foreach (var response in new[] { "identity", "path" })
        {
            wire = new MoveWire { SourceRevision = "rev:1", MoveResponseChange = response };
            using var http = new HttpClient(wire);
            var client = DropboxClient(http, "/Pilot", "id:pilot-folder");
            var refused = await RefusalAsync(() => client.MoveAsync(Reviewed("source.txt"), "moved.txt"));
            Require(refused == "DropboxEffectUncertainException",
                "A returned " + response + " substitution is uncertain, saw " + refused);
            Require(wire.MoveCalls == 1,
                "A returned " + response + " substitution was detected after exactly one move request");
        }

        // A8: an occupied destination refuses with zero move requests.
        wire = new MoveWire { SourceRevision = "rev:1", DestinationOccupied = true };
        using (var http = new HttpClient(wire))
        {
            var client = DropboxClient(http, "/Pilot", "id:pilot-folder");
            var refused = await RefusalAsync(() => client.MoveAsync(Reviewed("source.txt"), "moved.txt"));
            Require(refused == "DropboxApiException" && wire.MoveCalls == 0 && wire.ListFolderCalls == 0,
                "A8 an occupied destination refuses from the bounded exact-path lookup with zero effect, saw " +
                refused);
        }

        // A9: a destination parent that is occupied by a file refuses with zero move requests.
        wire = new MoveWire { SourceRevision = "rev:1", ParentOccupiedByFile = true };
        using (var http = new HttpClient(wire))
        {
            var client = DropboxClient(http, "/Pilot", "id:pilot-folder");
            var refused = await RefusalAsync(() => client.MoveAsync(Reviewed("source.txt"), "sub/moved.txt"));
            Require(refused is "IOException" or "DropboxApiException" && wire.MoveCalls == 0,
                "A9 a failed destination parent refuses with zero effect, saw " + refused);
            Require(wire.ListFolderCalls == 0,
                "A9 the destination parent is verified by exact path metadata, never by a listing");
        }

        // A10: the cross-root move keeps one verified connection, account and namespace and moves once.
        wire = new MoveWire { SourceRevision = "rev:1" };
        using (var http = new HttpClient(wire))
        {
            var source = DropboxClient(http, "/Pilot", "id:pilot-folder");
            var destination = DropboxClient(http, "/Archive", "id:archive-folder");
            var moved = await source.MoveToAsync(Reviewed("source.txt"), destination, "moved.txt");
            Require(moved.Id == "id:source" && moved.RelativePath == "moved.txt" && wire.MoveCalls == 1 &&
                    wire.LastMoveTo == "id:archive-folder/moved.txt",
                "A10 the cross-root move preserves the stable identity at the destination root exactly once");
        }

        // A11: a changed account, namespace or connection refuses the cross-root move before any request.
        foreach (var changed in new[] { "account", "namespace", "connection" })
        {
            wire = new MoveWire { SourceRevision = "rev:1" };
            using var http = new HttpClient(wire);
            var source = DropboxClient(http, "/Pilot", "id:pilot-folder");
            var destination = DropboxClient(http, "/Archive", "id:archive-folder", changed);
            var refused = await RefusalAsync(() =>
                source.MoveToAsync(Reviewed("source.txt"), destination, "moved.txt"));
            Require(refused == "InvalidOperationException" && wire.Calls == 0 && wire.MoveCalls == 0,
                "A11 a changed " + changed + " refuses the cross-root move with zero provider requests, saw " +
                refused);
        }
    }

    // ----- B: the real Dropbox identity lookup --------------------------------------------------

    private static async Task DropboxIdentityLookupAsync()
    {
        // B1: only Dropbox's own classified absent-path response answers false.
        var wire = new MoveWire { SourceRevision = "rev:1", IdentityLookupMode = "missing" };
        using (var http = new HttpClient(wire))
        {
            var client = DropboxClient(http, "/Pilot", "id:pilot-folder");
            var before = wire.Calls;
            var providerBefore = client.ProviderCallCount;
            Require(!await client.ObjectExistsAsync("id:deleted"),
                "B1 the classified absent-path response is the only response that answers false");
            Require(wire.ListFolderCalls == 0,
                "B1 the identity check performs no recursive inventory");
            Require(client.ProviderCallCount - providerBefore == wire.Calls - before,
                "B1 the identity check adds exactly the requests the real client counted");
        }

        // B2: the same identity observed at another path inside the namespace still exists.
        wire = new MoveWire { SourceRevision = "rev:4", IdentityLookupMode = "relocated" };
        using (var http = new HttpClient(wire))
        {
            var client = DropboxClient(http, "/Pilot", "id:pilot-folder");
            Require(await client.ObjectExistsAsync("id:deleted"),
                "B2 a relocated same identity still exists; a path disagreement is never absence");
            Require(wire.ListFolderCalls == 0,
                "B2 the relocated identity is proved without any recursive inventory");
        }

        // B3: a different identity, a wrong shape and every adverse transport response stay errors.
        foreach (var mode in new[] { "wrong-identity", "missing-identity", "folder-tag-only" })
        {
            wire = new MoveWire { SourceRevision = "rev:1", IdentityLookupMode = mode };
            using var http = new HttpClient(wire);
            var client = DropboxClient(http, "/Pilot", "id:pilot-folder");
            var refused = await RefusalAsync(() => client.ObjectExistsAsync("id:deleted"));
            Require(refused is "IOException" or "InvalidDataException" or "JsonException",
                "B3 the " + mode + " response is an error, never absence, saw " + refused);
        }
        foreach (var mode in new[] { "malformed", "unauthorized", "rate-limited" })
        {
            wire = new MoveWire { SourceRevision = "rev:1", IdentityLookupMode = mode };
            using var http = new HttpClient(wire);
            var client = DropboxClient(http, "/Pilot", "id:pilot-folder");
            var refused = await RefusalAsync(() => client.ObjectExistsAsync("id:deleted"));
            Require(refused is "JsonException" or "DropboxApiException" or "HttpRequestException",
                "B3 the " + mode + " response stays an error, never absence, saw " + refused);
        }
        wire = new MoveWire { SourceRevision = "rev:1", IdentityLookupMode = "transport" };
        using (var http = new HttpClient(wire))
        {
            var client = DropboxClient(http, "/Pilot", "id:pilot-folder");
            var refused = await RefusalAsync(() => client.ObjectExistsAsync("id:deleted"));
            Require(refused == "HttpRequestException",
                "B3 transport loss stays an error, never absence, saw " + refused);
        }

        // B4: a non-identity argument is refused before any provider request.
        wire = new MoveWire { SourceRevision = "rev:1" };
        using (var http = new HttpClient(wire))
        {
            var client = DropboxClient(http, "/Pilot", "id:pilot-folder");
            var refused = await RefusalAsync(() => client.ObjectExistsAsync("deleted.txt"));
            Require(refused == "InvalidDataException" && wire.Calls == 0,
                "B4 a relative path is not an identity and refuses with zero provider requests, saw " + refused);
        }
    }

    private static async Task<string> RefusalAsync(Func<Task> action)
    {
        try
        {
            await action();
            return "none";
        }
        catch (Exception exception)
        {
            // I2: System.Text.Json.JsonReaderException derives JsonException, so the normalised refusal name is
            // JsonException rather than the concrete reader type.
            return exception is JsonException ? nameof(JsonException) : exception.GetType().Name;
        }
    }

    private static PilotRemoteEntry Reviewed(string path) =>
        new(path, PilotEntryKind.File, "id:source", "rev:1", 11, Digest);

    private static DropboxPilotClient DropboxClient(HttpClient http, string root, string folderId,
        string change = "none") => new(http, new FixedTokenSource(), root,
        new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            change == "account" ? "other-account" : "account-id",
            change == "namespace" ? "other-namespace" : "root-namespace",
            change == "connection" ? "home-namespace-other" : "home-namespace",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
            change == "connection" ? "connection-other" : ""),
        folderId);

    private sealed class MoveWire : HttpMessageHandler
    {
        private static readonly (string Id, string Path)[] Roots =
            [("id:pilot-folder", "/Pilot"), ("id:archive-folder", "/Archive")];

        internal string SourceRevision = "rev:1";
        internal string? MoveResponseRevision;
        internal string? SourceChange;
        internal string? MoveResponseChange;
        internal bool DestinationOccupied;
        internal bool ParentOccupiedByFile;
        internal string IdentityLookupMode = "missing";
        internal int Calls;
        internal int MoveCalls;
        internal int ListFolderCalls;
        internal int CreateFolderCalls;
        internal string? LastMoveFrom;
        internal string? LastMoveTo;
        internal readonly List<string> Routes = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Method != HttpMethod.Post) throw new InvalidOperationException("Unexpected method.");
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
                return Json("{\"account_id\":\"account-id\",\"root_info\":{\"root_namespace_id\":\"root-namespace\",\"home_namespace_id\":\"home-namespace\"}}");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            // F3: only routes that carry a path read it; the move route supplies from_path/to_path below.
            if (route.EndsWith("/files/list_folder", StringComparison.Ordinal))
            {
                ListFolderCalls++;
                Routes.Add("list_folder");
                return Json("{\"entries\":[],\"cursor\":\"cursor\",\"has_more\":false}");
            }
            if (route.EndsWith("/files/create_folder_v2", StringComparison.Ordinal))
            {
                CreateFolderCalls++;
                Routes.Add("create_folder_v2");
                throw new InvalidOperationException(
                    "A scoped native move must not create destination parents for this fixture.");
            }
            if (route.EndsWith("/files/move_v2", StringComparison.Ordinal))
            {
                MoveCalls++;
                Routes.Add("move_v2");
                LastMoveFrom = body.RootElement.GetProperty("from_path").GetString();
                LastMoveTo = body.RootElement.GetProperty("to_path").GetString();
                var destination = LastMoveTo!;
                var owner = Roots.Single(root => destination.StartsWith(root.Id + "/", StringComparison.Ordinal));
                var name = destination[(owner.Id.Length + 1)..];
                var display = MoveResponseChange == "path"
                    ? owner.Path + "/other.txt"
                    : owner.Path + "/" + name;
                return Json("{\"metadata\":" + MoveMetadata(
                    MoveResponseChange == "identity" ? "id:other" : "id:source", display,
                    MoveResponseRevision ?? SourceRevision) + "}");
            }
            if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
            {
                var path = body.RootElement.GetProperty("path").GetString()!;
                Routes.Add("get_metadata:" + path);
                var owner = Roots.FirstOrDefault(root => root.Id == path);
                if (owner.Id is not null)
                    return Json("{\".tag\":\"folder\",\"name\":\"" + owner.Path.TrimStart('/') +
                        "\",\"id\":\"" + owner.Id + "\",\"path_lower\":\"" + owner.Path.ToLowerInvariant() +
                        "\",\"path_display\":\"" + owner.Path + "\"}");
                if (path == "id:deleted" && IdentityLookupMode == "missing")
                    return Missing();
                if (path == "id:deleted" && IdentityLookupMode == "relocated")
                    return Json(MoveMetadata("id:deleted", "/Pilot/relocated-elsewhere.txt", SourceRevision));
                if (path == "id:deleted" && IdentityLookupMode == "wrong-identity")
                    return Json(MoveMetadata("id:another-object", "/Pilot/deleted.txt", SourceRevision));
                if (path == "id:deleted" && IdentityLookupMode == "missing-identity")
                    return Json("{\".tag\":\"file\",\"name\":\"deleted.txt\",\"size\":1,\"path_display\":\"/Pilot/deleted.txt\"}");
                if (path == "id:deleted" && IdentityLookupMode == "folder-tag-only")
                    return Json("{\".tag\":\"other\",\"name\":\"deleted.txt\",\"id\":\"id:deleted\",\"path_display\":\"/Pilot/deleted.txt\"}");
                if (path == "id:deleted" && IdentityLookupMode == "malformed") return Raw("{not json");
                if (path == "id:deleted" && IdentityLookupMode == "unauthorized")
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    { Content = new StringContent("{\"error_summary\":\"invalid_access_token\"}") };
                if (path == "id:deleted" && IdentityLookupMode == "rate-limited")
                {
                    var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    { Content = new StringContent("{\"error_summary\":\"too_many_requests\"}") };
                    limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                    return limited;
                }
                if (path == "id:deleted" && IdentityLookupMode == "transport")
                    throw new HttpRequestException("simulated transport loss");
                if (path == "id:source")
                {
                    if (SourceChange == "path")
                        return Json(MoveMetadata("id:source", "/Pilot/other.txt", SourceRevision));
                    if (SourceChange == "identity")
                        return Json(MoveMetadata("id:other", "/Pilot/source.txt", SourceRevision));
                    if (SourceChange == "kind")
                        return Json("{\".tag\":\"folder\",\"name\":\"source.txt\",\"id\":\"id:source\"," +
                            "\"path_lower\":\"/pilot/source.txt\",\"path_display\":\"/Pilot/source.txt\"}");
                    return Json(MoveMetadata("id:source", "/Pilot/source.txt", SourceRevision));
                }
                if (path.EndsWith("/sub", StringComparison.Ordinal))
                {
                    if (ParentOccupiedByFile)
                        return Json(MoveMetadata("id:parent-file", path, "rev:1"));
                    return Missing();
                }
                if (DestinationOccupied && path.EndsWith("/moved.txt", StringComparison.Ordinal))
                {
                    var ownerRoot = Roots.Single(root => path.StartsWith(root.Id + "/", StringComparison.Ordinal));
                    return Json(MoveMetadata("id:occupant", ownerRoot.Path + "/moved.txt", "rev:9"));
                }
                return Missing();
            }
            throw new InvalidOperationException("Unexpected scoped native-move provider route: " + route);
        }

        private static string MoveMetadata(string id, string display, string revision) =>
            "{\".tag\":\"file\",\"name\":\"" + display.Split("/")[^1] + "\",\"id\":\"" + id +
            "\",\"rev\":\"" + revision + "\",\"size\":11,\"content_hash\":\"" + Digest + "\"," +
            "\"path_lower\":\"" + display.ToLowerInvariant() + "\",\"path_display\":\"" + display + "\"}";

        private static HttpResponseMessage Missing() => new(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"not_found\"}}}")
        };

        private static HttpResponseMessage Json(string value) => Raw(value);

        private static HttpResponseMessage Raw(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }

    // ----- C/D: the real single-pair executor, reconciler and local proof -------------------------

    private static async Task SinglePairScopedProofAsync()
    {
        // C1: the real scanner's scoped proof observes exactly the target path. The negative control on the
        // same root shows which siblings and descendants a whole-root inventory would have read.
        var scopedRoot = Path.Combine(_root, "c-scoped-local-proof");
        Directory.CreateDirectory(scopedRoot);
        await File.WriteAllTextAsync(Path.Combine(scopedRoot, "source.txt"), "scoped payload");
        await File.WriteAllTextAsync(Path.Combine(scopedRoot, "sibling.txt"), "sibling payload");
        await File.WriteAllBytesAsync(Path.Combine(scopedRoot, "big.bin"), new byte[8 * 1024 * 1024]);
        PilotDirectoryIdentity scopedIdentity;
        using (var lease = PilotDirectoryLease.Open(scopedRoot))
        {
            scopedIdentity = lease.Identity;
            var scoped = await new PilotLocalScanner().ScanAsync(lease,
                includePath: ExactPath("source.txt"), traverseDirectory: _ => false,
                refreshPaths: ["source.txt"], contentProvider: "Dropbox", expectedPairId: ScopedPairId);
            Require(scoped.PathsInspected == 1 && scoped.FilesHashed == 1 && scoped.Entries.Count == 1,
                "C1 the scoped proof inspected one path and hashed one file, saw " + scoped.PathsInspected +
                " paths / " + scoped.FilesHashed + " hashes / " + scoped.Entries.Count + " entries");
            Require(scoped.Entries["source.txt"].Kind == PilotEntryKind.File &&
                    !scoped.Entries.ContainsKey("sibling.txt") && !scoped.Entries.ContainsKey("big.bin"),
                "C1 no sibling or unrelated large entry was observed or read");
            Require(scoped.TotalBytes == new FileInfo(Path.Combine(scopedRoot, "source.txt")).Length,
                "C1 only the target file's bytes were read, saw " + scoped.TotalBytes + " bytes");
            var wholeRoot = await new PilotLocalScanner().ScanAsync(lease, contentProvider: "Dropbox",
                expectedPairId: ScopedPairId);
            Require(wholeRoot.PathsInspected >= 3 && wholeRoot.FilesHashed >= 3 &&
                    wholeRoot.TotalBytes > scoped.TotalBytes,
                "C1 the same root read by a whole-root inventory observes every sibling and the 8 MiB file: " +
                wholeRoot.PathsInspected + " paths / " + wholeRoot.FilesHashed + " hashes");
            AssertLocalIdentity(scopedIdentity, scopedRoot);
        }

        // C2: with an unrelated sibling held exclusively, the scoped proof still observes only the target and
        // records no blocked decision, which is only possible if the sibling is never opened.
        using (var held = new FileStream(Path.Combine(scopedRoot, "sibling.txt"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            using var lease = PilotDirectoryLease.Open(scopedRoot);
            var scoped = await new PilotLocalScanner().ScanAsync(lease,
                includePath: ExactPath("source.txt"), traverseDirectory: _ => false,
                refreshPaths: ["source.txt"], contentProvider: "Dropbox", expectedPairId: ScopedPairId);
            Require(scoped.Decisions.Count == 0 && scoped.PathsInspected == 1,
                "C2 an exclusively held sibling is never touched by the scoped proof, saw " +
                scoped.Decisions.Count + " decisions and " + scoped.PathsInspected + " inspected paths");
            Require(held.SafeFileHandle is not null,
                "C2 the sibling stays exclusively held for the whole scoped proof");
        }
        Require(await File.ReadAllTextAsync(Path.Combine(scopedRoot, "sibling.txt")) == "sibling payload",
            "C2 the exclusively held sibling keeps its exact bytes after the scoped proof");

        // C3: the real executor moves an unchanged remote directory. Its local source proof is metadata-only,
        // so an exclusively held descendant of the moved directory is never opened, and exactly one exact
        // baseline is committed once.
        var directory = await MoveFixture.CreateAsync("c-unchanged-directory", PilotEntryKind.Directory,
            withRequestSentAttempt: false);
        Require(directory.FixtureKind == PilotEntryKind.Directory,
            "C3 the directory fixture really retains a folder source identity");
        var lockedChild = Path.Combine(directory.DesiredLocalPath, "held-descendant.bin");
        await File.WriteAllBytesAsync(lockedChild, new byte[1024]);
        using (var held = new FileStream(lockedChild, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var moved = await directory.ExecuteAsync();
            Require(moved.Status == SyncAuthorityOperationStatus.Completed,
                "C3 the unchanged directory move completed, saw " + moved.Status + ": " + moved.Reason);
            Require(directory.Remote.MoveCount == 1 && directory.Remote.DirectoryExists(directory.DesiredPath) &&
                    !directory.Remote.DirectoryExists(directory.PriorPath),
                "C3 exactly one provider move relocated the exact retained folder identity");
            Require(held.SafeFileHandle is not null && File.Exists(lockedChild),
                "C3 the exclusively held descendant of the moved folder was never opened or changed");
        }
        Require(await directory.SyncPointCountAsync() == 1 && await directory.MembershipCountAsync() == 1,
            "C3 exactly one synchronization point and one successor membership were committed");
        Require(await directory.LatestReceiptCertaintyAsync() == "known_success",
            "C3 the unchanged move retained one known-success receipt");
        Require(await directory.OperationStatusAsync() == "completed",
            "C3 the unchanged move completed its retained operation");
        var replay = await directory.TryExecuteAsync();
        Require(replay is not null && directory.Remote.MoveCount == 1 &&
                await directory.SyncPointCountAsync() == 1,
            "C3 a repeat or restart refuses to issue the move again, saw " + replay);

        // C4: changed remote contents keep a truthful known-success move receipt with the actual successor and
        // no synchronization point, and no restart may replay the move.
        var changed = await MoveFixture.CreateAsync("c-changed-content", PilotEntryKind.File,
            withRequestSentAttempt: false);
        Require(changed.FixtureKind == PilotEntryKind.File,
            "C4 the changed-content fixture really retains a file source identity");
        changed.Remote.Put(changed.PriorPath, "latest provider bytes");
        var changedEntry = (await changed.Remote.ListEntriesAsync()).Single(entry => entry.RelativePath == changed.PriorPath);
        Require(changedEntry.Id == MoveFixture.FileIdentity && changedEntry.Revision == "rev:2",
            "C4 a provider content edit preserves stable identity and advances the immutable revision");
        var latestHash = await changed.RemoteContentHashAsync(changed.PriorPath);
        var blocked = await changed.ExecuteAsync();
        Require(blocked.Status == SyncAuthorityOperationStatus.Blocked &&
                blocked.Reason.Contains("fresh comparison", StringComparison.Ordinal),
            "C4 changed remote contents return a truthful held state, saw " + blocked.Status + ": " +
            blocked.Reason);
        Require(changed.Remote.MoveCount == 1 && !changed.Remote.Exists(changed.PriorPath) &&
                changed.Remote.Exists(changed.DesiredPath),
            "C4 the changed object was physically moved exactly once and retained at the destination");
        Require(await changed.SyncPointCountAsync() == 0,
            "C4 no synchronization point claims the original local bytes equal the changed remote bytes");
        Require(await changed.MembershipCountAsync() == 1 &&
                await changed.LatestReceiptCertaintyAsync() == "known_success",
            "C4 the actual latest successor and a known-success move receipt were retained");
        Require(await changed.LatestReceiptEvidenceAsync() is { } evidence &&
                evidence.Contains("native_move_applied_content_comparison_pending", StringComparison.Ordinal),
            "C4 the retained receipt records the explicit content-comparison-pending classification");
        Require(await changed.SuccessorVersionHashAsync() == latestHash,
            "C4 the retained successor version binds the provider's actual latest content hash");
        Require(await changed.OperationStatusAsync() == "blocked",
            "C4 the retained operation is visibly blocked rather than reported synced");
        var changedReplay = await changed.TryExecuteAsync();
        Require(changedReplay is not null && changed.Remote.MoveCount == 1 &&
                await changed.SyncPointCountAsync() == 0 &&
                await changed.LatestReceiptCertaintyAsync() == "known_success",
            "C4 no restart replays the move or invents an equal-content baseline, saw " + changedReplay);
    }

    private static async Task ChangedContentReconciliationAsync()
    {
        // D: the exact crash boundary after a successful move. The retained known-success receipt is durable,
        // the identity with changed contents is already at the destination path, and the attempt has not been
        // reconciled. The reconciler must settle the same held state without a second network move.
        var fixture = await MoveFixture.CreateAsync("d-changed-content-reconciliation", PilotEntryKind.File,
            withRequestSentAttempt: true);
        // The provider already applied the move: one identical identity now sits at the destination path with
        // contents that differ from the retained member.
        fixture.Remote.PutWithId(fixture.DesiredPath, "latest provider bytes", MoveFixture.FileIdentity, revision: 2);
        var destinationEntry = (await fixture.Remote.ListEntriesAsync()).Single(entry => entry.RelativePath == fixture.DesiredPath);
        Require(destinationEntry.Id == MoveFixture.FileIdentity && destinationEntry.Revision == "rev:2",
            "D pre-applied changed content has the same stable identity and a new revision");
        fixture.Remote.Delete(fixture.PriorPath);
        await fixture.RecordKnownSuccessReceiptAsync();
        var before = await fixture.RetainedStateAsync();
        var reconciled = await fixture.ReconcileAsync();
        Require(reconciled.Status == SyncAuthorityOperationStatus.Blocked &&
                reconciled.Reason.Contains("fresh comparison", StringComparison.Ordinal),
            "D the retained known-success receipt with changed contents settles as held, saw " +
            reconciled.Status + ": " + reconciled.Reason);
        Require(fixture.Remote.MoveCount == 0,
            "D the reconciliation issued no second network move");
        Require(await fixture.SyncPointCountAsync() == 0 && await fixture.MembershipCountAsync() == 0,
            "D no synchronization point or membership was invented from the current destination path");
        Require(await fixture.ReceiptCountAsync() == 1,
            "D the retained known-success receipt stayed the single durable receipt");
        Require(await fixture.OperationStatusAsync() == "blocked" &&
                await fixture.AttemptStatusesAsync() == "blocked",
            "D the retained attempt and operation are visibly blocked");
        Require(await fixture.RetainedStateAsync() != before,
            "D the fenced reconciliation transition is the only retained change");
        var replay = await fixture.TryExecuteAsync();
        Require(replay is not null && fixture.Remote.MoveCount == 0 &&
                await fixture.ReceiptCountAsync() == 1,
            "D no restart reissues the move or appends a second receipt, saw " + replay);
    }

    private static Func<string, bool> ExactPath(string path) => item => string.Equals(
        PilotPath.CanonicalComparisonKey(item), PilotPath.CanonicalComparisonKey(path), StringComparison.Ordinal);

    private static void AssertLocalIdentity(PilotDirectoryIdentity identity, string root)
    {
        Require(identity.VolumeSerialNumber != 0 && identity.FileId != 0,
            "C1 the scoped proof ran against a real NTFS root identity");
        Require(Directory.Exists(root), "C1 the real fixture root is present");
    }

    private static Task<long> ScalarLongAsync(string databasePath, string sql) => ScalarAsync(databasePath, sql,
        value => value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture));

    private static async Task<T> ScalarAsync<T>(string databasePath, string sql, Func<object?, T> project)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath + ";Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return project(await command.ExecuteScalarAsync());
    }

    private static Task<string> ScalarStringAsync(string databasePath, string sql) => ScalarAsync(databasePath,
        sql, value => value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");

    private const string ScopedPairId = "pair-scoped-local-proof";

    // The real retained-authority fixture for one pair root, the real isolated journal and the real executors.
    private static async Task ExactVersionRegistrationAsync()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "g-exact-versions")).FullName;
        var databasePath = Path.Combine(directory, "authority.db");
        var authority = new SyncAuthorityDatabase(databasePath);
        await authority.InitializeAsync(InstallationId, CapabilityProfileId, "exact-versions", "{}");
        await authority.RegisterObjectAsync("object-exact", "file");
        const string identity = "{\"fileId\":\"id:exact\",\"revision\":\"rev:1\"}";
        Require(await authority.RegisterObjectVersionExactAsync("version-canonical", "object-exact",
                "dropbox", identity, Digest, 7) == "version-canonical",
            "G new exact registration returns its committed row");
        Require(await authority.RegisterObjectVersionExactAsync("version-alias", "object-exact",
                "dropbox", identity, Digest, 7) == "version-canonical" &&
                await ScalarLongAsync(databasePath, "SELECT count(*) FROM object_version;") == 1,
            "G an equivalent requested alias returns the actual canonical row without a phantom version");
        var hashConflict = await RefusalAsync(async () =>
            await authority.RegisterObjectVersionExactAsync("version-conflict", "object-exact",
                "dropbox", identity, new string('b', 64), 7));
        var nullConflict = await RefusalAsync(async () =>
            await authority.RegisterObjectVersionExactAsync("version-null", "object-exact",
                "dropbox", identity, null, 7));
        var sizeConflict = await RefusalAsync(async () =>
            await authority.RegisterObjectVersionExactAsync("version-size", "object-exact",
                "dropbox", identity, Digest, 8));
        var idConflict = await RefusalAsync(async () =>
            await authority.RegisterObjectVersionExactAsync("version-canonical", "object-exact",
                "dropbox", "{\"fileId\":\"id:other\",\"revision\":\"rev:1\"}", Digest, 7));
        Require(new[] { hashConflict, nullConflict, sizeConflict, idConflict }.All(
                error => error == nameof(SyncObjectVersionIdentityConflictException)) &&
                await ScalarLongAsync(databasePath, "SELECT count(*) FROM object_version;") == 1,
            "G hash, null, size and requested-ID conflicts reject atomically without ignored inserts");
        var reopened = new SyncAuthorityDatabase(databasePath);
        await reopened.InitializeAsync(InstallationId, CapabilityProfileId, "exact-versions", "{}");
        Require(await reopened.RegisterObjectVersionExactAsync("version-alias-reopened", "object-exact",
                "dropbox", identity, Digest, 7) == "version-canonical",
            "G canonical identity survives database reopen");
    }

    private static async Task AppliedVersionConflictAsync()
    {
        foreach (var crashAfterReceipt in new[] { false, true })
        {
            var fixture = await MoveFixture.CreateAsync("h-identity-conflict-" + crashAfterReceipt,
                PilotEntryKind.File, withRequestSentAttempt: false);
            // Deliberately inconsistent provider input: changed bytes under the SAME immutable revision.
            // Unlike C4/D/E, this case tests the retained identity-conflict boundary itself.
            fixture.Remote.PutWithId(fixture.PriorPath, "conflicting provider bytes", MoveFixture.FileIdentity);
            var previousHook = SyncAuthorityMoveRemoteExecutor.PhaseForTest;
            try
            {
                if (crashAfterReceipt)
                    SyncAuthorityMoveRemoteExecutor.PhaseForTest = phase =>
                    {
                        if (phase == "remote-native-move-identity-conflict-receipt")
                            throw new IOException("isolated crash after durable applied-conflict receipt");
                    };
                if (crashAfterReceipt)
                    Require((await fixture.TryExecuteAsync())?.Contains("isolated crash", StringComparison.Ordinal) == true,
                        "H the interruption occurs after the actual raw receipt commit");
                else
                {
                    var result = await fixture.ExecuteAsync();
                    Require(result.Status == SyncAuthorityOperationStatus.Blocked &&
                            result.Reason.Contains("fresh comparison", StringComparison.Ordinal),
                        "H a successful provider move with conflicting version metadata reports a held comparison");
                }
            }
            finally { SyncAuthorityMoveRemoteExecutor.PhaseForTest = previousHook; }

            Require(fixture.Remote.MoveCount == 1 && await fixture.ReceiptCountAsync() == 1 &&
                    await fixture.LatestReceiptCertaintyAsync() == "known_success" &&
                    await fixture.SyncPointCountAsync() == 0 && await fixture.MembershipCountAsync() == 0,
                "H exactly one applied move and raw success receipt survive without a fabricated baseline/member");
            Require(await ScalarLongAsync(fixture.DatabasePath, "SELECT count(*) FROM object_version;") == 2,
                "H the version conflict creates no phantom or replacement version");
            var raw = await ScalarStringAsync(fixture.DatabasePath,
                "SELECT destination_identity_json FROM effect_receipt LIMIT 1;");
            using (var receipt = JsonDocument.Parse(raw))
            {
                var value = receipt.RootElement;
                Require(value.GetProperty("classification").GetString() ==
                        SyncAuthorityMoveRemoteExecutor.SuccessorIdentityConflict &&
                        value.GetProperty("Id").GetString() == MoveFixture.FileIdentity &&
                        value.GetProperty("RelativePath").GetString() == fixture.DesiredPath &&
                        !value.TryGetProperty("ObjectVersionId", out _) &&
                        !value.TryGetProperty("MembershipId", out _),
                    "H raw receipt binds the actual moved object and contains no invented successor references");
            }

            fixture.Authority = new SyncAuthorityDatabase(fixture.DatabasePath);
            await fixture.Authority.InitializeAsync(InstallationId, CapabilityProfileId, fixture.Slug, "{}");
            if (crashAfterReceipt)
            {
                // Current state deliberately cannot prove the original outcome. Settlement must use
                // only the already durable exact receipt, before local scanning or remote inventory.
                await File.WriteAllTextAsync(fixture.DesiredLocalPath, "later unrelated local contents");
                fixture.Remote.PutWithId(fixture.DesiredPath, "scoped move payload", MoveFixture.FileIdentity);
                var listCount = fixture.Remote.ListEntriesCount;
                var settled = await fixture.ReconcileAsync();
                Require(settled.Status == SyncAuthorityOperationStatus.Blocked &&
                        fixture.Remote.ListEntriesCount == listCount,
                    "H interrupted raw-success classification settles after reopen without current-path inference");
            }
            Require(await fixture.OperationStatusAsync() == "blocked" &&
                    await fixture.AttemptStatusesAsync() == "blocked" &&
                    await fixture.TryExecuteAsync() is not null && fixture.Remote.MoveCount == 1 &&
                    await fixture.ReceiptCountAsync() == 1 && await fixture.SyncPointCountAsync() == 0,
                "H retained blocked authority prevents a second move or a new receipt/baseline after reopen");
        }
    }

    private static async Task<SyncAuthorityCommandAdmission> AdmitFixturePairsAsync(
        SyncAuthorityDatabase authority, params string[] pairIds)
    {
        var admission = await authority.CreateAcceptedCommandAdmissionAsync(null,
            pairIds.Length == 1 ? "run_pair" : "run_all", pairIds.Length == 1 ? pairIds[0] : null,
            null, null, JsonSerializer.Serialize(new { pairIds }), SyncAuthorityCommandPurpose.NormalEffect);
        await authority.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        return admission;
    }

    private sealed class MoveFixture
    {
        internal const string FileIdentity = "id:file-move-source";
        internal const string DirectoryIdentity = "id:dir-move-source";
        internal const string RemoteRootId = "id:folder-move-root";
        internal const string ObjectId = "object-move-source";
        internal const string LocalRootBinding = "root-move-local";
        internal const string DropboxRootBinding = "root-move-dropbox";
        internal const string LocalVersionId = "version-move-local";
        internal const string DropboxVersionId = "version-move-dropbox";

        internal string Slug = "";
        internal string CaseRoot = "";
        internal string LocalRoot = "";
        internal string DesiredLocalPath = "";
        internal string PriorPath = "";
        internal string DesiredPath = "";
        internal string DatabasePath = "";
        internal string PairId = "";
        internal string OperationId = "";
        internal string SourceMemberId = "";
        internal string DestinationMemberId = "";
        internal string AttemptId = "";
        internal SyncAuthorityDatabase Authority = null!;
        internal SyncAuthorityCommandAdmission Admission = null!;
        internal FakeSyncRemoteStore Remote = null!;
        internal SyncPair Pair = null!;
        internal SyncAuthorityLease StartLease = null!;
        private PilotEntryKind Kind;
        private bool _startLeaseReleased;

        internal static async Task<MoveFixture> CreateAsync(string slug, PilotEntryKind kind,
            bool withRequestSentAttempt)
        {
            var fixture = new MoveFixture { Slug = slug, Kind = kind };
            fixture.CaseRoot = Path.Combine(_root, slug);
            Directory.CreateDirectory(fixture.CaseRoot);
            fixture.LocalRoot = Path.Combine(fixture.CaseRoot, "pair-root");
            Directory.CreateDirectory(fixture.LocalRoot);
            fixture.PriorPath = kind == PilotEntryKind.Directory ? "prior" : "prior.txt";
            fixture.DesiredPath = kind == PilotEntryKind.Directory ? "moved" : "moved.txt";
            fixture.DesiredLocalPath = Path.Combine(fixture.LocalRoot,
                fixture.DesiredPath.Replace('/', Path.DirectorySeparatorChar));
            var payload = Encoding.UTF8.GetBytes("scoped move payload");
            if (kind == PilotEntryKind.Directory) Directory.CreateDirectory(fixture.DesiredLocalPath);
            else await File.WriteAllBytesAsync(fixture.DesiredLocalPath, payload);
            PilotDirectoryIdentity rootIdentity;
            using (var rootLease = PilotDirectoryLease.Open(fixture.LocalRoot))
                rootIdentity = rootLease.Identity;
            var localIdentityJson = await LocalIdentityJsonAsync(fixture.LocalRoot, fixture.DesiredPath,
                rootIdentity.VolumeSerialNumber);
            var local = await LocalEntryAsync(fixture.LocalRoot, fixture.DesiredPath);
            fixture.PairId = "pair-native-move-" + slug;
            fixture.OperationId = "operation-native-move-" + slug;
            fixture.SourceMemberId = "member-source-" + slug;
            fixture.DestinationMemberId = "member-destination-" + slug;
            fixture.DatabasePath = Path.Combine(fixture.CaseRoot, "authority.db");
            fixture.Authority = new SyncAuthorityDatabase(fixture.DatabasePath);
            await fixture.Authority.InitializeAsync(InstallationId, CapabilityProfileId, slug, "{}");
            fixture.Pair = new SyncPair(fixture.PairId, "Native move " + slug, fixture.LocalRoot,
                rootIdentity.VolumeSerialNumber, rootIdentity.FileId, "/Move", RemoteRootId);
            await fixture.Authority.RegisterPairAsync(fixture.PairId, 1);
            fixture.Admission = await AdmitFixturePairsAsync(fixture.Authority, fixture.PairId);
            await fixture.Authority.RegisterRootBindingAsync(LocalRootBinding, fixture.PairId, "local", 1,
                JsonSerializer.Serialize(new { volumeSerial = rootIdentity.VolumeSerialNumber,
                    fileId = rootIdentity.FileId }), fixture.LocalRoot);
            await fixture.Authority.RegisterRootBindingAsync(DropboxRootBinding, fixture.PairId, "dropbox", 1,
                "{\"folderId\":\"" + RemoteRootId + "\"}", fixture.Pair.DropboxRoot);
            fixture.Remote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
            var remoteIdentity = kind == PilotEntryKind.Directory ? DirectoryIdentity : FileIdentity;
            if (kind == PilotEntryKind.Directory) fixture.Remote.PutDirectoryWithId(fixture.PriorPath,
                remoteIdentity);
            else fixture.Remote.PutWithId(fixture.PriorPath, "scoped move payload", remoteIdentity);
            await fixture.Authority.RegisterObjectAsync(ObjectId,
                kind == PilotEntryKind.Directory ? "directory" : "file");
            await fixture.Authority.RegisterObjectVersionAsync(LocalVersionId, ObjectId, "local",
                localIdentityJson, local.ContentHash, local.Size);
            await fixture.Authority.RegisterObjectVersionAsync(DropboxVersionId, ObjectId, "dropbox",
                JsonSerializer.Serialize(new { fileId = remoteIdentity, revision = "rev:1" }),
                local.ContentHash, local.Size);
            var preconditions = JsonSerializer.Serialize(new
            {
                observationSetId = "observation-set-" + slug,
                pairId = fixture.PairId,
                kind = "move_remote",
                sourcePath = fixture.DesiredPath,
                priorPath = fixture.PriorPath,
                configurationRevision = 1L
            });
            var members = new[]
            {
                new SyncAuthorityMember(fixture.SourceMemberId, fixture.PairId, ObjectId, "source",
                    LocalRootBinding, null, LocalVersionId, null, fixture.DesiredPath, "{\"present\":true}"),
                new SyncAuthorityMember(fixture.DestinationMemberId, fixture.PairId, ObjectId, "destination",
                    DropboxRootBinding, null, DropboxVersionId, null, fixture.PriorPath,
                    "{\"present\":true}"),
                new SyncAuthorityMember("member-lock-" + slug, fixture.PairId, ObjectId, "lock",
                    DropboxRootBinding, null, null, null, fixture.DesiredPath, "{\"present\":false}")
            };
            await fixture.Authority.CreateOperationAsync(new SyncAuthorityOperationPlan(fixture.OperationId,
                "idempotency-" + fixture.OperationId, SyncAuthorityOperationKind.MoveRemote, fixture.PairId,
                ObjectId, 1, CapabilityProfileId, preconditions, 80, members, [], []), admission: fixture.Admission);
            fixture.StartLease = await fixture.Authority.AcquireOperationLeaseAsync(fixture.OperationId,
                "worker-fixture-" + slug, TimeSpan.FromMinutes(10));
            if (withRequestSentAttempt)
            {
                var attempt = await fixture.Authority.StartDispatchedAttemptAsync(fixture.OperationId,
                    fixture.StartLease, Hash("native-move-scoped-proofs", slug), "dropbox-native-move");
                fixture.AttemptId = attempt.AttemptId;
            }
            if (!withRequestSentAttempt) await fixture.ReleaseStartLeaseAsync();
            return fixture;
        }

        internal PilotEntryKind FixtureKind => Kind;

        internal async Task ReleaseStartLeaseAsync()
        {
            if (_startLeaseReleased) return;
            _startLeaseReleased = true;
            await Authority.ReleaseLeaseAsync(StartLease);
        }

        internal async Task<SyncAuthorityExecutionResult> ExecuteAsync() =>
            await new SyncAuthorityMoveRemoteExecutor(Pair, Remote, Authority)
                .ExecuteAsync(OperationId, "worker-execute-" + Slug);

        internal async Task<SyncAuthorityExecutionResult> ReconcileAsync() =>
            await new SyncAuthorityMoveRemoteReconciler(Pair, Remote, Authority)
                .ReconcileAsync(OperationId, "worker-reconcile-" + Slug);

        internal async Task<string?> TryExecuteAsync()
        {
            try
            {
                await ExecuteAsync();
                return null;
            }
            catch (Exception exception)
            {
                return exception.GetType().Name + ": " + exception.Message;
            }
        }

        internal async Task RecordKnownSuccessReceiptAsync()
        {
            await Authority.RecordReceiptAsync(AttemptId, StartLease,
                SyncAuthorityReceiptCertainty.KnownSuccess,
                JsonSerializer.Serialize(new { fileId = FileIdentity }), "{}",
                "{\"classification\":\"root_move_applied\"}", Hash("receipt", Slug));
            await ReleaseStartLeaseAsync();
        }

        internal Task<string> OperationStatusAsync() => ScalarStringAsync(DatabasePath,
            "SELECT status FROM operation WHERE operation_id='" + OperationId + "';");

        internal Task<string> AttemptStatusesAsync() => ScalarStringAsync(DatabasePath,
            "SELECT group_concat(status,',') FROM operation_attempt WHERE operation_id='" + OperationId + "';");

        internal Task<long> ReceiptCountAsync() => ScalarLongAsync(DatabasePath,
            "SELECT count(*) FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id" +
            " WHERE a.operation_id='" + OperationId + "';");

        internal Task<string> LatestReceiptCertaintyAsync() => ScalarStringAsync(DatabasePath,
            "SELECT r.certainty FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id" +
            " WHERE a.operation_id='" + OperationId + "' ORDER BY r.observed_utc DESC,r.rowid DESC LIMIT 1;");

        internal Task<string> LatestReceiptEvidenceAsync() => ScalarStringAsync(DatabasePath,
            "SELECT r.provider_metadata_json FROM effect_receipt r JOIN operation_attempt a ON" +
            " a.attempt_id=r.attempt_id WHERE a.operation_id='" + OperationId + "'" +
            " ORDER BY r.observed_utc DESC,r.rowid DESC LIMIT 1;");

        internal Task<long> SyncPointCountAsync() => ScalarLongAsync(DatabasePath,
            "SELECT count(*) FROM synchronization_point WHERE pair_id='" + PairId + "';");

        internal Task<long> MembershipCountAsync() => ScalarLongAsync(DatabasePath,
            "SELECT count(*) FROM membership WHERE object_id='" + ObjectId + "';");

        internal Task<string> SuccessorVersionHashAsync() => ScalarStringAsync(DatabasePath,
            "SELECT content_hash FROM object_version WHERE object_id='" + ObjectId + "' AND side='dropbox'" +
            " ORDER BY rowid DESC LIMIT 1;");

        internal Task<string> RetainedStateAsync() => ScalarStringAsync(DatabasePath,
            "SELECT (SELECT status FROM operation WHERE operation_id='" + OperationId + "') || '|' ||" +
            " (SELECT coalesce(group_concat(status,','),'') FROM operation_attempt WHERE operation_id='" +
            OperationId + "') || '|' || (SELECT count(*) FROM effect_receipt r JOIN operation_attempt a ON" +
            " a.attempt_id=r.attempt_id WHERE a.operation_id='" + OperationId + "') || '|' ||" +
            " (SELECT count(*) FROM synchronization_point WHERE pair_id='" + PairId + "');");

        internal async Task<string?> RemoteContentHashAsync(string path) =>
            (await Remote.ListEntriesAsync()).SingleOrDefault(item => item.RelativePath == path)?.ContentHash;

        private static async Task<string> LocalIdentityJsonAsync(string root, string path, uint volumeSerial)
        {
            var entry = await LocalEntryAsync(root, path);
            return JsonSerializer.Serialize(new
            {
                volumeSerial = volumeSerial.ToString("x8", CultureInfo.InvariantCulture),
                fileId = entry.FileId!.Value.ToString("x16", CultureInfo.InvariantCulture)
            });
        }

        private static async Task<PilotLocalEntry> LocalEntryAsync(string root, string path)
        {
            using var lease = PilotDirectoryLease.Open(root);
            var scan = await new PilotLocalScanner().ScanAsync(lease, refreshPaths: [path],
                includePath: ExactPath(path), traverseDirectory: _ => false, contentProvider: "Dropbox");
            return scan.Entries.TryGetValue(path, out var entry)
                ? entry
                : throw new IOException("The fixture local entry was not observed.");
        }
    }

    // ----- E: the real cross-pair outbound direction ---------------------------------------------

    private static async Task CrossPairScopedProofAsync()
    {
        // E1: the provider's own root contents changed after legitimate planning. The move is still applied exactly
        // once, the root known-success receipt is durable before any destination observation, and divergent
        // contents never receive an accepted equal baseline.
        var changed = await CrossPairFixture.CreateAsync("e-changed-content");
        await changed.MoveLocalAsync();
        await changed.ObserveAsync();
        var plan = (await changed.Coordinator().PlanAsync()).Single();
        var beforeEdit = (await changed.SourceRemote.ListEntriesAsync()).Single(entry => entry.RelativePath == changed.SourcePath);
        changed.SourceRemote.Put(changed.SourcePath, "latest provider payload");
        var afterEdit = (await changed.SourceRemote.ListEntriesAsync()).Single(entry => entry.RelativePath == changed.SourcePath);
        Require(afterEdit.Id == beforeEdit.Id && afterEdit.Revision != beforeEdit.Revision,
            "E1 editing source content preserves the stable identity and advances the revision");
        var diverged = await changed.Coordinator().ExecuteAsync(plan, "worker-e1");
        Require(diverged.Status == SyncAuthorityOperationStatus.Blocked,
            "E1 changed provider contents are physically moved into a held state, saw " + diverged.Status);
        Require(changed.SourceRemote.MoveCount == 1 && !changed.SourceRemote.Exists(changed.SourcePath) &&
                changed.DestinationRemote.ReadText(changed.DestinationPath) == "latest provider payload",
            "E1 the changed object was moved exactly once by the provider itself, with no upload or delete");
        Require(await changed.FirstReceiptAsync() is { } rootReceipt &&
                rootReceipt.Contains("root_move_applied", StringComparison.Ordinal),
            "E1 the root known-success receipt is retained before destination observation, saw " +
            await changed.FirstReceiptAsync());
        Require(await changed.LastReceiptAsync() is { } heldReceipt &&
                heldReceipt.Contains("root_effect_applied_destination_subtree_diverged", StringComparison.Ordinal),
            "E1 the divergence is recorded after the retained root receipt, saw " +
            await changed.LastReceiptAsync());
        Require(diverged.Reason.Contains("did not converge", StringComparison.Ordinal) &&
                diverged.Reason.Contains("fresh comparison", StringComparison.Ordinal),
            "E1 the held state reports a successful move whose contents still need comparison: " +
            diverged.Reason);
        Require(await changed.SyncPointCountAsync() == 0,
            "E1 divergent contents never receive an accepted equal baseline");
        Require(changed.DestinationRemote.ReadText(changed.DestinationPath) == "latest provider payload" &&
                changed.SourceRemote.Exists(changed.DestinationPath) == false,
            "E1 the retained content is untouched by the held state");

        // E2: the unchanged legitimate path still completes with exactly one retained root receipt.
        var unchanged = await CrossPairFixture.CreateAsync("e-unchanged-content");
        await unchanged.MoveLocalAsync();
        await unchanged.ObserveAsync();
        var unchangedPlan = (await unchanged.Coordinator().PlanAsync()).Single();
        var completed = await unchanged.Coordinator().ExecuteAsync(unchangedPlan, "worker-e2");
        Require(completed.Status == SyncAuthorityOperationStatus.Completed &&
                unchanged.SourceRemote.MoveCount == 1 && unchanged.SourceRemote.UploadCount == 0 &&
                unchanged.SourceRemote.DeleteCount == 0 &&
                unchanged.DestinationRemote.ReadText(unchanged.DestinationPath) == "cross-pair payload",
            "E2 the unchanged outbound move still completes exactly once without upload or delete, saw " +
            completed.Status);
        Require(await unchanged.SyncPointCountAsync() == 1 &&
                await unchanged.ReceiptCountAsync() == 2,
            "E2 the unchanged convergence accepts one equal baseline after its root receipt");

        // E3: a dispatched request that retained no durable receipt stays unresolved, derives nothing from the
        // current paths, and a delayed completion cannot produce a second network move.
        var lost = await CrossPairFixture.CreateAsync("e-request-sent-no-receipt");
        await lost.MoveLocalAsync();
        await lost.ObserveAsync();
        var lostPlan = (await lost.Coordinator().PlanAsync()).Single();
        SyncAuthorityCrossPairMoveCoordinator.EffectAdmissionPhaseForTest = _ =>
            throw new IOException("simulated process termination after dispatch and before the request");
        var interrupted = await RefusalAsync(() =>
            lost.Coordinator().ExecuteAsync(lostPlan, "worker-e3-crash"));
        SyncAuthorityCrossPairMoveCoordinator.EffectAdmissionPhaseForTest = null;
        var listingsBefore = lost.SourceRemote.ListEntriesCount + lost.DestinationRemote.ListEntriesCount;
        Require(interrupted == "IOException" && lost.SourceRemote.MoveCount == 0,
            "E3 the simulated crash issued no provider move, saw " + interrupted);
        var unresolved = await lost.Coordinator().ExecuteAsync(lostPlan, "worker-e3-reconcile");
        Require(unresolved.Status == SyncAuthorityOperationStatus.EffectIssuing &&
                !unresolved.EffectDispatched &&
                unresolved.Reason.Contains("unresolved", StringComparison.Ordinal),
            "E3 a sent request with no durable receipt stays explicitly unresolved, saw " + unresolved.Status);
        Require(lost.SourceRemote.ListEntriesCount + lost.DestinationRemote.ListEntriesCount ==
                listingsBefore,
            "E3 the unresolved boundary observed no inventory at all");
        // The provider applies the original request after the crash. A retry unlocked by the earlier reading
        // would produce a second network move here.
        var expected = (await lost.SourceRemote.ListEntriesAsync()).Single(item =>
            item.RelativePath == lost.SourcePath);
        await lost.SourceRemote.MoveToAsync(expected, lost.DestinationRemote, lost.DestinationPath);
        var again = await lost.Coordinator().ExecuteAsync(lostPlan, "worker-e3-delayed");
        Require(again.Status == SyncAuthorityOperationStatus.EffectIssuing &&
                again.Reason.Contains("unresolved", StringComparison.Ordinal),
            "E3 the delayed provider completion leaves the operation unresolved, saw " + again.Status);
        Require(lost.SourceRemote.MoveCount == 1 && await lost.ReceiptCountAsync() == 0,
            "E3 the delayed completion enabled no second network move and retained no fabricated receipt");

        // E4: an unknown receipt with the source still present never becomes terminal non-application and
        // never unlocks a retry.
        var unknown = await CrossPairFixture.CreateAsync("e-receipt-unknown");
        await unknown.MoveLocalAsync();
        await unknown.ObserveAsync();
        var unknownPlan = (await unknown.Coordinator().PlanAsync()).Single();
        unknown.SourceRemote.InterruptNextMoveBeforeEffect = true;
        var lostResponse = await unknown.Coordinator().ExecuteAsync(unknownPlan, "worker-e4");
        Require(lostResponse.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                unknown.SourceRemote.MoveCount == 0,
            "E4 the lost response stayed effect-unknown with zero provider moves, saw " + lostResponse.Status);
        var stillUnknown = await unknown.Coordinator().ExecuteAsync(unknownPlan, "worker-e4-reconcile");
        Require(stillUnknown.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                !stillUnknown.EffectDispatched &&
                stillUnknown.Reason.Contains("unresolved", StringComparison.Ordinal),
            "E4 an unknown receipt with the source present is not terminal non-application, saw " +
            stillUnknown.Status);
        Require(await unknown.AttemptStatusesAsync() == "effect_unknown" &&
                await unknown.ReceiptCountAsync() == 1 && unknown.SourceRemote.MoveCount == 0,
            "E4 the unknown receipt stayed the only durable receipt and opened no retry");
        var refusedRetry = await unknown.Coordinator().ExecuteAsync(unknownPlan, "worker-e4-retry");
        Require(refusedRetry.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                unknown.SourceRemote.MoveCount == 0,
            "E4 a further pass never issues a second network move, saw " + refusedRetry.Status);

        // E5: a retained positive known-not-applied receipt is the only authorisation for one fenced retry,
        // and that retry completes the move exactly once while adding no fabricated receipt.
        var retry = await CrossPairFixture.CreateAsync("e-known-not-applied");
        await retry.MoveLocalAsync();
        await retry.ObserveAsync();
        var retryPlan = (await retry.Coordinator().PlanAsync()).Single();
        await retry.RecordKnownNotAppliedAttemptAsync(retryPlan);
        var ready = await retry.Coordinator().ExecuteAsync(retryPlan, "worker-e5-reconcile");
        Require(ready.Status == SyncAuthorityOperationStatus.Ready && !ready.EffectDispatched,
            "E5 the retained known-not-applied receipt opens exactly one fenced retry, saw " + ready.Status);
        Require(await retry.AttemptStatusesAsync() == "verified_not_applied" &&
                await retry.ReceiptCountAsync() == 1 && retry.SourceRemote.MoveCount == 0,
            "E5 the settled receipt opened the retry without issuing an effect or a second receipt");
        var retried = await retry.Coordinator().ExecuteAsync(retryPlan, "worker-e5-retry");
        Require(retried.Status == SyncAuthorityOperationStatus.Completed &&
                retry.SourceRemote.MoveCount == 1 &&
                !retry.SourceRemote.Exists(retry.SourcePath) &&
                retry.DestinationRemote.ReadText(retry.DestinationPath) == "cross-pair payload",
            "E5 the one permitted retry completed the provider move exactly once, saw " + retried.Status);
    }

    // ----- F: both real post-delete branches ------------------------------------------------------

    private static async Task PostDeleteIdentityAsync()
    {
        // F1: the directory deletion branch asks the one bounded identity question and spends no root listing
        // on the post-delete proof. The single listing here is the unchanged pre-delete descendant check.
        var directory = await PostDeleteFixture.CreateDirectoryAsync("f-directory");
        var deleted = await directory.ExecuteAsync();
        Require(directory.IsDirectory,
            "F1 the directory branch fixture really retains a retained folder identity");
        Require(deleted.Status == SyncAuthorityOperationStatus.Completed,
            "F1 the empty folder deletion completed, saw " + deleted.Status + ": " + deleted.Reason);
        Require(directory.Store!.ObjectExistsCalls == 1 &&
                directory.Store!.ObjectExistsIds.Single() == "id:folder",
            "F1 the post-delete proof asked exactly one bounded identity question, saw " +
            directory.Store.ObjectExistsCalls + " calls");
        Require(directory.Inner.ListEntriesCount == 1,
            "F1 the only listing was the unchanged pre-delete descendant check, saw " +
            directory.Inner.ListEntriesCount);
        Require(!directory.Inner.DirectoryExists("folder"),
            "F1 the exact folder identity was removed after the bounded absence proof");
        Require(directory.Store!.ListFallbackCalls == 1,
            "F1 the single pre-delete descendant listing was the only listing; post-delete used exact identity lookup");

        // F2: a surviving identity still refuses, so the bounded proof is not a weaker one.
        var surviving = await PostDeleteFixture.CreateDirectoryAsync("f-surviving");
        surviving.Survivor = true;
        var refused = await RefusalAsync(() => surviving.ExecuteAsync());
        Require(refused == "DropboxEffectUncertainException" &&
                surviving.Store!.ObjectExistsCalls == 1 &&
                await ScalarStringAsync(surviving.DatabasePath,
                    "SELECT status FROM operation WHERE operation_id='" + surviving.OperationId + "';") != "completed",
            "F2 a provider-reported surviving identity refuses the deletion commit, saw " + refused);
        Require(await ScalarLongAsync(surviving.DatabasePath,
                "SELECT count(*) FROM operation_attempt WHERE operation_id='" + surviving.OperationId + "';") == 1,
            "F2 contradictory post-delete identity evidence never causes a second attempt");

        // F3: the file deletion branch also uses the bounded identity question and never lists the root.
        var file = await PostDeleteFixture.CreateFileAsync("f-file");
        var fileDeleted = await file.ExecuteAsync();
        Require(fileDeleted.Status == SyncAuthorityOperationStatus.Completed,
            "F3 the conditional file deletion completed, saw " + fileDeleted.Status + ": " +
            fileDeleted.Reason);
        Require(file.Store!.ObjectExistsCalls == 1 && file.Store!.ObjectExistsIds.Single() == "id:file",
            "F3 the file branch's post-delete proof asked exactly one bounded identity question, saw " +
            file.Store.ObjectExistsCalls + " calls");
        Require(file.Inner.ListEntriesCount == 0,
            "F3 the file branch spent no root listing on its post-delete proof, saw " +
            file.Inner.ListEntriesCount);

        // F4: an unsupported store keeps its conservative listing fallback for both branches.
        var fallbackDirectory = await PostDeleteFixture.CreateDirectoryAsync("f-fallback-directory",
            withIdentityLookup: false);
        var fallbackDeleted = await fallbackDirectory.ExecuteAsync();
        Require(fallbackDeleted.Status == SyncAuthorityOperationStatus.Completed &&
                fallbackDirectory.Inner.ListEntriesCount == 2,
            "F4 the unsupported store keeps its two-listing conservative absence proof, saw " +
            fallbackDirectory.Inner.ListEntriesCount + " listings and " + fallbackDeleted.Status);
        var fallbackFile = await PostDeleteFixture.CreateFileAsync("f-fallback-file",
            withIdentityLookup: false);
        var fallbackFileDeleted = await fallbackFile.ExecuteAsync();
        Require(fallbackFileDeleted.Status == SyncAuthorityOperationStatus.Completed &&
                fallbackFile.Inner.ListEntriesCount == 1,
            "F4 the unsupported store's file branch keeps its listing fallback, saw " +
            fallbackFile.Inner.ListEntriesCount + " listings and " + fallbackFileDeleted.Status);
    }

    private sealed class CountingIdentityLookupStore(ISyncRemoteStore inner, Func<string, bool> exists)
        : ISyncRemoteStore, ISyncRemoteObjectIdentityLookup
    {
        internal int ObjectExistsCalls;
        internal int ListFallbackCalls;
        internal readonly List<string> ObjectExistsIds = [];

        public bool SupportsConditionalDirectoryDelete => inner.SupportsConditionalDirectoryDelete;

        public Task<bool> ObjectExistsAsync(string providerObjectId,
            CancellationToken cancellationToken = default)
        {
            ObjectExistsCalls++;
            ObjectExistsIds.Add(providerObjectId);
            return Task.FromResult(exists(providerObjectId));
        }

        public Task VerifyRootIdentityAsync(CancellationToken cancellationToken = default) =>
            inner.VerifyRootIdentityAsync(cancellationToken);

        public Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesAsync(
            CancellationToken cancellationToken = default)
        {
            ListFallbackCalls++;
            return inner.ListEntriesAsync(cancellationToken);
        }

        public Task<PilotUploadResult> UploadCreateOnlyAsync(string relativePath, Stream source,
            string expectedContentHash, CancellationToken cancellationToken = default) =>
            ((IPilotRemoteStore)inner).UploadCreateOnlyAsync(relativePath, source, expectedContentHash,
                cancellationToken);

        public Task<PilotRemoteEntry> ReadEntryAsync(string relativePath, string providerObjectId,
            CancellationToken cancellationToken = default) =>
            inner.ReadEntryAsync(relativePath, providerObjectId, cancellationToken);

        public Task<PilotRemoteEntry?> FindEntryAtPathAsync(string relativePath,
            CancellationToken cancellationToken = default) =>
            inner.FindEntryAtPathAsync(relativePath, cancellationToken);

        public Task VerifyControlNamespaceAsync(SyncRemoteControlNamespaceBinding binding,
            string installationId, string namespaceId, CancellationToken cancellationToken = default) =>
            inner.VerifyControlNamespaceAsync(binding, installationId, namespaceId, cancellationToken);

        public Task<PilotUploadResult> UploadAsync(string relativePath, Stream source,
            string expectedContentHash, string? expectedRemoteRevision,
            CancellationToken cancellationToken = default) =>
            inner.UploadAsync(relativePath, source, expectedContentHash, expectedRemoteRevision,
                cancellationToken);

        public Task<SyncDownloadResult> DownloadAsync(PilotRemoteEntry expected, Stream destination,
            CancellationToken cancellationToken = default) =>
            inner.DownloadAsync(expected, destination, cancellationToken);

        public Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected, string recoveryPath,
            CancellationToken cancellationToken = default) =>
            inner.CopyToRecoveryAsync(expected, recoveryPath, cancellationToken);

        public Task<SyncArchiveResult?> GetRecoveryAsync(string recoveryPath,
            CancellationToken cancellationToken = default) =>
            inner.GetRecoveryAsync(recoveryPath, cancellationToken);

        public Task DeleteAsync(PilotRemoteEntry expected, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(expected, cancellationToken);

        public Task VerifyRecoveryRootAsync(SyncRemoteRecoveryRootBinding binding,
            CancellationToken cancellationToken = default) =>
            inner.VerifyRecoveryRootAsync(binding, cancellationToken);

        public Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected,
            SyncRemoteRecoveryRootBinding binding, string deterministicName,
            CancellationToken cancellationToken = default) =>
            inner.CopyToRecoveryAsync(expected, binding, deterministicName, cancellationToken);

        public Task<SyncArchiveResult?> GetRecoveryAsync(SyncRemoteRecoveryRootBinding binding,
            string deterministicName, CancellationToken cancellationToken = default) =>
            inner.GetRecoveryAsync(binding, deterministicName, cancellationToken);
    }

    private sealed class PostDeleteFixture
    {
        internal string CaseRoot = "";
        internal string DatabasePath = "";
        internal string PairId = "";
        internal string OperationId = "";
        internal SyncPair Pair = null!;
        internal SyncAuthorityDatabase Authority = null!;
        internal SyncAuthorityCommandAdmission Admission = null!;
        internal FakeSyncRemoteStore Inner = null!;
        internal CountingIdentityLookupStore? Store;
        private ISyncRemoteStore _ExecutorStore = null!;
        private bool _directory = false;
        private string _objectId = "";
        private string _identity = "";
        internal bool Survivor;

        internal bool IsDirectory => _directory;

        internal static Task<PostDeleteFixture> CreateDirectoryAsync(string slug,
            bool withIdentityLookup = true) => CreateAsync(slug, directory: true, withIdentityLookup);

        internal static Task<PostDeleteFixture> CreateFileAsync(string slug,
            bool withIdentityLookup = true) => CreateAsync(slug, directory: false, withIdentityLookup);

        private static async Task<PostDeleteFixture> CreateAsync(string slug, bool directory,
            bool withIdentityLookup)
        {
            var fixture = new PostDeleteFixture { _directory = directory };
            fixture.CaseRoot = Path.Combine(_root, slug);
            Directory.CreateDirectory(fixture.CaseRoot);
            fixture.PairId = "pair-post-delete-" + slug;
            fixture.OperationId = "operation-post-delete-" + slug;
            fixture._objectId = "object-post-delete-" + slug;
            fixture._identity = directory ? "id:folder" : "id:file";
            fixture.DatabasePath = Path.Combine(fixture.CaseRoot, "authority.db");
            fixture.Authority = new SyncAuthorityDatabase(fixture.DatabasePath);
            await fixture.Authority.InitializeAsync(InstallationId, CapabilityProfileId, slug, "{}");
            // The shared fixture's exact-path metadata mode models Dropbox's own get_metadata lookup, so the
            // retained identity read never enumerates the tree and every listing counted here is a deliberate
            // inventory consumer.
            fixture.Inner = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
            var contentHash = directory
                ? null
                : await DropboxHashAsync(Encoding.UTF8.GetBytes("post-delete payload"));
            if (directory) fixture.Inner.PutDirectoryWithId("folder", "id:folder");
            else fixture.Inner.PutWithId("file.txt", "post-delete payload", "id:file");
            fixture.Pair = new SyncPair(fixture.PairId, "Post-delete " + slug,
                Path.Combine(fixture.CaseRoot, "local"), 1, 1, "/Pilot", "id:pilot-folder")
            {
                RecoveryRoot = directory
                    ? null
                    : new SyncRemoteRecoveryRootBinding(SyncDropboxArtifactPaths.RecoveryContainerPath,
                        "id:recovery-root", "root-namespace")
            };
            Directory.CreateDirectory(fixture.Pair.LocalRoot);
            await fixture.Authority.RegisterPairAsync(fixture.PairId, 1);
            fixture.Admission = await AdmitFixturePairsAsync(fixture.Authority, fixture.PairId);
            await fixture.Authority.RegisterRootBindingAsync("root-post-delete-" + slug, fixture.PairId,
                "dropbox", 1, "{\"folderId\":\"id:pilot-folder\"}", "/Pilot");
            await fixture.Authority.RegisterObjectAsync(fixture._objectId,
                directory ? "directory" : "file");
            await fixture.Authority.RegisterObjectVersionAsync("version-post-delete-" + slug,
                fixture._objectId, "dropbox",
                JsonSerializer.Serialize(new { fileId = fixture._identity, revision = directory ? null : "rev:1" }),
                contentHash, directory ? 0 : Encoding.UTF8.GetByteCount("post-delete payload"));
            await fixture.Authority.RecordTombstoneAsync("tombstone-post-delete-" + slug, fixture.PairId,
                fixture._objectId, "dropbox", null, null, 2);
            var members = new[]
            {
                new SyncAuthorityMember("member-post-delete-" + slug, fixture.PairId, fixture._objectId,
                    "source", "root-post-delete-" + slug, null, "version-post-delete-" + slug, null,
                    directory ? "folder" : "file.txt", "{}")
            };
            var allocations = directory
                ? Array.Empty<SyncAuthorityAllocation>()
                : new[]
                {
                    new SyncAuthorityAllocation("allocation-post-delete-" + slug,
                        "control-post-delete-" + slug, SyncAuthorityAllocationKind.RemoteRecoveryFile,
                        "recovery-name-post-delete-" + slug, "id:recovery-root", Hash("nonce-post-delete", slug))
                };
            await fixture.Authority.CreateOperationAsync(new SyncAuthorityOperationPlan(
                fixture.OperationId, "idempotency-" + fixture.OperationId,
                SyncAuthorityOperationKind.DeleteRemote, fixture.PairId, fixture._objectId, 1,
                CapabilityProfileId,
                JsonSerializer.Serialize(new { tombstoneId = "tombstone-post-delete-" + slug }), 100,
                members, [], allocations), admission: fixture.Admission);
            fixture.Store = withIdentityLookup
                ? new CountingIdentityLookupStore(fixture.Inner, fixture.ExistsById)
                : null;
            fixture._ExecutorStore = withIdentityLookup ? fixture.Store! : fixture.Inner;
            return fixture;
        }

        private bool ExistsById(string providerObjectId) => Survivor && providerObjectId == _identity;

        internal Task<SyncAuthorityExecutionResult> ExecuteAsync() =>
            new SyncAuthorityDeleteRemoteExecutor(Pair, _ExecutorStore, Authority)
                .ExecuteAsync(OperationId, "worker-post-delete");
    }

    private static async Task<string> DropboxHashAsync(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        return await DropboxContentHash.ComputeAsync(stream);
    }

    private sealed class CrossPairFixture
    {
        internal SyncPair SourcePair = null!;
        internal SyncPair DestinationPair = null!;
        internal FakeSyncRemoteStore SourceRemote = null!;
        internal FakeSyncRemoteStore DestinationRemote = null!;
        internal SyncAuthorityDatabase Authority = null!;
        internal SyncAuthorityCommandAdmission Admission = null!;
        internal string SourcePath = "";
        internal string DestinationPath = "";
        internal string CaseRoot = "";
        internal string DatabasePath = "";
        private string _sourceRoot = "";
        private string _destinationRoot = "";
        private string _sourceDropboxRoot = "";
        private string _destinationDropboxRoot = "";
        private SyncAuthorityObservationCoordinator _sourceObserver = null!;
        private SyncAuthorityObservationCoordinator _destinationObserver = null!;

        internal static async Task<CrossPairFixture> CreateAsync(string slug)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var fixture = new CrossPairFixture();
            fixture.CaseRoot = Path.Combine(_root, slug);
            fixture._sourceRoot = Path.Combine(fixture.CaseRoot, "source-root");
            fixture._destinationRoot = Path.Combine(fixture.CaseRoot, "destination-root");
            Directory.CreateDirectory(fixture._sourceRoot);
            Directory.CreateDirectory(fixture._destinationRoot);
            fixture.SourcePath = "source.txt";
            fixture.DestinationPath = "destination.txt";
            await File.WriteAllTextAsync(Path.Combine(fixture._sourceRoot, fixture.SourcePath),
                "cross-pair payload");
            PilotDirectoryIdentity sourceIdentity;
            PilotDirectoryIdentity destinationIdentity;
            using (var root = PilotDirectoryLease.Open(fixture._sourceRoot)) sourceIdentity = root.Identity;
            using (var root = PilotDirectoryLease.Open(fixture._destinationRoot))
                destinationIdentity = root.Identity;
            SyncLocalControlNamespaceBinding sourceControl;
            SyncLocalControlNamespaceBinding destinationControl;
            using (var root = PilotDirectoryLease.Open(fixture._sourceRoot))
                sourceControl = SyncControlNamespace.BindLocalForTest(root, InstallationId,
                    "control-cross-" + slug + "-source");
            using (var root = PilotDirectoryLease.Open(fixture._destinationRoot))
                destinationControl = SyncControlNamespace.BindLocalForTest(root, InstallationId,
                    "control-cross-" + slug + "-destination");
            fixture.SourcePair = new SyncPair("pair-cross-" + slug + "-source", "Cross source " + slug,
                fixture._sourceRoot, sourceIdentity.VolumeSerialNumber, sourceIdentity.FileId,
                "/CrossSource", "id:folder-cross-source-" + slug)
            {
                ControlNamespace = new SyncControlNamespaceBinding(InstallationId,
                    "control-cross-" + slug + "-source", sourceControl),
                RecoveryRoot = new SyncRemoteRecoveryRootBinding(
                    SyncDropboxArtifactPaths.ProductContainerPath + "/CrossRecovery",
                    "id:folder-cross-recovery-" + slug, "root-namespace-cross")
            };
            fixture.DestinationPair = new SyncPair("pair-cross-" + slug + "-destination",
                "Cross destination " + slug, fixture._destinationRoot,
                destinationIdentity.VolumeSerialNumber, destinationIdentity.FileId, "/CrossDestination",
                "id:folder-cross-destination-" + slug)
            {
                ControlNamespace = new SyncControlNamespaceBinding(InstallationId,
                    "control-cross-" + slug + "-destination", destinationControl)
            };
            fixture._sourceDropboxRoot = "root-cross-" + slug + "-source-dropbox";
            fixture._destinationDropboxRoot = "root-cross-" + slug + "-destination-dropbox";
            fixture.DatabasePath = Path.Combine(fixture.CaseRoot, "authority.db");
            fixture.Authority = new SyncAuthorityDatabase(fixture.DatabasePath);
            await fixture.Authority.InitializeAsync(InstallationId, CapabilityProfileId, slug, "{}");
            await fixture.Authority.RegisterPairAsync(fixture.SourcePair.Id, 1);
            await fixture.Authority.RegisterPairAsync(fixture.DestinationPair.Id, 1);
            fixture.Admission = await AdmitFixturePairsAsync(fixture.Authority, fixture.SourcePair.Id, fixture.DestinationPair.Id);
            await fixture.Authority.RegisterRootBindingAsync("root-cross-" + slug + "-source-local",
                fixture.SourcePair.Id, "local", 1,
                JsonSerializer.Serialize(new { sourceIdentity.VolumeSerialNumber, sourceIdentity.FileId }),
                fixture._sourceRoot);
            await fixture.Authority.RegisterRootBindingAsync(fixture._sourceDropboxRoot,
                fixture.SourcePair.Id, "dropbox", 1,
                JsonSerializer.Serialize(new { folderId = fixture.SourcePair.DropboxFolderId }),
                fixture.SourcePair.DropboxRoot);
            await fixture.Authority.RegisterRootBindingAsync("root-cross-" + slug + "-destination-local",
                fixture.DestinationPair.Id, "local", 1,
                JsonSerializer.Serialize(new
                    { destinationIdentity.VolumeSerialNumber, destinationIdentity.FileId }),
                fixture._destinationRoot);
            await fixture.Authority.RegisterRootBindingAsync(fixture._destinationDropboxRoot,
                fixture.DestinationPair.Id, "dropbox", 1,
                JsonSerializer.Serialize(new { folderId = fixture.DestinationPair.DropboxFolderId }),
                fixture.DestinationPair.DropboxRoot);
            fixture.SourceRemote = new FakeSyncRemoteStore();
            fixture.DestinationRemote = new FakeSyncRemoteStore();
            fixture.SourceRemote.Put(fixture.SourcePath, "cross-pair payload");
            fixture._sourceObserver = new SyncAuthorityObservationCoordinator(fixture.SourcePair,
                fixture.SourceRemote, fixture.Authority, "root-cross-" + slug + "-source-local",
                fixture._sourceDropboxRoot);
            fixture._destinationObserver = new SyncAuthorityObservationCoordinator(fixture.DestinationPair,
                fixture.DestinationRemote, fixture.Authority, "root-cross-" + slug + "-destination-local",
                fixture._destinationDropboxRoot);
            await fixture._sourceObserver.ObserveAsync("initial");
            await fixture._sourceObserver.ObserveAsync("initial");
            await fixture._destinationObserver.ObserveAsync("initial");
            await fixture._destinationObserver.ObserveAsync("initial");
            var adopted = await new SyncAuthorityInitialPlanner(fixture.Authority)
                .AdoptAsync(fixture.SourcePair.Id);
            Require(adopted.Count == 1 && adopted.Single().Kind == SyncDecisionKind.Baseline,
                "E the cross-pair fixture did not establish its effect-free baseline");
            return fixture;
        }

        internal SyncAuthorityCrossPairMoveCoordinator Coordinator() => new(SourcePair, SourceRemote,
            new SyncAuthorityPlannerOptions(1, CapabilityProfileId, "control-" + SourcePair.Id,
                "control-" + SourcePair.Id, EnableDeletionEffects: true, EnableMoveEffects: true),
            DestinationPair, DestinationRemote,
            new SyncAuthorityPlannerOptions(1, CapabilityProfileId, "control-" + DestinationPair.Id,
                "control-" + DestinationPair.Id, EnableDeletionEffects: true, EnableMoveEffects: true),
            Authority, Admission);

        internal Task MoveLocalAsync()
        {
            File.Move(Path.Combine(_sourceRoot, SourcePath), Path.Combine(_destinationRoot, DestinationPath));
            return Task.CompletedTask;
        }

        internal async Task ObserveAsync()
        {
            await _sourceObserver.ObserveAsync("monitor");
            await _destinationObserver.ObserveAsync("monitor");
        }

        internal async Task RecordKnownNotAppliedAttemptAsync(SyncAuthorityCrossPairMovePlan plan)
        {
            await Authority.MarkOperationGroupActiveAsync(plan.OperationGroupId);
            var lease = await Authority.AcquireOperationLeaseAsync(plan.OperationId,
                "worker-fixture-" + plan.OperationId, TimeSpan.FromMinutes(10));
            var attempt = await Authority.StartDispatchedAttemptAsync(plan.OperationId, lease,
                Hash("cross-pair-known-not-applied", plan.OperationId, plan.SourcePath),
                "dropbox-cross-root-native-move");
            await Authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownNotApplied, "{}", "{}",
                "{\"classification\":\"provider_rejected\"}",
                Hash("cross-pair-not-applied", attempt.AttemptId));
            await Authority.ReleaseLeaseAsync(lease);
        }

        internal Task<string> AttemptStatusesAsync() => ScalarStringAsync(DatabasePath,
            "SELECT coalesce(group_concat(status,','),'') FROM operation_attempt WHERE operation_id IN" +
            " (SELECT operation_id FROM operation WHERE pair_id='" + SourcePair.Id + "');");

        internal Task<long> ReceiptCountAsync() => ScalarLongAsync(DatabasePath,
            "SELECT count(*) FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id" +
            " WHERE a.operation_id IN (SELECT operation_id FROM operation WHERE pair_id='" +
            SourcePair.Id + "');");

        internal Task<long> SyncPointCountAsync() => ScalarLongAsync(DatabasePath,
            "SELECT count(*) FROM synchronization_point WHERE pair_id='" + DestinationPair.Id + "';");

        internal Task<string> FirstReceiptAsync() => ScalarStringAsync(DatabasePath,
            "SELECT r.certainty || '|' || r.provider_metadata_json FROM effect_receipt r JOIN" +
            " operation_attempt a ON a.attempt_id=r.attempt_id WHERE a.operation_id IN (SELECT" +
            " operation_id FROM operation WHERE pair_id='" + SourcePair.Id + "') ORDER BY r.rowid LIMIT 1;");

        internal Task<string> LastReceiptAsync() => ScalarStringAsync(DatabasePath,
            "SELECT r.certainty || '|' || r.provider_metadata_json FROM effect_receipt r JOIN" +
            " operation_attempt a ON a.attempt_id=r.attempt_id WHERE a.operation_id IN (SELECT" +
            " operation_id FROM operation WHERE pair_id='" + SourcePair.Id + "') ORDER BY r.rowid DESC LIMIT 1;");
    }
}

