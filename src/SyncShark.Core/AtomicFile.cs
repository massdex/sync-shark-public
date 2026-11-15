namespace Massdex.SyncShark;

/// <summary>Publishes a completed file by same-directory move, absorbing a brief external hold (#135).</summary>
internal static class AtomicFile
{
    internal static void Move(string source, string destination, bool overwrite, AtomicReplacePolicy policy,
        CancellationToken cancellationToken = default) =>
        Move(source, destination, overwrite, policy, cancellationToken, null);

    /// <param name="onDelay">Test seam: runs before each retry instead of waiting out the policy's delay.</param>
    internal static void Move(string source, string destination, bool overwrite, AtomicReplacePolicy policy,
        CancellationToken cancellationToken, Action<TimeSpan>? onDelay)
    {
        for (var retry = 0; ; retry++)
        {
            // The caller's cancellation is this boundary's own: a cancelled write never publishes.
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            // A short exponential backoff suits the antivirus or indexer hold that briefly owns the file.
            // After the bound the original failure propagates unchanged, so the caller's existing failure
            // and notice path is exactly what runs.
            catch (Exception error) when (retry < policy.Delays.Count && IsTransientHold(error))
            {
                if (onDelay is { } controlled) controlled(policy.Delays[retry]);
                else if (cancellationToken.WaitHandle.WaitOne(policy.Delays[retry]))
                    throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    // A sharing violation or an access denial is how Windows reports a brief external hold.
    private static bool IsTransientHold(Exception error) =>
        error is UnauthorizedAccessException ||
        error is IOException && error.HResult is SharingViolation or AccessDenied;

    private const int SharingViolation = unchecked((int)0x80070020);
    private const int AccessDenied = unchecked((int)0x80070005);
}
