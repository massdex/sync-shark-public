using Massdex.SyncShark;
using Massdex.SyncShark.V2;

/// <summary>
/// Extra native edge checks for a caller-owned, already registered and connected disposable Cloud Files
/// fixture root. Nothing here registers, connects, unregisters, deletes a sync root or touches a real
/// account: hydration bytes come only from the caller-supplied fake range source.
/// Call in order on the same fresh fixture root:
/// <list type="number">
/// <item>CheckLargeFirstRangeReadAsync returns the owned-pointer state observed before hydration.</item>
/// <item>CheckHydratedPointerMutationRefusedAsync, passing that PointerBefore state.</item>
/// <item>CheckFullyHydratedPointerReplacementAsync.</item>
/// </list>
/// Before call 1 the caller must seed its fake range source with
/// <c>source.Replace(LargeEdgeContent(), LargeEdgeRemote())</c> and pass the same source's read counter.
/// </summary>
internal static class FilesOnDemandNativeEdgeChecks
{
    internal const string LargePointerPath = "large-edge-pointer.bin";
    internal const int LargePointerSize = (2 * 1024 * 1024) + 17;
    internal const string InstallationId = "fod-edge-install01";
    // Timestamp-specific so the private control namespace beside the disposable root belongs to this run.
    private static string LocalRootIdFor(string fullRoot) => "edge-local-" + Path.GetFileName(fullRoot);

    private static readonly byte[] LargeContent = BuildLargeContent();

    /// <summary>The exact bytes the caller's fake range source must serve for the large pointer.</summary>
    internal static byte[] LargeEdgeContent() => LargeContent;

    /// <summary>The exact remote identity the caller's fake range source must expose for the large pointer.</summary>
    internal static PilotRemoteEntry LargeEdgeRemote() => new(
        LargePointerPath, PilotEntryKind.File, "id:edge-large", "revision-edge-large",
        LargeContent.Length, DropboxContentHash.Compute(LargeContent),
        new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

    internal sealed record LargeFirstReadObservation(
        ItemState PointerBefore,
        ItemState? ItemAfter,
        PilotDecisionKind? ScannerDecisionAfter,
        WindowsCloudFilesProvider.StubState NativeBefore,
        WindowsCloudFilesProvider.StubState NativeAfter,
        SyncCloudFileAvailability AvailabilityAfter,
        bool NativePartiallyOnDiskBit,
        bool FullyHydrated,
        bool PartiallyHydrated,
        int DownloadsBeforeRead,
        int DownloadsAfterRead);

    private sealed record ScanObservation(ItemState? Item, PilotDecision? Decision);

    /// <summary>
    /// Creates the large pointer, proves it is natively fully dehydrated, reads exactly its first 4 KiB
    /// through Windows, then inspects the real native stub state and a metadata-only scan. A FULL hydration
    /// policy may make the whole object resident; that outcome is reported as fully hydrated and is never
    /// described as partial. Scanning must not invoke the range source.
    /// </summary>
    internal static async Task<LargeFirstReadObservation> CheckLargeFirstRangeReadAsync(
        string root, WindowsCloudFilesProvider provider, PilotDirectoryIdentity rootIdentity,
        Func<int> downloads, string pairId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(downloads);
        var fullRoot = RequireRoot(root);
        if (!provider.IsConnected)
            throw new IOException("The disposable Cloud Files provider must already be connected.");
        var remote = LargeEdgeRemote();
        var full = Path.Combine(fullRoot, remote.RelativePath);
        if (File.Exists(full) || Directory.Exists(full))
            throw new IOException("The edge fixture path already exists; pass a fresh disposable root.");

        provider.CreateNewPlaceholder(remote, rootIdentity);
        var scannedBefore = await ScanAsync(fullRoot, pairId, remote.RelativePath, cancellationToken)
            .ConfigureAwait(false);
        var pointer = scannedBefore.Item
            ?? throw new IOException("The large pointer was omitted from the metadata-only scan.");
        Require(pointer.Kind == ItemKind.File && pointer.Flags.HasFlag(ItemFlags.OwnedPointer),
            "The large pointer was not inventoried as an owned online-only file.");
        Require(pointer.Hash == remote.ContentHash && pointer.Size == remote.Size,
            "The large pointer lost its exact cloud content identity at creation.");

        var (nativeBefore, placeholderBefore) = Inspect(full);
        Require(nativeBefore is { OwnedFullyDehydrated: true } before && before.OnDiskBytes == 0,
            "The fresh large pointer is not natively fully dehydrated. " + Describe(nativeBefore));
        Require(placeholderBefore is not null &&
                placeholderBefore.State.Availability == SyncCloudFileAvailability.OnlineOnly &&
                placeholderBefore.State.IsInSync,
            "The fresh large pointer is not an in-sync online-only placeholder.");

        var downloadsBefore = downloads();
        var first = new byte[4096];
        using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                   FileOptions.SequentialScan))
            stream.ReadExactly(first, 0, first.Length);
        var downloadsAfter = downloads();
        Require(first.AsSpan().SequenceEqual(LargeContent.AsSpan(0, first.Length)),
            "The first 4 KiB read returned different bytes than the exact cloud revision.");
        Require(downloadsAfter > downloadsBefore,
            "The first 4 KiB read did not reach the supplied range source.");

