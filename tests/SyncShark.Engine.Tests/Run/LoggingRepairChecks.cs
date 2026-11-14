using System.Collections.Concurrent;
using System.Text;
using System.Net;
using Massdex.SyncShark.Engine.Dropbox;
using System.Security.AccessControl;
using System.Security.Principal;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.V2;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Durability and quiet progress through the real direct Repair composition, with offline adapters.</summary>
internal static class LoggingRepairChecks
{
    internal static async Task RunAsync()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            await SavingAsync(Path.Combine(folder.FullName, "saved"), false);
            await SavingAsync(Path.Combine(folder.FullName, "refused"), true);
            await QuietAsync(Path.Combine(folder.FullName, "quiet"));
            await AutoSyncQuietAsync(Path.Combine(folder.FullName, "auto-quiet"));
            await HostFeedbackAsync(Path.Combine(folder.FullName, "host-feedback"));
            await EmptyQuietAsync(Path.Combine(folder.FullName, "empty-quiet"));
            await SchemaQuietAsync(Path.Combine(folder.FullName, "schema"), false);
            await SchemaQuietAsync(Path.Combine(folder.FullName, "schema-failure"), true);
            await MonitorPromotionAsync(Path.Combine(folder.FullName, "promotion"));
            await SourceRetryWaitAsync(Path.Combine(folder.FullName, "source-wait"));
            await SourceLossPublishesAllMembersAsync(Path.Combine(folder.FullName, "source-members"));
            await SourceLossPublishesAllMembersAsync(Path.Combine(folder.FullName, "source-members-several"), several: true);
            await ReplacementBaseAfterRestartWakesRepairAsync(Path.Combine(folder.FullName, "replacement-wake"));
            await BootstrapDebtSurvivesRecoverableLossAsync(Path.Combine(folder.FullName, "bootstrap-recovery"));
            await ExplicitRepairClearedRowStillRecoversAsync(Path.Combine(folder.FullName, "explicit-recovery"));
            foreach (var members in new[] { 1, 2 })
            {
                foreach (var kind in Enum.GetValues<SourceFault>())
                    await SourceRecoversAtDeadlineAsync(Path.Combine(folder.FullName, "deadline-" + kind + members), kind, false, members);
                await SourceRecoversAtDeadlineAsync(Path.Combine(folder.FullName, "deadline-failed-resume-" + members),
                    SourceFault.Resumed, true, members);
            }
            await SourceAttemptRetryAsync(Path.Combine(folder.FullName, "source-before"), afterCommit: false);
            await SourceAttemptRetryAsync(Path.Combine(folder.FullName, "source-after"), afterCommit: true);
            foreach (var afterCommit in new[] { false, true })
            {
                foreach (var kind in new[] { SourceFault.JournalChanged, SourceFault.BelowRetention, SourceFault.AheadOfEnd })
                    await SourceFailureAttemptsAsync(Path.Combine(folder.FullName, "source-" + kind + afterCommit), afterCommit, kind, false);
                await SourceFailureAttemptsAsync(Path.Combine(folder.FullName, "source-failed-resume-" + afterCommit), afterCommit,
                    SourceFault.Resumed, true);
            }
            await SourceAttemptRetryAsync(Path.Combine(folder.FullName, "superseded"), afterCommit: false, sourceFailed: false);
            await SupersededAccountingFailureAsync(Path.Combine(folder.FullName, "superseded-accounting"));
            await ScheduledSupersessionRecoveryAsync(Path.Combine(folder.FullName, "scheduled-superseded"));
            await IdleRetryHeartbeatAsync(Path.Combine(folder.FullName, "idle-retry"));
            await WaitingPublicationAsync(Path.Combine(folder.FullName, "waiting-context"), snapshot: false);
            await WaitingPublicationAsync(Path.Combine(folder.FullName, "waiting-snapshot"), snapshot: true);
            await WaitingObservationCostsAsync(Path.Combine(folder.FullName, "waiting-costs"));
            await StoreSubscriberReleaseAsync(Path.Combine(folder.FullName, "store-subscriber"));
            await ConfigurationQuietAsync(Path.Combine(folder.FullName, "dispatch-config"), false);
            await ConfigurationQuietAsync(Path.Combine(folder.FullName, "persistent-config"), true);
            await HealthySourcesWithFailedSubscriberAsync(Path.Combine(folder.FullName, "source-subscriber"));
            await FailedFinalSubscriberAsync(Path.Combine(folder.FullName, "subscriber"), false);
            await FailedFinalSubscriberAsync(Path.Combine(folder.FullName, "metadata"), true);
            await InaccessiblePruneAsync(Path.Combine(folder.FullName, "prune"));
            await ExtremeRetentionAsync(Path.Combine(folder.FullName, "retention"), false);
            await ExtremeRetentionAsync(Path.Combine(folder.FullName, "retention-writer"), true);
        }
        finally { folder.Delete(recursive: true); }
    }

    private static async Task SavingAsync(string folder, bool throws)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var rig = await RepairChecks.ComposeRigAsync(folder, decorate: store => new HookStore(store)
        {
            BeforeFinishRepair = () =>
            {
                entered.TrySetResult();
                Check.Require(release.Wait(TimeSpan.FromSeconds(30)), "release durable save barrier");
                if (throws) throw new IOException("synthetic persistence failure");
            }
        });
        using var log = rig.Log(folder, "saving");
        var run = Task.Run(() => rig.RunAsync(log, CancellationToken.None));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var lines = Lines(log.Path);
            Check.Require(lines.Any(fields => fields[1] == RunLog.ProvisionalKind), "provisional counts are flushed");
            Check.Require(!lines.Any(SavingEnded) && !lines.Any(TerminalSuccess),
                "neither saving-end nor terminal success precedes durable FinishRepair");
        }
        finally { release.Set(); }
        try
        {
            var summary = await run;
            Check.Require(!throws && summary.Completed && rig.Store.LastRepair(rig.Pair.PairId)!.Completed,
                "successful save is durable before returning completion");
        }
        catch (IOException) when (throws) { }
        var final = Lines(log.Path);
        Check.Require(final.Any(SavingEnded) == !throws && final.Any(TerminalSuccess) == !throws,
            "only a successful durable save closes saving and asserts terminal success");
    }

    private static async Task FailedFinalSubscriberAsync(string folder, bool metadataFails)
    {
        const string secret = "private diagnostic persistence text";
        var metadataAttempted = false;
        await using var rig = await RepairChecks.ComposeRigAsync(folder, decorate: store => new HookStore(store)
        {
            BeforeMarkRepairLogFailed = () =>
            {
                metadataAttempted = true;
                if (metadataFails) throw new Microsoft.Data.Sqlite.SqliteException(secret, 13);
            }
        });
        var threw = false;
        using var log = RunLog.Open(folder, rig.Pair.PairId, "subscriber", message =>
        {
            if (rig.Store.LastRepair(rig.Pair.PairId)?.Completed != true) return;
            Check.Require(message.Contains(Text.Get(TextKey.StageFinish), StringComparison.Ordinal),
                "the throwing callback is specifically the post-commit saving-end boundary");
            threw = true;
            throw new IOException("synthetic closed progress stream");
        }, rig.Host.Settings.LogBufferCharacters);
        var summary = await rig.RunAsync(log, CancellationToken.None);
        var saved = rig.Store.LastRepair(rig.Pair.PairId)!;
        Check.Require(threw && metadataAttempted && summary.Completed && summary.LogFailed && saved.Completed &&
            saved.LogFailed == !metadataFails,
            "diagnostic persistence failure preserves completion without pretending metadata was saved");
        var lines = Lines(log.Path);
        Check.Require(lines.Any(TerminalSuccess), "subscriber failure still writes terminal success");
        Check.Require(!lines.SelectMany(fields => fields).Any(field => field.Contains(secret, StringComparison.Ordinal)),
            "diagnostic persistence failure never logs raw exception text");
        if (metadataFails)
            Check.Require(lines.Any(fields => fields[1] == nameof(RunEventKind.Failed) &&
                fields[2] == nameof(Microsoft.Data.Sqlite.SqliteException)), "metadata failure is safely diagnosed");
    }

    private static async Task InaccessiblePruneAsync(string folder)
    {
        await using var rig = await RepairChecks.ComposeRigAsync(folder);
        LogFiles.Prune(Path.Combine(folder, "missing"), TimeSpan.Zero, DateTimeOffset.UtcNow);
        var logs = Directory.CreateDirectory(Path.Combine(folder, "logs"));
        using var log = rig.Log(logs.FullName, "prune");
        var original = logs.GetAccessControl();
        var denied = logs.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        denied.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory,
            AccessControlType.Deny));
        try
        {
            logs.SetAccessControl(denied);
            var inaccessible = false;
            try { _ = Directory.EnumerateFiles(logs.FullName).ToArray(); }
            catch (UnauthorizedAccessException) { inaccessible = true; }
            Check.Require(inaccessible, "synthetic log directory refuses enumeration");
            var summary = await rig.RunAsync(log, CancellationToken.None);
            Check.Require(summary.Completed && rig.Store.LastRepair(rig.Pair.PairId)!.Completed,
                "inaccessible post-commit log pruning preserves returned and persisted Repair completion");
        }
        finally
        {
            denied.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            logs.SetAccessControl(denied);
        }
        log.Dispose();
        logs.Delete(recursive: true);
    }

    private static async Task ExtremeRetentionAsync(string folder, bool writerFails)
    {
        using var defaults = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        var input = SettingsParser.WithOverride(null, nameof(EngineSettings.LogRetentionDays),
            int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var settings = SettingsParser.Parse(defaults.RootElement.GetRawText(), input);
        await using var rig = await RepairChecks.ComposeRigAsync(folder, settings: settings);
        using var log = rig.Log(folder, "retention");
        if (writerFails) RepairChecks.FailOnLogEvent(log, nameof(RunEventKind.Failed), nameof(OverflowException));
        var summary = await rig.RunAsync(log, CancellationToken.None);
        var saved = rig.Store.LastRepair(rig.Pair.PairId)!;
        Check.Require(summary.Completed && saved.Completed,
            "accepted extreme retention cannot revoke committed Repair completion");
        Check.Require(log.HasFailed == writerFails && summary.LogFailed == writerFails && saved.LogFailed == writerFails,
            "late retention diagnostic writer failure agrees with returned and persisted logging health");
        var lines = Lines(log.Path);
        Check.Require(lines.Any(TerminalSuccess), "the writer succeeds through authoritative completion output");
        Check.Require(lines.Any(fields => fields[1] == nameof(RunEventKind.Failed) &&
            fields[2] == nameof(OverflowException)) == !writerFails,
            "unrepresentable retention is diagnosed unless its writer fails");
    }

    private static bool SavingEnded(string[] fields) => fields[1] == nameof(RunEventKind.Stage) &&
        fields[2] == nameof(TextKey.StageFinish) && fields[5] == nameof(RunEvent.Ended);

    private static bool TerminalSuccess(string[] fields) => fields[1] == RunLog.CompleteKind && fields[5] == "true";

    private static async Task QuietAsync(string folder)
    {
        var clock = new QuietClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var rig = await RepairChecks.ComposeRigAsync(folder, time: clock,
            decorate: store => new HookStore(store)
            {
                WrapSink = (_, sink) => new HeldSink(sink, () =>
                {
                    if (Interlocked.Increment(ref count) == 2) entered.TrySetResult();
                }, release.Task)
            });
        var modified = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(rig.Pair.LocalRoot, "Visible");
        await File.WriteAllTextAsync(path, "content");
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        rig.Wire.SeedFile("Visible", "content", modified);
        var progress = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = rig.Host.Settings;
        using var log = RunLog.Open(folder, rig.Pair.PairId, "quiet", message =>
        {
            rig.Host.ReportProgress(message);
            if (message.Contains("Visible", StringComparison.Ordinal)) progress.TrySetResult(message);
        }, settings.LogBufferCharacters, clock);
        var run = rig.Host.DispatchSessionAsync(rig.Pair.PairId, () => log,
            (session, token) => Repair.RunAsync(session, token), CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Check.Require(!progress.Task.IsCompleted, "listing facts remain coalesced before the quiet interval");
            for (var elapsed = 0; elapsed < 30; elapsed += 5)
            {
                rig.Host.ReportProgress("Pair A activity");
                clock.Advance(TimeSpan.FromSeconds(5));
            }
            var message = await progress.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Check.Require(message.Contains("1", StringComparison.Ordinal) && !run.IsCompleted,
                "direct Repair publishes intermediate path/count at the existing interval while sinks are blocked");
        }
        finally { release.TrySetResult(); }
        Check.Require((await run).Completed, "quiet observation does not change Repair completion");
    }

    private static async Task EmptyQuietAsync(string folder)
    {
        foreach (var throws in new[] { false, true })
        {
            var clock = new QuietClock();
            var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var armed = false;
            await using var rig = await RepairChecks.ComposeRigAsync(Path.Combine(folder, throws.ToString()), time: clock,
                progress: _ =>
                {
                    if (!armed) return;
                    observed.TrySetResult();
                    if (throws) throw new IOException("synthetic subscriber failure");
                });
            using var log = RunLog.Open(folder, rig.Pair.PairId, throws.ToString(), rig.Host.ReportProgress,
                rig.Host.Settings.LogBufferCharacters, clock);
            var run = rig.Host.DispatchSessionAsync(rig.Pair.PairId, () => log, async (_, _) =>
            {
                armed = true;
                active.TrySetResult();
                await release.Task;
                return true;
            }, default);
            try
            {
                await active.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await clock.AwaitArmedAsync();
                clock.Advance(TimeSpan.FromSeconds(31));
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(30));
                // Keep the run registered until delivery and diagnosis finish.
                await clock.AwaitArmedAsync();
                Check.Require(Lines(log.Path).Any(fields => fields[5] == nameof(RunEvent.InProgress)),
                    "an active run without snapshots flushes its quiet heartbeat");
            }
            finally { release.TrySetResult(); }
            Check.Require(await run, "quiet fallback cannot fail the active run");
            Check.Require(Lines(log.Path).Any(fields => fields[1] == nameof(RunEventKind.Failed)) == throws,
                "quiet subscriber failure is diagnosed in the active file");
        }
    }

    private static async Task HostFeedbackAsync(string folder)
    {
        RequestResult? healthy = null;
        foreach (var batch in new[] { false, true })
        foreach (var throws in new[] { false, true })
        {
            var root = Path.Combine(folder, $"{batch}-{throws}");
            var armed = false;
            var delivered = 0;
            await using var rig = await RepairChecks.ComposeRigAsync(root, progress: _ =>
            {
                if (!armed) return;
                delivered++;
                if (throws) throw new IOException("synthetic subscriber failure");
            });
            var before = (rig.Store.ReadSource(rig.Pair.PairId, Side.Local),
                rig.Store.ReadSource(rig.Pair.PairId, Side.Dropbox));
            using var log = rig.Log(root, "feedback");
            await rig.Host.DispatchSessionAsync(rig.Pair.PairId, () => log, async (session, token) =>
            {
                using var http = new HttpClient(new ThrottledResponse());
                var api = new DropboxApi(http, new FeedbackToken(), "namespace-a",
                    (AdaptiveLimiter)session.Admission, _ => ValueTask.FromResult<Stream>(new MemoryStream()),
                    rig.Host.Settings.Backoff, TimeProvider.System, "account-id", "source-a");
                armed = true;
                if (batch) api.ReportBatchOutcome(true);
                else
                {
                    var attempt = new RequestAttempt(new object(), AttemptOwner.Recovery);
                    Exception? failure = null;
                    try
                    {
                        await api.RpcAsync(DropboxProtocol.GetMetadata, new { }, DropboxReplyShapes.Void(), attempt, token);
                    }
                    catch (Exception error) { failure = error; }
                    Check.Require(failure is DropboxApiException && ReferenceEquals(attempt.Completion?.Error, failure),
                        "host subscriber cannot replace a classified provider refusal");
                    if (!throws) healthy = attempt.Completion!.Value.Result;
                    else Check.Require(attempt.Completion!.Value.Result == healthy,
                        "throwing host subscriber preserves the healthy request outcome");
                }
                Check.Require(delivered == 1 &&
                    Lines(log.Path).Any(fields => fields[1] == nameof(RunEventKind.Failed)) == throws,
                    "host feedback diagnoses one failed subscriber without escaping");
                return true;
            }, default);
            Check.Require(before == (rig.Store.ReadSource(rig.Pair.PairId, Side.Local),
                rig.Store.ReadSource(rig.Pair.PairId, Side.Dropbox)), "feedback preserves source validity");
            Check.Require(await rig.Host.DispatchSessionAsync(rig.Pair.PairId, () => log,
                (_, _) => Task.FromResult(true), default), "host remains usable after diagnostic feedback");
        }
    }

    private sealed class FeedbackToken : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("synthetic-token");
    }

    private sealed class ThrottledResponse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") });
    }

    private static async Task AutoSyncQuietAsync(string folder)
    {
        var clock = new QuietClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quiet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        SelectionStore? blocked = null;
        await using var rig = await RepairChecks.ComposeRigAsync(folder, time: clock,
            decorate: inner => new HookStore(blocked = new SelectionStore(inner, () =>
            {
                entered.TrySetResult();
                Check.Require(release.Wait(TimeSpan.FromSeconds(30)), "release local selection lookup");
            })));
        File.WriteAllText(Path.Combine(rig.Pair.LocalRoot, "A.txt"), "before");
        File.WriteAllText(Path.Combine(rig.Pair.LocalRoot, "Z.txt"), "before");
        using (var baseline = rig.Log(folder, "baseline")) await rig.RunAsync(baseline, default);
        File.WriteAllText(Path.Combine(rig.Pair.LocalRoot, "A.txt"), "after");
        File.WriteAllText(Path.Combine(rig.Pair.LocalRoot, "Z.txt"), "after");
        var generation = rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation;
        await rig.Host.CatchUpLocalAsync(rig.Pair.PairId, generation, default);
        rig.Store.EndWorkPass(rig.Pair.PairId);
        // B-137: the snapshot the pass reports is fixed when the pass starts, so the count taken here at the same
        // watermark is what the public AutoSync entry must print, live and in the saved log.
        var queued = rig.Store.CountWork(rig.Pair.PairId, generation, rig.Store.ReadWorkWatermark());
        Check.Require(queued.Local + queued.Dropbox > 0, "AutoSync starts with queued rows to snapshot");
        var live = new ConcurrentQueue<string>();
        using var log = RunLog.Open(folder, rig.Pair.PairId, "auto", message =>
        {
            live.Enqueue(message);
            if (message.Contains("progress;")) quiet.TrySetResult();
        }, rig.Host.Settings.LogBufferCharacters, clock);
        var auto = new Massdex.SyncShark.Engine.Run.AutoSync(rig.Store, _ => log, rig.Host);
        blocked!.Block = true;
        auto.Start();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // DESIGN-114 section 3.1: selection starts no longer imply an earlier lane has completed.
            await blocked.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await clock.AwaitArmedAsync();
            clock.Advance(TimeSpan.FromSeconds(31));
            await quiet.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var facts = Lines(log.Path).Where(fields => fields[2] == nameof(TextKey.StageRows) &&
                fields[5] == nameof(RunEvent.InProgress)).ToArray();
            Check.Require(facts.Any(fields => fields[3] == "Z.txt" && !fields[4].Contains("count 0 of")),
                "real AutoSync flushes completed branches and current local subject while selection is blocked");
            Check.Require(Lines(log.Path).Count(fields => fields[2] == nameof(TextKey.StageRows) && fields[5].Length == 0) == 1,
                "AutoSync starts one row stage, not one file line per selection");
            var sentence = Text.Get(TextKey.AutoSyncQueueAtStart,
                (TextArg.Count, (queued.Local + queued.Dropbox).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (TextArg.Local, queued.Local.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (TextArg.Dropbox, queued.Dropbox.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var snapshot = Lines(log.Path).Where(fields => fields[RunLog.FieldWhat] == nameof(TextKey.AutoSyncQueueAtStart)).ToArray();
            Check.Require(snapshot.Length == 1 && snapshot[0][RunLog.FieldDetail] == sentence &&
                    snapshot[0][RunLog.FieldOutcome] == nameof(RunEvent.InProgress),
                "real AutoSync writes one canonical queue snapshot with the counts it counted: " +
                string.Join("|", snapshot.Select(fields => fields[RunLog.FieldDetail])));
            Check.Require(live.Count(message => message.Contains(sentence, StringComparison.Ordinal)) == 1,
                "the live subscriber receives the queue snapshot sentence exactly once: " +
                string.Join("|", live.Where(message => message.Contains("queue", StringComparison.OrdinalIgnoreCase))));
        }
        finally
        {
            blocked.Block = false;
            release.Set();
            await auto.StopAsync();
        }
    }

    private sealed class SelectionStore(IStateStore inner, Action entered) : ForwardingStateStore(inner)
    {
        internal bool Block;
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CompleteWork(WorkSelection selection, SelectionPeer? peer)
        {
            var completed = base.CompleteWork(selection, peer);
            if (completed && selection.Key == PathNames.Key("A.txt")) Completed.TrySetResult();
            return completed;
        }
        public override SyncedEntry? SyncedByKey(string pairId, Side side, string key)
        {
            if (Block && side == Side.Local && key == PathNames.Key("Z.txt")) entered();
            return base.SyncedByKey(pairId, side, key);
        }
    }

    private static async Task SchemaQuietAsync(string folder, bool fails)
    {
        foreach (var mode in new[] { EngineHost.Mode.Direct, EngineHost.Mode.Persistent })
            await SchemaQuietModeAsync(Path.Combine(folder, mode.ToString()), fails, mode);
    }

    private static async Task SchemaQuietModeAsync(string folder, bool fails, EngineHost.Mode mode)
    {
        await using var rig = await RepairChecks.ComposeRigAsync(folder);
        var clock = new QuietClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quiet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var cause = new IOException("synthetic schema failure");
        using var log = RunLog.Open(folder, rig.Pair.PairId, "schema", message =>
        {
            if (message.Contains("progress;", StringComparison.Ordinal)) quiet.TrySetResult();
        }, rig.Host.Settings.LogBufferCharacters, clock);
        var store = new NoRetryStore(new InMemoryStateStore());
        await using var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        log.Event(RunEvent.Stage(TextKey.StageInstanceLock));
        monitor.Observe(() => log.ReportProgress());
        monitor.Start(quietOnly: true);
        await clock.AwaitArmedAsync();
        clock.Advance(TimeSpan.FromSeconds(31));
        await quiet.Task.WaitAsync(TimeSpan.FromSeconds(30));
        log.Event(RunEvent.Stage(TextKey.StageInstanceLock, ended: true));
        quiet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var composition = Task.Run(() =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(
                new Dropbox.ResponseStreamChecks.BlockedSynchronizationContext());
            try
            {
                return EngineHost.Compose(folder, _ => { }, rig.Host.Authority,
                    rig.Host.Settings, rig.Host.Http, clock, _ => null, _ =>
                    {
                        log.Event(RunEvent.Stage(TextKey.StageSchema));
                        entered.TrySetResult();
                        Check.Require(release.Wait(TimeSpan.FromSeconds(30)), "release blocked schema");
                        if (fails) throw cause;
                        log.Event(RunEvent.Stage(TextKey.StageSchema, ended: true));
                        return store;
                    }, mode, log, monitor);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await clock.AwaitArmedAsync();
            clock.Advance(TimeSpan.FromSeconds(31));
            await quiet.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Check.Require(!composition.IsCompleted && Lines(log.Path).Any(fields =>
                    fields[2] == nameof(TextKey.StageSchema) && fields[4].Contains("progress;", StringComparison.Ordinal)),
                "schema remains observable while the store factory is blocked beyond the quiet interval");
            Check.Require(clock.TimersCreated == 1 && Lines(log.Path).Count(fields =>
                fields[5] == nameof(RunEvent.InProgress)) == 2,
                "pre-composition observation transfers one timer and emits no duplicate heartbeat");
        }
        finally { release.Set(); }
        try
        {
            await using var host = await composition;
            Check.Require(!fails && store.RetryReads == 0,
                "direct composition and its quiet cycle never read or execute prerequisite retries");
        }
        catch (IOException error) when (fails)
        {
            Check.Require(ReferenceEquals(error, cause), "composition preserves the original schema failure");
            Check.Require(!clock.HasArmedTimer, "failed composition joins and disposes the quiet scheduler");
            log.Failed(error);
            var failures = Lines(log.Path).Where(fields => fields[1] == nameof(RunEventKind.Failed)).ToArray();
            Check.Require(failures.Length == 1 && failures[0].Any(field => field.Contains("[application:")),
                "schema failure remains one terminal record with its throw site across startup boundaries");
            using var closed = File.Open(log.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    private static async Task ConfigurationQuietAsync(string folder, bool persistent)
    {
        await using var rig = await RepairChecks.ComposeRigAsync(folder);
        var clock = new QuietClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quiet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var cause = new OperationCanceledException("synthetic configuration cancellation");
        using var log = RunLog.Open(folder, "pair-a", "configuration", message =>
        {
            if (message.Contains("progress;")) quiet.TrySetResult();
        }, rig.Host.Settings.LogBufferCharacters, clock);
        var store = new BlockedConfigurationStore(new InMemoryStateStore(), () =>
        {
            entered.TrySetResult();
            Check.Require(release.Wait(TimeSpan.FromSeconds(30)), "release blocked configuration");
            throw cause;
        });
        await using var host = EngineHost.Compose(folder, _ => { }, rig.Host.Authority,
            rig.Host.Settings, rig.Host.Http, clock, _ => log, _ => store,
            persistent ? EngineHost.Mode.Persistent : EngineHost.Mode.Direct, persistent ? log : null);
        var run = Task.Run(async () =>
        {
            if (persistent) await host.StartAsync(default);
            else await host.DispatchSessionAsync("pair-a", () => log, (_, _) => Task.FromResult(true), default);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await clock.AwaitArmedAsync();
            clock.Advance(TimeSpan.FromSeconds(31));
            await quiet.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Check.Require(!run.IsCompleted && Lines(log.Path).Any(fields =>
                fields[2] == nameof(TextKey.StageConfiguration) && fields[4].Contains("progress;")),
                "startup and AutoSync dispatch publish configuration before registration while setup is blocked");
        }
        finally { release.Set(); }
        try { await run; throw new InvalidOperationException("configuration must preserve cancellation"); }
        catch (OperationCanceledException error)
        {
            Check.Require(ReferenceEquals(error, cause), "startup preserves the original cancellation");
        }
        var failures = Lines(log.Path).Where(fields => fields[1] == nameof(RunEventKind.Failed)).ToArray();
        Check.Require(failures.Length == 1, "configuration cancellation has exactly one diagnosis in its early log");
        using (File.Open(log.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        store.Block = null;
        await host.DisposeAsync();
        await host.DisposeAsync();
        Check.Require(store.Disposals == 1 && !clock.HasArmedTimer,
            "startup log ownership and host resources tolerate repeated disposal without a remaining scheduler");
    }

    private sealed class BlockedConfigurationStore(IStateStore inner, Action block) : ForwardingStateStore(inner)
    {
        internal Action? Block = block;
        internal int Disposals;
        public override IReadOnlyList<PairConfig> Pairs()
        {
            Block?.Invoke();
            return base.Pairs();
        }
        public override void Dispose() { Disposals++; base.Dispose(); }
    }

    private static async Task StoreSubscriberReleaseAsync(string folder)
    {
        await using var rig = await RepairChecks.ComposeRigAsync(folder);
        var root = Path.Combine(folder, "composition");
        using var log = RunLog.Open(folder, "pair-a", "store-subscriber", message =>
        {
            if (message.Contains(Text.Get(TextKey.StageStore)) && message.Contains("Complete"))
                throw new IOException("synthetic subscriber failure");
        }, rig.Host.Settings.LogBufferCharacters);
        await using (var host = EngineHost.Compose(root, _ => { }, rig.Host.Authority, EngineHost.Mode.Direct,
            EngineStartupAdmission.Require(root), log))
            Check.Require(log.HasFailed && !host.Store.Pairs().Any(),
                "store-complete subscriber failure returns a usable host instead of orphaning it");
        using var database = File.Open(Massdex.SyncShark.Engine.Storage.StateStore.DatabasePath(root), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
    }

    private static async Task HealthySourcesWithFailedSubscriberAsync(string folder)
    {
        await using var rig = await RepairChecks.ComposeRigAsync(folder);
        var other = rig.Pair with { PairId = "pair-b", Drive = null };
        rig.Store.SavePair(other);
        await rig.Host.RecomposeCurrentAsync(other.PairId, default);
        await rig.Host.EnsureSourceBasesAsync(other.PairId, default);
        var pairs = new[] { rig.Pair.PairId, other.PairId };
        var before = pairs.Select(id => (Local: rig.Store.ReadSource(id, Side.Local),
            Remote: rig.Store.ReadSource(id, Side.Dropbox), Obligated: rig.Store.Obligated(id))).ToArray();
        using var log = RunLog.Open(folder, rig.Pair.PairId, "source-subscriber", message =>
        {
            if (message.Contains(Text.Get(TextKey.StageVolumeWait)) && message.Contains("Complete"))
                throw new IOException("synthetic subscriber failure");
        }, rig.Host.Settings.LogBufferCharacters);
        await rig.Host.EnsureSourceBasesAsync(rig.Pair.PairId, default, log);
        for (var index = 0; index < pairs.Length; index++)
        {
            var local = rig.Store.ReadSource(pairs[index], Side.Local);
            var remote = rig.Store.ReadSource(pairs[index], Side.Dropbox);
            Check.Require(local?.Epoch == before[index].Local?.Epoch && remote?.Epoch == before[index].Remote?.Epoch &&
                rig.Store.Obligated(pairs[index]) == before[index].Obligated,
                "a diagnostic subscriber failure cannot invalidate either volume member or create Repair debt");
        }
        Check.Require(log.HasFailed, "the diagnostic failure remains visible");
        await rig.Host.EnsureSourceBasesAsync(other.PairId, default);
    }

    private static async Task MonitorPromotionAsync(string folder)
    {
        await using var rig = await RepairChecks.ComposeRigAsync(folder);
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(rig.Host.Settings, new QuietClock(),
            _ => null, rig.Host.IsPaused, rig.Host);
        monitor.Start(quietOnly: true);
        monitor.Start();
        var refused = false;
        try { monitor.Start(); }
        catch (InvalidOperationException) { refused = true; }
        Check.Require(refused, "a quiet scheduler promotes only once");
        await monitor.StopAsync();
        refused = false;
        try { monitor.Start(); }
        catch (InvalidOperationException) { refused = true; }
        Check.Require(refused, "a stopped scheduler cannot restart or promote");
    }

    private static async Task SourceRetryWaitAsync(string folder)
    {
        var clock = new AdvancingClock();
        var paths = new List<string>();
        await using var rig = await RepairChecks.ComposeRigAsync(folder, time: clock, openLog: id =>
        {
            var log = RunLog.Open(folder, id, Guid.NewGuid().ToString("N"), _ => { }, 262144, clock);
            paths.Add(log.Path);
            return log;
        });
        clock.Reads = 0;
        FailJournal(rig.Host);
        var retry = rig.Store.ReadPrerequisiteRetry(rig.Pair.PairId)!.Value;
        var wait = paths.SelectMany(Lines).Single(fields => fields[1] == nameof(RunEventKind.Wait));
        var delay = double.Parse(wait[RunLog.FieldOutcome], System.Globalization.CultureInfo.InvariantCulture);
        Check.Require(retry.Attempts == 1 && rig.Store.Obligated(rig.Pair.PairId) &&
            !rig.Store.Issues(rig.Pair.PairId).Any(issue => issue.Cause == TextKey.RequiredRepairRetry),
            "source loss without an active attempt records one retry with no notice and preserves obligation");
        Check.Require(delay == (retry.RetryAfter - clock.FirstRead).TotalSeconds &&
            delay == rig.Host.Settings.Backoff.InitialBackoffSeconds,
            "the Wait carries the persisted delay from the single accounting clock read, not a later clock read");
        Check.Require(wait[RunLog.FieldWhat] == nameof(TextKey.WaitSourceRetry) &&
            paths.SelectMany(Lines).Count(fields => fields[1] == nameof(RunEventKind.Failed)) == 1,
            "source loss retains its original diagnostic and adds one truthful Wait");
    }

    // J1 (-11 §3): P1's accounting fails, yet P2 gets its row, Monitor wait, log and the status tail; after a
    // restart past P2's deadline its due wake recovers without Repair. Several failures aggregate.
    private static async Task SourceLossPublishesAllMembersAsync(string folder, bool several = false)
    {
        var clock = new FakeTimeProvider();
        // Real wall time, so the local file times the Repairs write are never in the clock's future.
        clock.Advance(DateTimeOffset.UtcNow - clock.GetUtcNow());
        var logs = new System.Collections.Concurrent.ConcurrentQueue<(string PairId, string Path)>();
        RunLog OpenLog(string id)
        {
            var log = RunLog.Open(folder, id, Guid.NewGuid().ToString("N"), _ => { }, 262144, clock);
            logs.Enqueue((id, log.Path));
            return log;
        }
        SourceFinishStore? observed = null;
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        var rig = await RepairChecks.ComposeRigAsync(folder, time: clock, monitor: monitor, openLog: OpenLog,
            decorate: store => new HookStore(observed = new SourceFinishStore(store, afterCommit: false)));
        var other = rig.Pair with { PairId = "pair-b", Drive = null };
        var members = new[] { rig.Pair.PairId, other.PairId };
        rig.Store.SavePair(other);
        await rig.Host.RecomposeCurrentAsync(other.PairId, default);
        await rig.Host.EnsureSourceBasesAsync(other.PairId, default);
        foreach (var pairId in members)
        {
            using var log = rig.Log(folder, pairId);
            Check.Require((await rig.RunAsync(log, default, targetPair: pairId)).Completed &&
                !rig.Store.Obligated(pairId), "each volume member starts without Repair debt");
        }
        monitor.Start();
        var failure = new IOException("synthetic first-member accounting failure");
        var loss = new IOException("synthetic catch-up source loss");
        observed!.AccountingFailure = failure;
        observed.FailFirstOnly = !several;
        var now = clock.GetUtcNow();
        var firstWait = TimeSpan.FromSeconds(rig.Host.Settings.Backoff.InitialBackoffSeconds);
        while (logs.TryDequeue(out _)) { }
        // A failed local catch-up is the public source-loss route for every member of its volume.
        observed.WatermarkFailure.Value = loss;
        Exception? caught = null;
        try
        {
            await rig.Host.CatchUpLocalAsync(rig.Pair.PairId,
                rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation, default);
        }
        catch (Exception error) { caught = error; }
        observed.WatermarkFailure.Value = null;
        Check.Require(ReferenceEquals(caught, loss), "the catch-up keeps its own failure as the failure");
        Exception? actual = null;
        try { await rig.Host.ChangeGenerationAsync(rig.Pair, (_, _, _, _) => Task.FromResult(true), default); }
        catch (Exception error) { actual = error; }
        Check.Require(!rig.Store.Obligated(rig.Pair.PairId) && !rig.Store.Obligated(other.PairId),
            "accounting failure for the first member cannot prevent the second member's retry publication, and a recoverable source loss adds no source-loss obligation");
        Check.Require(rig.Host.Status().Status == AutoStatus.Held,
            "J1 after the status tail the host reports the source failure");
        var written = logs.ToArray();
        foreach (var pairId in members)
            Check.Require(written.Where(log => log.PairId == pairId).SelectMany(log => Lines(log.Path))
                    .Count(fields => fields[RunLog.FieldKind] == nameof(RunEventKind.Failed)) == 1,
                "J1 both members' logs record the source failure");
        AggregateException? reported = null;
        if (several)
        {
            Check.Require(actual is AggregateException { InnerExceptions: [var first, var second] } &&
                    ReferenceEquals(first, failure) && ReferenceEquals(second, failure) && observed.Accountings == 0 &&
                    members.All(id => rig.Store.ReadPrerequisiteRetry(id) is null),
                "J1 several members' accounting failures are reported together; neither member gains debt or a deadline");
            await monitor.StopAsync();
            try { await rig.DisposeAsync(); }
            catch (AggregateException error) { reported = error; }
            Check.Require(reported?.InnerExceptions is [var only] && ReferenceEquals(only, actual),
                "host disposal reports the aggregated accounting failures once");
            return;
        }
        Check.Require(ReferenceEquals(actual, failure), "source-loss accounting preserves the original failure");
        var p2 = members.Single(id => rig.Store.ReadPrerequisiteRetry(id) is not null);
        var p1 = members.Single(id => id != p2);
        Check.Require(observed.Accountings == 1 &&
                rig.Store.ReadPrerequisiteRetry(p2) == new FailureRetryOwner.State(1, now + firstWait) &&
                rig.Store.ReadPrerequisiteRetry(p1) is null,
            "J1 the member whose accounting failed gets no invented deadline; the other gets the A01 first wait");
        await UntilAsync(() => clock.NextDueIn == firstWait);
        Check.Require(written.Where(log => log.PairId == p2).SelectMany(log => Lines(log.Path))
                .Count(fields => fields[RunLog.FieldKind] == nameof(RunEventKind.Wait) &&
                    fields[RunLog.FieldWhat] == nameof(TextKey.WaitSourceRetry)) == 1,
            "J1 the rearm armed Monitor's wait for P2 at its first wait, and P2's log carries its Wait");
        await monitor.StopAsync();
        try { await rig.DisposeAsync(); }
        catch (AggregateException error) { reported = error; }
        Check.Require(reported?.InnerExceptions is [var single] && ReferenceEquals(single, failure),
            "host disposal reports the preserved source-loss accounting failure once");

        // Restart on the same store past P2's deadline; nothing but the host's startup wake reaches either member.
        clock.Advance(firstWait);
        RestartFinishStore? restarted = null;
        var restartedMonitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        await using var after = await RepairChecks.ComposeRigAsync(folder, time: clock, monitor: restartedMonitor,
            mode: EngineHost.Mode.Persistent, wire: rig.Wire, openLog: OpenLog,
            decorate: store => new HookStore(restarted = new RestartFinishStore(store)));
        await after.Host.StartAsync(default);
        await UntilAsync(() => after.Store.ReadPrerequisiteRetry(p2) is null);
        await after.Host.CatchUpLocalAsync(p1, after.Store.ReadPairExecution(p1)!.Value.Generation, default);
        var quiet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (restartedMonitor.ScheduleQuietStatus(TimeSpan.Zero, () => quiet.TrySetResult()))
            await quiet.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Check.Require(restarted!.Finishes(p2) == 0 && after.Store.ReadPrerequisiteRetry(p2) is null &&
            !after.Store.Obligated(p2), "P2's due wake recovers without a Repair");
        Check.Require(restarted.Finishes(p1) == 0 && !after.Store.Obligated(p1),
            "P1 owes nothing after its recoverable loss");
    }

    // Counts committed Repair completions per pair for the J1 restart.
    private sealed class RestartFinishStore(IStateStore inner) : ForwardingStateStore(inner)
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _finishes = new();
        internal int Finishes(string pairId) => _finishes.GetValueOrDefault(pairId);
        public override bool FinishRepair(string pairId, RepairSummary summary, long expectedGeneration,
            Guid scanToken, LocalCatchUp local, DropboxCatchUp dropbox, out Exception? secondaryFailure)
        {
            var completed = base.FinishRepair(pairId, summary, expectedGeneration, scanToken, local, dropbox,
                out secondaryFailure);
            if (completed) _finishes.AddOrUpdate(pairId, 1, (_, count) => count + 1);
            return completed;
        }
    }

    private static async Task UntilAsync(Func<bool> condition, Func<string>? evidence = null)
    {
        var bound = Environment.TickCount64 + 30_000;
        while (!condition())
        {
            if (Environment.TickCount64 >= bound)
                Check.Require(false, "the awaited host state arrives" + (evidence is null ? "" : "; " + evidence()));
            await Task.Yield();
        }
    }

    private static void FailJournal(EngineHost host)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var volumes = (System.Collections.IDictionary)typeof(EngineHost).GetField("_volumes", flags)!.GetValue(host)!;
        typeof(EngineHost).GetMethod("JournalFailed", flags)!.Invoke(host,
            [volumes.Values.Cast<object>().Single(), new IOException("synthetic source loss")]);
    }

    private static async Task SourceAttemptRetryAsync(string folder, bool afterCommit, bool sourceFailed = true)
    {
        if (sourceFailed)
        {
            await SourceFailureAttemptsAsync(folder, afterCommit, SourceFault.Resumed, failedResume: false);
            return;
        }
        var clock = new QuietClock();
        var paths = new List<string>();
        SourceFinishStore? observed = null;
        await using var rig = await RepairChecks.ComposeRigAsync(folder, time: clock,
            decorate: store => new HookStore(observed = new SourceFinishStore(store, afterCommit)), openLog: id =>
            {
                var log = RunLog.Open(folder, id, Guid.NewGuid().ToString("N"), _ => { }, 262144, clock);
                paths.Add(log.Path);
                return log;
            });
        observed!.AtFinish = () =>
        {
            if (sourceFailed) FailJournal(rig.Host);
            else rig.Store.RequireRepairAfterLoss(rig.Pair.PairId,
                rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation);
        };
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var summary = await rig.Host.RunPrerequisiteAsync(rig.Pair.PairId, default);
            Check.Require(summary is not null && summary.Completed == afterCommit && summary.Superseded != afterCommit,
                "source loss after durable finish keeps success; loss before finish supersedes the attempt");
            Check.Require(rig.Store.Obligated(rig.Pair.PairId) &&
                rig.Store.ReadPrerequisiteRetry(rig.Pair.PairId) is not null && observed.Accountings == attempt,
                "source loss and its enclosing consequence select exactly one retry and preserve pending debt");
            Check.Require(!rig.Store.Issues(rig.Pair.PairId).Any(issue => issue.Cause == TextKey.RequiredRepairRetry),
                "repeated source loss never creates the generic prerequisite retry notice");
        }
        var reason = sourceFailed ? TextKey.WaitSourceRetry : TextKey.WaitSupersededRetry;
        Check.Require(paths.SelectMany(Lines).Count(fields => fields[RunLog.FieldKind] == nameof(RunEventKind.Wait) &&
            fields[RunLog.FieldWhat] == reason.ToString()) == 2,
            "each source-attributed attempt emits its selected Wait even after the attempt log completed");
        observed.AtFinish = null;
        Check.Require((await rig.Host.RunPrerequisiteAsync(rig.Pair.PairId, default)) is { Completed: true } &&
            !rig.Store.Obligated(rig.Pair.PairId) && rig.Store.ReadPrerequisiteRetry(rig.Pair.PairId) is null &&
            observed.Accountings == 2,
            "source recovery clears the retained debt and retry without a further failure increment");
    }

    // #36: with no earlier primary, a failed accounting step becomes the attempt's failure and is also logged.
    private static async Task SupersededAccountingFailureAsync(string folder)
    {
        var paths = new List<string>();
        SourceFinishStore? observed = null;
        await using var rig = await RepairChecks.ComposeRigAsync(folder,
            decorate: store => new HookStore(observed = new SourceFinishStore(store, afterCommit: false)), openLog: id =>
            {
                var log = RunLog.Open(folder, id, Guid.NewGuid().ToString("N"), _ => { }, 262144);
                paths.Add(log.Path);
                return log;
            });
        var failure = new IOException("synthetic superseded accounting failure");
        observed!.AtFinish = () => rig.Store.RequireRepairAfterLoss(rig.Pair.PairId,
            rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation);
        observed.AccountingFailure = failure;
        Exception? actual = null;
        try { await rig.Host.RunPrerequisiteAsync(rig.Pair.PairId, default); }
        catch (Exception error) { actual = error; }
        Check.Require(ReferenceEquals(actual, failure),
            "a superseded attempt with no earlier primary fails with its accounting failure");
        Check.Require(paths.SelectMany(Lines).Any(fields => fields.Length > RunLog.FieldWhat &&
            fields[RunLog.FieldKind] == nameof(RunEventKind.Failed) &&
            fields[RunLog.FieldWhat] == nameof(IOException)), "the failed accounting is also logged");
        Check.Require(rig.Store.Obligated(rig.Pair.PairId) && rig.Store.ReadPrerequisiteRetry(rig.Pair.PairId) is null,
            "failed accounting keeps the debt and invents no deadline");
    }

    private static async Task ScheduledSupersessionRecoveryAsync(string folder)
    {
        var clock = new FakeTimeProvider();
        for (var lifetime = 0; lifetime < 2; lifetime++)
        {
            var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
            SourceFinishStore? observed = null;
            await using var rig = await RepairChecks.ComposeRigAsync(folder, time: clock, monitor: monitor,
                decorate: store => new HookStore(observed = new SourceFinishStore(store, afterCommit: false)),
                openLog: id => RunLog.Open(folder, id, Guid.NewGuid().ToString("N"), _ => { }, 262144, clock));
            var pairId = rig.Pair.PairId;
            if (lifetime == 0)
            {
                observed!.AtFinish = () => rig.Store.RequireRepairAfterLoss(pairId,
                    rig.Store.ReadPairExecution(pairId)!.Value.Generation);
                Check.Require(await rig.Host.RunPrerequisiteAsync(pairId, default) is { Superseded: true },
                    "the first supersession leaves a durable scheduled opportunity");
            }
            else
                Check.Require(rig.Store.ReadPrerequisiteRetry(pairId) is { Attempts: 2 },
                    "two supersessions survive host restart without a new event");
            monitor.Start();
            var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (monitor.ScheduleQuietStatus(TimeSpan.Zero, () => refreshed.TrySetResult()))
                await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var next = rig.Store.ReadPrerequisiteRetry(pairId)!.Value;
            clock.Advance(next.RetryAfter - clock.GetUtcNow());
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (lifetime == 0 ? rig.Store.ReadPrerequisiteRetry(pairId)?.Attempts != 2 : rig.Store.Obligated(pairId))
            {
                bound.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            await monitor.StopAsync();
            Check.Require(lifetime == 0
                    ? observed!.Accountings == 2 && rig.Store.Obligated(pairId)
                    : observed!.Accountings == 0 && rig.Store.ReadPrerequisiteRetry(pairId) is null &&
                        rig.Store.LastRepair(pairId) is { Completed: true },
                "deadlines alone repeat supersession and recover after restart without duplicate accounting");
        }
    }

    private static bool MonitorIdle(Massdex.SyncShark.Engine.Run.Monitor monitor, string pairId)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = monitor.GetType();
        lock (type.GetField("_gate", flags)!.GetValue(monitor)!)
            return !((HashSet<string>)type.GetField("_opportunities", flags)!.GetValue(monitor)!).Contains(pairId) &&
                !((HashSet<string>)type.GetField("_runningPairs", flags)!.GetValue(monitor)!).Contains(pairId);
    }

    private static async Task BootstrapDebtSurvivesRecoverableLossAsync(string folder)
    {
        var clock = new FakeTimeProvider();
        clock.Advance(DateTimeOffset.UtcNow - clock.GetUtcNow());
        var logs = new ConcurrentQueue<string>();
        IStateStore raw = null!;
        SourceFinishStore observed = null!;
        RestartFinishStore finishes = null!;
        await using var rig = await RepairChecks.ComposeRigAsync(folder, time: clock, mode: EngineHost.Mode.Persistent,
            decorate: store =>
            {
                raw = store;
                return new HookStore(observed = new SourceFinishStore(finishes = new RestartFinishStore(store), false));
            }, openLog: id =>
            {
                var log = RunLog.Open(folder, id, Guid.NewGuid().ToString("N"), _ => { }, 262144, clock);
                logs.Enqueue(log.Path);
                return log;
            });
        var pairId = rig.Pair.PairId;
        rig.Wire.BeforeListingAsync = async (_, _) =>
        {
            rig.Wire.BeforeListingAsync = null;
            observed.ArmPageFault(SourceFault.Resumed);
            File.WriteAllText(Path.Combine(rig.Pair.LocalRoot, "trigger.txt"), "pending");
            await UntilAsync(() => logs.SelectMany(Lines).Any(fields =>
                fields[RunLog.FieldKind] == nameof(RunEventKind.Failed)));
        };
        await rig.Host.StartAsync(default);
        await UntilAsync(() => raw.ReadPrerequisiteRetry(pairId) is { Attempts: 1 } &&
            logs.SelectMany(Lines).Count(fields => fields[RunLog.FieldWhat] == nameof(TextKey.WaitSourceRetry)) == 1);
        Check.Require(raw.ReadSource(pairId, Side.Local)?.Obligation == SourceObligation.Bootstrap &&
            !raw.Issues(pairId).Any(issue => issue.Cause == TextKey.RequiredRepairRetry) && finishes.Finishes(pairId) == 0,
            "recoverable loss keeps Bootstrap and its source retry without a root notice");
        var wait = TimeSpan.FromSeconds(rig.Host.Settings.Backoff.InitialBackoffSeconds);
        await UntilAsync(() => clock.NextDueIn == wait);
        clock.Advance(wait);
        await UntilAsync(() => finishes.Finishes(pairId) == 1 && !raw.Obligated(pairId) &&
            raw.ReadPrerequisiteRetry(pairId) is null);
    }

    private static async Task ExplicitRepairClearedRowStillRecoversAsync(string folder)
    {
        var clock = new FakeTimeProvider();
        clock.Advance(DateTimeOffset.UtcNow - clock.GetUtcNow());
        var logs = new ConcurrentQueue<string>();
        IStateStore raw = null!;
        SourceFinishStore observed = null!;
        RestartFinishStore finishes = null!;
        var afterFinish = false;
        var rowCleared = false;
        string pairId = string.Empty;
        await using var rig = await RepairChecks.ComposeRigAsync(folder, time: clock, mode: EngineHost.Mode.Persistent,
            decorate: store =>
            {
                raw = store;
                return new HookStore(observed = new SourceFinishStore(finishes = new RestartFinishStore(store), false))
                {
                    AfterFinishRepair = () =>
                    {
                        if (!afterFinish) return;
                        rowCleared = raw.ReadPrerequisiteRetry(pairId) is null;
                        afterFinish = false;
                    }
                };
            }, openLog: id =>
            {
                var log = RunLog.Open(folder, id, Guid.NewGuid().ToString("N"), _ => { }, 262144, clock);
                logs.Enqueue(log.Path);
                return log;
            });
        pairId = rig.Pair.PairId;
        using (var log = rig.Log(folder, "initial"))
            Check.Require((await rig.RunAsync(log, default)).Completed, "explicit recovery starts after Bootstrap");
        await rig.Host.StartAsync(default);
        var generation = raw.ReadPairExecution(pairId)!.Value.Generation;
        await rig.Host.CatchUpLocalAsync(pairId, generation, default);
        await UntilAsync(() => EngineHostChecks.AutoSyncIdle(rig.Host, pairId));
        while (logs.TryDequeue(out _)) { }
        var now = clock.GetUtcNow();
        observed.AtFinish = () =>
        {
            observed.AtFinish = null;
            observed.ArmPageFault(SourceFault.Resumed);
            File.WriteAllText(Path.Combine(rig.Pair.LocalRoot, "trigger.txt"), "pending");
            Check.Require(SpinWait.SpinUntil(() => raw.ReadPrerequisiteRetry(pairId) is { Attempts: 1 } &&
                logs.SelectMany(Lines).Any(fields => fields[RunLog.FieldWhat] == nameof(TextKey.WaitSourceRetry)),
                TimeSpan.FromSeconds(30)), "background failure accounting completes before explicit finish");
            Check.Require(raw.ReadItemRetry(pairId) is not null &&
                !rig.Host.ItemRetryHolds(pairId, generation, out var admitted) && admitted is null,
                "the stopped source owns recovery before explicit finish clears the row");
            afterFinish = true;
        };
        var operations = new EngineOperations(rig.Instance.StateAuthority.ProductRoot, _ => { }, rig.Host);
        Check.Require(await operations.RepairAsync(pairId, default) is { Completed: true } && rowCleared,
            "explicit Repair commits and clears the debt-free source retry");
        await UntilAsync(() => rig.Host.Status().Status == AutoStatus.Idle);
        Check.Require(finishes.Finishes(pairId) == 2 && !raw.Obligated(pairId) &&
            raw.ReadPrerequisiteRetry(pairId) is null && clock.GetUtcNow() == now,
            "operation wake alone resumes the source without another Repair or clock advance");
    }

    private static async Task SourceRecoversAtDeadlineAsync(string folder, SourceFault kind, bool failedResume, int memberCount)
    {
        var clock = new FakeTimeProvider();
        clock.Advance(DateTimeOffset.UtcNow - clock.GetUtcNow());
        var logs = new ConcurrentQueue<(string PairId, string Path)>();
        IStateStore raw = null!;
        SourceFinishStore observed = null!;
        RestartFinishStore finishes = null!;
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        var gate = new object();
        var armed = false;
        var counting = false;
        var retryPages = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var snapshot = new TaskCompletionSource<Dictionary<string, int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var members = Array.Empty<string>();
        var normalDebt = new ConcurrentDictionary<string, bool>();
        await using var rig = await RepairChecks.ComposeRigAsync(folder, time: clock, monitor: monitor,
            mode: EngineHost.Mode.Persistent, openLog: id =>
            {
                var log = RunLog.Open(folder, id, Guid.NewGuid().ToString("N"), _ => { }, 262144, clock);
                logs.Enqueue((id, log.Path));
                return log;
            }, decorate: store =>
            {
                raw = store;
                var counted = Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore.Wrap(store);
                var hook = (Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore)(object)counted;
                hook.Before = method =>
                {
                    lock (gate)
                    {
                        if (armed && method.Name == nameof(IStateStore.ReadJournal)) { armed = false; counting = true; }
                        if (counting) counts[method.Name] = counts.GetValueOrDefault(method.Name) + 1;
                    }
                };
                hook.After = method =>
                {
                    if (method.Name != nameof(IStateStore.AcquireJournalPage) || !hook.LastPageHasDebt) return;
                    foreach (var id in members)
                        if (raw.ReadSource(id, Side.Local)?.Obligation == SourceObligation.NormalRepair) normalDebt[id] = true;
                };
                return new HookStore(observed = new SourceFinishStore(finishes = new RestartFinishStore(counted), false))
                {
                    AfterPrerequisiteRetries = () =>
                    {
                        lock (gate)
                        {
                            retryPages++;
                            if (!counting) return;
                            counting = false;
                            snapshot.TrySetResult(new Dictionary<string, int>(counts));
                        }
                    }
                };
            });
        members = memberCount == 1 ? [rig.Pair.PairId] : [rig.Pair.PairId, "pair-b"];
        if (memberCount == 2)
        {
            // One Local journal source, not one root: a sibling Local root on the same volume and a Dropbox root that
            // pair-a's scope holds out as nested (the ScenarioFixture.AddSecondPair topology; the wire has one tree).
            var local = Path.Combine(folder, members[1] + "-local");
            Directory.CreateDirectory(local);
            rig.Wire.SeedFolder(members[1]);
            var dropboxRoot = rig.Pair.DropboxRoot + "/" + members[1];
            var other = rig.Pair with
            {
                PairId = members[1], Drive = null, LocalRoot = local, DropboxRoot = dropboxRoot,
                Roots = rig.Pair.Roots! with
                {
                    LocalRoot = local, DropboxRoot = dropboxRoot, Local = WindowsDirectoryListing.ReadIdentity(local)
                }
            };
            raw.SavePair(other);
            await rig.Host.RecomposeCurrentAsync(members[1], default);
            var identity = await rig.Host.DispatchAsync(members[1], (registration, pair, generation, _) =>
                rig.Host.BorrowGeneration(registration, pair.PairId, generation).Dropbox.ReadRootIdentityAsync(default),
                default);
            raw.SavePair(other with { Roots = other.Roots! with { Dropbox = identity } });
            // Both generations recompose against the saved pairs, so pair-a's scope now holds pair-b's root out.
            foreach (var id in members)
            {
                await rig.Host.DrainGenerationAsync(id, default);
                await rig.Host.RecomposeCurrentAsync(id, default);
            }
            await rig.Host.EnsureSourceBasesAsync(members[1], default);
        }
        foreach (var id in members)
        {
            using var log = rig.Log(folder, "initial-" + id);
            Check.Require((await rig.RunAsync(log, default, targetPair: id)).Completed && !raw.Obligated(id),
                "each recovery fixture member starts with its initial Repair complete");
        }
        await rig.Host.StartAsync(default);
        await rig.Host.CatchUpLocalAsync(rig.Pair.PairId, raw.ReadPairExecution(rig.Pair.PairId)!.Value.Generation, default);
        bool Idle() => members.All(id => EngineHostChecks.AutoSyncIdle(rig.Host, id) && MonitorIdle(monitor, id));
        using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            do
            {
                await UntilAsync(Idle);
                var settled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (monitor.ScheduleQuietStatus(TimeSpan.Zero, () =>
                {
                    lock (gate) settled.TrySetResult(retryPages);
                }))
                {
                    var mark = await settled.Task.WaitAsync(bound.Token);
                    await UntilAsync(() => { lock (gate) return retryPages > mark; });
                }
                bound.Token.ThrowIfCancellationRequested();
            } while (!Idle());
        }
        while (logs.TryDequeue(out _)) { }
        lock (gate) armed = kind == SourceFault.Resumed && !failedResume;
        observed.ArmPageFault(kind);
        File.WriteAllText(Path.Combine(rig.Pair.LocalRoot, "trigger.txt"), "pending");
        if (kind == SourceFault.Resumed && !failedResume)
        {
            var measured = await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var expected = new Dictionary<string, int>
            {
                [nameof(IStateStore.RecordPrerequisiteFailure)] = memberCount,
                [nameof(IStateStore.ReadSource)] = 3 * memberCount,
                [nameof(IStateStore.ReadPrerequisiteRetry)] = memberCount,
                [nameof(IStateStore.ReadPairExecution)] = memberCount,
                [nameof(IStateStore.ReadJournal)] = 1,
                [nameof(IStateStore.ReadPrerequisiteRetries)] = 1
            };
            var evidence = string.Join(", ", measured.OrderBy(item => item.Key).Select(item => item.Key + "=" + item.Value));
            Console.WriteLine("B229 C1e c M=" + memberCount + ": " + evidence);
            foreach (var (method, count) in expected)
                Check.Require(measured.GetValueOrDefault(method) == count, "background recovery " + method + ": " + evidence);
            foreach (var method in measured.Keys)
                Check.Require(expected.ContainsKey(method), "unexpected background recovery method " + method + ": " + evidence);
        }
        bool Accounted(int attempts) => members.All(id => raw.ReadPrerequisiteRetry(id)?.Attempts == attempts &&
            logs.Where(log => log.PairId == id).SelectMany(log => Lines(log.Path))
                .Count(fields => fields[RunLog.FieldWhat] == nameof(TextKey.WaitSourceRetry)) == attempts);
        await UntilAsync(() => Accounted(1));
        foreach (var id in members)
        {
            Check.Require(!raw.Obligated(id) && logs.Where(log => log.PairId == id).SelectMany(log => Lines(log.Path))
                .Count(fields => fields[RunLog.FieldKind] == nameof(RunEventKind.Failed)) == 1,
                "the first source failure adds no debt and emits one failure per member");
            Check.Require(raw.ReadItemRetry(id) is not null &&
                !rig.Host.ItemRetryHolds(id, raw.ReadPairExecution(id)!.Value.Generation, out var admitted) && admitted is null,
                "the stopped source excludes the durable retry from item-only admission");
        }
        if (failedResume) observed.ArmPageFault(SourceFault.Resumed);
        var firstWait = TimeSpan.FromSeconds(rig.Host.Settings.Backoff.InitialBackoffSeconds);
        await UntilAsync(() => clock.NextDueIn == firstWait);
        clock.Advance(firstWait);
        if (failedResume)
        {
            await UntilAsync(() => Accounted(2));
            Check.Require(members.All(raw.Obligated), "the unconfirmed failed resume immediately obligates every member");
            var next = members.Select(id => raw.ReadPrerequisiteRetry(id)!.Value.RetryAfter).Min() - clock.GetUtcNow();
            await UntilAsync(() => clock.NextDueIn == next);
            clock.Advance(next);
        }
        var repair = kind != SourceFault.Resumed || failedResume;
        await UntilAsync(() => members.All(id => raw.ReadPrerequisiteRetry(id) is null && !raw.Obligated(id) &&
            finishes.Finishes(id) == (repair ? 2 : 1)));
        Check.Require(members.All(id => observed.ItemCompletions(id) == 0 && (!repair || normalDebt.ContainsKey(id))),
            "source recovery never retires an item-only retry; lost continuity repairs the observed normal debt; " +
            $"variant={nameof(SourceRecoversAtDeadlineAsync)}; kind={kind}; members={memberCount}; failedResume={failedResume}; continuity={(repair ? "lost" : "healthy")}; " +
            string.Join("; ", members.Select(id => $"member={id}, CompleteItemRetry={observed.ItemCompletions(id)}, " +
                $"CompleteItemRetryLastResult={observed.ItemCompletionResult(id)?.ToString() ?? "none recorded"}, " +
                $"observedDebtKinds={(normalDebt.ContainsKey(id) ? nameof(SourceObligation.NormalRepair) : "none recorded")}")));
        if (!repair)
        {
            var before = raw.ReadWorkWatermark();
            foreach (var id in members) File.WriteAllText(Path.Combine(raw.Pair(id)!.LocalRoot, "after.txt"), "later");
            // Diagnostic only: each conjunct of the wait below, per member, with its binding, notices and retry row.
            string AfterEvidence() =>
                $"watermarkAdvanced={raw.ReadWorkWatermark() > before} (before={before}, now={raw.ReadWorkWatermark()}); " +
                $"wireHasAfter={rig.Wire.Snapshot().Any(item => item.Path == "after.txt" && !item.Folder && Encoding.UTF8.GetString(item.Bytes) == "later")}; " +
                "wire=[" + string.Join(", ", rig.Wire.Snapshot().Where(item => item.Path.EndsWith("after.txt", StringComparison.Ordinal))
                    .Select(item => item.Path + (item.Folder ? " folder" : "=" + Encoding.UTF8.GetString(item.Bytes)))) + "]; " +
                $"hostStatus={rig.Host.Status()}; " + string.Join("; ", members.Select(id =>
                    $"member={id}, bound={raw.SyncedByKey(id, Side.Local, PathNames.Key("after.txt")) is not null}, " +
                    $"binding={raw.SyncedByKey(id, Side.Local, PathNames.Key("after.txt"))?.ToString() ?? "none"}, " +
                    $"retry={(raw.ReadPrerequisiteRetry(id) is { } row ? row.Attempts + "@" + row.RetryAfter.UtcTicks : "none")}, " +
                    $"obligated={raw.Obligated(id)}, issues=[" +
                    string.Join(", ", raw.Issues(id).Select(issue => issue.Kind + "/" + issue.Cause + "/" + issue.Path)) + "]"));
            await UntilAsync(() => raw.ReadWorkWatermark() > before && members.All(id =>
                raw.SyncedByKey(id, Side.Local, PathNames.Key("after.txt")) is not null) && rig.Wire.Snapshot()
                .Any(item => item.Path == "after.txt" && !item.Folder && Encoding.UTF8.GetString(item.Bytes) == "later"), AfterEvidence);
        }
    }

    private static async Task ReplacementBaseAfterRestartWakesRepairAsync(string folder)
    {
        var clock = new FakeTimeProvider();
        clock.Advance(DateTimeOffset.UtcNow - clock.GetUtcNow());
        var logs = new ConcurrentQueue<string>();
        RunLog OpenLog(string id)
        {
            var log = RunLog.Open(folder, id, Guid.NewGuid().ToString("N"), _ => { }, 262144, clock);
            logs.Enqueue(log.Path);
            return log;
        }
        IStateStore raw = null!;
        SourceFinishStore firstStore = null!;
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        var rig = await RepairChecks.ComposeRigAsync(folder, time: clock, monitor: monitor,
            mode: EngineHost.Mode.Persistent, openLog: OpenLog,
            decorate: store => { raw = store; return new HookStore(firstStore = new SourceFinishStore(store, false)); });
        var pairId = rig.Pair.PairId;
        var firstWait = TimeSpan.FromSeconds(rig.Host.Settings.Backoff.InitialBackoffSeconds);
        try
        {
            Check.Require(MonitorIdle(monitor, pairId), "the first base queues no replacement recovery wake");
            using (var log = rig.Log(folder, "initial"))
                Check.Require((await rig.RunAsync(log, default)).Completed && !raw.Obligated(pairId),
                    "the restart fixture begins with a completed Repair and no debt");
            await rig.Host.StartAsync(default);
            await rig.Host.CatchUpLocalAsync(pairId, raw.ReadPairExecution(pairId)!.Value.Generation, default);
            while (logs.TryDequeue(out _)) { }
            firstStore.ArmPageFault(SourceFault.Resumed);
            File.WriteAllText(Path.Combine(rig.Pair.LocalRoot, "trigger.txt"), "pending");
            await UntilAsync(() => raw.ReadPrerequisiteRetry(pairId) is { Attempts: 1 } && logs.SelectMany(Lines)
                .Any(fields => fields[RunLog.FieldWhat] == nameof(TextKey.WaitSourceRetry)));
            Check.Require(!raw.Obligated(pairId) && raw.ReadPrerequisiteRetry(pairId)!.Value.RetryAfter == clock.GetUtcNow() + firstWait,
                "the first source failure leaves a debt-free first retry");
        }
        finally
        {
            await monitor.StopAsync();
            await rig.DisposeAsync();
        }
        clock.Advance(firstWait);
        SourceFinishStore restarted = null!;
        RestartFinishStore finishes = null!;
        var secondMonitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        await using var after = await RepairChecks.ComposeRigAsync(folder, time: clock, monitor: secondMonitor,
            mode: EngineHost.Mode.Persistent, wire: rig.Wire, openLog: OpenLog, composeBeforeStart: false,
            decorate: store =>
            {
                raw = store;
                var pair = store.Pair(pairId)!;
                var generation = store.ReadPairExecution(pairId)!.Value.Generation;
                var source = store.ReadSource(pairId, Side.Local)!.Value;
                var volume = pair.Roots!.LocalVolumeKey();
                var saved = store.ReadJournal(volume)!;
                Check.Require(store.AcquireJournalPage(volume, saved, saved with { JournalId = saved.JournalId ^ 1UL },
                    [(pairId, generation, source.Epoch, source.Revision, false)], []) is not null,
                    "downtime changes only the saved journal identity");
                restarted = new SourceFinishStore(finishes = new RestartFinishStore(store), false);
                restarted.HoldLostInterval();
                return new HookStore(restarted);
            });
        try
        {
            await after.Host.StartAsync(default).WaitAsync(TimeSpan.FromSeconds(30));
            await restarted.LostIntervalEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await UntilAsync(() => raw.ReadPrerequisiteRetry(pairId) is null && restarted.ItemCompletions(pairId) == 1 &&
                !raw.Obligated(pairId) && finishes.Finishes(pairId) == 0 && MonitorIdle(secondMonitor, pairId));
            restarted.LostIntervalRelease.TrySetResult();
            await UntilAsync(() => finishes.Finishes(pairId) == 1 && !raw.Obligated(pairId) &&
                raw.ReadPrerequisiteRetry(pairId) is null && raw.LastRepair(pairId) is { Completed: true });
            Check.Require(restarted.ItemCompletions(pairId) == 1,
                "only the replacement-base wake reaches Repair after startup retired the item-only retry");
        }
        finally { restarted.LostIntervalRelease.TrySetResult(); }
    }

    private static async Task SourceFailureAttemptsAsync(string folder, bool afterCommit, SourceFault kind,
        bool failedResume)
    {
        var clock = new QuietClock();
        var paths = new List<string>();
        IStateStore raw = null!;
        SourceFinishStore observed = null!;
        RestartFinishStore finishes = null!;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var counterGate = new object();
        var counting = false;
        await using var rig = await RepairChecks.ComposeRigAsync(folder, time: clock,
            decorate: store =>
            {
                raw = store;
                var counted = Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore.Wrap(store);
                ((Massdex.SyncShark.Engine.Tests.Local.JournalSourceChecks.HookedStore)(object)counted).Before = method =>
                {
                    lock (counterGate)
                        if (counting) counts[method.Name] = counts.GetValueOrDefault(method.Name) + 1;
                };
                return new HookStore(observed = new SourceFinishStore(finishes = new RestartFinishStore(counted), afterCommit));
            }, openLog: id =>
            {
                var log = RunLog.Open(folder, id, Guid.NewGuid().ToString("N"), _ => { }, 262144, clock);
                paths.Add(log.Path);
                return log;
            });
        var pairId = rig.Pair.PairId;
        var generation = raw.ReadPairExecution(pairId)!.Value.Generation;
        observed.AtFinish = () =>
        {
            observed.AtFinish = null;
            var armed = observed.ArmPageFault(kind);
            File.WriteAllText(Path.Combine(rig.Pair.LocalRoot, "finish-trigger.txt"), "trigger");
            Exception? caught = null;
            try { rig.Host.CatchUpLocalAsync(pairId, generation, default).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(); }
            catch (Exception error) { caught = error; }
            Check.Require(ReferenceEquals(caught, armed), "the at-finish catch-up completes within the bound with its armed failure");
        };
        Exception? firstFailure = null;
        RepairSummary? first = null;
        try { first = await rig.Host.RunPrerequisiteAsync(pairId, default); }
        catch (Exception error) { firstFailure = error; }
        var refused = !afterCommit && kind is SourceFault.JournalChanged or SourceFault.BelowRetention;
        Check.Require(refused ? firstFailure is LiveAdmissionStaleException { Reason: LiveAdmissionStaleReason.CoverageChanged }
                : firstFailure is null && first is { Completed: true },
            "the first finish preserves its approved journal fence outcome: " + kind);
        Check.Require(raw.Obligated(pairId) == refused && raw.ReadPrerequisiteRetry(pairId) is { Attempts: 1 } &&
            observed.Accountings == 1, "the source failure is accounted once, preserving only the existing debt");
        var armedResume = failedResume ? observed.ArmPageFault(SourceFault.Resumed) : null;
        var previousLogs = paths.Count;
        lock (counterGate) counting = kind == SourceFault.Resumed;
        Exception? resumeFailure = null;
        RepairSummary? second = null;
        try { second = await rig.Host.RunPrerequisiteAsync(pairId, default); }
        catch (Exception error) { resumeFailure = error; }
        finally { lock (counterGate) counting = false; }
        if (kind == SourceFault.Resumed) RequireRecoveryCounts(counts, failedResume);
        if (kind == SourceFault.Resumed && !failedResume)
        {
            var lines = paths.Skip(previousLogs).SelectMany(Lines).ToArray();
            foreach (var stage in new[] { TextKey.StageVolumeWait, TextKey.StageJournalStop })
                Check.Require(lines.Count(fields => fields[RunLog.FieldWhat] == stage.ToString() &&
                    fields[RunLog.FieldOutcome] == nameof(RunEvent.Ended)) == 1,
                    "one completed recovery stage is logged: " + stage);
            var journal = lines.Single(fields => fields[RunLog.FieldWhat] == nameof(TextKey.StageJournal) &&
                fields[RunLog.FieldOutcome] == nameof(RunEvent.Ended));
            var format = Text.Get(TextKey.StageEnded, (TextArg.Name, Text.Get(TextKey.StageJournal)),
                (TextArg.Count, "RECORDS"), (TextArg.Seconds, "SECONDS"));
            var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(format)
                .Replace("RECORDS", "(?<records>[0-9]+)").Replace("SECONDS", ".*") + "$";
            var match = System.Text.RegularExpressions.Regex.Match(journal[RunLog.FieldDetail], pattern);
            Check.Require(match.Success, "the completed journal stage exposes its delivered-record count");
            var records = long.Parse(match.Groups["records"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var lookups = counts.GetValueOrDefault(nameof(IStateStore.SyncedById)) +
                counts.GetValueOrDefault(nameof(IStateStore.SyncedBindingsById));
            Check.Require(lookups <= records, "C1e recovery saved lookups do not exceed delivered records");
            Console.WriteLine($"B229 C1e a LOG: records={records}; lookups={lookups}; volume-waits=1; journal-stops=1");
        }
        if (failedResume)
        {
            Check.Require(ReferenceEquals(resumeFailure, armedResume) && raw.Obligated(pairId) &&
                raw.ReadPrerequisiteRetry(pairId) is { Attempts: 2 }, "the failed resume keeps debt and its second retry");
            Check.Require(await rig.Host.RunPrerequisiteAsync(pairId, default) is { Completed: true },
                "the next attempt repairs the failed resume");
        }
        else
            Check.Require(resumeFailure is null && (kind == SourceFault.Resumed ? second is null : second is { Completed: true }),
                "a continuous source resumes without Repair; an unproven interval runs Repair");
        Check.Require(!raw.Obligated(pairId) && raw.ReadPrerequisiteRetry(pairId) is null &&
            finishes.Finishes(pairId) == (kind == SourceFault.Resumed && !failedResume || refused ? 1 : 2),
            "only required whole-pair Repairs finish, and successful recovery clears the retry");
        Check.Require(!raw.Issues(pairId).Any(issue => issue.Cause == TextKey.RequiredRepairRetry) &&
            paths.SelectMany(Lines).Count(fields => fields[RunLog.FieldKind] == nameof(RunEventKind.Wait) &&
                fields[RunLog.FieldWhat] == nameof(TextKey.WaitSourceRetry)) == (failedResume ? 2 : 1),
            "each failed source attempt has one wait and no generic retry notice");
        Check.Require(await rig.Host.RunPrerequisiteAsync(pairId, default) is null, "a recovered pair owes no extra attempt");
    }

    private static void RequireRecoveryCounts(IReadOnlyDictionary<string, int> counts, bool failedResume)
    {
        var expected = failedResume
            ? new Dictionary<string, int>
            {
                [nameof(IStateStore.ReadPairExecution)] = 6, [nameof(IStateStore.ReadSource)] = 4,
                [nameof(IStateStore.ReadPrerequisiteRetry)] = 2, [nameof(IStateStore.HasOpenIssue)] = 1,
                [nameof(IStateStore.ReadItemRetry)] = 1, [nameof(IStateStore.Obligated)] = 1,
                [nameof(IStateStore.RequireRepairAfterLoss)] = 1, [nameof(IStateStore.RecordPrerequisiteFailure)] = 1,
                [nameof(IStateStore.ReadJournal)] = 1
            }
            : new Dictionary<string, int>
            {
                [nameof(IStateStore.ReadPairExecution)] = 5, [nameof(IStateStore.ReadPrerequisiteRetry)] = 3,
                [nameof(IStateStore.Obligated)] = 3, [nameof(IStateStore.ReadSource)] = 3,
                [nameof(IStateStore.HasOpenIssue)] = 1, [nameof(IStateStore.ReadItemRetry)] = 1,
                [nameof(IStateStore.CompleteRecomposition)] = 1, [nameof(IStateStore.ReadWorkWatermark)] = 1
            };
        var sourceMethods = new[] { nameof(IStateStore.AcquireJournalPage), nameof(IStateStore.SyncedById),
            nameof(IStateStore.SyncedBindingsById) };
        var evidence = string.Join(", ", counts.OrderBy(entry => entry.Key).Select(entry => entry.Key + "=" + entry.Value));
        Console.WriteLine("B229 C1e " + (failedResume ? "b" : "a") + ": " + evidence);
        foreach (var (method, count) in expected)
            Check.Require(counts.GetValueOrDefault(method) == count,
                "B229 C1e " + method + " expected " + count + "; measured " + evidence);
        foreach (var method in counts.Keys)
            Check.Require(expected.ContainsKey(method) || sourceMethods.Contains(method),
                "B229 C1e unexpected method " + method + "; measured " + evidence);
        if (!failedResume) Check.Require(counts.GetValueOrDefault(nameof(IStateStore.AcquireJournalPage)) >= 1,
            "the resumed capture durably publishes its boundary");
    }

    private enum SourceFault { Resumed, JournalChanged, BelowRetention, AheadOfEnd }

    private sealed class SourceFinishStore(IStateStore inner, bool afterCommit) : ForwardingStateStore(inner)
    {
        internal Action? AtFinish;
        private int _holdLostInterval;
        internal readonly TaskCompletionSource LostIntervalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource LostIntervalRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, int> _itemCompletions = new();
        private readonly ConcurrentDictionary<string, bool> _itemCompletionResults = new();
        internal int ItemCompletions(string pairId) => _itemCompletions.GetValueOrDefault(pairId);
        internal bool? ItemCompletionResult(string pairId) =>
            _itemCompletionResults.TryGetValue(pairId, out var completed) ? completed : null;
        internal void HoldLostInterval() => Interlocked.Exchange(ref _holdLostInterval, 1);
        public override bool CompleteItemRetry(string pairId, long expectedGeneration, FailureRetryOwner.State expected)
        {
            var completed = base.CompleteItemRetry(pairId, expectedGeneration, expected);
            _itemCompletionResults[pairId] = completed;
            _itemCompletions.AddOrUpdate(pairId, 1, (_, count) => count + 1);
            return completed;
        }
        private Tuple<SourceFault, IOException>? _pageFault;
        internal IOException ArmPageFault(SourceFault kind)
        {
            var error = new IOException("synthetic journal publication failure");
            Interlocked.Exchange(ref _pageFault, Tuple.Create(kind, error));
            return error;
        }
        public override long? AcquireJournalPage(string volumeKey, JournalPosition? expected, JournalPosition next,
            IReadOnlyList<(string PairId, long Generation, long Epoch, long Revision, bool LostInterval)> members,
            IEnumerable<(string PairId, ChangeNotice Notice)> notices)
        {
            if (members.Any(member => member.LostInterval) && Interlocked.Exchange(ref _holdLostInterval, 0) != 0)
            {
                LostIntervalEntered.TrySetResult();
                LostIntervalRelease.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            }
            if (Interlocked.Exchange(ref _pageFault, null) is { } fault)
            {
                var (kind, error) = fault;
                if (kind != SourceFault.Resumed)
                {
                    var saved = expected ?? throw new InvalidOperationException("the fault requires a committed position");
                    var checkpoint = NtfsUsnJournal.ReadCheckpoint(base.Pair(members[0].PairId)!.LocalRoot);
                    Check.Require(kind != SourceFault.BelowRetention ||
                        Math.Max(checkpoint.FirstUsn, checkpoint.LowestValidUsn) > 0,
                        "the below-retention fixture needs a nonzero retained start");
                    var bogus = kind switch
                    {
                        SourceFault.JournalChanged => saved with { JournalId = saved.JournalId ^ 1UL },
                        SourceFault.BelowRetention => saved with { NextUsn = 0 },
                        SourceFault.AheadOfEnd => saved with { NextUsn = checked((ulong)checkpoint.NextUsn + (1UL << 30)) },
                        _ => throw new InvalidOperationException()
                    };
                    base.AcquireJournalPage(volumeKey, expected, bogus,
                        members.Select(member => (member.PairId, member.Generation, member.Epoch, member.Revision, false)).ToArray(), []);
                }
                throw error;
            }
            return base.AcquireJournalPage(volumeKey, expected, next, members, notices);
        }
        internal Exception? AccountingFailure;
        // J1: only the first accounting call fails, whichever member the volume reaches first.
        internal bool FailFirstOnly;
        private int _failed;
        internal readonly AsyncLocal<Exception?> WatermarkFailure = new();
        internal int Accountings;
        public override long ReadWorkWatermark() =>
            WatermarkFailure.Value is { } failure ? throw failure : base.ReadWorkWatermark();
        public override bool FinishRepair(string pairId, RepairSummary summary, long expectedGeneration,
            Guid scanToken, LocalCatchUp local, DropboxCatchUp dropbox, out Exception? secondaryFailure)
        {
            if (!afterCommit) AtFinish?.Invoke();
            var completed = base.FinishRepair(pairId, summary, expectedGeneration, scanToken, local, dropbox, out secondaryFailure);
            if (afterCommit) AtFinish?.Invoke();
            return completed;
        }
        public override void RecordPrerequisiteFailure(string pairId, long expectedGeneration, long localEpoch,
            long dropboxEpoch, long localRevision, long dropboxRevision, FailureRetryOwner.State? expected,
            FailureRetryOwner.State next, StateChange failureNotice)
        {
            if (AccountingFailure is { } failure && (!FailFirstOnly || Interlocked.Exchange(ref _failed, 1) == 0))
                throw failure;
            base.RecordPrerequisiteFailure(pairId, expectedGeneration, localEpoch, dropboxEpoch,
                localRevision, dropboxRevision, expected, next, failureNotice);
            Accountings++;
        }
    }

    private sealed class AdvancingClock : TimeProvider
    {
        internal int Reads;
        internal DateTimeOffset FirstRead;
        public override DateTimeOffset GetUtcNow()
        {
            var now = DateTimeOffset.UnixEpoch.AddSeconds(Interlocked.Increment(ref Reads));
            if (Reads == 1) FirstRead = now;
            return now;
        }
    }

    private static async Task IdleRetryHeartbeatAsync(string folder)
    {
        var clock = new QuietClock();
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        var paths = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        EngineHost? host = null;
        NoRetryStore? observed = null;
        await using var rig = await RepairChecks.ComposeRigAsync(folder, time: clock, monitor: monitor,
            decorate: store => new HookStore(observed = new NoRetryStore(store)),
            progress: messages.Enqueue, openLog: id =>
            {
                var log = RunLog.Open(folder, id, Guid.NewGuid().ToString("N"),
                    message => host!.ReportProgress(message), 262144, clock);
                paths.Enqueue(log.Path);
                return log;
            });
        host = rig.Host;
        var pairId = rig.Pair.PairId;
        var generation = rig.Store.ReadPairExecution(pairId)!.Value.Generation;
        var local = rig.Store.ReadSource(pairId, Side.Local)!.Value;
        var remote = rig.Store.ReadSource(pairId, Side.Dropbox)!.Value;
        rig.Store.RecordPrerequisiteFailure(pairId, generation, local.Epoch, remote.Epoch,
            local.Revision, remote.Revision, null,
            new Massdex.SyncShark.V2.FailureRetryOwner.State(1, clock.GetUtcNow().AddHours(1)), StateChange.Empty);
        using (var completed = rig.Log(folder, "completed"))
            completed.Complete(new RepairSummary(completed.RunId, clock.GetUtcNow(), clock.GetUtcNow(),
                true, false, null, false, new Dictionary<string, int>(), new Dictionary<string, int>(), completed.Path, false));
        Check.Require(host.Status().Status == AutoStatus.Waiting, "idle durable retry is visible without an active log");
        using var subscription = new CancellationTokenSource();
        await using var status = host.SubscribeStatusAsync(subscription.Token).GetAsyncEnumerator();
        Check.Require(await status.MoveNextAsync(), "the waiting observer has a status subscriber");
        host.ReportProgress(Text.Get(TextKey.StatusStillQuiet));
        observed!.RetryReads = 0;
        monitor.Start(quietOnly: true);
        for (var observation = 0; observation < 2; observation++)
        {
            await clock.AwaitArmedAsync();
            clock.Advance(TimeSpan.FromSeconds(31));
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (paths.Count <= observation) { bound.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var barrier = monitor.ScheduleQuietStatus(TimeSpan.Zero, () => delivered.TrySetResult());
            await delivered.Task.WaitAsync(bound.Token);
        }
        await monitor.StopAsync();
        Check.Require(observed.RetryReads == 2,
            "each quiet publication reads its eligible retry once and reuses that observation for status delivery");
        var waiting = Text.Get(TextKey.StatusStillWaiting, (TextArg.Reason, nameof(AttemptOwner.Prerequisite)));
        Check.Require(messages.Count(message => message == waiting) == 2 && paths.Count == 2 &&
            paths.All(path => Lines(path).Any(fields => fields[RunLog.FieldDetail] == waiting)) &&
            paths.All(path => Lines(path).All(fields => fields[RunLog.FieldKind] != RunLog.CompleteKind)),
            "each idle heartbeat reaches output and a short-lived log, without a fake Repair summary");
        foreach (var path in paths)
        {
            using var closed = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        Check.Require(clock.TimersCreated == 1, "idle prerequisite visibility reuses the one scheduler timer");
    }

    private static async Task WaitingPublicationAsync(string folder, bool snapshot)
    {
        var clock = new QuietClock();
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        EngineHost? host = null;
        Task? child = null;
        var capture = false;
        await using var rig = await RepairChecks.ComposeRigAsync(folder, time: clock, monitor: monitor,
            progress: message =>
            {
                messages.Enqueue(message);
                if (!capture || child is not null) return;
                child = Task.Run(async () =>
                {
                    await releaseChild.Task;
                    host!.ReportProgress("later publication");
                });
                published.TrySetResult();
            });
        host = rig.Host;
        using var log = RunLog.Open(folder, rig.Pair.PairId, "waiting-publication", host.ReportProgress,
            host.Settings.LogBufferCharacters, clock);
        var running = host.DispatchSessionAsync(rig.Pair.PairId, () => log, async (_, _) =>
        {
            if (snapshot) log.Event(RunEvent.Stage(TextKey.StageRows));
            entered.TrySetResult();
            await releaseRun.Task;
            return true;
        }, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var generation = rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation;
            var local = rig.Store.ReadSource(rig.Pair.PairId, Side.Local)!.Value;
            var remote = rig.Store.ReadSource(rig.Pair.PairId, Side.Dropbox)!.Value;
            rig.Store.RecordPrerequisiteFailure(rig.Pair.PairId, generation, local.Epoch, remote.Epoch,
                local.Revision, remote.Revision, null,
                new FailureRetryOwner.State(1, clock.GetUtcNow().AddHours(1)), StateChange.Empty);
            using var subscription = new CancellationTokenSource();
            await using var status = host.SubscribeStatusAsync(subscription.Token).GetAsyncEnumerator();
            Check.Require(await status.MoveNextAsync(), "the publication has a real status subscriber");
            capture = true;
            monitor.Start(quietOnly: true);
            await clock.AwaitArmedAsync();
            clock.Advance(TimeSpan.FromSeconds(31));
            await published.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var barrier = monitor.ScheduleQuietStatus(TimeSpan.Zero, () => delivered.TrySetResult());
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await monitor.StopAsync();
            var waiting = Text.Get(TextKey.StatusStillWaiting, (TextArg.Reason, nameof(AttemptOwner.Prerequisite)));
            Check.Require(messages.Any(message => message.Contains(waiting, StringComparison.Ordinal)) &&
                Lines(log.Path).Any(fields => fields[RunLog.FieldDetail] == waiting),
                "an eligible retry prints its waiting sentence to output and its same-pair log even with active snapshots");
            releaseRun.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(30));
            await host.PauseAsync(null, default);
            releaseChild.TrySetResult();
            await child!.WaitAsync(TimeSpan.FromSeconds(30));
            Check.Require(await status.MoveNextAsync() && status.Current.Message == "later publication" &&
                status.Current.Status == AutoStatus.Paused,
                "background progress cannot inherit a completed quiet publication's status observation");
        }
        finally
        {
            releaseRun.TrySetResult();
            releaseChild.TrySetResult();
            await running;
            if (child is not null) await child;
        }
    }

    private static async Task WaitingObservationCostsAsync(string folder)
    {
        int[]? previous = null;
        // One and two storage pages expose a scan of unrelated items while the involved retry stays fixed.
        foreach (var pages in new[] { 1, 2 })
        {
            var root = Path.Combine(folder, pages.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Massdex.SyncShark.Engine.Storage.StateStore? actual = null;
            EngineHost? host = null;
            var paths = new List<string>();
            await using var rig = await RepairChecks.ComposeRigAsync(root,
                decorate: store => { actual = (Massdex.SyncShark.Engine.Storage.StateStore)store; return new HookStore(store); },
                openLog: id =>
                {
                    var log = RunLog.Open(root, id, Guid.NewGuid().ToString("N"), message => host!.ReportProgress(message), 262144);
                    paths.Add(log.Path);
                    return log;
                });
            host = rig.Host;
            var items = pages * host.Settings.StoragePageSize;
            var entries = Enumerable.Range(0, items).Select(index =>
            {
                var path = "Parent/File-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new SyncedEntry(PathNames.Key(path), path, PathNames.DropboxKey(path), path,
                    false, "local-" + index, 1, 0, "remote-" + index, "hash");
            }).ToArray();
            rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Record = entries });
            var generation = rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation;
            var local = rig.Store.ReadSource(rig.Pair.PairId, Side.Local)!.Value;
            var remote = rig.Store.ReadSource(rig.Pair.PairId, Side.Dropbox)!.Value;
            rig.Store.RecordPrerequisiteFailure(rig.Pair.PairId, generation, local.Epoch, remote.Epoch,
                local.Revision, remote.Revision, null, new FailureRetryOwner.State(1, DateTimeOffset.UtcNow.AddHours(1)), StateChange.Empty);
            using var subscription = new CancellationTokenSource();
            await using var status = host.SubscribeStatusAsync(subscription.Token).GetAsyncEnumerator();
            Check.Require(await status.MoveNextAsync(), "the cost probe has a status subscriber");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var publish = typeof(EngineHost).GetMethod("PublishQuietStatus", flags)!;
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(Massdex.SyncShark.Engine.Storage.StateStore)
                .GetField("_connection", flags)!.GetValue(actual)!;
            var counts = new int[2];
            for (var operation = 0; operation < counts.Length; operation++)
            {
                if (operation == 0) host.Status(); else publish.Invoke(host, null);
                var beforeLogs = paths.Count;
                var statements = 0;
                SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, _) => statements++), null);
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                try { if (operation == 0) host.Status(); else publish.Invoke(host, null); }
                finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                var newLogs = paths.Skip(beforeLogs).ToArray();
                var bytes = newLogs.Sum(path => new FileInfo(path).Length);
                foreach (var path in newLogs) { using var closed = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                counts[operation] = statements;
                Console.WriteLine($"prerequisite-cost items={items} involved-retries=1 operation={(operation == 0 ? "status" : "heartbeat")} statements={statements} allocated={allocated} elapsed-ms={elapsed.TotalMilliseconds} log-files={newLogs.Length} log-bytes={bytes}");
            }
            using (ExecutionContext.SuppressFlow())
                Check.Require((bool)publish.Invoke(host, null)!,
                    "a quiet publication made while flow is already suppressed still publishes without throwing");
            Check.Require(previous is null || counts.SequenceEqual(previous),
                "waiting status and heartbeat statement counts do not grow with unrelated pair items");
            previous = counts;
        }
    }

    private sealed class NoRetryStore(IStateStore inner) : ForwardingStateStore(inner)
    {
        internal int RetryReads;
        public override IEnumerable<(long PairRow, string PairId, long Generation, FailureRetryOwner.State State)>
            ReadPrerequisiteRetries(long? afterTicks, long? afterPairRow, int pageSize)
        {
            Interlocked.Increment(ref RetryReads);
            return base.ReadPrerequisiteRetries(afterTicks, afterPairRow, pageSize);
        }
    }

    private sealed class HeldSink(IScanSink inner, Action entered, Task release) : IScanSink
    {
        public Task AddAsync(Listed item, CancellationToken ct) => inner.AddAsync(item, ct);
        public async Task CompleteAsync()
        {
            entered();
            await release;
            await inner.CompleteAsync();
        }
        public Task AbortAsync() => inner.AbortAsync();
    }

    private sealed class QuietClock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _clock.GetUtcNow().UtcTicks;
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        internal int TimersCreated;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref TimersCreated);
            return _clock.CreateTimer(callback, state, dueTime, period);
        }
        internal async Task AwaitArmedAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!HasArmedTimer)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }
        public void Advance(TimeSpan by) => _clock.Advance(by);
        public bool HasArmedTimer => _clock.HasArmedTimer;
    }

    private static string[][] Lines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(RunLog.FieldSeparator)).ToArray();
    }
}
