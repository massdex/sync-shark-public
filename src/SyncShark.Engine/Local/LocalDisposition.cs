namespace Massdex.SyncShark.Engine.Local;

/// <summary>Disposes the exact held file authorized by an armed permanent-removal note.</summary>
internal static class LocalDisposition
{
    /// <summary>Permanently disposes the exact selected file named by an armed pending note from a newly opened
    /// writer-and-delete-refusing handle after its identity and complete selected content are revalidated. Only the
    /// removal owner calls this, after it durably armed the permitted fallback after a definite no-effect recycle
    /// refusal and rechecked the live setting; missing or stale evidence throws without an effect, and a failure
    /// after the disposition is marked is Unknown, never success.</summary>
    internal static async Task<RemovalResult> DeletePermanentlyAsync(string fullPath, PendingEffect note,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SyncLocalFileAuthority? held = null;
        try
        {
            held = OpenExclusive(fullPath);
            LocalRemovalProof.Verify(held, note.Losing, fullPath);
            await LocalRemovalProof.VerifySelectedContentAsync(held, note.Losing, fullPath, ct).ConfigureAwait(false);
            // The mark has no effect until the held handle closes.
            held.DeleteOnClose();
        }
        catch
        {
            if (held is not null)
            {
                try { held.Dispose(); }
                catch (Exception error) when (LocalErrors.IsFileFailure(error))
                {
                    // Preserve the primary failure; the permanent disposition was never marked.
                }
            }
            throw;
        }
        try { held.Dispose(); }
        catch (Exception error)
        {
            // Closing the marked handle performs the disposition; any failure here is an uncertain effect.
            return new RemovalResult(RemovalOutcome.Unknown, null, null,
                LocalErrors.Cause(error, TextKey.RecycleFailed));
        }
        return new RemovalResult(RemovalOutcome.Removed, null, null, null);
    }

    private static SyncLocalFileAuthority OpenExclusive(string fullPath)
    {
        try { return SyncLocalFileAuthority.OpenExistingExclusive(fullPath); }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw new ItemFailedException(fullPath, LocalErrors.Cause(error, TextKey.RecycleFailed), error);
        }
    }

}
