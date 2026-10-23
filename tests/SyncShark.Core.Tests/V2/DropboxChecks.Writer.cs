using System.Globalization;
using System.Net;
using System.Text;

namespace Massdex.SyncShark.V2;

// The writer contract checks: conditional put/move/delete/mkdir, recovery reference and exact-revision read.
internal static partial class DropboxChecks
{
    private static int _actionSeq;

    private static async Task PutCreateAsync()
    {
        using (var small = new Harness())
        {
            var bytes = Encoding.UTF8.GetBytes("0123456789");
            var created = await small.Writer().PutAsync(PutAction("new.txt", Source("new.txt", bytes)),
                new MemoryStream(bytes), CancellationToken.None);
            Require(small.Wire.Calls.Contains("/2/files/upload_session/finish_batch_v2"), "a small create is batched");
            Require(created.Kind == ItemKind.File && created.ObjectId.Length != 0 && created.VersionId is not null &&
                created.Hash == Source("new.txt", bytes).Hash, "the created item carries id, revision and hash");
        }

        using (var casing = new Harness())
        {
            // Dropbox's displayed root casing differs from the saved pair root, and v2 runs hold the account scope.
            casing.Wire.RootPath = "/pilot";
            await using var scope = SyncProviderAccountVerificationScope.Begin(CancellationToken.None);
            var bytes = Encoding.UTF8.GetBytes("cased");
            var created = await casing.Writer().PutAsync(PutAction("Cased.txt", Source("Cased.txt", bytes)),
                new MemoryStream(bytes), CancellationToken.None);
            Require(created.Kind == ItemKind.File && created.Path == "Cased.txt" &&
                created.ObjectId.Length != 0,
                "a batch upload maps Dropbox's display casing back to the pair path");
        }

        using (var large = new Harness())
        {
            var bytes = new byte[(8 * 1024 * 1024) + 1];
            await large.Writer().PutAsync(PutAction("big.bin", Source("big.bin", bytes)), new MemoryStream(bytes),
                CancellationToken.None);
            Require(large.Wire.Calls.Contains("/2/files/upload") && large.Wire.LastUploadMode == "add" &&
                !large.Wire.Calls.Contains("/2/files/upload_session/finish_batch_v2"),
                "a large create uploads in one add request");
        }
    }

    private static async Task PutOccupiedAsync()
    {
        using var h = new Harness();
        var writer = h.Writer();
        h.Wire.SeedFile("busy.txt", "old");
        var bytes = Encoding.UTF8.GetBytes("new-bytes");
        await ExpectAsync<ExpectedTargetMismatchException>(() =>
            writer.PutAsync(PutAction("busy.txt", Source("busy.txt", bytes)), new MemoryStream(bytes),
                CancellationToken.None));
    }

    private static async Task PutUpdateAsync()
    {
        using var h = new Harness();
        var writer = h.Writer();
        h.Wire.SeedFile("u.txt", "v1");
        var current = (await writer.ObserveAsync("u.txt", CancellationToken.None))!;
        var bytes = Encoding.UTF8.GetBytes("v2");
        var source = Source("u.txt", bytes);
        var stale = new SyncAction("a-stale", 2, ActionKind.Update, Side.B, "u.txt", null, source,
            current with { VersionId = "stale" }, false, "k-stale");
        var before = h.Wire.Calls.Count;
        await ExpectAsync<ExpectedTargetMismatchException>(() => writer.PutAsync(stale, new MemoryStream(bytes),
            CancellationToken.None));
        Require(h.Wire.Calls.Skip(before).Contains("/2/files/upload_session/finish_batch_v2") &&
            h.Wire.LastUploadMode == "update", "a stale revision is rejected by the conditional batch commit");

        var update = new SyncAction("a-race", 2, ActionKind.Update, Side.B, "u.txt", null, source, current, false, "k");
        h.Wire.Script("/2/files/upload_session/finish_batch_v2", HttpStatusCode.InternalServerError, 1);
        await ExpectAsync<UncertainEffectException>(() => writer.PutAsync(update, new MemoryStream(bytes),
            CancellationToken.None));
        var written = await writer.PutAsync(update, new MemoryStream(bytes), CancellationToken.None);
        Require(written.ObjectId == current.ObjectId && written.Hash == source.Hash, "conditional batch update preserves object identity");
    }

