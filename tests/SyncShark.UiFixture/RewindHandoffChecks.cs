using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

namespace SyncShark.UiFixture;

// M25: actual GUI confirmation and actual CLI JSON handoff to the real Host. Only the provider
// input is the existing Core fixture. No fake Apply/Preview result, fabricated consent decision,
// private Host invocation or changed product wiring. The actual modal dialog buttons are exercised.
internal static class RewindHandoffChecks
{
    private const string PairId = "61700000000000000000000000000025";
    private const string Refusal = "The Rewind session or preview changed after it was reviewed. " +
        "Detect and review the current preview, then confirm that exact batch; no new local effect was issued.";

    internal static void Run(string root, ThemeManager theme)
    {
        Directory.CreateDirectory(root);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = timeout.Token;
        var remote = RemoteInput.Create();
        var localRoot = Path.Combine(root, "local");
        Directory.CreateDirectory(localRoot);
        File.WriteAllText(Path.Combine(localRoot, "replace.txt"), "checkpoint replacement");
        File.WriteAllText(Path.Combine(localRoot, "removed.txt"), "checkpoint deletion");
        remote.Put("replace.txt", "checkpoint replacement");
        remote.Put("removed.txt", "checkpoint deletion");
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "dbid:m25-controlled", "root-m25-controlled", "home-m25-controlled",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes).Normalize();
        var pairs = new SyncPairStore(instance.StateAuthority);
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        Pump(connections.SaveNewAsync(binding, token));
        var installationId = Pump(pairs.GetOrCreateInstallationIdAsync(token));
        SyncPair pair;
        using (var local = PilotDirectoryLease.Open(localRoot))
        {
            var control = SyncControlNamespace.BindLocalForTest(local, installationId, PairId);
            var remoteControlRoot = SyncControlNamespace.RemoteContainerPath + "/" + PairId;
            var remoteControl = new SyncRemoteControlNamespaceBinding(remoteControlRoot, "id:control-" + PairId,
                remoteControlRoot + "/.syncshark-owner.json", "id:marker-" + PairId, "rev:marker-1",
                DropboxContentHash.Compute(SyncControlNamespace.MarkerBytes(installationId, PairId)));
            pair = new SyncPair(PairId, "M25 reviewed rollback", local.Path, local.Identity.VolumeSerialNumber,
                local.Identity.FileId, "/M25ReviewedRollback", "id:m25-reviewed-root", PropagateDeletions: true,
                ConnectionId: binding.ConnectionId, InitialReviewApproved: true, FilesOnDemandEnabled: false,
                RecoveryRoot: new SyncRemoteRecoveryRootBinding(SyncDropboxArtifactPaths.RecoveryContainerPath,
                    "id:m25-recovery", binding.RootNamespaceId),
                ControlNamespace: new SyncControlNamespaceBinding(installationId, PairId, control, remoteControl));
        }
        Pump(pairs.SaveAsync(new SyncPairSet([pair], pair.Id), token));
        Pump(new SyncAutomationStore(instance.StateAuthority).SaveAsync(new SyncAutomationSettings(false, 15, false), token));
        var transport = new DenyNetwork();
        using var http = new HttpClient(transport);
        var credentials = new ControlledCredentials(binding);
        var database = new SyncAuthorityDatabase(Path.Combine(root, "authority", "syncshark.db"));
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http, pairs,
            new SyncRuntimeStateStore(instance.StateAuthority), connections, credentials, database,
            (_, _) => remote.Store, googleDriveCredentialStore: credentials);
        Pump(host.LoadPairsAsync(token));
        Pump(host.SaveTransferSettingsAsync(host.LoadTransferSettings() with { ResourceMode = "Performance" }, token));
        Pump(database.RegisterPairAsync(pair.Id, pair.PolicyRevision,
            new SyncAuthorityPairConfiguration("recoverable", 0, 1, "manual_preserve_both",
                "content_times_basic_attrs", RecursiveMode: true, AutomaticPruning: false,
                RecoveryRetentionDays: 30), token));
        Pump(database.RetainPairDefinitionAsync(pair, true, token));
        var baseline = Pump(host.RunPairsAsync([pair], "M25 effect-free baseline", cancellationToken: token));
        Check(baseline.Items.Single().Result is { Blocked: 0, Conflicts: 0 } && remote.Downloads == 0 &&
              remote.Uploads == 0, "M25/PREP real Host establishes an effect-free synchronized checkpoint input");
        pair = Pump(host.LoadPairsAsync(token)).Pairs.Single();
        var priorAccountStatus = MainWindow.DropboxConnectionStatusLoaderForTest;
        var priorBrowser = MainWindow.PairFolderBrowserFactoryForTest;
        var browser = new ScriptedFolderBrowserService
            { ListHandler = (_, _) => Task.FromResult(new DropboxFolderPage([], null)) };
        // The controlled provider does not supply live OAuth. Use real retained account metadata
        // for background health display only; no Preview/Apply/consent or outcome seam is replaced.
        MainWindow.DropboxConnectionStatusLoaderForTest = _ => host.GetDropboxConnectionStatusAsync(verifyLive: false);
        // Pair-editor initialization is incidental to Rewind. Supply folder-navigation input
        // without live OAuth; the real Host's checkpoint, Preview, Apply and consent stay intact.
        MainWindow.PairFolderBrowserFactoryForTest = _ => Task.FromResult<IDropboxFolderBrowserService>(browser);
        var window = new MainWindow(instance.StateAuthority, theme, http, host)
        {
            Width = 1240, Height = 790, Left = -32000, Top = -32000,
            ShowInTaskbar = false, ShowActivated = false
        };
        var phases = new List<string>();
        var phase = Element<TextBlock>(window, "PhaseText");
        var observer = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        EventHandler recordPhase = (_, _) => phases.Add(phase.Text);
        observer.AddValueChanged(phase, recordPhase);
        try
        {
            window.Show();
            Pump(window.RefreshAfterCliCommandAsync());
            Call(window, "NavigateTo", "Rewind", true);
            Element<ComboBox>(window, "RewindPairBox").SelectedItem = window.PairRows.Single(item => item.Id == pair.Id);
            Pump(window.RefreshRewindStateAsync());
            Check(Element<TextBlock>(window, "RewindPathText").Text == pair.DropboxRoot &&
                  Element<Button>(window, "PrepareRewindButton").IsEnabled,
                "M25/GUI prepare surface names the exact selected Dropbox root");
            CheckRefreshPublication(window, pair.Id);
            var beforeCancel = Effects(host, remote, localRoot);
            Dialog(window, "PrepareRewind_Click", "Prepare Dropbox Rewind", dialog =>
            {
                Check(Element<TextBlock>(dialog, "DialogSummary").Text.Contains(pair.Name, StringComparison.Ordinal) &&
                      Element<TextBlock>(dialog, "DialogDetail").Text.Contains(pair.DropboxRoot, StringComparison.Ordinal) &&
                      Element<TextBlock>(dialog, "DialogDetail").Text.Contains("Expected account:", StringComparison.Ordinal) &&
                      Element<TextBlock>(dialog, "DialogDetail").Text.Contains("No Dropbox content changes", StringComparison.Ordinal),
                    "M25/GUI actual preparation dialog exposes selected pair, root, account and no-provider-change scope");
            }, approve: false);
            Check(Pump(host.LoadRewindSessionsAsync()).Count == 0 && Effects(host, remote, localRoot) == beforeCancel,
                "M25/GUI preparation cancellation creates no checkpoint or effect and launches no browser");

            // The GUI's successful Prepare branch deliberately opens an external browser. Use the
            // actual public CLI checkpoint for controlled setup; do not fake its Host handoff or launch.
            var checkpoint = Cli(host, instance.StateAuthority, token,
                ["--cli", "rewind", "checkpoint", "--pair", pair.Id, "--yes", "--json"]);
            Check(checkpoint.Exit == 0 && Active(host).Status == SyncRewindStatus.AwaitingDropbox,
                "M25/CLI real checkpoint entry reaches actual Host preparation and retained paused session");
            Pump(window.RefreshRewindStateAsync());
            remote.Put("replace.txt", "provider rewind replacement");
            remote.Delete("removed.txt"); // Controlled provider input, not a provider operation under test.
            Detect(window, host, token);
            AssertPreview(window, host, pair);

            // Replace the retained preview through the real CLI while the GUI still holds what it
            // displayed. Every mismatched identity must refuse through the actual registry/operations.
            var cliPreview = Preview(host, instance.StateAuthority, token);
            var consent = cliPreview.Confirmation;
            Check(consent.SessionId == Active(host).SessionId && consent.LocalPreviewScanId == Active(host).LocalPreviewScanId &&
                  consent.DropboxPreviewScanId == Active(host).DropboxPreviewScanId,
                "M25/CLI actual preview JSON exposes all three exact retained confirmation identities");
            foreach (var wrong in new[]
            {
                consent with { SessionId = "wrong-reviewed-session" },
                consent with { LocalPreviewScanId = "wrong-reviewed-local-scan" },
                consent with { DropboxPreviewScanId = "wrong-reviewed-dropbox-scan" }
            })
                RefuseCli(host, instance.StateAuthority, remote, localRoot, wrong, token);
            var replacement = Preview(host, instance.StateAuthority, token).Confirmation;
            Check(replacement != consent, "M25/CLI actual second preview replaces immutable scan identities");
            RefuseCli(host, instance.StateAuthority, remote, localRoot, consent, token);

            var staleGuiEffects = Effects(host, remote, localRoot);
            var replacementSession = JsonSerializer.Serialize(Active(host));
            phases.Clear();
            Dialog(window, "ApplyRewind_Click", "Apply Dropbox Rewind", dialog => AssertApplyDialog(dialog, pair), approve: true);
            Settle(window, host, token);
            Check(Effects(host, remote, localRoot) == staleGuiEffects &&
                  JsonSerializer.Serialize(Active(host)) == replacementSession && phases.Any(text => text.Contains(Refusal, StringComparison.Ordinal)),
                "M25/GUI cached preview superseded by actual CLI preview refuses; replacement session and all targets preserved");

            Detect(window, host, token);
            var cancelEffects = Effects(host, remote, localRoot);
            Dialog(window, "ApplyRewind_Click", "Apply Dropbox Rewind", dialog => AssertApplyDialog(dialog, pair), approve: false);
            Check(Effects(host, remote, localRoot) == cancelEffects && Active(host).Status == SyncRewindStatus.PreviewReady,
                "M25/GUI actual destructive confirmation cancellation leaves preview and target effects unchanged");

            // The crucial original race: the GUI has captured its displayed confirmation BEFORE
            // showing this modal dialog. A genuine CLI preview replaces durable consent while open.
            var duringDialogEffects = Effects(host, remote, localRoot);
            SyncRewindSession? duringDialogReplacement = null;
            phases.Clear();
            Dialog(window, "ApplyRewind_Click", "Apply Dropbox Rewind", dialog =>
            {
                AssertApplyDialog(dialog, pair);
                _ = Preview(host, instance.StateAuthority, token);
                duringDialogReplacement = Active(host);
            }, approve: true);
            Settle(window, host, token);
            Check(duringDialogReplacement is not null && Effects(host, remote, localRoot) == duringDialogEffects &&
                  JsonSerializer.Serialize(Active(host)) == JsonSerializer.Serialize(duringDialogReplacement) &&
                  phases.Any(text => text.Contains(Refusal, StringComparison.Ordinal)),
                "M25/GUI preview replaced while actual consent dialog is open refuses before forced deletion or replacement");

            Detect(window, host, token);
            var reviewed = SyncRewindApplyConfirmation.FromSession(Active(host));
            var admissions = new List<string>();
            host.RewindApplyPhaseForTest = stage => { admissions.Add(stage); return Task.CompletedTask; };
            Dialog(window, "ApplyRewind_Click", "Apply Dropbox Rewind", dialog => AssertApplyDialog(dialog, pair), approve: true);
            Settle(window, host, token);
            host.RewindApplyPhaseForTest = null;
            Check(admissions.Contains("confirmed_preview_admission", StringComparer.Ordinal) &&
                  SyncRewindApplyConfirmation.FromSession(Active(host)) == reviewed &&
                  Active(host).Status == SyncRewindStatus.AppliedPaused &&
                  Active(host).PathProgress is { Count: 2 } paths && paths.All(item => item.Status == SyncRewindPathStatus.Completed) &&
                  File.ReadAllText(Path.Combine(localRoot, "replace.txt")) == "provider rewind replacement" &&
                  !File.Exists(Path.Combine(localRoot, "removed.txt")) && remote.Uploads == 0,
                "M25/GUI unchanged displayed confirmation reaches real admission and applies only two reviewed paths, remaining paused");

            var resumed = Cli(host, instance.StateAuthority, token,
                ["--cli", "rewind", "resume", "--pair", pair.Id, "--yes", "--json"]);
            Check(resumed.Exit == 0 &&
                  Pump(host.LoadRewindSessionsAsync()).All(item => item.Status == SyncRewindStatus.Resumed),
                "M25/CLI actual resume closes the completed GUI-owned rollback safely");
            Check(Cli(host, instance.StateAuthority, token,
                ["--cli", "rewind", "checkpoint", "--pair", pair.Id, "--yes", "--json"]).Exit == 0,
                "M25/CLI actual new checkpoint is legitimate after completed handoff");
            remote.Put("replace.txt", "second provider rewind replacement");
            remote.Put("cli-restored.txt", "second provider restored payload");
            var fresh = Preview(host, instance.StateAuthority, token).Confirmation;
            admissions.Clear();
            host.RewindApplyPhaseForTest = stage => { admissions.Add(stage); return Task.CompletedTask; };
            var applied = Cli(host, instance.StateAuthority, token, ApplyArgs(pair.Id, fresh));
            host.RewindApplyPhaseForTest = null;
            using var appliedJson = JsonDocument.Parse(applied.Output);
            var returned = ReadConfirmation(appliedJson.RootElement.GetProperty("data"));
            Console.WriteLine("M25 exact-apply diagnostic: " + JsonSerializer.Serialize(new
            {
                exit = applied.Exit,
                ok = appliedJson.RootElement.GetProperty("ok").GetBoolean(),
                returnedSame = returned == fresh,
                admissions = admissions.ToArray(),
                status = Active(host).Status.ToString(),
                pathProgress = Active(host).PathProgress,
                replacementSame = File.ReadAllText(Path.Combine(localRoot, "replace.txt")) == "second provider rewind replacement",
                restoredSame = File.ReadAllText(Path.Combine(localRoot, "cli-restored.txt")) == "second provider restored payload",
                removedAbsent = !File.Exists(Path.Combine(localRoot, "removed.txt")),
                uploads = remote.Uploads,
                transportCalls = transport.Calls
            }));
            Check(applied.Exit == 0 && appliedJson.RootElement.GetProperty("ok").GetBoolean() && returned == fresh &&
                  admissions.Contains("confirmed_preview_admission", StringComparer.Ordinal) &&
                  Active(host).Status == SyncRewindStatus.AppliedPaused &&
                  File.ReadAllText(Path.Combine(localRoot, "replace.txt")) == "second provider rewind replacement" &&
                  File.ReadAllText(Path.Combine(localRoot, "cli-restored.txt")) == "second provider restored payload" &&
                  !File.Exists(Path.Combine(localRoot, "removed.txt")) && remote.Uploads == 0 && transport.Calls == 0,
                "M25/CLI exact confirmation read from real preview JSON reaches actual apply and returns the same identity with real bounded effects");
            Console.WriteLine("PASS M25 actual GUI/CLI Rewind confirmed-preview handoff; isolated provider inputs; browser-opening GUI Prepare success not exercised");
            Console.WriteLine("M25 used owned isolated file effects. The following legacy Recovery summary describes its original presentation-only cases, not this added handoff subfixture.");
        }
        finally
        {
            host.RewindApplyPhaseForTest = null;
            observer.RemoveValueChanged(phase, recordPhase);
            window.RequestProductExit();
            MainWindow.DropboxConnectionStatusLoaderForTest = priorAccountStatus;
            MainWindow.PairFolderBrowserFactoryForTest = priorBrowser;
        }
    }

    private static void CheckRefreshPublication(MainWindow window, string pairId)
    {
        var oldBarrier = window.BeforeRewindReadForTest;
        bool ControlsDisabled() => new[] { "PrepareRewindButton", "DetectRewindButton",
            "ApplyRewindButton", "ResumeRewindButton" }.All(name => !Element<Button>(window, name).IsEnabled);
        var pairBox = Element<ComboBox>(window, "RewindPairBox");
        var pair = window.PairRows.Single(item => item.Id == pairId);
        try
        {
            var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.BeforeRewindReadForTest = () => held.Task;
            var reading = window.RefreshRewindStateAsync();
            Check(!reading.IsCompleted && ControlsDisabled() &&
                  Element<TextBlock>(window, "RewindStatusText").Text.StartsWith("Reading", StringComparison.Ordinal),
                "M25/READ pending real checkpoint read disables all effects and does not claim absence");
            pairBox.SelectedItem = null;
            Check(ControlsDisabled(), "M25/READ selection cleared during read has no enabled action");
            pairBox.SelectedItem = pair;
            Check(ControlsDisabled(), "M25/READ returning to selected pair does not publish cached absence");
            held.SetResult(true);
            Pump(reading);
            Check(Element<Button>(window, "PrepareRewindButton").IsEnabled,
                "M25/READ completed real authority read publishes the current empty checkpoint state");

            var firstGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            window.BeforeRewindReadForTest = () => ++calls == 1 ? firstGate.Task : secondGate.Task;
            var first = window.RefreshRewindStateAsync();
            var second = window.RefreshRewindStateAsync();
            firstGate.SetResult(true);
            Pump(first);
            Check(!second.IsCompleted && ControlsDisabled(),
                "M25/READ superseded completion cannot clear the newer read's pending state");
            secondGate.SetResult(true);
            Pump(second);
            Check(Element<Button>(window, "PrepareRewindButton").IsEnabled,
                "M25/READ latest real read alone publishes usable checkpoint state");

            window.BeforeRewindReadForTest = () => Task.FromException(
                new IOException("Controlled checkpoint read boundary failure."));
            Pump(window.RefreshRewindStateAsync());
            Check(ControlsDisabled() && Element<TextBlock>(window, "RewindStatusText").Text.Contains(
                      "Controlled checkpoint read boundary failure", StringComparison.Ordinal),
                "M25/READ failed read reports failure and leaves every action disabled");
            window.BeforeRewindReadForTest = null;
            Pump(window.RefreshRewindStateAsync());
            Check(Element<Button>(window, "PrepareRewindButton").IsEnabled,
                "M25/READ successful real read after failure restores the permitted action");
        }
        finally
        {
            window.BeforeRewindReadForTest = oldBarrier;
        }
    }

    private static void AssertPreview(MainWindow window, SyncAuthorityRuntimeHost host, SyncPair pair)
    {
        window.UpdateLayout();
        var session = Active(host);
        var cached = Field<SyncRewindSession>(window, "_selectedRewindSession");
        Check(session.PairId == pair.Id && session.Status == SyncRewindStatus.PreviewReady &&
              JsonSerializer.Serialize(cached) == JsonSerializer.Serialize(session) &&
              session.Preview is { Count: 2 } && window.RewindRows.Count == 2 &&
              Element<DataGrid>(window, "RewindGrid").IsVisible && Element<DataGrid>(window, "RewindGrid").Items.Count == 2 &&
              window.RewindRows.Select(item => item.Path).Order().SequenceEqual(new[] { "removed.txt", "replace.txt" }) &&
              window.RewindRows.Any(item => item.Path == "removed.txt" && item.Effect.Contains("Remove", StringComparison.OrdinalIgnoreCase)) &&
              Element<Button>(window, "ApplyRewindButton").IsEnabled &&
              SyncRewindApplyConfirmation.FromSession(cached) == SyncRewindApplyConfirmation.FromSession(session),
            "M25/GUI actual Detect populates exact changed/removed paths and retains the displayed preview's three confirmation identities");
    }

    private static void AssertApplyDialog(AppDialogWindow dialog, SyncPair pair) =>
        Check(Element<TextBlock>(dialog, "DialogSummary").Text.Contains(pair.Name, StringComparison.Ordinal) &&
              Element<TextBlock>(dialog, "DialogDetail").Text.Contains("Dropbox rollback paths: 2", StringComparison.Ordinal) &&
              Element<TextBlock>(dialog, "DialogDetail").Text.Contains("pair remains paused", StringComparison.Ordinal) &&
              AutomationProperties.GetName(Element<Button>(dialog, "PrimaryButton")) == "Apply reviewed rollback",
            "M25/GUI real Apply dialog confirms exact selected pair and two displayed rollback paths with paused scope");

    private static void Detect(MainWindow window, SyncAuthorityRuntimeHost host, CancellationToken token)
    {
        Check(Element<Button>(window, "DetectRewindButton").IsEnabled,
            "M25/GUI actual Detect action is enabled for the retained checkpoint");
        Call(window, "DetectRewind_Click", Element<Button>(window, "DetectRewindButton"), new RoutedEventArgs());
        Settle(window, host, token);
        Check(Active(host).Status == SyncRewindStatus.PreviewReady, "M25/GUI actual Detect handler produces retained preview");
    }

    private static void RefuseCli(SyncAuthorityRuntimeHost host, PilotStateAuthority state, RemoteInput remote,
        string localRoot, SyncRewindApplyConfirmation confirmation, CancellationToken token)
    {
        var before = Effects(host, remote, localRoot);
        var retained = JsonSerializer.Serialize(Active(host));
        var refused = Cli(host, state, token, ApplyArgs(PairId, confirmation));
        using var json = JsonDocument.Parse(refused.Output);
        var error = json.RootElement.GetProperty("error");
        // Detailed CLI failures now include type, HRESULT and safe application frames. Preserve
        // the exact original refusal cause and its real guard entry, not the obsolete bare text.
        var diagnosticPrefix = SyncCli.DescribeException(new InvalidOperationException(Refusal)) +
            " [application: " + typeof(SyncAuthorityRuntimeHost).FullName + ".RequireConfirmedRewindSession <- ";
        Check(refused.Exit == (int)SyncCliExitCode.Failed && !json.RootElement.GetProperty("ok").GetBoolean() &&
              error.GetProperty("code").GetString() == "operation_failed" &&
              error.GetProperty("message").GetString()?.StartsWith(diagnosticPrefix, StringComparison.Ordinal) == true &&
              Effects(host, remote, localRoot) == before && JsonSerializer.Serialize(Active(host)) == retained && !host.HasActiveRun,
            "M25/CLI actual mismatched/stale session or preview refuses exactly before effects and preserves current preview");
    }

    private static (SyncRewindApplyConfirmation Confirmation, string Output) Preview(SyncAuthorityRuntimeHost host,
        PilotStateAuthority state, CancellationToken token)
    {
        var preview = Cli(host, state, token, ["--cli", "rewind", "preview", "--pair", PairId, "--yes", "--json"]);
        using var json = JsonDocument.Parse(preview.Output);
        Check(preview.Exit == 0 && json.RootElement.GetProperty("ok").GetBoolean(), "M25/CLI actual public preview JSON succeeds");
        return (ReadConfirmation(json.RootElement.GetProperty("data")), preview.Output);
    }

    private static SyncRewindApplyConfirmation ReadConfirmation(JsonElement data)
    {
        var value = data.GetProperty("rewindConfirmation");
        return new(value.GetProperty("sessionId").GetString()!, value.GetProperty("localPreviewScanId").GetString()!,
            value.GetProperty("dropboxPreviewScanId").GetString()!);
    }

    private static string[] ApplyArgs(string pairId, SyncRewindApplyConfirmation value) =>
        ["--cli", "rewind", "apply", "--pair", pairId, "--key", value.SessionId,
            "--source", value.LocalPreviewScanId, "--destination", value.DropboxPreviewScanId, "--yes", "--json"];

    private static (int Exit, string Output) Cli(SyncAuthorityRuntimeHost host, PilotStateAuthority state,
        CancellationToken token, string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var runtime = new CliAppRuntime(host, state, () => host.HasActiveRun, () => false,
            () => throw new InvalidOperationException("Unexpected app launch in Rewind fixture."), () => Task.CompletedTask);
        var exit = Pump(SyncCli.ExecuteAsync(args, output, error, () => runtime, token));
        Console.WriteLine("M25 actual CLI receipt " + output);
        return (exit, output.ToString());
    }

    private static SyncRewindSession Active(SyncAuthorityRuntimeHost host) => Pump(host.LoadRewindSessionsAsync())
        .Single(item => item.PairId == PairId && item.Status != SyncRewindStatus.Resumed);

    private static string Effects(SyncAuthorityRuntimeHost host, RemoteInput remote, string localRoot)
    {
        using var connection = new SqliteConnection("Data Source=" + host.Authority.DatabasePath);
        connection.Open();
        var rows = new List<string>();
        foreach (var table in new[] { "operation", "operation_attempt", "effect_receipt", "recovery_artifact", "operation_group" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            rows.Add(table);
            while (reader.Read()) rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? "<null>" : Convert.ToString(reader.GetValue(index),
                    System.Globalization.CultureInfo.InvariantCulture))));
        }
        var files = Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(localRoot, path) + ":" +
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        return JsonSerializer.Serialize(new { Rows = rows, Files = files, remote.Effects });
    }

    private static void Dialog(MainWindow owner, string handler, string title, Action<AppDialogWindow> inspect,
        bool approve)
    {
        var seen = false;
        Exception? failure = null;
        var clock = Stopwatch.StartNew();
        var queued = 0;
        using var pulse = new System.Threading.Timer(_ =>
        {
            if (Interlocked.CompareExchange(ref queued, 1, 0) != 0) return;
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                Interlocked.Exchange(ref queued, 0);
                if (seen) return;
                var dialog = Application.Current.Windows.OfType<AppDialogWindow>()
                    .SingleOrDefault(item => ReferenceEquals(item.Owner, owner) && item.Title == title);
                if (clock.Elapsed > TimeSpan.FromSeconds(15))
                {
                    seen = true;
                    failure = new TimeoutException("Rewind modal dialog deadline expired.");
                    dialog?.Close();
                    return;
                }
                if (dialog is null || !dialog.IsVisible) return;
                seen = true;
                try
                {
                    if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Rewind modal dialog deadline expired.");
                    inspect(dialog);
                    Element<Button>(dialog, approve ? "PrimaryButton" : "SecondaryButton")
                        .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }
                catch (Exception error)
                {
                    failure = error;
                    Element<Button>(dialog, "SecondaryButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }
            }));
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        var button = Element<Button>(owner, handler == "PrepareRewind_Click" ? "PrepareRewindButton" : "ApplyRewindButton");
        Check(button.IsEnabled, "M25/GUI actual " + handler + " control is enabled before invocation");
        Call(owner, handler, button, new RoutedEventArgs());
        if (failure is not null) throw new InvalidOperationException("Rewind actual modal assertion failed.", failure);
        Check(seen, "M25/GUI actual " + title + " dialog was presented and its real button handled consent");
    }

    private static void Settle(MainWindow window, SyncAuthorityRuntimeHost host, CancellationToken token) =>
        Until(() => !window.RunActiveForTest && !host.HasActiveRun && !Field<bool>(window, "_readingRunOutcomes"),
            "actual Rewind handler/readback", token, () => Console.WriteLine("Rewind settle progress: " + DescribeRun(window, host)));

    // A real Host inventory is the work being waited for, so the wait is bounded by the selector's own
    // cancellation token and reports the Host's actual phase instead of a fixed wall-clock deadline.
    private static string DescribeRun(MainWindow window, SyncAuthorityRuntimeHost host)
    {
        var run = host.ActiveRun;
        var comparison = run?.Progress?.Comparison;
        var checkedCount = comparison is { } value ? $"{value.CompletedChecks:N0}/{value.TotalChecks:N0}" : "<none>";
        var elapsed = run is { } active
            ? (DateTimeOffset.UtcNow - active.StartedUtc).TotalSeconds.ToString("F1",
                System.Globalization.CultureInfo.InvariantCulture)
            : "<none>";
        var uiPhase = window.FindName("PhaseText") is TextBlock phase ? phase.Text : string.Empty;
        var rewindStatus = window.FindName("RewindStatusText") is TextBlock status ? status.Text : string.Empty;
        return $"runActive={window.RunActiveForTest}; hostActive={host.HasActiveRun}; " +
               $"readingOutcomes={Field<bool>(window, "_readingRunOutcomes")}; " +
               $"kind={run?.Kind ?? "<none>"}; elapsedSeconds={elapsed}; " +
               $"state={run?.Progress?.State ?? "<none>"}; phase={comparison?.Phase ?? "<none>"}; " +
               $"path={comparison?.RelativePath ?? "<none>"}; checked={checkedCount}; " +
               $"uiPhase={uiPhase}; rewindStatus={rewindStatus}";
    }

    private static void Until(Func<bool> ready, string label) => Until(ready, label, default, null);

    private static void Until(Func<bool> ready, string label, CancellationToken token, Action? onProgress)
    {
        var started = Stopwatch.StartNew();
        TimeSpan? lastProgress = null;
        while (!ready())
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (onProgress is not null &&
                (lastProgress is null || started.Elapsed - lastProgress.Value >= TimeSpan.FromSeconds(2)))
            {
                lastProgress = started.Elapsed;
                onProgress();
            }
            if (token.CanBeCanceled)
            {
                if (token.IsCancellationRequested)
                    throw new TimeoutException("Timed out waiting for " + label + " before the selector's cancellation deadline.");
            }
            else if (started.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException("Timed out waiting for " + label);
            }
            Thread.Sleep(1);
        }
    }
    private static void Pump(Task task) { Until(() => task.IsCompleted, "actual fixture task"); task.GetAwaiter().GetResult(); }
    private static T Pump<T>(Task<T> task) { Pump((Task)task); return task.GetAwaiter().GetResult(); }
    private static T Element<T>(FrameworkElement parent, string name) => (T)(parent.FindName(name) ??
        throw new MissingMemberException(parent.GetType().Name, name));
    private static T Field<T>(object owner, string name) => (T)(owner.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(owner) ??
        throw new MissingMemberException(owner.GetType().Name, name));
    private static void Call(object owner, string name, params object?[] args) =>
        (owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingMethodException(owner.GetType().Name, name)).Invoke(owner, args);
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Rewind handoff: " + message);
        Console.WriteLine("PASS " + message);
    }

    private sealed class RemoteInput(ISyncRemoteStore store, object instance, Type type)
    {
        internal ISyncRemoteStore Store => store;
        internal static RemoteInput Create()
        {
            // Reuse the existing approved local test provider factory, not a copied remote simulator.
            // Reflection reaches fixture INPUT only; all Host/UI/CLI decisions remain real public paths.
            var factory = typeof(Program).GetMethod("CreateCoreTestRemote", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("Existing Core provider-input factory is unavailable.");
            var value = ((ISyncRemoteStore Store, object Instance, Type Type))(factory.Invoke(null, [null]) ??
                throw new InvalidDataException("Existing Core provider-input factory returned no input."));
            Console.WriteLine("M25 local provider-input assembly sha256 " + Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(value.Type.Assembly.Location))).ToLowerInvariant());
            return new(value.Store, value.Instance, value.Type);
        }
        internal void Put(string path, string value) => Invoke("Put", path, value);
        internal void Delete(string path) => Invoke("Delete", path);
        private void Invoke(string method, params object[] values) => (type.GetMethod(method) ??
            throw new MissingMethodException(type.Name, method)).Invoke(instance, values);
        private int Count(string property) => (int)(type.GetProperty(property)?.GetValue(instance) ??
            throw new MissingMemberException(type.Name, property));
        internal int Downloads => Count("DownloadCount");
        internal int Uploads => Count("UploadCount");
        internal string Effects => JsonSerializer.Serialize(new
        {
            Download = Downloads, Upload = Uploads, Archive = Count("ArchiveCount"), Delete = Count("DeleteCount"),
            Copy = Count("VisibleCopyCount"), Move = Count("MoveCount"), Directory = Count("DirectoryCreateCount"),
            Range = Count("RangeDownloadCount")
        });
    }

    private sealed class DenyNetwork : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
              Console.WriteLine("M25 denied HTTP input: " + request.Method.Method + " " +
                  request.RequestUri?.GetLeftPart(UriPartial.Path));
              Console.WriteLine("M25 denied HTTP caller types: " + string.Join(" <- ",
                  new StackTrace().GetFrames().Select(frame => frame.GetMethod()?.DeclaringType?.FullName)
                      .Where(type => type?.Contains("SyncShark", StringComparison.Ordinal) == true)));
            throw new InvalidOperationException("Rewind fixture denies every real HTTP request.");
        }
    }

    private sealed class ControlledCredentials(DropboxConnectionBinding expected)
        : IDropboxRefreshTokenStore, IGoogleDriveRefreshTokenStore
    {
        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding selected) =>
            selected.ConnectionId == expected.ConnectionId && selected.AccountId == expected.AccountId ? this :
                throw new InvalidOperationException("Rewind fixture denies an unrelated credential route.");
        public Task<string?> ReadAsync(CancellationToken token = default) => Task.FromResult<string?>("m25-synthetic-input");
        public Task<string?> ReadAsync(string id, CancellationToken token = default) =>
            throw new InvalidOperationException("Rewind fixture denies Google credential reads.");
        public Task WriteAsync(string value, CancellationToken token = default) =>
            throw new InvalidOperationException("Rewind fixture denies credential writes.");
        public Task WriteAsync(string id, string value, CancellationToken token = default) =>
            throw new InvalidOperationException("Rewind fixture denies credential writes.");
        public Task DeleteAsync(CancellationToken token = default) =>
            throw new InvalidOperationException("Rewind fixture denies credential deletion.");
        public Task DeleteAsync(string id, CancellationToken token = default) =>
            throw new InvalidOperationException("Rewind fixture denies credential deletion.");
    }
}
