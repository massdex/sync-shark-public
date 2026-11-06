using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private sealed record SharingChoice(string Id, string Label);
    private static ComboBox SharingChoices(params (string Id, string Label)[] choices) => new()
    {
        ItemsSource = choices.Select(item => new SharingChoice(item.Id, item.Label)).ToArray(),
        DisplayMemberPath = nameof(SharingChoice.Label), SelectedValuePath = nameof(SharingChoice.Id), SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private Window SharingDialog(string title, out StackPanel fields, out TextBlock feedback, out WrapPanel actions)
    {
        var body = new Grid { Margin = new Thickness(24) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        fields = new StackPanel();
        var scroll = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); body.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        feedback = Text("", 12, brush: "MutedInk"); feedback.Margin = new Thickness(0, 0, 0, 10); footer.Children.Add(feedback);
        actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        Grid.SetRow(footer, 2); body.Children.Add(footer);
        var window = new Window { Owner = this, Title = title, Width = 640, Height = 690, MinWidth = 430, MinHeight = 400,
            MaxWidth = SystemParameters.WorkArea.Width, MaxHeight = SystemParameters.WorkArea.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, WindowStyle = WindowStyle.None, Icon = Icon, ShowInTaskbar = false };
        var close = IconButton("Close", "close", (_, _) => window.Close());
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); header.Children.Add(Text(title, 22, true)); body.Children.Add(header);
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = body };
        frame.SetResourceReference(Border.BorderBrushProperty, "Border"); window.Content = frame;
        System.Windows.Shell.WindowChrome.SetWindowChrome(window, new System.Windows.Shell.WindowChrome
        { CaptionHeight = 64, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0) });
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            if (!WorkspaceInput.CloseOpenPicker(window)) window.Close();
        };
        window.SetResourceReference(Window.BackgroundProperty, "Canvas"); window.SetResourceReference(Window.ForegroundProperty, "Ink");
        WorkspaceInput.Enable(window); return window;
    }

    private async Task OpenCreateAccountLinkAsync(string id, AccountSharingItem? initialItem = null)
    {
        try
        {
            var client = await _authorityHost.CreateAccountSharingClientAsync(id);
            var google = _authorityHost.LoadAccountBinding(id)?.Provider == SyncProviders.GoogleDrive;
            var provider = google ? "Google Drive" : "Dropbox";
            var window = SharingDialog("Create Shared Link", out var fields, out var feedback, out var actions);
            fields.Children.Add(Field("Account", Text(SharingAccountLabel(id), 13)));
            if (initialItem is not null && initialItem.ConnectionId != client.ConnectionId)
                throw new InvalidOperationException("The selected sharing item belongs to another account.");
            AccountSharingItem? selected = initialItem; var busy = false; var uncertain = false;
            var location = Text(initialItem?.Name ?? "Choose a file or folder from this account.", 13);
            fields.Children.Add(Field(provider + " Location", location));
            var choose = AsyncButton("Choose File or Folder", async (_, _) =>
            {
                selected = await BrowseAccountItemAsync(window, client, false);
                if (selected is not null) location.Text = selected.Path;
            }); fields.Children.Add(choose);
            var audience = google ? SharingChoices(("public", "Anyone With the Link"), ("domain", "People in an Organisation")) :
                SharingChoices(("public", "Anyone With the Link"), ("team", "Team Only"), ("no_one", "People Who Already Have Access"));
            fields.Children.Add(Field("Who Can Open the Link", audience));
            var domain = new TextBox { MaxLength = 253, Visibility = Visibility.Collapsed, ToolTip = "The exact organisation domain, for example example.com" };
            System.Windows.Automation.AutomationProperties.SetName(domain, "Organisation domain");
            audience.SelectionChanged += (_, _) => domain.Visibility = google && audience.SelectedValue as string == "domain" ? Visibility.Visible : Visibility.Collapsed;
            fields.Children.Add(domain);
            fields.Children.Add(Text("Creates a view-only link. Account and folder policies may limit access. This does not move or change the item.", 12, brush: "MutedInk"));
            Button create = null!;
            create = AsyncButton("Create Link", async (_, _) =>
            {
                if (busy || uncertain) return;
                if (selected is null) { feedback.Text = "Choose a file or folder first."; return; }
                var item = selected; var access = audience.SelectedValue as string ?? "public";
                if (access == "domain") access = "domain:" + domain.Text.Trim();
                if (!AppDialogWindow.Confirm(window, "Create Shared Link", "Create a view-only link?",
                    SharingAccountLabel(id) + "\n" + item.Path + "\nAccess: " + AudienceLabel(access), "Create Link")) return;
                busy = true; fields.IsEnabled = actions.IsEnabled = false;
                try
                {
                    var link = await client.CreateLinkAsync(item, access);
                    feedback.Text = "Link created. Copy it or open it below.";
                    actions.Children.Clear(); actions.Children.Add(Button("Copy Shared Link", (_, _) => SetClipboardText(link.Url)));
                    actions.Children.Add(Button("Open Shared Link", (_, _) => OpenAccountSharingUrl(link.Url), true));
                    actions.Children.Add(Button("Close", (_, _) => window.Close(), true));
                    await LoadAccountSharingAsync(true);
                }
                catch (Exception error) { uncertain = IsSharingUncertain(error); feedback.Text = SharingFailure(error); }
                finally { busy = false; actions.IsEnabled = true; fields.IsEnabled = !uncertain; create.IsEnabled = !uncertain; }
            });
            ActionAvailability.Bind(create, () => busy ? "Waiting for the provider to confirm the new link."
                : "The provider did not confirm the last result. Refresh the sharing list before trying again.");
            actions.Children.Add(create); actions.Children.Add(Button("Cancel", (_, _) => window.Close(), true));
            window.Closing += (_, e) => { if (busy) { e.Cancel = true; feedback.Text = "Waiting for " + provider + " to confirm the change…"; } };
            window.ShowDialog();
        }
        catch (Exception error) { ShowStatus(SharingFailure(error)); }
    }

    private async Task OpenAccountLinkEditorAsync(AccountSharedLink snapshot)
    {
        if (!TryCloseSharingEditor()) { RestoreSharingEditorSelection(); return; }
        var epoch = ++_sharingEditorEpoch;
        try
        {
            var client = await _authorityHost.CreateAccountSharingClientAsync(snapshot.ConnectionId);
            var current = await client.RefreshLinkAsync(snapshot);
            if (epoch != _sharingEditorEpoch || _sharingPage?.Visibility != Visibility.Visible) return;
            var window = SharingEditor("Manage Shared Link", new(current.ConnectionId, current.NamespaceId, current.Url, current.PermissionId, current.RevisionFingerprint), out var fields, out var feedback, out var actions);
            fields.Children.Add(Field("Name", Text(current.Name, 16, true)));
            fields.Children.Add(Field("Account", Text(SharingAccountLabel(current.ConnectionId), 13)));
            fields.Children.Add(Field(SyncProviders.DisplayName(current.Provider) + " Location", Text(current.Path ?? current.Name, 13)));
            fields.Children.Add(Field("Current Access", Text(AudienceLabel(current.Audience) + "\n" + SharingRoleLabel(current.Access), 13)));
            var role = SharingChoices(new[] { ("keep", SharingRoleLabel(current.Access)) }.Concat(
                (current.AllowedRoles ?? []).Where(value => value != current.Access).Select(value => (value, SharingRoleLabel(value)))).ToArray());
            if (current.Provider == SyncProviders.GoogleDrive) fields.Children.Add(Field("Link permission", role));
            if (current.ManagementNote is { Length: > 0 } note) fields.Children.Add(Text(note, 12, brush: "MutedInk"));
            var audience = SharingChoices(new[] { ("keep", AudienceLabel(current.Audience)) }.Concat(current.AllowedAudiences
                .Where(value => value is "public" or "team" or "no_one").Select(value => (value, AudienceLabel(value)))).ToArray());
            if (current.Provider != SyncProviders.GoogleDrive) fields.Children.Add(Field("Who Can Open the Link", audience));
            var expiryOptions = new List<(string, string)> { ("keep", current.Expires is { } date ? date.ToLocalTime().ToString("d MMM yyyy, HH:mm") : "No Expiry") };
            if (current.CanSetExpiry) expiryOptions.Add(("set", "Set Expiry")); if (current.CanRemoveExpiry) expiryOptions.Add(("remove", "Remove Expiry"));
            var expiry = SharingChoices(expiryOptions.ToArray());
            if (current.Provider != SyncProviders.GoogleDrive) fields.Children.Add(Field("Expiry", expiry));
            var expiryDate = new DatePicker { SelectedDate = DateTime.Today.AddDays(7), Visibility = Visibility.Collapsed };
            System.Windows.Automation.AutomationProperties.SetName(expiryDate, "Link Expiry Date");
            expiry.SelectionChanged += (_, _) => expiryDate.Visibility = expiry.SelectedValue as string == "set" ? Visibility.Visible : Visibility.Collapsed;
            fields.Children.Add(expiryDate);
            if (current.CanSetExpiry) fields.Children.Add(Text("Expiry dates end at 23:59 in this computer's local time.", 12, brush: "MutedInk", margin: new Thickness(0, 5, 0, 16)));
            var passwordOptions = new List<(string, string)> { ("keep", current.PasswordRequired ? "Password Protected" : "No Password") };
            if (current.CanSetPassword) passwordOptions.Add(("set", "Set a New Password")); if (current.CanRemovePassword) passwordOptions.Add(("remove", "Remove Password"));
            var passwordMode = SharingChoices(passwordOptions.ToArray());
            if (current.Provider != SyncProviders.GoogleDrive) fields.Children.Add(Field("Password", passwordMode));
            var password = new PasswordBox { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 16) };
            System.Windows.Automation.AutomationProperties.SetName(password, "New Link Password");
            passwordMode.SelectionChanged += (_, _) => password.Visibility = passwordMode.SelectedValue as string == "set" ? Visibility.Visible : Visibility.Collapsed;
            fields.Children.Add(password);
            var downloadOptions = new List<(string, string)> { ("keep", current.AllowDownload ? "Downloads Allowed" : "Downloads Disabled") };
            if (current.CanAllowDownload) downloadOptions.Add(("allow", "Allow Downloads")); if (current.CanDisallowDownload) downloadOptions.Add(("block", "Disable Downloads"));
            var download = SharingChoices(downloadOptions.ToArray());
            if (current.DownloadSettingKnown) fields.Children.Add(Field("Downloads", download));
            fields.Children.Add(Text("Only supported settings for this exact permission are offered. Other permissions and existing membership stay unchanged.", 12, brush: "MutedInk"));
            var busy = false; var saved = false; var uncertain = false;
            bool Dirty() => current.CanEdit && new[] { audience, expiry, passwordMode, download, role }.Any(control => control.SelectedIndex > 0);
            Button save = null!;
            save = AsyncButton("Save Changes", async (_, _) =>
            {
                if (busy || uncertain || !Dirty()) return;
                busy = true; fields.IsEnabled = actions.IsEnabled = false;
                try
                {
                    var expiryMode = expiry.SelectedValue as string; var passMode = passwordMode.SelectedValue as string;
                    var change = new SharedLinkChange
                    {
                        Audience = audience.SelectedValue as string is { } value && value != "keep" ? value : null,
                        Role = role.SelectedValue as string is { } selectedRole && selectedRole != "keep" ? selectedRole : null,
                        ChangeExpiry = expiryMode != "keep", Expires = expiryMode == "set" ? EndOfLocalDate(expiryDate.SelectedDate) : null,
                        RequirePassword = passMode == "keep" ? null : passMode == "set", Password = passMode == "set" ? password.Password : null,
                        AllowDownload = download.SelectedValue as string == "keep" ? null : download.SelectedValue as string == "allow"
                    };
                    await client.ChangeLinkAsync(current, change); password.Clear(); saved = true;
                    feedback.Text = "The provider confirmed the saved link settings."; busy = false; window.Close(); await LoadAccountSharingAsync(true);
                }
                catch (Exception error) { uncertain = IsSharingUncertain(error); feedback.Text = SharingFailure(error); }
                finally { busy = false; actions.IsEnabled = true; fields.IsEnabled = !uncertain; save.IsEnabled = !uncertain && Dirty(); }
            }); save.IsEnabled = false;
            ActionAvailability.Bind(save, () => busy ? "Waiting for the provider to confirm the change."
                : uncertain ? "The last result is uncertain. Refresh this link before changing it again."
                : !current.CanEdit ? current.ManagementNote ?? "The provider does not allow this account to edit this link."
                : "Change a supported link setting before saving.");
            var discard = Button("Close", (_, _) => window.Close(), true);
            foreach (var control in new[] { audience, expiry, passwordMode, download, role }) control.SelectionChanged += (_, _) =>
            {
                save.IsEnabled = !uncertain && Dirty();
                CommandIcons.SetUnsavedAction(discard, Dirty() ? "Discard Changes" : "Close",
                    Dirty() ? UnsavedActionIntent.Discard : UnsavedActionIntent.Neutral);
            };
            actions.Children.Add(Button("Copy Link", (_, _) => SetClipboardText(current.Url), true));
            if (current.CanRevoke) actions.Children.Add(AsyncButton("Revoke Link", async (_, _) =>
            {
                if (busy || uncertain || !AppDialogWindow.Confirm(window.OwnerWindow, "Revoke Shared Link", "Stop this link from granting access?",
                    SharingAccountLabel(current.ConnectionId) + "\n" + current.Name + "\nThe item, other links, and existing folder membership stay unchanged.", "Revoke Link", destructive: true)) return;
                busy = true; fields.IsEnabled = actions.IsEnabled = false;
                try { await client.RevokeLinkAsync(current); saved = true; busy = false; window.Close(); await LoadAccountSharingAsync(true); }
                catch (Exception error) { uncertain = IsSharingUncertain(error); feedback.Text = SharingFailure(error); }
                finally { busy = false; actions.IsEnabled = true; fields.IsEnabled = !uncertain; save.IsEnabled = !uncertain && Dirty(); }
            }, true));
            actions.Children.Add(save); actions.Children.Add(discard);
            window.Closing += (_, e) =>
            {
                if (busy) { e.Cancel = true; feedback.Text = "Waiting for the provider to confirm the change…"; }
                else if (!saved && !uncertain && Dirty() && !AppDialogWindow.Confirm(window.OwnerWindow, "Unsaved Changes", "Discard the link changes?", "Only your unsaved edits will be discarded. The existing link stays unchanged.", "Discard Changes", "Keep editing", destructive: true)) e.Cancel = true;
            };
            window.Closed += (_, _) => password.Clear(); window.Present();
        }
        catch (Exception error) { ShowStatus(SharingFailure(error)); }
    }

    private async void OpenFileRequestEditor(string id, AccountFileRequest? snapshot)
    {
        if (!TryCloseSharingEditor()) { RestoreSharingEditorSelection(); return; }
        var epoch = ++_sharingEditorEpoch;
        try
        {
            var client = await _authorityHost.CreateAccountSharingClientAsync(id);
            var current = snapshot is null ? null : await client.GetRequestAsync(snapshot.NamespaceId, snapshot.Id);
            if (epoch != _sharingEditorEpoch || _sharingPage?.Visibility != Visibility.Visible) return;
            var ns = current?.NamespaceId ?? client.Namespaces[0];
            var window = SharingEditor(current is null ? "Create File Request" : "Manage File Request", current is null ? null : new(current.ConnectionId, current.NamespaceId, current.Url), out var fields, out var feedback, out var actions);
            fields.Children.Add(Field("Account", Text(SharingAccountLabel(id), 13)));
            var title = new TextBox { Text = current?.Title ?? "", MaxLength = 255 }; fields.Children.Add(Field("Title", title));
            var description = new TextBox { Text = current?.Description ?? "", AcceptsReturn = true, Height = 78, TextWrapping = TextWrapping.Wrap, MaxLength = 10000 };
            fields.Children.Add(Field("Description", description));
            var destination = new TextBox { Text = current?.Destination ?? "", IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
            fields.Children.Add(Field("Upload Destination", destination));
            var choose = AsyncButton("Choose Folder", async (_, _) =>
            {
                var folder = await BrowseAccountItemAsync(window.OwnerWindow, client, true, current?.NamespaceId);
                if (folder is not null) { destination.Text = folder.Path; ns = folder.NamespaceId; }
            }); fields.Children.Add(choose);
            var open = SharingChoices(("open", "Open to Uploads"), ("closed", "Closed")); open.SelectedValue = current?.IsOpen == false ? "closed" : "open";
            fields.Children.Add(Field("Status", open));
            var deadline = SharingChoices(("keep", current?.Deadline is { } date ? date.ToLocalTime().ToString("g") : "No Deadline"), ("set", "Set Deadline"), ("remove", "Remove Deadline"));
            fields.Children.Add(Field("Deadline", deadline));
            var deadlineDate = new DatePicker { SelectedDate = DateTime.Today.AddDays(7), Visibility = Visibility.Collapsed };
            System.Windows.Automation.AutomationProperties.SetName(deadlineDate, "File Request Deadline Date");
            deadline.SelectionChanged += (_, _) => deadlineDate.Visibility = deadline.SelectedValue as string == "set" ? Visibility.Visible : Visibility.Collapsed; fields.Children.Add(deadlineDate);
            var late = SharingChoices(("none", "Do Not Accept Late Uploads"), ("one_day", "Allow 1 Extra Day"), ("two_days", "Allow 2 Extra Days"),
                ("seven_days", "Allow 7 Extra Days"), ("thirty_days", "Allow 30 Extra Days"), ("always", "Always Allow Late Uploads"));
            var lateField = Field("After the Deadline", late); lateField.Visibility = Visibility.Collapsed; fields.Children.Add(lateField);
            deadline.SelectionChanged += (_, _) => lateField.Visibility = deadline.SelectedValue as string == "set" ? Visibility.Visible : Visibility.Collapsed;
            fields.Children.Add(Text("Deadlines require a supported Dropbox plan and end at 23:59 local time. Closing a request stops new uploads; previously received files stay in the destination.", 12, brush: "MutedInk"));
            if (current is not null)
            {
                fields.Children.Add(Field("Uploads Received", Text(current.FileCount.ToString("N0"), 13)));
                fields.Children.Add(Field("Created", Text(current.Created.ToLocalTime().ToString("d MMM yyyy, HH:mm"), 13)));
            }
            var busy = false; var saved = false; var uncertain = false;
            bool Dirty() => title.Text != (current?.Title ?? "") || description.Text != (current?.Description ?? "") ||
                destination.Text != (current?.Destination ?? "") || (open.SelectedValue as string == "open") != (current?.IsOpen ?? true) || deadline.SelectedIndex != 0;
            Button save = null!;
            save = AsyncButton(current is null ? "Create Request" : "Save Changes", async (_, _) =>
            {
                if (busy || uncertain) return;
                if (current is null && !AppDialogWindow.Confirm(window.OwnerWindow, "Create File Request", "Create an upload link for this account?",
                    SharingAccountLabel(id) + "\nDestination: " + destination.Text + "\nPeople with this link can submit files without seeing the folder's other contents.", "Create Request")) return;
                busy = true; fields.IsEnabled = actions.IsEnabled = false;
                try
                {
                    // A close/reopen-only operation works even if the old destination no longer exists.
                    var onlyState = current is not null && title.Text == current.Title && description.Text == current.Description &&
                        destination.Text == (current.Destination ?? "") && deadline.SelectedIndex == 0;
                    var change = new FileRequestChange(title.Text, destination.Text, description.Text, open.SelectedValue as string == "open",
                        deadline.SelectedIndex > 0, deadline.SelectedValue as string == "set" ? EndOfLocalDate(deadlineDate.SelectedDate) : null,
                        late.SelectedValue as string == "none" ? null : late.SelectedValue as string);
                    var result = onlyState ? await client.SetRequestOpenAsync(current!, change.IsOpen) : await client.SaveRequestAsync(ns, change, current);
                    saved = true; feedback.Text = "Dropbox confirmed the file request.";
                    actions.Children.Clear(); actions.Children.Add(Button("Copy Request Link", (_, _) => SetClipboardText(result.Url)));
                    actions.Children.Add(Button("Open Request", (_, _) => OpenAccountSharingUrl(result.Url), true));
                    actions.Children.Add(Button("Close", (_, _) => window.Close(), true)); fields.IsEnabled = false;
                    await LoadAccountSharingAsync(true);
                }
                catch (Exception error) { uncertain = error is DropboxEffectUncertainException; feedback.Text = SharingFailure(error); }
                finally { busy = false; actions.IsEnabled = true; fields.IsEnabled = !saved && !uncertain; save.IsEnabled = !uncertain; }
            }); save.IsEnabled = current is null;
            ActionAvailability.Bind(save, () => busy ? "Waiting for Dropbox to confirm the file request."
                : uncertain ? "The last result is uncertain. Refresh the request before changing it again."
                : "There are no unsaved changes to this file request.");
            var discard = Button("Close", (_, _) => window.Close(), true);
            void Update()
            {
                save.IsEnabled = !busy && !uncertain && (current is null || Dirty());
                CommandIcons.SetUnsavedAction(discard, Dirty() ? "Discard Changes" : "Close",
                    Dirty() ? UnsavedActionIntent.Discard : UnsavedActionIntent.Neutral);
            }
            foreach (var input in new[] { title, description, destination }) input.TextChanged += (_, _) => Update();
            open.SelectionChanged += (_, _) => Update(); deadline.SelectionChanged += (_, _) => Update();
            if (current is not null) actions.Children.Add(Button("Copy Request Link", (_, _) => SetClipboardText(current.Url), true));
            actions.Children.Add(save); actions.Children.Add(discard);
            window.Closing += (_, e) =>
            {
                if (busy) { e.Cancel = true; feedback.Text = "Waiting for Dropbox to confirm the change…"; }
                else if (!saved && !uncertain && Dirty() && !AppDialogWindow.Confirm(window.OwnerWindow, "Unsaved Changes", "Discard the file request changes?", "Only your unsaved edits will be discarded. The existing request stays unchanged.", "Discard Changes", "Keep editing", destructive: true)) e.Cancel = true;
            };
            window.Present();
        }
        catch (Exception error) { ShowStatus(SharingFailure(error)); }
    }

    private async Task<AccountSharingItem?> BrowseAccountItemAsync(Window owner, IAccountSharingClient client, bool foldersOnly, string? fixedNamespace = null)
    {
        var window = SharingDialog(foldersOnly ? "Choose Folder" : "Choose File or Folder", out var fields, out var feedback, out var actions); window.Owner = owner;
        var completed = new TaskCompletionSource<AccountSharingItem?>();
        using var lifetime = new CancellationTokenSource();
        var namespaces = SharingChoices(client.Namespaces.Select((id, index) => (id, index == 0 ? "Personal Files" : "Team Space")).ToArray());
        if (fixedNamespace is not null) { namespaces.SelectedValue = fixedNamespace; namespaces.IsEnabled = false; }
        fields.Children.Add(Field("Location", namespaces));
        var path = new TextBox { Text = "/", IsReadOnly = true, TextWrapping = TextWrapping.Wrap }; fields.Children.Add(path);
        var parents = new Stack<string>();
        var list = BuildAccountBrowserList(); fields.Children.Add(list);
        var entries = new List<AccountSharingItem>(); AccountSharingCursor? cursor = null; var loading = false; var generation = 0;
            Button more = null!;
            more = AsyncButton("Load More", async (_, _) => await LoadAsync(false), true); fields.Children.Add(more);
        async Task LoadAsync(bool reset)
        {
            var ownGeneration = ++generation; var ns = namespaces.SelectedValue as string ?? client.Namespaces[0];
            if (reset) { entries.Clear(); cursor = null; list.ItemsSource = null; }
            loading = true; list.IsEnabled = actions.IsEnabled = more.IsEnabled = false; feedback.Text = "Loading folders and files…";
            try
            {
                var result = await client.ListItemsAsync(ns, path.Text, cursor, lifetime.Token);
                if (ownGeneration != generation) return;
                cursor = result.Next; entries.AddRange(result.Items.Where(item => !foldersOnly || item.Kind == "folder"));
                list.ItemsSource = entries.OrderBy(item => item.Kind != "folder").ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                feedback.Text = "Double-click a folder to open it. Select an item, then choose it."; more.Visibility = cursor is null ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception error) { if (ownGeneration == generation) feedback.Text = SharingFailure(error); }
            finally { if (ownGeneration == generation) { loading = false; list.IsEnabled = actions.IsEnabled = more.IsEnabled = true; } }
        }
        list.MouseDoubleClick += async (_, _) =>
        { if (!loading && list.SelectedItem is AccountSharingItem { Kind: "folder" } folder) { parents.Push(path.Text); path.Text = folder.Path; await LoadAsync(true); } };
        namespaces.SelectionChanged += async (_, _) => { parents.Clear(); path.Text = "/"; await LoadAsync(true); };
        actions.Children.Add(AsyncButton("Up One Folder", async (_, _) =>
        { path.Text = parents.Count > 0 ? parents.Pop() : "/"; await LoadAsync(true); }, true));
        actions.Children.Add(Button("Choose", (_, _) =>
        {
            if (list.SelectedItem is not AccountSharingItem item) { feedback.Text = "Select a file or folder first."; return; }
            completed.TrySetResult(item); window.Close();
        })); actions.Children.Add(Button("Cancel", (_, _) => window.Close(), true));
        window.Closed += (_, _) => { generation++; lifetime.Cancel(); completed.TrySetResult(null); };
        window.Loaded += async (_, _) => await LoadAsync(true);
        window.ShowDialog(); return await completed.Task;
    }
    internal static ListBox BuildAccountBrowserList()
    {
        var list = new ListBox { MinHeight = 280, MaxHeight = 350, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 12, 0, 12), HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
        list.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ControlSurface");
        list.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Ink");
        list.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "Divider");
        list.ItemTemplate = RowTemplate("""
          <DockPanel Margin="4,6">
            <local:CommandGlyph IconName="{Binding Kind}" Width="20" Height="20" Margin="0,0,10,0"/>
            <TextBlock Text="{Binding Name}" FontSize="13" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" ToolTip="{Binding Path}"/>
          </DockPanel>
          """);
        list.ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse("""
          <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBoxItem">
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Foreground" Value="{DynamicResource Ink}"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Padding" Value="8,4"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
              <Border Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border>
            </ControlTemplate></Setter.Value></Setter>
            <Style.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{DynamicResource HoverSurface}"/></Trigger>
              <Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="{DynamicResource SelectedSurface}"/><Setter Property="Foreground" Value="{DynamicResource SelectedInk}"/></Trigger>
            </Style.Triggers>
          </Style>
          """);
        System.Windows.Automation.AutomationProperties.SetName(list, "Account Files and Folders");
        return list;
    }

    private static DateTimeOffset EndOfLocalDate(DateTime? date) => date is { } value
        ? new DateTimeOffset(DateTime.SpecifyKind(value.Date.AddHours(23).AddMinutes(59), DateTimeKind.Local))
        : throw new InvalidOperationException("Choose a date first.");
    private static string SharingRoleLabel(string role) => role switch { "reader" or "viewer" => "Can view", "commenter" => "Can comment", "writer" or "editor" => "Can edit", _ => role };
    private static bool IsSharingUncertain(Exception error) => error is DropboxEffectUncertainException or GoogleDriveEffectUncertainException;
    private static string SharingFailure(Exception error) => IsSharingUncertain(error)
        ? "The provider has not confirmed the result. Refresh the list and check it before trying again; the change may already have been saved."
        : error.Message;
}
