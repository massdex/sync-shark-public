using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static partial class Program
{
    private static uint _popupHostDpi;
    private static IReadOnlyList<RealDpiMonitor> _realDpiMonitors = [];
    private static readonly List<uint> ObservedTargetWindowDpis = [];

    private static int RunTrayTooltipContract()
    {
        var statuses = new[]
        {
            "Starting Sync Shark", "Needs Attention", "Sync Paused", "Up to Date", "Saving Folder Inventories",
            "Work Waiting to Finish", "Choose Folders to Sync", "Connect Dropbox", "Live Sync Paused", "Ready to Check"
        };
        using var icon = new System.Windows.Forms.NotifyIcon();
        foreach (var status in statuses)
        {
            var tooltip = App.FormatTrayTooltip(status);
            if (tooltip != $"Sync Shark\n{status}" || tooltip.Contains('·'))
                throw new InvalidOperationException($"Tray tooltip must separate product and status: {status}");
            icon.Text = tooltip;
            if (icon.Text != tooltip || icon.Visible)
                throw new InvalidOperationException("The hidden notification icon did not retain the two-line tooltip.");
        }
        var longTooltip = App.FormatTrayTooltip(new string('x', 100));
        icon.Text = longTooltip;
        if (longTooltip.Length != 63 || !longTooltip.StartsWith("Sync Shark\n", StringComparison.Ordinal))
            throw new InvalidOperationException("Tray tooltip must retain its header and existing length limit.");
        Console.WriteLine("PASS: two-line tray tooltip for startup, attention, paused, idle and active states; length limit preserved. No visible UI or product startup.");
        return 0;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // Before the evidence writer or any fixture file I/O: placeholder attributes must be visible
        // to the whole process. Non-Windows hosts make no native call.
        WindowsPlaceholderCompatibility.Expose();
        using var interactions = UiInteractionEvidence.Start(args);
        try { return RunFixtureMain(args); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static int RunFixtureMain(string[] args)
    {
        if (args is ["--sparse-planning-correction-checks", var sparseCorrectionRoot])
        { SparsePlanningContextChecks.RunAsync(Path.GetFullPath(sparseCorrectionRoot), correctionsOnly: true).GetAwaiter().GetResult(); return 0; }
        if (args is ["--bounded-presentation-history-checks", var historyRoot])
        { BoundedPresentationHistoryChecks.RunAsync(Path.GetFullPath(historyRoot)).GetAwaiter().GetResult(); return 0; }
        if (args is ["--real-staged-guard-checks"])
        { try { return RunRealStagedGuardChecks(); }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args is ["--real-staged-window", var realStagedRoot])
        { try { return RunRealStagedWindow(realStagedRoot); }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length >= 3 && args[0] == "--real-staged-clone")
        { try { return RunRealStagedCloneAsync(args[1], args[2..]).GetAwaiter().GetResult(); }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 2 && args[0] == "--cloned-state-adapter-selftest")
        { try { return RunClonedStateAdapterSelftest(args[1]); }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 2 && args[0] == "--cloned-state-adapter-live")
        { try { return RunClonedStateAdapterLive(args[1]); }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 2 && args[0] == "--cloned-state-journey-applicability")
        { try { return RunClonedStateJourneyApplicability(args[1]); }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 2 && args[0] == "--run-outcome-pair-save-checks")
        { try { return RunOutcomeAndPairSaveChecks.Run(Path.GetFullPath(args[1])); }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 2 && args[0] == "--presentation-refresh-checks")
        { try { return PresentationRefreshChecks.Run(Path.GetFullPath(args[1])); }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length is 2 or 3 && args[0] == "--startup-loading-ui-checks" &&
            (args.Length == 2 || args[2] == "--inspect"))
            return StartupLoadingChecks.Run(Path.GetFullPath(args[1]), args.Length == 3);
        if (args.Length is 3 or 4 && args[0] == "--mixed-file-retry-ui-checks")
        { try { return RunMixedFileRetryUiChecks(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args.Length == 4 && args[3] == "--uncertain"); }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length is 3 or 4 && args[0] == "--operation-log-ui-checks")
        { try { return RunOperationLogUiChecks(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args.Length == 4 && args[3] == "--inspect"); }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args is ["--pair-failure-presentation-checks"])
            return PairFailurePresentationChecks.Run();
        if (args.Length == 2 && args[0] == "--endpoint-pair-ui-checks")
            return RunEndpointPairUiChecks(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--endpoint-reversed-ui-checks")
            return RunEndpointReversedUiChecks(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--endpoint-policy-ui-checks")
            return RunEndpointPolicyUiChecks(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--explicit-work-admission-checks")
        { try { ExplicitWorkAdmissionChecks.RunAsync(Path.GetFullPath(args[1])).GetAwaiter().GetResult(); return 0; }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 2 && args[0] == "--compact-observation-cut-checks")
        { try { CompactObservationCutChecks.RunAsync(Path.GetFullPath(args[1])).GetAwaiter().GetResult(); return 0; }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 2 && args[0] == "--endpoint-cloud-host-checks")
        { try { RunEndpointCloudHostChecksAsync(Path.GetFullPath(args[1])).GetAwaiter().GetResult(); return 0; }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 2 && args[0] is "--pair-endpoint-runtime-checks" or "--pair-endpoint-host-checks")
        { try { PairEndpointRuntimeChecks.RunAsync(Path.GetFullPath(args[1]), args[0] == "--pair-endpoint-host-checks").GetAwaiter().GetResult(); return 0; }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 2 && args[0] == "--cli-window-host")
            return CliBridgeChecks.RunWindowHost(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--google-window-host")
            return CliBridgeChecks.RunWindowHost(Path.GetFullPath(args[1]), googleAccounts: true);
        if (args.Length >= 2 && args[0] == "--cli-window-client")
            return CliBridgeChecks.RunWindowClient(args.Skip(1).ToArray());
        if (args.Length == 1 && args[0] == "--cli-executable-checks")
            return CliExecutableChecks.Run();
        if (args is ["--cli-exit-disposition-checks", var exitDispositionRoot])
            return CliBridgeChecks.RunExitDispositionChecks(Path.GetFullPath(exitDispositionRoot));
        if (args.Length >= 1 && args[0] == "--cli-bridge-checks")
            return CliBridgeChecks.RunTwoProcess(Path.GetFullPath(args.Length > 1
                ? args[1]
                : Path.Combine(Path.GetTempPath(), "sync-shark-cli-bridge-" + Guid.NewGuid().ToString("N"))));
        if (args.Length >= 1 && args[0] == "--cli-bridge-presentation-checks")
            return CliBridgeChecks.RunPresentationHookChecks(Path.GetFullPath(args.Length > 1
                ? args[1]
                : Path.Combine(Path.GetTempPath(), "sync-shark-cli-bridge-hook-" + Guid.NewGuid().ToString("N"))));
        if (args.Length >= 1 && args[0] == "--cli-bridge-host")
            return CliBridgeChecks.RunHost(Path.GetFullPath(args.Length > 1
                ? args[1]
                : Path.Combine(Path.GetTempPath(), "sync-shark-cli-bridge-host-" + Guid.NewGuid().ToString("N"))),
                initialSetup: args.Length > 2 && args[2] == "initial");
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        if (args.Length == 1 && args[0] == "--tray-tooltip-contract")
            return RunTrayTooltipContract();
        if (args.Length == 2 && args[0] == "--cli-settings-checks")
            return CliSettingsChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 1 && args[0] == "--release-setup-checks")
            return ReleaseSetupChecks.Run();
        if (args.Length == 2 && args[0] == "--provider-package-checks")
            return ProviderPackageChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--shared-drive-account-ui-checks")
        { try { return SharedDriveAccountUiChecks.Run(Path.GetFullPath(args[1])); }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 2 && args[0] == "--provider-activity-checks")
            return ProviderActivityChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && string.Equals(args[0], "--endpoint-location-action-checks", StringComparison.Ordinal))
            return EndpointLocationActionChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 1 && args[0] == "--explorer-registration-checks")
            return ExplorerRegistrationChecks.Run();
        if (args.Length == 1 && args[0] == "--protected-runtime-checks")
        { try { return ProtectedRuntimeChecks.Run(); } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 3 && args[0] == "--protected-runtime-child")
        { try { return ProtectedRuntimeChecks.Child(args); } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 1 && args[0] == "--discovery-transaction-checks")
        { try { return DiscoveryTransactionChecks.Run(); } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 4 && args[0] == "--discovery-transaction-child")
        { try { return DiscoveryTransactionChecks.Child(args); } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 1 && args[0] == "--visual-corrections-contract")
            return RunVisualCorrectionsContract();
        if (args.Length == 1 && args[0] == "--google-sharing-checks")
        { Task.Run(GoogleDriveSharingChecks.RunAsync).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 2 && args[0] == "--google-trash-checks")
        { Task.Run(() => GoogleDriveTrashChecks.RunAsync(Path.GetFullPath(args[1]))).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 2 && args[0] == "--google-trash-host-checks")
        { Task.Run(() => GoogleTrashHostChecks.RunAsync(Path.GetFullPath(args[1]))).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 1 && args[0] == "--google-shared-drive-browser-checks")
        { Task.Run(() => GoogleDriveSharedDriveBrowserChecks.RunAsync()).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 1 && args[0] == "--google-navigation-checks")
        { Task.Run(GoogleDriveNavigationChecks.RunAsync).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 1 && args[0] == "--google-recovery-checks")
        { Task.Run(GoogleDriveRecoveryChecks.RunAsync).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 2 && args[0] == "--google-revision-checks")
        { Task.Run(() => GoogleDriveRevisionChecks.RunAsync(Path.GetFullPath(args[1]))).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 2 && args[0] == "--manual-recovery-record-checks")
        { Task.Run(() => ManualRecoveryRecordChecks.RunAsync(Path.GetFullPath(args[1]))).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 2 && args[0] == "--cloud-native-observation-checks")
        { Task.Run(() => CloudNativeObservationChecks.RunAsync(Path.GetFullPath(args[1]))).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 2 && args[0] == "--settings-structure-checks")
        { try { return SettingsStructureChecks.Run(Path.GetFullPath(args[1])); }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 2 && args[0] == "--google-range-checks")
        { Task.Run(() => GoogleDriveVerifiedRangeChecks.RunAsync(Path.GetFullPath(args[1]))).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 2 && args[0] == "--google-native-cloud-files-checks")
        { Task.Run(() => GoogleNativeCloudFilesChecks.RunAsync(Path.GetFullPath(args[1]))).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 2 && args[0] == "--google-native-cloud-files-approved-checks")
        { Task.Run(() => GoogleNativeCloudFilesChecks.RunAsync(Path.GetFullPath(args[1]), approvedLocation: true)).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 1 && args[0] == "--oauth-return-bridge-checks")
        { Task.Run(OAuthReturnBridgeChecks.RunAsync).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 3 && args[0] == "--endpoint-model-checks")
        { Task.Run(() => EndpointModelChecks.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]))).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 2 && args[0] == "--endpoint-editor-checks")
        { Task.Run(() => PairEditorChecks.RunAsync(Path.GetFullPath(args[1]))).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 2 && args[0] == "--endpoint-picker-checks")
        { Task.Run(() => EndpointPickerChecks.RunAsync(Path.GetFullPath(args[1]))).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 2 && args[0] == "--oauth-presentation-checks")
        { Task.Run(() => OAuthPresentationChecks.RunAsync(Path.GetFullPath(args[1]))).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 2 && args[0] == "--oauth-return-preview")
        { OAuthReturnBridgeChecks.RenderPreview(Path.GetFullPath(args[1])); return 0; }
        if (args.Length == 2 && args[0] == "--cloud-identity-checks")
        { Task.Run(() => CloudPlaceholderIdentityChecks.RunAsync(Path.GetFullPath(args[1]))).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 1 && args[0] == "--window-close-contract")
            return RunWindowCloseContract();
        if (args.Length == 2 && args[0] == "--workspace-ui-checks") return WorkspaceUiChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--activity-account-ui-checks") return RunActivityAccountChecks(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--first-sync-context-checks") return FirstSyncContextChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--first-sync-feedback-checks") return FirstSyncFeedbackChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--identity-recovery-ui-checks") return FirstSyncFeedbackChecks.Run(Path.GetFullPath(args[1]), identityRecoveryOnly: true);
        if (args.Length == 2 && args[0] == "--workflow-surfacing-checks") return WorkflowSurfacingChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && string.Equals(args[0], "--endpoint-review-checks", StringComparison.Ordinal))
            return EndpointReviewChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && string.Equals(args[0], "--endpoint-hierarchy-ui-checks", StringComparison.Ordinal))
            return EndpointHierarchyUiChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && string.Equals(args[0], "--conflict-presentation-path-checks", StringComparison.Ordinal))
            return ConflictPresentationPathChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && string.Equals(args[0], "--notice-options-ui-checks", StringComparison.Ordinal))
            return NoticeOptionsUiChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && string.Equals(args[0], "--ui-foundation-checks", StringComparison.Ordinal))
            return FoundationUiChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && string.Equals(args[0], "--shared-presentation-contract-checks", StringComparison.Ordinal))
            return SharedPresentationContractChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && string.Equals(args[0], "--pause-scope-checks", StringComparison.Ordinal))
            return PauseScopeChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && string.Equals(args[0], "--recovery-ui-checks", StringComparison.Ordinal))
            return RecoveryUiChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && string.Equals(args[0], "--authorization-pending-ui-checks", StringComparison.Ordinal))
            return AuthorizationPendingUiChecks.Run(Path.GetFullPath(args[1]));
        if (args is ["--sharing-ui-checks", var sharingRoot]) return SharingUiChecks.Run(Path.GetFullPath(sharingRoot));
        if (args.Length == 2 && string.Equals(args[0], "--pair-storage-ui-checks", StringComparison.Ordinal))
            return PairStorageUiChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && string.Equals(args[0], "--source-ui-correction-checks", StringComparison.Ordinal))
            return SourceUiCorrectionChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && string.Equals(args[0], "--local-folder-creation-ui-checks", StringComparison.Ordinal))
            return LocalFolderCreationUiChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && string.Equals(args[0], "--run-history-ui-checks", StringComparison.Ordinal))
            return RunHistoryUiChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 1 && string.Equals(args[0], "--full-tree-selection-contract",
                StringComparison.Ordinal))
            return RunFullTreeSelectionContract();
        if (args.Length == 2 && string.Equals(args[0], "--pairs-ui-contract", StringComparison.Ordinal))
            return RunPairsUiContract(args[1]);
        if (args.Length == 2 && string.Equals(args[0], "--pair-endpoint-mark-checks", StringComparison.Ordinal))
            return RunPairEndpointMarkChecks(args[1]);
        if (args.Length == 2 && string.Equals(args[0], "--internal-attention-icon-checks", StringComparison.Ordinal))
            return RunInternalAttentionIconChecks(args[1]);
        if (args.Length == 2 && string.Equals(args[0], "--discard-presentation-checks", StringComparison.Ordinal))
            return RunDiscardPresentationChecks(args[1]);
        if (args.Length == 2 && string.Equals(args[0], "--shared-details-chrome-checks", StringComparison.Ordinal))
            return RunSharedDetailsChromeChecks(args[1]);
        if (args.Length == 3 && string.Equals(args[0], "--focused-ui-acceptance-contract", StringComparison.Ordinal))
            return FocusedUiAcceptanceContract.Run(args[1], args[2]);
        if (args.Length == 3 && string.Equals(args[0], "--focused-ui-isolation-contract", StringComparison.Ordinal))
            return FocusedUiAcceptanceContract.Run(args[1], args[2], isolationOnly: true);
        if (args.Length == 3 && string.Equals(args[0], "--activity-kind-ui-contract", StringComparison.Ordinal))
            return FocusedUiAcceptanceContract.Run(args[1], args[2], activityKindOnly: true);
        if (args.Length == 1 && string.Equals(args[0], "--nested-update-presentation-contract",
                StringComparison.Ordinal))
        { try { return RunNestedUpdatePresentationContract(); }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length > 0 && string.Equals(args[0], "--folder-browser-pagination-contract", StringComparison.Ordinal))
        {
            if (args.Length != 2) throw new ArgumentException("Provide one supported folder-browser contract mode.");
            return FolderBrowserPaginationContract.Run(args[1]);
        }
        if (args.Length == 1 && string.Equals(args[0], "--real-dpi-probe", StringComparison.Ordinal))
            return ProbeRealDpiProcess();
        if (args.Length == 3 && string.Equals(args[0], "--capture-real-dpi", StringComparison.Ordinal))
            return CaptureRealDpiProcess(args[1], double.Parse(args[2], CultureInfo.InvariantCulture));
        if (args.Length == 3 && string.Equals(args[0], "--popup-dpi", StringComparison.Ordinal))
            return RenderPopupDpiProcess(args[1], double.Parse(args[2], CultureInfo.InvariantCulture));
        if (args.Length == 2 && string.Equals(args[0], "--iteration-ui-checks", StringComparison.Ordinal))
            return IterationUiChecks.Run(args[1]);
        if (args.Length == 2 && args[0] == "--native-explorer-checks")
        { Task.Run(() => NativeExplorerChecks.RunAsync(args[1])).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 2 && args[0] == "--native-explorer-cleanup-checks")
        { Task.Run(() => NativeExplorerChecks.RunAsync(args[1], partialCleanupOnly: true)).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 2 && args[0] == "--native-explorer-approved-checks")
        {
            try { Task.Run(() => NativeExplorerChecks.RunAsync(args[1], approvedLocation: true)).GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.WriteLine("NATIVE approved check failed: " + error); return 1; }
        }
        if (args.Length == 1 && args[0] == "--native-shell-probe")
        {
            try { Task.Run(NativeExplorerChecks.ProbeShellFirstAsync).GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.WriteLine("NATIVE probe failed: " + error.Message); return 1; }
        }
        if (args.Length == 1 && args[0] == "--native-shell-scope")
        { Task.Run(NativeExplorerChecks.ReadSearchScope).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 1 && args[0] == "--cleanup-native-fixtures")
        { NativeExplorerChecks.CleanAbandonedFixtures(); return 0; }
        if (args.Length == 2 && args[0] == "--iteration-renders") return RenderIterationViews(args[1]);
        if (args.Length == 3 && args[0] == "--iteration-renders") return RenderIterationViews(args[1], args[2]);
        if (args.Length == 1 && string.Equals(args[0], "--interactive-inspection", StringComparison.Ordinal))
            return RunInteractiveInspection();
        if (args.Length == 1 && string.Equals(args[0], "--workspace-inspection", StringComparison.Ordinal))
            return RunInteractiveInspection(workspacePilot: true);
        if (args.Length == 1 && string.Equals(args[0], "--workspace-editor-inspection", StringComparison.Ordinal))
            return RunDetailedWorkspaceInspection(seedPending: false);
        if (args.Length == 2 && string.Equals(args[0], "--user-action-feedback-checks", StringComparison.Ordinal))
        { try { return UserActionFeedbackChecks.Run(Path.GetFullPath(args[1])); }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length == 2 && string.Equals(args[0], "--local-root-health-checks", StringComparison.Ordinal))
        { try { return LocalRootHealthChecks.Run(Path.GetFullPath(args[1])); }
          catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
        if (args.Length != 1) throw new ArgumentException("Provide one output directory.");
        var outputRoot = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outputRoot);
        var stateRoot = Path.Combine(Path.GetTempPath(), "sync-shark-ui-fixture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateRoot);

        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, SuppressProductStartup = true };
        app.InitializeComponent();
        _realDpiMonitors = ProbeRealDpiMonitors();
        WriteRealDpiInventory();
        var appLock = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "state", "instance.lock"));
        var theme = new ThemeManager(new UiPreferencesStore(appLock.StateAuthority));
        theme.Initialize();
        try
        {
            AssertConflictDiffContract();
            AssertWindowsStartupContract();
            AssertBackgroundWindowLifecycle(appLock.StateAuthority, theme);
            theme.SetPreferenceAsync(UiPreferencesStore.LightTheme).GetAwaiter().GetResult();
            RenderMainWindow(Path.Combine(outputRoot, "dashboard-light-successor.png"), 1240, 790, appLock.StateAuthority, theme);
            theme.SetPreferenceAsync(UiPreferencesStore.AmoledTheme).GetAwaiter().GetResult();
            RenderMainWindow(Path.Combine(outputRoot, "dashboard-amoled-successor.png"), 1240, 790, appLock.StateAuthority, theme);
            RenderMainWindow(Path.Combine(outputRoot, "dashboard-amoled-maximized-successor.png"), 1920, 1080, appLock.StateAuthority, theme);
            RenderMainWindow(Path.Combine(outputRoot, "dashboard-amoled-compact-successor.png"), 1024, 768, appLock.StateAuthority, theme);
            RenderMainWindow(Path.Combine(outputRoot, "dashboard-amoled-keyboard-focus-successor.png"), 1240, 790, appLock.StateAuthority, theme, focusName: "ResultGrid");
            RenderMainWindow(Path.Combine(outputRoot, "pairs-amoled-successor.png"), 1240, 790, appLock.StateAuthority, theme, pageIndex: 1);
            RenderMainWindow(Path.Combine(outputRoot, "pairs-amoled-maximized-successor.png"), 1920, 1080, appLock.StateAuthority, theme, pageIndex: 1);
            RenderMainWindow(Path.Combine(outputRoot, "pairs-amoled-compact-successor.png"), 1024, 768, appLock.StateAuthority, theme, pageIndex: 1);
            RenderMainWindow(Path.Combine(outputRoot, "pairs-amoled-compact-focus-successor.png"), 1024, 768, appLock.StateAuthority, theme, pageIndex: 1, focusName: "PairNameBox");
            RenderMainWindow(Path.Combine(outputRoot, "connections-amoled-successor.png"), 1240, 790, appLock.StateAuthority, theme, pageIndex: 2);
            RenderMainWindow(Path.Combine(outputRoot, "first-sync-amoled-successor.png"), 1240, 790, appLock.StateAuthority, theme, pageIndex: 3);
            RenderMainWindow(Path.Combine(outputRoot, "activity-amoled-successor.png"), 1240, 790, appLock.StateAuthority, theme, pageIndex: 4);
            RenderMainWindow(Path.Combine(outputRoot, "activity-amoled-compact-successor.png"), 1024, 768, appLock.StateAuthority, theme, pageIndex: 4);
            RenderMainWindow(Path.Combine(outputRoot, "queue-amoled-successor.png"), 1240, 790, appLock.StateAuthority, theme, pageIndex: 5);
            RenderMainWindow(Path.Combine(outputRoot, "queue-amoled-compact-successor.png"), 1024, 768, appLock.StateAuthority, theme, pageIndex: 5);
            RenderMainWindow(Path.Combine(outputRoot, "conflicts-amoled-successor.png"), 1240, 790, appLock.StateAuthority, theme, pageIndex: 6);
            RenderMainWindow(Path.Combine(outputRoot, "conflicts-amoled-compact-focus-successor.png"), 1024, 768, appLock.StateAuthority, theme, pageIndex: 6, focusName: "KeepLocalConflictButton");
            foreach (var dpi in RequiredDpiContexts().Where(HasRealDpiContext))
                RenderPopupDpiInIsolatedProcess(outputRoot, dpi);
            RenderConflictComparisons(outputRoot);
            RenderMainWindow(Path.Combine(outputRoot, "recovery-amoled-successor.png"), 1240, 790, appLock.StateAuthority, theme, pageIndex: 7);
            RenderMainWindow(Path.Combine(outputRoot, "rewind-amoled-successor.png"), 1240, 790, appLock.StateAuthority, theme, pageIndex: 8);
            RenderMainWindow(Path.Combine(outputRoot, "settings-amoled-successor.png"), 1240, 790, appLock.StateAuthority, theme, pageIndex: 9);
            RenderMainWindow(Path.Combine(outputRoot, "settings-amoled-compact-focus-successor.png"), 1024, 768, appLock.StateAuthority, theme, pageIndex: 9, focusName: "AutomationIntervalBox");
            var dpiPages = new[]
            {
                "dashboard", "pairs", "connections", "first-sync", "activity", "queue", "conflicts",
                "recovery", "rewind", "settings"
            };
            foreach (var dpi in RequiredDpiContexts().Where(HasRealDpiContext))
            {
                for (var page = 0; page < dpiPages.Length; page++)
                    RenderMainWindow(Path.Combine(outputRoot,
                            $"{dpiPages[page]}-amoled-context-{dpi:N0}-dpi-successor.png"),
                        1240, 790, appLock.StateAuthority, theme, pageIndex: page,
                        focusName: page switch { 1 => "PairNameBox", 6 => "KeepLocalConflictButton", 9 => "AutomationIntervalBox", _ => null },
                        dpi: dpi);
            }
            RenderBrowser(Path.Combine(outputRoot, "dropbox-browser-amoled-successor.png"));
            if (HasRealDpiContext(144))
                RenderBrowser(Path.Combine(outputRoot, "dropbox-browser-amoled-150-percent-successor.png"), 144);
            if (HasRealDpiContext(192))
                RenderBrowser(Path.Combine(outputRoot, "dropbox-browser-amoled-200-percent-successor.png"), 192);
            ThemeManager.ApplyHighContrastForEvidence();
            RenderMainWindow(Path.Combine(outputRoot, "dashboard-high-contrast-successor.png"), 1240, 790, appLock.StateAuthority, theme);
            RenderMainWindow(Path.Combine(outputRoot, "conflicts-high-contrast-successor.png"), 1240, 790, appLock.StateAuthority, theme, pageIndex: 6, focusName: "KeepDropboxConflictButton");
            Console.WriteLine(JsonSerializer.Serialize(new { metric = "ui_fixture_complete" }));
            return 0;
        }
        finally
        {
            theme.Dispose();
            appLock.Dispose();
            app.Shutdown();
            if (Directory.Exists(stateRoot)) Directory.Delete(stateRoot, true);
        }
    }

    private static int RunInteractiveInspection(bool workspacePilot = false)
    {
        if (workspacePilot) return RunDetailedWorkspaceInspection();
        var stateRoot = Path.Combine(Path.GetTempPath(), "sync-shark-ui-inspection",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateRoot);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, SuppressProductStartup = true };
        app.InitializeComponent();
        var appLock = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "state", "instance.lock"));
        var theme = new ThemeManager(new UiPreferencesStore(appLock.StateAuthority));
        theme.Initialize();
        theme.SetPreferenceAsync(UiPreferencesStore.AmoledTheme).GetAwaiter().GetResult();
        MainWindow.SuppressLoadedStartupForTest = true;
        MainWindow.PairFolderBrowserFactoryForTest = _ =>
            Task.FromResult<IDropboxFolderBrowserService>(new EmbeddedDropboxPagingFixture());
        var localRoot = Path.Combine(stateRoot, "computer", "Documents");
        Directory.CreateDirectory(Path.Combine(localRoot, "Work", "Invoices"));
        Directory.CreateDirectory(Path.Combine(localRoot, "Work", "Photos"));
        Directory.CreateDirectory(Path.Combine(localRoot, "Archive"));
        var pair = new SyncPair("fixture-pair", "Documents ↔ Dropbox", localRoot, 1, 1,
            "/First", "id:first", Enabled: true, Priority: 20, PropagateDeletions: true,
            SelectionRules: [new SyncSelectionRule("Archive", false, "id:archive")],
            SelectionDefaultIncluded: true, InitialReviewApproved: false,
            RecoveryRoot: new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                "id:fixture-recovery", "fixture-root-namespace"));
        var window = new MainWindow(appLock.StateAuthority, theme)
        {
            Width = 1440,
            Height = 900,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        window.Loaded += (_, _) =>
        {
            window.LoadPairSetForTest(new SyncPairSet([pair], pair.Id));
            SeedMainWindow(window);
            if (workspacePilot)
            {
                window.Title = "Sync Shark · visual pilot";
                window.WindowState = WindowState.Maximized;
                var sample = new[]
                {
                    new ActivityRow(DateTimeOffset.Now.AddMinutes(-1).ToString("yyyy-MM-dd HH:mm:ss zzz"), "cloud test.txt", "Completed download", "Downloaded and verified.", "Dropbox: example account", pair.Id, "Pair A", "pilot-text", "File", "TXT"),
                    new ActivityRow(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"), "manual.pdf", "Starting upload", "Uploading a new file to Dropbox. Byte totals are not yet available.", "Dropbox · example account", pair.Id, "Pair A", "pilot-upload", "File", "PDF"),
                    new ActivityRow(DateTimeOffset.Now.AddMinutes(-2).ToString("yyyy-MM-dd HH:mm:ss zzz"), "warranty.pdf", "Completed upload", "Uploaded and verified.", "Dropbox · example account", pair.Id, "Pair A", "pilot-complete", "File", "PDF"),
                    new ActivityRow(DateTimeOffset.Now.AddMinutes(-3).ToString("yyyy-MM-dd HH:mm:ss zzz"), "serial-number.jpg", "Completed download", "Downloaded and verified.", "Dropbox · example account", pair.Id, "Pair A", "pilot-download", "File", "JPG"),
                    new ActivityRow(DateTimeOffset.Now.AddMinutes(-4).ToString("yyyy-MM-dd HH:mm:ss zzz"), "old-receipt.pdf", "Recoverable deletion", "A displaced copy is retained in Recovery.", "Dropbox · example account", pair.Id, "Pair A", "pilot-recovery", "File", "PDF")
                };
                window.ActivityRows.Clear();
                var track = typeof(MainWindow).GetMethod("TrackWorkspaceAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
                foreach (var row in sample) window.ActivityRows.Add(row);
                foreach (var row in sample.Reverse()) track.Invoke(window, [row]);
                typeof(MainWindow).GetField("_globalPendingQueueCount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, 2);
                var tray = new TrayFlyoutWindow(window);
                var toggle = new Button { Content = "Tray pilot", HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 12, 4) };
                toggle.Click += (_, _) => tray.ToggleAtTray();
                var layout = (Grid)window.Content;
                Grid.SetColumn(toggle, 0); Grid.SetRow(toggle, 1); layout.Children.Add(toggle);
            }
        };
        try
        {
            app.Run(window);
            return 0;
        }
        finally
        {
            MainWindow.SuppressLoadedStartupForTest = false;
            MainWindow.PairFolderBrowserFactoryForTest = null;
            appLock.Dispose();
        }
    }

    private static int RunFullTreeSelectionContract()
    {
        var localRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Selection fixture");
        var work = Path.Combine(localRoot, "Work");
        var invoices = Path.Combine(work, "Invoices");
        var photos = Path.Combine(work, "Photos");
        var privatePhotos = Path.Combine(photos, "Private");
        var local = new FolderTreeSelectionDraft(FolderTreePathKind.Windows);

        local.Set(invoices, included: true);
        var one = local.CreatePlan() ?? throw new InvalidDataException("One selected folder produced no pair.");
        if (!string.Equals(one.RootPath, invoices, StringComparison.OrdinalIgnoreCase) ||
            !one.DefaultIncluded || one.Rules.Count != 0 || local.CheckState(invoices) != true)
            throw new InvalidDataException("One selected folder did not become the complete pair root.");

        local.Set(photos, included: true);
        var siblings = local.CreatePlan() ?? throw new InvalidDataException("Sibling folders produced no pair.");
        var siblingPolicy = SyncSelectionPolicy.Compile(siblings.Rules, siblings.DefaultIncluded);
        if (!string.Equals(siblings.RootPath, work, StringComparison.OrdinalIgnoreCase) ||
            siblings.DefaultIncluded || local.CheckState(work) is not null ||
            !siblingPolicy.IsIncluded("Invoices/a.pdf") || !siblingPolicy.IsIncluded("Photos/a.jpg") ||
            siblingPolicy.IsIncluded("Future/a.txt"))
            throw new InvalidDataException("Sibling ticks did not derive a dashed shared parent.");

        local.Set(work, included: true);
        local.Set(privatePhotos, included: false);
        var excludedChild = local.CreatePlan() ??
                            throw new InvalidDataException("A child exclusion removed the pair.");
        var exclusionPolicy = SyncSelectionPolicy.Compile(excludedChild.Rules,
            excludedChild.DefaultIncluded);
        if (!excludedChild.DefaultIncluded || local.CheckState(work) is not null ||
            local.CheckState(privatePhotos) != false || !exclusionPolicy.IsIncluded("Photos/public.jpg") ||
            exclusionPolicy.IsIncluded("Photos/Private/secret.jpg"))
            throw new InvalidDataException("A child exclusion did not keep a dashed selected parent.");

        local.Set(privatePhotos, included: true);
        var restored = local.CreatePlan() ?? throw new InvalidDataException("Reticking a child removed the pair.");
        if (!restored.DefaultIncluded || restored.Rules.Count != 0 || local.CheckState(work) != true)
            throw new InvalidDataException("Reticking an excluded child did not restore the complete subtree.");

        var dropbox = new FolderTreeSelectionDraft(FolderTreePathKind.Dropbox);
        dropbox.Load("/Work", excludedChild.DefaultIncluded, excludedChild.Rules);
        local.Load(work, excludedChild.DefaultIncluded, excludedChild.Rules);
        if (!FolderTreeSelectionDraft.Equivalent(local.CreatePlan(), dropbox.CreatePlan()))
            throw new InvalidDataException("Matching local and Dropbox trees were not accepted as two-way.");
        dropbox.Set("/Work/Other", included: false, "dropbox:id:other");
        if (FolderTreeSelectionDraft.Equivalent(local.CreatePlan(), dropbox.CreatePlan()))
            throw new InvalidDataException("Different local and Dropbox ticks were accepted as two-way.");

        AssertCoalescedLocalFolderLoad();
        AssertFullTreeEditorSurface();

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result = "PASS",
            selector = "--full-tree-selection-contract",
            cases = new[]
            {
                "single-root", "sibling-lca", "tri-state-exclusion", "restore", "two-way-gate",
                "full-tree-ui", "right-chevron", "folder-accessibility", "visible-search-filter",
                "broken-pair-repair", "linked-promotion-rollback", "terminal-health",
                "responsive-resize-sequence", "clean-add-cancel", "coalesced-local-folder-load"
            }
        }));
        return 0;
    }

    private static void AssertCoalescedLocalFolderLoad()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-shark-coalesced-local-load",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "A"));
        Directory.CreateDirectory(Path.Combine(root, "B"));
        try
        {
            var node = LocalFolderNode.CreateUnloadedForTest(root);
            var first = node.LoadChildrenAsync();
            var second = node.LoadChildrenAsync();
            if (!ReferenceEquals(first, second))
                throw new InvalidDataException("Concurrent local folder expansion did not await one shared load.");
            var combined = Task.WhenAll(first, second);
            if (!combined.IsCompleted)
            {
                var frame = new DispatcherFrame();
                var timeout = Stopwatch.StartNew();
                var timer = new DispatcherTimer(DispatcherPriority.Send, Dispatcher.CurrentDispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(10)
                };
                timer.Tick += (_, _) =>
                {
                    if (combined.IsCompleted || timeout.Elapsed > TimeSpan.FromSeconds(15))
                        frame.Continue = false;
                };
                timer.Start();
                Dispatcher.PushFrame(frame);
                timer.Stop();
                if (!combined.IsCompleted)
                    throw new TimeoutException("The shared local folder load did not complete.");
            }
            combined.GetAwaiter().GetResult();
            if (!node.IsLoaded || node.Children.Count(item => !item.IsPlaceholder) != 2)
                throw new InvalidDataException("The shared local folder load did not publish the complete children.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void AssertFullTreeEditorSurface()
    {
        var stateRoot = Path.Combine(Path.GetTempPath(), "sync-shark-full-tree-ui",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateRoot);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, SuppressProductStartup = true };
        app.InitializeComponent();
        var appLock = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "state", "instance.lock"));
        var theme = new ThemeManager(new UiPreferencesStore(appLock.StateAuthority));
        theme.Initialize();
        try
        {
            var window = new MainWindow(appLock.StateAuthority, theme) { Width = 1100, Height = 760 };
            window.Show();
            // The test replaces the loaded pair set with unsaved fixture pairs.
            // Wait for the real initial load first, or its later database refresh
            // can remove those fixtures (and their editor) during the resize proof.
            var startupReady = typeof(MainWindow).GetField("_explorerStartupReady",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            PumpUntil(window, () => startupReady.GetValue(window) is true,
                "Initial startup did not finish before the isolated editor fixture was applied.");
            if (window.FindName("Navigation") is ListBox navigation)
                navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>()
                    .Single(item => Equals(item.Tag, "Pairs"));
            PumpUntil(window, () => window.LocalRootNodes.Count > 0,
                "The complete local filesystem roots did not load in the pair editor.");
            window.UpdateLayout();
            if (window.FindName("LocalRootTree") is not TreeView { Visibility: Visibility.Visible } localTree ||
                window.FindName("DropboxRootTree") is not TreeView { Visibility: Visibility.Visible } dropboxTree ||
                window.FindName("LocalFolderSelectionTree") is not TreeView
                    { Visibility: Visibility.Collapsed } ||
                window.FindName("DropboxFolderSelectionTree") is not TreeView
                    { Visibility: Visibility.Collapsed })
                throw new InvalidDataException(
                    "The editor did not use the complete local and Dropbox trees as its only folder surface.");

            localTree.BringIntoView();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var checkbox = FindVisualChildren<CheckBox>(localTree)
                .FirstOrDefault(item => item.DataContext is LocalFolderNode);
            var chevron = FindVisualChildren<ToggleButton>(localTree)
                .FirstOrDefault(item => item is not CheckBox && item.DataContext is LocalFolderNode);
            if (checkbox is null || chevron is null || checkbox.Visibility != Visibility.Visible ||
                chevron.Visibility != Visibility.Visible)
                throw new InvalidDataException("Folder rows do not expose both a checkbox and a chevron.");
            var checkboxX = checkbox.TransformToAncestor(localTree).Transform(new Point()).X;
            var chevronX = chevron.TransformToAncestor(localTree).Transform(new Point()).X;
            if (chevronX <= checkboxX)
                throw new InvalidDataException(
                    $"The folder chevron is not on the right side of its checkbox row (checkbox={checkboxX:F1}, chevron={chevronX:F1}).");
            if (checkbox.DataContext is not LocalFolderNode checkboxNode ||
                !AutomationProperties.GetName(checkbox).Contains(checkboxNode.Name, StringComparison.Ordinal) ||
                !AutomationProperties.GetName(checkbox).Contains("computer", StringComparison.OrdinalIgnoreCase) ||
                chevron.DataContext is not LocalFolderNode chevronNode ||
                !AutomationProperties.GetName(chevron).Contains(chevronNode.Name, StringComparison.Ordinal) ||
                !(AutomationProperties.GetName(chevron).StartsWith("Expand ", StringComparison.OrdinalIgnoreCase) ||
                  AutomationProperties.GetName(chevron).StartsWith("Collapse ", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    "Folder checkboxes and right-side chevrons do not identify their folder and action to assistive technology.");

            window.ApplyFolderTreeFilterForTest("__no_loaded_folder_can_match__", 0);
            if (window.LocalRootNodes.Any(node => node.IsFilterVisible) ||
                window.FindName("FolderSearchScopeText") is not TextBlock noResults ||
                !noResults.Text.Contains("No loaded folders match", StringComparison.Ordinal))
                throw new InvalidDataException("Folder search did not filter the visible complete-tree models or show a no-results state.");
            window.ApplyFolderTreeFilterForTest(string.Empty, 0);
            if (window.LocalRootNodes.Any(node => !node.IsFilterVisible))
                throw new InvalidDataException("Clearing folder search did not restore the complete folder tree.");

            var replacement = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Windows");
            var missing = Path.Combine(Path.GetPathRoot(replacement)!,
                "SyncSharkMissing-" + Guid.NewGuid().ToString("N"));
            var brokenPair = new SyncPair("0123456789abcdefabcd", "Broken pair", missing, 1, 1,
                "/Fixture", "id:fixture", SelectionRules: [], SelectionDefaultIncluded: true);
            window.PrepareExistingPairEditorForTest(brokenPair);
            try
            {
                PumpUntil(window, () =>
                        window.FindName("LocalHierarchyNotice") is Border { Visibility: Visibility.Visible } &&
                        localTree.Items.Count > 0 &&
                        ReferenceEquals(localTree.ItemsSource, window.LocalRootNodes) &&
                        ReferenceEquals(dropboxTree.ItemsSource, window.DropboxRootNodes) &&
                        window.LocalRootNodes.SelectMany(root => root.Children).Any(node =>
                            string.Equals(node.Path, replacement, StringComparison.OrdinalIgnoreCase)),
                    "A broken pair did not retain the rendered complete trees or open at its deepest surviving ancestor.");
            }
            catch (TimeoutException exception)
            {
                var loadedLocalPaths = string.Join(", ", window.LocalRootNodes.SelectMany(root => root.Children)
                    .Where(node => !node.IsPlaceholder).Select(node => node.Path));
                throw new TimeoutException(
                    $"{exception.Message} Notice={((Border)window.FindName("LocalHierarchyNotice")!).Visibility}; " +
                    $"localItems={localTree.Items.Count}; dropboxItems={dropboxTree.Items.Count}; " +
                    $"localSource={ReferenceEquals(localTree.ItemsSource, window.LocalRootNodes)}; " +
                    $"dropboxSource={ReferenceEquals(dropboxTree.ItemsSource, window.DropboxRootNodes)}; " +
                    $"children=[{loadedLocalPaths}]", exception);
            }
            window.SetGlobalLocalSelectionForTest(replacement, include: true);
            PumpUntil(window, () => string.Equals(window.GlobalDraftPlanForTest?.LocalRoot, replacement,
                    StringComparison.OrdinalIgnoreCase),
                "Ticking a replacement did not repair the broken pair draft.");
            if (window.FindName("LocalHierarchyNotice") is not Border { Visibility: Visibility.Collapsed } ||
                !string.Equals(window.GlobalDraftPlanForTest?.DropboxRoot.Path, "/Fixture",
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Repairing the local side did not preserve the paired Dropbox selection or clear the warning.");

            var mismatchedNamesPair = new SyncPair("1123456789abcdefabcd", "Different names", replacement,
                1, 1, "/temp", "id:temp", SelectionRules: [], SelectionDefaultIncluded: true);
            window.PrepareExistingPairEditorForTest(mismatchedNamesPair);
            PumpUntil(window, () => window.GlobalDraftPlanForTest is not null,
                "The differently named existing pair did not establish a valid linked selection.");
            var sibling = Path.Combine(Path.GetPathRoot(replacement)!, "ProgramData");
            window.SetGlobalLocalSelectionForTest(sibling, include: true);
            if (!string.Equals(window.GlobalDraftPlanForTest?.LocalRoot, replacement,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(window.GlobalDraftPlanForTest?.DropboxRoot.Path, "/temp",
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "An unrepresentable sibling promotion stranded or broadened a valid differently named pair.");

            PumpTask(window, window.RefreshPairSyncStatusForTestAsync());
            if (((TextBlock)window.FindName("PairHealthLocalText")!).Text.Contains("Checking", StringComparison.Ordinal) ||
                ((TextBlock)window.FindName("PairHealthDropboxText")!).Text.Contains("Checking", StringComparison.Ordinal) ||
                !((Button)window.FindName("EditorSyncNowButton")!).IsEnabled ||
                ((TextBlock)window.FindName("PairHealthReasonText")!).Text.Contains("no longer exists",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "A transient pair-health failure remained on Checking, blocked Sync now, or falsely marked a saved folder missing.");

            AssertPairEditorResizeSequence(window);

            ((TabControl)window.FindName("PairEditorTabs")!).SelectedIndex = 4;
            window.ApplyFolderTreeFilterForTest("Windows", 5);
            // This test deliberately discards its unsaved fixture draft; the
            // production navigation guard must still execute that choice.
            window.UnsavedNavigationChoiceForTest = () => AppDialogChoice.Secondary;
            ((Button)window.FindName("AddPairButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (((TabControl)window.FindName("PairEditorTabs")!).SelectedIndex != 0 ||
                ((TextBox)window.FindName("FolderSelectionSearchBox")!).Text.Length != 0 ||
                ((ComboBox)window.FindName("FolderSelectionFilterBox")!).SelectedIndex != 0)
                throw new InvalidDataException("Add pair retained an unrelated tab, search, or folder filter.");
            ((Button)window.FindName("CancelPairButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (((ContentControl)window.FindName("NewPairEditorHost")!).Visibility != Visibility.Collapsed ||
                ((DataGrid)window.FindName("PairList")!).SelectedItem is not null)
                throw new InvalidDataException("Cancelling a new pair opened an unrelated saved-pair editor.");
            window.RequestProductExit();
        }
        finally
        {
            theme.Dispose();
            appLock.Dispose();
            app.Shutdown();
            if (Directory.Exists(stateRoot)) Directory.Delete(stateRoot, true);
        }
    }

    private static void AssertPairEditorResizeSequence(MainWindow window)
    {
        foreach (var width in new[] { 1240d, 1800d, 1240d, 760d, 1800d })
        {
            window.Width = width;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            window.UpdateLayout();
            if (window.FindName("PairsScroller") is not ScrollViewer scroller ||
                window.FindName("PairRootGrid") is not Grid rootGrid ||
                window.FindName("PairEditorPanel") is not Border editor ||
                window.FindName("LocalRootPanel") is not Border local ||
                window.FindName("DropboxRootPanel") is not Border dropbox)
                throw new InvalidDataException("The responsive pair-editor surfaces are missing.");
            // Responsive work is coalesced at Background priority after the new
            // viewport is measured. Observe that real dispatcher path before
            // asserting geometry, rather than only draining Loaded priority.
            try
            {
                PumpUntil(window, () =>
                {
                    window.UpdateLayout();
                    return editor.IsVisible && editor.IsLoaded && rootGrid.IsMeasureValid &&
                           rootGrid.IsArrangeValid && rootGrid.ActualWidth <= scroller.ViewportWidth + 1;
                }, $"The pair editor did not settle after resize to {width:F0}px.");
            }
            catch (TimeoutException error)
            {
                throw new InvalidDataException($"{error.Message} viewport={scroller.ViewportWidth:F1}; " +
                    $"editor={editor.Width:F1}/{editor.ActualWidth:F1}; root={rootGrid.ActualWidth:F1}; " +
                    $"local={local.ActualWidth:F1}/row{Grid.GetRow(local)}; cloud={dropbox.ActualWidth:F1}/row{Grid.GetRow(dropbox)}; " +
                    $"passes={window.ResponsiveLayoutPasses}; visible={editor.IsVisible}; loaded={editor.IsLoaded}; " +
                    $"parent={editor.Parent?.GetType().Name}; measure={editor.IsMeasureValid}; arrange={editor.IsArrangeValid}; " +
                    $"page={((FrameworkElement)window.FindName("PairsPage")).Visibility}; host={((FrameworkElement)window.FindName("NewPairEditorHost")).Visibility}", error);
            }
            if (rootGrid.ActualWidth > scroller.ViewportWidth + 1)
                throw new InvalidDataException(
                    $"The pair editor retained a stale width after resize to {width:F0}px " +
                    $"(editor={rootGrid.ActualWidth:F1}; viewport={scroller.ViewportWidth:F1}).");

            var localBounds = BoundsWithin(local, editor);
            var dropboxBounds = BoundsWithin(dropbox, editor);
            if (localBounds.IntersectsWith(dropboxBounds))
                throw new InvalidDataException(
                    $"The local and Dropbox panes overlap after resize to {width:F0}px: " +
                    $"local={localBounds}; dropbox={dropboxBounds}.");
            AssertInside(localBounds, editor, width, "local folder pane");
            AssertInside(dropboxBounds, editor, width, "Dropbox folder pane");
        }
    }

    private static Rect BoundsWithin(FrameworkElement element, FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(new Point(), element.RenderSize));

    private static void AssertInside(Rect bounds, FrameworkElement ancestor, double requestedWidth,
        string description)
    {
        if (bounds.Left < -0.5 || bounds.Top < -0.5 || bounds.Right > ancestor.ActualWidth + 0.5 ||
            bounds.Bottom > ancestor.ActualHeight + 0.5)
            throw new InvalidDataException(
                $"The {description} escaped the pair editor after resize to {requestedWidth:F0}px: " +
                $"bounds={bounds}; editor={ancestor.ActualWidth:F1}x{ancestor.ActualHeight:F1}.");
    }

    private static int RunPairsUiContract(string rawOutputRoot)
    {
        var priorStartup = MainWindow.SuppressLoadedStartupForTest;
        MainWindow.SuppressLoadedStartupForTest = true;
        var outputRoot = Path.GetFullPath(rawOutputRoot);
        Directory.CreateDirectory(outputRoot);
        var stateRoot = Path.Combine(Path.GetTempPath(), "sync-shark-pairs-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateRoot);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, SuppressProductStartup = true };
        app.InitializeComponent();
        var appLock = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "state", "instance.lock"));
        var theme = new ThemeManager(new UiPreferencesStore(appLock.StateAuthority));
        theme.Initialize();
        try
        {
            theme.SetPreferenceAsync(UiPreferencesStore.AmoledTheme).GetAwaiter().GetResult();
            AssertPairDraftBindingPlans();
            AssertPairsWindowShell(appLock.StateAuthority, theme);
            AssertPairsInteractiveContract(appLock.StateAuthority, theme);
            void PreparePairsRender(MainWindow value)
            {
                var navigation = (ListBox)value.FindName("Navigation");
                navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>()
                    .Single(item => Equals(item.Tag, "Pairs"));
                value.PrepareExistingPairEditorForTest(new SyncPair("pairs-render", "Example pair",
                    @"C:\Fixture\Documents", 1, 1, "/Documents", "id:render"));
                ((Expander)value.FindName("PairIdentityDisclosure")).IsExpanded = true;
                value.UpdateLayout();
                value.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                value.UpdateLayout();
            }
            RenderMainWindow(Path.Combine(outputRoot, "pairs-amoled-1240x790.png"), 1240, 790,
                appLock.StateAuthority, theme, focusName: "PairNameBox", prepare: PreparePairsRender);
            RenderMainWindow(Path.Combine(outputRoot, "pairs-amoled-1920x1040.png"), 1920, 1040,
                appLock.StateAuthority, theme, prepare: PreparePairsRender);
            RenderMainWindow(Path.Combine(outputRoot, "pairs-amoled-760x700.png"), 760, 700,
                appLock.StateAuthority, theme, focusName: "PairNameBox", prepare: PreparePairsRender);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result = "PASS",
                selector = "--pairs-ui-contract",
                renders = new[]
                {
                    "pairs-amoled-760x700.png", "pairs-amoled-1240x790.png", "pairs-amoled-1920x1040.png"
                }
            }));
            return 0;
        }
        finally
        {
            MainWindow.SuppressLoadedStartupForTest = priorStartup;
            theme.Dispose();
            appLock.Dispose();
            app.Shutdown();
            if (Directory.Exists(stateRoot)) Directory.Delete(stateRoot, true);
        }
    }

    private static void AssertPairsWindowShell(PilotStateAuthority authority, ThemeManager theme)
    {
        var window = new MainWindow(authority, theme) { Width = 1100, Height = 720 };
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        if (window.FindName("SidebarToggle") is not Button toggle ||
            window.FindName("NavigationColumn") is not ColumnDefinition navigationColumn)
            throw new InvalidDataException("The collapsible sidebar controls are missing.");
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        window.UpdateLayout();
        if (Math.Abs(navigationColumn.ActualWidth - 64d) > 1d)
            throw new InvalidDataException("The sidebar did not collapse to its compact width.");

        window.WindowState = WindowState.Maximized;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, 0x00000002);
        var monitorInfo = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
            throw new InvalidDataException("The maximized window monitor work area could not be read.");
        var dpi = GetDpiForWindow(handle);
        var workHeight = (monitorInfo.Work.Bottom - monitorInfo.Work.Top) * 96d / dpi;
        if (window.ActualHeight > workHeight + 2)
            throw new InvalidDataException(
                $"The maximized window overlaps the Windows work area (height={window.ActualHeight:F1}; work={workHeight:F1}).");
        window.RequestProductExit();
    }

    private static void AssertPairDraftBindingPlans()
    {
        var preview = PairDraftPreview();
        var single = MainWindow.CreateDraftBindingPlan(preview,
            [new SyncSelectionRule("Work/Invoices", true, "id:local-invoices")], false);
        if (!string.Equals(single.LocalRoot, Path.Combine(preview.LocalBrowseRoot, "Work", "Invoices"),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(single.DropboxRoot.Path, "/Browse/Work/Invoices", StringComparison.Ordinal) ||
            !single.DefaultIncluded || single.Rules.Count != 0)
            throw new InvalidDataException(
                "A single selected folder did not become the complete two-way root, including its direct and future content.");

        var siblings = MainWindow.CreateDraftBindingPlan(preview,
        [
            new SyncSelectionRule("Work/Invoices", true, "id:local-invoices"),
            new SyncSelectionRule("Work/Photos", true, "id:local-photos")
        ], false);
        var siblingSelection = SyncSelectionPolicy.Compile(siblings.Rules, siblings.DefaultIncluded);
        if (!string.Equals(siblings.LocalRoot, Path.Combine(preview.LocalBrowseRoot, "Work"),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(siblings.DropboxRoot.Path, "/Browse/Work", StringComparison.Ordinal) ||
            siblings.DefaultIncluded || !siblingSelection.IsIncluded("Invoices/2026.pdf") ||
            !siblingSelection.IsIncluded("Photos/current.jpg") || siblingSelection.IsIncluded("root-file.txt") ||
            siblingSelection.IsIncluded("FutureSibling/new.txt"))
            throw new InvalidDataException(
                "Sibling selections did not retain their real common parent while excluding parent files and future siblings.");

        var unmatched = MainWindow.CreateDraftBindingPlan(preview,
            [new SyncSelectionRule("Local only", true, "id:local-only")], false);
        if (!string.Equals(unmatched.LocalRoot, preview.LocalBrowseRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(unmatched.DropboxRoot.FolderId, preview.DropboxBrowseRoot.FolderId,
                StringComparison.Ordinal) || unmatched.DefaultIncluded || unmatched.Rules.Count != 1)
            throw new InvalidDataException(
                "A local-only selection invented an unverified Dropbox counterpart or narrowed the bound roots.");

        var duplicateNames = new[] { "Copy of Photos", "copy of photos (2)" };
        if (!string.Equals(MainWindow.NextDuplicatePairName("Photos", duplicateNames), "Copy of Photos (3)",
                StringComparison.Ordinal))
            throw new InvalidDataException("Duplicate pair names did not advance without case-insensitive collisions.");

        var source = new SyncPair("0123456789abcdefabcd", "Photos", preview.LocalBrowseRoot, 1, 1,
            preview.DropboxBrowseRoot.Path, preview.DropboxBrowseRoot.FolderId);
        if (MainWindow.DuplicateEndpointsDiffer(source, preview.LocalBrowseRoot, preview.DropboxBrowseRoot) ||
            !MainWindow.DuplicateEndpointsDiffer(source, Path.Combine(preview.LocalBrowseRoot, "Work"),
                new DropboxFolderEntry("Work", "/Browse/Work", "id:dropbox-work")))
            throw new InvalidDataException("Duplicate endpoint protection did not distinguish identical and changed roots.");

        var counts = MainWindow.CountPreviewOperations(
        [
            "create_remote", "update_remote", "create_local", "update_local", "delete_remote", "delete_local",
            "move_remote", "move_local", "reconcile"
        ]);
        if (counts != new PairPreviewCounts(2, 2, 2, 2, 1))
            throw new InvalidDataException("Sync preview categories overlap or omit an operation.");

        var recovery = new DropboxFolderEntry("Recovery", "/Apps/Sync Shark/Recovery", "id:recovery");
        if (!MainWindow.PairRootsAreDisjoint(preview.LocalBrowseRoot, preview.DropboxBrowseRoot,
                recovery, [], null) ||
            !MainWindow.PairRootsAreDisjoint(preview.LocalBrowseRoot,
                new DropboxFolderEntry("Dropbox", "/", "ns:fixture-root"),
                recovery, [], null) ||
            MainWindow.PairRootsAreDisjoint(preview.LocalBrowseRoot,
                new DropboxFolderEntry("Browse", "/Browse", "id:browse"),
                new DropboxFolderEntry("Browse", "/Browse", "id:same"), [], null) ||
            MainWindow.PairRootsAreDisjoint(preview.LocalBrowseRoot,
                new DropboxFolderEntry("Child", "/Browse/Child", "id:child"),
                new DropboxFolderEntry("Browse", "/Browse", "id:ancestor"), [], null))
            throw new InvalidDataException("Pair save admission did not enforce safe Recovery placement.");

        if (!MainWindow.FilesOnDemandValueForBinding(currentValue: true, requestedValue: false) ||
            MainWindow.FilesOnDemandValueForBinding(currentValue: false, requestedValue: false) ||
            !MainWindow.FilesOnDemandValueForBinding(currentValue: false, requestedValue: true))
            throw new InvalidDataException(
                "A failed available-offline transition could change storage before pair settings were saved.");

        var attention = new PairRow(source.Id, source.Name, source.LocalRoot, source.DropboxRoot,
            "Dropbox", false, 0, false, true, "Off", NeedsAttention: true,
            AttentionReason: "Local folder unavailable: C:\\Missing.");
        if (attention.Status != "Needs attention" || attention.LiveSyncLabel != "Pair Paused" ||
            !attention.StatusDetail.Contains("Local folder unavailable", StringComparison.Ordinal))
            throw new InvalidDataException("Pair pause and Needs attention were not represented independently.");
        attention.UpdateHealth(false, "This pair is paused.");
        if (attention.NeedsAttention || attention.Status == "Needs attention" ||
            attention.StatusDetail != "This pair is paused.")
            throw new InvalidDataException("A repaired pair retained a stale Needs attention presentation.");
        var warning = MainWindow.DeletePairWarning(attention);
        if (!warning.Contains("Files on this computer and in the cloud are not deleted", StringComparison.Ordinal) ||
            !warning.Contains("Recovery and activity history remain available", StringComparison.Ordinal) ||
            !warning.Contains("Online-only files will be downloaded first", StringComparison.Ordinal))
            throw new InvalidDataException("Delete pair did not state its non-destructive file and recovery effects.");
    }

    private static void AssertPairsInteractiveContract(PilotStateAuthority authority, ThemeManager theme)
    {
        var window = new MainWindow(authority, theme) { Width = 760, Height = 700 };
        // These account-less synthetic editor inputs are intentionally not saveable. Route a draft
        // guard through its existing test seam rather than waiting for an unattended modal; this
        // contract checks row interaction, not the independently exercised unsaved-change choice.
        window.UnsavedNavigationChoiceForTest = () => AppDialogChoice.Secondary;
        window.Show();
        if (window.FindName("Navigation") is ListBox navigation)
            navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>()
                .Single(item => Equals(item.Tag, "Pairs"));
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        window.PreparePairDraftForTest(PairDraftPreview());
        window.UpdateLayout();

        if (window.FindResource("FolderIconTemplate") is not DataTemplate ||
            window.FindResource("DropboxIconTemplate") is not DataTemplate)
            throw new InvalidDataException("The pair editor is missing its folder or Dropbox icon template.");
        foreach (var name in new[]
                 {
                     "PairHealthOverallText", "PairHealthReasonText", "PairHealthLocalText",
                     "PairHealthDropboxText", "PairHealthLiveSyncText", "PairHealthMonitorText",
                     "PairHealthFirstSyncText", "PairHealthPendingText", "PairHealthConflictsText",
                     "PairHealthRecoveryText", "LocalHierarchyNotice", "DropboxHierarchyNotice",
                     "LocalHierarchyEmptyState", "DropboxHierarchyEmptyState"
                 })
            if (window.FindName(name) is not FrameworkElement)
                throw new InvalidDataException($"The pair Sync status surface is missing {name}.");

        var syncTypes = window.FindName("SyncTypeBox") as ComboBox ??
                        throw new InvalidDataException("The pair sync type control is missing.");
        // One-way directions became supported pair settings in the product's pair-workflow polish, so the
        // control now offers all three modes as real, enabled selections instead of the earlier disabled
        // "coming later" placeholders this expectation was written against.
        if (syncTypes.Items.Count != 3 || syncTypes.Items.Cast<ComboBoxItem>().Any(item => !item.IsEnabled) ||
            syncTypes.Items[0] is not ComboBoxItem { Content: "Two-Way" } ||
            syncTypes.Items[1] is not ComboBoxItem { Content: "Upload Only" } ||
            syncTypes.Items[2] is not ComboBoxItem { Content: "Download Only" })
            throw new InvalidDataException("The pair sync type control does not offer all three real sync modes.");

        window.Width = 1400;
        window.UpdateLayout();
        AssertSharedFolderSelectionState(window, "Work/Invoices", expected: false);
        window.SetDraftFolderSelectionForTest("Work/Invoices", true);
        window.UpdateLayout();
        AssertSharedFolderSelectionState(window, "Work/Invoices", expected: true);
        window.SetDraftFolderSelectionForTest("Work/Invoices", false);
        window.UpdateLayout();
        AssertSharedFolderSelectionState(window, "Work/Invoices", expected: false);

        var pair = new SyncPair("0123456789abcdefabcd", "Editable pair",
            Path.Combine(Path.GetTempPath(), "sync-shark-fixture", "final-state-successor", "acceptance", "local"),
            1, 1, "/Sync Shark Pilot", "id:fixture", SelectionRules: [], SelectionDefaultIncluded: false);
        window.PrepareExistingPairEditorForTest(pair);
        window.UpdateLayout();
        PumpUntil(window, () => FindVisualChildren<ContentControl>(window).Any(item =>
                string.Equals(item.Name, "PairRowDetailsHost", StringComparison.Ordinal) &&
                ReferenceEquals(item.Content, window.FindName("PairEditorPanel"))),
            "Selecting an existing pair did not expand its in-row editor.");
        PumpUntil(window, () =>
                window.FindName("LocalRootPanel") is Border { ActualWidth: > 0 } &&
                window.FindName("DropboxRootPanel") is Border { ActualWidth: > 0 },
            "A saved pair did not render both folder panes.");
        // The synthetic draft helper supplies selection inputs only; it does not open the editor.
        // Assert the real affordances after the saved-row entry point has attached and laid it out.
        foreach (var name in new[] { "PairSearchIcon", "PairSearchCue", "FolderSearchIcon", "FolderSearchCue" })
            if (window.FindName(name) is not FrameworkElement { Visibility: Visibility.Visible } element ||
                !element.IsVisible || element.ActualWidth <= 0)
                throw new InvalidDataException($"The permanent search affordance {name} is not visible.");
        var localRootPanel = (Border)window.FindName("LocalRootPanel")!;
        var dropboxRootPanel = (Border)window.FindName("DropboxRootPanel")!;
        if (Math.Abs(localRootPanel.ActualWidth - dropboxRootPanel.ActualWidth) > 1)
            throw new InvalidDataException(
                "A saved pair did not retain the same balanced side-by-side folder layout as a new pair. " +
                $"local={localRootPanel.ActualWidth:F1}; dropbox={dropboxRootPanel.ActualWidth:F1}; " +
                $"rootGrid={((Grid)window.FindName("PairRootGrid")!).ActualWidth:F1}; " +
                $"viewport={((ScrollViewer)window.FindName("PairsScroller")!).ViewportWidth:F1}.");
        var pairRow = window.PairRows.Single(item => item.Id == pair.Id);
        var statusButton = FindVisualChildren<Button>(window).SingleOrDefault(item =>
            string.Equals(AutomationProperties.GetName(item), "Open pair sync status", StringComparison.Ordinal) &&
            ReferenceEquals(item.DataContext, pairRow));
        if (statusButton?.Content is not Border statusPill ||
            !FindVisualChildren<TextBlock>(statusPill).Any(item =>
                string.Equals(item.Text, pairRow.Status, StringComparison.Ordinal)) ||
            !FindVisualChildren<TextBlock>(statusPill).Any(item =>
                string.Equals(item.Text, pairRow.StatusSummary, StringComparison.Ordinal)))
            throw new InvalidDataException(
                "Expanding a saved pair replaced its coloured Sync status and brief description with a chevron.");

        var emptyDropboxTree = new SyncFolderSelectionTree(pair.Id, [], 0, 0, 0, 0,
            new SyncFolderRollup(0, 0, 0, 0, 0, 0),
            LocalHierarchyAvailable: false,
            LocalHierarchyMessage: "Local folder unavailable: C:\\Fixture\\Local. Choose another folder to repair this pair.",
            DropboxHierarchyAvailable: true,
            DropboxHierarchyMessage: null,
            LocalRootFiles: null,
            DropboxRootFiles: 1);
        ((TextBox)window.FindName("LocalRootBox")!).Text = pair.LocalRoot;
        ((TextBox)window.FindName("DropboxRootBox")!).Text = pair.DropboxRoot;
        window.ApplyFolderHierarchyPresentationForTest(emptyDropboxTree);
        window.UpdateLayout();
        // The production heading is the provider title, then " - ", then the cloud folder's own
        // display name (HierarchyPorts then ApplyHierarchyRootPresentation). This pair is the
        // fixture's Dropbox pair rooted at /Sync Shark Pilot.
        const string expectedDropboxHeading = "Dropbox - Sync Shark Pilot";
        if (window.FindName("LocalHierarchyEmptyState") is not Border { Visibility: Visibility.Collapsed } ||
            window.FindName("DropboxHierarchyEmptyState") is not Border { Visibility: Visibility.Visible } ||
            window.FindName("DropboxHierarchyRootNameText") is not TextBlock dropboxHierarchyHeading ||
            !string.Equals(dropboxHierarchyHeading.Text, expectedDropboxHeading, StringComparison.Ordinal) ||
            window.FindName("DropboxHierarchyEmptyStateText") is not TextBlock dropboxRootState ||
            !dropboxRootState.Text.Contains("1 file at this level", StringComparison.Ordinal) ||
            window.FindName("FolderSelectionSummary") is not TextBlock hierarchySummary ||
            !hierarchySummary.Text.Contains("Cloud: 1 file at this level", StringComparison.Ordinal) ||
            !hierarchySummary.Text.Contains("Local folder unavailable", StringComparison.Ordinal))
            throw new InvalidDataException(
                "A healthy Dropbox root with files but no subfolders still rendered as a blank hierarchy.");
        var rootSelection = window.FindName("DropboxHierarchyRootSelectionBox") as CheckBox ??
                            throw new InvalidDataException("The empty Dropbox root has no selection control.");
        var wholeSelection = window.FindName("WholeRootSelectionBox") as CheckBox ??
                             throw new InvalidDataException("The shared root selection control is missing.");
        rootSelection.IsChecked = true;
        rootSelection.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (wholeSelection.IsChecked != true || !window.PendingSelectionDefaultIncludedForTest)
            throw new InvalidDataException("Selecting the Dropbox root did not select the complete two-way pair.");
        rootSelection.IsChecked = false;
        rootSelection.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (wholeSelection.IsChecked != false || window.PendingSelectionDefaultIncludedForTest)
            throw new InvalidDataException("Clearing the Dropbox root did not clear the complete two-way pair.");
        window.SetExistingFolderSelectionForTest("Work", true, "id:local-work");
        if (pair.SelectionRules?.Count != 0 ||
            window.PendingSelectionRulesForTest.SingleOrDefault() is not { FolderPath: "Work", Included: true })
            throw new InvalidDataException("An existing pair folder choice was not held as an unsaved editor draft.");
        var cancel = window.FindName("CancelPairButton") as Button ??
                     throw new InvalidDataException("The in-row editor Cancel button is missing.");
        cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        window.PrepareExistingPairEditorForTest(pair);
        if (window.PendingSelectionRulesForTest.Count != 0 || window.PendingSelectionDefaultIncludedForTest)
            throw new InvalidDataException("Cancel did not discard the unsaved existing-pair folder choice.");

        // Seed the actual source collection, not its derived visible rows: account scoping and
        // workspace refresh legitimately rebuild PairRows from the pair set.
        var archivePair = pair with
        {
            Id = "fedcba9876543210abcd", Name = "Archive", LocalRoot = @"D:\Archive",
            DropboxRoot = "/Archive", DropboxFolderId = "id:archive"
        };
        window.LoadPairSetForTest(new SyncPairSet([pair, archivePair], pair.Id));
        var search = window.FindName("PairSearchBox") as TextBox ??
                     throw new InvalidDataException("The pair search box is missing.");
        search.Text = "archive";
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        var filtered = CollectionViewSource.GetDefaultView(window.PairRows).Cast<PairRow>().ToArray();
        if (filtered.Length != 1 || !string.Equals(filtered[0].Name, "Archive", StringComparison.Ordinal))
            throw new InvalidDataException(
                "Pair search did not filter the expandable list: matched=" + filtered.Length + " of " +
                window.PairRows.Count + "; names=" + string.Join("|", window.PairRows.Select(row => row.Name)) +
                "; kept=" + string.Join("|", filtered.Select(row => row.Name)) + "; scope=" +
                (typeof(MainWindow).GetField("_workspaceAccountId", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(window)?.ToString() ?? "all"));
        search.Clear();

        var pager = new EmbeddedDropboxPagingFixture();
        var root = PumpTask(window, window.PrepareDropboxDraftBrowserForTestAsync(pager));
        // Opening a folder follows its continuation pages automatically (bounded to 40 pages per open
        // in LoadRemainingDraftFolderPagesAsync), then stops on a repeated cursor. This fixture's
        // third page repeats "cursor-2", so the open retains page one, appends page two and fails
        // closed on the cycle before a third page can be added.
        if (root.LoadedChildCount != 2 ||
            root.Children.All(child => child.Entry?.FolderId != "id:first") ||
            root.Children.All(child => child.Entry?.FolderId != "id:second") ||
            root.CanLoadMore || root.PaginationBlockedReason is not { } blockedCursor ||
            !blockedCursor.Contains("cyclic", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The embedded Dropbox browser did not keep its first page and fail closed on a cursor cycle: " +
                "children=" + root.LoadedChildCount + "; cursor=" + (root.ContinueCursor ?? "none") +
                "; blocked=" + (root.PaginationBlockedReason ?? "none") + ".");
        var retained = root.LoadedChildCount;
        PumpTask(window, window.LoadMoreDropboxRootForTestAsync());
        if (root.LoadedChildCount != retained)
            throw new InvalidDataException("The embedded Dropbox browser retried a stopped cursor cycle.");

        AssertResponsiveViewports(window);
        if (window.FindName("SidebarToggle") is Button toggle)
        {
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.UpdateLayout();
            AssertResponsiveViewports(window);
        }
        // UI12: the leading control shows the pair's own two endpoint marks instead of a bulky expand
        // arrow, the row body and Enter toggle the same details, and every control with its own
        // action keeps that action.
        window.PrepareExistingPairEditorForTest(pair);
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        var pairList = window.FindName("PairList") as DataGrid ??
                       throw new InvalidDataException("The folder pair list is missing.");
        PumpUntil(window, () => pairRow.IsExpanded, "Re-opening the saved pair did not expand its row.");
        var dropboxToggle = FindVisualChildren<Button>(window).SingleOrDefault(item =>
            ReferenceEquals(item.DataContext, pairRow) &&
            AutomationProperties.GetName(item).Contains("Expand or collapse", StringComparison.Ordinal)) ??
            throw new InvalidDataException("The pair row has no expand or collapse control.");
        if (dropboxToggle.Content is not PairEndpointMark dropboxMarks ||
            !ReferenceEquals(UiPairEndpointSideTemplate(dropboxMarks, "Left"), window.FindResource("FolderIconTemplate")) ||
            !ReferenceEquals(UiPairEndpointSideTemplate(dropboxMarks, "Right"), window.FindResource("DropboxIconTemplate")) ||
            AutomationProperties.GetItemStatus(dropboxToggle) != "Expanded")
            throw new InvalidDataException(
                "A Dropbox pair did not show both of its endpoint marks with an expanded accessible state.");

        // The provider mark follows the pair's own binding, not the Dropbox default.
        var googlePair = pair with
        {
            Id = "ui12-google-pair", Name = "Google Drive pair", Provider = SyncProviders.GoogleDrive
        };
        window.PrepareExistingPairEditorForTest(googlePair);
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        PumpUntil(window, () => window.PairRows.Any(item => item.Id == googlePair.Id),
            "The Google pair row did not load.");
        var googleRow = window.PairRows.Single(item => item.Id == googlePair.Id);
        PumpUntil(window, () => googleRow.IsExpanded, "Selecting a pair no longer opens its details.");
        var googleToggle = FindVisualChildren<Button>(window).SingleOrDefault(item =>
            ReferenceEquals(item.DataContext, googleRow) &&
            AutomationProperties.GetName(item).Contains("Expand or collapse", StringComparison.Ordinal)) ??
            throw new InvalidDataException("The Google pair row has no expand or collapse control.");
        if (googleToggle.Content is not PairEndpointMark googleMarks ||
            !ReferenceEquals(UiPairEndpointSideTemplate(googleMarks, "Right"), window.FindResource("GoogleDriveIconTemplate")))
            throw new InvalidDataException("A Google Drive pair did not show the Google endpoint mark in its row.");

        var googleContainer = pairList.ItemContainerGenerator.ContainerFromItem(googleRow) as DataGridRow ??
                              throw new InvalidDataException("The Google pair row was not rendered.");
        void ActivateRowArea(FrameworkElement target)
        {
            // WPF supplies ClickCount during native input processing. A manually constructed
            // event defaults to zero, which correctly fails the product's single-click guard.
            // Supply that input fact; keep the actual routed handlers and their decisions intact.
            foreach (var routedEvent in new[]
                     { UIElement.PreviewMouseLeftButtonDownEvent, UIElement.PreviewMouseLeftButtonUpEvent })
            {
                var input = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = routedEvent, Source = target };
                typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!
                    .SetValue(input, 1);
                // The left-button preview event is direct: WPF's input pipeline re-raises it at
                // each ancestor. Deliver the row-originated input at the list's registered handler.
                pairList.RaiseEvent(input);
            }
        }
        var liveSyncSwitch = FindVisualChildren<CheckBox>(googleContainer).FirstOrDefault(item =>
            ReferenceEquals(item.DataContext, googleRow)) ??
            throw new InvalidDataException("The Google pair row lost its Live Sync switch.");
        ActivateRowArea(liveSyncSwitch);
        window.UpdateLayout();
        if (!googleRow.IsExpanded)
            throw new InvalidDataException("The Live Sync switch toggled the row details instead of its own value.");
        var statusControl = FindVisualChildren<Button>(googleContainer).FirstOrDefault(item =>
            AutomationProperties.GetName(item) == "Open pair sync status") ??
            throw new InvalidDataException("The Google pair row lost its status control.");
        ActivateRowArea(statusControl);
        window.UpdateLayout();
        if (!googleRow.IsExpanded)
            throw new InvalidDataException("The pair status control toggled the row details instead of its own action.");

        ActivateRowArea(googleContainer);
        PumpUntil(window, () => !googleRow.IsExpanded,
            "Clicking the pair row body did not collapse the open details.");
        ActivateRowArea(googleContainer);
        PumpUntil(window, () => googleRow.IsExpanded,
            "Clicking the pair row body again did not reopen the details.");

        var keyboardSource = PresentationSource.FromVisual(window) ??
            throw new InvalidDataException("The pair window has no presentation source for keyboard input.");
        pairList.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, keyboardSource, 0, Key.Enter)
            { RoutedEvent = Keyboard.KeyDownEvent });
        PumpUntil(window, () => !googleRow.IsExpanded, "Enter on the pair row did not collapse the open details.");
        pairList.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, keyboardSource, 0, Key.Enter)
            { RoutedEvent = Keyboard.KeyDownEvent });
        PumpUntil(window, () => googleRow.IsExpanded, "Enter on the pair row did not reopen the details.");
        window.RequestProductExit();
    }

    // UI12: one side of the leading pair-row mark, resolved through the accessible side name the
    // shared pair endpoint mark publishes, so this reads the real rendered template per endpoint.
    private static DataTemplate? UiPairEndpointSideTemplate(PairEndpointMark mark, string side) =>
        FindVisualChildren<ContentControl>(mark)
            .Where(item => AutomationProperties.GetName(item)
                .StartsWith(side + " endpoint", StringComparison.Ordinal))
            .Select(item => item.ContentTemplate)
            .FirstOrDefault();

    private static void AssertSharedFolderSelectionState(MainWindow window, string relativePath, bool expected)
    {
        var row = window.FolderSelectionRows.SingleOrDefault(item =>
            string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        if (row?.IsChecked != expected)
            throw new InvalidDataException("The pair draft did not retain one shared two-way folder state.");
    }

    private static SyncPairDraftPreview PairDraftPreview() => new(@"C:\Browse",
        new DropboxFolderEntry("Browse", "/Browse", "id:browse"),
    [
        new SyncFolderSelectionNode("Work", "Work", 1, false, SyncFolderPresence.Mixed, 2,
            LocalStableFolderIdentity: "id:local-work", DropboxStableFolderIdentity: "id:dropbox-work"),
        new SyncFolderSelectionNode("Work/Invoices", "Invoices", 2, false, SyncFolderPresence.Mixed, 0,
            LocalStableFolderIdentity: "id:local-invoices",
            DropboxStableFolderIdentity: "id:dropbox-invoices"),
        new SyncFolderSelectionNode("Work/Photos", "Photos", 2, false, SyncFolderPresence.Mixed, 0,
            LocalStableFolderIdentity: "id:local-photos", DropboxStableFolderIdentity: "id:dropbox-photos"),
        new SyncFolderSelectionNode("Local only", "Local only", 1, false, SyncFolderPresence.LocalOnly, 0,
            LocalStableFolderIdentity: "id:local-only")
    ], 3, 2, true);

    private static int RunNestedUpdatePresentationContract()
    {
        var stateRoot = Path.Combine(Path.GetTempPath(), "sync-shark-ui-nested-presentation",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateRoot);
        var localRoot = Path.Combine(stateRoot, "local");
        Directory.CreateDirectory(localRoot);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, SuppressProductStartup = true };
        app.InitializeComponent();
        var appLock = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "state", "instance.lock"));
        var theme = new ThemeManager(new UiPreferencesStore(appLock.StateAuthority));
        theme.Initialize();
        MainWindow? window = null;
        SyncAuthorityRuntimeHost? fixtureHost = null;
        var priorSynchronizationContext = SynchronizationContext.Current;
        var priorStartupSuppression = MainWindow.SuppressLoadedStartupForTest;
        var priorDraftRunChoice = MainWindow.UnsavedDraftRunChoiceForTest;
        try
        {
            MainWindow.SuppressLoadedStartupForTest = true;
            MainWindow.UnsavedDraftRunChoiceForTest = () =>
                throw new InvalidDataException("An unedited wrapper fixture unexpectedly opened the unsaved-sync-draft dialog.");
            SynchronizationContext.SetSynchronizationContext(null);
            const string relativePath = "focused/metadata-path.txt";
            const string before = "nested wrapper baseline bytes";
            const string manualBytes = "nested wrapper manual update bytes";
            const string backgroundBytes = "nested wrapper background update bytes";
            const string automaticBytes = "nested wrapper automatic update bytes";
            var localPath = Path.Combine(localRoot, "focused", "metadata-path.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            File.WriteAllText(localPath, before);

            var connection = new DropboxConnectionBinding(
                DropboxProviderConfiguration.RegisteredClientId, "fixture-account", "fixture-root-namespace",
                "fixture-home-namespace", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes).Normalize();
            var connectionStore = new DropboxConnectionStore(appLock.StateAuthority);
            connectionStore.SaveNewAsync(connection).GetAwaiter().GetResult();
            var credentialStore = new FixtureDropboxRefreshTokenStore("fixture-refresh-token");
            var (remote, remoteObject, remoteType) = CreateCoreTestRemote();
            InvokeRemote(remoteType, remoteObject, "Put", relativePath, before);

            var pairStore = new SyncPairStore(appLock.StateAuthority);
            SyncPair pair;
            using (var localLease = PilotDirectoryLease.Open(localRoot))
                pair = PresentationPair("0123456789abcdefabcd", "Wrapper fixture", localRoot, localLease.Identity) with
                {
                    ConnectionId = connection.ConnectionId,
                    InitialReviewApproved = true,
                    RecoveryRoot = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                        "id:fixture-recovery", connection.RootNamespaceId)
                };
            pairStore.SaveAsync(new SyncPairSet([pair], pair.Id)).GetAwaiter().GetResult();
            var automation = new SyncAutomationStore(appLock.StateAuthority);
            automation.SaveAsync(new SyncAutomationSettings(false, 15, false)).GetAwaiter().GetResult();

            fixtureHost = new SyncAuthorityRuntimeHost(appLock.StateAuthority,
                new HttpClient(new EndpointUiNoNetwork()), pairStore,
                new SyncRuntimeStateStore(appLock.StateAuthority), connectionStore, credentialStore,
                new SyncAuthorityDatabase(Path.Combine(stateRoot, "authority", "syncshark.db")),
                (_, _) => remote);
            var firstBaseline = fixtureHost.RunPairsAsync([pair], "Fixture first baseline")
                .GetAwaiter().GetResult();
            RequireSuccessfulBatch(firstBaseline, "first effect-free baseline");
            pair = fixtureHost.LoadPairsAsync().GetAwaiter().GetResult().Pairs.Single();
            var stableBaseline = fixtureHost.RunPairsAsync([pair], "Fixture stable baseline")
                .GetAwaiter().GetResult();
            RequireSuccessfulBatch(stableBaseline, "stable effect-free baseline");
            if (!string.Equals(File.ReadAllText(localPath), before, StringComparison.Ordinal) ||
                ReadRemoteCount(remoteType, remoteObject, "DownloadCount") != 0 ||
                ReadRemoteCount(remoteType, remoteObject, "UploadCount") != 0)
                throw new InvalidDataException("The wrapper fixture baseline was not effect-free.");

            window = new MainWindow(appLock.StateAuthority, theme);
            window.UnsavedNavigationChoiceForTest = () =>
                throw new InvalidDataException("An unedited wrapper fixture unexpectedly opened the unsaved-navigation-draft dialog.");
            BindAuthorityHost(window, fixtureHost);
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(window.Dispatcher));
            app.DispatcherUnhandledException += (_, eventArgs) =>
                Console.Error.WriteLine("FIXTURE unhandled dispatcher exception: " + eventArgs.Exception);
            var phase = window.FindName("PhaseText") as TextBlock ??
                        throw new InvalidDataException("The nested presentation fixture could not find PhaseText.");
            var progress = window.FindName("RunProgress") as ProgressBar ??
                           throw new InvalidDataException("The nested wrapper fixture could not find RunProgress.");
            var runTransitions = new List<string>();
            var busyDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                ProgressBar.IsIndeterminateProperty, typeof(ProgressBar));
            busyDescriptor.AddValueChanged(progress, (_, _) =>
                runTransitions.Add(progress.IsIndeterminate ? "Busy" : "Settled"));
            var disconnectedDownloads = ReadRemoteCount(remoteType, remoteObject, "DownloadCount");
            var disconnectedUploads = ReadRemoteCount(remoteType, remoteObject, "UploadCount");
            credentialStore.DeleteAsync().GetAwaiter().GetResult();
            PumpTask(window, window.SyncNowFromBackgroundAsync());
            if (ReadRemoteCount(remoteType, remoteObject, "DownloadCount") != disconnectedDownloads ||
                ReadRemoteCount(remoteType, remoteObject, "UploadCount") != disconnectedUploads ||
                !string.Equals(File.ReadAllText(localPath), before, StringComparison.Ordinal) ||
                !phase.Text.Contains("The protected Dropbox credential is unavailable.", StringComparison.Ordinal) ||
                !window.TrayDetail.Contains("The protected Dropbox credential is unavailable.", StringComparison.Ordinal) ||
                phase.Visibility != Visibility.Visible || progress.IsIndeterminate)
                throw new InvalidDataException("The background connectivity rejection was not presented as expected. " +
                    $"Downloads before={disconnectedDownloads}, after={ReadRemoteCount(remoteType, remoteObject, "DownloadCount")}; " +
                    $"phase={phase.Text}; indeterminate={progress.IsIndeterminate}.");
            var accountScope = typeof(MainWindow).GetField("_workspaceAccountId",
                                   BindingFlags.Instance | BindingFlags.NonPublic) ??
                               throw new MissingFieldException(nameof(MainWindow), "_workspaceAccountId");
            var refreshWorkspace = typeof(MainWindow).GetMethod("RefreshWorkspace",
                                       BindingFlags.Instance | BindingFlags.NonPublic) ??
                                   throw new MissingMethodException(nameof(MainWindow), "RefreshWorkspace");
            accountScope.SetValue(window, connection.ConnectionId);
            refreshWorkspace.Invoke(window, null);
            if (!phase.Text.Contains("The protected Dropbox credential is unavailable.", StringComparison.Ordinal) ||
                phase.Visibility != Visibility.Visible)
                throw new InvalidDataException("Account-filtered Home hid the exact terminal failure.");
            accountScope.SetValue(window, null);
            refreshWorkspace.Invoke(window, null);
            credentialStore.WriteAsync("fixture-refresh-token").GetAwaiter().GetResult();

            InvokeRemote(remoteType, remoteObject, "Put", relativePath, manualBytes);
            runTransitions.Clear();
            var manual = typeof(MainWindow).GetMethod("Run_Click", BindingFlags.Instance | BindingFlags.NonPublic) ??
                         throw new MissingMethodException(nameof(MainWindow), "Run_Click");
            var manualRequestedAfter = DateTimeOffset.UtcNow;
            manual.Invoke(window, [window, new RoutedEventArgs()]);
            // Run control reset can precede the final asynchronous history reload.
            // Wait for the actual user-visible result, not a private flag alone.
            PumpUntil(window, () => !window.RunActiveForTest &&
                                    window.VisibleRows.Any(row => row.PairId == pair.Id && row.Path == relativePath &&
                                        row.Outcome == SyncDecisionKind.Downloaded.ToString()) &&
                                    window.ActivityRows.Any(row => row.PairId == pair.Id && row.Path == relativePath &&
                                        row.Outcome == SyncDecisionKind.Downloaded.ToString()) &&
                                    string.Equals(File.ReadAllText(localPath), manualBytes, StringComparison.Ordinal),
                "The manual wrapper did not complete its nested UpdateLocal operation.", TimeSpan.FromSeconds(30));
            if (!string.Equals(File.ReadAllText(localPath), manualBytes, StringComparison.Ordinal))
                throw new InvalidDataException("The manual wrapper presented completion before exact local bytes existed.");
            var manualProof = CaptureWrapperProof(window, fixtureHost, runTransitions, "Manual", pair.Id, relativePath,
                manualRequestedAfter);

            InvokeRemote(remoteType, remoteObject, "Put", relativePath, backgroundBytes);
            runTransitions.Clear();
            Console.WriteLine("FIXTURE Tray: dispatching the real background wrapper.");
            var backgroundRequestedAfter = DateTimeOffset.UtcNow;
            var backgroundTask = window.SyncNowFromBackgroundAsync();
            Console.WriteLine("FIXTURE Tray: invocation returned task " + backgroundTask.Status);
            try { PumpTask(window, backgroundTask); }
            catch (TimeoutException)
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new
                {
                    stage = "Tray timeout before cleanup",
                    taskStatus = backgroundTask.Status.ToString(),
                    uiRunActive = window.RunActiveForTest,
                    readingRunOutcomes = (bool)typeof(MainWindow).GetField("_readingRunOutcomes",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!,
                    hostActiveRunId = fixtureHost.ActiveRun?.Id,
                    phase = phase.Text,
                    indeterminate = progress.IsIndeterminate,
                    downloads = ReadRemoteCount(remoteType, remoteObject, "DownloadCount"),
                    uploads = ReadRemoteCount(remoteType, remoteObject, "UploadCount"),
                    localMatchesBackground = string.Equals(File.ReadAllText(localPath), backgroundBytes,
                        StringComparison.Ordinal)
                }));
                throw;
            }
            Console.WriteLine("FIXTURE Tray: background wrapper returned.");
            if (!string.Equals(File.ReadAllText(localPath), backgroundBytes, StringComparison.Ordinal))
                throw new InvalidDataException("The background wrapper did not apply the nested UpdateLocal operation.");
            var backgroundProof = CaptureWrapperProof(window, fixtureHost, runTransitions, "Tray", pair.Id, relativePath,
                backgroundRequestedAfter);

            var scheduled = typeof(MainWindow).GetMethod("RunScheduledAsync",
                                BindingFlags.Instance | BindingFlags.NonPublic) ??
                            throw new MissingMethodException(nameof(MainWindow), "RunScheduledAsync");
            var beforeDisabled = ReadRemoteCount(remoteType, remoteObject, "DownloadCount");
            Console.WriteLine("FIXTURE Automatic disabled: checking the real scheduling gate.");
            var disabled = scheduled.Invoke(window, ["Automatic disabled", null]) as Task<bool> ??
                           throw new InvalidDataException("The automatic wrapper did not return a task.");
            if (!PumpTask(window, disabled) ||
                ReadRemoteCount(remoteType, remoteObject, "DownloadCount") != beforeDisabled)
                throw new InvalidDataException("Disabled automatic syncing reached the run host.");

            Console.WriteLine("FIXTURE Automatic enabled: saving isolated automation settings.");
            automation.SaveAsync(new SyncAutomationSettings(true, 15, false)).GetAwaiter().GetResult();
            Console.WriteLine("FIXTURE Automatic enabled: settings saved; dispatching the real scheduled wrapper.");
            InvokeRemote(remoteType, remoteObject, "Put", relativePath, automaticBytes);
            runTransitions.Clear();
            var automaticRequestedAfter = DateTimeOffset.UtcNow;
            var enabled = scheduled.Invoke(window, ["Automatic fixture", null]) as Task<bool> ??
                          throw new InvalidDataException("The automatic wrapper did not return a task.");
            Console.WriteLine("FIXTURE Automatic enabled: invocation returned task " + enabled.Status);
            if (!PumpTask(window, enabled) ||
                !string.Equals(File.ReadAllText(localPath), automaticBytes, StringComparison.Ordinal))
                throw new InvalidDataException("The enabled automatic wrapper did not complete its nested UpdateLocal operation.");
            var automaticProof = CaptureWrapperProof(window, fixtureHost, runTransitions, "Automatic fixture", pair.Id,
                relativePath, automaticRequestedAfter);

            var sharedWrapper = typeof(MainWindow).GetMethod("RunPairsAsync",
                                    BindingFlags.Instance | BindingFlags.NonPublic) ??
                                throw new MissingMethodException(nameof(MainWindow), "RunPairsAsync");
            credentialStore.DeleteAsync().GetAwaiter().GetResult();
            var downloadsBeforeRejection = ReadRemoteCount(remoteType, remoteObject, "DownloadCount");
            var rejected = sharedWrapper.Invoke(window,
                [new[] { pair }, "Fixture rejected command"]) as Task ??
                           throw new InvalidDataException("The shared run wrapper did not return a task.");
            PumpTask(window, rejected);
            if (!phase.Text.Contains("The protected Dropbox credential is unavailable.", StringComparison.Ordinal) ||
                phase.Visibility != Visibility.Visible || progress.IsIndeterminate || !window.RunButton.IsEnabled ||
                ReadRemoteCount(remoteType, remoteObject, "DownloadCount") != downloadsBeforeRejection)
                throw new InvalidDataException("The shared wrapper did not present and reset after a rejected command.");
            // The retained successes precede this real callback failure. Reading
            // them must not turn a newer unresolved failure into a healthy pair.
            PumpTask(window, window.RefreshAfterCliCommandAsync());
            var callbackFailures = (HashSet<string>)typeof(MainWindow)
                .GetField("_runFailurePairIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            if (!callbackFailures.Contains(pair.Id) || !window.TrayAttentionCases.Any(item =>
                    item.Pair.Id == pair.Id && item.IsProblem))
                throw new InvalidDataException("An older retained success incorrectly cleared a newer callback failure.");

            if (!manualProof.Completed || !backgroundProof.Completed || !automaticProof.Completed ||
                ReadRemoteCount(remoteType, remoteObject, "DownloadCount") < 3)
                throw new InvalidDataException("A real wrapper-level nested UpdateLocal proof was incomplete.");

            // Inject a failing admission observer after real durable acceptance,
            // before ownership/dispatch. The host journal, failure record and UI
            // wrapper are not replaced by a fake success/failure decision.
            const string commandFailureTrigger = "Fixture accepted command failure";
            const string commandFailureCause = "Fixture accepted command failed before dispatch.";
            credentialStore.WriteAsync("fixture-refresh-token").GetAwaiter().GetResult();
            var failedCommandDownloads = ReadRemoteCount(remoteType, remoteObject, "DownloadCount");
            var priorRunDispatcher = MainWindow.PairRunDispatcherForTest;
            try
            {
                MainWindow.PairRunDispatcherForTest = (targets, trigger) => Task.Run(() =>
                    fixtureHost.RunPairsAsync(targets, trigger,
                        admissionProgress: message =>
                        {
                            if (message.StartsWith("Admission: command ", StringComparison.Ordinal))
                                throw new IOException(commandFailureCause);
                        }));
                var failedCommandTask = sharedWrapper.Invoke(window,
                    [new[] { pair }, commandFailureTrigger]) as Task ??
                    throw new InvalidDataException("The failing command wrapper did not return a task.");
                PumpTask(window, failedCommandTask);
            }
            finally { MainWindow.PairRunDispatcherForTest = priorRunDispatcher; }
            if (!phase.Text.Contains(commandFailureCause, StringComparison.Ordinal) ||
                phase.Visibility != Visibility.Visible || progress.IsIndeterminate || !window.RunButton.IsEnabled ||
                !window.TrayDetail.Contains(commandFailureCause, StringComparison.Ordinal) ||
                ReadRemoteCount(remoteType, remoteObject, "DownloadCount") != failedCommandDownloads)
                throw new InvalidDataException("A command exception lost its exact cause, feedback reset or no-dispatch evidence.");
            var commandHistory = PumpTask(window, Task.Run(() => fixtureHost.Authority.LoadRunHistoryAsync(100)));
            var failedRun = commandHistory.Single(item =>
            {
                using var request = JsonDocument.Parse(item.RequestJson);
                return request.RootElement.TryGetProperty("trigger", out var trigger) &&
                       trigger.GetString() == commandFailureTrigger;
            });
            using var failedResult = JsonDocument.Parse(failedRun.ResultJson ?? "{}");
            if (failedRun.Status != "blocked" || failedRun.CompletedUtc is null ||
                !failedResult.RootElement.GetProperty("commandFailure").GetProperty("Message")
                    .GetString()!.Contains(commandFailureCause, StringComparison.Ordinal) ||
                new[] { manualProof.RunId, backgroundProof.RunId, automaticProof.RunId }
                    .Any(id => commandHistory.Single(item => item.Id == id).Status != "completed"))
                throw new InvalidDataException("The failed accepted command did not retain its cause or changed a completed run's outcome.");
            var projectRun = typeof(MainWindow).GetMethod("ProjectRun", BindingFlags.Instance | BindingFlags.NonPublic) ??
                             throw new MissingMethodException(nameof(MainWindow), "ProjectRun");
            var failedRunRow = (RunHistoryRow)projectRun.Invoke(window, [failedRun])!;
            if (!failedRunRow.Fields.Any(field => field.Label == "What happened" &&
                    field.Value.Contains(commandFailureCause, StringComparison.Ordinal)) ||
                !failedRunRow.Fields.Any(field => field.Label == "Next action" &&
                    field.Value.Contains("operation log", StringComparison.Ordinal)))
                throw new InvalidDataException("The Runs detail projection omitted the exact command failure and recovery action.");
            var retainedCase = window.TrayAttentionCases.SingleOrDefault(item => item.Pair.Id == pair.Id &&
                item.RunId == failedRun.Id && item.IsProblem);
            var cliCases = PumpTask(window, window.LoadCliAttentionItemsAsync());
            if (retainedCase is null || !retainedCase.Description.Contains(commandFailureCause, StringComparison.Ordinal) ||
                window.TrayAttentionCases.Count(item => item.Pair.Id == pair.Id && item.IsProblem) != 1 ||
                cliCases.Count(item => item.PairId == pair.Id && item.IsProblem) != 1 ||
                !cliCases.Any(item => item.PairId == pair.Id && item.RunId == failedRun.Id && item.IsProblem &&
                    item.Description.Contains(commandFailureCause, StringComparison.Ordinal)))
                throw new InvalidDataException("Problems/tray/running-GUI CLI attention omitted the exact accepted command cause/run.");
            // Follow the same actionable case used by All/Problems and tray,
            // not a test-only direct navigation to the expected log.
            retainedCase.Open();
            var commandLogLoading = typeof(MainWindow).GetField("_operationLogLoading", BindingFlags.Instance | BindingFlags.NonPublic)!;
            PumpUntil(window, () => commandLogLoading.GetValue(window) is false,
                "The failed accepted command's operation log did not load.");
            var commandLogGrid = (DataGrid)typeof(MainWindow).GetField("_operationLogGrid",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            if (!commandLogGrid.Items.Cast<OperationLogRow>().Any(row => row.RunId == failedRun.Id &&
                    row.Entry.Reason.Contains(commandFailureCause, StringComparison.Ordinal)))
                throw new InvalidDataException("The exact failed run's rendered operation log omitted its cause.");

            // A successful run outside this button handler (for example CLI)
            // resolves the exact target through retained readback. It must not
            // leave an older transient Home error or replay completed effects.
            var externalSuccess = PumpTask(window, Task.Run(() => fixtureHost.RunPairsAsync([pair],
                "Fixture ordinary check outside the window")));
            RequireSuccessfulBatch(externalSuccess, "external ordinary success after accepted-command refusal");
            PumpTask(window, window.RefreshAfterCliCommandAsync());
            var transientFailure = typeof(MainWindow).GetField("_workspaceCommandFailure",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            if (transientFailure is not null || window.TrayAttentionCases.Any(item =>
                    item.Pair.Id == pair.Id && item.IsProblem) ||
                window.TrayDetail.Contains(commandFailureCause, StringComparison.Ordinal) ||
                ReadRemoteCount(remoteType, remoteObject, "DownloadCount") != failedCommandDownloads)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    stage = "later ordinary success",
                    transientFailure,
                    currentProblems = window.TrayAttentionCases.Where(item => item.Pair.Id == pair.Id && item.IsProblem)
                        .Select(item => new { item.Title, item.Description, item.RunId }),
                    trayDetail = window.TrayDetail,
                    expectedDownloadCount = failedCommandDownloads,
                    actualDownloadCount = ReadRemoteCount(remoteType, remoteObject, "DownloadCount"),
                    durableAttention = PumpTask(window, Task.Run(() => fixtureHost.LoadPairIdsNeedingAttentionAsync())),
                    attentionCache = typeof(MainWindow).GetField("_attentionPairIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window),
                    runFailureCache = typeof(MainWindow).GetField("_runFailurePairIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window),
                    monitorCache = typeof(MainWindow).GetField("_monitorStateByPair", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window),
                    attentionSources = window.PairAttentionSourcesForTest(pair.Id),
                    currentCommandProjection = PumpTask(window, Task.Run(() =>
                        fixtureHost.LoadRetainedCommandProblemProjectionAsync()))
                }));
                throw new InvalidDataException("A real later successful target check did not resolve stale Home/attention state or replayed a file effect.");
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result = "PASS",
                selector = "--nested-update-presentation-contract",
                witnessId = "TC-NESTED-02.c",
                entryPoints = new[] { "Run_Click", "SyncNowFromBackgroundAsync", "RunScheduledAsync" },
                manual = manualProof,
                background = new { connectivityGate = true, rejectedWithoutFileEffects = true,
                    terminalFailureVisibleOnHomeAccountAndTray = true, proof = backgroundProof },
                automatic = new { disabledGate = true, proof = automaticProof },
                exceptionLifecycle = new { rejectedCommandPresented = true, lifecycleReset = true },
                acceptedCommandFailure = new { retainedRun = failedRun, exactRunLogPresented = true,
                    homeAndTrayCauseVisible = true, successfulRunsPreserved = true,
                    noAdditionalDownloads = true, runsCauseAndNextActionPresented = true,
                    actionableProblemsCaseAndRunningGuiCli = true,
                    laterExternalSuccessResolvesHomeWithoutEffectReplay = true },
                runtime = new
                {
                    realHost = true,
                    sourceAssembly = typeof(SyncAuthorityRuntimeHost).Assembly.GetName().Name,
                    remoteFixtureAssembly = remoteType.Assembly.GetName().Name,
                    downloads = ReadRemoteCount(remoteType, remoteObject, "DownloadCount"),
                    finalLocalSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(localPath)))
                        .ToLowerInvariant()
                }
            }));
            return 0;
        }
        catch (Exception exception)
        {
            // Preserve the first failure even if shutdown has another problem.
            Console.Error.WriteLine("The real nested-wrapper fixture failed before cleanup: " + exception);
            throw;
        }
        finally
        {
            MainWindow.SuppressLoadedStartupForTest = priorStartupSuppression;
            MainWindow.UnsavedDraftRunChoiceForTest = priorDraftRunChoice;
            SynchronizationContext.SetSynchronizationContext(priorSynchronizationContext);
            if (window is not null) window.RequestProductExit();
            else fixtureHost?.Dispose();
            theme.Dispose();
            appLock.Dispose();
            app.Shutdown();
            if (Directory.Exists(stateRoot)) Directory.Delete(stateRoot, true);
        }
    }

    private static T PumpTask<T>(MainWindow window, Task<T> task)
    {
        PumpUntil(window, () => task.IsCompleted, "The wrapper task did not complete.");
        return task.GetAwaiter().GetResult();
    }

    private static void PumpTask(MainWindow window, Task task)
    {
        PumpUntil(window, () => task.IsCompleted, "The wrapper task did not complete.");
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(MainWindow window, Func<bool> completed, string failure,
        TimeSpan? maximumWait = null)
    {
        var timeout = Stopwatch.StartNew();
        if (completed()) return;
        var frame = new DispatcherFrame();
        // The fixture has no visible native window. Its Win32-backed dispatcher
        // timer can stop ticking even after a task has completed. A bounded
        // thread-pool pulse queues ONE UI check at a time; the predicate still
        // runs on the real dispatcher and must satisfy the unchanged assertion.
        var tickQueued = 0;
        using var pulse = new System.Threading.Timer(_ =>
        {
            if (Interlocked.CompareExchange(ref tickQueued, 1, 0) != 0) return;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                Interlocked.Exchange(ref tickQueued, 0);
                if (completed() || timeout.Elapsed > (maximumWait ?? TimeSpan.FromSeconds(15)))
                    frame.Continue = false;
            }));
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        Dispatcher.PushFrame(frame);
        if (!completed()) throw new TimeoutException(failure);
    }

    private static SyncPair PresentationPair(string id, string name, string localRoot,
        PilotDirectoryIdentity identity) => new(id, name, localRoot, identity.VolumeSerialNumber,
        identity.FileId, "/Fixture", "id:fixture", ConnectionId: "fixture-connection",
        InitialReviewApproved: true);

    private static void BindAuthorityHost(MainWindow window, SyncAuthorityRuntimeHost fixtureHost)
    {
        var field = typeof(MainWindow).GetField("_authorityHost", BindingFlags.Instance | BindingFlags.NonPublic) ??
                    throw new MissingFieldException(nameof(MainWindow), "_authorityHost");
        var original = field.GetValue(window) as SyncAuthorityRuntimeHost ??
                       throw new InvalidDataException("The product window did not retain its authority host.");
        field.SetValue(window, fixtureHost);
        if (!ReferenceEquals(field.GetValue(window), fixtureHost))
            throw new InvalidDataException("The test-side window binding did not retain the fixture authority host.");
        original.Dispose();
    }

    private static (ISyncRemoteStore Store, object Instance, Type Type) CreateCoreTestRemote(string? assemblyPath = null)
    {
        if (assemblyPath is null)
        {
            // Shared artifacts place sibling project outputs under bin/<project>/<configuration>.
            // Retain the ordinary project-local output layout too, using this fixture's actual
            // configuration instead of silently requiring a second Release build or stale assembly.
            var configuration = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))?.Name
                ?? throw new InvalidDataException("The UI fixture output configuration is unavailable.");
            var artifactsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "SyncShark.Core.Tests", configuration, "net8.0", "SyncShark.Core.Tests.dll"));
            var projectPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "SyncShark.Core.Tests", "bin", configuration, "net8.0",
                "SyncShark.Core.Tests.dll"));
            var colocatedPath = Path.Combine(AppContext.BaseDirectory, "SyncShark.Core.Tests.dll");
            assemblyPath = File.Exists(colocatedPath) ? colocatedPath :
                File.Exists(artifactsPath) ? artifactsPath : projectPath;
        }
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("Build the focused Core test fixture before this UI contract.",
                assemblyPath);
        var assembly = Assembly.LoadFrom(assemblyPath);
        var type = assembly.GetType("FakeSyncRemoteStore", throwOnError: true) ??
                   throw new TypeLoadException("The focused Core remote fixture is unavailable.");
        var instance = Activator.CreateInstance(type, nonPublic: true) ??
                       throw new InvalidDataException("The focused Core remote fixture could not be created.");
        return ((ISyncRemoteStore)instance, instance, type);
    }

    private static void InvokeRemote(Type type, object instance, string method, params object[] arguments)
    {
        var target = type.GetMethod(method, BindingFlags.Instance | BindingFlags.Public) ??
                     throw new MissingMethodException(type.FullName, method);
        target.Invoke(instance, arguments);
    }

    private static int ReadRemoteCount(Type type, object instance, string property)
    {
        var target = type.GetProperty(property, BindingFlags.Instance | BindingFlags.Public) ??
                     throw new MissingMemberException(type.FullName, property);
        return (int)(target.GetValue(instance) ?? throw new InvalidDataException(property + " was unavailable."));
    }

    private static void RequireSuccessfulBatch(SyncAuthorityBatchResult batch, string stage)
    {
        var item = batch.Items.Single();
        if (item.FailureMessage is not null || item.Result is null || item.Result.Blocked != 0 ||
            item.Result.Conflicts != 0 || item.Result.Stopped)
            throw new InvalidDataException("The " + stage + " failed: " +
                                           (item.FailureMessage ?? "the result was not ready"));
    }

    private static WrapperPresentationProof CaptureWrapperProof(MainWindow window,
        SyncAuthorityRuntimeHost host,
        IReadOnlyCollection<string> transitions, string trigger, string pairId, string relativePath,
        DateTimeOffset requestedAfter)
    {
        // Pending is now an exact unfinished-operation projection, not a run-status
        // stream. Prove the current presentation through real busy feedback, the
        // exact retained terminal command, and its own rendered operation-log table.
        var running = transitions.Contains("Busy", StringComparer.Ordinal);
        var settled = transitions.Contains("Settled", StringComparer.Ordinal);
        Console.WriteLine("FIXTURE " + trigger + ": loading the exact retained command history.");
        var history = PumpTask(window, Task.Run(() => host.Authority.LoadRunHistoryAsync(100)));
        Console.WriteLine("FIXTURE " + trigger + ": command history returned.");
        var run = history.Single(item =>
        {
            using var request = JsonDocument.Parse(item.RequestJson);
            // Earlier connectivity refusals legitimately retain the same trigger.
            // Bind this actual invocation, not an arbitrary newest/first match.
            return item.StartedUtc >= requestedAfter &&
                request.RootElement.TryGetProperty("trigger", out var value) && value.GetString() == trigger &&
                request.RootElement.GetProperty("pairIds").EnumerateArray().Any(id => id.GetString() == pairId);
        });
        var completed = run.Status == "completed" && run.CompletedUtc is not null;
        var result = window.VisibleRows.Any(item => item.PairId == pairId &&
                                                    string.Equals(item.Path, relativePath,
                                                        StringComparison.OrdinalIgnoreCase) &&
                                                    item.Outcome == SyncDecisionKind.Downloaded.ToString());
        var activity = window.ActivityRows.Any(item => string.Equals(item.Path, relativePath,
            StringComparison.OrdinalIgnoreCase) && item.Outcome == SyncDecisionKind.Downloaded.ToString());
        Console.WriteLine("FIXTURE " + trigger + ": opening the exact rendered run log.");
        window.OpenOperationLog(run.Id);
        Console.WriteLine("FIXTURE " + trigger + ": log navigation returned; awaiting its real readback.");
        var loading = typeof(MainWindow).GetField("_operationLogLoading", BindingFlags.Instance | BindingFlags.NonPublic) ??
                      throw new MissingFieldException(nameof(MainWindow), "_operationLogLoading");
        PumpUntil(window, () => loading.GetValue(window) is false, "The exact wrapper-run operation log did not load.");
        Console.WriteLine("FIXTURE " + trigger + ": rendered log readback settled.");
        var logGrid = (DataGrid)(typeof(MainWindow).GetField("_operationLogGrid",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) ??
            throw new MissingFieldException(nameof(MainWindow), "_operationLogGrid"));
        var logged = logGrid.Items.Cast<OperationLogRow>().Any(item => item.RunId == run.Id &&
            item.Entry.PairId == pairId && item.Entry.RelativePath == relativePath &&
            item.Entry.OperationKind == SyncAuthorityOperationKind.UpdateLocal &&
            item.Entry.Phase == SyncActivityPhase.Completed &&
            item.Action == "Download — Completed");
        if (!running || !settled || !completed || !result || !activity || !logged || window.RunProgress.IsIndeterminate)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { trigger, retainedRun = run,
                busy = running, settled, result, activity, exactRunLog = logged,
                matchingLogRows = logGrid.Items.Cast<OperationLogRow>()
                    .Where(item => item.RunId == run.Id && item.Entry.PairId == pairId &&
                                   item.Entry.RelativePath == relativePath)
                    .Select(item => new { item.Action, item.Entry.OperationKind, item.Entry.Phase }) }));
            throw new InvalidDataException(trigger +
                $" lacks current busy/settled/terminal/result/activity/exact-run-log proof: {running}/{settled}/{completed}/{result}/{activity}/{logged}.");
        }
        return new WrapperPresentationProof(trigger, pairId, run.Id, running, completed, result, activity,
            !window.RunProgress.IsIndeterminate, logged);
    }

    private sealed class FixtureDropboxRefreshTokenStore(string? token) : IDropboxRefreshTokenStore
    {
        private string? _token = token;

        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_token);

        public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            _token = refreshToken;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            _token = null;
            return Task.CompletedTask;
        }
    }

    private sealed record WrapperPresentationProof(string Trigger, string PairId, string RunId, bool BusyPresented,
        bool Completed, bool ResultPresented, bool ActivityPresented, bool LifecycleReset, bool ExactRunLogPresented);

    private static void AssertConflictDiffContract()
    {
        var line = ConflictTextDiff.Unified("first\nlocal\nlast", "first\ndropbox\nlast", false, false);
        if (!line.Contains("- local", StringComparison.Ordinal) ||
            !line.Contains("+ dropbox", StringComparison.Ordinal))
            throw new InvalidDataException("Line conflict comparison did not retain both changed values.");

        var word = ConflictTextDiff.Unified("alpha    beta", "alpha beta", true, true);
        if (word.Contains("-    ", StringComparison.Ordinal))
            throw new InvalidDataException("Ignore-whitespace word comparison retained a whitespace-only change.");

        var syntax = ConflictTextDiff.Unified("var answer = 41; // old", "var answer = 42; // new", false,
            wordMode: false, syntaxMode: true);
        if (!ConflictTextDiff.IsCodePath("answer.cs") ||
            !syntax.Contains("- 41", StringComparison.Ordinal) ||
            !syntax.Contains("+ 42", StringComparison.Ordinal))
            throw new InvalidDataException("Syntax-aware conflict comparison did not isolate the changed token.");

        var large = string.Concat(Enumerable.Repeat("0123456789\n", 400_000));
        var bounded = ConflictTextDiff.Unified(large, large + "different", false, false);
        if (bounded.Length > 2_100_000 || !bounded.Contains("omitted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Large conflict comparison exceeded its bounded display contract.");
    }

    private static void AssertBackgroundWindowLifecycle(PilotStateAuthority authority, ThemeManager theme)
    {
        var window = new MainWindow(authority, theme)
        {
            Width = 900,
            Height = 650,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32_000,
            Top = -32_000,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        window.Close();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        if (window.IsVisible || !window.IsLoaded)
            throw new InvalidDataException(
                "Closing the main window did not hide the live background runtime.");
        window.ShowFromBackground();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        if (!window.IsVisible || !window.IsLoaded)
            throw new InvalidDataException("The background runtime did not reopen its retained main window.");
        MainWindow.ExitCheckpointWaitForTest = (_, _) => Task.FromResult(false);
        try
        {
            try
            {
                window.PrepareProductExitAsync().GetAwaiter().GetResult();
                throw new InvalidDataException("A timed-out sync checkpoint was treated as a successful exit.");
            }
            catch (TimeoutException)
            {
            }
            if (!window.IsLoaded || !window.IsVisible)
                throw new InvalidDataException("A timed-out sync checkpoint closed the live product window.");

            MainWindow.ExitCheckpointWaitForTest = (_, _) => Task.FromResult(true);
            window.PrepareProductExitAsync().GetAwaiter().GetResult();
        }
        finally
        {
            MainWindow.ExitCheckpointWaitForTest = null;
        }
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        if (window.IsLoaded)
            throw new InvalidDataException("Graceful product exit did not checkpoint and close the retained runtime window.");
    }

    private static void AssertWindowsStartupContract()
    {
        const string executable = @"C:\Program Files\Massdex\Sync Shark\SyncShark.exe";
        var command = WindowsStartupRegistration.BuildCommand(executable);
        if (!string.Equals(command, "\"" + executable + "\" --background", StringComparison.Ordinal))
            throw new InvalidDataException("Windows logon launch is not bound to the quoted executable and background mode.");
        try
        {
            _ = WindowsStartupRegistration.BuildCommand("SyncShark.exe");
            throw new InvalidDataException("Windows logon launch accepted a relative executable path.");
        }
        catch (ArgumentException)
        {
        }
    }

    private static int RenderPopupDpiProcess(string rawOutputRoot, double dpi)
    {
        var outputRoot = Path.GetFullPath(rawOutputRoot);
        Directory.CreateDirectory(outputRoot);
        var stateRoot = Path.Combine(Path.GetTempPath(), "sync-shark-ui-popup-fixture",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateRoot);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, SuppressProductStartup = true };
        app.InitializeComponent();
        _realDpiMonitors = ProbeRealDpiMonitors();
        WriteRealDpiInventory();
        if (!HasRealDpiContext(dpi))
            throw new InvalidOperationException($"No real Windows monitor exposes the required {dpi:N0}-DPI context.");
        var appLock = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "state", "instance.lock"));
        var theme = new ThemeManager(new UiPreferencesStore(appLock.StateAuthority));
        theme.Initialize();
        try
        {
            ObservedTargetWindowDpis.Clear();
            theme.SetPreferenceAsync(UiPreferencesStore.AmoledTheme).GetAwaiter().GetResult();
            if (Math.Abs(dpi - 144d) < 0.5)
                RenderConflictContextMenu(Path.Combine(outputRoot,
                    "conflict-context-menu-amoled-successor.png"), appLock.StateAuthority, theme, dpi);
            RenderConflictContextMenu(Path.Combine(outputRoot,
                $"conflict-context-menu-amoled-context-{dpi:N0}-dpi-successor.png"),
                appLock.StateAuthority, theme, dpi);
            RenderConflictGroupPopup(Path.Combine(outputRoot,
                $"conflict-group-popup-amoled-context-{dpi:N0}-dpi-successor.png"),
                appLock.StateAuthority, theme, dpi);
            RenderHeaderAccountToolTip(Path.Combine(outputRoot,
                $"header-account-tooltip-amoled-context-{dpi:N0}-dpi-successor.png"),
                appLock.StateAuthority, theme, dpi);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                metric = "loaded_popup_dpi_context",
                dpi,
                hostDpi = _popupHostDpi,
                hwndTransitions = 0,
                wpfRootSynchronizations = 0,
                syntheticDpiOperations = 0
            }));
            return 0;
        }
        finally
        {
            theme.Dispose();
            appLock.Dispose();
            app.Shutdown();
            if (Directory.Exists(stateRoot)) Directory.Delete(stateRoot, true);
        }
    }

    private static int CaptureRealDpiProcess(string rawOutputRoot, double dpi)
    {
        if (!RequiredDpiContexts().Contains(dpi))
            throw new ArgumentOutOfRangeException(nameof(dpi), "The release DPI capture must be 96, 144, or 192.");
        var outputRoot = Path.GetFullPath(rawOutputRoot);
        Directory.CreateDirectory(outputRoot);
        var receiptPath = Path.Combine(outputRoot, $"real-dpi-{dpi:N0}-receipt.json");
        var existingCapture = Directory.EnumerateFiles(outputRoot,
                $"*-context-{dpi:N0}-dpi-successor.png", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (File.Exists(receiptPath) || existingCapture is not null)
            throw new IOException(
                $"The immutable {dpi:N0}-DPI capture already exists or is incomplete; use a new empty capture root.");
        var stateRoot = Path.Combine(Path.GetTempPath(), "sync-shark-ui-dpi-capture",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateRoot);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, SuppressProductStartup = true };
        app.InitializeComponent();
        _realDpiMonitors = ProbeRealDpiMonitors();
        WriteRealDpiInventory();
        if (!HasRealDpiContext(dpi))
            throw new InvalidOperationException($"No real Windows monitor exposes the required {dpi:N0}-DPI context.");
        var appLock = PilotInstanceLock.Acquire(Path.Combine(stateRoot, "state", "instance.lock"));
        var theme = new ThemeManager(new UiPreferencesStore(appLock.StateAuthority));
        theme.Initialize();
        try
        {
            ObservedTargetWindowDpis.Clear();
            theme.SetPreferenceAsync(UiPreferencesStore.AmoledTheme).GetAwaiter().GetResult();
            var pages = new[]
            {
                "dashboard", "pairs", "connections", "first-sync", "activity", "queue", "conflicts",
                "recovery", "rewind", "settings"
            };
            var generated = new List<string>();
            for (var page = 0; page < pages.Length; page++)
            {
                var name = $"{pages[page]}-amoled-context-{dpi:N0}-dpi-successor.png";
                RenderMainWindow(Path.Combine(outputRoot, name), 1240, 790, appLock.StateAuthority, theme,
                    pageIndex: page,
                    focusName: page switch
                    {
                        1 => "PairNameBox", 6 => "KeepLocalConflictButton", 9 => "AutomationIntervalBox", _ => null
                    }, dpi: dpi);
                generated.Add(name);
            }
            var contextMenu = $"conflict-context-menu-amoled-context-{dpi:N0}-dpi-successor.png";
            RenderConflictContextMenu(Path.Combine(outputRoot, contextMenu), appLock.StateAuthority, theme, dpi);
            generated.Add(contextMenu);
            var groupPopup = $"conflict-group-popup-amoled-context-{dpi:N0}-dpi-successor.png";
            RenderConflictGroupPopup(Path.Combine(outputRoot, groupPopup), appLock.StateAuthority, theme, dpi);
            generated.Add(groupPopup);
            var tooltip = $"header-account-tooltip-amoled-context-{dpi:N0}-dpi-successor.png";
            RenderHeaderAccountToolTip(Path.Combine(outputRoot, tooltip), appLock.StateAuthority, theme, dpi);
            generated.Add(tooltip);
            var browser = $"dropbox-browser-amoled-context-{dpi:N0}-dpi-successor.png";
            RenderBrowser(Path.Combine(outputRoot, browser), dpi);
            generated.Add(browser);

            var appAssembly = typeof(MainWindow).Assembly;
            var fixtureAssembly = typeof(Program).Assembly;
            var informationalVersion = appAssembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
            var observedWindowDpis = ObservedTargetWindowDpis.Distinct().Order().ToArray();
            if (observedWindowDpis.Length != 1 || Math.Abs(observedWindowDpis[0] - dpi) > 0.5)
                throw new InvalidDataException("The real-DPI capture did not retain one exact loaded-window DPI.");
            var manifest = generated.Order(StringComparer.Ordinal).Select(name =>
            {
                var path = Path.Combine(outputRoot, name);
                return new
                {
                    path = name,
                    bytes = new FileInfo(path).Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                };
            }).ToArray();
            var receipt = JsonSerializer.Serialize(new
            {
                schema = "sync-shark-real-dpi-capture-v1",
                targetDpi = dpi,
                observedWindowDpis,
                observedPopupDpi = _popupHostDpi,
                syntheticDpiOperations = 0,
                applicationInformationalVersion = informationalVersion,
                fixtureAssemblySha256 = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(fixtureAssembly.Location))).ToLowerInvariant(),
                applicationAssemblySha256 = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(appAssembly.Location))).ToLowerInvariant(),
                capturedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                files = manifest
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(receiptPath, receipt, new UTF8Encoding(false));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                metric = "real_dpi_capture",
                targetDpi = dpi,
                observedWindowDpis,
                observedPopupDpi = _popupHostDpi,
                syntheticDpiOperations = 0,
                fileCount = manifest.Length,
                receipt = Path.GetFileName(receiptPath)
            }));
            return 0;
        }
        finally
        {
            theme.Dispose();
            appLock.Dispose();
            app.Shutdown();
            if (Directory.Exists(stateRoot)) Directory.Delete(stateRoot, true);
        }
    }

    private static void RenderPopupDpiInIsolatedProcess(string outputRoot, double dpi)
    {
        var executable = Environment.ProcessPath ??
                         throw new InvalidOperationException("The UI fixture executable path is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entryAssembly = typeof(Program).Assembly.Location;
            if (string.IsNullOrWhiteSpace(entryAssembly) || !File.Exists(entryAssembly))
                throw new InvalidOperationException("The UI fixture entry assembly path is unavailable.");
            start.ArgumentList.Add(entryAssembly);
        }
        start.ArgumentList.Add("--popup-dpi");
        start.ArgumentList.Add(outputRoot);
        start.ArgumentList.Add(dpi.ToString(CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ??
                            throw new InvalidOperationException("The isolated popup-DPI fixture did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = standardOutput.GetAwaiter().GetResult();
        var error = standardError.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidDataException(
                $"The isolated {dpi:N0}-DPI popup fixture failed with exit {process.ExitCode}: {error}");
        if (!string.IsNullOrWhiteSpace(output)) Console.Write(output);
    }

    private static void RenderConflictComparisons(string outputRoot)
    {
        var now = new DateTimeOffset(2026, 9, 4, 18, 0, 0, TimeSpan.Zero);
        var code = new SyncConflictContent("fixture-code", "fixture-pair", "src/engine.cs", 28, 28,
            "sha256:local", "sha256:dropbox", now, now.AddSeconds(4),
            Encoding.UTF8.GetBytes("var answer = 41; // local\n"),
            Encoding.UTF8.GetBytes("var answer = 42; // Dropbox\n"), null);
        RenderWindow(new ConflictComparisonWindow(code),
            Path.Combine(outputRoot, "conflict-code-diff-amoled-successor.png"), 1120, 760, true);

        var binary = new SyncConflictContent("fixture-binary", "fixture-pair", "archive.bin", 2048, 4096,
            "sha256:local-binary", "sha256:dropbox-binary", now, now.AddMinutes(2),
            [0, 1, 2, 3], [0, 9, 8, 7],
            "Opaque content stays unchanged; compare size, time and hash metadata before choosing an action.");
        RenderWindow(new ConflictComparisonWindow(binary),
            Path.Combine(outputRoot, "conflict-binary-metadata-amoled-successor.png"), 1120, 760, true);

        var image = new SyncConflictContent("fixture-image", "fixture-pair", "design.png", 1, 1,
            "sha256:local-image", "sha256:dropbox-image", now, now.AddMinutes(1),
            CreatePixelPng(Colors.DeepSkyBlue), CreatePixelPng(Colors.Orange), null);
        RenderWindow(new ConflictComparisonWindow(image),
            Path.Combine(outputRoot, "conflict-image-comparison-amoled-successor.png"), 1120, 760, true);

        var unavailable = binary with
        {
            ConflictId = "fixture-unreadable",
            RelativePath = "locked.dat",
            LocalBytes = null,
            DropboxBytes = null,
            Note = "Content preview is unavailable because the retained versions could not be read. Metadata remains available and no content was changed."
        };
        RenderWindow(new ConflictComparisonWindow(unavailable),
            Path.Combine(outputRoot, "conflict-unreadable-metadata-amoled-successor.png"), 1120, 760, true);
    }

    private static byte[] CreatePixelPng(Color color)
    {
        var bitmap = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);
        var pixels = Enumerable.Repeat(new[] { color.B, color.G, color.R, color.A }, 4)
            .SelectMany(value => value).ToArray();
        bitmap.WritePixels(new Int32Rect(0, 0, 2, 2), pixels, 8, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void RenderMainWindow(string path, int width, int height, PilotStateAuthority authority, ThemeManager theme,
        int pageIndex = 0, string? focusName = null, double dpi = 0, Action<MainWindow>? prepare = null)
    {
        var window = new MainWindow(authority, theme) { Width = width, Height = height };
        if (window.FindName("Navigation") is ListBox navigation) navigation.SelectedIndex = pageIndex;
        RenderWindow(window, path, width, height, showActualWindow: true,
            value =>
            {
                SeedMainWindow((MainWindow)value);
                prepare?.Invoke((MainWindow)value);
                if (focusName is not null) FocusControl(value, focusName);
            }, dpi);
    }

    private static void SeedMainWindow(MainWindow window)
    {
        var conflict = new DecisionRow("fixture-pair", "Documents ↔ Dropbox", "proposal.docx", "Conflict",
            "Both sides changed from the retained baseline; neither version was overwritten or deleted.",
            "Dropbox · user@example.test", "Local newer", "Use local", "2026-09-04 18:12",
            "2026-09-04 18:08");
        if (window.VisibleRows.Count == 0)
        {
            window.VisibleRows.Add(conflict);
            window.VisibleRows.Add(new DecisionRow("fixture-pair", "Documents ↔ Dropbox", "invoice.pdf", "Uploaded",
                "Updated the exact expected Dropbox revision after revalidation.",
                "Dropbox · user@example.test"));
        }
        if (window.ConflictRows.Count == 0) window.ConflictRows.Add(conflict);
        if (window.ActivityRows.Count == 0)
        {
            window.ActivityRows.Add(new ActivityRow("2026-09-01 06:18:12 +01:00", "proposal.docx", "Conflict",
                "Documents ↔ Dropbox: both versions remain available.", "Dropbox · user@example.test",
                "fixture-pair", "Documents ↔ Dropbox", "operation-conflict"));
            window.ActivityRows.Add(new ActivityRow("2026-09-01 06:17:41 +01:00", "invoice.pdf", "Uploaded",
                "Documents ↔ Dropbox: verified transfer completed.", "Dropbox · user@example.test",
                "fixture-pair", "Documents ↔ Dropbox", "operation-upload"));
        }
        if (window.QueueRows.Count == 0)
        {
            window.QueueRows.Add(new QueueRow("fixture-pair", "2026-09-01 06:18:00 +01:00", "Documents ↔ Dropbox",
                "proposal.docx", 20, "Upload update", "Effect unknown · recovery first", "Dropbox · user@example.test"));
            window.QueueRows.Add(new QueueRow("fixture-pair-2", "2026-09-01 06:18:00 +01:00", "Projects ↔ Dropbox",
                "invoice.pdf", 10, "Download new", "Queued", "Dropbox · user@example.test"));
        }
        if (window.PairRows.Count == 0)
            window.PairRows.Add(new PairRow("fixture-pair", "Documents ↔ Dropbox",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dropbox", "Documents"),
                "/Documents", "Dropbox · user@example.test", true, 20, true, true, "Monitoring · 2 pending"));
        if (window.FolderSelectionRows.Count == 0)
        {
            window.FolderSelectionRows.Add(new FolderSelectionRow("fixture-pair", "Work", "Work", true,
                "On this PC and Dropbox", 3, LocalItems: 420, OnlineOnlyItems: 18, MixedItems: 402));
            window.FolderSelectionRows.Add(new FolderSelectionRow("fixture-pair", "Work/Invoices", "    Invoices",
                true, "On this PC and Dropbox", 0, LocalItems: 186, MixedItems: 186));
            window.FolderSelectionRows.Add(new FolderSelectionRow("fixture-pair", "Work/Photos", "    Photos",
                null, "Mixed selection", 2, LocalItems: 234, OnlineOnlyItems: 18, MixedItems: 216));
            window.FolderSelectionRows.Add(new FolderSelectionRow("fixture-pair", "Archive", "Archive", false,
                "Excluded", 7, ExcludedItems: 980));
        }
        if (window.BootstrapRows.Count == 0)
        {
            window.BootstrapRows.Add(new BootstrapRow("fixture-pair", "Projects/local-newer.cs", "Local newer",
                "Review conflict", "Nothing changes until you choose", "12.4 KiB", "12.3 KiB",
                "2026-09-04 18:12", "2026-09-04 18:08", "Documents ↔ Dropbox", "file", "CS"));
            window.BootstrapRows.Add(new BootstrapRow("fixture-pair", "Archive/cloud-only.pdf", "Dropbox only",
                "Download", "Copy to this computer", "—", "2.1 MiB", "Unavailable",
                "2026-09-03 09:24", "Documents ↔ Dropbox", "file", "PDF"));
            window.BootstrapRows.Add(new BootstrapRow("fixture-pair", "Photos", "Identical",
                "No change", "Record as already in sync", "—", "—", "Unavailable", "Unavailable",
                "Documents ↔ Dropbox", "folder", "Folder"));
        }
        if (window.FindName("EmptyResultsSurface") is FrameworkElement empty) empty.Visibility = Visibility.Collapsed;
        if (window.FindName("UploadedCount") is TextBlock uploaded) uploaded.Text = "1";
        if (window.FindName("DownloadedCount") is TextBlock downloaded) downloaded.Text = "0";
        if (window.FindName("IdenticalCount") is TextBlock identical) identical.Text = "3";
        if (window.FindName("ConflictCount") is TextBlock conflicts) conflicts.Text = "1";
        if (window.FindName("BlockedCount") is TextBlock blocked) blocked.Text = "0";
        if (window.FindName("QueueCount") is TextBlock queue) queue.Text = "1";
        if (window.FindName("SummaryText") is TextBlock summary)
            summary.Text = "Documents ↔ Dropbox: 1 uploaded, 0 downloaded, 3 identical, 1 conflict, 0 blocked. Cloud-only files remain visible without downloading their content.";
        if (window.FindName("PairNameBox") is TextBox pairName) pairName.Text = "Documents ↔ Dropbox";
        if (window.FindName("PairPriorityBox") is TextBox priority) priority.Text = "20";
        if (window.FindName("LocalRootBox") is TextBox local)
            local.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dropbox", "Documents");
        if (window.FindName("DropboxRootBox") is TextBox remote) remote.Text = "/Documents";
        if (window.FindName("DropboxFolderIdText") is TextBlock folderId) folderId.Text = "id:documents-stable-folder";
        if (window.FindName("ConnectionText") is TextBlock pairConnection) pairConnection.Text = "user@example.test · Full Dropbox";
        if (window.FindName("HeaderState") is TextBlock headerState) headerState.Text = "Dropbox connected";
        if (window.FindName("HeaderAccountButton") is Button accountButton) accountButton.Content = "user@example.test";
        if (window.FindName("ConnectionProviderText") is TextBlock provider) provider.Text = "Dropbox";
        if (window.FindName("ConnectionNameText") is TextBlock accountName) accountName.Text = "Fixture account";
        if (window.FindName("ConnectionEmailText") is TextBlock email) email.Text = "user@example.test";
        if (window.FindName("ConnectionTypeText") is TextBlock accountType) accountType.Text = "Personal";
        if (window.FindName("ConnectionAccessText") is TextBlock access) access.Text = "Full Dropbox";
        if (window.FindName("ConnectionHealthText") is TextBlock health) health.Text = "Connected and verified";
        if (window.FindName("ConnectionVerifiedText") is TextBlock verified) verified.Text = "4 Sept 2026, 18:20:00";
        if (window.FindName("ConnectionLastSyncText") is TextBlock lastSync) lastSync.Text = "4 Sept 2026, 18:19:42";
        if (window.FindName("ConnectionPairsText") is TextBlock pairs) pairs.Text = "2 Linked Folder Pairs";
        if (window.FindName("ConnectionIdText") is TextBlock connectionId) connectionId.Text = "dropbox-connection-fixture";
        if (window.FindName("ConnectionConnectButton") is Button connect) connect.IsEnabled = false;
        if (window.FindName("ConnectionReauthorizeButton") is Button reconnect) reconnect.IsEnabled = true;
        if (window.FindName("ConnectionManageButton") is Button manage) manage.IsEnabled = true;
        if (window.FindName("ConnectionDisconnectButton") is Button disconnect) disconnect.IsEnabled = true;
        if (window.FindName("BootstrapSummary") is TextBlock bootstrapSummary)
            bootstrapSummary.Text = "Documents ↔ Dropbox: 3,842 inventoried · 716 create in Dropbox · 231 create on this PC · 42 conflicts stay unchanged. Showing 3 matching examples.";
        VerifyAccessibleControls(window);
    }

    private static void VerifyAccessibleControls(MainWindow window)
    {
        foreach (var name in new[] { "PairNameBox", "PairPriorityBox", "LocalRootBox", "DropboxRootBox", "PairEnabledBox", "PairFilesOnDemandBox",
                     "PairDeletionBox", "AutomationEnabledBox", "AutomationIntervalBox", "StartWithWindowsBox",
                     "KeepLocalConflictButton", "KeepDropboxConflictButton" })
        {
            if (window.FindName(name) is not FrameworkElement element)
                throw new InvalidDataException($"The evidence fixture could not find {name}.");
            var accessibleName = System.Windows.Automation.AutomationProperties.GetName(element);
            var labelledBy = System.Windows.Automation.AutomationProperties.GetLabeledBy(element);
            var intrinsicContent = element is ContentControl { Content: string content } && !string.IsNullOrWhiteSpace(content);
            if (string.IsNullOrWhiteSpace(accessibleName) && labelledBy is null && !intrinsicContent)
                throw new InvalidDataException($"{name} has no programmatic label.");
        }
    }

    private static void RenderBrowser(string path, double dpi = 0)
    {
        using var httpClient = new HttpClient(new BrowserFixtureHandler());
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "account-id", "root-namespace", "home-namespace",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes);
        var window = new DropboxFolderBrowserWindow(new DropboxBrowserClient(httpClient, new FixtureTokenSource(), binding),
            "Dropbox · user@example.test");
        window.Roots[0].ReplaceChildren([
            new DropboxFolderEntry("Documents", "/Documents", "id:documents"),
            new DropboxFolderEntry("Projects", "/Projects", "id:projects"),
            new DropboxFolderEntry("Sync Shark Workspace", "/Sync Shark Workspace", "id:workspace")
        ]);
        if (window.FindName("BrowserStatus") is System.Windows.Controls.TextBlock status)
            status.Text = "Loaded 3 child folders.";
        if (window.FindName("AccountText") is not TextBlock { Text: "Dropbox · user@example.test" })
            throw new InvalidDataException("The Dropbox folder browser does not identify its exact connection.");
        RenderWindow(window, path, 760, 650, showActualWindow: true, dpi: dpi);
    }

    private static void RenderWindow(Window window, string path, int width, int height, bool showActualWindow,
        Action<Window>? afterShow = null, double dpi = 0)
    {
        var content = (FrameworkElement)window.Content;
        var renderDpi = dpi > 0 ? dpi : 96d;
        if (showActualWindow)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32_000;
            window.Top = -32_000;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            renderDpi = ApplyLoadedWindowDpi(window, width, height, dpi);
            afterShow?.Invoke(window);
        }
        else
        {
            content.Width = width;
            content.Height = height;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
        }
        content.UpdateLayout();
        if (window is MainWindow renderedMainWindow)
        {
            AssertVisibleColumnWidths(renderedMainWindow);
            AssertResponsiveViewports(renderedMainWindow);
        }
        var logicalWidth = showActualWindow ? Math.Max(1, content.ActualWidth) : width;
        var logicalHeight = showActualWindow ? Math.Max(1, content.ActualHeight) : height;
        var scale = renderDpi / 96d;
        var renderWidth = Math.Max(1, (int)Math.Round(logicalWidth * scale));
        var renderHeight = Math.Max(1, (int)Math.Round(logicalHeight * scale));
        var bitmap = new RenderTargetBitmap(renderWidth, renderHeight, renderDpi, renderDpi, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        if (!showActualWindow) window.Content = null;
        if (window is MainWindow mainWindow) mainWindow.RequestProductExit();
        else window.Close();
    }

    private static double ApplyLoadedWindowDpi(Window window, int logicalWidth, int logicalHeight, double dpi)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Loaded-window DPI evidence requires Windows.");
        if (dpi != 0 && dpi is < 96 or > 768) throw new ArgumentOutOfRangeException(nameof(dpi));
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) throw new InvalidDataException("The DPI fixture has no loaded HWND.");
        if (dpi > 0)
        {
            var target = _realDpiMonitors.FirstOrDefault(item => Math.Abs(item.Dpi - dpi) <= 0.5)
                         ?? throw new InvalidOperationException(
                             $"No real Windows monitor exposes the required {dpi:N0}-DPI context.");
            var scale = target.Dpi / 96d;
            if (!SetWindowPos(handle, IntPtr.Zero, target.Bounds.Left + 24, target.Bounds.Top + 24,
                    Math.Max(1, (int)Math.Round(logicalWidth * scale)),
                    Math.Max(1, (int)Math.Round(logicalHeight * scale)), 0x0010 | 0x0004))
                throw new InvalidDataException("The loaded DPI fixture could not move to the selected real monitor.");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        }
        var hwndDpi = GetDpiForWindow(handle);
        var observed = VisualTreeHelper.GetDpi(window).PixelsPerInchX;
        if (hwndDpi == 0 || Math.Abs(observed - hwndDpi) > 0.5)
            throw new InvalidDataException(
                $"The loaded WPF root reported {observed:F1} DPI while its real HWND reported {hwndDpi} DPI.");
        if (dpi > 0 && Math.Abs(hwndDpi - dpi) > 0.5)
            throw new InvalidDataException(
                $"The loaded HWND entered {hwndDpi} DPI instead of the selected real {dpi:F1}-DPI monitor context.");
        if (dpi > 0) ObservedTargetWindowDpis.Add(hwndDpi);
        return hwndDpi;
    }

    private static int ProbeRealDpiProcess()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Real DPI observation requires Windows.");
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, SuppressProductStartup = true };
        app.InitializeComponent();
        try
        {
            _realDpiMonitors = ProbeRealDpiMonitors();
            WriteRealDpiInventory();
            return 0;
        }
        finally
        {
            app.Shutdown();
        }
    }

    private static IReadOnlyList<RealDpiMonitor> ProbeRealDpiMonitors()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Real DPI observation requires Windows.");
        var native = new List<(IntPtr Handle, NativeRect Bounds)>();
        MonitorEnumProcedure callback = (IntPtr handle, IntPtr deviceContext, ref NativeRect bounds,
            IntPtr data) =>
        {
            native.Add((handle, bounds));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) || native.Count == 0)
            throw new InvalidDataException("Windows did not expose an attached monitor for DPI observation.");

        var result = new List<RealDpiMonitor>(native.Count);
        for (var index = 0; index < native.Count; index++)
        {
            var monitor = native[index];
            var probe = new Window
            {
                Width = 64,
                Height = 64,
                Opacity = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32_000,
                Top = -32_000
            };
            probe.Show();
            probe.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            var handle = new WindowInteropHelper(probe).Handle;
            if (handle == IntPtr.Zero || !SetWindowPos(handle, IntPtr.Zero,
                    monitor.Bounds.Left + 8, monitor.Bounds.Top + 8, 64, 64, 0x0010 | 0x0004))
                throw new InvalidDataException("A DPI probe HWND could not enter an attached monitor.");
            probe.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var dpi = GetDpiForWindow(handle);
            probe.Close();
            if (dpi == 0) throw new InvalidDataException("Windows returned zero DPI for a loaded monitor HWND.");
            result.Add(new RealDpiMonitor(monitor.Handle, monitor.Bounds, dpi, index));
        }
        return result;
    }

    private static double[] RequiredDpiContexts() => [96d, 144d, 192d];

    private static bool HasRealDpiContext(double dpi) =>
        _realDpiMonitors.Any(item => Math.Abs(item.Dpi - dpi) <= 0.5);

    private static void WriteRealDpiInventory()
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "real_dpi_inventory",
            syntheticDpiOperations = 0,
            monitors = _realDpiMonitors.Select(item => new
            {
                item.Index,
                item.Dpi,
                width = item.Bounds.Right - item.Bounds.Left,
                height = item.Bounds.Bottom - item.Bounds.Top
            })
        }));
        foreach (var missing in RequiredDpiContexts().Where(dpi => !HasRealDpiContext(dpi)))
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                metric = "real_dpi_environment_blocker",
                targetDpi = missing,
                reason = "No attached Windows monitor currently exposes this real DPI context; no synthetic message, WPF root override, or bitmap enlargement was used."
            }));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private delegate bool MonitorEnumProcedure(IntPtr monitor, IntPtr deviceContext,
        ref NativeRect bounds, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clippingRectangle,
        MonitorEnumProcedure callback, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo monitorInfo);

    private sealed record RealDpiMonitor(IntPtr Handle, NativeRect Bounds, uint Dpi, int Index);

    private static void AssertVisibleColumnWidths(MainWindow window)
    {
        AssertVisibleColumnWidth(window, "BootstrapGrid", 0, 240, "First Sync item and location");
        AssertVisibleColumnWidth(window, "PairList", 1, 240, "Folder Pair name and locations");
        AssertVisibleColumnWidth(window, "ConflictGrid", 0, 240, "Conflict item and location");
        AssertVisibleColumnWidth(window, "RecoveryGrid", 0, 240, "Recovery item and original location");
    }

    private static void AssertResponsiveViewports(MainWindow window)
    {
        foreach (var grid in FindVisualChildren<DataGrid>(window).Where(item => item.IsVisible))
        {
            grid.UpdateLayout();
            var requiredWidth = grid.Columns.Sum(column => column.ActualWidth);
            if (requiredWidth <= grid.ActualWidth + 1) continue;
            var scroll = FindVisualChild<ScrollViewer>(grid);
            if (scroll is null || scroll.ComputedHorizontalScrollBarVisibility != Visibility.Visible)
                throw new InvalidDataException(
                    $"{System.Windows.Automation.AutomationProperties.GetName(grid)} clips columns without an accessible horizontal viewport " +
                    $"(grid={grid.ActualWidth:F1}×{grid.ActualHeight:F1}, columns={requiredWidth:F1}, " +
                    $"scroll={scroll?.ComputedHorizontalScrollBarVisibility.ToString() ?? "missing"}).");
        }
        if (window.FindName("SafetySummary") is TextBlock safety &&
            safety.ActualHeight + safety.Margin.Top + safety.Margin.Bottom + 0.5 < safety.DesiredSize.Height)
            throw new InvalidDataException(
                $"The baseline safety text is vertically truncated ({safety.ActualWidth:F1}×{safety.ActualHeight:F1} actual; {safety.DesiredSize.Width:F1}×{safety.DesiredSize.Height:F1} desired). ");

        if (Keyboard.FocusedElement is FrameworkElement focused && window.Content is FrameworkElement content &&
            focused.IsVisible && IsVisualDescendantOf(focused, content))
        {
            var bounds = focused.TransformToAncestor(content)
                .TransformBounds(new Rect(new Point(), focused.RenderSize));
            if (bounds.Left < -0.5 || bounds.Top < -0.5 || bounds.Right > content.ActualWidth + 0.5 ||
                bounds.Bottom > content.ActualHeight + 0.5)
                throw new InvalidDataException(
                    $"Keyboard focus on {focused.Name} was not brought into the active viewport: " +
                    $"bounds={bounds.Left:F1},{bounds.Top:F1},{bounds.Right:F1},{bounds.Bottom:F1}; " +
                    $"viewport={content.ActualWidth:F1}×{content.ActualHeight:F1}.");
        }
    }

    private static void AssertVisibleColumnWidth(MainWindow window, string gridName, int columnIndex,
        double minimumWidth, string description)
    {
        if (window.FindName(gridName) is not DataGrid grid || !grid.IsVisible) return;
        grid.UpdateLayout();
        if (columnIndex >= grid.Columns.Count)
            throw new InvalidDataException($"{description} column is missing.");
        if (grid.Columns[columnIndex].ActualWidth + 0.1 < minimumWidth)
            throw new InvalidDataException(
                $"{description} collapsed to {grid.Columns[columnIndex].ActualWidth:F1}px; minimum is {minimumWidth:F1}px.");
    }

    private static void RenderConflictContextMenu(string path, PilotStateAuthority authority, ThemeManager theme,
        double dpi)
    {
        var window = new MainWindow(authority, theme) { Width = 1240, Height = 790 };
        if (window.FindName("Navigation") is ListBox navigation) navigation.SelectedIndex = 6;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32_000;
        window.Top = -32_000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        ApplyLoadedWindowDpi(window, 1240, 790, dpi);
        SeedMainWindow(window);
        if (window.FindName("ConflictGrid") is not DataGrid grid || grid.ContextMenu is null)
            throw new InvalidDataException("The pair-bound conflict context menu is absent.");
        grid.SelectedIndex = 0;
        var menu = grid.ContextMenu;
        menu.PlacementTarget = grid;
        menu.Placement = PlacementMode.Center;
        menu.IsOpen = true;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        menu.UpdateLayout();
        ApplyLoadedPopupDpi(menu, dpi, "conflict context menu");
        AssertPopupDpi(menu, dpi, "conflict context menu");
        SaveElementBitmap(menu, path, dpi);
        menu.IsOpen = false;
        window.RequestProductExit();
    }

    private static void RenderConflictGroupPopup(string path, PilotStateAuthority authority, ThemeManager theme,
        double dpi)
    {
        var window = CreateOffscreenMainWindow(authority, theme, pageIndex: 6, dpi);
        if (window.FindName("ConflictGroupFilter") is not ComboBox combo)
            throw new InvalidDataException("The conflict grouping popup control is absent.");
        combo.ApplyTemplate();
        combo.IsDropDownOpen = true;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        if (combo.Template.FindName("PART_Popup", combo) is not Popup { Child: FrameworkElement popupChild } popup)
            throw new InvalidDataException("The conflict grouping popup did not open through its real template.");
        popupChild.UpdateLayout();
        ApplyLoadedPopupDpi(popupChild, dpi, "conflict grouping popup");
        AssertPopupDpi(popupChild, dpi, "conflict grouping popup");
        SaveElementBitmap(popupChild, path, dpi);
        popup.IsOpen = false;
        window.RequestProductExit();
    }

    private static void RenderHeaderAccountToolTip(string path, PilotStateAuthority authority, ThemeManager theme,
        double dpi)
    {
        var window = CreateOffscreenMainWindow(authority, theme, pageIndex: 0, dpi);
        if (window.FindName("HeaderAccountButton") is not Button button || button.ToolTip is null)
            throw new InvalidDataException("The account-details tooltip is absent.");
        var toolTip = button.ToolTip as ToolTip ?? new ToolTip { Content = button.ToolTip };
        button.ToolTip = toolTip;
        toolTip.PlacementTarget = button;
        toolTip.Placement = PlacementMode.Bottom;
        toolTip.IsOpen = true;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        toolTip.UpdateLayout();
        ApplyLoadedPopupDpi(toolTip, dpi, "account-details tooltip");
        AssertPopupDpi(toolTip, dpi, "account-details tooltip");
        SaveElementBitmap(toolTip, path, dpi);
        toolTip.IsOpen = false;
        window.RequestProductExit();
    }

    private static MainWindow CreateOffscreenMainWindow(PilotStateAuthority authority, ThemeManager theme,
        int pageIndex, double dpi)
    {
        var window = new MainWindow(authority, theme)
        {
            Width = 1240,
            Height = 790,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32_000,
            Top = -32_000,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        if (window.FindName("Navigation") is ListBox navigation) navigation.SelectedIndex = pageIndex;
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        ApplyLoadedWindowDpi(window, 1240, 790, dpi);
        SeedMainWindow(window);
        window.UpdateLayout();
        return window;
    }

    private static void AssertPopupDpi(Visual visual, double expectedDpi, string description)
    {
        var observed = VisualTreeHelper.GetDpi(visual).PixelsPerInchX;
        if (Math.Abs(observed - expectedDpi) > 0.5)
            throw new InvalidDataException(
                $"The loaded {description} remained at {observed:F1} DPI instead of {expectedDpi:F1} DPI.");
    }

    private static void ApplyLoadedPopupDpi(FrameworkElement popupContent, double dpi, string description)
    {
        if (PresentationSource.FromVisual(popupContent) is not HwndSource { Handle: var handle } ||
            handle == IntPtr.Zero)
            throw new InvalidDataException($"The loaded {description} has no popup HWND.");
        _popupHostDpi = GetDpiForWindow(handle);
        var observed = VisualTreeHelper.GetDpi(popupContent).PixelsPerInchX;
        if (_popupHostDpi == 0 || Math.Abs(_popupHostDpi - dpi) > 0.5)
            throw new InvalidDataException(
                $"The loaded {description} popup HWND reported {_popupHostDpi} DPI on the selected real {dpi:F1}-DPI monitor.");
        if (Math.Abs(observed - _popupHostDpi) > 0.5)
            throw new InvalidDataException(
                $"The loaded {description} WPF root reported {observed:F1} DPI while its popup HWND reported {_popupHostDpi} DPI.");
        popupContent.UpdateLayout();
    }

    private static void SaveElementBitmap(FrameworkElement element, string path, double dpi)
    {
        var observedDpi = VisualTreeHelper.GetDpi(element).PixelsPerInchX;
        if (Math.Abs(observedDpi - dpi) > 0.5)
            throw new InvalidDataException("A popup render cannot claim a DPI different from its loaded visual context.");
        var scale = observedDpi / 96d;
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * scale));
        var bitmap = new RenderTargetBitmap(width, height, observedDpi, observedDpi, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void FocusFirstResultCell(Window window)
    {
        if (window.FindName("ResultGrid") is not DataGrid grid || grid.Items.Count == 0) return;
        grid.SelectedIndex = 0;
        grid.ScrollIntoView(grid.Items[0]);
        grid.UpdateLayout();
        if (grid.ItemContainerGenerator.ContainerFromIndex(0) is not DataGridRow row) return;
        var presenter = FindVisualChild<DataGridCellsPresenter>(row);
        presenter?.UpdateLayout();
        if (presenter?.ItemContainerGenerator.ContainerFromIndex(0) is DataGridCell cell)
        {
            cell.Focus();
            Keyboard.Focus(cell);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            if (cell.Background is not SolidColorBrush cellBackground || row.Background is not SolidColorBrush rowBackground ||
                cellBackground.Color != rowBackground.Color)
                throw new InvalidDataException("The focused DataGrid cell does not inherit the selected row background.");
            if (cell.Foreground is not SolidColorBrush cellForeground || row.Foreground is not SolidColorBrush rowForeground ||
                cellForeground.Color != rowForeground.Color)
                throw new InvalidDataException("The focused DataGrid cell does not inherit the selected row foreground.");
        }
    }

    private static void FocusControl(Window window, string name)
    {
        if (name == "ResultGrid")
        {
            FocusFirstResultCell(window);
            return;
        }
        if (window.FindName(name) is not Control control)
            throw new InvalidDataException($"The focus evidence control {name} is absent.");
        control.Focus();
        Keyboard.Focus(control);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        if (!control.IsKeyboardFocusWithin)
            throw new InvalidDataException($"The focus evidence control {name} did not receive keyboard focus.");
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static bool IsVisualDescendantOf(DependencyObject candidate, DependencyObject ancestor)
    {
        for (DependencyObject? current = candidate; current is not null;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : null)
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
}

internal sealed class FixtureTokenSource : IDropboxAccessTokenSource
{
    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("fixture-token");
}

internal sealed class EmbeddedDropboxPagingFixture : IDropboxFolderBrowserService
{
    public Task<DropboxFolderPage> ListFoldersAsync(string? folderId = null,
        CancellationToken cancellationToken = default) => Task.FromResult(new DropboxFolderPage(
        [new DropboxFolderEntry("First", "/First", "id:first")], "cursor-1"));

    public Task<DropboxFolderPage> ContinueFoldersAsync(string cursor,
        CancellationToken cancellationToken = default) => Task.FromResult(cursor switch
        {
            "cursor-1" => new DropboxFolderPage(
                [new DropboxFolderEntry("Second", "/Second", "id:second")], "cursor-2"),
            "cursor-2" => new DropboxFolderPage(
                [new DropboxFolderEntry("Third", "/Third", "id:third")], "cursor-2"),
            _ => throw new InvalidDataException("Unexpected continuation cursor.")
        });

    public Task<DropboxFolderPage> SearchFoldersAsync(string query, string? folderId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new DropboxFolderPage([], null));

    public Task<DropboxFolderPage> ContinueFolderSearchAsync(string cursor,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new DropboxFolderPage([], null));

    public async IAsyncEnumerable<DropboxFolderEntry> EnumerateFoldersAsync(string? folderId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<DropboxFolderEntry> CreateFolderAsync(string? parentFolderId, string name,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The read-only embedded folder test must not create provider state.");
}

internal sealed class BrowserFixtureHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.AbsolutePath;
        var json = route.EndsWith("/users/get_current_account", StringComparison.Ordinal)
            ? """{"account_id":"account-id","root_info":{"root_namespace_id":"root-namespace","home_namespace_id":"home-namespace"}}"""
            : """{"entries":[{".tag":"folder","name":"Documents","id":"id:documents","path_lower":"/documents","path_display":"/Documents"},{".tag":"folder","name":"Projects","id":"id:projects","path_lower":"/projects","path_display":"/Projects"},{".tag":"folder","name":"Sync Shark Workspace","id":"id:workspace","path_lower":"/sync shark workspace","path_display":"/Sync Shark Workspace"}],"cursor":"fixture","has_more":false}""";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
