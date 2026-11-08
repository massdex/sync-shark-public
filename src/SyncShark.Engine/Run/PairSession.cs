using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Composes one pair's run through Open and its private constructor.
/// The Syncer shares the pair runtime; adapters and events share the supplied scope, store and log. The HTTP client
/// and the account-keyed limiter are process-owned; the scope, adapters and executor are session-owned.</summary>
public sealed class PairSession
{
    /// <summary>Receives the same wrapped sides, loaded settings and product-lifetime scan owner used by the caller.</summary>
    private PairSession(IStateStore store, PairConfig pair, EngineSettings settings, PathScope scope,
        ISide local, ISide dropbox, ISide prerequisiteDropbox, ILocalRecovery recovery,
        IAdmission admission, bool priority, RunLog? log,
        Func<string, string, long, CancellationToken, ValueTask<IAsyncDisposable>> reserveDownloadAsync,
        TimeProvider time,
        Func<bool, CancellationToken, Task<(RootBinding Roots, DriveEvidence? Drive)>> readRoots,
        ScanOwnership scans, EngineHost host, object registration, long expectedGeneration,
        Func<string, bool, CancellationToken, Task<DeletionEvidence>> readDeletionEvidenceAsync,
        Func<object, RequestAttempt> beginDownloadAttempt,
        Func<RequestAttempt, CancellationToken, Task<bool>> completeDownloadAttemptAsync,
        (string AccountId, string RouteName, string SourceIdentity) uploadAdmissionIdentity,
        IItemAttempts itemAttempts,
        Func<CancellationToken, Task<string>>? captureFolderCursorAsync = null,
        Func<string, CancellationToken, IAsyncEnumerable<Listed>>? readFolderChangesAsync = null,
        Func<RequestAttempt, CancellationToken, Task<bool>>? completeRecoveryAttemptAsync = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(itemAttempts);
        ArgumentNullException.ThrowIfNull(readRoots);
        host.RequireGeneration(registration, pair.PairId, expectedGeneration);
        if (!ReferenceEquals(store, host.Store) || !ReferenceEquals(scans, host.Scans))
            throw new ArgumentException(null, nameof(host));
        Store = store;
        Pair = pair;
        Settings = settings;
        Scope = scope;
        Local = local;
        Dropbox = dropbox;
        PrerequisiteDropbox = prerequisiteDropbox;
        Recovery = recovery;
        Admission = admission;
        Priority = priority;
        Log = log;
        Time = time;
        Scans = scans;
        Host = host;
        Registration = registration;
        ExpectedGeneration = expectedGeneration;
        ItemAttempts = itemAttempts;
        _readRoots = readRoots;
        var context = new ExecutorContext(Local: local, Dropbox: dropbox, Recovery: recovery,
            Store: store, Pair: pair, Scope: scope, Settings: settings,
            Runtime: PairRuntimes.For(pair.PairId), Admission: admission, Priority: priority,
            Events: value => log?.Event(value), ReserveDownloadAsync: reserveDownloadAsync, Time: time,
            ReadDeletionEvidenceAsync: readDeletionEvidenceAsync, BeginDownloadAttempt: beginDownloadAttempt,
            CompleteDownloadAttemptAsync: completeDownloadAttemptAsync,
            UploadAdmissionIdentity: uploadAdmissionIdentity, ItemAttempts: itemAttempts,
            Opportunity: registration, ExpectedGeneration: expectedGeneration)
        {
            CaptureFolderCursorAsync = captureFolderCursorAsync,
            ReadFolderChangesAsync = readFolderChangesAsync,
            CompleteRecoveryAttemptAsync = completeRecoveryAttemptAsync
        };
        Context = context;
        Syncer = new Syncer(context, new SyncPorts(new RecoveryExecutionBoundary(() => this),
            new LiveExecutionBoundary(_ => this), PairSafety.SelectedBoundaryFor(this), LocalSide.HashSlots));
    }

    private readonly Func<bool, CancellationToken, Task<(RootBinding Roots, DriveEvidence? Drive)>> _readRoots;

