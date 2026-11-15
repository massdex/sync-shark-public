namespace Massdex.SyncShark;

public sealed class UiPreferencesStore
{
    public const string SystemTheme = "System";
    public const string LightTheme = "Light";
    public const string AmoledTheme = "AMOLED";
    public const string DarkTheme = "Dark";
    internal static readonly string[] Themes = [SystemTheme, LightTheme, DarkTheme, AmoledTheme];
    public const string SimpleView = "Simple";
    public const string DetailedView = "Detailed";
    // U1 presentation contract: the three selectable status-icon styles. These are the one canonical
    // spelling of the persisted preference; the shared presentation reads them instead of repeating
    // a private copy of the same three strings.
    public const string ColourIconSet = "Colour";
    public const string SymbolIconSet = "Symbol";
    public const string CombinedIconSet = "Combined";
    public const string DefaultIconSet = CombinedIconSet;
    public static readonly string[] IconSets = [ColourIconSet, SymbolIconSet, CombinedIconSet];

    // One canonical supported-value test: exact, case-sensitive and ordinal like the stored value.
    public static bool IsSupportedIconSet(string? iconSet) =>
        iconSet is not null && Array.IndexOf(IconSets, iconSet) >= 0;

    public static bool IsStatusIconStyle(string? value) => IsSupportedIconSet(value);

    internal static bool IsSupportedTheme(string? theme) => theme is not null && Array.IndexOf(Themes, theme) >= 0;

    internal static bool IsViewMode(string? value) => value is SimpleView or DetailedView;

    internal static bool IsPageKey(string key) => System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z]{3,24}$");

    private readonly MigratingMutableJsonStore<UiPreferences> _store;

    public UiPreferencesStore(PilotStateAuthority authority) =>
        _store = new MigratingMutableJsonStore<UiPreferences>(authority,
            SyncSharkRuntimePaths.UiPreferencesFileName);

    public UiPreferences Load() => Resolve(_store.Load());

    public Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken = default) =>
        _store.SaveAsync(Normalize(preferences), cancellationToken);

    /// <summary>Reads the saved preferences without migrating, seeding or writing anything.</summary>
    public Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Resolve(_store.Read()));
    }

    /// <summary>Merges only the patch's fields into the current saved values and returns the committed result.</summary>
    public Task<UiPreferences> UpdateAsync(UiPreferencesUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        update.Validate();
        return UpdateAsync(update.ApplyTo, cancellationToken);
    }

    /// <summary>For already-authorized local callers: the transform runs against current values inside the store's
    /// gate, so it must derive its result from its argument. It is never serialized over IPC.</summary>
    public Task<UiPreferences> UpdateAsync(Func<UiPreferences, UiPreferences> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return Task.FromResult(_store.Update(stored => Normalize(update(Resolve(stored))), cancellationToken));
    }

    private static UiPreferences Resolve(UiPreferences? stored) => Normalize(stored ?? new UiPreferences(SystemTheme));

    private static UiPreferences Normalize(UiPreferences preferences)
    {
        var theme = preferences.Theme.Trim();
        if (!IsSupportedTheme(theme))
            throw new InvalidDataException("The saved UI theme preference is not supported.");
        // An absent or unsupported style resolves to the frozen combined default instead of failing
        // the whole preference load. That keeps exactly one deterministic supported style for the
        // shared presentation while never persisting an unrecognised value as authority.
        preferences = preferences with
        {
            StatusIconStyle = IsSupportedIconSet(preferences.StatusIconStyle) ? preferences.StatusIconStyle : DefaultIconSet
        };
        var pauses = (preferences.AccountPauses ?? []).ToArray();
        if (pauses.Any(item => !PauseScopeRules.IsExactConnectionId(item.ConnectionId)) ||
            pauses.Select(item => item.ConnectionId).Distinct().Count() != pauses.Length)
            throw new InvalidDataException(
                "Account pause state is not bound to unique Dropbox or Google Drive connection identities.");
        // A timezone that no longer resolves is dropped rather than failing the whole load; the
        // persisted UTC deadline stays authoritative.
        if (preferences.PauseTimeZoneId is not null && PauseSchedule.TryResolveZone(preferences.PauseTimeZoneId) is null)
            preferences = preferences with { PauseTimeZoneId = null };
        pauses = pauses.Select(item => item.TimeZoneId is not null && PauseSchedule.TryResolveZone(item.TimeZoneId) is null
            ? item with { TimeZoneId = null }
            : item).ToArray();
        var modes = preferences.PageViewModes is null
            ? null
            : preferences.PageViewModes.Where(item => IsViewMode(item.Value))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        if (modes is not null && modes.Keys.Any(key => !IsPageKey(key)))
            throw new InvalidDataException("A saved page view mode is not bound to a supported page key.");
        return preferences with { Theme = theme, AccountPauses = pauses, PageViewModes = modes };
    }
}
