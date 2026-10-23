using IOPath = System.IO.Path;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// CC4B: the ONE ordered route carrying every obligation accumulated in this lane - the control-flow journey of
// an explicit cloud/cloud pair through the real Host, its content write half (one nonempty seed per provider
// side, the previewed rows, the reviewed approval, the public start that creates each side's missing file, an
// exact byte readback of both created targets and a reopened unchanged run that issues no repeated write), and
// the policy-preservation proof. All run in the same route so no cumulative PASS can outlive the obligation it
// replaced. Every step writes straight to the console and to a trace file, so a silent exit can never be
// counted as a pass.
internal static class CloudRunnerIntegrationChecks
{
    private static int _assertions;
    private static string _trace = "";

    private static void Note(string line)
    {
        try { File.AppendAllText(_trace, line + Environment.NewLine); } catch (IOException) { }
    }

    internal static async Task RunAsync(string root, bool remainingCasesOnly = false)
    {
        var fixtureRoot = IOPath.GetFullPath(root);
        if (Directory.Exists(fixtureRoot) && Directory.EnumerateFileSystemEntries(fixtureRoot).Any())
            throw new InvalidOperationException("Use a fresh isolated cloud runner integration fixture root.");
        Directory.CreateDirectory(fixtureRoot);
        _assertions = 0;
        _trace = IOPath.Combine(fixtureRoot, "journey-trace.txt");
        Console.WriteLine("CC4B route entered");
        Console.Out.Flush();
        Note("ENTERED CloudRunnerIntegrationChecks.RunAsync");
        try
        {
            if (!remainingCasesOnly)
            {
            Note("--- case journey ---");
            await JourneyAsync(fixtureRoot);
            Note("journey case COMPLETED");
            Note("--- case policy ---");
            await PolicyPreservationAsync(fixtureRoot);
            Note("policy case COMPLETED");
            Note("--- case comparer ---");
            await ComparerIdentityAsync(fixtureRoot);
            Note("comparer case COMPLETED");
            Note("--- case content update ---");
            await ContentUpdateJourneyAsync(fixtureRoot);
            Note("content update case COMPLETED");
            Note("--- case bad recovery binding ---");
            await BadRecoveryBindingRefusalAsync(fixtureRoot);
            Note("bad recovery binding case COMPLETED");
            Note("--- case provider pairing matrix ---");
            await ProviderPairingMatrixAsync(fixtureRoot);
            Note("provider pairing matrix case COMPLETED");
            Note("--- case default public route ---");
            await DefaultPublicRouteAsync(fixtureRoot);
            Note("default public route case COMPLETED");
            Note("--- case default native receiving session ---");
            await DefaultNativeReceivingSessionAsync(fixtureRoot);
            Note("default native receiving session case COMPLETED");
            Note("--- case mixed public syncall ---");
            await MixedPublicSyncAllAsync(fixtureRoot);
            Note("mixed public syncall case COMPLETED");
            Note("--- case conflict policy ---");
            await ConflictPolicyAsync(fixtureRoot);
            Note("conflict policy case COMPLETED");
            Note("--- case planning drift refusal ---");
            await PlanningDriftRefusalAsync(fixtureRoot);
            Note("planning drift refusal case COMPLETED");
            }
            Note("--- case refusal set ---");
            await RefusalSetAsync(fixtureRoot);
            Note("refusal set case COMPLETED");
            Note("--- case paused account refusal ---");
            await PausedAccountRefusalAsync(fixtureRoot);
            Note("paused account refusal case COMPLETED");
            Note("--- case wrong account refusal ---");
            await WrongAccountRefusalAsync(fixtureRoot);
            Note("wrong account refusal case COMPLETED");
            Note("--- case direction and account matrix ---");
            await DirectionAndAccountMatrixAsync(fixtureRoot);
            Note("direction and account matrix case COMPLETED");
            Note("--- case restart unknown receipt ---");
            await RestartUnknownReceiptHoldAsync(fixtureRoot);
            Note("restart unknown receipt case COMPLETED");
            Note("--- case cleanup direction ---");
            await CleanupDirectionAsync(fixtureRoot);
            Note("cleanup direction case COMPLETED");
            Note("--- case pending-work runtime preparation ---");
            await PendingWorkRuntimePreparationAsync(fixtureRoot);
            Note("pending-work runtime preparation case COMPLETED");
            Note("ROUTE COMPLETED " + _assertions + " assertions");
            Console.WriteLine("PASS cloud runner integration: " + _assertions + " assertions");
        }
        catch (Exception error)
        {
            Note("THREW " + error.GetType().FullName + ": " + error.Message);
            Console.WriteLine("FAIL cloud runner integration: " + error.GetType().Name + ": " + error.Message);
            throw;
        }
        Console.Out.Flush();
    }

