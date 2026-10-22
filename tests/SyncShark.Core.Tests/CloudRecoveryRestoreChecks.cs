using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Rig = CloudPairHostBindingChecks.HostRig;
using Spec = CloudPairHostBindingChecks.EndpointSpec;

// CC4E-3: the real public Host and CLI manual cloud-restore proofs.
//
// Every case runs the REAL SyncAuthorityRuntimeHost over real isolated SQLite, the real private bounded Windows
// staging allocation and the real Dropbox/Google Drive adapters over their stateful in-process provider
// transports. Nothing is mocked at the decision boundary: authority resolution, the bounded private stage, the
// retained destination preimage, the conditional upload, the durable receipt, the independent readback, the
// narrow commit and the owned cleanup are all the production ones. An assertion claims only what the observed
// run proves; missing required coverage fails the command. Provider-inapplicable cases are identified
// separately and never counted as passes.
[SupportedOSPlatform("windows")]
internal static class CloudRecoveryRestoreChecks
{
    private static int _checks;
    private static int _coveredCases;
    private static int _notApplicableCases;
    private static readonly HashSet<string> CoveredCaseTags = new(StringComparer.Ordinal);

    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Use a fresh manual Recovery restore fixture root.");
        Directory.CreateDirectory(root);
        CloudPairHostBindingChecks.UseFixtureRoot(root);
        _checks = 0;
        _coveredCases = 0;
        _notApplicableCases = 0;
        CoveredCaseTags.Clear();
        try
        {
            foreach (var (left, right) in new[]
                     {
                         (SyncEndpointKind.Dropbox, SyncEndpointKind.Dropbox),
                         (SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive),
                         (SyncEndpointKind.GoogleDrive, SyncEndpointKind.Dropbox),
                         (SyncEndpointKind.GoogleDrive, SyncEndpointKind.GoogleDrive)
                     })
            {
                // The refusal matrix runs on the two same-provider combinations, which together cover both
                // logical sides and both target providers; every combination runs the same real preview,
                // create-only restore, in-place preimage replacement, copy and CLI journeys.
                await JourneyAsync(left, right, refusals: left == right);
            }
            // An unresolved manual restore blocks that pair's own further cloud preparation, so every
            // uncertainty case runs on its own fresh pair: one genuinely unresolved outcome per pair is the
            // honest boundary this fixture can and must observe.
            foreach (var (left, right) in new[]
                     {
                         (SyncEndpointKind.Dropbox, SyncEndpointKind.Dropbox),
                         (SyncEndpointKind.GoogleDrive, SyncEndpointKind.GoogleDrive)
                     })
            {
                if (right == SyncEndpointKind.Dropbox)
                    await UncertaintyAsync(left, right, "hidden-create", ListingAbsentUncertainAsync);
                else
                    NotApplicable(left, right, "hidden-create",
                        "Dropbox's occupied-path add-mode refusal is not a Drive operation; " +
                        "Drive's real hidden-destination/name-race behavior is tested by drive-hidden-name-race.");
                await UncertaintyAsync(left, right, "changed-target", ChangedTargetAsync);
                await UncertaintyAsync(left, right, "post-effect-mismatch", PostEffectMismatchAsync);
                await UncertaintyAsync(left, right, "cancelled-after-dispatch", CancelledAfterDispatchAsync);
                await UncertaintyAsync(left, right, "declined-conditional-write", UnsupportedConditionalWriteAsync);
                await UncertaintyAsync(left, right, "cancelled-before-dispatch", CancelledBeforeDispatchAsync);
                await UncertaintyAsync(left, right, "abandon-window-expiry", AbandonWindowExpiryAsync);
                if (right == SyncEndpointKind.GoogleDrive)
                    await UncertaintyAsync(left, right, "drive-hidden-name-race", HiddenNameRaceAsync);
                else
                    NotApplicable(left, right, "drive-hidden-name-race",
                        "Dropbox paths are unique, unlike Drive sibling names; " +
                        "Dropbox's real hidden occupied-path refusal is tested by hidden-create.");
                await UncertaintyAsync(left, right, "retained-no-effect-retention", GuardedRejectionAsync);
            }
        }
        finally
        {
            SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = null;
        }
        var requiredTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in new[] { SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
        {
            foreach (var label in new[] { "changed-target", "post-effect-mismatch", "cancelled-after-dispatch",
                         "declined-conditional-write", "cancelled-before-dispatch", "abandon-window-expiry",
                         "retained-no-effect-retention" })
                requiredTags.Add(kind + "-" + kind + "-" + label);
        }
        requiredTags.Add("Dropbox-Dropbox-hidden-create");
        requiredTags.Add("GoogleDrive-GoogleDrive-drive-hidden-name-race");
        Check(CoveredCaseTags.SetEquals(requiredTags) && _coveredCases == 16 && _notApplicableCases == 2,
            "Required Recovery uncertainty coverage is incomplete or unexpected; missing: " +
            string.Join(", ", requiredTags.Except(CoveredCaseTags)) + "; unexpected: " +
            string.Join(", ", CoveredCaseTags.Except(requiredTags)));
        Console.WriteLine("PASS shared cloud Recovery restore: " + _checks + " assertions; required cases 16, " +
                          "covered cases " + _coveredCases + ", provider-inapplicable cases " +
                          _notApplicableCases + " (not passes), missing required cases 0, failed cases 0");
    }

    private static void NotApplicable(SyncEndpointKind left, SyncEndpointKind right, string label, string reason)
    {
        _notApplicableCases++;
        Console.WriteLine("NOT APPLICABLE (not a PASS): " + left + "-" + right + "-" + label + ": " + reason);
    }

    private static Spec Endpoint(SyncEndpointKind kind, string name) => kind == SyncEndpointKind.Dropbox
        ? CloudPairHostBindingChecks.HostDropbox(name, "/Recovery restore " + name)
        : CloudPairHostBindingChecks.HostDrive(name, "/Recovery restore " + name);

    private static SyncPair Draft(Spec left, Spec right, string name, SyncPair? prior = null) =>
        SyncEndpointPairDraft.Create(new SyncPairEndpoints(
            SyncEndpoint.Cloud(left.Kind, left.ConnectionId, left.EndpointNamespaceId, left.FolderId,
                left.FolderPath),
            SyncEndpoint.Cloud(right.Kind, right.ConnectionId, right.EndpointNamespaceId, right.FolderId,
                right.FolderPath),
            SyncMode.TwoWay), name, prior);

    private static ISyncRemoteStore Store(Rig rig, Spec spec) => spec.Kind == SyncEndpointKind.Dropbox
        ? rig.DropboxClient(spec)
        : rig.DriveClient(spec);