    internal static PairGenerationResources ComposeGeneration(IStateStore store, PairConfig pair,
        IReadOnlyList<PairConfig> scopePairs, long generation, EngineSettings settings, HttpClient http,
        PilotStateAuthority authority, TimeProvider time, Func<string, IAdmission> admissionForAccount,
        Func<(string AccountId, string RouteName, string SourceIdentity), TimeSpan,
            Func<CancellationToken, Task>, IDisposable> scheduleReconnect, string sourceIdentity,
        EngineExternals externals)
    {
        ArgumentNullException.ThrowIfNull(externals);
        ArgumentNullException.ThrowIfNull(externals.Credentials);
        ArgumentNullException.ThrowIfNull(externals.CreateDropboxClient);
        ArgumentNullException.ThrowIfNull(admissionForAccount);
        ArgumentNullException.ThrowIfNull(scheduleReconnect);
        if (string.IsNullOrWhiteSpace(pair.ConnectionId))
            throw new EngineRequestException(TextKey.AccountMissing, (TextArg.Account, pair.ConnectionId));
        var binding = new DropboxConnectionStore(authority).Load(pair.ConnectionId)
            ?? throw new EngineRequestException(TextKey.AccountMissing, (TextArg.Account, pair.ConnectionId));
        if (string.IsNullOrEmpty(binding.AccountId)) throw new InvalidDataException();
        var admission = admissionForAccount(binding.AccountId);
        if (admission is not AdaptiveLimiter limiter) throw new ArgumentException(null, nameof(admissionForAccount));
        var scope = new PathScope(pair, scopePairs, settings.DefaultIgnoreRules);
        var tokens = new RefreshingDropboxAccessTokenSource(http, externals.Credentials, binding);
        var scratch = Path.Combine(authority.ProductRoot, SyncSharkRuntimePaths.ReplacementStagingDirectoryName);
        ValueTask<Stream> Spool(CancellationToken ct) => LocalSide.CreatePageSpoolAsync(scratch, ct);
        var api = new DropboxApi(http, tokens, binding.RootNamespaceId, limiter, Spool,
            settings.Backoff, time, binding.AccountId, sourceIdentity);
        var listing = new DropboxListing(api, pair, scope);
        var dropbox = new DropboxSide(api, pair, scope, settings, listing);
        var sourceLocal = new LocalListing(pair, scope, static (_, _, _, _) => throw new InvalidOperationException());
        var paths = pair.Roots is { } roots ? new UsnPaths(pair, store, sourceLocal, roots.Local) : null;
        var longpollHttp = externals.CreateDropboxClient(
            TimeSpan.FromSeconds(DropboxApiLimits.LongpollMaxSeconds) + DropboxApiLimits.LongpollTimeoutAllowance);
        try
        {
            var longpollApi = new DropboxApi(longpollHttp, tokens, binding.RootNamespaceId, limiter, Spool,
                settings.Backoff, time, binding.AccountId, sourceIdentity);
            var feed = new DropboxFeed(store, pair, generation, scope, longpollApi, listing,
                settings.Backoff, time, scheduleReconnect, sourceIdentity);
            return new PairGenerationResources(pairId: pair.PairId, generation: generation, settings: settings,
                scope: scope, api: api, dropboxListing: listing, sourceLocalListing: sourceLocal, paths: paths,
                feed: feed, admission: admission, accountOrderKey: binding.AccountId, dropbox: dropbox,
                longpollHttp: longpollHttp);
        }
        catch
        {
            longpollHttp.Dispose();
            throw;
        }
    }

