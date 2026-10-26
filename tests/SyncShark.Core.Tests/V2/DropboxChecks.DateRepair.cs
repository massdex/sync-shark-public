namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    internal static async Task DateRepairMetadataPageAsync()
    {
        await SourceAbsenceBatchAsync();
        await ReviewedRemovalRootListingAsync();
        using var fixture = new Harness();
        fixture.Wire.SeedFile("a.txt", "a");
        fixture.Wire.SeedFile("b.txt", "b");
        var rows = await fixture.Client.ReadMetadataPageAsync(["a.txt", "b.txt"], CancellationToken.None);
        LocalChecks.Require(rows.Count == 2 && rows.Values.All(row => row is not null), "page returns exact metadata");
        LocalChecks.Require(fixture.Wire.Calls.Count(call => call.EndsWith("/get_current_account")) == 1 &&
            fixture.Wire.Calls.Count(call => call.EndsWith("/get_metadata")) == 3,
            "page performs one account and one root check, plus one metadata request per candidate");
        await fixture.Client.ReadMetadataPageAsync(["a.txt"], CancellationToken.None);
        LocalChecks.Require(fixture.Wire.Calls.Count(call => call.EndsWith("/get_current_account")) == 2 &&
            fixture.Wire.Calls.Count(call => call.EndsWith("/get_metadata")) == 5,
            "next page freshly verifies account and root");
        fixture.Wire.MetadataFailurePath = DropboxWire.RootId + "/a.txt";
        var errors = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var partial = await fixture.Client.ReadMetadataPageAsync(["a.txt", "b.txt"], CancellationToken.None,
            (path, error) => errors[path] = error.Message);
        LocalChecks.Require(errors.ContainsKey("a.txt") && partial.ContainsKey("b.txt") && !partial.ContainsKey("a.txt"),
            "one failed Dropbox lookup does not discard successful sibling metadata");
        LocalChecks.Require(fixture.Wire.Calls.All(call => call.EndsWith("/get_current_account") || call.EndsWith("/get_metadata")),
            "metadata repair sends no content, upload or deletion requests");
    }
}
