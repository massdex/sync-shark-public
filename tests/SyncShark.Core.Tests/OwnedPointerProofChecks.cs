using Massdex.SyncShark;
using Massdex.SyncShark.V2;

internal static class OwnedPointerProofChecks
{
    internal static void Run()
    {
        CheckNativeResidencyDecoder();
        const string pair = "pair-a";
        const string token = "11111111-1111-4111-8111-111111111111";
        var remote = new PilotRemoteEntry("old/file.txt", PilotEntryKind.File, "remote-file", "revision-1",
            10, "hash", DateTimeOffset.UnixEpoch);
        var identity = WindowsCloudFileIdentity.FromRemote(pair, remote);
        var registration = new CloudFilesRootRegistration(7,
            WindowsCloudFileIdentity.Root(pair) with { V2OwnershipToken = token });
        var state = new SyncCloudFileState("new/file.txt", SyncCloudFileAvailability.OnlineOnly,
            false, false, 10, 0, remote.Id, remote.Revision, remote.ContentHash);
        var native = new WindowsCloudPlaceholderInfo(identity, state, 9, 7, false, 0x31);
        var stored = new ItemState(remote.RelativePath, ItemKind.File,
            SyncLocalEndpointStore.ObjectId(3, 9), null, remote.ContentHash, 10,
            DateTimeOffset.UnixEpoch, ItemFlags.OwnedPointer);
        OwnedPointerObservation Classify(WindowsCloudPlaceholderInfo value, bool allow = true,
            ItemState? row = null, PilotRemoteEntry? receipt = null) => OwnedPointerProof.Classify(value,
                token, registration, pair, SyncProviders.Dropbox, 3, row ?? stored, receipt ?? remote,
                10, allowDirtyRename: allow);
        Require(Classify(native) == OwnedPointerObservation.Pointer,
            "same entity, receipt, size and empty data ranges admit a renamed dirty pointer");
        var disguised = native with { RawPlaceholderState = 0, HasStandardInfo = true };
        Require(Classify(disguised) == OwnedPointerObservation.Pointer,
            "successful native standard info and exact entity receipt survive hidden attribute/tag bits");
        Require(Classify(disguised with { HasStandardInfo = false }) == OwnedPointerObservation.NeedsVerification,
            "missing attribute bits without authoritative native metadata cannot establish dehydration");
        Require(Classify(disguised with { ModifiedDataSize = 1 }) == OwnedPointerObservation.NeedsVerification,
            "native modified-byte evidence still blocks disguised pointer admission");
        Require(Classify(disguised with { FileId = 11 }) == OwnedPointerObservation.Foreign,
            "native metadata success never replaces entity ownership proof");
        Require(Classify(native, allow: false) == OwnedPointerObservation.NotObserved,
            "dirty metadata without acknowledgment is not evidence that bytes changed");
        Require(OwnedPointerProof.Classify(native, token, null, pair, SyncProviders.Dropbox, 3,
            stored, remote, 10, true) == OwnedPointerObservation.NotObserved,
            "unreadable registration preserves the previous classification without verification debt");
        Require(Classify(native, row: stored with { Flags = ItemFlags.NeedsRehash | ItemFlags.Unreadable, Hash = null }) ==
            OwnedPointerObservation.Pointer, "journal/cache flags cannot invalidate an exact entity receipt");
        Require(OwnedPointerProof.Classify(native, token, registration, pair, SyncProviders.Dropbox, 3,
            stored, null, 10, true) == OwnedPointerObservation.NotObserved,
            "conversion before receipt commit is not local byte evidence");
        var unavailable = native with { RawPlaceholderState = uint.MaxValue, HasStandardInfo = true,
            State = state with { Availability = SyncCloudFileAvailability.NotManaged, OnDiskBytes = -1 } };
        Require(Classify(unavailable) == OwnedPointerObservation.NotObserved,
            "contradictory residency retains owned identity but does not create verification debt");
        var unregisterRefused = false;
        try { WindowsCloudFilesProvider.RequireObservedResidencyForUnregistration(unavailable); }
        catch (IOException) { unregisterRefused = true; }
        Require(unregisterRefused, "unregistration retains an owned file whose residency could not be observed");
        Require(Classify(native with { RawPlaceholderState = uint.MaxValue }) == OwnedPointerObservation.NotObserved,
            "unreadable native state does not create verification debt");
        if (OperatingSystem.IsWindows())
            Require(LocalInventory.RegistrationPath(@"E:") == LocalInventory.RegistrationPath(@"E:\"),
                "trimmed drive-root registration is not resolved against a drive current directory");
        Require(Classify(native with { State = state with { IsInSync = true } }, allow: false) == OwnedPointerObservation.Pointer,
            "unchanged in-sync owned pointer does not need rename acknowledgment");
        Require(Classify(native with { FileId = 11 }) == OwnedPointerObservation.Foreign,
            "file-id mismatch is foreign, never an owned pointer");
        Require(Classify(native with { SyncRootFileId = 11 }) == OwnedPointerObservation.Foreign,
            "root file-id mismatch is foreign");
        Require(OwnedPointerProof.Classify(native, "22222222-2222-4222-8222-222222222222", registration,
            pair, SyncProviders.Dropbox, 3, stored, remote, 10, true) == OwnedPointerObservation.Foreign,
            "same root path with a different registration GUID is foreign");
        Require(OwnedPointerProof.Classify(native, token, registration, pair, SyncProviders.Dropbox,
            3, null, remote, 10, true) == OwnedPointerObservation.Foreign,
            "serialized identity alone cannot invent a stored owned entity");
        foreach (var changed in new[] {
            native with { Identity = identity with { DropboxId = "other" } },
            native with { Identity = identity with { DropboxRevision = "revision-2" } },
            native with { Identity = identity with { LogicalSize = 11 } },
            native with { State = state with { Availability = SyncCloudFileAvailability.PartiallyHydrated } },
            native with { State = state with { OnDiskBytes = 1 } },
            native with { ModifiedDataSize = 1 },
            native with { RawPlaceholderState = 1 } })
            Require(Classify(changed) == OwnedPointerObservation.NeedsVerification,
                "receipt/size/range/state mismatch is protected verification debt");
        Require(OwnedPointerProof.Classify(native, token, registration, pair, SyncProviders.Dropbox,
            3, stored, remote, 11, true) == OwnedPointerObservation.NeedsVerification,
            "logical EOF mismatch is not a harmless rename");
        var resident = native with { State = state with {
            Availability = SyncCloudFileAvailability.AvailableOffline, OnDiskBytes = 10 } };
        Require(Classify(resident with { Identity = identity with { DropboxRevision = "old-receipt" } }) ==
            OwnedPointerObservation.Resident, "fully resident bytes are ordinary hashable content");
        var entry = new PilotLocalEntry("new/file.txt", PilotEntryKind.File, "unused", 10,
            DateTime.UnixEpoch, null, 3, 9, SyncCloudFileAvailability.PartiallyHydrated,
            ContentUnreadable: true, NeedsRehash: true);
        var observed = LocalItems.FromScan(entry);
        Require(observed.Flags.HasFlag(ItemFlags.NeedsRehash) && !observed.Flags.HasFlag(ItemFlags.Placeholder) &&
            observed.Flags.HasFlag(ItemFlags.OwnedPointer) &&
            observed.Flags.HasFlag(ItemFlags.Unreadable) && observed.Hash is null,
            "partial observation survives inventory but cannot upload or auto-recall");

        using var store = new InMemoryStateStore();
        var localRoot = new RootDef("local", SyncProviders.Local, null, @"C:\Fixture",
            "id:local:00000001:0000000000000001");
        var remoteRoot = new RootDef("remote", SyncProviders.Dropbox, "account", "/Remote", "remote-root");
        store.SavePair(new PairDef(pair, "Pair A", localRoot, remoteRoot, [], [], true));
        var generation = store.BeginGeneration(localRoot.RootId, "full");
        store.WriteSnapshot(new Snapshot(localRoot.RootId, Side.A, generation, "cursor", [stored], []));
        var inventory = new LocalInventory(localRoot, Side.A, store, new HashSet<string>(),
            V2Settings.Default, TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage);
        var failedProbe = new PilotLocalScan(new Dictionary<string, PilotLocalEntry>(),
            [new PilotDecision(stored.Path, PilotDecisionKind.Blocked, OwnedPointerProof.ObservationMessage),
             new PilotDecision("new/file.txt", PilotDecisionKind.Blocked, OwnedPointerProof.ObservationMessage)], 0);
        generation = store.BeginGeneration(localRoot.RootId, "full");
        var snapshot = inventory.BuildSnapshot(generation, localRoot.RootId, "next", failedProbe, _ => true);
        Require(snapshot.Items.Count == 0 && snapshot.IncludesPath?.Invoke(stored.Path) == false &&
            snapshot.IncludesPath?.Invoke("new/file.txt") == false,
            "transient renamed pointer holds both names without publishing a degraded classification");
        store.WriteSnapshot(snapshot with { OpenAbsenceVerifier = null, VerifyAbsence = _ => FileAbsence.Gone });
        Require(store.ByObject(localRoot.RootId, stored.ObjectId) == stored,
            "even physical absence at the old name cannot erase an unobserved pointer's previous row");
    }

    private static void CheckNativeResidencyDecoder()
    {
        // Exact disguised attribute view observed on a disposable native pointer: tag=0,
        // CfGetPlaceholderStateFromAttributeTag=0, successful standard info, zero bytes.
        static SyncCloudFileAvailability Decode(long resident, uint attributes = 0x500020,
            uint raw = 0, long modified = 0) => WindowsCloudFilesProvider.DecodePlaceholderAvailability(
                false, 32, resident, modified, attributes, raw);
        Require(Decode(0) == SyncCloudFileAvailability.OnlineOnly,
            "hidden cloud reparse tag with recall-on-data-access is never resident");
        Require(Decode(8) == SyncCloudFileAvailability.PartiallyHydrated,
            "real nonzero partial native byte count remains partial with a hidden tag");
        Require(Decode(32) == SyncCloudFileAvailability.PartiallyHydrated,
            "recall flag never permits AvailableOffline, even with a full byte count");
        Require(Decode(0, 0x20) == SyncCloudFileAvailability.OnlineOnly,
            "successful native zero-byte metadata wins over absent attribute hints");
        Require(Decode(8, 0x20) == SyncCloudFileAvailability.PartiallyHydrated,
            "successful native partial-byte metadata wins over absent attribute hints");
        Require(Decode(32, 0x20) == SyncCloudFileAvailability.AvailableOffline,
            "actual EOF and full native residency admit ordinary bytes");
        Require(Decode(32, 0x1020) != SyncCloudFileAvailability.AvailableOffline,
            "OFFLINE also blocks resident classification");
        Require(Decode(0, 0x20, 0x31) == SyncCloudFileAvailability.OnlineOnly,
            "PARTIALLY_ON_DISK alone is not proof of partially resident bytes");
        Require(Decode(4096, 0x20) == SyncCloudFileAvailability.AvailableOffline,
            "native accounting above EOF is not invented corruption; recall flags still protect missing bytes");
        foreach (var bad in new[] { -1L })
        {
            var rejected = false;
            try { _ = Decode(bad); } catch (IOException) { rejected = true; }
            Require(rejected, "contradictory native counts fail metadata inspection rather than authorize reading");
        }
        var hiddenStub = new WindowsCloudFilesProvider.StubState(0, 0, 0, 1, 9) { HasStandardInfo = true };
        Require(hiddenStub.OwnedFullyDehydrated && !hiddenStub.FullyDehydrated,
            "owned native proof tolerates disguise without weakening foreign-stub deletion protection");
        Require(!(hiddenStub with { ModifiedBytes = 1 }).OwnedFullyDehydrated &&
                !(hiddenStub with { OnDiskBytes = 1 }).OwnedFullyDehydrated &&
                !(hiddenStub with { InSyncState = 0 }).OwnedFullyDehydrated,
            "owned mutation still demands zero resident/modified bytes and in-sync acknowledgment");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Owned pointer proof: " + message);
    }
}
