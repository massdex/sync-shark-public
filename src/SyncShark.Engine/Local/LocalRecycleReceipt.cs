using System.Globalization;
using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Classifies the exact recycled object named by a shell receipt.</summary>
internal static class LocalRecycleReceipt
{
    /// <summary>Test hook: the live acceptance harness receives (original path, receipt path) once per
    /// recycled folder; null in production.</summary>
    internal static Action<string, string>? Observed { get; set; }

    // The receipt walk proves every directory on its own enumeration handle: the root against the
    // deleted folder's volume and ID, and each subfolder against the ID its parent listed.
    internal static async IAsyncEnumerable<Listed> ContentsAsync(PairConfig pair, PathScope scope, string receipt,
        string path, string folderId, [EnumeratorCancellation] CancellationToken ct)
    {
        var root = pair.Roots?.Local ?? throw new ItemFailedException(path, TextKey.RecycleUnchecked, null);
        if (!long.TryParse(folderId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
            throw new ItemFailedException(path, TextKey.RecycleUnchecked, null);
        // Unproven NTFS-only premise, not documented by FILE_ID_INFO: the wide ID is the listed 64-bit ID with a zero
        // high half. The live T6 run proves it; if false, ListRoot refuses the receipt and the effect is Unknown.
        var expected = new DirectoryIdentity(root.VolumeSerialNumber, unchecked((ulong)id),
            root.FileIdHigh is null ? null : 0UL);
        var listing = new LocalListing(pair with { LocalRoot = receipt, Roots = pair.Roots! with { Local = expected } },
            scope, static (_, _, _, _) => { });
        var observed = false;
        await foreach (var listed in listing.Walk("", new ListOptions(Recursive: true, IncludeOutOfScope: true)
            { RequireListedIdentity = true }, ct).ConfigureAwait(false))
        {
            // A full walk re-yields an unreadable subfolder instead of throwing; here that is an unchecked receipt.
            if (listed.Entry?.Skip == TextKey.SkipUnreadable.ToString())
                throw new ItemFailedException(path, TextKey.RecycleUnchecked, null);
            // The first entry follows ListRoot's authentication of the receipt root.
            if (!observed) { observed = true; Observed?.Invoke(path, receipt); }
            var original = PathNames.Child(path, listed.Path);
            yield return new Listed(original, listed.Entry! with { Path = original });
        }
        // An authenticated empty receipt.
        if (!observed) Observed?.Invoke(path, receipt);
    }

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
