namespace Massdex.SyncShark.Engine.Tests;

/// <summary>Check name → the test files it runs (design section 10).</summary>
public static class CheckRegistry
{
    // Membership and placement share one catalog; literal All keys remain discoverable before the build.
    private static readonly (Action Run, int Shard)[] BudgetCatalog =
    [
        (Run.CallBudgetChecks.RunBudgetCommon, 4),
        (Run.CallBudgetChecks.RunBudgetPair, 1),
        (Run.CallBudgetChecks.RunBudgetWholeFolder, 2),
        (Run.CallBudgetChecks.RunBudgetDeepFolder, 3),
        (Run.CallBudgetChecks.RunBudgetManyFolders, 4),
        (Run.CallBudgetChecks.RunBudgetPartialFolder, 3),
        (Run.CallBudgetChecks.RunBudgetExclusion, 4),
        (Run.CallBudgetChecks.RunBudgetPlanSql, 1),
        (Run.CallBudgetChecks.RunBudgetAdverse, 2),
        (Run.CallBudgetChecks.RunBudgetComposedProvider, 1),
        (Executor.ActionExecutorChecks.FileSizedForgetCosts, 4)
    ];
    private static readonly Action[] BudgetChecks = ValidateBudget();

    static CheckRegistry()
    {
        foreach (var entry in BudgetCatalog)
            if (!All.TryGetValue("budget-" + entry.Shard, out var shard) || !shard.Contains(entry.Run))
                throw new InvalidOperationException("Budget entry is absent from its registered shard: " + entry.Run.Method.Name);
    }

