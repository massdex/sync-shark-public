using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark;

// Shared endpoint identity and validation contract. Presentation, editor drafts, content identity and
// the local/local runtime consume this model. The legacy saved pair format remains unchanged until
// the runtime host's explicit additive persistence integration is activated.

/// <summary>
/// The storage kind an endpoint names. The persisted provider literals remain
/// SyncProviders.Dropbox and SyncProviders.GoogleDrive; this enum only gives the symmetric model a
/// closed set of kinds, including a local folder.
/// </summary>
public enum SyncEndpointKind
{
    Local = 0,
    Dropbox = 1,
    GoogleDrive = 2
}

/// <summary>
/// Provider-level identity semantics, shared by endpoint validation and presentation. Account identity
/// authorizes access; it does not necessarily separate storage objects shared between accounts.
/// New providers register their semantics here rather than adding conditionals to each pair surface.
/// Runtime capabilities remain the adapter's responsibility and are not inferred from this metadata.
/// </summary>
public sealed record SyncEndpointProviderDefinition(SyncEndpointKind Kind, string? ProviderId,
    string DisplayName, bool ObjectIdsAreProviderGlobal, bool PathsDescribeAncestry,
    string ContentHashAlgorithm);

public static class SyncEndpointProviders
{
    public static IReadOnlyList<SyncEndpointProviderDefinition> All { get; } = Array.AsReadOnly(
        new SyncEndpointProviderDefinition[]
        {
            new(SyncEndpointKind.Local, null, "This computer", false, true, SyncContentIdentity.DropboxBlockSha256),
            new(SyncEndpointKind.Dropbox, SyncProviders.Dropbox, "Dropbox", true, true, SyncContentIdentity.DropboxBlockSha256),
            new(SyncEndpointKind.GoogleDrive, SyncProviders.GoogleDrive, "Google Drive", true, false, SyncContentIdentity.Sha256)
        });

    public static SyncEndpointProviderDefinition Get(SyncEndpointKind kind) =>
        All.SingleOrDefault(provider => provider.Kind == kind)
        ?? throw new ArgumentOutOfRangeException(nameof(kind), "This endpoint provider is not registered.");

    public static bool TryGet(string? providerId, out SyncEndpointProviderDefinition? definition)
    {
        definition = string.IsNullOrWhiteSpace(providerId) ? null : All.SingleOrDefault(provider =>
            provider.ProviderId is not null && string.Equals(provider.ProviderId, providerId.Trim(),
                StringComparison.OrdinalIgnoreCase));
        return definition is not null;
    }
}

/// <summary>
/// A safe, non-secret reason an endpoint, pair, persisted record or account binding was refused.
/// Members 1-7 are the original N02 taxonomy and keep their exact meanings; 8-20 were added by the
/// E1 contract packet and are listed in the E1 result artifact.
/// </summary>
public enum SyncEndpointError
{
    UnknownProvider = 1,
    MissingLocalPath = 2,
    InvalidLocalPath = 3,
    MissingCloudAccount = 4,
    MissingCloudFolder = 5,
    SameEndpoint = 6,
    OverlappingLocalRoots = 7,

    /// <summary>A persisted local endpoint carries no stable local folder identity.</summary>
    MissingLocalIdentity = 8,

    /// <summary>A persisted local identity is malformed or incomplete (a zero volume or file id).</summary>
    InvalidLocalIdentity = 9,

    /// <summary>The cloud folder identity is not in the accepted shape for its provider.</summary>
    InvalidCloudFolderIdentity = 10,

    /// <summary>A persisted record mixes local and cloud identity, or its provider disagrees with its kind.</summary>
    /// <remarks>
    /// For a cloud/cloud pair it also classifies every compatibility-projection, local-identity and
    /// device-storage rule that contradicts the pair's explicit endpoint document: that document is
    /// the only cloud/cloud source of truth, and the absent local values it projects must stay absent.
    /// </remarks>
    EndpointKindMismatch = 11,

    /// <summary>More than one verified account binding could satisfy a legacy implicit account.</summary>
    AmbiguousAccountBinding = 12,

    /// <summary>No verified account binding exists for the account a legacy pair depends on.</summary>
    UnavailableAccountBinding = 13,

    /// <summary>Two cloud endpoints on one verified account name the same folder or nested folders.</summary>
    OverlappingCloudFolders = 14,

    /// <summary>Cloud ancestry or ownership cannot be established; the pair fails closed.</summary>
    UnverifiedCloudNamespace = 15,

    /// <summary>An existing pair already retains this exact endpoint.</summary>
    DuplicateEndpointAcrossPairs = 16,

    /// <summary>An existing pair retains an endpoint that contains or is contained by this one.</summary>
    OverlappingEndpointAcrossPairs = 17,

    /// <summary>The persisted endpoint document declares a version this build does not understand.</summary>
    UnsupportedSchemaVersion = 18,

    /// <summary>No persisted endpoint document was supplied where one was required.</summary>
    MissingEndpointDocument = 19,

    /// <summary>The persisted endpoint document is present but structurally invalid.</summary>
    InvalidSchemaDocument = 20
}

/// <summary>One refusal reason with its safe explanation.</summary>
public sealed record SyncEndpointRefusal(SyncEndpointError Error, string Reason);

/// <summary>The complete refusal set for one proposed endpoint or pair. Empty refusals mean accepted.</summary>
public sealed record SyncEndpointValidation(IReadOnlyList<SyncEndpointRefusal> Refusals)
{
    public static SyncEndpointValidation Accepted { get; } = new(Array.Empty<SyncEndpointRefusal>());

    public bool IsValid => Refusals.Count == 0;

    public IReadOnlyList<SyncEndpointError> Errors => Refusals.Select(item => item.Error).ToArray();

    public string Describe() => string.Join("; ", Refusals.Select(item => item.Error + ": " + item.Reason));

    /// <summary>The union of several validation outcomes, preserving each refusal in order.</summary>
    public static SyncEndpointValidation Combine(params SyncEndpointValidation[] validations)
    {
        ArgumentNullException.ThrowIfNull(validations);
        var refusals = validations.Where(item => item is not null).SelectMany(item => item.Refusals).ToArray();
        return refusals.Length == 0 ? Accepted : new SyncEndpointValidation(refusals);
    }
}

