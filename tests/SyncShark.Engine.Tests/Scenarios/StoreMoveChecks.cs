using Microsoft.Data.Sqlite;
using Massdex.SyncShark.Engine.Storage;
namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>Builds the release schema from its creation DDL, using only synthetic data.</summary>
public static class StoreMoveChecks
{
    public static async Task CreateFixtureAsync()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        connection.CreateFunction<string, string>("pathkey", PathNames.Key, isDeterministic: true);
        connection.CreateFunction<string, int>("plandepth", PathNames.Depth, isDeterministic: true);
        connection.CreateFunction<string, int>("localaccessretrycause", _ => 0, isDeterministic: true);
        using var command = connection.CreateCommand();
        command.CommandText = ReleaseSchema;
        await command.ExecuteNonQueryAsync();
        command.CommandText = "PRAGMA user_version";
        Check.Require(Convert.ToInt32(await command.ExecuteScalarAsync()) == 16, "Fixture version");
        command.CommandText = "SELECT count(*) FROM pair";
        Check.Require(Convert.ToInt64(await command.ExecuteScalarAsync()) == 0, "Fixture is not empty");
    }

    private const string ReleaseSchema = """
        CREATE TABLE IF NOT EXISTS pair(id INTEGER PRIMARY KEY, pair_id TEXT NOT NULL UNIQUE, def_json TEXT NOT NULL, pending_json TEXT,
          last_repair_json TEXT, first_repair_done INTEGER NOT NULL DEFAULT 0) STRICT;
        CREATE TABLE IF NOT EXISTS account(connection_id TEXT PRIMARY KEY, limiter_level INTEGER NOT NULL) STRICT;
        CREATE TABLE IF NOT EXISTS synced_entry(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          local_path TEXT NOT NULL, dropbox_key_delta TEXT, dropbox_path_delta TEXT,
          dropbox_key TEXT GENERATED ALWAYS AS (coalesce(dropbox_key_delta, pathkey(local_path))) VIRTUAL,
          dropbox_path TEXT GENERATED ALWAYS AS (coalesce(dropbox_path_delta, local_path)) VIRTUAL,
          is_folder INTEGER NOT NULL, local_id ANY NOT NULL, local_size INTEGER NOT NULL, local_mtime INTEGER NOT NULL,
          dropbox_id TEXT NOT NULL, dropbox_hash ANY NOT NULL) STRICT;
        CREATE UNIQUE INDEX IF NOT EXISTS synced_local_key ON synced_entry(pair_id, pathkey(local_path));
        CREATE UNIQUE INDEX IF NOT EXISTS synced_dropbox_key ON synced_entry(pair_id, dropbox_key_delta) WHERE dropbox_key_delta IS NOT NULL;
        CREATE INDEX IF NOT EXISTS synced_local_id ON synced_entry(pair_id, local_id);
        CREATE INDEX IF NOT EXISTS synced_dropbox_id ON synced_entry(pair_id, dropbox_id);
        CREATE TABLE IF NOT EXISTS issue(issue_id INTEGER PRIMARY KEY, pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          kind INTEGER NOT NULL, cause TEXT NOT NULL, path TEXT NOT NULL, subject_json TEXT NOT NULL,
          at_ticks INTEGER NOT NULL, dismissed INTEGER NOT NULL DEFAULT 0,
          UNIQUE(pair_id, path, kind)) STRICT;
        ALTER TABLE pair ADD COLUMN execution_generation INTEGER NOT NULL DEFAULT 0 CHECK(execution_generation>=0);
        ALTER TABLE pair ADD COLUMN retry_attempts INTEGER CHECK(retry_attempts IS NULL OR retry_attempts BETWEEN 0 AND 2147483647);
        ALTER TABLE pair ADD COLUMN retry_after_ticks INTEGER CHECK((retry_attempts IS NULL AND retry_after_ticks IS NULL) OR (retry_attempts IS NOT NULL AND retry_after_ticks IS NOT NULL AND retry_after_ticks BETWEEN 0 AND 3155378975999999999));
        CREATE INDEX pair_retry_due ON pair(retry_after_ticks,id) WHERE retry_after_ticks IS NOT NULL;
        CREATE TABLE journal_position(
         volume_key TEXT PRIMARY KEY NOT NULL CHECK(length(volume_key)>0),
         journal_id INTEGER NOT NULL, next_usn INTEGER NOT NULL
        ) STRICT;
        CREATE TABLE source_state(
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         side INTEGER NOT NULL CHECK(side IN (0,1)),
         source_epoch INTEGER NOT NULL CHECK(source_epoch>=0),
         provenance_json TEXT CHECK(provenance_json IS NULL OR (json_valid(provenance_json) AND coalesce(json_extract(provenance_json,'$.Version'),0)=1)),
         cursor TEXT CHECK(cursor IS NULL OR length(cursor)>0),
         repair_reason TEXT CHECK(repair_reason IS NULL OR length(repair_reason)>0),
         repair_revision INTEGER NOT NULL CHECK(repair_revision>=0),
         PRIMARY KEY(pair_id,side),
         CHECK(side=1 OR cursor IS NULL),
         CHECK(cursor IS NULL OR provenance_json IS NOT NULL)
        ) STRICT;
        CREATE TABLE change_work(
         work_id INTEGER PRIMARY KEY AUTOINCREMENT,
         work_origin TEXT NOT NULL DEFAULT 'Acquired' CHECK(work_origin IN ('Acquired','Issue')),
         issue_work_key TEXT,
         dispatch_kind TEXT NOT NULL DEFAULT 'OrdinaryItem' CHECK(dispatch_kind IN ('OrdinaryItem','LocalBranch','LocalRecovery')),
         pending_operation_id TEXT,
         pending_generation INTEGER CHECK(pending_generation IS NULL OR pending_generation>=0),
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         pair_generation INTEGER NOT NULL CHECK(pair_generation>=0),
         source_epoch INTEGER NOT NULL CHECK(source_epoch>=0),
         side INTEGER NOT NULL CHECK(side IN (0,1)),
         domain TEXT NOT NULL CHECK(length(domain)>0),
         path_key TEXT NOT NULL, old_path_key TEXT,
         identity ANY CHECK(identity IS NULL OR typeof(identity) IN ('integer','text')),
         notice_json TEXT CHECK(notice_json IS NULL OR (json_valid(notice_json) AND coalesce(json_extract(notice_json,'$.Version'),0)=1)),
         before_json TEXT CHECK(before_json IS NULL OR (json_valid(before_json) AND coalesce(json_extract(before_json,'$.Version'),0)=1)),
         acquisition_work_id INTEGER CHECK(acquisition_work_id IS NULL OR (acquisition_work_id>0 AND acquisition_work_id<=work_id)),
         parent_anchor INTEGER, obligation_key TEXT,
         CHECK(work_id>0),
         CHECK((work_origin='Acquired' AND notice_json IS NOT NULL AND acquisition_work_id IS NOT NULL) OR
               (work_origin='Issue' AND notice_json IS NULL AND acquisition_work_id IS NULL AND parent_anchor IS NULL
                AND obligation_key IS NULL AND issue_work_key IS NOT NULL AND length(issue_work_key)>0)),
         CHECK((dispatch_kind='LocalRecovery' AND pending_operation_id IS NOT NULL AND length(pending_operation_id)>0 AND pending_generation IS NOT NULL) OR
               (dispatch_kind<>'LocalRecovery' AND pending_operation_id IS NULL AND pending_generation IS NULL)),
         CHECK((parent_anchor IS NULL AND obligation_key IS NULL) OR
               (parent_anchor IS NOT NULL AND parent_anchor>0 AND parent_anchor<work_id AND obligation_key IS NOT NULL AND length(obligation_key)>0)),
         UNIQUE(parent_anchor,obligation_key)
        ) STRICT;
        CREATE INDEX work_pair_order ON change_work(pair_id,pair_generation,work_id);
        CREATE INDEX work_source_order ON change_work(pair_id,pair_generation,side,source_epoch,work_id) WHERE work_origin='Acquired';
        CREATE INDEX work_identity ON change_work(pair_id,pair_generation,side,source_epoch,domain,identity,work_id);
        CREATE INDEX work_cross_identity ON change_work(side,domain,identity,pair_id,work_id);
        CREATE INDEX work_path ON change_work(pair_id,pair_generation,side,domain,path_key,work_id);
        CREATE INDEX work_old_path ON change_work(pair_id,pair_generation,side,domain,old_path_key,work_id) WHERE old_path_key IS NOT NULL;
        CREATE TABLE issue_member(
         member_id INTEGER PRIMARY KEY,
         issue_id INTEGER REFERENCES issue(issue_id) ON DELETE SET NULL,
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         work_id INTEGER REFERENCES change_work(work_id) ON DELETE CASCADE,
         side INTEGER NOT NULL CHECK(side IN (0,1)), path_key TEXT NOT NULL,
         identity ANY CHECK(identity IS NULL OR typeof(identity) IN ('integer','text')),
         subject_json TEXT NOT NULL CHECK(json_valid(subject_json) AND coalesce(json_extract(subject_json,'$.Version'),0)=1),
         skip_cause TEXT,
         CHECK((work_id IS NOT NULL AND skip_cause IS NULL) OR
               (work_id IS NULL AND issue_id IS NOT NULL AND skip_cause IS NOT NULL AND length(skip_cause)>0))
        ) STRICT;
        CREATE UNIQUE INDEX issue_member_work ON issue_member(work_id) WHERE work_id IS NOT NULL;
        CREATE INDEX issue_member_issue ON issue_member(issue_id,side,path_key);
        CREATE UNIQUE INDEX issue_member_skip ON issue_member(pair_id,side,path_key) WHERE work_id IS NULL;
        CREATE TABLE folder_removal(
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         operation_id TEXT NOT NULL CHECK(length(operation_id)>0),
         generation INTEGER NOT NULL CHECK(generation>=0),
         phase TEXT NOT NULL CHECK(phase IN ('Prepared','Observed','SubmitArmed','Submitted','Terminal','Checking','Recovering','Complete','Unknown','Retained')),
         deleting_side INTEGER NOT NULL CHECK(deleting_side IN (0,1)),
         root_entry_json TEXT NOT NULL CHECK(json_valid(root_entry_json) AND coalesce(json_extract(root_entry_json,'$.Version'),0)=1),
         source_evidence_json TEXT NOT NULL CHECK(json_valid(source_evidence_json) AND coalesce(json_extract(source_evidence_json,'$.Version'),0)=1),
         saved_root_json TEXT CHECK(saved_root_json IS NULL OR (json_valid(saved_root_json) AND coalesce(json_extract(saved_root_json,'$.Version'),0)=1)),
         root_context_json TEXT NOT NULL CHECK(json_valid(root_context_json) AND coalesce(json_extract(root_context_json,'$.Version'),0)=1),
         cursor TEXT CHECK(cursor IS NULL OR length(cursor)>0),
         active INTEGER NOT NULL CHECK(active IN (0,1)), cause TEXT,
         PRIMARY KEY(pair_id,operation_id), CHECK(phase<>'Complete' OR active=0)
        ) STRICT;
        CREATE INDEX folder_removal_active ON folder_removal(pair_id,active,operation_id);CREATE INDEX folder_removal_root ON folder_removal(pair_id,json_extract(root_entry_json,'$.Value.Path'),operation_id) WHERE active=1;CREATE TABLE folder_removal_submission(
         submission_id TEXT PRIMARY KEY NOT NULL CHECK(length(submission_id)>0),
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         generation INTEGER NOT NULL CHECK(generation>=0),
         phase TEXT NOT NULL CHECK(phase IN ('Armed','Accepted','Terminal','Unknown')),
         job_id TEXT CHECK(job_id IS NULL OR length(job_id)>0), cause TEXT,
         CHECK(phase<>'Accepted' OR job_id IS NOT NULL),
         CHECK(phase<>'Armed' OR job_id IS NULL)
        ) STRICT;CREATE INDEX folder_submission_pending ON folder_removal_submission(pair_id,submission_id) WHERE phase='Accepted';CREATE TABLE folder_removal_item(
         pair_id INTEGER NOT NULL, operation_id TEXT NOT NULL,
         ordinal INTEGER NOT NULL CHECK(ordinal>=0),
         generation INTEGER NOT NULL CHECK(generation>=0),
         provenance TEXT NOT NULL CHECK(provenance IN ('Planned','Unexpected','BlockingParent')),
         path TEXT NOT NULL, policy_key TEXT NOT NULL,
         native_key TEXT GENERATED ALWAYS AS (pathkey(path)) VIRTUAL,
         present INTEGER GENERATED ALWAYS AS (expected_json IS NOT NULL) VIRTUAL,
         expected_json TEXT CHECK(expected_json IS NULL OR (json_valid(expected_json) AND coalesce(json_extract(expected_json,'$.Version'),0)=1)),
         saved_json TEXT CHECK(saved_json IS NULL OR (json_valid(saved_json) AND coalesce(json_extract(saved_json,'$.Version'),0)=1)),
         verdict TEXT NOT NULL CHECK(verdict IN ('Survives','Removed')),
         follow_up INTEGER NOT NULL CHECK(follow_up IN (0,1)),
         follow_up_state TEXT NOT NULL CHECK(follow_up_state IN ('None','Pending','Done')),
         selected_branch INTEGER NOT NULL CHECK(selected_branch IN (0,1)),
         effect_kind TEXT NOT NULL CHECK(effect_kind IN ('None','RecursiveFolder','ConditionalFile','StateOnly')),
         effect_state TEXT NOT NULL CHECK(effect_state IN ('None','Ready','Armed','Accepted','Verified','NoEffect','Unknown','Settled')),
         effect_generation INTEGER NOT NULL CHECK(effect_generation>=0),
         submission_id TEXT REFERENCES folder_removal_submission(submission_id) ON DELETE NO ACTION,
         submission_member INTEGER CHECK(submission_member IS NULL OR submission_member>=0),
         effect_result_json TEXT CHECK(effect_result_json IS NULL OR (json_valid(effect_result_json) AND coalesce(json_extract(effect_result_json,'$.Version'),0)=1)),
         phase TEXT NOT NULL CHECK(phase IN ('Planned','DeleteArmed','Deleted','RecoverySelected','AllocateArmed','StageArmed','Staged','PublishArmed','Published','Conflict','Unknown','DiscardArmed','Settled')),
         selected_json TEXT CHECK(selected_json IS NULL OR (json_valid(selected_json) AND coalesce(json_extract(selected_json,'$.Version'),0)=1)),
         stage_allocation_json TEXT CHECK(stage_allocation_json IS NULL OR (json_valid(stage_allocation_json) AND coalesce(json_extract(stage_allocation_json,'$.Version'),0)=1)),
         stage_json TEXT CHECK(stage_json IS NULL OR (json_valid(stage_json) AND coalesce(json_extract(stage_json,'$.Version'),0)=1)),
         result_json TEXT CHECK(result_json IS NULL OR (json_valid(result_json) AND coalesce(json_extract(result_json,'$.Version'),0)=1)),
         cause TEXT,
         PRIMARY KEY(pair_id,operation_id,ordinal),
         FOREIGN KEY(pair_id,operation_id) REFERENCES folder_removal(pair_id,operation_id) ON DELETE CASCADE,
         CHECK((follow_up=0 AND follow_up_state='None') OR (follow_up=1 AND follow_up_state IN ('Pending','Done'))),
         CHECK((effect_kind='None' AND effect_state='None') OR (effect_kind<>'None' AND effect_state<>'None')),
         CHECK((submission_id IS NULL AND submission_member IS NULL) OR
               (submission_id IS NOT NULL AND submission_member IS NOT NULL AND effect_kind IN ('RecursiveFolder','ConditionalFile') AND effect_state IN ('Armed','Accepted','Verified','NoEffect','Unknown','Settled'))),
         CHECK(effect_state NOT IN ('Armed','Accepted') OR submission_id IS NOT NULL),
         CHECK(effect_kind NOT IN ('RecursiveFolder','ConditionalFile') OR (expected_json IS NOT NULL AND verdict='Removed')),
         CHECK(phase NOT IN ('RecoverySelected','AllocateArmed','StageArmed','Staged','PublishArmed','Published','DiscardArmed') OR selected_json IS NOT NULL),
         CHECK(phase NOT IN ('AllocateArmed','StageArmed','Staged','PublishArmed','Published','DiscardArmed') OR stage_allocation_json IS NOT NULL),
         CHECK(phase NOT IN ('StageArmed','Staged','PublishArmed','Published','DiscardArmed') OR stage_json IS NOT NULL),
         CHECK(phase<>'Published' OR result_json IS NOT NULL)
        ) STRICT;
        CREATE UNIQUE INDEX folder_item_identity ON folder_removal_item(pair_id,operation_id,native_key,provenance,present);
        CREATE INDEX folder_item_followup ON folder_removal_item(pair_id,operation_id,follow_up_state,ordinal);
        CREATE INDEX folder_item_effect ON folder_removal_item(pair_id,operation_id,effect_state,ordinal);
        CREATE UNIQUE INDEX folder_submission_member ON folder_removal_item(submission_id,submission_member) WHERE submission_id IS NOT NULL;
        CREATE INDEX folder_item_ready_order ON folder_removal_item(pair_id,operation_id,-plandepth(policy_key),policy_key,native_key,present,ordinal) WHERE effect_state='Ready';

        CREATE UNIQUE INDEX work_issue_registration ON change_work(pair_id,issue_work_key) WHERE issue_work_key IS NOT NULL;
        ALTER TABLE issue ADD COLUMN item_retry_episode TEXT;
        ALTER TABLE issue ADD COLUMN item_retry_revision INTEGER CHECK(item_retry_revision IS NULL OR item_retry_revision>=0);
        ALTER TABLE issue ADD COLUMN item_retry_work_id INTEGER REFERENCES change_work(work_id) ON DELETE NO ACTION
         CHECK((item_retry_episode IS NULL AND item_retry_revision IS NULL AND item_retry_work_id IS NULL) OR
               (item_retry_episode IS NOT NULL AND length(item_retry_episode)>0
                AND item_retry_revision IS NOT NULL AND item_retry_work_id IS NOT NULL
                AND kind=0 AND localaccessretrycause(cause)=1));
        CREATE UNIQUE INDEX issue_item_retry_work ON issue(item_retry_work_id) WHERE item_retry_work_id IS NOT NULL;
        CREATE INDEX issue_member_retry_subject ON issue_member(pair_id,side,path_key,identity) WHERE work_id IS NOT NULL;
        ALTER TABLE folder_removal ADD COLUMN retirement_disposition TEXT
         CHECK(retirement_disposition IS NULL OR (retirement_disposition IN ('Acknowledged','NoEffect') AND active=0 AND phase IN ('Unknown','Retained')));
        ALTER TABLE pair ADD COLUMN deleted_history_cursor TEXT CHECK(deleted_history_cursor IS NULL OR length(deleted_history_cursor)>0);
        CREATE TABLE discovery_deleted_name(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          path_key TEXT NOT NULL, PRIMARY KEY(pair_id,path_key)) STRICT, WITHOUT ROWID;
        CREATE TRIGGER invalidate_deleted_history AFTER UPDATE OF def_json,execution_generation,first_repair_done ON pair
        WHEN json_remove(new.def_json,'$.Drive')<>json_remove(old.def_json,'$.Drive') OR new.execution_generation<>old.execution_generation OR (new.first_repair_done=1 AND old.first_repair_done=0)
        BEGIN
          UPDATE pair SET deleted_history_cursor=NULL WHERE id=new.id;
        END;
        CREATE TABLE issue_new(issue_id INTEGER PRIMARY KEY AUTOINCREMENT,
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         kind INTEGER NOT NULL, cause TEXT NOT NULL, path TEXT NOT NULL, subject_json TEXT NOT NULL,
         at_ticks INTEGER NOT NULL, dismissed INTEGER NOT NULL DEFAULT 0,
         item_retry_episode TEXT,
         item_retry_revision INTEGER CHECK(item_retry_revision IS NULL OR item_retry_revision>=0),
         item_retry_work_id INTEGER REFERENCES change_work(work_id) ON DELETE NO ACTION
          CHECK((item_retry_episode IS NULL AND item_retry_revision IS NULL AND item_retry_work_id IS NULL) OR
                (item_retry_episode IS NOT NULL AND length(item_retry_episode)>0
                 AND item_retry_revision IS NOT NULL AND item_retry_work_id IS NOT NULL
                 AND kind=0 AND localaccessretrycause(cause)=1)),
         ignored INTEGER NOT NULL DEFAULT 0 CHECK(ignored IN (0,1)),
         UNIQUE(pair_id,path,kind)) STRICT;
        INSERT INTO issue_new(issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed,
         item_retry_episode,item_retry_revision,item_retry_work_id)
         SELECT issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed,
          item_retry_episode,item_retry_revision,item_retry_work_id FROM issue;
        DROP TABLE issue;
        ALTER TABLE issue_new RENAME TO issue;
        DELETE FROM sqlite_sequence WHERE name='issue' AND seq=0;
        CREATE UNIQUE INDEX issue_item_retry_work ON issue(item_retry_work_id) WHERE item_retry_work_id IS NOT NULL;
        ALTER TABLE pair ADD COLUMN deleted_history_owed INTEGER NOT NULL DEFAULT 1 CHECK(deleted_history_owed IN (0,1));
        CREATE TABLE discovery_deleted_block(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          last_key TEXT NOT NULL, name_count INTEGER NOT NULL CHECK(name_count BETWEEN 1 AND 5000),
          names BLOB NOT NULL, PRIMARY KEY(pair_id,last_key)) STRICT;
        CREATE TABLE discovery_deleted_delta(pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          path_key TEXT NOT NULL, PRIMARY KEY(pair_id,path_key)) STRICT, WITHOUT ROWID;
        DROP TRIGGER invalidate_deleted_history;
        CREATE TRIGGER invalidate_deleted_history AFTER UPDATE OF def_json,execution_generation ON pair
        WHEN json_remove(new.def_json,'$.Drive')<>json_remove(old.def_json,'$.Drive') OR new.execution_generation<>old.execution_generation
        BEGIN
          UPDATE pair SET deleted_history_cursor=NULL,deleted_history_owed=1 WHERE id=new.id;
          DELETE FROM discovery_deleted_delta WHERE pair_id=new.id;
        END;
        UPDATE source_state SET repair_reason=coalesce(repair_reason,'NormalRepair'),repair_revision=repair_revision+1;
        DROP TABLE discovery_deleted_name;
        ALTER TABLE discovery_deleted_delta ADD COLUMN spent INTEGER NOT NULL DEFAULT 0 CHECK(spent IN (0,1));
        DROP TRIGGER invalidate_deleted_history;
        CREATE TRIGGER invalidate_deleted_history AFTER UPDATE OF def_json,execution_generation ON pair
        WHEN json_remove(new.def_json,'$.Drive')<>json_remove(old.def_json,'$.Drive') OR new.execution_generation<>old.execution_generation
        BEGIN
          UPDATE pair SET deleted_history_cursor=NULL,deleted_history_owed=1 WHERE id=new.id;
          DELETE FROM discovery_deleted_delta WHERE pair_id=new.id AND spent=0;
        END;
        ALTER TABLE synced_entry ADD COLUMN local_published_journal INTEGER;ALTER TABLE synced_entry ADD COLUMN local_published_usn INTEGER;
        ALTER TABLE change_work ADD COLUMN discovery_required INTEGER NOT NULL DEFAULT 0 CHECK(discovery_required IN (0,1));
        CREATE INDEX issue_path_key ON issue(pair_id, pathkey(path), kind);
        ALTER TABLE change_work ADD COLUMN rename_continuation_json TEXT CHECK(rename_continuation_json IS NULL OR (json_valid(rename_continuation_json) AND coalesce(json_extract(rename_continuation_json,'$.Version'),0)=1)); ALTER TABLE folder_removal ADD COLUMN rename_work_id INTEGER REFERENCES change_work(work_id) ON DELETE RESTRICT; CREATE INDEX folder_rename_work ON folder_removal(rename_work_id) WHERE rename_work_id IS NOT NULL;
        CREATE TABLE binding_repair(
         pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
         side INTEGER NOT NULL CHECK(side IN (0,1)),
         identity ANY NOT NULL CHECK(typeof(identity) IN ('integer','text')),
         other_first ANY NOT NULL, other_second ANY NOT NULL,
         issue_id INTEGER,
         CHECK(other_first < other_second),
         PRIMARY KEY(pair_id,side,identity)) STRICT;
        ALTER TABLE folder_removal ADD COLUMN authority TEXT NOT NULL DEFAULT 'Legacy'
         CHECK(authority IN ('Inferred','Chosen','Excluded','Vacate','Repair','Legacy'));
        ALTER TABLE folder_removal ADD COLUMN pre_restore_id TEXT CHECK(pre_restore_id IS NULL OR length(pre_restore_id)>0);CREATE INDEX folder_removal_repair ON folder_removal(pair_id,deleting_side,json_extract(saved_root_json,'$.Value.LocalId'),json_extract(saved_root_json,'$.Value.DropboxId'),operation_id) WHERE authority='Repair';CREATE INDEX folder_removal_unresolved ON folder_removal(pair_id,operation_id) WHERE active=1 OR (phase IN ('Unknown','Retained') AND retirement_disposition IS NULL);
        CREATE TABLE folder_move(
          pair_id INTEGER NOT NULL REFERENCES pair(id) ON DELETE CASCADE,
          source_id TEXT NOT NULL, root_id TEXT NOT NULL, parent_id TEXT,
          source_path TEXT NOT NULL, destination_path TEXT NOT NULL, destination_id TEXT,
          phase TEXT NOT NULL CHECK(phase IN ('Armed','Filling','Filled','Relabelled')),
          launch TEXT, job_id TEXT, generation INTEGER NOT NULL,
          CHECK(job_id IS NULL OR launch IS NOT NULL),
          CHECK(phase='Armed' OR destination_id IS NOT NULL),
          PRIMARY KEY(pair_id, source_id)) STRICT;
        CREATE INDEX folder_move_root ON folder_move(pair_id, root_id);
        PRAGMA user_version=16;
        """;
}
