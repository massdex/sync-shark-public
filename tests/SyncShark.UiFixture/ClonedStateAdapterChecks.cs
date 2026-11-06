using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

namespace SyncShark.UiFixture;

/// <summary>
/// R682: the ONE test-only cloned-state host adapter. It reuses the real product parser, registry,
/// operations, errors and database logic over an explicitly supplied clone of the current app state.
/// Production bytes are unchanged: this class lives only in the UiFixture test assembly, binds its own
/// clone lock/state authority and explicit clone database, denies all network, and uses credential
/// stores that refuse every read and write. It never calls CliEntry.Execute, so the live IPC bridge is
/// unreachable, and it never falls back to the live windows product root.
/// </summary>
internal sealed class ClonedStateAdapter : IAsyncDisposable
{
    // R697: generated fixture output belongs in the RELOCATED owner build allocation on F. The legacy
    // LocalAppData allocation no longer exists after the migration, so the old path was a dead end for this
    // test-only lane. Test fixture constant only; no product behaviour depends on it.
    internal static string OwnerAllocation => Path.Combine(DiagnosticPaths.RequireRoot(), "build");

    // Exact same retained live-provider stage on the existing owner-local SSD allocation.
    // No other fixture destination is admitted outside the ordinary F allocation.
    internal static string RealStagedSsdRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestData", "Projects",
        "sync-shark", "build", "root682-cloned-state-live", "real-provider-state");

    // Same retained stage continued on the owner volume with measured capacity. The SSD
    // snapshot remains rollback; neither older snapshot may run once this continuation exists.
    internal static string RealStagedCapacityRoot => Path.Combine(OwnerAllocation,
        "root682-cloned-state-live", "real-provider-state-capacity");

    internal static string LiveProductRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Massdex", "SyncShark");

    internal static string LiveDatabasePath => Path.Combine(LiveProductRoot, "vNext", "state", "syncshark.db");

    private static readonly HashSet<string> AllowedCommands = new(StringComparer.Ordinal)
    {
        "status", "accounts list", "accounts show", "pairs list", "pairs show",
        "settings show", "attention list", "runs list", "jobs status"
    };

    // Exactly the current state documents the read surface needs. Deliberately NOT a wildcard: any live
    // state file outside this set (a secret, a recovery archive, an unknown document) refuses the clone
    // instead of being copied, so credential material and unrelated state cannot leak into the clone.
    // The connection documents hold account REFERENCES (CredentialReference/ClientId metadata); the
    // secret itself lives only in the OS credential store and is never read or copied.
    private static readonly HashSet<string> RequiredStateFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pilot-settings.jsonl",
        "dropbox-connection.jsonl", "dropbox-connection-a.json", "dropbox-connection-b.json",
        "google-drive-connection.jsonl", "google-drive-connection-a.json", "google-drive-connection-b.json",
        "ui-preferences.jsonl", "ui-preferences-a.json", "ui-preferences-b.json",
        "sync-product-state-a.json", "sync-product-state-b.json",
        "sync-automation-a.json", "sync-automation-b.json"
    };

    private readonly PilotInstanceLock _instanceLock;
    private readonly HttpClient _client;
    private readonly SyncAuthorityRuntimeHost _host;
    private readonly string _cloneRoot;
    private int _disposed;

    private ClonedStateAdapter(string cloneRoot, PilotInstanceLock instanceLock, HttpClient client,
        SyncAuthorityRuntimeHost host)
    {
        _cloneRoot = cloneRoot;
        _instanceLock = instanceLock;
        _client = client;
        _host = host;
    }

    internal string CloneRoot => _cloneRoot;
    internal string CloneDatabasePath => Path.Combine(_cloneRoot, "vNext", "state", "syncshark.db");
    internal SyncAuthorityRuntimeHost Host => _host;

    internal static string RequireAllowedDestination(string destinationRoot, bool mustBeFresh)
    {
        var full = Path.GetFullPath(destinationRoot);
        var allocation = Path.GetFullPath(OwnerAllocation).TrimEnd(Path.DirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
        if (!full.StartsWith(allocation, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), RealStagedSsdRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The clone destination must stay inside the registered owner build allocation: " + allocation);
        RequireNoReparseAncestry(full);
        var live = Path.GetFullPath(LiveProductRoot);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar);
        if (IsSameOrNested(trimmed, live) || IsSameOrNested(live, trimmed))
            throw new InvalidDataException(
                "The clone destination resolves to the live product root; the adapter refuses before any effect.");
        if (string.Equals(trimmed, allocation.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The clone destination must be a directory inside the allocation, not the allocation root.");
        if (mustBeFresh && Directory.Exists(full))
            throw new InvalidDataException(
                "The clone destination already exists; reconcile it rather than overwriting cloned state.");
        return full;
    }

    internal static void RequireLiveSourceConfirmed(string sourceRoot, bool handoffConfirmed)
    {
        var full = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var live = Path.GetFullPath(LiveProductRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(full, live, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The clone source must be the live product root; an arbitrary source is refused.");
        if (!handoffConfirmed)
            throw new InvalidDataException(
                "Refused: snapshotting the live product database requires the Root's installed-state handoff " +
                "confirmation. The installer owns the product root until that handoff, so no read may start here.");
        if (!string.Equals(SyncSharkRuntimePaths.ProductRoot, live, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "This process does not resolve the live product root; refusing to clone ambiguous state.");
    }

    private static void RequireNoReparseAncestry(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            // A not-yet-created destination or ancestor reports Attributes = -1, so existence must be
            // checked before the attribute flags; every EXISTING ancestor is still validated.
            if (!current.Exists) continue;
            if ((current.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                throw new InvalidDataException(
                    "The clone destination crosses a reparse point or an offline item: " + current.FullName);
        }
    }

    private static bool IsSameOrNested(string candidate, string parent)
    {
        var c = candidate.TrimEnd(Path.DirectorySeparatorChar);
        var p = parent.TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(c, p, StringComparison.OrdinalIgnoreCase) ||
               c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static ClonedSnapshot PrepareClone(string destinationRoot, bool handoffConfirmed)
    {
        // The handoff/authorisation check belongs to the prepare path itself, never to a caller.
        RequireLiveSourceConfirmed(LiveProductRoot, handoffConfirmed);
        var destination = RequireAllowedDestination(destinationRoot, mustBeFresh: true);
        var liveState = Path.Combine(LiveProductRoot, "state");
        var cloneState = Path.Combine(destination, "state");
        var cloneDatabase = Path.Combine(destination, "vNext", "state", "syncshark.db");

        // Enumerate the live state set BEFORE any write. Only the explicit required set is copied; the
        // instance lock is skipped and any other file refuses the clone instead of being copied blindly.
        var toCopy = new List<string>();
        foreach (var file in Directory.GetFiles(liveState))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, SyncSharkRuntimePaths.InstanceLockFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!RequiredStateFiles.Contains(name))
                throw new InvalidDataException(
                    "The live state root holds a file this adapter is not authorised to snapshot, so the clone" +
                    " is refused rather than copying it: " + name);
            toCopy.Add(file);
        }
        toCopy.Sort(StringComparer.OrdinalIgnoreCase);

        var sourceDatabaseHash = Sha256Of(LiveDatabasePath);
        var sourceDatabaseBytes = new FileInfo(LiveDatabasePath).Length;
        Directory.CreateDirectory(cloneState);
        Directory.CreateDirectory(Path.GetDirectoryName(cloneDatabase)!);

        var copied = new List<string>();
        // Hold the ordinary product maintenance lock for the WHOLE clone step - the database backup and the
        // state-document copy - so no running owner mutates state mid-snapshot, then release it at once. It
        // is the same exclusive lock the window/CLI take; it forces no runtime and writes no process id.
        using (PilotInstanceLock.AcquireForMaintenance(SyncSharkRuntimePaths.InstanceLockFile))
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                   {
                       DataSource = LiveDatabasePath, Mode = SqliteOpenMode.ReadOnly,
                       Cache = SqliteCacheMode.Private, Pooling = false
                   }.ToString()))
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
                   {
                       DataSource = cloneDatabase, Mode = SqliteOpenMode.ReadWriteCreate,
                       Cache = SqliteCacheMode.Private, Pooling = false
                   }.ToString()))
            {
                source.Open();
                target.Open();
                source.BackupDatabase(target);
            }
            foreach (var file in toCopy)
            {
                var name = Path.GetFileName(file);
                File.Copy(file, Path.Combine(cloneState, name), overwrite: false);
                copied.Add(name);
            }
        }

        // A snapshot is a logical copy, not a byte image: prove the SOURCE was not mutated (before/after
        // hash) and the CLONE is a sound database, instead of demanding target/source byte equality.
        var sourceHashAfter = Sha256Of(LiveDatabasePath);
        if (!string.Equals(sourceHashAfter, sourceDatabaseHash, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The live database changed during the snapshot; the clone is refused rather than used.");
        var (integrity, schemaVersion) = VerifyCloneIntegrity(cloneDatabase);
        return new ClonedSnapshot(destination, cloneDatabase, sourceDatabaseBytes, sourceDatabaseHash,
            sourceHashAfter, Sha256Of(cloneDatabase), new FileInfo(cloneDatabase).Length, schemaVersion, integrity,
            copied);
    }

    internal static object ContinueRetainedRealCloneOnSsd()
    {
        // Continue the newest retained state, never the older USB/SSD rollback.
        // The caller must preserve an occupied SSD destination separately first;
        // mustBeFresh deliberately refuses an overwrite or authority reset.
        var fromCapacity = Directory.Exists(RealStagedCapacityRoot);
        var sourceRoot = RequireAllowedDestination(fromCapacity ? RealStagedCapacityRoot :
            Path.Combine(OwnerAllocation, "root682-cloned-state-live", "real-provider-state"),
            mustBeFresh: false);
        var targetRoot = RequireAllowedDestination(RealStagedSsdRoot, mustBeFresh: true);
        return ContinueRetainedRealClone(sourceRoot, targetRoot,
            fromCapacity
                ? ["bridge-conflicts.json", "choices-f00d0000000000000012.json", "review-f00d0000000000000012.json",
                   "choices-f00d0000000000000028.json", "review-f00d0000000000000028.json"]
                : ["bridge-conflicts.json", "choices-f00d0000000000000012.json", "review-f00d0000000000000012.json"]);
    }

    internal static object ContinueRetainedRealCloneOnCapacityVolume() => ContinueRetainedRealClone(
        RequireAllowedDestination(RealStagedSsdRoot, mustBeFresh: false),
        RequireAllowedDestination(RealStagedCapacityRoot, mustBeFresh: true),
        ["bridge-conflicts.json", "choices-f00d0000000000000012.json", "review-f00d0000000000000012.json",
         "choices-f00d0000000000000028.json", "review-f00d0000000000000028.json"]);

    private static object ContinueRetainedRealClone(string sourceRoot, string targetRoot, string[] rootFiles)
    {
        using var original = PilotInstanceLock.AcquireForMaintenance(SyncSharkRuntimePaths.InstanceLockFile);
        using var staged = PilotInstanceLock.Acquire(Path.Combine(sourceRoot, "state", SyncSharkRuntimePaths.InstanceLockFileName));
        var relativeFiles = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(sourceRoot, "state")))
        {
            var name = Path.GetFileName(file);
            if (name == SyncSharkRuntimePaths.InstanceLockFileName) continue;
            if (!RequiredStateFiles.Contains(name)) throw new InvalidDataException("Unknown retained state document.");
            relativeFiles.Add(Path.Combine("state", name));
        }
        if (Directory.GetFiles(sourceRoot).Any(file => !rootFiles.Contains(Path.GetFileName(file), StringComparer.Ordinal)))
            throw new InvalidDataException("Unknown retained stage root document.");
        relativeFiles.AddRange(rootFiles);
        const string databaseRelative = @"vNext\state\syncshark.db";
        relativeFiles.Add(databaseRelative);
        var sourceDatabase = Path.Combine(sourceRoot, databaseRelative);
        // A closed SQLite database may retain an empty WAL and its disposable shared-memory
        // index after a read-only inspection. Nonempty WAL is authoritative: never omit it.
        if (Directory.GetFiles(Path.GetDirectoryName(sourceDatabase)!).Any(file =>
                file != sourceDatabase && file != sourceDatabase + "-shm" &&
                !(file == sourceDatabase + "-wal" && new FileInfo(file).Length == 0)))
            throw new InvalidDataException("The retained database must be closed and checkpointed without sidecars.");
        foreach (var relative in relativeFiles)
        {
            var file = Path.Combine(sourceRoot, relative);
            RequireNoReparseAncestry(Path.GetDirectoryName(file)!);
            if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                throw new InvalidDataException("A retained source file is not an ordinary local file.");
        }
        var bytes = relativeFiles.Sum(relative => new FileInfo(Path.Combine(sourceRoot, relative)).Length);
        if (new DriveInfo(Path.GetPathRoot(targetRoot)!).AvailableFreeSpace < bytes + 4L * 1024 * 1024 * 1024)
            throw new IOException("Insufficient physical capacity for this retained snapshot and bounded inventory growth.");
        var receipts = new List<object>();
        // The source lock spans settings, all history and exact prior approvals. It stays as rollback;
        // this is continuation, never re-seeding from the older installed database or resetting effects.
        foreach (var relative in relativeFiles)
        {
            var source = Path.Combine(sourceRoot, relative); var target = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
            input.Position = 0;
            var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            if (Sha256Of(target) != hash) throw new IOException("Retained clone copy differs; do not use it.");
            receipts.Add(new { relativePath = relative, bytes = input.Length, sha256 = hash });
        }
        return new { sourceRoot, targetRoot, bytes, files = receipts, sourcePreserved = true,
            providerCalls = 0, fileEffects = 0, freshInstalledSnapshot = false };
    }

    /// <summary>
    /// Proves the cloned database is sound with SQLite's own integrity check plus the exact schema version
    /// the product expects, rather than comparing raw bytes against the source.
    /// </summary>
    private static (string Integrity, string SchemaVersion) VerifyCloneIntegrity(string cloneDatabase)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = cloneDatabase, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(check.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The cloned database failed SQLite integrity_check: " + result);
        }
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            return ("ok", Convert.ToString(version.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "unknown");
        }
    }

    internal static string Sha256Of(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal sealed record ClonedSnapshot(string Root, string DatabasePath, long SourceBytes, string SourceSha256,
        string SourceSha256After, string CloneSha256, long CloneBytes, string SchemaVersion, string Integrity,
        IReadOnlyList<string> CopiedStateFiles);

    internal static ClonedStateAdapter Open(string cloneRoot)
    {
        var full = Path.GetFullPath(cloneRoot);
        RequireAllowedDestination(full, mustBeFresh: false);
        var database = Path.Combine(full, "vNext", "state", "syncshark.db");
        if (!File.Exists(database))
            throw new InvalidDataException("The clone carries no authority database; refusing to open it.");
        Directory.CreateDirectory(Path.Combine(full, "state"));
        PilotInstanceLock instanceLock;
        try
        {
            instanceLock = PilotInstanceLock.AcquireForMaintenance(Path.Combine(full, "state",
                SyncSharkRuntimePaths.InstanceLockFileName));
        }
        catch (IOException error)
        {
            throw new InvalidOperationException(
                "Another clone owner already holds this clone's exclusive state lock, so this adapter refuses " +
                "rather than forcing or duplicating it: " + error.Message, error);
        }
        var client = new HttpClient(new DenyAllNetworkHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        try
        {
            var authority = instanceLock.StateAuthority;
            var host = new SyncAuthorityRuntimeHost(authority, client, new SyncPairStore(authority),
                new SyncRuntimeStateStore(authority), new DropboxConnectionStore(authority),
                new RefusingDropboxCredentialStore(), new SyncAuthorityDatabase(database),
                googleDriveCredentialStore: new UnconfiguredGoogleDriveCredentialStore(),
                googleDriveTokenEndpoint: null);
            return new ClonedStateAdapter(full, instanceLock, client, host);
        }
        catch
        {
            client.Dispose();
            instanceLock.Dispose();
            throw;
        }
    }

    internal async Task<(int ExitCode, string Stdout, string Stderr)> RunCommandAsync(string[] args)
    {
        RequireAllowedCommand(args);
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await SyncCli.ExecuteAsync(args, stdout, stderr,
            () => new ClonedStateCliRuntime(_instanceLock, _client, _host), CancellationToken.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Refuses any command outside the selected read set BEFORE a lock, runtime, database or bridge is
    /// opened, so a mutating command cannot reach the clone, let alone production.
    /// </summary>
    internal static void RequireAllowedCommand(string[] args)
    {
        var invocation = SyncCliRegistry.Parse(args);
        if (!SyncCli.IsStateFreeInvocation(args) && !AllowedCommands.Contains(invocation.Command))
            throw new InvalidOperationException(
                "The cloned-state adapter exposes only selected read commands; '" + invocation.Command +
                "' is refused before any lock, runtime, database or bridge is opened.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _host.Dispose();
        }
        finally
        {
            _client.Dispose();
            _instanceLock.Dispose();
        }
        await Task.CompletedTask;
    }
}

/// <summary>
/// R682: the thin test-only runtime. It adds no command semantics: every command still executes once in
/// the real SyncCliOperationsBase over the clone host.
/// </summary>
internal sealed class ClonedStateCliRuntime : SyncCliOperationsBase
{
    private readonly PilotInstanceLock _instanceLock;
    private readonly HttpClient _client;
    private readonly SyncAuthorityRuntimeHost _host;

    internal ClonedStateCliRuntime(PilotInstanceLock instanceLock, HttpClient client, SyncAuthorityRuntimeHost host)
    {
        _instanceLock = instanceLock;
        _client = client;
        _host = host;
    }

    protected override SyncAuthorityRuntimeHost Host => _host;
    protected override PilotStateAuthority StateAuthority => _instanceLock.StateAuthority;
    public override string Mode => "clone";
    // The ADAPTER owns the shared clone lock, transport and host for the whole readback, so the
    // per-command runtime must not dispose them when SyncCli.ExecuteAsync releases this instance.
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Denies every network request. No clone command may reach a provider through this handler.</summary>
internal sealed class DenyAllNetworkHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Network is refused for the cloned-state adapter: " + request.Method + " " + request.RequestUri);
}

/// <summary>Refuses every Dropbox credential read and write; the clone never touches an OS secret.</summary>
internal sealed class RefusingDropboxCredentialStore : IDropboxRefreshTokenStore
{
    public Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "Credential reads are refused for the cloned-state adapter; no secret is read or exported.");

    public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "Credential writes are refused for the cloned-state adapter; no secret is written.");

    public Task DeleteAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "Credential deletion is refused for the cloned-state adapter; no secret is changed.");
}
