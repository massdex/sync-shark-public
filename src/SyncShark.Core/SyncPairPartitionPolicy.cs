using System.Globalization;

namespace Massdex.SyncShark;

/// <summary>
/// The relation between one candidate pair and one retained pair across every endpoint of the same
/// locality. It is the one reusable decision behind nested-pair partition: a nested relation is
/// allowed only when the enclosing pair proves by its own persisted selection/ignore policy that the
/// whole nested subtree stays excluded, on every side where the two pairs are nested. Equal roots,
/// unproven nesting, unresolved identity and private control/recovery ownership all stay refused.
/// </summary>
public enum SyncPairPartitionRelation
{
    /// <summary>No endpoint relationship at all; the two pairs are safely disjoint.</summary>
    Distinct = 0,

    /// <summary>An endpoint names the exact same store as another pair's endpoint; always refused.</summary>
    Same = 1,

    /// <summary>A nested relation proven safe: the enclosing pair excludes the whole nested subtree.</summary>
    NestedAllowed = 2,

    /// <summary>A nested relation that is not proven excluded, or is a reserved control/recovery path.</summary>
    NestedRefused = 3,

    /// <summary>The relation cannot be established from the supplied identities; refused rather than guessed.</summary>
    Unverified = 4,

    /// <summary>
    /// A corresponding nested DATA relation admitted from BOTH verified endpoints: one bijective
    /// component-relative translation shared by every endpoint, one enclosing direction, and the enclosing
    /// pair includes the whole relative subtree as ordinary participant data. It is deliberately distinct
    /// from <see cref="NestedAllowed"/>, whose meaning stays the fully excluded partition, and it is never
    /// granted by the strict raw endpoint-only callers.
    /// </summary>
    CorrespondingIncluded = 5
}

/// <summary>
/// The single reusable nested-pair partition policy. It consumes only complete pair definitions and
/// verified endpoint/root facts that the product already retains - the existing persisted selection and
/// ignore policy, the existing endpoint identities and the existing control/recovery bindings. It adds
/// no schema, no registry, no controller and no provider-pair dispatch matrix, and it never turns a
/// display name into authority. Raw endpoint-only callers that do not carry a pair policy keep their
/// existing strict overlap refusal; only this policy-aware path may admit a proven nested partition.
/// </summary>
public static class SyncPairPartitionPolicy
{
    internal static bool HasComparableOpaqueEndpoints(SyncPair candidate, SyncPair retained)
    {
        var left = SyncCloudPairConfiguration.SavedEndpoints(candidate);
        var right = SyncCloudPairConfiguration.SavedEndpoints(retained);
        return left is not null && right is not null &&
               new[] { left.Left, left.Right }.Any(first => !first.IsLocal &&
                   !SyncEndpointProviders.Get(first.Kind).PathsDescribeAncestry &&
                   new[] { right.Left, right.Right }.Any(second => !second.IsLocal && second.Kind == first.Kind));
    }

