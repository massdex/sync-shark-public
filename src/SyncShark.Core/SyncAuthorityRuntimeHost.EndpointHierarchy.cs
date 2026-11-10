namespace Massdex.SyncShark;

// W2-C: the ONE generic read-only endpoint hierarchy reader.
//
// It is driven entirely by the pair's canonical ordered endpoint context, so there is no mechanism per
// provider combination and no provider-pair branch. Both ordered endpoints are resolved and validated
// BEFORE any I/O; each side is then read through its OWN identity: a local side uses its own bound local
// path, held volume and file identity, and a cloud side uses its own retained record and account through
// the existing ResolveCloudSide / CreateCloudEndpointAdapter factory plus the standard
// VerifyRootIdentityAsync / ListEntriesAsync contract. The pair's legacy provider fields are never
// substituted for the other side, and the first ordered endpoint is never assumed to be the physical
// local folder: the physical local and remote ports come from the context's CompatibilityPort.
//
// This path is strictly read-only. It never prepares a pair, registers a candidate cloud root, allocates
// recovery, plans, runs or syncs, and it opens no local root for a pair that has no local endpoint. A
// genuine document-less legacy pair keeps the existing validated compatibility client; an explicit
// malformed or incomplete document is refused by the context capture before any I/O and never falls back
// to the legacy fields.

public sealed partial class SyncAuthorityRuntimeHost
{
    /// <summary>The result of reading ONE ordered endpoint, kept side-bound until the shared merge.</summary>
    private sealed record EndpointHierarchyRead(
        SyncEndpointContextSide Endpoint,
        bool IsLocal,
        PilotLocalScan? LocalScan,
        IReadOnlyList<PilotRemoteEntry> CloudEntries,
        IReadOnlySet<string> OnlineOnly,
        bool Available,
        bool Verified,
        string? Message,
        int? RootFiles,
        bool Definitive = false);

    /// <summary>One ordered endpoint's entries as the shared merge reads them, without a provider assumption.</summary>
    private sealed record EndpointHierarchyEntry(string RelativePath, PilotEntryKind Kind, bool IsCloud,
        string? CloudId, PilotLocalEntry? Local);

    private async Task<SyncFolderSelectionTree> LoadEndpointHierarchyTreeCoreAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        var pair = (await LoadPairsAsync(cancellationToken)).Pairs.SingleOrDefault(item =>
                       string.Equals(item.Id, pairId, StringComparison.Ordinal))
                   ?? throw new InvalidDataException("The requested sync pair is not retained.");
        // Capture and validate the complete ordered pair BEFORE any I/O. An explicit malformed or
        // incomplete endpoint document is refused here and can never fall back to the legacy fields.
        var expected = SyncEndpointContext.Capture(pair);
        var leftRead = await ReadEndpointHierarchyAsync(pair, expected.Left, cancellationToken);
        var rightRead = await ReadEndpointHierarchyAsync(pair, expected.Right, cancellationToken);
        // EL1 revalidation: the pair and BOTH ordered identities must still be exactly the context this
        // read started from, and each cloud side's retained account must still be the same account. A pair
        // or account rebind during the awaited reads therefore refuses instead of publishing mixed-
        // generation rows. Cancellation was already propagated as UNKNOWN by the reads.
        var current = (await LoadPairsAsync(cancellationToken)).Pairs.SingleOrDefault(item =>
            string.Equals(item.Id, pairId, StringComparison.Ordinal));
        if (current is null)
            throw new InvalidOperationException("The folder pair changed while its hierarchy was read.");
        RequireSameOrderedEndpointContext(SyncEndpointContext.Capture(current), expected);
        var typedAuthority = pair.EndpointDocument is not null;
        RequireSameEndpointAccount(typedAuthority, leftRead);
        RequireSameEndpointAccount(typedAuthority, rightRead);

