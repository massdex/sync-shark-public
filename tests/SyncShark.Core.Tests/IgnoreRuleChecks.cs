using Massdex.SyncShark;
using System.Text.Json;

internal static class IgnoreRuleChecks
{
    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The real ignore-policy inventory check requires Windows.");
        var rules = SyncIgnorePolicy.Normalize([new(SyncIgnoreKind.FileExtension, " TMP "),
            new(SyncIgnoreKind.FileName, "Thumbs.db"), new(SyncIgnoreKind.FolderName, "cache")]);
        Require(rules[0].Value == ".tmp", "Plain file type input is normalized without regex");
        Require(SyncIgnorePolicy.IsIgnored("Work/DRAFT.TMP", false, rules), "File type matches future items case-insensitively");
        Require(!SyncIgnorePolicy.IsIgnored("report.tmp", true, rules), "File type does not exclude a similarly named directory");
        Require(!SyncIgnorePolicy.IsIgnored("Thumbs.db", true, rules), "File Name does not exclude a directory with that name");
        Require(SyncIgnorePolicy.IsIgnored("Work/cache/sub/file.txt", false, rules) &&
            !SyncIgnorePolicy.IsIgnored("Work/cached/file.txt", false, rules), "Folder names match complete path components at any depth");
        try { SyncIgnorePolicy.Normalize([new(SyncIgnoreKind.FileName, "*.txt")]); throw new Exception("Wildcard unexpectedly accepted"); }
        catch (InvalidDataException) { Console.WriteLine("PASS Unsupported wildcard gives a plain-input validation error"); }

