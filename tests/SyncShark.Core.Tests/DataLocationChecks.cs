using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

internal static class DataLocationChecks
{
    private static readonly string[] CrashPhases = ["journal-flushed", "journal", "copy-open", "copy-file", "publish-journal-flushed", "verified", "publish-entry", "publish-entry-second", "pointer-flushed", "marker", "pointer", "cleanup-file", "cleanup-file-second", "cleanup-directory", "cleanup-evidence", "cleanup-lock", "cleanup-journal", "cleanup-initial-consumed"];

    internal static async Task RunAsync(string root)
    {
        using var allocation = new OwnedAllocation();
        root = allocation.Path;
        var home = Path.Combine(root, "home");
        var away = Path.Combine(root, "away");
        Directory.CreateDirectory(home);
        using var isolated = allocation.Override(home);
        Directory.CreateDirectory(Path.Combine(home, "versions"));
        var installed = Path.Combine(home, "versions", "retained.txt");
        await File.WriteAllTextAsync(installed, "installer-owned");
        Directory.CreateDirectory(Path.Combine(home, "engine"));
        var database = Path.Combine(home, "engine", "state.db");
        var snapshot = new Dictionary<string, byte[]>();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE fixture(value TEXT); INSERT INTO fixture VALUES('retained');";
            await command.ExecuteNonQueryAsync();
            foreach (var path in new[] { database, database + "-wal", database + "-shm" })
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var copy = new MemoryStream();
                input.CopyTo(copy);
                snapshot[path] = copy.ToArray();
            }
        }
        // Restore the committed WAL snapshot without keeping a writer open during migration.
        foreach (var item in snapshot) await File.WriteAllBytesAsync(item.Key, item.Value);
        Require(snapshot[database + "-wal"].Length > 0, "engine fixture contains committed WAL bytes");
        SyncDataLocation.MoveResult outward;
        using (var authority = PilotInstanceLock.Acquire(Path.Combine(home, "state", "instance.lock")))
            outward = await SyncDataLocation.MoveAsync(authority.StateAuthority, away);
        SyncDataLocation.CleanupOldCopy(outward);
        Require(File.ReadAllText(SyncDataLocation.PointerPath).Trim() == away && File.Exists(installed),
            "outward move preserves installer files and flips pointer");
        var pointerBefore = File.ReadAllBytes(SyncDataLocation.PointerPath);
        await File.WriteAllTextAsync(SyncDataLocation.PointerPath, away.Replace('\\', '/').ToUpperInvariant() + "/");
        Require(string.Equals(SyncDataLocation.ReadProductRoot(), away, StringComparison.OrdinalIgnoreCase),
            "pointer normalizes forward slashes, case and trailing slash");
        await File.WriteAllBytesAsync(SyncDataLocation.PointerPath, pointerBefore);
        using (var authority = PilotInstanceLock.Acquire(Path.Combine(away, "state", "instance.lock").Replace('\\', '/')))
        {
            var same = await SyncDataLocation.MoveAsync(authority.StateAuthority, away.Replace('\\', '/').ToUpperInvariant() + "/");
            Require(!same.RestartRequired && string.Equals(authority.StateAuthority.ProductRoot, away, StringComparison.OrdinalIgnoreCase),
                "authority and user destination normalize equivalent path spellings");
            var foreign = Path.Combine(home, "foreign.txt");
            await File.WriteAllTextAsync(foreign, "foreign");
            await RejectAsync(() => SyncDataLocation.MoveAsync(authority.StateAuthority, home));
            Require(File.ReadAllText(foreign) == "foreign" && File.ReadAllBytes(SyncDataLocation.PointerPath).SequenceEqual(pointerBefore),
                "foreign default-home data is refused without pointer or file changes");
            File.Delete(foreign);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await SyncDataLocation.MoveAsync(authority.StateAuthority, home, cancelled.Token); throw new Exception("Cancellation accepted"); }
            catch (OperationCanceledException) { }
            Require(File.Exists(installed) && File.ReadAllBytes(SyncDataLocation.PointerPath).SequenceEqual(pointerBefore),
                "cancelled return preserves installer files and pointer");
            var collision = Path.Combine(away, "versions");
            Directory.CreateDirectory(collision);
            await File.WriteAllTextAsync(Path.Combine(collision, "foreign.txt"), "source");
            await RejectAsync(() => SyncDataLocation.MoveAsync(authority.StateAuthority, home));
            Require(File.ReadAllText(installed) == "installer-owned", "owned destination collision is never merged");
            Directory.Delete(collision, recursive: true);
        }
        SyncDataLocation.MoveResult returned;
        using (var authority = PilotInstanceLock.Acquire(Path.Combine(away, "state", "instance.lock")))
            returned = await SyncDataLocation.MoveAsync(authority.StateAuthority, home);
        Require(returned.RestartRequired && File.ReadAllText(SyncDataLocation.PointerPath).Trim() == home &&
            File.ReadAllText(Path.Combine(away, "MOVED-TO.txt")) == home && File.ReadAllText(installed) == "installer-owned",
            "return to default verifies data, preserves installation, flips pointer and retains move marker");
        SyncDataLocation.CleanupOldCopy(returned);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            Require((string?)await command.ExecuteScalarAsync() == "ok", "returned engine database integrity is intact");
            command.CommandText = "SELECT value FROM fixture;";
            Require((string?)await command.ExecuteScalarAsync() == "retained", "returned engine database retains rows");
        }
        Require(!Directory.Exists(Path.Combine(away, "engine")), "old copied data is removed only after successful move");
    }

    internal static async Task CrashChildAsync(string allocation, string token, string home, string source,
        string target, string phase, string direction)
    {
        using var isolated = SyncSharkRuntimePaths.OverrideProductRootForOwnedAllocationTest(home, allocation, token);
        source = Path.GetFullPath(source); target = Path.GetFullPath(target);
        if (!PilotPath.IsPathInside(allocation, source) || !PilotPath.IsPathInside(allocation, target) ||
            string.Equals(source, allocation, StringComparison.OrdinalIgnoreCase) || string.Equals(target, allocation, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source, SyncDataLocation.ReadProductRoot(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || !CrashPhases.Contains(phase) || direction is not ("outward" or "return") ||
            (direction == "outward" ? !string.Equals(source, home, StringComparison.OrdinalIgnoreCase) : !string.Equals(target, home, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Crash-child paths, pointer or phase do not match the owned move.");
        SyncDataLocation.ValidateDirectory(source); SyncDataLocation.ValidateDirectory(target);
        var occurrence = 0;
        var boundary = phase.EndsWith("-second", StringComparison.Ordinal) ? phase[..^7] : phase;
        SyncDataLocation.MoveCrashPointForTest = point =>
        {
            if (point != boundary || ++occurrence != (boundary == phase ? 1 : 2)) return;
            File.WriteAllText(Path.Combine(allocation, "killed-at.txt"), direction + ":" + phase);
            System.Diagnostics.Process.GetCurrentProcess().Kill();
            Thread.Sleep(Timeout.Infinite);
        };
        SyncDataLocation.MoveResult result;
        using (var authority = PilotInstanceLock.AcquireForMaintenance(Path.Combine(source, "state", "instance.lock")))
            result = await SyncDataLocation.MoveAsync(authority.StateAuthority, target);
        SyncDataLocation.CleanupOldCopy(result);
        throw new Exception("Crash point was not reached: " + phase);
    }

    internal static async Task RecoveryAsync(string root)
    {
        _ = Path.GetFullPath(root); // Keep the public harness argument validation; owned data lives on the TEMP NTFS volume.
        foreach (var direction in new[] { "outward", "return" })
        foreach (var phase in CrashPhases)
        {
            using var allocation = new OwnedAllocation();
            var home = Path.Combine(allocation.Path, "home");
            var away = Path.Combine(allocation.Path, "away");
            var source = direction == "outward" ? home : away;
            var target = direction == "outward" ? away : home;
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(Path.Combine(home, "empty"));
            await SeedWalAsync(home);
            // A second ordinary file makes both cleanup-file and publish-entry second occurrences reachable.
            await File.WriteAllTextAsync(Path.Combine(home, "notes.txt"), "retained text");
            Directory.CreateDirectory(Path.Combine(home, "versions"));
            var installationFiles = new[] { Path.Combine(home, "versions", "keep.txt"), Path.Combine(home, "active", "keep.txt"),
                Path.Combine(home, "replacement-staging", "transactions", "keep.txt") };
            foreach (var installationFile in installationFiles)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(installationFile)!);
                await File.WriteAllTextAsync(installationFile, "installer");
            }
            using var isolated = allocation.Override(home);
            if (direction == "return")
            {
                SyncDataLocation.MoveResult outward;
                using (var preparation = PilotInstanceLock.AcquireForMaintenance(Path.Combine(home, "state", "instance.lock")))
                    outward = await SyncDataLocation.MoveAsync(preparation.StateAuthority, away);
                Require(SyncDataLocation.CleanupOldCopy(outward).Contains("Old runtime data removed"), "public outward move prepares return crash fixture");
            }
            var exit = await RunCrashChildAsync(allocation.Path, allocation.Token, home, source, target, phase, direction);
            Require(exit != 0 && File.ReadAllText(Path.Combine(allocation.Path, "killed-at.txt")) == direction + ":" + phase,
                "child killed at " + direction + ":" + phase);
            var current = SyncDataLocation.ReadProductRoot();
            SyncDataLocation.MoveResult? result = null;
            using (var authority = AcquireWithMoveRecovery(current))
                if (current == source) result = await SyncDataLocation.MoveAsync(authority.StateAuthority, target);
            if (result is not null) Require(SyncDataLocation.CleanupOldCopy(result).Contains("Old runtime data removed"), "retry cleanup completes");
            await VerifyWalAsync(target);
            Require(SyncDataLocation.ReadProductRoot() == target && installationFiles.All(path => File.ReadAllText(path) == "installer") &&
                Directory.Exists(Path.Combine(target, "empty")) && !Directory.Exists(Path.Combine(source, "engine")) &&
                !File.Exists(Path.Combine(home, "data-move.json")) && !File.Exists(Path.Combine(home, "data-move.json.tmp")) &&
                !File.Exists(SyncDataLocation.PointerPath + ".move.tmp") &&
                !Directory.EnumerateDirectories(allocation.Path, ".syncshark-data-move-*").Any(),
                "kill recovery preserves one authoritative WAL copy and installation at " + direction + ":" + phase);
        }
        CheckOriginalOverrideContract();
        await CrashChildConfinementAsync();
        await SourceConstraintsAsync();
        await TopologyRefusalsAsync();
        await PublicationJunctionAsync();
        await RefusalsAsync();
        await CliOutcomesAsync();
    }

    // The destructive-path rows that run without elevation or a GUI surface: stage deletion identity, the journal
    // temporary preimage, the marker/startup and catch recovery routes, a mutated counterpart after restart, and
    // cleanup held by one process while another asks to move.
    internal static async Task TargetedRecoveryAsync(string? only = null)
    {
        (string Name, Func<Task> Run)[] cases =
        [
            ("stage", StageSubstitutionAsync),
            ("journal-temp", JournalTemporaryIdentityAsync),
            ("marker", MarkerRecoveryAsync),
            ("counterpart", CounterpartMutationRestartAsync),
            ("cross-process", CrossProcessCleanupAsync),
        ];
        foreach (var item in cases.Where(item => only is null || item.Name == only)) await item.Run();
    }

    private static async Task<SyncDataLocation.MoveResult> CommitMoveAsync(string home, string target)
    {
        using var owner = PilotInstanceLock.AcquireForMaintenance(Path.Combine(home, "state", "instance.lock"));
        return await SyncDataLocation.MoveAsync(owner.StateAuthority, target);
    }

    private static async Task StageSubstitutionAsync()
    {
        using var allocation = new OwnedAllocation();
        var home = Path.Combine(allocation.Path, "home");
        var target = Path.Combine(allocation.Path, "away");
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(Path.Combine(home, "payload.bin"), "retained source");
        using var isolated = allocation.Override(home);
        var substitute = Path.Combine(allocation.Path, "substituted-stage");
        var blocked = false;
        var heldDuringPublication = false;
        SyncDataLocation.MoveCrashPointForTest = point =>
        {
            if (point is not ("publish-entry" or "stage-delete")) return;
            var stage = Directory.EnumerateDirectories(allocation.Path, ".syncshark-data-move-*").Single();
            try { Directory.Move(stage, substitute); }
            catch (IOException) { if (point == "publish-entry") heldDuringPublication = true; else blocked = true; }
        };
        SyncDataLocation.MoveResult moved;
        try { moved = await CommitMoveAsync(home, target); }
        finally { SyncDataLocation.MoveCrashPointForTest = null; }
        Require(heldDuringPublication && blocked && !Directory.Exists(substitute) && !Directory.EnumerateDirectories(allocation.Path, ".syncshark-data-move-*").Any() &&
            File.ReadAllText(Path.Combine(target, "payload.bin")) == "retained source" &&
            File.ReadAllText(SyncDataLocation.PointerPath) == target + Environment.NewLine,
            "the held stage cannot be renamed in the publication window or at deletion; a substitute cannot displace it");
        Require(SyncDataLocation.CleanupOldCopy(moved).Contains("Old runtime data removed"), "stage-deletion control leaves ordinary cleanup complete");
    }

    private static async Task JournalTemporaryIdentityAsync()
    {
        using var allocation = new OwnedAllocation();
        var home = Path.Combine(allocation.Path, "home");
        var target = Path.Combine(allocation.Path, "away");
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(Path.Combine(home, "payload.bin"), "retained source");
        using var isolated = allocation.Override(home);
        await CommitMoveAsync(home, target);
        var journal = Path.Combine(home, "data-move.json");
        var temporary = journal + ".tmp";
        var publishing = await File.ReadAllBytesAsync(temporary);
        // A temporary that is not the publishing preimage cannot be reconciled; both preimages stay untouched.
        await File.WriteAllBytesAsync(temporary, await File.ReadAllBytesAsync(journal));
        Require(RecoveryRefused(target) && File.Exists(journal) && File.Exists(temporary) &&
            File.ReadAllText(Path.Combine(home, "payload.bin")) == "retained source",
            "recovery refuses a journal temporary that is not the publishing preimage");
        // An initial temporary that never published cannot own a stage directory.
        File.Delete(journal);
        File.Delete(SyncDataLocation.PointerPath);
        File.Delete(Path.Combine(home, "MOVED-TO.txt"));
        var stage = Path.GetFullPath(System.Text.Json.JsonDocument.Parse(File.ReadAllText(temporary)).RootElement.GetProperty("Stage").GetString()!);
        Directory.CreateDirectory(stage);
        Require(RecoveryRefused(home) && Directory.Exists(stage),
            "an unpublished journal temporary with a stage is refused and the stage is retained");
        Directory.Delete(stage);
        await File.WriteAllTextAsync(SyncDataLocation.PointerPath, target + Environment.NewLine);
        await File.WriteAllTextAsync(Path.Combine(home, "MOVED-TO.txt"), target);
        await File.WriteAllBytesAsync(temporary, publishing);
        RecoverMarker(target);
        Require(!File.Exists(journal) && !File.Exists(temporary) && !File.Exists(Path.Combine(home, "MOVED-TO.txt")) &&
            !File.Exists(Path.Combine(home, "payload.bin")) && File.Exists(Path.Combine(target, "payload.bin")),
            "the exact publishing preimage replays and consumes the recovery evidence");
    }

    private static bool RecoveryRefused(string target)
    {
        try { RecoverMarker(target); return false; }
        catch (IOException) { return true; }
    }

    private static void RecoverMarker(string target)
    {
        using var owner = PilotInstanceLock.AcquireForMaintenance(Path.Combine(target, "state", "instance.lock"));
        SyncDataLocation.CheckMoveMarker(owner.StateAuthority);
    }

    private static async Task MarkerRecoveryAsync()
    {
        using var allocation = new OwnedAllocation();
        using var foreign = new OwnedAllocation();
        var home = Path.Combine(allocation.Path, "home");
        var target = Path.Combine(allocation.Path, "away");
        Directory.CreateDirectory(home);
        using var isolated = allocation.Override(home);
        var marker = Path.Combine(home, "MOVED-TO.txt");
        await File.WriteAllTextAsync(marker, target);
        Require(RecoveryRefused(home) && File.ReadAllText(marker) == target,
            "a move marker without its journal cannot prove ownership and is retained");
        File.Delete(marker);
        RecoverMarker(home);
        Require(!Path.Exists(marker) && !File.Exists(Path.Combine(home, "data-move.json")), "a startup check with no move is a no-op");
        await File.WriteAllTextAsync(Path.Combine(home, "payload.bin"), "retained source");
        var failures = 0;
        SyncDataLocation.MoveCrashPointForTest = point =>
        {
            if (point != "verified") return;
            CreateNativeLinkAsync("/J", target, foreign.Path).GetAwaiter().GetResult();
            failures++;
        };
        var refused = false;
        try { await CommitMoveAsync(home, target); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { refused = true; }
        finally { SyncDataLocation.MoveCrashPointForTest = null; }
        if (Directory.Exists(target)) Directory.Delete(target);
        var journal = Path.Combine(home, "data-move.json");
        Require(refused && failures == 1 && File.Exists(journal) && File.ReadAllText(Path.Combine(home, "payload.bin")) == "retained source",
            "a failed move leaves its own recovery evidence rather than deleting by path");
        RecoverMarker(home);
        Require(!File.Exists(journal) && !File.Exists(journal + ".tmp") && !Directory.EnumerateDirectories(allocation.Path, ".syncshark-data-move-*").Any() &&
            !File.Exists(SyncDataLocation.PointerPath) && File.ReadAllText(Path.Combine(home, "payload.bin")) == "retained source",
            "the failed attempt's journal and stage are reconciled by the same recovery path");
    }

    private static async Task CounterpartMutationRestartAsync()
    {
        using var allocation = new OwnedAllocation();
        var home = Path.Combine(allocation.Path, "home");
        var target = Path.Combine(allocation.Path, "away");
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(Path.Combine(home, "payload.bin"), "retained source");
        using var isolated = allocation.Override(home);
        await CommitMoveAsync(home, target);
        var copy = Path.Combine(target, "payload.bin");
        var original = await File.ReadAllBytesAsync(copy);
        await File.WriteAllTextAsync(copy, "changed counterpart");
        Require(RecoveryRefused(target) && File.ReadAllText(Path.Combine(home, "payload.bin")) == "retained source" &&
            File.Exists(Path.Combine(home, "data-move.json")) && File.Exists(Path.Combine(home, "MOVED-TO.txt")) &&
            File.ReadAllText(copy) == "changed counterpart",
            "a changed authoritative counterpart after restart is refused; the old copy and evidence are retained");
        await File.WriteAllBytesAsync(copy, original);
        RecoverMarker(target);
        Require(!File.Exists(Path.Combine(home, "payload.bin")) && !File.Exists(Path.Combine(home, "data-move.json")) &&
            !File.Exists(Path.Combine(home, "MOVED-TO.txt")) && File.ReadAllText(copy) == "retained source",
            "a restored counterpart lets restart recovery finish the retained cleanup");
    }

    private static async Task CrossProcessCleanupAsync()
    {
        using var allocation = new OwnedAllocation();
        var home = Path.Combine(allocation.Path, "home");
        var target = Path.Combine(allocation.Path, "away");
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(Path.Combine(home, "payload.bin"), "retained source");
        using var isolated = allocation.Override(home);
        var moved = await CommitMoveAsync(home, target);
        var ready = Path.Combine(allocation.Path, "cleanup-ready");
        var release = Path.Combine(allocation.Path, "cleanup-release");
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var argument in new[] { "--data-location-destination-hold", allocation.Path, allocation.Token, home, target })
            start.ArgumentList.Add(argument);
        using var child = System.Diagnostics.Process.Start(start)!;
        try
        {
            for (var attempt = 0; attempt < 600 && !File.Exists(ready); attempt++) await Task.Delay(100);
            Require(File.Exists(ready), "a second process holds the committed destination authority");
            var cleanupRefused = SyncDataLocation.CleanupOldCopy(moved).Contains("cleanup incomplete", StringComparison.OrdinalIgnoreCase);
            var moveRefused = false;
            try
            {
                using var owner = PilotInstanceLock.AcquireForMaintenance(Path.Combine(target, "state", "instance.lock"));
                await SyncDataLocation.MoveAsync(owner.StateAuthority, Path.Combine(allocation.Path, "third"));
            }
            catch (IOException) { moveRefused = true; }
            Require(cleanupRefused && moveRefused && File.Exists(Path.Combine(home, "payload.bin")) && File.Exists(Path.Combine(home, "data-move.json")),
                "while another process owns the destination, cleanup and a new move are both refused and the old copy and evidence are retained");
            await File.WriteAllTextAsync(release, string.Empty);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            Require(child.ExitCode == 0 && SyncDataLocation.CleanupOldCopy(moved).Contains("Old runtime data removed") &&
                !File.Exists(Path.Combine(home, "payload.bin")) && !File.Exists(Path.Combine(home, "data-move.json")) &&
                !File.Exists(Path.Combine(home, "MOVED-TO.txt")) && File.ReadAllText(Path.Combine(target, "payload.bin")) == "retained source",
                "the retained cleanup completes once the competing process releases the destination");
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    }

    // Holds the committed destination authority from a second process until the parent releases it.
    internal static Task DestinationHoldChildAsync(string allocation, string token, string home, string target)
    {
        allocation = Path.GetFullPath(allocation); home = Path.GetFullPath(home); target = Path.GetFullPath(target);
        using var isolated = SyncSharkRuntimePaths.OverrideProductRootForOwnedAllocationTest(home, allocation, token);
        if (!PilotPath.IsPathInside(allocation, home) || !PilotPath.IsPathInside(allocation, target) ||
            !string.Equals(SyncDataLocation.ReadProductRoot(), target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The destination-hold child does not match the owned move.");
        using var owner = PilotInstanceLock.AcquireForMaintenance(Path.Combine(target, "state", "instance.lock"));
        File.WriteAllText(Path.Combine(allocation, "cleanup-ready"), string.Empty);
        while (!File.Exists(Path.Combine(allocation, "cleanup-release"))) Thread.Sleep(50);
        return Task.CompletedTask;
    }

    private static void CheckOriginalOverrideContract()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var method = typeof(SyncSharkRuntimePaths).GetMethod("OverrideProductRootForTest", flags)!;
        Require(method.GetParameters() is [{ ParameterType: var type }] && type == typeof(string), "original name-only override lookup retains its single string signature");
        var root = Path.Combine(Path.GetTempPath(), "installer55-override-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var isolated = (IDisposable)method.Invoke(null, [root])!;
            Require(SyncSharkRuntimePaths.ProductRoot == Path.GetFullPath(root), "original name-only override can still be invoked");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root); }
        var rejected = false;
        try { _ = method.Invoke(null, [Path.GetPathRoot(Path.GetTempPath())!]); }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is InvalidDataException) { rejected = true; }
        Require(rejected, "original name-only override still refuses a root outside temporary storage");
        var ambiguous = false;
        try { _ = typeof(OverloadedOverride).GetMethod("OverrideProductRootForTest", flags); }
        catch (System.Reflection.AmbiguousMatchException) { ambiguous = true; }
        Require(ambiguous, "synthetic same-name overload fails the name-only consumer contract");
    }

    private static class OverloadedOverride
    {
        private static void OverrideProductRootForTest(string root) { }
        private static void OverrideProductRootForTest(string root, string token) { }
    }

    private static async Task<int> RunCrashChildAsync(string allocation, string token, string home, string source, string target, string phase, string direction)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var arg in new[] { "--data-location-crash-child", allocation, token, home, source, target, phase, direction })
            start.ArgumentList.Add(arg);
        using var child = System.Diagnostics.Process.Start(start)!;
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
        catch { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } throw; }
        return child.ExitCode;
    }

    private static async Task CrashChildConfinementAsync()
    {
        using var allocation = new OwnedAllocation();
        using var outside = new OwnedAllocation();
        var home = Path.Combine(allocation.Path, "home");
        var away = Path.Combine(allocation.Path, "away");
        Directory.CreateDirectory(home);
        var sentinel = Path.Combine(outside.Path, "retained.txt");
        await File.WriteAllTextAsync(sentinel, "untouched");
        foreach (var paths in new[] { (outside.Path, away), (home, outside.Path), (away, home),
            (home, allocation.Path), (Path.Combine(allocation.Path, "..", Path.GetFileName(outside.Path)), away) })
        {
            var exit = await RunCrashChildAsync(allocation.Path, allocation.Token, home, paths.Item1, paths.Item2, "journal", "outward");
            Require(exit != 0 && !File.Exists(Path.Combine(allocation.Path, "killed-at.txt")) && File.ReadAllText(sentinel) == "untouched" && !Directory.Exists(Path.Combine(outside.Path, "state")) &&
                !Directory.Exists(Path.Combine(home, "state")) && !Directory.Exists(away) &&
                !File.Exists(Path.Combine(home, "data-move.json")), "crash child refuses outside paths and mismatched fresh pointer before effects");
        }
        foreach (var arguments in new[] { ("wrong-token", "journal", "outward"), (allocation.Token, "unknown-phase", "outward"),
            (allocation.Token, "journal", "unknown-direction"), (allocation.Token, "journal", "return") })
        {
            var exit = await RunCrashChildAsync(allocation.Path, arguments.Item1, home, home, away, arguments.Item2, arguments.Item3);
            Require(exit != 0 && !Directory.Exists(Path.Combine(home, "state")) && !Directory.Exists(away) &&
                !File.Exists(Path.Combine(allocation.Path, "killed-at.txt")), "public crash-child entry refuses token, phase and direction mismatch before effects");
        }
        foreach (var link in new[] { away, home })
        {
            if (link == home) Directory.Delete(home);
            await CreateNativeLinkAsync("/J", link, outside.Path);
            try
            {
                var exit = await RunCrashChildAsync(allocation.Path, allocation.Token, home, home, away, "journal", "outward");
                Require(exit != 0 && !Directory.Exists(Path.Combine(outside.Path, "state")) &&
                    !File.Exists(Path.Combine(allocation.Path, "killed-at.txt")), "public crash-child entry refuses unsafe source or target ancestry before effects");
            }
            finally { Directory.Delete(link); if (link == home) Directory.CreateDirectory(home); }
        }
        var receipt = Path.Combine(allocation.Path, ".installer55-owner");
        await RejectAsync(() => { OwnedAllocation.CreateReceipt(allocation.Path, "replacement"); return Task.CompletedTask; }, typeof(IOException));
        Require(File.ReadAllText(receipt) == allocation.Token, "existing ownership receipt is refused without replacement");
    }

    private static async Task PublicationJunctionAsync()
    {
        using var allocation = new OwnedAllocation();
        using var foreignAllocation = new OwnedAllocation();
        var home = Path.Combine(allocation.Path, "home");
        var target = Path.Combine(allocation.Path, "away");
        Directory.CreateDirectory(home);
        var original = Path.Combine(home, "payload.bin");
        await File.WriteAllTextAsync(original, "retained source");
        using var isolated = allocation.Override(home);
        using var owner = PilotInstanceLock.AcquireForMaintenance(Path.Combine(home, "state", "instance.lock"));
        SyncDataLocation.MoveCrashPointForTest = point =>
        {
            if (point == "verified") CreateNativeLinkAsync("/J", target, foreignAllocation.Path).GetAwaiter().GetResult();
        };
        try
        {
            var refused = false;
            try { await SyncDataLocation.MoveAsync(owner.StateAuthority, target); }
            catch (Exception error) when (error is IOException or InvalidDataException) { refused = true; }
            Require(refused && File.ReadAllText(original) == "retained source" && !File.Exists(SyncDataLocation.PointerPath) &&
                Directory.EnumerateFileSystemEntries(foreignAllocation.Path).Select(Path.GetFileName).SequenceEqual([".installer55-owner"]),
                "junction inserted after verification receives no payload and source remains authoritative");
        }
        finally { SyncDataLocation.MoveCrashPointForTest = null; if (Directory.Exists(target)) Directory.Delete(target); }
        SyncDataLocation.CheckMoveMarker(owner.StateAuthority);
        Require(!File.Exists(Path.Combine(home, "data-move.json")) && !File.Exists(Path.Combine(home, "data-move.json.tmp")),
            "publication-junction refusal can reconcile after the junction is removed");
        var blocked = false;
        var pointerEvidenceBlocked = false;
        var pointerReplacement = Path.Combine(allocation.Path, "pointer-replacement");
        await File.WriteAllTextAsync(pointerReplacement, target + Environment.NewLine);
        SyncDataLocation.MoveCrashPointForTest = point =>
        {
            if (point == "pointer-flushed")
            {
                var writeBlocked = false;
                var replaceBlocked = false;
                try { File.WriteAllBytes(SyncDataLocation.PointerPath + ".move.tmp", []); }
                catch (IOException) { writeBlocked = true; }
                try { File.Move(pointerReplacement, SyncDataLocation.PointerPath + ".move.tmp", overwrite: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { replaceBlocked = true; }
                pointerEvidenceBlocked = writeBlocked && replaceBlocked;
            }
            if (point != "verified") return;
            var stage = Directory.EnumerateDirectories(allocation.Path, ".syncshark-data-move-*").Single();
            try { Directory.Move(stage, Path.Combine(allocation.Path, "substituted-stage")); }
            catch (IOException) { blocked = true; }
            Require(blocked, "verified stage root cannot be renamed before publication");
        };
        try { await SyncDataLocation.MoveAsync(owner.StateAuthority, target); }
        finally { SyncDataLocation.MoveCrashPointForTest = null; }
        Require(blocked && File.ReadAllText(Path.Combine(target, "payload.bin")) == "retained source" &&
            !File.Exists(Path.Combine(foreignAllocation.Path, "payload.bin")), "stage publication retains exact root binding without outside effects");
        Require(pointerEvidenceBlocked && File.ReadAllText(SyncDataLocation.PointerPath) == target + Environment.NewLine,
            "pointer temporary rejects same-object writes and identical-byte identity replacement before commit");
    }

    private static async Task SourceConstraintsAsync()
    {
        using var allocation = new OwnedAllocation();
        var home = Path.Combine(allocation.Path, "home");
        var target = Path.Combine(allocation.Path, "away");
        Directory.CreateDirectory(home);
        using var isolated = allocation.Override(home);
        var original = Path.Combine(home, "payload.bin");
        var alias = Path.Combine(allocation.Path, "alias.bin");
        await File.WriteAllTextAsync(original, "retained source");
        using var owner = PilotInstanceLock.AcquireForMaintenance(Path.Combine(home, "state", "instance.lock"));
        await CreateNativeLinkAsync("/H", alias, original);
        async Task RefusesBeforePublication(string label)
        {
            var refused = false;
            try { await SyncDataLocation.MoveAsync(owner.StateAuthority, target); }
            catch (IOException) { refused = true; }
            Require(refused && File.ReadAllText(original) == "retained source" &&
                !File.Exists(SyncDataLocation.PointerPath) && !File.Exists(Path.Combine(home, "data-move.json")) &&
                !Directory.Exists(target) && !Directory.EnumerateDirectories(allocation.Path, ".syncshark-data-move-*").Any(), label);
        }
        await RefusesBeforePublication("hard-linked source is refused before publication or copy effects");
        Require(File.ReadAllText(alias) == "retained source", "hard-link refusal preserves the outside alias");
        File.Delete(alias);
        var attributes = File.GetAttributes(original);
        try
        {
            File.SetAttributes(original, attributes | FileAttributes.Offline);
            Require((File.GetAttributes(original) & FileAttributes.Offline) != 0, "offline source attribute control is set");
            await RefusesBeforePublication("offline source is refused before publication or copy effects");
        }
        finally { File.SetAttributes(original, attributes); }
    }

    private static async Task TopologyRefusalsAsync()
    {
        using var allocation = new OwnedAllocation();
        using var foreign = new OwnedAllocation();
        var home = Path.Combine(allocation.Path, "home");
        var target = Path.Combine(allocation.Path, "away");
        Directory.CreateDirectory(home);
        var sentinel = Path.Combine(foreign.Path, "retained.txt");
        await File.WriteAllTextAsync(sentinel, "outside retained");
        using var isolated = allocation.Override(home);
        using var owner = PilotInstanceLock.AcquireForMaintenance(Path.Combine(home, "state", "instance.lock"));
        foreach (var kind in new[] { "junction", "symbolic-link" })
        foreach (var sourceSide in new[] { true, false })
        {
            var link = Path.Combine(sourceSide ? home : allocation.Path, "linked");
            if (kind == "junction") await CreateNativeLinkAsync("/J", link, foreign.Path);
            else if (!CreateSymbolicLinkW(link, foreign.Path, 3))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1314) throw new IOException("Owned symbolic-link capability probe failed with Windows error " + error);
                Console.WriteLine("UNAVAILABLE symbolic-link topology: Windows error 1314 (unprivileged creation refused); no elevation attempted.");
                break;
            }
            try
            {
                var refused = false;
                try { await SyncDataLocation.MoveAsync(owner.StateAuthority, sourceSide ? target : Path.Combine(link, "away")); }
                catch (Exception error) when (error is IOException or InvalidDataException) { refused = true; }
                Require(refused && File.ReadAllText(sentinel) == "outside retained" &&
                    !File.Exists(SyncDataLocation.PointerPath) && !File.Exists(Path.Combine(home, "data-move.json")) &&
                    !Directory.Exists(Path.Combine(foreign.Path, "away")) && !Directory.Exists(target),
                    "public move refuses " + kind + (sourceSide ? " nested source" : " destination ancestor") + " before outside effects");
            }
            finally { Directory.Delete(link); }
        }
    }

    private static async Task CliOutcomesAsync()
    {
        using var allocation = new OwnedAllocation();
        var home = Path.Combine(allocation.Path, "home");
        var target = Path.Combine(allocation.Path, "away");
        Directory.CreateDirectory(home);
        using var isolated = allocation.Override(home);
        await SeedWalAsync(home);
        async Task<System.Text.Json.JsonElement> Invoke(string destination)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var settings = System.Text.Json.JsonSerializer.Serialize(new { dataLocation = destination });
            var exit = await SyncCli.ExecuteAsync(["--cli", "settings", "set", "--settings", settings, "--yes", "--json"],
                stdout, stderr, () => SyncCliRuntime.Acquire(Path.Combine(SyncDataLocation.ReadProductRoot(), "state", "instance.lock")), default);
            Require(exit == 0, "actual data-location CLI succeeds: " + stderr);
            using var document = System.Text.Json.JsonDocument.Parse(stdout.ToString());
            return document.RootElement.GetProperty("data").Clone();
        }
        var same = await Invoke(home);
        Require(!same.GetProperty("restartRequired").GetBoolean(), "actual CLI same-root result remains a no-op");
        Require(!File.Exists(SyncDataLocation.PointerPath) && !File.Exists(Path.Combine(home, "data-move.json")),
            "actual CLI same-root result creates no pointer or cleanup journal");
        var foreign = Path.Combine(home, "engine", "foreign.txt");
        SyncDataLocation.MoveCrashPointForTest = point => { if (point == "pointer") File.WriteAllText(foreign, "foreign-retained"); };
        System.Text.Json.JsonElement moved;
        try { moved = await Invoke(target); }
        finally { SyncDataLocation.MoveCrashPointForTest = null; }
        Require(moved.GetProperty("restartRequired").GetBoolean() && moved.GetProperty("effectsApplied").GetBoolean() &&
            moved.GetProperty("dataLocation").GetString() == target &&
            moved.GetProperty("effectsNote").GetString()!.Contains("cleanup incomplete", StringComparison.OrdinalIgnoreCase) &&
            SyncDataLocation.ReadProductRoot() == target && File.Exists(foreign),
            "actual CLI preserves committed location, effects and restart when cleanup fails");
        File.Delete(foreign);
        using (var owner = AcquireWithMoveRecovery(target))
            Require(!File.Exists(Path.Combine(home, "data-move.json")), "actual CLI released source authority before target cleanup/recovery");
        PilotInstanceLock? busyTarget = null;
        SyncDataLocation.MoveCrashPointForTest = point =>
        {
            if (point == "pointer") busyTarget = PilotInstanceLock.AcquireForMaintenance(Path.Combine(home, "state", "instance.lock"));
        };
        try
        {
            var returned = await Invoke(home);
            Require(returned.GetProperty("restartRequired").GetBoolean() && returned.GetProperty("effectsApplied").GetBoolean() &&
                returned.GetProperty("dataLocation").GetString() == home &&
                returned.GetProperty("effectsNote").GetString()!.Contains("cleanup incomplete", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(home, "data-move.json")) && Directory.Exists(Path.Combine(target, "engine")),
                "actual CLI busy destination retains committed result and recoverable source evidence");
        }
        finally { SyncDataLocation.MoveCrashPointForTest = null; busyTarget?.Dispose(); }
        using (var owner = AcquireWithMoveRecovery(home))
            Require(!File.Exists(Path.Combine(home, "data-move.json")) && !Directory.Exists(Path.Combine(target, "engine")),
                "target authority release and startup recovery resolve the actual CLI busy-target refusal");
    }

    private static async Task SeedWalAsync(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "engine"));
        var database = Path.Combine(root, "engine", "state.db");
        var snapshot = new Dictionary<string, byte[]>();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE fixture(value TEXT); INSERT INTO fixture VALUES('retained');";
            await command.ExecuteNonQueryAsync();
            foreach (var path in new[] { database, database + "-wal", database + "-shm" })
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var copy = new MemoryStream();
                input.CopyTo(copy);
                snapshot[path] = copy.ToArray();
            }
        }
        Require(snapshot[database + "-wal"].Length > 0, "fixture has a committed SQLite WAL");
        foreach (var entry in snapshot) await File.WriteAllBytesAsync(entry.Key, entry.Value);
    }

    private static async Task VerifyWalAsync(string root)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "engine", "state.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Require((string?)await command.ExecuteScalarAsync() == "ok", "recovered database passes integrity check");
        command.CommandText = "SELECT value FROM fixture;";
        Require((string?)await command.ExecuteScalarAsync() == "retained", "recovered database retains committed WAL row");
    }

    private static async Task RefusalsAsync()
    {
        using var allocation = new OwnedAllocation();
        var home = Path.Combine(allocation.Path, "home");
        var target = Path.Combine(allocation.Path, "away");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(Path.Combine(home, "empty"));
        using var isolated = allocation.Override(home);
        await SeedWalAsync(home);
        SyncDataLocation.MoveResult moved;
        using (var owner = PilotInstanceLock.AcquireForMaintenance(Path.Combine(home, "state", "instance.lock")))
            moved = await SyncDataLocation.MoveAsync(owner.StateAuthority, target);
        var oldDatabase = Path.Combine(home, "engine", "state.db");
        var oldBytes = File.ReadAllBytes(oldDatabase);
        var newDatabase = Path.Combine(target, "engine", "state.db");
        var newBytes = File.ReadAllBytes(newDatabase);
        await File.WriteAllTextAsync(newDatabase, "changed-new-copy");
        Require(SyncDataLocation.CleanupOldCopy(moved).Contains("cleanup incomplete", StringComparison.OrdinalIgnoreCase) &&
            File.ReadAllBytes(oldDatabase).SequenceEqual(oldBytes), "changed authoritative bytes never authorize old-copy deletion");
        await File.WriteAllBytesAsync(newDatabase, newBytes);
        await File.WriteAllTextAsync(oldDatabase, "changed-old-copy");
        Require(SyncDataLocation.CleanupOldCopy(moved).Contains("cleanup incomplete", StringComparison.OrdinalIgnoreCase) &&
            File.ReadAllText(oldDatabase) == "changed-old-copy", "changed old bytes are retained with recovery evidence");
        await File.WriteAllBytesAsync(oldDatabase, oldBytes);
        var foreignFile = Path.Combine(home, "engine", "foreign.txt");
        await File.WriteAllTextAsync(foreignFile, "foreign-retained");
        Require(SyncDataLocation.CleanupOldCopy(moved).Contains("cleanup incomplete", StringComparison.OrdinalIgnoreCase) &&
            File.ReadAllText(foreignFile) == "foreign-retained" && File.ReadAllBytes(oldDatabase).SequenceEqual(oldBytes),
            "foreign source entries fail full preflight before any deletion");
        File.Delete(foreignFile);
        var targetEngine = Path.Combine(target, "engine");
        var preservedEngine = Path.Combine(allocation.Path, "preserved-engine");
        Directory.Move(targetEngine, preservedEngine);
        await CreateNativeLinkAsync("/J", targetEngine, preservedEngine);
        try
        {
            Require(SyncDataLocation.CleanupOldCopy(moved).Contains("cleanup incomplete", StringComparison.OrdinalIgnoreCase) &&
                File.ReadAllBytes(oldDatabase).SequenceEqual(oldBytes), "intermediate destination junction never authorizes source deletion");
        }
        finally { Directory.Delete(targetEngine); Directory.Move(preservedEngine, targetEngine); }
        // A junction that resolves back inside the destination is still a reparse object in the manifest ancestry.
        var insideTarget = Path.Combine(target, "inside-preserved");
        Directory.Move(targetEngine, insideTarget);
        await CreateNativeLinkAsync("/J", targetEngine, insideTarget);
        try
        {
            Require(SyncDataLocation.CleanupOldCopy(moved).Contains("cleanup incomplete", StringComparison.OrdinalIgnoreCase) &&
                File.ReadAllBytes(oldDatabase).SequenceEqual(oldBytes), "destination junction resolving inside the destination is refused");
        }
        finally { Directory.Delete(targetEngine); Directory.Move(insideTarget, targetEngine); }
        // An empty manifest directory replaced by a junction is refused in preflight, before any source deletion.
        var preservedEmpty = Path.Combine(allocation.Path, "preserved-empty");
        Directory.Move(Path.Combine(target, "empty"), preservedEmpty);
        await CreateNativeLinkAsync("/J", Path.Combine(target, "empty"), preservedEmpty);
        try
        {
            Require(SyncDataLocation.CleanupOldCopy(moved).Contains("cleanup incomplete", StringComparison.OrdinalIgnoreCase) &&
                File.ReadAllBytes(oldDatabase).SequenceEqual(oldBytes), "empty destination directory junction is refused before deletion");
        }
        finally { Directory.Delete(Path.Combine(target, "empty")); Directory.Move(preservedEmpty, Path.Combine(target, "empty")); }
        Directory.Delete(Path.Combine(target, "empty"));
        Require(SyncDataLocation.CleanupOldCopy(moved).Contains("cleanup incomplete", StringComparison.OrdinalIgnoreCase) &&
            File.ReadAllBytes(oldDatabase).SequenceEqual(oldBytes), "missing empty destination directory preserves all old files");
        Directory.CreateDirectory(Path.Combine(target, "empty"));
        using (var writer = OpenDirectoryWriter(targetEngine))
        {
            Require(!writer.IsInvalid, "authoritative directory write-open control is permitted before cleanup");
            Require(SyncDataLocation.CleanupOldCopy(moved).Contains("cleanup incomplete", StringComparison.OrdinalIgnoreCase) &&
                File.ReadAllBytes(oldDatabase).SequenceEqual(oldBytes), "third-party directory writer retains evidence for retry");
        }
        var blocked = false;
        var counterpartBlocked = false;
        var emptyCounterpartBlocked = false;
        var evidenceAttempts = 0;
        var marker = Path.Combine(home, "MOVED-TO.txt");
        var protectedEvidence = new[] { Path.Combine(home, "data-move.json"), Path.Combine(home, "data-move.json.tmp"),
            marker, Path.Combine(home, "state", "instance.lock") };
        var replacements = protectedEvidence.ToDictionary(path => path, path =>
        {
            var replacement = Path.Combine(allocation.Path, "replacement-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(replacement, File.ReadAllBytes(path));
            return replacement;
        });
        SyncDataLocation.MoveCrashPointForTest = point =>
        {
            if (point == "cleanup-preflight")
                foreach (var path in protectedEvidence)
                {
                    var writeBlocked = false; var replacementBlocked = false;
                    try { File.WriteAllBytes(path, []); } catch (IOException) { writeBlocked = true; }
                    try { File.Move(replacements[path], path, overwrite: true); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { replacementBlocked = true; }
                    Require(writeBlocked && replacementBlocked, "held evidence rejects same-object write and identical-byte replacement: " + Path.GetFileName(path));
                    evidenceAttempts++;
                }
            if (point == "cleanup-directory" && !emptyCounterpartBlocked && !Directory.Exists(Path.Combine(home, "engine")) &&
                Directory.Exists(Path.Combine(home, "empty")))
            {
                try { Directory.Move(Path.Combine(target, "empty"), Path.Combine(allocation.Path, "replaced-empty")); }
                catch (IOException) { emptyCounterpartBlocked = true; }
                Require(emptyCounterpartBlocked, "empty counterpart remains bound through old-directory disposition");
            }
            if (point == "cleanup-before-file" && !counterpartBlocked)
            {
                using var writer = OpenDirectoryWriter(targetEngine);
                counterpartBlocked = writer.IsInvalid && Marshal.GetLastWin32Error() == 32;
                Require(counterpartBlocked, "actual cleanup counterpart chain excludes directory writers through deletion");
            }
            if (point != "cleanup-before-evidence") return;
            try { File.WriteAllText(marker, "foreign"); }
            catch (IOException) { blocked = true; }
        };
        try { Require(SyncDataLocation.CleanupOldCopy(moved).Contains("Old runtime data removed") && blocked && counterpartBlocked &&
            emptyCounterpartBlocked && evidenceAttempts == protectedEvidence.Length,
            "evidence and empty counterparts remain bound through exact-handle consumption"); }
        finally
        {
            SyncDataLocation.MoveCrashPointForTest = null;
            foreach (var replacement in replacements.Values) if (File.Exists(replacement)) File.Delete(replacement);
        }
        var third = Path.Combine(allocation.Path, "third");
        SyncDataLocation.MoveResult next;
        using (var owner = PilotInstanceLock.AcquireForMaintenance(Path.Combine(target, "state", "instance.lock")))
            next = await SyncDataLocation.MoveAsync(owner.StateAuthority, third);
        var journalPath = Path.Combine(home, "data-move.json");
        var journal = File.ReadAllBytes(journalPath);
        Require(SyncDataLocation.CleanupOldCopy(moved).Contains("cleanup incomplete", StringComparison.OrdinalIgnoreCase) &&
            File.ReadAllBytes(journalPath).SequenceEqual(journal) && Directory.Exists(Path.Combine(target, "engine")),
            "stale A-to-B result cannot consume B-to-C recovery evidence");
        Require(SyncDataLocation.CleanupOldCopy(next).Contains("Old runtime data removed"), "current cleanup resolves retained evidence");
        Require(SyncDataLocation.CleanupOldCopy(next).Contains("No pending cleanup"), "missing journal does not imply cleanup completion");
        var failure = new IOException("literal {location} {oldRoot}");
        Require(SyncDataLocation.DescribeMoveFailure(null, failure).StartsWith("Data location was not changed.") &&
            SyncDataLocation.DescribeMoveFailure(new(third, false, "same"), failure).Contains("no move was performed") &&
            SyncDataLocation.DescribeMoveFailure(next, failure).Contains("literal {location} {oldRoot}"),
            "three-state failure reporting and one-pass substitutions preserve literal braces");
    }

    private static async Task CreateNativeLinkAsync(string kind, string path, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", kind, path, target }) start.ArgumentList.Add(argument);
        using var link = System.Diagnostics.Process.Start(start)!;
        await link.WaitForExitAsync();
        Require(link.ExitCode == 0, "owned unprivileged NTFS link created: " + kind);
    }

    private static SafeFileHandle OpenDirectoryWriter(string path) => CreateFileW(path, 0x40000000,
        FileShare.Read | FileShare.Write, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CreateSymbolicLinkW(string path, string target, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare sharing,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    private sealed class OwnedAllocation : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetPathRoot(System.IO.Path.GetTempPath())!, "SyncSharkTest", "installer55-" + Guid.NewGuid().ToString("N"));
        internal string Token { get; } = Guid.NewGuid().ToString("N");
        private readonly PilotDirectoryIdentity _receiptIdentity;
        private readonly PilotDirectoryIdentity _rootIdentity;
        private readonly long _availableBytes;
        internal OwnedAllocation()
        {
            var parentPath = System.IO.Path.GetDirectoryName(Path)!;
            System.IO.Directory.CreateDirectory(parentPath);
            using var parents = new PilotDirectoryLease.MigrationChain(parentPath);
            using var parent = PilotDirectoryLease.OpenMigrationDirectory(parentPath);
            if (File.Exists(Path) || Directory.Exists(Path)) throw new IOException("The owned allocation already exists.");
            _availableBytes = new DriveInfo(System.IO.Path.GetPathRoot(Path)!).AvailableFreeSpace;
            if (_availableBytes <= 0) throw new IOException("No fixture space is available.");
            Console.WriteLine("Owned small-fixture available bytes: " + _availableBytes);
            using var root = PilotDirectoryLease.CreateChild(parent, System.IO.Path.GetFileName(Path), hidden: false);
            root.VerifyMigrationBinding();
            _rootIdentity = root.Identity;
            CreateReceipt(Path, Token);
            var receiptPath = System.IO.Path.Combine(Path, ".installer55-owner");
            using var receipt = SyncLocalFileAuthority.OpenExistingReadGuard(receiptPath);
            _receiptIdentity = receipt.VerifyMigrationFile(receiptPath);
        }
        internal static void CreateReceipt(string allocation, string token)
        {
            using var receipt = new StreamWriter(new FileStream(System.IO.Path.Combine(allocation, ".installer55-owner"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None));
            receipt.Write(token);
        }
        internal IDisposable Override(string home) => SyncSharkRuntimePaths.OverrideProductRootForOwnedAllocationTest(home, Path, Token);
        public void Dispose()
        {
            using var parents = new PilotDirectoryLease.MigrationChain(System.IO.Path.GetDirectoryName(Path)!);
            using var root = PilotDirectoryLease.OpenMigrationDirectory(Path, delete: true);
            if (root.Identity != _rootIdentity) throw new IOException("Owned allocation root changed; cleanup refused.");
            var receiptPath = System.IO.Path.Combine(Path, ".installer55-owner");
            using (var receipt = SyncLocalFileAuthority.OpenExistingExclusive(receiptPath))
            {
                receipt.VerifyMigrationFile(receiptPath); receipt.VerifySameSingleLink(_receiptIdentity);
                using (var reader = new StreamReader(receipt.Stream, leaveOpen: true))
                    if (reader.ReadToEnd() != Token) throw new IOException("Owned allocation receipt changed; cleanup refused.");
                var held = new List<IDisposable>();
                var removals = new List<Action>();
                long bytes = 0;
                void Inspect(string directory)
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        if (entry == receiptPath) continue;
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Ambiguous fixture link retained; cleanup refused.");
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            var child = PilotDirectoryLease.OpenMigrationDirectory(entry, delete: true);
                            held.Add(child);
                            Inspect(entry);
                            removals.Add(() => { child.DeleteOnClose(); child.Dispose(); });
                        }
                        else
                        {
                            var file = SyncLocalFileAuthority.OpenExistingExclusive(entry);
                            held.Add(file);
                            file.VerifyMigrationFile(entry);
                            bytes = checked(bytes + file.Stream.Length);
                            if (bytes > _availableBytes) throw new IOException("Owned fixture exceeded its observed available space; cleanup refused.");
                            removals.Add(() => { file.DeleteOnClose(); file.Dispose(); });
                        }
                    }
                }
                try
                {
                    Inspect(Path);
                    Console.WriteLine("Owned small-fixture cleanup inventory: " + bytes + " bytes; " + held.Count + " child handles.");
                    parents.Verify(); root.VerifyMigrationBinding(); receipt.VerifySameSingleLink(_receiptIdentity);
                    foreach (var remove in removals) remove();
                }
                finally
                {
                    for (var index = held.Count - 1; index >= 0; index--) held[index].Dispose();
                }
                receipt.DeleteOnClose();
            }
            root.DeleteOnClose();
        }
    }

    private static PilotInstanceLock AcquireWithMoveRecovery(string root)
    {
        var authority = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        try
        {
            // The synthetic-root seam skips normal startup; exercise its exact recovery hook under the lease.
            SyncDataLocation.CheckMoveMarker(authority.StateAuthority);
            return authority;
        }
        catch { authority.Dispose(); throw; }
    }

    private static async Task RejectAsync(Func<Task> action, Type? expected = null)
    {
        try { await action(); }
        catch (Exception error) when ((expected ?? typeof(InvalidDataException)).IsInstanceOfType(error)) { return; }
        throw new Exception("Unsafe data-location destination was accepted.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
}
