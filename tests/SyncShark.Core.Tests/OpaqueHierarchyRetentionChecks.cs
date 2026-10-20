using System.Globalization;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// D2 focused proof for the retained opaque hierarchy observation. It uses a tiny real authority database with a
// real retained provider account and a real active cloud root binding; no provider, network, live state or
// fixture-external effect is touched. It proves the internal-only retention path, the coherent read-back
// context, idempotent replay, the no-write read and the forged and stale refusals.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class OpaqueHierarchyRetentionChecks
{
    private const string Installation = "d2-hierarchy-fixture";
    private const string Profile = "profile-d2-hierarchy";
    private const string Provider = SyncProviders.Dropbox;
    private const string Account = "account-d2-hierarchy";
    private const string Namespace = "ns-d2-hierarchy";
    private const string Pair = "pair-d2-hierarchy";
    private const string Root = "root-d2-hierarchy";
    private static int _assertions;

    internal static async Task RunAsync(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Use an empty isolated D2 hierarchy fixture root.");
        Directory.CreateDirectory(root);
        _assertions = 0;
        var db = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        await db.InitializeAsync(Installation, Profile, "d2-hierarchy", "{}");
        await db.RegisterProviderAccountAsync(Account, Installation, "id:" + Account, Namespace,
            Namespace + "-home", "BIND1 d2", 1, cancellationToken: default, provider: Provider);
        await db.RegisterPairAsync(Pair, 1);
        const string folder = "id:d2-folder";
        await db.RegisterRootBindingAsync(Root, Pair, "dropbox", 1,
            SyncAuthorityCloudRootPersistence.Serialize(Provider, Account, Namespace, folder),
            "/D2/Child");
        var snapshot = Snapshot(folder);

        var before = await CountAsync(db.DatabasePath);
        var eventId = await db.RetainOpaqueHierarchyObservationAsync(snapshot, Pair, "dropbox", 1);
        Assert(!string.IsNullOrWhiteSpace(eventId), "the internal retention path appends one observation");
        var afterRetain = await CountAsync(db.DatabasePath);
        Assert(afterRetain == before + 1, "retention appends exactly one history row");

        var resolved = await db.ResolveRetainedOpaqueHierarchyAsync(Pair, "dropbox");
        Assert(resolved is not null, "the retained observation resolves for the exact current binding");
        Assert(resolved!.RootBindingId == Root && resolved.Snapshot.FolderId == folder &&
               resolved.Snapshot.CompleteToCorpusRoot && resolved.Snapshot.ChainFolderIds.Count == 3,
            "the resolved observation carries the exact retained binding and complete chain");
        Assert(await CountAsync(db.DatabasePath) == afterRetain,
            "a read-back resolve appends nothing");

        var replay = await db.RetainOpaqueHierarchyObservationAsync(snapshot, Pair, "dropbox", 1);
        Assert(replay == eventId && await CountAsync(db.DatabasePath) == afterRetain,
            "an exact repeat is idempotent and appends nothing");

        await RefusesAsync(() => db.RetainOpaqueHierarchyObservationAsync(Snapshot("id:foreign-folder"), Pair,
            "dropbox", 1), "a forged hierarchy does not bind to the current active root");
        Assert(await CountAsync(db.DatabasePath) == afterRetain, "a forged retention appends nothing");

        // D2 (c): the observation is retained for the exact current root in either the ACTIVE or the BLOCKED
        // candidate preimage state, but the ordinary readback resolves it only while the root is genuinely
        // ACTIVE. The publication-scoped core - the only consumer that may admit a prepared candidate blocked
        // preimage - resolves it there too. This is the partial-safe unavailable-evidence behaviour: a side
        // without a usable bound observation yields null rather than a guessed or half-bound context.
        await ExecuteAsync(db.DatabasePath,
            "UPDATE root_binding SET state='blocked' WHERE root_binding_id='" + Root + "';");
        Assert(await db.ResolveRetainedOpaqueHierarchyAsync(Pair, "dropbox") is null,
            "an ordinary readback refuses a blocked candidate preimage observation");
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = db.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var scopedTransaction = connection.BeginTransaction();
            var scoped = await SyncAuthorityDatabase.ResolveRetainedOpaqueHierarchyCoreAsync(connection,
                scopedTransaction, Installation, Pair, "dropbox", default, allowBlockedCandidateRoot: true);
            Assert(scoped is not null && scoped!.Snapshot.FolderId == folder,
                "the publication-scoped resolver admits the exact prepared blocked candidate preimage");
            var activeOnly = await SyncAuthorityDatabase.ResolveRetainedOpaqueHierarchyCoreAsync(connection,
                scopedTransaction, Installation, Pair, "dropbox", default);
            Assert(activeOnly is null, "the same core without the candidate scope stays active-only");
        }
        // An unrelated pair resolves nothing: the retained observation is bound to its own pair, side, root
        // and generation and never leaks across them.
        Assert(await db.ResolveRetainedOpaqueHierarchyAsync("pair-d2-unrelated", "dropbox") is null,
            "an unrelated pair resolves no retained observation");
        await ExecuteAsync(db.DatabasePath,
            "UPDATE root_binding SET state='active' WHERE root_binding_id='" + Root + "';");
        Assert(await db.ResolveRetainedOpaqueHierarchyAsync(Pair, "dropbox") is not null,
            "restoring the exact active root restores the retained observation");

        await ExecuteAsync(db.DatabasePath, "UPDATE root_binding SET generation=2 WHERE root_binding_id='" +
            Root + "';");
        Assert(await db.ResolveRetainedOpaqueHierarchyAsync(Pair, "dropbox") is null,
            "a changed root generation makes the retained observation stale and it refuses");

        Console.WriteLine("PASS opaque hierarchy retention: " + _assertions + " assertions; real authority rows, no provider.");
    }

    private static SyncAuthorityCloudHierarchySnapshot Snapshot(string folderId)
    {
        var corpus = PilotPath.DropboxNamespaceRootIdentity(Namespace);
        var chain = new List<string> { corpus, "id:d2-parent", folderId };
        return new SyncAuthorityCloudHierarchySnapshot(Provider, Account, Namespace, Root, folderId, chain)
        {
            CompleteToCorpusRoot = true,
            CorpusRootId = corpus,
            ChainNodeKeys = chain.Select(id => new SyncAuthorityCloudHierarchyNodeKeys(id,
                new[] { SyncCloudObjectKey.Native(Provider, id) })).ToArray(),
            ObjectKeyCoverage = SyncCloudObjectKeyCoverage.MountAliasesComplete
        };
    }

    private static async Task RefusesAsync(Func<Task> action, string label)
    {
        try
        {
            await action();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException)
        {
            Assert(!string.IsNullOrWhiteSpace(error.Message), label);
            return;
        }
        throw new InvalidDataException("Expected a refusal: " + label);
    }

    private static void Assert(bool value, string label)
    {
        _assertions++;
        if (!value) throw new InvalidDataException("Opaque hierarchy retention: " + label);
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM history_event WHERE event_kind='opaque_hierarchy_retained';";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
