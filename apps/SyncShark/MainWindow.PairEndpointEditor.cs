using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace Massdex.SyncShark;

// E2: the compact symmetric pair editor surface (EP-03), with a real folder picker on each side.
//
// One persistent SyncPairEditorSession owns the draft for as long as this editor is open. The strip
// presents that session and routes every endpoint control into it; it never rebuilds the draft from the
// window's local-left/cloud-right fields, which is what previously let one side's choices appear on the
// other and left the left cloud side without an account or folder control at all.
//
// Each side owns its own kind, account, local path or cloud folder and browse ticket. A browse result is
// applied only while its ticket is still that side's current choice, so a late result for an account or
// kind the side no longer names cannot overwrite the newer selection.
//
// The retained ordered-shape fields the existing save path reads (LocalRootBox, _selectedDropboxFolder,
// ...) are kept in step for that path only. They are an integration bridge into the existing authority
// save, not this surface's state.
public partial class MainWindow
{
    private SyncPairEditorSession? _endpointSession;
    private bool _endpointStripBuilt;
    private bool _refreshingEndpointStrip;

    // Which side's Browse command armed the folder tree on this computer. The tree is the existing one;
    // arming a side is what makes a tick in that tree belong to that side's endpoint.
    private SyncPairSide? _localEndpointBrowseSide;

    // UI354-PROVIDER1: the side whose kind the user explicitly changed in this editor open. Only an
    // explicit choice speaks for a side that has no account yet, so a default or programmatic kind can
    // never displace a chosen or retained account's own presentation.
    private SyncPairSide? _explicitEndpointKindSide;

    /// <summary>
    /// True while the explicit session names both endpoints, so the one endpoint Save path owns this
    /// draft. A draft the session cannot name yet keeps the retained two-tree draft.
    /// </summary>
    private bool EndpointPairAuthored => EndpointDraft() is { Endpoints: not null };

    /// <summary>
    /// The one endpoint Save path (EUI-S1). Every ordered combination this build can bind - both local
    /// folders, cloud-then-local and local-then-cloud - is created from the explicit session through the
    /// shared SyncEndpointPairDraft.Create and the authority host's endpoint binding boundary. The
    /// retained two-tree planner stays a rules source only, and only while it describes the same
    /// physical pair; a plan that no longer matches the explicit endpoints is refused rather than bound.
    /// </summary>
    private async Task<bool> SaveEndpointPairAsync(SyncPairDraft draft)
    {
        SavePairButton.IsEnabled = false;
        ShowPairEditorStatus("Checking the complete pair choices and verifying folder ownership before saving…");
        try
        {
            var endpoints = draft.Endpoints ?? throw new InvalidOperationException("Choose both endpoints first.");
            if (!draft.Validation.IsValid) throw new InvalidDataException(draft.Validation.Describe());
            if (draft.UnsupportedBindingReason is { } unsupportedBinding)
                throw new NotSupportedException(unsupportedBinding);
            if (!int.TryParse(PairPriorityBox.Text, out var priority))
                throw new InvalidDataException("Priority must be a whole number from -100 to 100.");
            var name = PairNameBox.Text.Trim();
            if (name.Length == 0) throw new InvalidDataException("Enter a name for this folder pair.");
            var existing = RetainedPairForEditor();
            if (draft.EditingPairId != existing?.Id ||
                existing is not null && draft.ExpectedPolicyRevision != existing.PolicyRevision)
                throw new InvalidOperationException(
                    "This pair changed after the editor opened. Reload its current settings before saving; your draft was not applied.");
            var localOnly = endpoints.Left.IsLocal && endpoints.Right.IsLocal;
            var cloudOnly = !endpoints.Left.IsLocal && !endpoints.Right.IsLocal;
            var layout = cloudOnly ? null : SyncEndpointEngineLayout.Create(endpoints);
            var supportsDeviceStorage = !localOnly && !cloudOnly;
            var plan = MatchingDraftBindingPlan(endpoints);
            if (_globalDraftPlan is not null && plan is null)
                throw new InvalidOperationException(
                    "The folder-tree selection no longer describes the chosen endpoints. Tick the matching folders again before saving.");
            var requestedFilesOnDemand = PairFilesOnDemandBox.IsChecked == true;
            // EUI-S3: both local endpoints force managed cloud storage off; a pair with one cloud
            // endpoint keeps the established value and the explicit cloud-files confirmation.
            var filesOnDemandForBinding = supportsDeviceStorage && (FilesOnDemandValueForBinding(existing?.FilesOnDemandEnabled,
                requestedFilesOnDemand) || _draftNeedsCloudFiles);
            if (supportsDeviceStorage && _draftNeedsCloudFiles && !AppDialogWindow.Confirm(this, "Enable Folder Storage Controls",
                "Let Sync Shark manage storage for this pair?",
                "The pair's default stays Keep Offline. Only folders you choose as Cloud Only can be freed after their exact provider copy is verified. This does not take over folders managed by another provider.",
                "Enable and Save")) return false;
            // The explicitly selected Recovery container is retained only for the cloud account this
            // explicit draft names; otherwise the host prepares the recommended Recovery as before.
            var recovery = supportsDeviceStorage && _selectedRecoveryFolder is { } selectedRecovery &&
                string.Equals(_pairDraftConnectionId, layout!.Remote.ConnectionId, StringComparison.Ordinal)
                    ? selectedRecovery
                    : null;
            // The SAME complete-candidate derivation the draft-availability decision consumes, so the
            // exclusion policy that admitted this draft is exactly the policy being bound. A stale plan or
            // an underivable candidate refuses rather than borrowing the retained or an older plan.
            var candidate = CompleteEndpointCandidate(endpoints, existing)
                ?? throw new InvalidDataException(draft.Validation.IsValid
                    ? "The complete pair policy for these endpoints could not be derived, so the draft was not saved."
                    : draft.Validation.Describe());
            if (candidate.Priority != priority)
                throw new InvalidDataException(
                    "The pair priority changed while the draft was being saved; the draft was not applied.");
            // The confirmation-derived part stays here: the cloud-files answer must never be requested
            // while the editor is only deciding availability.
            var pair = candidate with { FilesOnDemandEnabled = filesOnDemandForBinding };
            var result = await _authorityHost.BindEndpointPairAsync(pair, existing?.Id, draft.ExpectedPolicyRevision,
                CancellationToken.None, recovery,
                progress: detail => Dispatcher.Invoke(() => ShowPairEditorStatus(detail)));
            // SS-D03: the successful authoritative return IS the commit. This identity is adopted and this
            // exact draft is closed by the shared pipeline before any storage, row, monitor or Explorer
            // follow-up can fail; each follow-up reports separately and none can be retried into a second
            // Bind.
            var status = existing is null || result.IdentityChanged
                ? "Pair saved. Review its first sync before files are copied between the two folders."
                : "Pair saved. The ordered endpoints and their recovery locations are verified.";
            var followUpProblems = await FinishPairSaveAsync(result, status, result.IdentityChanged
                ? "Authoritative endpoint identity changed; a new pair ID was created and the old baseline was retired."
                : "The ordered endpoints and their recovery locations were verified; initial discovery will be effect-free.");
            if (result.Pair.FilesOnDemandEnabled)
                await RunPairSaveFollowUpAsync(followUpProblems, "the storage settings", async () =>
                {
                    var storage = await _authorityHost.ApplyPendingDirectoryStorageAsync(result.Pair.Id,
                        onProgress: entry => Dispatcher.InvokeAsync(() => ShowPairEditorStatus(
                            entry.Comparison is { } detail ? SyncProgressDescription.Describe(detail) : entry.State)).Task);
                    _pairSet = await _authorityHost.LoadPairsAsync();
                    if (storage.Failures.Count != 0)
                        throw new IOException("Storage remains pending for " + storage.Failures.Count +
                            " items; existing bytes are preserved. " + string.Join(Environment.NewLine,
                                storage.Failures.Select(SyncActivityText.Safe)));
                });
            if (supportsDeviceStorage)
                await RunPairSaveFollowUpAsync(followUpProblems, "the Explorer integration refresh",
                    () => RefreshExplorerIntegrationAsync());
            if (followUpProblems.Count > 0)
            {
                // Projection refreshes can open the saved pair's editor again. Close that draft before
                // presenting a retry, so Save retries only this committed identity's retained follow-ups.
                EndPairDraft();
                var followUpStatus = PairSaveStatusWithFollowUps(status, followUpProblems);
                ShowPairEditorStatus(followUpStatus, isError: true);
                ShowStatus(followUpStatus);
            }
            return true;
        }
        catch (Exception error)
        {
            ShowPairEditorStatus("Pair was not saved: " + error.Message, isError: true);
            ShowStatus("Pair was not saved: " + error.Message);
            return false;
        }
        finally { SetRunControls(); }
    }