/// <summary>
/// One immutable endpoint identity. A local endpoint carries only its local folder path; a cloud
/// endpoint carries the stable connection id of its account plus the provider folder id and/or path.
/// Values are retained exactly as supplied: the compatibility layer must round-trip saved legacy
/// fields unchanged, so this type never silently rewrites the state it was given.
/// </summary>
public sealed record SyncEndpoint
{
    private SyncEndpoint(SyncEndpointKind kind, string? localPath, SyncLocalEndpointIdentity? localIdentity,
        string? connectionId, string? namespaceId, string? folderId, string? folderPath)
    {
        Kind = kind;
        LocalPath = localPath;
        LocalIdentity = localIdentity;
        ConnectionId = connectionId;
        NamespaceId = namespaceId;
        FolderId = folderId;
        FolderPath = folderPath;
    }

    public SyncEndpointKind Kind { get; }

    /// <summary>The bounded local folder path. Present only for SyncEndpointKind.Local.</summary>
    public string? LocalPath { get; }

    /// <summary>
    /// The stable local folder identity (NTFS volume serial plus file id) when it is known. A local
    /// endpoint persisted by this contract requires it; the value model tolerates its absence so an
    /// unpersisted draft endpoint remains expressible.
    /// </summary>
    public SyncLocalEndpointIdentity? LocalIdentity { get; }

    /// <summary>The stable account/connection id. Present only for a cloud endpoint.</summary>
    public string? ConnectionId { get; }

    /// <summary>
    /// The verified provider root namespace of the bound account, when the product has verified one.
    /// Two cloud endpoints on one provider are separated only by a verified namespace; an unverified
    /// namespace never separates them.
    /// </summary>
    public string? NamespaceId { get; }

    /// <summary>The provider folder id. Present only for a cloud endpoint, when the provider issued one.</summary>
    public string? FolderId { get; }

    /// <summary>The provider folder path. Present only for a cloud endpoint, when a path is known.</summary>
    public string? FolderPath { get; }

    public bool IsLocal => Kind == SyncEndpointKind.Local;

    public bool IsCloud => !IsLocal;

    /// <summary>Creates a local endpoint. A local endpoint can never carry cloud identity.</summary>
    public static SyncEndpoint Local(string localPath) => Local(localPath, null);

    /// <summary>Creates a local endpoint together with the stable local folder identity it is bound to.</summary>
    public static SyncEndpoint Local(string localPath, SyncLocalEndpointIdentity? identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        if (!Path.IsPathFullyQualified(localPath))
            throw new ArgumentException("A local endpoint requires a fully qualified local folder path.", nameof(localPath));
        if (identity is not null && !identity.IsComplete)
            throw new ArgumentException("A local endpoint identity requires a non-zero volume serial and file id.", nameof(identity));
        return new SyncEndpoint(SyncEndpointKind.Local, localPath, identity, null, null, null, null);
    }

    /// <summary>Creates a cloud endpoint. A cloud endpoint can never carry a local path.</summary>
    public static SyncEndpoint Cloud(SyncEndpointKind kind, string connectionId, string? folderId, string? folderPath)
        => Cloud(kind, connectionId, null, folderId, folderPath);

