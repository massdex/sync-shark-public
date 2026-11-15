using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Prepared source and test additions for the activity-context storage correction (CCS1-CCS9).
//
// Test source is not executed evidence: only a captured successful invocation on bound compiled bytes
// closes a case. Full UI and release acceptance are separate from this isolated database fixture.
//
// Every retained row and every read below goes through the REAL append path
// (SyncAuthorityDatabase.AppendActivityEventsAsync), the REAL shared projection
// (LoadActivityProjectionPageAsync) and real isolated SQLite state. The only rows staged with raw SQL are the
// ones the append path refuses by design: an unresolvable, foreign, cross-run, recursive or malformed stored
// context reference, which an out-of-band writer is the only way to produce.
internal static class ActivityContextStorageChecks
{
    private static int _assertions;
    private static int _cases;

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await BoundedDetailedStreamAsync(root);
        await ReplayCollisionAndRollbackAsync(root);
        await BatchGroupingAsync(root);
        await ReferenceRefusalsAsync(root);
        await LinkedOperationContextAsync(root);
        Console.WriteLine("PASS activity context storage: " + _assertions + " assertions across " + _cases +
            " cases; batch-local full-context anchors with indexed one-hop references, 512 retained detailed " +
            "events hydrated through the real projection, idempotent replay after reopen with whole-append " +
            "rollback on changed content/path/context, a run or context change inside one batch starting its own " +
            "anchor, current-pair rename/rebind/retire leaving every retained context and filtered readback " +
            "unchanged, and unresolvable stored references staying visibly unknown with the legacy connection " +
            "fallback suppressed.");
    }

    // ----- 1. CCS1/CCS2/CCS3/CCS8: the bounded detailed stream through the real projection ------------------

    private static async Task BoundedDetailedStreamAsync(string root)
    {
        Claim("one full context anchor per append transaction retains and hydrates every one of 512 detailed events");
        var caseRoot = Path.Combine(root, "bounded-stream");
        Directory.CreateDirectory(caseRoot);
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("acs-stream-install", "acs-stream-profile", "owner-fixture", "{}");
        var pair = LegacyPair("pair-acs-stream", "Stream fixture", LocalFolder(caseRoot, "local"),
            "connection-acs-stream");
        var context = SyncEndpointContext.Capture(pair);
        var start = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        var first = Stream("acs-stream-", "connection-acs-stream", pair.Id, context, 0, 256, "run-acs-stream", start);
        var second = Stream("acs-stream-", "connection-acs-stream", pair.Id, context, 256, 256, "run-acs-stream",
            start.AddSeconds(900));
        // The baseline is what this build would retain without the reference representation: every event carries
        // its own complete context. It is a real measurement of these two appends, never an extrapolation to the
        // 7.63 GB database and never a claimed saving on a table whose bytes were not measured.
        var baselineBytes = first.Concat(second).Sum(item => Encoding.UTF8.GetByteCount(LogicalPayload(item)));
        await database.AppendActivityEventsAsync(first);
        await database.AppendActivityEventsAsync(second);

        var fingerprint = await FingerprintAsync(database.DatabasePath);
        var (ids, stored) = await StoredRowsAsync(database.DatabasePath, pair.Id);
        var retainedBytes = stored.Sum(Encoding.UTF8.GetByteCount);
        var anchors = stored.Count(payload => ContextMember(payload, "EndpointContext") is not null);
        var references = stored.Count(payload =>
            ContextMember(payload, "EndpointContextEventId") is { Length: > 2 });
        Console.WriteLine("MEASURE activity context storage: logical full-context baseline " + baselineBytes +
            " bytes, actual retained " + retainedBytes + " bytes across " + stored.Count + " events (" +
            anchors + " full anchors, " + references + " references)");
        Require(ids.Count == 512 && anchors == 2 && references == 510 && anchors + references == ids.Count,
            "two 256-event appends retain 512 logical events as exactly 2 full-context anchors and 510 references");
        Require(retainedBytes < baselineBytes,
            "the actual retained payload is smaller than the full per-event context baseline it replaced");
        Require(ids[0] == first[0].EventId && ids[256] == second[0].EventId &&
                ContextMember(stored[0], "EndpointContext") is not null &&
                ContextMember(stored[256], "EndpointContext") is not null,
            "each append transaction anchors on its own first NEW event while every logical identity and order is retained");
        Require(ContextMember(stored[1], "EndpointContextEventId") is not null &&
                ContextMember(stored[1], "EndpointContext") is null,
            "a later event of the same group stores one reference instead of a second copy of the full context");

        var rows = await ReadAllAsync(database, pair.Id);
        Require(rows.Count == 512 && rows.Select(row => row.Sequence).Distinct().Count() == 512,
            "all 512 detailed events remain individually retained with 512 distinct durable sequences");
        Require(rows.Select(row => row.Sequence).SequenceEqual(rows.Select(row => row.Sequence).OrderBy(value => value)),
            "ascending detailed pages return the complete retained stream in gap-free durable order");
        Require(rows.All(row => row.EndpointContext == context),
            "every anchored and referenced row hydrates the exact historical endpoint context through the shared projection");
        Require(rows.Select((row, index) => row.EventId == "history:" + ids[index] &&
                row.PairId == pair.Id && row.RunId == "run-acs-stream" &&
                row.OperationId == "op-acs-" + index.ToString("D3", CultureInfo.InvariantCulture) &&
                row.RelativePath == "folder/item-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".txt" &&
                row.Progress?.Completed == index &&
                row.Severity == (index % 8 == 7 ? "Error" : "Information") &&
                row.Reason.Contains(index.ToString("D3", CultureInfo.InvariantCulture), StringComparison.Ordinal))
            .All(matches => matches),
            "no retained identity, pair, run, operation, path, counter, severity or failure reason is lost by the reference representation");

        var forward = await database.LoadActivityProjectionPageAsync(200, pairId: pair.Id,
            query: new SyncActivityQuery(DetailedLog: true, OldestFirst: true));
        var next = await database.LoadActivityProjectionPageAsync(200, pairId: pair.Id,
            query: new SyncActivityQuery(DetailedLog: true, OldestFirst: true,
                AfterSequence: forward[^1].Sequence));
        var backward = await database.LoadActivityProjectionPageAsync(50, pairId: pair.Id,
            query: new SyncActivityQuery(DetailedLog: true, NewestLogFirst: true,
                ThroughSequence: next[0].Sequence - 1));
        Require(forward.Count == 200 && next.Count == 200 && backward.Count == 50 &&
                forward[0].EventId == "history:" + ids[0] && next[0].EventId == "history:" + ids[200] &&
                backward[0].EventId == "history:" + ids[199] && backward[^1].EventId == "history:" + ids[150],
            "forward cursors, the newest tail and backward paging stay exact across anchors and references");

        var scoped = await ScopedAsync(database, "connection-acs-stream");
        var runScoped = await database.LoadActivityProjectionPageAsync(500, pairId: pair.Id,
            query: new SyncActivityQuery(RunId: "run-acs-stream", DetailedLog: true));
        var runScopedRemainder = await database.LoadActivityProjectionPageAsync(500, offset: 500, pairId: pair.Id,
            query: new SyncActivityQuery(RunId: "run-acs-stream", DetailedLog: true));
        var searchScoped = await database.LoadActivityProjectionPageAsync(500, pairId: pair.Id, search: "item-137",
            query: new SyncActivityQuery(DetailedLog: true));
        var errorScoped = await database.LoadActivityProjectionPageAsync(500, pairId: pair.Id,
            query: new SyncActivityQuery(Severity: "Error", DetailedLog: true));
        Require(scoped.Count == 512 && scoped.All(row => row.EndpointContext == context),
            "the exact recorded account reaches every anchored and referenced row once through the hydrated context");
        Require(runScoped.Count == 500 && runScopedRemainder.Count == 12 &&
                runScoped.Concat(runScopedRemainder).Select(row => row.EventId).Distinct().Count() == 512 &&
                runScoped.Concat(runScopedRemainder).All(row => row.EndpointContext == context) &&
                searchScoped.Count == 1 && searchScoped[0].RelativePath == "folder/item-137.txt" &&
                errorScoped.Count == 64 && errorScoped.All(row => row.Severity == "Error"),
            "run, path/search and severity filters keep selecting the same rows when the context is stored by reference");
        await AssertStorageBrowseEquivalenceAsync(database);
        Require(await FingerprintAsync(database.DatabasePath) == fingerprint,
            "every cursor, filter, account, search and bounded-browse read leaves the retained history byte-identical");
    }

    // ----- 2. CCS4: idempotent replay and whole-append rollback --------------------------------------------

    private static async Task ReplayCollisionAndRollbackAsync(string root)
    {
        Claim("replay after reopen stays idempotent while changed content, path or context refuses and rolls back the append");
        var caseRoot = Path.Combine(root, "replay");
        Directory.CreateDirectory(caseRoot);
        var path = Path.Combine(caseRoot, "activity.db");
        var database = new SyncAuthorityDatabase(path);
        await database.InitializeAsync("acs-replay-install", "acs-replay-profile", "owner-fixture", "{}");
        var pair = LegacyPair("pair-acs-replay", "Replay fixture", LocalFolder(caseRoot, "local"),
            "connection-acs-replay");
        var context = SyncEndpointContext.Capture(pair);
        var batch = Stream("acs-replay-", "connection-acs-replay", pair.Id, context, 0, 64, "run-acs-replay",
            new DateTimeOffset(2026, 10, 6, 1, 0, 0, TimeSpan.Zero));
        await database.AppendActivityEventsAsync(batch);
        var before = await FingerprintAsync(path);

        // A genuinely new database instance replays the identical append: no row is added, no byte changes.
        var reopened = new SyncAuthorityDatabase(path);
        await reopened.AppendActivityEventsAsync(batch);
        Require(await FingerprintAsync(path) == before && await PresentationCountAsync(path) == 64,
            "replaying an identical batch through a reopened database adds no row and rewrites no retained byte");
        // The same proof for one stored REFERENCED row on its own, exercising the expansion path directly.
        await reopened.AppendActivityEventAsync(batch[10]);
        Require(await FingerprintAsync(path) == before && await PresentationCountAsync(path) == 64,
            "replaying one stored referenced row is idempotent, including its anchor-expanded logical context");

        foreach (var (label, changed) in new (string Label, SyncActivityEvent Changed)[]
                 {
                     ("anchor message", batch[0] with { Message = "different retained authority" }),
                     ("referenced message", batch[10] with { Message = "different retained authority" }),
                     ("referenced path", batch[10] with { RelativePath = "folder/other.txt" }),
                     ("referenced context", batch[10] with
                         { EndpointContext = context with { CapturedName = "Different captured name" } })
                 })
        {
            var neighbour = batch[0] with { EventId = "acs-replay-extra" };
            var rejected = false;
            try { await reopened.AppendActivityEventsAsync([neighbour, changed]); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected && await FingerprintAsync(path) == before && await PresentationCountAsync(path) == 64 &&
                    await CountAsync(path, neighbour.EventId) == 0,
                "changing the retained " + label + " under one event identity refuses the append and rolls back its whole transaction");
        }

        // A reference is an internal stored representation, never caller effect authority.
        var suppliedReference = false;
        try
        {
            var caller = JsonSerializer.Deserialize<SyncActivityEvent>(ReferencePayload(
                batch[0] with { EventId = "acs-supplied-reference" }, batch[0].EventId))!;
            await reopened.AppendActivityEventAsync(caller);
        }
        catch (ArgumentException) { suppliedReference = true; }
        Require(suppliedReference && await CountAsync(path, "acs-supplied-reference") == 0 &&
                await FingerprintAsync(path) == before,
            "a caller-supplied endpoint context reference is refused outright and retains nothing");
    }

    // ----- 3. CCS2/CCS3: grouping by context/pair/run, and current-pair mutation ---------------------------

    private static async Task BatchGroupingAsync(string root)
    {
        Claim("a changed run or context inside one batch starts its own anchor and current-pair mutation never rewrites history");
        var caseRoot = Path.Combine(root, "grouping");
        Directory.CreateDirectory(caseRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(caseRoot, "state", "instance.lock"));
        using var client = new HttpClient(new NoNetwork());
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("acs-group-install", "acs-group-profile", "owner-fixture", "{}");
        var pair = LegacyPair("pair-acs-group", "Grouping fixture", LocalFolder(caseRoot, "local"),
            "connection-acs-alpha");
        await database.RegisterPairAsync(pair.Id, pair.PolicyRevision);
        await database.RetainPairDefinitionAsync(pair, select: true);
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, client,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new NoTokens(), authorityDatabase: database);
        var context = SyncEndpointContext.Capture(pair);
        var renamed = SyncEndpointContext.Capture(pair with { Name = "Grouping fixture renamed" });
        var rebound = SyncEndpointContext.Capture(LegacyPair(pair.Id, "Grouping fixture",
            LocalFolder(caseRoot, "local-rebound"), "connection-acs-beta"));
        Require(renamed.CapturedName != context.CapturedName && renamed.PairId == context.PairId &&
                rebound.Right.ConnectionId != context.Right.ConnectionId,
            "the fixture really captures a different name and a different recorded account under the same pair identity");

        var start = new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.Zero);
        var anchor = new SyncActivityEvent("acs-group-anchor", start, [SyncActivityCategories.Sync],
            "Information", "File", "Upload", "Uploaded folder/one.txt", "connection-acs-alpha", pair.Id,
            "run-acs-g1", "op-acs-1", "folder/one.txt", context);
        var sameGroup = anchor with { EventId = "acs-group-same", OccurredUtc = start.AddSeconds(1),
            RelativePath = "folder/two.txt" };
        var otherRun = anchor with { EventId = "acs-group-run", OccurredUtc = start.AddSeconds(2),
            RunId = "run-acs-g2", RelativePath = "folder/three.txt" };
        var otherContext = anchor with { EventId = "acs-group-context", OccurredUtc = start.AddSeconds(3),
            RelativePath = "folder/four.txt", EndpointContext = renamed };
        await database.AppendActivityEventsAsync([anchor, sameGroup, otherRun, otherContext]);
        var (ids, stored) = await StoredRowsAsync(database.DatabasePath, pair.Id);
        Require(ids.SequenceEqual(["acs-group-anchor", "acs-group-same", "acs-group-run", "acs-group-context"]),
            "one batch retains all four logical events with their own identities in submission order");
        Require(ContextMember(stored[1], "EndpointContext") is null &&
                ContextMember(stored[1], "EndpointContextEventId") == "\"acs-group-anchor\"" &&
                ContextMember(stored[2], "EndpointContext") is not null &&
                ContextMember(stored[3], "EndpointContext") is not null,
            "the same group references its anchor while a changed run or a changed context starts its own full anchor");
        var rows = await ReadAllAsync(database, pair.Id);
        Require(rows[0].EndpointContext == context && rows[1].EndpointContext == context &&
                rows[2].EndpointContext == context && rows[3].EndpointContext == renamed,
            "each row hydrates its own exact historical context rather than a latest-by-pair or current-pair value");
        var fingerprint = await FingerprintAsync(database.DatabasePath);

        await database.RetainPairDefinitionAsync(pair with { Name = "Grouping fixture renamed" }, select: true);
        await database.RetainPairDefinitionAsync(
            pair with { Name = "Grouping fixture renamed", ConnectionId = "connection-acs-beta" }, select: true);
        await host.RetirePairAsync(pair.Id);
        var after = await ReadAllAsync(database, pair.Id);
        var alphaScoped = await ScopedAsync(database, "connection-acs-alpha");
        var betaScoped = await ScopedAsync(database, "connection-acs-beta");
        Require(after.Count == 4 && after[0].EndpointContext == context && after[3].EndpointContext == renamed &&
                after.All(row => row.PairId == pair.Id) &&
                await FingerprintAsync(database.DatabasePath) == fingerprint,
            "rename, rebind and retirement of the current pair leave every retained context and byte unchanged");
        Require(alphaScoped.Count == 4 && betaScoped.Count == 0,
            "the recorded alpha account still finds its history once while the newly bound beta account acquires none of it");
        await AssertStorageBrowseEquivalenceAsync(database);
    }

    // ----- 4. CCS5: unresolvable, foreign, cross-run, recursive and malformed references -------------------

    private static async Task ReferenceRefusalsAsync(string root)
    {
        Claim("an unresolvable, foreign, cross-run, recursive or malformed stored reference stays unknown and never re-attributes a row");
        var caseRoot = Path.Combine(root, "refusals");
        Directory.CreateDirectory(caseRoot);
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("acs-refusal-install", "acs-refusal-profile", "owner-fixture", "{}");
        var pair = LegacyPair("pair-acs-refusal", "Refusal fixture", LocalFolder(caseRoot, "local"),
            "connection-acs-refusal");
        var other = LegacyPair("pair-acs-other", "Other fixture", LocalFolder(caseRoot, "local-other"),
            "connection-acs-other");
        var context = SyncEndpointContext.Capture(pair);
        var otherContext = SyncEndpointContext.Capture(other);
        var start = new DateTimeOffset(2026, 10, 6, 3, 0, 0, TimeSpan.Zero);
        const string refusalRun = "run-acs-refusal";
        var anchorAndPeer = Stream("acs-refusal-", "connection-acs-refusal", pair.Id, context, 0, 2, refusalRun,
            start);
        var otherAnchor = Stream("acs-other-", "connection-acs-other", other.Id, otherContext, 0, 1,
            "run-acs-other", start);
        await database.AppendActivityEventsAsync(anchorAndPeer);
        await database.AppendActivityEventsAsync(otherAnchor);
        // A valid control row the corrupt references must never conceal or re-attribute.
        var control = anchorAndPeer[0] with { EventId = "acs-refusal-control", OccurredUtc = start.AddSeconds(1),
            RelativePath = "folder/control.txt" };
        await database.AppendActivityEventAsync(control);

        const string legacyFallback = "connection-legacy-fallback";
        SyncActivityEvent Staged(string eventId, string runId) => anchorAndPeer[0] with
        {
            EventId = eventId, OccurredUtc = start.AddSeconds(10), ConnectionId = legacyFallback,
            RelativePath = "folder/" + eventId + ".txt", EndpointContext = null,
            RunId = runId
        };
        var staged = new (string EventId, string? Reference, string RunId, string Label)[]
        {
            ("acs-refusal-missing", "acs-absent-anchor", refusalRun,
                "a reference to an anchor that does not exist"),
            ("acs-refusal-foreign", otherAnchor[0].EventId, "run-acs-other",
                "a reference to the anchor of a different pair (its run matches, so only the pair disagrees)"),
            ("acs-refusal-crossrun", anchorAndPeer[0].EventId, "run-acs-different",
                "a reference to its own pair's anchor whose only disagreement is the run"),
            ("acs-refusal-recursive", anchorAndPeer[1].EventId, refusalRun,
                "a reference to another reference-only row"),
            ("acs-refusal-empty", "", refusalRun, "an empty explicit reference"),
            ("acs-refusal-null", null, refusalRun, "a null explicit reference"),
            ("acs-refusal-malformed", null, refusalRun, "a reference member that is not a string at all")
        };
        foreach (var (eventId, reference, runId, _) in staged)
        {
            var payload = eventId == "acs-refusal-malformed"
                ? MalformedReferencePayload(Staged(eventId, runId))
                : ReferencePayload(Staged(eventId, runId), reference);
            await StagePresentationAsync(database.DatabasePath, eventId, payload, start.AddSeconds(10));
        }
        // A staged row whose reference IS resolvable proves the resolution itself works for rows the append
        // path never wrote, so the refusals above are not the absence of a working lookup.
        await StagePresentationAsync(database.DatabasePath, "acs-refusal-resolved",
            ReferencePayload(Staged("acs-refusal-resolved", refusalRun), anchorAndPeer[0].EventId),
            start.AddSeconds(11));

        var fingerprint = await FingerprintAsync(database.DatabasePath);
        var page = await database.LoadActivityProjectionPageAsync(500, query: new SyncActivityQuery(DetailedLog: true));
        Require(page.Count == 4 + staged.Length + 1,
            "every readable log row survives unresolvable references instead of the bad reference concealing them");
        foreach (var (eventId, _, _, label) in staged)
            Require(page.Single(row => row.EventId == "history:" + eventId).EndpointContext is null,
                "the projection leaves the context absent for " + label + " rather than substituting a current or legacy account");
        Require(page.Single(row => row.EventId == "history:acs-refusal-resolved").EndpointContext == context &&
                page.Single(row => row.EventId == "history:" + control.EventId).EndpointContext == context &&
                page.Single(row => row.EventId == "history:" + anchorAndPeer[1].EventId).EndpointContext == context,
            "a resolvable staged reference, the valid peer row and the control row keep their exact hydrated context");
        Require((await ScopedAsync(database, legacyFallback)).Count == 0,
            "a context-bearing row with an unresolved reference is never re-attributed through its retained legacy connection");
        Require((await ScopedAsync(database, "connection-acs-other")).Count == 1 &&
                (await ScopedAsync(database, "connection-acs-refusal")).Count == 4,
            "a foreign anchor's account acquires none of the referencing pair's rows while the recorded account keeps its own");
        Require(await FingerprintAsync(database.DatabasePath) == fingerprint,
            "reading rows with unresolvable references rewrites nothing in the append-only history");
    }

    private static async Task LinkedOperationContextAsync(string root)
    {
        Claim("a lifecycle row resolves an operation represented only by compact context references through the existing index");
        var caseRoot = Path.Combine(root, "linked-operation");
        Directory.CreateDirectory(caseRoot);
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("acs-linked-install", "acs-linked-profile", "owner-fixture", "{}");
        var pair = LegacyPair("pair-acs-linked", "Linked fixture", LocalFolder(caseRoot, "local"),
            "connection-acs-linked");
        var context = SyncEndpointContext.Capture(pair);
        await database.RegisterPairAsync(pair.Id, pair.PolicyRevision);
        await database.RetainPairDefinitionAsync(pair, select: false);
        const string operationId = "op-acs-linked";
        const string rootId = "root-acs-linked";
        const string objectId = "object-acs-linked";
        await database.RegisterRootBindingAsync(rootId, pair.Id, "local", 1, "{}",
            Path.Combine(caseRoot, "local"));
        await database.RegisterObjectAsync(objectId, "file");
        var admission = await database.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id, null, null,
            "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await database.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        await database.CreateOperationAsync(new SyncAuthorityOperationPlan(operationId, "idem-acs-linked",
            SyncAuthorityOperationKind.CreateRemote, pair.Id, objectId, pair.PolicyRevision, "acs-linked-profile",
            "{}", 0,
            [new SyncAuthorityMember("member-acs-linked", pair.Id, objectId, "source", rootId,
                null, null, null, "reference.txt", "{}")], [], []), admission);
        var start = new DateTimeOffset(2026, 10, 6, 4, 0, 0, TimeSpan.Zero);
        var anchor = new SyncActivityEvent("acs-linked-anchor", start, [SyncActivityCategories.Sync],
            "Information", "File", "Checking", "Checking anchor.txt", pair.ConnectionId, pair.Id,
            "run-acs-linked", "op-acs-anchor", "anchor.txt", context);
        var referenced = anchor with { EventId = "acs-linked-reference", OperationId = operationId,
            RelativePath = "reference.txt", OccurredUtc = start.AddSeconds(1) };
        await database.AppendActivityEventsAsync([anchor, referenced]);
        var (_, stored) = await StoredRowsAsync(database.DatabasePath, pair.Id);
        Require(ContextMember(stored[1], "EndpointContext") is null &&
                ContextMember(stored[1], "EndpointContextEventId") == "\"acs-linked-anchor\"",
            "the actual linked operation has no inline-context presentation row, making the lookup regression decisive");
        // A retained lifecycle input, not an execution of a sync effect. The REAL projection must decide its
        // attribution using the real planned operation and the actual indexed compact presentation row.
        await using (var connection = await OpenAsync(database.DatabasePath, SqliteOpenMode.ReadWrite))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO history_event(history_event_id,operation_id,event_kind,detail_json," +
                "occurred_utc) VALUES('acs-linked-lifecycle',$operation,'attempt_reconciled','{}',$utc);";
            command.Parameters.AddWithValue("$operation", operationId);
            command.Parameters.AddWithValue("$utc", start.AddSeconds(2).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }
        var rows = await database.LoadActivityProjectionPageAsync(500,
            query: new SyncActivityQuery(DetailedLog: true, OldestFirst: true));
        Require(rows.Single(row => row.EventId == "history:acs-linked-lifecycle").EndpointContext == context,
            "bounded lifecycle lookup hydrates the exact reference-only operation context with no new index or history scan");
        Require((await ScopedAsync(database, "connection-acs-linked"))
                .Any(row => row.EventId == "history:acs-linked-lifecycle" && row.EndpointContext == context),
            "account-filtered lifecycle readback preserves the same exact attribution");
        await AssertStorageBrowseEquivalenceAsync(database);
        var changed = context with { CapturedName = "Different historical context" };
        await database.AppendActivityEventsAsync([
            anchor with { EventId = "acs-linked-ambiguous-anchor", EndpointContext = changed },
            referenced with { EventId = "acs-linked-ambiguous-reference", EndpointContext = changed }]);
        rows = await database.LoadActivityProjectionPageAsync(500,
            query: new SyncActivityQuery(DetailedLog: true, OldestFirst: true));
        Require(rows.Single(row => row.EventId == "history:acs-linked-lifecycle").EndpointContext is null,
            "two different hydrated contexts for the same exact operation refuse ambiguous lifecycle attribution");
    }

    // ----- shared fixtures and helpers ---------------------------------------------------------------------

    private static async Task AssertStorageBrowseEquivalenceAsync(SyncAuthorityDatabase database)
    {
        // Exercise both REAL query branches on compact rows. The original exhaustive activity regression
        // remains unchanged and runs separately; do not repeat its whole Cartesian matrix on each storage
        // fixture, including the 512-event stream.
        var beforeFixture = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var query in new[] {
                     new SyncActivityQuery(),
                     new SyncActivityQuery(DetailedLog: true, OldestFirst: true),
                     new SyncActivityQuery(DetailedLog: true, NewestLogFirst: true),
                     new SyncActivityQuery([SyncActivityCategories.Diagnostics], DetailedLog: true) })
        {
            var complete = await database.LoadActivityProjectionPageAsync(500, sinceUtc: beforeFixture,
                query: query);
            var actual = await database.LoadActivityProjectionPageAsync(3, offset: 1, query: query);
            Require(complete.Skip(1).Take(3).Select(row => JsonSerializer.Serialize(row))
                    .SequenceEqual(actual.Select(row => JsonSerializer.Serialize(row))),
                "compact-context browse and fully filtered queries preserve all projected fields and page boundaries");
            if (query.DetailedLog && complete.Count > 1)
            {
                var cursorQuery = query with { AfterSequence = complete.Min(row => row.Sequence) };
                var expected = await database.LoadActivityProjectionPageAsync(500, sinceUtc: beforeFixture,
                    query: cursorQuery);
                var cursorPage = await database.LoadActivityProjectionPageAsync(3, offset: 1, query: cursorQuery);
                Require(expected.Skip(1).Take(3).Select(row => JsonSerializer.Serialize(row))
                        .SequenceEqual(cursorPage.Select(row => JsonSerializer.Serialize(row))),
                    "compact-context cursors precede paging in both real activity query branches");
            }
        }
    }

    private static IReadOnlyList<SyncActivityEvent> Stream(string prefix, string connectionId, string pairId,
        SyncEndpointContext context, int first, int count, string runId, DateTimeOffset start) =>
        Enumerable.Range(first, count).Select(index => new SyncActivityEvent(
            prefix + index.ToString("D3", CultureInfo.InvariantCulture), start.AddSeconds(index),
            index % 8 == 7
                ? [SyncActivityCategories.Sync, SyncActivityCategories.Diagnostics]
                : [SyncActivityCategories.Sync],
            index % 8 == 7 ? "Error" : "Information", "File",
            index % 8 == 7 ? "Upload failed" : "Uploaded",
            (index % 8 == 7
                ? "Failed to upload folder/item-" + index.ToString("D3", CultureInfo.InvariantCulture) +
                  ".txt: the provider refused the request."
                : "Uploaded folder/item-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".txt"),
            connectionId, pairId, runId,
            "op-acs-" + index.ToString("D3", CultureInfo.InvariantCulture),
            "folder/item-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".txt", context,
            index % 3 == 0 ? SyncAuthorityOperationKind.CreateRemote : SyncAuthorityOperationKind.UpdateRemote,
            index % 2 == 0 ? SyncActivityPhase.Running : SyncActivityPhase.Completed,
            new SyncActivityProgress(index, index % 3 == 0 ? null : 4096,
                index % 2 == 0 ? "bytes" : "items checked", index))).ToArray();

    private static string MalformedReferencePayload(SyncActivityEvent item)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(item))!.AsObject();
        node["EndpointContext"] = new JsonObject { ["EndpointContextEventId"] = 5 };
        return node.ToJsonString();
    }

    private static string ReferencePayload(SyncActivityEvent item, string? reference)
    {
        var node = JsonSerializer.SerializeToNode(item)!.AsObject();
        node["EndpointContext"] = new JsonObject { ["EndpointContextEventId"] = reference };
        return node.ToJsonString();
    }

    private static string LogicalPayload(SyncActivityEvent item) => JsonSerializer.Serialize(item with
    {
        OccurredUtc = item.OccurredUtc.ToUniversalTime(), Action = SyncActivityText.Safe(item.Action),
        Message = SyncActivityText.Safe(item.Message)
    });

    private static string? ContextMember(string payload, string name)
    {
        var node = JsonNode.Parse(payload)?.AsObject();
        if (node?["EndpointContext"] is JsonObject context && context.ContainsKey("EndpointContextEventId"))
            return name == "EndpointContextEventId" ? context[name]?.ToJsonString() : null;
        return node is null || !node.TryGetPropertyValue(name, out var value) || value is null
            ? null : value.ToJsonString();
    }

    private static async Task<IReadOnlyList<SyncAuthorityActivityProjection>> ReadAllAsync(
        SyncAuthorityDatabase database, string pairId)
    {
        var rows = new List<SyncAuthorityActivityProjection>();
        for (var offset = 0; ; offset += 500)
        {
            var page = await database.LoadActivityProjectionPageAsync(500, offset, pairId: pairId,
                query: new SyncActivityQuery(DetailedLog: true, OldestFirst: true));
            rows.AddRange(page);
            if (page.Count < 500) break;
        }
        return rows;
    }

    private static async Task<IReadOnlyList<SyncAuthorityActivityProjection>> ScopedAsync(
        SyncAuthorityDatabase database, string account)
    {
        var rows = new List<SyncAuthorityActivityProjection>();
        for (var offset = 0; ; offset += 500)
        {
            var page = await database.LoadActivityProjectionPageAsync(500, offset, null, "All actions",
                connectionId: account, query: new SyncActivityQuery(DetailedLog: true));
            rows.AddRange(page);
            if (page.Count < 500) break;
        }
        return rows;
    }

    private static async Task<(IReadOnlyList<string> Ids, IReadOnlyList<string> Payloads)> StoredRowsAsync(
        string databasePath, string pairId)
    {
        var ids = new List<string>();
        var payloads = new List<string>();
        await using var connection = await OpenAsync(databasePath, SqliteOpenMode.ReadOnly);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT history_event_id,detail_json FROM history_event WHERE event_kind='presentation' " +
            "AND json_extract(detail_json,'$.PairId')=$pair ORDER BY rowid;";
        command.Parameters.AddWithValue("$pair", pairId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
            payloads.Add(reader.GetString(1));
        }
        return (ids, payloads);
    }

    private static async Task<long> PresentationCountAsync(string databasePath)
    {
        await using var connection = await OpenAsync(databasePath, SqliteOpenMode.ReadOnly);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM history_event WHERE event_kind='presentation';";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountAsync(string databasePath, string eventId)
    {
        await using var connection = await OpenAsync(databasePath, SqliteOpenMode.ReadOnly);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM history_event WHERE history_event_id=$id;";
        command.Parameters.AddWithValue("$id", eventId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string> FingerprintAsync(string databasePath)
    {
        await using var connection = await OpenAsync(databasePath, SqliteOpenMode.ReadOnly);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT history_event_id,coalesce(operation_id,''),event_kind,detail_json," +
            "occurred_utc FROM history_event ORDER BY rowid;";
        var payload = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            for (var column = 0; column < 5; column++) payload.Append(reader.GetString(column)).Append('\u001f');
            payload.Append('#');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private static async Task StagePresentationAsync(string databasePath, string eventId, string payload,
        DateTimeOffset occurredUtc)
    {
        await using var connection = await OpenAsync(databasePath, SqliteOpenMode.ReadWrite);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO history_event(history_event_id,operation_id,event_kind,detail_json," +
            "occurred_utc) VALUES($id,NULL,'presentation',$detail,$utc);";
        command.Parameters.AddWithValue("$id", eventId);
        command.Parameters.AddWithValue("$detail", payload);
        command.Parameters.AddWithValue("$utc", occurredUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = mode, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static SyncPair LegacyPair(string pairId, string name, string localFolder, string connectionId)
    {
        var identity = IdentityOf(localFolder);
        return new SyncPair(pairId, name, localFolder, identity.VolumeSerialNumber, identity.RootFileId,
            "/" + pairId, "id:" + pairId, ConnectionId: connectionId);
    }

    private static string LocalFolder(string root, string name) =>
        Directory.CreateDirectory(Path.Combine(root, name)).FullName;

    private static SyncLocalEndpointIdentity IdentityOf(string folder)
    {
        using var lease = PilotDirectoryLease.Open(folder);
        return new SyncLocalEndpointIdentity(lease.Identity.VolumeSerialNumber, lease.Identity.FileId);
    }

    private static void Claim(string description)
    {
        _cases++;
        Console.WriteLine("CASE ACS " + _cases + ": " + description);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("Activity context storage assertion failed: " + message);
        _assertions++;
        Console.WriteLine("PASS ACS " + message);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This fixture has no network authority.");
    }

    private sealed class NoTokens : IDropboxRefreshTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task WriteAsync(string value, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
    }
}
