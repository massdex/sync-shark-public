using System.Text;
using Massdex.SyncShark;

// SS-D12: real checks on the ONE validated presentation projection. Every read is the real store on
// isolated disposable state, and every counted store load is the production loader invocation reported by
// the installed diagnostic seam, so a hidden extra read is counted rather than assumed. Nothing here
// creates a provider request, reads a credential, writes an original file or touches product state.
internal static class PresentationSnapshotChecks
{
    private static int _checks;

    internal static async Task<int> RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var authority = instance.StateAuthority;
        var stateRoot = authority.StateRoot;
        using var http = new HttpClient();
        using var host = new SyncAuthorityRuntimeHost(authority, http);
        var dropboxStore = new DropboxConnectionStore(authority);
        var googleStore = new GoogleDriveConnectionStore(authority);
        var preferencesStore = new UiPreferencesStore(authority);
        var token = CancellationToken.None;

        // --- one validated projection: exactly three real store reads, in a fixed order -------------
        var primary = DropboxAccount("presentation-primary", primary: true);
        var google = GoogleAccount("presentation-google-permission");
        await dropboxStore.SaveNewAsync(primary, token);
        await googleStore.SaveAsync(google, token);
        await preferencesStore.SaveAsync(new UiPreferences(UiPreferencesStore.DarkTheme,
            AccountPauses: [new SyncAccountPause(google.ConnectionId, true, null)]), token);

        var reads = new List<string>();
        var snapshot = Measure(host, reads);
        Check(reads.Count == 3,
            "one presentation projection performs exactly three real store reads: " + Describe(reads));
        Check(reads[0] == "dropbox-connections:1" && reads[1] == "google-drive-connections:1" &&
              reads[2] == "ui-preferences:1",
            "the three reads are the whole active Dropbox collection, the whole active Google Drive " +
            "collection and the normalized preferences, each reporting what it actually parsed: " + Describe(reads));
        Check(snapshot.AccountsByConnectionId.Count == 2 && snapshot.OrderedAccounts.Count == 2,
            "the projection carries one metadata entry per active account and nothing else");
        Check(snapshot.OrderedAccounts[0].ConnectionId == primary.ConnectionId &&
              snapshot.OrderedAccounts[1].ConnectionId == google.ConnectionId,
            "the active accounts keep their existing presentation order: Dropbox connection-id order, then Google Drive");
        Check(snapshot.PrimaryDropboxConnectionId == primary.ConnectionId &&
              snapshot.PrimaryConnectionId == primary.ConnectionId,
            "the primary Dropbox account is identified from the same collection read");

        // --- display lookups match the existing authority semantics, with no further store reads ----
        Check(snapshot.Account(primary.ConnectionId)?.Email == primary.Email &&
              snapshot.Account(google.ConnectionId)?.Email == google.Email,
            "an exact account id resolves to that account's non-secret display metadata");
        Check(snapshot.Account(null)?.ConnectionId == primary.ConnectionId,
            "a null account id keeps the existing primary fallback: the primary Dropbox account first");
        Check(snapshot.Account("connection-" + new string('f', 24)) is null,
            "an unknown account id supplies no account instead of an arbitrary available one");
        Check(snapshot.AccountPaused(google.ConnectionId, DateTimeOffset.UtcNow) &&
              !snapshot.AccountPaused(primary.ConnectionId, DateTimeOffset.UtcNow) &&
              !snapshot.AccountPaused(null, DateTimeOffset.UtcNow),
            "the persisted account pause is projected exactly, and a blank id resolves only through the primary Dropbox account");
        Check(!snapshot.GlobalPaused(DateTimeOffset.UtcNow),
            "the projection states the global pause from the same preferences");

