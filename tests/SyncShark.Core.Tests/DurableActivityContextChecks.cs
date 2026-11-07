using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// DB-CONTEXT/DB-HISTORY/SS-A04/SS-D12 detached activity-context proofs (schema 10).
//
// Test source is not executed evidence: only a captured successful invocation on bound compiled bytes closes a
// case. Full UI and release acceptance are separate from this isolated database fixture.
//
// Every retained record and every read below goes through the REAL append path
// (SyncAuthorityDatabase.AppendActivityEventsAsync / AppendActivityEventAsync) and the REAL shared projection
// (LoadActivityProjectionPageAsync). The only rows staged with raw SQL are the ones the append path refuses by
// design: a colliding, foreign, inconsistent, malformed or missing dictionary entry, and a legacy CCS2 inline
// anchor with its dependent reference, which an out-of-band writer is the only way to produce.
internal static class DurableActivityContextChecks
{
    private static int _assertions;
    private static int _cases;
    private const string Separator = "\u001f";

    internal static async Task RunAsync(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Directory.CreateDirectory(root);
        await OneSnapshotAcrossManyAppendsAsync(root);
        await DistinctContextsAndDictionaryRefusalsAsync(root);
        await LegacyFormsAndReplayAsync(root);
        await NoOrphanContextAndLifetimeAsync(root);
        await VersionNineMigrationAsync(root);
        Console.WriteLine("PASS durable activity context: " + _assertions + " assertions across " + _cases +
            " cases; one immutable detached context snapshot per exact pair/run/context group serving two" +
            " thousand separately awaited appends, distinct pair, run, absent run, root, account, provider," +
            " label and endpoint order never sharing, deliberate collision, foreign, inconsistent, malformed" +
            " and missing dictionary input refused with whole-append rollback, legacy inline and one-hop event" +
            " anchors still read and replayed exactly, no orphan context after a refused or cancelled append," +
            " the real Host reusing one snapshot across separately committed terminal callbacks, the indexed" +
            " reverse reference and the foreign key holding a snapshot while events need it, and an exact" +
            " additive version 9 to version 10 migration.");
    }

    // ----- 1. one snapshot, many separately committed appends -------------------------------

    private static async Task OneSnapshotAcrossManyAppendsAsync(string root)
    {
        Claim("two thousand separately awaited appends retain exactly one immutable context snapshot, and the shared projection returns every complete original event");
        var caseRoot = Fresh(root, "many-appends");
        var path = Path.Combine(caseRoot, "activity.db");
        var database = new SyncAuthorityDatabase(path);
        await database.InitializeAsync("dac-many-install", "dac-many-profile", "owner-fixture", "{}");
        var pair = LegacyPair("pair-dac-many", "Many-append fixture", LocalFolder(caseRoot, "local"),
            "connection-dac-many");
        var context = SyncEndpointContext.Capture(pair);
        const string runId = "run-dac-many";
        var start = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        var events = Stream("dac-many-", "connection-dac-many", pair.Id, context, 0, 2000, runId, start);
        // One awaited call per event is one real writer transaction per event. The dictionary must serve all of
        // them from the single immutable snapshot it retained on the first miss.
        foreach (var item in events) await database.AppendActivityEventAsync(item);

        var expectedKey = ExpectedContextKey(pair.Id, runId, context);
        var snapshots = await ScalarAsync(path, "SELECT count(*) FROM activity_context;");
        var keyed = await ScalarAsync(path, "SELECT count(*) FROM history_event WHERE activity_context_key='" +
            expectedKey + "';");
        var unkeyed = await ScalarAsync(path, "SELECT count(*) FROM history_event WHERE activity_context_key IS NULL;");
        var rows = await StoredRowsAsync(path);
        Require(snapshots == 1 && keyed == 2000 && unkeyed == 0 && rows.Count == 2000,
            "two thousand separately committed appends retain exactly one snapshot, key every event by its exact" +
            " detached identity and leave no event without one");
        Require(rows.All(row => string.Equals(row.ContextKey, expectedKey, StringComparison.Ordinal)),
            "every retained event carries the independently recomputed lowercase-hex identity of its exact pair, run and complete context");
        Require(rows.All(row => row.Payload.Contains("\"EndpointContextKey\"", StringComparison.Ordinal) &&
                !row.Payload.Contains("\"CapturedName\"", StringComparison.Ordinal)),
            "every retained payload stores one internal reference and no event stores the full context body");
        Require(rows.Select((row, index) =>
                string.Equals(row.EventId, events[index].EventId, StringComparison.Ordinal)).All(matches => matches),
            "the complete original submission order and every logical identity are retained");

        var retainedContextBytes = await ScalarAsync(path, "SELECT coalesce(sum(encoded_bytes),0) FROM activity_context;");
        var retainedEventBytes = rows.Sum(row => Encoding.UTF8.GetByteCount(row.Payload));
        var inlineBaseline = events.Sum(item => Encoding.UTF8.GetByteCount(CanonicalPayload(item)));
        Console.WriteLine("MEASURE durable activity context many-appends: snapshots " + snapshots + ", events " +
            rows.Count + ", retained context bytes " + retainedContextBytes + ", retained event bytes " +
            retainedEventBytes + ", full inline baseline " + inlineBaseline + ", database bytes " + Length(path) +
            ", wal bytes " + Length(path + "-wal"));
        Require(retainedEventBytes + retainedContextBytes < inlineBaseline,
            "the actually retained bytes are smaller than the per-event inline context baseline they replace");

        var loaded = await ReadAllAsync(database, pair.Id);
        Require(loaded.Count == 2000 && loaded.Select(row => row.Sequence).Distinct().Count() == 2000,
            "all two thousand events remain individually retained in gap-free durable order");
        Require(loaded.All(row => row.EndpointContext == context),
            "every event hydrates the exact historical endpoint context through the shared projection");
        Require(loaded.Select((row, index) =>
                string.Equals(row.EventId, "history:" + events[index].EventId, StringComparison.Ordinal) &&
                string.Equals(row.RunId, runId, StringComparison.Ordinal) &&
                string.Equals(row.RelativePath, "folder/item-" + index.ToString("D4", CultureInfo.InvariantCulture) + ".txt", StringComparison.Ordinal) &&
                row.Progress?.Completed == index && row.Severity == (index % 8 == 7 ? "Error" : "Information"))
            .All(matches => matches),
            "no retained identity, run, operation, path, counter, severity or failure reason is lost by the reference form");

        var scoped = await ScopedAsync(database, "connection-dac-many");
        var forward = await database.LoadActivityProjectionPageAsync(500, pairId: pair.Id,
            query: new SyncActivityQuery(DetailedLog: true, OldestFirst: true));
        var next = await database.LoadActivityProjectionPageAsync(500, offset: 500, pairId: pair.Id,
            query: new SyncActivityQuery(DetailedLog: true, OldestFirst: true));
        Require(scoped.Count == 2000 && scoped.All(row => row.EndpointContext == context),
            "the recorded account reaches every event once through the hydrated detached context");
        Require(forward.Count == 500 && next.Count == 500 && next[0].Sequence > forward[^1].Sequence,
            "forward cursors over the reference form stay exact and gap-free");
        var plan = await QueryPlanAsync(path, "SELECT rowid FROM history_event WHERE activity_context_key='" +
            expectedKey + "';");
        Require(plan.Contains("history_event_activity_context", StringComparison.Ordinal),
            "the reverse-reference read over two thousand events uses the new partial index rather than scanning history (" + plan + ")");
        await RequireNoOrphanContextAsync(path);
    }