        var leftIsLocalPort = string.Equals(expected.Left.CompatibilityPort, SyncAuthorityCloudSides.LogicalLeft,
            StringComparison.Ordinal);
        var localPortRead = leftIsLocalPort ? leftRead : rightRead;
        var remotePortRead = leftIsLocalPort ? rightRead : leftRead;

        var localEntries = EntriesOf(localPortRead);
        var remoteEntries = EntriesOf(remotePortRead);
        var localFolders = localEntries.Values.Where(item => item.Kind == PilotEntryKind.Directory)
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        var remoteFolders = remoteEntries.Values.Where(item => item.Kind == PilotEntryKind.Directory)
            .ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        // R1: online-only is OBSERVED local placeholder residency ONLY. It is never inferred from a
        // remote-only entry, so a cloud-only item stays only on its own endpoint and is never counted as a
        // placeholder claim. Missing local data is not evidence of a placeholder, and selective-sync
        // exclusions are not placeholders either.
        var onlineOnly = localPortRead.IsLocal ? localPortRead.OnlineOnly : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var paths = localFolders.Keys.Concat(remoteFolders.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Count(character => character == '/'))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var folderPaths = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rollups = paths.ToDictionary(path => path, _ => new FolderRollupBuilder(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var folderPath in paths)
        {
            var included = SyncSelectionPolicy.IsIncluded(folderPath, pair, isDirectory: true);
            foreach (var ancestor in ExistingFolderAncestors(folderPath, folderPaths))
            {
                rollups[ancestor].FolderCount++;
                if (included) rollups[ancestor].IncludedFolderCount++;
            }
        }

        var localRootFiles = localPortRead.RootFiles;
        var dropboxRootFiles = remotePortRead.RootFiles;
        var pairRollup = new FolderRollupBuilder();
        foreach (var itemPath in localEntries.Keys.Concat(remoteEntries.Keys).Concat(onlineOnly)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var localPresent = localEntries.ContainsKey(itemPath);
            var remotePresent = remoteEntries.ContainsKey(itemPath);
            var isOnlineOnly = localPortRead.IsLocal && onlineOnly.Contains(itemPath);
            var localOnly = localPresent && !remotePresent;
            var mixed = localPresent && remotePresent && !isOnlineOnly;
            var excluded = !SyncSelectionPolicy.IsIncluded(itemPath, pair,
                (localEntries.GetValueOrDefault(itemPath)?.Kind ?? remoteEntries.GetValueOrDefault(itemPath)?.Kind) == PilotEntryKind.Directory);
            pairRollup.AddItem(localOnly, isOnlineOnly, mixed, excluded);
            foreach (var ancestor in ExistingFolderAncestors(itemPath, folderPaths))
                rollups[ancestor].AddItem(localOnly, isOnlineOnly, mixed, excluded);
        }
        foreach (var state in await _authority.LoadPairPathStatesAsync(pair.Id, cancellationToken))
        {
            pairRollup.AddState(state.Pending, state.Error);
            foreach (var ancestor in ExistingFolderAncestors(state.RelativePath, folderPaths))
                rollups[ancestor].AddState(state.Pending, state.Error);
        }

        var nodes = paths.Select(path =>
        {
            var rollup = rollups[path];
            bool? checkState = rollup.IncludedFolderCount == 0
                ? false
                : rollup.IncludedFolderCount == rollup.FolderCount
                    ? true
                    : null;
            var localPresent = localFolders.ContainsKey(path);
            var remotePresent = remoteFolders.ContainsKey(path);
            var presence = !localPortRead.Available && remotePresent
                ? SyncFolderPresence.LocalUnloaded
                : !remotePortRead.Available && localPresent
                    ? SyncFolderPresence.DropboxUnloaded
                    : onlineOnly.Contains(path) && remotePresent
                        ? SyncFolderPresence.OnlineOnly
                        : localPresent && remotePresent
                            ? SyncFolderPresence.Mixed
                            : remotePresent
                                ? SyncFolderPresence.CloudOnly
                                : SyncFolderPresence.LocalOnly;
            localFolders.TryGetValue(path, out var localFolder);
            remoteFolders.TryGetValue(path, out var remoteFolder);
            return new SyncFolderSelectionNode(path, Path.GetFileName(path.Replace('/',
                    Path.DirectorySeparatorChar)), path.Count(character => character == '/') + 1,
                checkState, presence, rollup.FolderCount - 1,
                remoteFolder?.CloudId is not null
                    ? "dropbox:" + remoteFolder.CloudId
                    : localFolder?.Local is { VolumeSerialNumber: not null, FileId: not null } localEntry
                        ? LocalSelectionIdentity(localEntry)
                        : null,
                rollup.ToRollup(),
                localFolder?.Local is { VolumeSerialNumber: not null, FileId: not null } localIdentityEntry
                    ? LocalSelectionIdentity(localIdentityEntry)
                    : null,
                remoteFolder?.CloudId);
        }).ToArray();
        return new SyncFolderSelectionTree(pair.Id, nodes,
            nodes.Count(item => item.Presence == SyncFolderPresence.CloudOnly),
            nodes.Count(item => item.Presence == SyncFolderPresence.LocalOnly),
            nodes.Count(item => item.Presence == SyncFolderPresence.Mixed),
            nodes.Count(item => item.Presence == SyncFolderPresence.OnlineOnly), pairRollup.ToRollup(),
            localPortRead.Available, localPortRead.Message, remotePortRead.Available, remotePortRead.Message,
            localRootFiles, dropboxRootFiles, expected, SideFacts(leftRead), SideFacts(rightRead));
    }

