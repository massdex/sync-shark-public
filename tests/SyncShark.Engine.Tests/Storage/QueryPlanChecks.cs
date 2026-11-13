using System.Text.RegularExpressions;
using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Hot storage lookups stay indexed on a fresh database without statistics.</summary>
public static class QueryPlanChecks
{
    // The leading verb decides whether a statement is a write, so an unrecognised write form still fails closed.
    private static readonly Regex WriteVerb = new(
        @"^\s*(?:INSERT|REPLACE|UPDATE|DELETE|CREATE|DROP|ALTER)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WriteTarget = new(
        @"^\s*(?:INSERT(?:\s+OR\s+\w+)?\s+INTO|REPLACE\s+INTO|UPDATE(?:\s+OR\s+\w+)?|DELETE\s+FROM" +
        @"|CREATE\s+(?:UNIQUE\s+)?(?:TABLE|INDEX)|DROP\s+(?:TABLE|INDEX)|ALTER\s+TABLE)\s+" +
        @"(?:(?:IF\s+NOT\s+EXISTS|IF\s+EXISTS)\s+)?(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A full scan of one of these tables defeats the point of a hot lookup; CTE reads are not table scans.
    private static readonly string[] DurableTables =
    [
        "change_work", "synced_entry", "folder_removal_item", "folder_removal_submission", "folder_removal",
        "issue_member", "issue", "source_state", "journal_position", "pair", "account",
    ];

    private static void ResponsibilityEquivalence()
    {
        var keys = new[] { "", "parent", "parent/child", "parent/child/leaf", "parent/childish", "sibling" };
        foreach (var folder in new[] { false, true })
        foreach (var key in keys)
        foreach (var other in keys)
        {
            var set = new PrefixSet();
            set.Add(other);
            Check.Require(set.ResponsibilityOverlaps(key, folder) ==
                PathNames.ResponsibilityOverlaps(key, other, folder),
                "responsibility snapshot matches single-key SQL predicate");
        }
        Check.Require(!new PrefixSet().ResponsibilityOverlaps(string.Empty, true), "empty responsibility set");
    }

    public static void Run()
    {
        PlanWriteFence();
        ResponsibilityEquivalence();

        using var connection = OpenSchema();
        CheckPlans(connection);
        DescendantProbeCost();
        EvidenceIdentityCost(connection);
        EchoProbePlans(connection);
        EchoProbeCosts();
    }

    private static void EchoProbePlans(SqliteConnection connection)
    {
        var direct = Plan(connection, StateSchema.SyncedDropboxBound);
        var target = direct.Where(line => line.StartsWith("SEARCH synced_entry ", StringComparison.Ordinal)).ToArray();
        Check.Require(target.Length == 3 &&
            target.Count(line => line.Contains("synced_dropbox_key", StringComparison.Ordinal) &&
                IsLookup(line, "dropbox_key_delta")) == 2 &&
            target.Count(line => line.Contains("synced_local_key", StringComparison.Ordinal) &&
                IsLookup(line, "<expr>")) == 1 &&
            target.Count(line => line.Contains("dropbox_key_delta=?", StringComparison.Ordinal)) == 1 &&
            target.Count(line => line.Contains("<expr>=?", StringComparison.Ordinal)) == 1 &&
            target.Count(line => line.Contains("dropbox_key_delta>? AND dropbox_key_delta<?", StringComparison.Ordinal)) == 1,
            "B214-QP1: exactly three direct binding seeks: " + string.Join("; ", direct));
        var inherited = Plan(connection, StateSchema.SyncedInheritedUnderPage);
        Check.Require(inherited.Count(line => line.StartsWith("SEARCH synced_entry ", StringComparison.Ordinal)) == 1 &&
            inherited.Any(line => line.Contains("synced_local_key", StringComparison.Ordinal) &&
                line.Contains("<expr>>? AND <expr><?", StringComparison.Ordinal)),
            "B214-QP1: one bounded inherited-key range: " + string.Join("; ", inherited));
        var peers = new[] { StateSchema.PeerPathPage, StateSchema.OwnedPeerPathPage }.Select(sql => Plan(connection, sql)).ToArray();
        foreach (var peer in peers)
        {
            Console.WriteLine("B214-QP1 Local peer plan: " + string.Join("; ", peer));
            Check.Require(peer.Any(line => line.StartsWith("SEARCH w USING INDEX work_path ", StringComparison.Ordinal) &&
                line.Contains("path_key=?", StringComparison.Ordinal)), "B214-QP1: Local peers seek work_path at the exact path");
        }
        var gate = Plan(connection, StateSchema.EchoWorkPending);
        Console.WriteLine("B214-CR2 gate plan: " + string.Join("; ", gate));
        Check.Require(gate.Any(line => line.StartsWith("SEARCH w USING", StringComparison.Ordinal) &&
                line.Contains("INDEX work_source_order (pair_id=? AND pair_generation=? AND side=? AND source_epoch=? AND work_id>? AND work_id<?)", StringComparison.Ordinal)) &&
            gate.All(line => !line.StartsWith("SCAN w ", StringComparison.Ordinal)),
            "B214-CR2: the no-echo gate seeks the current-source work range without scanning acquired work");
        var pending = Plan(connection, StateSchema.EchoPending, keepPair: true);
        Check.Require(pending.Any(line => line.StartsWith("SEARCH pair USING INTEGER PRIMARY KEY", StringComparison.Ordinal)),
            "B214-QP1: pending effects read the pair primary key");
        foreach (var line in direct.Concat(inherited).Concat(peers.SelectMany(plan => plan)).Concat(pending))
            Check.Require(!new[] { "SCAN synced_entry", "SCAN change_work", "LIST SUBQUERY", "MATERIALIZE",
                "COMPOUND", "UNION ALL", "USE TEMP B-TREE" }.Any(part => line.Contains(part, StringComparison.Ordinal)),
                "B214-QP1: no materialization, durable scan or read-time sort: " + line);
    }

    // The reviewed two sizes expose work proportional to an overridden subtree rather than the existing page size.
    private static void EchoProbeCosts()
    {
        const int pageSize = 100;
        foreach (var shape in new[] { "inherited", "explicit", "overridden", "late-inherited" })
        {
            (int Rows, long Steps, bool Found)? previous = null;
            foreach (var count in new[] { shape is "inherited" or "explicit" ? 10 : 1_000, 100_000 })
            {
                using var connection = OpenSchema();
                SeedEchoProbe(connection, count, shape);
                var direct = ReadEchoProbe(connection, StateSchema.SyncedDropboxBound, pageSize);
                var range = shape == "explicit" ? direct :
                    ReadEchoProbe(connection, StateSchema.SyncedInheritedUnderPage, pageSize);
                Check.Require(direct.Found == (shape == "explicit") &&
                    range.Found == (shape is "explicit" or "inherited") &&
                    range.Rows == (shape is "explicit" or "inherited" ? 1 : pageSize),
                    "B214-QP2: the probe returns the expected evidence within its page: " + shape);
                if (previous is { } before)
                    Check.Require(range.Steps == before.Steps,
                        "B214-QP2: VM steps do not grow with descendants: " + shape + " " + before.Steps + "/" + range.Steps);
                Console.WriteLine($"B214-QP2 shape={shape} descendants={count} rows={range.Rows} vm-steps={range.Steps}");
                previous = range;
            }
        }
        foreach (var (overrides, inherited, rows, found) in new[]
        {
            (10, false, 10, false), (10, true, 11, true), (60, false, 60, false),
            (pageSize, false, pageSize, false), (pageSize - 1, true, pageSize, true)
        })
        {
            using var connection = OpenSchema();
            SeedEchoProbe(connection, overrides, inherited ? "late-inherited" : "overridden");
            var actual = ReadEchoProbe(connection, StateSchema.SyncedInheritedUnderPage, pageSize);
            Check.Require(actual.Rows == rows && actual.Found == found,
                "B214-QP2: exhausted, full nonmatching and final-row inherited evidence stay distinct");
        }
    }

    private static void SeedEchoProbe(SqliteConnection connection, int count, string shape)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO pair(pair_id,def_json) VALUES('echo-probe','{}');";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO synced_entry(pair_id,local_path,dropbox_key_delta,dropbox_path_delta," +
            "is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) " +
            "VALUES(1,$path,$key,$remote,0,'',1,0,$id,'hash');";
        command.Parameters.AddWithValue("$path", "");
        command.Parameters.AddWithValue("$key", DBNull.Value);
        command.Parameters.AddWithValue("$remote", DBNull.Value);
        command.Parameters.AddWithValue("$id", "");
        void Insert(string path, string? remote)
        {
            command.Parameters["$path"].Value = path;
            command.Parameters["$key"].Value = remote is null ? DBNull.Value : PathNames.DropboxKey(remote);
            command.Parameters["$remote"].Value = (object?)remote ?? DBNull.Value;
            command.Parameters["$id"].Value = StateEncoding.IdFor(Side.Dropbox, "id-" + path)!;
            command.ExecuteNonQuery();
        }
        for (var index = 0; index < count; index++)
        {
            var suffix = index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            Insert((shape == "explicit" ? "Q/" : "P/") + suffix,
                shape == "inherited" ? null : (shape == "explicit" ? "P/" : "Q/") + suffix);
        }
        if (shape == "late-inherited") Insert("P/Z", null);
        transaction.Commit();
    }

