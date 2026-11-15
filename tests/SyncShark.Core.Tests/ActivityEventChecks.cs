using Massdex.SyncShark;

internal static class ActivityEventChecks
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await ObservationIdentityProgressChecks.RunAsync(Path.Combine(root, "observation-identity-progress"));
        var db = new SyncAuthorityDatabase(Path.Combine(root, "activity.db"));
        await db.InitializeAsync("activity-tests", "activity-profile", "owner-fixture", "{}");
        var now = DateTimeOffset.UtcNow;
        var detailedText = SyncActivityText.Safe(new string('x', 700) +
            " nested cause: provider refused HTTP 503; access_token=bounded-fixture-secret\n");
        Require(detailedText.Contains("nested cause: provider refused HTTP 503") &&
                !detailedText.Contains("bounded-fixture-secret") && !detailedText.Any(char.IsControl) &&
                SyncActivityText.Safe(new string('x', 5000)).Length == 4096,
            "Detailed log text retains causes beyond the one-line CLI budget while preserving redaction, controls and its finite cap");
        var first = new SyncActivityEvent("event-first", now, [SyncActivityCategories.Sync], "Information",
            "Folder pair", "Sync started", "Started", "account-a", "pair-a", "run-a");
        await db.AppendActivityEventAsync(first);
        await db.AppendActivityEventAsync(first);
        var collisionRejected = false;
        try { await db.AppendActivityEventAsync(first with { Message = "Changed" }); }
        catch (InvalidDataException) { collisionRejected = true; }
        Require(collisionRejected, "Event replay is idempotent and changed-identity replay is rejected");
        await db.AppendActivityEventAsync(first with { EventId = "event-second", Action = "Sync finished",
            Categories = [SyncActivityCategories.Sync, SyncActivityCategories.Diagnostics], Severity = "Success",
            Message = "access_token=fixture-secret https://example.invalid/return?code=fixture-code&state=fixture-state#private" });
        await db.AppendActivityEventAsync(first with { EventId = "event-other", ConnectionId = "account-b", PairId = "pair-b", RunId = "run-b" });
        var reopened = new SyncAuthorityDatabase(db.DatabasePath);
        var query = new SyncActivityQuery([SyncActivityCategories.Sync], RunId: "run-a", OldestFirst: true);
        var rows = await reopened.LoadActivityProjectionPageAsync(query: query);
        Require(rows.Count == 2 && rows[0].Outcome == "Sync started" && rows[1].Outcome == "Sync finished" &&
                rows[0].Sequence < rows[1].Sequence && rows.Select(row => row.EventId).Distinct().Count() == 2,
            "Distinct start/end rows survive reopen and tied timestamps use retained sequence order");
        Require(rows.All(row => row.ConnectionId == "account-a" && row.RunId == "run-a" && row.Entity == "Folder pair"),
            "Account, pair, run and entity are retained without invented file paths");
        Require(!rows[1].Reason.Contains("fixture-secret") && !rows[1].Reason.Contains("fixture-code") &&
                !rows[1].Reason.Contains("fixture-state") && !rows[1].Reason.Contains("#private"),
            "Producer redacts credential assignments and URL query/fragment before persistence");
        var multiple = await db.LoadActivityProjectionPageAsync(query: new SyncActivityQuery(
            [SyncActivityCategories.Sync, SyncActivityCategories.Diagnostics]));
        Require(multiple.Count == 3, "Selecting overlapping categories does not duplicate an event");
        var page = await db.LoadActivityProjectionPageAsync(1, 1, allowedPairIds: ["pair-a"], query: query);
        Require(page.Count == 1 && page[0].Outcome == "Sync finished", "Filtering and chronology occur before pagination");
        Require((await db.LoadActivityProjectionPageAsync(allowedPairIds: [])).Count == 0 &&
                (await db.LoadActivityProjectionPageAsync(allowedPairIds: ["pair-b"])).Single().RunId == "run-b",
            "Empty and other-account scopes cannot expose unrelated pair history");
        Require((await db.LoadActivityProjectionPageAsync(query: new SyncActivityQuery(Severity: "Success"))).Count == 1,
            "Severity is independent of category");
        var tailQuery = new SyncActivityQuery(RunId: "run-a", DetailedLog: true, NewestLogFirst: true);
        var tail = await reopened.LoadActivityProjectionPageAsync(1, query: tailQuery);
        var earlier = await reopened.LoadActivityProjectionPageAsync(1,
            query: tailQuery with { ThroughSequence = tail.Single().Sequence - 1 });
        Require(tail.Single().EventId == "history:event-second" && earlier.Single().EventId == "history:event-first",
            "A bounded live-log query reads the latest retained event immediately and can page backward without replaying old pages");
        var forward = await reopened.LoadActivityProjectionPageAsync(1,
            query: new SyncActivityQuery(RunId: "run-a", DetailedLog: true));
        var next = await reopened.LoadActivityProjectionPageAsync(1,
            query: new SyncActivityQuery(RunId: "run-a", DetailedLog: true, AfterSequence: forward.Single().Sequence));
        Require(forward.Single().EventId == "history:event-first" && next.Single().EventId == "history:event-second",
            "The existing forward log cursor used by CLI, retry and complete export remains ascending and gap-free");
        Require((await reopened.LoadActivityProjectionPageAsync(1, search: "Started", query: tailQuery)).Single().EventId == "history:event-first" &&
                (await reopened.LoadActivityProjectionPageAsync(1, allowedPairIds: ["pair-b"], query: tailQuery)).Count == 0,
            "Tail pagination applies complete-history search and exact account/pair scope before selecting the latest page");

        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var client = new HttpClient(new RejectNetwork());
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, client,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new NoTokens(), authorityDatabase: db);
        var pair = new SyncPair("pair-progress", "Progress fixture", root, 1, 1, "/fixture", "id:fixture");
        await host.RetainProgressForTestAsync(new(pair, "Pending", null, null, null));
        var transientMeasurements = new List<SyncComparisonProgress>();
        for (var index = 0; index < 50; index++)
            await host.RetainProgressForTestAsync(new(pair, "Comparing", null, null, null,
                    Comparison: new("Comparing files", "item.txt", index, 50, now, DateTimeOffset.UtcNow)),
                progress => { transientMeasurements.Add(progress.Comparison!); return Task.CompletedTask; });
        await host.RetainProgressForTestAsync(new(pair, "Completed", null, null, null));
        var progressRows = await db.LoadActivityProjectionPageAsync(pairId: pair.Id,
            query: new([SyncActivityCategories.Sync], OldestFirst: true));
        Require(progressRows.Count == 2 && progressRows[0].Outcome == "Sync started" &&
                progressRows[^1].Outcome == "Sync finished" && !host.ActivityLogIncomplete &&
                host.ActivityLogPendingCount == 0 && host.ActivityProgressCacheForTest.Entries <= 1000,
            "The real host retains the run's own start and finish while fifty transient comparison callbacks add no presentation row");
        Require(transientMeasurements.Count == 50 &&
                transientMeasurements.Select(item => (long)item.CompletedChecks)
                    .SequenceEqual(Enumerable.Range(0, 50).Select(value => (long)value)) &&
                transientMeasurements.All(item => item.TotalChecks == 50 && item.ProgressUnit == "items checked") &&
                progressRows.All(row => row.Progress is null && row.RelativePath.Length == 0),
            "Every transient measurement still reaches its live callback in submission order with its exact counts and unit and none is retained");

        // The retained operation log now carries durable terminal action records only, so the page, filter,
        // cursor and search equivalence below still runs over a real multi-row retained history.
        var detailedPair = pair with { Id = "pair-detailed-progress" };
        for (var index = 0; index < 75; index++)
            await host.RetainProgressForTestAsync(new(detailedPair, "Running", null, null, null,
                Action: new SyncAuthorityLiveAction("operation-detailed-" + index, $"folder/item-{index}.txt",
                    "Download", "Completed", $"Completed the retained file action for folder/item-{index}.txt.",
                    now, "File", "TXT", SyncAuthorityOperationKind.CreateLocal, SyncActivityPhase.Completed)));
        await host.RetainProgressForTestAsync(new(detailedPair, "Completed", null, null, null));
        var detailed = await new SyncAuthorityDatabase(db.DatabasePath).LoadActivityProjectionPageAsync(200,
            pairId: detailedPair.Id, query: new([SyncActivityCategories.Sync], OldestFirst: true));
        Require(detailed.Count == 76 && detailed.Take(75).Select(row => row.RelativePath).Distinct().Count() == 75,
            "Every distinct durable file action is retained even when all callbacks arrive within the old throttle window");
        Require(detailed[0].Reason.Contains("folder/item-0.txt") && detailed[0].RelativePath == "folder/item-0.txt" &&
                detailed[^1].Outcome == "Sync finished" && detailed[^1].RelativePath.Length == 0,
            "Retained log names the exact file action and keeps the terminal run outcome as its own row, not just entry counts");
        // Page selection may precede expensive display enrichment, but it must not
        // change any field, filter, ordering or scope of the retained operation log.
        foreach (var newestFirst in new[] { false, true })
        {
            var pageQuery = new SyncActivityQuery([SyncActivityCategories.Sync], DetailedLog: true,
                NewestLogFirst: newestFirst);
            var fullLog = await db.LoadActivityProjectionPageAsync(200, pairId: detailedPair.Id, query: pageQuery);
            var pagedLog = new List<SyncAuthorityActivityProjection>();
            for (var offset = 0; offset < fullLog.Count; offset += 7)
                pagedLog.AddRange(await db.LoadActivityProjectionPageAsync(7, offset,
                    pairId: detailedPair.Id, query: pageQuery));
            Require(fullLog.Count == 76 && SameRows(fullLog, pagedLog),
                "Bounded detailed-log pages retain every projected field and measured counter in " +
                (newestFirst ? "descending" : "ascending") + " durable order without gaps or duplicates");
            var matching = fullLog.Where(row => row.RelativePath.Contains("folder/item-7", StringComparison.Ordinal)).ToArray();
            var filteredPage = await db.LoadActivityProjectionPageAsync(2, 1, search: "folder/item-7",
                pairId: detailedPair.Id, query: pageQuery);
            Require(matching.Length == 6 && SameRows(matching.Skip(1).Take(2), filteredPage),
                "Detailed-log search applies to complete retained history before offset and page enrichment");
        }
        var databaseWork = SyncProgressDescription.Describe(new("Saving inventories", "Progress fixture", 75, 75,
            now, now, "Committing the completed inventory transaction to Sync Shark's database."));
        Require(databaseWork.Contains("Committing") && databaseWork.Contains("Progress fixture"),
            "Non-file stages describe the actual process instead of inventing an active file");
        var indexWork = SyncProgressDescription.Describe(new("Reading Folder Index", "Progress fixture", 125, 0,
            now, now, "Reading the drive metadata index; no file transfer is running.", "index records read"));
        Require(indexWork.Contains("125 index records read so far") && !indexWork.Contains("entries found") &&
            !indexWork.Contains(" of "),
            "Unknown-total progress preserves the actual work unit without inventing files or a percentage");
        var indexPair = pair with { Id = "pair-index-progress" };
        var indexMeasurements = new List<SyncComparisonProgress>();
        await host.RetainProgressForTestAsync(new(indexPair, "Checking", null, null, null,
                Comparison: new("Reading Folder Index", "Progress fixture", 125, 0, now, now,
                    "Reading the drive metadata index; no file transfer is running.", "index records read")),
            progress => { indexMeasurements.Add(progress.Comparison!); return Task.CompletedTask; });
        await host.RetainProgressForTestAsync(new(indexPair, "Completed", null, null, null));
        var indexRows = await db.LoadActivityProjectionPageAsync(pairId: indexPair.Id);
        Require(indexMeasurements.Count == 1 && indexMeasurements[0] is
                { CompletedChecks: 125, TotalChecks: 0, ProgressUnit: "index records read" } &&
                indexRows.Count == 1 && indexRows[0].Outcome == "Sync finished",
            "An unknown-total index measurement still reaches its live callback with its real unit and is not retained as a presentation row");
        await host.RetainProgressForTestAsync(new(detailedPair, "Stopped", null, "InventoryReadFailed",
            "Cannot read folder/locked.txt. access_token=fixture-secret"));
        var failure = (await new SyncAuthorityDatabase(db.DatabasePath).LoadActivityProjectionPageAsync(200,
            pairId: detailedPair.Id, query: new([SyncActivityCategories.Sync], Severity: "Error"))).Single();
        Require(failure.Reason.Contains("InventoryReadFailed") && failure.Reason.Contains("folder/locked.txt") &&
                !failure.Reason.Contains("fixture-secret"),
            "An ordinary failed operation retains its exact reason and target while redacting protected values");
        var terminalPair = pair with { Id = "pair-terminal-reasons" };
        await host.RetainProgressForTestAsync(new(terminalPair, "Checking", null, null,
            "Preparing the verified folder inventory; no file actions are running."));
        await host.RetainProgressForTestAsync(new(terminalPair, "Blocked", null, null,
            "The folder scan was cancelled or reached its time limit. No file actions were started. access_token=fixture-secret"));
        await host.RetainProgressForTestAsync(new(terminalPair, "Failed", null, null,
            "The provider refused this inventory page; retry after reconnecting.",
            Comparison: new("Reading folders", "old-item.txt", 4, 0, now, now, "Earlier inventory work.")));
        var terminalRows = await new SyncAuthorityDatabase(db.DatabasePath).LoadActivityProjectionPageAsync(20,
            pairId: terminalPair.Id, query: new([SyncActivityCategories.Sync], OldestFirst: true));
        Require(terminalRows.Count == 3 && terminalRows[0].Severity == "Information" &&
                terminalRows[0].Reason.Contains("Preparing the verified folder inventory") &&
                terminalRows[1].Severity == "Error" && terminalRows[1].Reason.Contains("time limit") &&
                terminalRows[1].Reason.Contains("No file actions were started") &&
                !terminalRows[1].Reason.Contains("fixture-secret") &&
                terminalRows[2].Severity == "Error" && terminalRows[2].Reason.Contains("provider refused") &&
                terminalRows[2].Outcome == "Failed" && terminalRows[2].RelativePath == "old-item.txt" &&
                terminalRows[2].OccurredUtc >= terminalRows[1].OccurredUtc &&
                !terminalRows[2].Reason.Contains("Earlier inventory work"),
            "Real host persistence retains untyped refusal reasons, current terminal chronology and last-target context, flushes terminal errors, redacts secrets and does not promote an ordinary status message to failure");
        var terminalCli = SyncCliOperationsBase.DescribeRunProgress(new(terminalPair, "Failed", null, null,
            "The provider refused this inventory page; retry after reconnecting.",
            Comparison: new("Reading folders", "old-item.txt", 4, 0, now, now, "Earlier inventory work.")));
        Require(terminalCli.StartsWith("Failed: ") && terminalCli.Contains("provider refused") &&
                !terminalCli.Contains("Earlier inventory work"),
            "The actual CLI progress formatter presents the terminal reason ahead of a stale comparison callback");
        await AssertBrowsePageEquivalenceAsync(db);
        await AssertRunPageEquivalenceAsync(db);
    }

    internal static async Task AssertBrowsePageEquivalenceAsync(SyncAuthorityDatabase database)
    {
        // Match the existing production read scope: SQLite may reuse its parsed schema, but every real
        // query still performs its own admission/context validation. Keep the complete matrix unchanged.
        await using var readScope = database.BeginConnectionReuseScope();
        // Every record in these fixtures is newer than this date. Supplying it forces the
        // existing fully filtered query without changing which rows legitimately match.
        var beforeFixture = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var filter in new[] { "All", "All actions", "Changes" })
        foreach (var oldest in new[] { false, true })
        foreach (var detailed in new[] { false, true })
        foreach (var newest in detailed ? new[] { false, true } : new[] { false })
        foreach (var categories in new IReadOnlyList<string>?[] { null,
                     [SyncActivityCategories.Files], [SyncActivityCategories.Sync],
                     [SyncActivityCategories.Diagnostics], [SyncActivityCategories.Files, SyncActivityCategories.Sync] })
        {
            var query = new SyncActivityQuery(categories, OldestFirst: oldest,
                DetailedLog: detailed, NewestLogFirst: newest);
            var complete = await database.LoadActivityProjectionPageAsync(200, filter: filter,
                sinceUtc: beforeFixture, query: query);
            foreach (var (limit, offset) in new[] { (1, 0), (2, 1), (200, 0), (2, 3) })
            {
                var browse = await database.LoadActivityProjectionPageAsync(limit, offset,
                    filter: filter, query: query);
                var filtered = await database.LoadActivityProjectionPageAsync(limit, offset,
                    filter: filter, sinceUtc: beforeFixture, query: query);
                Require(SameRows(filtered, browse) && SameRows(complete.Skip(offset).Take(limit), browse),
                    $"Bounded {filter} browse preserves every projected field, endpoint context and " +
                    $"page boundary against the real fully filtered query: oldest={oldest}, detailed={detailed}, newest={newest}, categories={string.Join(',', categories ?? [])}, limit={limit}, offset={offset}");
            }
            if (detailed && complete.Count > 1)
            {
                foreach (var cursorQuery in new[] {
                    query with { AfterSequence = complete.Min(row => row.Sequence) },
                    query with { ThroughSequence = complete.Max(row => row.Sequence) - 1 } })
                {
                    var reference = await database.LoadActivityProjectionPageAsync(200, filter: filter,
                        sinceUtc: beforeFixture, query: cursorQuery);
                    var cursorPage = await database.LoadActivityProjectionPageAsync(2, 1,
                        filter: filter, query: cursorQuery);
                    Require(SameRows(reference.Skip(1).Take(2), cursorPage),
                        "Detailed page selection applies exact forward/backward durable cursors before limit and display enrichment");
                }
            }
        }
    }

    private static async Task AssertRunPageEquivalenceAsync(SyncAuthorityDatabase database)
    {
        await using var readScope = database.BeginConnectionReuseScope();
        // The unscoped production query is an independent baseline: only its
        // complete already-projected rows are filtered by the fixture's run id.
        // No stand-in SQL, account validator or fabricated projection is used.
        var beforeFixture = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var detailed in new[] { false, true })
        foreach (var newest in new[] { false, true })
        foreach (var account in new string?[] { null, "account-a", "account-b", "absent-account" })
        foreach (var severity in new string?[] { null, "Success", "Error" })
        {
            var unscoped = new SyncActivityQuery(Severity: severity, DetailedLog: detailed,
                NewestLogFirst: newest);
            var reference = await database.LoadActivityProjectionPageAsync(500,
                sinceUtc: beforeFixture, query: unscoped, connectionId: account);
            foreach (var run in new[] { "run-a", "run-b", "absent-run" })
            foreach (var (limit, offset) in new[] { (1, 0), (2, 1), (200, 0) })
            {
                var actual = await database.LoadActivityProjectionPageAsync(limit, offset,
                    query: unscoped with { RunId = run }, connectionId: account);
                var expected = reference.Where(row => row.RunId == run).Skip(offset).Take(limit);
                Require(SameRows(expected, actual),
                    "Exact run early scope preserves every projected field, retained account validation, ordering, severity and page boundary");
            }
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); Console.WriteLine("PASS " + message); }

    private static bool SameRows(IEnumerable<SyncAuthorityActivityProjection> expected,
        IEnumerable<SyncAuthorityActivityProjection> actual) =>
        expected.Select(row => System.Text.Json.JsonSerializer.Serialize(row))
            .SequenceEqual(actual.Select(row => System.Text.Json.JsonSerializer.Serialize(row)));

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This fixture has no network authority.");
    }

    private sealed class NoTokens : IDropboxRefreshTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task WriteAsync(string value, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task DeleteAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
