using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    // Owner decision "Local journal gap verification": missing evidence is unknown, not unchanged.
    // A stored item without a hash, or one flagged NeedsRehash, must require content proof even when the
    // journal proof matches its metadata and its USN sits below the boundary. Discovery therefore keeps a
    // durable protection row instead of retiring it. This reads the real SQLite rows and the durable
    // protection frontier, not a "touched" callback: promotion and plan touch are separate owners.
    private static async Task GapUnchangedRequiresContentProofChecksAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var product = Path.Combine(folder, "store");
        var root = new RootDef("proof-root", SyncProviders.Local, null, Path.Combine(folder, "root"),
            "id:proof-root");
        var remote = new RootDef("proof-cloud", SyncProviders.Dropbox, "acc", "/proof", "id:proof-cloud");
        var pair = new PairDef("proof-pair", "proof-pair", root, remote, [], [], true);
        const string cursor = "0000000000000001:200";
        var stamp = DateTimeOffset.UtcNow;
        var hash = new string('a', 64);
        // Every proof's USN is below the boundary, so only missing evidence can reject retirement.
        var proved = new ItemState("proved.txt", ItemKind.File, SyncLocalEndpointStore.ObjectId(1, 10), null,
            hash, 3, stamp, ItemFlags.None);
        var missing = new ItemState("missing-hash.txt", ItemKind.File, SyncLocalEndpointStore.ObjectId(1, 11),
            null, null, 3, stamp, ItemFlags.None);
        var needs = new ItemState("needs-rehash.txt", ItemKind.File, SyncLocalEndpointStore.ObjectId(1, 12),
            null, hash, 3, stamp, ItemFlags.NeedsRehash);
        using var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System);
        store.SavePair(pair);
        var generation = store.BeginGeneration(root.RootId, "full");
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, generation, cursor, [proved, missing, needs], []));
        store.CompleteGeneration(generation, cursor, true);
        // Snapshot ingestion deliberately normalizes untrusted incoming hash/flag combinations.
        // Establish real verification debt first, then model a legacy retained hash on that dirty
        // entity. The proof guard must reject the flag even when a hash is physically present.
        store.RequireRehash(root.RootId, store.ByObject(root.RootId, needs.ObjectId)!, "fixture retained dirty proof");
        using (var seed = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(product, "v2", "state.db"), Pooling = false }.ToString()))
        {
            seed.Open();
            using var command = seed.CreateCommand();
            command.CommandText = "UPDATE file_state SET hash=$hash WHERE root_no=" +
                "(SELECT root_no FROM root WHERE root_id=$root) AND object_id=$object;";
            command.Parameters.AddWithValue("$hash", Convert.FromHexString(hash));
            command.Parameters.AddWithValue("$root", root.RootId);
            command.Parameters.AddWithValue("$object", needs.ObjectId);
            LocalChecks.Require(command.ExecuteNonQuery() == 1, "seed exactly one retained dirty hash");
        }
        var seededNeeds = store.ByObject(root.RootId, needs.ObjectId)!;
        var initialDebt = store.RehashPendingCount(root.RootId);
        LocalChecks.Require(seededNeeds.Hash == hash && (seededNeeds.Flags & ItemFlags.NeedsRehash) != 0 && initialDebt == 1,
            "proof fixture distinguishes a retained hash with dirty flag from a missing hash");
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, cursor, 100, true);
        var epoch = gaps.GapEpoch(root.RootId)!;
        var io = new GapCheckIo(stamp, cursor) { UsnOverride = 99 };
        await using (var worker = new LocalGapWorker(gaps, io))
            worker.Discover(root, epoch, default);

        // Raw rows are the durable oracle: the stored classification is not inferred from a projection.
        (string ObjectId, string? Reason)[] Reasons()
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(product, "v2", "state.db"), Pooling = false }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT object_id,discovery_reason FROM local_gap_item WHERE root_no=" +
                "(SELECT root_no FROM root WHERE root_id=$r) ORDER BY object_id;";
            command.Parameters.AddWithValue("$r", root.RootId);
            using var reader = command.ExecuteReader();
            var rows = new List<(string, string?)>();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            return rows.ToArray();
        }
        var reasons = Reasons().ToDictionary(row => row.ObjectId, row => row.Reason);
        LocalChecks.Require(reasons.GetValueOrDefault(proved.ObjectId) == "proved-unchanged" &&
            reasons.GetValueOrDefault(missing.ObjectId) == "content-proof-required" &&
            reasons.GetValueOrDefault(needs.ObjectId) == "content-proof-required",
            "missing hash and NeedsRehash require content proof while matching metadata still retires");

        gaps.PromoteGapDiscovery(root.RootId);
        var candidates = gaps.GapHashPage(root.RootId, epoch, 128)
            .Select(item => item.ObjectId).ToHashSet(StringComparer.Ordinal);
        LocalChecks.Require(candidates.Contains(missing.ObjectId) && candidates.Contains(needs.ObjectId) &&
            !candidates.Contains(proved.ObjectId),
            "promotion retires only the proved item and keeps both unproved items as hash candidates");

        var protection = store.ProtectionBatch(root.RootId, ItemFlags.NeedsRehash,
            [missing.Path, needs.Path, proved.Path, ""]);
        LocalChecks.Require(protection[0].Covered && protection[1].Covered && !protection[2].Covered &&
            protection[3].Contains,
            "durable protection survives promotion for unproven content and releases only the proved entity");

        var liveMissing = store.Live(root.RootId, [missing.Path]).Single();
        var liveNeeds = store.Live(root.RootId, [needs.Path]).Single();
        LocalChecks.Require(liveMissing.Hash is null && liveNeeds.Hash == hash &&
            (liveNeeds.Flags & ItemFlags.NeedsRehash) != 0 && store.RehashPendingCount(root.RootId) == initialDebt,
            "discovery classifies evidence without mutating live content or adding foreground hash debt");
        Console.WriteLine("PASS gap proof evidence: missing hash and NeedsRehash stay protected until verified");
    }
}
