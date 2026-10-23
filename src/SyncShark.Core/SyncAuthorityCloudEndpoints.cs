using System.Text.Json;
using System.Text.Json.Nodes;

namespace Massdex.SyncShark;

// CC2C: one typed, provider-neutral cloud endpoint port shared by BOTH logical sides of a pair.
//
// A port binds the actual registered provider definition, an explicit endpoint record, the retained
// provider-account id, the retained authority root binding id and the adapter that lists that endpoint.
// Nothing here is a local path, a volume serial, a selected NTFS root or a fabricated local endpoint, and
// there is no per-provider switch: the registered provider definition and the shared provider-account
// identity seam decide every rule. The provider/account namespace mapping is settled in exactly one place
// (SyncAuthorityProviderAccountIdentity): a Dropbox endpoint namespace is the retained root namespace and a
// Google Drive endpoint namespace is that row's native account (permission) id, with no label, e-mail,
// path, substring or wildcard heuristic anywhere on this path.
//
// The provider hierarchy is an explicit typed INPUT. This layer does not claim an implemented live
// hierarchy producer: the caller supplies a read-only snapshot bound to one exact retained root, and the
// Host/adapter producer remains a later packet. A missing or unbound snapshot is Unverified and is never
// treated as proof of disjointness.

/// <summary>
/// The two existing compatibility tags. They carry ONLY the legacy database/operation side meaning that
/// logical left is local and logical right is dropbox; neither tag licenses an NTFS call or
/// Dropbox-specific behaviour, and a port is resolved to its typed provider endpoint before any effect.
/// </summary>
public static class SyncAuthorityCloudSides
{
    public const string LogicalLeft = "local";
    public const string LogicalRight = "dropbox";

    public static bool IsCompatibilityTag(string? side) =>
        string.Equals(side, LogicalLeft, StringComparison.Ordinal) ||
        string.Equals(side, LogicalRight, StringComparison.Ordinal);

    public static string RequireCompatibilityTag(string? side) => IsCompatibilityTag(side)
        ? side!
        : throw new ArgumentOutOfRangeException(nameof(side),
            "A cloud endpoint port carries one of the two retained compatibility side tags.");
}

/// <summary>The one shared three-plus-one-valued result of comparing two cloud root identities.</summary>
public enum SyncAuthorityCloudRootRelation
{
    /// <summary>The two roots provably cannot be the same store and neither contains the other.</summary>
    Distinct = 0,

    /// <summary>Both roots name the same provider folder identity.</summary>
    Same = 1,

    /// <summary>One root provably contains the other.</summary>
    Nested = 2,

    /// <summary>Ancestry cannot be established from the supplied identities; never treated as disjoint.</summary>
    Unverified = 3
}

/// <summary>
/// The kind of one provider-issued storage-object key. A native object key is the provider's own file or
/// folder identity and it is the only key a provider API call is ever made with. A shared-namespace mount
/// key is a second, route-issued key for one backing shared object: the provider mounts that backing object
/// at a location inside an access route's namespace and issues a fresh location-scoped id, so the native ids
/// of two routes differ while the mount key names the one object both routes can reach. A provider with no
/// mount mapping issues no keys of that kind, and "ordinary file/folder IDs remain their native object keys".
/// </summary>
public enum SyncCloudObjectKeyKind
{
    /// <summary>The provider's own file or folder identity; the only key a provider API call uses.</summary>
    NativeObject = 0,

    /// <summary>A route-issued mount key naming a shared backing object reached from this route's namespace.</summary>
    SharedNamespaceMount = 1
}

/// <summary>
/// One provider-qualified storage-object key: the registered provider literal, the kind of key and the exact
/// provider-issued value. Two keys name the same backing object only when the provider, the kind and the
/// value all match, with the value compared ordinal and case-sensitively, so one identical literal id under
/// two different providers never aliases and two case variants are never merged. The provider literal is
/// normalised to its registered lowercase form so value equality and SyncCloudObjectKeys.SameObject agree.
/// Keys are readback evidence only: they are never user-supplied CLI authority and they are never inferred
/// from a display name, account label, e-mail or path.
/// </summary>
public readonly record struct SyncCloudObjectKey(string Provider, SyncCloudObjectKeyKind Kind, string Value)
{
    /// <summary>One native provider object key.</summary>
    public static SyncCloudObjectKey Native(string provider, string value) =>
        Create(provider, SyncCloudObjectKeyKind.NativeObject, value);

    /// <summary>One route-issued shared-namespace mount key.</summary>
    public static SyncCloudObjectKey SharedNamespaceMount(string provider, string value) =>
        Create(provider, SyncCloudObjectKeyKind.SharedNamespaceMount, value);

    public static SyncCloudObjectKey Create(string provider, SyncCloudObjectKeyKind kind, string value)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new InvalidDataException("A cloud object key requires a registered provider literal.");
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("A cloud object key requires a provider-issued value.");
        return new SyncCloudObjectKey(provider.Trim().ToLowerInvariant(), kind, value);
    }

    public bool IsWellFormed => !string.IsNullOrWhiteSpace(Provider) && !string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Provider + ":" + Kind + ":" + Value;
}

/// <summary>
/// The one shared comparison and key production of provider-issued object keys. Provider qualification is
/// what keeps one identical literal id observed on two different providers legitimate, and the key kind keeps
/// a provider's native id space from silently aliasing its own mount id space.
/// </summary>
public static class SyncCloudObjectKeys
{
    /// <summary>True when two keys name the same backing object.</summary>
    public static bool SameObject(SyncCloudObjectKey left, SyncCloudObjectKey right) =>
        left.Kind == right.Kind &&
        string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Value, right.Value, StringComparison.Ordinal);

    /// <summary>True when ANY key of one set names the same backing object as a key of the other set.</summary>
    public static bool Intersects(IReadOnlyList<SyncCloudObjectKey>? left, IReadOnlyList<SyncCloudObjectKey>? right)
    {
        if (left is null || right is null) return false;
        foreach (var first in left)
            foreach (var second in right)
                if (SameObject(first, second)) return true;
        return false;
    }

    /// <summary>
    /// The complete provider-neutral key set of one provider item: its native object key followed by every
    /// additional alias the adapter actually read back. An alias that names a different provider than the item
    /// it was read with is refused rather than silently dropped.
    /// </summary>
    public static IReadOnlyList<SyncCloudObjectKey> Combine(string provider, string nativeId,
        IReadOnlyList<SyncCloudObjectKey>? aliases)
    {
        var keys = new List<SyncCloudObjectKey>(1 + (aliases?.Count ?? 0)) { SyncCloudObjectKey.Native(provider, nativeId) };
        if (aliases is not null)
            foreach (var alias in aliases)
            {
                if (!string.Equals(alias.Provider, provider, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "A readback object alias names a different provider than the item it was read with; the alias is refused.");
                keys.Add(alias);
            }
        return keys;
    }
}

/// <summary>How completely one provider-issued readback covered its provider's object keys.</summary>
public enum SyncCloudObjectKeyCoverage
{
    /// <summary>No readback declared object-key coverage; it is never evidence of disjointness.</summary>
    Undeclared = 0,

    /// <summary>This provider issues one globally stable key per object and the readback covered it.</summary>
    NativeOnly = 1,

    /// <summary>This provider can mount one backing object under route-issued keys and the readback covered that alias.</summary>
    MountAliasesComplete = 2
}

/// <summary>
/// The one shared statement of what a registered provider's object identities can name, so the alias model
/// and its comparison stay provider-neutral and no pair- or combination-specific engine is created. A
/// route-scoped provider (Dropbox) issues a second, location-scoped key for a shared backing object, so two of
/// its routes cannot be called disjoint unless that alias was read back and compared. A globally stable
/// provider (Google Drive) issues exactly one key per object through the same model with no mount mapping.
/// </summary>
public static class SyncCloudObjectKeySemantics
{
    /// <summary>True only for a provider whose same backing object can be reached through a route-issued key.</summary>
    public static bool IssuesRouteScopedObjectAliases(string? provider) =>
        string.Equals(provider, SyncProviders.Dropbox, StringComparison.OrdinalIgnoreCase);

