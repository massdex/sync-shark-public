using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class SettledWorkLifetimeChecks
{
    internal static async Task RunRegistrationAsync(string root,bool successorHistoryOnly=false,bool successorClosureOnly=false,bool successorGroupOnly=false,bool successorCompletionOnly=false)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh action lifetime fixture.");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "authority.db");
        var db = new SyncAuthorityDatabase(path);
        await db.InitializeAsync("lifetime-install", "profile", "fixture", "{}");
        await db.RegisterPairAsync("pair", 1);
        await db.RegisterRootBindingAsync("local-root", "pair", "local", 1,
            """{"volumeSerial":"00000001","fileId":"root"}""", root);
        await db.RegisterRootBindingAsync("dropbox-root", "pair", "dropbox", 1,
            """{"folderId":"id:root"}""", "/Fixture");
        var scans = new Dictionary<string, string>();
        foreach (var side in new[] { "local", "dropbox" })
        {
            var scan = await db.BeginScanGenerationAsync("pair", side + "-root", side, "manual", "{}");
            await db.AppendObservationEntriesAsync(scan, new[] { "file", "manual", "automatic", "pending-auto", "delete", "reconciled-delete" }.Select(name =>
                new SyncAuthorityObservationInput(name + ".txt", "file", side == "local" ? JsonSerializer.Serialize(new { volumeSerial="00000001",fileId=name,lastWriteUtc="2026-01-01T00:00:00Z" }) : JsonSerializer.Serialize(new { fileId="id:"+name,revision="1" }),
                    new string('a',64),4,"{}")).ToArray());
            await db.CompleteScanGenerationAsync(scan);
            scans[side] = scan.ScanGenerationId;
        }
        var set = await db.LoadPlanningObservationSetAsync("pair", 1, "local-root", "dropbox-root",
            scans["local"], scans["dropbox"]);
        var local = set.Local.Single(x => x.RelativePath == "file.txt");
        var remote = set.Dropbox.Single(x => x.RelativePath == "file.txt");
        var authority = set.Bind(SyncAuthorityReconciliationPlanner.ObservationSetIdentity(set), local.ObjectId);
        var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", "pair", null,
            null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        if(successorGroupOnly)await db.RegisterOperationGroupAsync("successor-group","compound","{}");
        var ticket = await db.ReserveActionAsync("first-intent", admission);
        SyncAuthorityOperationPlan Plan(SyncAuthorityActionRegistration registration,
            string key, SyncAuthorityPlanningAuthority? current = null) => new(registration.OperationId, key,
            SyncAuthorityOperationKind.UpdateRemote, "pair", local.ObjectId, 1, "profile", "{}", 50,
            [new(registration.OperationId + "-source", "pair", local.ObjectId, "source", "local-root",
                local.MembershipId, local.ObjectVersionId, local.MembershipGeneration, "file.txt", "{}"),
             new(registration.OperationId + "-destination", "pair", remote.ObjectId, "destination", "dropbox-root",
                remote.MembershipId, remote.ObjectVersionId, remote.MembershipGeneration, "file.txt", "{}")],
            [], [], OperationGroupId:successorGroupOnly?"successor-group":null, PlanningAuthorities: [current ?? authority]);
        var plan = Plan(ticket, "first-intent");
        await Refuses(() => db.CreateOperationAsync(plan, admission), "bare missing ID does not create an action");
        await db.CreateOperationAsync(plan, admission, reservation: ticket.Reservation);
        Require(await Scalar(path, "SELECT next_action_sequence FROM installation;") == 2,
            "one committed registration advances the installation sequence exactly once");
        await db.CreateOperationAsync(plan, admission);
        Require(await Scalar(path, "SELECT count(*) FROM operation;") == 1,
            "exact retained replay creates no second action");
        var changedMember = plan.Members[0] with { ExpectedStateJson = """{"different":true}""" };
        await Refuses(() => db.CreateOperationAsync(plan with { Members = [changedMember, plan.Members[1]] }, admission),
            "retained replay compares immutable members, not only its header");
        if(successorGroupOnly)await db.MarkOperationGroupActiveAsync("successor-group");
        var lease = await db.AcquireOperationLeaseAsync(ticket.OperationId, "fixture-worker", TimeSpan.FromMinutes(1));
        await Refuses(() => db.CloseOrdinaryCommandIssuanceAsync(admission.CommandRequestId),
            "ordinary issuance does not close across an active worker lease");
        var attempt = await db.StartDispatchedAttemptAsync(ticket.OperationId, lease, new string('b', 64), "fixture-update");
        await db.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.KnownSuccess,
            "{}", "{}", "{}", new string('c', 64));
        await db.CommitSuccessfulAttemptAsync(attempt.AttemptId, lease, "completed-point", "pair", local.ObjectId,
            local.ObjectVersionId, remote.ObjectVersionId, local.MembershipId, remote.MembershipId, "{}");
        await db.CompleteOperationAsync(lease, "{}");
        Require(!(await db.CloseSettledActionAsync(ticket.OperationId)).Closed,
            "an active worker lease blocks action closure even after completion");
        await db.ReleaseLeaseAsync(lease);
        if(successorCompletionOnly)
            Require(await Scalar(path,"SELECT count(*) FROM operation WHERE completion_state_json IS NOT NULL AND closure_json IS NULL;")==1,
                "actual commit retains a completion snapshot before terminal closure");
        else
        Require((await db.CloseSettledActionAsync(ticket.OperationId)).Closed,
            "exact committed successor, receipts and released worker bind a closure certificate");

        Require(await Scalar(path, "SELECT count(*) FROM history_event WHERE producer_kind='lifecycle' AND operation_id IS NULL;") == 2,
            "new committed and completed lifecycle events retain snapshots without action foreign keys");
        Require(await Scalar(path, "SELECT count(*) FROM history_event WHERE producer_kind='lifecycle' AND retention_class='ordinary' AND encoded_bytes>0;") == 2,
            "lifecycle snapshots are charged to the existing bounded ordinary history budget");
        if(successorHistoryOnly)
        {
            await db.CloseOrdinaryCommandIssuanceAsync(admission.CommandRequestId);
            await db.TransitionCommandRequestAsync(admission.CommandRequestId,"completed","{}");
            var expected=(await db.LoadActivityProjectionPageAsync(query:new(DetailedLog:true,OldestFirst:true)))
                .Where(x=>x.OperationId==ticket.OperationId&&x.Phase==SyncActivityPhase.Completed).ToArray();
            Require(expected.Length==1,"actual completion writer produced one exact detached completed lifecycle snapshot");
            await using(var c=new SqliteConnection("Data Source="+path+";Pooling=False"))
            {
                await c.OpenAsync();await using var q=c.CreateCommand();
                q.CommandText="PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";await q.ExecuteNonQueryAsync();
            }
            var before=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            var options=new SyncEssentialSuccessorOptions(root);
            var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);
            var targetPath=Path.Combine(root,"successor.db");
            var result=await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,targetPath,identity,options);
            await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(path,targetPath,identity,options);
            Require(result.Tables.Single(x=>x.Table=="operation").SelectedRows==0 &&
                result.Tables.Single(x=>x.Table=="command_request").SelectedRows==0 &&
                result.Tables.Single(x=>x.Table=="history_event").SelectedRows==1,
                "completed operation display survives without importing old command/action graph");
            var target=new SyncAuthorityDatabase(targetPath);
            Require(JsonSerializer.Serialize(await target.LoadActivityProjectionPageAsync(query:new(DetailedLog:true,OldestFirst:true)))==
                JsonSerializer.Serialize(expected),
                "real reader preserves completed action snapshot, roots and size after journal omission");
            var rejected=false;
            await using(var c=new SqliteConnection("Data Source="+targetPath+";Pooling=False"))
            {
                await c.OpenAsync();await using var q=c.CreateCommand();
                q.CommandText="""
                    INSERT INTO history_event(rowid,history_event_id,event_kind,detail_json,occurred_utc,
                        producer_command_id,producer_ordinal,retention_class,encoded_bytes,expires_utc,producer_kind)
                    SELECT next_history_sequence,'forged-lifecycle','presentation','{"OperationSnapshot":{}}',
                        '2026-10-13T00:00:00.0000000Z','unretained-command',next_history_sequence,'ordinary',1,
                        '2027-10-13T00:00:00.0000000Z','lifecycle' FROM installation;
                    """;
                try{await q.ExecuteNonQueryAsync();}
                catch(SqliteException error)when(error.Message.Contains("exact retained command and sequence",StringComparison.Ordinal))
                {rejected=true;}
            }
            Require(rejected,"restored lifecycle guard rejects a forged producer outside import");
            Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))==before,
                "original completed action and history remain byte-exact");
            return;
        }
        var activityWriter = await db.OpenRunActivityWriterAsync(admission);
        for (var ordinal = 1; ordinal <= 5; ordinal++)
            await db.AppendRunActivityAsync([activityWriter.Enqueue(new SyncActivityEvent("",
                DateTimeOffset.UtcNow, [SyncActivityCategories.Sync], "Information", "Run", "Progress",
                "Snapshot ordinal isolation", PairId: "pair", RunId: admission.CommandRequestId))]);
        Require(await Scalar(path, """
            SELECT count(*) FROM history_event l JOIN history_event r
              ON r.producer_command_id=l.producer_command_id AND r.producer_ordinal=l.producer_ordinal
            WHERE l.producer_kind='lifecycle' AND r.producer_kind='run';
            """) == 2, "run and lifecycle producer ordinals can coincide without collisions");
        await using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource=path, Mode=SqliteOpenMode.ReadWrite, Pooling=false }.ToString()))
        {
            await writer.OpenAsync();
            await using var alter = writer.CreateCommand();
            alter.CommandText = "UPDATE root_binding SET display_path='later display root' WHERE root_binding_id='local-root';";
            await alter.ExecuteNonQueryAsync();
        }
        var displayed = (await db.LoadActivityProjectionAsync(100))
            .Where(x => x.OperationId == ticket.OperationId).ToArray();
        Require(displayed.Length == 2 && displayed.All(x => x.RelativePath == "file.txt" && x.SizeBytes == 4),
            "lifecycle projection preserves the exact historical path and size");
        Require(displayed.All(x => x.LocalRoot == root),
            "a later root display change cannot rewrite snapshot history");
        await db.CloseRunActivityWriterAsync(activityWriter);
        var freshForStale = await db.ReserveActionAsync("stale-second-intent", admission);
        await Refuses(() => db.CreateOperationAsync(Plan(freshForStale, "stale-second-intent"), admission,
                reservation: freshForStale.Reservation),
            "a new GUID and active run cannot reuse the plan after its completion changed the baseline");
        Require(await Scalar(path, "SELECT next_action_sequence FROM installation;") == 2,
            "rejected stale authority spends no registration sequence");
        await db.CreateOperationAsync(plan, admission);
        Require((await db.LoadOperationAsync(ticket.OperationId)).Attempts.Count == 1,
            "completed exact replay retains its one attempt and does not issue another");

        var current = await db.LoadPlanningObservationSetAsync("pair", 1, "local-root", "dropbox-root",
            scans["local"], scans["dropbox"]);
        var currentAuthority = current.Bind(SyncAuthorityReconciliationPlanner.ObservationSetIdentity(current), local.ObjectId);
        var rollback = await db.ReserveActionAsync("rollback-intent", admission);
        var rollbackPlan = Plan(rollback, "rollback-intent", currentAuthority);
        var absentObject = rollbackPlan.Members[0] with { ObjectId = "missing-object" };
        await Refuses(() => db.CreateOperationAsync(rollbackPlan with { Members = [absentObject, rollbackPlan.Members[1]] },
                admission, reservation: rollback.Reservation), "foreign-key failure rolls back the new action");
        Require(!await db.IsOperationRetainedAsync(rollback.OperationId, default), "rolled-back action is absent");
        await Refuses(() => db.CreateOperationAsync(rollbackPlan, admission, reservation: rollback.Reservation),
            "a spent reservation cannot recreate its missing row after rollback");
        var independentlyFresh = await db.ReserveActionAsync("fresh-after-rollback", admission);
        await db.CreateOperationAsync(Plan(independentlyFresh, "fresh-after-rollback", currentAuthority) with {
            Dependencies = successorGroupOnly?[]:[new(ticket.OperationId, "must_complete")] },
            admission, reservation: independentlyFresh.Reservation);
        Require(independentlyFresh.OperationId != rollback.OperationId &&
            await Scalar(path, "SELECT max(registration_sequence) FROM operation;") == 2,
            "independent valid work receives a distinct identity and next committed sequence");
        Require(await Scalar(path, "SELECT count(*) FROM effect_receipt;") == 1,
            "receipts remain present until verified action retirement");
        if(successorClosureOnly||successorGroupOnly||successorCompletionOnly)
        {
            await using(var c=new SqliteConnection("Data Source="+path+";Pooling=False"))
            {
                await c.OpenAsync();await using var q=c.CreateCommand();
                q.CommandText="PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";await q.ExecuteNonQueryAsync();
            }
            var before=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            var options=new SyncEssentialSuccessorOptions(root);
            var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);
            var targetPath=Path.Combine(root,"successor.db");
            var report=await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,targetPath,identity,options);
            await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(path,targetPath,identity,options);
            var target=new SyncAuthorityDatabase(targetPath);
            Require(report.Tables.Single(x=>x.Table=="operation").SelectedRows==2,
                "unfinished child retains its exact closed completed prerequisite");
            if(successorGroupOnly)
            {
                Require(await Scalar(targetPath,"SELECT count(*) FROM operation_group WHERE status='active';")==1&&
                    await Scalar(targetPath,"SELECT count(*) FROM operation_dependency;")==0,
                    "required group retains its completed sibling without a prerequisite edge");
                await Refuses(()=>target.CompleteOperationGroupAsync("successor-group"),
                    "retained active group cannot complete while its exact remaining child is unfinished");
            }
            else
            {
            Require(await Scalar(targetPath,"SELECT count(*) FROM operation_dependency;")==1&&
                await target.LoadUnfinishedDependencyAsync(independentlyFresh.OperationId,default) is null,
                "production dependency view recognizes retained original completion");
            }
            if(successorCompletionOnly)
            {
                Require(await Scalar(targetPath,"SELECT action_closed_count FROM installation;")==0 &&
                    await Scalar(targetPath,"SELECT count(*) FROM operation WHERE completion_state_json IS NOT NULL AND closure_json IS NULL;")==1,
                    "successor preserves committed state without inventing a closure certificate");
                Require((await target.CloseSettledActionAsync(ticket.OperationId)).Closed,
                    "actual successor maintenance closes the retained completion after original guards pass");
            }
            Require(await Scalar(targetPath,"SELECT action_closed_count FROM installation;")==1,
                "successor closed-action accounting derives from its selected graph");
            await Refuses(()=>target.ReserveActionAsync("stale-successor-admission",admission),
                "retained historical command cannot grant new successor work");
            await Refuses(()=>target.StartDispatchedAttemptAsync(ticket.OperationId,lease,new string('d',64),"stale-lease"),
                "retained released source lease cannot dispatch an effect");
            await target.EnableActivityRetentionForTestAsync();
            target.ActivityRetentionClockForTest=()=>DateTimeOffset.UtcNow.AddDays(8);
            var retirement=await target.RetireSettledActionBatchAsync();
            Require(retirement.RetiredActions==1&&!await target.IsOperationRetainedAsync(ticket.OperationId,default),
                "actual successor maintenance verifies and retires the original closed graph");
            if(successorGroupOnly)
                await Refuses(()=>target.CompleteOperationGroupAsync("successor-group"),
                    "group still refuses premature completion after valid sibling retirement");
            else
            {
            Require(await Scalar(targetPath,"SELECT count(*) FROM operation_dependency WHERE terminal_proof_json IS NOT NULL;")==1&&
                await target.LoadUnfinishedDependencyAsync(independentlyFresh.OperationId,default) is null,
                "real dependency compaction preserves completed prerequisite after graph retirement");
            }
            Require(await Scalar(targetPath,"SELECT action_closed_count FROM installation;")==0&&
                await Scalar(targetPath,"SELECT action_closed_bytes FROM installation;")==0,
                "actual retirement drains successor closed-action counters exactly");
            Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))==before,
                "original closed action and prerequisite graph remain byte-exact");
            return;
        }

        await Refuses(async () =>
        {
            await using var direct = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource=path, Mode=SqliteOpenMode.ReadWrite, Pooling=false }.ToString());
            await direct.OpenAsync(); await using var deletion = direct.CreateCommand();
            deletion.CommandText = "DELETE FROM operation_member WHERE operation_id=$id;";
            deletion.Parameters.AddWithValue("$id", ticket.OperationId);
            await deletion.ExecuteNonQueryAsync();
        }, "ordinary SQL cannot delete a closed action's immutable members");
        await db.EnableActivityRetentionForTestAsync();
        db.ActivityRetentionClockForTest = () => DateTimeOffset.UtcNow.AddDays(8);
        db.ActionRetirementBoundaryForTest = phase => {
            if (phase == "before-action-retirement-commit")
                throw new InvalidOperationException("Synthetic crash before commit");
        };
        await Refuses(() => db.RetireSettledActionBatchAsync(), "a crash before maintenance commit rolls back its graph changes");
        Require(await db.IsOperationRetainedAsync(ticket.OperationId, default) &&
            await Scalar(path, "SELECT count(*) FROM effect_receipt;") == 1 &&
            await Scalar(path, "SELECT action_retired_count FROM installation;") == 0,
            "rollback keeps the complete action, receipt and accounting");
        var delayedMaintenancePreflight=false;
        db.ActionRetirementBoundaryForTest = phase=>
        {
            if(phase=="after-action-maintenance-preflight"&&!delayedMaintenancePreflight)
            {
                delayedMaintenancePreflight=true;
                Thread.Sleep(75); // Reproduce slow checkout without changing production clocks.
            }
        };
        for (var batch = 0; batch < 8 && await db.IsOperationRetainedAsync(ticket.OperationId, default); batch++)
        {
            var result = await db.RetireSettledActionBatchAsync();
            Require(result.RetiredActions <= 200 && result.RetiredEncodedBytes <= 1_048_576 && result.ProcessedEncodedBytes <= 1_048_576,
                "each action batch respects the row and encoded-payload bounds");
        }
        db.ActionRetirementBoundaryForTest=null;
        Require(delayedMaintenancePreflight&&!await db.IsOperationRetainedAsync(ticket.OperationId, default) &&
            await Scalar(path, "SELECT action_retired_count FROM installation;") == 1,
            "the expired resolved action graph is collected exactly once despite preflight exceeding the cooperative time budget");
        Require(await Scalar(path, "SELECT count(*) FROM effect_receipt;") == 0 &&
            await Scalar(path, "SELECT action_closed_bytes FROM installation;") == 0,
            "retirement releases its receipts and exact closed-detail charge");
        await Refuses(() => db.CreateOperationAsync(plan, admission, reservation: ticket.Reservation),
            "a spent ticket cannot revive its collected action while the originating command is still active");
        var replayTicket = await db.ReserveActionAsync("first-intent", admission);
        await Refuses(() => db.CreateOperationAsync(Plan(replayTicket, "first-intent"), admission,
                reservation: replayTicket.Reservation),
            "even a fresh ID cannot replay the retired action's stale pre-completion authority");
        var retainedHistory = (await db.LoadActivityProjectionAsync(100))
            .Where(x => x.OperationId == ticket.OperationId).ToArray();
        Require(retainedHistory.Length == 2 && retainedHistory.All(x =>
            x.RelativePath == "file.txt" && x.SizeBytes == 4 && x.LocalRoot == root),
            "retiring journal details preserves detached history with its historical path, size and root");
        Require(await db.IsOperationRetainedAsync(independentlyFresh.OperationId, default),
            "unrelated active work is not collected with a completed sibling");
        Require(await Scalar(path, "SELECT count(*) FROM operation_dependency WHERE depends_on_operation_id IS NULL AND terminal_proof_sha256 IS NOT NULL;") == 1 &&
            (await db.LoadOperationAsync(independentlyFresh.OperationId)).Dependencies.Single().DependsOnOperationId == ticket.OperationId,
            "the surviving dependency retains the exact retired predecessor and typed terminal proof");
        Require(await db.LoadUnfinishedDependencyAsync(independentlyFresh.OperationId, default) is null,
            "the recovery dependency reader accepts the verified compact completion proof");
        var dependentLease = await db.AcquireOperationLeaseAsync(independentlyFresh.OperationId,
            "compact-dependency-worker", TimeSpan.FromMinutes(1));
        Require(true, "real ordinary lease admission accepts the same verified terminal prerequisite");
        var unknownAttempt = await db.StartDispatchedAttemptAsync(independentlyFresh.OperationId,
            dependentLease, new string('d', 64), "fixture-unknown-outcome");
        await db.RecordReceiptAsync(unknownAttempt.AttemptId, dependentLease,
            SyncAuthorityReceiptCertainty.Unknown, "{}", "{}", "{}", new string('e', 64));
        await db.ReleaseLeaseAsync(dependentLease);
        Require(!(await db.CloseSettledActionAsync(independentlyFresh.OperationId)).Closed,
            "an unknown issued outcome cannot receive a settled closure certificate");
        db.ActivityRetentionClockForTest = () => DateTimeOffset.UtcNow.AddDays(800);
        for (var sweep = 0; sweep < 3; sweep++) await db.RetireSettledActionBatchAsync();
        Require(await db.IsOperationRetainedAsync(independentlyFresh.OperationId, default) &&
            await Scalar(path, "SELECT count(*) FROM effect_receipt WHERE certainty='unknown';") == 1,
            "800 days and repeated maintenance preserve the exact unknown action and receipt");
        await Refuses(async () =>
        {
            await using var direct = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource=path, Mode=SqliteOpenMode.ReadWrite, Pooling=false }.ToString());
            await direct.OpenAsync(); await using var forge = direct.CreateCommand();
            forge.CommandText = "UPDATE operation_dependency SET terminal_proof_json='{}' WHERE retired_dependency_id IS NOT NULL;";
            await forge.ExecuteNonQueryAsync();
        }, "ordinary SQL cannot replace a compact terminal prerequisite");

        await db.CloseOrdinaryCommandIssuanceAsync(admission.CommandRequestId);
        Require(await Scalar(path, "SELECT ordinary_closed_command_floor FROM installation;") == admission.AdmissionSequence,
            "drained ordinary command advances its floor while retained action and receipt facts remain");
        await Refuses(() => db.ReserveActionAsync("late-callback", admission),
            "late callback cannot reserve work after explicit ordinary closure");
        Require(await db.IsOperationRetainedAsync(independentlyFresh.OperationId, default),
            "ordinary command closure preserves unresolved action detail");
        await Refuses(() => db.AcquireOperationLeaseAsync(independentlyFresh.OperationId, "late-dispatch",
                TimeSpan.FromMinutes(1)),
            "a closed ordinary command cannot grant a new normal-effect lease to its unknown action");
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "completed");
        var next = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", "pair", null, null,
            "{}", SyncAuthorityCommandPurpose.NormalEffect);
        var nextAction = await db.ReserveActionAsync("next-run-intent", next);
        Require(nextAction.OperationId != ticket.OperationId && next.AdmissionSequence > admission.AdmissionSequence,
            "legitimate subsequent command remains admissible above the closed floor");
        await AssertJournalAccountingAsync(db, path, "after collection, dependency compaction and unknown receipt");
        var journal = await db.LoadActionJournalStatusAsync("pair");
        Require(journal.SafetyHeldActions == 1 && journal.SafetyHeldEncodedBytes > 0 && !journal.AccountingPending,
            "the exact unknown action is measured and held after its ordinary command closes");
        db.ActionJournalWarningBytesForTest = 1;
        Require((await db.LoadActionJournalStatusAsync("pair")) is { Warning: true },
            "the warning threshold does not itself pause automatic work");

        SyncAuthorityOperationPlan BudgetPlan(SyncAuthorityActionRegistration registration, string key, string relativePath)
        {
            var source = current.Local.Single(x => x.RelativePath == relativePath);
            var destination = current.Dropbox.Single(x => x.RelativePath == relativePath);
            return Plan(registration,key,currentAuthority) with {
                ObjectId=source.ObjectId,
                Members=[
                    new(registration.OperationId+"-source","pair",source.ObjectId,"source","local-root",
                        source.MembershipId,source.ObjectVersionId,source.MembershipGeneration,relativePath,"{}"),
                    new(registration.OperationId+"-destination","pair",destination.ObjectId,"destination","dropbox-root",
                        destination.MembershipId,destination.ObjectVersionId,destination.MembershipGeneration,relativePath,"{}")],
                PlanningAuthorities=[current.Bind(SyncAuthorityReconciliationPlanner.ObservationSetIdentity(current),source.ObjectId)]
            };
        }
        await db.CreateOperationAsync(BudgetPlan(nextAction, "next-run-intent", "manual.txt"), next,
            reservation: nextAction.Reservation);
        var manualLease = await db.AcquireOperationLeaseAsync(nextAction.OperationId, "manual-over-budget", TimeSpan.FromMinutes(1));
        await db.ReleaseLeaseAsync(manualLease);
        Require(true, "manual work remains admitted with a journal warning");

        var automatic = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", "pair", null, null,
            """{"automatic":true,"trigger":"Monitor"}""", SyncAuthorityCommandPurpose.NormalEffect);
        var autoAction = await db.ReserveActionAsync("automatic-budget", automatic);
        await db.CreateOperationAsync(BudgetPlan(autoAction, "automatic-budget", "automatic.txt"), automatic,
            reservation: autoAction.Reservation);
        // This bounded input fixture supplies a measured-counter value above the
        // former 256MiB stop. Byte-charge correctness is proved independently above;
        // no 256MiB payload is allocated merely to test the removed admission predicate.
        var free = new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;
        Require(free > 16_777_216, "the isolated authority has physical capacity for its bounded receipts and rollback");
        await using (var input = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource=path,Mode=SqliteOpenMode.ReadWrite,Pooling=false }.ToString()))
        {
            await input.OpenAsync();await using var supply = input.CreateCommand();
            supply.CommandText = "UPDATE operation SET journal_encoded_bytes=journal_encoded_bytes+268435457 WHERE operation_id=$id;";
            supply.Parameters.AddWithValue("$id",nextAction.OperationId);await supply.ExecuteNonQueryAsync();
        }
        Require((await db.LoadActionJournalStatusAsync("pair")).SafetyHeldEncodedBytes > 268_435_456,
            "the admission input is explicitly above the former fixed 256MiB journal cap");
        var admittedAutoLease = await db.AcquireOperationLeaseAsync(autoAction.OperationId, "automatic-admitted", TimeSpan.FromMinutes(1));
        var drainAttempt = await db.StartDispatchedAttemptAsync(autoAction.OperationId, admittedAutoLease,
            new string('f',64), "already-admitted-drain");
        await db.RecordReceiptAsync(drainAttempt.AttemptId, admittedAutoLease, SyncAuthorityReceiptCertainty.Unknown,
            """{"name":"雪🦈"}""", "{}", "{}", new string('1',64));
        await db.ReleaseLeaseAsync(admittedAutoLease);
        Require(true, "automatic work with safe authority can dispatch and drain above the former fixed journal cap");
        var recoveryLease = await db.AcquireReconciliationOperationLeaseAsync(independentlyFresh.OperationId,
            "reconcile-over-budget", TimeSpan.FromMinutes(1));
        await db.ReleaseLeaseAsync(recoveryLease);
        Require(true, "exact reconciliation remains admitted with a journal warning");
        var shown = await db.LoadActivityRetentionWindowAsync();
        Require(shown.Actions is { Warning: true, SafetyHeldActions: 3 } &&
            shown.Describe().Contains("do not pause synchronization", StringComparison.OrdinalIgnoreCase),
            "the existing retention projection exposes counts, bytes and nonblocking warnings");
        CliChecks.CheckActionLifetimePresentation(shown);
        await db.RegisterPairAsync("other-pair",1);
        Require(!(await db.LoadActionJournalStatusAsync("other-pair")).Warning,
            "an unrelated pair does not inherit another pair's held-evidence warning");
        // Simulate a retained predecessor row with no measured charge. The real
        // bounded owner transaction must fill it, not treat it as zero bytes.
        await using (var setup = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource=path,Mode=SqliteOpenMode.ReadWrite,Pooling=false }.ToString()))
        {
            await setup.OpenAsync();await using var mark = setup.CreateCommand();
            mark.CommandText = "UPDATE operation SET journal_accounted=0,journal_encoded_bytes=0 WHERE operation_id=$id;";
            mark.Parameters.AddWithValue("$id",nextAction.OperationId);await mark.ExecuteNonQueryAsync();
        }
        Require((await db.LoadActionJournalStatusAsync("pair")).AccountingPending,
            "unmeasured predecessor detail is reported as pending, never zero safety evidence");
        var pendingAuto=await db.ReserveActionAsync("pending-automatic",automatic);
        await db.CreateOperationAsync(BudgetPlan(pendingAuto,"pending-automatic","pending-auto.txt"),automatic,
            reservation:pendingAuto.Reservation);
        var pendingLease = await db.AcquireOperationLeaseAsync(pendingAuto.OperationId,"pending-accounting",TimeSpan.FromMinutes(1));
        var pendingAttempt=await db.StartDispatchedAttemptAsync(pendingAuto.OperationId,pendingLease,new string('5',64),"pending-accounting-effect");
        await db.RecordReceiptAsync(pendingAttempt.AttemptId,pendingLease,SyncAuthorityReceiptCertainty.Unknown,"{}","{}","{}",new string('6',64));
        await db.ReleaseLeaseAsync(pendingLease);
        Require(true,"safe automatic authority dispatches and retains its receipt while historical accounting is pending");
        var uncertainLease=await db.AcquireOperationLeaseAsync(autoAction.OperationId,"uncertain-safety-check",TimeSpan.FromMinutes(1));
        var beforeUnsafe=await Scalar(path,"SELECT count(*) FROM operation_attempt;");
        await Refuses(()=>db.StartDispatchedAttemptAsync(autoAction.OperationId,uncertainLease,
            new string('4',64),"unsafe-repeat"),"an unresolved prior effect still refuses a new dispatch after the storage cap is removed");
        Require(await Scalar(path,"SELECT count(*) FROM operation_attempt;")==beforeUnsafe,
            "the actual uncertainty refusal creates no new attempt");
        await db.ReleaseLeaseAsync(uncertainLease);
        db.ActionRetirementBoundaryForTest = _ => throw new InvalidOperationException("Crash accounting before commit");
        await Refuses(() => db.RetireSettledActionBatchAsync(), "predecessor measurement rolls back on a transaction crash");
        Require((await db.LoadActionJournalStatusAsync("pair")).AccountingPending,
            "rolled-back predecessor accounting remains visibly pending");
        db.ActionRetirementBoundaryForTest = null;
        for(var i=0;i<8 && (await db.LoadActionJournalStatusAsync("pair")).AccountingPending;i++)
            await db.RetireSettledActionBatchAsync();
        Require(!(await db.LoadActionJournalStatusAsync("pair")).AccountingPending,
            "bounded maintenance completes predecessor accounting without an effect");
        await AssertJournalAccountingAsync(db,path,"after predecessor measurement restart");

        foreach (var name in new[] { "delete", "reconciled-delete" })
        {
            var relativePath=name+".txt";
            var selected=current.Local.Single(x=>x.RelativePath==relativePath);
            var remoteDeleted=current.Dropbox.Single(x=>x.RelativePath==relativePath);
            var deletion=await db.ReserveActionAsync(name,next);
            var deletePlan=BudgetPlan(deletion,name,relativePath) with { Kind=SyncAuthorityOperationKind.DeleteLocal };
            await db.CreateOperationAsync(deletePlan,next,reservation:deletion.Reservation);
            await db.RecordTombstoneAsync(name+"-tombstone","pair",selected.ObjectId,"dropbox",
                remoteDeleted.MembershipId,remoteDeleted.ObjectVersionId,2);
            var deleteLease=await db.AcquireOperationLeaseAsync(deletion.OperationId,name+"-worker",TimeSpan.FromMinutes(1));
            var deleteAttempt=await db.StartDispatchedAttemptAsync(deletion.OperationId,deleteLease,new string('2',64),"fixture-deletion");
            await db.RecordReceiptAsync(deleteAttempt.AttemptId,deleteLease,SyncAuthorityReceiptCertainty.KnownSuccess,
                "{}", "{}", "{}",new string('3',64));
            await Refuses(()=>db.CommitSuccessfulDeletionAttemptAsync(deleteAttempt.AttemptId,deleteLease,
                name+"-tombstone","pair",local.ObjectId,"{}"),"deletion closure refuses a mismatched exact tombstone object");
            if(name=="delete")
                await db.CommitSuccessfulDeletionAttemptAsync(deleteAttempt.AttemptId,deleteLease,
                    name+"-tombstone","pair",selected.ObjectId,"{}");
            else
                await db.CommitReconciledDeletionAttemptAsync(deleteAttempt.AttemptId,deleteLease,
                    name+"-tombstone","pair",selected.ObjectId,"{}");
            await db.CompleteOperationAsync(deleteLease,"{}");
            await db.ReleaseLeaseAsync(deleteLease);
            Require((await db.CloseSettledActionAsync(deletion.OperationId)).Closed,
                name+" commit binds the exact resolved tombstone and admits closure after worker drain");
            Require(await Scalar(path,"SELECT count(*) FROM operation WHERE json_extract(completion_state_json,'$.kind')='resolved_tombstone';")>=1,
                name+" retains typed deletion completion without inventing a synchronization point");
        }
        await AssertJournalAccountingAsync(db,path,"after both typed deletion completion paths");
        await CheckUnpublishedControlAdmissionAsync(Path.Combine(root, "unpublished-control"));
        await ConsentLifetimeChecks.RunAsync(Path.Combine(root, "consent-lifetime"));
        await RunSummaryLifetimeChecks.RunAsync(Path.Combine(root, "run-summaries"));
        await ContextLifetimeChecks.RunAsync(Path.Combine(root, "context-lifetime"));
        await ObjectLifetimeChecks.RunAsync(Path.Combine(root, "object-lifetime"));
        await FailedScanClosureChecks.RunAsync(Path.Combine(root, "failed-scans"));
        await InventoryLifetimeChecks.RunAsync(Path.Combine(root,"inventory-lifetime"));
        await SchemaInventoryChecks.RunAsync(Path.Combine(root,"schema-inventory"));
        await WorkWindowChecks.RunAsync(Path.Combine(root,"work-window"));
        await MonitorLifetimeChecks.RunAsync(Path.Combine(root,"monitor-lifetime"));
        await using var check = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await check.OpenAsync();
        await using var query = check.CreateCommand(); query.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await query.ExecuteReaderAsync();
        Require(!await reader.ReadAsync(), "foreign-key integrity remains exact");
        Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", scope = "fresh action registration, drained closure, detached history and exact bounded graph retirement",
            limitation = "Synthetic SQLite proof; complete safety-held budgets, remaining lifetime classes and full product acceptance remain separate." }));
    }

    private static async Task CheckUnpublishedControlAdmissionAsync(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "authority.db");
        var db = new SyncAuthorityDatabase(path);
        await db.InitializeAsync("control-install", "profile", "fixture", "{}");
        await db.RegisterPairAsync("candidate", 1);
        foreach (var side in new[] { "local", "dropbox" })
            await db.RegisterRootBindingAsync(side + "-root", "candidate", side, 1,
                JsonSerializer.Serialize(new { identityKind = "cloud", provider = "dropbox",
                    providerAccountId = side, namespaceId = side, folderId = side }), "/Fixture/" + side);
        await db.RegisterObjectAsync("control-object", "directory");
        async Task Change(string sql)
        {
            await using var c = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            await c.OpenAsync(); await using var q = c.CreateCommand(); q.CommandText = sql;
            await q.ExecuteNonQueryAsync();
        }
        await Change("UPDATE root_binding SET state='blocked';");
        async Task Register(string key, bool setup = true)
        {
            var ticket = await db.PrepareControlActionAsync(key);
            var command = await db.CreateAcceptedCommandAdmissionAsync(null, "reconcile", "candidate",
                "local-root", null, JsonSerializer.Serialize(new {
                    installationId = "control-install", pairId = "candidate", side = "local",
                    operationId = ticket.OperationId, localRootBindingId = "local-root",
                    nativeIdentity = "fixture-parent", configurationRevision = 1,
                    namespaceId = "candidate", allowedOperationKind = "AllocateControl"
                }), setup ? SyncAuthorityCommandPurpose.ControlAllocation : SyncAuthorityCommandPurpose.NormalEffect);
            var plan = new SyncAuthorityOperationPlan(ticket.OperationId, key,
                SyncAuthorityOperationKind.AllocateControl, "candidate", "control-object", 1, "profile",
                """{"ParentIdentityText":"fixture-parent"}""", 1000,
                [new(ticket.OperationId + "-member", "candidate", "control-object", "lock", "local-root",
                    null, null, null, null, "{}")], [], []);
            await db.CreateOperationAsync(plan, command, reservation: ticket.Reservation);
        }
        foreach (var scenario in new[] {
            ("selected", "UPDATE sync_pair SET selected=1;", "UPDATE sync_pair SET selected=0;"),
            ("published", "UPDATE sync_pair SET pair_json='{}';", "UPDATE sync_pair SET pair_json=NULL;"),
            ("root revision", "UPDATE root_binding SET generation=2 WHERE side='dropbox';",
                "UPDATE root_binding SET generation=1 WHERE side='dropbox';"),
            ("retired", "UPDATE sync_pair SET state='retired';", "UPDATE sync_pair SET state='discovery_required';"),
            ("noncandidate peer", "UPDATE root_binding SET state='out_of_scope' WHERE side='dropbox';",
                "UPDATE root_binding SET state='blocked' WHERE side='dropbox';")
        })
        {
            await Change(scenario.Item2);
            try { await Register("reject-" + scenario.Item1.Replace(' ', '-')); throw new Exception("Expected exact target refusal."); }
            catch (InvalidDataException e) when (e.Message == "A fresh control action needs its exact current pair/root/configuration.")
            { Require(await Scalar(path, "SELECT count(*) FROM operation;") == 0,
                scenario.Item1 + " cloud candidate refuses fresh setup without creating an action"); }
            await Change(scenario.Item3);
        }
        try { await Register("normal-blocked", setup: false); throw new Exception("Expected exact target refusal."); }
        catch (InvalidDataException e) when (e.Message == "A fresh control action needs its exact current pair/root/configuration.")
        { Require(await Scalar(path, "SELECT count(*) FROM operation;") == 0,
            "ordinary admission cannot use the unpublished cloud setup exception"); }
        await Register("valid-unpublished");
        Require(await Scalar(path, "SELECT count(*) FROM operation;") == 1,
            "exact unpublished two-root cloud candidate accepts its single scoped control action");
    }

    private static async Task AssertJournalAccountingAsync(SyncAuthorityDatabase db, string path, string phase)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource=path, Mode=SqliteOpenMode.ReadOnly, Pooling=false }.ToString());
        await connection.OpenAsync();
        long exact = 0;
        // Independent recomputation from the actual SQLite columns, not the trigger's field list.
        foreach (var table in new[] { "operation","operation_member","operation_dependency","operation_step",
            "resource_allocation","namespace_allocation","operation_attempt","effect_receipt",
            "upload_session_checkpoint","lease","lease_fence" })
        {
            var columns = new List<string>();
            await using (var meta = connection.CreateCommand())
            {
                meta.CommandText = $"PRAGMA table_info({table});";
                await using var rows = await meta.ExecuteReaderAsync();
                while (await rows.ReadAsync()) if (!rows.GetString(1).StartsWith("journal_", StringComparison.Ordinal))
                    columns.Add(rows.GetString(1));
            }
            await using var sum = connection.CreateCommand();
            sum.CommandText = $"SELECT coalesce(sum({string.Join("+", columns.Select(x => $"coalesce(length(CAST({x} AS BLOB)),0)"))}),0) FROM {table};";
            exact = checked(exact + Convert.ToInt64(await sum.ExecuteScalarAsync()));
        }
        var measured = await db.LoadActionJournalStatusAsync();
        Require(measured.EncodedBytes == exact && measured.RetainedActions == await Scalar(path,"SELECT count(*) FROM operation;"),
            "transactional journal totals equal independently recomputed exact scalar bytes " + phase);
        Require((await db.LoadActionJournalStatusAsync("pair")).EncodedBytes == exact,
            "pair-scoped journal accounting equals its exact retained graph " + phase);
    }

    private static async Task Refuses(Func<Task> work, string description)
    {
        try { await work(); }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or SqliteException)
        { Require(true, description); return; }
        throw new InvalidDataException("Expected refusal: " + description);
    }
    private static void Require(bool value, string description)
    { if (!value) throw new InvalidDataException(description); Console.WriteLine("PASS " + description); }
    private static async Task<long> Scalar(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
