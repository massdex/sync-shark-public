namespace Massdex.SyncShark.V2;

// The v2 CLI reads the store's own catalog rows directly for the legacy Recovery projection and open
// notices . The bounded keyset and restore surfaces live in V2CliRuntime.RecoveryRestore.cs.
public sealed partial class V2CliRuntime
{
    private const string RecoveryRetained = "retained";
    private const string RecoveryLocalSource = "LocalArchive";
    private const string RecoveryCloudSource = "DropboxArchive";
    private const string NoticeOpen = "open";

    /// <summary>
    /// The registry routes the catalog commands through the shared surface entry point, so core v2 answers
    /// the ones it owns here and refuses the rest .
    /// </summary>
    public async Task<object> ExecuteSurfaceAsync(SyncCliInvocation invocation, CancellationToken cancellationToken) =>
        invocation.Command switch
        {
            // Every core v2 Recovery list is one bounded keyset page with its cursor and state hints; the
            // legacy provider/search/offset catalog exists only on the v1 surface.
            "recovery list" => await LoadRecoveryCommandAsync(invocation, cancellationToken),
            "conflicts list" => await LoadConflictsAsync(invocation.PairId, invocation.AccountId, invocation.Search,
                invocation.Limit, invocation.Offset, cancellationToken),
            _ => throw NotInV2()
        };

    /// <inheritdoc />
    public Task<SyncCliRecoveryListData> LoadRecoveryAsync(string pairId, string? search, int limit,
        CancellationToken cancellationToken)
    {
        var pair = RequirePair(pairId);
        var retained = _store.Recoveries(pairId)
            .Where(recovery => string.IsNullOrEmpty(search) ||
                recovery.Path.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(recovery => recovery.Created)
            .ToArray();
        var page = retained.Take(limit).Select(recovery => Project(pair, recovery)).ToArray();
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new SyncCliRecoveryListData(pairId, page, limit, page.Length,
            retained.Length > page.Length, retained.Length, retained.Sum(recovery => recovery.Size),
            retained.Count(recovery => recovery.Expires <= now), (int)_run.Settings.RecoveryRetention.TotalDays,
            0, page.Length));
    }

    /// <inheritdoc />
    public Task<SyncCliConflictListData> LoadConflictsAsync(string? pairId, string? accountId, string? search,
        int limit, int offset, CancellationToken cancellationToken)
    {
        IEnumerable<Notice> notices = _store.OpenNotices(pairId);
        if (accountId is not null)
            notices = notices.Where(notice => accountId.Equals(AccountOf(notice.PairId), StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(search))
            notices = notices.Where(notice => notice.Path.Contains(search, StringComparison.OrdinalIgnoreCase));
        var all = notices.ToArray();
        var page = all.Skip(offset).Take(limit).Select(Project).ToArray();
        return Task.FromResult(new SyncCliConflictListData(page, limit, offset, page.Length,
            offset + page.Length < all.Length, all.Length)
        {
            FreeSpacePending = FreeSpacePresentation.Group(all)
        });
    }

    private string? AccountOf(string pairId) => _store.Pair(pairId)?.B.AccountId;

    private static SyncCliRecoveryItemData Project(PairDef pair, RecoveryRecord recovery)
    {
        var provider = (recovery.Side == Side.A ? pair.A : pair.B).Provider;
        var source = string.Equals(provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase)
            ? RecoveryLocalSource
            : RecoveryCloudSource;
        // Core v2 restores a retained row only when its content hash and provider can be verified; both the
        // original destination and an add-beside copy are offered (the bounded restore surface runs the same checks).
        var restorable = !string.IsNullOrEmpty(recovery.Hash) &&
            (string.Equals(provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(provider, SyncProviders.Dropbox, StringComparison.OrdinalIgnoreCase));
        return new SyncCliRecoveryItemData(recovery.RecoveryId, pair.PairId, pair.Name, source, recovery.Path,
            RecoveryRetained, recovery.Size, recovery.Hash, recovery.Created, restorable, restorable);
    }

    // One notice projects to one open conflict; sizes, hashes and times are not known without an endpoint
    // read, and the group is the notice kind so every kind stays visible .
    private static SyncCliConflictData Project(Notice notice) => new(notice.NoticeId ?? string.Empty, notice.PairId,
        notice.Path, 0, null, null, null, null, null, null, notice.Kind.ToString(), notice.Detail, NoticeOpen)
        {
            FreeSpace = notice.FreeSpace, Options = NoticeOptions.Options(notice),
            RetainedRevision = notice.RetainedVersion?.Loser.VersionId
        };
}