    /// <summary>Creates a cloud endpoint bound to the verified root namespace of its account.</summary>
    public static SyncEndpoint Cloud(SyncEndpointKind kind, string connectionId, string? namespaceId,
        string? folderId, string? folderPath)
    {
        if (SyncEndpointProviders.Get(kind).ProviderId is null)
            throw new ArgumentException("A cloud endpoint requires a registered cloud provider.", nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        if (string.IsNullOrWhiteSpace(folderId) && string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("A cloud endpoint requires a provider folder id or path.", nameof(folderId));
        return new SyncEndpoint(kind, null, null, connectionId,
            string.IsNullOrWhiteSpace(namespaceId) ? null : namespaceId,
            string.IsNullOrWhiteSpace(folderId) ? null : folderId,
            string.IsNullOrWhiteSpace(folderPath) ? null : folderPath);
    }
}

/// <summary>
/// One ordered pair of endpoints plus its direction. Direction is stored separately from endpoint
/// identity: changing the direction never changes which two endpoints the pair names. The concrete
/// source/destination meaning of each direction for local/local and cloud/cloud pairs is deliberately
/// not chosen here; that is a decision for the following packet.
/// </summary>
public sealed record SyncPairEndpoints(SyncEndpoint Left, SyncEndpoint Right, SyncMode Direction);

/// <summary>Validation and canonical comparison for endpoints and pairs.</summary>
public static class SyncEndpoints
{
    /// <summary>Maps a persisted provider literal to an endpoint kind. Blank means the Dropbox default.</summary>
    public static bool TryMapProvider(string? provider, out SyncEndpointKind kind)
    {
        var found = SyncEndpointProviders.TryGet(SyncProviders.Normalize(provider), out var definition);
        kind = definition?.Kind ?? SyncEndpointKind.Local;
        return found;
    }

    /// <summary>The persisted provider literal for a kind. A local endpoint has no provider literal.</summary>
    public static string? ProviderLiteral(SyncEndpointKind kind) => SyncEndpointProviders.Get(kind).ProviderId;

    public static SyncEndpointValidation ValidateLocal(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
            return Refuse(new SyncEndpointRefusal(SyncEndpointError.MissingLocalPath,
                "A local endpoint requires a local folder path."));
        try
        {
            _ = PilotPath.NormalizeLocalRoot(localPath);
        }
        catch (ArgumentException error)
        {
            return Refuse(new SyncEndpointRefusal(SyncEndpointError.InvalidLocalPath, error.Message));
        }
        return SyncEndpointValidation.Accepted;
    }

    public static SyncEndpointValidation ValidateCloud(string? provider, string? connectionId, string? folderId,
        string? folderPath)
    {
        var refusals = new List<SyncEndpointRefusal>();
        if (!TryMapProvider(provider, out _))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.UnknownProvider,
                "This provider is not a supported endpoint kind."));
        if (string.IsNullOrWhiteSpace(connectionId))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.MissingCloudAccount,
                "A cloud endpoint requires the stable connection id of its account."));
        if (string.IsNullOrWhiteSpace(folderId) && string.IsNullOrWhiteSpace(folderPath))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.MissingCloudFolder,
                "A cloud endpoint requires the provider folder id or path of the folder it syncs."));
        return refusals.Count == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(refusals);
    }

    public static bool TryLocal(string? localPath, out SyncEndpoint? endpoint)
    {
        var validation = ValidateLocal(localPath);
        endpoint = validation.IsValid ? SyncEndpoint.Local(localPath!) : null;
        return validation.IsValid;
    }

    public static bool TryCloud(string? provider, string? connectionId, string? folderId, string? folderPath,
        out SyncEndpoint? endpoint)
    {
        var validation = ValidateCloud(provider, connectionId, folderId, folderPath);
        endpoint = validation.IsValid && TryMapProvider(provider, out var kind)
            ? SyncEndpoint.Cloud(kind, connectionId!, folderId, folderPath)
            : null;
        return endpoint is not null;
    }

    /// <summary>The two pairwise rules: distinct endpoints, and non-overlapping local roots.</summary>
    public static SyncEndpointValidation ValidatePair(SyncEndpoint left, SyncEndpoint right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var refusals = new List<SyncEndpointRefusal>();
        if (ReferencesSameStore(left, right))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.SameEndpoint,
                "A pair requires two distinct endpoints; left and right name the same store."));
        if (left.IsLocal && right.IsLocal && LocalRootsOverlap(left.LocalPath!, right.LocalPath!))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.OverlappingLocalRoots,
                "A local/local pair requires two local folders that do not overlap."));
        return refusals.Count == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(refusals);
    }

    public static bool TryPair(SyncEndpoint left, SyncEndpoint right, SyncMode direction,
        out SyncPairEndpoints? pair, out SyncEndpointValidation validation)
    {
        validation = ValidatePair(left, right);
        pair = validation.IsValid ? new SyncPairEndpoints(left, right, direction) : null;
        return validation.IsValid;
    }

    /// <summary>
    /// True when both endpoints name the same object, even through different access accounts or
    /// local path aliases. StoreKey separately retains the account-bound access route.
    /// </summary>
    public static bool ReferencesSameStore(SyncEndpoint left, SyncEndpoint right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Kind != right.Kind) return false;
        if (left.IsLocal && left.LocalIdentity is { } first && right.LocalIdentity is { } second)
            return first == second;
        if (SyncEndpointProviders.Get(left.Kind).ObjectIdsAreProviderGlobal &&
            !string.IsNullOrWhiteSpace(left.FolderId) && !string.IsNullOrWhiteSpace(right.FolderId))
            return string.Equals(left.FolderId, right.FolderId, StringComparison.Ordinal);
        return string.Equals(StoreKey(left), StoreKey(right), StringComparison.Ordinal);
    }

    /// <summary>The canonical comparison key for one endpoint.</summary>
    public static string StoreKey(SyncEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.IsLocal) return "local|" + LocalComparisonKey(endpoint.LocalPath!);
        var folder = string.IsNullOrWhiteSpace(endpoint.FolderId)
            ? "path:" + (endpoint.FolderPath ?? string.Empty).ToUpperInvariant()
            : "id:" + endpoint.FolderId;
        return ProviderLiteral(endpoint.Kind) + "|" + endpoint.ConnectionId + "|" + folder;
    }

    /// <summary>True when two local folders are the same folder or one contains the other.</summary>
    public static bool LocalRootsOverlap(string left, string right)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(left);
        ArgumentException.ThrowIfNullOrWhiteSpace(right);
        var first = LocalComparisonKey(left);
        var second = LocalComparisonKey(right);
        if (string.Equals(first, second, StringComparison.Ordinal)) return true;
        var separator = Path.DirectorySeparatorChar;
        return first.StartsWith(second + separator, StringComparison.Ordinal) ||
               second.StartsWith(first + separator, StringComparison.Ordinal);
    }

    private static string LocalComparisonKey(string localPath) => Trim(localPath).ToUpperInvariant();

    private static string Trim(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static SyncEndpointValidation Refuse(SyncEndpointRefusal refusal) => new(new[] { refusal });
}

/// <summary>
/// The lossless mapping between the saved SyncPair shape and the symmetric endpoint model. The legacy
/// shape keeps a local folder plus one cloud selection spread across LocalRoot, Provider,
/// ConnectionId, DropboxRoot and DropboxFolderId; this maps those exact fields, and the reverse
/// write-back only rewrites those same fields on the caller's own template, so no other saved field is
/// touched and no saved production file is rewritten by this packet.
///
/// The legacy ordered pair is (Left = local endpoint, Right = cloud endpoint).
/// </summary>
public static class SyncEndpointsCompatibility
{
    /// <summary>Maps one saved pair. Null endpoints mean an identity could not be formed at all.</summary>
    public static SyncLegacyEndpoints FromSyncPair(SyncPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        var refusals = new List<SyncEndpointRefusal>();

        var localValidation = SyncEndpoints.ValidateLocal(pair.LocalRoot);
        // The saved pair already carries its local root identity, so the local endpoint binds it and a
        // persisted endpoint can prove it still names the same folder. An incomplete legacy identity is
        // left null here and refused by SyncEndpointContracts.ValidateRecord rather than invented.
        var local = localValidation.IsValid
            ? SyncEndpoint.Local(pair.LocalRoot,
                SyncLocalEndpointIdentity.From(pair.LocalVolumeSerialNumber, pair.LocalRootFileId))
            : null;
        refusals.AddRange(localValidation.Refusals);

        var cloudValidation = SyncEndpoints.ValidateCloud(pair.Provider, pair.ConnectionId, pair.DropboxFolderId,
            pair.DropboxRoot);
        var cloud = cloudValidation.IsValid && SyncEndpoints.TryMapProvider(pair.Provider, out var kind)
            ? SyncEndpoint.Cloud(kind, pair.ConnectionId, pair.DropboxFolderId, pair.DropboxRoot)
            : null;
        refusals.AddRange(cloudValidation.Refusals);

        SyncPairEndpoints? endpoints = local is not null && cloud is not null
            ? new SyncPairEndpoints(local, cloud, pair.Direction)
            : null;
        if (endpoints is not null)
            refusals.AddRange(SyncEndpoints.ValidatePair(endpoints.Left, endpoints.Right).Refusals);
        return new SyncLegacyEndpoints(pair, endpoints,
            refusals.Count == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(refusals));
    }

