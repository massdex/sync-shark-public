using System.Net;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Checks for DropboxSide (`--engine-checks dropbox`), written by DropboxSide's writer.</summary>
public static class DropboxSideChecks
{
    private const string DeletedTime = "2026-01-02T03:04:05Z";
    private static readonly string Hash = new string('a', 64);
    private static readonly DateTimeOffset DeletedAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly EngineSettings Settings = new("{name}", "yyyyMMdd", "local", "dropbox", ".case",
        RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins, 1, "", 262144, ScanTestSettings.BatchRows);

    // One ordinary request-attempt context per listing read, as the pair session composes it.
    private static ListingReadContext BeginListing() => new(
        () => new RequestAttempt(new object(), AttemptOwner.Request),
        (_, _) => Task.FromResult(false), static () => ValueTask.CompletedTask);

    public static void Run()
    {
        var settings = new EngineSettings("{name}", "yyyyMMdd", "local", "dropbox", ".case",
            RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins, 1, "", 262144, ScanTestSettings.BatchRows);
        foreach (var root in new[] { "/", "/Parent" })
        {
            var pair = new PairConfig("pair", "Pair A", Path.GetTempPath(), root, "connection", [], [],
                FirstRunPolicy.NewestWins, true, null);
            var scope = new PathScope(pair, [pair]);
            // Hash lookup must not dereference the API or download content.
            ISide side = new DropboxSide(null!, pair, scope, settings, new DropboxListing(null!, pair, scope))
                .WithListingContext(BeginListing);
            Check.Require(side.Side == Side.Dropbox, "DropboxSide identifies the Dropbox side.");

            foreach (var hash in new[] { "", new string('a', 64), new string('b', 64) })
            {
                var entry = new Entry("Child.txt", false, 1, DateTimeOffset.UnixEpoch, "id:file", "revision",
                    hash, null);
                var result = side.ContentHashAsync(entry, CancellationToken.None);
                Check.Require(result.IsCompletedSuccessfully, "A listed content hash is available without I/O.");
                Check.Require(result.GetAwaiter().GetResult() == hash,
                    "ContentHashAsync returns the supplied content hash unchanged, including an empty hash.");
            }
        }

        // The listing and writer forwarders keep their own checks; the root and evidence forwarders are checked here.
        ForwardingAsync().GetAwaiter().GetResult();
    }

    private static async Task ForwardingAsync()
    {
        await RootIdentityAsync();
        await DeletionEvidenceAsync();
        await RecoveryFolderReadsAsync();
    }

    private static async Task RecoveryFolderReadsAsync()
    {
        var operations = new List<object>();
        var completions = 0;
        Task<bool> RetryTransient(RequestAttempt attempt, CancellationToken ct)
        {
            completions++;
            operations.Add(attempt.Operation);
            if (attempt.Completion is not { } completion || !attempt.TryClaimCompletion())
                return Task.FromResult(false);
            if (completion.Result != RequestResult.TransientFailure) return Task.FromResult(false);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }

        // A158: success calls complete once and ends the episode.
        using (var fixture = new Fixture(Pair(), _ => Json(new { cursor = "c0" })))
        {
            completions = 0;
            var cursor = await fixture.Side.CaptureFolderCursorAsync(RetryTransient, default);
            Check.Require(cursor == "c0" && completions == 1 && fixture.Handler.Requests.Count == 1,
                "A successful recovery read completes its one episode.");
        }

        // A162: a transient capture failure retries once on the same operation.
        using (var fixture = new Fixture(Pair(),
            _ => throw new HttpRequestException(nameof(RetryTransient)),
            _ => Json(new { cursor = "c0" })))
        {
            completions = 0;
            var cursor = await fixture.Side.CaptureFolderCursorAsync(RetryTransient, default);
            Check.Require(cursor == "c0" && completions == 2 && fixture.Handler.Requests.Count == 2 &&
                ReferenceEquals(operations[^1], operations[^2]),
                "A transient capture read retries on the same episode and attempt key.");
        }

        // A157: a transient later page retries that page; completed pages are not repeated.
        using (var fixture = new Fixture(Pair(),
            _ => Page(true, "c1", File("/Parent", "First")),
            _ => throw new HttpRequestException(nameof(RetryTransient)),
            _ => Page(false, "c2", File("/Parent", "Second"))))
        {
            completions = 0;
            var seen = new List<string>();
            await foreach (var listed in fixture.Side.ReadFolderChangesAsync("c0", RetryTransient, default))
                seen.Add(listed.Path);
            Check.Require(seen.SequenceEqual(new[] { "First", "Second" }) && fixture.Handler.Requests.Count == 3,
                "A transient later page retries the same page; the completed page is not repeated.");
            Check.Require(fixture.Handler.Requests[1].Body.Contains("\"c1\"", StringComparison.Ordinal) &&
                fixture.Handler.Requests[2].Body.Contains("\"c1\"", StringComparison.Ordinal),
                "The retry resends the same page cursor.");
        }

        // A160: a permanent failure is retained after one attempt.
        using (var fixture = new Fixture(Pair(), _ => Refused(HttpStatusCode.Conflict, "path/not_found/..")))
        {
            completions = 0;
            Exception? caught = null;
            try { await fixture.Side.CaptureFolderCursorAsync(RetryTransient, default); }
            catch (Exception error) { caught = error; }
            Check.Require(caught is not null and not OperationCanceledException &&
                fixture.Handler.Requests.Count == 1 && completions == 1,
                "A permanent recovery read is retained, never retried.");
        }

        // A159: cancellation stops before another send.
        using (var cancelled = new CancellationTokenSource())
        using (var fixture = new Fixture(Pair()))
        {
            cancelled.Cancel();
            await ExpectThrowsAsync<OperationCanceledException>(
                () => fixture.Side.CaptureFolderCursorAsync(RetryTransient, cancelled.Token),
                "A cancelled recovery read stops.");
            Check.Require(fixture.Handler.Requests.Count == 0, "Cancellation sends no request and resends nothing.");
        }

        // A161: two independent reads get their own episode key.
        operations.Clear();
        using (var fixture = new Fixture(Pair(),
            _ => Json(new { cursor = "c0" }), _ => Json(new { cursor = "c1" })))
        {
            await fixture.Side.CaptureFolderCursorAsync(RetryTransient, default);
            await fixture.Side.CaptureFolderCursorAsync(RetryTransient, default);
        }
        Check.Require(operations.Count == 2 && !ReferenceEquals(operations[0], operations[1]),
            "Two independent recovery reads share no failure episode.");
    }