    // ----- 2. distinct contexts never share, and dictionary refusals roll back -------------

    private static async Task DistinctContextsAndDictionaryRefusalsAsync(string root)
    {
        Claim("changed pair, run, absent run, root, account, provider, label or endpoint order never shares a snapshot, and colliding, foreign, inconsistent, malformed or missing dictionary input changes nothing");
        var caseRoot = Fresh(root, "distinct");
        var path = Path.Combine(caseRoot, "activity.db");
        var database = new SyncAuthorityDatabase(path);
        await database.InitializeAsync("dac-distinct-install", "dac-distinct-profile", "owner-fixture", "{}");
        var local = LocalFolder(caseRoot, "local");
        var pair = LegacyPair("pair-dac-distinct", "Distinct fixture", local, "connection-dac-alpha");
        var renamed = LegacyPair("pair-dac-distinct", "Distinct fixture renamed", local, "connection-dac-alpha");
        var rooted = LegacyPair("pair-dac-distinct", "Distinct fixture", LocalFolder(caseRoot, "local-rooted"),
            "connection-dac-alpha");
        var accounted = LegacyPair("pair-dac-distinct", "Distinct fixture", local, "connection-dac-beta");
        var otherPair = LegacyPair("pair-dac-other", "Distinct fixture", local, "connection-dac-alpha");
        var providerPair = LegacyPair("pair-dac-distinct", "Distinct fixture", local, "connection-dac-alpha",
            provider: "googledrive");
        var context = SyncEndpointContext.Capture(pair);
        var reversed = ReversedEndpoints(context);
        Require(SyncEndpointContext.Validate(reversed, context.PairId).Count == 0,
            "the reversed ordered-side fixture is a genuinely valid endpoint context for the same pair, so its distinct identity is a real ordered-context difference");
        var variants = new (string Id, SyncPair Owner, SyncEndpointContext Context, string? RunId)[]
        {
            ("dac-share-a", pair, context, "run-dac-distinct"),
            ("dac-share-b", pair, context, "run-dac-distinct"),
            ("dac-run", pair, context, "run-dac-distinct-other"),
            ("dac-norun", pair, context, null),
            ("dac-root", rooted, SyncEndpointContext.Capture(rooted), "run-dac-distinct"),
            ("dac-account", accounted, SyncEndpointContext.Capture(accounted), "run-dac-distinct"),
            ("dac-pair", otherPair, SyncEndpointContext.Capture(otherPair), "run-dac-distinct"),
            ("dac-provider", providerPair, SyncEndpointContext.Capture(providerPair), "run-dac-distinct"),
            ("dac-label", renamed, SyncEndpointContext.Capture(renamed), "run-dac-distinct"),
            ("dac-order", pair, reversed, "run-dac-distinct")
        };
        var start = new DateTimeOffset(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);
        var batch = variants.Select((variant, index) => new SyncActivityEvent(variant.Id, start.AddSeconds(index),
            [SyncActivityCategories.Sync], "Information", "File", "Uploaded", "Uploaded folder/" + variant.Id + ".txt",
            variant.Owner.ConnectionId, variant.Owner.Id, variant.RunId, "op-" + variant.Id,
            "folder/" + variant.Id + ".txt", variant.Context)).ToArray();
        foreach (var item in batch) SyncEndpointContext.RequireRetainable(item.EndpointContext, item.PairId);

        await database.AppendActivityEventsAsync(batch);
        var distinctKeys = variants.Select(variant => ExpectedContextKey(variant.Context.PairId, variant.RunId,
            variant.Context)).Distinct(StringComparer.Ordinal).ToArray();
        var snapshots = await ScalarAsync(path, "SELECT count(*) FROM activity_context;");
        var rows = await StoredRowsAsync(path);
        var stored = rows.ToDictionary(row => row.EventId, StringComparer.Ordinal);
        Require(snapshots == 9 && distinctKeys.Length == 9 && rows.Count == batch.Length,
            "the batch retains exactly one snapshot per distinct context group and every event of the batch is retained");
        Require(batch.All(item => string.Equals(stored[item.EventId].ContextKey,
                ExpectedContextKey(item.PairId, item.RunId, item.EndpointContext!), StringComparison.Ordinal)),
            "every retained event is keyed by its own exact context identity");
        Require(string.Equals(stored["dac-share-a"].ContextKey, stored["dac-share-b"].ContextKey, StringComparison.Ordinal) &&
                !string.Equals(stored["dac-share-a"].ContextKey, stored["dac-run"].ContextKey, StringComparison.Ordinal) &&
                !string.Equals(stored["dac-run"].ContextKey, stored["dac-norun"].ContextKey, StringComparison.Ordinal) &&
                !string.Equals(stored["dac-run"].ContextKey, stored["dac-root"].ContextKey, StringComparison.Ordinal) &&
                !string.Equals(stored["dac-root"].ContextKey, stored["dac-account"].ContextKey, StringComparison.Ordinal) &&
                !string.Equals(stored["dac-account"].ContextKey, stored["dac-pair"].ContextKey, StringComparison.Ordinal) &&
                !string.Equals(stored["dac-pair"].ContextKey, stored["dac-provider"].ContextKey, StringComparison.Ordinal) &&
                !string.Equals(stored["dac-provider"].ContextKey, stored["dac-label"].ContextKey, StringComparison.Ordinal) &&
                !string.Equals(stored["dac-label"].ContextKey, stored["dac-order"].ContextKey, StringComparison.Ordinal),
            "an identical group shares one key while a changed run, absent run, root, account, pair, provider, label or endpoint order gets its own");
        var loaded = await ReadAllAsync(database, pair.Id);
        var otherLoaded = await ReadAllAsync(database, otherPair.Id);
        Require(loaded.Count == batch.Length - 1 && otherLoaded.Count == 1,
            "the pair-scoped read returns exactly the events of that pair, so a changed pair is genuinely stored apart");
        Require(loaded.All(row => variants.Where(variant => variant.Owner.Id == pair.Id)
                .Any(variant => row.EndpointContext == variant.Context)),
            "each row hydrates one of its own exact historical contexts rather than a latest-by-pair or current value");

        // Deliberate corruption of the dictionary, exactly as an out-of-band writer could leave it. Every case
        // must refuse the whole append and change no retained byte.
        var changedContent = SyncEndpointContext.Capture(otherPair);
        var refused = new (string EventId, string ContextJson, string PairId, string? RunId, string Label)[]
        {
            ("dac-refuse-collision", JsonSerializer.Serialize(changedContent), pair.Id, "run-dac-distinct",
                "retained snapshot whose content does not match the context the event names"),
            ("dac-refuse-foreign-pair", JsonSerializer.Serialize(context), "pair-dac-foreign", "run-dac-distinct",
                "retained snapshot recorded under a foreign pair"),
            ("dac-refuse-foreign-run", JsonSerializer.Serialize(context), pair.Id, "run-dac-foreign",
                "retained snapshot recorded under a foreign run")
        };
        var eventKey = ExpectedContextKey(pair.Id, "run-dac-distinct", context);
        foreach (var (eventId, contextJson, storedPair, storedRun, label) in refused)
        {
            await StageContextAsync(path, eventKey, storedPair, storedRun, contextJson);
            var before = await FingerprintAsync(path);
            var neighbour = new SyncActivityEvent("dac-neighbour-" + eventId, start, [SyncActivityCategories.Sync],
                "Information", "File", "Uploaded", "Uploaded folder/neighbour.txt", "connection-dac-alpha",
                pair.Id, "run-dac-distinct", "op-neighbour", "folder/neighbour.txt", context);
            var rejected = false;
            try
            {
                await database.AppendActivityEventsAsync([neighbour, batch[0] with { EventId = eventId }]);
            }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected &&
                    await ScalarAsync(path, "SELECT count(*) FROM history_event WHERE history_event_id='dac-neighbour-" + eventId + "';") == 0 &&
                    await ScalarAsync(path, "SELECT count(*) FROM history_event WHERE history_event_id='" + eventId + "';") == 0 &&
                    await FingerprintAsync(path) == before,
                "a refused " + label + " rejects the whole append, retains neither event and rewrites no byte");
            await RemoveContextAsync(path, eventKey);
        }