    /// <summary>
    /// Writes the mapped endpoints back onto the caller's own template. Only the fields this mapping
    /// owns are rewritten. A provider literal that already normalizes to the endpoint kind is retained
    /// exactly, so a saved empty or differently-cased Dropbox literal stays unchanged.
    /// </summary>
    public static SyncPair ToSyncPair(SyncPairEndpoints endpoints, SyncPair template)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(template);
        if (!endpoints.Left.IsLocal || !endpoints.Right.IsCloud)
            throw new InvalidDataException("The legacy compatibility mapping requires a local left endpoint and a cloud right endpoint.");
        var literal = SyncEndpoints.ProviderLiteral(endpoints.Right.Kind) ??
            throw new InvalidDataException("A local endpoint has no persisted provider literal.");
        return template with
        {
            LocalRoot = endpoints.Left.LocalPath!,
            LocalVolumeSerialNumber = endpoints.Left.LocalIdentity?.VolumeSerialNumber ?? template.LocalVolumeSerialNumber,
            LocalRootFileId = endpoints.Left.LocalIdentity?.RootFileId ?? template.LocalRootFileId,
            Provider = SyncEndpoints.TryMapProvider(template.Provider, out var templateKind) && templateKind == endpoints.Right.Kind
                ? template.Provider
                : literal,
            ConnectionId = endpoints.Right.ConnectionId!,
            DropboxRoot = endpoints.Right.FolderPath ?? template.DropboxRoot,
            DropboxFolderId = endpoints.Right.FolderId ?? template.DropboxFolderId,
            Direction = endpoints.Direction
        };
    }
}

/// <summary>One mapped saved pair together with its validation outcome.</summary>
public sealed record SyncLegacyEndpoints(SyncPair Legacy, SyncPairEndpoints? Endpoints, SyncEndpointValidation Validation);

// ===================================================================================================
// E1 contract: the frozen interface for the later E2 editor and E3 runtime packets.
//
// Scope: the immutable endpoint value model, the versioned additive persistence and compatibility
// layer, and endpoint, pair and cross-pair validation shared by the editor and runtime. Legacy
// fields retain their meaning; explicit endpoint documents must never silently override them.
// ===================================================================================================

/// <summary>The stable local folder identity a persisted local endpoint must carry.</summary>
public sealed record SyncLocalEndpointIdentity(uint VolumeSerialNumber, ulong RootFileId)
{
    /// <summary>The identity, or null when either component is zero (an incomplete legacy identity).</summary>
    public static SyncLocalEndpointIdentity? From(uint volumeSerialNumber, ulong rootFileId) =>
        volumeSerialNumber == 0 || rootFileId == 0
            ? null
            : new SyncLocalEndpointIdentity(volumeSerialNumber, rootFileId);

    public bool IsComplete => VolumeSerialNumber != 0 && RootFileId != 0;
}

/// <summary>The persisted endpoint document versions this build understands.</summary>
public static class SyncEndpointSchema
{
    /// <summary>A saved pair with no endpoint document: Left is the local endpoint, Right the cloud endpoint.</summary>
    public const int LegacyImplicit = 1;

    /// <summary>An explicit symmetric left/right endpoint document.</summary>
    public const int SymmetricEndpoints = 2;

    public const int Current = SymmetricEndpoints;

    public const string VersionMember = "endpointSchemaVersion";
    public const string LeftMember = "leftEndpoint";
    public const string RightMember = "rightEndpoint";
}

/// <summary>
/// One endpoint exactly as it is persisted. Persisted identity is deliberately wider than the
/// in-memory value model: a local record carries its stable local folder identity, and a cloud record
/// carries provider, account, verified namespace and the stable provider folder identity.
/// </summary>
[JsonConverter(typeof(SyncEndpointRecordJsonConverter))]
public sealed record SyncEndpointRecord(
    SyncEndpointKind Kind,
    string? LocalPath = null,
    uint? LocalVolumeSerialNumber = null,
    ulong? LocalRootFileId = null,
    string? Provider = null,
    string? ConnectionId = null,
    string? NamespaceId = null,
    string? FolderId = null,
    string? FolderPath = null)
{
    public bool IsLocal => Kind == SyncEndpointKind.Local;

    /// <summary>The persisted record for one endpoint.</summary>
    public static SyncEndpointRecord From(SyncEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.IsLocal
            ? new SyncEndpointRecord(SyncEndpointKind.Local, endpoint.LocalPath,
                endpoint.LocalIdentity?.VolumeSerialNumber, endpoint.LocalIdentity?.RootFileId)
            : new SyncEndpointRecord(endpoint.Kind, Provider: SyncEndpoints.ProviderLiteral(endpoint.Kind),
                ConnectionId: endpoint.ConnectionId, NamespaceId: endpoint.NamespaceId,
                FolderId: endpoint.FolderId, FolderPath: endpoint.FolderPath);
    }
}

/// <summary>
/// The persisted endpoint document. Direction is deliberately absent: it remains in its existing saved
/// member, so a direction change can never create a second source of truth for the pair.
/// </summary>
public sealed record SyncEndpointDocument(int Version, SyncEndpointRecord? Left, SyncEndpointRecord? Right);

/// <summary>
/// The versioned additive persistence and readback contract. Writing touches only the three endpoint
/// members of the caller's own saved object, so every other saved member, including the legacy pair
/// fields, is left exactly as it was and a build that does not know this document still reads the
/// legacy pair unchanged.
/// </summary>
public static class SyncEndpointPersistence
{
    /// <summary>The current-version document for one pair.</summary>
    public static SyncEndpointDocument Symmetric(SyncPairEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return new SyncEndpointDocument(SyncEndpointSchema.SymmetricEndpoints,
            SyncEndpointRecord.From(endpoints.Left), SyncEndpointRecord.From(endpoints.Right));
    }

    /// <summary>
    /// Writes the endpoint document additively onto the caller's own saved pair object. Only the
    /// version member and the two endpoint members are created or replaced, and each endpoint object is
    /// rebuilt, so a member that no longer applies is removed rather than left stale.
    /// </summary>
    public static void WriteAdditive(JsonObject savedPair, SyncPairEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(savedPair);
        ArgumentNullException.ThrowIfNull(endpoints);
        savedPair[SyncEndpointSchema.VersionMember] = JsonValue.Create(SyncEndpointSchema.SymmetricEndpoints);
        savedPair[SyncEndpointSchema.LeftMember] = WriteRecord(endpoints.Left);
        savedPair[SyncEndpointSchema.RightMember] = WriteRecord(endpoints.Right);
    }