    private static async Task RootIdentityAsync()
    {
        // A namespace root is identified from the fresh account reply alone; get_metadata at the root is unsupported.
        using (var fixture = new Fixture(Pair("/"), _ => Account()))
        {
            var identity = await fixture.Side.ReadRootIdentityAsync(default);
            var request = fixture.Handler.Requests.Single();
            Check.Require(identity == new DropboxRootIdentity("account", "rootns", "homens",
                    PilotPath.DropboxNamespaceRootIdentity("rootns")) &&
                request.Body == "null" &&
                request.Path.EndsWith(DropboxProtocol.GetCurrentAccount, StringComparison.Ordinal),
                "A namespace root returns the typed account identity, sends the null argument and reads no metadata.");
        }

        // An ordinary root is identified by fresh metadata at the configured absolute path.
        using (var fixture = new Fixture(Pair("/Parent"), _ => Account(),
            _ => Json(FolderMetadata("/Parent", "id:folder"))))
        {
            var identity = await fixture.Side.ReadRootIdentityAsync(default);
            using var argument = JsonDocument.Parse(fixture.Handler.Requests[1].Body);
            Check.Require(identity == new DropboxRootIdentity("account", "rootns", "homens", "id:folder") &&
                fixture.Handler.Requests.Count == 2 &&
                fixture.Handler.Requests[1].Path.EndsWith(DropboxProtocol.GetMetadata, StringComparison.Ordinal) &&
                argument.RootElement.GetProperty("path").GetString() == "/Parent" &&
                !argument.RootElement.GetProperty("include_deleted").GetBoolean(),
                "An ordinary root reads fresh metadata at its configured location and returns that folder identity.");
        }

        await ExpectThrowsAsync<ChangedUnderneathException>(
            () => RootFailureAsync(_ => Account(), _ => Json(FileMetadata("/Parent", "id:file"))),
            "A file at the configured root path is refused, never returned as the root folder.");
        await ExpectThrowsAsync<ChangedUnderneathException>(
            () => RootFailureAsync(_ => Account(), _ => Json(FolderMetadata("/Parent", ""))),
            "An empty folder identity is refused.");
        await ExpectThrowsAsync<ChangedUnderneathException>(
            () => RootFailureAsync(_ => Account(), _ => Json(FolderMetadata("/Parent/Other", "id:folder"))),
            "Metadata for a different configured location is refused.");
        await ExpectThrowsAsync<HttpRequestException>(
            () => RootFailureAsync(_ => Refused(HttpStatusCode.Conflict, "invalid_account/..")),
            "A refused account read is surfaced, not turned into a root identity.");
        await ExpectThrowsAsync<HttpRequestException>(
            () => RootFailureAsync(_ => Account(), _ => Refused(HttpStatusCode.Conflict, "path/not_file/..")),
            "A refused root metadata read is surfaced, not turned into a root identity.");

        using (var cancelled = new CancellationTokenSource())
        using (var fixture = new Fixture(Pair("/Parent")))
        {
            cancelled.Cancel();
            await ExpectThrowsAsync<OperationCanceledException>(
                () => fixture.Side.ReadRootIdentityAsync(cancelled.Token),
                "A pre-cancelled root read stops rather than returning an identity.");
            Check.Require(fixture.Handler.Requests.Count == 0, "A pre-cancelled root read sends nothing.");
        }
    }

