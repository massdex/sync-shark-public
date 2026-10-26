using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class ConsentLifetimeChecks
{
    internal static async Task RunAsync(string root,bool successorOnly=false)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh consent lifetime fixture.");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "authority.db");
        var db = new SyncAuthorityDatabase(path);
        await db.InitializeAsync("consent-life", "profile", "fixture", "{}");
        var pair = new SyncPair("pair", "Consent lifetime", root, 0, 0, "/Fixture", "id:root");
        await db.RegisterPairAsync(pair.Id, pair.PolicyRevision);
        await db.RegisterRootBindingAsync("local-root", pair.Id, "local", 1,
            """{"volumeSerial":"00000001","fileId":"root"}""", root);
        await db.RegisterRootBindingAsync("dropbox-root", pair.Id, "dropbox", 1,
            """{"folderId":"id:root"}""", "/Fixture");
        var choices = Enumerable.Range(0, 6).Select(i => new SyncBootstrapActionChoice(
            "file-" + i + ".txt", "Local only", "Upload")).ToArray();
        var scans = new Dictionary<string, string>();
        foreach (var side in new[] { "local", "dropbox" })
        {
            var scan = await db.BeginScanGenerationAsync(pair.Id, side + "-root", side, "manual", "{}");
            await db.AppendObservationEntriesAsync(scan, [new SyncAuthorityObservationInput(
                choices[1].RelativePath, "file", side == "local"
                    ? """{"volumeSerial":"00000001","fileId":"one","lastWriteUtc":"2026-01-01T00:00:00Z"}"""
                    : """{"fileId":"id:one","revision":"1"}""", new string('a',64), 4, "{}")]);
            await db.CompleteScanGenerationAsync(scan); scans[side] = scan.ScanGenerationId;
        }
        var plan = new SyncBootstrapActionPlan(pair.Id, scans["local"], scans["dropbox"], choices);
        var reserved = SyncAuthorityCommandReservation.Mint();
        var consent = SyncAuthorityConsentEncoding.ConsentIdForReservedCommand(reserved.CommandId);
        var request = SyncReviewedConsentReference.BuildAcceptedRunRequestJson(pair, plan, "fixture",
            pair.PolicyRevision, null, null);
        var sha = SyncReviewedConsentReference.LowercaseRequestSha256(request);
        var prepared = choices.Select(SyncAuthorityConsentEncoding.Prepare).ToArray();
        await db.BeginConsentPreparationAsync(new(consent, reserved.CommandId, reserved.CommandId, pair.Id, sha,
            SyncAuthorityConsentEncoding.OrderedDigest(prepared),
            SyncReviewedConsentReference.BuildConsentHeaderJson(request, plan, consent), choices.Length));
        await db.AppendConsentItemsAsync(consent, 0, prepared);
        await db.SealConsentAsync(consent);
        var admission = await db.CreateAcceptedCommandAdmissionWithConsentAsync(reserved, consent, "run_pair",
            pair.Id, null, null, SyncReviewedConsentReference.BuildConsentBackedRunRequestJson(pair, plan,
                "fixture", pair.PolicyRevision, null, consent, sha), SyncAuthorityCommandPurpose.NormalEffect);
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        var original = await db.LoadRunByIdAsync(admission.CommandRequestId)
            ?? throw new InvalidDataException("Missing admitted original.");
        var observations = await db.LoadPlanningObservationSetAsync(pair.Id, 1, "local-root", "dropbox-root",
            scans["local"], scans["dropbox"]);
        var local = observations.Local.Single(); var remote = observations.Dropbox.Single();
        await db.RegisterOperationGroupAsync("group", "compound", JsonSerializer.Serialize(new { consentId = consent }));
        var ticket = await db.ReserveActionAsync("unknown-item", admission);
        await db.CreateOperationAsync(new(ticket.OperationId, "unknown-item", SyncAuthorityOperationKind.UpdateRemote,
            pair.Id, local.ObjectId, 1, "profile", JsonSerializer.Serialize(new { path = choices[1].RelativePath }), 50,
            [new(ticket.OperationId+"-source",pair.Id,local.ObjectId,"source","local-root",local.MembershipId,
                local.ObjectVersionId,local.MembershipGeneration,choices[1].RelativePath,"{}"),
             new(ticket.OperationId+"-destination",pair.Id,remote.ObjectId,"destination","dropbox-root",remote.MembershipId,
                remote.ObjectVersionId,remote.MembershipGeneration,choices[1].RelativePath,"{}")],
            [], [], OperationGroupId:"group", PlanningAuthorities:[
                observations.Bind(SyncAuthorityReconciliationPlanner.ObservationSetIdentity(observations),local.ObjectId)]),
            admission, reservation:ticket.Reservation);
        await db.MarkOperationGroupActiveAsync("group");
        var lease = await db.AcquireOperationLeaseAsync(ticket.OperationId, "worker", TimeSpan.FromMinutes(1));
        var attempt = await db.StartDispatchedAttemptAsync(ticket.OperationId, lease, new string('b',64), "fixture-unknown");
        await db.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.Unknown,
            "{}", "{}", "{}", new string('c',64));
        await db.ReleaseLeaseAsync(lease);
        var now = DateTimeOffset.UtcNow;
        // Terminal lifecycle rows are explicit fixture input; the unknown effect
        // above is issued through the real command/action/receipt path.
        await Change(path, "UPDATE consent_item SET lifecycle='issued',issued_utc=$now WHERE ordinal IN (0,2,3);", ("$now",now.ToString("O")));
        await Change(path, "UPDATE consent_item SET lifecycle='completed',completed_utc=$now WHERE ordinal IN (0,2,3);", ("$now",now.ToString("O")));
        await db.EnableActivityRetentionForTestAsync();
        db.ActivityRetentionClockForTest = () => now.AddDays(8);
        await Change(path, "UPDATE consent_header SET last_use_utc=$now;", ("$now",now.AddDays(8).ToString("O")));
        await db.RetireInactiveConsentBatchAsync();
        Require(await Scalar(path,"SELECT count(*) FROM consent_item;")==6,
            "an accepted active original selection pins all still-needed consent choices");
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "blocked");
        await db.CloseOrdinaryCommandIssuanceAsync(admission.CommandRequestId);
        await Refuses(()=>Change(path,"DELETE FROM consent_item WHERE ordinal=0;"),
            "ordinary SQL cannot retire an original consent item");
        db.ConsentRetirementBoundaryForTest = phase => { if(phase=="before-commit") throw new InvalidOperationException("fixture crash"); };
        await Refuses(()=>db.RetireInactiveConsentBatchAsync(),"interrupted consent compaction rolls back atomically");
        Require(await Scalar(path,"SELECT count(*) FROM consent_item;")==6 &&
            await Scalar(path,"SELECT retired_choice_count FROM consent_header;")==0,
            "rollback retains both original bodies and unchanged terminal layout");
        var delayedPasses=0;
        db.ConsentRetirementBoundaryForTest=phase=>
        { if(phase=="after-consent-maintenance-preflight"){delayedPasses++;Thread.Sleep(75);} };
        for(var i=0;i<12 && await Scalar(path,"SELECT retired_choice_count FROM consent_header;")<3;i++)
            await db.RetireInactiveConsentBatchAsync();
        Require(await Scalar(path,"SELECT retired_choice_count FROM consent_header;")==3 &&
            await Scalar(path,"SELECT count(*) FROM consent_item;")==3,
            "completed siblings retire while one issued unknown and eligible choices remain");
        Require(await Scalar(path,"SELECT completed_prefix FROM consent_header;")==0 &&
            await Scalar(path,"SELECT json_array_length(terminal_ranges_json) FROM consent_header;")==1,
            "completion compresses to one monotonic prefix and one sparse contiguous range");
        if(successorOnly)
        {
            var beforeHeader=await db.LoadConsentHeaderAsync(consent);
            var beforeItems=await db.LoadConsentItemsAsync(consent,[1,4,5],SyncAuthorityConsentSelectionMode.ExactRead);
            var beforeWindow=await db.LoadActivityRetentionWindowAsync();
            await Change(path,"PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;");
            var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            var options=new SyncEssentialSuccessorOptions(root);
            var identity=await SyncAuthorityEssentialSuccessor.InspectSourceAsync(path,options);
            var targetPath=Path.Combine(root,"successor.db");
            SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=phase=>
            {if(phase=="after-table:consent_item")throw new InvalidOperationException("fixture interrupted after exact consent rows");};
            try
            {
                var interrupted=false;
                try{await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,targetPath,identity,options);}
                catch(InvalidOperationException e) when(e.Message=="fixture interrupted after exact consent rows")
                {interrupted=true;}
                Require(interrupted,"exact interruption after consent rows remains incomplete");
            }
            finally{SyncAuthorityDatabase.EssentialSuccessorBoundaryForTest=null;}
            var target=new SyncAuthorityDatabase(targetPath);
            await Refuses(()=>target.LoadConsentHeaderAsync(consent),"incomplete imported consent cannot be consumed");
            var result=await SyncAuthorityEssentialSuccessor.PrepareEssentialSuccessorAsync(path,targetPath,identity,options);
            Require(result.Complete&&result.TargetGeneration!=identity.DatabaseGeneration,"consent successor completes in a new generation");
            Require(await target.LoadConsentHeaderAsync(consent)==beforeHeader,
                "original consent identity, plan, counts, digest and lifecycle are exact");
            Require((await target.LoadConsentItemsAsync(consent,[1,4,5],SyncAuthorityConsentSelectionMode.ExactRead)).SequenceEqual(beforeItems),
                "issued and eligible choice bytes, hashes and ordinals remain exact");
            Require(await Scalar(targetPath,"SELECT count(*) FROM consent_item;")==3 &&
                await Scalar(targetPath,"SELECT completed_prefix FROM consent_header;")==0 &&
                await Scalar(targetPath,"SELECT retired_choice_count FROM consent_header;")==3,
                "compacted completed ordinals are retained as bounded facts and never recreated");
            Require((await target.LoadActivityRetentionWindowAsync()).Consents==beforeWindow.Consents,
                "derived consent budget and terminal counters match exact retained scope");
            await Refuses(()=>target.ReserveActionAsync("old-source-admission",admission),
                "old command admission cannot issue a successor action");
            var originalPlan=await target.MaterializeRetainedOriginalConsentPlanAsync(consent,
                new HashSet<string>([choices[4].RelativePath],StringComparer.OrdinalIgnoreCase));
            Require(originalPlan.Choices.Select(x=>x.RelativePath).SequenceEqual(new[]{choices[1].RelativePath,choices[4].RelativePath,choices[5].RelativePath}),
                "successor retry materializes exact surviving choices without path reconstruction");
            await Refuses(()=>target.LoadConsentItemsAsync(consent,[0],SyncAuthorityConsentSelectionMode.ReissueSelection),
                "retired completed original ordinal cannot be reissued");
            await Refuses(()=>Change(targetPath,
                "INSERT INTO consent_item(consent_id,ordinal,relative_path_key,original_relative_path,original_expected_group,original_action,"+
                "choice_json,choice_sha256,encoded_bytes,lifecycle) SELECT consent_id,0,'invented','invented',original_expected_group,original_action,"+
                "choice_json,choice_sha256,encoded_bytes,'eligible' FROM consent_item LIMIT 1;"),
                "restored production guard rejects forged retired ordinal");
            await SyncAuthorityEssentialSuccessor.VerifyEssentialSuccessorAsync(path,targetPath,identity,options);
            var fresh=await target.CreateAcceptedCommandAdmissionWithConsentAsync(null,consent,"run_pair",
                pair.Id,null,null,SyncReviewedConsentReference.BuildConsentBackedRunRequestJson(pair,plan,
                    "fixture",pair.PolicyRevision,null,consent,sha),SyncAuthorityCommandPurpose.NormalEffect);
            var freshTicket=await target.ReserveActionAsync("fresh-successor-admission",fresh);
            Require(fresh.CommandRequestId!=admission.CommandRequestId && freshTicket.OperationId!=ticket.OperationId,
                "a fresh successor command can reserve new work against the retained original consent");
            Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))==hash,
                "source consent and unknown-effect journal bytes remain unchanged");
            return;
        }
        var selected = await db.MaterializeRetainedOriginalConsentPlanAsync(consent,
            new HashSet<string>([choices[4].RelativePath],StringComparer.OrdinalIgnoreCase));
        Require(selected.Choices.Select(c=>c.RelativePath).SequenceEqual(new[] {choices[1].RelativePath,choices[4].RelativePath,choices[5].RelativePath}),
            "materialization returns exact surviving paths without recreating completed choices");
        var filtered=selected.Choices.Select(c=>c.RelativePath==choices[4].RelativePath?c:c with {Action="Skip for now"}).ToArray();
        var reference=SyncReviewedConsentReference.Create(original,selected,filtered,consent);
        var bits=SyncReviewedConsentReference.DecodeSelection(reference);
        Require(reference.ChoiceCount==6 && bits[4] && bits.Count(b=>b)==1,
            "new retry bitmap keeps ordinal four instead of renumbering it after compaction");
        SyncReviewedConsentReference.RequireSelectionAgainstRoot(reference,selected);
        var historical=SyncReviewedConsentReference.Create(original,plan,choices,consent);
        SyncReviewedConsentReference.RequireSelectionAgainstRoot(historical,selected);
        Require(true,"historical filters remain readable while retired bits supply no new choice");
        await Refuses(()=>db.MaterializeRetainedOriginalConsentPlanAsync(consent,
            new HashSet<string>([choices[0].RelativePath],StringComparer.OrdinalIgnoreCase)),
            "a completed retired path cannot materialize into fresh work");
        await Refuses(()=>db.LoadConsentItemsAsync(consent,[0],SyncAuthorityConsentSelectionMode.ReissueSelection),
            "a retired original ordinal cannot be reissued");
        Require(await Scalar(path,"SELECT count(*) FROM operation_attempt;")==1 &&
            await Scalar(path,"SELECT count(*) FROM effect_receipt WHERE certainty='unknown';")==1,
            "unknown attempt and receipt remain exact after sibling compaction");
        var accounting = (await db.LoadActivityRetentionWindowAsync()).Consents!;
        Require(!accounting.AccountingPending && accounting.RetiredItems==3 &&
            accounting.InactiveEligibleBytes==prepared[4].EncodedBytes+prepared[5].EncodedBytes,
            "inactive eligible bytes and cumulative retired choices match exact surviving bodies");
        Require(await Scalar(path,"SELECT retained_operation_count FROM consent_item WHERE ordinal=1;")==1,
            "exact consent reference accounting includes the real stamped unknown operation");
        await Change(path,"UPDATE consent_header SET commands_accounted=0; UPDATE consent_item SET eligible_bytes_accounted=0;");
        Require((await db.LoadActivityRetentionWindowAsync()).Consents!.AccountingPending,
            "unmeasured predecessor consent counters are explicitly pending");
        db.ConsentRetirementBoundaryForTest=phase=>{if(phase=="before-commit")throw new InvalidOperationException("fixture accounting crash");};
        await Refuses(()=>db.RetireInactiveConsentBatchAsync(),"interrupted predecessor consent measurement rolls back");
        Require((await db.LoadActivityRetentionWindowAsync()).Consents!.AccountingPending,
            "an interrupted measurement does not fabricate complete consent accounting");
        db.ConsentRetirementBoundaryForTest=null;
        for(var i=0;i<12 && (await db.LoadActivityRetentionWindowAsync()).Consents!.AccountingPending;i++)
            await db.RetireInactiveConsentBatchAsync();
        Require((await db.LoadActivityRetentionWindowAsync()).Consents is {AccountingPending:false} measured &&
            measured.InactiveEligibleBytes==prepared[4].EncodedBytes+prepared[5].EncodedBytes &&
            await Scalar(path,"SELECT retained_operation_count FROM consent_item WHERE ordinal=1;")==1,
            "bounded predecessor measurement reconstructs exact counters and real operation pins");
        Require(delayedPasses>0,"slow consent preflight still permits bounded measurement and item progress");
        db.ConsentEligibleBudgetForTest=prepared[5].EncodedBytes;
        for(var i=0;i<12 && await Scalar(path,"SELECT retired_choice_count FROM consent_header;")<4;i++)
            await db.RetireInactiveConsentBatchAsync();
        Require(await Scalar(path,"SELECT retired_choice_count FROM consent_header;")==4 &&
            (await db.LoadActivityRetentionWindowAsync()).Consents!.InactiveEligibleBytes==prepared[5].EncodedBytes,
            "eligible payload pressure expires one unissued item and stops at the exact remaining budget");
        db.ConsentEligibleBudgetForTest=null;
        db.ActivityRetentionClockForTest=()=>now.AddDays(16);
        for(var i=0;i<12 && await Scalar(path,"SELECT retired_choice_count FROM consent_header;")<5;i++)
            await db.RetireInactiveConsentBatchAsync();
        Require(await Scalar(path,"SELECT count(*) FROM consent_item;")==1 &&
            await Scalar(path,"SELECT expired_choice_count FROM consent_header;")==2,
            "seven-day inactive eligible expiry leaves only the exact issued unknown item");
        await Refuses(()=>db.MaterializeRetainedOriginalConsentPlanAsync(consent,
            new HashSet<string>([choices[4].RelativePath],StringComparer.OrdinalIgnoreCase)),
            "an expired selected path requires new approval and never rebuilds from its history");
        await using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText="PRAGMA foreign_key_check;";
        await using var rows=await q.ExecuteReaderAsync();Require(!await rows.ReadAsync(),"consent compaction preserves every foreign key");
    }
    private static async Task Change(string path,string sql,params (string Name,object Value)[] parameters)
    {
        await using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());
        await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=sql;
        foreach(var p in parameters)q.Parameters.AddWithValue(p.Name,p.Value);
        await q.ExecuteNonQueryAsync();
    }
    private static async Task<long> Scalar(string path,string sql)
    {
        await using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        await c.OpenAsync();await using var q=c.CreateCommand();q.CommandText=sql;
        return Convert.ToInt64(await q.ExecuteScalarAsync());
    }
    private static async Task Refuses(Func<Task> work,string description)
    {
        try{await work();}catch(Exception e) when(e is InvalidDataException or InvalidOperationException or SqliteException)
        {Require(true,description);return;}throw new Exception("Expected refusal: "+description);
    }
    private static void Require(bool value,string description)
    {if(!value)throw new InvalidDataException(description);Console.WriteLine("PASS "+description);}
}
