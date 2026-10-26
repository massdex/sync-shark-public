using System.Globalization;
using System.IO;
using Massdex.SyncShark;
using IOPath = System.IO.Path;

// R22-3 proof: the real typed cloud root registration (the guarded consumer) admits an excluded nested cloud
// candidate through the shared partition policy and refuses an unexcluded, a foreign and a same-revision
// changed-policy candidate, leaving the retained authority rows unchanged. The registration writes into a real
// isolated SyncAuthorityDatabase; only the provider hierarchy input is synthetic (permitted as input).
internal static class ExcludedNestedCloudTypedAdmissionChecks
{
    private const string Installation = "installation-nested-cloud-431";
    private const string SourceRevision = "nested-cloud-431";
    private const string ProfileId = "profile-nested-cloud-431";
    private const string CapabilityJson = "{\"name\":\"nested-cloud-431\"}";
    private const string AccountId = "account-nested-cloud-431";
    private const string RootNs = "ns-nested-cloud-431";
    private const string HomeNs = "ns-nested-cloud-home-431";
    private const string Provider = "dropbox";
    private const string ChildSegment = "C2";
    private const string ParentName = "P";

    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = IOPath.GetFullPath(root);
        Directory.CreateDirectory(_root);
        _assertions = 0;

        var parent = CloudPair("Cloud parent pair", IOPath.Combine(_root, "p", "L"), "id:cloud-parent", "/P", []);
        var child = CloudPair("Cloud child pair", IOPath.Combine(_root, "c", "L"), "id:cloud-child", "/P/C2", []);
        var parentExcludingChild = parent with
        {
            SelectionRules = new[] { new SyncSelectionRule(ChildSegment, false) },
            SelectionDefaultIncluded = true
        };

        // SUCCESS: the enclosing parent excludes the whole child subtree, so the nested candidate is admitted.
        var admitted = await RunRegistrationAsync("admitted", parentExcludingChild, child, child, expectRefusal: false);
        Require(admitted.Refused is null,
            "ECN-01 R22-3 the typed cloud registration admits the excluded nested candidate");
        Require(await CountAsync(admitted.DatabasePath, "SELECT count(*) FROM root_binding WHERE root_binding_id='" +
                admitted.CandidateRootBindingId + "' AND state='active'") == 1,
            "ECN-01 the admitted candidate root binding is active");

        // REJECTION 1: the enclosing parent does not exclude the child subtree.
        var unexcluded = await RunRegistrationAsync("unexcluded", parent, child, child, expectRefusal: true);
        Require(unexcluded.Refused is not null,
            "ECN-02 R22-3 the typed cloud registration refuses an unexcluded nested candidate: " +
            (unexcluded.Refused ?? "accepted"));
        Require(await CountAsync(unexcluded.DatabasePath, "SELECT count(*) FROM root_binding WHERE root_binding_id='" +
                unexcluded.CandidateRootBindingId + "'") == 0,
            "ECN-02 the refused candidate wrote no root binding");

        // REJECTION 2: a FOREIGN candidate pair (different id) cannot waive the same relation.
        var foreign = await RunRegistrationAsync("foreign", parentExcludingChild, child, parentExcludingChild,
            expectRefusal: true);
        Require(foreign.Refused is not null,
            "ECN-03 R22-3 a foreign candidate pair cannot waive the overlap: " + (foreign.Refused ?? "accepted"));
        Require(await CountAsync(foreign.DatabasePath, "SELECT count(*) FROM root_binding WHERE root_binding_id='" +
                foreign.CandidateRootBindingId + "'") == 0,
            "ECN-03 the foreign-candidate refusal wrote no root binding");

        // REJECTION 3: the exact same id and revision but a different policy (propagate deletions) is refused.
        var changedPolicy = child with { PropagateDeletions = true };
        var changed = await RunRegistrationAsync("changed-policy", parentExcludingChild, child, changedPolicy,
            expectRefusal: true);
        Require(changed.Refused is not null,
            "ECN-04 R22-3 a same-id/same-revision candidate with a different policy is refused: " +
            (changed.Refused ?? "accepted"));
        Require(await CountAsync(changed.DatabasePath, "SELECT count(*) FROM root_binding WHERE root_binding_id='" +
                changed.CandidateRootBindingId + "'") == 0,
            "ECN-04 the changed-policy refusal wrote no root binding");

        // REJECTION 4: a PUBLISHED candidate pair_json whose exclusion differs from the carried candidate.
        var publishedOverride = child with
        {
            SelectionRules = new[] { new SyncSelectionRule(ChildSegment + "Other", false) },
            SelectionDefaultIncluded = true
        };
        var publishedChanged = await RunRegistrationAsync("published-changed-exclusion", parentExcludingChild,
            child, child, expectRefusal: true, publishedOverride: publishedOverride);
        Require(publishedChanged.Refused is not null,
            "ECN-05 R22-3 a published definition whose exclusion differs from the carried candidate is refused: " +
            (publishedChanged.Refused ?? "accepted"));
        Require(await CountAsync(publishedChanged.DatabasePath,
                "SELECT count(*) FROM root_binding WHERE root_binding_id='" +
                publishedChanged.CandidateRootBindingId + "'") == 0,
            "ECN-05 the published-changed-exclusion refusal wrote no root binding");

