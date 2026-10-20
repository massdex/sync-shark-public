using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// PREPARED SOURCE, NOT COMPILED IN THIS BUILD, and not executed evidence.
//
// CCS7 requires a read-only PlanHistoryRetentionAsync proof on a tiny synthetic fixture. The retention planner
// is deliberately excluded from the product build (SyncShark.Core.csproj removes
// SyncAuthorityDatabase.HistoryRetention.cs and SyncAuthorityRuntimeHost.HistoryRetention.cs, and this test
// project removes AuthorityHistoryRetentionChecks.cs), so this file follows that exact existing pattern and is
// excluded from compilation the same way. It compiles and runs only when M7 is separately activated under its
// own protected review. Until then the CCS7 registration in SyncAuthorityDatabase.HistoryRetention.cs is source
// compatibility only, exactly as the work order requires, and this proof is UNVERIFIED.
//
// ExpireHistoryAsync is never called here: the proof is a read-only plan, never a delete trial.
[SupportedOSPlatform("windows")]
internal static class ActivityContextRetentionChecks
{
    internal static async Task RunAsync(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidDataException("Use a fresh isolated activity-context retention fixture root.");
        Directory.CreateDirectory(root);
        var old = DateTimeOffset.UtcNow.AddDays(-200);
        var recent = DateTimeOffset.UtcNow.AddDays(-10);
        var alone = await PlanAsync(Path.Combine(root, "anchor-alone"), old, recent, withDependent: false);
        var held = await PlanAsync(Path.Combine(root, "anchor-held"), old, recent, withDependent: true);
        var delta = held.Plan.KeptReasons.GetValueOrDefault("retained_reference") -
            alone.Plan.KeptReasons.GetValueOrDefault("retained_reference");
        Check(!alone.Plan.Applied && !held.Plan.Applied &&
            alone.Plan.RowsByTable.GetValueOrDefault("history_event") == 0 &&
            held.Plan.RowsByTable.GetValueOrDefault("history_event") == 0,
            "no retained activity row, and in particular neither the full-context anchor nor its dependent, is ever selected by the read-only plan");
        Check(old < held.Plan.CutoffUtc && old < alone.Plan.CutoffUtc,
            "the anchor is genuinely older than the retention cutoff, so it would be a candidate without the hold");
        Check(delta >= 1 && held.Plan.KeptReasons.GetValueOrDefault("presentation_replay_identity") > 0,
            "a newer retained dependent event holds its older full-context anchor through the registered history-event context reference");
        Check(alone.Fingerprint == alone.ReplanFingerprint && held.Fingerprint == held.ReplanFingerprint &&
            alone.Plan.SelectedRows == alone.Replan.SelectedRows &&
            held.Plan.SelectedRows == held.Replan.SelectedRows,
            "planning twice mutates no retained byte and selects exactly the same rows, so this is a plan and never a delete trial");
        Console.WriteLine("PASS activity context retention plan: " +
            held.Plan.KeptReasons.GetValueOrDefault("retained_reference") +
            " rows held through the registered context reference (delta " + delta.ToString(CultureInfo.InvariantCulture) +
            " over the anchor-alone fixture), history_event rows selected " +
            held.Plan.RowsByTable.GetValueOrDefault("history_event").ToString(CultureInfo.InvariantCulture) +
            ", cutoff " + held.Plan.CutoffUtc.ToString("O", CultureInfo.InvariantCulture) + ".");
    }

    private sealed record RetentionFixture(SyncAuthorityHistoryRetentionResult Plan, string Fingerprint,
        string ReplanFingerprint, long ReplanSelectedRows);

    private static async Task<RetentionFixture> PlanAsync(string caseRoot, DateTimeOffset old,
        DateTimeOffset recent, bool withDependent)
    {
        var database = new SyncAuthorityDatabase(Path.Combine(caseRoot, "activity.db"));
        await database.InitializeAsync("acs-retention-install", "acs-retention-profile", "owner-fixture", "{}");
        var local = Directory.CreateDirectory(Path.Combine(caseRoot, "local")).FullName;
        var pair = LegacyPair("pair-acs-retention", "Retention fixture", local, "connection-acs-retention");
        var context = SyncEndpointContext.Capture(pair);
        var anchor = new SyncActivityEvent("acs-retention-anchor", old, [SyncActivityCategories.Sync],
            "Information", "File", "Uploaded", "Uploaded folder/anchor.txt", "connection-acs-retention",
            pair.Id, "run-acs-retention", "op-acs-retention", "folder/anchor.txt", context);
        var dependent = anchor with { EventId = "acs-retention-dependent", OccurredUtc = recent,
            RelativePath = "folder/dependent.txt" };
        // One append transaction: the first new row carries the full context, the newer second row references
        // that anchor, which is exactly the representation the read-only plan must recognise as a dependency.
        await database.AppendActivityEventsAsync(withDependent ? [anchor, dependent] : [anchor]);
        var fingerprint = await FingerprintAsync(database.DatabasePath);
        var plan = await database.PlanHistoryRetentionAsync(100_000, 200_000, 32L * 1024 * 1024);
        var replan = await database.PlanHistoryRetentionAsync(100_000, 200_000, 32L * 1024 * 1024);
        return new RetentionFixture(plan, fingerprint, await FingerprintAsync(database.DatabasePath),
            replan.SelectedRows);
    }

    private static async Task<string> FingerprintAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT history_event_id,coalesce(operation_id,''),event_kind,detail_json," +
            "occurred_utc FROM history_event ORDER BY rowid;";
        var payload = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            for (var column = 0; column < 5; column++) payload.Append(reader.GetString(column)).Append('\u001f');
            payload.Append('#');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private static SyncPair LegacyPair(string pairId, string name, string localFolder, string connectionId)
    {
        using var lease = PilotDirectoryLease.Open(localFolder);
        return new SyncPair(pairId, name, localFolder, lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
            "/" + pairId, "id:" + pairId, ConnectionId: connectionId);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("Activity context retention assertion failed: " + message);
        Console.WriteLine("PASS ACS-R " + message);
    }
}
