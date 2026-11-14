namespace Massdex.SyncShark;

// CC2F: the one revision-bound common-content digest resolver for the typed cloud endpoint ports.
//
// The resolver answers exactly one question for the shared CC2A comparison - what is the whole-file SHA-256
// of the bytes retained for one exact cloud object revision - and nothing else. It decides no equality,
// adopts no baseline, plans no operation and performs no effect. Its whole authority is the retained
// authority database, the two typed ports of one explicit cloud/cloud pair and the request side the shared
// comparison supplied; there is no bare provider string, no client selection and no per-provider branch.
//
// Request.Side arrives as the literal "left"/"right" and is mapped centrally to the matching logical
// compatibility port of that pair ("local"/"dropbox" in the stored rows). The provider is never consulted to
// infer a side, and a port whose retained compatibility tag disagrees fails closed.
//
// Byte retrieval uses the exact range capability with a writable, deliberately non-seekable hashing sink. A
// full DownloadAsync destination must be a real seekable file, so a hashing sink is never handed to one; that
// distinction is the point of this seam. Transport metadata alone never proves a body: the sink independently
// computes both the common whole-file SHA-256 and the provider-native digest over exactly the streamed bytes,
// and a result is returned only when the byte count, the native digest, the returned range receipt and the
// re-read retained source all still agree.

/// <summary>
/// The one revision-bound common digest resolver over the two typed cloud endpoint ports of one pair.
/// </summary>
public sealed class SyncAuthorityCloudCommonDigestResolver : ISyncAuthorityCommonDigestResolver
{
    /// <summary>The literal CC2A request side of the pair's logical left endpoint.</summary>
    public const string LeftSide = "left";

    /// <summary>The literal CC2A request side of the pair's logical right endpoint.</summary>
    public const string RightSide = "right";

    private readonly SyncAuthorityDatabase _authority;
    private readonly SyncAuthorityCloudEndpointSet _endpoints;

