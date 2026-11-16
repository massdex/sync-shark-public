using Massdex.SyncShark.Engine.Run;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>AutoSync's Dropbox moves take the same batch collector as the Repair's (`--engine-checks move-batch`).</summary>
public static class MoveBatchChecks
{
    private static readonly DateTimeOffset BaseTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void AutoSyncMove() => AutoSyncMoveAsync().GetAwaiter().GetResult();

    private static async Task AutoSyncMoveAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "syncshark-move-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using var rig = await RepairChecks.ComposeRigAsync(folder, level: 1);
            var old = Path.Combine(rig.Pair.LocalRoot, "old.txt");
            await File.WriteAllTextAsync(old, "content");
            File.SetLastWriteTimeUtc(old, BaseTime.UtcDateTime);
            rig.Wire.SeedFile("old.txt", "content", BaseTime);
            using (var baseline = rig.Log(folder, "baseline"))
            {
                Check.Require((await rig.RunAsync(baseline, default)).Completed, "The composed baseline completes.");
                await rig.DrainWorkAsync(baseline, default);
            }
            var pairId = rig.Pair.PairId;
            var generation = rig.Store.ReadPairExecution(pairId)!.Value.Generation;
            using var log = rig.Log(folder, "autosync");
            var moved = await rig.Host.DispatchSessionAsync(pairId, () => log, async (session, ct) =>
            {
                File.Move(old, Path.Combine(rig.Pair.LocalRoot, "new.txt"));
                await rig.Host.CatchUpLocalAsync(pairId, generation, ct);
                rig.Wire.Calls.Clear();
                await session.Syncer.SyncWorkAsync(ct);
                return await session.Context.Dropbox.ReadOneAsync("new.txt", ct);
            }, default);
            Check.Require(moved is { IsFolder: false } && rig.Wire.Calls.Count(call => call == "/2/files/move_batch_v2") == 1 &&
                !rig.Wire.Calls.Contains("/2/files/move_v2"),
                "An AutoSync file rename moves its Dropbox counterpart through the shared move batch, not move_v2.");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
