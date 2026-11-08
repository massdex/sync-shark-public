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
            await IdleRetryHeartbeatAsync(Path.Combine(folder.FullName, "idle-retry"));
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
        using var log = RunLog.Open(folder, rig.Pair.PairId, "auto", message =>
        {
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
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var volumes = (System.Collections.IDictionary)typeof(EngineHost).GetField("_volumes", flags)!.GetValue(rig.Host)!;
        var volume = volumes.Values.Cast<object>().Single();
        var failed = typeof(EngineHost).GetMethod("JournalFailed", flags)!;
        clock.Reads = 0;
        failed.Invoke(rig.Host, [volume, new IOException("synthetic source loss")]);
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
        await using var rig = await RepairChecks.ComposeRigAsync(folder, time: clock, monitor: monitor,
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
        host.ReportProgress(Text.Get(TextKey.StatusStillQuiet));
        monitor.Start(quietOnly: true);
        for (var observation = 0; observation < 2; observation++)
        {
            await clock.AwaitArmedAsync();
            clock.Advance(TimeSpan.FromSeconds(31));
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (paths.Count <= observation) { bound.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
        }
        await monitor.StopAsync();
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