    /// <summary>The exact bytes the fixture retained as one artifact's predecessor.</summary>
    private static byte[] RetainedBytes(SyncAuthorityCloudRecoveryDescriptor descriptor)
    {
        var text = descriptor.Size == 0 ? string.Empty : "Original " + descriptor.OriginalPath;
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.LongLength != descriptor.Size)
            throw new InvalidOperationException(
                "The fixture's retained predecessor bytes do not match the retained size for " +
                descriptor.OriginalPath + ".");
        return bytes;
    }

    /// <summary>One target provider's own registered native digest of the exact bytes.</summary>
    private static string NativeDigest(Spec spec, byte[] bytes) => spec.Kind == SyncEndpointKind.Dropbox
        ? DropboxContentHash.Compute(bytes)
        : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<CloudPairHostBindingChecks.AdapterRead> ReadAdapterAsync(Rig rig, Spec spec,
        string relativePath) => spec.Kind == SyncEndpointKind.Dropbox
        ? await rig.ReadDropboxFileAsync(spec, relativePath)
        : await rig.ReadDriveFileAsync(spec, relativePath);

    private static async Task<byte[]> ReadAsync(Rig rig, Spec spec, string relativePath) =>
        (await ReadAdapterAsync(rig, spec, relativePath)).Bytes;

    private static async Task<string[]> InventoryAsync(Rig rig, Spec spec)
    {
        var store = Store(rig, spec);
        await store.VerifyRootIdentityAsync();
        var entries = await store.ListEntriesAsync();
        await store.VerifyRootIdentityAsync();
        return entries.Select(entry => entry.RelativePath).ToArray();
    }

    private static async Task<bool> ExistsAsync(Rig rig, Spec spec, string relativePath) =>
        (await InventoryAsync(rig, spec)).Contains(relativePath, StringComparer.Ordinal);

    private static bool Same(byte[] left, byte[] right) => left.AsSpan().SequenceEqual(right);

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        _checks++;
    }

    private static async Task<Exception> RefusedAsync(Func<Task> action, string label)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
                                      IOException or ArgumentException or HttpRequestException or
                                      NotSupportedException)
        { Check(true, label); return error; }
        throw new InvalidOperationException("Expected a safe refusal: " + label);
    }

    private static async Task<SyncAuthorityBatchResult> RunPairAsync(Rig rig, SyncPair pair, string tag)
    {
        var result = await rig.Host.RunPairsAsync(new[] { pair }, "manual");
        Check(result.Items.Count == 1 && result.Items[0].FailureMessage is null &&
              result.Items[0].Result is { Blocked: 0, Conflicts: 0 },
            tag + ": the ordinary pair run completes before the restore cases: " +
            result.Items.FirstOrDefault()?.FailureMessage);
        return result;
    }

    /// <summary>One retained typed artifact of this pair: its projection and its exact descriptor.</summary>
    private sealed record Artifact(SyncRecoveryArtifactProjection Projection,
        SyncAuthorityCloudRecoveryDescriptor Descriptor)
    {
        internal string Key => Projection.RecoveryArtifactId;
        internal string Path => Descriptor.OriginalPath;
        internal bool Left => string.Equals(Descriptor.LogicalSide, SyncAuthorityCloudSides.LogicalLeft,
            StringComparison.Ordinal);
    }

    private static async Task<Artifact[]> TypedArtifactsAsync(Rig rig, string pairId) =>
        (await rig.Db.LoadRecoveryArtifactsAsync()).Where(item => item.PairId == pairId)
            .Select(item => SyncAuthorityCloudRecoveryDescriptor.TryParse(item.IdentityJson, out var descriptor)
                ? new Artifact(item, descriptor!) : null)
            .Where(item => item is not null).Select(item => item!).ToArray();

    private static async Task<SyncRecoveryCatalogItem> ItemAsync(Rig rig, string pairId, string key) =>
        (await rig.Host.LoadRecoveryCatalogAsync(pairId)).Single(item => item.Key == key);

    private static Spec Target(Artifact artifact, Spec left, Spec right) => artifact.Left ? left : right;

    private static async Task<Dictionary<string, long>> OperationStatusesAsync(Rig rig) =>
        (await rig.RowsAsync("SELECT status||'='||count(*) FROM operation WHERE kind='reconcile' GROUP BY status;"))
        .Select(row => row.Split('='))
        .ToDictionary(parts => parts[0], parts => long.Parse(parts[1], CultureInfo.InvariantCulture));

    private static async Task<string> LatestCommandStatusAsync(Rig rig) => await rig.ScalarAsync(
        "SELECT status FROM command_request WHERE command_kind='reconcile' ORDER BY rowid DESC LIMIT 1;");

    /// <summary>The number of restore operations this authority has completed.</summary>
    private static async Task<long> CompletedAsync(Rig rig) =>
        (await OperationStatusesAsync(rig)).TryGetValue("completed", out var value) ? value : 0;

    /// <summary>Every reconcile command this authority has recorded, by status.</summary>
    private static async Task<Dictionary<string, long>> CommandStatusesAsync(Rig rig) =>
        (await rig.RowsAsync(
            "SELECT status||'='||count(*) FROM command_request WHERE command_kind='reconcile' GROUP BY status;"))
        .Select(row => row.Split('='))
        .ToDictionary(parts => parts[0], parts => long.Parse(parts[1], CultureInfo.InvariantCulture));

    private static long Total(IReadOnlyDictionary<string, long> statuses) => statuses.Values.Sum();

    /// <summary>Every no-effect abandonment this authority has recorded.</summary>
    private static async Task<long> SupersededRestoreCountAsync(Rig rig) => long.Parse(await rig.ScalarAsync(
        "SELECT count(*) FROM operation WHERE kind='reconcile' AND status='superseded';"),
        CultureInfo.InvariantCulture);

    /// <summary>Every no-effect abandonment this authority has recorded.</summary>
    private static async Task<long> NoEffectSupersedeCountAsync(Rig rig) =>
        long.Parse(await rig.ScalarAsync(
            "SELECT count(*) FROM history_event WHERE event_kind='manual_restore_no_effect_superseded' OR (producer_kind='lifecycle' AND json_extract(detail_json,'$.OperationSnapshot.EventKind')='manual_restore_no_effect_superseded');"),
            CultureInfo.InvariantCulture);

    /// <summary>The newest manual restore operation with the immutable values its guards bind.</summary>
    private static async Task<(string OperationId, string CommandId, string ArtifactId, string Path)>
        LatestRestoreOperationAsync(Rig rig)
    {
        var row = (await rig.RowsAsync(
            "SELECT operation_id||'|'||json_extract(preconditions_json,'$.restoreCommandId')||'|'||" +
            "json_extract(preconditions_json,'$.recoveryArtifactId')||'|'||" +
            "json_extract(preconditions_json,'$.path') FROM operation WHERE kind='reconcile' " +
            "ORDER BY rowid DESC LIMIT 1;")).Single();
        var parts = row.Split('|');
        return (parts[0], parts[1], parts[2], parts[3]);
    }

    private static async Task<long> AttemptCountAsync(Rig rig, string operationId) =>
        long.Parse(await rig.ScalarAsync("SELECT count(*) FROM operation_attempt WHERE operation_id='" +
                                         operationId + "';"), CultureInfo.InvariantCulture);

    private static async Task<string> OperationStatusAsync(Rig rig, string operationId) =>
        await rig.ScalarAsync("SELECT status FROM operation WHERE operation_id='" + operationId + "';");

    /// <summary>
    /// The exact retained recovery member of one artifact as the target adapter serves it, with the bytes that
    /// member holds, read through the target's own bound Recovery root.
    /// </summary>
    private static async Task<(SyncArchiveResult? Member, byte[] Bytes)> RetainedMemberAsync(Rig rig, Spec spec,
        SyncAuthorityCloudRecoveryDescriptor descriptor)
    {
        var store = Store(rig, spec);
        var binding = new SyncRemoteRecoveryRootBinding(descriptor.RecoveryPath, descriptor.RecoveryFolderId,
            descriptor.RecoveryNamespaceId);
        var name = Path.GetFileName(descriptor.RetainedPath);
        var member = await store.GetRecoveryAsync(binding, name);
        if (member is null) return (null, []);
        using var destination = new MemoryStream();
        await store.DownloadRecoveryAsync(binding, member, destination);
        return (member, destination.ToArray());
    }

    /// <summary>
    /// Advancing one recovery allocation must pass the same ordered transitions the product uses, so fixture
    /// state never violates the authority's own allocation guard.
    /// </summary>
    private static async Task<long> AdvanceRecoveryAllocationForTestAsync(Rig rig, int requiredCount)
    {
        const string newest = "(SELECT operation_id FROM operation WHERE kind='reconcile' ORDER BY rowid DESC LIMIT 1)";
        var leaseId = await rig.ScalarAsync("SELECT current_lease_id FROM operation WHERE operation_id=" + newest +
                                            ";");
        var token = await rig.ScalarAsync("SELECT current_fencing_token FROM operation WHERE operation_id=" +
                                          newest + ";");
        if (string.IsNullOrWhiteSpace(leaseId) || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "The guard fixture needs the operation's own current fenced lease to advance its allocation.");
        var allocations = await rig.RowsAsync(
            "SELECT allocation_id FROM resource_allocation WHERE resource_kind='remote_recovery_file' " +
            "AND status='authorized' AND operation_id=" + newest + ";");
        if (allocations.Count != requiredCount)
            throw new InvalidOperationException("The guard fixture expected " + requiredCount +
                                                " fresh remote recovery allocation(s), found " + allocations.Count + ".");
        foreach (var allocation in allocations)
        {
            foreach (var (from, to) in new[]
                     {
                         ("authorized", "creating"), ("creating", "created"), ("created", "bound"),
                         ("bound", "verified")
                     })
                await rig.ExecuteSqlAsync(
                    "UPDATE resource_allocation SET status=$status, bound_identity='guard-fixture-binding', " +
                    "content_hash='" + new string('a', 64) + "', size_bytes=1, transition_lease_id=$lease, " +
                    "transition_fencing_token=$token WHERE allocation_id=$id AND status=$from;",
                    ("$status", to), ("$lease", leaseId), ("$token", token), ("$id", allocation),
                    ("$from", from));
        }
        return allocations.Count;
    }

    private static async Task<string[]> PreimageDescriptorsAsync(Rig rig, string pairId) =>
        (await rig.RowsAsync(
            "SELECT ra.identity_json FROM recovery_artifact ra JOIN operation o ON o.operation_id=ra.operation_id " +
            "WHERE o.kind='reconcile' AND o.pair_id='" + pairId + "';")).ToArray();

    /// <summary>
    /// Requires that exactly this many restore operations reached their completed boundary, and that none is
    /// left in the committed-but-uncleaned state the owned cleanup entry must always close.
    /// </summary>
    private static async Task RequireCompletedWithCleanupAsync(Rig rig, long completed, string label)
    {
        var statuses = await OperationStatusesAsync(rig);
        Check(statuses.TryGetValue("completed", out var value) && value == completed,
            label + ": completed restore operations were " + completed + " but the authority holds " +
            string.Join(",", statuses.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => entry.Key + "=" + entry.Value)));
        Check(!statuses.ContainsKey("committed") && !statuses.ContainsKey("cleanup_pending"),
            label + ": every committed restore completed its own owned private cleanup");
    }

    private static async Task JourneyAsync(SyncEndpointKind leftKind, SyncEndpointKind rightKind, bool refusals)
    {
        var tag = leftKind + "-" + rightKind;
        var (rig, pair, left, right, byPath) = await BuildFixtureAsync("restore-" + tag, leftKind, rightKind,
            full: true);
        using (rig)
        {
            var one = byPath["left-one.txt"];
            var two = byPath["left-two.txt"];
            var three = byPath["right.txt"];
            Check(one.Descriptor.Size > 0 && three.Descriptor.Size > 0,
                tag + ": the retained set carries the nonempty predecessors");
            await PreviewCasesAsync(rig, pair, tag, one, two, left, right);
            await CreateOnlyAbsentAsync(rig, pair, tag, one, left, right);
            await RestoreInPlaceAsync(rig, pair, tag, three, left, right);
            await SecondCallAsync(rig, pair, tag, three, left, right);
            await ZeroLengthAsync(rig, pair, tag, two, left, right);
            await CopyCasesAsync(rig, pair, tag, one, left, right);
            await CliDispatchAsync(rig, pair, tag, two, left, right);
            // CC4E-ISSUE-3 correction: a pre-dispatch failure must be abandoned as a no-effect refusal, must not
            // strand a leased operation, and must leave the same pair able to complete a fresh restore.
            await InterruptedThenFreshRestoreAsync(rig, pair, tag, one, left, right);
            if (refusals)
            {
                await WrongDescriptorCasesAsync(rig, pair, tag, one, left, right);
                // The changed-retained-copy case runs last of this pair's cases because it permanently
                // replaces the retained member it observes.
                await ChangedArchiveAsync(rig, pair, tag, one, left, right);
                Console.WriteLine("PASS " + tag + " manual restore descriptor and retained-copy refusals");
            }
        }
        Console.WriteLine("PASS " + tag + " real Host and CLI manual cloud restore");
    }

    /// <summary>
    /// One uncertainty case on its own fresh pair: an unresolved manual restore blocks that pair's further
    /// cloud preparation, so exactly one genuinely unresolved outcome per pair is what this fixture can and
    /// must observe. Every case never reuses a spent request and never claims an outcome it did not observe.
    /// </summary>
    private static async Task UncertaintyAsync(SyncEndpointKind leftKind, SyncEndpointKind rightKind,
        string label, Func<Rig, SyncPair, string, Artifact, Spec, Spec, Task<bool>> run)
    {
        var tag = leftKind + "-" + rightKind + "-" + label;
        var (rig, pair, left, right, byPath) = await BuildFixtureAsync("case-" + tag, leftKind, rightKind,
            full: false);
        bool covered;
        using (rig)
        {
            covered = await run(rig, pair, tag, byPath["left-one.txt"], left, right);
        }
        if (!covered)
            throw new InvalidOperationException("Required Recovery uncertainty case was NOT COVERED: " + tag);
        Check(CoveredCaseTags.Add(tag), "Recovery uncertainty case ran more than once: " + tag);
        _coveredCases++;
        Console.WriteLine("PASS " + tag + " retained uncertainty");
    }

    /// <summary>
    /// The real isolated fixture every case runs over: two registered cloud endpoints, the real Host pair
    /// binding, the effect-free bootstrap review, the enabled pair and two real update passes that retain typed
    /// cloud predecessors. The full variant adds one further pass that retains a fourth predecessor kept free
    /// of every destructive case, so each success case has its own exact retained artifact path.
    /// </summary>
    private static async Task<(Rig Rig, SyncPair Pair, Spec Left, Spec Right,
        Dictionary<string, Artifact> ByPath)> BuildFixtureAsync(string caseName, SyncEndpointKind leftKind,
        SyncEndpointKind rightKind, bool full)
    {
        var rig = await Rig.CreateAsync(caseName);
        var left = Endpoint(leftKind, caseName + "-L");
        var right = Endpoint(rightKind, caseName + "-R");
        try
        {
            await rig.AddEndpointAsync(left);
            await rig.AddEndpointAsync(right);
            foreach (var path in new[] { "left-one.txt", "left-two.txt", "right.txt" })
            {
                rig.SeedFile(left, path, path == "left-two.txt" ? "" : "Original " + path);
                rig.SeedFile(right, path, path == "left-two.txt" ? "" : "Original " + path);
            }
            rig.SeedFile(left, "extra.txt", "Original extra.txt");
            rig.SeedFile(right, "extra.txt", "Original extra.txt");
            var bound = await rig.Host.BindEndpointPairAsync(
                Draft(left, right, caseName) with { RecoveryRetentionDays = 90 });
            await rig.Host.RefreshBootstrapInventoryAsync(bound.Pair.Id);
            await rig.Host.ApproveBootstrapMergeAsync(bound.Pair.Id);
            var enabled = await rig.Host.SetPairEnabledAsync(bound.Pair.Id, true);
            var pair = enabled.Pairs.Single(item => item.Id == bound.Pair.Id);
            await RunPairAsync(rig, pair, caseName);
            rig.SeedFile(left, "left-one.txt", "Changed left one");
            rig.SeedFile(left, "left-two.txt", "Changed left two");
            rig.SeedFile(right, "right.txt", "Changed right");
            await RunPairAsync(rig, pair, caseName);
            if (full)
            {
                rig.SeedFile(left, "extra.txt", "Changed extra");
                await RunPairAsync(rig, pair, caseName);
            }
            var artifacts = await TypedArtifactsAsync(rig, pair.Id);
            var expected = full ? 4 : 3;
            Check(artifacts.Length == expected,
                caseName + ": the real update path retained " + expected + " typed cloud predecessors");
            var byPath = artifacts.ToDictionary(item => item.Path, StringComparer.Ordinal);
            Check(byPath["left-two.txt"].Descriptor.Size == 0,
                caseName + ": the retained set carries the zero-length predecessor");
            return (rig, pair, left, right, byPath);
        }
        catch
        {
            rig.Dispose();
            throw;
        }
    }

    private static async Task PreviewCasesAsync(Rig rig, SyncPair pair, string tag, Artifact one, Artifact two,
        Spec left, Spec right)
    {
        var item = await ItemAsync(rig, pair.Id, one.Key);
        Check(item.CanRestore && item.CanRestoreCopy && !item.CanRemove,
            tag + ": the real catalogue item is eligible for the typed route while retention stays enforced");
        var before = rig.Wire.TotalRequests;
        var preview = await rig.Host.PreviewRecoveryAsync(item, false);
        Check(rig.Wire.TotalRequests > before,
            tag + ": the preview reads the destination through the target's own adapter, never a local path probe");
        Check(preview.TargetRelativePath == one.Path && !preview.RestoreAsCopy && preview.DestinationExists,
            tag + ": the in-place preview names the exact normalized destination and its current occupancy");
        Check(preview.SourceRelativePath == one.Path,
            tag + ": the preview names the retained artifact's own original path");
        Check(preview.Summary.Contains("cloud endpoint", StringComparison.Ordinal) &&
              preview.Summary.Contains("Recovery root", StringComparison.Ordinal),
            tag + ": the preview names the actual logical endpoint and the verified Recovery root");
        var copyPreview = await rig.Host.PreviewRecoveryAsync(item, true);
        Check(copyPreview.RestoreAsCopy && copyPreview.TargetRelativePath != one.Path &&
              !copyPreview.TargetRelativePath.Contains("..", StringComparison.Ordinal) &&
              !await ExistsAsync(rig, Target(one, left, right), copyPreview.TargetRelativePath),
            tag + ": the copy preview chooses a distinct create-only destination that the target does not hold");
        var zero = await ItemAsync(rig, pair.Id, two.Key);
        var zeroPreview = await rig.Host.PreviewRecoveryAsync(zero, false);
        Check(zeroPreview.TargetRelativePath == two.Path && two.Descriptor.Size == 0,
            tag + ": the zero-length retained copy previews its exact destination");
        Check((await OperationStatusesAsync(rig)).Count == 0,
            tag + ": an informational preview created no operation and no effect");
    }

    /// <summary>
    /// CC4E-2/CC4E-3: a restore whose destination is absent takes the create-only path: the target adapter's
    /// own inventory proved the chosen destination absent, the write is a create with no expected revision, no
    /// destination preimage is retained, no conditional replacement is issued and no existing object is
    /// displaced. The copy destination is used because this fixture deliberately refuses to remove a visible
    /// pair-root object, so an absent destination is created - never fabricated by deleting anything.
    /// </summary>
    private static async Task CreateOnlyAbsentAsync(Rig rig, SyncPair pair, string tag, Artifact artifact,
        Spec left, Spec right)
    {
        var spec = Target(artifact, left, right);
        var item = await ItemAsync(rig, pair.Id, artifact.Key);
        var inventory = await InventoryAsync(rig, spec);
        var writes = rig.Wire.Writes.Total;
        var updates = rig.Wire.Updates.Total;
        var deletes = rig.Wire.RecoveryDeleteRequests;
        var result = await rig.Host.RestoreRecoveryAsync(item, true);
        Check(!inventory.Contains(result.TargetRelativePath, StringComparer.Ordinal) &&
              result.TargetRelativePath != artifact.Path,
            tag + ": the create-only restore wrote to a destination its own inventory proved absent");
        var restored = await ReadAdapterAsync(rig, spec, result.TargetRelativePath);
        Check(Same(restored.Bytes, RetainedBytes(artifact.Descriptor)),
            tag + ": the absent destination was restored through a real create-only adapter write");
        Check(restored.Result.ContentHash == NativeDigest(spec, RetainedBytes(artifact.Descriptor)),
            tag + ": the created object carries the target provider's own native digest of the retained bytes");
        Check(rig.Wire.Writes.Total == writes + 1,
            tag + ": exactly one create write was issued for the absent destination");
        Check(rig.Wire.Updates.Total == updates,
            tag + ": an absent destination issued no conditional replacement and no preimage copy");
        Check(rig.Wire.RecoveryDeleteRequests == deletes,
            tag + ": the create-only restore issued no deletion");
        Check((await PreimageDescriptorsAsync(rig, pair.Id)).Length == 0,
            tag + ": an absent destination needs no retained preimage");
        Check(await LatestCommandStatusAsync(rig) == "completed",
            tag + ": the create-only restore command reached completed");
        await RequireCompletedWithCleanupAsync(rig, 1,
            tag + ": the create-only restore operation reached its completed boundary");
        var allocations = await rig.RowsAsync(
            "SELECT a.resource_kind||'|'||a.control_namespace_id||'|'||a.intended_parent_identity " +
            "FROM resource_allocation a JOIN operation o ON o.operation_id=a.operation_id " +
            "WHERE o.kind='reconcile' AND o.pair_id='" + pair.Id + "';");
        var namespaceId = await rig.ScalarAsync(
            "SELECT control_namespace_id FROM pair_control_namespace WHERE pair_id='" + pair.Id +
            "' AND status='active';");
        Check(allocations.Count > 0 && allocations.All(row => row.Split('|')[1] == namespaceId),
            tag + ": every restore allocation is private staging inside the pair's own control namespace");
        Check(allocations.All(row => row.Split('|')[0] != "local_recovery_file"),
            tag + ": the cloud restore never allocates a local recovery file");
        var definition = await rig.PairDefinitionAsync(pair.Id);
        Check(!definition.Contains("\"localRoot\":\"", StringComparison.Ordinal) ||
              definition.Contains("\"localRoot\":\"\"", StringComparison.Ordinal) ||
              !definition.Contains("localRoot", StringComparison.Ordinal),
            tag + ": the restored pair carries no selected NTFS root for the restore to fall back on");
    }

    private static async Task RestoreInPlaceAsync(Rig rig, SyncPair pair, string tag, Artifact artifact, Spec left,
        Spec right)
    {
        var spec = Target(artifact, left, right);
        var item = await ItemAsync(rig, pair.Id, artifact.Key);
        var predecessor = await ReadAdapterAsync(rig, spec, artifact.Path);
        Check(!Same(predecessor.Bytes, RetainedBytes(artifact.Descriptor)),
            tag + ": the in-place destination carries different bytes than the retained artifact");
        var updates = rig.Wire.Updates;
        var deletes = rig.Wire.RecoveryDeleteRequests;
        var result = await rig.Host.RestoreRecoveryAsync(item, false);
        var restored = await ReadAdapterAsync(rig, spec, artifact.Path);
        Check(result.TargetRelativePath == artifact.Path && Same(restored.Bytes, RetainedBytes(artifact.Descriptor)),
            tag + ": the existing destination was conditionally replaced with the exact retained bytes");
        var after = rig.Wire.Updates;
        Check(after.DropboxUpdates + after.DriveUpdates == updates.DropboxUpdates + updates.DriveUpdates + 1,
            tag + ": exactly one revision-bound conditional replacement was issued");
        Check(after.DropboxRecoveryCopies + after.DriveRecoveryCopies ==
              updates.DropboxRecoveryCopies + updates.DriveRecoveryCopies + 1,
            tag + ": the displaced predecessor was retained in the target's own Recovery root first");
        Check(rig.Wire.RecoveryDeleteRequests == deletes, tag + ": the in-place restore deleted nothing");
        Check(restored.Result.ContentHash == NativeDigest(spec, RetainedBytes(artifact.Descriptor)),
            tag + ": the replaced object carries the target provider's own native digest of the retained bytes");
        var preimages = await PreimageDescriptorsAsync(rig, pair.Id);
        var matches = preimages
            .Select(json => SyncAuthorityCloudRecoveryDescriptor.TryParse(json, out var descriptor)
                ? descriptor : null)
            .Where(descriptor => descriptor is not null &&
                                 string.Equals(descriptor.OriginalPath, artifact.Path, StringComparison.Ordinal))
            .ToArray();
        Check(matches.Length == 1,
            tag + ": exactly one typed preimage artifact names this exact displaced destination");
        var preimage = matches[0]!;
        Check(preimage.Size == predecessor.Result.Size &&
              string.Equals(preimage.ContentHash, predecessor.Result.ContentHash, StringComparison.OrdinalIgnoreCase),
            tag + ": the retained preimage descriptor carries the exact displaced size and native hash");
        var binding = new SyncRemoteRecoveryRootBinding(preimage.RecoveryPath, preimage.RecoveryFolderId,
            preimage.RecoveryNamespaceId);
        var retained = await Store(rig, spec).GetRecoveryAsync(binding, Path.GetFileName(preimage.RetainedPath));
        Check(retained is not null && !string.IsNullOrWhiteSpace(retained.FileId) &&
              retained.Size == predecessor.Result.Size &&
              string.Equals(retained.ContentHash, predecessor.Result.ContentHash, StringComparison.OrdinalIgnoreCase),
            tag + ": the retained preimage reads back at the target's own Recovery root with the displaced content");
        using (var recovered = new MemoryStream())
        {
            var receipt = await Store(rig, spec).DownloadRecoveryAsync(binding, retained!, recovered,
                CancellationToken.None);
            Check(Same(recovered.ToArray(), predecessor.Bytes) && receipt.Size == predecessor.Result.Size &&
                  string.Equals(receipt.ContentHash, predecessor.Result.ContentHash,
                      StringComparison.OrdinalIgnoreCase),
                tag + ": the displaced destination's exact previous bytes are recoverable from the preimage");
        }
        Check(await LatestCommandStatusAsync(rig) == "completed",
            tag + ": the in-place restore command reached completed");
        await RequireCompletedWithCleanupAsync(rig, 2,
            tag + ": two restore operations reached their completed boundary");
    }

    private static async Task SecondCallAsync(Rig rig, SyncPair pair, string tag, Artifact artifact, Spec left,
        Spec right)
    {
        var spec = Target(artifact, left, right);
        var item = await ItemAsync(rig, pair.Id, artifact.Key);
        var updates = rig.Wire.Updates.Total;
        var restored = await ReadAsync(rig, spec, artifact.Path);
        var result = await rig.Host.RestoreRecoveryAsync(item, false);
        Check(result.TargetRelativePath == artifact.Path &&
              Same(await ReadAsync(rig, spec, artifact.Path), restored),
            tag + ": a second explicit restore revalidates and restores the same exact retained bytes");
        Check(rig.Wire.Updates.Total == updates + 2,
            tag + ": the second restore issued its own replacement and its own preimage, never a reused request");
        Check((await PreimageDescriptorsAsync(rig, pair.Id)).Length == 2,
            tag + ": the second explicit restore retained the now-current destination as a second typed preimage");
        await RequireCompletedWithCleanupAsync(rig, 3,
            tag + ": three restore operations reached their completed boundary");
    }

    private static async Task ZeroLengthAsync(Rig rig, SyncPair pair, string tag, Artifact artifact, Spec left,
        Spec right)
    {
        var spec = Target(artifact, left, right);
        var item = await ItemAsync(rig, pair.Id, artifact.Key);
        var predecessor = await ReadAdapterAsync(rig, spec, artifact.Path);
        Check(artifact.Descriptor.Size == 0 && predecessor.Bytes.Length > 0,
            tag + ": the zero-length retained copy replaces a nonempty destination");
        await rig.Host.RestoreRecoveryAsync(item, false);
        var restored = await ReadAdapterAsync(rig, spec, artifact.Path);
        Check(restored.Bytes.Length == 0 && restored.Result.Size == 0 &&
              restored.Result.ContentHash == NativeDigest(spec, []),
            tag + ": the zero-length restore wrote an exact empty object with the target's native empty digest");
        Check((await PreimageDescriptorsAsync(rig, pair.Id)).Length == 3,
            tag + ": the zero-length replacement also retained its displaced predecessor");
        await RequireCompletedWithCleanupAsync(rig, 4,
            tag + ": four restore operations reached their completed boundary");
    }

    private static async Task CopyCasesAsync(Rig rig, SyncPair pair, string tag, Artifact artifact, Spec left,
        Spec right)
    {
        var spec = Target(artifact, left, right);
        var item = await ItemAsync(rig, pair.Id, artifact.Key);
        var deletes = rig.Wire.RecoveryDeleteRequests;
        var occupied = await ReadAdapterAsync(rig, spec, artifact.Path);
        var before = await InventoryAsync(rig, spec);
        var seeded = SeedCandidateNames(rig, spec, artifact.Path);
        var result = await rig.Host.RestoreRecoveryAsync(item, true);
        Check(result.TargetRelativePath != artifact.Path && !seeded.Contains(result.TargetRelativePath),
            tag + ": the copy chose a create-only destination that was neither the original nor an occupied candidate");
        Check(!before.Contains(result.TargetRelativePath, StringComparer.Ordinal),
            tag + ": the chosen copy path was absent from the target's own pre-copy inventory");
        Check(Same(await ReadAsync(rig, spec, artifact.Path), occupied.Bytes),
            tag + ": the occupied original destination kept its exact bytes and identity");
        foreach (var candidate in seeded)
            Check(Encoding.UTF8.GetString(await ReadAsync(rig, spec, candidate)) == "Occupied candidate",
                tag + ": the occupied candidate " + candidate + " was never overwritten");
        Check(Same(await ReadAsync(rig, spec, result.TargetRelativePath), RetainedBytes(artifact.Descriptor)),
            tag + ": the copy holds the exact retained bytes");
        Check(rig.Wire.RecoveryDeleteRequests == deletes, tag + ": the copy deleted nothing");
        var second = await rig.Host.RestoreRecoveryAsync(item, true);
        Check(second.TargetRelativePath != result.TargetRelativePath &&
              second.TargetRelativePath != artifact.Path && !seeded.Contains(second.TargetRelativePath),
            tag + ": a second copy chose its own distinct create-only destination");
        Check(Same(await ReadAsync(rig, spec, result.TargetRelativePath), RetainedBytes(artifact.Descriptor)) &&
              Same(await ReadAsync(rig, spec, second.TargetRelativePath), RetainedBytes(artifact.Descriptor)),
            tag + ": both copies hold the exact retained bytes and neither replaced the other");
        Check(rig.Wire.RecoveryDeleteRequests == deletes, tag + ": neither copy deleted anything");
        await RequireCompletedWithCleanupAsync(rig, 6,
            tag + ": six restore operations reached their completed boundary");
    }

    /// <summary>
    /// Seeds the recovered-copy candidate names this exact naming convention can propose for the seconds around
    /// now, so the bounded collision search has taken candidates to skip. The seeded objects are ordinary
    /// visible files: nothing here can cause a deletion or an overwrite.
    /// </summary>
    private static string[] SeedCandidateNames(Rig rig, Spec spec, string originalPath)
    {
        var slash = originalPath.LastIndexOf('/');
        var parent = slash < 0 ? string.Empty : originalPath[..slash];
        var name = slash < 0 ? originalPath : originalPath[(slash + 1)..];
        var extension = Path.GetExtension(name);
        var stem = extension.Length == 0 ? name : name[..^extension.Length];
        var candidates = new List<string>();
        var now = DateTimeOffset.UtcNow;
        for (var offset = -1; offset <= 4; offset++)
        for (var sequence = 0; sequence <= 1; sequence++)
        {
            var suffix = sequence == 0 ? string.Empty : " " + sequence;
            var candidate = stem + " (recovered " + now.AddSeconds(offset).ToString("yyyyMMdd-HHmmss") + ")" +
                            suffix + extension;
            if (parent.Length != 0) candidate = parent + "/" + candidate;
            rig.SeedFile(spec, candidate, "Occupied candidate");
            candidates.Add(candidate);
        }
        return candidates.ToArray();
    }

    private static async Task CliDispatchAsync(Rig rig, SyncPair pair, string tag, Artifact artifact, Spec left,
        Spec right)
    {
        var spec = Target(artifact, left, right);
        var item = await ItemAsync(rig, pair.Id, artifact.Key);
        Check(item.CanRestore && item.CanRestoreCopy,
            tag + ": the CLI dispatch starts from an eligible catalogue item, never a hand-set flag");
        var before = await InventoryAsync(rig, spec);
        var runtime = new RigCliOperations(rig.Host, rig.StateAuthority);
        var dispatched = await SyncCliRegistry.DispatchAsync(runtime,
            SyncCliRegistry.Parse(["--cli", "recovery", "restore", "--pair", pair.Id, "--key", artifact.Key,
                "--copy", "--yes"]), TextWriter.Null, CancellationToken.None);
        Check(dispatched is SyncCliRecoveryActionData data &&
              data.Action == "restore-copy" && !string.IsNullOrWhiteSpace(data.Result),
            tag + ": the existing CLI recovery restore command dispatched through the real registry");
        var after = await InventoryAsync(rig, spec);
        var created = after.Where(path => !before.Contains(path, StringComparer.Ordinal)).ToArray();
        Check(created.Length == 1 && created[0] != artifact.Path &&
              Same(await ReadAsync(rig, spec, created[0]), RetainedBytes(artifact.Descriptor)),
            tag + ": the CLI restore created exactly one new copy holding the exact retained bytes");
        Check(Same(await ReadAsync(rig, spec, artifact.Path), RetainedBytes(artifact.Descriptor)),
            tag + ": the CLI copy left the original destination unchanged");
        await RequireCompletedWithCleanupAsync(rig, 7,
            tag + ": seven restore operations reached their completed boundary");
    }

    /// <summary>
    /// The one create-only boundary this fixture can drive against an object the provider still holds: the
    /// Dropbox add-mode write is refused while the pair-root object is only hidden from the listing. The
    /// restore therefore never overwrites what the provider keeps, issues no conditional replacement and no
    /// preimage, and leaves its command blocked with the outcome explicitly unresolved.
    /// </summary>
    private static async Task<bool> ListingAbsentUncertainAsync(Rig rig, SyncPair pair, string tag,
        Artifact artifact, Spec left, Spec right)
    {
        var spec = Target(artifact, left, right);
        if (spec.Kind != SyncEndpointKind.Dropbox)
            throw new InvalidOperationException(tag + ": Dropbox add-mode case received a non-Dropbox target.");
        var item = await ItemAsync(rig, pair.Id, artifact.Key);
        var current = await ReadAdapterAsync(rig, spec, artifact.Path);
        var completedBefore = await CompletedAsync(rig);
        rig.Wire.SetDropboxMissing(current.Result.FileId!, true);
        Check(!await ExistsAsync(rig, spec, artifact.Path),
            tag + ": the hidden destination is absent from the target's own listing");
        var updates = rig.Wire.Updates.Total;
        var deletes = rig.Wire.RecoveryDeleteRequests;
        await RefusedAsync(() => rig.Host.RestoreRecoveryAsync(item, false),
            tag + ": a create-only write the provider refuses is never reported as success");
        Check(rig.Wire.Updates.Total == updates,
            tag + ": the refused create-only write issued no conditional replacement and no preimage copy");
        Check(rig.Wire.RecoveryDeleteRequests == deletes,
            tag + ": the refused create-only write deleted nothing");
        Check(await LatestCommandStatusAsync(rig) == "blocked",
            tag + ": the refused create-only write left its command blocked");
        var statuses = await OperationStatusesAsync(rig);
        Console.WriteLine("  " + tag + " hidden-destination create-only refusal retained as " +
                          string.Join(",", statuses.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                              .Select(entry => entry.Key + "=" + entry.Value)));
        Check(await CompletedAsync(rig) == completedBefore,
            tag + ": the refused create-only write completed no restore operation");
        // A dispatched attempt is NEVER abandoned as a no-effect refusal: this pair keeps its unresolved
        // operation and its exact evidence instead of being quietly superseded.
        Check(await SupersededRestoreCountAsync(rig) == 0 && await NoEffectSupersedeCountAsync(rig) == 0,
            tag + ": an operation that dispatched a request is never admitted to the no-effect transition");
        Check(await AttemptCountAsync(rig, (await LatestRestoreOperationAsync(rig)).OperationId) >= 1,
            tag + ": the refused create-only request left its dispatched attempt retained");
        rig.Wire.SetDropboxMissing(current.Result.FileId!, false);
        Check(Same(await ReadAsync(rig, spec, artifact.Path), current.Bytes),
            tag + ": the provider-held destination kept its exact bytes throughout the refusal");
        return true;
    }

    private static async Task WrongDescriptorCasesAsync(Rig rig, SyncPair pair, string tag, Artifact artifact,
        Spec left, Spec right)
    {
        var spec = Target(artifact, left, right);
        var destination = await ReadAsync(rig, spec, artifact.Path);
        var writes = rig.Wire.Writes.Total;
        var updates = rig.Wire.Updates.Total;
        var commands = await CommandStatusesAsync(rig);
        var operation = await rig.Db.LoadOperationAsync(artifact.Projection.OperationId);
        var allocation = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        var item = await ItemAsync(rig, pair.Id, artifact.Key);
        var invalid = new[]
        {
            ("corpus", artifact.Descriptor with { RecoveryCorpusId = "foreign-corpus" }),
            ("account", artifact.Descriptor with { ProviderAccountId = "foreign-account" }),
            ("endpoint-root", artifact.Descriptor with { EndpointRootBindingId = "foreign-root" }),
            ("recovery-root", artifact.Descriptor with { RecoveryRootBindingId = "foreign-recovery-root" }),
            ("algorithm", artifact.Descriptor with
            {
                NativeAlgorithm = artifact.Descriptor.NativeAlgorithm == SyncContentIdentity.DropboxBlockSha256
                    ? SyncContentIdentity.Sha256
                    : SyncContentIdentity.DropboxBlockSha256
            }),
            ("retained-file", artifact.Descriptor with { RetainedFileId = "foreign-file" }),
            ("retained-revision", artifact.Descriptor with { RetainedRevision = "foreign-revision" }),
            ("logical-side", artifact.Descriptor with
            {
                LogicalSide = artifact.Left ? SyncAuthorityCloudSides.LogicalRight :
                    SyncAuthorityCloudSides.LogicalLeft
            }),
            ("newer-version", artifact.Descriptor with { })
        };
        for (var index = 0; index < invalid.Length; index++)
        {
            var (label, descriptor) = invalid[index];
            var identity = label == "newer-version"
                ? descriptor.Serialize().Replace("\"identityVersion\":1", "\"identityVersion\":2",
                    StringComparison.Ordinal)
                : descriptor.Serialize();
            var key = "invalid-restore-" + tag + "-" + index;
            await rig.Db.RegisterRecoveryArtifactAsync(key, artifact.Projection.OperationId,
                artifact.Projection.ObjectId, allocation.AllocationId, "dropbox", identity,
                artifact.Projection.ContentHash, artifact.Projection.Size);
            var hostile = item with
            {
                Key = key, IdentityJson = "{}", Identity = "untrusted", Provider = "untrusted",
                ConnectionId = "untrusted", LogicalSide = "untrusted",
                Source = SyncRecoverySource.LocalArchive, CanRestore = true, CanRemove = true
            };
            await RefusedAsync(() => rig.Host.RestoreRecoveryAsync(hostile, false),
                tag + ": the " + label + " descriptor cannot be restored or fall back to the legacy route");
            var after = await CommandStatusesAsync(rig);
            Check(Total(after) == Total(commands) &&
                  (after.TryGetValue("completed", out var now) ? now : 0) ==
                  (commands.TryGetValue("completed", out var before) ? before : 0),
                tag + ": the " + label + " refusal created no restore command and completed nothing");
            Check(rig.Wire.Writes.Total == writes && rig.Wire.Updates.Total == updates,
                tag + ": the " + label + " refusal issued no provider write effect");
            Check(Same(await ReadAsync(rig, spec, artifact.Path), destination),
                tag + ": the " + label + " refusal left the destination unchanged");
        }
    }

    private static async Task ChangedArchiveAsync(Rig rig, SyncPair pair, string tag, Artifact artifact,
        Spec left, Spec right)
    {
        var spec = Target(artifact, left, right);
        var destination = await ReadAsync(rig, spec, artifact.Path);
        var operations = (await OperationStatusesAsync(rig)).Values.Sum();
        var commands = await CommandStatusesAsync(rig);
        rig.Wire.ChangeRecoveryMember(artifact.Descriptor.RetainedFileId);
        var item = await ItemAsync(rig, pair.Id, artifact.Key);
        await RefusedAsync(() => rig.Host.RestoreRecoveryAsync(item, false),
            tag + ": a changed retained Recovery member is refused before any restore is attempted");
        var after = await CommandStatusesAsync(rig);
        Check(Total(after) == Total(commands) &&
              (after.TryGetValue("completed", out var completed) ? completed : 0) ==
              (commands.TryGetValue("completed", out var before) ? before : 0),
            tag + ": the changed-archive refusal created no restore command and completed nothing");
        Check((await OperationStatusesAsync(rig)).Values.Sum() == operations,
            tag + ": the changed-archive refusal created no restore operation and no private allocation");
        Check(Same(await ReadAsync(rig, spec, artifact.Path), destination),
            tag + ": the changed-archive refusal left the destination unchanged");
    }

    /// <summary>
    /// CC4E-ISSUE-3 correction: one interrupted retained read fails BEFORE any attempt is dispatched, so the
    /// operation must be abandoned as a proven no-effect refusal through the shared CC3A cleanup entry and the
    /// narrow leased-to-superseded terminal transition. Nothing is dispatched, deleted or completed - and the
    /// SAME pair must then complete a fresh explicit restore, proving no leased operation was stranded.
    /// </summary>
    private static async Task InterruptedThenFreshRestoreAsync(Rig rig, SyncPair pair, string tag,
        Artifact artifact, Spec left, Spec right)
    {
        var spec = Target(artifact, left, right);
        var destination = await ReadAsync(rig, spec, artifact.Path);
        var writes = rig.Wire.Writes.Total;
        var updates = rig.Wire.Updates.Total;
        var deletes = rig.Wire.RecoveryDeleteRequests;
        var supersedes = await NoEffectSupersedeCountAsync(rig);
        var completed = await CompletedAsync(rig);
        Check(artifact.Descriptor.Size > 0,
            tag + ": the interrupted-read fixture needs a nonempty retained member to interrupt");
        var retained = RetainedBytes(artifact.Descriptor);
        rig.Wire.RecoveryTransportStreamForTest = id =>
            id == artifact.Descriptor.RetainedFileId ? new FaultingReadStream(retained, 1) : null;
        try
        {
            var item = await ItemAsync(rig, pair.Id, artifact.Key);
            await RefusedAsync(() => rig.Host.RestoreRecoveryAsync(item, false),
                tag + ": a retained read that fails after its first bytes produces no verified stage");
        }
        finally
        {
            rig.Wire.RecoveryTransportStreamForTest = null;
        }
        Check(rig.Wire.Writes.Total == writes && rig.Wire.Updates.Total == updates,
            tag + ": the interrupted read issued no provider write effect");
        Check(rig.Wire.RecoveryDeleteRequests == deletes,
            tag + ": the interrupted read deleted nothing");
        Check(await LatestCommandStatusAsync(rig) == "blocked",
            tag + ": the interrupted read left its command blocked");
        Check(Same(await ReadAsync(rig, spec, artifact.Path), destination),
            tag + ": the interrupted read left the destination unchanged");
        Check(await CompletedAsync(rig) == completed,
            tag + ": the interrupted read completed no new restore operation");
        var abandoned = await LatestRestoreOperationAsync(rig);
        Check(await OperationStatusAsync(rig, abandoned.OperationId) == "superseded",
            tag + ": the pre-dispatch failure reached the existing leased-to-superseded terminal boundary");
        Check(await AttemptCountAsync(rig, abandoned.OperationId) == 0,
            tag + ": the abandoned operation dispatched no attempt of any kind");
        Check(await NoEffectSupersedeCountAsync(rig) == supersedes + 1,
            tag + ": exactly one no-effect abandonment was recorded for the interrupted read");
        Check(await rig.ScalarAsync(
                  "SELECT count(*) FROM operation WHERE kind='reconcile' AND status='leased';") == "0",
            tag + ": no manual restore operation was left leased and blocking this pair");
        var fresh = await rig.Host.RestoreRecoveryAsync(await ItemAsync(rig, pair.Id, artifact.Key), false);
        Check(fresh.TargetRelativePath == artifact.Path &&
              Same(await ReadAsync(rig, spec, artifact.Path), retained),
            tag + ": the same pair completed a fresh restore after the no-effect abandonment");
        Check(await CompletedAsync(rig) == completed + 1 && await LatestCommandStatusAsync(rig) == "completed",
            tag + ": the fresh restore completed exactly one operation and its command");
    }

    private static async Task<bool> ChangedTargetAsync(Rig rig, SyncPair pair, string tag, Artifact artifact,
        Spec left, Spec right)
    {
        var spec = Target(artifact, left, right);
        var item = await ItemAsync(rig, pair.Id, artifact.Key);
        var destination = await ReadAdapterAsync(rig, spec, artifact.Path);
        var inventory = (await InventoryAsync(rig, spec)).Order(StringComparer.Ordinal).ToArray();
        var updates = rig.Wire.Updates;
        var writes = rig.Wire.Writes.Total;
        var deletes = rig.Wire.RecoveryDeleteRequests;
        SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = phase =>
        {
            if (phase == "request-dispatched-before-upload") rig.Wire.ChangeRecoveryMember(destination.Result.FileId!);
        };
        try
        {
            var refusal = await RefusedAsync(() => rig.Host.RestoreRecoveryAsync(item, false),
                tag + ": a destination that changed after its verification is never replaced");
            Console.WriteLine("  " + tag + " changed-target refusal: " + refusal.GetType().Name + ": " +
                              refusal.Message);
        }
        finally
        {
            SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = null;
        }
        var after = rig.Wire.Updates;
        Console.WriteLine("  " + tag + " changed-target observation: updates " +
                          updates.DropboxUpdates + "/" + updates.DriveUpdates + " -> " + after.DropboxUpdates + "/" +
                          after.DriveUpdates + ", recovery copies " + updates.DropboxRecoveryCopies + "/" +
                          updates.DriveRecoveryCopies + " -> " + after.DropboxRecoveryCopies + "/" +
                          after.DriveRecoveryCopies + ", creates " + writes + " -> " + rig.Wire.Writes.Total +
                          ", statuses " + string.Join(",",
                              (await OperationStatusesAsync(rig)).OrderBy(entry => entry.Key,
                                  StringComparer.Ordinal).Select(entry => entry.Key + "=" + entry.Value)));
        if (spec.Kind == SyncEndpointKind.Dropbox)
            Check(after.DropboxUpdates == updates.DropboxUpdates + 1,
                tag + ": the conditional replacement request was received and refused by the provider");
        else
            Check(after.DriveUpdates == updates.DriveUpdates,
                tag + ": the Drive adapter proved the changed destination before issuing any write request");
        Check(after.DropboxRecoveryCopies + after.DriveRecoveryCopies ==
              updates.DropboxRecoveryCopies + updates.DriveRecoveryCopies + 1,
            tag + ": the destination preimage was retained before the refused request");
        Check((await InventoryAsync(rig, spec)).Order(StringComparer.Ordinal).SequenceEqual(inventory),
            tag + ": the refused request created no new object in the target's own root");
        Check(rig.Wire.RecoveryDeleteRequests == deletes, tag + ": the refused request deleted nothing");
        Check(Same(await ReadAsync(rig, spec, artifact.Path), destination.Bytes),
            tag + ": the refused request left the destination's own bytes unchanged");
        var statuses = await OperationStatusesAsync(rig);
        Check(statuses.ContainsKey("effect_unknown") || statuses.ContainsKey("blocked"),
            tag + ": the unknown restore outcome is retained as unresolved, never as completed");
        Check(await LatestCommandStatusAsync(rig) == "blocked",
            tag + ": the unknown restore outcome left its command blocked");
        var runUpdates = rig.Wire.Updates.Total;
        var run = await rig.Host.RunPairsAsync(new[] { pair }, "manual");
        var reportedBlocked = run.Items.Count == 1 && run.Items[0].Result is { Blocked: >= 1 };
        var refusedToRun = run.Items.Count == 1 && run.Items[0].FailureMessage is not null;
        Console.WriteLine("  " + tag + " ordinary-run observation: blocked=" + reportedBlocked + ", refusal=" +
                          (run.Items.FirstOrDefault()?.FailureMessage ?? "none"));
        Check(reportedBlocked || refusedToRun,
            tag + ": the ordinary runner never silently proceeds past the unresolved manual restore");
        Check(rig.Wire.Updates.Total == runUpdates,
            tag + ": the ordinary runner reissued no conditional replacement for the unresolved restore");
        Check(await SupersededRestoreCountAsync(rig) == 0 && await NoEffectSupersedeCountAsync(rig) == 0,
            tag + ": an unresolved dispatched request is never abandoned as a no-effect refusal");
        return true;
    }

    private static async Task<bool> PostEffectMismatchAsync(Rig rig, SyncPair pair, string tag, Artifact artifact,
        Spec left, Spec right)
    {
        var spec = Target(artifact, left, right);
        var item = await ItemAsync(rig, pair.Id, artifact.Key);
        var destination = await ReadAdapterAsync(rig, spec, artifact.Path);
        var updates = rig.Wire.Updates.Total;
        SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = phase =>
        {
            if (phase != "after-upload-before-readback") return;
            if (spec.Kind == SyncEndpointKind.Dropbox)
                rig.Wire.FaultDropboxRecoveryMember(destination.Result.FileId!,
                    CloudPairHostBindingChecks.HostWire.RecoveryMemberFault.NativeId);
            else
                rig.Wire.FaultDriveRecoveryMember(destination.Result.FileId!,
                    CloudPairHostBindingChecks.HostWire.RecoveryMemberFault.NativeId);
        };
        try
        {
            await RefusedAsync(() => rig.Host.RestoreRecoveryAsync(item, false),
                tag + ": a post-effect destination readback mismatch is never committed as success");
        }
        finally
        {
            SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = null;
            if (spec.Kind == SyncEndpointKind.Dropbox)
                rig.Wire.ClearDropboxRecoveryMemberFault(destination.Result.FileId!);
            else
                rig.Wire.ClearDriveRecoveryMemberFault(destination.Result.FileId!);
        }
        Check(rig.Wire.Updates.Total == updates + 2,
            tag + ": the mismatching restore issued exactly its preimage and its one replacement request");
        Check(Same(await ReadAsync(rig, spec, artifact.Path), RetainedBytes(artifact.Descriptor)),
            tag + ": the applied replacement bytes are retained as the observed outcome of the mismatch");
        var statuses = await OperationStatusesAsync(rig);
        Check(statuses.ContainsKey("effect_unknown") || statuses.ContainsKey("blocked"),
            tag + ": the mismatching readback left the restore unresolved rather than completed");
        Check(await LatestCommandStatusAsync(rig) == "blocked",
            tag + ": the mismatching readback left its command blocked");
        Check(await SupersededRestoreCountAsync(rig) == 0 && await NoEffectSupersedeCountAsync(rig) == 0,
            tag + ": a mismatching post-effect readback is never abandoned as a no-effect refusal");
        return true;
    }

    private static async Task<bool> CancelledAfterDispatchAsync(Rig rig, SyncPair pair, string tag,
        Artifact artifact,
        Spec left, Spec right)
    {
        var spec = Target(artifact, left, right);
        var item = await ItemAsync(rig, pair.Id, artifact.Key);
        var writes = rig.Wire.Writes.Total;
        using var cancellation = new CancellationTokenSource();
        SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = phase =>
        {
            if (phase == "request-dispatched-before-upload") cancellation.Cancel();
        };
        try
        {
            try
            {
                await rig.Host.RestoreRecoveryAsync(item, true, cancellation.Token);
                throw new InvalidOperationException("A cancelled restore was accepted: " + tag);
            }
            catch (OperationCanceledException)
            {
                Check(true, tag + ": a cancellation after the dispatched request is never reported as success");
            }
        }
        finally
        {
            SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = null;
        }
        Check(rig.Wire.Writes.Total == writes,
            tag + ": the cancelled restore issued no create write and left no new object");
        Check(await LatestCommandStatusAsync(rig) == "blocked",
            tag + ": the cancelled restore left its command blocked");
        var statuses = await OperationStatusesAsync(rig);
        Check(!statuses.ContainsKey("committed"),
            tag + ": the cancelled restore committed nothing and its uncertainty is retained");
        Check(await SupersededRestoreCountAsync(rig) == 0 && await NoEffectSupersedeCountAsync(rig) == 0,
            tag + ": a cancellation after the dispatched request is retained, never abandoned as a no-effect refusal");
        _ = spec;
        return true;
    }

    /// <summary>One provider entry resolved again by its exact native id, with the bytes it serves.</summary>
    private static async Task<(PilotRemoteEntry Entry, byte[] Bytes)?> ReadEntryByIdAsync(Rig rig, Spec spec,
        string fileId)
    {
        var store = Store(rig, spec);
        await store.VerifyRootIdentityAsync();
        var entries = await store.ListEntriesAsync();
        await store.VerifyRootIdentityAsync();
        var entry = entries.FirstOrDefault(item => string.Equals(item.Id, fileId, StringComparison.Ordinal));
        if (entry is null) return null;
        using var destination = new MemoryStream();
        await store.DownloadAsync(entry, destination);
        return (entry, destination.ToArray());
    }

    /// <summary>
    /// CC4E-ISSUE-1 correction: the bounded HostWire upload decline seam drives the REAL Host, the real adapter,
    /// the real private stage and real SQLite through the existing conclusive-non-application path. The retained
    /// predecessor, the destination and the command are all preserved, nothing is retried, and the refusal is
    /// durably recorded as known-not-applied rather than as an unknown effect.
    /// </summary>
    private static async Task<bool> UnsupportedConditionalWriteAsync(Rig rig, SyncPair pair, string tag,
        Artifact artifact, Spec left, Spec right)
    {
        var spec = Target(artifact, left, right);
        var destination = await ReadAdapterAsync(rig, spec, artifact.Path);
        var inventory = (await InventoryAsync(rig, spec)).Order(StringComparer.Ordinal).ToArray();
        // The destination is hidden from the target's own listing so this restore takes the create-only path;
        // the transport still holds the object and the armed seam declines the request before any mutation.
        // The SAME default-off seam is armed for a Dropbox and for a Drive destination, so the
        // unsupported-conditional-write path is proved through one shared path for every provider rather
        // than through a pair-specific branch.
        var dropboxTarget = spec.Kind == SyncEndpointKind.Dropbox;
        if (dropboxTarget) rig.Wire.SetDropboxMissing(destination.Result.FileId!, true);
        else rig.Wire.HideDriveNodeUntilNextCreate = destination.Result.FileId;
        rig.Wire.DeclineNextUploadForPath = artifact.Path;
        var uploads = dropboxTarget ? rig.Wire.DropboxUploadRequests : rig.Wire.DriveCreateRequests;
        var writes = rig.Wire.Writes.Total;
        var updates = rig.Wire.Updates;
        var deletes = rig.Wire.RecoveryDeleteRequests;
        try
        {
            var item = await ItemAsync(rig, pair.Id, artifact.Key);
            var refusal = await RefusedAsync(() => rig.Host.RestoreRecoveryAsync(item, false),
                tag + ": an adapter that declares the conditional write unsupported is refused, not applied");
            Check(refusal.Message.Contains("cannot perform this conditional restore", StringComparison.Ordinal),
                tag + ": the refusal carries the durable known-not-applied classification");
        }
        finally
        {
            if (dropboxTarget) rig.Wire.SetDropboxMissing(destination.Result.FileId!, false);
            else rig.Wire.HideDriveNodeUntilNextCreate = null;
        }
        Check((dropboxTarget ? rig.Wire.DropboxUploadRequests : rig.Wire.DriveCreateRequests) == uploads + 1,
            tag + ": exactly one upload request reached the transport boundary and was declined there");
        Check(rig.Wire.Writes.Total == writes + 1,
            tag + ": exactly one write request was received at the transport boundary and declined there");
        Check(rig.Wire.Updates.Total == updates.Total &&
              rig.Wire.Updates.DropboxUpdates == updates.DropboxUpdates &&
              rig.Wire.Updates.DropboxRecoveryCopies == updates.DropboxRecoveryCopies,
            tag + ": the declined write issued no conditional replacement and retained no preimage");
        Check((await InventoryAsync(rig, spec)).Order(StringComparer.Ordinal).SequenceEqual(inventory),
            tag + ": the declined write added no object to the target's own root");
        Check(rig.Wire.RecoveryDeleteRequests == deletes, tag + ": the declined write deleted nothing");
        var operation = await LatestRestoreOperationAsync(rig);
        Check(await rig.ScalarAsync(
                  "SELECT count(*) FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id " +
                  "WHERE a.operation_id='" + operation.OperationId + "' AND r.certainty='known_not_applied';") == "1",
            tag + ": the declined restore holds exactly one durable known-not-applied receipt");
        Check(await rig.ScalarAsync("SELECT status FROM operation_attempt WHERE operation_id='" +
                                    operation.OperationId + "';") == "blocked",
            tag + ": the declined restore attempt is durably blocked");
        Check(await AttemptCountAsync(rig, operation.OperationId) == 1,
            tag + ": no second attempt and no retry were issued for the declined restore");
        Check(await OperationStatusAsync(rig, operation.OperationId) == "blocked",
            tag + ": the declined restore operation is blocked, never completed");
        Check(await CompletedAsync(rig) == 0 && await SupersededRestoreCountAsync(rig) == 0 &&
              await NoEffectSupersedeCountAsync(rig) == 0,
            tag + ": the declined restore reported no success and was not abandoned as a no-effect refusal");
        var original = await ReadAdapterAsync(rig, spec, artifact.Path);
        Check(Same(original.Bytes, destination.Bytes) &&
              original.Result.FileId == destination.Result.FileId,
            tag + ": the destination object kept its exact native id and bytes");
        var retained = await RetainedMemberAsync(rig, spec, artifact.Descriptor);
        Check(retained.Member is not null && retained.Member.Size == artifact.Descriptor.Size &&
              string.Equals(retained.Member.ContentHash, artifact.Descriptor.ContentHash,
                  StringComparison.OrdinalIgnoreCase) &&
              Same(retained.Bytes, RetainedBytes(artifact.Descriptor)),
            tag + ": the exact retained predecessor is preserved and still recoverable");
        return true;
    }

    /// <summary>
    /// CC4E-ISSUE-3 correction: a cancellation landing AFTER the operation is leased but BEFORE any attempt is
    /// dispatched is a pre-dispatch failure. It must be recorded as a no-effect refusal under a bounded
    /// NONCANCELLED cleanup token - the original cancellation must never suppress its own recording - and the
    /// same pair must still complete a fresh explicit restore.
    /// </summary>
    /// <summary>
    /// CC4E closure (item A proof): the bounded abandonment window is itself bounded, so an abandonment that
    /// expires before its own cleanup completes cannot take the leased-to-superseded transition, and it never
    /// replaces the original failure. ONLY the window is shortened, through the default-off test seam; the real
    /// cleanup, the real zero-attempt guard and the real database decision all still run.
    /// </summary>
    private static async Task<bool> AbandonWindowExpiryAsync(Rig rig, SyncPair pair, string tag,
        Artifact artifact, Spec left, Spec right)
    {
        var spec = Target(artifact, left, right);
        var destination = await ReadAsync(rig, spec, artifact.Path);
        var writes = rig.Wire.Writes.Total;
        var deletes = rig.Wire.RecoveryDeleteRequests;
        var supersedes = await NoEffectSupersedeCountAsync(rig);
        var completed = await CompletedAsync(rig);
        using var cancellation = new CancellationTokenSource();
        SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = phase =>
        {
            if (phase == "operation-leased-before-stage") cancellation.Cancel();
        };
        // One tick cannot contain the abandonment's authority load, its shared cleanup, its fresh lease or its
        // supersede write, so this proves the expiry path rather than a mock of it.
        SyncAuthorityRuntimeHost.AbandonCleanupWindowForTest = TimeSpan.FromTicks(1);
        try
        {
            var item = await ItemAsync(rig, pair.Id, artifact.Key);
            try
            {
                await rig.Host.RestoreRecoveryAsync(item, false, cancellation.Token);
                throw new InvalidOperationException("An expired-abandonment restore was accepted: " + tag);
            }
            catch (OperationCanceledException)
            {
                Check(true, tag + ": the ORIGINAL pre-dispatch failure still surfaces when the abandonment expires");
            }
        }
        finally
        {
            SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = null;
            SyncAuthorityRuntimeHost.AbandonCleanupWindowForTest = TimeSpan.Zero;
        }
        Check(rig.Wire.Writes.Total == writes && rig.Wire.RecoveryDeleteRequests == deletes,
            tag + ": the expired abandonment issued no write and no delete");
        Check(await NoEffectSupersedeCountAsync(rig) == supersedes,
            tag + ": an expired abandonment did NOT take the leased-to-superseded transition");
        var retained = await LatestRestoreOperationAsync(rig);
        Check(await OperationStatusAsync(rig, retained.OperationId) != "superseded" &&
              await AttemptCountAsync(rig, retained.OperationId) == 0,
            tag + ": the unproved cleanup left the operation retained with zero attempts rather than superseded");
        Check(Same(await ReadAsync(rig, spec, artifact.Path), destination),
            tag + ": the expired abandonment left the destination unchanged");
        Check(await LatestCommandStatusAsync(rig) == "blocked",
            tag + ": the expired abandonment left the command blocked, never completed");
        Check(await CompletedAsync(rig) == completed,
            tag + ": the expired abandonment completed nothing");
        return true;
    }


    private static async Task<bool> CancelledBeforeDispatchAsync(Rig rig, SyncPair pair, string tag,
        Artifact artifact, Spec left, Spec right)
    {
        var spec = Target(artifact, left, right);
        var destination = await ReadAsync(rig, spec, artifact.Path);
        var writes = rig.Wire.Writes.Total;
        var deletes = rig.Wire.RecoveryDeleteRequests;
        var supersedes = await NoEffectSupersedeCountAsync(rig);
        var completed = await CompletedAsync(rig);
        using var cancellation = new CancellationTokenSource();
        SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = phase =>
        {
            if (phase == "operation-leased-before-stage") cancellation.Cancel();
        };
        try
        {
            var item = await ItemAsync(rig, pair.Id, artifact.Key);
            try
            {
                await rig.Host.RestoreRecoveryAsync(item, false, cancellation.Token);
                throw new InvalidOperationException("A cancelled restore was accepted: " + tag);
            }
            catch (OperationCanceledException)
            {
                Check(true, tag + ": a cancellation before any request is a refusal");
            }
        }
        finally
        {
            SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = null;
        }
        Check(rig.Wire.Writes.Total == writes && rig.Wire.RecoveryDeleteRequests == deletes,
            tag + ": the cancelled pre-dispatch restore issued no write and no delete");
        Check(await LatestCommandStatusAsync(rig) == "blocked",
            tag + ": the cancelled pre-dispatch restore left its command blocked");
        Check(Same(await ReadAsync(rig, spec, artifact.Path), destination),
            tag + ": the cancelled pre-dispatch restore left the destination unchanged");
        var abandoned = await LatestRestoreOperationAsync(rig);
        Check(await OperationStatusAsync(rig, abandoned.OperationId) == "superseded" &&
              await AttemptCountAsync(rig, abandoned.OperationId) == 0,
            tag + ": the cancellation before dispatch was recorded as a proven no-effect abandonment");
        Check(await NoEffectSupersedeCountAsync(rig) == supersedes + 1 && await CompletedAsync(rig) == completed,
            tag + ": the cancelled pre-dispatch attempt completed nothing and recorded one no-effect abandonment");
        var fresh = await rig.Host.RestoreRecoveryAsync(await ItemAsync(rig, pair.Id, artifact.Key), false);
        Check(fresh.TargetRelativePath == artifact.Path &&
              Same(await ReadAsync(rig, spec, artifact.Path), RetainedBytes(artifact.Descriptor)) &&
              await CompletedAsync(rig) == completed + 1,
            tag + ": the same pair completed a fresh restore after the cancelled pre-dispatch attempt");
        return true;
    }

    /// <summary>
    /// CC4E-ISSUE-2 correction: Google names are not unique inside a folder, so a real same-name create can land
    /// beside an object the adapter's own preflight could not see. The bounded one-shot window hides the existing
    /// sibling during preflight, lets the REAL create produce a NEW native id, and serves both objects again during
    /// the successor readback. Expected: exactly one create, the original sibling's bytes and id unchanged, the
    /// ambiguity failing closed, a retained unresolved outcome, no completed command and no automatic second
    /// create, delete or rename. The same fixture then proves the existing visible duplicate-name refusal.
    /// </summary>
    private static async Task<bool> HiddenNameRaceAsync(Rig rig, SyncPair pair, string tag, Artifact artifact,
        Spec left, Spec right)
    {
        var spec = Target(artifact, left, right);
        if (spec.Kind != SyncEndpointKind.GoogleDrive)
            throw new InvalidOperationException(tag + ": Drive duplicate-name case received a non-Drive target.");
        var original = await ReadAdapterAsync(rig, spec, artifact.Path);
        var creates = rig.Wire.DriveCreateRequests;
        var multipart = rig.Wire.DriveMultipartCreateRequests;
        var deletes = rig.Wire.RecoveryDeleteRequests;
        var updates = rig.Wire.Updates.Total;
        var inventory = (await InventoryAsync(rig, spec)).Length;
        rig.Wire.HideDriveNodeUntilNextCreate = original.Result.FileId!;
        var racedItem = await ItemAsync(rig, pair.Id, artifact.Key);
        try
        {
            await RefusedAsync(() => rig.Host.RestoreRecoveryAsync(racedItem, false),
                tag + ": a same-name race is failed closed instead of reporting one verified successor");
        }
        finally
        {
            rig.Wire.HideDriveNodeUntilNextCreate = null;
        }
        Check(rig.Wire.DriveCreateRequests == creates + 1 && rig.Wire.DriveMultipartCreateRequests == multipart + 1,
            tag + ": exactly one real create was served for the raced destination");
        Check(rig.Wire.RecoveryDeleteRequests == deletes && rig.Wire.Updates.Total == updates,
            tag + ": the raced restore deleted nothing and issued no conditional replacement");
        _ = inventory;
        // The original sibling is read back by its own native id: the raced create produced a NEW id and never
        // changed, renamed or deleted the original. The adapter's own inventory now refuses the ambiguous path,
        // which is itself the fail-closed observation this case requires.
        var originalEntry = new PilotRemoteEntry(artifact.Path, PilotEntryKind.File, original.Result.FileId,
            original.Result.Revision, original.Result.Size, original.Result.ContentHash);
        using (var readback = new MemoryStream())
        {
            await Store(rig, spec).DownloadAsync(originalEntry, readback);
            Check(Same(readback.ToArray(), original.Bytes),
                tag + ": the original same-name sibling kept its exact native id, path and bytes");
        }
        var raced = await LatestRestoreOperationAsync(rig);
        Check((await OperationStatusAsync(rig, raced.OperationId) is "effect_unknown" or "blocked") &&
              await AttemptCountAsync(rig, raced.OperationId) == 1 &&
              await SupersededRestoreCountAsync(rig) == 0,
            tag + ": the ambiguous outcome stayed unresolved with one dispatched attempt and never completed");
        Check(await rig.ScalarAsync(
                  "SELECT count(*) FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id " +
                  "WHERE a.operation_id='" + raced.OperationId +
                  "' AND r.provider_metadata_json LIKE '%ambiguous%';") == "1",
            tag + ": the same-name ambiguity reached the adapter's own fail-closed inventory refusal");
        Check(await CompletedAsync(rig) == 0 && await LatestCommandStatusAsync(rig) == "blocked",
            tag + ": the raced restore completed nothing and left its command blocked");
        var retained = await RetainedMemberAsync(rig, spec, artifact.Descriptor);
        Check(retained.Member is not null && Same(retained.Bytes, RetainedBytes(artifact.Descriptor)),
            tag + ": the retained predecessor is still exact after the raced create");
        using var stream = new MemoryStream(RetainedBytes(artifact.Descriptor));
        var createsBefore = rig.Wire.DriveCreateRequests;
        var refusal = await RefusedAsync(() => Store(rig, spec).UploadAsync(artifact.Path, stream,
                NativeDigest(spec, RetainedBytes(artifact.Descriptor)), original.Result.Revision),
            tag + ": a visible duplicate name is refused by the adapter's own ambiguity check");
        Check(refusal.Message.Contains("more than one item named", StringComparison.Ordinal) ||
              refusal.Message.Contains("ambiguous paths", StringComparison.Ordinal),
            tag + ": the refusal is the adapter's own duplicate-name message");
        Check(rig.Wire.DriveCreateRequests == createsBefore,
            tag + ": the visible duplicate-name refusal issued no write request");
        return true;
    }

    /// <summary>
    /// CC4E-ISSUE-3 correction: the narrow no-effect transition must refuse when the failure is not provably an
    /// untouched pre-dispatch refusal. This case obtains a genuinely leased, zero-attempt operation whose remote
    /// recovery allocation a fixture state change advanced to verified, so the flow's own abandonment is refused
    /// and the operation is retained; the real helper is then called directly to prove that its exact private
    /// cleanup clause and its advanced-recovery-allocation clause each refuse without changing anything.
    /// </summary>
    private static async Task<bool> GuardedRejectionAsync(Rig rig, SyncPair pair, string tag, Artifact artifact,
        Spec left, Spec right)
    {
        var spec = Target(artifact, left, right);
        var destination = await ReadAsync(rig, spec, artifact.Path);
        var retained = RetainedBytes(artifact.Descriptor);
        rig.Wire.RecoveryTransportStreamForTest = id =>
            id == artifact.Descriptor.RetainedFileId ? new FaultingReadStream(retained, 1) : null;
        SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = phase =>
        {
            if (phase != "operation-leased-before-stage") return;
            AdvanceRecoveryAllocationForTestAsync(rig, 1).GetAwaiter().GetResult();
        };
        var guardItem = await ItemAsync(rig, pair.Id, artifact.Key);
        try
        {
            await RefusedAsync(() => rig.Host.RestoreRecoveryAsync(guardItem, false),
                tag + ": the interrupted read failed before any attempt was dispatched");
        }
        finally
        {
            rig.Wire.RecoveryTransportStreamForTest = null;
            SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = null;
        }
        var operation = await LatestRestoreOperationAsync(rig);
        Check(await OperationStatusAsync(rig, operation.OperationId) == "leased" &&
              await AttemptCountAsync(rig, operation.OperationId) == 0 &&
              await NoEffectSupersedeCountAsync(rig) == 0,
            tag + ": an operation whose remote recovery allocation advanced is retained leased, never abandoned");
        Check(await LatestCommandStatusAsync(rig) == "blocked",
            tag + ": the retained unprovable operation still left its command blocked");
        Check(Same(await ReadAsync(rig, spec, artifact.Path), destination),
            tag + ": the retained operation changed no destination");
        var controlNamespace = await rig.ScalarAsync(
            "SELECT control_namespace_id FROM pair_control_namespace WHERE pair_id='" + pair.Id +
            "' AND status='active';");
        var transactionName = await rig.ScalarAsync(
            "SELECT deterministic_name FROM resource_allocation WHERE operation_id='" + operation.OperationId +
            "' AND resource_kind='transaction_directory';");
        var stageName = await rig.ScalarAsync(
            "SELECT deterministic_name FROM resource_allocation WHERE operation_id='" + operation.OperationId +
            "' AND resource_kind='stage_file';");
        var transactionPath = Path.Combine(rig.StateRoot,
            SyncControlNamespace.LocalPrefix + controlNamespace, transactionName);
        var stagePath = Path.Combine(transactionPath, stageName);
        var removed = !File.Exists(stagePath) && !Directory.Exists(transactionPath);
        Check(removed, tag + ": the flow's own exact private cleanup removed the owned stage and transaction");
        var lease = await rig.Db.AcquireOperationLeaseAsync(operation.OperationId, "cc4e-guard",
            TimeSpan.FromMinutes(5));
        try
        {
            var cleanupRefusal = await RefusedAsync(() => rig.Db.SupersedeNoEffectManualRestoreAsync(
                    operation.OperationId, lease, operation.CommandId, operation.ArtifactId, operation.Path,
                    cleaned: false, stageRemoved: removed, transactionRemoved: removed, residualCount: 0,
                    detailJson: "{\"case\":\"guard-cleanup\"}"),
                tag + ": an unproven private cleanup is refused by the no-effect transition");
            Check(cleanupRefusal.Message.Contains("exact private cleanup", StringComparison.Ordinal),
                tag + ": the refusal names the exact private cleanup requirement");
            var allocationRefusal = await RefusedAsync(() => rig.Db.SupersedeNoEffectManualRestoreAsync(
                    operation.OperationId, lease, operation.CommandId, operation.ArtifactId, operation.Path,
                    cleaned: true, stageRemoved: removed, transactionRemoved: removed, residualCount: 0,
                    detailJson: "{\"case\":\"guard-allocation\"}"),
                tag + ": an advanced remote recovery allocation is refused by the no-effect transition");
            Check(allocationRefusal.Message.Contains("advanced a remote recovery allocation",
                    StringComparison.Ordinal),
                tag + ": the refusal names the advanced remote recovery allocation");
        }
        finally
        {
            await rig.Db.ReleaseLeaseAsync(lease);
        }
        Check(await OperationStatusAsync(rig, operation.OperationId) == "leased" &&
              await NoEffectSupersedeCountAsync(rig) == 0 &&
              await AttemptCountAsync(rig, operation.OperationId) == 0,
            tag + ": both guarded refusals left the operation retained, leased and unmodified");
        return true;
    }

    /// <summary>A bounded transport that yields one chunk and then fails, as the shared stage proofs use.</summary>
    private sealed class FaultingReadStream(byte[] bytes, int firstChunk) : Stream
    {
        private bool _yielded;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.LongLength;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Yield(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Yield(buffer.Span));

        private int Yield(Span<byte> buffer)
        {
            if (_yielded)
                throw new IOException("Synthetic transport interruption after the first bytes were read.");
            _yielded = true;
            var take = Math.Min(Math.Min(firstChunk, buffer.Length), bytes.Length);
            bytes.AsSpan(0, take).CopyTo(buffer);
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// The real CLI operations runtime over this fixture's own Host and state authority, so the existing
    /// <c>recovery restore</c> command is dispatched by the production registry rather than a test copy of it.
    /// </summary>
    private sealed class RigCliOperations(SyncAuthorityRuntimeHost host, PilotStateAuthority state)
        : SyncCliOperationsBase
    {
        protected override SyncAuthorityRuntimeHost Host { get; } = host;
        protected override PilotStateAuthority StateAuthority { get; } = state;
        public override string Mode => "exclusive-product-lock";
    }
}
