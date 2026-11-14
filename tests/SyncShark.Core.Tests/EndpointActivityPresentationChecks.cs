using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// EPS3A checks: the shared action-presentation resolver, its typed source and its compatibility guarantees.
//
// These assert exact verbs, exact affected sides and exact icon semantics from captured real
// SyncEndpointContext values, with two genuinely distinct account contexts. The resolver is a pure function of
// the typed kind, the recorded context and the retained phase, so no database is needed for these cases.
//
// The SQLite case uses the real live producer and projection. Authored checks are not executed evidence.
internal static class EndpointActivityPresentationChecks
{
    private static int _assertions;

    private static readonly SyncEndpointKind[] Kinds =
    {
        SyncEndpointKind.Local, SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive
    };

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(Path.GetFullPath(root));
        _assertions = 0;
        Console.WriteLine("EPS3A endpoint activity presentation route entered");
        NineOrderedLayoutsAndTwoAccounts();
        UnknownNumericKindAndMissingContext();
        IconSemantics();
        EveryStatusHasItsExpectedPhase();
        LegacyJsonEnumBehaviour();
        await SqliteRoundTripAsync(Path.GetFullPath(root));
        Console.WriteLine("PASS endpoint activity presentation: " + _assertions + " assertions");
    }

    private static void Assert(bool condition, string label)
    {
        _assertions++;
        if (condition) return;
        throw new InvalidDataException("EPS3A assertion failed: " + label);
    }

    /// <summary>One captured context with real recorded compatibility ports and distinct account ids.</summary>
    private static SyncEndpointContext Context(string label, SyncEndpointKind left, SyncEndpointKind right,
        string leftAccount, string rightAccount)
    {
        // Use the actual typed document capture. Identity inputs are fixtures, not filesystem effects.
        SyncEndpointRecord Side(SyncEndpointKind kind, string account, string tag) => kind == SyncEndpointKind.Local
            ? new(kind, LocalPath: Path.Combine(Path.GetTempPath(), "eps3a-" + label + "-" + tag),
                LocalVolumeSerialNumber: 1, LocalRootFileId: tag == "left" ? 1ul : 2ul)
            : new(kind, Provider: SyncEndpoints.ProviderLiteral(kind), ConnectionId: account,
                NamespaceId: "namespace-" + account, FolderId: "id:" + account, FolderPath: "/" + account);
        var pair = new SyncPair("pair-" + label, "Captured " + label, "", 0, 0, "", "")
        {
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = Side(left, leftAccount, "left"),
            RightEndpoint = Side(right, rightAccount, "right")
        };
        return SyncEndpointContext.Capture(pair);
    }

    /// <summary>
    /// All nine ordered layouts. The affected side is asserted exactly per layout, including the reversed case
    /// where the local folder is on the right, and the two accounts stay distinct in the recorded context.
    /// </summary>
    private static void NineOrderedLayoutsAndTwoAccounts()
    {
        var seen = 0;
        foreach (var left in Kinds)
        foreach (var right in Kinds)
        {
            seen++;
            var label = left + "-" + right;
            var context = Context(label, left, right, "acct-left-" + label, "acct-right-" + label);
            Assert((left == SyncEndpointKind.Local || context.Left.ConnectionId == "acct-left-" + label) &&
                   (right == SyncEndpointKind.Local || context.Right.ConnectionId == "acct-right-" + label),
                label + ": each cloud side retains its own exact account");

            // UpdateLocal targets the local compatibility port, whichever recorded side carries it.
            var localPortSide = string.Equals(context.Left.CompatibilityPort, SyncAuthorityCloudSides.LogicalLeft,
                StringComparison.Ordinal) ? SyncActivitySide.Left : SyncActivitySide.Right;
            var row = SyncEndpointActivityPresentation.Resolve(SyncAuthorityOperationKind.UpdateLocal, context,
                SyncActivityPhase.Completed, "notes.txt");
            Assert(row.Affected == localPortSide,
                label + ": UpdateLocal affects the side carrying the local port, observed " + row.Affected);
            Assert(row.Source == (localPortSide == SyncActivitySide.Left
                    ? SyncActivitySide.Right
                    : SyncActivitySide.Left),
                label + ": the source is the opposite recorded side for a directional update");
            var mixed = (left == SyncEndpointKind.Local) != (right == SyncEndpointKind.Local);
            Assert(row.Action == (mixed ? "Download" : "Copy"),
                label + ": UpdateLocal uses actual endpoint kinds, observed " + row.Action);
            var remote = SyncEndpointActivityPresentation.Resolve(SyncAuthorityOperationKind.UpdateRemote,
                context, SyncActivityPhase.Completed, "notes.txt");
            Assert(remote.Action == (mixed ? "Upload" : "Copy") &&
                   remote.Target != row.Target && remote.Source != row.Source,
                label + ": UpdateRemote has the opposite exact direction");
            foreach (var (kind, expectedTarget, expectedVerb) in new[]
            {
                (SyncAuthorityOperationKind.CreateLocal, row.Target, mixed ? "Download" : "Copy"),
                (SyncAuthorityOperationKind.CreateRemote, remote.Target, mixed ? "Upload" : "Copy")
            })
            {
                var created = SyncEndpointActivityPresentation.Resolve(kind, context, SyncActivityPhase.Completed);
                Assert(created.Action == expectedVerb && created.Target == expectedTarget,
                    label + ": create direction agrees with the independently expected endpoints");
            }

            // A delete names the affected endpoint and invents no source.
            var remove = SyncEndpointActivityPresentation.Resolve(SyncAuthorityOperationKind.DeleteRemote, context,
                SyncActivityPhase.Completed, "notes.txt");
            Assert(remove.Action == "Archive" && remove.Source == SyncActivitySide.Neutral,
                label + ": DeleteRemote archives at the affected endpoint with no invented source");
        }
        Assert(seen == 9, "all nine ordered layouts were exercised, observed " + seen);
    }

    /// <summary>An unknown numeric kind, an unknown provider value and a missing context all stay neutral.</summary>
    private static void UnknownNumericKindAndMissingContext()
    {
        var context = Context("unknown", SyncEndpointKind.Dropbox, SyncEndpointKind.Dropbox, "a1", "a2");
        var unknownKind = SyncEndpointActivityPresentation.Resolve((SyncAuthorityOperationKind)9999, context,
            SyncActivityPhase.Unknown, "x.txt");
        Assert(unknownKind.IsNeutral && unknownKind.Action == "Unknown",
            "an unknown numeric kind is neutral with an Unknown action, never a standing label");
        var noContext = SyncEndpointActivityPresentation.Resolve(SyncAuthorityOperationKind.UpdateRemote, null,
            SyncActivityPhase.Unknown, "x.txt");
        Assert(noContext.IsNeutral && noContext.Affected == SyncActivitySide.Neutral && noContext.Action == "Transfer",
            "a known transfer without context retains its action but never fabricates an orientation");
        var badContext = context with { Left = context.Left with { Kind = (SyncEndpointKind)9999 } };
        var unsupported = SyncEndpointActivityPresentation.Resolve(SyncAuthorityOperationKind.UpdateRemote,
            badContext, SyncActivityPhase.Completed);
        Assert(unsupported.IsNeutral && unsupported.Action == "Transfer" &&
               unknownKind.IconKind == SyncActivityIconKind.Internal,
            "an unsupported kind or context cannot invent a transfer direction or file entity");
        var noKind = SyncEndpointActivityPresentation.Resolve(null, context, SyncActivityPhase.Unknown);
        Assert(noKind.IsNeutral && noKind.Action == "Unknown", "a missing kind is neutral");
        Assert(SyncEndpointActivityPresentation.PhaseFor((SyncAuthorityOperationStatus)9999) ==
               SyncActivityPhase.Unknown, "an unknown numeric status stays Unknown");
    }

    /// <summary>File, folder and internal actions carry their own exact icon semantics.</summary>
    private static void IconSemantics()
    {
        var context = Context("icons", SyncEndpointKind.Local, SyncEndpointKind.Dropbox, "a1", "a2");
        var file = SyncEndpointActivityPresentation.Resolve(SyncAuthorityOperationKind.UpdateRemote, context,
            SyncActivityPhase.Completed, "a.txt");
        var folder = SyncEndpointActivityPresentation.Resolve(SyncAuthorityOperationKind.UpdateRemote, context,
            SyncActivityPhase.Completed, "docs", true);
        var internalRow = SyncEndpointActivityPresentation.Resolve(SyncAuthorityOperationKind.AllocateControl,
            context, SyncActivityPhase.Internal);
        Assert(file.Category == SyncActivityCategory.UserFile && folder.Category == SyncActivityCategory.UserFolder,
            "file and folder actions keep their own categories");
        Assert(folder.IconKind == SyncActivityIconKind.Folder,
            "a folder action carries the Core Folder icon kind, independent of any WPF key");
        Assert(internalRow.IconKind == SyncActivityIconKind.Internal &&
               internalRow.Category == SyncActivityCategory.Configuration &&
               internalRow.Title == "Sync storage setup",
            "a setup event has a useful title and branded icon and never enters File activity");
        Assert(file.IconKind == SyncActivityIconKind.File && file.IconTemplateKey is null,
            "a file action carries the Core File icon kind and returns no template key");
    }

    /// <summary>Every enum value of the status type has its expected phase, and the retained codes reuse it.</summary>
    private static void EveryStatusHasItsExpectedPhase()
    {
        var expected = new Dictionary<SyncAuthorityOperationStatus, SyncActivityPhase>
        {
            [SyncAuthorityOperationStatus.Planned] = SyncActivityPhase.Planned,
            [SyncAuthorityOperationStatus.Ready] = SyncActivityPhase.Planned,
            [SyncAuthorityOperationStatus.Leased] = SyncActivityPhase.Running,
            [SyncAuthorityOperationStatus.Validating] = SyncActivityPhase.Running,
            [SyncAuthorityOperationStatus.AllocationAuthorized] = SyncActivityPhase.Planned,
            [SyncAuthorityOperationStatus.Staged] = SyncActivityPhase.Running,
            [SyncAuthorityOperationStatus.EffectIssuing] = SyncActivityPhase.Running,
            [SyncAuthorityOperationStatus.EffectUnknown] = SyncActivityPhase.EffectUnknown,
            [SyncAuthorityOperationStatus.EffectObserved] = SyncActivityPhase.Running,
            [SyncAuthorityOperationStatus.Reconciling] = SyncActivityPhase.Running,
            [SyncAuthorityOperationStatus.Verifying] = SyncActivityPhase.Running,
            [SyncAuthorityOperationStatus.Committed] = SyncActivityPhase.Running,
            [SyncAuthorityOperationStatus.CleanupPending] = SyncActivityPhase.Running,
            [SyncAuthorityOperationStatus.Completed] = SyncActivityPhase.Completed,
            [SyncAuthorityOperationStatus.Blocked] = SyncActivityPhase.Blocked,
            [SyncAuthorityOperationStatus.Cancelled] = SyncActivityPhase.Cancelled,
            [SyncAuthorityOperationStatus.Superseded] = SyncActivityPhase.Superseded
        };
        foreach (var status in Enum.GetValues<SyncAuthorityOperationStatus>())
            Assert(expected.TryGetValue(status, out var phase) &&
                   SyncEndpointActivityPresentation.PhaseFor(status) == phase,
                "status " + status + " maps to its expected phase");
        Assert(SyncEndpointActivityPresentation.PhaseForRetainedCode("completed") == SyncActivityPhase.Completed &&
               SyncEndpointActivityPresentation.PhaseForRetainedCode("staged") == SyncActivityPhase.Running &&
               SyncEndpointActivityPresentation.PhaseForRetainedCode("not_a_code") is null &&
               SyncEndpointActivityPresentation.PhaseForRetainedCode(null) is null,
            "the retained codes reuse the one mapping and an unknown or absent code stays absent");
    }

    /// <summary>
    /// The real enum JSON behaviour: SyncEndpointContextSide carries JsonStringEnumConverter on its Side and
    /// Kind members, so a captured context round-trips its enums as strings, and the new optional activity
    /// members are omitted when absent.
    /// </summary>
    private static void LegacyJsonEnumBehaviour()
    {
        var context = Context("json", SyncEndpointKind.Local, SyncEndpointKind.Dropbox, "a1", "a2");
        var sideJson = JsonSerializer.Serialize(context.Left);
        Assert(sideJson.Contains("\"Local\"", StringComparison.Ordinal),
            "a context side serializes its kind through the existing string enum converter");
        var roundTrip = JsonSerializer.Deserialize<SyncEndpointContextSide>(sideJson);
        Assert(roundTrip is not null && roundTrip.Kind == SyncEndpointKind.Local,
            "a context side round-trips through the existing converter");
        var withKind = new SyncActivityEvent("e2", DateTimeOffset.UnixEpoch, new[] { SyncActivityCategories.Sync },
            "Information", "File", "Upload", "done", OperationKind: SyncAuthorityOperationKind.UpdateRemote,
            Phase: SyncActivityPhase.Completed);
        var json = JsonSerializer.Serialize(withKind);
        using var encoded = JsonDocument.Parse(json);
        Assert(encoded.RootElement.GetProperty("OperationKind").GetString() == "UpdateRemote" &&
               encoded.RootElement.GetProperty("Phase").GetString() == "Completed",
            "JSON enum names are explicit and not confused with database status codes");
        using var absent = JsonDocument.Parse(JsonSerializer.Serialize(withKind with { OperationKind = null, Phase = null }));
        Assert(!absent.RootElement.TryGetProperty("OperationKind", out _) &&
               !absent.RootElement.TryGetProperty("Phase", out _), "absent optional members are omitted");
        Assert(SyncEndpointActivityPresentation.KindForJsonName("UpdateRemote") == SyncAuthorityOperationKind.UpdateRemote &&
               SyncEndpointActivityPresentation.KindForJsonName("update_remote") is null &&
               SyncEndpointActivityPresentation.KindForJsonName("3") is null &&
               SyncEndpointActivityPresentation.PhaseForJsonName("Running") == SyncActivityPhase.Running &&
               SyncEndpointActivityPresentation.PhaseForJsonName("staged") is null,
            "JSON names and authority codes use their own exact parsers");
        var replayed = JsonSerializer.Deserialize<SyncActivityEvent>(json);
        Assert(replayed?.OperationKind == SyncAuthorityOperationKind.UpdateRemote &&
               replayed.Phase == SyncActivityPhase.Completed,
            "the optional typed members round-trip");
        const string legacy = "{\"EventId\":\"e1\",\"Categories\":[\"Sync log\"],\"Severity\":\"Information\","
            + "\"Entity\":\"File\",\"Action\":\"Upload\",\"Message\":\"done\"}";
        var old = JsonSerializer.Deserialize<SyncActivityEvent>(legacy);
        Assert(old is not null && old.OperationKind is null && old.Phase is null,
            "a retained payload without the new members replays with them absent");
    }
    /// <summary>
    /// ITEM B: the real round trip. Activity is appended through the REAL live producer
    /// RetainProgressForTestAsync and read back through the REAL projection LoadActivityProjectionPageAsync,
    /// over a real SQLite file, with a reopen before the read so it is a persistence read. The retained kind
    /// and phase are asserted against the exact live values, and a second row whose producer supplied no
    /// typed metadata proves that absence stays absence rather than defaulting.
    /// </summary>
    private static async Task SqliteRoundTripAsync(string root)
    {
        var databasePath = Path.Combine(root, "eps3a-activity.db");
        var db = new SyncAuthorityDatabase(databasePath);
        await db.InitializeAsync("eps3a", "eps3a-profile", "owner-fixture", "{}");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var client = new HttpClient(new RejectNetwork());
        using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, client,
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            new DropboxConnectionStore(instance.StateAuthority), new NoTokens(), authorityDatabase: db);
        var pair = new SyncPair("pair-eps3a", "EPS3A fixture", root, 1, 1, "/fixture", "id:fixture");
        await db.RegisterPairAsync(pair.Id, pair.PolicyRevision);

        // The live producer supplies the typed kind and phase on its action.
        var typed = new SyncAuthorityLiveAction("operation-eps3a", "notes.txt", "Upload", "Completed",
            "The replacement completed.", DateTimeOffset.UtcNow,
            OperationKind: SyncAuthorityOperationKind.UpdateRemote, Phase: SyncActivityPhase.Completed);
        await host.RetainProgressForTestAsync(new SyncAuthorityBatchProgress(pair, "Completed", null, null, null,
            Action: typed));
        Assert(!host.ActivityLogIncomplete, "the live producer retained the typed event without a UI callback");

        // A second row through the same producer with NO typed metadata, so absence can stay absent.
        var untagged = new SyncAuthorityLiveAction("operation-eps3a-bare", "legacy.txt", "Upload", "Completed",
            "Recorded without typed metadata.", DateTimeOffset.UtcNow);
        await host.RetainProgressForTestAsync(new SyncAuthorityBatchProgress(pair, "Completed", null, null, null,
            Action: untagged));
        Assert(!host.ActivityLogIncomplete, "the live producer retained the untagged event as well");

        // A real persistence read: reopen the very same database file before reading.
        var reopened = new SyncAuthorityDatabase(databasePath);
        await reopened.InitializeAsync("eps3a", "eps3a-profile", "owner-fixture", "{}");
        var rows = await reopened.LoadActivityProjectionPageAsync(pairId: pair.Id,
            query: new SyncActivityQuery(new[] { SyncActivityCategories.Sync }, OldestFirst: true));
        var live = rows.Single(row => string.Equals(row.OperationId, "operation-eps3a", StringComparison.Ordinal));
        Assert(live.OperationKind == SyncAuthorityOperationKind.UpdateRemote,
            "the retained kind equals the exact live kind, observed " + live.OperationKind);
        Assert(live.Phase == SyncActivityPhase.Completed,
            "the retained phase equals the exact live phase, observed " + live.Phase);
        var bare = rows.Single(row =>
            string.Equals(row.OperationId, "operation-eps3a-bare", StringComparison.Ordinal));
        Assert(bare.OperationKind is null && bare.Phase is null,
            "a row whose producer supplied no typed metadata reads back absent, never defaulted");

        var transferRows = await reopened.LoadActivityProjectionPageAsync(pairId: pair.Id, filter: "Transfers");
        Assert(transferRows.Any(row => row.OperationId == typed.OperationId) &&
               transferRows.All(row => row.OperationId != untagged.OperationId),
            "typed presentation kinds participate in SQL filtering, without inferring kinds for older rows");

        // Inputs model an immutable earlier lifecycle record whose operation now has a later status.
        // The projection under test is still the real query; only the isolated fixture rows are supplied here.
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                     { DataSource = databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO operation(operation_id,pair_id,object_id,kind,status,priority,
                    configuration_revision,capability_profile_id,idempotency_key,preconditions_json,
                    created_utc,terminal_utc,operation_group_id)
                VALUES('op-history', $pair, NULL, 'update_remote','completed',100,1,'eps3a-profile',
                    'history-key','{}',$utc,$utc,NULL);
                INSERT INTO history_event(history_event_id,operation_id,event_kind,detail_json,occurred_utc)
                VALUES('eps3a-history','op-history','effect_committed','{}',$utc);
                INSERT INTO reconciliation_decision(reconciliation_decision_id,pair_id,object_id,
                    decision_kind,decision_json,decided_utc)
                VALUES('eps3a-decision',$pair,NULL,'conflict',
                    '{"operationId":"op-history","path":"notes.txt","reason":"Both sides changed"}',$utc);
                """;
            command.Parameters.AddWithValue("$pair", pair.Id);
            command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        var later = await reopened.LoadActivityProjectionPageAsync(pairId: pair.Id);
        Assert(later.Single(row => row.EventId == "history:eps3a-history").Phase == SyncActivityPhase.Running,
            "an earlier effect-commit event is not relabelled Completed from the operation's current state");
        Assert(later.Single(row => row.EventId == "decision:eps3a-decision") is
               { OperationKind: SyncAuthorityOperationKind.UpdateRemote, Phase: SyncActivityPhase.Conflict,
                 DecisionCode: "conflict" },
            "decision metadata comes from the immutable decision, not the mutable operation status");
        Assert(later.Single(row => row.OperationId == typed.OperationId).Phase == typed.Phase,
            "later unrelated operation state leaves the exact producer phase unchanged");
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