    /// <summary>The key coverage a readback of this provider must declare to be complete evidence.</summary>
    public static SyncCloudObjectKeyCoverage RequiredCoverage(string? provider) =>
        IssuesRouteScopedObjectAliases(provider)
            ? SyncCloudObjectKeyCoverage.MountAliasesComplete
            : SyncCloudObjectKeyCoverage.NativeOnly;
}

/// <summary>One actual chain node's provider-issued object keys, index-aligned with the chain's folder ids.</summary>
public sealed record SyncAuthorityCloudHierarchyNodeKeys(string FolderId,
    IReadOnlyList<SyncCloudObjectKey> ObjectKeys);

/// <summary>
/// One cloud root identity as the typed layer treats it: the registered provider literal, the retained
/// provider-account id (absent only for a grandfathered row that never recorded one), the retained native
/// endpoint namespace, the stable provider folder identity, the presentation path, the exact authority id
/// it was read from and that authority's kind. The display path is presentation only and never substitutes
/// for a folder identity or an ancestor chain.
/// </summary>
public sealed record SyncAuthorityCloudRootIdentity(
    string Provider,
    string? ProviderAccountId,
    string? NamespaceId,
    string FolderId,
    string? FolderPath,
    string AuthorityId,
    string AuthorityKind,
    string CompatibilitySide)
{
    public const string PairRootAuthority = "pair_root";
    public const string RecoveryRootAuthority = "recovery_root";
}