        // REJECTION 5: the strict PUBLIC/raw nested registration (no candidate) refuses the same overlap.
        var raw = await RunRegistrationAsync("raw-public", parentExcludingChild, child, child, expectRefusal: true,
            useRaw: true);
        Require(raw.Refused is not null,
            "ECN-06 R22-3 the strict public/raw nested cloud registration refuses the overlap: " +
            (raw.Refused ?? "accepted"));
        Require(await CountAsync(raw.DatabasePath, "SELECT count(*) FROM root_binding WHERE root_binding_id='" +
                raw.CandidateRootBindingId + "'") == 0,
            "ECN-06 the raw refusal wrote no root binding");

        // Exercise the same real transaction for the opaque provider and reversed ordered endpoints.
        // Provider readbacks are inputs only: the production guard resolves each identity from its own DB row.
        foreach (var reversed in new[] { false, true })
        {
            var googleParent = CloudPair("Opaque parent " + reversed, IOPath.Combine(_root, "google-" + reversed, "parent"),
                "opaque-parent-431", "/PresentationParent", [new SyncSelectionRule(ChildSegment, false)],
                SyncEndpointKind.GoogleDrive, reversed);
            var googleChild = CloudPair("Opaque child " + reversed, IOPath.Combine(_root, "google-" + reversed, "child"),
                "opaque-child-431", "/UnrelatedPresentation", [], SyncEndpointKind.GoogleDrive, reversed);
            var google = await RunRegistrationAsync("google-admitted-" + reversed, googleParent, googleChild,
                googleChild, expectRefusal: false);
            Require(google.Refused is null && await CountAsync(google.DatabasePath,
                    "SELECT count(*) FROM root_binding WHERE root_binding_id='" + google.CandidateRootBindingId + "' AND state='active'") == 1,
                "ECN-07 opaque observed ancestry admits the excluded subtree through actual typed registration; reversed=" + reversed);
            foreach (var fault in new[] { "names", "incomplete", "missing-retained", "duplicate", "wrong-account" })
            {
                var rejected = await RunRegistrationAsync("google-" + reversed + "-" + fault, googleParent,
                    googleChild, googleChild, expectRefusal: true, proofFault: fault);
                Require(rejected.Refused is not null && await CountAsync(rejected.DatabasePath,
                        "SELECT count(*) FROM root_binding WHERE root_binding_id='" + rejected.CandidateRootBindingId + "'") == 0,
                    "ECN-08 opaque " + fault + " evidence refuses without a candidate authority write; reversed=" + reversed);
            }
        }

