using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Owns the single transient admission registry and joins borrowers before retiring generation resources.</summary>
internal sealed class EngineHost : IAsyncDisposable
{
    internal enum RegistrationKind { Dispatch, Acquisition, Observation }
    internal enum Mode { Persistent, Direct, Preview }
    internal enum PrerequisiteStopCause
    {
        None, UserCancellation, Stop, Pause, Disable, OwnConfiguration, OtherPairConfiguration
    }
    private enum RegistrationState { Waiting, Holding, Draining, Retired }
    private readonly object _gate = new();
    private const int SingleHolder = 1;
    private const int LatestStatusCapacity = 1;
    private readonly SemaphoreSlim _configuration = new(SingleHolder);
    private readonly SemaphoreSlim _pauseTransition = new(SingleHolder);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<object, Registration> _registrations = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<Guid, ItemAttempt> _items = new();
    private readonly Dictionary<string, PairGenerationResources> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IAdmission> _accounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Volume> _volumes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Prerequisite> _prerequisites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _feedStops = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _feeds = new(StringComparer.Ordinal);
    private readonly EngineExternals _externals;
    private readonly PilotStateAuthority _authority;
    private readonly EngineSettings _settings;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly Func<string, RunLog?> _openLog;
    private readonly Action<string> _progress;
    private readonly Mode _mode;
    private HttpClient? _ownedHttp;
    private AutoSync _auto = null!;
    private Monitor _monitor = null!;
    private Transition? _transition;
    private bool _started;
    private bool _stopping;
    private Task? _dispose;
    private UiPreferencesStore? _preferences;
    private ExceptionDispatchInfo? _preferenceFailure;
    private ExceptionDispatchInfo? _sourceFailure;
    private Task _sourceCancellation = Task.CompletedTask;

    internal void BackgroundFailed(Exception error)
    {
        lock (_gate)
        {
            if (_stopping || _sourceFailure is not null) return;
            _sourceFailure = ExceptionDispatchInfo.Capture(error);
            foreach (var id in _resources.Keys) _closed.Add(id);
            _sourceCancellation = _stop.CancelAsync();
            PublishStatus();
            foreach (var channel in _statusSubscribers) channel.Writer.TryComplete();
        }
    }

    internal CancellationToken Stopping => _stop.Token;