    /// <summary>
    /// Reads the endpoint document from one saved pair object. An absent document is the legacy shape
    /// and is accepted as version 1 with no records, because the existing saved members keep their
    /// original meaning; a document of an unknown version is refused instead of guessed.
    /// </summary>
    public static SyncEndpointValidation TryRead(JsonObject? savedPair, out SyncEndpointDocument? document)
    {
        document = null;
        if (savedPair is null)
            return Refuse(SyncEndpointError.MissingEndpointDocument, "No saved pair document was supplied.");
        if (!savedPair.TryGetPropertyValue(SyncEndpointSchema.VersionMember, out var versionNode) ||
            versionNode is null)
        {
            if (savedPair[SyncEndpointSchema.LeftMember] is not null ||
                savedPair[SyncEndpointSchema.RightMember] is not null)
                return Refuse(SyncEndpointError.InvalidSchemaDocument,
                    "Explicit endpoints require their saved endpoint schema version.");
            document = new SyncEndpointDocument(SyncEndpointSchema.LegacyImplicit, null, null);
            return SyncEndpointValidation.Accepted;
        }
        if (versionNode is not JsonValue versionValue || !versionValue.TryGetValue<int>(out var version))
            return Refuse(SyncEndpointError.InvalidSchemaDocument,
                "The saved endpoint schema version is not an integer.");
        if (version != SyncEndpointSchema.SymmetricEndpoints)
            return Refuse(SyncEndpointError.UnsupportedSchemaVersion,
                "The saved endpoint schema version " + version + " is not understood by this build.");
        if (!TryReadRecord(savedPair, SyncEndpointSchema.LeftMember, out var left, out var leftRefusal))
            return new SyncEndpointValidation(new[] { leftRefusal! });
        if (!TryReadRecord(savedPair, SyncEndpointSchema.RightMember, out var right, out var rightRefusal))
            return new SyncEndpointValidation(new[] { rightRefusal! });
        document = new SyncEndpointDocument(version, left, right);
        return SyncEndpointValidation.Accepted;
    }

    internal static bool TryReadRecord(JsonObject parent, string member, out SyncEndpointRecord? record,
        out SyncEndpointRefusal? refusal)
    {
        record = null;
        refusal = null;
        if (!parent.TryGetPropertyValue(member, out var node) || node is not JsonObject endpoint)
        {
            refusal = new SyncEndpointRefusal(SyncEndpointError.InvalidSchemaDocument,
                "The saved endpoint member '" + member + "' is missing or is not an object.");
            return false;
        }
        var kindLiteral = ReadString(endpoint, "kind");
        SyncEndpointKind kind;
        if (string.Equals(kindLiteral, "local", StringComparison.Ordinal))
        {
            kind = SyncEndpointKind.Local;
        }
        else if (!SyncEndpoints.TryMapProvider(kindLiteral, out kind))
        {
            refusal = new SyncEndpointRefusal(SyncEndpointError.UnknownProvider,
                "The saved endpoint member '" + member + "' names an unsupported endpoint kind.");
            return false;
        }
        record = new SyncEndpointRecord(kind, ReadString(endpoint, "localPath"),
            ReadUInt(endpoint, "volumeSerialNumber"), ReadULong(endpoint, "rootFileId"),
            ReadString(endpoint, "provider"), ReadString(endpoint, "connectionId"),
            ReadString(endpoint, "namespaceId"), ReadString(endpoint, "folderId"),
            ReadString(endpoint, "folderPath"));
        return true;
    }

    internal static JsonObject WriteRecord(SyncEndpoint endpoint)
    {
        var record = new JsonObject { ["kind"] = KindLiteral(endpoint.Kind) };
        SetMember(record, "localPath", endpoint.LocalPath);
        if (endpoint.LocalIdentity is { } identity)
        {
            record["volumeSerialNumber"] = JsonValue.Create(identity.VolumeSerialNumber);
            record["rootFileId"] = JsonValue.Create(identity.RootFileId);
        }
        SetMember(record, "provider", SyncEndpoints.ProviderLiteral(endpoint.Kind));
        SetMember(record, "connectionId", endpoint.ConnectionId);
        SetMember(record, "namespaceId", endpoint.NamespaceId);
        SetMember(record, "folderId", endpoint.FolderId);
        SetMember(record, "folderPath", endpoint.FolderPath);
        return record;
    }

    private static void SetMember(JsonObject target, string member, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) target.Remove(member);
        else target[member] = JsonValue.Create(value);
    }

    private static string? ReadString(JsonObject parent, string member) =>
        parent.TryGetPropertyValue(member, out var node) && node is JsonValue value &&
        value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static uint? ReadUInt(JsonObject parent, string member) =>
        parent.TryGetPropertyValue(member, out var node) && node is JsonValue value &&
        value.TryGetValue<uint>(out var number) ? number : null;

    private static ulong? ReadULong(JsonObject parent, string member) =>
        parent.TryGetPropertyValue(member, out var node) && node is JsonValue value &&
        value.TryGetValue<ulong>(out var number) ? number : null;

    private static string KindLiteral(SyncEndpointKind kind) =>
        SyncEndpointProviders.Get(kind).ProviderId ?? SyncProviders.Local;

    private static SyncEndpointValidation Refuse(SyncEndpointError error, string reason) =>
        new(new[] { new SyncEndpointRefusal(error, reason) });
}

/// <summary>
/// The typed product model uses the same explicit endpoint wire representation as the additive
/// migration contract. There is no second enum/name/identity serialization to keep in step.
/// </summary>
internal sealed class SyncEndpointRecordJsonConverter : JsonConverter<SyncEndpointRecord>
{
    public override SyncEndpointRecord Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        var value = JsonNode.Parse(ref reader);
        var parent = new JsonObject { [SyncEndpointSchema.LeftMember] = value };
        if (!SyncEndpointPersistence.TryReadRecord(parent, SyncEndpointSchema.LeftMember,
                out var record, out var refusal))
            throw new JsonException(refusal?.Reason ?? "The saved endpoint is malformed.");
        var validation = SyncEndpointContracts.ValidateRecord(record!);
        if (!validation.IsValid) throw new JsonException(validation.Describe());
        return record!;
    }

    public override void Write(Utf8JsonWriter writer, SyncEndpointRecord value, JsonSerializerOptions options)
    {
        var validation = SyncEndpointContracts.ValidateRecord(value);
        if (!validation.IsValid) throw new JsonException(validation.Describe());
        SyncEndpointPersistence.WriteRecord(SyncEndpointContracts.ToEndpoint(value)).WriteTo(writer, options);
    }
}

