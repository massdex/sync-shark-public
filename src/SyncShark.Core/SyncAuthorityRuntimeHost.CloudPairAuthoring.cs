using System.Runtime.Versioning;
using System.Text.Json;

namespace Massdex.SyncShark;

// A narrow, typed review-required outcome shared by the authoring UI and CLI. The CLI must not
// classify arbitrary provider failures as a review request or infer this outcome from message text.
internal sealed class SyncPairAuthoringReviewException(string message) : InvalidOperationException(message);

public sealed partial class SyncAuthorityRuntimeHost
{
    /// <summary>
    /// One operation-scoped provider-verified partition proof: the two exact pair definitions and the
    /// independently resolved opaque cloud endpoint proofs for each. It carries no live effect and allocates
    /// no control/recovery directory; a consumer must re-check the pair revision before it acts on it.
    /// </summary>
    internal sealed record SyncAuthorityOpaquePartitionProof(SyncPair Candidate, SyncPair Retained,
        IReadOnlyList<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof> CandidateProofs,
        IReadOnlyList<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof> RetainedProofs);

    /// <summary>
    /// Reads the provider-verified ancestry of one pair's opaque cloud endpoint through that endpoint's OWN
    /// retained account and root, or returns null when no such proof exists. Uses only the existing
    /// ResolveCloudSide/ReadCandidateHierarchyAsync readers; EndpointHierarchy.cs is a descendant selection
    /// reader and is deliberately not consulted.
    /// </summary>
    /// <summary>One opaque cloud side's proof plus every held fact that must survive the awaited reads.</summary>
    private sealed record OpaqueSideProof(string PairId, string Port, SyncEndpointKind Kind, string ConnectionId,
        CloudAccountFacts Facts, SyncAuthorityRetainedRootAuthority? HeldAuthority,
        SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof Proof);

    /// <summary>
    /// D2 readback: one opaque side's provider-verified hierarchy for the shared partition proof. A side whose
    /// own root is already PUBLISHED is first read live from its provider, exactly as before. When that read
    /// fails because the provider INPUT is unavailable - an absent transport status or a 5xx server response,
    /// never a 4xx authoritative refusal and never a malformed or mismatched result - the pair's own RETAINED
    /// observation of the exact same root, generation, account, namespace and folder supplies the bound context
    /// instead. A side with no published root (a new or re-bound candidate) has no retained root to fall back
    /// to, so the live read stays mandatory and no effect path is weakened.
    /// </summary>
    private async Task<SyncAuthorityCloudHierarchySnapshot?> ReadOpaqueSideHierarchyAsync(string pairId,
        string port, string rootBindingId, SyncAuthorityRetainedRootAuthority? held, CloudSideResolution resolution,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCandidateHierarchyAsync(resolution, rootBindingId, cancellationToken);
        }
        catch (HttpRequestException error) when (held is not null &&
            (error.StatusCode is null || (int)error.StatusCode >= 500))
        {
            var retained = await _authority.ResolveRetainedOpaqueHierarchyAsync(pairId, port, cancellationToken);
            if (retained is null ) return null;
            if (!string.Equals(retained.RootBindingId, rootBindingId, StringComparison.Ordinal) ||
                retained.RootGeneration != held.Generation) return null;
            if (!string.Equals(retained.Snapshot.Provider, resolution.Facts.Provider, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(retained.Snapshot.ProviderAccountId, resolution.Facts.ProviderAccountId,
                    StringComparison.Ordinal)) return null;
            return retained.Snapshot;
        }
    }