    private async Task ObserveSourceAsync(Task source, CancellationToken cancellation)
    {
        try { await source.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (LiveAdmissionStaleException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { BackgroundFailed(error); }
    }

    private async Task StartJournalAsync(Volume volume)
    {
        if (!volume.Source.Available) return;
        await volume.Source.StartAsync(_stop.Token).ConfigureAwait(false);
        var running = volume.Source.Completion;
        if (ReferenceEquals(volume.Observed, running)) return;
        volume.Observed = running;
        volume.Supervision = ObserveJournalAsync(volume, running, volume.Source.LifetimeCancellation);
    }

    private async Task ObserveJournalAsync(Volume volume, Task running, CancellationToken cancellation)
    {
        await Task.Yield();
        Exception? failure = null;
        try { await running.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        catch (LiveAdmissionStaleException) when (cancellation.IsCancellationRequested) { return; }
        catch (Exception error) { failure = error; }
        if (failure is null && cancellation.IsCancellationRequested) return;
        try { JournalFailed(volume, failure); }
        catch (Exception error) { BackgroundFailed(error); }
    }

    private void JournalFailed(Volume volume, Exception? error)
    {
        IReadOnlyList<JournalSource.Member> affected;
        lock (volume.Gate)
        {
            if (_stopping || volume.Members is not { Count: > 0 } members) return;
            affected = members;
            volume.Source.Invalidate();
            foreach (var member in members)
                Store.RequireRepairAfterLoss(member.Pair.PairId, member.Generation);
            RefreshMembers(volume, members, null);
        }
        foreach (var member in affected)
        {
            using var log = _openLog(member.Pair.PairId);
            log?.Failed(error ?? new IOException(Text.Get(TextKey.JournalSourceFailed,
                (TextArg.Reason, nameof(JournalSource)), (TextArg.Value, string.Empty))));
            lock (_gate)
                if (!_prerequisites.ContainsKey(member.Pair.PairId))
                    RecordPrerequisiteFailure(member.Pair.PairId, member.Generation);
            _monitor.RearmPrerequisiteWait();
        }
        PublishStatus();
    }

    private static async Task StopJournalAsync(Volume volume)
    {
        try { await volume.Source.StopAsync().ConfigureAwait(false); }
        catch (Exception) when (volume.Observed?.IsFaulted == true)
        {
            // The observed lifetime owns this failure and its durable Repair handoff, not the next borrower.
        }
        finally { await volume.Supervision.ConfigureAwait(false); }
    }
    private Action? _stopSupervision;
    private ISystemWakeSource? _wakeSource;
    private readonly HashSet<RunLog> _startupLogs = [];
    private RunLog? _hostStartupLog;
    // The session log the host keeps open for host-level state notes (AutoSync's idle transitions).
    private RunLog? _hostLog;
    private readonly HashSet<object> _listingWaits = new(ReferenceEqualityComparer.Instance);
    private int _feedWaits;

    private EngineHost(PilotStateAuthority authority, EngineSettings settings, HttpClient http,
        TimeProvider time, Func<string, RunLog?> openLog, Action<string> progress, Mode mode,
        EngineExternals externals)
    {
        _externals = externals;
        _authority = authority;
        _settings = settings;
        _http = http;
        _time = time;
        _openLog = openLog;
        _progress = new SuppressingProgress(progress, error =>
        {
            RunLog[] logs;
            lock (_gate) logs = _registrations.Values.Select(value => value.Log).OfType<RunLog>()
                .Concat(_startupLogs).Distinct().ToArray();
            foreach (var log in logs) log.Failed(error);
        }).Publish;
        _mode = mode;
    }

    internal ScanOwnership Scans { get; } = new();
    internal IStateStore Store { get; private set; } = null!;
    internal EngineOperations Operations { get; private set; } = null!;
    internal PilotStateAuthority Authority => _authority;
    internal EngineSettings Settings => _settings;
    internal TimeProvider Time => _time;
    internal HttpClient Http => _http;
    internal ILocalRecovery? Recovery => _externals.Recovery;
    internal bool Persistent => _mode == Mode.Persistent;

    /// <summary>The host's own session log, written only by host-level waits (AutoSync's idle transitions).</summary>
    internal RunLog? HostLog => _hostLog;

    /// <summary>The one production composition: the caller supplies its product root, progress sink and the
    /// already-owned authority, and Run wires settings, transport, store, logs and the clock once.</summary>
    internal static EngineHost Compose(string productRoot, Action<string> progress, PilotStateAuthority authority,
        Mode mode, RunLog? startupLog = null, Monitor? startupMonitor = null)
    {
        RunLog? ownedLog = null;
        HttpClient? http = null;
        EngineHost? host = null;
        Action<string> logSink = message => host!.ReportProgress(message);
        try
        {
            if (mode != Mode.Preview && startupMonitor is null)
            {
                startupMonitor = new Monitor(TimeProvider.System);
                startupMonitor.Start(quietOnly: true);
            }
            var delivery = new SuppressingProgress(progress, error => startupLog?.Failed(error));
            startupMonitor?.Observe(() =>
            {
                var message = Text.Get(TextKey.StatusStillQuiet);
                if (startupLog is null) delivery.Publish(message);
                else startupLog.ReportProgress(message);
                return true;
            });
            var settings = LoadSettings(productRoot);
            ownedLog = startupLog is null && mode == Mode.Persistent
                ? RunLog.Open(Path.Combine(productRoot, ResourceNames.EngineFolder, ResourceNames.LogsFolder),
                    nameof(EngineHost), ResourceNames.NewId(), progress, settings.LogBufferCharacters) : null;
            startupLog ??= ownedLog;
            http = EngineComposition.CreateDropboxHttpClient(DropboxApiLimits.ReadWriteAllowance,
                DecompressionMethods.GZip | DecompressionMethods.Deflate, TimeProvider.System);
            startupLog?.Event(RunEvent.Stage(TextKey.StageStore));
            host = Compose(productRoot, progress, authority, settings, http, TimeProvider.System,
                pairId => RunLog.Open(
                    Path.Combine(productRoot, ResourceNames.EngineFolder, ResourceNames.LogsFolder), pairId,
                    ResourceNames.NewId(), logSink, settings.LogBufferCharacters,
                    mode: mode switch
                    {
                        Mode.Persistent => TextKey.HumanModeHosted,
                        Mode.Preview => TextKey.HumanModePreview,
                        _ => TextKey.HumanModeDirect,
                    }),
                validator => mode == Mode.Preview
                    ? StateStore.OpenReadOnly(StateStore.DatabasePath(productRoot)) ??
                        throw new EngineRequestException(TextKey.NoPairsYet)
                    : StateStore.Open(StateStore.DatabasePath(productRoot), validator, startupLog),
                mode, startupLog, startupMonitor);
            startupLog?.Event(RunEvent.Stage(TextKey.StageStore, ended: true, count: RunLog.CompletedOperation));
            host._ownedHttp = http;
            return host;
        }
        catch (Exception error)
        {
            startupLog?.Failed(error);
            if (host is not null)
                try { Task.Run(async () => await host.DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult(); }
                catch (Exception cleanup) { startupLog?.Failed(cleanup); }
            if (startupMonitor is not null)
                try { startupMonitor.StopAsync().GetAwaiter().GetResult(); }
                catch (Exception cleanup) { startupLog?.Failed(cleanup); }
            ownedLog?.Dispose();
            http?.Dispose();
            throw;
        }
    }

    /// <summary>The settings record the engine composes with: the embedded defaults merged with the user's file.</summary>
    internal static EngineSettings LoadSettings(string productRoot) =>
        SettingsParser.Parse(DefaultsJson(), SettingsFile.Read(productRoot));

    /// <summary>The one reader of the embedded settings defaults.</summary>
    internal static string DefaultsJson()
    {
        using var document = CliCatalogResource.Load(typeof(EngineSettings).Assembly, ResourceNames.Settings);
        return document.RootElement.GetRawText();
    }

    internal static EngineHost Compose(string productRoot, Action<string> progress,
        PilotStateAuthority authority, EngineSettings settings, HttpClient http, TimeProvider time,
        Func<string, RunLog?> openLog,
        Func<Func<Guid, string, long, IssueSubject, ItemRetryToken?, bool>, IStateStore> openStore, Mode mode,
        RunLog? startupLog = null, Monitor? startupMonitor = null) =>
        Compose(productRoot, progress, authority, settings, http, time, openLog, openStore, mode,
            new EngineExternals(new WindowsDropboxRefreshTokenStore(), allowance =>
                EngineComposition.CreateDropboxHttpClient(allowance,
                    DecompressionMethods.GZip | DecompressionMethods.Deflate, time)), startupLog, startupMonitor);

    internal static EngineHost Compose(string productRoot, Action<string> progress,
        PilotStateAuthority authority, EngineSettings settings, HttpClient http, TimeProvider time,
        Func<string, RunLog?> openLog,
        Func<Func<Guid, string, long, IssueSubject, ItemRetryToken?, bool>, IStateStore> openStore, Mode mode,
        EngineExternals externals, RunLog? startupLog = null, Monitor? startupMonitor = null)
    {
        ArgumentNullException.ThrowIfNull(externals);
        ArgumentNullException.ThrowIfNull(externals.Credentials);
        ArgumentNullException.ThrowIfNull(externals.CreateDropboxClient);
        ArgumentNullException.ThrowIfNull(openStore);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(openLog);
        ArgumentNullException.ThrowIfNull(progress);
        var host = new EngineHost(authority, settings, http, time, openLog, progress, mode, externals);
        host._monitor = startupMonitor ?? new Monitor(time);
        try
        {
            host._monitor.Attach(settings, openLog, host.IsPaused, host);
            if (mode != Mode.Preview && startupMonitor is null) host._monitor.Start(quietOnly: true);
            host.TrackStartupLog(startupLog, active: true);
            host.Store = openStore(host.IsRegisteredItemAttempt) ?? throw new InvalidOperationException();
            host._auto = new AutoSync(host.Store, openLog, host);
            host.Operations = new EngineOperations(productRoot, host.ReportProgress, host);
            if (mode == Mode.Persistent) host._hostStartupLog = startupLog;
            return host;
        }
        catch (Exception error)
        {
            startupLog?.Failed(error);
            // Composition is synchronous; join its quiet work before releasing its log and preserve the cause.
            try { host._monitor.StopAsync().GetAwaiter().GetResult(); }
            catch (Exception) { }
            startupLog?.Dispose();
            host.Store?.Dispose();
            host._stop.Dispose();
            throw;
        }
        finally { host.TrackStartupLog(startupLog, active: false); }
    }

    /// <summary>Runs one live opportunity; the host sets its kind and mutation.</summary>
    internal Task<T> DispatchAsync<T>(string pairId,
        Func<object, PairConfig, long, CancellationToken, Task<T>> execute, CancellationToken ct) =>
        ExecuteInGenerationAsync(pairId, RegistrationKind.Dispatch, mutationAuthorized: true, execute, ct);

    /// <summary>Runs one acquisition lifetime; the host sets its kind and mutation.</summary>
    internal Task<T> AcquireAsync<T>(string pairId,
        Func<object, PairConfig, long, CancellationToken, Task<T>> execute, CancellationToken ct) =>
        ExecuteInGenerationAsync(pairId, RegistrationKind.Acquisition, mutationAuthorized: true, execute, ct);

    /// <summary>Runs one read-only observation; the host sets its kind and no Local mutation.</summary>
    internal Task<T> ObserveAsync<T>(string pairId,
        Func<object, PairConfig, long, CancellationToken, Task<T>> execute, CancellationToken ct) =>
        ExecuteInGenerationAsync(pairId, RegistrationKind.Observation, mutationAuthorized: false, execute, ct);

    /// <summary>The one pair-operation start: open its log, recompose, admit the pair and open its session.</summary>
    internal async Task<T> DispatchSessionAsync<T>(string pairId, Func<RunLog?> openLog,
        Func<PairSession, CancellationToken, Task<T>> execute, CancellationToken ct, RunLog? startupLog = null,
        bool prerequisite = false)
    {
        ArgumentNullException.ThrowIfNull(openLog);
        ArgumentNullException.ThrowIfNull(execute);
        using var ownedLog = startupLog is null ? openLog() : null;
        var log = startupLog ?? ownedLog;
        try
        {
            await RecomposeCurrentAsync(pairId, ct, log).ConfigureAwait(false);
            return await ExecuteInGenerationAsync(pairId, RegistrationKind.Dispatch, mutationAuthorized: true,
                async (registration, pair, generation, token) =>
            {
                using var observations = DropboxApi.ObserveRequests(log is null ? null : log.Event);
                lock (_gate) ((Registration)registration).Log = log;
                try
                {
                    return await execute(OpenForRegistration(registration, pair, generation, log), token).ConfigureAwait(false);
                }
                finally { lock (_gate) ((Registration)registration).Log = null; }
            }, ct, prerequisite).ConfigureAwait(false);
        }
        catch (Exception error) { log?.Failed(error); throw; }
    }

    private async Task<T> ExecuteInGenerationAsync<T>(string pairId, RegistrationKind kind,
        bool mutationAuthorized, Func<object, PairConfig, long, CancellationToken, Task<T>> execute,
        CancellationToken ct, bool prerequisite = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(pairId);
        ArgumentNullException.ThrowIfNull(execute);
        ct.ThrowIfCancellationRequested();
        Registration registration;
        PairConfig capturedPair;
        lock (_gate)
        {
            _sourceFailure?.Throw();
            if (_stopping || _closed.Contains(pairId)) throw Stale();
            if (kind == RegistrationKind.Dispatch && IsPaused(pairId))
            {
                if (prerequisite && _prerequisites.TryGetValue(pairId, out var pending) && IsCallerStopped(pending.Cause))
                    throw new PrerequisiteStoppedException(new OperationCanceledException(ct));
                throw Stale();
            }
            if (mutationAuthorized && _mode == Mode.Preview) throw new InvalidOperationException();
            var current = Store.ReadPairExecution(pairId) ?? throw Unknown(pairId);
            capturedPair = current.Pair;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            registration = new Registration(pairId, current.Generation, kind, mutationAuthorized, cancellation);
            if (_resources.TryGetValue(pairId, out var resources)) registration.Resources = resources;
            _registrations.Add(registration, registration);
            if (kind == RegistrationKind.Dispatch) ArmQuietStatus();
            PublishStatus();
        }
        try
        {
            return await execute(registration, capturedPair, registration.Generation,
                registration.Cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                registration.State = RegistrationState.Retired;
                _registrations.Remove(registration);
                foreach (var id in registration.Items) _items.TryRemove(id, out _);
                registration.Finished.TrySetResult();
                PublishStatus();
            }
            registration.Cancellation.Dispose();
        }
    }

    internal bool TryHoldGeneration(object registration)
    {
        lock (_gate)
        {
            if (!_registrations.TryGetValue(registration, out var current) || !CanAdmit(current)) return false;
            current.State = RegistrationState.Holding;
            return true;
        }
    }

    private bool CanAdmit(Registration registration) => !_stopping &&
        registration.State is RegistrationState.Waiting or RegistrationState.Holding &&
        !_closed.Contains(registration.PairId) && !registration.Cancellation.IsCancellationRequested &&
        (registration.Kind != RegistrationKind.Dispatch || !IsPaused(registration.PairId));

    internal void RequireGeneration(object registration, string pairId, long generation)
    {
        lock (_gate)
        {
            if (!_registrations.TryGetValue(registration, out var current) || current.PairId != pairId ||
                current.Generation != generation) throw Stale();
            if (!CanAdmit(current))
            {
                // A drain closes admission before cancellation is delivered; this is the same caller stop.
                if (current.State == RegistrationState.Draining && !_closed.Contains(pairId) &&
                    _prerequisites.TryGetValue(pairId, out var pending) && pending.Generation == generation &&
                    IsCallerStopped(pending.Cause))
                    throw new OperationCanceledException(current.Cancellation.Token);
                throw Stale();
            }
        }
    }

    internal bool IsMutationAuthorized(object registration)
    {
        lock (_gate) return Registered(registration).Mutation;
    }

    private Registration Registered(object registration) =>
        _registrations.TryGetValue(registration, out var value) ? value : throw Stale();

    internal PairGenerationResources BorrowGeneration(object registration, string pairId, long generation)
    {
        lock (_gate)
        {
            RequireGeneration(registration, pairId, generation);
            var current = Registered(registration);
            if (!_resources.TryGetValue(pairId, out var resources) || resources.Generation != generation ||
                current.Resources is not null && !ReferenceEquals(current.Resources, resources)) throw Stale();
            current.Resources = resources;
            return resources;
        }
    }

    internal PairGenerationResources BorrowConfigurationResources(object transition, string pairId, long generation)
    {
        lock (_gate)
        {
            RequireConfigurationTransition(transition, pairId, generation);
            if (_transition!.Candidates.TryGetValue(pairId, out var value)) return value;
            var expected = _transition.Expected[pairId];
            value = ComposeResources(expected.Pair, Store.Pairs(), expected.Generation);
            _transition.Candidates.Add(pairId, value);
            return value;
        }
    }

    internal Task DrainGenerationAsync(string pairId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task drain;
        lock (_gate)
        {
            _closed.Add(pairId);
            _monitor.WithdrawFolderPolls(pairId);
            drain = DrainAsync(SelectDrain(registration => registration.PairId == pairId));
        }
        return drain.WaitAsync(ct);
    }

    private Registration[] SelectDrain(Func<Registration, bool> select)
    {
        var selected = _registrations.Values.Where(select).ToArray();
        foreach (var registration in selected) registration.State = RegistrationState.Draining;
        return selected;
    }

    private static async Task DrainAsync(Registration[] registrations)
    {
        // Cancellation callbacks must never run beneath the lifecycle gate.
        await Task.Yield();
        List<Exception>? failures = null;
        foreach (var registration in registrations)
        {
            try { await registration.Cancellation.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) when (registration.Finished.Task.IsCompleted) { }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        try { await Task.WhenAll(registrations.Select(registration => registration.Finished.Task)).ConfigureAwait(false); }
        catch (Exception error) { (failures ??= []).Add(error); }
        if (failures is not null) throw new AggregateException(failures);
    }

    /// <summary>A finished user operation may have released retained live work.</summary>
    internal void WakeAfterOperation(string pairId) => Wake(pairId);

    /// <summary>The one wake: every event reaches both live work and an owed Repair; a null pair wakes every pair.</summary>
    private void Wake(string? pairId)
    {
        if (pairId is null)
        {
            foreach (var pair in Store.Pairs())
            {
                _auto.Kick(pair.PairId);
                _monitor.Wake(pair.PairId);
            }
            return;
        }
        _auto.Kick(pairId);
        _monitor.Wake(pairId);
    }

    internal Task DrainDispatchAsync(string? pairId, Action publishPause, CancellationToken ct) =>
        PauseTransitionAsync(pairId, publishPause, resume: false, ct);
    internal Task ResumeDispatchAsync(string? pairId, Action publishResume, CancellationToken ct) =>
        PauseTransitionAsync(pairId, publishResume, resume: true, ct);

    private async Task PauseTransitionAsync(string? pairId, Action publish, bool resume, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(publish);
        await _pauseTransition.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Registration[] drain;
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                if (_stopping) throw Stale();
                if (pairId is not null && Store.ReadPairExecution(pairId) is null) throw Unknown(pairId);
                publish();
                if (!resume) _monitor.WithdrawFolderPolls(pairId);
                drain = resume ? [] : SelectDrain(registration => registration.Kind == RegistrationKind.Dispatch &&
                    (pairId is null || registration.PairId == pairId));
                if (!resume)
                    foreach (var pending in _prerequisites.Values)
                        if (pairId is null || pending.Pair.PairId == pairId) pending.Cause = PrerequisiteStopCause.Pause;
            }
            await DrainAsync(drain).ConfigureAwait(false);
            PublishStatus();
            if (resume)
            {
                string[] pending;
                lock (_gate) pending = _closed.Where(id => pairId is null || id == pairId).ToArray();
                foreach (var id in pending) await RecomposeCurrentAsync(id, _stop.Token).ConfigureAwait(false);
                Wake(pairId);
            }
        }
        finally { _pauseTransition.Release(); }
    }

    internal void ReopenGeneration(string pairId, long generation)
    {
        lock (_gate)
        {
            if (_stopping || _registrations.Values.Any(registration => registration.PairId == pairId) ||
                Store.ReadPairExecution(pairId)?.Generation != generation ||
                !_resources.TryGetValue(pairId, out var resources) || resources.Generation != generation) throw Stale();
            _closed.Remove(pairId);
            PublishStatus();
        }
    }

    /// <summary>The one configuration transition: the host derives the affected pairs from its own snapshot and
    /// applies the generation fence around the caller's write. The supplied definition describes a pair that does
    /// not exist yet; a saved pair's stored definition is authoritative.</summary>
    internal async Task<T> ChangeGenerationAsync<T>(PairConfig changing,
        Func<object, IStateStore, IReadOnlyDictionary<string, (PairConfig Pair, long Generation)>,
            CancellationToken, Task<T>> prepareAndCommit, CancellationToken ct)
    {
        if (_mode == Mode.Preview) throw new InvalidOperationException();
        ArgumentNullException.ThrowIfNull(changing);
        ArgumentNullException.ThrowIfNull(prepareAndCommit);
        var changingPairId = changing.PairId;
        await _configuration.WaitAsync(ct).ConfigureAwait(false);
        Transition transition;
        Registration[] drain;
        try
        {
            lock (_gate)
            {
                _sourceFailure?.Throw();
                if (_stopping) throw Stale();
                var pairs = Store.Pairs();
                var definition = pairs.FirstOrDefault(value => value.PairId == changingPairId) ?? changing;
                transition = new Transition(changingPairId, Affected(pairs, definition),
                    CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token));
                _transition = transition;
                foreach (var id in transition.Ids)
                {
                    _closed.Add(id);
                    _monitor.WithdrawFolderPolls(id);
                }
                foreach (var pending in _prerequisites.Values)
                    if (transition.Ids.Contains(pending.Pair.PairId, StringComparer.Ordinal))
                        pending.Cause = pending.Pair.PairId == changingPairId
                            ? PrerequisiteStopCause.OwnConfiguration : PrerequisiteStopCause.OtherPairConfiguration;
                drain = SelectDrain(registration => transition.Ids.Contains(registration.PairId, StringComparer.Ordinal));
            }
        }
        catch { _configuration.Release(); throw; }
        try
        {
            var draining = DrainAsync(drain);
            await FreezeVolumesAsync(transition.Ids).ConfigureAwait(false);
            await draining.ConfigureAwait(false);
            foreach (var id in transition.Ids)
                if (Store.ReadPairExecution(id) is { } current) transition.Expected.Add(id, current);
            transition.Cancellation.Token.ThrowIfCancellationRequested();
            // A pair that exists must still be at the generation this transition drained; a pair being created has none.
            if (transition.Expected.TryGetValue(changingPairId, out var changed))
                RequireConfigurationTransition(transition, changingPairId, changed.Generation);
            return await prepareAndCommit(transition, Store, transition.Expected,
                transition.Cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                foreach (var candidate in transition.Candidates.Values) await candidate.DisposeAsync().ConfigureAwait(false);
                if (!_stopping && _mode == Mode.Persistent)
                {
                    try { await InstallCurrentAsync(transition.Ids).ConfigureAwait(false); }
                    catch (Exception error)
                    {
                        foreach (var id in transition.Ids)
                            await RecordCompositionFailureAsync(id, error).ConfigureAwait(false);
                        throw;
                    }
                }
            }
            finally
            {
                lock (_gate) if (ReferenceEquals(_transition, transition)) _transition = null;
                transition.Cancellation.Dispose();
                _configuration.Release();
                string[] open;
                lock (_gate) open = transition.Ids.Where(id => !_closed.Contains(id)).ToArray();
                foreach (var id in open) Wake(id);
            }
        }
    }

    /// <summary>The pairs whose definitions overlap a changed one: same pair, either local root under the other,
    /// the same local volume, or an overlapping Dropbox root on one connection.</summary>
    private static string[] Affected(IReadOnlyList<PairConfig> pairs, PairConfig pair) =>
        pairs.Where(value => value.PairId == pair.PairId ||
            PathNames.RelativeUnderLocalRoot(value.LocalRoot, pair.LocalRoot) is not null ||
            PathNames.RelativeUnderLocalRoot(pair.LocalRoot, value.LocalRoot) is not null ||
            value.Roots is not null && pair.Roots is not null &&
                value.Roots.Local.VolumeSerialNumber == pair.Roots.Local.VolumeSerialNumber ||
            value.ConnectionId == pair.ConnectionId &&
                (PathNames.RelativeUnderDropboxRoot(value.DropboxRoot, pair.DropboxRoot) is not null ||
                 PathNames.RelativeUnderDropboxRoot(pair.DropboxRoot, value.DropboxRoot) is not null))
            .Select(value => value.PairId).Append(pair.PairId).Distinct(StringComparer.Ordinal).ToArray();

    internal void RequireConfigurationTransition(object transition, string pairId, long expectedGeneration)
    {
        lock (_gate)
        {
            if (_stopping || !ReferenceEquals(_transition, transition) ||
                _transition.Cancellation.IsCancellationRequested ||
                !_transition.Expected.TryGetValue(pairId, out var expected) || expected.Generation != expectedGeneration)
                throw Stale();
        }
    }

    internal async Task RecomposeCurrentAsync(string pairId, CancellationToken ct, RunLog? log = null)
    {
        log?.Event(RunEvent.Stage(TextKey.StageConfiguration));
        TrackStartupLog(log, active: true);
        try
        {
            lock (_gate)
            {
                if (_stopping) throw Stale();
                if (_transition is { } active)
                {
                    if (_prerequisites.TryGetValue(pairId, out var pending))
                        pending.Cause = active.ChangingPairId == pairId
                            ? PrerequisiteStopCause.OwnConfiguration : PrerequisiteStopCause.OtherPairConfiguration;
                    throw Stale();
                }
                if (!_closed.Contains(pairId) && _resources.ContainsKey(pairId)) return;
            }
            await _configuration.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                await InstallCurrentAsync([pairId]).ConfigureAwait(false);
            }
            finally { _configuration.Release(); }
        }
        catch (Exception error) { log?.Failed(error); throw; }
        finally
        {
            TrackStartupLog(log, active: false);
            log?.Event(RunEvent.Stage(TextKey.StageConfiguration, ended: true));
        }
    }

    /// <summary>The one registration-scoped session composition: the host supplies its store, settings, transport,
    /// authority and clock, so a caller passes only its registration, pair, generation and run log.</summary>
    internal PairSession OpenForRegistration(object registration, PairConfig pair, long generation, RunLog? log) =>
        PairSession.Open(Store, pair, _settings, _http, true, log, _authority, this, registration, generation, _time);

    internal async Task<RootBinding> ReadImportRootsAsync(PairConfig pair, CancellationToken ct)
    {
        await using var resources = ComposeResources(pair, Store.Pairs(), 0);
        return await PairSession.ReadImportRootsAsync(Store, pair, resources, _authority, ct).ConfigureAwait(false);
    }

    private PairGenerationResources ComposeResources(PairConfig pair, IReadOnlyList<PairConfig> scopes, long generation) =>
        PairSession.ComposeGeneration(Store, pair, scopes, generation, _settings, _http, _authority, _time,
            AdmissionForAccount, (identity, delay, reconnect) =>
                _monitor.ScheduleListenerReconnect(identity.SourceIdentity, delay, reconnect), () => OpenFeedWait(pair.PairId),
            pair.PairId, _externals);

    private IAdmission AdmissionForAccount(string account)
    {
        lock (_gate)
        {
            if (_accounts.TryGetValue(account, out var admission)) return admission;
            admission = new AdaptiveLimiter(Store.LimiterLevel(account) ?? _settings.InitialLimiterLevel,
                _settings.InitialLimiterLevel, _time, new WindowsNetworkSignal(),
                wait => _progress(Text.Get(wait.Reason)), _settings.Backoff);
            _accounts.Add(account, admission);
            return admission;
        }
    }

    private async Task InstallCurrentAsync(IReadOnlyList<string> ids)
    {
        var scopes = Store.Pairs();
        var installedPairs = new List<(string PairId, long Generation, bool Enabled)>();
        foreach (var id in ids)
        {
            var current = Store.ReadPairExecution(id);
            foreach (var volume in _volumes.Values)
            {
                lock (volume.Gate)
                {
                    var remaining = (volume.Members ?? volume.Frozen).Where(member => member.Pair.PairId != id).ToArray();
                    volume.Frozen = remaining;
                    if (volume.Members is not null) volume.Members = remaining;
                }
            }
            PairGenerationResources? replacement = current is { } value
                ? ComposeResources(value.Pair, scopes, value.Generation) : null;
            PairGenerationResources? old;
            lock (_gate)
            {
                if (_stopping) throw Stale();
                _resources.Remove(id, out old);
                if (replacement is not null) _resources.Add(id, replacement);
            }
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
            if (current is { } installed)
            {
                InstallVolumeMember(installed.Pair, installed.Generation, replacement!);
                installedPairs.Add((id, installed.Generation, installed.Pair.Enabled));
            }
            else lock (_gate) _closed.Remove(id);
        }
        foreach (var volume in _volumes.Values)
            lock (volume.Gate) volume.Members ??= volume.Frozen;
        foreach (var installed in installedPairs) ReopenGeneration(installed.PairId, installed.Generation);
        if (_started)
        {
            foreach (var installed in installedPairs)
                if (installed.Enabled) StartFeed(installed.PairId);
            foreach (var volume in _volumes.Values)
                if (volume.Members is { Count: > 0 }) await StartJournalAsync(volume).ConfigureAwait(false);
        }
    }

    private void InstallVolumeMember(PairConfig pair, long generation, PairGenerationResources resources)
    {
        if (!pair.Enabled || pair.Roots is null || resources.Paths is null) return;
        var key = pair.Roots.LocalVolumeKey();
        if (!_volumes.TryGetValue(key, out var volume))
        {
            volume = new Volume();
            var captured = volume;
            volume.Source = new JournalSource(key, pair.LocalRoot, Store, Store.ReadJournal(key), volume.Gate,
                () => captured.Members, (candidate, changed) => RefreshMembers(captured, candidate, changed));
            volume.Source.WorkCommitted = id => Wake(id);
            volume.Source.ReportItemFailureAsync = ReportJournalItemFailureAsync;
            _volumes.Add(key, volume);
        }
        var source = Store.ReadSource(pair.PairId, Side.Local) ?? throw Stale();
        lock (volume.Gate)
        {
            var members = volume.Members ?? volume.Frozen;
            volume.Members = members.Where(member => member.Pair.PairId != pair.PairId)
                .Append(new JournalSource.Member(pair, generation, source.Epoch, source.Revision,
                    resources.Paths, pair.Drive)).ToArray();
            volume.Frozen = volume.Members;
        }
    }

    private Task ReportJournalItemFailureAsync(JournalSource.Member member, ItemFailedException failed,
        CancellationToken ct) => AcquireAsync(member.Pair.PairId,
        async (registration, pair, generation, token) =>
        {
            if (generation != member.Generation || !TryHoldGeneration(registration) ||
                Store.ReadSource(pair.PairId, Side.Local) is not { } source ||
                source.Epoch != member.Epoch || source.Revision != member.Revision || failed.LocalId is null)
                throw Stale();
            var (subject, expected) = Executor.ActionExecutor.CaptureAttempt(Store, pair.PairId, generation,
                failed.Path, new IssueSubject([new IssueItem(Side.Local, failed.Path, failed.LocalId)], null));
            using var log = _openLog(pair.PairId);
            return await ExecuteItemAttemptAsync(registration, registration, expected,
                subject, (attemptId, attemptToken) =>
                {
                    Executor.Syncer.FailItem(Store, pair.PairId, ItemAttemptsFor(registration),
                        log, attemptId, expected, member.Epoch, failed.Path, subject, failed.Cause, attemptToken);
                    return Task.FromResult(true);
                }, token).ConfigureAwait(false);
        }, ct);

    private void RefreshMembers(Volume volume, IReadOnlyList<JournalSource.Member> candidate,
        IReadOnlyList<string>? changed)
    {
        lock (volume.Gate)
        {
            if (volume.Members is null) throw Stale();
            volume.Members = candidate.Select(member =>
            {
                if (changed is not null && !changed.Contains(member.Pair.PairId, StringComparer.Ordinal)) return member;
                var current = Store.ReadPairExecution(member.Pair.PairId) ?? throw Stale();
                var source = Store.ReadSource(member.Pair.PairId, Side.Local) ?? throw Stale();
                if (current.Generation != member.Generation) throw Stale();
                return member with { Epoch = source.Epoch, Revision = source.Revision };
            }).ToArray();
            volume.Frozen = volume.Members;
        }
    }

    private async Task FreezeVolumesAsync(IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
        {
            if (_feedStops.Remove(id, out var cancellation))
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                if (_feeds.Remove(id, out var feed))
                {
                    try { await feed.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                }
                cancellation.Dispose();
            }
        }
        foreach (var volume in _volumes.Values)
        {
            lock (volume.Gate)
            {
                if (!(volume.Members ?? volume.Frozen).Any(member => ids.Contains(member.Pair.PairId, StringComparer.Ordinal)))
                    continue;
                volume.Frozen = volume.Members ?? volume.Frozen;
                volume.Members = null;
            }
            await volume.Serial.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopJournalAsync(volume).ConfigureAwait(false);
            }
            finally { volume.Serial.Release(); }
        }
    }

