using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// EPS1 focused proofs: the shared immutable endpoint context, its exact capture from the pair the real
// producer used, and the SQLite append path that retains or refuses it.
//
// The capture proofs call SyncEndpointContext.Capture, the same helper the real RetainProgressAsync
// producer calls, and the retention proofs read the retained detail JSON back from real isolated SQLite
// state and compare it with the captured value. No expected payload is handwritten here.
internal static class EndpointContextCaptureChecks
{
    private static int _assertions;
    private static int _cases;

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        NineOrderedConfigurations(root);
        ReversedPhysicalLogicalMapping(root);
        TwoAccountsOfSameProvider(root);
        CaptureRefusals(root);
        ProjectionFieldIsOptional();
        await ProducersAsync(root);
        await LocalLocalDoesNotConsultThePrimaryAccountAsync(root);
        await LegacyUnknownStaysUnknownAsync(root);
        Console.WriteLine("PASS endpoint context capture: " + _assertions + " assertions across " + _cases +
            " cases; nine ordered configurations, reversed physical/logical mapping, two same-provider accounts," +
            " real RetainProgressAsync producer capture into SQLite, an existing primary Dropbox account that a" +
            " local/local pair never consults, absent legacy account and local identity staying unknown, capture and" +
            " append rejection of unknown provider, mismatched side/provider, foreign pair id and malformed metadata," +
            " serialized round trip, older-payload replay, and pair rename/rebind leaving a retained snapshot unchanged.");
    }

    // ----- 1. all nine ordered configurations, one shared contract -------------------------------------

    private static void NineOrderedConfigurations(string root)
    {
        Claim("the nine ordered provider combinations share one capture contract");
        var kinds = new[] { SyncEndpointKind.Local, SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive };
        var observed = new List<string>();
        foreach (var leftKind in kinds)
        foreach (var rightKind in kinds)
        {
            var tag = leftKind + "-" + rightKind;
            var pair = BuildPair("Configuration " + tag,
                CloudOrLocal(root, leftKind, "l-" + tag), CloudOrLocal(root, rightKind, "r-" + tag));
            var context = SyncEndpointContext.Capture(pair);
            Require(SyncEndpointPresentation.PortTitle(context, context.Left.CompatibilityPort) ==
                    SyncEndpointPresentation.SectionTitle(SyncPairSide.Left, leftKind) &&
                    SyncEndpointPresentation.PortTitle(context, context.Right.CompatibilityPort) ==
                    SyncEndpointPresentation.SectionTitle(SyncPairSide.Right, rightKind),
                "R536 public port titles preserve logical sides for " + tag);
            Require(context.Left.Side == SyncPairSide.Left && context.Right.Side == SyncPairSide.Right &&
                    context.Left.Kind == leftKind && context.Right.Kind == rightKind,
                "the " + tag + " pair captures its ordered sides and exact registered kinds");
            var expectedLeft = SyncEndpointContext.CompatibilityPort(leftKind, rightKind, SyncPairSide.Left);
            var expectedRight = SyncEndpointContext.CompatibilityPort(leftKind, rightKind, SyncPairSide.Right);
            Require(context.Left.CompatibilityPort == expectedLeft &&
                    context.Right.CompatibilityPort == expectedRight && expectedLeft != expectedRight,
                "the " + tag + " pair carries the centrally computed compatibility ports " + expectedLeft +
                "/" + expectedRight);
            Require(MetadataMatchesKind(context.Left) && MetadataMatchesKind(context.Right),
                "each " + tag + " side carries only the identity its own kind declares");
            observed.Add(tag + "=" + expectedLeft + "/" + expectedRight);
        }
        Require(observed.Distinct().Count() == 9, "all nine ordered configurations were captured exactly once (" +
            string.Join(", ", observed) + ")");
    }

    // ----- 2. reversed physical/logical mapping --------------------------------------------------------

    private static void ReversedPhysicalLogicalMapping(string root)
    {
        Claim("a right-side local folder reverses the two compatibility ports without inventing cloud identity");
        var localPath = LocalFolder(root, "reversed-local");
        foreach (var cloudKind in new[] { SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
        {
            var cloud = CloudOrLocal(root, cloudKind, "reversed-cloud-" + cloudKind);
            var pair = BuildPair("Reversed " + cloudKind, cloud, SyncEndpoint.Local(localPath, IdentityOf(localPath)));
            Require(pair.RightEndpoint is { IsLocal: true } && pair.LeftEndpoint is { IsLocal: false } &&
                    SyncEndpointEngineLayout.IsReversed(pair),
                "the " + cloudKind + " fixture is a real reversed cloud-left/local-right pair");
            var legacy = SyncEndpointsCompatibility.FromSyncPair(pair);
            Require(legacy.Endpoints is { } mapped && mapped.Left.IsLocal && mapped.Right.IsCloud,
                "the legacy compatibility mapping of the " + cloudKind +
                " pair would place the local folder on the left instead");
            var context = SyncEndpointContext.Capture(pair);
            Require(context.Left.Kind == cloudKind && context.Right.Kind == SyncEndpointKind.Local,
                "the capture follows the explicit document's ordered sides, not the legacy projection's order");
            Require(context.Left.CompatibilityPort == SyncAuthorityCloudSides.LogicalRight &&
                    context.Right.CompatibilityPort == SyncAuthorityCloudSides.LogicalLeft,
                "a right-side local folder maps the cloud left endpoint to the retained remote port and the local right endpoint to the local port");
            Require(context.Left.ConnectionId == cloud.ConnectionId &&
                    context.Left.NamespaceId == cloud.NamespaceId && context.Left.FolderId == cloud.FolderId &&
                    context.Left.FolderPath == cloud.FolderPath && context.Right.LocalPath == localPath &&
                    context.Right.ConnectionId is null && context.Right.Provider is null,
                "the reversed " + cloudKind +
                " pair keeps its left endpoint's exact cloud identity and assigns no cloud account to its local right endpoint");
        }
    }

    // ----- 3. two distinct accounts of the same provider -----------------------------------------------

    private static void TwoAccountsOfSameProvider(string root)
    {
        Claim("two distinct accounts of one provider stay distinguishable in one retained context");
        var left = CloudOrLocal(root, SyncEndpointKind.Dropbox, "account-one");
        var right = CloudOrLocal(root, SyncEndpointKind.Dropbox, "account-two");
        Require(left.ConnectionId != right.ConnectionId && left.NamespaceId != right.NamespaceId,
            "the Dropbox fixture carries two distinct accounts and namespaces");
        var context = SyncEndpointContext.Capture(BuildPair("Two Dropbox accounts", left, right));
        Require(context.Left.Provider == SyncProviders.Dropbox && context.Right.Provider == SyncProviders.Dropbox &&
                context.Left.ConnectionId == left.ConnectionId && context.Right.ConnectionId == right.ConnectionId &&
                context.Left.ConnectionId != context.Right.ConnectionId &&
                context.Left.NamespaceId == left.NamespaceId && context.Right.NamespaceId == right.NamespaceId,
            "both same-provider sides keep their own registered provider, account and verified namespace");
    }

    // ----- 4. capture and append refusals --------------------------------------------------------------

    private static void CaptureRefusals(string root)
    {
        Claim("a pair whose declared endpoints cannot be described safely is refused rather than guessed");
        var localPath = LocalFolder(root, "refusal-local");
        var identity = IdentityOf(localPath);
        var legacy = new SyncPair("legacy-unknown-provider", "Legacy fixture", localPath,
            identity.VolumeSerialNumber, identity.RootFileId, "/Legacy", "id:legacy");
        RequireThrows(() => SyncEndpointContext.Capture(legacy with { Provider = "box" }),
            "does not register", "a legacy pair naming an unregistered provider is refused before any capture");

        var cloudPair = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
            CloudOrLocal(root, SyncEndpointKind.Dropbox, "refusal-left"),
            CloudOrLocal(root, SyncEndpointKind.GoogleDrive, "refusal-right"), SyncMode.TwoWay), "Refusal fixture");
        RequireThrows(() => SyncEndpointContext.Capture(cloudPair with
                { LeftEndpoint = cloudPair.LeftEndpoint! with { Provider = "google-drive" } }),
            "not a usable pair", "an explicit document whose provider disagrees with its kind is refused");
        RequireThrows(() => SyncEndpointContext.Capture(cloudPair with { RightEndpoint = null }),
            "never read as a legacy pair", "an explicit document missing a member is refused instead of falling back to legacy fields");

        var good = SyncEndpointContext.Capture(legacy with { Provider = SyncProviders.Dropbox });
        Require(good.PairId == legacy.Id && good.Right.FolderId == "id:legacy" &&
                good.Right.FolderPath == "/Legacy" && good.Right.Provider == SyncProviders.Dropbox,
            "the same legacy pair captures its declared provider and recorded root when nothing contradicts it");

        RequireRefusal(good, good with { PairId = "other-pair" }, "names a different folder pair",
            "foreign PairId");
        RequireRefusal(good, good with { Left = good.Left with { Side = SyncPairSide.Right } },
            "carries its ordered sides as left then right", "mismatched side");
        RequireRefusal(good, good with { Right = good.Right with { CompatibilityPort = "local" } },
            "not the ports the shared endpoint layout assigns", "mismatched compatibility port");
        RequireRefusal(good, good with { Left = good.Left with { ConnectionId = "account-x" } },
            "local left endpoint cannot carry a provider, cloud account", "cloud account on a local endpoint");
        RequireRefusal(good, good with { Left = good.Left with { Kind = (SyncEndpointKind)9 } },
            "is not a registered endpoint kind", "unregistered endpoint kind");
        RequireRefusal(good, good with { Right = good.Right with { Provider = "google-drive" } },
            "not the registered provider of its endpoint kind", "provider/kind mismatch on a cloud side");
        RequireRefusal(good, good with { Right = good.Right with { LocalPath = localPath } },
            "cannot carry a local folder path", "local path on a cloud endpoint");
        RequireRefusal(good, good with { Right = good.Right with { FolderId = null, FolderPath = null } },
            "requires the provider folder identity or the recorded root path", "cloud side with no folder identity");
        RequireRefusal(good, good with { Right = good.Right with { ConnectionId = "  " } },
            "is blank; an unknown identity is absent rather than blank", "blank account identity");
        RequireRefusal(good, good with { Left = good.Left with { LocalRootFileId = null } },
            "records both parts of its local folder identity or neither", "incomplete local identity");
        RequireRefusal(good, good with { CapturedName = new string('n', 500) },
            "exceeds the retained presentation bound", "unbounded captured name");
        RequireRefusal(good, good with { CapturedName = "name\u0001with-control" },
            "contains a control character", "control character in metadata");
        RequireRefusal(good, good with { Left = good.Left with { LocalPath = "C:\\" + new string('p', 33000) } },
            "exceeds the retained presentation bound", "unbounded local folder path");
        RequireRefusal(good, good with { Left = null! }, "requires both ordered sides", "missing ordered side");
    }

    private static void RequireRefusal(SyncEndpointContext good, SyncEndpointContext candidate, string fragment,
        string label)
    {
        var reasons = SyncEndpointContext.Validate(candidate, good.PairId);
        var matched = reasons.Any(reason => reason.Contains(fragment, StringComparison.Ordinal));
        Require(matched, "the append rule refuses " + label + " (" + string.Join("; ", reasons) + ")");
        try
        {
            SyncEndpointContext.RequireRetainable(candidate, good.PairId);
        }
        catch (ArgumentException error)
        {
            Require(error.Message.Contains(fragment, StringComparison.Ordinal),
                "the append entry point throws the same refusal for " + label);
            return;
        }
        throw new InvalidDataException("The rejected endpoint context was accepted: " + label);
    }

    // ----- 5. the optional projection field ------------------------------------------------------------

    private static void ProjectionFieldIsOptional()
    {
        Claim("the projection keeps an optional endpoint context that stays absent when it is null");
        var projection = new SyncAuthorityActivityProjection(DateTimeOffset.UtcNow, "pair-projection",
            "relative.txt", "Unchanged", "No retained explanation.", null);
        Require(projection.EndpointContext is null,
            "an activity projection still constructs without an endpoint context");
        Require(!JsonSerializer.Serialize(projection).Contains("EndpointContext", StringComparison.Ordinal),
            "a null projection context adds no member to the serialized projection");
    }

    // ----- 6. the real producer, idempotency, older payloads and pair mutation -------------------------

    private static async Task ProducersAsync(string root)
    {
        Claim("the real retain path captures the exact context into SQLite, stays idempotent and never rewrites it");
        var caseRoot = Path.Combine(root, "producers");
        Directory.CreateDirectory(caseRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(caseRoot, "state", "instance.lock"));
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("eps1-producers", "eps1-profile", "owner-fixture", "{}");
        using var client = new HttpClient(new RejectNetwork());
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, client,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new NoTokens(), authorityDatabase: database);
        var pair = BuildPair("Producer pair",
            CloudOrLocal(root, SyncEndpointKind.GoogleDrive, "producer-left"),
            SyncEndpoint.Local(LocalFolder(root, "producer-local"), IdentityOf(LocalFolder(root, "producer-local"))));
        var expected = SyncEndpointContext.Capture(pair);
        await host.RetainProgressForTestAsync(new SyncAuthorityBatchProgress(pair, "Pending", null, null, null));
        Require(!host.ActivityLogIncomplete,
            "the real progress producer retained its presentation record without a gap");
        var rows = await DetailRowsAsync(database.DatabasePath, pair.Id);
        Require(rows.Count == 1, "the real producer retained exactly one presentation row for the pair");
        var retained = ContextOf(rows[0]);
        Require(retained is not null, "the retained detail JSON carries the endpoint context member");
        Require(retained == expected,
            "the retained SQLite detail JSON round-trips to the exact captured context, including the reversed ports and both folder identities");
        Require(rows[0].Contains("\"Side\":\"Left\"", StringComparison.Ordinal) &&
                rows[0].Contains("\"Kind\":\"GoogleDrive\"", StringComparison.Ordinal),
            "the retained context names its sides and kinds by stable name rather than by enum ordinal");

        var item = new SyncActivityEvent("eps1-idempotent", DateTimeOffset.UtcNow,
            [SyncActivityCategories.Sync], "Information", "Folder pair", "Sync started", "Started",
            null, pair.Id, null, null, "", expected);
        await database.AppendActivityEventAsync(item);
        await database.AppendActivityEventAsync(item);
        Require(await CountAsync(database.DatabasePath, item.EventId) == 1,
            "replaying an identical event with an identical context stays idempotent");
        var changedRefused = false;
        try
        {
            await database.AppendActivityEventAsync(item with
                { EndpointContext = expected with { CapturedName = "A different captured name" } });
        }
        catch (InvalidDataException) { changedRefused = true; }
        Require(changedRefused, "replaying one event identity with a changed context is refused");

        var foreignPairRefused = false;
        try
        {
            await database.AppendActivityEventAsync(item with { EventId = "eps1-foreign-pair", PairId = "another-pair" });
        }
        catch (ArgumentException error)
        {
            foreignPairRefused = error.Message.Contains("names a different folder pair", StringComparison.Ordinal);
        }
        Require(foreignPairRefused && await CountAsync(database.DatabasePath, "eps1-foreign-pair") == 0,
            "the real append refuses a context whose pair id is not the event's pair id and writes nothing");

        // The producer pair's ordered left side is a cloud endpoint and its right side is the local folder.
        var malformed = expected with { Right = expected.Right with { ConnectionId = "account-x" } };
        var malformedRefused = false;
        try
        {
            await database.AppendActivityEventAsync(item with { EventId = "eps1-malformed", EndpointContext = malformed });
        }
        catch (ArgumentException error)
        {
            malformedRefused = error.Message.Contains("cannot carry a provider, cloud account", StringComparison.Ordinal);
        }
        Require(malformedRefused && await CountAsync(database.DatabasePath, "eps1-malformed") == 0,
            "the real append refuses cloud metadata on a local endpoint and writes nothing");

        // An older build wrote no context member at all; that exact payload must replay idempotently and
        // must not gain a null member from this change.
        var older = new SyncActivityEvent("eps1-older-payload", DateTimeOffset.UtcNow, [SyncActivityCategories.Sync],
            "Information", "Folder pair", "Sync started", "Started");
        var olderPayload = JsonSerializer.Serialize(older with { OccurredUtc = older.OccurredUtc.ToUniversalTime() });
        Require(!olderPayload.Contains("EndpointContext", StringComparison.Ordinal),
            "an event without a context serializes with no context member, so an older retained payload stays byte-compatible");
        await WriteOlderPayloadAsync(database.DatabasePath, older.EventId, olderPayload,
            older.OccurredUtc.ToUniversalTime());
        await database.AppendActivityEventAsync(older);
        Require(await CountAsync(database.DatabasePath, older.EventId) == 1,
            "replaying an older retained payload that has no context member is idempotent");
        Require(await DetailAsync(database.DatabasePath, older.EventId) == olderPayload,
            "the older payload is retained byte-identically, with no added null member");

        var snapshotBefore = await DetailAsync(database.DatabasePath, "eps1-idempotent");
        var rebound = pair with
        {
            Name = "Renamed after capture",
            RightEndpoint = pair.RightEndpoint! with { LocalPath = LocalFolder(root, "producer-rebound") }
        };
        var recaptured = SyncEndpointContext.Capture(rebound);
        Require(recaptured.CapturedName == "Renamed after capture" &&
                recaptured.Right.LocalPath != expected.Right.LocalPath &&
                recaptured.PairId == expected.PairId,
            "a renamed and rebound pair captures its own new values under the same pair identity");
        Require(await DetailAsync(database.DatabasePath, "eps1-idempotent") == snapshotBefore,
            "the already retained snapshot is unchanged by a later pair rename or rebind");

        var gapPair = pair with { Provider = "box", EndpointSchemaVersion = null, LeftEndpoint = null, RightEndpoint = null };
        var rowsBeforeGap = (await DetailRowsAsync(database.DatabasePath, pair.Id)).Count;
        await host.RetainProgressForTestAsync(new SyncAuthorityBatchProgress(gapPair,
            "Failed", null, null, null, null, new SyncComparisonProgress("Comparing files", "item.txt", 3, 9,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        Require(host.ActivityLogIncomplete &&
                (await DetailRowsAsync(database.DatabasePath, pair.Id)).Count == rowsBeforeGap,
            "a pair whose endpoints cannot be described follows the existing append failure/gap behaviour and retains no invented context");
    }

    // ----- 7. local/local never consults an existing primary Dropbox account ---------------------------

    private static async Task LocalLocalDoesNotConsultThePrimaryAccountAsync(string root)
    {
        Claim("an explicit local/local pair retains two local endpoints and zero cloud accounts while a primary Dropbox account exists");
        var caseRoot = Path.Combine(root, "local-local");
        Directory.CreateDirectory(caseRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(caseRoot, "state", "instance.lock"));
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        await connections.SaveNewAsync(new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "acct-eps1-primary", "ns-eps1-root", "ns-eps1-home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes));
        Require(connections.Load()?.AccountId == "acct-eps1-primary",
            "a real primary Dropbox account binding exists in the same state root before the pair runs");

        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("eps1-local-local", "eps1-profile", "owner-fixture", "{}");
        using var client = new HttpClient(new RejectNetwork());
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, client,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new NoTokens(), authorityDatabase: database);
        var localLeft = LocalFolder(root, "local-local-left");
        var localRight = LocalFolder(root, "local-local-right");
        var pair = BuildPair("Local to local", SyncEndpoint.Local(localLeft, IdentityOf(localLeft)),
            SyncEndpoint.Local(localRight, IdentityOf(localRight)));
        Require(pair.Provider == SyncProviders.Local && pair.DropboxFolderId.Length > 0 &&
                SyncEndpointsCompatibility.FromSyncPair(pair).Endpoints is null,
            "the local/local pair's legacy projection can form no cloud endpoint at all, so a fallback read would have invented one");
        await host.RetainProgressForTestAsync(new SyncAuthorityBatchProgress(pair, "Pending", null, null, null));
        Require(!host.ActivityLogIncomplete, "the local/local pair's progress record is retained without a gap");
        var rows = await DetailRowsAsync(database.DatabasePath, pair.Id);
        Require(rows.Count == 1, "the local/local pair retained its presentation record");
        var context = ContextOf(rows[0]);
        Require(context is not null && context.Left.Kind == SyncEndpointKind.Local &&
                context.Right.Kind == SyncEndpointKind.Local && context.Left.LocalPath == localLeft &&
                context.Right.LocalPath == localRight,
            "the retained context carries both local endpoints exactly");
        Require(context!.Left.Provider is null && context.Left.ConnectionId is null &&
                context.Left.NamespaceId is null && context.Left.FolderId is null && context.Left.FolderPath is null &&
                context.Right.Provider is null && context.Right.ConnectionId is null &&
                context.Right.NamespaceId is null && context.Right.FolderId is null && context.Right.FolderPath is null,
            "zero cloud accounts are assigned to either local endpoint");
        Require(!rows[0].Contains("acct-eps1-primary", StringComparison.Ordinal) &&
                !rows[0].Contains("ns-eps1", StringComparison.Ordinal),
            "the existing primary Dropbox account and its namespace appear nowhere in the retained record");
    }

    // ----- 8. an absent legacy account stays unknown ---------------------------------------------------

    private static async Task LegacyUnknownStaysUnknownAsync(string root)
    {
        Claim("a legacy pair with no saved account or local identity stays unknown rather than guessed");
        var caseRoot = Path.Combine(root, "legacy-unknown");
        Directory.CreateDirectory(caseRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(caseRoot, "state", "instance.lock"));
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        await connections.SaveNewAsync(new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "acct-eps1-legacy-primary", "ns-eps1-legacy", "ns-eps1-legacy-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes));
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("eps1-legacy", "eps1-profile", "owner-fixture", "{}");
        using var client = new HttpClient(new RejectNetwork());
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, client,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new NoTokens(), authorityDatabase: database);
        var localPath = LocalFolder(root, "legacy-local");
        var unknown = new SyncPair("legacy-unknown-account", "Legacy unknown", localPath, 0, 0, "/Legacy", "id:legacy");
        Require(unknown.ConnectionId.Length == 0 && unknown.Provider == SyncProviders.Dropbox &&
                unknown.LocalVolumeSerialNumber == 0 && unknown.LocalRootFileId == 0,
            "the legacy fixture saves no account id and no complete local folder identity");
        await host.RetainProgressForTestAsync(new SyncAuthorityBatchProgress(unknown, "Pending", null, null, null));
        Require(!host.ActivityLogIncomplete, "the legacy pair with an absent account is still retained without a gap");
        var unknownRow = (await DetailRowsAsync(database.DatabasePath, unknown.Id)).Single();
        var unknownContext = ContextOf(unknownRow)!;
        Require(unknownContext.Right.Kind == SyncEndpointKind.Dropbox &&
                unknownContext.Right.Provider == SyncProviders.Dropbox &&
                unknownContext.Right.ConnectionId is null && unknownContext.Right.NamespaceId is null &&
                unknownContext.Right.FolderId == "id:legacy" && unknownContext.Right.FolderPath == "/Legacy",
            "the legacy pair keeps its declared provider, folder id and recorded path and leaves the missing account unknown");
        Require(unknownContext.Left.Kind == SyncEndpointKind.Local && unknownContext.Left.LocalPath == localPath &&
                unknownContext.Left.LocalVolumeSerialNumber is null && unknownContext.Left.LocalRootFileId is null,
            "an incomplete legacy local identity is recorded as absent rather than invented");
        Require(!unknownRow.Contains("acct-eps1-legacy-primary", StringComparison.Ordinal) &&
                !unknownRow.Contains("ns-eps1-legacy", StringComparison.Ordinal),
            "the verified primary account that exists on this installation is never substituted for the absent legacy account");

        var declared = unknown with
        {
            Id = "legacy-declared-account", ConnectionId = "conn-legacy-declared",
            LocalVolumeSerialNumber = UnknownVolume, LocalRootFileId = 4242
        };
        var declaredContext = SyncEndpointContext.Capture(declared);
        Require(declaredContext.Right.ConnectionId == "conn-legacy-declared" &&
                declaredContext.Left.LocalVolumeSerialNumber == UnknownVolume &&
                declaredContext.Left.LocalRootFileId == 4242,
            "a legacy pair that does save its account is captured with exactly that declared account");
    }

    private const uint UnknownVolume = 7351;

    // ----- shared fixtures -----------------------------------------------------------------------------

    private static SyncEndpoint CloudOrLocal(string root, SyncEndpointKind kind, string tag) => kind switch
    {
        SyncEndpointKind.Local => SyncEndpoint.Local(LocalFolder(root, tag), IdentityOf(LocalFolder(root, tag))),
        SyncEndpointKind.Dropbox => SyncEndpoint.Cloud(kind, "connection-" + tag, "namespace-" + tag,
            "id:" + tag, "/" + tag),
        _ => SyncEndpoint.Cloud(kind, "connection-" + tag, "namespace-" + tag, "drive-" + tag, "Drive/" + tag)
    };

    private static SyncPair BuildPair(string name, SyncEndpoint left, SyncEndpoint right)
    {
        var endpoints = new SyncPairEndpoints(left, right, SyncMode.TwoWay);
        return left.IsLocal || right.IsLocal
            ? SyncEndpointPairDraft.Create(endpoints, name)
            : SyncCloudPairConfiguration.CreateDraft(endpoints, name);
    }

    private static string LocalFolder(string root, string name) =>
        Directory.CreateDirectory(Path.Combine(root, name)).FullName;

    private static SyncLocalEndpointIdentity IdentityOf(string folder)
    {
        using var lease = PilotDirectoryLease.Open(folder);
        return new SyncLocalEndpointIdentity(lease.Identity.VolumeSerialNumber, lease.Identity.FileId);
    }

    private static bool MetadataMatchesKind(SyncEndpointContextSide side) => side.Kind == SyncEndpointKind.Local
        ? side.LocalPath is not null && side.LocalVolumeSerialNumber is > 0 && side.LocalRootFileId is > 0 &&
          side.Provider is null && side.ConnectionId is null && side.NamespaceId is null &&
          side.FolderId is null && side.FolderPath is null
        : side.LocalPath is null && side.LocalVolumeSerialNumber is null && side.LocalRootFileId is null &&
          side.Provider == SyncEndpoints.ProviderLiteral(side.Kind) && side.ConnectionId is not null &&
          side.NamespaceId is not null && (side.FolderId is not null || side.FolderPath is not null);

    private static SyncEndpointContext? ContextOf(string detailJson)
    {
        var node = JsonNode.Parse(detailJson)?.AsObject();
        return node?["EndpointContext"]?.Deserialize<SyncEndpointContext>();
    }

    private static async Task<IReadOnlyList<string>> DetailRowsAsync(string databasePath, string pairId)
    {
        var rows = new List<string>();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT detail_json FROM history_event WHERE event_kind='presentation' " +
            "AND json_extract(detail_json,'$.PairId')=$pair ORDER BY rowid;";
        command.Parameters.AddWithValue("$pair", pairId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }

    private static async Task<string?> DetailAsync(string databasePath, string eventId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT detail_json FROM history_event WHERE history_event_id=$id;";
        command.Parameters.AddWithValue("$id", eventId);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (string)value;
    }

    private static async Task<long> CountAsync(string databasePath, string eventId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM history_event WHERE history_event_id=$id;";
        command.Parameters.AddWithValue("$id", eventId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task WriteOlderPayloadAsync(string databasePath, string eventId, string payload,
        DateTimeOffset occurredUtc)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO history_event(history_event_id,operation_id,event_kind,detail_json," +
            "occurred_utc) VALUES($id,NULL,'presentation',$detail,$utc);";
        command.Parameters.AddWithValue("$id", eventId);
        command.Parameters.AddWithValue("$detail", payload);
        command.Parameters.AddWithValue("$utc", occurredUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private static void RequireThrows(Action action, string fragment, string message)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            Require(error.Message.Contains(fragment, StringComparison.Ordinal),
                message + " (" + error.GetType().Name + ")");
            return;
        }
        throw new InvalidDataException("The refused capture succeeded: " + message);
    }

    private static void Claim(string description)
    {
        _cases++;
        Console.WriteLine("CASE EPS1 " + _cases + ": " + description);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("Endpoint context capture assertion failed: " + message);
        _assertions++;
        Console.WriteLine("PASS EPS1 " + message);
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This fixture has no network authority.");
    }

    private sealed class NoTokens : IDropboxRefreshTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task WriteAsync(string value, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
    }
}
