using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Rig = CloudPairHostBindingChecks.HostRig;
using Spec = CloudPairHostBindingChecks.EndpointSpec;

// The complete ordered endpoint x sync mode matrix over the REAL Host: all nine ordered endpoint type
// pairs (Local, Dropbox, Google Drive) crossed with all three sync modes. Each case is bound through the
// public Host.BindEndpointPairAsync on its own fresh case identity, reviewed through the real bootstrap
// preview, approved, run, run again unchanged over the identical sources, and then run once more after the
// Host is disposed and reopened over the same persistent provider transport and the same case authority.
// Local sides are real disposable NTFS directories reached through their own native directory identity;
// cloud sides are the real Dropbox and Google Drive adapters. Only the provider HTTP transport and the
// credential values are synthetic and in-process: no network, no live account, no credential store and no
// installed state. Every case is bounded and reported on its own, so one failure never erases another
// case's result and no case is ever turned into a skip. This is owner functional proof of the existing
// complete acceptance scope; it is not a formal reviewer or challenger verdict and it does not discharge
// retained-original, clone, live or installed acceptance.
[SupportedOSPlatform("windows")]
internal static class OrderedEndpointModeChecks
{
    private const string LeftOnlyName = "ordered-only-left.txt";
    private const string RightOnlyName = "ordered-only-right.txt";
    private const string EqualName = "ordered-equal.txt";
    private const string ReportFileName = "ordered-endpoint-mode-matrix.json";
    private static readonly TimeSpan CaseBudget = TimeSpan.FromSeconds(150);
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };

    private static readonly List<Dictionary<string, object?>> Rows = [];
    private static int _assertions;
    private static string _root = string.Empty;
    private static string _reportPath = string.Empty;

    /// <summary>One ordered endpoint of one matrix case: its logical endpoint plus its real fixture side.</summary>
    private sealed record ModePort(SyncEndpoint Endpoint, Spec? Cloud, string? LocalRoot, string? RecoveryFolderId);

    private static void Require(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidDataException("Ordered endpoint mode matrix assertion failed: " + label);
    }

    private static bool BytesEqual(byte[]? actual, byte[]? expected) =>
        expected is null ? actual is null : actual is not null && actual.SequenceEqual(expected);

    private static int WireWrites(Rig rig) => rig.Wire.Writes.Total;
    private static int WireUpdates(Rig rig) => rig.Wire.Updates.Total;
    private static int WireDeletes(Rig rig) => rig.Wire.RecoveryDeleteRequests;

    internal static async Task RunAsync(string root)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated ordered endpoint mode matrix fixture root.");
        Directory.CreateDirectory(_root);
        _reportPath = Path.Combine(_root, ReportFileName);
        Rows.Clear();
        _assertions = 0;
        CloudPairHostBindingChecks.UseFixtureRoot(_root);
        // The ordinary production planning route is what this matrix must exercise, so the independent
        // planning switch is held at its default false for the whole route, exactly as the default public
        // route does.
        var priorPlanning = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            var kinds = new[] { SyncEndpointKind.Local, SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive };
            foreach (var leftKind in kinds)
            foreach (var rightKind in kinds)
            foreach (var mode in Enum.GetValues<SyncMode>())
                await RunCaseAsync(leftKind, rightKind, mode);
            await WriteReportAsync();
            var failed = Rows.Where(row => !string.Equals(row["result"] as string, "PASS", StringComparison.Ordinal))
                .Select(row => row["case"] as string).ToArray();
            if (Rows.Count != 27 || failed.Length > 0)
                throw new InvalidDataException("The ordered endpoint mode matrix is incomplete: " + Rows.Count +
                    " of 27 cases present; failed cases " + string.Join(", ", failed) + ".");
            Console.WriteLine("PASS ordered endpoint mode matrix: 27 cases, " + _assertions +
                " assertions; report " + _reportPath);
        }
        finally
        {
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = priorPlanning;
        }
    }

    private static async Task RunCaseAsync(SyncEndpointKind leftKind, SyncEndpointKind rightKind, SyncMode mode)
    {
        var tag = leftKind + "-" + rightKind + "-" + mode;
        var caseName = "ordered-mode-" + tag;
        var phases = new List<string>();
        var row = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["case"] = tag,
            ["left_kind"] = leftKind.ToString(),
            ["right_kind"] = rightKind.ToString(),
            ["mode"] = mode.ToString(),
            ["result"] = "FAIL",
            ["fixture_path"] = Path.Combine(_root, "host", caseName),
            ["phases"] = phases
        };
        Rows.Add(row);
        var caseAssertions = _assertions;
        var watch = Stopwatch.StartNew();
        try
        {
            // One persistent transport per case, so the reopened Host observes exactly what the previous
            // Host left behind on the provider side.
            using var transport = new CloudPairHostBindingChecks.HostWire();
            using var deadline = new CancellationTokenSource(CaseBudget);
            var token = deadline.Token;
            var leftOnlyBytes = Encoding.UTF8.GetBytes("Ordered logical left payload for " + tag + "\n");
            var rightOnlyBytes = Encoding.UTF8.GetBytes("Ordered logical right payload for " + tag + "\n");
            var equalBytes = Encoding.UTF8.GetBytes("Ordered identical payload for " + tag + "\n");
            // The stored direction is logical left/right: upload-only carries left to right and download-only
            // carries right to left whichever provider each side occupies, and the engine reverses the
            // physical port direction for a reversed (cloud-left/local-right) layout.
            var leftToRight = mode != SyncMode.DownloadOnly;
            var rightToLeft = mode != SyncMode.UploadOnly;
            var pairId = string.Empty;
            SyncEndpointRecord? expectedLeft = null;
            SyncEndpointRecord? expectedRight = null;
            ModePort? leftPort = null;
            ModePort? rightPort = null;
            var hasCloud = false;
            var leftReceived = (byte[]?)null;
            var rightReceived = (byte[]?)null;
            var permittedProviderCreates = 0;
            var repeatRequests = 0;
            var restartRequests = 0;

            using (var rig = await Rig.CreateAsync(caseName, transport))
            {
                var caseDirectory = Directory.GetParent(rig.StateRoot)?.FullName
                    ?? throw new InvalidDataException("The ordered mode matrix fixture has no case directory.");
                var left = await PortAsync(rig, leftKind, caseDirectory, tag + "-left", "left");
                var right = await PortAsync(rig, rightKind, caseDirectory, tag + "-right", "right");
                leftPort = left;
                rightPort = right;
                hasCloud = left.Cloud is not null || right.Cloud is not null;
                // Every originally seeded payload exists before the pair is bound, so binding and the first
                // review can both be proved to issue no user-file effect at all.
                await SeedAsync(rig, left, LeftOnlyName, leftOnlyBytes);
                await SeedAsync(rig, right, RightOnlyName, rightOnlyBytes);
                await SeedAsync(rig, left, EqualName, equalBytes);
                await SeedAsync(rig, right, EqualName, equalBytes);

                var draft = SyncEndpointPairDraft.Create(
                    new SyncPairEndpoints(left.Endpoint, right.Endpoint, mode), "Ordered mode " + tag);
                expectedLeft = draft.LeftEndpoint;
                expectedRight = draft.RightEndpoint;
                var bound = await rig.Host.BindEndpointPairAsync(draft, cancellationToken: token,
                    recoveryFolder: RecoveryEntry(left, right));
                phases.Add("bind");
                pairId = bound.Pair.Id;
                Require(bound.Pair.LeftEndpoint == expectedLeft && bound.Pair.RightEndpoint == expectedRight &&
                        bound.Pair.Direction == mode && bound.Pair.InitialReviewApproved == false,
                    tag + ": the public binding retains the exact ordered endpoint identities and direction " +
                    "without review approval");
                Require(WireWrites(rig) == 0 && await ReadAsync(rig, right, LeftOnlyName) is null &&
                        await ReadAsync(rig, left, RightOnlyName) is null,
                    tag + ": binding issues no user-file effect on either ordered endpoint");

                await rig.Host.RefreshBootstrapInventoryAsync(bound.Pair.Id, token);
                var review = await rig.Host.LoadBootstrapReviewPageAsync(bound.Pair.Id, cancellationToken: token);
                phases.Add("review");
                Require(review.InventoryComplete &&
                        review.Items.Any(item => item.RelativePath == LeftOnlyName) &&
                        review.Items.Any(item => item.RelativePath == RightOnlyName) &&
                        review.Items.Any(item => item.RelativePath == EqualName),
                    tag + ": the complete bootstrap preview exposes both one-sided payloads and the identical " +
                    "payload, observed " + review.Items.Count + " rows");
                Require(WireWrites(rig) == 0 && await ReadAsync(rig, right, LeftOnlyName) is null &&
                        await ReadAsync(rig, left, RightOnlyName) is null,
                    tag + ": the bootstrap preview issues no user-file effect either");

                await rig.Host.ApproveBootstrapMergeAsync(bound.Pair.Id, token);
                var runnable = (await rig.Host.SetPairEnabledAsync(bound.Pair.Id, true, token)).Pairs
                    .Single(item => item.Id == bound.Pair.Id);
                Require(runnable.InitialReviewApproved == true && runnable.Enabled &&
                        runnable.LeftEndpoint == expectedLeft && runnable.RightEndpoint == expectedRight &&
                        runnable.Direction == mode,
                    tag + ": the approved enabled retained pair still carries its ordered endpoints and direction");

                // Provider write accounting starts after binding, review and approval, so the private staging
                // and Recovery setup is never counted as user payload traffic.
                var writesBeforeRun = WireWrites(rig);
                var updatesBeforeRun = WireUpdates(rig);
                var deletesBeforeRun = WireDeletes(rig);
                var run = await rig.Host.RunPairsAsync([runnable], "ordered endpoint mode matrix",
                    cancellationToken: token);
                phases.Add("run");
                CheckBatch(run, 1, tag + ": the ordinary host run completes its one pair with no failure and no " +
                    "hidden blocked, conflict or unknown outcome");

                leftReceived = await ReadAsync(rig, left, RightOnlyName);
                rightReceived = await ReadAsync(rig, right, LeftOnlyName);
                Require(leftToRight ? BytesEqual(rightReceived, leftOnlyBytes) : rightReceived is null,
                    tag + ": the logical left-to-right transfer obeys the selected mode with exact destination bytes");
                Require(rightToLeft ? BytesEqual(leftReceived, rightOnlyBytes) : leftReceived is null,
                    tag + ": the logical right-to-left transfer obeys the selected mode with exact destination bytes");
                Require(BytesEqual(await ReadAsync(rig, left, LeftOnlyName), leftOnlyBytes) &&
                        BytesEqual(await ReadAsync(rig, right, RightOnlyName), rightOnlyBytes),
                    tag + ": both ordered source payloads survive the run byte-exact");
                Require(BytesEqual(await ReadAsync(rig, left, EqualName), equalBytes) &&
                        BytesEqual(await ReadAsync(rig, right, EqualName), equalBytes),
                    tag + ": the identical payload is unchanged on both ordered endpoints");
                permittedProviderCreates = (leftToRight && right.Cloud is not null ? 1 : 0) +
                    (rightToLeft && left.Cloud is not null ? 1 : 0);
                Require(WireWrites(rig) - writesBeforeRun == permittedProviderCreates &&
                        WireUpdates(rig) - updatesBeforeRun == 0 && WireDeletes(rig) == deletesBeforeRun,
                    tag + ": the run issued exactly the permitted payload creates and no update, " +
                    "retained-predecessor copy, delete or move, observed " + (WireWrites(rig) - writesBeforeRun) +
                    " creates against " + permittedProviderCreates + " permitted and " +
                    (WireUpdates(rig) - updatesBeforeRun) + " updates");

                var writesBeforeRepeat = WireWrites(rig);
                var updatesBeforeRepeat = WireUpdates(rig);
                var deletesBeforeRepeat = WireDeletes(rig);
                var requestsBeforeRepeat = rig.Wire.TotalRequests;
                var operationsBeforeRepeat = await OperationCountAsync(rig, bound.Pair.Id);
                var repeat = await rig.Host.RunPairsAsync([runnable],
                    "ordered endpoint mode matrix unchanged repeat", cancellationToken: token);
                phases.Add("repeat");
                repeatRequests = rig.Wire.TotalRequests - requestsBeforeRepeat;
                CheckBatch(repeat, 1,
                    tag + ": the unchanged repeat completes without failure");
                Require(WireWrites(rig) == writesBeforeRepeat && WireUpdates(rig) == updatesBeforeRepeat &&
                        WireDeletes(rig) == deletesBeforeRepeat &&
                        await OperationCountAsync(rig, bound.Pair.Id) == operationsBeforeRepeat,
                    tag + ": the unchanged repeat issues no additional payload write, update, delete or move " +
                    "and records no new operation");
                Require(!hasCloud || repeatRequests > 0,
                    tag + ": the unchanged repeat performed the real provider reads its cloud endpoints require, " +
                    "observed " + repeatRequests + " provider requests");
                Require(await EndpointPayloadsMatchAsync(rig, left, right, leftToRight, rightToLeft,
                            leftOnlyBytes, rightOnlyBytes, equalBytes),
                    tag + ": the unchanged repeat preserves every exact payload byte and the excluded " +
                    "direction's absence");

                row["payload_bytes"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["left_only"] = (long)leftOnlyBytes.Length,
                    ["right_only"] = (long)rightOnlyBytes.Length,
                    ["equal"] = (long)equalBytes.Length
                };
                row["direction"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["left_to_right_copied"] = leftToRight,
                    ["right_to_left_copied"] = rightToLeft,
                    ["permitted_copies"] = (leftToRight ? 1 : 0) + (rightToLeft ? 1 : 0),
                    ["excluded_copies"] = (leftToRight ? 0 : 1) + (rightToLeft ? 0 : 1),
                    ["permitted_provider_creates"] = permittedProviderCreates,
                    ["equal_payload_effects"] = 0,
                    ["left_only_absent_on_right"] = rightReceived is null,
                    ["right_only_absent_on_left"] = leftReceived is null
                };
                row["repeat"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["writes"] = WireWrites(rig) - writesBeforeRepeat,
                    ["updates"] = WireUpdates(rig) - updatesBeforeRepeat,
                    ["deletes"] = WireDeletes(rig) - deletesBeforeRepeat,
                    ["new_operations"] = await OperationCountAsync(rig, bound.Pair.Id) - operationsBeforeRepeat,
                    ["provider_requests"] = repeatRequests,
                    ["payloads_identical"] = true
                };
            }

            // Persisted restart: the same case directory, the same authority database and the same persistent
            // provider transport, reopened with a completely new Host. No endpoint is reseeded, no pair is
            // reset or rebound and no provider object is recreated.
            using (var reopened = await Rig.CreateAsync(caseName, transport))
            {
                await ReopenCloudAsync(reopened, leftPort);
                await ReopenCloudAsync(reopened, rightPort);
                var saved = (await reopened.Host.LoadPairsAsync(token)).Pairs
                    .Single(item => item.Id == pairId);
                Require(saved.LeftEndpoint?.Kind == leftKind && saved.RightEndpoint?.Kind == rightKind &&
                        saved.LeftEndpoint == expectedLeft && saved.RightEndpoint == expectedRight &&
                        saved.Direction == mode && saved.InitialReviewApproved == true && saved.Enabled,
                    tag + ": the reopened authority retains the exact ordered endpoints, direction and review " +
                    "approval");
                var writesBeforeRestart = WireWrites(reopened);
                var updatesBeforeRestart = WireUpdates(reopened);
                var deletesBeforeRestart = WireDeletes(reopened);
                var requestsBeforeRestart = reopened.Wire.TotalRequests;
                var operationsBeforeRestart = await OperationCountAsync(reopened, pairId);
                var restarted = await reopened.Host.RunPairsAsync([saved],
                    "ordered endpoint mode matrix persisted restart", cancellationToken: token);
                phases.Add("restart");
                restartRequests = reopened.Wire.TotalRequests - requestsBeforeRestart;
                CheckBatch(restarted, 1, tag + ": the persisted restart run completes without failure");
                Require(WireWrites(reopened) == writesBeforeRestart &&
                        WireUpdates(reopened) == updatesBeforeRestart &&
                        WireDeletes(reopened) == deletesBeforeRestart &&
                        await OperationCountAsync(reopened, pairId) == operationsBeforeRestart,
                    tag + ": the persisted restart replays no write, update, delete or move and records no new " +
                    "operation");
                Require(!hasCloud || restartRequests > 0,
                    tag + ": the persisted restart performed the real provider reads its cloud endpoints " +
                    "require, observed " + restartRequests + " provider requests");
                Require(await EndpointPayloadsMatchAsync(reopened, leftPort!, rightPort!, leftToRight,
                            rightToLeft, leftOnlyBytes, rightOnlyBytes, equalBytes),
                    tag + ": the persisted restart keeps every payload byte-exact and keeps the excluded " +
                    "direction absent");
                row["restart"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["pair_id"] = pairId,
                    ["definition_matched"] = true,
                    ["writes"] = WireWrites(reopened) - writesBeforeRestart,
                    ["updates"] = WireUpdates(reopened) - updatesBeforeRestart,
                    ["deletes"] = WireDeletes(reopened) - deletesBeforeRestart,
                    ["new_operations"] = await OperationCountAsync(reopened, pairId) - operationsBeforeRestart,
                    ["provider_requests"] = restartRequests,
                    ["payloads_identical"] = true
                };
            }

            row["result"] = "PASS";
            Console.WriteLine("PASS " + tag + " (" + watch.ElapsedMilliseconds + " ms)");
        }
        catch (Exception error)
        {
            row["error"] = error.GetType().FullName + ": " + error.Message;
            Console.Error.WriteLine("FAIL " + tag + " after " + string.Join("+", phases) + ": " +
                                    error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            row["elapsed_ms"] = watch.ElapsedMilliseconds;
            row["case_assertions"] = _assertions - caseAssertions;
            TryWriteReport();
        }
    }

    /// <summary>
    /// Whether both permitted copies hold their exact source bytes and both excluded copies are simply absent,
    /// re-read now through each endpoint's own real adapter or its own real local file.
    /// </summary>
    private static async Task<bool> EndpointPayloadsMatchAsync(Rig rig, ModePort left, ModePort right,
        bool leftToRight, bool rightToLeft, byte[] leftOnlyBytes, byte[] rightOnlyBytes, byte[] equalBytes)
    {
        var leftCarried = await ReadAsync(rig, left, RightOnlyName);
        var rightCarried = await ReadAsync(rig, right, LeftOnlyName);
        return (leftToRight ? BytesEqual(rightCarried, leftOnlyBytes) : rightCarried is null) &&
               (rightToLeft ? BytesEqual(leftCarried, rightOnlyBytes) : leftCarried is null) &&
               BytesEqual(await ReadAsync(rig, left, LeftOnlyName), leftOnlyBytes) &&
               BytesEqual(await ReadAsync(rig, right, RightOnlyName), rightOnlyBytes) &&
               BytesEqual(await ReadAsync(rig, left, EqualName), equalBytes) &&
               BytesEqual(await ReadAsync(rig, right, EqualName), equalBytes);
    }

    /// <summary>
    /// Whether one batch completed its exact single pair with no failure and no hidden outcome. The full
    /// batch is attached only when the check fails, so a passing case never pays for serializing it.
    /// </summary>
    private static void CheckBatch(SyncAuthorityBatchResult batch, int expectedItems, string label)
    {
        var completed = batch.Items.Count == expectedItems && !batch.Stopped &&
            batch.Items.All(item => item.FailureType is null && item.FailureMessage is null &&
                item.Result is { Blocked: 0, Conflicts: 0, Failed: 0, NotAttempted: 0, NeedsVerification: 0,
                    Stopped: false });
        Require(completed, completed ? label : label + " [" + JsonSerializer.Serialize(batch) + "]");
    }

    /// <summary>One ordered case endpoint: a real local directory or one registered cloud endpoint.</summary>
    private static async Task<ModePort> PortAsync(Rig rig, SyncEndpointKind kind, string caseDirectory,
        string tag, string side)
    {
        if (kind == SyncEndpointKind.Local)
        {
            var path = Directory.CreateDirectory(Path.Combine(caseDirectory, "local-" + side)).FullName;
            using var lease = PilotDirectoryLease.Open(path);
            return new ModePort(SyncEndpoint.Local(path, new SyncLocalEndpointIdentity(
                lease.Identity.VolumeSerialNumber, lease.Identity.FileId)), null, path, null);
        }
        return await RegisterCloudAsync(rig, CloudSpec(kind, tag));
    }

    /// <summary>
    /// Re-registers one already-authored cloud endpoint on the reopened rig. The retained provider account
    /// row is an idempotent upsert of the identical identity and the transport keeps its own object state, so
    /// nothing is reseeded, reset or rebound: only this rig's own per-endpoint binding dictionaries are
    /// repopulated. The Recovery container is deliberately NOT re-registered here, because the persistent
    /// transport already serves the exact mapping the first phase established or created. A local endpoint
    /// needs nothing at all, because its directory and native identity are unchanged.
    /// </summary>
    private static async Task ReopenCloudAsync(Rig rig, ModePort? port)
    {
        if (port?.Cloud is not { } cloud) return;
        await rig.AddEndpointAsync(cloud);
    }

    private static Spec CloudSpec(SyncEndpointKind kind, string tag) => kind == SyncEndpointKind.Dropbox
        ? CloudPairHostBindingChecks.HostDropbox(tag, "/Dropbox Ordered " + tag)
        : CloudPairHostBindingChecks.HostDrive(tag, "/Drive Ordered " + tag);

    private static async Task<ModePort> RegisterCloudAsync(Rig rig, Spec cloud)
    {
        await rig.AddEndpointAsync(cloud);
        // The one canonical fixture Recovery container of this exact endpoint's own account and namespace,
        // registered once per ordered endpoint so two cloud sides never share one folder identity.
        var recoveryFolderId = cloud.Kind == SyncEndpointKind.Dropbox
            ? "id:ordered-recovery-" + cloud.AccountId
            : "drive-ordered-recovery-" + cloud.AccountId;
        rig.RegisterFolder(cloud, Rig.RecoveryContainerPath, recoveryFolderId);
        return new ModePort(SyncEndpoint.Cloud(cloud.Kind, cloud.ConnectionId, cloud.EndpointNamespaceId,
            cloud.FolderId, cloud.FolderPath), cloud, null, recoveryFolderId);
    }

    /// <summary>
    /// The one registered canonical Recovery container of whichever ordered endpoint is a cloud endpoint,
    /// supplied explicitly only through the exact canonical product container path the fixture supports.
    /// </summary>
    private static DropboxFolderEntry? RecoveryEntry(ModePort left, ModePort right)
    {
        var cloud = left.Cloud is not null ? left : right;
        return cloud.Cloud is null
            ? null
            : new DropboxFolderEntry("Recovery", Rig.RecoveryContainerPath, cloud.RecoveryFolderId!);
    }

    private static async Task SeedAsync(Rig rig, ModePort port, string relativePath, byte[] bytes)
    {
        if (port.LocalRoot is { } local)
        {
            await File.WriteAllBytesAsync(Path.Combine(local, relativePath), bytes);
            return;
        }
        rig.SeedFile(port.Cloud!, relativePath, Encoding.UTF8.GetString(bytes));
    }

    /// <summary>One payload read through the real adapter or the real local file it belongs to.</summary>
    private static async Task<byte[]?> ReadAsync(Rig rig, ModePort port, string relativePath)
    {
        if (port.LocalRoot is { } local)
        {
            var file = Path.Combine(local, relativePath);
            return File.Exists(file) ? await File.ReadAllBytesAsync(file) : null;
        }
        var cloud = port.Cloud!;
        if (cloud.Kind == SyncEndpointKind.Dropbox)
        {
            var entries = await rig.DropboxClient(cloud).ListEntriesAsync();
            if (!entries.Any(entry => entry.Kind == PilotEntryKind.File &&
                    string.Equals(entry.RelativePath, relativePath, StringComparison.Ordinal))) return null;
            return (await rig.ReadDropboxFileAsync(cloud, relativePath)).Bytes;
        }
        var inventory = await rig.DriveClient(cloud).ListInventoryAsync();
        if (!inventory.Entries.Any(entry => entry.Kind == GoogleDriveEntryKind.Blob &&
                string.Equals(entry.RelativePath, relativePath, StringComparison.Ordinal))) return null;
        return (await rig.ReadDriveFileAsync(cloud, relativePath)).Bytes;
    }

    /// <summary>
    /// The real authority's own dispatched-operation count for this exact pair. Every effect the pair
    /// dispatched, of whatever kind, is one row, so an unchanged repeat or a persisted restart can only be
    /// proved by this count not growing.
    /// </summary>
    private static async Task<long> OperationCountAsync(Rig rig, string pairId)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = rig.Db.DatabasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM operation WHERE pair_id=$pair;";
        command.Parameters.AddWithValue("$pair", pairId);
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>The identity of the exact artifact executing this route, read from the file itself.</summary>
    private static Dictionary<string, object?> AssemblyIdentity()
    {
        var assembly = typeof(OrderedEndpointModeChecks).Assembly;
        var location = assembly.Location;
        long? bytes = null;
        string? hash = null;
        string? written = null;
        if (!string.IsNullOrEmpty(location) && File.Exists(location))
        {
            var info = new FileInfo(location);
            bytes = info.Length;
            written = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture);
            using var stream = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = assembly.GetName().Name,
            ["full_name"] = assembly.FullName,
            ["version"] = assembly.GetName().Version?.ToString(),
            ["location"] = location,
            ["bytes"] = bytes,
            ["sha256"] = hash,
            ["last_write_utc"] = written,
            ["source_revision"] = BuildIdentity.SourceRevision,
            ["informational_version"] = BuildIdentity.InformationalVersion
        };
    }

    private static Dictionary<string, object?> BuildReport()
    {
        var passed = Rows.Count(row => string.Equals(row["result"] as string, "PASS", StringComparison.Ordinal));
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["generated_by"] = "OrderedEndpointModeChecks (tests/SyncShark.Core.Tests)",
            ["route"] = "OrderedEndpointModeChecks.RunAsync",
            ["fixture_root"] = _root,
            ["cases_expected"] = 27,
            ["cases_present"] = Rows.Count,
            ["cases_passed"] = passed,
            ["complete"] = Rows.Count == 27 && passed == 27,
            ["assertions"] = _assertions,
            ["provider_transport"] = "synthetic in-process HTTP; local sides are real NTFS directories",
            ["assembly"] = AssemblyIdentity(),
            ["cases"] = Rows
        };
    }

    private static Task WriteReportAsync() =>
        File.WriteAllTextAsync(_reportPath, JsonSerializer.Serialize(BuildReport(), ReportJson));

    /// <summary>
    /// Writes the report after every case so a hard abort still leaves a truthful partial report, and never
    /// lets a reporting failure replace an already observed case result.
    /// </summary>
    private static void TryWriteReport()
    {
        try
        {
            File.WriteAllText(_reportPath, JsonSerializer.Serialize(BuildReport(), ReportJson));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Ordered endpoint mode matrix report write deferred: " +
                                    error.GetType().Name + ": " + error.Message);
        }
    }
}