    private void StartFeed(string pairId)
    {
        if (_feeds.ContainsKey(pairId) || _stopping) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _feedStops.Add(pairId, cancellation);
        _feeds.Add(pairId, ObserveSourceAsync(RunFeedAsync(), cancellation.Token));
        async Task RunFeedAsync()
        {
            await Task.Yield();
            await AcquireAsync(pairId,
                async (registration, pair, generation, token) =>
                {
                    if (!TryHoldGeneration(registration)) throw Stale();
                    var resources = BorrowGeneration(registration, pairId, generation);
                    resources.Feed.WorkCommitted = () => Wake(pairId);
                    await resources.Feed.RunAsync(token).ConfigureAwait(false);
                    return pair;
                }, cancellation.Token).ConfigureAwait(false);
        }
    }

    internal async Task EnsureSourceBasesAsync(string pairId, CancellationToken ct, RunLog? log = null)
    {
        await AcquireAsync(pairId, async (registration, pair, generation, token) =>
        {
            if (!pair.Enabled) throw new EngineRequestException(TextKey.PairDisabled, (TextArg.Pair, pairId));
            if (!TryHoldGeneration(registration)) throw Stale();
            var resources = BorrowGeneration(registration, pairId, generation);
            lock (_gate)
            {
                ((Registration)registration).Log = log;
                if (log is not null) ArmQuietStatus();
            }
            using var observations = DropboxApi.ObserveRequests(log is null ? null : log.Event);
            try
            {
                log?.Event(RunEvent.Stage(TextKey.StageSourceBase));
                await resources.Feed.EnsureSourceBaseAsync(token).ConfigureAwait(false);
                log?.Event(RunEvent.Stage(TextKey.StageSourceBase, ended: true, count: RunLog.CompletedOperation));
                if (pair.Roots is null) throw new EngineRequestException(TextKey.RootUnbound);
                await CatchUpLocalAsync(pairId, generation, token, log).ConfigureAwait(false);
                return pair;
            }
            finally { lock (_gate) ((Registration)registration).Log = null; }
        }, ct).ConfigureAwait(false);
    }