        Console.WriteLine("PASS excluded nested cloud typed admission: " + _assertions +
            " assertions; the guarded typed cloud registration admits only the exactly bound, policy-matching," +
            " excluded nested candidate and refuses unexcluded, foreign and same-revision changed-policy candidates" +
            " with no authority row written.");
    }

    private sealed record RegistrationOutcome(string DatabasePath, string CandidateRootBindingId, string? Refused);

    private static async Task<RegistrationOutcome> RunRegistrationAsync(string name, SyncPair retainedParent,
        SyncPair candidatePair, SyncPair candidateDefinition, bool expectRefusal, SyncPair? publishedOverride = null,
        bool useRaw = false, string? proofFault = null)
    {
        var root = IOPath.Combine(_root, name);
        Directory.CreateDirectory(root);
        var databasePath = IOPath.Combine(root, "authority.db");
        var database = new SyncAuthorityDatabase(databasePath);
        await database.InitializeAsync(Installation, ProfileId, SourceRevision, CapabilityJson);
        var retainedCloud = CloudEndpoint(retainedParent);
        var candidateCloud = CloudEndpoint(candidatePair);
        var provider = SyncEndpoints.ProviderLiteral(candidateCloud.Kind)!;
        var namespaceId = candidateCloud.NamespaceId!;
        var providerAccountId = SyncAuthorityProviderAccountIdentity.DeriveId(provider, AccountId, RootNs, HomeNs);
        await database.RegisterProviderAccountAsync(providerAccountId, Installation, AccountId, RootNs, HomeNs,
            "Nested cloud fixture", 1, default, provider);

        var retainedRootBindingId = "rootbinding-cloud-parent-" + Guid.NewGuid().ToString("N");
        var candidateRootBindingId = "rootbinding-cloud-child-" + Guid.NewGuid().ToString("N");

        await database.RegisterPairAsync(retainedParent.Id, retainedParent.PolicyRevision,
            SyncAuthorityPairConfiguration.FromPair(retainedParent));
        await database.RetainPairDefinitionAsync(retainedParent, select: false);
        await database.RegisterCloudEndpointRootBindingAsync(retainedRootBindingId, retainedParent.Id,
            SyncAuthorityCloudSides.LogicalRight, 1, Installation, provider, providerAccountId, namespaceId,
            retainedCloud.FolderId!, retainedCloud.FolderPath!,
            default, new[]
            {
                Snapshot(retainedRootBindingId, providerAccountId,
                    new[] { retainedCloud.FolderId! }, new[] { ParentName }, provider, namespaceId)
            });

        await database.RegisterPairAsync(candidatePair.Id, candidatePair.PolicyRevision,
            SyncAuthorityPairConfiguration.FromPair(candidatePair));
        if (publishedOverride is not null)
            await database.RetainPairDefinitionAsync(publishedOverride, select: false);

        string? refused = null;
        try
        {
            var candidateSnapshots = new[]
            {
                Snapshot(candidateRootBindingId, providerAccountId,
                    new[] { retainedCloud.FolderId!, candidateCloud.FolderId! },
                    new[] { ParentName, ChildSegment }, provider, namespaceId),
                Snapshot(retainedRootBindingId, providerAccountId,
                    new[] { retainedCloud.FolderId! }, new[] { ParentName }, provider, namespaceId)
            };
            candidateSnapshots = proofFault switch
            {
                "names" => [candidateSnapshots[0] with { ChainFolderNames = null }, candidateSnapshots[1]],
                "incomplete" => [candidateSnapshots[0] with { CompleteToCorpusRoot = false }, candidateSnapshots[1]],
                "missing-retained" => [candidateSnapshots[0]],
                "duplicate" => [candidateSnapshots[0], candidateSnapshots[1], candidateSnapshots[0]],
                "wrong-account" => [candidateSnapshots[0] with { ProviderAccountId = "foreign-account-431" }, candidateSnapshots[1]],
                _ => candidateSnapshots
            };
            if (useRaw)
                await database.RegisterCloudEndpointRootBindingAsync(candidateRootBindingId, candidatePair.Id,
                    SyncAuthorityCloudSides.LogicalRight, 1, Installation, provider, providerAccountId, namespaceId,
                    candidateCloud.FolderId!, candidateCloud.FolderPath!, default,
                    candidateSnapshots);
            else
                await database.RegisterCloudEndpointRootBindingForCandidateAsync(candidateRootBindingId,
                    candidatePair.Id, SyncAuthorityCloudSides.LogicalRight, 1, Installation, provider,
                    providerAccountId, namespaceId, candidateCloud.FolderId!,
                    candidateCloud.FolderPath!, candidateDefinition, default, candidateSnapshots);
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            refused = error.Message;
        }
        if (expectRefusal && refused is null)
            throw new InvalidDataException("The registration was expected to refuse " + name + " but it was accepted.");
        return new RegistrationOutcome(databasePath, candidateRootBindingId, refused);
    }

    // A complete provider-verified chain to its corpus root WITH index-aligned observed names, so the
    // transactional typed guard can bind the proof to the exact DB-held identities and derive the relative
    // exclusion subtree from names only (never from a display path).
    private static SyncAuthorityCloudHierarchySnapshot Snapshot(string rootBindingId, string providerAccountId,
        IReadOnlyList<string> chain, IReadOnlyList<string> names, string provider = Provider,
        string namespaceId = RootNs) => new(provider, providerAccountId, namespaceId,
        rootBindingId, chain[^1], chain)
    {
        CompleteToCorpusRoot = true,
        CorpusRootId = chain[0],
        ChainFolderNames = names,
        ObjectKeyCoverage = SyncCloudObjectKeySemantics.RequiredCoverage(provider),
        ChainNodeKeys = chain.Select(id => new SyncAuthorityCloudHierarchyNodeKeys(id,
            [SyncCloudObjectKey.Native(provider, id)])).ToArray()
    };

    private static SyncEndpoint CloudEndpoint(SyncPair pair)
    {
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(pair)!;
        return endpoints.Left.IsLocal ? endpoints.Right : endpoints.Left;
    }

    private static SyncPair CloudPair(string name, string localPath, string folderId, string folderPath,
        IReadOnlyList<SyncSelectionRule> rules, SyncEndpointKind kind = SyncEndpointKind.Dropbox, bool reversed = false)
    {
        Directory.CreateDirectory(localPath);
        using var lease = PilotDirectoryLease.Open(localPath);
        var local = SyncEndpoint.Local(lease.Path,
            new SyncLocalEndpointIdentity(lease.Identity.VolumeSerialNumber, lease.Identity.FileId));
        var cloud = SyncEndpoint.Cloud(kind, "conn-cloud-431", kind == SyncEndpointKind.GoogleDrive ? AccountId : RootNs,
            folderId, folderPath);
        var endpoints = reversed ? new SyncPairEndpoints(cloud, local, SyncMode.TwoWay) :
            new SyncPairEndpoints(local, cloud, SyncMode.TwoWay);
        var template = new SyncPair("template", name, lease.Path, lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, folderPath, folderId, SelectionRules: rules, SelectionDefaultIncluded: true);
        return SyncEndpointPairDraft.Create(endpoints, name, template);
    }

    private static async Task<long> CountAsync(string databasePath, string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        _assertions++;
        Console.WriteLine("PASS " + message);
    }
}
