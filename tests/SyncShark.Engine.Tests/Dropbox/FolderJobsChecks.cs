using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.Scenarios;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checking a saved launch must select its observation route, never a mutation.</summary>
public static class FolderJobsChecks
{
    public static async Task RunAsync()
    {
        foreach (var (launch, check) in new[] { ("files/move_batch_v2", "files/move_batch/check_v2"),
            ("files/create_folder_batch", "files/create_folder_batch/check"), ("files/delete_batch", "files/delete_batch/check") })
            Check.Require(FolderJobs.CheckRoute(launch) == check && FolderJobs.CheckRoute(check) == check, "Wrong job check route");
        var pair = ItemChecks.Pair();
        var job = new AcceptedJob(pair.PairId, "files/upload", "token", [new(null, "folder", null)]);
        foreach (var invalid in new[] { job, job with { PairId = "other", Route = "files/delete_batch" } })
        {
            try { await new FolderJobs().ReadAsync(null!, pair, invalid, default); }
            catch (InvalidDataException) { continue; }
            throw new InvalidDataException("An unsupported or cross-pair job reached the provider");
        }
    }
}