        var legacyExplicit = Pair("legacy-explicit", primary.ConnectionId);
        var legacyBlank = Pair("legacy-blank", "");
        var accountless = Pair("accountless-local", "") with { Provider = SyncProviders.Local };
        var localToCloud = Pair("local-to-cloud", "") with
        {
            EndpointSchemaVersion = 1,
            LeftEndpoint = new SyncEndpointRecord(SyncEndpointKind.Local, LocalPath: Path.Combine(root, "local")),
            RightEndpoint = new SyncEndpointRecord(SyncEndpointKind.GoogleDrive, Provider: SyncProviders.GoogleDrive,
                ConnectionId: google.ConnectionId, NamespaceId: "google-namespace", FolderId: "id:google-folder",
                FolderPath: "/Google Folder")
        };
        var cloudToCloud = Pair("cloud-to-cloud", "") with
        {
            EndpointSchemaVersion = 1,
            LeftEndpoint = new SyncEndpointRecord(SyncEndpointKind.Dropbox, Provider: SyncProviders.Dropbox,
                ConnectionId: primary.ConnectionId, NamespaceId: "dropbox-namespace", FolderId: "id:dropbox-folder",
                FolderPath: "/Dropbox Folder"),
            RightEndpoint = new SyncEndpointRecord(SyncEndpointKind.GoogleDrive, Provider: SyncProviders.GoogleDrive,
                ConnectionId: google.ConnectionId, NamespaceId: "google-namespace", FolderId: "id:google-folder",
                FolderPath: "/Google Folder")
        };
        Check(snapshot.PairConnectionIds(legacyExplicit).SequenceEqual([primary.ConnectionId]),
            "a legacy pair with a saved account resolves that exact account");
        Check(snapshot.PairConnectionIds(legacyBlank).SequenceEqual([primary.ConnectionId]),
            "a legacy pair with no saved account resolves the primary Dropbox account");
        Check(snapshot.PairConnectionIds(accountless).Count == 0,
            "an accountless local/local pair stays accountless");
        Check(snapshot.PairConnectionIds(localToCloud).SequenceEqual([google.ConnectionId]),
            "a pair with an endpoint document resolves its nonlocal endpoint account");
        Check(snapshot.PairConnectionIds(cloudToCloud).SequenceEqual([primary.ConnectionId, google.ConnectionId]),
            "a two-endpoint pair resolves both nonlocal accounts in endpoint order");
        Check(snapshot.PairAccountPaused(snapshot.PairConnectionIds(cloudToCloud), DateTimeOffset.UtcNow) &&
              !snapshot.PairAccountPaused(snapshot.PairConnectionIds(legacyBlank), DateTimeOffset.UtcNow),
            "the pair-level pause projection follows the same per-account projection");

        // A hundred pair and account lookups inside one projection must add no store read at all.
        var pairCountReads = new List<string>();
        SyncAuthorityRuntimeHost.PresentationStoreLoadForTest = (store, count) => pairCountReads.Add(store + ":" + count);
        try
        {
            var lookups = 0;
            for (var index = 0; index < 100; index++)
            {
                var pair = Pair("scale-pair-" + index, index % 2 == 0 ? primary.ConnectionId : google.ConnectionId);
                lookups += snapshot.PairConnectionIds(pair).Count;
                _ = snapshot.Account(pair.ConnectionId);
                _ = snapshot.AccountPaused(pair.ConnectionId, DateTimeOffset.UtcNow);
            }
            Check(lookups == 100 && pairCountReads.Count == 0,
                "one hundred pair and account lookups reuse the projection and add no store read: " + Describe(pairCountReads));
        }
        finally { SyncAuthorityRuntimeHost.PresentationStoreLoadForTest = null; }

        // --- the read count does not grow with the number of accounts -------------------------------
        for (var index = 0; index < 99; index++)
            await dropboxStore.SaveAsync(DropboxAccount("presentation-scale-" + index, primary: false), token);
        var scaledReads = new List<string>();
        var scaled = Measure(host, scaledReads);
        Check(scaledReads.Count == 3 && scaledReads[0] == "dropbox-connections:100",
            "one hundred active accounts still take exactly three store reads: " + Describe(scaledReads));
        Check(scaled.AccountsByConnectionId.Count == 101 && scaled.OrderedAccounts.Count == 101,
            "the whole active collection is projected, not a sample: " +
            scaled.AccountsByConnectionId.Count + " accounts");
        Check(scaled.OrderedAccounts[^1].ConnectionId == google.ConnectionId,
            "every Dropbox account is presented before the Google Drive accounts in the existing order");
        Check(scaled.AccountsByConnectionId.Keys.OrderBy(key => key, StringComparer.Ordinal)
                  .SequenceEqual(scaled.AccountsByConnectionId.Keys),
            "the account index is deterministic ordinal and immutable for the pass");

        // --- a rejected projection supplies nothing, not the previous successful one ---------------
        var corruptReads = new List<string>();
        var slotPreimages = CaptureSlots(stateRoot, "dropbox-connection");
        CorruptSlots(stateRoot, "dropbox-connection");
        var rejected = false;
        try { _ = Measure(host, corruptReads); }
        catch (InvalidDataException error)
        {
            rejected = true;
            Check(!error.Message.Contains(primary.Email, StringComparison.Ordinal),
                "the rejection states no account detail from the previous successful projection");
        }
        Check(rejected, "corrupt current slots reject the next projection outright");
        Check(corruptReads.Count == 0,
            "the rejected projection supplies no partially loaded account or preference data: " + Describe(corruptReads));

        // --- valid state again recovers on the next projection without a restart --------------------
        RestoreSlots(slotPreimages);
        // G2: the supported logical tombstone must follow the in-place restore and precede the new binding,
        // exactly as the correction required; no identity-bound file is deleted anywhere in this path.
        await dropboxStore.DeleteAsync(token);
        var recovered = DropboxAccount("presentation-recovered", primary: true);
        await dropboxStore.SaveNewAsync(recovered, token);
        var recoveredReads = new List<string>();
        var afterRecovery = Measure(host, recoveredReads);
        Check(recoveredReads.Count == 3 && afterRecovery.Account(recovered.ConnectionId)?.Email == recovered.Email,
            "restoring valid state recovers the next projection without a restart: " + Describe(recoveredReads));
        Check(!afterRecovery.AccountsByConnectionId.ContainsKey(primary.ConnectionId),
            "the recovered projection never supplies the replaced account from the earlier successful pass");
        Check(afterRecovery.Account(google.ConnectionId)?.Email == google.Email,
            "the untouched provider collection is still projected truthfully");