/// <summary>The three-valued result of comparing two cloud endpoints for overlap.</summary>
public enum SyncCloudOverlap
{
    /// <summary>The endpoints cannot name the same store or nested folders.</summary>
    Distinct = 0,

    /// <summary>The endpoints name the same folder, or one folder contains the other.</summary>
    Overlapping = 1,

    /// <summary>Ancestry or ownership cannot be established from the supplied identities.</summary>
    Unverified = 2
}

/// <summary>
/// Cloud and local overlap comparison. A cloud comparison uses the provider, the verified account
/// namespace and the stable folder identity or hierarchical provider path; it never compares display
/// names alone, never treats equal names on different accounts as equal, and fails closed with an
/// explicit result when ancestry cannot be established.
/// </summary>
public static class SyncEndpointOverlap
{
    /// <summary>Compares two endpoints for overlap. Two local endpoints are never cloud-compared here.</summary>
    public static SyncCloudOverlap Compare(SyncEndpoint left, SyncEndpoint right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.IsLocal || right.IsLocal || left.Kind != right.Kind) return SyncCloudOverlap.Distinct;

        // A shared object's id does not change when another account gains access. Check it BEFORE
        // comparing account namespaces; those are access routes, not disjointness evidence.
        if (SyncEndpoints.ReferencesSameStore(left, right)) return SyncCloudOverlap.Overlapping;
        if (!SyncEndpointProviders.Get(left.Kind).PathsDescribeAncestry)
            return SyncCloudOverlap.Unverified;

        var leftNamespace = string.IsNullOrWhiteSpace(left.NamespaceId) ? null : left.NamespaceId;
        var rightNamespace = string.IsNullOrWhiteSpace(right.NamespaceId) ? null : right.NamespaceId;
        if (leftNamespace is not null && rightNamespace is not null)
        {
            // Two verified namespaces separate equal folder identities; one shared namespace does not.
            if (!string.Equals(leftNamespace, rightNamespace, StringComparison.Ordinal))
                return SyncCloudOverlap.Distinct;
        }
        else if (!string.Equals(left.ConnectionId, right.ConnectionId, StringComparison.Ordinal))
        {
            // While neither namespace is verified, the account identity is the only separation a legacy
            // pair can offer. This preserves the existing same-named-folders-on-two-accounts rule.
            return SyncCloudOverlap.Distinct;
        }

        if (!string.IsNullOrWhiteSpace(left.FolderId) && !string.IsNullOrWhiteSpace(right.FolderId) &&
            string.Equals(left.FolderId, right.FolderId, StringComparison.Ordinal))
            return SyncCloudOverlap.Overlapping;

        if (!string.IsNullOrWhiteSpace(left.FolderPath) && !string.IsNullOrWhiteSpace(right.FolderPath))
            return FolderPathsOverlap(left.FolderPath!, right.FolderPath!)
                ? SyncCloudOverlap.Overlapping
                : SyncCloudOverlap.Distinct;

        // Two different opaque folder ids with no provider path: ancestry cannot be established, so the
        // comparison refuses rather than assuming the folders are separate.
        return SyncCloudOverlap.Unverified;
    }

    /// <summary>The refusal for one pair, or an accepted validation when the endpoints are distinct.</summary>
    public static SyncEndpointValidation ValidateCloudOverlap(SyncEndpoint left, SyncEndpoint right)
    {
        switch (Compare(left, right))
        {
            case SyncCloudOverlap.Overlapping:
                return Refuse(SyncEndpointError.OverlappingCloudFolders,
                    "The two cloud endpoints name the same folder, or one folder contains the other, on one account.");
            case SyncCloudOverlap.Unverified:
                return Refuse(SyncEndpointError.UnverifiedCloudNamespace,
                    "Cloud folder ancestry cannot be established from the supplied endpoint identities; the pair is refused rather than guessed.");
            default:
                return SyncEndpointValidation.Accepted;
        }
    }

    /// <summary>True when two hierarchical provider folder paths are equal or one contains the other.</summary>
    public static bool FolderPathsOverlap(string left, string right)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(left);
        ArgumentException.ThrowIfNullOrWhiteSpace(right);
        var first = FolderPathKey(left);
        var second = FolderPathKey(right);
        if (string.Equals(first, second, StringComparison.Ordinal)) return true;
        return first.StartsWith(second + "/", StringComparison.Ordinal) ||
               second.StartsWith(first + "/", StringComparison.Ordinal);
    }

    private static string FolderPathKey(string value)
    {
        var normalized = "/" + string.Join('/',
            value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        return normalized.TrimEnd('/').ToUpperInvariant();
    }

    private static SyncEndpointValidation Refuse(SyncEndpointError error, string reason) =>
        new(new[] { new SyncEndpointRefusal(error, reason) });
}

