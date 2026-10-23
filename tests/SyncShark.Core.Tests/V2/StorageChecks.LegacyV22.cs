using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    // Exact fresh v22 DDL from e5a9be0b's StoreSchema.cs (v22 adds entity-owned deferred planning work): the
    // historical base DDL plus every presence-guarded helper that version created. Not the current schema with
    // columns removed, so opening it exercises the real v22 -> current upgrade path.
    private const string LegacyV22Schema = """
        -- *_key columns hold PathKey.Of: equal exactly when equal under .NET OrdinalIgnoreCase (owner decision 50).
                CREATE TABLE dir(dir_no INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, path_key TEXT NOT NULL, mapped_key TEXT);
                CREATE INDEX dir_key ON dir(path_key);
                CREATE INDEX dir_mapped_key ON dir(mapped_key) WHERE mapped_key IS NOT NULL;
                CREATE TABLE root(root_no INTEGER PRIMARY KEY, root_id TEXT NOT NULL UNIQUE, cursor TEXT);
                CREATE TABLE pair(pair_no INTEGER PRIMARY KEY, pair_id TEXT NOT NULL UNIQUE, def_json TEXT NOT NULL,
                  first_sync_done INTEGER NOT NULL);
                CREATE TABLE generation(gen INTEGER PRIMARY KEY AUTOINCREMENT, root_no INTEGER NOT NULL, kind TEXT NOT NULL,
                  started INTEGER NOT NULL, finished INTEGER, complete INTEGER);
                CREATE TABLE file_state(root_no INTEGER NOT NULL, dir_no INTEGER NOT NULL, name_key TEXT NOT NULL, name TEXT NOT NULL,
                  kind INTEGER NOT NULL,
                  object_id TEXT NOT NULL, version_id TEXT, hash BLOB, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
                  flags INTEGER NOT NULL, identity_json TEXT, PRIMARY KEY(root_no, dir_no, name_key, name)) WITHOUT ROWID;
                CREATE INDEX fs_obj ON file_state(root_no, object_id);
                CREATE TABLE tombstone(root_no INTEGER NOT NULL, object_id TEXT NOT NULL, dir_no INTEGER NOT NULL,
                  name TEXT NOT NULL, deleted_at INTEGER NOT NULL, PRIMARY KEY(root_no, object_id)) WITHOUT ROWID;
                CREATE TABLE sync_point(pair_no INTEGER NOT NULL, object_key TEXT NOT NULL, gen INTEGER NOT NULL, kind INTEGER NOT NULL,
                  hash BLOB, size INTEGER NOT NULL,
                  a_dir INTEGER NOT NULL, a_name TEXT NOT NULL, a_name_key TEXT NOT NULL, a_object_id TEXT NOT NULL, a_version_id TEXT, a_mtime INTEGER NOT NULL,
                  b_dir INTEGER, b_name TEXT, b_name_key TEXT, b_object_id TEXT NOT NULL, b_version_id TEXT, b_mtime INTEGER NOT NULL,
                  PRIMARY KEY(pair_no, object_key)) WITHOUT ROWID;
                CREATE INDEX sp_a_obj ON sync_point(pair_no, a_object_id);
                CREATE INDEX sp_b_obj ON sync_point(pair_no, b_object_id);
                CREATE INDEX sp_a_dir ON sync_point(pair_no, a_dir, a_name_key);
                CREATE INDEX sp_b_dir ON sync_point(pair_no, b_dir, b_name_key) WHERE b_dir IS NOT NULL;
                -- A rowid table: an intent's JSON (~1 KB) overflows a WITHOUT ROWID page's local payload, one page per row.
                CREATE TABLE pending_effect(action_id TEXT PRIMARY KEY, pair_no INTEGER NOT NULL, action_json TEXT NOT NULL,
                  state TEXT NOT NULL, attempts INTEGER NOT NULL, updated INTEGER NOT NULL);
                CREATE INDEX pe_unresolved ON pending_effect(pair_no, state, updated);
                CREATE TABLE recovery(recovery_id TEXT PRIMARY KEY, pair_no INTEGER NOT NULL, action_id TEXT NOT NULL,
                  side TEXT NOT NULL, path TEXT NOT NULL, path_key TEXT, location TEXT NOT NULL, version_id TEXT, hash TEXT NOT NULL,
                  size INTEGER NOT NULL, created INTEGER NOT NULL, expires INTEGER NOT NULL) WITHOUT ROWID;
                CREATE INDEX rc_pair ON recovery(pair_no, created);
                CREATE TABLE notice(notice_id TEXT PRIMARY KEY, pair_no INTEGER NOT NULL, rule INTEGER NOT NULL, kind TEXT NOT NULL,
                  path TEXT NOT NULL, path_key TEXT, detail TEXT NOT NULL, reason_key TEXT NOT NULL, state TEXT NOT NULL, choice TEXT,
                  created INTEGER NOT NULL, restore_hold INTEGER NOT NULL DEFAULT 0) WITHOUT ROWID;
                CREATE INDEX nt_open ON notice(state, pair_no, created);
                -- Positive and negative probe results are reusable only after validating their basis cursor.
                CREATE TABLE deletion_evidence(pair_no INTEGER NOT NULL, root_no INTEGER NOT NULL, path TEXT NOT NULL,
                  path_key TEXT NOT NULL, deleted_at INTEGER, probed_at INTEGER NOT NULL, basis_cursor TEXT, content_hash TEXT, failure_json TEXT,
                  PRIMARY KEY(pair_no, root_no, path_key)) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS journal_volume(serial INTEGER PRIMARY KEY, volume TEXT NOT NULL, state_json TEXT NOT NULL);

                CREATE TABLE IF NOT EXISTS local_rehash(root_no INTEGER NOT NULL, object_id TEXT NOT NULL,
                  last_attempt INTEGER NOT NULL, reason TEXT NOT NULL, PRIMARY KEY(root_no, object_id)) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS rehash_retry ON local_rehash(root_no, last_attempt, object_id);


                CREATE TABLE IF NOT EXISTS planning_deferred(root_no INTEGER NOT NULL, object_id TEXT NOT NULL,
                  pair_no INTEGER NOT NULL, path TEXT NOT NULL, version INTEGER NOT NULL,
                  reason TEXT NOT NULL DEFAULT 'Waiting for content verification',
                  PRIMARY KEY(root_no,object_id,pair_no)) WITHOUT ROWID;
                DROP INDEX IF EXISTS planning_deferred_due;
                CREATE INDEX IF NOT EXISTS planning_deferred_pair ON planning_deferred(pair_no,root_no,object_id);


                CREATE TABLE IF NOT EXISTS sync_retry(pair_no INTEGER NOT NULL, path_key TEXT NOT NULL,
                  json TEXT NOT NULL, retry_after INTEGER NOT NULL, PRIMARY KEY(pair_no,path_key));
                CREATE INDEX IF NOT EXISTS sync_retry_due ON sync_retry(pair_no,retry_after,path_key);
                CREATE INDEX IF NOT EXISTS sync_retry_kind ON sync_retry(pair_no,json_extract(json,'$.Kind'),path_key);
                CREATE INDEX IF NOT EXISTS sync_retry_delete_blocked ON sync_retry(pair_no,path_key)
                  WHERE json_extract(json,'$.DeleteBlockedDetail') IS NOT NULL;
                CREATE INDEX IF NOT EXISTS sync_retry_delete_review ON sync_retry(pair_no,path_key)
                  WHERE json_extract(json,'$.MissingDeleteIdentityReview')=1;
                CREATE INDEX IF NOT EXISTS sync_retry_review_path ON sync_retry(pair_no,json_extract(json,'$.Path'))
                  WHERE json_extract(json,'$.MissingDeleteIdentityReview')=1;
                CREATE INDEX IF NOT EXISTS nt_groupable_delete ON notice(pair_no,reason_key,notice_id) WHERE state='open';
                CREATE INDEX IF NOT EXISTS pe_uncertain_delete ON pending_effect(pair_no,action_id)
                  WHERE state='uncertain' AND json_extract(action_json,'$.Kind')='Delete';


                CREATE TABLE IF NOT EXISTS local_gap(root_no INTEGER PRIMARY KEY, epoch TEXT NOT NULL,
                  journal_cursor TEXT NOT NULL, boundary INTEGER NOT NULL, trust_usn INTEGER NOT NULL,
                  after_object TEXT NOT NULL DEFAULT '', discovery_done INTEGER NOT NULL DEFAULT 0,
                  next_result INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS local_gap_item(root_no INTEGER NOT NULL, object_id TEXT NOT NULL,
                  epoch TEXT NOT NULL, expected_json TEXT NOT NULL, hash TEXT, proof_json TEXT,
                  result_seq INTEGER NOT NULL DEFAULT 0, last_attempt INTEGER NOT NULL DEFAULT 0,
                  PRIMARY KEY(root_no,object_id)) WITHOUT ROWID;

        ALTER TABLE local_gap ADD COLUMN inventory_pending INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE local_gap ADD COLUMN worker_after TEXT NOT NULL DEFAULT '';
        ALTER TABLE local_gap ADD COLUMN worker_done INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE local_gap_item ADD COLUMN metadata_ready INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE local_gap_item ADD COLUMN metadata_stamp TEXT;
        ALTER TABLE local_gap ADD COLUMN reason TEXT NOT NULL DEFAULT 'journal history discontinuity';
        ALTER TABLE local_gap_item ADD COLUMN failure_count INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE local_gap_item ADD COLUMN failure_reason TEXT;
        ALTER TABLE local_gap_item ADD COLUMN retry_allowed INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE local_gap_item ADD COLUMN defer_count INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE local_gap_item ADD COLUMN defer_reason TEXT;
        ALTER TABLE local_gap_item ADD COLUMN discovery_reason TEXT;
        CREATE INDEX IF NOT EXISTS gap_ready ON local_gap_item(root_no,epoch,last_attempt,object_id) WHERE hash IS NULL AND retry_allowed=1 AND metadata_ready=0;
        CREATE INDEX IF NOT EXISTS gap_metadata ON local_gap_item(root_no,epoch,object_id) WHERE metadata_ready=1;
        CREATE INDEX IF NOT EXISTS gap_waiting ON local_gap_item(root_no,object_id) WHERE hash IS NULL AND retry_allowed=0;
        CREATE INDEX IF NOT EXISTS gap_failed ON local_gap_item(root_no,object_id) WHERE failure_count>=3;
        CREATE INDEX IF NOT EXISTS gap_results ON local_gap_item(root_no,result_seq,object_id) WHERE hash IS NOT NULL;

                CREATE TABLE IF NOT EXISTS run_summary(seq INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL UNIQUE,
                  pair_id TEXT NOT NULL, started INTEGER NOT NULL, completed INTEGER NOT NULL, outcome TEXT NOT NULL,
                  done INTEGER NOT NULL, uploaded INTEGER NOT NULL, downloaded INTEGER NOT NULL, deleted INTEGER NOT NULL,
                  moved INTEGER NOT NULL, kept_both INTEGER NOT NULL, held INTEGER NOT NULL, failed INTEGER NOT NULL,
                  uncertain INTEGER NOT NULL, skipped INTEGER NOT NULL, deferred INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS run_summary_pair ON run_summary(pair_id, seq);

        CREATE INDEX IF NOT EXISTS nt_pair_reason ON notice(pair_no,reason_key);

                CREATE TABLE IF NOT EXISTS notice_legacy_fold(pair_no INTEGER NOT NULL, reason_key TEXT NOT NULL,
                  PRIMARY KEY(pair_no,reason_key)) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS fs_flagged ON file_state(root_no) WHERE flags != 0;CREATE INDEX IF NOT EXISTS fs_refused ON file_state(root_no, kind) WHERE (flags & 64) != 0;CREATE INDEX IF NOT EXISTS fs_quarantine ON file_state(root_no, kind) WHERE (flags & 128) != 0;
        CREATE INDEX IF NOT EXISTS pe_intent_order ON pending_effect(pair_no);
        CREATE INDEX IF NOT EXISTS rc_page ON recovery(pair_no, recovery_id);
        CREATE INDEX IF NOT EXISTS rc_path ON recovery(pair_no, path_key, created DESC, recovery_id DESC);CREATE INDEX IF NOT EXISTS rc_path_missing ON recovery(recovery_id) WHERE path_key IS NULL;
        CREATE INDEX IF NOT EXISTS nt_restore_page ON notice(pair_no, notice_id) WHERE state='open' AND (reason_key='recovery-restore-pending' OR reason_key='recovery-restore-ready');
        CREATE INDEX IF NOT EXISTS nt_restore_path ON notice(pair_no, path_key) WHERE state='open' AND reason_key='recovery-restore-pending';
        CREATE INDEX IF NOT EXISTS nt_restore_path_ready ON notice(pair_no, path_key) WHERE reason_key='recovery-restore-ready';
        CREATE INDEX IF NOT EXISTS nt_restore_hold_path ON notice(pair_no, path_key) WHERE state='open' AND reason_key='recovery-restore-pending' AND restore_hold=1;
        CREATE INDEX IF NOT EXISTS nt_restore_reason_page ON notice(notice_id) WHERE reason_key='recovery-restore-pending' OR reason_key='recovery-restore-ready';

                CREATE TABLE IF NOT EXISTS defender_deferral(object_id TEXT PRIMARY KEY, attempts INTEGER NOT NULL,
                  first_utc INTEGER NOT NULL, last_run TEXT NOT NULL, updated INTEGER NOT NULL, consent INTEGER NOT NULL DEFAULT 0, batch TEXT) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS defender_deferral_updated ON defender_deferral(updated,object_id);
                CREATE TABLE IF NOT EXISTS defender_grant(grant_id TEXT PRIMARY KEY, owner_object TEXT NOT NULL,
                  source_object TEXT, local_object TEXT, path TEXT NOT NULL, path_key TEXT NOT NULL,
                  stage_path TEXT NOT NULL, verified INTEGER NOT NULL DEFAULT 0) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS defender_grant_path ON defender_grant(path_key);
                CREATE TABLE IF NOT EXISTS quarantine_upgrade(name TEXT PRIMARY KEY) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS defender_deferral_batch ON defender_deferral(batch,object_id);
        INSERT INTO quarantine_upgrade VALUES('grant-backfill');
        INSERT INTO quarantine_upgrade VALUES('notice-path-key-backfill');

                CREATE TABLE IF NOT EXISTS prepare_header(pair_id TEXT PRIMARY KEY, generation TEXT NOT NULL UNIQUE,
                  metadata_json TEXT NOT NULL, digest TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS prepare_row(generation TEXT NOT NULL, kind TEXT NOT NULL, ordinal INTEGER NOT NULL,
                  lookup_key TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(generation, kind, ordinal));
                CREATE INDEX IF NOT EXISTS prepare_lookup ON prepare_row(generation, kind, lookup_key, ordinal);
                CREATE TABLE IF NOT EXISTS prepare_cursor(generation TEXT NOT NULL, root_id TEXT NOT NULL, cursor TEXT,
                  PRIMARY KEY(generation, root_id)) WITHOUT ROWID;


                CREATE TABLE IF NOT EXISTS choice_merge(notice_id TEXT PRIMARY KEY, content BLOB NOT NULL);
                CREATE TABLE IF NOT EXISTS choice_restore_receipt(pair_no INTEGER NOT NULL, action_id TEXT NOT NULL,
                  item_json TEXT NOT NULL, PRIMARY KEY(pair_no, action_id)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS choice_victim(ord INTEGER PRIMARY KEY, op_id TEXT NOT NULL, pair_no INTEGER NOT NULL,
                  state INTEGER NOT NULL, depth INTEGER NOT NULL, dir_path TEXT NOT NULL, name TEXT NOT NULL, kind INTEGER NOT NULL,
                  object_id TEXT NOT NULL, version_id TEXT, hash BLOB, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
                  flags INTEGER NOT NULL, identity_json TEXT);
                CREATE INDEX IF NOT EXISTS cv_op ON choice_victim(op_id, state, depth, ord);
                CREATE INDEX IF NOT EXISTS cv_item ON choice_victim(op_id, dir_path, name);
                CREATE INDEX IF NOT EXISTS cv_scan ON choice_victim(op_id, state, ord);


                CREATE TABLE IF NOT EXISTS removal_review(entity_key TEXT PRIMARY KEY, pair_id TEXT NOT NULL,
                  generation TEXT NOT NULL UNIQUE, metadata_json TEXT NOT NULL, digest TEXT NOT NULL);


                CREATE TABLE IF NOT EXISTS deletion_classification(root_no INTEGER PRIMARY KEY,
                  root_binding TEXT NOT NULL, basis_cursor TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS deletion_classification_path(root_no INTEGER NOT NULL,
                  path_key TEXT NOT NULL, path TEXT NOT NULL, exact INTEGER NOT NULL,
                  PRIMARY KEY(root_no,path_key)) WITHOUT ROWID;


                CREATE TABLE IF NOT EXISTS observation_checkpoint(root_no INTEGER PRIMARY KEY,
                  root_binding TEXT NOT NULL, scope_json TEXT NOT NULL, cursor TEXT NOT NULL);


                CREATE TABLE IF NOT EXISTS listing_checkpoint(root_id TEXT PRIMARY KEY, binding TEXT NOT NULL, mode TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS listing_page(root_id TEXT NOT NULL, request_key TEXT NOT NULL,
                  cursor TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(root_id,request_key));
                CREATE TABLE IF NOT EXISTS pair_pause(pair_id TEXT PRIMARY KEY);


                CREATE TABLE IF NOT EXISTS local_walk(root_id TEXT PRIMARY KEY, binding TEXT NOT NULL, cursor TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS local_walk_page(root_id TEXT NOT NULL, ordinal INTEGER NOT NULL,
                  payload TEXT NOT NULL, PRIMARY KEY(root_id,ordinal));
        PRAGMA user_version=22;
        """;

    private static void LegacyV22UpgradeChecks(string root)
    {
        var folder = Fresh(root, "upgrade-v22");
        var database = DbPath(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        // Entity identities, the committed cursor and the content hash are generated, never literal fixture constants.
        var cursor = "cursor-" + Guid.NewGuid().ToString("N");
        var objectId = "entity-" + Guid.NewGuid().ToString("N");
        var versionId = "revision-" + Guid.NewGuid().ToString("N");
        var gapObject = "entity-" + Guid.NewGuid().ToString("N");
        var actionId = "action-" + Guid.NewGuid().ToString("N");
        var recoveryId = "recovery-" + Guid.NewGuid().ToString("N");
        var openNotice = "notice-" + Guid.NewGuid().ToString("N");
        var closedNotice = "notice-" + Guid.NewGuid().ToString("N");
        var runId = "run-" + Guid.NewGuid().ToString("N");
        var epoch = "epoch-" + Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
        var pair = Pair("p1");
        var action = new SyncAction(actionId, 2, ActionKind.Delete, Side.B, "kept.txt", null, null, null, true, "k1");
        var retry = new SyncRetry("kept.txt", "sync", 1, "seeded", T0.Add(V2Settings.Default.AutoRetryDelay));
        var created = T0.ToUnixTimeMilliseconds();
        using (var db = new SqliteConnection("Data Source=" + database + ";Pooling=False"))
        {
            db.Open();
            StoreSchema.Execute(db, null, LegacyV22Schema);
            // Representative rows for every family the later migrations touch, plus baseline entities they must keep.
            StoreSchema.Execute(db, null, """
                INSERT INTO root(root_no,root_id,cursor) VALUES(1,$root,$cursor);
                INSERT INTO dir(dir_no,path,path_key) VALUES(1,'','');
                INSERT INTO pair(pair_no,pair_id,def_json,first_sync_done) VALUES(1,$pair,$def,1);
                INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,version_id,hash,size,mtime,flags)
                VALUES(1,1,'KEPT.TXT','kept.txt',0,$object,$version,$hash,4,0,0);
                INSERT INTO sync_point(pair_no,object_key,gen,kind,hash,size,a_dir,a_name,a_name_key,a_object_id,
                  a_version_id,a_mtime,b_dir,b_name,b_name_key,b_object_id,b_version_id,b_mtime)
                VALUES(1,'KEPT.TXT',1,0,$hash,4,1,'kept.txt','KEPT.TXT',$object,$version,0,
                  1,'kept.txt','KEPT.TXT',$object,$version,0);
                INSERT INTO pending_effect(action_id,pair_no,action_json,state,attempts,updated)
                VALUES($action,1,$json,'uncertain',1,$created);
                INSERT INTO recovery(recovery_id,pair_no,action_id,side,path,path_key,location,version_id,hash,size,
                  created,expires)
                VALUES($recovery,1,$action,'B','kept.txt',NULL,'memory://retained/kept',$version,$hashText,4,$created,$expires);
                INSERT INTO notice(notice_id,pair_no,rule,kind,path,path_key,detail,reason_key,state,choice,created,
                  restore_hold)
                VALUES($openNotice,1,2,'Info','kept.txt',$key,'Seeded v22 notice','sync-retry','open',NULL,$created,0);
                INSERT INTO notice(notice_id,pair_no,rule,kind,path,path_key,detail,reason_key,state,choice,created,
                  restore_hold)
                VALUES($closedNotice,1,2,'Info','kept.txt',$key,'Seeded closed notice','sync-retry','closed',
                  'acknowledged',$created,0);
                INSERT INTO sync_retry(pair_no,path_key,json,retry_after) VALUES(1,'KEPT.TXT',$retryJson,$created);
                INSERT INTO local_gap(root_no,epoch,journal_cursor,boundary,trust_usn,after_object,discovery_done,
                  next_result,inventory_pending,worker_after,worker_done,reason)
                VALUES(1,$epoch,$cursor,0,0,'',0,0,0,'',0,'seeded gap');
                INSERT INTO local_gap_item(root_no,object_id,epoch,expected_json,hash,proof_json,result_seq,
                  last_attempt,metadata_ready,metadata_stamp,failure_count,failure_reason,retry_allowed,defer_count,
                  defer_reason,discovery_reason)
                VALUES(1,$gapObject,$epoch,'{}',NULL,NULL,0,0,1,'stamp',0,NULL,1,0,NULL,'seeded');
                INSERT INTO planning_deferred(root_no,object_id,pair_no,path,version,reason)
                VALUES(1,$gapObject,1,'deferred.txt',1,'seeded reason');
                INSERT INTO run_summary(run_id,pair_id,started,completed,outcome,done,uploaded,downloaded,deleted,
                  moved,kept_both,held,failed,uncertain,skipped,deferred)
                VALUES($run,'p1',$created,$created,'complete',1,0,0,0,0,0,0,0,0,0,0);
                INSERT INTO local_rehash(root_no,object_id,last_attempt,reason)
                VALUES(1,$gapObject,$rehashStamp,'seeded obligation');
                """,
                ("$root", pair.A.RootId), ("$cursor", cursor), ("$pair", "p1"),
                ("$def", JsonSerializer.Serialize(pair, RowCodec.Json)), ("$object", objectId),
                ("$version", versionId), ("$hashText", hash), ("$hash", Convert.FromHexString(hash)), ("$action", actionId),
                ("$json", JsonSerializer.Serialize(action, RowCodec.Json)), ("$recovery", recoveryId),
                ("$openNotice", openNotice), ("$closedNotice", closedNotice),
                ("$key", PathKey.Of("kept.txt")), ("$created", created),
                ("$expires", T0.Add(V2Settings.Default.RecoveryRetention).ToUnixTimeMilliseconds()),
                ("$retryJson", JsonSerializer.Serialize(retry, RowCodec.Json)),
                ("$epoch", epoch), ("$gapObject", gapObject), ("$run", runId), ("$rehashStamp", 1234L));
        }

        for (var reopen = 0; reopen < 2; reopen++)
        {
            using (var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash))
            {
                Require(store.Cursor(pair.A.RootId) == cursor, "the v22 committed cursor survives the upgrade exactly");
                Require(store.FirstSyncDone("p1"), "the v22 first-sync completion survives the upgrade");
                Require(store.Pairs().Single().PairId == "p1", "the v22 pair definition survives the upgrade");
                var retained = store.ByObject(pair.A.RootId, objectId)!;
                Require(retained.Hash == hash && retained.Size == 4 && retained.Path == "kept.txt",
                    "the v22 entity row survives with its exact content identity");
                Require(store.UnresolvedIntents("p1").Single().ActionId == actionId,
                    "the v22 unresolved intent survives with its action");
                var recovery = store.Recoveries("p1").Single();
                Require(recovery.ActionId == actionId && recovery.Hash == hash &&
                    recovery.Location == "memory://retained/kept",
                    "the v22 recovery receipt survives the upgrade");
                Require(store.OpenNotices("p1").Any(notice => notice.NoticeId == openNotice &&
                    notice.Detail == "Seeded v22 notice"), "the v22 open notice survives the upgrade");
                Require(store.ReadSyncRetries("p1").Count == 1, "the v22 retry debt survives the upgrade");
            }
            using (var db = new SqliteConnection("Data Source=" + database + ";Pooling=False"))
            {
                db.Open();
                Require((long)StoreSchema.Scalar(db, null, "PRAGMA user_version;")! == StoreSchema.Version,
                    "opening a real v22 store stamps the current schema version");
                Require((long)StoreSchema.Scalar(db, null,
                    "SELECT intent_no FROM pending_effect WHERE action_id=$a;", ("$a", actionId))! == 1,
                    "the v24 intent order preserves the v22 insertion rowid");
                Require(StoreSchema.Scalar(db, null,
                    "SELECT closed_at FROM notice WHERE notice_id=$n;", ("$n", closedNotice)) is long &&
                    StoreSchema.Scalar(db, null,
                    "SELECT closed_at FROM notice WHERE notice_id=$n;", ("$n", openNotice)) is null or DBNull,
                    "only the v22 closed notice gains a terminal closure instant");
                Require((string)StoreSchema.Scalar(db, null,
                    "SELECT path_key FROM recovery WHERE recovery_id=$r;", ("$r", recoveryId))! ==
                    PathKey.Of("kept.txt"), "the open repair fills the v22 recovery path key");
                Require((long)StoreSchema.Scalar(db, null,
                    "SELECT evidence_revision FROM local_rehash WHERE object_id=$o;", ("$o", gapObject))! == 1234,
                    "the v27 evidence revision is seeded from the v22 rehash attempt");
                Require((string)StoreSchema.Scalar(db, null,
                    "SELECT reason FROM planning_deferred WHERE object_id=$o;", ("$o", gapObject))! == "seeded reason" &&
                    (long)StoreSchema.Scalar(db, null,
                    "SELECT alias_dir FROM planning_deferred WHERE object_id=$o;", ("$o", gapObject))! == -1,
                    "the v22 deferred-planning row keeps its fields and gains the v26 alias default");
                Require((long)StoreSchema.Scalar(db, null,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN " +
                    "('planning_deferred_pair_version','planning_deferred_root_version');")! == 2,
                    "the v25 version indexes are added around the preserved deferred row");
                Require((string)StoreSchema.Scalar(db, null,
                    "SELECT path FROM notice WHERE notice_id=$n;", ("$n", closedNotice))! == "kept.txt" &&
                    (string)StoreSchema.Scalar(db, null,
                    "SELECT reason FROM local_gap;")! == "seeded gap",
                    "the v22 entity rows keep their exact values");
                Require((long)StoreSchema.Scalar(db, null,
                    "SELECT COUNT(*) FROM sync_point WHERE object_key='KEPT.TXT' AND hash=$hash AND size=4;",
                    ("$hash", Convert.FromHexString(hash)))! == 1,
                    "the v22 baseline content identity survives both opens");
                Require((long)StoreSchema.Scalar(db, null, "SELECT COUNT(*) FROM run_summary;")! == 1,
                    "the v22 run summary survives the upgrade");
                Require((long)StoreSchema.Scalar(db, null,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='nt_terminal';")! == 1,
                    "the v23 terminal notice index is added by the upgrade");
            }
        }
        Console.WriteLine("PASS v22 fixture upgrade preserves rows and stamps the current version");
    }
}