    private static Action[] ValidateBudget()
    {
        if (BudgetCatalog.Select(entry => entry.Run.Method).Distinct().Count() != BudgetCatalog.Length)
            throw new InvalidOperationException("Duplicate budget catalog method.");
        Action forget = Executor.ActionExecutorChecks.FileSizedForgetCosts;
        if (!BudgetCatalog.Any(entry => entry.Run.Method == forget.Method))
            throw new InvalidOperationException("Missing budget catalog method: " + forget.Method.Name);
        var methods = typeof(Run.CallBudgetChecks).GetMethods(System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(method => method.Name.StartsWith("RunBudget", StringComparison.Ordinal) &&
                method.ReturnType == typeof(void) && method.GetParameters().Length == 0);
        foreach (var method in methods)
            if (!BudgetCatalog.Any(entry => entry.Run.Method == method))
                throw new InvalidOperationException("Missing budget catalog method: " + method.Name);
        return BudgetCatalog.Select(entry => entry.Run).ToArray();
    }

    private static Action[] BudgetShard(int shard)
    {
        var actions = BudgetCatalog.Where(entry => entry.Shard == shard).Select(entry => entry.Run).ToArray();
        if (actions.Length == 0) throw new InvalidOperationException("Budget shard is empty: " + shard);
        return actions;
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<Action>> All { get; } = new Dictionary<string, IReadOnlyList<Action>>
    {
        ["literals"] = [Layout.LiteralsChecks.Run, Layout.OwnershipChecks.Run],
        ["dependencies"] = [Layout.DependencyChecks.Run],
        ["strings"] = [Layout.StringsChecks.Run],
        ["settings"] = [Contracts.SettingsParserChecks.Run],
        ["vocabulary"] = [Contracts.VocabularyChecks.Run],
        ["paths"] = [Contracts.PathNamesChecks.Run],
        ["row"] = [Contracts.RowChecks.Run],
        ["side-facts"] = [Contracts.SideFactsChecks.Run],
        ["scope"] = [Contracts.PathScopeChecks.Run],
        ["issues"] = [Contracts.IssuePolicyChecks.Run, Contracts.IssueSubjectChecks.Run, Contracts.IssueSubjectChecks.ReviewedIdentity,
            Contracts.IssueSubjectChecks.ReviewedNullMembers, Contracts.DriveEvidenceChecks.Run],
        ["locks"] = [Contracts.PathLocksChecks.Run],
        ["containment"] = [Executor.ChangeRunnerChecks.RunContainment, Storage.ScanReaderChecks.Run, Storage.ChangeTableChecks.Run],
        ["containment-move-out"] = [Executor.ChangeRunnerChecks.RunMoveOutSafety],
        ["work-lifecycle"] = [Storage.ChangeTableChecks.Run, Storage.QueryPlanChecks.Run],
        ["repair-held"] = [Storage.ChangeTableChecks.RepairHeldSelection, Executor.ChangeRunnerChecks.RunRepairHeld, Executor.FolderDeleteRunnerChecks.RunRepairHeld, Executor.ScanPassChecks.RunRepairHeld],
        ["repair-claims"] = [Storage.StateStoreChecks.ClaimedRepair, Storage.QueryPlanChecks.ClaimedRetirement,
            Executor.ChangeRunnerChecks.FailedSelectionReleasesClaim],
        ["store"] = [Storage.DeletedHistoryChecks.Run, Storage.IssueFactsChecks.Run, Storage.StateStoreChecks.Run, Storage.ScanWriterChecks.Run, Storage.ScanReaderChecks.Run, Storage.StateCommitterChecks.Run, Storage.IssueTableChecks.Run,
            Storage.IssueTableChecks.VisibilityCases, Storage.IssueTableChecks.ReviewedRetryReads,
            Storage.IssueTableChecks.ReviewedRootIdentity, Storage.IssueTableChecks.ReviewedPairDecision,
            Storage.StoredFolderPlanChecks.Run, Storage.ChangeTableChecks.Run, Storage.CompactStorageChecks.Run, Storage.QueryPlanChecks.Run],
        ["local"] = [Local.LocalSideChecks.Run, Local.LocalListingChecks.Run, Local.LocalWriterChecks.Run, Local.LocalStagedChecks.Run, Local.LocalBinChecks.Run, Local.LocalPathsChecks.Run, Local.PendingResolverChecks.Run, Local.StageNamesChecks.Run, Local.LogFilesChecks.Run, Local.SettingsFileChecks.Run, Local.DownloadSpaceChecks.Run, Local.VolumeSpaceChecks.Run, Local.JournalSourceChecks.Run, Local.PendingNotesChecks.Run],
        ["dropbox"] = [Dropbox.DropboxApiChecks.Run, Dropbox.DropboxSideChecks.Run, Dropbox.DropboxPathsChecks.Run, Dropbox.DropboxListingChecks.Run, Dropbox.DropboxWriterChecks.Run, Dropbox.DropboxStagedChecks.Run, Dropbox.DropboxEntriesChecks.Run, Dropbox.DropboxErrorsChecks.Run, Dropbox.UploadSliceChecks.Run, Dropbox.ResponseStreamChecks.Run, Dropbox.DropboxFeedChecks.Run],
        ["limiter"] = [Dropbox.AdaptiveLimiterChecks.Run],
        ["decide"] = [Planner.DecideChecks.Run, Planner.TimeRulesChecks.Run, Planner.SyncActionChecks.Run, Planner.RenameDecisionChecks.Run],
        ["renames"] = [Planner.RenamesChecks.Run],
        ["folder-deletes"] = [Planner.FolderDeletesChecks.Run, Executor.RecoveryChecks.Run],
        ["resolutions"] = [Planner.ResolutionsChecks.Run],
        ["exclusions"] = [Planner.ExclusionsChecks.Run, Executor.ExclusionRunnerChecks.Run],
        ["released-history"] = [Executor.ChangeRunnerChecks.ReleasedHistory],
        ["stale-freshness"] = [Executor.ChangeRunnerChecks.RunStaleFreshness],
        ["syncer"] = [Executor.SyncerChecks.Run, Executor.OwnFolderChecks.Run, Executor.ActionExecutorChecks.Run, Executor.LockKeysChecks.Run, Executor.ObserverChecks.Run, Executor.TransfersChecks.Run, Executor.ParentsChecks.Run, Executor.SyncedEntriesChecks.Run, Executor.RenameRunnerChecks.Run, Executor.KeepBothRunnerChecks.Run, Executor.FolderPlanBuilderChecks.Run, Executor.FolderDeleteRunnerChecks.Run, Executor.ResolutionRunnerChecks.Run, Executor.ChangeRunnerChecks.Run],
        ["mapped-holds"] = [Contracts.IssuePolicyChecks.Run, Executor.ChangeRunnerChecks.RunMappedHolds],
        ["safety"] = [Executor.SyncerChecks.RunResolvedFolderExclusion, Executor.SyncerChecks.RunResolvedFolderHold, Executor.ChangeRunnerChecks.RunLiveReplaceSafety, Executor.ChangeRunnerChecks.RunRaisedHoldSafety, Executor.ChangeRunnerChecks.RunHeldRenameSafety],
        ["repair"] = [Run.RepairChecks.Run, Executor.ScanPassChecks.Run, Run.RowContextsChecks.Run, Run.PrefixSetChecks.Run, Run.TaskSetChecks.Run, Run.RunLogChecks.Run, Run.EngineOperationsChecks.Run,
            Run.ExcludedSavedRowChecks.KeptEdit, Run.ExcludedSavedRowChecks.DeletedCopy,
            Run.ExcludedSavedRowChecks.UnchangedReads, Run.ExcludedSavedRowChecks.BlockedNotice,
            Run.AutoSyncChecks.Run, Run.EngineHostChecks.Run,
            Run.EngineHostChecks.ReviewedPrerequisiteRetry, Run.EngineHostChecks.ReviewedObligationRetry,
            Run.EngineHostChecks.ReviewedSourceWaitRetry, Run.EngineHostChecks.ReviewedDispatchWaitRetry,
            Run.EngineHostChecks.ReviewedRepairDispatchRetry,
            Run.EngineHostChecks.ReviewedRecoveryRetry, Run.LiveContextChecks.Run, Run.MonitorChecks.Run,
            Run.FailureEpisodeChecks.Run],
        ["repair-direct"] = [Run.RepairChecks.DirectRepair],
        ["repair-forgotten"] = [Run.RepairChecks.ConcurrentForgottenStore, Run.RepairChecks.ConcurrentForgottenMemory, Run.RepairChecks.ForgottenFailure],
        ["human-log"] = [Run.HumanLogChecks.Run],
        ["attempt-cost"] = [Executor.ActionExecutorChecks.AttemptCost],
        ["engine-host"] = [Run.EngineHostChecks.Run,
            Run.EngineHostChecks.ReviewedPrerequisiteRetry, Run.EngineHostChecks.ReviewedObligationRetry,
            Run.EngineHostChecks.ReviewedSourceWaitRetry, Run.EngineHostChecks.ReviewedDispatchWaitRetry,
            Run.EngineHostChecks.ReviewedRepairDispatchRetry,
            Run.EngineHostChecks.ReviewedRecoveryRetry, Edge.EngineHostReadChecks.Run],
        ["engine-operations"] = [Run.EngineOperationsChecks.Run],
        ["peer"] = [Storage.LiveFenceChecks.Run, Executor.ChangeRunnerChecks.RunPeers],
        ["live-run-core"] = [Scenarios.LiveRunChecks.RunCore],
        ["live-wake"] = [Scenarios.LiveRunChecks.RunWake, Scenarios.LiveRunChecks.RunLockPair],
        ["live-admission"] = [Storage.LiveFenceChecks.Run, Local.JournalSourceChecks.QuietVolumeWatermark,
            Executor.ChangeRunnerChecks.RunPeers, Scenarios.LiveRunChecks.Run, Scenarios.LiveRunChecks.RunHeldTwice,
            Run.LiveContextChecks.Run],
        ["random-sequence"] = [Run.RandomSequenceChecks.Run],
        ["budget"] = BudgetChecks,
        ["budget-1"] = BudgetShard(1),
        ["budget-2"] = BudgetShard(2),
        ["budget-3"] = BudgetShard(3),
        ["budget-4"] = BudgetShard(4),
        ["issue-category-cost"] = [Run.IssueCategoryCostChecks.Run],
        ["speed"] = [Run.CallBudgetChecks.RunSpeed],
        ["storage-history"] = [Storage.DeletedHistoryChecks.Run],
        ["history-restore-speed"] = [Storage.DeletedHistoryChecks.RunRestoreSpeed],
        ["scan-writer"] = [Storage.ScanWriterChecks.Run],
        ["scan-pass"] = [Executor.ScanPassChecks.Run],
        ["scan-owner"] = [Storage.ScanPartitionChecks.Run, Storage.ChangeTableChecks.RepairOverlap,
            Executor.ScanPassChecks.FreshIssuesSurviveReconciliation, Storage.QueryPlanChecks.Run],
        ["read-speed"] = [Run.CallBudgetChecks.RunReadSpeed],
        ["read-provider-speed"] = [Run.CallBudgetChecks.RunReadProviderSpeed],
        // The default scenario check runs the offline cases, then the same cases live against the real account.
        ["scenarios"] = [Scenarios.ScenarioChecks.RunAll],
        // The same cases against the in-memory Dropbox account, for when the live service is unreachable.
        ["scenarios-offline"] = Scenarios.ScenarioChecks.OfflineCases(),
        ["scenarios-live"] = [Scenarios.ScenarioChecks.RunLive],
        ["cli"] = [Edge.EngineCommandsChecks.Run, Edge.EngineCommandsChecks.ReviewedNoticeChoices,
            Edge.EngineCliRuntimeChecks.Run],
        ["host-reads"] = [Edge.EngineHostReadChecks.Run],
        ["live-reservation"] = [Run.LiveContextChecks.ConcurrentReservationBeforeAdmission],
        ["persistent-failure"] = [Run.LiveContextChecks.PersistentFailurePass],
        ["transport"] = [Edge.EngineHostTransportChecks.Run],
    };
}