    private static async Task RootFailureAsync(params Func<CancellationToken, HttpResponseMessage>[] replies)
    {
        using var fixture = new Fixture(Pair("/Parent"), replies);
        await fixture.Side.ReadRootIdentityAsync(default);
    }

    private static async Task DeletionEvidenceAsync()
    {
        await DeletionBoundaryAsync();
        await DeletionLiveAsync();
        await DeletionFileAsync();
        await DeletionMovedAndRestoredAsync();
        await DeletionFolderAsync();
    }

    private static async Task DeletionBoundaryAsync()
    {
        // An unbound pair has no authoritative scope to read evidence in.
        using (var fixture = new Fixture(Pair("/Parent")))
        {
            var unknown = Evidence<DeletionEvidence.Unknown>(
                await fixture.Side.ReadDeletionEvidenceAsync("Item.txt", false, default),
                "An unbound pair cannot supply deletion evidence.");
            Check.Require(unknown.Cause == TextKey.RootUnbound && fixture.Handler.Requests.Count == 0,
                "An unbound pair returns Unknown(RootUnbound) without a provider request.");
        }

        // An empty or untransferable path is never sent to the provider.
        foreach (var path in new[] { "", "CON" })
        {
            using var fixture = new Fixture(BoundPair());
            var unknown = Evidence<DeletionEvidence.Unknown>(
                await fixture.Side.ReadDeletionEvidenceAsync(path, false, default),
                "An empty or untransferable path cannot supply deletion evidence.");
            Check.Require(unknown.Cause == TextKey.SkipReservedName && fixture.Handler.Requests.Count == 0,
                "An empty or untransferable path returns Unknown(SkipReservedName) without a provider request.");
        }

        // Cancellation propagates; it never becomes an evidence variant.
        using (var cancelled = new CancellationTokenSource())
        using (var fixture = new Fixture(BoundPair()))
        {
            cancelled.Cancel();
            await ExpectThrowsAsync<OperationCanceledException>(
                () => fixture.Side.ReadDeletionEvidenceAsync("Item.txt", false, cancelled.Token),
                "A pre-cancelled evidence read stops rather than returning Unknown.");
            Check.Require(fixture.Handler.Requests.Count == 0, "A pre-cancelled evidence read sends nothing.");
        }
    }

    private static async Task DeletionLiveAsync()
    {
        // A live incarnation at the exact target defeats deletion.
        using (var fixture = new Fixture(BoundPair(),
            _ => Json(FileMetadata("/Parent/Item.txt", "id:item")),
            _ => Json(FileMetadata("/Parent/Item.txt", "id:item"))))
        {
            var live = Evidence<DeletionEvidence.LiveAtTarget>(
                await fixture.Side.ReadDeletionEvidenceAsync("Item.txt", false, default),
                "A live incarnation at the exact target is LiveAtTarget.");
            Check.Require(live.Location.Domain == "namespace" && live.Location.NativePath == "/Parent/Item.txt" &&
                fixture.Handler.Requests.Count == 2,
                "LiveAtTarget keeps the shared identity domain and native path.");
        }

        // A live incarnation elsewhere is a moved survivor, preserved intact even outside this pair.
        using (var fixture = new Fixture(BoundPair(),
            _ => Json(FileMetadata("/Parent/Item.txt", "id:item")),
            _ => Json(FileMetadata("/Elsewhere/Item.txt", "id:item"))))
        {
            var live = Evidence<DeletionEvidence.LiveElsewhere>(
                await fixture.Side.ReadDeletionEvidenceAsync("Item.txt", false, default),
                "A live incarnation elsewhere is LiveElsewhere.");
            Check.Require(live.Location.Domain == "namespace" && live.Location.NativePath == "/Elsewhere/Item.txt" &&
                fixture.Handler.Requests.Count == 2,
                "LiveElsewhere preserves the out-of-pair location intact.");
        }

        // A live folder with no identity cannot support a live answer.
        using (var fixture = new Fixture(BoundPair(), _ => Json(FolderMetadata("/Parent/Item", ""))))
        {
            var unknown = Evidence<DeletionEvidence.Unknown>(
                await fixture.Side.ReadDeletionEvidenceAsync("Item", true, default),
                "A live identity without an ID is not trustworthy evidence.");
            Check.Require(unknown.Cause == TextKey.ReadDropboxFailed && fixture.Handler.Requests.Count == 1,
                "An ID-less live identity returns Unknown without a by-ID read.");
        }
    }

