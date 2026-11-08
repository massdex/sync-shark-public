using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using StackPanel = System.Windows.Controls.StackPanel;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

public sealed record StorageAccountChoice(string Id, string Provider, string Account, bool Supported,
    string? ExplicitProviderKey = null)
{
    // U1: the visible identity is the account alone. The provider is carried by the provider mark and
    // stays available to assistive technology, instead of repeating "Dropbox:" in every row.
    public string Label => Account;
    public string CompactLabel => Account;

    // The provider identity the shared account-picker template binds its mark to. A stored
    // connection's exact provider id stays authoritative; otherwise the registered display name
    // resolves it, and a missing or unknown provider claims nothing and falls back to the neutral
    // mark rather than guessing a provider from arbitrary text.
    public string ProviderKey => !string.IsNullOrWhiteSpace(ExplicitProviderKey)
        ? ExplicitProviderKey!
        : Provider switch
        {
            "Dropbox" => SyncProviders.Dropbox,
            "Google Drive" => SyncProviders.GoogleDrive,
            _ => "unknown"
        };

    // The accessible name keeps the provider that the visible label no longer repeats.
    public string AccessibleName => string.IsNullOrWhiteSpace(Provider) ||
        string.Equals(Provider, "All Providers", StringComparison.Ordinal)
        ? Account : Provider + " " + Account;
}

public partial class MainWindow
{
    private ComboBox? _accountPicker;
    private ComboBox? _pairAccountPicker;
    private string? _selectedConnectionId;
    private bool _updatingAccountPickers;
    private long _accountRefreshGeneration;
    private Button? _accountPauseButton;
    private Button? _addAccountButton;
    // UI20: the Connections account mark and its one clickable linked-pair count.
    private ContentControl? _connectionProviderIcon;
    private Button? _connectionPairsLink;
    private FrameworkElement? _sharedDrivesRow;
    private System.Windows.Controls.CheckBox? _sharedDrivesToggle;
    private bool _savingSharedDrives;
    private UIElement? _authorizationPanel;
    private TextBlock? _authorizationMessage;
    private Button? _authorizationCancelButton;
    // AUTH2: the Connections panel observes the shared host attempt. The window keeps no per-attempt
    // cancellation source of its own: it displays the exact attempt id and routes its cancel action
    // through the same shared exact-target host operation the accounts cancel command uses.
    private string? _observedAuthorizationAttemptId;
    private bool _observedAuthorizationStartedByThisWindow;
    private bool _authorizationCancellationRequested;
    private bool _uiAuthorizationStartInProgress;
    private Dictionary<string, string> _accountLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SyncAccountStatus> _knownAccountStatus = new(StringComparer.Ordinal);

    private void RememberAccountStatus(SyncAccountStatus status)
    {
        if (status.Binding is not { } binding) return;
        if (!status.LiveVerified && status.FailureType is null && status.HasProtectedCredential &&
            _knownAccountStatus.TryGetValue(binding.ConnectionId, out var known))
            status = status with { LiveVerified = known.LiveVerified, FailureType = known.FailureType, SpaceUsage = known.SpaceUsage };
        _knownAccountStatus[binding.ConnectionId] = status;
    }

    private string? SelectedPairConnectionId => !_editingNewPair && PairList.SelectedItem is PairRow row
        ? _pairSet.Pairs.SingleOrDefault(pair => pair.Id == row.Id)?.ConnectionId
        : _pairAccountPicker?.SelectedValue as string ?? _selectedConnectionId ?? _connectionStatus?.Binding?.ConnectionId;
    private SyncAccountBinding? PairAccountBinding =>
        PresentationAccountBinding(SelectedPairConnectionId) ??
        (DropboxConnectionStatusLoaderForTest is not null ? _connectionStatus?.Binding : null);

