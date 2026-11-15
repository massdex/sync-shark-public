using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// M10: actual Host-created monitors, actual Google adapter, actual retained
// cursor/hints and actual Host debounce notification. Only HTTP input is supplied.
// The callback observes notification; it NEVER implements pair scheduling itself.
internal static class GoogleHintSchedulingChecks
{
    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The isolated Host fixture requires Windows.");
        if (Directory.Exists(root)) throw new IOException("Use a fresh Google hint fixture root.");
        CloudPairHostBindingChecks.UseFixtureRoot(root);
        var failures = new List<Exception>();
        foreach (var mode in new[] { "nested", "distinct-basename", "outside-root", "removed", "trashed",
                     "malformed-ancestry", "inaccessible-ancestry", "race-same-pair", "race-other-pair", "known-moved-out" })
        {
            try { await CaseAsync(mode); Console.WriteLine("PASS M10 Google Host hint " + mode); }
            catch (Exception error)
            {
                Console.WriteLine("FAIL M10 Google Host hint " + mode + ": " + error.Message);
                failures.Add(new InvalidOperationException(mode + ": " + error.Message, error));
            }
        }
        if (failures.Count != 0) throw new AggregateException("M10 actual Host hint scope is not closed.", failures);

        async Task CaseAsync(string mode)
        {
            using var wire = new CloudPairHostBindingChecks.HostWire();
            using var gate = new OneCycleChanges(wire);
            gate.FaultMode = mode;
            using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("google-hints-" + mode, wire, gate);
            var googleA = CloudPairHostBindingChecks.HostDrive("hint-" + mode, "/Pair A");
            var googleB = googleA with { FolderId = "hint-other-root-" + mode, FolderPath = "/Pair B" };
            var dropboxA = CloudPairHostBindingChecks.HostDropbox("hint-a-" + mode, "/Target A");
            var dropboxB = CloudPairHostBindingChecks.HostDropbox("hint-b-" + mode, "/Target B");
            foreach (var endpoint in new[] { googleA, googleB, dropboxA, dropboxB })
                await rig.AddEndpointAsync(endpoint);
            var paths = mode == "distinct-basename" ? new[] { "first/report.txt", "second/report.txt" }
                : mode == "nested" ? new[] { "nested/deeper/report.txt" }
                : mode == "inaccessible-ancestry" ? new[] { "unreadable/report.txt" } : new[] { "report.txt" };
            var inputRoot = mode == "outside-root"
                ? googleA with { FolderId = "hint-outside-root-" + mode, FolderPath = "/Outside Both Pairs" }
                : googleA;
            if (mode == "outside-root") rig.RegisterFolder(googleA, inputRoot.FolderPath, inputRoot.FolderId);
            foreach (var path in paths)
            {
                var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : null;
                if (parent is not null)
                    rig.RegisterFolder(googleA, inputRoot.FolderPath + "/" + parent,
                        "hint-parent-" + mode + "-" + parent.Replace('/', '-'));
                var seeded = rig.SeedFile(inputRoot, path, "retained hint bytes " + path);
                gate.FileIds.Add(seeded.ObjectId);
            }
            Check(gate.FileIds.Distinct().Count() == paths.Length,
                "controlled same-basename files have distinct actual provider IDs");
            if (mode == "inaccessible-ancestry") gate.RefusedParentId = "hint-parent-inaccessible-ancestry-unreadable";
            if (mode == "known-moved-out")
            {
                var client = rig.DriveClient(googleA);
                await client.ListInventoryAsync(); // Populate the EXISTING prior-path snapshot via its real public inventory.
                var outsideRoot = googleA with { FolderId = "known-moved-outside", FolderPath = "/Outside" };
                rig.RegisterFolder(googleA, outsideRoot.FolderPath, outsideRoot.FolderId);
                gate.ParentOverride = outsideRoot.FolderId; // Provider now reports the SAME native object outside.
                var changed = await client.ContinueChangesAsync("synthetic-monitor-" + googleA.AccountId);
                Check(changed.Entries.Count == 1 && changed.Entries[0].RelativePath == paths[0],
                    "known moved-out native item still invalidates its prior in-root path");
                await rig.Db.VerifyHealthAsync();
                return;
            }
            var pairA = await rig.AddPairAsync("hint-a-" + mode, googleA, dropboxA);
            var pairB = await rig.AddPairAsync("hint-b-" + mode, googleB, dropboxB);
            // Pre-approved retained definitions supply input only. Host root binding,
            // monitor production, durable hint and callback decisions are not faked.
            foreach (var retained in new[] { pairA, pairB })
            {
                await rig.PrepareAsync(retained, createStaging: true);
                await rig.Db.RetainPairDefinitionAsync(retained.Pair with
                    { Enabled = true, InitialReviewApproved = true }, select: false);
            }
            var pairs = (await rig.Host.LoadPairsAsync()).Pairs.Where(pair =>
                pair.Id == pairA.Id || pair.Id == pairB.Id).ToArray();
            var writesBefore = wire.Writes;
            var operationCount = await rig.CountAsync("SELECT count(*) FROM operation;");
            var attemptsBefore = await rig.CountAsync("SELECT count(*) FROM operation_attempt;");
            var receiptsBefore = await rig.CountAsync("SELECT count(*) FROM effect_receipt;");
            var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var unresolvedNotified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notifications = 0;
            var scopes = new List<string[]>();
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondNotified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var race = mode.StartsWith("race-", StringComparison.Ordinal);
            if (race)
            {
                var secondRoot = mode == "race-same-pair" ? googleA : googleB;
                gate.NextFileIds.Add(rig.SeedFile(secondRoot, "later.txt", "new hint while callback runs").ObjectId);
            }
            var started = await rig.Host.ConfigureMonitorsAsync(pairs, async scope =>
            {
                var count = Interlocked.Increment(ref notifications);
                lock (scopes)
                {
                    scopes.Add(scope.Order(StringComparer.Ordinal).ToArray());
                    if (scopes.SelectMany(item => item).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                        .SequenceEqual(new[] { pairA.Id, pairB.Id }.Order(StringComparer.Ordinal)))
                        unresolvedNotified.TrySetResult();
                }
                notified.TrySetResult();
                if (race && count == 1) await releaseFirst.Task;
                if (count == 2) secondNotified.TrySetResult();
                return true;
            });
            try
            {
                Check(started.Count == 2 && started.All(item => item.State == "Monitoring" && item.FailureMessage is null),
                    "both actual pair monitors start under their retained roots");
                // Each blocked continuation can be requested ONLY AFTER the real
                // monitor committed its prior page. No private decision is invoked.
                await gate.BothFirstPagesCommitted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                if (mode == "outside-root") await Task.Delay(TimeSpan.FromMilliseconds(1500));
                else if (mode is "removed" or "trashed" or "malformed-ancestry" or "inaccessible-ancestry")
                    // Independent rooted feed commits may straddle the debounce interval. Observe all
                    // required admissions, not only the first callback, within the existing deadline.
                    await unresolvedNotified.Task.WaitAsync(TimeSpan.FromSeconds(15));
                else await notified.Task.WaitAsync(TimeSpan.FromSeconds(15));
                if (race)
                {
                    await notified.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    gate.ReleaseNextPage.TrySetResult();
                    await gate.BothSecondPagesCommitted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    releaseFirst.TrySetResult();
                    await secondNotified.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    var expectedSecond = mode == "race-same-pair" ? pairA.Id : pairB.Id;
                    lock (scopes) Check(scopes.Count == 2 && scopes[0].SequenceEqual(new[] { pairA.Id }) &&
                        scopes[1].SequenceEqual(new[] { expectedSecond }),
                        "older successful callback cannot consume newer same/different-pair hint watermark");
                }
                var hints = await ReadHintsAsync(rig.Db.DatabasePath);
                var roots = new Dictionary<string, string>
                {
                    [pairA.Id] = (await rig.Db.LoadActiveRootBindingIdAsync(pairA.Id, "local"))!,
                    [pairB.Id] = (await rig.Db.LoadActiveRootBindingIdAsync(pairB.Id, "local"))!
                };
                Console.WriteLine("EVIDENCE M10 Google Host hint " + mode + ": " + JsonSerializer.Serialize(new
                { pairA = pairA.Id, pairB = pairB.Id, roots, providerIds = gate.FileIds, paths,
                    notifications, gate.ChangePages, hints, scopes, writesBefore, writesAfter = wire.Writes }));
                Check(wire.Writes == writesBefore && await rig.CountAsync("SELECT count(*) FROM operation;") == operationCount &&
                    await rig.CountAsync("SELECT count(*) FROM operation_attempt;") == attemptsBefore &&
                    await rig.CountAsync("SELECT count(*) FROM effect_receipt;") == receiptsBefore,
                    "change input/monitoring performs no transfer, operation attempt or receipt");
                await rig.Db.VerifyHealthAsync();
                if (mode == "outside-root")
                    Check(hints.Count == 0 && notifications == 0,
                        "same-basename object proved outside BOTH roots cannot dirty or notify an unrelated pair");
                else if (mode is "removed" or "trashed" or "malformed-ancestry" or "inaccessible-ancestry")
                {
                    Check(hints.Count == 2 && hints.All(hint => hint.Path is null && hint.ConsumedUtc is null) &&
                        hints.Select(hint => hint.PairId).Order().SequenceEqual(new[] { pairA.Id, pairB.Id }.Order()),
                        "unknown removed/trashed/malformed/inaccessible changes retain conservative pair hints");
                    lock (scopes) Check(scopes.Count > 0 && scopes.All(scope => scope.Length > 0 &&
                        scope.All(id => id == pairA.Id || id == pairB.Id)) &&
                        scopes.SelectMany(scope => scope).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                            .SequenceEqual(new[] { pairA.Id, pairB.Id }.Order(StringComparer.Ordinal)),
                        "actual Host admits exactly both unresolved affected roots, not silent ignore");
                }
                else if (!race)
                {
                    Check(hints.Count == paths.Length && hints.All(hint => hint.PairId == pairA.Id &&
                            hint.RootBindingId == roots[pairA.Id] && hint.Path is not null &&
                            hint.Kind == "dropbox_google-drive-blob") &&
                            hints.Select(hint => hint.Path).Order().SequenceEqual(paths.Order()) &&
                            hints.Select(hint => hint.FileId).Order().SequenceEqual(gate.FileIds.Order()) &&
                            hints.All(hint => hint.ConsumedUtc is null),
                        "nested paths retain exact bound root/ancestor/native IDs; no peer root or anonymous pair-wide substitution");
                    Check(notifications == 1, "one actual Host debounce notification follows retained in-root hints");
                    lock (scopes) Check(scopes.Count == 1 && scopes[0].SequenceEqual(new[] { pairA.Id }),
                        "actual Host scoped admission contains only the affected pair");
                }
            }
            finally
            {
                releaseFirst.TrySetResult();
                await rig.Host.StopMonitorsAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }

    private static void Check(bool passed, string meaning)
    {
        if (!passed) throw new InvalidOperationException(meaning);
        Console.WriteLine("PASS M10 " + meaning);
    }

    private sealed record Hint(string PairId, string RootBindingId, string? Path, string Kind,
        string? FileId, string? ConsumedUtc);

    private static async Task<List<Hint>> ReadHintsAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.pair_id,c.root_binding_id,e.normalized_path_key,e.event_kind,
                   json_extract(e.event_json,'$.fileId'),e.consumed_utc
            FROM monitor_event e JOIN monitor_cursor c ON c.monitor_cursor_id=e.monitor_cursor_id
            WHERE e.event_kind LIKE 'dropbox_google-drive-%' ORDER BY e.pair_id,e.monitor_event_id;
            """;
        var result = new List<Hint>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new Hint(reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return result;
    }

    // The same account-wide page is supplied to BOTH rooted production adapters.
    // Root-dependent provider answers would hide the missing production filter.
    private sealed class OneCycleChanges(CloudPairHostBindingChecks.HostWire wire) : HttpMessageHandler
    {
        internal List<string> FileIds { get; } = [];
        internal List<string> NextFileIds { get; } = [];
        internal string? FaultMode { get; set; }
        internal string? RefusedParentId { get; set; }
        internal string? ParentOverride { get; set; }
        internal TaskCompletionSource ReleaseNextPage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource BothSecondPagesCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource BothFirstPagesCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ChangePages => Volatile.Read(ref _changePages);
        private int _changePages;
        private int _continuations;
        private int _secondContinuations;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri ?? throw new InvalidDataException("Missing fixture request URI.");
            if (RefusedParentId is not null && uri.AbsolutePath.EndsWith("/files/" + RefusedParentId, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            if (uri.Host != "www.googleapis.com" || !uri.AbsolutePath.EndsWith("/changes", StringComparison.Ordinal))
                return await wire.SendDirectAsync(request);
            if (request.Method != HttpMethod.Get || !uri.Query.Contains("includeRemoved=true", StringComparison.Ordinal) ||
                !uri.Query.Contains("restrictToMyDrive=true", StringComparison.Ordinal))
                throw new InvalidDataException("The real Google change-feed request lost its documented corpus/options.");
            if (uri.Query.Contains("scope-proof-after-second", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _secondContinuations) == 2) BothSecondPagesCommitted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            var second = uri.Query.Contains("scope-proof-after-first", StringComparison.Ordinal);
            if (second)
            {
                if (Interlocked.Increment(ref _continuations) == 2) BothFirstPagesCommitted.TrySetResult();
                await ReleaseNextPage.Task.WaitAsync(token);
            }
            if (!second && !uri.Query.Contains("synthetic-monitor-", StringComparison.Ordinal))
                throw new InvalidDataException("The Google monitor did not use its actual bootstrapped account cursor.");
            Interlocked.Increment(ref _changePages);
            var files = new List<JsonElement>();
            foreach (var id in second ? NextFileIds : FileIds)
            {
                using var metadata = new HttpRequestMessage(HttpMethod.Get,
                    "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(id));
                metadata.Headers.Authorization = request.Headers.Authorization;
                using var response = await wire.SendDirectAsync(metadata);
                response.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (body.RootElement.GetProperty("id").GetString() != id)
                    throw new InvalidDataException("The controlled change file does not match its actual wire identity.");
                var node = JsonNode.Parse(body.RootElement.GetRawText())!;
                if (FaultMode == "trashed") node["trashed"] = true;
                if (FaultMode == "malformed-ancestry") node["parents"] = new JsonArray("", "unverified-parent");
                if (ParentOverride is not null) node["parents"] = new JsonArray(ParentOverride);
                files.Add(JsonSerializer.SerializeToElement(node));
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                { nextPageToken = second ? "scope-proof-after-second" : "scope-proof-after-first", changes = files.Select(file => new
                    { fileId = file.GetProperty("id").GetString(), removed = FaultMode == "removed",
                        changeType = "file", file }) }), Encoding.UTF8, "application/json")
            };
        }
    }
}