        var local = Path.Combine(root, "local"); Directory.CreateDirectory(local);
        await File.WriteAllTextAsync(Path.Combine(local, "baseline.tmp"), "retained baseline");
        await File.WriteAllTextAsync(Path.Combine(local, "gone.tmp"), "retained missing baseline");
        var remote = new FakeSyncRemoteStore(); remote.Put("baseline.tmp", "retained baseline"); remote.Put("gone.tmp", "retained missing baseline");
        using var lease = PilotDirectoryLease.Open(local);
        var pair = new SyncPair("ignore-policy-pair", "Ignore check", local, lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, "/Ignore", "id:ignore", PropagateDeletions: true,
            ConnectionId: "account-first");
        pair = pair with { ControlNamespace = new SyncControlNamespaceBinding("ignore-installation", pair.Id,
            SyncControlNamespace.BindLocalForTest(lease, "ignore-installation", pair.Id)) };
        var db = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        await db.InitializeAsync("ignore-installation", "ignore-profile", "ignore-check", "{}");
        await db.RegisterPairAsync(pair.Id, 1);
        var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        await db.RetainPairDefinitionAsync(pair, true);
        await db.RegisterRootBindingAsync("ignore-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { lease.Identity.VolumeSerialNumber, lease.Identity.FileId }), local);
        await db.RegisterRootBindingAsync("ignore-dropbox", pair.Id, "dropbox", 1,
            "{\"folderId\":\"id:ignore\"}", pair.DropboxRoot);
        var observer = new SyncAuthorityObservationCoordinator(pair, remote, db, "ignore-local", "ignore-dropbox");
        await observer.ObserveAsync("initial"); await observer.ObserveAsync("initial");
        await new SyncAuthorityInitialPlanner(db).AdoptAsync(pair.Id);
        await File.WriteAllTextAsync(Path.Combine(local, "pending.tmp"), "not dispatched yet");
        await observer.ObserveAsync("manual");
        var options = new SyncAuthorityPlannerOptions(1, "ignore-profile", "ignore-control", "ignore-control",
            EnableDeletionEffects: true, EnableMoveEffects: true);
        var priorPlan = await new SyncAuthorityReconciliationPlanner(db, options, admission: admission).PlanAsync(pair.Id);
        Require(priorPlan.Any(item => item.RelativePath == "pending.tmp" && item.OperationId is not null), "An upload is genuinely queued before changing policy");
        try { await db.EnsurePairConfigurationMutableAsync(pair.Id); throw new Exception("A queued policy change was unexpectedly accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS A policy change is fenced while an old operation remains queued"); }
        await db.SetPairStateAsync(pair.Id, "active");
        await new SyncAuthorityPairRunner(pair, remote, db, "ignore-local", "ignore-dropbox", options,
            new PilotRunControl(), admission: admission).RunAsync("manual");
        await db.EnsurePairConfigurationMutableAsync(pair.Id);
        pair = pair with { IgnoreRules = rules, PolicyRevision = 2 };
        await db.RegisterPairAsync(pair.Id, 2); await db.RetainPairDefinitionAsync(pair, true);
        await File.WriteAllTextAsync(Path.Combine(local, "new.tmp"), "ignored after rule saved");
        File.Delete(Path.Combine(local, "baseline.tmp")); // The remote must NOT be deleted by sync.
        File.Delete(Path.Combine(local, "gone.tmp")); remote.Delete("gone.tmp");
        await File.WriteAllTextAsync(Path.Combine(local, "keep.txt"), "sync me");
        await File.WriteAllTextAsync(Path.Combine(local, "Thumbs.db"), "keep local");
        Directory.CreateDirectory(Path.Combine(local, "cache"));
        await File.WriteAllTextAsync(Path.Combine(local, "cache", "local.txt"), "ignored subtree");
        Directory.CreateDirectory(Path.Combine(local, "report.tmp"));
        await File.WriteAllTextAsync(Path.Combine(local, "report.tmp", "allowed.txt"), "directory is included");
        remote.Put("cloud.tmp", "keep in cloud"); remote.Put("cloud.txt", "download me");
        var observed = await new SyncAuthorityObservationCoordinator(pair, remote, db, "ignore-local", "ignore-dropbox").ObserveAsync("manual");
        var ignored = await db.LoadIgnoredPathsAsync(pair.Id, rules);
        Require(ignored.Contains("gone.tmp") && ignored.Contains("baseline.tmp"), "Ignore evidence includes retained history even when both sides are absent");
        var current = await new SyncAuthorityReconciliationPlanner(db, options with { ConfigurationRevision = 2 }, admission: admission).PlanAsync(pair.Id);
        foreach (var item in current.Where(item => item.OperationId is not null))
            Console.WriteLine("PLANNED " + item.RelativePath + " " + item.Kind);
        Require(current.Where(item => item.OperationId is not null).All(item =>
            !SyncIgnorePolicy.IsIgnored(item.RelativePath, Directory.Exists(Path.Combine(local, item.RelativePath)), rules)), "Real planner emits no effect for ignored local, remote or historical items");
        await db.SetPairStateAsync(pair.Id, "active");
        await new SyncAuthorityPairRunner(pair, remote, db, "ignore-local", "ignore-dropbox",
            options with { ConfigurationRevision = 2 }, new PilotRunControl(), admission: admission).RunAsync("manual");
        Require(remote.Exists("keep.txt") && File.Exists(Path.Combine(local, "cloud.txt")), "Nonmatching files still sync in both directions through the real runner");
        Require(remote.Exists("baseline.tmp") && !File.Exists(Path.Combine(local, "baseline.tmp")), "Ignored missing local file does not delete or re-download its cloud copy");
        Require(!remote.Exists("new.tmp") && !remote.Exists("Thumbs.db") && !remote.Exists("cache/local.txt") &&
            !File.Exists(Path.Combine(local, "cloud.tmp")), "Ignored files and subtree contents are neither uploaded nor downloaded");
        Require(remote.Exists("report.tmp/allowed.txt"), "Directory ending in an ignored file type remains functional");
        Require(File.ReadAllText(Path.Combine(local, "pending.tmp")) == "not dispatched yet" &&
            File.ReadAllText(Path.Combine(local, "cache", "local.txt")) == "ignored subtree", "Adding rules retains existing local bytes");
        var otherAccountPair = pair with { Id = "other-account-pair", ConnectionId = "account-second", IgnoreRules = [] };
        Require(SyncSelectionPolicy.IsIncluded("pending.tmp", otherAccountPair), "Rules belong to one pair and do not leak to another account");
        var restored = JsonSerializer.Deserialize<SyncPair>(JsonSerializer.Serialize(pair))!;
        Require(SyncIgnorePolicy.Normalize(restored.IgnoreRules).SequenceEqual(rules), "Ignore rules survive pair persistence");
        Require(observed.LocalFilesHashed <= 3, "Ignored file contents are not opened or hashed during inventory");
    }

    private static void Require(bool condition, string label)
    { if (!condition) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
}