    public SyncAuthorityCloudCommonDigestResolver(SyncAuthorityDatabase authority,
        SyncAuthorityCloudEndpointSet endpoints)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(endpoints);
        _authority = authority;
        _endpoints = endpoints;
    }

    /// <summary>The exact cloud/cloud pair whose two retained roots this resolver may read.</summary>
    public string PairId => _endpoints.PairId;

    public async Task<SyncAuthorityCommonDigest?> ResolveAsync(SyncAuthorityCommonDigestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var port = PortFor(request.Side);
        if (port is null) return null;
        try
        {
            return await ResolveCoreAsync(port, request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is never a refusal. It reaches the caller and can never be cached as a success.
            throw;
        }
        catch (Exception error) when (IsSafeRefusal(error))
        {
            // A read-only provider mismatch, an unavailable or unsupported content/range capability, an
            // unretained or drifted identity and a malformed retained identity are all normal unknowns.
            return null;
        }
    }

    /// <summary>
    /// The retained compatibility port for one literal CC2A side, or null for any other value. The mapping
    /// is positional to this pair's explicit left/right endpoint document and is cross-checked against the
    /// port's retained compatibility tag, so nothing here is inferred from a provider.
    /// </summary>
    private SyncAuthorityCloudEndpointPort? PortFor(string? side)
    {
        var expectedTag = side switch
        {
            LeftSide => SyncAuthorityCloudSides.LogicalLeft,
            RightSide => SyncAuthorityCloudSides.LogicalRight,
            _ => null
        };
        if (expectedTag is null) return null;
        var port = string.Equals(expectedTag, SyncAuthorityCloudSides.LogicalLeft, StringComparison.Ordinal)
            ? _endpoints.Left
            : _endpoints.Right;
        return string.Equals(port.CompatibilitySide, expectedTag, StringComparison.Ordinal) ? port : null;
    }

    private async Task<SyncAuthorityCommonDigest?> ResolveCoreAsync(SyncAuthorityCloudEndpointPort port,
        SyncAuthorityCommonDigestRequest request, CancellationToken cancellationToken)
    {
        // The exact request shape. Every value here is one the shared comparison already retained; a
        // missing, malformed or substituted value is unknown rather than an inference.
        if (!string.Equals(request.ObjectKind, "file", StringComparison.Ordinal)) return null;
        if (request.Size < 0) return null;
        if (string.IsNullOrWhiteSpace(request.ObjectVersionId)) return null;
        if (string.IsNullOrWhiteSpace(request.RootBindingId) ||
            !string.Equals(request.RootBindingId, port.RootBindingId, StringComparison.Ordinal)) return null;
        if (!string.Equals(request.NativeAlgorithm, port.Definition.ContentHashAlgorithm, StringComparison.Ordinal))
            return null;
        if (!SyncContentIdentity.SupportsIncrementalHashing(request.NativeAlgorithm)) return null;
        if (!SyncContentIdentity.IdentityMatchesAlgorithm(request.NativeAlgorithm, request.NativeHash)) return null;
        string path;
        try
        {
            path = PilotPath.NormalizeRelative(request.RelativePath);
        }
        catch (ArgumentException)
        {
            return null;
        }
        // Only the exact already-normalized retained path is accepted, so a display path or a re-normalized
        // path can never stand in for the membership the request names.
        if (!string.Equals(path, request.RelativePath, StringComparison.Ordinal)) return null;

        var before = await RetainedSourceAsync(port, path, request, cancellationToken).ConfigureAwait(false);
        if (before is null) return null;

        if (request.Size == 0)
        {
            // Zero bytes need no range call at all: a zero-length range is invalid, so the correct empty
            // digests are computed by the same seam and the retained source is still re-read before and
            // after without inventing a provider read.
            var empty = SyncContentIdentity.EmptyDigest(request.NativeAlgorithm);
            var afterEmpty = await RetainedSourceAsync(port, path, request, cancellationToken)
                .ConfigureAwait(false);
            if (afterEmpty is null || !SameRetainedSource(afterEmpty, before)) return null;
            return new SyncAuthorityCommonDigest(port.RootBindingId, request.ObjectVersionId, 0,
                request.NativeAlgorithm, request.NativeHash, empty, 0);
        }

        // The range/content capability is required. An adapter without it yields no verified digest and is
        // never substituted by an unrelated algorithm, a whole-file in-memory buffer or a full download.
        if (port.Store is not ISyncRemoteRangeStore ranges) return null;
        using var sink = new SyncContentHashingSink(request.NativeAlgorithm, request.Size);
        var receipt = await ranges.DownloadRangeAsync(before.Expected, 0, request.Size, sink, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null) return null;
        // The returned range receipt must describe exactly the retained source and the requested range. The
        // adapter's own before/after provider root and current-metadata verification is part of that call.
        if (!string.Equals(receipt.FileId, before.Expected.Id, StringComparison.Ordinal)) return null;
        if (!string.Equals(receipt.Revision, before.Expected.Revision, StringComparison.Ordinal)) return null;
        if (receipt.TotalSize != request.Size) return null;
        if (receipt.Offset != 0 || receipt.Length != request.Size) return null;
        if (!string.Equals(receipt.ContentHash, request.NativeHash, StringComparison.OrdinalIgnoreCase))
            return null;
        // Transport metadata never proves the body: the sink must have retained exactly the reviewed byte
        // count, and its own native digest over those bytes must equal the retained native digest.
        if (sink.ByteCount != request.Size) return null;
        var hashed = sink.Finish();
        if (hashed.ByteCount != request.Size) return null;
        if (!string.Equals(hashed.NativeDigest, request.NativeHash, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!SyncContentIdentity.IdentityMatchesAlgorithm(SyncContentIdentity.Sha256, hashed.CommonSha256))
            return null;
        // The retained source is re-read after the read, so a root, membership, revision, identity, digest or
        // size change can never be cached as a verified success.
        var after = await RetainedSourceAsync(port, path, request, cancellationToken).ConfigureAwait(false);
        if (after is null || !SameRetainedSource(after, before)) return null;
        return new SyncAuthorityCommonDigest(port.RootBindingId, request.ObjectVersionId, request.Size,
            request.NativeAlgorithm, request.NativeHash, hashed.CommonSha256, hashed.ByteCount);
    }

    /// <summary>
    /// One exact retained cloud source: the immutable object version, its explicit cloud identity and the
    /// membership that retained it. Re-reading it is what makes the before/after comparison decisive.
    /// </summary>
    private sealed record RetainedCloudSource(PilotRemoteEntry Expected, string ObjectVersionId,
        string IdentityJson, string RootBindingId, string MembershipId, string ScanGenerationId,
        string ContentHash, long Size);

    /// <summary>
    /// The complete retained observation membership for exactly this pair, retention side, root, path,
    /// object version, native digest and size, together with the immutable object version and identity the
    /// provider file id and revision come from. The existing latest-complete load is one query for the
    /// logical side; no per-file version enumeration happens here.
    /// </summary>
    private async Task<RetainedCloudSource?> RetainedSourceAsync(SyncAuthorityCloudEndpointPort port,
        string path, SyncAuthorityCommonDigestRequest request, CancellationToken cancellationToken)
    {
        // The exact path-scoped read, not the whole side: one normalized path costs at most two materialized
        // rows instead of the complete retained inventory of this side, in the same single-statement snapshot
        // of the same latest complete scan generation the whole-side load uses.
        var matches = await _authority
            .LoadLatestCompleteObservationAtPathAsync(_endpoints.PairId, port.CompatibilitySide, path,
                cancellationToken)
            .ConfigureAwait(false);
        // Two memberships for one path are not a complete single membership, so this is refused rather than
        // resolved to whichever row happened to be read first. The lookup reads two rows precisely so this
        // duplicate stays visible instead of being truncated away.
        if (matches.Count > 1) return null;
        var entry = matches.Count == 1 ? matches[0] : null;
        if (entry is null) return null;
        if (string.IsNullOrWhiteSpace(entry.MembershipId)) return null;
        if (!string.Equals(entry.RootBindingId, port.RootBindingId, StringComparison.Ordinal)) return null;
        if (!string.Equals(entry.ObjectVersionId, request.ObjectVersionId, StringComparison.Ordinal)) return null;
        if (!string.Equals(entry.ObjectKind, "file", StringComparison.Ordinal)) return null;
        if (entry.Size != request.Size) return null;
        if (string.IsNullOrWhiteSpace(entry.ContentHash) ||
            !string.Equals(entry.ContentHash, request.NativeHash, StringComparison.OrdinalIgnoreCase)) return null;

        var version = await _authority.LoadObjectVersionAsync(request.ObjectVersionId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(version.Side, port.CompatibilitySide, StringComparison.Ordinal)) return null;
        if (!string.Equals(version.ObjectId, entry.ObjectId, StringComparison.Ordinal)) return null;
        if (version.Size is null || version.Size.Value != request.Size) return null;
        if (string.IsNullOrWhiteSpace(version.ContentHash) ||
            !string.Equals(version.ContentHash, request.NativeHash, StringComparison.OrdinalIgnoreCase)) return null;
        if (!SyncAuthorityCloudObservationIdentity.TryParse(version.IdentityJson, out var identity) ||
            identity is null) return null;
        // The current provider, retained account, verified native namespace, stable root binding and store
        // must all still agree, so another account, namespace, root or version is never accepted.
        if (!string.Equals(identity.Provider, port.Provider, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(identity.ProviderAccountId, port.ProviderAccountId, StringComparison.Ordinal)) return null;
        if (!string.Equals(identity.NamespaceId, port.NamespaceId, StringComparison.Ordinal)) return null;
        if (!string.Equals(identity.ContentAlgorithm, request.NativeAlgorithm, StringComparison.Ordinal))
            return null;
        if (string.IsNullOrWhiteSpace(identity.FileId)) return null;
        // A nonempty object is read by its exact retained revision; without one there is nothing a range
        // read could be bound to.
        if (request.Size > 0 && string.IsNullOrWhiteSpace(identity.Revision)) return null;
        var expected = new PilotRemoteEntry(path, PilotEntryKind.File, identity.FileId, identity.Revision,
            request.Size, request.NativeHash);
        return new RetainedCloudSource(expected, version.ObjectVersionId, version.IdentityJson,
            port.RootBindingId, entry.MembershipId, entry.ScanGenerationId, entry.ContentHash, request.Size);
    }

    private static bool SameRetainedSource(RetainedCloudSource left, RetainedCloudSource right) =>
        string.Equals(left.ObjectVersionId, right.ObjectVersionId, StringComparison.Ordinal) &&
        string.Equals(left.IdentityJson, right.IdentityJson, StringComparison.Ordinal) &&
        string.Equals(left.RootBindingId, right.RootBindingId, StringComparison.Ordinal) &&
        string.Equals(left.MembershipId, right.MembershipId, StringComparison.Ordinal) &&
        string.Equals(left.ScanGenerationId, right.ScanGenerationId, StringComparison.Ordinal) &&
        left.Size == right.Size &&
        string.Equals(left.ContentHash, right.ContentHash, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Expected.Id, right.Expected.Id, StringComparison.Ordinal) &&
        string.Equals(left.Expected.Revision, right.Expected.Revision, StringComparison.Ordinal);

    /// <summary>
    /// The bounded safe-refusal classes. Cancellation is handled before this, and a programming fault - a null
    /// argument, an invalid operation or a disposed object - is deliberately not a refusal.
    /// </summary>
    private static bool IsSafeRefusal(Exception error) => error switch
    {
        ArgumentNullException => false,
        InvalidOperationException => false,
        IOException => true,
        HttpRequestException => true,
        System.Text.Json.JsonException => true,
        NotSupportedException => true,
        ArgumentException => true,
        _ => false
    };
}

