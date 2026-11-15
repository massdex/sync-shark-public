using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private sealed record CliOwnershipSession(SyncPair Pair, ExplorerOwnership.Inspection Inspection);
    private readonly Dictionary<string, CliOwnershipSession> _cliOwnershipSessions = new(StringComparer.Ordinal);
    internal Action<ProcessStartInfo>? CliLocationLauncherForTest { get; set; }

    private DateTimeOffset _startupStartedUtc = DateTimeOffset.UtcNow;
    private long _startupGeneration;
    private DateTimeOffset _startupLastProgressUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? _startupCompletedUtc;
    private string _startupState = "starting";
    private string _startupPhase = "opening-window";
    private string _startupDetail = "Opening the application; initialization and recovery have not completed.";
    private string? _startupError;

    private void ReportStartupPhase(string phase, string detail, string state = "loading", string? error = null)
    {
        _startupState = state;
        _startupPhase = phase;
        _startupDetail = detail;
        _startupError = error;
        _startupLastProgressUtc = DateTimeOffset.UtcNow;
        if (state == "ready") _startupCompletedUtc = _startupLastProgressUtc;
        if (_startupLoading && state == "loading") StartupLoadingText.Text = detail;
        // Phase boundaries are real advancement, not timer heartbeats. Never log account/credential payloads.
        App.WriteStartupProgress(state + "; " + phase, detail);
    }

    internal SyncCliAppStatusData CaptureStartupStatusForCli() => new(_startupState, _startupPhase,
        _startupDetail, _startupStartedUtc, _startupLastProgressUtc, _startupCompletedUtc,
        _startupState == "ready" && _explorerStartupReady && !_explorerStartupFailed && !_startupLoading && !_closing,
        Math.Max(0, (DateTimeOffset.UtcNow - _startupLastProgressUtc).TotalSeconds), Environment.ProcessId,
        _runActive || _authorityHost.HasActiveRun, _authorityHost.ActiveRun?.Paused == true,
        typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown", BuildIdentity.SourceRevision,
        _startupError);

    private async Task RunStartupWorkAsync(string phase, string detail, Func<Task> work)
    {
        var startupGeneration = _startupGeneration;
        ReportStartupPhase(phase, detail);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await work();
            var completion = $"Completed: {detail} Elapsed: {Stopwatch.GetElapsedTime(started).TotalSeconds:F3}s.";
            // A CLI refresh can join the startup projection while initialization is
            // still pending and finish after readiness (or after a genuine retry).
            // Its completion is useful telemetry, not authority to regress that
            // newer startup state to loading or manufacture its last advancement.
            if (_startupLoading && !_closing && _startupCompletedUtc is null &&
                _startupGeneration == startupGeneration && _startupState == "loading" &&
                _startupPhase == phase)
                ReportStartupPhase(phase, completion);
            else
                App.WriteStartupProgress("refresh-completed; " + phase, completion);
        }
        catch (Exception error)
        {
            App.WriteStartupProgress("failed; " + phase,
                $"{detail} Elapsed: {Stopwatch.GetElapsedTime(started).TotalSeconds:F3}s. {SyncCli.DescribeException(error)}");
            throw;
        }
    }

    private async Task RunStartupProjectionWorkAsync(string detail, Func<Task> work)
    {
        // Only actual awaited startup reads advance this state. Ordinary refreshes/status reads
        // cannot manufacture startup progress or replace a completed/error phase.
        if (_startupLoading && !_closing && _startupPhase is
            ("retained-history" or "retained-review" or "recovery-results"))
            await RunStartupWorkAsync(_startupPhase, detail, work);
        else
        {
            if (_readingRunOutcomes && !_closing)
                ShowRunStatus(detail + " No new file transfer is running.");
            await work();
        }
    }

    internal async Task RefreshAfterCliCommandAsync()
    {
        // Read back, never reconstruct a command's effect from its requested arguments. Classify
        // the current editor against its OLD retained baseline after the read completes, so user
        // edits made during that await are preserved too. No await separates this decision/reload.
        var refreshedPairSet = await _authorityHost.LoadPairsAsync();
        var selectedId = (PairList.SelectedItem as PairRow)?.Id;
        var openingPair = _pairSet.Pairs.SingleOrDefault(pair =>
            string.Equals(pair.Id, _editingPairRootId, StringComparison.Ordinal));
        var openingSession = _endpointSession;
        var reloadCleanEditor = !_editingNewPair && PairLocationDraftActive && openingPair is not null &&
            string.Equals(openingPair.Id, selectedId, StringComparison.Ordinal) &&
            openingSession is not null && openingSession.EditingPairId == openingPair.Id &&
            openingSession.EditingPolicyRevision == openingPair.PolicyRevision &&
            !CurrentPairEditorHasUnsavedChanges() &&
            refreshedPairSet.Pairs.Any(pair => string.Equals(pair.Id, openingPair.Id, StringComparison.Ordinal));
        _pairSet = refreshedPairSet;
        // Reuse the real selection/session path only for a clean, current opening baseline. A dirty
        // or already stale draft keeps its controls AND expected revision; refresh is not consent.
        if (reloadCleanEditor) ApplyPairSelection(openingPair!.Id);
        // An external run can resolve a retained failure without sending this
        // window a progress callback. Refresh the SAME durable queue/conflict/
        // attention snapshot as an in-window run before rebuilding pair rows;
        // old cached attention is not evidence of a current problem.
        if (_authorityHost.AuthorityStateExists)
            await RefreshAuthorityProjectionsAsync(requireSuccess: true, selectedPairId: selectedId);
        else
            RefreshPairRows(selectedId);
        RefreshWorkspace();
        // The review has its own retained inventory and Start-button state. Updating the
        // pair rows alone leaves an open review actionable after a CLI approval. Reload
        // the selected review through the same generation/account guards as navigation;
        // this reads retained state and never repeats the command or starts a scan.
        if (_attentionTabs?.SelectedIndex == 1 && BootstrapPairBox.SelectedValue is string)
            await RefreshBootstrapReviewAsync();
    }

    internal void DisposeCliOwnershipSessions()
    {
        foreach (var session in _cliOwnershipSessions.Values) session.Inspection.Dispose();
        _cliOwnershipSessions.Clear();
    }

    internal async Task<object> ExecuteCliAppActionAsync(SyncCliInvocation command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Runs on the application's dispatcher through the existing trusted CLI adapter. Do not touch
        // authority projections, credentials, providers or the settings/pair editor for this read.
        if (command.Command == "app status") return CaptureStartupStatusForCli();
        // DIAG354-4: the diagnostics command runs the one shared in-app read-only diagnostic action the
        // Diagnostics page runs, through the existing running-application adapter. It shows no dialog,
        // copies nothing, creates no file, contacts no provider and reads no credential; only the safe
        // structured outcomes and the identical redacted report are returned.
        if (command.Command is "diagnostics run")
        {
            var diagnostics = await RunDiagnosticActionAsync(token);
            return BuildCliDiagnosticsData(diagnostics);
        }
        if (command.Command is "updates verify" or "updates apply")
            return await VerifyOrApplyLocalUpdateFromCliAsync(command, token);
        if (command.Command is "sharing open" or "sharing manage" or "file-requests open")
            return await ExecuteCliSharingActionAsync(command, token);
        if (command.Command is "explorer restore" or "app uninstall")
        {
            if (!command.Yes) throw new SyncCliUsageException("This installed application action requires --yes.");
            RequireCleanCliSettingsDrafts();
            if (!ExplorerIntegration.IsInstalledProcess)
                throw new SyncCliUnsupportedException("Use the signed installed application for this action. No installation or registration was changed.");
            if (command.Command == "explorer restore")
            {
                await RestoreExplorerIntegrationAsync(propagateErrors: true);
                return new SyncCliControlData(command.Command, true, "The existing Explorer integration registration workflow completed.");
            }
            await StartRemovalHandoffAsync(token);
            CliUpdateExitPrepared = true;
            return new SyncCliControlData(command.Command, true,
                "Managed files are local and the uninstaller acknowledged its handoff. The app has checkpointed and will exit after this response. Removal and any Windows approval are still pending; accounts, files, settings and recovery data are retained.");
        }
        var pair = (await _authorityHost.LoadPairsAsync(token)).Pairs.SingleOrDefault(item => item.Id == command.PairId)
            ?? throw new SyncCliTargetException("The exact folder pair is not retained.");
        if (command.AccountId is not null && command.AccountId != pair.ConnectionId)
            throw new SyncCliTargetException("That pair does not belong to the selected account.");
        if (command.Command == "storage disable")
        {
            if (!command.Yes || command.AccountId is null || command.Generation is null)
                throw new SyncCliUsageException("Supply the exact --account, --generation and --yes before making this pair fully available offline.");
            if (command.Generation != pair.PolicyRevision)
                throw new SyncCliTargetException("The pair policy changed. Read storage show before changing it.");
            RequireCleanCliSettingsDrafts();
            if (!pair.FilesOnDemandEnabled)
                return new { pairId = pair.Id, filesOnDemandEnabled = false, changed = false, pair.PolicyRevision };
            if (!ExplorerIntegration.IsInstalledProcess)
                throw new SyncCliUnsupportedException("Disabling managed storage requires the signed installed app. No files or registration changed.");
            await DisablePairFilesOnDemandAsync(pair, token);
            var updated = _pairSet.Pairs.Single(item => item.Id == pair.Id);
            return new { pairId = updated.Id, updated.ConnectionId, updated.PolicyRevision,
                filesOnDemandEnabled = updated.FilesOnDemandEnabled, changed = true,
                note = "Managed files are available offline; online-only storage is off. Synchronization remains enabled according to the pair's existing setting." };
        }
        if (command.Command == "locations open") return await OpenCliLocationAsync(pair, command, token);
        if (command.Command == "explorer roots")
        {
            var roots = await Task.Run(() => ExplorerIntegration.CompetingRoots(pair.LocalRoot), token);
            var pairs = (await _authorityHost.LoadPairsAsync(token)).Pairs;
            return new { pairId = pair.Id, pair.ConnectionId, pair.FilesOnDemandEnabled, roots = roots.Select(root => new {
                root.Id, root.Path, root.Provider, root.HasRestorableShellMetadata,
                affectedPairs = pairs.Where(item => CliRootContains(root.Path, item.LocalRoot)).Select(item => new { item.Id, item.Name, item.ConnectionId }).ToArray(),
                scope = "The entire registered root, including items outside Sync Shark pairs." }).ToArray() };
        }
        if (!command.Yes) throw new SyncCliUsageException("This action requires --yes for the exact target.");
        if (!ExplorerIntegration.IsInstalledProcess)
            throw new SyncCliUnsupportedException("Ownership changes require the signed installed app. No inspection or registration effect occurred.");
        RequireCleanCliSettingsDrafts();
        if (string.IsNullOrWhiteSpace(command.Key) || string.IsNullOrWhiteSpace(command.Path) || !Path.IsPathFullyQualified(command.Path))
            throw new SyncCliUsageException("Supply the exact --key and fully qualified --path of the entire registered root.");
        if (command.Command == "explorer inspect")
        {
            if (_cliOwnershipSessions.Count >= 8)
                throw new InvalidOperationException("Release an existing inspection before opening another (maximum eight).");
            var root = ExplorerIntegration.CompetingRoots(pair.LocalRoot).SingleOrDefault(item => item.Id == command.Key && CliSamePath(item.Path, command.Path))
                ?? throw new SyncCliTargetException("That exact registration and entire-root path no longer match this pair.");
            var inspection = new ExplorerOwnership.Inspection(root);
            try { await Task.Run(() => inspection.Inspect(token), token); }
            catch { inspection.Dispose(); throw; }
            var key = Guid.NewGuid().ToString("N"); // Ephemeral in-process handle, never a new persisted identity.
            _cliOwnershipSessions.Add(key, new(pair, inspection));
            return new { inspectionKey = key, root.Path, root.Provider, pairId = pair.Id, inspection.Files, inspection.Folders,
                note = "Inspection only. Remove needs this key and entire-root path. Handles and Undo last only until release or app exit." };
        }
        if (!_cliOwnershipSessions.TryGetValue(command.Key, out var session) || session.Pair.Id != pair.Id ||
            !CliSamePath(session.Inspection.Target.Path, command.Path))
            throw new SyncCliTargetException("The exact inspection is not held by this running app for this pair and root.");
        var held = session.Inspection;
        // Release must remain possible even if the pair policy changed after inspection.
        if (command.Command == "explorer release")
        {
            _cliOwnershipSessions.Remove(command.Key); held.Dispose();
            return new { released = true, undoAvailable = false, note = "Inspection handles released. No registration was changed by this command." };
        }
        if (session.Pair.PolicyRevision != pair.PolicyRevision || session.Pair.ConnectionId != pair.ConnectionId ||
            !CliSamePath(session.Pair.LocalRoot, pair.LocalRoot))
            throw new InvalidOperationException("The pair changed after inspection. Release the inspection and inspect again.");
        if (command.Command == "explorer remove") held.Remove();
        else if (command.Command == "explorer undo")
        {
            if (!held.Removed) throw new InvalidOperationException("That inspection has no removed registration to undo.");
            held.Restore();
        }
        else throw new SyncCliUsageException("Unknown application action.");
        await RefreshPairOwnershipAsync(pair);
        return new { inspectionKey = command.Key, held.Target.Path, held.Removed, held.RemovalUncertain,
            note = "Only the exact shell registration changed. File contents, native cloud ownership and cloud access were not changed." };
    }

    private async Task<object> OpenCliLocationAsync(SyncPair pair, SyncCliInvocation command, CancellationToken token)
    {
        if (!command.Yes || command.Path is null || command.Choice is not ("local" or "cloud"))
            throw new SyncCliUsageException("Use --path <relative-path-or-dot>, --choice local|cloud and --yes.");
        var relative = command.Path is "." or "" ? "" : PilotPath.NormalizeRelative(command.Path);
        var row = new ActivityRow("", relative, "", "", PairId: pair.Id, PairName: pair.Name,
            ItemKind: "Folder", LocalRoot: pair.LocalRoot, DropboxRoot: pair.DropboxRoot);
        if (command.Choice == "local")
        {
            var path = LocalItemPath(row) ?? throw new SyncCliUsageException("The path is outside this pair.");
            var folder = Directory.Exists(path);
            if (!folder && !File.Exists(path)) throw new SyncCliTargetException("The exact local item is unavailable.");
            LaunchCliLocation(new ProcessStartInfo("explorer.exe") { UseShellExecute = true,
                Arguments = folder ? "\"" + path + "\"" : "/select,\"" + path + "\"" });
            return new { opened = true, pairId = pair.Id, location = path, note = "File Explorer launch requested; no file content was opened." };
        }
        if (pair.Provider == SyncProviders.GoogleDrive)
        {
            var googleUrl = await _authorityHost.GetGoogleDriveWebLocationAsync(pair.Id, pair.ConnectionId!, relative.Length == 0 ? "." : relative, token);
            LaunchCliLocation(new ProcessStartInfo(googleUrl) { UseShellExecute = true });
            return new { opened = true, pairId = pair.Id, accountId = pair.ConnectionId, location = googleUrl,
                note = "The exact provider-returned website location was opened; its browser session is separate." };
        }
        var connection = _authorityHost.LoadDropboxConnection(pair.ConnectionId)
            ?? throw new SyncCliTargetException("Reconnect this pair's exact account first.");
        var client = await _authorityHost.CreateAccountSharingClientAsync(connection.ConnectionId, token);
        var item = await client.GetItemAsync(connection.RootNamespaceId, CloudItemPath(row)!, token);
        var url = CloudItemUrl(row with { ItemKind = item.Kind == "folder" ? "Folder" : "File" })!;
        LaunchCliLocation(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        return new { opened = true, pairId = pair.Id, accountId = connection.ConnectionId, location = url.AbsoluteUri,
            note = "The provider website was opened. Its browser account session is separate; no browser identity was changed." };
    }

    private async Task<object> ExecuteCliSharingActionAsync(SyncCliInvocation command, CancellationToken token)
    {
        if (!command.Yes || string.IsNullOrWhiteSpace(command.AccountId) || string.IsNullOrWhiteSpace(command.Key))
            throw new SyncCliUsageException("Supply the exact --account, --key and --yes.");
        var connection = _authorityHost.LoadAccountBinding(command.AccountId)
            ?? throw new SyncCliTargetException("The exact account is not retained.");
        var client = await _authorityHost.CreateAccountSharingClientAsync(connection.ConnectionId, token);
        var ns = client.Namespaces[0];
        string? permissionId = null, fingerprint = null;
        if (command.Selection is not null)
        {
            using var value = JsonDocument.Parse(command.Selection);
            if (value.RootElement.ValueKind != JsonValueKind.Object)
                throw new SyncCliUsageException("--selection must be an object of exact namespaceId, permissionId and revisionFingerprint strings.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name) || property.Value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(property.Value.GetString()))
                    throw new SyncCliUsageException("Selection fields must be unique, nonempty strings.");
                switch (property.Name)
                {
                    case "namespaceId": ns = property.Value.GetString()!; break;
                    case "permissionId": permissionId = property.Value.GetString(); break;
                    case "revisionFingerprint": fingerprint = property.Value.GetString(); break;
                    default: throw new SyncCliUsageException("Unknown sharing selection field: " + property.Name);
                }
            }
        }
        if (!client.Namespaces.Contains(ns, StringComparer.Ordinal))
            throw new SyncCliTargetException("That namespace is not part of this account.");
        if (command.Command == "file-requests open")
        {
            if (connection.Provider != SyncProviders.Dropbox)
                throw new SyncCliUnsupportedException("File requests are a Dropbox feature. Use sharing for Google Drive link permissions.");
            if (permissionId is not null || fingerprint is not null)
                throw new SyncCliUsageException("A file request selection supports only namespaceId.");
            var request = await client.GetRequestAsync(ns, command.Key, token);
            DropboxAccountSharingClient.ValidateUrl(request.Url);
            LaunchCliLocation(new ProcessStartInfo(request.Url) { UseShellExecute = true });
            return new { opened = true, accountId = connection.ConnectionId, request.Id, request.Url };
        }
        var found = await AccountSharingLookup.ExactAsync(client, ns, command.Key, permissionId, token);
        var current = await client.RefreshLinkAsync(found, token);
        if (fingerprint is not null && !string.Equals(fingerprint, current.RevisionFingerprint, StringComparison.Ordinal))
            throw new SyncCliTargetException("The link permission changed since it was selected. Refresh before opening it.");
        if (command.Command == "sharing manage")
        {
            if (!TryCloseSharingEditor()) throw new SyncCliOperationException("Finish the existing sharing edits first.");
            NavigateTo("Sharing");
            if (_sharingPage?.Visibility != System.Windows.Visibility.Visible)
                throw new SyncCliOperationException("Finish the current draft before opening the sharing editor.");
            await OpenAccountLinkEditorAsync(current);
            return new { editorRequested = true, accountId = connection.ConnectionId, current.Url,
                current.PermissionId, note = "Opening the editor does not claim that link changes have been saved. Private password input, when supported, stays in the app." };
        }
        AccountSharingLookup.ValidateUrl(current.Url);
        LaunchCliLocation(new ProcessStartInfo(current.Url) { UseShellExecute = true });
        return new { opened = true, accountId = connection.ConnectionId, current.Url };
    }

    private void LaunchCliLocation(ProcessStartInfo start)
    {
        if (CliLocationLauncherForTest is { } launch) launch(start);
        else Process.Start(start);
    }
    private static bool CliSamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private static bool CliRootContains(string root, string path) => CliSamePath(root, path) ||
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