    internal async Task<LocalCatchUp> CatchUpLocalAsync(string pairId, long generation, CancellationToken ct,
        RunLog? log = null)
    {
        var pair = Store.ReadPairExecution(pairId) ?? throw Stale();
        if (pair.Generation != generation || pair.Pair.Roots is null) throw Stale();
        var key = pair.Pair.Roots.LocalVolumeKey();
        if (!_volumes.TryGetValue(key, out var volume)) throw Stale();
        return await WithVolumeStoppedAsync(volume,
            token => volume.Source.CatchUpForPairAsync(pairId, generation, token, log), ct, log).ConfigureAwait(false);
    }

    /// <summary>The one stop/run/restart sequence for a volume: holds its serial, stops and joins acquisition,
    /// runs the caller's operation and restarts, always keeping the operation's own failure as the failure.</summary>
    private async Task<T> WithVolumeStoppedAsync<T>(Volume volume, Func<CancellationToken, Task<T>> run,
        CancellationToken ct, RunLog? log = null)
    {
        log?.Event(RunEvent.Stage(TextKey.StageVolumeWait));
        await volume.Serial.WaitAsync(ct).ConfigureAwait(false);
        ExceptionDispatchInfo? failure = null;
        var result = default(T);
        try
        {
            log?.Event(RunEvent.Stage(TextKey.StageVolumeWait, ended: true, count: RunLog.CompletedOperation));
            log?.Event(RunEvent.Stage(TextKey.StageJournalStop));
            await StopJournalAsync(volume).ConfigureAwait(false);
            log?.Event(RunEvent.Stage(TextKey.StageJournalStop, ended: true, count: RunLog.CompletedOperation));
            _sourceFailure?.Throw();
            result = await run(ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
            if (!ct.IsCancellationRequested && error is not LiveAdmissionStaleException)
            {
                try { JournalFailed(volume, error); }
                catch (Exception handoffFailure) { BackgroundFailed(handoffFailure); }
            }
        }
        try
        {
            if (_started && !_stopping && volume.Members is not null && !_stop.IsCancellationRequested)
                await StartJournalAsync(volume).ConfigureAwait(false);
        }
        catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        finally { volume.Serial.Release(); }
        failure?.Throw();
        return result!;
    }

    internal void ValidateSourceBase(object registration, string pairId, long generation,
        SourceProvenance? provenance, string? cursor) =>
        BorrowGeneration(registration, pairId, generation).Feed.ValidateSourceBase(provenance, cursor);

    internal async Task<LocalCatchUp> CheckpointAfterRepairAsync(object registration, string pairId, long generation,
        CancellationToken ct)
    {
        RequireGeneration(registration, pairId, generation);
        var pair = Store.ReadPairExecution(pairId) ?? throw Stale();
        if (pair.Generation != generation || pair.Pair.Roots is null) throw Stale();
        var key = pair.Pair.Roots.LocalVolumeKey();
        if (!_volumes.TryGetValue(key, out var volume)) throw Stale();
        lock (volume.Gate) if (volume.Members is null) throw Stale();
        return await WithVolumeStoppedAsync(volume, token =>
        {
            lock (volume.Gate) if (volume.Members is null) throw Stale();
            return volume.Source.CheckpointForPairAsync(pairId, generation, token);
        }, ct).ConfigureAwait(false);
    }

    internal Task<DropboxCatchUp> CatchUpDropboxAsync(object registration, string pairId, long generation,
        CancellationToken ct) => BorrowGeneration(registration, pairId, generation).Feed.CatchUpAsync(ct);

    /// <summary>Whether an ordinary admitted operation may run this pair at all.</summary>
    internal enum PairAdmission { Admitted, Disabled, Paused, AwaitingDecision }

    /// <summary>
    /// The one answer for a pair's ordinary admission: enabled, not paused, and no unresolved drive change.
    /// Prerequisite eligibility (an outstanding obligation) is a separate test with the opposite sense.
    /// </summary>
    internal PairAdmission Admission(string pairId, PairConfig pair)
    {
        if (!pair.Enabled) return PairAdmission.Disabled;
        if (IsPaused(pairId)) return PairAdmission.Paused;
        return Store.IssueCounts(pairId).GetValueOrDefault(IssueKind.DriveChange) != 0
            ? PairAdmission.AwaitingDecision : PairAdmission.Admitted;
    }

    internal async Task<RepairSummary?> RunPrerequisiteAsync(string pairId, CancellationToken ct,
        RunLog? startupLog = null)
    {
        Prerequisite? attempt = null;
        long generation = 0;
        RepairSummary? summary = null;
        Exception? failure = null;
        RunLog? ownedLog = null;
        RunLog? log = startupLog;
        CancellationTokenRegistration stop = default;
        try
        {
            lock (_gate)
            {
                var current = Store.ReadPairExecution(pairId);
                if (_stopping || _prerequisites.ContainsKey(pairId) || current is null ||
                    Admission(pairId, current.Value.Pair) != PairAdmission.Admitted ||
                    !Store.Obligated(pairId) && Store.ReadPrerequisiteRetry(pairId) is null) return null;
                attempt = new Prerequisite(current.Value.Pair, current.Value.Generation);
                generation = current.Value.Generation;
                _prerequisites.Add(pairId, attempt);
            }
            // Registered after the attempt exists so an already-cancelled caller still classifies it.
            stop = ct.Register(() =>
            {
                lock (_gate) attempt!.Cause = PrerequisiteStopCause.UserCancellation;
            });
            ownedLog = startupLog is null ? _openLog(pairId) : null;
            log ??= ownedLog;
            await RecomposeCurrentAsync(pairId, ct, log).ConfigureAwait(false);
            await EnsureSourceBasesAsync(pairId, ct, log).ConfigureAwait(false);
            if (!Store.Obligated(pairId))
            {
                var local = Store.ReadSource(pairId, Side.Local) ?? throw Stale();
                var dropbox = Store.ReadSource(pairId, Side.Dropbox) ?? throw Stale();
                var retry = Store.ReadPrerequisiteRetry(pairId);
                if (retry is not null)
                    Store.CompleteRecomposition(pairId, generation, local.Epoch, dropbox.Epoch,
                        local.Revision, dropbox.Revision, retry.Value);
                Wake(pairId);
                return null;
            }
            summary = await DispatchSessionAsync(pairId, () => log,
                (session, token) => Repair.RunAsync(session, token), ct, log, prerequisite: true).ConfigureAwait(false);
            if (summary is { Completed: true }) Wake(pairId);
            return summary;
        }
        catch (Exception error)
        {
            failure = error;
            // Admission precedes the owned log, so a failure in that read still gets one.
            if (log is null) ownedLog = _openLog(pairId);
            log ??= ownedLog;
            log?.Failed(error);
            // A caller stop is CallerStopped and counts nothing (engine.md 5.6). Its classification travels with the
            // exception, so a resume that clears the pause flag cannot turn the stop back into a failure.
            PrerequisiteStopCause cause;
            lock (_gate) cause = attempt?.Cause ?? PrerequisiteStopCause.None;
            if (error is OperationCanceledException cancellation && error is not PrerequisiteStoppedException &&
                IsCallerStopped(cause))
                throw new PrerequisiteStoppedException(cancellation);
            throw;
        }
        finally
        {
            try
            {
                if (attempt is not null)
                {
                    try { await CompletePrerequisiteAsync(attempt, summary, failure).ConfigureAwait(false); }
                    catch (Exception error) { log?.Failed(error); throw; }
                }
            }
            finally
            {
                if (attempt is not null) lock (_gate) _prerequisites.Remove(pairId);
                ownedLog?.Dispose();
                stop.Dispose();
            }
        }
    }

    /// <summary>engine.md 5.6: a pause, disable or cancel is CallerStopped and counts nothing.</summary>
    private static bool IsCallerStopped(PrerequisiteStopCause cause) =>
        cause is PrerequisiteStopCause.UserCancellation or PrerequisiteStopCause.Stop or
            PrerequisiteStopCause.Pause or PrerequisiteStopCause.Disable;

    private async Task RecordCompositionFailureAsync(string pairId, Exception error)
    {
        Prerequisite attempt;
        lock (_gate)
        {
            var pair = Store.ReadPairExecution(pairId);
            if (_stopping || pair is null || !pair.Value.Pair.Enabled || IsPaused(pairId) ||
                _prerequisites.ContainsKey(pairId)) return;
            _closed.Add(pairId);
            attempt = new Prerequisite(pair.Value.Pair, pair.Value.Generation);
            _prerequisites.Add(pairId, attempt);
        }
        try { await CompletePrerequisiteAsync(attempt, null, error).ConfigureAwait(false); }
        finally { lock (_gate) _prerequisites.Remove(pairId); }
    }

    internal Task CompletePrerequisiteAsync(object attempt, RepairSummary? summary, Exception? error)
    {
        lock (_gate)
        {
            if (attempt is not Prerequisite pending || !_prerequisites.TryGetValue(pending.Pair.PairId, out var current) ||
                !ReferenceEquals(current, pending)) throw Stale();
            if (pending.Completion is not null) return pending.Completion;
            pending.Completion = CompleteAsync();
            return pending.Completion;
            async Task CompleteAsync()
            {
                await Task.Yield();
                if (error is null && !Store.Obligated(pending.Pair.PairId) &&
                    (summary is { Completed: true } || Store.ReadPrerequisiteRetry(pending.Pair.PairId) is null)) return;
                lock (_gate)
                {
                    if (_stopping || IsPaused(pending.Pair.PairId) || pending.Cause is not
                        (PrerequisiteStopCause.None or PrerequisiteStopCause.OtherPairConfiguration)) return;
                    var pair = Store.ReadPairExecution(pending.Pair.PairId);
                    if (pair is null || !pair.Value.Pair.Enabled || pair.Value.Generation != pending.Generation) return;
                    RecordPrerequisiteFailure(pending.Pair.PairId, pending.Generation);
                }
                _monitor.RearmPrerequisiteWait();
            }
        }
    }

    private void RecordPrerequisiteFailure(string pairId, long generation)
    {
        var local = Store.ReadSource(pairId, Side.Local) ?? throw Stale();
        var dropbox = Store.ReadSource(pairId, Side.Dropbox) ?? throw Stale();
        var expected = Store.ReadPrerequisiteRetry(pairId);
        var next = FailureRetryOwner.AdoptedFailed(expected?.Attempts ?? 0, _time.GetUtcNow(), null,
            _settings.Backoff, Random.Shared.NextDouble);
        Store.RecordPrerequisiteFailure(pairId, generation, local.Epoch, dropbox.Epoch,
            local.Revision, dropbox.Revision, expected, next, RetryNotice());
    }

    internal ListingReadContext BeginListing(object registration, AttemptOwner owner)
    {
        Registration operation;
        lock (_gate)
        {
            operation = Registered(registration);
            if (!CanAdmit(operation) || owner is not (AttemptOwner.Request or AttemptOwner.Prerequisite) ||
                owner == AttemptOwner.Prerequisite && (!operation.Mutation || operation.Kind != RegistrationKind.Dispatch))
                throw Stale();
        }
        var identity = new object();
        Issue? notice = null;
        var episode = new FailureRetryOwner.Episode(providerWaitCounts: false);
        var ended = false;
        return new ListingReadContext(() =>
        {
            if (ended) throw new ObjectDisposedException(nameof(ListingReadContext));
            RequireGeneration(registration, operation.PairId, operation.Generation);
            return new RequestAttempt(identity, owner);
        }, async (attempt, ct) =>
        {
            if (!ReferenceEquals(attempt.Operation, identity) || attempt.Owner != owner) throw Stale();
            if (attempt.ClaimResult() is not { } result) return false;
            if (owner == AttemptOwner.Request) return false;
            if (result.Result == RequestResult.Success)
            {
                episode.Succeeded();
                CloseNotice();
                return false;
            }
            if (result.Result != RequestResult.TransientFailure) return false;
            RequireGeneration(registration, operation.PairId, operation.Generation);
            notice ??= Store.RecordPrerequisitePageWait(operation.PairId, operation.Generation, identity, RetryNotice());
            lock (_gate) _listingWaits.Add(identity);
            var delay = episode.Failed(result.ProviderWait, attempt.ProviderWaitStartedTimestamp, _time,
                _settings.Backoff, Random.Shared.NextDouble);
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            RequireGeneration(registration, operation.PairId, operation.Generation);
            return true;
        }, () =>
        {
            if (!ended) { CloseNotice(); ended = true; }
            return ValueTask.CompletedTask;
        });
        void CloseNotice()
        {
            if (notice is null) return;
            Store.CompletePrerequisitePageWait(operation.PairId, operation.Generation, identity, notice);
            notice = null;
            lock (_gate) _listingWaits.Remove(identity);
        }
    }

    private static StateChange RetryNotice() => StateChange.Empty with
    {
        Raise = [(IssueKind.Failure, TextKey.RequiredRepairRetry, string.Empty, new IssueSubject([], null))]
    };

    internal IItemAttempts ItemAttemptsFor(object registration)
    {
        lock (_gate) { Registered(registration); return new ItemPort(this, registration); }
    }

    internal async Task<T> ExecuteItemAttemptAsync<T>(object registration, object opportunity,
        ItemRetryCandidate? expected, IssueSubject subject, Func<Guid, CancellationToken, Task<T>> execute,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(execute);
        ct.ThrowIfCancellationRequested();
        var item = ReserveItem(registration, opportunity, expected, subject);
        try
        {
            if (expected is not null && !Store.TryClaimItemRetry(expected, item.Id)) throw SelectionStale();
            return await execute(item.Id, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _items.TryRemove(item.Id, out _);
                item.Operation.Items.Remove(item.Id);
                if (item.Reported is null && item.Completed is null &&
                    item.Operation.Consumed.TryGetValue(item.Opportunity, out var consumed))
                {
                    consumed.Remove(item.Subject);
                    if (consumed.Count == 0) item.Operation.Consumed.Remove(item.Opportunity);
                }
            }
        }
    }

    private ItemAttempt ReserveItem(object registration, object opportunity, ItemRetryCandidate? expected, IssueSubject subject)
    {
        lock (_gate)
        {
            var operation = Registered(registration);
            if (!operation.Mutation || !CanAdmit(operation)) throw Stale();
            if (!ReferenceEquals(opportunity, registration))
            {
                if (opportunity is not WorkSelection selected) throw SelectionStale();
                selected.RequireFor(operation.PairId, operation.Generation);
            }
            if (operation.Consumed.TryGetValue(opportunity, out var consumed) &&
                consumed.Contains(subject)) throw SelectionStale();
            if (expected is not null && (expected.PairId != operation.PairId ||
                expected.Token.PairGeneration != operation.Generation || !expected.Token.Subject.HasSameItems(subject)))
                throw SelectionStale();
            if (_items.Values.Any(active => active.Operation.PairId == operation.PairId &&
                (active.Subject.Items.Intersect(subject.Items).Any() || expected is not null &&
                 active.Authority?.IssueId == expected.Token.IssueId))) throw SelectionStale();
            if (consumed is null) operation.Consumed.Add(opportunity, consumed = new(IssueSubject.ItemSetComparer));
            consumed.Add(subject);
            var item = new ItemAttempt(ResourceNames.NewAttemptId(), operation, opportunity, subject, expected?.Token);
            _items.TryAdd(item.Id, item);
            operation.Items.Add(item.Id);
            return item;
        }
    }

    internal bool IsRegisteredItemAttempt(Guid attemptId, string pairId, long generation,
        IssueSubject subject, ItemRetryToken? expectedEpisode) =>
        _items.TryGetValue(attemptId, out var item) && item.Operation.PairId == pairId &&
        item.Operation.Generation == generation && item.Operation.Mutation &&
        item.Operation.State != RegistrationState.Retired && item.Subject.HasSameItems(subject) &&
        TokenEquals(item.Authority, expectedEpisode);

    internal ItemRetryCandidate ReportItemFailure(object registration, Guid attemptId, long sourceEpoch,
        string path, IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
        PendingEffect? expectedPending, ItemRetryToken? expectedEpisode)
    {
        var item = RequireItem(registration, attemptId);
        var report = (sourceEpoch, path, subject, cause, selection, dispatch, expectedPending, expectedEpisode);
        lock (item.Gate)
        {
            if (item.Report is { } previous)
            {
                if (previous.Item1 != sourceEpoch || previous.Item2 != path ||
                    !SubjectEquals(previous.Item3, subject) || previous.Item4 != cause ||
                    previous.Item5 != selection || previous.Item6 != dispatch || previous.Item7 != expectedPending ||
                    !TokenEquals(previous.Item8, expectedEpisode)) throw SelectionStale();
                return item.Reported!;
            }
            if (item.Completed is not null || !TokenEquals(item.Authority, expectedEpisode)) throw SelectionStale();
            var result = Store.RecordInUseFailure(item.Operation.PairId, item.Operation.Generation, sourceEpoch,
                path, subject, cause, selection, dispatch, expectedPending, expectedEpisode, attemptId);
            item.Report = report;
            item.Reported = result;
            item.Authority = result.Token;
            return result;
        }
    }

    internal void CompleteItemAttempt(object registration, Guid attemptId, ItemRetryCandidate expected, StateChange change,
        PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null)
    {
        var item = RequireItem(registration, attemptId);
        lock (item.Gate)
        {
            if (item.Completed is not null)
            {
                if (!SameChange(item.Completed, change) || !TokenEquals(item.CompletedToken, expected.Token) ||
                    item.CompletedPending != completedPending)
                    throw SelectionStale();
                return;
            }
            if (item.Reported is not null || !TokenEquals(item.Authority, expected.Token)) throw SelectionStale();
            Store.CompleteItemAttempt(expected, attemptId, change, completedPending, selection, committedIssue);
            item.CompletedPending = completedPending;
            item.CompletedToken = expected.Token;
            item.Completed = change;
        }
    }

    private ItemAttempt RequireItem(object registration, Guid id) =>
        _items.TryGetValue(id, out var item) && ReferenceEquals(item.Operation, registration)
            ? item : throw SelectionStale();

    private static bool SameChange(StateChange left, StateChange right) =>
        ReferenceEquals(left, right) || left.Record.SequenceEqual(right.Record) &&
        left.StaleBindings.SequenceEqual(right.StaleBindings) &&
        left.ForgetBothSubtrees.SequenceEqual(right.ForgetBothSubtrees) &&
        left.ForgetSelf.SequenceEqual(right.ForgetSelf) && left.ForgetSubtrees.SequenceEqual(right.ForgetSubtrees) &&
        left.Renames.SequenceEqual(right.Renames) && left.FirstRunAcknowledged.SequenceEqual(right.FirstRunAcknowledged) &&
        left.Close.Count == right.Close.Count && left.Close.Zip(right.Close).All(pair =>
            pair.First.Path == pair.Second.Path && pair.First.Kinds.SequenceEqual(pair.Second.Kinds)) &&
        left.Raise.Count == right.Raise.Count && left.Raise.Zip(right.Raise).All(pair =>
            pair.First.Kind == pair.Second.Kind && pair.First.Cause == pair.Second.Cause &&
            pair.First.Path == pair.Second.Path && SubjectEquals(pair.First.Subject, pair.Second.Subject));

    private static bool SubjectEquals(IssueSubject left, IssueSubject right) =>
        left.Incoming == right.Incoming && left.LocalHash == right.LocalHash &&
        left.FirstRunDeletion == right.FirstRunDeletion && left.HasSameItems(right);

    private static bool TokenEquals(ItemRetryToken? left, ItemRetryToken? right) =>
        left is null ? right is null : right is not null && left.IssueId == right.IssueId &&
        left.EpisodeId == right.EpisodeId && left.Revision == right.Revision && left.WorkId == right.WorkId &&
        left.PairGeneration == right.PairGeneration && left.SourceEpoch == right.SourceEpoch &&
        left.Subject.HasSameItems(right.Subject);

    internal void ReportInventoryFailure(object registration, string path, Entry? observed, TextKey cause, bool wholeRoot,
        RunLog? log)
    {
        Registration operation;
        lock (_gate)
        {
            operation = Registered(registration);
            if (!operation.Mutation || !CanAdmit(operation)) throw Stale();
        }
        if (!IssuePolicy.IsLocalAccessRetryCause(cause)) throw new ArgumentException(null, nameof(cause));
        if (wholeRoot) return;
        if (observed is null || string.IsNullOrEmpty(path)) throw SelectionStale();
        var subject = new IssueSubject([new IssueItem(Side.Local, observed.Path, observed.Id)], Side.Local);
        lock (operation.InventoryGate)
        {
            if (operation.Inventory.TryGetValue(path, out var previous))
            {
                if (previous.Entry != observed || previous.Cause != cause) throw SelectionStale();
                return;
            }
            operation.InventoryEpoch ??= Store.ReadSource(operation.PairId, Side.Local)?.Epoch ?? throw Stale();
            var (attemptSubject, expected) = Executor.ActionExecutor.CaptureAttempt(
                Store, operation.PairId, operation.Generation, path, subject);
            if (expected is not null && (expected.Dispatch != ItemRetryDispatch.LocalBranch ||
                expected.PendingOperationId is not null || expected.PendingGeneration is not null ||
                expected.Token.SourceEpoch != operation.InventoryEpoch)) throw SelectionStale();
            var item = ReserveItem(registration, registration, expected, subject);
            if (expected is not null && !Store.TryClaimItemRetry(expected, item.Id)) throw SelectionStale();
            Executor.Syncer.FailItem(Store, operation.PairId, ItemAttemptsFor(registration),
                log, item.Id, expected, operation.InventoryEpoch.Value,
                path, attemptSubject, cause, CancellationToken.None);
            operation.Inventory.Add(path, (observed, cause));
        }
    }

    internal void ScheduleFolderPoll(string pairId, bool pending, Action wake)
    {
        lock (_gate)
        {
            if (!pending || _stopping || _closed.Contains(pairId) || IsPaused(pairId))
            {
                _monitor.WithdrawFolderPolls(pairId);
                return;
            }
            _monitor.ScheduleFolderPoll(pairId, Settings.FolderDeletePollInterval, wake);
        }
    }

    internal bool IsPaused(string pairId) => _auto.IsPaused(pairId);
    internal Task PauseAsync(string? pairId, CancellationToken ct) =>
        DrainDispatchAsync(pairId, () => _auto.PublishPause(pairId, true), ct);
    internal Task ResumeAsync(string? pairId, CancellationToken ct) =>
        ResumeDispatchAsync(pairId, () => _auto.PublishPause(pairId, false), ct);
    internal void BindStopSupervision(Action stopSupervision) => _stopSupervision = stopSupervision;

    internal void BindPlatformWake(ISystemWakeSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            if (_stopping || _wakeSource is not null) throw new InvalidOperationException();
            _wakeSource = source;
            source.Woke += PlatformWoke;
        }
        PlatformWoke();
    }