    private string PairAccountLabel => PairAccountBinding is { } binding
        ? binding.ProviderName + ": " + (string.IsNullOrWhiteSpace(binding.DisplayName) ? binding.Email : binding.DisplayName)
        : "Choose a connected account";
    private string LabelForPair(SyncPair pair)
    {
        if (pair.EndpointDocument is { Left: { } left, Right: { } right })
            return string.Join(" ↔ ", new[] { left, right }.Select(side => side.Kind == SyncEndpointKind.Local
                ? "This computer" : AccountIdLabel(side.ConnectionId)));
        return AccountIdLabel(pair.ConnectionId);
    }

    // ONE helper resolves an account id to its label for both explicit endpoint documents and legacy pairs:
    // the connected account label first, then the retained binding through the existing AccountLabel, else
    // the exact non-empty id so two unavailable accounts with the same display name stay distinguishable,
    // else Account Not Retained when no id is recorded at all.
    private string AccountIdLabel(string? connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return "Account Not Retained";
        if (_accountLabels.TryGetValue(connectionId, out var label)) return label;
        var binding = PresentationAccountBinding(connectionId);
        return binding is null
            ? "Account Unavailable (" + connectionId + ")"
            : AccountLabel(binding.ProviderName, binding.DisplayName, binding.Email);
    }
    private string LabelForPairId(string? pairId) => _pairSet.Pairs.FirstOrDefault(pair => pair.Id == pairId) is { } pair
        ? LabelForPair(pair) : "Account Not Retained";
    // SS-D12: the tray account list is the same display-only projection the Connections pickers use, so a
    // tray render reads the one snapshot its scope established instead of the provider stores per lookup.
    internal IReadOnlyList<StorageAccountChoice> TrayAccounts => PresentationAccounts
        .Select(item => new StorageAccountChoice(item.ConnectionId, SyncProviders.DisplayName(item.Provider),
            AccountLabel(SyncProviders.DisplayName(item.Provider), item.DisplayName, item.Email), true, item.Provider)).ToArray();
    internal bool TrayMatchesAccount(string? pairId, string? accountId) => accountId is null ||
        _pairSet.Pairs.Any(pair => pair.Id == pairId &&
            (pair.EndpointDocument is { Left: { } left, Right: { } right }
                ? left.ConnectionId == accountId || right.ConnectionId == accountId
                : pair.ConnectionId == accountId));
    internal string TrayLabelForPair(string pairId) => LabelForPairId(pairId);

