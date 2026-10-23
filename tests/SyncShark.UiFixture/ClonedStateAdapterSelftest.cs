using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

namespace SyncShark.UiFixture;

internal static partial class Program
{
    /// <summary>
    /// R682: focused fixture cases for the cloned-state adapter. Every case here runs WITHOUT touching the
    /// live product state: they exercise the destination/source guards, the read-command allowlist, the
    /// clone lock, the deny-all network handler, the refusing credential stores and a fresh clone host.
    /// </summary>
    private static int RunClonedStateAdapterSelftest(string root)
    {
        var scratch = Path.Combine(Path.GetFullPath(ClonedStateAdapter.OwnerAllocation), root);
        if (Directory.Exists(scratch))
        {
            // A previous run's junction must be removed as a link, never recursed into its live target.
            foreach (var child in Directory.GetDirectories(scratch))
                if ((new DirectoryInfo(child).Attributes & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(child, recursive: false);
            Directory.Delete(scratch, recursive: true);
        }
        Directory.CreateDirectory(scratch);
        var cases = new List<string>();
        var results = new List<object>();
        void Refused(string label, Action action)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException)
            { cases.Add(label); results.Add(new { @case = label, refused = true, message = error.Message }); return; }
            throw new InvalidDataException("Case did not refuse: " + label);
        }

        Refused("live-root destination refused", () =>
            ClonedStateAdapter.RequireAllowedDestination(ClonedStateAdapter.LiveProductRoot, mustBeFresh: true));
        Refused("destination outside the owner allocation refused", () =>
            ClonedStateAdapter.RequireAllowedDestination(Path.Combine(Path.GetTempPath(), "rc682-outside"), true));
        Refused("allocation root destination refused", () =>
            ClonedStateAdapter.RequireAllowedDestination(ClonedStateAdapter.OwnerAllocation, true));
        if (ClonedStateAdapter.RequireAllowedDestination(ClonedStateAdapter.RealStagedSsdRoot, false) !=
            ClonedStateAdapter.RealStagedSsdRoot) throw new InvalidDataException("Exact SSD continuation root was not admitted.");
        cases.Add("exact retained SSD continuation root admitted without opening it");
        Refused("SSD sibling destination refused", () => ClonedStateAdapter.RequireAllowedDestination(
            ClonedStateAdapter.RealStagedSsdRoot + "-other", false));
        Refused("SSD parent destination refused", () => ClonedStateAdapter.RequireAllowedDestination(
            Path.GetDirectoryName(ClonedStateAdapter.RealStagedSsdRoot)!, false));
        var existing = Path.Combine(scratch, "already-there");
        Directory.CreateDirectory(existing);
        Refused("existing destination refused", () =>
            ClonedStateAdapter.RequireAllowedDestination(existing, mustBeFresh: true));

        var alias = Path.Combine(scratch, "live-alias");
        var junction = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + alias + "\" \"" +
                                            ClonedStateAdapter.LiveProductRoot + "\"");
        junction.UseShellExecute = false; junction.RedirectStandardOutput = true;
        junction.RedirectStandardError = true; junction.CreateNoWindow = true;
        try
        {
            using var process = Process.Start(junction);
            process?.WaitForExit();
        }
        catch (Exception) { }
        if (Directory.Exists(alias))
            Refused("destination junction alias refused", () =>
                ClonedStateAdapter.RequireAllowedDestination(alias, mustBeFresh: true));

        Refused("non-live clone source refused", () =>
            ClonedStateAdapter.RequireLiveSourceConfirmed(Path.GetTempPath(), handoffConfirmed: true));
        Refused("live source without the installed-state handoff refused", () =>
            ClonedStateAdapter.RequireLiveSourceConfirmed(ClonedStateAdapter.LiveProductRoot, false));
        Refused("snapshot refused at the prepare path without the handoff confirmation", () =>
            ClonedStateAdapter.PrepareClone(Path.Combine(scratch, "no-handoff-clone"), handoffConfirmed: false));

        var emptyClone = Path.Combine(scratch, "empty-clone");
        Directory.CreateDirectory(emptyClone);
        Refused("missing clone database refused", () => ClonedStateAdapter.Open(emptyClone));

        Refused("mutating command refused before any open", () => ClonedStateAdapter.RequireAllowedCommand(
            ["--cli", "pairs", "retire", "--pair", "f00d0000000000000012", "--yes"]));
        ClonedStateAdapter.RequireAllowedCommand(["--cli", "pairs", "list"]);
        cases.Add("selected read command accepted by the allowlist");

