using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// EPS2 focused proofs: the retained endpoint context surfaced on the activity projection, and the
// historical account scoping that now follows the account recorded WITH the event instead of whichever
// pair or account currently owns it.
//
// Every positive row comes from the real producer (RetainProgressForTestAsync -> RetainProgressAsync ->
// the exact SyncPair capture -> AppendActivityEventAsync) or from the real AppendActivityEventAsync, and
// every read goes through the real SQLite projection and the real RuntimeHost entry point. The rows
// staged directly into the isolated fixture database are the ones the append path refuses by design: a
// malformed, foreign or ambiguous retained context, an older payload with no context member, and the
// decision and lifecycle history an operation writes. No provider, credential or installed state is used.
internal static class EndpointHistoryProjectionChecks
{
    private static int _assertions;
    private static int _cases;

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        await OrderedConfigurationsAsync(root);
        await SameProviderAccountsAndReversalAsync(root);
        await LocalLocalWithPrimaryAccountAsync(root);
        await OperationLinkedContextsAsync(root);
        await BoundedContextMemoAsync(root);
        await MalformedAndForeignContextsAsync(root);
        await LegacyAndAccountLevelHistoryAsync(root);
        await PaginationOrderingAndFiltersAsync(root);
        await RebindRenameAndRemovalAsync(root);
        await PlannerDecisionContextsAsync(root);
        Console.WriteLine("PASS endpoint history projection: " + _assertions + " assertions across " + _cases +
            " cases; nine ordered configurations through the real producer, two same-provider accounts each" +
            " finding one event once, a reversed cloud/local pair, local/local with a real primary Dropbox" +
            " binding present, the decision and lifecycle branches reusing only their exact operation-linked" +
            " context, ambiguous and foreign association staying absent, malformed contexts staying unknown" +
            " and unattributed, an absent legacy account staying unknown, an exact legacy account attributing" +
            " only itself, an account-level row keeping its recorded account, filters and ordering applied" +
            " before the page window, a rebind that leaves account A its history and gives account B none, and" +
            " a reopened database returning the same rows with every retained history byte unchanged.");
    }

    // ----- 1. the nine ordered configurations through the real producer ---------------------------------

    private static async Task OrderedConfigurationsAsync(string root)
    {
        Claim("the nine ordered endpoint configurations reach the projection with the context their producer captured");
        var caseRoot = Path.Combine(root, "ordered");
        Directory.CreateDirectory(caseRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(caseRoot, "state", "instance.lock"));
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("eps2-fixture-installation", "eps2-ordered-profile", "eps2-fixture", "{}");
        using var client = new HttpClient(new RejectNetwork());
        using var host = HostFor(instance, client, database);
        var kinds = new[] { SyncEndpointKind.Local, SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive };
        var pairs = new List<SyncPair>();
        foreach (var leftKind in kinds)
        foreach (var rightKind in kinds)
        {
            var tag = leftKind + "-" + rightKind;
            var pair = BuildPair("Configuration " + tag, EndpointOf(caseRoot, leftKind, "l-" + tag),
                EndpointOf(caseRoot, rightKind, "r-" + tag));
            pairs.Add(pair);
            await host.RetainProgressForTestAsync(new SyncAuthorityBatchProgress(pair, "Pending", null, null, null));
        }
        Require(!host.ActivityLogIncomplete,
            "every ordered configuration retained its progress record without an activity gap");
        Require(pairs.Select(pair => pair.Id).Distinct(StringComparer.Ordinal).Count() == 9,
            "the nine ordered configurations are nine distinct folder pairs");

        var all = await host.LoadActivityProjectionPageAsync(100, 0, null, "All actions");
        Require(all.Count == 9 && all.Select(row => row.EventId).Distinct(StringComparer.Ordinal).Count() == 9,
            "the nine configurations reach the projection exactly once each, with no duplicated row from the context join");
        foreach (var pair in pairs)
        {
            var expected = SyncEndpointContext.Capture(pair);
            var row = all.Single(item => item.PairId == pair.Id);
            Require(row.EndpointContext == expected,
                "the " + pair.Name + " row carries the exact captured context: ordered sides, kinds, ports, accounts and recorded roots");
        }
        var localLocal = pairs.Single(pair => pair.Name == "Configuration Local-Local");
        Require(all.Single(row => row.PairId == localLocal.Id).EndpointContext is
                { Left.Kind: SyncEndpointKind.Local, Right.Kind: SyncEndpointKind.Local },
            "the local/local configuration is projected with two local endpoints and no cloud account");

        var accounts = pairs.Select(pair => SyncEndpointContext.Capture(pair))
            .SelectMany(context => new[] { context.Left, context.Right })
            .Where(side => side.ConnectionId is not null).Select(side => side.ConnectionId!).ToArray();
        Require(accounts.Length == 12 && accounts.Distinct(StringComparer.Ordinal).Count() == 12,
            "the twelve cloud endpoints of the nine configurations carry twelve distinct recorded accounts");
        foreach (var account in accounts)
        {
            var scoped = await host.LoadActivityProjectionPageAsync(100, 0, null, "All actions", connectionId: account);
            Require(scoped.Count > 0 && scoped.All(row => row.EndpointContext is { } context &&
                    (context.Left.ConnectionId == account || context.Right.ConnectionId == account)),
                "selecting the recorded account " + account + " returns only rows whose retained context names that exact account");
        }
        var reversed = pairs.Single(pair => pair.Name == "Configuration Dropbox-Local");
        var reversedContext = SyncEndpointContext.Capture(reversed);
        Require(reversedContext.Left.Kind == SyncEndpointKind.Dropbox &&
                reversedContext.Left.CompatibilityPort == SyncAuthorityCloudSides.LogicalRight &&
                reversedContext.Right.Kind == SyncEndpointKind.Local &&
                reversedContext.Right.CompatibilityPort == SyncAuthorityCloudSides.LogicalLeft,
            "the reversed Dropbox-left/local-right configuration keeps the shared layout ports with the cloud account on the left side");
        Require((await host.LoadActivityProjectionPageAsync(100, 0, null, "All actions",
                    connectionId: reversedContext.Left.ConnectionId)).All(row => row.PairId == reversed.Id),
            "selecting that reversed configuration's cloud account returns only that pair's rows");
    }

    // ----- 2. two accounts of one provider, and a reversed cloud/local pair ------------------------------

    private static async Task SameProviderAccountsAndReversalAsync(string root)
    {
        Claim("two recorded accounts of one provider each find the same event once, and a reversed pair keeps its recorded account");
        var caseRoot = Path.Combine(root, "same-provider");
        Directory.CreateDirectory(caseRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(caseRoot, "state", "instance.lock"));
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("eps2-fixture-installation", "eps2-same-provider-profile", "eps2-fixture", "{}");
        using var client = new HttpClient(new RejectNetwork());
        using var host = HostFor(instance, client, database);

        var pair = BuildPair("Two Dropbox accounts", CloudEndpoint(SyncEndpointKind.Dropbox, "account-one"),
            CloudEndpoint(SyncEndpointKind.Dropbox, "account-two"));
        Require(pair.LeftEndpoint is { IsLocal: false } left && pair.RightEndpoint is { IsLocal: false } right &&
                left.ConnectionId != right.ConnectionId,
            "the fixture is one pair holding two distinct accounts of the same provider");
        await host.RetainProgressForTestAsync(new SyncAuthorityBatchProgress(pair, "Pending", null, null, null));
        var retained = (await database.LoadActivityProjectionPageAsync(50, 0, null, "All actions", pairId: pair.Id)).Single();
        foreach (var account in new[] { "connection-account-one", "connection-account-two" })
        {
            var scoped = await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions", connectionId: account);
            Require(scoped.Count == 1 && scoped[0].EventId == retained.EventId,
                "the " + account + " view of the two-account pair returns that one event once, not a duplicate and not a neighbour");
        }

        var reversedLocal = LocalFolder(caseRoot, "reversed-local");
        var reversed = BuildPair("Reversed cloud to local", CloudEndpoint(SyncEndpointKind.Dropbox, "reversed-cloud"),
            SyncEndpoint.Local(reversedLocal, IdentityOf(reversedLocal)));
        Require(reversed.LeftEndpoint is { IsLocal: false } && reversed.RightEndpoint is { IsLocal: true } &&
                SyncEndpointEngineLayout.IsReversed(reversed),
            "the reversed fixture is a real cloud-left/local-right pair while the legacy projection orders the local folder first");
        await host.RetainProgressForTestAsync(new SyncAuthorityBatchProgress(reversed, "Pending", null, null, null));
        var reversedContext = SyncEndpointContext.Capture(reversed);
        Require(reversedContext.Left.CompatibilityPort == SyncAuthorityCloudSides.LogicalRight &&
                reversedContext.Right.CompatibilityPort == SyncAuthorityCloudSides.LogicalLeft,
            "the reversed pair records the retained remote port on its cloud endpoint and the local port on its local endpoint");
        var reversedScoped = await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions",
            connectionId: reversedContext.Left.ConnectionId);
        Require(reversedScoped.Count == 1 && reversedScoped[0].PairId == reversed.Id &&
                reversedScoped[0].EndpointContext == reversedContext,
            "the reversed pair is found through its recorded cloud account with its ordered context intact");
    }

    // ----- 3. local/local while a real primary Dropbox account exists -----------------------------------

    private static async Task LocalLocalWithPrimaryAccountAsync(string root)
    {
        Claim("a local/local event is never attributed to the primary Dropbox account that exists on this installation");
        var caseRoot = Path.Combine(root, "local-local");
        Directory.CreateDirectory(caseRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(caseRoot, "state", "instance.lock"));
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        await connections.SaveNewAsync(new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "acct-eps2-primary", "ns-eps2-root", "ns-eps2-home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes));
        var primary = connections.Load();
        Require(primary?.AccountId == "acct-eps2-primary",
            "a real primary Dropbox account binding exists in the same state root before the pair runs");
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("eps2-fixture-installation", "eps2-local-local-profile", "eps2-fixture", "{}");
        using var client = new HttpClient(new RejectNetwork());
        using var host = HostFor(instance, client, database);
        var localLeft = LocalFolder(caseRoot, "local-local-left");
        var localRight = LocalFolder(caseRoot, "local-local-right");
        var pair = BuildPair("Local to local", SyncEndpoint.Local(localLeft, IdentityOf(localLeft)),
            SyncEndpoint.Local(localRight, IdentityOf(localRight)));
        await host.RetainProgressForTestAsync(new SyncAuthorityBatchProgress(pair, "Pending", null, null, null));
        Require(!host.ActivityLogIncomplete, "the local/local pair's progress record is retained without a gap");
        var all = await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions");
        Require(all.Count == 1 && all[0].PairId == pair.Id &&
                all[0].EndpointContext is { Left.Kind: SyncEndpointKind.Local, Right.Kind: SyncEndpointKind.Local },
            "the local/local event stays visible under All Accounts with two local endpoints recorded");
        var localLocalContext = all[0].EndpointContext!;
        Require(localLocalContext.Left.ConnectionId is null && localLocalContext.Right.ConnectionId is null,
            "the local/local context records zero cloud accounts");
        Require((await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions",
                    connectionId: primary!.AccountId)).Count == 0,
            "the local/local event is not attributed to the primary Dropbox account that exists on this installation");
    }

    // ----- 4. the decision and lifecycle branches -------------------------------------------------------

    private static async Task OperationLinkedContextsAsync(string root)
    {
        Claim("decision and lifecycle rows reuse only the exact operation-linked context of their own pair and operation");
        var caseRoot = Path.Combine(root, "branches");
        Directory.CreateDirectory(caseRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(caseRoot, "state", "instance.lock"));
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("eps2-fixture-installation", "eps2-branch-profile", "eps2-fixture", "{}");
        using var client = new HttpClient(new RejectNetwork());
        using var host = HostFor(instance, client, database);
        await database.RegisterPairAsync("pair-branch", 1);
        var now = DateTimeOffset.UtcNow;
        var owner = LegacyPair("pair-branch", "Branch fixture", LocalFolder(caseRoot, "branch-local"), "connection-branch-owner");
        var context = SyncEndpointContext.Capture(owner);
        Require(context.PairId == "pair-branch" && context.Right.ConnectionId == "connection-branch-owner",
            "the branch fixture captures the exact pair identity and recorded cloud account");

        await StageOperationAsync(database.DatabasePath, "op-branch-1", "pair-branch", "eps2-branch-profile", "create_remote", now);
        await database.AppendActivityEventAsync(new SyncActivityEvent("evt-branch-present", now,
            [SyncActivityCategories.Sync], "Information", "Folder pair", "Sync started", "Started",
            null, "pair-branch", null, "op-branch-1", string.Empty, context));
        await StageDecisionAsync(database.DatabasePath, "decision-branch-1", "pair-branch", "operation",
            DecisionJson("op-branch-1", "branch.txt", "Branch fixture decision."), now.AddSeconds(1));
        await StageHistoryEventAsync(database.DatabasePath, "history-branch-1", "op-branch-1", "effect_committed", "{}", now.AddSeconds(2));
        var page = await database.LoadActivityProjectionPageAsync(200, 0, null, "All actions");
        var decision = page.Single(row => row.EventId == "decision:decision-branch-1");
        var lifecycle = page.Single(row => row.EventId == "history:history-branch-1");
        Require(decision.EndpointContext == context && lifecycle.EndpointContext == context,
            "the decision and lifecycle rows reuse the exact recorded context of their own pair and operation");
        Require(decision.OccurredUtc.UtcDateTime == now.AddSeconds(1).UtcDateTime &&
                lifecycle.OperationId == "op-branch-1" && decision.RelativePath == "branch.txt",
            "the reused context leaves the row's own time, path and operation identity untouched");
        var ownerScoped = await host.LoadActivityProjectionPageAsync(200, 0, null, "All actions", connectionId: "connection-branch-owner");
        Require(ownerScoped.Any(row => row.EventId == decision.EventId) && ownerScoped.Any(row => row.EventId == lifecycle.EventId),
            "both operation-linked rows are attributed to the account their own exact operation recorded");
        Require((await host.LoadActivityProjectionPageAsync(200, 0, null, "All actions", connectionId: "connection-unrelated"))
                .All(row => row.EventId != decision.EventId && row.EventId != lifecycle.EventId),
            "an unrelated account does not acquire the operation-linked rows");

        var ambiguous = LegacyPair("pair-branch", "Branch fixture other endpoints",
            LocalFolder(caseRoot, "branch-ambiguous"), "connection-ambiguous-other");
        var ambiguousContext = SyncEndpointContext.Capture(ambiguous);
        Require(ambiguousContext.PairId == context.PairId && ambiguousContext.Right.ConnectionId != context.Right.ConnectionId,
            "the ambiguity fixture holds two valid contexts that disagree for the same pair and operation");
        await database.AppendActivityEventAsync(new SyncActivityEvent("evt-ambiguous-first", now,
            [SyncActivityCategories.Sync], "Information", "Folder pair", "Sync started", "Started",
            null, "pair-branch", null, "op-branch-2", string.Empty, context));
        await database.AppendActivityEventAsync(new SyncActivityEvent("evt-ambiguous-second", now,
            [SyncActivityCategories.Sync], "Information", "Folder pair", "Sync started", "Started",
            null, "pair-branch", null, "op-branch-2", string.Empty, ambiguousContext));
        await database.AppendActivityEventAsync(new SyncActivityEvent("evt-foreign-operation", now,
            [SyncActivityCategories.Sync], "Information", "Folder pair", "Sync started", "Started",
            null, "pair-branch", null, "op-unrelated-9", string.Empty, context));
        var elsewhere = LegacyPair("pair-elsewhere", "Elsewhere", LocalFolder(caseRoot, "branch-elsewhere"), "connection-elsewhere");
        var elsewhereContext = SyncEndpointContext.Capture(elsewhere);
        await database.AppendActivityEventAsync(new SyncActivityEvent("evt-foreign-pair", now,
            [SyncActivityCategories.Sync], "Information", "Folder pair", "Sync started", "Started",
            null, "pair-elsewhere", null, "op-branch-3", string.Empty, elsewhereContext));
        await StageOperationAsync(database.DatabasePath, "op-branch-2", "pair-branch", "eps2-branch-profile", "create_remote", now.AddSeconds(3));
        await StageOperationAsync(database.DatabasePath, "op-branch-3", "pair-branch", "eps2-branch-profile", "create_remote", now.AddSeconds(5));
        await StageDecisionAsync(database.DatabasePath, "decision-branch-2", "pair-branch", "operation",
            DecisionJson("op-branch-2", "ambiguous.txt", "Ambiguous fixture."), now.AddSeconds(3));
        await StageHistoryEventAsync(database.DatabasePath, "history-branch-2", "op-branch-2", "effect_committed", "{}", now.AddSeconds(4));
        await StageDecisionAsync(database.DatabasePath, "decision-branch-3", "pair-branch", "operation",
            DecisionJson("op-branch-3", "foreign.txt", "Foreign fixture."), now.AddSeconds(5));
        await StageHistoryEventAsync(database.DatabasePath, "history-branch-3", "op-branch-3", "effect_committed", "{}", now.AddSeconds(6));

        var branched = await host.LoadActivityProjectionPageAsync(200, 0, null, "All actions");
        var disputed = branched.Single(row => row.EventId == "decision:decision-branch-2");
        var disputedLifecycle = branched.Single(row => row.EventId == "history:history-branch-2");
        Require(disputed.EndpointContext is null && disputedLifecycle.EndpointContext is null,
            "two matching presentation records that disagree leave the operation-linked context absent rather than choosing one");
        var disputedScopes = await Task.WhenAll(
            host.LoadActivityProjectionPageAsync(200, 0, null, "All actions", connectionId: "connection-branch-owner"),
            host.LoadActivityProjectionPageAsync(200, 0, null, "All actions", connectionId: "connection-ambiguous-other"));
        Require(disputedScopes.All(rows => rows.All(row => row.EventId != disputed.EventId && row.EventId != disputedLifecycle.EventId)),
            "an ambiguous row is attributed to neither disagreeing account");
        Require(branched.Any(row => row.EventId == disputed.EventId) && branched.Any(row => row.EventId == disputedLifecycle.EventId),
            "an ambiguous row stays visible under All Accounts instead of being dropped");
        var unlinked = branched.Single(row => row.EventId == "decision:decision-branch-3");
        var unlinkedLifecycle = branched.Single(row => row.EventId == "history:history-branch-3");
        Require(unlinked.EndpointContext is null && unlinkedLifecycle.EndpointContext is null,
            "a presentation record associated with a different operation is never reused for another operation");
        Require((await host.LoadActivityProjectionPageAsync(200, 0, null, "All actions", connectionId: "connection-elsewhere"))
                .All(row => row.EventId != unlinked.EventId && row.EventId != unlinkedLifecycle.EventId),
            "a presentation record retained under a different pair never attributes this pair's operation rows");
        Require(branched.Any(row => row.EventId == "history:evt-foreign-pair" && row.PairId == "pair-elsewhere" &&
                row.EndpointContext == elsewhereContext),
            "the foreign presentation record itself stays an ordinary row of its own pair carrying its own context");
        await StageDecisionAsync(database.DatabasePath, "decision-invalid-own", "pair-branch", "operation",
            JsonSerializer.Serialize(new { operationId = "op-branch-1", path = "invalid-own.txt",
                reason = "Invalid own context", endpointContext = 5 }), now.AddSeconds(10));
        await StageDecisionAsync(database.DatabasePath, "decision-valid-own", "pair-branch", "operation",
            JsonSerializer.Serialize(new { operationId = "op-branch-2", path = "valid-own.txt",
                reason = "Valid own context", endpointContext = context }), now.AddSeconds(11));
        var ownPage = await database.LoadActivityProjectionPageAsync(2, filter: "Changes",
            query: new([SyncActivityCategories.Files]));
        Require(ownPage.Count == 2 && ownPage[0].EndpointContext == context && ownPage[1].EndpointContext is null,
            "a bounded File activity page preserves valid own context despite linked ambiguity and never substitutes a valid link for invalid own context");
        Require((await database.LoadActivityProjectionPageAsync(200, filter: "Changes", connectionId: "connection-branch-owner"))
                .All(row => row.EventId != "decision:decision-invalid-own"),
            "invalid own context plus a valid matching link still attributes no account");
        await ActivityEventChecks.AssertBrowsePageEquivalenceAsync(database);
    }

    private static async Task BoundedContextMemoAsync(string root)
    {
        Claim("statement-local context memo preserves exact pair attribution and overflow; mapped history reads release their private resources");
        var caseRoot = Path.Combine(root, "context-memo");
        Directory.CreateDirectory(caseRoot);
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("eps2-fixture-installation", "eps2-memo-profile", "eps2-fixture", "{}");
        var context = SyncEndpointContext.Capture(LegacyPair("pair-memo", "Memo fixture",
            LocalFolder(caseRoot, "local"), "connection-memo"));
        var now = DateTimeOffset.UtcNow;
        var events = Enumerable.Range(0, 300).Select(index => new SyncActivityEvent("memo-" + index,
            now.AddSeconds(index), [SyncActivityCategories.Files], "Information", "File", "Uploaded", "Completed",
            null, "pair-memo", null, null, "item-" + index + ".txt",
            context with { CapturedName = "Retained snapshot " + index })).ToArray();
        await database.AppendActivityEventsAsync(events);
        var repeated = events[299] with { EventId = "memo-repeat", OccurredUtc = now.AddSeconds(301) };
        await database.AppendActivityEventAsync(repeated);
        await StageHistoryEventAsync(database.DatabasePath, "memo-foreign", null, "presentation",
            JsonSerializer.Serialize(repeated with { EventId = "memo-foreign", PairId = "pair-foreign" }), now.AddSeconds(302));
        var malformed = JsonNode.Parse(JsonSerializer.Serialize(repeated))!.AsObject();
        malformed["EventId"] = "memo-malformed";
        malformed["EndpointContext"] = "not-json";
        await StageHistoryEventAsync(database.DatabasePath, "memo-malformed", null, "presentation",
            malformed.ToJsonString(), now.AddSeconds(303));
        malformed["EventId"] = "memo-large";
        malformed["EndpointContext"] = JsonSerializer.Serialize(context with { CapturedName = new string('x', 600000) });
        await StageHistoryEventAsync(database.DatabasePath, "memo-large", null, "presentation",
            malformed.ToJsonString(), now.AddSeconds(304));
        var before = await HistoryFingerprintAsync(database.DatabasePath);
        var observedConnections = new List<SqliteConnection>();
        SyncAuthorityDatabase.ActivityReadConnectionForTest = connection =>
        {
            observedConnections.Add(connection);
            Require(!new SqliteConnectionStringBuilder(connection.ConnectionString).Pooling,
                "the actual activity reader does not retain its mapping in an execution pool");
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA cache_size;";
            Require(Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == -65536,
                "the actual activity reader uses the same bounded64MiB cache as health inspection");
            command.CommandText = "PRAGMA mmap_size;";
            var mapping = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            Require(mapping >= 0 && mapping <= 2147483648L,
                "the actual activity reader mapping is bounded and unsupported mapping may safely return zero");
        };
        IReadOnlyList<SyncAuthorityActivityProjection> page;
        try
        {
            await using var reuse = database.BeginConnectionReuseScope();
            page = await database.LoadActivityProjectionPageAsync(500, filter: "Changes");
        }
        finally { SyncAuthorityDatabase.ActivityReadConnectionForTest = null; }
        Require(observedConnections.Count == 1 && observedConnections.All(connection => connection.State == System.Data.ConnectionState.Closed),
            "disposing the real activity read releases its native connection even inside an execution reuse scope");
        Require(page.Count == 304 && events.All(item => page.Single(row => row.EventId == HistoryId(item.EventId)).EndpointContext == item.EndpointContext),
            "more than256 distinct valid contexts retain every exact captured field when the memo overflows");
        Require(page.Single(row => row.EventId == HistoryId("memo-repeat")).EndpointContext == repeated.EndpointContext &&
                page.Where(row => row.EventId is "history:memo-foreign" or "history:memo-malformed" or "history:memo-large")
                    .All(row => row.EndpointContext is null),
            "identical JSON under a different pair, invalid JSON and over-budget context cannot acquire the valid row's attribution");
        var scoped = await database.LoadActivityProjectionPageAsync(500, filter: "Changes", connectionId: "connection-memo");
        Require(scoped.Count == 301 && scoped.All(row => row.PairId == "pair-memo" && row.EndpointContext is not null),
            "selected-account filtering remains exact across memo hits, null results and overflow");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var rejected = false;
        try { await database.LoadActivityProjectionPageAsync(40, cancellationToken: cancelled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        Require(rejected && await HistoryFingerprintAsync(database.DatabasePath) == before,
            "cancellation and all optimized reads leave every retained history byte unchanged");
    }

    // ----- 5. malformed, foreign and ambiguous retained contexts ----------------------------------------

    private static async Task MalformedAndForeignContextsAsync(string root)
    {
        Claim("a malformed retained context stays unknown, attributes nothing and never fails the page");
        var caseRoot = Path.Combine(root, "malformed");
        Directory.CreateDirectory(caseRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(caseRoot, "state", "instance.lock"));
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("eps2-fixture-installation", "eps2-malformed-profile", "eps2-fixture", "{}");
        using var client = new HttpClient(new RejectNetwork());
        using var host = HostFor(instance, client, database);
        var good = SyncEndpointContext.Capture(LegacyPair("pair-malformed", "Malformed fixture",
            LocalFolder(caseRoot, "malformed-local"), "connection-malformed"));
        await database.AppendActivityEventAsync(new SyncActivityEvent("evt-malformed-control", DateTimeOffset.UtcNow,
            [SyncActivityCategories.Sync], "Information", "Folder pair", "Sync started", "Started",
            "connection-legacy-only", "pair-malformed", null, null, string.Empty, good));
        var mutations = new (string Label, SyncEndpointContext Mutated)[]
        {
            ("a context naming a different folder pair", good with { PairId = "pair-malformed-elsewhere" }),
            ("an endpoint kind this build does not register",
                good with { Right = good.Right with { Kind = (SyncEndpointKind)9 } }),
            ("a provider this build does not register", good with { Right = good.Right with { Provider = "box" } }),
            ("a compatibility port the shared layout does not assign",
                good with { Right = good.Right with { CompatibilityPort = SyncAuthorityCloudSides.LogicalLeft } }),
            ("ordered sides that are not left then right",
                good with { Left = good.Left with { Side = SyncPairSide.Right } }),
            ("cloud metadata on the local endpoint",
                good with { Left = good.Left with { ConnectionId = "connection-invented" } }),
            ("a missing ordered side", good with { Right = null! }),
            ("an unbounded captured name", good with { CapturedName = new string('n', 500) }),
            ("a blank pair identity", good with { PairId = "   " })
        };
        var staged = new List<(string Label, string EventId)>();
        var index = 0;
        foreach (var (label, mutated) in mutations)
        {
            var eventId = "evt-malformed-" + index;
            await StageHistoryEventAsync(database.DatabasePath, eventId, null, "presentation",
                JsonSerializer.Serialize(new SyncActivityEvent(eventId, DateTimeOffset.UtcNow.AddSeconds(index),
                    [SyncActivityCategories.Sync], "Information", "Folder pair", "Sync started", "Started",
                    "connection-legacy-only", "pair-malformed", null, null, string.Empty, mutated)),
                DateTimeOffset.UtcNow.AddMinutes(1));
            staged.Add((label, eventId));
            index++;
        }
        await StageHistoryEventAsync(database.DatabasePath, "evt-malformed-nolegacy", null, "presentation",
            JsonSerializer.Serialize(new SyncActivityEvent("evt-malformed-nolegacy", DateTimeOffset.UtcNow,
                [SyncActivityCategories.Sync], "Information", "Folder pair", "Sync started", "Started",
                null, "pair-malformed", null, null, string.Empty, mutations[0].Mutated)),
            DateTimeOffset.UtcNow.AddMinutes(2));
        await StageHistoryEventAsync(database.DatabasePath, "evt-malformed-nonobject", null, "presentation",
            NonObjectContextPayload("evt-malformed-nonobject", "connection-legacy-only"), DateTimeOffset.UtcNow.AddMinutes(3));

        var page = await host.LoadActivityProjectionPageAsync(200, 0, null, "All actions");
        Require(page.Count == staged.Count + 3,
            "the page still returns every row, so a malformed retained context cannot fail or truncate the whole page");
        foreach (var (label, eventId) in staged)
            Require(page.Single(row => row.EventId == HistoryId(eventId)).EndpointContext is null,
                "the projection leaves the context absent for " + label + " instead of guessing or substituting one");
        Require(page.Single(row => row.EventId == HistoryId("evt-malformed-nonobject")).EndpointContext is null,
            "the projection leaves the context absent when the retained member is not a context object");
        var byContext = await host.LoadActivityProjectionPageAsync(200, 0, null, "All actions", connectionId: "connection-malformed");
        Require(byContext.Count == 1 && byContext[0].EventId == HistoryId("evt-malformed-control"),
            "only the valid control row is attributed to the account its own context records, and no malformed context is honoured");
        Require((await host.LoadActivityProjectionPageAsync(200, 0, null, "All actions", connectionId: "connection-invented")).Count == 0,
            "an account named only inside a malformed context is never attributed a row");
        var byLegacy = await host.LoadActivityProjectionPageAsync(200, 0, null, "All actions", connectionId: "connection-legacy-only");
        Require(byLegacy.Count == staged.Count + 1 &&
                byLegacy.All(row => row.EventId != HistoryId("evt-malformed-control") && row.EndpointContext is null),
            "an unusable context is replaced only by the explicitly retained legacy single connection, and the context itself stays absent");
        Require(page.Any(row => row.EventId == HistoryId("evt-malformed-nolegacy")) &&
                (await host.LoadActivityProjectionPageAsync(200, 0, null, "All actions", connectionId: "connection-legacy-only"))
                    .All(row => row.EventId != HistoryId("evt-malformed-nolegacy")),
            "a malformed context with no explicitly retained legacy account attributes the row to no account and it stays under All Accounts");
        var fingerprint = await HistoryFingerprintAsync(database.DatabasePath);
        await host.LoadActivityProjectionPageAsync(200, 0, null, "All actions", connectionId: "connection-malformed");
        Require(await HistoryFingerprintAsync(database.DatabasePath) == fingerprint,
            "reading a page containing retained malformed contexts rewrites nothing in the append-only history");
        await ActivityEventChecks.AssertBrowsePageEquivalenceAsync(database);
    }

    // ----- 6. absent and exact legacy accounts, and account-level history --------------------------------

    private static async Task LegacyAndAccountLevelHistoryAsync(string root)
    {
        Claim("an absent legacy account stays unknown while an exact legacy account and an account-level row keep their recorded account");
        var caseRoot = Path.Combine(root, "legacy");
        Directory.CreateDirectory(caseRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(caseRoot, "state", "instance.lock"));
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        await connections.SaveNewAsync(new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "acct-eps2-legacy-primary", "ns-eps2-legacy", "ns-eps2-legacy-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes));
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("eps2-fixture-installation", "eps2-legacy-profile", "eps2-fixture", "{}");
        using var client = new HttpClient(new RejectNetwork());
        using var host = HostFor(instance, client, database);
        var absent = LegacyPair("pair-legacy-absent", "Legacy absent", LocalFolder(caseRoot, "legacy-absent"), null);
        Require(absent.ConnectionId.Length == 0, "the absent-account fixture saves no account id");
        await host.RetainProgressForTestAsync(new SyncAuthorityBatchProgress(absent, "Pending", null, null, null));
        var absentRow = (await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions"))
            .Single(row => row.PairId == absent.Id);
        Require(absentRow.EndpointContext is { Right.ConnectionId: null } &&
                absentRow.EndpointContext.Right.Provider == SyncProviders.Dropbox &&
                absentRow.EndpointContext.Right.FolderPath == absent.DropboxRoot,
            "the legacy pair records its provider and recorded root with the missing account left unknown");
        var exact = LegacyPair("pair-legacy-exact", "Legacy exact", LocalFolder(caseRoot, "legacy-exact"), "connection-legacy-exact");
        await host.RetainProgressForTestAsync(new SyncAuthorityBatchProgress(exact, "Pending", null, null, null));
        var exactScoped = await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions", connectionId: "connection-legacy-exact");
        Require(exactScoped.Count(row => row.PairId == exact.Id) == 1 && exactScoped.All(row => row.PairId != absent.Id),
            "the exactly recorded legacy account finds its own event once and cannot acquire the absent-account row");
        Require((await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions", connectionId: "   ")).Count == 0,
            "a blank requested account matches no row rather than the rows whose account is unknown");

        await database.AppendActivityEventAsync(new SyncActivityEvent("evt-account-level", DateTimeOffset.UtcNow,
            [SyncActivityCategories.Connections], "Information", "Account", "Account connected", "Connected",
            "connection-account-level", null));
        var level = await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions", connectionId: "connection-account-level");
        Require(level.Count == 1 && level[0].EventId == HistoryId("evt-account-level") && level[0].EndpointContext is null,
            "an account-level row with no pair keeps its recorded account and carries no endpoint context");
        Require((await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions", connectionId: "connection-legacy-exact"))
                .All(row => row.EventId != HistoryId("evt-account-level")),
            "an account-level row is not attributed to a different account");
        Require((await database.LoadActivityProjectionPageAsync(50, 0, null, "All actions", allowedPairIds: [],
                    connectionId: "connection-account-level")).Single().EventId == HistoryId("evt-account-level"),
            "an empty caller-supplied pair restriction removes pair rows but keeps the exact account-level row under its recorded account");
        Require((await database.LoadActivityProjectionPageAsync(50, 0, null, "All actions", allowedPairIds: [])).Count == 0,
            "an empty caller-supplied pair restriction with no requested account yields no pair rows");

        var older = new SyncActivityEvent("evt-older-payload", DateTimeOffset.UtcNow,
            [SyncActivityCategories.Sync], "Information", "Folder pair", "Sync started", "Started",
            "connection-older", "pair-older");
        var olderPayload = JsonSerializer.Serialize(older);
        Require(!olderPayload.Contains("EndpointContext", StringComparison.Ordinal),
            "an event with no context serializes with no context member, so an older retained payload stays byte-compatible");
        await StageHistoryEventAsync(database.DatabasePath, older.EventId, null, "presentation", olderPayload,
            DateTimeOffset.UtcNow.AddSeconds(5));
        var olderRow = (await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions",
            connectionId: "connection-older")).Single();
        Require(olderRow.EventId == HistoryId(older.EventId) && olderRow.EndpointContext is null,
            "an older payload with no context member is attributed by its retained legacy connection and projects no context");
    }

    // ----- 7. ordering, filters and pagination ----------------------------------------------------------

    private static async Task PaginationOrderingAndFiltersAsync(string root)
    {
        Claim("filters, ordering and pagination all run inside the query, so interleaved other-account rows cannot shift a page");
        var caseRoot = Path.Combine(root, "pagination");
        Directory.CreateDirectory(caseRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(caseRoot, "state", "instance.lock"));
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("eps2-fixture-installation", "eps2-pagination-profile", "eps2-fixture", "{}");
        using var client = new HttpClient(new RejectNetwork());
        using var host = HostFor(instance, client, database);
        var tied = new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero);
        await AppendAsync(database, "evt-p1-a", tied, "pair-page-1", "connection-page-1", "page-a.txt", SyncActivityCategories.Sync, "run-page");
        await AppendAsync(database, "evt-p1-b", tied, "pair-page-1", "connection-page-1", "page-b.txt", SyncActivityCategories.Sync, "run-page");
        await AppendAsync(database, "evt-p2-a", tied, "pair-page-2", "connection-page-2", "page-c.txt", SyncActivityCategories.Diagnostics, "run-other");
        await AppendAsync(database, "evt-p1-c", tied.AddSeconds(1), "pair-page-1", "connection-page-1", "page-d.txt", SyncActivityCategories.Sync, "run-page");

        var newest = await database.LoadActivityProjectionPageAsync(50, 0, null, "All actions");
        Require(newest.Select(row => row.EventId).SequenceEqual(
                new[] { "evt-p1-c", "evt-p2-a", "evt-p1-b", "evt-p1-a" }.Select(HistoryId)),
            "descending order is newest first and tied timestamps keep their stable retained sequence");
        var oldest = await database.LoadActivityProjectionPageAsync(50, 0, null, "All actions",
            query: new SyncActivityQuery(OldestFirst: true));
        Require(oldest.Select(row => row.EventId).SequenceEqual(
                new[] { "evt-p1-a", "evt-p1-b", "evt-p2-a", "evt-p1-c" }.Select(HistoryId)),
            "ascending order is the exact reverse of descending order for tied timestamps");
        Require((await database.LoadActivityProjectionPageAsync(1, 0, null, "All actions")).Single().EventId == HistoryId("evt-p1-c") &&
                (await database.LoadActivityProjectionPageAsync(1, 1, null, "All actions")).Single().EventId == HistoryId("evt-p2-a") &&
                (await database.LoadActivityProjectionPageAsync(1, 2, null, "All actions")).Single().EventId == HistoryId("evt-p1-b"),
            "page size one with nonzero offsets returns the successive rows of the ordered result");
        Require((await database.LoadActivityProjectionPageAsync(1, 1, null, "All actions", allowedPairIds: ["pair-page-1"]))
                .Single().EventId == HistoryId("evt-p1-b"),
            "a pair restriction is applied before the page window, so the second page of one pair is that pair's own second row");
        Require((await database.LoadActivityProjectionPageAsync(10, 0, "page-b", "All actions")).Single().EventId == HistoryId("evt-p1-b"),
            "the search filter runs inside the query rather than over one already-fetched page");
        Require((await database.LoadActivityProjectionPageAsync(1, 1, "page-", "All actions")).Single().EventId == HistoryId("evt-p2-a"),
            "search and pagination combine in one ordered query");
        Require((await database.LoadActivityProjectionPageAsync(10, 0, null, "All actions",
                    query: new SyncActivityQuery([SyncActivityCategories.Diagnostics]))).Single().EventId == HistoryId("evt-p2-a"),
            "the category filter is applied in SQL before the window");
        Require((await database.LoadActivityProjectionPageAsync(10, 0, null, "All actions",
                    query: new SyncActivityQuery(RunId: "run-page", OldestFirst: true)))
                .Select(row => row.EventId).SequenceEqual(new[] { "evt-p1-a", "evt-p1-b", "evt-p1-c" }.Select(HistoryId)),
            "the run filter is applied in SQL before the window");
        Require((await database.LoadActivityProjectionPageAsync(10, 0, null, "All actions", allowedPairIds: ["pair-page-2"]))
                .Single().EventId == HistoryId("evt-p2-a"),
            "a non-empty pair restriction keeps only the rows of the pairs it names");
        Require((await database.LoadActivityProjectionPageAsync(10, 0, null, "All actions", itemType: "TXT",
                    query: new SyncActivityQuery([SyncActivityCategories.Diagnostics])))
                .Single().EventId == HistoryId("evt-p2-a"),
            "the category and item-type filters combine inside the query and still resolve through the retained path and kind");
        var scoped = await host.LoadActivityProjectionPageAsync(10, 0, null, "All actions", connectionId: "connection-page-1");
        Require(scoped.Select(row => row.EventId).SequenceEqual(new[] { "evt-p1-c", "evt-p1-b", "evt-p1-a" }.Select(HistoryId)),
            "the host entry point returns exactly the requested account's rows in the same stable order with interleaved neighbours excluded");
        await ActivityEventChecks.AssertBrowsePageEquivalenceAsync(database);
    }

    // ----- 8. rebind, rename and removal ----------------------------------------------------------------

    private static async Task RebindRenameAndRemovalAsync(string root)
    {
        Claim("a rebind, rename or removal never moves a retained row to the account that currently owns the pair");
        var caseRoot = Path.Combine(root, "rebind");
        Directory.CreateDirectory(caseRoot);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(caseRoot, "state", "instance.lock"));
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("eps2-fixture-installation", "eps2-rebind-profile", "eps2-fixture", "{}");
        using var client = new HttpClient(new RejectNetwork());
        using var host = HostFor(instance, client, database);
        var bound = LegacyPair("pair-rebind", "Rebind fixture", LocalFolder(caseRoot, "rebind-local"), "connection-account-alpha");
        await database.RegisterPairAsync(bound.Id, bound.PolicyRevision);
        var registered = await database.RetainPairDefinitionAsync(bound, select: true);
        Require(registered.Pairs.Single().ConnectionId == "connection-account-alpha",
            "the pair is retained bound to the alpha account");
        await host.RetainProgressForTestAsync(new SyncAuthorityBatchProgress(bound, "Pending", null, null, null));
        var historical = (await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions")).Single();
        Require(historical.EndpointContext is { Right.ConnectionId: "connection-account-alpha" },
            "the historical row records the alpha account the pair used at the time");
        var fingerprint = await HistoryFingerprintAsync(database.DatabasePath);
        Require((await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions",
                    connectionId: "connection-account-alpha")).Count == 1,
            "the alpha account sees its historical row");
        Require((await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions",
                    connectionId: "connection-account-beta")).Count == 0,
            "the beta account does not yet see a row recorded against alpha");

        var rebound = bound with { ConnectionId = "connection-account-beta" };
        var reboundSet = await database.RetainPairDefinitionAsync(rebound, select: true);
        Require(reboundSet.Pairs.Single().ConnectionId == "connection-account-beta",
            "the pair really is rebound to the beta account in the current pair set");
        Require((await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions",
                    connectionId: "connection-account-alpha")).Count == 1 &&
                (await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions",
                    connectionId: "connection-account-beta")).Count == 0,
            "after the rebind the alpha account still sees its historical row and the beta account does not acquire it from current pair membership");
        Require((await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions")).Count == 1,
            "the historical row stays visible under All Accounts after the rebind");

        await database.RetainPairDefinitionAsync(rebound with { Name = "Rebind fixture renamed" }, select: true);
        Require((await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions")).Single().EndpointContext is
                { CapturedName: "Rebind fixture" },
            "a later rename does not rewrite the captured name already retained with the historical row");

        await host.RetirePairAsync(bound.Id);
        Require((await database.LoadPairSetAsync()).Pairs.All(pair => pair.Id != bound.Id),
            "the pair is removed from the current pair set");
        Require((await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions",
                    connectionId: "connection-account-alpha")).Count == 1 &&
                (await host.LoadActivityProjectionPageAsync(50, 0, null, "All actions",
                    connectionId: "connection-account-beta")).Count == 0,
            "removal of the pair leaves the recorded history attributable to exactly the account that recorded it");

        var reopenedDatabase = new SyncAuthorityDatabase(database.DatabasePath);
        using var reopenedHost = HostFor(instance, client, reopenedDatabase);
        var reopened = await reopenedHost.LoadActivityProjectionPageAsync(50, 0, null, "All actions");
        Require(reopened.Count == 1 && reopened[0].EventId == historical.EventId &&
                reopened[0].EndpointContext == historical.EndpointContext,
            "a reopened database and host return the same historical row carrying the same retained context");
        Require(await HistoryFingerprintAsync(database.DatabasePath) == fingerprint,
            "no projection read in this case rewrote, backfilled or reordered a single retained history byte");
    }

    private static async Task PlannerDecisionContextsAsync(string root)
    {
        Claim("real non-operation planner decisions retain their own context, including batched unchanged rows");
        var configurations = new[]
        {
            (SyncEndpointKind.Local, SyncEndpointKind.GoogleDrive),
            (SyncEndpointKind.GoogleDrive, SyncEndpointKind.Local),
            (SyncEndpointKind.Local, SyncEndpointKind.Local),
            (SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive)
        };
        for (var index = 0; index < configurations.Length; index++)
        {
            var caseRoot = Path.Combine(root, "planner-context-" + index);
            var (left, right) = configurations[index];
            // The retained observations are scripted planner inputs, not a claim of live provider execution.
            var pair = BuildPair("Recorded planner pair " + index,
                EndpointOf(caseRoot, left, "decision-left-" + index),
                EndpointOf(caseRoot, right, "decision-right-" + index));
            // Register the real derived identity before observing. Cloud/cloud IDs cannot be replaced by
            // an arbitrary fixture name without contradicting their ordered endpoint document.
            var (database, pairId) = await CloudPairComparisonChecks.CreateDecisionBaselineAsync(
                caseRoot, "baseline", pair.Id);
            await database.RetainPairDefinitionAsync(pair, select: true);
            var context = SyncEndpointContext.Capture(pair);
            var options = new SyncAuthorityPlannerOptions(1, "cc2a-profile", "cc2a-local-control", "cc2a-cloud-control");
            var admission = await database.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pairId, null,
                null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
            await database.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
            var unchanged = await new SyncAuthorityReconciliationPlanner(database, options, endpointContext: context, admission: admission)
                .PlanAsync(pairId);
            Require(unchanged.Single().Kind == SyncAuthorityPlannerOutcomeKind.Unchanged,
                "the real baseline produces a batched unchanged decision for " + left + "/" + right);
            const string fenceReason = "Retained decision context fixture fence";
            var outcomes = await new SyncAuthorityReconciliationPlanner(database, options,
                    membershipPathFences: new Dictionary<string, string> { ["retained.txt"] = fenceReason },
                    endpointContext: context, admission: admission).PlanAsync(pairId);
            Require(outcomes.Single() is { Kind: SyncAuthorityPlannerOutcomeKind.Blocked, OperationId: null },
                "the production membership fence creates a real blocked decision without an operation");
            var rows = await database.LoadActivityProjectionPageAsync(100, 0, null, "All actions");
            var blocked = rows.Single(row => row.Reason == fenceReason);
            Require(blocked.EndpointContext == context && blocked.OperationId is null,
                "the non-operation decision projects its exact captured name, ordered endpoints and accounts");
            Require(rows.Count(row => row.EndpointContext == context) == 2,
                "both direct blocked and batched unchanged decisions retain the same context");
            Require((await database.ListRunnableOperationsForPairAsync(pairId)).Count == 0,
                "retaining presentation metadata does not create an operation");
            var accounts = new[] { context.Left.ConnectionId, context.Right.ConnectionId }
                .Where(account => account is not null).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var account in accounts)
            {
                var scoped = await database.LoadActivityProjectionPageAsync(100, 0, null, "All actions", connectionId: account);
                Require(scoped.Count(row => row.EventId == blocked.EventId) == 1 &&
                        scoped.All(row => row.EndpointContext == context),
                    "each recorded cloud account sees only its retained decisions, once");
            }
            Require((await database.LoadActivityProjectionPageAsync(100, 0, null, "All actions",
                    connectionId: "connection-unrelated")).Count == 0,
                "an unrelated or primary account cannot acquire decision history, including local/local");

            var before = await DecisionFingerprintAsync(database.DatabasePath);
            foreach (var invalid in new[] { context with { PairId = "foreign-pair" }, context with { Left = null! } })
            {
                var rejected = false;
                try { await new SyncAuthorityReconciliationPlanner(database, options, endpointContext: invalid, admission: admission).PlanAsync(pairId); }
                catch (ArgumentException) { rejected = true; }
                Require(rejected, "the real producer refuses foreign or malformed context before planning effects");
            }
            Require(await DecisionFingerprintAsync(database.DatabasePath) == before,
                "rejected context leaves every prior decision byte unchanged");

            await database.RetainPairDefinitionAsync(pair with { Name = "Renamed current pair" }, select: true);
            var rebound = pair with { Name = "Renamed current pair", ConnectionId = "connection-rebound" };
            if (pair.EndpointDocument is not null)
            {
                // Rebuild the current endpoint document, not merely its legacy compatibility fields.
                rebound = BuildPair("Renamed current pair",
                    EndpointOf(caseRoot, left, "rebound-left-" + index),
                    EndpointOf(caseRoot, right, "rebound-right-" + index));
                if (left == SyncEndpointKind.Local || right == SyncEndpointKind.Local)
                    rebound = rebound with { Id = pairId };
                else
                {
                    // A cloud endpoint change creates an explicit new identity. Prove that the illegal
                    // same-ID substitution is rejected, then retain the legitimate successor below.
                    var rejected = false;
                    try { await database.RetainPairDefinitionAsync(rebound with { Id = pairId }, select: true); }
                    catch (InvalidDataException) { rejected = true; }
                    Require(rejected && rebound.Id != pairId,
                        "cloud endpoint rebinding requires its own derived identity, never a silent same-ID replacement");
                }
            }
            if (rebound.Id == pairId)
                await database.RetainPairDefinitionAsync(rebound, select: true);
            else
            {
                // Replacement is a two-step authority operation: prepare the new identity, then use the
                // production replacement entry point to retire the old definition and retain its history.
                await database.RegisterPairAsync(rebound.Id, rebound.PolicyRevision);
                await database.ReplacePairDefinitionAsync(pairId, rebound);
            }
            var reopened = new SyncAuthorityDatabase(database.DatabasePath);
            var historical = (await reopened.LoadActivityProjectionPageAsync(100, 0, null, "All actions"))
                .Single(row => row.EventId == blocked.EventId);
            Require(historical.EndpointContext == context && historical.EndpointContext != SyncEndpointContext.Capture(rebound),
                "reopen after real current-pair rename/rebind preserves the original decision context");
            foreach (var account in new[] { SyncEndpointContext.Capture(rebound).Left.ConnectionId,
                         SyncEndpointContext.Capture(rebound).Right.ConnectionId }.Where(account => account is not null))
                Require((await reopened.LoadActivityProjectionPageAsync(100, 0, null, "All actions", connectionId: account)).Count == 0,
                    "the newly bound account receives none of the original account's decision history");
            Require(await DecisionFingerprintAsync(database.DatabasePath) == before,
                "rename, rebind, reopen and projection reads never rewrite retained decision JSON");
        }

        var (legacyDatabase, legacyPairId) = await CloudPairComparisonChecks.CreateDecisionBaselineAsync(
            Path.Combine(root, "planner-legacy-context"), "legacy");
        var legacyAdmission = await legacyDatabase.CreateAcceptedCommandAdmissionAsync(null, "run_pair", legacyPairId,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await legacyDatabase.TransitionCommandRequestAsync(legacyAdmission.CommandRequestId, "executing");
        var legacyOptions = new SyncAuthorityPlannerOptions(1, "cc2a-profile", "cc2a-local-control", "cc2a-cloud-control");
        await new SyncAuthorityReconciliationPlanner(legacyDatabase, legacyOptions,
            membershipPathFences: new Dictionary<string, string> { ["retained.txt"] = "Legacy decision fence" }, admission: legacyAdmission).PlanAsync(legacyPairId);
        var legacy = (await legacyDatabase.LoadActivityProjectionPageAsync(100, 0, null, "All actions"))
            .Single(row => row.Reason == "Legacy decision fence");
        Require(legacy.EndpointContext is null && legacy.OperationId is null,
            "an existing caller omitting context remains neutral rather than guessing current endpoints");
        await StageDecisionAsync(legacyDatabase.DatabasePath, "malformed-own-decision", legacyPairId, "blocked",
            JsonSerializer.Serialize(new { path = "malformed.txt", reason = "Malformed own context", endpointContext = 5 }), DateTimeOffset.UtcNow);
        var legacyBefore = await DecisionFingerprintAsync(legacyDatabase.DatabasePath);
        Require((await legacyDatabase.LoadActivityProjectionPageAsync(100, 0, null, "All actions"))
                .Single(row => row.Reason == "Malformed own context").EndpointContext is null,
            "malformed retained decision context survives projection as explicitly unattributed");
        Require((await legacyDatabase.LoadActivityProjectionPageAsync(100, 0, null, "All actions",
                connectionId: "connection-unrelated")).Count == 0 &&
                await DecisionFingerprintAsync(legacyDatabase.DatabasePath) == legacyBefore,
            "legacy and malformed reads neither acquire an account nor rewrite stored payloads");
    }

    private static async Task<string> DecisionFingerprintAsync(string databasePath)
    {
        await using var connection = await ReadOnlyAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT reconciliation_decision_id,decision_json FROM reconciliation_decision ORDER BY rowid;";
        var payload = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            AppendField(payload, reader.GetString(0));
            AppendField(payload, reader.GetString(1));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    // ----- shared fixtures and helpers ------------------------------------------------------------------

    private static SyncAuthorityRuntimeHost HostFor(PilotInstanceLock instance, HttpClient client,
        SyncAuthorityDatabase database) =>
        new(instance.StateAuthority, client, new SyncPairStore(instance.StateAuthority),
            new SyncRuntimeStateStore(instance.StateAuthority), new DropboxConnectionStore(instance.StateAuthority),
            new NoTokens(), authorityDatabase: database);

    private static SyncPair LegacyPair(string pairId, string name, string localFolder, string? connectionId)
    {
        var identity = IdentityOf(localFolder);
        return new SyncPair(pairId, name, localFolder, identity.VolumeSerialNumber, identity.RootFileId,
            "/" + pairId, "id:" + pairId, ConnectionId: connectionId ?? string.Empty);
    }

    private static SyncPair BuildPair(string name, SyncEndpoint left, SyncEndpoint right)
    {
        var endpoints = new SyncPairEndpoints(left, right, SyncMode.TwoWay);
        return left.IsLocal || right.IsLocal
            ? SyncEndpointPairDraft.Create(endpoints, name)
            : SyncCloudPairConfiguration.CreateDraft(endpoints, name);
    }

    private static SyncEndpoint EndpointOf(string root, SyncEndpointKind kind, string tag) =>
        kind == SyncEndpointKind.Local
            ? SyncEndpoint.Local(LocalFolder(root, tag), IdentityOf(LocalFolder(root, tag)))
            : CloudEndpoint(kind, tag);

    private static SyncEndpoint CloudEndpoint(SyncEndpointKind kind, string tag) => kind == SyncEndpointKind.GoogleDrive
        ? SyncEndpoint.Cloud(kind, "connection-" + tag, "namespace-" + tag, "drive-" + tag, "/Drive/" + tag)
        : SyncEndpoint.Cloud(kind, "connection-" + tag, "namespace-" + tag, "id:" + tag, "/" + tag);

    private static string LocalFolder(string root, string name) =>
        Directory.CreateDirectory(Path.Combine(root, name)).FullName;

    private static SyncLocalEndpointIdentity IdentityOf(string folder)
    {
        using var lease = PilotDirectoryLease.Open(folder);
        return new SyncLocalEndpointIdentity(lease.Identity.VolumeSerialNumber, lease.Identity.FileId);
    }

    private static Task AppendAsync(SyncAuthorityDatabase database, string eventId, DateTimeOffset occurredUtc,
        string pairId, string connectionId, string relativePath, string category, string runId) =>
        database.AppendActivityEventAsync(new SyncActivityEvent(eventId, occurredUtc, [category], "Information",
            "File", "Uploaded", "Uploaded " + relativePath, connectionId, pairId, runId, null, relativePath));

    private static string DecisionJson(string operationId, string path, string reason) =>
        JsonSerializer.Serialize(new { path, reason, operationId });

    // A presentation row is retained in the append-only history, so the projection names it by the
    // retained history identity rather than by the caller's own activity event id.
    private static string HistoryId(string activityEventId) => "history:" + activityEventId;

    // A retained payload whose context member is not an object at all. The append path refuses this, so
    // an out-of-band writer is the only way such bytes can exist; the page must still survive them.
    private static string NonObjectContextPayload(string eventId, string connectionId)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(new SyncActivityEvent(eventId, DateTimeOffset.UtcNow,
            [SyncActivityCategories.Sync], "Information", "Folder pair", "Sync started", "Started",
            connectionId, "pair-malformed", null, null, string.Empty)))!.AsObject();
        node["EndpointContext"] = 5;
        return node.ToJsonString();
    }

    private static async Task StageHistoryEventAsync(string databasePath, string eventId, string? operationId,
        string eventKind, string payload, DateTimeOffset occurredUtc)
    {
        await using var connection = await ReadWriteAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO history_event(history_event_id,operation_id,event_kind,detail_json," +
            "occurred_utc) VALUES($id,$operation,$kind,$detail,$utc);";
        command.Parameters.AddWithValue("$id", eventId);
        command.Parameters.AddWithValue("$operation", operationId is null ? DBNull.Value : operationId);
        command.Parameters.AddWithValue("$kind", eventKind);
        command.Parameters.AddWithValue("$detail", payload);
        command.Parameters.AddWithValue("$utc", Utc(occurredUtc));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task StageDecisionAsync(string databasePath, string decisionId, string pairId,
        string decisionKind, string decisionJson, DateTimeOffset decidedUtc)
    {
        await using var connection = await ReadWriteAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO reconciliation_decision(reconciliation_decision_id,pair_id,object_id," +
            "decision_kind,decision_json,decided_utc) VALUES($id,$pair,NULL,$kind,$json,$utc);";
        command.Parameters.AddWithValue("$id", decisionId);
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$kind", decisionKind);
        command.Parameters.AddWithValue("$json", decisionJson);
        command.Parameters.AddWithValue("$utc", Utc(decidedUtc));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task StageOperationAsync(string databasePath, string operationId, string pairId,
        string capabilityProfileId, string kind, DateTimeOffset createdUtc)
    {
        await using var connection = await ReadWriteAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO operation(operation_id,pair_id,object_id,kind,status,priority," +
            "configuration_revision,capability_profile_id,idempotency_key,preconditions_json,created_utc," +
            "operation_group_id) VALUES($id,$pair,NULL,$kind,'planned',100,1,$profile,$key,'{}',$utc,NULL);";
        command.Parameters.AddWithValue("$id", operationId);
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$profile", capabilityProfileId);
        command.Parameters.AddWithValue("$key", "idempotency:" + operationId);
        command.Parameters.AddWithValue("$utc", Utc(createdUtc));
        await command.ExecuteNonQueryAsync();
    }

    // The exact retained history identity, so a proof can show that reading the page never rewrote it.
    private static async Task<string> HistoryFingerprintAsync(string databasePath)
    {
        await using var connection = await ReadOnlyAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT history_event_id,coalesce(operation_id,''),event_kind,detail_json," +
            "occurred_utc FROM history_event ORDER BY rowid;";
        var payload = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            for (var column = 0; column < 5; column++) AppendField(payload, reader.GetString(column));
            payload.Append('#');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private static void AppendField(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static async Task<SqliteConnection> ReadOnlyAsync(string databasePath) =>
        await OpenAsync(databasePath, SqliteOpenMode.ReadOnly);

    private static async Task<SqliteConnection> ReadWriteAsync(string databasePath) =>
        await OpenAsync(databasePath, SqliteOpenMode.ReadWrite);

    private static async Task<SqliteConnection> OpenAsync(string databasePath, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = mode, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static void Claim(string description)
    {
        _cases++;
        Console.WriteLine("CASE EPS2 " + _cases + ": " + description);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("Endpoint history projection assertion failed: " + message);
        _assertions++;
        Console.WriteLine("PASS EPS2 " + message);
    }

    private sealed class RejectNetwork : HttpMessageHandler
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
