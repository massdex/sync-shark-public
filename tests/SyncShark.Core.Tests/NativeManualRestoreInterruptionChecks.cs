using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Original M4 / D-F3 and D-F6's manual RenameTo-before-Dispose access window.
// Root owns Program wiring, serial builds and all execution. Fake remote bytes are
// inputs; the actual public Host produces the archive, command, stage and restore.
[SupportedOSPlatform("windows")]
internal static class NativeManualRestoreInterruptionChecks
{
    private const string Parent = "destination";
    private const string Relative = Parent + "/manual-interrupted.txt";
    private const string Before = "original retained manual restore version\n";
    private const string After = "later destination retained as manual restore preimage\n";
    private const string ChildSelector = "--native-manual-restore-interruption-crash";
    private const int CrashExit = 73;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // --native-manual-restore-interruption-checks <fresh owned fixture root>
    internal static async Task RunAsync(string root)
    {
        RequireWindows();
        var exactRoot = Path.GetFullPath(root);
        if (File.Exists(exactRoot) || Directory.Exists(exactRoot) &&
            Directory.EnumerateFileSystemEntries(exactRoot).Any())
            throw new IOException("The manual interruption suite requires a fresh empty fixture allocation.");
        Directory.CreateDirectory(exactRoot);
        using var rootLease = PilotDirectoryLease.Open(exactRoot);
        var failures = new List<string>();
        var results = new List<object>();
        var cases = new (string Id, Func<string, CancellationToken, Task> Run)[]
        {
            ("M4-manual-normal", NormalAsync),
            ("M4-manual-before-promotion", (path, token) => InterruptedAsync(path, "before-promotion", "repair", token)),
            ("M4-manual-after-promotion", (path, token) => InterruptedAsync(path, "after-promotion", "repair", token)),
            ("M4-manual-same-bytes-foreign-native-id", (path, token) => InterruptedAsync(path, "after-promotion", "foreign", token)),
            ("M4-manual-swapped-valid-envelope", SwappedValidEnvelopeAsync),
            ("M4-manual-missing-envelope", (path, token) => InterruptedAsync(path, "after-promotion", "missing-envelope", token)),
            ("M4-manual-failure-preserves-checkpoint", FailureCheckpointAsync),
            ("M4-manual-legacy-no-original-binding", LegacyAsync)
        };
        foreach (var item in cases)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            try
            {
                await item.Run(Contained(exactRoot, item.Id), deadline.Token);
                results.Add(new { caseId = item.Id, passed = true });
            }
            catch (Exception error)
            {
                failures.Add(item.Id + ": " + error.GetType().Name + ": " + error.Message);
                results.Add(new { caseId = item.Id, passed = false, error = error.GetType().Name, detail = error.Message });
            }
            rootLease.VerifyBinding();
        }
        await WriteJsonAsync(Contained(exactRoot, "manual-interruption-result.json"), new
        {
            original = new[] { "M4", "D-F3", "D-F6 manual access interruption only" },
            restore = "SyncAuthorityRuntimeHost.RestoreRecoveryAsync",
            verification = "SyncAuthorityRuntimeHost.VerifyRunOutcomesAsync",
            projection = "SyncAuthorityRuntimeHost.LoadRunRetryAsync",
            cases = results, failures,
            testBinary = Binary(typeof(NativeManualRestoreInterruptionChecks).Assembly.Location),
            coreBinary = Binary(typeof(SyncAuthorityRuntimeHost).Assembly.Location),
            qualifications = new[]
            {
                "Access repair is never reported as restore completion; all interrupted commands stay blocked.",
                "Legacy case is a real public persisted legacy-shaped command with no checkpoint, not proof that an interrupted legacy file can be repaired.",
                "Directory, provider revision, typed cloud restore and D-F6 delete-pending orphan cleanup are outside this regular-file proof."
            }
        }, CancellationToken.None);
        if (failures.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, failures));
    }

    // --native-manual-restore-interruption-crash <fresh case root> <before-promotion|after-promotion>
    internal static async Task CrashAsync(string root, string phase)
    {
        RequireWindows(); ValidatePhase(phase);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var fixture = await Fixture.CreateAsync(root, deadline.Token);
        var selected = await fixture.ProduceRestoreInputAsync(deadline.Token);
        var selectedArtifact = (await fixture.Db.LoadRecoveryArtifactsAsync(deadline.Token))
            .Single(item => item.RecoveryArtifactId == selected.Key);
        var sourceBefore = FileStamp(ArchivePath(selectedArtifact.IdentityJson));
        Require(fixture.Host.ManualLocalRestorePhaseForTest is null, "M4 manual fixture owns no other phase hook");
        fixture.Host.ManualLocalRestorePhaseForTest = async (observed, commandId) =>
        {
            if (observed != phase) return;
            var command = await fixture.Db.LoadManualLocalRestoreCommandAsync(commandId, deadline.Token)
                ?? throw new InvalidDataException("The real manual restore command disappeared.");
            var checkpoint = Checkpoint(command);
            Require(command.Status == "executing" && checkpoint.CommandId == commandId &&
                    checkpoint.PairId == fixture.Pair.Id && checkpoint.RecoveryArtifactId == selected.Key &&
                    checkpoint.Phase == "promotion_pending" && checkpoint.StageIdentity is not null &&
                    checkpoint.RootIdentity == fixture.LocalIdentity &&
                    checkpoint.TargetPath == fixture.Target && checkpoint.TargetRelativePath == Relative &&
                    checkpoint.StagePath == Contained(fixture.ControlRoot, "restore-" + commandId[8..] + ".partial"),
                "M4 actual manual command durably binds its original source/root/control/stage before the interruption");
            var sidecar = SyncLocalFileAuthority.RecoverySidecarPath(checkpoint.StagePath);
            Require(File.Exists(sidecar) && (phase == "before-promotion"
                    ? File.Exists(checkpoint.StagePath) && !File.Exists(fixture.Target)
                    : !File.Exists(checkpoint.StagePath) && File.Exists(fixture.Target)),
                "M4 actual manual phase is on the intended side of RenameTo with pending access restoration");
            // The actual writer still owns its handles. Read its durable producer identities and retained
            // source/preimage, not a helper-created successful operation or fabricated receipt.
            // Restore still holds the archive exclusively. Its actually verified held native identity
            // in the checkpoint corroborates this pre-call stamp; do not manufacture a sharing error
            // by reopening that same source from the hook. The displaced preimage is already closed.
            var retained = await fixture.RetainedAsync(checkpoint, deadline.Token, sourceBefore);
            var counts = await fixture.CountsAsync(deadline.Token);
            fixture.RequireNoExternalEffects();
            await WriteJsonAsync(Contained(fixture.Root, "manual-interrupted-boundary.json"), new Boundary(
                phase, fixture.Root, fixture.Db.DatabasePath, fixture.Pair.Id, commandId, checkpoint,
                retained, counts, sidecar, fixture.Remote.DownloadCount, Environment.ProcessId,
                Binary(Environment.ProcessPath ?? throw new IOException("The child executable is absent.")),
                Binary(typeof(NativeManualRestoreInterruptionChecks).Assembly.Location),
                Binary(typeof(SyncAuthorityRuntimeHost).Assembly.Location)), deadline.Token);
            Console.Out.Flush(); Console.Error.Flush();
            Environment.Exit(CrashExit); // No unwinding/Dispose/receipt; throwing is NOT this witness.
        };
        try
        {
            await fixture.Host.RestoreRecoveryAsync(selected, restoreAsCopy: false, deadline.Token);
            throw new InvalidDataException("The exact manual interruption hook was not reached.");
        }
        finally { fixture.Host.ManualLocalRestorePhaseForTest = null; }
    }

    private static async Task NormalAsync(string root, CancellationToken token)
    {
        using var fixture = await Fixture.CreateAsync(root, token);
        var selected = await fixture.ProduceRestoreInputAsync(token);
        var before = await fixture.ManualCommandsAsync(token);
        await fixture.Host.RestoreRecoveryAsync(selected, restoreAsCopy: false, token);
        var commandId = (await fixture.ManualCommandsAsync(token)).Except(before, StringComparer.Ordinal).Single();
        var command = await fixture.Db.LoadManualLocalRestoreCommandAsync(commandId, token)
            ?? throw new InvalidDataException("The normal public restore command was not retained.");
        Require(command.Status == "completed" && await File.ReadAllTextAsync(fixture.Target, token) == Before,
            "M4 normal public manual restore completes with the exact actually retained original bytes");
        AssertInherited(fixture.Target, "M4 normal public manual target");
        var source = await fixture.Db.LoadRecoveryArtifactsAsync(token);
        var retained = source.Where(item => item.RecoveryArtifactId == selected.Key || SourceLink(item.IdentityJson) == selected.Key).ToArray();
        Require(retained.Length == 2 && retained.All(item => item.Status is "retained" or "verified") &&
                retained.Any(item => File.ReadAllText(ArchivePath(item.IdentityJson)) == Before) &&
                retained.Any(item => File.ReadAllText(ArchivePath(item.IdentityJson)) == After),
            "M4 normal control retains both actual source and displaced destination without inventing an archive");
        var counts = await fixture.CountsAsync(token);
        await fixture.ReopenAsync(token);
        var plan = await fixture.Host.LoadRunRetryAsync(commandId, token);
        Require(plan.UnavailableReason is not null && plan.Items.All(item => !item.CanRetry) &&
                await fixture.CountsAsync(token) == counts && await File.ReadAllTextAsync(fixture.Target, token) == Before,
            "M4 completed manual restore cannot acquire sync retry authority across reopen");
        var verified = await fixture.VerifyAsync(commandId, token);
        Require(Detail(verified).Contains("Completed effects are never replayed", StringComparison.Ordinal) &&
                await fixture.CountsAsync(token) == counts && await File.ReadAllTextAsync(fixture.Target, token) == Before,
            "M4 direct public verification of a completed manual command refuses replay without new effects");
        AssertInherited(fixture.Target, "M4 reopened normal public manual target");
        fixture.RequireNoExternalEffects();
    }

    private static async Task InterruptedAsync(string root, string phase, string mode, CancellationToken token)
    {
        var boundary = await RunCrashChildAsync(root, phase, token);
        using var fixture = await Fixture.OpenAsync(root, token);
        var command = await fixture.Db.LoadManualLocalRestoreCommandAsync(boundary.CommandId, token)
            ?? throw new InvalidDataException("The interrupted command is absent after cold startup.");
        Require(command.Status == "blocked" && Checkpoint(command) == boundary.Checkpoint &&
                fixture.Db.DatabasePath == boundary.DatabasePath && fixture.Pair.Id == boundary.PairId &&
                await fixture.CountsAsync(token) == boundary.Counts,
            "M4 cold public Host retains the exact command/checkpoint and blocks its interrupted outcome without a reset");
        await fixture.RequireRetainedAsync(boundary.Checkpoint, boundary.Retained, token);
        var sidecar = FileStamp(boundary.SidecarPath);
        if (mode == "foreign")
        {
            await RejectForeignAsync(fixture, boundary, sidecar, token);
            return;
        }
        if (mode == "missing-envelope")
        {
            await MissingEnvelopeAsync(fixture, boundary, sidecar, token);
            return;
        }
        await RequireVerifyOnlyPlanAsync(fixture, boundary.CommandId, token);
        var first = await fixture.VerifyAsync(boundary.CommandId, token);
        Require(Detail(first).Contains("access was restored", StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(boundary.SidecarPath),
            "M4 actual public outcome verification repairs and consumes only the exact retained access envelope");
        var restoredPath = phase == "before-promotion" ? boundary.Checkpoint.StagePath : fixture.Target;
        Require(Identity(restoredPath) == boundary.Checkpoint.StageIdentity &&
                await File.ReadAllTextAsync(restoredPath, token) == Before &&
                (phase == "before-promotion" ? !File.Exists(fixture.Target) : !File.Exists(boundary.Checkpoint.StagePath)),
            "M4 access repair preserves the same native file and bytes at its existing candidate; it never copies or promotes a pending stage");
        if (phase == "after-promotion") AssertInherited(restoredPath, "M4 interrupted public manual target");
        else Require(!HasInheritedWorldRead(restoredPath),
            "M4 unpromoted stage retains its real control-parent access, not a fabricated destination DACL");
        var repaired = FileStamp(restoredPath);
        command = await fixture.Db.LoadManualLocalRestoreCommandAsync(boundary.CommandId, token)
            ?? throw new InvalidDataException("The verified command disappeared.");
        using (var result = JsonDocument.Parse(command.ResultJson!))
        {
            var access = result.RootElement.GetProperty("manualLocalRestoreAccess");
            Require(command.Status == "blocked" && access.GetProperty("accessRestored").GetBoolean() &&
                    !access.GetProperty("restoreCompleted").GetBoolean() && access.GetProperty("exactBytes").GetBoolean(),
                "M4 durable access checkpoint explicitly does not complete the uncertain restore");
        }
        for (var repetition = 0; repetition < 2; repetition++)
        {
            if (repetition != 0) await fixture.ReopenAsync(token);
            await RequireVerifyOnlyPlanAsync(fixture, boundary.CommandId, token);
            var repeat = await fixture.VerifyAsync(boundary.CommandId, token);
            Require(Detail(repeat).Contains("envelope is absent", StringComparison.OrdinalIgnoreCase) &&
                    FileStamp(restoredPath) == repaired && !File.Exists(boundary.SidecarPath) &&
                    await fixture.CountsAsync(token) == boundary.Counts &&
                    (await fixture.Db.LoadManualLocalRestoreCommandAsync(boundary.CommandId, token))!.Status == "blocked" &&
                    fixture.Remote.DownloadCount == 0,
                "M4 repeated/cold public verification is qualified, does not replay effects or add commands, and preserves the repaired object");
            await fixture.RequireRetainedAsync(boundary.Checkpoint, boundary.Retained, token);
            fixture.RequireNoExternalEffects();
        }
        await fixture.RequireCliVerifyAsync(boundary.CommandId, token);
        Require(FileStamp(restoredPath) == repaired && await fixture.CountsAsync(token) == boundary.Counts,
            "M4 existing CLI verification consumer reaches the same guarded path without a new restore/run");
        await WriteJsonAsync(Contained(root, "manual-access-repair-proof.json"),
            new { boundary, first, repaired, unresolved = true, restoredCompleted = false }, token);
    }

    private static async Task RejectForeignAsync(Fixture fixture, Boundary boundary, OwnedFile sidecar,
        CancellationToken token)
    {
        // Only the known fixture-owned promoted object: preserve its actual bytes/native ID and the
        // original encrypted envelope. This adversarial input is not a production recovery decision.
        var ownerAccess = new FileSecurity(); ownerAccess.SetAccessRuleProtection(true, false);
        ownerAccess.AddAccessRule(new FileSystemAccessRule(CurrentSid(), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(fixture.Target).SetAccessControl(ownerAccess);
        Require(Identity(fixture.Target) == boundary.Checkpoint.StageIdentity, "M4 foreign-target input starts at the actual promoted stage identity");
        var preservedPath = Contained(fixture.Root, "preserved-original-promoted.bin");
        File.Move(fixture.Target, preservedPath);
        await File.WriteAllTextAsync(fixture.Target, Before, token); // Same bytes/size/hash; different NTFS object.
        var foreign = FileStamp(fixture.Target); var preserved = FileStamp(preservedPath);
        Require(foreign.Identity != boundary.Checkpoint.StageIdentity && preserved.Identity == boundary.Checkpoint.StageIdentity &&
                foreign.Sha256 == preserved.Sha256 && foreign.Length == preserved.Length,
            "M4 changed-native-ID challenge cannot be rejected merely by different content or length");
        for (var reopen = 0; reopen < 2; reopen++)
        {
            if (reopen != 0) await fixture.ReopenAsync(token);
            await RequireVerifyOnlyPlanAsync(fixture, boundary.CommandId, token);
            var result = await fixture.VerifyAsync(boundary.CommandId, token);
            Require(Detail(result).Contains("unresolved", StringComparison.OrdinalIgnoreCase) &&
                    !Detail(result).Contains("access was restored", StringComparison.OrdinalIgnoreCase) &&
                    FileStamp(fixture.Target) == foreign && FileStamp(preservedPath) == preserved &&
                    FileStamp(boundary.SidecarPath) == sidecar && await fixture.CountsAsync(token) == boundary.Counts &&
                    (await fixture.Db.LoadManualLocalRestoreCommandAsync(boundary.CommandId, token))!.Status == "blocked" &&
                    fixture.Remote.DownloadCount == 0,
                "M4 actual public verification refuses a same-byte foreign native target before any ACL/replay/completion and preserves original bytes/envelope across reopen");
            await fixture.RequireRetainedAsync(boundary.Checkpoint, boundary.Retained, token);
            fixture.RequireNoExternalEffects();
        }
        await WriteJsonAsync(Contained(fixture.Root, "manual-foreign-rejection-proof.json"),
            new { boundary, foreign, preserved, sidecar }, token);
    }

    private static async Task MissingEnvelopeAsync(Fixture fixture, Boundary boundary, OwnedFile sidecar,
        CancellationToken token)
    {
        var retainedEnvelope = Contained(fixture.Root, "preserved-original-access-envelope.bin");
        File.Move(boundary.SidecarPath, retainedEnvelope); // Preserve, never delete, the sole recovery input.
        var retained = FileStamp(retainedEnvelope);
        Require(retained.Identity == sidecar.Identity && retained.Sha256 == sidecar.Sha256,
            "M4 missing-envelope input preserves the exact original encrypted bytes and native object");
        var dacl = Dacl(fixture.Target); RequireReadDenied(fixture.Target);
        for (var reopen = 0; reopen < 2; reopen++)
        {
            if (reopen != 0) await fixture.ReopenAsync(token);
            await RequireVerifyOnlyPlanAsync(fixture, boundary.CommandId, token);
            var result = await fixture.VerifyAsync(boundary.CommandId, token);
            Require(Detail(result).Contains("envelope is absent", StringComparison.OrdinalIgnoreCase) &&
                    Detail(result).Contains("does not prove restore completion", StringComparison.OrdinalIgnoreCase) &&
                    Dacl(fixture.Target) == dacl && FileStamp(retainedEnvelope) == retained &&
                    !File.Exists(boundary.SidecarPath) && await fixture.CountsAsync(token) == boundary.Counts &&
                    (await fixture.Db.LoadManualLocalRestoreCommandAsync(boundary.CommandId, token))!.Status == "blocked" &&
                    fixture.Remote.DownloadCount == 0,
                "M4 absent envelope yields an honest unresolved result, not success or speculative access repair, across cold reopen");
            RequireReadDenied(fixture.Target);
            await fixture.RequireRetainedAsync(boundary.Checkpoint, boundary.Retained, token);
            fixture.RequireNoExternalEffects();
        }
        await WriteJsonAsync(Contained(fixture.Root, "manual-missing-envelope-proof.json"),
            new { boundary, originalEnvelope = sidecar, retained, dacl, accessRemainsIsolated = true }, token);
    }

    private static async Task SwappedValidEnvelopeAsync(string root, CancellationToken token)
    {
        // Both inputs are genuine public manual restores interrupted by actual child exit. The donor
        // envelope is cryptographically valid for a different native object, not random corrupt bytes.
        var original = await RunCrashChildAsync(root, "after-promotion", token);
        var donorRoot = Contained(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "-donor");
        var donor = await RunCrashChildAsync(donorRoot, "after-promotion", token);
        using var fixture = await Fixture.OpenAsync(root, token);
        using var donorFixture = await Fixture.OpenAsync(donorRoot, token);
        var ownedEnvelope = FileStamp(original.SidecarPath);
        var donorEnvelope = FileStamp(donor.SidecarPath);
        Require(original.Checkpoint.StageIdentity != donor.Checkpoint.StageIdentity &&
                original.Checkpoint.StageIdentity!.VolumeSerialNumber == donor.Checkpoint.StageIdentity!.VolumeSerialNumber,
            "M4 swapped-envelope input binds distinct genuine promoted native objects on the same owned fixture volume");
        AllowOwnerForFixtureInput(fixture.Target);
        AllowOwnerForFixtureInput(donorFixture.Target);
        Require(Identity(fixture.Target) == original.Checkpoint.StageIdentity &&
                Identity(donorFixture.Target) == donor.Checkpoint.StageIdentity,
            "M4 swap input starts from both public producers' actual promoted objects");
        var preservedOriginal = Contained(root, "preserved-original-promoted.bin");
        var preservedEnvelope = Contained(root, "preserved-original-access-envelope.bin");
        File.Move(fixture.Target, preservedOriginal);
        File.Move(original.SidecarPath, preservedEnvelope);
        File.Move(donorFixture.Target, fixture.Target);
        File.Move(donor.SidecarPath, original.SidecarPath);
        var foreign = FileStamp(fixture.Target);
        var preserved = FileStamp(preservedOriginal);
        var retainedOriginalEnvelope = FileStamp(preservedEnvelope);
        var swapped = FileStamp(original.SidecarPath);
        Require(foreign.Identity == donor.Checkpoint.StageIdentity && preserved.Identity == original.Checkpoint.StageIdentity &&
                foreign.Sha256 == preserved.Sha256 && !HasInheritedWorldRead(fixture.Target) &&
                retainedOriginalEnvelope.Identity == ownedEnvelope.Identity && retainedOriginalEnvelope.Sha256 == ownedEnvelope.Sha256 &&
                swapped.Identity == donorEnvelope.Identity && swapped.Sha256 == donorEnvelope.Sha256,
            "M4 valid swapped envelope would match the foreign candidate; original object/envelope and both source/preimages are preserved");
        for (var reopen = 0; reopen < 2; reopen++)
        {
            if (reopen != 0) await fixture.ReopenAsync(token);
            await RequireVerifyOnlyPlanAsync(fixture, original.CommandId, token);
            var verified = await fixture.VerifyAsync(original.CommandId, token);
            Require(Detail(verified).Contains("envelope does not match the original command-bound native identity", StringComparison.Ordinal) &&
                    FileStamp(fixture.Target) == foreign && FileStamp(preservedOriginal) == preserved &&
                    FileStamp(preservedEnvelope) == retainedOriginalEnvelope && FileStamp(original.SidecarPath) == swapped &&
                    await fixture.CountsAsync(token) == original.Counts &&
                    (await fixture.Db.LoadManualLocalRestoreCommandAsync(original.CommandId, token))!.Status == "blocked" &&
                    fixture.Remote.DownloadCount == 0 && donorFixture.Remote.DownloadCount == 0,
                "M4 public verification rejects a valid foreign-object envelope BEFORE changing ACL, consuming envelope or replaying/completing restore across reopen");
            await fixture.RequireRetainedAsync(original.Checkpoint, original.Retained, token);
            await donorFixture.RequireRetainedAsync(donor.Checkpoint, donor.Retained, token);
            Require(await donorFixture.CountsAsync(token) == donor.Counts,
                "M4 donor remains the same unresolved command, never consumed as the original restore");
            fixture.RequireNoExternalEffects(); donorFixture.RequireNoExternalEffects();
        }
        await WriteJsonAsync(Contained(root, "manual-swapped-valid-envelope-proof.json"),
            new { original, donor, foreign, preserved, retainedOriginalEnvelope, swapped }, token);
    }

    private static void AllowOwnerForFixtureInput(string path)
    {
        var security = new FileSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentSid(), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static async Task FailureCheckpointAsync(string root, CancellationToken token)
    {
        using var fixture = await Fixture.CreateAsync(root, token);
        var selected = await fixture.ProduceRestoreInputAsync(token);
        string? failedCommand = null;
        fixture.Host.ManualLocalRestorePhaseForTest = (phase, id) =>
        {
            if (phase != "after-promotion") return Task.CompletedTask;
            failedCommand = id; throw new IOException("M4 fixture failure after real manual promotion");
        };
        Exception? failed = null;
        try { await fixture.Host.RestoreRecoveryAsync(selected, restoreAsCopy: false, token); }
        catch (IOException error) { failed = error; }
        finally { fixture.Host.ManualLocalRestorePhaseForTest = null; }
        Require(failed is not null && failedCommand is not null, "M4 checkpoint retention control reaches the real error/finally path");
        var command = await fixture.Db.LoadManualLocalRestoreCommandAsync(failedCommand!, token)
            ?? throw new InvalidDataException("The failed manual command is absent.");
        var checkpoint = Checkpoint(command);
        using (var result = JsonDocument.Parse(command.ResultJson!))
            Require(command.Status == "blocked" && result.RootElement.TryGetProperty("commandFailure", out _) &&
                    checkpoint.StageIdentity == Identity(fixture.Target) && checkpoint.Phase == "promotion_pending",
                "M4 generic command failure retains the original native checkpoint rather than replacing it");
        var retained = await fixture.RetainedAsync(checkpoint, token);
        var target = FileStamp(fixture.Target); var counts = await fixture.CountsAsync(token);
        AssertInherited(fixture.Target, "M4 ordinary error unwind restores real destination inheritance");
        await fixture.ReopenAsync(token);
        await RequireVerifyOnlyPlanAsync(fixture, failedCommand!, token);
        var verified = await fixture.VerifyAsync(failedCommand!, token);
        Require(Detail(verified).Contains("envelope is absent", StringComparison.OrdinalIgnoreCase) &&
                FileStamp(fixture.Target) == target && await fixture.CountsAsync(token) == counts,
            "M4 error-unwind access restoration is not conflated with abrupt exit or uncertain restore completion");
        await fixture.RequireRetainedAsync(checkpoint, retained, token);
        fixture.RequireNoExternalEffects();
    }

    private static async Task LegacyAsync(string root, CancellationToken token)
    {
        using var fixture = await Fixture.CreateAsync(root, token);
        var selected = await fixture.ProduceRestoreInputAsync(token);
        var id = "command-" + Guid.NewGuid().ToString("N");
        await fixture.Db.CreateCommandRequestAsync(id, "reconcile", fixture.Pair.Id, selected.Key, null,
            JsonSerializer.Serialize(new { action = "restore_recovery", pairIds = new[] { fixture.Pair.Id },
                source = "LocalArchive", sourcePath = Relative, TargetRelativePath = Relative, restoreAsCopy = false }), token);
        await fixture.Db.TransitionCommandRequestAsync(id, "accepted", cancellationToken: token);
        await fixture.Db.TransitionCommandRequestAsync(id, "executing", cancellationToken: token);
        var target = FileStamp(fixture.Target);
        var source = FileStamp(ArchivePath((await fixture.Db.LoadRecoveryArtifactsAsync(token))
            .Single(item => item.RecoveryArtifactId == selected.Key).IdentityJson));
        var counts = await fixture.CountsAsync(token);
        for (var reopen = 0; reopen < 2; reopen++)
        {
            await fixture.ReopenAsync(token);
            await RequireVerifyOnlyPlanAsync(fixture, id, token);
            var result = await fixture.VerifyAsync(id, token);
            Require(Detail(result).Contains("Legacy manual restore", StringComparison.OrdinalIgnoreCase) &&
                    Detail(result).Contains("original root/stage checkpoint", StringComparison.OrdinalIgnoreCase) &&
                    FileStamp(fixture.Target) == target && FileStamp(source.Path) == source &&
                    await fixture.CountsAsync(token) == counts &&
                    (await fixture.Db.LoadManualLocalRestoreCommandAsync(id, token))!.Status == "blocked" &&
                    fixture.Remote.DownloadCount == 0,
                "M4 real persisted legacy command refuses effects: current pair settings do not invent its historical root/stage authority");
            fixture.RequireNoExternalEffects();
        }
        await WriteJsonAsync(Contained(root, "manual-legacy-qualification-proof.json"),
            new { commandId = id, target, source, counts, actualInterruptedLegacyRepair = "NOT PROVED; original binding is absent" }, token);
    }

    private static async Task RequireVerifyOnlyPlanAsync(Fixture fixture, string id, CancellationToken token)
    {
        var plan = await fixture.Host.LoadRunRetryAsync(id, token, selectedPairId: fixture.Pair.Id);
        Require(plan.RunId == id && plan.PairId == fixture.Pair.Id && plan.UnavailableReason is null &&
                plan.Items.Count == 1 && plan.Items.Single().Path == Relative &&
                plan.Items.Single().State == "Verify first" && !plan.Items.Single().CanRetry,
            "M4 existing GUI/CLI run-retry projection exposes exactly the original manual command as Verify first, never a retry");
        var wrong = await fixture.Host.LoadRunRetryAsync(id, token, selectedPairId: "another-fixture-pair");
        Require(wrong.UnavailableReason is not null && wrong.Items.Count == 0,
            "M4 wrong selected pair cannot acquire verification/retry projection authority");
        var counts = await fixture.CountsAsync(token);
        Exception? refused = null;
        try { await fixture.Host.RetryRunAsync(id, [Relative], "all", cancellationToken: token, selectedPairId: fixture.Pair.Id); }
        catch (InvalidOperationException error) { refused = error; }
        Require(refused is not null && await fixture.CountsAsync(token) == counts,
            "M4 actual public RetryRun rejects manual Verify-first selection before admitting any command/effect");
    }

    private static async Task<Boundary> RunCrashChildAsync(string root, string phase, CancellationToken token)
    {
        ValidatePhase(phase);
        var test = Binary(typeof(NativeManualRestoreInterruptionChecks).Assembly.Location);
        var core = Binary(typeof(SyncAuthorityRuntimeHost).Assembly.Location);
        var executable = Binary(Environment.ProcessPath ?? throw new IOException("The current executable is absent."));
        var start = new ProcessStartInfo(executable.Path)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // Preserve the production test-only Temp containment rule. This child owns
        // only this suite allocation, including the abrupt-exit runtime sibling;
        // no global environment or installed product root is changed.
        var childTemporaryRoot = Path.GetDirectoryName(Path.GetFullPath(root))
            ?? throw new IOException("The exact crash-child fixture allocation has no parent.");
        start.Environment["TEMP"] = childTemporaryRoot;
        start.Environment["TMP"] = childTemporaryRoot;
        if (string.Equals(Path.GetFileNameWithoutExtension(executable.Path), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(test.Path);
        start.ArgumentList.Add(ChildSelector); start.ArgumentList.Add(Path.GetFullPath(root)); start.ArgumentList.Add(phase);
        using var child = Process.Start(start) ?? throw new IOException("The exact fixture crash child did not start.");
        var pid = child.Id; var startedUtc = child.StartTime.ToUniversalTime();
        var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            await WriteJsonAsync(Contained(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "-child-invocation.json"),
                new { pid, startedUtc, executable, test, core, selector = ChildSelector, phase, caseRoot = root }, token);
            await child.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(100), token);
        }
        catch
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: false); await child.WaitForExitAsync(); }
            Directory.CreateDirectory(root);
            await WriteJsonAsync(Contained(root, "owned-child-timeout.json"),
                new { pid, startedUtc, executable, test, core, phase }, CancellationToken.None);
            await File.WriteAllTextAsync(Contained(root, "child.stdout.log"), await stdout, CancellationToken.None);
            await File.WriteAllTextAsync(Contained(root, "child.stderr.log"), await stderr, CancellationToken.None);
            throw;
        }
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Contained(root, "child.stdout.log"), await stdout, token);
        await File.WriteAllTextAsync(Contained(root, "child.stderr.log"), await stderr, token);
        Require(Binary(test.Path) == test && Binary(core.Path) == core && Binary(executable.Path) == executable &&
                child.ExitCode == CrashExit && File.Exists(Contained(root, "manual-interrupted-boundary.json")),
            "M4 retained exit code/boundary and identical current binaries prove a real abrupt process interruption, not Dispose or a skipped test");
        var boundary = JsonSerializer.Deserialize<Boundary>(await File.ReadAllTextAsync(
            Contained(root, "manual-interrupted-boundary.json"), token), Json)
            ?? throw new InvalidDataException("The exact manual interruption boundary is absent.");
        Require(boundary.Phase == phase && boundary.Root == Path.GetFullPath(root) && boundary.ProcessId == pid &&
                boundary.Executable == executable && boundary.TestBinary == test && boundary.CoreBinary == core &&
                boundary.Downloads == 2 && boundary.Checkpoint.StageIdentity is not null,
            "M4 boundary binds the actual source producer, native command/stage, exact phase and subprocess binary identities");
        return boundary;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; }
        internal string LocalRoot => Contained(Root, "local");
        internal string Target => Contained(LocalRoot, Relative);
        internal string ControlRoot => Pair.ControlNamespace!.Local.RootPath;
        internal PilotDirectoryIdentity LocalIdentity => new(Pair.LocalVolumeSerialNumber, Pair.LocalRootFileId);
        internal FakeSyncRemoteStore Remote { get; private set; } = new() { ExactPathMetadataForTest = true };
        internal SyncPair Pair = null!;
        internal SyncAuthorityDatabase Db = null!;
        internal SyncAuthorityRuntimeHost Host = null!;
        private PilotInstanceLock instance = null!;
        private HttpClient http = null!;
        private readonly DenyNetwork transport = new();
        private DaclPreimage? preimage;
        private int providerPreparations;
        private static readonly DropboxConnectionBinding Connection = new DropboxConnectionBinding(
            DropboxProviderConfiguration.RegisteredClientId, "manual-interruption-account", "manual-interruption-root", "manual-interruption-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes).Normalize();
        private Fixture(string root) => Root = Path.GetFullPath(root);

        internal static async Task<Fixture> CreateAsync(string root, CancellationToken token)
        {
            var f = new Fixture(root);
            if (File.Exists(f.Root) || Directory.Exists(f.Root)) throw new IOException("The manual fixture case root must be fresh.");
            Directory.CreateDirectory(Contained(f.LocalRoot, Parent));
            using var local = PilotDirectoryLease.Open(f.LocalRoot);
            using var parent = PilotDirectoryLease.Open(Contained(f.LocalRoot, Parent));
            local.VerifyBinding(); parent.VerifyBinding();
            f.preimage = new DaclPreimage(local.Identity, parent.Identity,
                new DirectoryInfo(local.Path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                new DirectoryInfo(parent.Path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            await WriteJsonAsync(Contained(f.Root, "owned-dacl-preimage.json"), f.preimage, token);
            new DirectoryInfo(local.Path).SetAccessControl(ParentSecurity(distinctive: false));
            new DirectoryInfo(parent.Path).SetAccessControl(ParentSecurity(distinctive: true));
            try
            {
                f.instance = PilotInstanceLock.Acquire(Contained(f.Root, "state/instance.lock"));
                var store = new SyncPairStore(f.instance.StateAuthority);
                var installation = await store.GetOrCreateInstallationIdAsync(token);
                var pairId = Guid.NewGuid().ToString("N");
                var localControl = SyncControlNamespace.BindLocalForTest(local, installation, pairId);
                var remotePath = SyncControlNamespace.RemoteContainerPath + "/" + pairId;
                var remoteControl = new SyncRemoteControlNamespaceBinding(remotePath, "id:manual-interruption-control",
                    remotePath + "/.syncshark-owner.json", "id:manual-interruption-marker", "rev:marker-1",
                    DropboxContentHash.Compute(SyncControlNamespace.MarkerBytes(installation, pairId)));
                f.Pair = new SyncPair(pairId, "M4 isolated manual restore interruption", f.LocalRoot,
                    local.Identity.VolumeSerialNumber, local.Identity.FileId, "/ManualInterruption", "id:manual-interruption-root",
                    ConnectionId: Connection.ConnectionId, InitialReviewApproved: true, FilesOnDemandEnabled: false,
                    ControlNamespace: new SyncControlNamespaceBinding(installation, pairId, localControl, remoteControl),
                    RecoveryRoot: new SyncRemoteRecoveryRootBinding(SyncDropboxArtifactPaths.RecoveryContainerPath,
                        "id:manual-interruption-recovery", Connection.RootNamespaceId));
                await store.SaveAsync(new SyncPairSet([f.Pair], f.Pair.Id), token);
                await new DropboxConnectionStore(f.instance.StateAuthority).SaveNewAsync(Connection, token);
                await new SyncAutomationStore(f.instance.StateAuthority).SaveAsync(new SyncAutomationSettings(false, 15, false), token);
                f.Remote.PutDirectory(Parent);
                await f.InitializeAsync(token, reopening: false);
                Require(!HasInheritedWorldRead(f.ControlRoot),
                    "M4 actual private manual staging parent differs from the intended inherited destination access");
                return f;
            }
            catch { f.Dispose(); throw; }
        }

        internal static async Task<Fixture> OpenAsync(string root, CancellationToken token)
        {
            var f = new Fixture(root);
            f.preimage = JsonSerializer.Deserialize<DaclPreimage>(await File.ReadAllTextAsync(
                Contained(f.Root, "owned-dacl-preimage.json"), token), Json)
                ?? throw new InvalidDataException("The original fixture DACL preimage is absent.");
            using (var local = PilotDirectoryLease.Open(f.LocalRoot, f.preimage.LocalIdentity)) local.VerifyBinding();
            using (var parent = PilotDirectoryLease.Open(Contained(f.LocalRoot, Parent), f.preimage.ParentIdentity)) parent.VerifyBinding();
            try
            {
                f.instance = PilotInstanceLock.Acquire(Contained(f.Root, "state/instance.lock"));
                f.Pair = new SyncPairStore(f.instance.StateAuthority).Load().Pairs.Single();
                Require(f.Pair.LocalRoot == f.LocalRoot && f.LocalIdentity == f.preimage.LocalIdentity,
                    "M4 cold manual fixture reopens the exact original native root binding");
                f.Remote.PutDirectory(Parent);
                await f.InitializeAsync(token, reopening: true);
                Require(f.providerPreparations == 0, "M4 cold initialization has not prepared a provider for access verification");
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
                new DenyCredentials(), Db, (_, _) => { providerPreparations++; return Remote; },
                googleDriveCredentialStore: new DenyCredentials());
            Pair = (await Host.LoadPairsAsync(token)).Pairs.Single();
            // Only isolated fixture settings. Preserve actual available-memory admission and all other
            // controls; do not re-register pair configs after the real Host import.
            if (reopening)
                Require(Host.LoadTransferSettings().ResourceMode == "Performance", "M4 manual fixture cold reopen preserves functional mode");
            else await Host.SaveTransferSettingsAsync(Host.LoadTransferSettings() with { ResourceMode = "Performance" }, token);
        }

        internal async Task ReopenAsync(CancellationToken token)
        {
            Host.Dispose(); http.Dispose(); instance.Dispose();
            instance = PilotInstanceLock.Acquire(Contained(Root, "state/instance.lock"));
            Remote = new FakeSyncRemoteStore { ExactPathMetadataForTest = true };
            Remote.PutDirectory(Parent); providerPreparations = 0;
            await InitializeAsync(token, reopening: true);
            Require(providerPreparations == 0, "M4 reopened verification has no provider-preparation side effect");
        }

        internal async Task<SyncRecoveryCatalogItem> ProduceRestoreInputAsync(CancellationToken token)
        {
            await RunHealthyAsync(token);
            Remote.Put(Relative, Before);
            Require((await RunHealthyAsync(token)).Downloaded == 1 && await File.ReadAllTextAsync(Target, token) == Before,
                "M4 manual producer starts from an actually downloaded and committed original version");
            Remote.Put(Relative, After);
            Require((await RunHealthyAsync(token)).Downloaded == 1 && await File.ReadAllTextAsync(Target, token) == After,
                "M4 actual public update creates the manual input archive before any test interruption");
            var artifact = (await Db.LoadRecoveryArtifactsAsync(token)).Single(item => item.Side == "local");
            Require(artifact.Status is "retained" or "verified" && File.ReadAllText(ArchivePath(artifact.IdentityJson)) == Before,
                "M4 manual restore source is the real update's retained predecessor, not a fabricated artifact/admission");
            var selected = (await Host.LoadRecoveryCatalogAsync(Pair.Id, cancellationToken: token))
                .Single(item => item.Key == artifact.RecoveryArtifactId);
            Require(selected.Source == SyncRecoverySource.LocalArchive && selected.CanRestore,
                "M4 actual public catalog exposes the exact retained local archive");
            RequireNoExternalEffects();
            return selected;
        }

        private async Task<SyncRunResult> RunHealthyAsync(CancellationToken token)
        {
            var batch = await Host.RunPairsAsync([Pair], "Manual", cancellationToken: token);
            Console.WriteLine(JsonSerializer.Serialize(new { entrypoint = "Host.RunPairsAsync", batch }));
            var item = batch.Items.Single();
            Require(!batch.Stopped && item.FailureType is null && item.Result is not null &&
                    item.Result.Decisions.All(decision => decision.Kind is not
                        (SyncDecisionKind.Blocked or SyncDecisionKind.Conflict or SyncDecisionKind.Stopped or SyncDecisionKind.ResponseUnknown)),
                "M4 isolated producer completes through real Host planning/admission/effects rather than a helper substitute");
            return item.Result!;
        }

        internal async Task<SyncAuthorityBatchResult> VerifyAsync(string command, CancellationToken token)
        {
            var prepared = providerPreparations;
            var result = await Host.VerifyRunOutcomesAsync(command, cancellationToken: token);
            Console.WriteLine(JsonSerializer.Serialize(new { entrypoint = "Host.VerifyRunOutcomesAsync", command, result }));
            Require(!result.Stopped && result.Items.Count == 1 && result.Items.Single().FailureType is null &&
                    result.Items.Single().Result is { } actual && actual.AppliedItems == 0 &&
                    actual.Decisions.Count == 1 && actual.Decisions.Single().Kind == SyncDecisionKind.Blocked &&
                    actual.Decisions.Single().NotAttempted &&
                    providerPreparations == prepared,
                "M4 public manual access verification reports unresolved/no transfer, not success, and does not prepare a provider");
            return result;
        }

        internal async Task RequireCliVerifyAsync(string command, CancellationToken token)
        {
            var prepared = providerPreparations;
            var progress = new List<string>();
            var runtime = new FixtureCli(Host, instance.StateAuthority);
            var result = await runtime.VerifyRunAsync(command, progress.Add, token);
            Console.WriteLine(JsonSerializer.Serialize(new { entrypoint = "SyncCliOperationsBase.VerifyRunAsync", command, result }));
            using var receipt = JsonDocument.Parse(JsonSerializer.Serialize(result));
            var value = receipt.RootElement;
            Require(value.GetProperty("originalRunId").GetString() == command &&
                    value.GetProperty("runId").ValueKind == JsonValueKind.Null &&
                    value.GetProperty("count").GetInt32() == 1 && !value.GetProperty("stopped").GetBoolean() &&
                    value.GetProperty("pairs")[0].GetProperty("result").GetProperty("Blocked").GetInt32() == 1 &&
                    providerPreparations == prepared && progress.Count != 0,
                "M4 actual CLI consumer reports the original unresolved manual command, not a new run or completed transfer");
            await using var connection = ReadOnlyConnection(); await connection.OpenAsync(token);
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT count(*) FROM history_event WHERE event_kind='presentation' AND json_extract(detail_json,'$.RunId')=$id AND json_extract(detail_json,'$.Action')='Manual restore access verification' AND json_extract(detail_json,'$.Message') LIKE '%envelope is absent%';";
            query.Parameters.AddWithValue("$id", command);
            Require(Convert.ToInt64(await query.ExecuteScalarAsync(token)) > 0,
                "M4 qualified verification reason is retained in the original run's real activity projection for CLI/GUI readback");
        }

        internal async Task<RetainedFiles> RetainedAsync(CheckpointValue checkpoint, CancellationToken token,
            OwnedFile? heldSource = null)
        {
            var artifacts = await Db.LoadRecoveryArtifactsAsync(token);
            var source = artifacts.Single(item => item.RecoveryArtifactId == checkpoint.RecoveryArtifactId);
            var preimage = artifacts.Single(item => item.RecoveryArtifactId == checkpoint.PreimageRecoveryArtifactId);
            Require(source.Side == "local" && preimage.Side == "local" &&
                    source.Status is "retained" or "verified" && preimage.Status is "retained" or "verified" &&
                    SourceLink(preimage.IdentityJson) == source.RecoveryArtifactId,
                "M4 actual manual preimage is durably linked to its exact retained source");
            var original = heldSource ?? FileStamp(ArchivePath(source.IdentityJson));
            var displaced = FileStamp(ArchivePath(preimage.IdentityJson));
            Require(original.Path == ArchivePath(source.IdentityJson) && original.Identity == checkpoint.SourceIdentity &&
                    (heldSource is not null || File.ReadAllText(original.Path) == Before) &&
                    original.Sha256 == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Before))).ToLowerInvariant() &&
                    File.ReadAllText(displaced.Path) == After,
                "M4 original version and actual displaced destination remain readable, native-bound and retained");
            return new(source, preimage, original, displaced);
        }
        internal async Task RequireRetainedAsync(CheckpointValue checkpoint, RetainedFiles expected, CancellationToken token)
        {
            foreach (var id in new[] { expected.Source.RecoveryArtifactId, expected.Preimage.RecoveryArtifactId })
            {
                Exception? refusal = null;
                try { await Db.AuthorizeRecoveryArtifactReleaseAsync(id, token); }
                catch (InvalidOperationException error) { refusal = error; }
                Require(refusal is not null && refusal.Message.Contains("pending or unresolved restore", StringComparison.Ordinal),
                    "M4 actual public cleanup admission refuses the source and linked preimage while the manual command remains unresolved");
            }
            Require(await RetainedAsync(checkpoint, token) == expected,
                "M4 verification/refusal/reopen retains exact source and linked preimage rows, identities, bytes and DACLs");
        }
        internal async Task<string[]> ManualCommandsAsync(CancellationToken token)
        {
            await using var connection = ReadOnlyConnection(); await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT command_request_id FROM command_request WHERE command_kind='reconcile' AND json_extract(payload_json,'$.action')='restore_recovery' AND json_extract(payload_json,'$.source')='LocalArchive';";
            var ids = new List<string>(); await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) ids.Add(reader.GetString(0));
            return ids.ToArray();
        }
        internal async Task<AuthorityCounts> CountsAsync(CancellationToken token)
        {
            await using var connection = ReadOnlyConnection(); await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT (SELECT count(*) FROM command_request),(SELECT count(*) FROM operation),(SELECT count(*) FROM operation_attempt),(SELECT count(*) FROM recovery_artifact),(SELECT count(*) FROM synchronization_point),(SELECT count(*) FROM effect_receipt);";
            await using var reader = await command.ExecuteReaderAsync(token); await reader.ReadAsync(token);
            return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
        }
        private SqliteConnection ReadOnlyConnection() => new(new SqliteConnectionStringBuilder
        { DataSource = Db.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        internal void RequireNoExternalEffects() => Require(transport.Calls == 0 &&
                Remote.UploadCount == 0 && Remote.ArchiveCount == 0 && Remote.DeleteCount == 0 &&
                Remote.VisibleCopyCount == 0 && Remote.MoveCount == 0 && Remote.DirectoryCreateCount == 0,
            "M4 manual fixture reaches no network/real credentials or provider write/archive/delete/copy/move effects");
        public void Dispose()
        {
            Host?.Dispose(); http?.Dispose(); instance?.Dispose(); transport.Dispose();
            if (preimage is null) return;
            using var local = PilotDirectoryLease.Open(LocalRoot, preimage.LocalIdentity);
            using var parent = PilotDirectoryLease.Open(Contained(LocalRoot, Parent), preimage.ParentIdentity);
            local.VerifyBinding(); parent.VerifyBinding();
            RestoreDacl(parent.Path, preimage.ParentDacl); RestoreDacl(local.Path, preimage.LocalDacl);
            // Restore only exact fixture-parent preimages. Preserve DB/files/sidecars/failures; no cleanup.
        }
    }

    private static CheckpointValue Checkpoint(SyncManualLocalRestoreCommand command)
    {
        using var result = JsonDocument.Parse(command.ResultJson ?? throw new InvalidDataException("The manual checkpoint is absent."));
        return result.RootElement.GetProperty("manualLocalRestore").Deserialize<CheckpointValue>()
            ?? throw new InvalidDataException("The actual manual checkpoint is unreadable.");
    }
    private static string Detail(SyncAuthorityBatchResult result) => result.Items.Single().Result!.Decisions.Single().Reason;
    private static string ArchivePath(string json)
    { using var value = JsonDocument.Parse(json); return value.RootElement.GetProperty("path").GetString() ?? throw new InvalidDataException("Actual archive path is absent."); }
    private static string? SourceLink(string json)
    { using var value = JsonDocument.Parse(json); return value.RootElement.TryGetProperty("sourceRecoveryArtifactId", out var source) ? source.GetString() : null; }
    private static DirectorySecurity ParentSecurity(bool distinctive)
    {
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentSid(), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (distinctive) security.AddAccessRule(new FileSystemAccessRule(World(), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
    private static void RestoreDacl(string path, string dacl)
    { var security = new DirectorySecurity(); security.SetSecurityDescriptorSddlForm(dacl, AccessControlSections.Access); new DirectoryInfo(path).SetAccessControl(security); }
    private static bool HasInheritedWorldRead(string path)
    {
        FileSystemSecurity security = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        return security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule =>
            rule.IsInherited && rule.IdentityReference.Equals(World()) && rule.AccessControlType == AccessControlType.Allow &&
            (rule.FileSystemRights & FileSystemRights.ReadData) != 0);
    }
    private static void AssertInherited(string path, string detail) => Require(HasInheritedWorldRead(path),
        detail + " inherits its actual destination parent's distinctive World Read ACE, not the private-stage DACL");
    private static void RequireReadDenied(string path)
    {
        Exception? denied = null;
        try { using var readable = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (UnauthorizedAccessException error) { denied = error; }
        Require(denied is not null, "M4 missing-envelope target remains genuinely isolated, not merely a missing receipt");
    }
    private static SecurityIdentifier CurrentSid()
    { using var identity = WindowsIdentity.GetCurrent(); return identity.User ?? throw new IOException("The fixture identity has no SID."); }
    private static SecurityIdentifier World() => new(WellKnownSidType.WorldSid, null);
    private static PilotDirectoryIdentity Identity(string path)
    { using var file = SyncLocalFileAuthority.OpenExistingExclusive(path); return file.VerifySingleLink(); }
    private static string Dacl(string path) => new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    private static OwnedFile FileStamp(string path) => new(Path.GetFullPath(path), Identity(path), new FileInfo(path).Length,
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), Dacl(path));
    private static BinaryStamp Binary(string path) => new(Path.GetFullPath(path), new FileInfo(path).Length,
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
    private static string Contained(string root, string relative)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var exact = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!exact.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Manual fixture path escaped its exact owned allocation.");
        return exact;
    }
    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await file.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json)), token);
        await file.FlushAsync(token); file.Flush(true);
    }
    private static void ValidatePhase(string phase)
    { if (phase is not ("before-promotion" or "after-promotion")) throw new ArgumentException("An exact manual RenameTo boundary is required.", nameof(phase)); }
    private static void RequireWindows()
    { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("M4 manual interruption requires real Windows/NTFS; unsupported is not PASS."); }
    private static void Require(bool condition, string detail)
    { if (!condition) throw new InvalidDataException(detail); Console.WriteLine("PASS " + detail); }
    private sealed record OwnedFile(string Path, PilotDirectoryIdentity Identity, long Length, string Sha256, string Dacl);
    private sealed record BinaryStamp(string Path, long Length, string Sha256);
    private sealed record DaclPreimage(PilotDirectoryIdentity LocalIdentity, PilotDirectoryIdentity ParentIdentity, string LocalDacl, string ParentDacl);
    private sealed record AuthorityCounts(long Commands, long Operations, long Attempts, long Artifacts,
        long SynchronizationPoints, long EffectReceipts);
    private sealed record RetainedFiles(SyncRecoveryArtifactProjection Source, SyncRecoveryArtifactProjection Preimage,
        OwnedFile Original, OwnedFile Displaced);
    private sealed record CheckpointValue(int Version, string CommandId, string PairId, long PolicyRevision,
        string Provider, string RecoveryArtifactId, string SourceArchivePath, PilotDirectoryIdentity SourceIdentity,
        long ExpectedSize, string ExpectedHash, string RootPath, PilotDirectoryIdentity RootIdentity,
        string ControlPath, PilotDirectoryIdentity ControlIdentity, string TargetRelativePath, string TargetPath,
        string ParentPath, PilotDirectoryIdentity ParentIdentity, string StagePath, PilotDirectoryIdentity? StageIdentity,
        string Phase, string? PreimageRecoveryArtifactId);
    private sealed record Boundary(string Phase, string Root, string DatabasePath, string PairId, string CommandId,
        CheckpointValue Checkpoint, RetainedFiles Retained, AuthorityCounts Counts, string SidecarPath, int Downloads,
        int ProcessId, BinaryStamp Executable, BinaryStamp TestBinary, BinaryStamp CoreBinary);
    private sealed class FixtureCli(SyncAuthorityRuntimeHost host, PilotStateAuthority state) : SyncCliOperationsBase
    {
        protected override SyncAuthorityRuntimeHost Host => host;
        protected override PilotStateAuthority StateAuthority => state;
        public override string Mode => "exclusive-product-lock";
    }
    private sealed class DenyNetwork : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("The manual interruption fixture denies every HTTP request."); }
    }
    private sealed class DenyCredentials : IDropboxRefreshTokenStore, IGoogleDriveRefreshTokenStore
    {
        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding selected) => this;
        public Task<string?> ReadAsync(CancellationToken token = default) => Task.FromResult<string?>("manual-fixture-synthetic-input");
        public Task<string?> ReadAsync(string id, CancellationToken token = default) => throw new InvalidOperationException("Manual fixture denies Google credential access.");
        public Task WriteAsync(string value, CancellationToken token = default) => Refuse();
        public Task WriteAsync(string id, string value, CancellationToken token = default) => Refuse();
        public Task DeleteAsync(CancellationToken token = default) => Refuse();
        public Task DeleteAsync(string id, CancellationToken token = default) => Refuse();
        private static Task Refuse() => throw new InvalidOperationException("Manual fixture denies protected credential mutation.");
    }
}