    private static async Task DeletionFileAsync()
    {
        // A coherent deleted head plus a proved-null known-ID lookup is a substantiated deletion; the request
        // contract is one deleted metadata read, one path-mode revision head and one by-ID read.
        using (var fixture = new Fixture(BoundPair(),
            _ => PathNotFound(),
            _ => Json(History(true, DeletedTime, Head("id:gone", "/Parent/Gone.txt", contentHash: Hash))),
            _ => PathNotFound()))
        {
            var deleted = Evidence<DeletionEvidence.DeletedFile>(
                await fixture.Side.ReadDeletionEvidenceAsync("Gone.txt", false, default),
                "A coherent deleted head and a proved-null ID is DeletedFile.");
            using var metadata = JsonDocument.Parse(fixture.Handler.Requests[0].Body);
            using var history = JsonDocument.Parse(fixture.Handler.Requests[1].Body);
            using var byId = JsonDocument.Parse(fixture.Handler.Requests[2].Body);
            Check.Require(deleted.Path == "Gone.txt" && deleted.Domain == "namespace" &&
                deleted.RemoteId == "id:gone" && deleted.Revision == "rev" &&
                deleted.DeletedAt == DeletedAt && deleted.TimeUnavailable is null &&
                deleted.ContentHash == Hash && deleted.HashUnavailable is null,
                "DeletedFile carries the validated domain, identity, revision, time and hash.");
            Check.Require(fixture.Handler.Requests.Count == 3 &&
                fixture.Handler.Requests[0].Path.EndsWith(DropboxProtocol.GetMetadata, StringComparison.Ordinal) &&
                metadata.RootElement.GetProperty("path").GetString() == "/Parent/Gone.txt" &&
                metadata.RootElement.GetProperty("include_deleted").GetBoolean() &&
                fixture.Handler.Requests[1].Path.EndsWith(DropboxProtocol.ListRevisions, StringComparison.Ordinal) &&
                history.RootElement.GetProperty("path").GetString() == "/Parent/Gone.txt" &&
                history.RootElement.GetProperty("mode").GetString() == DropboxProtocol.Path &&
                history.RootElement.GetProperty("limit").GetInt32() == 1 &&
                !history.RootElement.TryGetProperty("before_rev", out _) &&
                fixture.Handler.Requests[2].Path.EndsWith(DropboxProtocol.GetMetadata, StringComparison.Ordinal) &&
                byId.RootElement.GetProperty("path").GetString() == "id:gone" &&
                !byId.RootElement.GetProperty("include_deleted").GetBoolean(),
                "The file reader reads deleted metadata, one path-mode revision head and one by-ID absence.");
        }

        // A valid time with a missing history hash keeps the coherent remainder usable.
        var (missingHash, _) = await ReadFileAsync(_ => PathNotFound(),
            _ => Json(History(true, DeletedTime, Head("id:gone", "/Parent/Gone.txt"))),
            _ => PathNotFound());
        var withoutHash = Evidence<DeletionEvidence.DeletedFile>(missingHash,
            "A missing history hash must not discard the valid time.");
        Check.Require(withoutHash.ContentHash is null && withoutHash.HashUnavailable == TextKey.DeletionHashMissing &&
            withoutHash.DeletedAt == DeletedAt && withoutHash.TimeUnavailable is null,
            "A missing history hash keeps the valid time and names only the hash as unavailable.");

        // A missing time with a valid history hash keeps the coherent remainder usable.
        var (missingTime, _) = await ReadFileAsync(_ => PathNotFound(),
            _ => Json(History(true, null, Head("id:gone", "/Parent/Gone.txt", contentHash: Hash))),
            _ => PathNotFound());
        var withoutTime = Evidence<DeletionEvidence.DeletedFile>(missingTime,
            "A missing history time must not discard the valid hash.");
        Check.Require(withoutTime.DeletedAt is null && withoutTime.TimeUnavailable == TextKey.DeletionTimeMissing &&
            withoutTime.ContentHash == Hash && withoutTime.HashUnavailable is null,
            "A missing history time keeps the valid hash and names only the time as unavailable.");

        // A malformed field stays field-local; it never erases an independently valid sibling.
        var (malformedTime, _) = await ReadFileAsync(_ => PathNotFound(),
            _ => Json(History(true, 5, Head("id:gone", "/Parent/Gone.txt", contentHash: Hash))),
            _ => PathNotFound());
        var badTime = Evidence<DeletionEvidence.DeletedFile>(malformedTime,
            "A malformed history time must not erase the valid hash.");
        Check.Require(badTime.TimeUnavailable == TextKey.DeletionTimeMalformed && badTime.ContentHash == Hash &&
            badTime.DeletedAt is null,
            "A malformed history time keeps the valid hash and names the time as malformed.");

        var (malformedHash, _) = await ReadFileAsync(_ => PathNotFound(),
            _ => Json(History(true, DeletedTime, Head("id:gone", "/Parent/Gone.txt", contentHash: 5))),
            _ => PathNotFound());
        var badHash = Evidence<DeletionEvidence.DeletedFile>(malformedHash,
            "A malformed history hash must not erase the valid time.");
        Check.Require(badHash.HashUnavailable == TextKey.DeletionHashMalformed && badHash.DeletedAt == DeletedAt &&
            badHash.ContentHash is null,
            "A malformed history hash keeps the valid time and names the hash as malformed.");

        // A head with no usable ID after history handling is a non-destructive history failure.
        var (noId, noIdRequests) = await ReadFileAsync(_ => PathNotFound(),
            _ => Json(History(true, DeletedTime, Head(null, "/Parent/Gone.txt"))));
        var unavailable = Evidence<DeletionEvidence.HistoryUnavailable>(noId,
            "A head without an ID is HistoryUnavailable, never DeletedFile.");
        Check.Require(unavailable.Cause == TextKey.ReadDropboxFailed && noIdRequests.Count == 2,
            "A head without an ID is HistoryUnavailable and issues no by-ID read.");

        // A failed lookup with a KNOWN ID is an identity failure, never a deletion.
        var (failedId, _) = await ReadFileAsync(_ => PathNotFound(),
            _ => Json(History(true, DeletedTime, Head("id:gone", "/Parent/Gone.txt"))),
            _ => Refused(HttpStatusCode.Conflict, "path/not_file/.."));
        var failed = Evidence<DeletionEvidence.Unknown>(failedId,
            "A failed known-ID lookup is Unknown, never a deletion.");
        Check.Require(failed.Cause == TextKey.DropboxRefused,
            "A failed known-ID lookup stays Unknown with the by-ID read's mapped refusal, never a deletion.");

        // A non-deleted head, or a head for another path, cannot construct a deletion.
        var (notDeleted, _) = await ReadFileAsync(_ => PathNotFound(),
            _ => Json(History(false, DeletedTime, Head("id:gone", "/Parent/Gone.txt"))),
            _ => PathNotFound());
        Check.Require(Evidence<DeletionEvidence.Unknown>(notDeleted, "A non-deleted head is not a deletion.").Cause ==
            TextKey.FailChanged, "A non-deleted head is Unknown(FailChanged).");

        var (otherPath, _) = await ReadFileAsync(_ => PathNotFound(),
            _ => Json(History(true, DeletedTime, Head("id:gone", "/Parent/Other.txt"))),
            _ => PathNotFound());
        Check.Require(Evidence<DeletionEvidence.Unknown>(otherPath, "A head for another path is not a deletion.").Cause ==
            TextKey.FailChanged, "A head whose native path differs is Unknown(FailChanged).");

        // Only the documented structured miss is a successful history negative; a generic miss or permission
        // refusal is not, and neither is absence itself.
        var (noHistory, noHistoryRequests) = await ReadFileAsync(_ => PathNotFound(), _ => PathNotFound());
        Check.Require(noHistory is DeletionEvidence.NoRetainedHistory && noHistoryRequests.Count == 2,
            "A documented path-mode history miss is NoRetainedHistory.");

        var (genericMiss, _) = await ReadFileAsync(_ => PathNotFound(),
            _ => Refused(HttpStatusCode.Conflict, "path/not_found/.."));
        Check.Require(Evidence<DeletionEvidence.Unknown>(genericMiss, "A generic miss is not NoRetainedHistory.").Cause ==
            TextKey.ReadDropboxFailed, "A generic 409 miss stays Unknown rather than NoRetainedHistory.");

        var (permission, permissionRequests) = await ReadFileAsync(
            _ => Refused(HttpStatusCode.Forbidden, "no_permission/.."));
        Check.Require(Evidence<DeletionEvidence.Unknown>(permission, "A permission refusal is not absence.").Cause ==
            TextKey.ReadDropboxFailed && permissionRequests.Count == 1,
            "A permission refusal is Unknown and never read as absence or deletion.");
    }

