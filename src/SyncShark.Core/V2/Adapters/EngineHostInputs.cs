namespace Massdex.SyncShark.V2;

/// <summary>What the host hands the engine: where state lives, the v1 state authority and the shared HTTP client.</summary>
/// <remarks>
/// The local Recovery location and retention come from the product settings store through
/// <paramref name="StateAuthority"/> (decision 55). <paramref name="DropboxCredentials"/> defaults to the Windows
/// protected store; a check passes a stand-in. <paramref name="LongpollHandler"/> is the handler of the engine's own
/// long-poll HTTP client (a check passes one that fails on any request); null means a production handler.
/// </remarks>
public sealed record EngineHostInputs(string ProductRoot, PilotStateAuthority? StateAuthority, HttpClient Http,
    IDropboxRefreshTokenStore? DropboxCredentials = null, HttpMessageHandler? LongpollHandler = null)
{
    // Explicit fixture dependency; null preserves the native production activity/power policy.
    internal Func<bool>? ContentPausedForTest { get; init; }
}
