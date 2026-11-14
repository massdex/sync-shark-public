using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

// Measurement branch only (SPEC-B212-9 1.8, print-only): candidate VAC-supersede-cost's scenario (c) body, run here
// with an Inferred same-path operation (SourceEvidence null) at the baseline, where D6 continues it. Same fixture shape
// and the same counts as the candidate: member pages and ready-effect pages for the old id, provider reads from its
// first ready-effect page to its retirement, retirements, freezes and the whole opportunity's executed SQL.
public static partial class ChangeRunnerChecks
{
    private static async Task VacateBaselineCostAsync()
    {
        int pageSize;
        using (var sizing = new Fixture()) pageSize = sizing.Settings.StoragePageSize;
        int Pages(int rows) => (rows + pageSize - 1) / pageSize;
        foreach (var members in new[] { 2 * pageSize + 1, 4 * (2 * pageSize + 1) })
        {
            var continued = await BaselineTraceAsync(members, 8);
            Console.WriteLine("B212-VAC-baseline-cost (c, Inferred) M=" + members + " P=" + pageSize + " walk=" +
                (Pages(members) + 1) + " executed-sql=" + continued.Sql + "; (c) " + continued);
        }
    }

    private static async Task<(int Sql, string Calls, int Items, int Ready, int Eligibility, int Retired, int Begun)>
        BaselineTraceAsync(int members, int unrelated)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = guard.Token;
        var fixture = new Fixture(realStore: true);
        using var owned = fixture;
        var c = VacateSetup(fixture, Side.Local);
        var children = Enumerable.Range(0, members - 1)
            .Select(index => (Path: PathNames.Child(VacateNew, "c" + index), Id: "child-other-" + index)).ToArray();
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Record = children.Select(child => SyncedEntries.From(FolderEntry(child.Path, child.Id + "-local"),
                    FolderEntry(child.Path, child.Id)))
                .Concat(Enumerable.Range(0, unrelated).Select(index => SyncedEntries.From(
                    LocalEntry("Unrelated/f" + index, "unrelated-local-" + index),
                    RemoteEntry("Unrelated/f" + index, "unrelated-dropbox-" + index)))).ToArray()
        });
        foreach (var child in children) fixture.Dropbox.Seed(FolderEntry(child.Path, child.Id), null);
        var old = await FreezeVacateRootAsync(c, VacateOccupant, ct);
        var calls = new List<string>();
        var (items, ready) = (0, 0);
        int? first = null, retirement = null;
        fixture.Hooks.BeforeCallWith = (method, args) =>
        {
            lock (calls)
            {
                calls.Add(method.Name);
                if (args.Length < 2 || args[1] as string != old) return;
                if (method.Name == nameof(IStateStore.FolderRemovalItems)) items++;
                if (method.Name == nameof(IStateStore.ReadyFolderEffects))
                {
                    ready++;
                    first ??= c.Local.Reads + c.Dropbox.Reads;
                }
                if (method.Name == nameof(IStateStore.RetireFolderRemoval))
                    retirement ??= c.Local.Reads + c.Dropbox.Reads;
            }
        };
        var begun = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
        var retired = fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval));
        var sql = await SqlStatementsAsync(fixture, () => VacateOpportunityAsync(c, ct));
        fixture.Hooks.BeforeCallWith = null;
        var profile = string.Join(",", calls.GroupBy(name => name).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key + "=" + group.Count()));
        return (sql.Total, profile, items, ready, retirement is { } at && first is { } start ? at - start : -1,
            fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval)) - retired,
            fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) - begun);
    }
}
