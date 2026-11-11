using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark;

// CC1: the one shared cloud/cloud pair configuration and compatibility-projection contract.
//
// A cloud/cloud pair is persisted as an explicit symmetric endpoint document whose two sides are both
// cloud endpoints. Every local-containing pair keeps its existing validation, identity derivation and
// field projection byte semantics; the members added here apply only to a document that declares two
// cloud sides. For such a pair the legacy saved members carry a compatibility projection in which
// LocalRoot is exactly empty and both numeric local identity fields are exactly zero, and the retained
// cloud projection (provider, connection, folder path and folder id) agrees with the document's right
// endpoint. Those two absent values are never handed to a filesystem API, and this helper never calls
// local-path normalization, the physical engine layout or ToPilotSettings.
//
// This is STRUCTURAL configuration validation, not runtime root-relationship authorization. It decides
// whether a persisted cloud/cloud record is internally consistent enough to retain as configuration. It
// does NOT establish that the two provider roots are disjoint, that any folder hierarchy exists, or that
// recovery containment holds, and ignoring an unverified provider relationship here is not an assertion
// of disjointness. Those live checks are mandatory in the Host activation work before any sync effect.
//
// Runtime execution is deliberately not enabled here: SyncEndpointPairRuntime.BindingSupportReason and
// the UI/CLI activation of a cloud/cloud pair remain unavailable until the later execution packet, and
// this helper never consults or relaxes that gate. SyncPairStore consumes it today; the binding host
// consumes the same entry points when the cloud runtime is implemented.
public static class SyncCloudPairConfiguration
{
    /// <summary>The versioned identity scheme of a cloud/cloud pair id.</summary>
    public const string IdentityScheme = "syncshark.cloud-pair.v1";

    /// <summary>
    /// True when the pair claims two cloud endpoints. A document that names a local side is never a
    /// cloud/cloud claim, and a pair with no endpoint document is a legacy record. A document that
    /// declares no local side but is present, unsupported-versioned or missing a member is still a
    /// cloud/cloud claim so that it fails closed as one instead of being read as an incomplete local pair.
    /// </summary>
    public static bool IsCloudOnly(SyncPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        var document = pair.EndpointDocument;
        if (document is null) return false;
        if (document.Left?.IsLocal == true || document.Right?.IsLocal == true) return false;
        return document.Version != SyncEndpointSchema.LegacyImplicit || document.Left is not null ||
            document.Right is not null;
    }

    /// <summary>
    /// The deterministic 20-character lowercase pair id of an ordered cloud/cloud document. The hashed
    /// tuple is the document scheme tag followed by the length-prefixed provider, connection, verified
    /// namespace and stable folder identity of the left endpoint and then of the right endpoint, so the
    /// encoding is unambiguous. Display name, direction, policy and mutable folder path are deliberately
    /// absent: changing one of those never changes the identity, and swapping the sides, changing an
    /// account or changing a verified namespace always does.
    /// </summary>
    public static string DeriveId(SyncPairEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return DeriveId(SyncEndpointRecord.From(endpoints.Left), SyncEndpointRecord.From(endpoints.Right));
    }

    /// <summary>The same derivation over the two persisted endpoint records, in left/right order.</summary>
    public static string DeriveId(SyncEndpointRecord left, SyncEndpointRecord right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var identity = string.Join("|", IdentityScheme,
            Component(left.Provider), Component(left.ConnectionId), Component(left.NamespaceId),
            Component(left.FolderId),
            Component(right.Provider), Component(right.ConnectionId), Component(right.NamespaceId),
            Component(right.FolderId));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(digest).ToLowerInvariant()[..20];
    }