    /// <summary>Composes a generation using the caller's product-lifetime scan owner and clock.</summary>
    internal static PairSession Open(IStateStore store, PairConfig pair, EngineSettings settings, HttpClient http,
        bool priority, RunLog? log, PilotStateAuthority authority, EngineHost host, object registration,
        long expectedGeneration, TimeProvider time)
    {
        host.RequireGeneration(registration, pair.PairId, expectedGeneration);
        if (!ReferenceEquals(store, host.Store)) throw new ArgumentException(null, nameof(store));
        var resources = host.BorrowGeneration(registration, pair.PairId, expectedGeneration);
        if (!ReferenceEquals(settings, resources.Settings)) throw new ArgumentException(null, nameof(settings));
        var mutation = host.IsMutationAuthorized(registration);
        var local = CreateLocal(store, pair, resources, authority,
            mutation ? (path, observed, cause, wholeRoot) =>
                host.ReportInventoryFailure(registration, path, observed, cause, wholeRoot, log) :
                static (_, _, _, _) => { }, mutation);
        var recovery = host.Recovery ?? local;
        var ordinary = resources.Dropbox.WithListingContext(() => host.BeginListing(registration, AttemptOwner.Request));
        var prerequisite = resources.Dropbox.WithListingContext(() => host.BeginListing(registration, AttemptOwner.Prerequisite));
        var failures = new ConditionalWeakTable<object, FailureRetryOwner.Episode>();
        async Task<bool> CompleteDownload(RequestAttempt attempt, CancellationToken ct)
        {
            if (attempt.ClaimResult() is not { } completion) return false;
            if (completion.Result == RequestResult.Success)
            {
                failures.Remove(attempt.Operation);
                return false;
            }
            if (completion.Result != RequestResult.TransientFailure ||
                attempt.Owner is not (AttemptOwner.Transfer or AttemptOwner.Recovery) ||
                attempt.Owner == AttemptOwner.Transfer && !completion.NoEffectProven) return false;
            ct.ThrowIfCancellationRequested();
            host.RequireGeneration(registration, pair.PairId, expectedGeneration);
            var episode = failures.GetValue(attempt.Operation,
                static _ => new FailureRetryOwner.Episode(providerWaitCounts: true));
            var wait = episode.Failed(completion.ProviderWait, attempt.ProviderWaitStartedTimestamp, time,
                settings.Backoff, Random.Shared.NextDouble);
            await Task.Delay(wait, time, ct).ConfigureAwait(false);
            host.RequireGeneration(registration, pair.PairId, expectedGeneration);
            return true;
        }
        async ValueTask<IAsyncDisposable> Reserve(string root, string target, long bytes, CancellationToken ct)
        {
            var lease = await DownloadSpace.Shared.ReserveAsync(root, target, bytes, ct).ConfigureAwait(false);
            try
            {
                if (!host.TryHoldGeneration(registration))
                {
                    ct.ThrowIfCancellationRequested();
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
                }
                return lease;
            }
            catch
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        Task<bool> CompleteRecovery(RequestAttempt attempt, CancellationToken ct) =>
            attempt.Owner == AttemptOwner.Recovery
                ? CompleteDownload(attempt, ct) : throw new ArgumentException(null, nameof(attempt));
        return new PairSession(store: store, pair: pair, settings: resources.Settings, scope: resources.Scope,
            local: local, dropbox: ordinary, prerequisiteDropbox: prerequisite, recovery: recovery,
            admission: resources.Admission, priority: priority, log: log, reserveDownloadAsync: Reserve, time: time,
            readRoots: (includeDrive, ct) => ReadRootValuesAsync(store, pair, expectedGeneration,
                local.ReadRootIdentity, resources.Dropbox.ReadRootIdentityAsync,
                recovery.Drive, includeDrive, ct),
            scans: host.Scans, host: host, registration: registration, expectedGeneration: expectedGeneration,
            readDeletionEvidenceAsync: resources.Dropbox.ReadDeletionEvidenceAsync,
            beginDownloadAttempt: operation => new RequestAttempt(operation, AttemptOwner.Transfer),
            completeDownloadAttemptAsync: CompleteDownload,
            uploadAdmissionIdentity: resources.Api.AdmissionIdentity(DropboxProtocol.UploadSessionStart),
            itemAttempts: host.ItemAttemptsFor(registration),
            captureFolderCursorAsync: ct => resources.Dropbox.CaptureFolderCursorAsync(CompleteRecovery, ct),
            readFolderChangesAsync: (cursor, ct) =>
                resources.Dropbox.ReadFolderChangesAsync(cursor, CompleteRecovery, ct),
            completeRecoveryAttemptAsync: CompleteRecovery);
    }

    private static LocalSide CreateLocal(IStateStore store, PairConfig pair, PairGenerationResources resources,
        PilotStateAuthority authority, Action<string, Entry?, TextKey, bool> reportInventoryFailure, bool mutation)
    {
        var pending = new PendingNotes(store, pair.PairId, resources.Settings.StoragePageSize, mutation);
        return new LocalSide(pair: pair, scope: resources.Scope, settings: resources.Settings, pending: pending,
            recoveryRoot: Path.Combine(authority.ProductRoot, SyncSharkRuntimePaths.ReplacementStagingDirectoryName),
            reportInventoryFailure: reportInventoryFailure);
    }

    /// <summary>Reads both configured root identities, optionally including the existing drive observation.</summary>
    internal Task<(RootBinding Roots, DriveEvidence? Drive)> ReadRootsAsync(bool includeDrive,
        CancellationToken ct) => _readRoots(includeDrive, ct);

    private static async Task<(RootBinding Roots, DriveEvidence? Drive)> ReadRootValuesAsync(
        IStateStore store, PairConfig pair, long expectedGeneration, Func<DirectoryIdentity> readLocal,
        Func<CancellationToken, Task<DropboxRootIdentity>> readDropbox, Func<DriveEvidence> readDrive,
        bool includeDrive, CancellationToken ct)
    {
        var dropbox = await PairSafety.ReadRootAsync(store, pair, expectedGeneration, Side.Dropbox, readDropbox, ct)
            .ConfigureAwait(false);
        var local = await PairSafety.ReadRootAsync(store, pair, expectedGeneration, Side.Local,
            _ => Task.FromResult(readLocal()), ct).ConfigureAwait(false);
        var roots = new RootBinding(pair.LocalRoot, pair.DropboxRoot, pair.ConnectionId, local, dropbox);
        ct.ThrowIfCancellationRequested();
        return (roots, includeDrive ? readDrive() : null);
    }

    /// <summary>The caller-owned state store.</summary>
    public IStateStore Store { get; }
    public PairConfig Pair { get; }
    /// <summary>The settings shared by this session.</summary>
    public EngineSettings Settings { get; }
    public PathScope Scope { get; }
    public ISide Local { get; }
    public ISide Dropbox { get; }
    /// <summary>The prerequisite listing view shares the ordinary side's writer and collector.</summary>
    public ISide PrerequisiteDropbox { get; }
    public ILocalRecovery Recovery { get; }
    /// <summary>The shared admission controller.</summary>
    public IAdmission Admission { get; }
    internal bool Priority { get; }
    internal TimeProvider Time { get; }
    internal ScanOwnership Scans { get; }
    internal object Registration { get; }
    internal EngineHost Host { get; }
    internal long ExpectedGeneration { get; }
    internal IItemAttempts ItemAttempts { get; }
    internal bool SessionPaused => Host.IsPaused(Pair.PairId);

    internal static async Task<RootBinding> ReadImportRootsAsync(IStateStore store, PairConfig pair,
        PairGenerationResources resources, PilotStateAuthority authority, CancellationToken ct)
    {
        var local = CreateLocal(store, pair, resources, authority, static (_, _, _, _) => { }, mutation: false);
        var dropbox = await resources.Dropbox.ReadRootIdentityAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new RootBinding(pair.LocalRoot, pair.DropboxRoot, pair.ConnectionId, local.ReadRootIdentity(), dropbox);
    }

    internal static async Task<RootBinding> ReadConfigurationRootsAsync(IStateStore store, PairConfig pair,
        EngineSettings settings, HttpClient http, PilotStateAuthority authority, EngineHost host,
        object transition, long expectedGeneration, CancellationToken ct)
    {
        if (!ReferenceEquals(store, host.Store)) throw new ArgumentException(null, nameof(store));
        host.RequireConfigurationTransition(transition, pair.PairId, expectedGeneration);
        var resources = host.BorrowConfigurationResources(transition, pair.PairId, expectedGeneration);
        var local = CreateLocal(store, pair, resources, authority, static (_, _, _, _) => { }, mutation: false);
        var result = await ReadRootValuesAsync(store, pair, expectedGeneration, local.ReadRootIdentity,
            resources.Dropbox.ReadRootIdentityAsync, local.Drive, includeDrive: false, ct).ConfigureAwait(false);
        host.RequireConfigurationTransition(transition, pair.PairId, expectedGeneration);
        return result.Roots;
    }
    /// <summary>The session's executor shares the pair's process-owned ReplaceTurn across runs.</summary>
    public Syncer Syncer { get; }
    internal ExecutorContext Context { get; }
    /// <summary>The run log; null for a read-only preview.</summary>
    public RunLog? Log { get; }
}
