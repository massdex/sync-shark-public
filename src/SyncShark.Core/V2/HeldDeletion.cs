namespace Massdex.SyncShark.V2;

/// <summary>A reviewed local-only path and the conditional rule authorized for it, not an effect yet.</summary>
public sealed record HeldDeletion(string Path, Side Local, string Rule = HeldDeletion.NewerDeletionRule)
{
    // Persisted review identifier retained for existing prepares. Decision 78 refines its file semantics:
    // later deletion + equal hashes deletes without Recovery; different restores; unknown stays pending.
    public const string NewerDeletionRule = "dropbox-deletion-newer-than-local:recovery-first-delete;otherwise-upload";

    public const string NewerDeletionDisplayRule =
        "dropbox-deletion-newer-than-local: delete locally without Recovery if identical to the deleted version; " +
        "restore to Dropbox if different; stay pending if either hash is unknown; " +
        "upload if deleted before the local change or never existed";
}
