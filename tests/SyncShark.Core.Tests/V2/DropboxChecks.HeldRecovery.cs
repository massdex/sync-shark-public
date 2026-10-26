namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    internal static async Task HeldFolderTombstonesAsync()
    {
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("folder");
            await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            Require(h.Inventory.IsAtOrUnderExactTombstone("folder/nested"),
                "an exact ancestor tombstone authorizes an empty descendant folder");
        }
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("folder");
            h.Wire.SeedFolder("folder");
            await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            Require(!h.Inventory.IsAtOrUnderExactTombstone("folder/nested"),
                "a deleted then recreated live ancestor no longer authorizes an empty descendant delete");
        }
        using (var h = new Harness())
            Require(!h.Inventory.IsAtOrUnderExactTombstone("folder/nested"),
                "an unavailable listing never authorizes an empty folder delete");
        Console.WriteLine("PASS HeldRecovery_ExactAncestorTombstones");
    }
}
