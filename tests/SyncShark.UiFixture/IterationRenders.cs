using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static partial class Program
{
    private static int RenderIterationViews(string output, string? only = null)
    {
        Directory.CreateDirectory(output);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(output, "fixture-state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(appLock.StateAuthority));
        theme.Initialize(); MainWindow.SuppressLoadedStartupForTest = true;
        var folder = Path.Combine(output, "fixture-local"); Directory.CreateDirectory(folder);
        PilotDirectoryIdentity identity;
        using (var lease = PilotDirectoryLease.Open(folder)) identity = lease.Identity;
        var pair = new SyncPair("fixture-pair", "Documents", folder, identity.VolumeSerialNumber, identity.FileId,
            "/Documents", "id:fixture", InitialReviewApproved: true);
        var navigate = typeof(MainWindow).GetMethod("NavigateTo", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            foreach (var appearance in new[] { "Light", "Dark" })
            {
                theme.SetPreferenceAsync(appearance).GetAwaiter().GetResult();
                var pages = new List<string> { "Dashboard", "Activity", "Pairs", "Attention", "Recovery", "Connections", "Settings" };
                if (appearance == "Dark") pages.AddRange(["Pairs-expanded", "Connections-expanded", "Settings-sync", "Settings-explorer", "Settings-appinfo", "Settings-diagnostics", "Activity-details", "Dashboard-collapsed"]);
                foreach (var page in pages)
                {
                    if (only is not null && only != (appearance + "-" + page).ToLowerInvariant()) continue;
                    var window = new MainWindow(appLock.StateAuthority, theme) { Width = 1280, Height = 820 };
                    var renderPath = Path.Combine(output, appearance.ToLowerInvariant() + "-" + page.ToLowerInvariant() + ".png");
                    RenderWindow(window, renderPath, 1280, 820, true, value =>
                        {
                            window.LoadPairSetForTest(new SyncPairSet([pair], pair.Id));
                            SeedMainWindow(window);
                            navigate.Invoke(window, [page.Split('-')[0], true]);
                            PumpTask(window, Task.Delay(250));
                            var track = typeof(MainWindow).GetMethod("TrackWorkspaceAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
                            foreach (var row in new[] {
                                new ActivityRow(DateTimeOffset.Now.ToString("O"), "cloud test.txt", "Completed download", "Downloaded and verified.", PairId: pair.Id, PairName: pair.Name, ItemKind: "File", FileType: "TXT"),
                                new ActivityRow(DateTimeOffset.Now.AddMinutes(-1).ToString("O"), "manual.pdf", "Completed upload", "Uploaded and verified.", PairId: pair.Id, PairName: pair.Name, ItemKind: "File", FileType: "PDF") })
                            { window.ActivityRows.Add(row); track.Invoke(window, [row]); }
                            typeof(MainWindow).GetMethod("RefreshWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                            // Allow the real asynchronous shell-association worker to replace its loading image.
                            PumpTask(window, Task.Delay(800));
                            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                            if (page == "Pairs-expanded")
                                typeof(MainWindow).GetMethod("ExpandPairRow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window.PairRows.First()]);
                            if (page == "Connections-expanded")
                                foreach (var expander in FindVisualChildren<Expander>(window)) expander.IsExpanded = true;
                            if (page.StartsWith("Settings-", StringComparison.Ordinal))
                            {
                                // Each Settings target names its own tab; a target must never
                                // silently render a different tab than the one it is named for.
                                var (index, header) = page switch
                                {
                                    "Settings-sync" => (1, "Sync & Performance"),
                                    "Settings-explorer" => (3, "Explorer"),
                                    "Settings-appinfo" => (4, "About & Updates"),
                                    _ => (5, "Diagnostics")
                                };
                                var tabs = (TabControl)typeof(MainWindow).GetField("_settingsTabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                                tabs.SelectedIndex = index;
                                if (tabs.Items[index] is not TabItem tab || tab.Header.ToString() != header)
                                    throw new InvalidDataException("The " + page + " render did not select the " + header + " tab.");
                            }
                            if (page == "Activity-details") window.OpenWorkspaceActivity(0, window.ActivityRows.First());
                            if (page == "Dashboard-collapsed")
                                typeof(MainWindow).GetMethod("SidebarToggle_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
                            PumpTask(window, Task.Delay(150));
                        });
                    if (page.StartsWith("Settings-", StringComparison.Ordinal)) AssertSettingsFrame(renderPath, page);
                    Console.WriteLine("RENDER " + appearance + " " + page);
                }
                if (only is not null && only != (appearance + "-tray").ToLowerInvariant()) continue;
                var owner = new MainWindow(appLock.StateAuthority, theme);
                owner.LoadPairSetForTest(new SyncPairSet([pair], pair.Id));
                SeedMainWindow(owner);
                var trackTray = typeof(MainWindow).GetMethod("TrackWorkspaceAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
                foreach (var row in new[] {
                    new ActivityRow(DateTimeOffset.Now.ToString("O"), "cloud test.txt", "Completed download", "Downloaded and verified.", PairId: pair.Id, PairName: pair.Name, ItemKind: "File", FileType: "TXT"),
                    new ActivityRow(DateTimeOffset.Now.AddMinutes(-1).ToString("O"), "manual.pdf", "Completed upload", "Uploaded and verified.", PairId: pair.Id, PairName: pair.Name, ItemKind: "File", FileType: "PDF") })
                { owner.ActivityRows.Add(row); trackTray.Invoke(owner, [row]); }
                typeof(MainWindow).GetMethod("RefreshWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null);
                var tray = new TrayFlyoutWindow(owner);
                RenderWindow(tray, Path.Combine(output, appearance.ToLowerInvariant() + "-tray.png"), 414, 620, true, _ =>
                {
                    typeof(TrayFlyoutWindow).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, null);
                    PumpTask(owner, Task.Delay(800));
                });
                owner.RequestProductExit();
            }
            return 0;
        }
        finally { MainWindow.SuppressLoadedStartupForTest = false; app.Shutdown(); }
    }

    // A named Settings target has to leave its own readable frame behind: a missing,
    // empty or undecodable artifact must not pass as a rendered tab.
    private static void AssertSettingsFrame(string path, string page)
    {
        if (!File.Exists(path)) throw new InvalidDataException("The " + page + " render produced no frame.");
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth <= 0 || decoder.Frames[0].PixelHeight <= 0)
            throw new InvalidDataException("The " + page + " render produced no readable frame.");
    }
}
