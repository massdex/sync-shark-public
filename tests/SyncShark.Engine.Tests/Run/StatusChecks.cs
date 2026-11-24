using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static class StatusChecks
{
    public static async Task RunAsync()
    {
        using var store=new InMemoryStateStore(); var pair=ItemChecks.Pair(); await store.SavePairAsync(pair,default);
        var before=await new Status().ReadAsync(store,pair.PairId,default);
        Check.Require(before.Contains(pair.Name) && before.Contains("no Repair"),"Empty status claims a completed Repair");
        var revision=await store.OweRepairAsync(pair.PairId,Side.Local,"request",default);
        await store.FinishRepairAsync(pair.PairId,(revision,0),"{\"Finished\":\"2025-01-01T00:00:00Z\"}",default);
        var after=await new Status().ReadAsync(store,pair.PairId,default,"pointer warning");
        Check.Require(after.StartsWith("pointer warning"+Environment.NewLine,StringComparison.Ordinal) && after.Contains("2025-01-01"),"Startup warning or saved Repair lost after reopening status");
    }
}
