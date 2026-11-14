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
    // Source recovery remains prerequisite-owned after another member makes the shared source available.
    private readonly Dictionary<string, long> _sourceRetries = new(StringComparer.Ordinal);
    // The item wait the host last wrote for each pair, with the keys its turns recorded (IR-1). Every host write of a
    // pair's retry row updates or removes its entry in the same locked step; nothing else sets a row.
    private readonly Dictionary<string, ItemWait> _itemWaits = new(StringComparer.Ordinal);
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

    // Acquisition and supervision outlive the caller, so the whole start runs without its context, such as Repair's.
    private Task StartJournalAsync(Volume volume) => WithoutFlow(() => Task.Run(() => StartJournalCoreAsync(volume)));

    private async Task StartJournalCoreAsync(Volume volume)
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

    // Publication, attribution and accounting share one membership snapshot; callbacks follow its atomic boundary.
    private void JournalFailed(Volume volume, Exception? error)
    {
        List<(string PairId, long? UnownedGeneration, TimeSpan? Wait)> affected = [];
        List<Exception>? accounting = null;
        // Membership precedes lifecycle; no lifecycle path acquires the membership gate.
        lock (volume.Gate)
        lock (_gate)
        {
            if (_stopping || volume.Members is not { Count: > 0 } members) return;
            volume.Source.Invalidate();
            var lost = volume.Source.ContinuityLost;
            foreach (var member in members)
            {
                var pairId = member.Pair.PairId;
                _sourceRetries[pairId] = member.Generation;
                if (lost) Store.RequireRepairAfterLoss(pairId, member.Generation);
                if (_prerequisites.TryGetValue(pairId, out var pending))
                {
                    pending.SourceFailed = true;
                    affected.Add((pairId, null, null));
                }
                else affected.Add((pairId, member.Generation, null));
            }
            // One member's accounting failure must not leave a later member's debt without its retry row.
            for (var index = 0; index < affected.Count; index++)
            {
                var member = affected[index];
                if (member.UnownedGeneration is not { } generation) continue;
                try
                {
                    affected[index] = (member.PairId, generation,
                        RecordPrerequisiteFailure(member.PairId, generation, providerWait: null, withoutNotice: true));
                }
                catch (Exception failure) { (accounting ??= []).Add(failure); }
            }
            RefreshMembers(volume, members, null);
        }
        foreach (var (pairId, _, wait) in affected)
        {
            using var log = _openLog(pairId);
            log?.Failed(error ?? new IOException(Text.Get(TextKey.JournalSourceFailed,
                (TextArg.Reason, nameof(JournalSource)), (TextArg.Value, string.Empty))));
            if (wait is { } selected) ReportPrerequisiteWait(pairId, TextKey.WaitSourceRetry, selected, log);
        }
        _monitor.RearmPrerequisiteWait();
        ArmQuietStatus();
        PublishStatus();
        // Still fatal (ObserveJournalAsync → BackgroundFailed); the members already accounted keep their rows.
        // One failure stays the original failure; several are reported together.
        if (accounting is [var only]) ExceptionDispatchInfo.Capture(only).Throw();
        if (accounting is not null) throw new AggregateException(accounting);
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
    private readonly HashSet<object> _listingWaits = new(ReferenceEqualityComparer.Instance);

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

    // The owned log, HTTP client, monitor and host are created in one try whose single catch dismantles each created part in order.
    /// <summary>The one production composition: the caller supplies its product root, progress sink and the
    /// already-owned authority, and Run wires settings, transport, store, logs and the clock once.</summary>
    internal static EngineHost Compose(string productRoot, Action<string> progress, PilotStateAuthority authority,
        Mode mode, EngineStartupAdmission admission, RunLog? startupLog = null, Monitor? startupMonitor = null, EngineSettings? runSettings = null)
    {
        admission.RequireRoot(productRoot);
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
            // A caller that already read its run's settings passes them, so one run uses one snapshot.
            var settings = runSettings ?? LoadSettings(productRoot);
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
                    : StateStore.Open(StateStore.DatabasePath(productRoot), validator, startupLog, DownloadSpace.Shared),
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

    // Host construction, monitor attach, store open and operations creation share one composed host whose catch rolls back whatever exists.
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
            var store = openStore(host.IsRegisteredItemAttempt) ?? throw new InvalidOperationException();
            lock (host._gate)
            {
                host.Store = store;
                host._auto = new AutoSync(store, openLog, host);
            }
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

    // Registration admission and its finally retirement bracket the caller execute in one lifetime.
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
            if (prerequisite) registration.Prerequisite = _prerequisites.GetValueOrDefault(pairId);
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
                // A retired dispatch's confirmation would keep its whole registration reachable from the wait.
                if (_itemWaits.TryGetValue(registration.PairId, out var wait) && ReferenceEquals(wait.Checked, registration))
                    wait.Checked = null;
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
                // A drain closes admission before cancellation is delivered: the same caller stop for the prerequisite's
                // own work. An ordinary dispatch is stale here, as at admission, whatever the pair's Repair is doing.
                if (current.State == RegistrationState.Draining && !_closed.Contains(pairId) &&
                    (current.Kind != RegistrationKind.Dispatch || current.Prerequisite is not null) &&
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
            // IR-1: drained dispatch can record no more keys for a paused pair; on resume a pending row holds it until its deadline.
            if (!resume) lock (_gate) { if (pairId is null) _itemWaits.Clear(); else _itemWaits.Remove(pairId); }
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

    // One transition owns the configuration semaphore, Transition record, drainage and candidate installation in one try/finally lifetime.
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
                    // A changed definition keeps no keys recorded under the old one; a pending row then holds the pair.
                    _itemWaits.Remove(id);
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
                lock (_gate) open = transition.Ids.Where(id => !_closed.Contains(id)).Concat(transition.Refused).ToArray();
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
                (PathNames.RelativeUnderDropboxRoot(PathNames.TrimDropboxRoot(value.DropboxRoot), pair.DropboxRoot) is not null ||
                 PathNames.RelativeUnderDropboxRoot(PathNames.TrimDropboxRoot(pair.DropboxRoot), value.DropboxRoot) is not null))
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
                    // IR-1: an unaffected pair refused here is woken when this transition ends.
                    if (!active.Ids.Contains(pairId, StringComparer.Ordinal)) active.Refused.Add(pairId);
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

    private PairGenerationResources ComposeResources(PairConfig pair, IReadOnlyList<PairConfig> scopes, long generation) =>
        PairSession.ComposeGeneration(Store, pair, scopes, generation, _settings, _http, _authority, _time,
            AdmissionForAccount, (identity, delay, reconnect) =>
                _monitor.ScheduleListenerReconnect(identity.SourceIdentity, delay, reconnect), pair.PairId, _externals);

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

    // Replacement, old disposal, volume membership and feed start share one installed-generation pass over the gate and volume state.
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
                if (_sourceRetries.TryGetValue(id, out var sourceGeneration) && current?.Generation != sourceGeneration)
                    _sourceRetries.Remove(id);
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
                () => captured.Members, (candidate, changed) => RefreshMembers(captured, candidate, changed),
                _settings.JournalPageBufferBytes);
            volume.Source.WorkCommitted = id => Wake(id);
            volume.Source.ReportItemFailureAsync = ReportJournalItemFailureAsync;
            // LocalSourceStopped reads the map under _gate from AutoSync and Monitor threads.
            lock (_gate) _volumes.Add(key, volume);
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

    internal async Task EnsureSourceBasesAsync(string pairId, CancellationToken ct, RunLog? log = null,
        bool prerequisite = false)
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
                await CatchUpLocalAsync(pairId, generation, token, log, prerequisite).ConfigureAwait(false);
                return pair;
            }
            finally { lock (_gate) ((Registration)registration).Log = null; }
        }, ct).ConfigureAwait(false);
    }

    internal async Task<LocalCatchUp> CatchUpLocalAsync(string pairId, long generation, CancellationToken ct,
        RunLog? log = null, bool prerequisite = false)
    {
        var pair = Store.ReadPairExecution(pairId) ?? throw Stale();
        if (pair.Generation != generation || pair.Pair.Roots is null) throw Stale();
        var key = pair.Pair.Roots.LocalVolumeKey();
        if (!_volumes.TryGetValue(key, out var volume)) throw Stale();
        if (prerequisite || !prerequisite)
        {
            return await WithVolumeStoppedAsync(volume, async token =>
            {
                var caughtUp = await volume.Source.CatchUpForPairAsync(pairId, generation, token, log).ConfigureAwait(false);
                // The attempt's own catch-up discharges a source failure that reached it earlier; the stopped reader and
                // held serial keep any later failure after this point, where JournalFailed sets the flag again.
                lock (volume.Gate)
                lock (_gate)
                    if (_prerequisites.TryGetValue(pairId, out var pending)) pending.SourceFailed = false;
                return caughtUp;
            }, ct, log).ConfigureAwait(false);
        }
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
        return Store.HasOpenIssue(pairId, IssueKind.DriveChange)
            ? PairAdmission.AwaitingDecision : PairAdmission.Admitted;
    }

    // A stopped Local source owes prerequisite catch-up; this reads no membership gate while callers hold _gate.
    private bool LocalSourceStopped(PairConfig pair) => pair.Roots is { } roots &&
        _volumes.TryGetValue(roots.LocalVolumeKey(), out var volume) && !volume.Source.Available;

    // Compose durable classification with source availability and the member's retained recovery purpose. The
    // generation is read here, under _gate, because the purpose is dropped under _gate when a generation is replaced.
    private FailureRetryOwner.State? ItemRetry(string pairId)
    {
        lock (_gate)
            return Store.ReadItemRetry(pairId) is { } row && Store.ReadPairExecution(pairId) is { } current &&
                !SourceRecoveryPending(pairId, current.Generation) && !LocalSourceStopped(current.Pair) ? row : null;
    }

    // Callers hold _gate; purpose belongs to the failed member's generation, not the volume's availability.
    private bool SourceRecoveryPending(string pairId, long generation) =>
        _sourceRetries.TryGetValue(pairId, out var failedGeneration) && failedGeneration == generation;

    /// <summary>The item-only state this dispatch is admitted with; true when a pre-deadline wait must hold the
    /// pair's ordinary dispatch.</summary>
    internal bool ItemRetryHolds(string pairId, long generation, out FailureRetryOwner.State? admitted)
    {
        admitted = ItemRetry(pairId);
        return admitted is { } row && _monitor.ItemWaitPending(pairId, generation, row);
    }

    /// <summary>True when this live dispatch must skip the side and key: a running Repair's unaccounted temporary turn,
    /// or the pair's pending item wait that recorded it (IR-1). Only AutoSync's SyncWorkAsync reaches it. Host-owned
    /// inputs are read under one lock. The stored row is read once per dispatch, at its first held key, so a decision
    /// costs no store read after that; a row clear outside the host after that read is seen by the next dispatch.</summary>
    internal bool HeldForRetry(object registration, Side side, string key)
    {
        var operation = (Registration)registration;
        lock (_gate)
        {
            if (_prerequisites.TryGetValue(operation.PairId, out var repair) &&
                repair.Retained?.Contains((side, key)) == true) return true;
            if (!_itemWaits.TryGetValue(operation.PairId, out var known) || !known.Keys.Contains((side, key))) return false;
            return LiveItemWait(operation.PairId, operation) is { } wait &&
                _monitor.ItemWaitPending(operation.PairId, operation.Generation, wait.State);
        }
    }

    /// <summary>True when the pending wait being admitted is the one the host recorded keys for, so only those keys
    /// wait.</summary>
    internal bool ItemWaitNarrows(string pairId, FailureRetryOwner.State pending)
    {
        lock (_gate) return _itemWaits.TryGetValue(pairId, out var wait) && wait.State == pending;
    }

    /// <summary>An admitted due wait hands its keys to this dispatch; with no row, a stale entry is retired.</summary>
    internal void ReleaseItemWait(string pairId, FailureRetryOwner.State? due)
    {
        lock (_gate)
        {
            if (due is null) LiveItemWait(pairId);
            else if (_itemWaits.TryGetValue(pairId, out var wait) && wait.State == due) _itemWaits.Remove(pairId);
        }
    }

    // Callers hold _gate. A write continues the entry only when the row it advanced is the entry's own state.
    private void RecordItemKeys(string pairId, IEnumerable<(Side Side, string Key)> keys,
        FailureRetryOwner.State? advanced, FailureRetryOwner.State next)
    {
        if (!_itemWaits.TryGetValue(pairId, out var wait) || advanced is null || wait.State != advanced)
            _itemWaits[pairId] = wait = new ItemWait(next);
        wait.State = next;
        wait.Keys.UnionWith(keys);
    }

    // Callers hold _gate. The entry describes the pair's row only while the row is still the host's last write; a row
    // cleared outside the host retires the entry here, when observed. A dispatch that already confirmed the row reuses
    // that answer: the host's own writes keep State equal to the row, and a dispatch reads it once, not once per key.
    private ItemWait? LiveItemWait(string pairId, object? dispatch = null)
    {
        if (!_itemWaits.TryGetValue(pairId, out var wait)) return null;
        if (dispatch is not null && ReferenceEquals(wait.Checked, dispatch)) return wait;
        if (Store.ReadItemRetry(pairId) == wait.State)
        {
            wait.Checked = dispatch;
            return wait;
        }
        _itemWaits.Remove(pairId);
        return null;
    }

    // The attempt record, generation, log ownership, stop registration and exactly-once completion share one try/catch/finally lifetime.
    // An explicit request is told here, under the gate, that the registered Repair (until its retirement, after
    // FinishRepair) has taken it; with no obligation it gets null for its own ordinary Repair (B-131).
    internal async Task<RepairSummary?> RunPrerequisiteAsync(string pairId, CancellationToken ct,
        RunLog? startupLog = null, Issue? expected = null,
        (long Generation, FailureRetryOwner.State State)? schedule = null, bool explicitRequest = false)
    {
        Prerequisite? attempt = null;
        var itemOnly = false;
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
                var reviewedRetry = expected is null ? null : Store.ReadPrerequisiteRetry(pairId, expected);
                if (_stopping || current is null ||
                    Admission(pairId, current.Value.Pair) != PairAdmission.Admitted) return null;
                if (_prerequisites.TryGetValue(pairId, out var pending))
                {
                    if (schedule is null) pending.OwedEvent = true;
                    else pending.OwedSchedule = true;
                    if (explicitRequest) throw new EngineRequestException(TextKey.RepairAlreadyRunning, (TextArg.Pair, pairId));
                    return null;
                }
                if (explicitRequest && !Store.Obligated(pairId)) return null;
                var retry = expected is null ? Store.ReadPrerequisiteRetry(pairId) : reviewedRetry;
                if (schedule is { } selected &&
                    (selected.Generation != current.Value.Generation || selected.State != retry)) return null;
                if (retry is not null && ItemRetry(pairId) is not null) itemOnly = true;
                else
                {
                    if (!Store.Obligated(pairId) && retry is null &&
                        !SourceRecoveryPending(pairId, current.Value.Generation) && !LocalSourceStopped(current.Value.Pair)) return null;
                    attempt = new Prerequisite(current.Value.Pair, current.Value.Generation);
                    generation = current.Value.Generation;
                    _prerequisites.Add(pairId, attempt);
                }
            }
            if (itemOnly)
            {
                // A due wake hands the retained work to ordinary dispatch; an event wake admits nothing and leaves the wait armed.
                if (schedule is not null) _auto.Kick(pairId);
                return null;
            }
            // Registered after the attempt exists so an already-cancelled caller still classifies it.
            stop = ct.Register(() =>
            {
                lock (_gate) attempt!.Cause = PrerequisiteStopCause.UserCancellation;
            });
            ownedLog = startupLog is null ? _openLog(pairId) : null;
            log ??= ownedLog;
            await RecomposeCurrentAsync(pairId, ct, log).ConfigureAwait(false);
            if (expected is not null) Store.ReadPrerequisiteRetry(pairId, expected);
            await EnsureSourceBasesAsync(pairId, ct, log, prerequisite: true).ConfigureAwait(false);
            if (!Store.Obligated(pairId))
            {
                var local = Store.ReadSource(pairId, Side.Local) ?? throw Stale();
                var dropbox = Store.ReadSource(pairId, Side.Dropbox) ?? throw Stale();
                var retry = Store.ReadPrerequisiteRetry(pairId, expected);
                if (retry is not null)
                    Store.CompleteRecomposition(pairId, generation, local.Epoch, dropbox.Epoch,
                        local.Revision, dropbox.Revision, retry.Value);
                return null;
            }
            summary = await DispatchSessionAsync(pairId, () => log,
                async (session, token) =>
                {
                    if (expected is not null) Store.ReadPrerequisiteRetry(pairId, expected);
                    // The scope covers only the awaited Repair, never dispatch's recomposition or feed start.
                    using var scope = RepairScope.Enter();
                    return await Repair.RunAsync(session, token).ConfigureAwait(false);
                }, ct, log, prerequisite: true).ConfigureAwait(false);
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
                    try { await CompletePrerequisiteAsync(attempt, summary, failure, log).ConfigureAwait(false); }
                    // Accounting never revokes a committed Repair or replaces an earlier primary.
                    catch (Exception error) when (summary is { Completed: true })
                    {
                        log?.Failed(error, TextKey.RepairCompletedSecondaryFailure);
                    }
                    catch (Exception error)
                    {
                        log?.Failed(error);
                        if (failure is null) throw;
                    }
                }
            }
            finally
            {
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
        await CompletePrerequisiteAsync(attempt, null, error).ConfigureAwait(false);
    }

    // The memoized completion owns one exact registration through accounting, release and deferred callbacks.
    private Task CompletePrerequisiteAsync(Prerequisite pending, RepairSummary? summary, Exception? error,
        RunLog? log = null)
    {
        lock (_gate)
        {
            if (pending.Completion is not null) return pending.Completion;
            if (!_prerequisites.TryGetValue(pending.Pair.PairId, out var current) ||
                !ReferenceEquals(current, pending)) throw Stale();
            return pending.Completion = CompleteAsync();
        }

        // Accounting and exact removal stay indivisible to C4; the finally delivers that same attempt's captured opportunities.
        async Task CompleteAsync()
        {
            await Task.Yield();
            var pairId = pending.Pair.PairId;
            TimeSpan? wait = null;
            TextKey? reason = null;
            var succeeded = false;
            try
            {
                lock (_gate)
                {
                    try
                    {
                        if (_stopping || IsPaused(pairId) || pending.Cause is not
                            (PrerequisiteStopCause.None or PrerequisiteStopCause.OtherPairConfiguration)) return;
                        var pair = Store.ReadPairExecution(pairId);
                        if (pair is null || !pair.Value.Pair.Enabled || pair.Value.Generation != pending.Generation) return;
                        // A completed Repair committed its own FinishRepair; debt that landed after it is a new event
                        // for the owed Repair, not a failure of this one (engine.md 819, RQ-179).
                        succeeded = error is null && (summary is { Completed: true } ||
                            !Store.Obligated(pairId) && Store.ReadPrerequisiteRetry(pairId) is null);
                        if (!pending.SourceFailed &&
                            (succeeded || error is EngineRequestException { Key: TextKey.ResolveStale }))
                        {
                            if (succeeded) _sourceRetries.Remove(pairId);
                            // The Repair no longer owns the row: its own temporary turn starts the item wait now,
                            // printed as a wait (retry is output, not a notice).
                            if (pending.ItemTurn is { } turn)
                                (wait, reason) = (RecordPrerequisiteFailure(pairId, pending.Generation, Remaining(turn),
                                    withoutNotice: true, itemKeys: pending.Retained ?? []), TextKey.WaitItemRetry);
                            return;
                        }
                        reason = pending.SourceFailed ? TextKey.WaitSourceRetry :
                            summary?.Superseded == true ? TextKey.WaitSupersededRetry : null;
                        wait = RecordPrerequisiteFailure(pairId, pending.Generation, Remaining(pending.ItemTurn),
                            reason is not null);
                    }
                    finally
                    {
                        if (_prerequisites.TryGetValue(pairId, out var registered) && ReferenceEquals(registered, pending))
                            _prerequisites.Remove(pairId);
                    }
                }
            }
            finally
            {
                // Release precedes callbacks; a successful continuation is never a new prerequisite event.
                _monitor.RearmPrerequisiteWait(pending.OwedSchedule ? pairId : null);
                if (pending.OwedEvent) _monitor.Wake(pairId);
                // IR-1: a retained turn with no written row is handed back; a written row's wait owns its wake.
                if (succeeded || (pending.ItemTurn is not null && wait is null)) _auto.Kick(pairId);
                ArmQuietStatus();
                if (wait is { } selected && reason is { } key) ReportPrerequisiteWait(pairId, key, selected, log);
            }
        }
    }

    private TimeSpan? Remaining((TimeSpan? Wait, long? Started)? turn) =>
        turn is { } held ? FailureRetryOwner.RemainingProviderObservation(held.Wait, held.Started, _time) : null;

    // Callers hold _gate.
    private TimeSpan RecordPrerequisiteFailure(string pairId, long generation, TimeSpan? providerWait,
        bool withoutNotice = false, IEnumerable<(Side Side, string Key)>? itemKeys = null)
    {
        var local = Store.ReadSource(pairId, Side.Local) ?? throw Stale();
        var dropbox = Store.ReadSource(pairId, Side.Dropbox) ?? throw Stale();
        var expected = Store.ReadPrerequisiteRetry(pairId);
        var now = _time.GetUtcNow();
        var next = FailureRetryOwner.AdoptedFailed(expected?.Attempts ?? 0, now, providerWait,
            _settings.Backoff, Random.Shared.NextDouble);
        Store.RecordPrerequisiteFailure(pairId, generation, local.Epoch, dropbox.Epoch,
            local.Revision, dropbox.Revision, expected, next, withoutNotice ? StateChange.Empty : RetryNotice());
        // Every host row write keeps the entry true: item keys continue or start it; any other write retires it.
        if (itemKeys is null) _itemWaits.Remove(pairId);
        else RecordItemKeys(pairId, itemKeys, expected, next);
        return next.RetryAfter - now;
    }

    private void ReportPrerequisiteWait(string pairId, TextKey reason, TimeSpan wait, RunLog? log)
    {
        var item = new WaitEvent(reason, wait);
        if (log?.ReportWait(item) == true) return;
        using var waitingLog = _openLog(pairId);
        if (waitingLog?.ReportWait(item) != true)
            ReportProgress(Text.Get(reason, (TextArg.Seconds,
                wait.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    // The begin, complete and dispose delegates share one identity, retry episode, open notice and ended flag for one listing lifetime.
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
            if (HoldsItem(operation.PairId, subject, expected?.Token.IssueId)) throw SelectionStale();
            if (consumed is null) operation.Consumed.Add(opportunity, consumed = new(IssueSubject.ItemSetComparer));
            consumed.Add(subject);
            var item = new ItemAttempt(ResourceNames.NewAttemptId(), operation, opportunity, subject, expected?.Token);
            _items.TryAdd(item.Id, item);
            operation.Items.Add(item.Id);
            return item;
        }
    }

    // An active attempt on any of these items, or under this issue's episode, holds them.
    private bool HoldsItem(string pairId, IssueSubject subject, long? issueId) =>
        _items.Values.Any(active => active.Operation.PairId == pairId &&
            (active.Subject.Items.Intersect(subject.Items).Any() || issueId is not null && active.Authority?.IssueId == issueId));

    /// <summary>Whether this registered attempt may act; an empty id asks whether any active attempt holds the
    /// episode, which fences the store's retirement of a replaced file's episode (B-177).</summary>
    internal bool IsRegisteredItemAttempt(Guid attemptId, string pairId, long generation,
        IssueSubject subject, ItemRetryToken? expectedEpisode) =>
        attemptId == Guid.Empty ? HoldsItem(pairId, subject, expectedEpisode?.IssueId) :
        _items.TryGetValue(attemptId, out var item) && item.Operation.PairId == pairId &&
        item.Operation.Generation == generation && item.Operation.Mutation &&
        item.Operation.State != RegistrationState.Retired && item.Subject.HasSameItems(subject) &&
        TokenEquals(item.Authority, expectedEpisode);

    internal ItemRetryCandidate ReportItemFailure(object registration, Guid attemptId, long sourceEpoch,
        string path, IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
        PendingEffect? expectedPending, ItemRetryToken? expectedEpisode, Action<Issue, bool>? settledIssue = null,
        Action<Issue>? committedIssue = null)
    {
        var item = RequireItem(registration, attemptId);
        var report = (sourceEpoch, path, subject, cause, selection, dispatch, expectedPending, expectedEpisode);
        (Issue Issue, bool IsNew)? settled = null;
        ItemRetryCandidate result;
        lock (item.Gate)
        {
            if (item.Report is { } previous)
            {
                if (previous.Item1 != sourceEpoch || previous.Item2 != path ||
                    !SubjectEquals(previous.Item3, subject) || previous.Item4 != cause ||
                    previous.Item5 != selection || previous.Item6 != dispatch || previous.Item7 != expectedPending ||
                    !TokenEquals(previous.Item8, expectedEpisode)) throw SelectionStale();
                result = item.Reported!;
            }
            else
            {
                if (item.Completed is not null || !TokenEquals(item.Authority, expectedEpisode)) throw SelectionStale();
                result = Store.RecordInUseFailure(item.Operation.PairId, item.Operation.Generation, sourceEpoch,
                    path, subject, cause, selection, dispatch, expectedPending, expectedEpisode, attemptId,
                    (issue, isNew) => settled = (issue, isNew));
                item.Report = report;
                item.Reported = result;
                item.ReportedIssue = settled!.Value.Issue;
                item.Authority = result.Token;
            }
        }
        // Responsibility survives replay even when its first consumer threw; announcements never replay.
        committedIssue?.Invoke(item.ReportedIssue!);
        if (settled is { } announcement) settledIssue?.Invoke(announcement.Issue, announcement.IsNew);
        return result;
    }

    // One proven-no-effect temporary turn: retention every time, accounting at most once per registration.
    internal (bool NewlyAccepted, DateTimeOffset? RetryAt) ReportTemporaryFailure(object registration, Guid attemptId,
        long sourceEpoch, string path, IssueSubject subject, TextKey cause, WorkSelection? selection,
        ItemRetryDispatch dispatch, TimeSpan? providerWait, long? providerWaitStarted)
    {
        var item = RequireItem(registration, attemptId);
        var owner = item.Operation;
        var report = (sourceEpoch, path, subject, cause, selection, dispatch, providerWait, providerWaitStarted);
        var advanced = false;
        DateTimeOffset? retryAt;
        lock (item.Gate)
        {
            if (item.Temporary is { } previous)
            {
                if (previous.Item1 != sourceEpoch || previous.Item2 != path ||
                    !SubjectEquals(previous.Item3, subject) || previous.Item4 != cause ||
                    previous.Item5 != selection || previous.Item6 != dispatch || previous.Item7 != providerWait ||
                    previous.Item8 != providerWaitStarted) throw SelectionStale();
                return (false, item.TemporaryRetryAt);
            }
            if (item.Report is not null || item.Completed is not null) throw SelectionStale();
            // An acquired selection keeps its work by not completing; without one, the endpoint becomes ordinary work.
            (Side Side, string Path, string? Id)? successor = selection is null ? Successor(path, subject) : null;
            // The join decision, advance and publication are one step under _gate, so concurrent distinct items of
            // one registration account at most once.
            lock (_gate)
            {
                var (repair, joined) = (owner.Prerequisite, owner.ItemRetry);
                // The key the retained work is selected under: the selection's own, or the one AddEndpoint stores.
                var retained = selection is { } held ? (held.Side, held.Key)
                    : (successor!.Value.Side, PathNames.KeyFor(successor.Value.Side, successor.Value.Path));
                if (repair is not null || joined is not null)
                {
                    // The running Repair accounts its own turn once after it ends; a later ordinary turn joins.
                    Store.RecordTemporaryItem(owner.PairId, owner.Generation, sourceEpoch, selection, successor, null, null);
                    if (repair is not null)
                    {
                        repair.ItemTurn ??= (providerWait, providerWaitStarted);
                        (repair.Retained ??= []).Add(retained);
                    }
                    else
                    {
                        // A late turn joins the pair's current wait, the one row (B-70), never an older state.
                        if (_itemWaits.TryGetValue(owner.PairId, out var current)) current.Keys.Add(retained);
                    }
                    retryAt = joined?.RetryAfter;
                }
                else
                {
                    var expected = Store.ReadPrerequisiteRetry(owner.PairId);
                    var next = FailureRetryOwner.AdoptedFailed(expected?.Attempts ?? 0, _time.GetUtcNow(),
                        FailureRetryOwner.RemainingProviderObservation(providerWait, providerWaitStarted, _time),
                        _settings.Backoff, Random.Shared.NextDouble);
                    // A CAS clash returns the newer state, which this registration joins with no second draw.
                    var committed = Store.RecordTemporaryItem(owner.PairId, owner.Generation, sourceEpoch, selection,
                        successor, expected, next) ?? throw Stale();
                    advanced = committed == next;
                    owner.ItemRetry = committed;
                    RecordItemKeys(owner.PairId, [retained], expected, committed);
                    retryAt = committed.RetryAfter;
                }
            }
            item.Temporary = report;
            item.TemporaryRetryAt = retryAt;
        }
        // The commit precedes the signal, so a quiet Monitor refreshes and arms the new row's timer.
        if (advanced) _monitor.RearmPrerequisiteWait();
        return (true, retryAt);
    }

    // The source-side endpoint of a turn without an acquired selection, with its observed ID when one is known.
    private static (Side Side, string Path, string? Id) Successor(string path, IssueSubject subject)
    {
        var side = subject.EndpointSide(path) ?? throw new ArgumentException(null, nameof(subject));
        return (side, path, subject.Items.FirstOrDefault(item => item.Side == side && item.Path == path)?.Id);
    }

    internal void CompleteItemAttempt(object registration, Guid attemptId, ItemRetryCandidate expected, StateChange change,
        PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null,
        Action<Issue, bool>? settledIssue = null)
    {
        var item = RequireItem(registration, attemptId);
        List<(Issue Issue, bool IsNew)>? settled = null;
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
            Store.CompleteItemAttempt(expected, attemptId, change, completedPending, selection, committedIssue,
                settledIssue is null ? null : (issue, isNew) => (settled ??= []).Add((issue, isNew)));
            item.CompletedPending = completedPending;
            item.CompletedToken = expected.Token;
            item.Completed = change;
        }
        if (settled is not null)
            foreach (var (issue, isNew) in settled) settledIssue!(issue, isNew);
    }

    // Row 43a: the respelling commits under the attempt's own token but leaves it uncompleted, so the unwind frees its
    // subject for the content attempt in the same opportunity.
    internal void CommitItemRename(object registration, Guid attemptId, ItemRetryCandidate expected, StateChange change,
        WorkSelection? selection)
    {
        var item = RequireItem(registration, attemptId);
        lock (item.Gate)
        {
            if (item.Completed is not null || item.Reported is not null || !TokenEquals(item.Authority, expected.Token))
                throw SelectionStale();
            Store.CommitItemRename(expected, attemptId, change, selection);
        }
    }

    private ItemAttempt RequireItem(object registration, Guid id) =>
        _items.TryGetValue(id, out var item) && ReferenceEquals(item.Operation, registration)
            ? item : throw SelectionStale();

    private static bool SameChange(StateChange left, StateChange right) =>
        ReferenceEquals(left, right) || left.Record.SequenceEqual(right.Record) &&
        left.StaleBindings.SequenceEqual(right.StaleBindings) &&
        left.ForgetBothSubtrees.SequenceEqual(right.ForgetBothSubtrees) &&
        left.LocalPublished.Equals(right.LocalPublished) && left.CloseScope.Equals(right.CloseScope) &&
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
            var item = ReserveItem(registration, registration, expected, attemptSubject);
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

    internal bool IsPaused(string pairId) => _auto?.IsPaused(pairId) == true;
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

    private readonly AsyncLocal<WaitingObservation?> _publishingObservation = new();

    // One captured eligibility answer must accompany both log delivery and its synchronous status publication.
    private bool PublishQuietStatus()
    {
        WaitingObservation observation;
        RunLog[] logs;
        RunLog[] retryLogs;
        string message;
        lock (_gate)
        {
            if (_stopping) return false;
            observation = ObserveWaiting();
            var active = _registrations.Values.Where(value =>
                (value.Kind == RegistrationKind.Dispatch || value.Log is not null) &&
                value.State != RegistrationState.Draining && !IsPaused(value.PairId)).ToArray();
            if (active.Length == 0 && _startupLogs.Count == 0 && observation.RetryPair is null) return false;
            message = _listingWaits.Count != 0 || observation.RetryPair is not null
                ? Text.Get(TextKey.StatusStillWaiting, (TextArg.Reason, nameof(AttemptOwner.Prerequisite)))
                : Text.Get(TextKey.StatusStillQuiet);
            logs = active.Select(value => value.Log).OfType<RunLog>().Concat(_startupLogs).Distinct().ToArray();
            retryLogs = active.Where(value => value.PairId == observation.RetryPair)
                .Select(value => value.Log).OfType<RunLog>().Distinct().ToArray();
        }
        var previous = _publishingObservation.Value;
        _publishingObservation.Value = observation;
        // Synchronous callbacks share this observation; work they start must observe its own later state.
        try
        {
            return WithoutFlow(() =>
            {
                var reported = false;
                var retryReported = false;
                foreach (var log in logs)
                {
                    var retryLog = retryLogs.Contains(log);
                    var accepted = log.ReportProgress(message, includeFallback: retryLog);
                    reported |= accepted;
                    retryReported |= accepted && retryLog;
                }
                if (observation.RetryPair is { } pairId && !retryReported)
                {
                    using var log = _openLog(pairId);
                    reported = log?.ReportProgress(message) == true;
                }
                if (!reported && Store is not null)
                {
                    _progress(message);
                    PublishStatus(message);
                }
                return true;
            });
        }
        finally { _publishingObservation.Value = previous; }
    }

    // Work started inside does not inherit this caller's ambient scopes; never spans an await.
    private static T WithoutFlow<T>(Func<T> start)
    {
        if (ExecutionContext.IsFlowSuppressed()) return start();
        using (ExecutionContext.SuppressFlow()) return start();
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
            if (_statusSubscribers.Count == 0) return;
            var status = _publishingObservation.Value ?? ObserveWaiting();
            var update = new HostStatusUpdate(status.Status, status.Detail, message);
            foreach (var channel in _statusSubscribers) channel.Writer.TryWrite(update);
        }
    }

    internal (AutoStatus? Status, string? Detail) Status()
    {
        lock (_gate)
        {
            var observation = ObserveWaiting();
            return (observation.Status, observation.Detail);
        }
    }

    private WaitingObservation ObserveWaiting()
    {
        if (_stopping) return new(AutoStatus.Off, null, null);
        if (_sourceFailure is not null) return new(AutoStatus.Held, null, null);
        if (_auto is null || Store is null) return new(null, null, null);
        if (_auto.IsGloballyPaused) return new(AutoStatus.Paused, null, null);
        string? retryPair = null;
        long? ticks = null;
        long? row = null;
        while (retryPair is null)
        {
            var count = 0;
            foreach (var retry in Store.ReadPrerequisiteRetries(ticks, row, _settings.StoragePageSize))
            {
                count++;
                ticks = retry.State.RetryAfter.UtcTicks;
                row = retry.PairRow;
                if (_prerequisites.ContainsKey(retry.PairId)) continue;
                var current = Store.ReadPairExecution(retry.PairId);
                if (current is null || current.Value.Generation != retry.Generation ||
                    Admission(retry.PairId, current.Value.Pair) != PairAdmission.Admitted) continue;
                retryPair = retry.PairId;
                break;
            }
            if (count < _settings.StoragePageSize) break;
        }
        if (_listingWaits.Count != 0) return new(AutoStatus.Waiting, nameof(AttemptOwner.Prerequisite), retryPair);
        if (_closed.Count != 0) return new(AutoStatus.Held, null, retryPair);
        if (_registrations.Values.Any(value => value.Kind == RegistrationKind.Dispatch))
            return new(AutoStatus.Running, null, retryPair);
        if (retryPair is not null) return new(AutoStatus.Waiting, nameof(AttemptOwner.Prerequisite), retryPair);
        if (_volumes.Values.Any(volume => volume.Members is { Count: > 0 } && !volume.Source.Available))
            return new(AutoStatus.Waiting, Text.Get(TextKey.JournalRepairRequired), null);
        return new(_started ? AutoStatus.Idle : null, null, null);
    }

    private sealed record WaitingObservation(AutoStatus? Status, string? Detail, string? RetryPair);

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
        // The committed or joined item state of this registration's first temporary turn; null if none.
        internal FailureRetryOwner.State? ItemRetry;
        // The prerequisite this dispatch runs for; null for ordinary dispatch.
        internal Prerequisite? Prerequisite;
        internal Dictionary<string, (Entry Entry, TextKey Cause)> Inventory { get; } = new(StringComparer.Ordinal);
    }
    private sealed class Transition(string changingPairId, string[] ids, CancellationTokenSource cancellation)
    {
        internal string ChangingPairId { get; } = changingPairId;
        internal string[] Ids { get; } = ids;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal Dictionary<string, (PairConfig Pair, long Generation)> Expected { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, PairGenerationResources> Candidates { get; } = new(StringComparer.Ordinal);
        // Unaffected pairs whose start this transition refused; its end wakes them (IR-1).
        internal HashSet<string> Refused { get; } = new(StringComparer.Ordinal);
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
        internal bool SourceFailed;
        internal bool OwedEvent;
        internal bool OwedSchedule;
        internal Task? Completion;
        // The first temporary turn of this Repair: the provider wait and its receipt time, if any.
        internal (TimeSpan? Wait, long? Started)? ItemTurn;
        // Side and key of this Repair's unaccounted temporary turns; live work skips them until completion (IR-1).
        internal HashSet<(Side Side, string Key)>? Retained;
    }
    private sealed class ItemWait(FailureRetryOwner.State state)
    {
        internal FailureRetryOwner.State State = state;
        internal HashSet<(Side Side, string Key)> Keys { get; } = [];
        // The running dispatch that last read the stored row and found State; its retirement clears it.
        internal object? Checked;
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
        internal Issue? ReportedIssue;
        internal ItemRetryToken? CompletedToken;
        internal StateChange? Completed;
        internal PendingEffect? CompletedPending;
        internal (long, string, IssueSubject, TextKey, WorkSelection?, ItemRetryDispatch, TimeSpan?, long?)? Temporary;
        internal DateTimeOffset? TemporaryRetryAt;
    }
    private sealed class ItemPort(EngineHost host, object registration) : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) =>
            host.ExecuteItemAttemptAsync(registration, opportunity, expected, subject, execute, ct);
        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
            TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
            ItemRetryToken? expectedEpisode, Action<Issue, bool>? settledIssue = null,
            Action<Issue>? committedIssue = null) =>
            host.ReportItemFailure(registration, attemptId, sourceEpoch, path,
                subject, cause, selection, dispatch, expectedPending, expectedEpisode, settledIssue, committedIssue);
        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null,
            Action<Issue, bool>? settledIssue = null) =>
            host.CompleteItemAttempt(registration, attemptId, expected, change, completedPending, selection,
                committedIssue, settledIssue);
        public void CommitRename(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            WorkSelection? selection) => host.CommitItemRename(registration, attemptId, expected, change, selection);
        public (bool NewlyAccepted, DateTimeOffset? RetryAt) ReportTemporary(Guid attemptId, long sourceEpoch,
            string path, IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
            TimeSpan? providerWait, long? providerWaitStarted) => host.ReportTemporaryFailure(registration, attemptId,
                sourceEpoch, path, subject, cause, selection, dispatch, providerWait, providerWaitStarted);
        public bool HeldForRetry(Side side, string key) => host.HeldForRetry(registration, side, key);
    }
}
