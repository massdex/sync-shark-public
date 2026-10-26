namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task VerifyPostProofJournalEditAsync(SqliteStateStore store, RootDef root, NtfsUsnCheckpoint saved)
    {
        var before = store.Live(root.RootId).Single(item => item.Path == "proven.txt");
        LocalChecks.Seed(root.RootPath, before.Path, "changed");
        File.SetLastWriteTimeUtc(Path.Combine(root.RootPath, before.Path), before.Mtime.UtcDateTime);
        var through = new NtfsUsnCheckpoint(1, 0, 400, 0);
        var inventory = RestartInventory(store, root, through);
        inventory.JournalReadForTest = Read;
        bool Read(string _, NtfsUsnCheckpoint from, NtfsUsnCheckpoint to,
            out IReadOnlyList<NtfsUsnChange> changes, out string? failure, out NtfsUsnReadFailure kind, CancellationToken ct)
        {
            changes = [new(LocalItems.ParseObjectId(before.ObjectId).FileId,
                LocalItems.ParseObjectId(root.RootObjectId).FileId, 350, 1, FileAttributes.Normal, before.Path)];
            failure = null; kind = NtfsUsnReadFailure.OverLimit;
            return true;
        }
        await inventory.ChangesSinceAsync(saved.Cursor, store.BeginGeneration(root.RootId, "delta"), _ => true, default);
        var flagged = store.ByObject(root.RootId, before.ObjectId)!;
        LocalChecks.Require(flagged.Flags.HasFlag(ItemFlags.NeedsRehash) && flagged.Hash is null,
            "live journal re-flags a previously proven same-size same-mtime content edit");
        var proof = await inventory.GapIo!.HashAsync(root, flagged, default);
        LocalChecks.Require(proof.Hash == DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("changed")) &&
            store.CompleteRehash(root.RootId, flagged, proof.Hash!),
            "native content proof establishes the later edit instead of reusing prior epoch proof");
    }
}
