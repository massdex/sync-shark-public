using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    private const string ReplacingId = "replacing-id";
    // Two pair sizes with the involved set fixed (one path, at most one wait): work tied to the wait must not grow.
    private static readonly int[] VanishedBackground = [0, 2_000];

    public static void VanishedWaitCosts() => VanishedWaitCostsAsync().GetAwaiter().GetResult();

    // B-195b print-only probe, byte-identical on the candidate and on measure/0385-b195b-base: two AutoSync passes of a
    // saved file's Local deletion with both sides empty, with no wait and with the replacing file's in-use wait still
    // recorded at the path, at two pair sizes. No threshold: CI prints both sides for the comparison.
    // sql: statements SQLite ran on the store connection. store-calls: proxied top-level store calls.
    // store-call-ms: synchronous duration of those calls, returned or thrown, including reflection; NOT lock hold.
    // allocated-bytes: process-wide, including the SQL trace and the proxy; compare like with like only.
    private static async Task VanishedWaitCostsAsync()
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var background in VanishedBackground)
        foreach (var held in new[] { false, true })
        {
            var attempt = Guid.NewGuid();
            using var fixture = VanishedFixture(attempt, held, out var deletion);
            if (background > 0)
                fixture.Inner.Commit(PairId, StateChange.Empty with
                {
                    Record = Enumerable.Range(0, background).Select(index => SyncedEntries.From(
                        LocalEntry("Background/f" + index, "background-local-" + index),
                        RemoteEntry("Background/f" + index, "background-remote-" + index))).ToArray()
                });
            for (var pass = 0; pass < 2; pass++)
            {
                var (calls, time) = (fixture.Hooks.Total, fixture.Hooks.CallTime);
                var (waitReads, retirements) = (fixture.Hooks.Count(nameof(IStateStore.ReadReplacedItemRetry)),
                    fixture.Hooks.Count(nameof(IStateStore.RetireReplacedItemRetry)));
                var outcome = "";
                var allocated = GC.GetTotalAllocatedBytes(precise: true);
                var sql = await SqlStatementsAsync(fixture, async () =>
                {
                    try { outcome = "completed=" + (await Validated(fixture, deletion)).Completed; }
                    catch (LiveAdmissionStaleException error) { outcome = "refused=" + error.Reason; }
                });
                allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                Console.WriteLine($"b195b-vanished-pass background={background} wait={held} pass={pass} {outcome} " +
                    $"store-calls={fixture.Hooks.Total - calls} sql={sql.Total} " +
                    $"wait-reads={fixture.Hooks.Count(nameof(IStateStore.ReadReplacedItemRetry)) - waitReads} " +
                    $"retirements={fixture.Hooks.Count(nameof(IStateStore.RetireReplacedItemRetry)) - retirements} " +
                    $"allocated-bytes={allocated} " +
                    $"store-call-ms={(fixture.Hooks.CallTime - time).TotalMilliseconds.ToString("F3", invariant)} " +
                    $"notices={fixture.Inner.Issues(PairId).Count}");
            }
        }
    }

    // A saved file deleted on both sides. held: the in-use wait of the file that replaced it is still recorded at the
    // path although that file is gone too, so its id names no file. The deletion is the selection.
    private static Fixture VanishedFixture(Guid attempt, bool held, out WorkSelection deletion)
    {
        var fixture = new Fixture(realStore: true, registeredAttempt: attempt);
        TrustEchoSources(fixture);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Record = [SyncedEntries.From(LocalEntry(Item, LocalId), RemoteEntry(Item, DropboxId))]
        });
        if (held)
            fixture.Inner.RecordInUseFailure(PairId, Generation, fixture.Inner.ReadSource(PairId, Side.Local)!.Value.Epoch,
                Item, new IssueSubject([new IssueItem(Side.Local, Item, ReplacingId)], null), TextKey.FailLocalInUse, null,
                ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        deletion = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Deleted, Item, null, LocalId, null, false, false));
        return fixture;
    }
}