        // --- the null-account fallback falls through to the primary Google Drive account ------------
        await dropboxStore.DeleteAsync(token);
        var googleOnlyReads = new List<string>();
        var googleOnly = Measure(host, googleOnlyReads);
        Check(googleOnlyReads.Count == 3 && googleOnly.PrimaryDropboxConnectionId is null &&
              googleOnly.Account(null)?.ConnectionId == google.ConnectionId,
            "with no Dropbox account the primary fallback resolves the primary Google Drive account");

        // --- the whole active collection is still validated, not just its first member -------------
        var duplicate = DropboxAccount("presentation-duplicate", primary: true);
        await dropboxStore.SaveNewAsync(duplicate with
        {
            AdditionalConnections = [DropboxAccount("presentation-duplicate", primary: false)]
        }, token);
        var duplicateRejected = false;
        try { _ = host.LoadPresentationSnapshot(); }
        catch (InvalidDataException) { duplicateRejected = true; }
        Check(duplicateRejected,
            "a duplicated account inside the active collection rejects the whole projection instead of " +
            "presenting its first member");

        Console.WriteLine("{\"result\":\"PASS\",\"checks\":" + _checks +
            ",\"boundary\":\"real isolated state stores, real host projection, counted store reads; " +
            "no provider, network, credential, signing or user-folder effect\"}");
        return 0;
    }

    private static SyncPresentationSnapshot Measure(SyncAuthorityRuntimeHost host, List<string> reads)
    {
        SyncAuthorityRuntimeHost.PresentationStoreLoadForTest = (store, count) => reads.Add(store + ":" + count);
        try { return host.LoadPresentationSnapshot(); }
        finally { SyncAuthorityRuntimeHost.PresentationStoreLoadForTest = null; }
    }

    private static string Describe(IReadOnlyList<string> reads) =>
        reads.Count == 0 ? "no store read" : string.Join(", ", reads);

    private static string SlotPath(string stateRoot, string baseName, string slot) =>
        Path.Combine(stateRoot, baseName + "-" + slot + ".json");

    private static void CorruptSlots(string stateRoot, string baseName)
    {
        foreach (var slot in new[] { "a", "b" })
            File.WriteAllBytes(SlotPath(stateRoot, baseName, slot), Encoding.UTF8.GetBytes("corrupt-slot"));
    }

    private static Dictionary<string, byte[]> CaptureSlots(string stateRoot, string baseName)
    {
        var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var slot in new[] { "a", "b" })
        {
            var path = SlotPath(stateRoot, baseName, slot);
            if (File.Exists(path)) images[path] = File.ReadAllBytes(path);
        }
        return images;
    }

    // F4: the captured slots are restored in place, preserving the exact file identity the authority binds,
    // so recovery never deletes and recreates an identity-bound state file.
    private static void RestoreSlots(Dictionary<string, byte[]> preimages)
    {
        foreach (var entry in preimages)
        {
            if (!File.Exists(entry.Key))
                throw new InvalidDataException("The captured state slot was removed instead of corrupted: " + entry.Key);
            File.WriteAllBytes(entry.Key, entry.Value);
        }
    }

    private static void ClearSlots(string stateRoot, string baseName)
    {
        foreach (var slot in new[] { "a", "b" })
            if (File.Exists(SlotPath(stateRoot, baseName, slot))) File.Delete(SlotPath(stateRoot, baseName, slot));
    }

    private static SyncPair Pair(string id, string connectionId) =>
        new(id, id, Path.Combine(Path.GetTempPath(), "sync-shark-presentation", id), 1, 1, "/Presentation",
            "id:presentation", ConnectionId: connectionId);

    private static DropboxConnectionBinding DropboxAccount(string account, bool primary)
    {
        var connectionId = DropboxConnectionBinding.CreateConnectionId(account);
        return new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, account,
            "root-" + account, "home-" + account, DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes, ConnectionId: connectionId, DisplayName: account,
            Email: account + "@example.test",
            CredentialReference: primary ? WindowsDropboxRefreshTokenStore.RegisteredTarget
                : WindowsDropboxRefreshTokenStore.AccountTarget(connectionId)).Normalize();
    }

    private static GoogleDriveConnectionBinding GoogleAccount(string permissionId)
    {
        var accountKey = GoogleDriveConnectionBinding.CreateAccountKey(permissionId);
        return new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId, accountKey,
            permissionId, permissionId + "@example.test", permissionId,
            GoogleDriveProviderConfiguration.RequiredScopes,
            ConnectionId: GoogleDriveConnectionBinding.CreateConnectionId(accountKey)).Normalize();
    }

    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException("Presentation snapshot: " + label);
        _checks++;
        Console.WriteLine("PASS " + label);
    }
}

