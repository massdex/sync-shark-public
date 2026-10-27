using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// CC2G focused proofs for the shared cloud object overlap correction.
//
// This fixture drives the REAL Dropbox metadata readers and the REAL two-pass Dropbox hierarchy readback over
// deterministic HTTP protocol fixtures, the REAL provider-qualified object-key model and the REAL shared root
// comparison, and the REAL typed cloud endpoint set, observation coordinator and isolated SQLite authority
// state. The provider side is synthetic; no live provider, credential, transport or installed database is
// reachable, and every fixture wire refuses a route it does not serve. Nothing here claims effect dispatch,
// a real transport or Host completion.
internal static class SharedCloudObjectOverlapChecks
{
    private const string InstallationId = "installation-shared-cloud-overlap-316";
    private const string SourceRevision = "shared-object-overlap-316";
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated shared cloud object overlap fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;

        await ObjectKeyModelAsync();
        await DropboxMetadataAliasAsync();
        await MountedRequestCompletenessAsync();
        await DropboxHierarchyAliasAsync();
        await RootOverlapComparisonAsync();
        await RootOverlapRegistrationAsync();
        await InventoryOverlapGuardAsync();

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "SharedCloudObjectOverlapChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "shared-cloud-object-overlap-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS shared cloud object overlap: " + _assertions +
            " assertions; real Dropbox mount-alias metadata and two-pass hierarchy readback over HTTP protocol" +
            " fixtures, the shared provider-qualified object-key model and root comparison, and pre-observation" +
            " inventory overlap refusal over real isolated SQLite state.");
    }

    // ----- 1. the provider-qualified object-key model ---------------------------------------------

    private static Task ObjectKeyModelAsync()
    {
        Claim("CC2G-1/keys", "One provider-qualified object key never aliases across providers or key kinds");
        var native = SyncCloudObjectKey.Native(SyncProviders.Dropbox, "id:shared");
        var nativeAgain = SyncCloudObjectKey.Native(SyncProviders.Dropbox, "id:shared");
        var nativeOtherProvider = SyncCloudObjectKey.Native(SyncProviders.GoogleDrive, "id:shared");
        var nativeOtherCase = SyncCloudObjectKey.Native(SyncProviders.Dropbox, "ID:Shared");
        var mount = SyncCloudObjectKey.SharedNamespaceMount(SyncProviders.Dropbox, "84528192421");
        var mountAgain = SyncCloudObjectKey.SharedNamespaceMount(SyncProviders.Dropbox, "84528192421");
        var mountOtherCase = SyncCloudObjectKey.SharedNamespaceMount(SyncProviders.Dropbox, "8452819242A");
        Require(SyncCloudObjectKeys.SameObject(native, nativeAgain), "the same provider and exact value is one object");
        Require(!SyncCloudObjectKeys.SameObject(native, nativeOtherProvider),
            "one identical literal id observed on two different providers never aliases");
        Require(!SyncCloudObjectKeys.SameObject(native, nativeOtherCase),
            "object keys compare their value ordinal and case-sensitive");
        Require(!SyncCloudObjectKeys.SameObject(native, mount),
            "a native object key and a route-issued mount key are different key kinds");
        Require(SyncCloudObjectKeys.SameObject(mount, mountAgain) &&
                !SyncCloudObjectKeys.SameObject(mount, mountOtherCase),
            "a route-issued mount key is ordinal and case-sensitive too");
        Require(SyncCloudObjectKeys.Intersects(new[] { native, mount }, new[] { mountAgain }),
            "a root's own mount key intersects another route's own mount key");
        Require(!SyncCloudObjectKeys.Intersects(new[] { mount }, new[] { nativeOtherProvider }),
            "keys of two different providers never intersect");
        Require(!SyncCloudObjectKeys.Intersects(null, new[] { mount }) &&
                !SyncCloudObjectKeys.Intersects(new[] { mount }, null),
            "a missing key set intersects nothing");
        Require(SyncCloudObjectKeySemantics.IssuesRouteScopedObjectAliases(SyncProviders.Dropbox) &&
                !SyncCloudObjectKeySemantics.IssuesRouteScopedObjectAliases(SyncProviders.GoogleDrive),
            "only the route-scoped provider issues mount aliases that must be covered");
        Require(SyncCloudObjectKeySemantics.RequiredCoverage(SyncProviders.Dropbox) ==
                SyncCloudObjectKeyCoverage.MountAliasesComplete &&
                SyncCloudObjectKeySemantics.RequiredCoverage(SyncProviders.GoogleDrive) ==
                SyncCloudObjectKeyCoverage.NativeOnly,
            "each provider declares the object-key coverage a complete readback must carry");
        var combined = SyncCloudObjectKeys.Combine(SyncProviders.Dropbox, "id:mount", new[] { mount });
        Require(combined.Count == 2 && combined[0].Kind == SyncCloudObjectKeyKind.NativeObject &&
                combined[0].Value == "id:mount" && combined[1].Kind == SyncCloudObjectKeyKind.SharedNamespaceMount,
            "one item's key set is its own native key plus every alias read back for it");
        Require(Refused(() => SyncCloudObjectKeys.Combine(SyncProviders.Dropbox, "id:mount",
                new[] { nativeOtherProvider })),
            "an alias naming another provider than its own item is refused");
        return Task.CompletedTask;
    }

    // ----- 2. the real Dropbox metadata readers ---------------------------------------------------

    private static async Task DropboxMetadataAliasAsync()
    {
        Claim("CC2G-1/metadata",
            "The real Dropbox listing keeps native ids and retains only the actual mount alias read back");
        var wire = new DropboxObjectWire("/Pair", "id:pair-root", "dbid-list", "ns-list", "ns-list-home")
            { PageSize = 1 };
        wire.Add("/Pair/Mounted", "id:mounted", sharedFolderId: "shared-777");
        wire.Add("/Pair/Mounted/Child", "id:mounted-child", parentSharedFolderId: "shared-777");
        wire.Add("/Pair/Ordinary", "id:ordinary");
        wire.AddFile("/Pair/ordinary.txt", "id:file-one", "rev-1", 7, new string('a', 64));
        using var http = new HttpClient(wire);
        var binding = DropboxBinding("dbid-list", "ns-list", "ns-list-home");
        var client = new DropboxPilotClient(http, new SyntheticTokens(), "/Pair", binding, "id:pair-root");
        var entries = await client.ListEntriesAsync();
        Require(entries.Select(item => item.RelativePath).OrderBy(item => item, StringComparer.Ordinal)
                .SequenceEqual(new[] { "Mounted", "Mounted/Child", "Ordinary", "ordinary.txt" }),
            "the real recursive listing returns every actual child exactly once");
        var mounted = entries.Single(item => item.RelativePath == "Mounted");
        Require(mounted.Kind == PilotEntryKind.Directory && mounted.Id == "id:mounted",
            "the mounted folder keeps its native request/mount id unchanged");
        Require(mounted.ObjectAliases is { Count: 1 } &&
                mounted.ObjectAliases[0].Provider == SyncProviders.Dropbox &&
                mounted.ObjectAliases[0].Kind == SyncCloudObjectKeyKind.SharedNamespaceMount &&
                mounted.ObjectAliases[0].Value == "shared-777",
            "the mounted folder retains sharing_info.shared_folder_id as an additional provider-qualified key");
        var child = entries.Single(item => item.RelativePath == "Mounted/Child");
        Require(child.ObjectAliases is null,
            "a folder merely CONTAINED by a shared folder never gets its ancestor's parent_shared_folder_id as its own alias");
        Require(entries.Single(item => item.RelativePath == "Ordinary").ObjectAliases is null &&
                entries.Single(item => item.RelativePath == "ordinary.txt").ObjectAliases is null,
            "ordinary folders and files remain their native object key only");
        Require(wire.ForbiddenCalls == 0 && wire.MetadataCalls >= 2 && wire.ListCalls == 1 &&
                wire.ContinueCalls == 3 && wire.AcceptedRecursiveMountedRequests == 1,
            "the actual recursive mounted-folder request and three exact continuation pages used reads only");
        Require(wire.SharedFolderIdCalls == 1,
            "exactly the one mounted folder contributed a shared-namespace key");
    }

    private static async Task MountedRequestCompletenessAsync()
    {
        Claim("H1/Q3/request-completeness",
            "Actual Dropbox mounted recursive paging must complete before real observation can publish disappearance authority");
        var left = Dropbox("mounted-request", "id:mounted-request-root", "/Mounted Request");
        var right = Drive("mounted-request", "drive-mounted-request-root");
        var fixture = await ObservationAsync("mounted-request", left, right, [], []);
        var wire = new DropboxObjectWire(left.FolderPath, left.FolderId, left.AccountId,
            left.RootNamespaceId, left.HomeNamespaceId) { PageSize = 1 };
        wire.Add(left.FolderPath + "/Mounted", "id:request-mount", "shared-request");
        wire.AddFile(left.FolderPath + "/Mounted/child.txt", "id:request-child", "rev-request", 7,
            new string('a', 64));
        using var mutations = new MountedRequestMutation(wire);
        using var http = new HttpClient(mutations);
        var binding = DropboxBinding(left.AccountId, left.RootNamespaceId, left.HomeNamespaceId);
        var client = new DropboxPilotClient(http, new SyntheticTokens(), left.FolderPath, binding, left.FolderId);
        var set = SyncAuthorityCloudEndpointSet.FromPair(fixture.Pair, client, fixture.RightStore.Store,
            "root-left-mounted-request", "root-right-mounted-request", left.ProviderAccountId, right.ProviderAccountId);
        var observer = new SyncAuthorityObservationCoordinator(fixture.Pair, client, fixture.Authority,
            "root-left-mounted-request", "root-right-mounted-request", cloudEndpoints: set);
        await observer.ObserveAsync("initial");
        var initial = await fixture.Authority.LoadLatestCompleteObservationAsync(fixture.Pair.Id,
            SyncAuthorityCloudSides.LogicalLeft);
        Require(initial.Select(item => item.RelativePath).OrderBy(path => path, StringComparer.Ordinal)
                .SequenceEqual(new[] { "Mounted", "Mounted/child.txt" }) &&
                wire.AcceptedRecursiveMountedRequests == 1 && wire.ContinueCalls == 1,
            "the real adapter and observation retain mounted descendants from both complete pages");
        var initialIds = initial.Select(item => (item.RelativePath, item.ObjectId, item.ObjectVersionId,
            item.MembershipId, item.ScanGenerationId)).ToArray();
        var observations = await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM observation;");
        var paths = await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_path_index;");
        foreach (var mode in new[] { "omit-mounted", "false-mounted", "malformed-mounted", "false-recursive",
                     "missing-cursor", "invalid-continuation", "malformed-mount-alias" })
        {
            mutations.Mode = mode;
            wire.MissingCursor = mode == "missing-cursor";
            wire.RefuseContinuation = mode == "invalid-continuation";
            wire.MalformedAliasFolderId = mode == "malformed-mount-alias" ? "id:request-mount" : null;
            var refused = false;
            try { await observer.ObserveAsync("monitor"); }
            catch (Exception error) when (error is DropboxApiException or InvalidDataException or
                                               InvalidOperationException or IOException or JsonException)
            { refused = true; }
            Require(refused, mode + ": incomplete mounted input is refused by the actual adapter/observation path");
            var reopened = new SyncAuthorityDatabase(fixture.DatabasePath);
            var retained = await reopened.LoadLatestCompleteObservationAsync(fixture.Pair.Id,
                SyncAuthorityCloudSides.LogicalLeft);
            Require(retained.Select(item => (item.RelativePath, item.ObjectId, item.ObjectVersionId,
                        item.MembershipId, item.ScanGenerationId)).SequenceEqual(initialIds) &&
                    await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM observation;") == observations &&
                    await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_path_index;") == paths,
                mode + ": the prior complete generation, mounted identities and path authority survive reopen unchanged");
            foreach (var table in new[] { "tombstone", "operation", "operation_attempt", "effect_receipt", "lease" })
                Require(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM " + table + ";") == 0,
                    mode + ": no disappearance or deletion authority/effect in " + table);
        }
        Require(wire.InvalidListRequests == 4 && wire.ForbiddenCalls == 0,
            "the strict wire rejected omitted, false and malformed mounted flags plus false recursion, with no effects");
        mutations.Mode = null;
        wire.MissingCursor = wire.RefuseContinuation = false;
        wire.MalformedAliasFolderId = null;
        await observer.ObserveAsync("monitor");
        Require((await fixture.Authority.LoadLatestCompleteObservationAsync(fixture.Pair.Id,
                SyncAuthorityCloudSides.LogicalLeft)).Count == 2,
            "correct mounted recursive input succeeds again after each retained refusal");
    }

    // Sentinels change only controlled transport input. The adapter and observer
    // still make the actual production completeness/publication decision.
    private sealed class MountedRequestMutation(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal string? Mode { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/files/list_folder", StringComparison.Ordinal) &&
                Mode is "omit-mounted" or "false-mounted" or "malformed-mounted" or "false-recursive")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                if (Mode == "omit-mounted") body.Remove("include_mounted_folders");
                else if (Mode == "false-mounted") body["include_mounted_folders"] = false;
                else if (Mode == "malformed-mounted") body["include_mounted_folders"] = "true";
                else body["recursive"] = false;
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    // ----- 3. the real Dropbox two-pass hierarchy readback -----------------------------------------

    private static async Task DropboxHierarchyAliasAsync()
    {
        Claim("CC2G-2/readback",
            "The real two-pass hierarchy readback binds each node's mount key and refuses alias drift or malformed data");
        var wire = new DropboxObjectWire("/Shared", "id:shared-mount", "dbid-hier", "ns-hier", "ns-hier-home");
        wire.Add("/Shared", "id:shared-mount", sharedFolderId: "shared-777");
        using var http = new HttpClient(wire);
        var binding = DropboxBinding("dbid-hier", "ns-hier", "ns-hier-home");
        var client = new DropboxPilotClient(http, new SyntheticTokens(), "/Shared", binding, "id:shared-mount");
        var evidence = await client.ReadRootHierarchyAsync(DropboxRequest(binding, "dbid-hier", "ns-hier",
            "ns-hier-home", "id:shared-mount", "/Shared"));
        var snapshot = evidence.ToSnapshot("root-mount");
        Require(evidence.ObjectKeyCoverage == SyncCloudObjectKeyCoverage.MountAliasesComplete,
            "a Dropbox readback declares the mount-alias coverage its provider requires");
        Require(evidence.ChainNodeKeys is { Count: 2 } &&
                evidence.ChainNodeKeys[0].FolderId == PilotPath.DropboxNamespaceRootIdentity("ns-hier") &&
                evidence.ChainNodeKeys[1].FolderId == "id:shared-mount",
            "each chain node carries its own key set, ordered from the verified corpus root to the selected root");
        Require(snapshot.RootNodeKeys().Count == 2 &&
                snapshot.RootNodeKeys().Any(key => key.Kind == SyncCloudObjectKeyKind.SharedNamespaceMount &&
                                                   key.Value == "shared-777"),
            "the selected root's own key set retains the shared folder mounted at that exact location");
        Require(snapshot.AncestorNodeKeys().Count == 1 &&
                snapshot.AncestorNodeKeys()[0].Kind == SyncCloudObjectKeyKind.NativeObject,
            "the namespace-root ancestor carries only its own native key");
        Require(snapshot.ObjectKeyCoverageIsComplete &&
                snapshot.IsDisjointnessCompleteFor(RootIdentity(evidence, "root-mount",
                    SyncAuthorityCloudSides.LogicalLeft)),
            "the produced snapshot is complete disjointness evidence for exactly its bound root");
        Require(evidence.VerifiedPassCount == 2 && wire.MetadataCalls == 2 && wire.ForbiddenCalls == 0,
            "two fresh metadata passes agreed and no listing, content or effect route was reached");

        var driftWire = new DropboxObjectWire("/Shared", "id:shared-mount", "dbid-hier", "ns-hier", "ns-hier-home");
        driftWire.Add("/Shared", "id:shared-mount", sharedFolderId: "shared-777");
        driftWire.SecondReadAlias = "shared-888";
        using var driftHttp = new HttpClient(driftWire);
        var driftClient = new DropboxPilotClient(driftHttp, new SyntheticTokens(), "/Shared", binding,
            "id:shared-mount");
        Require(await RefusedAsync(() => driftClient.ReadRootHierarchyAsync(DropboxRequest(binding, "dbid-hier",
                "ns-hier", "ns-hier-home", "id:shared-mount", "/Shared"))),
            "a shared-folder mount identity that changes between the two fresh passes is refused as drift");

        var malformedWire = new DropboxObjectWire("/Shared", "id:shared-mount", "dbid-hier", "ns-hier", "ns-hier-home");
        malformedWire.Add("/Shared", "id:shared-mount", sharedFolderId: "shared-777");
        malformedWire.MalformedAliasFolderId = "id:shared-mount";
        using var malformedHttp = new HttpClient(malformedWire);
        var malformedClient = new DropboxPilotClient(malformedHttp, new SyntheticTokens(), "/Shared", binding,
            "id:shared-mount");
        Require(await RefusedAsync(() => malformedClient.ReadRootHierarchyAsync(DropboxRequest(binding, "dbid-hier",
                "ns-hier", "ns-hier-home", "id:shared-mount", "/Shared"))),
            "a malformed shared-folder mount identity is refused rather than read without its shared-namespace key");

        var namespaceRoot = PilotPath.DropboxNamespaceRootIdentity("ns-hier");
        var misalignedEvidence = new SyncAuthorityCloudRootHierarchyEvidence(SyncProviders.Dropbox,
            binding.ConnectionId, DerivedAccount("dbid-hier", "ns-hier", "ns-hier-home"), "ns-hier", "/Shared",
            "id:shared-mount", namespaceRoot, new[] { namespaceRoot, "id:shared-mount" }, DateTimeOffset.UtcNow, 2)
        {
            ObjectKeyCoverage = SyncCloudObjectKeyCoverage.MountAliasesComplete,
            ChainNodeKeys = new[]
            {
                new SyncAuthorityCloudHierarchyNodeKeys(namespaceRoot,
                    new[] { SyncCloudObjectKey.Native(SyncProviders.Dropbox, namespaceRoot) })
            }
        };
        Require(Refused(() => misalignedEvidence.ToSnapshot("root-misaligned")),
            "declared object-key coverage without one node key set per chain node is refused rather than used as ancestry");
        var undeclared = DropboxChain("root-undeclared", "account-h", "ns-h", "id:shared-mount", Array.Empty<string>(),
            Array.Empty<string>()) with { ObjectKeyCoverage = SyncCloudObjectKeyCoverage.Undeclared };
        Require(!undeclared.IsDisjointnessCompleteFor(new SyncAuthorityCloudRootIdentity(SyncProviders.Dropbox,
                "account-h", "ns-h", "id:shared-mount", "/Shared", "root-undeclared",
                SyncAuthorityCloudRootIdentity.PairRootAuthority, SyncAuthorityCloudSides.LogicalLeft)),
            "a Dropbox chain that never declared mount-alias coverage is never disjointness evidence");
    }

    // ----- fixtures: deterministic Dropbox metadata endpoint --------------------------------------

    private sealed record WireItem(string Id, string Display, bool Folder, string? SharedFolderId,
        string? ParentSharedFolderId, string? Revision, long Size, string? ContentHash);

    private sealed class OverlapFixtureException(string message) : Exception(message);

    // Deterministic Dropbox endpoint: the account read, folder metadata and one recursive listing page.
    // It serves no content, continuation or effect route, so an unexpected provider call fails the run.
    private sealed class DropboxObjectWire : HttpMessageHandler
    {
        private readonly string _accountId;
        private readonly string _rootNamespaceId;
        private readonly string _homeNamespaceId;
        private readonly string _rootPath;
        private readonly Dictionary<string, WireItem> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _pathById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _reads = new(StringComparer.Ordinal);

        internal DropboxObjectWire(string rootPath, string rootId, string accountId = "dbid-object",
            string rootNamespaceId = "ns-object", string homeNamespaceId = "ns-object-home")
        {
            _accountId = accountId;
            _rootNamespaceId = rootNamespaceId;
            _homeNamespaceId = homeNamespaceId;
            _rootPath = PilotPath.NormalizeDropboxRoot(rootPath);
            Add(_rootPath, rootId);
        }

        internal string? SecondReadAlias { get; set; }
        internal string? MalformedAliasFolderId { get; set; }
        internal int AccountCalls { get; private set; }
        internal int MetadataCalls { get; private set; }
        internal int ListCalls { get; private set; }
        internal int ContinueCalls { get; private set; }
        internal int AcceptedRecursiveMountedRequests { get; private set; }
        internal int InvalidListRequests { get; private set; }
        internal int PageSize { get; set; } = int.MaxValue;
        internal bool MissingCursor { get; set; }
        internal bool RefuseContinuation { get; set; }
        private WireItem[] _listing = [];
        private int _pageOffset;
        private string? _nextCursor;
        internal int ForbiddenCalls { get; private set; }
        internal int SharedFolderIdCalls { get; private set; }

        internal void Add(string path, string id, string? sharedFolderId = null,
            string? parentSharedFolderId = null)
        {
            var normalized = PilotPath.NormalizeDropboxRoot(path);
            _byPath[normalized] = new WireItem(id, normalized, true, sharedFolderId, parentSharedFolderId, null, 0,
                null);
            _pathById[id] = normalized;
        }

        internal void AddFile(string path, string id, string revision, long size, string? contentHash)
        {
            var normalized = PilotPath.NormalizeDropboxRoot(path);
            _byPath[normalized] = new WireItem(id, normalized, false, null, null, revision, size, contentHash);
            _pathById[id] = normalized;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Post)
                return Forbidden("a Dropbox provider effect is not part of a metadata read");
            if (!string.Equals(request.RequestUri!.Host, "api.dropboxapi.com", StringComparison.Ordinal))
                return Forbidden("the read used an unexpected provider origin");
            var route = request.RequestUri.AbsolutePath;
            if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            {
                AccountCalls++;
                return Json("{\"account_id\":\"" + _accountId + "\",\"root_info\":{\"root_namespace_id\":\"" +
                            _rootNamespaceId + "\",\"home_namespace_id\":\"" + _homeNamespaceId + "\"}}");
            }
            if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var path = body.RootElement.GetProperty("path").GetString() ?? string.Empty;
                MetadataCalls++;
                if (!TryResolve(path, out var item))
                    return new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = new StringContent("{\"error_summary\":\"not_found\"}", Encoding.UTF8,
                            "application/json")
                    };
                return Json(Serialize(item, AliasFor(item), MalformedFor(item)));
            }
            if (route.EndsWith("/files/list_folder", StringComparison.Ordinal))
            {
                ListCalls++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var input = body.RootElement;
                if (!input.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
                    !TryResolve(path.GetString() ?? "", out var selected) || selected.Display != _rootPath ||
                    !True(input, "recursive") || !True(input, "include_mounted_folders") ||
                    !True(input, "include_non_downloadable_files") ||
                    !input.TryGetProperty("include_deleted", out var deleted) || deleted.ValueKind != JsonValueKind.False)
                {
                    InvalidListRequests++;
                    return InvalidRequest("recursive mounted inventory parameters are incomplete");
                }
                AcceptedRecursiveMountedRequests++;
                _listing = _byPath.Values.Where(item => item.Display.StartsWith(_rootPath.TrimEnd('/') + "/",
                    StringComparison.OrdinalIgnoreCase)).OrderBy(entry => entry.Display, StringComparer.Ordinal).ToArray();
                _pageOffset = 0;
                return ListingPage();
            }
            if (route.EndsWith("/files/list_folder/continue", StringComparison.Ordinal))
            {
                ContinueCalls++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (RefuseContinuation || _nextCursor is null ||
                    !body.RootElement.TryGetProperty("cursor", out var cursor) || cursor.ValueKind != JsonValueKind.String ||
                    cursor.GetString() != _nextCursor)
                    return InvalidRequest("continuation is not the exact accepted mounted inventory page");
                return ListingPage();
            }
            return Forbidden("an unexpected Dropbox route was requested");
        }

        private static bool True(JsonElement input, string name) =>
            input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        private HttpResponseMessage ListingPage()
        {
            var end = (int)Math.Min(_listing.Length, (long)_pageOffset + PageSize);
            var entries = _listing[_pageOffset..end].Select(item => Serialize(item, AliasFor(item), MalformedFor(item)));
            _pageOffset = end;
            var hasMore = end < _listing.Length;
            _nextCursor = hasMore ? "mounted-page-" + end : null;
            return Json("{\"entries\":[" + string.Join(',', entries) + "],\"has_more\":" +
                (hasMore ? "true" : "false") + (MissingCursor && hasMore ? "" :
                    ",\"cursor\":\"" + (_nextCursor ?? "mounted-complete") + "\"") + "}");
        }

        private static HttpResponseMessage InvalidRequest(string reason) => new(HttpStatusCode.BadRequest)
        { Content = new StringContent(JsonSerializer.Serialize(new { error_summary = reason }), Encoding.UTF8,
            "application/json") };

        private bool TryResolve(string pathOrId, out WireItem item)
        {
            item = null!;
            if (pathOrId.StartsWith("id:", StringComparison.Ordinal))
                return _pathById.TryGetValue(pathOrId, out var byId) && _byPath.TryGetValue(byId, out item!);
            return _byPath.TryGetValue(PilotPath.NormalizeDropboxRoot(pathOrId), out item!);
        }

        private string? AliasFor(WireItem item)
        {
            if (item.SharedFolderId is null) return null;
            SharedFolderIdCalls++;
            if (SecondReadAlias is null) return item.SharedFolderId;
            _reads.TryGetValue(item.Id, out var reads);
            _reads[item.Id] = ++reads;
            return reads >= 2 ? SecondReadAlias : item.SharedFolderId;
        }

        private bool MalformedFor(WireItem item) => MalformedAliasFolderId is not null &&
            string.Equals(item.Id, MalformedAliasFolderId, StringComparison.Ordinal);

        private static string Serialize(WireItem item, string? sharedFolderId, bool malformed)
        {
            var builder = new StringBuilder();
            builder.Append("{\".tag\":\"").Append(item.Folder ? "folder" : "file").Append('"');
            builder.Append(",\"name\":\"").Append(item.Display[(item.Display.LastIndexOf('/') + 1)..]).Append('"');
            builder.Append(",\"id\":\"").Append(item.Id).Append('"');
            builder.Append(",\"path_lower\":\"").Append(item.Display.ToLowerInvariant()).Append('"');
            builder.Append(",\"path_display\":\"").Append(item.Display).Append('"');
            if (!item.Folder)
            {
                builder.Append(",\"rev\":\"").Append(item.Revision).Append('"');
                builder.Append(",\"size\":").Append(item.Size);
                if (item.ContentHash is not null)
                    builder.Append(",\"content_hash\":\"").Append(item.ContentHash).Append('"');
            }
            if (malformed)
                builder.Append(",\"sharing_info\":{\"read_only\":false,\"shared_folder_id\":12345}");
            else if (sharedFolderId is not null || item.ParentSharedFolderId is not null)
            {
                builder.Append(",\"sharing_info\":{\"read_only\":false");
                if (sharedFolderId is not null)
                    builder.Append(",\"shared_folder_id\":\"").Append(sharedFolderId).Append('"');
                if (item.ParentSharedFolderId is not null)
                    builder.Append(",\"parent_shared_folder_id\":\"").Append(item.ParentSharedFolderId).Append('"');
                builder.Append('}');
            }
            builder.Append('}');
            return builder.ToString();
        }

        private HttpResponseMessage Forbidden(string reason)
        {
            ForbiddenCalls++;
            throw new OverlapFixtureException("Shared cloud object overlap checks: " + reason);
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }

    private sealed class SyntheticTokens : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("synthetic-not-a-credential");
        }
    }

    private static DropboxConnectionBinding DropboxBinding(string accountId, string rootNamespaceId,
        string homeNamespaceId) =>
        new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, accountId, rootNamespaceId,
            homeNamespaceId, DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();

    private static string DerivedAccount(string accountId, string rootNamespaceId, string homeNamespaceId) =>
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, accountId, rootNamespaceId,
            homeNamespaceId);

    private static SyncAuthorityCloudRootHierarchyRequest DropboxRequest(DropboxConnectionBinding binding,
        string accountId, string rootNamespaceId, string homeNamespaceId, string folderId, string folderPath) =>
        new(SyncProviders.Dropbox, binding.ConnectionId,
            DerivedAccount(accountId, rootNamespaceId, homeNamespaceId), rootNamespaceId, folderId, folderPath);
    // ----- 4. the shared comparison of two cloud roots ---------------------------------------------

    private static SyncAuthorityCloudRootIdentity RootIdentity(SyncAuthorityCloudRootHierarchyEvidence evidence,
        string rootBindingId, string side) =>
        new(evidence.Provider, evidence.ProviderAccountId, evidence.NamespaceId, evidence.SelectedRootId,
            evidence.FolderPath, rootBindingId, SyncAuthorityCloudRootIdentity.PairRootAuthority, side);

    private static SyncAuthorityCloudRootIdentity CloudRoot(string folderId, string folderPath,
        string rootBindingId, string side, string provider = SyncProviders.Dropbox, string account = "account",
        string ns = "ns") =>
        new(provider, account, ns, folderId, folderPath, rootBindingId,
            SyncAuthorityCloudRootIdentity.PairRootAuthority, side);

    private static IReadOnlyList<SyncCloudObjectKey> NodeKeys(string provider, string folderId, string[] mounts)
    {
        var keys = new List<SyncCloudObjectKey> { SyncCloudObjectKey.Native(provider, folderId) };
        foreach (var mount in mounts)
            keys.Add(SyncCloudObjectKey.SharedNamespaceMount(provider, mount));
        return keys;
    }

    // One complete Dropbox chain from the synthetic namespace root to the selected root. The selected root's
    // node carries the supplied mount aliases; named ancestors carry theirs through ancestorMounts.
    private static SyncAuthorityCloudHierarchySnapshot DropboxChain(string rootBindingId, string account, string ns,
        string selectedId, string[] ancestorIds, string[] selectedMounts,
        params (string NodeId, string[] Mounts)[] ancestorMounts)
    {
        var chainIds = new List<string> { PilotPath.DropboxNamespaceRootIdentity(ns) };
        chainIds.AddRange(ancestorIds);
        chainIds.Add(selectedId);
        var nodes = chainIds.Select(id => new SyncAuthorityCloudHierarchyNodeKeys(id,
            NodeKeys(SyncProviders.Dropbox, id, string.Equals(id, selectedId, StringComparison.Ordinal)
                ? selectedMounts
                : ancestorMounts.Where(item => string.Equals(item.NodeId, id, StringComparison.Ordinal))
                    .SelectMany(item => item.Mounts).ToArray()))).ToArray();
        return new SyncAuthorityCloudHierarchySnapshot(SyncProviders.Dropbox, account, ns, rootBindingId, selectedId,
            chainIds)
        {
            CompleteToCorpusRoot = true,
            CorpusRootId = chainIds[0],
            ChainNodeKeys = nodes,
            ObjectKeyCoverage = SyncCloudObjectKeyCoverage.MountAliasesComplete
        };
    }

    private static Task RootOverlapComparisonAsync()
    {
        Claim("CC2G-2/compare",
            "The shared comparison decides Same, Nested and Distinct from native ids and mount aliases only with complete coverage");
        var leftMount = CloudRoot("id:left-mount", "/Shared", "root-left", SyncAuthorityCloudSides.LogicalLeft,
            account: "acct-left", ns: "ns-left");
        var leftMountChain = DropboxChain("root-left", "acct-left", "ns-left", "id:left-mount",
            Array.Empty<string>(), new[] { "shared-777" });
        var rightMount = CloudRoot("id:right-mount", "/Shared", "root-right", SyncAuthorityCloudSides.LogicalRight,
            account: "acct-right", ns: "ns-right");
        var rightMountChain = DropboxChain("root-right", "acct-right", "ns-right", "id:right-mount",
            Array.Empty<string>(), new[] { "shared-777" });
        Require(SyncAuthorityCloudRootComparison.Compare(leftMount, leftMountChain, rightMount, rightMountChain) ==
                SyncAuthorityCloudRootRelation.Same,
            "two DIFFERENT Dropbox mount ids with the SAME shared_folder_id from two namespaces are one backing folder");
        Require(SyncAuthorityCloudRootComparison.Compare(rightMount, rightMountChain, leftMount, leftMountChain) ==
                SyncAuthorityCloudRootRelation.Same, "the same-object alias decision is not order dependent");
        Require(SyncAuthorityCloudRootComparison.Validate(rightMount, rightMountChain, leftMount, leftMountChain,
                "right").Errors.Contains(SyncEndpointError.DuplicateEndpointAcrossPairs),
            "the shared validator refuses that alias duplicate with the same-object error");

        var child = CloudRoot("id:right-child", "/Shared/Leaf", "root-child", SyncAuthorityCloudSides.LogicalRight,
            account: "acct-right", ns: "ns-right");
        var childChain = DropboxChain("root-child", "acct-right", "ns-right", "id:right-child",
            new[] { "id:right-mount" }, Array.Empty<string>(), ("id:right-mount", new[] { "shared-777" }));
        Require(SyncAuthorityCloudRootComparison.Compare(child, childChain, leftMount, leftMountChain) ==
                SyncAuthorityCloudRootRelation.Nested,
            "a root nested under the shared mount is contained by the mount read through another account");
        Require(SyncAuthorityCloudRootComparison.Compare(leftMount, leftMountChain, child, childChain) ==
                SyncAuthorityCloudRootRelation.Nested,
            "the mount-against-descendant decision is not order dependent");
        Require(SyncAuthorityCloudRootComparison.Validate(child, childChain, leftMount, leftMountChain, "right")
                .Errors.Contains(SyncEndpointError.OverlappingEndpointAcrossPairs),
            "the shared validator refuses that containment with the overlap error");

        var siblingA = CloudRoot("id:sibling-a", "/Parent/A", "root-sibling-a", SyncAuthorityCloudSides.LogicalLeft,
            account: "acct-shared", ns: "ns-shared");
        var siblingAChain = DropboxChain("root-sibling-a", "acct-shared", "ns-shared", "id:sibling-a",
            new[] { "id:parent" }, Array.Empty<string>());
        var siblingB = CloudRoot("id:sibling-b", "/Parent/B", "root-sibling-b", SyncAuthorityCloudSides.LogicalRight,
            account: "acct-shared", ns: "ns-shared");
        var siblingBChain = DropboxChain("root-sibling-b", "acct-shared", "ns-shared", "id:sibling-b",
            new[] { "id:parent" }, Array.Empty<string>());
        Require(SyncAuthorityCloudRootComparison.Compare(siblingB, siblingBChain, siblingA, siblingAChain) ==
                SyncAuthorityCloudRootRelation.Distinct,
            "two complete alias-covered chains that merely share an ancestor contain neither root and stay distinct");
        Require(SyncAuthorityCloudRootComparison.Validate(siblingB, siblingBChain, siblingA, siblingAChain, "right")
                .IsValid, "the shared validator accepts two provably distinct alias-covered roots");

        var otherMount = CloudRoot("id:other-mount", "/Other", "root-other", SyncAuthorityCloudSides.LogicalRight,
            account: "acct-other", ns: "ns-other");
        var otherMountChain = DropboxChain("root-other", "acct-other", "ns-other", "id:other-mount",
            Array.Empty<string>(), new[] { "shared-999" });
        Require(SyncAuthorityCloudRootComparison.Compare(otherMount, otherMountChain, leftMount, leftMountChain) ==
                SyncAuthorityCloudRootRelation.Distinct,
            "two complete alias-covered mounts with no shared key in two namespaces are provably distinct");

        var undeclared = siblingBChain with { ObjectKeyCoverage = SyncCloudObjectKeyCoverage.Undeclared };
        Require(SyncAuthorityCloudRootComparison.Compare(siblingB, undeclared, siblingA, siblingAChain) ==
                SyncAuthorityCloudRootRelation.Unverified,
            "an undeclared mount-alias coverage on one complete chain leaves the decision Unverified");
        Require(SyncAuthorityCloudRootComparison.Validate(siblingB, undeclared, siblingA, siblingAChain, "right")
                .Errors.Contains(SyncEndpointError.UnverifiedCloudNamespace),
            "the shared validator refuses undeclared alias coverage with the unverified-namespace error");
        var incomplete = DropboxChain("root-other", "acct-other", "ns-other", "id:other-mount",
            Array.Empty<string>(), new[] { "shared-999" }) with { CompleteToCorpusRoot = false };
        Require(SyncAuthorityCloudRootComparison.Compare(otherMount, incomplete, leftMount, leftMountChain) ==
                SyncAuthorityCloudRootRelation.Unverified,
            "an incomplete chain across two routes never falls through to a distinctness decision");
        var incompleteSameAlias = rightMountChain with { CompleteToCorpusRoot = false };
        Require(SyncAuthorityCloudRootComparison.Compare(rightMount, incompleteSameAlias, leftMount, leftMountChain) ==
                SyncAuthorityCloudRootRelation.Same,
            "two roots whose own readback keys name one backing object are that object even without a complete chain");

        var foreignKeys = siblingBChain with
        {
            ChainNodeKeys = new[]
            {
                new SyncAuthorityCloudHierarchyNodeKeys("id:parent",
                    new[] { SyncCloudObjectKey.Native(SyncProviders.GoogleDrive, "id:parent") }),
                new SyncAuthorityCloudHierarchyNodeKeys("id:sibling-b",
                    new[] { SyncCloudObjectKey.Native(SyncProviders.Dropbox, "id:sibling-b") })
            }
        };
        Require(!foreignKeys.ObjectKeyCoverageIsComplete &&
                SyncAuthorityCloudRootComparison.Compare(siblingB, foreignKeys, siblingA, siblingAChain) ==
                SyncAuthorityCloudRootRelation.Unverified,
            "a supplied node key set naming another provider is malformed coverage and stays unverified");
        var missingNative = siblingBChain with
        {
            ChainNodeKeys = new[]
            {
                new SyncAuthorityCloudHierarchyNodeKeys(PilotPath.DropboxNamespaceRootIdentity("ns-shared"),
                    new[] { SyncCloudObjectKey.Native(SyncProviders.Dropbox,
                        PilotPath.DropboxNamespaceRootIdentity("ns-shared")) }),
                new SyncAuthorityCloudHierarchyNodeKeys("id:sibling-b",
                    new[] { SyncCloudObjectKey.SharedNamespaceMount(SyncProviders.Dropbox, "other-mount") })
            }
        };
        Require(!missingNative.ObjectKeyCoverageIsComplete &&
                SyncAuthorityCloudRootComparison.Compare(siblingB, missingNative, siblingA, siblingAChain) ==
                SyncAuthorityCloudRootRelation.Unverified,
            "a node key set without that exact node's own native key is never complete coverage");
        var misalignedKeys = siblingBChain with
        {
            ChainNodeKeys = new[]
            {
                new SyncAuthorityCloudHierarchyNodeKeys(PilotPath.DropboxNamespaceRootIdentity("ns-shared"),
                    new[] { SyncCloudObjectKey.Native(SyncProviders.Dropbox,
                        PilotPath.DropboxNamespaceRootIdentity("ns-shared")) })
            }
        };
        Require(!misalignedKeys.ObjectKeyCoverageIsComplete &&
                misalignedKeys.RootNodeKeys().Count == 0 && misalignedKeys.AncestorNodeKeys().Count == 0,
            "a declared coverage with fewer node key sets than chain nodes supplies no usable alias evidence");

        var googleA = CloudRoot("shared-literal", "/Drive A", "root-ga", SyncAuthorityCloudSides.LogicalLeft,
            provider: SyncProviders.GoogleDrive, account: "acct-ga", ns: "perm-ga");
        var googleB = CloudRoot("gd-other", "/Drive B", "root-gb", SyncAuthorityCloudSides.LogicalRight,
            provider: SyncProviders.GoogleDrive, account: "acct-gb", ns: "perm-gb");
        Require(SyncAuthorityCloudRootComparison.Compare(googleB, null, googleA, null) ==
                SyncAuthorityCloudRootRelation.Unverified,
            "a path-opaque provider without a native chain stays unverified rather than assumed disjoint");
        var dropboxLiteral = CloudRoot("shared-literal", "/Dropbox", "root-dl",
            SyncAuthorityCloudSides.LogicalRight);
        Require(SyncAuthorityCloudRootComparison.Compare(dropboxLiteral, null, googleA, null) ==
                SyncAuthorityCloudRootRelation.Distinct,
            "one identical literal id under two different providers is never the same object");
        var googleSameLiteral = CloudRoot("shared-literal", "/Drive A2", "root-ga2",
            SyncAuthorityCloudSides.LogicalRight, provider: SyncProviders.GoogleDrive, account: "acct-gc",
            ns: "perm-gc");
        Require(SyncAuthorityCloudRootComparison.Compare(googleSameLiteral, null, googleA, null) ==
                SyncAuthorityCloudRootRelation.Same,
            "the same provider-global folder identity is the same object before any account or namespace comparison");
        return Task.CompletedTask;
    }
    // ----- 5. real isolated SQLite root registration ----------------------------------------------

    private static async Task RootOverlapRegistrationAsync()
    {
        Claim("CC2G-2/registration",
            "Real isolated SQLite root registration refuses an aliased mount and a nested root and accepts genuinely distinct roots");
        var authority = await OpenAuthorityAsync("registration-overlap");
        var databasePath = DatabasePath("registration-overlap");
        var leftSpec = Dropbox("90", "id:left-mount");
        var rightSpec = Dropbox("91", "id:right-mount");
        await RegisterAccountAsync(authority, leftSpec);
        await RegisterAccountAsync(authority, rightSpec);
        var pair = await RetainPairAsync(authority, CreatePair(leftSpec, rightSpec, "CC2G registration"));
        var leftChain = DropboxChain("root-left-mount", leftSpec.ProviderAccountId, leftSpec.EndpointNamespaceId,
            "id:left-mount", Array.Empty<string>(), new[] { "shared-777" });
        await RegisterCloudRootAsync(authority, pair, leftSpec, "root-left-mount",
            SyncAuthorityCloudSides.LogicalLeft, new[] { leftChain });
        Require(await ScalarAsync(databasePath,
                "SELECT count(*) FROM root_binding WHERE pair_id=$pair AND state='active';",
                ("$pair", pair.Id)) == 1,
            "the first mount root is retained");
        var rightChain = DropboxChain("root-right-mount", rightSpec.ProviderAccountId,
            rightSpec.EndpointNamespaceId, "id:right-mount", Array.Empty<string>(), new[] { "shared-777" });
        var duplicate = await RefusalMessageAsync(() => RegisterCloudRootAsync(authority, pair, rightSpec,
            "root-right-mount", SyncAuthorityCloudSides.LogicalRight, new[] { rightChain, leftChain }));
        Require(duplicate is not null && duplicate.Contains("DuplicateEndpointAcrossPairs", StringComparison.Ordinal),
            "a root with a different mount id but the same shared_folder_id from another namespace is refused as the same object");
        var nestedSpec = rightSpec with { FolderId = "id:right-child", FolderPath = "/Shared/Leaf" };
        var nestedChain = DropboxChain("root-right-child", nestedSpec.ProviderAccountId,
            nestedSpec.EndpointNamespaceId, "id:right-child", new[] { "id:right-mount" }, Array.Empty<string>(),
            ("id:right-mount", new[] { "shared-777" }));
        var nested = await RefusalMessageAsync(() => RegisterCloudRootAsync(authority, pair, nestedSpec,
            "root-right-child", SyncAuthorityCloudSides.LogicalRight, new[] { nestedChain, leftChain }));
        Require(nested is not null && nested.Contains("OverlappingEndpointAcrossPairs", StringComparison.Ordinal),
            "a nested root under the shared mount is refused across accounts as containment");
        var uncoveredSpec = rightSpec with { FolderId = "id:right-other", FolderPath = "/Other" };
        var uncoveredChain = DropboxChain("root-right-other", uncoveredSpec.ProviderAccountId,
            uncoveredSpec.EndpointNamespaceId, "id:right-other", Array.Empty<string>(), new[] { "shared-999" }) with
            { ObjectKeyCoverage = SyncCloudObjectKeyCoverage.Undeclared };
        var uncovered = await RefusalMessageAsync(() => RegisterCloudRootAsync(authority, pair, uncoveredSpec,
            "root-right-other", SyncAuthorityCloudSides.LogicalRight, new[] { uncoveredChain, leftChain }));
        Require(uncovered is not null && uncovered.Contains("UnverifiedCloudNamespace", StringComparison.Ordinal),
            "a root whose chain never declared mount-alias coverage is refused across routes rather than called distinct");
        await RegisterCloudRootAsync(authority, pair, uncoveredSpec, "root-right-other",
            SyncAuthorityCloudSides.LogicalRight, new[]
            {
                uncoveredChain with { ObjectKeyCoverage = SyncCloudObjectKeyCoverage.MountAliasesComplete },
                leftChain
            });
        Require(await ScalarAsync(databasePath,
                "SELECT count(*) FROM root_binding WHERE pair_id=$pair AND state='active';",
                ("$pair", pair.Id)) == 2,
            "a genuinely distinct alias-covered mount root is retained beside the first");
        Require(await ScalarAsync(databasePath, "SELECT count(*) FROM root_binding WHERE state='active';") == 2,
            "every refused registration leaves the retained active roots unchanged");
    }

    // ----- 6. the pre-observation inventory overlap guard ------------------------------------------

    private static async Task InventoryOverlapGuardAsync()
    {
        Claim("CC2G-3/guard",
            "The pre-observation guard refuses any backing object both sides can reach before either scan generation");
        await DistinctInventoriesAsync();
        await SharedNativeIdAsync("same-relative-path", "shared.txt", "shared.txt");
        await SharedNativeIdAsync("different-relative-path", "left-only.txt", "right-only.txt");
        await SharedMountAliasAsync();
        await ExcludedOverlapAsync();
        await CrossProviderLiteralIdAsync();
    }

    private static async Task DistinctInventoriesAsync()
    {
        var fixture = await ObservationAsync("distinct", Dropbox("100", "id:obs-left"),
            Dropbox("101", "id:obs-right"),
            new[] { FileEntry("left-only.txt", "id:left-file", new string('a', 64)) },
            new[] { FileEntry("right-only.txt", "id:right-file", new string('b', 64)) });
        await Coordinator("distinct", fixture).ObserveAsync("initial");
        var left = await fixture.Authority.LoadLatestCompleteObservationAsync(fixture.Pair.Id,
            SyncAuthorityCloudSides.LogicalLeft);
        var right = await fixture.Authority.LoadLatestCompleteObservationAsync(fixture.Pair.Id,
            SyncAuthorityCloudSides.LogicalRight);
        Require(left.Count == 1 && right.Count == 1,
            "genuinely distinct backing objects are observed on both sides");
        Require(left[0].ObjectId != right[0].ObjectId,
            "two genuinely distinct provider file ids stay two distinct logical objects");
        Require(fixture.LeftStore.VerifyCalls == 2 && fixture.RightStore.VerifyCalls == 2,
            "each root is still verified before and after its listing");
        Require(fixture.LeftStore.UnexpectedCalls == 0 && fixture.RightStore.UnexpectedCalls == 0,
            "the observation reaches no provider effect beyond root verification and listing");
    }

    private static async Task SharedNativeIdAsync(string caseName, string leftPath, string rightPath)
    {
        var fixture = await ObservationAsync("native-" + caseName, Dropbox("110", "id:nat-left"),
            Dropbox("111", "id:nat-right"),
            new[] { FileEntry(leftPath, "SAME-NATIVE-ID", new string('a', 64)) },
            new[] { FileEntry(rightPath, "SAME-NATIVE-ID", new string('b', 64)) });
        await AssertOverlapRefusedAsync("native-" + caseName, fixture,
            "one provider-qualified native file id reachable from BOTH sides at " + caseName + " is refused");
    }

    private static async Task SharedMountAliasAsync()
    {
        var mount = SyncCloudObjectKey.SharedNamespaceMount(SyncProviders.Dropbox, "shared-777");
        var fixture = await ObservationAsync("mount-alias", Dropbox("120", "id:ma-left"),
            Dropbox("121", "id:ma-right"),
            new[] { FolderEntry("left-mount", "id:left-mount", new[] { mount }) },
            new[] { FolderEntry("right-mount", "id:right-mount", new[] { mount }) });
        await AssertOverlapRefusedAsync("mount-alias", fixture,
            "two DIFFERENT native ids sharing one mount alias on the two sides are refused as one backing object");
    }

    private static async Task ExcludedOverlapAsync()
    {
        var fixture = await ObservationAsync("excluded-overlap", Dropbox("130", "id:ex-left"),
            Dropbox("131", "id:ex-right"),
            new[] { FileEntry("excluded/shared.txt", "SAME-EXCLUDED-ID", new string('c', 64)) },
            new[] { FileEntry("excluded/shared.txt", "SAME-EXCLUDED-ID", new string('d', 64)) },
            new[] { new SyncSelectionRule("excluded", false) });
        await AssertOverlapRefusedAsync("excluded-overlap", fixture,
            "an overlap on a path the saved selection EXCLUDES is refused before either scan generation");
    }

    private static async Task CrossProviderLiteralIdAsync()
    {
        var fixture = await ObservationAsync("cross-provider", Dropbox("140", "id:cp-left"),
            Drive("141", "gd-cp-right"),
            new[] { FileEntry("left.txt", "SAME-LITERAL-ID", new string('e', 64)) },
            new[] { FileEntry("right.txt", "SAME-LITERAL-ID", new string('f', 64)) });
        await Coordinator("cross-provider", fixture).ObserveAsync("initial");
        var left = await fixture.Authority.LoadLatestCompleteObservationAsync(fixture.Pair.Id,
            SyncAuthorityCloudSides.LogicalLeft);
        var right = await fixture.Authority.LoadLatestCompleteObservationAsync(fixture.Pair.Id,
            SyncAuthorityCloudSides.LogicalRight);
        Require(left.Count == 1 && right.Count == 1 && left[0].ObjectId != right[0].ObjectId,
            "one identical literal id string on two different providers is legitimate and stays two distinct objects");
        Require(await ScalarAsync(fixture.DatabasePath,
                "SELECT count(*) FROM scan_generation WHERE pair_id=$pair;", ("$pair", fixture.Pair.Id)) == 2,
            "both scan generations are retained for the distinct cross-provider pair");
    }

    private static SyncAuthorityObservationCoordinator Coordinator(string caseName, ObservationFixture fixture) =>
        new(fixture.Pair, fixture.LeftStore.Store, fixture.Authority, "root-left-" + caseName,
            "root-right-" + caseName, onInventory: null, cloudEndpoints: fixture.Set);

    private static async Task AssertOverlapRefusedAsync(string caseName, ObservationFixture fixture, string reason)
    {
        var refused = false;
        try
        {
            await Coordinator(caseName, fixture).ObserveAsync("initial");
        }
        catch (SyncAuthorityCloudObjectOverlapException error)
        {
            refused = error.Message.Contains("refused before either scan generation", StringComparison.Ordinal);
        }
        Require(refused, reason + " with an explicit overlap reason");
        Require(await ScalarAsync(fixture.DatabasePath,
                "SELECT count(*) FROM scan_generation WHERE pair_id=$pair;", ("$pair", fixture.Pair.Id)) == 0,
            reason + ": no scan generation was begun for either side");
        Require(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM observation;") == 0,
            reason + ": no observation row was retained");
        Require(await ScalarAsync(fixture.DatabasePath,
                "SELECT count(*) FROM sync_path_index WHERE pair_id=$pair;", ("$pair", fixture.Pair.Id)) == 0,
            reason + ": no path index was written");
        Require(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM operation;") == 0,
            reason + ": no effect was enqueued");
    }
    // ----- fixtures: real isolated SQLite observation ---------------------------------------------

    private sealed record EndpointSpec(SyncEndpointKind Kind, string Provider, string ConnectionId, string AccountId,
        string RootNamespaceId, string HomeNamespaceId, string EndpointNamespaceId, string ProviderAccountId,
        string FolderId, string FolderPath);

    private sealed record ObservationFixture(SyncAuthorityDatabase Authority, string DatabasePath, SyncPair Pair,
        SyncAuthorityCloudEndpointSet Set, FixtureStore LeftStore, FixtureStore RightStore);

    private static EndpointSpec Dropbox(string index, string folderId, string? folderPath = null) => new(
        SyncEndpointKind.Dropbox, SyncProviders.Dropbox, "conn-d" + index, "dbid-" + index, "ns-root-" + index,
        "ns-home-" + index, "ns-root-" + index,
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, "dbid-" + index, "ns-root-" + index,
            "ns-home-" + index), folderId, folderPath ?? "/Dropbox " + index);

    private static EndpointSpec Drive(string index, string folderId, string? folderPath = null) => new(
        SyncEndpointKind.GoogleDrive, SyncProviders.GoogleDrive, "conn-g" + index, "perm-" + index, "key-" + index,
        "key-" + index, "perm-" + index,
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.GoogleDrive, "perm-" + index, "key-" + index,
            "key-" + index), folderId, folderPath ?? "/Drive " + index);

    private static SyncEndpoint Endpoint(EndpointSpec spec) => SyncEndpoint.Cloud(spec.Kind, spec.ConnectionId,
        spec.EndpointNamespaceId, spec.FolderId, spec.FolderPath);

    private static SyncPair CreatePair(EndpointSpec left, EndpointSpec right, string name) =>
        SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(Endpoint(left), Endpoint(right),
            SyncMode.TwoWay), name);

    private static string CorpusRoot(EndpointSpec spec) =>
        SyncEndpointProviders.Get(spec.Kind).PathsDescribeAncestry
            ? PilotPath.DropboxNamespaceRootIdentity(spec.RootNamespaceId)
            : "corpus-" + spec.FolderId;

    private static SyncAuthorityCloudHierarchySnapshot Chain(EndpointSpec spec, string rootBindingId)
    {
        var corpus = CorpusRoot(spec);
        return new SyncAuthorityCloudHierarchySnapshot(spec.Provider, spec.ProviderAccountId,
            spec.EndpointNamespaceId, rootBindingId, spec.FolderId, new[] { corpus, spec.FolderId })
        {
            CompleteToCorpusRoot = true,
            CorpusRootId = corpus,
            ChainNodeKeys = new[]
            {
                new SyncAuthorityCloudHierarchyNodeKeys(corpus,
                    NodeKeys(spec.Provider, corpus, Array.Empty<string>())),
                new SyncAuthorityCloudHierarchyNodeKeys(spec.FolderId,
                    NodeKeys(spec.Provider, spec.FolderId, Array.Empty<string>()))
            },
            ObjectKeyCoverage = SyncCloudObjectKeySemantics.RequiredCoverage(spec.Provider)
        };
    }

    private static PilotRemoteEntry FileEntry(string relativePath, string fileId, string contentHash,
        IReadOnlyList<SyncCloudObjectKey>? aliases = null) =>
        new(relativePath, PilotEntryKind.File, fileId, "rev-1", 3, contentHash, DateTimeOffset.UnixEpoch, null,
            aliases);

    private static PilotRemoteEntry FolderEntry(string relativePath, string folderId,
        IReadOnlyList<SyncCloudObjectKey>? aliases = null) =>
        new(relativePath, PilotEntryKind.Directory, folderId, null, 0, null, DateTimeOffset.UnixEpoch, null,
            aliases);

    private static async Task<ObservationFixture> ObservationAsync(string caseName, EndpointSpec leftSpec,
        EndpointSpec rightSpec, IReadOnlyList<PilotRemoteEntry> leftEntries,
        IReadOnlyList<PilotRemoteEntry> rightEntries, SyncSelectionRule[]? selection = null)
    {
        var authority = await OpenAuthorityAsync(caseName);
        var databasePath = DatabasePath(caseName);
        await RegisterAccountAsync(authority, leftSpec);
        await RegisterAccountAsync(authority, rightSpec);
        var pair = await RetainPairAsync(authority, CreatePair(leftSpec, rightSpec, "CC2G " + caseName) with
        {
            SelectionRules = selection ?? Array.Empty<SyncSelectionRule>()
        });
        var leftRoot = "root-left-" + caseName;
        var rightRoot = "root-right-" + caseName;
        await RegisterCloudRootAsync(authority, pair, leftSpec, leftRoot, SyncAuthorityCloudSides.LogicalLeft,
            new[] { Chain(leftSpec, leftRoot) });
        await RegisterCloudRootAsync(authority, pair, rightSpec, rightRoot, SyncAuthorityCloudSides.LogicalRight,
            new[] { Chain(rightSpec, rightRoot), Chain(leftSpec, leftRoot) });
        var leftStore = CreateStore(leftEntries);
        var rightStore = CreateStore(rightEntries);
        var set = SyncAuthorityCloudEndpointSet.FromPair(pair, leftStore.Store, rightStore.Store, leftRoot,
            rightRoot, leftSpec.ProviderAccountId, rightSpec.ProviderAccountId);
        return new ObservationFixture(authority, databasePath, pair, set, leftStore, rightStore);
    }

    public class FixtureStore : DispatchProxy
    {
        private IReadOnlyList<PilotRemoteEntry> _entries = Array.Empty<PilotRemoteEntry>();
        internal int VerifyCalls;
        internal int ListCalls;
        internal int UnexpectedCalls;

        internal IReadOnlyList<PilotRemoteEntry> Entries
        {
            get => _entries;
            set => _entries = value;
        }

        internal ISyncRemoteStore Store => (ISyncRemoteStore)(object)this;

        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            nameof(ISyncRemoteStore.VerifyRootIdentityAsync) => Verify(),
            nameof(ISyncRemoteStore.ListEntriesAsync) => List(),
            _ => Unexpected(method)
        };

        private object Verify()
        {
            VerifyCalls++;
            return Task.CompletedTask;
        }

        private object List()
        {
            ListCalls++;
            return Task.FromResult(_entries);
        }

        private object Unexpected(MethodInfo? method)
        {
            UnexpectedCalls++;
            throw new InvalidOperationException(
                "Unexpected provider effect in a shared-object overlap fixture: " + method?.Name);
        }
    }

    private static FixtureStore CreateStore(IReadOnlyList<PilotRemoteEntry> entries)
    {
        var store = (FixtureStore)(object)DispatchProxy.Create<ISyncRemoteStore, FixtureStore>();
        store.Entries = entries;
        return store;
    }

    private static async Task<SyncAuthorityDatabase> OpenAuthorityAsync(string caseName)
    {
        var directory = Path.Combine(_root, caseName);
        Directory.CreateDirectory(directory);
        var authority = new SyncAuthorityDatabase(Path.Combine(directory, "syncshark.db"));
        await authority.InitializeAsync(InstallationId, "profile-shared-object-overlap-316", SourceRevision, "{}");
        return authority;
    }

    private static string DatabasePath(string caseName) =>
        Path.Combine(_root, caseName, "syncshark.db");

    private static async Task RegisterAccountAsync(SyncAuthorityDatabase authority, EndpointSpec spec)
    {
        await authority.RegisterProviderAccountAsync(spec.ProviderAccountId, InstallationId, spec.AccountId,
            spec.RootNamespaceId, spec.HomeNamespaceId, spec.Provider + " account", 1,
            cancellationToken: default, provider: spec.Provider);
    }

    private static async Task<SyncPair> RetainPairAsync(SyncAuthorityDatabase authority, SyncPair pair)
    {
        var normalized = SyncPairStore.NormalizePair(pair);
        await authority.RegisterPairAsync(normalized.Id, normalized.PolicyRevision);
        await authority.RetainPairDefinitionAsync(normalized, true);
        return normalized;
    }

    private static async Task RegisterCloudRootAsync(SyncAuthorityDatabase authority, SyncPair pair,
        EndpointSpec spec, string rootBindingId, string side,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? hierarchies = null)
    {
        await authority.RegisterCloudEndpointRootBindingAsync(rootBindingId, pair.Id, side, 1, InstallationId,
            spec.Provider, spec.ProviderAccountId, spec.EndpointNamespaceId, spec.FolderId, spec.FolderPath,
            cancellationToken: default, hierarchies: hierarchies);
    }
    // ----- helpers ---------------------------------------------------------------------------------

    private static bool Refused(Func<object?> action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
                                          ArgumentException or FormatException or
                                          SyncAuthorityCloudRootHierarchyException)
        {
            return true;
        }
    }

    private static async Task<string?> RefusalMessageAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (OverlapFixtureException)
        {
            throw;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
                                          ArgumentException or IOException or FormatException or
                                          NotSupportedException or SyncAuthorityCloudRootHierarchyException)
        {
            return error.Message;
        }
    }

    private static async Task<bool> RefusedAsync(Func<Task> action) =>
        await RefusalMessageAsync(action) is not null;


    private static async Task<long> ScalarAsync(string databasePath, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Claim(string id, string description) =>
        Cases.Add(new Dictionary<string, object?> { ["id"] = id, ["description"] = description });

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidDataException("Shared cloud object overlap assertion failed: " + message);
        _assertions++;
    }
}