    /// <summary>
    /// Every comparable opaque cloud side of one pair, each with its OWN compatibility port, held account facts
    /// and (for a published pair) the whole retained authority record. A cloud/cloud pair contributes both
    /// opaque sides; a planned unpublished candidate side is exempt ONLY for the exact port present in the map.
    /// </summary>
    private async Task<IReadOnlyList<OpaqueSideProof>?> ReadOpaqueProofsAsync(SyncPair pair,
        IReadOnlyDictionary<string, string>? plannedRootBindingIds, CancellationToken cancellationToken)
    {
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(pair);
        if (endpoints is null) return null;
        var sides = new List<OpaqueSideProof>();
        // Every ordered side is considered with its OWN compatibility port; the ordered Left is never assumed
        // to be the DB-local side.
        foreach (var (side, orderedSide) in new[]
                 { (endpoints.Left, SyncPairSide.Left), (endpoints.Right, SyncPairSide.Right) })
        {
            if (side.IsLocal || side.Kind == SyncEndpointKind.Local) continue;
            if (SyncEndpointProviders.Get(side.Kind).PathsDescribeAncestry) continue;
            if (string.IsNullOrWhiteSpace(side.ConnectionId) || string.IsNullOrWhiteSpace(side.FolderId)) return null;
            var facts = ResolveCloudAccount(side.Kind, side.ConnectionId!);
            var namespaceId = side.NamespaceId;
            if (namespaceId is null && pair.EndpointDocument is null)
                namespaceId = SyncAuthorityProviderAccountIdentity.RetainedEndpointNamespace(facts.Provider,
                    facts.AccountId, facts.RootNamespaceId, facts.HomeNamespaceId);
            if (string.IsNullOrWhiteSpace(namespaceId)) return null;
            var port = SyncEndpointContext.CompatibilityPort(endpoints.Left.Kind, endpoints.Right.Kind, orderedSide);
            string? rootBindingId;
            SyncAuthorityRetainedRootAuthority? held = null;
            if (plannedRootBindingIds is { } planned && planned.TryGetValue(port, out var plannedId))
                rootBindingId = plannedId;   // planned exemption for THIS port key only
            else
            {
                rootBindingId = await _authority.LoadActiveRootBindingIdAsync(pair.Id, port, cancellationToken);
                if (string.IsNullOrWhiteSpace(rootBindingId)) return null;
                held = await _authority.LoadRetainedRootAuthorityAsync(pair.Id, port, cancellationToken);
                if (held is null ||
                    !string.Equals(held.RootBindingId, rootBindingId, StringComparison.Ordinal) ||
                    !string.Equals(held.PairId, pair.Id, StringComparison.Ordinal) ||
                    !string.Equals(held.Side, port, StringComparison.Ordinal) || held.Generation < 1 ||
                    !held.HasRetainedProviderAccount) return null;
            }
            if (string.IsNullOrWhiteSpace(rootBindingId)) return null;
            if (held is not null &&
                (!string.Equals(held.IdentityProviderAccountId, facts.ProviderAccountId, StringComparison.Ordinal) ||
                 !string.Equals(held.IdentityFolderId, side.FolderId, StringComparison.Ordinal) ||
                 held.IsExplicitCloudIdentity &&
                 (!string.Equals(held.IdentityProvider, facts.Provider, StringComparison.OrdinalIgnoreCase) ||
                  !string.Equals(held.IdentityNamespaceId, namespaceId, StringComparison.Ordinal)) ||
                 !string.Equals(held.AccountProvider, facts.Provider, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(held.AccountId, facts.AccountId, StringComparison.Ordinal) ||
                 !string.Equals(held.AccountRootNamespaceId, facts.RootNamespaceId, StringComparison.Ordinal) ||
                 !string.Equals(held.AccountHomeNamespaceId, facts.HomeNamespaceId, StringComparison.Ordinal)))
                return null;
            var endpoint = SyncEndpoint.Cloud(side.Kind, side.ConnectionId!, namespaceId, side.FolderId,
                side.FolderPath);
            var resolution = ResolveCloudSide(SyncEndpointRecord.From(endpoint));
            var snapshot = await ReadOpaqueSideHierarchyAsync(pair.Id, port, rootBindingId!, held, resolution,
                cancellationToken);
            if (snapshot is null) return null;
            sides.Add(new OpaqueSideProof(pair.Id, port, side.Kind, side.ConnectionId!, facts, held,
                new SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof(snapshot, facts.ProviderAccountId,
                    rootBindingId!, new SyncAuthorityCloudRootIdentity(facts.Provider, facts.ProviderAccountId,
                        namespaceId, side.FolderId!, side.FolderPath, rootBindingId!,
                        SyncAuthorityCloudRootIdentity.PairRootAuthority, port))));
        }
        return sides;
    }

    /// <summary>
    /// The one operation-scoped proof a caller may pass to the shared partition decision, or null when either
    /// side has no provider-verified opaque proof or either pair definition changed during the awaited reads.
    /// </summary>
    internal async Task<SyncAuthorityOpaquePartitionProof?> BuildOpaquePartitionProofAsync(SyncPair candidate,
        IReadOnlyDictionary<string, string>? plannedCandidateRootBindingIds, SyncPair? retainedCandidatePrior,
        SyncPair retained, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(retained);
        // Capture the full normalized definitions and the relevant retained-set identity BEFORE any provider I/O.
        var before = await _authority.LoadAllPairDefinitionsAsync(cancellationToken, includeRetired: false);
        var candidateDefinition = PairDefinition(candidate);
        var retainedDefinition = PairDefinition(retained);
        var priorDefinition = retainedCandidatePrior is null ? null : PairDefinition(retainedCandidatePrior);
        var heldRetained = before.SingleOrDefault(item => string.Equals(item.Id, retained.Id, StringComparison.Ordinal));
        if (heldRetained is null || PairDefinition(heldRetained) != retainedDefinition) return null;
        if (retainedCandidatePrior is null)
        {
            if (before.Any(item => string.Equals(item.Id, candidate.Id, StringComparison.Ordinal))) return null;
        }
        else
        {
            var heldPrior = before.SingleOrDefault(item => item.Id == retainedCandidatePrior.Id);
            if (heldPrior is null || PairDefinition(heldPrior) != priorDefinition) return null;
        }
        var candidateSides = await ReadOpaqueProofsAsync(candidate, plannedCandidateRootBindingIds,
            cancellationToken);
        var retainedSides = await ReadOpaqueProofsAsync(retained, null, cancellationToken);
        if (candidateSides is null || retainedSides is null) return null;
        // AFTER all provider I/O, re-hold each published side's WHOLE retained authority record (Generation and
        // IdentityJson included) and each side's OWN re-resolved account facts; any change refuses.
        foreach (var side in candidateSides.Concat(retainedSides))
        {
            var ownerId = side.PairId;
            if (side.HeldAuthority is { } held)
            {
                var again = await _authority.LoadRetainedRootAuthorityAsync(ownerId, side.Port, cancellationToken);
                if (again is null || !Equals(again, held)) return null;
            }
            var factsAgain = ResolveCloudAccount(side.Kind, side.ConnectionId);
            if (factsAgain != side.Facts) return null;
        }
        // After the awaited reads, require the exact full definitions and the retained-set identity to be
        // unchanged: Id+PolicyRevision alone is insufficient, and an edited draft must never be compared to its
        // persisted prior definition as though it were unchanged.
        var after = await _authority.LoadAllPairDefinitionsAsync(cancellationToken, includeRetired: false);
        if (!SameDefinitionSet(before, after) || PairDefinition(candidate) != candidateDefinition ||
            PairDefinition(retained) != retainedDefinition) return null;
        if (retainedCandidatePrior is null)
        {
            // A new candidate must still be absent from the retained set BY ID, regardless of revision.
            if (after.Any(item => string.Equals(item.Id, candidate.Id, StringComparison.Ordinal))) return null;
        }
        else
        {
            var prior = after.FirstOrDefault(item =>
                string.Equals(item.Id, retainedCandidatePrior.Id, StringComparison.Ordinal));
            if (prior is null || PairDefinition(prior) != priorDefinition ||
                PairDefinition(retainedCandidatePrior) != priorDefinition) return null;
        }
        return new SyncAuthorityOpaquePartitionProof(candidate, retained,
            candidateSides.Select(item => item.Proof).ToArray(),
            retainedSides.Select(item => item.Proof).ToArray());
    }

    private static bool SameDefinitionSet(IReadOnlyList<SyncPair> before, IReadOnlyList<SyncPair> after)
    {
        if (before.Count != after.Count) return false;
        foreach (var left in before)
        {
            var right = after.FirstOrDefault(item => string.Equals(item.Id, left.Id, StringComparison.Ordinal));
            if (right is null || !string.Equals(PairDefinition(left), PairDefinition(right),
                    StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static string PairDefinition(SyncPair pair) =>
        JsonSerializer.Serialize(SyncPairStore.NormalizePair(pair));

    /// <summary>
    /// The independently bound opaque cloud endpoint proofs of one pair for RELATED-GROUP scheduling. The proof
    /// is taken from the pair's own FROZEN verified hierarchy snapshot - the same provider-verified chain the
    /// prepared cloud context already compared against the retained authority - and bound to the exact ordered
    /// endpoint through the existing compatibility port. A pair with no prepared cloud context, or an opaque
    /// endpoint with no complete bound chain, yields null so the shared grouping keeps its fail-closed refusal;
    /// no display path and no identity-only guess is ever introduced. A path-descriptive pair yields an empty
    /// proof set and keeps its existing ancestry comparison.
    /// </summary>
    private static IReadOnlyList<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof>? BoundOpaqueProofsForGrouping(
        SyncPair pair, IReadOnlyDictionary<string, SyncAuthorityPreparedCloudPair> preparedCloudByPair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (!preparedCloudByPair.TryGetValue(pair.Id, out var prepared) || prepared is null) return null;
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(pair);
        if (endpoints is null) return null;
        var proofs = new List<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof>();
        foreach (var (endpoint, orderedSide) in new[]
                 {
                     (endpoints.Left, SyncPairSide.Left), (endpoints.Right, SyncPairSide.Right)
                 })
        {
            if (endpoint.IsLocal || SyncEndpointProviders.Get(endpoint.Kind).PathsDescribeAncestry) continue;
            if (string.IsNullOrWhiteSpace(endpoint.FolderId) ||
                string.IsNullOrWhiteSpace(endpoint.NamespaceId)) return null;
            var provider = SyncEndpoints.ProviderLiteral(endpoint.Kind);
            var snapshot = prepared.HierarchySnapshots.FirstOrDefault(item => item is not null &&
                string.Equals(item.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.NamespaceId, endpoint.NamespaceId, StringComparison.Ordinal) &&
                string.Equals(item.FolderId, endpoint.FolderId, StringComparison.Ordinal));
            if (snapshot is null) return null;
            var port = SyncEndpointContext.CompatibilityPort(endpoints.Left.Kind, endpoints.Right.Kind,
                orderedSide);
            var identity = new SyncAuthorityCloudRootIdentity(snapshot.Provider, snapshot.ProviderAccountId,
                snapshot.NamespaceId, endpoint.FolderId!, endpoint.FolderPath, snapshot.RootBindingId,
                SyncAuthorityCloudRootIdentity.PairRootAuthority, port);
            if (!snapshot.BindsTo(identity) || !snapshot.IsCompleteFor(identity)) return null;
            proofs.Add(new SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof(snapshot,
                snapshot.ProviderAccountId, snapshot.RootBindingId, identity));
        }
        return proofs;
    }

    /// <summary>
    /// One retained pair's operation-scoped comparison projection together with the exact retained facts it
    /// was derived from, so a caller can re-hold them across its own awaited reads.
    /// </summary>
    internal sealed record RetainedEndpointProjection(SyncPair Comparison,
        IReadOnlyList<(string Port, SyncAuthorityRetainedRootAuthority Authority)> HeldFacts,
        bool IsComplete = true)
    {
        internal async Task<bool> StillHeldAsync(SyncAuthorityDatabase authority, string pairId,
            CancellationToken cancellationToken)
        {
            foreach (var (port, held) in HeldFacts)
            {
                var again = await authority.LoadRetainedRootAuthorityAsync(pairId, port, cancellationToken);
                if (again is null || !Equals(again, held)) return false;
            }
            return true;
        }
    }

    /// <summary>One ordered cloud side's own retained namespace plus the row it was re-established from.</summary>
    private sealed record RetainedCloudNamespace(string NamespaceId, SyncAuthorityRetainedRootAuthority Authority);

    /// <summary>
    /// The operation-scoped comparison form of ONE retained pair for the shared partition comparator. A pair
    /// that was published before the explicit endpoint document existed keeps no namespace in its legacy
    /// compatibility projection, so the comparator cannot establish the cloud ancestry the product already
    /// verified for it. This resolves each ordered cloud side through its OWN compatibility port and its OWN
    /// retained root/provider-account rows and returns a comparison copy whose cloud side carries that
    /// already-retained namespace. It rewrites no retained definition, inherits nothing from the candidate or
    /// another pair and fabricates no namespace: an explicit endpoint document is always its own source of
    /// truth and is returned unchanged, and a missing or inactive root binding, a missing/inactive provider
    /// account or a mismatched identity tuple leaves the raw side unchanged but marks the comparison
    /// incomplete. Authoring must refuse that comparison: an unchecked connection can otherwise look like
    /// a different account. The namespace, folder, account, generation and tuple gates remain unchanged.
    /// </summary>
    internal async Task<RetainedEndpointProjection> ProjectRetainedPairEndpointsAsync(SyncPair pair,
        CancellationToken cancellationToken, bool projectSidesLackingNamespaceEvenWithDocument = false)
    {
        // A retained peer's explicit document is its own source of truth and is returned unchanged. The
        // CANDIDATE projection sets the flag so a document synthesised from the legacy shape - which carries no
        // cloud namespace of its own - is still projected from that same pair's OWN retained authority.
        if (pair.EndpointDocument is not null && !projectSidesLackingNamespaceEvenWithDocument)
            return new RetainedEndpointProjection(pair, []);
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(pair);
        if (endpoints is null) return new RetainedEndpointProjection(pair, [], IsComplete: false);
        var ordered = new[] { endpoints.Left, endpoints.Right };
        var projected = new SyncEndpoint?[2];
        var held = new List<(string Port, SyncAuthorityRetainedRootAuthority Authority)>();
        for (var index = 0; index < ordered.Length; index++)
        {
            var side = ordered[index];
            projected[index] = side;
            // Only a document-less cloud side with no namespace of its own is projected, and only from its
            // own retained authority; every other side (and every explicit document) is left untouched.
            if (side.IsLocal || SyncEndpointProviders.Get(side.Kind).ProviderId is null) continue;
            if (!string.IsNullOrWhiteSpace(side.NamespaceId)) continue;
            if (string.IsNullOrWhiteSpace(side.ConnectionId) || string.IsNullOrWhiteSpace(side.FolderId))
                return new RetainedEndpointProjection(pair, held, IsComplete: false);
            var port = SyncEndpointContext.CompatibilityPort(endpoints.Left.Kind, endpoints.Right.Kind,
                index == 0 ? SyncPairSide.Left : SyncPairSide.Right);
            if (await ReadOwnRetainedCloudNamespaceAsync(pair.Id, port, side, cancellationToken)
                    is not { } retained)
                return new RetainedEndpointProjection(pair, held, IsComplete: false);
            projected[index] = SyncEndpoint.Cloud(side.Kind, side.ConnectionId!, retained.NamespaceId,
                side.FolderId, side.FolderPath);
            held.Add((port, retained.Authority));
        }
        if (held.Count == 0) return new RetainedEndpointProjection(pair, []);
        var document = new SyncEndpointDocument(SyncEndpointSchema.SymmetricEndpoints,
            SyncEndpointRecord.From(projected[0]!), SyncEndpointRecord.From(projected[1]!));
        return new RetainedEndpointProjection(pair with
        {
            EndpointSchemaVersion = document.Version,
            LeftEndpoint = document.Left,
            RightEndpoint = document.Right
        }, held);
    }

    /// <summary>
    /// The namespace this pair's OWN retained authority already verified for one ordered cloud side, or null
    /// when that tuple cannot be re-established. It reuses the existing retained provider-account mapping and
    /// a root binding that is active, current, generation-bearing and this pair's own, and it refuses on any
    /// missing, inactive or mismatched fact rather than substituting the candidate's account, a display path,
    /// another pair's row or a provider default.
    /// </summary>
    private async Task<RetainedCloudNamespace?> ReadOwnRetainedCloudNamespaceAsync(string pairId, string port,
        SyncEndpoint side, CancellationToken cancellationToken)
    {
        CloudAccountFacts facts;
        string? rootBindingId;
        SyncAuthorityRetainedRootAuthority? authority;
        try
        {
            // The Host keeps its own live connection-store tuple validation: the endpoint's exact account is
            // resolved from the registered connection, never from a display path or another pair's account.
            facts = ResolveCloudAccount(side.Kind, side.ConnectionId);
            if (string.IsNullOrWhiteSpace(facts.ProviderAccountId)) return null;
            rootBindingId = await _authority.LoadActiveRootBindingIdAsync(pairId, port, cancellationToken);
            if (string.IsNullOrWhiteSpace(rootBindingId)) return null;
            authority = await _authority.LoadRetainedRootAuthorityAsync(pairId, port, cancellationToken);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            NotSupportedException or GoogleDriveBlockedException or InvalidDataException)
        {
            return null;
        }
        if (authority is null || !string.Equals(authority.RootBindingId, rootBindingId, StringComparison.Ordinal))
            return null;
        // The ONE shared strict projector decides the projection (own pair, compatibility port, generation,
        // provider-account derivation, folder, standard connection route, namespace and explicit identity);
        // the Host supplies its live connection facts and keeps the held row for the across-await recheck.
        var endpoint = SyncAuthorityRetainedEndpointProjection.Project(pairId, port, side, authority,
            facts.Provider, facts.AccountId, facts.RootNamespaceId, facts.HomeNamespaceId);
        return endpoint?.NamespaceId is { Length: > 0 } namespaceId
            ? new RetainedCloudNamespace(namespaceId, authority)
            : null;
    }

    // Availability can defer opaque ancestry; authoring cannot. This operation-scoped
    // decision is completed before Recovery/control allocation or any pair publication.
    /// <summary>Diagnostic hook: the exact pair ids this production decision compares.</summary>
    internal static Action<string>? PartitionComparisonProbeForTest { get; set; }

    private async Task<IReadOnlyDictionary<string, SyncPairPartitionRelation>> RequireVerifiedPairPartitionsAsync(
        SyncPair candidate, string? replacingPairId, CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        // Retired definitions remain available for history/recovery, but no longer own data
        // folders. Their root bindings are deliberately out-of-scope after retirement.
        // Compare only current owners and recheck that same set after awaited provider reads.
        var before = await _authority.LoadAllPairDefinitionsAsync(cancellationToken, includeRetired: false);
        var prior = before.SingleOrDefault(item => item.Id == candidate.Id);
        var definition = PairDefinition(candidate);
        // root702(b): the CANDIDATE's own document-less cloud sides are projected from the candidate's OWN
        // retained authority exactly like the retained peer, so a pair published before the explicit endpoint
        // document existed is compared with the namespace this product retains for that exact pair. Only the
        // endpoint namespace identity is projected; the candidate's requested policy is preserved unchanged.
        var comparisonCandidate = candidate;
        if (prior is not null)
        {
            var candidateProjection = await ProjectRetainedPairEndpointsAsync(candidate, cancellationToken,
                projectSidesLackingNamespaceEvenWithDocument: true);
            if (!candidateProjection.IsComplete)
                throw new SyncPairAuthoringReviewException(
                    "The selected pair's own retained cloud account and folder identity could not be verified. " +
                    "Nothing was saved; reconnect or refresh that exact account before retrying.");
            comparisonCandidate = candidateProjection.Comparison;
        }
        var decisions = new Dictionary<string, SyncPairPartitionRelation>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, string>? planned = null;
        foreach (var retained in before.Where(item => item.Id != candidate.Id && item.Id != replacingPairId))
        {
            // The retained pair's OWN already-verified namespace is projected into an operation-scoped
            // comparison copy BEFORE the shared decision, so a pair published before the explicit endpoint
            // document existed is compared with the namespace this product retains for that exact pair, its
            // own account and its own folder - never with the candidate's account or an assumed default. The
            // projection is never persisted and never substitutes for the retained definition: the
            // full-definition recheck below still binds the raw retained row.
            if (PartitionComparisonProbeForTest is { } probe)
                probe("candidate=" + candidate.Id + " replacing=" + (replacingPairId ?? "<null>") +
                    " retained=" + retained.Id + " decision=" + SyncPairPartitionPolicy.Decide(comparisonCandidate, retained));
            var projection = await ProjectRetainedPairEndpointsAsync(retained, cancellationToken);
            if (!projection.IsComplete)
                throw new SyncPairAuthoringReviewException(
                    "The retained cloud account and folder identity for pair “" + SyncActivityText.Safe(retained.Name) +
                    "” could not be verified. Nothing was saved; reconnect or refresh that exact account before retrying.");
            var relation = SyncPairPartitionPolicy.Decide(comparisonCandidate, projection.Comparison);
            SyncAuthorityOpaquePartitionProof? proof = null;
            if (relation == SyncPairPartitionRelation.Unverified)
            {
                planned ??= prior is null ? PlannedOpaqueRootIds(comparisonCandidate) : null;
                progress?.Invoke("Pair save: verifying the selected cloud folders' identities and ancestry against retained pair “" +
                    SyncActivityText.Safe(retained.Name) + "”. No files are being synced.");
                proof = await BuildOpaquePartitionProofAsync(comparisonCandidate, planned, prior, retained,
                    cancellationToken);
                relation = proof is null ? SyncPairPartitionRelation.Unverified :
                    SyncPairPartitionPolicy.DecideWithHierarchy(proof.Candidate, proof.CandidateProofs,
                        projection.Comparison, proof.RetainedProofs);
            }
            // A corresponding INCLUDED nesting is a distinct admitted relation, never a weakening of the
            // excluded partition: both endpoints must correspond with one equal relative translation and the
            // enclosing pair must include the whole relative subtree as ordinary data.
            if (relation is SyncPairPartitionRelation.NestedRefused or SyncPairPartitionRelation.Unverified)
            {
                var included = proof is null
                    ? SyncPairPartitionPolicy.DecideCorrespondingIncluded(comparisonCandidate, projection.Comparison)
                    : SyncPairPartitionPolicy.DecideCorrespondingIncluded(proof.Candidate,
                        projection.Comparison, proof.CandidateProofs, proof.RetainedProofs);
                if (included == SyncPairPartitionRelation.CorrespondingIncluded)
                    relation = SyncPairPartitionRelation.CorrespondingIncluded;
            }
            // The retained facts the comparison was derived from are re-held across the awaited reads above:
            // a row that changed while the relation was being decided is refused rather than compared.
            if (relation is SyncPairPartitionRelation.Distinct or SyncPairPartitionRelation.NestedAllowed or
                    SyncPairPartitionRelation.CorrespondingIncluded &&
                !await projection.StillHeldAsync(_authority, retained.Id, cancellationToken))
                relation = SyncPairPartitionRelation.Unverified;
            if (relation is not (SyncPairPartitionRelation.Distinct or SyncPairPartitionRelation.NestedAllowed or
                    SyncPairPartitionRelation.CorrespondingIncluded))
                throw new SyncPairAuthoringReviewException(relation switch
                {
                    SyncPairPartitionRelation.Same => "Another pair owns this exact folder. Choose a different folder.",
                    SyncPairPartitionRelation.NestedRefused =>
                        "These pairs overlap. The enclosing pair must exclude the entire nested folder and its private storage, without included exceptions.",
                    _ => "The cloud folders' relationship could not be verified against retained pair “" +
                         SyncActivityText.Safe(retained.Name) + "”. Nothing was saved; reconnect or refresh the exact account and folder choices before retrying."
                });
            decisions.Add(retained.Id, relation);
        }
        if (PairDefinition(candidate) != definition ||
            !SameDefinitionSet(before, await _authority.LoadAllPairDefinitionsAsync(cancellationToken, includeRetired: false)))
            throw new SyncPairAuthoringReviewException(
                "The pair definitions changed while folder ownership was being checked. Reload before saving; nothing was applied.");
        return decisions;
    }

    private IReadOnlyDictionary<string, string> PlannedOpaqueRootIds(SyncPair pair)
    {
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(pair)
            ?? throw new InvalidDataException("The candidate has no complete endpoint identities.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (endpoint, side) in new[]
                 { (endpoints.Left, SyncPairSide.Left), (endpoints.Right, SyncPairSide.Right) })
        {
            if (endpoint.IsLocal || SyncEndpointProviders.Get(endpoint.Kind).PathsDescribeAncestry) continue;
            var record = SyncEndpointRecord.From(endpoint);
            if (record.NamespaceId is null && pair.EndpointDocument is null)
            {
                var facts = ResolveCloudAccount(endpoint.Kind, endpoint.ConnectionId);
                record = record with { NamespaceId = SyncAuthorityProviderAccountIdentity.RetainedEndpointNamespace(
                    facts.Provider, facts.AccountId, facts.RootNamespaceId, facts.HomeNamespaceId) };
            }
            var resolved = ResolveCloudSide(record);
            var id = SyncCloudPairConfiguration.IsCloudOnly(pair) ? CloudAuthoringRootId(pair.Id, resolved) :
                Id("root-dropbox", pair.Id, resolved.Facts.RootNamespaceId, endpoint.FolderId!);
            result.Add(SyncEndpointContext.CompatibilityPort(endpoints.Left.Kind, endpoints.Right.Kind, side), id);
        }
        return result;
    }

    // Authoring composes the same endpoint adapters used by execution. No provider-pair dispatch,
    // fabricated local root or second engine is introduced. Only publication activates the roots.
    [SupportedOSPlatform("windows")]
    private async Task<SyncPairBindingResult> BindCloudEndpointPairAsync(SyncPair draft,
        string? replacingPairId, long? expectedPolicyRevision, CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        var endpoints = RequireCloudOnlyProjection(draft);
        var installationId = await EnsureAuthorityInitializedAsync(cancellationToken, progress);
        var retained = await LoadPairsAsync(cancellationToken);
        var prior = replacingPairId is null ? null : retained.Pairs.SingleOrDefault(p => p.Id == replacingPairId);
        if (replacingPairId is not null && prior is null)
            throw new InvalidDataException("The folder pair selected for replacement is no longer retained.");
        if (prior is not null && (expectedPolicyRevision is null || prior.PolicyRevision != expectedPolicyRevision))
            throw new SyncPairAuthoringReviewException("The pair changed after it was selected. Reload it before saving.");
        if (retained.Pairs.Any(p => p.Id != replacingPairId &&
            (p.Id == draft.Id || string.Equals(p.Name, draft.Name, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Another pair already uses this identity or name.");
        if (prior?.FilesOnDemandEnabled == true)
            throw new InvalidOperationException("Disable Files On Demand on the existing pair before changing its endpoint type.");

        // Structural checks are an early refusal, never a replacement for provider-issued hierarchy
        // and the complete retained-authority check inside the publication transaction.
        // The policy-aware pair-set rule carries the candidate's and the retained pairs' own persisted
        // policy, so a strict nested cloud relation is admitted only when the enclosing pair excludes the
        // whole nested subtree. Legacy endpoint-only callers keep the strict overload.
        var validation = SyncCloudPairConfiguration.ValidateAgainstRetainedPairs(draft,
            retained.Pairs.Where(p => p.Id != replacingPairId));
        if (!validation.IsValid) throw new SyncEndpointRuntimeRefusedException(validation);
        await RequireVerifiedPairPartitionsAsync(draft, replacingPairId, cancellationToken, progress);

        if (prior is not null && prior.Id == draft.Id)
        {
            if (draft.Enabled && prior.InitialReviewApproved != true)
                throw new SyncPairAuthoringReviewException("Review the first-sync plan before enabling this cloud pair.");
            // A policy-only edit must not prepare replacement roots, create staging or reset approval.
            // Verify the exact retained pair through read-only runtime preparation first. The draft
            // contains the requested policy changes, so it must never be passed to that unchanged-pair
            // route. Publication below still requires the configuration-editing guard and retained
            // revision; no changed policy is accepted by weakening read-only preparation.
            var verified = draft with
            {
                PolicyRevision = prior.PolicyRevision,
                InitialReviewApproved = prior.InitialReviewApproved,
                ControlNamespace = prior.ControlNamespace,
                RightControlNamespace = prior.RightControlNamespace,
                RecoveryRoot = prior.RecoveryRoot
            };
            progress?.Invoke("Pair save: verifying both retained cloud endpoint bindings and their owned staging and recovery through the provider adapters.");
            await PrepareCloudPairAsync(prior, cancellationToken);
            await EnsurePairConfigurationMutableAsync(prior.Id, cancellationToken);
            var current = (await LoadPairsAsync(cancellationToken)).Pairs.SingleOrDefault(p => p.Id == prior.Id);
            if (current?.PolicyRevision != expectedPolicyRevision)
                throw new SyncPairAuthoringReviewException("The pair changed while its endpoints were checked. Reload it before saving.");
            var updated = verified with
            {
                PolicyRevision = SamePolicy(prior, verified) ? prior.PolicyRevision : checked(prior.PolicyRevision + 1)
            };
            progress?.Invoke("Pair save: registering and committing the verified cloud pair policy in the retained database.");
            var cloudPolicy = await RegisterPairPolicyAsync(updated, cancellationToken, select: true);
            var pairSet = cloudPolicy.DefinitionCommitted
                ? cloudPolicy.CommittedPairSet!
                : await _authority.RetainPairDefinitionAsync(updated, select: true, cancellationToken);
            _selectedPairId = updated.Id;
            progress?.Invoke("Pair save: committed cloud pair policy successfully; no sync was started by this save.");
            return new SyncPairBindingResult(pairSet, updated, null, IdentityChanged: false);
        }

        // New identities always require review; GUI/CLI enablement cannot publish an unreviewed
        // replacement already running. The old pair remains unchanged until the final transaction.
        var candidate = draft with { Enabled = false, InitialReviewApproved = false, PolicyRevision = 1 };
        var left = ResolveCloudSide(SyncEndpointRecord.From(endpoints.Left));
        var right = ResolveCloudSide(SyncEndpointRecord.From(endpoints.Right));
        var leftId = CloudAuthoringRootId(candidate.Id, left);
        var rightId = CloudAuthoringRootId(candidate.Id, right);
        progress?.Invoke("Pair save: asking the left cloud provider for the selected folder identity and hierarchy.");
        var leftHierarchy = await ReadCandidateHierarchyAsync(left, leftId, cancellationToken);
        progress?.Invoke("Pair save: asking the right cloud provider for the selected folder identity and hierarchy.");
        var rightHierarchy = await ReadCandidateHierarchyAsync(right, rightId, cancellationToken);
        await RegisterCloudAuthoringAccountAsync(left.Facts, installationId, cancellationToken);
        await RegisterCloudAuthoringAccountAsync(right.Facts, installationId, cancellationToken);
        var preparation = new SyncAuthorityDatabase.SyncAuthorityCloudPairCandidate(candidate, candidate.PolicyRevision,
            installationId, leftId, left.Facts.Provider, left.Facts.ProviderAccountId, left.Record.NamespaceId!,
            left.Record.FolderId!, SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(left.Record.FolderPath),
            rightId, right.Facts.Provider, right.Facts.ProviderAccountId, right.Record.NamespaceId!,
            right.Record.FolderId!, SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(right.Record.FolderPath));
        progress?.Invoke("Pair save: registering the unpublished two-endpoint candidate in the retained database.");
        var prepared = await _authority.PrepareCloudPairCandidateAsync(preparation, cancellationToken);
        progress?.Invoke("Pair save: verifying or preparing the left endpoint's owned Recovery binding.");
        await ResolveCloudSideRecoveryAsync(left, candidate.Id, SyncAuthorityCloudSides.LogicalLeft,
            leftId, leftHierarchy, installationId, cancellationToken);
        progress?.Invoke("Pair save: verifying or preparing the right endpoint's owned Recovery binding.");
        await ResolveCloudSideRecoveryAsync(right, candidate.Id, SyncAuthorityCloudSides.LogicalRight,
            rightId, rightHierarchy, installationId, cancellationToken);

        // This narrowly scoped allocation uses the exact unpublished candidate returned by the DB.
        // Ordinary runtime reopening keeps its active-root requirement unchanged.
        using (var anchor = PilotDirectoryLease.Open(stateAuthority.StateRoot))
        {
            anchor.VerifyBinding();
            // RC639-5: when this exact candidate already carries an authenticated retained-control
            // recovery, reopen that owned namespace and skip the allocator entirely rather than colliding
            // with the object this installation already owns. A candidate with no recovered ownership keeps
            // the unchanged allocation path.
            if (!await TryReopenRecoveredCandidateControlAsync(prepared, anchor, cancellationToken))
            {
                var allocator = new SyncAuthorityLocalControlNamespaceAllocator(_authority);
                await allocator.BindAsync(anchor, prepared.InstallationId, prepared.PairId,
                    prepared.LeftRootBindingId, prepared.PolicyRevision,
                    Id("profile", BuildIdentity.SourceRevision, CapabilityJson), cancellationToken,
                    side: SyncAuthorityCloudSides.LogicalLeft);
            }
        }

        // Preparation uses inert defaults. Retain the actual requested policy through the same
        // shared policy boundary as every later edit, while this candidate is still unpublished.
        await RegisterPairPolicyAsync(candidate, cancellationToken, replacingPairId);

        // Refresh AFTER all preparation. Both supplied lists contain both candidates and all current
        // retained authorities of their provider. A new unrepresented authority is refused by the DB.
        progress?.Invoke("Pair save: refreshing both provider hierarchies and checking retained authorities before publication.");
        leftHierarchy = await ReadCandidateHierarchyAsync(left, leftId, cancellationToken);
        rightHierarchy = await ReadCandidateHierarchyAsync(right, rightId, cancellationToken);
        var chains = new List<SyncAuthorityCloudHierarchySnapshot> { leftHierarchy, rightHierarchy };
        foreach (var authority in await _authority.LoadRetainedCloudAuthoritiesAsync(cancellationToken))
        {
            if (!string.Equals(authority.Provider, left.Facts.Provider, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(authority.Provider, right.Facts.Provider, StringComparison.OrdinalIgnoreCase)) continue;
            var retainedChain = await ReadRetainedAuthorityHierarchyAsync(authority, cancellationToken);
            chains.Add(retainedChain);
            // D2: the just-verified chain of that exact active pair root becomes its retained observation, so a
            // later readback or restart has a bound context without a fresh provider read.
            await _authority.RetainVerifiedPairRootObservationAsync(authority, retainedChain, cancellationToken);
        }
        progress?.Invoke("Pair save: publishing the verified two-endpoint candidate atomically in the retained database.");
        var published = await _authority.PublishCloudPairCandidateAsync(candidate.Id, candidate,
            prior?.Id, prior?.PolicyRevision, installationId, chains, chains, cancellationToken);
        // D2: the candidate's own two just-verified chains are retained against the roots publication activated.
        await _authority.RetainVerifiedPairRootObservationAsync(candidate.Id,
            SyncAuthorityCloudSides.LogicalLeft, leftHierarchy, cancellationToken);
        await _authority.RetainVerifiedPairRootObservationAsync(candidate.Id,
            SyncAuthorityCloudSides.LogicalRight, rightHierarchy, cancellationToken);
        _selectedPairId = candidate.Id;
        progress?.Invoke("Pair save: verified cloud endpoint candidate published successfully; first-sync review is still required.");
        return new SyncPairBindingResult(published, candidate, null, IdentityChanged: prior is not null);
    }

    /// <summary>
    /// RC639-5: reopen the exact authenticated retained-control recovery of this unpublished candidate when
    /// one exists, so the normal guarded authoring path continues against the object this installation
    /// already owns. It creates nothing and returns false when the candidate has no recovered ownership, in
    /// which case the unchanged allocator path runs.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task<bool> TryReopenRecoveredCandidateControlAsync(
        SyncAuthorityDatabase.SyncAuthorityCloudPairCandidateState prepared, PilotDirectoryLease anchor,
        CancellationToken cancellationToken)
    {
        var generation = await _authority.LoadRootBindingGenerationAsync(prepared.PairId,
            prepared.LeftRootBindingId, cancellationToken);
        if (generation < 1) return false;
        if (!await _authority.HasRecoveredControlOwnershipAsync(prepared.PairId,
                SyncAuthorityCloudSides.LogicalLeft, prepared.LeftRootBindingId, generation, cancellationToken))
            return false;
        using var reopened = await _authority.ReopenRetainedControlAsync(prepared.PairId,
            SyncAuthorityCloudSides.LogicalLeft, prepared.LeftRootBindingId, generation, anchor,
            cancellationToken);
        reopened.VerifyBinding();
        return true;
    }

    /// <summary>
    /// RC639-5: the shared retained-control recovery authoring entry. It installs ONLY the recovered
    /// ownership metadata for the current draft (an idempotent, resumable safe boundary), imports no old
    /// database or policy, and then invokes the SAME guarded authoring path used by every other save so that
    /// publication stays on the unchanged boundary. The draft is authored with its current disabled,
    /// fresh-review policy; a replay completes only the missing publication.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task<SyncPairBindingResult> RecoverRetainedControlAndAuthorAsync(SyncPair draft,
        SyncAuthorityDatabase.RetainedControlSource source, PilotDirectoryLease selectedRoot,
        string? replacingPairId = null, long? expectedPolicyRevision = null,
        CancellationToken cancellationToken = default, Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(selectedRoot);
        // the SHARED recovery entry holds the EXISTING configuration-mutation gate across its whole
        // validation/retention/publication flow, and delegates to the gate-free normal binding core so the gate is
        // never re-entered. Every normal caller path still acquires the gate exactly once.
        using var mutation = await EnterPairConfigurationMutationAsync(cancellationToken);
        // the SHARED app action binds the supplied source to the normalized current draft and the
        // held recovery target BEFORE RetainRecoveredControlAsync can commit anything. A wrong source pair, side
        // or root, an enabled/approved draft, or a wrong held physical identity refuses with NO recovery metadata
        // written, rather than failing later during ordinary publication.
        var normalized = SyncPairStore.NormalizePair(draft);
        RequireSupportedRecoveryFamily(normalized);
        if (normalized.Enabled || normalized.InitialReviewApproved == true)
            throw new InvalidDataException(
                "Retained-control recovery authors the CURRENT disabled, fresh-review draft only; an enabled or " +
                "approved draft is refused before any recovery metadata is written.");
        var target = await ResolveRetainedControlRecoveryTargetAsync(normalized, selectedRoot, cancellationToken);
        if (!string.Equals(source.PairId, normalized.Id, StringComparison.Ordinal) ||
            !string.Equals(source.Side, target.Side, StringComparison.Ordinal) ||
            !string.Equals(source.RootBindingId, target.RootBindingId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The retained-control source does not match the normalized current draft and the held recovery " +
                "target; a wrong pair, side or root is refused before any recovery metadata is written.");
        // the legitimate CURRENT draft identity and BOTH own endpoint/account/namespace/folder
        // identities are validated BEFORE retention, reusing the existing normal endpoint derivation and the
        // retained connection facts. A consistent draft carrying a changed cloud folder, namespace or connection is
        // refused here rather than committing recovered metadata that normal save would reject afterwards.
        RequireRecoveryDraftCurrent(normalized);
        // an INITIAL publication validates the current unpublished candidate revision here, at the
        // recovery boundary, and then hands NORMAL initial authoring NO published-edit expected revision. A
        // published repair keeps its exact replacement identity and revision CAS unchanged.
        // An INITIAL publication binds the expected current unpublished revision INSIDE the retention transaction
        // below and hands NORMAL initial authoring no published-edit expected revision. A published repair keeps
        // its exact replacement identity and revision CAS.
        // bind the exact current mode, identity and revision BEFORE retention, with no null
        // bypass, and pass that expectation into the retention transaction which revalidates it.
        var current = await _authority.LoadRetainedPairStateAsync(normalized.Id, cancellationToken)
            ?? throw new InvalidOperationException(
                "No current retained pair row exists for this recovery; it is refused before any metadata is written.");
        long expectationRevision;
        if (replacingPairId is null)
        {
            expectationRevision = expectedPolicyRevision ?? normalized.PolicyRevision;
            if (current.Published || current.Revision != expectationRevision)
                throw new InvalidOperationException(
                    "The current unpublished candidate does not match the exact draft revision; reload it before recovering.");
            expectedPolicyRevision = null;
        }
        else
        {
            // the published branch must name THIS exact pair as its replacement identity before
            // any recovered metadata is written.
            if (!string.Equals(replacingPairId, normalized.Id, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "A published repair must name this exact pair as its replacement identity; the recovery " +
                    "refuses before any metadata is written.");
            expectationRevision = expectedPolicyRevision
                ?? throw new InvalidOperationException(
                    "A published repair requires its exact expected policy revision.");
            if (!current.Published || current.Revision != expectationRevision)
                throw new InvalidOperationException(
                    "The current published pair does not match the exact replacement revision; reload it before recovering.");
        }
        var expectation = new SyncAuthorityDatabase.RetainedControlRecoveryExpectation(normalized.Id,
            expectationRevision, MustBeUnpublished: replacingPairId is null);
        progress?.Invoke(
            "Pair recovery: installing the retained original-control ownership metadata for the current draft.");
        var outcome = await _authority.RetainRecoveredControlAsync(source, selectedRoot, expectation,
            cancellationToken);
        progress?.Invoke(outcome.Replayed
            ? "Pair recovery: the retained ownership metadata was already installed; completing only the " +
              "missing publication."
            : "Pair recovery: retained ownership metadata installed from the pinned original database; no " +
              "pair policy was imported.");
        return await BindEndpointPairCoreAsync(normalized, replacingPairId, expectedPolicyRevision, cancellationToken,
            recoveryFolder: null, progress: progress,
            recoveredExpectedUnpublishedRevision: replacingPairId is null ? expectationRevision : null);
    }

    /// <summary>
    /// LC653-1: the shared recovery entry supports every family whose normal authoring derives exactly one
    /// affected root identity: the existing two-endpoint cloud pair and the actual local/cloud pair in either
    /// placement. A pair with no cloud endpoint is not a supported recovery family and refuses with this
    /// precise message rather than being silently coerced into a cloud projection.
    /// </summary>
    private static void RequireSupportedRecoveryFamily(SyncPair draft)
    {
        if (SyncCloudPairConfiguration.IsCloudOnly(draft)) return;
        if (IsLocalCloudPair(draft)) return;
        throw new NotSupportedException(
            "Retained-control recovery authoring supports the two-endpoint cloud pair family and the " +
            "local/cloud pair family in either placement; a pair with no cloud endpoint is refused.");
    }

    private static bool IsLocalCloudPair(SyncPair pair)
    {
        var document = pair.EndpointDocument;
        return document?.Left is not null && document.Right is not null &&
               document.Left.IsLocal != document.Right.IsLocal;
    }

    /// <summary>
    /// LC653-2/3: the ONE family-aware recovery target, derived exactly as the normal authoring path derives
    /// the affected root identity and bound to the HELD selected-root lease. A local/cloud pair targets its
    /// LOCAL root (the actual normal-authoring derivation over the resolved volume id and the held file id) on
    /// the pair's own local compatibility port; a cloud/cloud pair keeps its existing cloud logical-left
    /// target. A local endpoint is never forced into a cloud projection and no provider/pair matrix is created.
    /// The draft's local path, volume and file identity are verified against the held lease BEFORE any
    /// retention; the database still revalidates the exact pair, side, root, generation and original ownership
    /// proof inside the retention transaction.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal async Task<(string Side, string RootBindingId)> ResolveRetainedControlRecoveryTargetAsync(
        SyncPair draft, PilotDirectoryLease selectedRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedRoot);
        RequireSupportedRecoveryFamily(draft);
        if (IsLocalCloudPair(draft))
        {
            if (!string.Equals(PilotPath.NormalizeLocalRoot(draft.LocalRoot),
                    PilotPath.NormalizeLocalRoot(selectedRoot.Path), StringComparison.OrdinalIgnoreCase) ||
                selectedRoot.Identity.VolumeSerialNumber != draft.LocalVolumeSerialNumber ||
                selectedRoot.Identity.FileId != draft.LocalRootFileId)
                throw new InvalidDataException(
                    "The held local root does not match the recovery draft's exact local path, volume and file " +
                    "identity; the recovery refuses before retaining any metadata.");
            var endpoints = SyncCloudPairConfiguration.SavedEndpoints(draft)
                ?? throw new InvalidDataException("The recovery draft has no complete endpoint identities.");
            var side = LocalCompatibilitySide(endpoints)
                ?? throw new InvalidDataException(
                    "The recovery draft names no local endpoint on its pair's compatibility ports.");
            var installationId = await EnsureAuthorityInitializedAsync(cancellationToken);
            var volumeId = await _authority.ResolveVolumeIdAsync(installationId,
                selectedRoot.Identity.VolumeSerialNumber.ToString("x8", System.Globalization.CultureInfo.InvariantCulture), "NTFS", null,
                supportsFileId128: false,
                supportsUsn: NtfsUsnJournal.TryReadCheckpoint(selectedRoot.Path, out _),
                supportsAtomicRename: true, cancellationToken);
            var fileIdHex = selectedRoot.Identity.FileId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
            return (side, LocalRecoveryRootId(draft.Id, volumeId, fileIdHex));
        }
        var cloudEndpoints = RequireCloudOnlyProjection(draft);
        var left = ResolveCloudSide(SyncEndpointRecord.From(cloudEndpoints.Left));
        return (SyncAuthorityCloudSides.LogicalLeft, CloudAuthoringRootId(draft.Id, left));
    }

    private static string? LocalCompatibilitySide(SyncPairEndpoints endpoints)
    {
        foreach (var (endpoint, side) in new[]
                 {
                     (endpoints.Left, SyncPairSide.Left), (endpoints.Right, SyncPairSide.Right)
                 })
            if (endpoint.IsLocal)
                return SyncEndpointContext.CompatibilityPort(endpoints.Left.Kind, endpoints.Right.Kind, side);
        return null;
    }

    /// <summary>
    /// the read-only pre-retention validation of the CURRENT draft. It reuses the existing normal
    /// endpoint derivation (the engine layout id for a local/cloud pair, the cloud-pair id for a cloud/cloud pair)
    /// to require that the carried pair identity is exactly the identity of its own ordered endpoints, and reuses
    /// the retained connection facts to require each own cloud endpoint's provider, connection and namespace to
    /// agree. Nothing is written and no new machinery is introduced.
    /// </summary>
    private void RequireRecoveryDraftCurrent(SyncPair draft)
    {
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(draft)
            ?? throw new InvalidDataException("The recovery draft has no complete endpoint identities.");
        if (IsLocalCloudPair(draft))
        {
            var localEndpoint = endpoints.Left.IsLocal ? endpoints.Left : endpoints.Right;
            var cloudEndpoint = endpoints.Left.IsLocal ? endpoints.Right : endpoints.Left;
            var settings = new PilotSettings(localEndpoint.LocalPath!, draft.LocalVolumeSerialNumber,
                draft.LocalRootFileId, cloudEndpoint.FolderPath ?? string.Empty,
                cloudEndpoint.FolderId ?? string.Empty);
            // R675: validate the exact retained connection, provider and endpoint namespace FIRST, then derive
            // the identity with the SAME salt normal authoring uses for this provider (Google: the connection
            // account key; Dropbox: the root namespace, which is also its own endpoint namespace).
            var connection = RequireRecoveryCloudEndpoint(cloudEndpoint);
            if (!string.Equals(draft.ConnectionId, connection.ConnectionId, StringComparison.Ordinal) ||
                !string.Equals(draft.Provider, connection.Provider, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The recovery draft's own provider or connection does not match its cloud endpoint and the " +
                    "retained provider; the recovery refuses before any metadata is written.");
            var derived = SyncEndpointEngineLayout.DerivePairId(settings,
                PairIdentitySalt(connection.Provider, connection), reversed: !endpoints.Left.IsLocal);
            if (!string.Equals(derived, draft.Id, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "The recovery draft's pair identity is not the identity derived from its own ordered endpoints; " +
                    "a changed or fabricated draft identity is refused before any recovery metadata is written.");
            return;
        }
        if (!string.Equals(SyncCloudPairConfiguration.DeriveId(endpoints), draft.Id, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The recovery draft's pair identity is not the identity derived from its own ordered endpoints; " +
                "a changed or fabricated draft identity is refused before any recovery metadata is written.");
        RequireRecoveryCloudEndpoint(endpoints.Left);
        RequireRecoveryCloudEndpoint(endpoints.Right);
    }

    /// <summary>
    /// R675: validates one own cloud endpoint against its exact retained connection and RETURNS that connection,
    /// so the caller can derive the pair identity with the same salt normal authoring uses. A missing or unknown
    /// connection, a provider mismatch and any endpoint-namespace disagreement still refuse here, before any
    /// recovery metadata is written.
    /// </summary>
    private ProviderConnectionRecord RequireRecoveryCloudEndpoint(SyncEndpoint endpoint)
    {
        if (endpoint.IsLocal)
            throw new InvalidDataException("A local endpoint carries no retained cloud connection.");
        var record = SyncEndpointRecord.From(endpoint);
        var connection = LoadProviderConnections().SingleOrDefault(account =>
            string.Equals(account.ConnectionId, record.ConnectionId, StringComparison.Ordinal));
        if (connection is null ||
            !string.Equals(connection.Provider, record.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(connection.RootNamespaceId, record.NamespaceId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The recovery draft's own cloud endpoint account or storage namespace does not match the retained " +
                "connection; the recovery refuses before any metadata is written.");
        return connection;
    }

    /// <summary>
    /// R675: the pair-id salt the NORMAL authoring path uses for this provider over this exact retained
    /// connection. Google derives its pair id from the connection ACCOUNT KEY while its endpoint namespace stays
    /// the PermissionId; Dropbox derives it from the root namespace, which is also its endpoint namespace. This
    /// is a read-only mapping over already-validated retained facts; it changes no identity, store, endpoint
    /// namespace serialization, provider-account row, root identity or cloud/cloud derivation.
    /// </summary>
    private static string PairIdentitySalt(string provider, ProviderConnectionRecord connection) =>
        string.Equals(provider, SyncProviders.GoogleDrive, StringComparison.OrdinalIgnoreCase)
            ? connection.AccountId
            : connection.RootNamespaceId;

    /// <summary>
    /// LC653-2/3: the ONE local-root identity derivation shared by the recovery resolver and the normal
    /// authoring path, exposed so the local/cloud recovery target and the retained local root row can never
    /// disagree. It creates nothing and reads no authority.
    /// </summary>
    internal static string LocalRecoveryRootId(string pairId, string volumeId, string fileIdHex) =>
        Id("root-local", pairId, volumeId, fileIdHex);

    private static string CloudAuthoringRootId(string pairId, CloudSideResolution side) =>
        Id("root-dropbox", pairId, side.Facts.Provider, side.Facts.ProviderAccountId,
            side.Record.NamespaceId!, side.Record.FolderId!);

    /// <summary>LC653-4: the current retained revision for this pair id (published or unpublished candidate), or 0.</summary>
    internal Task<long> LoadRetainedPairRevisionAsync(string pairId, CancellationToken cancellationToken = default) =>
        _authority.LoadRetainedPairRevisionAsync(pairId, cancellationToken);

    private Task RegisterCloudAuthoringAccountAsync(CloudAccountFacts facts, string installationId,
        CancellationToken cancellationToken) =>
        _authority.RegisterProviderAccountAsync(facts.ProviderAccountId, installationId, facts.AccountId,
            facts.RootNamespaceId, facts.HomeNamespaceId, facts.Provider, 1, cancellationToken, facts.Provider);
}
