using System.Runtime.InteropServices;
using Massdex.SyncShark;
using Massdex.SyncShark.V2;

internal static class FilesOnDemandNativePointerChecks
{
    private const string RenamedPath = "renamed-pointer.bin";
    private const uint FileReadAttributes = 0x00000080;
    private const uint WriteDacAccess = 0x00040000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    /// <summary>
    /// F2 regression: a transient root-registration read failure is an unobserved path, never byte
    /// evidence. The prior owned row must keep its exact path, hash, size and flags; the scan reports
    /// an incomplete path; no rehash debt and no pointer-verify notice may be created; nothing downloads.
    /// </summary>
    internal static async Task VerifyTransientRegistrationAsync(string relativePath, string objectId,
        string pairId, SqliteStateStore store, PairDef pair, Func<int> reads, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pair);
        var before = store.ByObject(pair.A.RootId, objectId)
            ?? throw new IOException("The owned pointer row is absent before the transient-registration check.");
        if (before.Flags != ItemFlags.OwnedPointer || before.Path != relativePath || before.Hash is null)
            throw new IOException("The transient-registration fixture does not start from a clean owned pointer row: " + before);
        var readsBefore = reads();
        var inventory = new LocalInventory(pair.A, Side.A, store,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), V2Settings.Default, TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage,
            pairId: pairId)
        {
            // Simulates a sharing/IO failure while Windows registration data is read.
            ReadRootRegistrationForTest = _ => throw new IOException("Synthetic transient registration read failure.")
        };
        var generation = store.BeginGeneration(pair.A.RootId, "snapshot");
        var snapshot = await inventory.FullAsync(generation, _ => true, new Progress<string>(), cancellationToken)
            .ConfigureAwait(false);
        Require(snapshot.Items.All(item => item.Path != relativePath),
            "A transient registration failure published the pointer as an observation.");
        Require(snapshot.IncompleteSubtrees.Contains(relativePath) || snapshot.IncompleteSubtrees.Contains(""),
            "A transient registration failure did not mark the pointer path incomplete: " +
            string.Join(", ", snapshot.IncompleteSubtrees));
        Require(snapshot.IncludesPath is not null && !snapshot.IncludesPath(relativePath),
            "The unobserved pointer path was not masked from snapshot publication.");
        store.WriteSnapshot(snapshot);
        var stored = store.ByObject(pair.A.RootId, objectId)
            ?? throw new IOException("The durable pointer row disappeared after an unobserved scan.");
        Require(stored.Path == before.Path && stored.Hash == before.Hash && stored.Size == before.Size &&
                stored.Mtime == before.Mtime && stored.Flags == before.Flags,
            "An unobserved scan changed the durable pointer row. Before: " + before + " After: " + stored);
        Require((stored.Flags & ItemFlags.NeedsRehash) == 0,
            "A transient registration failure created rehash verification debt.");
        Require(!store.OpenNotices(pairId).Any(notice => notice.Storage?.Operation == "pointer-verify"),
            "A transient registration failure raised a per-item pointer-verify notice.");
        Require(store.StorageSettlementFor(pair.A.RootId, objectId, pair.B.RootId) is null,
            "An unobserved scan queued a settlement or acknowledgment.");
        Require(reads() == readsBefore, "An unobserved metadata scan downloaded the pointer.");
    }

    /// <summary>
    /// F4 regression: an in-sync-cleared pointer with no ack is repaired by metadata alone. The scan
    /// queues one entity-keyed acknowledgment and the real service re-marks it in sync without a
    /// hydration request or download.
    /// </summary>
    internal static async Task VerifyDirtyPointerSelfHealsAsync(string rootPath, string relativePath, string objectId,
        string pairId, SqliteStateStore store, PairDef pair, Func<int> reads,
        Func<WindowsCloudFilesProvider> createProvider, PilotRemoteEntry current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(createProvider);
        var full = Path.Combine(Path.GetFullPath(rootPath), relativePath);
        var readBeforeClear = WindowsCloudFilesProvider.TryReadOwnedPlaceholder(full, out var beforeClear);
        var beforeNative = DescribeNativeState(full);
        // Clear the native in-sync bit without touching bytes: an ordinary dirty-rename shape.
        using (var handle = WindowsCloudFilesProvider.Native.CreateFile(full,
                   FileReadAttributes | WriteDacAccess, FileShare.None, IntPtr.Zero, OpenExisting,
                   FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero))
        {
            if (handle.IsInvalid)
                throw new IOException("The dirty-pointer fixture could not be opened exclusively.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            var cleared = CfSetInSyncState(handle.DangerousGetHandle(), 0, 0, IntPtr.Zero);
            if (cleared < 0)
                throw new IOException("The dirty-pointer fixture could not clear its in-sync state.",
                    Marshal.GetExceptionForHR(cleared));
        }
        var readsBefore = reads();
        Require(WindowsCloudFilesProvider.TryReadOwnedPlaceholder(full, out var dirty) && dirty is not null &&
                !dirty.State.IsInSync && OwnedPointerProof.IsDehydrated(dirty),
            "The fixture did not produce a dehydrated, in-sync-cleared pointer. " +
            $"Before clear (read={readBeforeClear}): {beforeClear}; {beforeNative}; " +
            $"after clear: {dirty}; {DescribeNativeState(full)}");
        var receipt = store.StoragePointerFor(pair.A.RootId, objectId, pair.B.RootId)?.Remote
            ?? throw new IOException("The dirty pointer has no durable cloud receipt.");
        var inventory = new LocalInventory(pair.A, Side.A, store,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), V2Settings.Default, TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage,
            pairId: pairId);
        var generation = store.BeginGeneration(pair.A.RootId, "snapshot");
        var snapshot = await inventory.FullAsync(generation, _ => true, new Progress<string>(), cancellationToken)
            .ConfigureAwait(false);
        var observed = snapshot.Items.SingleOrDefault(item => item.Path == relativePath);
        Require(observed is not null && observed.Flags == ItemFlags.OwnedPointer && observed.Hash == receipt.ContentHash,
            "A dirty in-sync-cleared pointer was not preserved as an owned observation: " + observed);
        var queued = store.StorageSettlementFor(pair.A.RootId, objectId, pair.B.RootId);
        Require(queued is { Kind: StorageSettlement.AcknowledgeMove },
            "The metadata scan did not queue an entity acknowledgment for the dirty pointer: " + queued);
        Require(queued!.Remote.Id == receipt.Id && queued.Remote.RelativePath == relativePath &&
                queued.Remote.ContentHash == receipt.ContentHash && queued.Remote.Revision == receipt.Revision,
            "The queued acknowledgment does not carry the exact durable receipt at the current path: " + queued!.Remote);
        var stored = store.ByObject(pair.A.RootId, objectId);
        Require(stored is not null && stored.Flags == ItemFlags.OwnedPointer && stored.Hash == receipt.ContentHash,
            "Queueing the acknowledgment changed the durable pointer row: " + stored);
        Require(!store.OpenNotices(pairId).Any(notice => notice.Storage?.Operation == "pointer-verify"),
            "A dirty in-sync-cleared pointer was turned into verification debt.");
        Require(store.StorageHydrationFor(pair.A.RootId, objectId) is null,
            "A dirty pointer was queued for hydration instead of metadata acknowledgment.");
        Require(reads() == readsBefore, "A dirty metadata scan downloaded the pointer.");
        // Drain only this entity's explicit settlement target. A refresh row queued by ordinary
        // snapshot publication stays pending, proving the acknowledgment lane coexists with it.
        var refreshBefore = store.StoragePointerFor(pair.A.RootId, objectId, pair.B.RootId);
        var target = new StorageNotice(pair.A.RootId, objectId, pair.B.RootId, "settlement",
            StorageNotices.ReceiptScope(queued!.Remote.Id, queued!.Remote.Revision, queued!.Remote.ContentHash));
        using (var lifetime = new CloudFilesLifetime((_, _, _) => createProvider(), _ => true, store,
                   (_, _, _) => Task.FromResult<PilotRemoteEntry?>(current)))
        {
            await using (var service = new StorageSettlementService(store, lifetime, target: target))
                await service.DrainAsync(cancellationToken).ConfigureAwait(false);
            Require(store.StorageSettlementFor(pair.A.RootId, objectId, pair.B.RootId) is null,
                "The target acknowledgment was not retired by the real settlement service.");
            Require(WindowsCloudFilesProvider.TryReadOwnedPlaceholder(full, out var healed) && healed is not null &&
                    healed.State.IsInSync && healed.State.Availability == SyncCloudFileAvailability.OnlineOnly &&
                    healed.Identity.RelativePath == relativePath && OwnedPointerProof.IsDehydrated(healed),
                "The real service did not restore in-sync state on the unchanged dehydrated pointer: " + healed);
        }
        Require(reads() == readsBefore, "Acknowledging the dirty pointer downloaded bytes.");
        if (refreshBefore is { JobId: > 0 })
            Require(store.StoragePointerFor(pair.A.RootId, objectId, pair.B.RootId) is { JobId: > 0 },
                "The targeted acknowledgment lane consumed or replaced the unrelated pending refresh.");
        Require(!store.OpenNotices(pairId).Any(notice =>
                notice.Storage?.Operation is "pointer-verify" or "settlement" or "offline"),
            "The self-healed pointer left a storage notice open.");
        if (refreshBefore is { JobId: > 0 }) store.RetireStoragePointer(refreshBefore);
    }

    /// <summary>
    /// F3 regression: a renamed dehydrated pointer, a committed remote Move with a new revision, the
    /// real acknowledgment service, then a native open through the NEW path. The fake range source
    /// rejects a stale requested relative path, so only an acknowledgment that rewrites the fetch
    /// identity can hydrate. The fixture is restored to its original path/revision without downloads.
    /// </summary>
    internal static async Task VerifyRenameAsync(string rootPath, string path, string pairId, ItemState pointer,
        Func<int> reads, PilotRemoteEntry remote, Action<PilotRemoteEntry> replaceRemote,
        SqliteStateStore store, PairDef pair, string registrationId,
        Func<WindowsCloudFilesProvider> createProvider, byte[] content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(replaceRemote);
        ArgumentNullException.ThrowIfNull(createProvider);
        if (pointer.Path != Path.GetFileName(path) || pointer.Flags != ItemFlags.OwnedPointer || pointer.Hash is null)
            throw new ArgumentException("The rename fixture must start from one clean owned pointer row.", nameof(pointer));
        var fullRoot = Path.GetFullPath(rootPath);
        var renamed = Path.Combine(fullRoot, RenamedPath);
        var (volume, id) = LocalItems.ParseObjectId(pointer.ObjectId);
        var movedReceipt = remote with { RelativePath = RenamedPath, Revision = "renamed-move" };
        var readsBefore = reads();
        Exception? failure = null;
        File.Move(path, renamed);
        try
        {
            replaceRemote(movedReceipt);
            // Production ownership classification must preserve the moved entity and its exact hash.
            var inventory = new LocalInventory(pair.A, Side.A, store,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), V2Settings.Default, TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage,
                pairId: pairId);
            var generation = store.BeginGeneration(pair.A.RootId, "snapshot");
            var scan = await inventory.FullAsync(generation, _ => true, new Progress<string>(), cancellationToken)
                .ConfigureAwait(false);
            var observed = scan.Items.SingleOrDefault(item => item.Path == RenamedPath)
                ?? throw new IOException("The renamed pointer was omitted from the production metadata scan: " +
                    string.Join("; ", scan.IncompleteReasons.Select(reason => reason.Key + "=" + reason.Value)));
            Require(observed.ObjectId == pointer.ObjectId && observed.Flags == ItemFlags.OwnedPointer &&
                    observed.Hash == pointer.Hash && observed.Size == pointer.Size && observed.Mtime == pointer.Mtime,
                "Renaming the pointer lost owned identity or its verified content fingerprint: " + observed);
            Require(reads() == readsBefore, "Renaming the pointer fetched file contents.");
            store.WriteSnapshot(scan with { Items = [observed], IncompleteSubtrees = [] });
            // The scan must not have queued the Move acknowledgment; the committed remote Move owns it.
            Require(store.StorageSettlementFor(pair.A.RootId, pointer.ObjectId, pair.B.RootId) is null,
                "The pre-Move metadata scan queued an acknowledgment before the remote Move was committed.");
            var cloud = new ItemState(remote.RelativePath, ItemKind.File, remote.Id!, remote.Revision,
                pointer.Hash, pointer.Size, remote.ModifiedUtc ?? pointer.Mtime, ItemFlags.None);
            var movedCloud = cloud with { Path = RenamedPath, VersionId = movedReceipt.Revision };
            var move = new SyncAction("move-pointer", 9, ActionKind.Move, Side.B, RenamedPath, remote.RelativePath,
                observed, cloud, false, "pointer-object");
            store.RecordIntent(pair.PairId, move);
            store.CommitResult(pair.PairId, generation, move,
                new(move.ActionId, Outcome.Done, observed, movedCloud, null, null));
            var acknowledgment = store.StorageSettlementFor(pair.A.RootId, pointer.ObjectId, pair.B.RootId);
            Require(acknowledgment is { Kind: StorageSettlement.AcknowledgeMove },
                "The committed Move did not queue its typed acknowledgment: " + acknowledgment);
            Require(acknowledgment!.Remote.RelativePath == RenamedPath &&
                    acknowledgment.Remote.Revision == movedReceipt.Revision &&
                    acknowledgment.Remote.Id == remote.Id && acknowledgment.Remote.ContentHash == pointer.Hash,
                "The acknowledgment does not carry the post-Move path and revision: " + acknowledgment!.Remote);
            Require(store.StoragePointerFor(pair.A.RootId, pointer.ObjectId, pair.B.RootId)?.Remote == movedReceipt,
                "The Move did not publish the post-Move receipt durably.");
            var refreshBefore = store.StoragePointerFor(pair.A.RootId, pointer.ObjectId, pair.B.RootId);
            var target = new StorageNotice(pair.A.RootId, pointer.ObjectId, pair.B.RootId, "settlement",
                StorageNotices.ReceiptScope(acknowledgment!.Remote.Id, acknowledgment!.Remote.Revision, acknowledgment!.Remote.ContentHash));
            using (var lifetime = new CloudFilesLifetime((_, _, _) => createProvider(), _ => true, store,
                       (_, _, _) => Task.FromResult<PilotRemoteEntry?>(movedReceipt)))
            {
                await using (var service = new StorageSettlementService(store, lifetime, target: target))
                    await service.DrainAsync(cancellationToken).ConfigureAwait(false);
                Require(store.StorageSettlementFor(pair.A.RootId, pointer.ObjectId, pair.B.RootId) is null,
                    "The real acknowledgment service did not retire the Move job.");
                if (refreshBefore is { JobId: > 0 })
                    Require(store.StoragePointerFor(pair.A.RootId, pointer.ObjectId, pair.B.RootId) is { JobId: > 0 },
                        "The targeted acknowledgment lane consumed an unrelated pending pointer refresh.");
                var connected = lifetime.RequireProvider(pair, cancellationToken);
                Require(WindowsCloudFilesProvider.TryReadOwnedPlaceholder(renamed, out var acknowledged) &&
                        acknowledged is not null && acknowledged.Identity.RelativePath == RenamedPath &&
                        acknowledged.Identity.DropboxRevision == movedReceipt.Revision &&
                        acknowledged.State.IsInSync && OwnedPointerProof.IsDehydrated(acknowledged),
                    "The acknowledgment did not rewrite the fetch identity to the new path and revision: " + acknowledged);
                var beforeOpen = reads();
                var hydratedBytes = await File.ReadAllBytesAsync(renamed, cancellationToken).ConfigureAwait(false);
                Require(hydratedBytes.AsSpan().SequenceEqual(content.AsSpan()),
                    "Opening the renamed pointer returned different bytes than the exact moved revision.");
                Require(reads() > beforeOpen,
                    "Opening the renamed pointer did not fetch through the new path; a stale identity was accepted.");
                var hydrationReads = reads();
                Require(WindowsCloudFilesProvider.TryReadOwnedPlaceholder(renamed, out var hydrated) &&
                        hydrated is not null &&
                        hydrated.State.Availability == SyncCloudFileAvailability.AvailableOffline,
                    "Opening the acknowledged pointer did not hydrate it normally.");
                await connected.ConvertVerifiedSyncedFileAsync(movedReceipt, cancellationToken, true,
                    new PilotDirectoryIdentity(volume, id)).ConfigureAwait(false);
                Require(WindowsCloudFilesProvider.TryReadOwnedPlaceholder(renamed, out var dehydrated) &&
                        dehydrated is not null && dehydrated.State.IsInSync && OwnedPointerProof.IsDehydrated(dehydrated),
                    "The renamed pointer could not be returned to an in-sync dehydrated state.");
                File.Move(renamed, path);
                replaceRemote(remote);
                // The same acknowledgment reverses the fixture: tolerate the new path/revision on the
                // current identity, then write the original path/revision and in-sync bit back.
                connected.AcknowledgeDehydratedMove(remote, new PilotDirectoryIdentity(volume, id), () => { });
                Require(WindowsCloudFilesProvider.TryReadOwnedPlaceholder(path, out var restored) &&
                        restored is not null && restored.Identity.RelativePath == remote.RelativePath &&
                        restored.Identity.DropboxRevision == remote.Revision && restored.State.IsInSync &&
                        OwnedPointerProof.IsDehydrated(restored),
                    "The rename fixture could not be restored to its original identity.");
                Require(reads() == hydrationReads,
                    "Fixture restoration downloaded bytes after the single legitimate hydration.");
            }
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(renamed) && !File.Exists(path)) File.Move(renamed, path);
                replaceRemote(remote);
            }
            catch (Exception restoreError) when (failure is not null)
            {
                Console.WriteLine("FAIL rename fixture file restoration: " + restoreError.Message);
            }
            if (failure is not null && File.Exists(path))
            {
                // Best-effort native recovery after a mid-test failure: dehydrate whatever is at the
                // original path, then let the acknowledgment tolerate the moved path/revision.
                try
                {
                    ForceDehydrate(path);
                    using var recovered = createProvider();
                    recovered.ExpectedRegistrationId = registrationId;
                    recovered.RegisterAndConnect();
                    recovered.AcknowledgeDehydratedMove(remote, new PilotDirectoryIdentity(volume, id), () => { });
                }
                catch (Exception recoveryError)
                {
                    Console.WriteLine("FAIL rename native recovery: " + recoveryError.Message);
                }
            }
            try { RestoreDurableRow(store, pair, pointer, remote); }
            catch (Exception restoreError) when (failure is not null)
            {
                Console.WriteLine("FAIL rename durable restoration: " + restoreError.Message);
            }
        }
    }

    private static void ForceDehydrate(string path)
    {
        using var handle = WindowsCloudFilesProvider.Native.CreateFile(path,
            FileReadAttributes | WriteDacAccess, FileShare.None, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("The rename recovery handle could not be opened.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        var dehydrated = WindowsCloudFilesProvider.Native.CfDehydratePlaceholder(
            handle.DangerousGetHandle(), 0, -1, 0, IntPtr.Zero);
        if (dehydrated < 0)
            throw new IOException("The rename recovery could not dehydrate the pointer.",
                Marshal.GetExceptionForHR(dehydrated));
    }

    // Fixture-only bookkeeping: publish the original owned row and receipt so later checks start clean.
    private static void RestoreDurableRow(SqliteStateStore store, PairDef pair, ItemState pointer, PilotRemoteEntry remote)
    {
        var generation = store.BeginGeneration(pair.A.RootId, "snapshot");
        store.WriteSnapshot(new Snapshot(pair.A.RootId, Side.A, generation, "rename-restore", [pointer], []));
        var pending = store.StorageSettlementFor(pair.A.RootId, pointer.ObjectId, pair.B.RootId);
        if (pending is not null) store.RetireStorageSettlement(pending);
        store.QueueStorageSettlement(pair.A.RootId, pointer.ObjectId, pair.B.RootId, remote);
        var queued = store.StorageSettlementFor(pair.A.RootId, pointer.ObjectId, pair.B.RootId);
        if (queued is not null)
            store.CompleteStorageSettlement(queued, new StorageObserved("OnlineOnly", false, true));
        var refresh = store.StoragePointerFor(pair.A.RootId, pointer.ObjectId, pair.B.RootId);
        if (refresh is not null) store.RetireStoragePointer(refresh);
    }

    private static string DescribeNativeState(string path)
    {
        try
        {
            using var held = WindowsCloudFilesProvider.OpenAttributes(path, writeDac: false, exclusive: false);
            var ok = WindowsCloudFilesProvider.Native.GetFileInformationByHandleEx(
                held.Handle.DangerousGetHandle(), 9, out var tag,
                (uint)Marshal.SizeOf<WindowsCloudFilesProvider.Native.FileAttributeTagInfo>());
            var raw = ok ? WindowsCloudFilesProvider.Native.CfGetPlaceholderStateFromAttributeTag(
                tag.FileAttributes, tag.ReparseTag) : uint.MaxValue;
            var mode = "unavailable";
            try { mode = $"process={RtlQueryProcessPlaceholderCompatibilityMode()},thread={RtlQueryThreadPlaceholderCompatibilityMode()}"; }
            catch (EntryPointNotFoundException) { }
            return $"tagRead={ok},attrs=0x{tag.FileAttributes:x},tag=0x{tag.ReparseTag:x},raw=0x{raw:x},visibility={mode}";
        }
        catch (Exception error) { return "native diagnostic unavailable: " + error.Message; }
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern sbyte RtlQueryProcessPlaceholderCompatibilityMode();

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern sbyte RtlQueryThreadPlaceholderCompatibilityMode();

    [DllImport("cldapi.dll", ExactSpelling = true)]
    private static extern int CfSetInSyncState(IntPtr fileHandle, uint inSyncState, uint flags, IntPtr inSyncUsn);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new IOException(message);
    }
}
