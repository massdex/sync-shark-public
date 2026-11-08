using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Keep both versions (section 7.8, D16).</summary>
public sealed class KeepBothRunner(ExecutorContext context, Transfers transfers)
{
    /// <summary>Chooses conflict labels on the retained side, or both sides, reusing matching content when present.</summary>
    public async Task<KeepBothLabels> ChooseLabelsAsync(Row row, CancellationToken ct, Side? keptSide = null)
    {
        var rule = context.Settings.ConflictLabel;
        (string? Path, bool Reuse) dropbox = keptSide is null or Side.Dropbox
            ? await ChooseAsync(Side.Dropbox, row.Dropbox!.Path, row.Local!, row.LocalHash, rule, ct)
            : (null, false);
        (string? Path, bool Reuse) local = keptSide is null or Side.Local
            ? await ChooseAsync(Side.Local, row.Local!.Path, row.Dropbox!, row.Dropbox!.ContentHash, rule, ct)
            : (null, false);
        return new KeepBothLabels(dropbox.Path, dropbox.Reuse, local.Path, local.Reuse);
    }

    /// <summary>Copies what isn't reused and returns the one StateChange for ActionExecutor to commit.</summary>
    public async Task<StateChange> RunAsync(Row row, KeepBothLabels labels, CancellationToken ct)
    {
        if (labels.DropboxLabel is null && labels.LocalLabel is null) throw new ArgumentException(null, nameof(labels));
        var local = row.Local!;
        var dropbox = row.Dropbox!;
        Entry? copiedDropbox = null;
        Entry? copiedLocal = null;
        if (labels.DropboxLabel is { } dropboxLabel)
        {
            copiedDropbox = labels.ReuseDropbox
                ? await context.Dropbox.ReadOneAsync(dropboxLabel, ct)
                    ?? throw new ChangedUnderneathException(dropboxLabel)
                : await transfers.UpAsync(local, dropboxLabel, null, ct);
            if (labels.ReuseDropbox && (copiedDropbox.Skip is not null || !await UsableDropboxAsync(row, copiedDropbox, ct)))
            {
                throw new ChangedUnderneathException(dropboxLabel);
            }
        }
        if (labels.LocalLabel is { } localLabel)
        {
            copiedLocal = labels.ReuseLocal
                ? await context.Local.ReadOneAsync(localLabel, ct)
                    ?? throw new ChangedUnderneathException(localLabel)
                : await DownAsync(dropbox, localLabel, ct);
            if (labels.ReuseLocal && (copiedLocal.Skip is not null || !await UsableLocalAsync(row, copiedLocal, ct)))
            {
                throw new ChangedUnderneathException(localLabel);
            }
        }

        // shortcut: a crash can leave an extra copy + InfoKeptBoth; next run logs a NameClash that the in-sync linked row closes (no data lost); upgrade only if seen in practice
        return await RecordBindings.PrepareAsync(context, StateChange.Empty with
        {
            ForgetSelf = row.Synced is { } synced ? [(Side.Local, synced.LocalKey)] : [],
            Record = copiedDropbox is null ? [SyncedEntries.From(copiedLocal!, dropbox)]
                : copiedLocal is null ? [SyncedEntries.From(local, copiedDropbox)]
                : [SyncedEntries.From(local, copiedDropbox), SyncedEntries.From(copiedLocal, dropbox)],
            Close = [(local.Path, IssueClosure.AfterKeepBoth),
                (dropbox.Path, IssueClosure.AfterKeepBoth)],
            Raise = [(IssueKind.Info, TextKey.InfoKeptBoth, local.Path,
                new IssueSubject([new(Side.Local, local.Path, local.Id),
                    new(Side.Dropbox, dropbox.Path, dropbox.Id)], null))]
        }, ct);
    }

    /// <summary>A chosen label set is usable only when each reuse flag matches what exists at its label path and
    /// that entry still holds the content it reuses. The executor asks this before any effect.</summary>
    internal async Task<bool> AreUsableAsync(Row row, KeepBothLabels labels, CancellationToken ct)
    {
        var local = labels.LocalLabel is { } localLabel ? await context.Local.ReadOneAsync(localLabel, ct) : null;
        var remote = labels.DropboxLabel is { } remoteLabel ? await context.Dropbox.ReadOneAsync(remoteLabel, ct) : null;
        if (labels.ReuseLocal != (local is not null) || labels.ReuseDropbox != (remote is not null)) return false;
        if (local is not null && !await UsableLocalAsync(row, local, ct)) return false;
        return remote is null || await UsableDropboxAsync(row, remote, ct);
    }

    private async Task<bool> UsableLocalAsync(Row row, Entry local, CancellationToken ct) =>
        !local.IsFolder && await context.Local.ContentHashAsync(local, ct) == row.Dropbox!.ContentHash;

    private async Task<bool> UsableDropboxAsync(Row row, Entry remote, CancellationToken ct) =>
        !remote.IsFolder && remote.ContentHash ==
        (row.LocalHash ?? await context.Local.ContentHashAsync(row.Local!, ct));

    /// <summary>A fresh label is a create: only a Published download authorizes the keep-both record.</summary>
    private async Task<Entry> DownAsync(Entry dropbox, string localLabel, CancellationToken ct)
    {
        var result = await transfers.DownAsync(dropbox, localLabel, null, null, static _ => { }, ct);
        return result.Outcome == RemovalOutcome.Published && result.Final is { } final
            ? final
            : throw new ItemFailedException(localLabel, result.Cause ?? TextKey.PublishFailed, null);
    }

    private async Task<(string Path, bool Reuse)> ChooseAsync(Side destination, string path,
        Entry source, string? requiredHash, LabelRule rule, CancellationToken ct)
    {
        var sideName = destination == Side.Dropbox ? rule.NameA : rule.NameB;
        var side = context.For(destination);
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            var label = KeepBothLabel.For(path, sideName, source.Modified, rule, attempt);
            var existing = await side.ReadOneAsync(label, ct);
            if (existing is null) return (label, false);
            if (existing.IsFolder || existing.Skip is not null || existing.Size != source.Size) continue;
            var hash = destination == Side.Local
                ? await side.ContentHashAsync(existing, ct)
                : existing.ContentHash;
            requiredHash ??= await context.Local.ContentHashAsync(source, ct);
            if (hash == requiredHash) return (label, true);
        }
    }
}
