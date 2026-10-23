// Exact no-command migration shapes measured from the retained v21 -> v27 compatibility fixture.
// Approved core changes: recovery/gap, NOTICE23 terminal/aggregated indexes, INDEX24 retry state,
// deferred-plan aliases and intent ordering, VERIFY27 evidence ownership. No FoD objects are allowed.
internal static partial class FilesOnDemandCompatibilityChecks
{
    private static string NormalizeSchema(string text) => System.Text.RegularExpressions.Regex.Replace(text,
        """'(?:''|[^'])*'|"(?:""|[^"])*"|\s+""",
        match => char.IsWhiteSpace(match.Value[0]) ? " " : match.Value).Trim();

    private static readonly IReadOnlyDictionary<string, string> ApprovedCoreShapes = new Dictionary<string, string>
    {
        // F4 / NOTICE3: indexed pair/reason/path notice probes replace per-item full-population reads.
        ["index|nt_pair_reason_path"] = """CREATE INDEX nt_pair_reason_path ON notice(pair_no,reason_key,path_key)""",
        ["index|nt_groupable_delete"] = """CREATE INDEX nt_groupable_delete ON notice(pair_no,reason_key,notice_id) WHERE state IN ('open','aggregated')""",
        ["index|nt_restore_hold_path"] = """CREATE INDEX nt_restore_hold_path ON notice(pair_no, path_key) WHERE state IN ('open','aggregated') AND reason_key='recovery-restore-pending' AND restore_hold=1""",
        ["index|nt_restore_page"] = """CREATE INDEX nt_restore_page ON notice(pair_no, notice_id) WHERE state IN ('open','aggregated') AND (reason_key='recovery-restore-pending' OR reason_key='recovery-restore-ready')""",
        ["index|nt_restore_path"] = """CREATE INDEX nt_restore_path ON notice(pair_no, path_key) WHERE state IN ('open','aggregated') AND reason_key='recovery-restore-pending'""",
        ["index|nt_restore_path_ready"] = """CREATE INDEX nt_restore_path_ready ON notice(pair_no, path_key) WHERE reason_key='recovery-restore-ready'""",
        ["index|nt_restore_reason_page"] = """CREATE INDEX nt_restore_reason_page ON notice(notice_id) WHERE reason_key='recovery-restore-pending' OR reason_key='recovery-restore-ready'""",
        ["index|nt_terminal"] = """CREATE INDEX nt_terminal ON notice(closed_at,notice_id) WHERE state='closed' AND restore_hold=0""",
        ["index|planning_deferred_pair"] = """CREATE INDEX planning_deferred_pair ON planning_deferred(pair_no,root_no,object_id)""",
        ["index|planning_deferred_pair_version"] = """CREATE INDEX planning_deferred_pair_version ON planning_deferred(pair_no,version,root_no,object_id)""",
        ["index|planning_deferred_root_version"] = """CREATE INDEX planning_deferred_root_version ON planning_deferred(root_no,version)""",
        ["index|rc_page"] = """CREATE INDEX rc_page ON recovery(pair_no, recovery_id)""",
        ["index|rc_path"] = """CREATE INDEX rc_path ON recovery(pair_no, path_key, created DESC, recovery_id DESC)""",
        ["index|rc_path_missing"] = """CREATE INDEX rc_path_missing ON recovery(recovery_id) WHERE path_key IS NULL""",
        ["table|local_gap"] = """CREATE TABLE local_gap(root_no INTEGER PRIMARY KEY, epoch TEXT NOT NULL, journal_cursor TEXT NOT NULL, boundary INTEGER NOT NULL, trust_usn INTEGER NOT NULL, after_object TEXT NOT NULL DEFAULT '', discovery_done INTEGER NOT NULL DEFAULT 0, next_result INTEGER NOT NULL DEFAULT 0, worker_after TEXT NOT NULL DEFAULT '', worker_done INTEGER NOT NULL DEFAULT 0, reason TEXT NOT NULL DEFAULT 'journal history discontinuity', inventory_pending INTEGER NOT NULL DEFAULT 0)""",
        ["table|local_gap_item"] = """CREATE TABLE local_gap_item(root_no INTEGER NOT NULL, object_id TEXT NOT NULL, epoch TEXT NOT NULL, expected_json TEXT NOT NULL, hash TEXT, proof_json TEXT, result_seq INTEGER NOT NULL DEFAULT 0, last_attempt INTEGER NOT NULL DEFAULT 0, metadata_ready INTEGER NOT NULL DEFAULT 0, metadata_stamp TEXT, failure_count INTEGER NOT NULL DEFAULT 0, failure_reason TEXT, retry_allowed INTEGER NOT NULL DEFAULT 1, defer_count INTEGER NOT NULL DEFAULT 0, defer_reason TEXT, discovery_reason TEXT, retry_after INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(root_no,object_id)) WITHOUT ROWID""",
        ["table|local_rehash"] = """CREATE TABLE local_rehash(root_no INTEGER NOT NULL, object_id TEXT NOT NULL, last_attempt INTEGER NOT NULL, reason TEXT NOT NULL, failure_count INTEGER NOT NULL DEFAULT 0, retry_after INTEGER NOT NULL DEFAULT 0, evidence_revision INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(root_no, object_id)) WITHOUT ROWID""",
        ["table|notice"] = """CREATE TABLE notice(notice_id TEXT PRIMARY KEY, pair_no INTEGER NOT NULL, rule INTEGER NOT NULL, kind TEXT NOT NULL, path TEXT NOT NULL, detail TEXT NOT NULL, reason_key TEXT NOT NULL, state TEXT NOT NULL, choice TEXT, created INTEGER NOT NULL, path_key TEXT, restore_hold INTEGER NOT NULL DEFAULT 0, closed_at INTEGER) WITHOUT ROWID""",
        ["table|pending_effect"] = """CREATE TABLE "pending_effect"(intent_no INTEGER PRIMARY KEY AUTOINCREMENT, action_id TEXT NOT NULL UNIQUE, pair_no INTEGER NOT NULL, action_json TEXT NOT NULL, state TEXT NOT NULL, attempts INTEGER NOT NULL, updated INTEGER NOT NULL)""",
        ["table|planning_deferred"] = """CREATE TABLE planning_deferred(root_no INTEGER NOT NULL, object_id TEXT NOT NULL, pair_no INTEGER NOT NULL, path TEXT NOT NULL, version INTEGER NOT NULL, reason TEXT NOT NULL DEFAULT 'Waiting for content verification', alias_dir INTEGER NOT NULL DEFAULT -1, alias_key TEXT NOT NULL DEFAULT '', alias_name TEXT NOT NULL DEFAULT '', alias_unsettled INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(root_no,object_id,pair_no)) WITHOUT ROWID""",
        ["table|recovery"] = """CREATE TABLE recovery(recovery_id TEXT PRIMARY KEY, pair_no INTEGER NOT NULL, action_id TEXT NOT NULL, side TEXT NOT NULL, path TEXT NOT NULL, location TEXT NOT NULL, version_id TEXT, hash TEXT NOT NULL, size INTEGER NOT NULL, created INTEGER NOT NULL, expires INTEGER NOT NULL, path_key TEXT) WITHOUT ROWID""",
        ["table|root"] = """CREATE TABLE root(root_no INTEGER PRIMARY KEY, root_id TEXT NOT NULL UNIQUE, cursor TEXT, index_failure_count INTEGER NOT NULL DEFAULT 0, index_retry_after INTEGER NOT NULL DEFAULT 0, index_failure_reason TEXT)""",
    };

    private static void AssertApprovedCoreMigration(string[] before, string[] after)
    {
        var old = before.Select(NormalizeSchema).ToHashSet(StringComparer.Ordinal);
        var current = after.Select(NormalizeSchema).ToHashSet(StringComparer.Ordinal);
        var added = current.Except(old).ToArray();
        var removed = old.Except(current).ToArray();
        static string Key(string row) => string.Join("|", row.Split('|').Take(2));
        Require(removed.All(row => ApprovedCoreShapes.ContainsKey(Key(row))),
            "no-command open removes only the exact approved core definitions");
        var expected = ApprovedCoreShapes.Select(entry => entry.Key + "|" + entry.Value).ToHashSet(StringComparer.Ordinal);
        Require(expected.SetEquals(added), "no-command core migration definitions match the measured approved shapes exactly. " +
            "Unexpected: " + string.Join("; ", added.Except(expected)) +
            " Missing: " + string.Join("; ", expected.Except(added)));
    }
}
