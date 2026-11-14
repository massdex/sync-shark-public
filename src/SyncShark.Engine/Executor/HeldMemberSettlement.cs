using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Closes held frozen members of a completed recursive effect only on two-sided identity absence.</summary>
internal sealed class HeldMemberSettlement(ExecutorContext context)
{
    internal async Task SettleAsync(FolderRemovalState operation, FolderRemovalItem root, CancellationToken ct)
    {
        if (context.Local is not IIdentitySide local || context.Dropbox is not IIdentitySide dropbox) return;
        var (low, high) = PathNames.DescendantBounds(PathNames.Key(root.Path));
        foreach (var kind in IssueClosure.AfterEffect)
        {
            var after = (Key: low, IssueId: (long)StateSchema.BeforeFirstOrdinal);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = context.Store.HeldFolderMembers(context.Pair.PairId, operation.OperationId, kind, after,
                    high, context.Settings.StoragePageSize);
                if (page.Count == 0) break;
                after = (PathNames.Key(page[^1].Issue.Path), page[^1].Issue.Id);
                var proven = new List<Issue>();
                foreach (var (issue, saved) in page)
                    if (Bound(issue, saved) &&
                        await AbsentAsync(() => local.ReadByIdDocumentedAsync(saved!.LocalId, ct)) &&
                        await AbsentAsync(() => dropbox.ReadByIdAsync(saved!.DropboxId, ct))) proven.Add(issue);
                if (proven.Count != 0) context.Store.CloseIdentityAbsent(context.Pair.PairId, proven);
            }
        }
    }

    // Paths cannot prove absence for an escaped member; its subject must identify the frozen binding instead.
    private static bool Bound(Issue issue, SyncedEntry? saved) =>
        saved is { LocalId.Length: > 0, DropboxId.Length: > 0 } && issue.Subject.Items.Count != 0 &&
        !IssuePolicy.UnknownMove(issue.Kind, issue.Subject) &&
        issue.Subject.Items.All(item => item.Id == saved!.IdOn(item.Side));

    // Found anywhere or unable to inspect proves nothing; cancellation still belongs to the caller.
    private static async Task<bool> AbsentAsync(Func<Task<(IdentityLocation Location, bool InScope)?>> read)
    {
        try { return await read() is not { InScope: true }; }
        catch (Exception error) when (error is not OperationCanceledException) { return false; }
    }
}
