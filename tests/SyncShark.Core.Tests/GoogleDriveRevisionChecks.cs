using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// Real revision parsing, navigation, retention, checksum and spool decisions; only HTTP input is synthetic.
// Run serially with other admission fixtures. No installed staging, credentials or live provider is used.
internal static class GoogleDriveRevisionChecks
{
    private const string RootId = "revision-root";
    private const string FileId = "revision-file";
    private const string FileName = "example.bin";
    private const string RevisionId = "revision-1";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("revision-content");
    private static readonly string Md5 = Convert.ToHexString(MD5.HashData(Payload)).ToLowerInvariant();
    private static readonly string Sha256 = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
    private static readonly DateTimeOffset Modified = DateTimeOffset.Parse("2026-09-01T12:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture);

    internal static async Task RunAsync(string scratchRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Revision staging uses held NTFS directories.");
        var parent = Path.GetFullPath(scratchRoot);
        var installed = Path.GetFullPath(SyncSharkRuntimePaths.ReplacementStagingRoot).TrimEnd(Path.DirectorySeparatorChar);
        Require(!string.Equals(parent, installed, StringComparison.OrdinalIgnoreCase) &&
            !parent.StartsWith(installed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "fixture scratch must not be the installed replacement staging tree");
        Directory.CreateDirectory(parent);
        var root = Path.Combine(parent, "google-revisions-" + Guid.NewGuid().ToString("N"));
        Clean(root);
        var capacity = SyncRecoverySpoolAdmission.CapacityBytesForTest;
        var timeout = SyncRecoverySpoolAdmission.WaitTimeoutForTest;
        var failures = new List<Exception>();
        SyncRecoverySpoolAdmission.CapacityBytesForTest = 1024 * 1024;
        SyncRecoverySpoolAdmission.WaitTimeoutForTest = TimeSpan.FromSeconds(10);
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS Google revisions: " + name); }
            catch (Exception error) { failures.Add(new InvalidOperationException(name + ": " + error.Message, error)); }
            // Never mask a leaked reservation by resetting admission or deleting a nonempty directory.
            Clean(root);
        }
        try
        {
            await Check("paged listing, empty page, identical duplicate and stable path binding", () => ListingAsync(root));
            await Check("repeat cursor rejected", () => BadListingAsync(root, repeatedCursor: true));
            await Check("changed duplicate rejected", () => BadListingAsync(root, repeatedCursor: false));
            foreach (var changed in new[] { Blob() with { Version = "8" }, Blob() with { Id = "replacement-file" } })
                await Check("listing detects changed file/version " + changed.Id + "/" + changed.Version,
                    () => ChangedFileDuringListingAsync(root, changed));
            await Check("wrong account refused before file lookup", () => WrongAccountAsync(root));
            await Check("different selected file refused before revision lookup", () => WrongFileAsync(root));
            await Check("path traversal refused before network", () => TraversalAsync(root));
            await Check("native document rejected without retention", () => NativeDocumentAsync(root));
            foreach (var bad in BadIdentities())
                await Check("initial revision identity rejected " + Describe(bad), () => BadInitialRevisionAsync(root, bad));
            await Check("explicit retention then verified bytes and SHA-256", () => ConsumeAsync(root, alreadyRetained: false));
            await Check("retained revision needs no PATCH", () => ConsumeAsync(root, alreadyRetained: true));
            await Check("consumer exception releases private spool", () => ConsumeAsync(root, alreadyRetained: true, throwConsumer: true));
            foreach (var bad in BadIdentities().Append(Expected() with { KeepForever = false }))
                await Check("retention readback rejected " + Describe(bad) + "/" + bad.KeepForever,
                    () => BadRetentionAsync(root, bad));
            foreach (var mode in new[] { "short", "oversized", "wrong-md5", "wrong-header-length" })
                await Check("media rejection " + mode, () => BadMediaAsync(root, mode));
            foreach (var bad in BadIdentities())
                await Check("post-download revision identity rejected " + Describe(bad), () => BadAfterReadAsync(root, bad));
            await Check("final location rejects file moved out of pair", () => BadFinalLocationAsync(root, movedOut: true));
            await Check("final location rejects replacement file ID", () => BadFinalLocationAsync(root, movedOut: false));
            await Check("low capacity prevents retention and media", () => LowCapacityAsync(root));
            await Check("cancellation before network", () => CancelAsync(root, "before"));
            await Check("cancellation during streaming", () => CancelAsync(root, "stream"));
            await Check("cancellation at final readback", () => CancelAsync(root, "readback"));
            await Check("public Host journalled conditional revision restore", () => GoogleRevisionRestoreHostChecks.RunAsync(parent));
        }
        finally
        {
            SyncRecoverySpoolAdmission.CapacityBytesForTest = capacity;
            SyncRecoverySpoolAdmission.WaitTimeoutForTest = timeout;
            Clean(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: false);
        }
        if (failures.Count != 0) throw new AggregateException("Google revision checks failed", failures);
    }

    private static GoogleDriveRevisionEntry Expected(bool keep = true) =>
        new(FileName, FileId, RevisionId, Payload.Length, Md5, Modified, keep);
    private static IEnumerable<GoogleDriveRevisionEntry> BadIdentities()
    {
        yield return Expected() with { RevisionId = "another-revision" };
        yield return Expected() with { Size = Payload.Length + 1 };
        yield return Expected() with { Md5 = new string('0', 32) };
    }
    private static string Describe(GoogleDriveRevisionEntry row) => row.RevisionId + "/" + row.Size + "/" + row.Md5;
    private static GoogleDriveConnectionBinding Binding() => new GoogleDriveConnectionBinding(
        GoogleDriveProviderConfiguration.RegisteredClientId, GoogleDriveConnectionBinding.CreateAccountKey("revision-account"),
        "revision-account", "fixture@example.invalid", "Fixture", [GoogleDriveProviderConfiguration.DriveScope],
        CredentialReference: "synthetic-only").Normalize();
    private static GoogleDrivePilotClient Client(HttpClient http, string root) =>
        new(http, new Tokens(), Binding(), RootId, "/Pair") { RevisionStagingRootForTest = root };

    private static async Task ListingAsync(string root)
    {
        using var script = new Protocol(); using var http = new HttpClient(script);
        var folder = new Node("nested-folder", "Folder", [RootId], "application/vnd.google-apps.folder");
        var file = Blob() with { Parents = [folder.Id] };
        var old = Expected(keep: false) with { RelativePath = "Folder/" + FileName, Md5 = Md5.ToUpperInvariant() };
        var newer = old with { RevisionId = "revision-2", ModifiedUtc = Modified.AddHours(1), KeepForever = true };
        Navigate(script, folder, file);
        Page(script, [old], next: "page-2");
        Page(script, [], page: "page-2", next: "page-3");
        Page(script, [old, newer], page: "page-3");
        Navigate(script, folder, file);
        var result = await Client(http, root).ListRevisionsAsync("Folder/" + FileName);
        Require(result.Count == 2 && result[0].RevisionId == "revision-2" && result[1].RevisionId == RevisionId,
            "real listing follows empty pages, deduplicates identical rows and sorts by modification time");
        Require(result.All(row => row.FileId == FileId && row.RelativePath == "Folder/" + FileName &&
            row.Size == Payload.Length && row.Md5 == Md5), "all listed identities retain exact path/id/size and normalized MD5");
        Require(result[0].KeepForever && !result[1].KeepForever, "listing preserves provider retention state without changing it");
        script.Done(); script.ReadOnly();
        Require(!Directory.Exists(root), "listing allocates no revision spool");
    }

    private static async Task BadListingAsync(string root, bool repeatedCursor)
    {
        using var script = new Protocol(); using var http = new HttpClient(script);
        Navigate(script, Blob()); Page(script, [Expected()], next: "same-cursor");
        if (repeatedCursor) Page(script, [], page: "same-cursor", next: "same-cursor");
        else Page(script, [Expected() with { Md5 = new string('0', 32) }], page: "same-cursor");
        await RejectAsync(() => Client(http, root).ListRevisionsAsync(FileName));
        script.Done(); script.ReadOnly();
    }

    private static async Task ChangedFileDuringListingAsync(string root, Node changed)
    {
        using var script = new Protocol(); using var http = new HttpClient(script);
        Navigate(script, Blob()); Page(script, [Expected()]); Navigate(script, changed);
        await RejectAsync(() => Client(http, root).ListRevisionsAsync(FileName));
        script.Done(); script.ReadOnly();
    }

    private static async Task WrongAccountAsync(string root)
    {
        using var script = new Protocol(); using var http = new HttpClient(script);
        About(script, "different-account");
        await RejectAsync(() => Client(http, root).ListRevisionsAsync(FileName));
        script.Done(); script.ReadOnly();
        Require(script.Requests.Count == 1, "account identity fails before any file or revision lookup");
    }

    private static async Task WrongFileAsync(string root)
    {
        using var script = new Protocol(); using var http = new HttpClient(script);
        Navigate(script, Blob() with { Id = "different-file" });
        await NoConsumerAsync(Client(http, root), Expected()); script.Done(); script.ReadOnly();
    }

    private static async Task TraversalAsync(string root)
    {
        using var script = new Protocol(); using var http = new HttpClient(script);
        await NoConsumerAsync(Client(http, root), Expected() with { RelativePath = "../" + FileName });
        script.Done(); Require(script.Requests.Count == 0, "root escape fails before provider access");
    }

    private static async Task NativeDocumentAsync(string root)
    {
        using var script = new Protocol(); using var http = new HttpClient(script);
        Navigate(script, Blob() with { Mime = "application/vnd.google-apps.document" });
        await RejectAsync(() => Client(http, root).ListRevisionsAsync(FileName)); script.Done(); script.ReadOnly();
    }

    private static async Task BadInitialRevisionAsync(string root, GoogleDriveRevisionEntry bad)
    {
        using var script = new Protocol(); using var http = new HttpClient(script);
        Navigate(script, Blob()); Revision(script, bad);
        await NoConsumerAsync(Client(http, root), Expected()); script.Done(); script.ReadOnly();
    }

    private static async Task ConsumeAsync(string root, bool alreadyRetained, bool throwConsumer = false)
    {
        using var script = new Protocol(); using var http = new HttpClient(script);
        Navigate(script, Blob()); Revision(script, Expected(alreadyRetained));
        if (!alreadyRetained) Retain(script, Expected(), root);
        var media = Media(script, Payload, root); Revision(script, Expected());
        Navigate(script, Blob());
        var invoked = 0;
        Stream? consumed = null;
        async Task<int> Consume(Stream bytes, string hash)
        {
            invoked++; consumed = bytes;
            Require(media.EndReached && script.Remaining == 0,
                "consumer is after full media EOF, exact post-read revision metadata and final pair location readback");
            Require(bytes.CanSeek && bytes.Position == 0 && bytes.Length == Payload.Length, "verified full spool is rewound for consumer");
            Require(hash == Sha256 && hash != Md5, "consumer receives independently computed SHA-256, not provider MD5");
            AssertReserved(root);
            using var copy = new MemoryStream(); await bytes.CopyToAsync(copy);
            Require(copy.ToArray().SequenceEqual(Payload), "consumer receives only the exact verified full revision");
            if (throwConsumer) throw new ConsumerFailure();
            return 71;
        }
        if (throwConsumer)
        {
            var rejected = false;
            try { await Client(http, root).WithRevisionBytesAsync(Expected(), Consume, CancellationToken.None); }
            catch (ConsumerFailure) { rejected = true; }
            Require(rejected, "consumer's own failure propagates without replay");
        }
        else Require(await Client(http, root).WithRevisionBytesAsync(Expected(), Consume, CancellationToken.None) == 71,
            "consumer result is returned unchanged");
        Require(invoked == 1 && consumed is not null && !consumed.CanRead && media.Disposed,
            "consumer executes once and stage/input handles are closed even after consumer failure");
        Require(script.Requests.Count(row => row.Method == HttpMethod.Patch) == (alreadyRetained ? 0 : 1),
            "only explicit byte consumption may retain an unretained revision, and only once");
        script.Done();
    }

    private static async Task BadRetentionAsync(string root, GoogleDriveRevisionEntry bad)
    {
        using var script = new Protocol(); using var http = new HttpClient(script);
        Navigate(script, Blob()); Revision(script, Expected(keep: false)); Retain(script, bad, root);
        await NoConsumerAsync(Client(http, root), Expected()); script.Done();
        Require(script.Requests.Count(row => row.Method == HttpMethod.Patch) == 1 &&
            script.Requests.All(row => row.Alt != "media"), "unconfirmed retention neither retries nor downloads");
    }

    private static async Task BadMediaAsync(string root, string mode)
    {
        using var script = new Protocol(); using var http = new HttpClient(script);
        Navigate(script, Blob()); Revision(script, Expected());
        byte[] delivered = mode switch
        {
            "short" => Payload[..^1], "oversized" => [.. Payload, (byte)33],
            "wrong-md5" => Enumerable.Repeat((byte)88, Payload.Length).ToArray(), _ => Payload
        };
        var media = Media(script, delivered, root, mode == "wrong-header-length" ? Payload.Length + 1L : null);
        await NoConsumerAsync(Client(http, root), Expected()); script.Done(); script.ReadOnly();
        Require(media.Disposed, "failed media response stream is released");
    }

    private static async Task BadAfterReadAsync(string root, GoogleDriveRevisionEntry bad)
    {
        using var script = new Protocol(); using var http = new HttpClient(script);
        Navigate(script, Blob()); Revision(script, Expected());
        var media = Media(script, Payload, root); Revision(script, bad);
        await NoConsumerAsync(Client(http, root), Expected()); script.Done(); script.ReadOnly();
        Require(media.EndReached && media.Disposed, "valid bytes still fail closed on changed post-read revision metadata");
    }

    private static async Task BadFinalLocationAsync(string root, bool movedOut)
    {
        using var script = new Protocol(); using var http = new HttpClient(script);
        Navigate(script, Blob()); Revision(script, Expected());
        var media = Media(script, Payload, root); Revision(script, Expected());
        if (movedOut)
        {
            About(script, Binding().PermissionId);
            Metadata(script, new Node(RootId, "Pair", [], "application/vnd.google-apps.folder"));
            script.Json(HttpMethod.Get, "files", new { files = Array.Empty<object>(), incompleteSearch = false }, inspect: request =>
                Require(Query(request, "q") == "'" + RootId + "' in parents and name = '" + FileName + "' and trashed = false",
                    "final readback checks the original path inside the bound pair, not the moved file's new location"));
        }
        else Navigate(script, Blob() with { Id = "replacement-file" });
        await NoConsumerAsync(Client(http, root), Expected()); script.Done(); script.ReadOnly();
        Require(media.EndReached && media.Disposed,
            "fully verified revision bytes are withheld and released when the final pair location no longer names the selected file");
    }

    private static async Task LowCapacityAsync(string root)
    {
        var capacity = SyncRecoverySpoolAdmission.CapacityBytesForTest;
        SyncRecoverySpoolAdmission.CapacityBytesForTest = Payload.Length * 2 - 1;
        try
        {
            using var script = new Protocol(); using var http = new HttpClient(script);
            Navigate(script, Blob()); Revision(script, Expected(keep: false));
            var error = await NoConsumerAsync(Client(http, root), Expected()); script.Done(); script.ReadOnly();
            Require(error is IOException && error.Message.Contains("shared capacity", StringComparison.Ordinal),
                "low-capacity rejection must originate from real spool admission, not an unrelated filesystem failure");
        }
        finally { SyncRecoverySpoolAdmission.CapacityBytesForTest = capacity; }
    }

    private static async Task CancelAsync(string root, string phase)
    {
        using var script = new Protocol(); using var http = new HttpClient(script); using var stop = new CancellationTokenSource();
        ChunkedMedia? media = null;
        if (phase == "before") stop.Cancel();
        else
        {
            Navigate(script, Blob()); Revision(script, Expected());
            media = Media(script, Payload, root, cancelOnSecondRead: phase == "stream" ? stop : null);
            if (phase == "readback") Revision(script, Expected(), beforeResponse: stop.Cancel);
        }
        var calls = 0; var cancelled = false;
        try
        {
            await Client(http, root).WithRevisionBytesAsync(Expected(), (_, _) =>
            { calls++; return Task.FromResult(0); }, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { cancelled = true; }
        Require(cancelled && calls == 0, "requested cancellation never invokes the consumer");
        if (media is not null) Require(media.Disposed, "cancelled media handle is closed");
        script.Done(); script.ReadOnly();
    }

    private static async Task<Exception> NoConsumerAsync(GoogleDrivePilotClient client, GoogleDriveRevisionEntry expected)
    {
        var calls = 0;
        var error = await RejectAsync(() => client.WithRevisionBytesAsync(expected, (_, _) =>
        { calls++; return Task.FromResult(0); }, CancellationToken.None));
        Require(calls == 0, "no consumer observes rejected bytes or metadata");
        return error;
    }
    private static async Task<Exception> RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or
            ArgumentException or GoogleDriveApiException) { return error; }
        throw new FixtureFailure("Expected a real production rejection");
    }
    private static void Clean(string root)
    {
        Require(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any(), "private temporary file must be deleted on every exit");
        Require(SyncRecoverySpoolAdmission.SnapshotForTest(Path.GetPathRoot(root)!) == (0, 0, 0),
            "all revision reservations and waiters must be released");
    }
    private static void AssertReserved(string root)
    {
        Require(SyncRecoverySpoolAdmission.SnapshotForTest(Path.GetPathRoot(root)!) == (Payload.Length * 2L, 0, 1),
            "one 2x full-size reservation remains active until the consumer finishes");
        Require(Directory.Exists(root) && Directory.EnumerateFiles(root, "*.tmp").Count() == 1,
            "exactly one private temporary staging file exists inside the supplied test boundary");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new FixtureFailure("Google revision checks: " + message); }

    private static Node Blob() => new(FileId, FileName, [RootId], "application/octet-stream");
    private sealed record Node(string Id, string Name, string[] Parents, string Mime, string Version = "7")
    {
        internal object Wire() => new
        {
            id = Id, name = Name, parents = Parents, mimeType = Mime, version = Version, trashed = false,
            size = Payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), md5Checksum = Md5,
            sha256Checksum = Sha256, capabilities = new { canDownload = true },
            webViewLink = Mime == "application/vnd.google-apps.folder" ? "https://drive.google.com/drive/folders/" + Id :
                Mime == "application/vnd.google-apps.document" ? "https://docs.google.com/document/d/" + Id + "/edit" :
                "https://drive.google.com/file/d/" + Id + "/view"
        };
    }
    private static void Navigate(Protocol script, params Node[] chain)
    {
        About(script, Binding().PermissionId);
        var root = new Node(RootId, "Pair", [], "application/vnd.google-apps.folder");
        for (var pass = 0; pass < 2; pass++)
        {
            Metadata(script, root);
            var parent = RootId;
            foreach (var node in chain)
            {
                var expectedParent = parent;
                script.Json(HttpMethod.Get, "files", new { files = new[] { node.Wire() }, incompleteSearch = false }, inspect: request =>
                {
                    Require(Query(request, "q") == "'" + expectedParent + "' in parents and name = '" + node.Name + "' and trashed = false",
                        "navigation binds each exact name and parent without broad inventory");
                    Require(Query(request, "pageToken") is null, "fixture navigation is a single unambiguous page");
                });
                parent = node.Id;
            }
            if (chain.Length > 0) Metadata(script, chain[^1]);
        }
    }
    private static void About(Protocol script, string account) => script.Json(HttpMethod.Get, "about",
        new { user = new { permissionId = account, emailAddress = "fixture@example.invalid" }, storageQuota = new { } });
    private static void Metadata(Protocol script, Node node) => script.Json(HttpMethod.Get, "files/" + node.Id, node.Wire());
    private static object RevisionWire(GoogleDriveRevisionEntry row) => new
    {
        id = row.RevisionId, mimeType = "application/octet-stream", size = row.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
        md5Checksum = row.Md5, modifiedTime = row.ModifiedUtc, keepForever = row.KeepForever
    };
    private static void Page(Protocol script, GoogleDriveRevisionEntry[] rows, string? page = null, string? next = null) =>
        script.Json(HttpMethod.Get, "files/" + FileId + "/revisions", new { revisions = rows.Select(RevisionWire).ToArray(), nextPageToken = next },
            inspect: request => Require(Query(request, "pageToken") == page && Query(request, "pageSize") == "200",
                "revision list uses its exact continuation and bounded page size"));
    private static void Revision(Protocol script, GoogleDriveRevisionEntry row, Action? beforeResponse = null) =>
        script.Json(HttpMethod.Get, "files/" + FileId + "/revisions/" + RevisionId, RevisionWire(row),
            inspect: request => Require(Query(request, "alt") is null && Query(request, "fields")?.Contains("md5Checksum", StringComparison.Ordinal) == true,
                "revision lookup asks for exact integrity metadata"), beforeResponse: beforeResponse);
    private static void Retain(Protocol script, GoogleDriveRevisionEntry row, string root) =>
        script.Json(HttpMethod.Patch, "files/" + FileId + "/revisions/" + RevisionId, RevisionWire(row), inspect: _ => AssertReserved(root),
            bodyCheck: body =>
            {
                using var doc = JsonDocument.Parse(body);
                Require(doc.RootElement.EnumerateObject().Count() == 1 && doc.RootElement.GetProperty("keepForever").GetBoolean(),
                    "the only effect is retaining the exact selected revision; no file bytes, name or permissions change");
            });
    private static ChunkedMedia Media(Protocol script, byte[] payload, string root, long? declaredLength = null,
        CancellationTokenSource? cancelOnSecondRead = null)
    {
        var media = new ChunkedMedia(payload, cancelOnSecondRead);
        script.Add(HttpMethod.Get, "files/" + FileId + "/revisions/" + RevisionId, () =>
        {
            var content = new StreamContent(media);
            if (declaredLength is not null) content.Headers.ContentLength = declaredLength;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }, inspect: request =>
        {
            Require(Query(request, "alt") == "media" && request.Headers.Range is null, "whole exact revision media is requested, never unpinned ranges");
            AssertReserved(root);
        });
        return media;
    }
    private static string? Query(HttpRequestMessage request, string key) => request.RequestUri!.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2))
        .Where(pair => Uri.UnescapeDataString(pair[0]) == key)
        .Select(pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "").FirstOrDefault();

    private sealed class Tokens : IGoogleDriveAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult("synthetic-not-a-credential"); }
    }
    private sealed class FixtureFailure(string message) : Exception(message);
    private sealed class ConsumerFailure : Exception { }
    private sealed record Received(HttpMethod Method, string Path, string? Alt);
    private sealed class Protocol : HttpMessageHandler
    {
        private readonly Queue<Step> _steps = new();
        private bool _reported;
        internal List<Received> Requests { get; } = [];
        internal int Remaining => _steps.Count;
        internal void Add(HttpMethod method, string path, Func<HttpResponseMessage> response,
            Action<HttpRequestMessage>? inspect = null, Action<string>? bodyCheck = null, Action? beforeResponse = null) =>
            _steps.Enqueue(new(method, path, response, inspect, bodyCheck, beforeResponse));
        internal void Json(HttpMethod method, string path, object payload, Action<HttpRequestMessage>? inspect = null,
            Action<string>? bodyCheck = null, Action? beforeResponse = null) => Add(method, path,
            () => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") }, inspect, bodyCheck, beforeResponse);
        internal void Done() => Require(_steps.Count == 0, "all and only the expected protocol requests occur");
        internal void ReadOnly() => Require(Requests.All(row => row.Method == HttpMethod.Get), "listing/rejected preflight never changes retention");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Requests.Add(new(request.Method, uri.AbsolutePath, Query(request, "alt")));
            if (!_steps.TryDequeue(out var step)) throw new FixtureFailure("Unexpected HTTP request " + request.Method + " " + uri.AbsolutePath);
            Require(uri.Scheme == "https" && uri.Host == "www.googleapis.com" && uri.Port == 443 && uri.UserInfo.Length == 0 &&
                uri.AbsolutePath == "/drive/v3/" + step.Path && request.Method == step.Method,
                "request order, exact file/revision identity and official origin must match");
            Require(request.Headers.Authorization?.Scheme == "Bearer", "real client uses the synthetic protected-token callback");
            if (request.Method == HttpMethod.Get) Require(request.Content is null, "read requests carry no mutation body");
            step.Inspect?.Invoke(request);
            if (step.BodyCheck is not null)
            {
                Require(request.Content is not null, "effect has its exact payload");
                step.BodyCheck(await request.Content!.ReadAsStringAsync(cancellationToken));
            }
            step.BeforeResponse?.Invoke();
            return step.Response();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_reported && Requests.Count != 0)
            {
                _reported = true;
                Console.WriteLine("Google revision HTTP: " + string.Join(" -> ", Requests.Select(row =>
                    row.Method + " " + row.Path + (row.Alt is null ? "" : "?alt=" + row.Alt))) +
                    "; effects=" + Requests.Count(row => row.Method != HttpMethod.Get));
            }
            base.Dispose(disposing);
        }
        private sealed record Step(HttpMethod Method, string Path, Func<HttpResponseMessage> Response,
            Action<HttpRequestMessage>? Inspect, Action<string>? BodyCheck, Action? BeforeResponse);
    }

    // No Content-Length unless the fixture explicitly supplies it: short/oversized tests exercise
    // the production streaming bound, not merely the response-header length check.
    private sealed class ChunkedMedia(byte[] payload, CancellationTokenSource? cancelOnSecondRead) : Stream
    {
        private int _position;
        private int _reads;
        internal bool EndReached { get; private set; }
        internal bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count), CancellationToken.None);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadCore(buffer.Span, cancellationToken));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(ReadCore(buffer.AsSpan(offset, count), cancellationToken));
        private int ReadCore(Span<byte> buffer, CancellationToken cancellationToken)
        {
            if (++_reads == 2 && cancelOnSecondRead is not null) cancelOnSecondRead.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            cancelOnSecondRead?.Token.ThrowIfCancellationRequested();
            Require(!Disposed, "provider input is not read after disposal");
            var count = Math.Min(3, Math.Min(buffer.Length, payload.Length - _position));
            payload.AsSpan(_position, count).CopyTo(buffer); _position += count;
            if (count == 0) EndReached = true;
            return count;
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