    // Stop exactly when production knows the answer; the handle belongs to this synthetic SqliteConnection.
    private static (int Rows, long Steps, bool Found) ReadEchoProbe(SqliteConnection connection, string sql, int take)
    {
        var prepared = SQLitePCL.raw.sqlite3_prepare_v2(connection.Handle, sql, out var statement);
        Check.Require(prepared == SQLitePCL.raw.SQLITE_OK, "B214 probe prepares the production statement");
        try
        {
            SQLitePCL.raw.sqlite3_bind_text(statement, SQLitePCL.raw.sqlite3_bind_parameter_index(statement, "$p"), "echo-probe");
            SQLitePCL.raw.sqlite3_bind_text(statement, SQLitePCL.raw.sqlite3_bind_parameter_index(statement, "$k"), PathNames.DropboxKey("P"));
            var takeIndex = SQLitePCL.raw.sqlite3_bind_parameter_index(statement, "$take");
            if (takeIndex != 0) SQLitePCL.raw.sqlite3_bind_int(statement, takeIndex, take);
            var rows = 0;
            var found = false;
            while (true)
            {
                var result = SQLitePCL.raw.sqlite3_step(statement);
                if (result == SQLitePCL.raw.SQLITE_DONE) break;
                Check.Require(result == SQLitePCL.raw.SQLITE_ROW, "B214 probe executes successfully");
                rows++;
                if (SQLitePCL.raw.sqlite3_column_int(statement, 0) != 0) { found = true; break; }
            }
            return (rows, SQLitePCL.raw.sqlite3_stmt_status(statement, SQLitePCL.raw.SQLITE_STMTSTATUS_VM_STEP, 0), found);
        }
        finally { SQLitePCL.raw.sqlite3_finalize(statement); }
    }

