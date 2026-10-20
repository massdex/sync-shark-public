using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using StackPanel = System.Windows.Controls.StackPanel;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private void OpenSharing(ActivityRow row)
    {
        if (row.PairId is null || row.Path.StartsWith('<') || !_pairSet.Pairs.Any(pair => pair.Id == row.PairId))
        { ShowStatus("Choose a current file or folder in a connected pair."); return; }
        OpenSharing(row.PairId, row.Path);
    }

    private void OpenSharing(string pairId, string relativePath)
    {
        if (_pairSet.Pairs.SingleOrDefault(pair => pair.Id == pairId)?.Provider == SyncProviders.GoogleDrive)
        { _ = OpenGooglePairSharingAsync(pairId, relativePath); return; }
        Button create = null!, refresh = null!;
        Window window = null!;
        DropboxSharingItem? current = null;
        var busy = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Text("Share & Manage Links", 23, true));
        panel.Children.Add(Text(relativePath, 14, margin: new Thickness(0, 12, 0, 5)));
        var pair = _pairSet.Pairs.Single(item => item.Id == pairId);
        panel.Children.Add(Text(LabelForPair(pair), 12, brush: "MutedInk"));
        var feedback = Text("Loading Current Sharing…", 13, margin: new Thickness(0, 16, 0, 12));
        panel.Children.Add(feedback);
        var linksPanel = new StackPanel(); panel.Children.Add(linksPanel);
        var createPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var audience = new ComboBox { ItemsSource = new[] { "Anyone With the Link", "Team Only" },
            SelectedIndex = 0, MinWidth = 220, Margin = new Thickness(0, 0, 12, 0),
            ToolTip = "Team-only access requires a supported Dropbox account. Dropbox may further restrict access." };
        System.Windows.Automation.AutomationProperties.SetName(audience, "New Link Access");
        createPanel.Children.Add(audience);
        create = AsyncButton("Create Link", async (_, _) => await CreateAsync());
        createPanel.Children.Add(create); panel.Children.Add(createPanel);
        panel.Children.Add(Text("Only direct links to this item are shown. Access inherited from a parent folder is managed in Dropbox.",
            12, brush: "MutedInk", margin: new Thickness(0, 16, 0, 16)));
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        refresh = AsyncButton("Refresh", async (_, _) => await RefreshAsync(), true); footer.Children.Add(refresh);
        ActionAvailability.Bind(create, () => busy ? "Waiting for Dropbox to finish the current sharing operation."
            : current is null ? "Refresh this item's current sharing state before creating a link."
            : "A direct shared link already exists. Copy or manage that link below.");
        ActionAvailability.Bind(refresh, () => "Waiting for Dropbox to finish the current sharing operation.");
        window = new Window { Owner = this, Title = "Share & Manage Links", Width = 650, Height = 570,
            MinWidth = 440, MinHeight = 350, MaxWidth = SystemParameters.WorkArea.Width,
            MaxHeight = SystemParameters.WorkArea.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        window.SetResourceReference(Window.BackgroundProperty, "Canvas");
        window.SetResourceReference(Window.ForegroundProperty, "Ink");
        footer.Children.Add(Button("Close", (_, _) => window.Close(), true)); panel.Children.Add(footer);
        WorkspaceInput.Enable(window);
        async Task<bool> ExecuteAsync(Func<Task> operation)
        {
            if (busy) return false;
            busy = true; create.IsEnabled = refresh.IsEnabled = linksPanel.IsEnabled = false;
            try { await operation(); return true; }
            catch (Exception error)
            { feedback.Text = error is DropboxEffectUncertainException
                ? "Dropbox has not confirmed the result. Refresh to check before trying again."
                : "Sharing could not be completed: " + error.Message; return false; }
            finally { busy = false; refresh.IsEnabled = linksPanel.IsEnabled = true; create.IsEnabled = current is { Links.Count: 0 }; }
        }
        async Task RefreshAsync() => await ExecuteAsync(async () =>
        {
            current = null; linksPanel.Children.Clear();
            current = await _authorityHost.GetSharingItemAsync(pairId, relativePath);
            feedback.Text = current.Links.Count == 0 ? "No Direct Shared Links" : $"{current.Links.Count} Direct Shared Link{(current.Links.Count == 1 ? "" : "s")}";
            foreach (var link in current.Links)
            {
                var item = current;
                var body = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
                body.Children.Add(Text("Access: " + AudienceLabel(link.Visibility), 14, true));
                body.Children.Add(Text("Permission: " + (link.Access == "editor" ? "Can Edit" : link.Access == "viewer" ? "Can View" : "As Reported by Dropbox"), 12, brush: "MutedInk"));
                if (link.Expires is { } expires) body.Children.Add(Text("Expires: " + expires.ToLocalTime().ToString("d MMM yyyy, HH:mm"), 12));
                var url = new TextBox { Text = link.Url, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 8), ToolTip = "This shared link may grant access. Only send it to intended recipients." };
                body.Children.Add(url);
                var actions = new WrapPanel(); body.Children.Add(actions);
                actions.Children.Add(Button("Copy Shared Link", (_, _) => SetClipboardText(link.Url), true));
                var change = Button("Change Access", (sender, _) =>
                {
                    var menu = WorkspaceMenu(); menu.PlacementTarget = sender as UIElement;
                    foreach (var choice in Enum.GetValues<DropboxLinkAudience>())
                    {
                        var selected = choice;
                        MenuAction(menu, selected == DropboxLinkAudience.AnyoneWithLink ? "Anyone With the Link" : "Team Only", async (_, _) =>
                        {
                            if (!AppDialogWindow.Confirm(window, "Change Link Access", "Change access to this item?",
                                relativePath + "\nNew Access: " + (selected == DropboxLinkAudience.AnyoneWithLink ? "Anyone With the Link" : "Team Only"), "Change Access")) return;
                            if (await ExecuteAsync(async () => { var updated = await _authorityHost.ChangeSharedLinkAudienceAsync(pairId, item, link.Url, selected);
                                feedback.Text = "Dropbox Confirmed Access: " + AudienceLabel(updated.Visibility); })) await RefreshAsync();
                        });
                    }
                    menu.IsOpen = true;
                }, true);
                actions.Children.Add(change);
                var revoke = AsyncButton("Revoke Link", async (_, _) =>
                {
                    if (!AppDialogWindow.Confirm(window, "Revoke Shared Link", "Stop this link from granting access?",
                        relativePath + "\nThe file stays in Dropbox. Other links and existing folder membership are unchanged.", "Revoke Link", destructive: true)) return;
                    if (await ExecuteAsync(async () => await _authorityHost.RevokeSharedLinkAsync(pairId, item, link.Url))) await RefreshAsync();
                }, true);
                revoke.IsEnabled = link.CanRevoke;
                ActionAvailability.Bind(revoke, () => busy ? "Waiting for Dropbox to finish the current sharing operation."
                    : "Dropbox has not granted this account permission to revoke this link.");
                actions.Children.Add(revoke);
                linksPanel.Children.Add(body);
            }
        });
        async Task CreateAsync()
        {
            if (current is not { Links.Count: 0 } item) return;
            var choice = audience.SelectedIndex == 1 ? DropboxLinkAudience.TeamOnly : DropboxLinkAudience.AnyoneWithLink;
            if (!AppDialogWindow.Confirm(window, "Create Shared Link", "Create a view-only link to this item?",
                relativePath + "\nAccess: " + (choice == DropboxLinkAudience.AnyoneWithLink ? "Anyone With the Link" : "Team Only") +
                "\nDropbox account and folder policies may restrict the final access.", "Create Link")) return;
            if (await ExecuteAsync(async () => await _authorityHost.CreateSharedLinkAsync(pairId, item, choice))) await RefreshAsync();
        }
        window.Loaded += async (_, _) => await RefreshAsync();
        window.Show();
    }

    private async Task OpenGooglePairSharingAsync(string pairId, string relativePath)
    {
        try
        {
            if (!TryCloseSharingEditor()) return;
            var pair = _pairSet.Pairs.Single(item => item.Id == pairId);
            ShowStatus("Checking the exact Google Drive item…");
            var resolved = await _authorityHost.ResolveGoogleDriveWebLocationAsync(pair.Id, pair.ConnectionId!,
                relativePath.Length == 0 ? "." : relativePath);
            var client = (GoogleDriveAccountSharingClient)await _authorityHost.CreateAccountSharingClientAsync(resolved.ConnectionId);
            var item = await client.GetItemByIdAsync(resolved.NamespaceId, resolved.FileId, resolved.ResourceKey);
            var links = await client.GetItemLinksAsync(item);
            var window = SharingDialog("Share & Manage Links", out var fields, out var feedback, out var actions);
            fields.Children.Add(Field("Item", Text(item.Name, 16, true)));
            fields.Children.Add(Field("Account", Text(SharingAccountLabel(item.ConnectionId), 13)));
            fields.Children.Add(Text("Link audiences for this item. Existing individual access and inherited permissions are not revoked by a link action.", 12, brush: "MutedInk"));
            foreach (var link in links)
            {
                var commands = new WrapPanel();
                commands.Children.Add(Button("Copy Shared Link", (_, _) => SetClipboardText(link.Url), true));
                commands.Children.Add(AsyncButton("Manage Link", async (_, _) =>
                {
                    window.Close(); NavigateTo("Sharing"); await OpenAccountLinkEditorAsync(link);
                }, true));
                fields.Children.Add(SharingListRow("link", AudienceLabel(link.Audience), "",
                    SharingRoleLabel(link.Access), commands, link.ManagementNote));
            }
            if (links.Count == 0) feedback.Text = "No link permissions exist for this item.";
            actions.Children.Add(AsyncButton("Create Shared Link", async (_, _) =>
            { window.Close(); await OpenCreateAccountLinkAsync(item.ConnectionId, item); }));
            actions.Children.Add(Button("Close", (_, _) => window.Close(), true));
            window.ShowDialog();
        }
        catch (Exception error) { ShowStatus(SharingFailure(error)); }
    }

    private static string AudienceLabel(string value) => value switch
    {
        "public" => "Anyone With the Link", "team_only" or "team" => "Team Only",
        "password" => "Password Required", "team_and_password" => "Team and Password Required",
        "shared_folder_only" or "members" => "Existing Folder Members", "no_one" => "Existing Access Only",
        _ when value.StartsWith("domain:", StringComparison.Ordinal) => "People in " + value[7..],
        _ => "Restricted by the provider"
    };
}
