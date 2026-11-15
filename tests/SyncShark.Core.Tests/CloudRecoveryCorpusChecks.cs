using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// CC2E focused proofs: schema 6 recovery-corpus scope and the typed recovery lookup, verified legacy
// enrichment and scoped registration helpers, over real isolated SQLite databases this run creates under
// the supplied root. The provider side is synthetic typed input only: no live provider, credential,
// transport, installed database or product state is reachable and no effect is authorised. CC4A separately
// proves the real production HTTP hierarchy and the Host consumers.
internal static class CloudRecoveryCorpusChecks
{
    private const string InstallationId = "installation-cloud-recovery-corpus-316";
    private const string SourceRevision = "cloud-recovery-corpus-316";
    private const string DurableUtc = "2026-09-19T00:00:00.0000000+00:00";

    // The Google account deliberately records a native permission id that differs from its account key, so
    // a mapping that treats the two as interchangeable fails.
    private const string GooglePermissionA = "permission-gA-316";
    private const string GoogleKeyA = "account-key-gA-316";
    private const string GooglePermissionB = "permission-gB-316";
    private const string GoogleKeyB = "account-key-gB-316";
    private const string DropboxAccountId = "dbid-corpus-316";
    private const string DropboxNamespaceId = "namespace-corpus-316";
    private const string DropboxHomeNamespaceId = "namespace-home-corpus-316";

    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated cloud recovery corpus fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;

