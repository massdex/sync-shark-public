namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    public static async Task IncludePreviewScopeAsync()
    {
        using var h = new Harness();
        h.Wire.SeedFolder("wanted");
        h.Wire.SeedFile("wanted/inside.txt", "inside");
        h.Wire.SeedFolder("sibling");
        h.Wire.SeedFile("sibling/outside.txt", "outside");
        var folder = await h.Client.FindEntryAtPathAsync("wanted");
        var preview = await h.Client.ListPreviewSubtreeAsync("wanted", 10, CancellationToken.None);
        Require(preview.Complete && preview.Entries.Count == 2 &&
            preview.Entries.All(e => e.RelativePath == "wanted" || e.RelativePath.StartsWith("wanted/")),
            "include preview retains exactly the requested subtree");
        Require(h.Wire.ListRequests.Count == 1 && h.Wire.ListRequests[0] == (folder!.Id, true),
            "include preview lists only the exact folder ID, never the pair root");
        // Root verification also checks the account/namespace binding. That read is required
        // safety work, not a content scan or effect; keep an exact read-only route allowlist.
        Require(h.Wire.Calls.Contains("/2/users/get_current_account"),
            "include preview verifies the bound account identity");
        Require(h.Wire.Calls.All(call => call is "/2/users/get_current_account" or
                "/2/files/get_metadata" or "/2/files/list_folder"),
            "include preview performs no history, deletion, cursor checkpoint or write routes");
        var capped = await h.Client.ListPreviewSubtreeAsync("wanted", 1, CancellationToken.None);
        Require(!capped.Complete && capped.Entries.Count == 1, "remote cap enforced while collecting");
        var lists = h.Wire.ListRequests.Count;
        var exact = await h.Client.ListPreviewSubtreeAsync("wanted/inside.txt", 10, CancellationToken.None);
        Require(exact.Complete && exact.Entries.Count == 1 && h.Wire.ListRequests.Count == lists,
            "file preview is exact metadata without any list request");
        var missing = await h.Client.ListPreviewSubtreeAsync("missing", 10, CancellationToken.None);
        Require(missing.Complete && missing.Entries.Count == 0 && h.Wire.ListRequests.Count == lists,
            "missing subtree does not fall back to a root scan");
    }
}
