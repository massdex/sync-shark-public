using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static class ProvenanceClaimChecks
{
    internal static void Run(string folder)
    {
        var path = Path.Combine(folder, "claims.db");
        using var output = new StringWriter();
        using var provenance = new SqlitePlanProvenance(path, output);
        provenance.BeginRun(1);
        var item = new ItemState("Parent/File", ItemKind.File, "entity", null, null, 1,
            DateTimeOffset.UnixEpoch, ItemFlags.None);
        for (var i = 0; i < 511; i++) provenance.Claim("baseline-identity-A", item);
        using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString());
        reader.Open();
        using var command = reader.CreateCommand();
        command.CommandText = "SELECT COUNT(*),MAX(rowid) FROM claims";
        using (var rows = command.ExecuteReader())
            LocalChecks.Require(rows.Read() && rows.GetInt64(0) == 511 && rows.GetInt64(1) == 511,
                "claims append without a random-key index and commit at the bounded transaction boundary");
        provenance.Claim("baseline-identity-A", item with { ObjectId = "other-entity" });
        provenance.Claim("baseline-identity-B", item);
        var counts = new List<string>();
        provenance.Summary(counts.Add);
        LocalChecks.Require(counts.Contains("Provenance run 1: claims:baseline-identity-A=2") &&
            counts.Contains("Provenance run 1: claims:baseline-identity-B=1"),
            "summary deduplicates identity plus canonical path per kind across committed and partial batches");
        provenance.BeginRun(2);
        provenance.Claim("baseline-identity-A", item);
        counts.Clear();
        provenance.Summary(counts.Add);
        LocalChecks.Require(counts.Contains("Provenance run 2: claims:baseline-identity-A=1") &&
            counts.Contains("Provenance run 2: claims:baseline-identity-B=0"), "new run clears prior claims");
    }
}
