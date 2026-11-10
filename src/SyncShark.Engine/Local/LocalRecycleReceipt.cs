using System.Globalization;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Classifies the exact recycled object named by a shell receipt.</summary>
internal static class LocalRecycleReceipt
{
    // The receipt names the exact recycled object by opening only that Shell-returned path read-only and reading its
    // identity and complete content from that one handle; a missing or unreadable receipt is Unknown, never success.
    // A mismatch hashes only the receipt and returns it as the victim proof the wrong-restore order records.
    internal static async Task<(RemovalResult Result, string? BinLocator, StageProof? Victim)> VerifyAsync(
        string? receiptPath, PilotDirectoryIdentity? expected, CancellationToken ct)
    {
        if (receiptPath is null || expected is null)
            return (new RemovalResult(RemovalOutcome.Unknown, null, null, null), receiptPath, null);
        try
        {
            using var receipt = SyncLocalFileAuthority.OpenExistingSharedRead(receiptPath);
            var identity = receipt.ReadIdentity();
            if (identity == expected)
                return (new RemovalResult(RemovalOutcome.Removed, null, null, null), receiptPath, null);
            // A read failure here is Unknown, never a pre-effect refusal: the shell may already have acted.
            var hash = await LocalSide.HashAsync(receipt.Stream, ct).ConfigureAwait(false);
            var victim = new StageProof(receiptPath,
                identity.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture),
                LocalIdentity.FileIdText(identity.FileId),
                receipt.Stream.Length, hash);
            return (new RemovalResult(RemovalOutcome.Retained, null, null, null), receiptPath, victim);
        }
        catch (OperationCanceledException)
        {
            // The shell may already have acted; a cancelled proof read is an unknown effect, not an absent one.
            return (new RemovalResult(RemovalOutcome.Unknown, null, null, null), receiptPath, null);
        }
        catch (Exception error) when (LocalErrors.IsFileFailure(error))
        {
            return (new RemovalResult(RemovalOutcome.Unknown, null, null, null), receiptPath, null);
        }
    }

}