        await FreshSchemaSixAsync();
        await VersionFiveMigrationAsync();
        await InterruptedMigrationRollsBackAsync();
        await TamperedRollbackRejectedAsync();
        await LocalUniquenessUnchangedAsync();
        await DistinctCorporaAsync();
        await ExactLookupAsync();
        await CollisionAndLegacyAsync();
        await RetiredRowNotRevivedAsync();
        await VerifiedEnrichmentAsync();
        await OverlapRefusalAsync();
        SourceBoundary();

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "CloudRecoveryCorpusChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "cloud-recovery-corpus-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloud recovery corpus: " + _assertions +
            " assertions; real isolated SQLite schema-5 to current migration including schema-6 corpus scope, rollback and refusal proofs," +
            " and exact provider/account/namespace/root/corpus lookup, verified legacy enrichment and scoped" +
            " registration over the one shared cloud root comparison.");
    }

    // ----- 1. a fresh database uses the current schema with corpus scope ------------------------------------------------

    private static async Task FreshSchemaSixAsync()
    {
        Claim("CC2E-1/fresh", "A fresh authority database has the current schema and repeats initialization idempotently");
        var path = Path.Combine(_root, "fresh", "syncshark.db");
        var authority = new SyncAuthorityDatabase(path);
        await authority.InitializeAsync(InstallationId, "profile-cloud-recovery-corpus-316", SourceRevision, "{}");
        Require(await NumberAsync(path, "PRAGMA user_version;") == SyncAuthoritySchema.Version &&
                SyncAuthoritySchema.Version >= 6,
            "a fresh authority database is exactly the current supported schema, including corpus scope");
        Require(await NumberAsync(path, "SELECT count(*) FROM schema_migration WHERE schema_version=" + SyncAuthoritySchema.Version + ";") == 1 &&
                await NumberAsync(path, "SELECT count(*) FROM schema_migration;") == 1,
            "a fresh database records exactly one current-schema migration receipt");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM pragma_table_info('recovery_root_binding') WHERE name='provider_corpus_id' AND type='TEXT' AND \"notnull\"=0;") == 1,
            "recovery_root_binding gains exactly one nullable TEXT provider_corpus_id column");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='one_active_recovery_root_per_side';") == 1 &&
                await NumberAsync(path,
                "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='recovery_root_generation_per_account';") == 1,
            "both retained recovery index names are recreated exactly once");
        Require((await TextAsync(path,
                    "SELECT sql FROM sqlite_schema WHERE name='one_active_recovery_root_per_side';") ?? "")
                .Contains("coalesce(provider_corpus_id,'')", StringComparison.Ordinal) &&
                (await TextAsync(path,
                    "SELECT sql FROM sqlite_schema WHERE name='recovery_root_generation_per_account';") ?? "")
                .Contains("coalesce(provider_corpus_id,'')", StringComparison.Ordinal),
            "both recovery indices scope their uniqueness to the exact retained corpus");
        Require(await TextAsync(path, "PRAGMA foreign_key_check;") is null &&
                string.Equals(await TextAsync(path, "PRAGMA quick_check;"), "ok", StringComparison.Ordinal),
            "the fresh current-schema database passes foreign-key and quick checks");
        Require(!File.Exists(authority.Version2RollbackDatabasePath) &&
                !File.Exists(authority.Version3RollbackDatabasePath) &&
                !File.Exists(authority.Version4RollbackDatabasePath) &&
                !File.Exists(authority.Version5RollbackDatabasePath),
            "a fresh database creates no migration rollback copy");
        await RejectAsync(() => ExecuteAsync(path,
                "INSERT INTO recovery_root_binding(recovery_root_binding_id,installation_id,provider_account_id," +
                " side,display_path,normalized_path_key,identity_json,generation,status,verified_utc,provider_corpus_id)" +
                " VALUES('empty-corpus','" + InstallationId + "',NULL,'dropbox','/E','/E','{}',1," +
                "'active','" + DurableUtc + "','');"),
            "the schema-6 column refuses an empty verified corpus literal");
        var before = await SnapshotAsync(path);
        await authority.InitializeAsync(InstallationId, "profile-cloud-recovery-corpus-316", SourceRevision, "{}");
        Require(await SnapshotAsync(path) == before && await NumberAsync(path, "PRAGMA user_version;") == SyncAuthoritySchema.Version,
            "repeating initialization on a current database changes no retained row");
    }

    // ----- 2. a real schema-5 database migrates to schema 6 without losing a value -----------------

    private static async Task VersionFiveMigrationAsync()
    {
        Claim("CC2E-1/migrate",
            "The schema-5 to schema-6 migration preserves every retained recovery row and leaves the corpus NULL");
        var path = Path.Combine(_root, "from-v5", "syncshark.db");
        await BuildVersionFivePredecessorAsync(path);
        var before = await SnapshotAsync(path);
        var recoveryBefore = await RowSignatureAsync(path, "recovery_root_binding");
        var authority = new SyncAuthorityDatabase(path);
        await authority.InitializeAsync(InstallationId, "profile-cloud-recovery-corpus-316", SourceRevision, "{}");
        Require(await NumberAsync(path, "PRAGMA user_version;") == SyncAuthoritySchema.Version,
            "a version-5 predecessor migrates to the current schema through the corpus-scope migration");
        Require(await NumberAsync(path, "SELECT count(*) FROM schema_migration;") == 2 &&
                await NumberAsync(path, "SELECT count(*) FROM schema_migration WHERE schema_version=5;") == 1 &&
                await NumberAsync(path, "SELECT count(*) FROM schema_migration WHERE schema_version=" + SyncAuthoritySchema.Version + ";") == 1,
            "the version-5 receipt is retained and exactly one current-schema receipt is added");
        Require(await SnapshotAsync(path) == before,
            "every pre-existing identity, JSON, path, status, generation and value is preserved by the migration");
        Require(await RowSignatureAsync(path,
                "recovery_root_binding",
                "recovery_root_binding_id,installation_id,provider_account_id,side,display_path," +
                "normalized_path_key,identity_json,generation,status,verified_utc") == recoveryBefore,
            "every retained recovery row keeps its exact prior column values");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM recovery_root_binding WHERE provider_corpus_id IS NOT NULL;") == 0,
            "every retained recovery row receives NULL and no corpus is inferred from a path, label or account");
        Require(await NumberAsync(path, "SELECT count(*) FROM recovery_root_binding;") == 4,
            "all four representative local, Dropbox and Google recovery rows survive");
        Require(File.Exists(authority.Version5RollbackDatabasePath) &&
                await NumberAsync(authority.Version5RollbackDatabasePath, "PRAGMA user_version;") == 5 &&
                await NumberAsync(authority.Version5RollbackDatabasePath,
                    "SELECT count(*) FROM recovery_root_binding;") == 4,
            "the exact version-5 rollback copy is retained with every pre-migration recovery row");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM pragma_table_info('recovery_root_binding') WHERE name='provider_corpus_id';") == 1,
            "the migrated database carries the new nullable corpus column");
        await RejectAsync(() => ExecuteAsync(path,
                "INSERT INTO recovery_root_binding(recovery_root_binding_id,installation_id,provider_account_id," +
                " side,display_path,normalized_path_key,identity_json,generation,status,verified_utc)" +
                " VALUES('recovery-orphan-corpus','" + InstallationId + "','absent-provider-account'," +
                "'dropbox','/Orphan','/Orphan','{\"folderId\":\"id:orphan\"}',9,'active','" +
                DurableUtc + "');"),
            "the migrated recovery table still enforces its provider-account foreign key");
        Require(await TextAsync(path, "PRAGMA foreign_key_check;") is null,
            "the migrated database has no foreign-key violation");
    }

    private static async Task InterruptedMigrationRollsBackAsync()
    {
        Claim("CC2E-1/interrupt", "An interrupted schema-6 migration exposes no partial authority and converges on retry");
        var path = Path.Combine(_root, "interrupted", "syncshark.db");
        await BuildVersionFivePredecessorAsync(path);
        var before = await SnapshotAsync(path);
        var authority = new SyncAuthorityDatabase(path);
        var interrupted = false;
        SyncAuthorityDatabase.MigrationBoundaryForTest = observed =>
        {
            if (!string.Equals(observed, "v6-schema-applied-before-version-commit", StringComparison.Ordinal))
                return;
            interrupted = true;
            throw new IOException("Injected version-6 migration interruption");
        };
        try
        {
            await authority.InitializeAsync(InstallationId, "profile-cloud-recovery-corpus-316", SourceRevision,
                "{}");
            throw new InvalidOperationException("The injected version-6 interruption did not fire.");
        }
        catch (IOException error) when (error.Message == "Injected version-6 migration interruption")
        {
        }
        finally
        {
            SyncAuthorityDatabase.MigrationBoundaryForTest = null;
        }
        Require(interrupted, "the version-6 pre-commit boundary is reachable and interruptible");
        Require(await NumberAsync(path, "PRAGMA user_version;") == 5 && await SnapshotAsync(path) == before &&
                await NumberAsync(path,
                    "SELECT count(*) FROM pragma_table_info('recovery_root_binding') WHERE name='provider_corpus_id';") == 0,
            "an interrupted schema-6 migration exposes no partially upgraded database and no new column");
        Require(File.Exists(authority.Version5RollbackDatabasePath) &&
                await NumberAsync(authority.Version5RollbackDatabasePath, "PRAGMA user_version;") == 5,
            "the interrupted attempt retained a verified version-5 rollback copy before migrating");
        var rollbackHash = await FileHashAsync(authority.Version5RollbackDatabasePath);
        await authority.InitializeAsync(InstallationId, "profile-cloud-recovery-corpus-316", SourceRevision, "{}");
        Require(await NumberAsync(path, "PRAGMA user_version;") == SyncAuthoritySchema.Version &&
                rollbackHash == await FileHashAsync(authority.Version5RollbackDatabasePath),
            "the retried migration converges and leaves the verified version-5 rollback copy untouched");
    }

    private static async Task TamperedRollbackRejectedAsync()
    {
        Claim("CC2E-1/tamper", "A tampered version-5 rollback copy is refused before migration and nothing is rewritten");
        var path = Path.Combine(_root, "tampered", "syncshark.db");
        await BuildVersionFivePredecessorAsync(path);
        var before = await SnapshotAsync(path);
        var authority = new SyncAuthorityDatabase(path);
        SyncAuthorityDatabase.MigrationBoundaryForTest = observed =>
        {
            if (string.Equals(observed, "v6-schema-applied-before-version-commit", StringComparison.Ordinal))
                throw new IOException("Injected version-6 migration interruption");
        };
        try
        {
            await authority.InitializeAsync(InstallationId, "profile-cloud-recovery-corpus-316", SourceRevision,
                "{}");
        }
        catch (IOException)
        {
        }
        finally
        {
            SyncAuthorityDatabase.MigrationBoundaryForTest = null;
        }
        Require(File.Exists(authority.Version5RollbackDatabasePath),
            "the interrupted attempt retained its version-5 rollback copy for tamper testing");
        await ExecuteAsync(authority.Version5RollbackDatabasePath,
            "UPDATE recovery_root_binding SET display_path='/Tampered' WHERE side='local';");
        await RejectAsync(() => authority.InitializeAsync(InstallationId, "profile-cloud-recovery-corpus-316",
                SourceRevision, "{}"),
            "a tampered version-5 rollback copy is refused before migration");
        Require(await NumberAsync(path, "PRAGMA user_version;") == 5 && await SnapshotAsync(path) == before,
            "the refused tampered rollback leaves the user database and every retained row unchanged");
    }

    // ----- 3. the retained local uniqueness behaviour is unchanged ---------------------------------

    private static async Task LocalUniquenessUnchangedAsync()
    {
        Claim("CC2E-1/local", "The retained unverified rows keep exactly their previous uniqueness on the local side");
        var path = Path.Combine(_root, "local-uniqueness", "syncshark.db");
        await BuildVersionFivePredecessorAsync(path);
        var authority = new SyncAuthorityDatabase(path);
        await authority.InitializeAsync(InstallationId, "profile-cloud-recovery-corpus-316", SourceRevision, "{}");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM recovery_root_binding WHERE side='local' AND status='active';") == 1,
            "the predecessor retains its one active local recovery root");
        await RejectAsync(() => ExecuteAsync(path,
                "INSERT INTO recovery_root_binding(recovery_root_binding_id,installation_id,provider_account_id," +
                " side,display_path,normalized_path_key,identity_json,generation,status,verified_utc)" +
                " VALUES('local-second','" + InstallationId + "',NULL,'local','/Local/Second'," +
                "'/Local/Second','{}',5,'active','" + DurableUtc + "');"),
            "a second unverified active local recovery root is still refused by the retained index");
        await ExecuteAsync(path,
            "INSERT INTO recovery_root_binding(recovery_root_binding_id,installation_id,provider_account_id," +
            " side,display_path,normalized_path_key,identity_json,generation,status,verified_utc,provider_corpus_id)" +
            " VALUES('local-scoped','" + InstallationId + "',NULL,'local','/Local/Scoped'," +
            "'/Local/Scoped','{}',6,'active','" + DurableUtc + "','local-corpus');");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM recovery_root_binding WHERE side='local' AND status='active';") == 2,
            "uniqueness on the local side is scoped to the exact corpus, never to a display path");
    }

    // ----- 4. known different corpora on one account can each hold one active recovery root --------

    private static async Task DistinctCorporaAsync()
    {
        Claim("CC2E-2/corpora",
            "One Google account holds one exact active recovery root per corpus, and each scoped root is found by its exact scope");
        var authority = await OpenAuthorityAsync("distinct-corpora");
        var path = DatabasePath("distinct-corpora");
        await RegisterGoogleAccountAsync(authority);
        var myDrive = GoogleScope("root-gA-mydrive", "my-drive-root-gA", "my-drive-gA");
        var sharedA = GoogleScope("root-gA-sda", "shared-drive-a-root", "shared-drive-a");
        var sharedB = GoogleScope("root-gA-sdb", "shared-drive-b-root", "shared-drive-b");
        await RegisterScopeAsync(authority, myDrive, GoogleChain(myDrive));
        await RegisterScopeAsync(authority, sharedA, GoogleChain(myDrive), GoogleChain(sharedA));
        await RegisterScopeAsync(authority, sharedB, GoogleChain(myDrive), GoogleChain(sharedA),
            GoogleChain(sharedB));
        Require(await NumberAsync(path,
                "SELECT count(*) FROM recovery_root_binding WHERE status='active' AND provider_corpus_id IS NOT NULL;") == 3,
            "three actual corpora of one account each hold their own active recovery root");
        Require(await NumberAsync(path,
                "SELECT count(DISTINCT provider_corpus_id) FROM recovery_root_binding WHERE status='active';") == 3,
            "each retained active recovery root carries its own opaque corpus identity");
        var found = await authority.FindCloudRecoveryRootAsync(InstallationId, SyncProviders.GoogleDrive,
            GoogleAccountId(GooglePermissionA, GoogleKeyA), GooglePermissionA, sharedA.FolderId, sharedA.CorpusId);
        Require(found is not null && found.RecoveryRootBindingId == sharedA.RootBindingId &&
                found.ProviderCorpusId == sharedA.CorpusId && found.FolderId == sharedA.FolderId,
            "the exact provider, account, namespace, root and corpus lookup returns exactly its retained scope");
        var selection = await authority.EnrichCloudRecoveryRootCorpusAsync(sharedA.RootBindingId, InstallationId,
            SyncProviders.GoogleDrive, GoogleAccountId(GooglePermissionA, GoogleKeyA), GooglePermissionA,
            sharedA.FolderId, sharedA.CorpusId, default,
            new[] { GoogleChain(myDrive), GoogleChain(sharedA), GoogleChain(sharedB) });
        Require(selection.ProviderCorpusId == sharedA.CorpusId &&
                await NumberAsync(path,
                    "SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id='" +
                    sharedA.RootBindingId + "' AND provider_corpus_id='" + sharedA.CorpusId + "';") == 1,
            "an exact same-id replay preserves the already verified corpus");
    }

    private static async Task ExactLookupAsync()
    {
        Claim("CC2E-2/lookup", "A wrong provider, account, namespace, root or corpus is refused by the exact lookup without a change");
        var authority = await OpenAuthorityAsync("exact-lookup");
        var path = DatabasePath("exact-lookup");
        await RegisterGoogleAccountAsync(authority);
        var scope = GoogleScope("root-lookup", "root-lookup-folder", "corpus-lookup");
        await RegisterScopeAsync(authority, scope, GoogleChain(scope));
        var before = await RowSignatureAsync(path, "recovery_root_binding");
        Require(await authority.FindCloudRecoveryRootAsync(InstallationId, SyncProviders.GoogleDrive,
                GoogleAccountId(GooglePermissionA, GoogleKeyA), GooglePermissionA, scope.FolderId,
                "another-corpus") is null,
            "a wrong corpus returns null");
        Require(await authority.FindCloudRecoveryRootAsync(InstallationId, SyncProviders.GoogleDrive,
                GoogleAccountId(GooglePermissionA, GoogleKeyA), GooglePermissionA, "another-root",
                scope.CorpusId) is null,
            "a wrong recovery root returns null");
        Require(await authority.FindCloudRecoveryRootAsync(InstallationId, SyncProviders.GoogleDrive,
                GoogleAccountId(GooglePermissionA, GoogleKeyA), "another-namespace", scope.FolderId,
                scope.CorpusId) is null,
            "a wrong endpoint namespace returns null rather than matching another account leg");
        Require(await RowSignatureAsync(path, "recovery_root_binding") == before,
            "every refused lookup leaves the retained recovery authority byte-identical");
        await RejectAsync(() => authority.FindCloudRecoveryRootAsync(InstallationId, "not-a-registered-provider",
                GoogleAccountId(GooglePermissionA, GoogleKeyA), GooglePermissionA, scope.FolderId, scope.CorpusId),
            "an unregistered provider is refused by the shared provider registry");
        await RejectAsync(() => authority.FindCloudRecoveryRootAsync(InstallationId, SyncProviders.GoogleDrive,
                GoogleAccountId(GooglePermissionA, GoogleKeyA), GooglePermissionA, scope.FolderId, "  "),
            "an empty corpus never satisfies a corpus lookup");
        await RejectAsync(() => authority.FindCloudRecoveryRootAsync(InstallationId, SyncProviders.GoogleDrive,
                GoogleAccountId(GooglePermissionB, GoogleKeyB), GooglePermissionA, scope.FolderId, scope.CorpusId),
            "an account of another installation or an absent account is refused");
        Require(await RowSignatureAsync(path, "recovery_root_binding") == before,
            "every refusal leaves the retained recovery authority byte-identical");
    }

    // ----- 5. collision, unclassified legacy refusal and verified in-place enrichment ---------------

    private static async Task CollisionAndLegacyAsync()
    {
        Claim("CC2E-2/collision",
            "One account and corpus is a collision for another folder, and an unclassified legacy root blocks a new binding");
        var authority = await OpenAuthorityAsync("collision-legacy");
        var path = DatabasePath("collision-legacy");
        await RegisterGoogleAccountAsync(authority);
        var first = GoogleScope("root-collision", "collision-root", "corpus-collision");
        await RegisterScopeAsync(authority, first, GoogleChain(first));
        var otherFolder = first with { RootBindingId = "root-collision-other", FolderId = "collision-other" };
        var refused = await RefusalAsync(() => RegisterScopeAsync(authority, otherFolder,
            GoogleChain(first), GoogleChain(otherFolder)));
        Require(refused is not null && refused.Contains("collision", StringComparison.Ordinal),
            "a second folder for the same account and corpus is refused as a collision, never as a silent retirement");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id='" +
                first.RootBindingId + "' AND status='active';") == 1,
            "the retained recovery root survives the refused collision unchanged");

        // A representative unclassified legacy Google recovery row, exactly as a predecessor would carry it.
        await RegisterGoogleAccountAsync(authority, GooglePermissionB, GoogleKeyB);
        await SeedLegacyRecoveryAsync(path, "legacy-google-root",
            GoogleAccountId(GooglePermissionB, GoogleKeyB), "legacy-google-folder", null);
        var blocking = GoogleScope("root-blocked", "blocked-root", "corpus-blocked",
            GooglePermissionB, GoogleKeyB);
        var unclassified = await RefusalAsync(() => RegisterScopeAsync(authority, blocking,
            GoogleChain(blocking)));
        Require(unclassified is not null && unclassified.Contains("no verified corpus", StringComparison.Ordinal),
            "creating another recovery binding while a same-account legacy root has no verified corpus is refused");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id='root-blocked';") == 0,
            "the refused binding leaves no partial recovery row behind");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM recovery_root_binding WHERE provider_account_id='" +
                GoogleAccountId(GooglePermissionB, GoogleKeyB) + "' AND provider_corpus_id IS NULL;") == 1,
            "the unclassified legacy root itself stays retained and unverified");
    }

    private static async Task RetiredRowNotRevivedAsync()
    {
        Claim("CC2E-2/retired",
            "A retained recovery row that is no longer active is never revived or rewritten by a new request");
        var authority = await OpenAuthorityAsync("retired-root");
        var path = DatabasePath("retired-root");
        await RegisterGoogleAccountAsync(authority);
        var scope = GoogleScope("root-retired", "retired-root-folder", "corpus-retired");
        await RegisterScopeAsync(authority, scope, GoogleChain(scope));
        await ExecuteAsync(path,
            "UPDATE recovery_root_binding SET status='retired' WHERE recovery_root_binding_id='root-retired';");
        var before = await RowSignatureAsync(path, "recovery_root_binding");
        var refused = await RefusalAsync(() => RegisterScopeAsync(authority, scope, GoogleChain(scope)));
        Require(refused is not null && refused.Contains("not active", StringComparison.Ordinal),
            "a retained recovery row that is not active is refused rather than revived");
        Require(await RowSignatureAsync(path, "recovery_root_binding") == before,
            "the refused revival leaves the retained recovery row completely unchanged");
    }

    private static async Task VerifiedEnrichmentAsync()
    {
        Claim("CC2E-2/enrich",
            "A verified in-place enrichment changes exactly the corpus column and an ordinary legacy replay never erases it");
        var authority = await OpenAuthorityAsync("verified-enrichment");
        var path = DatabasePath("verified-enrichment");
        await RegisterDropboxAccountAsync(authority);
        var legacyId = "legacy-dropbox-root";
        await SeedLegacyRecoveryAsync(path, legacyId,
            SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, DropboxAccountId,
                DropboxNamespaceId, DropboxHomeNamespaceId), "legacy-dropbox-folder", DropboxNamespaceId);
        var columns = "recovery_root_binding_id,installation_id,provider_account_id,side,display_path," +
            "normalized_path_key,identity_json,generation,status,verified_utc";
        var beforeRow = await RowSignatureAsync(path, "recovery_root_binding", columns);
        var chain = DropboxChain(legacyId, "legacy-dropbox-folder", Array.Empty<string>());
        await RejectAsync(() => authority.EnrichCloudRecoveryRootCorpusAsync(legacyId, InstallationId,
                SyncProviders.Dropbox,
                SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, DropboxAccountId,
                    DropboxNamespaceId, DropboxHomeNamespaceId), DropboxNamespaceId, "legacy-dropbox-folder",
                "namespace-corpus-316"),
            "enrichment without a freshly verified corpus chain is refused");
        await RejectAsync(() => authority.EnrichCloudRecoveryRootCorpusAsync(legacyId, InstallationId,
                SyncProviders.Dropbox,
                SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, DropboxAccountId,
                    DropboxNamespaceId, DropboxHomeNamespaceId), DropboxNamespaceId, "another-folder",
                "namespace-corpus-316", default, new[] { chain with { FolderId = "another-folder" } }),
            "enrichment against a different folder identity is refused");
        await RejectAsync(() => authority.EnrichCloudRecoveryRootCorpusAsync(legacyId, InstallationId,
                SyncProviders.Dropbox,
                SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, DropboxAccountId,
                    DropboxNamespaceId, DropboxHomeNamespaceId), "another-namespace", "legacy-dropbox-folder",
                "namespace-corpus-316", default, new[] { chain }),
            "enrichment against a different endpoint namespace is refused");
        Require(await RowSignatureAsync(path, "recovery_root_binding", columns) == beforeRow &&
                await NumberAsync(path,
                    "SELECT count(*) FROM recovery_root_binding WHERE provider_corpus_id IS NOT NULL;") == 0,
            "every refused enrichment leaves the retained legacy row completely unchanged");
        var enriched = await authority.EnrichCloudRecoveryRootCorpusAsync(legacyId, InstallationId,
            SyncProviders.Dropbox,
            SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, DropboxAccountId,
                DropboxNamespaceId, DropboxHomeNamespaceId), DropboxNamespaceId, "legacy-dropbox-folder",
            DropboxNamespaceId, default, new[] { chain });
        Require(enriched.ProviderCorpusId == DropboxNamespaceId &&
                await RowSignatureAsync(path, "recovery_root_binding", columns) == beforeRow,
            "verified enrichment writes exactly one column and preserves every other retained value");
        await RejectAsync(() => authority.EnrichCloudRecoveryRootCorpusAsync(legacyId, InstallationId,
                SyncProviders.Dropbox,
                SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, DropboxAccountId,
                    DropboxNamespaceId, DropboxHomeNamespaceId), DropboxNamespaceId, "legacy-dropbox-folder",
                "a-different-corpus", default, new[] { chain }),
            "a nonnull recovery scope is never changed by a different request");
        await authority.RegisterDropboxRecoveryRootAsync(legacyId, InstallationId,
            SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, DropboxAccountId,
                DropboxNamespaceId, DropboxHomeNamespaceId), "/Recovery/Legacy", "legacy-dropbox-folder",
            DropboxNamespaceId, 7);
        Require(await NumberAsync(path,
                "SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id='" + legacyId +
                "' AND provider_corpus_id='" + DropboxNamespaceId + "' AND generation=7 AND status='active';") == 1,
            "an ordinary legacy registration replay after scoped enrichment never erases the verified corpus");
        var replay = await authority.RegisterCloudRecoveryRootAsync(legacyId, InstallationId,
            SyncProviders.Dropbox, DropboxProviderAccountId(), DropboxNamespaceId, "legacy-dropbox-folder",
            DropboxNamespaceId, "/Recovery/Legacy", 7, default, new[] { chain });
        Require(replay.ProviderCorpusId == DropboxNamespaceId && replay.RecoveryRootBindingId == legacyId,
            "an exact same-id scoped replay also keeps the retained verified corpus");
        var collision = await RefusalAsync(() => authority.RegisterCloudRecoveryRootAsync("root-dropbox-other",
            InstallationId, SyncProviders.Dropbox, DropboxProviderAccountId(), DropboxNamespaceId,
            "another-dropbox-folder", DropboxNamespaceId, "/Recovery/Other", 1, default,
            new[] { chain, DropboxChain("root-dropbox-other", "another-dropbox-folder", Array.Empty<string>()) }));
        Require(collision is not null && collision.Contains("collision", StringComparison.Ordinal),
            "another folder for one Dropbox account and corpus is a collision, never a silent retirement");
    }

    // ----- 6. the shared root comparison still refuses an overlap before any scope is written -----

    private static async Task OverlapRefusalAsync()
    {
        Claim("CC2E-2/overlap",
            "Recovery registration reuses the one shared root comparison and refuses a nested root instead of normalising it");
        var authority = await OpenAuthorityAsync("overlap-refusal");
        var path = DatabasePath("overlap-refusal");
        await RegisterGoogleAccountAsync(authority);
        var parent = GoogleScope("root-overlap-parent", "overlap-parent", "corpus-overlap");
        await RegisterScopeAsync(authority, parent, GoogleChain(parent));
        var nested = GoogleScope("root-overlap-child", "overlap-child", "corpus-overlap-child");
        var nestedChain = GoogleChain(nested) with
        {
            ChainFolderIds = new[] { GoogleCorpusRootId(GooglePermissionA), "overlap-parent", "overlap-child" }
        };
        var refused = await RefusalAsync(() => RegisterScopeAsync(authority, nested, GoogleChain(parent),
            nestedChain));
        Require(refused is not null &&
                (refused.Contains("OverlappingEndpointAcrossPairs", StringComparison.Ordinal) ||
                 refused.Contains("Nested", StringComparison.Ordinal)),
            "a recovery root nested under a retained root is refused by the shared comparison");
        var unverified = GoogleScope("root-overlap-unverified", "overlap-unverified", "corpus-overlap-u");
        var unverifiedRefusal = await RefusalAsync(() => RegisterScopeAsync(authority, unverified,
            GoogleChain(unverified), GoogleChain(parent) with { CompleteToCorpusRoot = false }));
        Require(unverifiedRefusal is not null &&
                unverifiedRefusal.Contains("UnverifiedCloudNamespace", StringComparison.Ordinal),
            "a recovery root with no bound fresh hierarchy is refused as unverified, never assumed disjoint");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM recovery_root_binding WHERE status='active';") == 1,
            "every refused overlap leaves exactly the one retained recovery root");
        await RejectAsync(() => authority.RegisterCloudRecoveryRootAsync("root-overlap-copy", InstallationId,
                SyncProviders.GoogleDrive, GoogleAccountId(GooglePermissionA, GoogleKeyA), GooglePermissionA,
                parent.FolderId, parent.CorpusId, "/Drive/Overlap", 1, default, new[] { GoogleChain(parent) }),
            "a second recovery binding for the exact same provider root identity is refused as the same object");
    }

    private static void SourceBoundary()
    {
        var repositoryRoot = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (repositoryRoot is not null &&
               !File.Exists(Path.Combine(repositoryRoot.FullName, "SyncShark.sln")))
            repositoryRoot = repositoryRoot.Parent;
        if (repositoryRoot is null)
            throw new InvalidOperationException("Run the cloud recovery corpus checks from the repository tree.");
        var migration = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "SyncShark.Core",
            "SyncAuthoritySchemaMigration6.cs"));
        Require(migration.Contains("provider_corpus_id", StringComparison.Ordinal) &&
                migration.Contains("one_active_recovery_root_per_side", StringComparison.Ordinal) &&
                migration.Contains("recovery_root_generation_per_account", StringComparison.Ordinal) &&
                !migration.Contains("DROP TABLE", StringComparison.Ordinal) &&
                !migration.Contains("PRAGMA foreign_keys", StringComparison.Ordinal),
            "the schema-6 migration adds one column and recreates both recovery indices without rebuilding a table or disabling foreign keys");
        var endpoints = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "SyncShark.Core",
            "SyncAuthorityDatabase.CloudEndpoints.cs"));
        Require(endpoints.Contains("RequireCloudRootIdentityAvailableAsync", StringComparison.Ordinal) &&
                endpoints.Contains("SyncAuthorityCloudObjectOverlapGuard.RequireDistinctInventories",
                    StringComparison.Ordinal) &&
                endpoints.Contains("SyncEndpointProviders.TryGet", StringComparison.Ordinal),
            "the recovery helpers reuse the one shared root comparison and keep the overlap-guard hand-off obligation explicit");
    }

    // ----- fixtures: real isolated SQLite recovery authority ----------------------------------------

    private sealed record Scope(string Provider, string ProviderAccountId, string NamespaceId,
        string RootBindingId, string FolderId, string CorpusId, string DisplayPath);

    private static string GoogleAccountId(string permissionId, string key) =>
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.GoogleDrive, permissionId, key, key);

    private static string DropboxProviderAccountId() =>
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, DropboxAccountId,
            DropboxNamespaceId, DropboxHomeNamespaceId);

    // The Google corpus root id stands in for the provider-issued My Drive root or shared-drive id that a
    // real adapter reports; it is opaque and never derived from a display path.
    private static string GoogleCorpusRootId(string permissionId) => "corpus-root-" + permissionId;

    private static Scope GoogleScope(string rootBindingId, string folderId, string corpusId,
        string permissionId = GooglePermissionA, string key = GoogleKeyA) =>
        new(SyncProviders.GoogleDrive, GoogleAccountId(permissionId, key), permissionId, rootBindingId,
            folderId, corpusId, "/Drive/" + rootBindingId);

    private static Scope DropboxScope(string rootBindingId, string folderId, string corpusId) =>
        new(SyncProviders.Dropbox, DropboxProviderAccountId(), DropboxNamespaceId, rootBindingId, folderId, corpusId,
            "/Recovery/" + rootBindingId);

    private static SyncAuthorityCloudHierarchySnapshot GoogleChain(Scope scope)
    {
        var corpusRoot = GoogleCorpusRootId(scope.NamespaceId);
        return GoogleChainFor(scope.RootBindingId, scope.ProviderAccountId, scope.NamespaceId, scope.FolderId,
            corpusRoot, new[] { corpusRoot, scope.FolderId });
    }

    private static SyncAuthorityCloudHierarchySnapshot GoogleChainFor(string rootBindingId,
        string providerAccountId, string namespaceId, string folderId, string corpusRoot,
        IReadOnlyList<string> chain) =>
        new(SyncProviders.GoogleDrive, providerAccountId, namespaceId, rootBindingId, folderId, chain)
        {
            CompleteToCorpusRoot = true,
            CorpusRootId = corpusRoot,
            ChainNodeKeys = chain.Select(id => new SyncAuthorityCloudHierarchyNodeKeys(id,
                new[] { SyncCloudObjectKey.Native(SyncProviders.GoogleDrive, id) })).ToArray(),
            ObjectKeyCoverage = SyncCloudObjectKeyCoverage.NativeOnly
        };

    private static SyncAuthorityCloudHierarchySnapshot DropboxChain(string rootBindingId, string folderId,
        string[] ancestorIds)
    {
        var chain = new List<string> { PilotPath.DropboxNamespaceRootIdentity(DropboxNamespaceId) };
        chain.AddRange(ancestorIds);
        chain.Add(folderId);
        return new SyncAuthorityCloudHierarchySnapshot(SyncProviders.Dropbox, DropboxProviderAccountId(),
            DropboxNamespaceId, rootBindingId, folderId, chain)
        {
            CompleteToCorpusRoot = true,
            CorpusRootId = chain[0],
            ChainNodeKeys = chain.Select(id => new SyncAuthorityCloudHierarchyNodeKeys(id,
                new[] { SyncCloudObjectKey.Native(SyncProviders.Dropbox, id) })).ToArray(),
            ObjectKeyCoverage = SyncCloudObjectKeyCoverage.MountAliasesComplete
        };
    }

    private static async Task<SyncAuthorityDatabase> OpenAuthorityAsync(string caseName)
    {
        var directory = Path.Combine(_root, caseName);
        Directory.CreateDirectory(directory);
        var authority = new SyncAuthorityDatabase(Path.Combine(directory, "syncshark.db"));
        await authority.InitializeAsync(InstallationId, "profile-cloud-recovery-corpus-316", SourceRevision, "{}");
        return authority;
    }

    private static string DatabasePath(string caseName) => Path.Combine(_root, caseName, "syncshark.db");

    private static async Task RegisterGoogleAccountAsync(SyncAuthorityDatabase authority,
        string permissionId = GooglePermissionA, string key = GoogleKeyA) =>
        await authority.RegisterProviderAccountAsync(GoogleAccountId(permissionId, key), InstallationId,
            permissionId, key, key, "Google Drive account", 1, default, SyncProviders.GoogleDrive);

    private static async Task RegisterDropboxAccountAsync(SyncAuthorityDatabase authority) =>
        await authority.RegisterProviderAccountAsync(DropboxProviderAccountId(), InstallationId,
            DropboxAccountId,
            DropboxNamespaceId, DropboxHomeNamespaceId, "Dropbox account", 1);

    private static async Task RegisterScopeAsync(SyncAuthorityDatabase authority, Scope scope,
        params SyncAuthorityCloudHierarchySnapshot[] hierarchies) =>
        await authority.RegisterCloudRecoveryRootAsync(scope.RootBindingId, InstallationId, scope.Provider,
            scope.ProviderAccountId, scope.NamespaceId, scope.FolderId, scope.CorpusId, scope.DisplayPath, 1,
            default, hierarchies);

    private static async Task SeedLegacyRecoveryAsync(string path, string recoveryRootBindingId,
        string providerAccountId, string folderId, string? rootNamespaceId)
    {
        var identity = rootNamespaceId is null
            ? "{\"folderId\":\"" + folderId + "\"}"
            : "{\"folderId\":\"" + folderId + "\",\"rootNamespaceId\":\"" + rootNamespaceId + "\"}";
        await ExecuteAsync(path,
            "INSERT INTO recovery_root_binding(recovery_root_binding_id,installation_id,provider_account_id," +
            " side,display_path,normalized_path_key,identity_json,generation,status,verified_utc)" +
            " VALUES($id,$installation,$provider,'dropbox',$path,$path,$identity,7,'active',$utc);",
            ("$id", recoveryRootBindingId), ("$installation", InstallationId),
            ("$provider", providerAccountId), ("$path", "/Recovery/" + recoveryRootBindingId),
            ("$identity", identity), ("$utc", DurableUtc));
    }

    // Builds a real version-5 predecessor with the same SQL texts the product uses, then fills it with
    // representative local, unbound, Google and Dropbox recovery rows. Nothing about the predecessor is
    // produced by the code that later migrates it.
    private static async Task BuildVersionFivePredecessorAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sql = SyncAuthoritySchema.Sql + SyncAuthoritySchemaSupplement.Sql +
                  SyncAuthoritySchemaMigration3.Sql + SyncAuthoritySchemaMigration4.Sql +
                  SyncAuthoritySchemaMigration5.Sql;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = sql;
            await schema.ExecuteNonQueryAsync();
        }
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText =
                "INSERT INTO installation(installation_id,database_generation,created_utc)" +
                " VALUES($installation,'generation-corpus-316',$utc);" +
                "INSERT INTO capability_profile(capability_profile_id,source_revision,profile_json,created_utc)" +
                " VALUES('profile-cloud-recovery-corpus-316','cloud-recovery-corpus-316','{}',$utc);" +
                "INSERT INTO sync_pair(pair_id,configuration_revision,state,created_utc,pair_json,selected)" +
                " VALUES('pair-corpus-316',1,'paused',$utc,NULL,0);" +
                "INSERT INTO root_binding(root_binding_id,pair_id,side,generation,identity_json,display_path," +
                " state,observed_utc) VALUES('root-pair-corpus-316','pair-corpus-316','dropbox',1," +
                " '{}','/Documents','active',$utc);" +
                "INSERT INTO logical_object(object_id,object_kind,created_utc)" +
                " VALUES('object-corpus-316','file',$utc);" +
                "INSERT INTO object_version(object_version_id,object_id,side,identity_json,content_hash," +
                " size_bytes,observed_utc) VALUES('object-version-corpus-316','object-corpus-316'," +
                " 'dropbox','{}','" + new string('0', 64) + "',12,$utc);" +
                "INSERT INTO provider_account(provider_account_id,installation_id,provider,account_id," +
                " root_namespace_id,home_namespace_id,display_label,binding_revision,verified_utc,status)" +
                " VALUES($google,$installation,'google-drive',$googlePermission,$googleKey,$googleKey," +
                " 'Google Drive',1,$utc,'active');" +
                "INSERT INTO provider_account(provider_account_id,installation_id,provider,account_id," +
                " root_namespace_id,home_namespace_id,display_label,binding_revision,verified_utc,status)" +
                " VALUES($dropbox,$installation,'dropbox',$dropboxAccount,$dropboxNamespace," +
                " $dropboxHome,'Dropbox',1,$utc,'active');" +
                "INSERT INTO recovery_root_binding(recovery_root_binding_id,installation_id," +
                " provider_account_id,side,display_path,normalized_path_key,identity_json,generation,status," +
                " verified_utc) VALUES" +
                " ('recovery-local-316',$installation,NULL,'local','/Local/Recovery','/Local/Recovery'," +
                " '{\"volumeSerial\":\"vs-316\"}',1,'active',$utc)," +
                " ('recovery-unbound-316',$installation,NULL,'dropbox','/Recovery/Unbound'," +
                " '/Recovery/Unbound','{\"folderId\":\"id:unbound\"}',2,'active',$utc)," +
                " ('recovery-google-legacy-316',$installation,$google,'dropbox','/Recovery/Google'," +
                " '/Recovery/Google','{\"folderId\":\"id:google-legacy\"}',3,'active',$utc)," +
                " ('recovery-dropbox-legacy-316',$installation,$dropbox,'dropbox','/Recovery/Dropbox'," +
                " '/Recovery/Dropbox','{\"folderId\":\"id:dropbox-legacy\",\"rootNamespaceId\":\"" +
                DropboxNamespaceId + "\"}',4,'active',$utc);";
            seed.Parameters.AddWithValue("$installation", InstallationId);
            seed.Parameters.AddWithValue("$utc", DurableUtc);
            seed.Parameters.AddWithValue("$google", GoogleAccountId(GooglePermissionA, GoogleKeyA));
            seed.Parameters.AddWithValue("$googlePermission", GooglePermissionA);
            seed.Parameters.AddWithValue("$googleKey", GoogleKeyA);
            seed.Parameters.AddWithValue("$dropbox", DropboxProviderAccountId());
            seed.Parameters.AddWithValue("$dropboxAccount", DropboxAccountId);
            seed.Parameters.AddWithValue("$dropboxNamespace", DropboxNamespaceId);
            seed.Parameters.AddWithValue("$dropboxHome", DropboxHomeNamespaceId);
            await seed.ExecuteNonQueryAsync();
        }
        await using (var receipt = connection.CreateCommand())
        {
            receipt.CommandText = "INSERT INTO schema_migration(schema_version,applied_utc,source_revision)" +
                " VALUES(5,'" + DurableUtc + "','predecessor'); PRAGMA user_version=5;";
            await receipt.ExecuteNonQueryAsync();
        }
    }

    // A canonical, order-stable dump of every retained authority row this packet can affect. The migration
    // receipt table is asserted separately so the preserved-row proof never depends on the migration's own
    // bookkeeping row.
    private static async Task<string> SnapshotAsync(string path)
    {
        var builder = new StringBuilder();
        // The recovery table is compared across the one version that adds a column to it, so it is always
        // projected over exactly its retained columns. Installation also projects its original three
        // columns because later additive admission/lifetime counters are tested by their own migrations;
        // every other table here is compared in full.
        foreach (var (table, projection) in new[]
                 {
                     ("installation", "installation_id,database_generation,created_utc"), ("capability_profile", "*"), ("provider_account", "*"),
                     ("recovery_root_binding",
                         "recovery_root_binding_id,installation_id,provider_account_id,side,display_path," +
                         "normalized_path_key,identity_json,generation,status,verified_utc"),
                     ("sync_pair", "*"), ("root_binding", "*"), ("logical_object", "*"),
                     ("object_version", "*")
                 })
            builder.Append(table).Append('=').Append(await RowSignatureAsync(path, table, projection))
                .Append(';');
        return builder.ToString();
    }

    private static async Task<string> RowSignatureAsync(string path, string table, string projection = "*")
    {
        var rows = new List<string>();
        await using var connection = await OpenReadOnlyAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + projection + " FROM " + table + " ORDER BY rowid;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var cells = new List<string>();
            for (var index = 0; index < reader.FieldCount; index++)
                cells.Add(reader.IsDBNull(index) ? "<null>" : reader.GetValue(index).ToString() ?? "<null>");
            rows.Add(string.Join("|", cells));
        }
        return string.Join("~", rows.OrderBy(value => value, StringComparer.Ordinal));
    }

    private static async Task<string> FileHashAsync(string path) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(string path, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync();
        }
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> NumberAsync(string path, string sql)
    {
        await using var connection = await OpenReadOnlyAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> TextAsync(string path, string sql)
    {
        await using var connection = await OpenReadOnlyAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static void Claim(string id, string description) =>
        Cases.Add(new Dictionary<string, object?> { ["id"] = id, ["description"] = description });

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidDataException("Cloud recovery corpus assertion failed: " + message);
        _assertions++;
    }

    private static async Task<string?> RefusalAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                                          NotSupportedException or SqliteException or InvalidDataException or
                                          IOException)
        {
            return error.GetType().Name + ": " + error.Message;
        }
    }

    private static async Task RejectAsync(Func<Task> action, string message)
    {
        var refusal = await RefusalAsync(action);
        if (refusal is null) throw new InvalidDataException("Expected refusal: " + message);
        Require(true, message);
    }
}
