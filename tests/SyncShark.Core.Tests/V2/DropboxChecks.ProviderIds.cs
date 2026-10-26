using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static void ProviderIdentityChecks()
    {
        foreach (var (input, expected) in new (string?, string)[]
        {
            (null, "dropbox"), ("", "dropbox"), (" \t", "dropbox"),
            (" LOCAL ", "local"), ("DropBox", "dropbox"),
            (" GOOGLE-DRIVE ", "google-drive"), (" Other ", "other")
        })
            Require(ProviderIds.Normalize(input) == expected && SyncProviders.Normalize(input) == expected,
                "lower and legacy provider normalization retain protocol identity");
        Require(ProviderIds.Local == SyncProviders.Local && ProviderIds.Dropbox == SyncProviders.Dropbox &&
            ProviderIds.GoogleDrive == SyncProviders.GoogleDrive, "provider const aliases remain identical");
        Require(ProviderIds.Normalize("LOCAL") is ProviderIds.Local, "provider IDs remain constant patterns");

        var root = new RootDef("root-a", ProviderIds.Dropbox, "account", "/Parent", "id:root");
        var coverage = new InventoryCoverage(root, ["Child", "Child/Nested"], ["*.tmp"], true);
        using var json = JsonDocument.Parse(coverage.Json);
        Require(json.RootElement.GetProperty("Root").GetProperty("RootId").GetString() == root.RootId &&
            json.RootElement.GetProperty("Prefixes")[0].GetString() == "Child" &&
            json.RootElement.GetProperty("Ignores")[0].GetString() == "*.tmp" &&
            json.RootElement.GetProperty("Artifacts").GetBoolean() &&
            !json.RootElement.TryGetProperty("root", out _), "coverage retains PascalCase persisted paths");
        var restored = JsonSerializer.Deserialize<InventoryCoverage>(coverage.Json)!;
        Require(restored.Root == root && restored.CanReuse(coverage), "coverage JSON preserves root binding");
        Require(!(restored with { Root = root with { RootObjectId = "id:other" } }).CanReuse(coverage),
            "coverage cannot cross root identity");
        Require(!(restored with { Artifacts = false }).CanReuse(coverage) &&
            !(restored with { Ignores = [] }).CanReuse(coverage), "coverage artifacts and ignore widening invalidate reuse");
        Require((restored with { Prefixes = [] }).Widened(coverage).SequenceEqual(["Child"]),
            "coverage widening returns only outer removed boundary");
    }
}
