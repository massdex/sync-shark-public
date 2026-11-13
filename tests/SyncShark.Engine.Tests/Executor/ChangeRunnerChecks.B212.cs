using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

// B-212 execution witnesses (SPEC-B212-6 9.3, SPEC-B212-7 9.1): each store starts as a genuine version-14 file whose
// one-time step creates the duplicate-folder obligation, then one public SyncWorkAsync call executes it.
public static partial class ChangeRunnerChecks
{
    // T1's names: X is the Local folder at Renamed; A = X@Old <-> R@Old (content), D = X@Renamed <-> E@Renamed
    // (empty). Old sorts before Renamed, so the attempt checks A first (the store's key order).
    private const string RepairOld = "Old";
    private const string RepairNew = "Renamed";
    private const string RepairChild = "Old/child";
    private const string RepairX = "folder-x";
    private const string RepairR = "folder-r";
    private const string RepairE = "folder-e";

    public static void RunDuplicateRepair() => RunDuplicateRepairAsync().GetAwaiter().GetResult();

    private static async Task RunDuplicateRepairAsync()
    {
        await RepairDropboxVictimAsync();
        await RepairHandDeletedVictimAsync();
        await RepairLateDescendantAsync();
    }

    // E1 (T1): one Dropbox delete, the settle through the operation path (-7 2), then W renames R onto Renamed.
    private static async Task RepairDropboxVictimAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = RepairFixture(victimPresent: true);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, RepairNew, RepairOld,
            RepairX, null, false, false));
        var (local, dropbox, submits) = RepairViews(fixture);
        var calls = new List<string>();
        string? atFirstSelection = null;
        fixture.Hooks.BeforeCall = method =>
        {
            lock (calls)
            {
                calls.Add(method.Name);
                if (method.Name != nameof(IStateStore.BeginSelection) || atFirstSelection is not null) return;
                atFirstSelection = $"localReads={local.Reads} localListings={local.Listings} dropboxReads={dropbox.Reads} " +
                    $"dropboxListings={dropbox.Listings} submits={submits.Count} " +
                    $"victimBound={fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(RepairNew)) is not null} " +
                    $"preimage={fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(RepairOld)) == RepairBinding(RepairOld, RepairR)}";
            }
        };
        await LiveSyncer(fixture, selection, new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        fixture.Hooks.BeforeCall = null;
        var settled = calls.IndexOf(nameof(IStateStore.CompleteFolderRemoval));
        var renamed = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(RepairNew));
        Check.Require(submits.Count == 1 && await fixture.Dropbox.ReadOneAsync(RepairNew, default) is { Id: RepairR } &&
            dropbox.Moves == 1 && LocalEffects(local) == 0 && fixture.Inner.Issues(PairId).Count == 0 &&
            RepairRows(fixture) == 0 && fixture.Inner.UnresolvedFolderRemovals(PairId, default).Count() == 0 &&
            renamed is { LocalId: RepairX, DropboxId: RepairR } &&
            settled >= 0 && settled < calls.IndexOf(nameof(IStateStore.BeginSelection)) &&
            atFirstSelection is not null && atFirstSelection.Contains("victimBound=False", StringComparison.Ordinal) &&
            atFirstSelection.Contains("preimage=True", StringComparison.Ordinal) &&
            fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark()) == (0, 0),
            "B212-E1: E is deleted once, the obligation settles before W's first selection, and W moves R onto Renamed: " +
            "submits=" + submits.Count + "; dropboxMoves=" + dropbox.Moves + "; localEffects=" + LocalEffects(local) +
            "; issues=" + fixture.Inner.Issues(PairId).Count + "; obligations=" + RepairRows(fixture) +
            "; renamed=" + renamed + "; settledAt=" + settled + "; atFirstSelection=" + atFirstSelection);
        Console.WriteLine("B212-E1 attempt provider calls before W's first selection: " + atFirstSelection);
    }

    // E7: E deleted by hand, so D4p settles state-only (-7 1.4): no local delete, no submission, and the handoff alone
    // moves R onto Renamed with no other pending work.
    private static async Task RepairHandDeletedVictimAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = RepairFixture(victimPresent: false);
        var (local, dropbox, submits) = RepairViews(fixture);
        await LiveSyncer(fixture, RepairOpportunity(fixture), new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var renamed = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(RepairNew));
        Check.Require(local.Deletes == 0 && LocalEffects(local) == 0 && submits.Count == 0 && dropbox.Moves == 1 &&
            await fixture.Dropbox.ReadOneAsync(RepairNew, default) is { Id: RepairR } &&
            renamed is { LocalId: RepairX, DropboxId: RepairR } && RepairRows(fixture) == 0 &&
            fixture.Inner.Issues(PairId).Count == 0 &&
            fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark()) == (0, 0),
            "B212-E7: the state-only settle forgets D, and the handoff moves R onto Renamed: localDeletes=" + local.Deletes +
            "; localEffects=" + LocalEffects(local) + "; submits=" + submits.Count + "; dropboxMoves=" + dropbox.Moves +
            "; renamed=" + renamed + "; obligations=" + RepairRows(fixture) + "; issues=" + fixture.Inner.Issues(PairId).Count);
    }

    // E3: a saved descendant committed under the victim's key before completion is refused by the folder validation
    // and the pre-forget P2 (-7 2): the victim stays bound, the operation is Retained, the obligation is kept.
    private static async Task RepairLateDescendantAsync()
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = RepairFixture(victimPresent: true);
        RepairViews(fixture);
        var late = RepairNew + "/late";
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name != nameof(IStateStore.CompleteFolderRemoval)) return;
            fixture.Hooks.BeforeCall = null;
            fixture.Inner.Commit(PairId, StateChange.Empty with
                { Record = [SyncedEntries.From(LocalEntry(late, "late-local"), RemoteEntry(late, "late-dropbox"))] });
        };
        await LiveSyncer(fixture, RepairOpportunity(fixture), new Boundary(_ => new Lease()), [])
            .SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        var operations = fixture.Inner.UnresolvedFolderRemovals(PairId, default).ToArray();
        Check.Require(fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(RepairNew)) == RepairBinding(RepairNew, RepairE) &&
            operations is [{ Active: false, Phase: FolderRemovalPhase.Retained, Authority: FolderRemovalAuthority.Repair }] &&
            RepairRows(fixture) == 1,
            "B212-E3: the late descendant refuses the settle; the victim stays bound and the operation is Retained: operations=" +
            string.Join(" | ", operations.Select(operation => operation.Phase + ":" + operation.Active + ":" + operation.Authority)) +
            "; obligations=" + RepairRows(fixture));
    }

    // The T1 store: the two duplicate bindings and A's saved child, committed and then made a genuine version-14 file
    // (the two v15 additions removed), so reopening runs the one-time step. Its own schema check refuses any other shape.
    private static Fixture RepairFixture(bool victimPresent)
    {
        var fixture = new Fixture(realStore: true);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Record = [RepairBinding(RepairOld, RepairR), RepairBinding(RepairNew, RepairE),
                SyncedEntries.From(LocalEntry(RepairChild, "child-local"), RemoteEntry(RepairChild, "child-dropbox"))]
        });
        fixture.Local.Seed(FolderEntry(RepairNew, RepairX), null);
        fixture.Local.Seed(LocalEntry(RepairNew + "/child", "child-local"), [1]);
        fixture.Dropbox.Seed(FolderEntry(RepairOld, RepairR), null);
        fixture.Dropbox.Seed(RemoteEntry(RepairChild, "child-dropbox"), [1]);
        if (victimPresent) fixture.Dropbox.Seed(FolderEntry(RepairNew, RepairE), null);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fixture.DatabasePath + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE binding_repair;ALTER TABLE folder_removal DROP COLUMN authority;PRAGMA user_version=14;";
            command.ExecuteNonQuery();
        }
        fixture.RestartStore();
        RepairDone(fixture);
        ClearObligations(fixture);
        Check.Require(RepairRows(fixture) == 1, "B212 fixture: the one-time step records one obligation for X's two bindings");
        return fixture;
    }

    private static SyncedEntry RepairBinding(string path, string dropboxId) =>
        SyncedEntries.From(FolderEntry(path, RepairX), FolderEntry(path, dropboxId));

    private static (CountingSide Local, CountingSide Dropbox, List<int> Submits) RepairViews(Fixture fixture)
    {
        var local = new CountingSide(fixture.Local);
        var dropbox = new CountingSide(fixture.Dropbox);
        var submits = new List<int>();
        fixture.LocalView = local;
        fixture.DropboxView = new ProxySide(dropbox) { OnSubmit = effects => submits.Add(effects.Count) };
        return (local, dropbox, submits);
    }

    private static int LocalEffects(CountingSide local) =>
        local.Stages + local.Publishes + local.Ensures + local.Deletes + local.Moves;

    // The context's opportunity only; a witness with no seeded notice selects nothing through it.
    private static WorkSelection RepairOpportunity(Fixture fixture)
    {
        var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
        return new WorkSelection(PairId, Generation, source.Epoch, Side.Local, VolumeKey, RepairX,
            PathNames.KeyFor(Side.Local, RepairNew), 0, 0);
    }

    private static long RepairRows(Fixture fixture)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fixture.DatabasePath + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM binding_repair;";
        return (long)command.ExecuteScalar()!;
    }
}
