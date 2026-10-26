using System.Text.Json;
using Massdex.SyncShark;

// EL-1/CFC follow-up: the side- and context-bound Google Drive endpoint web location. Every case calls the
// NEW public entry point SyncAuthorityRuntimeHost.ResolveGoogleDriveEndpointWebLocationAsync and nothing else:
// no helper is tested in its place. The rig is the existing CloudPairHostBindingChecks real-Host harness (real
// SQLite authority, real Google Drive adapter, bounded synthetic HTTP transport, synthetic tokens); no live
// provider, credential, install or product data is reached.
internal static class GoogleDriveEndpointNavigationChecks
{
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException(
                "Use a fresh isolated Google Drive endpoint navigation fixture root.");
        Directory.CreateDirectory(_root);
        CloudPairHostBindingChecks.UseFixtureRoot(_root);
        Cases.Clear();
        _assertions = 0;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Google Drive endpoint navigation checks require Windows NTFS and DPAPI-backed fixture state.");

        await DrivePairBothSidesAsync();
        await LegacyNoDocumentPairAsync();
        await InvalidSideRefusedAsync();
        await ForeignPairContextRefusedAsync();
        await WrongDeclaredNamespaceRefusedAsync();
        await SameAccountTwoRootsAsync();
        await ExplicitLocalOrientationsAsync();
        await MalformedContextRefusalsAsync();
        await StaleRecordedIdentityRefusalsAsync();
        await DuringReadChangesAsync();
        await NameOnlyRenameSucceedsAsync();
        await NestedFileRelativePathAsync();

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "GoogleDriveEndpointNavigationChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["entry_point"] = "SyncAuthorityRuntimeHost.ResolveGoogleDriveEndpointWebLocationAsync",
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "google-drive-endpoint-navigation-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS google drive endpoint navigation: " + _assertions + " assertions across " +
            Cases.Count + " cases; both sides of one explicit Drive pair, a genuine no-document legacy pair, " +
            "and three pre-provider refusals.");
    }

    private static void Claim(string id, string requirement) => Cases.Add(new Dictionary<string, object?>
    {
        ["id"] = id,
        ["requirement"] = requirement,
        ["observed"] = "pending"
    });

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        _assertions++;
        var current = Cases[^1];
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

    /// <summary>
    /// One provider-supplied browser location for a Drive folder node, supplied through the narrow additive
    /// fixture seam over that endpoint's own retained account binding. The URL names that folder's own native
    /// identity, which is what the production navigation validation requires.
    /// </summary>
    private static string GiveDriveFolderWebLocation(CloudPairHostBindingChecks.HostRig rig,
        CloudPairHostBindingChecks.EndpointSpec spec)
    {
        var url = "https://drive.google.com/drive/folders/" + spec.FolderId;
        rig.Wire.AddDriveFolder(rig.DriveClient(spec).Connection, spec.FolderPath, spec.FolderId, url);
        return url;
    }

    /// <summary>The four read counters and every write counter, snapshotted AFTER fixture setup.</summary>
    private static (int Requests, int NodeReads, int NameLookups, int MediaReads, int Writes, int Creates,
        int Multipart, int Updates, int Copies, int RecoveryDeletes) Snapshot(
            CloudPairHostBindingChecks.HostRig rig) => (rig.Wire.TotalRequests, rig.Wire.DriveNodeReadRequests,
        rig.Wire.DriveNameLookupRequests, rig.Wire.DriveMediaReadRequests, rig.Wire.Writes.Total,
        rig.Wire.DriveCreateRequests, rig.Wire.DriveMultipartCreateRequests, rig.Wire.DriveUpdateRequests,
        rig.Wire.DriveRecoveryCopyRequests, rig.Wire.RecoveryDeleteRequests);

    /// <summary>Zero provider mutations and zero media reads, compared against the post-setup snapshot.</summary>
    private static void RequireNoMutation(CloudPairHostBindingChecks.HostRig rig,
        (int Requests, int NodeReads, int NameLookups, int MediaReads, int Writes, int Creates, int Multipart,
            int Updates, int Copies, int RecoveryDeletes) before, string tag)
    {
        var after = Snapshot(rig);
        Require(after.Writes == before.Writes && after.Creates == before.Creates &&
                after.Multipart == before.Multipart && after.Updates == before.Updates &&
                after.Copies == before.Copies && after.RecoveryDeletes == before.RecoveryDeletes &&
                after.MediaReads == before.MediaReads,
            tag + ": the navigation made no provider write, copy, deletion or media read (writes " +
            before.Writes + " to " + after.Writes + ", media " + before.MediaReads + " to " + after.MediaReads + ")");
    }

    private static SyncPairEndpoints CloudEndpoints(CloudPairHostBindingChecks.EndpointSpec left,
        CloudPairHostBindingChecks.EndpointSpec right) => new(
        SyncEndpoint.Cloud(left.Kind, left.ConnectionId, left.EndpointNamespaceId, left.FolderId, left.FolderPath),
        SyncEndpoint.Cloud(right.Kind, right.ConnectionId, right.EndpointNamespaceId, right.FolderId,
            right.FolderPath), SyncMode.TwoWay);

    private static async Task<SyncPair> RetainExplicitDrivePairAsync(CloudPairHostBindingChecks.HostRig rig,
        CloudPairHostBindingChecks.EndpointSpec left, CloudPairHostBindingChecks.EndpointSpec right, string tag,
        string? leftNamespace = null, string? rightNamespace = null)
    {
        // Keep the Windows-only public binding call guarded in this helper too: the platform
        // analyzer does not propagate RunAsync's guard through intervening async test methods.
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Endpoint-pair binding checks require Windows.");
        var endpoints = CloudEndpoints(left, right);
        if (rightNamespace is not null)
            endpoints = endpoints with
            {
                Right = SyncEndpoint.Cloud(right.Kind, right.ConnectionId, rightNamespace, right.FolderId,
                    right.FolderPath)
            };
        if (leftNamespace is not null)
            endpoints = endpoints with
            {
                Left = SyncEndpoint.Cloud(left.Kind, left.ConnectionId, leftNamespace, left.FolderId,
                    left.FolderPath)
            };
        if (leftNamespace is null && rightNamespace is null)
        {
            var bound = await rig.Host.BindEndpointPairAsync(
                SyncEndpointPairDraft.Create(endpoints, tag));
            return bound.Pair;
        }
        // A declared namespace that does not match the retained account is refused by the binding host, so the
        // pair is retained through the same public authority write the host itself uses; the production read
        // under test is then what has to refuse it.
        var draft = SyncCloudPairConfiguration.CreateDraft(endpoints, tag);
        await rig.Db.RegisterPairAsync(draft.Id, draft.PolicyRevision);
        await rig.Db.RetainPairDefinitionAsync(draft, select: true);
        return (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == draft.Id);
    }

    private static async Task DrivePairBothSidesAsync()
    {
        Claim("GDN-1-pair-both-sides",
            "Each Google Drive side of one explicit Drive pair resolves its own native URL, item, root and account");
        var left = CloudPairHostBindingChecks.HostDrive("gdn1-left", "/DriveLeft");
        var right = CloudPairHostBindingChecks.HostDrive("gdn1-right", "/DriveRight");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("gdn-1-pair-both-sides");
        await rig.AddEndpointAsync(left);
        await rig.AddEndpointAsync(right);
        var leftUrl = GiveDriveFolderWebLocation(rig, left);
        var rightUrl = GiveDriveFolderWebLocation(rig, right);
        var pair = await RetainExplicitDrivePairAsync(rig, left, right, "GDN 1 pair");
        var context = SyncEndpointContext.Capture(pair);
        Require(pair.EndpointDocument is not null && context.Left.Kind == SyncEndpointKind.GoogleDrive &&
                context.Right.Kind == SyncEndpointKind.GoogleDrive,
            "GDN-1 the pair is an explicit two-Google-Drive document with two ordered Drive endpoints");
        Require(left.ConnectionId != right.ConnectionId && left.FolderId != right.FolderId,
            "GDN-1 the fixture really retains two distinct Drive accounts and two distinct roots");
        // The read and write counters are snapshotted AFTER fixture setup, so setup reads cannot contaminate
        // the navigation counts.
        var before = Snapshot(rig);
        var leftLocation = await rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Left,
            context);
        Require(leftLocation.WebViewLink == leftUrl && leftLocation.FileId == left.FolderId &&
                leftLocation.RootFolderId == left.FolderId && leftLocation.ConnectionId == left.ConnectionId &&
                leftLocation.Kind == GoogleDriveEntryKind.Folder && leftLocation.RelativePath == "." &&
                leftLocation.NamespaceId == left.EndpointNamespaceId,
            "GDN-1 the left side returned its own provider-supplied native URL, item, root, namespace and account");
        var rightLocation = await rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Right,
            context);
        Require(rightLocation.WebViewLink == rightUrl && rightLocation.FileId == right.FolderId &&
                rightLocation.RootFolderId == right.FolderId && rightLocation.ConnectionId == right.ConnectionId &&
                rightLocation.NamespaceId == right.EndpointNamespaceId,
            "GDN-1 the right side returned its own distinct native URL, item, root, namespace and account");
        Require(leftLocation.WebViewLink != rightLocation.WebViewLink &&
                leftLocation.ConnectionId != rightLocation.ConnectionId &&
                leftLocation.RootFolderId != rightLocation.RootFolderId,
            "GDN-1 the two sides' URLs, accounts and roots are provably distinct from each other");
        Require(rig.Wire.DriveNodeReadRequests > before.NodeReads &&
                rig.Wire.TotalRequests > before.Requests,
            "GDN-1 both resolutions read provider metadata over the bounded transport (node reads " +
            before.NodeReads + " to " + rig.Wire.DriveNodeReadRequests + ")");
        RequireNoMutation(rig, before, "GDN-1");
    }

    private static async Task LegacyNoDocumentPairAsync()
    {
        Claim("GDN-2-legacy-no-document",
            "A genuine no-document local/Google legacy pair resolves its right Google side without a namespace");
        var drive = CloudPairHostBindingChecks.HostDrive("gdn2", "/DriveLegacy");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("gdn-2-legacy-no-document");
        await rig.AddEndpointAsync(drive);
        var url = GiveDriveFolderWebLocation(rig, drive);
        var binding = rig.DriveClient(drive).Connection;
        var localRoot = Directory.CreateDirectory(Path.Combine(_root, "gdn-2-local")).FullName;
        using var lease = PilotDirectoryLease.Open(localRoot);
        var legacy = SyncPair.FromPilot(new PilotSettings(localRoot, lease.Identity.VolumeSerialNumber,
                lease.Identity.FileId, drive.FolderPath, drive.FolderId), "GDN 2 legacy", binding.AccountKey) with
        {
            Provider = SyncProviders.GoogleDrive,
            ConnectionId = binding.ConnectionId,
            InitialReviewApproved = false,
            Enabled = true
        };
        await rig.Db.RegisterPairAsync(legacy.Id, legacy.PolicyRevision, new SyncAuthorityPairConfiguration(
            "disabled", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: 30));
        await rig.Db.RetainPairDefinitionAsync(legacy, select: false);
        var pair = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == legacy.Id);
        Require(pair.EndpointDocument is null,
            "GDN-2 the pair under test really has no endpoint document (a genuine legacy pair)");
        var context = SyncEndpointContext.Capture(pair);
        Require(context.Left.IsLocal && context.Right.Kind == SyncEndpointKind.GoogleDrive &&
                context.Right.NamespaceId is null && context.Right.ConnectionId == binding.ConnectionId,
            "GDN-2 the legacy pair resolves as left-local and right-Google with NO recorded namespace");
        var before = Snapshot(rig);
        var location = await rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Right,
            context);
        Require(location.WebViewLink == url && location.FileId == drive.FolderId &&
                location.RootFolderId == drive.FolderId && location.ConnectionId == binding.ConnectionId,
            "GDN-2 the right side succeeded without a namespace and returned its own native URL, item, root and account");
        Require(rig.Wire.DriveNodeReadRequests > before.NodeReads,
            "GDN-2 the legacy resolution read provider metadata over the bounded transport");
        RequireNoMutation(rig, before, "GDN-2");
    }

    private static async Task InvalidSideRefusedAsync()
    {
        Claim("GDN-3-invalid-side", "An invalid side value refuses before any HTTP request");
        var left = CloudPairHostBindingChecks.HostDrive("gdn3-left", "/DriveLeft");
        var right = CloudPairHostBindingChecks.HostDrive("gdn3-right", "/DriveRight");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("gdn-3-invalid-side");
        await rig.AddEndpointAsync(left);
        await rig.AddEndpointAsync(right);
        GiveDriveFolderWebLocation(rig, left);
        GiveDriveFolderWebLocation(rig, right);
        var pair = await RetainExplicitDrivePairAsync(rig, left, right, "GDN 3 pair");
        var context = SyncEndpointContext.Capture(pair);
        var before = Snapshot(rig);
        await Refused<ArgumentOutOfRangeException>(() =>
                rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, (SyncPairSide)99, context),
            "GDN-3 a side that is neither logical left nor right is refused rather than mapped onto the right side");
        Require(rig.Wire.TotalRequests == before.Requests && rig.Wire.DriveNodeReadRequests == before.NodeReads &&
                rig.Wire.DriveNameLookupRequests == before.NameLookups,
            "GDN-3 the invalid side made ZERO HTTP requests and zero provider reads");
        RequireNoMutation(rig, before, "GDN-3");
    }

    private static async Task ForeignPairContextRefusedAsync()
    {
        Claim("GDN-4-foreign-pair-context",
            "An expected context naming a different pair id with otherwise equal endpoints refuses before any HTTP request");
        var left = CloudPairHostBindingChecks.HostDrive("gdn4-left", "/DriveLeft");
        var right = CloudPairHostBindingChecks.HostDrive("gdn4-right", "/DriveRight");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("gdn-4-foreign-pair-context");
        await rig.AddEndpointAsync(left);
        await rig.AddEndpointAsync(right);
        GiveDriveFolderWebLocation(rig, left);
        GiveDriveFolderWebLocation(rig, right);
        var pair = await RetainExplicitDrivePairAsync(rig, left, right, "GDN 4 pair");
        var ownContext = SyncEndpointContext.Capture(pair);
        var foreign = ownContext with { PairId = "gdn-4-foreign-pair-identity" };
        Require(foreign.PairId != pair.Id && foreign.Left == ownContext.Left && foreign.Right == ownContext.Right,
            "GDN-4 the supplied context carries otherwise identical ordered endpoints under a different pair id");
        var before = Snapshot(rig);
        var refusal = await Refused<ArgumentException>(() =>
                rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Left, foreign),
            "GDN-4 a context for a different pair is refused rather than trusted because it compares equal to itself");
        Require(refusal.Message.Contains("different folder pair", StringComparison.Ordinal),
            "GDN-4 the refusal names the pair identity mismatch as its reason");
        Require(rig.Wire.TotalRequests == before.Requests && rig.Wire.DriveNodeReadRequests == before.NodeReads,
            "GDN-4 the foreign context made ZERO HTTP requests and zero provider reads");
        RequireNoMutation(rig, before, "GDN-4");
    }

    private static async Task WrongDeclaredNamespaceRefusedAsync()
    {
        Claim("GDN-5-wrong-declared-namespace",
            "An explicit endpoint with a wrong declared namespace but a valid bound account and root refuses before any HTTP request");
        var left = CloudPairHostBindingChecks.HostDrive("gdn5-left", "/DriveLeft");
        var right = CloudPairHostBindingChecks.HostDrive("gdn5-right", "/DriveRight");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("gdn-5-wrong-declared-namespace");
        await rig.AddEndpointAsync(left);
        await rig.AddEndpointAsync(right);
        GiveDriveFolderWebLocation(rig, left);
        GiveDriveFolderWebLocation(rig, right);
        var pair = await RetainExplicitDrivePairAsync(rig, left, right, "GDN 5 pair",
            rightNamespace: "wrong-declared-namespace");
        var context = SyncEndpointContext.Capture(pair);
        Require(context.Right.NamespaceId == "wrong-declared-namespace" &&
                context.Right.ConnectionId == right.ConnectionId && context.Right.FolderId == right.FolderId,
            "GDN-5 the retained document declares a wrong namespace over an otherwise valid account and root");
        var before = Snapshot(rig);
        var refusal = await Refused<InvalidOperationException>(() =>
                rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Right, context),
            "GDN-5 the wrong declared namespace is refused by the shared retained-authority check");
        Require(refusal.Message.Contains("namespace", StringComparison.OrdinalIgnoreCase),
            "GDN-5 the refusal names the namespace identity mismatch as its reason");
        Require(rig.Wire.TotalRequests == before.Requests && rig.Wire.DriveNodeReadRequests == before.NodeReads,
            "GDN-5 the wrong namespace made ZERO HTTP requests and zero provider reads");
        RequireNoMutation(rig, before, "GDN-5");
    }

    /// <summary>Retains one explicit pair through the same public authority writes the Host's own binding uses.</summary>
    private static async Task<SyncPair> RetainExplicitPairAsync(CloudPairHostBindingChecks.HostRig rig,
        SyncPairEndpoints endpoints, string tag)
    {
        var draft = SyncEndpointPairDraft.Create(endpoints, tag);
        await rig.Db.RegisterPairAsync(draft.Id, draft.PolicyRevision);
        await rig.Db.RetainPairDefinitionAsync(draft, select: true);
        return (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == draft.Id);
    }

    /// <summary>One local endpoint over a real directory, resolved through its own NTFS lease.</summary>
    private static SyncEndpoint LocalEndpoint(CloudPairHostBindingChecks.HostRig rig, string tag)
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, tag + "-local")).FullName;
        using var lease = PilotDirectoryLease.Open(path);
        return SyncEndpoint.Local(path, new SyncLocalEndpointIdentity(lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId));
    }

    /// <summary>
    /// Replaces the retained Google connection binding through its own existing store, so the selected
    /// account the call was made for is no longer a retained account and a different one is. The store's own
    /// delete and save APIs are used, the store's own reassignment guard is respected, and no production
    /// check is bypassed.
    /// </summary>
    private static async Task ReplaceGoogleBindingAsync(CloudPairHostBindingChecks.HostRig rig)
    {
        var store = new GoogleDriveConnectionStore(rig.StateAuthority);
        var original = store.Load()
                       ?? throw new InvalidDataException("The fixture retains no Google connection to change.");
        var permission = original.PermissionId + "-changed";
        var changed = new GoogleDriveConnectionBinding(original.ClientId,
            GoogleDriveConnectionBinding.CreateAccountKey(permission), permission, original.Email,
            original.DisplayName, original.Scopes, CredentialReference: original.CredentialReference).Normalize();
        await store.DeleteAsync();
        await store.SaveAsync(changed);
    }

    private static async Task SameAccountTwoRootsAsync()
    {
        Claim("GDN-6-two-roots-one-account",
            "One Google account with two distinct sibling roots serves each root's own native URL and root");
        var first = CloudPairHostBindingChecks.HostDrive("gdn6", "/DriveOne");
        var second = first with { FolderId = "gd-folder-gdn6-two", FolderPath = "/DriveTwo" };
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("gdn-6-two-roots-one-account");
        await rig.AddEndpointAsync(first);
        await rig.AddEndpointAsync(second);
        var firstUrl = GiveDriveFolderWebLocation(rig, first);
        var secondUrl = GiveDriveFolderWebLocation(rig, second);
        Require(first.ConnectionId == second.ConnectionId && first.FolderId != second.FolderId,
            "GDN-6 the fixture really retains ONE Google account with TWO distinct sibling roots");
        var pair = await RetainExplicitPairAsync(rig, CloudEndpoints(first, second), "GDN 6 pair");
        var context = SyncEndpointContext.Capture(pair);
        Require(context.Left.ConnectionId == context.Right.ConnectionId &&
                context.Left.FolderId != context.Right.FolderId,
            "GDN-6 the pair's own captured context carries one shared account and two distinct roots");
        var before = Snapshot(rig);
        var left = await rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Left, context);
        Require(left.WebViewLink == firstUrl && left.FileId == first.FolderId &&
                left.RootFolderId == first.FolderId && left.ConnectionId == first.ConnectionId,
            "GDN-6 the left root returned its OWN native URL, item, root and shared account");
        var right = await rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Right, context);
        Require(right.WebViewLink == secondUrl && right.FileId == second.FolderId &&
                right.RootFolderId == second.FolderId && right.ConnectionId == second.ConnectionId,
            "GDN-6 the right root returned its OWN native URL, item, root and shared account");
        Require(left.WebViewLink != right.WebViewLink && left.RootFolderId != right.RootFolderId &&
                left.ConnectionId == right.ConnectionId,
            "GDN-6 the two roots differ while the account is provably the same one");
        Require(rig.Wire.DriveNodeReadRequests > before.NodeReads,
            "GDN-6 both resolutions read provider metadata over the bounded transport");
        RequireNoMutation(rig, before, "GDN-6");
    }

    private static async Task ExplicitLocalOrientationsAsync()
    {
        Claim("GDN-7-explicit-local-orientations",
            "The explicit Google/local and local/Google orientations both serve the cloud side and refuse the local side without provider access");
        foreach (var googleFirst in new[] { true, false })
        {
            var tag = googleFirst ? "gdn7-google-local" : "gdn7-local-google";
            var drive = CloudPairHostBindingChecks.HostDrive(tag, googleFirst ? "/DriveLeft" : "/DriveRight");
            using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync(tag);
            await rig.AddEndpointAsync(drive);
            var url = GiveDriveFolderWebLocation(rig, drive);
            var local = LocalEndpoint(rig, tag);
            var cloud = SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, drive.ConnectionId,
                drive.EndpointNamespaceId, drive.FolderId, drive.FolderPath);
            var endpoints = googleFirst
                ? new SyncPairEndpoints(cloud, local, SyncMode.TwoWay)
                : new SyncPairEndpoints(local, cloud, SyncMode.TwoWay);
            var pair = await RetainExplicitPairAsync(rig, endpoints, "GDN 7 " + tag);
            Require(pair.EndpointDocument is { Left: not null, Right: not null },
                "GDN-7 " + tag + ": the pair retains an explicit two-endpoint document");
            var context = SyncEndpointContext.Capture(pair);
            var cloudSide = googleFirst ? SyncPairSide.Left : SyncPairSide.Right;
            var localSide = googleFirst ? SyncPairSide.Right : SyncPairSide.Left;
            var before = Snapshot(rig);
            var location = await rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, cloudSide, context);
            Require(location.WebViewLink == url && location.FileId == drive.FolderId &&
                    location.RootFolderId == drive.FolderId && location.ConnectionId == drive.ConnectionId &&
                    location.Kind == GoogleDriveEntryKind.Folder,
                "GDN-7 " + tag + ": the cloud side succeeded and returned its own native URL, item, root and account");
            var afterProvider = Snapshot(rig);
            var refusal = await Refused<InvalidOperationException>(() =>
                    rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, localSide, context),
                "GDN-7 " + tag + ": the local side is refused as a non-Drive endpoint");
            Require(refusal.Message.Contains("not a Google Drive endpoint", StringComparison.Ordinal),
                "GDN-7 " + tag + ": the refusal names the local side as the reason");
            Require(rig.Wire.TotalRequests == afterProvider.Requests &&
                    rig.Wire.DriveNodeReadRequests == afterProvider.NodeReads &&
                    rig.Wire.DriveNameLookupRequests == afterProvider.NameLookups,
                "GDN-7 " + tag + ": the local-side request made NO provider access at all");
            RequireNoMutation(rig, before, "GDN-7 " + tag);
        }
    }

    private static async Task MalformedContextRefusalsAsync()
    {
        Claim("GDN-8-context-refusals",
            "A null, either-side-null, mis-ordered or malformed context and a missing retained pair are exact pre-provider refusals");
        var left = CloudPairHostBindingChecks.HostDrive("gdn8-left", "/DriveLeft");
        var right = CloudPairHostBindingChecks.HostDrive("gdn8-right", "/DriveRight");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("gdn-8-context-refusals");
        await rig.AddEndpointAsync(left);
        await rig.AddEndpointAsync(right);
        GiveDriveFolderWebLocation(rig, left);
        GiveDriveFolderWebLocation(rig, right);
        var pair = await RetainExplicitPairAsync(rig, CloudEndpoints(left, right), "GDN 8 pair");
        var context = SyncEndpointContext.Capture(pair);
        // A second, Google/local pair so a malformed LOCAL side is available too.
        var mixedDrive = CloudPairHostBindingChecks.HostDrive("gdn8-mixed", "/DriveMixed");
        await rig.AddEndpointAsync(mixedDrive);
        GiveDriveFolderWebLocation(rig, mixedDrive);
        var mixed = await RetainExplicitPairAsync(rig, new SyncPairEndpoints(
            SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, mixedDrive.ConnectionId,
                mixedDrive.EndpointNamespaceId, mixedDrive.FolderId, mixedDrive.FolderPath),
            LocalEndpoint(rig, "gdn8-mixed"), SyncMode.TwoWay), "GDN 8 mixed pair");
        var mixedContext = SyncEndpointContext.Capture(mixed);
        var before = Snapshot(rig);
        await Refused<ArgumentNullException>(() =>
                rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Left, null!),
            "GDN-8 a null expected context is refused before any context or provider work");
        var noLeft = await Refused<ArgumentException>(() =>
                rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Left,
                    context with { Left = null! }),
            "GDN-8 a context whose left side is null is refused");
        Require(noLeft.Message.Contains("requires both ordered sides", StringComparison.Ordinal),
            "GDN-8 the null-left refusal names the missing ordered side");
        await Refused<ArgumentException>(() =>
                rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Right,
                    context with { Right = null! }),
            "GDN-8 a context whose right side is null is refused");
        var misordered = await Refused<ArgumentException>(() =>
                rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Left,
                    context with { Left = context.Right, Right = context.Left }),
            "GDN-8 a context whose sides are in the wrong order is refused");
        Require(misordered.Message.Contains("left then right", StringComparison.Ordinal),
            "GDN-8 the mis-ordered refusal names the left-then-right rule");
        var malformed = await Refused<ArgumentException>(() =>
                rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(mixed.Id, SyncPairSide.Right,
                    mixedContext with
                    {
                        // The mixed pair is Google-left/local-right, so the malformed side is the LOCAL right
                        // one: a local endpoint carrying cloud identity is structurally refused.
                        Right = mixedContext.Right with { Provider = SyncProviders.GoogleDrive,
                            ConnectionId = "connection-invented" }
                    }),
            "GDN-8 a local side carrying cloud identity is refused as malformed");
        Require(malformed.Message.Contains("cannot carry", StringComparison.Ordinal),
            "GDN-8 the malformed-side refusal names the mixed local/cloud identity");
        var missing = await Refused<InvalidDataException>(() =>
                rig.Host.ResolveGoogleDriveEndpointWebLocationAsync("gdn8-not-retained-pair",
                    SyncPairSide.Right, context),
            "GDN-8 a pair id that is not retained is refused");
        Require(missing.Message.Contains("no longer retained", StringComparison.Ordinal),
            "GDN-8 the missing-pair refusal names the unretained pair");
        Require(rig.Wire.TotalRequests == before.Requests &&
                rig.Wire.DriveNodeReadRequests == before.NodeReads &&
                rig.Wire.DriveNameLookupRequests == before.NameLookups,
            "GDN-8 every one of the six refusals made ZERO HTTP requests and zero provider reads");
        RequireNoMutation(rig, before, "GDN-8");
    }

    private static async Task StaleRecordedIdentityRefusalsAsync()
    {
        Claim("GDN-9-stale-recorded-identity",
            "A pre-await context carrying a stale recorded account, root or local identity refuses before any HTTP request");
        var left = CloudPairHostBindingChecks.HostDrive("gdn9-left", "/DriveLeft");
        var right = CloudPairHostBindingChecks.HostDrive("gdn9-right", "/DriveRight");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("gdn-9-stale-recorded-identity");
        await rig.AddEndpointAsync(left);
        await rig.AddEndpointAsync(right);
        GiveDriveFolderWebLocation(rig, left);
        GiveDriveFolderWebLocation(rig, right);
        var pair = await RetainExplicitPairAsync(rig, CloudEndpoints(left, right), "GDN 9 pair");
        var context = SyncEndpointContext.Capture(pair);
        // The mixed pair needs its OWN Google account and root: one provider account and folder identity is
        // retained by exactly one pair, which the existing pair-set rule enforces.
        var mixedDrive = CloudPairHostBindingChecks.HostDrive("gdn9-mixed", "/DriveMixed");
        await rig.AddEndpointAsync(mixedDrive);
        GiveDriveFolderWebLocation(rig, mixedDrive);
        var mixed = await RetainExplicitPairAsync(rig, new SyncPairEndpoints(
            LocalEndpoint(rig, "gdn9-mixed"), SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive,
                mixedDrive.ConnectionId, mixedDrive.EndpointNamespaceId, mixedDrive.FolderId,
                mixedDrive.FolderPath), SyncMode.TwoWay), "GDN 9 mixed pair");
        var mixedContext = SyncEndpointContext.Capture(mixed);
        var before = Snapshot(rig);
        var staleAccount = await Refused<InvalidOperationException>(() =>
                rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Right,
                    context with { Right = context.Right with { ConnectionId = "connection-stale-account" } }),
            "GDN-9 a recorded account that is no longer the pair's own account refuses");
        Require(staleAccount.Message.Contains("changed while its Google location was checked",
                StringComparison.Ordinal),
            "GDN-9 the stale-account refusal is the ordered-context comparison's own message");
        await Refused<InvalidOperationException>(() =>
                rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Right,
                    context with { Right = context.Right with { FolderId = "gd-folder-stale-root" } }),
            "GDN-9 a recorded root that is no longer the pair's own root refuses");
        await Refused<InvalidOperationException>(() =>
                rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(mixed.Id, SyncPairSide.Right,
                    mixedContext with
                    {
                        Left = mixedContext.Left with
                        {
                            LocalRootFileId = mixedContext.Left.LocalRootFileId!.Value + 1
                        }
                    }),
            "GDN-9 a recorded local root identity that no longer matches refuses");
        Require(rig.Wire.TotalRequests == before.Requests &&
                rig.Wire.DriveNodeReadRequests == before.NodeReads &&
                rig.Wire.DriveNameLookupRequests == before.NameLookups,
            "GDN-9 all three stale-identity refusals made ZERO HTTP requests and zero provider reads");
        RequireNoMutation(rig, before, "GDN-9");
    }

    private static async Task DuringReadChangesAsync()
    {
        Claim("GDN-10-during-read-change",
            "An account-binding, other-endpoint or local-identity change during the awaited read refuses after provider access and returns no URL");
        // (i) the selected protected account binding is replaced during the read.
        {
            var left = CloudPairHostBindingChecks.HostDrive("gdn10a-left", "/DriveLeft");
            var right = CloudPairHostBindingChecks.HostDrive("gdn10a-right", "/DriveRight");
            using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("gdn-10-account-binding");
            await rig.AddEndpointAsync(left);
            await rig.AddEndpointAsync(right);
            GiveDriveFolderWebLocation(rig, left);
            GiveDriveFolderWebLocation(rig, right);
            var pair = await RetainExplicitPairAsync(rig, CloudEndpoints(left, right), "GDN 10a pair");
            var context = SyncEndpointContext.Capture(pair);
            var before = Snapshot(rig);
            var fired = 0;
            rig.Wire.OnDriveNodeReadOnceAsync = async () =>
            {
                fired++;
                await ReplaceGoogleBindingAsync(rig);
            };
            var refusal = await Refused<InvalidOperationException>(() =>
                    rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Left, context),
                "GDN-10a a protected account binding replaced during the read refuses");
            Require(fired == 1, "GDN-10a the one-shot during-read callback actually fired exactly once");
            Require(rig.Wire.DriveNodeReadRequests > before.NodeReads &&
                    rig.Wire.TotalRequests > before.Requests,
                "GDN-10a provider metadata was accessed before the refusal (node reads " + before.NodeReads +
                " to " + rig.Wire.DriveNodeReadRequests + ")");
            Require(refusal.Message.Contains("Google connection changed", StringComparison.Ordinal),
                "GDN-10a the refusal is the protected-binding revalidation's own message");
            RequireNoMutation(rig, before, "GDN-10a");
        }
        // (ii) the OTHER ordered endpoint is changed during the read.
        {
            var left = CloudPairHostBindingChecks.HostDrive("gdn10b-left", "/DriveLeft");
            var right = CloudPairHostBindingChecks.HostDrive("gdn10b-right", "/DriveRight");
            using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("gdn-10-other-endpoint");
            await rig.AddEndpointAsync(left);
            await rig.AddEndpointAsync(right);
            GiveDriveFolderWebLocation(rig, left);
            GiveDriveFolderWebLocation(rig, right);
            var pair = await RetainExplicitPairAsync(rig, CloudEndpoints(left, right), "GDN 10b pair");
            var context = SyncEndpointContext.Capture(pair);
            var before = Snapshot(rig);
            var fired = 0;
            rig.Wire.OnDriveNodeReadOnceAsync = async () =>
            {
                fired++;
                // The mutable folder path is deliberately outside the pair's derived identity, so this is a
                // genuine recorded-endpoint change the retained rows accept and the identity comparison catches.
                var changed = pair with
                {
                    RightEndpoint = pair.RightEndpoint! with { FolderPath = "/DriveOtherRecordedPath" },
                    DropboxRoot = "/DriveOtherRecordedPath"
                };
                await rig.Db.RetainPairDefinitionAsync(changed, select: true);
            };
            var refusal = await Refused<InvalidOperationException>(() =>
                    rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Left, context),
                "GDN-10b the other ordered endpoint changing during the read refuses");
            Require(fired == 1, "GDN-10b the one-shot during-read callback actually fired exactly once");
            Require(rig.Wire.DriveNodeReadRequests > before.NodeReads,
                "GDN-10b provider metadata was accessed before the refusal");
            Require(refusal.Message.Contains("changed while its Google location was checked",
                    StringComparison.Ordinal),
                "GDN-10b the refusal is the ordered-context comparison's own message");
            RequireNoMutation(rig, before, "GDN-10b");
        }
        // (iii) a local endpoint's root-file identity is changed during the read.
        {
            var drive = CloudPairHostBindingChecks.HostDrive("gdn10c", "/DriveRight");
            using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("gdn-10-local-identity");
            await rig.AddEndpointAsync(drive);
            GiveDriveFolderWebLocation(rig, drive);
            var pair = await RetainExplicitPairAsync(rig, new SyncPairEndpoints(
                LocalEndpoint(rig, "gdn10c"), SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive,
                    drive.ConnectionId, drive.EndpointNamespaceId, drive.FolderId, drive.FolderPath),
                SyncMode.TwoWay), "GDN 10c pair");
            var context = SyncEndpointContext.Capture(pair);
            var before = Snapshot(rig);
            var fired = 0;
            rig.Wire.OnDriveNodeReadOnceAsync = async () =>
            {
                fired++;
                // A consistent change of the local root identity: the pair's own field and the persisted
                // endpoint record move together, so the pair still normalizes and only the caller's captured
                // local identity becomes stale.
                var changedFileId = pair.LeftEndpoint!.LocalRootFileId!.Value + 1;
                var changed = pair with
                {
                    LocalRootFileId = changedFileId,
                    LeftEndpoint = pair.LeftEndpoint with { LocalRootFileId = changedFileId }
                };
                await rig.Db.RetainPairDefinitionAsync(changed, select: true);
            };
            var refusal = await Refused<InvalidOperationException>(() =>
                    rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Right, context),
                "GDN-10c a local root identity changing during the read refuses");
            Require(fired == 1, "GDN-10c the one-shot during-read callback actually fired exactly once");
            Require(rig.Wire.DriveNodeReadRequests > before.NodeReads,
                "GDN-10c provider metadata was accessed before the refusal");
            Require(refusal.Message.Contains("changed while its Google location was checked",
                    StringComparison.Ordinal),
                "GDN-10c the refusal is the ordered-context comparison's own message");
            RequireNoMutation(rig, before, "GDN-10c");
        }
    }

    private static async Task NameOnlyRenameSucceedsAsync()
    {
        Claim("GDN-11-name-only-rename",
            "A name-only rename during the awaited read still succeeds, proving the captured display name is not authority");
        var drive = CloudPairHostBindingChecks.HostDrive("gdn11", "/DriveRight");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("gdn-11-name-only-rename");
        await rig.AddEndpointAsync(drive);
        var url = GiveDriveFolderWebLocation(rig, drive);
        var pair = await RetainExplicitPairAsync(rig, new SyncPairEndpoints(
            LocalEndpoint(rig, "gdn11"), SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive,
                drive.ConnectionId, drive.EndpointNamespaceId, drive.FolderId, drive.FolderPath),
            SyncMode.TwoWay), "GDN 11 original name");
        var context = SyncEndpointContext.Capture(pair);
        var before = Snapshot(rig);
        var fired = 0;
        rig.Wire.OnDriveNodeReadOnceAsync = async () =>
        {
            fired++;
            await rig.Db.RetainPairDefinitionAsync(pair with { Name = "GDN 11 renamed during the read" },
                select: true);
        };
        var location = await rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Right,
            context);
        Require(fired == 1, "GDN-11 the one-shot during-read rename actually fired exactly once");
        var renamed = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
        Require(renamed.Name == "GDN 11 renamed during the read" &&
                SyncEndpointContext.Capture(renamed).CapturedName != context.CapturedName,
            "GDN-11 the pair's captured display name really changed during the awaited read");
        Require(location.WebViewLink == url && location.FileId == drive.FolderId &&
                location.ConnectionId == drive.ConnectionId && location.RootFolderId == drive.FolderId,
            "GDN-11 the name-only change still resolved the exact native URL, item, root and account");
        Require(rig.Wire.DriveNodeReadRequests > before.NodeReads,
            "GDN-11 the successful resolution read provider metadata over the bounded transport");
        RequireNoMutation(rig, before, "GDN-11");
    }

    private static async Task NestedFileRelativePathAsync()
    {
        Claim("GDN-12-nested-file-relative-path",
            "One non-root nested file path through the new method returns the exact provider-native file URL, id, account and root");
        var root = CloudPairHostBindingChecks.HostDrive("gdn12-root", "/DriveRoot");
        var other = CloudPairHostBindingChecks.HostDrive("gdn12-other", "/DriveOther");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("gdn-12-nested-file");
        await rig.AddEndpointAsync(root);
        await rig.AddEndpointAsync(other);
        GiveDriveFolderWebLocation(rig, root);
        GiveDriveFolderWebLocation(rig, other);
        var binding = rig.DriveClient(root).Connection;
        rig.Wire.AddDriveFolder(binding, root.FolderPath + "/Sub", "gd-folder-gdn12-sub");
        var providerPath = root.FolderPath.TrimEnd('/') + "/Sub/file.txt";
        // The file's own identity is derived from its account and path, so re-seeding the same bytes with its
        // own browser location gives one node that carries its exact native identity and URL.
        var seeded = rig.Wire.AddDriveFile(binding, providerPath, "nested file bytes");
        var fileUrl = "https://drive.google.com/file/d/" + seeded.ObjectId + "/view";
        var seededWithUrl = rig.Wire.AddDriveFile(binding, providerPath, "nested file bytes", fileUrl);
        Require(seededWithUrl.ObjectId == seeded.ObjectId && seededWithUrl.Revision == seeded.Revision,
            "GDN-12 re-seeding the same bytes kept the file's own identity and version");
        var pair = await RetainExplicitPairAsync(rig, CloudEndpoints(root, other), "GDN 12 pair");
        var context = SyncEndpointContext.Capture(pair);
        var before = Snapshot(rig);
        var location = await rig.Host.ResolveGoogleDriveEndpointWebLocationAsync(pair.Id, SyncPairSide.Left,
            context, "Sub/file.txt");
        Require(location.WebViewLink == fileUrl && location.FileId == seeded.ObjectId &&
                location.RelativePath == "Sub/file.txt" && location.Kind == GoogleDriveEntryKind.Blob &&
                location.ConnectionId == root.ConnectionId && location.RootFolderId == root.FolderId,
            "GDN-12 the nested file path returned its own exact native file URL, id, account and root");
        Require(rig.Wire.DriveNameLookupRequests > before.NameLookups &&
                rig.Wire.DriveNodeReadRequests > before.NodeReads,
            "GDN-12 the nested path really walked its ancestors and read its own item (name lookups " +
            before.NameLookups + " to " + rig.Wire.DriveNameLookupRequests + ")");
        RequireNoMutation(rig, before, "GDN-12");
    }
}