    private void PlatformWoke()
    {
        lock (_gate)
        {
            if (_stopping) return;
            foreach (var pair in Store.Pairs())
                if (pair.Enabled) Wake(pair.PairId);
        }
    }

    internal async Task StartAsync(CancellationToken ct)
    {
        var log = Interlocked.Exchange(ref _hostStartupLog, null);
        TrackStartupLog(log, active: true);
        // The host keeps this log open for host-level waits; DisposeCoreAsync closes it.
        _hostLog = log;
        log?.Event(RunEvent.Stage(TextKey.StageConfiguration));
        try
        {
            await StartCoreAsync(ct).ConfigureAwait(false);
            log?.Event(RunEvent.Stage(TextKey.StageConfiguration, ended: true, count: RunLog.CompletedOperation));
        }
        catch (Exception error) { log?.Failed(error); throw; }
        finally
        {
            TrackStartupLog(log, active: false);
            log?.Dispose();
        }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_mode != Mode.Persistent) throw new InvalidOperationException();
        lock (_gate) { if (_started) return; _started = true; }
        _preferences = new UiPreferencesStore(_authority);
        try { _preferences.Load(); }
        catch (Exception error) { _preferenceFailure = ExceptionDispatchInfo.Capture(error); }
        foreach (var pair in Store.Pairs())
        {
            using var log = _openLog(pair.PairId);
            await RecomposeCurrentAsync(pair.PairId, ct, log).ConfigureAwait(false);
        }
        foreach (var configured in Store.Pairs())
            await ObserveAsync(configured.PairId,
                async (registration, pair, generation, token) =>
                {
                    using var log = _openLog(pair.PairId);
                    var session = OpenForRegistration(registration, pair, generation, log);
                    await session.Syncer.ReconcileReceiptsAsync(token).ConfigureAwait(false);
                    return pair;
                }, ct).ConfigureAwait(false);
        _monitor.Start();
        _auto.Start();
        // AutoSync.Start owns its startup opportunity; only Repair still needs its wake.
        foreach (var pair in Store.Pairs()) if (pair.Enabled) _monitor.Wake(pair.PairId);
    }

    internal Task<UiPreferences> ReadPreferencesAsync(CancellationToken ct)
    {
        _preferenceFailure?.Throw();
        return (_preferences ?? throw new InvalidOperationException()).LoadAsync(ct);
    }
    internal Task<UiPreferences> UpdatePreferencesAsync(UiPreferencesUpdate update, CancellationToken ct)
    {
        _preferenceFailure?.Throw();
        return (_preferences ?? throw new InvalidOperationException()).UpdateAsync(update, ct);
    }

    internal void ReportProgress(string message)
    {
        lock (_gate)
        {
            if (_stopping) return;
            if (_mode != Mode.Preview) ArmQuietStatus();
        }
        _progress(message);
        PublishStatus(message);
    }

    private void TrackStartupLog(RunLog? log, bool active)
    {
        if (log is null || _mode == Mode.Preview) return;
        lock (_gate)
        {
            if (active)
            {
                _startupLogs.Add(log);
                ArmQuietStatus();
            }
            else _startupLogs.Remove(log);
        }
    }

    private void ArmQuietStatus() => _monitor.Observe(PublishQuietStatus);

    private bool PublishQuietStatus()
    {
        string message;
        RunLog[] logs;
        lock (_gate)
        {
            if (_stopping) return false;
            var active = _registrations.Values.Where(value =>
                (value.Kind == RegistrationKind.Dispatch || value.Log is not null) &&
                value.State != RegistrationState.Draining && !IsPaused(value.PairId)).ToArray();
            if (active.Length == 0 && _startupLogs.Count == 0) return false;
            message = WaitReason() is { } waiting
                ? Text.Get(TextKey.StatusStillWaiting, (TextArg.Reason, waiting))
                : Text.Get(TextKey.StatusStillQuiet);
            logs = active.Select(value => value.Log).OfType<RunLog>().Concat(_startupLogs).Distinct().ToArray();
        }
        var reported = false;
        foreach (var log in logs) reported |= log.ReportProgress(message);
        if (!reported && Store is not null)
        {
            _progress(message);
            PublishStatus(message);
        }
        return true;
    }

    /// <summary>One gate and one active-item pass for the requested issues, applying the single lookup's order:
    /// recovery, then disabled, then unavailable (including a null candidate), then paused, then running, then
    /// waiting. A missing candidate is Unavailable, never the enum's default.</summary>
    internal IReadOnlyDictionary<long, RetryPresentationState> RetryStates(PairConfig pair, long generation,
        IReadOnlyDictionary<long, ItemRetryCandidate?> candidates)
    {
        var states = new Dictionary<long, RetryPresentationState>(candidates.Count);
        lock (_gate)
        {
            // Config answers a disabled pair; only the live retry states need a started authority (CV-RETRYVIEW).
            var disabled = !pair.Enabled;
            var unavailable = _stopping || !_started;
            var paused = !disabled && !unavailable && IsPaused(pair.PairId);
            HashSet<long>? running = null;
            if (!disabled && !unavailable && !paused && candidates.Values.Any(value => value is not null))
            {
                running = [];
                foreach (var item in _items.Values)
                    if (item.Operation.PairId == pair.PairId && item.Authority?.IssueId is { } id) running.Add(id);
            }
            foreach (var (issueId, candidate) in candidates)
                states[issueId] =
                    candidate?.Dispatch == ItemRetryDispatch.LocalRecovery
                        ? RetryPresentationState.RecoveryRequired
                    : disabled ? RetryPresentationState.Disabled
                    : unavailable || candidate is null || candidate.PairId != pair.PairId ||
                      candidate.Token.PairGeneration != generation ? RetryPresentationState.Unavailable
                    : paused ? RetryPresentationState.Paused
                    : running!.Contains(issueId) ? RetryPresentationState.Running
                    : RetryPresentationState.Waiting;
        }
        return states;
    }

    private readonly HashSet<System.Threading.Channels.Channel<HostStatusUpdate>> _statusSubscribers = [];

    internal async IAsyncEnumerable<HostStatusUpdate> SubscribeStatusAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<HostStatusUpdate>(
            new System.Threading.Channels.BoundedChannelOptions(LatestStatusCapacity)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false
            });
        lock (_gate)
        {
            if (_stopping || _sourceFailure is not null) yield break;
            _statusSubscribers.Add(channel);
            var status = Status();
            channel.Writer.TryWrite(new HostStatusUpdate(status.Status, status.Detail, null));
        }
        try
        {
            await foreach (var update in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return update;
        }
        finally
        {
            lock (_gate) _statusSubscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }

    private void PublishStatus(string? message = null)
    {
        lock (_gate)
        {
            var status = Status();
            var update = new HostStatusUpdate(status.Status, status.Detail, message);
            foreach (var channel in _statusSubscribers) channel.Writer.TryWrite(update);
        }
    }

    internal (AutoStatus? Status, string? Detail) Status()
    {
        lock (_gate)
        {
            if (_stopping) return (AutoStatus.Off, null);
            if (_sourceFailure is not null) return (AutoStatus.Held, null);
            if (_auto.IsGloballyPaused) return (AutoStatus.Paused, null);
            if (WaitReason() is { } waiting) return (AutoStatus.Waiting, waiting);
            if (_closed.Count != 0) return (AutoStatus.Held, null);
            if (_registrations.Values.Any(value => value.Kind == RegistrationKind.Dispatch))
                return (AutoStatus.Running, null);
            if (_volumes.Values.Any(volume => volume.Members is { Count: > 0 } && !volume.Source.Available))
                return (AutoStatus.Waiting, Text.Get(TextKey.JournalRepairRequired));
            return (_started ? AutoStatus.Idle : null, null);
        }
    }

    /// <summary>The single waiting reason for both status and quiet reports; callers hold _gate.</summary>
    private string? WaitReason() =>
        _listingWaits.Count != 0 ? nameof(AttemptOwner.Prerequisite)
        : null;

    /// <summary>Opens the host's one feed-wait contribution: a log line now and its closing line with the duration.</summary>
    internal Action OpenFeedWait(string pairId)
    {
        var log = _openLog(pairId);
        log?.Event(RunEvent.Stage(TextKey.StageFeedWait));
        lock (_gate) _feedWaits++;
        PublishStatus();
        return () =>
        {
            log?.Event(RunEvent.Stage(TextKey.StageFeedWait, ended: true, count: RunLog.CompletedOperation));
            log?.Dispose();
            lock (_gate) _feedWaits--;
            PublishStatus();
        };
    }

    internal Task StopAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _stopSupervision?.Invoke();
        return DisposeAsync().AsTask().WaitAsync(ct);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_dispose ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Registration[] drain;
        lock (_gate)
        {
            _stopping = true;
            PublishStatus();
            foreach (var channel in _statusSubscribers) channel.Writer.TryComplete();
            if (_wakeSource is not null) { _wakeSource.Woke -= PlatformWoke; _wakeSource = null; }
            foreach (var pending in _prerequisites.Values) pending.Cause = PrerequisiteStopCause.Stop;
            drain = SelectDrain(_ => true);
        }
        await Task.Yield();
        await _stop.CancelAsync().ConfigureAwait(false);
        List<Exception>? failures = null;
        async Task Join(Func<Task> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        await Join(() => _sourceCancellation).ConfigureAwait(false);
        await Join(() => _monitor.StopAsync()).ConfigureAwait(false);
        await Join(() => _auto.StopAsync()).ConfigureAwait(false);
        await Join(() => FreezeVolumesAsync(_resources.Keys.ToArray())).ConfigureAwait(false);
        await Join(() => DrainAsync(drain)).ConfigureAwait(false);
        await _configuration.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var resource in _resources.Values)
                await Join(() => resource.DisposeAsync().AsTask()).ConfigureAwait(false);
            _resources.Clear();
            Store.Dispose();
            _ownedHttp?.Dispose();
            Interlocked.Exchange(ref _hostStartupLog, null)?.Dispose();
            Interlocked.Exchange(ref _hostLog, null)?.Dispose();
        }
        finally { _configuration.Release(); }
        if (_sourceFailure is { } sourceFailure) (failures ??= []).Add(sourceFailure.SourceException);
        if (failures is not null) throw new AggregateException(failures);
    }

    private static LiveAdmissionStaleException Stale() => new(LiveAdmissionStaleReason.CoverageChanged);
    private static LiveAdmissionStaleException SelectionStale() => new(LiveAdmissionStaleReason.SelectionChanged);
    private static EngineRequestException Unknown(string pairId) => new(TextKey.PairUnknown, (TextArg.Pair, pairId));

    private sealed class Registration(string pairId, long generation, RegistrationKind kind, bool mutation,
        CancellationTokenSource cancellation)
    {
        internal string PairId { get; } = pairId;
        internal long Generation { get; } = generation;
        internal RegistrationKind Kind { get; } = kind;
        internal bool Mutation { get; } = mutation;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal volatile RegistrationState State;
        internal PairGenerationResources? Resources;
        internal RunLog? Log;
        internal HashSet<Guid> Items { get; } = [];
        internal Dictionary<object, HashSet<IssueSubject>> Consumed { get; } = new(ReferenceEqualityComparer.Instance);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal object InventoryGate { get; } = new();
        internal long? InventoryEpoch;
        internal Dictionary<string, (Entry Entry, TextKey Cause)> Inventory { get; } = new(StringComparer.Ordinal);
    }
    private sealed class Transition(string changingPairId, string[] ids, CancellationTokenSource cancellation)
    {
        internal string ChangingPairId { get; } = changingPairId;
        internal string[] Ids { get; } = ids;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal Dictionary<string, (PairConfig Pair, long Generation)> Expected { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, PairGenerationResources> Candidates { get; } = new(StringComparer.Ordinal);
    }
    private sealed class Volume
    {
        internal object Gate { get; } = new();
        internal SemaphoreSlim Serial { get; } = new(SingleHolder);
        internal JournalSource Source = null!;
        internal Task? Observed;
        internal Task Supervision = Task.CompletedTask;
        internal IReadOnlyList<JournalSource.Member>? Members = [];
        internal IReadOnlyList<JournalSource.Member> Frozen = [];
    }
    private sealed class Prerequisite(PairConfig pair, long generation)
    {
        internal PairConfig Pair { get; } = pair;
        internal long Generation { get; } = generation;
        internal PrerequisiteStopCause Cause;
        internal Task? Completion;
    }
    private sealed class ItemAttempt(Guid id, Registration operation, object opportunity, IssueSubject subject,
        ItemRetryToken? authority)
    {
        internal Guid Id { get; } = id;
        internal Registration Operation { get; } = operation;
        internal object Opportunity { get; } = opportunity;
        internal IssueSubject Subject { get; } = subject;
        internal volatile ItemRetryToken? Authority = authority;
        internal object Gate { get; } = new();
        internal (long, string, IssueSubject, TextKey, WorkSelection?, ItemRetryDispatch, PendingEffect?, ItemRetryToken?)? Report;
        internal ItemRetryCandidate? Reported;
        internal ItemRetryToken? CompletedToken;
        internal StateChange? Completed;
        internal PendingEffect? CompletedPending;
    }
    private sealed class ItemPort(EngineHost host, object registration) : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            host.ExecuteItemAttemptAsync(registration, opportunity, expected, subject, execute, ct);
        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
            TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
            ItemRetryToken? expectedEpisode) => host.ReportItemFailure(registration, attemptId, sourceEpoch, path,
                subject, cause, selection, dispatch, expectedPending, expectedEpisode);
        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change, PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) =>
            host.CompleteItemAttempt(registration, attemptId, expected, change, completedPending, selection, committedIssue);
    }
}
