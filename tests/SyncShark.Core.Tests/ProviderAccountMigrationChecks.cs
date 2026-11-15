using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

// Real isolated SQLite proofs for the provider-qualified authority account migration and the shared
// provider-account identity helper. Every assertion reads a real database this run created under the
// supplied root; nothing here touches an installed database, provider, credential or product state.
internal static class ProviderAccountMigrationChecks
{
    private const string InstallationId = "installation-provider-316";
    private const string DropboxAccountId = "dbid-account-316";
    private const string DropboxRootNamespaceId = "namespace-root-316";
    private const string DropboxHomeNamespaceId = "namespace-home-316";

    // The retained Google authority row deliberately records an account column and a root namespace
    // that are different values, so a mapping that treats them as interchangeable fails.
    private const string GooglePermissionId = "permission-native-316";
    private const string GoogleAccountKey = "account-key-recorded-316";

    // A row whose retained id is the grandfathered Google derivation over its own account and root but
    // whose home namespace differs from its root: home must equal root before a row can be Google.
    private const string SplitRootNamespaceId = "namespace-split-316";
    private const string SplitHomeNamespaceId = "namespace-split-home-316";

    // A row that is labelled "Google Drive" but whose retained id is the Dropbox derivation over its
    // own recorded identity: a label must never classify a row.
    private const string LabelledAccountId = "dbid-labelled-316";
    private const string LabelledNamespaceId = "namespace-labelled-316";

    private const string PairId = "pair-provider-316";
    private const string DropboxRootBindingId = "root-dropbox-316";
    private const string ObjectId = "object-provider-316";
    private const string ObjectVersionId = "object-version-provider-316";
    private const string GoogleRecoveryBindingId = "recovery-google-316";
    private const string DropboxRecoveryBindingId = "recovery-dropbox-316";
    private const string DurableUtc = "2026-09-19T00:00:00.0000000+00:00";

