namespace Massdex.SyncShark.Engine.Local;

/// <summary>Revalidates selected removal identity and complete content on the held file.</summary>
internal static class LocalRemovalProof
{
    // The proof names the selected object by volume serial (RemovalProof.VolumeId) and file ID
    // (RemovalProof.Expected.Id) in invariant decimal; VerifySelectedContentAsync checks its size and complete
    // content hash. An in-place edit keeps the identity, so identity alone is never content authority.
    internal static PilotDirectoryIdentity Verify(SyncLocalFileAuthority held, RemovalProof proof, string fullPath)
    {
        PilotDirectoryIdentity identity;
        try { identity = held.ReadIdentity(); }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw new ItemFailedException(fullPath, LocalErrors.Cause(error, TextKey.RecycleFailed), error);
        }
        if (!LocalFileProof.SameObject(identity, proof.VolumeId, proof.Expected.Id))
            throw new ChangedUnderneathException(fullPath);
        return identity;
    }

    internal static async Task VerifySelectedContentAsync(SyncLocalFileAuthority held, RemovalProof proof,
        string fullPath, CancellationToken ct)
    {
        try
        {
            var content = await LocalFileProof.SameContent(held, proof.Expected.Size, proof.ContentHash, ct)
                .ConfigureAwait(false);
            if (!content.Matches)
                throw new ChangedUnderneathException(fullPath)
                {
                    ObservedLocal = RemovalResult.CompleteObservation(proof.Expected, content.Hash)
                };
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            throw new ItemFailedException(fullPath, LocalErrors.Cause(error, TextKey.RecycleFailed), error);
        }
    }

}
