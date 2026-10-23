using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;
using StackPanel = System.Windows.Controls.StackPanel;
using Orientation = System.Windows.Controls.Orientation;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private string? _workspaceAccountId;
    private readonly List<ComboBox> _workspaceAccountPickers = [];
    private bool _updatingWorkspaceAccounts;
    private int _workspaceScopeGeneration;
    private Task _workspaceScopeRefresh = Task.CompletedTask;
    internal static Func<string?, Task<IReadOnlyList<SyncAuthorityActivityProjection>>>? SummaryHistoryLoaderForTest { get; set; }
    private int? _workspaceScopePending;
    private SyncAccountStatus? _workspaceScopeStatus;
    // V6: the Home last-success line describes the CURRENT account scope. All Accounts resolves every pair,
    // including an accountless local/local pair; a selected account resolves only the pairs this window
    // attributes to it, so an accountless local/local pair belongs to no single account. The primary
    // connection status stays primary-specific and is never rewritten with another scope's number.
    private DateTimeOffset? _scopeLastSuccessfulUtc;
    private readonly List<ActivityRow> _scopedHomeHistory = [];
    private string? _workspaceRunningPairId;
    private bool _updatingAttentionPairs;
    private bool _updatingActivityPairs;
    private string? _activityFilterAccountScope;
    private readonly List<DecisionRow> _attentionConflictRows = [];
    private bool ScopeMatches(string? pairId) => TrayMatchesAccount(pairId, _workspaceAccountId);
    private string WorkspaceScopeLabel => _workspaceAccountId is null ? "All Accounts" :
        _accountLabels.GetValueOrDefault(_workspaceAccountId, "Account Unavailable");
    private DateTimeOffset? ScopeLastSuccessfulSyncUtc => _scopeLastSuccessfulUtc;

    // V6: the ONE resolution for the CURRENT account scope's last successful retained run. All Accounts asks
    // the existing authority for every pair (so an accountless local/local pair counts); a selected account
    // asks only for the pairs this window's own account attribution names. An absent value stays null.
    private async Task<DateTimeOffset?> ResolveScopeLastSuccessfulSyncUtcAsync()
    {
        if (!_authorityHost.AuthorityStateExists) return null;
        if (_workspaceAccountId is null)
            return await Task.Run(() => _authorityHost.LoadLastSuccessfulSyncUtcAsync());
        var pairIds = _pairSet.Pairs.Where(pair => ScopeMatches(pair.Id)).Select(pair => pair.Id).ToArray();
        return await Task.Run(() => _authorityHost.LoadLastSuccessfulSyncUtcAsync(allowedPairIds: pairIds));
    }

    private FrameworkElement BuildWorkspaceAccountPicker(string surface)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var picker = new ComboBox { MinWidth = 170, MaxWidth = 340, ItemTemplate = SharedPresentation.AccountChoiceTemplate(),
            SelectedValuePath = nameof(ActivityPairChoice.ConnectionId), ToolTip = "Show all accounts or only one account." };
        picker.Style = SharedPresentation.QuietComboBoxStyle();
        System.Windows.Automation.AutomationProperties.SetName(picker, surface + " Account");
        _workspaceAccountPickers.Add(picker);
        picker.SelectionChanged += async (_, _) =>
        {
            if (_updatingWorkspaceAccounts || picker.SelectedItem is not ActivityPairChoice choice) return;
            ApplyAccountScopeTransition(choice.ConnectionId);
            RefreshWorkspaceAccountPickers();
            RefreshAttentionPairChoices();
            RefreshActivityAccountScope();
            ClearMatchingConflictSelection(); ApplyConflictFilter();
            _attentionFingerprint = ""; RefreshWorkspaceFeeds(); RefreshWorkspace();
            await RefreshVisibleActivityAsync();
            await _workspaceScopeRefresh;
            if (!SuppressLoadedStartupForTest)
            { await LoadConflictPageAsync(true); if (_attentionTabs?.SelectedIndex == 1) await RefreshBootstrapReviewAsync(); }
        };
        row.Children.Add(picker); RefreshWorkspaceAccountPickers(); return row;
    }

    private void RefreshAttentionPairChoices()
    {
        if (BootstrapPairBox is null) return;
        var choices = PairRows.Where(pair => ScopeMatches(pair.Id)).ToArray();
        var selected = BootstrapPairBox.SelectedValue as string;
        _updatingAttentionPairs = true;
        try
        {
            BootstrapPairBox.ItemsSource = choices;
            BootstrapPairBox.SelectedValue = choices.FirstOrDefault(pair => pair.Id == selected)?.Id ??
                choices.FirstOrDefault(pair => pair.Id == _pairSet.SelectedPairId)?.Id ?? choices.FirstOrDefault()?.Id;
        }
        finally { _updatingAttentionPairs = false; }
        UpdateFirstSyncActionAvailability();
    }

    private void IncludeAttentionPair(string pairId)
    {
        if (ScopeMatches(pairId)) return;
        ApplyAccountScopeTransition(_pairSet.Pairs.FirstOrDefault(pair => pair.Id == pairId)?.ConnectionId);
        RefreshWorkspaceAccountPickers(); RefreshAttentionPairChoices();
        ClearMatchingConflictSelection(); _attentionFingerprint = "";
    }

    // The one account-scope transition used by every scope entry point: synchronously invalidate the
    // previous scope's in-flight async generation and scoped caches, refresh what is already rendered so a
    // newly shown header cannot disagree with stale rows, then leave repopulation to the existing async
    // scoped load. Picker refresh stays with the caller so a picker cannot recurse through this transition.
    private void ApplyAccountScopeTransition(string? accountId)
    {
        if (string.Equals(_workspaceAccountId, accountId, StringComparison.Ordinal)) return;
        _workspaceAccountId = accountId;
        _workspaceScopeGeneration++;
        _workspaceScopePending = null; _workspaceScopeStatus = null; _scopeLastSuccessfulUtc = null;
        _scopedHomeHistory.Clear();
        _attentionFingerprint = "";
        // The existing Activity/Queue scope path already bumps its own query versions and resets offsets;
        // calling it here clears stale Activity and Queue rows through that one path rather than a new one.
        RefreshActivityAccountScope();
        RefreshWorkspaceFeeds();
        RefreshWorkspace();
        // Navigation and tray entry points must also replenish the new scope, not leave
        // its pending/status placeholders waiting for a later unrelated refresh.
        if (_workspaceReady) _workspaceScopeRefresh = RefreshWorkspaceScopeDataAsync();
    }

    private void RefreshWorkspaceAccountPickers()
    {
        // SS-D12: this helper resolves every account label from one presentation snapshot instead of a
        // store read per account, and it joins the caller's scope when one is already open.
        using var presentation = OpenPresentationScope();
        _updatingWorkspaceAccounts = true;
        try
        {
            var choices = new[] { new ActivityPairChoice(null, "All Accounts") }.Concat(_accountLabels
                .Select(account => {
                    var binding = PresentationAccountBinding(account.Key);
                    return new ActivityPairChoice(null, account.Value, account.Key, binding?.Provider ?? "",
                        binding is null ? account.Value : AccountLabel(binding.ProviderName, binding.DisplayName, binding.Email));
                })).ToArray();
            if (_workspaceAccountId is not null && !choices.Any(item => item.ConnectionId == _workspaceAccountId)) ApplyAccountScopeTransition(null);
            foreach (var picker in _workspaceAccountPickers)
            { picker.ItemsSource = choices; picker.SelectedItem = choices.First(item => item.ConnectionId == _workspaceAccountId); }
        }
        finally { _updatingWorkspaceAccounts = false; }
    }

    private async Task RefreshWorkspaceScopeDataAsync()
    {
        var generation = ++_workspaceScopeGeneration;
        var account = _workspaceAccountId;
        if (account is null)
        {
            // V6: All Accounts resolves the latest successful retained run across EVERY pair - including a
            // local/local pair, which has no provider account of its own - through the existing authority
            // query, under the same generation guard the per-account load uses so a stale response cannot
            // overwrite a newer scope.
            DateTimeOffset? allAccounts;
            try
            {
                allAccounts = await ResolveScopeLastSuccessfulSyncUtcAsync();
            }
            catch (Exception error)
            {
                if (generation == _workspaceScopeGeneration)
                    ShowStatus("The last successful sync could not be read: " + error.Message);
                return;
            }
            if (generation != _workspaceScopeGeneration || _workspaceAccountId is not null) return;
            _scopeLastSuccessfulUtc = allAccounts;
            RefreshWorkspaceFeeds(); RefreshWorkspace(); return;
        }
        try
        {
            // V6: the selected account's own last success is resolved here through the same one resolution
            // path, so an accountless local/local pair is never attributed to this account.
            var accountLastSuccessful = await ResolveScopeLastSuccessfulSyncUtcAsync();
            var page = await LoadSummaryHistoryAsync(account);
            var pending = await _authorityHost.LoadPendingQueueCountAsync(connectionId: account);
            var status = await _authorityHost.GetAccountStatusAsync(false, connectionId: account);
            if (generation != _workspaceScopeGeneration || account != _workspaceAccountId) return;
            _scopedHomeHistory.Clear();
            _scopedHomeHistory.AddRange(page.Select(RetainedActivityRow));
            _workspaceScopePending = pending; _workspaceScopeStatus = status;
            _scopeLastSuccessfulUtc = accountLastSuccessful;
            RefreshWorkspaceFeeds(); RefreshWorkspace();
        }
        catch (Exception error)
        { if (generation == _workspaceScopeGeneration) ShowStatus("This account's overview could not be refreshed: " + error.Message); }
    }

    private bool ScopeMatchesRun(SyncAuthorityRunHistory run)
    {
        if (_workspaceAccountId is null) return true;
        try
        {
            using var request = JsonDocument.Parse(run.RequestJson);
            return request.RootElement.ValueKind == JsonValueKind.Object &&
                request.RootElement.TryGetProperty("pairIds", out var ids) && ids.ValueKind == JsonValueKind.Array &&
                ids.EnumerateArray().Any(id => id.ValueKind == JsonValueKind.String && ScopeMatches(id.GetString()));
        }
        catch (JsonException) { return false; }
    }

    // The compact header is the sole account selector. Pair filtering narrows that scope;
    // it must never silently supply a different account to the retained-page query.
    private void RefreshActivityPairChoices()
    {
        if (ActivityPairBox is null) return;
        var selected = (ActivityPairBox.SelectedItem as ActivityPairChoice)?.Id;
        var choices = new[] { new ActivityPairChoice(null, "All pairs") }.Concat(_pairSet.Pairs
            .Where(pair => ScopeMatches(pair.Id)).Select(pair => new ActivityPairChoice(pair.Id,
                _workspaceAccountId is null && _accountLabels.Count > 1
                    ? pair.Name + " — " + LabelForPair(pair) : pair.Name))).ToArray();
        if (ActivityPairChoices.SequenceEqual(choices)) return;
        _updatingActivityPairs = true;
        try
        {
            ActivityPairChoices.Clear();
            foreach (var choice in choices) ActivityPairChoices.Add(choice);
            ActivityPairBox.SelectedItem = ActivityPairChoices.FirstOrDefault(item => item.Id == selected)
                ?? ActivityPairChoices[0];
        }
        finally { _updatingActivityPairs = false; }
    }

    private void RefreshActivityAccountScope()
    {
        if (!string.Equals(_activityFilterAccountScope, _workspaceAccountId, StringComparison.Ordinal))
        {
            _activityFilterAccountScope = _workspaceAccountId;
            Interlocked.Increment(ref _activityQueryVersion);
            Interlocked.Increment(ref _queueQueryVersion);
            _activityRetainedOffset = 0;
            _activityHasMore = false;
            ActivityRows.Clear(); QueueRows.Clear();
            _inspectedActivity = null;
            // The retained run selection and its details belong to the previous scope just as the
            // activity selection does, so they are dropped through the same invalidation path.
            ClearRetainedRunSelection();
            if (_workspaceReady) CloseWorkspaceDetails();
        }
        RefreshActivityPairChoices();
    }

    private async Task RefreshVisibleActivityAsync()
    {
        RefreshActivityAccountScope();
        if (!_workspaceReady || _workspacePage != "Activity") return;
        if (_activityTabs.SelectedIndex == 1)
        { await LoadQueuePageAsync(true); ApplyPendingViewFilter(); }
        else if (_activityTabs.SelectedIndex == 2) RenderRunHistory();
        else if (_activityTabs.SelectedIndex == 3) await LoadOperationLogAsync(true);
        else await LoadActivityPageAsync(true);
    }

    private void OpenScopedActivity(int tab)
    {
        RefreshActivityAccountScope();
        ActivityPairBox.SelectedItem = ActivityPairChoices.FirstOrDefault(item => item.Id is null);
        OpenWorkspaceActivity(tab);
    }

    // UI20: the one clickable linked-pair count is the only control for this link. It selects the
    // existing app-wide exact-account scope by stable connection id, so every scoped surface agrees,
    // and it never selects a pair by display name or by row index.
    private void OpenPairsForAccount() => OpenPairsForAccount(_selectedConnectionId);

    private void OpenPairsForAccount(string? connectionId)
    {
        ApplyAccountScopeTransition(connectionId);
        RefreshWorkspaceAccountPickers();
        RefreshAttentionPairChoices();
        ApplyPairViewFilter();
        if (!string.Equals(_workspacePage, "Pairs", StringComparison.Ordinal)) NavigateTo("Pairs");
    }

    // The Pairs list keeps one filter, so the account scope and the existing search box are applied
    // through the same predicate instead of a second competing filter that could silently drop one.
    private bool PairRowMatchesVisibleFilter(PairRow row)
    {
        var query = PairSearchBox.Text.Trim();
        return (query.Length == 0 || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Connection.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.LocalRoot.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.DropboxRoot.Contains(query, StringComparison.OrdinalIgnoreCase)) && ScopeMatches(row.Id);
    }

    private void ApplyPairViewFilter()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(PairRows);
        if (view is null) return;
        view.Filter = item => item is PairRow row && PairRowMatchesVisibleFilter(row);
        view.Refresh();
    }
}