    private static readonly string GoogleRowId =
        LegacyDerivation("provider-account", SyncProviders.GoogleDrive, GooglePermissionId, GoogleAccountKey);
    private static readonly string DropboxRowId = LegacyDerivation("provider-account", DropboxAccountId,
        DropboxRootNamespaceId, DropboxHomeNamespaceId);
    private static readonly string SplitHomeRowId = LegacyDerivation("provider-account",
        SyncProviders.GoogleDrive, GooglePermissionId, SplitRootNamespaceId);
    private static readonly string LabelledRowId = LegacyDerivation("provider-account", LabelledAccountId,
        LabelledNamespaceId, LabelledNamespaceId);

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        // The fresh route runs first so SQLite's provider is initialised before any raw fixture
        // connection is opened, exactly as the established authority checks rely on.
        await FreshCurrentVersionAsync(root);
        await SharedIdentityDerivationsAsync();
        await VersionFourMigrationAsync(root);
        await VersionThreeChainAsync(root);
        await VersionTwoChainAsync(root);
        await InterruptedMigrationRollsBackAsync(root);
        await TamperedRollbackRejectedAsync(root);
        SourceBoundaryAsync();
        Console.WriteLine("PASS provider-account migration checks complete");
    }

    private static async Task FreshCurrentVersionAsync(string root)
    {
        var path = Path.Combine(root, "fresh", "syncshark.db");
        var authority = new SyncAuthorityDatabase(path);
        await authority.InitializeAsync(InstallationId, "profile-provider-316", "provider-316", "{}");
        Require(await NumberAsync(path, "PRAGMA user_version;") == SyncAuthoritySchema.Version,
            "Fresh authority schema is the current version");
        Require(await NumberAsync(path, "SELECT count(*) FROM schema_migration WHERE schema_version=" +
                SyncAuthoritySchema.Version + ";") == 1,
            "Fresh authority database retains its current-version migration receipt");
        // The provider column is no longer constrained to the single literal 'dropbox'. Writing an
        // unregistered literal directly proves the constraint is a real non-empty provider value.
        await ExecuteAsync(path,
            """
            INSERT INTO provider_account(provider_account_id,installation_id,provider,account_id,
                root_namespace_id,home_namespace_id,display_label,binding_revision,verified_utc,status)
            VALUES('fixture-provider-row','installation-provider-316','fixture-provider','a','r','h',
                'Fixture',1,'2026-09-19T00:00:00.0000000+00:00','active');
            """);
        Require(await NumberAsync(path,
                "SELECT count(*) FROM provider_account WHERE provider='fixture-provider';") == 1,
            "The rebuilt provider column accepts a non-Dropbox provider literal");
        await RejectAsync(() => ExecuteAsync(path,
                """
                INSERT INTO provider_account(provider_account_id,installation_id,provider,account_id,
                    root_namespace_id,home_namespace_id,display_label,binding_revision,verified_utc,status)
                VALUES('empty-provider-row','installation-provider-316','','a2','r2','h2',
                    'Fixture',1,'2026-09-19T00:00:00.0000000+00:00','active');
                """),
            "An empty provider literal is refused by the rebuilt provider column");
        Require(await TextAsync(path, "PRAGMA foreign_key_check;") is null,
            "Fresh current-version database has no foreign-key violation");
        Require(string.Equals(await TextAsync(path, "PRAGMA quick_check;"), "ok", StringComparison.Ordinal),
            "Fresh current-version database passes quick_check");
        Require(!File.Exists(authority.Version2RollbackDatabasePath) &&
                !File.Exists(authority.Version3RollbackDatabasePath) &&
                !File.Exists(authority.Version4RollbackDatabasePath),
            "A fresh database creates no migration rollback copy");
    }

    private static Task SharedIdentityDerivationsAsync()
    {
        var dropboxId = SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox,
            DropboxAccountId, DropboxRootNamespaceId, DropboxHomeNamespaceId);
        Require(string.Equals(dropboxId, DropboxRowId, StringComparison.Ordinal),
            "The shared helper reproduces the recorded Dropbox provider-account derivation exactly");
        var googleId = SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.GoogleDrive,
            GooglePermissionId, GoogleAccountKey, GoogleAccountKey);
        Require(string.Equals(googleId, GoogleRowId, StringComparison.Ordinal),
            "The shared helper reproduces the recorded Google provider-account derivation exactly");
        Require(!string.Equals(DropboxRowId, GoogleRowId, StringComparison.Ordinal),
            "The two legacy derivations stay distinct for these deliberately different identities");
        Require(string.Equals(SyncAuthorityProviderAccountIdentity.DeriveId("Dropbox", DropboxAccountId,
                    DropboxRootNamespaceId, DropboxHomeNamespaceId), DropboxRowId, StringComparison.Ordinal),
            "A registered provider literal resolves through the registry rather than a caller branch");
        Reject(() => SyncAuthorityProviderAccountIdentity.DeriveId("not-a-registered-provider",
            DropboxAccountId, DropboxRootNamespaceId, DropboxHomeNamespaceId),
            "An unregistered provider is refused by the shared helper");
        Reject(() => SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Local,
            DropboxAccountId, DropboxRootNamespaceId, DropboxHomeNamespaceId),
            "A non-cloud provider kind is refused by the shared helper");
        Reject(() => SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, "",
            DropboxRootNamespaceId, DropboxHomeNamespaceId),
            "A missing identity part is refused by the shared helper");

        Require(string.Equals(SyncAuthorityProviderAccountIdentity.RetainedEndpointNamespace(
                    SyncProviders.Dropbox, DropboxAccountId, DropboxRootNamespaceId, DropboxHomeNamespaceId),
                DropboxRootNamespaceId, StringComparison.Ordinal),
            "A Dropbox endpoint namespace must match the retained root namespace");
        Require(string.Equals(SyncAuthorityProviderAccountIdentity.RetainedEndpointNamespace(
                    SyncProviders.GoogleDrive, GooglePermissionId, GoogleAccountKey, GoogleAccountKey),
                GooglePermissionId, StringComparison.Ordinal),
            "A Google endpoint namespace must match the retained native account id, not the account key");
        Require(!SyncAuthorityProviderAccountIdentity.MatchesEndpointNamespace(SyncProviders.GoogleDrive,
                GooglePermissionId, GoogleAccountKey, GoogleAccountKey, GoogleAccountKey),
            "The retained Google root namespace (the account key) is refused as an endpoint namespace");
        Require(SyncAuthorityProviderAccountIdentity.MatchesEndpointNamespace(SyncProviders.Dropbox,
                DropboxAccountId, DropboxRootNamespaceId, DropboxHomeNamespaceId, DropboxRootNamespaceId),
            "The exact Dropbox retained namespace is accepted");
        Require(!SyncAuthorityProviderAccountIdentity.MatchesEndpointNamespace(SyncProviders.Dropbox,
                DropboxAccountId, DropboxRootNamespaceId, DropboxHomeNamespaceId, DropboxHomeNamespaceId) &&
                !SyncAuthorityProviderAccountIdentity.MatchesEndpointNamespace(SyncProviders.Dropbox,
                    DropboxAccountId, DropboxRootNamespaceId, DropboxHomeNamespaceId, "*") &&
                !SyncAuthorityProviderAccountIdentity.MatchesEndpointNamespace(SyncProviders.Dropbox,
                    DropboxAccountId, DropboxRootNamespaceId, DropboxHomeNamespaceId, "Dropbox") &&
                !SyncAuthorityProviderAccountIdentity.MatchesEndpointNamespace(SyncProviders.Dropbox,
                    DropboxAccountId, DropboxRootNamespaceId, DropboxHomeNamespaceId, null),
            "A wrong, wildcard, display-name or absent endpoint namespace is refused");
        Reject(() => SyncAuthorityProviderAccountIdentity.RequireEndpointNamespace(
                SyncProviders.GoogleDrive, GooglePermissionId, GoogleAccountKey, GoogleAccountKey,
                GoogleAccountKey),
            "The refusing form rejects a Google endpoint that presents the account key");
        Require(string.Equals(SyncAuthorityProviderAccountIdentity.RequireEndpointNamespace(
                    SyncProviders.GoogleDrive, GooglePermissionId, GoogleAccountKey, GoogleAccountKey,
                    GooglePermissionId),
                GooglePermissionId, StringComparison.Ordinal),
            "The refusing form returns the retained namespace for the exact Google endpoint");
        Reject(() => SyncAuthorityProviderAccountIdentity.RequireRetainedEndpointMatches(
                SyncProviders.GoogleDrive, DropboxRowId, GooglePermissionId, GoogleAccountKey,
                GoogleAccountKey, GooglePermissionId),
            "A retained id that is another provider's derivation over the same tuple is refused");
        SyncAuthorityProviderAccountIdentity.RequireRetainedEndpointMatches(SyncProviders.GoogleDrive,
            GoogleRowId, GooglePermissionId, GoogleAccountKey, GoogleAccountKey, GooglePermissionId);
        Require(true,
            "The complete Google provider, account and endpoint tuple validates against its derivation");
        Require(SyncAuthorityProviderAccountIdentity.IsLegacyGoogleProviderAccount(GoogleRowId,
                GooglePermissionId, GoogleAccountKey, GoogleAccountKey) &&
                !SyncAuthorityProviderAccountIdentity.IsLegacyGoogleProviderAccount(GoogleRowId,
                    GooglePermissionId, SplitRootNamespaceId, SplitHomeNamespaceId) &&
                !SyncAuthorityProviderAccountIdentity.IsLegacyGoogleProviderAccount(LabelledRowId,
                    LabelledAccountId, LabelledNamespaceId, LabelledNamespaceId),
            "The legacy Google classification uses only the exact derivation over the row's own tuple");
        return Task.CompletedTask;
    }

    private static async Task VersionFourMigrationAsync(string root)
    {
        var path = Path.Combine(root, "from-v4", "syncshark.db");
        await BuildPredecessorAsync(path, 4);
        var before = await SnapshotAsync(path);
        var authority = new SyncAuthorityDatabase(path);
        await authority.InitializeAsync(InstallationId, "profile-provider-316", "provider-316", "{}");
        Require(await NumberAsync(path, "PRAGMA user_version;") == SyncAuthoritySchema.Version,
            "A version-4 predecessor migrates to the current version");
        Require(await NumberAsync(path, "SELECT count(*) FROM schema_migration WHERE schema_version=" +
                    SyncAuthoritySchema.Version + ";") == 1 &&
                await NumberAsync(path,
                    "SELECT count(*) FROM schema_migration WHERE schema_version=4;") == 1,
            "The explicit version-4 receipt is retained and exactly one current-version receipt is added");
        Require(File.Exists(authority.Version4RollbackDatabasePath) &&
                await NumberAsync(authority.Version4RollbackDatabasePath, "PRAGMA user_version;") == 4 &&
                await NumberAsync(authority.Version4RollbackDatabasePath,
                    "SELECT count(*) FROM provider_account WHERE provider='dropbox';") == 4 &&
                await NumberAsync(authority.Version4RollbackDatabasePath,
                    "SELECT count(*) FROM recovery_root_binding;") == 2,
            "The exact version-4 rollback copy is retained with every pre-migration provider and recovery row");
        Require(await SnapshotAsync(path) == before,
            "Every pre-existing row identity and value is preserved by the version-4 predecessor migration");
        Require(await NumberAsync(path, "SELECT count(*) FROM recovery_root_binding;") == 2 &&
                await NumberAsync(path,
                    "SELECT count(*) FROM recovery_root_binding WHERE provider_corpus_id IS NULL;") == 2,
            "The additive recovery corpus column is NULL for every retained recovery row");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM provider_account WHERE provider='dropbox';") == 3 &&
                await NumberAsync(path,
                    "SELECT count(*) FROM provider_account WHERE provider='google-drive';") == 1,
            "Exactly one provider literal changed, and only the exact legacy Google row changed it");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM provider_account WHERE provider_account_id='" + GoogleRowId +
                "' AND provider='google-drive';") == 1,
            "The exact legacy Google row is reclassified to its real provider");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM provider_account WHERE provider_account_id='" + DropboxRowId +
                "' AND provider='dropbox';") == 1 &&
                await NumberAsync(path,
                    "SELECT count(*) FROM provider_account WHERE provider_account_id='" + LabelledRowId +
                    "' AND provider='dropbox';") == 1 &&
                await NumberAsync(path,
                    "SELECT count(*) FROM provider_account WHERE provider_account_id='" + SplitHomeRowId +
                    "' AND provider='dropbox';") == 1,
            "A Dropbox row, a Google-labelled row and a home-differs row all retain their Dropbox meaning");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id='" +
                GoogleRecoveryBindingId + "' AND provider_account_id='" + GoogleRowId +
                "' AND generation=1 AND status='active';") == 1 &&
                await NumberAsync(path,
                    "SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id='" +
                    DropboxRecoveryBindingId + "' AND provider_account_id='" + DropboxRowId +
                    "' AND generation=2 AND normalized_path_key='/Recovery/Dropbox' AND status='active';") == 1,
            "Both retained recovery bindings keep their account reference, generation and values");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM root_binding WHERE root_binding_id='" + DropboxRootBindingId +
                "' AND state='active';") == 1 &&
                await NumberAsync(path, "SELECT count(*) FROM object_version WHERE object_version_id='" +
                    ObjectVersionId + "';") == 1,
            "The retained pair root binding and object history survive the migration");
        Require(await TextAsync(path, "PRAGMA foreign_key_check;") is null &&
                string.Equals(await TextAsync(path, "PRAGMA quick_check;"), "ok", StringComparison.Ordinal),
            "The migrated current-version database passes foreign-key and quick checks");
        await RejectAsync(() => ExecuteAsync(path,
                """
                INSERT INTO recovery_root_binding(recovery_root_binding_id,installation_id,
                    provider_account_id,side,display_path,normalized_path_key,identity_json,generation,
                    status,verified_utc)
                VALUES('recovery-orphan','installation-provider-316','absent-provider-account','dropbox',
                    '/Orphan','/Orphan','{"folderId":"id:orphan"}',3,'active',
                    '2026-09-19T00:00:00.0000000+00:00');
                """),
            "The rebuilt recovery table still enforces its provider-account foreign key");

        await authority.RegisterProviderAccountAsync("coexist-dropbox-316", InstallationId,
            "shared-account-316", "shared-namespace-316", "shared-namespace-316", "Shared", 1);
        await authority.RegisterProviderAccountAsync("coexist-google-316", InstallationId,
            "shared-account-316", "shared-namespace-316", "shared-namespace-316", "Shared", 1,
            default, SyncProviders.GoogleDrive);
        Require(await NumberAsync(path,
                "SELECT count(*) FROM provider_account WHERE account_id='shared-account-316' AND root_namespace_id='shared-namespace-316';") == 2,
            "Equal raw account and namespace strings coexist under two different real providers");
        await RejectAsync(() => authority.RegisterProviderAccountAsync("coexist-google-316",
                InstallationId, "shared-account-316", "shared-namespace-316", "shared-namespace-316",
                "Shared", 1),
            "A forged reclassification of a Google row back to Dropbox is refused");
        await RejectAsync(() => authority.RegisterProviderAccountAsync("coexist-google-316",
                InstallationId, "drifted-account-316", "shared-namespace-316", "shared-namespace-316",
                "Shared", 1, default, SyncProviders.GoogleDrive),
            "Account drift on a retained provider account is refused");
        await RejectAsync(() => authority.RegisterProviderAccountAsync("coexist-google-316",
                "installation-other-316", "shared-account-316", "shared-namespace-316",
                "shared-namespace-316", "Shared", 1, default, SyncProviders.GoogleDrive),
            "Installation drift on a retained provider account is refused");
        await RejectAsync(() => authority.RegisterProviderAccountAsync("coexist-new-316",
                InstallationId, "shared-account-316", "shared-namespace-316", "shared-namespace-316",
                "Shared", 1),
            "A duplicate Dropbox tuple is still refused by the provider-scoped unique constraint");
        await RejectAsync(() => authority.RegisterProviderAccountAsync("unregistered-316",
                InstallationId, "unregistered-account-316", "unregistered-namespace-316",
                "unregistered-namespace-316", "Unregistered", 1, default, "not-a-registered-provider"),
            "An unknown provider is refused by provider registration");
        Require(await NumberAsync(path,
                "SELECT count(*) FROM provider_account WHERE provider_account_id='coexist-google-316' AND provider='google-drive' AND account_id='shared-account-316' AND binding_revision=1;") == 1 &&
                await NumberAsync(path,
                    "SELECT count(*) FROM provider_account WHERE provider_account_id='unregistered-316';") == 0 &&
                await NumberAsync(path,
                    "SELECT count(*) FROM provider_account WHERE provider_account_id='coexist-new-316';") == 0,
            "Every rejected registration left its retained row and the database unmutated");
    }

    private static async Task VersionThreeChainAsync(string root)
    {
        var path = Path.Combine(root, "from-v3", "syncshark.db");
        await BuildPredecessorAsync(path, 3);
        var before = await SnapshotAsync(path);
        var authority = new SyncAuthorityDatabase(path);
        await authority.InitializeAsync(InstallationId, "profile-provider-316", "provider-316", "{}");
        Require(await NumberAsync(path, "PRAGMA user_version;") == SyncAuthoritySchema.Version,
            "A version-3 predecessor migrates through version 4 to the current version");
        Require(File.Exists(authority.Version3RollbackDatabasePath) &&
                await NumberAsync(authority.Version3RollbackDatabasePath, "PRAGMA user_version;") == 3 &&
                !File.Exists(authority.Version4RollbackDatabasePath),
            "The version-3 predecessor retains exactly its version-3 rollback copy");
        Require(await SnapshotAsync(path) == before &&
                await NumberAsync(path,
                    "SELECT count(*) FROM provider_account WHERE provider_account_id='" + GoogleRowId +
                    "' AND provider='google-drive';") == 1,
            "The chained version-3 migration preserves every row and classifies the legacy Google row");
    }

    private static async Task VersionTwoChainAsync(string root)
    {
        var path = Path.Combine(root, "from-v2", "syncshark.db");
        await BuildPredecessorAsync(path, 2);
        var before = await SnapshotAsync(path);
        var authority = new SyncAuthorityDatabase(path);
        await authority.InitializeAsync(InstallationId, "profile-provider-316", "provider-316", "{}");
        Require(await NumberAsync(path, "PRAGMA user_version;") == SyncAuthoritySchema.Version,
            "A version-2 predecessor migrates through the supported chain to the current version");
        Require(File.Exists(authority.Version2RollbackDatabasePath) &&
                await NumberAsync(authority.Version2RollbackDatabasePath, "PRAGMA user_version;") == 2 &&
                !File.Exists(authority.Version3RollbackDatabasePath) &&
                !File.Exists(authority.Version4RollbackDatabasePath),
            "The version-2 predecessor retains exactly its version-2 rollback copy");
        Require(await SnapshotAsync(path) == before &&
                await NumberAsync(path,
                    "SELECT count(*) FROM provider_account WHERE provider_account_id='" + GoogleRowId +
                    "' AND provider='google-drive';") == 1 &&
                await NumberAsync(path, "SELECT count(*) FROM upload_session_checkpoint;") == 0 &&
                await NumberAsync(path, "SELECT count(*) FROM sync_path_index;") == 0,
            "The chained version-2 migration preserves every row, classifies Google and adds only the additive layers");
    }

    private static async Task InterruptedMigrationRollsBackAsync(string root)
    {
        var path = Path.Combine(root, "interrupted", "syncshark.db");
        await BuildPredecessorAsync(path, 4);
        var before = await SnapshotAsync(path);
        var authority = new SyncAuthorityDatabase(path);
        var interrupted = false;
        SyncAuthorityDatabase.MigrationBoundaryForTest = observed =>
        {
            if (!string.Equals(observed, "v5-schema-applied-before-version-commit", StringComparison.Ordinal))
                return;
            interrupted = true;
            throw new IOException("Injected version-5 migration interruption");
        };
        try
        {
            await authority.InitializeAsync(InstallationId, "profile-provider-316", "provider-316", "{}");
            throw new InvalidOperationException("The injected version-5 interruption did not fire.");
        }
        catch (IOException error) when (error.Message == "Injected version-5 migration interruption")
        {
        }
        finally
        {
            SyncAuthorityDatabase.MigrationBoundaryForTest = null;
        }
        Require(interrupted, "The predecessor-to-current pre-commit boundary is reachable and interruptible");
        Require(await NumberAsync(path, "PRAGMA user_version;") == 4 && await SnapshotAsync(path) == before,
            "An interrupted predecessor-to-current migration exposes no partially upgraded database");
        await RejectAsync(() => ExecuteAsync(path,
                """
                INSERT INTO provider_account(provider_account_id,installation_id,provider,account_id,
                    root_namespace_id,home_namespace_id,display_label,binding_revision,verified_utc,status)
                VALUES('after-rollback-row','installation-provider-316','google-drive','a3','r3','h3',
                    'After rollback',1,'2026-09-19T00:00:00.0000000+00:00','active');
                """),
            "The rolled-back predecessor still constrains the provider column to its recorded literal");
        Require(File.Exists(authority.Version4RollbackDatabasePath) &&
                await NumberAsync(authority.Version4RollbackDatabasePath, "PRAGMA user_version;") == 4,
            "The interrupted attempt retained a verified version-4 rollback copy before migrating");
        var rollbackHash = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(authority.Version4RollbackDatabasePath)));
        await authority.InitializeAsync(InstallationId, "profile-provider-316", "provider-316", "{}");
        Require(await NumberAsync(path, "PRAGMA user_version;") == SyncAuthoritySchema.Version &&
                await NumberAsync(path,
                    "SELECT count(*) FROM provider_account WHERE provider_account_id='" + GoogleRowId +
                    "' AND provider='google-drive';") == 1 &&
                rollbackHash == Convert.ToHexString(SHA256.HashData(
                    await File.ReadAllBytesAsync(authority.Version4RollbackDatabasePath))),
            "The retried migration converges and leaves the verified version-4 rollback copy untouched");
    }

    private static async Task TamperedRollbackRejectedAsync(string root)
    {
        var path = Path.Combine(root, "tampered", "syncshark.db");
        await BuildPredecessorAsync(path, 4);
        var before = await SnapshotAsync(path);
        var authority = new SyncAuthorityDatabase(path);
        SyncAuthorityDatabase.MigrationBoundaryForTest = observed =>
        {
            if (string.Equals(observed, "v5-schema-applied-before-version-commit", StringComparison.Ordinal))
                throw new IOException("Injected version-5 migration interruption");
        };
        try
        {
            await authority.InitializeAsync(InstallationId, "profile-provider-316", "provider-316", "{}");
        }
        catch (IOException)
        {
        }
        finally
        {
            SyncAuthorityDatabase.MigrationBoundaryForTest = null;
        }
        Require(File.Exists(authority.Version4RollbackDatabasePath),
            "The interrupted attempt retained its version-4 rollback copy for tamper testing");
        await ExecuteAsync(authority.Version4RollbackDatabasePath,
            "UPDATE provider_account SET display_label='Tampered' WHERE provider_account_id='" +
            GoogleRowId + "';");
        var tamperedHash = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(authority.Version4RollbackDatabasePath)));
        await RejectAsync(() => authority.InitializeAsync(InstallationId, "profile-provider-316",
                "provider-316", "{}"),
            "A tampered version-4 rollback copy is refused before migration");
        Require(await NumberAsync(path, "PRAGMA user_version;") == 4 && await SnapshotAsync(path) == before &&
                tamperedHash == Convert.ToHexString(SHA256.HashData(
                    await File.ReadAllBytesAsync(authority.Version4RollbackDatabasePath))),
            "The refused tampered rollback leaves the user database and the rollback bytes unchanged");
    }

    private static void SourceBoundaryAsync()
    {
        var repositoryRoot = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (repositoryRoot is not null &&
               !File.Exists(Path.Combine(repositoryRoot.FullName, "SyncShark.sln")))
            repositoryRoot = repositoryRoot.Parent;
        if (repositoryRoot is null)
            throw new InvalidOperationException("Run the provider-account checks from the repository tree.");
        var host = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "SyncShark.Core",
            "SyncAuthorityRuntimeHost.cs"));
        Require(host.Contains("SyncAuthorityProviderAccountIdentity.DeriveId", StringComparison.Ordinal) &&
                !host.Contains("Id(\"provider-account\"", StringComparison.Ordinal),
            "Both host provider-account derivations come from the shared helper, not a local branch");
        var migration = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "SyncShark.Core",
            "SyncAuthoritySchemaMigration5.cs"));
        Require(migration.Contains("one_active_recovery_root_per_side", StringComparison.Ordinal) &&
                migration.Contains("recovery_root_generation_per_account", StringComparison.Ordinal) &&
                migration.Contains("provider_account_provider_qualified", StringComparison.Ordinal) &&
                migration.Contains("DROP TABLE recovery_root_binding;", StringComparison.Ordinal) &&
                migration.Contains("DROP TABLE provider_account;", StringComparison.Ordinal),
            "The version-5 migration rebuilds both tables and recreates the same recovery indices");
    }

    // Builds a real predecessor database at the requested supported version using the same SQL texts
    // the product uses, then fills it with representative provider, recovery, pair, root and history
    // rows. Nothing about the predecessor is produced by the code that later migrates it.
    private static async Task BuildPredecessorAsync(string path, int version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sql = SyncAuthoritySchema.Sql + SyncAuthoritySchemaSupplement.Sql +
                  (version >= 3 ? SyncAuthoritySchemaMigration3.Sql : string.Empty) +
                  (version >= 4 ? SyncAuthoritySchemaMigration4.Sql : string.Empty);
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
                " VALUES('installation-provider-316','generation-provider-316','2026-09-19T00:00:00.0000000+00:00');" +
                "INSERT INTO capability_profile(capability_profile_id,source_revision,profile_json,created_utc)" +
                " VALUES('profile-provider-316','provider-316','{}','2026-09-19T00:00:00.0000000+00:00');" +
                "INSERT INTO sync_pair(pair_id,configuration_revision,state,created_utc,pair_json,selected)" +
                " VALUES('pair-provider-316',1,'paused','2026-09-19T00:00:00.0000000+00:00',NULL,0);" +
                "INSERT INTO root_binding(root_binding_id,pair_id,side,generation,identity_json,display_path,state,observed_utc)" +
                " VALUES('root-dropbox-316','pair-provider-316','dropbox',1,'{}','/Documents','active'," +
                "'2026-09-19T00:00:00.0000000+00:00');" +
                "INSERT INTO logical_object(object_id,object_kind,created_utc)" +
                " VALUES('object-provider-316','file','2026-09-19T00:00:00.0000000+00:00');" +
                "INSERT INTO object_version(object_version_id,object_id,side,identity_json,content_hash,size_bytes,observed_utc)" +
                " VALUES('object-version-provider-316','object-provider-316','dropbox','{}'," +
                "'0000000000000000000000000000000000000000000000000000000000000000',12," +
                "'2026-09-19T00:00:00.0000000+00:00');";
            await seed.ExecuteNonQueryAsync();
        }
        await using (var providers = connection.CreateCommand())
        {
            providers.CommandText =
                "INSERT INTO provider_account(provider_account_id,installation_id,provider,account_id," +
                " root_namespace_id,home_namespace_id,display_label,binding_revision,verified_utc,status)" +
                " VALUES($googleRow,'installation-provider-316','dropbox',$permission,$key,$key," +
                " 'Google Drive',1,'2026-09-19T00:00:00.0000000+00:00','active')," +
                " ($dropboxRow,'installation-provider-316','dropbox',$account,$root,$home," +
                " 'Dropbox',1,'2026-09-19T00:00:00.0000000+00:00','active')," +
                " ($labelledRow,'installation-provider-316','dropbox',$labelledAccount,$labelledNamespace," +
                " $labelledNamespace,'Google Drive',1,'2026-09-19T00:00:00.0000000+00:00','active')," +
                " ($splitHomeRow,'installation-provider-316','dropbox',$permission,$splitRoot," +
                " $splitHome,'Google Drive',1,'2026-09-19T00:00:00.0000000+00:00','active');";
            foreach (var (name, value) in new[]
                     {
                         ("$googleRow", GoogleRowId), ("$permission", GooglePermissionId),
                         ("$key", GoogleAccountKey), ("$dropboxRow", DropboxRowId),
                         ("$account", DropboxAccountId), ("$root", DropboxRootNamespaceId),
                         ("$home", DropboxHomeNamespaceId), ("$labelledRow", LabelledRowId),
                         ("$labelledAccount", LabelledAccountId), ("$labelledNamespace", LabelledNamespaceId),
                         ("$splitHomeRow", SplitHomeRowId), ("$splitRoot", SplitRootNamespaceId),
                         ("$splitHome", SplitHomeNamespaceId)
                     })
                providers.Parameters.AddWithValue(name, value);
            await providers.ExecuteNonQueryAsync();
        }
        await using (var recovery = connection.CreateCommand())
        {
            recovery.CommandText =
                "INSERT INTO recovery_root_binding(recovery_root_binding_id,installation_id," +
                " provider_account_id,side,display_path,normalized_path_key,identity_json,generation," +
                " status,verified_utc)" +
                " VALUES('recovery-google-316','installation-provider-316',$googleRow,'dropbox'," +
                " '/Recovery/Google','/Recovery/Google','{}',1,'active'," +
                " '2026-09-19T00:00:00.0000000+00:00')" +
                // The version-2 and version-3 recovery table still allows one active recovery root per
                // side, so the second binding is added only for the version-4 predecessor, whose
                // provider-scoped index can hold an active root for each account.
                (version >= 4
                    ? ",\n ('recovery-dropbox-316','installation-provider-316',$dropboxRow,'dropbox'," +
                      " '/Recovery/Dropbox','/Recovery/Dropbox','{}',2,'active'," +
                      " '2026-09-19T00:00:00.0000000+00:00')"
                    : string.Empty) + ";";
            recovery.Parameters.AddWithValue("$googleRow", GoogleRowId);
            recovery.Parameters.AddWithValue("$dropboxRow", DropboxRowId);
            await recovery.ExecuteNonQueryAsync();
        }
        await using (var receipt = connection.CreateCommand())
        {
            receipt.CommandText =
                "INSERT INTO schema_migration(schema_version,applied_utc,source_revision)" +
                " VALUES($version,'2026-09-19T00:00:00.0000000+00:00','predecessor');" +
                "PRAGMA user_version=" + version + ";";
            receipt.Parameters.AddWithValue("$version", version);
            await receipt.ExecuteNonQueryAsync();
        }
    }

    private static string LegacyDerivation(string role, params string[] parts) => role + "-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant()[..32];

    // A canonical, order-stable dump of every retained authority row this packet can affect. The
    // migration receipt table is asserted separately so the preserved-row proof never depends on the
    // migration's own bookkeeping row.
    private static async Task<string> SnapshotAsync(string path)
    {
        var builder = new StringBuilder();
        foreach (var table in new[]
                 {
                     "installation", "capability_profile",
                     "sync_pair", "root_binding", "logical_object", "object_version"
                 })
            builder.Append(table).Append('=').Append(await RowSignatureAsync(path, table)).Append(';');
        // recovery_root_binding is compared over exactly the columns every supported predecessor already
        // carried, because a later additive migration appends one nullable column to it. That appended
        // column is asserted separately at its own migration, so the preserved-row proof still covers
        // every pre-existing value of every retained recovery row.
        builder.Append("recovery_root_binding=").Append(await RowSignatureAsync(path, "recovery_root_binding",
            "recovery_root_binding_id,installation_id,provider_account_id,side,display_path," +
            "normalized_path_key,identity_json,generation,status,verified_utc")).Append(';');
        // provider_account is compared by every column except the provider literal, because a truthful
        // provider is the one intended correction this migration makes; the literals are asserted
        // separately so the preserved-row proof still covers every other value of every row.
        builder.Append("provider_identity=").Append(await RowSignatureAsync(path, "provider_account",
            "provider_account_id,installation_id,account_id,root_namespace_id,home_namespace_id," +
            "display_label,binding_revision,verified_utc,status")).Append(';');
        return builder.ToString();
    }

    private static async Task<string> RowSignatureAsync(string path, string table,
        string projection = "*")
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

    private static async Task ExecuteAsync(string path, string sql)
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

    private static void Require(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private static void Reject(Action action, string label)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                                          NotSupportedException or SqliteException or InvalidDataException)
        {
            Console.WriteLine("PASS " + label);
            return;
        }
        throw new InvalidOperationException("Expected refusal: " + label);
    }

    private static async Task RejectAsync(Func<Task> action, string label)
    {
        try
        {
            await action();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                                          NotSupportedException or SqliteException or InvalidDataException)
        {
            Console.WriteLine("PASS " + label);
            return;
        }
        throw new InvalidOperationException("Expected refusal: " + label);
    }
}