        var scannedAfter = await ScanAsync(fullRoot, pairId, remote.RelativePath, cancellationToken)
            .ConfigureAwait(false);
        var itemAfter = scannedAfter.Item;
        var (nativeAfter, placeholderAfter) = Inspect(full);
        Require(nativeAfter is not null && placeholderAfter is not null,
            "The native placeholder state became unreadable after the first read.");
        Require(downloads() == downloadsAfter, "Metadata-only scanning downloaded the large pointer.");

        var availability = placeholderAfter!.State.Availability;
        var fullyHydrated = nativeAfter!.OnDiskBytes == remote.Size;
        var partiallyHydrated = !fullyHydrated && nativeAfter.OnDiskBytes > 0 &&
            availability == SyncCloudFileAvailability.PartiallyHydrated;
        var partiallyOnDiskBit = (nativeAfter.PlaceholderState & 0x20) != 0;
        if (fullyHydrated)
        {
            // The registered root uses the FULL hydration policy. When Windows made the whole object
            // resident, never label that partial: the scanner must publish ordinary local bytes.
            Require(availability == SyncCloudFileAvailability.AvailableOffline,
                "Windows reported a full resident copy but not an available-offline placeholder.");
            Require(itemAfter is not null && !itemAfter.Flags.HasFlag(ItemFlags.OwnedPointer),
                "A fully hydrated pointer was still inventoried as online-only.");
        }
        else if (availability == SyncCloudFileAvailability.PartiallyHydrated)
        {
            // A metadata-only scan has no durable V2 ownership proof. Depending on its ownership
            // classifier it must either keep the owned-pointer entry or refuse to present the item
            // as ordinary local bytes; neither outcome may claim the partial content is complete.
            Require(itemAfter is not null
                    ? itemAfter.Flags.HasFlag(ItemFlags.OwnedPointer)
                    : scannedAfter.Decision is { Kind: PilotDecisionKind.Blocked or PilotDecisionKind.OnlineOnly },
                "A partially hydrated pointer was presented as ordinary local data.");
        }
        else
        {
            Require(availability == SyncCloudFileAvailability.OnlineOnly,
                "The read left the Cloud Files item in an unexpected availability state: " + availability);
            Require(itemAfter is not null && itemAfter.Flags.HasFlag(ItemFlags.OwnedPointer),
                "An online-only pointer was not inventoried as an owned pointer.");
        }
        if (itemAfter is not null)
        {
            // The physical object identity and logical size must survive hydration. A metadata-only
            // scan legitimately omits the hash for resident bytes, so compare it only when present.
            Require(itemAfter.ObjectId == pointer.ObjectId && itemAfter.Size == pointer.Size,
                "The first read changed the pointer's physical identity or logical size.");
            if (itemAfter.Hash is { } afterHash)
                Require(afterHash == pointer.Hash,
                    "The first read changed the pointer's content fingerprint.");
        }
        return new(pointer, itemAfter, scannedAfter.Decision?.Kind, nativeBefore!, nativeAfter,
            availability, partiallyOnDiskBit, fullyHydrated, partiallyHydrated, downloadsBefore, downloadsAfter);
    }

    /// <summary>
    /// Proves that Move and Delete refuse an owned pointer whose native state is no longer fully dehydrated.
    /// The expected state is the pre-hydration scan state, so the refusal is the planner/executor race. Both
    /// operations must raise ExpectedTargetMismatchException without downloading a byte and without changing
    /// the filesystem.
    /// </summary>
    internal static async Task CheckHydratedPointerMutationRefusedAsync(
        string root, PilotDirectoryIdentity rootIdentity, string pairId, ItemState pointerExpected,
        Func<int> downloads, string recoveryRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pointerExpected);
        ArgumentNullException.ThrowIfNull(downloads);
        var fullRoot = RequireRoot(root);
        if (pointerExpected.Kind != ItemKind.File || !pointerExpected.Flags.HasFlag(ItemFlags.OwnedPointer))
            throw new ArgumentException("The expected target must be an owned online-only file state.",
                nameof(pointerExpected));
        if (!string.Equals(pointerExpected.Path, LargePointerPath, StringComparison.Ordinal))
            throw new ArgumentException("The expected target is not the large edge pointer.",
                nameof(pointerExpected));
        var full = Path.Combine(fullRoot, pointerExpected.Path);
        Require(File.Exists(full), "The large edge pointer is absent before the mutation refusal check.");
        var destination = pointerExpected.Path + ".moved";
        Require(!File.Exists(Path.Combine(fullRoot, destination)),
            "The move destination unexpectedly exists before the refusal check.");
        Directory.CreateDirectory(recoveryRoot);

        var writer = await LocalWriter.OpenAsync(
            new RootDef(LocalRootIdFor(fullRoot), SyncProviders.Local, null, fullRoot,
                SyncLocalEndpointStore.ObjectId(rootIdentity.VolumeSerialNumber, rootIdentity.FileId)),
            Side.A, pairId, InstallationId, recoveryRoot, cancellationToken).ConfigureAwait(false);
        var downloadsBefore = downloads();

        var move = new SyncAction("edge-move-refused", 6, ActionKind.Move, Side.A, destination,
            pointerExpected.Path, null, pointerExpected, false, null);
        await ExpectMismatchAsync(() => writer.MoveAsync(move, cancellationToken),
            "A no-longer-dehydrated owned pointer was moved instead of being refused.")
            .ConfigureAwait(false);
        Require(downloads() == downloadsBefore, "Refusing the pointer move downloaded bytes.");
        Require(File.Exists(full) && !File.Exists(Path.Combine(fullRoot, destination)),
            "Refusing the pointer move changed the filesystem.");

        var delete = new SyncAction("edge-delete-refused", 6, ActionKind.Delete, Side.A,
            pointerExpected.Path, null, null, pointerExpected, false, null);
        await ExpectMismatchAsync(() => writer.DeleteAsync(delete, cancellationToken),
            "A no-longer-dehydrated owned pointer was deleted instead of being refused.")
            .ConfigureAwait(false);
        Require(downloads() == downloadsBefore, "Refusing the pointer delete downloaded bytes.");
        Require(File.Exists(full), "Refusing the pointer delete removed the object.");
        Require(WindowsCloudFilesProvider.TryReadOwnedPlaceholder(full, out _),
            "Refusing the pointer delete lost the owned placeholder identity.");
        // The last writer-using check recreates this if it runs; leave no private sibling behind now.
        RemoveControlNamespace(fullRoot);
    }

    /// <summary>
    /// Forces complete hydration, proves the native state is fully available offline, then replaces the
    /// hydrated object through the ordinary LocalWriter update path. The published object must be an
    /// ordinary local file with the replacement bytes; no range download may occur during the replacement.
    /// </summary>
    internal static async Task<ItemState> CheckFullyHydratedPointerReplacementAsync(
        string root, PilotDirectoryIdentity rootIdentity, string pairId, Func<int> downloads,
        string recoveryRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        var fullRoot = RequireRoot(root);
        var remote = LargeEdgeRemote();
        var full = Path.Combine(fullRoot, remote.RelativePath);
        Require(File.Exists(full), "The large edge pointer is absent before the replacement check.");

        var original = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
        Require(original.Length == remote.Size &&
                DropboxContentHash.Compute(original) == remote.ContentHash,
            "Fully hydrating the pointer returned different bytes than its exact cloud revision.");
        var (native, placeholder) = Inspect(full);
        Require(native is not null && native.OnDiskBytes == remote.Size && placeholder is not null &&
                placeholder.State.Availability == SyncCloudFileAvailability.AvailableOffline,
            "The FULL hydration policy did not make the whole pointer resident; a fully hydrated " +
            "replacement cannot be claimed. Native state: " + Describe(native));
        var hydratedScan = await ScanAsync(fullRoot, pairId, remote.RelativePath, cancellationToken)
            .ConfigureAwait(false);
        var hydrated = hydratedScan.Item;
        Require(hydrated is not null && !hydrated.Flags.HasFlag(ItemFlags.OwnedPointer) &&
                hydrated.Size == remote.Size,
            "The fully resident pointer was still inventoried as an online-only pointer.");
        if (hydrated!.Hash is { } residentHash)
            Require(residentHash == remote.ContentHash,
                "The resident pointer's metadata scan reported a different content fingerprint.");

        Directory.CreateDirectory(recoveryRoot);
        var writer = await LocalWriter.OpenAsync(
            new RootDef(LocalRootIdFor(fullRoot), SyncProviders.Local, null, fullRoot,
                SyncLocalEndpointStore.ObjectId(rootIdentity.VolumeSerialNumber, rootIdentity.FileId)),
            Side.A, pairId, InstallationId, recoveryRoot, cancellationToken).ConfigureAwait(false);
        var replacement = BuildReplacementContent();
        var source = new ItemState(remote.RelativePath, ItemKind.File,
            "id:local:00000001:0000000000000001", null, DropboxContentHash.Compute(replacement),
            replacement.Length, new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero), ItemFlags.None);
        var action = new SyncAction("edge-hydrated-replace", 2, ActionKind.Update, Side.A,
            remote.RelativePath, null, source, hydrated, false, null);

        var downloadsBefore = downloads();
        var result = await writer.PutAsync(action, new MemoryStream(replacement, writable: false),
            cancellationToken).ConfigureAwait(false);
        Require(downloads() == downloadsBefore, "Replacing a fully hydrated pointer downloaded bytes.");
        Require(!WindowsCloudFilesProvider.IsCloudFilesPlaceholder(full) &&
                !WindowsCloudFilesProvider.TryReadOwnedPlaceholder(full, out _),
            "The replacement remained a Cloud Files placeholder instead of an ordinary local file.");
        var published = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
        Require(published.AsSpan().SequenceEqual(replacement.AsSpan()),
            "The hydrated pointer replacement published different bytes.");
        var replacedScan = await ScanAsync(fullRoot, pairId, remote.RelativePath, cancellationToken)
            .ConfigureAwait(false);
        var item = replacedScan.Item;
        Require(item is not null && !item.Flags.HasFlag(ItemFlags.OwnedPointer) &&
                item.Size == replacement.Length && result.Hash == source.Hash,
            "The replaced hydrated pointer is not an ordinary local file with the replacement content.");
        if (item!.Hash is { } itemHash)
            Require(itemHash == source.Hash,
                "The replaced file's metadata scan reported a different content fingerprint.");
        RemoveControlNamespace(fullRoot);
        return result;
    }

    private static async Task<ScanObservation> ScanAsync(string root, string pairId, string relativePath,
        CancellationToken cancellationToken)
    {
        using var lease = PilotDirectoryLease.Open(root);
        var scan = await new PilotLocalScanner { MetadataOnly = true, CloudFilesAware = true }.ScanAsync(
            lease, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cancellationToken,
            contentProvider: SyncProviders.Dropbox, expectedPairId: pairId).ConfigureAwait(false);
        var entry = scan.Entries.Values.FirstOrDefault(item => item.RelativePath == relativePath);
        var decision = scan.Decisions.FirstOrDefault(item => item.RelativePath == relativePath);
        return new(entry is null ? null : LocalItems.FromScan(entry), decision);
    }

    private static (WindowsCloudFilesProvider.StubState? Native, WindowsCloudPlaceholderInfo? Placeholder)
        Inspect(string fullPath)
    {
        using var held = SyncLocalFileAuthority.OpenOnlineOnlyStub(fullPath);
        var native = WindowsCloudFilesProvider.ReadStubState(held.Handle);
        var placeholder = WindowsCloudFilesProvider.TryReadOwnedPlaceholder(held.Handle,
            directory: false, out var info) ? info : null;
        return (native, placeholder);
    }

    private static async Task ExpectMismatchAsync(Func<Task> operation, string message)
    {
        try { await operation().ConfigureAwait(false); }
        catch (ExpectedTargetMismatchException) { return; }
        throw new IOException(message);
    }

    private static string RequireRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
            throw new IOException("The caller did not supply an existing disposable fixture root.");
        return full;
    }

    /// <summary>
    /// The exact private control namespace LocalWriter.OpenAsync creates beside the supplied disposable
    /// root for this run's timestamp-specific root id. It is never inside the registered root.
    /// </summary>
    internal static string ControlNamespacePath(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var control = Path.GetFullPath(SyncControlNamespace.LocalRootFor(fullRoot,
            LocalItems.NamespaceId(LocalRootIdFor(fullRoot))));
        if (!string.Equals(Path.GetDirectoryName(control), Path.GetDirectoryName(fullRoot),
                StringComparison.OrdinalIgnoreCase))
            throw new IOException("The private control namespace is not the fixture root's sibling.");
        return control;
    }

    internal static bool ControlNamespaceExists(string root) => Directory.Exists(ControlNamespacePath(root));

    /// <summary>
    /// Fixture cleanup hook: removes only this check's deterministic private control namespace beside the
    /// disposable root. The path must be the fixture root's sibling, not a reparse point, and carry either
    /// the exact installation/namespace marker or be empty. Anything else is refused, never deleted.
    /// </summary>
    internal static void RemoveControlNamespace(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var control = ControlNamespacePath(fullRoot);
        if (!Directory.Exists(control)) return;
        if ((File.GetAttributes(control) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to remove a reparse-point control namespace: " + control);
        var namespaceId = LocalItems.NamespaceId(LocalRootIdFor(fullRoot));
        var markerPath = Path.Combine(control, SyncControlNamespace.MarkerName);
        if (File.Exists(markerPath))
        {
            if (!File.ReadAllBytes(markerPath).AsSpan().SequenceEqual(
                    SyncControlNamespace.MarkerBytes(InstallationId, namespaceId)))
                throw new IOException(
                    "Refusing to remove a control namespace owned by another installation: " + control);
        }
        else if (Directory.EnumerateFileSystemEntries(control).Any())
        {
            throw new IOException("Refusing to remove an unmarked non-empty control namespace: " + control);
        }
        Directory.Delete(control, recursive: true);
    }

    private static byte[] BuildLargeContent()
    {
        var bytes = new byte[LargePointerSize];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = (byte)((index * 37 + 11) & 0xff);
        return bytes;
    }

    private static byte[] BuildReplacementContent()
    {
        var bytes = new byte[(256 * 1024) + 5];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = (byte)(255 - ((index * 17 + 3) & 0xff));
        return bytes;
    }

    private static string Describe(WindowsCloudFilesProvider.StubState? state) => state is null
        ? "none"
        : "state=0x" + state.PlaceholderState.ToString("x") + " onDisk=" + state.OnDiskBytes +
          " modified=" + state.ModifiedBytes + " inSync=" + state.InSyncState + " fileId=" + state.FileId;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new IOException(message);
    }
}
