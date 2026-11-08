using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SystemColors = System.Windows.SystemColors;

namespace Massdex.SyncShark;

public sealed class ThemeManager : IDisposable
{
    private readonly Func<CancellationToken, Task<UiPreferences>> _load;
    private readonly Func<UiPreferencesUpdate, CancellationToken, Task<UiPreferences>> _update;
    private readonly UiPreferencesStore? _legacyStore;
    private string _preference = UiPreferencesStore.SystemTheme;
    private bool _subscribed;
    private bool _disposed;

    public ThemeManager(UiPreferencesStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _legacyStore = store;
        _load = cancellationToken => store.LoadAsync(cancellationToken);
        _update = (update, cancellationToken) => store.UpdateAsync(update, cancellationToken);
    }

    internal ThemeManager(Func<CancellationToken, Task<UiPreferences>> load,
        Func<UiPreferencesUpdate, CancellationToken, Task<UiPreferences>> update)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(update);
        _load = load;
        _update = update;
    }

    public string Preference => _preference;

    public void Initialize()
    {
        if (_legacyStore is null)
            throw new InvalidOperationException("This ThemeManager is initialized asynchronously.");
        SetTheme(_legacyStore.Load().Theme);
        Subscribe();
    }

    internal async Task<UiPreferences> InitializeAsync(CancellationToken cancellationToken)
    {
        var preferences = await _load(cancellationToken).ConfigureAwait(false);
        await OnDispatcherAsync(() => { SetTheme(preferences.Theme); Subscribe(); }).ConfigureAwait(false);
        return preferences;
    }

    public async Task SetPreferenceAsync(string preference, CancellationToken cancellationToken = default)
    {
        ValidatePreference(preference);
        await OnDispatcherAsync(() => SetTheme(preference)).ConfigureAwait(false);
        var committed = await _update(new UiPreferencesUpdate { Theme = preference }, cancellationToken)
            .ConfigureAwait(false);
        // A newer preview or save owns the selection; an older acknowledgement must not clear it.
        await OnDispatcherAsync(() =>
        {
            if (_preference == preference) SetTheme(committed.Theme);
        }).ConfigureAwait(false);
    }

    internal void PreviewPreference(string preference)
    {
        ValidatePreference(preference);
        SetTheme(preference);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_subscribed) SystemParameters.StaticPropertyChanged -= SystemParametersChanged;
    }

    private static void ValidatePreference(string preference)
    {
        if (preference is not (UiPreferencesStore.SystemTheme or UiPreferencesStore.LightTheme or UiPreferencesStore.DarkTheme or UiPreferencesStore.AmoledTheme))
            throw new ArgumentOutOfRangeException(nameof(preference));
    }

    private void Subscribe()
    {
        if (_subscribed || _disposed) return;
        _subscribed = true;
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;
    }

    private Task OnDispatcherAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(action).Task;
    }

    private void SetTheme(string theme)
    {
        if (_disposed) return;
        _preference = theme;
        Apply();
    }

    internal static void ApplyHighContrastForEvidence() => ApplyHighContrast();

    private void SystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        Apply();
    }

    private void Apply()
    {
        if (SystemParameters.HighContrast)
        {
            ApplyHighContrast();
            return;
        }
        var dark = _preference is UiPreferencesStore.AmoledTheme or UiPreferencesStore.DarkTheme ||
                   (_preference == UiPreferencesStore.SystemTheme && !WindowsAppsUseLightTheme());
        if (dark)
        {
            ApplyAmoled();
            if (_preference != UiPreferencesStore.AmoledTheme)
            { Set("Canvas", "#0E1114"); Set("Surface", "#14181D"); Set("RaisedSurface", "#191E24"); }
        }
        else ApplyLight();
    }

    private static bool WindowsAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch { return true; }
    }

    private static void ApplyLight()
    {
        Set("DropboxBlue", "#0061FE"); Set("DropboxCloud", "#B4C8E1"); Set("PrimaryInk", "#FFFFFF"); Set("SelectedInk", "#1E1919");
        Set("Canvas", "#F7F5F2"); Set("Surface", "#FFFFFF"); Set("RaisedSurface", "#FFFFFF");
        Set("Ink", "#1E1919"); Set("MutedInk", "#5B5650"); Set("Border", "#D3CEC9"); Set("Divider", "#EBE9E6");
        Set("ControlSurface", "#FFFFFF"); Set("HoverSurface", "#EBE9E6"); Set("SelectedSurface", "#B4C8E1"); Set("DisabledSurface", "#D3CEC9");
        Set("ScrollThumb", "#817A74");
        Set("FocusBrush", "#0061FE"); Set("SuccessBrush", "#0F503C"); Set("WarningBrush", "#9B6400"); Set("ErrorBrush", "#9B0032"); Set("ErrorButtonInk", "#FFFFFF");
    }

    private static void ApplyAmoled()
    {
        Set("DropboxBlue", "#0061FE"); Set("DropboxCloud", "#B4C8E1"); Set("PrimaryInk", "#FFFFFF"); Set("SelectedInk", "#1E1919");
        Set("Canvas", "#000000"); Set("Surface", "#101113"); Set("RaisedSurface", "#15171A");
        Set("Ink", "#F1F3F6"); Set("MutedInk", "#A5ABB5"); Set("Border", "#2C3037"); Set("Divider", "#2C3037");
        Set("ControlSurface", "#202328"); Set("HoverSurface", "#242931"); Set("SelectedSurface", "#202D41"); Set("DisabledSurface", "#242321");
        Set("SelectedInk", "#F1F3F6");
        Set("ScrollThumb", "#514D49");
        Set("FocusBrush", "#87B5FF"); Set("SuccessBrush", "#8CD4A6"); Set("WarningBrush", "#E8BC74"); Set("ErrorBrush", "#E74949"); Set("ErrorButtonInk", "#1E1919");
    }

    private static void ApplyHighContrast()
    {
        Set("DropboxBlue", SystemColors.HighlightBrush); Set("DropboxCloud", SystemColors.HighlightBrush);
        Set("PrimaryInk", SystemColors.HighlightTextBrush); Set("SelectedInk", SystemColors.HighlightTextBrush);
        Set("Canvas", SystemColors.WindowBrush); Set("Surface", SystemColors.WindowBrush); Set("RaisedSurface", SystemColors.WindowBrush);
        Set("Ink", SystemColors.WindowTextBrush); Set("MutedInk", SystemColors.GrayTextBrush); Set("Border", SystemColors.WindowTextBrush); Set("Divider", SystemColors.WindowTextBrush);
        Set("ControlSurface", SystemColors.ControlBrush); Set("HoverSurface", SystemColors.HighlightBrush); Set("SelectedSurface", SystemColors.HighlightBrush); Set("DisabledSurface", SystemColors.ControlBrush);
        Set("ScrollThumb", SystemColors.WindowTextBrush);
        Set("FocusBrush", SystemColors.HighlightBrush); Set("SuccessBrush", SystemColors.WindowTextBrush); Set("WarningBrush", SystemColors.WindowTextBrush); Set("ErrorBrush", SystemColors.WindowTextBrush); Set("ErrorButtonInk", SystemColors.HighlightTextBrush);
    }

    private static void Set(string key, string color) => Set(key, new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)));

    private static void Set(string key, System.Windows.Media.Brush brush) =>
        System.Windows.Application.Current.Resources[key] = brush;
}
