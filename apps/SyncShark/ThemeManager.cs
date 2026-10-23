using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SystemColors = System.Windows.SystemColors;

namespace Massdex.SyncShark;

public sealed class ThemeManager(UiPreferencesStore store) : IDisposable
{
    private string _preference = UiPreferencesStore.SystemTheme;

    public string Preference => _preference;

    public void Initialize()
    {
        _preference = store.Load().Theme;
        Apply();
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;
    }

    public async Task SetPreferenceAsync(string preference, CancellationToken cancellationToken = default)
    {
        await store.SaveAsync(store.Load() with { Theme = preference }, cancellationToken);
        _preference = store.Load().Theme;
        Apply();
    }

    internal void PreviewPreference(string preference)
    {
        if (preference is not (UiPreferencesStore.SystemTheme or UiPreferencesStore.LightTheme or UiPreferencesStore.DarkTheme or UiPreferencesStore.AmoledTheme))
            throw new ArgumentOutOfRangeException(nameof(preference));
        _preference = preference;
        Apply();
    }

    public void Dispose() => SystemParameters.StaticPropertyChanged -= SystemParametersChanged;

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
