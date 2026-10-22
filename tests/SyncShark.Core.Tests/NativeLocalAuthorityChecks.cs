using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// Original M19/H4/M4. This file supplies bounded INPUT, not replacement decisions.
// Root wires/runs the selectors. No CFAPI, live transport or product state is used.
[SupportedOSPlatform("windows")]
internal static class NativeLocalAuthorityChecks
{
    private const string Destination = "destination";
    private const string Relative = Destination + "/native-proof.txt";
    private const string Before = "original local-NTFS predecessor\n";
    private const string After = "exact local-NTFS successor\n";
    private const int CrashExit = 73;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Original native proofs require Windows/NTFS; no substituted PASS.");
        var exactRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(exactRoot);
        using (var rootLease = PilotDirectoryLease.Open(exactRoot)) rootLease.VerifyBinding();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var cases = new List<object>();
        var failures = new List<string>();
        await Case("M19 held one-character RenameTo and occupied target", () => OneCharacterAsync(Fresh("rename")));
        await Case("M19 public Host one-character create/update/readback", () => HostOneCharacterAsync(Fresh("host-character"), deadline.Token));
        await Case("M4 public Host create/update/manual final-parent DACL", () => HostInheritanceAsync(Fresh("host-dacl"), deadline.Token));
        await Case("H4 public Host actual ancestor junction refusal", () => HostAncestorAsync(Fresh("host-ancestor"), deadline.Token));
        await Case("M4 public legacy create/update final-parent DACL", () => LegacyInheritanceAsync(Fresh("legacy-dacl"), deadline.Token));
        await Case("M4 real issued legacy create sidecar after process interruption", () => InterruptedLegacyAsync(Fresh("legacy-interruption"), deadline.Token));
        await File.WriteAllTextAsync(Path.Combine(exactRoot, "native-local-authority-result.json"),
            JsonSerializer.Serialize(new { originals = new[] { "M19", "H4", "M4" }, cases, failures,
                binaries = new[] { Binary(typeof(NativeLocalAuthorityChecks).Assembly.Location),
                    Binary(typeof(SyncAuthorityRuntimeHost).Assembly.Location) },
                open = new[] { "Authority update/manual-restore interruption is not proved by the legacy create crash.",
                    "CFAPI activation and a second physical volume are excluded, not proved." } }, Json), CancellationToken.None);
        if (failures.Count != 0)
            throw new InvalidDataException("Original native proof failures retained: " + string.Join("; ", failures));