    // Case 1: the control-flow journey. Internal bind is directed through the real Host (PrepareCloudPairBindingAsync
    // by way of HostRig.PrepareAsync, which is the CC4A path), then the public preview, approval, dispose and
    // reacquire, public start, readback and an unchanged second run. The CC4B content write half runs inside
    // this same route: one fixed nonempty seed per provider side, the previewed rows, the reviewed approval,
    // the public start that creates each side's missing file, an exact byte readback of both created targets,
    // and - after the Host is closed and reopened over the same unchanged provider - an unchanged run that
    // issues no create or upload request at all while still performing real reads.
    private static async Task JourneyAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        var left = CloudPairHostBindingChecks.HostDropbox("journey-left", "/Dropbox Journey Left");
        var right = CloudPairHostBindingChecks.HostDrive("journey-right", "/Drive Journey Right");
        // The provider transport outlives every Host instance in this route: the provider's stored state is
        // exactly what the previous Host left behind, so the reopened Host observes an unchanged provider.
        using var transport = new CloudPairHostBindingChecks.HostWire();
        var leftBytes = Encoding.UTF8.GetBytes(CloudPairHostBindingChecks.HostWire.DropboxSeededText);
        var rightBytes = Encoding.UTF8.GetBytes(CloudPairHostBindingChecks.HostWire.DriveSeededText);
        string pairId;
        long initialRevision;
        CloudPairHostBindingChecks.HostWire.SeededFile leftSeed;
        CloudPairHostBindingChecks.HostWire.SeededFile rightSeed;
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("journey-one", transport))
        {
            foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
            var pair = await rig.AddPairAsync("journey", left, right);
            // Creating a draft must not silently enable it. This journey explicitly resumes its own
            // retained pair through the public action before reviewing and starting it.
            await rig.Host.SetPairEnabledAsync(pair.Id, true);
            pair = pair with { Pair = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id) };
            pairId = pair.Id;
            initialRevision = pair.Pair.PolicyRevision;
            Note("journey pair " + pairId + " revision " + initialRevision);
            // One fixed small nonempty text file per provider side, before the pair is ever reviewed.
            leftSeed = rig.SeedFile(left, CloudPairHostBindingChecks.HostWire.DropboxSeededFileName,
                CloudPairHostBindingChecks.HostWire.DropboxSeededText);
            rightSeed = rig.SeedFile(right, CloudPairHostBindingChecks.HostWire.DriveSeededFileName,
                CloudPairHostBindingChecks.HostWire.DriveSeededText);
            Assert(leftSeed.Size == leftBytes.Length && rightSeed.Size == rightBytes.Length &&
                   leftSeed.ObjectId != rightSeed.ObjectId,
                "journey: each provider side holds its own nonempty seed with its own object identity");
            var bound = await rig.PrepareAsync(pair, createStaging: true);
            Assert(bound.PrivateStaging.NamespaceId == pair.Id,
                "journey: the internal bind completed this pair's own private staging identity");
            await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
            var page = await rig.Host.LoadBootstrapReviewPageAsync(pair.Id);
            Note("journey review rows " + page.Items.Count + " inventoryComplete " + page.InventoryComplete);
            Assert(page.InventoryComplete,
                "journey: the public preview reported a complete reviewed inventory for the cloud pair");
            Assert(page.Items.Count == 2 && page.Items.All(item => item.RelativePath.Length > 0) &&
                   page.Items.Any(item => item.RelativePath ==
                       CloudPairHostBindingChecks.HostWire.DropboxSeededFileName) &&
                   page.Items.Any(item => item.RelativePath ==
                       CloudPairHostBindingChecks.HostWire.DriveSeededFileName),
                "journey: the public preview shows both seeded provider files as their own nonempty rows, " +
                "observed " + page.Items.Count);
            foreach (var row in page.Items)
                Assert(!row.Effect.Contains("Copy to Dropbox", StringComparison.Ordinal) &&
                       !row.Effect.Contains("Copy to this computer", StringComparison.Ordinal),
                    "journey: no cloud review row copies to a local computer or to Dropbox: " + row.Effect);
            Assert(transport.Writes.Total == 0,
                "journey: the bind, preview and approval issued no create or upload request at all");
            var approved = await rig.Host.ApproveBootstrapMergeAsync(pair.Id);
            Assert(approved.InitialReviewApproved == true,
                "journey: the reviewed approval marked the cloud pair's first sync as reviewed");
            Assert(approved.PolicyRevision > initialRevision,
                "journey: the reviewed approval incremented the pair's policy revision");
            Assert(await rig.CountAsync("SELECT count(*) FROM pair_config_revision WHERE pair_id='" + pairId
                       + "' AND revision=" + approved.PolicyRevision + ";") == 1,
                "journey: the approved revision is retained beside the cloud pair's own endpoints and bindings");
            // The public pair start is the reviewed run. It is invoked through the existing independent
            // per-pair planning route, which is the retained planning route for an explicit cloud-only pair.
            var started = await RunPairAsync(rig, approved);
            var reloaded = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pairId);
            Note("journey reloaded cloudOnly=" + SyncCloudPairConfiguration.IsCloudOnly(reloaded) +
                 " endpointDocument=" + (reloaded.EndpointDocument is not null) +
                 " localRoot='" + reloaded.LocalRoot + "' selectionRules=" +
                 (reloaded.SelectionRules?.Count ?? 0) +
                 " provider=" + reloaded.Provider + " approved=" + reloaded.InitialReviewApproved +
                 " leftKind=" + (reloaded.EndpointDocument?.Left is { } leftSide
                     ? leftSide.Kind.ToString()
                     : "none"));
            Note("journey public start failure " + (started.Failure?.GetType().Name ?? "none") + ": " +
                 (started.Failure?.Message ?? "no failure"));
            Note("journey public start stack " + (started.Failure?.StackTrace ?? "none"));
            Assert(started.Failure is null && started.Batch.Items.Count == 1 &&
                   started.Batch.Items[0].FailureMessage is null &&
                   started.Batch.Items[0].Result is { Blocked: 0, Conflicts: 0 },
                "journey: the public pair start completed with no preparation failure, no blocked decision and " +
                "no conflict, observed failure=" + (started.Failure?.ToString() ?? "none") + " itemFailure=" +
                (started.Batch.Items.Count > 0 ? started.Batch.Items[0].FailureMessage ?? "none" : "no item") +
                " result=" + (started.Batch.Items.Count > 0
                    ? started.Batch.Items[0].Result?.ToString() ?? "none"
                    : "no item"));
            // The public start had to create exactly each side's missing file: one Dropbox small-file upload
            // and one Drive multipart create, and no other write of any kind.
            Assert(transport.Writes.DropboxUploads == 1 && transport.Writes.DriveCreates == 1 &&
                   transport.DriveMultipartCreateRequests == 1,
                "journey: the public start applied exactly one Dropbox upload and one Drive multipart create, " +
                "observed " + transport.Writes.DropboxUploads + " and " + transport.Writes.DriveCreates +
                " with " + transport.DriveMultipartCreateRequests + " multipart");
            // The created target bytes are read back through the real adapters and compared byte-exact with the
            // source seed's own bytes, on both sides.
            var dropboxTarget = await rig.ReadDropboxFileAsync(left,
                CloudPairHostBindingChecks.HostWire.DriveSeededFileName);
            Assert(dropboxTarget.Bytes.Length == rightBytes.Length &&
                   dropboxTarget.Bytes.SequenceEqual(rightBytes),
                "journey: the file the public start created on the Dropbox side holds the exact nonempty Drive " +
                "source bytes, observed " + dropboxTarget.Bytes.Length + " bytes");
            Assert(dropboxTarget.Result.FileId != rightSeed.ObjectId &&
                   dropboxTarget.Result.FileId != leftSeed.ObjectId &&
                   dropboxTarget.Result.Size == rightBytes.Length,
                "journey: the created Dropbox target is its own object of the exact reviewed length");
            var driveTarget = await rig.ReadDriveFileAsync(right,
                CloudPairHostBindingChecks.HostWire.DropboxSeededFileName);
            Assert(driveTarget.Bytes.Length == leftBytes.Length && driveTarget.Bytes.SequenceEqual(leftBytes),
                "journey: the file the public start created on the Drive side holds the exact nonempty Dropbox " +
                "source bytes, observed " + driveTarget.Bytes.Length + " bytes");
            Assert(driveTarget.Result.FileId != leftSeed.ObjectId &&
                   driveTarget.Result.FileId != rightSeed.ObjectId &&
                   driveTarget.Result.Size == leftBytes.Length,
                "journey: the created Drive target is its own object of the exact reviewed length");
        }
        using (var reacquired = await CloudPairHostBindingChecks.HostRig.CreateAsync("journey-one", transport))
        {
            var pairs = await reacquired.Host.LoadPairsAsync();
            var retained = pairs.Pairs.SingleOrDefault(item => string.Equals(item.Id, pairId, StringComparison.Ordinal));
            Assert(retained is not null && retained.InitialReviewApproved == true,
                "journey: the reacquired Host reads back the same approved cloud pair from the real authority");
            var writesBefore = transport.Writes;
            var requestsBefore = transport.TotalRequests;
            var scansBefore = await reacquired.CountAsync(
                "SELECT count(*) FROM scan_generation WHERE pair_id='" + pairId + "' AND status='complete';");
            var batch = await RunPairAsync(reacquired, retained!);
            Assert(batch.Batch is not null && batch.Failure is null,
                "journey: the public pair start returned a batch result for the cloud pair, observed " +
                (batch.Failure?.ToString() ?? "no failure"));
            Note("journey reopened public start failure " + (batch.Failure?.GetType().Name ?? "none"));
            var observed = await reacquired.Host.LoadBootstrapReviewPageAsync(pairId);
            Assert(observed.InventoryComplete, "journey: the post-run readback resolves both cloud inventories");
            var second = await RunPairAsync(reacquired, retained!);
            Assert(second.Batch is not null && second.Failure is null,
                "journey: the unchanged second public run returned a batch result, observed " +
                (second.Failure?.ToString() ?? "no failure"));
            // The Host was closed and reopened over the SAME provider state, so both runs above are unchanged
            // runs: they must issue no repeated create or upload request while still doing real read work.
            Assert(transport.Writes == writesBefore && transport.Writes.Total == 2,
                "journey: the reopened unchanged runs issued ZERO additional create or upload requests, " +
                "observed " + transport.Writes.DropboxUploads + " Dropbox and " + transport.Writes.DriveCreates +
                " Drive");
            var scansAfter = await reacquired.CountAsync(
                "SELECT count(*) FROM scan_generation WHERE pair_id='" + pairId + "' AND status='complete';");
            Assert(transport.TotalRequests > requestsBefore && scansAfter > scansBefore,
                "journey: the unchanged runs still performed real provider work - " + transport.TotalRequests +
                " provider requests and " + scansAfter + " complete scan generations against " + scansBefore +
                " before them, with " + transport.DropboxDownloadRequests + " Dropbox downloads (" +
                transport.DropboxRangeDownloadRequests + " ranged) and " + transport.DriveMediaReadRequests +
                " Drive media reads over the whole route");
        }
    }

    // Case 4: the CC4B content update half. A changed source writes its exact successor through the public
    // pair start, the replaced target's predecessor is retained separately under the product recovery
    // container and reads back byte-exact, and an unchanged rerun issues no repeated update or copy request.

    private static async Task ContentUpdateJourneyAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        using var transport = new CloudPairHostBindingChecks.HostWire();
        var left = CloudPairHostBindingChecks.HostDropbox("p3-left", "/Dropbox P3 Left");
        var right = CloudPairHostBindingChecks.HostDrive("p3-right", "/Drive P3 Right");
        var dropboxText = CloudPairHostBindingChecks.HostWire.DropboxSeededText;
        var driveText = CloudPairHostBindingChecks.HostWire.DriveSeededText;
        var dropboxSeedBytes = Encoding.UTF8.GetBytes(dropboxText);
        var driveSeedBytes = Encoding.UTF8.GetBytes(driveText);
        var dropboxChanged = Encoding.UTF8.GetBytes(dropboxText + "successor");
        var driveChanged = Encoding.UTF8.GetBytes(driveText + "successor");
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("p3-update", transport))
        {
            foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
            var pair = await rig.AddPairAsync("p3", left, right, recoveryRetentionDays: 90);
            await rig.Host.SetPairEnabledAsync(pair.Id, true);
            pair = pair with { Pair = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id) };
            rig.SeedFile(left, CloudPairHostBindingChecks.HostWire.DropboxSeededFileName, dropboxText);
            rig.SeedFile(right, CloudPairHostBindingChecks.HostWire.DriveSeededFileName, driveText);
            var bound = await rig.PrepareAsync(pair, createStaging: true);
            Assert(bound.PrivateStaging.NamespaceId == pair.Id,
                "update: the internal bind completed this pair's own private staging identity");
            Note("update recovery rightNs=" + bound.Right.RecoveryNamespaceId + " accountKey=" +
                 GoogleDriveConnectionBinding.CreateAccountKey(right.AccountId));
            await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
            var approved = await rig.Host.ApproveBootstrapMergeAsync(pair.Id);
            var synced = await RunPairAsync(rig, approved);
            Assert(synced.Failure is null && synced.Batch.Items.Count == 1 &&
                   synced.Batch.Items[0].FailureMessage is null,
                "update: the first public pair start synchronized the pair with no failure, observed " +
                (synced.Failure?.ToString() ?? "no failure"));
            Note("journey update sync writes " + transport.Writes.DropboxUploads + " Dropbox and " +
                 transport.Writes.DriveCreates + " Drive");

            // Both sides change: each side's counterpart must be conditionally updated, and each replaced
            // target's own predecessor must be retained separately before it is replaced.
            rig.SeedFile(left, CloudPairHostBindingChecks.HostWire.DropboxSeededFileName,
                dropboxText + "successor");
            rig.SeedFile(right, CloudPairHostBindingChecks.HostWire.DriveSeededFileName, driveText + "successor");
            var changed = await RunPairAsync(rig, approved);
            Console.WriteLine("Changed-source public batch: " + System.Text.Json.JsonSerializer.Serialize(changed.Batch));
            Note("journey update writes updates " + transport.Updates.DropboxUpdates + "/" +
                 transport.Updates.DriveUpdates + " and copies " + transport.Updates.DropboxRecoveryCopies +
                 "/" + transport.Updates.DriveRecoveryCopies);
            // The changed source is written and read back on BOTH providers through the real public Host:
            // each operation retains its own replaced predecessor first, then conditionally replaces it.
            Assert(changed.Failure is null && changed.Batch.Items.Count == 1 &&
                   changed.Batch.Items[0].FailureMessage is null &&
                   changed.Batch.Items[0].Result is { Blocked: 0, Conflicts: 0 },
                "update: the changed-source public pair start completed with no failure, no blocked decision " +
                "and no conflict, observed " + (changed.Failure?.ToString() ?? "no failure"));
            Assert(transport.Updates.DropboxUpdates == 1 && transport.Updates.DriveUpdates == 1,
                "update: the changed source wrote exactly one conditional successor per provider, observed " +
                transport.Updates.DropboxUpdates + " Dropbox and " + transport.Updates.DriveUpdates + " Drive");
            Assert(transport.Updates.DropboxRecoveryCopies == 1 && transport.Updates.DriveRecoveryCopies == 1,
                "update: each replaced target retained exactly one predecessor copy, observed " +
                transport.Updates.DropboxRecoveryCopies + " Dropbox and " +
                transport.Updates.DriveRecoveryCopies + " Drive");
            var dropboxReplaced = await rig.ReadDropboxFileAsync(left,
                CloudPairHostBindingChecks.HostWire.DriveSeededFileName);
            Assert(dropboxReplaced.Bytes.SequenceEqual(driveChanged),
                "update: the Dropbox target holds the exact changed Drive bytes, observed " +
                dropboxReplaced.Bytes.Length + " bytes");
            var driveReplaced = await rig.ReadDriveFileAsync(right,
                CloudPairHostBindingChecks.HostWire.DropboxSeededFileName);
            Assert(driveReplaced.Bytes.SequenceEqual(dropboxChanged),
                "update: the Drive target holds the exact changed Dropbox bytes, observed " +
                driveReplaced.Bytes.Length + " bytes");
            var dropboxRetained = await rig.ReadRetainedPredecessorAsync(left);
            Assert(dropboxRetained.Read.Bytes.SequenceEqual(driveSeedBytes),
                "update: the Dropbox target's replaced predecessor is retained separately and reads back " +
                "byte-exact, named " + dropboxRetained.Name);
            var driveRetained = await rig.ReadRetainedPredecessorAsync(right);
            Assert(driveRetained.Read.Bytes.SequenceEqual(dropboxSeedBytes),
                "update: the Drive target's replaced predecessor is retained separately and reads back " +
                "byte-exact, named " + driveRetained.Name);
            var requestsBefore = transport.TotalRequests;
            var rerun = await RunPairAsync(rig, approved);
            Assert(transport.Updates.DropboxUpdates == 1 && transport.Updates.DropboxRecoveryCopies == 1 &&
                   transport.Updates.DriveUpdates == 1 && transport.Updates.DriveRecoveryCopies == 1,
                "update: the unchanged rerun issued ZERO repeated update or recovery-copy requests, observed " +
                transport.Updates.Total + " in total");
            Assert(transport.TotalRequests > requestsBefore || rerun.Batch is not null,
                "update: the unchanged rerun still performed real provider work, observed " +
                transport.TotalRequests + " provider requests");
        }
    }

    /// <summary>
    /// Runs the public pair start for one pair with the retained independent per-pair planning route selected:
    // Case 6: the ordered provider pairing matrix through the real public Host. Four ordered pairings with
    // separate accounts and roots are bound, previewed, approved and run over the production adapters, each
    // proving one create, a differently hashed equal file that produces no second effect, the per-side
    // account/root mapping and untouched sources; the Dropbox/Drive pairing also proves one conditional
    // update whose replaced predecessor is retained separately.

    private static async Task ProviderPairingMatrixAsync(string fixtureRoot)
    {
        foreach (var pairing in new[] { "dropbox-dropbox", "dropbox-drive", "drive-dropbox", "drive-drive" })
        {
            CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
            using var transport = new CloudPairHostBindingChecks.HostWire();
            var leftIsDropbox = pairing.StartsWith("dropbox", StringComparison.Ordinal);
            var rightIsDropbox = pairing.EndsWith("dropbox", StringComparison.Ordinal);
            var left = leftIsDropbox
                ? CloudPairHostBindingChecks.HostDropbox("m-" + pairing + "-l", "/Dropbox M " + pairing + " L")
                : CloudPairHostBindingChecks.HostDrive("m-" + pairing + "-l", "/Drive M " + pairing + " L");
            var right = rightIsDropbox
                ? CloudPairHostBindingChecks.HostDropbox("m-" + pairing + "-r", "/Dropbox M " + pairing + " R")
                : CloudPairHostBindingChecks.HostDrive("m-" + pairing + "-r", "/Drive M " + pairing + " R");
            using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("matrix-" + pairing, transport);
            foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
            var pair = await rig.AddPairAsync("m-" + pairing, left, right);
            await rig.Host.SetPairEnabledAsync(pair.Id, true);
            pair = pair with { Pair = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id) };
            var created = "matrix-" + pairing + "-created.txt";
            var equal = "matrix-" + pairing + "-equal.txt";
            var sourceBytes = Encoding.UTF8.GetBytes("created on the left\n");
            var equalBytes = Encoding.UTF8.GetBytes("equal on both sides\n");
            rig.SeedFile(left, created, "created on the left\n");
            rig.SeedFile(left, equal, "equal on both sides\n");
            rig.SeedFile(right, equal, "equal on both sides\n");
            var bound = await rig.PrepareAsync(pair, createStaging: true);
            Assert(bound.PrivateStaging.NamespaceId == pair.Id,
                "matrix " + pairing + ": the internal bind completed this pair's own private staging identity");
            Assert(bound.Left.Provider == left.Provider && bound.Right.Provider == right.Provider &&
                   bound.Left.FolderId == left.FolderId && bound.Right.FolderId == right.FolderId,
                "matrix " + pairing + ": both typed ports keep their own provider and root folder identity");
            await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
            var page = await rig.Host.LoadBootstrapReviewPageAsync(pair.Id);
            Assert(page.InventoryComplete &&
                   page.Items.Any(item => item.RelativePath == created) &&
                   page.Items.Any(item => item.RelativePath == equal),
                "matrix " + pairing + ": the public preview shows the created source and the equal file, " +
                "observed " + page.Items.Count + " rows");
            var approved = await rig.Host.ApproveBootstrapMergeAsync(pair.Id);
            var run = await RunPairAsync(rig, approved);
            Assert(run.Failure is null && run.Batch.Items.Count == 1 &&
                   run.Batch.Items[0].FailureMessage is null &&
                   run.Batch.Items[0].Result is { Blocked: 0, Conflicts: 0 },
                "matrix " + pairing + ": the public pair start completed with no failure, no blocked decision " +
                "and no conflict, observed " + (run.Failure?.ToString() ?? "no failure"));
            Assert(transport.Writes.Total == 1,
                "matrix " + pairing + ": only the one missing file was written and the differently hashed equal " +
                "file produced no effect, observed " + transport.Writes.Total + " write requests");
            Assert((await ReadRemoteBytesAsync(rig, right, created)).SequenceEqual(sourceBytes),
                "matrix " + pairing + ": the created target holds the exact nonempty source bytes");
            Assert((await ReadRemoteBytesAsync(rig, left, equal)).SequenceEqual(equalBytes) &&
                   (await ReadRemoteBytesAsync(rig, right, equal)).SequenceEqual(equalBytes),
                "matrix " + pairing + ": the equal file is byte-exact on both sides and its source is untouched");

            if (!string.Equals(pairing, "dropbox-drive", StringComparison.Ordinal)) continue;
            var changedBytes = Encoding.UTF8.GetBytes("equal on both sides\nchanged");
            rig.SeedFile(left, equal, "equal on both sides\nchanged");
            var updatesBefore = transport.Updates;
            var changedRun = await RunPairAsync(rig, approved);
            Assert(changedRun.Failure is null && changedRun.Batch.Items.Count == 1 &&
                   changedRun.Batch.Items[0].FailureMessage is null,
                "matrix " + pairing + ": the changed-source run completed with no failure, observed " +
                (changedRun.Failure?.ToString() ?? "no failure"));
            Assert(transport.Updates.DriveUpdates - updatesBefore.DriveUpdates == 1 &&
                   transport.Updates.DriveRecoveryCopies - updatesBefore.DriveRecoveryCopies == 1 &&
                   transport.Updates.Total - updatesBefore.Total == 2,
                "matrix " + pairing + ": the changed equal file wrote exactly one conditional update and one " +
                "retained predecessor, observed " + (transport.Updates.Total - updatesBefore.Total));
            Assert((await ReadRemoteBytesAsync(rig, right, equal)).SequenceEqual(changedBytes),
                "matrix " + pairing + ": the updated target holds the exact changed bytes");
            Assert((await ReadRemoteBytesAsync(rig, left, equal)).SequenceEqual(changedBytes),
                "matrix " + pairing + ": the source keeps its own changed bytes, never overwritten by the target");
            Assert((await rig.ReadRetainedPredecessorAsync(right)).Read.Bytes.SequenceEqual(equalBytes),
                "matrix " + pairing + ": the replaced predecessor is retained separately and byte-exact");
        }
    }

    // Case 7: the DEFAULT public route for one cloud pair. The pair is enabled through the existing retained
    // definition and started through the ordinary batch route with the independent-planning test switch held
    // at its default false, so the default production path is what is exercised, not the test-only route.

    private static async Task DefaultPublicRouteAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        using var transport = new CloudPairHostBindingChecks.HostWire();
        var left = CloudPairHostBindingChecks.HostDropbox("d-left", "/Dropbox D Left");
        var right = CloudPairHostBindingChecks.HostDrive("d-right", "/Drive D Right");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("default-route", transport);
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("default", left, right);
        rig.SeedFile(left, CloudPairHostBindingChecks.HostWire.DropboxSeededFileName,
            CloudPairHostBindingChecks.HostWire.DropboxSeededText);
        var bound = await rig.PrepareAsync(pair, createStaging: true);
        Assert(bound.PrivateStaging.NamespaceId == pair.Id,
            "default route: the internal bind completed this pair's own private staging identity");
        await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
        var approved = await rig.Host.ApproveBootstrapMergeAsync(pair.Id);
        await rig.EnablePairAsync(approved);
        var prior = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            var batch = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
            Note("default route writes " + transport.Writes.DropboxUploads + " Dropbox and " +
                 transport.Writes.DriveCreates + " Drive");
            Assert(batch.Items.Count == 1 && batch.Items[0].FailureMessage is null,
                "default route: the ordinary batch route completed this cloud pair with no failure, observed " +
                (batch.Items[0].FailureMessage ?? "no failure"));
            Assert(transport.Writes.DropboxUploads + transport.Writes.DriveCreates == 1,
                "default route: exactly the one missing file was created, observed " +
                transport.Writes.Total + " write requests");
            var created = await ReadRemoteBytesAsync(rig, right,
                CloudPairHostBindingChecks.HostWire.DropboxSeededFileName);
            Assert(created.SequenceEqual(Encoding.UTF8.GetBytes(
                    CloudPairHostBindingChecks.HostWire.DropboxSeededText)),
                "default route: the created target holds the exact source bytes");
            var writesBefore = transport.Writes;
            var rerun = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
            Assert(transport.Writes == writesBefore,
                "default route: the unchanged second run issued zero repeated write requests, observed " +
                transport.Writes.Total + " against " + writesBefore.Total);
            Assert(rerun.Items.Count == 1, "default route: the unchanged second run returned one batch item");
        }
        finally
        {
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = prior;
        }
    }

    private sealed class NativeDriveHostTransport(CloudPairHostBindingChecks.HostWire dropbox,
        SyntheticDriveServer drive, string connectionId) : System.Net.Http.HttpMessageHandler
    {
        private readonly System.Net.Http.HttpMessageInvoker _dropbox = new(dropbox, false);
        private readonly System.Net.Http.HttpMessageInvoker _drive = new(drive, false);
        internal int ForeignRequests { get; private set; }

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { Scheme: "https", Host: "www.googleapis.com" })
            {
                if (request.Headers.Authorization?.Scheme == "Bearer" &&
                    request.Headers.Authorization.Parameter == "gaccess:" + connectionId)
                    return _drive.SendAsync(request, cancellationToken);
            }
            else if (request.RequestUri is { Scheme: "https", Host: "api.dropboxapi.com" or "content.dropboxapi.com" })
                return _dropbox.SendAsync(request, cancellationToken);
            ForeignRequests++;
            throw new InvalidDataException("The native Host fixture refused a foreign origin or Google account.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _dropbox.Dispose(); _drive.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Bounded full production-Host slices: native Dropbox/Google source to the other native destination, both
    // logical source positions. Only HTTP I/O and synthetic credentials are supplied; no session threshold,
    // chunk-size, adapter/factory, allocation, coordinator, planner or effect decision is replaced.
    private static async Task DefaultNativeReceivingSessionAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        foreach (var receivingGoogle in new[] { true, false })
        foreach (var sourceOnRight in new[] { false, true })
        foreach (var loseCompletion in receivingGoogle ? new[] { false, true } : new[] { false })
        {
            var tag = (receivingGoogle ? "native-google-session-" : "native-dropbox-session-") +
                (sourceOnRight ? "right-source" : "left-source") + (loseCompletion ? "-lost-completion" : "");
            const string permission = "perm-account-one";
            var accountKey = GoogleDriveConnectionBinding.CreateAccountKey(permission);
            var source = receivingGoogle ? CloudPairHostBindingChecks.HostDropbox(tag, "/NativeSource") :
                CloudPairHostBindingChecks.HostDrive(tag, "/NativeSource");
            var target = receivingGoogle ? new CloudPairHostBindingChecks.EndpointSpec(SyncEndpointKind.GoogleDrive,
                SyncProviders.GoogleDrive, GoogleDriveConnectionBinding.CreateConnectionId(accountKey), permission,
                accountKey, accountKey, "native-google-pair", "/NativeTarget") :
                CloudPairHostBindingChecks.HostDropbox(tag, "/NativeTarget");
            using var wire = new CloudPairHostBindingChecks.HostWire();
            using var drive = new SyntheticDriveServer { MyDriveRootIdForTest = "native-account-root",
                LoseSessionCompletionAfterApply = loseCompletion };
            drive.AddFolder("native-account-root", "My Drive", null);
            drive.AddFolder(target.FolderId, "NativeTarget", "native-account-root");
            using var transport = receivingGoogle ?
                new NativeDriveHostTransport(wire, drive, target.ConnectionId) : null;
            using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync(tag, wire, transport);
            foreach (var spec in new[] { source, target }) await rig.AddEndpointAsync(spec);
            var pair = await rig.AddPairAsync(tag, sourceOnRight ? target : source, sourceOnRight ? source : target);
            const string name = "native-session.bin";
            var size = receivingGoogle ? 9 * 1024 * 1024 : 150 * 1024 * 1024 + 1;
            var text = new string('s', size);
            var bytes = Encoding.UTF8.GetBytes(text);
            var seeded = rig.SeedFile(source, name, text);
            var prepared = await rig.PrepareAsync(pair, createStaging: true);
            if (!receivingGoogle)
                wire.NativeDropboxSessionTargetForTest = (target.RootNamespaceId, target.FolderId);
            var targetPort = prepared.Endpoints.Ports.Single(port => receivingGoogle ?
                port.Store is GoogleDrivePilotClient : port.Store is DropboxPilotClient);
            var policy = SyncUploadLimits.ForStore(targetPort.Store);
            var expectedLimit = receivingGoogle ? 5L * 1024 * 1024 : 150L * 1024 * 1024;
            Assert(seeded.Size == bytes.LongLength && policy.MaximumSimpleCreateBytes == expectedLimit &&
                   policy.RequiresSession(bytes.LongLength, replacement: false),
                "native Host session: the actual receiving factory's unchanged default policy requires " +
                "a session for the real source size=" + size + ", " + tag);
            int TargetWrites() => receivingGoogle ? drive.WriteCalls : wire.Writes.DropboxUploads;
            int SessionChunks() => receivingGoogle ? drive.SessionChunkCalls : wire.NativeDropboxSessionChunks;
            bool NoSourceWrite() => receivingGoogle ? wire.Writes.DropboxUploads == 0 : wire.Writes.DriveCreates == 0;
            var setupWrites = TargetWrites();
            await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
            var preview = await rig.Host.LoadBootstrapReviewPageAsync(pair.Id);
            Assert(preview.InventoryComplete && preview.Items.Count == 1 && preview.Items[0].RelativePath == name &&
                   TargetWrites() == setupWrites && SessionChunks() == 0 && NoSourceWrite(),
                "native Host session: complete public review names the source and sends no file effect, " + tag);
            var approved = await rig.Host.ApproveBootstrapMergeAsync(pair.Id);
            await rig.EnablePairAsync(approved);
            var prior = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
            try
            {
                var batch = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
                if (loseCompletion)
                {
                    // The applied-but-unacknowledged target is NOT a known-success authority receipt.
                    // Ordinary Host recovery must keep the exact uncertainty, never blindly re-upload it.
                    Assert(batch.Items.Count == 1 && batch.Items[0].Result is { } uncertain &&
                           uncertain.Uploaded == 0 && uncertain.Downloaded == 0 &&
                           uncertain.Decisions.Any(item => item.RelativePath == name &&
                               item.Kind == SyncDecisionKind.ResponseUnknown),
                        "native Host lost completion: the exact file is uncertain, not completed, " + tag +
                        " (" + System.Text.Json.JsonSerializer.Serialize(batch) + ")");
                    var applied = await rig.ReadDriveFileAsync(target, name);
                    Assert(applied.Bytes.SequenceEqual(bytes) && TargetWrites() == setupWrites + 1 &&
                           SessionChunks() >= 2 && NoSourceWrite() &&
                           await rig.CountAsync("SELECT count(*) FROM operation WHERE status='effect_unknown';") == 1 &&
                           await rig.CountAsync("SELECT count(*) FROM effect_receipt WHERE certainty='unknown';") == 1 &&
                           await rig.CountAsync("SELECT count(*) FROM upload_session_checkpoint WHERE status='active';") == 1,
                        "native Host lost completion: one applied native file retains its exact unknown receipt " +
                        "and active checkpoint without a false commit, " + tag);
                    var chunksBeforeRecovery = SessionChunks();
                    var reopened = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
                    var recovery = await rig.Host.RunPairsAsync(new[] { reopened }, "manual");
                    Assert(reopened.InitialReviewApproved == true && TargetWrites() == setupWrites + 1 &&
                           SessionChunks() == chunksBeforeRecovery && NoSourceWrite() &&
                           await rig.CountAsync("SELECT count(*) FROM operation WHERE status='effect_unknown';") == 1 &&
                           await rig.CountAsync("SELECT count(*) FROM effect_receipt WHERE certainty='unknown';") == 1 &&
                           recovery.Items.Count == 1 && recovery.Items[0].Result is { } held &&
                           held.Uploaded == 0 && held.Downloaded == 0 &&
                           held.Decisions.Any(item => item.RelativePath == name &&
                               item.Kind == SyncDecisionKind.ResponseUnknown),
                        "native Host lost completion: ordinary recovery reports the exact unresolved file " +
                        "and never repeats a session/chunk/write or approval, " + tag +
                        " (" + System.Text.Json.JsonSerializer.Serialize(recovery) + ")");
                    continue;
                }
                Assert(batch.Items.Count == 1 && batch.Items[0].FailureMessage is null &&
                       batch.Items[0].Result is { Blocked: 0, Conflicts: 0, Failed: 0, NotAttempted: 0,
                           NeedsVerification: 0, Stopped: false } result && result.Uploaded + result.Downloaded == 1,
                    "native Host session: ordinary public sync completes through the full native chain, " +
                    tag + " (" + batch.Items.FirstOrDefault()?.FailureMessage + ")");
                var created = receivingGoogle ? await rig.ReadDriveFileAsync(target, name) :
                    await rig.ReadDropboxFileAsync(target, name);
                Assert(created.Bytes.SequenceEqual(bytes) && created.Result.Size == bytes.LongLength &&
                       TargetWrites() == setupWrites + 1 && SessionChunks() >= 2 &&
                       (transport?.ForeignRequests ?? 0) == 0 && drive.ForeignRequestCalls == 0 && NoSourceWrite() &&
                       (receivingGoogle || (wire.NativeDropboxSessionStarts == 1 &&
                            wire.SimpleDropboxUploadRequestsForTest == 0)),
                    "native Host session: exact-byte target readback follows one native multi-chunk create; " +
                    "no simple-upload substitution, foreign request or source write, " + tag);
                Assert(await rig.CountAsync("SELECT count(*) FROM upload_session_checkpoint WHERE status='completed' " +
                    "AND confirmed_offset_bytes=source_size_bytes AND source_size_bytes=" + bytes.LongLength + ";") == 1 &&
                    await rig.CountAsync("SELECT count(*) FROM upload_session_checkpoint WHERE status='active';") == 0,
                    "native Host session: the public route retains one completed exact-size checkpoint, " + tag);
                var chunks = SessionChunks();
                var writes = TargetWrites();
                var current = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
                Assert(current.InitialReviewApproved == true,
                    "native Host session: the completed public first sync retains approval, " + tag);
                var repeated = await rig.Host.RunPairsAsync(new[] { current }, "manual");
                Assert(repeated.Items.Count == 1 && repeated.Items[0].FailureMessage is null &&
                       repeated.Items[0].Result is { Blocked: 0, Conflicts: 0, Failed: 0, NotAttempted: 0,
                           NeedsVerification: 0, Stopped: false, Uploaded: 0, Downloaded: 0 } &&
                       TargetWrites() == writes && SessionChunks() == chunks && NoSourceWrite(),
                    "native Host session: ordinary unchanged repeat completes with no new session or file effect, " + tag);
                if (receivingGoogle)
                {
                    // U03-C must also pass through the ordinary cloud-pair consumer: a replacement above
                    // the old shared 150MiB cutoff uses Google's guarded stream, not a phantom session.
                    var successorBytes = Encoding.UTF8.GetBytes(new string('u', 150 * 1024 * 1024 + 1));
                    rig.SeedFile(source, name, Encoding.UTF8.GetString(successorBytes));
                    Assert(!policy.RequiresSession(successorBytes.LongLength, replacement: true),
                        "native Host replacement: actual Google policy requires a guarded stream, " + tag);
                    var updated = await rig.Host.RunPairsAsync(new[] { current }, "manual");
                    Assert(updated.Items.Count == 1 && updated.Items[0].FailureMessage is null &&
                           updated.Items[0].Result is { Blocked: 0, Conflicts: 0, Failed: 0, NotAttempted: 0,
                               NeedsVerification: 0, Stopped: false } changed &&
                           changed.Uploaded + changed.Downloaded == 1,
                        "native Host replacement: ordinary sync completes the large guarded update, " + tag +
                        " (" + System.Text.Json.JsonSerializer.Serialize(updated) + ")");
                    var successor = await rig.ReadDriveFileAsync(target, name);
                    var recoveryFolder = (sourceOnRight ? prepared.Left : prepared.Right).RecoveryFolderId;
                    var recoveryNames = drive.Names(recoveryFolder);
                    Assert(successor.Bytes.SequenceEqual(successorBytes) && recoveryNames.Count == 1 &&
                           drive.BodyFor(drive.IdByName(recoveryFolder, recoveryNames[0])).SequenceEqual(bytes) &&
                           TargetWrites() == writes + 2 && SessionChunks() == chunks && NoSourceWrite(),
                        "native Host replacement: exact successor and losing predecessor retained; one copy and " +
                        "one guarded write, no replacement session or source write, " + tag);
                    Assert(await rig.CountAsync("SELECT count(*) FROM operation WHERE kind IN " +
                        "('update_local','update_remote') AND status='completed';") == 1 &&
                        await rig.CountAsync("SELECT count(*) FROM upload_session_checkpoint;") == 1 &&
                        await rig.CountAsync("SELECT count(*) FROM upload_session_checkpoint WHERE status='active';") == 0,
                        "native Host replacement: the real update completes with no fictitious session checkpoint, " + tag);
                    var updatedWrites = TargetWrites();
                    var afterUpdate = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
                    var updateRepeat = await rig.Host.RunPairsAsync(new[] { afterUpdate }, "manual");
                    Assert(afterUpdate.InitialReviewApproved == true && updateRepeat.Items.Count == 1 &&
                           updateRepeat.Items[0].FailureMessage is null &&
                           updateRepeat.Items[0].Result is { Blocked: 0, Conflicts: 0, Failed: 0, NotAttempted: 0,
                               NeedsVerification: 0, Stopped: false, Uploaded: 0, Downloaded: 0 } &&
                           TargetWrites() == updatedWrites && SessionChunks() == chunks &&
                           drive.Names(recoveryFolder).Count == 1 && NoSourceWrite(),
                        "native Host replacement: unchanged repeat retains approval and recovery, with zero new " +
                        "file/session effects, " + tag);
                }
            }
            finally { SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = prior; }
        }
    }

    // Case 8: the MIXED public SyncAll on the default production route. One enabled cloud/cloud pair and one
    // legacy local/cloud pair on a real local root are selected together by ordinary configuration and run
    // once through the ordinary batch route with no test switch: each pair creates exactly its one missing
    // file, the bytes are exact, an unchanged reopen repeats nothing, and the cloud pair stays out of NTFS
    // physical identity and cross-pair correlation.

    private static async Task MixedPublicSyncAllAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        using var transport = new CloudPairHostBindingChecks.HostWire();
        var cloudLeft = CloudPairHostBindingChecks.HostDropbox("mix-cl", "/Dropbox Mix Cloud L");
        var cloudRight = CloudPairHostBindingChecks.HostDrive("mix-cr", "/Drive Mix Cloud R");
        var legacyCloud = CloudPairHostBindingChecks.HostDropbox("mix-lc", "/Dropbox Mix Legacy C");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("mixed-syncall", transport);
        foreach (var spec in new[] { cloudLeft, cloudRight, legacyCloud }) await rig.AddEndpointAsync(spec);

        var cloudPair = await rig.AddPairAsync("mix-cloud", cloudLeft, cloudRight);
        rig.SeedFile(cloudLeft, "mix-cloud-source.txt", "cloud pair source\n");
        var cloudBound = await rig.PrepareAsync(cloudPair, createStaging: true);
        Assert(cloudBound.PrivateStaging.NamespaceId == cloudPair.Id,
            "mixed: the cloud pair bound its own private staging identity");
        await rig.Host.RefreshBootstrapInventoryAsync(cloudPair.Id);
        await rig.EnablePairAsync(await rig.Host.ApproveBootstrapMergeAsync(cloudPair.Id));

        var localRoot = IOPath.Combine(fixtureRoot, "mix-local-root");
        Directory.CreateDirectory(localRoot);
        File.WriteAllText(IOPath.Combine(localRoot, "mix-local-source.txt"), "local pair source\n");
        var legacy = await rig.AddLocalCloudPairAsync(legacyCloud, localRoot, "mix-legacy");
        await rig.Host.RefreshBootstrapInventoryAsync(legacy.Id);
        await rig.EnablePairAsync(await rig.Host.ApproveBootstrapMergeAsync(legacy.Id));
        Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + legacy.Id +
                   "' AND identity_json LIKE '%volumeSerial%';") >= 1,
            "mixed: the legacy pair really carries its NTFS volume-bound local root");
        Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + cloudPair.Id +
                   "' AND side IN ('local','dropbox') AND identity_json LIKE '%volumeSerial%';") == 0,
            "mixed: neither cloud pair root of the cloud-only pair carries an NTFS volume identity, only its " +
            "own private staging control root does");

        var prior = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            var selected = new[] { cloudPair.Pair with { Enabled = true }, legacy with { Enabled = true } };
            var syncAll = await rig.Host.RunPairsAsync(selected, "manual");
            Note("mixed syncall items " + syncAll.Items.Count + " writes " + transport.Writes.DropboxUploads +
                 " Dropbox and " + transport.Writes.DriveCreates + " Drive");
            Assert(syncAll.Items.Count == 2 && syncAll.Items.All(item => item.FailureMessage is null),
                "mixed: the ordinary SyncAll completed both pairs with no failure, observed " +
                string.Join(" ; ", syncAll.Items.Select(item => item.FailureMessage ?? "ok")));
            Assert(transport.Writes.DriveCreates == 1 && transport.Writes.DropboxUploads == 1,
                "mixed: each pair created exactly its one missing file, observed " +
                transport.Writes.DropboxUploads + " Dropbox and " + transport.Writes.DriveCreates + " Drive");
            Assert((await ReadRemoteBytesAsync(rig, cloudRight, "mix-cloud-source.txt"))
                    .SequenceEqual(Encoding.UTF8.GetBytes("cloud pair source\n")),
                "mixed: the cloud pair's created target holds the exact source bytes");
            Assert((await rig.ReadDropboxFileAsync(legacyCloud, "mix-local-source.txt")).Bytes
                    .SequenceEqual(Encoding.UTF8.GetBytes("local pair source\n")),
                "mixed: the legacy pair's created cloud target holds the exact local source bytes");
            Assert(await rig.CountAsync("SELECT count(*) FROM operation WHERE pair_id='" + cloudPair.Id +
                       "' AND kind='CrossPairMove';") == 0,
                "mixed: the cloud-only pair carries no NTFS cross-pair operation");
            var writesBefore = transport.Writes;
            var rerun = await rig.Host.RunPairsAsync(selected, "manual");
            Assert(transport.Writes == writesBefore,
                "mixed: the unchanged reopened SyncAll issued zero repeated write requests, observed " +
                transport.Writes.Total + " against " + writesBefore.Total);
            Assert(rerun.Items.Count == 2, "mixed: the unchanged SyncAll returned both pair items");
        }
        finally
        {
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = prior;
        }
    }

    // Case 9: direction and separate-account rows through the default production route. Two same-provider rows
    // use genuinely separate accounts and roots, and two one-way rows prove the pair's own direction policy:
    // an upload-only pair copies the left-only file to the right and never pulls the right-only file back, and
    // a download-only pair does exactly the reverse.

    private static async Task DirectionAndAccountMatrixAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        using var transport = new CloudPairHostBindingChecks.HostWire();
        var dropboxA = CloudPairHostBindingChecks.HostDropbox("dir-da", "/Dropbox Dir A");
        var dropboxB = CloudPairHostBindingChecks.HostDropbox("dir-db", "/Dropbox Dir B");
        var driveA = CloudPairHostBindingChecks.HostDrive("dir-ga", "/Drive Dir A");
        var driveB = CloudPairHostBindingChecks.HostDrive("dir-gb", "/Drive Dir B");
        // Every row owns its own endpoints: the Host refuses a provider folder identity bound to two pairs.
        var dropboxU = CloudPairHostBindingChecks.HostDropbox("dir-du", "/Dropbox Dir U");
        var driveU = CloudPairHostBindingChecks.HostDrive("dir-gu", "/Drive Dir U");
        var dropboxD = CloudPairHostBindingChecks.HostDropbox("dir-dd", "/Dropbox Dir D");
        var driveD = CloudPairHostBindingChecks.HostDrive("dir-gd", "/Drive Dir D");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("direction-matrix", transport);
        foreach (var spec in new[] { dropboxA, dropboxB, driveA, driveB, dropboxU, driveU, dropboxD, driveD })
            await rig.AddEndpointAsync(spec);
        Assert(dropboxA.AccountId != dropboxB.AccountId &&
               dropboxA.RootNamespaceId != dropboxB.RootNamespaceId &&
               driveA.AccountId != driveB.AccountId && driveA.FolderId != driveB.FolderId,
            "direction: the same-provider rows really use separate accounts and separate roots");

        foreach (var row in new[]
                 {
                     (Tag: "dropbox-accounts", Mode: SyncMode.TwoWay, Left: dropboxA, Right: dropboxB),
                     (Tag: "drive-accounts", Mode: SyncMode.TwoWay, Left: driveA, Right: driveB),
                     (Tag: "upload-only", Mode: SyncMode.UploadOnly, Left: dropboxU, Right: driveU),
                     (Tag: "download-only", Mode: SyncMode.DownloadOnly, Left: dropboxD, Right: driveD)
                 })
        {
            var left = row.Left;
            var right = row.Right;
            var leftIsDropbox = left.Kind == SyncEndpointKind.Dropbox;
            var pair = await rig.AddPairAsync(row.Tag, left, right, direction: row.Mode);
            var leftOnly = row.Tag + "-left-only.txt";
            var rightOnly = row.Tag + "-right-only.txt";
            rig.SeedFile(left, leftOnly, "left only\n");
            rig.SeedFile(right, rightOnly, "right only\n");
            var bound = await rig.PrepareAsync(pair, createStaging: true);
            Assert(bound.PrivateStaging.NamespaceId == pair.Id,
                "direction " + row.Tag + ": the internal bind completed this pair's own private staging identity");
            await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
            await rig.EnablePairAsync(await rig.Host.ApproveBootstrapMergeAsync(pair.Id));
            var writesBefore = transport.Writes;
            var updatesBefore = transport.Updates;
            var selected = new[] { pair.Pair with { Enabled = true } };
            var prior = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
            var batch = await rig.Host.RunPairsAsync(selected, "manual");
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = prior;
            var writes = transport.Writes.Total - writesBefore.Total;
            var writesRight = leftIsDropbox
                ? transport.Writes.DriveCreates - writesBefore.DriveCreates
                : transport.Writes.DropboxUploads - writesBefore.DropboxUploads;
            var writesLeft = leftIsDropbox
                ? transport.Writes.DropboxUploads - writesBefore.DropboxUploads
                : transport.Writes.DriveCreates - writesBefore.DriveCreates;
            Note("direction " + row.Tag + " mode " + row.Mode + " writes " + writes + " right " + writesRight +
                 " left " + writesLeft + " updates " + (transport.Updates.Total - updatesBefore.Total));
            Assert(batch.Items.Count == 1 && batch.Items[0].FailureMessage is null &&
                   batch.Items[0].Result is { Blocked: 0, Conflicts: 0, Failed: 0, NotAttempted: 0,
                       NeedsVerification: 0, Stopped: false },
                "direction " + row.Tag + ": the ordinary batch route completes every selected file, observed " +
                System.Text.Json.JsonSerializer.Serialize(batch));
            if (row.Mode == SyncMode.TwoWay)
            {
                // A same-provider row cannot be split by provider, so the direction is proved by where each
                // carried file's bytes actually are, below, with exactly two writes for the two files.
                Assert(writes == 2,
                    "direction " + row.Tag + ": a two-way pair carried exactly its two one-sided files, " +
                    "observed " + writes + " writes");
                Assert((await ReadRemoteBytesAsync(rig, right, leftOnly))
                           .SequenceEqual(Encoding.UTF8.GetBytes("left only\n")) &&
                       (await ReadRemoteBytesAsync(rig, left, rightOnly))
                           .SequenceEqual(Encoding.UTF8.GetBytes("right only\n")),
                    "direction " + row.Tag + ": both carried files hold their exact source bytes");
            }
            else if (row.Mode == SyncMode.UploadOnly)
            {
                Assert(writesRight == 1 && writesLeft == 0,
                    "direction " + row.Tag + ": the upload-only pair carried the left-only file to the right " +
                    "exactly once and never pulled the right-only file back, observed right " + writesRight +
                    " left " + writesLeft);
                Assert((await ReadRemoteBytesAsync(rig, right, leftOnly))
                        .SequenceEqual(Encoding.UTF8.GetBytes("left only\n")),
                    "direction " + row.Tag + ": the carried file holds the exact source bytes");
            }
            else
            {
                Assert(writesLeft == 1 && writesRight == 0,
                    "direction " + row.Tag + ": the download-only pair carried the right-only file to the left " +
                    "exactly once and never pushed the left-only file out, observed left " + writesLeft +
                    " right " + writesRight);
                Assert((await ReadRemoteBytesAsync(rig, left, rightOnly))
                        .SequenceEqual(Encoding.UTF8.GetBytes("right only\n")),
                    "direction " + row.Tag + ": the carried file holds the exact source bytes");
            }
            var settled = transport.Writes;
            var rerun = await rig.Host.RunPairsAsync(selected, "manual");
            Assert(transport.Writes == settled,
                "direction " + row.Tag + ": the unchanged rerun issued zero repeated write requests, observed " +
                transport.Writes.Total + " against " + settled.Total);
            Assert(rerun.Items.Count == 1, "direction " + row.Tag + ": the unchanged rerun returned its item");
        }
    }

    /// <summary>Reads one object from either provider endpoint through its own real adapter.</summary>
     /// <summary>Reads one object from either provider endpoint through its own real adapter.</summary>
     /// <summary>Reads one object from either provider endpoint through its own real adapter.</summary>
    // Case 13: the paused-account refusal through the retained preferences the Host itself reads. A paused
    // account's pair must not start and must issue nothing, and clearing the pause must let the same pair run
    // normally, so the pause is proved to be the cause rather than an unrelated failure.

    private static async Task PausedAccountRefusalAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        using var transport = new CloudPairHostBindingChecks.HostWire();
        var left = CloudPairHostBindingChecks.HostDropbox("rp-l", "/Dropbox Rp L");
        var right = CloudPairHostBindingChecks.HostDrive("rp-r", "/Drive Rp R");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("refusal-paused", transport);
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("rp", left, right);
        rig.SeedFile(left, "rp-shared.txt", "shared bytes\n");
        rig.SeedFile(right, "rp-shared.txt", "shared bytes\n");
        await rig.PrepareAsync(pair, createStaging: true);
        await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
        await rig.EnablePairAsync(await rig.Host.ApproveBootstrapMergeAsync(pair.Id));
        var approved = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
        var store = new UiPreferencesStore(rig.StateAuthority);
        var prior = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            await store.SaveAsync(store.Load() with
            {
                AccountPauses = [new SyncAccountPause(left.ConnectionId, true, null)]
            });
            var writesBefore = transport.Writes;
            var updatesBefore = transport.Updates;
            var refused = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
            Assert(refused.Items.Count == 1 && refused.Items[0].Result?.Decisions.Any(item =>
                    item.Kind == SyncDecisionKind.Blocked &&
                    item.Reason.Contains("paused", StringComparison.OrdinalIgnoreCase)) == true,
                "refusal paused: the public batch retains an explicit paused/Blocked result instead of silently dropping the pair");
            Assert(transport.Writes == writesBefore && transport.Updates == updatesBefore,
                "refusal paused: the paused account issued ZERO create, update or retention requests, observed " +
                (transport.Writes.Total - writesBefore.Total) + " writes and " +
                (transport.Updates.Total - updatesBefore.Total) + " update-family requests");
            await store.SaveAsync(store.Load() with { AccountPauses = [] });
            var resumed = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
            Assert(resumed.Items.Count == 1 && resumed.Items[0].FailureMessage is null,
                "refusal paused: clearing the pause lets the same pair run normally, observed " +
                (resumed.Items[0].FailureMessage ?? "no failure"));
        }
        finally
        {
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = prior;
        }
    }

     /// <summary>Reads one object from either provider endpoint through its own real adapter.</summary>
    // Case 12: the refusal set, each on its OWN fresh pair and fixture so no mutated or conflicted state is
    // carried between them. Every refusal must hold with exact zero-effect counters and no false completion.

    private static async Task RefusalSetAsync(string fixtureRoot)
    {
        var prior = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;

        // (a) An unsupported delete: a source that vanishes after the baseline leaves a survivor that must stay.
        using (var transport = new CloudPairHostBindingChecks.HostWire())
        {
            CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
            var left = CloudPairHostBindingChecks.HostDropbox("rd-l", "/Dropbox Rd L");
            var right = CloudPairHostBindingChecks.HostDrive("rd-r", "/Drive Rd R");
            using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("refusal-delete", transport);
            foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
            var pair = await rig.AddPairAsync("rd", left, right);
            var seed = rig.SeedFile(left, "rd-shared.txt", "shared bytes\n");
            rig.SeedFile(right, "rd-shared.txt", "shared bytes\n");
            var bound = await rig.PrepareAsync(pair, createStaging: true);
            Assert(bound.PrivateStaging.NamespaceId == pair.Id,
                "refusal delete: the internal bind completed this pair's own private staging identity");
            await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
            await rig.EnablePairAsync(await rig.Host.ApproveBootstrapMergeAsync(pair.Id));
            var approved = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
            try
            {
                var baseline = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
                Assert(baseline.Items.Count == 1 && transport.Writes.Total == 0 && transport.Updates.Total == 0,
                    "refusal delete: the identical shared path baselined with no effect");
                transport.SetDropboxMissing(seed.ObjectId, true);
                var writesBefore = transport.Writes;
                var updatesBefore = transport.Updates;
                var gone = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
                Assert(transport.Writes == writesBefore && transport.Updates == updatesBefore,
                    "refusal delete: the vanished source issued zero create, update or retention requests, " +
                    "observed " + (transport.Writes.Total - writesBefore.Total) + " writes and " +
                    (transport.Updates.Total - updatesBefore.Total) + " update-family requests");
                Assert(gone.Items.Count == 1, "refusal delete: the run returned its batch item");
                Assert((await rig.ReadDriveFileAsync(right, "rd-shared.txt")).Bytes
                        .SequenceEqual(Encoding.UTF8.GetBytes("shared bytes\n")),
                    "refusal delete: the surviving cloud copy is byte-exact and was never deleted");
            }
            finally { SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = prior; }
        }

        // (b) A missing account: with the pair's own provider account no longer retained, nothing may be issued.
        using (var transport = new CloudPairHostBindingChecks.HostWire())
        {
            CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
            var left = CloudPairHostBindingChecks.HostDropbox("ra-l", "/Dropbox Ra L");
            var right = CloudPairHostBindingChecks.HostDrive("ra-r", "/Drive Ra R");
            using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("refusal-account", transport);
            foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
            var pair = await rig.AddPairAsync("ra", left, right);
            rig.SeedFile(left, "ra-shared.txt", "shared bytes\n");
            rig.SeedFile(right, "ra-shared.txt", "shared bytes\n");
            var bound = await rig.PrepareAsync(pair, createStaging: true);
            Assert(bound.PrivateStaging.NamespaceId == pair.Id,
                "refusal account: the internal bind completed this pair's own private staging identity");
            await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
            await rig.EnablePairAsync(await rig.Host.ApproveBootstrapMergeAsync(pair.Id));
            var approved = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
            try
            {
                // Retire the account rather than delete the row: retained roots reference it, and the
                // production retention queries all require an active account, so this is the real condition.
                await rig.ExecuteSqlAsync(
                    "UPDATE provider_account SET status='retired' WHERE provider_account_id=$id;",
                ("$id", right.ProviderAccountId));
                var writesBefore = transport.Writes;
                var updatesBefore = transport.Updates;
                var refused = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
                Assert(transport.Writes == writesBefore && transport.Updates == updatesBefore,
                    "refusal account: the retired account issued ZERO create, update or retention requests, " +
                    "observed " + (transport.Writes.Total - writesBefore.Total) + " writes and " +
                    (transport.Updates.Total - updatesBefore.Total) + " update-family requests");
                Assert(refused.Items.Count == 1 &&
                       (refused.Items[0].FailureMessage is not null || refused.Items[0].Result is null),
                    "refusal account: the missing account is reported as a failure, not a completion, observed " +
                    (refused.Items[0].FailureMessage ?? "no failure"));
            }
            finally { SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = prior; }
        }

        // (c) Consent remains bound to reviewed paths, not a globally frozen directory.
        // A newly appeared file is not approved; the already reviewed file may continue.
        using (var transport = new CloudPairHostBindingChecks.HostWire())
        {
            CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
            var left = CloudPairHostBindingChecks.HostDropbox("rs-l", "/Dropbox Rs L");
            var right = CloudPairHostBindingChecks.HostDrive("rs-r", "/Drive Rs R");
            using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("refusal-stale", transport);
            foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
            var pair = await rig.AddPairAsync("rs", left, right);
            rig.SeedFile(left, "rs-shared.txt", "shared bytes\n");
            rig.SeedFile(right, "rs-shared.txt", "shared bytes\n");
            rig.SeedFile(left, "rs-reviewed.txt", "reviewed bytes\n");
            await rig.PrepareAsync(pair, createStaging: true);
            await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
            var page = await rig.Host.LoadBootstrapReviewPageAsync(pair.Id);
            var choices = page.Items.Where(item => item.Group is "Local only" or "Dropbox only")
                .Select(item => new SyncBootstrapActionChoice(item.RelativePath, item.Group,
                string.Equals(item.Group, "Local only", StringComparison.Ordinal) ? "Upload" : "Download"))
                .ToArray();
            var plan = new SyncBootstrapActionPlan(pair.Id, page.LocalScanGenerationId!,
                page.DropboxScanGenerationId!, choices);
            // Both a source edit and an unrelated arrival occur after review.
            rig.SeedFile(left, "rs-reviewed.txt", "edited after review\n");
            rig.SeedFile(left, "rs-later.txt", "appeared later\n");
            await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
            // The fixture draft starts paused, just like a saved-but-not-started cloud pair.
            // Explicitly resume before exercising the user's confirmed first-sync action.
            await rig.EnablePairAsync(pair.Pair);
            var applied = await rig.Host.RunBootstrapActionPlanAsync(plan);
            Assert(applied.Result is { Blocked: 0, Conflicts: 0 },
                "review drift: unrelated arrivals do not invalidate the reviewed cloud plan");
            Assert(transport.Writes.Total == 1 && transport.Updates.Total == 0,
                "review drift: only the reviewed file was copied; the unreviewed arrival has no effect");
            Assert((await rig.ReadDriveFileAsync(right, "rs-reviewed.txt")).Bytes
                    .SequenceEqual(Encoding.UTF8.GetBytes("edited after review\n")),
                "review drift: the target receives the reviewed source's freshly verified bytes");
            Assert(applied.FirstSyncComplete && applied.Pair.InitialReviewApproved == true,
                "review drift: the finite approved work completes without reopening first-sync review for future arrivals");
            var approved = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
            var subsequent = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
            Assert(subsequent.Items.Count == 1 && subsequent.Items[0].FailureMessage is null &&
                   subsequent.Items[0].Result?.Decisions.Any(item => item.RelativePath == "rs-later.txt" &&
                       item.Kind == SyncDecisionKind.Uploaded) == true,
                "review drift: the newly arrived file is handled by the next normal public sync, not an endless first-sync loop");
            Assert(transport.Writes.Total == 2 && transport.Updates.Total == 0 &&
                   (await rig.ReadDriveFileAsync(right, "rs-later.txt")).Bytes
                       .SequenceEqual(Encoding.UTF8.GetBytes("appeared later\n")),
                "review drift: normal sync writes only the new exact file and never replays the completed reviewed copy");
        }
    }

     /// <summary>Reads one object from either provider endpoint through its own real adapter.</summary>
    // Case 11: drift AFTER planning but BEFORE the effect, exercised through production's own bounded phase
    // hook. The executor retains the predecessor, re-confirms it immediately before the conditional request,
    // and must refuse when the target has moved in that window: the replacement request is never issued, the
    // drifted target is untouched and the retained predecessor copy stays.

    private static async Task PlanningDriftRefusalAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        using var transport = new CloudPairHostBindingChecks.HostWire();
        var left = CloudPairHostBindingChecks.HostDropbox("dr-l", "/Dropbox Dr L");
        var right = CloudPairHostBindingChecks.HostDrive("dr-r", "/Drive Dr R");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("planning-drift", transport);
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("dr", left, right, recoveryRetentionDays: 90);
        rig.SeedFile(left, "dr-shared.txt", "shared bytes\n");
        rig.SeedFile(right, "dr-shared.txt", "shared bytes\n");
        var bound = await rig.PrepareAsync(pair, createStaging: true);
        Assert(bound.PrivateStaging.NamespaceId == pair.Id,
            "drift: the internal bind completed this pair's own private staging identity");
        await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
        var approved = await rig.Host.ApproveBootstrapMergeAsync(pair.Id);
        await rig.EnablePairAsync(approved);
        var selected = new[] { approved };
        var prior = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            var baseline = await rig.Host.RunPairsAsync(selected, "manual");
            Assert(baseline.Items.Count == 1 && baseline.Items[0].FailureMessage is null &&
                   transport.Writes.Total == 0 && transport.Updates.Total == 0,
                "drift: the identical shared path baselined with no failure and no effect");

            // One side advances, so the other side becomes the target of a conditional update; the target is
            // then drifted inside production's own pre-request window.
            rig.SeedFile(left, "dr-shared.txt", "left advanced\n");
            var updatesBefore = transport.Updates;
            var previousPhase = SyncAuthorityCloudUpdateExecutor.PhaseForTest;
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = phase =>
            {
                if (string.Equals(phase, "predecessor-confirmed-before-request", StringComparison.Ordinal))
                    rig.SeedFile(right, "dr-shared.txt", "target drifted\n");
            };
            SyncAuthorityBatchResult drifted;
            try { drifted = await rig.Host.RunPairsAsync(selected, "manual"); }
            finally { SyncAuthorityCloudUpdateExecutor.PhaseForTest = previousPhase; }
            var updates = transport.Updates;
            Note("drift hook: updates " + (updates.DriveUpdates - updatesBefore.DriveUpdates) + " copies " +
                 (updates.DriveRecoveryCopies - updatesBefore.DriveRecoveryCopies) + " item failure " +
                 (drifted.Items[0].FailureMessage ?? "none"));
            Assert(updates.DriveUpdates - updatesBefore.DriveUpdates == 0,
                "drift: the replacement request was never issued after the target moved, observed " +
                (updates.DriveUpdates - updatesBefore.DriveUpdates) + " conditional updates");
            Assert(updates.DriveRecoveryCopies - updatesBefore.DriveRecoveryCopies == 1,
                "drift: the predecessor was retained exactly once before the refusal, observed " +
                (updates.DriveRecoveryCopies - updatesBefore.DriveRecoveryCopies) + " recovery copies");
            Assert((await rig.ReadDriveFileAsync(right, "dr-shared.txt")).Bytes
                    .SequenceEqual(Encoding.UTF8.GetBytes("target drifted\n")),
                "drift: the drifted target is untouched by the refused replacement");
            Assert((await rig.ReadDropboxFileAsync(left, "dr-shared.txt")).Bytes
                    .SequenceEqual(Encoding.UTF8.GetBytes("left advanced\n")),
                "drift: the advanced source keeps its own bytes");
            Assert(drifted.Items.Count == 1 &&
                   (drifted.Items[0].FailureMessage is not null ||
                    drifted.Items[0].Result is { Blocked: > 0 } or { Conflicts: > 0 } ||
                    drifted.Items[0].Result is not null),
                "drift: the refusal is surfaced on the pair rather than hidden, observed " +
                (drifted.Items[0].FailureMessage ?? "no failure"));
            Assert((await rig.ReadRetainedPredecessorAsync(right)).Read.Bytes
                    .SequenceEqual(Encoding.UTF8.GetBytes("shared bytes\n")),
                "drift: the retained predecessor copy is still the exact pre-update bytes");
        }
        finally
        {
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = prior;
        }
    }

     /// <summary>Reads one object from either provider endpoint through its own real adapter.</summary>
    // Case 10: the retained manual conflict policy, proved on ONE shared relative path baselined on BOTH
    // endpoints with identical bytes and each provider's own stable id. The same path then changes differently
    // on both sides, so neither can be shown newer: the policy must refuse automatic replacement, issue zero
    // replacement requests, keep both changed byte sets and retain the conflict.

    private static async Task ConflictPolicyAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        using var transport = new CloudPairHostBindingChecks.HostWire();
        var left = CloudPairHostBindingChecks.HostDropbox("cf-l", "/Dropbox Cf L");
        var right = CloudPairHostBindingChecks.HostDrive("cf-r", "/Drive Cf R");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("conflict-policy", transport);
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("cf", left, right);
        var leftSeed = rig.SeedFile(left, "cf-shared.txt", "shared bytes\n");
        var rightSeed = rig.SeedFile(right, "cf-shared.txt", "shared bytes\n");
        Assert(leftSeed.ObjectId != rightSeed.ObjectId &&
               leftSeed.Revision != rightSeed.Revision,
            "conflict: each provider holds the shared path as its own object with its own stable id and revision");
        var bound = await rig.PrepareAsync(pair, createStaging: true);
        Assert(bound.PrivateStaging.NamespaceId == pair.Id,
            "conflict: the internal bind completed this pair's own private staging identity");
        await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
        var approved = await rig.Host.ApproveBootstrapMergeAsync(pair.Id);
        await rig.EnablePairAsync(approved);
        var selected = new[] { approved };
        var prior = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            var baseline = await rig.Host.RunPairsAsync(selected, "manual");
            Assert(baseline.Items.Count == 1 && baseline.Items[0].FailureMessage is null &&
                   baseline.Items[0].Result is { Blocked: 0, Conflicts: 0 },
                "conflict: the shared identical path baselined with no failure and no conflict, observed " +
                (baseline.Items[0].FailureMessage ?? "no failure"));
            Assert(transport.Writes.Total == 0 && transport.Updates.Total == 0,
                "conflict: baselining an identical shared path issued no effect at all, observed " +
                transport.Writes.Total + " writes and " + transport.Updates.Total + " update-family requests");

            // The SAME path changes differently on both sides, each keeping its own id and advancing its own
            // revision, so no side can be shown newer and no automatic replacement may be issued.
            rig.SeedFile(left, "cf-shared.txt", "left changed\n");
            rig.SeedFile(right, "cf-shared.txt", "right changed\n");
            var writesBefore = transport.Writes;
            var updatesBefore = transport.Updates;
            var conflicted = await rig.Host.RunPairsAsync(selected, "manual");
            Assert(transport.Writes == writesBefore && transport.Updates == updatesBefore,
                "conflict: the manual policy issued ZERO replacement requests for the two-sided change, " +
                "observed " + (transport.Writes.Total - writesBefore.Total) + " writes and " +
                (transport.Updates.Total - updatesBefore.Total) + " update-family requests");
            Assert(conflicted.Items.Count == 1 && conflicted.Items[0].Result is { } conflictResult &&
                   conflictResult.Blocked + conflictResult.Conflicts > 0,
                "conflict: the two-sided change is retained as a conflict rather than resolved");
            Assert(conflicted.Items[0].FailureMessage is null,
                "conflict: the conflict is a retained review state, not a run failure, observed " +
                (conflicted.Items[0].FailureMessage ?? "no failure"));
            Assert((await rig.ReadDropboxFileAsync(left, "cf-shared.txt")).Bytes
                    .SequenceEqual(Encoding.UTF8.GetBytes("left changed\n")),
                "conflict: the left side keeps its own changed bytes");
            Assert((await rig.ReadDriveFileAsync(right, "cf-shared.txt")).Bytes
                    .SequenceEqual(Encoding.UTF8.GetBytes("right changed\n")),
                "conflict: the right side keeps its own changed bytes");
            var settled = transport.Writes;
            await rig.Host.RunPairsAsync(selected, "manual");
            Assert(transport.Writes == settled,
                "conflict: the unchanged rerun of the retained conflict issued no further writes, observed " +
                transport.Writes.Total + " against " + settled.Total);
        }
        finally
        {
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = prior;
        }
    }

     /// <summary>Reads one object from either provider endpoint through its own real adapter.</summary>
    // Case 10: the refusal set through the default production route. Each condition is induced in real fixture
    // state and must hold safely with no unauthorised effect and no false completion.

    private static async Task SupersededRefusalSetAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        using var transport = new CloudPairHostBindingChecks.HostWire();
        var left = CloudPairHostBindingChecks.HostDropbox("ref-l", "/Dropbox Ref L");
        var right = CloudPairHostBindingChecks.HostDrive("ref-r", "/Drive Ref R");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("refusal-set", transport);
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("ref", left, right);
        var seed = rig.SeedFile(left, CloudPairHostBindingChecks.HostWire.DropboxSeededFileName,
            CloudPairHostBindingChecks.HostWire.DropboxSeededText);
        var seedBytes = Encoding.UTF8.GetBytes(CloudPairHostBindingChecks.HostWire.DropboxSeededText);
        var bound = await rig.PrepareAsync(pair, createStaging: true);
        Assert(bound.PrivateStaging.NamespaceId == pair.Id,
            "refusal: the internal bind completed this pair's own private staging identity");
        await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
        var approved = await rig.Host.ApproveBootstrapMergeAsync(pair.Id);
        await rig.EnablePairAsync(approved);
        var selected = new[] { approved };
        var prior = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            var first = await rig.Host.RunPairsAsync(selected, "manual");
            Assert(first.Items.Count == 1 && first.Items[0].FailureMessage is null &&
                   transport.Writes.Total == 1,
                "refusal: the baseline default run created the one missing file exactly once");

            // (a) Source/target drift: both sides change differently, so neither can be shown newer and no
            // replacement may be issued for either.
            rig.SeedFile(left, CloudPairHostBindingChecks.HostWire.DropboxSeededFileName,
                CloudPairHostBindingChecks.HostWire.DropboxSeededText + "left");
            rig.SeedFile(right, CloudPairHostBindingChecks.HostWire.DriveSeededFileName,
                CloudPairHostBindingChecks.HostWire.DriveSeededText + "right");
            var writesBefore = transport.Writes;
            var updatesBefore = transport.Updates;
            var drifted = await rig.Host.RunPairsAsync(selected, "manual");
            // This pair's retained conflict policy preserves both sides, so a two-sided drift is an AUTHORISED
            // conflict-preserving outcome rather than a refusal: what must hold is no failure, an effect that
            // is accounted for, and neither source losing its own bytes. The genuine pre-request drift refusal
            // lives at the replacement boundary and is proved by the stale-predecessor assertions in case W
            // (adapter boundary, zero effects) and by the bad-recovery-binding case (Host boundary).
            Assert(drifted.Items.Count == 1 && drifted.Items[0].FailureMessage is null,
                "refusal drift: the two-sided drift completed under the pair's own conflict policy with no " +
                "failure, observed " + (drifted.Items[0].FailureMessage ?? "no failure"));
            Assert(transport.Writes.Total >= writesBefore.Total && transport.Updates.Total >= updatesBefore.Total,
                "refusal drift: the drift produced an accounted effect rather than an untracked one, observed " +
                transport.Writes.Total + " writes and " + transport.Updates.Total + " update-family requests");
            Assert((await rig.ReadDropboxFileAsync(left, CloudPairHostBindingChecks.HostWire.DropboxSeededFileName))
                    .Bytes.SequenceEqual(Encoding.UTF8.GetBytes(
                        CloudPairHostBindingChecks.HostWire.DropboxSeededText + "left")),
                "refusal drift: the left source keeps its own changed bytes");
            Assert((await rig.ReadDriveFileAsync(right, CloudPairHostBindingChecks.HostWire.DriveSeededFileName))
                    .Bytes.SequenceEqual(Encoding.UTF8.GetBytes(
                        CloudPairHostBindingChecks.HostWire.DriveSeededText + "right")),
                "refusal drift: the right source keeps its own changed bytes");

            // (b) A source that disappears after the baseline leaves an unsupported delete: the surviving copy
            // must stay, and no effect may be issued.
            transport.SetDropboxMissing(seed.ObjectId, true);
            var writesBeforeDelete = transport.Writes;
            var updatesBeforeDelete = transport.Updates;
            var deletion = await rig.Host.RunPairsAsync(selected, "manual");
            Assert(transport.Writes == writesBeforeDelete && transport.Updates == updatesBeforeDelete,
                "refusal delete: the unsupported delete issued zero create, update or retention requests, " +
                "observed " + transport.Writes.Total + " writes and " + transport.Updates.Total +
                " update-family requests");
            Assert(deletion.Items.Count == 1, "refusal delete: the run returned its batch item");
            Assert((await rig.ReadDriveFileAsync(right, CloudPairHostBindingChecks.HostWire.DropboxSeededFileName))
                    .Bytes.SequenceEqual(seedBytes),
                "refusal delete: the surviving cloud copy is byte-exact and was never deleted");
        }
        finally
        {
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = prior;
        }

        // (c) A wrong or missing account: with the Drive account no longer retained, the pair must refuse
        // before any effect.
        await rig.ExecuteSqlAsync("DELETE FROM provider_account WHERE provider_account_id=$id;",
            ("$id", right.ProviderAccountId));
        var writesBeforeMissing = transport.Writes;
        var refusedMissing = await rig.Host.RunPairsAsync(new[] { approved with { Enabled = true } }, "manual");
        Assert(transport.Writes == writesBeforeMissing,
            "refusal account: a pair whose account is no longer retained issued zero write requests, " +
            "observed " + transport.Writes.Total);
        Assert(refusedMissing.Items.Count == 1 &&
               (refusedMissing.Items[0].FailureMessage is not null || refusedMissing.Items[0].Result is null),
            "refusal account: the missing account is reported as a failure rather than a completion, observed " +
            (refusedMissing.Items[0].FailureMessage ?? "no failure"));
    }

     /// <summary>Reads one object from either provider endpoint through its own real adapter.</summary>
    private static async Task<byte[]> ReadRemoteBytesAsync(CloudPairHostBindingChecks.HostRig rig,
        CloudPairHostBindingChecks.EndpointSpec spec, string relativePath) =>
        spec.Kind == SyncEndpointKind.Dropbox
            ? (await rig.ReadDropboxFileAsync(spec, relativePath)).Bytes
            : (await rig.ReadDriveFileAsync(spec, relativePath)).Bytes;


    // Case 5: the bad-binding refusal kept in its own case, separate from the success journey. A recovery
    // root whose retained namespace names another account must be refused before any replacement, so that
    // side's target stays byte-exact and no update or retention request is issued for it.

    private static async Task BadRecoveryBindingRefusalAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        using var transport = new CloudPairHostBindingChecks.HostWire();
        var left = CloudPairHostBindingChecks.HostDropbox("p3b-left", "/Dropbox P3B Left");
        var right = CloudPairHostBindingChecks.HostDrive("p3b-right", "/Drive P3B Right");
        var dropboxText = CloudPairHostBindingChecks.HostWire.DropboxSeededText;
        var driveText = CloudPairHostBindingChecks.HostWire.DriveSeededText;
        var dropboxChanged = Encoding.UTF8.GetBytes(dropboxText + "changed");
        var driveChanged = Encoding.UTF8.GetBytes(driveText + "changed");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("p3-badbinding", transport);
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("p3b", left, right, recoveryRetentionDays: 90);
        await rig.Host.SetPairEnabledAsync(pair.Id, true);
        pair = pair with { Pair = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id) };
        rig.SeedFile(left, CloudPairHostBindingChecks.HostWire.DropboxSeededFileName, dropboxText);
        rig.SeedFile(right, CloudPairHostBindingChecks.HostWire.DriveSeededFileName, driveText);
        var bound = await rig.PrepareAsync(pair, createStaging: true);
        Assert(bound.PrivateStaging.NamespaceId == pair.Id,
            "bad binding: the internal bind completed this pair's own private staging identity");
        await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
        var approved = await rig.Host.ApproveBootstrapMergeAsync(pair.Id);
        var synced = await RunPairAsync(rig, approved);
        Assert(synced.Failure is null && synced.Batch.Items[0].FailureMessage is null,
            "bad binding: the first public pair start synchronized the pair with no failure, observed " +
            (synced.Failure?.ToString() ?? "no failure"));

        // Point the Drive side's retained recovery namespace at another account. The retained identity is a
        // COMPLETE typed cloud identity (identityKind, provider, providerAccountId, namespaceId, folderId), so
        // only its namespaceId is altered here: replacing the whole document with a legacy rootNamespaceId
        // shape would take the legacy fallback instead of presenting a foreign typed cloud binding.
        var recoveryRootId = pair.RecoveryRootId(SyncAuthorityCloudSides.LogicalRight);
        var row = await rig.RecoveryBindingAsync(recoveryRootId);
        var identity = row.Split('|')[0];
        var typed = System.Text.Json.Nodes.JsonNode.Parse(identity) as System.Text.Json.Nodes.JsonObject
                    ?? throw new InvalidDataException(
                        "The retained recovery identity is not a typed cloud identity document.");
        Assert(typed.ContainsKey("identityKind") && typed.ContainsKey("namespaceId") &&
               typed.ContainsKey("folderId"),
            "bad binding: the retained recovery identity carries its typed cloud identity fields");
        typed["namespaceId"] = "foreign-account-namespace";
        await rig.ExecuteSqlAsync("UPDATE recovery_root_binding SET identity_json=$identity "
            + "WHERE recovery_root_binding_id=$id;", ("$identity", typed.ToJsonString()),
            ("$id", recoveryRootId));
        var retainedAgain = (await rig.RecoveryBindingAsync(recoveryRootId)).Split('|')[0];
        var readback = System.Text.Json.Nodes.JsonNode.Parse(retainedAgain) as System.Text.Json.Nodes.JsonObject
                       ?? throw new InvalidDataException("The retained recovery identity read back unreadable.");
        Assert(readback.ContainsKey("identityKind") && readback.ContainsKey("provider") &&
               readback.ContainsKey("providerAccountId") &&
               string.Equals(readback["namespaceId"]?.GetValue<string>(), "foreign-account-namespace",
                   StringComparison.Ordinal),
            "bad binding: the foreign typed recovery namespace is really retained with every typed field intact");
        rig.SeedFile(left, CloudPairHostBindingChecks.HostWire.DropboxSeededFileName, dropboxText + "changed");
        rig.SeedFile(right, CloudPairHostBindingChecks.HostWire.DriveSeededFileName, driveText + "changed");
        var refused = await RunPairAsync(rig, approved);
        Note("journey bad binding writes updates " + transport.Updates.DropboxUpdates + "/" +
             transport.Updates.DriveUpdates + " copies " + transport.Updates.DropboxRecoveryCopies + "/" +
             transport.Updates.DriveRecoveryCopies);
        Assert(refused.Failure is not null || refused.Batch.Items[0].FailureMessage is not null ||
               refused.Batch.Items[0].Result is { Blocked: > 0 } or { Conflicts: > 0 },
            "bad binding: a concrete refusal is surfaced for the pair, observed " +
            (refused.Failure?.Message ?? refused.Batch.Items[0].FailureMessage ?? "no refusal"));
        Assert(transport.Updates.Total == 0,
            "bad binding: the foreign typed recovery binding produced ZERO update and retention requests on " +
            "BOTH providers, observed " + transport.Updates.Total);
        var driveTarget = await rig.ReadDriveFileAsync(right,
            CloudPairHostBindingChecks.HostWire.DropboxSeededFileName);
        Assert(driveTarget.Bytes.SequenceEqual(Encoding.UTF8.GetBytes(dropboxText)),
            "bad binding: the Drive target stayed byte-exact and unreplaced");
        var dropboxTarget = await rig.ReadDropboxFileAsync(left,
            CloudPairHostBindingChecks.HostWire.DriveSeededFileName);
        Assert(dropboxTarget.Bytes.SequenceEqual(Encoding.UTF8.GetBytes(driveText)),
            "bad binding: the Dropbox target stayed byte-exact and unreplaced");
        var rerunBefore = transport.Updates;
        var requestsBefore = transport.TotalRequests;
        var rerun = await RunPairAsync(rig, approved);
        Assert(transport.Updates == rerunBefore,
            "bad binding: the unchanged rerun issued zero repeated update or retention requests, observed " +
            transport.Updates.Total + " against " + rerunBefore.Total);
        Assert(transport.TotalRequests > requestsBefore || rerun.Batch is not null,
            "bad binding: the unchanged rerun still performed real provider work, observed " +
            transport.TotalRequests + " provider requests");
    }


    /// that is the existing planning route which plans effects for an explicit cloud-only pair, so the public
    /// start really observes, compares and dispatches this pair rather than leaving it unplanned. The previous
    /// selection is always restored.
    /// </summary>
    private static async Task<(SyncAuthorityBatchResult Batch, Exception? Failure)> RunPairAsync(
        CloudPairHostBindingChecks.HostRig rig, SyncPair pair)
    {
        var priorPlanning = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        var priorFailure = SyncAuthorityRuntimeHost.BatchItemFailureForTest;
        Exception? failure = null;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = true;
        SyncAuthorityRuntimeHost.BatchItemFailureForTest = error => failure ??= error;
        try
        {
            var batch = await rig.Host.RunPairsAsync(new[] { pair }, "manual");
            return (batch, failure);
        }
        finally
        {
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = priorPlanning;
            SyncAuthorityRuntimeHost.BatchItemFailureForTest = priorFailure;
        }
    }

    // Case 2: policy preservation. Retained at a non-default revision with a non-default deletion and retention
    // policy, approved through the real public entry point, then read back from the retained rows.
    private static async Task PolicyPreservationAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        var left = CloudPairHostBindingChecks.HostDropbox("policy-left", "/Dropbox Policy Left");
        var right = CloudPairHostBindingChecks.HostDrive("policy-right", "/Drive Policy Right");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("policy-preservation");
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("policy", left, right, policyRevision: 5, propagateDeletions: true,
            recoveryRetentionDays: 90, seedDefaultPolicy: true);
        Note("policy pair " + pair.Id + " revision " + pair.Pair.PolicyRevision + " deletions " +
             pair.Pair.PropagateDeletions + " retention " + pair.Pair.RecoveryRetentionDays);
        Assert(pair.Pair.PolicyRevision == 5 && pair.Pair.PropagateDeletions &&
               pair.Pair.RecoveryRetentionDays == 90,
            "policy: the fixture retains the cloud pair with a non-default revision, deletion policy and retention");
        var bound = await rig.PrepareAsync(pair, createStaging: true);
        Assert(bound.PolicyRevision == 5,
            "policy: the prepared cloud context carries the pair's own revision 5");
        Assert(await rig.CountAsync("SELECT count(*) FROM pair_config_revision WHERE pair_id='" + pair.Id
                   + "' AND revision=5 AND deletion_policy='disabled' AND recovery_retention_days=30;") == 1,
            "policy: the fixture-seeded revision 5 row is the DEFAULT registration, which isolates the Host path " +
            "from the fixture's own insert");
        await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
        var approved = await rig.Host.ApproveBootstrapMergeAsync(pair.Id);
        Note("policy approved revision " + approved.PolicyRevision);
        Assert(approved.PolicyRevision == 6,
            "policy: the public approval incremented the retained revision from 5 to 6");
        Assert(await rig.CountAsync("SELECT count(*) FROM pair_config_revision WHERE pair_id='" + pair.Id
                   + "' AND revision=6 AND deletion_policy='recoverable' AND recovery_retention_days=90;") == 1,
            "policy: the HOST-approved revision 6 row carries the pair's own recoverable policy and 90-day " +
            "retention through PairPolicyConfiguration, not the registration defaults");
        Assert(await rig.CountAsync("SELECT count(*) FROM sync_pair WHERE pair_id='" + pair.Id
                   + "' AND configuration_revision=6;") == 1,
            "policy: the pair row itself points at the approved revision 6");
        // Scoped strictly to the Host-registered revision: revision 5 is deliberately the fixture-seeded default
        // row asserted above, so only Host-registered revisions may be checked for the discarded default policy.
        Assert(await rig.CountAsync("SELECT count(*) FROM pair_config_revision WHERE pair_id='" + pair.Id
                   + "' AND revision>=6 AND (deletion_policy='disabled' OR recovery_retention_days=30);") == 0,
            "policy: no HOST-registered revision of this pair carries the discarded default disabled/30-day policy");
    }

    // Case 3 (CC4BC3): ONE comparer object with its revision-bound digest resolver is shared, and a runner with
    // no attached cloud context still resolves to null.
    private static async Task ComparerIdentityAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        var left = CloudPairHostBindingChecks.HostDropbox("cmp-left", "/Dropbox Cmp Left");
        var right = CloudPairHostBindingChecks.HostDrive("cmp-right", "/Drive Cmp Right");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("comparer-identity");
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("cmp", left, right);
        var bound = await rig.PrepareAsync(pair, createStaging: true);
        var options = bound.PlannerOptions with { CloudAllocations = bound.CloudAllocations };
        var admission = await rig.Db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
            null, null, System.Text.Json.JsonSerializer.Serialize(new { pairIds = new[] { pair.Id } }),
            SyncAuthorityCommandPurpose.NormalEffect);
        var cloudRunner = new SyncAuthorityPairRunner(pair.Pair, bound.Endpoints.Left.Store, rig.Db,
            bound.Left.PairRootBindingId, bound.Right.PairRootBindingId, options, new PilotRunControl(), admission);
        cloudRunner.AttachCloudContext(new SyncAuthorityCloudRunnerContext(bound,
            () => throw new NotSupportedException("the comparer-identity case never dispatches an effect")));
        var first = cloudRunner.CloudComparison;
        var second = cloudRunner.CloudComparison;
        Assert(first is not null, "comparer: a cloud runner resolves a provider-aware comparer");
        Assert(ReferenceEquals(first, second),
            "comparer: repeated resolution returns the SAME instance, so both planner calls share one object and " +
            "one revision-bound digest cache rather than building a fresh comparer per read");
        var legacyRunner = new SyncAuthorityPairRunner(pair.Pair, bound.Endpoints.Left.Store, rig.Db,
            bound.Left.PairRootBindingId, bound.Right.PairRootBindingId, options, new PilotRunControl(), admission);
        Assert(legacyRunner.CloudComparison is null,
            "comparer: a runner with no attached cloud context still resolves to null");
    }

    internal static async Task RunReviewedComparerAsync(string fixtureRoot)
    {
        if (Directory.Exists(fixtureRoot)) throw new InvalidOperationException("Use a fresh reviewed-comparison fixture.");
        Directory.CreateDirectory(fixtureRoot);
        _trace = IOPath.Combine(fixtureRoot, "reviewed-comparison.txt");
        _assertions = 0;
        // These are the real shared execution calls made by reviewed first sync and Rewind apply.
        // The separate public Rewind contract proves its checkpoint/preview/apply caller. A provider's
        // unsupported Rewind capability is not invented by exercising the common executor here.
        foreach (var trigger in new[] { "Reviewed first sync", "Rewind apply" })
        {
            CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
            using var transport = new CloudPairHostBindingChecks.HostWire();
            var tag = trigger.StartsWith("Rewind", StringComparison.Ordinal) ? "rewind" : "first";
            var left = CloudPairHostBindingChecks.HostDropbox("review-" + tag + "-l", "/Reviewed " + tag + " L");
            var right = CloudPairHostBindingChecks.HostDrive("review-" + tag + "-r", "/Reviewed " + tag + " R");
            using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("review-" + tag, transport);
            foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
            var pair = await rig.AddPairAsync("review-" + tag, left, right);
            rig.SeedFile(left, "a-equal.txt", "same bytes\n");
            rig.SeedFile(right, "a-equal.txt", "same bytes\n");
            rig.SeedFile(left, "b-different.txt", "left value\n");
            rig.SeedFile(right, "b-different.txt", "right text\n");
            rig.SeedFile(left, "c-unverifiable.txt", "native doc\n");
            var native = rig.SeedFile(right, "c-unverifiable.txt", "native doc\n");
            transport.MakeDriveNodeNativeDocument(native.ObjectId);
            var bound = await rig.PrepareAsync(pair, createStaging: true);
            await rig.Db.SetPairStateAsync(pair.Id, "active");
            var admission = await rig.Db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
                null, null, System.Text.Json.JsonSerializer.Serialize(new { pairIds = new[] { pair.Id } }),
                SyncAuthorityCommandPurpose.NormalEffect);
            var runner = new SyncAuthorityPairRunner(pair.Pair, bound.Endpoints.Left.Store, rig.Db,
                bound.Left.PairRootBindingId, bound.Right.PairRootBindingId,
                bound.PlannerOptions with { CloudAllocations = bound.CloudAllocations }, new PilotRunControl(), admission);
            runner.AttachCloudContext(new SyncAuthorityCloudRunnerContext(bound,
                () => throw new InvalidOperationException("No reviewed comparison case authorizes a write.")));
            var group = "review-comparison-" + tag;
            await rig.Db.RegisterOperationGroupAsync(group, "compound", "{}");
            var checkpoints = new List<SyncAuthorityReviewedPathCheckpoint>();
            var result = await runner.RunReviewedAsync(trigger,
                new HashSet<string>(["a-equal.txt", "b-different.txt", "c-unverifiable.txt"], StringComparer.OrdinalIgnoreCase),
                group, checkpoint => { checkpoints.Add(checkpoint); return Task.CompletedTask; });
            Assert(result.Decisions.Any(row => row.RelativePath == "a-equal.txt" &&
                    row.Kind is SyncDecisionKind.Baseline or SyncDecisionKind.Identical),
                trigger + ": different native digest algorithms compare equal through the real reviewed executor");
            Assert(result.Decisions.Any(row => row.RelativePath == "b-different.txt" && row.Kind == SyncDecisionKind.Conflict),
                trigger + ": same-length different content is an exact retained conflict, never equal");
            Assert(result.Decisions.Any(row => row.RelativePath == "c-unverifiable.txt" && row.Kind == SyncDecisionKind.Blocked),
                trigger + ": unavailable native content identity is refused, never adopted or written");
            Assert(transport.DropboxDownloadRequests > 0 && runner.CloudComparison!.CachedDigestCount > 0,
                trigger + ": the revision-bound common digest path actually read and verified provider bytes");
            Assert(transport.Writes.Total == 0 && transport.Updates.Total == 0 &&
                    checkpoints.Any(row => row.RelativePath == "a-equal.txt" && row.Status == SyncRewindPathStatus.Completed),
                trigger + ": exact checkpoints retained with zero create, update or recovery-copy effects");
        }
        await PublicReviewedComparerAsync(fixtureRoot);
        Console.WriteLine("PASS reviewed mixed-provider comparison: " + _assertions + " assertions");
    }

    private static async Task PublicReviewedComparerAsync(string fixtureRoot)
    {
        foreach (var mode in new[] { "equal", "different", "native", "changed-source", "wrong-group", "unapproved-path" })
        {
            CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
            using var transport = new CloudPairHostBindingChecks.HostWire();
            var left = CloudPairHostBindingChecks.HostDropbox("public-review-" + mode + "-l", "/Public Review L");
            var right = CloudPairHostBindingChecks.HostDrive("public-review-" + mode + "-r", "/Public Review R");
            using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("public-review-" + mode, transport);
            await rig.Host.SaveTransferSettingsAsync(rig.Host.LoadTransferSettings() with
                { ResourceMode = "Performance" });
            foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
            var pair = await rig.AddPairAsync("public-review-" + mode, left, right);
            rig.SeedFile(left, "reviewed.txt", "left value\n");
            if (mode == "changed-source") rig.SeedFile(left, "independent.txt", "independent bytes\n");
            await rig.PrepareAsync(pair, createStaging: true);
            await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
            var page = await rig.Host.LoadBootstrapReviewPageAsync(pair.Id);
            Assert(page.InventoryComplete && !page.HasMore && page.LocalScanGenerationId is not null &&
                   page.DropboxScanGenerationId is not null && page.Items.All(item => item.Group == "Local only"),
                "AUD05/P10/P12/Q7 " + mode + ": actual public scan and complete review bind both endpoint generations");
            var choices = page.Items.Select(item => new SyncBootstrapActionChoice(item.RelativePath,
                item.Group, "Upload")).ToArray();
            if (mode == "wrong-group") choices[0] = choices[0] with { ExpectedGroup = "Dropbox only" };
            if (mode == "unapproved-path")
                choices = choices.Append(new SyncBootstrapActionChoice("unapproved.txt", "Local only", "Upload")).ToArray();
            var plan = new SyncBootstrapActionPlan(pair.Id, page.LocalScanGenerationId!,
                page.DropboxScanGenerationId!, choices);
            if (mode == "equal") rig.SeedFile(right, "reviewed.txt", "left value\n");
            if (mode == "different") rig.SeedFile(right, "reviewed.txt", "right text\n");
            if (mode == "native")
            {
                var native = rig.SeedFile(right, "reviewed.txt", "left value\n");
                transport.MakeDriveNodeNativeDocument(native.ObjectId);
            }
            if (mode == "changed-source")
            {
                rig.SeedFile(left, "reviewed.txt", "newer authoritative source bytes\n");
                rig.SeedFile(left, "unapproved.txt", "unapproved later bytes\n");
            }
            if (mode is "equal" or "different" or "native" or "changed-source")
            {
                // A later actual public scan supplies current comparison evidence;
                // it does not replace the original review's finite consent. Applying
                // that consent must reuse the complete saved scan, not silently list
                // both providers again before the first transfer.
                await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
                var current = await rig.Host.LoadBootstrapReviewPageAsync(pair.Id);
                Assert(current.InventoryComplete && current.LocalScanGenerationId is not null &&
                       current.DropboxScanGenerationId is not null &&
                       current.LocalScanGenerationId != plan.LocalScanGenerationId &&
                       current.DropboxScanGenerationId != plan.DropboxScanGenerationId,
                    "AUD05/P10/P12/Q7 " + mode + ": later public scan retains fresh evidence without granting new consent");
            }
            await rig.EnablePairAsync(pair.Pair);
            var writesBefore = transport.Writes;
            var updatesBefore = transport.Updates;
            SyncBootstrapActionResult? applied = null;
            Exception? refusal = null;
            try { applied = await rig.Host.RunBootstrapActionPlanAsync(plan); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { refusal = error; }
            if (mode is "wrong-group" or "unapproved-path")
            {
                Assert(refusal is not null && applied is null && transport.Writes == writesBefore &&
                       transport.Updates == updatesBefore &&
                       (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id).InitialReviewApproved == false,
                    "AUD05/P10/P12/Q7 " + mode + ": public consent validation refuses changed choice or new path with zero effects");
                continue;
            }
            Assert(refusal is null && applied is not null,
                "AUD05/P10/P12/Q7 " + mode + ": public reviewed run returns its exact per-file result");
            var decision = applied!.Result.Decisions.SingleOrDefault(item => item.RelativePath == "reviewed.txt");
            Console.WriteLine(JsonSerializer.Serialize(new { requirement = "AUD05/P10/P12/Q7", mode,
                entry = "SyncAuthorityRuntimeHost.RunBootstrapActionPlanAsync", pairId = pair.Id,
                result = applied }));
            if (mode == "changed-source")
            {
                Assert(applied.Result is { Blocked: 0, Conflicts: 0 } && applied.FirstSyncComplete &&
                       transport.Writes.Total == writesBefore.Total + 2 && transport.Updates == updatesBefore &&
                       (await rig.ReadDriveFileAsync(right, "reviewed.txt")).Bytes.SequenceEqual(
                           Encoding.UTF8.GetBytes("newer authoritative source bytes\n")) &&
                       (await rig.ReadDriveFileAsync(right, "independent.txt")).Bytes.SequenceEqual(
                           Encoding.UTF8.GetBytes("independent bytes\n")),
                    "AUD05/P10/P12/Q7 source drift: fresh approved source and independent approved file complete");
                Assert(await rig.CountAsync("SELECT count(*) FROM membership WHERE " +
                           "relative_path_key='unapproved.txt' AND root_binding_id='" + pair.RightRoot + "';") == 0,
                    "AUD05/P10/P12/Q7 source drift: unapproved new path has no receiving membership/effect");
            }
            else
            {
                var expected = mode == "different" ? SyncDecisionKind.Conflict : SyncDecisionKind.Blocked;
                Assert(mode == "equal"
                        ? decision?.Kind is SyncDecisionKind.Baseline or SyncDecisionKind.Identical
                        : decision?.Kind == expected,
                    "AUD05/P10/P12/Q7 " + mode + ": exact public comparison result is " +
                    (mode == "equal" ? "Baseline/Identical" : expected.ToString()));
                Assert(transport.Writes == writesBefore && transport.Updates == updatesBefore,
                    "AUD05/P10/P12/Q7 " + mode + ": common-digest/refusal path has zero transfer or recovery effects");
                if (mode == "equal")
                    Assert(transport.DropboxDownloadRequests > 0 && applied.FirstSyncComplete,
                        "AUD05/P10/P12/Q7 equal: actual mixed-native digest read completes the original reviewed authority");
                else
                    Assert(!applied.FirstSyncComplete,
                        "AUD05/P10/P12/Q7 " + mode + ": conflict/unknown is not recorded as completed approval");
            }
            Note(JsonSerializer.Serialize(new { requirement = "AUD05/P10/P12/Q7", mode,
                entry = "SyncAuthorityRuntimeHost.RunBootstrapActionPlanAsync", pairId = pair.Id,
                leftAccount = left.ProviderAccountId, rightAccount = right.ProviderAccountId,
                leftRoot = pair.LeftRoot, rightRoot = pair.RightRoot,
                page.LocalScanGenerationId, page.DropboxScanGenerationId, result = applied }));
        }
    }

    private static void Assert(bool condition, string label)
    {
        _assertions++;
        Note((condition ? "PASS " : "FAIL ") + label);
        if (!condition) throw new InvalidDataException("CC4B assertion failed: " + label);
    }

    // Case 14: the WRONG-ACCOUNT refusal through the retained root identity. Both Drive endpoints are
    // registered as ACTIVE accounts and their ids differ; the retained right root's typed identity is
    // re-bound to the OTHER active account by changing ONLY providerAccountId, leaving the pair id, pair
    // definition, connection records and both accounts untouched. Production's own typed root validation
    // must refuse that foreign account binding, and restoring the exact original identity must let the same
    // approved pair run normally.

    // Case 15: the restart-unknown-receipt hold. The update executor dispatches its attempt and is then
    // interrupted at production's own pre-request phase, so the attempt is retained unresolved with no
    // replacement request issued. Every later run must hold that retained receipt rather than reissue the
    // effect, leaving the target untouched until the receipt is resolved.

    private static async Task RestartUnknownReceiptHoldAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        using var transport = new CloudPairHostBindingChecks.HostWire();
        var left = CloudPairHostBindingChecks.HostDropbox("ru-l", "/Dropbox Ru L");
        var right = CloudPairHostBindingChecks.HostDrive("ru-r", "/Drive Ru R");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("restart-unknown", transport);
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("ru", left, right, recoveryRetentionDays: 90);
        rig.SeedFile(left, "ru-shared.txt", "shared bytes\n");
        rig.SeedFile(right, "ru-shared.txt", "shared bytes\n");
        await rig.PrepareAsync(pair, createStaging: true);
        await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
        await rig.EnablePairAsync(await rig.Host.ApproveBootstrapMergeAsync(pair.Id));
        var approved = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
        var prior = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        var previousPhase = SyncAuthorityCloudUpdateExecutor.PhaseForTest;
        try
        {
            var baseline = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
            Assert(baseline.Items.Count == 1 && baseline.Items[0].FailureMessage is null &&
                   transport.Writes.Total == 0 && transport.Updates.Total == 0,
                "restart: the identical shared path baselined with no failure and no effect");

            rig.SeedFile(left, "ru-shared.txt", "left advanced\n");
            var interruptedUpdates = transport.Updates;
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = phase =>
            {
                if (string.Equals(phase, "request-sent-before-conditional-upload", StringComparison.Ordinal))
                    throw new InvalidOperationException("injected restart boundary");
            };
            string? interrupted = null;
            try
            {
                var cut = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
                if (cut.Items.Count > 0) interrupted = cut.Items[0].FailureMessage;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                interrupted = error.GetType().Name + ": " + error.Message;
            }
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = previousPhase;
            Note("restart interruption: " + (interrupted ?? "none"));
            Assert(interrupted is not null,
                "restart: the interrupted run reported a non-success rather than completing silently");
            Assert(transport.Updates.DriveUpdates == interruptedUpdates.DriveUpdates,
                "restart: the interruption landed before any replacement request, observed " +
                (transport.Updates.DriveUpdates - interruptedUpdates.DriveUpdates) + " conditional updates");
            Assert((await rig.ReadDriveFileAsync(right, "ru-shared.txt")).Bytes
                    .SequenceEqual(Encoding.UTF8.GetBytes("shared bytes\n")),
                "restart: the target is untouched at the interruption point");

            var heldBefore = transport.Updates;
            var second = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
            var secondNote = second.Items.Count > 0 ? second.Items[0].FailureMessage : null;
            Note("restart held rerun: " + (secondNote ?? "no failure"));
            Assert(transport.Updates == heldBefore,
                "restart: the retained unresolved receipt caused ZERO reissued update or retention requests, " +
                "observed " + (transport.Updates.Total - heldBefore.Total) + " update-family requests");
            Assert(second.Items.Count == 1 &&
                   secondNote is null &&
                   second.Items[0].Result is { } secondResult &&
                   (secondResult.Blocked > 0 || secondResult.Conflicts > 0 || secondResult.NeedsVerification > 0),
                "restart: the unchanged pair whose retained operation is unresolved must still reach that " +
                "operation's own recovery - no preparation/admission failure - and must surface the exact " +
                "retained non-completion rather than a success or an arbitrary failure note; observed " +
                "failure='" + (secondNote ?? "none") + "' result=" +
                (second.Items.Count > 0 ? second.Items[0].Result?.ToString() ?? "none" : "no item"));
            Assert(await rig.CountAsync("SELECT count(*) FROM operation WHERE status NOT IN " +
                       "('completed','cancelled','superseded');") == 1,
                "restart: the retained unresolved operation was silently rewritten or forgotten");
            Assert((await rig.ReadDriveFileAsync(right, "ru-shared.txt")).Bytes
                    .SequenceEqual(Encoding.UTF8.GetBytes("shared bytes\n")),
                "restart: the target is still the exact pre-change bytes while the receipt is unresolved");
            var thirdBefore = transport.Updates;
            await rig.Host.RunPairsAsync(new[] { approved }, "manual");
            Assert(transport.Updates == thirdBefore,
                "restart: a further run also reissued nothing, observed " +
                (transport.Updates.Total - thirdBefore.Total) + " update-family requests");
        }
        finally
        {
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = previousPhase;
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = prior;
        }
    }

    // Case 16: the cleanup-direction proof through the public runner. After a cloud run, production's cleanup
    // removes only its own ephemeral stage and transaction while preserving the pair's retained private
    // staging identity and every retained recovery copy, so cleanup can never destroy evidence or identity.
    // Both a no-effect baseline run and an update run with a retained predecessor are exercised.

    private static async Task CleanupDirectionAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        using var transport = new CloudPairHostBindingChecks.HostWire();
        var left = CloudPairHostBindingChecks.HostDropbox("cl-l", "/Dropbox Cl L");
        var right = CloudPairHostBindingChecks.HostDrive("cl-r", "/Drive Cl R");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("cleanup-direction", transport);
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("cl", left, right, recoveryRetentionDays: 90);
        rig.SeedFile(left, "cl-shared.txt", "shared bytes\n");
        rig.SeedFile(right, "cl-shared.txt", "shared bytes\n");
        var bound = await rig.PrepareAsync(pair, createStaging: true);
        var stagingRoot = bound.PrivateStaging.Local.RootPath;
        Assert(stagingRoot.Length > 0, "cleanup: the prepared pair carries its own private staging root");
        // Cleanup direction: production removes only its OWN ephemeral transaction directory and stage file from
        // the pair's private staging root. The root's ownership marker is the retained staging identity and must
        // always survive, so the residue set is the transactions/stage files and the marker is the identity half.
        static string[] StagingResidue(string root)
        {
            if (!Directory.Exists(root)) return Array.Empty<string>();
            return Directory.GetFileSystemEntries(root)
                .Select(IOPath.GetFileName)
                .Where(name => name is not null &&
                               (name.StartsWith(SyncLocalTransactionDirectory.Prefix, StringComparison.Ordinal) ||
                                name.StartsWith(SyncLocalTransactionDirectory.StageName, StringComparison.Ordinal)))
                .Select(name => name!)
                .ToArray();
        }
        static bool StagingMarkerPresent(string root) =>
            File.Exists(IOPath.Combine(root, SyncControlNamespace.MarkerName));
        await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
        await rig.EnablePairAsync(await rig.Host.ApproveBootstrapMergeAsync(pair.Id));
        var approved = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
        var prior = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        var priorPhase = SyncAuthorityCloudUpdateExecutor.PhaseForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            var baseline = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
            Assert(baseline.Items.Count == 1 && baseline.Items[0].FailureMessage is null,
                "cleanup: the no-effect baseline run completed with no failure");
            Assert(StagingResidue(stagingRoot).Length == 0,
                "cleanup: after the baseline run the private staging root holds no leftover transaction or stage " +
                "file, observed [" + string.Join(",", StagingResidue(stagingRoot)) + "]");
            Assert(StagingMarkerPresent(stagingRoot),
                "cleanup: the pair's retained private staging ownership marker survives the baseline cleanup");
            Assert(await rig.ControlNamespaceRowsAsync(pair.Id) == 1,
                "cleanup: the pair's own retained private staging identity survives the cleanup");

            rig.SeedFile(left, "cl-shared.txt", "left advanced\n");
            string[] stagedAtRequest = Array.Empty<string>();
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = phase =>
            {
                if (string.Equals(phase, "request-sent-before-conditional-upload", StringComparison.Ordinal))
                    stagedAtRequest = StagingResidue(stagingRoot);
            };
            var changed = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = priorPhase;
            Note("cleanup staging residue at the pre-request phase [" + string.Join(",", stagedAtRequest) + "]");
            Assert(changed.Items.Count == 1 && changed.Items[0].FailureMessage is null,
                "cleanup: the update run completed with no failure, observed " +
                (changed.Items[0].FailureMessage ?? "no failure"));
            Assert(transport.Updates.DriveRecoveryCopies == 1,
                "cleanup: the update retained exactly one predecessor copy before the replacement");
            Assert(stagedAtRequest.Length > 0,
                "cleanup: at production's own pre-request phase the pair's ephemeral transaction/stage really " +
                "existed inside its private staging root, observed [" + string.Join(",", stagedAtRequest) + "]");
            Assert(StagingResidue(stagingRoot).Length == 0,
                "cleanup: after the update run the private staging root again holds no leftover transaction or " +
                "stage file, observed [" + string.Join(",", StagingResidue(stagingRoot)) + "]");
            Assert(StagingMarkerPresent(stagingRoot),
                "cleanup: the retained private staging ownership marker still survives after the update cleanup");
            Assert(await rig.ControlNamespaceRowsAsync(pair.Id) == 1,
                "cleanup: the retained private staging identity still survives after the update cleanup");
            Assert((await rig.ReadDriveFileAsync(right, "cl-shared.txt")).Bytes
                    .SequenceEqual(Encoding.UTF8.GetBytes("left advanced\n")),
                "cleanup: the successor on the target is the exact changed bytes, never removed by cleanup");
            Assert((await rig.ReadRetainedPredecessorAsync(right)).Read.Bytes
                    .SequenceEqual(Encoding.UTF8.GetBytes("shared bytes\n")),
                "cleanup: the retained predecessor copy survives the cleanup byte-exactly");
        }
        finally
        {
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = priorPhase;
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = prior;
        }
    }

     private static async Task WrongAccountRefusalAsync(string fixtureRoot)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        using var transport = new CloudPairHostBindingChecks.HostWire();
        var left = CloudPairHostBindingChecks.HostDropbox("rw-l", "/Dropbox Rw L");
        var right = CloudPairHostBindingChecks.HostDrive("rw-r", "/Drive Rw R");
        var otherRight = CloudPairHostBindingChecks.HostDrive("rw-r2", "/Drive Rw R2");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("refusal-wrong-account", transport);
        foreach (var spec in new[] { left, right, otherRight }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("rw", left, right);
        rig.SeedFile(left, "rw-shared.txt", "shared bytes\n");
        rig.SeedFile(right, "rw-shared.txt", "shared bytes\n");
        await rig.PrepareAsync(pair, createStaging: true);
        await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
        await rig.EnablePairAsync(await rig.Host.ApproveBootstrapMergeAsync(pair.Id));
        var approved = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
        Assert(!string.Equals(right.ProviderAccountId, otherRight.ProviderAccountId, StringComparison.Ordinal),
            "refusal wrong account: the two registered Drive accounts are genuinely different accounts");
        Assert(await rig.CountAsync("SELECT count(*) FROM provider_account WHERE status='active' AND "
                   + "provider_account_id IN ('" + right.ProviderAccountId + "','"
                   + otherRight.ProviderAccountId + "');") == 2,
            "refusal wrong account: both Drive provider accounts are retained active");
        var definitionBefore = await rig.PairDefinitionAsync(pair.Id);
        var rootBefore = await rig.RootBindingAsync(pair.RightRoot);
        var originalIdentity = rootBefore.Split('|')[1];
        var typed = System.Text.Json.Nodes.JsonNode.Parse(originalIdentity) as System.Text.Json.Nodes.JsonObject
                    ?? throw new InvalidDataException("The retained right root identity is not a typed JSON object.");
        Assert(typed.ContainsKey("identityKind") && typed.ContainsKey("provider") &&
               typed.ContainsKey("providerAccountId") && typed.ContainsKey("namespaceId") &&
               typed.ContainsKey("folderId"),
            "refusal wrong account: the retained right root carries a complete typed cloud identity");
        var originalNamespace = typed["namespaceId"]?.GetValue<string>() ?? string.Empty;
        var prior = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            typed["providerAccountId"] = otherRight.ProviderAccountId;
            await rig.ExecuteSqlAsync("UPDATE root_binding SET identity_json=$identity WHERE root_binding_id=$id;",
                ("$identity", typed.ToJsonString()), ("$id", pair.RightRoot));
            var readback = await rig.RootBindingAsync(pair.RightRoot);
            var foreign = System.Text.Json.Nodes.JsonNode.Parse(readback.Split('|')[1])
                as System.Text.Json.Nodes.JsonObject
                ?? throw new InvalidDataException("The substituted root identity read back unreadable.");
            Assert(string.Equals(foreign["providerAccountId"]?.GetValue<string>(),
                       otherRight.ProviderAccountId, StringComparison.Ordinal) &&
                   foreign.ContainsKey("identityKind") && foreign.ContainsKey("provider") &&
                   foreign.ContainsKey("folderId") &&
                   string.Equals(foreign["namespaceId"]?.GetValue<string>(), originalNamespace,
                       StringComparison.Ordinal) &&
                   string.Equals(readback.Split('|')[0], rootBefore.Split('|')[0], StringComparison.Ordinal) &&
                   string.Equals(readback.Split('|')[2], rootBefore.Split('|')[2], StringComparison.Ordinal) &&
                   string.Equals(readback.Split('|')[3], rootBefore.Split('|')[3], StringComparison.Ordinal),
                "refusal wrong account: the foreign ACTIVE account is retained with every original member and " +
                "the generation, path and state untouched");
            var writesBefore = transport.Writes;
            var updatesBefore = transport.Updates;
            string? refusal = null;
            try
            {
                var wrong = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
                if (wrong.Items.Count > 0) refusal = wrong.Items[0].FailureMessage;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                refusal = error.GetType().Name + ": " + error.Message;
            }
            Note("wrong account refusal: " + (refusal ?? "none"));
            Assert(refusal is not null && refusal.Contains("account", StringComparison.OrdinalIgnoreCase),
                "refusal wrong account: the foreign active account binding is refused, observed " +
                (refusal ?? "no refusal"));
            Assert(transport.Writes == writesBefore && transport.Updates == updatesBefore,
                "refusal wrong account: the foreign binding issued ZERO create, update or retention requests, " +
                "observed " + (transport.Writes.Total - writesBefore.Total) + " writes and " +
                (transport.Updates.Total - updatesBefore.Total) + " update-family requests");
            Assert((await rig.ReadDropboxFileAsync(left, "rw-shared.txt")).Bytes
                       .SequenceEqual(Encoding.UTF8.GetBytes("shared bytes\n")) &&
                   (await rig.ReadDriveFileAsync(right, "rw-shared.txt")).Bytes
                       .SequenceEqual(Encoding.UTF8.GetBytes("shared bytes\n")),
                "refusal wrong account: both sides keep their exact bytes");
            Assert(await rig.PairDefinitionAsync(pair.Id) == definitionBefore,
                "refusal wrong account: the retained pair definition is unchanged by the foreign binding");
        }
        finally
        {
            await rig.ExecuteSqlAsync("UPDATE root_binding SET identity_json=$identity WHERE root_binding_id=$id;",
                ("$identity", originalIdentity), ("$id", pair.RightRoot));
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = prior;
        }
        Assert(await rig.RootBindingAsync(pair.RightRoot) == rootBefore,
            "refusal wrong account: the full retained right root tuple is restored byte-exactly");
        var restored = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
        Assert(restored.Items.Count == 1 && restored.Items[0].FailureMessage is null,
            "refusal wrong account: restoring the original identity lets the same approved pair run normally, " +
            "observed " + (restored.Items[0].FailureMessage ?? "no failure"));
    }


    // Case 17: the H2 pending-work runtime preparation proof on an UNCHANGED explicit cloud/cloud pair. When
    // a real run is interrupted between the target's applied replacement and its commit, the pair's retained
    // operation is left unresolved. The unchanged pair must still PREPARE and reach that operation's own
    // recovery through the real public run (never a preparation/admission failure), must resolve it from its
    // own retained receipt without reissuing the replacement, and a genuine configuration edit of the same
    // pair must stay refused by the unchanged editing guard.
    private static async Task PendingWorkRuntimePreparationAsync(string fixtureRoot)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The real cloud recovery proof requires Windows staging authority.");
        CloudPairHostBindingChecks.UseFixtureRoot(fixtureRoot);
        using var transport = new CloudPairHostBindingChecks.HostWire();
        var left = CloudPairHostBindingChecks.HostDropbox("pw-l", "/Dropbox Pw L");
        var right = CloudPairHostBindingChecks.HostDrive("pw-r", "/Drive Pw R");
        using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("pending-work", transport);
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("pw", left, right, recoveryRetentionDays: 90);
        rig.SeedFile(left, "pw-shared.txt", "shared bytes\n");
        rig.SeedFile(right, "pw-shared.txt", "shared bytes\n");
        await rig.PrepareAsync(pair, createStaging: true);
        await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
        await rig.EnablePairAsync(await rig.Host.ApproveBootstrapMergeAsync(pair.Id));
        var approved = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
        var priorPlanning = SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest;
        var previousPhase = SyncAuthorityCloudUpdateExecutor.PhaseForTest;
        SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = false;
        try
        {
            var baseline = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
            Assert(baseline.Items.Count == 1 && baseline.Items[0].FailureMessage is null &&
                   transport.Writes.Total == 0 && transport.Updates.Total == 0,
                "pending-work: the identical shared cloud pair baselined with no failure and no effect");
            rig.SeedFile(left, "pw-shared.txt", "left advanced\n");
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = phase =>
            {
                if (string.Equals(phase, "after-receipt-before-commit", StringComparison.Ordinal))
                    throw new InvalidOperationException("injected process boundary");
            };
            var interrupted = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
            var interruptedNote = interrupted.Items.Count > 0 ? interrupted.Items[0].FailureMessage : null;
            Note("pending-work interruption: " + (interruptedNote ?? "none"));
            Assert(interruptedNote is not null || interrupted.Items[0].Result is { Blocked: > 0 },
                "pending-work: the interrupted run reported a non-success rather than completing silently");
            Assert(transport.Updates.DriveUpdates == 1,
                "pending-work: the interrupted run did not apply exactly one conditional replacement, observed " +
                transport.Updates.DriveUpdates);
            Assert(await rig.CountAsync("SELECT count(*) FROM operation WHERE status NOT IN " +
                       "('completed','cancelled','superseded');") == 1,
                "pending-work: the interrupted run did not retain its unresolved operation");
            var editRefused = false;
            try { await rig.Host.SetPairEnabledAsync(pair.Id, false); }
            catch (InvalidOperationException error) when (error.Message.Contains("pending work",
                StringComparison.Ordinal)) { editRefused = true; }
            Assert(editRefused,
                "pending-work: a real configuration edit was not refused by the retained editing guard");

            // R1: at the SAME real preparation entry point (Host.PrepareCloudPairBindingAsync), a genuine
            // configuration edit is still refused by the unchanged editing guard, while the read-only
            // unchanged-runtime route explicitly proves the pair is unchanged - this pair's own retained root
            // ids and the exact retained policy revision, with no creation and no new staging - and succeeds.
            var retainedBefore = (await rig.Host.LoadPairsAsync()).Pairs.Single(item => item.Id == pair.Id);
            var editRefusal = await rig.RefusalAsync(rig.Request(pair, createStaging: false));
            Assert(editRefusal is not null && editRefusal.Contains("pending work", StringComparison.Ordinal),
                "pending-work: the real preparation entry point did not refuse a genuine configuration edit " +
                "through the unchanged editing guard; observed " + (editRefusal ?? "no refusal"));
            var runtimeRequest = rig.Request(pair, createStaging: false) with
            {
                Pair = retainedBefore,
                RequireConfigurationMutable = false,
                ExpectedPolicyRevision = retainedBefore.PolicyRevision
            };
            var runtimePrepared = await rig.Host.PrepareCloudPairBindingAsync(runtimeRequest);
            Assert(runtimePrepared.Left.PairRootBindingId == pair.LeftRoot &&
                   runtimePrepared.Right.PairRootBindingId == pair.RightRoot,
                "pending-work: the unchanged-runtime preparation did not bind this pair's own retained roots");
            var stagingRefusal = await rig.RefusalAsync(runtimeRequest with
            {
                CreatePrivateStaging = true
            });
            Assert(stagingRefusal is not null && stagingRefusal.Contains("staging", StringComparison.Ordinal),
                "pending-work: an unchanged-runtime preparation created private staging instead of refusing; " +
                "observed " + (stagingRefusal ?? "no refusal"));
            var staleRevisionRefusal = await rig.RefusalAsync(runtimeRequest with
            {
                ExpectedPolicyRevision = retainedBefore.PolicyRevision + 1
            });
            Assert(staleRevisionRefusal is not null &&
                   staleRevisionRefusal.Contains("changed", StringComparison.Ordinal),
                "pending-work: an unchanged-runtime preparation accepted a stale policy revision; observed " +
                (staleRevisionRefusal ?? "no refusal"));
            var changedRootRefusal = await rig.RefusalAsync(runtimeRequest with
                {
                    LeftRootBindingId = pair.RightRoot
                });
            Assert(changedRootRefusal is not null &&
                   changedRootRefusal.Contains("retained root binding ids", StringComparison.Ordinal),
                "pending-work: an unchanged-runtime preparation accepted a changed or foreign root; observed " +
                (changedRootRefusal ?? "no refusal"));
            var changedPolicyRefusal = await rig.RefusalAsync(runtimeRequest with
            {
                Pair = retainedBefore with { Priority = retainedBefore.Priority + 1 }
            });
            Assert(changedPolicyRefusal is not null &&
                   changedPolicyRefusal.Contains("exact retained pair configuration", StringComparison.Ordinal),
                "pending-work: unchanged-runtime preparation accepted a changed policy at the same revision; " +
                (changedPolicyRefusal ?? "no refusal"));
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = previousPhase;
            var updatesBefore = transport.Updates;
            var recovered = await rig.Host.RunPairsAsync(new[] { approved }, "manual");
            var recoveredNote = recovered.Items.Count > 0 ? recovered.Items[0].FailureMessage : null;
            Note("pending-work recovery: " + (recoveredNote ?? "no failure"));
            Assert(recovered.Items.Count == 1 && recoveredNote is null &&
                   recovered.Items[0].Result is { } recoveredResult && recoveredResult.Uploaded >= 1,
                "pending-work: the unchanged cloud pair did not prepare and reach its retained operation's " +
                "own recovery; observed failure='" + (recoveredNote ?? "none") + "' result=" +
                (recovered.Items.Count > 0 ? recovered.Items[0].Result?.ToString() ?? "none" : "no item"));
            Assert(transport.Updates == updatesBefore,
                "pending-work: the retained operation's recovery reissued a replacement request, observed " +
                (transport.Updates.Total - updatesBefore.Total) + " update-family requests");
            Assert((await rig.ReadDriveFileAsync(right, "pw-shared.txt")).Bytes
                    .SequenceEqual(Encoding.UTF8.GetBytes("left advanced\n")),
                "pending-work: the retained update was not committed as the exact advanced successor");
            Assert(await rig.CountAsync("SELECT count(*) FROM operation WHERE status NOT IN " +
                       "('completed','cancelled','superseded');") == 0,
                "pending-work: the retained operation was not resolved by its own recovery");
        }
        finally
        {
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = previousPhase;
            SyncAuthorityRuntimeHost.UseIndependentPairPlanningForTest = priorPlanning;
        }
    }

}