    private UIElement BuildAccountPicker()
    {
        var row = new DockPanel();
        var add = Button("Add Account", AddStorageAccount_Click,
            explanation: "Choose a cloud provider and authorize an account in your browser. Existing accounts and folder pairs stay unchanged.");
        add.Margin = new Thickness(16, 0, 0, 0);
        _addAccountButton = add;
        // U1: provider mark plus the account identity, with no visible Account label and no repeated
        // provider prefix in the text; the accessible name keeps both identities.
        _accountPicker = new ComboBox { ItemTemplate = SharedPresentation.ProviderAccountTemplate(),
            SelectedValuePath = nameof(StorageAccountChoice.Id), MinWidth = 240, MaxWidth = 560,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, ToolTip = "Select the account whose connection details you want to view." };
        System.Windows.Automation.AutomationProperties.SetName(_accountPicker, "Storage Account");
        _accountPicker.SelectionChanged += async (_, _) =>
        {
            if (_closing || _updatingAccountPickers || _accountPicker.SelectedItem is not StorageAccountChoice choice) return;
            if (!choice.Supported) { ShowStatus("This provider is not connected in this version."); return; }
            _selectedConnectionId = choice.Id;
            await RefreshConnectionStateAsync(verifyLive: false);
            if (!SuppressLoadedStartupForTest) await RefreshConnectionStateAsync();
        };
        row.Children.Add(_accountPicker);
        _pairAccountPicker = new ComboBox { ItemTemplate = SharedPresentation.ProviderAccountTemplate(),
            SelectedValuePath = nameof(StorageAccountChoice.Id), ToolTip = "Choose the account for this new pair. Existing pairs keep their original account." };
        System.Windows.Automation.AutomationProperties.SetName(_pairAccountPicker, "Folder Pair Account");
        _pairAccountPicker.SelectionChanged += async (_, _) =>
        {
            if (_closing || _updatingAccountPickers || !_editingNewPair) return;
            // No selected path/identity from one account may survive a change to another.
            _selectedDropboxFolder = null; _selectedRecoveryFolder = null;
            DropboxRootBox.Clear(); RecoveryRootBox.Clear();
            _draftBrowseDropboxRoot = null; _draftSelectionRules = []; _draftPreview = null;
            var (generation, token) = StartPairDraft();
            // Presentation follows the selected identity immediately; drive enumeration
            // or a slow/failed provider request must not leave the old provider visible.
            PairConnectionSummary.Text = PairAccountLabel;
            ApplyPairEditorProviderPresentation();
            UpdateStorageOptionsForSelectedRoot(); UpdatePairSaveAvailability();
            await InitializeNewPairRootBrowserAsync(generation, token, _draftBrowseLocalRoot);
            if (!IsCurrentPairDraft(generation)) return;
            UpdateStorageOptionsForSelectedRoot(); UpdatePairSaveAvailability();
        };
        SyncTypePanel.Children.Add(_pairAccountPicker);
        RefreshAccountPickers();
        var panel = new StackPanel();
        var waiting = new DockPanel { Margin = new Thickness(0, 0, 0, 20), Visibility = Visibility.Collapsed };
        var cancel = Button("Cancel Connection", (_, _) => CancelPendingAuthorizationFromUi());
        _authorizationCancelButton = cancel;
        ActionAvailability.Bind(cancel, () => "Cancellation was requested. Waiting for the authorization operation to stop safely.");
        DockPanel.SetDock(cancel, Dock.Right); waiting.Children.Add(cancel);
        _authorizationMessage = new TextBlock { TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        waiting.Children.Add(_authorizationMessage);
        _authorizationPanel = waiting; panel.Children.Add(waiting);
        return panel;
    }

    private void RefreshAccountPickers()
    {
        if (_accountPicker is null || _pairAccountPicker is null) return;
        // SS-D12: one presentation scope for this whole picker pass, so the account list and every account
        // label or pause below read the one snapshot it established instead of the stores per account.
        using var presentation = OpenPresentationScope();
        _updatingAccountPickers = true;
        try
        {
            var choices = PresentationAccounts.Where(item => SyncProviders.IsKnown(item.Provider))
                .Select(item => new StorageAccountChoice(item.ConnectionId, SyncProviders.DisplayName(item.Provider),
                    AccountLabel(SyncProviders.DisplayName(item.Provider), item.DisplayName, item.Email), true, item.Provider)).ToArray();
            _accountLabels = choices.ToDictionary(item => item.Id, item => item.Label, StringComparer.Ordinal);
            RefreshWorkspaceAccountPickers();
            RefreshSharingAccountChoices();
            RefreshStorageChoices(true);
            // A new pair is seeded from the app-wide account scope the user is actually looking at,
            // so Home scoped to a Google Drive account opens Add Pair on that exact account rather
            // than the first connection in the list. An account already chosen for this draft is
            // kept, and an existing pair always keeps its own stored account.
            var chosenPairAccount = _pairAccountPicker.SelectedValue as string;
            var pairChoice = _editingNewPair ? chosenPairAccount ?? string.Empty : SelectedPairConnectionId;
            var scopeSeed = _editingNewPair && _workspaceAccountId is not null &&
                choices.Any(item => item.Id == _workspaceAccountId) ? _workspaceAccountId : null;
            _accountPicker.ItemsSource = choices;
            _selectedConnectionId = choices.Any(item => item.Id == _selectedConnectionId) ? _selectedConnectionId : choices.FirstOrDefault()?.Id;
            _accountPicker.SelectedValue = _selectedConnectionId;
            _pairAccountPicker.ItemsSource = choices;
            _pairAccountPicker.SelectedValue = choices.Any(item => item.Id == pairChoice) ? pairChoice : scopeSeed ?? _selectedConnectionId;
            _pairAccountPicker.IsEnabled = _editingNewPair && !_runActive;
            _pairAccountPicker.Visibility = _editingNewPair ? Visibility.Visible : Visibility.Collapsed;
            PairConnectionSummary.Visibility = _editingNewPair ? Visibility.Collapsed : Visibility.Visible;
            RefreshAccountPauseButton();
            ApplyPairEditorProviderPresentation();
            UpdateConnectionIdentity();
        }
        finally { _updatingAccountPickers = false; }
    }

    // One account-label form across the app: the exact provider email first, with the holder's display
    // name in brackets, instead of the bulky name-em dash-email combination. The provider identity is
    // carried by the provider mark, so the missing-email fallback stays compact and deterministic.
    internal static string AccountLabel(string providerName, string? displayName, string? email)
    {
        var mail = (email ?? string.Empty).Trim();
        var holder = (displayName ?? string.Empty).Trim();
        if (mail.Length > 0)
            return holder.Length > 0 && !string.Equals(mail, holder, StringComparison.OrdinalIgnoreCase)
                ? mail + " (" + holder + ")" : mail;
        if (holder.Length > 0) return holder;
        return (providerName ?? string.Empty).Trim() is { Length: > 0 } provider ? provider + " account" : "Account";
    }

    // The Connections account label names the exact account: the real provider mark from the same two
    // templates the Folder Pairs rows already use, plus the exact email with the holder in brackets.
    // Two accounts that share a display name stay distinct because the email differs, and the full
    // tooltip keeps the provider and the stable connection id even when the label is trimmed.
    private void UpdateConnectionIdentity()
    {
        var binding = PresentationAccountBinding(_selectedConnectionId);
        var google = binding?.Provider == SyncProviders.GoogleDrive
            ? _authorityHost.LoadGoogleDriveConnection(binding.ConnectionId) : null;
        if (_sharedDrivesRow is not null)
            _sharedDrivesRow.Visibility = google is null ? Visibility.Collapsed : Visibility.Visible;
        if (_sharedDrivesToggle is not null)
        {
            _sharedDrivesToggle.IsChecked = google?.SharedDrivesEnabled == true;
            _sharedDrivesToggle.IsEnabled = google is not null && !_savingSharedDrives;
        }
        var providerName = binding?.ProviderName ?? "Cloud storage";
        var providerTemplate = binding is null ? null : ProviderPresentation.For(binding.Provider).IconResourceKey;
        if (_connectionProviderIcon is not null)
        {
            _connectionProviderIcon.ContentTemplate = providerTemplate is null ? null : FindResource(providerTemplate) as DataTemplate;
            _connectionProviderIcon.Content = providerTemplate is null ? CommandIcons.Icon("cloud", 28) : new object();
        }
        ConnectionNameText.Text = binding is null ? "Not connected"
            : AccountLabel(providerName, binding.DisplayName, binding.Email);
        // The email is carried once, by the label above; a second copy would only repeat it.
        ConnectionEmailText.Text = binding is null ? "—" : binding.Email;
        ConnectionEmailText.Visibility = Visibility.Collapsed;
        ConnectionNameText.ToolTip = binding is null
            ? "No cloud account is connected. Choose Add Account to connect one."
            : ConnectionNameText.Text + "\n" + providerName + " account\nConnection " + binding.ConnectionId;
        ConnectionEmailText.ToolTip = ConnectionNameText.ToolTip;
        if (_connectionPairsLink is not null)
            System.Windows.Automation.AutomationProperties.SetName(_connectionPairsLink, binding is null
                ? "Folder Pairs linked to this account" : "Show folder pairs for " + ConnectionNameText.Text);
    }

    private async Task SetSharedDrivesFromUiAsync()
    {
        if (_savingSharedDrives || _sharedDrivesToggle is null || _selectedConnectionId is not { } account) return;
        var enabled = _sharedDrivesToggle.IsChecked == true;
        _savingSharedDrives = true;
        _sharedDrivesToggle.IsEnabled = false;
        try
        {
            await _authorityHost.SetGoogleSharedDrivesEnabledAsync(account, enabled);
            ShowStatus(enabled ? "Shared drives enabled for this Google account. No files were synced." :
                "Shared drives disabled for this Google account. Shared-drive pairs cannot sync until re-enabled; no files were changed.");
        }
        catch (Exception error) { ShowStatus("Shared-drive setting was not changed: " + error.Message); }
        finally { _savingSharedDrives = false; UpdateConnectionIdentity(); }
    }

    private void RefreshAccountPauseButton()
    {
        if (_accountPauseButton is null) return;
        var paused = _selectedConnectionId is not null && PresentationAccountPaused(_selectedConnectionId);
        _accountPauseButton.Content = CommandIcons.Label(paused ? "Resume Account" : "Pause Account", paused ? "play" : "pause");
        _accountPauseButton.IsEnabled = _selectedConnectionId is not null;
        _accountPauseButton.ToolTip = "Pause only this account. An operation already in progress finishes safely; other " +
            "accounts continue. A run that already spans more than one account refuses an account pause instead of " +
            "pausing everything.";
    }

    internal void OpenAccountPauseMenu(UIElement target, string? connectionId = null)
    {
        var id = connectionId ?? _selectedConnectionId;
        if (id is null) return;
        var menu = WorkspaceMenu(); menu.PlacementTarget = target;
        // The account scope shares the global option set; the scope lives on the menu, so every row
        // is exactly the option name.
        menu.ToolTip = "Pause or resume this account only. Other accounts and other pause scopes are unchanged.";
        if (_authorityHost.IsAccountPaused(id))
            MenuAction(menu, "Resume Account", "play", "Resumes this account only. Global and pair pauses are unchanged.",
                async (_, _) => await ResumePausedAsync(id));
        foreach (var option in PauseOption.All)
            MenuAction(menu, option.Label, option.StopSquare ? "stop" : "pause", option.Detail,
                async (_, _) => await SetPausedAsync(option, id));
        menu.IsOpen = true;
    }

    private async Task SetAccountPauseAsync(string id, TimeSpan? duration, bool indefinitely)
    {
        if (_authorityHost.LoadAccountBinding(id) is null) { ShowStatus("The selected account is unavailable."); return; }
        if (indefinitely) { await SetPausedAsync(PauseOption.Indefinitely, id); }
        else if (duration is { } delay) { await SetPausedDurationAsync(delay, id); }
        else { await ResumePausedAsync(id); }
        RefreshAccountPauseButton();
    }

    private void AddStorageAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AuthorizationAttemptWaiting) { ShowStatus("Finish or cancel the waiting connection first."); return; }
        if (_runActive) { ShowStatus("Wait for the current sync to finish before adding an account."); return; }
        var menu = WorkspaceMenu(); menu.PlacementTarget = sender as UIElement;
        menu.Items.Add(StorageAccountMenuItem("Dropbox", SyncProviders.Dropbox, async (_, _) =>
        {
            try
            {
                var binding = await AuthorizeDropboxFromUiAsync(token =>
                    _authorityHost.ConnectDropboxAsync(OpenAuthorizationPageForUiAsync, token, addAccount: true));
                _selectedConnectionId = binding.ConnectionId;
                RefreshAccountPickers(); await RefreshConnectionStateAsync();
                ShowStatus("Dropbox Account Added. Existing pairs remain on their original accounts. Choose this account when adding a new pair.");
            }
            catch (Exception error) { ShowConnectionFailure("Account was not added: " + error.Message); }
        }));
        menu.Items.Add(StorageAccountMenuItem("Google Drive", SyncProviders.GoogleDrive, async (_, _) =>
        {
            try
            {
                var binding = await AuthorizeAccountFromUiAsync(token =>
                    _authorityHost.ConnectGoogleDriveAsync(OpenAuthorizationPageForUiAsync, token), "Google Drive");
                _selectedConnectionId = binding.ConnectionId;
                RefreshAccountPickers(); await RefreshConnectionStateAsync();
                ShowStatus("Google Drive account added. Choose this account when adding a folder pair.");
            }
            catch (Exception error) { ShowConnectionFailure("Google account was not added: " + error.Message); }
        }));
        menu.IsOpen = true;
    }

    // P1: an Add Account choice carries the same official provider mark the Connections account
    // selectors use, in place of the generic glyph the label text would otherwise infer. Text, order,
    // enabled state and the click handler stay exactly as the caller supplies them.
    private MenuItem StorageAccountMenuItem(string label, string provider, RoutedEventHandler action)
    {
        var header = CommandIcons.Label(label);
        header.Children.RemoveAt(0);
        header.Children.Insert(0, SharedPresentation.ProviderMark(provider, 16, this));
        var item = new MenuItem { Header = header };
        System.Windows.Automation.AutomationProperties.SetName(item, label);
        item.Click += action;
        return item;
    }

    private void ShowConnectionFailure(string message)
    {
        // A failed attempt to add another account is not the selected account's health.
        ShowStatus(message);
    }

    private async Task<DropboxConnectionBinding> AuthorizeDropboxFromUiAsync(
        Func<CancellationToken, Task<DropboxConnectionBinding>> authorize)
        => await AuthorizeAccountFromUiAsync(authorize, "Dropbox");

    private async Task<T> AuthorizeAccountFromUiAsync<T>(Func<CancellationToken, Task<T>> authorize, string provider)
    {
        if (AuthorizationAttemptWaiting)
            throw new InvalidOperationException("Finish or cancel the waiting connection first.");
        NavigateTo("Connections");
        // AUTH2: no window-owned cancellation source exists. The host's attempt identity is the single
        // handle this window displays and cancels through the shared exact-target operation, and the
        // window-lifetime caller boundary is that same operation (see OnClosed).
        _uiAuthorizationStartInProgress = true;
        try { return await authorize(CancellationToken.None); }
        catch (OperationCanceledException) when (_authorizationCancellationRequested)
        {
            throw new InvalidOperationException("Connection cancelled. Existing accounts were kept; you can try again now.");
        }
        finally
        {
            _uiAuthorizationStartInProgress = false;
            RefreshAuthorizationPresentation();
            SetRunControls();
        }
    }

    // Fixture seam: production opens the registered provider page through the existing shell
    // activation. A fixture replaces only that opener, so the real shared host authorization
    // lifecycle, its exact-target cancellation and this panel are exercised without a browser,
    // a provider request or a credential read.
    internal static Func<Uri, Task>? AuthorizationPageOpenerForTest { get; set; }
    internal static Task OpenAuthorizationPageForUiAsync(Uri uri) =>
        (AuthorizationPageOpenerForTest ?? OpenAuthorizationPageAsync)(uri);

    // AUTH2: the pending state is the shared host attempt, never a window-owned token.
    private bool AuthorizationAttemptWaiting => _authorityHost.PendingAuthorization is not null;

    // The host raises this from whichever thread changed the attempt, including the bridge thread
    // that runs a command-line request. Posting the refresh never blocks that thread and never
    // blocks the UI thread.
    private void AuthorizationAttemptChanged()
    {
        if (_closing) return;
        if (Dispatcher.CheckAccess()) RefreshAuthorizationPresentation();
        else Dispatcher.InvokeAsync(RefreshAuthorizationPresentation);
    }

    // The displayed cancel action is bound to the exact attempt id it was rendered for, so a queued
    // stale action can only ever be refused: it can never cancel a newer attempt.
    private void CancelPendingAuthorizationFromUi()
    {
        var attemptId = _observedAuthorizationAttemptId ?? _authorityHost.PendingAuthorization?.AttemptId;
        if (attemptId is null)
        {
            ShowStatus("No interactive authorization attempt is waiting.");
            return;
        }
        if (_authorityHost.RequestAuthorizationAttemptCancellation(attemptId))
            _authorizationCancellationRequested = true;
        else
            ShowStatus("Authorization attempt " + attemptId + " was already finishing; no other attempt was cancelled.");
        RefreshAuthorizationPresentation();
    }

    // Only the attempt that actually ended clears this presentation. A cancellation requested by the
    // command line is reported truthfully, exactly like one requested here.
    private void RefreshAuthorizationPresentation()
    {
        var snapshot = _authorityHost.PendingAuthorization;
        if (snapshot is null)
        {
            var finished = _observedAuthorizationAttemptId;
            var fromCommandLine = finished is not null && !_observedAuthorizationStartedByThisWindow;
            var wasCancelled = _authorizationCancellationRequested;
            _observedAuthorizationAttemptId = null;
            _observedAuthorizationStartedByThisWindow = false;
            _authorizationCancellationRequested = false;
            if (_authorizationPanel is not null) _authorizationPanel.Visibility = Visibility.Collapsed;
            if (_authorizationMessage is not null) _authorizationMessage.Text = string.Empty;
            if (_authorizationCancelButton is not null) _authorizationCancelButton.IsEnabled = false;
            if (fromCommandLine)
                ShowStatus(wasCancelled
                    ? "The authorization attempt started from the command line was cancelled. Existing accounts " +
                      "were kept; you can try again now."
                    : "The authorization attempt started from the command line finished. The Connections page " +
                      "shows the retained result.");
        }
        else
        {
            if (!string.Equals(_observedAuthorizationAttemptId, snapshot.AttemptId, StringComparison.Ordinal))
            {
                _observedAuthorizationAttemptId = snapshot.AttemptId;
                _observedAuthorizationStartedByThisWindow = _uiAuthorizationStartInProgress;
                _authorizationCancellationRequested = false;
            }
            if (snapshot.Status == SyncPendingAuthorizationSnapshot.CancellationRequestedPhase)
                _authorizationCancellationRequested = true;
            if (_authorizationPanel is not null) _authorizationPanel.Visibility = Visibility.Visible;
            if (_authorizationMessage is not null) _authorizationMessage.Text = AuthorizationMessage(snapshot);
            if (_authorizationCancelButton is not null)
                _authorizationCancelButton.IsEnabled = !_authorizationCancellationRequested;
        }
        if (_addAccountButton is not null) _addAccountButton.IsEnabled = !AuthorizationAttemptWaiting;
        if (_accountPicker is not null) _accountPicker.IsEnabled = !AuthorizationAttemptWaiting;
        SetRunControls();
    }

    private string AuthorizationMessage(SyncPendingAuthorizationSnapshot snapshot)
    {
        var provider = SyncProviders.DisplayName(snapshot.Provider);
        var attempt = "\nAuthorization attempt " + snapshot.AttemptId + ".";
        return (_authorizationCancellationRequested
            ? "Cancelling the " + provider + " connection. Waiting for that authorization to finish."
            : _observedAuthorizationStartedByThisWindow
                ? "Finish connecting " + provider + " in your browser. You can cancel here and try again. This " +
                  "request expires after 5 minutes."
                : "The command line started a " + provider + " authorization. Finish it in your browser, or " +
                  "cancel exactly this attempt here.") + attempt;
    }
}