        string Fresh(string name)
        {
            var path = Contained(exactRoot, name);
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException("Native proof requires a fresh, caller-owned case path: " + path);
            return path;
        }
        async Task Case(string name, Func<Task> action)
        {
            try { await action(); cases.Add(new { name, status = "passed" }); }
            catch (Exception error)
            {
                // Preserve failures and continue genuinely independent original cases.
                failures.Add(name + ": " + error.Message);
                cases.Add(new { name, status = "failed", error = error.ToString() });
                Console.WriteLine("FAIL " + name + " " + error);
            }
        }
    }

    private static async Task OneCharacterAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var destination = PilotDirectoryLease.Open(root);
        var stagePath = Contained(root, "owned-stage.tmp");
        PilotDirectoryIdentity promoted;
        await using (var stage = SyncLocalFileAuthority.CreateNewMovableFile(stagePath))
        {
            await stage.CreateGuardedWriteStream().WriteAsync(Encoding.UTF8.GetBytes(After));
            await stage.Stream.FlushAsync(); stage.Stream.Flush(true);
            promoted = stage.VerifySingleLink();
            stage.RenameTo(destination, "a");
            Require(stage.VerifySingleLink() == promoted && stage.FinalPath == Contained(root, "a"),
                "M19 real held RenameTo reaches the exact one-character path with the same NTFS identity");
        }
        using (var reopened = SyncLocalFileAuthority.OpenExistingExclusive(Contained(root, "a")))
            Require(reopened.VerifySingleLink() == promoted,
                "M19 one-character promotion survives disposal/reopen with the exact identity");
        Require(await File.ReadAllTextAsync(Contained(root, "a")) == After,
            "M19 one-character promotion has exact byte readback after the exclusive handle is released");

        var occupied = Contained(root, "b");
        await File.WriteAllTextAsync(occupied, Before);
        var occupiedIdentity = Identity(occupied);
        var refusedStage = Contained(root, "occupied-stage.tmp");
        PilotDirectoryIdentity sourceIdentity;
        await using (var stage = SyncLocalFileAuthority.CreateNewMovableFile(refusedStage))
        {
            await stage.CreateGuardedWriteStream().WriteAsync(Encoding.UTF8.GetBytes(After));
            await stage.Stream.FlushAsync(); stage.Stream.Flush(true);
            sourceIdentity = stage.VerifySingleLink();
            Exception? refusal = null;
            try { stage.RenameTo(destination, "b"); }
            catch (IOException error) { refusal = error; }
            Require(refusal?.InnerException is Win32Exception { NativeErrorCode: 80 or 183 } &&
                    stage.VerifySingleLink() == sourceIdentity && stage.FinalPath == refusedStage,
                "M19 occupied one-character target refuses without moving/substituting the held source");
        }
        Require(Identity(occupied) == occupiedIdentity && await File.ReadAllTextAsync(occupied) == Before &&
                Identity(refusedStage) == sourceIdentity && await File.ReadAllTextAsync(refusedStage) == After,
            "M19 occupied refusal preserves both exact identities and byte contents after reopen");
    }

    private static async Task HostOneCharacterAsync(string root, CancellationToken token)
    {
        using var fixture = await Fixture.CreateAsync(root, token);
        await fixture.RunHostAsync(token); // Real empty baseline before the new remote input.
        fixture.Remote.Put("a", Before);
        var created = await fixture.RunHostAsync(token);
        Require(created.Downloaded == 1 && await File.ReadAllTextAsync(fixture.Target("a"), token) == Before,
            "M19 public Host planner/runner creates and reads back an actual one-character native target");
        var original = Identity(fixture.Target("a"));
        fixture.Remote.Put("a", After);
        var updated = await fixture.RunHostAsync(token);
        Require(updated.Downloaded == 1 && Identity(fixture.Target("a")) != original &&
                await File.ReadAllTextAsync(fixture.Target("a"), token) == After,
            "M19 public Host update promotes a new exact native object to the one-character target");
        var artifact = (await fixture.Db.LoadRecoveryArtifactsAsync(token)).Single(item => item.Side == "local");
        Require(await File.ReadAllTextAsync(ArchivePath(artifact.IdentityJson), token) == Before,
            "M19 public update retains the actual one-character predecessor bytes");
        var downloads = fixture.Remote.DownloadCount;
        await fixture.ReopenHostAsync(token);
        await fixture.RunHostAsync(token);
        Require(fixture.Remote.DownloadCount == downloads && await File.ReadAllTextAsync(fixture.Target("a"), token) == After,
            "M19 reopened public Host does not repeat the completed one-character transfer");
    }

    private static async Task HostInheritanceAsync(string root, CancellationToken token)
    {
        using var fixture = await Fixture.CreateAsync(root, token);
        var inheritanceFailures = new List<string>();
        await fixture.RunHostAsync(token);
        fixture.Remote.Put(Relative, Before);
        Require((await fixture.RunHostAsync(token)).Downloaded == 1,
            "M4 create is emitted by the actual public Host, not an inserted operation");
        CheckInheritance(fixture.Target(Relative), "M4 public authority create");
        fixture.Remote.Put(Relative, After);
        Require((await fixture.RunHostAsync(token)).Downloaded == 1,
            "M4 update is emitted from the actual accepted baseline and changed remote revision");
        CheckInheritance(fixture.Target(Relative), "M4 public authority update");
        var artifact = (await fixture.Db.LoadRecoveryArtifactsAsync(token)).Single(item => item.Side == "local");
        Require(await File.ReadAllTextAsync(ArchivePath(artifact.IdentityJson), token) == Before,
            "M4 manual restore input is the actual update's retained predecessor, not a seeded completed artifact");
        var selected = (await fixture.Host.LoadRecoveryCatalogAsync(fixture.Pair.Id, cancellationToken: token))
            .Single(item => item.Key == artifact.RecoveryArtifactId);
        await fixture.Host.RestoreRecoveryAsync(selected, restoreAsCopy: false, token);
        Require(await File.ReadAllTextAsync(fixture.Target(Relative), token) == Before,
            "M4 actual public manual restore reads back the exact retained version");
        CheckInheritance(fixture.Target(Relative), "M4 public manual restore");
        await fixture.ReopenHostAsync(token);
        CheckInheritance(fixture.Target(Relative), "M4 reopened manual-restore destination");
        fixture.RequireNoExternalEffects();
        if (inheritanceFailures.Count != 0) throw new InvalidDataException(string.Join("; ", inheritanceFailures));
        void CheckInheritance(string path, string label)
        {
            try { AssertInherited(path, label); }
            catch (InvalidDataException error)
            { inheritanceFailures.Add(error.Message); Console.WriteLine("FAIL " + error.Message); }
        }
    }

    private static async Task HostAncestorAsync(string root, CancellationToken token)
    {
        using var fixture = await Fixture.CreateAsync(root, token);
        await fixture.RunHostAsync(token);
        fixture.Remote.Put(Relative, Before);
        await fixture.RunHostAsync(token);
        fixture.Remote.Put(Relative, After);
        await fixture.RunHostAsync(token);
        var artifact = (await fixture.Db.LoadRecoveryArtifactsAsync(token)).Single(item => item.Side == "local");
        var archivePath = ArchivePath(artifact.IdentityJson);
        var archiveIdentity = Identity(archivePath);
        var selected = (await fixture.Host.LoadRecoveryCatalogAsync(fixture.Pair.Id, cancellationToken: token))
            .Single(item => item.Key == artifact.RecoveryArtifactId);
        // Legitimate success is the SAME public entry, with the real archive, before redirecting any ancestor.
        await fixture.Host.RestoreRecoveryAsync(selected, restoreAsCopy: false, token);
        Require(await File.ReadAllTextAsync(fixture.Target(Relative), token) == Before,
            "H4 public restore succeeds at an ordinary contained native destination");
        var records = (await fixture.Db.LoadRecoveryArtifactsAsync(token)).Select(item =>
            (item.RecoveryArtifactId, item.IdentityJson, item.ContentHash, item.Size)).ToArray();
        var controlFiles = Inventory(fixture.ControlRoot);
        var source = Contained(fixture.LocalRoot, Destination);
        var outside = Contained(root, "fixture-owned-outside-root");
        PilotDirectoryIdentity ancestorIdentity;
        using (var parent = PilotDirectoryLease.Open(source))
        { parent.VerifyBinding(); ancestorIdentity = parent.Identity; }
        Directory.Move(source, outside);
        using (var moved = PilotDirectoryLease.Open(outside))
            Require(moved.Identity == ancestorIdentity, "H4 redirect input preserves the actual moved ancestor identity");
        var outsideTarget = Contained(outside, "native-proof.txt");
        var outsideIdentity = Identity(outsideTarget);
        var outsideFiles = Inventory(outside);
        var junctionCreated = false;
        try
        {
            CreateJunction(source, outside);
            junctionCreated = true;
            Require((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0,
                "H4 adversarial input is an actual ancestor junction to an owned outside-root subtree");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (attempt != 0) await fixture.ReopenHostAsync(token);
                Exception? refused = null;
                try { await fixture.Host.RestoreRecoveryAsync(selected, restoreAsCopy: false, token); }
                catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
                { refused = error; }
                Require(refused is not null, "H4 actual public restore rejects ancestor redirection, including reopen/repeat");
                Require(Identity(outsideTarget) == outsideIdentity && await File.ReadAllTextAsync(outsideTarget, token) == Before &&
                        Inventory(outside).SequenceEqual(outsideFiles),
                    "H4 junction refusal leaves outside-root identities, bytes and member set unchanged");
                Require(Identity(archivePath) == archiveIdentity && await File.ReadAllTextAsync(archivePath, token) == Before &&
                        (await fixture.Db.LoadRecoveryArtifactsAsync(token)).Select(item =>
                            (item.RecoveryArtifactId, item.IdentityJson, item.ContentHash, item.Size)).SequenceEqual(records),
                    "H4 junction refusal neither consumes nor invents retained recovery evidence");
                Require(Inventory(fixture.ControlRoot).SequenceEqual(controlFiles),
                    "H4 junction refusal leaves no newly retained staging/sidecar byte effects in the owned control area");
            }
        }
        finally
        {
            // Only the junction created above is removed, non-recursively; never traverse its target.
            if (junctionCreated)
            {
                var target = new DirectoryInfo(source).LinkTarget;
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) == 0 || target is null ||
                    !string.Equals(Path.GetFullPath(target), outside, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("H4 teardown refuses a substituted non-junction ancestor.");
                Directory.Delete(source, recursive: false);
            }
            if (!Directory.Exists(source) && Directory.Exists(outside))
            {
                using (var moved = PilotDirectoryLease.Open(outside))
                    if (moved.Identity != ancestorIdentity) throw new IOException("H4 teardown refuses a substituted outside subtree.");
                Directory.Move(outside, source);
            }
        }
        Require(!Directory.Exists(outside) && (File.GetAttributes(source) & FileAttributes.ReparsePoint) == 0,
            "H4 exact owned ancestor is restored and its junction is absent after teardown");
        fixture.RequireNoExternalEffects();
    }

    private static async Task LegacyInheritanceAsync(string root, CancellationToken token)
    {
        using var fixture = await Fixture.CreateAsync(root, token);
        var inheritanceFailures = new List<string>();
        await fixture.EstablishLegacyAsync(token);
        fixture.Remote.Put(Relative, Before);
        Require((await fixture.Legacy().RunAsync(token)).Downloaded == 1,
            "M4 public legacy engine creates from a real new remote input after an accepted baseline");
        CheckInheritance(fixture.Target(Relative), "M4 public legacy create");
        fixture.Remote.Put(Relative, After);
        Require((await fixture.Legacy().RunAsync(token)).Downloaded == 1,
            "M4 public legacy engine replaces from the real changed remote revision");
        CheckInheritance(fixture.Target(Relative), "M4 public legacy update");
        Require(await File.ReadAllTextAsync(fixture.Target(Relative), token) == After &&
                fixture.Runtime.LoadPair(fixture.Pair.Id).PendingTransfer is null,
            "M4 completed legacy replacement has exact bytes and no fabricated pending completion");
        var downloads = fixture.Remote.DownloadCount;
        await fixture.ReopenHostAsync(token);
        await fixture.Legacy().RunAsync(token);
        Require(fixture.Remote.DownloadCount == downloads,
            "M4 reopened legacy engine does not redownload an already completed replacement");
        CheckInheritance(fixture.Target(Relative), "M4 reopened legacy target");
        fixture.RequireNoExternalEffects();
        if (inheritanceFailures.Count != 0) throw new InvalidDataException(string.Join("; ", inheritanceFailures));
        void CheckInheritance(string path, string label)
        {
            try { AssertInherited(path, label); }
            catch (InvalidDataException error)
            { inheritanceFailures.Add(error.Message); Console.WriteLine("FAIL " + error.Message); }
        }
    }

    // Root also wires --native-local-authority-crash <fresh owned case root>.
    // This is an intentionally abrupt EXIT of this exact child process after the
    // real legacy intent is persisted and promoted. Throwing would Dispose the
    // handle and erase the interrupted-access boundary, so it is not a substitute.
    internal static async Task CrashLegacyCreateAsync(string root)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var fixture = await Fixture.CreateAsync(root, deadline.Token);
        await fixture.EstablishLegacyAsync(deadline.Token);
        fixture.Remote.Put(Relative, After);
        Require(SyncLocalTransfer.TargetPromotedForTest is null,
            "M4 controlled crash does not replace an existing hook");
        SyncLocalTransfer.TargetPromotedForTest = promotedPath =>
        {
            var pending = fixture.Runtime.LoadPair(fixture.Pair.Id).PendingTransfer;
            Require(pending is { StageArtifactId: not null, StageFileName: not null } &&
                    pending.RelativePath == Relative && promotedPath == fixture.Target(Relative) &&
                    File.Exists(fixture.Target(Relative)),
                "M4 interruption follows an actual issued/persisted create intent and native promotion");
            using var local = PilotDirectoryLease.Open(fixture.LocalRoot);
            using var control = SyncControlNamespace.OpenLocal(fixture.Pair.ControlNamespace!, local);
            var sidecar = SyncLocalFileAuthority.RecoverySidecarPath(Path.Combine(
                SyncLocalTransactionDirectory.PathFor(control, pending!), pending!.StageFileName!));
            Require(File.Exists(sidecar), "M4 real protected-access sidecar exists before abrupt exit");
            File.WriteAllText(Contained(root, "interrupted-boundary.json"), JsonSerializer.Serialize(new
                { entrypoint = "BidirectionalSyncEngine.RunAsync", phase = "SyncLocalTransfer.TargetPromotedForTest",
                    pending, sidecar, processId = Environment.ProcessId,
                    binary = Binary(typeof(NativeLocalAuthorityChecks).Assembly.Location) }, Json));
            Console.Out.Flush(); Console.Error.Flush();
            Environment.Exit(CrashExit);
        };
        try { await fixture.Legacy().RunAsync(deadline.Token); }
        finally { SyncLocalTransfer.TargetPromotedForTest = null; }
        throw new InvalidDataException("M4 public create did not reach the exact controlled interruption boundary.");
    }

    private static async Task InterruptedLegacyAsync(string root, CancellationToken token)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidDataException("Current test executable identity is absent.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(NativeLocalAuthorityChecks).Assembly.Location);
        start.ArgumentList.Add("--native-local-authority-crash"); start.ArgumentList.Add(Path.GetFullPath(root));
        using var process = Process.Start(start) ?? throw new IOException("Owned native crash child did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(60), token); }
        catch
        {
            // Bound to the one process started here; this is never foreign-process cleanup.
            if (!process.HasExited) { process.Kill(entireProcessTree: false); await process.WaitForExitAsync(); }
            throw;
        }
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Contained(root, "interrupted-child.stdout.log"), await stdout, CancellationToken.None);
        await File.WriteAllTextAsync(Contained(root, "interrupted-child.stderr.log"), await stderr, CancellationToken.None);
        Require(process.ExitCode == CrashExit && File.Exists(Contained(root, "interrupted-boundary.json")),
            "M4 exact child exited only after the actual persisted/promoted boundary; raw failures remain");
        using var fixture = await Fixture.OpenAsync(root, token);
        fixture.Remote.Put(Relative, After); // SAME synthetic id/revision/bytes as the exited child.
        var pending = fixture.Runtime.LoadPair(fixture.Pair.Id).PendingTransfer;
        Require(pending is { StageArtifactId: not null, StageFileName: not null } && pending.RelativePath == Relative,
            "M4 reopened real product state retains the issued intent; the test inserts no intent or success record");
        using var local = PilotDirectoryLease.Open(fixture.LocalRoot);
        using var control = SyncControlNamespace.OpenLocal(fixture.Pair.ControlNamespace!, local);
        var sidecar = SyncLocalFileAuthority.RecoverySidecarPath(Path.Combine(
            SyncLocalTransactionDirectory.PathFor(control, pending!), pending!.StageFileName!));
        Require(File.Exists(sidecar), "M4 interrupted native sidecar survived process exit and state reopen");
        var recovered = await fixture.Legacy().RunAsync(token);
        Require(fixture.Runtime.LoadPair(fixture.Pair.Id).PendingTransfer is null && !File.Exists(sidecar) &&
                Format(Identity(fixture.Target(Relative))) == pending.StageArtifactId &&
                await File.ReadAllTextAsync(fixture.Target(Relative), token) == After &&
                fixture.Remote.DownloadCount == 0 && recovered.Decisions.Any(item => item.Kind == SyncDecisionKind.Recovered),
            "M4 actual public engine restores access from the real sidecar/intent, reconciles exact identity/bytes and does not redownload");
        AssertInherited(fixture.Target(Relative), "M4 reconciled interrupted create final-parent DACL");
        await fixture.ReopenHostAsync(token);
        await fixture.Legacy().RunAsync(token);
        Require(fixture.Remote.DownloadCount == 0 && fixture.Runtime.LoadPair(fixture.Pair.Id).PendingTransfer is null &&
                !File.Exists(sidecar) && Format(Identity(fixture.Target(Relative))) == pending.StageArtifactId,
            "M4 second reopen/repeat cannot replay the already reconciled interrupted create");
        fixture.RequireNoExternalEffects();
    }

    private sealed class Fixture : IDisposable
    {
        private PilotInstanceLock instance = null!;
        private readonly DenyNetwork transport = new();
        private HttpClient http = null!;
        private readonly string root;
        internal SyncPair Pair = null!;
        internal SyncAuthorityRuntimeHost Host = null!;
        internal SyncAuthorityDatabase Db = null!;
        internal SyncRuntimeStateStore Runtime = null!;
        internal readonly FakeSyncRemoteStore Remote = new() { ExactPathMetadataForTest = true };
        internal string LocalRoot => Contained(root, "local");
        internal string ControlRoot => Pair.ControlNamespace!.Local.RootPath;
        private static readonly DropboxConnectionBinding Connection = new DropboxConnectionBinding(
            DropboxProviderConfiguration.RegisteredClientId, "native-fixture-account", "native-fixture-root", "native-fixture-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes).Normalize();
        private Fixture(string root) => this.root = Path.GetFullPath(root);

        internal static async Task<Fixture> CreateAsync(string root, CancellationToken token)
        {
            var f = new Fixture(root);
            if (Directory.Exists(f.root) || File.Exists(f.root)) throw new IOException("Fixture root must be fresh.");
            Directory.CreateDirectory(f.LocalRoot);
            Directory.CreateDirectory(Contained(f.LocalRoot, Destination));
            using (var lease = PilotDirectoryLease.Open(f.LocalRoot)) lease.VerifyBinding();
            var localRoot = new DirectoryInfo(f.LocalRoot);
            await File.WriteAllTextAsync(Contained(f.root, "original-local-dacl.txt"),
                localRoot.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access), token);
            var privateRootSecurity = new DirectorySecurity();
            privateRootSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            privateRootSecurity.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            localRoot.SetAccessControl(privateRootSecurity);
            var parent = new DirectoryInfo(Contained(f.LocalRoot, Destination));
            // Capture/restore only the one freshly created fixture parent. A distinctive inherited World
            // Read ACE differentiates destination inheritance from a private stage's saved owner-only DACL.
            await File.WriteAllTextAsync(Contained(f.root, "original-parent-dacl.txt"),
                parent.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access), token);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(World(), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            parent.SetAccessControl(security);
            try
            {
                f.instance = PilotInstanceLock.Acquire(Contained(f.root, "state/instance.lock"));
                var store = new SyncPairStore(f.instance.StateAuthority);
                var installation = await store.GetOrCreateInstallationIdAsync(token);
                const string pairId = "61700000000000000000000000000194";
                using var lease = PilotDirectoryLease.Open(f.LocalRoot);
                var localControl = SyncControlNamespace.BindLocalForTest(lease, installation, pairId);
                var remotePath = SyncControlNamespace.RemoteContainerPath + "/" + pairId;
                var remoteControl = new SyncRemoteControlNamespaceBinding(remotePath, "id:native-control",
                    remotePath + "/.syncshark-owner.json", "id:native-marker", "rev:marker-1",
                    DropboxContentHash.Compute(SyncControlNamespace.MarkerBytes(installation, pairId)));
                f.Pair = new SyncPair(pairId, "Original native local proof", f.LocalRoot,
                    lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/NativeProof", "id:native-root",
                    ConnectionId: Connection.ConnectionId, InitialReviewApproved: true, FilesOnDemandEnabled: false,
                    ControlNamespace: new SyncControlNamespaceBinding(installation, pairId, localControl, remoteControl),
                    RecoveryRoot: new SyncRemoteRecoveryRootBinding(SyncDropboxArtifactPaths.RecoveryContainerPath,
                        "id:native-recovery", Connection.RootNamespaceId));
                await store.SaveAsync(new SyncPairSet([f.Pair], f.Pair.Id), token);
                await new DropboxConnectionStore(f.instance.StateAuthority).SaveNewAsync(Connection, token);
                await new SyncAutomationStore(f.instance.StateAuthority).SaveAsync(new SyncAutomationSettings(false, 15, false), token);
                f.Remote.PutDirectory(Destination);
                await f.InitializeHostAsync(token, reopening: false);
                await f.Db.RetainPairDefinitionAsync(f.Pair, true, token);
                Require(!InheritedWorldRead(Contained(f.LocalRoot, Destination)) &&
                        !new DirectoryInfo(f.ControlRoot).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                            .Cast<FileSystemAccessRule>().Any(rule => rule.IdentityReference.Equals(World()) && rule.AccessControlType == AccessControlType.Allow),
                    "M4 destination ACE is explicit on the fixture parent and absent from private control-stage inheritance");
                return f;
            }
            catch { f.Dispose(); throw; }
        }

        internal static async Task<Fixture> OpenAsync(string root, CancellationToken token)
        {
            var f = new Fixture(root);
            using (var lease = PilotDirectoryLease.Open(f.LocalRoot)) lease.VerifyBinding();
            f.instance = PilotInstanceLock.Acquire(Contained(f.root, "state/instance.lock"));
            try
            {
                f.Pair = new SyncPairStore(f.instance.StateAuthority).Load().Pairs.Single();
                Require(f.Pair.LocalRoot == f.LocalRoot && f.Pair.ControlNamespace is not null,
                    "M4 reopen uses the retained exact local/control binding, not a replacement definition");
                f.Remote.PutDirectory(Destination);
                await f.InitializeHostAsync(token, reopening: true);
                return f;
            }
            catch { f.Dispose(); throw; }
        }

        private async Task InitializeHostAsync(CancellationToken token, bool reopening)
        {
            http = new HttpClient(transport, disposeHandler: false);
            var stores = new SyncPairStore(instance.StateAuthority);
            Runtime = new SyncRuntimeStateStore(instance.StateAuthority);
            Db = new SyncAuthorityDatabase(Contained(root, "authority/syncshark.db"));
            Host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http, stores, Runtime,
                new DropboxConnectionStore(instance.StateAuthority), new DenyCredentials(), Db,
                (_, _) => Remote, googleDriveCredentialStore: new DenyCredentials());
            await Host.LoadPairsAsync(token);
            if (reopening)
                Require(Host.LoadTransferSettings().ResourceMode == "Performance",
                    "M4 cold reopen preserves the existing transfer settings without rewriting an interrupted run");
            else
                await Host.SaveTransferSettingsAsync(Host.LoadTransferSettings() with { ResourceMode = "Performance" }, token);
        }
        internal async Task ReopenHostAsync(CancellationToken token)
        {
            Host.Dispose(); http.Dispose(); instance.Dispose();
            instance = PilotInstanceLock.Acquire(Contained(root, "state/instance.lock"));
            await InitializeHostAsync(token, reopening: true);
            Pair = (await Host.LoadPairsAsync(token)).Pairs.Single();
        }
        internal string Target(string path) => Contained(LocalRoot, path);
        internal BidirectionalSyncEngine Legacy() => new(Pair, Remote, Runtime, new PilotRunControl());
        internal async Task EstablishLegacyAsync(CancellationToken token)
        {
            await Legacy().RunAsync(token); await Legacy().RunAsync(token);
            Require(Runtime.LoadPair(Pair.Id).BaselineEstablished && Runtime.LoadPair(Pair.Id).PendingTransfer is null,
                "M4 actual public legacy engine establishes the initial fixture baseline");
        }
        internal async Task<SyncRunResult> RunHostAsync(CancellationToken token)
        {
            var batch = await Host.RunPairsAsync([Pair], "Manual", cancellationToken: token);
            Console.WriteLine(JsonSerializer.Serialize(new { entrypoint = "Host.RunPairsAsync", batch }));
            var item = batch.Items.Single();
            Require(!batch.Stopped && item.FailureType is null && item.Result is not null &&
                    item.Result.Decisions.All(decision => decision.Kind is not (SyncDecisionKind.Blocked or SyncDecisionKind.Conflict)),
                "native public Host completes the ordinary fixture run without admission/capability substitution");
            return item.Result!;
        }
        internal void RequireNoExternalEffects() => Require(transport.Calls == 0 &&
                Remote.UploadCount == 0 && Remote.ArchiveCount == 0 && Remote.DeleteCount == 0 && Remote.VisibleCopyCount == 0,
            "native local proofs invoke no HTTP/live credentials/cloud write/delete/archive effects");
        public void Dispose()
        {
            Host?.Dispose(); http?.Dispose(); instance?.Dispose(); transport.Dispose();
            var original = Contained(root, "original-parent-dacl.txt");
            var parent = Contained(LocalRoot, Destination);
            if (File.Exists(original) && Directory.Exists(parent))
            {
                using var lease = PilotDirectoryLease.Open(parent); lease.VerifyBinding();
                var restored = new DirectorySecurity();
                restored.SetSecurityDescriptorSddlForm(File.ReadAllText(original), AccessControlSections.Access);
                new DirectoryInfo(parent).SetAccessControl(restored);
            }
            var localOriginal = Contained(root, "original-local-dacl.txt");
            if (File.Exists(localOriginal) && Directory.Exists(LocalRoot))
            {
                using var lease = PilotDirectoryLease.Open(LocalRoot); lease.VerifyBinding();
                var restored = new DirectorySecurity();
                restored.SetSecurityDescriptorSddlForm(File.ReadAllText(localOriginal), AccessControlSections.Access);
                new DirectoryInfo(LocalRoot).SetAccessControl(restored);
            }
        }
    }

    private static void AssertInherited(string path, string label)
    {
        Require(InheritedWorldRead(path), label + " has the destination parent's genuinely inherited World Read ACE after handle disposal");
        var dacl = new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        using var reopened = SyncLocalFileAuthority.OpenExistingExclusive(path);
        reopened.VerifySingleLink();
        Console.WriteLine(JsonSerializer.Serialize(new { requirement = "M4", consumer = label,
            finalPath = reopened.FinalPath, identity = Format(reopened.ReadIdentity()),
            dacl }));
    }
    private static bool InheritedWorldRead(string path)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        return security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule =>
            rule.IsInherited && rule.IdentityReference.Equals(World()) && rule.AccessControlType == AccessControlType.Allow &&
            (rule.FileSystemRights & FileSystemRights.ReadData) != 0);
    }
    private static SecurityIdentifier World() => new(WellKnownSidType.WorldSid, null);
    private static string[] Inventory(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + "|" + Format(Identity(path)) + "|" +
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant())
        .OrderBy(item => item, StringComparer.Ordinal).ToArray();
    private static PilotDirectoryIdentity Identity(string path)
    { using var file = SyncLocalFileAuthority.OpenExistingExclusive(path); return file.VerifySingleLink(); }
    private static string Format(PilotDirectoryIdentity identity) => $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}";
    private static string ArchivePath(string json)
    { using var value = JsonDocument.Parse(json); return value.RootElement.GetProperty("path").GetString()!; }
    private static object Binary(string path) => new { path = Path.GetFullPath(path),
        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() };
    private static string Contained(string root, string relative)
    {
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var exact = Path.GetFullPath(Path.Combine(parent, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!exact.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native fixture path escaped its exact owned allocation.");
        return exact;
    }
    private static void CreateJunction(string path, string target)
    {
        if (File.Exists(path) || Directory.Exists(path) || !Directory.Exists(target))
            throw new IOException("Native junction inputs are not an absent source and owned retained target.");
        using (var targetLease = PilotDirectoryLease.Open(target)) targetLease.VerifyBinding();
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", Path.GetFullPath(path), Path.GetFullPath(target) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Owned junction creation did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        { process.Kill(entireProcessTree: false); throw new IOException("Owned junction creation exceeded its bound."); }
        if (process.ExitCode != 0) throw new IOException("Owned junction input failed: " +
            stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult());
    }
    private static void Require(bool value, string detail)
    { if (!value) throw new InvalidDataException(detail); Console.WriteLine("PASS " + detail); }
    private sealed class DenyNetwork : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Native fixture denies every HTTP request."); }
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
