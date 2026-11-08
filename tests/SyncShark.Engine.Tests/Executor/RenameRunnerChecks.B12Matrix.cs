using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>B12-MEAS: test-only allocation matrix for the final rename runner (ApplyAsync dispatch to the
/// Follow and Adopt lifetimes and the Here/Moving/Gone destination identities), across success, suspended,
/// failed and cancelled outcomes. Registered only as the <c>b12-meas</c> group.</summary>
public static partial class RenameRunnerChecks
{
    public static void RunB12RenameMatrix()
    {
        foreach (var kind in new[] { "follow", "adopt" })
        foreach (var outcome in new[] { "success", "suspended", "failed", "cancelled" })
            RenameDecisionPath(kind, outcome);
        foreach (var identity in new[] { DestinationIdentity.Here, DestinationIdentity.Moving, DestinationIdentity.Gone })
        foreach (var outcome in new[] { "success", "suspended", "failed", "cancelled" })
            IdentityPath(identity, outcome);
    }

    private static void RenameDecisionPath(string kind, string outcome)
    {
        try
        {
            using var f = new Fixture();
            IReadOnlyList<RenameCandidate> group;
            if (kind == "adopt")
            {
                var local = f.MoveAsync(Side.Local, "New").GetAwaiter().GetResult();
                var dropbox = f.MoveAsync(Side.Dropbox, "New").GetAwaiter().GetResult();
                f.ScanAsync().GetAwaiter().GetResult();
                group = [local, dropbox];
            }
            else
            {
                var side = Side.Local;
                var moved = f.MoveAsync(side, "New").GetAwaiter().GetResult();
                moved = moved with { Moved = moved.Moved with { Modified = Fixture.Time.AddMinutes(1), ContentHash = "edited" } };
                f.For(side).Seed(moved.Moved, Fixture.Content);
                f.ScanAsync().GetAwaiter().GetResult();
                f.Raise(IssueKind.Failure, TextKey.FailChanged, "Old", side);
                group = [moved];
            }
            if (outcome == "failed")
                f.For(Side.Local).FailNext("New", new ItemFailedException("New", TextKey.FailChanged, null));
            var context = outcome == "suspended"
                ? f.Context with { Local = new B12YieldingSide(f.Context.Local), Dropbox = new B12YieldingSide(f.Context.Dropbox) }
                : f.Context;
            using var cts = new CancellationTokenSource();
            if (outcome == "cancelled") cts.Cancel();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetTotalAllocatedBytes(precise: true);
            RenameKind? decision = null;
            var threw = false;
            try { decision = f.RunAsync(group, context: context, ct: cts.Token).GetAwaiter().GetResult().Kind; }
            catch (OperationCanceledException) { threw = true; }
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            Console.WriteLine("B12 rename-matrix kind=" + kind + " outcome=" + outcome + " decision=" + decision +
                " cancelled=" + threw + " allocated=" + allocated);
        }
        catch (Exception error)
        {
            Console.WriteLine("B12 rename-matrix kind=" + kind + " outcome=" + outcome + " probe-failed " +
                error.GetType().Name + ": " + error.Message);
        }
    }

    private static void IdentityPath(DestinationIdentity identity, string outcome)
    {
        try
        {
            using var f = new Fixture();
            var moved = f.MoveAsync(Side.Local, "New").GetAwaiter().GetResult();
            var ownerLocal = f.LocalOld with { Path = "New", Id = "owner-local" };
            var ownerDropbox = f.DropboxOld with { Path = "New", Id = "owner-cloud" };
            var owner = SyncedEntries.From(ownerLocal, ownerDropbox);
            f.Store.Commit(f.Pair.PairId, StateChange.Empty with { Record = [owner] });
            f.Dropbox.Seed(ownerDropbox, Fixture.Content);
            var source = f.Store.ReadSource(f.Pair.PairId, Side.Local)!.Value;
            var acquired = f.Store.AcquireJournalPage("1", new JournalPosition(1, 1), new JournalPosition(1, 2),
                [(f.Pair.PairId, Fixture.Generation, source.Epoch, source.Revision, false)],
                [(f.Pair.PairId, new ChangeNotice(Side.Local, ChangeKind.Renamed, "New", "Old", moved.Moved.Id,
                    moved.Moved, false, false))])!.Value;
            var selection = new WorkSelection(f.Pair.PairId, Fixture.Generation, source.Epoch, Side.Local,
                "1", moved.Moved.Id, PathNames.Key("New"), acquired, acquired);
            Check.Require(f.Store.BeginSelection(selection), "B12 matrix selection admitted");
            using var cts = new CancellationTokenSource();
            if (outcome == "cancelled") cts.Cancel();
            var local = new B12IdentitySide(f.Local, async (id, ct) =>
            {
                if (outcome == "suspended") await Task.Yield();
                if (outcome == "cancelled") ct.ThrowIfCancellationRequested();
                if (outcome == "failed") throw new ItemFailedException("New", TextKey.FailChanged, null);
                if (identity == DestinationIdentity.Gone) return null;
                return (new IdentityLocation("1", "native", ownerLocal with
                    { Path = identity == DestinationIdentity.Here ? "New" : "Elsewhere" }), true);
            });
            var context = f.Context with { Local = local };
            var items = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(), RefusingExecutionBoundary.Instance,
                RefusingExecutionBoundary.Instance, 0)).Items;
            var lease = AdmitAsync(f, [moved], moved.Moved, Fixture.Generation).GetAwaiter().GetResult();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetTotalAllocatedBytes(precise: true);
            RenameKind? decision = null;
            var threw = false;
            try
            {
                var result = items.RenameAdmittedAsync(lease, selection, context.Opportunity, [moved], moved.Moved,
                    cts.Token).GetAwaiter().GetResult();
                decision = result.Kind;
            }
            catch (OperationCanceledException) { threw = true; }
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            f.Store.EndSelection(selection);
            Console.WriteLine("B12 rename-matrix identity=" + identity + " outcome=" + outcome + " decision=" + decision +
                " cancelled=" + threw + " allocated=" + allocated);
        }
        catch (Exception error)
        {
            Console.WriteLine("B12 rename-matrix identity=" + identity + " outcome=" + outcome + " probe-failed " +
                error.GetType().Name + ": " + error.Message);
        }
    }

    private sealed class B12YieldingSide(ISide inner) : ISide
    {
        public Side Side => inner.Side;

        public async IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            await foreach (var item in inner.ReadAllAsync(under, options, ct)) yield return item;
        }

        public async Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
        {
            await Task.Yield();
            return await inner.ReadOneAsync(path, ct);
        }

        public async Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct)
        {
            await Task.Yield();
            return await inner.OpenReadAsync(entry, attempt, ct);
        }

        public async Task<string> ContentHashAsync(Entry entry, CancellationToken ct)
        {
            await Task.Yield();
            return await inner.ContentHashAsync(entry, ct);
        }

        public async Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
        {
            await Task.Yield();
            return await inner.StageAsync(path, content, source, ct);
        }

        public async Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct)
        {
            await Task.Yield();
            return await inner.PublishAsync(staged, expected, selection, ct);
        }

        public async Task<Entry> EnsureFolderAsync(string path, CancellationToken ct)
        {
            await Task.Yield();
            return await inner.EnsureFolderAsync(path, ct);
        }

        public async Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
        {
            await Task.Yield();
            return await inner.DeleteAsync(expected, selection, ct);
        }

        public async Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct)
        {
            await Task.Yield();
            return await inner.MoveAsync(expected, newPath, ct);
        }

        public async Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct)
        {
            await Task.Yield();
            return await inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
        }

        public async Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount, RequestAttempt attempt,
            CancellationToken ct)
        {
            await Task.Yield();
            return await inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
        }
    }

    private sealed class B12IdentitySide(ISide inner,
        Func<string, CancellationToken, Task<(IdentityLocation Location, bool InScope)?>> identity) : ISide, IIdentitySide
    {
        public Side Side => inner.Side;
        public Task<(IdentityLocation Location, bool InScope)?> ReadByIdAsync(string id, CancellationToken ct) => identity(id, ct);
        public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct) =>
            inner.ReadAllAsync(under, options, ct);
        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct) => inner.ReadOneAsync(path, ct);
        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) =>
            inner.OpenReadAsync(entry, attempt, ct);
        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => inner.ContentHashAsync(entry, ct);
        public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct) =>
            inner.StageAsync(path, content, source, ct);
        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct) => inner.PublishAsync(staged, expected, selection, ct);
        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct) => inner.EnsureFolderAsync(path, ct);
        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct) =>
            inner.DeleteAsync(expected, selection, ct);
        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) =>
            inner.MoveAsync(expected, newPath, ct);
        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
            inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount, RequestAttempt attempt,
            CancellationToken ct) => inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
    }
}
