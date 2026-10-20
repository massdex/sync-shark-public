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
    private readonly ConcurrentDictionary<string, (IInventorySource Inventory, IEndpointWriter Writer)> _open =
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
        var floor = settings.MinSafetyPoll;
        SafetyPoll = () => floor;
    }

    /// <summary>
    /// Opens both sides of <paramref name="pair"/>. Every prerequisite of BOTH sides (folders, root identities,
    /// installation identity, Recovery location, Dropbox connection and credential) is checked before any writer is
    /// opened, so a refusal leaves no control namespace or Recovery folder behind.
    /// </summary>
    public async Task<PairEndpoints> OpenAsync(PairDef pair, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pair);
        var a = await CheckSideAsync(pair.A, ct).ConfigureAwait(false);
        var b = await CheckSideAsync(pair.B, ct).ConfigureAwait(false);
        var openedA = await OpenSideAsync(pair, pair.A, Side.A, a, ct).ConfigureAwait(false);
        var openedB = await OpenSideAsync(pair, pair.B, Side.B, b, ct).ConfigureAwait(false);
        _open[pair.A.RootId] = openedA;
        _open[pair.B.RootId] = openedB;
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
    public Task PrepareAsync(PairDef pair, CancellationToken ct) => OpenAsync(pair, ct);

    /// <inheritdoc />
    public IInventorySource Inventory(RootDef root) => Opened(root).Inventory;

    /// <inheritdoc />
    public IEndpointWriter Writer(RootDef root) => Opened(root).Writer;

    private (IInventorySource Inventory, IEndpointWriter Writer) Opened(RootDef root) =>
        _open.TryGetValue(root.RootId, out var opened)
            ? opened
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
        Side side, CheckedSide checkedSide, CancellationToken ct)
    {
        if (checkedSide.Client is { } client)
        {
            var dropbox = new DropboxInventory(root, side, client, _store, pair.PairId);
            // A reopen within the run (apply reopens after its preview) keeps the classification already read.
            if (_open.TryGetValue(root.RootId, out var previous) && previous.Inventory is DropboxInventory earlier)
                dropbox.AdoptClassificationFrom(earlier);
            return (dropbox, new DropboxWriter(root, side, client, _paths.Spool, _settings, PairInclude.IgnoreRules(pair)));
        }
        var recovery = checkedSide.Recovery!;
        var inventory = new LocalInventory(root, side, _store, OwnedInside(root, recovery), _settings, _time);
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
            recoveryUnavailable: recoveryUnavailable);

    // Only product-owned folders strictly inside the root are excluded from its scan: a root AT or INSIDE Recovery
    // is the user's own choice (backing Recovery up) and is scanned; its own copies stay hidden as artifact names.
    private IReadOnlySet<string> OwnedInside(RootDef root, string recovery) =>
        _paths.ProductOwnedPaths(recovery).Where(path => LocalItems.RelativeTo(root.RootPath, path) is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsLocal(RootDef root) =>
        string.Equals(root.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase);
}