    /// <summary>
    /// The relation of one candidate pair against one retained pair. Both pairs must resolve to their
    /// canonical endpoints; a pair whose identity cannot be established is Unverified and is refused.
    /// </summary>
    public static SyncPairPartitionRelation Decide(SyncPair candidate, SyncPair retained)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(retained);
        var candidateEndpoints = SyncCloudPairConfiguration.SavedEndpoints(candidate);
        var retainedEndpoints = SyncCloudPairConfiguration.SavedEndpoints(retained);
        if (candidateEndpoints is null || retainedEndpoints is null) return SyncPairPartitionRelation.Unverified;
        return Decide(candidateEndpoints, candidate, retainedEndpoints, retained,
            new[] { candidate, retained });
    }

    /// <summary>
    /// The shared pair-set rule for EVERY pair shape: a candidate pair is checked against every other
    /// retained pair, so a saved policy edit that would remove a nested exclusion, add an included exception
    /// below an excluded child, or otherwise break a retained nested partition is refused before it is
    /// persisted. Equal stores and unproven nested overlaps are refused; a genuinely disjoint pair is
    /// unaffected, and an unverified relation is not refused here (the exact authority/hierarchy checks own
    /// that).
    /// </summary>
    public static SyncEndpointValidation ValidateAgainstRetainedPairs(SyncPair candidate,
        IEnumerable<SyncPair>? retainedPairs, bool refuseUnverified = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var refusals = new List<SyncEndpointRefusal>();
        foreach (var retained in (retainedPairs ?? Array.Empty<SyncPair>()).Where(item => item is not null))
        {
            if (string.Equals(retained.Id, candidate.Id, StringComparison.Ordinal)) continue;
            var relation = Decide(candidate, retained);
            // A corresponding INCLUDED nesting is a distinct admitted relation, not an excluded partition:
            // it never weakens the excluded-partition proof, and every non-corresponding case keeps its
            // existing refusal below.
            if (relation == SyncPairPartitionRelation.NestedRefused &&
                DecideCorrespondingIncluded(candidate, retained) == SyncPairPartitionRelation.CorrespondingIncluded)
                continue;
            switch (relation)
            {
                case SyncPairPartitionRelation.Same:
                    refusals.Add(new SyncEndpointRefusal(SyncEndpointError.DuplicateEndpointAcrossPairs,
                        "Another retained pair names the exact same local or cloud store as this candidate."));
                    break;
                case SyncPairPartitionRelation.NestedRefused:
                    refusals.Add(new SyncEndpointRefusal(SyncEndpointError.OverlappingEndpointAcrossPairs,
                        "Another retained pair names a folder that contains, or is contained by, this candidate and the enclosing pair does not exclude the whole nested subtree."));
                    break;
                case SyncPairPartitionRelation.Unverified when refuseUnverified:
                    // A strict runtime entry point must not let an unresolved/malformed retained pair be waived:
                    // the established overlap/identity check still owns this relation and is never bypassed.
                    refusals.Add(new SyncEndpointRefusal(SyncEndpointError.UnverifiedCloudNamespace,
                        "A retained pair's relation to this pair cannot be established from its own retained facts; the runtime refuses rather than treating it as distinct."));
                    break;
            }
        }
        return refusals.Count == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(refusals);
    }

    /// <summary>
    /// D2/OV637-4: the same pair-set rule resolved with independently bound opaque cloud endpoint proofs. A
    /// path-opaque correspondence is admitted only from the caller's complete bound hierarchy proofs, exactly
    /// as the typed cloud guard admits it, and a pair with no supplied proof keeps the fail-closed refusal. The
    /// proofs can only ADMIT a correspondence the retained authority already verified; they never turn a Same,
    /// an unexcluded NestedRefused or a genuinely unrelated relation into acceptance.
    /// </summary>
    public static SyncEndpointValidation ValidateAgainstRetainedPairs(SyncPair candidate,
        IEnumerable<SyncPair>? retainedPairs,
        Func<SyncPair, IReadOnlyList<SyncOpaqueCloudEndpointProof>?>? proofsOf)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var refusals = new List<SyncEndpointRefusal>();
        foreach (var retained in (retainedPairs ?? Array.Empty<SyncPair>()).Where(item => item is not null))
        {
            if (string.Equals(retained.Id, candidate.Id, StringComparison.Ordinal)) continue;
            var relation = Decide(candidate, retained);
            if (relation == SyncPairPartitionRelation.NestedRefused &&
                DecideCorrespondingIncluded(candidate, retained, proofsOf?.Invoke(candidate),
                    proofsOf?.Invoke(retained)) == SyncPairPartitionRelation.CorrespondingIncluded)
                continue;
            switch (relation)
            {
                case SyncPairPartitionRelation.Same:
                    refusals.Add(new SyncEndpointRefusal(SyncEndpointError.DuplicateEndpointAcrossPairs,
                        "Another retained pair names the exact same local or cloud store as this candidate."));
                    break;
                case SyncPairPartitionRelation.NestedRefused:
                    refusals.Add(new SyncEndpointRefusal(SyncEndpointError.OverlappingEndpointAcrossPairs,
                        "Another retained pair names a folder that contains, or is contained by, this candidate and the enclosing pair does not exclude the whole nested subtree."));
                    break;
            }
        }
        return refusals.Count == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(refusals);
    }

    /// <summary>The same decision over already-resolved endpoints and their owning pair definitions.</summary>
    /// <summary>
    /// The provider-verified variant of <see cref="Decide(SyncPair,SyncPair)"/>: identical policy rules, but a
    /// path-opaque cloud relation may be resolved from supplied provider-verified hierarchy snapshots. The
    /// relation still comes from the established typed comparator, and the relative subtree is derived only
    /// from provider-observed names. Callers that supply no evidence keep the fail-closed Unverified refusal.
    /// </summary>
    public static SyncPairPartitionRelation DecideWithHierarchy(SyncPair candidate,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? candidateProofs, SyncPair retained,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? retainedProofs)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(retained);
        var candidateEndpoints = SyncCloudPairConfiguration.SavedEndpoints(candidate);
        var retainedEndpoints = SyncCloudPairConfiguration.SavedEndpoints(retained);
        if (candidateEndpoints is null || retainedEndpoints is null) return SyncPairPartitionRelation.Unverified;
        return Decide(candidateEndpoints, candidate, retainedEndpoints, retained, new[] { candidate, retained },
            candidateProofs, retainedProofs);
    }

    /// <summary>
    /// One independently resolved opaque cloud endpoint proof: the provider-verified readback PLUS the exactly
    /// resolved provider-account id and retained root binding id that the operation itself holds. A snapshot is
    /// never trusted to certify its own account or root binding.
    /// </summary>
    public sealed record SyncOpaqueCloudEndpointProof(SyncAuthorityCloudHierarchySnapshot Snapshot,
        string ProviderAccountId, string RootBindingId, SyncAuthorityCloudRootIdentity? HeldIdentity = null);

    /// <summary>
    /// One proven endpoint correspondence of a corresponding INCLUDED nesting: the ordered candidate endpoint
    /// index, the ONE ordered retained endpoint index it corresponds to on the same locality and provider,
    /// whether the candidate endpoint is the inner one, and that endpoint pair's shared component-relative
    /// translation. Consumers that project one pair's roots or paths onto the other map SIDES from this
    /// bijection and never assume that inner local equals outer local or that both sides keep their order.
    /// </summary>
    public readonly record struct SyncCorrespondingEndpointMatch(int CandidateEndpointIndex,
        int RetainedEndpointIndex, bool CandidateEndpointIsInner, string Relative);

    /// <summary>
    /// The provider-verified relation for one path-opaque cloud endpoint pair, or null when the relation cannot be
    /// taken from proof (path-descriptive provider, absent/ambiguous/mis-bound proof). The caller must already
    /// have proved the relation BEFORE deriving any relative names, and Distinct never requires observed names.
    /// </summary>
    private static SyncAuthorityCloudRootRelation? OpaqueHierarchyRelation(SyncEndpoint candidate,
        SyncEndpoint retained, IReadOnlyList<SyncOpaqueCloudEndpointProof>? candidateProofs,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? retainedProofs, out bool enclosingIsCandidate,
        out string relative)
    {
        enclosingIsCandidate = false;
        relative = string.Empty;
        if (candidate.IsLocal || retained.IsLocal || candidate.Kind != retained.Kind) return null;
        // A path-descriptive provider already decided through its verified ancestry path.
        if (SyncEndpointProviders.Get(candidate.Kind).PathsDescribeAncestry) return null;
        if (MatchProof(candidate, candidateProofs) is not { } candidateProof ||
            MatchProof(retained, retainedProofs) is not { } retainedProof) return null;
        var candidateIdentity = Identity(candidate, candidateProof, SyncAuthorityCloudSides.LogicalLeft);
        var retainedIdentity = Identity(retained, retainedProof, SyncAuthorityCloudSides.LogicalRight);
        // Before the comparator may use ANY object-key/alias intersection, each snapshot must independently bind
        // to the resolved identity it claims and carry a complete verified chain to its corpus root. A
        // wrong-account or wrong-root snapshot whose ancestor keys merely intersect is therefore Unverified
        // rather than Nested; this gate never relaxes, only refuses.
        if (!candidateProof.Snapshot.BindsTo(candidateIdentity) ||
            !retainedProof.Snapshot.BindsTo(retainedIdentity) ||
            !candidateProof.Snapshot.IsCompleteFor(candidateIdentity) ||
            !retainedProof.Snapshot.IsCompleteFor(retainedIdentity))
            return SyncAuthorityCloudRootRelation.Unverified;
        var relation = SyncAuthorityCloudRootComparison.Compare(
            candidateIdentity, candidateProof.Snapshot, retainedIdentity, retainedProof.Snapshot);
        if (relation != SyncAuthorityCloudRootRelation.Nested) return relation;
        // Names are required ONLY after Nested identity proof; a Nested relation without usable observed names
        // stays Unverified rather than being relaxed or guessed from a display path.
        if (SyncAuthorityCloudHierarchySnapshot.RelativeSubtree(retainedProof.Snapshot, candidateProof.Snapshot)
            is { Length: > 0 } nestedUnderRetained)
        {
            enclosingIsCandidate = false;
            relative = nestedUnderRetained;
            return relation;
        }
        if (SyncAuthorityCloudHierarchySnapshot.RelativeSubtree(candidateProof.Snapshot, retainedProof.Snapshot)
            is { Length: > 0 } nestedUnderCandidate)
        {
            enclosingIsCandidate = true;
            relative = nestedUnderCandidate;
            return relation;
        }
        return SyncAuthorityCloudRootRelation.Unverified;
    }

    /// <summary>
    /// The one proof for this endpoint, or null when none or more than one proof matches its exact
    /// provider/namespace/folder. An ambiguous match refuses rather than picking the first.
    /// </summary>
    private static SyncOpaqueCloudEndpointProof? MatchProof(SyncEndpoint endpoint,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? proofs)
    {
        if (proofs is null) return null;
        var provider = SyncEndpoints.ProviderLiteral(endpoint.Kind);
        var matches = proofs.Where(proof => proof is not null && proof.Snapshot is not null &&
            string.Equals(proof.Snapshot.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(proof.Snapshot.NamespaceId, endpoint.NamespaceId ?? proof.HeldIdentity?.NamespaceId, StringComparison.Ordinal) &&
            (proof.HeldIdentity is null || HeldIdentityMatches(endpoint, proof)) &&
            string.Equals(proof.Snapshot.FolderId, endpoint.FolderId, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static SyncAuthorityCloudRootIdentity Identity(SyncEndpoint endpoint,
        SyncOpaqueCloudEndpointProof proof, string side) => proof.HeldIdentity ?? new(
        SyncEndpoints.ProviderLiteral(endpoint.Kind)!, proof.ProviderAccountId, endpoint.NamespaceId,
        endpoint.FolderId!, endpoint.FolderPath, proof.RootBindingId,
        SyncAuthorityCloudRootIdentity.PairRootAuthority, side);

    private static bool HeldIdentityMatches(SyncEndpoint endpoint, SyncOpaqueCloudEndpointProof proof) =>
        proof.HeldIdentity is { } identity &&
        identity.AuthorityKind == SyncAuthorityCloudRootIdentity.PairRootAuthority &&
        identity.AuthorityId == proof.RootBindingId && identity.ProviderAccountId == proof.ProviderAccountId &&
        identity.Provider == SyncEndpoints.ProviderLiteral(endpoint.Kind) && identity.FolderId == endpoint.FolderId &&
        !string.IsNullOrWhiteSpace(identity.NamespaceId) &&
        (endpoint.NamespaceId is null || endpoint.NamespaceId == identity.NamespaceId);

    public static SyncPairPartitionRelation Decide(SyncPairEndpoints candidateEndpoints, SyncPair candidatePolicy,
        SyncPairEndpoints retainedEndpoints, SyncPair retainedPolicy, IReadOnlyList<SyncPair>? knownPairs = null,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? candidateProofs = null,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? retainedProofs = null) =>
        DecideCore(candidateEndpoints, candidatePolicy, retainedEndpoints, retainedPolicy,
            knownPairs, candidateProofs, retainedProofs);

    private static SyncPairPartitionRelation DecideCore(SyncPairEndpoints candidateEndpoints, SyncPair candidatePolicy,
        SyncPairEndpoints retainedEndpoints, SyncPair retainedPolicy, IReadOnlyList<SyncPair>? knownPairs = null,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? candidateProofs = null,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? retainedProofs = null, bool localRootsOnly = false)
    {
        ArgumentNullException.ThrowIfNull(candidateEndpoints);
        ArgumentNullException.ThrowIfNull(retainedEndpoints);
        ArgumentNullException.ThrowIfNull(candidatePolicy);
        ArgumentNullException.ThrowIfNull(retainedPolicy);
        var known = knownPairs ?? new[] { candidatePolicy, retainedPolicy };

        var sawSame = false;
        var sawUnverified = false;
        var candidateEnclosing = 0;
        var retainedEnclosing = 0;
        var relatives = new List<string>();

        foreach (var candidateEndpoint in new[] { candidateEndpoints.Left, candidateEndpoints.Right })
        foreach (var retainedEndpoint in new[] { retainedEndpoints.Left, retainedEndpoints.Right })
        {
            if (localRootsOnly && (!candidateEndpoint.IsLocal || !retainedEndpoint.IsLocal)) continue;
            if (candidateEndpoint.IsLocal != retainedEndpoint.IsLocal) continue;
            if (!candidateEndpoint.IsLocal && candidateEndpoint.Kind != retainedEndpoint.Kind) continue;

            if (EqualEndpoint(candidateEndpoint, retainedEndpoint))
            {
                sawSame = true;
                continue;
            }

            // A nested relation is only meaningful at a component boundary and only when the enclosing
            // pair excludes the whole relative subtree. Either side may be the enclosing pair.
            if (IsComparable(candidateEndpoint, retainedEndpoint))
            {
                var candidateUnderRetained = RelativeSubtree(retainedEndpoint, candidateEndpoint);
                if (candidateUnderRetained is not null)
                {
                    retainedEnclosing++;
                    relatives.Add(candidateUnderRetained);
                    continue;
                }
                var retainedUnderCandidate = RelativeSubtree(candidateEndpoint, retainedEndpoint);
                if (retainedUnderCandidate is not null)
                {
                    candidateEnclosing++;
                    relatives.Add(retainedUnderCandidate);
                    continue;
                }
                if (ProvablyDistinct(candidateEndpoint, retainedEndpoint)) continue;
            }
            // A path-opaque provider cannot prove ancestry from identities alone. When provider-verified
            // hierarchy evidence is supplied for BOTH endpoints, the established typed comparator decides the
            // relation and the relative subtree is derived ONLY from provider-observed names. No display path
            // is ever consulted; a missing or incomplete chain stays Unverified.
            switch (OpaqueHierarchyRelation(candidateEndpoint, retainedEndpoint, candidateProofs, retainedProofs,
                        out var opaqueEnclosingIsCandidate, out var opaqueRelative))
            {
                case SyncAuthorityCloudRootRelation.Same:
                    // The provider-verified comparison proved one shared object; the alias is never lost.
                    sawSame = true;
                    continue;
                case SyncAuthorityCloudRootRelation.Nested:
                    if (opaqueEnclosingIsCandidate) candidateEnclosing++;
                    else retainedEnclosing++;
                    relatives.Add(opaqueRelative);
                    continue;
                case SyncAuthorityCloudRootRelation.Distinct:
                    // Verified disjointness needs no observed names.
                    continue;
                case SyncAuthorityCloudRootRelation.Unverified:
                default:
                    break;
            }
            sawUnverified = true;
        }

        if (sawSame) return SyncPairPartitionRelation.Same;
        if (relatives.Count == 0) return sawUnverified
            ? SyncPairPartitionRelation.Unverified
            : SyncPairPartitionRelation.Distinct;
        // Every nested relation must share one enclosing direction; a mixed direction is unproven.
        if (candidateEnclosing != 0 && retainedEnclosing != 0) return SyncPairPartitionRelation.Unverified;
        var enclosing = candidateEnclosing != 0 ? candidatePolicy : retainedPolicy;
        foreach (var relative in relatives)
        {
            if (!ExcludesWholeSubtree(enclosing, relative)) return SyncPairPartitionRelation.NestedRefused;
            if (IsReservedControlOrRecoveryPath(enclosing, known, relative))
                return SyncPairPartitionRelation.NestedRefused;
        }
        // Private control/recovery ownership of the nested pair that falls inside the enclosing root is
        // reserved ownership, not ordinary data. The enclosing pair must exclude it too, otherwise the
        // partition would hand a parent scan another pair's private staging or recovery as participant data.
        var nested = candidateEnclosing != 0 ? retainedPolicy : candidatePolicy;
        // Missing opaque-provider evidence can defer the unknown relation, never a refusal already
        // proved by the local/descriptive endpoints. In particular the enclosing pair must exclude
        // the child's physically known private ownership even before cloud ancestry is read back.
        if (!OwnershipExcluded(enclosing, nested, localRootsOnly: true))
            return SyncPairPartitionRelation.NestedRefused;
        if (sawUnverified) return SyncPairPartitionRelation.Unverified;
        if (!OwnershipExcluded(enclosing, nested, localRootsOnly)) return SyncPairPartitionRelation.NestedRefused;
        return SyncPairPartitionRelation.NestedAllowed;
    }

    /// <summary>
    /// True only when every private control/recovery ownership path of the nested pair that lies inside the
    /// enclosing pair's roots is itself excluded by the enclosing policy (or is the automatically reserved
    /// remote product container, which the enclosing pair's protected rules already exclude).
    /// </summary>
    private static bool OwnershipExcluded(SyncPair enclosing, SyncPair nested, bool localRootsOnly = false,
        bool includeDerivedLocalOwnership = false)
    {
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(enclosing);
        if (endpoints is null) return false;
        foreach (var (isLocal, ownedPath) in OwnedPaths(nested, includeDerivedLocalOwnership))
        {
            if (localRootsOnly && !isLocal) continue;
            foreach (var endpoint in new[] { endpoints.Left, endpoints.Right })
            {
                if (endpoint.IsLocal != isLocal) continue;
                // A cloud ownership path we cannot place because the enclosing endpoint has no provider path
                // is unproven, not safe.
                if (!isLocal && string.IsNullOrWhiteSpace(endpoint.FolderPath)) return false;
                var relative = RelativeOwnershipPath(endpoint, ownedPath, isLocal);
                if (relative is null) continue;
                if (!ExcludesWholeSubtree(enclosing, relative)) return false;
            }
        }
        return true;
    }

    private static IEnumerable<(bool IsLocal, string Path)> OwnedPaths(SyncPair pair,
        bool includeDerivedLocalOwnership = false)
    {
        if (pair.ControlNamespace is { } left && !string.IsNullOrWhiteSpace(left.Local.RootPath))
            yield return (true, left.Local.RootPath);
        if (pair.ControlNamespace?.Remote is { } leftRemote && !string.IsNullOrWhiteSpace(leftRemote.RootPath))
            yield return (false, leftRemote.RootPath);
        if (pair.RightControlNamespace is { } right && !string.IsNullOrWhiteSpace(right.Local.RootPath))
            yield return (true, right.Local.RootPath);
        if (pair.RightControlNamespace?.Remote is { } rightRemote && !string.IsNullOrWhiteSpace(rightRemote.RootPath))
            yield return (false, rightRemote.RootPath);
        if (pair.RecoveryRoot is { } recovery && !string.IsNullOrWhiteSpace(recovery.RootPath))
            yield return (false, recovery.RootPath);
        if (!includeDerivedLocalOwnership) yield break;
        // A nested pair's private local control namespace is a SIBLING of its own local root and is named by
        // the product's own deterministic formula, so its exact location is known even before the candidate
        // has been allocated. The enclosing pair must exclude that location too, otherwise an included parent
        // scan would adopt the child's private staging as ordinary participant data.
        foreach (var derived in DerivedLocalOwnershipPaths(pair)) yield return (true, derived);
    }

    /// <summary>
    /// The deterministic private local control-namespace locations of one pair, derived with the existing
    /// production formula (sibling of the local root, named by the pair's own namespace id). No new naming
    /// scheme is introduced.
    /// </summary>
    private static IEnumerable<string> DerivedLocalOwnershipPaths(SyncPair pair)
    {
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(pair);
        if (endpoints is null) yield break;
        foreach (var (endpoint, namespaceId) in new[]
                 {
                     (endpoints.Left, pair.Id),
                     (endpoints.Right, SyncEndpointPairRuntime.RightControlNamespaceId(pair.Id))
                 })
        {
            if (!endpoint.IsLocal || string.IsNullOrWhiteSpace(endpoint.LocalPath)) continue;
            string[] segments;
            try { segments = PilotPath.NormalizeLocalRoot(endpoint.LocalPath!).Split(Path.DirectorySeparatorChar); }
            catch (ArgumentException) { continue; }
            var parent = string.Join(Path.DirectorySeparatorChar, segments[..^1]);
            if (parent.Length == 0) continue;
            yield return Path.Combine(parent, SyncControlNamespace.LocalPrefix +
                SyncControlNamespace.NormalizeNamespaceId(namespaceId));
        }
    }

    private static string? RelativeOwnershipPath(SyncEndpoint enclosing, string ownershipPath, bool isLocal)
    {
        try
        {
            if (isLocal)
            {
                var root = PilotPath.NormalizeLocalRoot(enclosing.LocalPath!);
                // A local root of a local/local pair is retained under the dropbox side as a provider-path
                // form ("/C:/..."); normalise that back to the drive form before comparing.
                var owned = PilotPath.NormalizeLocalRoot(NormalizeLocalCandidate(ownershipPath));
                if (!owned.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return null;
                return PilotPath.NormalizeRelative(owned[(root.Length + 1)..]);
            }
            if (string.IsNullOrWhiteSpace(enclosing.FolderPath)) return null;
            var cloudRoot = PilotPath.NormalizeDropboxRoot(enclosing.FolderPath);
            var cloudOwned = PilotPath.NormalizeDropboxRoot(ownershipPath);
            if (cloudRoot == "/")
                return PilotPath.NormalizeRelative(cloudOwned);
            if (!cloudOwned.StartsWith(cloudRoot + "/", StringComparison.OrdinalIgnoreCase)) return null;
            return PilotPath.NormalizeRelative(cloudOwned[(cloudRoot.Length + 1)..]);
        }
        catch (ArgumentException) { return null; }
    }

    private static string NormalizeLocalCandidate(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length >= 3 && (trimmed[0] is '/' or '\\') && char.IsLetter(trimmed[1]) && trimmed[2] == ':')
            return trimmed[1..];
        return trimmed;
    }

    /// <summary>
    /// The row-scoped admission decision. A pair-level NestedAllowed is necessary but never sufficient: the
    /// actual competing row is bound by its own side and path to the exact represented current endpoint or
    /// owned control of the pair that really owns it. The enclosing pair's own DATA root may be waived by the
    /// exact excluded candidate root; every other row (the nested pair's data root, any represented private
    /// control row) requires that exact row's own subtree to be excluded. A stale, unrepresented, unknown or
    /// private-control row that merely contains the candidate is refused, so a child can never be admitted
    /// inside another pair's reserved ownership.
    /// </summary>
    public static bool AllowsNestedOverlapPath(SyncPair candidate, SyncPair retained, string existingPath,
        string candidatePath, string? rowPairId, string? rowSide)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(retained);
        if (string.IsNullOrWhiteSpace(existingPath) || string.IsNullOrWhiteSpace(candidatePath)) return false;
        if (string.IsNullOrWhiteSpace(rowPairId) || string.IsNullOrWhiteSpace(rowSide)) return false;
        var relation = Decide(candidate, retained);
        // This API admits a represented PATH ROW only, never a cloud identity or an effect.
        // A physically local row can prove its own NTFS partition independently; the
        // separate typed cloud registration still requires provider-issued ancestry.
        if (relation == SyncPairPartitionRelation.Unverified &&
            IsPhysicalLocalPath(existingPath) && IsPhysicalLocalPath(candidatePath) &&
            SyncCloudPairConfiguration.SavedEndpoints(candidate) is { } candidateEndpoints &&
            SyncCloudPairConfiguration.SavedEndpoints(retained) is { } retainedEndpoints)
            relation = DecideCore(candidateEndpoints, candidate, retainedEndpoints, retained,
                localRootsOnly: true);
        if (relation != SyncPairPartitionRelation.NestedAllowed) return false;
        // Bind the candidate definition to the ACTUAL path being registered: a candidate pair definition for
        // another root cannot waive this registration's overlap, because it does not represent candidatePath
        // on any of its own sides.
        var candidateControl = IsRepresentedRow(candidate, candidatePath, "local_control") ||
            IsRepresentedRow(candidate, candidatePath, "dropbox_control");
        if (!candidateControl && !IsRepresentedRow(candidate, candidatePath, "local") &&
            !IsRepresentedRow(candidate, candidatePath, "dropbox")) return false;
        SyncPair enclosing;
        if (NestedSubtreeRelative(candidate, retained) is not null) enclosing = candidate;
        else if (NestedSubtreeRelative(retained, candidate) is not null) enclosing = retained;
        else return false;
        var nested = ReferenceEquals(enclosing, candidate) ? retained : candidate;
        var rowPair = string.Equals(rowPairId, enclosing.Id, StringComparison.Ordinal) ? enclosing
            : string.Equals(rowPairId, nested.Id, StringComparison.Ordinal) ? nested : null;
        if (rowPair is null) return false;
        var controlRow = rowSide.EndsWith("_control", StringComparison.Ordinal);
        // The exact row must really be one of that pair's own endpoint roots or owned control subtrees, on the
        // locality its own side names.
        if (!IsRepresentedRow(rowPair, existingPath, rowSide)) return false;
        // A newly verified child control is admitted only against the enclosing DATA row,
        // with its exact subtree excluded. Never waive another private-control boundary.
        if (candidateControl)
            return ReferenceEquals(nested, candidate) && !controlRow &&
                string.Equals(rowPairId, enclosing.Id, StringComparison.Ordinal) &&
                CoveredByEnclosingPolicy(enclosing, candidatePath);
        if (controlRow)
        {
            // A private control row may be waived only for its own exact excluded subtree, and never when the
            // candidate root sits inside it (reverse child-inside-control).
            if (ContainsPath(existingPath, candidatePath)) return false;
            return CoveredByEnclosingPolicy(enclosing, existingPath);
        }
        // Candidate-path coverage alone is sufficient only for the enclosing pair's own represented data root.
        if (string.Equals(rowPairId, enclosing.Id, StringComparison.Ordinal))
            return CoveredByEnclosingPolicy(enclosing, candidatePath);
        return CoveredByEnclosingPolicy(enclosing, existingPath);
    }

    /// <summary>
    /// The corresponding INCLUDED data relation: one pair strictly encloses the other at a component boundary
    /// on BOTH verified endpoints with ONE equal component-relative translation and one enclosing direction,
    /// and the enclosing pair includes the whole relative subtree as ordinary participant data. Every
    /// non-corresponding case returns <see cref="SyncPairPartitionRelation.NestedRefused"/> or
    /// <see cref="SyncPairPartitionRelation.Unverified"/> so the caller keeps its existing fail-closed refusal;
    /// this decision never widens authority on its own and is never reached by a raw path-only caller.
    /// </summary>
    public static SyncPairPartitionRelation DecideCorrespondingIncluded(SyncPair candidate, SyncPair retained,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? candidateProofs = null,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? retainedProofs = null)
        => DecideCorrespondingCore(candidate, retained, candidateProofs, retainedProofs, out _, out _, out _);

    /// <summary>
    /// The corresponding INCLUDED relation together with the exact proven endpoint bijection. Each match names
    /// one ordered candidate endpoint and the ONE ordered retained endpoint it corresponds to, whether the
    /// candidate endpoint is inner, and the shared component-relative translation. Consumers that must project
    /// one pair's roots or paths onto the other map sides from this bijection, so a swapped local/local order
    /// or a provider-qualified side is followed exactly rather than assumed.
    /// </summary>
    public static SyncPairPartitionRelation DecideCorrespondingIncluded(SyncPair candidate, SyncPair retained,
        out IReadOnlyList<SyncCorrespondingEndpointMatch> endpointMatches,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? candidateProofs = null,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? retainedProofs = null)
        => DecideCorrespondingCore(candidate, retained, candidateProofs, retainedProofs, out _, out _,
            out endpointMatches);

    /// <summary>
    /// The corresponding INCLUDED relation together with its exact enclosing and nested pair and the one
    /// component-relative translation, or false when the two pairs are not a complete corresponding nesting.
    /// Consumers that must project one pair's paths onto the other (the durable reservation alias fence) use
    /// this so they never re-derive the relation or the relative translation independently.
    /// </summary>
    public static bool TryCorrespondingIncluded(SyncPair candidate, SyncPair retained, out SyncPair enclosing,
        out SyncPair nested, out string relative,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? candidateProofs = null,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? retainedProofs = null)
    {
        var relation = DecideCorrespondingCore(candidate, retained, candidateProofs, retainedProofs,
            out var candidateIsInner, out relative, out _);
        enclosing = candidateIsInner ? retained : candidate;
        nested = candidateIsInner ? candidate : retained;
        return relation == SyncPairPartitionRelation.CorrespondingIncluded;
    }

    /// <summary>
    /// The corresponding INCLUDED relation with its exact enclosing/nested pair, the one component-relative
    /// translation and the proven endpoint bijection. Consumers that project one pair's roots or paths onto the
    /// other use this single shared decision so they never re-derive the relation, the relative translation or
    /// the endpoint mapping independently.
    /// </summary>
    public static bool TryCorrespondingIncluded(SyncPair candidate, SyncPair retained, out SyncPair enclosing,
        out SyncPair nested, out string relative,
        out IReadOnlyList<SyncCorrespondingEndpointMatch> endpointMatches,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? candidateProofs = null,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? retainedProofs = null)
    {
        var relation = DecideCorrespondingCore(candidate, retained, candidateProofs, retainedProofs,
            out var candidateIsInner, out relative, out endpointMatches);
        enclosing = candidateIsInner ? retained : candidate;
        nested = candidateIsInner ? candidate : retained;
        return relation == SyncPairPartitionRelation.CorrespondingIncluded;
    }

    private static SyncPairPartitionRelation DecideCorrespondingCore(SyncPair candidate, SyncPair retained,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? candidateProofs,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? retainedProofs, out bool candidateIsInner,
        out string relative, out IReadOnlyList<SyncCorrespondingEndpointMatch> endpointMatches)
    {
        candidateIsInner = false;
        relative = string.Empty;
        endpointMatches = Array.Empty<SyncCorrespondingEndpointMatch>();
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(retained);
        var candidateEndpoints = SyncCloudPairConfiguration.SavedEndpoints(candidate);
        var retainedEndpoints = SyncCloudPairConfiguration.SavedEndpoints(retained);
        if (candidateEndpoints is null || retainedEndpoints is null) return SyncPairPartitionRelation.Unverified;
        if (!TryCorrespondingSubtree(candidateEndpoints, retainedEndpoints, candidateProofs, retainedProofs,
                out candidateIsInner, out relative, out endpointMatches))
            return SyncPairPartitionRelation.NestedRefused;
        var enclosing = candidateIsInner ? retained : candidate;
        var nested = candidateIsInner ? candidate : retained;
        var known = new[] { candidate, retained };
        // Reserved private control/recovery ownership inside the enclosing root is never handed to ordinary
        // participant data, and the relative subtree itself must not be a reserved path.
        if (IsReservedControlOrRecoveryPath(enclosing, known, relative))
            return SyncPairPartitionRelation.NestedRefused;
        if (!IncludesWholeSubtree(enclosing, relative)) return SyncPairPartitionRelation.NestedRefused;
        // The nested pair keeps its private control/staging/recovery authority independently: the enclosing
        // pair must itself exclude every owned path of the nested pair that falls inside its own roots,
        // exactly as for an excluded nested partition, or an included parent scan would adopt the child's
        // private staging as ordinary data.
        if (!OwnershipExcluded(enclosing, nested, includeDerivedLocalOwnership: true))
            return SyncPairPartitionRelation.NestedRefused;
        return SyncPairPartitionRelation.CorrespondingIncluded;
    }

    /// <summary>
    /// True only when every endpoint of one pair is nested, in the SAME enclosing direction and at the SAME
    /// component-relative path, with exactly one endpoint of the other pair. The mapping is bijective on both
    /// sides, so a reversed endpoint order is accepted but a missing second nested endpoint, an unequal
    /// translation, a mixed direction or an unverified opaque ancestry is refused rather than guessed.
    /// </summary>
    private static bool TryCorrespondingSubtree(SyncPairEndpoints candidateEndpoints,
        SyncPairEndpoints retainedEndpoints,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? candidateProofs,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? retainedProofs,
        out bool candidateIsInner, out string relative,
        out IReadOnlyList<SyncCorrespondingEndpointMatch> endpointMatches)
    {
        candidateIsInner = false;
        relative = string.Empty;
        endpointMatches = Array.Empty<SyncCorrespondingEndpointMatch>();
        var candidates = new[] { candidateEndpoints.Left, candidateEndpoints.Right };
        var retaineds = new[] { retainedEndpoints.Left, retainedEndpoints.Right };
        var matches = new List<SyncCorrespondingEndpointMatch>();
        var candidateMatches = new int[candidates.Length];
        var retainedMatches = new int[retaineds.Length];
        for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
        for (var retainedIndex = 0; retainedIndex < retaineds.Length; retainedIndex++)
        {
            if (!TryEndpointNesting(candidates[candidateIndex], retaineds[retainedIndex],
                    candidateProofs, retainedProofs, out var inner, out var nestedRelative)) continue;
            matches.Add(new SyncCorrespondingEndpointMatch(candidateIndex, retainedIndex, inner, nestedRelative));
            candidateMatches[candidateIndex]++;
            retainedMatches[retainedIndex]++;
        }
        if (matches.Count == 0) return false;
        if (candidateMatches.Any(count => count != 1) || retainedMatches.Any(count => count != 1)) return false;
        var first = matches[0];
        foreach (var match in matches)
        {
            if (match.CandidateEndpointIsInner != first.CandidateEndpointIsInner) return false;
            if (!string.Equals(match.Relative, first.Relative, StringComparison.OrdinalIgnoreCase)) return false;
        }
        candidateIsInner = first.CandidateEndpointIsInner;
        relative = first.Relative;
        endpointMatches = matches.ToArray();
        return relative.Length > 0;
    }

    /// <summary>
    /// One endpoint-to-endpoint nesting at a component boundary. A descriptive provider (or two local
    /// endpoints) resolves ancestry from its verified identity/path; a path-opaque provider resolves ONLY from
    /// a complete, independently bound provider-verified hierarchy proof, and an absent or unbound proof is
    /// refused rather than guessed.
    /// </summary>
    private static bool TryEndpointNesting(SyncEndpoint candidate, SyncEndpoint retained,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? candidateProofs,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? retainedProofs, out bool candidateIsInner,
        out string relative)
    {
        candidateIsInner = false;
        relative = string.Empty;
        if (candidate.IsLocal != retained.IsLocal) return false;
        if (!candidate.IsLocal && candidate.Kind != retained.Kind) return false;
        if (candidate.IsLocal || SyncEndpointProviders.Get(candidate.Kind).PathsDescribeAncestry)
        {
            var candidateUnderRetained = RelativeSubtree(retained, candidate);
            var retainedUnderCandidate = RelativeSubtree(candidate, retained);
            if (candidateUnderRetained is { Length: > 0 } && retainedUnderCandidate is null)
            {
                candidateIsInner = true;
                relative = candidateUnderRetained;
                return true;
            }
            if (retainedUnderCandidate is { Length: > 0 } && candidateUnderRetained is null)
            {
                candidateIsInner = false;
                relative = retainedUnderCandidate;
                return true;
            }
            return false;
        }
        if (OpaqueHierarchyRelation(candidate, retained, candidateProofs, retainedProofs,
                out var enclosingIsCandidate, out var opaqueRelative) != SyncAuthorityCloudRootRelation.Nested)
            return false;
        if (opaqueRelative.Length == 0) return false;
        candidateIsInner = !enclosingIsCandidate;
        relative = opaqueRelative;
        return true;
    }

    /// <summary>
    /// True only when the enclosing pair's own persisted policy proves the whole subtree rooted at the
    /// relative path is INCLUDED as ordinary participant data: the subtree root is included as a directory,
    /// nothing beneath it is ignored, and no explicit exclusion rule carves a hole at or below it. A
    /// default-included pair with no covering exclusion qualifies; a mixed or excluded policy does not.
    /// </summary>
    public static bool IncludesWholeSubtree(SyncPair enclosing, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(enclosing);
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var normalized = PilotPath.NormalizeRelative(relativePath);
        if (normalized.Length == 0) return false;
        var index = SyncSelectionPolicy.Compile(enclosing);
        if (!index.IsIncluded(normalized, isDirectory: true)) return false;
        if (!index.ShouldTraverseDirectory(normalized)) return false;
        foreach (var rule in SyncSelectionPolicy.Normalize(enclosing.SelectionRules))
        {
            if (rule.Included) continue;
            if (string.Equals(rule.FolderPath, normalized, StringComparison.OrdinalIgnoreCase) ||
                rule.FolderPath.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>
    /// The row-scoped admission decision for a corresponding INCLUDED nesting: the same shape as
    /// <see cref="AllowsNestedOverlapPath"/>, but the enclosing pair must include the whole relative subtree
    /// as ordinary data instead of excluding it. A private control/recovery row is never admitted by this path.
    /// </summary>
    public static bool AllowsCorrespondingIncludedPath(SyncPair candidate, SyncPair retained,
        string existingPath, string candidatePath, string? rowPairId, string? rowSide,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? candidateProofs = null,
        IReadOnlyList<SyncOpaqueCloudEndpointProof>? retainedProofs = null)
    {
        // Test-visible decision-tuple capture for the real admission transaction (D1). It records the exact
        // stage this row-scoped admission stopped at; production behaviour is unchanged and nothing consumes
        // the text outside the focused fixture.
        void Report(string stage) => CorrespondingAdmissionDiagnosticForTest?.Invoke(
            "stage=" + stage + " candidate=" + candidate.Id + " retained=" + retained.Id +
            " rowSide=" + rowSide + " existing=" + existingPath + " candidatePath=" + candidatePath +
            " decide=" + Decide(candidate, retained) + " corresponding=" + DecideCorrespondingIncluded(candidate, retained) +
            " subtree=" + (NestedSubtreeRelative(candidate, retained) ?? "-") +
            " subtreeReverse=" + (NestedSubtreeRelative(retained, candidate) ?? "-") +
            " includesWholeSubtree=" + IncludesWholeSubtree(candidate, NestedSubtreeRelative(candidate, retained) ?? "") +
            " excludesWholeSubtree=" + ExcludesWholeSubtree(candidate, NestedSubtreeRelative(candidate, retained) ?? "") +
            " candidateIsLocalRow=" + IsRepresentedRow(candidate, candidatePath, "local") +
            " candidateIsDropboxRow=" + IsRepresentedRow(candidate, candidatePath, "dropbox") +
            " existingIsLocalRow=" + IsRepresentedRow(candidate, existingPath, "local") +
            " existingIsDropboxRow=" + IsRepresentedRow(candidate, existingPath, "dropbox"));
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(retained);
        if (string.IsNullOrWhiteSpace(existingPath) || string.IsNullOrWhiteSpace(candidatePath))
        { Report("empty-path"); return false; }
        if (string.IsNullOrWhiteSpace(rowPairId) || string.IsNullOrWhiteSpace(rowSide))
        { Report("empty-row-binding"); return false; }
        var controlRow = rowSide.EndsWith("_control", StringComparison.Ordinal);
        if (!TryCorrespondingIncluded(candidate, retained, out var enclosing, out var nested,
                out _, out _, candidateProofs, retainedProofs))
        { Report("not-corresponding"); return false; }
        var candidateControl = IsRepresentedRow(candidate, candidatePath, "local_control") ||
            IsRepresentedRow(candidate, candidatePath, "dropbox_control");
        if (!candidateControl && !IsRepresentedRow(candidate, candidatePath, "local") &&
            !IsRepresentedRow(candidate, candidatePath, "dropbox"))
        { Report("candidate-row-unrepresented"); return false; }
        // Use the same proof-bound direction as the pair decision; display paths alone
        // cannot establish ancestry for an opaque cloud endpoint.
        var rowPair = string.Equals(rowPairId, enclosing.Id, StringComparison.Ordinal) ? enclosing
            : string.Equals(rowPairId, nested.Id, StringComparison.Ordinal) ? nested : null;
        if (rowPair is null) { Report("row-pair-unrepresented"); return false; }
        if (!IsRepresentedRow(rowPair, existingPath, rowSide)) { Report("existing-row-unrepresented"); return false; }
        if (candidateControl)
            return ReferenceEquals(nested, candidate) && !controlRow &&
                string.Equals(rowPairId, enclosing.Id, StringComparison.Ordinal) &&
                CoveredByEnclosingPolicy(enclosing, candidatePath);
        if (controlRow)
        {
            // A private control/staging row is NEVER waived by the pair-level INCLUDED relation itself. It is
            // admitted only when the row is the NESTED pair's own owned control subtree AND the enclosing pair
            // independently excludes that exact subtree, and never when the candidate root sits inside it. This
            // is the same exact ownership-and-exclusion proof the excluded partition requires, so a
            // corresponding INCLUDED nesting cannot weaken the private-control boundary in either direction.
            if (!string.Equals(rowPairId, nested.Id, StringComparison.Ordinal))
            { Report("control-row-not-nested-owned"); return false; }
            if (ContainsPath(existingPath, candidatePath))
            { Report("candidate-inside-control"); return false; }
            var admittedControl = CoveredByEnclosingPolicy(enclosing, existingPath);
            Report(admittedControl ? "admitted-control" : "control-not-excluded");
            return admittedControl;
        }
        var admitted = string.Equals(rowPairId, enclosing.Id, StringComparison.Ordinal)
            ? IncludedByEnclosingPolicy(enclosing, candidatePath)
            : IncludedByEnclosingPolicy(enclosing, existingPath);
        Report(admitted ? "admitted" : "enclosing-policy-does-not-include");
        return admitted;
    }

    /// <summary>Test-visible capture of the last corresponding-included row admission stage. Never consumed in production.</summary>
    internal static Action<string>? CorrespondingAdmissionDiagnosticForTest { get; set; }

    /// <summary>True when any enclosing endpoint places the absolute path at a whole-subtree-included relative location.</summary>
    private static bool IncludedByEnclosingPolicy(SyncPair enclosing, string absolutePath)
    {
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(enclosing);
        if (endpoints is null) return false;
        foreach (var endpoint in new[] { endpoints.Left, endpoints.Right })
        {
            string? relative;
            if (endpoint.IsLocal) relative = RelativeOwnershipPath(endpoint, absolutePath, true);
            else if (!string.IsNullOrWhiteSpace(endpoint.FolderPath))
                relative = RelativeOwnershipPath(endpoint, absolutePath, false);
            else continue;
            if (string.IsNullOrWhiteSpace(relative)) continue;
            if (IncludesWholeSubtree(enclosing, relative!)) return true;
        }
        return false;
    }

    /// <summary>
    /// The connected components of the supplied pairs that are joined by a corresponding INCLUDED nesting
    /// relation. Single-pair components are omitted. The grouping is derived from the current pair
    /// definitions and is scheduling input only: it never selects a content winner and never grants authority.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> RelatedOverlapGroups(IEnumerable<SyncPair> pairs)
        => RelatedOverlapGroups(pairs, null);

    /// <summary>
    /// The same connected components, resolved with the supplied independently bound opaque cloud endpoint
    /// proofs. A path-opaque provider relation is admitted only from a complete bound hierarchy proof, exactly
    /// as the typed cloud guard admits it; a caller that supplies no proof for a pair keeps the fail-closed
    /// refusal, so an opaque overlap is never grouped from a display path or from identity alone.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> RelatedOverlapGroups(IEnumerable<SyncPair> pairs,
        Func<SyncPair, IReadOnlyList<SyncOpaqueCloudEndpointProof>?>? proofsOf)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var list = pairs.Where(item => item is not null).ToArray();
        if (list.Length < 2) return Array.Empty<IReadOnlyList<string>>();
        var parent = list.Select(item => item.Id).Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => id, StringComparer.Ordinal);
        string Find(string id)
        {
            while (!string.Equals(parent[id], id, StringComparison.Ordinal))
            {
                parent[id] = parent[parent[id]];
                id = parent[id];
            }
            return id;
        }
        void Union(string left, string right)
        {
            var first = Find(left);
            var second = Find(right);
            if (!string.Equals(first, second, StringComparison.Ordinal)) parent[first] = second;
        }
        for (var left = 0; left < list.Length; left++)
        for (var right = left + 1; right < list.Length; right++)
            if (IsCorrespondingPair(list[left], list[right], proofsOf) ||
                IsCorrespondingPair(list[right], list[left], proofsOf))
                Union(list[left].Id, list[right].Id);
        return list.GroupBy(item => Find(item.Id), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => (IReadOnlyList<string>)group.Select(item => item.Id)
                .OrderBy(id => id, StringComparer.Ordinal).ToArray())
            .ToArray();
    }

    private static bool IsCorrespondingPair(SyncPair candidate, SyncPair retained,
        Func<SyncPair, IReadOnlyList<SyncOpaqueCloudEndpointProof>?>? proofsOf) =>
        DecideCorrespondingIncluded(candidate, retained, proofsOf?.Invoke(candidate), proofsOf?.Invoke(retained)) ==
        SyncPairPartitionRelation.CorrespondingIncluded;

    private static bool IsPhysicalLocalPath(string path) =>
        Path.IsPathFullyQualified(path) && !path.StartsWith("/", StringComparison.Ordinal);

    /// <summary>
    /// True only when the row's own path is the EXACT endpoint root or owned control subtree that its own
    /// retained side represents. The side-to-endpoint binding uses the existing retained compatibility
    /// projection: the local side carries the pair's own local root and its control namespace; the dropbox
    /// side carries the pair's remote root (a provider path for a cloud pair, or the right local root of a
    /// local/local pair) and its right control namespace. Locality alone is never sufficient, so a local row
    /// cannot be satisfied by the right/remote root and a control row cannot be satisfied by the other side.
    /// </summary>
    private static bool IsRepresentedRow(SyncPair pair, string rowPath, string rowSide)
    {
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(pair);
        if (endpoints is null) return false;
        switch (rowSide)
        {
            case "local":
                return RootMatches(SideEndpoint(endpoints, rightSide: false), rowPath);
            case "dropbox":
                return RootMatches(SideEndpoint(endpoints, rightSide: true), rowPath);
            case "local_control":
                return OwnedMatches(pair.ControlNamespace, rowPath);
            case "dropbox_control":
                return OwnedMatches(pair.RightControlNamespace, rowPath);
            default:
                return false;
        }
    }

    /// <summary>
    /// The endpoint a retained side carries. A pair with exactly one local endpoint keeps its local endpoint
    /// under the local tag and its non-local endpoint under the dropbox tag; a local/local or cloud/cloud pair
    /// keeps its logical left under the local tag and its logical right under the dropbox tag. The cloud/cloud
    /// left tag is therefore never assumed to be a local path.
    /// </summary>
    private static SyncEndpoint SideEndpoint(SyncPairEndpoints endpoints, bool rightSide)
    {
        var all = new[] { endpoints.Left, endpoints.Right };
        var local = all.Where(item => item.IsLocal).ToArray();
        if (local.Length == 1) return rightSide ? all.Single(item => !item.IsLocal) : local[0];
        return rightSide ? endpoints.Right : endpoints.Left;
    }

    private static bool RootMatches(SyncEndpoint endpoint, string rowPath)
    {
        var root = endpoint.IsLocal ? endpoint.LocalPath : endpoint.FolderPath;
        return !string.IsNullOrWhiteSpace(root) && SamePathForm(root!, rowPath);
    }

    /// <summary>
    /// A control row matches the side's exact owned control binding, whether that binding is the local private
    /// namespace or the physical provider control namespace (a cloud/cloud logical side carries its control as
    /// a remote binding, not a local path).
    /// </summary>
    private static bool OwnedMatches(SyncControlNamespaceBinding? binding, string rowPath)
    {
        if (binding is null) return false;
        if (!string.IsNullOrWhiteSpace(binding.Local.RootPath) && SamePathForm(binding.Local.RootPath, rowPath))
            return true;
        return binding.Remote is { } remote && !string.IsNullOrWhiteSpace(remote.RootPath) &&
               SamePathForm(remote.RootPath, rowPath);
    }

    /// <summary>True when both values name one path, tolerating the provider-path form of a local root.</summary>
    private static bool SamePathForm(string left, string right)
    {
        try
        {
            return string.Equals(PilotPath.NormalizeDropboxRoot(left), PilotPath.NormalizeDropboxRoot(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>True when <paramref name="inner"/> is the same as or inside <paramref name="outer"/>.</summary>
    private static bool ContainsPath(string outer, string inner)
    {
        try
        {
            var first = PilotPath.NormalizeDropboxRoot(outer).TrimEnd('/');
            var second = PilotPath.NormalizeDropboxRoot(inner).TrimEnd('/');
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
                   second.StartsWith(first + "/", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    private static bool CoveredByEnclosingPolicy(SyncPair enclosing, string absolutePath)
    {
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(enclosing);
        if (endpoints is null) return false;
        // The exact existing row may be stored under either side label (a local/local pair keeps its right
        // local root under the dropbox side), so the relative place is resolved against the real endpoint
        // shape rather than the requesting family.
        foreach (var endpoint in new[] { endpoints.Left, endpoints.Right })
        {
            string? relative;
            if (endpoint.IsLocal) relative = RelativeOwnershipPath(endpoint, absolutePath, true);
            else if (!string.IsNullOrWhiteSpace(endpoint.FolderPath))
                relative = RelativeOwnershipPath(endpoint, absolutePath, false);
            else continue;
            if (relative is null) continue;
            if (ExcludesWholeSubtree(enclosing, relative)) return true;
        }
        return false;
    }

    /// <summary>
    /// True only when the pair's existing persisted selection/ignore policy proves the entire subtree
    /// rooted at the relative path stays excluded. An excluded folder rule covers every descendant; an
    /// included exception strictly below the subtree invalidates the proof, and a default-included pair
    /// with no covering rule is never a proof.
    /// </summary>
    public static bool ExcludesWholeSubtree(SyncPair enclosing, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(enclosing);
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var normalized = PilotPath.NormalizeRelative(relativePath);
        var index = SyncSelectionPolicy.Compile(enclosing);
        if (index.IsIncluded(normalized, isDirectory: true)) return false;
        // ShouldTraverseDirectory is true when the subtree root is included or an included exception
        // exists strictly below it; either case invalidates the whole-subtree exclusion proof.
        if (index.ShouldTraverseDirectory(normalized)) return false;
        return true;
    }

    /// <summary>
    /// True when the nested relative subtree names private Sync Shark control or recovery ownership
    /// rather than ordinary participant data. Such a subtree is never handed to a parent pair even when
    /// an exclusion rule appears to cover it.
    /// </summary>
    public static bool IsReservedControlOrRecoveryPath(SyncPair enclosing, IEnumerable<SyncPair>? knownPairs,
        string relativePath)
    {
        ArgumentNullException.ThrowIfNull(enclosing);
        if (string.IsNullOrWhiteSpace(relativePath)) return true;
        var normalized = PilotPath.NormalizeRelative(relativePath);
        // The absolute place is resolved from BOTH typed endpoints of the enclosing pair, never from the
        // legacy single LocalRoot/DropboxRoot projection, so a reversed or cloud/cloud pair is covered.
        var (locals, clouds) = AbsolutePlaces(enclosing, normalized);
        foreach (var cloud in clouds)
            if (IsDropboxAtOrBelow(SyncDropboxArtifactPaths.ProductContainerPath, cloud)) return true;
        foreach (var pair in knownPairs ?? Array.Empty<SyncPair>())
        {
            if (pair is null) continue;
            foreach (var (isLocal, owned) in OwnedPaths(pair))
            {
                if (isLocal)
                {
                    foreach (var local in locals)
                        if (IsLocalAtOrBelow(owned, local)) return true;
                }
                else
                {
                    foreach (var cloud in clouds)
                        if (IsDropboxAtOrBelow(owned, cloud)) return true;
                }
            }
        }
        return false;
    }

    /// <summary>Every real absolute place one pair-relative path names: one per local and one per cloud endpoint.</summary>
    private static (List<string> Locals, List<string> Clouds) AbsolutePlaces(SyncPair pair, string relativePath)
    {
        var locals = new List<string>();
        var clouds = new List<string>();
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(pair);
        if (endpoints is null) return (locals, clouds);
        foreach (var endpoint in new[] { endpoints.Left, endpoints.Right })
        {
            if (endpoint.IsLocal)
            {
                try
                {
                    locals.Add(PilotPath.NormalizeLocalRoot(Path.Combine(endpoint.LocalPath!,
                        relativePath.Replace('/', Path.DirectorySeparatorChar))));
                }
                catch (ArgumentException) { }
            }
            else if (!string.IsNullOrWhiteSpace(endpoint.FolderPath))
            {
                try { clouds.Add(PilotPath.NormalizeDropboxRoot(endpoint.FolderPath + "/" + relativePath)); }
                catch (ArgumentException) { }
            }
        }
        return (locals, clouds);
    }

    /// <summary>
    /// The effect-admission fence for a nested partition: a directory effect at the pair-relative path
    /// is refused when that directory is the excluded nested child subtree itself or an ancestor of it.
    /// The ancestor is refused even though it may be included, because a recursive move or delete would
    /// otherwise reach the child subtree the partition promises to leave to the child alone.
    /// </summary>
    public static bool RefusesAncestorDirectoryEffect(SyncPair enclosing, IEnumerable<SyncPair>? knownPairs,
        string pairRelativePath, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(enclosing);
        if (!isDirectory || string.IsNullOrWhiteSpace(pairRelativePath)) return false;
        var effect = PilotPath.NormalizeRelative(pairRelativePath);
        foreach (var other in knownPairs ?? Array.Empty<SyncPair>())
        {
            if (other is null || ReferenceEquals(other, enclosing)) continue;
            var subtree = NestedSubtreeRelative(enclosing, other);
            if (subtree is null) continue;
            if (!ExcludesWholeSubtree(enclosing, subtree)) continue;
            if (IsReservedControlOrRecoveryPath(enclosing, knownPairs, subtree)) continue;
            if (IsComponentAncestorOrEqual(effect, subtree)) return true;
        }
        return false;
    }

    /// <summary>The relative subtree of another pair nested under the enclosing pair, or null.</summary>
    public static string? NestedSubtreeRelative(SyncPair enclosing, SyncPair nested)
    {
        ArgumentNullException.ThrowIfNull(enclosing);
        ArgumentNullException.ThrowIfNull(nested);
        var enclosingEndpoints = SyncCloudPairConfiguration.SavedEndpoints(enclosing);
        var nestedEndpoints = SyncCloudPairConfiguration.SavedEndpoints(nested);
        if (enclosingEndpoints is null || nestedEndpoints is null) return null;
        string? subnet = null;
        var found = 0;
        foreach (var enclosed in new[] { enclosingEndpoints.Left, enclosingEndpoints.Right })
        foreach (var candidate in new[] { nestedEndpoints.Left, nestedEndpoints.Right })
        {
            if (enclosed.IsLocal != candidate.IsLocal) continue;
            if (!enclosed.IsLocal && enclosed.Kind != candidate.Kind) continue;
            if (!IsComparable(enclosed, candidate)) continue;
            var relative = RelativeSubtree(enclosed, candidate);
            // A non-nesting endpoint pair (the two cross pairings of a local/local pair, for example) is
            // simply not a nested side; only the sides that really nest contribute their relative subtree.
            if (relative is null) continue;
            found++;
            if (subnet is null) subnet = relative;
            else if (!string.Equals(subnet, relative, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return found == 0 ? null : subnet;
    }

    private static bool IsComparable(SyncEndpoint enclosing, SyncEndpoint nested)
    {
        if (enclosing.IsLocal) return true;
        return SyncEndpointProviders.Get(enclosing.Kind).PathsDescribeAncestry;
    }

    private static bool EqualEndpoint(SyncEndpoint left, SyncEndpoint right)
    {
        if (SyncEndpoints.ReferencesSameStore(left, right)) return true;
        if (left.IsLocal && right.IsLocal)
            return string.Equals(LocalRootKey(left.LocalPath!), LocalRootKey(right.LocalPath!), StringComparison.Ordinal);
        if (!left.IsLocal && !right.IsLocal && left.Kind == right.Kind)
        {
            if (SyncEndpointProviders.Get(left.Kind).ObjectIdsAreProviderGlobal &&
                !string.IsNullOrWhiteSpace(left.FolderId) && !string.IsNullOrWhiteSpace(right.FolderId))
                return string.Equals(left.FolderId, right.FolderId, StringComparison.Ordinal);
            // A presentation path is never Same across two different verified access routes: two other
            // verified namespaces may legitimately carry equal display paths. Path equality decides Same
            // only inside one route, and only for a provider whose paths describe ancestry.
            if (SyncEndpointProviders.Get(left.Kind).PathsDescribeAncestry &&
                string.Equals(CloudRoute(left), CloudRoute(right), StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(left.FolderPath) && !string.IsNullOrWhiteSpace(right.FolderPath))
                return string.Equals(FolderPathKey(left.FolderPath!), FolderPathKey(right.FolderPath!), StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>The verified access route of a cloud endpoint: the namespace when verified, else the account.</summary>
    private static string CloudRoute(SyncEndpoint endpoint) =>
        endpoint.NamespaceId ?? endpoint.ConnectionId ?? string.Empty;

    /// <summary>True when the candidate's identity proves the two endpoints cannot name one store.</summary>
    private static bool ProvablyDistinct(SyncEndpoint left, SyncEndpoint right)
    {
        if (left.IsLocal != right.IsLocal) return true;
        if (left.IsLocal)
        {
            // Two local roots whose paths are not nested cannot name one store or a nested subtree, so
            // the path itself proves them distinct. A nested/equal path pair needs the verified physical
            // identity (handled by the equal/nested branches above), which is why an absent identity there
            // is refused rather than reported distinct.
            return !LocalRootersOverlap(left.LocalPath!, right.LocalPath!);
        }
        if (left.Kind != right.Kind) return true;
        // Defer to the established endpoint overlap contract. A namespace difference alone is not object
        // disjointness for a provider-global or shared mount, and an incomplete cross-route hierarchy is
        // Unverified rather than automatically Distinct; opaque provider ancestry is never fabricated here.
        return SyncEndpointOverlap.Compare(left, right) == SyncCloudOverlap.Distinct;
    }

    private static bool LocalRootersOverlap(string left, string right)
    {
        var first = LocalRootKey(left);
        var second = LocalRootKey(right);
        var separator = Path.DirectorySeparatorChar;
        return string.Equals(first, second, StringComparison.Ordinal) ||
               first.StartsWith(second + separator, StringComparison.Ordinal) ||
               second.StartsWith(first + separator, StringComparison.Ordinal);
    }

    /// <summary>The component-boundary relative path of <paramref name="nested"/> under the enclosing endpoint, or null.</summary>
    private static string? RelativeSubtree(SyncEndpoint enclosing, SyncEndpoint nested)
    {
        if (enclosing.IsLocal != nested.IsLocal) return null;
        if (enclosing.IsLocal)
        {
            if (enclosing.LocalIdentity is null || nested.LocalIdentity is null) return null;
            if (enclosing.LocalIdentity == nested.LocalIdentity) return null;
            return ComponentSubtree(LocalRootKey(enclosing.LocalPath!), LocalRootKey(nested.LocalPath!),
                Path.DirectorySeparatorChar);
        }
        if (enclosing.Kind != nested.Kind) return null;
        if (!SyncEndpointProviders.Get(enclosing.Kind).PathsDescribeAncestry) return null;
        if (string.IsNullOrWhiteSpace(enclosing.NamespaceId) || string.IsNullOrWhiteSpace(nested.NamespaceId) ||
            !string.Equals(enclosing.NamespaceId, nested.NamespaceId, StringComparison.Ordinal)) return null;
        if (string.IsNullOrWhiteSpace(enclosing.FolderPath) || string.IsNullOrWhiteSpace(nested.FolderPath)) return null;
        return ComponentSubtree(FolderPathKey(enclosing.FolderPath!), FolderPathKey(nested.FolderPath!), '/');
    }

    /// <summary>The relative path of <paramref name="nested"/> when it is a strict component descendant of <paramref name="enclosing"/>, else null.</summary>
    private static string? ComponentSubtree(string enclosingKey, string nestedKey, char separator)
    {
        if (string.IsNullOrWhiteSpace(enclosingKey) || string.IsNullOrWhiteSpace(nestedKey)) return null;
        if (string.Equals(enclosingKey, nestedKey, StringComparison.Ordinal)) return null;
        // The provider root ("\\" for local volumes, "/" for a cloud namespace) is the only key whose
        // separator-trimmed form is empty, so its descendants are a plain prefix without a doubled separator.
        if (enclosingKey == "\\" || enclosingKey == "/")
            return nestedKey.TrimStart(separator, '/', '\\');
        if (!nestedKey.StartsWith(enclosingKey + separator, StringComparison.Ordinal)) return null;
        var relative = nestedKey[(enclosingKey.Length + 1)..];
        return relative.Length == 0 ? null : relative;
    }

    private static bool IsComponentAncestorOrEqual(string effect, string subtree)
    {
        var first = effect.Trim('/');
        var second = subtree.Trim('/');
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
               second.StartsWith(first + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalAtOrBelow(string root, string candidate)
    {
        var normalizedRoot = PilotPath.NormalizeLocalRoot(root);
        var normalizedCandidate = PilotPath.NormalizeLocalRoot(candidate);
        return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDropboxAtOrBelow(string root, string candidate)
    {
        var normalizedRoot = PilotPath.NormalizeDropboxRoot(root);
        var normalizedCandidate = PilotPath.NormalizeDropboxRoot(candidate);
        if (normalizedRoot == "/") return true;
        return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string LocalRootKey(string path)
    {
        try { return PilotPath.NormalizeLocalRoot(path).ToUpperInvariant(); }
        catch (ArgumentException) { return path.TrimEnd('\\').ToUpperInvariant(); }
    }

    private static string FolderPathKey(string value)
    {
        var normalized = "/" + string.Join('/', value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        var trimmed = normalized.TrimEnd('/');
        return (trimmed.Length == 0 ? "/" : trimmed).ToUpperInvariant();
    }
}
