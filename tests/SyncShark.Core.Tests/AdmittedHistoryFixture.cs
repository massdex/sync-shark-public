using System.Reflection;
using Massdex.SyncShark;

// Explicit per-fixture accepted command and Host control. No arbitrary event-id bypass, shared
// latest-run cache or synthetic effect authority: all writer admission checks are production calls.
internal sealed class AdmittedHistoryFixture : IAsyncDisposable
{
    private readonly SyncAuthorityRuntimeHost _host;
    private readonly PilotRunControl _control;
    private AdmittedHistoryFixture(SyncAuthorityRuntimeHost host, PilotRunControl control)
    { _host = host; _control = control; }

    internal static async Task<AdmittedHistoryFixture> StartAsync(SyncAuthorityRuntimeHost host,
        SyncAuthorityDatabase database, SyncPair pair)
    {
        await database.RegisterPairAsync(pair.Id, pair.PolicyRevision);
        var admission = await database.CreateAcceptedCommandAdmissionAsync(
            SyncAuthorityCommandReservation.Mint(), "run_pair", pair.Id, null, null,
            System.Text.Json.JsonSerializer.Serialize(new { pairIds = new[] { pair.Id } }),
            SyncAuthorityCommandPurpose.NormalEffect);
        var control = await host.BeginRunForTestAsync(commandId: admission.CommandRequestId);
        try
        {
            await (Task)typeof(SyncAuthorityRuntimeHost).GetMethod("RegisterRunActivityWriterAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host,
                    [admission, "run_pair", CancellationToken.None])!;
            return new(host, control);
        }
        catch { host.EndRunForTest(control); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await (Task)typeof(SyncAuthorityRuntimeHost).GetMethod("FinishRunActivityAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_host, [_control])!;
        }
        finally { _host.EndRunForTest(_control); }
    }
}