    /// <summary>
    /// The retained two-tree plan while - and only while - it describes the same physical pair as the
    /// explicit endpoints. An established selective-sync tree draft reaches the one binding pipeline
    /// through this; once its roots no longer match it is stale and must never supply another pair's rules.
    /// </summary>
    private PairDraftBindingPlan? MatchingDraftBindingPlan(SyncPairEndpoints endpoints)
    {
        if (_globalDraftPlan is not { } plan) return null;
        if (!endpoints.Left.IsLocal && !endpoints.Right.IsLocal) return null;
        var layout = SyncEndpointEngineLayout.Create(endpoints);
        if (!string.Equals(PilotPath.NormalizeLocalRoot(plan.LocalRoot),
                PilotPath.NormalizeLocalRoot(layout.Local.LocalPath!), StringComparison.OrdinalIgnoreCase))
            return null;
        return !layout.Remote.IsLocal && string.Equals(plan.DropboxRoot.FolderId, layout.Remote.FolderId,
            StringComparison.Ordinal) ? plan : null;
    }

    /// <summary>
    /// The endpoint availability projection (EUI-S4). Both-local pairs force managed cloud storage off;
    /// a pair with one cloud endpoint keeps the established storage controls for the folder on this
    /// computer, read through the shared physical layout rather than an assumed local-left side.
    /// </summary>
    private void UpdateEndpointSaveAvailability(SyncPairDraft draft)
    {
        var localOnly = draft.Endpoints is { Left.IsLocal: true, Right.IsLocal: true };
        var cloudOnly = draft.Endpoints is { Left.IsLocal: false, Right.IsLocal: false };
        if (localOnly || cloudOnly)
        {
            PairStorageBox.IsEnabled = false;
            StorageSupportText.Text = cloudOnly
                ? "Both folders are in the cloud. Device storage and Files On Demand do not apply."
                : "Both folders are local. Files stay available on their respective devices.";
            ActionAvailability.Refresh(PairStorageBox);
            return;
        }
        PairStorageBox.IsEnabled = !_runActive;
        UpdateStorageOptionsForSelectedRoot();
    }

    /// <summary>One offered account of the kind a side names, with its established compact label.</summary>
    private sealed record EndpointAccountChoice(SyncVerifiedAccountBinding Binding, string Label)
    {
        public string ConnectionId => Binding.ConnectionId;
    }

    /// <summary>Populates the selector item sources once, from the reusable presentation.</summary>
    private void BuildPairEndpointStrip()
    {
        if (_endpointStripBuilt) return;
        _endpointStripBuilt = true;
        var wasRefreshing = _refreshingEndpointStrip;
        _refreshingEndpointStrip = true;
        try
        {
            LeftEndpointKindBox.DisplayMemberPath = nameof(SyncEndpointKindChoice.ShortLabel);
            LeftEndpointKindBox.SelectedValuePath = nameof(SyncEndpointKindChoice.Kind);
            LeftEndpointKindBox.ItemsSource = SyncEndpointPresentation.KindChoices;
            RightEndpointKindBox.DisplayMemberPath = nameof(SyncEndpointKindChoice.ShortLabel);
            RightEndpointKindBox.SelectedValuePath = nameof(SyncEndpointKindChoice.Kind);
            RightEndpointKindBox.ItemsSource = SyncEndpointPresentation.KindChoices;
            PairDirectionBox.DisplayMemberPath = nameof(SyncDirectionChoice.Label);
            PairDirectionBox.SelectedValuePath = nameof(SyncDirectionChoice.Mode);
            PairDirectionBox.ItemsSource = SyncEndpointPresentation.DirectionChoices;
        }
        finally { _refreshingEndpointStrip = wasRefreshing; }
    }

