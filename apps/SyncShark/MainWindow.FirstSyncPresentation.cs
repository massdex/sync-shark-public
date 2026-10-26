using System.Windows.Controls;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    // EPS4A: an opened review records the exact ordered endpoints it was built from, so an
    // either-side provider, account or root rebind invalidates this pair's plan and draft choices
    // even when the legacy single connection id is unchanged. The legacy id is retained for
    // compatibility and is never the only evidence of identity.
    private sealed record FirstSyncReviewContext(string PairId, EndpointReviewPresentation Endpoints,
        string ConnectionId, string LocalGeneration, string CloudGeneration, int QueryVersion)
    {
        internal string LeftIdentity => Identity(Endpoints.Context.Left);
        internal string RightIdentity => Identity(Endpoints.Context.Right);

        // One ordered side's exact identity: the local folder path with its recorded volume and root
        // identity, or the cloud kind, provider, account, namespace and folder identity. Nothing here
        // reads a currently selected account, so missing evidence can never be filled in by another.
        internal static string Identity(SyncEndpointContextSide side) => side.IsLocal
            ? string.Join('\u001f', "local", side.LocalPath, side.LocalVolumeSerialNumber, side.LocalRootFileId)
            : string.Join('\u001f', "cloud", side.Kind, side.Provider, side.ConnectionId, side.NamespaceId,
                side.FolderId, side.FolderPath);
    }

    private FirstSyncReviewContext? _firstSyncReviewContext;
    private readonly Dictionary<string, (string? Local, string? Cloud)> _firstSyncChoiceGenerations = [];
    private int _retainedProblemRefreshGeneration;

    private async Task RefreshRetainedRunProblemsAsync()
    {
        if (!_authorityHost.AuthorityStateExists) return;
        var generation = ++_retainedProblemRefreshGeneration;
        var retained = await Task.Run(async () => (
            FirstSync: await _authorityHost.LoadRetainedFirstSyncProblemsAsync(),
            Commands: await _authorityHost.LoadRetainedCommandProblemProjectionAsync()));
        if (_closing || generation != _retainedProblemRefreshGeneration) return;
        _firstSyncProblemsByPair = retained.FirstSync.GroupBy(item => item.PairId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.ReportedUtc).First(),
                StringComparer.Ordinal);
        _commandProblemsByPair = retained.Commands.Problems.ToDictionary(item => item.PairId, StringComparer.Ordinal);
        // A CLI/background success has no Completed callback in this window.
        // Retire only a callback failure older than that SAME pair's proven
        // successful run. An old success cannot clear a newer failure; durable
        // operations, conflicts, endpoint and monitor health stay independent.
        foreach (var failure in _runFailureReportedUtcByPair.ToArray())
        {
            if (!retained.Commands.SuccessfulRunStartedUtcByPair.TryGetValue(failure.Key, out var succeeded) ||
                succeeded <= failure.Value) continue;
            _runFailureReportedUtcByPair.Remove(failure.Key);
            _runFailurePairIds.Remove(failure.Key);
            _runAttentionReasonByPair.Remove(failure.Key);
        }
        if (_workspaceCommandFailure is { } transient)
        {
            var unresolved = transient.PairIds.Where(id =>
                !retained.Commands.SuccessfulRunStartedUtcByPair.TryGetValue(id, out var succeeded) ||
                succeeded <= transient.ReportedUtc).ToHashSet(StringComparer.Ordinal);
            _workspaceCommandFailure = unresolved.Count == 0 ? null :
                (transient.Detail, unresolved, transient.ReportedUtc);
        }
        PresentRetainedFirstSyncProblem();
        RefreshFullApp();
        RefreshWorkspace();
    }

    internal async Task<IReadOnlyList<SyncCliAttentionItemData>> LoadCliAttentionItemsAsync()
    {
        // A running GUI is not permission to return a cached earlier failure.
        // This uses the same retained projection as the visible attention rows.
        await RefreshRetainedRunProblemsAsync();
        return CliAttentionItems;
    }

    private bool IsCurrentFirstSyncContext(FirstSyncReviewContext context) =>
        ReferenceEquals(_firstSyncReviewContext, context) && !_loadingBootstrap &&
        context.QueryVersion == _bootstrapQueryVersion &&
        string.Equals(BootstrapPairBox.SelectedValue as string, context.PairId, StringComparison.Ordinal) &&
        ScopeMatches(context.PairId) && SameCapturedEndpoints(context.PairId, context.ConnectionId,
            context.LeftIdentity, context.RightIdentity);

    // Both ordered endpoint identities are compared, not only the legacy connection id: a
    // local-to-local pair or a second account of the same provider keeps the legacy field's shape
    // while its real endpoints change underneath. A pair whose endpoints can no longer be described
    // never matches a captured plan.
    private bool SameCapturedEndpoints(string pairId, string? connectionId, string? leftIdentity,
        string? rightIdentity)
    {
        var pair = _pairSet.Pairs.FirstOrDefault(candidate => candidate.Id == pairId);
        if (pair is null) return false;
        if (!string.Equals(pair.ConnectionId, connectionId, StringComparison.Ordinal)) return false;
        try
        {
            var current = EndpointReviewPresentation.For(pair);
            return string.Equals(FirstSyncReviewContext.Identity(current.Context.Left), leftIdentity,
                       StringComparison.Ordinal) &&
                   string.Equals(FirstSyncReviewContext.Identity(current.Context.Right), rightIdentity,
                       StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void InvalidateFirstSyncReview(string message)
    {
        ++_bootstrapQueryVersion;
        _firstSyncReviewContext = null;
        _bootstrapInventoryComplete = false;
        _bootstrapHasMore = false;
        _loadingBootstrap = false;
        BootstrapRows.Clear();
        if (ApproveBootstrapButton is null || LoadMoreBootstrapButton is null || BootstrapSummary is null) return;
        ApproveBootstrapButton.IsEnabled = false;
        LoadMoreBootstrapButton.IsEnabled = false;
        BootstrapSummary.Text = message;
    }

    private void OpenFirstSyncReview(string pairId)
    {
        if (!_pairSet.Pairs.Any(pair => pair.Id == pairId)) return;
        IncludeAttentionPair(pairId);
        RefreshAttentionPairChoices();
        _updatingAttentionPairs = true;
        try { BootstrapPairBox.SelectedValue = pairId; }
        finally { _updatingAttentionPairs = false; }
        InvalidateFirstSyncReview("Loading this pair's first-sync plan…");
        NavigateTo("Bootstrap");
    }


    // ENG4: the retained typed first-sync problem with its exact run, stated on the First sync page itself, so a
    // problem is never an unexplained red state and its exact command is one click away.
    private void PresentRetainedFirstSyncProblem()
    {
        if (BootstrapRetainedProblem is null || BootstrapRetainedProblemLink is null) return;
        if (RepairBootstrapMappingsButton is not null)
            RepairBootstrapMappingsButton.Visibility = System.Windows.Visibility.Collapsed;
        var pairId = BootstrapPairBox?.SelectedValue as string;
        if (pairId is null || !_firstSyncProblemsByPair.TryGetValue(pairId, out var problem) ||
            problem.CommandId.Length == 0)
        {
            BootstrapRetainedProblem.Visibility = System.Windows.Visibility.Collapsed;
            BootstrapRetainedProblemLink.Visibility = System.Windows.Visibility.Collapsed;
            BootstrapRetainedProblemLink.Tag = null;
            return;
        }
        BootstrapRetainedProblem.Text = WorkspaceVisuals.ProblemPreview(problem.Message) + " " + problem.NextAction + " Run " +
            problem.CommandId + ".";
        BootstrapRetainedProblem.Visibility = System.Windows.Visibility.Visible;
        BootstrapRetainedProblemLink.Visibility = System.Windows.Visibility.Visible;
        BootstrapRetainedProblemLink.Tag = problem;
        if (RepairBootstrapMappingsButton is not null && problem.Code == "first_sync_identity_recovery_required")
        {
            RepairBootstrapMappingsButton.Visibility = System.Windows.Visibility.Visible;
            RepairBootstrapMappingsButton.IsEnabled = !UiOperationBusy;
        }
    }

    private void OpenRetainedFirstSyncProblem_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (BootstrapRetainedProblemLink?.Tag is SyncFirstSyncProblem problem) OpenFirstSyncProblemRun(problem);
    }

    private async void RepairBootstrapMappings_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        using var feedback = ActionFeedback.Begin(sender);
        if (!feedback.Entered || UiOperationBusy || BootstrapPairBox.SelectedValue is not string pairId ||
            !ScopeMatches(pairId)) return;
        // SS-D01: this queued mapping repair owns one cancellation token, created before the busy state is
        // exposed and before the host is called, so Stop cancels this exact invocation instead of passing None.
        var repairCancellation = BeginPendingRunCancellation();
        _runActive = true;
        ResetRunPresentation("Repairing copied file mappings", "Verifying retained history and both current folders. No new transfer has started.");
        SetRunControls();
        RepairBootstrapMappingsButton.IsEnabled = false;
        try
        {
            if (QueuedInvocationHoldForTest is { } holdQueued)
                await holdQueued("Copied-file mapping recovery", repairCancellation.Token);
            var result = await Task.Run(() => _authorityHost.RepairCompletedCopyMovesAsync(pairId,
                cancellationToken: repairCancellation.Token,
                onProgress: progress => PresentBatchProgressAsync(progress, "First sync", recoveryOnly: false)));
            if (BootstrapPairBox.SelectedValue as string != pairId) return;
            var preserved = result.Items.Count(item => item.Status == "preserved");
            if (preserved == 0)
            {
                if (!await RefreshBootstrapReviewAsync())
                {
                    SetFirstSyncFeedback("The copied-file mappings were verified and retained, but the review could not load. Refresh First sync before confirming any file choices.");
                    return;
                }
                await ReconcileBootstrapActionOverridesAsync(pairId);
                SetFirstSyncFeedback("Copied-file mappings were verified. Review the current plan before confirming any new sync choices.");
            }
            else SetFirstSyncFeedback($"{preserved} copied-file mappings still need attention. Both files were preserved; open run {result.RunId} for each exact reason.");
        }
        catch (OperationCanceledException) when (repairCancellation.IsCancellationRequested)
        {
            // SS-D01: the user's stop cancelled this queued repair, so no mapping was changed and no
            // failure is reported in place of the stop that actually ended it.
            SetFirstSyncFeedback("Copied-file mapping recovery was stopped before any change. Both files were preserved; refresh First sync for current state.");
        }
        catch (Exception error)
        {
            SetFirstSyncFeedback("Copied-file mapping recovery could not complete: " + SyncActivityText.Failure(error));
        }
        finally
        {
            EndPendingRunCancellation(repairCancellation);
            await FinishRunPresentationAsync("Copied-file mapping recovery",
                () => RefreshAuthorityProjectionsAsync(requireSuccess: true), SetFirstSyncFeedback,
                BootstrapChoiceSummary.Text);
            PresentRetainedFirstSyncProblem();
        }
    }


    private void SetFirstSyncFeedback(string message)
    {
        BootstrapChoiceSummary.Text = message;
        ShowActionStatus(message);
    }

    // EPS4A: capture both ordered endpoints before a review page is requested. A pair whose declared
    // endpoints cannot be described is refused with an explicit reason instead of being presented from
    // whatever account happens to be selected, and the refusal touches only that pair.
    private bool TryCaptureEndpointReview(SyncPair pair, out EndpointReviewPresentation endpoints)
    {
        try
        {
            endpoints = EndpointReviewPresentation.For(pair);
            return true;
        }
        catch (Exception exception)
        {
            endpoints = null!;
            DiscardFirstSyncPlan(pair.Id,
                "This pair's two endpoints cannot be described from the saved folder pair, so its first-sync plan is refused rather than guessed: " +
                exception.Message);
            return false;
        }
    }

    // EPS4A: discarding one pair's plan never touches another pair's rows, generations or draft choices.
    private void DiscardFirstSyncPlan(string pairId, string message)
    {
        _firstSyncReviewContext = null;
        _bootstrapInventoryComplete = false;
        _bootstrapHasMore = false;
        _loadingBootstrap = false;
        foreach (var key in _bootstrapActionOverrides.Keys.Where(key => key.PairId == pairId).ToArray())
            _bootstrapActionOverrides.Remove(key);
        _firstSyncChoiceGenerations.Remove(pairId);
        if (!string.Equals(BootstrapPairBox.SelectedValue as string, pairId, StringComparison.Ordinal)) return;
        BootstrapRows.Clear();
        if (BootstrapSummary is not null) BootstrapSummary.Text = message;
        if (ApproveBootstrapButton is not null) ApproveBootstrapButton.IsEnabled = false;
        if (LoadMoreBootstrapButton is not null) LoadMoreBootstrapButton.IsEnabled = false;
    }

    // EPS4A: the group filters, the two bulk action pickers and the endpoint headings present the same
    // ordered endpoints the rows were built from. Every picker keeps its physical key on Tag and only
    // its displayed text changes, so no stored group or action value is renamed.
    private void ApplyFirstSyncEndpointLabels(EndpointReviewPresentation endpoints)
    {
        if (BootstrapEndpointSummary is not null)
            // R4: the ordered side is stated ONCE. The shared presentation supplies the side, the endpoint
            // kind and the endpoint's own location, so the line reads 'Left: This computer - <path>' rather
            // than repeating the ordered prefix that the section title already carries. Wording only.
            BootstrapEndpointSummary.Text = string.Join("   ⇄   ",
                Summary(endpoints.Context.Left), Summary(endpoints.Context.Right));

            static string Summary(SyncEndpointContextSide side) =>
                SyncEndpointPresentation.SideLabel(side.Side) + ": " +
                SyncEndpointPresentation.Choice(side.Kind).SectionTitle + " — " +
                SyncEndpointPresentation.HierarchyLocation(side);
        if (BootstrapGroupFilter is not null)
            foreach (var item in BootstrapGroupFilter.Items.OfType<ComboBoxItem>())
            {
                var key = (item.Tag ?? item.Content)?.ToString() ?? string.Empty;
                item.Content = GroupLabel(key, endpoints);
            }
        foreach (var box in new[] { BootstrapSelectedActionBox, BootstrapMatchingActionBox })
            if (box is not null)
                foreach (var item in box.Items.OfType<ComboBoxItem>())
                {
                    var value = (item.Tag ?? item.Content)?.ToString() ?? string.Empty;
                    item.Content = ActionLabel(value, endpoints);
                }
    }

    internal static string GroupLabel(string group, EndpointReviewPresentation? endpoints) => endpoints is null
        ? group
        : group switch
        {
            "Local only" => "Only on " + endpoints.LocalPortTitle,
            "Dropbox only" => "Only on " + endpoints.RemotePortTitle,
            SyncBootstrapActions.LocalUpdatedGroup => "Changed since sync on " + endpoints.LocalPortTitle,
            SyncBootstrapActions.RemoteUpdatedGroup => "Changed since sync on " + endpoints.RemotePortTitle,
            "Local newer" => "Newer on " + endpoints.LocalPortTitle,
            "Dropbox newer" => "Newer on " + endpoints.RemotePortTitle,
            "Identical" => "Same on both endpoints",
            _ => group
        };

    internal static string ActionLabel(string action, EndpointReviewPresentation? endpoints) => endpoints is null
        ? action
        : action switch
        {
            "Upload" => "Copy to " + endpoints.RemotePortTitle,
            "Download" => "Copy to " + endpoints.LocalPortTitle,
            "MoveLocal" => "Move on " + endpoints.LocalPortTitle,
            "MoveRemote" => "Move on " + endpoints.RemotePortTitle,
            "Recommended" => "Recommended (each row's own action)",
            _ => action
        };
}