/// <summary>
/// One caller-supplied read-only native hierarchy snapshot. It is bound to exactly one retained authority
/// root, provider, provider account, native namespace and stable folder identity, and it carries the
/// provider-issued ancestor chain from the namespace root down to and including that folder. It is input
/// to a comparison, never a persisted authority grant, and the stable root identity JSON deliberately
/// excludes mutable ancestor arrays.
/// </summary>
public sealed record SyncAuthorityCloudHierarchySnapshot(
    string Provider,
    string ProviderAccountId,
    string NamespaceId,
    string RootBindingId,
    string FolderId,
    IReadOnlyList<string> ChainFolderIds)
{
    /// <summary>True when this snapshot is the provider-issued chain for exactly this root identity.</summary>
    public bool BindsTo(SyncAuthorityCloudRootIdentity identity) =>
        identity is not null &&
        string.Equals(RootBindingId, identity.AuthorityId, StringComparison.Ordinal) &&
        string.Equals(FolderId, identity.FolderId, StringComparison.Ordinal) &&
        string.Equals(Provider, identity.Provider, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ProviderAccountId, identity.ProviderAccountId, StringComparison.Ordinal) &&
        string.Equals(NamespaceId, identity.NamespaceId, StringComparison.Ordinal);

    /// <summary>True when this chain places exactly this folder inside its own ancestry.</summary>
    public bool Contains(string folderId) => ChainFolderIds is not null &&
        !string.IsNullOrWhiteSpace(folderId) &&
        ChainFolderIds.Contains(folderId, StringComparer.Ordinal);

    /// <summary>
    /// True only when <see cref="ChainFolderIds"/> is the provider-verified COMPLETE chain from the
    /// positively identified corpus root down to and including <see cref="FolderId"/>. The default is
    /// false, so a snapshot that has not been produced by a verified two-pass readback is never treated as
    /// proof that two roots are disjoint or nested.
    /// </summary>
    public bool CompleteToCorpusRoot { get; init; }

    /// <summary>The provider identity of the chain's verified terminal corpus root, or null when unverified.</summary>
    public string? CorpusRootId { get; init; }

    /// <summary>
    /// Each actual chain node's provider-issued object keys, index-aligned with ChainFolderIds, or null when
    /// the readback supplied no node keys. It is bound to this exact chain, it is never merged with another
    /// root's keys and it never becomes a persisted authority.
    /// </summary>
    public IReadOnlyList<SyncAuthorityCloudHierarchyNodeKeys>? ChainNodeKeys { get; init; }

    /// <summary>
    /// Each chain node's provider-observed folder NAME, index-aligned with ChainFolderIds, or null when the
    /// readback did not observe names. Names are presentation-adjacent provider facts, never authority: they are
    /// only usable to derive a relative exclusion subtree AFTER the typed comparison has already proved a
    /// Nested/Same/Distinct/Unverified relation from identities. A snapshot without names stays adequate for
    /// identity and disjointness but cannot prove an opaque nested exclusion.
    /// </summary>
    public IReadOnlyList<string>? ChainFolderNames { get; init; }

    /// <summary>True only when an observed name chain is present and every component is a safe single name.</summary>
    public bool HasObservedNames => ChainFolderIds is { Count: > 0 } ids &&
        ChainFolderNames is { Count: > 0 } names && names.Count == ids.Count &&
        names.All(IsSafeNameComponent);

    /// <summary>The verified selected root's observed name, or null when names were not observed.</summary>
    public string? SelectedName => HasObservedNames ? ChainFolderNames![^1] : null;

    /// <summary>A nonempty, safe single path component (never a path, control, traversal or trailing dot).</summary>
    public static bool IsSafeNameComponent(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 255 && value is not "." and not ".." &&
        value.IndexOfAny(['/', '\\']) < 0 && !value.Any(char.IsControl) && !value.EndsWith('.') &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// The component-boundary relative subtree of a proven nested chain, derived ONLY from provider-observed
    /// names. Returns null unless the enclosing snapshot's selected folder id appears inside the nested chain and
    /// the nested chain carries a complete observed name set; the caller must already have proved the Nested
    /// relation through the typed comparison and must not infer anything from a display path.
    /// </summary>
    public static string? RelativeSubtree(SyncAuthorityCloudHierarchySnapshot enclosing,
        SyncAuthorityCloudHierarchySnapshot nested)
    {
        ArgumentNullException.ThrowIfNull(enclosing);
        ArgumentNullException.ThrowIfNull(nested);
        if (!nested.HasObservedNames || nested.ChainFolderIds is not { Count: > 0 } ids) return null;
        var index = -1;
        for (var position = 0; position < ids.Count - 1; position++)
            if (string.Equals(ids[position], enclosing.FolderId, StringComparison.Ordinal)) { index = position; break; }
        if (index < 0) return null;
        var components = nested.ChainFolderNames!.Skip(index + 1).ToArray();
        if (components.Length == 0 || components.Any(component => !IsSafeNameComponent(component))) return null;
        return string.Join('/', components);
    }

    /// <summary>
    /// How completely this readback covered the object keys its provider can issue. The default is
    /// SyncCloudObjectKeyCoverage.Undeclared, so a chain that never declared coverage is never treated as
    /// proof that two access routes reach different objects.
    /// </summary>
    public SyncCloudObjectKeyCoverage ObjectKeyCoverage { get; init; }

    /// <summary>True when this snapshot is a complete, bound chain for exactly this root identity.</summary>
    public bool IsCompleteFor(SyncAuthorityCloudRootIdentity identity) =>
        CompleteToCorpusRoot && !string.IsNullOrWhiteSpace(CorpusRootId) && BindsTo(identity) &&
        ChainFolderIds is { Count: > 0 } &&
        string.Equals(ChainFolderIds[0], CorpusRootId, StringComparison.Ordinal) &&
        string.Equals(ChainFolderIds[^1], identity.FolderId, StringComparison.Ordinal);

    /// <summary>
    /// True only when this chain declares exactly the coverage its registered provider requires and every
    /// declared node key set is well formed, index-aligned and carries that node's own native key. A declared
    /// coverage is never accepted on a misaligned, mismatched or malformed key set.
    /// </summary>
    public bool ObjectKeyCoverageIsComplete
    {
        get
        {
            if (ObjectKeyCoverage != SyncCloudObjectKeySemantics.RequiredCoverage(Provider)) return false;
            if (ChainNodeKeys is not { Count: > 0 } nodes) return false;
            if (nodes.Count != ChainFolderIds.Count) return false;
            for (var index = 0; index < nodes.Count; index++)
            {
                if (nodes[index] is not { } node) return false;
                if (!string.Equals(node.FolderId, ChainFolderIds[index], StringComparison.Ordinal)) return false;
                if (node.ObjectKeys is not { Count: > 0 } keys) return false;
                var nativeKeyFound = false;
                foreach (var key in keys)
                {
                    if (!key.IsWellFormed) return false;
                    if (!string.Equals(key.Provider, Provider, StringComparison.OrdinalIgnoreCase)) return false;
                    if (key.Kind == SyncCloudObjectKeyKind.NativeObject &&
                        string.Equals(key.Value, ChainFolderIds[index], StringComparison.Ordinal))
                        nativeKeyFound = true;
                }
                if (!nativeKeyFound) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// True only when this chain can separate two access routes: it is a complete bound chain AND it covered
    /// every object-key kind its provider can issue. A provider whose same backing object can be reached
    /// through a route-issued mount key (Dropbox) is never disjoint from another route without that alias read
    /// back, so a missing, older or declared-but-incomplete coverage stays Unverified rather than becoming a
    /// route-only distinctness decision.
    /// </summary>
    public bool IsDisjointnessCompleteFor(SyncAuthorityCloudRootIdentity identity) =>
        IsCompleteFor(identity) &&
        (!SyncCloudObjectKeySemantics.IssuesRouteScopedObjectAliases(Provider) || ObjectKeyCoverageIsComplete);

    /// <summary>The object keys of this chain's own selected root node, or empty when none were declared.</summary>
    public IReadOnlyList<SyncCloudObjectKey> RootNodeKeys() => NodeKeysAt(ChainFolderIds.Count - 1);

    /// <summary>
    /// Every object key of this chain's verified ANCESTOR nodes: every declared node above the chain's own
    /// selected root. A root whose own key names one of these nodes is contained by this root. The selected
    /// root's own node is deliberately excluded so a root is never compared with itself.
    /// </summary>
    public IReadOnlyList<SyncCloudObjectKey> AncestorNodeKeys()
    {
        var result = new List<SyncCloudObjectKey>();
        for (var index = 0; index + 1 < ChainFolderIds.Count; index++) result.AddRange(NodeKeysAt(index));
        return result;
    }

    private IReadOnlyList<SyncCloudObjectKey> NodeKeysAt(int index)
    {
        if (ChainNodeKeys is not { Count: > 0 } nodes) return Array.Empty<SyncCloudObjectKey>();
        if (nodes.Count != ChainFolderIds.Count) return Array.Empty<SyncCloudObjectKey>();
        if (index < 0 || index >= nodes.Count) return Array.Empty<SyncCloudObjectKey>();
        return nodes[index].ObjectKeys ?? (IReadOnlyList<SyncCloudObjectKey>)Array.Empty<SyncCloudObjectKey>();
    }
}

/// <summary>One safe refusal of a provider-neutral cloud root hierarchy read or of its evidence.</summary>
public sealed class SyncAuthorityCloudRootHierarchyException(string message) : Exception(message);

/// <summary>
/// The exact retained endpoint one hierarchy read is bound to: the registered provider literal, the
/// retained connection (the provider-account access route), the retained provider-account id, the verified
/// native namespace, the stable selected root identity and the retained display path. It is produced from
/// one typed cloud endpoint port, and the adapter refuses any request that names another provider,
/// connection, namespace or root BEFORE it performs a provider read.
/// </summary>
public sealed record SyncAuthorityCloudRootHierarchyRequest(
    string Provider,
    string ConnectionId,
    string ProviderAccountId,
    string NamespaceId,
    string FolderId,
    string FolderPath)
{
    /// <summary>The request for exactly this port's retained endpoint.</summary>
    public static SyncAuthorityCloudRootHierarchyRequest For(SyncAuthorityCloudEndpointPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(port.Record.ConnectionId);
        return new SyncAuthorityCloudRootHierarchyRequest(port.Provider, port.Record.ConnectionId!,
            port.ProviderAccountId, port.NamespaceId, port.FolderId,
            SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(port.FolderPath));
    }

    /// <summary>The endpoint-binding gate, evaluated before any provider read.</summary>
    public void RequireBoundTo(string provider, string connectionId, string namespaceId, string folderId)
    {
        foreach (var value in new[] { Provider, ConnectionId, ProviderAccountId, NamespaceId, FolderId })
            if (string.IsNullOrWhiteSpace(value))
                throw new SyncAuthorityCloudRootHierarchyException(
                    "A cloud root hierarchy read requires the exact retained endpoint; a missing provider, connection, provider account, namespace or root identity is refused without a provider read.");
        if (!string.Equals(Provider, provider, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityCloudRootHierarchyException(
                "The requested cloud root hierarchy read names a different provider than this adapter serves; it is refused without a provider read.");
        if (!string.Equals(ConnectionId, connectionId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRootHierarchyException(
                "The requested cloud root hierarchy read names a different retained connection than this adapter is bound to; it is refused without a provider read.");
        if (!string.Equals(NamespaceId, namespaceId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRootHierarchyException(
                "The requested cloud root hierarchy read names a different native namespace than this adapter retains; it is refused without a provider read.");
        if (!string.Equals(FolderId, folderId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRootHierarchyException(
                "The requested cloud root hierarchy read names a different provider folder identity than this adapter is bound to; it is refused without a provider read.");
    }
}

/// <summary>
/// One provider-issued, read-only hierarchy readback bound to exactly one retained cloud root: the
/// registered provider, the retained connection and provider-account access route, the verified native
/// namespace, the selected root identity, the ordered corpus-root-to-selected-root stable ids, the verified
/// terminal corpus root, the observation time and the number of independent readback passes that agreed.
/// This is internal readback evidence: it is never a persisted permission and never a caller-supplied JSON
/// authority grant.
/// </summary>
public sealed record SyncAuthorityCloudRootHierarchyEvidence(
    string Provider,
    string ConnectionId,
    string ProviderAccountId,
    string NamespaceId,
    string FolderPath,
    string SelectedRootId,
    string CorpusRootId,
    IReadOnlyList<string> CorpusRootToSelectedRootIds,
    DateTimeOffset ObservedAtUtc,
    int VerifiedPassCount)
{
    /// <summary>
    /// Each actual chain node's provider-issued object keys, index-aligned with CorpusRootToSelectedRootIds,
    /// or null when this readback supplied no node keys. Both readback passes must have agreed on every alias.
    /// </summary>
    public IReadOnlyList<SyncAuthorityCloudHierarchyNodeKeys>? ChainNodeKeys { get; init; }

    /// <summary>
    /// Each chain node's provider-observed folder NAME, index-aligned with CorpusRootToSelectedRootIds, or null
    /// when the readback supplied no names. Both readback passes must agree on every name, exactly as they must
    /// agree on every node key.
    /// </summary>
    public IReadOnlyList<string>? ChainFolderNames { get; init; }

    /// <summary>How completely this readback covered the object keys its provider can issue.</summary>
    public SyncCloudObjectKeyCoverage ObjectKeyCoverage { get; init; }

    /// <summary>The one CC2C hierarchy value for this exact retained root binding; nothing else is created.</summary>
    public SyncAuthorityCloudHierarchySnapshot ToSnapshot(string rootBindingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootBindingId);
        if (CorpusRootToSelectedRootIds is null || CorpusRootToSelectedRootIds.Count == 0 ||
            !string.Equals(CorpusRootToSelectedRootIds[0], CorpusRootId, StringComparison.Ordinal) ||
            !string.Equals(CorpusRootToSelectedRootIds[^1], SelectedRootId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudRootHierarchyException(
                "This cloud root hierarchy evidence does not place its verified corpus root first and its selected root last; it is refused rather than used as ancestry.");
        if (VerifiedPassCount < 2)
            throw new SyncAuthorityCloudRootHierarchyException(
                "Cloud root hierarchy evidence requires two agreeing readback passes; a single unverified pass is refused.");
        if (ObjectKeyCoverage != SyncCloudObjectKeyCoverage.Undeclared &&
            (ChainNodeKeys is not { Count: > 0 } nodes || nodes.Count != CorpusRootToSelectedRootIds.Count))
            throw new SyncAuthorityCloudRootHierarchyException(
                "This cloud root hierarchy evidence declares object-key coverage without one well-formed node key set per chain node; it is refused rather than used as ancestry.");
        if (ChainFolderNames is { } names &&
            (names.Count != CorpusRootToSelectedRootIds.Count ||
             names.Any(name => !SyncAuthorityCloudHierarchySnapshot.IsSafeNameComponent(name))))
            throw new SyncAuthorityCloudRootHierarchyException(
                "This cloud root hierarchy evidence declares observed folder names that are not one safe name per chain node; it is refused rather than used as ancestry.");
        return new SyncAuthorityCloudHierarchySnapshot(Provider, ProviderAccountId, NamespaceId, rootBindingId,
            SelectedRootId, CorpusRootToSelectedRootIds)
        {
            CompleteToCorpusRoot = true,
            CorpusRootId = CorpusRootId,
            ChainNodeKeys = ChainNodeKeys,
            ChainFolderNames = ChainFolderNames,
            ObjectKeyCoverage = ObjectKeyCoverage
        };
    }
}

/// <summary>
/// The one internal provider-neutral hierarchy capability, implemented by BOTH registered cloud adapters.
/// An adapter that does not implement it fails explicitly; no default provider branch is used.
/// </summary>
public interface ISyncAuthorityCloudRootHierarchySource
{
    Task<SyncAuthorityCloudRootHierarchyEvidence> ReadRootHierarchyAsync(
        SyncAuthorityCloudRootHierarchyRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The one provider-neutral resolution of that capability from an already typed adapter.</summary>
public static class SyncAuthorityCloudRootHierarchy
{
    public static ISyncAuthorityCloudRootHierarchySource RequireSource(ISyncRemoteStore store) =>
        store as ISyncAuthorityCloudRootHierarchySource
        ?? throw new NotSupportedException(
            "This cloud adapter does not implement the verified provider root-hierarchy capability; the read is refused rather than routed through a default provider branch.");

    public static Task<SyncAuthorityCloudRootHierarchyEvidence> ReadAsync(ISyncRemoteStore store,
        SyncAuthorityCloudRootHierarchyRequest request, CancellationToken cancellationToken = default) =>
        RequireSource(store).ReadRootHierarchyAsync(request, cancellationToken);
}

/// <summary>
/// The native observation identity of one cloud item, shared by both logical sides. It carries the
/// explicit identity kind, the actual provider, the retained provider-account id, the native namespace,
/// the provider file id, the provider revision and the provider content-hash algorithm; size and content
/// hash remain the native values in their existing columns. Nothing here is inferred from a display label.
/// </summary>
public sealed record SyncAuthorityCloudObservationIdentity(
    string Provider,
    string ProviderAccountId,
    string NamespaceId,
    string FileId,
    string? Revision,
    string ContentAlgorithm)
{
    /// <summary>The explicit cloud identity kind. An identity without it is a legacy physical identity.</summary>
    public const string IdentityKindValue = "cloud";

    /// <summary>The exact persisted bytes of this identity, as the existing observation path stores it.</summary>
    public string Serialize() => JsonSerializer.Serialize(new
    {
        identityKind = IdentityKindValue,
        provider = Provider,
        providerAccountId = ProviderAccountId,
        namespaceId = NamespaceId,
        fileId = FileId,
        revision = Revision,
        contentAlgorithm = ContentAlgorithm
    });

    /// <summary>
    /// Reads an observation identity. A JSON value with no explicit identity kind is the legacy identity
    /// and returns false so the existing physical/cloud identity logic keeps its exact meaning. An explicit
    /// identity of another kind, a missing required member, a mixed local member, an unregistered provider
    /// or an algorithm that is not the registered one for that provider is refused rather than guessed.
    /// </summary>
    public static bool TryParse(string identityJson, out SyncAuthorityCloudObservationIdentity? identity)
    {
        identity = null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(identityJson);
        }
        catch (JsonException)
        {
            return false;
        }
        if (node is not JsonObject root) return false;
        if (!TryReadString(root, "identityKind", out var kind) || kind is null) return false;
        if (!string.Equals(kind, IdentityKindValue, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The observation identity declares an unsupported identity kind; it is refused without a write.");
        if (root.ContainsKey("volumeSerial"))
            throw new InvalidDataException(
                "The observation identity mixes a local physical member with an explicit cloud identity; it is refused without a write.");
        var provider = RequireMember(root, "provider");
        var providerAccountId = RequireMember(root, "providerAccountId");
        var namespaceId = RequireMember(root, "namespaceId");
        var fileId = RequireMember(root, "fileId");
        var algorithm = RequireMember(root, "contentAlgorithm");
        if (!SyncEndpointProviders.TryGet(provider, out var definition) || definition!.ProviderId is null)
            throw new InvalidDataException(
                "The observation identity names a provider that is not a registered cloud provider; it is refused without a write.");
        if (!string.Equals(definition.ContentHashAlgorithm, algorithm, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The observation identity declares a content algorithm that is not the registered algorithm of its provider; it is refused without a write.");
        ReadString(root, "revision", out var revision);
        identity = new SyncAuthorityCloudObservationIdentity(definition.ProviderId!, providerAccountId, namespaceId,
            fileId, revision, algorithm);
        return true;
    }

    private static string RequireMember(JsonObject root, string member)
    {
        if (!TryReadString(root, member, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException(
                "The explicit cloud observation identity is missing its required '" + member +
                "' member; it is refused without a write.");
        return value!;
    }

    private static bool TryReadString(JsonObject root, string member, out string? value)
    {
        value = null;
        if (!root.TryGetPropertyValue(member, out var node) || node is not JsonValue jsonValue) return false;
        if (!jsonValue.TryGetValue<string>(out var text)) return false;
        value = text;
        return true;
    }

    private static void ReadString(JsonObject root, string member, out string? value)
    {
        if (!TryReadString(root, member, out value)) value = null;
    }
}

/// <summary>
/// The retained stable identity of one cloud authority root as it is persisted: identity kind, registered
/// provider, retained provider account, native endpoint namespace and stable provider folder identity.
/// Display paths and mutable ancestor arrays are deliberately excluded.
/// </summary>
/// <summary>
/// The one shared retained remote-root identity decision. It loads the pair's exact current active remote root
/// authority ONCE (reusing only LoadRetainedRootAuthorityAsync) and validates it against the pair's registered
/// account, provider and native namespace before ANY effect or publication; a missing, inactive, misbound or
/// malformed root is refused and never silently falls back to the legacy identity. Serialize() emits the
/// explicit typed cloud observation identity for an explicit cloud root and the byte-identical historical
/// { fileId, revision } form for a verified legacy or native physical root.
/// </summary>
internal static class SyncAuthorityRemoteIdentity
{
    internal static async Task<SyncAuthorityRetainedRootAuthority> LoadAsync(SyncAuthorityDatabase authority,
        SyncPair pair, string expectedRootBindingId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRootBindingId);
        var retained = await authority.LoadRetainedRootAuthorityAsync(pair.Id,
            SyncAuthorityCloudSides.LogicalRight, cancellationToken);
        if (retained is null ||
            !string.Equals(retained.RootBindingId, expectedRootBindingId, StringComparison.Ordinal) ||
            !string.Equals(retained.PairId, pair.Id, StringComparison.Ordinal) ||
            !string.Equals(retained.Side, SyncAuthorityCloudSides.LogicalRight, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The pair's retained remote root is missing, inactive or misbound; the operation is refused rather than written with a legacy identity.");
        // A verified legacy or native physical remote root keeps its exact historical identity bytes.
        if (!retained.IsExplicitCloudIdentity) return retained;
        if (!SyncEndpointProviders.TryGet(retained.IdentityProvider, out var definition) ||
            definition?.ProviderId is null)
            throw new InvalidDataException(
                "The retained explicit cloud root names no registered provider; the operation is refused.");
        if (!retained.HasRetainedProviderAccount ||
            !string.Equals(retained.IdentityProvider, retained.AccountProvider, StringComparison.Ordinal) ||
            !string.Equals(retained.IdentityProvider, pair.Provider, StringComparison.Ordinal) ||
            !SyncAuthorityProviderAccountIdentity.RetainedRowMatchesDerivation(retained.AccountProvider!,
                retained.IdentityProviderAccountId, retained.AccountId!, retained.AccountRootNamespaceId!,
                retained.AccountHomeNamespaceId!) ||
            !string.Equals(retained.IdentityNamespaceId,
                SyncAuthorityProviderAccountIdentity.RetainedEndpointNamespace(retained.AccountProvider!,
                    retained.AccountId!, retained.AccountRootNamespaceId!, retained.AccountHomeNamespaceId!),
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The retained explicit cloud root's provider, account or native namespace does not match its registered account; the operation is refused.");
        return retained;
    }

    internal static string Serialize(SyncAuthorityRetainedRootAuthority retained, string fileId, string? revision)
    {
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        if (!retained.IsExplicitCloudIdentity)
            return JsonSerializer.Serialize(new { fileId, revision });
        if (!SyncEndpointProviders.TryGet(retained.IdentityProvider, out var definition) ||
            definition?.ProviderId is null || string.IsNullOrWhiteSpace(retained.IdentityProviderAccountId) ||
            string.IsNullOrWhiteSpace(retained.IdentityNamespaceId))
            throw new InvalidDataException(
                "The retained explicit cloud root names no registered provider; the identity is refused.");
        return new SyncAuthorityCloudObservationIdentity(retained.IdentityProvider!,
            retained.IdentityProviderAccountId!, retained.IdentityNamespaceId!, fileId, revision,
            definition.ContentHashAlgorithm).Serialize();
    }
}

public static class SyncAuthorityCloudRootPersistence
{
    public static string Serialize(string provider, string providerAccountId, string namespaceId,
        string folderId) => JsonSerializer.Serialize(new
    {
        identityKind = SyncAuthorityCloudObservationIdentity.IdentityKindValue,
        provider,
        providerAccountId,
        namespaceId,
        folderId
    });

    /// <summary>True when a retained root identity is an explicit cloud identity rather than a legacy one.</summary>
    public static bool IsCloudIdentity(string? identityJson)
    {
        if (string.IsNullOrWhiteSpace(identityJson)) return false;
        try
        {
            return JsonNode.Parse(identityJson) is JsonObject root &&
                   root.TryGetPropertyValue("identityKind", out var kind) &&
                   kind is JsonValue value && value.TryGetValue<string>(out var text) &&
                   string.Equals(text, SyncAuthorityCloudObservationIdentity.IdentityKindValue, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryRead(string? identityJson, out string? provider, out string? providerAccountId,
        out string? namespaceId, out string? folderId)
    {
        provider = null; providerAccountId = null; namespaceId = null; folderId = null;
        if (!IsCloudIdentity(identityJson)) return false;
        var root = JsonNode.Parse(identityJson!)!.AsObject();
        provider = Member(root, "provider");
        providerAccountId = Member(root, "providerAccountId");
        namespaceId = Member(root, "namespaceId");
        folderId = Member(root, "folderId");
        return !string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(providerAccountId) &&
               !string.IsNullOrWhiteSpace(namespaceId) && !string.IsNullOrWhiteSpace(folderId);
    }

    /// <summary>
    /// The presentation path of a cloud root in its canonical provider-path form. It is never a local path:
    /// a missing or blank provider path becomes the provider root, and an unsafe provider path is refused by
    /// the existing provider-path rule rather than silently reparsed as a Windows path.
    /// </summary>
    public static string NormalizeDisplayPath(string? providerPath) => string.IsNullOrWhiteSpace(providerPath)
        ? "/"
        : PilotPath.NormalizeDropboxRoot(providerPath);

    private static string? Member(JsonObject root, string member) =>
        root.TryGetPropertyValue(member, out var node) && node is JsonValue value &&
        value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}

/// <summary>
/// The one shared comparison of two cloud root identities. It decides whether two retained roots name the
/// same provider object, one contains the other, or they provably cannot be the same store; anything it
/// cannot establish is Unverified and is never disjointness. Different accounts or namespaces never on
/// their own prove that two provider-global folder identities are separate, and a path-descriptive
/// provider is never given a fabricated ancestor hierarchy.
/// </summary>
public static class SyncAuthorityCloudRootComparison
{
    public static SyncAuthorityCloudRootRelation Compare(SyncAuthorityCloudRootIdentity candidate,
        SyncAuthorityCloudHierarchySnapshot? candidateHierarchy, SyncAuthorityCloudRootIdentity retained,
        SyncAuthorityCloudHierarchySnapshot? retainedHierarchy)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(retained);
        if (!SyncEndpointProviders.TryGet(candidate.Provider, out var definition) || definition?.ProviderId is null)
            return SyncAuthorityCloudRootRelation.Unverified;
        if (!SyncEndpointProviders.TryGet(retained.Provider, out var retainedDefinition) ||
            retainedDefinition?.ProviderId is null)
            return SyncAuthorityCloudRootRelation.Unverified;
        if (!string.Equals(candidate.Provider, retained.Provider, StringComparison.OrdinalIgnoreCase))
            return SyncAuthorityCloudRootRelation.Distinct;
        // A provider-global folder identity is the same object no matter which account or namespace reaches
        // it, so this is decided before any account or namespace comparison.
        if (definition.ObjectIdsAreProviderGlobal && !string.IsNullOrWhiteSpace(candidate.FolderId) &&
            !string.IsNullOrWhiteSpace(retained.FolderId) &&
            string.Equals(candidate.FolderId, retained.FolderId, StringComparison.Ordinal))
            return SyncAuthorityCloudRootRelation.Same;
        // Disjointness across access routes requires complete, exactly bound provider evidence. A shared
        // object keeps its id whichever account, namespace or access root reaches it, so:
        //   * a root's own provider-issued keys naming the other root's own keys prove one backing object;
        //   * a root's own keys naming a verified ancestor node of the other chain prove containment;
        //   * either verified complete chain containing the other root's folder identity proves containment;
        //   * two complete, mutually non-containing and alias-covered chains prove neither contains the other;
        //   * a missing, incomplete or unbound chain proves nothing, and a different account or namespace is
        //     an access route rather than disjointness evidence. It never falls through to a route-only or
        //     to a non-null-array distinctness decision.
        // One genuinely verified same-namespace provider-path comparison may still separate two roots: with
        // both namespaces verified and equal, two non-overlapping provider paths stay distinct. Any other
        // route-only or incomplete-evidence case is Unverified rather than distinct.
        if (definition.ObjectIdsAreProviderGlobal)
        {
            var candidateComplete = candidateHierarchy is not null && candidateHierarchy.IsCompleteFor(candidate);
            var retainedComplete = retainedHierarchy is not null && retainedHierarchy.IsCompleteFor(retained);
            // A root's own provider-issued keys naming the other root's own keys are the same backing object,
            // whichever account, namespace or access route reaches it, and it is decided before any route
            // comparison. This is what makes two DIFFERENT Dropbox mount ids with the SAME
            // sharing_info.shared_folder_id (including an empty shared folder) one object rather than two.
            if (SyncCloudObjectKeys.Intersects(candidateHierarchy?.RootNodeKeys(),
                    retainedHierarchy?.RootNodeKeys()))
                return SyncAuthorityCloudRootRelation.Same;
            // A root's own keys naming a node of the other root's verified ancestor chain place it inside that
            // root, across routes: a nested root under a shared mount is contained by that mount.
            if (candidateHierarchy is not null && SyncCloudObjectKeys.Intersects(candidateHierarchy.RootNodeKeys(),
                    retainedHierarchy?.AncestorNodeKeys()))
                return SyncAuthorityCloudRootRelation.Nested;
            if (retainedHierarchy is not null && SyncCloudObjectKeys.Intersects(retainedHierarchy.RootNodeKeys(),
                    candidateHierarchy?.AncestorNodeKeys()))
                return SyncAuthorityCloudRootRelation.Nested;
            if (candidateComplete && candidateHierarchy!.Contains(retained.FolderId))
                return SyncAuthorityCloudRootRelation.Nested;
            if (retainedComplete && retainedHierarchy!.Contains(candidate.FolderId))
                return SyncAuthorityCloudRootRelation.Nested;
            if (candidateComplete && retainedComplete)
            {
                // Two complete chains are disjoint only when each readback also covered every object-key kind
                // its provider can issue. A missing, older or declared-but-incomplete cover is not proof of
                // disjointness across routes, and it never falls through to a route-only or to a
                // non-null-array distinctness decision.
                return candidateHierarchy!.IsDisjointnessCompleteFor(candidate) &&
                       retainedHierarchy!.IsDisjointnessCompleteFor(retained)
                    ? SyncAuthorityCloudRootRelation.Distinct
                    : SyncAuthorityCloudRootRelation.Unverified;
            }
        }
        if (!definition.PathsDescribeAncestry) return SyncAuthorityCloudRootRelation.Unverified;
        if (!string.IsNullOrWhiteSpace(candidate.NamespaceId) && !string.IsNullOrWhiteSpace(retained.NamespaceId) &&
            !string.Equals(candidate.NamespaceId, retained.NamespaceId, StringComparison.Ordinal))
            return SyncAuthorityCloudRootRelation.Unverified;
        if (Blank(candidate.NamespaceId) || Blank(retained.NamespaceId))
            return SyncAuthorityCloudRootRelation.Unverified;
        if (string.IsNullOrWhiteSpace(candidate.FolderPath) || string.IsNullOrWhiteSpace(retained.FolderPath))
            return SyncAuthorityCloudRootRelation.Unverified;
        if (string.Equals(candidate.FolderId, retained.FolderId, StringComparison.Ordinal))
            return SyncAuthorityCloudRootRelation.Same;
        return SyncEndpointOverlap.FolderPathsOverlap(candidate.FolderPath!, retained.FolderPath!)
            ? SyncAuthorityCloudRootRelation.Nested
            : SyncAuthorityCloudRootRelation.Distinct;
    }

    /// <summary>The refusal for a non-distinct relation, or an accepted validation for a provably distinct pair.</summary>
    public static SyncEndpointValidation Validate(SyncAuthorityCloudRootIdentity candidate,
        SyncAuthorityCloudHierarchySnapshot? candidateHierarchy, SyncAuthorityCloudRootIdentity retained,
        SyncAuthorityCloudHierarchySnapshot? retainedHierarchy, string sideLabel)
    {
        switch (Compare(candidate, candidateHierarchy, retained, retainedHierarchy))
        {
            case SyncAuthorityCloudRootRelation.Same:
                return Refuse(SyncEndpointError.DuplicateEndpointAcrossPairs,
                    "The " + sideLabel + " cloud root names the exact same provider folder identity as another active " +
                    AuthorityWord(retained) + "; the same provider object is never bound twice.");
            case SyncAuthorityCloudRootRelation.Nested:
                return Refuse(SyncEndpointError.OverlappingEndpointAcrossPairs,
                    "The " + sideLabel + " cloud root is contained by, or contains, another active " +
                    AuthorityWord(retained) + " on the same provider; the roots are not disjoint.");
            case SyncAuthorityCloudRootRelation.Unverified:
                return Refuse(SyncEndpointError.UnverifiedCloudNamespace,
                    "The " + sideLabel + " cloud root ancestry against another active " + AuthorityWord(retained) +
                    " cannot be established from the supplied native identities; a missing or unbound hierarchy is refused rather than assumed disjoint.");
            default:
                return SyncEndpointValidation.Accepted;
        }
    }

    private static string AuthorityWord(SyncAuthorityCloudRootIdentity retained) =>
        string.Equals(retained.AuthorityKind, SyncAuthorityCloudRootIdentity.RecoveryRootAuthority, StringComparison.Ordinal)
            ? "recovery boundary"
            : "pair root";

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static SyncEndpointValidation Refuse(SyncEndpointError error, string reason) =>
        new(new[] { new SyncEndpointRefusal(error, reason) });
}

/// <summary>
/// One typed cloud endpoint port: the actual registered provider definition, the exact explicit endpoint,
/// the retained provider-account id, the retained authority root binding id and the adapter that lists it.
/// </summary>
public sealed class SyncAuthorityCloudEndpointPort
{
    private SyncAuthorityCloudEndpointPort(string compatibilitySide, SyncEndpointRecord record,
        SyncEndpointProviderDefinition definition, string providerAccountId, string rootBindingId,
        ISyncRemoteStore store)
    {
        CompatibilitySide = compatibilitySide;
        Record = record;
        Definition = definition;
        ProviderAccountId = providerAccountId;
        RootBindingId = rootBindingId;
        Store = store;
    }

    public string CompatibilitySide { get; }
    public SyncEndpointRecord Record { get; }
    public SyncEndpointProviderDefinition Definition { get; }
    public string Provider => Definition.ProviderId!;
    public SyncEndpointKind Kind => Definition.Kind;
    public string ProviderAccountId { get; }
    public string RootBindingId { get; }
    public ISyncRemoteStore Store { get; }
    public string NamespaceId => Record.NamespaceId!;
    public string FolderId => Record.FolderId!;
    public string? FolderPath => Record.FolderPath;

    /// <summary>This port's retained root identity, for the one shared root comparison.</summary>
    public SyncAuthorityCloudRootIdentity RootIdentity => new(Provider, ProviderAccountId, NamespaceId, FolderId,
        FolderPath, RootBindingId, SyncAuthorityCloudRootIdentity.PairRootAuthority, CompatibilitySide);

    /// <summary>
    /// Reads the real provider hierarchy back through this port's own adapter, bound to this exact retained
    /// endpoint. An adapter without the capability fails explicitly and no default provider branch is used.
    /// </summary>
    public Task<SyncAuthorityCloudRootHierarchyEvidence> ReadRootHierarchyAsync(
        CancellationToken cancellationToken = default) =>
        SyncAuthorityCloudRootHierarchy.ReadAsync(Store,
            SyncAuthorityCloudRootHierarchyRequest.For(this), cancellationToken);

    /// <summary>The native observation identity this port records for one listed item.</summary>
    public SyncAuthorityCloudObservationIdentity ObservationIdentity(string fileId, string? revision) =>
        new(Provider, ProviderAccountId, NamespaceId, fileId, revision, Definition.ContentHashAlgorithm);

    internal static SyncAuthorityCloudEndpointPort Create(string compatibilitySide, SyncEndpointRecord record,
        string providerAccountId, string rootBindingId, ISyncRemoteStore store)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(store);
        SyncAuthorityCloudSides.RequireCompatibilityTag(compatibilitySide);
        if (record.IsLocal)
            throw new InvalidDataException("A typed cloud endpoint port cannot be a local folder.");
        if (!SyncEndpointProviders.TryGet(record.Provider, out var definition) || definition!.ProviderId is null)
            throw new InvalidDataException("A typed cloud endpoint port requires a registered cloud provider.");
        if (definition.Kind != record.Kind)
            throw new InvalidDataException("The typed cloud endpoint provider does not match its endpoint kind.");
        if (string.IsNullOrWhiteSpace(record.NamespaceId))
            throw new InvalidDataException(
                "A typed cloud endpoint port requires the verified native namespace of its account; an unverified namespace never separates two cloud roots.");
        if (string.IsNullOrWhiteSpace(record.FolderId))
            throw new InvalidDataException(
                "A typed cloud endpoint port requires the stable provider folder identity of its root; a display path alone is not an identity.");
        ArgumentException.ThrowIfNullOrWhiteSpace(providerAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootBindingId);
        return new SyncAuthorityCloudEndpointPort(compatibilitySide, record, definition, providerAccountId,
            rootBindingId, store);
    }
}

/// <summary>
/// The two-port set of one explicit cloud/cloud pair. It validates the two compatibility tags, the exact
/// explicit cloud/cloud endpoint document, the distinct side and root identities and the absence of any
/// NTFS selected-root substitute; it never routes a local-containing or legacy pair through the cloud path.
/// </summary>
public sealed class SyncAuthorityCloudEndpointSet
{
    private SyncAuthorityCloudEndpointSet(string pairId, SyncAuthorityCloudEndpointPort left,
        SyncAuthorityCloudEndpointPort right)
    {
        PairId = pairId;
        Left = left;
        Right = right;
    }

    public string PairId { get; }

    /// <summary>Logical left, carried under the retained local compatibility tag.</summary>
    public SyncAuthorityCloudEndpointPort Left { get; }

    /// <summary>Logical right, carried under the retained dropbox compatibility tag.</summary>
    public SyncAuthorityCloudEndpointPort Right { get; }

    public IEnumerable<SyncAuthorityCloudEndpointPort> Ports
    {
        get { yield return Left; yield return Right; }
    }

    public static SyncAuthorityCloudEndpointSet FromPair(SyncPair pair, ISyncRemoteStore leftStore,
        ISyncRemoteStore rightStore, string leftRootBindingId, string rightRootBindingId,
        string leftProviderAccountId, string rightProviderAccountId)
    {
        if (!TryFromPair(pair, leftStore, rightStore, leftRootBindingId, rightRootBindingId,
                leftProviderAccountId, rightProviderAccountId, out var set, out var validation))
            throw new SyncEndpointRuntimeRefusedException(validation);
        return set!;
    }

    public static bool TryFromPair(SyncPair pair, ISyncRemoteStore leftStore, ISyncRemoteStore rightStore,
        string leftRootBindingId, string rightRootBindingId, string leftProviderAccountId,
        string rightProviderAccountId, out SyncAuthorityCloudEndpointSet? set,
        out SyncEndpointValidation validation)
    {
        set = null;
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(leftStore);
        ArgumentNullException.ThrowIfNull(rightStore);
        var refusals = new List<SyncEndpointRefusal>();
        if (!SyncCloudPairConfiguration.IsCloudOnly(pair))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.EndpointKindMismatch,
                "A typed cloud endpoint set is produced only from an explicit cloud/cloud pair; a local-containing or legacy pair is never routed through the cloud path."));
        else
            refusals.AddRange(SyncCloudPairConfiguration.ValidateProjection(pair).Refusals);
        if (string.IsNullOrWhiteSpace(leftRootBindingId) || string.IsNullOrWhiteSpace(rightRootBindingId))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.MissingEndpointDocument,
                "Each typed cloud endpoint port requires its retained authority root binding id."));
        else if (string.Equals(leftRootBindingId, rightRootBindingId, StringComparison.Ordinal))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.SameEndpoint,
                "The two logical sides require two distinct authority root binding ids."));
        if (string.IsNullOrWhiteSpace(leftProviderAccountId) || string.IsNullOrWhiteSpace(rightProviderAccountId))
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.MissingCloudAccount,
                "Each typed cloud endpoint port requires the retained provider-account id of its account."));
        if (!string.IsNullOrEmpty(pair.LocalRoot) || pair.LocalVolumeSerialNumber != 0 ||
            pair.LocalRootFileId != 0)
            refusals.Add(new SyncEndpointRefusal(SyncEndpointError.EndpointKindMismatch,
                "A typed cloud endpoint set carries no NTFS selected root; a local path, volume serial or root file id is never a substitute."));
        if (refusals.Count == 0 && pair.EndpointDocument is { } document)
        {
            if (string.IsNullOrWhiteSpace(document.Left!.NamespaceId) ||
                string.IsNullOrWhiteSpace(document.Right!.NamespaceId))
                refusals.Add(new SyncEndpointRefusal(SyncEndpointError.UnverifiedCloudNamespace,
                    "Both cloud endpoints require their verified native namespace; an unverified namespace never separates two cloud roots."));
            if (string.IsNullOrWhiteSpace(document.Left!.FolderId) ||
                string.IsNullOrWhiteSpace(document.Right!.FolderId))
                refusals.Add(new SyncEndpointRefusal(SyncEndpointError.MissingCloudFolder,
                    "Both cloud endpoints require their stable provider folder identity."));
            if (refusals.Count == 0 && SyncEndpoints.ReferencesSameStore(
                    SyncEndpointContracts.ToEndpoint(document.Left!),
                    SyncEndpointContracts.ToEndpoint(document.Right!)))
                refusals.Add(new SyncEndpointRefusal(SyncEndpointError.SameEndpoint,
                    "The two logical sides of a typed cloud endpoint set must be two distinct root identities."));
        }
        validation = refusals.Count == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(refusals);
        if (!validation.IsValid) return false;
        var endpoints = pair.EndpointDocument!;
        try
        {
            set = new SyncAuthorityCloudEndpointSet(pair.Id,
                SyncAuthorityCloudEndpointPort.Create(SyncAuthorityCloudSides.LogicalLeft, endpoints.Left!,
                    leftProviderAccountId.Trim(), leftRootBindingId.Trim(), leftStore),
                SyncAuthorityCloudEndpointPort.Create(SyncAuthorityCloudSides.LogicalRight, endpoints.Right!,
                    rightProviderAccountId.Trim(), rightRootBindingId.Trim(), rightStore));
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            validation = new SyncEndpointValidation(new[]
            {
                new SyncEndpointRefusal(SyncEndpointError.EndpointKindMismatch, error.Message)
            });
            set = null;
            return false;
        }
        return true;
    }
}
/// <summary>One refused cross-side shared-object overlap: the two cloud sides can reach one backing object.</summary>
public sealed class SyncAuthorityCloudObjectOverlapException(string message) : IOException(message);

/// <summary>
/// The one shared pre-observation overlap guard. It compares the two COMPLETED inventories of a cloud/cloud
/// pair on their actual provider-qualified native object keys and route-issued mount aliases, and refuses the
/// observation when both sides can reach one backing object - at the same or a different relative path, and
/// whether or not the pair's selection or ignore rules exclude that path. It runs before any selection
/// filtering, scan generation, evidence or path-index write, so an overlap is never normalised into two
/// independent copy targets and never silently inferred absent. It reads no provider and retains no live
/// effect; a genuinely distinct pair is unaffected.
///
/// It does not replace the retained root-hierarchy guard: that guard compares the two retained ROOTS at
/// registration, while this guard compares the two INVENTORIES and therefore also catches two otherwise
/// non-nested roots that can both reach one mounted descendant. A queued Host/effect consumer must call this
/// guard, and refresh the retained root hierarchy and its object-key evidence, before it authorises any
/// effect; those packets own that integration and this packet does not claim it.
/// </summary>
public static class SyncAuthorityCloudObjectOverlapGuard
{
    public static void RequireDistinctInventories(SyncAuthorityCloudEndpointSet endpoints,
        IReadOnlyList<PilotRemoteEntry> left, IReadOnlyList<PilotRemoteEntry> right)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var leftKeys = KeyIndex(endpoints.Left.Provider, left);
        if (leftKeys.Count == 0) return;
        var rightKeys = KeyIndex(endpoints.Right.Provider, right);
        foreach (var (key, leftPath) in leftKeys)
            if (rightKeys.TryGetValue(key, out var rightPath))
                throw new SyncAuthorityCloudObjectOverlapException(
                    "The two cloud sides of this pair can both reach one backing " + KeyWord(key) + " (" +
                    key.Provider + " " + key.Value + "): the logical left side reaches it at '" + leftPath +
                    "' and the logical right side reaches it at '" + rightPath + "'. The observation is refused " +
                    "before either scan generation, so one shared backing object is never treated as two " +
                    "independent copy targets.");
    }

    private static Dictionary<SyncCloudObjectKey, string> KeyIndex(string provider,
        IReadOnlyList<PilotRemoteEntry> entries)
    {
        var index = new Dictionary<SyncCloudObjectKey, string>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id)) continue;
            foreach (var key in SyncCloudObjectKeys.Combine(provider, entry.Id!, entry.ObjectAliases))
                if (!index.ContainsKey(key)) index[key] = PilotPath.NormalizeRelative(entry.RelativePath);
        }
        return index;
    }

    private static string KeyWord(SyncCloudObjectKey key) =>
        key.Kind == SyncCloudObjectKeyKind.SharedNamespaceMount
            ? "shared-namespace mount object"
            : "object";
}

/// <summary>
/// CC4A-3: one logical side's exact retained account and recovery authority for one explicit cloud/cloud
/// pair. Every member is reconstructed from existing retained rows - the typed port's own endpoint record,
/// the retained provider account and the CC2E recovery row for this exact provider account AND actual
/// corpus - so no new pair member, schema column, property or cache carries it.
/// </summary>
public sealed record SyncAuthorityCloudSideAuthority(
    string CompatibilitySide,
    string Provider,
    string ConnectionId,
    string ProviderAccountId,
    string NamespaceId,
    string FolderId,
    string FolderPath,
    string PairRootBindingId,
    string RecoveryRootBindingId,
    string RecoveryFolderId,
    string RecoveryNamespaceId,
    string RecoveryCorpusId,
    string RecoveryPath)
{
    /// <summary>This side's selected pair root as the one shared comparable root identity.</summary>
    public SyncAuthorityCloudRootIdentity PairRootIdentity() =>
        new(Provider, ProviderAccountId, NamespaceId, FolderId, FolderPath, PairRootBindingId,
            SyncAuthorityCloudRootIdentity.PairRootAuthority, CompatibilitySide);

    /// <summary>
    /// This side's retained recovery boundary as the same shared comparable root identity. The retained
    /// recovery side tag denotes the stored cloud authority, and the typed context maps each logical port to
    /// its exact recovery authority; it is never read as a logical pair side.
    /// </summary>
    public SyncAuthorityCloudRootIdentity RecoveryRootIdentity() =>
        new(Provider, ProviderAccountId, RecoveryNamespaceId, RecoveryFolderId, RecoveryPath,
            RecoveryRootBindingId, SyncAuthorityCloudRootIdentity.RecoveryRootAuthority, CompatibilitySide);
}

/// <summary>
/// CC4A-3: the one internal prepared context of an explicit cloud/cloud pair. It carries only what existing
/// retained authority already decided: the CC2C typed two-port set, the pair's existing private
/// control-namespace binding, both sides' account and recovery identities, the frozen hierarchy snapshots
/// the roots were verified with, and the planner allocation options. It is reconstructed from the retained
/// rows on every preparation, never persisted as a new pair property, schema member or cache, and it carries
/// no serializable caller-supplied permission. It is not activation: the public enablement gates stay closed
/// and the runner packet consumes this context.
/// </summary>
public sealed class SyncAuthorityPreparedCloudPair
{
    internal SyncAuthorityPreparedCloudPair(SyncPair pair, SyncAuthorityCloudEndpointSet endpoints,
        SyncControlNamespaceBinding privateStaging, SyncAuthorityCloudSideAuthority left,
        SyncAuthorityCloudSideAuthority right, SyncAuthorityPlannerOptions plannerOptions,
        SyncAuthorityCloudAllocationConfiguration cloudAllocations,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> hierarchySnapshots)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(privateStaging);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(plannerOptions);
        ArgumentNullException.ThrowIfNull(cloudAllocations);
        Pair = pair;
        PolicyRevision = pair.PolicyRevision;
        Endpoints = endpoints;
        PrivateStaging = privateStaging;
        Left = left;
        Right = right;
        PlannerOptions = plannerOptions;
        CloudAllocations = cloudAllocations;
        HierarchySnapshots = hierarchySnapshots ?? Array.Empty<SyncAuthorityCloudHierarchySnapshot>();
        SideBindings = new[] { left, right };
    }

    public SyncPair Pair { get; }

    /// <summary>The policy revision this context was prepared under; a later revision invalidates it.</summary>
    public long PolicyRevision { get; }

    public SyncAuthorityCloudEndpointSet Endpoints { get; }

    /// <summary>
    /// The pair's existing private control-namespace binding. It is private implementation storage held
    /// beside the product's real state anchor and is never either endpoint: the pair's own LocalRoot stays
    /// exactly empty and its ControlNamespace members stay null, as the CC1 projection requires.
    /// </summary>
    public SyncControlNamespaceBinding PrivateStaging { get; }

    public SyncAuthorityCloudSideAuthority Left { get; }
    public SyncAuthorityCloudSideAuthority Right { get; }
    public IReadOnlyList<SyncAuthorityCloudSideAuthority> SideBindings { get; }
    public SyncAuthorityPlannerOptions PlannerOptions { get; }
    public SyncAuthorityCloudAllocationConfiguration CloudAllocations { get; }

    /// <summary>
    /// The frozen, verified hierarchy snapshots this preparation compared, one per verified root and retained
    /// recovery boundary. They are frozen input for the runner's own revalidation, never a persisted grant.
    /// </summary>
    public IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> HierarchySnapshots { get; }

    /// <summary>The exact side authority for one retained compatibility tag.</summary>
    public SyncAuthorityCloudSideAuthority SideAuthority(string side)
    {
        SyncAuthorityCloudSides.RequireCompatibilityTag(side);
        return string.Equals(side, SyncAuthorityCloudSides.LogicalLeft, StringComparison.Ordinal) ? Left : Right;
    }

    public SyncAuthorityCloudEndpointPort Port(string side) => Endpoints.Ports.Single(port =>
        string.Equals(port.CompatibilitySide, SyncAuthorityCloudSides.RequireCompatibilityTag(side),
            StringComparison.Ordinal));

    /// <summary>The frozen snapshot for one exact retained root identity, or null when none was verified.</summary>
    public SyncAuthorityCloudHierarchySnapshot? HierarchyFor(SyncAuthorityCloudRootIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return HierarchySnapshots.FirstOrDefault(snapshot => snapshot.BindsTo(identity));
    }

    /// <summary>
    /// A stale-context guard for the runner: the pair must still be retained at the same identity and policy
    /// revision this context was prepared under. A later revision is a re-preparation, never a silent reuse.
    /// </summary>
    public void RequireSameRetainedRevision(SyncPair current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!string.Equals(current.Id, Pair.Id, StringComparison.Ordinal))
            throw new InvalidDataException(
                "This prepared cloud context belongs to a different folder pair than the pair now retained.");
        if (current.PolicyRevision != PolicyRevision)
            throw new InvalidOperationException(
                "The folder pair changed after this cloud context was prepared. Prepare it again.");
    }
}