        // A malformed snapshot is valid JSON but cannot be the named context; a snapshot whose own PairId names
        // another pair and a missing snapshot must both stay visibly unknown. Each is staged as a real
        // presentation row so the read gate itself is exercised, never a synthetic shortcut.
        var malformedKey = ExpectedContextKey(pair.Id, "run-dac-malformed", context);
        await StageContextAsync(path, malformedKey, pair.Id, "run-dac-malformed", "\"not-a-context\"");
        var malformed = Staged("dac-read-malformed", pair.Id, "run-dac-malformed", context, malformedKey);
        await StagePresentationAsync(path, malformed);
        var inconsistentJson = JsonSerializer.Serialize(context with { PairId = otherPair.Id });
        var inconsistentKey = ExpectedContextKey(pair.Id, "run-dac-inconsistent", context);
        await StageContextAsync(path, inconsistentKey, pair.Id, "run-dac-inconsistent", inconsistentJson);
        var inconsistent = Staged("dac-read-inconsistent", pair.Id, "run-dac-inconsistent", context, inconsistentKey);
        await StagePresentationAsync(path, inconsistent);
        var missing = Staged("dac-read-missing", pair.Id, "run-dac-missing", context,
            ExpectedContextKey(pair.Id, "run-dac-missing", context));
        await StagePresentationAsync(path, missing);
        var controlKey = ExpectedContextKey(pair.Id, "run-dac-control", context);
        await StageContextAsync(path, controlKey, pair.Id, "run-dac-control", JsonSerializer.Serialize(context));
        var control = Staged("dac-read-control", pair.Id, "run-dac-control", context, controlKey);
        await StagePresentationAsync(path, control);
        var staged = await database.LoadActivityProjectionPageAsync(500,
            query: new SyncActivityQuery(DetailedLog: true));
        Require(staged.Single(row => row.EventId == "history:" + malformed.EventId).EndpointContext is null &&
                staged.Single(row => row.EventId == "history:" + inconsistent.EventId).EndpointContext is null &&
                staged.Single(row => row.EventId == "history:" + missing.EventId).EndpointContext is null,
            "a malformed, snapshot-inconsistent or missing dictionary entry leaves its row without any context instead of substituting one");
        Require(staged.Single(row => row.EventId == "history:" + control.EventId).EndpointContext == context,
            "a control row whose snapshot agrees exactly still hydrates its complete context, so the refusals are not a broken lookup");
        Require((await ScopedAsync(database, "connection-legacy-none")).Count == 0,
            "a context-bearing row with an unusable reference is never re-attributed through its retained legacy connection");
        await RequireNoOrphanContextAsync(path);
    }

    // ----- 3. legacy forms stay readable and every replay stays exact ----------------------

    private static async Task LegacyFormsAndReplayAsync(string root)
    {
        Claim("legacy inline and one-hop event anchors still read and replay exactly, the detached reference replays to its identical canonical event, and a changed payload under one identity refuses");
        var caseRoot = Fresh(root, "legacy");
        var path = Path.Combine(caseRoot, "activity.db");
        var database = new SyncAuthorityDatabase(path);
        await database.InitializeAsync("dac-legacy-install", "dac-legacy-profile", "owner-fixture", "{}");
        var pair = LegacyPair("pair-dac-legacy", "Legacy fixture", LocalFolder(caseRoot, "local"),
            "connection-dac-legacy");
        var context = SyncEndpointContext.Capture(pair);
        const string runId = "run-dac-legacy";
        var start = new DateTimeOffset(2026, 10, 12, 2, 0, 0, TimeSpan.Zero);

        // A legacy CCS2 pair staged out of band: the first row carries the full inline context and the second
        // references that anchor by event identity. The real append path can no longer produce this form.
        var anchor = new SyncActivityEvent("dac-legacy-anchor", start, [SyncActivityCategories.Sync],
            "Information", "File", "Uploaded", "Uploaded folder/anchor.txt", "connection-dac-legacy", pair.Id,
            runId, "op-dac-legacy", "folder/anchor.txt", context);
        var dependent = anchor with { EventId = "dac-legacy-dependent", OccurredUtc = start.AddSeconds(1),
            RelativePath = "folder/dependent.txt" };
        await StagePresentationAsync(path, anchor);
        await StagePresentationAsync(path, dependent, "dac-legacy-anchor");
        var legacyRead = await ReadAllAsync(database, pair.Id);
        Require(legacyRead.Count == 2 && legacyRead.All(row => row.EndpointContext == context),
            "the unchanged legacy one-hop anchor still hydrates its dependent row exactly");

        // The same two identities replayed through the REAL append path must be idempotent: the legacy
        // reference expands to the identical canonical record, so no row and no byte is added.
        var beforeLegacyReplay = await FingerprintAsync(path);
        await database.AppendActivityEventAsync(dependent);
        await database.AppendActivityEventAsync(anchor);
        Require(await FingerprintAsync(path) == beforeLegacyReplay &&
                await ScalarAsync(path, "SELECT count(*) FROM history_event;") == 2 &&
                await ScalarAsync(path, "SELECT count(*) FROM activity_context;") == 0,
            "replaying a legacy inline anchor and its dependent reference adds no row, no byte and no context snapshot");

        // A new-form record, then its replay through a genuinely reopened database instance.
        var retained = new SyncActivityEvent("dac-detached", start.AddSeconds(2), [SyncActivityCategories.Sync],
            "Information", "File", "Uploaded", "Uploaded folder/detached.txt", "connection-dac-legacy", pair.Id,
            runId, "op-dac-detached", "folder/detached.txt", context);
        await database.AppendActivityEventAsync(retained);
        var beforeDetachedReplay = await FingerprintAsync(path);
        var reopened = new SyncAuthorityDatabase(path);
        await reopened.AppendActivityEventsAsync([retained]);
        await reopened.AppendActivityEventAsync(retained);
        Require(await FingerprintAsync(path) == beforeDetachedReplay &&
                await ScalarAsync(path, "SELECT count(*) FROM activity_context;") == 1 &&
                await ScalarAsync(path, "SELECT count(*) FROM history_event;") == 3,
            "replaying a detached reference, alone and inside a batch, through a reopened database is idempotent and creates no orphan snapshot");

        // A changed payload under one event identity must refuse and roll back the whole append.
        var unchanged = await FingerprintAsync(path);
        var changedMessageRefused = false;
        var neighbour = retained with { EventId = "dac-detached-neighbour" };
        try
        {
            await reopened.AppendActivityEventsAsync([neighbour,
                retained with { Message = "different retained authority" }]);
        }
        catch (InvalidDataException) { changedMessageRefused = true; }
        Require(changedMessageRefused && await FingerprintAsync(path) == unchanged &&
                await ScalarAsync(path, "SELECT count(*) FROM history_event WHERE history_event_id='dac-detached-neighbour';") == 0,
            "changed content under one identity refuses the whole append and retains no neighbouring event");
        var changedContextRefused = false;
        try
        {
            await reopened.AppendActivityEventAsync(retained with
                { EndpointContext = context with { CapturedName = "Different captured name" } });
        }
        catch (InvalidDataException) { changedContextRefused = true; }
        Require(changedContextRefused && await FingerprintAsync(path) == unchanged &&
                await ScalarAsync(path, "SELECT count(*) FROM activity_context;") == 1,
            "a changed historical context under one identity refuses rather than adding a second snapshot");
        await RequireNoOrphanContextAsync(path);
    }

    // ----- 4. no orphan context, real Host reuse, and the retained lifetime -----------------

    private static async Task NoOrphanContextAndLifetimeAsync(string root)
    {
        Claim("a refused or cancelled append leaves no orphan snapshot, the real Host reuses one snapshot across separately committed terminal callbacks, and the foreign key holds the snapshot while events need it");
        var caseRoot = Fresh(root, "lifetime");
        var path = Path.Combine(caseRoot, "activity.db");
        var database = new SyncAuthorityDatabase(path);
        await database.InitializeAsync("dac-life-install", "dac-life-profile", "owner-fixture", "{}");
        var pair = LegacyPair("pair-dac-life", "Lifetime fixture", LocalFolder(caseRoot, "local"),
            "connection-dac-life");
        var context = SyncEndpointContext.Capture(pair);
        const string runId = "run-dac-life";
        var start = new DateTimeOffset(2026, 10, 12, 3, 0, 0, TimeSpan.Zero);

        // The first committed event of the group, so a later failure has something to roll back towards.
        var first = new SyncActivityEvent("dac-life-first", start, [SyncActivityCategories.Sync],
            "Information", "File", "Uploaded", "Uploaded folder/first.txt", "connection-dac-life", pair.Id,
            runId, "op-dac-life", "folder/first.txt", context);
        await database.AppendActivityEventAsync(first);
        var before = await FingerprintAsync(path);

        // A real in-transaction failure: the orphan candidate is the FIRST row of the batch, so its snapshot
        // would already be inserted by the time the second row refuses. The whole transaction must roll back.
        var orphanCandidate = first with { EventId = "dac-life-orphan",
            EndpointContext = context with { CapturedName = "Lifetime fixture renamed" } };
        var orphanKey = ExpectedContextKey(orphanCandidate.PairId, orphanCandidate.RunId,
            orphanCandidate.EndpointContext!);
        var failedAppendRefused = false;
        try
        {
            await database.AppendActivityEventsAsync([orphanCandidate,
                first with { Message = "different retained authority" }]);
        }
        catch (InvalidDataException) { failedAppendRefused = true; }
        Require(failedAppendRefused && await FingerprintAsync(path) == before &&
                await ScalarAsync(path, "SELECT count(*) FROM history_event WHERE history_event_id='dac-life-orphan';") == 0 &&
                await ScalarAsync(path, "SELECT count(*) FROM activity_context WHERE context_key='" + orphanKey + "';") == 0,
            "a failed append inside the real writer transaction rolls back its snapshot insertion and leaves no orphan context");

        // A cancelled append must also leave no orphan, and a silent success is never accepted as a rollback.
        var cancelledCandidate = first with { EventId = "dac-life-cancelled",
            EndpointContext = context with { CapturedName = "Lifetime fixture cancelled" } };
        var cancelledKey = ExpectedContextKey(cancelledCandidate.PairId, cancelledCandidate.RunId,
            cancelledCandidate.EndpointContext!);
        var cancelled = false;
        try
        {
            using var source = new CancellationTokenSource();
            source.Cancel();
            await database.AppendActivityEventAsync(cancelledCandidate, source.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        var cancelledRows = await ScalarAsync(path,
            "SELECT count(*) FROM history_event WHERE history_event_id='dac-life-cancelled';");
        var cancelledContext = await ScalarAsync(path,
            "SELECT count(*) FROM activity_context WHERE context_key='" + cancelledKey + "';");
        Require(cancelled ? cancelledRows == 0 && cancelledContext == 0
                : cancelledRows == 1 && cancelledContext == 1,
            "a cancelled append either rolls back completely or retains its event together with its snapshot, never a snapshot alone");
        await RequireNoOrphanContextAsync(path);

        // The real Host path: two separately committed terminal callbacks of one group must reuse the single
        // snapshot, because the dictionary is a database lifetime rather than one writer transaction.
        using var instance = PilotInstanceLock.Acquire(Path.Combine(caseRoot, "state", "instance.lock"));
        await database.RegisterPairAsync(pair.Id, pair.PolicyRevision);
        await database.RetainPairDefinitionAsync(pair, select: true);
        using var client = new HttpClient(new FirstSyncEffectChecks.NoNetworkHandler());
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, client,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new FirstSyncEffectChecks.FixtureCredentialStore(),
            authorityDatabase: database);
        var snapshotsBeforeHost = await ScalarAsync(path, "SELECT count(*) FROM activity_context;");
        var eventsBeforeHost = await ScalarAsync(path, "SELECT count(*) FROM history_event;");
        await host.RetainProgressForTestAsync(Terminal(pair, start.AddSeconds(10), "dac-host-one", "folder/one.bin"));
        await host.RetainProgressForTestAsync(Terminal(pair, start.AddSeconds(20), "dac-host-two", "folder/two.bin"));
        var snapshotsAfterHost = await ScalarAsync(path, "SELECT count(*) FROM activity_context;");
        var eventsAfterHost = await ScalarAsync(path, "SELECT count(*) FROM history_event;");
        Require(eventsAfterHost == eventsBeforeHost + 2 && snapshotsAfterHost == snapshotsBeforeHost + 1,
            "two separately committed real Host callbacks of one group retain exactly one additional snapshot, not one per commit");
        Require((await database.LoadActivityProjectionPageAsync(500, pairId: pair.Id,
                    query: new SyncActivityQuery(DetailedLog: true))).Count == eventsAfterHost,
            "the real Host callbacks are retained, keyed and readable through the shared projection");

        // The snapshot cannot be removed while an event still names it.
        var foreignKeys = await QueryAsync(path, "PRAGMA foreign_key_list(history_event);");
        Require(foreignKeys.Any(row => row.Length >= 7 &&
                string.Equals(row[3], "activity_context_key", StringComparison.Ordinal) &&
                string.Equals(row[2], "activity_context", StringComparison.Ordinal) &&
                string.Equals(row[6], "RESTRICT", StringComparison.OrdinalIgnoreCase)),
            "history_event declares activity_context_key as a RESTRICT reference to activity_context");
        var referencedKey = ExpectedContextKey(pair.Id, runId, context);
        var removalRefused = false;
        try { await ExecuteAsync(path, "DELETE FROM activity_context WHERE context_key='" + referencedKey + "';"); }
        catch (SqliteException error)
        {
            removalRefused = error.Message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase);
        }
        Require(removalRefused,
            "the database itself refuses to remove a snapshot while a retained event still references it");
        await RequireNoOrphanContextAsync(path);
    }

    // ----- 5. the exact additive version 9 to version 10 migration -------------------------

    private static async Task VersionNineMigrationAsync(string root)
    {
        Claim("a version 9 database migrates additively to version 10, keeps every retained row byte for byte and retains its rollback copy");
        var caseRoot = Fresh(root, "migration");
        var path = Path.Combine(caseRoot, "v9-fixture.db");
        var fixture = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        await fixture.OpenAsync();
        await ExecuteRawAsync(fixture, SyncAuthoritySchema.Sql + "\n" + SyncAuthoritySchemaSupplement.Sql);
        foreach (var migration in new[]
                 {
                     SyncAuthoritySchemaMigration3.Sql, SyncAuthoritySchemaMigration4.Sql,
                     SyncAuthoritySchemaMigration5.Sql, SyncAuthoritySchemaMigration6.Sql,
                     SyncAuthoritySchemaMigration7.Sql, SyncAuthoritySchemaMigration8.Sql,
                     SyncAuthoritySchemaMigration9.Sql
                 })
            await ExecuteRawAsync(fixture, migration);
        const string legacyPayload = "{\"EventId\":\"dac-migration-legacy\",\"Categories\":[\"Sync log\"],\"Severity\":\"Information\",\"Entity\":\"File\",\"Action\":\"Uploaded\",\"Message\":\"Retained before migration.\",\"RelativePath\":\"\"}";
        await ExecuteRawAsync(fixture,
            "INSERT INTO history_event(history_event_id,operation_id,event_kind,detail_json,occurred_utc) " +
            "VALUES('dac-migration-legacy',NULL,'presentation','" + legacyPayload + "'," +
            "'2026-01-01T00:00:00.0000000+00:00');");
        await ExecuteRawAsync(fixture,
            "INSERT INTO schema_migration(schema_version,applied_utc,source_revision) " +
            "VALUES(9,'2026-01-01T00:00:00.0000000+00:00','fixture-v9');");
        await ExecuteRawAsync(fixture, "PRAGMA user_version=9;");
        await fixture.DisposeAsync();

        var database = new SyncAuthorityDatabase(path);
        await database.InitializeAsync("dac-migration-install", "dac-migration-profile", "owner-fixture", "{}");
        Require(await ScalarAsync(path, "PRAGMA user_version;") == 10,
            "the version 9 fixture migrates to schema version 10");
        Require(await ScalarAsync(path, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='activity_context';") == 1 &&
                await ScalarAsync(path, "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name IN " +
                    "('activity_context_pair_run','history_event_activity_context');") == 2,
            "the migrated database carries the detached context table and both read indexes");
        var columns = await QueryAsync(path, "PRAGMA table_info(history_event);");
        Require(columns.Any(row => row.Length >= 4 &&
                string.Equals(row[1], "activity_context_key", StringComparison.Ordinal) &&
                string.Equals(row[3], "0", StringComparison.Ordinal)),
            "history_event gains exactly one nullable activity_context_key column");
        Require(await ScalarAsync(path, "SELECT count(*) FROM history_event WHERE " +
                    "history_event_id='dac-migration-legacy' AND detail_json='" + legacyPayload + "';") == 1,
            "the pre-existing presentation row keeps its identity and its exact payload bytes");
        Require(await ScalarAsync(path, "SELECT count(*) FROM schema_migration WHERE schema_version IN (9,10);") == 2,
            "the version 10 receipt is added beside the retained version 9 receipt");
        Require(File.Exists(path + ".v9-rollback"),
            "the version 9 rollback copy is retained before migration");

        // The migrated database passes exact current-schema admission and does not gain a second receipt.
        var reopened = new SyncAuthorityDatabase(path);
        await reopened.InitializeAsync("dac-migration-install", "dac-migration-profile", "owner-fixture", "{}");
        Require(await ScalarAsync(path, "PRAGMA user_version;") == 10 &&
                await ScalarAsync(path, "SELECT count(*) FROM schema_migration WHERE schema_version=10;") == 1,
            "reopening the migrated database passes exact current-schema admission without adding a second version 10 receipt");
    }

    // ----- shared fixture helpers ---------------------------------------------------------------

    private static string Fresh(string root, string name)
    {
        var path = Path.Combine(root, name);
        if (Directory.Exists(path)) throw new IOException("Use a fresh isolated durable activity context fixture.");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string LocalFolder(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static SyncPair LegacyPair(string pairId, string name, string localFolder, string connectionId,
        string provider = "dropbox")
    {
        using var lease = PilotDirectoryLease.Open(localFolder);
        return new SyncPair(pairId, name, localFolder, lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
            "/" + pairId, "id:" + pairId, ConnectionId: connectionId, Provider: provider);
    }

    // The same ordered sides with their retained order swapped. A credential-free two-cloud pair document is
    // not available inside this fixture, so the reversed context is constructed directly and then validated by
    // the existing shared validator before it is used as a real ordered-context difference.
    private static SyncEndpointContext ReversedEndpoints(SyncEndpointContext context) =>
        new(context.PairId, context.CapturedName, context.Right with { Side = SyncPairSide.Left },
            context.Left with { Side = SyncPairSide.Right });

    private static IReadOnlyList<SyncActivityEvent> Stream(string prefix, string connectionId, string pairId,
        SyncEndpointContext context, int first, int count, string runId, DateTimeOffset start) =>
        Enumerable.Range(first, count).Select(index => new SyncActivityEvent(
            prefix + index.ToString("D4", CultureInfo.InvariantCulture), start.AddSeconds(index),
            index % 8 == 7
                ? [SyncActivityCategories.Sync, SyncActivityCategories.Diagnostics]
                : [SyncActivityCategories.Sync],
            index % 8 == 7 ? "Error" : "Information", "File",
            index % 8 == 7 ? "Upload failed" : "Uploaded",
            index % 8 == 7
                ? "Failed to upload folder/item-" + index.ToString("D4", CultureInfo.InvariantCulture) +
                  ".txt: the provider refused the request."
                : "Uploaded folder/item-" + index.ToString("D4", CultureInfo.InvariantCulture) + ".txt",
            connectionId, pairId, runId, "op-dac-" + index.ToString("D4", CultureInfo.InvariantCulture),
            "folder/item-" + index.ToString("D4", CultureInfo.InvariantCulture) + ".txt", context,
            index % 3 == 0 ? SyncAuthorityOperationKind.CreateRemote : SyncAuthorityOperationKind.UpdateRemote,
            index % 2 == 0 ? SyncActivityPhase.Running : SyncActivityPhase.Completed,
            new SyncActivityProgress(index, index % 3 == 0 ? null : 4096,
                index % 2 == 0 ? "bytes" : "items checked", index))).ToArray();

    private static SyncActivityEvent Staged(string eventId, string pairId, string runId,
        SyncEndpointContext context, string? contextKey) =>
        new(eventId, new DateTimeOffset(2026, 10, 12, 4, 0, 0, TimeSpan.Zero), [SyncActivityCategories.Sync],
            "Information", "File", "Uploaded", "Uploaded folder/staged.txt", "connection-legacy-none", pairId,
            runId, "op-staged", "folder/staged.txt", context);

    // The documented detached snapshot identity, recomputed here from its own inputs rather than read back
    // from the implementation under test.
    private static string ExpectedContextKey(string? pairId, string? runId, SyncEndpointContext context) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((pairId ?? "") + Separator + (runId ?? "") +
            Separator + JsonSerializer.Serialize(context)))).ToLowerInvariant();

    private static string CanonicalPayload(SyncActivityEvent item) => JsonSerializer.Serialize(item with
    {
        OccurredUtc = item.OccurredUtc.ToUniversalTime(), Action = SyncActivityText.Safe(item.Action),
        Message = SyncActivityText.Safe(item.Message)
    });

    private sealed record StoredRow(string EventId, string Payload, string? ContextKey);

    private static async Task<IReadOnlyList<StoredRow>> StoredRowsAsync(string databasePath)
    {
        var rows = await QueryAsync(databasePath, "SELECT history_event_id,detail_json," +
            "activity_context_key FROM history_event WHERE event_kind='presentation' ORDER BY rowid;");
        return rows.Select(row => new StoredRow(row[0], row[1], row[2].Length == 0 ? null : row[2])).ToArray();
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
        SyncAuthorityDatabase database, string connectionId) =>
        await database.LoadActivityProjectionPageAsync(500, connectionId: connectionId,
            query: new SyncActivityQuery(DetailedLog: true));

    private static async Task RequireNoOrphanContextAsync(string databasePath)
    {
        Require(await ScalarAsync(databasePath, "SELECT count(*) FROM activity_context c WHERE NOT EXISTS(" +
                "SELECT 1 FROM history_event h WHERE h.activity_context_key=c.context_key);") == 0,
            "every retained context snapshot is referenced by at least one retained event, so no orphan context exists");
    }

    private static async Task StageContextAsync(string databasePath, string contextKey, string pairId,
        string? runId, string contextJson)
    {
        await ExecuteAsync(databasePath, "INSERT INTO activity_context(context_key,pair_id,run_id,context_json," +
            "encoded_bytes,created_utc) VALUES('" + contextKey + "','" + pairId + "'," +
            (runId is null ? "NULL" : "'" + runId + "'") + ",'" +
            contextJson.Replace("'", "''", StringComparison.Ordinal) + "'," +
            Encoding.UTF8.GetByteCount(contextJson).ToString(CultureInfo.InvariantCulture) +
            ",'2026-10-12T00:00:00.0000000+00:00');");
    }

    private static async Task RemoveContextAsync(string databasePath, string contextKey) =>
        await ExecuteAsync(databasePath, "DELETE FROM activity_context WHERE context_key='" + contextKey + "';");

    private static async Task StagePresentationAsync(string databasePath, SyncActivityEvent item,
        string? legacyAnchor = null)
    {
        var node = JsonSerializer.SerializeToNode(item)!.AsObject();
        if (legacyAnchor is not null)
            node["EndpointContext"] = new JsonObject { ["EndpointContextEventId"] = legacyAnchor };
        await using var connection = await OpenAsync(databasePath, SqliteOpenMode.ReadWrite);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO history_event(history_event_id,operation_id,event_kind,detail_json," +
            "occurred_utc,activity_context_key) VALUES($id,NULL,'presentation',$detail,$utc,NULL);";
        command.Parameters.AddWithValue("$id", item.EventId);
        command.Parameters.AddWithValue("$detail", node.ToJsonString());
        command.Parameters.AddWithValue("$utc", Utc(item.OccurredUtc));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = mode, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await ExecuteRawAsync(connection, "PRAGMA foreign_keys=ON;");
        return connection;
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = await OpenAsync(databasePath, SqliteOpenMode.ReadWrite);
        await ExecuteRawAsync(connection, sql);
    }

    private static async Task ExecuteRawAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = await OpenAsync(databasePath, SqliteOpenMode.ReadOnly);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string[]>> QueryAsync(string databasePath, string sql)
    {
        await using var connection = await OpenAsync(databasePath, SqliteOpenMode.ReadOnly);
        var rows = new List<string[]>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
                row[index] = reader.IsDBNull(index)
                    ? "" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)!;
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<string> QueryPlanAsync(string databasePath, string sql)
    {
        var rows = await QueryAsync(databasePath, "EXPLAIN QUERY PLAN " + sql);
        return string.Join(" ", rows.Select(row => string.Join(" ", row)));
    }

    private static async Task<string> FingerprintAsync(string databasePath)
    {
        var payload = new StringBuilder();
        foreach (var row in await QueryAsync(databasePath, "SELECT history_event_id,detail_json," +
                     "coalesce(activity_context_key,'') FROM history_event ORDER BY rowid;"))
            payload.Append(string.Join(Separator, row)).Append('#');
        foreach (var row in await QueryAsync(databasePath, "SELECT context_key,pair_id,coalesce(run_id,'')," +
                     "context_json,encoded_bytes FROM activity_context ORDER BY context_key;"))
            payload.Append(string.Join(Separator, row)).Append('#');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static SyncAuthorityBatchProgress Terminal(SyncPair pair, DateTimeOffset occurredUtc, string id,
        string path) =>
        new(pair, "Running", null, null, null,
            Action: new SyncAuthorityLiveAction(id, path, "Download", "Completed",
                "Completed the retained download of " + path + ".", occurredUtc, "File", "BIN",
                SyncAuthorityOperationKind.CreateLocal, SyncActivityPhase.Completed,
                new SyncActivityProgress(1, 256, "bytes", 10)));

    private static void Claim(string description)
    {
        _cases++;
        Console.WriteLine("CASE durable activity context " + _cases + ": " + description);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("Durable activity context assertion failed: " + message);
        _assertions++;
        Console.WriteLine("PASS DAC " + message);
    }
}
