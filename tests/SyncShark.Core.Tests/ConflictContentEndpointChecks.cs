using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// The Core conflict-content read for every ordered endpoint orientation. The authority is a real SQLite
// database, the local sides are real Windows files and the transport is a real HttpClient; only the provider
// wire is a bounded fixture. Every case drives the production SyncAuthorityRuntimeHost.LoadConflictContentAsync
// over retained rows, and no adapter stands in for the decision under proof: the local side is read by the
// production reviewed-file read and the cloud side by the production historical-revision read.
internal static class ConflictContentEndpointChecks
{
    internal const string InstallationId = "installation-conflict-content";
    internal const string ProfileId = "profile-conflict-content";

    private static readonly List<Dictionary<string, object?>> Results = [];
    private static int _assertions;
    private static string _reportRoot = "";

    internal static async Task RunAsync(string root)
    {
        var reportRoot = Path.GetFullPath(root);
        _reportRoot = reportRoot;
        Directory.CreateDirectory(reportRoot);
        Results.Clear();
        _assertions = 0;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Conflict content endpoint checks require Windows NTFS identities.");

        foreach (var left in Kinds())
        foreach (var right in Kinds())
            await OrientationAsync(left, right);
        await LegacyNoDocumentPairAsync();
        await ExactRetainedConflictAsync();
        await SameProviderTwoAccountsAsync();
        await StaleVersionAsync();
        await StaleAccountAsync();
        await PairRebindAsync();
        await BoundsAndNoEffectAsync();
        await MalformedDocumentRefusalAsync();
        await DriveToDriveTypedAsync();
        await DriveOnEitherSideAsync();
        await DriveUnavailableVersionsAsync();
        await DriveBoundsAndDuringReadChangeAsync();
        await CloudAuthorityCrossingAsync();
        await LegacyExplicitIdentityRefusalAsync();
        await MembershipDriftDuringReadAsync();

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "ConflictContentEndpointChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = reportRoot,
            ["assertions"] = _assertions,
            ["cases"] = Results
        };
        await File.WriteAllTextAsync(Path.Combine(reportRoot, "conflict-content-endpoint-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS conflict content endpoint: " + _assertions + " assertions across " + Results.Count +
            " cases; nine ordered endpoint orientations plus legacy, exact-conflict, two-account, stale-version, " +
            "stale-account, rebind, bounds/no-effect and malformed-document cases.");
    }

    private static IEnumerable<SyncEndpointKind> Kinds()
    {
        yield return SyncEndpointKind.Local;
        yield return SyncEndpointKind.Dropbox;
        yield return SyncEndpointKind.GoogleDrive;
    }

    private static string Word(SyncEndpointKind kind) => kind switch
    {
        SyncEndpointKind.Local => "local",
        SyncEndpointKind.Dropbox => "dropbox",
        _ => "google-drive"
    };

    // The engine layout's own rule: a physical local endpoint always occupies the local port, and for a pair
    // with no local endpoint the ordered left endpoint keeps the retained logical-left port.
    private static bool LeftCarriesLocalPort(SyncEndpointKind left, SyncEndpointKind right) =>
        left == SyncEndpointKind.Local || right != SyncEndpointKind.Local;

