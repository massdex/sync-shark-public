using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

// The dedicated provider/package check for this release correction. It exercises the production
// entry points - ExplorerPackage.Validate against the real indexed member inventory, and the real
// MainWindow pair editor and Pairs grid - rather than asserting helper constants, and it proves
// both the legitimate success and the safe rejection of every stated contract.
internal static class ProviderPackageChecks
{
    private const string IndexedVersion = "0.3.36.0";
    private static readonly string[] ShellSizes = { "16", "20", "24", "32", "48", "64", "256" };
    private static readonly string[] ShellForms = { "", "_altform-unplated", "_altform-lightunplated" };

    internal static int Run(string root)
    {
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        MainWindow.SuppressLoadedStartupForTest = true;
        MainWindow.PairFolderBrowserFactoryForTest = _ =>
            Task.FromResult<IDropboxFolderBrowserService>(new EmbeddedDropboxPagingFixture());
        var passed = 0;
        void Check(bool value, string label)
        { if (!value) throw new InvalidOperationException(label); passed++; Console.WriteLine("PASS " + label); }
        void Reject(Action action, string label)
        {
            try { action(); }
            catch (Exception error) when (error is IOException or ArgumentException or System.Xml.XmlException)
            { Check(true, label); return; }
            throw new InvalidOperationException("Expected a rejection: " + label);
        }

        IndexedShellMemberChecks();
        PackageContractChecks();
        ProviderIdentityChecks();
        ProviderPresentationChecks(Path.GetFullPath(root));
        MainWindow.PairFolderBrowserFactoryForTest = null;
        Console.WriteLine(passed + " dedicated provider/package checks passed; no provider call, credential, package registration or product-state effect was performed.");
        return 0;

        void ProviderIdentityChecks()
        {
            Check(Enum.GetValues<SyncEndpointKind>().Order().SequenceEqual(
                SyncEndpointProviders.All.Select(item => item.Kind).Order()),
                "Every endpoint kind has exactly one registered provider definition");
            Check(SyncEndpointProviders.All.All(item => SyncEndpointPresentation.Choice(item.Kind).Kind == item.Kind),
                "Every registered provider has a shared presentation instead of a fallback label");
            foreach (var provider in SyncEndpointProviders.All.Where(item => item.ProviderId is not null))
            {
                Check(SyncProviders.IsKnown(provider.ProviderId) &&
                    SyncProviders.DisplayName(provider.ProviderId) == provider.DisplayName,
                    "Registered provider identity and public name agree: " + provider.ProviderId);
                var choice = new StorageAccountChoice("exact-account", "Unrelated display name",
                    "Google is a user-selected label", true, provider.ProviderId!);
                Check(choice.ProviderKey == provider.ProviderId && choice.CompactLabel == choice.Account,
                    "Account presentation retains explicit provider identity: " + provider.ProviderId);
            }
            // U1 froze the account picker's provider identity: a stored connection's exact provider id
            // stays authoritative, the registered display name resolves a hand-built choice, and an
            // all-accounts or unrecognised choice claims no provider at all instead of guessing one.
            Check(new StorageAccountChoice("future-account", "Google Drive", "Holder", true, "future-provider").ProviderKey == "future-provider" &&
                new StorageAccountChoice("future-account", "Google Drive", "Holder", true).ProviderKey == SyncProviders.GoogleDrive &&
                new StorageAccountChoice("", "All Providers", "All Accounts", true).ProviderKey == "unknown" &&
                !SyncProviders.IsKnown(new StorageAccountChoice("", "All Providers", "All Accounts", true).ProviderKey),
                "An explicit provider id wins, a registered display name resolves, and an all-accounts choice claims no provider");
            Check(!SyncProviders.IsKnown("unknown") && !SyncProviders.IsKnown("DROPBOX") &&
                SyncProviders.Normalize(null) == SyncProviders.Dropbox &&
                SyncProviders.DisplayName("unknown") == "Cloud storage",
                "Canonical provider validation and legacy default retain their existing semantics");
            Check(SyncContentIdentity.AlgorithmFor(SyncProviders.Dropbox) == SyncContentIdentity.DropboxBlockSha256 &&
                SyncContentIdentity.AlgorithmFor(SyncProviders.GoogleDrive) == SyncContentIdentity.Sha256 &&
                SyncContentIdentity.AlgorithmFor(SyncProviders.Local) == SyncContentIdentity.DropboxBlockSha256,
                "Each endpoint selects its registered content algorithm explicitly");
            try { _ = SyncContentIdentity.Compute("future-provider", new byte[] { 1 }); }
            catch (NotSupportedException)
            { Check(true, "An unknown provider cannot silently hash bytes using Dropbox semantics"); return; }
            throw new InvalidOperationException("An unknown provider inherited a content algorithm.");
        }

        void IndexedShellMemberChecks()
        {
            using (var indexed = Indexed(IndexedVersion, indexed: true))
            {
                ExplorerPackage.Validate(indexed, new Version(IndexedVersion));
                Check(true, "The exact indexed member inventory is accepted by the production package predicate");
            }
            using (var legacy = Indexed(IndexedVersion, indexed: false))
            {
                ExplorerPackage.Validate(legacy, new Version(IndexedVersion));
                Check(true, "A legacy member set without indexed shell resources keeps its previous validation");
            }
        }

        void PackageContractChecks()
        {
            var version = new Version(IndexedVersion);
            using (var unknown = Indexed(IndexedVersion, true, extra: "Assets/other.png"))
                Reject(() => ExplorerPackage.Validate(unknown, version), "An unknown indexed package member is rejected");
            using (var unknown = Indexed(IndexedVersion, true, extra: "helper.dll"))
                Reject(() => ExplorerPackage.Validate(unknown, version), "An unexpected executable payload is rejected");
            using (var noPri = Indexed(IndexedVersion, true, omit: "resources.pri"))
                Reject(() => ExplorerPackage.Validate(noPri, version), "An indexed package without resources.pri is rejected");
            using (var noIcon = Indexed(IndexedVersion, true, omit: "Assets/SyncShark-64.targetsize-48_altform-unplated.png"))
                Reject(() => ExplorerPackage.Validate(noIcon, version), "An indexed package missing one required target-size icon is rejected");
            using (var noBase = Indexed(IndexedVersion, true, omit: "Assets/SyncShark-64.png"))
                Reject(() => ExplorerPackage.Validate(noBase, version), "An indexed package missing its base artwork is rejected");
            using (var duplicate = Indexed(IndexedVersion, true, extra: "resources.pri"))
                Reject(() => ExplorerPackage.Validate(duplicate, version), "A duplicated indexed member is rejected");
            using (var wrongPublisher = Indexed(IndexedVersion, true, publisher: "CN=Other"))
                Reject(() => ExplorerPackage.Validate(wrongPublisher, version), "Another package publisher is rejected");
            using (var wrongVersion = Indexed(IndexedVersion, true, declaredVersion: "0.3.35.0"))
                Reject(() => ExplorerPackage.Validate(wrongVersion, version), "A package for another version is rejected");
            using (var unsigned = Indexed(IndexedVersion, true, signature: false))
                Reject(() => ExplorerPackage.Validate(unsigned, version), "An unsigned indexed package is rejected");
            using (var noCommand = Indexed(IndexedVersion, true, explorer: false))
                Reject(() => ExplorerPackage.Validate(noCommand, version), "An indexed package without the protected Explorer payload is rejected");
            using (var malformed = MalformedManifest())
                Reject(() => ExplorerPackage.Validate(malformed, version), "A malformed package manifest is rejected");
            using (var oversized = Indexed(IndexedVersion, true, oversize: "AppxMetadata/CodeIntegrity.cat"))
                Reject(() => ExplorerPackage.Validate(oversized, version), "An oversized package member is rejected");
            var installed = InstalledIdentityPackage();
            if (installed is null)
            {
                Console.WriteLine("NOTE the signed installed identity package was not readable; the synthetic indexed inventory is the accepted member evidence");
                return;
            }
            using (installed)
            {
                ExplorerPackage.Validate(installed, version);
                Check(true, "The signed installed identity package validates against the corrected member allowlist");
            }
        }

        void ProviderPresentationChecks(string stateRootInput)
        {
            var stateRoot = Path.Combine(stateRootInput, "provider-presentation");
            Directory.CreateDirectory(stateRoot);
            using var instance = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "state", "instance.lock"));
            var authority = instance.StateAuthority;
            var personal = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "pp-personal", "pp-personal-root", "pp-personal-home",
                DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
                DisplayName: "Personal Holder", Email: "personal@example.test").Normalize();
            Pump(new DropboxConnectionStore(authority).SaveNewAsync(personal));
            const string permissionId = "pp-google-account";
            var google = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
                GoogleDriveConnectionBinding.CreateAccountKey(permissionId), permissionId,
                "google@example.test", "Google Holder", [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
            Pump(new GoogleDriveConnectionStore(authority).SaveAsync(google));
            var localRoot = Path.Combine(stateRoot, "local");
            Directory.CreateDirectory(localRoot);
            PilotDirectoryIdentity identity;
            using (var lease = PilotDirectoryLease.Open(localRoot)) identity = lease.Identity;
            var boundPair = new SyncPair("pp-google-pair", "Google Pair", localRoot,
                identity.VolumeSerialNumber, identity.FileId, "/Google/Paired", "pp-google-folder",
                ConnectionId: google.ConnectionId, Provider: SyncProviders.GoogleDrive);
            using var theme = new ThemeManager(new UiPreferencesStore(authority));
            theme.Initialize();
            var window = new MainWindow(authority, theme) { Width = 1240, Height = 790 };
            window.StartupRegistrationForTest = _ => throw new InvalidOperationException("This check must not modify Windows startup.");
            window.Show(); Drain(); window.UpdateLayout();
            Pump(window.AuthorityHost.LoadPairsAsync());
            Pump(window.AuthorityHost.Authority.RegisterPairAsync(boundPair.Id, boundPair.PolicyRevision));
            Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(boundPair, true));
            Pump(window.RefreshAfterCliCommandAsync());
            Drain(); window.UpdateLayout();
            try
            {
                var unknownAdapterRejected = false;
                try { window.AuthorityHost.CreateRemoteStoreForPair(boundPair with { Provider = "future-provider" }, personal); }
                catch (NotSupportedException) { unknownAdapterRejected = true; }
                Check(unknownAdapterRejected, "An unregistered provider cannot select the Dropbox transport even with a valid Dropbox account");
                Call(window, "NavigateTo", "Connections", true); Drain(); window.UpdateLayout();
                Call(window, "NavigateTo", "Pairs", true); Drain(); window.UpdateLayout();
                var pairList = (DataGrid)window.FindName("PairList");
                var headers = HeaderTexts(pairList);
                Check(!headers.Contains("Dropbox"), "The actual modernized pair grid does not present a Dropbox-only heading");
                Check(window.PairRows.Single().CloudName == "Google Drive", "The actual pair row names its stored provider");
                var dropboxMark = window.FindResource("DropboxIconTemplate") as DataTemplate;
                Check(dropboxMark is not null && GlyphPaths(dropboxMark).Count == 4,
                    "The Dropbox mark is the four official SVG glyph paths: no wordmark and no approximation");
                Check(dropboxMark is not null && GlyphPaths(dropboxMark).All(path =>
                        path.Fill is SolidColorBrush fill && fill.Color.ToString() == "#FF0061FF"),
                    "Every Dropbox glyph path carries the retained official brand blue");
                Check(dropboxMark is not null && dropboxMark.LoadContent() is Viewbox { Child: Canvas canvas } &&
                        Math.Abs(canvas.Width - 29.8472) < 0.0001 && Math.Abs(canvas.Height - 25.1367) < 0.0001,
                    "The Dropbox mark keeps the official glyph geometry inside its own viewbox");
                Check(window.FindResource("GoogleDriveIconTemplate") is DataTemplate googleMark &&
                        googleMark.LoadContent() is Viewbox { Child: Image image } && image.Width == 128 && image.Height == 128 &&
                        image.Stretch == Stretch.Uniform,
                    "The Google Drive mark retains the full 128 px official artwork and scales uniformly");
                foreach (var resource in new[] { "DropboxIconTemplate", "GoogleDriveIconTemplate" })
                foreach (var size in new[] { 15d, 20d, 32d })
                {
                    var mark = (FrameworkElement)((DataTemplate)window.FindResource(resource)).LoadContent();
                    mark.Measure(new Size(size, size));
                    mark.Arrange(new Rect(0, 0, size, size));
                    mark.UpdateLayout();
                    Check(mark.DesiredSize.Width <= size && mark.DesiredSize.Height <= size &&
                            mark.RenderSize.Width <= size && mark.RenderSize.Height <= size,
                        resource + " fits its " + size + " px consumer without fixed-size clipping");
                }
                var row = Visuals<DataGridRow>(pairList).FirstOrDefault();
                var cloudCell = row is null ? null : Visuals<DataGridCell>(row)
                    .FirstOrDefault(cell => Texts(cell).Contains("/Google/Paired"));
                var cloudButton = cloudCell is null ? null : Visuals<Button>(cloudCell)
                    .SingleOrDefault(button => Equals(button.Tag, "cloud"));
                var cloudMark = cloudButton is null ? null : Visuals<ContentControl>(cloudButton)
                    .SingleOrDefault(control => control.GetType() == typeof(ContentControl));
                Check(cloudMark is not null && ReferenceEquals(cloudMark.ContentTemplate, window.FindResource("GoogleDriveIconTemplate")),
                    "The cloud column row mark is the retained Google Drive artwork");

                SetField(window, "_workspaceAccountId", google.ConnectionId);
                ((Button)window.FindName("AddPairButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Drain(); window.UpdateLayout();
                Check(Field<bool>(window, "_editingNewPair"), "Add Pair opens the production new-pair editor");
                var pairAccountPicker = Field<ComboBox>(window, "_pairAccountPicker");
                Check(pairAccountPicker.SelectedValue as string == google.ConnectionId,
                    "Add Pair inherits the exact Google account selected on Home without manual reselection");
                window.UpdateLayout();
                var panel = window.FindName("DropboxRootPanel");
                var editorTexts = Texts(panel);
                Check(editorTexts.Contains("Google Drive") && editorTexts.Contains("Paired folder in Google Drive"),
                    "A Google Drive pair account renders Google Drive editor labels: " + string.Join(" | ", editorTexts));
                Check(!editorTexts.Any(text => text.Contains("Dropbox", StringComparison.Ordinal)),
                    "No Google Drive editor label, folder prompt or help string says Dropbox");
                Check(AutomationProperties.GetName((TextBox)window.FindName("DropboxRootBox")) == "Folder in Google Drive",
                    "The cloud folder accessibility name follows the selected account's provider");
                Check(AutomationProperties.GetName((TreeView)window.FindName("DropboxRootTree")) == "Choose folders in Google Drive" &&
                        AutomationProperties.GetName((TreeView)window.FindName("DropboxFolderSelectionTree")) == "Google Drive folders selected for two-way sync" &&
                        AutomationProperties.GetName((CheckBox)window.FindName("DropboxHierarchyRootSelectionBox")) == "Sync the complete Google Drive folder",
                    "The cloud folder trees and the whole-root selection carry Google Drive accessibility names");
                Check(AutomationProperties.GetName((Button)window.FindName("CreateOrSelectDropboxFolderButton")) == "Create or select a Google Drive folder",
                    "The cloud folder action carries the selected provider's accessibility name");
                Check(window.ProviderPresentation.Key == SyncProviders.GoogleDrive,
                    "The provider presentation is selected from the stored provider binding, not from display text");
                Check(MainWindow.DeletionDirectionDescription(SyncMode.UploadOnly, SyncProviders.GoogleDrive)
                        .Contains("to Google Drive", StringComparison.Ordinal) &&
                      MainWindow.DeletionDirectionDescription(SyncMode.DownloadOnly, SyncProviders.GoogleDrive)
                        .Contains("Google Drive deletions", StringComparison.Ordinal),
                    "Directional descriptions consume the shared provider name in both directions");
                Check(new ActivityRow("2026-09-18", "sample.txt", "uploaded", "",
                        ConnectionId: google.ConnectionId).HumanAction == "Uploaded to Google Drive" &&
                      new ActivityRow("2026-09-18", "sample.txt", "uploaded", "",
                        ConnectionId: personal.ConnectionId).HumanAction == "Uploaded to Dropbox",
                    "Activity action wording composes the shared name without replacing brand literals");
                Check(EditorMark(window) is { } googleEditorMark &&
                        ReferenceEquals(googleEditorMark.ContentTemplate, window.FindResource("GoogleDriveIconTemplate")),
                    "The editor cloud panel shows the Google Drive mark for a Google Drive account");
                var dropboxRootBox = (TextBox)window.FindName("DropboxRootBox");
                dropboxRootBox.Text = "/Stale/Google/Folder";
                SetField(window, "_selectedDropboxFolder", new DropboxFolderEntry("Stale", "/Stale/Google/Folder", "id:stale"));
                Call(window, "RefreshAccountPickers"); Drain(); window.UpdateLayout();
                Check(pairAccountPicker.SelectedValue as string == google.ConnectionId &&
                        window.ProviderPresentation.Key == SyncProviders.GoogleDrive,
                    "A new pair retains the exact Google Drive account and its presentation across a production refresh");
                SelectAccount(pairAccountPicker, personal.ConnectionId);
                window.UpdateLayout();
                Check(dropboxRootBox.Text.Length == 0 && Field<object?>(window, "_selectedDropboxFolder") is null,
                    "Switching the pair account clears the stale remote folder identity");
                var dropboxTexts = Texts(panel);
                Check(dropboxTexts.Contains("Dropbox") && dropboxTexts.Contains("Paired folder in Dropbox"),
                    "A Dropbox pair account renders Dropbox editor labels again");
                Check(EditorMark(window) is { } dropboxEditorMark &&
                        ReferenceEquals(dropboxEditorMark.ContentTemplate, window.FindResource("DropboxIconTemplate")),
                    "The editor cloud panel returns to the Dropbox mark for a Dropbox account");
                pairList.SelectedIndex = 0; Drain(); window.UpdateLayout();
                Check(pairList.SelectedItem is not null, "The existing account-bound pair remains selectable in the Pairs grid");
                var storedPicker = Field<ComboBox>(window, "_pairAccountPicker");
                Check(storedPicker.Visibility == Visibility.Collapsed && !storedPicker.IsEnabled,
                    "An existing account-bound pair does not offer a provider or account change in the editor");
                Check(window.ProviderPresentation.Key == SyncProviders.GoogleDrive,
                    "The existing Google Drive pair is still described as Google Drive after the editor account switch");
                var browser = new DropboxFolderBrowserWindow(new EmbeddedDropboxPagingFixture(),
                    "Google Holder (google@example.test)", SyncProviders.GoogleDrive);
                browser.Show(); Drain(); browser.UpdateLayout();
                Check(browser.Title == "Browse Google Drive folders" &&
                        browser.Roots.Single().Name == "Google Drive" &&
                        !Texts(browser).Any(text => text.Contains("Dropbox", StringComparison.Ordinal)),
                    "The shared full folder browser renders the exact selected provider throughout");
                browser.Close();
                Call(window, "RefreshAccountPickers"); Drain();
                var retained = Pump(window.AuthorityHost.LoadPairsAsync()).Pairs.Single(pair => pair.Id == boundPair.Id);
                Check(retained.Provider == SyncProviders.GoogleDrive && retained.ConnectionId == google.ConnectionId,
                    "The stored pair keeps its exact provider binding and connection after the editor refreshes");
            }
            finally { window.Close(); }
        }

    }

    private static MemoryStream Indexed(string version, bool indexed, string? extra = null, string? omit = null,
        string? publisher = null, string? declaredVersion = null, bool signature = true, bool explorer = true, string? oversize = null)
    {
        var members = new List<string> { "AppxManifest.xml", "AppxBlockMap.xml", "[Content_Types].xml" };
        if (signature) members.Add("AppxSignature.p7x");
        members.Add("AppxMetadata/CodeIntegrity.cat");
        members.Add("Assets/SyncShark-64.png");
        if (indexed)
        {
            members.Add("resources.pri");
            foreach (var size in ShellSizes)
                foreach (var form in ShellForms)
                    members.Add("Assets/SyncShark-64.targetsize-" + size + form + ".png");
        }
        if (explorer) members.Add("SyncShark.Explorer.dll");
        if (extra is not null) members.Add(extra);
        if (omit is not null) members.RemoveAll(name => name == omit);
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var member in members)
            {
                var bytes = member.EndsWith("AppxManifest.xml", StringComparison.Ordinal)
                    ? Encoding.UTF8.GetBytes(Manifest(declaredVersion ?? version, publisher ?? ExplorerPackage.Publisher))
                    : member == oversize ? new byte[1024 * 1024 + 1]
                    : member.Contains("targetsize-", StringComparison.Ordinal) ? "PNGshell"u8.ToArray()
                    : "MZfixture"u8.ToArray();
                using var entry = zip.CreateEntry(member).Open();
                entry.Write(bytes);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream MalformedManifest()
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry("AppxManifest.xml").Open()) entry.Write("<Package><Identity"u8);
            using (var entry = zip.CreateEntry("AppxSignature.p7x").Open()) entry.Write("PKCXfixture"u8);
            using (var entry = zip.CreateEntry("SyncShark.Explorer.dll").Open()) entry.Write("MZfixture"u8);
        }
        stream.Position = 0;
        return stream;
    }

    private static FileStream? InstalledIdentityPackage()
    {
        foreach (var version in new[] { "0.3.36", "0.3.35" })
        {
            var path = Path.Combine(@"C:\Program Files\Massdex\SyncShark\versions", version, "SyncShark.msix");
            if (File.Exists(path))
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        return null;
    }

    private static string Manifest(string version, string publisher) =>
        "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" xmlns:com=\"http://schemas.microsoft.com/appx/manifest/com/windows10\" xmlns:uap10=\"http://schemas.microsoft.com/appx/manifest/uap/windows10/10\"><Identity Name=\"" +
        ExplorerPackage.Name + "\" Publisher=\"" + publisher + "\" Version=\"" + version +
        "\" ProcessorArchitecture=\"x64\"/><Applications><Application Id=\"SyncShark\" Executable=\"SyncShark.exe\" uap10:RuntimeBehavior=\"win32App\"><Extensions><com:Extension Category=\"windows.comServer\" uap10:RuntimeBehavior=\"packagedClassicApp\" uap10:TrustLevel=\"mediumIL\"><com:ComServer><com:SurrogateServer><com:Class Id=\"B2251D9A-7803-4F1F-8FE8-CBDA284BD5E9\" Path=\"SyncShark.Explorer.dll\" ThreadingModel=\"STA\"/></com:SurrogateServer></com:ComServer></com:Extension></Extensions></Application></Applications></Package>";

    private static List<System.Windows.Shapes.Path> GlyphPaths(DataTemplate template) =>
        template.LoadContent() is Viewbox { Child: Canvas canvas }
            ? canvas.Children.OfType<System.Windows.Shapes.Path>().ToList()
            : [];

    private static ContentControl? EditorMark(MainWindow window) =>
        Visuals<ContentControl>((DependencyObject)window.FindName("DropboxRootPanel"))
            .FirstOrDefault(item => ReferenceEquals(item.Style, window.FindResource("CloudProviderMark")));

    private static List<string> HeaderTexts(DataGrid grid)
    {
        var texts = new List<string>();
        foreach (var header in Visuals<DataGridColumnHeader>(grid))
        {
            if (header.Content is TextBlock direct) texts.Add(direct.Text);
            else texts.AddRange(Texts(header));
        }
        return texts;
    }

    private static List<string> Texts(object? root) => root is DependencyObject element
        ? Visuals<TextBlock>(element).Select(item => item.Text).Where(text => !string.IsNullOrEmpty(text)).ToList()
        : [];

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Visuals<T>(child)) yield return descendant;
        }
    }

    private static void SelectAccount(ComboBox picker, string connectionId)
    {
        picker.SelectedItem = picker.Items.Cast<StorageAccountChoice>().Single(item => item.Id == connectionId);
        Drain();
        Pump(Task.Delay(20));
        Drain();
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Call(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Pump(Task task) { while (!task.IsCompleted) { Drain(); Thread.Sleep(1); } task.GetAwaiter().GetResult(); }

    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
}
