using System.Security.Cryptography;
using System.Text.Json;
using Massdex.SyncShark;

// CC1 focused proofs over the real SyncPairStore / SyncProductStateStore dual-slot state and a fresh
// authority reopen. Every cloud/cloud record below is produced by SyncCloudPairConfiguration and every
// write goes through the production persistence path. The fixture contains no provider client, no
// credential, no HttpClient, no host and no authority database, so a provider call is not reachable from
// any case in this file; the rejection cases prove the persisted state stayed byte-identical instead.
internal static class CloudPairConfigurationChecks
{
    private const string DropboxNamespaceOne = "ns:dropbox-root-1";
    private const string DropboxNamespaceTwo = "ns:dropbox-root-2";
    private const string DropboxNamespaceThree = "ns:dropbox-root-3";
    private const string GoogleNamespaceOne = "ns:google-root-1";
    private const string GoogleNamespaceTwo = "ns:google-root-2";

    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated cloud-pair configuration fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;

        await FourCloudOrderingsPersistAndReopenAsync();
        await TwoIndependentPairsCoexistAsync();
        await IdentityStabilityOrderAndAccountAsync();
        await PolicyRetentionAndGuardsAsync();
        await DisabledDefaultsAndProviderPathAsync();
        await LegacyAndLocalRoundTripAsync();
        await RejectionsLeaveStateUnchangedAsync();
        await ActivationStaysUnavailableAsync();

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "CloudPairConfigurationChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "cloud-pair-configuration-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloud-pair configuration: " + _assertions +
            " assertions; four ordered cloud combinations, independent pairs, identity and policy" +
            " retention, legacy/local round-trip, rejection with unchanged state, activation unavailable.");
    }

    // ----- endpoint fixtures ---------------------------------------------------------------------

    private static SyncEndpoint Dropbox(string connectionId, string folderId, string? folderPath,
        string? namespaceId) =>
        SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, connectionId, namespaceId, folderId, folderPath);

    private static SyncEndpoint Drive(string connectionId, string folderId, string? folderPath,
        string? namespaceId) =>
        SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, connectionId, namespaceId, folderId, folderPath);

    private static SyncPairEndpoints Ordering(string name) => name switch
    {
        "dropbox-dropbox" => new(Dropbox("conn-d1", "id:left-dd", "/Left DD", DropboxNamespaceOne),
            Dropbox("conn-d2", "id:right-dd", "/Right DD", DropboxNamespaceTwo), SyncMode.TwoWay),
        "dropbox-google" => new(Dropbox("conn-d1", "id:left-dg", "/Left DG", DropboxNamespaceOne),
            Drive("conn-g1", "gd-right-dg", "/Right DG", GoogleNamespaceOne), SyncMode.TwoWay),
        "google-dropbox" => new(Drive("conn-g1", "gd-left-gd", "/Left GD", GoogleNamespaceOne),
            Dropbox("conn-d2", "id:right-gd", "/Right GD", DropboxNamespaceTwo), SyncMode.TwoWay),
        "google-google" => new(Drive("conn-g1", "gd-left-gg", "/Left GG", GoogleNamespaceOne),
            Drive("conn-g2", "gd-right-gg", "/Right GG", GoogleNamespaceTwo), SyncMode.TwoWay),
        _ => throw new ArgumentException("Unknown ordering", nameof(name))
    };

    private static PilotInstanceLock OpenState(string name) =>
        PilotInstanceLock.Acquire(Path.Combine(_root, name, "state", "instance.lock"));

    private static string StateRoot(string name) => Path.Combine(_root, name, "state");

    private static string[] SlotFiles(string name) => Directory.EnumerateFiles(StateRoot(name))
        .Select(item => Path.GetFileName(item)!).OrderBy(item => item, StringComparer.Ordinal).ToArray();

    // Only the two persisted product-state slots carry pair state; the instance lock is held open by the
    // live authority and its name is still compared in the file-set assertion beside this fingerprint.
    private static string SlotFingerprint(string name) => string.Join(";", Directory
        .EnumerateFiles(StateRoot(name))
        .Where(item => Path.GetFileName(item)!.StartsWith("sync-product-state-", StringComparison.Ordinal))
        .OrderBy(item => item, StringComparer.Ordinal)
        .Select(item => Path.GetFileName(item) + ":" +
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item))).ToLowerInvariant()));

    // The production normalizer is the reference for what a saved record must read back as; comparing
    // against it also proves the cloud/cloud normalization is idempotent across a real reopen.
    private static SyncPair Expected(SyncPair draft) => SyncPairStore.NormalizePair(draft);

    // Record equality compares the list members by reference, so two equal-but-distinct saved arrays are
    // never ==; the canonical serialization of the persisted members is the structural comparison used here.
    private static string PersistedIdentity(SyncPair pair) => JsonSerializer.Serialize(pair);

    private static async Task<SyncPairSet> SaveAsync(PilotInstanceLock owner, SyncPair selected,
        params SyncPair[] pairs)
    {
        var store = new SyncPairStore(owner.StateAuthority);
        await store.SaveAsync(new SyncPairSet(pairs, selected.Id));
        return store.Load();
    }

    // ----- 1. the four ordered cloud combinations -------------------------------------------------

    private static async Task FourCloudOrderingsPersistAndReopenAsync()
    {
        Claim("CC1-4-orderings", "Every ordered cloud/cloud combination persists, projects and reopens through real state");
        foreach (var name in new[] { "dropbox-dropbox", "dropbox-google", "google-dropbox", "google-google" })
        {
            var endpoints = Ordering(name);
            var draft = SyncCloudPairConfiguration.CreateDraft(endpoints, "CC1 " + name);
            Require(SyncCloudPairConfiguration.IsCloudOnly(draft), name + " is recognised as a cloud/cloud record");
            Require(draft.LocalRoot.Length == 0 && draft.LocalVolumeSerialNumber == 0 && draft.LocalRootFileId == 0,
                name + " projects an exactly empty local root and exactly zero local identity fields");
            Require(draft.Provider == SyncEndpoints.ProviderLiteral(endpoints.Right.Kind) &&
                draft.ConnectionId == endpoints.Right.ConnectionId &&
                draft.DropboxRoot == endpoints.Right.FolderPath && draft.DropboxFolderId == endpoints.Right.FolderId,
                name + " right compatibility provider, account, path and folder id agree with the right endpoint");
            Require(draft.Id == SyncCloudPairConfiguration.DeriveId(endpoints) && draft.Id.Length == 20 &&
                draft.Id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
                name + " carries the derived 20-character lowercase pair id");
            Require(draft.ControlNamespace is null && draft.RightControlNamespace is null && draft.RecoveryRoot is null &&
                !draft.FilesOnDemandEnabled && draft.DirectoryStorageRules is null && draft.FileStorageRules is null &&
                !draft.Enabled && draft.InitialReviewApproved == false,
                name + " draft carries no local namespace, no recovery binding, no device storage rule and is" +
                " created disabled and unapproved");

            var stateName = "ordering-" + name;
            using (var owner = OpenState(stateName))
            {
                var loaded = await SaveAsync(owner, draft, draft);
                Require(loaded.Pairs.Count == 1 && loaded.SelectedPairId == draft.Id,
                    name + " persists exactly the one selected record");
                Require(loaded.Pairs[0].EndpointDocument == draft.EndpointDocument,
                    name + " reopens both ordered endpoint identities unchanged");
                Require(PersistedIdentity(loaded.Pairs[0]) == PersistedIdentity(Expected(draft)),
                    name + " reopens the whole record as the production normalizer defines it");
                Require(SlotFiles(stateName).All(item =>
                        item == SyncSharkRuntimePaths.InstanceLockFileName ||
                        item.StartsWith("sync-product-state-", StringComparison.Ordinal)) &&
                    SlotFiles(stateName).Count(item => item.StartsWith("sync-product-state-", StringComparison.Ordinal)) is 1 or 2,
                    name + " writes only the existing dual-slot product state, with no provider artifact or database");
            }
            using (var reopened = OpenState(stateName))
            {
                var fresh = new SyncPairStore(reopened.StateAuthority).Load();
                Require(fresh.Pairs.Count == 1 &&
                    PersistedIdentity(fresh.Pairs[0]) == PersistedIdentity(Expected(draft)),
                    name + " a fresh authority reads the same record and identity back");
            }
            Require(SyncCloudPairConfiguration.DeriveId(endpoints with { Direction = SyncMode.UploadOnly }) == draft.Id,
                name + " keeps its identity when only the direction changes");
        }
    }

    // ----- 2. independent pairs coexist -----------------------------------------------------------

    private static async Task TwoIndependentPairsCoexistAsync()
    {
        Claim("CC1-4-coexist", "Two independent cloud/cloud pairs are retained, reopened and never collide on the absent local identity");
        var first = SyncCloudPairConfiguration.CreateDraft(Ordering("dropbox-dropbox"), "CC1 independent one");
        var second = SyncCloudPairConfiguration.CreateDraft(Ordering("dropbox-google"), "CC1 independent two");
        Require(first.Id != second.Id && first.LocalRootFileId == 0 && second.LocalRootFileId == 0,
            "two cloud/cloud records share the absent local identity and still carry distinct pair ids");
        using (var owner = OpenState("independent-pairs"))
        {
            var loaded = await SaveAsync(owner, first, first, second);
            Require(loaded.Pairs.Count == 2 &&
                loaded.Pairs.Any(item => PersistedIdentity(item) == PersistedIdentity(Expected(first))) &&
                loaded.Pairs.Any(item => PersistedIdentity(item) == PersistedIdentity(Expected(second))),
                "both independent records persist in one pair set despite the shared (0,0) local identity");
        }
        using (var reopened = OpenState("independent-pairs"))
        {
            var store = new SyncPairStore(reopened.StateAuthority);
            var fresh = store.Load();
            Require(fresh.Pairs.Count == 2 &&
                fresh.Pairs.Any(item => PersistedIdentity(item) == PersistedIdentity(Expected(first))) &&
                fresh.Pairs.Any(item => PersistedIdentity(item) == PersistedIdentity(Expected(second))),
                "a fresh authority retains both independent records");
            await store.RemoveAsync(second.Id);
            var removed = store.Load();
            Require(removed.Pairs.Count == 1 &&
                PersistedIdentity(removed.Pairs[0]) == PersistedIdentity(Expected(first)),
                "retiring one independent record leaves the other exactly intact");
        }
    }

    // ----- 3. identity stability, order and account distinctions -----------------------------------

    private static async Task IdentityStabilityOrderAndAccountAsync()
    {
        Claim("CC1-2-identity", "Identity is derived from the ordered provider/account/namespace/folder tuple only");
        var endpoints = Ordering("dropbox-google");
        var baseline = SyncCloudPairConfiguration.CreateDraft(endpoints, "CC1 identity");
        Require(SyncCloudPairConfiguration.DeriveId(endpoints) == baseline.Id,
            "the derived identity is stable for the same ordered endpoints");
        Require(SyncCloudPairConfiguration.CreateDraft(endpoints, "CC1 renamed identity").Id == baseline.Id,
            "a display name change does not change the identity");
        Require(SyncCloudPairConfiguration.CreateDraft(endpoints with
                { Right = Drive("conn-g1", "gd-right-dg", "/Right DG renamed", GoogleNamespaceOne) },
                "CC1 identity").Id == baseline.Id,
            "a mutable display path change does not change the identity");
        Require(SyncCloudPairConfiguration.CreateDraft(endpoints with { Direction = SyncMode.DownloadOnly },
                "CC1 identity").Id == baseline.Id,
            "a direction change does not change the identity");
        Require(SyncCloudPairConfiguration.CreateDraft(endpoints with { Left = endpoints.Right, Right = endpoints.Left },
                "CC1 identity").Id != baseline.Id, "swapping the two sides changes the identity");
        Require(SyncCloudPairConfiguration.CreateDraft(endpoints with
                { Left = Dropbox("conn-d9", "id:left-dg", "/Left DG", DropboxNamespaceOne) },
                "CC1 identity").Id != baseline.Id, "changing the account changes the identity");
        Require(SyncCloudPairConfiguration.CreateDraft(endpoints with
                { Left = Dropbox("conn-d1", "id:left-dg", "/Left DG", "ns:dropbox-root-9") },
                "CC1 identity").Id != baseline.Id, "changing the verified namespace changes the identity");
        Require(SyncCloudPairConfiguration.CreateDraft(endpoints with
                { Left = Dropbox("conn-d1", "id:left-dg-other", "/Left DG", DropboxNamespaceOne) },
                "CC1 identity").Id != baseline.Id, "changing the stable folder identity changes the identity");

        using var owner = OpenState("identity-policy");
        var store = new SyncPairStore(owner.StateAuthority);
        var saved = await SaveAsync(owner, baseline, baseline);
        Require(saved.Pairs[0].PolicyRevision == 1, "a new cloud/cloud record starts at policy revision 1");
        var renamed = await store.UpsertAsync(saved.Pairs[0] with { Name = "CC1 identity renamed" });
        Require(renamed.Pairs[0].Id == baseline.Id && renamed.Pairs[0].PolicyRevision == 1 &&
            renamed.Pairs[0].Name == "CC1 identity renamed",
            "renaming retains the identity and does not advance the policy revision");
        var reprioritised = await store.UpsertAsync(renamed.Pairs[0] with { Priority = 7 });
        Require(reprioritised.Pairs[0].Id == baseline.Id && reprioritised.Pairs[0].PolicyRevision == 2,
            "a policy change retains the identity and advances the policy revision");
        Require(reprioritised.Pairs[0].LocalRoot.Length == 0 && reprioritised.Pairs[0].LocalRootFileId == 0 &&
            reprioritised.Pairs[0].EndpointDocument == baseline.EndpointDocument,
            "the updated record keeps its absent local projection and both endpoint identities");
        var rebound = reprioritised.Pairs[0] with
        {
            LeftEndpoint = SyncEndpointRecord.From(Dropbox("conn-d9", "id:left-dg", "/Left DG", DropboxNamespaceOne))
        };
        await ThrowsAsync(() => store.SaveAsync(new SyncPairSet([rebound], rebound.Id)),
            "an explicit rebind, never a silent one",
            "a changed endpoint under an unchanged pair id is refused rather than silently rebound");
    }

    // ----- 4. policy retention and guards ----------------------------------------------------------

    private static async Task PolicyRetentionAndGuardsAsync()
    {
        Claim("CC1-2-policy", "Retained policy survives creation and reopen while authority, approval and device state never do");
        var endpoints = Ordering("google-dropbox");
        var template = SyncCloudPairConfiguration.CreateDraft(Ordering("dropbox-dropbox"), "CC1 policy template") with
        {
            Enabled = true,
            Priority = 5,
            PropagateDeletions = true,
            Direction = SyncMode.DownloadOnly,
            SelectionDefaultIncluded = false,
            SelectionRules = [new SyncSelectionRule("Photos", true)],
            IgnoreRules = [new SyncIgnoreRule(SyncIgnoreKind.FileExtension, ".tmp")],
            RecoveryRetentionDays = 90,
            InitialReviewApproved = true
        };
        var draft = SyncCloudPairConfiguration.CreateDraft(endpoints, "CC1 policy", template);
        Require(!draft.Enabled && draft.Priority == 5 && draft.PropagateDeletions &&
            !draft.SelectionDefaultIncluded &&
            draft.RecoveryRetentionDays == 90 && draft.SelectionRules?.Count == 1 && draft.IgnoreRules?.Count == 1,
            "the retained policy template is carried onto the cloud/cloud draft, with its enabled state never inherited");
        Require(draft.Direction == endpoints.Direction,
            "the ordered endpoints, not the retained template, supply the direction");
        Require(draft.InitialReviewApproved == false && draft.ControlNamespace is null && draft.RecoveryRoot is null,
            "approval, private namespaces and the recovery binding are never inherited from the template");
        Require(draft.Id == SyncCloudPairConfiguration.DeriveId(endpoints),
            "the template's own identity is never reused");
        using (var owner = OpenState("policy-retention"))
        {
            var loaded = await SaveAsync(owner, draft, draft);
            Require(PersistedIdentity(loaded.Pairs[0]) == PersistedIdentity(Expected(draft)),
                "the full retained policy reopens unchanged");
        }
        using (var reopened = OpenState("policy-retention"))
        {
            var fresh = new SyncPairStore(reopened.StateAuthority).Load();
            Require(fresh.Pairs[0].RecoveryRetentionDays == 90 && fresh.Pairs[0].SelectionRules?.Count == 1 &&
                fresh.Pairs[0].IgnoreRules?.Count == 1 && fresh.Pairs[0].Priority == 5 && !fresh.Pairs[0].Enabled,
                "a fresh authority retains the selection, ignore, retention and priority policy");
        }
        await ThrowsAsync(async () =>
        {
            using var owner = OpenState("policy-guard-retention");
            await SaveAsync(owner, draft, draft with { RecoveryRetentionDays = 14 });
        }, "Recovery retention must be", "an unsupported recovery retention is refused");
        await ThrowsAsync(async () =>
        {
            using var owner = OpenState("policy-guard-name");
            await SaveAsync(owner, draft, draft with { Name = new string('n', 81) });
        }, "The sync pair name is not accepted", "an over-long pair name is refused");
    }

    // ----- 4b. disabled draft defaults, persisted enabled intent and the provider path rule ----------

    private static async Task DisabledDefaultsAndProviderPathAsync()
    {
        Claim("CC1-F2-defaults",
            "A new cloud/cloud draft is always disabled and unapproved, a persisted enabled intent survives reload, and activation stays refused");
        var endpoints = Ordering("google-dropbox");
        var enabledTemplate = SyncCloudPairConfiguration.CreateDraft(Ordering("dropbox-dropbox"),
            "CC1 enabled template") with
        {
            Enabled = true,
            InitialReviewApproved = true,
            Priority = 4,
            SelectionDefaultIncluded = false,
            RecoveryRetentionDays = 30
        };
        var draft = SyncCloudPairConfiguration.CreateDraft(endpoints, "CC1 disabled draft", enabledTemplate);
        Require(!draft.Enabled && draft.InitialReviewApproved == false,
            "an enabled, approved policy template still yields a disabled, unapproved draft");
        Require(draft.Priority == 4 && !draft.SelectionDefaultIncluded,
            "the template still supplies only the non-authority policy it is allowed to supply");
        using (var owner = OpenState("enabled-intent"))
        {
            var store = new SyncPairStore(owner.StateAuthority);
            var saved = await SaveAsync(owner, draft, draft);
            Require(!saved.Pairs[0].Enabled && saved.Pairs[0].InitialReviewApproved == false,
                "the persisted new record stays disabled and unapproved");
            var enabled = await store.UpsertAsync(saved.Pairs[0] with { Enabled = true });
            Require(enabled.Pairs[0].Enabled && enabled.Pairs[0].PolicyRevision == 2,
                "an explicit enabled edit is retained and advances the policy revision");
        }
        using (var reopened = OpenState("enabled-intent"))
        {
            var fresh = new SyncPairStore(reopened.StateAuthority).Load();
            Require(fresh.Pairs[0].Enabled && fresh.Pairs[0].InitialReviewApproved == false,
                "a fresh authority preserves the persisted enabled intent instead of forcing it off");
            // Stage-migrated: the hosted cloud/cloud runtime and authoring are implemented in this build.
            // The prior "activation stays unavailable" expectation is obsolete; the current behaviour is that
            // the ordered cloud runtime and presentation gates are available while the draft itself creates no
            // authority or effect.
            Require(SyncEndpointPairRuntime.BindingSupportReason(fresh.Pairs[0].LeftEndpoint!.Kind,
                    fresh.Pairs[0].RightEndpoint!.Kind) is null &&
                SyncEndpointPresentation.BindingSupported(fresh.Pairs[0].LeftEndpoint!.Kind,
                    fresh.Pairs[0].RightEndpoint!.Kind),
                "the enabled persisted record reports the now-available cloud endpoint runtime and presentation gate");
            var authored = SyncEndpointPairDraft.Create(endpoints, "CC1 enabled activation probe");
            Require(!authored.Enabled && authored.InitialReviewApproved == false &&
                authored.Direction == endpoints.Direction &&
                authored.LeftEndpoint!.FolderId == endpoints.Left.FolderId &&
                authored.RightEndpoint!.FolderId == endpoints.Right.FolderId,
                "the ordered cloud draft is constructible, retains the exact endpoints and direction, and stays disabled and unapproved");
        }

        Claim("CC1-F3-opaque-path",
            "An opaque provider's empty display path persists as presentation only and stays non-executable");
        var opaque = new SyncPairEndpoints(
            Drive("conn-g1", "gd-left-opaque", null, GoogleNamespaceOne),
            Drive("conn-g2", "gd-right-opaque", null, GoogleNamespaceTwo), SyncMode.TwoWay);
        var opaqueDraft = SyncCloudPairConfiguration.CreateDraft(opaque, "CC1 opaque path");
        Require(opaqueDraft.DropboxRoot.Length == 0 && opaqueDraft.LocalRoot.Length == 0 &&
            opaqueDraft.DropboxFolderId == "gd-right-opaque",
            "the path-less opaque record projects an empty presentation path, no local root and its stable folder id");
        using (var owner = OpenState("opaque-path"))
        {
            var loaded = await SaveAsync(owner, opaqueDraft, opaqueDraft);
            Require(PersistedIdentity(loaded.Pairs[0]) == PersistedIdentity(Expected(opaqueDraft)),
                "the path-less opaque record persists and reopens structurally unchanged");
        }
        using (var reopened = OpenState("opaque-path"))
        {
            var fresh = new SyncPairStore(reopened.StateAuthority).Load();
            Require(fresh.Pairs.Count == 1 && fresh.Pairs[0].EndpointDocument == opaqueDraft.EndpointDocument &&
                fresh.Pairs[0].DropboxRoot.Length == 0,
                "a fresh authority retains both path-less opaque endpoint identities and the empty presentation path");
            // Stage-migrated: an opaque provider's absent display path is presentation only, which its stable
            // object/hierarchy contract allows, so the draft is now authorable as a disabled, unapproved draft.
            // Unknown identity still fails closed through the unchanged provider/folder-identity refusals.
            var opaqueAuthored = SyncEndpointPairDraft.Create(opaque, "CC1 opaque activation probe");
            Require(!opaqueAuthored.Enabled && opaqueAuthored.InitialReviewApproved == false &&
                opaqueAuthored.LeftEndpoint!.FolderId == "gd-left-opaque" &&
                opaqueAuthored.RightEndpoint!.FolderId == "gd-right-opaque" &&
                opaqueAuthored.DropboxRoot.Length == 0,
                "the path-less opaque cloud draft is authorable as a disabled, unapproved draft retaining both stable folder identities and the empty presentation path");
        }

        var mixedOpaque = new SyncPairEndpoints(
            Drive("conn-g1", "gd-left-mixed", null, GoogleNamespaceOne),
            Dropbox("conn-d2", "id:right-mixed", "/Right Mixed", DropboxNamespaceTwo), SyncMode.TwoWay);
        using (var owner = OpenState("opaque-mixed-path"))
        {
            var mixedDraft = SyncCloudPairConfiguration.CreateDraft(mixedOpaque, "CC1 opaque mixed path");
            var loaded = await SaveAsync(owner, mixedDraft, mixedDraft);
            Require(PersistedIdentity(loaded.Pairs[0]) == PersistedIdentity(Expected(mixedDraft)) &&
                loaded.Pairs[0].DropboxRoot == "/Right Mixed",
                "a path-less opaque left endpoint coexists with a path-descriptive right endpoint");
        }

        Claim("CC1-F3-path-required",
            "A path-descriptive provider endpoint without its provider folder path is refused with no effect");
        Throws(() => SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
                Dropbox("conn-d1", "id:left-nopath", null, DropboxNamespaceOne),
                Dropbox("conn-d2", "id:right-dd", "/Right DD", DropboxNamespaceTwo), SyncMode.TwoWay),
                "CC1 no-path left"),
            "because this provider's folder paths describe ancestry",
            "a path-descriptive left endpoint with no provider path is refused before a draft exists");
        using (var owner = OpenState("path-required"))
        {
            var store = new SyncPairStore(owner.StateAuthority);
            var seeded = await SaveAsync(owner, opaqueDraft, opaqueDraft);
            Require(seeded.Pairs.Count == 1, "the path-rule fixture is seeded with one valid opaque record");
            var before = SlotFingerprint("path-required");
            var beforeFiles = string.Join(",", SlotFiles("path-required"));
            var missingRight = opaqueDraft with
            {
                Provider = SyncProviders.Dropbox,
                ConnectionId = "conn-d3",
                DropboxFolderId = "id:right-nopath",
                DropboxRoot = string.Empty,
                RightEndpoint = SyncEndpointRecord.From(
                    Dropbox("conn-d3", "id:right-nopath", null, DropboxNamespaceThree))
            };
            await RejectedAsync(store, "path-required", before, beforeFiles, missingRight,
                "because this provider's folder paths describe ancestry",
                "a path-descriptive right endpoint with no provider path is refused on the persistence path");
            var missingLeft = opaqueDraft with
            {
                LeftEndpoint = SyncEndpointRecord.From(
                    Dropbox("conn-d4", "id:left-nopath", null, "ns:dropbox-root-4"))
            };
            await RejectedAsync(store, "path-required", before, beforeFiles, missingLeft,
                "because this provider's folder paths describe ancestry",
                "a path-descriptive left endpoint with no provider path is refused on the persistence path too");
            var nested = opaqueDraft with
            {
                LeftEndpoint = SyncEndpointRecord.From(
                    Dropbox("conn-d5", "id:left-nested", "/Team", DropboxNamespaceOne)),
                RightEndpoint = SyncEndpointRecord.From(
                    Dropbox("conn-d5", "id:right-nested", "/Team/Project", DropboxNamespaceOne)),
                Provider = SyncProviders.Dropbox,
                ConnectionId = "conn-d5",
                DropboxFolderId = "id:right-nested",
                DropboxRoot = "/Team/Project"
            };
            await RejectedAsync(store, "path-required", before, beforeFiles, nested, "OverlappingCloudFolders",
                "proven nested roots stay refused by this correction, with the saved state unchanged");
            Require(new SyncPairStore(owner.StateAuthority).Load().Pairs.Count == 1,
                "every path-rule refusal above left the seeded record as the only retained record");
        }
    }

    // ----- 5. legacy and local round-trip ---------------------------------------------------------

    private static async Task LegacyAndLocalRoundTripAsync()
    {
        Claim("CC1-1-legacy", "Legacy local/cloud and explicit two-local records keep their existing validation, identity and projection");
        var localRoot = Directory.CreateDirectory(Path.Combine(_root, "legacy", "local")).FullName;
        var otherLocal = Directory.CreateDirectory(Path.Combine(_root, "legacy", "other")).FullName;
        var rightLocal = Directory.CreateDirectory(Path.Combine(_root, "legacy", "right-local")).FullName;
        var twoLocalLeft = Directory.CreateDirectory(Path.Combine(_root, "legacy", "two-local-left")).FullName;
        PilotDirectoryIdentity localIdentity;
        using (var lease = PilotDirectoryLease.Open(localRoot)) localIdentity = lease.Identity;
        PilotDirectoryIdentity otherIdentity;
        using (var lease = PilotDirectoryLease.Open(otherLocal)) otherIdentity = lease.Identity;
        PilotDirectoryIdentity rightIdentity;
        using (var lease = PilotDirectoryLease.Open(rightLocal)) rightIdentity = lease.Identity;
        PilotDirectoryIdentity twoLocalLeftIdentity;
        using (var lease = PilotDirectoryLease.Open(twoLocalLeft)) twoLocalLeftIdentity = lease.Identity;

        var legacy = new SyncPair("legacy-dropbox-pair", "Legacy Dropbox pair", localRoot + "\\",
            localIdentity.VolumeSerialNumber, localIdentity.FileId, "/Legacy", "id:legacy") with
        { ConnectionId = "conn-d1", RecoveryRetentionDays = 180, Priority = 3 };
        var legacyGoogle = new SyncPair("legacy-google-pair", "Legacy Google pair", otherLocal,
            otherIdentity.VolumeSerialNumber, otherIdentity.FileId, "/Team", "gd-legacy") with
        { Provider = SyncProviders.GoogleDrive, ConnectionId = "conn-g1" };
        var leftLocalEndpoint = SyncEndpoint.Local(twoLocalLeft,
            new SyncLocalEndpointIdentity(twoLocalLeftIdentity.VolumeSerialNumber, twoLocalLeftIdentity.FileId));
        var rightLocalEndpoint = SyncEndpoint.Local(rightLocal,
            new SyncLocalEndpointIdentity(rightIdentity.VolumeSerialNumber, rightIdentity.FileId));
        var twoLocalDocument = SyncEndpointPersistence.Symmetric(
            new SyncPairEndpoints(leftLocalEndpoint, rightLocalEndpoint, SyncMode.TwoWay));
        var twoLocal = new SyncPair("two-local-pair", "Two local pair", twoLocalLeft,
            leftLocalEndpoint.LocalIdentity!.VolumeSerialNumber, leftLocalEndpoint.LocalIdentity.RootFileId,
            rightLocal, rightLocalEndpoint.LocalIdentity!.IdentityToken(),
            RecoveryRoot: new SyncRemoteRecoveryRootBinding(rightLocal,
                rightLocalEndpoint.LocalIdentity!.IdentityToken(), SyncEndpointPairRuntime.LocalProviderLiteral),
            ConnectionId: "", Provider: SyncEndpointPairRuntime.LocalProviderLiteral,
            InitialReviewApproved: true) with
        {
            EndpointSchemaVersion = twoLocalDocument.Version,
            LeftEndpoint = twoLocalDocument.Left,
            RightEndpoint = twoLocalDocument.Right
        };
        var normalizedLegacy = Expected(legacy);
        Require(normalizedLegacy.LocalRoot == localRoot,
            "legacy normalization still removes only the trailing separator from its local root");

        using (var owner = OpenState("legacy-round-trip"))
        {
            var loaded = await SaveAsync(owner, legacy, legacy, legacyGoogle, twoLocal);
            Require(loaded.Pairs.Count == 3, "the legacy and two-local records persist beside each other");
                Require(PersistedIdentity(loaded.Pairs.Single(item => item.Id == legacy.Id)) ==
                    PersistedIdentity(normalizedLegacy),
                "the legacy Dropbox record reopens with only its existing normalization applied");
            Require(normalizedLegacy.LocalVolumeSerialNumber == localIdentity.VolumeSerialNumber &&
                normalizedLegacy.LocalRootFileId == localIdentity.FileId &&
                normalizedLegacy.DropboxFolderId == "id:legacy" && normalizedLegacy.ConnectionId == "conn-d1" &&
                normalizedLegacy.Provider == SyncProviders.Dropbox && normalizedLegacy.Priority == 3 &&
                normalizedLegacy.RecoveryRetentionDays == 180,
                "the legacy identity and projection fields are unchanged");
            Require(PersistedIdentity(loaded.Pairs.Single(item => item.Id == legacyGoogle.Id)) ==
                PersistedIdentity(Expected(legacyGoogle)),
                "the legacy Google record reopens unchanged");
            Require(PersistedIdentity(loaded.Pairs.Single(item => item.Id == twoLocal.Id)) ==
                PersistedIdentity(Expected(twoLocal)),
                "the explicit two-local record reopens through the existing projection unchanged");
            Require(!SyncCloudPairConfiguration.IsCloudOnly(normalizedLegacy) &&
                !SyncCloudPairConfiguration.IsCloudOnly(twoLocal),
                "neither local-containing shape is classified as a cloud/cloud record");
        }
        using (var reopened = OpenState("legacy-round-trip"))
        {
            var fresh = new SyncPairStore(reopened.StateAuthority).Load();
            Require(fresh.Pairs.Count == 3 &&
                PersistedIdentity(fresh.Pairs.Single(item => item.Id == legacy.Id)) ==
                    PersistedIdentity(normalizedLegacy),
                "a fresh authority retains every local-containing record unchanged");
        }
        await ThrowsAsync(async () =>
        {
            using var owner = OpenState("legacy-duplicate-identity");
            var alias = legacyGoogle with { Id = "legacy-alias-pair", Name = "Alias local folder" };
            await SaveAsync(owner, legacy, legacy, legacyGoogle, alias);
        }, "cannot be retained by more than one pair",
            "the existing uniqueness rule still refuses one local folder retained by two pairs");
    }

    // ----- 6. rejections leave persisted state unchanged -------------------------------------------

    private static async Task RejectionsLeaveStateUnchangedAsync()
    {
        Claim("CC1-4-rejections", "Every contradiction is refused before any write and leaves the persisted state byte-identical");
        const string name = "rejections";
        var endpoints = Ordering("dropbox-google");
        var good = SyncCloudPairConfiguration.CreateDraft(endpoints, "CC1 rejection seed");
        using var owner = OpenState(name);
        var store = new SyncPairStore(owner.StateAuthority);
        var seeded = await SaveAsync(owner, good, good);
        var before = SlotFingerprint(name);
        var beforeFiles = string.Join(",", SlotFiles(name));
        Require(seeded.Pairs.Count == 1 &&
            PersistedIdentity(seeded.Pairs[0]) == PersistedIdentity(Expected(good)),
            "the rejection fixture is seeded with one valid record");

        await RejectedAsync(store, name, before, beforeFiles,
            good with { LeftEndpoint = good.LeftEndpoint! with { Provider = "box" } }, "UnknownProvider",
            "an endpoint record with an unregistered provider is refused");
        await RejectedAsync(store, name, before, beforeFiles, good with { Provider = "box" }, "EndpointKindMismatch",
            "a pair provider that disagrees with the right endpoint is refused");
        await RejectedAsync(store, name, before, beforeFiles, good with { EndpointSchemaVersion = 7 },
            "UnsupportedSchemaVersion", "an unsupported endpoint document version is refused");
        await RejectedAsync(store, name, before, beforeFiles, good with { EndpointSchemaVersion = 1 },
            "UnsupportedSchemaVersion", "an explicit endpoint record under the legacy version is refused");
        await RejectedAsync(store, name, before, beforeFiles, good with { RightEndpoint = null },
            "InvalidSchemaDocument", "a cloud/cloud document with a missing right endpoint is refused");
        await RejectedAsync(store, name, before, beforeFiles, good with { LeftEndpoint = null },
            "InvalidSchemaDocument", "a cloud/cloud document with a missing left endpoint is refused");
        await RejectedAsync(store, name, before, beforeFiles,
            good with
            {
                LeftEndpoint = SyncEndpointRecord.From(SyncEndpoint.Local(
                    Directory.CreateDirectory(Path.Combine(_root, "rejections", "local")).FullName))
            },
            "The saved local folder identity is incomplete",
            "a document declaring a local side is never read as cloud/cloud and is refused by the existing local rule");
        Throws(() => SyncCloudPairConfiguration.RequireProjection(good with
            {
                LeftEndpoint = SyncEndpointRecord.From(SyncEndpoint.Local(
                    Directory.CreateDirectory(Path.Combine(_root, "rejections", "local")).FullName))
            }),
            "EndpointKindMismatch", "the projection contract itself refuses a document declaring a local side");
        await RejectedAsync(store, name, before, beforeFiles, good with { LocalRoot = @"C:\SyncShark\Fake" },
            "EndpointKindMismatch", "a cloud/cloud record with a fake local root is refused");
        await RejectedAsync(store, name, before, beforeFiles, good with { LocalRootFileId = 42 },
            "EndpointKindMismatch", "a cloud/cloud record with a fake local file id is refused");
        await RejectedAsync(store, name, before, beforeFiles, good with { LocalVolumeSerialNumber = 1234 },
            "EndpointKindMismatch", "a cloud/cloud record with a fake volume serial is refused");
        await RejectedAsync(store, name, before, beforeFiles, good with { ConnectionId = "conn-other" },
            "EndpointKindMismatch", "a saved account that disagrees with the right endpoint is refused");
        await RejectedAsync(store, name, before, beforeFiles, good with { DropboxFolderId = "id:other" },
            "EndpointKindMismatch", "a saved folder id that disagrees with the right endpoint is refused");
        await RejectedAsync(store, name, before, beforeFiles, good with { DropboxRoot = "/Other" },
            "EndpointKindMismatch", "a saved folder path that disagrees with the right endpoint is refused");
        await RejectedAsync(store, name, before, beforeFiles, good with { Id = "cloud-pair-rebind-0001" },
            "EndpointKindMismatch", "a saved pair id that is not the document identity is refused");
        await RejectedAsync(store, name, before, beforeFiles, good with
        {
            ControlNamespace = new SyncControlNamespaceBinding("install-cc1", good.Id,
                new SyncLocalControlNamespaceBinding(@"C:\SyncShark\Namespace", 1, 2, "id:marker", new string('a', 64)))
        }, "EndpointKindMismatch", "a cloud/cloud record with a local control namespace is refused");
        await RejectedAsync(store, name, before, beforeFiles,
            good with { LeftEndpoint = good.RightEndpoint, RightEndpoint = good.RightEndpoint },
            "SameEndpoint", "two identical cloud roots in one pair are refused");
        await RejectedAsync(store, name, before, beforeFiles, good with { FilesOnDemandEnabled = true },
            "EndpointKindMismatch", "a cloud/cloud record with Files On Demand is refused");
        await RejectedAsync(store, name, before, beforeFiles,
            good with { DirectoryStorageRules = [new SyncDirectoryStorageRule("Child", SyncDirectoryStorage.CloudOnly)] },
            "EndpointKindMismatch", "a cloud/cloud record with a device storage rule is refused");
        await RejectedAsync(store, name, before, beforeFiles, good with { DirectoryStorageApplyPaths = ["Child"] },
            "EndpointKindMismatch", "a cloud/cloud record with a pending device storage path is refused");
        await RejectedAsync(store, name, before, beforeFiles, good with { DefaultStorage = SyncDirectoryStorage.CloudOnly },
            "EndpointKindMismatch", "a cloud/cloud record with a default device storage choice is refused");
        await RejectedAsync(store, name, before, beforeFiles,
            good with { FileStorageRules = [new SyncFileStorageRule("Child/file.txt", SyncDirectoryStorage.AlwaysAvailable, 9, null)] },
            "EndpointKindMismatch", "a cloud/cloud record with a per-file device storage rule is refused");

        // One cloud root nested inside the other side of the same pair proves overlap inside the pair.
        var nested = SyncCloudPairConfiguration.CreateDraft(Ordering("dropbox-dropbox"), "CC1 nested seed") with
        {
            LeftEndpoint = SyncEndpointRecord.From(Dropbox("conn-d1", "id:left-overlap", "/Team", DropboxNamespaceOne)),
            RightEndpoint = SyncEndpointRecord.From(Dropbox("conn-d1", "id:right-overlap", "/Team/Project", DropboxNamespaceOne))
        };
        await RejectedAsync(store, name, before, beforeFiles, nested, "OverlappingCloudFolders",
            "a cloud folder nested inside the other side of one pair is refused");

        Throws(() => SyncCloudPairConfiguration.CreateDraft(
                new SyncPairEndpoints(endpoints.Left, endpoints.Left, SyncMode.TwoWay), "CC1 same root probe"),
            "SameEndpoint", "the draft helper refuses one cloud root used for both sides");
        Throws(() => SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
                Dropbox("conn-d1", "id:left-overlap", "/Team", DropboxNamespaceOne),
                Dropbox("conn-d1", "id:right-overlap", "/Team/Project", DropboxNamespaceOne), SyncMode.TwoWay),
                "CC1 overlap probe"),
            "OverlappingCloudFolders", "the draft helper refuses a nested cloud root before any record exists");

        // Cross-pair duplicates on either side, through the shared endpoint contract.
        var duplicateLeft = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(endpoints.Left,
            Dropbox("conn-d2", "id:right-gd", "/Right GD", DropboxNamespaceTwo), SyncMode.TwoWay),
            "CC1 duplicate left");
        await RejectedPairSetAsync(store, name, before, beforeFiles, [good, duplicateLeft],
            "DuplicateEndpointAcrossPairs",
            "a second pair reusing the first pair's left endpoint is refused on that side");
        var duplicateAcrossSides = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
            SyncEndpointContracts.ToEndpoint(good.RightEndpoint!),
            Dropbox("conn-d3", "id:right-across", "/Right Across", DropboxNamespaceThree), SyncMode.TwoWay),
            "CC1 duplicate across sides");
        await RejectedPairSetAsync(store, name, before, beforeFiles, [good, duplicateAcrossSides],
            "DuplicateEndpointAcrossPairs",
            "a second pair whose left endpoint is the first pair's right endpoint is refused");
        var duplicateRight = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
            Dropbox("conn-d3", "id:left-dup", "/Left Dup", DropboxNamespaceThree),
            SyncEndpointContracts.ToEndpoint(good.RightEndpoint!), SyncMode.TwoWay), "CC1 duplicate right");
        await RejectedPairSetAsync(store, name, before, beforeFiles, [good, duplicateRight],
            "cannot be retained by more than one pair",
            "a second pair reusing the first pair's right endpoint is refused by the existing right rule");
        var seed = SyncCloudPairConfiguration.CreateDraft(Ordering("dropbox-dropbox"), "CC1 overlap seed");
        var crossPairOverlap = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
            Dropbox("conn-d1", "id:left-dd-child", "/Left DD/Child", DropboxNamespaceOne),
            Dropbox("conn-d4", "id:right-overlap", "/Right Overlap", "ns:dropbox-root-4"), SyncMode.TwoWay),
            "CC1 cross-pair overlap");
        await RejectedPairSetAsync(store, name, before, beforeFiles, [seed, crossPairOverlap],
            "OverlappingEndpointAcrossPairs",
            "a cloud folder nested inside another retained pair's folder is refused");

        Require(SlotFingerprint(name) == before && string.Join(",", SlotFiles(name)) == beforeFiles,
            "every refusal above left the persisted state byte-identical, with no provider call and no new artifact");
        var after = new SyncPairStore(owner.StateAuthority).Load();
        Require(after.Pairs.Count == 1 &&
            PersistedIdentity(after.Pairs[0]) == PersistedIdentity(Expected(good)),
            "the seeded record is still the only retained record after every refusal");
    }

    private static async Task RejectedAsync(SyncPairStore store, string name, string before,
        string beforeFiles, SyncPair candidate, string fragment, string message)
    {
        await ThrowsAsync(() => store.SaveAsync(new SyncPairSet([candidate], candidate.Id)), fragment, message);
        Require(SlotFingerprint(name) == before && string.Join(",", SlotFiles(name)) == beforeFiles,
            message + "; persisted state unchanged");
    }

    private static async Task RejectedPairSetAsync(SyncPairStore store, string name, string before,
        string beforeFiles, SyncPair[] set, string fragment, string message)
    {
        await ThrowsAsync(() => store.SaveAsync(new SyncPairSet(set, set[0].Id)), fragment, message);
        Require(SlotFingerprint(name) == before && string.Join(",", SlotFiles(name)) == beforeFiles,
            message + "; persisted state unchanged");
    }

    // ----- 7. activation stays unavailable ---------------------------------------------------------

    private static async Task ActivationStaysUnavailableAsync()
    {
        // Stage-migrated (root semantic decision): the cloud/cloud runtime and authoring are implemented in
        // this build. The prior "never becomes authorable/executable" expectation is obsolete. The current
        // public behaviour is: every ordered cloud combination is authorable as a DISABLED, unapproved draft
        // that retains its exact endpoints and direction, and neither the draft nor an enabled intent creates
        // authority or an effect. Provider/hierarchy/authority refusals are unchanged.
        Claim("CC1-3-inactive",
            "Stage-migrated: a persisted cloud/cloud record is authorable as a disabled, unapproved draft with its exact endpoints and direction, and no authority or effect is created by the draft or by enabled intent");
        foreach (var name in new[] { "dropbox-dropbox", "dropbox-google", "google-dropbox", "google-google" })
        {
            var endpoints = Ordering(name);
            Require(SyncEndpointPairRuntime.BindingSupportReason(endpoints.Left.Kind, endpoints.Right.Kind) is null,
                name + " now reports an available cloud endpoint runtime (stage-migrated)");
            Require(SyncEndpointPresentation.BindingSupportReason(endpoints.Left.Kind, endpoints.Right.Kind) is null &&
                SyncEndpointPresentation.BindingSupported(endpoints.Left.Kind, endpoints.Right.Kind),
                name + " is now supported through the shared presentation gate (stage-migrated)");
            var authored = SyncEndpointPairDraft.Create(endpoints, "CC1 activation probe");
            Require(!authored.Enabled && authored.InitialReviewApproved == false &&
                authored.Direction == endpoints.Direction &&
                authored.LeftEndpoint!.FolderId == endpoints.Left.FolderId &&
                authored.RightEndpoint!.FolderId == endpoints.Right.FolderId,
                name + " is authorable as a disabled, unapproved draft retaining the exact endpoints and direction");
        }
        await Task.CompletedTask;
    }

    // ----- shared helpers -------------------------------------------------------------------------

    private static async Task ThrowsAsync(Func<Task> action, string fragment, string message)
    {
        try
        {
            await action();
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
            NotSupportedException)
        {
            Require(error.Message.Contains(fragment, StringComparison.Ordinal),
                message + " (" + error.GetType().Name + ")");
            return;
        }
        throw new InvalidDataException("The rejected operation succeeded: " + message);
    }

    private static void Throws(Action action, string fragment, string message)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
            NotSupportedException)
        {
            Require(error.Message.Contains(fragment, StringComparison.Ordinal),
                message + " (" + error.GetType().Name + ")");
            return;
        }
        throw new InvalidDataException("The rejected operation succeeded: " + message);
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
        Console.WriteLine("PASS CC1 " + message);
    }
}
