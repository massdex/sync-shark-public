using System.Net;
using System.Text;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task EvidenceValidationReviewChecksAsync()
    {
        // Decision 69: the last revision's content_hash travels with the deletion evidence and cache.
        var contentHash = DropboxContentHash.Compute(Encoding.UTF8.GetBytes("identical content"));
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            h.Wire.RevisionContentHash = contentHash;
            var prepared = await h.Inventory.PrepareDeletionEvidenceAsync(["gone.txt"], CancellationToken.None);
            Require(prepared.Pending.SequenceEqual(["gone.txt"]), "a fresh revision probe starts held");
            SyncRemoteDeletionEvidence? resolved = null;
            await h.Inventory.ResolveDeletionEvidenceAsync(["gone.txt"],
                (path, evidence) => resolved = evidence, CancellationToken.None);
            Require(resolved is { DeletedUtc: not null } && resolved.ContentHash == contentHash,
                "the last revision's content_hash travels with the deletion time");
            Require(h.Known.DeletionEvidenceRows("p1", RootId).Single().ContentHash == contentHash,
                "the deletion-evidence cache persists the last revision's content hash");
            var again = await h.Inventory.PrepareDeletionEvidenceAsync(["gone.txt"], CancellationToken.None);
            Require(again.Pending.Count == 0 && again.Resolved["gone.txt"].ContentHash == contentHash,
                "a cached row returns the persisted content hash");
            var times = await h.Inventory.DeletionTimesAsync(["gone.txt"], CancellationToken.None);
            Require(times["gone.txt"] == new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero),
                "the legacy deletion-time view is unchanged");
        }
        // Decision 78: a legacy positive cache row without a hash is re-probed once, not reused forever.
        foreach (var refreshedHash in new string?[] { contentHash, null })
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            h.Wire.RevisionContentHash = refreshedHash;
            await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            h.Known.StoreDeletionEvidence("p1", RootId,
                [new("gone.txt", DateTimeOffset.Parse("2026-10-12T08:00:00Z"), "saved")]);
            var prepared = await h.Inventory.PrepareDeletionEvidenceAsync(["gone.txt"], CancellationToken.None);
            Require(prepared.Pending.SequenceEqual(["gone.txt"]), "hashless cached deletion is a cache miss");
            var probes = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            SyncRemoteDeletionEvidence? resolved = null;
            await h.Inventory.ResolveDeletionEvidenceAsync(["gone.txt"],
                (_, evidence) => resolved = evidence, CancellationToken.None);
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == probes + 1 &&
                resolved is { DeletedUtc: not null } && resolved.ContentHash == refreshedHash,
                "legacy evidence gets one history probe; an unknown refreshed hash remains unknown");
        }
        // list_revisions is newest first: the first entry is the last revision, whatever server_modified says.
        var firstHash = new string('a', 64);
        var olderHash = new string('b', 64);
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            h.Wire.RevisionResponseOverride = new
            {
                is_deleted = true,
                server_deleted = "2026-10-12T08:00:00Z",
                entries = new object[]
                {
                    new { content_hash = firstHash, server_modified = "2026-10-11T00:00:00Z" },
                    new { content_hash = olderHash, server_modified = "2026-10-12T07:00:00Z" }
                }
            };
            SyncRemoteDeletionEvidence? resolved = null;
            await h.Inventory.ResolveDeletionEvidenceAsync(["gone.txt"],
                (path, evidence) => resolved = evidence, CancellationToken.None);
            Require(resolved?.ContentHash == firstHash, "the first (newest) revision supplies its content hash");
        }
        // A hashless newest revision leaves the hash unknown; an older matching hash never substitutes, so
        // the held path stays pending.
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            h.Wire.RevisionResponseOverride = new
            {
                is_deleted = true,
                server_deleted = "2026-10-12T08:00:00Z",
                entries = new object[]
                {
                    new { server_modified = "2026-10-12T07:00:00Z" },
                    new { content_hash = firstHash, server_modified = "2026-10-11T00:00:00Z" }
                }
            };
            SyncRemoteDeletionEvidence? resolved = null;
            await h.Inventory.ResolveDeletionEvidenceAsync(["gone.txt"],
                (path, evidence) => resolved = evidence, CancellationToken.None);
            Require(resolved is not null && resolved.ContentHash is null,
                "a hashless newest revision reports no history hash; the held path stays pending");
        }
        // A malformed newest hash is unknown, never an older or partial match.
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            h.Wire.RevisionResponseOverride = new
            {
                is_deleted = true,
                server_deleted = "2026-10-12T08:00:00Z",
                entries = new object[]
                {
                    new { content_hash = "not-a-content-hash" },
                    new { content_hash = firstHash }
                }
            };
            SyncRemoteDeletionEvidence? resolved = null;
            await h.Inventory.ResolveDeletionEvidenceAsync(["gone.txt"],
                (path, evidence) => resolved = evidence, CancellationToken.None);
            Require(resolved is not null && resolved.ContentHash is null,
                "a malformed newest hash is unknown and never falls back to an older entry");
        }
        foreach (var error in new[] { "not_found", "conflict" })
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            h.Wire.RevisionErrorTag = error;
            var callbacks = new Dictionary<string, SyncRemoteDeletionEvidence>();
            await h.Inventory.ResolveDeletionEvidenceAsync(["gone.txt"], (path, time) => callbacks[path] = time,
                CancellationToken.None);
            if (error == "not_found")
            {
                Require(callbacks.TryGetValue("gone.txt", out var evidence) && evidence.DeletedUtc is null &&
                    h.Known.DeletionEvidenceRows("p1", RootId).Single().DeletedUtc is null,
                    "exact path/not_found revision response resolves and caches proven no evidence");
                Require((await h.Inventory.DeletionTimesAsync(["gone.txt"], CancellationToken.None)).Count == 0,
                    "legacy lookup accepts proven missing revision path");
            }
            else
            {
                Require(callbacks.Count == 0 && h.Known.DeletionEvidenceRows("p1", RootId).Count == 0,
                    "other revision-history 409 responses never resolve or cache absence");
                await ExpectAsync<IOException>(() => h.Inventory.DeletionTimesAsync(["gone.txt"], CancellationToken.None));
            }
        }
        foreach (var malformed in new object[]
        {
            new { server_deleted = "2026-10-12T08:00:00Z", entries = Array.Empty<object>() },
            new { is_deleted = "true", server_deleted = "2026-10-12T08:00:00Z", entries = Array.Empty<object>() },
            new { is_deleted = true, entries = Array.Empty<object>() },
            new { is_deleted = true, server_deleted = "not-a-timestamp", entries = Array.Empty<object>() }
        })
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            h.Wire.RevisionResponseOverride = malformed;
            var prepared = await h.Inventory.PrepareDeletionEvidenceAsync(["gone.txt"], CancellationToken.None);
            Require(prepared.Pending.SequenceEqual(["gone.txt"]), "malformed history starts as a held probe");
            var callbacks = new List<(string Path, SyncRemoteDeletionEvidence Evidence)>();
            var probes = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            await h.Inventory.ResolveDeletionEvidenceAsync(["gone.txt"],
                (path, evidence) => callbacks.Add((path, evidence)), CancellationToken.None);
            Require(callbacks.Count == 1 && callbacks[0].Path == "gone.txt" &&
                callbacks[0].Evidence is { LookupFailed: true, PermanentFailure: true, DeletedUtc: null, ContentHash: null } &&
                h.Known.DeletionEvidenceRows("p1", RootId).Count == 0 &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == probes + 1,
                "malformed deletion flag or timestamp answers terminal Unknown once without caching absence or success");
            var again = await h.Inventory.PrepareDeletionEvidenceAsync(["gone.txt"], CancellationToken.None);
            Require(again.Pending.SequenceEqual(["gone.txt"]) && again.Resolved.Count == 0,
                "terminal Unknown leaves the item held for review, never resolved as deletion or restoration authority");
            callbacks.Clear();
            await h.Inventory.ResolveDeletionEvidenceAsync(["gone.txt"],
                (path, evidence) => callbacks.Add((path, evidence)), CancellationToken.None);
            Require(callbacks.Count == 1 && callbacks[0].Path == "gone.txt" &&
                callbacks[0].Evidence is { LookupFailed: true, PermanentFailure: true, DeletedUtc: null, ContentHash: null } &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == probes + 2 &&
                h.Known.DeletionEvidenceRows("p1", RootId).Count == 0,
                "explicit re-resolution probes again and cannot reuse terminal Unknown as successful cached evidence");
            await ExpectAsync<IOException>(() => h.Inventory.DeletionTimesAsync(["gone.txt"], CancellationToken.None));
            Require(h.Known.DeletionEvidenceRows("p1", RootId).Count == 0,
                "legacy malformed history throws without poisoning cache");
        }
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            h.Wire.Script("/2/files/list_revisions", HttpStatusCode.BadRequest, 1);
            await ExpectAsync<IOException>(() => h.Inventory.DeletionTimesAsync(["gone.txt"], CancellationToken.None));
            h.Wire.Script("/2/files/list_revisions", HttpStatusCode.BadRequest, 1);
            h.Wire.SeedChangePage("delta-start", [h.Wire.ChangeDeleted("/Pilot/gone.txt")], false, "delta-end");
            var delta = await h.Inventory.ChangesSinceAsync("delta-start", 2, _ => true, CancellationToken.None);
            var failure = h.Known.DeletionEvidenceRows("p1", RootId).Single(row => row.Path == "gone.txt").Failure;
            Require(delta.PendingDeletionEvidence.ContainsKey("gone.txt") &&
                    failure is { Attempts: 1, Generation: 2, Released: true } &&
                    delta.ReleasedDeletionEvidence.Contains("gone.txt"),
                "HTTP 400 records held evidence and reaches the permanent-failure release bound immediately");
            Require(!delta.CursorInvalid && delta.CursorAfter == "delta-end" &&
                    delta.PendingDeletionEvidence.Keys.All(delta.ReleasedDeletionEvidence.Contains) &&
                    h.Known.SyncRetryAt("p1", "gone.txt") is
                        { Kind: "deletion-evidence", Held: not null, ReviewedActions: null },
                "released permanent failure does not pin the cursor and retains one unreviewed durable evidence hold");
        }
        // Different cache bases are conservatively reprobed without a second feed that could
        // advance classification beyond the already captured snapshot cursor.
        using (var h = new Harness())
        {
            h.Wire.RevisionContentHash = contentHash;
            h.Wire.SeedDeletedEntry("gone-a.txt");
            h.Wire.SeedDeletedEntry("gone-b.txt");
            var full = await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            var time = DateTimeOffset.Parse("2026-10-12T08:00:00Z");
            h.Known.StoreDeletionEvidence("p1", RootId,
                [new("gone-a.txt", time, "oldest", contentHash), new("gone-b.txt", time, "newer", contentHash)]);
            h.Wire.SeedChangePage("oldest", [h.Wire.ChangeFile("unrelated.txt", "x")], false, "advanced");
            // If validation tries every group, this cycle will discard the cache instead.
            h.Wire.SeedChangePage("newer", [], true, "newer");
            var lists = h.Wire.Calls.Count(call => call == "/2/files/list_folder");
            var feeds = h.Wire.Calls.Count(call => call == "/2/files/list_folder/continue");
            var prepared = await h.Inventory.PrepareDeletionEvidenceAsync(["gone-a.txt", "gone-b.txt"], CancellationToken.None);
            Require(prepared.Pending.Count == 2 && prepared.Resolved.Count == 0 &&
                h.Known.DeletionEvidenceRows("p1", RootId).Count == 0,
                "different-basis evidence is discarded, not silently advanced beyond the snapshot");
            await h.Inventory.ResolveDeletionEvidenceAsync(["gone-a.txt", "gone-b.txt"], (_, _) => { }, CancellationToken.None);
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_folder/continue") == feeds &&
                h.Wire.Calls.Count(call => call == "/2/files/list_folder") == lists &&
                h.Known.DeletionEvidenceRows("p1", RootId).All(row => row.BasisCursor == full.CursorAfter),
                "fresh probes retain the snapshot basis with no separate cache-validation feed or full rewalk");
            h.Wire.SeedDeletedEntry("newly-deleted.txt");
            h.Wire.SeedChangePage(full.CursorAfter, [h.Wire.ChangeDeleted("/Pilot/newly-deleted.txt")], false, "advanced-again");
            var revisions = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            prepared = await h.Inventory.PrepareDeletionEvidenceAsync(["newly-deleted.txt"], CancellationToken.None);
            Require(prepared.Pending.Count == 0 && prepared.Resolved["newly-deleted.txt"].DeletedUtc is null,
                "same-run preparation retains its validated classification until an inventory boundary");
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_folder/continue") == feeds &&
                h.Wire.Calls.Count(call => call == "/2/files/list_folder") == lists &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == revisions,
                "same-run preparation performs no extra feed, full listing, or revision probe");

            // A delta explicitly starts a new inventory boundary. Its delete evidence uses the
            // saved feed and resolves the new tombstone without another deleted inventory.
            var delta = await h.Inventory.ChangesSinceAsync(full.CursorAfter, 2, _ => true, CancellationToken.None);
            Require(!delta.CursorInvalid && delta.CursorAfter == "advanced-again" &&
                delta.Changes.Count == 1 && delta.Changes.Single().Kind == ChangeKind.Delete &&
                delta.Changes.Single().Path == "newly-deleted.txt",
                "an explicit delta boundary consumes the advanced cursor's new tombstone");
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_folder") == lists &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == revisions + 1,
                "the new delta probes the new tombstone once with no deleted-inventory relist");
            feeds = h.Wire.Calls.Count(call => call == "/2/files/list_folder/continue");
            prepared = await h.Inventory.PrepareDeletionEvidenceAsync(["newly-deleted.txt"], CancellationToken.None);
            Require(prepared.Pending.Count == 0 && prepared.Resolved["newly-deleted.txt"].DeletedUtc == time,
                "preparation reuses the new tombstone evidence already resolved by the delta");
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_folder/continue") == feeds &&
                h.Wire.Calls.Count(call => call == "/2/files/list_folder") == lists &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == revisions + 1,
                "preparation after the delta adds no feed, duplicate full listing, or revision probe");
        }

        // Legacy rows without a basis cursor cannot validate or displace the fresh full-listing classification.
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            h.Wire.SeedFile("live.txt", "keep");
            await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            var time = DateTimeOffset.Parse("2026-10-12T08:00:00Z");
            h.Known.StoreDeletionEvidence("p1", RootId,
                [new("gone.txt", null, null), new("desktop.ini", time, null),
                    new("local-only/readme.txt", null, null), new("live.txt", time, null)]);
            var lists = h.Wire.Calls.Count(call => call == "/2/files/list_folder");
            var feeds = h.Wire.Calls.Count(call => call == "/2/files/list_folder/continue");
            var revisions = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            var prepared = await h.Inventory.PrepareDeletionEvidenceAsync(
                ["gone.txt", "desktop.ini", "local-only/readme.txt", "live.txt"], CancellationToken.None);
            Require(h.Known.DeletionEvidenceRows("p1", RootId).Count == 0,
                "NULL-basis cache rows are discarded rather than reused or assigned a fresh cursor");
            Require(prepared.Pending.SequenceEqual(["gone.txt"]) && prepared.Resolved.Count == 3 &&
                new[] { "desktop.ini", "local-only/readme.txt", "live.txt" }.All(path =>
                    prepared.Resolved.TryGetValue(path, out var evidence) &&
                    evidence.DeletedUtc is null && evidence.ContentHash is null),
                "fresh listing holds only its tombstone and resolves unrelated paths without evidence");
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_folder/continue") == feeds &&
                h.Wire.Calls.Count(call => call == "/2/files/list_folder") == lists &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == revisions,
                "NULL-basis rows need no feed request, duplicate full listing, or revision probe");
        }

        // Multi-cursor cycles invalidate cached rows, not the fresh classification: hold fresh probes.
        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            h.Known.StoreDeletionEvidence("p1", RootId, [new("gone.txt", null, "cycle-a")]);
            h.Wire.SeedChangePage("cycle-a", [], true, "cycle-b");
            h.Wire.SeedChangePage("cycle-b", [], true, "cycle-a");
            var lists = h.Wire.Calls.Count(call => call == "/2/files/list_folder");
            var prepared = await h.Inventory.PrepareDeletionEvidenceAsync(["gone.txt"], CancellationToken.None);
            Require(h.Known.DeletionEvidenceRows("p1", RootId).Count == 0 &&
                prepared.Pending.SequenceEqual(["gone.txt"]), "invalid feed removes cache and holds a fresh probe");
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_folder") == lists,
                "invalid cache feed retains the fresh full classification without duplicate validation relisting");
        }

        using (var h = new Harness())
        {
            h.Wire.SeedDeletedEntry("gone.txt");
            await h.Inventory.FullAsync(1, _ => true, Sink, CancellationToken.None);
            var time = DateTimeOffset.Parse("2026-10-12T08:00:00Z");
            h.Known.StoreDeletionEvidence("p1", RootId,
                [new("gone.txt", time, "saved"), new("desktop.ini", time, "saved")]);
            var clock = new ResilienceChecks.Clock();
            h.Client.ReadTransientBudget = TimeSpan.FromSeconds(3);
            h.Client.ReadRetryTime = clock;
            h.Client.ReadRetryDelay = (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                clock.Advance(delay);
                return Task.CompletedTask;
            };
            h.Wire.Script("/2/files/list_folder/continue", HttpStatusCode.ServiceUnavailable, 64);
            var lists = h.Wire.Calls.Count(call => call == "/2/files/list_folder");
            var feeds = h.Wire.Calls.Count(call => call == "/2/files/list_folder/continue");
            var revisions = h.Wire.Calls.Count(call => call == "/2/files/list_revisions");
            var prepared = await h.Inventory.PrepareDeletionEvidenceAsync(["gone.txt", "desktop.ini"], CancellationToken.None);
            var rows = h.Known.DeletionEvidenceRows("p1", RootId);
            Require(rows.Count == 0,
                "different-basis cache is discarded without a potentially failing independent feed");
            Require(prepared.Pending.SequenceEqual(["gone.txt"]) && prepared.Resolved.Count == 1 &&
                prepared.Resolved.TryGetValue("desktop.ini", out var evidence) &&
                evidence.DeletedUtc is null && evidence.ContentHash is null,
                "failed cache validation reuses no saved result and holds only the fresh listing tombstone");
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_folder/continue") == feeds &&
                h.Wire.Calls.Count(call => call == "/2/files/list_folder") == lists &&
                h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == revisions,
                "transient validation failure retains full-listing classification without relisting or probing");
        }
    }
}
