using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Massdex.SyncShark;
using Rig = CloudPairHostBindingChecks.HostRig;
using Spec = CloudPairHostBindingChecks.EndpointSpec;
using Wire = CloudPairHostBindingChecks.HostWire;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class CloudPublicPairAuthoringChecks
{
    private static int _assertions;

    internal static async Task RunAsync(string root)
    {
        root = PrepareFixtureRoot(root);
        var previous = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            foreach (var leftKind in new[] { SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
            foreach (var rightKind in new[] { SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
                await BindReviewRunAsync(leftKind, rightKind);
            await RebindAndEditAsync();
            await RecoveredHostEntryAsync();
        await RecoveredHostSuccessAsync();
        await RecoveredHostRestartAsync();
        await RecoveredHostCliEntryAsync();
            await LocalReplacementConsumersAsync();
            await LocalCloudGooglePolicyAtomicAsync();
            await PolicyPublicationOrientationAsync();
            await UnselectedPublicPolicyEditAsync();
            await LocalLocalPublicPolicyEditAsync();
            await RunCliCommandChecksAsync(Path.Combine(root, "cli"));
            Console.WriteLine("PASS public cloud pair authoring: " + _assertions + " assertions");
        }
        finally { SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = previous; }
    }

    /// <summary>
    /// RC639-5: a dedicated entry point so the shared Host recovery entry can be exercised in isolation
    /// without depending on the whole public cloud authoring matrix.
    /// </summary>
    internal static async Task RunRecoveredHostEntryAsync(string root)
    {
        PrepareFixtureRoot(root);
        await RecoveredHostEntryAsync();
        await RecoveredHostSuccessAsync();
        await RecoveredHostRestartAsync();
        await RecoveredHostCliEntryAsync();

        Console.WriteLine("PASS recovered host entry: " + _assertions + " assertions");
    }

    internal static async Task RunLocalReplacementChecksAsync(string root, bool localOnly = false)
    {
        PrepareFixtureRoot(root);
        if (localOnly) await LocalPairReplacementConsumersAsync();
        else await LocalReplacementConsumersAsync();
        Console.WriteLine("PASS public " + (localOnly ? "local/local" : "local/account") +
                          " replacement consumers: " + _assertions + " assertions");
    }

    // Every matrix row calls the public binding, review and ordinary-run entry points.
    // Only cloud HTTP responses are inputs supplied by the existing transport fixture;
    // native identities, authority, planning and effects are never replaced by a fake.
    internal static async Task RunEndpointModeMatrixAsync(string root)
    {
        PrepareFixtureRoot(root);
        var previous = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            var kinds = new[] { SyncEndpointKind.Local, SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive };
            foreach (var leftKind in kinds)
            foreach (var rightKind in kinds)
            foreach (var mode in Enum.GetValues<SyncMode>())
                await BindReviewRunMatrixAsync(leftKind, rightKind, mode);
            await LocalCloudObservationRootRejectionsAsync();
            Console.WriteLine("PASS public endpoint/mode matrix: 27 cases; " + _assertions + " assertions");
        }
        finally { SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = previous; }
    }

    /// <summary>
    /// r30: the bounded runnable subset of the full endpoint/mode matrix. It drives the five ordered
    /// TwoWay combinations that contain at least one local endpoint plus the GoogleDrive-left/
    /// Dropbox-right cloud pair, then the local/cloud observation-root refusals. The full 27-case
    /// route (RunEndpointModeMatrixAsync) remains available and unchanged.
    /// </summary>
    internal static async Task RunEndpointModeSubsetAsync(string root)
    {
        PrepareFixtureRoot(root);
        var previous = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            foreach (var (leftKind, rightKind) in TwoWayLocalSubset)
                await BindReviewRunMatrixAsync(leftKind, rightKind, SyncMode.TwoWay);
            await LocalCloudObservationRootRejectionsAsync();
            Console.WriteLine(
                "PASS public endpoint/mode subset: 6 ordered TwoWay cases (the 5 combinations with at least one " +
                "local endpoint plus GoogleDrive-left/Dropbox-right) followed by the local/cloud " +
                "observation-root rejection check; " + _assertions + " assertions");
        }
        finally { SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = previous; }
    }

    // The bounded r30 subset in its exact comparison order. The full matrix keeps its own
    // enumeration inside RunEndpointModeMatrixAsync.
    private static readonly (SyncEndpointKind Left, SyncEndpointKind Right)[] TwoWayLocalSubset = new[]
    {
        (SyncEndpointKind.Local, SyncEndpointKind.Local),
        (SyncEndpointKind.Local, SyncEndpointKind.Dropbox),
        (SyncEndpointKind.Local, SyncEndpointKind.GoogleDrive),
        (SyncEndpointKind.Dropbox, SyncEndpointKind.Local),
        (SyncEndpointKind.GoogleDrive, SyncEndpointKind.Local),
        (SyncEndpointKind.GoogleDrive, SyncEndpointKind.Dropbox)
    };

    private sealed record MatrixPort(SyncEndpoint Endpoint, Spec? Cloud, string? LocalRoot);

    private static async Task<MatrixPort> MatrixPortAsync(Rig rig, SyncEndpointKind kind, string tag)
    {
        if (kind == SyncEndpointKind.Local)
        {
            var parent = Directory.GetParent(rig.StateRoot)!.FullName;
            var path = Directory.CreateDirectory(Path.Combine(parent, tag)).FullName;
            return new MatrixPort(NativeLocalPort(path), null, path);
        }
        var cloud = Endpoint(kind, tag);
        await rig.AddEndpointAsync(cloud);
        rig.RegisterFolder(cloud, Rig.RecoveryContainerPath,
            kind == SyncEndpointKind.Dropbox ? "id:matrix-recovery-" + tag : "drive-matrix-recovery-" + tag);
        return new MatrixPort(Port(cloud), cloud, null);
    }

    private static async Task SeedMatrixFileAsync(Rig rig, MatrixPort port, string path, string text)
    {
        if (port.LocalRoot is { } local) await File.WriteAllTextAsync(Path.Combine(local, path), text);
        else rig.SeedFile(port.Cloud!, path, text);
    }

    private static async Task<byte[]?> ReadMatrixFileAsync(Rig rig, MatrixPort port, string path)
    {
        if (port.LocalRoot is { } local)
        {
            var file = Path.Combine(local, path);
            return File.Exists(file) ? await File.ReadAllBytesAsync(file) : null;
        }
        var cloud = port.Cloud!;
        if (cloud.Kind == SyncEndpointKind.Dropbox)
        {
            var entries = await rig.DropboxClient(cloud).ListEntriesAsync();
            if (!entries.Any(entry => entry.Kind == PilotEntryKind.File && entry.RelativePath == path)) return null;
            return (await rig.ReadDropboxFileAsync(cloud, path)).Bytes;
        }
        var inventory = await rig.DriveClient(cloud).ListInventoryAsync();
        if (!inventory.Entries.Any(entry => entry.Kind == GoogleDriveEntryKind.Blob && entry.RelativePath == path)) return null;
        return (await rig.ReadDriveFileAsync(cloud, path)).Bytes;
    }

    private static bool SameMatrixBytes(byte[]? expected, byte[]? actual) =>
        expected is null ? actual is null : actual is not null && actual.SequenceEqual(expected);

    private static async Task<(long Attempts, long Receipts)> MatrixEffectCountsAsync(Rig rig, string pairId)
    {
        await using var read = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = rig.Db.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
        await read.OpenAsync();
        await using var command = read.CreateCommand();
        command.CommandText = """
            SELECT (SELECT count(*) FROM operation_attempt a JOIN operation o ON o.operation_id=a.operation_id WHERE o.pair_id=$pair),
                   (SELECT count(*) FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id
                    JOIN operation o ON o.operation_id=a.operation_id WHERE o.pair_id=$pair);
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        await using var rows = await command.ExecuteReaderAsync();
        if (!await rows.ReadAsync()) throw new InvalidDataException("The real matrix authority has no effect-count readback.");
        return (rows.GetInt64(0), rows.GetInt64(1));
    }

    private static async Task AssertMatrixObservationScopeAsync(Rig rig, string pairId,
        MatrixPort left, MatrixPort right, string tag, CancellationToken cancellationToken)
    {
        await using var read = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = rig.Db.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
        await read.OpenAsync(cancellationToken);
        await using var command = read.CreateCommand();
        command.CommandText = """
            SELECT g.side,r.identity_json,v.identity_json,o.evidence_json
            FROM observation o
            JOIN scan_generation g ON g.scan_generation_id=o.scan_generation_id
            JOIN root_binding r ON r.root_binding_id=o.root_binding_id
            JOIN object_version v ON v.object_version_id=o.object_version_id
            WHERE g.pair_id=$pair AND g.status='complete' AND o.complete_generation=1
              AND g.generation=(SELECT max(latest.generation) FROM scan_generation latest
                                WHERE latest.root_binding_id=g.root_binding_id AND latest.status='complete');
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        var observed = 0;
        while (await rows.ReadAsync(cancellationToken))
        {
            observed++;
            var side = rows.GetString(0);
            var cloud = left.Cloud is not null && right.Cloud is not null
                ? side == "local" ? left.Cloud : right.Cloud
                : side == "dropbox" ? left.Cloud ?? right.Cloud : null;
            var explicitRoot = SyncAuthorityCloudRootPersistence.TryRead(rows.GetString(1),
                out var provider, out var account, out var nativeNamespace, out _);
            var explicitObservation = SyncAuthorityCloudObservationIdentity.TryParse(rows.GetString(2), out var identity);
            Check(explicitRoot == explicitObservation,
                tag + ": actual retained observation identity kind matches its own scan root");
            if (cloud is null)
            {
                Check(!explicitRoot, tag + ": native local observation is not relabelled as cloud");
                continue;
            }
            if (cloud.Kind == SyncEndpointKind.GoogleDrive)
                Check(explicitRoot, tag + ": actual Google inventory keeps an explicitly scoped cloud identity");
            if (explicitRoot)
            {
                Check(provider == cloud.Provider && account == cloud.ProviderAccountId &&
                      nativeNamespace == cloud.EndpointNamespaceId && identity is not null &&
                      identity.Provider == provider && identity.ProviderAccountId == account &&
                      identity.NamespaceId == nativeNamespace && !string.IsNullOrWhiteSpace(identity.FileId),
                    tag + ": actual observation binds its own retained provider/account/namespace, not another side or display label");
                using var evidence = JsonDocument.Parse(rows.GetString(3));
                Check(identity!.ContentAlgorithm == SyncContentIdentity.AlgorithmFor(cloud.Provider) &&
                      evidence.RootElement.GetProperty("contentAlgorithm").GetString() == identity.ContentAlgorithm,
                    tag + ": actual typed identity and evidence preserve the registered native content algorithm");
            }
            else
            {
                using var legacy = JsonDocument.Parse(rows.GetString(2));
                var value = legacy.RootElement;
                Check(value.EnumerateObject().Count() == 2 && value.TryGetProperty("fileId", out var id) &&
                      value.TryGetProperty("revision", out var revision) && rows.GetString(2) ==
                      JsonSerializer.Serialize(new { fileId = id.GetString(), revision = revision.GetString() }),
                    tag + ": verified legacy cloud observation retains the exact existing identity serialization");
            }
        }
        Check(observed >= 2, tag + ": observation-scope proof reads real retained entries from both inventories");
    }

    private static async Task<(long Scans, long Observations, long Versions, long Operations)>
        MatrixInventoryRetentionCountsAsync(Rig rig, CancellationToken cancellationToken)
    {
        await using var read = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = rig.Db.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
        await read.OpenAsync(cancellationToken);
        await using var command = read.CreateCommand();
        command.CommandText = """
            SELECT (SELECT count(*) FROM scan_generation),(SELECT count(*) FROM observation),
                   (SELECT count(*) FROM object_version),(SELECT count(*) FROM operation);
            """;
        await using var row = await command.ExecuteReaderAsync(cancellationToken);
        if (!await row.ReadAsync(cancellationToken)) throw new InvalidDataException("Missing actual inventory counts.");
        return (row.GetInt64(0), row.GetInt64(1), row.GetInt64(2), row.GetInt64(3));
    }

    private static async Task LocalCloudObservationRootRejectionsAsync()
    {
        foreach (var rejection in new[] { "wrong-binding", "inactive-binding", "malformed-explicit-scope",
                     "wrong-account", "wrong-provider", "wrong-namespace" })
        {
            using var rig = await Rig.CreateAsync("observation-root-" + rejection);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var left = await MatrixPortAsync(rig, SyncEndpointKind.Local, rejection + "-local");
            var right = await MatrixPortAsync(rig, SyncEndpointKind.GoogleDrive, rejection + "-drive");
            await SeedMatrixFileAsync(rig, left, "local.txt", "Unchanged local authority\n");
            await SeedMatrixFileAsync(rig, right, "remote.txt", "Unchanged remote authority\n");
            var recovery = new DropboxFolderEntry("Recovery", Rig.RecoveryContainerPath,
                "drive-matrix-recovery-" + rejection + "-drive");
            var bound = await rig.Host.BindEndpointPairAsync(
                SyncEndpointPairDraft.Create(new SyncPairEndpoints(left.Endpoint, right.Endpoint, SyncMode.TwoWay),
                    rejection), cancellationToken: deadline.Token, recoveryFolder: recovery);
            var localRoot = await rig.Db.LoadActiveRootBindingIdAsync(bound.Pair.Id, "local", deadline.Token)
                            ?? throw new InvalidDataException("The actual local root was not retained.");
            var cloudRoot = await rig.Db.LoadActiveRootBindingIdAsync(bound.Pair.Id, "dropbox", deadline.Token)
                            ?? throw new InvalidDataException("The actual cloud root was not retained.");
            if (rejection != "wrong-binding")
            {
                await using var edit = new Microsoft.Data.Sqlite.SqliteConnection(
                    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    { DataSource = rig.Db.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite }.ToString());
                await edit.OpenAsync(deadline.Token);
                await using var change = edit.CreateCommand();
                // Fault input is confined to this new isolated fixture. The production reader,
                // producer and transactional rejection are still the decisions being exercised.
                change.CommandText = rejection switch
                {
                    "inactive-binding" => "UPDATE root_binding SET state='blocked' WHERE root_binding_id=$root;",
                    "wrong-account" => "UPDATE root_binding SET identity_json=json_set(identity_json,'$.providerAccountId','different-account') WHERE root_binding_id=$root;",
                    "wrong-provider" => "UPDATE root_binding SET identity_json=json_set(identity_json,'$.provider','dropbox') WHERE root_binding_id=$root;",
                    "wrong-namespace" => "UPDATE root_binding SET identity_json=json_set(identity_json,'$.namespaceId','different-namespace') WHERE root_binding_id=$root;",
                    _ => "UPDATE root_binding SET identity_json=$identity WHERE root_binding_id=$root;"
                };
                change.Parameters.AddWithValue("$root", cloudRoot);
                if (rejection == "malformed-explicit-scope")
                    change.Parameters.AddWithValue("$identity", "{\"identityKind\":\"cloud\",\"provider\":\"google_drive\"}");
                Check(await change.ExecuteNonQueryAsync(deadline.Token) == 1,
                    rejection + ": exact isolated retained root receives the intended fault input");
            }
            var retainedBefore = await MatrixInventoryRetentionCountsAsync(rig, deadline.Token);
            var writesBefore = rig.Wire.Writes;
            try
            {
                await SyncAuthorityRemoteIdentity.LoadAsync(rig.Db, bound.Pair,
                    rejection == "wrong-binding" ? "different-remote-binding" : cloudRoot, deadline.Token);
                throw new Exception("Expected actual successor-scope rejection: " + rejection);
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
            {
                Check(true, rejection + ": actual common successor loader refuses invalid retained scope without effect");
            }
            var coordinator = new SyncAuthorityObservationCoordinator(bound.Pair, rig.DriveClient(right.Cloud!),
                rig.Db, localRoot, rejection == "wrong-binding" ? "different-remote-binding" : cloudRoot);
            try
            {
                await coordinator.ObserveAsync("initial", deadline.Token);
                throw new Exception("Expected actual root-scope rejection: " + rejection);
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
            {
                Check(true, rejection + ": actual local/cloud producer refuses unavailable or invalid retained scope");
            }
            Check(await MatrixInventoryRetentionCountsAsync(rig, deadline.Token) == retainedBefore &&
                  rig.Wire.Writes == writesBefore,
                rejection + ": refusal creates no scan, observation, version, operation or provider effect");
            Check(Encoding.UTF8.GetString((await ReadMatrixFileAsync(rig, left, "local.txt"))!) ==
                  "Unchanged local authority\n" &&
                  Encoding.UTF8.GetString((await ReadMatrixFileAsync(rig, right, "remote.txt"))!) ==
                  "Unchanged remote authority\n",
                rejection + ": both actual endpoint payloads survive the refusal unchanged");
        }
    }

    private static async Task BindReviewRunMatrixAsync(SyncEndpointKind leftKind, SyncEndpointKind rightKind, SyncMode mode)
    {
        var tag = leftKind + "-" + rightKind + "-" + mode;
        using var rig = await Rig.CreateAsync("public-matrix-" + tag);
        var left = await MatrixPortAsync(rig, leftKind, tag + "-left");
        var right = await MatrixPortAsync(rig, rightKind, tag + "-right");
        const string leftName = "only-left.txt", rightName = "only-right.txt";
        var leftText = "Exact logical left bytes for " + tag + "\n";
        var rightText = "Exact logical right bytes for " + tag + "\n";
        // Two small nonempty files, bounded setup plus two convergence runs. This is
        // a fixture latency allowance, never a live Child throughput/deadline claim.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        Console.WriteLine("MATRIX_CASE=" + tag + " payload_bytes=" +
                          (Encoding.UTF8.GetByteCount(leftText) + Encoding.UTF8.GetByteCount(rightText)) +
                          " deadline_seconds=90 phase=bind-review-run-repeat");
        await SeedMatrixFileAsync(rig, left, leftName, leftText);
        await SeedMatrixFileAsync(rig, right, rightName, rightText);
        var draft = SyncEndpointPairDraft.Create(new SyncPairEndpoints(left.Endpoint, right.Endpoint, mode), tag);
        var recoveryCloud = left.Cloud ?? right.Cloud;
        var recovery = recoveryCloud is null ? null : new DropboxFolderEntry("Recovery", Rig.RecoveryContainerPath,
            recoveryCloud.Kind == SyncEndpointKind.Dropbox ? "id:matrix-recovery-" +
                (left.Cloud is not null ? tag + "-left" : tag + "-right") : "drive-matrix-recovery-" +
                (left.Cloud is not null ? tag + "-left" : tag + "-right"));
        var bound = await rig.Host.BindEndpointPairAsync(draft, cancellationToken: deadline.Token, recoveryFolder: recovery);
        Check(bound.Pair.LeftEndpoint == draft.LeftEndpoint && bound.Pair.RightEndpoint == draft.RightEndpoint &&
              bound.Pair.Direction == mode && bound.Pair.InitialReviewApproved == false,
            tag + ": public binding retains the exact ordered identities and direction without approval");
        Check(rig.Wire.Writes.Total == 0 && await ReadMatrixFileAsync(rig, right, leftName) is null &&
              await ReadMatrixFileAsync(rig, left, rightName) is null,
            tag + ": binding has no user-file effect on either endpoint");
        await rig.Host.RefreshBootstrapInventoryAsync(bound.Pair.Id, deadline.Token);
        var review = await rig.Host.LoadBootstrapReviewPageAsync(bound.Pair.Id, cancellationToken: deadline.Token);
        Check(review.InventoryComplete && review.Items.Any(item => item.RelativePath == leftName) &&
              review.Items.Any(item => item.RelativePath == rightName),
            tag + ": complete actual review exposes both endpoint inventories");
        await AssertMatrixObservationScopeAsync(rig, bound.Pair.Id, left, right, tag, deadline.Token);
        await rig.Host.ApproveBootstrapMergeAsync(bound.Pair.Id, deadline.Token);
        var runnable = (await rig.Host.SetPairEnabledAsync(bound.Pair.Id, true, deadline.Token)).Pairs.Single();
        var preparationProgress = new System.Collections.Concurrent.ConcurrentQueue<SyncComparisonProgress>();
        var result = await rig.Host.RunPairsAsync([runnable], "public endpoint matrix", onProgress: update =>
        {
            if (update.Comparison is { Phase: "Checking endpoint bindings" } progress)
                preparationProgress.Enqueue(progress);
            return Task.CompletedTask;
        }, cancellationToken: deadline.Token);
        Check(preparationProgress.Any(progress => progress.Detail.StartsWith("Starting:", StringComparison.Ordinal)) &&
              preparationProgress.Any(progress => progress.Detail.StartsWith("Completed:", StringComparison.Ordinal)),
            tag + ": ordinary run reports its actual endpoint binding checks before awaits and after real completion");
        if (result.Items.Any(item => item.FailureType is not null || item.Result is not { Blocked: 0 }))
            Console.Error.WriteLine("MATRIX_FAILED_RESULT=" + tag + " " + JsonSerializer.Serialize(result));
        Check(result.Items.Count == 1 && result.Items[0].FailureType is null && result.Items[0].Result is { Blocked: 0 },
            tag + ": actual ordinary host run completes without a hidden blocked file: " + result.Items.FirstOrDefault()?.FailureMessage);
        var receivedRight = await ReadMatrixFileAsync(rig, right, leftName);
        var receivedLeft = await ReadMatrixFileAsync(rig, left, rightName);
        Check(mode == SyncMode.DownloadOnly ? receivedRight is null :
                receivedRight is not null && receivedRight.SequenceEqual(Encoding.UTF8.GetBytes(leftText)),
            tag + ": logical left-to-right transfer obeys the selected direction and exact byte readback");
        Check(mode == SyncMode.UploadOnly ? receivedLeft is null :
                receivedLeft is not null && receivedLeft.SequenceEqual(Encoding.UTF8.GetBytes(rightText)),
            tag + ": logical right-to-left transfer obeys the selected direction and exact byte readback");
        Check((await ReadMatrixFileAsync(rig, left, leftName))!.SequenceEqual(Encoding.UTF8.GetBytes(leftText)) &&
              (await ReadMatrixFileAsync(rig, right, rightName))!.SequenceEqual(Encoding.UTF8.GetBytes(rightText)),
            tag + ": both original source files survive unchanged");
        await AssertMatrixSuccessorScopeAsync(rig, bound.Pair.Id, tag, deadline.Token);
        var writesBeforeRepeat = rig.Wire.Writes;
        var effectsBeforeRepeat = await MatrixEffectCountsAsync(rig, bound.Pair.Id);
        var repeat = await rig.Host.RunPairsAsync([runnable], "public endpoint matrix unchanged repeat", cancellationToken: deadline.Token);
        Check(repeat.Items.Count == 1 && repeat.Items[0].FailureType is null && repeat.Items[0].Result is { Blocked: 0 } &&
              rig.Wire.Writes == writesBeforeRepeat,
            tag + ": unchanged repeat has no repeated provider effect or hidden pair failure");
        Check(await MatrixEffectCountsAsync(rig, bound.Pair.Id) == effectsBeforeRepeat,
            tag + ": unchanged repeat creates no new effect attempt or receipt, including native local effects");
        Check(SameMatrixBytes(receivedRight, await ReadMatrixFileAsync(rig, right, leftName)) &&
              SameMatrixBytes(receivedLeft, await ReadMatrixFileAsync(rig, left, rightName)),
            tag + ": unchanged repeat preserves exact destination bytes and absence of excluded-direction files");
        await AssertMatrixObservationScopeAsync(rig, bound.Pair.Id, left, right, tag + " repeat", deadline.Token);
        // Exercise the changed successor producers through the same ordinary host, not a
        // serializer-only test. The two distinct paths each have exactly one changed source.
        if (mode == SyncMode.TwoWay && (left.Cloud is null || right.Cloud is null))
        {
            var changedLeft = leftText + "One-sided later left edit\n";
            var changedRight = rightText + "One-sided later right edit\n";
            await SeedMatrixFileAsync(rig, left, leftName, changedLeft);
            await SeedMatrixFileAsync(rig, right, rightName, changedRight);
            var updated = await rig.Host.RunPairsAsync([runnable], "public endpoint matrix one-sided replacements",
                cancellationToken: deadline.Token);
            if (updated.Items.Any(item => item.FailureType is not null || item.Result is not { Blocked: 0 }))
                Console.Error.WriteLine("MATRIX_UPDATE_FAILED=" + tag + " " + JsonSerializer.Serialize(updated));
            Check(updated.Items.Count == 1 && updated.Items[0].FailureType is null &&
                  updated.Items[0].Result is { Blocked: 0, Conflicts: 0 },
                tag + ": ordinary host completes both independent one-sided replacements without hidden failure");
            Check((await ReadMatrixFileAsync(rig, right, leftName))!.SequenceEqual(Encoding.UTF8.GetBytes(changedLeft)) &&
                  (await ReadMatrixFileAsync(rig, left, rightName))!.SequenceEqual(Encoding.UTF8.GetBytes(changedRight)),
                tag + ": both real replacement consumers produce exact changed source bytes");
            await AssertMatrixSuccessorScopeAsync(rig, bound.Pair.Id, tag + " updated", deadline.Token);
            var updateWrites = rig.Wire.Writes;
            var updateEffects = await MatrixEffectCountsAsync(rig, bound.Pair.Id);
            var updatedRepeat = await rig.Host.RunPairsAsync([runnable], "public endpoint matrix replacement repeat",
                cancellationToken: deadline.Token);
            Check(updatedRepeat.Items.Count == 1 && updatedRepeat.Items[0].FailureType is null &&
                  updatedRepeat.Items[0].Result is { Blocked: 0, Conflicts: 0 } &&
                  rig.Wire.Writes == updateWrites && await MatrixEffectCountsAsync(rig, bound.Pair.Id) == updateEffects,
                tag + ": unchanged replacement repeat preserves logical identity and dispatches zero additional effects");
            await AssertMatrixObservationScopeAsync(rig, bound.Pair.Id, left, right, tag + " updated repeat", deadline.Token);
        }
    }

    private static async Task AssertMatrixSuccessorScopeAsync(Rig rig, string pairId,
        string tag, CancellationToken cancellationToken)
    {
        await using var read = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = rig.Db.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
        await read.OpenAsync(cancellationToken);
        await using var command = read.CreateCommand();
        command.CommandText = """
            SELECT r.identity_json,v.identity_json,p.object_id,v.object_id
            FROM synchronization_point p
            JOIN object_version v ON v.object_version_id=p.local_object_version_id
            JOIN membership m ON m.membership_id=p.local_membership_id
            JOIN root_binding r ON r.root_binding_id=m.root_binding_id
            WHERE p.pair_id=$pair
            UNION ALL
            SELECT r.identity_json,v.identity_json,p.object_id,v.object_id
            FROM synchronization_point p
            JOIN object_version v ON v.object_version_id=p.dropbox_object_version_id
            JOIN membership m ON m.membership_id=p.dropbox_membership_id
            JOIN root_binding r ON r.root_binding_id=m.root_binding_id
            WHERE p.pair_id=$pair;
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        var versions = 0;
        while (await rows.ReadAsync(cancellationToken))
        {
            versions++;
            Check(rows.GetString(2) == rows.GetString(3),
                tag + ": committed successor belongs to its synchronization point's logical object");
            var typedRoot = SyncAuthorityCloudRootPersistence.TryRead(rows.GetString(0),
                out var provider, out var account, out var nativeNamespace, out _);
            var typedVersion = SyncAuthorityCloudObservationIdentity.TryParse(rows.GetString(1), out var identity);
            Check(typedRoot == typedVersion,
                tag + ": actual committed successor preserves its own retained root's identity kind before repeat");
            if (typedRoot)
                Check(identity is not null && identity.Provider == provider &&
                      identity.ProviderAccountId == account && identity.NamespaceId == nativeNamespace &&
                      identity.ContentAlgorithm == SyncContentIdentity.AlgorithmFor(provider!),
                    tag + ": actual committed successor retains its exact native provider/account/namespace/algorithm");
            else
            {
                using var rootIdentity = JsonDocument.Parse(rows.GetString(0));
                using var versionIdentity = JsonDocument.Parse(rows.GetString(1));
                if (!rootIdentity.RootElement.TryGetProperty("volumeSerial", out _))
                {
                    var value = versionIdentity.RootElement;
                    Check(value.EnumerateObject().Count() == 2 &&
                          rows.GetString(1) == JsonSerializer.Serialize(new
                          {
                              fileId = value.GetProperty("fileId").GetString(),
                              revision = value.GetProperty("revision").GetString()
                          }), tag + ": legacy committed remote successor preserves byte-identical identity");
                }
            }
        }
        Check(versions >= 2, tag + ": successor proof reads actual completed two-sided synchronization points");
    }

    private static string PrepareFixtureRoot(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Use a fresh isolated public pair authoring fixture root.");
        Directory.CreateDirectory(root);
        CloudPairHostBindingChecks.UseFixtureRoot(root);
        _assertions = 0;
        return root;
    }

    private static async Task LocalReplacementConsumersAsync()
    {
        foreach (var kind in new[] { SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
        foreach (var reversed in new[] { false, true })
        {
            var tag = kind + (reversed ? "-local-right" : "-local-left");
            using var rig = await Rig.CreateAsync("public-local-replacement-" + tag);
            var cloud = Endpoint(kind, tag);
            await rig.AddEndpointAsync(cloud);
            var recovery = new DropboxFolderEntry("Recovery", Rig.RecoveryContainerPath,
                kind == SyncEndpointKind.Dropbox ? "id:recovery-" + tag : "drive-recovery-" + tag);
            rig.RegisterFolder(cloud, recovery.Path, recovery.FolderId);
            var fixtureParent = Directory.GetParent(rig.StateRoot)!.FullName;
            var beforeRoot = Directory.CreateDirectory(Path.Combine(fixtureParent, "original-local")).FullName;
            var afterRoot = Directory.CreateDirectory(Path.Combine(fixtureParent, "replacement-local")).FullName;
            var retainedFile = Path.Combine(beforeRoot, "retained-user-file.txt");
            const string retainedText = "Binding replacement must never transfer or erase these bytes.\n";
            await File.WriteAllTextAsync(retainedFile, retainedText);
            SyncPair DraftAt(string path, SyncPair? policy = null)
            {
                var local = NativeLocalPort(path);
                var endpoints = reversed ? new SyncPairEndpoints(Port(cloud), local, SyncMode.TwoWay)
                    : new SyncPairEndpoints(local, Port(cloud), SyncMode.TwoWay);
                return SyncEndpointPairDraft.Create(endpoints, "Replacement " + tag, policy);
            }
            var initialDraft = DraftAt(beforeRoot);
            var first = await rig.Host.BindEndpointPairAsync(initialDraft, recoveryFolder: recovery);
            Check(first.Pair.LocalRoot == beforeRoot && first.Pair.Enabled == initialDraft.Enabled &&
                  first.Pair.InitialReviewApproved == false,
                tag + ": public initial binding retains native local identity and requested enablement without first-sync approval");
            var oldLocal = await rig.Db.LoadActiveRootBindingIdAsync(first.Pair.Id, "local");
            var oldCloud = await rig.Db.LoadActiveRootBindingIdAsync(first.Pair.Id, "dropbox");
            Check(oldLocal is not null && oldCloud is not null,
                tag + ": the real local/account consumer registered both active authorities");
            var replacement = DraftAt(afterRoot, first.Pair);
            var beforeSet = JsonSerializer.Serialize(await rig.Host.LoadPairsAsync());
            Exception? stale = null;
            try
            {
                await rig.Host.BindEndpointPairAsync(replacement, first.Pair.Id,
                    first.Pair.PolicyRevision + 1, recoveryFolder: recovery);
            }
            catch (InvalidOperationException error) { stale = error; }
            Check(stale is not null && JsonSerializer.Serialize(await rig.Host.LoadPairsAsync()) == beforeSet &&
                  await rig.Db.LoadActiveRootBindingIdAsync(first.Pair.Id, "local") == oldLocal &&
                  await rig.Db.LoadActiveRootBindingIdAsync(first.Pair.Id, "dropbox") == oldCloud,
                tag + ": stale public replacement preserves the exact definition and both old active roots");
            var rebound = await rig.Host.BindEndpointPairAsync(replacement, first.Pair.Id,
                first.Pair.PolicyRevision, recoveryFolder: recovery);
            Check(rebound.IdentityChanged && rebound.Pair.Id != first.Pair.Id && rebound.Pair.LocalRoot == afterRoot &&
                  rebound.PairSet.Pairs.Count == 1 && rebound.PairSet.Pairs.Single().Id == rebound.Pair.Id,
                tag + ": same-cloud/new-local edit publishes exactly the new pair through the real provider consumer");
            Check(rebound.Pair.Enabled == replacement.Enabled && rebound.Pair.InitialReviewApproved == false &&
                  rebound.Pair.LeftEndpoint == replacement.LeftEndpoint && rebound.Pair.RightEndpoint == replacement.RightEndpoint,
                tag + ": ordered endpoints stay intact and replacement requires fresh first-sync review");
            Check(await CountActiveRootsAsync(rig, first.Pair.Id) == 0 &&
                  await CountActiveRootsAsync(rig, rebound.Pair.Id) == 2 &&
                  await rig.Db.LoadActiveRootBindingIdAsync(rebound.Pair.Id, "local") != oldLocal &&
                  await rig.Db.LoadActiveRootBindingIdAsync(rebound.Pair.Id, "dropbox") != oldCloud,
                tag + ": both old roots retire and both new roots become active in the same publication");
            Check(await File.ReadAllTextAsync(retainedFile) == retainedText &&
                  !Directory.EnumerateFileSystemEntries(afterRoot).Any() && rig.Wire.Writes.Total == 0,
                tag + ": native user bytes are preserved, replacement stays empty and no provider file transfer occurs");
        }
    }

    /// <summary>
    /// root727: the REQUIRED local/cloud GOOGLE policy-edit proof. The shared policy entry point wires the
    /// published same-root atomic publication for BOTH providers, but the cloud-public matrix pair is
    /// cloud/cloud (GoogleDrive-GoogleDrive), which returns through the cloud-only branch and never reaches the
    /// typed registrar. This case binds a real LOCAL + GoogleDrive pair through the public Host, then performs a
    /// normal same-root policy edit and asserts the typed ATOMIC branch was genuinely reached (the in-transaction
    /// after-writes hook fired exactly once) and that the committed pair is the same id with an advanced revision.
    /// </summary>
    private static async Task LocalCloudGooglePolicyAtomicAsync()
    {
        using var rig = await Rig.CreateAsync("public-local-google-atomic");
        var local = await MatrixPortAsync(rig, SyncEndpointKind.Local, "lcg-local");
        var drive = await MatrixPortAsync(rig, SyncEndpointKind.GoogleDrive, "lcg-drive");
        var draft = SyncEndpointPairDraft.Create(
            new SyncPairEndpoints(local.Endpoint, drive.Endpoint, SyncMode.TwoWay), "Local Google atomic");
        var bound = await rig.Host.BindEndpointPairAsync(draft);
        Check(SyncAuthorityRuntimeHost.IsGooglePair(bound.Pair) && bound.Pair.Enabled == draft.Enabled &&
              bound.Pair.InitialReviewApproved == false,
            "the real LOCAL/GoogleDrive pair binds on the GOOGLE engine layout with requested enablement and no approval");
        var atomicHits = 0;
        SyncPair committed;
        SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = () => atomicHits++;
        try
        {
            var edited = draft with { Priority = draft.Priority + 1 };
            var published = await rig.Host.BindEndpointPairAsync(edited, bound.Pair.Id, bound.Pair.PolicyRevision);
            Check(atomicHits == 1,
                "the LOCAL/cloud Google same-root policy edit genuinely reached the typed ATOMIC branch");
            Check(published.Pair.Id == bound.Pair.Id &&
                  published.Pair.PolicyRevision > bound.Pair.PolicyRevision,
                "the Google atomic publication advances the same pair id through the typed registrar");
            Check(published.Pair.LeftEndpoint == edited.LeftEndpoint &&
                  published.Pair.RightEndpoint == edited.RightEndpoint,
                "the Google atomic publication keeps both ordered endpoints intact");
            committed = published.Pair;
        }
        finally { SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = null; }
        var reopened = (await rig.Host.LoadPairsAsync()).Pairs.Single(p => p.Id == bound.Pair.Id);
        Check(reopened.Id == committed.Id && reopened.PolicyRevision == committed.PolicyRevision &&
              reopened.Direction == committed.Direction && reopened.Enabled == committed.Enabled &&
              reopened.Priority == committed.Priority && reopened.LeftEndpoint == committed.LeftEndpoint &&
              reopened.RightEndpoint == committed.RightEndpoint,
            "a fresh read of the pair set matches the committed Google local/cloud identity, revision, endpoints and policy");
        Check(rig.Wire.Writes.Total == 0, "the Google local/cloud policy edit transfers no user files");
        // root728 obligation (3): a POST-WRITE injected failure inside the typed Google atomic publication must
        // roll the whole edit back. The reached-stage hook firing proves the transaction writes were reached;
        // the ENTIRE published state must be unchanged and a FRESHLY reopened database must read the same state.
        // root729: the ROW-LEVEL published-authority projection (the Root725 helper), not a serialized pair set:
        // pair revision/definition/selection/state, configuration rows, EVERY root row (id, side, generation,
        // state, identity, display path), control bindings, allocation-linked control-namespace ownership and the
        // recovery root rows. The preparation/effect journal stays out and is accounted separately.
        var beforeFault = await ExcludedNestedPairPartitionChecks.PublishedAuthorityAsync(rig.Db.DatabasePath,
            bound.Pair.Id);
        SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest =
            () => throw new InvalidOperationException("injected-post-write-fault");
        Exception? injected = null;
        try
        {
            var failing = draft with { Priority = draft.Priority + 2 };
            await rig.Host.BindEndpointPairAsync(failing, bound.Pair.Id, committed.PolicyRevision);
        }
        catch (Exception error) { injected = error; }
        finally { SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = null; }
        Check(injected is not null &&
              injected.ToString().Contains("injected-post-write-fault", StringComparison.Ordinal),
            "root728(3) the Google atomic publication surfaced the exact post-write injected failure");
        Check(await ExcludedNestedPairPartitionChecks.PublishedAuthorityAsync(rig.Db.DatabasePath, bound.Pair.Id) ==
              beforeFault,
            "root728(3) the post-write failure left the WHOLE published-authority row projection unchanged");
        var reopenedAfterFault = new SyncAuthorityDatabase(rig.Db.DatabasePath);
        Check((await reopenedAfterFault.LoadPairSetAsync()).Pairs.Single(p => p.Id == bound.Pair.Id).PolicyRevision ==
              committed.PolicyRevision,
            "root728(3) a FRESHLY reopened database still reads the pre-fault published revision");
        Check(await ExcludedNestedPairPartitionChecks.PublishedAuthorityAsync(rig.Db.DatabasePath, bound.Pair.Id) ==
              beforeFault,
            "root728(3) the FRESH reopen projects the same unchanged published-authority rows");
    }

    /// <summary>
    /// root727: the endpoint-orientation classification. Every reachable pair shape is driven through the REAL
    /// public Host on a real SQLite authority, and the shared policy publication is discriminated by the
    /// in-transaction after-writes hook:
    ///   - LOCAL/LOCAL is NOT a same-root atomic publication: the local bind route retains the definition with a
    ///     SINGLE write (PrepareBoundLocalEndpointsAsync then RetainPairDefinitionAsync), so the hardcoded-select
    ///     double write and the selection-loss path cannot occur. The hook must NOT fire.
    ///   - LOCAL/GoogleDrive forward (already proven) and REVERSED both take the typed atomic publication, so the
    ///     hook must fire exactly once and the SAME id with both ordered endpoints must persist.
    /// </summary>
    private static async Task PolicyPublicationOrientationAsync()
    {
        using (var rig = await Rig.CreateAsync("public-local-local-atomic"))
        {
            var parent = Directory.GetParent(rig.StateRoot)!.FullName;
            var leftRoot = Directory.CreateDirectory(Path.Combine(parent, "ll-left")).FullName;
            var rightRoot = Directory.CreateDirectory(Path.Combine(parent, "ll-right")).FullName;
            var draft = SyncEndpointPairDraft.Create(
                new SyncPairEndpoints(NativeLocalPort(leftRoot), NativeLocalPort(rightRoot), SyncMode.TwoWay),
                "Local local orientation");
            var bound = await rig.Host.BindEndpointPairAsync(draft);
            Check(bound.Pair.Provider == SyncProviders.Local && bound.Pair.InitialReviewApproved == false,
                "the real LOCAL/LOCAL pair binds through the local route unapproved");
            var localHits = 0;
            SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = () => localHits++;
            try
            {
                var edited = draft with { Priority = draft.Priority + 1 };
                var published = await rig.Host.BindEndpointPairAsync(edited, bound.Pair.Id, bound.Pair.PolicyRevision);
                Check(localHits == 0,
                    "the LOCAL/LOCAL same-root edit does NOT take the same-root atomic publication (no hardcoded-select double write)");
                Check(published.Pair.Id == bound.Pair.Id &&
                      published.Pair.PolicyRevision > bound.Pair.PolicyRevision,
                    "the LOCAL/LOCAL same-root edit still commits an advanced revision through its single definition write");
            }
            finally { SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = null; }
        }
        using (var rig = await Rig.CreateAsync("public-google-local-reversed-atomic"))
        {
            var local = await MatrixPortAsync(rig, SyncEndpointKind.Local, "glr-local");
            var drive = await MatrixPortAsync(rig, SyncEndpointKind.GoogleDrive, "glr-drive");
            var draft = SyncEndpointPairDraft.Create(
                new SyncPairEndpoints(drive.Endpoint, local.Endpoint, SyncMode.TwoWay), "Reversed Google local");
            var bound = await rig.Host.BindEndpointPairAsync(draft);
            Check(SyncAuthorityRuntimeHost.IsGooglePair(bound.Pair),
                "the real REVERSED Google/local pair binds on the GOOGLE engine layout");
            var reversedHits = 0;
            SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = () => reversedHits++;
            try
            {
                var edited = draft with { Priority = draft.Priority + 1 };
                var published = await rig.Host.BindEndpointPairAsync(edited, bound.Pair.Id, bound.Pair.PolicyRevision);
                Check(reversedHits == 1,
                    "the REVERSED Google/local same-root edit genuinely reached the typed ATOMIC branch");
                Check(published.Pair.Id == bound.Pair.Id &&
                      published.Pair.PolicyRevision > bound.Pair.PolicyRevision &&
                      published.Pair.LeftEndpoint == edited.LeftEndpoint &&
                      published.Pair.RightEndpoint == edited.RightEndpoint,
                    "the REVERSED Google atomic publication keeps the SAME id and both ordered endpoints");
            }
            finally { SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = null; }
        }
    }

    /// <summary>
    /// root728 obligation (1): a REAL public POLICY caller (SetRootSelectionAsync, one of the five advancing
    /// callers) editing an UNSELECTED published pair while ANOTHER pair remains selected. The caller's intended
    /// selection is carried into the ONE atomic publication, so the other pair selection must survive, and the
    /// committed selection must read back from a FRESHLY reopened database - not only this Host.
    /// </summary>
    private static async Task UnselectedPublicPolicyEditAsync()
    {
        using var rig = await Rig.CreateAsync("public-unselected-policy-edit");
        var firstLocal = await MatrixPortAsync(rig, SyncEndpointKind.Local, "pe-a-local");
        var firstCloud = await MatrixPortAsync(rig, SyncEndpointKind.Dropbox, "pe-a-cloud");
        var secondLocal = await MatrixPortAsync(rig, SyncEndpointKind.Local, "pe-b-local");
        var secondCloud = await MatrixPortAsync(rig, SyncEndpointKind.Dropbox, "pe-b-cloud");
        var draftA = SyncEndpointPairDraft.Create(
            new SyncPairEndpoints(firstLocal.Endpoint, firstCloud.Endpoint, SyncMode.TwoWay), "Policy edit A");
        var draftB = SyncEndpointPairDraft.Create(
            new SyncPairEndpoints(secondLocal.Endpoint, secondCloud.Endpoint, SyncMode.TwoWay), "Policy edit B");
        var boundA = await rig.Host.BindEndpointPairAsync(draftA);
        var boundB = await rig.Host.BindEndpointPairAsync(draftB);
        Check((await rig.Host.LoadPairsAsync()).SelectedPairId == boundB.Pair.Id,
            "root728(1) the second bound pair is the SELECTED pair before the policy edit");
        var atomicHits = 0;
        SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = () => atomicHits++;
        try
        {
            await rig.Host.SetRootSelectionAsync(boundA.Pair.Id, included: false);
        }
        finally { SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = null; }
        var after = await rig.Host.LoadPairsAsync();
        Check(atomicHits == 1,
            "root728(1) the public policy edit of the UNSELECTED pair reached the ONE atomic publication");
        Check(after.SelectedPairId == boundB.Pair.Id,
            "root728(1) the public policy edit PRESERVES the other pair selection (no selection loss)");
        Check(after.Pairs.Single(p => p.Id == boundA.Pair.Id).PolicyRevision > boundA.Pair.PolicyRevision,
            "root728(1) the edited unselected pair advanced its revision through the public policy caller");
        var reopened = new SyncAuthorityDatabase(rig.Db.DatabasePath);
        Check((await reopened.LoadPairSetAsync()).SelectedPairId == boundB.Pair.Id,
            "root728(1) a FRESHLY reopened database reads back the same selected pair");
    }

    /// <summary>
    /// root728 item 3: the LOCAL/LOCAL public POLICY-CALLER path, explicitly distinguished from the ordinary bind.
    /// The bind route retains the definition with a single select:true write; here a real public policy caller
    /// (SetRootSelectionAsync) edits an UNSELECTED local/local pair while ANOTHER pair remains selected, so it must
    /// preserve the other pair selection and read back from a FRESHLY reopened database.
    /// </summary>
    private static async Task LocalLocalPublicPolicyEditAsync()
    {
        using var rig = await Rig.CreateAsync("public-locallocal-policy-edit");
        var parent = Directory.GetParent(rig.StateRoot)!.FullName;
        SyncPair DraftAt(string left, string right, string name)
        {
            var leftPort = NativeLocalPort(Directory.CreateDirectory(Path.Combine(parent, left)).FullName);
            var rightPort = NativeLocalPort(Directory.CreateDirectory(Path.Combine(parent, right)).FullName);
            return SyncEndpointPairDraft.Create(new SyncPairEndpoints(leftPort, rightPort, SyncMode.TwoWay), name);
        }
        var boundA = await rig.Host.BindEndpointPairAsync(DraftAt("llp-a-left", "llp-a-right", "Local policy A"));
        var boundB = await rig.Host.BindEndpointPairAsync(DraftAt("llp-b-left", "llp-b-right", "Local policy B"));
        Check(boundA.Pair.Provider == SyncProviders.Local && boundB.Pair.Provider == SyncProviders.Local &&
              (await rig.Host.LoadPairsAsync()).SelectedPairId == boundB.Pair.Id,
            "the second bound LOCAL/LOCAL pair is the selected pair before the policy edit");
        var localHits = 0;
        SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = () => localHits++;
        try { await rig.Host.SetRootSelectionAsync(boundA.Pair.Id, included: false); }
        finally { SyncAuthorityDatabase.PublishedSameRootEditAfterWritesForTest = null; }
        var after = await rig.Host.LoadPairsAsync();
        Check(localHits == 0,
            "the LOCAL/LOCAL public policy edit does NOT take the same-root atomic publication");
        Check(after.SelectedPairId == boundB.Pair.Id,
            "root728(3-local) the LOCAL/LOCAL public policy edit preserves the other pair selection");
        Check(after.Pairs.Single(p => p.Id == boundA.Pair.Id).PolicyRevision > boundA.Pair.PolicyRevision,
            "root728(3-local) the edited unselected LOCAL/LOCAL pair advanced its revision through the policy caller");
        var reopened = new SyncAuthorityDatabase(rig.Db.DatabasePath);
        Check((await reopened.LoadPairSetAsync()).SelectedPairId == boundB.Pair.Id,
            "root728(3-local) a FRESHLY reopened database reads back the same selected pair");
    }

    private static SyncEndpoint NativeLocalPort(string path)
    {
        using var lease = PilotDirectoryLease.Open(path);
        return SyncEndpoint.Local(path, new SyncLocalEndpointIdentity(
            lease.Identity.VolumeSerialNumber, lease.Identity.FileId));
    }

    private static async Task LocalPairReplacementConsumersAsync()
    {
        foreach (var row in new[]
                 {
                     (Tag: "change-left", ChangeLeft: true, Failure: (string?)null),
                     (Tag: "change-right", ChangeLeft: false, Failure: (string?)null),
                     (Tag: "late-right-root-claim", ChangeLeft: false, Failure: "late-claim"),
                     (Tag: "second-staging-failure", ChangeLeft: false, Failure: "second-staging")
                 })
        {
            var tag = row.Tag;
            using var rig = await Rig.CreateAsync("public-local-native-replacement-" + tag);
            var parent = Directory.GetParent(rig.StateRoot)!.FullName;
            var leftRoot = Directory.CreateDirectory(Path.Combine(parent, "original-left")).FullName;
            var rightRoot = Directory.CreateDirectory(Path.Combine(parent, "original-right")).FullName;
            var replacementRoot = Directory.CreateDirectory(Path.Combine(parent, "replacement-local")).FullName;
            var leftFile = Path.Combine(leftRoot, "original-left.txt");
            var rightFile = Path.Combine(rightRoot, "original-right.txt");
            await File.WriteAllTextAsync(leftFile, "Original left bytes must remain.\n");
            await File.WriteAllTextAsync(rightFile, "Original right bytes must remain.\n");
            SyncPair DraftAt(string left, string right, SyncPair? policy = null) =>
                SyncEndpointPairDraft.Create(new SyncPairEndpoints(NativeLocalPort(left), NativeLocalPort(right),
                    SyncMode.TwoWay), "Native replacement " + tag, policy);
            var first = await rig.Host.BindEndpointPairAsync(DraftAt(leftRoot, rightRoot));
            Check(first.Pair.Provider == SyncProviders.Local && first.Pair.InitialReviewApproved == false &&
                  await CountActiveRootsAsync(rig, first.Pair.Id) == 2,
                tag + ": accountless public initial binding retains both native roots and requires first review");
            var draft = DraftAt(row.ChangeLeft ? replacementRoot : leftRoot,
                row.ChangeLeft ? rightRoot : replacementRoot, first.Pair);
            var originalSet = JsonSerializer.Serialize(await rig.Host.LoadPairsAsync());
            Exception? stale = null;
            try
            {
                await rig.Host.BindEndpointPairAsync(draft, first.Pair.Id, first.Pair.PolicyRevision + 1);
            }
            catch (InvalidOperationException error) { stale = error; }
            Check(stale is not null && JsonSerializer.Serialize(await rig.Host.LoadPairsAsync()) == originalSet &&
                  await CountActiveRootsAsync(rig, first.Pair.Id) == 2,
                tag + ": stale accountless replacement preserves the exact old definition and active roots");
            if (row.Failure is { } failureMode)
            {
                var installation = await rig.Db.LoadInstallationIdAsync() ?? throw new InvalidOperationException("Missing fixture installation.");
                var leftBinding = await rig.Db.LoadActiveRootBindingIdAsync(first.Pair.Id, "local");
                var leftAuthority = await rig.Db.LoadRootBindingAuthorityForTestAsync(leftBinding);
                using var leftIdentity = JsonDocument.Parse(leftAuthority.IdentityJson);
                var volume = leftIdentity.RootElement.GetProperty("volumeId").GetString()!;
                var boundaries = 0;
                var injected = false;
                SyncAuthorityLocalControlNamespaceAllocator.BoundaryForTest = boundary =>
                {
                    if (boundary != "allocation-authorized-before-filesystem-effect") return;
                    ++boundaries;
                    if (failureMode == "second-staging" && boundaries == 2)
                    {
                        injected = true;
                        throw new IOException("Fixture: second native staging could not complete.");
                    }
                    if (failureMode != "late-claim" || injected) return;
                    using var lease = PilotDirectoryLease.Open(replacementRoot);
                    rig.Db.RegisterPairAsync("foreign-" + tag, 1,
                        SyncAuthorityPairConfiguration.FromPair(first.Pair)).GetAwaiter().GetResult();
                    rig.Db.RegisterLocalRootBindingAsync("foreign-root-" + tag, "foreign-" + tag, 1,
                        installation, volume, lease.Identity.VolumeSerialNumber.ToString("x8"),
                        lease.Identity.FileId.ToString("x16"), replacementRoot).GetAwaiter().GetResult();
                    injected = true;
                };
                Exception? refusal = null;
                try { await rig.Host.BindEndpointPairAsync(draft, first.Pair.Id, first.Pair.PolicyRevision); }
                catch (Exception error) when (error is InvalidOperationException or IOException) { refusal = error; }
                finally { SyncAuthorityLocalControlNamespaceAllocator.BoundaryForTest = null; }
                Check(injected && refusal is not null,
                    tag + ": actual allocator boundary was reached and the unsafe replacement refused");
                Check(JsonSerializer.Serialize(await rig.Host.LoadPairsAsync()) == originalSet &&
                      await CountActiveRootsAsync(rig, first.Pair.Id) == 2 && await CountActiveRootsAsync(rig, draft.Id) == 0,
                    tag + ": refusal preserves exact old selection/definition and both active roots; candidate stays unpublished and inactive");
                Check(await File.ReadAllTextAsync(leftFile) == "Original left bytes must remain.\n" &&
                      await File.ReadAllTextAsync(rightFile) == "Original right bytes must remain.\n" &&
                      !Directory.EnumerateFileSystemEntries(replacementRoot).Any() && rig.Wire.Writes.Total == 0,
                    tag + ": refusal preserves all original user bytes and performs no transfer");
                continue;
            }
            var replacement = await rig.Host.BindEndpointPairAsync(draft, first.Pair.Id, first.Pair.PolicyRevision);
            Check(replacement.IdentityChanged && replacement.Pair.Id != first.Pair.Id &&
                  replacement.PairSet.Pairs.Count == 1 && replacement.PairSet.SelectedPairId == replacement.Pair.Id &&
                  replacement.Pair.LeftEndpoint == draft.LeftEndpoint && replacement.Pair.RightEndpoint == draft.RightEndpoint &&
                  replacement.Pair.InitialReviewApproved == false,
                tag + ": public accountless edit publishes one freshly reviewed pair with the exact ordered roots");
            Check(await CountActiveRootsAsync(rig, first.Pair.Id) == 0 &&
                  await CountActiveRootsAsync(rig, replacement.Pair.Id) == 2,
                tag + ": both old authorities retire and both new native authorities activate");
            Check(await File.ReadAllTextAsync(leftFile) == "Original left bytes must remain.\n" &&
                  await File.ReadAllTextAsync(rightFile) == "Original right bytes must remain.\n" &&
                  !Directory.EnumerateFileSystemEntries(replacementRoot).Any() && rig.Wire.Writes.Total == 0,
                tag + ": editing preserves both original files and transfers nothing without approval");
        }
    }

    private static async Task<long> CountActiveRootsAsync(Rig rig, string pairId)
    {
        await using var read = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = rig.Db.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
        await read.OpenAsync();
        await using var command = read.CreateCommand();
        command.CommandText = "SELECT count(*) FROM root_binding WHERE pair_id=$pair AND side IN ('local','dropbox') AND state='active';";
        command.Parameters.AddWithValue("$pair", pairId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static Spec Endpoint(SyncEndpointKind kind, string tag) => kind == SyncEndpointKind.Dropbox
        ? CloudPairHostBindingChecks.HostDropbox(tag, "/Authoring " + tag)
        : CloudPairHostBindingChecks.HostDrive(tag, "/Authoring " + tag);

    private static SyncEndpoint Port(Spec spec) => SyncEndpoint.Cloud(spec.Kind, spec.ConnectionId,
        spec.EndpointNamespaceId, spec.FolderId, spec.FolderPath);

    /// <summary>
    /// <summary>
    /// RC639-5: the shared Host recovery entry publishes a recovered pair through the UNCHANGED guarded
    /// authoring path. A first real Host binds the draft, creating and owning the private staging namespace
    /// beside the state root, and is closed. A second real Host opens a FRESH authority database over that SAME
    /// state root, and the synthetic published pair DEFINITIONS are cleared through the supported pair-store API
    /// while the InstallationId, the anchor, the control directory and the marker all survive (this is the exact
    /// state reset the recovery models). The second Host then really collides, the recovery installs the ownership
    /// metadata from the sealed first database, and the normal publication completes disabled, unapproved and with
    /// the draft's TwoWay policy. Restart/resume, replay and the subsequent normal prepare are asserted too.
    /// </summary>
    internal static async Task RecoveredHostSuccessAsync()
    {
        var wire = new Wire();
        SyncPair draft;
        string stateRoot;
        string priorDatabase;
        string priorInstallation;
        using (var first = await Rig.CreateAsync("rhs-first", providerTransport: wire))
        {
            var left = await MatrixPortAsync(first, SyncEndpointKind.Dropbox, "rhs-left");
            var right = await MatrixPortAsync(first, SyncEndpointKind.GoogleDrive, "rhs-right");
            draft = SyncEndpointPairDraft.Create(
                new SyncPairEndpoints(left.Endpoint, right.Endpoint, SyncMode.TwoWay), "Recovered host success");
            var bound = await first.Host.BindEndpointPairAsync(draft);
            Check(bound.Pair.Enabled == false && bound.Pair.InitialReviewApproved == false,
                "the first Host binds the pair that owns the staging namespace, disabled and unapproved");
            priorInstallation = await first.Db.LoadInstallationIdAsync() ?? "";
            stateRoot = first.StateRoot;
            priorDatabase = first.Db.DatabasePath;
        }
        Check(priorInstallation.Length > 0, "the owning installation identity is retained for the reset comparison");
        var second = await Rig.CreateAsync("rhs-second", providerTransport: wire,
            stateDirectory: stateRoot, authorityFileName: "authority-b.db");
        var resetInstallation = await second.Db.LoadInstallationIdAsync() ?? "";
        Check(resetInstallation.Length > 0, "the reset Host reads an installation identity");
        // Clear ONLY the synthetic published pair definitions through the supported pair-store API. The pair
        // store filters the runtime state for removed definitions, so no aggregate invariant is broken.
        await new SyncPairStore(second.StateAuthority).SaveAsync(new SyncPairSet([], null));
        var afterReset = await second.Host.LoadPairsAsync();
        Check(afterReset.Pairs.Count == 0, "the synthetic pair definitions are cleared from the same state root");
        Check(await second.Db.LoadInstallationIdAsync() == resetInstallation && resetInstallation == priorInstallation,
            "the installation identity is unchanged by the reset");
        Exception? collided = null;
        try
        {
            await second.Host.BindEndpointPairAsync(draft);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException)
        {
            collided = error;
        }
        Check(collided is not null &&
              collided.Message.Contains("cannot be adopted without exact retained creation authority",
                  StringComparison.Ordinal),
            "the reset Host really collides with the owned staging namespace and refuses to adopt it");
        await SealAuthorityDatabaseAsync(priorDatabase);
        // The pinned source identity is passed in the exact lowercase 64-hex form the recover-control command
        // documents and validates.
        var priorSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(priorDatabase)))
            .ToLowerInvariant();
        // RC639-6/(c) functional limitation, proven at the real decision: the collision-without-metadata state
        // exists on this Host, but the pair is not yet RETAINED (publication is what retains a pair, and
        // publication requires either the completed allocation or the recovered metadata), so the administrative
        // repair handler cannot see it. The FIRST recovery for such a pair is therefore necessarily performed by
        // the Host recovery entry, which the success case proves; the handler serves an already-retained pair.
        Exception? handlerBeforeRetention = null;
        try
        {
            await new RigCliOperations(second.Host, second.StateAuthority).RecoverRetainedControlAsync(
                draft.Id, priorDatabase, priorSha, second.StateRoot, null, _ => { }, CancellationToken.None);
        }
        catch (Exception error) when (error is SyncCliTargetException or InvalidOperationException or IOException or
                                          InvalidDataException)
        {
            handlerBeforeRetention = error;
        }
        Check(handlerBeforeRetention is { } beforeRetention && beforeRetention.Message.Contains(
                  "No retained folder pair has that exact identity", StringComparison.Ordinal),
            "the handler cannot serve a pair that is not yet retained: its first-repair state is unreachable by construction");
        using var anchor = PilotDirectoryLease.Open(second.StateRoot);
        var target = await second.Host.ResolveRetainedControlRecoveryTargetAsync(draft, anchor);
        var published = await second.Host.RecoverRetainedControlAndAuthorAsync(draft,
            new SyncAuthorityDatabase.RetainedControlSource(priorDatabase, priorSha, draft.Id, target.Side,
                target.RootBindingId), anchor);
        Check(published.Pair.Id == draft.Id && published.Pair.Enabled == false &&
              published.Pair.InitialReviewApproved == false,
            "the recovered pair publishes through the unchanged guarded path, disabled and unapproved");
        Check(published.Pair.Direction == SyncMode.TwoWay,
            "the current draft's TwoWay direction is preserved by the recovered publication");
        Check(await CountActiveRootsAsync(second, draft.Id) == 2,
            "the recovered publication activates both candidate roots");
        // Restart/resume: a second recovery entry over the same state, with the sealed source still pinned,
        // must be idempotent and must not duplicate the binding or the activation.
        var resumed = await second.Host.RecoverRetainedControlAndAuthorAsync(draft,
            new SyncAuthorityDatabase.RetainedControlSource(priorDatabase, priorSha, draft.Id, target.Side,
                target.RootBindingId), anchor, replacingPairId: draft.Id,
            expectedPolicyRevision: published.Pair.PolicyRevision);
        Check(resumed.Pair.Id == draft.Id && await CountActiveRootsAsync(second, draft.Id) == 2 &&
              await ScalarPairCountAsync(second, draft.Id) == 1,
            "a restart-resume replay adds no duplicate pair, binding or activation");
        // The subsequent normal prepare consumer: a policy-only edit must verify and commit in place.
        var edited = draft with { Priority = draft.Priority + 1 };
        var prepared = await second.Host.BindEndpointPairAsync(edited, draft.Id, published.Pair.PolicyRevision);
        Check(prepared.Pair.Id == draft.Id && prepared.Pair.PolicyRevision > published.Pair.PolicyRevision,
            "the ordinary prepare consumer verifies and commits the recovered pair in place");
        // root726 FINDING 2 (probe): this case's pair is CLOUD/CLOUD (GoogleDrive-GoogleDrive), so RegisterPairPolicyAsync
        // returns through its cloud-only branch (SyncAuthorityRuntimeHost.cs:7199) and never reaches the Google
        // registrar at :7220 at all. :7220/:7225 are now wired with publishedPrior for the NON-cloud-only policy
        // path, but that path's atomic branch still needs a LOCAL/cloud Google case to be exercised and proved.
        // RC639-6: the recover-control HANDLER REPLAY, explicitly labelled as such. The handler is an
        // administrative repair entry for an EXISTING retained pair whose metadata recovery is already done, so
        // this leg runs it on the pair the recovery just published: its own argument validation, target
        // resolution, metadata retention (an EXACT REPLAY, not a first repair) and guarded authoring all run and
        // it returns its own projected pair data. The handler is NOT here proven in its intended first-repair
        // state where the metadata recovery has yet to happen; that remains an open, separately recorded item.
        var operationsAgain = new RigCliOperations(second.Host, second.StateAuthority);
        var handlerPublished = await operationsAgain.RecoverRetainedControlAsync(draft.Id, priorDatabase,
            priorSha, second.StateRoot, null, _ => { }, CancellationToken.None);
        Check(handlerPublished.Id == draft.Id && handlerPublished.Enabled == false &&
              handlerPublished.IsRetired == false,
            "the recover-control handler completes the recovered pair disabled through its own entry point");
        Check(await CountActiveRootsAsync(second, draft.Id) == 2 &&
              await ScalarPairCountAsync(second, draft.Id) == 1,
            "the handler success path leaves both candidate roots active and no duplicate pair");
    }

    /// <summary>Seals one fixture authority database into a clean single pinned file.</summary>
    private static async Task SealAuthorityDatabaseAsync(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await command.ExecuteNonQueryAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var sidecar in new[] { path + "-wal", path + "-shm" })
            if (File.Exists(sidecar)) File.Delete(sidecar);
    }

    /// <summary>root726: the exact affected published rows for one pair - definition/revision/selection, every own
    /// root row with id/side/generation/state/identity, and the pair control bindings - so a rollback comparison
    /// covers identities rather than counts. NULL-safe via parameterised reads.</summary>
    private static async Task<string> GooglePublishedAuthorityAsync(Rig rig, string pairId)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = rig.Db.DatabasePath }.ToString());
        await connection.OpenAsync();
        var parts = new List<string>();
        foreach (var sql in new[]
                 {
                     "SELECT configuration_revision, pair_json, selected, state FROM sync_pair WHERE pair_id=$pair;",
                     "SELECT root_binding_id, side, generation, state, identity_json FROM root_binding WHERE pair_id=$pair ORDER BY side, root_binding_id;",
                     "SELECT side, control_namespace_id, binding_generation, status FROM pair_control_namespace WHERE pair_id=$pair ORDER BY side, binding_generation;"
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$pair", pairId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                parts.Add(string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(index =>
                    reader.IsDBNull(index)
                        ? "null"
                        : System.Text.Json.JsonSerializer.Serialize(reader.GetValue(index)))));
        }
        return string.Join(" ;; ", parts);
    }


    private static async Task<long> ScalarPairCountAsync(Rig rig, string pairId)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = rig.Db.DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sync_pair WHERE pair_id=$pair;";
        command.Parameters.AddWithValue("$pair", pairId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// RC639-6: the recover-control HANDLER over a real retained pair. The production CLI modes acquire the real
    /// product lock and stores, so this fixture subclass supplies only the rig Host and its state authority; every
    /// handler semantic, refusal and projection stays the production one. It proves the handler refuses a missing
    /// pinned source, a malformed pinned hash, a missing held local root and an unknown pair, with no pair change.
    /// The handler SUCCESS path is not exercised here.
    /// </summary>
    internal static async Task RecoveredHostCliEntryAsync()
    {
        using var rig = await Rig.CreateAsync("recovered-host-cli");
        var left = await MatrixPortAsync(rig, SyncEndpointKind.Dropbox, "rhc-left");
        var right = await MatrixPortAsync(rig, SyncEndpointKind.GoogleDrive, "rhc-right");
        var draft = SyncEndpointPairDraft.Create(
            new SyncPairEndpoints(left.Endpoint, right.Endpoint, SyncMode.TwoWay), "Recovered host cli");
        var bound = await rig.Host.BindEndpointPairAsync(draft);
        var operations = new RigCliOperations(rig.Host, rig.StateAuthority);
        var before = JsonSerializer.Serialize(await rig.Host.LoadPairsAsync());
        var sha = new string('a', 64);
        var cases = new (string Label, string Pair, string Source, string Hash, string Root)[]
        {
            ("no pinned source", bound.Pair.Id, "", sha, rig.StateRoot),
            ("a malformed pinned hash", bound.Pair.Id, Path.Combine(rig.StateRoot, "x.db"), "not-a-hash",
                rig.StateRoot),
            ("no held local root", bound.Pair.Id, Path.Combine(rig.StateRoot, "x.db"), sha, ""),
            ("an unknown pair", "pair-absent", Path.Combine(rig.StateRoot, "x.db"), sha, rig.StateRoot)
        };
        foreach (var (label, pair, source, hash, root) in cases)
        {
            Exception? refused = null;
            try
            {
                await operations.RecoverRetainedControlAsync(pair, source, hash, root, null, _ => { },
                    CancellationToken.None);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                refused = error;
            }
            Check(refused is not null, "the recover-control handler refuses " + label);
        }
        Check(JsonSerializer.Serialize(await rig.Host.LoadPairsAsync()) == before,
            "the refused handler calls change no retained pair definition");
    }

    /// <summary>The one thing a fixture must supply for the shared CLI semantics: the Host and its state authority.</summary>
    internal sealed class RigCliOperations(SyncAuthorityRuntimeHost host, PilotStateAuthority stateAuthority)
        : SyncCliOperationsBase
    {
        protected override SyncAuthorityRuntimeHost Host => host;
        protected override PilotStateAuthority StateAuthority => stateAuthority;
        public override string Mode => "fixture";
    }

    // RC639-5: the shared Host retained-control recovery entry is reachable on a real Host and fails closed
    // on an unusable pinned source before any candidate preparation, staging allocation or publication, so no
    // pair is created and no retained definition changes. Only the real entry point and the real authority are
    // exercised here; the successful recovered publication through the Host is a separate case.
    internal static async Task RecoveredHostEntryAsync()
    {
        using var rig = await Rig.CreateAsync("recovered-host-entry");
        var left = await MatrixPortAsync(rig, SyncEndpointKind.Dropbox, "recovered-host-left");
        var right = await MatrixPortAsync(rig, SyncEndpointKind.GoogleDrive, "recovered-host-right");
        var draft = SyncEndpointPairDraft.Create(
            new SyncPairEndpoints(left.Endpoint, right.Endpoint, SyncMode.TwoWay), "Recovered host entry");
        var before = JsonSerializer.Serialize(await rig.Host.LoadPairsAsync());
        var absent = Path.Combine(rig.StateRoot, "absent-original.db");
        using var anchor = PilotDirectoryLease.Open(rig.StateRoot);
        Exception? refused = null;
        try
        {
            await rig.Host.RecoverRetainedControlAndAuthorAsync(draft,
                new SyncAuthorityDatabase.RetainedControlSource(absent, new string('a', 64), draft.Id,
                    "local", "root-not-retained"), anchor);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException)
        {
            refused = error;
        }
        Check(refused is not null,
            "the Host recovery entry is reachable and fails closed before any candidate preparation");
        Check(JsonSerializer.Serialize(await rig.Host.LoadPairsAsync()) == before,
            "a refused Host recovery entry creates no pair and changes no retained definition");
        // The wrapper runs the shared recovery FIRST, so a refusal must leave no candidate, operation, root
        // binding or pair row for this draft. That is the pre-preparation invariant this case proves.
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = rig.Db.DatabasePath }
                .ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT (SELECT count(*) FROM operation WHERE pair_id=$pair) + " +
                "(SELECT count(*) FROM root_binding WHERE pair_id=$pair) + " +
                "(SELECT count(*) FROM sync_pair WHERE pair_id=$pair);";
            command.Parameters.AddWithValue("$pair", draft.Id);
            var rows = Convert.ToInt64(await command.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture);
            Check(rows == 0, "a refused Host recovery entry performs no candidate preparation");
        }
    }


    private static SyncPair Draft(Spec left, Spec right, string name, SyncPair? policy = null) =>
        SyncEndpointPairDraft.Create(new SyncPairEndpoints(Port(left), Port(right), SyncMode.TwoWay), name, policy);

    private static async Task BindReviewRunAsync(SyncEndpointKind leftKind, SyncEndpointKind rightKind)
    {
        var tag = leftKind + "-" + rightKind;
        using var rig = await Rig.CreateAsync("public-" + tag);
        var left = Endpoint(leftKind, tag + "-left");
        var right = Endpoint(rightKind, tag + "-right");
        await rig.AddEndpointAsync(left);
        await rig.AddEndpointAsync(right);
        const string text = "Public binding reaches the shared default runner.\n";
        const string path = "public-binding.txt";
        rig.SeedFile(left, path, text);
        var draft = Draft(left, right, tag) with { PropagateDeletions = true, RecoveryRetentionDays = 90 };
        var result = await rig.Host.BindEndpointPairAsync(draft);
        var pair = result.Pair;
        Check(result.Settings is null && pair.LocalRoot.Length == 0 && pair.ControlNamespace is null,
            tag + ": no fake local settings, selected local root or endpoint control binding");
        Check(!pair.Enabled && pair.InitialReviewApproved != true && result.PairSet.SelectedPairId == pair.Id,
            tag + ": published and selected, but disabled and unapproved");
        Check(pair.PropagateDeletions && pair.RecoveryRetentionDays == 90,
            tag + ": requested policy is preserved");
        var leftRoot = await rig.Db.LoadActiveRootBindingIdAsync(pair.Id, SyncAuthorityCloudSides.LogicalLeft);
        var rightRoot = await rig.Db.LoadActiveRootBindingIdAsync(pair.Id, SyncAuthorityCloudSides.LogicalRight);
        Check(!string.IsNullOrWhiteSpace(leftRoot) && !string.IsNullOrWhiteSpace(rightRoot) && leftRoot != rightRoot,
            tag + ": two distinct active logical root authorities");
        Check(rig.Wire.Writes.Total == 0, tag + ": binding transferred no user file");
        await rig.Host.VerifyPairRootConnectionsAsync(pair.Id);
        Check(rig.Wire.Writes.Total == 0, tag + ": connection verification transferred no user file");
        await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
        var review = await rig.Host.LoadBootstrapReviewPageAsync(pair.Id);
        Check(review.InventoryComplete && review.Items.Any(item => item.RelativePath == path),
            tag + ": public first-sync preview contains the actual source file");
        await rig.Host.ApproveBootstrapMergeAsync(pair.Id);
        var enabled = await rig.Host.SetPairEnabledAsync(pair.Id, true);
        var runnable = enabled.Pairs.Single(p => p.Id == pair.Id);
        var run = await rig.Host.RunPairsAsync(new[] { runnable }, "manual");
        Check(run.Items.Count == 1 && run.Items[0].FailureMessage is null,
            tag + ": default public batch completed: " + run.Items.FirstOrDefault()?.FailureMessage);
        var bytes = rightKind == SyncEndpointKind.Dropbox
            ? (await rig.ReadDropboxFileAsync(right, path)).Bytes
            : (await rig.ReadDriveFileAsync(right, path)).Bytes;
        Check(bytes.SequenceEqual(Encoding.UTF8.GetBytes(text)), tag + ": exact nonempty target byte readback");
        Check(rig.Wire.Writes.Total == 1, tag + ": exactly one create effect");
        var monitors = await rig.Host.ConfigureMonitorsAsync([runnable], () => Task.FromResult(true));
        try
        {
            Check(monitors is [{ State: "Monitoring", FailureMessage: null }],
                "R538 " + tag + ": actual host starts both cloud endpoint feeds: " + monitors.FirstOrDefault()?.FailureMessage);
            Check((await rig.Host.LoadMonitorProjectionAsync()).Count(item => item.PairId == pair.Id &&
                    item.Kind == "dropbox_cursor") == 2,
                "R538 " + tag + ": two distinct retained endpoint cursors, without opening a fake local root");
            Check(rig.Wire.Writes.Total == 1, "R538 " + tag + ": monitoring adds no provider content effect");
        }
        finally { await rig.Host.StopMonitorsAsync(); }
        var before = rig.Wire.Writes;
        var unchanged = await rig.Host.RunPairsAsync(new[] { runnable }, "manual");
        Check(unchanged.Items.Count == 1 && unchanged.Items[0].FailureMessage is null && rig.Wire.Writes == before,
            tag + ": unchanged default run performs no repeated create");
    }

    private static async Task RebindAndEditAsync()
    {
        using var rig = await Rig.CreateAsync("public-rebind");
        var left = Endpoint(SyncEndpointKind.GoogleDrive, "edit-left");
        var right = Endpoint(SyncEndpointKind.Dropbox, "edit-right");
        var next = Endpoint(SyncEndpointKind.Dropbox, "edit-next");
        foreach (var spec in new[] { left, right, next }) await rig.AddEndpointAsync(spec);
        var first = await rig.Host.BindEndpointPairAsync(Draft(left, right, "Original"));
        var oldLeft = await rig.Db.LoadActiveRootBindingIdAsync(first.Pair.Id, SyncAuthorityCloudSides.LogicalLeft);
        var oldRight = await rig.Db.LoadActiveRootBindingIdAsync(first.Pair.Id, SyncAuthorityCloudSides.LogicalRight);
        var edit = Draft(left, right, "Renamed", first.Pair) with { Priority = 9, RecoveryRetentionDays = 180 };
        var edited = await rig.Host.BindEndpointPairAsync(edit, first.Pair.Id, first.Pair.PolicyRevision);
        Check(!edited.IdentityChanged && edited.Pair.Id == first.Pair.Id && edited.Pair.Name == "Renamed" &&
              edited.Pair.Priority == 9 && edited.Pair.RecoveryRetentionDays == 180 &&
              edited.Pair.PolicyRevision == first.Pair.PolicyRevision + 1,
            "policy edit preserves identity and increments revision");
        Check(await rig.Db.LoadActiveRootBindingIdAsync(first.Pair.Id, SyncAuthorityCloudSides.LogicalLeft) == oldLeft &&
              await rig.Db.LoadActiveRootBindingIdAsync(first.Pair.Id, SyncAuthorityCloudSides.LogicalRight) == oldRight,
            "policy edit does not replace active roots");
        var replacement = Draft(left, next, "Rebound", edited.Pair);
        var before = JsonSerializer.Serialize(await rig.Host.LoadPairsAsync());
        try
        {
            await rig.Host.BindEndpointPairAsync(replacement, edited.Pair.Id, first.Pair.PolicyRevision);
            throw new Exception("Expected stale revision refusal.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains(
                   "The pair changed after it was selected.", StringComparison.Ordinal)) { ++_assertions; }
        Check(JsonSerializer.Serialize(await rig.Host.LoadPairsAsync()) == before,
            "stale edit leaves the entire published set unchanged");
        SyncPairBindingResult rebound;
        var comparisons = new List<string>();
        SyncAuthorityRuntimeHost.PartitionComparisonProbeForTest = value => comparisons.Add(value);
        try
        {
            rebound = await rig.Host.BindEndpointPairAsync(replacement, edited.Pair.Id, edited.Pair.PolicyRevision);
        }
        catch (Exception error)
        {
            // Diagnostic evidence for the failing real decision: the exact retained rows and comparisons.
            throw new InvalidOperationException("REBIND-DIAGNOSTIC candidateId=" + replacement.Id +
                " replacingId=" + edited.Pair.Id + " firstId=" + first.Pair.Id + " comparisons=[" +
                string.Join(" ; ", comparisons) + "] | " + await AuthorityDumpAsync(rig) + " | " +
                error.GetType().Name + ": " + error.Message, error);
        }
        finally { SyncAuthorityRuntimeHost.PartitionComparisonProbeForTest = null; }
        Check(rebound.IdentityChanged && rebound.Pair.Id != edited.Pair.Id &&
              rebound.PairSet.Pairs.Any(p => p.Id == rebound.Pair.Id) &&
              rebound.PairSet.Pairs.All(p => p.Id != edited.Pair.Id),
            "endpoint-changing edit atomically replaces the old pair while retaining one endpoint");
        Check(!rebound.Pair.Enabled && rebound.Pair.InitialReviewApproved != true &&
              rebound.Pair.Priority == 9 && rebound.Pair.RecoveryRetentionDays == 180,
            "rebind preserves requested policy and requires a new review");
        Check(rig.Wire.Writes.Total == 0, "authoring and all refusals perform no user-file transfers");
    }

    // Real parser -> dispatcher -> shared operations -> real Host/database/authority. The browser seam
    // supplies folder inventory only; the Host still re-reads identities through real provider adapters
    // over the existing scripted HTTP transport. Mode checks are not installed named-pipe/GUI proof.
    internal static async Task RunCliCommandChecksAsync(string root)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(root);
        var previousBrowser = SyncCliOperationsBase.EndpointBrowserFactoryForTest;
        try
        {
            foreach (var mode in new[] { "direct", "running-application" })
            foreach (var leftKind in new[] { SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
            foreach (var rightKind in new[] { SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
            {
                var tag = mode + "-" + leftKind + "-" + rightKind;
                using var rig = await Rig.CreateAsync(tag);
                var left = Endpoint(leftKind, tag + "-left");
                var right = Endpoint(rightKind, tag + "-right");
                await rig.AddEndpointAsync(left);
                await rig.AddEndpointAsync(right);
                var requestedAccounts = new List<string>();
                SyncCliOperationsBase.EndpointBrowserFactoryForTest = (account, _) =>
                {
                    requestedAccounts.Add(account);
                    var spec = new[] { left, right }.Single(candidate => candidate.ConnectionId == account);
                    return Task.FromResult<IDropboxFolderBrowserService>(new FolderInventory(spec));
                };
                var endpointJson = JsonSerializer.Serialize(new
                {
                    left = new { provider = left.Provider, account = left.ConnectionId, path = left.FolderPath },
                    right = new { provider = right.Provider, account = right.ConnectionId, path = right.FolderPath },
                    direction = "TwoWay"
                });
                var output = new StringWriter();
                var errors = new StringWriter();
                var runtimeRequests = 0;
                ISyncCliRuntime Runtime() { ++runtimeRequests; return new CommandRuntime(rig, mode); }
                var refused = await SyncCli.ExecuteAsync(new[] { "--cli", "pairs", "create", "--settings",
                    endpointJson, "--json" }, output, errors, Runtime);
                Check(refused == (int)SyncCliExitCode.Usage && runtimeRequests == 0 && requestedAccounts.Count == 0,
                    tag + ": missing confirmation does not open runtime or browse an account");
                output.GetStringBuilder().Clear();
                errors.GetStringBuilder().Clear();
                var exit = await SyncCli.ExecuteAsync(new[] { "--cli", "pairs", "create", "--settings",
                    endpointJson, "--name", tag, "--enable", "--yes", "--json" }, output, errors, Runtime);
                Check(exit == 0, tag + ": command create succeeded: " + errors);
                using var created = JsonDocument.Parse(output.ToString());
                var data = created.RootElement.GetProperty("data");
                var pairId = data.GetProperty("id").GetString()!;
                Check(!data.GetProperty("enabled").GetBoolean() &&
                      data.GetProperty("firstSyncReviewRequired").GetBoolean() &&
                      data.GetProperty("localRoot").GetString() == string.Empty,
                    tag + ": --enable cannot activate a new unreviewed cloud pair");
                Check(data.GetProperty("endpointDocument").GetProperty("left").GetProperty("connectionId").GetString() == left.ConnectionId &&
                      data.GetProperty("endpointDocument").GetProperty("right").GetProperty("connectionId").GetString() == right.ConnectionId,
                    tag + ": command result identifies both ordered endpoints, not just legacy right provider");
                Check(requestedAccounts.SequenceEqual(new[] { left.ConnectionId, right.ConnectionId }),
                    tag + ": each folder is browsed through its exact endpoint account");
                var saved = (await rig.Host.LoadPairsAsync()).Pairs.Single(pair => pair.Id == pairId);
                output.GetStringBuilder().Clear();
                errors.GetStringBuilder().Clear();
                exit = await SyncCli.ExecuteAsync(new[] { "--cli", "pairs", "edit", "--pair", pairId,
                    "--settings", endpointJson, "--enable", "--yes", "--json" }, output, errors, Runtime);
                Check(exit == (int)SyncCliExitCode.NeedsReview &&
                      JsonSerializer.Serialize((await rig.Host.LoadPairsAsync()).Pairs.Single(pair => pair.Id == pairId)) ==
                      JsonSerializer.Serialize(saved),
                    tag + ": editing cannot enable an unreviewed pair or alter its retained definition");
                output.GetStringBuilder().Clear();
                errors.GetStringBuilder().Clear();
                exit = await SyncCli.ExecuteAsync(new[] { "--cli", "pairs", "edit", "--pair", pairId,
                    "--settings", endpointJson, "--name", "Edited " + tag, "--priority", "3", "--yes", "--json" },
                    output, errors, Runtime);
                Check(exit == 0, tag + ": edit without explicit generation binds observed prior revision: " + errors);
                var edited = (await rig.Host.LoadPairsAsync()).Pairs.Single(pair => pair.Id == pairId);
                Check(edited.Priority == 3 && edited.PolicyRevision > saved.PolicyRevision,
                    tag + ": command edit uses shared policy admission");
                output.GetStringBuilder().Clear();
                errors.GetStringBuilder().Clear();
                exit = await SyncCli.ExecuteAsync(new[] { "--cli", "pairs", "edit", "--pair", pairId,
                    "--settings", endpointJson, "--generation", saved.PolicyRevision.ToString(),
                    "--name", "Stale", "--yes", "--json" }, output, errors, Runtime);
                Check(exit == (int)SyncCliExitCode.NeedsReview &&
                      JsonSerializer.Serialize((await rig.Host.LoadPairsAsync()).Pairs.Single(pair => pair.Id == pairId)) ==
                      JsonSerializer.Serialize(edited) &&
                      rig.Wire.Writes.Total == 0,
                    tag + ": stale command cannot change definition or transfer user files");
            }
        }
        finally { SyncCliOperationsBase.EndpointBrowserFactoryForTest = previousBrowser; }
    }

    private sealed class CommandRuntime(Rig rig, string mode) : SyncCliOperationsBase
    {
        protected override SyncAuthorityRuntimeHost Host => rig.Host;
        protected override PilotStateAuthority StateAuthority => rig.StateAuthority;
        protected override bool ApplicationOwnsRuntime => mode == "running-application";
        public override string Mode => mode;
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FolderInventory(Spec spec) : IDropboxFolderBrowserService
    {
        private DropboxFolderEntry Entry => new(spec.FolderPath.Trim('/'), spec.FolderPath, spec.FolderId);
        public Task<DropboxFolderPage> ListFoldersAsync(string? folderId = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new DropboxFolderPage(new[] { Entry }, null));
        public Task<DropboxFolderPage> SearchFoldersAsync(string query, string? folderId = null,
            CancellationToken cancellationToken = default) => ListFoldersAsync(folderId, cancellationToken);
        public Task<DropboxFolderPage> ContinueFoldersAsync(string cursor,
            CancellationToken cancellationToken = default) => Task.FromResult(new DropboxFolderPage(Array.Empty<DropboxFolderEntry>(), null));
        public Task<DropboxFolderPage> ContinueFolderSearchAsync(string cursor,
            CancellationToken cancellationToken = default) => ContinueFoldersAsync(cursor, cancellationToken);
        public async IAsyncEnumerable<DropboxFolderEntry> EnumerateFoldersAsync(string? folderId = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return Entry;
            await Task.CompletedTask;
        }
        public Task<DropboxFolderEntry> CreateFolderAsync(string? parentFolderId, string name,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Authoring does not create an endpoint folder.");
    }

    /// <summary>
    /// RC639-5/(b): the REAL restart after a failed publication. A real provider INPUT failure is injected
    /// between the real metadata-recovery commit and the real publication; its no-transfer and no-publication
    /// result is retained; the Host and anchor are disposed cleanly; and a NEW Host opens the SAME authority
    /// database, state root, InstallationId and native control to complete ONLY the missing publication through
    /// the SAME production recovery entry.
    /// </summary>
    internal static async Task RecoveredHostRestartAsync()
    {
        var wire = new Wire();
        SyncPair draft; string stateRoot; string priorDatabase;
        using (var first = await Rig.CreateAsync("rhs-restart-a", providerTransport: wire))
        {
            var left = await MatrixPortAsync(first, SyncEndpointKind.Dropbox, "rhr-left");
            var right = await MatrixPortAsync(first, SyncEndpointKind.GoogleDrive, "rhr-right");
            draft = SyncEndpointPairDraft.Create(
                new SyncPairEndpoints(left.Endpoint, right.Endpoint, SyncMode.TwoWay), "Recovered host restart");
            var bound = await first.Host.BindEndpointPairAsync(draft);
            Check(bound.Pair.Enabled == false, "the restart fixture binds the owning pair disabled");
            stateRoot = first.StateRoot;
            priorDatabase = first.Db.DatabasePath;
        }
        await SealAuthorityDatabaseAsync(priorDatabase);
        var priorSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(priorDatabase)))
            .ToLowerInvariant();
        var second = await Rig.CreateAsync("rhs-restart-b", providerTransport: wire,
            stateDirectory: stateRoot, authorityFileName: "authority-b.db");
        await new SyncPairStore(second.StateAuthority).SaveAsync(new SyncPairSet([], null));
        // The real collision state is established by the real authoring attempt before anything is injected.
        try { await second.Host.BindEndpointPairAsync(draft); }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException) { }
        // RC639-6/(c) functional limitation, proven at the real decision: the collision-without-metadata state
        // exists on this Host, but the pair is not yet RETAINED (publication is what retains a pair, and
        // publication requires either the completed allocation or the recovered metadata), so the administrative
        // repair handler cannot see it. The FIRST recovery for such a pair is therefore necessarily performed by
        // the Host recovery entry, which the success case proves; the handler serves an already-retained pair.
        Exception? handlerBeforeRetention = null;
        try
        {
            await new RigCliOperations(second.Host, second.StateAuthority).RecoverRetainedControlAsync(
                draft.Id, priorDatabase, priorSha, second.StateRoot, null, _ => { }, CancellationToken.None);
        }
        catch (Exception error) when (error is SyncCliTargetException or InvalidOperationException or IOException or
                                          InvalidDataException)
        {
            handlerBeforeRetention = error;
        }
        Check(handlerBeforeRetention is { } beforeRetention && beforeRetention.Message.Contains(
                  "No retained folder pair has that exact identity", StringComparison.Ordinal),
            "the handler cannot serve a pair that is not yet retained: its first-repair state is unreachable by construction");
        (string Side, string RootBindingId) target;
        using (var targetAnchor = PilotDirectoryLease.Open(second.StateRoot))
            target = await second.Host.ResolveRetainedControlRecoveryTargetAsync(draft, targetAnchor);
        var transfersBefore = wire.Writes;
        using (var anchor = PilotDirectoryLease.Open(second.StateRoot))
        {
            wire.ProviderInputFailureForTest = _ => true;
            Exception? failure = null;
            try
            {
                await second.Host.RecoverRetainedControlAndAuthorAsync(draft,
                    new SyncAuthorityDatabase.RetainedControlSource(priorDatabase, priorSha, draft.Id,
                        target.Side, target.RootBindingId), anchor);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or
                                              InvalidDataException or HttpRequestException)
            {
                failure = error;
            }
            Check(failure is not null, "the injected provider INPUT failure really fails the publication");
            Check(await ScalarAsync(second, "SELECT count(*) FROM history_event WHERE " +
                      "event_kind='retained_control_recovered' AND json_extract(detail_json,'$.PairId')='" +
                      draft.Id + "';") == 1,
                "the real metadata recovery commit happened BEFORE the failed publication");
            Check(await CountActiveRootsAsync(second, draft.Id) == 0,
                "the failed publication activated no candidate root");
            Check(await ScalarAsync(second, "SELECT count(*) FROM sync_pair WHERE pair_id='" + draft.Id +
                      "';") == 1, "the failed publication left exactly one candidate pair row");
            Check(wire.Writes == transfersBefore, "the failed publication performed no provider transfer");
            wire.ProviderInputFailureForTest = null;
        }
        second.Dispose();
        // The reopened Host opens the EXACT authority database file the failed attempt used, over the same
        // state root, installation identity and native control.
        using var third = await Rig.CreateAsync("rhs-restart-c", providerTransport: wire,
            stateDirectory: stateRoot, authorityFileName: "../rhs-restart-b/authority-b.db");
        Check(await ScalarAsync(third, "SELECT count(*) FROM history_event WHERE " +
                  "event_kind='retained_control_recovered' AND json_extract(detail_json,'$.PairId')='" +
                  draft.Id + "';") == 1,
            "the reopened Host opens the SAME authority database with the retained recovery metadata");
        using var anchorC = PilotDirectoryLease.Open(third.StateRoot);
        var published = await third.Host.RecoverRetainedControlAndAuthorAsync(draft,
            new SyncAuthorityDatabase.RetainedControlSource(priorDatabase, priorSha, draft.Id, target.Side,
                target.RootBindingId), anchorC);
        Check(published.Pair.Id == draft.Id && published.Pair.Enabled == false &&
              published.Pair.InitialReviewApproved == false && published.Pair.Direction == SyncMode.TwoWay,
            "the reopened Host completes ONLY the missing publication, disabled TwoWay with fresh review");
        Check(await CountActiveRootsAsync(third, draft.Id) == 2,
            "the completed publication activates both candidate roots");
        Check(await ScalarAsync(third, "SELECT count(*) FROM history_event WHERE " +
                  "event_kind='retained_control_recovered' AND json_extract(detail_json,'$.PairId')='" +
                  draft.Id + "';") == 1 &&
              await ScalarAsync(third, "SELECT count(*) FROM pair_control_namespace WHERE pair_id='" +
                  draft.Id + "' AND status='active';") == 1,
            "the restart created no duplicate recovery history or control binding");
        Check(await ScalarAsync(third, "SELECT count(*) FROM operation WHERE pair_id='" + draft.Id +
                  "' AND status='completed';") >= 0 && wire.Writes == transfersBefore,
            "the completed publication performed no provider transfer");
        var prepared = await third.Host.BindEndpointPairAsync(draft with { Priority = draft.Priority + 1 },
            draft.Id, published.Pair.PolicyRevision);
        Check(prepared.Pair.Id == draft.Id && prepared.Pair.PolicyRevision > published.Pair.PolicyRevision,
            "the ordinary prepare consumer commits the restarted pair in place");
    }

    /// <summary>One scalar count from a fixture database.</summary>
    private static async Task<long> ScalarAsync(Rig rig, string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = rig.Db.DatabasePath }
                .ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Diagnostic: the exact retained pair and root-binding rows at a failing real decision.</summary>
    private static async Task<string> AuthorityDumpAsync(Rig rig)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = rig.Db.DatabasePath }.ToString());
        await connection.OpenAsync();
        var text = new System.Text.StringBuilder();
        foreach (var sql in new[]
                 {
                     "SELECT pair_id || ' state=' || state || ' rev=' || configuration_revision || ' json=' || COALESCE(pair_json,'<null>') FROM sync_pair ORDER BY pair_id;",
                     "SELECT pair_id || '/' || side || ' gen=' || generation || ' state=' || state || ' id=' || identity_json FROM root_binding ORDER BY pair_id, side, generation;"
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) text.Append(reader.GetString(0)).Append(" || ");
        }
        return text.ToString();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        ++_assertions;
        Console.WriteLine("PASS " + message);
    }
}