    private static async Task DeletionMovedAndRestoredAsync()
    {
        // A moved incarnation still carrying is_deleted and server_deleted is LiveElsewhere; nothing is written.
        using (var fixture = new Fixture(BoundPair(),
            _ => PathNotFound(),
            _ => Json(History(true, DeletedTime, Head("id:moved", "/Parent/Gone.txt"))),
            _ => Json(FileMetadata("/Elsewhere/Gone.txt", "id:moved"))))
        {
            var live = Evidence<DeletionEvidence.LiveElsewhere>(
                await fixture.Side.ReadDeletionEvidenceAsync("Gone.txt", false, default),
                "A moved same-ID incarnation defeats a deleted head.");
            Check.Require(live.Location.NativePath == "/Elsewhere/Gone.txt" &&
                fixture.Handler.Requests.All(request =>
                    request.Path.EndsWith(DropboxProtocol.GetMetadata, StringComparison.Ordinal) ||
                    request.Path.EndsWith(DropboxProtocol.ListRevisions, StringComparison.Ordinal)),
                "A moved survivor is reported with no upload or delete request.");
        }

        // The reader keeps no evidence cache: a later restoration is observed by fresh reads.
        using (var fixture = new Fixture(BoundPair(),
            _ => PathNotFound(),
            _ => Json(History(true, DeletedTime, Head("id:gone", "/Parent/Gone.txt", contentHash: Hash))),
            _ => PathNotFound(),
            _ => Json(FileMetadata("/Parent/Gone.txt", "id:restored")),
            _ => Json(FileMetadata("/Parent/Gone.txt", "id:restored"))))
        {
            var first = Evidence<DeletionEvidence.DeletedFile>(
                await fixture.Side.ReadDeletionEvidenceAsync("Gone.txt", false, default),
                "The first read observes the deletion.");
            var second = Evidence<DeletionEvidence.LiveAtTarget>(
                await fixture.Side.ReadDeletionEvidenceAsync("Gone.txt", false, default),
                "A restoration invalidates the earlier evidence through a fresh read.");
            Check.Require(first.RemoteId == "id:gone" && second.Location.Entry.Id == "id:restored" &&
                fixture.Handler.Requests.Count == 5,
                "Restored evidence is re-observed, never served from an earlier read.");
        }
    }

