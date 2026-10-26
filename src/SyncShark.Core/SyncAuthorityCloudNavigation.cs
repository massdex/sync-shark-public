namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityRuntimeHost
{
    /// <summary>Returns a provider-supplied URL. expectedAccountId is the exact saved ConnectionId.</summary>
    public async Task<string> GetGoogleDriveWebLocationAsync(string pairId, string expectedAccountId,
        string relativePath = ".", CancellationToken cancellationToken = default) =>
        (await ResolveGoogleDriveWebLocationAsync(pairId, expectedAccountId, relativePath, cancellationToken)).WebViewLink;

    /// <summary>Returns an exact, root-bound item identity for navigation or a subsequent sharing-client read.</summary>
    public async Task<GoogleDriveResolvedWebLocation> ResolveGoogleDriveWebLocationAsync(string pairId,
        string expectedAccountId, string relativePath = ".", CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        ArgumentException.ThrowIfNullOrWhiteSpace(pairId);
        var pair = (await LoadPairsAsync(cancellationToken)).Pairs.SingleOrDefault(item => item.Id == pairId)
            ?? throw new InvalidDataException("The requested folder pair is no longer retained.");
        ValidateGoogleNavigationTarget(pair, expectedAccountId);
        var client = CreateGoogleDriveClient(pair);
        if (client.Connection.ConnectionId != expectedAccountId)
            throw new InvalidOperationException("The selected Google account changed. Refresh before opening this location.");
        var location = await client.ResolveWebLocationAsync(expectedAccountId, relativePath, cancellationToken);
        var current = (await LoadPairsAsync(cancellationToken)).Pairs.SingleOrDefault(item => item.Id == pairId);
        if (current is null || !IsGooglePair(current) || current.ConnectionId != pair.ConnectionId ||
            current.DropboxFolderId != pair.DropboxFolderId || current.DropboxRoot != pair.DropboxRoot)
            throw new InvalidOperationException("The folder pair changed while its Google location was checked.");
        var binding = LoadGoogleDriveConnection(expectedAccountId);
        if (binding is null || binding.ConnectionId != client.Connection.ConnectionId ||
            binding.AccountKey != client.Connection.AccountKey || binding.PermissionId != client.Connection.PermissionId ||
            binding.ClientId != client.Connection.ClientId || binding.SharedDrivesEnabled != client.Connection.SharedDrivesEnabled)
            throw new InvalidOperationException("The Google connection changed while its location was checked.");
        return location;
    }

    internal static void ValidateGoogleNavigationTarget(SyncPair pair, string expectedAccountId)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (!IsGooglePair(pair)) throw new InvalidOperationException("This navigation action requires a Google Drive pair.");
        if (string.IsNullOrWhiteSpace(expectedAccountId) || pair.ConnectionId != expectedAccountId)
            throw new InvalidOperationException("The requested folder pair belongs to a different account.");
        if (string.IsNullOrWhiteSpace(pair.DropboxFolderId))
            throw new InvalidDataException("The Google folder pair has no bound root identity.");
    }

    /// <summary>
    /// EPS5/EL1: the SIDE- and CONTEXT-bound Google Drive endpoint location. The requested logical side is
    /// resolved from the pair's own canonical endpoint context and both ordered identities must still equal
    /// the context the caller captured, so a non-primary Google account is served from its OWN recorded
    /// account and root and a pair or account rebind refuses. Exactly one ResolveWebLocationAsync call is
    /// made, followed by post-await pair and protected-binding revalidation. A genuine no-document legacy
    /// pair keeps its existing saved-account client and an absent namespace is not by itself a refusal; a
    /// declared malformed or incomplete document never falls back to legacy fields. No fake pair, no
    /// primary-account substitution, no new scope and no provider mutation.
    /// </summary>
    public async Task<GoogleDriveResolvedWebLocation> ResolveGoogleDriveEndpointWebLocationAsync(string pairId,
        SyncPairSide side, SyncEndpointContext expectedContext, string relativePath = ".",
        CancellationToken cancellationToken = default)
    {
        stateAuthority.VerifyAuthority();
        ArgumentException.ThrowIfNullOrWhiteSpace(pairId);
        ArgumentNullException.ThrowIfNull(expectedContext);
        var pair = (await LoadPairsAsync(cancellationToken)).Pairs.SingleOrDefault(item => item.Id == pairId)
            ?? throw new InvalidDataException("The requested folder pair is no longer retained.");
        // EL1-C1: an enum value that is neither logical left nor right is rejected here, before any context
        // work or provider access, rather than being silently mapped onto the right-hand side.
        if (side is not (SyncPairSide.Left or SyncPairSide.Right))
            throw new ArgumentOutOfRangeException(nameof(side),
                "The requested side must be this pair's logical left or right endpoint.");
        // EL1-C2: the SUPPLIED context is validated with the same existing rule the append side uses, against
        // the requested pair id, so a null side, a mis-ordered side or a foreign pair is refused before the
        // provider is reached rather than trusted because it compares equal to itself.
        var contextReasons = SyncEndpointContext.Validate(expectedContext, pairId);
        if (contextReasons.Count != 0)
            throw new ArgumentException(
                "The supplied endpoint context cannot authorize this navigation: " +
                string.Join("; ", contextReasons), nameof(expectedContext));
        var context = SyncEndpointContext.Capture(pair);
        RequireSameOrderedEndpointContext(context, expectedContext);
        var selected = side == SyncPairSide.Left ? context.Left : context.Right;
        if (selected.IsLocal || selected.Kind != SyncEndpointKind.GoogleDrive)
            throw new InvalidOperationException(
                "The requested side of this pair is not a Google Drive endpoint, so it has no Drive location.");
        if (string.IsNullOrWhiteSpace(selected.ConnectionId))
            throw new InvalidOperationException(
                "The Google Drive endpoint records no exact account; a default or currently selected account is never substituted.");
        if (string.IsNullOrWhiteSpace(selected.FolderId))
            throw new InvalidDataException("The Google Drive endpoint has no bound root identity.");
        var accountId = selected.ConnectionId!;
        GoogleDrivePilotClient client;
        if (pair.EndpointDocument is null)
        {
            // A genuine no-document legacy pair: its saved account and root are the only authority there is,
            // and an absent namespace is not by itself a refusal.
            client = CreateGoogleDriveClient(pair);
            if (!string.Equals(client.Connection.ConnectionId, accountId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The selected Google account changed. Refresh before opening this location.");
        }
        else
        {
            // Declared symmetric endpoints: the requested side's OWN account and namespace are required, and
            // an incomplete declaration is refused rather than completed from legacy fields.
            if (string.IsNullOrWhiteSpace(selected.NamespaceId))
                throw new InvalidDataException("The declared Google Drive endpoint has no native namespace.");
            var record = new SyncEndpointRecord(selected.Kind, Provider: selected.Provider,
                ConnectionId: accountId, NamespaceId: selected.NamespaceId, FolderId: selected.FolderId,
                FolderPath: selected.FolderPath);
            var facts = ResolveCloudAccount(selected.Kind, accountId);
            // EL1-C3: the SHARED validating factory is used, not the Google-specific one, because it is the
            // shared step that requires the declared endpoint namespace to equal the retained provider-account
            // derivation before it selects the Google adapter.
            client = CreateCloudEndpointAdapter(record, facts) as GoogleDrivePilotClient
                     ?? throw new NotSupportedException(
                         "The registered Google Drive endpoint produced no Google Drive client.");
        }
        var location = await client.ResolveWebLocationAsync(accountId, relativePath, cancellationToken);
        var current = (await LoadPairsAsync(cancellationToken)).Pairs.SingleOrDefault(item => item.Id == pairId);
        if (current is null)
            throw new InvalidOperationException("The folder pair changed while its Google location was checked.");
        RequireSameOrderedEndpointContext(SyncEndpointContext.Capture(current), expectedContext);
        var binding = LoadGoogleDriveConnection(accountId);
        if (binding is null || !string.Equals(binding.ConnectionId, client.Connection.ConnectionId, StringComparison.Ordinal) ||
            !string.Equals(binding.AccountKey, client.Connection.AccountKey, StringComparison.Ordinal) ||
            !string.Equals(binding.PermissionId, client.Connection.PermissionId, StringComparison.Ordinal) ||
            !string.Equals(binding.ClientId, client.Connection.ClientId, StringComparison.Ordinal) ||
            binding.SharedDrivesEnabled != client.Connection.SharedDrivesEnabled)
            throw new InvalidOperationException("The Google connection changed while its location was checked.");
        return location;
    }

    /// <summary>Both ordered identities of one capture must still equal the caller's captured context.</summary>
    internal static void RequireSameOrderedEndpointContext(SyncEndpointContext current, SyncEndpointContext expected)
    {
        // EL1-C2: the pair id and BOTH ordered sides' full existing identity fields are compared, including the
        // two local identity fields. CapturedName is display text and is deliberately NOT an identity field, so
        // a name-only change is not an identity failure.
        if (!string.Equals(current.PairId, expected.PairId, StringComparison.Ordinal) ||
            !SameEndpointContextSide(current.Left, expected.Left) ||
            !SameEndpointContextSide(current.Right, expected.Right))
            throw new InvalidOperationException("The folder pair changed while its Google location was checked.");

        static bool SameEndpointContextSide(SyncEndpointContextSide actual, SyncEndpointContextSide recorded) =>
            actual.Side == recorded.Side && actual.Kind == recorded.Kind &&
            string.Equals(actual.CompatibilityPort, recorded.CompatibilityPort, StringComparison.Ordinal) &&
            string.Equals(actual.Provider, recorded.Provider, StringComparison.Ordinal) &&
            string.Equals(actual.ConnectionId, recorded.ConnectionId, StringComparison.Ordinal) &&
            string.Equals(actual.NamespaceId, recorded.NamespaceId, StringComparison.Ordinal) &&
            string.Equals(actual.FolderId, recorded.FolderId, StringComparison.Ordinal) &&
            string.Equals(actual.FolderPath, recorded.FolderPath, StringComparison.Ordinal) &&
            string.Equals(actual.LocalPath, recorded.LocalPath, StringComparison.Ordinal) &&
            actual.LocalVolumeSerialNumber == recorded.LocalVolumeSerialNumber &&
            actual.LocalRootFileId == recorded.LocalRootFileId;
    }
}