    private static async Task DeleteFileAsync()
    {
        using var h = new Harness();
        var writer = h.Writer();
        h.Wire.SeedFile("d.txt", "content");
        var current = (await writer.ObserveAsync("d.txt", CancellationToken.None))!;
        await AuthorizedFixtureDeleteAsync(writer, DeleteAction("d.txt", current), CancellationToken.None);
        Require(h.Wire.LastDeleteParentRev == current.VersionId, "the delete request carries parent_rev");
        Require(await writer.ObserveAsync("d.txt", CancellationToken.None) is null, "the file is gone");

        h.Wire.SeedFile("d2.txt", "content");
        var stale = (await writer.ObserveAsync("d2.txt", CancellationToken.None))! with { VersionId = "stale" };
        await ExpectAsync<ExpectedTargetMismatchException>(() => AuthorizedFixtureDeleteAsync(writer, DeleteAction("d2.txt", stale),
            CancellationToken.None));
        Require(await writer.ObserveAsync("d2.txt", CancellationToken.None) is not null, "a stale delete keeps the file");
        await DeleteBatchRaceAsync();
    }

    private static async Task DeleteFolderAsync()
    {
        using var h = new Harness();
        var writer = h.Writer();
        h.Wire.SeedFolder("empty");
        var empty = (await writer.ObserveAsync("empty", CancellationToken.None))!;
        await AuthorizedFixtureDeleteAsync(writer, DeleteAction("empty", empty), CancellationToken.None);
        Require(await writer.ObserveAsync("empty", CancellationToken.None) is null, "an empty folder is deleted");

        h.Wire.SeedFolder("full");
        h.Wire.SeedFile("full/child.txt", "x");
        var full = (await writer.ObserveAsync("full", CancellationToken.None))!;
        var before = h.Wire.Calls.Count;
        await ExpectAsync<ExpectedTargetMismatchException>(() => AuthorizedFixtureDeleteAsync(writer, DeleteAction("full", full),
            CancellationToken.None));
        Require(!h.Wire.Calls.Skip(before).Contains("/2/files/delete_v2"), "a non-empty folder sends no delete");
        Require(await writer.ObserveAsync("full", CancellationToken.None) is not null, "the non-empty folder stays");
    }

    private static async Task DeleteUncertainAsync()
    {
        using var h = new Harness();
        var writer = h.Writer();
        h.Wire.SeedFile("s.txt", "x");
        var current = (await writer.ObserveAsync("s.txt", CancellationToken.None))!;
        h.Wire.Script("/2/files/delete_batch", HttpStatusCode.InternalServerError, 1);
        await ExpectAsync<UncertainEffectException>(() => AuthorizedFixtureDeleteAsync(writer, DeleteAction("s.txt", current),
            CancellationToken.None));
    }

    private static async Task MoveChecksAsync()
    {
        using var h = new Harness();
        var writer = h.Writer();
        h.Wire.SeedFile("m.txt", "content");
        var current = (await writer.ObserveAsync("m.txt", CancellationToken.None))!;
        var moved = await writer.MoveAsync(MoveAction("moved.txt", "m.txt", current), CancellationToken.None);
        Require(moved.Path == "moved.txt" && moved.ObjectId == current.ObjectId, "a move preserves identity");
        Require(await writer.ObserveAsync("m.txt", CancellationToken.None) is null, "the move source is gone");

        h.Wire.SeedFile("occ-src.txt", "a");
        h.Wire.SeedFile("occ-dst.txt", "b");
        var source = (await writer.ObserveAsync("occ-src.txt", CancellationToken.None))!;
        await ExpectAsync<ExpectedTargetMismatchException>(() =>
            writer.MoveAsync(MoveAction("occ-dst.txt", "occ-src.txt", source), CancellationToken.None));
        Require(await writer.ObserveAsync("occ-src.txt", CancellationToken.None) is not null,
            "the source stays when the destination is occupied");

        h.Wire.SeedFile("rate.txt", "c");
        var rate = (await writer.ObserveAsync("rate.txt", CancellationToken.None))!;
        h.Wire.Script("/2/files/move_v2", HttpStatusCode.TooManyRequests, 1);
        var retried = await writer.MoveAsync(MoveAction("rate-dst.txt", "rate.txt", rate), CancellationToken.None);
        Require(retried.Path == "rate-dst.txt", "a single 429 is retried and the move succeeds");
    }

