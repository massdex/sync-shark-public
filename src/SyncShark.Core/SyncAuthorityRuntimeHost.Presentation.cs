namespace Massdex.SyncShark;

/// <summary>
/// SS-D12: one validated, display-only projection of the saved account and preference state for a single
/// synchronous presentation pass. It is built from the existing stores, keeps their whole-collection
/// validation, converts every account to <see cref="SyncAccountBinding"/> metadata immediately and retains
/// no credential reference, refresh token, provider binding object or effect authority. Nothing here is
/// accepted by any effect, admission or credential API: every command, run, recovery and provider path
/// continues to call the fresh authority methods, which consult the current stored state themselves.
/// </summary>
public sealed record SyncPresentationSnapshot(
    IReadOnlyList<SyncAccountBinding> OrderedAccounts,
    IReadOnlyDictionary<string, SyncAccountBinding> AccountsByConnectionId,
    string? PrimaryDropboxConnectionId,
    string? PrimaryConnectionId,
    UiPreferences Preferences)
{
    /// <summary>
    /// The display-only account lookup. An exact id resolves against the active account metadata;
    /// a null or blank id keeps the existing primary fallback order (the primary Dropbox account first,
    /// then the primary Google Drive account).
    /// </summary>
    public SyncAccountBinding? Account(string? connectionId)
    {
        if (!string.IsNullOrEmpty(connectionId)) return AccountsByConnectionId.GetValueOrDefault(connectionId);
        return PrimaryConnectionId is { } primary ? AccountsByConnectionId.GetValueOrDefault(primary) : null;
    }

    /// <summary>
    /// The display projection of the persisted account pause at the caller's already-captured instant.
    /// A null or blank id keeps the existing rule and resolves only through the primary Dropbox account.
    /// </summary>
    public bool AccountPaused(string? connectionId, DateTimeOffset nowUtc) => PauseStateProjection.AccountPaused(
        Preferences, string.IsNullOrEmpty(connectionId) ? PrimaryDropboxConnectionId : connectionId, nowUtc);

    /// <summary>
    /// The display projection over one pair's ordered nonlocal connection ids, exactly as the existing
    /// pair rule resolves them.
    /// </summary>
    public bool PairAccountPaused(IReadOnlyList<string> pairConnectionIds, DateTimeOffset nowUtc) =>
        pairConnectionIds.Any(id => AccountPaused(id, nowUtc));

    public bool GlobalPaused(DateTimeOffset nowUtc) => PauseStateProjection.GlobalPaused(Preferences, nowUtc);

    /// <summary>
    /// The existing pair-to-account rule, resolved from this snapshot instead of a fresh store read. A pair
    /// with an endpoint document uses its nonlocal endpoint connection ids; an accountless local/local pair
    /// stays accountless; a legacy pair resolves a blank saved id through the primary Dropbox account only.
    /// </summary>
    public IReadOnlyList<string> PairConnectionIds(SyncPair pair)
    {
        if (pair.EndpointDocument is { } document)
            return new[] { document.Left, document.Right }.Where(endpoint => endpoint is { IsLocal: false })
                .Select(endpoint => endpoint!.ConnectionId).Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
        if (SyncProviders.Normalize(pair.Provider) == SyncProviders.Local) return [];
        var connectionId = string.IsNullOrEmpty(pair.ConnectionId) ? PrimaryDropboxConnectionId : pair.ConnectionId;
        return connectionId is null ? [] : [connectionId];
    }
}

public sealed partial class SyncAuthorityRuntimeHost
{
    /// <summary>
    /// SS-D12 diagnostic seam: fired once for every real store read the presentation snapshot performs,
    /// carrying the store identity and the record count it actually parsed. It measures real store loads,
    /// so a hidden extra read is counted; it never supplies the read itself and never changes a decision.
    /// </summary>
    internal static Action<string, int>? PresentationStoreLoadForTest { get; set; }

    /// <summary>
    /// SS-D12: the one validated presentation projection for a synchronous refresh. Three validated store
    /// reads, independent of the number of folder pairs: the whole active Dropbox connection collection, the
    /// whole active Google Drive connection collection and the normalized UI preferences. Each store keeps
    /// its own validation, including whole-active-collection validation, so a rebind, edit or corrupt slot is
    /// observed at the next refresh and never masked by cached display state.
    /// </summary>
    public SyncPresentationSnapshot LoadPresentationSnapshot()
    {
        var dropbox = ReadPresentationStore("dropbox-connections", () => connectionStore.LoadActiveBindings(),
            list => list.Count);
        var google = ReadPresentationStore("google-drive-connections",
            () => _googleDriveConnections.LoadActiveBindings(), list => list.Count);
        var preferences = ReadPresentationStore("ui-preferences",
            () => new UiPreferencesStore(stateAuthority).Load(), value => value.AccountPauses?.Count ?? 0);

        // Metadata only: the provider binding objects, credential references and tokens are dropped here.
        var dropboxAccounts = dropbox.Select(item => SyncAccountBinding.From(item)).ToArray();
        var googleAccounts = google.Select(item => SyncAccountBinding.From(item)).ToArray();
        // The active Dropbox collection is the store's own primary-first order, and the primary account is its
        // first element. The presentation order then matches the existing provider-connection list: the
        // Dropbox accounts in connection-id order, then the Google Drive accounts in their stored order.
        var primaryDropboxId = dropboxAccounts.Length > 0 ? dropboxAccounts[0].ConnectionId : null;
        var ordered = dropboxAccounts.OrderBy(item => item.Provider, StringComparer.Ordinal)
            .ThenBy(item => item.ConnectionId, StringComparer.Ordinal).Concat(googleAccounts).ToArray();
        // The exact-id lookup keeps the existing order of the authority method: a Google Drive account wins
        // over a Dropbox account for the same id, so the Google Drive entries are indexed last.
        var index = new Dictionary<string, SyncAccountBinding>(StringComparer.Ordinal);
        foreach (var account in ordered) index[account.ConnectionId] = account;
        var primaryId = primaryDropboxId ??
            (googleAccounts.Length > 0 ? googleAccounts[0].ConnectionId : null);
        return new SyncPresentationSnapshot(ordered, index, primaryDropboxId, primaryId, preferences);
    }

    private static T ReadPresentationStore<T>(string store, Func<T> load, Func<T, int> parsedCount)
    {
        var value = load();
        PresentationStoreLoadForTest?.Invoke(store, parsedCount(value));
        return value;
    }
}
