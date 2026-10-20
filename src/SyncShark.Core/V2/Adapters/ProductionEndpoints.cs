using System.Collections.Concurrent;

namespace Massdex.SyncShark.V2;

/// <summary>One pair's opened endpoints: an inventory reader and a writer per side.</summary>
public sealed record PairEndpoints(IInventorySource InventoryA, IInventorySource InventoryB,
    IEndpointWriter WriterA, IEndpointWriter WriterB);

/// <summary>Opens the real Local/Dropbox endpoints per pair at run time; refusals happen before any effect.</summary>
public sealed partial class ProductionEndpoints : IEndpointProvider, IChangeWatchSource
{
    // Deliberately not called by PrepareAsync/OpenAsync: CLI previews must never register a provider.
    internal ICloudFilesConnection CreateCloudFiles(PairDef pair, WindowsCloudFilesBinding binding,
        SyncDirectoryStorage defaultStorage)
    {
        binding.RequireSame(PairMapping.CloudFilesBinding(pair));
        RequireLocalIdentity(pair.A);
        return new WindowsCloudFilesProvider(binding, new DropboxRangeSource(_dropbox, pair.B, binding),
            _paths.Spool, defaultStorage);
    }

    internal async Task<PilotRemoteEntry?> ObserveStorageRemoteAsync(PairDef pair, string path, CancellationToken ct)
    {
        var client = await _dropbox.OpenAsync(pair.B, ct).ConfigureAwait(false);
        await client.VerifyRootIdentityAsync(ct).ConfigureAwait(false);
        return await client.FindEntryAtPathAsync(path, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Dispatches on root.Provider (B-118): a local root gets a <see cref="LocalChangeWatch"/> over the product-owned
    /// folders; a Dropbox root gets a <see cref="DropboxChangeWatch"/> from one source per root, so its backoff and
    /// signalled cursor survive a watch rebuild. A root that cannot be opened faults the task (the pair is held).
    /// </remarks>
    public async Task<IChangeWatch> WatchAsync(RootDef root, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.Equals(root.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase))
            return await new LocalChangeWatchSource(_paths.ProductOwnedPaths(_paths.RecoveryRoot))
                .WatchAsync(root, ct).ConfigureAwait(false);
        if (!string.Equals(root.Provider, SyncProviders.Dropbox, StringComparison.OrdinalIgnoreCase))
            throw new SyncRunRefusedException("A " + root.Provider + " root cannot be watched in core v2.");
        if (!_dropboxWatches.TryGetValue(root.RootId, out var source))
        {
            var client = await _dropbox.OpenWatchAsync(root, ct).ConfigureAwait(false);
            client.ReadTransientBudget = _settings.DropboxReadTransientBudget;
            source = _dropboxWatches.GetOrAdd(root.RootId,
                _ => new DropboxChangeWatchSource(_store, client, _settings, () => SafetyPoll(), _time));
        }
        return await source.WatchAsync(root, ct).ConfigureAwait(false);
    }

    /// <summary>The safety-poll interval, the Dropbox watch's retry ceiling; continuous sync sets it when it starts.</summary>
    internal Func<TimeSpan> SafetyPoll { get; set; }

    private readonly IStateStore _store;
    private readonly V2Settings _settings;
    private readonly TimeProvider _time;
    private readonly EnginePaths _paths;
    private readonly Func<string?> _installationId;
    private readonly DropboxRootClients _dropbox;
    private readonly LocalGapIo? _gapIo;
    private readonly LocalGapWorker? _gapWorker;

    internal ValueTask StopGapWorkersAsync() => _gapWorker?.DisposeAsync() ?? ValueTask.CompletedTask;
    // Only an explicit continuation may reuse a preview's classification. Ordinary opens always start fresh.
    private readonly ConcurrentDictionary<string, (IInventorySource Inventory, IEndpointWriter Writer, EndpointContinuation? Preview)> _open =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DropboxChangeWatchSource> _dropboxWatches =
        new(StringComparer.Ordinal);

    public ProductionEndpoints(IStateStore store, V2Settings settings,
        TimeProvider time, EnginePaths paths, Func<string?> installationId, DropboxRootClients dropbox)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _installationId = installationId ?? throw new ArgumentNullException(nameof(installationId));
        _dropbox = dropbox ?? throw new ArgumentNullException(nameof(dropbox));
        if (store is ILocalGapStore gaps)
        {
            _gapIo = new LocalGapIo(store, settings, paths.ProductOwnedPaths(paths.RecoveryRoot));
            _gapWorker = new LocalGapWorker(gaps, _gapIo);
        }
        var floor = settings.MinSafetyPoll;
        SafetyPoll = () => floor;
    }

