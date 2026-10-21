using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    /// <summary>Visits bounded directory/file pages inside the affected entity's current subtree.</summary>
    public int EnqueueStoragePolicyPage(int limit = 128, IReadOnlyCollection<string>? activeRoots = null) => InStore(() =>
    {
        if (!_storageEnabled) return 0;
        CleanupStorageItems();
        BindStoragePathIntents();
        string rootId, objectId, afterDirKey, afterKey, afterName, priorScope; long afterDir, revision;
        using (var cmd = StoreSchema.Command(_connection, null, "SELECT w.root_id,w.object_id,w.after_dir_key,w.after_dir,w.after_name_key,w.after_name,w.revision,w.scope_key " +
            "FROM " + StorageRootsSql + " CROSS JOIN storage_subtree_sweep w ON w.root_id=roots.value " + StorageUnblockedSql + " ORDER BY w.root_id,w.object_id LIMIT 1;"))
        {
            StorageRootParameters(cmd, activeRoots);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return 0;
            rootId = reader.GetString(0); objectId = reader.GetString(1); afterDirKey = reader.GetString(2);
            afterDir = reader.GetInt64(3); afterKey = reader.GetString(4); afterName = reader.GetString(5); revision = reader.GetInt64(6); priorScope = reader.GetString(7);
        }
        var pairs = Pairs().Where(p => (p.A.RootId == rootId && p.A.Provider == SyncProviders.Local && p.B.Provider == SyncProviders.Dropbox) ||
            (p.B.RootId == rootId && p.B.Provider == SyncProviders.Local && p.A.Provider == SyncProviders.Dropbox)).ToArray();
        var root = pairs.Select(p => p.A.RootId == rootId ? p.A : p.B).FirstOrDefault();
        var item = objectId.Length == 0 || objectId == root?.RootObjectId ? null : ByObject(rootId, objectId);
        var scope = item?.Path ?? "";
        var rootNo = RootNo(rootId);
        if (root is null || rootNo is null || (objectId.Length != 0 && objectId != root.RootObjectId && item is null))
        {
            Finish(); return 0;
        }
        var budget = Math.Clamp(limit, 1, 512);
        var visited = 0;
        if (item is { Kind: ItemKind.File }) { Visit(item); Finish(); return 1; }
        var scopeKey = RowCodec.Key(scope);
        if (scopeKey != priorScope) { afterDirKey = scopeKey; afterDir = -1; afterKey = afterName = ""; }
        while (visited < budget)
        {
            long dir; string dirKey;
            using (var cmd = StoreSchema.Command(_connection, null,
                "SELECT dir_no,path_key FROM dir INDEXED BY dir_key WHERE " +
                (scope.Length == 0 ? "" : "path_key >= $scope AND path_key < $hi AND (path_key=$scope OR path_key >= $lo) AND ") +
                "(path_key,dir_no)>=($after,$dir) ORDER BY path_key,dir_no LIMIT 1;"))
            {
                cmd.Parameters.AddWithValue("$scope", scopeKey); cmd.Parameters.AddWithValue("$lo", scopeKey + "/"); cmd.Parameters.AddWithValue("$hi", scopeKey + "0");
                cmd.Parameters.AddWithValue("$after", afterDirKey); cmd.Parameters.AddWithValue("$dir", afterDir);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) { Finish(); return visited; }
                dir = reader.GetInt64(0); dirKey = reader.GetString(1);
            }
            var files = new List<(string Object, string Key, string Name)>();
            using (var cmd = StoreSchema.Command(_connection, null,
                "SELECT object_id,name_key,name FROM file_state WHERE root_no=$r AND dir_no=$d AND (name_key,name)>($k,$n) ORDER BY name_key,name LIMIT $limit;"))
            {
                cmd.Parameters.AddWithValue("$r", rootNo.Value); cmd.Parameters.AddWithValue("$d", dir);
                cmd.Parameters.AddWithValue("$k", dir == afterDir && dirKey == afterDirKey ? afterKey : "");
                cmd.Parameters.AddWithValue("$n", dir == afterDir && dirKey == afterDirKey ? afterName : "");
                cmd.Parameters.AddWithValue("$limit", budget - visited);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) files.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            afterDirKey = dirKey; afterDir = dir;
            if (files.Count == 0) { afterDir++; afterKey = afterName = ""; visited++; }
            else
            {
                foreach (var file in files) { var current = ByObject(rootId, file.Object); if (current is not null) Visit(current); }
                visited += files.Count; afterKey = files[^1].Key; afterName = files[^1].Name;
            }
        }
        StoreSchema.Execute(_connection, null, "UPDATE storage_subtree_sweep SET after_dir_key=$d,after_dir=$n,after_name_key=$k,after_name=$name,scope_key=$scope WHERE root_id=$r AND object_id=$o AND revision=$v;",
            ("$scope", scopeKey), ("$d", afterDirKey), ("$n", afterDir), ("$k", afterKey), ("$name", afterName), ("$r", rootId), ("$o", objectId), ("$v", revision));
        return visited;

        void Finish() => StoreSchema.Execute(_connection, null, "DELETE FROM storage_subtree_sweep WHERE root_id=$r AND object_id=$o AND revision=$v;", ("$r", rootId), ("$o", objectId), ("$v", revision));
        void Visit(ItemState local)
        {
            if (local.Kind != ItemKind.File) return;
            var mode = ResolveStoragePolicy(root!, local.Path);
            if (mode != "online-only")
            {
                StoreSchema.Execute(_connection, null, "DELETE FROM storage_settlement WHERE root_id=$r AND object_id=$o AND kind IS NULL;", ("$r", rootId), ("$o", local.ObjectId));
                if ((local.Flags & ItemFlags.OwnedPointer) != 0)
                    StoreSchema.Execute(_connection, null, "INSERT INTO storage_hydration(root_id,object_id) VALUES($r,$o) ON CONFLICT(root_id,object_id) DO NOTHING;", ("$r", rootId), ("$o", local.ObjectId));
                return;
            }
            if ((local.Flags & ItemFlags.NeedsRehash) == 0)
                StoreSchema.Execute(_connection, null, "DELETE FROM storage_hydration WHERE root_id=$r AND object_id=$o;", ("$r", rootId), ("$o", local.ObjectId));
            if (local.Flags != ItemFlags.None || local.Hash is null) return;
            foreach (var pair in pairs)
            {
                var side = pair.A.RootId == rootId ? Side.A : Side.B;
                var remoteRoot = side == Side.A ? pair.B : pair.A;
                foreach (var baseline in BaselinesAt(pair.PairId, side, [local.Path]))
                {
                    var localBasis = side == Side.A ? baseline.A : baseline.B;
                    var remoteBasis = side == Side.A ? baseline.B : baseline.A;
                    var remote = ByObject(remoteRoot.RootId, remoteBasis.ObjectId);
                    if (local.ObjectId != localBasis.ObjectId || local.Hash != localBasis.Hash || local.Size != localBasis.Size || local.Mtime != localBasis.Mtime || local.Path != localBasis.Path ||
                        remote is not { Kind: ItemKind.File, Hash: not null, VersionId: not null } || remote.Hash != remoteBasis.Hash || remote.Size != remoteBasis.Size || remote.VersionId != remoteBasis.VersionId || remote.Path != remoteBasis.Path ||
                        local.Size != remote.Size || !string.Equals(local.Hash, remote.Hash, StringComparison.OrdinalIgnoreCase)) continue;
                    QueueStorageSettlement(rootId, local.ObjectId, remoteRoot.RootId, new PilotRemoteEntry(local.Path, PilotEntryKind.File, remote.ObjectId, remote.VersionId, remote.Size, remote.Hash, local.Mtime));
                }
            }
        }
    });
}
