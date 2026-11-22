namespace Massdex.SyncShark.Engine.Tests;

/// <summary>Registers the checks run by the CI executable.</summary>
public static class CheckRegistry
{
    public static IReadOnlyList<Check> All(CheckArguments input) => Groups(input).Values.SelectMany(checks => checks).ToArray();

    private static Dictionary<string, Check[]> Groups(CheckArguments input) => new()
    {
        ["dependencies"] = [
            new("Layout/Dependencies", () => Layout.DependencyChecks.RunAsync(input))
        ],
        ["fixtures"] = [
            new("Fixtures/ReleaseSchema", Scenarios.StoreMoveChecks.CreateFixtureAsync)
        ],
        ["contracts"] = [
            new("Contracts/ContentHashChecks", Contracts.ContentHashChecks.RunAsync),
            new("Contracts/PathScopeChecks", Contracts.PathScopeChecks.RunAsync)
        ],
        ["dropbox"] = [
            new("Dropbox/ConnectionsChecks", Dropbox.ConnectionsChecks.RunAsync),
            new("Dropbox/CredentialStoreChecks", Dropbox.CredentialStoreChecks.RunAsync),
            new("Dropbox/DeletedHistoryChecks", Dropbox.DeletedHistoryChecks.RunAsync),
            new("Dropbox/DispatcherChecks", Dropbox.DispatcherChecks.RunAsync),
            new("Dropbox/DropboxApiChecks", Dropbox.DropboxApiChecks.RunAsync),
            new("Dropbox/DropboxEntriesChecks", Dropbox.DropboxEntriesChecks.RunAsync),
            new("Dropbox/DropboxErrorsChecks", Dropbox.DropboxErrorsChecks.RunAsync),
            new("Dropbox/DropboxFeedChecks", Dropbox.DropboxFeedChecks.RunAsync),
            new("Dropbox/DropboxListingChecks", Dropbox.DropboxListingChecks.RunAsync),
            new("Dropbox/DropboxPathsChecks", Dropbox.DropboxPathsChecks.RunAsync),
            new("Dropbox/DropboxProtocolChecks", Dropbox.DropboxProtocolChecks.RunAsync),
            new("Dropbox/DropboxReplyChecks", Dropbox.DropboxReplyChecks.RunAsync),
            new("Dropbox/DropboxReplyShapesChecks", Dropbox.DropboxReplyShapesChecks.RunAsync),
            new("Dropbox/DropboxSideChecks", Dropbox.DropboxSideChecks.RunAsync),
            new("Dropbox/DropboxWriterChecks", Dropbox.DropboxWriterChecks.RunAsync),
            new("Dropbox/FolderJobsChecks", Dropbox.FolderJobsChecks.RunAsync),
            new("Dropbox/RateLimitChecks", Dropbox.RateLimitChecks.RunAsync),
            new("Dropbox/ResponseStreamChecks", Dropbox.ResponseStreamChecks.RunAsync),
            new("Dropbox/RoutesChecks", Dropbox.RoutesChecks.RunAsync),
            new("Dropbox/SignInChecks", Dropbox.SignInChecks.RunAsync),
            new("Dropbox/UploadContentChecks", Dropbox.UploadContentChecks.RunAsync)
        ],
        ["edge"] = [
            new("Edge/CliChecks", Edge.CliChecks.RunAsync),
            new("Edge/CommandsChecks", Edge.CommandsChecks.RunAsync),
            new("Edge/HostClientChecks", Edge.HostClientChecks.RunAsync),
            new("Edge/HostServerChecks", Edge.HostServerChecks.RunAsync),
            new("Edge/PipeFrameChecks", Edge.PipeFrameChecks.RunAsync),
            new("Edge/RenderChecks", Edge.RenderChecks.RunAsync)
        ],
        ["executor"] = [
            new("Executor/CaseRenameChecks", Executor.CaseRenameChecks.RunAsync),
            new("Executor/EffectsChecks", Executor.EffectsChecks.RunAsync),
            new("Executor/ExclusionsChecks", Executor.ExclusionsChecks.RunAsync),
            new("Executor/FolderDeleteChecks", Executor.FolderDeleteChecks.RunAsync),
            new("Executor/FolderMergeChecks", Executor.FolderMergeChecks.RunAsync),
            new("Executor/ItemLocksChecks", Executor.ItemLocksChecks.RunAsync),
            new("Executor/ItemSyncChecks", Executor.ItemSyncChecks.RunAsync),
            new("Executor/KeepBothChecks", Executor.KeepBothChecks.RunAsync),
            new("Executor/NoticesChecks", Executor.NoticesChecks.RunAsync),
            new("Executor/ResolveChecks", Executor.ResolveChecks.RunAsync),
            new("Executor/RetriesChecks", Executor.RetriesChecks.RunAsync),
            new("Executor/UnfinishedChecks", Executor.UnfinishedChecks.RunAsync)
        ],
        ["cli"] = [
            new("Layout/CliParityChecks", () => Layout.CliParityChecks.RunAsync(input))
        ],
        ["strings"] = [
            new("Layout/StringsChecks", Layout.StringsChecks.RunAsync)
        ],
        ["local"] = [
            new("Local/DirectoryListingChecks", Local.DirectoryListingChecks.RunAsync),
            new("Local/DriveIdentityChecks", Local.DriveIdentityChecks.RunAsync),
            new("Local/IdentityPathsChecks", Local.IdentityPathsChecks.RunAsync),
            new("Local/InUseChecks", Local.InUseChecks.RunAsync),
            new("Local/JournalReaderChecks", Local.JournalReaderChecks.RunAsync),
            new("Local/LocalErrorsChecks", Local.LocalErrorsChecks.RunAsync),
            new("Local/LocalSideChecks", Local.LocalSideChecks.RunAsync),
            new("Local/LocalStagedChecks", Local.LocalStagedChecks.RunAsync),
            new("Local/LogFilesChecks", Local.LogFilesChecks.RunAsync),
            new("Local/PlaceholdersChecks", Local.PlaceholdersChecks.RunAsync),
            new("Local/RecycleBinChecks", Local.RecycleBinChecks.RunAsync),
            new("Local/UsnJournalChecks", Local.UsnJournalChecks.RunAsync),
            new("Local/VolumeSpaceChecks", Local.VolumeSpaceChecks.RunAsync)
        ],
        ["planner"] = [
            new("Planner/ConflictNameChecks", Planner.ConflictNameChecks.RunAsync),
            new("Planner/DecideChecks", Planner.DecideChecks.RunAsync),
            new("Planner/FolderDeletesChecks", Planner.FolderDeletesChecks.RunAsync),
            new("Planner/MovesChecks", Planner.MovesChecks.RunAsync),
            new("Planner/ResolutionsChecks", Planner.ResolutionsChecks.RunAsync),
            new("Planner/SyncActionChecks", Planner.SyncActionChecks.RunAsync),
            new("Planner/TimeRulesChecks", Planner.TimeRulesChecks.RunAsync)
        ],
        ["run"] = [
            new("Run/AutoSyncChecks", Run.AutoSyncChecks.RunAsync),
            new("Run/HostChecks", Run.HostChecks.RunAsync),
            new("Run/LogTextChecks", Run.LogTextChecks.RunAsync),
            new("Run/MonitorChecks", Run.MonitorChecks.RunAsync),
            new("Run/PairRunnerChecks", Run.PairRunnerChecks.RunAsync),
            new("Run/RepairChecks", Run.RepairChecks.RunAsync),
            new("Run/RunLogChecks", Run.RunLogChecks.RunAsync),
            new("Run/StatusChecks", Run.StatusChecks.RunAsync),
            new("Run/WorkQueueChecks", Run.WorkQueueChecks.RunAsync)
        ],
        ["call-budget"] = [
            new("Unchanged repair 5110", () => Scenarios.CallBudgetChecks.UnchangedAsync(5110)),
            new("Unchanged repair 20440", () => Scenarios.CallBudgetChecks.UnchangedAsync(20440))
        ],
        ["acceptance"] = [
            new("A38 empty page advances", Scenarios.RecoveryChecks.EmptyPageAsync),
            new("A40 provider delay", Scenarios.RecoveryChecks.RequestWaitAsync),
            new("A41 root replacement", Scenarios.RecoveryChecks.RootReplacementAsync),
            new("A43 single selected member", () => Scenarios.RecoveryChecks.BatchSelectionAsync(1)),
            new("A43 batched selected members", () => Scenarios.RecoveryChecks.BatchSelectionAsync(2)),
            new("A47 pause precedence", Scenarios.RecoveryChecks.PauseAsync),
            new("A22 identical occupied destination", () => Scenarios.RecoveryChecks.OccupiedDestinationAsync(false)),
            new("A23 folder merge", () => Scenarios.RecoveryChecks.OccupiedDestinationAsync(true)),
            new("A26 safe save", Scenarios.RecoveryChecks.SafeSaveAsync),
            new("A35 later proved deletion", () => Scenarios.RecoveryChecks.FirstDeletedAsync(false,true)),
            new("A35 no measured offset", () => Scenarios.RecoveryChecks.FirstDeletedAsync(false,false)),
            new("A36 empty deleted folder", () => Scenarios.RecoveryChecks.FirstDeletedAsync(true,true)),
            new("A39 unreadable selection", () => Scenarios.RecoveryChecks.RefusedRemovalAsync(false)),
            new("A48 recycle veto", () => Scenarios.RecoveryChecks.RefusedRemovalAsync(true)),
            new("A13 latest attributable edit", () => Scenarios.FolderChecks.DeleteAsync(edited:true)),
            new("A14 excluded survivor", () => Scenarios.FolderChecks.DeleteAsync(excluded:true)),
            new("A15 ignored contents", () => Scenarios.FolderChecks.DeleteAsync(ignored:true)),
            new("A16 unreadable descendant", () => Scenarios.FolderChecks.DeleteAsync(unreadable:true)),
            new("A24 move into exclusion", Scenarios.ReconciliationChecks.MoveOutsideAsync),
            new("A30 link membership", Scenarios.FolderChecks.LinksAsync),
            new("A46 fresh boot", Scenarios.FolderChecks.FreshStartAsync),
            new("A50 nested survivor", () => Scenarios.FolderChecks.DeleteAsync(nested:true)),
            new("A03 preserve two versions", () => Scenarios.RecoveryChecks.KeptVersionsAsync(false)),
            new("A04 preserve file and quiet folder child", () => Scenarios.RecoveryChecks.KeptVersionsAsync(true)),
            new("A09 churn", Scenarios.RecoveryChecks.ChurnAsync),
            new("A33 overlapping work", Scenarios.RecoveryChecks.ConcurrentRepairAsync),
            new("A37 repeat comparison", Scenarios.RecoveryChecks.RepeatRepairAsync),
            new("A42 altered stage", Scenarios.RecoveryChecks.AlteredStageAsync),
            new("A45 quiet running token", Scenarios.RecoveryChecks.QuietJobAsync),
            new("A51 retained legacy tie", () => Scenarios.RecoveryChecks.ClosedNoticeAsync(true)),
            new("A52 fully absent notice", () => Scenarios.RecoveryChecks.ClosedNoticeAsync(false)),
            new("A06 move plus edit", Scenarios.ReconciliationChecks.MoveAndEditAsync),
            new("A08 one-sided kind replacement", Scenarios.ReconciliationChecks.KindReplacementAsync),
            new("A18 move", Scenarios.ReconciliationChecks.MoveOnlyAsync),
            new("A19 opposite edit", Scenarios.ReconciliationChecks.MoveAndEditOppositeAsync),
            new("A20 move twice", Scenarios.ReconciliationChecks.DifferentDestinationsAsync),
            new("A21 moved and deleted", Scenarios.ReconciliationChecks.MoveAndDeleteAsync),
            new("A25 different renames", Scenarios.ReconciliationChecks.DifferentDestinationsAsync),
            new("A27 one-sided capitals", Scenarios.ReconciliationChecks.CapitalsAsync),
            new("A27 different capitals", Scenarios.ReconciliationChecks.DifferentCapitalsAsync),
            new("A27 first folders with different capitals", Scenarios.ReconciliationChecks.FolderCapitalsAsync),
            new("A28 keep exclusion", Scenarios.ReconciliationChecks.ExcludeKeepAsync),
            new("A29 ignored file", Scenarios.ReconciliationChecks.IgnoreAsync),
            new("A32 invalid name", Scenarios.ReconciliationChecks.ForbiddenNameAsync),
            new("A34 NewestWins", () => Scenarios.ReconciliationChecks.FirstRunAsync(FirstRunPolicy.NewestWins, 4)),
            new("A34 LocalWins", () => Scenarios.ReconciliationChecks.FirstRunAsync(FirstRunPolicy.LocalWins, 2)),
            new("A34 DropboxWins", () => Scenarios.ReconciliationChecks.FirstRunAsync(FirstRunPolicy.DropboxWins, 4)),
            new("A02 create file", () => Scenarios.ItemChecks.ChangeAsync(false, 3, null, Side.Local, 3)),
            new("A02 create empty folder", () => Scenarios.ItemChecks.ChangeAsync(false, null, 1, Side.Dropbox, 1, folder: true)),
            new("A05 pc edit", () => Scenarios.ItemChecks.ChangeAsync(true, 2, 1, Side.Local, 2)),
            new("A05 cloud edit", () => Scenarios.ItemChecks.ChangeAsync(true, 1, 3, Side.Dropbox, 3)),
            new("A07 same edits", () => Scenarios.ItemChecks.ChangeAsync(true, 4, 4, Side.Local, 4)),
            new("A10 pc deletion", () => Scenarios.ItemChecks.ChangeAsync(true, null, 1, Side.Local, null)),
            new("A10 cloud deletion", () => Scenarios.ItemChecks.ChangeAsync(true, 1, null, Side.Dropbox, null)),
            new("A11 cloud edit beats pc deletion", () => Scenarios.ItemChecks.ChangeAsync(true, null, 2, Side.Local, 2, TextKey.InfoEditBeatsDelete)),
            new("A12 both deleted", () => Scenarios.ItemChecks.ChangeAsync(true, null, null, Side.Dropbox, null)),
            new("A01 identical", Scenarios.ItemChecks.IdenticalAsync)
        ],
        ["storage"] = [
            new("Storage/CommitterChecks", Storage.CommitterChecks.RunAsync),
            new("Storage/DeletedNamesChecks", Storage.DeletedNamesChecks.RunAsync),
            new("Storage/EncodingChecks", Storage.EncodingChecks.RunAsync),
            new("Storage/NoticeTableChecks", Storage.NoticeTableChecks.RunAsync),
            new("Storage/PairTableChecks", Storage.PairTableChecks.RunAsync),
            new("Storage/RecordTableChecks", Storage.RecordTableChecks.RunAsync),
            new("Storage/ScanTableChecks", Storage.ScanTableChecks.RunAsync),
            new("Storage/StoreChecks", Storage.StoreChecks.RunAsync),
            new("Storage/StoreMoveChecks", Storage.StoreMoveChecks.RunAsync)
        ],
        ["literals"] = [
            new("Layout/Literals", () => Layout.LiteralsChecks.RunAsync(input))
        ],
    };
}