    /// <summary>
    /// Starts the one draft this editor open owns. Called whenever the editor opens a new or retained
    /// pair, so no endpoint choice can leak from the pair that was previously open.
    /// </summary>
    private void BeginPairEndpointStrip()
    {
        BuildPairEndpointStrip();
        _localEndpointBrowseSide = null;
        _explicitEndpointKindSide = null;
        _endpointSession = CreateEndpointSession();
        RefreshPairEndpointStrip();
    }

    /// <summary>Ends the draft with the editor draft it belongs to. A cancelled draft builds no pair.</summary>
    private void EndPairEndpointStrip()
    {
        _localEndpointBrowseSide = null;
        _endpointSession?.Cancel();
        _endpointSession = null;
    }

    /// <summary>
    /// The draft for this editor open. A retained pair opens through the frozen compatibility mapping so
    /// each side keeps its own retained identity; a new pair opens on the ordered shape the binding
    /// pipeline retains - this computer on the left, its cloud provider on the right - and binds no
    /// account or folder the user did not choose for that side, so a new pair can never inherit another
    /// pair's account and neither side can move the other.
    /// </summary>
    private SyncPairEditorSession CreateEndpointSession()
    {
        if (!_editingNewPair && RetainedPairForEditor() is { } retained)
            return SyncPairEditorSession.ForExistingPair(retained, _pairEditorGeneration, out _,
                _authorityHost.LoadProviderConnections().Select(record => new SyncVerifiedAccountBinding(
                    record.Provider, record.ConnectionId, record.RootNamespaceId, false)));
        var session = SyncPairEditorSession.ForNewPair(_pairEditorGeneration);
        if (session.KindOf(SyncPairSide.Left) == SyncEndpointKind.Local &&
            !string.IsNullOrWhiteSpace(LocalRootBox.Text))
            session.PickerFor(SyncPairSide.Left).ApplyLocalFolder(
                session.PickerFor(SyncPairSide.Left).BeginPick(), LocalRootBox.Text);
        return session;
    }

    private SyncPair? RetainedPairForEditor() => _editingNewPair || _editingPairRootId is null
        ? null
        : _pairSet.Pairs.SingleOrDefault(item =>
            string.Equals(item.Id, _editingPairRootId, StringComparison.Ordinal));

