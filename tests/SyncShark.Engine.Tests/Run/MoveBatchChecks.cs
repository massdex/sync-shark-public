using Massdex.SyncShark.Engine.Run;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Repair and AutoSync Dropbox moves share the side's batch collector (`--engine-checks move-batch`): each
/// member is saved, refused, held for settlement by identity or split in parts exactly as a single move would be.</summary>
public static class MoveBatchChecks
{
    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Siblings = ["a.txt", "b.txt", "c.txt"];

    public static void AutoSyncMove() => WithRigAsync(["old.txt"], async (rig, folder) =>
    {
        var moved = await SyncAfterAsync(rig, folder, () => Move(rig, "old.txt", "new.txt"), "new.txt");
        Check.Require(moved is { IsFolder: false } && rig.Wire.Calls.Count(call => call == MoveBatch) == 1 &&
            !rig.Wire.Calls.Contains(SingleMove),
            "An AutoSync file rename moves its Dropbox counterpart through the shared move batch, not move_v2.");
    }).GetAwaiter().GetResult();

    public static void SiblingsSaved() => WithRigAsync(
        [.. Siblings.Select(name => "Old/" + name), "New/keep.txt"], async (rig, folder) =>
    {
        await SyncAfterAsync(rig, folder, () =>
        {
            foreach (var name in Siblings) Move(rig, "Old/" + name, "New/" + name);
        }, null);
        Check.Require(Siblings.All(name => rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local,
                PathNames.Key("New/" + name)) is { DropboxPath: var saved } && saved == "New/" + name) &&
            rig.Store.Issues(rig.Pair.PairId).Count == 0 && rig.Wire.Calls.Contains(MoveBatch) &&
            !rig.Wire.Calls.Contains(SingleMove),
            "Every batched sibling's moved Dropbox location is saved in its binding, with no notice.");
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

    public static void FolderInParts() => WithRigAsync(["Old/a.txt", "Old/b.txt"], async (rig, folder) =>
    {
        var id = rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key("Old"))!.DropboxId!;
        rig.Wire.MoveBatchRefusals["New"] = "too_many_files";
        await SyncAfterAsync(rig, folder,
            () => Directory.Move(Path.Combine(rig.Pair.LocalRoot, "Old"), Path.Combine(rig.Pair.LocalRoot, "New")), null);
        Check.Require(rig.Wire.Calls.Contains(MoveBatch) && (rig.Store.FolderMoves(rig.Pair.PairId, id).Count != 0 ||
                rig.Store.Issues(rig.Pair.PairId).Any(issue => issue.Cause == TextKey.FailMoveInParts) ||
                rig.Wire.Calls.Contains(CreateFolder)),
            "A folder member refused with too_many_files takes the single move's in-parts fallback.");
    }).GetAwaiter().GetResult();

    private const string MoveBatch = "/2/files/move_batch_v2";
    private const string SingleMove = "/2/files/move_v2";
    private const string CreateFolder = "/2/files/create_folder_v2";

    private static void Move(RepairChecks.ComposedRig rig, string from, string to) =>
        File.Move(Path.Combine(rig.Pair.LocalRoot, from), Path.Combine(rig.Pair.LocalRoot, to));

    // One local change, caught up from the journal and synced by the same selection AutoSync runs.
    private static async Task<Entry?> SyncAfterAsync(RepairChecks.ComposedRig rig, string folder, Action change,
        string? read)
    {
        var pairId = rig.Pair.PairId;
        var generation = rig.Store.ReadPairExecution(pairId)!.Value.Generation;
        using var log = rig.Log(folder, "autosync");
        return await rig.Host.DispatchSessionAsync(pairId, () => log, async (session, ct) =>
        {
            change();
            await rig.Host.CatchUpLocalAsync(pairId, generation, ct);
            rig.Wire.Calls.Clear();
            await session.Syncer.SyncWorkAsync(ct);
            return read is null ? null : await session.Context.Dropbox.ReadOneAsync(read, ct);
        }, default);
    }

    // A synced baseline of the given files (and their folders) on both sides.
    private static async Task WithRigAsync(string[] files, Func<RepairChecks.ComposedRig, string, Task> body)
    {
        var folder = Path.Combine(Path.GetTempPath(), "syncshark-move-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using var rig = await RepairChecks.ComposeRigAsync(folder, level: 1);
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