    // B-101 (section 3.7): the admission identity probe (ChangeTable.UnownedIdentity) is one seek on work_identity.
    // Its VM steps depend on the rows sharing the identity, not on the pair's other pending work: the same probe is
    // measured over one and two scan batches (ScanTestSettings.BatchRows) of unrelated rows.
    private static void EvidenceIdentityCost(SqliteConnection connection)
    {
        var plan = Plan(connection, StateSchema.EvidenceIdentity);
        Console.WriteLine("B101-COST plan EvidenceIdentity: " + string.Join("; ", plan));
        Check.Require(plan.Any(line => line.Contains("work_identity", StringComparison.Ordinal)) &&
            plan.Where(IsTableAccess).All(line => !line.StartsWith("SCAN ", StringComparison.Ordinal)),
            "B-101: the admission identity probe seeks work_identity: " + string.Join("; ", plan));
        var path = Path.Combine(Path.GetTempPath(),
            "identity-cost-" + Guid.NewGuid().ToString("N") + StateSchema.DatabaseExtension);
        try
        {
            using var probe = new CostProbe(path);
            probe.CreateSchema();
            probe.Execute(StateSchema.UpsertPair, (StateSchema.ParamPair, "cost"), (StateSchema.ParamDef, "{}"));
            const long pairRow = 1; // the first pair row of a fresh schema
            var next = 0L;
            void Add(string identity, int count)
            {
                probe.Begin();
                for (var index = 0; index < count; index++)
                {
                    next++;
                    probe.Execute(StateSchema.InsertWork, (StateSchema.ParamId, next), (StateSchema.ParamPairRow, pairRow),
                        (StateSchema.ParamGeneration, 0L), (StateSchema.ParamEpoch, 0L), (StateSchema.ParamSide, (int)Side.Dropbox),
                        (StateSchema.ParamDomain, "domain"), (StateSchema.ParamPath, "p/" + next), (StateSchema.ParamOldPath, null),
                        (StateSchema.ParamIdentity, StateEncoding.IdFor(Side.Dropbox, identity + (count == 1 ? "" : next.ToString()))),
                        (StateSchema.ParamNotice, "{\"Version\":1}"), (StateSchema.ParamBefore, null),
                        (StateSchema.ParamAcquisition, next), (StateSchema.ParamParent, null), (StateSchema.ParamObligation, null));
                }
                probe.Commit();
            }
            StatementCost Measure() => probe.Execute(StateSchema.EvidenceIdentity, (StateSchema.ParamPairRow, pairRow),
                (StateSchema.ParamGeneration, 0L), (StateSchema.ParamSide, (int)Side.Dropbox), (StateSchema.ParamEpoch, 0L),
                (StateSchema.ParamDomain, "domain"), (StateSchema.ParamIdentity, StateEncoding.IdFor(Side.Dropbox, "moved")));
            var none = Measure();
            Add("moved", 1);
            var one = Measure();
            Add("other-", ScanTestSettings.BatchRows);
            var oneBatch = Measure();
            Add("other-", ScanTestSettings.BatchRows);
            var twoBatches = Measure();
            Console.WriteLine($"B101-COST EvidenceIdentity vm-steps: no-match={none.VmSteps} one-match={one.VmSteps} " +
                $"one-match+{ScanTestSettings.BatchRows}-unrelated={oneBatch.VmSteps} " +
                $"one-match+{2 * ScanTestSettings.BatchRows}-unrelated={twoBatches.VmSteps} prepares={one.ProbePrepares}");
            // Index depth grows logarithmically, so doubling the unrelated rows may add at most one B-tree level's step.
            Check.Require(one.VmSteps > none.VmSteps && twoBatches.VmSteps - oneBatch.VmSteps <= 1,
                "B-101: the admission identity probe does not grow with the pair's unrelated pending rows");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A light counted comparison of the #129 probe against the delete it can skip (section 3.7). Limits: CostProbe
    // runs its own raw connection; VM steps come from a 32-bit stmt_status counter; ProbePrepares is the probe's own
    // preparation count, not a production prepare count; and pair occurrences are source counts. It measures one
    // statement, not end-to-end ingestion.
    // B-164 G1: the receipt inspection runs one FolderRemovalItemByKey lookup per recycled entry: the pair-row
    // resolution (PairFence, StateStore.RequirePairRow) and then FolderItemByKey. The member statement's plan seeks
    // the probed native key, and both statements cost the same VM work for a 10-member and a 10,000-member operation
    // with two rows at the key: neither the scanned rows nor the ORDER BY ordinal sorter input grow with the operation.
    public static void FolderMemberByKeyBounded()
    {
        ExactKeyMatchesScopedLookup();
        var small = MemberLookup(10);
        var large = MemberLookup(10_000);
        Console.WriteLine($"B164-COST per-descendant lookup vm-steps: PairFence m10={small.Pair.VmSteps} " +
            $"m10000={large.Pair.VmSteps}; FolderItemByKey m10={small.Cost.VmSteps} m10000={large.Cost.VmSteps}; " +
            $"total m10={small.Pair.VmSteps + small.Cost.VmSteps} m10000={large.Pair.VmSteps + large.Cost.VmSteps}; " +
            "statements per descendant=2; plan: " + string.Join("; ", large.Plan));
        foreach (var plan in new[] { small.Plan, large.Plan })
            Check.Require(plan.Any(line => line.StartsWith("SEARCH folder_removal_item USING INDEX folder_item_identity",
                    StringComparison.Ordinal) && line.Contains("native_key", StringComparison.Ordinal)) &&
                !plan.Any(line => line.StartsWith("SCAN folder_removal_item", StringComparison.Ordinal)),
                "B-164 G1: the member lookup seeks its native key: " + string.Join("; ", plan));
        Check.Require(small.Cost.VmSteps > 0 && small.Cost.VmSteps == large.Cost.VmSteps &&
                small.Pair.VmSteps > 0 && small.Pair.VmSteps == large.Pair.VmSteps,
            "B-164 G1: the member lookup's VM work, pair resolution included, does not grow with the operation's size");
    }

    // One member row as the frozen plan writes it; the parent operation row is not what the lookups read.
    private static void InsertMember(CostProbe probe, string operation, long ordinal, string provenance, string member) =>
        probe.Execute(StateSchema.InsertFolderItem,
            ("$pair_id", 1L), ("$operation_id", operation), ("$ordinal", ordinal), ("$generation", 0L),
            ("$provenance", provenance), ("$path", member), ("$policy_key", PathNames.Key(member)),
            ("$expected_json", null), ("$saved_json", null), ("$verdict", "Survives"), ("$follow_up", 0L),
            ("$follow_up_state", "None"), ("$selected_branch", 0L), ("$effect_kind", "None"),
            ("$effect_state", "None"), ("$effect_generation", 0L), ("$submission_id", null),
            ("$submission_member", null), ("$effect_result_json", null), ("$phase", "Planned"),
            ("$selected_json", null), ("$stage_allocation_json", null), ("$stage_json", null),
            ("$result_json", null), ("$cause", null));

    // B-164 G1 correctness (standard 4.3): the exact-key form returns the member the scoped form returns with an empty
    // range, over a lower-ordinal other provenance, a case variant of the key, a longer sibling, the parent, a missing
    // key and the same key in another operation.
    private static void ExactKeyMatchesScopedLookup()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "member-key-" + Guid.NewGuid().ToString("N") + StateSchema.DatabaseExtension);
        try
        {
            using (var probe = new CostProbe(path))
            {
                probe.CreateSchema();
                probe.Execute(StateSchema.ForeignKeysOff);
                probe.Execute(StateSchema.UpsertPair, (StateSchema.ParamPair, "cost"), (StateSchema.ParamDef, "{}"));
                InsertMember(probe, "operation", 0, "Planned", "Folder/Ab");
                InsertMember(probe, "operation", 1, "Unexpected", "Folder/A");
                InsertMember(probe, "operation", 2, "BlockingParent", "folder/a");
                InsertMember(probe, "operation", 3, "Planned", "Folder/A");
                InsertMember(probe, "operation", 4, "Planned", "Folder");
                InsertMember(probe, "other", 0, "Planned", "Folder/A");
            }
            using var connection = new SqliteConnection("Data Source=" + path);
            StateStore.OpenConnection(connection);
            long? First(string sql, string key, bool scoped)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue(StateSchema.ParamPairRow, 1L);
                command.Parameters.AddWithValue(StateSchema.ParamOperation, "operation");
                command.Parameters.AddWithValue(StateSchema.ParamTargetKey, key);
                if (scoped)
                {
                    command.Parameters.AddWithValue(StateSchema.ParamLow, key);
                    command.Parameters.AddWithValue(StateSchema.ParamHigh, key);
                }
                command.Parameters.AddWithValue(StateSchema.ParamAfter, (long)StateSchema.BeforeFirstOrdinal);
                command.Parameters.AddWithValue(StateSchema.ParamTake, (long)StateSchema.OneRow);
                using var reader = command.ExecuteReader();
                return reader.Read() ? reader.GetInt64(reader.GetOrdinal("ordinal")) : null;
            }
            foreach (var (probed, expected) in new (string, long?)[]
                { ("Folder/A", 1), ("folder/a", 1), ("Folder/Ab", 0), ("Folder", 4), ("Folder/B", null) })
            {
                var key = PathNames.Key(probed);
                var exact = First(StateSchema.FolderItemByKey, key, scoped: false);
                Check.Require(exact == expected && exact == First(StateSchema.FolderItemsInScope, key, scoped: true),
                    "B-164 G1: the exact-key lookup returns the scoped form's member for " + probed);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static (StatementCost Pair, StatementCost Cost, IReadOnlyList<string> Plan) MemberLookup(int members)
    {
        var path = Path.Combine(Path.GetTempPath(),
            "member-cost-" + Guid.NewGuid().ToString("N") + StateSchema.DatabaseExtension);
        try
        {
            using var probe = new CostProbe(path);
            probe.CreateSchema();
            // Members only: the parent operation row is not what the lookup reads.
            probe.Execute(StateSchema.ForeignKeysOff);
            probe.Execute(StateSchema.UpsertPair, (StateSchema.ParamPair, "cost"), (StateSchema.ParamDef, "{}"));
            const long pairRow = 1; // the first pair row of a fresh schema
            string Member(int index) => "Folder/" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
            probe.Begin();
            for (var index = 0; index < members; index++) InsertMember(probe, "operation", index, "Planned", Member(index));
            var probed = Member(members / 2);
            InsertMember(probe, "operation", members, "Unexpected", probed);
            probe.Commit();
            var key = PathNames.Key(probed);
            (string, object?)[] arguments =
            [
                (StateSchema.ParamPairRow, pairRow), (StateSchema.ParamOperation, "operation"),
                (StateSchema.ParamTargetKey, key),
                (StateSchema.ParamAfter, (long)StateSchema.BeforeFirstOrdinal), (StateSchema.ParamTake, (long)StateSchema.OneRow)
            ];
            return (probe.ExecuteFirst(StateSchema.PairFence, (StateSchema.ParamPair, "cost")),
                probe.ExecuteFirst(StateSchema.FolderItemByKey, arguments),
                probe.ExplainPlan(StateSchema.FolderItemByKey, arguments));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void DescendantProbeCost()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "probe-cost-" + Guid.NewGuid().ToString("N") + StateSchema.DatabaseExtension);
        try
        {
            using var probe = new CostProbe(path);
            probe.CreateSchema();
            probe.Execute(StateSchema.UpsertPair, (StateSchema.ParamPair, "cost"), (StateSchema.ParamDef, "{}"));
            probe.AttachScan(":memory:");
            var key = PathNames.Key("cost/a");
            var emptyProbe = probe.ExecuteFirst(StateSchema.ScanHasDescendants, (StateSchema.ParamPair, "cost"),
                (StateSchema.ParamScan, Guid.Empty.ToByteArray()), (StateSchema.ParamSide, (int)Side.Local), (StateSchema.ParamTargetKey, key));
            var emptyDelete = probe.Execute(StateSchema.ScanDeleteDescendants, (StateSchema.ParamPair, "cost"),
                (StateSchema.ParamScan, Guid.Empty.ToByteArray()), (StateSchema.ParamSide, (int)Side.Local), (StateSchema.ParamTargetKey, key));
            Check.Require(emptyProbe.VmSteps > 0 && emptyProbe.VmSteps <= emptyDelete.VmSteps &&
                emptyDelete.ChangedRows == 0,
                "an empty descendant probe costs no more VM steps than the delete it skips");

            var childKey = PathNames.Key("cost/a/child");
            probe.Execute(StateSchema.ScanUpsert, (StateSchema.ParamPair, "cost"),
                (StateSchema.ParamScan, Guid.Empty.ToByteArray()), (StateSchema.ParamSide, (int)Side.Local), (StateSchema.ParamJoinKey, childKey),
                (StateSchema.ParamOwnKey, childKey), (StateSchema.ParamPath, "cost/a/child"),
                (StateSchema.ParamIsFolder, false), (StateSchema.ParamSize, 1L), (StateSchema.ParamMtime, 0L),
                (StateSchema.ParamId, "id-child"), (StateSchema.ParamRev, ""), (StateSchema.ParamHash, ""),
                (StateSchema.ParamSkip, null));
            var foundProbe = probe.ExecuteFirst(StateSchema.ScanHasDescendants, (StateSchema.ParamPair, "cost"),
                (StateSchema.ParamScan, Guid.Empty.ToByteArray()), (StateSchema.ParamSide, (int)Side.Local), (StateSchema.ParamTargetKey, key));
            var foundDelete = probe.Execute(StateSchema.ScanDeleteDescendants, (StateSchema.ParamPair, "cost"),
                (StateSchema.ParamScan, Guid.Empty.ToByteArray()), (StateSchema.ParamSide, (int)Side.Local), (StateSchema.ParamTargetKey, key));
            Check.Require(foundProbe.VmSteps > 0 && foundDelete.ChangedRows == 1 &&
                foundProbe.VmSteps + foundDelete.VmSteps <= 2 * foundDelete.VmSteps,
                "a found probe plus delete costs at most twice the direct delete's VM steps");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static SqliteConnection OpenSchema()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        StateStore.OpenConnection(connection);
        using (var create = connection.CreateCommand())
        {
            create.CommandText = StateSchema.Create + StateSchema.SchemaAdditions + StateSchema.DeletedHistorySchema + StateSchema.RebuildIssues + StateSchema.DeletedHistoryBlocks + StateSchema.DropDeletedHistoryNames + StateSchema.SpentDeletedHistory + StateSchema.LocalPublishedColumns + StateSchema.DiscoveryRequiredColumn + StateSchema.IssuePathKeyIndex + StateSchema.RenameContinuationColumns + StateSchema.PublishVersion +
                "ATTACH DATABASE ':memory:' AS scan_state;" + StateSchema.EmptyScanPragmas + StateSchema.CreateScan + StateSchema.CreateSkipTouched + StateSchema.CreateScanIssues;
            create.ExecuteNonQuery();
            // Production semantics: an issue DELETE also plans its child SET NULL program, checked below.
            create.CommandText = "PRAGMA foreign_keys=ON;";
            create.ExecuteNonQuery();
            create.CommandText = "SELECT count(*) FROM sqlite_master WHERE name LIKE 'sqlite_stat%';";
            Check.Require((long)create.ExecuteScalar()! == 0, "query plans are checked without ANALYZE statistics");
        }

        _ = new IssueFactsPublication(connection, writable: true);
        return connection;
    }

    public static void ClaimedRetirement()
    {
        using var connection = OpenSchema();
        ClaimedRetirement(connection);
    }

    private static void ClaimedRetirement(SqliteConnection connection)
    {
        CheckDurableStatement(connection, StateSchema.RetireUnclaimedWork(1));
        CheckDurableStatement(connection, StateSchema.OwnedPeerPathPage);
        var claimed = Plan(connection, StateSchema.RetireUnclaimedWork(1));
        Check.Require(claimed.Where(line => line.StartsWith("SCAN ", StringComparison.Ordinal))
            .All(line => line is "SCAN retained_dirty" or "SCAN CONSTANT ROW"),
            "claim-aware retirement scans only its existing materialized dirty set: " + string.Join("; ", claimed));
        Check.Require(claimed.Any(line => line.Contains("work_identity", StringComparison.Ordinal)) &&
            claimed.Any(line => line.Contains("work_path", StringComparison.Ordinal)),
            "claims use identity and peer-path indexes: " + string.Join("; ", claimed));
    }

    private static void CheckPlans(SqliteConnection connection)
    {
        foreach (var sql in new[] { StateSchema.DeleteScanBatch, StateSchema.DeleteTouchBatch,
                     StateSchema.DeleteScanIssueBatch, StateSchema.ScanIssueUnchanged })
        {
            var partition = Plan(connection, sql);
            Check.Require(partition.Any(line => line.Contains("scan_id=?", StringComparison.Ordinal)) &&
                partition.Where(IsTableAccess).All(line => !line.StartsWith("SCAN ", StringComparison.Ordinal)),
                "partition cleanup and evidence fences use token-indexed lookups: " + string.Join("; ", partition));
        }
        CheckDurableStatement(connection, StateSchema.DeleteSyncedBothSubtrees);
        Check.Require(Plan(connection, StateSchema.DeleteSyncedBothSubtrees).Any(line =>
            line.Contains("<expr>>? AND <expr><?", StringComparison.Ordinal)),
            "Bilateral subtree forgetting seeks the Local descendant range.");
        CheckDurableStatement(connection, StateSchema.EchoResponsibilities);
        CheckDurableStatement(connection, StateSchema.EchoPending);
        CheckDurableStatement(connection, StateSchema.PeerPathPage);
        CheckDurableStatement(connection, StateSchema.WorkHasChildren);
        CheckDurableStatement(connection, StateSchema.RequireIdentityDiscovery);
        CheckDurableStatement(connection, StateSchema.RequirePathDiscovery);
        CheckDurableStatement(connection, StateSchema.WorkIssue);
        CheckDurableStatement(connection, StateSchema.AnyLocalEvidence);
        CheckDurableStatement(connection, StateSchema.SelectIssueFact);
        CheckDurableStatement(connection, StateSchema.SelectIssues);
        CheckDurableStatement(connection, StateSchema.SelectIssuesByKinds);
        CheckDurableStatement(connection, StateSchema.SelectIssueFacts, keepPair: true);

        foreach (var (sql, key) in new[]
        {
            (StateSchema.RebaseDescendantsLocal, "<expr>"),
            (StateSchema.RebaseDescendantsDropbox, "dropbox_key"),
            (StateSchema.MoveScanSubtree, "<expr>"),
            (StateSchema.DeleteSyncedSubtreeLocal, "<expr>"),
            (StateSchema.DeleteSyncedSubtreeDropbox, "dropbox_key"),
            (StateSchema.ScanDeleteSelf, "<expr>"),
            (StateSchema.ScanDeleteDescendants, "<expr>"),
            (StateSchema.ScanHasDescendants, "<expr>"),
        })
        {
            var plan = Plan(connection, sql);
            Check.Require(plan.Any(line => line.Contains($"{key}>? AND {key}<?", StringComparison.Ordinal) ||
                key == "dropbox_key" && line.Contains("dropbox_key_delta>? AND dropbox_key_delta<?", StringComparison.Ordinal)),
                "descendants use a bounded BINARY index range: " + string.Join("; ", plan));
            foreach (var line in plan.Where(IsTableAccess))
                Check.Require(IsLookup(line, key) || IsLookup(line, "<expr>") || IsLookup(line, "dropbox_key_delta") || line.Contains("rowid=?") ||
                    line.StartsWith("SCAN json_each VIRTUAL TABLE", StringComparison.Ordinal),
                    "subtree access uses its natural or override range: " + line);
        }

        foreach (var sql in new[]
        {
            StateSchema.DeleteSyncedLocalKey, StateSchema.DeleteSyncedDropboxKey, StateSchema.DeleteSyncedEitherKey,
        })
        {
            var plan = Plan(connection, sql);
            foreach (var line in plan.Where(IsTableAccess))
                Check.Require(IsLookup(line, "<expr>") || IsLookup(line, "dropbox_key_delta") || line.Contains("rowid=?"),
                    "record deletion uses a complete key lookup: " + line);
            if (sql == StateSchema.DeleteSyncedEitherKey)
                Check.Require(plan.Count(line => IsLookup(line, "<expr>") || IsLookup(line, "dropbox_key_delta")) == 3, "record replacement has two indexed deletes");
        }

        var renames = Plan(connection, StateSchema.ScanRenames);
        Check.Require(renames.Contains("UNION ALL"), "rename sides use independent UNION ALL branches");
        Check.Require(renames.Count(line => IsLookup(line, "local_id") && line.Contains("synced_local_id")) == 1 &&
            renames.Count(line => IsLookup(line, "dropbox_id") && line.Contains("synced_dropbox_id")) == 1,
            "both rename branches probe a complete identity index: " + string.Join("; ", renames));
        Check.Require(renames.All(line => !line.StartsWith("SCAN ", StringComparison.Ordinal)),
            "rename joins never scan the inner pair");
        foreach (var line in renames.Where(line => line.StartsWith("SEARCH s ", StringComparison.Ordinal)))
            Check.Require(IsLookup(line, "local_id") || IsLookup(line, "dropbox_id"),
                "each rename inner access includes identity: " + line);

        var rows = Plan(connection, StateSchema.ScanRows);
        Check.Require(rows.Any(line => IsLookup(line, "<expr>")) &&
            rows.Any(line => IsLookup(line, "key_delta") && line.Contains("scan_key")),
            "row candidates use native and partial mapped-key indexes: " + string.Join("; ", rows));
        Check.Require(rows.Where(line => line.StartsWith("SCAN ")).All(line => line == "SCAN f" || line == "SCAN CONSTANT ROW"),
            "only the bounded free-key result may scan, never a joined table: " + string.Join("; ", rows));
        foreach (var line in rows.Where(line => line.StartsWith("SEARCH x ") || line.StartsWith("SEARCH l ") ||
                     line.StartsWith("SEARCH d ") || line.StartsWith("SEARCH s ") || line.StartsWith("SEARCH scan ")))
            Check.Require(IsLookup(line, "<expr>") || IsLookup(line, "key_delta") ||
                IsLookup(line, "dropbox_key_delta") || line.Contains("rowid=?"),
                "each row join constrains a complete indexed key: " + line);
        foreach (var sql in new[] { StateSchema.InScanLocal, StateSchema.InScanDropbox, StateSchema.SelectScanEntry,
            StateSchema.ClashWithInherited, StateSchema.ClashWithOverride, StateSchema.ClashInDropboxSubtree })
            foreach (var line in Plan(connection, sql).Where(IsTableAccess))
                Check.Require(IsLookup(line, "<expr>") || IsLookup(line, "key_delta") ||
                    IsLookup(line, "dropbox_key_delta") || line.Contains("rowid=?"),
                    "existence and uniqueness probes constrain the complete key: " + line);

        foreach (var sql in new[] { StateSchema.SelectSyncedByLocalId, StateSchema.SelectSyncedByDropboxId })
        {
            var plan = Plan(connection, sql);
            Check.Require(plan.All(line => IsLookup(line, "id") || IsLookup(line, "local_id") || IsLookup(line, "dropbox_id")),
                "single rename lookups use identity indexes: " + string.Join("; ", plan));
        }

        var scoped = Plan(connection, StateSchema.NextScopedScanKey);
        foreach (var line in scoped.Where(line => line.StartsWith("SEARCH scan ") || line.StartsWith("SEARCH synced_entry ")))
            Check.Require(line.Contains("pair_id=?") &&
                (line.Contains("<expr>>? AND <expr><?") || line.Contains("key_delta>? AND key_delta<?")),
                "batch next-key discovery bounds every source to the subtree: " + line);
        Check.Require(!scoped.Any(line => line.StartsWith("SCAN scan ") || line.StartsWith("SCAN synced_entry ")),
            "batch next-key discovery cannot scan the whole pair");
        var next = Plan(connection, StateSchema.NextScanKey);
        foreach (var line in next.Where(line => line.StartsWith("SEARCH scan ") || line.StartsWith("SEARCH synced_entry ")))
            Check.Require(line.Contains("pair_id=?") &&
                (line.Contains("<expr>>?") || line.Contains("key_delta>?")),
                "each page resumes with an indexed key range, never OFFSET or a pair scan: " + line);
        Check.Require(!next.Any(line => line.StartsWith("SCAN scan ") || line.StartsWith("SCAN synced_entry ")),
            "next-key discovery cannot rescan the whole pair");
        foreach (var sql in new[] { StateSchema.SelectSyncedByLocalKey, StateSchema.SelectSyncedByDropboxKey,
            StateSchema.SyncedLocalPublished, StateSchema.RenameSyncedRow, StateSchema.SelectIssueByPathKind, StateSchema.DeleteIssueByPathKind,
            StateSchema.ReplaceIssue })
            foreach (var line in Plan(connection, sql).Where(line => IsTableAccess(line) && !line.Contains("VIRTUAL TABLE")))
                Check.Require(IsLookup(line, "<expr>") || IsLookup(line, "dropbox_key_delta") ||
                    IsLookup(line, "path") && line.Contains("kind=?") || line.Contains("rowid=?"),
                    "per-effect reads and updates constrain their full natural key: " + line);

        var oldDescendants = Plan(connection, "DELETE FROM scan WHERE pair_id=$p AND side=$side AND substr(own_key,1,length($k)+1)=$k||'/';");
        Check.Require(!oldDescendants.Any(line => IsLookup(line, "<expr>")),
            "the plan oracle rejects a pair/side SEARCH masquerading as an indexed descendant lookup");

        // Production's issue DELETE carries the child SET NULL program; it must seek the child's FK index.
        var referential = ReferentialPlan(connection, StateSchema.DeleteIssueByPathKind);
        Check.Require(referential.Any(line => line.StartsWith("SEARCH issue_member ", StringComparison.Ordinal) &&
            line.Contains("issue_member_issue", StringComparison.Ordinal) &&
            line.Contains("issue_id=?", StringComparison.Ordinal)),
            "the issue referential action seeks issue_member's FK index: " + string.Join("; ", referential));

        foreach (var sql in new[] { StateSchema.SelectSyncedUnderLocalKeyPaged,
            StateSchema.SelectSyncedUnderDropboxKeyPaged, StateSchema.SelectSyncedUnderInheritedKeyPaged })
        {
            var plan = Plan(connection, sql);
            Check.Require(plan.Count == 1 && plan.All(line => line.StartsWith("SEARCH synced_entry ") &&
                line.Contains("pair_id=?") && (line.Contains("<expr>>? AND <expr><?") ||
                line.Contains("dropbox_key_delta>? AND dropbox_key_delta<?"))),
                "counterpart pages seek their cursor range without sorting or rebuilding membership: " + string.Join("; ", plan));
        }
        DeletedNamePlans(connection);
        SchemaStatements(connection);
        PlanStatements();
    }

    /// <summary>Discovery membership is one primary-key seek per asked question, never a relation scan.</summary>
    private static void DeletedNamePlans(SqliteConnection connection)
    {
        using (var create = connection.CreateCommand())
        {
            create.CommandText = StateSchema.CreateDeletedName;
            create.ExecuteNonQuery();
        }
        Check.Require(Plan(connection, StateSchema.DeletedNameLive)
                .Any(line => line.Contains("pair_id=? AND path_key=?", StringComparison.Ordinal)),
            "Resumed discovery live overrides seek the complete key without statistics.");
        foreach (var (sql, exact) in new[] { (StateSchema.HasDeletedNames, false), (StateSchema.DeletedNameExact, true) })
        {
            var plan = Plan(connection, sql);
            var access = plan.Single(line => line.StartsWith("SEARCH scan_state.deleted_name ", StringComparison.Ordinal) ||
                line.StartsWith("SEARCH n ", StringComparison.Ordinal));
            Check.Require(access.Contains("pair_id=?", StringComparison.Ordinal),
                "deleted-name membership seeks the pair prefix: " + access);
            if (exact)
                Check.Require(access.Contains("path_key=?", StringComparison.Ordinal),
                    "exact deleted-name membership seeks the complete key: " + access);
            Check.Require(!plan.Any(line => line.StartsWith("SCAN scan_state.deleted_name ", StringComparison.Ordinal)),
                "deleted-name membership never scans the discovery relation: " + string.Join("; ", plan));
        }
    }

    /// <summary>Schema-5 work, folder and retry statements seek their owning index instead of scanning.</summary>
    private static void SchemaStatements(SqliteConnection connection)
    {
        foreach (var sql in new[]
        {
            StateSchema.ReadPathSelection, StateSchema.DirtyIdentity,
            StateSchema.SyncedLocalPublished,
            StateSchema.EvidenceIdentity, StateSchema.EvidencePathExact, StateSchema.EvidencePathBelow,
            StateSchema.EvidenceOldPathExact, StateSchema.EvidenceOldPathBelow,
            StateSchema.ReadIssueMembers, StateSchema.FolderRead, StateSchema.ActiveFolders,
            StateSchema.FolderItems, StateSchema.ItemEpisodeByWork, StateSchema.WorkDispatch,
            StateSchema.IssueWorkByKey, StateSchema.HasCurrentLocalWork,
            StateSchema.RetryNoticeProtected, StateSchema.CloseRetryNotice, StateSchema.SelectRootIssueByKind,
            StateSchema.ReadFolderDisposition,
            StateSchema.SubmissionRead, StateSchema.SubmissionEffects,
            StateSchema.WorkHasChildren, StateSchema.DeleteWork, StateSchema.WorkIssue, StateSchema.PeerPathPage,
            StateSchema.OwnedPeerPathPage,
            StateSchema.UpdateRegisteredNotice, StateSchema.RegistrationByParent, StateSchema.MoveRegistration,
            StateSchema.EpisodeWork, StateSchema.RetireCoveredWork, StateSchema.RetireUnclaimedWork(1),
        })
            CheckDurableStatement(connection, sql);

        ClaimedRetirement(connection);

        foreach (var (sql, index) in new[]
        {
            (StateSchema.SelectWork, "work_source_order"),
            (StateSchema.CountWork, "work_source_order"),
            (StateSchema.ReadIdentitySelection, "work_identity"),
            (StateSchema.CompleteIdentitySelection, "work_identity"),
            (StateSchema.SaveBeforeIdentity, "work_identity"),
            // A residual's parent and obligation name one row through the unique ownership constraint.
            (StateSchema.ReadResidualIdentitySelection, "parent_anchor=? AND obligation_key=?"),
            (StateSchema.CompleteResidualIdentitySelection, "parent_anchor=? AND obligation_key=?"),
            (StateSchema.SaveBeforeResidualIdentity, "parent_anchor=? AND obligation_key=?"),
            (StateSchema.ActiveFolderByRoot, "folder_removal_root"),
            (StateSchema.FolderItemNative, "folder_item_identity"),
            (StateSchema.SelectedFolderBranch, "folder_item_identity"),
            (StateSchema.ReadyFirst, "folder_item_ready_order"),
            (StateSchema.ReadyNext, "folder_item_ready_order"),
            (StateSchema.PendingFolderSubmissions, "folder_submission_pending"),
            (StateSchema.EarliestRetry, "pair_retry_due"),
            (StateSchema.ItemEpisodeBySubject, "issue_member_retry_subject"),
            (StateSchema.ItemEpisodesAtPath, "issue_member_retry_subject"),
            (StateSchema.Registration, "issue_member_retry_subject"),
        })
            // EarliestRetry is itself the pair keyset, so its pair search line carries the owning index.
            CheckDurableStatement(connection, sql, index, keepPair: sql == StateSchema.EarliestRetry);

        var members = Plan(connection, StateSchema.FolderItems);
        Check.Require(members.Any(line => line.StartsWith("SEARCH folder_removal_item ", StringComparison.Ordinal) &&
            line.Contains("pair_id=? AND operation_id=? AND ordinal>?", StringComparison.Ordinal)),
            "exact recovery member paging seeks the operation and ordinal: " + string.Join("; ", members));

        // A scoped member page asks for one key or a range of it through the same identity index.
        CheckIndexUse(connection, StateSchema.FolderItemsInScope, "folder_item_identity");

        // The pair row is the whole lookup, so its own key seek is the expected plan.
        CheckDurableStatement(connection, StateSchema.ReadRetry, keepPair: true);
        CheckWorkCountRange(connection);
    }

    // The queue snapshot ranges the pending rows of each side through work_source_order: the full pair, generation,
    // side and epoch equality prefix plus the watermark range, read from the index alone so no row is fetched. An
    // alias scan ("SCAN w") would grow with everything stored rather than with the backlog; a constant-row scan is
    // the scalar wrapper and is not itself a defect.
    private static void CheckWorkCountRange(SqliteConnection connection)
    {
        var plan = Plan(connection, StateSchema.CountWork);
        const string prefix = "pair_id=? AND pair_generation=? AND side=? AND source_epoch=? AND work_id<?";
        Check.Require(plan.Count(line => line.StartsWith("SEARCH w USING COVERING INDEX work_source_order (",
                    StringComparison.Ordinal) && line.Contains(prefix, StringComparison.Ordinal)) == 2 &&
                plan.All(line => !line.StartsWith("SCAN w", StringComparison.Ordinal)),
            "the queue snapshot ranges both sides through work_source_order: " + string.Join("; ", plan));
    }

    private static void CheckDurableStatement(SqliteConnection connection, string sql, string? index = null,
        bool keepPair = false)
    {
        var plan = Plan(connection, sql, keepPair);
        foreach (var line in plan.Where(line => line.StartsWith("SCAN ", StringComparison.Ordinal)))
            Check.Require(line == "SCAN CONSTANT ROW" ||
                !DurableTables.Any(table => line.Contains(table, StringComparison.Ordinal)),
                "schema statements seek durable tables instead of scanning them: " + line);
        if (index is not null)
            Check.Require(plan.Any(line => line.Contains(index, StringComparison.Ordinal)),
                "schema statement uses its owning index " + index + ": " + sql);
    }

    private static void CheckIndexUse(SqliteConnection connection, string sql, string index)
    {
        var plan = Plan(connection, sql);
        Check.Require(plan.Any(line => line.Contains(index, StringComparison.Ordinal)),
            "schema statement uses its owning index " + index + ": " + sql);
    }

    /// <summary>Every statement a folder plan executes; the plan call-site fence reuses this list.</summary>
    internal static readonly (string Name, string Sql)[] PlanStatementInventory =
    [
        (nameof(StateSchema.CreateScan), StateSchema.CreateScan),
        (nameof(StateSchema.PlanCreateClassified), StateSchema.PlanCreateClassified),
        (nameof(StateSchema.PlanCreateClassifiedLocalRoots), StateSchema.PlanCreateClassifiedLocalRoots),
        (nameof(StateSchema.PlanCreateClassifiedDropboxRoots), StateSchema.PlanCreateClassifiedDropboxRoots),
        (nameof(StateSchema.PlanEntries), StateSchema.PlanEntries),
        (nameof(StateSchema.PlanPresent), StateSchema.PlanPresent),
        (nameof(StateSchema.PlanSaved), StateSchema.PlanSaved),
        (nameof(StateSchema.ClassifiedInsert), StateSchema.ClassifiedInsert),
        (nameof(StateSchema.ClassifiedNextDepth), StateSchema.ClassifiedNextDepth),
        (nameof(StateSchema.ClassifiedRemoved), StateSchema.ClassifiedRemoved),
        (nameof(StateSchema.ClassifiedHasSurvivor), StateSchema.ClassifiedHasSurvivor),
        (nameof(StateSchema.ClassifiedHasSelected), StateSchema.ClassifiedHasSelected),
        (nameof(StateSchema.ClassifiedSelect), StateSchema.ClassifiedSelect),
        (nameof(StateSchema.FrozenEvidence), StateSchema.FrozenEvidence),
        (nameof(StateSchema.ClassifiedSurvivors), StateSchema.ClassifiedSurvivors),
        (nameof(StateSchema.ClassifiedSelectedFirst), StateSchema.ClassifiedSelectedFirst),
        (nameof(StateSchema.ClassifiedSelected), StateSchema.ClassifiedSelected),
    ];

    /// <summary>Every plan DDL/DML statement writes only the private scan_state scratch schema.</summary>
    private static void PlanWriteFence()
    {
        foreach (var (name, sql) in PlanStatementInventory)
            Check.Require(OnlyScanStateWrites(sql), "plan statements write only scan_state: " + name);

        Check.Require(!OnlyScanStateWrites("DELETE FROM durable_state.synced_entry WHERE pair_id = $p;"),
            "the fence rejects a deliberately durable write");
        Check.Require(!OnlyScanStateWrites("UPDATE branch SET selected = 1 WHERE id = $id;"),
            "the fence rejects an unqualified write that would land in main");
        Check.Require(!OnlyScanStateWrites("INSERT OR REPLACE INTO durable_state.pair(pair_id, def_json) VALUES($p, $def);"),
            "the fence rejects INSERT OR REPLACE into the durable schema");
        Check.Require(!OnlyScanStateWrites("REPLACE INTO durable_state.pair(pair_id, def_json) VALUES($p, $def);"),
            "the fence rejects REPLACE INTO the durable schema");
        Check.Require(!OnlyScanStateWrites("DELETE FROM \"durable_state\".\"pair\" WHERE pair_id = $p;"),
            "the fence rejects a quoted durable write target");
    }

    /// <summary>Every plan read, anti-join, order and guard statement seeks its indexes without statistics.</summary>
    private static void PlanStatements()
    {
        var durable = Path.Combine(Path.GetTempPath(),
            "query-plan-" + Guid.NewGuid().ToString("N") + StateSchema.DatabaseExtension);
        try
        {
            using (var setup = StateStore.Connect(durable, SqliteOpenMode.ReadWriteCreate))
            using (var create = setup.CreateCommand())
            {
                create.CommandText = StateSchema.Create + StateSchema.SchemaAdditions + StateSchema.DeletedHistorySchema + StateSchema.RebuildIssues + StateSchema.DeletedHistoryBlocks + StateSchema.DropDeletedHistoryNames + StateSchema.SpentDeletedHistory + StateSchema.LocalPublishedColumns + StateSchema.DiscoveryRequiredColumn + StateSchema.IssuePathKeyIndex + StateSchema.RenameContinuationColumns + StateSchema.PublishVersion;
                create.ExecuteNonQuery();
            }

            using var connection = StateStore.Connect(StateSchema.ReadRoot, SqliteOpenMode.ReadWriteCreate);
            using (var attach = connection.CreateCommand())
            {
                attach.CommandText = StateSchema.AttachState;
                attach.Parameters.AddWithValue(StateSchema.ParamPath, durable);
                attach.ExecuteNonQuery();
            }
            using (var attach = connection.CreateCommand())
            {
                attach.CommandText = StateSchema.AttachScan;
                attach.Parameters.AddWithValue(StateSchema.ParamPath, ":memory:");
                attach.ExecuteNonQuery();
            }
            connection.CreateFunction(StateSchema.PlanDepthFunction,
                (string path) => PathNames.Depth(path), isDeterministic: true);

            using (var create = connection.CreateCommand())
            {
                create.CommandText = StateSchema.CreateScan + StateSchema.PlanCreateClassified +
                    StateSchema.PlanCreateClassifiedLocalRoots;
                create.ExecuteNonQuery();
                create.CommandText = "SELECT count(*) FROM durable_state.sqlite_master WHERE name LIKE 'sqlite_stat%';";
                Check.Require((long)create.ExecuteScalar()! == 0, "plan statements are checked without ANALYZE statistics");
            }

            foreach (var sql in new[]
            {
                StateSchema.PlanEntries, StateSchema.PlanPresent, StateSchema.PlanSaved,
                StateSchema.ClassifiedNextDepth, StateSchema.ClassifiedRemoved, StateSchema.ClassifiedHasSurvivor,
                StateSchema.ClassifiedHasSelected, StateSchema.ClassifiedSelected, StateSchema.ClassifiedSurvivors,
            })
                CheckPlan(connection, sql);

            // Only the first selected page has no cursor range to seek.
            CheckPlan(connection, StateSchema.ClassifiedSelectedFirst, fullBranchOrder: true);
            Check.Require(Rejects(connection, StateSchema.ClassifiedSelectedFirst),
                "the plan fence rejects a full branch-order scan without the first-page exception");

            // A pair-wide search must fail, so the keyed-search rule cannot regress to accepting one.
            Check.Require(Rejects(connection,
                "SELECT local_path FROM durable_state.synced_entry INDEXED BY synced_local_id WHERE pair_id=$p;"),
                "the plan fence rejects a pair-wide search");

            // Each deleting side owns exactly one classified-root index pair; both variants must be valid DDL.
            using (var roots = connection.CreateCommand())
            {
                roots.CommandText = "DROP INDEX scan_state.classified_local_root; DROP INDEX scan_state.classified_dropbox_root;" +
                    StateSchema.PlanCreateClassifiedDropboxRoots;
                roots.ExecuteNonQuery();
            }
        }
        finally
        {
            File.Delete(durable);
        }
    }

    private static void CheckPlan(SqliteConnection connection, string sql, bool fullBranchOrder = false)
    {
        var plan = Plan(connection, sql);
        Check.Require(plan.All(line => !line.Contains("USE TEMP B-TREE", StringComparison.Ordinal)),
            "plan statements order through an index, never a read-time sort: " + string.Join("; ", plan));
        foreach (var line in plan.Where(line => line.StartsWith("SCAN ", StringComparison.Ordinal)))
            Check.Require(line == "SCAN CONSTANT ROW" || line == "SCAN next_saved" || line == "SCAN same_depth" ||
                (fullBranchOrder && line.StartsWith("SCAN scan_state.classified USING INDEX classified_selected_order", StringComparison.Ordinal)) ||
                IsCoRoutineRead(plan, line),
                "plan statements seek their tables instead of scanning them: " + line);
        foreach (var line in plan.Where(line => line.StartsWith("SEARCH ", StringComparison.Ordinal)))
            Check.Require(IsKeyedSearch(line),
                "plan statements constrain a full key or range: " + line);
    }

    private static bool OnlyScanStateWrites(string sql)
    {
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!WriteVerb.IsMatch(statement)) continue;
            var match = WriteTarget.Match(statement);
            if (!match.Success || !match.Groups[1].Value.StartsWith("scan_state.", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool IsTableAccess(string line) =>
        line.StartsWith("SEARCH ", StringComparison.Ordinal) ||
        line.StartsWith("SCAN ", StringComparison.Ordinal) && line != "SCAN CONSTANT ROW";

    private static bool IsLookup(string line, string key) =>
        line.StartsWith("SEARCH ", StringComparison.Ordinal) && line.Contains("pair_id=?", StringComparison.Ordinal) &&
        (line.Contains($"AND {key}=?", StringComparison.Ordinal) ||
         line.Contains($"AND {key}>? AND {key}<?", StringComparison.Ordinal));

    // The statement's own program is the plan without the rows a declarative referential action appends.
    private static (List<string> Parent, List<string> Referential) PlanParts(SqliteConnection connection, string sql)
    {
        var withReferences = PlanRows(connection, sql, foreignKeys: true);
        var withoutReferences = PlanRows(connection, sql, foreignKeys: false);
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in withoutReferences)
            remaining[line] = remaining.GetValueOrDefault(line) + 1;
        var parent = new List<string>(withReferences.Count);
        var referential = new List<string>();
        foreach (var line in withReferences)
        {
            if (remaining.TryGetValue(line, out var count) && count > 0)
            {
                remaining[line] = count - 1;
                parent.Add(line);
            }
            else referential.Add(line);
        }
        Check.Require(parent.Count == withoutReferences.Count,
            "foreign keys add only a referential program to the statement plan: " + sql);
        foreach (var line in referential.Where(IsTableAccess))
            Check.Require(line.StartsWith("SEARCH ", StringComparison.Ordinal) &&
                line.Contains("INDEX ", StringComparison.Ordinal) && line.Contains("=?", StringComparison.Ordinal),
                "a referential action seeks its child index instead of scanning the child: " + line);
        return (parent, referential);
    }

    private static List<string> PlanRows(SqliteConnection connection, string sql, bool foreignKeys)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = foreignKeys ? "PRAGMA foreign_keys=ON;" : "PRAGMA foreign_keys=OFF;";
            pragma.ExecuteNonQuery();
        }
        var plan = new List<string>();
        // EXPLAIN applies to one statement, so inspect both deletes in compound commands.
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + statement;
            foreach (var parameter in typeof(StateSchema).GetFields().Where(field =>
                field.Name.StartsWith("Param", StringComparison.Ordinal) ||
                field.Name.StartsWith("PlanParam", StringComparison.Ordinal)))
                command.Parameters.AddWithValue((string)parameter.GetRawConstantValue()!, "A");
            foreach (Match parameter in Regex.Matches(statement, @"\$claim\d+_\w+"))
                if (!command.Parameters.Contains(parameter.Value)) command.Parameters.AddWithValue(parameter.Value, "A");
            using var reader = command.ExecuteReader();
            var before = plan.Count;
            while (reader.Read()) plan.Add(reader.GetString(3));
            Check.Require(plan.Count != before, "the statement has a query plan");
        }
        return plan;
    }

    private static List<string> Plan(SqliteConnection connection, string sql, bool keepPair = false)
    {
        var (parent, _) = PlanParts(connection, sql);
        return parent.Where(line => !line.Contains("SCALAR SUBQUERY", StringComparison.Ordinal) &&
            (keepPair || !IsPairLookup(line))).ToList();
    }

    private static List<string> ReferentialPlan(SqliteConnection connection, string sql) =>
        PlanParts(connection, sql).Referential;

    private static bool IsPairLookup(string line) =>
        line.StartsWith("SEARCH pair USING ", StringComparison.Ordinal) ||
        line.StartsWith("SEARCH durable_state.pair USING ", StringComparison.Ordinal);

    // A keyed search constrains more than the pair id: a key equality, a key range or a rowid.
    private static bool IsKeyedSearch(string line) =>
        line.Contains("<expr>", StringComparison.Ordinal) || line.Contains("key_delta", StringComparison.Ordinal) ||
        line.Contains("rowid=?", StringComparison.Ordinal) || line.Contains("policy_key", StringComparison.Ordinal) ||
        line.Contains("native_key", StringComparison.Ordinal) || line.Contains("local_id=?", StringComparison.Ordinal) ||
        line.Contains("dropbox_id=?", StringComparison.Ordinal);

    // A co-routine read only streams rows its subquery produced; those accesses are their own checked plan lines.
    private static bool IsCoRoutineRead(List<string> plan, string line) =>
        line.StartsWith("SCAN ", StringComparison.Ordinal) &&
        plan.Contains("CO-ROUTINE " + line["SCAN ".Length..], StringComparer.Ordinal);

    private static bool Rejects(SqliteConnection connection, string sql)
    {
        try { CheckPlan(connection, sql); return false; }
        catch (InvalidOperationException) { return true; }
    }
}