        using (var first = PilotInstanceLock.AcquireForMaintenance(Path.Combine(emptyClone, "state",
                   SyncSharkRuntimePaths.InstanceLockFileName)))
        {
            Refused("second clone owner lock contention refused", () =>
                PilotInstanceLock.AcquireForMaintenance(Path.Combine(emptyClone, "state",
                    SyncSharkRuntimePaths.InstanceLockFileName)).Dispose());
        }

        Refused("network use refused", () =>
            new HttpClient(new DenyAllNetworkHandler()).GetStringAsync("https://api.dropboxapi.com/2/users/get_current_account")
                .GetAwaiter().GetResult());
        Refused("dropbox credential read refused", () => new RefusingDropboxCredentialStore().ReadAsync()
            .GetAwaiter().GetResult());
        Refused("dropbox credential write refused", () => new RefusingDropboxCredentialStore()
            .WriteAsync("x").GetAwaiter().GetResult());
        Refused("google credential write refused", () =>
            new UnconfiguredGoogleDriveCredentialStore().WriteAsync("c", "t").GetAwaiter().GetResult());

        var freshClone = Path.Combine(scratch, "fresh-clone");
        Directory.CreateDirectory(Path.Combine(freshClone, "state"));
        Directory.CreateDirectory(Path.Combine(freshClone, "vNext", "state"));
        var freshDatabase = Path.Combine(freshClone, "vNext", "state", "syncshark.db");
        using (var create = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = freshDatabase, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            create.Open();
            using var command = create.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            command.ExecuteScalar();
        }
        var opened = ClonedStateAdapter.Open(freshClone);
        try
        {
            var pairs = opened.RunCommandAsync(["--cli", "pairs", "list", "--json"]).GetAwaiter().GetResult();
            var accounts = opened.RunCommandAsync(["--cli", "accounts", "list", "--json"]).GetAwaiter().GetResult();
            var attention = opened.RunCommandAsync(["--cli", "attention", "list", "--json"]).GetAwaiter().GetResult();
            cases.Add("fresh clone host opened and ran selected read commands");
            results.Add(new
            {
                @case = "fresh clone host read commands", refused = false,
                pairs_exit = pairs.ExitCode, pairs_stdout = Trim(pairs.Stdout),
                accounts_exit = accounts.ExitCode, accounts_stdout = Trim(accounts.Stdout),
                attention_exit = attention.ExitCode, attention_stdout = Trim(attention.Stdout),
                production_root_used = opened.CloneRoot
            });
        }
        finally { opened.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

        Console.WriteLine(JsonSerializer.Serialize(new { cases, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloned-state adapter selftest: " + cases.Count + " cases, live state untouched");
        return 0;
    }


    /// <summary>
    /// R697-ISOLATED-CLONED-JOURNEY (applicability leg). Reports the EXACT retained-clone applicability
    /// verdict without creating a new production database snapshot, re-proves the escape rejections inside
    /// the owned F allocation, and measures the fixture fit. It deliberately does not claim faithful
    /// copied-state proof: if no eligible retained clone exists, that gap stays OPEN.
    /// </summary>
    /// <summary>R697: the EXACT retained clone candidates recorded by the existing clone/migration records
    /// (coordinator682-cloned-state-adapter-result.json), relocated to the owner build allocation on F. The
    /// protected root713 live-alias duplicate is deliberately NOT listed, so it is never traversed.</summary>
    private static string[] RecordedRetainedCloneCandidates =>
    [
        Path.Combine(ClonedStateAdapter.OwnerAllocation, "root682-cloned-state-live", "live-state-clone"),
        Path.Combine(ClonedStateAdapter.OwnerAllocation, "root682-cloned-state-selftest", "empty-clone"),
        Path.Combine(ClonedStateAdapter.OwnerAllocation, "root682-cloned-state-selftest", "fresh-clone")
    ];

    private static int RunClonedStateJourneyApplicability(string? suppliedRoot)
    {
        var allocation = Path.GetFullPath(ClonedStateAdapter.OwnerAllocation);
        if (!Directory.Exists(allocation)) throw new InvalidDataException(
            "The relocated owner build allocation is unavailable: " + allocation);
        // R697 repair: this leg is READ-ONLY and performs NO scratch lifecycle. It never creates or deletes
        // any path and never deletes a user-supplied argument; the argument is an inert probe.
        string? probe = null;
        if (!string.IsNullOrWhiteSpace(suppliedRoot))
            probe = Path.IsPathFullyQualified(suppliedRoot) ? Path.GetFullPath(suppliedRoot)
                : Path.Combine(allocation, suppliedRoot);
        bool ProbeExists() => probe is not null && (File.Exists(probe) || Directory.Exists(probe));
        string ProbeSignature() => probe is null ? "" : File.Exists(probe)
            ? "file:" + new FileInfo(probe).Length
            : Directory.Exists(probe)
                ? "dir:" + string.Join(",", Directory.GetFileSystemEntries(probe)
                    .OrderBy(item => item, StringComparer.Ordinal))
                : "";
        var probeExistedBefore = ProbeExists();
        var probeSignatureBefore = ProbeSignature();

        // Exactly the recorded retained-clone candidates; no recursive scan, and the protected root713
        // live-alias duplicate is NOT listed so it is never traversed.
        var candidates = new List<object>();
        foreach (var candidate in RecordedRetainedCloneCandidates)
        {
            var database = Path.Combine(candidate, "vNext", "state", "syncshark.db");
            var directoryPresent = Directory.Exists(candidate);
            var databasePresent = File.Exists(database);
            long? databaseBytes = null;
            string? walBytes = null, shm = null, schemaVersion = null, integrity = null;
            if (databasePresent)
            {
                databaseBytes = new FileInfo(database).Length;
                walBytes = File.Exists(database + "-wal")
                    ? new FileInfo(database + "-wal").Length.ToString(CultureInfo.InvariantCulture) : "missing";
                shm = File.Exists(database + "-shm") ? "present" : "missing";
                try
                {
                    using var connection = new SqliteConnection(
                        "Data Source=" + database + ";Mode=ReadOnly;Pooling=False");
                    connection.Open();
                    using (var version = connection.CreateCommand())
                    { version.CommandText = "PRAGMA user_version;"; schemaVersion = Convert.ToString(
                        version.ExecuteScalar(), CultureInfo.InvariantCulture); }
                    using (var check = connection.CreateCommand())
                    { check.CommandText = "PRAGMA integrity_check;"; integrity = Convert.ToString(
                        check.ExecuteScalar(), CultureInfo.InvariantCulture); }
                }
                catch (Exception error) { integrity = "unreadable: " + error.GetType().Name; }
            }
            candidates.Add(new { path = candidate, directory_present = directoryPresent,
                database_present = databasePresent, database_bytes = databaseBytes, wal_bytes = walBytes,
                shm_sidecar = shm, schema_version = schemaVersion, integrity });
        }
        var liveCloneEligible =
            (bool)(candidates[0].GetType().GetProperty("database_present")!.GetValue(candidates[0])!) &&
            string.Equals((string?)candidates[0].GetType().GetProperty("integrity")!.GetValue(candidates[0]),
                "ok", StringComparison.OrdinalIgnoreCase);

        var rejections = new List<object>();
        void Refused(string label, Action action)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException
                                              or HttpRequestException)
            { rejections.Add(new { @case = label, refused = true, message = error.Message }); return; }
            throw new InvalidDataException("Case did not refuse: " + label);
        }
        Refused("production product root destination refused",
            () => ClonedStateAdapter.RequireAllowedDestination(ClonedStateAdapter.LiveProductRoot, true));
        Refused("destination outside the owner allocation refused",
            () => ClonedStateAdapter.RequireAllowedDestination(Path.Combine(Path.GetTempPath(), "r697-outside"), true));
        Refused("mutating command refused before any open", () => ClonedStateAdapter.RequireAllowedCommand(
            ["--cli", "pairs", "retire", "--pair", "f00d0000000000000012", "--yes"]));
        Refused("network use refused", () => new HttpClient(new DenyAllNetworkHandler())
            .GetStringAsync("https://api.dropboxapi.com/2/users/get_current_account").GetAwaiter().GetResult());
        Refused("dropbox credential read refused", () =>
            new RefusingDropboxCredentialStore().ReadAsync().GetAwaiter().GetResult());
        Refused("google credential write refused", () =>
            new UnconfiguredGoogleDriveCredentialStore().WriteAsync("c", "t").GetAwaiter().GetResult());

        var probeExistedAfter = ProbeExists();
        var probeSignatureAfter = ProbeSignature();
        if (probeExistedAfter != probeExistedBefore || probeSignatureAfter != probeSignatureBefore)
            throw new InvalidDataException("The supplied root argument was modified: " + probe);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema = "r697-cloned-state-applicability/2",
            owner_allocation = allocation,
            discovery_scope = "exactly the three recorded retained-clone candidate paths; no recursive scan",
            live_alias_traversed = false,
            live_database_present = File.Exists(ClonedStateAdapter.LiveDatabasePath),
            candidates,
            eligible_live_state_clone = liveCloneEligible,
            faithful_copied_state_proof = liveCloneEligible
                ? "AVAILABLE - the recorded live-state clone is present, schema-readable and integrity-ok"
                : "OPEN - no eligible recorded retained clone (missing, unreadable or not integrity-ok); no new snapshot is created",
            new_snapshot_created = false,
            supplied_root_probe = new { path = probe, existed_before = probeExistedBefore,
                existed_after = probeExistedAfter, unchanged = probeSignatureAfter == probeSignatureBefore },
            escape_rejections = rejections
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloned-state journey applicability: " + rejections.Count +
                          " escape rejections; eligible recorded live-state clone = " + liveCloneEligible);
        return 0;
    }
    private static string Trim(string value) => value.Length <= 400 ? value.Trim() : value.Trim()[..400] + "...";

