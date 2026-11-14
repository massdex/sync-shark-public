using System.Reflection;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Tests.Run;

public static partial class EngineHostChecks
{
    public static void RunInventoryCaseRename() => RunLocalBranchCaseRename(journal: false);
    public static void RunJournalCaseRename() => RunLocalBranchCaseRename(journal: true);

    private static void RunLocalBranchCaseRename(bool journal)
    {
        var root = Path.Combine(Path.GetTempPath(), "syncshark-branch-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var productRoot = OverrideProductRoot(root);
            LocalBranchCaseRenameAsync(root, journal).GetAwaiter().GetResult();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task LocalBranchCaseRenameAsync(string root, bool journal)
    {
        using var instance = Lock(root, journal ? "journal" : "inventory");
        const string PairId = "branch-rename-pair";
        const string Original = "lower.txt";
        const string Renamed = "LOWER.txt";
        await using var host = EngineHost.Compose(instance.StateAuthority.ProductRoot, _ => { },
            instance.StateAuthority, Settings, Http, TimeProvider.System, _ => null,
            validator => StateStore.Open(Path.Combine(instance.StateAuthority.StateRoot, "branch-rename.db"), validator),
            EngineHost.Mode.Persistent);
        var store = (StateStore)host.Store;
        store.SavePair(Paired(PairId));
        var local = new Entry(Original, false, 1, DateTimeOffset.UnixEpoch, "local-1", "", "", null);
        // Bind the existing journal callback without a production test hook or native volume acquisition.
        var reportJournal = typeof(EngineHost).GetMethod("ReportJournalItemFailureAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<JournalSource.Member, ItemFailedException, CancellationToken, Task>>(host);
        async Task ReportAsync(string path)
        {
            if (journal)
            {
                var (pair, generation) = store.ReadPairExecution(PairId)!.Value;
                var source = store.ReadSource(PairId, Side.Local)!.Value;
                // This callback consumes only member fences; journal path classification is not under test.
                var member = new JournalSource.Member(pair, generation, source.Epoch, source.Revision, null!);
                await reportJournal(member, new ItemFailedException(path, TextKey.FailLocalInUse, null)
                    { LocalId = local.Id }, CancellationToken.None);
            }
            else await host.DispatchAsync(PairId, (registration, _, _, _) =>
            {
                host.ReportInventoryFailure(registration, path, local with { Path = path },
                    TextKey.FailLocalInUse, false, null);
                // Repeated enumeration within one registration is idempotent.
                host.ReportInventoryFailure(registration, path, local with { Path = path },
                    TextKey.FailLocalInUse, false, null);
                return Task.FromResult(true);
            }, CancellationToken.None).WaitAsync(HookBound);
        }
        await ReportAsync(Original);
        var first = store.Issues(PairId).Single();
        var initial = store.ReadItemRetryState(PairId, [first.Id])[first.Id]!;
        Check.Require(first.Path == Original && initial.Dispatch == ItemRetryDispatch.LocalBranch,
            "case-rename branch setup: the first failure owns one old-spelling LocalBranch wait");
        // A second opportunity also fails: it must advance the same wait, not refuse or grow notices.
        foreach (var opportunity in new[] { "respelling", "repeated failure" })
        {
            var refused = false;
            try { await ReportAsync(Renamed); }
            catch (LiveAdmissionStaleException) { refused = true; }
            var notices = store.Issues(PairId);
            var current = store.ReadItemRetryState(PairId, [first.Id])[first.Id];
            Check.Require(!refused && notices.Count == 1 && notices[0].Id == first.Id &&
                notices[0].Path == Original && notices[0].Cause == TextKey.FailLocalInUse &&
                notices[0].Subject.HasSameItems(first.Subject) && current is not null &&
                current.Token.Revision > initial.Token.Revision && current.Dispatch == ItemRetryDispatch.LocalBranch,
                $"case-rename {(journal ? "journal" : "inventory")} {opportunity}: one captured notice and advancing wait, no refusal; " +
                $"refused={refused}, notices={notices.Count}");
            initial = current!;
        }
    }
}
