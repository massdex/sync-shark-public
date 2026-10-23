using System.Globalization;

namespace Massdex.SyncShark;

// Shared cross-side content comparison for the sync authority planners.
//
// Native content identities keep their provider algorithm: a Dropbox block hash is never relabelled or
// compared as if it were another provider's digest. The component answers one question - do these two
// retained observations carry the same content - and it answers it three ways: Equal, Different or
// Unknown. Unknown is never equality and never carries adoption, transfer or deletion authority.
//
// The provider-aware instance compares same-algorithm native identities directly and, only for same-sized
// byte files whose native algorithms are incomparable, obtains a revision-bound common digest through the
// optional resolver. Verified resolved digests are cached in-process against the exact
// side/root/version/size/algorithm/hash key, so a changed revision or a different identity can never reuse
// a stale digest; the cache is bounded to 1024 exact keys with no disk cache, schema or app-data root.
//
// The legacy instance is the clearly named single-algorithm compatibility policy used only by existing
// planner calls that omit the optional comparer argument. It preserves those callers' historical
// hash-equality semantics for their existing fixtures. It is not a cloud/cloud fallback and it is not
// cross-provider comparison.

/// <summary>The three explicit results of one cross-side content comparison.</summary>
public enum SyncContentComparisonKind
{
    /// <summary>Both sides provably retain the same content.</summary>
    Equal = 0,

    /// <summary>Both sides provably retain different content.</summary>
    Different = 1,

    /// <summary>Equality could not be proved. Unknown never authorises adoption, transfer or deletion.</summary>
    Unknown = 2
}

/// <summary>One comparison result with a safe, secret-free reason.</summary>
public sealed record SyncContentComparisonResult(SyncContentComparisonKind Kind, string Reason)
{
    public static SyncContentComparisonResult Equal(string reason) =>
        new(SyncContentComparisonKind.Equal, reason);

    public static SyncContentComparisonResult Different(string reason) =>
        new(SyncContentComparisonKind.Different, reason);

    public static SyncContentComparisonResult Unknown(string reason) =>
        new(SyncContentComparisonKind.Unknown, reason);

    public bool IsEqual => Kind == SyncContentComparisonKind.Equal;

    public bool IsDifferent => Kind == SyncContentComparisonKind.Different;

    public bool IsUnknown => Kind == SyncContentComparisonKind.Unknown;
}

/// <summary>
/// The exact retained identity a common-digest resolver is asked to bind its result to. The request
/// carries no credential and no provider client.
/// </summary>
public sealed record SyncAuthorityCommonDigestRequest(
    string Side,
    string RelativePath,
    string RootBindingId,
    string ObjectVersionId,
    string ObjectKind,
    long Size,
    string NativeAlgorithm,
    string NativeHash);

/// <summary>
/// A revision-bound common digest. The resolver must return it for exactly the requested root binding,
/// object version, size, native algorithm and native hash, and the digest must cover exactly the requested
/// byte count; anything else is refused and treated as unknown.
/// </summary>
public sealed record SyncAuthorityCommonDigest(
    string RootBindingId,
    string ObjectVersionId,
    long Size,
    string NativeAlgorithm,
    string NativeHash,
    string Sha256,
    long ByteCount);

