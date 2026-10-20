namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    public Task<SyncCliAvailabilityData> SetStorageIntentAsync(string pairId, string path, string mode,
        CancellationToken cancellationToken) => SetAvailabilityAsync(pairId, path, mode, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Never-refuse the intent is persisted before any eligibility check, including for a
    /// removable or non-NTFS volume, a pair layout that settlement does not handle, another registered sync root,
    /// and a path that is not indexed yet. Each limitation is then reported as a deferred reason with options
    /// instead of refusing the request.
    /// </remarks>
    public Task<SyncCliAvailabilityData> SetAvailabilityAsync(string pairId, string path, string availability,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pair = RequirePair(pairId);
        var store = StoragePolicyStore;
        var local = StorageLocalRoot(pair);

        // Deliberately independent of Run.UpdatePairSettings: intent is accepted during an active run.
        // A valid relative path with no indexed identity is retained as unresolved intent by the store.
        var relative = store.SetStoragePolicyAtPath(local, path, availability);
        // An online-only capability warning no longer applies after the final online policy is removed.
        // Offline hydration failures, if any, report their own current cause from the background lane.
        if (!store.HasOnlineStoragePolicy(local.RootId))
            StorageNotices.CompletePair(store, pair, "availability");

        var unsupportedLayout = pair.A.Provider != SyncProviders.Local || pair.B.Provider != SyncProviders.Dropbox;
        var volumeReason = availability == "online-only" ? StorageVolumeReason(local.RootPath) : null;
        var coexistence = availability == "online-only" && !unsupportedLayout && volumeReason is null
            ? StorageCoexistenceReason(local)
            : null;
        var unresolved = store.HasUnresolvedStorageIntent(local, relative);

        var reasons = new List<string>(4);
        var options = new List<string>(4);
        if (unsupportedLayout)
        {
            reasons.Add("this pair is not in the local-then-Dropbox layout that verified conversion and settlement handle");
            options.Add("keep this pair offline and use its provider's own availability controls, " +
                        "or create a local-to-Dropbox pair and repeat the intent there");
        }
        if (volumeReason is not null)
        {
            reasons.Add(volumeReason);
            options.Add("keep this pair offline, or move the pair to a fixed NTFS drive");
        }
        if (coexistence is not null)
        {
            reasons.Add(coexistence);
            options.Add("choose a folder outside the other registration's tree, or explicitly arrange a provider " +
                        "handover; Sync Shark never removes another provider's registration silently");
        }
        if (unresolved)
        {
            reasons.Add("this path has no indexed identity in the pair yet");
            options.Add("run or rescan the pair so the next scan indexes the path, then check storage list; " +
                        "the saved intent binds to that identity");
        }

        var note = StoragePhaseNote(availability);
        if (reasons.Count == 0)
            return Task.FromResult(new SyncCliAvailabilityData(pairId, relative, availability == "online-only",
                "Saved " + availability + " intent." + note +
                " Physical availability is not confirmed by this command."));

        return Task.FromResult(new SyncCliAvailabilityData(pairId, relative, false,
            "Saved " + availability + " intent, but it is not effective yet: " + string.Join("; ", reasons) + ". " +
            "Options: " + string.Join("; ", options) + "." + note +
            " Physical availability is not confirmed by this command."));
    }

    // Pair A is normally the local side; select B only so a reversed local pair still retains the intent.
    private static RootDef StorageLocalRoot(PairDef pair) =>
        pair.A.Provider == SyncProviders.Local ? pair.A
        : pair.B.Provider == SyncProviders.Local ? pair.B : pair.A;

    private static string StoragePhaseNote(string availability) => availability switch
    {
        "offline" => " Existing online-only files are queued for bounded, resumable hydration by the background host. " +
                     "Start the host to download them; failures retain the intent with Retry or Ignore.",
        "online-only" => " Files already on this device convert only after a verified remote revision and a " +
                         "reverified local content match; this command does not remove local bytes.",
        _ => " The explicit override is removed and the remaining policy hierarchy applies. " +
             "If the effective policy is offline, existing pointers are queued for hydration by the background host."
    };

    /// <summary>
    /// Windows Cloud Files needs a fixed NTFS volume. Returns the deferred reason, or null when the volume qualifies.
    /// </summary>
    private static string? StorageVolumeReason(string rootPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(rootPath)) return null;
        try
        {
            var volume = Path.GetPathRoot(Path.GetFullPath(rootPath));
            if (volume is null) return null;
            var drive = new DriveInfo(volume);
            if (drive.DriveType == DriveType.Removable)
                return "Windows Cloud Files does not support online-only on removable volumes";
            if (drive.DriveType != DriveType.Fixed)
                return "Windows Cloud Files requires a fixed NTFS volume, but this pair is on a " +
                       drive.DriveType.ToString().ToLowerInvariant() + " volume";
            var format = drive.DriveFormat;
            if (!string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase))
                return "Windows Cloud Files requires a fixed NTFS volume, but this pair's drive format is " +
                       (string.IsNullOrWhiteSpace(format) ? "not confirmed as NTFS" : format);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return null;
    }

    /// <summary>
    /// Predicts at command time the coexistence refusal that settlement would otherwise report only after registration.
    /// The store keeps the intent either way; this only picks the deferred reason. Overlap comes from the shared
    /// SyncRootManager registry read (no folder tree is scanned). A registration at this exact root counts as ours only
    /// when the v2 store records the GUID read from native SyncRootIdentity at that exact path, never a provider name,
    /// registry entry id or Cf root file id (which can recur after a registration reset).
    /// </summary>
    private string? StorageCoexistenceReason(RootDef local)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var root = Path.GetFullPath(local.RootPath);
            if (!StorageInventoryWindows.TryRegisteredRoots(out var registrations))
                return "the registered sync-root list could not be inspected";
            // Always inspect native identity, including when Explorer has published no entry at this path.
            var registration = WindowsCloudFilesProvider.ReadRootRegistration(root);
            var token = registration?.OwnershipToken;
            if (registration is not null && (token is null ||
                !StoragePolicyStore.OwnsStorageRegistration(local.RootId, root, token)))
                return "another or unrecorded provider owns the folder's native Cloud Files registration";
            foreach (var entry in registrations)
            {
                var overlap = WindowsCloudFilesProvider.RegisteredPathOverlap(root, new[] { entry.Path });
                if (overlap is null) continue;
                if (overlap.Value.Relation == "ancestor")
                    return "an ancestor folder is already a registered Cloud Files sync root";
                if (overlap.Value.Relation == "descendant")
                    return "a descendant folder is already a registered Cloud Files sync root";
                if (token is null) return "the folder is already a registered Cloud Files sync root";
            }
            return token is null && WindowsCloudFilesProvider.IsCloudFilesPlaceholder(root)
                ? "another File Explorer cloud provider already owns this folder" : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return "the folder's File Explorer cloud registration could not be inspected";
        }
    }

    private SqliteStateStore StoragePolicyStore => _store as SqliteStateStore ??
        throw new SyncCliUnsupportedException("Storage intent requires the durable SQLite store. No policy changed.");

    public Task<object> ListStorageAsync(string pairId, int limit, int offset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pair = RequirePair(pairId);
        // The advertised help says 1..200; use that literal rather than an activity-specific constant.
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);
        StoragePolicyStore.EnableStorage();
        return Task.FromResult<object>(new { pairId, limit, offset,
            unresolvedIntents = StoragePolicyStore.UnresolvedStorageIntents(StorageLocalRoot(pair), limit, offset),
            folders = StoragePolicyStore.StorageFolderSummaries(StorageLocalRoot(pair), limit, offset),
            detail = "Explicit online-only folders; counts describe indexed descendant files, including nested overrides. " +
                     "Unknown means no confirmed physical observation." });
    }

}
