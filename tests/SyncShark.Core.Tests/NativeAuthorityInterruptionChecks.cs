using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Original M4 / Claude D-F3. Root owns selector wiring, serial compilation and execution.
// Synthetic remote bytes are inputs only: Host plans, fences, stages, promotes and recovers.
// The current child binary EXITs before the receipt; throwing/disposing is not this witness.
// NativeLocalAuthorityChecks' existing normal/manual/legacy witnesses are not rerun here.
[SupportedOSPlatform("windows")]
internal static class NativeAuthorityInterruptionChecks
{
    private const string Parent = "destination";
    private const string Relative = Parent + "/interrupted.txt";
    private const string Before = "retained native authority predecessor\n";
    private const string After = "promoted native authority successor\n";
    private const string Foreign = "later user bytes at the formerly promoted destination\n";
    private const int CrashExit = 73;
    private const string ChildSelector = "--native-authority-interruption-crash";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Root wires --native-authority-interruption-checks <fresh owned root>.
    internal static async Task RunAsync(string root)
    {
        RequireWindows();
        var exactRoot = Path.GetFullPath(root);
        if (File.Exists(exactRoot) || Directory.Exists(exactRoot) &&
            Directory.EnumerateFileSystemEntries(exactRoot).Any())
            throw new IOException("The native interruption suite requires an empty, newly allocated fixture root.");
        Directory.CreateDirectory(exactRoot);
        using var rootLease = PilotDirectoryLease.Open(exactRoot);
        rootLease.VerifyBinding();
        var results = new List<object>();
        var failures = new List<string>();
        foreach (var mode in new[] { "create", "update" })
        foreach (var changedIdentity in new[] { false, true })
        {
            var caseId = "M4-authority-" + mode + (changedIdentity ? "-changed-identity" : "-reopen");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                await InterruptedAsync(Contained(exactRoot, caseId), mode, changedIdentity, deadline.Token);
                results.Add(new { caseId, passed = true });
            }
            catch (Exception error)
            {
                // Continue the finite case set; do not turn one ordinary failure into omitted coverage.
                failures.Add(caseId + ": " + error.GetType().Name + ": " + error.Message);
                results.Add(new { caseId, passed = false, error = error.GetType().Name, detail = error.Message });
            }
            rootLease.VerifyBinding();
        }
        await WriteJsonAsync(Contained(exactRoot, "native-authority-interruption-result.json"), new
        {
            requirement = "M4", entrypoint = "SyncAuthorityRuntimeHost.RunPairsAsync",
            cases = results, failures,
            testBinary = Binary(typeof(NativeAuthorityInterruptionChecks).Assembly.Location),
            coreBinary = Binary(typeof(SyncAuthorityRuntimeHost).Assembly.Location),
            manualRestoreInterruption = "UNVERIFIED: no retained command/stage-bound public recovery seam; not covered by this suite"
        }, CancellationToken.None);
        if (failures.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, failures));
    }

    // Root wires --native-authority-interruption-crash <fresh case root> <create|update>.
    internal static async Task CrashAsync(string root, string mode)
    {
        RequireWindows();
        ValidateMode(mode);
        Require(SyncAuthorityCreateLocalExecutor.PhaseForTest is null &&
                SyncAuthorityUpdateLocalExecutor.PhaseForTest is null,
            "M4 interruption does not replace another writer's phase hook");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var fixture = await Fixture.CreateAsync(root, deadline.Token);
        // A real, deliberately short renewable lease in this isolated subprocess only.
        // The parent lets it expire naturally; no SQL/status reset or fabricated recovery admission.
        var priorLease = SyncAuthorityLeasePolicy.LeaseDuration;
        var priorRenewal = SyncAuthorityLeasePolicy.RenewalInterval;
        SyncAuthorityLeasePolicy.LeaseDuration = TimeSpan.FromSeconds(2);
        SyncAuthorityLeasePolicy.RenewalInterval = TimeSpan.FromMilliseconds(250);
        try
        {
            await fixture.RunHealthyAsync(deadline.Token); // Real empty first observation/adoption.
            if (mode == "update")
            {
                fixture.Remote.Put(Relative, Before);
                var baseline = await fixture.RunHealthyAsync(deadline.Token);
                Require(baseline.Downloaded == 1 && await File.ReadAllTextAsync(fixture.Target, deadline.Token) == Before,
                    "M4 update starts from the Host's actually downloaded/committed predecessor");
            }
            fixture.Remote.Put(Relative, After);
            var phase = Phase(mode);
            Action<string> stop = observed =>
            {
                if (observed != phase) return;
                var operationId = fixture.PendingOperationIdAsync(mode, deadline.Token).GetAwaiter().GetResult();
                var operation = fixture.Db.LoadOperationAsync(operationId, deadline.Token).GetAwaiter().GetResult();
                var stage = operation.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.StageFile);
                var transaction = operation.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
                var attempt = operation.Attempts.OrderBy(item => item.AttemptNumber).Last();
                var stagePath = Contained(Contained(fixture.ControlRoot, transaction.DeterministicName), stage.DeterministicName);
                var sidecar = SyncLocalFileAuthority.RecoverySidecarPath(stagePath);
                Require(operation.Kind == Kind(mode) && operation.Status == SyncAuthorityOperationStatus.EffectIssuing &&
                        operation.Members.Any(item => item.ExpectedPathKey == Relative) &&
                        transaction.Status == SyncAuthorityAllocationStatus.Verified && transaction.BoundIdentity is not null &&
                        stage.Status == SyncAuthorityAllocationStatus.Verified && stage.BoundIdentity is not null &&
                        attempt.Status == SyncAuthorityAttemptStatus.RequestSent &&
                        attempt.EffectKind == Effect(mode) && !File.Exists(stagePath) && File.Exists(fixture.Target) &&
                        File.Exists(sidecar),
                    "M4 abrupt exit follows the real identity-bound stage promotion and pending access sidecar");
                Require(fixture.Db.LoadSuccessfulAttemptDestinationsAsync(attempt.AttemptId, deadline.Token)
                        .GetAwaiter().GetResult().Count == 0,
                    "M4 exact promoted attempt has no successful receipt at the interruption boundary");
                fixture.RequireNoCloudWrites();
                var boundary = new Boundary(mode, phase, fixture.Root, fixture.Db.DatabasePath, fixture.Pair.Id,
                    operation, attempt.AttemptId, stagePath, sidecar, fixture.Target,
                    fixture.Remote.FindEntryAtPathAsync(Relative, deadline.Token).GetAwaiter().GetResult()
                        ?? throw new InvalidDataException("The actual synthetic source disappeared."),
                    fixture.Remote.DownloadCount, Environment.ProcessId,
                    Binary(Environment.ProcessPath ?? throw new InvalidDataException("The current executable is absent.")),
                    Binary(typeof(NativeAuthorityInterruptionChecks).Assembly.Location),
                    Binary(typeof(SyncAuthorityRuntimeHost).Assembly.Location));
                WriteJsonAsync(Contained(fixture.Root, "interrupted-boundary.json"), boundary, CancellationToken.None)
                    .GetAwaiter().GetResult();
                Console.Out.Flush(); Console.Error.Flush();
                Environment.Exit(CrashExit); // Do not dispose the product stage/sidecar/Host.
            };
            if (mode == "create") SyncAuthorityCreateLocalExecutor.PhaseForTest = stop;
            else SyncAuthorityUpdateLocalExecutor.PhaseForTest = stop;
            await fixture.RunHealthyAsync(deadline.Token);
        }
        finally
        {
            SyncAuthorityCreateLocalExecutor.PhaseForTest = null;
            SyncAuthorityUpdateLocalExecutor.PhaseForTest = null;
            SyncAuthorityLeasePolicy.LeaseDuration = priorLease;
            SyncAuthorityLeasePolicy.RenewalInterval = priorRenewal;
        }
        throw new InvalidDataException("The real Host never reached the exact promotion-before-receipt exit boundary.");
    }

    private static async Task InterruptedAsync(string root, string mode, bool changedIdentity, CancellationToken token)
    {
        var testBinary = Binary(typeof(NativeAuthorityInterruptionChecks).Assembly.Location);
        var coreBinary = Binary(typeof(SyncAuthorityRuntimeHost).Assembly.Location);
        var executable = Binary(Environment.ProcessPath ?? throw new InvalidDataException("Current executable is absent."));
        var start = new ProcessStartInfo(executable.Path)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable.Path), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(testBinary.Path);
        start.ArgumentList.Add(ChildSelector); start.ArgumentList.Add(Path.GetFullPath(root)); start.ArgumentList.Add(mode);
        using var child = Process.Start(start) ?? throw new IOException("The exact owned crash child did not start.");
        var childPid = child.Id;
        var childStartedUtc = child.StartTime.ToUniversalTime();
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            // Retain the native identity before any timeout/termination. The child case root must remain
            // fresh for its producer, so this receipt belongs beside it in the already-owned suite root.
            await WriteJsonAsync(Contained(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "-child-invocation.json"),
                new { childPid, childStartedUtc, executable, testBinary, coreBinary, selector = ChildSelector, mode, caseRoot = root }, token);
            await child.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(75), token);
        }
        catch
        {
            // Only the one native process handle created above, never a name/age/process-tree kill.
            if (!child.HasExited) { child.Kill(entireProcessTree: false); await child.WaitForExitAsync(); }
            Directory.CreateDirectory(root);
            await WriteJsonAsync(Contained(root, "child-timeout.json"),
                new { childPid, childStartedUtc, executable, testBinary, coreBinary }, CancellationToken.None);
            await File.WriteAllTextAsync(Contained(root, "child.stdout.log"), await stdout, CancellationToken.None);
            await File.WriteAllTextAsync(Contained(root, "child.stderr.log"), await stderr, CancellationToken.None);
            throw;
        }
        Require(Binary(testBinary.Path) == testBinary && Binary(coreBinary.Path) == coreBinary &&
                Binary(executable.Path) == executable, "M4 parent/child binaries are unchanged across this exact invocation");
        await File.WriteAllTextAsync(Contained(root, "child.stdout.log"), await stdout, token);
        await File.WriteAllTextAsync(Contained(root, "child.stderr.log"), await stderr, token);
        Require(child.ExitCode == CrashExit && File.Exists(Contained(root, "interrupted-boundary.json")),
            "M4 child exit code and retained boundary distinguish real abrupt exit from a thrown error or skipped hook");
        var boundary = JsonSerializer.Deserialize<Boundary>(await File.ReadAllTextAsync(Contained(root, "interrupted-boundary.json"), token), Json)
                       ?? throw new InvalidDataException("The exact crash boundary is absent.");
        Require(boundary.Mode == mode && boundary.Phase == Phase(mode) && boundary.Root == Path.GetFullPath(root) &&
                boundary.ProcessId == childPid && boundary.Executable == executable &&
                boundary.TestBinary == testBinary && boundary.CoreBinary == coreBinary &&
                boundary.Downloads == (mode == "create" ? 1 : 2),
            "M4 retained boundary binds the same real producer, binary, phase and actual download count");
        using var fixture = await Fixture.OpenAsync(root, token);
        if (mode == "update") fixture.Remote.Put(Relative, Before);
        fixture.Remote.Put(Relative, After); // Same synthetic file id, revision and bytes across process exit.
        Require(await fixture.Remote.FindEntryAtPathAsync(Relative, token) == boundary.Remote,
            "M4 reopened controlled remote input has the identical retained revision/content, not a substituted decision");
        var before = await fixture.Db.LoadOperationAsync(boundary.Operation.OperationId, token);
        Require(before.Kind == Kind(mode) && before.Status == SyncAuthorityOperationStatus.EffectIssuing &&
                before.CurrentLeaseId == boundary.Operation.CurrentLeaseId &&
                before.Allocations.SequenceEqual(boundary.Operation.Allocations) &&
                before.Attempts.SequenceEqual(boundary.Operation.Attempts) &&
                fixture.Db.DatabasePath == boundary.DatabasePath && fixture.Pair.Id == boundary.PairId &&
                fixture.Target == boundary.TargetPath && File.Exists(boundary.SidecarPath),
            "M4 cold Host reopens the same real operation/allocations/pending attempt and protected sidecar without resetting them");
        var pendingAttempt = before.Attempts.Single(item => item.AttemptId == boundary.AttemptId);
        // Wait once for this actual retained lease's expiry, not an assumed clock advance or SQL reset.
        // Renewal may have extended it while the producer retained the boundary. Oversize waits fail closed.
        var expiresUtc = await fixture.PendingLeaseExpiryAsync(before.OperationId, token);
        var remainingLease = expiresUtc - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100);
        Require(remainingLease <= TimeSpan.FromSeconds(30), "M4 retained fixture lease has a finite admitted reopen delay");
        if (remainingLease > TimeSpan.Zero) await Task.Delay(remainingLease, token);
        Require((await fixture.Db.LoadSuccessfulAttemptDestinationsAsync(pendingAttempt.AttemptId, token)).Count == 0 &&
                (await fixture.Db.ListRecoveryOperationsForPairAsync(fixture.Pair.Id, cancellationToken: token))
                    .Any(item => item.OperationId == before.OperationId && item.RecoveryRequired),
            "M4 actual expired-lease recovery queue admits the retained unresolved effect without a fabricated receipt");
        var sidecar = FileStamp(boundary.SidecarPath);
        var completedBefore = await fixture.CompletedPromotionsAsync(token);
        var pointsBefore = await fixture.SyncPointsAsync(before.ObjectId!, token);
        var recovery = await RequirePredecessorAsync(fixture, before.OperationId, mode, token);
        if (changedIdentity)
        {
            await RejectChangedIdentityAsync(fixture, boundary, sidecar, completedBefore, pointsBefore, recovery, token);
            return;
        }
        await fixture.RunHealthyAsync(token); // The public Host recovery dispatch, not a reconciler called by the test.
        var completed = await fixture.Db.LoadOperationAsync(before.OperationId, token);
        Require(completed.Status == SyncAuthorityOperationStatus.Completed &&
                completed.Attempts.Count == before.Attempts.Count && completed.Attempts.All(item => item.Status == SyncAuthorityAttemptStatus.VerifiedSuccess) &&
                !File.Exists(boundary.SidecarPath) && !File.Exists(boundary.StagePath) &&
                Identity(fixture.Target) == before.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.StageFile).BoundIdentity &&
                await File.ReadAllTextAsync(fixture.Target, token) == After && fixture.Remote.DownloadCount == 0 &&
                (await fixture.Db.LoadSuccessfulAttemptDestinationsAsync(pendingAttempt.AttemptId, token)).Count == 1 &&
                await fixture.CompletedPromotionsAsync(token) == completedBefore + 1 &&
                await fixture.SyncPointsAsync(before.ObjectId!, token) == pointsBefore + 1,
            "M4 public recovery commits only the exact promoted identity, drains its owned sidecar and does not redownload/reissue");
        AssertInherited(fixture.Target, "M4 " + mode + " recovered successor");
        await RequirePredecessorAsync(fixture, before.OperationId, mode, token, recovery);
        var completedAttemptIds = completed.Attempts.Select(item => item.AttemptId).ToArray();
        var finalFile = FileStamp(fixture.Target);
        var completedCount = await fixture.CompletedPromotionsAsync(token);
        var pointCount = await fixture.SyncPointsAsync(before.ObjectId!, token);
        await fixture.ReopenAsync(token);
        await fixture.RunHealthyAsync(token);
        var repeated = await fixture.Db.LoadOperationAsync(before.OperationId, token);
        Require(repeated.Status == SyncAuthorityOperationStatus.Completed &&
                repeated.Attempts.Select(item => item.AttemptId).SequenceEqual(completedAttemptIds) &&
                FileStamp(fixture.Target) == finalFile && !File.Exists(boundary.SidecarPath) &&
                fixture.Remote.DownloadCount == 0 && await fixture.CompletedPromotionsAsync(token) == completedCount &&
                await fixture.SyncPointsAsync(before.ObjectId!, token) == pointCount,
            "M4 second cold reopen/public run retains the committed bytes/identity and never repeats a completed effect");
        AssertInherited(fixture.Target, "M4 " + mode + " second-reopen successor");
        await RequirePredecessorAsync(fixture, before.OperationId, mode, token, recovery);
        fixture.RequireNoCloudWrites();
        await WriteJsonAsync(Contained(root, "reopen-proof.json"), new
        { boundary, ownedSidecarBefore = sidecar, finalFile, completed, repeated, completedCount, pointCount, recovery,
            inheritedDacl = new FileInfo(fixture.Target).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access),
            reopenedDownloads = fixture.Remote.DownloadCount }, token);
    }

    private static async Task RejectChangedIdentityAsync(Fixture fixture, Boundary boundary, OwnedFile sidecar,
        long completedBefore, long pointsBefore, OwnedFile? recovery, CancellationToken token)
    {
        // Fixture INPUT: the user owns WRITE_DAC on the isolated target. Restore access on this one
        // owned file solely to move/retain the promoted bytes; do NOT consume/decrypt/delete its sidecar.
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentSid(), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(fixture.Target).SetAccessControl(security);
        Require(Identity(fixture.Target) == boundary.Operation.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.StageFile).BoundIdentity,
            "M4 changed-identity input starts with the producer's actual promoted file identity");
        var retainedPath = Contained(fixture.Root, "preserved-promoted.bin");
        File.Move(fixture.Target, retainedPath); // Preserve the former promoted object; no test deletion.
        await File.WriteAllTextAsync(fixture.Target, Foreign, token);
        var edited = FileStamp(fixture.Target);
        var retained = FileStamp(retainedPath);
        Require(edited.Identity != retained.Identity && await File.ReadAllTextAsync(retainedPath, token) == After,
            "M4 the changed target has distinct identity and every displaced promoted byte remains retained");
        for (var reopen = 0; reopen < 2; reopen++)
        {
            if (reopen != 0) await fixture.ReopenAsync(token);
            await fixture.RunRejectedAsync(token);
            var state = await fixture.Db.LoadOperationAsync(boundary.Operation.OperationId, token);
            Require(state.Status is not (SyncAuthorityOperationStatus.Completed or SyncAuthorityOperationStatus.Committed or
                        SyncAuthorityOperationStatus.CleanupPending or SyncAuthorityOperationStatus.Cancelled or SyncAuthorityOperationStatus.Superseded) &&
                    state.Attempts.Count == boundary.Operation.Attempts.Count &&
                    state.Allocations.All(item => item.Status != SyncAuthorityAllocationStatus.Released) &&
                    (await fixture.Db.LoadSuccessfulAttemptDestinationsAsync(boundary.AttemptId, token)).Count == 0 &&
                    FileStamp(fixture.Target) == edited && FileStamp(retainedPath) == retained &&
                    FileStamp(boundary.SidecarPath) == sidecar && fixture.Remote.DownloadCount == 0 &&
                    await fixture.CompletedPromotionsAsync(token) == completedBefore &&
                    await fixture.SyncPointsAsync(state.ObjectId!, token) == pointsBefore,
                "M4 changed promoted identity refuses success/replay/release across reopen and preserves edited bytes and exact owned sidecar");
            await RequirePredecessorAsync(fixture, state.OperationId, boundary.Mode, token, recovery);
            fixture.RequireNoCloudWrites();
        }
        await WriteJsonAsync(Contained(fixture.Root, "identity-rejection-proof.json"),
            new { boundary, edited, retained, sidecar, recovery, completedBefore, pointsBefore, reopenedDownloads = fixture.Remote.DownloadCount }, token);
    }

    private static async Task<OwnedFile?> RequirePredecessorAsync(Fixture fixture, string operationId, string mode,
        CancellationToken token, OwnedFile? expected = null)
    {
        var artifacts = (await fixture.Db.LoadRecoveryArtifactsAsync(token)).Where(item => item.OperationId == operationId && item.Side == "local").ToArray();
        if (mode == "create")
        { Require(artifacts.Length == 0, "M4 absent-destination create invents no predecessor archive"); return null; }
        Require(artifacts.Length == 1, "M4 actual update producer retains exactly one predecessor archive");
        using var value = JsonDocument.Parse(artifacts[0].IdentityJson);
        var path = value.RootElement.GetProperty("path").GetString() ?? throw new InvalidDataException("Actual predecessor path is absent.");
        Require(Contained(fixture.ControlRoot, Path.GetRelativePath(fixture.ControlRoot, path)) == Path.GetFullPath(path),
            "M4 actual predecessor is in this pair's verified private control root");
        var actual = FileStamp(path);
        Require(actual.Identity == value.RootElement.GetProperty("identity").GetString() &&
                await File.ReadAllTextAsync(path, token) == Before && (expected is null || actual == expected),
            "M4 retained predecessor identity/bytes survive the interrupted update and every recovery/refusal/reopen");
        return actual;
    }

    // NativeLocalAuthorityChecks.Fixture is private and CloudPairHostBindingChecks.HostRig is cloud-pair
    // specific. This narrow native construction follows its existing retained-pair/Host recipe, but shares
    // one implementation between create/update/rejection and adds no legacy or replacement decision fake.
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; }
        internal string LocalRoot => Contained(Root, "local");
        internal string Target => Contained(LocalRoot, Relative);
        internal string ControlRoot => Pair.ControlNamespace!.Local.RootPath;
        internal FakeSyncRemoteStore Remote { get; } = new() { ExactPathMetadataForTest = true };
        internal SyncPair Pair = null!;
        internal SyncAuthorityDatabase Db = null!;
        internal SyncAuthorityRuntimeHost Host = null!;
        private PilotInstanceLock instance = null!;
        private HttpClient http = null!;
        private readonly DenyNetwork transport = new();
        private DaclPreimage? preimage;
        private static readonly DropboxConnectionBinding Connection = new DropboxConnectionBinding(
            DropboxProviderConfiguration.RegisteredClientId, "native-interruption-account", "native-interruption-root", "native-interruption-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes).Normalize();
        private Fixture(string root) => Root = Path.GetFullPath(root);

        internal static async Task<Fixture> CreateAsync(string root, CancellationToken token)
        {
            var f = new Fixture(root);
            if (File.Exists(f.Root) || Directory.Exists(f.Root)) throw new IOException("Native interruption case root must be fresh.");
            Directory.CreateDirectory(f.LocalRoot);
            Directory.CreateDirectory(Contained(f.LocalRoot, Parent));
            using var local = PilotDirectoryLease.Open(f.LocalRoot);
            using var parent = PilotDirectoryLease.Open(Contained(f.LocalRoot, Parent));
            local.VerifyBinding(); parent.VerifyBinding();
            f.preimage = new DaclPreimage(local.Identity, parent.Identity,
                new DirectoryInfo(local.Path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                new DirectoryInfo(parent.Path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            await WriteJsonAsync(Contained(f.Root, "owned-dacl-preimage.json"), f.preimage, token);
            var localSecurity = ParentSecurity(distinctive: false);
            new DirectoryInfo(local.Path).SetAccessControl(localSecurity);
            new DirectoryInfo(parent.Path).SetAccessControl(ParentSecurity(distinctive: true));
            try
            {
                f.instance = PilotInstanceLock.Acquire(Contained(f.Root, "state/instance.lock"));
                var store = new SyncPairStore(f.instance.StateAuthority);
                var installation = await store.GetOrCreateInstallationIdAsync(token);
                var pairId = Guid.NewGuid().ToString("N");
                var localControl = SyncControlNamespace.BindLocalForTest(local, installation, pairId);
                var remotePath = SyncControlNamespace.RemoteContainerPath + "/" + pairId;
                var remoteControl = new SyncRemoteControlNamespaceBinding(remotePath, "id:native-interruption-control",
                    remotePath + "/.syncshark-owner.json", "id:native-interruption-marker", "rev:marker-1",
                    DropboxContentHash.Compute(SyncControlNamespace.MarkerBytes(installation, pairId)));
                f.Pair = new SyncPair(pairId, "M4 isolated authority interruption", f.LocalRoot,
                    local.Identity.VolumeSerialNumber, local.Identity.FileId, "/NativeInterruption", "id:native-interruption-root",
                    ConnectionId: Connection.ConnectionId, InitialReviewApproved: true, FilesOnDemandEnabled: false,
                    ControlNamespace: new SyncControlNamespaceBinding(installation, pairId, localControl, remoteControl),
                    RecoveryRoot: new SyncRemoteRecoveryRootBinding(SyncDropboxArtifactPaths.RecoveryContainerPath,
                        "id:native-interruption-recovery", Connection.RootNamespaceId));
                await store.SaveAsync(new SyncPairSet([f.Pair], f.Pair.Id), token);
                await new DropboxConnectionStore(f.instance.StateAuthority).SaveNewAsync(Connection, token);
                await new SyncAutomationStore(f.instance.StateAuthority).SaveAsync(new SyncAutomationSettings(false, 15, false), token);
                f.Remote.PutDirectory(Parent);
                await f.InitializeAsync(token, reopening: false);
                Require(!HasInheritedWorldRead(parent.Path) &&
                        !new DirectoryInfo(f.ControlRoot).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                            .Cast<FileSystemAccessRule>().Any(rule => rule.IdentityReference.Equals(World()) && rule.AccessControlType == AccessControlType.Allow),
                    "M4 distinctive inheritable destination ACE is absent from the actual private stage/control parent");
                return f;
            }
            catch { f.Dispose(); throw; }
        }

        internal static async Task<Fixture> OpenAsync(string root, CancellationToken token)
        {
            var f = new Fixture(root);
            f.preimage = JsonSerializer.Deserialize<DaclPreimage>(await File.ReadAllTextAsync(Contained(f.Root, "owned-dacl-preimage.json"), token), Json)
                         ?? throw new InvalidDataException("The owned DACL preimage is absent.");
            using (var local = PilotDirectoryLease.Open(f.LocalRoot, f.preimage.LocalIdentity)) local.VerifyBinding();
            using (var parent = PilotDirectoryLease.Open(Contained(f.LocalRoot, Parent), f.preimage.ParentIdentity)) parent.VerifyBinding();
            try
            {
                f.instance = PilotInstanceLock.Acquire(Contained(f.Root, "state/instance.lock"));
                f.Pair = new SyncPairStore(f.instance.StateAuthority).Load().Pairs.Single();
                Require(f.Pair.LocalRoot == f.LocalRoot && f.Pair.ControlNamespace is not null &&
                        new PilotDirectoryIdentity(f.Pair.LocalVolumeSerialNumber, f.Pair.LocalRootFileId) == f.preimage.LocalIdentity,
                    "M4 reopened Host retains the exact native local/control binding");
                f.Remote.PutDirectory(Parent);
                await f.InitializeAsync(token, reopening: true);
                return f;
            }
            catch { f.Dispose(); throw; }
        }

        private async Task InitializeAsync(CancellationToken token, bool reopening)
        {
            http = new HttpClient(transport, disposeHandler: false);
            Db = new SyncAuthorityDatabase(Contained(Root, "authority/syncshark.db"));
            Host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http, new SyncPairStore(instance.StateAuthority),
                new SyncRuntimeStateStore(instance.StateAuthority), new DropboxConnectionStore(instance.StateAuthority),
                new DenyCredentials(), Db, (_, _) => Remote, googleDriveCredentialStore: new DenyCredentials());
            await Host.LoadPairsAsync(token);
            // Only this isolated Host; real available-memory admission is retained.
            // Reopen verifies persistence before a potential update, rather than silently replacing settings.
            if (reopening)
                Require(Host.LoadTransferSettings().ResourceMode == "Performance", "M4 cold reopen persists the isolated functional mode without rewriting it");
            else
                await Host.SaveTransferSettingsAsync(Host.LoadTransferSettings() with { ResourceMode = "Performance" }, token);
            Require(Host.LoadTransferSettings().ResourceMode == "Performance", "M4 isolated functional ResourceMode is retained");
        }

        internal async Task ReopenAsync(CancellationToken token)
        {
            Host.Dispose(); http.Dispose(); instance.Dispose();
            instance = PilotInstanceLock.Acquire(Contained(Root, "state/instance.lock"));
            await InitializeAsync(token, reopening: true);
            Pair = (await Host.LoadPairsAsync(token)).Pairs.Single();
        }
        internal async Task<SyncRunResult> RunHealthyAsync(CancellationToken token)
        {
            var batch = await Host.RunPairsAsync([Pair], "Manual", cancellationToken: token);
            Console.WriteLine(JsonSerializer.Serialize(new { entrypoint = "Host.RunPairsAsync", batch }));
            var item = batch.Items.Single();
            Require(!batch.Stopped && item.FailureType is null && item.Result is not null &&
                    item.Result.Decisions.All(decision => decision.Kind is not (SyncDecisionKind.Blocked or SyncDecisionKind.Conflict or SyncDecisionKind.Stopped)),
                "M4 actual public Host completes the isolated run without synthetic admission or helper-only recovery");
            return item.Result!;
        }
        internal async Task RunRejectedAsync(CancellationToken token)
        {
            var batch = await Host.RunPairsAsync([Pair], "Manual", cancellationToken: token);
            Console.WriteLine(JsonSerializer.Serialize(new { entrypoint = "Host.RunPairsAsync", expected = "changed-identity rejection", batch }));
            var item = batch.Items.Single();
            Require(!batch.Stopped && (item.FailureType is not null || item.Result is not null &&
                    item.Result.Decisions.Any(decision => decision.Kind is SyncDecisionKind.Blocked or SyncDecisionKind.Conflict or SyncDecisionKind.ResponseUnknown)),
                "M4 public Host visibly refuses changed promoted identity, not a silent green result");
        }
        internal async Task<string> PendingOperationIdAsync(string mode, CancellationToken token)
        {
            await using var connection = ReadOnlyConnection(); await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT operation_id FROM operation WHERE pair_id=$pair AND kind=$kind AND status='effect_issuing';";
            command.Parameters.AddWithValue("$pair", Pair.Id); command.Parameters.AddWithValue("$kind", mode == "create" ? "create_local" : "update_local");
            var ids = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) ids.Add(reader.GetString(0));
            Require(ids.Count == 1, "M4 one actual pending public-planner operation identifies the exact promoted file");
            return ids.Single();
        }
        internal Task<long> CompletedPromotionsAsync(CancellationToken token) => CountAsync(
            "SELECT count(*) FROM operation_attempt a JOIN operation o ON o.operation_id=a.operation_id WHERE o.pair_id=$pair AND a.status='verified_success' AND a.effect_kind IN ('local-create-from-dropbox-revision','local-promote-stage');", null, token);
        internal Task<long> SyncPointsAsync(string objectId, CancellationToken token) => CountAsync(
            "SELECT count(*) FROM synchronization_point WHERE pair_id=$pair AND object_id=$object;", objectId, token);
        internal async Task<DateTimeOffset> PendingLeaseExpiryAsync(string operationId, CancellationToken token)
        {
            await using var connection = ReadOnlyConnection(); await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT l.expires_utc FROM lease l JOIN operation o ON o.current_lease_id=l.lease_id WHERE o.operation_id=$id;";
            command.Parameters.AddWithValue("$id", operationId);
            var value = (string?)await command.ExecuteScalarAsync(token)
                        ?? throw new InvalidDataException("The real pending operation lease is absent.");
            return DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        private async Task<long> CountAsync(string sql, string? objectId, CancellationToken token)
        {
            await using var connection = ReadOnlyConnection(); await connection.OpenAsync(token);
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            command.Parameters.AddWithValue("$pair", Pair.Id);
            if (objectId is not null) command.Parameters.AddWithValue("$object", objectId);
            return Convert.ToInt64(await command.ExecuteScalarAsync(token));
        }
        private SqliteConnection ReadOnlyConnection() => new(new SqliteConnectionStringBuilder
        { DataSource = Db.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        internal void RequireNoCloudWrites() => Require(transport.Calls == 0 &&
                Remote.UploadCount == 0 && Remote.ArchiveCount == 0 && Remote.DeleteCount == 0 &&
                Remote.VisibleCopyCount == 0 && Remote.MoveCount == 0 && Remote.DirectoryCreateCount == 0,
            "M4 native fixture reaches no HTTP/credential boundary or cloud upload/delete/archive/copy/move effect");
        public void Dispose()
        {
            Host?.Dispose(); http?.Dispose(); instance?.Dispose(); transport.Dispose();
            if (preimage is null) return;
            using var local = PilotDirectoryLease.Open(LocalRoot, preimage.LocalIdentity);
            using var parent = PilotDirectoryLease.Open(Contained(LocalRoot, Parent), preimage.ParentIdentity);
            local.VerifyBinding(); parent.VerifyBinding();
            Restore(parent.Path, preimage.ParentDacl); Restore(local.Path, preimage.LocalDacl);
            // Preserve fixture files/DB/sidecars and raw failures. No test cleanup substitutes for recovery.
        }
        private static void Restore(string path, string dacl)
        {
            var security = new DirectorySecurity(); security.SetSecurityDescriptorSddlForm(dacl, AccessControlSections.Access);
            new DirectoryInfo(path).SetAccessControl(security);
        }
    }

    private static DirectorySecurity ParentSecurity(bool distinctive)
    {
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentSid(), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (distinctive) security.AddAccessRule(new FileSystemAccessRule(World(), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
    private static void AssertInherited(string path, string detail)
    {
        Require(HasInheritedWorldRead(path), detail + " inherits the actual destination parent's distinctive World Read ACE, not the private-stage DACL");
        using var file = SyncLocalFileAuthority.OpenExistingExclusive(path); file.VerifySingleLink();
    }
    private static bool HasInheritedWorldRead(string path)
    {
        FileSystemSecurity security = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        return security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule =>
            rule.IsInherited && rule.IdentityReference.Equals(World()) && rule.AccessControlType == AccessControlType.Allow &&
            (rule.FileSystemRights & FileSystemRights.ReadData) != 0);
    }
    private static SecurityIdentifier CurrentSid()
    { using var identity = WindowsIdentity.GetCurrent(); return identity.User ?? throw new IOException("The fixture identity has no SID."); }
    private static SecurityIdentifier World() => new(WellKnownSidType.WorldSid, null);
    private static string Identity(string path)
    { using var file = SyncLocalFileAuthority.OpenExistingExclusive(path); var id = file.VerifySingleLink(); return $"{id.VolumeSerialNumber:x8}:{id.FileId:x16}"; }
    private static OwnedFile FileStamp(string path) => new(Path.GetFullPath(path), Identity(path),
        new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
        new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    private static BinaryStamp Binary(string path) => new(Path.GetFullPath(path), new FileInfo(path).Length,
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
    private static string Contained(string root, string relative)
    {
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var exact = Path.GetFullPath(Path.Combine(parent, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!exact.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Native fixture path escaped its exact owned allocation.");
        return exact;
    }
    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json));
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await file.WriteAsync(bytes, token); await file.FlushAsync(token); file.Flush(true);
    }
    private static void ValidateMode(string mode)
    { if (mode is not ("create" or "update")) throw new ArgumentException("An authority create/update case is required.", nameof(mode)); }
    private static SyncAuthorityOperationKind Kind(string mode) => mode == "create" ? SyncAuthorityOperationKind.CreateLocal : SyncAuthorityOperationKind.UpdateLocal;
    private static string Phase(string mode) => mode == "create" ? "target-promoted-before-receipt" : "local-update-stage-promoted-before-receipt";
    private static string Effect(string mode) => mode == "create" ? "local-create-from-dropbox-revision" : "local-promote-stage";
    private static void RequireWindows()
    { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("M4 needs real Windows/NTFS; unsupported is not PASS."); }
    private static void Require(bool condition, string detail)
    { if (!condition) throw new InvalidDataException(detail); Console.WriteLine("PASS " + detail); }
    private sealed record OwnedFile(string Path, string Identity, long Length, string Sha256, string Dacl);
    private sealed record BinaryStamp(string Path, long Length, string Sha256);
    private sealed record DaclPreimage(PilotDirectoryIdentity LocalIdentity, PilotDirectoryIdentity ParentIdentity, string LocalDacl, string ParentDacl);
    private sealed record Boundary(string Mode, string Phase, string Root, string DatabasePath, string PairId,
        SyncAuthorityOperationSnapshot Operation, string AttemptId, string StagePath, string SidecarPath, string TargetPath,
        PilotRemoteEntry Remote, int Downloads, int ProcessId, BinaryStamp Executable, BinaryStamp TestBinary, BinaryStamp CoreBinary);
    private sealed class DenyNetwork : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Native interruption fixture denies every HTTP request."); }
    }
    private sealed class DenyCredentials : IDropboxRefreshTokenStore, IGoogleDriveRefreshTokenStore
    {
        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding selected) => this;
        public Task<string?> ReadAsync(CancellationToken token = default) => Task.FromResult<string?>("native-fixture-synthetic-input");
        public Task<string?> ReadAsync(string id, CancellationToken token = default) => throw new InvalidOperationException("Native fixture denies Google credential access.");
        public Task WriteAsync(string value, CancellationToken token = default) => Refuse();
        public Task WriteAsync(string id, string value, CancellationToken token = default) => Refuse();
        public Task DeleteAsync(CancellationToken token = default) => Refuse();
        public Task DeleteAsync(string id, CancellationToken token = default) => Refuse();
        private static Task Refuse() => throw new InvalidOperationException("Native fixture denies protected credential mutation.");
    }
}