    /// <summary>
    /// Validates a cloud/cloud claim as structural configuration: the document shape, each persisted
    /// endpoint record through the shared endpoint contract, the two endpoints as a pair, the stable
    /// folder identity both sides need for identity derivation, the provider path rule, the derived pair
    /// id and the saved compatibility projection. Same identity and proven overlap stay refusals; an
    /// unverifiable provider relationship is not refused and is not thereby established as disjoint.
    /// Every contradiction is a refusal; a refused pair is never normalized or persisted.
    /// </summary>
    public static SyncEndpointValidation ValidateProjection(SyncPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        var document = pair.EndpointDocument;
        if (document is null)
            return Refuse(SyncEndpointError.MissingEndpointDocument,
                "A cloud/cloud pair requires an explicit endpoint document.");
        if (document.Version != SyncEndpointSchema.SymmetricEndpoints)
            return Refuse(SyncEndpointError.UnsupportedSchemaVersion,
                "The saved endpoint document version is not supported for a cloud/cloud pair.");
        var refusals = new List<SyncEndpointRefusal>();
        if (document.Left is null)
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.InvalidSchemaDocument,
                "The saved cloud/cloud endpoint document has no left endpoint."));
        if (document.Right is null)
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.InvalidSchemaDocument,
                "The saved cloud/cloud endpoint document has no right endpoint."));
        if (refusals.Count != 0) return new SyncEndpointValidation(refusals);
        var left = document.Left!;
        var right = document.Right!;
        if (left.IsLocal || right.IsLocal)
            return Refuse(SyncEndpointError.EndpointKindMismatch,
                "A cloud/cloud configuration requires two cloud endpoints, not a cloud endpoint and a local folder.");
        refusals.AddRange(SyncEndpointContracts.ValidateRecord(left).Refusals);
        refusals.AddRange(SyncEndpointContracts.ValidateRecord(right).Refusals);
        if (refusals.Count != 0) return new SyncEndpointValidation(refusals);

        // The shared pair contract decides the same-object and overlap refusals. Its unverified-ancestry
        // refusal is deliberately not applied to a cloud/cloud document, and the same refinement is used
        // across retained pairs below: two opaque provider roots are exactly the cloud/cloud case, and
        // applying that refusal would refuse every ordered pair of two provider roots whose ancestry the
        // provider cannot express (for example two Google Drive roots). Ignoring it retains a structurally
        // valid configuration and does NOT establish that the two roots are disjoint; live root, cross-pair
        // and recovery-boundary verification stays mandatory in the Host activation work before any effect.
        refusals.AddRange(ValidateEndpointPair(SyncEndpointContracts.ToEndpoint(left),
            SyncEndpointContracts.ToEndpoint(right)).Refusals);
        if (refusals.Count != 0) return new SyncEndpointValidation(refusals);

        if (string.IsNullOrWhiteSpace(left.FolderId))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.MissingCloudFolder,
                "A cloud/cloud pair requires the stable provider folder id of its left endpoint; a display path alone is not an identity."));
        if (string.IsNullOrWhiteSpace(right.FolderId))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.MissingCloudFolder,
                "A cloud/cloud pair requires the stable provider folder id of its right endpoint; a display path alone is not an identity."));

        // Provider metadata, not a per-provider switch, decides whether a folder path describes ancestry.
        // A path-descriptive provider must supply a non-empty provider folder path ('/' is the valid root);
        // an opaque provider's display path is presentation only and may be empty. Whichever applies, the
        // value is projected and compared in the canonical provider-path form and is never passed through a
        // local-path normalizer, and neither case substitutes for the live containment verification later.
        RequireProviderPath(refusals, left, "left");
        RequireProviderPath(refusals, right, "right");

        if (!string.IsNullOrEmpty(pair.LocalRoot) || pair.LocalVolumeSerialNumber != 0 ||
            pair.LocalRootFileId != 0)
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.EndpointKindMismatch,
                "A cloud/cloud pair carries no compatibility local root: LocalRoot must be exactly empty and both local identity fields must be exactly zero."));
        if (pair.ControlNamespace is not null)
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.EndpointKindMismatch,
                "A cloud/cloud pair has no local control namespace binding; its private staging identity is resolved when execution is implemented."));
        if (pair.FilesOnDemandEnabled || pair.DefaultStorage is not null ||
            (pair.DirectoryStorageRules?.Count ?? 0) != 0 || (pair.FileStorageRules?.Count ?? 0) != 0 ||
            (pair.DirectoryStorageApplyPaths?.Count ?? 0) != 0)
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.EndpointKindMismatch,
                "A cloud/cloud pair has no device storage or Files On Demand rules; those apply only to a device-resident root."));

        var rightLiteral = SyncEndpoints.ProviderLiteral(right.Kind);
        if (rightLiteral is null ||
            !string.Equals(SyncProviders.Normalize(pair.Provider), rightLiteral, StringComparison.Ordinal))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.EndpointKindMismatch,
                "The saved pair provider does not agree with the right endpoint of its cloud/cloud document."));
        if (!string.Equals(pair.ConnectionId?.Trim() ?? string.Empty, right.ConnectionId ?? string.Empty,
                StringComparison.Ordinal))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.EndpointKindMismatch,
                "The saved pair account does not agree with the right endpoint of its cloud/cloud document."));
        if (!string.Equals(pair.DropboxFolderId ?? string.Empty, right.FolderId ?? string.Empty,
                StringComparison.Ordinal))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.EndpointKindMismatch,
                "The saved pair folder id does not agree with the right endpoint of its cloud/cloud document."));
        if (!string.Equals(FolderPathKey(pair.DropboxRoot), FolderPathKey(right.FolderPath),
                StringComparison.Ordinal))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.EndpointKindMismatch,
                "The saved pair folder path does not agree with the right endpoint of its cloud/cloud document."));
        if (!string.Equals(pair.Id, DeriveId(left, right), StringComparison.Ordinal))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.EndpointKindMismatch,
                "The saved pair id is not the identity derived from this ordered endpoint document; a changed endpoint identity is an explicit rebind, never a silent one."));
        return refusals.Count == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(refusals);
    }

    /// <summary>
    /// Structural cloud-pair validation shared by draft and persistence. Unknown ancestry is resolved
    /// by the Host's provider-issued hierarchy, never accepted as runtime disjointness here.
    /// </summary>
    public static SyncEndpointValidation ValidateEndpointPair(SyncEndpoint left, SyncEndpoint right)
    {
        if (left.IsLocal || right.IsLocal) return SyncEndpointContracts.ValidatePair(left, right);
        var refusals = SyncEndpointContracts.ValidatePair(left, right).Refusals
            .Where(item => item.Error != SyncEndpointError.UnverifiedCloudNamespace).ToArray();
        return refusals.Length == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(refusals);
    }

    /// <summary>The validated endpoints of a cloud/cloud claim, or a refusal describing the contradiction.</summary>
    public static SyncPairEndpoints RequireProjection(SyncPair pair)
    {
        var validation = ValidateProjection(pair);
        if (!validation.IsValid) throw new InvalidDataException(validation.Describe());
        var document = pair.EndpointDocument!;
        return new SyncPairEndpoints(SyncEndpointContracts.ToEndpoint(document.Left!),
            SyncEndpointContracts.ToEndpoint(document.Right!), pair.Direction);
    }

    /// <summary>
    /// The endpoint identities one retained pair contributes to pair-set uniqueness. A cloud/cloud pair
    /// contributes the two identities of its own document, which is the only way its absent (0,0) local
    /// identity can be excluded from grouping. EVERY other pair, including a local-containing one, now
    /// resolves through the same canonical endpoint resolution, so a valid explicit local/local pair
    /// contributes its own two endpoints rather than a null from the legacy compatibility mapper, while a
    /// genuine no-document legacy pair still maps through that compatibility path with its own validation.
    /// A pair whose explicit document does not validate contributes nothing rather than a guess, and a
    /// malformed, unsupported or incomplete document never falls back.
    /// </summary>
    public static SyncPairEndpoints? SavedEndpoints(SyncPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        // EPS5 production fix: EVERY retained pair resolves through the ONE canonical endpoint resolution,
        // not only a cloud/cloud pair. The legacy compatibility mapper cannot represent a valid explicit
        // local/local pair, so such a pair contributed a null endpoint and a true same-side overlap was
        // reported as an invalid retained pair instead of the intended review-required overlap. Canonical
        // resolution keeps an explicit document's validation and refusal exact - including a malformed,
        // unsupported or incomplete document, which never falls back - while a genuine no-document legacy
        // pair still maps through the compatibility path with its own validation.
        return SyncEndpointPairRuntime.Resolve(pair, pair.EndpointDocument).Endpoints;
    }

    /// <summary>
    /// The pair-set rule for one cloud/cloud candidate against every other retained pair: the shared
    /// endpoint contract refuses an endpoint another pair already retains, on either side, and a folder
    /// that contains or is contained by another pair's endpoint on the same store. Its unverified-ancestry
    /// refusal is not applied here either, for the reason recorded on ValidateProjection, so a structurally
    /// valid configuration may coexist with a retained pair whose relationship to it still needs live
    /// verification. That is not an assertion of disjointness: the Host activation work must verify the
    /// cross-pair and recovery boundaries before any effect.
    /// </summary>
    public static SyncEndpointValidation ValidateAgainstRetainedPairs(SyncPairEndpoints candidate,
        IEnumerable<SyncPairEndpoints>? retainedPairs)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var refusals = SyncEndpointContracts.ValidatePairAgainstExisting(candidate.Left, candidate.Right,
                retainedPairs).Refusals.Where(item =>
            item.Error != SyncEndpointError.UnverifiedCloudNamespace).ToArray();
        return refusals.Length == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(refusals);
    }

    /// <summary>
    /// The policy-aware pair-set rule for a complete cloud/cloud candidate: a strict nested relation may be
    /// admitted only when the enclosing pair proves the whole nested subtree stays excluded on every side
    /// where the two pairs are nested. Its unverified-ancestry outcome is not a structural refusal here, for
    /// exactly the reason recorded on the endpoint overload: provider-issued hierarchy and the retained
    /// authority check still run before any effect.
    /// </summary>
    public static SyncEndpointValidation ValidateAgainstRetainedPairs(SyncPair candidate,
        IEnumerable<SyncPair>? retainedPairs)
    {
        ArgumentNullException.ThrowIfNull(candidate);
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
                case SyncPairPartitionRelation.Same:
                    refusals.Add(new SyncEndpointRefusal(SyncEndpointError.DuplicateEndpointAcrossPairs,
                        "Another retained pair names the exact same cloud store as this candidate."));
                    break;
                case SyncPairPartitionRelation.NestedRefused:
                    refusals.Add(new SyncEndpointRefusal(SyncEndpointError.OverlappingEndpointAcrossPairs,
                        "Another retained pair names a cloud folder that contains, or is contained by, this candidate and the enclosing pair does not exclude the whole nested subtree."));
                    break;
            }
        }
        return refusals.Count == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(refusals);
    }

    /// <summary>
    /// Builds one persistable cloud/cloud pair from an ordered pair of validated cloud endpoints. Policy
    /// is supplied as an optional retained template for selection, ignore, retention, priority, direction
    /// and propagation only. The draft is always created disabled and unapproved: enabled state, approval,
    /// authority, private namespaces, recovery binding and device storage are never inherited from the
    /// template, while normalization preserves the enabled state of an already persisted record. Runtime
    /// activation is neither consulted nor granted: this draft is the persistence foundation the later
    /// execution packet binds.
    /// </summary>
    public static SyncPair CreateDraft(SyncPairEndpoints endpoints, string name, SyncPair? policy = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("A cloud/cloud pair requires a name.");
        var document = SyncEndpointPersistence.Symmetric(endpoints);
        var id = DeriveId(document.Left!, document.Right!);
        var literal = SyncEndpoints.ProviderLiteral(endpoints.Right.Kind) ??
            throw new NotSupportedException("A cloud/cloud pair requires a registered cloud provider on both sides.");
        var draft = (policy ?? new SyncPair(id, name, string.Empty, 0, 0, string.Empty, string.Empty)) with
        {
            Id = id,
            Name = name.Trim(),
            LocalRoot = string.Empty,
            LocalVolumeSerialNumber = 0,
            LocalRootFileId = 0,
            DropboxRoot = endpoints.Right.FolderPath ?? string.Empty,
            DropboxFolderId = endpoints.Right.FolderId ?? string.Empty,
            Provider = literal,
            ConnectionId = endpoints.Right.ConnectionId ?? string.Empty,
            Direction = endpoints.Direction,
            EndpointSchemaVersion = document.Version,
            LeftEndpoint = document.Left,
            RightEndpoint = document.Right,
            Enabled = false,
            InitialReviewApproved = false,
            ControlNamespace = null,
            RightControlNamespace = null,
            RecoveryRoot = null,
            FilesOnDemandEnabled = false,
            DefaultStorage = null,
            DirectoryStorageRules = null,
            DirectoryStorageApplyPaths = null,
            FileStorageRules = null,
            PolicyRevision = 1
        };
        var validation = ValidateProjection(draft);
        if (!validation.IsValid) throw new SyncEndpointRuntimeRefusedException(validation);
        return draft;
    }

    private static string Component(string? value) =>
        value is null ? "-" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    // The provider definition decides the rule; there is no separate Google/Dropbox branch here. A
    // path-descriptive provider needs a real provider folder path ('/' is the valid root), while an opaque
    // provider's presentation path carries no authority and may be empty.
    private static void RequireProviderPath(List<SyncEndpointRefusal> refusals, SyncEndpointRecord record,
        string side)
    {
        if (!SyncEndpointProviders.Get(record.Kind).PathsDescribeAncestry) return;
        if (string.IsNullOrWhiteSpace(record.FolderPath))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.MissingCloudFolder,
                "A cloud/cloud pair requires the provider folder path of its " + side +
                " endpoint because this provider's folder paths describe ancestry; the '/' root is valid."));
    }

    // The saved compatibility path and the right endpoint path are the same presentation value; both are
    // compared in the canonical provider-path form so that normalization stays idempotent across reloads.
    private static string FolderPathKey(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : "/" + string.Join('/', value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));

    private static SyncEndpointValidation Refuse(SyncEndpointError error, string reason) =>
        new(new[] { new SyncEndpointRefusal(error, reason) });
}
