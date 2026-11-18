using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Repair and AutoSync Dropbox moves share the side's batch collector (`--engine-checks move-batch`): each
/// member is saved, refused, held for settlement by identity or split in parts exactly as a single move would be.</summary>
public static class MoveBatchChecks
{
    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Siblings = ["a.txt", "b.txt", "c.txt"];
    private static readonly string[] Folders = ["A", "B", "C"];

    public static void AutoSyncMove() => WithRigAsync(["old.txt"], async (rig, folder) =>
    {
        var moved = await SyncAfterAsync(rig, folder, () => Move(rig, "old.txt", "new.txt"), "new.txt");
        Check.Require(moved is { IsFolder: false } && rig.Wire.Calls.Count(call => call == MoveBatch) == 1 &&
            !rig.Wire.Calls.Contains(SingleMove),
            "An AutoSync file rename moves its Dropbox counterpart through the shared move batch, not move_v2.");
    }).GetAwaiter().GetResult();

    // The first submission is held until every sibling has reached the side, so the rest must share one request.
    public static void SiblingsSaved() => WithRigAsync(
        [.. Siblings.Select(name => "Old/" + name), "New/keep.txt"], async (rig, folder) =>
    {
        var reached = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Wire.HoldMoveBatch = gate.Task;
        // A bound, not a pause: if the moves never overlap, the size assertion below reports it instead of hanging.
        using var bound = new CancellationTokenSource(SiblingBound);
        using var expire = bound.Token.Register(() => gate.TrySetResult());
        await SyncAfterAsync(rig, folder, () =>
        {
            foreach (var name in Siblings) Move(rig, "Old/" + name, "New/" + name);
        }, null, dropbox => new IdentitySide(dropbox, ((IIdentitySide)dropbox).ReadByIdAsync)
        {
            Moving = () => { if (Interlocked.Increment(ref reached) == Siblings.Length) gate.TrySetResult(); }
        });
        Check.Require(rig.Wire.MoveBatchSizes.SequenceEqual([1, Siblings.Length - 1]) && !rig.Wire.Calls.Contains(SingleMove),
            "Siblings queued behind a held submission go out together in one multi-entry request; sizes=" +
            string.Join(",", rig.Wire.MoveBatchSizes));
        Check.Require(Siblings.All(name => rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local,
                PathNames.Key("New/" + name)) is { DropboxPath: var saved } && saved == "New/" + name) &&
            rig.Store.Issues(rig.Pair.PairId).Count == 0,
            "Every batched sibling's moved Dropbox location is saved in its binding, with no notice.");
    }).GetAwaiter().GetResult();

    // Sibling folders follow the same batching rule as files: they share one request behind a held submission.
    public static void FoldersBatched() => WithRigAsync(
        [.. Folders.Select(name => "Old/" + name + "/x.txt"), "New/keep.txt"], async (rig, folder) =>
    {
        var reached = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Wire.HoldMoveBatch = gate.Task;
        // A bound, not a pause: if the moves never overlap, the size assertion below reports it instead of hanging.
        using var bound = new CancellationTokenSource(SiblingBound);
        using var expire = bound.Token.Register(() => gate.TrySetResult());
        await SyncAfterAsync(rig, folder, () =>
        {
            foreach (var name in Folders)
                Directory.Move(Path.Combine(rig.Pair.LocalRoot, "Old", name), Path.Combine(rig.Pair.LocalRoot, "New", name));
        }, null, dropbox => new IdentitySide(dropbox, ((IIdentitySide)dropbox).ReadByIdAsync)
        {
            Moving = () => { if (Interlocked.Increment(ref reached) == Folders.Length) gate.TrySetResult(); }
        });
        Check.Require(rig.Wire.MoveBatchSizes.SequenceEqual([1, Folders.Length - 1]) && !rig.Wire.Calls.Contains(SingleMove),
            "Sibling folders queued behind a held submission go out together in one multi-entry request; sizes=" +
            string.Join(",", rig.Wire.MoveBatchSizes));
        Check.Require(Folders.All(name => rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local,
                PathNames.Key("New/" + name)) is { DropboxPath: var saved } && saved == "New/" + name) &&
            rig.Store.Issues(rig.Pair.PairId).Count == 0,
            "Every batched folder's moved Dropbox location is saved in its binding, with no notice.");
    }).GetAwaiter().GetResult();

    // A lone folder is refused on its own single-move answer, then filled in parts; the batch never arms it.
    public static void FolderInParts() => WithRigAsync(["Old/a.txt", "Old/b.txt"], async (rig, folder) =>
    {
        rig.Wire.MoveBatchRefusals["New"] = "too_many_files";
        await SyncAfterAsync(rig, folder,
            () => Directory.Move(Path.Combine(rig.Pair.LocalRoot, "Old"), Path.Combine(rig.Pair.LocalRoot, "New")), null);
        Check.Require(rig.Wire.Calls.Contains(MoveBatch) && !rig.Wire.Calls.Contains(SingleMove),
            "A folder move is submitted alone and refused with too_many_files, so the in-parts fallback owns it.");
        // A further pass may run any remaining continuation, so the fill is asserted after it.
        await SyncAfterAsync(rig, folder, () => { }, null);
        var pairId = rig.Pair.PairId;
        var (newA, newB, oldA, oldB) = await rig.Host.DispatchSessionAsync(pairId, () => null, async (session, ct) =>
            (await session.Context.Dropbox.ReadOneAsync("New/a.txt", ct),
             await session.Context.Dropbox.ReadOneAsync("New/b.txt", ct),
             await session.Context.Dropbox.ReadOneAsync("Old/a.txt", ct),
             await session.Context.Dropbox.ReadOneAsync("Old/b.txt", ct)), default);
        Check.Require(newA is not null && newB is not null && oldA is null && oldB is null &&
            rig.Store.Issues(pairId).Count == 0,
            "The refused folder is filled in parts: New/a.txt and New/b.txt exist with no notice, and the old children left Old.");
    }).GetAwaiter().GetResult();

    public static void NameTaken() => WithRigAsync(["old.txt"], async (rig, folder) =>
    {
        rig.Wire.MoveBatchRefusals["new.txt"] = "conflict";
        await SyncAfterAsync(rig, folder, () => Move(rig, "old.txt", "new.txt"), null);
        Check.Require(rig.Wire.Calls.Contains(MoveBatch) &&
            rig.Store.Issues(rig.Pair.PairId).Any(issue => issue.Cause == TextKey.ClashNameTaken),
            "A batch member refused for a taken name stores the single move's name-taken notice.");
    }).GetAwaiter().GetResult();

    public static void UnknownMember() => WithRigAsync(["old.txt"], async (rig, folder) =>
    {
        var id = rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key("old.txt"))!.DropboxId;
        rig.Wire.MoveBatchRefusals["new.txt"] = "internal_error";
        await SyncAfterAsync(rig, folder, () => Move(rig, "old.txt", "new.txt"), null);
        Check.Require(rig.Store.Issues(rig.Pair.PairId).Any(issue =>
                issue.Subject.Move is { ExpectedId: var expected, Destination: "new.txt" } && expected == id),
            "An Unknown batch member stores the unknown move for settlement by identity.");
    }).GetAwaiter().GetResult();

    private const string MoveBatch = "/2/files/move_batch_v2";
    private const string SingleMove = "/2/files/move_v2";
    private static readonly TimeSpan SiblingBound = TimeSpan.FromMinutes(1);

    private static void Move(RepairChecks.ComposedRig rig, string from, string to) =>
        File.Move(Path.Combine(rig.Pair.LocalRoot, from), Path.Combine(rig.Pair.LocalRoot, to));

    // One local change, caught up from the journal and synced by the same selection AutoSync runs.
    private static async Task<Entry?> SyncAfterAsync(RepairChecks.ComposedRig rig, string folder, Action change,
        string? read, Func<ISide, ISide>? dropbox = null)
    {
        var pairId = rig.Pair.PairId;
        var generation = rig.Store.ReadPairExecution(pairId)!.Value.Generation;
        using var log = rig.Log(folder, "autosync");
        return await rig.Host.DispatchSessionAsync(pairId, () => log, async (session, ct) =>
        {
            change();
            await rig.Host.CatchUpLocalAsync(pairId, generation, ct);
            rig.Wire.Calls.Clear();
            await SyncerFor(session, log, dropbox).SyncWorkAsync(ct);
            return read is null ? null : await session.Context.Dropbox.ReadOneAsync(read, ct);
        }, default);
    }

    // The session's own syncer, or the same composition over a wrapped Dropbox side (as the B214 budget check builds it).
    private static Syncer SyncerFor(PairSession session, RunLog log, Func<ISide, ISide>? dropbox)
    {
        if (dropbox is null) return session.Syncer;
        var context = session.Context with { Dropbox = dropbox(session.Context.Dropbox) };
        var ports = new SyncPorts(new RecoveryExecutionBoundary(() => session),
            new LiveExecutionBoundary(_ => session), PairSafety.SelectedBoundaryFor(session), LocalSide.HashSlots);
        return new Syncer(context, ports with
        {
            RunBatchAsync = async (selection, batchSide, scope, retries, settles, token) =>
            {
                await PairSafety.CheckRootsAsync(session, token);
                await FolderBatch.RunAsync(context, ports, session.PrerequisiteDropbox, log, selection, batchSide, scope,
                    retries, settles, next => PairSafety.CheckRootsAsync(session, next), session.Scans,
                    () => session.Host.TryHoldGeneration(session.Registration), token);
            }
        });
    }

    // A synced baseline of the given files (and their folders) on both sides.
    private static async Task WithRigAsync(string[] files, Func<RepairChecks.ComposedRig, string, Task> body)
    {
        var folder = Path.Combine(Path.GetTempPath(), "syncshark-move-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // One admission permit per sibling, so the sibling moves can be in flight together.
            await using var rig = await RepairChecks.ComposeRigAsync(folder, level: Siblings.Length);
            foreach (var parent in files.Select(PathNames.Parent).Where(parent => parent.Length != 0).Distinct())
            {
                Directory.CreateDirectory(Path.Combine(rig.Pair.LocalRoot, parent));
                rig.Wire.SeedFolder(parent);
            }
            foreach (var file in files)
            {
                var local = Path.Combine(rig.Pair.LocalRoot, file);
                await File.WriteAllTextAsync(local, "content");
                File.SetLastWriteTimeUtc(local, BaseTime.UtcDateTime);
                rig.Wire.SeedFile(file, "content", BaseTime);
            }
            using (var baseline = rig.Log(folder, "baseline"))
            {
                Check.Require((await rig.RunAsync(baseline, default)).Completed, "The composed baseline completes.");
                await rig.DrainWorkAsync(baseline, default);
            }
            await body(rig, folder);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