    /// <summary>One ordered endpoint's side-bound verified facts, exactly as this read established them.</summary>
    private static SyncEndpointHierarchySide SideFacts(EndpointHierarchyRead read) =>
        new(read.Endpoint, read.Available, read.Verified, read.Message, read.RootFiles, read.Definitive);

    private static IReadOnlyDictionary<string, EndpointHierarchyEntry> EntriesOf(EndpointHierarchyRead read) =>
        read.IsLocal
            ? LocalEntryArray(read).ToDictionary(item => item.RelativePath,
                item => new EndpointHierarchyEntry(item.RelativePath, item.Kind, false, null, item),
                StringComparer.OrdinalIgnoreCase)
            : read.CloudEntries.ToDictionary(item => item.RelativePath,
                item => new EndpointHierarchyEntry(item.RelativePath, item.Kind, true, item.Id, null),
                StringComparer.OrdinalIgnoreCase);

    private static PilotLocalEntry[] LocalEntryArray(EndpointHierarchyRead read) =>
        read.LocalScan is null ? [] : read.LocalScan.Entries.Values.ToArray();

    /// <summary>
    /// Reads ONE resolved endpoint through its own identity. A local endpoint opens only its own bound
    /// local path with its own held volume and file identity; a cloud endpoint uses only its own retained
    /// record and account. Cancellation and timeout propagate as unknown rather than becoming a missing
    /// side, and an ordinary recoverable per-side failure keeps the other side's successfully inspected
    /// rows while reporting this side truthfully.
    /// </summary>
    private async Task<EndpointHierarchyRead> ReadEndpointHierarchyAsync(SyncPair pair,
        SyncEndpointContextSide endpoint, CancellationToken cancellationToken)
    {
        if (endpoint.IsLocal)
        {
            // R2 stage 1: ROOT-OPEN binding validation. The lease's own native evidence decides whether the
            // root is positively missing, positively replaced, merely inaccessible or simply unknown.
            PilotDirectoryLease opened;
            try
            {
                opened = PilotDirectoryLease.Open(endpoint.LocalPath!, new PilotDirectoryIdentity(
                    endpoint.LocalVolumeSerialNumber ?? 0, endpoint.LocalRootFileId ?? 0));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverableHierarchyFailure(exception))
            {
                var (rootMessage, rootDefinitive) = LocalRootFailure(endpoint.LocalPath!, exception);
                return Failed(endpoint, rootMessage, rootDefinitive);
            }
            // R2 stage 2: DESCENDANT SCAN. A failure here is UNAVAILABLE or UNKNOWN; it never claims the
            // folder was replaced and never carries a replacement instruction.
            try
            {
                using var root = opened;
                var scan = await new PilotLocalScanner().ScanAsync(root, cancellationToken: cancellationToken,
                    contentProvider: pair.Provider, expectedPairId: pair.Id);
                var onlineOnly = scan.Entries.Values
                    .Where(item => item.CloudAvailability == SyncCloudFileAvailability.OnlineOnly)
                    .Select(item => item.RelativePath)
                    .Concat(scan.Decisions.Where(item => item.Kind == PilotDecisionKind.OnlineOnly)
                        .Select(item => item.RelativePath))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var rootFiles = scan.Entries.Values.Count(item => item.Kind == PilotEntryKind.File &&
                                                                  !item.RelativePath.Contains('/'));
                return new EndpointHierarchyRead(endpoint, true, scan, [], onlineOnly, true, true, null, rootFiles);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverableHierarchyFailure(exception))
            {
                return Failed(endpoint, LocalScanFailure(endpoint.LocalPath!, exception), false);
            }
        }

        try
        {
            var store = pair.EndpointDocument is null
                ? await LegacyCloudStoreAsync(pair, endpoint, cancellationToken)
                : ResolveCloudSide(new SyncEndpointRecord(endpoint.Kind, Provider: endpoint.Provider,
                    ConnectionId: endpoint.ConnectionId, NamespaceId: endpoint.NamespaceId,
                    FolderId: endpoint.FolderId, FolderPath: endpoint.FolderPath)).Adapter;
            // ReadRootHierarchyAsync is root and ancestor identity evidence, never a descendant listing;
            // this read uses the adapter's own verified root identity plus the descendant listing contract.
            await store.VerifyRootIdentityAsync(cancellationToken);
            var entries = await store.ListEntriesAsync(cancellationToken);
            await store.VerifyRootIdentityAsync(cancellationToken);
            var rootFiles = entries.Count(item => item.Kind == PilotEntryKind.File && !item.RelativePath.Contains('/'));
            return new EndpointHierarchyRead(endpoint, false, null, entries, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                true, true, null, rootFiles);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableHierarchyFailure(exception))
        {
            var (message, definitive) = EndpointHierarchyFailureMessage(endpoint, exception);
            return Failed(endpoint, message, definitive);
        }
    }

