using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Massdex.SyncShark;
using IOPath = System.IO.Path;
using CandidateRequest = Massdex.SyncShark.SyncAuthorityDatabase.SyncAuthorityCloudPairCandidate;

// BIND1-3 focused proofs: the real SQLite lifecycle of blocked unpublished cloud-pair candidate roots, the
// EXISTING journalled private-stage allocator and the one guarded atomic publication and rebind.
//
// Every lifecycle call is the shipped production method: RegisterProviderAccountAsync, RegisterPairAsync,
// RegisterCloudEndpointRootBindingAsync, RegisterCloudRecoveryRootAsync, the staged
// PrepareCloudPairCandidateAsync and PublishCloudPairCandidateAsync, the existing
// SyncAuthorityLocalControlNamespaceAllocator.BindAsync and the existing guarded root validator. Only the
// provider hierarchy input is fixture-supplied, and it is supplied as the typed snapshot the guard already
// consumes; the transaction, the guard, the allocator and every refusal are the production ones.

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class CloudPairAuthoringChecks
{
    private const string InstallationId = "installation-cloud-pair-authoring-319";
    private const string ProfileId = "profile-cloud-pair-authoring-319";
    private const string SourceRevision = "cloud-pair-authoring-319";
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The cloud-pair authoring proofs require Windows NTFS identities and protected ACLs.");
        _root = IOPath.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated cloud-pair authoring fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;

        await CaseAsync("A-prepare-and-publication",
            "A prepared candidate is invisible with no active roots, an exact replay reproduces it unchanged, and the EXISTING allocator plus one guarded publication publish it with both roots active",
            PrepareAndPublicationAsync);
        await CaseAsync("B-published-and-retired-candidates",
            "A published or retired candidate is refused without change",
            PublishedAndRetiredAsync);
        await CaseAsync("C-exact-preimage-mismatch",
            "A drifted candidate root preimage is refused without any write",
            ExactPreimageMismatchAsync);
        await CaseAsync("D-account-changes",
            "A candidate account retired or substituted between prepare and publish refuses atomically",
            AccountChangesAsync);
        await CaseAsync("E-foreign-and-orphan-roots",
            "A swapped, foreign, third-pair-overlapping or Recovery-overlapping candidate refuses atomically",
            ForeignAndOrphanRootsAsync);
        await CaseAsync("F-enabled-or-approved-definition",
            "An enabled or already approved candidate definition is refused rather than published",
            EnabledOrApprovedAsync);
        await CaseAsync("G-rebind-retaining-one-endpoint",
            "An endpoint-changing rebind replaces the old pair, retains the shared endpoint identity and leaves exactly one old root out of scope",
            RebindAsync);
        await CaseAsync("H-race-and-rollback",
            "Nonterminal or planning-referenced work appearing after prepare refuses, and a right-side activation failure rolls the left activation and the retirement back",
            RaceAndRollbackAsync);
        await CaseAsync("I-reopen-and-retry",
            "A refused preparation reopens and retries exactly, and the schema is unchanged by the whole lifecycle",
            ReopenAndRetryAsync);
        await CaseAsync("J-nested-containment",
            "With real complete typed corpus chains for both candidate roots, a live third-pair root nested under the candidate left root is refused by the shared guard and the candidate stays unpublished with no active root",
            NestedContainmentAsync);
        await CaseAsync("K-staging-authority",
            "Missing, foreign, unresolved and non-control preparation never publish; exact repaired staging publishes once",
            StagingAuthorityAsync);
        await CaseAsync("L-prepared-two-side-partition",
            "Both independently prepared candidate roots participate in atomic excluded-child publication; incomplete, foreign and unsafe evidence still refuses",
            PreparedTwoSidePartitionAsync);
        await CaseAsync("M-recovered-publication",
            "A pinned original database that owned this pair's private staging namespace is recovered into the current database and the candidate then publishes through the unchanged guarded path, with restart and replay",
            RecoveredPublicationAsync);

        await CaseAsync("N-required-alias-evidence",
            "An empty-evidence publication refuses while another retained pair reaches the same provider, account and namespace, then publishes from the retained observation alone; an unrelated pair keeps publishing",
            RequiredAliasEvidenceAsync);

        await CaseAsync("O-two-sided-registration-refusals",
            "The atomic two-sided registration refuses duplicate ids/sides and an absent account with no write, and a forced second-side guard failure rolls the first side's row back",
            TwoSidedRegistrationRefusalsAsync);

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "CloudPairAuthoringChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(IOPath.Combine(_root, "cloud-pair-authoring-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloud pair authoring: " + _assertions + " assertions; the real candidate " +
            "preparation and exact replay, the existing allocator and the one guarded atomic publication and " +
            "rebind over real SQLite, with every required refusal.");
    }

    private static async Task CaseAsync(string id, string label, Func<Task> body)
    {
        var before = _assertions;
        try
        {
            await body();
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "PASS", ["assertions"] = _assertions - before
            });
            Console.WriteLine("PASS " + id + ": " + label + " (" + (_assertions - before) + " assertions)");
        }
        catch (Exception exception)
        {
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "FAIL", ["assertions"] = _assertions - before,
                ["error"] = exception.GetType().Name + ": " + exception.Message
            });
            Console.WriteLine("FAIL " + id + ": " + exception.GetType().Name + ": " + exception.Message);
            throw;
        }
    }

    private static void Assert(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidDataException("BIND1 assertion failed: " + label);
    }

    private static async Task ExpectRefusalAsync<TException>(Func<Task> body, string label,
        string expectedDiagnostic) where TException : Exception
    {
        try
        {
            await body();
        }
        catch (Exception error)
        {
            // Bind both the exception type and the specific production gate. Another guard, a malformed
            // fixture, an I/O error or a long but unrelated diagnostic must never satisfy this case.
            Assert(error.GetType() == typeof(TException) &&
                   error.Message.Contains(expectedDiagnostic, StringComparison.Ordinal),
                label + " must reach " + typeof(TException).Name + ": " + expectedDiagnostic +
                "; observed " + error.GetType().Name + ": " + error.Message);
            return;
        }
        Assert(false, label + " must refuse and did not");
    }

    /// <summary>
    /// O9: the PERSISTED definition of one pair, loaded back through the production reader, so a case proves
    /// the stored Enabled and InitialReviewApproved state rather than the local variable it constructed.
    /// </summary>
    private static async Task AssertPersistedDisabledUnapprovedAsync(string databasePath, string pairId,
        string label)
    {
        var json = await ScalarAsync(databasePath, "SELECT pair_json FROM sync_pair WHERE pair_id='" + pairId +
            "';");
        Assert(!string.IsNullOrWhiteSpace(json), label + ": the pair definition is persisted");
        var persistedPair = JsonSerializer.Deserialize<SyncPair>(json!);
        Assert(persistedPair is not null && !persistedPair.Enabled &&
               persistedPair.InitialReviewApproved != true,
            label + ": the persisted definition is disabled and unapproved for first-sync review");
    }

    private static async Task<string?> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteSqlAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    // ----- the real bounded Windows and SQLite fixture -------------------------------------------------

    // ----- M: RC639-7 the real recovered publication through the corrected guards -----------------------
    private static async Task RecoveredPublicationAsync()
    {
        await using var fixture = await Fixture.CreateAsync("m-recovered", "m");
        var definition = fixture.Draft("cand-left", "cand-right", "BIND1 recovered");
        const string leftRoot = "root-cand-left-m";
        const string rightRoot = "root-cand-right-m";
        // The ORIGINAL database really created and owned this pair's private staging namespace.
        var priorPath = IOPath.Combine(fixture.CaseRoot, "prior.db");
        var prior = new SyncAuthorityDatabase(priorPath);
        await prior.InitializeAsync(InstallationId, ProfileId, SourceRevision, "{}");
        foreach (var role in new[] { "cand-left", "cand-right" })
            await prior.RegisterProviderAccountAsync(fixture.Account(role), InstallationId,
                "id:" + fixture.Account(role), fixture.AccountNamespace(role),
                fixture.AccountNamespace(role) + "-home", "BIND1 prior-" + role, 1,
                cancellationToken: default,
                provider: SyncEndpointProviders.Get(fixture.KindFor(role)).ProviderId!);
        await prior.PrepareCloudPairCandidateAsync(
            fixture.CandidateOf(definition, definition.PolicyRevision, leftRoot, rightRoot));
        _ = await new SyncAuthorityLocalControlNamespaceAllocator(prior).BindAsync(fixture.Anchor,
            InstallationId, definition.Id, leftRoot, definition.PolicyRevision, ProfileId,
            side: SyncAuthorityCloudSides.LogicalLeft);
        SqliteConnection.ClearAllPools();
        await using (var seal = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = priorPath }.ToString()))
        {
            await seal.OpenAsync();
            await using var checkpoint = seal.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        foreach (var sidecar in new[] { priorPath + "-wal", priorPath + "-shm" })
            if (File.Exists(sidecar)) File.Delete(sidecar);
        var priorSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(priorPath)));
        await using (var verify = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = priorPath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            await verify.OpenAsync();
            await using var check = verify.CreateCommand();
            check.CommandText = "PRAGMA quick_check;";
            Assert(Convert.ToString(await check.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == "ok",
                "the sealed original source is a clean single-file database");
        }
        SqliteConnection.ClearAllPools();
        // The CURRENT database prepares the same candidate and really collides with the owned object.
        await fixture.Db.PrepareCloudPairCandidateAsync(
            fixture.CandidateOf(definition, definition.PolicyRevision, leftRoot, rightRoot));
        await ExpectRefusalAsync<IOException>(() => fixture.AllocateStagingAsync(definition, leftRoot),
            "the ordinary allocator collides with the object this installation already owns",
            "cannot be adopted without exact retained creation authority");
        Assert(await ScalarAsync(fixture.DatabasePath,
            "SELECT count(*) FROM operation_attempt WHERE status='blocked' " +
            "AND effect_kind='local-control-directory-precondition';") == "1",
            "the current database really retains the non-creating collision");
        // Install ONLY the recovered ownership metadata, then publish through the unchanged guarded path.
        var outcome = await fixture.Db.RetainRecoveredControlAsync(
            new SyncAuthorityDatabase.RetainedControlSource(priorPath, priorSha, definition.Id, "local", leftRoot),
            fixture.Anchor);
        Assert(!outcome.Replayed && outcome.RootGeneration >= 1,
            "the recovered ownership metadata is retained against the exact held root generation");
        Assert(await fixture.Db.HasRecoveredControlOwnershipAsync(definition.Id, "local", leftRoot,
                outcome.RootGeneration),
            "the central resolver resolves the recovered ownership for the exact candidate root");
        var chains = new[]
        {
            fixture.Chain("cand-left", leftRoot, fixture.Folder("cand-left")),
            fixture.Chain("cand-right", rightRoot, fixture.Folder("cand-right"))
        };
        var published = await fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition, null, null,
            InstallationId, chains, chains);
        Assert(published.Pairs.Any(item => item.Id == definition.Id),
            "the recovered candidate publishes through the unchanged guarded publication path");
        Assert(await ScalarAsync(fixture.DatabasePath,
            "SELECT count(*) FROM root_binding WHERE pair_id='" + definition.Id + "' AND state='active';") == "2",
            "publication activates both candidate roots");
        Assert(await ScalarAsync(fixture.DatabasePath,
            "SELECT count(*) FROM control_namespace WHERE status='active';") == "1",
            "the retained control namespace stays exactly one active row");
        var replay = await fixture.Db.RetainRecoveredControlAsync(
            new SyncAuthorityDatabase.RetainedControlSource(priorPath, priorSha, definition.Id, "local", leftRoot),
            fixture.Anchor);
        Assert(replay.Replayed && replay.ControlNamespaceId == outcome.ControlNamespaceId,
            "the metadata replay after publication adds no duplicate binding");
    }

    // ----- N: D3/D2 per-side required alias evidence and the discriminating publish-time proof --------
    // ----- O: the two-sided registration's forced-failure matrix: nothing is partially durable -----------------

    private static async Task TwoSidedRegistrationRefusalsAsync()
    {
        await using var fixture = await Fixture.CreateAsync("o-two-sided", "o2");
        // the candidate DEFINITION must carry the exact endpoints the registration supplies, so the
        // unconditional binding is genuinely satisfied. Left is cand-left and right is third (both path-descriptive
        // Dropbox roles the fixture registers). The pair row and its immutable policy are registered with the exact
        // configuration the binding matcher derives from the definition.
        var definition = fixture.Draft("cand-left", "third", "BIND1 two-sided");
        await fixture.Db.RegisterPairAsync(definition.Id, definition.PolicyRevision,
            SyncAuthorityPairConfiguration.FromPair(definition));
        await fixture.Db.RetainPairDefinitionAsync(definition, select: false);
        const string leftRoot = "root-two-sided-left";
        const string rightRoot = "root-two-sided-right";
        var dropbox = SyncEndpointProviders.Get(SyncEndpointKind.Dropbox).ProviderId!;
        SyncAuthorityDatabase.SyncAuthorityCloudTwoSidedRoot Side(string root, string side, string role,
            string folder, string path) => new(root, side, dropbox, fixture.Account(role),
            fixture.EndpointNamespaceFor(role), folder, path);
        var left = Side(leftRoot, SyncAuthorityCloudSides.LogicalLeft, "cand-left", fixture.Folder("cand-left"),
            "/left");
        var right = Side(rightRoot, SyncAuthorityCloudSides.LogicalRight, "third", fixture.Folder("third"),
            "/right");
        var exact = new[]
        {
            fixture.Chain("cand-left", leftRoot, fixture.Folder("cand-left")),
            fixture.Chain("third", rightRoot, fixture.Folder("third"))
        };
        // Duplicate root ids and duplicate compatibility sides are pure-shape refusals: nothing is written.
        await ExpectRefusalAsync<InvalidDataException>(() => fixture.Db.RegisterTwoSidedCandidateCloudRootsAsync(
                definition.Id, definition.PolicyRevision, InstallationId, 1, left,
                right with { RootBindingId = leftRoot }, definition, exact),
            "duplicate two-sided root binding ids",
            "The two logical sides require two distinct retained authority root binding ids.");
        await ExpectRefusalAsync<InvalidDataException>(() => fixture.Db.RegisterTwoSidedCandidateCloudRootsAsync(
                definition.Id, definition.PolicyRevision, InstallationId, 1, left,
                right with { CompatibilitySide = SyncAuthorityCloudSides.LogicalLeft }, definition, exact),
            "duplicate two-sided compatibility sides",
            "The two logical sides require two distinct compatibility sides.");
        // A foreign or stale configuration revision is refused before any write.
        await ExpectRefusalAsync<InvalidDataException>(() => fixture.Db.RegisterTwoSidedCandidateCloudRootsAsync(
                definition.Id, definition.PolicyRevision + 7, InstallationId, 1, left, right, definition, exact),
            "a two-sided registration at a stale or foreign configuration revision",
            "not bound to this retained pair at this exact configuration revision");
        // A side whose namespace is not the retained namespace of its own provider account is refused.
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.RegisterTwoSidedCandidateCloudRootsAsync(
                definition.Id, definition.PolicyRevision, InstallationId, 1, left,
                right with { NamespaceId = "ns-not-this-account-o2" }, definition, exact),
            "a two-sided side whose namespace is not its provider account namespace",
            "endpoint namespace is not the retained namespace");
        // A side whose provider account is absent from this installation is refused before any write.
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.RegisterTwoSidedCandidateCloudRootsAsync(
                definition.Id, definition.PolicyRevision, InstallationId, 1, left,
                right with { ProviderAccountId = "account-absent-o2" }, definition, exact),
            "a two-sided side with no active provider account of this installation",
            "not bound to an active provider account");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE root_binding_id IN ('" +
                   leftRoot + "','" + rightRoot + "');") == "0",
            "every pre-upsert two-sided refusal wrote no root row");
        // DEFECT: an otherwise-disjoint registration whose own RIGHT side is NOT the candidate right
        // endpoint (an A/B definition reached with an incoming A/C side) must be refused by the unconditional
        // binding, with no root and no observation committed.
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.RegisterTwoSidedCandidateCloudRootsAsync(
                definition.Id, definition.PolicyRevision, InstallationId, 1, left,
                right with { FolderId = fixture.Folder("cand-left-other") }, definition, exact),
            "a two-sided registration whose own right side is not its candidate right endpoint",
            "not exactly bound to BOTH of this registration's own logical cloud root identities");
        // the exact logical-left/logical-right argument assignment. A swapped pair of tags with MATCHING
        // identities reaches this entry point's own shape rule, not an account or namespace mismatch.
        await ExpectRefusalAsync<InvalidDataException>(() => fixture.Db.RegisterTwoSidedCandidateCloudRootsAsync(
                definition.Id, definition.PolicyRevision, InstallationId, 1,
                left with { CompatibilitySide = SyncAuthorityCloudSides.LogicalRight },
                right with { CompatibilitySide = SyncAuthorityCloudSides.LogicalLeft }, definition, exact),
            "a two-sided registration with swapped logical-side argument tags",
            "logical-left identity as its left argument");
        // the NEW carried-candidate binding, exercised with VALID registration arguments and a VALID
        // retained row, so each refusal reaches the binding diagnostic rather than an earlier row query.
        await ExpectRefusalAsync<InvalidDataException>(() => fixture.Db.RegisterTwoSidedCandidateCloudRootsAsync(
                definition.Id, definition.PolicyRevision, InstallationId, 1, left, right,
                definition with { Id = "pair-foreign-carried-o2" }, exact),
            "a two-sided registration carrying a foreign candidate pair id",
            "not this registration's exact pair at this exact configuration revision");
        await ExpectRefusalAsync<InvalidDataException>(() => fixture.Db.RegisterTwoSidedCandidateCloudRootsAsync(
                definition.Id, definition.PolicyRevision, InstallationId, 1, left, right,
                definition with { PolicyRevision = definition.PolicyRevision + 5 }, exact),
            "a two-sided registration carrying a stale candidate policy revision",
            "not this registration's exact pair at this exact configuration revision");
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.RegisterTwoSidedCandidateCloudRootsAsync(
                definition.Id, definition.PolicyRevision, InstallationId, 1, left, right,
                definition with { RecoveryRetentionDays = 99 }, exact),
            "a two-sided registration carrying a changed immutable candidate policy",
            "not exactly bound to BOTH of this registration's own logical cloud root identities");
        // each own side needs exactly one complete provider hierarchy bound to its own exact identity.
        await ExpectRefusalAsync<InvalidDataException>(() => fixture.Db.RegisterTwoSidedCandidateCloudRootsAsync(
                definition.Id, definition.PolicyRevision, InstallationId, 1, left, right, definition,
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>()),
            "a two-sided registration with no own-side hierarchy evidence",
            "requires exactly one complete provider hierarchy bound to its exact identity");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE root_binding_id IN ('" +
                   leftRoot + "','" + rightRoot + "');") == "0",
            "the unbound and missing-evidence refusals committed no root row");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM history_event WHERE " +
                   "event_kind='opaque_hierarchy_retained';") == "0",
            "the unbound and missing-evidence refusals retained no observation");
        // exact SUCCESS: a genuinely bound registration with one complete chain per own side commits.
        await using (var ok = await Fixture.CreateAsync("o-two-sided-success", "ok"))
        {
            var okDefinition = ok.Draft("cand-left", "third", "BIND1 two-sided success");
            await ok.Db.RegisterPairAsync(okDefinition.Id, okDefinition.PolicyRevision,
                SyncAuthorityPairConfiguration.FromPair(okDefinition));
            await ok.Db.RetainPairDefinitionAsync(okDefinition, select: false);
            const string okLeft = "root-two-sided-left-ok";
            const string okRight = "root-two-sided-right-ok";
            await ok.Db.RegisterTwoSidedCandidateCloudRootsAsync(okDefinition.Id, okDefinition.PolicyRevision,
                InstallationId, 1,
                new SyncAuthorityDatabase.SyncAuthorityCloudTwoSidedRoot(okLeft,
                    SyncAuthorityCloudSides.LogicalLeft, dropbox, ok.Account("cand-left"),
                    ok.EndpointNamespaceFor("cand-left"), ok.Folder("cand-left"), "/left"),
                new SyncAuthorityDatabase.SyncAuthorityCloudTwoSidedRoot(okRight,
                    SyncAuthorityCloudSides.LogicalRight, dropbox, ok.Account("third"),
                    ok.EndpointNamespaceFor("third"), ok.Folder("third"), "/right"),
                okDefinition,
                new[]
                {
                    ok.Chain("cand-left", okLeft, ok.Folder("cand-left")),
                    ok.Chain("third", okRight, ok.Folder("third"))
                });
            Assert(await ScalarAsync(ok.DatabasePath, "SELECT count(*) FROM root_binding WHERE root_binding_id IN ('" +
                       okLeft + "','" + okRight + "') AND state='active';") == "2",
                "a genuinely bound two-sided registration commits both own roots");
        }
        // OV637-6: the three two-sided failure-matrix entries. Each runs in its OWN fresh fixture so a competitor
        // registered for one entry cannot pre-empt the next entry comparison, and each chain is built in the proven
        // provider shape (namespace-root corpus identity) with exactly one snapshot per bound identity for both own
        // sides and the colliding authority. Every candidate definition now carries the exact endpoints its
        // registration supplies (cand-left/third), so the unconditional binding is satisfied and the intended
        // comparator decision actually fires.
        SyncAuthorityDatabase.SyncAuthorityCloudTwoSidedRoot TwoSided(Fixture f, string root, string side,
            string role, string folder, string path) =>
            new(root, side, dropbox, f.Account(role), f.EndpointNamespaceFor(role), folder, path);
        // (1) TRUE Same against a retained FOREIGN pair root carrying the candidate LEFT identity: once both own
        // chains are supplied so no unverified-ancestry pre-emption occurs, the left guard refuses the Same
        // provider-global folder identity, and the atomic rollback removes both own rows with no observation.
        await using (var same = await Fixture.CreateAsync("o-two-sided-same", "os"))
        {
            var sameDefinition = same.Draft("cand-left", "third", "BIND1 two-sided same");
            await same.Db.RegisterPairAsync(sameDefinition.Id, sameDefinition.PolicyRevision,
                SyncAuthorityPairConfiguration.FromPair(sameDefinition));
            await same.Db.RetainPairDefinitionAsync(sameDefinition, select: false);
            const string sameLeft = "root-two-sided-left-os";
            const string sameRight = "root-two-sided-right-os";
            var foreignPair = same.Draft("cand-right", "old-left", "BIND1 two-sided foreign same");
            await same.Db.RegisterPairAsync(foreignPair.Id, foreignPair.PolicyRevision);
            await same.Db.RetainPairDefinitionAsync(foreignPair, select: false);
            var foreignRoot = "root-foreign-same-os";
            await same.Db.RegisterCloudEndpointRootBindingAsync(foreignRoot, foreignPair.Id,
                SyncAuthorityCloudSides.LogicalLeft, 1, InstallationId, dropbox, same.Account("cand-left"),
                same.EndpointNamespaceFor("cand-left"), same.Folder("cand-left"), "/foreign",
                cancellationToken: default,
                hierarchies: new[] { same.Chain("cand-left", foreignRoot, same.Folder("cand-left")) });
            await ExpectRefusalAsync<InvalidOperationException>(() =>
                same.Db.RegisterTwoSidedCandidateCloudRootsAsync(sameDefinition.Id, sameDefinition.PolicyRevision,
                    InstallationId, 1,
                    TwoSided(same, sameLeft, SyncAuthorityCloudSides.LogicalLeft, "cand-left",
                        same.Folder("cand-left"), "/left"),
                    TwoSided(same, sameRight, SyncAuthorityCloudSides.LogicalRight, "third",
                        same.Folder("third"), "/right"),
                    sameDefinition,
                    new[]
                    {
                        same.Chain("cand-left", sameLeft, same.Folder("cand-left")),
                        same.Chain("third", sameRight, same.Folder("third"))
                    }),
                "a two-sided registration colliding with a retained foreign pair root", "active pair root");
            Assert(await ScalarAsync(same.DatabasePath, "SELECT count(*) FROM root_binding WHERE root_binding_id IN ('" +
                       sameLeft + "','" + sameRight + "');") == "0",
                "the foreign-Same refusal rolled BOTH own rows back: nothing is partially durable");
            Assert(await ScalarAsync(same.DatabasePath, "SELECT count(*) FROM history_event WHERE " +
                       "event_kind='opaque_hierarchy_retained';") == "0",
                "no observation is retained when the two-sided root transaction does not commit");
        }
        // (2) RECOVERY-boundary overlap: with the recovery chain supplied the completed boundary at the RIGHT own
        // identity is refused; without it the same fixture proves the unverified-ancestry refusal instead.
        await using (var rec = await Fixture.CreateAsync("o-two-sided-recovery", "or"))
        {
            var recDefinition = rec.Draft("cand-left", "third", "BIND1 two-sided recovery");
            await rec.Db.RegisterPairAsync(recDefinition.Id, recDefinition.PolicyRevision,
                SyncAuthorityPairConfiguration.FromPair(recDefinition));
            await rec.Db.RetainPairDefinitionAsync(recDefinition, select: false);
            const string recRightRoot = "root-two-sided-right-or";
            var recoveryRoot = "recovery-two-sided-or";
            var recoveryChain = rec.Chain("third", recoveryRoot, rec.Folder("third"));
            await rec.Db.RegisterCloudRecoveryRootAsync(recoveryRoot, InstallationId, dropbox,
                rec.Account("third"), rec.EndpointNamespaceFor("third"), rec.Folder("third"),
                recoveryChain.CorpusRootId!, "/recovery-two-sided", 1, cancellationToken: default,
                hierarchies: new[] { recoveryChain });
            var recLeft = TwoSided(rec, "root-two-sided-left-or", SyncAuthorityCloudSides.LogicalLeft,
                "cand-left", rec.Folder("cand-left"), "/left");
            var recRight = TwoSided(rec, recRightRoot, SyncAuthorityCloudSides.LogicalRight, "third",
                rec.Folder("third"), "/right");
            var recLeftChain = rec.Chain("cand-left", "root-two-sided-left-or", rec.Folder("cand-left"));
            var recRightChain = rec.Chain("third", recRightRoot, rec.Folder("third"));
            await ExpectRefusalAsync<InvalidOperationException>(() =>
                rec.Db.RegisterTwoSidedCandidateCloudRootsAsync(recDefinition.Id, recDefinition.PolicyRevision,
                    InstallationId, 1, recLeft, recRight, recDefinition,
                    new[] { recLeftChain, recRightChain }),
                "a two-sided registration whose ancestry against a retained boundary is unverified",
                "cannot be established from the supplied native identities");
            await ExpectRefusalAsync<InvalidOperationException>(() =>
                rec.Db.RegisterTwoSidedCandidateCloudRootsAsync(recDefinition.Id, recDefinition.PolicyRevision,
                    InstallationId, 1, recLeft, recRight, recDefinition,
                    new[] { recLeftChain, recRightChain, recoveryChain }),
                "a two-sided registration colliding with an active recovery boundary", "active recovery boundary");
        }
        // (3) AMBIGUOUS ancestry: two supplied snapshots binding ONE own identity are refused rather than used.
        await using (var ambiguous = await Fixture.CreateAsync("o-two-sided-ambiguous", "oa"))
        {
            var ambDefinition = ambiguous.Draft("cand-left", "third", "BIND1 two-sided ambiguous");
            await ambiguous.Db.RegisterPairAsync(ambDefinition.Id, ambDefinition.PolicyRevision,
                SyncAuthorityPairConfiguration.FromPair(ambDefinition));
            await ambiguous.Db.RetainPairDefinitionAsync(ambDefinition, select: false);
            var ambLeftRoot = "root-two-sided-left-oa";
            var ambChain = ambiguous.Chain("cand-left", ambLeftRoot, ambiguous.Folder("cand-left"));
            await ExpectRefusalAsync<InvalidDataException>(() =>
                ambiguous.Db.RegisterTwoSidedCandidateCloudRootsAsync(ambDefinition.Id,
                    ambDefinition.PolicyRevision, InstallationId, 1,
                    TwoSided(ambiguous, ambLeftRoot, SyncAuthorityCloudSides.LogicalLeft, "cand-left",
                        ambiguous.Folder("cand-left"), "/left"),
                    TwoSided(ambiguous, "root-two-sided-right-oa", SyncAuthorityCloudSides.LogicalRight, "third",
                        ambiguous.Folder("third"), "/right"),
                    ambDefinition, new[] { ambChain, ambChain }),
                "a two-sided registration with two snapshots binding one own identity", "ambiguous");
        }
    }

    private static async Task RequiredAliasEvidenceAsync()
    {
        await using var fixture = await Fixture.CreateAsync("n-required-evidence", "n");
        var definition = fixture.Draft("cand-left", "cand-right", "BIND1 required evidence");
        var leftRoot = "root-cand-left-n";
        var rightRoot = "root-cand-right-n";
        await fixture.Db.PrepareCloudPairCandidateAsync(fixture.CandidateOf(definition, definition.PolicyRevision,
            leftRoot, rightRoot));
        await fixture.AllocateStagingAsync(definition, leftRoot);
        // Another retained, non-retired pair already reaches the candidate left root's provider, account and
        // NAMESPACE through a DIFFERENT, non-overlapping folder. That is the path-descriptive relation an
        // absent context used to be passed off as an independent root.
        var related = fixture.Draft("third", "old-right", "BIND1 related retained");
        await fixture.Db.RegisterPairAsync(related.Id, related.PolicyRevision);
        await fixture.Db.RetainPairDefinitionAsync(related, select: false);
        await fixture.Db.RegisterCloudEndpointRootBindingAsync("root-third-left-n", related.Id,
            SyncAuthorityCloudSides.LogicalLeft, 1, InstallationId,
            SyncEndpointProviders.Get(fixture.KindFor("cand-left")).ProviderId!, fixture.Account("cand-left"),
            fixture.EndpointNamespaceFor("cand-left"), "id:third-left-n", "/third-left/" + fixture.Name,
            cancellationToken: default);
        // D3: with NO bound context the empty-evidence publication is refused, and the reason names only the
        // related LEFT side; the unrelated right side is never implicated by that reason.
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.PublishCloudPairCandidateAsync(
                definition.Id, definition, null, null, InstallationId,
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>(),
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>()),
            "an empty-evidence publication whose left root has a related retained namespace peer",
            "The left cloud root shares its provider, account and namespace with another retained pair root");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   definition.Id + "' AND side IN ('local','dropbox') AND state='active';") == "0" &&
               await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   definition.Id + "' AND pair_json IS NOT NULL;") == "0",
            "the required-evidence refusal activated no candidate root and published nothing");
        // A fresh, entirely unrelated pair keeps publishing with absent evidence exactly as before, so D3
        // never turns a genuinely independent root into a refusal.
        await using (var unrelated = await Fixture.CreateAsync("n-unrelated-control", "nu"))
        {
            var control = unrelated.Draft("cand-left", "cand-right", "BIND1 unrelated control");
            var controlLeft = "root-cand-left-nu";
            await unrelated.Db.PrepareCloudPairCandidateAsync(unrelated.CandidateOf(control,
                control.PolicyRevision, controlLeft, "root-cand-right-nu"));
            await unrelated.AllocateStagingAsync(control, controlLeft);
            await unrelated.Db.PublishCloudPairCandidateAsync(control.Id, control, null, null, InstallationId,
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>(),
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>());
            Assert(await ScalarAsync(unrelated.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                       control.Id + "' AND side IN ('local','dropbox') AND state='active';") == "2",
                "a genuinely unrelated pair still publishes with absent caller evidence");
        }
        // D2 (b): the SAME publication now succeeds with EMPTY caller evidence only because the candidate
        // left root's RETAINED provider-verified observation supplies the bound context.
        var generation = await fixture.Db.LoadRootBindingGenerationAsync(definition.Id, leftRoot);
        Assert(generation >= 1, "the prepared candidate left root carries a real retained generation");
        var eventId = await fixture.Db.RetainOpaqueHierarchyObservationAsync(
            fixture.Chain("cand-left", leftRoot, fixture.Folder("cand-left")), definition.Id,
            SyncAuthorityCloudSides.LogicalLeft, generation);
        Assert(!string.IsNullOrWhiteSpace(eventId),
            "the verified candidate left hierarchy is retained as one bound observation");
        var published = await fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition, null, null,
            InstallationId, Array.Empty<SyncAuthorityCloudHierarchySnapshot>(),
            Array.Empty<SyncAuthorityCloudHierarchySnapshot>());
        Assert(published.Pairs.Any(item => item.Id == definition.Id),
            "the same empty-evidence publication publishes because the retained observation supplies the context");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   definition.Id + "' AND side IN ('local','dropbox') AND state='active';") == "2",
            "the discriminating publication activated both candidate roots");
        await AssertPersistedDisabledUnapprovedAsync(fixture.DatabasePath, definition.Id,
            "the discriminating publication");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Name = "";
        internal string CaseRoot = "";
        internal string AnchorPath = "";
        internal SyncAuthorityDatabase Db = null!;
        private PilotDirectoryLease? _anchor;

        internal PilotDirectoryLease Anchor => _anchor!;
        internal string DatabasePath => Db.DatabasePath;

        internal static async Task<Fixture> CreateAsync(string name, string slug)
        {
            var fixture = new Fixture
            {
                Name = name,
                CaseRoot = IOPath.Combine(_root, name),
                AnchorPath = IOPath.Combine(_root, name, "anchor")
            };
            Directory.CreateDirectory(fixture.CaseRoot);
            Directory.CreateDirectory(fixture.AnchorPath);
            fixture._anchor = PilotDirectoryLease.Open(fixture.AnchorPath);
            fixture.Db = new SyncAuthorityDatabase(IOPath.Combine(fixture.CaseRoot, "syncshark.db"));
            await fixture.Db.InitializeAsync(InstallationId, ProfileId, SourceRevision, "{}");
            await fixture.RegisterAccountAsync("cand-left", slug);
            await fixture.RegisterAccountAsync("cand-right", slug);
            await fixture.RegisterAccountAsync("old-left", slug);
            await fixture.RegisterAccountAsync("old-right", slug);
            await fixture.RegisterAccountAsync("third", slug);
            return fixture;
        }

        internal string Account(string role) => "account-" + role + "-" + Name;

        internal string AccountNamespace(string role) => "ns-" + role + "-" + Name;

        /// <summary>The exact endpoint namespace this role's retained account presents to the shared guard.</summary>
        internal string EndpointNamespaceFor(string role) =>
            KindFor(role) == SyncEndpointKind.GoogleDrive ? "id:" + Account(role) : AccountNamespace(role);

        /// <summary>
        /// O7: the provider-issued COMPLETE corpus chain the shared guard verifies for one root, as the same
        /// typed snapshot the accepted cloud fixtures construct. A missing or unbound chain never becomes a
        /// corpus, so the Recovery setup must supply this rather than empty snapshots.
        /// </summary>
        internal SyncAuthorityCloudHierarchySnapshot Chain(string role, string rootBindingId, string folderId,
            params string[] ancestors)
        {
            var kind = KindFor(role);
            var provider = SyncEndpointProviders.Get(kind).ProviderId!;
            var namespaceId = EndpointNamespaceFor(role);
            var chain = new List<string>
            {
                kind == SyncEndpointKind.Dropbox
                    ? PilotPath.DropboxNamespaceRootIdentity(namespaceId)
                    : "corpus-root-" + namespaceId
            };
            chain.AddRange(ancestors);
            chain.Add(folderId);
            return new SyncAuthorityCloudHierarchySnapshot(provider, Account(role), namespaceId,
                rootBindingId, folderId, chain)
            {
                CompleteToCorpusRoot = true,
                CorpusRootId = chain[0],
                ChainNodeKeys = chain.Select(id => new SyncAuthorityCloudHierarchyNodeKeys(id,
                    new[] { SyncCloudObjectKey.Native(provider, id) })).ToArray(),
                ObjectKeyCoverage = kind == SyncEndpointKind.Dropbox
                    ? SyncCloudObjectKeyCoverage.MountAliasesComplete
                    : SyncCloudObjectKeyCoverage.NativeOnly
            };
        }

        /// <summary>
        /// The same complete corpus chain as Chain(...) PLUS the index-aligned provider-observed folder names
        /// that an OPAQUE (Google Drive) nested-exclusion proof requires. names must align with the chain that
        /// Chain(...) builds for the same arguments: [corpus root, ...ancestors, folderId]. The names are used
        /// only to derive a relative exclusion subtree AFTER the typed comparison proves a Nested relation;
        /// a path-descriptive (Dropbox) side never needs them.
        /// </summary>
        internal SyncAuthorityCloudHierarchySnapshot ChainNamed(string role, string rootBindingId, string folderId,
            IReadOnlyList<string> names, params string[] ancestors)
        {
            var snapshot = Chain(role, rootBindingId, folderId, ancestors);
            if (names.Count != snapshot.ChainFolderIds.Count)
                throw new InvalidDataException(
                    "An observed-name chain must supply exactly one safe name per chain node.");
            return snapshot with { ChainFolderNames = names };
        }

        internal async Task RegisterAccountAsync(string role, string slug)
        {
            // The registered provider is the same KindFor this fixture reports, and the retained namespaces
            // follow the real semantics: a Dropbox endpoint presents its retained root namespace while a Google
            // Drive endpoint presents its native permission (account) id, which is exactly what
            // SyncAuthorityProviderAccountIdentity.RetainedEndpointNamespace requires.
            var provider = SyncEndpointProviders.Get(KindFor(role)).ProviderId!;
            await Db.RegisterProviderAccountAsync(Account(role), InstallationId, "id:" + Account(role),
                AccountNamespace(role), AccountNamespace(role) + "-home",
                "BIND1 " + role, 1, cancellationToken: default, provider: provider);
        }

        internal SyncEndpointKind KindFor(string role) =>
            string.Equals(role, "cand-right", StringComparison.Ordinal) ||
            string.Equals(role, "old-right", StringComparison.Ordinal)
                ? SyncEndpointKind.GoogleDrive
                : SyncEndpointKind.Dropbox;

        internal string Folder(string role) =>
            (KindFor(role) == SyncEndpointKind.Dropbox ? "id:" : "") + "folder-" + role + "-" + Name;

        internal string DisplayPath(string role) => "/" + role + "/" + Name;

        /// <summary>The real shared cloud draft definition of one ordered cloud/cloud pair.</summary>
        internal SyncPair Draft(string leftRole, string rightRole, string label)
        {
            var draft = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
                SyncEndpoint.Cloud(KindFor(leftRole), "conn-" + leftRole + "-" + Name,
                    EndpointNamespaceFor(leftRole), Folder(leftRole), DisplayPath(leftRole)),
                SyncEndpoint.Cloud(KindFor(rightRole), "conn-" + rightRole + "-" + Name,
                    EndpointNamespaceFor(rightRole), Folder(rightRole), DisplayPath(rightRole)),
                SyncMode.TwoWay), label);
            return SyncPairStore.NormalizePair(draft with { Enabled = false, InitialReviewApproved = false });
        }

        /// <summary>The candidate of this fixture's ordinary cand-left/cand-right pair.</summary>
        internal CandidateRequest CandidateOf(SyncPair definition, long revision,
            string? leftRootBindingId = null, string? rightRootBindingId = null)
            => CandidateOf(definition, revision, "cand-left", "cand-right", leftRootBindingId,
                rightRootBindingId);

        /// <summary>
        /// O5: the candidate bound to the ACTUAL two roles, so a case that keeps another pair's endpoint
        /// (case G keeps the exact old-left account, namespace, folder and provider) binds those exact facts
        /// rather than this fixture's default roles.
        /// </summary>
        internal CandidateRequest CandidateOf(SyncPair definition, long revision, string leftRole,
            string rightRole, string? leftRootBindingId = null, string? rightRootBindingId = null)
        {
            var projection = SyncCloudPairConfiguration.RequireProjection(definition);
            return new CandidateRequest(definition, revision, InstallationId,
                leftRootBindingId ?? "root-cand-left-" + Name,
                SyncEndpoints.ProviderLiteral(projection.Left.Kind) ?? "",
                Account(leftRole), EndpointNamespaceFor(leftRole), projection.Left.FolderId ?? "",
                SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(projection.Left.FolderPath),
                rightRootBindingId ?? "root-cand-right-" + Name,
                SyncEndpoints.ProviderLiteral(projection.Right.Kind) ?? "",
                Account(rightRole), EndpointNamespaceFor(rightRole), projection.Right.FolderId ?? "",
                SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(projection.Right.FolderPath));
        }

        /// <summary>The one existing journalled private-stage allocation of this candidate, side logical-left.</summary>
        internal async Task<SyncLocalControlNamespaceBinding> AllocateStagingAsync(SyncPair definition,
            string leftRootBindingId)
        {
            var allocator = new SyncAuthorityLocalControlNamespaceAllocator(Db);
            return await allocator.BindAsync(Anchor, InstallationId, definition.Id, leftRootBindingId,
                definition.PolicyRevision, ProfileId, side: SyncAuthorityCloudSides.LogicalLeft);
        }

        internal async Task AddWorkAsync(SyncPair definition, string rootBindingId, string id,
            SyncAuthorityOperationKind kind, SyncAuthorityCommandAdmission admission)
        {
            await Db.RegisterObjectAsync("object-" + id, "directory");
            await Db.CreateOperationAsync(new SyncAuthorityOperationPlan(id, "idempotency-" + id, kind,
                definition.Id, "object-" + id, definition.PolicyRevision, ProfileId, "{}", 100,
                [new SyncAuthorityMember("member-" + id, definition.Id, "object-" + id, "lock",
                    rootBindingId, null, null, null, null, "{}")], [], []), admission: admission);
        }

        public async ValueTask DisposeAsync()
        {
            _anchor?.Dispose();
            SqliteConnection.ClearAllPools();
            await Task.CompletedTask;
        }
    }

    // ----- A: the minimal prepare, allocator and publish flow with exact replay ------------------------

    private static async Task PrepareAndPublicationAsync()
    {
        await using var fixture = await Fixture.CreateAsync("a-minimal", "a");
        var definition = fixture.Draft("cand-left", "cand-right", "BIND1 minimal");
        var revision = definition.PolicyRevision;
        var leftRoot = "root-cand-left-a";
        var rightRoot = "root-cand-right-a";
        var schemaBefore = await SchemaSnapshotAsync(fixture.DatabasePath);
        var prepared = await fixture.Db.PrepareCloudPairCandidateAsync(
            fixture.CandidateOf(definition, revision, leftRoot, rightRoot));
        Assert(!prepared.Replayed && prepared.PairId == definition.Id, "the first preparation is a real retain");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   definition.Id + "' AND pair_json IS NULL AND selected=0;") == "1",
            "the prepared candidate keeps a null pair_json and no selection");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   definition.Id + "' AND pair_json IS NOT NULL;") == "0",
            "the prepared candidate is invisible in the retained pair definitions");
        foreach (var root in new[] { leftRoot, rightRoot })
            Assert(await ScalarAsync(fixture.DatabasePath, "SELECT state FROM root_binding WHERE root_binding_id='" +
                       root + "';") == "blocked",
                "the prepared candidate root " + root + " is retained blocked, never active");
        Assert(await ScalarAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM root_binding WHERE pair_id='" + definition.Id + "' AND side IN ('local','dropbox') AND state='active';") == "0",
            "the prepared candidate owns no active root at all");

        // Exact replay: the identical input reproduces the identical retained candidate with no new write.
        var replayed = await fixture.Db.PrepareCloudPairCandidateAsync(
            fixture.CandidateOf(definition, revision, leftRoot, rightRoot));
        Assert(replayed.Replayed && replayed.LeftIdentityJson == prepared.LeftIdentityJson &&
               replayed.RightIdentityJson == prepared.RightIdentityJson,
            "the exact replay reports the identical retained candidate identities");
        Assert(await ScalarAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM root_binding WHERE pair_id='" + definition.Id + "';") == "2",
            "the exact replay created no additional root binding");

        // The single existing private-stage allocation, owned and locked by the candidate logical-left root.
        var binding = await fixture.AllocateStagingAsync(definition, leftRoot);
        Assert(!string.IsNullOrWhiteSpace(binding.RootPath) &&
               await ScalarAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM pair_control_namespace WHERE pair_id='" + definition.Id +
                   "' AND side='local' AND status='active';") == "1",
            "the existing allocator retained one logical-left pair-private staging binding");
        Assert(await ScalarAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM namespace_allocation a JOIN operation o ON o.operation_id=a.operation_id " +
                   "WHERE o.pair_id='" + definition.Id + "' AND a.status='verified' AND a.bound_identity IS NOT NULL;") == "1",
            "the staging allocation is verified with a retained physical identity");

        var published = await fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition, null, null,
            InstallationId, Array.Empty<SyncAuthorityCloudHierarchySnapshot>(),
            Array.Empty<SyncAuthorityCloudHierarchySnapshot>());
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   definition.Id + "' AND pair_json IS NOT NULL AND selected=1;") == "1",
            "publication retained the pair definition and selected it in one transaction");
        Assert(await ScalarAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM root_binding WHERE pair_id='" + definition.Id + "' AND side IN ('local','dropbox') AND state='active';") == "2",
            "publication activated both exact candidate roots");
        Assert(!definition.Enabled && definition.InitialReviewApproved != true,
            "the published cloud pair stays disabled and unapproved for first-sync review");
        await AssertPersistedDisabledUnapprovedAsync(fixture.DatabasePath, definition.Id,
            "the published cloud pair");
        Assert(await SchemaSnapshotAsync(fixture.DatabasePath) == schemaBefore,
            "the whole lifecycle changed no schema table");
        Assert(await ScalarAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM sync_pair WHERE pair_id='" + definition.Id +
                   "' AND pair_json IS NULL;") == "0",
            "no unpublished candidate row survives publication");
    }

    // ----- B and C: candidate-state and preimage refusals ---------------------------------------------

    private static async Task PublishedAndRetiredAsync()
    {
        await using var fixture = await Fixture.CreateAsync("b-published", "b");
        var definition = fixture.Draft("cand-left", "cand-right", "BIND1 published");
        var leftRoot = "root-cand-left-b";
        var rightRoot = "root-cand-right-b";
        await fixture.Db.PrepareCloudPairCandidateAsync(fixture.CandidateOf(definition, definition.PolicyRevision,
            leftRoot, rightRoot));
        await fixture.AllocateStagingAsync(definition, leftRoot);
        await fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition, null, null, InstallationId,
            Array.Empty<SyncAuthorityCloudHierarchySnapshot>(), Array.Empty<SyncAuthorityCloudHierarchySnapshot>());
        var rootsBefore = await ScalarAsync(fixture.DatabasePath,
            "SELECT count(*) FROM root_binding WHERE pair_id='" + definition.Id + "';");
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.PrepareCloudPairCandidateAsync(
                fixture.CandidateOf(definition, definition.PolicyRevision, leftRoot, rightRoot)),
            "preparing an already published candidate",
            "The candidate pair is retired, published, selected or retained at another revision;");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   definition.Id + "';") == rootsBefore &&
               await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   definition.Id + "' AND pair_json IS NOT NULL;") == "1",
            "the published-candidate refusal changed no root and no definition");

        await using var retired = await Fixture.CreateAsync("b-retired", "br");
        var retiredDefinition = retired.Draft("cand-left", "cand-right", "BIND1 retired");
        await retired.Db.RegisterPairAsync(retiredDefinition.Id, retiredDefinition.PolicyRevision);
        await ExecuteSqlAsync(retired.DatabasePath, "UPDATE sync_pair SET state='retired' WHERE pair_id='" +
            retiredDefinition.Id + "';");
        await ExpectRefusalAsync<InvalidOperationException>(() => retired.Db.PrepareCloudPairCandidateAsync(
                retired.CandidateOf(retiredDefinition, retiredDefinition.PolicyRevision, "root-cand-left-br",
                    "root-cand-right-br")),
            "preparing a retired candidate that must never be resurrected",
            "The candidate pair is retired, published, selected or retained at another revision;");
        Assert(await ScalarAsync(retired.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   retiredDefinition.Id + "' AND state='retired';") == "1" &&
               await ScalarAsync(retired.DatabasePath,
                   "SELECT count(*) FROM root_binding WHERE pair_id='" + retiredDefinition.Id + "';") == "0",
            "the retired candidate stayed retired with no retained root");
    }

    private static async Task ExactPreimageMismatchAsync()
    {
        await using var fixture = await Fixture.CreateAsync("c-exact-preimage", "c");
        var definition = fixture.Draft("cand-left", "cand-right", "BIND1 preimage");
        var leftRoot = "root-cand-left-c";
        var rightRoot = "root-cand-right-c";
        await fixture.Db.PrepareCloudPairCandidateAsync(fixture.CandidateOf(definition, definition.PolicyRevision,
            leftRoot, rightRoot));
        // A drifted retained preimage: the display path no longer matches what preparation wrote.
        await ExecuteSqlAsync(fixture.DatabasePath, "UPDATE root_binding SET display_path='/drifted' " +
            "WHERE root_binding_id='" + leftRoot + "';");
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.PrepareCloudPairCandidateAsync(
                fixture.CandidateOf(definition, definition.PolicyRevision, leftRoot, rightRoot)),
            "replaying a candidate whose blocked preimage drifted",
            "The retained candidate root does not carry the exact blocked identity, side, generation, path and state that preparation wrote.");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT display_path FROM root_binding WHERE " +
                   "root_binding_id='" + leftRoot + "';") == "/drifted",
            "the drifted preimage was refused without any write of its own");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   definition.Id + "';") == "2",
            "the mismatch refusal added no root binding");
    }

    // ----- D: candidate accounts changed between prepare and publish ---------------------------------

    private static async Task AccountChangesAsync()
    {
        await using var fixture = await Fixture.CreateAsync("d-accounts", "d");
        var definition = fixture.Draft("cand-left", "cand-right", "BIND1 accounts");
        var leftRoot = "root-cand-left-d";
        var rightRoot = "root-cand-right-d";
        await fixture.Db.PrepareCloudPairCandidateAsync(fixture.CandidateOf(definition, definition.PolicyRevision,
            leftRoot, rightRoot));
        await fixture.AllocateStagingAsync(definition, leftRoot);
        // O6 coverage 1: the RETIRED-account case stays its own coverage. A retired candidate account refuses
        // and leaves the candidate unpublished with its blocked root intact and nothing activated.
        await ExecuteSqlAsync(fixture.DatabasePath, "UPDATE provider_account SET status='retired' " +
            "WHERE provider_account_id='" + fixture.Account("cand-right") + "';");
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition, null,
                null, InstallationId, Array.Empty<SyncAuthorityCloudHierarchySnapshot>(),
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>()),
            "publishing while the candidate right account is retired",
            "The candidate endpoint is not bound to an active provider account of this installation.");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   definition.Id + "' AND pair_json IS NOT NULL;") == "0" &&
               await ScalarAsync(fixture.DatabasePath, "SELECT state FROM root_binding WHERE root_binding_id='" +
                   rightRoot + "';") == "blocked",
            "the retired-account refusal left the candidate unpublished with its blocked root intact");
        await ExecuteSqlAsync(fixture.DatabasePath, "UPDATE provider_account SET status='active' " +
            "WHERE provider_account_id='" + fixture.Account("cand-right") + "';");
        // O6 coverage 2: the ACTIVE SUBSTITUTION case. Both candidate accounts stay active and only the exact
        // right-side association changes, to another VALID ACTIVE Google Drive account whose namespace is
        // MISMATCHED - same provider, different namespace authority - so the refusal under test is the exact
        // account and namespace-authority association, not a retirement and not a provider disagreement.
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT provider FROM provider_account WHERE " +
                   "provider_account_id='" + fixture.Account("old-right") + "' AND status='active';") ==
               SyncEndpointProviders.Get(SyncEndpointKind.GoogleDrive).ProviderId,
            "the substitute is a real active account of the same provider");
        var exactPreimage = await ScalarAsync(fixture.DatabasePath,
            "SELECT identity_json FROM root_binding WHERE root_binding_id='" + rightRoot + "';");
        var activeAccounts = await ScalarAsync(fixture.DatabasePath,
            "SELECT count(*) FROM provider_account WHERE status='active';");
        await ExecuteSqlAsync(fixture.DatabasePath, "UPDATE root_binding SET identity_json=" +
            "json_set(identity_json,'$.providerAccountId','" + fixture.Account("old-right") + "') " +
            "WHERE root_binding_id='" + rightRoot + "';");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT json_extract(identity_json,'$.namespaceId') " +
                   "FROM root_binding WHERE root_binding_id='" + rightRoot + "';") ==
               fixture.EndpointNamespaceFor("cand-right"),
            "the substituted identity kept the candidate namespace while naming another provider account");
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition, null,
                null, InstallationId, Array.Empty<SyncAuthorityCloudHierarchySnapshot>(),
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>()),
            "publishing after the right account association was substituted with a mismatched namespace",
            "The candidate endpoint namespace is not the retained namespace of its provider account.");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   definition.Id + "' AND pair_json IS NOT NULL;") == "0" &&
               await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   definition.Id + "' AND side IN ('local','dropbox') AND state='active';") == "0" &&
               await ScalarAsync(fixture.DatabasePath, "SELECT state FROM root_binding WHERE root_binding_id='" +
                   rightRoot + "';") == "blocked",
            "the substituted-association refusal left the candidate unpublished with its blocked root intact");
        Assert(await ScalarAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM provider_account WHERE status='active';") == activeAccounts,
            "every account stayed active, so the refusal is the identity association and not a retirement");
        // The exact prepared preimage restored, the identical candidate then succeeds.
        await ExecuteSqlAsync(fixture.DatabasePath, "UPDATE root_binding SET identity_json='" +
            exactPreimage!.Replace("'", "''") + "' WHERE root_binding_id='" + rightRoot + "';");
        await fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition, null, null, InstallationId,
            Array.Empty<SyncAuthorityCloudHierarchySnapshot>(), Array.Empty<SyncAuthorityCloudHierarchySnapshot>());
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   definition.Id + "' AND pair_json IS NOT NULL;") == "1",
            "the exact preimage restore let the identical candidate publish");
        // O9: assert the PERSISTED definition through the production reader, not the local variable.
        var persistedJson = await ScalarAsync(fixture.DatabasePath,
            "SELECT pair_json FROM sync_pair WHERE pair_id='" + definition.Id + "';");
        var persisted = JsonSerializer.Deserialize<SyncPair>(persistedJson!);
        Assert(persisted is not null && !persisted.Enabled && persisted.InitialReviewApproved != true,
            "the persisted pair definition is disabled and unapproved for first-sync review");
    }

    // ----- E: foreign, swapped and overlapping roots ---------------------------------------------------

    private static async Task ForeignAndOrphanRootsAsync()
    {
        await using var fixture = await Fixture.CreateAsync("e-foreign", "e");
        var definition = fixture.Draft("cand-left", "cand-right", "BIND1 foreign");
        // A swapped candidate: the left side carries the right endpoint's own identity.
        var swapped = fixture.CandidateOf(definition, definition.PolicyRevision, "root-cand-left-e",
            "root-cand-right-e") with
        {
            LeftNamespaceId = fixture.AccountNamespace("cand-right"),
            LeftFolderId = fixture.Folder("cand-right")
        };
        await ExpectRefusalAsync<InvalidDataException>(() => fixture.Db.PrepareCloudPairCandidateAsync(swapped),
            "a candidate whose left fields are another endpoint's identity",
            "The draft definition's left endpoint does not agree with the candidate namespace, folder and display path.");
        Assert(await ScalarAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM root_binding WHERE pair_id='" + definition.Id + "';") == "0",
            "the swapped candidate retained no root at all");

        var leftRoot = "root-cand-left-e";
        var rightRoot = "root-cand-right-e";
        await fixture.Db.PrepareCloudPairCandidateAsync(fixture.CandidateOf(definition, definition.PolicyRevision,
            leftRoot, rightRoot));
        await fixture.AllocateStagingAsync(definition, leftRoot);
        // A third live pair already holds the candidate's own left identity as an active root.
        var thirdDefinition = fixture.Draft("third", "cand-right", "BIND1 third");
        await fixture.Db.RegisterPairAsync(thirdDefinition.Id, thirdDefinition.PolicyRevision);
        await fixture.Db.RetainPairDefinitionAsync(thirdDefinition, select: false);
        await fixture.Db.RegisterCloudEndpointRootBindingAsync("root-third-left", thirdDefinition.Id,
            SyncAuthorityCloudSides.LogicalLeft, 1, InstallationId,
            SyncEndpointProviders.Get(fixture.KindFor("cand-left")).ProviderId!, fixture.Account("cand-left"),
            fixture.EndpointNamespaceFor("cand-left"), fixture.Folder("cand-left"), "/third-left",
            cancellationToken: default);
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition, null,
                null, InstallationId, Array.Empty<SyncAuthorityCloudHierarchySnapshot>(),
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>()),
            "publishing a candidate whose left identity is already an active third-pair root",
            "The left cloud root names the exact same provider folder identity as another active pair root;");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   definition.Id + "' AND side IN ('local','dropbox') AND state='active';") == "0",
            "the third-pair overlap refusal activated no candidate root");
        await ExecuteSqlAsync(fixture.DatabasePath, "UPDATE root_binding SET state='out_of_scope' " +
            "WHERE root_binding_id='root-third-left';");
        // A shared Recovery authority holding the candidate's own right identity is refused the same way.
        await fixture.Db.RegisterCloudRecoveryRootAsync("recovery-e-right-overlap", InstallationId,
            SyncEndpointProviders.Get(fixture.KindFor("cand-right")).ProviderId!, fixture.Account("cand-right"),
            fixture.EndpointNamespaceFor("cand-right"), fixture.Folder("cand-right"), "corpus-e-right",
            SyncAuthorityCloudRootPersistence.NormalizeDisplayPath("/Recovery/e"), 1,
            cancellationToken: default,
            hierarchies: new[]
            {
                fixture.Chain("cand-right", "recovery-e-right-overlap", fixture.Folder("cand-right"))
            });
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition, null,
                null, InstallationId, Array.Empty<SyncAuthorityCloudHierarchySnapshot>(),
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>()),
            "publishing a candidate whose right identity is a shared Recovery authority",
            "The right cloud root names the exact same provider folder identity as another active recovery boundary;");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   definition.Id + "' AND side IN ('local','dropbox') AND state='active';") == "0" &&
               await ScalarAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id='recovery-e-right-overlap';") == "1",
            "the Recovery overlap refusal activated no candidate root and left Recovery unchanged");
    }

    // ----- F: enabled or approved definitions ----------------------------------------------------------

    private static async Task EnabledOrApprovedAsync()
    {
        await using var fixture = await Fixture.CreateAsync("f-enabled", "f");
        var enabled = fixture.Draft("cand-left", "cand-right", "BIND1 enabled") with { Enabled = true };
        await ExpectRefusalAsync<InvalidDataException>(() => fixture.Db.PrepareCloudPairCandidateAsync(
                fixture.CandidateOf(enabled, enabled.PolicyRevision, "root-cand-left-f", "root-cand-right-f")),
            "preparing an enabled candidate definition",
            "The candidate pair definition is enabled;");
        var approved = fixture.Draft("cand-left", "cand-right", "BIND1 approved") with
        {
            InitialReviewApproved = true
        };
        await ExpectRefusalAsync<InvalidDataException>(() => fixture.Db.PrepareCloudPairCandidateAsync(
                fixture.CandidateOf(approved, approved.PolicyRevision, "root-cand-left-f2", "root-cand-right-f2")),
            "preparing an already approved candidate definition",
            "The candidate pair definition is already approved for first-sync review;");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair;") == "0" &&
               await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding;") == "0",
            "an enabled or approved definition left the database completely unchanged");
    }

    // ----- G: the endpoint-changing rebind retaining one old endpoint ----------------------------------

    private static async Task RebindAsync()
    {
        await using var fixture = await Fixture.CreateAsync("g-rebind", "g");
        var oldDefinition = fixture.Draft("old-left", "old-right", "BIND1 old");
        await fixture.Db.RegisterPairAsync(oldDefinition.Id, oldDefinition.PolicyRevision);
        await fixture.Db.RetainPairDefinitionAsync(oldDefinition, select: true);
        await fixture.Db.RegisterCloudEndpointRootBindingAsync("root-old-left", oldDefinition.Id,
            SyncAuthorityCloudSides.LogicalLeft, 1, InstallationId,
            SyncEndpointProviders.Get(fixture.KindFor("old-left")).ProviderId!, fixture.Account("old-left"),
            fixture.AccountNamespace("old-left"), fixture.Folder("old-left"), "/old-left",
            cancellationToken: default);
        await fixture.Db.RegisterCloudEndpointRootBindingAsync("root-old-right", oldDefinition.Id,
            SyncAuthorityCloudSides.LogicalRight, 1, InstallationId,
            SyncEndpointProviders.Get(fixture.KindFor("old-right")).ProviderId!, fixture.Account("old-right"),
            fixture.EndpointNamespaceFor("old-right"), fixture.Folder("old-right"), "/old-right",
            cancellationToken: default);
        // The candidate keeps the old left endpoint identity and changes the right one.
        var rebind = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
                SyncEndpoint.Cloud(fixture.KindFor("old-left"), "conn-old-left-" + fixture.Name,
                fixture.EndpointNamespaceFor("old-left"), fixture.Folder("old-left"),
                fixture.DisplayPath("old-left")),
            SyncEndpoint.Cloud(fixture.KindFor("cand-right"), "conn-cand-right-" + fixture.Name,
                fixture.EndpointNamespaceFor("cand-right"), fixture.Folder("cand-right"),
                fixture.DisplayPath("cand-right")),
            SyncMode.TwoWay), "BIND1 rebind");
        var definition = SyncPairStore.NormalizePair(rebind with
        {
            Enabled = false,
            InitialReviewApproved = false
        });
        var leftRoot = "root-cand-left-g";
        var rightRoot = "root-cand-right-g";
        await fixture.Db.PrepareCloudPairCandidateAsync(fixture.CandidateOf(definition, definition.PolicyRevision,
            "old-left", "cand-right", leftRoot, rightRoot));
        await fixture.AllocateStagingAsync(definition, leftRoot);
        // O8: the stale expected OLD revision is tested BEFORE any valid publication, so the revision gate is
        // proven on its own instead of being masked by the unpublished gate, and the exact state must be
        // unchanged: candidate unpublished, old pair still retained, candidate root still blocked.
        var oldJson = await ScalarAsync(fixture.DatabasePath,
            "SELECT pair_json FROM sync_pair WHERE pair_id='" + oldDefinition.Id + "';");
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition,
                oldDefinition.Id, oldDefinition.PolicyRevision - 1, InstallationId,
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>(), Array.Empty<SyncAuthorityCloudHierarchySnapshot>()),
            "a rebind whose expected old revision is stale, tested before any valid publication",
            "The retained pair selected for replacement changed after it was reviewed;");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   definition.Id + "' AND pair_json IS NOT NULL;") == "0" &&
               await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   oldDefinition.Id + "' AND state='retired';") == "0" &&
               await ScalarAsync(fixture.DatabasePath, "SELECT state FROM root_binding WHERE root_binding_id='" +
                   rightRoot + "';") == "blocked",
            "the stale-revision refusal left the candidate unpublished, the old pair retained and the root blocked");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT pair_json FROM sync_pair WHERE pair_id='" +
                   oldDefinition.Id + "';") == oldJson,
            "stale revision preserves the complete old definition and settings");

        var third = fixture.Draft("third", "cand-right", "BIND1 rebind rollback");
        await fixture.Db.RegisterPairAsync(third.Id, third.PolicyRevision);
        await fixture.Db.RetainPairDefinitionAsync(third, select: false);
        await fixture.Db.RegisterCloudEndpointRootBindingAsync("root-rebind-third-g", third.Id,
            SyncAuthorityCloudSides.LogicalRight, 1, InstallationId,
            SyncEndpointProviders.Get(fixture.KindFor("cand-right")).ProviderId!, fixture.Account("cand-right"),
            fixture.EndpointNamespaceFor("cand-right"), fixture.Folder("cand-right"), "/third-right",
            cancellationToken: default, hierarchies: new[]
            {
                fixture.Chain("cand-right", "root-rebind-third-g", fixture.Folder("cand-right")),
                fixture.Chain("old-right", "root-old-right", fixture.Folder("old-right"))
            });
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.PublishCloudPairCandidateAsync(
                definition.Id, definition, oldDefinition.Id, oldDefinition.PolicyRevision, InstallationId,
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>(), Array.Empty<SyncAuthorityCloudHierarchySnapshot>()),
            "rebind right activation fails after temporarily retiring the old roots",
            "The right cloud root names the exact same provider folder identity as another active pair root;");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT pair_json FROM sync_pair WHERE pair_id='" +
                   oldDefinition.Id + "' AND state<>'retired' AND selected=1;") == oldJson &&
               await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   oldDefinition.Id + "' AND side IN ('local','dropbox') AND state='active';") == "2" &&
               await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   definition.Id + "' AND state='blocked';") == "2",
            "failed right activation restores old selection/settings/both roots and both candidate blocked roots");
        await ExecuteSqlAsync(fixture.DatabasePath,
            "UPDATE root_binding SET state='out_of_scope' WHERE root_binding_id='root-rebind-third-g';");
        await ExpectRefusalAsync<InvalidDataException>(() => fixture.Db.PublishCloudPairCandidateAsync(
                definition.Id, definition, oldDefinition.Id, oldDefinition.PolicyRevision, InstallationId,
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>(), Array.Empty<SyncAuthorityCloudHierarchySnapshot>()),
            "a retained definition still owns the endpoint after its root left the active set",
            "One Dropbox folder identity cannot be retained by more than one pair.");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT pair_json FROM sync_pair WHERE pair_id='" +
                   oldDefinition.Id + "' AND state<>'retired' AND selected=1;") == oldJson &&
               await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   definition.Id + "' AND state='blocked';") == "2",
            "pair-set validation rolls back publication before commit");
        await fixture.Db.RetirePairDefinitionAsync(third.Id);
        await fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition, oldDefinition.Id,
            oldDefinition.PolicyRevision, InstallationId, Array.Empty<SyncAuthorityCloudHierarchySnapshot>(),
            Array.Empty<SyncAuthorityCloudHierarchySnapshot>());
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   oldDefinition.Id + "' AND state='retired';") == "1",
            "the rebind retired the replaced pair in the same transaction");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   oldDefinition.Id + "' AND state='out_of_scope';") == "2" &&
               await ScalarAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM root_binding WHERE root_binding_id='root-old-right' AND state='active';") == "0",
            "exactly the replaced pair's own roots left the active set");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   definition.Id + "' AND pair_json IS NOT NULL AND selected=1;") == "1" &&
               await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   definition.Id + "' AND side IN ('local','dropbox') AND state='active';") == "2",
            "the rebind published the new pair with both roots active");
        Assert(await ScalarAsync(fixture.DatabasePath,
                   "SELECT json_extract(identity_json,'$.folderId') FROM root_binding WHERE root_binding_id='" +
                  leftRoot + "';") == fixture.Folder("old-left"),
            "the unchanged endpoint identity was retained exactly through the rebind");
        await AssertPersistedDisabledUnapprovedAsync(fixture.DatabasePath, definition.Id,
            "the rebound cloud pair");
    }

    // ----- H: races after prepare, and rollback of a failed right activation ---------------------------

    private static async Task RaceAndRollbackAsync()
    {
        await using var fixture = await Fixture.CreateAsync("h-race", "h");
        var definition = fixture.Draft("cand-left", "cand-right", "BIND1 race");
        var leftRoot = "root-cand-left-h";
        var rightRoot = "root-cand-right-h";
        await fixture.Db.PrepareCloudPairCandidateAsync(fixture.CandidateOf(definition, definition.PolicyRevision,
            leftRoot, rightRoot));
        await fixture.AllocateStagingAsync(definition, leftRoot);
        // Real nonterminal work for the candidate appears between prepare and publish.
        await fixture.Db.RegisterObjectAsync("object-h-race", "directory");
        var raceAdmission = await fixture.Db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", definition.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await fixture.Db.TransitionCommandRequestAsync(raceAdmission.CommandRequestId, "executing");
        await fixture.Db.CreateOperationAsync(new SyncAuthorityOperationPlan("operation-h-race", "idempotency-h",
            SyncAuthorityOperationKind.AllocateControl, definition.Id, "object-h-race", definition.PolicyRevision,
            ProfileId, "{}", 100,
            [new SyncAuthorityMember("member-h-race", definition.Id, "object-h-race", "lock", leftRoot, null, null,
                null, null, "{}")], [], []), admission: raceAdmission);
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition, null,
                null, InstallationId, Array.Empty<SyncAuthorityCloudHierarchySnapshot>(),
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>()),
            "publication while nonterminal candidate work remains",
            "Cloud-pair authoring cannot publish while queued, interrupted, blocked, cleanup or planning-referenced work remains.");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   definition.Id + "' AND side IN ('local','dropbox') AND state='active';") == "0",
            "the nonterminal-work refusal activated no candidate root");
        // A planning authority naming the candidate appears between prepare and publish.
        await ExecuteSqlAsync(fixture.DatabasePath, "UPDATE operation SET status='cancelled',terminal_utc='2026-09-21T00:00:00Z' WHERE " +
            "operation_id='operation-h-race';");
        var unrelated = fixture.Draft("third", "cand-right", "BIND1 planning reference");
        await fixture.Db.RegisterPairAsync(unrelated.Id, unrelated.PolicyRevision);
        await fixture.Db.RetainPairDefinitionAsync(unrelated, select: false);
        await fixture.Db.RegisterObjectAsync("object-h-plan", "directory");
        var planningPreconditions = JsonSerializer.Serialize(new
        {
            planningAuthorities = new[]
            {
                new SyncAuthorityPlanningAuthority("observation-set-h", definition.Id, unrelated.PolicyRevision,
                    "root-unrelated-left", "root-unrelated-right", "scan-unrelated-local",
                    "scan-unrelated-dropbox", "planning-state-h") { ObjectId = "object-h-plan" }
            }
        });
        var planningAdmission = await fixture.Db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", unrelated.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await fixture.Db.TransitionCommandRequestAsync(planningAdmission.CommandRequestId, "executing");
        await fixture.Db.CreateOperationAsync(new SyncAuthorityOperationPlan("operation-h-plan", "idempotency-hp",
            SyncAuthorityOperationKind.AllocateControl, unrelated.Id, "object-h-plan", unrelated.PolicyRevision,
            ProfileId, planningPreconditions, 100,
            [new SyncAuthorityMember("member-h-plan", unrelated.Id, "object-h-plan", "lock", leftRoot, null, null,
                null, null, "{}")], [], []), admission: planningAdmission);
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition, null,
                null, InstallationId, Array.Empty<SyncAuthorityCloudHierarchySnapshot>(),
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>()),
            "publication while a typed planning reference names the candidate from an unrelated pair",
            "Cloud-pair authoring cannot publish while queued, interrupted, blocked, cleanup or planning-referenced work remains.");
        // Rollback: a right-side activation collision must leave the left activation undone.
        await ExecuteSqlAsync(fixture.DatabasePath, "UPDATE operation SET status='cancelled',terminal_utc='2026-09-21T00:00:00Z' WHERE " +
            "operation_id IN ('operation-h-race','operation-h-plan');");
        var thirdDefinition = fixture.Draft("third", "cand-right", "BIND1 rollback third");
        await fixture.Db.RegisterPairAsync(thirdDefinition.Id, thirdDefinition.PolicyRevision);
        await fixture.Db.RetainPairDefinitionAsync(thirdDefinition, select: false);
        await fixture.Db.RegisterCloudEndpointRootBindingAsync("root-third-right", thirdDefinition.Id,
            SyncAuthorityCloudSides.LogicalRight, 1, InstallationId,
            SyncEndpointProviders.Get(fixture.KindFor("cand-right")).ProviderId!, fixture.Account("cand-right"),
            fixture.EndpointNamespaceFor("cand-right"), fixture.Folder("cand-right"), "/third-right",
            cancellationToken: default);
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition, null,
                null, InstallationId, Array.Empty<SyncAuthorityCloudHierarchySnapshot>(),
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>()),
            "publication whose right activation collides with a live third-pair root",
            "The right cloud root names the exact same provider folder identity as another active pair root;");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   definition.Id + "' AND side IN ('local','dropbox') AND state='active';") == "0",
            "the failed right activation rolled the left activation back");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT state FROM root_binding WHERE root_binding_id='" +
                   leftRoot + "';") == "blocked" &&
               await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   definition.Id + "' AND pair_json IS NULL;") == "1",
            "the rollback left the left root blocked and the candidate unpublished");
    }

    // ----- I: retry after a refused preparation, reopen, and schema stability ---------------------------

    private static async Task ReopenAndRetryAsync()
    {
        await using var fixture = await Fixture.CreateAsync("i-retry", "i");
        var definition = fixture.Draft("cand-left", "cand-right", "BIND1 retry");
        var schemaBefore = await SchemaSnapshotAsync(fixture.DatabasePath);
        // A first attempt whose candidate fields disagree with its own draft definition.
        var mismatched = fixture.CandidateOf(definition, definition.PolicyRevision, "root-cand-left-i",
            "root-cand-right-i") with
        {
            RightDisplayPath = "/mismatched"
        };
        await ExpectRefusalAsync<InvalidDataException>(() => fixture.Db.PrepareCloudPairCandidateAsync(mismatched),
            "a first preparation whose candidate fields disagree with its draft definition",
            "The draft definition's right endpoint does not agree with the candidate namespace, folder and display path.");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair;") == "0",
            "the refused first preparation retained nothing");
        // The exact retry after reopen succeeds with the identical input and no residue from the refusal.
        var reopened = new SyncAuthorityDatabase(fixture.DatabasePath);
        var prepared = await reopened.PrepareCloudPairCandidateAsync(fixture.CandidateOf(definition,
            definition.PolicyRevision, "root-cand-left-i", "root-cand-right-i"));
        Assert(!prepared.Replayed &&
               await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   definition.Id + "' AND state='blocked';") == "2",
            "the retry after reopen retained both blocked candidate roots exactly once");
        Assert(await SchemaSnapshotAsync(fixture.DatabasePath) == schemaBefore,
            "the refused preparation, the retry and the reopen changed no schema object");
    }

    /// <summary>
    /// O7 nested containment: with the real complete typed corpus chains supplied for both candidate roots, a
    /// live third-pair root whose folder is NESTED under the candidate left root must be refused by the shared
    /// guard, and the candidate must stay unpublished with no active root.
    /// </summary>
    private static async Task NestedContainmentAsync()
    {
        await using var fixture = await Fixture.CreateAsync("j-nested", "j");
        var definition = fixture.Draft("cand-left", "cand-right", "BIND1 nested");
        var leftRoot = "root-cand-left-j";
        var rightRoot = "root-cand-right-j";
        await fixture.Db.PrepareCloudPairCandidateAsync(fixture.CandidateOf(definition, definition.PolicyRevision,
            leftRoot, rightRoot));
        await fixture.AllocateStagingAsync(definition, leftRoot);
        var nestedDefinition = fixture.Draft("third", "cand-right", "BIND1 nested third");
        await fixture.Db.RegisterPairAsync(nestedDefinition.Id, nestedDefinition.PolicyRevision);
        await fixture.Db.RetainPairDefinitionAsync(nestedDefinition, select: false);
        var nestedFolder = fixture.Folder("cand-left") + "/nested";
        await fixture.Db.RegisterCloudEndpointRootBindingAsync("root-third-nested", nestedDefinition.Id,
            SyncAuthorityCloudSides.LogicalLeft, 1, InstallationId,
            SyncEndpointProviders.Get(fixture.KindFor("cand-left")).ProviderId!, fixture.Account("cand-left"),
            fixture.EndpointNamespaceFor("cand-left"), nestedFolder,
            fixture.DisplayPath("cand-left") + "/nested", cancellationToken: default);
        await ExpectRefusalAsync<InvalidOperationException>(() => fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition, null,
                null, InstallationId,
                new[]
                {
                    fixture.Chain("cand-left", leftRoot, fixture.Folder("cand-left")),
                    fixture.Chain("cand-left", "root-third-nested", nestedFolder, fixture.Folder("cand-left"))
                },
                new[] { fixture.Chain("cand-right", rightRoot, fixture.Folder("cand-right")) }),
            "publishing a candidate whose left root contains a live third-pair root, with real hierarchy input",
            "The left cloud root is contained by, or contains, another active pair root on the same provider;");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE pair_id='" +
                   definition.Id + "' AND side IN ('local','dropbox') AND state='active';") == "0" &&
               await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   definition.Id + "' AND pair_json IS NOT NULL;") == "0",
            "the nested-containment refusal activated no candidate root and published nothing");
    }

    private static async Task StagingAuthorityAsync()
    {
        await using var fixture = await Fixture.CreateAsync("k-staging", "k");
        var definition = fixture.Draft("cand-left", "cand-right", "BIND1 staging");
        const string leftRoot = "root-cand-left-k";
        await fixture.Db.PrepareCloudPairCandidateAsync(fixture.CandidateOf(definition,
            definition.PolicyRevision, leftRoot, "root-cand-right-k"));
        Task Publish() => fixture.Db.PublishCloudPairCandidateAsync(definition.Id, definition, null, null,
            InstallationId, Array.Empty<SyncAuthorityCloudHierarchySnapshot>(),
            Array.Empty<SyncAuthorityCloudHierarchySnapshot>());
        const string stageGate = "Publication requires the exact completed private staging allocation of this candidate, bound to its logical-left root;";
        await ExpectRefusalAsync<InvalidOperationException>(Publish, "publication with missing staging", stageGate);
        await fixture.AllocateStagingAsync(definition, leftRoot);
        var ownControl = await ScalarAsync(fixture.DatabasePath,
            "SELECT control_namespace_id FROM pair_control_namespace WHERE pair_id='" + definition.Id + "';");
        Assert(!string.IsNullOrEmpty(ownControl), "real allocator retained this candidate's control namespace");

        // A SECOND real allocation supplies the foreign identity. Only the fault-injection binding changes;
        // the test never fabricates a successful allocator journal, marker or verification record.
        var foreign = fixture.Draft("third", "cand-right", "BIND1 foreign staging");
        await fixture.Db.PrepareCloudPairCandidateAsync(fixture.CandidateOf(foreign,
            foreign.PolicyRevision, "third", "cand-right", "root-foreign-left-k", "root-foreign-right-k"));
        await fixture.AllocateStagingAsync(foreign, "root-foreign-left-k");
        await ExecuteSqlAsync(fixture.DatabasePath, "UPDATE pair_control_namespace SET control_namespace_id=" +
            "(SELECT control_namespace_id FROM pair_control_namespace WHERE pair_id='" + foreign.Id + "') " +
            "WHERE pair_id='" + definition.Id + "';");
        await ExpectRefusalAsync<InvalidOperationException>(Publish, "publication borrowing foreign staging", stageGate);
        await ExecuteSqlAsync(fixture.DatabasePath, "UPDATE pair_control_namespace SET control_namespace_id='" +
            ownControl + "' WHERE pair_id='" + definition.Id + "';");

        var workAdmission = await fixture.Db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", definition.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await fixture.Db.TransitionCommandRequestAsync(workAdmission.CommandRequestId, "executing");
        await fixture.AddWorkAsync(definition, leftRoot, "operation-k-unresolved",
            SyncAuthorityOperationKind.AllocateControl, workAdmission);
        await ExpectRefusalAsync<InvalidOperationException>(Publish, "publication with unresolved staging",
            "Cloud-pair authoring cannot publish while queued, interrupted, blocked, cleanup or planning-referenced work remains.");
        await ExecuteSqlAsync(fixture.DatabasePath, "UPDATE operation SET status='cancelled',terminal_utc='2026-09-21T00:00:00Z' " +
            "WHERE operation_id='operation-k-unresolved';");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_json IS NOT NULL;") == "0" &&
               await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM root_binding WHERE side IN ('local','dropbox') AND state='active';") == "0" &&
               await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM pair_control_namespace;") == "2",
            "all rejected publications preserved both inert candidates and both real allocations");
        await Publish();
        await AssertPersistedDisabledUnapprovedAsync(fixture.DatabasePath, definition.Id, "repaired exact staging");
        Assert(await ScalarAsync(fixture.DatabasePath, "SELECT count(*) FROM sync_pair WHERE pair_id='" +
                   foreign.Id + "' AND pair_json IS NULL;") == "1",
            "publishing the owner never publishes the foreign candidate");

        await using var unexpected = await Fixture.CreateAsync("k-unexpected", "ku");
        var unexpectedPair = unexpected.Draft("cand-left", "cand-right", "BIND1 unexpected work");
        await unexpected.Db.PrepareCloudPairCandidateAsync(unexpected.CandidateOf(unexpectedPair,
            unexpectedPair.PolicyRevision, "root-unexpected-left-k", "root-unexpected-right-k"));
        await unexpected.AllocateStagingAsync(unexpectedPair, "root-unexpected-left-k");
        var unexpectedAdmission = await unexpected.Db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", unexpectedPair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await unexpected.Db.TransitionCommandRequestAsync(unexpectedAdmission.CommandRequestId, "executing");
        await unexpected.AddWorkAsync(unexpectedPair, "root-unexpected-left-k", "operation-k-unexpected",
            SyncAuthorityOperationKind.UpdateRemote, unexpectedAdmission);
        // Legal ready-to-cancelled transition: no invented completion, changed immutable identity or effect.
        // This terminal non-control operation passes the nonterminal gate, but not the preparation-kind gate.
        await ExecuteSqlAsync(unexpected.DatabasePath, "UPDATE operation SET status='cancelled',terminal_utc='2026-09-21T00:00:00Z' " +
            "WHERE operation_id='operation-k-unexpected';");
        await ExpectRefusalAsync<InvalidOperationException>(() => unexpected.Db.PublishCloudPairCandidateAsync(
                unexpectedPair.Id, unexpectedPair, null, null, InstallationId,
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>(), Array.Empty<SyncAuthorityCloudHierarchySnapshot>()),
            "publication with unexpected terminal non-control work",
            "The candidate pair carries an unresolved or unexpected operation; cloud-pair authoring is refused.");
        Assert(await ScalarAsync(unexpected.DatabasePath,
                   "SELECT count(*) FROM sync_pair WHERE pair_json IS NOT NULL;") == "0" &&
               await ScalarAsync(unexpected.DatabasePath,
                   "SELECT count(*) FROM root_binding WHERE side IN ('local','dropbox') AND state='active';") == "0",
            "unexpected terminal work leaves the entire candidate unpublished and inactive");
    }

    private static async Task PreparedTwoSidePartitionAsync()
    {
        await using var fixture = await Fixture.CreateAsync("l-prepared-partition", "l");
        var parent = fixture.Draft("cand-left", "cand-right", "BIND1 excluded cloud parent") with
        {
            SelectionRules = [new SyncSelectionRule("Child", false)]
        };
        var endpoints = SyncCloudPairConfiguration.RequireProjection(parent);
        var childLeftFolder = "id:child-left-" + fixture.Name;
        var childRightFolder = "child-right-" + fixture.Name;
        var child = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
            SyncEndpoint.Cloud(endpoints.Left.Kind, endpoints.Left.ConnectionId!, endpoints.Left.NamespaceId!,
                childLeftFolder, endpoints.Left.FolderPath + "/Child"),
            SyncEndpoint.Cloud(endpoints.Right.Kind, endpoints.Right.ConnectionId!, endpoints.Right.NamespaceId!,
                childRightFolder, endpoints.Right.FolderPath + "/Child"), SyncMode.TwoWay),
            "BIND1 nested cloud child");
        const string childLeft = "root-l-child-left", childRight = "root-l-child-right";
        const string parentLeft = "root-l-parent-left", parentRight = "root-l-parent-right";
        var childChains = new[]
        {
            fixture.ChainNamed("cand-left", childLeft, childLeftFolder, ["Corpus", "Parent", "Child"],
                endpoints.Left.FolderId!),
            fixture.ChainNamed("cand-right", childRight, childRightFolder, ["Corpus", "Parent", "Child"],
                endpoints.Right.FolderId!)
        };
        await fixture.Db.PrepareCloudPairCandidateAsync(fixture.CandidateOf(child, child.PolicyRevision,
            childLeft, childRight));
        await fixture.AllocateStagingAsync(child, childLeft);
        await fixture.Db.PublishCloudPairCandidateAsync(child.Id, child, null, null, InstallationId,
            childChains, childChains);
        await fixture.Db.PrepareCloudPairCandidateAsync(fixture.CandidateOf(parent, parent.PolicyRevision,
            parentLeft, parentRight));
        await fixture.AllocateStagingAsync(parent, parentLeft);
        var parentChains = new[]
        {
            fixture.ChainNamed("cand-left", parentLeft, endpoints.Left.FolderId!, ["Corpus", "Parent"]),
            fixture.ChainNamed("cand-right", parentRight, endpoints.Right.FolderId!, ["Corpus", "Parent"])
        };
        var allChains = parentChains.Concat(childChains).ToArray();
        Task<SyncPairSet> Publish(SyncPair draft, IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> chains) =>
            fixture.Db.PublishCloudPairCandidateAsync(parent.Id, draft, null, null, InstallationId, chains, chains);
        async Task<string> State() =>
            (await ScalarAsync(fixture.DatabasePath,
                "SELECT json_group_array(json_array(pair_id,side,root_binding_id,generation,identity_json,state)) " +
                "FROM (SELECT * FROM root_binding ORDER BY root_binding_id);") ?? "") +
            (await ScalarAsync(fixture.DatabasePath,
                "SELECT json_group_array(json_array(pair_id,configuration_revision,pair_json,selected,state)) " +
                "FROM (SELECT * FROM sync_pair ORDER BY pair_id);") ?? "");
        var preparedState = await State();
        Assert(await ScalarAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM root_binding WHERE pair_id='" + parent.Id + "' AND state='blocked';") == "2",
            "both own candidate roots are blocked at first activation, while both child roots remain active");
        // The D1 cumulative correction changed this unsafe overlap refusal to the structured
        // OverlappingEndpointAcrossPairs refusal. The bound contract is the exact structured error code plus
        // the InvalidDataException type; the order-dependent prose detail is intentionally not asserted, and
        // every surrounding no-activation and no-publication assertion is retained unchanged.
        const string overlapGate = "OverlappingEndpointAcrossPairs";
        // The unbound-hierarchy path still reaches the cloud-root disjointness guard, whose structured code is
        // OverlappingEndpointAcrossPairs but whose class remains InvalidOperationException; only the stale
        // expected reason needed correcting here.
        await ExpectRefusalAsync<InvalidOperationException>(() => Publish(parent,
                allChains.Where(item => item.RootBindingId != parentRight).ToArray()),
            "omitted other own candidate-side hierarchy", overlapGate);
        Assert(await State() == preparedState, "omitted candidate-side evidence left all roots and pairs unchanged");
        await ExpectRefusalAsync<InvalidDataException>(() => Publish(parent,
                allChains.Select(item => item.RootBindingId == parentRight
                    ? item with { ProviderAccountId = fixture.Account("old-right") } : item).ToArray()),
            "foreign other own candidate-side account", "publication hierarchy is not bound");
        Assert(await State() == preparedState, "foreign evidence left all identities, revisions and states unchanged");
        await ExpectRefusalAsync<InvalidDataException>(() => Publish(parent with
                { SelectionRules = Array.Empty<SyncSelectionRule>() }, allChains),
            "unexcluded child cannot acquire a partition allowance", overlapGate);
        Assert(await State() == preparedState, "unexcluded child refusal left all roots and pairs unchanged");
        await ExpectRefusalAsync<InvalidOperationException>(() => Publish(parent with
                { SelectionRules = [new SyncSelectionRule("Child", false), new SyncSelectionRule("Child/keep", true)] },
                allChains), "included descendant exception cannot acquire a partition allowance", overlapGate);
        Assert(await State() == preparedState, "included descendant refusal left all roots and pairs unchanged");

        var published = await Publish(parent, allChains);
        Assert(published.Pairs.Count == 2 && published.Pairs.Any(item => item.Id == child.Id) &&
               published.Pairs.Any(item => item.Id == parent.Id), "complete publication retains both independent pairs");
        Assert(await ScalarAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM root_binding WHERE state='active' AND root_binding_id IN " +
                   "('root-l-child-left','root-l-child-right','root-l-parent-left','root-l-parent-right');") == "4",
            "both independently verified prepared roots activate atomically without retiring the child");
        await AssertPersistedDisabledUnapprovedAsync(fixture.DatabasePath, parent.Id, "new excluded parent");
        var reopened = new SyncAuthorityDatabase(fixture.DatabasePath);
        var readback = await reopened.LoadPairSetAsync();
        var retainedParent = readback.Pairs.Single(item => item.Id == parent.Id);
        Assert(retainedParent.SelectionRules!.SequenceEqual(parent.SelectionRules!) &&
               readback.Pairs.Any(item => item.Id == child.Id),
            "reopen preserves the exact whole-child exclusion and the child identity");
    }

    /// <summary>
    /// O10: one compact EXACT schema snapshot - every sqlite_master definition in name order plus the schema
    /// version - so a case proves the whole schema is unchanged rather than only counting objects.
    /// </summary>
    private static async Task<string?> SchemaSnapshotAsync(string databasePath)
    {
        var definitions = await ScalarAsync(databasePath,
            "SELECT group_concat(name || ':' || coalesce(sql,''), '|') FROM " +
            "(SELECT name, sql FROM sqlite_master ORDER BY name);");
        var version = await ScalarAsync(databasePath, "PRAGMA user_version;");
        return (definitions ?? "") + "#" + (version ?? "none");
    }
}