    private void PairEndpointKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingEndpointStrip) return;
        if (_endpointSession is not { } session || sender is not ComboBox box) return;
        if (box.SelectedValue is not SyncEndpointKind kind) return;
        var chosenSide = EndpointSideOf(sender);
        session.SetKind(chosenSide, kind);
        _explicitEndpointKindSide = chosenSide;
        RefreshPairEndpointStrip();
        UpdatePairSaveAvailability();
    }

    private void PairEndpointAccount_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingEndpointStrip) return;
        if (_endpointSession is not { } session || sender is not ComboBox box) return;
        if (box.SelectedItem is not EndpointAccountChoice choice) return;
        var side = EndpointSideOf(sender);
        session.SetCloudAccount(side, choice.Binding);
        if (side == SyncPairSide.Right) KeepRetainedCloudAccountInStep(choice.ConnectionId);
        RefreshPairEndpointStrip();
        UpdatePairSaveAvailability();
    }

    private void PairDirection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingEndpointStrip) return;
        if (PairDirectionBox.SelectedIndex >= 0 && SyncTypeBox.SelectedIndex != PairDirectionBox.SelectedIndex)
            SetPairDirectionIndex(PairDirectionBox.SelectedIndex);
        RefreshPairEndpointStrip();
    }

    /// <summary>
    /// Writes one direction choice to both direction controls so the strip and Sync Settings can never
    /// disagree. The persisted SyncMode value is the selected index of the direction list, which is the
    /// retained SyncMode order, so no stored meaning changes.
    /// </summary>
    private void SetPairDirectionIndex(int index)
    {
        var wasRefreshing = _refreshingEndpointStrip;
        _refreshingEndpointStrip = true;
        try
        {
            if (Enum.IsDefined((SyncMode)index)) _endpointSession?.SetDirection((SyncMode)index);
            if (SyncTypeBox.SelectedIndex != index) SyncTypeBox.SelectedIndex = index;
            if (PairDirectionBox.SelectedIndex != index) PairDirectionBox.SelectedIndex = index;
        }
        finally { _refreshingEndpointStrip = wasRefreshing; }
    }

    private SyncPairSide EndpointSideOf(object sender) =>
        ReferenceEquals(sender, RightEndpointKindBox) || ReferenceEquals(sender, RightEndpointAccountBox) ||
        ReferenceEquals(sender, RightEndpointPathBox) || ReferenceEquals(sender, RightEndpointBrowseButton)
            ? SyncPairSide.Right
            : SyncPairSide.Left;

    /// <summary>
    /// Re-presents both sides from the one draft. The strip presents and routes; it never binds, browses
    /// or writes, and it never rebuilds the draft it is showing.
    /// </summary>
    private void RefreshPairEndpointStrip()
    {
        if (LeftEndpointKindBox is null || RightEndpointKindBox is null || PairDirectionBox is null ||
            LeftEndpointSummary is null || RightEndpointSummary is null || PairEndpointValidationText is null)
            return;
        if (_refreshingEndpointStrip) return;
        BuildPairEndpointStrip();
        if (_endpointSession is not { } session)
        {
            ClearEndpointStrip();
            return;
        }
        _refreshingEndpointStrip = true;
        try
        {
            LeftEndpointKindBox.SelectedValue = session.KindOf(SyncPairSide.Left);
            RightEndpointKindBox.SelectedValue = session.KindOf(SyncPairSide.Right);
            PairDirectionBox.SelectedIndex = SyncTypeBox.SelectedIndex is >= 0 and <= 2
                ? SyncTypeBox.SelectedIndex : (int)SyncMode.TwoWay;
            PresentEndpointSide(session, SyncPairSide.Left, LeftEndpointKindIcon, LeftEndpointSummary,
                LeftEndpointAccountBox, LeftEndpointPathBox, LeftEndpointBrowseButton);
            PresentEndpointSide(session, SyncPairSide.Right, RightEndpointKindIcon, RightEndpointSummary,
                RightEndpointAccountBox, RightEndpointPathBox, RightEndpointBrowseButton);
            PairRootGrid.Visibility = session.KindOf(SyncPairSide.Left) == SyncEndpointKind.Local &&
                session.KindOf(SyncPairSide.Right) != SyncEndpointKind.Local ? Visibility.Visible : Visibility.Collapsed;
            DropboxRootBox.ToolTip = SyncEndpointPresentation.ShortLabel(session.KindOf(SyncPairSide.Right)) +
                " folder linked to the other endpoint of this pair.";
            // UI354-PROVIDER1: the editor's provider labels, marks and accessible names follow the draft
            // the user is editing, and retained provider data that no longer matches it is dropped.
            ApplyExplicitEndpointProviderPresentation(session);
            ReconcileRetainedCloudContext(session);
            ApplyExplicitEditorLayout(true);
            PresentEndpointValidation(session);
        }
        finally { _refreshingEndpointStrip = false; }
    }

    /// <summary>
    /// UI354-PROVIDER1: the folder editor is described by the draft the user is actually editing. A kind
    /// change must move every provider label, mark, tooltip and accessible name immediately - before an
    /// account is chosen - so this follows the session's own cloud kind through the shared presentation
    /// mapping instead of the retained account binding.
    /// </summary>
    private void ApplyExplicitEndpointProviderPresentation(SyncPairEditorSession session)
    {
        ProviderPresentation = ExplicitEndpointPresentation(session) ??
            ProviderPresentation.For(ProviderForConnectionId(SelectedPairConnectionId) ??
                                     ProviderForConnectionId(_selectedConnectionId));
    }

    /// <summary>
    /// The presentation this draft itself names. A stored account's exact provider stays authoritative
    /// for its side; a side that has no account yet is described by its chosen kind. Retained pairs and
    /// legacy drafts therefore keep their established labels, while an unsaved kind change speaks for
    /// the side the user is editing.
    /// </summary>
    private ProviderPresentation? ExplicitEndpointPresentation(SyncPairEditorSession session)
    {
        if (SessionCloudKind(session) is not { } cloud) return null;
        // Only an explicit user kind choice speaks, and only for the side that has no account yet. A
        // retained pair, a programmatic default and any chosen account keep their existing authority.
        var cloudSide = cloud == session.KindOf(SyncPairSide.Right) ? SyncPairSide.Right : SyncPairSide.Left;
        if (_explicitEndpointKindSide != cloudSide) return null;
        if (!string.IsNullOrWhiteSpace(session.PickerFor(cloudSide).ConnectionId)) return null;
        return ProviderPresentation.ForEndpointKind(cloud);
    }

    /// <summary>The cloud endpoint kind this session names, or null when neither side is a cloud endpoint.</summary>
    private static SyncEndpointKind? SessionCloudKind(SyncPairEditorSession session)
    {
        var right = session.KindOf(SyncPairSide.Right);
        if (right != SyncEndpointKind.Local) return right;
        var left = session.KindOf(SyncPairSide.Left);
        return left != SyncEndpointKind.Local ? left : null;
    }

    /// <summary>
    /// UI354-PROVIDER1: the retained two-tree panel may present provider data only while the provider and
    /// account context it was loaded for still matches the explicit session. On a kind or account
    /// mismatch the loaded provider data and any stale selection are cleared, the panel states the chosen
    /// provider's own prompts, and the emptied tree can no longer apply a folder to this draft. Nothing
    /// here contacts a provider: it only drops data that no longer describes the draft.
    /// </summary>
    private void ReconcileRetainedCloudContext(SyncPairEditorSession session)
    {
        if (SessionCloudKind(session) is not { } cloud) return;
        // Only an explicit kind choice for the account-less cloud side may invalidate the retained panel;
        // every other draft keeps its account authority and its loaded provider data untouched.
        var cloudSideForPanel = cloud == session.KindOf(SyncPairSide.Right) ? SyncPairSide.Right : SyncPairSide.Left;
        if (_explicitEndpointKindSide != cloudSideForPanel) return;
        var chosen = ProviderPresentation.ForEndpointKind(cloud);
        // The panel is stale only when this side's own account does not belong to the chosen kind (a kind
        // or account change), or when no account is chosen yet and the loaded panel names another
        // provider. A matching retained side is left exactly as it was.
        var side = cloud == session.KindOf(SyncPairSide.Right) ? SyncPairSide.Right : SyncPairSide.Left;
        var accountProvider = ProviderForConnectionId(session.PickerFor(side).ConnectionId);
        if (accountProvider is not null)
        {
            if (string.Equals(accountProvider, chosen.Key, StringComparison.Ordinal)) return;
            ClearStaleRetainedCloudContext(chosen);
            return;
        }
        if (string.Equals(ProviderForConnectionId(_pairDraftConnectionId ?? _selectedConnectionId), chosen.Key,
                StringComparison.Ordinal)) return;
        ClearStaleRetainedCloudContext(chosen);
    }

    /// <summary>Drops retained provider data and prompts for the provider this draft actually names.</summary>
    private void ClearStaleRetainedCloudContext(ProviderPresentation chosen)
    {
        ClearRetainedCloudFolderSelection();
        // Emptying the loaded tree drops its selection as well, so no retained row can be acted on.
        if (DropboxRootNodes is not null) DropboxRootNodes.Clear();
        if (DropboxRootBrowserActions is not null) DropboxRootBrowserActions.Visibility = Visibility.Collapsed;
        if (DropboxRootBrowserStatus is not null)
            DropboxRootBrowserStatus.Text = "Choose a " + chosen.Name + " account for this side, then choose its " +
                chosen.Name + " folder.";
    }

    /// <summary>
    /// UI354-DRAFT1: one UI presentation formatter over the shared validation object. Core keeps its exact
    /// refusal codes and Describe text, so the CLI and every machine consumer are unchanged; only this hint
    /// is friendly, de-duplicated and free of enum or stable-connection-id vocabulary. Identical messages
    /// collapse to one, distinct non-missing refusals keep their existing safe reason.
    /// </summary>
    private static string EndpointValidationHint(SyncEndpointValidation validation)
    {
        var messages = new List<string>();
        foreach (var refusal in validation.Refusals)
        {
            var message = refusal.Error switch
            {
                SyncEndpointError.MissingLocalPath => "Choose a folder on this computer.",
                SyncEndpointError.MissingCloudAccount => "Choose an account for each cloud side.",
                SyncEndpointError.MissingCloudFolder => "Choose a folder for each cloud side.",
                _ => refusal.Reason
            };
            if (!string.IsNullOrWhiteSpace(message) && !messages.Contains(message, StringComparer.Ordinal))
                messages.Add(message);
        }
        return string.Join(" ", messages);
    }

    /// <summary>
    /// UI354-DRAFT2: the one layout rule for the explicit endpoint editor. While the explicit session owns
    /// the draft, each endpoint's own account chooser is the only visible account selection: the legacy
    /// Connection panel stays alive for the compatibility save/browse bridge but is collapsed, and Pair
    /// Name spans the freed width. Without an explicit session the legacy layout is untouched. The existing
    /// responsive layout calls this same rule, so resizing cannot restore the panel.
    /// </summary>
    private void ApplyExplicitEditorLayout(bool explicitSessionActive)
    {
        if (SyncTypePanel is null || PairNameBox is null) return;
        SyncTypePanel.Visibility = explicitSessionActive ? Visibility.Collapsed : Visibility.Visible;
        if (PairNameBox.Parent is FrameworkElement namePanel)
            System.Windows.Controls.Grid.SetColumnSpan(namePanel, explicitSessionActive ? 2 : 1);
    }

    /// <summary>Shows one side from that side's own picker, with no reference to the other side.</summary>
    private void PresentEndpointSide(SyncPairEditorSession session, SyncPairSide side, ContentControl kindIcon,
        TextBlock summary,
        ComboBox accountBox, TextBox pathBox, Button browseButton)
    {
        var kind = session.KindOf(side);
        var picker = session.PickerFor(side);
        // UI354-ICON1: both sides apply the canonical mark through the one shared helper, which owns the
        // fixed 28x28 DIP host and the uniform fit, so an intrinsic provider size can never inflate it.
        if (kindIcon is not null) ProviderPresentation.ApplyEndpointKindMark(kindIcon, kind);
        if (accountBox is not null)
        {
            accountBox.DisplayMemberPath = nameof(EndpointAccountChoice.Label);
            accountBox.SelectedValuePath = nameof(EndpointAccountChoice.ConnectionId);
            var offered = EndpointAccountChoicesFor(kind);
            accountBox.ItemsSource = offered;
            accountBox.SelectedValue = picker.ConnectionId;
            accountBox.Visibility = picker.IsCloud ? Visibility.Visible : Visibility.Collapsed;
            accountBox.ToolTip = picker.IsCloud
                ? "Choose which " + SyncEndpointPresentation.ShortLabel(kind) +
                  " account holds this endpoint's folder. Existing pairs keep their original account."
                : null;
        }
        if (pathBox is not null)
        {
            pathBox.Text = SelectionText(session, side);
            pathBox.ToolTip = SyncEndpointPresentation.EndpointDisplay(session.RecordOf(side));
            // An empty path is not clickable: there is no folder to open yet.
            pathBox.IsHitTestVisible = picker.CanOpenSelectedFolder;
        }
        if (browseButton is not null)
        {
            browseButton.IsEnabled = !picker.IsCloud || picker.CanBrowseCloud;
            browseButton.ToolTip = picker.IsCloud
                ? picker.CanBrowseCloud
                    ? "Choose the exact " + SyncEndpointPresentation.ShortLabel(kind) +
                      " folder to pair inside the account chosen for this side."
                    : picker.CloudBrowseRefusal
                : "Choose the folder on this computer for this endpoint.";
            ToolTipService.SetShowOnDisabled(browseButton, true);
            System.Windows.Automation.AutomationProperties.SetHelpText(browseButton, browseButton.ToolTip as string ?? "");
        }
        if (summary is null) return;
        summary.Text = "Choose the " + SyncEndpointPresentation.ShortLabel(kind) + " endpoint. " +
            SyncEndpointPresentation.FolderControlHint(kind) + " Chosen: " +
            SyncEndpointPresentation.EndpointDisplay(session.RecordOf(side)) + ".";
    }

    private void ClearEndpointStrip()
    {
        _refreshingEndpointStrip = true;
        try
        {
            LeftEndpointKindBox.SelectedIndex = -1;
            RightEndpointKindBox.SelectedIndex = -1;
            if (LeftEndpointSummary is not null) LeftEndpointSummary.Text = string.Empty;
            if (RightEndpointSummary is not null) RightEndpointSummary.Text = string.Empty;
            if (LeftEndpointPathBox is not null) { LeftEndpointPathBox.Clear(); LeftEndpointPathBox.IsHitTestVisible = false; }
            if (RightEndpointPathBox is not null) { RightEndpointPathBox.Clear(); RightEndpointPathBox.IsHitTestVisible = false; }
            if (LeftEndpointAccountBox is not null) { LeftEndpointAccountBox.ItemsSource = null; LeftEndpointAccountBox.Visibility = Visibility.Collapsed; }
            if (RightEndpointAccountBox is not null) { RightEndpointAccountBox.ItemsSource = null; RightEndpointAccountBox.Visibility = Visibility.Collapsed; }
        }
        finally { _refreshingEndpointStrip = false; }
    }

    /// <summary>The chosen path as the side's own control shows it, or empty when nothing is chosen yet.</summary>
    private static string SelectionText(SyncPairEditorSession session, SyncPairSide side)
    {
        var draft = session.Draft(side);
        if (draft.IsLocal) return draft.LocalPath ?? string.Empty;
        return draft.FolderPath ?? draft.FolderId ?? string.Empty;
    }

    /// <summary>
    /// The accounts offered for one kind, provider-filtered and labelled with the established compact
    /// account form. Two accounts that share a display name stay distinct because the connection id and
    /// the email differ.
    /// </summary>
    private IReadOnlyList<EndpointAccountChoice> EndpointAccountChoicesFor(SyncEndpointKind kind)
    {
        var records = _authorityHost.LoadProviderConnections();
        var verified = new List<SyncVerifiedAccountBinding>();
        foreach (var record in records)
            verified.Add(new SyncVerifiedAccountBinding(record.Provider, record.ConnectionId,
                record.RootNamespaceId, IsLegacyPrimaryConnection(record)));
        var choices = new List<EndpointAccountChoice>();
        foreach (var binding in SyncEndpointAccountChoices.ForKind(kind, verified))
        {
            var record = records.FirstOrDefault(item =>
                string.Equals(item.ConnectionId, binding.ConnectionId, StringComparison.Ordinal));
            var label = record is null
                ? binding.ConnectionId
                : SyncEndpointPresentation.AccountLabel(SyncEndpointPresentation.ShortLabel(kind),
                    record.DisplayName, record.Email);
            choices.Add(new EndpointAccountChoice(binding, label));
        }
        return choices;
    }

    private bool IsLegacyPrimaryConnection(ProviderConnectionRecord record)
    {
        if (string.Equals(record.Provider, SyncProviders.GoogleDrive, StringComparison.Ordinal))
            return _authorityHost.LoadGoogleDriveConnections().FirstOrDefault() is { } google &&
                   string.Equals(google.ConnectionId, record.ConnectionId, StringComparison.Ordinal);
        return _authorityHost.LoadDropboxConnection() is { } dropbox &&
               string.Equals(dropbox.ConnectionId, record.ConnectionId, StringComparison.Ordinal);
    }

    private void PresentEndpointValidation(SyncPairEditorSession session)
    {
        if (EndpointDraft() is not { } draft)
        {
            PairEndpointValidationText.Text = string.Empty;
            return;
        }
        if (draft.UnsupportedBindingReason is { } unsupported)
        {
            PairEndpointValidationText.Text = unsupported;
            return;
        }
        if (!draft.Validation.IsValid)
        {
            PairEndpointValidationText.Text = "This pair cannot be saved yet: " + EndpointValidationHint(draft.Validation);
            return;
        }
        PairEndpointValidationText.Text = draft.Intent == SyncPairEditIntent.Rebind
            ? "Both endpoints are valid. Saving will rebind this pair: its stored endpoint identity changes, " +
              "so it must complete one effect-free first inventory that you review before any transfer. " +
              "Renaming the pair does not do this."
            : "Both endpoints are valid. " + SyncEndpointPresentation.DirectionLabel(draft.Direction) + ": " +
              SyncEndpointPresentation.DirectionDescription(draft.Direction);
    }

    /// <summary>
    /// The one validated draft extraction this editor exposes: both ordered endpoints, the direction and
    /// exactly why saving is or is not possible. The window's save availability and the E3/E4 integration
    /// owner both read this and nothing else.
    /// </summary>
    internal SyncPairDraft? EndpointDraft()
    {
        if (_endpointSession is not { } session) return null;
        var existing = RetainedPairForEditor();
        return session.ExtractDraftWithCompletePolicy(existing, RetainedEndpointPairs(),
            RetainedPairDefinitions(), endpoints => CompleteEndpointCandidate(endpoints, existing),
            deferOpaqueHierarchyVerification: true);
    }

    /// <summary>
    /// The complete retained pair definitions the one partition policy consumes. The pair this editor is
    /// open on is excluded: it is the draft's own baseline, not another pair's overlap.
    /// </summary>
    private IReadOnlyList<SyncPair> RetainedPairDefinitions()
    {
        var retained = new List<SyncPair>();
        foreach (var pair in _pairSet.Pairs)
        {
            if (RetainedPairForEditor() is { } editing &&
                string.Equals(pair.Id, editing.Id, StringComparison.Ordinal)) continue;
            retained.Add(pair);
        }
        return retained;
    }

    /// <summary>
    /// The complete candidate definition this draft would save, derived from the SAME sources the actual
    /// save uses: the still-matching retained tree plan (never a stale, retained or borrowed plan) and the
    /// current ignore rules. The draft-availability decision and the real save both consume this one
    /// derivation, so the policy that admits the draft is exactly the policy that is bound.
    /// </summary>
    private SyncPair? CompleteEndpointCandidate(SyncPairEndpoints endpoints, SyncPair? existing)
    {
        try
        {
            var plan = MatchingDraftBindingPlan(endpoints);
            if (_globalDraftPlan is not null && plan is null) return null;
            var name = PairNameBox.Text.Trim();
            var candidate = SyncEndpointPairDraft.Create(endpoints,
                name.Length == 0 ? "Pending pair name" : name, existing);
            if (plan is not null)
                candidate = candidate with
                {
                    SelectionRules = plan.Rules, SelectionDefaultIncluded = plan.DefaultIncluded
                };
            else if (existing is null)
                candidate = candidate with { SelectionRules = [], SelectionDefaultIncluded = true };
            candidate = candidate with { IgnoreRules = _draftIgnoreRules };
            return ApplyDraftEditorPolicy(candidate, endpoints, existing);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
            or SyncEndpointRuntimeRefusedException)
        {
            // An underivable candidate never waives a retained overlap; the strict draft validation owns
            // the refusal and reports the actionable reason.
            return null;
        }
    }

    /// <summary>
    /// Applies the ordinary, prompt-free editor policy that the availability decision and the real save
    /// share: enabled, priority, delete propagation, retention, the default storage choice and the
    /// directory-storage rules. Every value is a pure control read, so evaluating it while the user is
    /// still editing has no prompt and no side effect. The cloud-files confirmation outcome and the
    /// explicitly selected Recovery folder stay in the save, because they depend on a user answer.
    /// </summary>
    private SyncPair ApplyDraftEditorPolicy(SyncPair candidate, SyncPairEndpoints endpoints, SyncPair? existing)
    {
        var localOnly = endpoints.Left.IsLocal && endpoints.Right.IsLocal;
        var cloudOnly = !endpoints.Left.IsLocal && !endpoints.Right.IsLocal;
        var supportsDeviceStorage = !localOnly && !cloudOnly;
        var requestedFilesOnDemand = PairFilesOnDemandBox.IsChecked == true;
        var priority = int.TryParse(PairPriorityBox.Text, out var parsed) ? parsed : existing?.Priority ?? 0;
        return candidate with
        {
            Enabled = PairEnabledBox.IsChecked == true,
            Priority = priority,
            PropagateDeletions = PairDeletionBox.IsChecked == true,
            RecoveryRetentionDays = SelectedRecoveryRetentionDays(),
            DefaultStorage = cloudOnly ? null : requestedFilesOnDemand
                ? SyncDirectoryStorage.CloudOnly : SyncDirectoryStorage.AlwaysAvailable,
            DirectoryStorageRules = supportsDeviceStorage ? _draftDirectoryStorageRules : null,
            DirectoryStorageApplyPaths = null,
            FileStorageRules = null
        };
    }

    /// <summary>
    /// The endpoints already retained by the other pairs, so a draft cannot reuse or nest an endpoint
    /// another pair owns. The pair this editor is open on is excluded: it is the draft's own baseline.
    /// </summary>
    private IReadOnlyList<SyncPairEndpoints> RetainedEndpointPairs()
    {
        var retained = new List<SyncPairEndpoints>();
        foreach (var pair in _pairSet.Pairs)
        {
            if (RetainedPairForEditor() is { } editing &&
                string.Equals(pair.Id, editing.Id, StringComparison.Ordinal)) continue;
            if (SyncPairEditorSession.RetainedEndpoints(pair) is { } endpoints) retained.Add(endpoints);
        }
        return retained;
    }

    /// <summary>True while the chosen ordered combination is one this build can actually bind.</summary>
    internal bool EndpointCombinationSupported() => _endpointSession is not { } session ||
        SyncEndpointPresentation.BindingSupported(session.KindOf(SyncPairSide.Left),
            session.KindOf(SyncPairSide.Right));

    /// <summary>The exact reason the chosen combination cannot be bound yet, or null when it can.</summary>
    internal string? EndpointCombinationReason() => _endpointSession is not { } session
        ? null
        : SyncEndpointPresentation.BindingSupportReason(session.KindOf(SyncPairSide.Left),
            session.KindOf(SyncPairSide.Right));

    /// <summary>
    /// True when saving would change either side's endpoint identity and must therefore re-enter the
    /// existing first-sync safeguard. A rename, a direction change or a policy change never does, and an
    /// incomplete draft is left to the ordinary save validation.
    /// </summary>
    internal bool EndpointRebindRequiresReview()
    {
        if (_editingNewPair || _editingPairRootId is null) return false;
        return EndpointDraft() is { Endpoints: not null, Intent: SyncPairEditIntent.Rebind };
    }

    /// <summary>
    /// Routes the folder the user ticked on this computer to the endpoint side whose Browse command armed
    /// this tree. The tree is the existing one; arming a side is what binds its selection to that side.
    /// </summary>
    private void ApplyLocalEndpointBrowse(string? localPath)
    {
        if (_endpointSession is not { } session) return;
        var side = _localEndpointBrowseSide ?? SyncPairSide.Left;
        var picker = session.PickerFor(side);
        if (picker.IsCloud) return;
        if (string.Equals(session.Draft(side).LocalPath, localPath, StringComparison.OrdinalIgnoreCase)) return;
        var outcome = picker.ApplyLocalFolder(picker.BeginPick(), localPath);
        if (outcome.State == SyncEndpointPickerState.Refused) ShowStatus(outcome.Reason);
    }

    private void PairEndpointPath_Click(object sender, MouseButtonEventArgs e)
    {
        if (_endpointSession is not { } session) return;
        var side = EndpointSideOf(sender);
        // An empty path is not clickable, so there is nothing to open and nothing to report.
        if (!session.PickerFor(side).CanOpenSelectedFolder) return;
        var draft = session.Draft(side);
        if (draft.LocalPath is not { Length: > 0 } path) return;
        if (!Directory.Exists(path))
        {
            ShowStatus("The chosen folder on this computer is not available right now: " + path);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
    }

    private async void PairEndpointBrowse_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        if (_endpointSession is not { } session) return;
        var side = EndpointSideOf(sender);
        var picker = session.PickerFor(side);
        if (!picker.IsCloud)
        {
            var localTicket = picker.BeginPick();
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose the " + SyncEndpointPresentation.SideLabel(side).ToLowerInvariant() + " endpoint folder",
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true || !IsCurrentEndpointPick(localTicket)) return;
            var outcome = picker.ApplyLocalFolder(localTicket, dialog.FolderName);
            if (outcome.Applied && side == SyncPairSide.Left) LocalRootBox.Text = dialog.FolderName;
            RefreshPairEndpointStrip();
            UpdatePairSaveAvailability();
            ShowStatus(outcome.Reason);
            return;
        }
        if (!picker.CanBrowseCloud)
        {
            ShowStatus(picker.CloudBrowseRefusal ?? "Choose this side's account first.");
            return;
        }
        var ticket = picker.BeginPick();
        try
        {
            var transport = PairFolderBrowserFactoryForTest is null
                ? await _authorityHost.CreateFolderBrowserAsync(connectionId: picker.ConnectionId)
                : await PairFolderBrowserFactoryForTest(CancellationToken.None);
            if (!IsCurrentEndpointPick(ticket))
            {
                ShowStatus(StaleEndpointPickMessage);
                return;
            }
            var browser = new DropboxFolderBrowserWindow(transport,
                EndpointBrowserAccountLabel(picker.Kind, picker.ConnectionId)) { Owner = this };
            // The modal is the only thing the fixture seam replaces; a null seam opens the real window and
            // reads the real selection exactly as before.
            var chosen = PairEndpointFolderBrowserDialogForTest is null
                ? browser.ShowDialog() == true ? browser.SelectedFolder : null
                : PairEndpointFolderBrowserDialogForTest(browser);
            if (chosen is not { } folder) return;
            ApplyEndpointFolderChoice(session, side, ticket, folder);
        }
        catch (Exception error)
        {
            ShowStatus("The folder browser could not be opened: " + error.Message);
        }
    }

    /// <summary>
    /// UI398-3: the ONE display-only resolver for an EXACT endpoint connection id. It is the resolution
    /// the folder-browser subtitle has always used - the connection-to-label map first, then the retained
    /// binding through the shared account form - generalised so the Activity endpoint sidebar, the
    /// expanded details and the endpoint action captions all name the account of the connection id they
    /// actually recorded. Nothing here reads the current or primary account, the selected pair or the
    /// provider of another endpoint.
    /// A missing or unregistered identity is stated as missing instead of resolved. That guard is what
    /// keeps a null from reaching the host: LoadAccountBinding(null) answers with the primary account, so
    /// looking a missing identity up would present a different account's label as this endpoint's own.
    /// The connection id itself is never displayed or used as a label: it keeps its existing transport and
    /// authority role (CreateFolderBrowserAsync above and every authority decision).
    /// </summary>
    internal string EndpointAccountDisplayLabel(string? connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return AccountNotRecordedLabel;
        if (_accountLabels.TryGetValue(connectionId, out var known) && !string.IsNullOrWhiteSpace(known))
            return known;
        return _authorityHost.LoadAccountBinding(connectionId) is { } retained
            ? SyncEndpointPresentation.AccountLabel(retained.ProviderName, retained.DisplayName, retained.Email)
            : AccountUnavailableLabel;
    }

    /// <summary>The exact wording a surface uses when the endpoint recorded no account at all.</summary>
    internal const string AccountNotRecordedLabel = "Account not recorded";

    /// <summary>The exact wording a surface uses when the endpoint recorded an identity nothing resolves.</summary>
    internal const string AccountUnavailableLabel = "Account unavailable";

    /// <summary>
    /// UI397_A: the folder-browser subtitle names the exact account this side browses through the shared
    /// resolver above. The identity is looked up by this picker's own connection id, so another account's
    /// label can never be shown, and the shared form's own provider-shaped fallback keeps the subtitle
    /// readable when no identity is retained, which is why this window restates the resolver's neutral
    /// wording rather than showing it here.
    /// </summary>
    private string EndpointBrowserAccountLabel(SyncEndpointKind kind, string? connectionId)
    {
        var resolved = EndpointAccountDisplayLabel(connectionId);
        return string.Equals(resolved, AccountNotRecordedLabel, StringComparison.Ordinal) ||
               string.Equals(resolved, AccountUnavailableLabel, StringComparison.Ordinal)
            ? SyncEndpointPresentation.AccountLabel(SyncEndpointPresentation.ShortLabel(kind), null, null)
            : resolved;
    }

    private const string StaleEndpointPickMessage =
        "This endpoint changed after the folder was chosen, so the older choice was not applied.";

    private bool IsCurrentEndpointPick(SyncEndpointPickerTicket ticket) =>
        _endpointSession is { } session && session.Generation == ticket.EditorGeneration &&
        session.IsCurrentPick(ticket);

    private void ApplyEndpointFolderChoice(SyncPairEditorSession session, SyncPairSide side,
        SyncEndpointPickerTicket ticket, DropboxFolderEntry folder)
    {
        if (!ReferenceEquals(_endpointSession, session) || !session.IsCurrentPick(ticket))
        {
            ShowStatus(StaleEndpointPickMessage);
            return;
        }
        // UI354-PROVIDER1: a folder identity belongs to the account and provider that issued it. A result
        // whose account no longer matches this side's chosen kind is refused rather than carried into the
        // draft, so an older tree action can never apply a former provider's folder.
        var picker = session.PickerFor(side);
        var accountProvider = ProviderForConnectionId(picker.ConnectionId);
        if (picker.IsCloud && accountProvider is not null &&
            !string.Equals(accountProvider, ProviderPresentation.ForEndpointKind(picker.Kind).Key, StringComparison.Ordinal))
        {
            ShowStatus("This side is the " + SyncEndpointPresentation.ShortLabel(picker.Kind) +
                " endpoint, so a " + SyncProviders.DisplayName(accountProvider) +
                " folder cannot be applied to it. Choose a matching account for this side first.");
            return;
        }
        var outcome = session.PickerFor(side).ApplyCloudFolder(ticket, folder);
        if (!outcome.Applied)
        {
            ShowStatus(outcome.Reason);
            return;
        }
        if (side == SyncPairSide.Right) KeepRetainedCloudFolderInStep(folder);
        RefreshPairEndpointStrip();
        UpdatePairSaveAvailability();
        ShowStatus(outcome.Reason);
    }

    /// <summary>
    /// Keeps the retained ordered shape's own account selection in step with the right endpoint's choice,
    /// so a pair saved through the existing authority path cannot record an account this editor no longer
    /// shows. The account picker's own handler is suppressed; this method performs the clearing itself.
    /// </summary>
    private void KeepRetainedCloudAccountInStep(string connectionId)
    {
        if (_pairAccountPicker is not null &&
            !string.Equals(_pairAccountPicker.SelectedValue as string, connectionId, StringComparison.Ordinal))
        {
            var wasUpdating = _updatingAccountPickers;
            _updatingAccountPickers = true;
            try { _pairAccountPicker.SelectedValue = connectionId; }
            finally { _updatingAccountPickers = wasUpdating; }
        }
        ClearRetainedCloudFolderSelection();
    }

    private void KeepRetainedCloudFolderInStep(DropboxFolderEntry folder)
    {
        _selectedDropboxFolder = folder;
        _draftBrowseDropboxRoot = folder;
        DropboxRootBox.Text = folder.Path;
        DropboxFolderIdText.Text = folder.FolderId;
    }

    /// <summary>
    /// A folder identity belongs to the account that issued it, so switching the account clears this
    /// side's retained cloud folder instead of carrying a folder id across accounts.
    /// </summary>
    private void ClearRetainedCloudFolderSelection()
    {
        _selectedDropboxFolder = null;
        _draftBrowseDropboxRoot = null;
        _selectedDropboxRootNode = null;
        DropboxRootBox.Clear();
        DropboxFolderIdText.Text = "Not selected";
    }
}