    private static void Claim(string id, string requirement) => Results.Add(new Dictionary<string, object?>
    {
        ["id"] = id,
        ["requirement"] = requirement,
        ["observed"] = "pending"
    });

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        _assertions++;
        var current = Results[^1];
        var observed = (string?)current["observed"];
        current["observed"] = observed == "pending" ? message : observed + " | " + message;
        current["state"] = "observed";
        Console.WriteLine("PASS " + message);
    }

    private static async Task<TException> Refused<TException>(Func<Task> operation, string message)
        where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException expected)
        {
            Require(!string.IsNullOrWhiteSpace(expected.Message),
                message + " (" + expected.GetType().Name + ")");
            return expected;
        }
        throw new InvalidDataException("The operation completed where " + typeof(TException).Name +
            " was required: " + message);
    }

    private static async Task OrientationAsync(SyncEndpointKind leftKind, SyncEndpointKind rightKind)
    {
        var slug = Word(leftKind) + "-to-" + Word(rightKind);
        Claim("CC1-orientation-" + slug, "The conflict content preview of a " + slug +
            " pair reads each retained side from the endpoint its own layout assigns");
        await using var rig = await Rig.CreateAsync("orientation-" + slug);
        const string relative = "conflict.txt";
        if (leftKind == SyncEndpointKind.Local) rig.AddLocal("left");
        else rig.AddCloud("left", leftKind);
        if (rightKind == SyncEndpointKind.Local) rig.AddLocal("right");
        else rig.AddCloud("right", rightKind);
        var pair = rig.CreatePair(leftKind, rightKind, "Conflict content " + slug);
        await rig.RetainAsync(pair);
        var localPortBytes = Encoding.UTF8.GetBytes("local port bytes " + slug);
        var dropboxPortBytes = Encoding.UTF8.GetBytes("dropbox port bytes " + slug);
        var fixture = await rig.RetainConflictAsync(pair, relative, localPortBytes, dropboxPortBytes);
        var requestsBefore = rig.Wire.Requests;
        var expectsGoogle = leftKind == SyncEndpointKind.GoogleDrive || rightKind == SyncEndpointKind.GoogleDrive;
        if (expectsGoogle)
        {
            // CFC-R2: a Google Drive side is no longer blanket-refused as a missing capability. This
            // orientation rig has no connected Google Drive account, so the exact retained account is
            // required first and the preview refuses without any provider call, another store, another
            // account or the latest bytes. The working Drive read is proved with a connected account in
            // cases CC10 and CC11.
            var captured = SyncEndpointContext.Capture(pair);
            var capturedLocal = fixture.LeftIsLocalPort ? captured.Left : captured.Right;
            Require(captured.Left.Kind == leftKind && captured.Right.Kind == rightKind &&
                    capturedLocal.CompatibilityPort == SyncAuthorityCloudSides.LogicalLeft &&
                    (leftKind == SyncEndpointKind.Local || rightKind == SyncEndpointKind.Local
                        ? capturedLocal.IsLocal
                        : ReferenceEquals(capturedLocal, captured.Left)),
                "CC1 " + slug + ": the pair's ordered endpoints and layout keep the local port on the local endpoint");
            var refusal = await Refused<GoogleDriveBlockedException>(() => rig.LoadAsync(pair.Id, relative),
                "CC1 " + slug + ": a Google Drive side refuses without its exact connected retained account");
            Require(refusal.Message.Contains("Reconnect", StringComparison.Ordinal) ||
                    refusal.Message.Contains("not connected", StringComparison.Ordinal),
                "CC1 " + slug + ": the Drive refusal names the exact account that must be reconnected");
            Require(rig.Wire.GoogleCalls == 0 && rig.Wire.Requests == requestsBefore,
                "CC1 " + slug + ": the refused Drive side issued no provider request and required no unrelated login");
            Require(rig.Wire.Uploads == 0 &&
                    (await rig.Db.LoadConflictAsync(fixture.ConflictId)).Status == "open",
                "CC1 " + slug + ": the refused Drive preview changed neither version and left the conflict open");
            return;
        }
        var content = await rig.LoadAsync(pair.Id, relative);
        var context = content.EndpointContext ?? throw new InvalidDataException(
            "The preview of a " + slug + " pair did not capture the pair's endpoint context.");

        Require(string.Equals(context.PairId, pair.Id, StringComparison.Ordinal) &&
                context.Left.Kind == leftKind && context.Right.Kind == rightKind &&
                context.Left.Side == SyncPairSide.Left && context.Right.Side == SyncPairSide.Right,
            "CC1 " + slug + ": the captured endpoint context keeps the pair's own ordered endpoints");
        var localPortSide = fixture.LeftIsLocalPort ? context.Left : context.Right;
        var dropboxPortSide = fixture.LeftIsLocalPort ? context.Right : context.Left;
        Require(localPortSide.CompatibilityPort == SyncAuthorityCloudSides.LogicalLeft &&
                dropboxPortSide.CompatibilityPort == SyncAuthorityCloudSides.LogicalRight,
            "CC1 " + slug + ": the two ordered sides carry the layout's own local and cloud compatibility ports");
        Require(leftKind != SyncEndpointKind.Local || rightKind != SyncEndpointKind.Local ||
                ReferenceEquals(localPortSide, context.Left),
            "CC1 " + slug + ": a pair of two local folders keeps the retained left-then-right port order");
        if (leftKind == SyncEndpointKind.Local) Require(ReferenceEquals(localPortSide, context.Left),
            "CC1 " + slug + ": the local port is the pair's local folder, not a substituted cloud account");
        if (rightKind == SyncEndpointKind.Local && leftKind != SyncEndpointKind.Local)
            Require(ReferenceEquals(localPortSide, context.Right),
                "CC1 " + slug + ": a cloud-first layout reverses its ports so the local folder keeps the local port");

        {
            Require(content.Note is null && content.LocalBytes is not null && content.DropboxBytes is not null,
                "CC1 " + slug + ": both retained versions were returned without an unavailable-content note (note: " +
                (content.Note ?? "none") + "; routed: " + rig.Wire.RoutedTail(6) + ")");
            Require(content.LocalBytes!.SequenceEqual(localPortBytes),
                "CC1 " + slug + ": the local port returned the exact retained version of its own endpoint");
            Require(content.DropboxBytes!.SequenceEqual(dropboxPortBytes),
                "CC1 " + slug + ": the cloud port returned the exact retained version of its own endpoint");
            Require(content.LocalSize == localPortBytes.LongLength && content.DropboxSize == dropboxPortBytes.LongLength,
                "CC1 " + slug + ": the preview metadata carries each retained version's own exact length");
            Require(content.LocalHash == fixture.LocalPortHash && content.DropboxHash == fixture.DropboxPortHash,
                "CC1 " + slug + ": the preview metadata carries each retained version's own exact content hash");
            if (leftKind != SyncEndpointKind.Local && rightKind != SyncEndpointKind.Local)
                Require(pair.LocalRoot.Length == 0,
                    "CC1 " + slug + ": a pair of two cloud endpoints retains no compatibility local root, so no local file could have supplied either side");
            if (leftKind == SyncEndpointKind.Local && rightKind == SyncEndpointKind.Local)
                Require(fixture.LocalPortPath != fixture.DropboxPortPath && File.Exists(fixture.LocalPortPath) &&
                        File.Exists(fixture.DropboxPortPath),
                    "CC1 " + slug + ": the two local folders supplied their own distinct retained versions");
        }

        Require(rig.Wire.Uploads == 0 && rig.Wire.Deletes == 0 && rig.Wire.Copies == 0 && rig.Wire.Restores == 0,
            "CC1 " + slug + ": the preview issued no provider mutation");
        Require((await rig.Db.LoadConflictAsync(fixture.ConflictId)).Status == "open",
            "CC1 " + slug + ": the preview left the exact retained conflict open");
    }

    private static async Task LegacyNoDocumentPairAsync()
    {
        Claim("CC2-legacy-no-document", "A genuine document-less legacy pair still resolves left-local/right-cloud and previews both exact versions");
        await using var rig = await Rig.CreateAsync("legacy-no-document");
        const string relative = "legacy.txt";
        rig.AddLocal("local");
        rig.AddCloud("cloud", SyncEndpointKind.Dropbox);
        var local = rig.Local("local");
        var cloud = rig.Cloud("cloud");
        var baseBytes = Encoding.UTF8.GetBytes("legacy base bytes");
        await File.WriteAllBytesAsync(PilotPath.ResolveContainedPath(local.Path, relative), baseBytes);
        rig.Wire.Put(cloud.Account!, cloud.FolderPath, relative, baseBytes);
        var pair = rig.CreateLegacyPair("local", "cloud", "Legacy content pair");
        await rig.RetainAsync(pair);
        var observer = new SyncAuthorityObservationCoordinator(pair, rig.ClientFor("cloud"), rig.Db,
            rig.PortRootBindingId(pair, SyncAuthorityCloudSides.LogicalLeft),
            rig.PortRootBindingId(pair, SyncAuthorityCloudSides.LogicalRight));
        await observer.ObserveAsync("initial");
        await observer.ObserveAsync("initial");
        await new SyncAuthorityInitialPlanner(rig.Db).AdoptAsync(pair.Id);
        await rig.Db.SetPairStateAsync(pair.Id, "active");

        var localBytes = Encoding.UTF8.GetBytes("legacy local conflict bytes");
        var cloudBytes = Encoding.UTF8.GetBytes("legacy cloud conflict bytes, different length");
        await File.WriteAllBytesAsync(PilotPath.ResolveContainedPath(local.Path, relative), localBytes);
        var replaced = rig.Wire.Put(cloud.Account!, cloud.FolderPath, relative, cloudBytes);
        await observer.ObserveAsync("monitor");
        var admission = await rig.Db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id, null, null,
            "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await rig.Db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        var plan = (await new SyncAuthorityReconciliationPlanner(rig.Db, rig.Options, admission: admission).PlanAsync(pair.Id))
            .Single(outcome => outcome.RelativePath == relative);
        Require(plan.Kind == SyncAuthorityPlannerOutcomeKind.Conflict && plan.ConflictId is not null,
            "CC2 the production planner retained a conflict for the document-less legacy pair");
        Require(pair.EndpointDocument is null && pair.LocalRoot.Length > 0 && pair.Provider == SyncProviders.Dropbox,
            "CC2 the pair under test really has no endpoint document and keeps its legacy local plus cloud fields");

        var content = await rig.LoadAsync(pair.Id, relative);
        var context = content.EndpointContext ?? throw new InvalidDataException(
            "The legacy pair's preview did not capture an endpoint context.");
        Require(string.Equals(content.ConflictId, plan.ConflictId, StringComparison.Ordinal) &&
                context.Left.Kind == SyncEndpointKind.Local && context.Right.Kind == SyncEndpointKind.Dropbox &&
                context.Left.CompatibilityPort == SyncAuthorityCloudSides.LogicalLeft &&
                context.Right.CompatibilityPort == SyncAuthorityCloudSides.LogicalRight,
            "CC2 the document-less legacy pair resolved as left-local and right-cloud on the layout's own ports");
        Require(content.LocalBytes is not null && content.LocalBytes.SequenceEqual(localBytes) &&
                content.DropboxBytes is not null && content.DropboxBytes.SequenceEqual(cloudBytes),
            "CC2 the legacy pair previewed the exact retained local version and the exact retained cloud revision");
        Require(string.Equals(content.DropboxHash, replaced.Hash, StringComparison.OrdinalIgnoreCase) &&
                content.DropboxSize == cloudBytes.LongLength,
            "CC2 the legacy cloud revision's own hash and length reached the preview metadata");
        Require((await rig.Db.LoadConflictAsync(plan.ConflictId!)).Status == "open" && rig.Wire.Uploads == 0,
            "CC2 the legacy preview changed neither version and issued no provider mutation");
    }

    private static async Task ExactRetainedConflictAsync()
    {
        Claim("CC3-exact-retained-conflict", "A bound conflict id and generation select that exact retained conflict instead of the newest open one");
        await using var rig = await Rig.CreateAsync("exact-retained-conflict");
        const string relative = "exact.txt";
        rig.AddLocal("left");
        rig.AddCloud("right", SyncEndpointKind.Dropbox);
        var pair = rig.CreatePair(SyncEndpointKind.Local, SyncEndpointKind.Dropbox, "Exact retained conflict pair");
        await rig.RetainAsync(pair);
        var olderBytes = Encoding.UTF8.GetBytes("first cloud revision");
        var newerBytes = Encoding.UTF8.GetBytes("second cloud revision");
        var first = await rig.RetainConflictAsync(pair, relative, Encoding.UTF8.GetBytes("first local"),
            olderBytes, "older");
        var second = await rig.RetainConflictAsync(pair, relative, Encoding.UTF8.GetBytes("first local"),
            newerBytes, "newer", generation: 2, objectId: first.ObjectId, reusingLocalPortVersion: first);
        var older = await rig.Db.LoadConflictAsync(first.ConflictId);
        var newer = await rig.Db.LoadConflictAsync(second.ConflictId);
        Require(newer.ConflictGeneration > older.ConflictGeneration,
            "CC3 the fixture retained two open conflicts for one path at two exact generations");

        var current = await rig.LoadAsync(pair.Id, relative);
        Require(string.Equals(current.ConflictId, second.ConflictId, StringComparison.Ordinal) &&
                current.DropboxBytes is not null && current.DropboxBytes.SequenceEqual(newerBytes),
            "CC3 a caller with no bound generation still resolves the current open conflict for the path");
        var bound = await rig.LoadAsync(pair.Id, relative, conflictId: first.ConflictId,
            conflictGeneration: older.ConflictGeneration);
        Require(string.Equals(bound.ConflictId, first.ConflictId, StringComparison.Ordinal) &&
                bound.DropboxBytes is not null && bound.DropboxBytes.SequenceEqual(olderBytes) &&
                !bound.DropboxBytes.SequenceEqual(newerBytes),
            "CC3 the bound conflict id and generation returned that exact retained conflict's version, not the newer one");
        var requestsBefore = rig.Wire.Requests;
        await Refused<InvalidOperationException>(
            () => rig.LoadAsync(pair.Id, relative, conflictId: first.ConflictId,
                conflictGeneration: newer.ConflictGeneration),
            "CC3 a generation that no longer matches the bound conflict is refused before any read");
        Require(rig.Wire.Requests == requestsBefore,
            "CC3 the refused stale generation issued no provider request at all");
    }

    private static async Task SameProviderTwoAccountsAsync()
    {
        Claim("CC4-two-accounts-one-provider", "Two Dropbox endpoints of one pair read their own retained accounts' revisions");
        await using var rig = await Rig.CreateAsync("two-accounts-one-provider");
        const string relative = "shared-name.txt";
        rig.AddCloud("left", SyncEndpointKind.Dropbox);
        rig.AddCloud("right", SyncEndpointKind.Dropbox);
        var left = rig.Cloud("left");
        var right = rig.Cloud("right");
        var pair = rig.CreatePair(SyncEndpointKind.Dropbox, SyncEndpointKind.Dropbox, "Two account pair");
        await rig.RetainAsync(pair);
        var leftBytes = Encoding.UTF8.GetBytes("left account version");
        var rightBytes = Encoding.UTF8.GetBytes("right account version with a different length");
        var fixture = await rig.RetainConflictAsync(pair, relative, leftBytes, rightBytes);
        var content = await rig.LoadAsync(pair.Id, relative);
        Require(content.LocalBytes is not null && content.LocalBytes.SequenceEqual(leftBytes) &&
                content.DropboxBytes is not null && content.DropboxBytes.SequenceEqual(rightBytes),
            "CC4 each cloud side returned its own account's retained revision, never the other account's bytes");
        Require(left.Account!.DownloadRequests == 1 && right.Account!.DownloadRequests == 1,
            "CC4 each retained account served exactly one historical revision read");
        Require(content.EndpointContext!.Left.ConnectionId == left.ConnectionId &&
                content.EndpointContext.Right.ConnectionId == right.ConnectionId,
            "CC4 the captured context names each side's own retained account");
        Require(rig.Wire.Uploads == 0 && rig.Wire.Deletes == 0 &&
                (await rig.Db.LoadConflictAsync(fixture.ConflictId)).Status == "open",
            "CC4 the two-account preview changed neither account's content");
    }

    private static async Task StaleVersionAsync()
    {
        Claim("CC5-stale-version", "An unavailable original version returns metadata with an explicit note and never newer bytes");
        await using var rig = await Rig.CreateAsync("stale-version");
        const string relative = "stale.txt";
        rig.AddLocal("left");
        rig.AddCloud("right", SyncEndpointKind.Dropbox);
        var cloud = rig.Cloud("right");
        var pair = rig.CreatePair(SyncEndpointKind.Local, SyncEndpointKind.Dropbox, "Stale version pair");
        await rig.RetainAsync(pair);
        var cloudBytes = Encoding.UTF8.GetBytes("vanished cloud revision");
        var retired = await rig.RetainConflictAsync(pair, relative, Encoding.UTF8.GetBytes("stale local version"),
            cloudBytes);
        // The provider retains a newer revision of the same file while the reviewed revision disappears.
        var newer = rig.Wire.Put(cloud.Account!, cloud.FolderPath, relative,
            Encoding.UTF8.GetBytes("newer cloud bytes that must never be substituted"));
        rig.Wire.ForgetRevision(cloud.Account!, retired.DropboxPortRevision);
        var content = await rig.LoadAsync(pair.Id, relative);
        Require(content.LocalBytes is null && content.DropboxBytes is null && content.Note is not null,
            "CC5 a disappeared reviewed cloud revision left both versions unread with an explicit note");
        Require(content.Note!.Contains("without changing either version", StringComparison.Ordinal) &&
                content.Note.Contains("Dropbox", StringComparison.Ordinal) &&
                content.DropboxHash == retired.DropboxPortHash && content.DropboxSize == cloudBytes.LongLength,
            "CC5 the note explains the unavailable content while the retained metadata stays available");
        Require(newer.Bytes.Length > 0,
            "CC5 the newer cloud revision really exists on the provider and was still never returned");

        // The reviewed local file is gone: the local side cannot be supplied and no bytes are presented.
        var deletedBytes = Encoding.UTF8.GetBytes("local version whose file is then removed");
        var deleted = await rig.RetainConflictAsync(pair, "deleted-local.txt", deletedBytes,
            Encoding.UTF8.GetBytes("present cloud version"), "deleted-local");
        File.Delete(deleted.LocalPortPath);
        var deletedContent = await rig.LoadAsync(pair.Id, "deleted-local.txt");
        Require(deletedContent.LocalBytes is null && deletedContent.DropboxBytes is null &&
                deletedContent.Note is not null && deletedContent.LocalSize == deletedBytes.LongLength &&
                deletedContent.LocalHash == deleted.LocalPortHash &&
                (await rig.Db.LoadConflictAsync(deleted.ConflictId)).Status == "open",
            "CC5 a reviewed local version whose file is gone returned metadata with an explicit note, no bytes and left the conflict open");

        // A local file rewritten after it was reviewed is refused rather than previewed as newer content.
        var reviewedBytes = Encoding.UTF8.GetBytes("reviewed local version");
        var changed = await rig.RetainConflictAsync(pair, "changed-local.txt", reviewedBytes,
            Encoding.UTF8.GetBytes("changed cloud version"), "changed-local");
        await File.WriteAllBytesAsync(changed.LocalPortPath, Encoding.UTF8.GetBytes("rewritten local bytes"));
        var changedContent = await rig.LoadAsync(pair.Id, "changed-local.txt");
        Require(changedContent.LocalBytes is null && changedContent.DropboxBytes is null &&
                changedContent.Note is not null && changedContent.LocalSize == reviewedBytes.LongLength &&
                changedContent.LocalHash == changed.LocalPortHash,
            "CC5 a local file rewritten after it was reviewed returned no bytes and kept the reviewed metadata");
        Require(rig.Wire.Uploads == 0 && rig.Wire.Deletes == 0,
            "CC5 no stale-version case mutated either version");
    }

    private static async Task StaleAccountAsync()
    {
        Claim("CC6-stale-account", "A cloud endpoint whose retained account is no longer connected returns no bytes and contacts no provider");
        await using var rig = await Rig.CreateAsync("stale-account");
        const string relative = "stale-account.txt";
        rig.AddLocal("left");
        rig.AddCloud("right", SyncEndpointKind.Dropbox);
        var pair = rig.CreatePair(SyncEndpointKind.Local, SyncEndpointKind.Dropbox, "Stale account pair");
        await rig.RetainAsync(pair);
        var fixture = await rig.RetainConflictAsync(pair, relative, Encoding.UTF8.GetBytes("local side"),
            Encoding.UTF8.GetBytes("cloud side"));
        rig.Wire.ClearAccounts();
        var requestsBefore = rig.Wire.Requests;
        // CFC-R3: the retained provider account is part of the read's authority. An account that is no
        // longer connected is an actionable blocker for this exact endpoint, exactly as the product's other
        // provider operations already report it, and it is never replaced by another account or a default.
        var refusal = await Refused<InvalidOperationException>(() => rig.LoadAsync(pair.Id, relative),
            "CC6 an endpoint whose exact retained account is not connected refuses instead of substituting another account");
        Require(refusal.Message.Contains("Connect the exact verified Dropbox account this endpoint names",
                StringComparison.Ordinal),
            "CC6 the refusal names the exact retained account that must be connected");
        Require(rig.Wire.Requests == requestsBefore,
            "CC6 the stale account was never contacted and no access token was requested");
        Require((await rig.Db.LoadConflictAsync(fixture.ConflictId)).Status == "open" &&
                rig.Wire.Uploads == 0 && rig.Wire.Deletes == 0 &&
                (await rig.Db.LoadObjectVersionAsync(fixture.DropboxPortVersionId)).ContentHash ==
                fixture.DropboxPortHash,
            "CC6 the refused read left the conflict and both retained versions unchanged");
    }

    private static async Task PairRebindAsync()
    {
        Claim("CC7-pair-rebind", "The captured endpoint context exposes a pair rebind and a rebound pair never returns the old account's bytes");
        await using var rig = await Rig.CreateAsync("pair-rebind");
        const string relative = "rebind.txt";
        rig.AddLocal("left");
        rig.AddCloud("right", SyncEndpointKind.Dropbox);
        rig.AddCloud("other", SyncEndpointKind.Dropbox);
        var original = rig.Cloud("right");
        var other = rig.Cloud("other");
        var pair = rig.CreatePair(SyncEndpointKind.Local, SyncEndpointKind.Dropbox, "Rebind pair");
        await rig.RetainAsync(pair);
        var cloudBytes = Encoding.UTF8.GetBytes("rebind original account revision");
        await rig.RetainConflictAsync(pair, relative, Encoding.UTF8.GetBytes("rebind local version"), cloudBytes);
        var content = await rig.LoadAsync(pair.Id, relative);
        var loadedContext = content.EndpointContext ?? throw new InvalidDataException(
            "The rebind pair's preview did not capture an endpoint context.");
        Require(content.DropboxBytes is not null && content.DropboxBytes.SequenceEqual(cloudBytes) &&
                loadedContext.Right.ConnectionId == original.ConnectionId,
            "CC7 the preview before the rebind returned the original account's exact revision and captured its account");

        // The same retained pair is rebound to another account of the same provider, which holds different bytes.
        var otherBytes = Encoding.UTF8.GetBytes("rebound account different bytes");
        rig.Wire.Put(other.Account!, other.FolderPath, relative, otherBytes);
        var rebound = rig.CreatePair(SyncEndpointKind.Local, SyncEndpointKind.Dropbox, pair.Name,
            rightSlot: "other") with { Id = pair.Id };
        await rig.RetainPairDefinitionAsync(rebound);
        var reboundContext = SyncEndpointContext.Capture(rebound);
        Require(loadedContext != reboundContext &&
                reboundContext.Right.ConnectionId == other.ConnectionId &&
                loadedContext.Right.ConnectionId == original.ConnectionId,
            "CC7 the loaded preview's endpoint context differs from the rebound pair's context on exactly the cloud account");
        // CFC-R3: the retained root authority belongs to the original account, and both accounts here are
        // individually valid and connected, so the read must refuse rather than cross the rebind.
        var rebindRefusal = await Refused<InvalidOperationException>(() => rig.LoadAsync(pair.Id, relative),
            "CC7 a rebound pair refuses under both accounts being valid rather than returning either side's bytes");
        Require(rebindRefusal.Message.Contains("does not belong to the exact account", StringComparison.Ordinal) ||
                rebindRefusal.Message.Contains("retained authority root", StringComparison.Ordinal),
            "CC7 the rebind refusal names the retained authority that no longer matches the rebound endpoint");
        Require(other.Account!.DownloadRequests == 0,
            "CC7 the rebound account was not asked for a revision its retained identity does not own");
    }

    private static async Task BoundsAndNoEffectAsync()
    {
        Claim("CC8-bounds-and-no-effect", "The per-side maximum bound is kept, an invalid bound is refused and no preview changes either version");
        await using var rig = await Rig.CreateAsync("bounds-and-no-effect");
        const string relative = "bounds.txt";
        rig.AddLocal("left");
        rig.AddCloud("right", SyncEndpointKind.Dropbox);
        var pair = rig.CreatePair(SyncEndpointKind.Local, SyncEndpointKind.Dropbox, "Bounds pair");
        await rig.RetainAsync(pair);
        var localBytes = Encoding.UTF8.GetBytes("bounded local version");
        var cloudBytes = Encoding.UTF8.GetBytes("bounded cloud version");
        var fixture = await rig.RetainConflictAsync(pair, relative, localBytes, cloudBytes);
        var localBefore = await File.ReadAllBytesAsync(fixture.LocalPortPath);
        var requestsBefore = rig.Wire.Requests;

        var bounded = await rig.LoadAsync(pair.Id, relative, maximumContentBytes: 4);
        Require(bounded.LocalBytes is null && bounded.DropboxBytes is null && bounded.Note is not null &&
                bounded.Note.Contains("bounded to", StringComparison.Ordinal) &&
                bounded.LocalSize == localBytes.LongLength,
            "CC8 a version over the per-side maximum returns metadata with the existing bound note and no bytes");
        Require(rig.Wire.Requests == requestsBefore && rig.Wire.Uploads == 0 && rig.Wire.Deletes == 0,
            "CC8 the bounded preview read no provider revision and issued no provider mutation");
        await Refused<ArgumentOutOfRangeException>(() => rig.LoadAsync(pair.Id, relative, maximumContentBytes: 0),
            "CC8 a zero maximum is refused by the existing bound validation");
        // CFC-R1: the database now returns null for a no-row, NULL or DBNull result before any identifier
        // conversion or load, so the Host reaches its own intended refusal for a path with no open conflict.
        var noConflict = await Refused<InvalidOperationException>(
            () => rig.LoadAsync(pair.Id, "not-conflicted.txt"),
            "CC8 a path with no open conflict reaches the host's intended refusal instead of an empty identifier");
        Require(noConflict.Message.Contains("no longer open", StringComparison.Ordinal),
            "CC8 the no-open-conflict refusal is the host's own documented message");
        var versionBefore = await rig.Db.LoadObjectVersionAsync(fixture.LocalPortVersionId);
        var successful = await rig.LoadAsync(pair.Id, relative);
        var versionAfter = await rig.Db.LoadObjectVersionAsync(fixture.LocalPortVersionId);
        Require(successful.LocalBytes is not null && successful.DropboxBytes is not null,
            "CC8 the same conflict previews successfully inside the bound");
        Require(versionBefore.ContentHash == versionAfter.ContentHash && versionBefore.Size == versionAfter.Size &&
                (await File.ReadAllBytesAsync(fixture.LocalPortPath)).SequenceEqual(localBefore) &&
                localBefore.SequenceEqual(localBytes) && rig.Wire.Uploads == 0 && rig.Wire.Deletes == 0 &&
                rig.Wire.Copies == 0 && rig.Wire.Restores == 0,
            "CC8 the successful preview left the retained version, the local file and the provider unchanged and downloaded nothing locally");
    }

    private static async Task MalformedDocumentRefusalAsync()
    {
        Claim("CC9-malformed-document-refusal", "An incomplete or unsupported endpoint document is refused instead of falling back to legacy fields");
        await using var rig = await Rig.CreateAsync("malformed-document");
        rig.AddLocal("left");
        rig.AddLocal("right");
        var pair = rig.CreatePair(SyncEndpointKind.Local, SyncEndpointKind.Local, "Malformed document pair");
        await rig.RetainAsync(pair);
        var requestsBefore = rig.Wire.Requests;
        var incomplete = pair with { LeftEndpoint = null };
        await Refused<InvalidDataException>(() => Task.Run(() => SyncEndpointContext.Capture(incomplete)),
            "CC9 the load's own endpoint resolution refuses a document missing its left endpoint rather than reading the legacy fields");
        await Refused<InvalidDataException>(() => rig.RetainPairDefinitionAsync(incomplete),
            "CC9 the product refuses to retain a document missing its left endpoint at all");
        var unsupported = pair with { EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints + 1 };
        await Refused<InvalidDataException>(() => rig.RetainPairDefinitionAsync(unsupported),
            "CC9 the product refuses to retain a document of an unsupported schema version");
        await Refused<InvalidOperationException>(() => rig.LoadAsync(pair.Id, "malformed.txt"),
            "CC9 the untouched pair still loads and reaches its own no-open-conflict refusal rather than a guessed document");
        Require(rig.Wire.Requests == requestsBefore, "CC9 no refused document contacted a provider");
    }

    // ---------------------------------------------------------------------------------------------------
    // CFC-R2/R4: the Google Drive read and the retained typed authority, proved through the real Host, the
    // real SQLite authority, the real cloud observer and the real provider adapters over the shared bounded
    // HTTP fixture. No case here writes an observation identity by hand: the observer's own typed identities
    // are what the preview reads, and the hand-written identity below is a deliberate foreign-identity fault.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>One typed cloud/cloud conflict fixture over the shared real-Host harness.</summary>
    private sealed record TypedCloudCase(CloudPairHostBindingChecks.HostRig Rig,
        CloudPairHostBindingChecks.HostPair Pair, CloudPairHostBindingChecks.HostWire.SeededFile LeftFile,
        CloudPairHostBindingChecks.HostWire.SeededFile RightFile, SyncAuthorityObservedEntry Local,
        SyncAuthorityObservedEntry Remote, string ConflictId, string RelativePath) : IDisposable
    {
        public void Dispose() => Rig.Dispose();
    }

    private static async Task<TypedCloudCase> RetainTypedCloudConflictAsync(string caseName,
        CloudPairHostBindingChecks.EndpointSpec left, CloudPairHostBindingChecks.EndpointSpec right,
        string relative, string leftText, string rightText)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(Path.Combine(_reportRoot, "cloud-observer"));
        var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync(caseName);
        try
        {
            await rig.AddEndpointAsync(left);
            await rig.AddEndpointAsync(right);
            var leftFile = rig.SeedFile(left, relative, leftText);
            var rightFile = rig.SeedFile(right, relative, rightText);
            var pair = await rig.AddPairAsync(caseName, left, right);
            // Fixture setup only: each side's retained cloud root is registered through the same public
            // registration and the same typed identity document the Host's own candidate registration
            // writes, and the typed two-port set is then built over this rig's real adapters. The conflict
            // preview itself allocates no operation, no stage, no root and no effect.
            await rig.Db.RegisterRootBindingAsync(pair.LeftRoot, pair.Id, SyncAuthorityCloudSides.LogicalLeft, 1,
                SyncAuthorityCloudRootPersistence.Serialize(left.Provider, left.ProviderAccountId,
                    left.EndpointNamespaceId, left.FolderId), left.FolderPath);
            await rig.Db.RegisterRootBindingAsync(pair.RightRoot, pair.Id, SyncAuthorityCloudSides.LogicalRight, 1,
                SyncAuthorityCloudRootPersistence.Serialize(right.Provider, right.ProviderAccountId,
                    right.EndpointNamespaceId, right.FolderId), right.FolderPath);
            var ports = SyncAuthorityCloudEndpointSet.FromPair(pair.Pair, StoreFor(rig, left), StoreFor(rig, right),
                pair.LeftRoot, pair.RightRoot, left.ProviderAccountId, right.ProviderAccountId);
            await new SyncAuthorityCloudObservation(pair.Pair, ports, rig.Db).ObserveAsync("initial");
            var path = PilotPath.NormalizeRelative(relative);
            var local = (await rig.Db.LoadLatestCompleteObservationAsync(pair.Id,
                    SyncAuthorityCloudSides.LogicalLeft))
                .Single(entry => entry.RelativePath == path);
            var remote = (await rig.Db.LoadLatestCompleteObservationAsync(pair.Id,
                    SyncAuthorityCloudSides.LogicalRight))
                .Single(entry => entry.RelativePath == path);
            var conflictId = "conflict-" + caseName;
            await rig.Db.RecordConflictAsync(conflictId, pair.Id, local.ObjectId,
                "Typed cloud observer conflict", local.ObjectVersionId, local.MembershipId,
                remote.ObjectVersionId, remote.MembershipId);
            return new TypedCloudCase(rig, pair, leftFile, rightFile, local, remote, conflictId, path);
        }
        catch
        {
            rig.Dispose();
            throw;
        }
    }

    /// <summary>The rig's own real adapter for one seeded endpoint, over the rig's bounded transport.</summary>
    private static ISyncRemoteStore StoreFor(CloudPairHostBindingChecks.HostRig rig,
        CloudPairHostBindingChecks.EndpointSpec spec) => spec.Kind == SyncEndpointKind.Dropbox
        ? rig.DropboxClient(spec)
        : rig.DriveClient(spec);

    private static async Task DriveToDriveTypedAsync()
    {
        Claim("CC10-drive-to-drive-typed",
            "The current reviewed Google Drive binary is read for both sides of a two-Drive pair through the real cloud observer's typed observations");
        var left = CloudPairHostBindingChecks.HostDrive("cc10-left", "/DriveLeft");
        var right = CloudPairHostBindingChecks.HostDrive("cc10-right", "/DriveRight");
        using var fixture = await RetainTypedCloudConflictAsync("cc10-drive-to-drive", left, right, "drive.txt",
            "drive left reviewed bytes", "drive right reviewed bytes");
        var leftVersion = await fixture.Rig.Db.LoadObjectVersionAsync(fixture.Local.ObjectVersionId);
        var rightVersion = await fixture.Rig.Db.LoadObjectVersionAsync(fixture.Remote.ObjectVersionId);
        Require(leftVersion.IdentityJson.Contains("\"identityKind\":\"cloud\"", StringComparison.Ordinal) &&
                rightVersion.IdentityJson.Contains("\"identityKind\":\"cloud\"", StringComparison.Ordinal),
            "CC10 the observer retained an explicit typed cloud identity for each direction, not a hand-written legacy shape");
        Require(SyncAuthorityCloudObservationIdentity.TryParse(leftVersion.IdentityJson, out var typedLeft) &&
                typedLeft!.ProviderAccountId == left.ProviderAccountId &&
                typedLeft.NamespaceId == left.EndpointNamespaceId &&
                typedLeft.Provider == SyncProviders.GoogleDrive && !string.IsNullOrWhiteSpace(typedLeft.FileId) &&
                !string.IsNullOrWhiteSpace(typedLeft.Revision) && typedLeft.ContentAlgorithm == SyncContentIdentity.Sha256,
            "CC10 the retained typed identity binds the left side's own provider account, native namespace, file id, revision and algorithm");
        var requestsBefore = fixture.Rig.Wire.TotalRequests;
        var content = await fixture.Rig.Host.LoadConflictContentAsync(fixture.Pair.Id, fixture.RelativePath);
        var leftExpected = Encoding.UTF8.GetBytes("drive left reviewed bytes");
        var rightExpected = Encoding.UTF8.GetBytes("drive right reviewed bytes");
        Require(content.Note is null && content.LocalBytes is not null && content.LocalBytes.SequenceEqual(leftExpected),
            "CC10 the left Drive port returned the exact reviewed binary through the real adapter (note: " +
            (content.Note ?? "none") + ")");
        Require(content.DropboxBytes is not null && content.DropboxBytes.SequenceEqual(rightExpected),
            "CC10 the right Drive port returned the exact reviewed binary through the real adapter");
        Require(content.EndpointContext!.Left.ConnectionId == left.ConnectionId &&
                content.EndpointContext.Right.ConnectionId == right.ConnectionId,
            "CC10 the captured context names each side's own Drive account");
        Require(fixture.Rig.Wire.TotalRequests > requestsBefore,
            "CC10 the read was served by the real Drive adapter over the bounded transport");
        Require((await fixture.Rig.Db.LoadConflictAsync(fixture.ConflictId)).Status == "open",
            "CC10 the Drive preview left the retained conflict open and mutated nothing");
    }

    private static async Task DriveOnEitherSideAsync()
    {
        Claim("CC11-drive-on-either-side",
            "A Dropbox and a Drive endpoint of one pair each read their own retained version, in both ordered directions");
        foreach (var driveFirst in new[] { false, true })
        {
            var tag = driveFirst ? "cc11-drive-left" : "cc11-drive-right";
            var dropbox = CloudPairHostBindingChecks.HostDropbox(tag, driveFirst ? "/DbRight" : "/DbLeft");
            var drive = CloudPairHostBindingChecks.HostDrive(tag, driveFirst ? "/DriveLeft" : "/DriveRight");
            var left = driveFirst ? drive : dropbox;
            var right = driveFirst ? dropbox : drive;
            var leftText = driveFirst ? "cc11 drive left bytes" : "cc11 dropbox left bytes";
            var rightText = driveFirst ? "cc11 dropbox right bytes" : "cc11 drive right bytes";
            using var fixture = await RetainTypedCloudConflictAsync(tag, left, right, "mixed.txt", leftText, rightText);
            var content = await fixture.Rig.Host.LoadConflictContentAsync(fixture.Pair.Id, fixture.RelativePath);
            Require(content.Note is null && content.LocalBytes is not null &&
                    content.LocalBytes.SequenceEqual(Encoding.UTF8.GetBytes(leftText)),
                "CC11 " + tag + ": the left port returned its own retained version (note: " +
                (content.Note ?? "none") + ")");
            Require(content.DropboxBytes is not null &&
                    content.DropboxBytes.SequenceEqual(Encoding.UTF8.GetBytes(rightText)),
                "CC11 " + tag + ": the right port returned its own retained version through its own adapter");
        }
    }

    private static async Task DriveUnavailableVersionsAsync()
    {
        Claim("CC12-drive-unavailable-versions",
            "A superseded, deleted or native-document Drive version is safely unavailable and never substituted with the latest bytes");
        var left = CloudPairHostBindingChecks.HostDrive("cc12-left", "/DriveLeft");
        var right = CloudPairHostBindingChecks.HostDrive("cc12-right", "/DriveRight");
        using (var superseded = await RetainTypedCloudConflictAsync("cc12-superseded", left, right, "stale.txt",
                   "cc12 reviewed bytes", "cc12 peer bytes"))
        {
            const string newerText = "cc12 newer Drive bytes that must never be substituted";
            var reseeded = superseded.Rig.SeedFile(right, "stale.txt", newerText);
            Require(reseeded.Revision != superseded.RightFile.Revision,
                "CC12 re-seeding the Drive object produced a genuinely different provider version");
            var content = await superseded.Rig.Host.LoadConflictContentAsync(superseded.Pair.Id,
                superseded.RelativePath);
            Require(content.LocalBytes is null && content.DropboxBytes is null && content.Note is not null,
                "CC12 a superseded Drive version left both versions unread with an explicit note (note: " +
                (content.Note ?? "none") + ")");
            Require(content.Note!.Contains("Google Drive", StringComparison.Ordinal),
                "CC12 the note names the Google Drive endpoint whose reviewed version is unavailable");
            Require(content.Note.Contains("unavailable without changing either version", StringComparison.Ordinal) ||
                    content.Note.Contains("Google Drive", StringComparison.Ordinal),
                "CC12 the superseded Drive version is reported rather than silently replaced");
        }
        foreach (var mode in new[] { "deleted", "native-document" })
        {
            using var fixture = await RetainTypedCloudConflictAsync("cc12-" + mode, left, right, mode + ".txt",
                "cc12 " + mode + " local bytes", "cc12 " + mode + " peer bytes");
            if (mode == "deleted") fixture.Rig.Wire.SetDriveMissing(fixture.RightFile.ObjectId, true);
            else fixture.Rig.Wire.MakeDriveNodeNativeDocument(fixture.RightFile.ObjectId);
            var content = await fixture.Rig.Host.LoadConflictContentAsync(fixture.Pair.Id, fixture.RelativePath);
            Require(content.LocalBytes is null && content.DropboxBytes is null && content.Note is not null &&
                    content.Note.Contains("Google Drive", StringComparison.Ordinal),
                "CC12 a " + mode + " Drive version is unavailable with an endpoint-derived note (note: " +
                (content.Note ?? "none") + ")");
            Require(content.LocalSize is not null && content.DropboxSize is not null,
                "CC12 the " + mode + " case still returned the retained metadata rather than nothing");
        }
    }

    private static async Task DriveBoundsAndDuringReadChangeAsync()
    {
        Claim("CC13-drive-bounds-and-during-read-change",
            "An over-bound Drive version is bounded with metadata only, and a Drive version that changes during the read refuses");
        var left = CloudPairHostBindingChecks.HostDrive("cc13-left", "/DriveLeft");
        var right = CloudPairHostBindingChecks.HostDrive("cc13-right", "/DriveRight");
        using var fixture = await RetainTypedCloudConflictAsync("cc13-bounds", left, right, "bounds.txt",
            "cc13 bounded local bytes", "cc13 bounded peer bytes");
        var requestsBefore = fixture.Rig.Wire.TotalRequests;
        var bounded = await fixture.Rig.Host.LoadConflictContentAsync(fixture.Pair.Id, fixture.RelativePath,
            maximumContentBytes: 4);
        Require(bounded.LocalBytes is null && bounded.DropboxBytes is null && bounded.Note is not null &&
                bounded.Note.Contains("bounded to", StringComparison.Ordinal) && bounded.LocalSize is not null,
            "CC13 a Drive version over the per-side maximum returns metadata with the bound note and no bytes");
        Require(fixture.Rig.Wire.TotalRequests == requestsBefore,
            "CC13 the bounded preview requested no Drive media at all");
        fixture.Rig.Wire.DriftDriveVersion(fixture.RightFile.ObjectId, 1);
        var drifted = await fixture.Rig.Host.LoadConflictContentAsync(fixture.Pair.Id, fixture.RelativePath);
        Require(drifted.LocalBytes is null && drifted.DropboxBytes is null && drifted.Note is not null,
            "CC13 a Drive version that changed during the read refused to present content (note: " +
            (drifted.Note ?? "none") + ")");
        Require((await fixture.Rig.Db.LoadConflictAsync(fixture.ConflictId)).Status == "open",
            "CC13 neither the bounded nor the drifted Drive read changed the retained conflict");
    }

    private static async Task CloudAuthorityCrossingAsync()
    {
        Claim("CC14-cloud-authority-crossing",
            "A foreign typed identity, a foreign native id and a superseded Dropbox revision cannot cross the retained authority");
        var left = CloudPairHostBindingChecks.HostDrive("cc14-left", "/DriveLeft");
        var right = CloudPairHostBindingChecks.HostDrive("cc14-right", "/DriveRight");
        using var fixture = await RetainTypedCloudConflictAsync("cc14-authority", left, right, "authority.txt",
            "cc14 local bytes", "cc14 peer bytes");
        var localBytes = Encoding.UTF8.GetBytes("cc14 local bytes");
        var foreignIdentity = new SyncAuthorityCloudObservationIdentity(SyncProviders.GoogleDrive,
            "perm-foreign-account", "perm-foreign-account", "drive-file-foreign", "version-foreign",
            SyncContentIdentity.Sha256).Serialize();
        await fixture.Rig.Db.RegisterObjectVersionAsync("version-cc14-foreign", fixture.Local.ObjectId,
            SyncAuthorityCloudSides.LogicalLeft, foreignIdentity,
            Convert.ToHexString(SHA256.HashData(localBytes)).ToLowerInvariant(), localBytes.LongLength);
        await fixture.Rig.Db.RegisterMembershipAsync("membership-cc14-foreign", fixture.Local.ObjectId,
            fixture.Local.RootBindingId, fixture.RelativePath, 2);
        await fixture.Rig.Db.RecordConflictAsync("conflict-cc14-foreign", fixture.Pair.Id, fixture.Local.ObjectId,
            "Foreign typed identity conflict", "version-cc14-foreign", "membership-cc14-foreign",
            fixture.Remote.ObjectVersionId, fixture.Remote.MembershipId);
        var requestsBefore = fixture.Rig.Wire.TotalRequests;
        await Refused<InvalidDataException>(
            () => fixture.Rig.Host.LoadConflictContentAsync(fixture.Pair.Id, fixture.RelativePath,
                conflictId: "conflict-cc14-foreign", conflictGeneration: 2),
            "CC14 an explicitly typed foreign identity is refused rather than reinterpreted or read");
        Require(fixture.Rig.Wire.TotalRequests == requestsBefore,
            "CC14 the foreign typed identity was refused before any provider request");
        fixture.Rig.Wire.FaultDriveRecoveryMember(fixture.RightFile.ObjectId,
            CloudPairHostBindingChecks.HostWire.RecoveryMemberFault.NativeId);
        // The foreign conflict is the newest open conflict for this path, so the colliding-native-id read
        // binds the ORIGINAL retained conflict explicitly and still cannot present its bytes.
        var collided = await fixture.Rig.Host.LoadConflictContentAsync(fixture.Pair.Id, fixture.RelativePath,
            conflictId: fixture.ConflictId, conflictGeneration: 1);
        Require(collided.LocalBytes is null && collided.DropboxBytes is null && collided.Note is not null,
            "CC14 a foreign native id on the reviewed Drive object refused to present content (note: " +
            (collided.Note ?? "none") + ")");
        fixture.Rig.Wire.ClearDriveRecoveryMemberFault(fixture.RightFile.ObjectId);
        var restored = await fixture.Rig.Host.LoadConflictContentAsync(fixture.Pair.Id, fixture.RelativePath,
            conflictId: fixture.ConflictId, conflictGeneration: 1);
        Require(restored.DropboxBytes is not null &&
                restored.DropboxBytes.SequenceEqual(Encoding.UTF8.GetBytes("cc14 peer bytes")),
            "CC14 clearing the foreign native id restored the exact reviewed Drive read");

        // Dropbox keeps its read-only historical capability: after the file itself has moved on, the retained
        // predecessor revision is still served exactly, and the newer revision is never substituted for it.
        var dropbox = CloudPairHostBindingChecks.HostDropbox("cc14-db", "/DbLeft");
        using var history = await RetainTypedCloudConflictAsync("cc14-dropbox-history", dropbox, right,
            "history.txt", "cc14 retained dropbox predecessor", "cc14 drive peer bytes");
        var newer = history.Rig.SeedFile(dropbox, "history.txt", "cc14 newer dropbox revision");
        Require(newer.Revision != history.LeftFile.Revision,
            "CC14 re-seeding the Dropbox object produced a genuinely different provider revision");
        var historical = await history.Rig.Host.LoadConflictContentAsync(history.Pair.Id, history.RelativePath);
        Require(historical.Note is null && historical.LocalBytes is not null &&
                historical.LocalBytes.SequenceEqual(Encoding.UTF8.GetBytes("cc14 retained dropbox predecessor")) &&
                !historical.LocalBytes.SequenceEqual(Encoding.UTF8.GetBytes("cc14 newer dropbox revision")),
            "CC14 the Dropbox side kept its read-only historical revision capability and returned the retained predecessor");
    }

    private static async Task LegacyExplicitIdentityRefusalAsync()
    {
        Claim("CC15-legacy-explicit-identity",
            "Malformed explicit observation and root kinds never fall back to genuine legacy authority");
        JsonNode?[] kinds = [null, JsonValue.Create(7), new JsonObject(),
            JsonValue.Create("foreign"), JsonValue.Create("cloud")];
        await using (var rig = await Rig.CreateAsync("legacy-explicit-version"))
        {
            rig.AddLocal("local");
            rig.AddCloud("cloud", SyncEndpointKind.Dropbox);
            var pair = rig.CreateLegacyPair("local", "cloud", "Legacy explicit identity refusal");
            await rig.RetainAsync(pair);
            var fixture = await rig.RetainConflictAsync(pair, "identity.txt",
                Encoding.UTF8.GetBytes("local version"), Encoding.UTF8.GetBytes("cloud version"));
            var valid = await rig.LoadAsync(pair.Id, "identity.txt");
            Require(valid.LocalBytes is not null && valid.DropboxBytes is not null,
                "CC15 genuine untyped legacy versions still preview");
            var version = await rig.Db.LoadObjectVersionAsync(fixture.DropboxPortVersionId);
            for (var index = 0; index < kinds.Length; index++)
            {
                var identity = JsonNode.Parse(version.IdentityJson)!.AsObject();
                identity["identityKind"] = kinds[index]?.DeepClone();
                var versionId = "version-cc15-" + index;
                var conflictId = "conflict-cc15-" + index;
                await rig.Db.RegisterObjectVersionAsync(versionId, version.ObjectId,
                    SyncAuthorityCloudSides.LogicalRight, identity.ToJsonString(), version.ContentHash, version.Size);
                await rig.Db.RecordConflictAsync(conflictId, pair.Id, version.ObjectId, "Malformed explicit kind",
                    fixture.LocalPortVersionId, fixture.LocalPortMembershipId, versionId,
                    fixture.DropboxPortMembershipId);
                var retained = await rig.Db.LoadConflictAsync(conflictId);
                var requests = rig.Wire.Requests;
                await Refused<InvalidDataException>(() => rig.LoadAsync(pair.Id, "identity.txt",
                        conflictId: conflictId, conflictGeneration: retained.ConflictGeneration),
                    "CC15 explicit observation kind variant " + index + " cannot become legacy");
                Require(rig.Wire.Requests == requests,
                    "CC15 malformed observation rejected before provider contact");
            }
        }
        for (var index = 0; index < kinds.Length; index++)
        {
            await using var rig = await Rig.CreateAsync("legacy-explicit-root-" + index);
            rig.AddLocal("local");
            rig.AddCloud("cloud", SyncEndpointKind.Dropbox);
            var pair = rig.CreateLegacyPair("local", "cloud", "Malformed retained root");
            await rig.RetainAsync(pair, original =>
            {
                var identity = JsonNode.Parse(original)!.AsObject();
                identity["identityKind"] = kinds[index]?.DeepClone();
                return identity.ToJsonString();
            });
            await rig.RetainConflictAsync(pair, "root.txt", Encoding.UTF8.GetBytes("local root bytes"),
                Encoding.UTF8.GetBytes("cloud root bytes"));
            var requests = rig.Wire.Requests;
            await Refused<InvalidDataException>(() => rig.LoadAsync(pair.Id, "root.txt"),
                "CC15 explicit root kind variant " + index + " cannot become legacy");
            Require(rig.Wire.Requests == requests, "CC15 malformed root rejected before provider contact");
        }
    }

    private static async Task MembershipDriftDuringReadAsync()
    {
        Claim("CC16-membership-drift",
            "Changing either retained membership's generation or path during the read prevents returning bytes");
        foreach (var side in new[] { "local", "dropbox" })
        foreach (var changeGeneration in new[] { true, false })
        {
            await using var rig = await Rig.CreateAsync("membership-drift-" + side + "-" + changeGeneration);
            rig.AddLocal("local");
            rig.AddCloud("cloud", SyncEndpointKind.Dropbox);
            var pair = rig.CreateLegacyPair("local", "cloud", "Membership drift");
            await rig.RetainAsync(pair);
            var fixture = await rig.RetainConflictAsync(pair, "drift.txt",
                Encoding.UTF8.GetBytes("local drift bytes"), Encoding.UTF8.GetBytes("cloud drift bytes"));
            var retained = await rig.Db.LoadConflictAsync(fixture.ConflictId);
            var membership = side == "local" ? fixture.LocalPortMembershipId : fixture.DropboxPortMembershipId;
            rig.Wire.AfterDownload = async () =>
            {
                // Change only fixture input through the real SQLite constraints. The actual Host read and
                // its post-read decision are never replaced; no trigger or production guard is disabled.
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = rig.Db.DatabasePath, Mode = SqliteOpenMode.ReadWrite,
                    ForeignKeys = true, Pooling = false
                }.ToString());
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = changeGeneration
                    ? "UPDATE membership SET generation=generation+1 WHERE membership_id=$id;"
                    : "UPDATE membership SET relative_path_key=$path WHERE membership_id=$id;";
                command.Parameters.AddWithValue("$id", membership);
                if (!changeGeneration) command.Parameters.AddWithValue("$path", "moved-drift.txt");
                Require(await command.ExecuteNonQueryAsync() == 1,
                    "CC16 exactly the selected fixture membership changed during the byte read");
            };
            var refusal = await Refused<InvalidOperationException>(() => rig.LoadAsync(pair.Id, "drift.txt",
                    conflictId: fixture.ConflictId, conflictGeneration: retained.ConflictGeneration),
                "CC16 changed " + side + " membership cannot return reviewed bytes");
            Require(refusal.Message.Contains("retained conflict changed", StringComparison.OrdinalIgnoreCase),
                "CC16 the actual post-read retained-authority check refused the changed membership");
            Require(rig.Wire.Uploads == 0 && rig.Wire.Deletes == 0,
                "CC16 preview issued no provider mutation");
        }
    }

    /// <summary>The bounded provider wire: real HTTP requests, one store per retained account, no Google transport.</summary>
    private sealed class Wire : HttpMessageHandler
    {
        internal sealed class Account
        {
            internal string AccountId = "";
            internal string RootNamespaceId = "";
            internal string HomeNamespaceId = "";
            internal string AccessToken = "";
            internal string RefreshToken = "";
            internal readonly Dictionary<string, string> Folders = new(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, WireFile> Files = new(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, WireFile> History = new(StringComparer.Ordinal);
            internal int TokenRequests;
            internal int DownloadRequests;
        }

        internal sealed record WireFile(string Folder, string RelativePath, string Id, string Revision, byte[] Bytes)
        {
            internal string Hash => DropboxContentHash.Compute(Bytes);
        }

        private readonly List<Account> _accounts = [];
        private readonly Dictionary<string, Account> _byAccess = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Account> _byRefresh = new(StringComparer.Ordinal);
        private int _sequence;

        /// <summary>Every routed request with the status it was answered with, so a failure explains itself.</summary>
        internal readonly List<string> Routed = [];

        internal int Requests { get; private set; }
        internal int GoogleCalls { get; private set; }
        internal int Uploads { get; private set; }
        internal int Deletes { get; private set; }
        internal int Copies { get; private set; }
        internal int Restores { get; private set; }

        internal Account AddAccount(string slot)
        {
            var account = new Account
            {
                AccountId = "account-" + slot,
                RootNamespaceId = "root-namespace-" + slot,
                HomeNamespaceId = "home-namespace-" + slot,
                AccessToken = "access-token-" + slot,
                RefreshToken = "refresh-token-" + slot
            };
            _accounts.Add(account);
            _byAccess[account.AccessToken] = account;
            _byRefresh[account.RefreshToken] = account;
            return account;
        }

        internal void ClearAccounts()
        {
            _accounts.Clear();
            _byAccess.Clear();
            _byRefresh.Clear();
        }

        internal IReadOnlyList<DropboxConnectionBinding> Bindings()
        {
            var result = new List<DropboxConnectionBinding>();
            for (var index = 0; index < _accounts.Count; index++)
            {
                var account = _accounts[index];
                var connectionId = DropboxConnectionBinding.CreateConnectionId(account.AccountId);
                result.Add(new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                    account.AccountId, account.RootNamespaceId, account.HomeNamespaceId,
                    DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
                    ConnectionId: connectionId,
                    CredentialReference: index == 0
                        ? WindowsDropboxRefreshTokenStore.RegisteredTarget
                        : WindowsDropboxRefreshTokenStore.AccountTarget(connectionId)).Normalize());
            }
            return result;
        }

        internal string? RefreshForConnection(string connectionId) => _accounts.FirstOrDefault(account =>
            string.Equals(DropboxConnectionBinding.CreateConnectionId(account.AccountId), connectionId,
                StringComparison.Ordinal))?.RefreshToken;

        internal WireFile Put(Account account, string folderPath, string relativePath, byte[] bytes)
        {
            var existing = FileAt(account, folderPath, relativePath);
            var file = new WireFile(folderPath, PilotPath.NormalizeRelative(relativePath),
                existing?.Id ?? "id:" + account.AccountId + "-" + (++_sequence),
                "revision-" + account.AccountId + "-" + (++_sequence), bytes.ToArray());
            account.Files[FullPath(folderPath, relativePath)] = file;
            account.History[file.Revision] = file;
            return file;
        }

        internal void ForgetRevision(Account account, string revision) => account.History.Remove(revision);

        private static string FullPath(string folderPath, string relativePath) =>
            folderPath.TrimEnd('/') + "/" + PilotPath.NormalizeRelative(relativePath);

        private static WireFile? FileAt(Account account, string folderPath, string relativePath) =>
            account.Files.GetValueOrDefault(FullPath(folderPath, relativePath));

        private static JsonObject FileMetadata(WireFile file) => new()
        {
            [".tag"] = "file",
            ["name"] = Path.GetFileName(file.RelativePath),
            ["path_display"] = FullPath(file.Folder, file.RelativePath),
            ["path_lower"] = FullPath(file.Folder, file.RelativePath).ToLowerInvariant(),
            ["id"] = file.Id,
            ["rev"] = file.Revision,
            ["size"] = file.Bytes.Length,
            ["content_hash"] = file.Hash,
            // No modified time: newest wins cannot decide, so two-sided edits stay manual conflicts here.
            ["is_downloadable"] = true
        };

        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
            };

        private static HttpResponseMessage Missing() => Json(
            new { error_summary = "path/not_found/", error = new { path = new { } } }, HttpStatusCode.Conflict);

        private static string? Field(string form, string name)
        {
            foreach (var pair in form.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var split = pair.Split('=', 2);
                if (split.Length == 2 && string.Equals(Uri.UnescapeDataString(split[0]), name,
                        StringComparison.Ordinal))
                    return Uri.UnescapeDataString(split[1]);
            }
            return null;
        }

        private static string? FolderPathFor(Account account, string pathOrId)
        {
            foreach (var folder in account.Folders)
                if (string.Equals(folder.Key, pathOrId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(folder.Value, pathOrId, StringComparison.Ordinal))
                    return folder.Key;
            return null;
        }

        private static HttpResponseMessage MetadataFor(Account account, string pathOrId)
        {
            if (FolderPathFor(account, pathOrId) is { } folderPath)
                return Json(new Dictionary<string, object>
                {
                    [".tag"] = "folder",
                    ["name"] = Path.GetFileName(folderPath),
                    ["id"] = account.Folders[folderPath],
                    ["path_display"] = folderPath,
                    ["path_lower"] = folderPath.ToLowerInvariant()
                });
            foreach (var file in account.Files.Values)
                if (string.Equals(file.Id, pathOrId, StringComparison.Ordinal) ||
                    string.Equals(FullPath(file.Folder, file.RelativePath), pathOrId,
                        StringComparison.OrdinalIgnoreCase))
                    return Json(FileMetadata(file));
            return Missing();
        }

        internal string RoutedTail(int count) => string.Join(" | ", Routed.TakeLast(count));
        internal Func<Task>? AfterDownload { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await HandleAsync(request, cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith("/2/files/download", StringComparison.Ordinal) &&
                AfterDownload is { } afterDownload)
            {
                AfterDownload = null;
                await afterDownload();
            }
            Routed.Add(request.RequestUri!.AbsolutePath + " => " + (int)response.StatusCode);
            return response;
        }

        private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            var uri = request.RequestUri!;
            if (uri.Host.Contains("google", StringComparison.OrdinalIgnoreCase))
            {
                GoogleCalls++;
                return Json(new { error = "this fixture serves no Google Drive transport" },
                    HttpStatusCode.NotImplemented);
            }
            if (uri.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal))
            {
                var form = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                var refresh = Field(form, "refresh_token");
                if (refresh is null || !_byRefresh.TryGetValue(refresh, out var tokenAccount))
                    return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
                tokenAccount.TokenRequests++;
                return Json(new
                {
                    access_token = tokenAccount.AccessToken, token_type = "bearer", expires_in = 14400
                });
            }
            var access = request.Headers.Authorization?.Parameter;
            if (access is null || !_byAccess.TryGetValue(access, out var account))
                return Json(new { error_summary = "invalid_access_token/value" }, HttpStatusCode.Unauthorized);
            if (uri.AbsolutePath.EndsWith("/2/users/get_current_account", StringComparison.Ordinal))
                return Json(new
                {
                    account_id = account.AccountId,
                    root_info = new Dictionary<string, object>
                    {
                        [".tag"] = "user",
                        ["root_namespace_id"] = account.RootNamespaceId,
                        ["home_namespace_id"] = account.HomeNamespaceId
                    }
                });
            var argument = request.Headers.TryGetValues("Dropbox-API-Arg", out var values)
                ? values.Single()
                : request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(argument);
            var input = document.RootElement;
            if (uri.AbsolutePath.EndsWith("/2/files/get_metadata", StringComparison.Ordinal))
                return MetadataFor(account, input.GetProperty("path").GetString()!);
            if (uri.AbsolutePath.EndsWith("/2/files/list_folder", StringComparison.Ordinal))
            {
                var folderPath = FolderPathFor(account, input.TryGetProperty("path", out var root)
                    ? root.GetString()! : "/");
                if (folderPath is null) return Missing();
                var entries = new List<object>();
                foreach (var folder in account.Folders)
                    if (folder.Key.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase))
                        entries.Add(new Dictionary<string, object>
                        {
                            [".tag"] = "folder",
                            ["name"] = Path.GetFileName(folder.Key),
                            ["id"] = folder.Value,
                            ["path_display"] = folder.Key,
                            ["path_lower"] = folder.Key.ToLowerInvariant()
                        });
                foreach (var file in account.Files.Values)
                    if (string.Equals(file.Folder, folderPath, StringComparison.OrdinalIgnoreCase))
                        entries.Add(FileMetadata(file));
                return Json(new { entries, has_more = false, cursor = "fixture-cursor" });
            }
            if (uri.AbsolutePath.EndsWith("/2/files/download", StringComparison.Ordinal))
            {
                var requested = input.GetProperty("path").GetString()!;
                if (!requested.StartsWith("rev:", StringComparison.Ordinal)) return Missing();
                if (!account.History.TryGetValue(requested["rev:".Length..], out var file)) return Missing();
                account.DownloadRequests++;
                var metadata = FileMetadata(file);
                metadata.Remove(".tag");
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(file.Bytes)
                };
                response.Headers.Add("Dropbox-API-Result", metadata.ToJsonString());
                return response;
            }
            if (uri.AbsolutePath.EndsWith("/2/files/upload", StringComparison.Ordinal))
            {
                Uploads++;
                return Json(new { error = "no upload" }, HttpStatusCode.NotImplemented);
            }
            if (uri.AbsolutePath.EndsWith("/2/files/delete_v2", StringComparison.Ordinal))
            {
                Deletes++;
                return Json(new { error = "no delete" }, HttpStatusCode.NotImplemented);
            }
            if (uri.AbsolutePath.EndsWith("/2/files/copy_v2", StringComparison.Ordinal))
            {
                Copies++;
                return Json(new { error = "no copy" }, HttpStatusCode.NotImplemented);
            }
            if (uri.AbsolutePath.EndsWith("/2/files/restore", StringComparison.Ordinal))
            {
                Restores++;
                return Json(new { error = "no restore" }, HttpStatusCode.NotImplemented);
            }
            return Json(new { error_summary = "unrouted_fixture_route/value" }, HttpStatusCode.NotImplemented);
        }
    }

    private sealed class FixtureRefreshTokens(Wire wire, string? connectionId = null) : IDropboxRefreshTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(connectionId is null ? null : wire.RefreshForConnection(connectionId));
        }

        public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The conflict-content fixture never writes a credential.");

        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The conflict-content fixture never deletes a credential.");

        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding binding) =>
            new FixtureRefreshTokens(wire, binding.ConnectionId);
    }

    private sealed class FixedAccessTokens(string accessToken) : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(accessToken);
        }
    }

    private sealed record LocalSpec(string Slot, string Path, uint VolumeSerial, ulong FileId);

    private sealed record CloudSpec(string Slot, SyncEndpointKind Kind, string ConnectionId, string AccountId,
        string RootNamespaceId, string HomeNamespaceId, string FolderId, string FolderPath, Wire.Account? Account);

    private sealed record ConflictFixture(string ConflictId, string LocalPortVersionId, string DropboxPortVersionId,
        string LocalPortMembershipId, string DropboxPortMembershipId, string LocalPortHash, string DropboxPortHash,
        string LocalPortPath, string DropboxPortPath, byte[] LocalPortBytes, byte[] DropboxPortBytes,
        bool LeftIsLocalPort, string DropboxPortRevision, string ObjectId);

    private sealed record VersionRegistration(string VersionId, string Hash, string Revision);

    private sealed class Rig : IAsyncDisposable
    {
        private readonly Dictionary<string, LocalSpec> _locals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CloudSpec> _clouds = new(StringComparer.Ordinal);
        private int _sequence;

        internal string Root { get; private set; } = "";
        internal SyncAuthorityDatabase Db { get; private set; } = null!;
        internal Wire Wire { get; private set; } = new();
        internal HttpClient Http { get; private set; } = null!;

        internal SyncAuthorityPlannerOptions Options => new(1, ProfileId, "namespace-conflict-content",
            "namespace-conflict-content", EnableDeletionEffects: true, DeletionMinimumInterval: TimeSpan.Zero);

        internal static async Task<Rig> CreateAsync(string name)
        {
            var rig = new Rig();
            rig.Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TestData", "Projects", "sync-shark", "cloud-pro-tests", name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rig.Root);
            rig.Http = new HttpClient(rig.Wire);
            rig.Db = new SyncAuthorityDatabase(Path.Combine(rig.Root, "db", "authority.db"));
            await rig.Db.InitializeAsync(InstallationId, ProfileId, "conflict-content", "{}");
            return rig;
        }

        internal LocalSpec Local(string slot) => _locals[slot];

        internal CloudSpec Cloud(string slot) => _clouds[slot];

        internal void AddLocal(string slot)
        {
            var path = Directory.CreateDirectory(Path.Combine(Root, slot + "-local")).FullName;
            using var lease = PilotDirectoryLease.Open(path);
            _locals[slot] = new LocalSpec(slot, lease.Path, lease.Identity.VolumeSerialNumber, lease.Identity.FileId);
        }

        internal void AddCloud(string slot, SyncEndpointKind kind)
        {
            if (kind == SyncEndpointKind.GoogleDrive)
            {
                // A Google Drive endpoint with no Google account anywhere on this installation: the read must
                // therefore never reach for that account in order to decide that it cannot supply bytes.
                _clouds[slot] = new CloudSpec(slot, kind, "connection-drive-" + slot, "drive-account-" + slot,
                    "drive-namespace-" + slot, "drive-namespace-" + slot, "drive-folder-" + slot,
                    "/DriveRoot-" + slot, null);
                return;
            }
            var account = Wire.AddAccount(slot);
            var folderPath = "/Pilot-" + slot;
            var folderId = "id:folder-" + slot;
            account.Folders[folderPath] = folderId;
            _clouds[slot] = new CloudSpec(slot, kind,
                DropboxConnectionBinding.CreateConnectionId(account.AccountId), account.AccountId,
                account.RootNamespaceId, account.HomeNamespaceId, folderId, folderPath, account);
        }

        internal SyncEndpoint Endpoint(SyncEndpointKind kind, string slot) => kind switch
        {
            SyncEndpointKind.Local => SyncEndpoint.Local(_locals[slot].Path,
                new SyncLocalEndpointIdentity(_locals[slot].VolumeSerial, _locals[slot].FileId)),
            SyncEndpointKind.Dropbox => SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, _clouds[slot].ConnectionId,
                _clouds[slot].RootNamespaceId, _clouds[slot].FolderId, _clouds[slot].FolderPath),
            _ => SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, _clouds[slot].ConnectionId,
                _clouds[slot].RootNamespaceId, _clouds[slot].FolderId, _clouds[slot].FolderPath)
        };

        internal SyncPair CreatePair(SyncEndpointKind leftKind, SyncEndpointKind rightKind, string name,
            string leftSlot = "left", string rightSlot = "right")
        {
            var endpoints = new SyncPairEndpoints(Endpoint(leftKind, leftSlot), Endpoint(rightKind, rightSlot),
                SyncMode.TwoWay);
            return SyncEndpointPairDraft.Create(endpoints, name);
        }

        internal SyncPair CreateLegacyPair(string localSlot, string cloudSlot, string name)
        {
            var local = _locals[localSlot];
            var cloud = _clouds[cloudSlot];
            var settings = new PilotSettings(local.Path, local.VolumeSerial, local.FileId, cloud.FolderPath,
                cloud.FolderId);
            return SyncPair.FromPilot(settings, name) with
            {
                Provider = SyncProviders.Dropbox,
                ConnectionId = cloud.ConnectionId,
                InitialReviewApproved = true
            };
        }

        internal DropboxPilotClient ClientFor(string cloudSlot)
        {
            var cloud = _clouds[cloudSlot];
            return new DropboxPilotClient(Http, new FixedAccessTokens(cloud.Account!.AccessToken), cloud.FolderPath,
                BindingFor(cloud), cloud.FolderId);
        }

        /// <summary>One retained root binding id per compatibility port, exactly as the authority names the two sides.</summary>
        internal string PortRootBindingId(SyncPair pair, string port) =>
            AuthorityId("root-binding", pair.Id, port);

        /// <summary>The pair's own ordered endpoints and the port each side carries, derived from the endpoint kinds.</summary>
        private (string LeftSlot, string RightSlot, SyncEndpointKind LeftKind, SyncEndpointKind RightKind)
            Layout(SyncPair pair)
        {
            if (pair.LeftEndpoint is { } left && pair.RightEndpoint is { } right)
                return (SlotFor(SyncEndpointContracts.ToEndpoint(left)),
                    SlotFor(SyncEndpointContracts.ToEndpoint(right)), left.Kind, right.Kind);
            // A document-less legacy pair is left-local and right-cloud over the rig's one local and one cloud folder.
            return (_locals.Values.Single().Slot, _clouds.Values.Single().Slot, SyncEndpointKind.Local,
                SyncEndpointKind.Dropbox);
        }

        private string SlotFor(SyncEndpoint endpoint) => endpoint.IsLocal
            ? _locals.Values.Single(local => string.Equals(local.Path, endpoint.LocalPath,
                StringComparison.OrdinalIgnoreCase)).Slot
            : _clouds.Values.Single(cloud => string.Equals(cloud.ConnectionId, endpoint.ConnectionId,
                StringComparison.Ordinal) && string.Equals(cloud.FolderId, endpoint.FolderId,
                StringComparison.Ordinal)).Slot;

        internal async Task RetainAsync(SyncPair pair, Func<string, string>? cloudIdentityTransform = null)
        {
            await Db.RegisterPairAsync(pair.Id, 1, new SyncAuthorityPairConfiguration("recoverable", 0, 1,
                "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true, AutomaticPruning: false,
                RecoveryRetentionDays: 30));
            var (leftSlot, rightSlot, leftKind, rightKind) = Layout(pair);
            var leftIsLocalPort = LeftCarriesLocalPort(leftKind, rightKind);
            var sides = new[]
            {
                (Slot: leftIsLocalPort ? leftSlot : rightSlot, Port: SyncAuthorityCloudSides.LogicalLeft),
                (Slot: leftIsLocalPort ? rightSlot : leftSlot, Port: SyncAuthorityCloudSides.LogicalRight)
            };
            // One real volume row per distinct local volume serial: two local roots on this computer's single
            // volume share their retained volume identity, exactly as the production registration does.
            var volumes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (slot, port) in sides)
            {
                var bindingId = PortRootBindingId(pair, port);
                if (_locals.TryGetValue(slot, out var local))
                {
                    var serial = local.VolumeSerial.ToString("x8");
                    var fileId = local.FileId.ToString("x16");
                    if (!volumes.TryGetValue(serial, out var volumeId))
                    {
                        volumeId = AuthorityId("volume", InstallationId, serial);
                        await Db.RegisterVolumeAsync(volumeId, InstallationId, serial, "NTFS", null,
                            supportsFileId128: false, supportsUsn: false, supportsAtomicRename: true);
                        volumes[serial] = volumeId;
                    }
                    if (port == SyncAuthorityCloudSides.LogicalLeft)
                        await Db.RegisterLocalRootBindingAsync(bindingId, pair.Id, 1, InstallationId, volumeId,
                            serial, fileId, local.Path);
                    else
                        // The typed binding of a pair's second local folder, exactly as the local-to-local
                        // runtime retains its right local root on the cloud-tagged port.
                        await Db.RegisterRootBindingAsync(bindingId, pair.Id, port, 1,
                            JsonSerializer.Serialize(new { localRootPath = local.Path,
                                localRootIdentity = serial + ":" + fileId }), local.Path);
                    continue;
                }
                var cloud = _clouds[slot];
                // The retained provider-account id is the registered derivation over this endpoint's own
                // identity tuple, exactly as the real registration writes it, so the read's retained
                // authority binding is a real binding rather than a fixture-local name.
                var accountId = SyncAuthorityProviderAccountIdentity.DeriveId(cloud.Kind == SyncEndpointKind.GoogleDrive
                    ? SyncProviders.GoogleDrive : SyncProviders.Dropbox, cloud.AccountId, cloud.RootNamespaceId,
                    cloud.HomeNamespaceId);
                await Db.RegisterProviderAccountAsync(accountId, InstallationId, cloud.AccountId,
                    cloud.RootNamespaceId, cloud.HomeNamespaceId, cloud.ConnectionId, 1,
                    provider: cloud.Kind == SyncEndpointKind.GoogleDrive
                        ? SyncProviders.GoogleDrive
                        : SyncProviders.Dropbox);
                if (cloudIdentityTransform is not null)
                    await Db.RegisterRootBindingAsync(bindingId, pair.Id, port, 1,
                        cloudIdentityTransform(JsonSerializer.Serialize(new { providerAccountId = accountId,
                            rootNamespaceId = cloud.RootNamespaceId, folderId = cloud.FolderId })),
                        cloud.FolderPath);
                else if (port == SyncAuthorityCloudSides.LogicalRight)
                    await Db.RegisterDropboxRootBindingAsync(bindingId, pair.Id, 1, InstallationId, accountId,
                        cloud.RootNamespaceId, cloud.FolderId, cloud.FolderPath);
                else
                    // A cloud endpoint on the pair's local port: the typed cloud root of a two-cloud pair.
                    await Db.RegisterRootBindingAsync(bindingId, pair.Id, port, 1,
                        JsonSerializer.Serialize(new { providerAccountId = accountId,
                            rootNamespaceId = cloud.RootNamespaceId, folderId = cloud.FolderId }),
                        cloud.FolderPath);
            }
            await RetainPairDefinitionAsync(pair);
        }

        internal async Task RetainPairDefinitionAsync(SyncPair pair) =>
            await Db.RetainPairDefinitionAsync(pair, true);

        internal async Task<SyncConflictContent> LoadAsync(string pairId, string relativePath,
            long maximumContentBytes = 8L * 1024 * 1024, string? conflictId = null, long? conflictGeneration = null)
        {
            using var appLock = PilotInstanceLock.Acquire(Path.Combine(Root, "host-state", "instance.lock"));
            var product = new SyncProductStateStore(appLock.StateAuthority);
            await product.UpdateAsync(current => current with { InstallationId = InstallationId });
            var connections = new DropboxConnectionStore(appLock.StateAuthority);
            var bindings = Wire.Bindings();
            if (bindings.Count > 0)
                await connections.SaveAsync(bindings[0] with { AdditionalConnections = bindings.Skip(1).ToArray() },
                    CancellationToken.None);
            else await connections.DeleteAsync(CancellationToken.None);
            using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, Http,
                new SyncPairStore(appLock.StateAuthority), new SyncRuntimeStateStore(appLock.StateAuthority),
                connections, new FixtureRefreshTokens(Wire), Db);
            return await host.LoadConflictContentAsync(pairId, relativePath, maximumContentBytes,
                CancellationToken.None, conflictId, conflictGeneration);
        }

        internal async Task<ConflictFixture> RetainConflictAsync(SyncPair pair, string relativePath,
            byte[] localPortBytes, byte[] dropboxPortBytes, string? variant = null, long generation = 1,
            string? objectId = null, ConflictFixture? reusingLocalPortVersion = null)
        {
            var normalized = PilotPath.NormalizeRelative(relativePath);
            var suffix = (variant is null ? "" : "-" + variant) + "-" + (++_sequence);
            var (leftSlot, rightSlot, leftKind, rightKind) = Layout(pair);
            var leftIsLocalPort = LeftCarriesLocalPort(leftKind, rightKind);
            var localPortSlot = leftIsLocalPort ? leftSlot : rightSlot;
            var localPortKind = leftIsLocalPort ? leftKind : rightKind;
            var dropboxPortSlot = leftIsLocalPort ? rightSlot : leftSlot;
            var dropboxPortKind = leftIsLocalPort ? rightKind : leftKind;
            objectId ??= "object-conflict" + suffix;
            await Db.RegisterObjectAsync(objectId, "file");
            // A successor conflict on the same logical object keeps the same reviewed local version and only
            // adds the newer cloud observation, exactly as a second planning pass over unchanged local state does.
            var localVersionId = reusingLocalPortVersion?.LocalPortVersionId ?? "version-local" + suffix;
            var localMembershipId = reusingLocalPortVersion?.LocalPortMembershipId ?? "membership-local" + suffix;
            var localPortHash = reusingLocalPortVersion?.LocalPortHash ?? (await RegisterPortVersionAsync(pair,
                objectId, localPortSlot, localPortKind, SyncAuthorityCloudSides.LogicalLeft, localVersionId,
                localMembershipId, normalized, localPortBytes, generation)).Hash;
            localPortBytes = reusingLocalPortVersion?.LocalPortBytes ?? localPortBytes;
            var cloudVersionId = "version-cloud" + suffix;
            var cloudMembershipId = "membership-cloud" + suffix;
            var dropboxPort = await RegisterPortVersionAsync(pair, objectId, dropboxPortSlot, dropboxPortKind,
                SyncAuthorityCloudSides.LogicalRight, cloudVersionId, cloudMembershipId, normalized,
                dropboxPortBytes, generation);
            var conflictId = "conflict" + suffix + "-" + AuthorityId("conflict", pair.Id, normalized);
            await Db.RecordConflictAsync(conflictId, pair.Id, objectId, "Fixture conflict", localVersionId,
                localMembershipId, cloudVersionId, cloudMembershipId);
            return new ConflictFixture(conflictId, localVersionId, cloudVersionId, localMembershipId,
                cloudMembershipId, localPortHash, dropboxPort.Hash,
                PortPath(localPortSlot, localPortKind, normalized),
                PortPath(dropboxPortSlot, dropboxPortKind, normalized), localPortBytes, dropboxPortBytes,
                leftIsLocalPort, dropboxPort.Revision, objectId);
        }

        /// <summary>The exact path a retained version of one port reads its bytes from.</summary>
        private string PortPath(string slot, SyncEndpointKind kind, string relativePath) =>
            kind == SyncEndpointKind.Local
                ? PilotPath.ResolveContainedPath(_locals[slot].Path, relativePath)
                : _clouds[slot].FolderPath.TrimEnd('/') + "/" + relativePath;

        private async Task<VersionRegistration> RegisterPortVersionAsync(SyncPair pair, string objectId, string slot,
            SyncEndpointKind kind, string port, string versionId, string membershipId, string relativePath,
            byte[] bytes, long generation) => kind == SyncEndpointKind.Local
            ? await RegisterLocalVersionAsync(pair, objectId, slot, port, versionId, membershipId, relativePath,
                bytes, generation)
            : await RegisterCloudVersionAsync(pair, objectId, slot, port, versionId, membershipId, relativePath,
                bytes, generation);

        private async Task<VersionRegistration> RegisterLocalVersionAsync(SyncPair pair, string objectId, string slot,
            string port, string versionId, string membershipId, string relativePath, byte[] bytes, long generation)
        {
            var local = _locals[slot];
            var full = PilotPath.ResolveContainedPath(local.Path, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, bytes);
            // The retained local identity is the FILE's own identity, exactly as the production scanner
            // records it, never the selected root folder's identity.
            uint volumeSerial;
            ulong fileId;
            await using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (!PilotDirectoryLease.GetFileInformationByHandle(stream.SafeFileHandle, out var information))
                    throw new IOException("The fixture could not read the local file's own identity.");
                volumeSerial = information.VolumeSerialNumber;
                fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            }
            var lastWrite = File.GetLastWriteTimeUtc(full);
            var identity = SyncAuthorityLocalVersionIdentity.Serialize(PilotEntryKind.File, volumeSerial, fileId,
                lastWrite, null, null, SyncContentIdentity.IdentityStampFor(pair.Provider));
            var hash = SyncContentIdentity.Compute(pair.Provider, bytes);
            await Db.RegisterObjectVersionAsync(versionId, objectId, port, identity, hash, bytes.LongLength);
            await Db.RegisterMembershipAsync(membershipId, objectId, PortRootBindingId(pair, port), relativePath,
                generation);
            return new VersionRegistration(versionId, hash, "local-reviewed:" + versionId);
        }

        private async Task<VersionRegistration> RegisterCloudVersionAsync(SyncPair pair, string objectId, string slot,
            string port, string versionId, string membershipId, string relativePath, byte[] bytes, long generation)
        {
            var cloud = _clouds[slot];
            string identity;
            string hash;
            string revision;
            if (cloud.Kind == SyncEndpointKind.Dropbox)
            {
                var file = Wire.Put(cloud.Account!, cloud.FolderPath, relativePath, bytes);
                identity = JsonSerializer.Serialize(new { fileId = file.Id, revision = file.Revision });
                hash = file.Hash;
                revision = file.Revision;
            }
            else
            {
                revision = "drive-revision-" + slot;
                identity = JsonSerializer.Serialize(new { fileId = "drive-file-" + slot, revision });
                hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            }
            await Db.RegisterObjectVersionAsync(versionId, objectId, port, identity, hash, bytes.LongLength);
            await Db.RegisterMembershipAsync(membershipId, objectId, PortRootBindingId(pair, port), relativePath,
                generation);
            return new VersionRegistration(versionId, hash, revision);
        }

        private static DropboxConnectionBinding BindingFor(CloudSpec cloud) => new DropboxConnectionBinding(
            DropboxProviderConfiguration.RegisteredClientId, cloud.AccountId, cloud.RootNamespaceId,
            cloud.HomeNamespaceId, DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes, ConnectionId: cloud.ConnectionId,
            CredentialReference: WindowsDropboxRefreshTokenStore.AccountTarget(cloud.ConnectionId)).Normalize();

        internal static string AuthorityId(string role, params string[] parts) => role + "-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant()[..32];

        public ValueTask DisposeAsync()
        {
            Http.Dispose();
            Wire.Dispose();
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine("RESIDUAL conflict content fixture scratch retained at " + Root + " (" +
                    error.GetType().Name + ")");
            }
            return ValueTask.CompletedTask;
        }
    }
}