/// <summary>
/// Optional revision-bound resolver for a common digest over a specific retained revision. A missing
/// resolver yields Unknown; it never invents a comparison capability.
/// </summary>
public interface ISyncAuthorityCommonDigestResolver
{
    Task<SyncAuthorityCommonDigest?> ResolveAsync(SyncAuthorityCommonDigestRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>One shared cross-side content comparison used by the real planners.</summary>
public interface ISyncAuthorityContentComparison
{
    Task<SyncContentComparisonResult> CompareAsync(SyncAuthorityObservedEntry left,
        SyncAuthorityObservedEntry right, CancellationToken cancellationToken = default);
}

/// <summary>
/// The single-algorithm compatibility policy for existing planner callers that omit the optional
/// provider-aware comparer. Missing hashes remain Unknown; it never compares across algorithms because it
/// is only ever used where both sides were produced with one registered algorithm.
/// </summary>
public sealed class SyncAuthorityLegacyContentComparison : ISyncAuthorityContentComparison
{
    public static SyncAuthorityLegacyContentComparison Instance { get; } = new();

    public Task<SyncContentComparisonResult> CompareAsync(SyncAuthorityObservedEntry left,
        SyncAuthorityObservedEntry right, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Compare(left, right));
    }

    private static SyncContentComparisonResult Compare(SyncAuthorityObservedEntry left,
        SyncAuthorityObservedEntry right)
    {
        if (!string.Equals(left.ObjectKind, right.ObjectKind, StringComparison.Ordinal))
            return SyncContentComparisonResult.Different("The retained object kinds differ.");
        if (string.Equals(left.ObjectKind, "directory", StringComparison.Ordinal))
            return SyncContentComparisonResult.Equal(
                "Both sides retain a directory, whose content is carried by its members.");
        if (left.ContentHash is null || right.ContentHash is null || left.Size is null || right.Size is null)
            return SyncContentComparisonResult.Unknown(
                "The retained entries do not carry a complete non-null hash and size.");
        return left.Size == right.Size &&
               string.Equals(left.ContentHash, right.ContentHash, StringComparison.OrdinalIgnoreCase)
            ? SyncContentComparisonResult.Equal("Both sides retain the same non-null content hash and size.")
            : SyncContentComparisonResult.Different("The retained content hashes or sizes differ.");
    }
}

/// <summary>
/// Provider-aware comparison. Left and right native hash algorithms come from the registered provider
/// definitions; different algorithms are never compared as raw strings.
/// </summary>
public sealed class SyncAuthorityProviderAwareContentComparison : ISyncAuthorityContentComparison
{
    /// <summary>The exact-key bound on the instance-local resolved-digest cache.</summary>
    public const int DefaultCacheCapacity = 1024;

    private readonly string _leftAlgorithm;
    private readonly string _rightAlgorithm;
    private readonly ISyncAuthorityCommonDigestResolver? _resolver;
    private readonly int _cacheCapacity;
    private readonly Dictionary<string, SyncAuthorityCommonDigest> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheInsertionOrder = new();

    /// <summary>Carries the left/right native hash algorithms from the registered provider definitions.</summary>
    public SyncAuthorityProviderAwareContentComparison(string? leftProvider, string? rightProvider,
        ISyncAuthorityCommonDigestResolver? resolver = null, int cacheCapacity = DefaultCacheCapacity)
        : this(new ResolvedAlgorithms(SyncContentIdentity.AlgorithmFor(leftProvider),
            SyncContentIdentity.AlgorithmFor(rightProvider)), resolver, cacheCapacity)
    {
    }

