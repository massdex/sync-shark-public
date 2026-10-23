using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static partial class Program
{
    private const BindingFlags DiscardFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static T DiscardField<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, DiscardFlags)?.GetValue(target)
            ?? throw new MissingFieldException(target.GetType().FullName, name));

    private static void DiscardCall(object target, string name, params object?[] arguments) =>
        (target.GetType().GetMethods(DiscardFlags).FirstOrDefault(method =>
                method.Name == name && method.GetParameters().Length == arguments.Length)
         ?? throw new MissingMethodException(target.GetType().FullName, name)).Invoke(target, arguments);

    private static System.Windows.Shapes.Path DiscardGlyph(Button button)
    {
        var panel = (StackPanel)button.Content;
        var viewbox = (Viewbox)panel.Children[0];
        return (System.Windows.Shapes.Path)((Canvas)viewbox.Child).Children[0];
    }

    private static bool DiscardIsTrayRed(Button button) =>
        DiscardGlyph(button).Stroke is SolidColorBrush { Color: { R: 0xE7, G: 0x49, B: 0x49 } };

    private static bool DiscardIsBin(Button button) =>
        string.Equals(DiscardGlyph(button).Data.ToString(), CommandIcons.GeometryFor("delete").ToString(),
            StringComparison.Ordinal);

    private static bool DiscardIsClose(Button button) =>
        string.Equals(DiscardGlyph(button).Data.ToString(), CommandIcons.GeometryFor("close").ToString(),
            StringComparison.Ordinal);

    private static int RunDiscardPresentationChecks(string rawRoot)
    {
        var root = Path.GetFullPath(rawRoot);
        Directory.CreateDirectory(root);
        var localRoot = Directory.CreateDirectory(Path.Combine(root, "local")).FullName;
        var priorStartup = MainWindow.SuppressLoadedStartupForTest;
        App? app = null;
        MainWindow? window = null;
        PilotInstanceLock? instance = null;
        ThemeManager? theme = null;
        try
        {
            app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
            theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
            theme.Initialize();
            var preferences = new UiPreferencesStore(instance.StateAuthority);
            using var http = new HttpClient(new EndpointUiNoNetwork());
            using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http,
                new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
                new DropboxConnectionStore(instance.StateAuthority), new InspectionCredentialBoundary(),
                new SyncAuthorityDatabase(Path.Combine(root, "state", "authority.db")));
            Task.Run(() => host.LoadPairsAsync()).GetAwaiter().GetResult();
            MainWindow.SuppressLoadedStartupForTest = true;
            window = new MainWindow(instance.StateAuthority, theme)
            {
                Width = 1240, Height = 790, Left = -20000, Top = -20000, ShowInTaskbar = false,
                ShowActivated = false
            };
            BindAuthorityHost(window, host);
            window.Show();
            AssertDiscardFactory();
            AssertDiscardSettingsConsumer(window, preferences);
            AssertDiscardPairConsumers(window, localRoot);
            AssertDiscardDialogChoice(window);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result = "PASS",
                selector = "--discard-presentation-checks",
                source = "CommandIcons.SetUnsavedAction and its real Settings, pair and dialog consumers",
                consumers = new[] { "settings-draft", "existing-pair", "new-pair", "dialog-three-choice" }
            }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            MainWindow.SuppressLoadedStartupForTest = priorStartup;
            window?.RequestProductExit();
            theme?.Dispose();
            instance?.Dispose();
            app?.Shutdown();
        }
    }

    // The one factory: explicit intent decides the glyph and the destructive red, and nothing infers
    // deletion from the label text.
    private static void AssertDiscardFactory()
    {
        var discard = new Button();
        CommandIcons.SetUnsavedAction(discard, "Discard Changes", UnsavedActionIntent.Discard);
        EndpointUiRequire(DiscardIsBin(discard) && DiscardIsTrayRed(discard) &&
            System.Windows.Automation.AutomationProperties.GetName(discard) == "Discard Changes",
            "discard presentation: a discard intent gets the bin glyph in the tray red with its accessible name");
        var neutral = new Button();
        CommandIcons.SetUnsavedAction(neutral, "Keep editing", UnsavedActionIntent.Neutral);
        EndpointUiRequire(DiscardIsClose(neutral) && !DiscardIsTrayRed(neutral) &&
            System.Windows.Automation.AutomationProperties.GetName(neutral) == "Keep editing",
            "discard presentation: a neutral intent keeps the close glyph and never takes the destructive red");
        EndpointUiRequire(!DiscardIsBin(neutral) &&
            !string.Equals(CommandIcons.GeometryFor("delete").ToString(),
                CommandIcons.GeometryFor("close").ToString(), StringComparison.Ordinal),
            "discard presentation: cancel, close and keep-editing keep their own distinct glyph");
        EndpointUiRequire(!discard.IsDefault && !neutral.IsDefault && !discard.IsCancel && !neutral.IsCancel,
            "discard presentation: a destructive choice never becomes the safe default action");
        var discardIntoClean = new Button();
        CommandIcons.SetUnsavedAction(discardIntoClean, "Discard Changes", UnsavedActionIntent.Discard);
        EndpointUiRequire(discardIntoClean.ToolTip as string == "Discard only these unsaved changes; saved data stays unchanged." &&
            System.Windows.Automation.AutomationProperties.GetHelpText(discardIntoClean) == "Discard only these unsaved changes; saved data stays unchanged.",
            "discard presentation: the dirty draft describes discarding only the unsaved changes");
        CommandIcons.SetUnsavedAction(discardIntoClean, "Close", UnsavedActionIntent.Neutral);
        EndpointUiRequire(discardIntoClean.ToolTip as string == "Close this editor without changing saved data." &&
            System.Windows.Automation.AutomationProperties.GetHelpText(discardIntoClean) == "Close this editor without changing saved data.",
            "discard presentation: cleaning the same draft replaces the discard wording with the neutral Close description");
        var neutralCancel = new Button();
        CommandIcons.SetUnsavedAction(neutralCancel, "Cancel", UnsavedActionIntent.Neutral);
        EndpointUiRequire(neutralCancel.ToolTip as string == "Leave this draft without applying changes." &&
            System.Windows.Automation.AutomationProperties.GetHelpText(neutralCancel) == "Leave this draft without applying changes.",
            "discard presentation: a neutral Cancel leaves the draft without applying changes");
        var otherNeutral = new Button();
        CommandIcons.SetUnsavedAction(otherNeutral, "Keep editing", UnsavedActionIntent.Neutral);
        EndpointUiRequire(otherNeutral.ToolTip is null &&
            string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetHelpText(otherNeutral)),
            "discard presentation: an unrelated neutral choice is never given the Close or Cancel wording");
    }

    private static void AssertDiscardSettingsConsumer(MainWindow window, UiPreferencesStore preferences)
    {
        // Settings is opened from its own gear action rather than the page list, so this uses the
        // product's own navigation entry point.
        DiscardCall(window, "OpenWorkspaceSettings");
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        window.UpdateLayout();
        var style = DiscardField<ComboBox>(window, "_settingsIconStyle");
        var discard = DiscardField<Button>(window, "_settingsDiscard");
        var saved = preferences.Load().StatusIconStyle;
        EndpointUiRequire(DiscardIsBin(discard) && DiscardIsTrayRed(discard) &&
            System.Windows.Automation.AutomationProperties.GetName(discard) == "Discard Changes",
            "discard presentation: the real General settings Discard Changes is the red bin, not a blue close");
        style.SelectedItem = saved == "Colour" ? "Symbol" : "Colour";
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        EndpointUiRequire(discard.Visibility == Visibility.Visible &&
            !Equals(style.SelectedItem, saved) && preferences.Load().StatusIconStyle == saved,
            "discard presentation: an unsaved General draft offers the destructive Discard without persisting it");
        discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        EndpointUiRequire(Equals(style.SelectedItem, saved) && discard.Visibility != Visibility.Visible &&
            DiscardIsBin(discard) && DiscardIsTrayRed(discard),
            "discard presentation: the settings round trip restores the draft and keeps the destructive presentation");
    }

    private static void AssertDiscardPairConsumers(MainWindow window, string localRoot)
    {
        var pair = new SyncPair("discard-pair", "Discard pair", localRoot, 1, 1, "/Shared", "id:shared",
            ConnectionId: "discard-connection", Provider: SyncProviders.Dropbox, InitialReviewApproved: true);
        window.PrepareExistingPairEditorForTest(pair);
        window.SetExistingFolderSelectionForTest("Work", true, "id:local-work");
        DiscardCall(window, "UpdatePairSaveAvailability");
        window.UpdateLayout();
        var cancel = (Button)window.FindName("CancelPairButton")!;
        EndpointUiRequire(DiscardIsBin(cancel) && DiscardIsTrayRed(cancel) &&
            cancel.Content is StackPanel { Children: [Viewbox, TextBlock { Text: "Discard Changes" }] },
            "discard presentation: an existing pair with a draft offers Discard Changes as the red bin");
        DiscardCall(window, "BeginNewPairEditor");
        DiscardCall(window, "UpdatePairSaveAvailability");
        window.UpdateLayout();
        var newCancel = (Button)window.FindName("CancelPairButton")!;
        EndpointUiRequire(DiscardIsClose(newCancel) && !DiscardIsTrayRed(newCancel) &&
            newCancel.Content is StackPanel { Children: [Viewbox, TextBlock { Text: "Cancel" }] },
            "discard presentation: a new unsaved pair keeps a neutral Cancel rather than a delete");
    }

    private static void AssertDiscardDialogChoice(MainWindow window)
    {
        var inspected = false;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var dialog = Application.Current.Windows.OfType<AppDialogWindow>().LastOrDefault();
            if (dialog is null) return;
            var primary = (Button)dialog.FindName("PrimaryButton")!;
            var secondary = (Button)dialog.FindName("SecondaryButton")!;
            var tertiary = (Button)dialog.FindName("TertiaryButton")!;
            inspected = DiscardIsBin(secondary) && DiscardIsTrayRed(secondary) && DiscardIsClose(tertiary) &&
                        !DiscardIsTrayRed(tertiary) && primary.IsDefault && !secondary.IsDefault &&
                        System.Windows.Automation.AutomationProperties.GetName(secondary) == "Discard Changes";
            dialog.Close();
        });
        var choice = AppDialogWindow.Choose(window, "Unsaved Changes", "Save this folder pair before leaving?",
            "Save its changes, discard its draft, or stay in this editor.", "Save Changes", "Discard Changes",
            "Stay Here", secondaryIsDiscard: true);
        EndpointUiRequire(inspected,
            "discard presentation: the Save, Discard and Stay choice marks only its discard as destructive");
        EndpointUiRequire(choice == AppDialogChoice.Cancel,
            "discard presentation: closing the unsaved choice without a decision changes nothing");
    }
}
