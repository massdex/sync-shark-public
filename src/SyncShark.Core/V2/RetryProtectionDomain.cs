namespace Massdex.SyncShark.V2;

internal static class RetryProtectionDomain
{
    internal static bool Includes(SyncRetry retry, bool restoredOnly = false) =>
        (!retry.ReprobeEvidence || retry.RetryAfter < DateTimeOffset.MaxValue) &&
        (!restoredOnly || retry.ReprobeEvidence);
}