    /// <summary>
    /// R682 live slice: clone the CURRENT retained state and read it back through the real product pipeline
    /// with network and credentials denied, then prove the live source was byte-identical before and after.
    /// </summary>
    private static int RunClonedStateAdapterLive(string root)
    {
        var scratch = Path.Combine(Path.GetFullPath(ClonedStateAdapter.OwnerAllocation), root);
        if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        Directory.CreateDirectory(scratch);

        var liveBefore = ClonedStateAdapter.Sha256Of(ClonedStateAdapter.LiveDatabasePath);
        var liveBytes = new FileInfo(ClonedStateAdapter.LiveDatabasePath).Length;

        var cloneRoot = Path.Combine(scratch, "live-state-clone");
        var snapshot = ClonedStateAdapter.PrepareClone(cloneRoot, handoffConfirmed: true);

        var liveAfter = ClonedStateAdapter.Sha256Of(ClonedStateAdapter.LiveDatabasePath);
        if (!string.Equals(liveBefore, liveAfter, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The live database changed across the clone; refusing to report success.");

        var results = new List<object>();
        var adapter = ClonedStateAdapter.Open(cloneRoot);

        void Run(string[] args)
        {
            var result = adapter.RunCommandAsync(args).GetAwaiter().GetResult();
            results.Add(new
            {
                command = string.Join(' ', args), exit = result.ExitCode,
                stdout = Trim(result.Stdout), stderr = Trim(result.Stderr)
            });
        }

        try
        {
            Run(["--cli", "pairs", "list", "--json"]);
            Run(["--cli", "pairs", "show", "--pair", "f00d0000000000000012", "--json"]);
            Run(["--cli", "accounts", "list", "--json"]);
            Run(["--cli", "settings", "show", "--json"]);
            Run(["--cli", "attention", "list", "--json"]);
        }
        finally
        {
            adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        var pairRows = DumpCloneDiagnostics(snapshot.DatabasePath);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            clone_root = snapshot.Root,
            live_bytes = liveBytes,
            live_sha256_before = liveBefore,
            live_sha256_after = liveAfter,
            source_bytes = snapshot.SourceBytes,
            snapshot_source_sha256 = snapshot.SourceSha256,
            snapshot_source_sha256_after = snapshot.SourceSha256After,
            clone_bytes = snapshot.CloneBytes,
            schema_version = snapshot.SchemaVersion,
            integrity = snapshot.Integrity,
            state_files = snapshot.CopiedStateFiles.Count,
            pair_rows = pairRows,
            results
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloned-state adapter live clone: live source byte-identical, readback ran through the real pipeline");
        return 0;
    }

    /// <summary>
    /// Read-only diagnostic over the CLONE database only: it characterises the retained pair rows exactly
    /// (including unpublished candidates the published-pair CLI surface intentionally hides), so the
    /// SAME-child state is reported from the real row rather than inferred. No production access, no write.
    /// </summary>
    private static List<string> DumpCloneDiagnostics(string database)
    {
        var rows = new List<string>();
        using var connection = new SqliteConnection("Data Source=" + database + ";Mode=ReadOnly;Pooling=False");
        connection.Open();
        using (var tables = connection.CreateCommand())
        {
            tables.CommandText = "select name from sqlite_master where type='table' order by name";
            var names = new List<string>();
            using var reader = tables.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
            rows.Add("tables: " + string.Join(", ", names));
        }
        if (!rows[0].Split(": ")[1].Split(", ").Contains("sync_pair", StringComparer.Ordinal))
            return rows;
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "select * from sync_pair";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var fields = new List<string>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    string value;
                    if (reader.IsDBNull(i)) value = "NULL";
                    else
                    {
                        var text = Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                        value = name.Contains("json", StringComparison.OrdinalIgnoreCase)
                            ? "<json " + text.Length + " chars>"
                            : text.Length <= 80 ? text : text[..80] + "...";
                    }
                    fields.Add(name + "=" + value);
                }
                rows.Add("sync_pair: " + string.Join(", ", fields));
            }
        }
        return rows;
    }
}
