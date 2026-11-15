namespace Massdex.SyncShark;

/// <summary>
/// Maps ordered user endpoints onto the established physical local/remote engine ports. The local
/// port always names a real selected folder, never a staging substitute for a cloud endpoint.
/// Provider identity stays on the endpoint; orientation is independent of provider implementation.
/// </summary>
public sealed record SyncEndpointEngineLayout(SyncEndpoint Local, SyncEndpoint Remote, bool Reversed)
{
    public static SyncEndpointEngineLayout Create(SyncPairEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Left.IsLocal) return new(endpoints.Left, endpoints.Right, false);
        if (endpoints.Right.IsLocal) return new(endpoints.Right, endpoints.Left, true);
        throw new NotSupportedException("Two cloud endpoints require the cloud-to-cloud runtime.");
    }

    public static bool IsReversed(SyncPair pair) => pair.LeftEndpoint is { IsLocal: false } &&
        pair.RightEndpoint is { IsLocal: true };

    /// <summary>Stored direction is logical left/right; the existing planner's ports are physical.</summary>
    public static SyncMode EngineDirection(SyncPair pair) => IsReversed(pair) ? Reverse(pair.Direction) : pair.Direction;

    public static SyncMode Reverse(SyncMode direction) => direction switch
    {
        SyncMode.TwoWay => SyncMode.TwoWay,
        SyncMode.UploadOnly => SyncMode.DownloadOnly,
        SyncMode.DownloadOnly => SyncMode.UploadOnly,
        _ => throw new InvalidDataException("Unknown pair direction.")
    };

    /// <summary>Preserve every legacy identity; a side swap is a new reviewed binding.</summary>
    public static string DerivePairId(PilotSettings settings, string? remoteNamespace, bool reversed) =>
        SyncPair.DeriveId(settings, reversed ? "right-local\n" + remoteNamespace : remoteNamespace);
}
