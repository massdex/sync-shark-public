using System.Collections.Concurrent;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// The real production reservation admission decides these cases. Inputs come
// from retained roots/objects created by the preceding real CLI effect fixture.
// No executor/provider effect is invoked and no production state is accessed.
internal static class ExecutionReservationCandidateChecks
{
    internal static async Task<int> RunAsync(SyncAuthorityDatabase db)
    {
        var checks = 0;
        void Require(bool passed, string label)
        {
            if (!passed) throw new InvalidOperationException("Reservation candidate check failed: " + label);
            checks++;
            Console.WriteLine("PASS " + label);
        }

        var prototypes = new List<SyncAuthorityOperationSnapshot>();
        long retainedHeaderCount;
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                     { DataSource = db.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT count(*) FROM operation_member WHERE expected_path_key IS NOT NULL;";
                retainedHeaderCount = Convert.ToInt64(await count.ExecuteScalarAsync());
            }
            var ids = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT operation_id FROM operation
                    WHERE status='completed' AND kind='create_local' AND pair_id IS NOT NULL
                    ORDER BY created_utc DESC,operation_id;
                    """;
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
            }
            foreach (var id in ids) prototypes.Add(await db.LoadOperationAsync(id));
        }
        var pairs = prototypes.GroupBy(item => item.PairId!, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.ObjectId).Distinct().Count() >= 2).Take(2).ToArray();
        Require(pairs.Length == 2, "real completed CLI effects provide two retained participant root sets");
        var first = pairs[0].First();
        var peer = pairs[0].First(item => item.ObjectId != first.ObjectId);
        var second = pairs[1].First();
        static string Root(SyncAuthorityOperationSnapshot item, string role) =>
            item.Members.Single(member => member.Role == role).RootBindingId;
        var directoryId = "object-" + Guid.NewGuid().ToString("N");
        await db.RegisterObjectAsync(directoryId, "directory");
        // One genuine accepted batch command scoped to the two exact participant pairs this fixture seeds.
        var seedAdmission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_all", null, null, null,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                pairIds = pairs.SelectMany(group => group.Select(item => item.PairId)).Distinct(StringComparer.Ordinal).ToArray()
            }), SyncAuthorityCommandPurpose.NormalEffect);

        SyncAuthorityOperationPlan File(SyncAuthorityOperationSnapshot input, string path,
            string? additionalLock = null)
        {
            var id = "operation-" + Guid.NewGuid().ToString("N");
            var members = input.Members.Select(member => member with
            { MemberId = "member-" + Guid.NewGuid().ToString("N"), ExpectedPathKey = path }).ToList();
            if (additionalLock is not null)
                members.Add(new("member-" + Guid.NewGuid().ToString("N"), input.PairId!, input.ObjectId!,
                    "lock", Root(input, "destination"), null, null, null, additionalLock, "{}"));
            return new(id, id, SyncAuthorityOperationKind.CreateLocal, input.PairId, input.ObjectId,
                input.ConfigurationRevision, input.CapabilityProfileId, "{}", 50, members, [], []);
        }

        var participants = new[] { first, second };
        var subtreeMembers = participants.SelectMany(input => new[] { "source", "destination" }
            .Select(role => new SyncAuthorityMember("member-" + Guid.NewGuid().ToString("N"),
                input.PairId!, directoryId, "affected", Root(input, role), null, null, null,
                "metadata-guard/tree", "{\"present\":true}"))).ToArray();
        var crossId = "operation-" + Guid.NewGuid().ToString("N");
        var cross = new SyncAuthorityOperationPlan(crossId, crossId, SyncAuthorityOperationKind.CrossPairMove,
            first.PairId, directoryId, first.ConfigurationRevision, first.CapabilityProfileId,
            System.Text.Json.JsonSerializer.Serialize(new
            { entryKind = "Directory", sourcePairId = first.PairId, destinationPairId = second.PairId }),
            50, subtreeMembers, [], [],
            SubtreeReservations: subtreeMembers.Select(member =>
                new SyncAuthoritySubtreeReservation(member.PairId, member.RootBindingId,
                    member.ExpectedPathKey!)).ToArray());

        async Task<bool> Refused(Task action)
        {
            try { await action; return false; }
            catch (InvalidOperationException error) when (error.Message.Contains(
                       "nonterminal operation reservation", StringComparison.Ordinal))
            { return true; }
        }

        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = new ConcurrentQueue<int>();
        var snapshots = new ConcurrentQueue<int>();
        var boundary = 0;
        SyncAuthorityDatabase.ReservationCandidateSnapshotLoadedForTest = snapshots.Enqueue;
        SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = size =>
        {
            commits.Enqueue(size);
            if (Interlocked.Increment(ref boundary) != 1) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Reservation proof did not release its first commit.");
        };
        var pending = new List<Task>();
        try
        {
            await using (db.BeginExecutionConnectionScope(planningReservations: true))
            {
                // Hold ONE already-running transaction while admitting the complete
                // next batch. All eleven requests then exercise ONE real SQLite
                // transaction, not a fake index or separately committed operations.
                var warm = db.SetPairStateAsync(first.PairId!, "active");
                pending.Add(warm);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var warmFile = db.CreateOperationAsync(File(first, "metadata-guard/warm.txt"), seedAdmission);
                var occupied = db.CreateOperationAsync(File(first, "metadata-guard/occupied.txt"), seedAdmission);
                var duplicate = db.CreateOperationAsync(File(peer, "metadata-guard/occupied.txt"), seedAdmission);
                var warmAgain = db.CreateOperationAsync(File(peer, "metadata-guard/warm-again.txt"), seedAdmission);
                var subtree = db.CreateOperationAsync(cross, seedAdmission);
                var child = db.CreateOperationAsync(File(peer, "metadata-guard/tree/child.txt"), seedAdmission);
                var rollback = db.CreateOperationAsync(File(peer, "metadata-guard/occupied.txt",
                    "metadata-guard/rollback-only.txt"), seedAdmission);
                var afterRollback = db.CreateOperationAsync(File(peer, "metadata-guard/rollback-only.txt"), seedAdmission);
                var unicode = db.CreateOperationAsync(File(first, "metadata-guard/Café.txt"), seedAdmission);
                var unicodeDuplicate = db.CreateOperationAsync(File(peer, "metadata-guard/café.txt"), seedAdmission);
                var safe = db.CreateOperationAsync(File(peer, "metadata-guard/independent.txt"), seedAdmission);
                pending.AddRange([warmFile, occupied, duplicate, warmAgain, subtree, child,
                    rollback, afterRollback, unicode, unicodeDuplicate, safe]);
                release.Set();
                await warm;
                await Task.WhenAll(warmFile, occupied, warmAgain, subtree, afterRollback, unicode, safe);
                Require(await Refused(duplicate),
                    "later same-transaction ASCII overlap is rejected after the coarse index was loaded");
                Require(await Refused(child),
                    "new same-transaction cross-pair subtree blocks its later descendant");
                Require(await Refused(rollback),
                    "a real conflicting request rolls back its inserted additional lock/member rows");
                Require(await Refused(unicodeDuplicate),
                    "transaction-bound ordinal Unicode candidates preserve case-conflict rejection");
                Require(commits.Count == 2 && commits.Last() == 7,
                    "seven legitimate peers and four refusals used one actual transaction after the held warm commit");
                Require(snapshots.Count >= 3 && snapshots.All(count => count > 0),
                    "savepoint refusals invalidate complete header snapshots before later guards");
                Require(snapshots.All(count => count >= retainedHeaderCount),
                    "materialized header lookup preserves all historical members, not only active operations");
                Require((await db.LoadOperationAsync(crossId)).SubtreeReservations.Count == 4,
                    "accepted coordinated reservation retains every participant subtree unchanged");
            }
        }
        finally
        {
            release.Set();
            try { await Task.WhenAll(pending); } catch { /* Every expected rejection is checked above. */ }
            SyncAuthorityDatabase.ReservationCandidateSnapshotLoadedForTest = null;
            SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = null;
        }

        // A new reservation committed OUTSIDE the planning scope must become visible
        // to the next planning transaction; an old candidate snapshot would omit it.
        var external = File(first, "metadata-guard/next-transaction.txt");
        await db.CreateOperationAsync(external, seedAdmission);
        await using (db.BeginExecutionConnectionScope(planningReservations: true))
            Require(await Refused(db.CreateOperationAsync(File(peer, "metadata-guard/next-transaction.txt"), seedAdmission)),
                "next transaction sees newly committed reservations rather than a prior candidate snapshot");

        // A different real SQLite writer commits while the held planning scope
        // stays alive. Its connection's data_version must invalidate the prior
        // coarse index, not merely the opening of a replacement scope.
        await using (db.BeginExecutionConnectionScope(planningReservations: true))
        {
            await db.CreateOperationAsync(File(first, "metadata-guard/held-connection-warm.txt"), seedAdmission);
            var independent = new SyncAuthorityDatabase(db.DatabasePath);
            await independent.CreateOperationAsync(File(first, "metadata-guard/external-during-scope.txt"), seedAdmission);
            Require(await Refused(db.CreateOperationAsync(File(peer, "metadata-guard/external-during-scope.txt"), seedAdmission)),
                "held planning connection detects an independent committed reservation before the next guard");
            await db.CreateOperationAsync(File(peer, "metadata-guard/held-connection-safe.txt"), seedAdmission);
            Require(true, "external-commit invalidation preserves unrelated legitimate planning");
        }

        // Failed FULL commit releases the candidate universe; a later transaction
        // can admit the same path because no request was acknowledged or retained.
        var failedPlan = File(first, "metadata-guard/failed-commit.txt");
        SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = _ =>
            throw new IOException("Injected reservation candidate pre-commit refusal.");
        try
        {
            await using (db.BeginExecutionConnectionScope(planningReservations: true))
            {
                var failed = false;
                try { await db.CreateOperationAsync(failedPlan, seedAdmission); }
                catch (IOException) { failed = true; }
                Require(failed, "failed durable commit acknowledges no planned reservation");
            }
        }
        finally { SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = null; }
        await using (db.BeginExecutionConnectionScope(planningReservations: true))
            await db.CreateOperationAsync(File(peer, "metadata-guard/failed-commit.txt"), seedAdmission);
        Require(true, "next transaction safely admits a path from the rolled-back commit");
        // A Unicode filename must not turn every following ordinary file into a
        // scan of unrelated retained plans. Exercise the actual reservation path,
        // including ordinal Unicode case collisions, not a replacement comparer.
        var selectedCounts = new List<int>();
        var previousSelection = SyncAuthorityDatabase.ReservationCandidateSelectionForTest;
        try
        {
            await using var scope = db.BeginExecutionConnectionScope(planningReservations: true);
            for (var index = 0; index < 64; index++)
                await db.CreateOperationAsync(File(first, $"unicode-scale/Étage-{index}.txt"), seedAdmission);
            SyncAuthorityDatabase.ReservationCandidateSelectionForTest = selectedCounts.Add;
            await db.CreateOperationAsync(File(peer, "unicode-scale/independent.txt"), seedAdmission);
            Require(selectedCounts.Count == 1 && selectedCounts[0] < 16,
                "unrelated Unicode history is not fetched for an ordinary file reservation");
            selectedCounts.Clear();
            Require(await Refused(db.CreateOperationAsync(File(peer, "unicode-scale/étage-17.txt"), seedAdmission)),
                "ordinal non-ASCII case collision is rejected by real transactional admission");
            Require(selectedCounts.Count == 1 && selectedCounts[0] < 16,
                "Unicode input uses bounded candidates rather than the whole root");
            await db.CreateOperationAsync(File(first, "unicode-scale/日本語.txt"), seedAdmission);
            Require(await Refused(db.CreateOperationAsync(File(peer, "unicode-scale/日本語.txt"), seedAdmission)),
                "identical non-Latin filename remains reserved");
            await db.CreateOperationAsync(File(first, "unicode-scale\\\\nested\\\\Résumé.txt"), seedAdmission);
            Require(await Refused(db.CreateOperationAsync(File(peer, "unicode-scale/nested/résumé.txt"), seedAdmission)),
                "normalized separator aliases preserve ordinal Unicode collision rejection");
            await db.CreateOperationAsync(File(first, "unicode-scale/Café.txt"), seedAdmission);
            await db.CreateOperationAsync(File(peer, "unicode-scale/Cafe\u0301.txt"), seedAdmission);
            Require(true, "distinct Unicode normalization forms preserve the existing ordinal path contract");
        }
        finally { SyncAuthorityDatabase.ReservationCandidateSelectionForTest = previousSelection; }
        await db.VerifyHealthAsync();
        Require(true, "all reservation rejection/commit boundaries leave the real SQLite database healthy");
        Console.WriteLine("TIMING reservation candidates: " +
            System.Text.Json.JsonSerializer.Serialize(new { snapshots = snapshots.ToArray(), commits = commits.ToArray() }));
        return checks;
    }
}