    private SyncAuthorityProviderAwareContentComparison(ResolvedAlgorithms algorithms,
        ISyncAuthorityCommonDigestResolver? resolver, int cacheCapacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithms.Left);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithms.Right);
        ArgumentOutOfRangeException.ThrowIfLessThan(cacheCapacity, 1);
        _leftAlgorithm = algorithms.Left;
        _rightAlgorithm = algorithms.Right;
        _resolver = resolver;
        _cacheCapacity = cacheCapacity;
    }

    private readonly record struct ResolvedAlgorithms(string Left, string Right);

    /// <summary>Explicit algorithm names for a caller that already resolved them from an endpoint document.</summary>
    public static SyncAuthorityProviderAwareContentComparison ForAlgorithmNames(string leftAlgorithm,
        string rightAlgorithm, ISyncAuthorityCommonDigestResolver? resolver = null,
        int cacheCapacity = DefaultCacheCapacity) =>
        new(new ResolvedAlgorithms(leftAlgorithm, rightAlgorithm), resolver, cacheCapacity);

    /// <summary>Read-only diagnostic: the number of exact keys currently retained in the instance cache.</summary>
    public int CachedDigestCount
    {
        get { lock (_cache) return _cache.Count; }
    }

    public async Task<SyncContentComparisonResult> CompareAsync(SyncAuthorityObservedEntry left,
        SyncAuthorityObservedEntry right, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(left.ObjectKind, right.ObjectKind, StringComparison.Ordinal))
            return SyncContentComparisonResult.Different(
                "The retained object kinds differ, so the contents cannot be equal.");
        if (string.Equals(left.ObjectKind, "directory", StringComparison.Ordinal))
            return SyncContentComparisonResult.Equal(
                "Both sides retain a directory, whose content is carried by its members.");
        if (!string.Equals(left.ObjectKind, "file", StringComparison.Ordinal))
            return SyncContentComparisonResult.Unknown(
                "The retained object kind is neither a byte file nor a directory, so content equality is unknown.");
        if (left.Size is null || right.Size is null)
            return SyncContentComparisonResult.Unknown(
                "At least one side retains no known size; an unknown size is never equality.");
        if (left.Size != right.Size)
            return SyncContentComparisonResult.Different(
                "The retained sizes differ, so the contents cannot be equal.");
        if (string.IsNullOrWhiteSpace(left.ContentHash) || string.IsNullOrWhiteSpace(right.ContentHash))
            return SyncContentComparisonResult.Unknown(
                "At least one side retains no content identity; a missing identity is never equality.");

        if (string.Equals(_leftAlgorithm, _rightAlgorithm, StringComparison.Ordinal))
        {
            if (!SyncContentIdentity.IsKnown(_leftAlgorithm))
                return SyncContentComparisonResult.Unknown(
                    "The registered content-identity algorithm " + _leftAlgorithm + " is not comparable.");
            if (!SyncContentIdentity.IdentityMatchesAlgorithm(_leftAlgorithm, left.ContentHash) ||
                !SyncContentIdentity.IdentityMatchesAlgorithm(_rightAlgorithm, right.ContentHash))
                return SyncContentComparisonResult.Unknown(
                    "At least one retained identity is not a valid " + _leftAlgorithm +
                    " digest, so it cannot be compared.");
            return string.Equals(left.ContentHash, right.ContentHash, StringComparison.OrdinalIgnoreCase)
                ? SyncContentComparisonResult.Equal(
                    "Both sides retain the same valid " + _leftAlgorithm + " identity at the same size.")
                : SyncContentComparisonResult.Different(
                    "Two valid " + _leftAlgorithm + " identities at the same size differ.");
        }

        // Incomparable native algorithms. A common verified digest is required; the native hash strings are
        // never compared and no capability or equality is invented when the proof is unavailable.
        var (leftDigest, leftRefusal) = await CommonDigestAsync("left", left, _leftAlgorithm, cancellationToken);
        if (leftDigest is null) return SyncContentComparisonResult.Unknown("Left side: " + leftRefusal);
        var (rightDigest, rightRefusal) =
            await CommonDigestAsync("right", right, _rightAlgorithm, cancellationToken);
        if (rightDigest is null) return SyncContentComparisonResult.Unknown("Right side: " + rightRefusal);
        return string.Equals(leftDigest.Sha256, rightDigest.Sha256, StringComparison.OrdinalIgnoreCase)
            ? SyncContentComparisonResult.Equal("The verified common SHA-256 digests of the " +
                _leftAlgorithm + " and " + _rightAlgorithm + " identities match at the same size.")
            : SyncContentComparisonResult.Different("The verified common SHA-256 digests of the " +
                _leftAlgorithm + " and " + _rightAlgorithm + " identities differ at the same size.");
    }

    private async Task<(SyncAuthorityCommonDigest? Digest, string Refusal)> CommonDigestAsync(string side,
        SyncAuthorityObservedEntry entry, string algorithm, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Size is null || string.IsNullOrWhiteSpace(entry.ContentHash))
            return (null, "the retained entry carries no complete size and native identity.");
        var size = entry.Size.Value;
        var nativeHash = entry.ContentHash;
        if (string.Equals(algorithm, SyncContentIdentity.Sha256, StringComparison.Ordinal))
        {
            // A verified native SHA-256 is already the common digest; no download and no resolver call.
            if (!SyncContentIdentity.IdentityMatchesAlgorithm(SyncContentIdentity.Sha256, nativeHash))
                return (null, "the retained " + SyncContentIdentity.Sha256 +
                    " identity is not a valid SHA-256 digest.");
            return (new SyncAuthorityCommonDigest(entry.RootBindingId, entry.ObjectVersionId, size,
                SyncContentIdentity.Sha256, nativeHash, nativeHash, size), string.Empty);
        }
        var key = CacheKey(side, entry, algorithm);
        if (TryGetCached(key, out var cached)) return (cached, string.Empty);
        if (_resolver is null)
            return (null, "the " + algorithm + " identity has no comparable common digest and no " +
                "revision-bound digest resolver is available.");
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = await _resolver.ResolveAsync(new SyncAuthorityCommonDigestRequest(side,
            entry.RelativePath, entry.RootBindingId, entry.ObjectVersionId, entry.ObjectKind, size, algorithm,
            nativeHash), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (resolved is null)
            return (null, "the revision-bound digest resolver returned no verified common digest.");
        var refusal = RefusalFor(resolved, entry, size, algorithm, nativeHash);
        if (refusal is not null) return (null, refusal);
        Cache(key, resolved);
        return (resolved, string.Empty);
    }

    private static string? RefusalFor(SyncAuthorityCommonDigest resolved, SyncAuthorityObservedEntry entry,
        long size, string algorithm, string nativeHash)
    {
        if (!string.Equals(resolved.RootBindingId, entry.RootBindingId, StringComparison.Ordinal))
            return "the resolved digest is bound to a different root binding than the one requested.";
        if (!string.Equals(resolved.ObjectVersionId, entry.ObjectVersionId, StringComparison.Ordinal))
            return "the resolved digest is bound to a different object version than the one requested; " +
                "a revision-bound digest never applies to another revision.";
        if (resolved.Size != size || resolved.ByteCount != size)
            return "the resolved digest reports a different size or byte count than the requested identity.";
        if (!string.Equals(resolved.NativeAlgorithm, algorithm, StringComparison.Ordinal))
            return "the resolved digest reports a different native algorithm than the one requested.";
        if (!string.Equals(resolved.NativeHash, nativeHash, StringComparison.OrdinalIgnoreCase))
            return "the resolved digest reports a different native hash than the one requested.";
        if (!SyncContentIdentity.IdentityMatchesAlgorithm(SyncContentIdentity.Sha256, resolved.Sha256))
            return "the resolved digest is not a valid SHA-256 digest.";
        return null;
    }

    private static string CacheKey(string side, SyncAuthorityObservedEntry entry, string algorithm) =>
        string.Join("\n", side, entry.RootBindingId, entry.ObjectVersionId,
            (entry.Size ?? -1).ToString(CultureInfo.InvariantCulture), algorithm, entry.ContentHash ?? "none");

    private bool TryGetCached(string key, out SyncAuthorityCommonDigest digest)
    {
        lock (_cache) return _cache.TryGetValue(key, out digest!);
    }

    private void Cache(string key, SyncAuthorityCommonDigest digest)
    {
        lock (_cache)
        {
            if (_cache.ContainsKey(key)) return;
            _cache[key] = digest;
            _cacheInsertionOrder.Enqueue(key);
            while (_cache.Count > _cacheCapacity) _cache.Remove(_cacheInsertionOrder.Dequeue());
        }
    }
}