/// <summary>
/// The validation entry points E1 owns and freezes for the later editor and runtime packets: one
/// persisted endpoint record, one pair, and a candidate pair against the pairs already retained.
/// </summary>
public static class SyncEndpointContracts
{
    /// <summary>Validates one persisted endpoint record, including the identity it must carry.</summary>
    public static SyncEndpointValidation ValidateRecord(SyncEndpointRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var refusals = new List<SyncEndpointRefusal>();
        if (record.IsLocal)
        {
            refusals.AddRange(SyncEndpoints.ValidateLocal(record.LocalPath).Refusals);
            if (record.LocalVolumeSerialNumber is null || record.LocalRootFileId is null)
                refusals.Add(new SyncEndpointRefusal(SyncEndpointError.MissingLocalIdentity,
                    "A persisted local endpoint must carry its stable local volume serial and root file id."));
            else if (record.LocalVolumeSerialNumber == 0 || record.LocalRootFileId == 0)
                refusals.Add(new SyncEndpointRefusal(SyncEndpointError.InvalidLocalIdentity,
                    "A persisted local endpoint identity is incomplete: both the volume serial and the root file id are required."));
            if (!string.IsNullOrWhiteSpace(record.Provider) || !string.IsNullOrWhiteSpace(record.ConnectionId) ||
                !string.IsNullOrWhiteSpace(record.NamespaceId) || !string.IsNullOrWhiteSpace(record.FolderId) ||
                !string.IsNullOrWhiteSpace(record.FolderPath))
                refusals.Add(new SyncEndpointRefusal(SyncEndpointError.EndpointKindMismatch,
                    "A local endpoint cannot carry cloud provider, account or folder identity."));
            return Conclude(refusals);
        }

        if (!SyncEndpointProviders.TryGet(record.Provider, out var definition))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.UnknownProvider,
                "The persisted endpoint provider is not a supported endpoint kind."));
        else if (definition!.Kind != record.Kind)
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.EndpointKindMismatch,
                "The persisted endpoint provider does not match its endpoint kind."));
        refusals.AddRange(SyncEndpoints.ValidateCloud(record.Provider, record.ConnectionId, record.FolderId,
            record.FolderPath).Refusals);
        if (!IsAcceptedFolderIdentity(record.Kind, record.FolderId))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.InvalidCloudFolderIdentity,
                "The persisted cloud folder id is not in the accepted shape for its provider."));
        if (!string.IsNullOrWhiteSpace(record.LocalPath) || record.LocalVolumeSerialNumber is not null ||
            record.LocalRootFileId is not null)
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.EndpointKindMismatch,
                "A cloud endpoint cannot carry local folder identity."));
        return Conclude(refusals);
    }

    /// <summary>The local identity rule on its own, for a draft endpoint before it is persisted.</summary>
    public static SyncEndpointValidation ValidateLocalIdentity(SyncEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsLocal) return SyncEndpointValidation.Accepted;
        if (endpoint.LocalIdentity is null)
            return Refuse(SyncEndpointError.MissingLocalIdentity,
                "A local endpoint must carry its stable local volume serial and root file id before it is persisted.");
        return endpoint.LocalIdentity.IsComplete
            ? SyncEndpointValidation.Accepted
            : Refuse(SyncEndpointError.InvalidLocalIdentity,
                "The local endpoint identity is incomplete: both the volume serial and the root file id are required.");
    }

    /// <summary>One pair: two distinct endpoints, non-overlapping local roots, no cloud overlap.</summary>
    public static SyncEndpointValidation ValidatePair(SyncEndpoint left, SyncEndpoint right)
    {
        var refusals = new List<SyncEndpointRefusal>(SyncEndpoints.ValidatePair(left, right).Refusals);
        if (!SyncEndpoints.ReferencesSameStore(left, right))
            refusals.AddRange(SyncEndpointOverlap.ValidateCloudOverlap(left, right).Refusals);
        return Conclude(refusals);
    }

    /// <summary>
    /// One candidate pair against the pairs already retained: an endpoint already retained by another
    /// pair, or one that contains or is contained by another pair's endpoint on the same store, is
    /// refused before any effect.
    /// </summary>
    public static SyncEndpointValidation ValidatePairAgainstExisting(SyncEndpoint left, SyncEndpoint right,
        IEnumerable<SyncPairEndpoints>? existingPairs)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var refusals = new List<SyncEndpointRefusal>();
        foreach (var existing in (existingPairs ?? Array.Empty<SyncPairEndpoints>()).Where(item => item is not null))
        foreach (var candidate in new[] { left, right })
        foreach (var retained in new[] { existing.Left, existing.Right })
        {
            if (candidate.IsLocal != retained.IsLocal) continue;
            if (candidate.IsLocal)
            {
                if (SameLocalStore(candidate, retained))
                    refusals.Add(new SyncEndpointRefusal(SyncEndpointError.DuplicateEndpointAcrossPairs,
                        "Another retained pair already syncs this exact local folder."));
                else if (SyncEndpoints.LocalRootsOverlap(candidate.LocalPath!, retained.LocalPath!))
                    refusals.Add(new SyncEndpointRefusal(SyncEndpointError.OverlappingEndpointAcrossPairs,
                        "Another retained pair syncs a local folder that contains, or is contained by, this one."));
                continue;
            }
            if (candidate.Kind != retained.Kind) continue;
            if (SyncEndpoints.ReferencesSameStore(candidate, retained))
            {
                refusals.Add(new SyncEndpointRefusal(SyncEndpointError.DuplicateEndpointAcrossPairs,
                    "Another retained pair already syncs this exact cloud folder."));
                continue;
            }
            switch (SyncEndpointOverlap.Compare(candidate, retained))
            {
                case SyncCloudOverlap.Overlapping:
                    refusals.Add(new SyncEndpointRefusal(SyncEndpointError.OverlappingEndpointAcrossPairs,
                        "Another retained pair syncs a cloud folder that contains, or is contained by, this one."));
                    break;
                case SyncCloudOverlap.Unverified:
                    refusals.Add(new SyncEndpointRefusal(SyncEndpointError.UnverifiedCloudNamespace,
                        "Cloud folder ancestry against a retained pair cannot be established from the supplied identities; the pair is refused rather than guessed."));
                    break;
            }
        }
        return Conclude(refusals);
    }

    /// <summary>
    /// The policy-aware cross-pair rule for a complete candidate pair: it carries the candidate's and the
    /// retained pairs' own persisted selection/ignore policy, so a strict nested relation may be admitted
    /// only when the enclosing pair proves the whole nested subtree stays excluded on every side where the
    /// two pairs are nested. Equal roots, unproven nesting, reserved control/recovery ownership and any
    /// unverified relation stay refused. Endpoint-only callers keep the strict overload above; this method
    /// is the only cross-pair entry point that may admit a nested partition.
    /// </summary>
    public static SyncEndpointValidation ValidatePairAgainstExisting(SyncPair candidate,
        IEnumerable<SyncPair>? retainedPairs)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var resolved = SyncCloudPairConfiguration.SavedEndpoints(candidate);
        if (resolved is null)
            return new SyncEndpointValidation(new[]
            {
                new SyncEndpointRefusal(SyncEndpointError.InvalidSchemaDocument,
                    "The candidate pair endpoints could not be resolved from its own retained definition.")
            });
        var refusals = new List<SyncEndpointRefusal>();
        foreach (var retained in (retainedPairs ?? Array.Empty<SyncPair>()).Where(item => item is not null))
        {
            if (string.Equals(retained.Id, candidate.Id, StringComparison.Ordinal)) continue;
            var relation = SyncPairPartitionPolicy.Decide(candidate, retained);
            if (relation == SyncPairPartitionRelation.NestedRefused &&
                SyncPairPartitionPolicy.DecideCorrespondingIncluded(candidate, retained) ==
                    SyncPairPartitionRelation.CorrespondingIncluded)
                continue;
            switch (relation)
            {
                case SyncPairPartitionRelation.Distinct:
                case SyncPairPartitionRelation.NestedAllowed:
                    break;
                case SyncPairPartitionRelation.Same:
                    refusals.Add(new SyncEndpointRefusal(SyncEndpointError.DuplicateEndpointAcrossPairs,
                        "Another retained pair already syncs this exact local or cloud store."));
                    break;
                case SyncPairPartitionRelation.NestedRefused:
                    refusals.Add(new SyncEndpointRefusal(SyncEndpointError.OverlappingEndpointAcrossPairs,
                        "Another retained pair syncs a folder that contains, or is contained by, this one and the enclosing pair does not exclude the whole nested subtree."));
                    break;
                default:
                    refusals.Add(new SyncEndpointRefusal(SyncEndpointError.UnverifiedCloudNamespace,
                        "The relation to another retained pair cannot be established from the supplied identities; the pair is refused rather than guessed."));
                    break;
            }
        }
        return Conclude(refusals);
    }

    /// <summary>
    /// Builds the pair from two persisted records. Both records must already validate; an invalid record
    /// returns its refusals without constructing endpoints.
    /// </summary>
    public static bool TryCreate(SyncEndpointRecord left, SyncEndpointRecord right, SyncMode direction,
        out SyncPairEndpoints? pair, out SyncEndpointValidation validation)
    {
        validation = SyncEndpointValidation.Combine(ValidateRecord(left), ValidateRecord(right));
        if (!validation.IsValid)
        {
            pair = null;
            return false;
        }
        var created = new SyncPairEndpoints(ToEndpoint(left), ToEndpoint(right), direction);
        validation = ValidatePair(created.Left, created.Right);
        if (!validation.IsValid)
        {
            pair = null;
            return false;
        }
        pair = created;
        return true;
    }

    /// <summary>Converts one already-validated persisted record to the immutable value model.</summary>
    public static SyncEndpoint ToEndpoint(SyncEndpointRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.IsLocal
            ? SyncEndpoint.Local(record.LocalPath!,
                SyncLocalEndpointIdentity.From(record.LocalVolumeSerialNumber ?? 0, record.LocalRootFileId ?? 0))
            : SyncEndpoint.Cloud(record.Kind, record.ConnectionId!, record.NamespaceId, record.FolderId,
                record.FolderPath);
    }

    /// <summary>True when two local endpoints name the same folder: by identity first, then by path.</summary>
    public static bool SameLocalStore(SyncEndpoint left, SyncEndpoint right)
    {
        if (left.LocalIdentity is { } leftIdentity && right.LocalIdentity is { } rightIdentity)
            return leftIdentity == rightIdentity;
        return string.Equals(SyncEndpoints.StoreKey(left), SyncEndpoints.StoreKey(right), StringComparison.Ordinal);
    }

    private static bool IsAcceptedFolderIdentity(SyncEndpointKind kind, string? folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId)) return true;
        return kind switch
        {
            SyncEndpointKind.Dropbox => PilotPath.IsDropboxFolderIdentity(folderId),
            SyncEndpointKind.GoogleDrive => IsOpaqueDriveIdentity(folderId),
            _ => false
        };
    }

    // Mirrors the existing SyncState.NormalizePair rule for opaque Google Drive folder ids: a stable
    // object id, never the "root" alias, a path or a URL.
    private static bool IsOpaqueDriveIdentity(string id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 1024 && id != "root" &&
        !id.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) ||
                             character is '/' or '\\' or '?' or '#');

    private static SyncEndpointValidation Conclude(List<SyncEndpointRefusal> refusals) =>
        refusals.Count == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(refusals);

    private static SyncEndpointValidation Refuse(SyncEndpointError error, string reason) =>
        new(new[] { new SyncEndpointRefusal(error, reason) });
}

