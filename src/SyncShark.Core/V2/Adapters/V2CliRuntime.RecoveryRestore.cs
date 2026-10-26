using System.Globalization;

namespace Massdex.SyncShark.V2;

// The core v2 Recovery surface: every `recovery list` is one bounded keyset page, and `recovery restore` selects
// one exact id or one such page. Both read the store's own Recovery index; restore additionally reaches the
// engine's guarded effect. A plain list is store-only (no provider request, no engine gate); --verify explicitly
// asks for live classification. Paging continues from an explicit recovery-id cursor, so a large history never has
// to fit in memory and a bare restore can never broaden into "everything retained".
public sealed partial class V2CliRuntime
{
    // The CLI page size is bounded by the engine's own RecoveryQuery limit (1..500); a 100-row default keeps one
    // invocation readable and one restore page safely small. An explicit --limit may raise it to 500.
    private const int RecoveryDefaultPageLimit = 100;

    /// <summary>
    /// Runs every core v2 `recovery list` as one bounded keyset page. The default is a store-only read: no provider
    /// request and no engine gate. An explicit <c>--verify</c> adds the engine's live per-row classification.
    /// </summary>
    internal async Task<object> LoadRecoveryCommandAsync(SyncCliInvocation invocation,
        CancellationToken cancellationToken)
    {
        var pairId = invocation.PairId ?? throw new SyncCliUsageException(
            "recovery list requires --pair <exact-id>.");
        // The core v2 list is always the bounded keyset page. Legacy provider/search/offset/deleted selectors
        // cannot be mapped onto it without making the reported cursor describe only part of the real selection,
        // so they are explicit unsupported refusals rather than silently ignored options.
        if (invocation.AccountId is not null)
            throw new SyncCliUnsupportedException(
                "The core v2 Recovery page is pair-scoped; --account is not available. Nothing was read.");
        if (invocation.Flag("--deleted"))
            throw new SyncCliUnsupportedException(
                "Deleted cloud items are not available in the core v2 Recovery page. Nothing was read.");
        if (invocation.Search is not null)
            throw new SyncCliUnsupportedException(
                "--search is not available in the bounded core v2 Recovery page; use --path-prefix. Nothing was read.");
        if (invocation.Flag("--offset"))
            throw new SyncCliUsageException(
                "--offset is not available for the bounded core v2 Recovery page; continue with --after. Nothing was read.");
        var pair = RequirePair(pairId);
        var limit = RecoveryLimit(invocation);
        // Ask for one extra row when the engine bound allows it, so "more follow" is proven rather than guessed.
        var probe = limit < RecoveryQuery.MaximumLimit;
        var query = new RecoveryQuery(
            Since: RecoverySince(invocation.Value("--since")),
            PathPrefix: RecoveryPrefix(invocation.Value("--path-prefix")),
            Side: RecoverySide(pair, invocation.Value("--side")),
            ActionId: invocation.Value("--action-id"),
            AfterId: invocation.Value("--after"),
            Limit: probe ? limit + 1 : limit);
        // The default is a durable-state read: no provider request, no historical download and no engine gate, so
        // a large catalog stays a cheap page and a read never queues behind AutoSync. Live classification is opt-in.
        if (!invocation.Flag("--verify")) return RecoveryStorePage(pair, query, limit, probe);
        // --verify is the engine's read-only projection: it observes each destination and retained revision and
        // names the live classification (current, absent, occupied or a copy name). It therefore contacts providers
        // and serializes through the engine's run gate; a listing that asked for it may wait behind an active run.
        IReadOnlyList<RecoveryRestoreItem> rows;
        try
        {
            rows = await _run.RestoreRecoveryPageAsync(pairId, query, false, true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SyncRunRefusedException error)
        {
            throw new SyncCliReviewException(error.Message);
        }
        catch (EndpointAccessException error)
        {
            throw new SyncCliReviewException(ReconnectText(error));
        }
        var page = rows.Take(limit).ToArray();
        var more = rows.Count > limit;
        var mayHaveMore = !more && page.Length == limit && !probe;
        var next = more || mayHaveMore ? page[^1].Id : null;
        var hint = more
            ? $"More retained items follow; repeat with the same filters plus --after {next}."
            : mayHaveMore
                ? $"This page is full; more may follow. Repeat with the same filters plus --after {next} to check."
                : "This page is the end of the filtered retained items.";
        return new SyncCliRecoveryPageData(pair.PairId,
            page.Select(item => ProjectRecoveryPageItem(pair, item)).ToArray(), limit, page.Length, more,
            mayHaveMore, next, hint, []);
    }

    /// <summary>
    /// One store-only page: each row's classification comes from durable state (an open restore notice, an expired
    /// retention window, or retained) with no endpoint access and no engine gate. Destinations are unknown without
    /// observing an endpoint, so they remain null until <c>--verify</c>.
    /// </summary>
    private SyncCliRecoveryPageData RecoveryStorePage(PairDef pair, RecoveryQuery query, int limit, bool probe)
    {
        var records = _store.RecoveryPage(pair.PairId, query);
        var page = records.Take(limit).ToArray();
        var more = records.Count > limit;
        var mayHaveMore = !more && page.Length == limit && !probe;
        var next = more || mayHaveMore ? page[^1].RecoveryId : null;
        var now = TimeProvider.System.GetUtcNow();
        var hint = (more
                ? $"Store-only listing; more retained items follow. Repeat with the same filters plus --after {next}."
                : mayHaveMore
                    ? $"Store-only listing; this page is full and more may follow. Repeat with the same filters plus --after {next} to check."
                    : "Store-only listing; this is the end of the filtered retained items.") +
            " Add --verify to observe each destination and retained revision (contacts providers and waits behind any active sync).";
        return new SyncCliRecoveryPageData(pair.PairId,
            page.Select(record => ProjectRecoveryStoreItem(pair, record, now)).ToArray(), limit, page.Length, more,
            mayHaveMore, next, hint, []);
    }

    private SyncCliRecoveryPageItemData ProjectRecoveryStoreItem(PairDef pair, RecoveryRecord record,
        DateTimeOffset now)
    {
        // The notice id is the deterministic id the engine writes for this Recovery item. Keep this formula
        // aligned with SyncRun.RecoveryRestoreId ("rrs", pair, recovery); a store-only row then names its own notice.
        var saved = _store.NoticeById(record.PairId, ShortId.Make("rrs", record.PairId, record.RecoveryId));
        var notice = saved is { Closed: false } found ? found.Notice : null;
        var classification = notice is not null ? "NOTICE-OPEN" : record.Expires <= now ? "EXPIRED" : "RETAINED";
        var detail = notice is null
            ? null
            : notice.ReasonKey == SyncRun.RecoveryRestoreReady
                ? "A restore was verified and is waiting for ordinary sync."
                : "A restore notice is open for this item.";
        return new SyncCliRecoveryPageItemData(record.RecoveryId, RecoverySideName(pair, record.Side.ToString()),
            record.Path, record.VersionId, record.Size, record.Created, classification, null, detail);
    }

    /// <summary>
    /// Runs `recovery restore` for core v2. One exact id (with the legacy --key alias) or one keyset page of
    /// filters; nothing writes without --yes, and --dry-run wins even when --yes is also present.
    /// </summary>
    public async Task<object> RestoreRecoveryCommandAsync(SyncCliInvocation invocation,
        CancellationToken cancellationToken)
    {
        var pairId = invocation.PairId ?? throw new SyncCliUsageException(
            "recovery restore requires --pair <exact-id>.");
        var pair = RequirePair(pairId);
        // Provider revision restore by path is the v1 surface; core v2 only restores retained Recovery rows.
        if (invocation.AccountId is not null || invocation.Path is not null)
            throw new SyncCliUnsupportedException(
                "Restoring one provider revision by --account/--path is not in core v2 yet. Nothing was restored.");
        var exactId = invocation.Value("--id") ?? invocation.Key;
        var filtered = invocation.Flag("--since") || invocation.Flag("--path-prefix") ||
                       invocation.Flag("--action-id") || invocation.Flag("--side");
        if (exactId is not null && filtered)
            throw new SyncCliUsageException(
                "Choose one exact --id (or legacy --key), or a filter selection, not both. Nothing was restored.");
        // Never fall through to every retained row: the parser enforces this too, but the runtime must hold the
        // same line for callers that build an invocation without parsing.
        if (exactId is null && !invocation.Flag("--since") && !invocation.Flag("--path-prefix") &&
            !invocation.Flag("--action-id"))
            throw new SyncCliUsageException("recovery restore needs an exact --id (or legacy --key), or at least " +
                "one of --since, --path-prefix or --action-id. It never restores every retained item by default.");
        var asCopy = invocation.Copy || invocation.Flag("--as-copy");
        // Default dry run without --yes; an explicit --dry-run overrides even a confirmed --yes.
        var dryRun = invocation.Flag("--dry-run") || !invocation.Yes;
        // Validate a supplied limit even for an exact id, so a direct caller never gets silent clamping.
        var requestedLimit = RecoveryLimitOrNull(invocation);
        var limit = exactId is not null ? 1 : requestedLimit ?? RecoveryDefaultPageLimit;
        // A dry run may probe one extra row for an exact "more follows"; a real restore must never over-fetch,
        // because every fetched row is an effect. In that case a full page reports that more may follow.
        var probe = dryRun && exactId is null && limit < RecoveryQuery.MaximumLimit;
        var query = new RecoveryQuery(exactId, RecoverySince(invocation.Value("--since")),
            RecoveryPrefix(invocation.Value("--path-prefix")), RecoverySide(pair, invocation.Value("--side")),
            invocation.Value("--action-id"), invocation.Value("--after"), probe ? limit + 1 : limit);
        // Both preview and restore share the engine's pair admission with ordinary runs.
        return await _run.WithPairAdmissionAsync<object>(pairId, async () =>
        {
            try
            {
                var rows = await _run.RestoreRecoveryPageAsync(pairId, query, asCopy, dryRun, cancellationToken)
                    .ConfigureAwait(false);
                if (exactId is not null && rows.Count == 0)
                    throw new SyncCliTargetException("No retained recovery item has that exact identity.");
                // The exact confirmed shape keeps the existing single-action CLI type, so the legacy --key/--copy
                // invocation remains output-compatible; a dry run and a filtered page use the structured page below.
                if (exactId is not null && !dryRun)
                    return ProjectRecoveryAction(pair, rows[0], asCopy);
                var page = rows.Take(limit).ToArray();
                var more = rows.Count > limit;
                var mayHaveMore = exactId is null && !more && page.Length == limit && !probe;
                var next = exactId is null && (more || mayHaveMore) ? page[^1].Id : null;
                var hint = dryRun
                    ? more
                        ? $"Dry run only; nothing was changed. More follow: repeat with the same filters plus --after {next}, or add --yes to restore this page."
                        : mayHaveMore
                            ? $"Dry run only; nothing was changed. This page is full and more may follow; repeat with the same filters plus --after {next} to check, or add --yes to restore this page."
                            : "Dry run only; nothing was changed. Add --yes to restore this exact page."
                    : more
                        ? $"More follow: repeat with the same filters plus --after {next} for the next page."
                        : mayHaveMore
                            ? $"This page is full and more may follow; repeat with the same filters plus --after {next} to check."
                            : "This page is the end of the filtered selection.";
                return new SyncCliRecoveryRestorePageData(pair.PairId,
                    page.Select(item => ProjectRecoveryRestoreItem(pair, item, asCopy, dryRun)).ToArray(), dryRun,
                    asCopy, page.Length, more, mayHaveMore, next, hint, []);
            }
            catch (SyncRunRefusedException error)
            {
                throw new SyncCliReviewException(error.Message);
            }
            catch (EndpointAccessException error)
            {
                throw new SyncCliReviewException(ReconnectText(error));
            }
        }, cancellationToken, Console.Error.WriteLine);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The legacy exact-key entry point: it always restores (the caller already confirmed) and returns the existing
    /// single-action CLI type, so the v1-shaped callers and their outputs are unchanged.
    /// </remarks>
    public async Task<SyncCliRecoveryActionData> RestoreRecoveryAsync(string pairId, string key, bool asCopy,
        CancellationToken cancellationToken)
    {
        var pair = RequirePair(pairId);
        return await _run.WithPairAdmissionAsync(pairId, async () =>
        {
            try
            {
                var rows = await _run.RestoreRecoveryPageAsync(pairId, new RecoveryQuery(Id: key, Limit: 1), asCopy,
                    false, cancellationToken).ConfigureAwait(false);
                if (rows.Count == 0)
                    throw new SyncCliTargetException("No retained recovery item has that exact identity.");
                return ProjectRecoveryAction(pair, rows[0], asCopy);
            }
            catch (SyncRunRefusedException error)
            {
                throw new SyncCliReviewException(error.Message);
            }
            catch (EndpointAccessException error)
            {
                throw new SyncCliReviewException(ReconnectText(error));
            }
        }, cancellationToken, Console.Error.WriteLine);
    }

    private static SyncCliRecoveryPageItemData ProjectRecoveryPageItem(PairDef pair, RecoveryRestoreItem item) =>
        new(item.Id, RecoverySideName(pair, item.Side), item.Path, item.Version, item.Size, item.Created,
            item.Classification, item.Destination, item.Detail);

    private static SyncCliRecoveryRestoreItemData ProjectRecoveryRestoreItem(PairDef pair, RecoveryRestoreItem item,
        bool asCopy, bool dryRun) =>
        new(item.Id, RecoverySideName(pair, item.Side), item.Path, item.Version, item.Size, item.Created,
            item.Classification, item.Destination, item.Detail,
            RecoveryAction(item.Classification, asCopy, dryRun));

    private static SyncCliRecoveryActionData ProjectRecoveryAction(PairDef pair, RecoveryRestoreItem item,
        bool asCopy)
    {
        var restored = string.Equals(item.Classification, "RESTORED", StringComparison.Ordinal);
        // Never claim an effect the engine did not verify: only RESTORED is "Restored"; SKIP-PRESENT/CONFLICT/
        // PROTECTED/NO-VERSION/FAILED report their own classification.
        var result = (restored ? "Restored" : "Not restored") + " '" + (item.Destination ?? item.Path) + "': " +
            item.Classification;
        if (!string.IsNullOrWhiteSpace(item.Detail)) result += ". " + item.Detail;
        return new SyncCliRecoveryActionData(pair.PairId, item.Id, RecoveryAction(item.Classification, asCopy, false),
            result);
    }

    private static string RecoveryAction(string classification, bool asCopy, bool dryRun) =>
        dryRun ? "restore-preview" :
        string.Equals(classification, "RESTORED", StringComparison.Ordinal)
            ? asCopy ? "restore-copy" : "restore"
            : "restore-" + classification.ToLowerInvariant();

    // The page size is rejected outside the engine's own 1..500 bound, never silently clamped: a direct caller
    // constructing an invocation without the parser must see the same refusal as the CLI.
    private static int RecoveryLimit(SyncCliInvocation invocation) =>
        RecoveryLimitOrNull(invocation) ?? RecoveryDefaultPageLimit;

    private static int? RecoveryLimitOrNull(SyncCliInvocation invocation)
    {
        if (!invocation.Flag("--limit")) return null;
        if (invocation.Limit is < 1 or > RecoveryQuery.MaximumLimit)
            throw new SyncCliUsageException("The option --limit requires an integer between 1 and " +
                RecoveryQuery.MaximumLimit.ToString(CultureInfo.InvariantCulture) + ".");
        return invocation.Limit;
    }

    // The engine names a side as "A"/"B"; the CLI names it by the root's actual provider, so a reversed
    // Dropbox/local pair still reports the truthful side rather than assuming A is local.
    private static string RecoverySideName(PairDef pair, string side) =>
        string.Equals(side, Side.A.ToString(), StringComparison.OrdinalIgnoreCase) ? pair.A.Provider :
        string.Equals(side, Side.B.ToString(), StringComparison.OrdinalIgnoreCase) ? pair.B.Provider : side;

    // --side local|dropbox selects the one root with that provider. Both roots sharing the provider means the
    // requested provider covers both sides, so no narrowing is applied; no matching root is a clear refusal.
    internal static Side? RecoverySide(PairDef pair, string? text)
    {
        if (text is null) return null;
        var provider = text switch
        {
            "local" => SyncProviders.Local,
            "dropbox" => SyncProviders.Dropbox,
            _ => throw new SyncCliUsageException("For recovery, --side requires local or dropbox.")
        };
        var a = string.Equals(pair.A.Provider, provider, StringComparison.OrdinalIgnoreCase);
        var b = string.Equals(pair.B.Provider, provider, StringComparison.OrdinalIgnoreCase);
        // A local/local or cloud/cloud pair has two roots of the same provider: the provider already names both
        // sides, so --side local is valid and simply reads both rather than refusing a truthful filter.
        if (a && b) return null;
        if (!a && !b)
            throw new SyncCliUsageException($"--side {text} requires a pair with a {text} root; this pair has " +
                $"{pair.A.Provider} and {pair.B.Provider} roots. No page was read.");
        return a ? Side.A : Side.B;
    }

    private static DateTimeOffset? RecoverySince(string? text)
    {
        if (text is null) return null;
        // The parser owns the vocabulary; this keeps a directly constructed invocation honest too.
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"(Z|[+-]\d{2}:\d{2})$") ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var since))
            throw new SyncCliUsageException("--since requires an ISO timestamp with Z or an explicit UTC offset.");
        return since;
    }

    // A pair-relative literal prefix: '/' separators, no root, no traversal segments. The engine compares the
    // literal path (never a LIKE pattern), so a prefix is exact and case-insensitive.
    private static string? RecoveryPrefix(string? text)
    {
        if (text is null) return null;
        if (Path.IsPathRooted(text))
            throw new SyncCliUsageException("--path-prefix requires a pair-relative folder path, not a rooted path.");
        var prefix = text.Replace('\\', '/').Trim('/');
        if (prefix.Length == 0 || prefix.Split('/').Any(segment => segment is "." or ".."))
            throw new SyncCliUsageException("--path-prefix requires a pair-relative folder path without '.' or " +
                "'..' segments.");
        return prefix;
    }
}
