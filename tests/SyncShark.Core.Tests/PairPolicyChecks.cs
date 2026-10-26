using Massdex.SyncShark;
using System.Text.Json;

internal static class PairPolicyChecks
{
    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native pair policy checks require Windows.");
        foreach (var direction in Enum.GetValues<SyncMode>())
        {
            var folder = Path.Combine(root, direction.ToString()); var local = Path.Combine(folder, "local");
            Directory.CreateDirectory(local);
            await File.WriteAllTextAsync(Path.Combine(local, "computer.txt"), "only on this computer");
            var remote = new FakeSyncRemoteStore(); remote.Put("cloud.txt", "only in the cloud");
            PilotDirectoryIdentity identity;
            using (var lease = PilotDirectoryLease.Open(local)) identity = lease.Identity;
            var pair = new SyncPair("pair-policy", "Policy check", local, identity.VolumeSerialNumber,
                identity.FileId, "/Policy", "id:policy", Direction: direction);
            using (var selected = PilotDirectoryLease.Open(local))
                pair = pair with { ControlNamespace = new SyncControlNamespaceBinding("policy-installation", "policy-namespace",
                    SyncControlNamespace.BindLocalForTest(selected, "policy-installation", "policy-namespace")) };
            var db = new SyncAuthorityDatabase(Path.Combine(folder, "authority.db"));
            await db.InitializeAsync("policy-installation", "policy-profile", "policy-check", "{}");
            await db.RegisterPairAsync(pair.Id, 1);
            await db.RegisterRootBindingAsync("policy-local", pair.Id, "local", 1,
                JsonSerializer.Serialize(new { identity.VolumeSerialNumber, identity.FileId }), local);
            await db.RegisterRootBindingAsync("policy-dropbox", pair.Id, "dropbox", 1,
                "{\"folderId\":\"id:policy\"}", pair.DropboxRoot);
            var observer = new SyncAuthorityObservationCoordinator(pair, remote, db, "policy-local", "policy-dropbox");
            await observer.ObserveAsync("initial"); await observer.ObserveAsync("initial");
            await new SyncAuthorityInitialPlanner(db).AdoptAsync(pair.Id);
            var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
                null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
            await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
            var options = new SyncAuthorityPlannerOptions(1, "policy-profile", "policy-control", "policy-control", Direction: direction);
            var results = await new SyncAuthorityReconciliationPlanner(db, options, admission: admission).PlanAsync(pair.Id);
            var retainedState = await db.LoadPairStateAsync(pair.Id);
            var pendingIds = results.Where(item => item.OperationId is not null)
                .Select(item => item.OperationId!).ToArray();
            Require(pendingIds.Length > 0, "Real planner supplies retained nonterminal policy authority");
            var pendingBefore = await Task.WhenAll(pendingIds.Select(id => db.LoadOperationAsync(id)));
            await db.RegisterPairAsync(pair.Id, 1);
            Require(await db.LoadPairStateAsync(pair.Id) == retainedState &&
                JsonSerializer.Serialize(await Task.WhenAll(pendingIds.Select(id => db.LoadOperationAsync(id)))) ==
                    JsonSerializer.Serialize(pendingBefore),
                "Idempotent same-revision registration preserves pair state and pending operation authority");
            foreach (var liveState in new[] { "paused", "blocked", "active", "discovery_required" })
            {
                await db.SetPairStateAsync(pair.Id, liveState);
                await db.RegisterPairAsync(pair.Id, 1);
                Require(await db.LoadPairStateAsync(pair.Id) == liveState,
                    "Same-revision registration preserves the exact " + liveState + " state");
            }
            await db.SetPairStateAsync(pair.Id, retainedState);
            await ExpectPolicyRejectionAsync(() => db.RegisterPairAsync(pair.Id, 2),
                "A changed revision still refuses retained nonterminal work");
            await ExpectPolicyRejectionAsync(() => db.RegisterPairAsync(pair.Id, 1,
                new SyncAuthorityPairConfiguration("recoverable", 300, 2, "manual_preserve_both", "content_only",
                    RecursiveMode: false, AutomaticPruning: false, RecoveryRetentionDays: 30)),
                "The unchanged-revision fast path still rejects immutable policy drift");
            using (var stopped = new CancellationTokenSource())
            {
                stopped.Cancel();
                try { await db.RegisterPairAsync(pair.Id, 1, stopped.Token); throw new InvalidOperationException("Cancelled policy registration was accepted."); }
                catch (OperationCanceledException) { }
            }
            Require(await db.LoadPairStateAsync(pair.Id) == retainedState,
                "Rejected revision/policy/cancellation do not alter pair state");
            foreach (var item in results)
            {
                var allowed = direction == SyncMode.TwoWay || (direction == SyncMode.UploadOnly ? item.RelativePath == "computer.txt" : item.RelativePath == "cloud.txt");
                Require(item.Kind == (allowed ? SyncAuthorityPlannerOutcomeKind.Operation : SyncAuthorityPlannerOutcomeKind.Skipped),
                    direction + " produces the correct immutable operation/skip for " + item.RelativePath);
                if (!allowed) Require(item.OperationId is null && item.Reason.Contains("no synchronization point"), "Skipped is not a transfer or an accepted baseline");
            }
            Require(results.Count == 2, "Both directions remain visible in retained decisions");
            await db.SetPairStateAsync(pair.Id, "active");
            var executed = await new SyncAuthorityPairRunner(pair, remote, db, "policy-local", "policy-dropbox",
                options, new PilotRunControl(), admission: admission).RunAsync("manual");
            Require(remote.Exists("computer.txt") == (direction != SyncMode.DownloadOnly) &&
                File.Exists(Path.Combine(local, "cloud.txt")) == (direction != SyncMode.UploadOnly),
                direction + " executes only the permitted direction through the real pair runner");
            Require(File.ReadAllText(Path.Combine(local, "computer.txt")) == "only on this computer" && remote.Exists("cloud.txt"),
                "One-way mode preserves destination-only data; it is not a destructive mirror");
            foreach (var kind in new[] { SyncAuthorityOperationKind.CreateLocal, SyncAuthorityOperationKind.UpdateLocal,
                SyncAuthorityOperationKind.DeleteLocal, SyncAuthorityOperationKind.MoveLocal })
                Require(SyncModePolicy.Allows(direction, kind) == (direction != SyncMode.UploadOnly), direction + " local effect fence: " + kind);
            foreach (var kind in new[] { SyncAuthorityOperationKind.CreateRemote, SyncAuthorityOperationKind.UpdateRemote,
                SyncAuthorityOperationKind.DeleteRemote, SyncAuthorityOperationKind.MoveRemote })
                Require(SyncModePolicy.Allows(direction, kind) == (direction != SyncMode.DownloadOnly), direction + " cloud effect fence: " + kind);
            var storage = pair with { FilesOnDemandEnabled = true, DirectoryStorageRules = [
                new("Work", SyncDirectoryStorage.AlwaysAvailable), new("Work/Archive", SyncDirectoryStorage.CloudOnly)] };
            Require(SyncDirectoryStoragePolicy.Resolve(storage, "Work/new.txt") == SyncDirectoryStorage.AlwaysAvailable, "Future child inherits offline policy");
            Require(SyncDirectoryStoragePolicy.Resolve(storage, "Work/Archive/new.txt") == SyncDirectoryStorage.CloudOnly, "Child exception overrides parent storage policy");
            Require(SyncDirectoryStoragePolicy.Resolve(storage, "Workshops/new.txt") == SyncDirectoryStorage.CloudOnly, "Storage policy matches path boundaries, not prefixes");
            Require(SyncSelectionPolicy.IsIncluded("Work/Archive/new.txt", storage.SelectionRules, storage.SelectionDefaultIncluded), "Cloud-only storage does not deselect synchronization");
            var restored = JsonSerializer.Deserialize<SyncPair>(JsonSerializer.Serialize(storage))!;
            Require(restored.Direction == direction && restored.DirectoryStorageRules?.Count == 2, "Direction and directory policies survive persistence");
            var native = SyncDirectoryStoragePolicy.AdoptExplorerChoices(storage,
                [new("Work", 7, "id:work", false)]);
            Require(SyncDirectoryStoragePolicy.Resolve(native, "Work/future.txt") == SyncDirectoryStorage.CloudOnly &&
                native.DirectoryStorageApplyPaths!.Contains("Work"), "An explicit Explorer folder choice becomes a persistent future-child policy");
            Require(SyncDirectoryStoragePolicy.AdoptExplorerChoices(native, [new("Work", 7, "id:work", true)]) == native,
                "A native state still transitioning from an app choice cannot overwrite the pending draft");
            // Exercise the production retirement/re-registration branch, not a
            // substituted policy guard. This DB has no effects or pending work.
            var lifecycle = new SyncAuthorityDatabase(Path.Combine(folder, "policy-lifecycle.db"));
            await lifecycle.InitializeAsync("policy-installation", "policy-profile", "policy-check", "{}");
            await lifecycle.RegisterPairAsync(pair.Id, 1);
            var lifecyclePair = pair with { ControlNamespace = null };
            await lifecycle.RetainPairDefinitionAsync(lifecyclePair, true);
            await lifecycle.RetirePairDefinitionAsync(pair.Id);
            Require(await lifecycle.LoadPairStateAsync(pair.Id) == "retired", "Production retirement fixture is retained");
            await lifecycle.RegisterPairAsync(pair.Id, 1);
            Require(await lifecycle.LoadPairStateAsync(pair.Id) == "discovery_required",
                "An unencumbered retired pair still takes the original guarded revival branch");
            await lifecycle.RegisterPairAsync(pair.Id, 2);
            await lifecycle.RetainPairDefinitionAsync(lifecyclePair with { PolicyRevision = 2 }, true);
            Require((await lifecycle.LoadPairSetAsync()).Pairs.Single().PolicyRevision == 2,
                "An unencumbered new policy revision still registers and publishes through the real lifecycle");
        }
    }
    private static async Task ExpectPolicyRejectionAsync(Func<Task> operation, string label)
    {
        try { await operation(); }
        catch (InvalidOperationException) { Console.WriteLine("PASS " + label); return; }
        throw new InvalidOperationException(label + " was incorrectly accepted.");
    }
    private static void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
}