    private static async Task DeletionFolderAsync()
    {
        // An exact tombstone is a deleted path; a prefix-colliding sibling is never queried or used.
        using (var fixture = new Fixture(BoundPair(), _ => Json(Tombstone("/Parent/Gone"))))
        {
            var deleted = Evidence<DeletionEvidence.DeletedPath>(
                await fixture.Side.ReadDeletionEvidenceAsync("Gone", true, default),
                "An exact folder tombstone is DeletedPath.");
            using var request = JsonDocument.Parse(fixture.Handler.Requests.Single().Body);
            Check.Require(deleted.Path == "Gone" && deleted.TombstonePath == "Gone" &&
                fixture.Handler.Requests.Count == 1 &&
                request.RootElement.GetProperty("path").GetString() == "/Parent/Gone",
                "An exact tombstone names the exact path and never a prefix-colliding sibling.");
        }

        // A proper ancestor tombstone is DeletedPath with the nearest validated ancestor.
        using (var fixture = new Fixture(BoundPair(), _ => PathNotFound(), _ => Json(Tombstone("/Parent/A"))))
        {
            var deleted = Evidence<DeletionEvidence.DeletedPath>(
                await fixture.Side.ReadDeletionEvidenceAsync("A/B", true, default),
                "An ancestor tombstone is DeletedPath with the nearest tombstone.");
            Check.Require(deleted.Path == "A/B" && deleted.TombstonePath == "A" &&
                fixture.Handler.Requests.Count == 2,
                "A missing exact path falls back to the nearest ancestor tombstone.");
        }

        // No tombstone only after every necessary exact and ancestor lookup answered.
        using (var fixture = new Fixture(BoundPair(), _ => PathNotFound(), _ => PathNotFound()))
        {
            var none = Evidence<DeletionEvidence.NoFolderTombstone>(
                await fixture.Side.ReadDeletionEvidenceAsync("A/B", true, default),
                "Validated negative exact and ancestor answers are NoFolderTombstone.");
            using var exact = JsonDocument.Parse(fixture.Handler.Requests[0].Body);
            using var ancestor = JsonDocument.Parse(fixture.Handler.Requests[1].Body);
            Check.Require(none.Path == "A/B" && fixture.Handler.Requests.Count == 2 &&
                exact.RootElement.GetProperty("path").GetString() == "/Parent/A/B" &&
                ancestor.RootElement.GetProperty("path").GetString() == "/Parent/A",
                "NoFolderTombstone is reached only after the exact path and its ancestors were queried.");
        }

        // A recreated live ancestor invalidates its marker instead of proving deletion.
        using (var fixture = new Fixture(BoundPair(),
            _ => PathNotFound(),
            _ => Json(FolderMetadata("/Parent/A", "id:a"))))
        {
            var none = Evidence<DeletionEvidence.NoFolderTombstone>(
                await fixture.Side.ReadDeletionEvidenceAsync("A/B", true, default),
                "A recreated live ancestor invalidates its marker rather than proving deletion.");
            Check.Require(none.Path == "A/B" && fixture.Handler.Requests.Count == 2,
                "A live ancestor is not a tombstone and adds no unexpected request.");
        }

        // A failed ancestor lookup is not a validated negative answer.
        using (var fixture = new Fixture(BoundPair(),
            _ => PathNotFound(),
            _ => Refused(HttpStatusCode.Forbidden, "no_permission/..")))
        {
            var unknown = Evidence<DeletionEvidence.Unknown>(
                await fixture.Side.ReadDeletionEvidenceAsync("A/B", true, default),
                "A failed ancestor lookup is Unknown, never NoFolderTombstone.");
            Check.Require(unknown.Cause == TextKey.ReadDropboxFailed,
                "A failed ancestor lookup keeps the failure as Unknown.");
        }

        // A live folder at the exact target is a live location, confirmed by the shared by-ID read.
        using (var fixture = new Fixture(BoundPair(),
            _ => Json(FolderMetadata("/Parent/Folder", "id:folder")),
            _ => Json(FolderMetadata("/Parent/Folder", "id:folder"))))
        {
            var live = Evidence<DeletionEvidence.LiveAtTarget>(
                await fixture.Side.ReadDeletionEvidenceAsync("Folder", true, default),
                "A live folder at the exact target is LiveAtTarget.");
            Check.Require(live.Location.Entry.IsFolder && fixture.Handler.Requests.Count == 2,
                "A live folder keeps its kind and is confirmed through the shared by-ID read.");
        }
    }