    /// <summary>Opens independent read-only review inventories without replacing active sync endpoints.</summary>
    public async Task<(IInventorySource A, IInventorySource B)> OpenReviewAsync(PairDef pair, CancellationToken ct)
    {
        var a = Read(pair.A, Side.A);
        var b = Read(pair.B, Side.B);
        await Task.WhenAll(a, b);
        return (await a, await b);

        async Task<IInventorySource> Read(RootDef root, Side side)
        {
            var checkedSide = await CheckSideAsync(root, ct).ConfigureAwait(false);
            return checkedSide.Client is { } client
                ? new DropboxInventory(root, side, client, _store, pair.PairId)
                : new LocalInventory(root, side, _store, OwnedInside(root, checkedSide.Recovery!), _settings, _time, pairId: pair.PairId);
        }
    }

    /// <summary>
    /// Opens both sides of <paramref name="pair"/>. Every prerequisite of BOTH sides (folders, root identities,
    /// installation identity, Recovery location, Dropbox connection and credential) is checked before any writer is
    /// opened, so a refusal leaves no control namespace or Recovery folder behind.
    /// </summary>
    public Task<PairEndpoints> OpenAsync(PairDef pair, CancellationToken ct) => OpenAsync(pair, null, ct);

    private async Task<PairEndpoints> OpenAsync(PairDef pair, EndpointContinuation? continuation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pair);
        var a = await CheckSideAsync(pair.A, ct).ConfigureAwait(false);
        var b = await CheckSideAsync(pair.B, ct).ConfigureAwait(false);
        if (continuation is not null && (continuation.PairId != pair.PairId ||
            continuation.RootA != pair.A.RootId || continuation.RootB != pair.B.RootId)) continuation = null;
        var openedA = await OpenSideAsync(pair, pair.A, Side.A, a, continuation, ct).ConfigureAwait(false);
        var openedB = await OpenSideAsync(pair, pair.B, Side.B, b, continuation, ct).ConfigureAwait(false);
        // Consume the preview ownership: even a second apply carrying the old token must open fresh.
        _open[pair.A.RootId] = (openedA.Inventory, openedA.Writer, null);
        _open[pair.B.RootId] = (openedB.Inventory, openedB.Writer, null);
        return new PairEndpoints(openedA.Inventory, openedB.Inventory, openedA.Writer, openedB.Writer);
    }

    /// <summary>
    /// The pair-relative folders the product owns inside the pair's local roots (data folder, spool, Recovery when
    /// set). The run protects them like exclusions on both sides.
    /// </summary>
    public IReadOnlyList<string> ProductOwnedRelative(PairDef pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        var owned = _paths.ProductOwnedPaths(_paths.RecoveryRoot);
        return new[] { pair.A, pair.B }.Where(IsLocal)
            .SelectMany(root => LocalItems.ProductOwnedRelative(root.RootPath, owned))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <inheritdoc />
    EndpointContinuation? IEndpointProvider.CaptureContinuation(PairDef pair)
    {
        // Called under SyncRun's gate immediately after a successful preview, before another run can open roots.
        if (!_open.TryGetValue(pair.A.RootId, out var a) || !_open.TryGetValue(pair.B.RootId, out var b)) return null;
        var preview = new EndpointContinuation(pair.PairId, pair.A.RootId, pair.B.RootId, Guid.NewGuid());
        _open[pair.A.RootId] = (a.Inventory, a.Writer, preview);
        _open[pair.B.RootId] = (b.Inventory, b.Writer, preview);
        return preview;
    }

    /// <inheritdoc />
    public Task PrepareAsync(PairDef pair, CancellationToken ct) => OpenAsync(pair, ct);

    /// <inheritdoc />
    public Task PrepareAsync(PairDef pair, EndpointContinuation? continuation, CancellationToken ct) =>
        OpenAsync(pair, continuation, ct);

    /// <inheritdoc />
    public IInventorySource Inventory(RootDef root) => Opened(root).Inventory;

    /// <inheritdoc />
    public IEndpointWriter Writer(RootDef root) => Opened(root).Writer;

    private (IInventorySource Inventory, IEndpointWriter Writer) Opened(RootDef root) =>
        _open.TryGetValue(root.RootId, out var opened)
            ? (opened.Inventory, opened.Writer)
            : throw new SyncRunRefusedException("The endpoints for this pair were not opened before the run.");

    // A side that passed every check: the local Recovery root and installation, or the opened Dropbox client.
    private sealed record CheckedSide(string? Recovery, string? Installation, DropboxPilotClient? Client, string? RecoveryUnavailable = null);

    // Checks one side without any effect: nothing is created or allocated here.
    private async Task<CheckedSide> CheckSideAsync(RootDef root, CancellationToken ct)
    {
        if (IsLocal(root))
        {
            if (!Directory.Exists(root.RootPath))
                throw new SyncRunRefusedException("The local folder '" + root.RootPath + "' is missing.");
            RequireSeparatedFromData(root);
            RequireLocalIdentity(root);
            var installation = _installationId() ?? throw new SyncRunRefusedException(
                "This installation has no identity yet, so the local folder cannot be bound.");
            try { return new CheckedSide(_paths.RequireRecoveryRoot(), installation, null); }
            catch (SyncRunRefusedException error)
            {
                // This path is never allocated or used for Recovery in degraded mode. Keep
                // missing/invalid configuration separate from authority to change user files.
                var path = _paths.RecoveryRoot is { } configured && Path.IsPathFullyQualified(configured)
                    ? configured : Path.Combine(_paths.ProductRoot, "recovery-unavailable");
                return new CheckedSide(path, installation, null, error.Message);
            }
        }
        if (string.Equals(root.Provider, SyncProviders.Dropbox, StringComparison.OrdinalIgnoreCase))
        {
            var client = await _dropbox.OpenAsync(root, ct).ConfigureAwait(false);
            client.ReadTransientBudget = _settings.DropboxReadTransientBudget;
            return new CheckedSide(null, null, client);
        }
        throw new SyncRunRefusedException("A " + root.Provider + " side is not supported in core v2.");
    }

    // A local root may hold, sit at or sit inside the Recovery folder (its own copies are artifact-named and never
    // inventoried), but never equal, contain or sit inside the data folder (database and spool). The lease guard
    // checks the process-wide data folder; this checks the folder this engine was opened on.
    private void RequireSeparatedFromData(RootDef root)
    {
        try
        {
            PilotPath.RequireSeparatedFromProductState(root.RootPath, _paths.ProductRoot);
        }
        catch (InvalidDataException)
        {
            throw new SyncRunRefusedException("The local folder '" + root.RootPath +
                                              "' overlaps the Sync Shark data folder, which is never synced.");
        }
    }

    private static void RequireLocalIdentity(RootDef root)
    {
        PilotDirectoryIdentity identity;
        try
        {
            var (volume, fileId) = LocalItems.ParseObjectId(root.RootObjectId);
            identity = new PilotDirectoryIdentity(volume, fileId);
        }
        catch (ArgumentException)
        {
            throw new SyncRunRefusedException("The local folder '" + root.RootPath + "' has no valid recorded identity.");
        }
        try
        {
            using var lease = PilotDirectoryLease.Open(root.RootPath, identity);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                                          or InvalidOperationException)
        {
            throw new SyncRunRefusedException("The local folder '" + root.RootPath +
                                              "' is no longer the folder this pair was created with.");
        }
    }

    private async Task<(IInventorySource Inventory, IEndpointWriter Writer)> OpenSideAsync(PairDef pair, RootDef root,
        Side side, CheckedSide checkedSide, EndpointContinuation? continuation, CancellationToken ct)
    {
        if (checkedSide.Client is { } client)
        {
            var dropbox = new DropboxInventory(root, side, client, _store, pair.PairId);
            // Another run on this root replaces the slot and invalidates reuse. Unrelated roots do not.
            if (continuation is not null && _open.TryGetValue(root.RootId, out var previous) &&
                previous.Preview == continuation && previous.Inventory is DropboxInventory earlier)
                dropbox.AdoptClassificationFrom(earlier);
            return (dropbox, new DropboxWriter(root, side, client, _paths.Spool, _settings, PairInclude.IgnoreRules(pair)));
        }
        var recovery = checkedSide.Recovery!;
        var inventory = new LocalInventory(root, side, _store, OwnedInside(root, recovery), _settings, _time, new DefenderHistory(), pair.PairId)
        { GapWorker = _gapWorker, GapIo = _gapIo };
        // Direct downloads use a target-volume partial, with no product-spool demand.
        var writer = await OpenLocalWriterAsync(_store, root, side, pair, checkedSide.Installation!, recovery, ct,
            _settings, _paths.DatabasePath, _time, checkedSide.RecoveryUnavailable).ConfigureAwait(false);
        return (inventory, writer);

    }

    // Shared production composition seam: tests exercise this factory, not a copy of its callback.
    internal static Task<LocalWriter> OpenLocalWriterAsync(IStateStore store, RootDef root, Side side, PairDef pair,
        string installation, string recovery, CancellationToken ct, V2Settings settings,
        string? databasePath = null, TimeProvider? time = null, string? recoveryUnavailable = null) =>
        LocalWriter.OpenAsync(root, side, pair.PairId, installation, recovery, ct, settings,
            databasePath: databasePath, time: time, ignoreRules: PairInclude.IgnoreRules(pair),
            providerNameRefused: item => ProviderRefusedPaths.SourceContains(store, root.RootId, item),
            recoveryUnavailable: recoveryUnavailable,
            verifyCarriedChoice: (operation, item) => store.VerifyCarriedChoice(pair.PairId, operation, item));

    // Only product-owned folders strictly inside the root are excluded from its scan: a root AT or INSIDE Recovery
    // is the user's own choice (backing Recovery up) and is scanned; its own copies stay hidden as artifact names.
    private IReadOnlySet<string> OwnedInside(RootDef root, string recovery) =>
        _paths.ProductOwnedPaths(recovery).Where(path => LocalItems.RelativeTo(root.RootPath, path) is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsLocal(RootDef root) =>
        string.Equals(root.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase);
}