    private static async Task MkDirChecksAsync()
    {
        using var h = new Harness();
        var writer = h.Writer();
        h.Wire.SeedFolder("existing");
        Require((await writer.MkDirAsync("existing", CancellationToken.None)).Kind == ItemKind.Folder,
            "mkdir over an existing folder is idempotent");
        Require((await writer.MkDirAsync("fresh", CancellationToken.None)).Kind == ItemKind.Folder,
            "mkdir creates a new folder");
        await MkDirBatchChecksAsync();
    }

    private static async Task PreserveChecksAsync()
    {
        using var h = new Harness();
        var writer = h.Writer();
        h.Wire.SeedFile("p.txt", "content");
        var current = (await writer.ObserveAsync("p.txt", CancellationToken.None))!;
        var before = h.Wire.Calls.Count;
        var reference = await writer.PreserveAsync(current, "action-1", CancellationToken.None);
        Require(h.Wire.Calls.Count == before, "preserve makes no provider call");
        Require(reference.VersionId == current.VersionId && reference.Hash == current.Hash,
            "the recovery reference carries the revision and hash");
    }

    private static async Task OpenReadChecksAsync(string spool)
    {
        using var h = new Harness();
        var writer = h.Writer(spool);
        h.Wire.SeedFile("read.txt", "hello world");
        var current = (await writer.ObserveAsync("read.txt", CancellationToken.None))!;
        await using (var stream = await writer.OpenReadAsync(current, CancellationToken.None))
        {
            Require(stream.CanSeek && stream.Length == 11, "the spool is a seekable exact revision");
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, CancellationToken.None);
            Require(Encoding.UTF8.GetString(buffer.ToArray()) == "hello world", "the spool holds the exact bytes");
        }
        Require(!Directory.Exists(spool) || Directory.GetFiles(spool).Length == 0, "the spool is emptied on dispose");