    private static async Task<(DeletionEvidence Evidence, List<(string Path, string Body)> Requests)> ReadFileAsync(
        params Func<CancellationToken, HttpResponseMessage>[] replies)
    {
        using var fixture = new Fixture(BoundPair(), replies);
        var evidence = await fixture.Side.ReadDeletionEvidenceAsync("Gone.txt", false, default);
        return (evidence, fixture.Handler.Requests);
    }

    private static async Task ExpectThrowsAsync<T>(Func<Task> action, string what) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        Check.Require(false, what);
    }

    private static T Evidence<T>(DeletionEvidence evidence, string what) where T : DeletionEvidence =>
        evidence as T ?? throw new InvalidOperationException(what);

    private static PairConfig Pair(string root = "/Parent") => new("pair", "Pair A", Path.GetTempPath(), root,
        "connection", [], [], FirstRunPolicy.NewestWins, true, null);

    private static PairConfig BoundPair(string root = "/Parent") => Pair(root) with
    {
        Roots = new RootBinding(@"C:\Local", root, "connection", new DirectoryIdentity(0, 0, null),
            new DropboxRootIdentity("account", "namespace", "home", "id:folder"))
    };

    private static HttpResponseMessage Account() => Json(new Dictionary<string, object?>
    {
        [DropboxProtocol.AccountId] = "account",
        [DropboxProtocol.RootInfo] = new Dictionary<string, object?>
        {
            [DropboxProtocol.RootNamespaceId] = "rootns",
            [DropboxProtocol.HomeNamespaceId] = "homens"
        }
    });

    private static object FolderMetadata(string pathDisplay, string id) => new Dictionary<string, object?>
    {
        [DropboxProtocol.Tag] = DropboxProtocol.TagFolder,
        [DropboxProtocol.PathDisplay] = pathDisplay,
        [DropboxProtocol.Id] = id
    };

    private static object FileMetadata(string pathDisplay, string id) => new Dictionary<string, object?>
    {
        [DropboxProtocol.Tag] = DropboxProtocol.TagFile,
        [DropboxProtocol.PathDisplay] = pathDisplay,
        [DropboxProtocol.Id] = id,
        [DropboxProtocol.Rev] = "rev",
        [DropboxProtocol.Size] = 1L,
        [DropboxProtocol.ClientModified] = DeletedTime
    };

    private static object Tombstone(string pathDisplay) => new Dictionary<string, object?>
    {
        [DropboxProtocol.Tag] = DropboxProtocol.TagDeleted,
        [DropboxProtocol.PathDisplay] = pathDisplay
    };

    private static object History(bool isDeleted, object? serverDeleted, params object[] entries)
    {
        var history = new Dictionary<string, object?>
        {
            [DropboxProtocol.IsDeleted] = isDeleted,
            [DropboxProtocol.Entries] = entries
        };
        if (serverDeleted is not null) history[DropboxProtocol.ServerDeleted] = serverDeleted;
        return history;
    }

    private static Dictionary<string, object?> Head(string? id, string? pathDisplay, string revision = "rev",
        object? serverDeleted = null, object? contentHash = null)
    {
        var head = new Dictionary<string, object?>
        {
            [DropboxProtocol.Rev] = revision,
            [DropboxProtocol.PathDisplay] = pathDisplay
        };
        if (id is not null) head[DropboxProtocol.Id] = id;
        if (serverDeleted is not null) head[DropboxProtocol.ServerDeleted] = serverDeleted;
        if (contentHash is not null) head[DropboxProtocol.ContentHash] = contentHash;
        return head;
    }

    // The documented list_revisions path-mode miss is a structured path/not_found; its summary keeps by-ID mapping working.
    private static HttpResponseMessage PathNotFound() => Json(new Dictionary<string, object?>
    {
        [DropboxProtocol.ErrorSummary] = DropboxProtocol.PathNotFound + "/..",
        [DropboxProtocol.Error] = new Dictionary<string, object?>
        {
            [DropboxProtocol.Tag] = DropboxProtocol.Path,
            [DropboxProtocol.Path] = new Dictionary<string, object?>
            {
                [DropboxProtocol.Tag] = "not_found"
            }
        }
    }, HttpStatusCode.Conflict);

    private static HttpResponseMessage Refused(HttpStatusCode status, string summary) =>
        Json(new Dictionary<string, object?> { [DropboxProtocol.ErrorSummary] = summary }, status);

    private static HttpResponseMessage Json(object data, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };

    private static Dictionary<string, object?> File(string root, string path) => new()
    {
        [DropboxProtocol.Tag] = DropboxProtocol.TagFile,
        [DropboxProtocol.PathDisplay] = root.TrimEnd('/') + "/" + path,
        [DropboxProtocol.Id] = "id:" + path,
        [DropboxProtocol.Rev] = "rev",
        [DropboxProtocol.Size] = 1L,
        [DropboxProtocol.ClientModified] = "2026-01-01T00:00:00Z",
        [DropboxProtocol.ContentHash] = new string('a', 64)
    };

    private static HttpResponseMessage Page(bool more, string cursor, params object[] entries) =>
        Json(new { entries, cursor, has_more = more });

    private sealed class Fixture : IDisposable
    {
        // Zero delays keep the retry path immediate; no fixture here reaches it, because every refusal is permanent.
        private static readonly FailureRetryOwner.BackoffSettings NoDelay = new(0, 1, 0, 0);
        private readonly HttpClient _http;
        public Handler Handler { get; }
        public DropboxSide Side { get; }

        public Fixture(PairConfig pair, params Func<CancellationToken, HttpResponseMessage>[] replies)
        {
            Handler = new Handler(replies);
            _http = new HttpClient(Handler);
            var network = new FakeNetworkSignal();
            network.SetAvailable(true);
            var time = new FakeTimeProvider();
            var limiter = new AdaptiveLimiter(1, 1, time, network, _ => { }, NoDelay);
            var api = new DropboxApi(_http, new Tokens(), "namespace", limiter,
                _ => new ValueTask<Stream>(new MemoryStream()), NoDelay, time, "account", "source");
            var scope = new PathScope(pair, [pair]);
            Side = new DropboxSide(api, pair, scope, Settings, new DropboxListing(api, pair, scope));
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class Handler(IEnumerable<Func<CancellationToken, HttpResponseMessage>> replies) : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, HttpResponseMessage>> _replies = new(replies);
        public List<(string Path, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync(cancellationToken)));
            Check.Require(_replies.Count != 0, "Unexpected Dropbox request: the fixture has no reply left.");
            return _replies.Dequeue()(cancellationToken);
        }
    }

    private sealed class Tokens : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("test-token");
    }
}
