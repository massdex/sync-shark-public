namespace Massdex.SyncShark.Engine.Local;

/// <summary>Holds the selected file through its recycle effect and exact receipt verification.</summary>
public static class LocalRecycle
{
    /// <summary>Recycles fullPath on an STA thread. A file proof is verified against the held source before the
    /// shell acts: a stale or mismatched proof throws without an effect, and a file removal without a proof is
    /// refused. Once the shell may have acted, the result is conservative: NoEffect only for a definite pre-effect
    /// refusal, Removed only when the receipt proves the recycled object is the held one, Retained when the receipt
    /// is another object, and Unknown otherwise. The returned bin locator is the receipt path the shell named, when
    /// it named one; a Retained receipt also returns its victim StageProof (observed identity, size and full content
    /// hash) for the pending owner's wrong-victim recovery. OperationId and Final belong to the pending owner and
    /// stay absent; a folder carries no file proof and keeps the folder owner's removal contract.</summary>
    public static async Task<(RemovalResult Result, string? BinLocator, StageProof? Victim)> SendAsync(
        string fullPath, RemovalProof? proof, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SyncLocalFileAuthority? held = null;
        PilotDirectoryIdentity? identity = null;
        try
        {
            if (proof is null)
            {
                if (File.Exists(fullPath)) throw new ArgumentNullException(nameof(proof));
            }
            else
            {
                held = OpenGuard(fullPath);
                identity = LocalRemovalProof.Verify(held, proof, fullPath);
                await LocalRemovalProof.VerifySelectedContentAsync(held, proof, fullPath, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            var (result, binLocator) = LocalBin.RunShell(fullPath, identity);
            if (result is null)
                return await LocalRecycleReceipt.VerifyAsync(binLocator, identity, ct).ConfigureAwait(false);
            return (result, binLocator, null);
        }
        finally { held?.Dispose(); }
    }

    private static SyncLocalFileAuthority OpenGuard(string fullPath)
    {
        try { return SyncLocalFileAuthority.OpenExistingRecycleGuard(fullPath); }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw new ItemFailedException(fullPath, LocalErrors.Cause(error, TextKey.RecycleFailed), error);
        }
    }

}