        await ExpectAsync<ExpectedTargetMismatchException>(() =>
            writer.OpenReadAsync(current with { VersionId = "stale" }, CancellationToken.None));
    }

    // The direct download writes the exact revision into the caller's stream and creates no product spool file.
    private static async Task DownloadToChecksAsync(string spool)
    {
        using var h = new Harness();
        var writer = h.Writer(spool);
        h.Wire.SeedFile("direct.txt", "direct bytes");
        var current = (await writer.ObserveAsync("direct.txt", CancellationToken.None))!;
        using var destination = new MemoryStream();
        var result = await writer.DownloadToAsync(current, destination, CancellationToken.None);
        Require(result.Revision == current.VersionId && result.ContentHash == current.Hash &&
            result.Size == current.Size && Encoding.UTF8.GetString(destination.ToArray()) == "direct bytes",
            "the direct download writes the exact planned revision into the caller's stream");
        Require(!Directory.Exists(spool) || Directory.GetFiles(spool).Length == 0,
            "the direct download creates no product spool file");
        using var rejected = new MemoryStream();
        // The provider rejects changed response metadata before writing any bytes, using its existing IO contract.
        var metadataRejected = false;
        try
        {
            await writer.DownloadToAsync(current with { Hash = new string('0', 64) }, rejected,
                CancellationToken.None);
        }
        catch (IOException error) when (error.GetType() == typeof(IOException)) { metadataRejected = true; }
        Require(metadataRejected && rejected.Length == 0,
            "changed download metadata is rejected before any bytes reach the target stage");
    }

    // A Dropbox-to-local download is ONE provider call: the revision-pinned /files/download. Its
    // Dropbox-API-Result carries the whole source identity, so no separate metadata read runs before or after.
    private static async Task DownloadSingleCallChecksAsync()
    {
        using var h = new Harness();
        var writer = h.Writer();
        h.Wire.SeedFile("single.txt", "one exact revision");
        var current = (await writer.ObserveAsync("single.txt", CancellationToken.None))!;
        var callsBefore = h.Wire.Calls.Count;
        var readsBefore = h.Wire.MetadataPaths.Count;
        await using (var stream = await writer.OpenReadAsync(current, CancellationToken.None))
        {
            Require(stream.CanSeek && stream.Length == current.Size, "the one-call spool holds the exact length");
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, CancellationToken.None);
            Require(Encoding.UTF8.GetString(copy.ToArray()) == "one exact revision",
                "the one-call spool holds the exact revision bytes");
        }
        var calls = h.Wire.Calls.Skip(callsBefore).ToArray();
        // Exclude the coalesced pair-root and account verifies; everything else must be the one download.
        Require(calls.Count(call => call != "/2/files/get_metadata" &&
                call != "/2/users/get_current_account") == 1 &&
            calls.Contains("/2/files/download"),
            "one download makes exactly one provider call excluding root/account verifies: " + string.Join(",", calls));
        Require(h.Wire.MetadataPaths.Skip(readsBefore).All(path => path == DropboxWire.RootId),
            "the only metadata reads are the pair-root verifies, never the file");
    }

    // A response header whose rev, hash, id or size disagrees with the planned entry is refused before any
    // target byte is touched; a truncated or altered body is refused by the length and content-hash guards.
    private static async Task DownloadRefusalChecksAsync()
    {
        var bytes = Encoding.UTF8.GetBytes("expected body bytes");
        foreach (var mode in new[] { "rev", "hash", "id", "size" })
        {
            using var h = new Harness();
            h.Wire.SeedFile("hdr.txt", Encoding.UTF8.GetString(bytes));
            var current = (await h.Writer().ObserveAsync("hdr.txt", CancellationToken.None))!;
            switch (mode)
            {
                case "rev": h.Wire.DownloadMetadataRevisionOverride = current.VersionId + "-other"; break;
                case "hash": h.Wire.DownloadMetadataHashOverride = new string('0', 64); break;
                case "id": h.Wire.DownloadMetadataIdOverride = "id:other-object"; break;
                default: h.Wire.DownloadMetadataSizeOverride = current.Size + 1; break;
            }
            using var destination = new MemoryStream();
            await destination.WriteAsync(Encoding.UTF8.GetBytes("untouched target"));
            await ExpectAsync<IOException>(() =>
                h.Client.DownloadAsync(DropboxItems.ToEntry(current), destination, CancellationToken.None));
            Require(destination.ToArray().SequenceEqual(Encoding.UTF8.GetBytes("untouched target")),
                mode + ": a mismatched response header leaves the target unchanged");
        }

        foreach (var mode in new[] { "truncated", "altered" })
        {
            using var h = new Harness();
            h.Wire.SeedFile("body.txt", Encoding.UTF8.GetString(bytes));
            var current = (await h.Writer().ObserveAsync("body.txt", CancellationToken.None))!;
            h.Wire.DownloadBodyOverride = mode == "truncated"
                ? bytes.Take(bytes.Length - 3).ToArray()
                : bytes.Select((value, index) => index == 0 ? (byte)(value ^ 0xff) : value).ToArray();
            using var destination = new MemoryStream();
            await ExpectAsync<IOException>(() =>
                h.Client.DownloadAsync(DropboxItems.ToEntry(current), destination, CancellationToken.None));
        }
    }

    // b8c0df4a: only a structurally parsed path/not_found on HTTP 409 is the narrow missing-source proof that
    // maps to Skipped. Restricted, locked, unknown, malformed, summary-only and 5xx refusals all stay failures.
    private static async Task DownloadRefusalMappingChecksAsync()
    {
        static string Structured(string tag) =>
            "{\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"" + tag + "\"}}}";
        var cases = new (string Name, HttpStatusCode Status, string Body, bool Skipped)[]
        {
            ("parsed path/not_found", HttpStatusCode.Conflict, Structured("not_found"), true),
            ("path/restricted_content", HttpStatusCode.Conflict, Structured("restricted_content"), false),
            ("path/locked", HttpStatusCode.Conflict, Structured("locked"), false),
            ("unknown tag", HttpStatusCode.Conflict, Structured("unrecognized"), false),
            ("malformed body", HttpStatusCode.Conflict, "{\"error\":", false),
            ("summary only", HttpStatusCode.Conflict, "{\"error_summary\":\"path/not_found/\"}", false),
            ("server error", HttpStatusCode.InternalServerError, Structured("not_found"), false)
        };
        static async Task ExpectMappedAsync(string label, bool skipped, Func<Task> operation)
        {
            try { await operation(); }
            catch (Exception error) when (skipped
                ? error is ExpectedTargetMismatchException
                : error is DropboxApiException)
            { return; }
            throw new InvalidOperationException(label + ": unexpected download refusal mapping.");
        }

        foreach (var (name, status, body, skipped) in cases)
        {
            using var h = new Harness();
            // A 5xx read is retried against the transient budget; a tiny budget keeps the row immediate.
            h.Client.ReadTransientBudget = TimeSpan.FromMilliseconds(50);
            h.Client.ReadRetryDelay = (_, _) => Task.CompletedTask;
            h.Wire.SeedFile("refused.txt", "x");
            h.Wire.DownloadRefusalOverride = (status, body);
            var current = (await h.Writer().ObserveAsync("refused.txt", CancellationToken.None))!;
            await ExpectMappedAsync(name, skipped, () =>
                h.Writer().OpenReadAsync(current, CancellationToken.None));
            using var destination = new MemoryStream();
            await ExpectMappedAsync(name, skipped, () =>
                h.Writer().DownloadToAsync(current, destination, CancellationToken.None));
        }
    }

    private static async Task ObserveChecksAsync()
    {
        using var h = new Harness();
        var writer = h.Writer();
        Require(await writer.ObserveAsync("nope.txt", CancellationToken.None) is null, "observe is null when absent");
        h.Wire.SeedFile("here.txt", "x");
        Require(await writer.ObserveAsync("here.txt", CancellationToken.None) is not null, "observe returns the item");
    }

    // The batch commit names each entry by the root's verified root-namespace path plus the pair-relative path.
    // Id-prefixed entry paths make Dropbox's finish_batch_v2 (and the other batch endpoints) roughly an order of
    // magnitude slower; the verified path also follows a renamed or moved pair root on Dropbox.
    private static async Task BatchRootPathChecksAsync()
    {
        using (var h = new Harness())
        {
            var writer = h.Writer();
            var bytes = Encoding.UTF8.GetBytes("0123456789");
            await writer.PutAsync(PutAction("new.txt", Source("new.txt", bytes)), new MemoryStream(bytes),
                CancellationToken.None);
            Require(h.Wire.LastUploadCommitPaths.SequenceEqual(new[] { "/Pilot/new.txt" }),
                "the upload commit names the verified root path: " + string.Join(",", h.Wire.LastUploadCommitPaths));

            await writer.MkDirAsync("newdir", CancellationToken.None);
            Require(h.Wire.LastMkDirBatchPaths.SequenceEqual(new[] { "/Pilot/newdir" }),
                "the folder-create batch names the verified root path: " + string.Join(",", h.Wire.LastMkDirBatchPaths));

            h.Wire.SeedFile("gone.txt", "x");
            var current = (await writer.ObserveAsync("gone.txt", CancellationToken.None))!;
            await AuthorizedFixtureDeleteAsync(writer, DeleteAction("gone.txt", current), CancellationToken.None);
            // Deletes stay id-rooted parent_rev binds the file, not its parent, so a
            // path-rooted delete after a root swap could target a replacement folder at the old path.
            Require(h.Wire.LastDeleteBatchPaths.SequenceEqual(new[] { DropboxWire.RootId + "/gone.txt" }),
                "the delete batch keeps its id-rooted path: " + string.Join(",", h.Wire.LastDeleteBatchPaths));
        }

        // A root that moves between the pre-commit verification and the commit makes every entry uncertain: the
        // applied paths can no longer be trusted, and reconciliation resolves them instead of a blind replay.
        using (var h = new Harness())
        {
            var bytes = Encoding.UTF8.GetBytes("0123456789");
            h.Wire.MoveRootOnFinishBatch = true;
            var notices = new List<SyncByteProgress>();
            using var progress = SyncByteProgressStream.Begin("new.txt", item =>
            {
                notices.Add(item);
                return Task.CompletedTask;
            });
            await ExpectAsync<UncertainEffectException>(() =>
                h.Writer().PutAsync(PutAction("new.txt", Source("new.txt", bytes)), new MemoryStream(bytes),
                    CancellationToken.None));
            Require(notices.Any(item => item.Kind == ProcessMessageKind.Notice &&
                item.Detail.Contains("Dropbox pair root moved during upload; verifying", StringComparison.Ordinal)),
                "a root moved during the commit raises its verifying notice");
        }

        // A success entry whose returned path is outside the verified root is uncertain, never a success.
        using (var h = new Harness())
        {
            var bytes = Encoding.UTF8.GetBytes("0123456789");
            h.Wire.UploadSuccessPathOverride = "/Elsewhere/new.txt";
            await ExpectAsync<UncertainEffectException>(() =>
                h.Writer().PutAsync(PutAction("new.txt", Source("new.txt", bytes)), new MemoryStream(bytes),
                    CancellationToken.None));
        }

        // A folder-create result whose path is outside the verified root cannot prove the create landed in the
        // pair; it is Uncertain, never a plain failure the caller might replay.
        using (var h = new Harness())
        {
            h.Wire.MkDirSuccessPathOverride = "/Elsewhere/newdir";
            await ExpectAsync<UncertainEffectException>(() => h.Writer().MkDirAsync("newdir", CancellationToken.None));
        }

        // The post-commit root re-read is hard-bounded: an unanswered read marks the batch uncertain instead of
        // holding the commit open forever.
        using (var h = new Harness())
        {
            h.Client.RootVerifyTimeout = TimeSpan.FromMilliseconds(150);
            h.Wire.RootMetadataHoldAfter = 1;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Wire.RootMetadataGate = gate.Task;
            var bytes = Encoding.UTF8.GetBytes("0123456789");
            try
            {
                await ExpectAsync<UncertainEffectException>(() =>
                    h.Writer().PutAsync(PutAction("slow-root.txt", Source("slow-root.txt", bytes)),
                        new MemoryStream(bytes), CancellationToken.None));
            }
            finally { gate.TrySetResult(); }
        }

        // The namespace root keeps its existing leading-only "/relative" commit paths.
        using (var h = new Harness())
        {
            h.Wire.RootPath = "/";
            using var http = new HttpClient(h.Wire);
            var client = new DropboxPilotClient(http, new FixedTokenSource(), "/", Binding(), "ns:root-namespace");
            var writer = new DropboxWriter(new RootDef(RootId, "Dropbox", "acc", "/", "ns:root-namespace"), Side.B,
                client, Path.Combine(_root, "spool-ns"),
                V2Settings.Default with { UploadBatchSize = 1, MkDirBatchSize = 1 });
            var bytes = Encoding.UTF8.GetBytes("0123456789");
            await writer.PutAsync(PutAction("plain.txt", Source("plain.txt", bytes)), new MemoryStream(bytes),
                CancellationToken.None);
            Require(h.Wire.LastUploadCommitPaths.SequenceEqual(new[] { "/plain.txt" }),
                "the namespace-root pair keeps its leading-only commit path: " +
                string.Join(",", h.Wire.LastUploadCommitPaths));
            await writer.MkDirAsync("plaindir", CancellationToken.None);
            Require(h.Wire.LastMkDirBatchPaths.SequenceEqual(new[] { "/plaindir" }),
                "the namespace-root folder batch keeps its leading-only path: " +
                string.Join(",", h.Wire.LastMkDirBatchPaths));
        }
    }

    private static SyncAction PutAction(string path, ItemState source) =>
        new(NextActionId(), 18, ActionKind.CopyNew, Side.B, path, null, source, null, false, "k-" + path);

    private static SyncAction DeleteAction(string path, ItemState target) =>
        new(NextActionId(), 6, ActionKind.Delete, Side.B, path, null, null, target, true, "k-" + path);

    private static SyncAction MoveAction(string path, string from, ItemState target) =>
        new(NextActionId(), 9, ActionKind.Move, Side.B, path, from, null, target, false, "k-" + path);

    private static ItemState Source(string path, byte[] bytes) =>
        new(path, ItemKind.File, "id:local:" + path.Length.ToString(CultureInfo.InvariantCulture), null,
            DropboxContentHash.Compute(bytes), bytes.Length, DateTimeOffset.UnixEpoch, ItemFlags.None);

    private static string NextActionId() => "a" + (++_actionSeq).ToString(CultureInfo.InvariantCulture);
}