    /// <summary>
    /// A genuine document-less legacy pair keeps the existing validated compatibility client for its single
    /// cloud endpoint: the saved account's own factory, never a default or currently selected account.
    /// </summary>
    private async Task<ISyncRemoteStore> LegacyCloudStoreAsync(SyncPair pair, SyncEndpointContextSide endpoint,
        CancellationToken cancellationToken)
    {
        if (endpoint.Kind == SyncEndpointKind.GoogleDrive) return CreateGoogleDriveClient(pair);
        var connection = await RequireConnectionAsync(cancellationToken, pair.ConnectionId);
        return remoteFactory?.Invoke(pair, connection) ?? CreateDropboxClient(pair, connection);
    }

    private static EndpointHierarchyRead Failed(SyncEndpointContextSide endpoint, string message,
        bool definitive = false) =>
        new(endpoint, endpoint.IsLocal, null, [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false, message, null, definitive);

    /// <summary>
    /// EL1-style account revalidation for one cloud endpoint: its retained account must still resolve to the
    /// same retained provider-account identity. This performs no provider call and no mutation.
    /// </summary>
    private void RequireSameEndpointAccount(bool typedAuthority, EndpointHierarchyRead read)
    {
        // An explicit pair's cloud endpoint carries its own typed namespace and is revalidated against the
        // retained account; a genuine document-less legacy pair keeps its single validated compatibility
        // client and its own post-listing root-identity revalidation, so it is not re-resolved here.
        if (!typedAuthority || read.IsLocal || !read.Available) return;
        _ = ResolveCloudSide(new SyncEndpointRecord(read.Endpoint.Kind, Provider: read.Endpoint.Provider,
            ConnectionId: read.Endpoint.ConnectionId, NamespaceId: read.Endpoint.NamespaceId,
            FolderId: read.Endpoint.FolderId, FolderPath: read.Endpoint.FolderPath));
    }

    private static bool IsRecoverableHierarchyFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or
        HttpRequestException or NotSupportedException;

    /// <summary>
    /// R2: ROOT-OPEN binding validation, decided from the lease's TYPED native evidence and never from
    /// parsed message text. The lease wraps the Win32 error as the inner exception, so a positively missing
    /// root is proven by FileNotFound/DirectoryNotFound, an access problem by UnauthorizedAccessException,
    /// and a replaced identity by the lease's own identity-refusal IOException, which carries no native
    /// inner error. Anything else stays UNKNOWN: unavailable, with NO replacement instruction, because an
    /// unclassified IO or transient failure is not proof that the folder changed.
    /// </summary>
    private static (string Message, bool Definitive) LocalRootFailure(string localPath, Exception exception)
    {
        var native = exception.InnerException;
        if (exception is FileNotFoundException or DirectoryNotFoundException ||
            native is FileNotFoundException or DirectoryNotFoundException)
            return ($"Local folder unavailable: {localPath}. Choose another folder to repair this pair.", true);
        if (exception is UnauthorizedAccessException || native is UnauthorizedAccessException)
            return ($"Sync Shark cannot access the local folder: {localPath}. Check its permissions or choose another folder.", false);
        // A plain IOException carries NO unique meaning here: OpenCore throws that same shape for an unsafe
        // or non-directory root, a path mismatch and a non-NTFS volume, so it is never classified as a
        // replacement. It stays UNKNOWN with neutral wording and no replacement instruction.
        return ($"The bound folder at {localPath} could not be verified; its recorded identity has been judged neither missing nor replaced. Refresh to try again.", false);
    }

    /// <summary>
    /// R2: a DESCENDANT SCAN failure is UNAVAILABLE or UNKNOWN only. It never claims a replaced identity and
    /// never instructs a replacement.
    /// </summary>
    private static string LocalScanFailure(string localPath, Exception exception) =>
        exception is UnauthorizedAccessException || exception.InnerException is UnauthorizedAccessException
            ? $"Sync Shark cannot read inside the local folder: {localPath}. Check its permissions and refresh."
            : $"The local folder {localPath} could not be read just now; it is reported unavailable rather than missing. Refresh to try again.";

    /// <summary>
    /// W2-C: provider-neutral, endpoint-resolved failure wording. It classifies by exception TYPE, never by
    /// matching a provider name inside a message, and it keeps a definitive identity change distinct from an
    /// account problem and from a transient network failure. It performs no repair, rebind or provider
    /// mutation, and it adds no new error framework.
    /// </summary>
    private static (string Message, bool Definitive) EndpointHierarchyFailureMessage(
        SyncEndpointContextSide endpoint, Exception exception)
    {
        var label = SyncEndpointPresentation.SectionTitle(endpoint.Side, endpoint.Kind);
        return exception switch
        {
            GoogleDriveBlockedException =>
                ($"{label} account is not connected. Reconnect that exact account, then refresh.", true),
            HttpRequestException =>
                ($"{label} could not be reached right now. Check the network connection and refresh again.", false),
            InvalidDataException =>
                ($"{label} folder moved, changed identity, or no longer exists. Choose the correct folder to repair this pair.", true),
            InvalidOperationException =>
                ($"{label} account is not available. Reconnect that exact account, then refresh.", true),
            NotSupportedException =>
                ($"{label} uses a provider this build does not support; the read was refused.", true),
            _ => ($"{label} could not be loaded. Check the connection or refresh again.", false)
        };
    }
}