/// <summary>One account binding the product has already verified for this installation.</summary>
public sealed record SyncVerifiedAccountBinding(string Provider, string ConnectionId, string? NamespaceId,
    bool IsLegacyPrimary);

/// <summary>
/// The verified account bindings available to a resolution. The implementation supplies only accounts
/// the product itself verified; "the first available account" is never a candidate.
/// </summary>
public interface ISyncVerifiedAccountBindings
{
    IReadOnlyList<SyncVerifiedAccountBinding> CandidatesFor(string provider);
}

/// <summary>One account resolution: the resolved connection id, or the exact refusal.</summary>
public sealed record SyncAccountBindingResolution(string? ConnectionId, SyncEndpointValidation Validation)
{
    public bool IsResolved => ConnectionId is not null;
}

/// <summary>
/// The legacy account rule. An empty saved ConnectionId resolves only through the single verified
/// legacy primary account binding; several candidates are ambiguous and none is unavailable, so the
/// pair stays disabled with an actionable reason instead of being assigned an account.
/// </summary>
public static class SyncAccountBindings
{
    public static SyncAccountBindingResolution Resolve(SyncPair legacyPair, ISyncVerifiedAccountBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(legacyPair);
        ArgumentNullException.ThrowIfNull(bindings);
        var provider = SyncProviders.Normalize(legacyPair.Provider);
        var candidates = bindings.CandidatesFor(provider) ?? Array.Empty<SyncVerifiedAccountBinding>();
        var saved = string.IsNullOrWhiteSpace(legacyPair.ConnectionId) ? null : legacyPair.ConnectionId.Trim();
        if (saved is not null)
            return candidates.Any(item => string.Equals(item.ConnectionId, saved, StringComparison.Ordinal))
                ? new SyncAccountBindingResolution(saved, SyncEndpointValidation.Accepted)
                : new SyncAccountBindingResolution(null, Refuse(SyncEndpointError.UnavailableAccountBinding,
                    "The account saved by this pair is not a verified account binding on this installation."));
        var legacyPrimary = candidates.Where(item => item.IsLegacyPrimary).ToArray();
        if (legacyPrimary.Length == 1)
            return new SyncAccountBindingResolution(legacyPrimary[0].ConnectionId, SyncEndpointValidation.Accepted);
        if (legacyPrimary.Length == 0)
            return new SyncAccountBindingResolution(null, Refuse(SyncEndpointError.UnavailableAccountBinding,
                "This pair saved no account id and no verified legacy primary account binding exists for its provider."));
        return new SyncAccountBindingResolution(null, Refuse(SyncEndpointError.AmbiguousAccountBinding,
            "This pair saved no account id and more than one verified legacy primary account binding exists; no account is chosen automatically."));
    }

    private static SyncEndpointValidation Refuse(SyncEndpointError error, string reason) =>
        new(new[] { new SyncEndpointRefusal(error, reason) });
}
