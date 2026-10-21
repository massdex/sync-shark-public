using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    public static void NameMappingScale()
    {
        var root = Path.Combine(Path.GetTempPath(), "mapping-scale-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(root, Settings with { StoreDurableCommits = false }, TimeProvider.System);
        var pair = Pair("mapping-scale", "C:\\local", "/cloud");
        store.SavePair(pair);
        MappingSnapshot(store, pair.B, Side.B, MappingItem("folder:", "folder", ItemKind.Folder),
            MappingItem("folder:/child:name", "target"));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = SqliteStateStore.DatabasePath(root), Pooling = false }.ToString()))
        {
            connection.Open();
            using var tx = connection.BeginTransaction();
            foreach (var (directory, count, prefix) in new[] { ("", 900000, "root"), ("folder:", 100000, "leaf") })
            {
                using var command = StoreSchema.Command(connection, tx,
                    "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<$count) " +
                    "INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,version_id,hash,size,mtime,flags) " +
                    "SELECT (SELECT root_no FROM root WHERE root_id=$root),(SELECT dir_no FROM dir WHERE path=$dir)," +
                    "printf('F%06d',x),printf('f%06d',x),0,$prefix||x,'v',X'11',1,0,0 FROM n;");
                command.Parameters.AddWithValue("$count", count);
                command.Parameters.AddWithValue("$root", pair.B.RootId);
                command.Parameters.AddWithValue("$dir", directory);
                command.Parameters.AddWithValue("$prefix", prefix);
                command.ExecuteNonQuery();
            }
            tx.Commit();
        }
        var recorder = new RecordingStore(store);
        var allocated = GC.GetTotalAllocatedBytes(true);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var scope = new RunScope(recorder, _ => true, _ => true).LoadTouched(pair, ["folder_/child_name"], [], []);
        var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        Require(scope.B.Count == 1 && scope.B[0].ObjectId == "target", "million-row delta loads only the matching leaf");
        Require(recorder.ChildDirectories.SequenceEqual(new[] { "folder:" }),
            "100k-child directory is read once; 900k unrelated root siblings are never read");
        Require(bytes < 256L * 1024 * 1024, "million-row mapping delta stays below 256 MiB of managed allocations");
        Console.WriteLine($"PASS mapping-scale: 1,000,002 items; 100,001 direct children; allocated={bytes}; elapsedMs={timer.ElapsedMilliseconds}; peakWorkingSet={System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64}");
    }
}
