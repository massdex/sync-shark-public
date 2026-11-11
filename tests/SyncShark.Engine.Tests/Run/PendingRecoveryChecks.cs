using System.Globalization;
using System.Text;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Local;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Repair-start recovery of the pair's single held local operation.</summary>
public static class PendingRecoveryChecks
{
    private const string Original = "original";
    private const string Changed = "modified";
    private const string Target = "held";

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory(nameof(PendingRecoveryChecks)).FullName;
        var previous = LocalBin.SendOverride;
        try
        {
            LocalBin.SendOverride = path => { File.Delete(path); return true; };
            foreach (var (phase, changed) in new[]
            {
                (PendingPhase.Unknown, false), (PendingPhase.Retained, false),
                (PendingPhase.Armed, false), (PendingPhase.Armed, true)
            })
            {
                var folder = Directory.CreateDirectory(Path.Combine(root, $"{phase}-{changed}")).FullName;
                await CaseAsync(folder, phase, changed);
            }
            await LegacyAsync(Directory.CreateDirectory(Path.Combine(root, "legacy")).FullName);
            var diag = new List<string>();
            foreach (var phase in new[] { PendingPhase.Unknown, PendingPhase.Retained })
            foreach (var synchronized in new[] { false, true })
                try { await FullRepairsAsync(Directory.CreateDirectory(Path.Combine(root, $"full-{phase}-{synchronized}")).FullName,
                    phase, synchronized); Console.WriteLine($"DIAG PASS full-{phase}-{synchronized}"); }
                catch (Exception e) { diag.Add($"full-{phase}-{synchronized}"); Console.WriteLine($"DIAG FAIL full-{phase}-{synchronized}: {e}"); }
            try { await SettledAsync(Directory.CreateDirectory(Path.Combine(root, "settled")).FullName); Console.WriteLine("DIAG PASS settled"); }
            catch (Exception e) { diag.Add("settled"); Console.WriteLine("DIAG FAIL settled: " + e); }
            if (diag.Count > 0) throw new InvalidOperationException("DIAG failed: " + string.Join(",", diag));
            var message = Text.Get(TextKey.FailEarlierReplace, (TextArg.Path, Target));
            Check.Require(message == "Could not replace held: an earlier local operation is still unfinished. " +
                "Inspect its recovery notice and use recovery-resolve to choose AcceptCurrent or AbandonIncoming.",
                "The earlier-operation refusal names the recovery notice and recovery-resolve choices");
        }
        finally
        {
            LocalBin.SendOverride = previous;
            Directory.Delete(root, recursive: true);
        }
    }

    // Synchronized endpoints make the note's own row an in-sync result, whose closure must not end the notice (RG-2).
    private static async Task FullRepairsAsync(string folder, PendingPhase phase, bool synchronized)
    {
        await using var rig = await RepairChecks.ComposeRigAsync(folder);
        rig.Wire.SeedFile(Target, synchronized ? Original : "new content", DateTimeOffset.UnixEpoch.AddDays(1));
        PendingEffect? note = null;
        using var first = rig.Log(folder, "full-first");
        var result = await rig.RunAsync(first, CancellationToken.None, async (context, ct) =>
            note = await ArmAsync(context, phase, false, ct, await context.Dropbox.ReadOneAsync(Target, ct),
                synchronized ? Encoding.UTF8.GetBytes(Original) : null));
        Check.Require(result.Completed, "The held operation does not prevent the first full Repair completing");
        Check.Require(!synchronized || rig.Store.SyncedCount(rig.Pair.PairId) == 1,
            "setup: synchronized endpoints record the note's own row in sync");
        RequirePending(rig, note!, true);
        var issue = rig.Store.Issues(rig.Pair.PairId, [IssueKind.Failure]).Single();
        Check.Require(first.IssuesByKind.Values.Sum() == 1,
            "A full Repair announces the recovery notice once, not again for its refused replacement");
        Check.Require(rig.Store.SetIssueIgnored(rig.Pair.PairId, issue, true), "Ignore applies to the exact recovery notice");
        using var second = rig.Log(folder, "full-second");
        result = await rig.RunAsync(second, CancellationToken.None);
        Check.Require(result.Completed, "The second full Repair completes with the held operation still visible");
        RequirePending(rig, note!, true);
        var repeated = rig.Store.Issues(rig.Pair.PairId, [IssueKind.Failure]).Single();
        Check.Require(first.IssuesByKind.Values.Sum() + second.IssuesByKind.Values.Sum() == 1 &&
            repeated.Id == issue.Id && repeated.Cause == issue.Cause && repeated.Ignored,
            "Two full Repairs preserve the sole recovery event, cause, issue identity and Ignore");
    }

    // Stops at the scan boundary, so only the settling transaction itself can have closed the notice (RG-R2).
    private static async Task SettledAsync(string folder)
    {
        var reachedScan = false;
        await using var rig = await RepairChecks.ComposeRigAsync(folder, decorate: inner => new HookStore(inner)
        {
            BeforeOpenScan = side =>
            {
                if (side != Side.Local) return;
                reachedScan = true;
                throw new OperationCanceledException();
            }
        });
        PendingEffect? note = null;
        using (var first = rig.Log(folder, "settled-first"))
            await StartAsync(rig, first, () => reachedScan, async (context, ct) =>
                note = await ArmAsync(context, PendingPhase.Unknown, false, ct));
        RequirePending(rig, note!, true);
        reachedScan = false;
        using var second = rig.Log(folder, "settled-second");
        await StartAsync(rig, second, () => reachedScan, (context, _) =>
        {
            // A Removed replace whose target is gone publishes its intact stage at the next Repair's start.
            File.Delete(Path.Combine(context.Pair.LocalRoot, Target));
            context.Store.AdvancePending(context.Pair.PairId, note!,
                note! with { Generation = note.Generation + 1, Phase = PendingPhase.Removed });
            return Task.CompletedTask;
        });
        Check.Require(rig.Store.Pending(rig.Pair.PairId) is NoPending &&
            File.ReadAllText(Path.Combine(rig.Pair.LocalRoot, Target)) == "new content",
            "setup: Repair start publishes the stage and settles the note");
        Check.Require(rig.Store.Issues(rig.Pair.PairId, [IssueKind.Failure]).Count == 0,
            "RG-R2: the settling Repair closes the note's recovery notice with the note");
    }

    private static async Task LegacyAsync(string folder)
    {
        await using var rig = await RepairChecks.ComposeRigAsync(folder);
        rig.Wire.SeedFile(Target.ToUpperInvariant(), "new content", DateTimeOffset.UnixEpoch.AddDays(1));
        File.WriteAllText(Path.Combine(rig.Pair.LocalRoot, Target), Original);
        File.SetLastWriteTimeUtc(Path.Combine(rig.Pair.LocalRoot, Target), DateTime.UnixEpoch);
        using var setup = rig.Log(folder, "legacy-setup");
        var first = await rig.RunAsync(setup, CancellationToken.None, async (context, ct) =>
        {
            var source = (await context.Dropbox.ReadOneAsync(Target, ct))!;
            SeedLegacy(folder, rig, source);
        });
        var pending = rig.Store.Pending(rig.Pair.PairId);
        Check.Require(pending is LegacyPending legacy && legacy.Source.Path == Target.ToUpperInvariant(),
            "The historical note retains its differently cased Dropbox source");
        var issue = rig.Store.Issues(rig.Pair.PairId, [IssueKind.Failure]).Single();
        Check.Require(first.Completed && setup.IssuesByKind.Values.Sum() == 1 && issue.Cause == TextKey.RecoveryRetained && issue.Path == Target,
            "The full legacy Repair keeps exactly one Retained notice at the local spelling");
        Check.Require(rig.Store.SetIssueIgnored(rig.Pair.PairId, issue, true), "Ignore applies to the legacy recovery notice");
        using var second = rig.Log(folder, "legacy-second");
        var result = await rig.RunAsync(second, CancellationToken.None);
        var repeated = rig.Store.Issues(rig.Pair.PairId, [IssueKind.Failure]).Single();
        Check.Require(result.Completed && rig.Store.Pending(rig.Pair.PairId) == pending &&
            setup.IssuesByKind.Values.Sum() + second.IssuesByKind.Values.Sum() == 1 &&
            repeated.Id == issue.Id && repeated.Cause == issue.Cause && repeated.Ignored &&
            repeated.Path == Target && File.ReadAllText(Path.Combine(rig.Pair.LocalRoot, Target)) == Original,
            "Two full legacy Repairs preserve one notice, its ID, cause, spelling, Ignore and original bytes");
    }

    private static void SeedLegacy(string folder, RepairChecks.ComposedRig rig, Entry source)
    {
        var raw = System.Text.Json.JsonSerializer.Serialize(new
        {
            Path = Target, StagePath = Path.Combine(rig.Pair.LocalRoot, "legacy-stage"), Source = source
        });
        // Seed the historical encoding; the current public writer intentionally cannot create legacy notes.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = Path.Combine(folder, "composed.db"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE pair SET pending_json=$note WHERE pair_id=$pair";
            command.Parameters.AddWithValue("$note", raw);
            command.Parameters.AddWithValue("$pair", rig.Pair.PairId);
            command.ExecuteNonQuery();
        }
    }

    private static async Task CaseAsync(string folder, PendingPhase phase, bool changed)
    {
        // Stop at the existing scan boundary: no ordinary row processing can hide the startup result.
        var reachedScan = false;
        await using var rig = await RepairChecks.ComposeRigAsync(folder, decorate: inner => new HookStore(inner)
        {
            BeforeOpenScan = side =>
            {
                if (side != Side.Local) return;
                reachedScan = true;
                throw new OperationCanceledException();
            }
        });
        PendingEffect? note = null;
        using (var first = rig.Log(folder, "first"))
        {
            await StartAsync(rig, first, () => reachedScan, async (context, ct) =>
                note = await ArmAsync(context, phase, changed, ct));
            var held = phase != PendingPhase.Armed || changed;
            RequirePending(rig, note!, held);
            Check.Require(first.IssuesByKind.Values.Sum() == (held ? 1 : 0),
                "Repair start announces exactly one held-operation issue, or none for a settled Armed note");
        }
        var issue = rig.Store.Issues(rig.Pair.PairId, [IssueKind.Failure]).SingleOrDefault();
        using var second = rig.Log(folder, "second");
        reachedScan = false;
        await StartAsync(rig, second, () => reachedScan, (context, ct) =>
            ReplaceAsync(context, phase == PendingPhase.Armed && !changed, ct));
        Check.Require(second.IssuesByKind.Values.Sum() == 0,
            "Repeating Repair start does not announce the same recovery issue twice");
        if (issue is not null)
        {
            RequirePending(rig, note!, true);
            Check.Require(rig.Store.Issues(rig.Pair.PairId, [IssueKind.Failure]).Single().Id == issue.Id,
                "Repeated startup retains the identical notice rather than replacing it");
        }
    }

    private static async Task StartAsync(RepairChecks.ComposedRig rig, RunLog log, Func<bool> reachedScan,
        Func<ExecutorContext, CancellationToken, Task> before)
    {
        Exception? failure = null;
        try { await rig.RunAsync(log, CancellationToken.None, before); }
        catch (Exception error) { failure = error; }
        Check.Require(reachedScan(),
            "The real Repair reaches the post-recovery scan boundary; failure=" + failure?.ToString());
        Check.Require(Massdex.SyncShark.Engine.Tests.Executor.ChangeRunnerChecks.Cancelled(failure),
            "The real Repair exits by cancellation; failure=" + failure?.ToString());
    }

    private static async Task<PendingEffect> ArmAsync(ExecutorContext context, PendingPhase phase, bool changed,
        CancellationToken ct, Entry? incoming = null, byte[]? content = null)
    {
        var full = Path.Combine(context.Pair.LocalRoot, Target);
        File.WriteAllText(full, Original);
        if (incoming is not null) File.SetLastWriteTimeUtc(full, DateTime.UnixEpoch);
        var expected = (await context.Local.ReadOneAsync(Target, ct))!;
        var proof = new RemovalProof(expected,
            VolumeSpace.Serial(context.Pair.LocalRoot).ToString(CultureInfo.InvariantCulture),
            DropboxContentHash.Compute(Encoding.UTF8.GetBytes(Original)));
        var source = incoming ?? LocalWriterChecks.Source(Target);
        await using var stage = await LocalWriterChecks.StageAsync((LocalSide)context.Local, Target, source, content);
        var note = new PendingEffect(1, "held-operation", 0, PendingKind.LocalReplace, PendingPhase.Armed, Target,
            proof, null, source, stage.Proof, null, null, false, null);
        context.Store.ArmPending(context.Pair.PairId, note);
        if (phase != PendingPhase.Armed)
        {
            var next = note with { Generation = note.Generation + 1, Phase = phase };
            context.Store.AdvancePending(context.Pair.PairId, note, next);
            note = next;
        }
        if (changed)
        {
            File.WriteAllText(full, Changed);
            File.SetLastWriteTimeUtc(full, expected.Modified.UtcDateTime);
            var current = (await context.Local.ReadOneAsync(Target, ct))!;
            Check.Require(current.Id == expected.Id && current.Size == expected.Size &&
                current.Modified == expected.Modified && await context.Local.ContentHashAsync(current, ct) != proof.ContentHash,
                "The Armed mismatch changes only full content, not identity, size or timestamp");
        }
        return note;
    }

    private static void RequirePending(RepairChecks.ComposedRig rig, PendingEffect note, bool held)
    {
        var pending = rig.Store.Pending(rig.Pair.PairId);
        var issues = rig.Store.Issues(rig.Pair.PairId, [IssueKind.Failure]).ToArray();
        Check.Require(held ? pending == new CurrentPending(note) : pending is NoPending,
            "Repair retains the exact unsettled note and clears only the proved Armed note");
        Check.Require(File.Exists(note.Stage!.Path) == held,
            "Repair discards the owned stage only when it safely abandons Armed");
        Check.Require(held ? issues.Length == 1 && issues[0].Path == Target &&
            issues[0].Cause == (note.Phase == PendingPhase.Retained ? TextKey.RecoveryRetained : TextKey.RecoveryUnknown)
            : issues.Length == 0, "Held recovery has exactly its existing recovery reason");
        Check.Require(File.ReadAllText(Path.Combine(rig.Pair.LocalRoot, Target)) ==
            (held && note.Phase == PendingPhase.Armed ? Changed : Original),
            "Startup never changes the original path's bytes");
    }

    private static async Task ReplaceAsync(ExecutorContext context, bool succeeds, CancellationToken ct)
    {
        const string path = "later";
        var full = Path.Combine(context.Pair.LocalRoot, path);
        File.WriteAllText(full, Original);
        var expected = (await context.Local.ReadOneAsync(path, ct))!;
        var selection = new RemovalSelection(new RemovalProof(expected,
            VolumeSpace.Serial(context.Pair.LocalRoot).ToString(CultureInfo.InvariantCulture),
            DropboxContentHash.Compute(Encoding.UTF8.GetBytes(Original))), null);
        await using var stage = await LocalWriterChecks.StageAsync((LocalSide)context.Local, path);
        RemovalResult? result = null;
        ItemFailedException? failure = null;
        try { result = await context.Local.PublishAsync(stage, expected, selection, ct); }
        catch (ItemFailedException error) { failure = error; }
        Check.Require(succeeds ? result is { Outcome: RemovalOutcome.Published, Cause: null } && failure is null
            : result is null && failure is { Cause: TextKey.FailEarlierReplace },
            "A later same-pair replace succeeds only after startup settled the earlier operation");
        Check.Require(File.ReadAllText(full) == (succeeds ? "new content" : Original),
            "A refused later replace leaves its target unchanged; an admitted replace publishes incoming bytes");
    }
}
