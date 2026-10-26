using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Extends the retained R697 clone; it never copies, resets or opens live application data.
// HostWire supplies provider input only. CLI/Host/database/planner/effects are production code.
internal static class MixedClonedJourneyChecks
{
    internal static async Task RunAsync(SyncAuthorityRuntimeHost host, SyncAuthorityDatabase database,
        CloudPairHostBindingChecks.HostWire wire, DropboxConnectionBinding binding, SyncPair parent,
        string local, Func<string[], Task<(int Exit, string Out, string Err)>> cli)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native folder identity proof requires Windows.");
        void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidDataException(message);
            Console.WriteLine("PASS mixed clone: " + message);
        }
        async Task<string> Command(params string[] arguments)
        {
            var result = await cli(arguments);
            Console.WriteLine(JsonSerializer.Serialize(new { command = string.Join(" ", arguments.Take(3)),
                exit = result.Exit, output = result.Out, error = result.Err }));
            if (result.Exit != 0) throw new InvalidDataException("Mixed journey command failed: " + result.Err);
            return result.Out;
        }
        async Task<long> Receipts()
        {
            using var connection = new SqliteConnection("Data Source=" + database.DatabasePath + ";Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM effect_receipt WHERE certainty='known_success';";
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        var original = Directory.GetFiles(local).ToDictionary(path => Path.GetFileName(path)!,
            File.ReadAllBytes, StringComparer.Ordinal);
        Check(parent.Direction == SyncMode.TwoWay && parent.InitialReviewApproved == true,
            "retained completed parent remains true TwoWay; no history or approval reset");
        var childLocal = Path.Combine(local, "Child");
        Directory.CreateDirectory(childLocal);
        using var childLease = PilotDirectoryLease.Open(childLocal);
        var childCloud = parent.DropboxRoot.TrimEnd('/') + "/Child";
        const string childFolderId = "id:host-mixed-clone-child";
        wire.AddDropboxFolder(binding, childCloud, childFolderId);
        var childDraft = SyncPair.FromPilot(new PilotSettings(childLocal,
            childLease.Identity.VolumeSerialNumber, childLease.Identity.FileId, childCloud, childFolderId),
            "Child overlap fixture", binding.RootNamespaceId) with
        { ConnectionId = binding.ConnectionId, Direction = SyncMode.TwoWay, Enabled = false,
            PropagateDeletions = false };
        // Use the same explicit, namespace-bound endpoint authoring boundary as the UI.
        // The legacy document-less binder cannot prove a nested cloud partition.
        childDraft = SyncEndpointPairDraft.Create(new SyncPairEndpoints(
            SyncEndpoint.Local(childLease.Path, new SyncLocalEndpointIdentity(
                childLease.Identity.VolumeSerialNumber, childLease.Identity.FileId)),
            SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, binding.ConnectionId,
                binding.RootNamespaceId, childFolderId, childCloud), SyncMode.TwoWay),
            childDraft.Name, childDraft);
        var control = PilotPath.NormalizeRelative(Path.GetRelativePath(local,
            SyncControlNamespace.LocalRootFor(childLocal, childDraft.Id)));
        var rules = (parent.SelectionRules ?? []).Where(rule =>
            !string.Equals(rule.FolderPath, "Child", StringComparison.OrdinalIgnoreCase)).ToList();
        if (!rules.Any(rule => string.Equals(rule.FolderPath, control, StringComparison.OrdinalIgnoreCase)))
            rules.Add(new SyncSelectionRule(control, false));
        var child = (await host.LoadPairsAsync()).Pairs.SingleOrDefault(pair => pair.LocalRoot == childLocal);
        if (child is null)
        {
            // Establish the existing-child starting state through ordinary product policy,
            // before removing the parent exclusion. Never register an unexcluded new overlap.
            var initiallyExcluded = rules.Append(new SyncSelectionRule("Child", false)).ToArray();
            if (!SyncPairPartitionPolicy.ExcludesWholeSubtree(parent, "Child") ||
                !SyncPairPartitionPolicy.ExcludesWholeSubtree(parent, control))
            {
                await Command("--cli", "pairs", "edit", "--pair", parent.Id, "--account", binding.ConnectionId,
                    "--generation", parent.PolicyRevision.ToString(), "--settings",
                    JsonSerializer.Serialize(new { direction = "TwoWay", selectionRules = initiallyExcluded }),
                    "--enable", "--yes", "--json");
                parent = (await host.LoadPairsAsync()).Pairs.Single(pair => pair.Id == parent.Id);
            }
            else Console.WriteLine("PASS mixed clone: retained parent exclusion already established; do not replay its edit");
            var recovery = parent.RecoveryRoot ?? throw new InvalidDataException("Retained Recovery is missing.");
            child = (await host.BindEndpointPairAsync(childDraft,
                recoveryFolder: new DropboxFolderEntry("Recovery", recovery.RootPath,
                    recovery.RootFolderId))).Pair;
        }
        Check(child.Id == childDraft.Id && child.Direction == SyncMode.TwoWay,
            "nested TwoWay child is retained alongside the existing parent");
        var childBefore = JsonSerializer.Serialize(child);
        // A previous invocation may have committed this policy before its readback failed.
        // Reconcile the retained policy rather than replaying a completed authoring effect.
        if (!SyncSelectionPolicy.IsIncluded("Child", parent) ||
            !SyncPairPartitionPolicy.ExcludesWholeSubtree(parent, control))
        {
            await Command("--cli", "pairs", "edit", "--pair", parent.Id, "--account", binding.ConnectionId,
                "--generation", parent.PolicyRevision.ToString(), "--settings",
                JsonSerializer.Serialize(new { direction = "TwoWay", selectionRules = rules }),
                "--enable", "--yes", "--json");
        }
        else Console.WriteLine("PASS mixed clone: retained parent inclusion already committed; do not replay its edit");
        parent = (await host.LoadPairsAsync()).Pairs.Single(pair => pair.Id == parent.Id);
        Check(JsonSerializer.Serialize((await host.LoadPairsAsync()).Pairs.Single(pair => pair.Id == child.Id)) == childBefore,
            "including Child in the parent keeps the pre-existing child definition and revision unchanged");
        Check(SyncSelectionPolicy.IsIncluded("Child", parent) &&
            SyncPairPartitionPolicy.ExcludesWholeSubtree(parent, control),
            "parent includes Child DATA and excludes only the child's private control");
        var fixture = new[]
        {
            (Path: "journey-mixed-upload.txt", Local: "local-only upload", Cloud: (string?)null, Winner: "local"),
            (Path: "journey-mixed-conflict.txt", Local: "local authoritative outside", Cloud: (string?)"older cloud outside", Winner: "local"),
            (Path: "Child/journey-mixed-download.txt", Local: (string?)null, Cloud: (string?)"cloud-only download", Winner: "cloud"),
            (Path: "Child/journey-mixed-conflict.txt", Local: "older local inside", Cloud: (string?)"cloud authoritative inside", Winner: "cloud")
        };
        var existing = fixture.Count(item => File.Exists(Path.Combine(local,
            item.Path.Replace('/', Path.DirectorySeparatorChar))));
        if (existing != 0 && existing != fixture.Length)
            throw new InvalidDataException("Mixed fixture is partial; reconcile its exact outcomes before continuing.");
        var resumeCompletedTransfers = existing == fixture.Length;
        if (!resumeCompletedTransfers)
        {
        foreach (var item in fixture)
        {
            var file = Path.Combine(local, item.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file)) throw new InvalidDataException("Refuse to reset a prior mixed fixture: " + item.Path);
            if (item.Local is not null) await File.WriteAllTextAsync(file, item.Local, new UTF8Encoding(false));
            if (item.Cloud is not null) wire.AddDropboxFile(binding, parent.DropboxRoot.TrimEnd('/') + "/" + item.Path, item.Cloud);
        }
        await Command("--cli", "scan", "--pair", parent.Id, "--yes", "--json");
        // Observation alone is not reconciliation for an already-approved pair. The real
        // planner must retain conflicts while still transferring independent safe files.
        var initialRun = await cli(["--cli", "run", "--pair", parent.Id, "--yes", "--json"]);
        Console.WriteLine(JsonSerializer.Serialize(new { step = "mixed_initial_run",
            exit = initialRun.Exit, output = initialRun.Out, error = initialRun.Err }));
        Check(initialRun.Exit == 5, "ordinary run retains the two genuine conflicts as needs-attention");
        foreach (var item in fixture.Where(item => item.Local is null || item.Cloud is null))
        {
            var expected = item.Local ?? item.Cloud!;
            Check(await File.ReadAllTextAsync(Path.Combine(local, item.Path.Replace('/', Path.DirectorySeparatorChar))) == expected &&
                Encoding.UTF8.GetString(wire.ReadDropboxFixtureBytes(binding, parent.DropboxRoot.TrimEnd('/') + "/" + item.Path)) == expected,
                "independent safe file completed despite other conflicts: " + item.Path);
        }
        // Conflicts use the ordinary version/generation-guarded resolver, never a fixture winner override.
        foreach (var item in fixture.Where(item => item.Local is not null && item.Cloud is not null))
        {
            var shown = await Command("--cli", "conflicts", "show", "--pair", parent.Id, "--path", item.Path, "--json");
            using var document = JsonDocument.Parse(shown);
            var conflict = document.RootElement.GetProperty("data").GetProperty("conflict");
            await Command("--cli", "conflicts", "resolve", "--pair", parent.Id, "--path", item.Path,
                "--key", conflict.GetProperty("conflictId").GetString()!,
                "--generation", conflict.GetProperty("generation").GetInt64().ToString(),
                "--choice", item.Winner == "local" ? "KeepLocal" : "KeepDropbox", "--yes", "--json");
        }
        await Command("--cli", "run", "--pair", parent.Id, "--yes", "--json");
        }
        else
        {
            // Program restored provider input only from completed exact-path receipts/versions
            // whose sizes/hashes match these retained local files. Never replay their effects.
            Console.WriteLine("PASS mixed clone: completed fixture files retained; verifying bytes without replaying transfers or conflict choices");
            var retainedRecovery = await host.LoadRecoveryCatalogAsync(parent.Id);
            foreach (var item in fixture.Where(item => item.Local is not null && item.Cloud is not null && item.Winner == "local"))
            {
                var loser = Encoding.UTF8.GetBytes(item.Cloud!);
                var hash = DropboxContentHash.Compute(loser);
                var copy = retainedRecovery.Single(record => record.RelativePath == item.Path &&
                    record.Source == SyncRecoverySource.DropboxArchive && record.ContentHash == hash);
                if (copy.Size != loser.LongLength || copy.Identity is null || copy.Revision is null)
                    throw new InvalidDataException("Retained synthetic cloud Recovery identity is incomplete.");
                wire.RestoreDropboxFixtureFile(binding, copy.Location, copy.Identity, copy.Revision, hash, loser);
            }
        }
        foreach (var item in fixture)
        {
            var expected = item.Winner == "local" ? item.Local! : item.Cloud!;
            Check(await File.ReadAllTextAsync(Path.Combine(local, item.Path.Replace('/', Path.DirectorySeparatorChar))) == expected &&
                Encoding.UTF8.GetString(wire.ReadDropboxFixtureBytes(binding, parent.DropboxRoot.TrimEnd('/') + "/" + item.Path)) == expected,
                item.Path + " has exact " + item.Winner + "-winning bytes on both sides");
        }
        foreach (var item in original)
            Check(item.Value.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(local, item.Key!))),
                "earlier completed local file unchanged: " + item.Key);
        Check(JsonSerializer.Serialize((await host.LoadPairsAsync()).Pairs.Single(pair => pair.Id == child.Id)) == childBefore,
            "parent's real mixed effects did not change the retained child's definition or revision");
        // Child first sync sees the parent's completed effects; no duplicate upload/download is needed.
        if (!child.Enabled)
            await Command("--cli", "pairs", "edit", "--pair", child.Id, "--enable", "--yes", "--json");
        child = (await host.LoadPairsAsync()).Pairs.Single(pair => pair.Id == child.Id);
        if (child.InitialReviewApproved != true)
        {
            await Command("--cli", "first-sync", "prepare", "--pair", child.Id, "--yes", "--json");
            await Command("--cli", "first-sync", "apply", "--pair", child.Id, "--allow-recommended", "--yes", "--json");
        }
        var beforeRepeat = await Receipts();
        var pairs = (await host.LoadPairsAsync()).Pairs;
        var batch = await host.RunPairsAsync([pairs.Single(pair => pair.Id == parent.Id),
            pairs.Single(pair => pair.Id == child.Id)], "mixed overlap unchanged repeat");
        Check(batch.Items.Count == 2 && batch.Items.All(item => item.FailureType is null &&
            (item.Result?.Uploaded ?? 0) == 0 && (item.Result?.Downloaded ?? 0) == 0),
            "both overlapping pairs finish unchanged without duplicate transfers");
        Check(await Receipts() == beforeRepeat, "unchanged repeat adds zero successful effect receipts");
        var recoveryList = await Command("--cli", "recovery", "list", "--pair", parent.Id, "--json");
        using var recoveryDocument = JsonDocument.Parse(recoveryList);
        var catalog = await host.LoadRecoveryCatalogAsync(parent.Id);
        foreach (var item in fixture.Where(item => item.Local is not null && item.Cloud is not null))
        {
            var loser = Encoding.UTF8.GetBytes(item.Winner == "local" ? item.Cloud! : item.Local!);
            var hash = DropboxContentHash.Compute(loser);
            var retained = catalog.Single(copy => copy.RelativePath == item.Path && copy.ContentHash == hash);
            Check(recoveryDocument.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()
                .Any(copy => copy.GetProperty("key").GetString() == retained.Key &&
                    copy.GetProperty("contentHash").GetString() == hash && copy.GetProperty("canRestoreCopy").GetBoolean()),
                "CLI Recovery exposes the exact losing version and restore-copy action: " + item.Path);
            byte[] retainedBytes;
            if (retained.Source == SyncRecoverySource.LocalArchive)
            {
                var allocation = Path.GetFullPath(Path.Combine(local, "..")) + Path.DirectorySeparatorChar;
                Check(Path.GetFullPath(retained.Location).StartsWith(allocation, StringComparison.OrdinalIgnoreCase),
                    "local Recovery byte check stays inside the existing isolated journey");
                retainedBytes = await File.ReadAllBytesAsync(retained.Location);
            }
            else
            {
                Check(retained.Source == SyncRecoverySource.DropboxArchive, "cloud predecessor is retained in product Recovery");
                retainedBytes = wire.ReadDropboxFixtureBytes(binding, retained.Location);
            }
            Check(loser.SequenceEqual(retainedBytes), "exact replaced bytes remain recoverable: " + item.Path);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { parent_id = parent.Id, child_id = child.Id,
            child_retained_through_parent_run = true, role_mapping = "isolated Parent parent and nested Child fixture; not live pair identities" }));
        Console.WriteLine("COMPLETE mixed cloned journey: TwoWay parent, included nested child, opposite guarded winners, exact bytes, recovery and no-effect repeat. Synthetic provider only; not live acceptance.");
    }
}
