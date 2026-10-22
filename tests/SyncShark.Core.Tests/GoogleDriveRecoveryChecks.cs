using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// Real recovery consumers; the handler supplies protocol responses only. No provider, credentials,
// files, staging, native API or historical-revision API is used. Unexpected requests are test
// failures, never provider refusals which could accidentally satisfy a rejection assertion.
internal static class GoogleDriveRecoveryChecks
{
    private const string PairRoot = "recovery-pair-root";
    private const string RecoveryRoot = "recovery-retention-root";
    private const string Name = "retained-operation-000001.bin";
    private const string Destination = "restored.bin";
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherDigest = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private static readonly Item Source = new("source-id", "source.bin", [PairRoot]);
    private static readonly Item Archive = new("retained-id", Name, [RecoveryRoot]);
    private static readonly Item Restored = new("restored-id", Destination, [PairRoot]);

    internal static async Task RunAsync()
    {
        var failures = new List<Exception>();
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS Google recovery: " + name); }
            catch (Exception error) { failures.Add(new InvalidOperationException(name + ": " + error.Message, error)); }
        }

        await Check("deterministic copy label and subsequent bound lookup", CopyAndLookupAsync);
        await Check("missing lookup is null and never creates content", MissingAsync);
        await Check("wrong account refused before recovery data lookup", WrongAccountAsync);
        await Check("recovery namespace identity accepts the account's own spellings and refuses every other",
            RecoveryNamespaceIdentityAsync);
        foreach (var bad in new[]
        {
            Folder(RecoveryRoot) with { Id = "different-root-id" },
            Folder(RecoveryRoot) with { Trashed = true },
            Folder(RecoveryRoot) with { Mime = "application/octet-stream" },
            Folder(RecoveryRoot) with { Drive = "unapproved-shared-drive" }
        }) await Check("recovery root rejection " + bad.Id + "/" + bad.Mime + "/" + bad.Trashed + "/" + bad.Drive,
            () => RootRejectedAsync(bad));
        await Check("unavailable bound recovery folder", MissingRootAsync);
        foreach (var badName in new[] { "", "short", "../retained-operation-000001.bin", "folder/" + Name,
            "folder\\" + Name, "retained operation-000001.bin", new string('a', 97) })
            await Check("malformed retention name " + JsonSerializer.Serialize(badName), () => BadNameAsync(badName));

        foreach (var current in new[] { Source with { Size = 8 }, Source with { Hash = OtherDigest },
            Source with { Version = "2" }, Source with { Hash = null } })
            await Check("source drift " + current.Size + "/" + current.Version + "/" + current.Hash,
                () => SourceRejectedAsync(current, Remote(Source)));
        await Check("reviewed source has no SHA256", () => SourceRejectedAsync(Source, Remote(Source) with { ContentHash = null }));
        await Check("existing recovery name is create-only", () => CopyOccupiedAsync([Archive]));
        await Check("duplicate recovery name is create-only", () => CopyOccupiedAsync([Archive, Archive with { Id = "duplicate-id" }]));
        await Check("ambiguous recovery lookup refuses duplicate names", DuplicateLookupAsync);
        foreach (var bad in new[] { Archive with { Hash = null }, Archive with { Parents = ["outside-recovery"] },
            Archive with { Name = "different-retention-name.bin" }, Archive with { Drive = "other-corpus" },
            Archive with { Trashed = true }, Archive with { Mime = GoogleDrivePilotClient.FolderMimeType } })
            await Check("lookup rejects unbound/non-binary entry " + bad.Name + "/" + string.Join(',', bad.Parents) +
                "/" + bad.Hash + "/" + bad.Drive + "/" + bad.Trashed + "/" + bad.Mime,
                () => LookupRejectedAsync(bad));

        foreach (var bad in new[] { Archive with { Parents = ["outside-recovery"] }, Archive with { Name = "wrong-name.bin" },
            Archive with { Hash = OtherDigest }, Archive with { Hash = null }, Archive with { Size = 8 },
            Archive with { Trashed = true }, Archive with { Mime = GoogleDrivePilotClient.FolderMimeType } })
            await Check("copy readback rejected " + bad.Name + "/" + string.Join(',', bad.Parents) + "/" + bad.Hash +
                "/" + bad.Size + "/" + bad.Trashed + "/" + bad.Mime, () => CopyReadbackRejectedAsync(bad));

        await Check("restore to exact create-only destination", RestoreAsync);
        foreach (var expected in new[] { Result(Archive) with { FileId = "other-id" },
            Result(Archive) with { Revision = Revision(Archive.Id, "2") }, Result(Archive) with { Size = 8 },
            Result(Archive) with { ContentHash = OtherDigest } })
            await Check("restore exact expected identity " + expected.FileId + "/" + expected.Revision + "/" + expected.Size +
                "/" + expected.ContentHash, () => RestoreRejectedAsync(expected, [Archive]));
        await Check("restore moved archive is absent from bound folder", () => RestoreRejectedAsync(Result(Archive), []));
        await Check("restore refuses a foreign-parent lookup response", () => RestoreRejectedAsync(Result(Archive),
            [Archive with { Parents = ["foreign-root"] }]));
        await Check("restore never overwrites an occupied destination", RestoreOccupiedAsync);
        await Check("restore source drift rechecked before copy", RestoreSourceDriftAsync);

        await Check("delete uses one exact conditional trash, not permanent deletion", DeleteAsync);
        foreach (var bad in new[] { Archive with { Version = "2" }, Archive with { Size = 8 },
            Archive with { Hash = OtherDigest }, Archive with { Hash = null }, Archive with { Trashed = true } })
            await Check("delete changed content " + bad.Version + "/" + bad.Size + "/" + bad.Hash + "/" + bad.Trashed,
                () => DeleteContentRejectedAsync(bad));
        await Check("delete archive outside bound folder is refused before trash", DeleteWrongFolderAsync);
        await Check("uncertain copy response is not replayed", () => EffectFailureAsync(HttpStatusCode.InternalServerError));
        await Check("rejected copy response is not replayed", () => EffectFailureAsync(HttpStatusCode.Conflict));
        if (failures.Count > 0) throw new AggregateException("Google recovery production regressions", failures);
        Console.WriteLine("PASS Google recovery protocol checks; no live/native acceptance or historical revision coverage");
    }

    // Kept compatible with runner conventions without allocating anything in scratchRoot.
    internal static Task RunAsync(string scratchRoot)
    { ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot); return RunAsync(); }

    private static GoogleDriveConnectionBinding Account(string permission = "recovery-account") =>
        new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey(permission), permission, "fixture@example.invalid", "Fixture",
            [GoogleDriveProviderConfiguration.DriveScope], CredentialReference: "synthetic-only").Normalize();
    private static GoogleDrivePilotClient Client(HttpClient http, string permission = "recovery-account") =>
        new(http, new Tokens(), Account(permission), PairRoot, "/Pair");
    private static SyncRemoteRecoveryRootBinding Binding(string permission = "recovery-account") =>
        new("/Recovery", RecoveryRoot, Account(permission).AccountKey);
    private static string Revision(string id, string version) => JsonSerializer.Serialize(new[] { id, version });
    private static PilotRemoteEntry Remote(Item item) => new(item.Name, PilotEntryKind.File, item.Id,
        Revision(item.Id, item.Version), item.Size, item.Hash);
    private static SyncArchiveResult Result(Item item) => new(item.Id, Revision(item.Id, item.Version), Name, item.Size, item.Hash!);
    private static Item Folder(string id) => new(id, id, [], Mime: GoogleDrivePilotClient.FolderMimeType, Size: 0, Hash: null);

    private static async Task CopyAndLookupAsync()
    {
        using var p = new Protocol(); using var http = new HttpClient(p); var client = Client(http);
        CopyPrelude(p); p.Copy(Source.Id, RecoveryRoot, Name, Archive); p.File(Archive.Id, Archive);
        p.File(RecoveryRoot, Folder(RecoveryRoot)); p.List(RecoveryRoot, Name, [Archive]);
        var copied = await client.CopyToRecoveryAsync(Remote(Source), Binding(), Name);
        Require(copied == Result(Archive) && copied.RecoveryPath == Name, "copy result must retain the deterministic name, not provider display path");
        Require(await client.GetRecoveryAsync(Binding(), copied.RecoveryPath) == copied,
            "the returned label must be accepted by the real bound lookup without path reconstruction");
        p.Complete(1);
    }

    private static async Task MissingAsync()
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        p.File(RecoveryRoot, Folder(RecoveryRoot)); p.List(RecoveryRoot, Name, []);
        Require(await Client(http).GetRecoveryAsync(Binding(), Name) is null, "missing retention name must remain absent");
        p.Complete(0);
    }

    /// <summary>
    /// The recovery namespace must name this connection's own bound account, and the two persisted spellings
    /// of that one account are the provider-native permission id and the legacy derived account key. Both are
    /// verified against the real folder; a blank namespace, another account's native or derived namespace and
    /// an inconsistent connection derivation are all refused before any provider request.
    /// </summary>
    private static async Task RecoveryNamespaceIdentityAsync()
    {
        var account = Account();
        var native = account.PermissionId;
        var legacy = account.AccountKey;
        Require(!string.Equals(native, legacy, StringComparison.Ordinal),
            "the native and legacy spellings must be genuinely different so the acceptance is meaningful");
        foreach (var accepted in new[] { native, legacy })
        {
            using var acceptedProtocol = new Protocol();
            using var acceptedHttp = new HttpClient(acceptedProtocol);
            var client = Client(acceptedHttp);
            acceptedProtocol.File(RecoveryRoot, Folder(RecoveryRoot));
            await client.VerifyRecoveryRootAsync(new SyncRemoteRecoveryRootBinding("/Recovery", RecoveryRoot,
                accepted));
            Require(acceptedProtocol.Received.Count == 1,
                "this account's own namespace spelling must verify with its own folder read: " + accepted);
        }
        foreach (var refused in new[] { "", "   ", native + "-other", legacy + "-other", "other-account" })
        {
            using var refusedProtocol = new Protocol();
            using var refusedHttp = new HttpClient(refusedProtocol);
            var client = Client(refusedHttp);
            await RejectAsync(() => client.VerifyRecoveryRootAsync(new SyncRemoteRecoveryRootBinding("/Recovery",
                RecoveryRoot, refused)));
            Require(refusedProtocol.Received.Count == 0,
                "a blank or foreign recovery namespace must be refused before any provider request: " + refused);
        }
        using (var inconsistentProtocol = new Protocol())
        using (var inconsistentHttp = new HttpClient(inconsistentProtocol))
        {
            var inconsistent = new GoogleDrivePilotClient(inconsistentHttp, new Tokens(),
                account with { AccountKey = "other-account-key" }, PairRoot, "/Pair");
            await RejectAsync(() => inconsistent.VerifyRecoveryRootAsync(new SyncRemoteRecoveryRootBinding(
                "/Recovery", RecoveryRoot, native)));
            Require(inconsistentProtocol.Received.Count == 0,
                "an inconsistent connection derivation must be refused before any provider request");
        }
    }

    private static async Task WrongAccountAsync()
    {
        using var p = new Protocol(); using var http = new HttpClient(p); var client = Client(http, "other-account");
        await RejectAsync(() => client.GetRecoveryAsync(Binding(), Name));
        await RejectAsync(() => client.CopyToRecoveryAsync(Remote(Source), Binding(), Name));
        await RejectAsync(() => client.DeleteRecoveryAsync(Binding(), Result(Archive)));
        Require(p.Received.Count == 0, "cross-account recovery must fail before any provider lookup");
        p.File(PairRoot, Folder(PairRoot));
        await RejectAsync(() => client.CopyRecoveryToPathAsync(Binding(), Result(Archive), Destination));
        Require(p.Received.Count == 1, "restore may verify its own pair root but must not read another account's recovery folder");
        p.Complete(0);
    }

    private static async Task RootRejectedAsync(Item root)
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        p.File(RecoveryRoot, root);
        await RejectAsync(() => Client(http).GetRecoveryAsync(Binding(), Name));
        p.Complete(0); // A name lookup after a wrong root is an unexpected request, not a fake rejection.
    }

    private static async Task MissingRootAsync()
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        p.File(RecoveryRoot, Folder(RecoveryRoot), HttpStatusCode.NotFound);
        await RejectAsync(() => Client(http).GetRecoveryAsync(Binding(), Name)); p.Complete(0);
    }

    private static async Task BadNameAsync(string name)
    {
        using var p = new Protocol(); using var http = new HttpClient(p); var client = Client(http);
        p.File(RecoveryRoot, Folder(RecoveryRoot));
        await RejectAsync(() => client.CopyToRecoveryAsync(Remote(Source), Binding(), name));
        p.File(RecoveryRoot, Folder(RecoveryRoot));
        await RejectAsync(() => client.GetRecoveryAsync(Binding(), name)); p.Complete(0);
    }

    private static async Task SourceRejectedAsync(Item current, PilotRemoteEntry expected)
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        p.File(RecoveryRoot, Folder(RecoveryRoot)); p.File(Source.Id, current);
        await RejectAsync(() => Client(http).CopyToRecoveryAsync(expected, Binding(), Name)); p.Complete(0);
    }

    private static async Task CopyOccupiedAsync(Item[] matches)
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        CopyPrelude(p, matches);
        await RejectAsync(() => Client(http).CopyToRecoveryAsync(Remote(Source), Binding(), Name)); p.Complete(0);
    }

    private static async Task DuplicateLookupAsync()
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        p.File(RecoveryRoot, Folder(RecoveryRoot)); p.List(RecoveryRoot, Name, [Archive, Archive with { Id = "duplicate-id" }]);
        await RejectAsync(() => Client(http).GetRecoveryAsync(Binding(), Name)); p.Complete(0);
    }

    private static async Task LookupRejectedAsync(Item match)
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        p.File(RecoveryRoot, Folder(RecoveryRoot)); p.List(RecoveryRoot, Name, [match]);
        await RejectAsync(() => Client(http).GetRecoveryAsync(Binding(), Name)); p.Complete(0);
    }

    private static async Task CopyReadbackRejectedAsync(Item readback)
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        CopyPrelude(p); p.Copy(Source.Id, RecoveryRoot, Name, Archive); p.File(Archive.Id, readback);
        var error = await RejectAsync(() => Client(http).CopyToRecoveryAsync(Remote(Source), Binding(), Name));
        Require(error is GoogleDriveEffectUncertainException, "a sent copy with invalid readback must be reported uncertain, not as no effect");
        p.Complete(1);
    }

    private static async Task RestoreAsync()
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        RestorePrelude(p, [Archive]); p.File(Archive.Id, Archive); p.List(PairRoot, Destination, []);
        p.Copy(Archive.Id, PairRoot, Destination, Restored); p.File(Restored.Id, Restored);
        var restored = await Client(http).CopyRecoveryToPathAsync(Binding(), Result(Archive), Destination);
        Require(restored == Remote(Restored) && restored.RelativePath == Destination,
            "restore result must carry the newly created exact object and requested destination");
        p.Complete(1);
    }

    private static async Task RestoreRejectedAsync(SyncArchiveResult expected, Item[] matches)
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        RestorePrelude(p, matches);
        await RejectAsync(() => Client(http).CopyRecoveryToPathAsync(Binding(), expected, "new-parent/" + Destination));
        p.Complete(0); // No destination lookup or parent creation is scripted before archive admission.
    }

    private static async Task RestoreOccupiedAsync()
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        RestorePrelude(p, [Archive]); p.File(Archive.Id, Archive); p.List(PairRoot, Destination, [Restored]);
        await RejectAsync(() => Client(http).CopyRecoveryToPathAsync(Binding(), Result(Archive), Destination)); p.Complete(0);
    }

    private static async Task RestoreSourceDriftAsync()
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        RestorePrelude(p, [Archive]); p.File(Archive.Id, Archive with { Version = "2" });
        await RejectAsync(() => Client(http).CopyRecoveryToPathAsync(Binding(), Result(Archive), Destination)); p.Complete(0);
    }

    private static async Task DeleteAsync()
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        p.File(RecoveryRoot, Folder(RecoveryRoot)); p.File(Archive.Id, Archive);
        p.List(RecoveryRoot, Name, [Archive]); p.File(Archive.Id, Archive);
        p.Guard(Archive); p.Trash(Archive.Id); p.File(Archive.Id, Archive with { Trashed = true, Version = "2" });
        await Client(http).DeleteRecoveryAsync(Binding(), Result(Archive)); p.Complete(1);
        Require(p.Received.All(r => r.Method != "DELETE") && p.Received.Count(r => r.Method == "PATCH") == 1,
            "recovery deletion must mean one guarded trash request, never permanent deletion");
    }

    private static async Task DeleteContentRejectedAsync(Item current)
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        p.File(RecoveryRoot, Folder(RecoveryRoot)); p.File(Archive.Id, current);
        await RejectAsync(() => Client(http).DeleteRecoveryAsync(Binding(), Result(Archive))); p.Complete(0);
    }

    private static async Task DeleteWrongFolderAsync()
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        p.File(RecoveryRoot, Folder(RecoveryRoot)); p.File(Archive.Id, Archive with { Parents = ["outside-recovery"] });
        p.List(RecoveryRoot, Name, [Archive with { Id = "different-copy-in-bound-folder" }]);
        await RejectAsync(() => Client(http).DeleteRecoveryAsync(Binding(), Result(Archive)));
        // Either the exact metadata parent or the bound name lookup may establish the refusal.
        // Do not demand an unnecessary lookup after the stronger metadata guard has rejected it.
        p.Complete(0, requireAll: false);
    }

    private static async Task EffectFailureAsync(HttpStatusCode status)
    {
        using var p = new Protocol(); using var http = new HttpClient(p);
        CopyPrelude(p); p.Copy(Source.Id, RecoveryRoot, Name, Archive, status);
        var error = await RejectAsync(() => Client(http).CopyToRecoveryAsync(Remote(Source), Binding(), Name));
        Require(status != HttpStatusCode.InternalServerError || error is GoogleDriveEffectUncertainException,
            "server failure after copy dispatch has an uncertain effect outcome");
        p.Complete(1);
    }

    private static void CopyPrelude(Protocol p, Item[]? occupied = null)
    { p.File(RecoveryRoot, Folder(RecoveryRoot)); p.File(Source.Id, Source); p.List(RecoveryRoot, Name, occupied ?? []); }
    private static void RestorePrelude(Protocol p, Item[] matches)
    { p.File(PairRoot, Folder(PairRoot)); p.File(RecoveryRoot, Folder(RecoveryRoot)); p.List(RecoveryRoot, Name, matches); }

    private static async Task<Exception> RejectAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) when (error is IOException || error is InvalidDataException ||
            error is InvalidOperationException || error is GoogleDriveApiException)
        { return error; }
        throw new FixtureFailure("Production accepted an operation that must be refused");
    }
    private static void Require(bool value, string message)
    { if (!value) throw new FixtureFailure(message); }
    private sealed class FixtureFailure(string message) : Exception(message);
    private sealed class Tokens : IGoogleDriveAccessTokenSource
    { public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult("synthetic-not-a-credential"); } }

    private sealed record Item(string Id, string Name, string[] Parents,
        string Mime = "application/octet-stream", long Size = 7, string? Hash = Digest,
        string Version = "1", bool Trashed = false, string? Drive = null)
    {
        internal object Wire() => new { id = Id, name = Name, mimeType = Mime, parents = Parents, trashed = Trashed,
            size = Size.ToString(CultureInfo.InvariantCulture), sha256Checksum = Hash, version = Version,
            headRevisionId = "head-" + Version, driveId = Drive, capabilities = new { canDownload = true } };
    }
    private sealed record ReceivedRequest(string Method, string Path, Dictionary<string, string> Query,
        string? Body, string[] IfMatch);
    private sealed record Step(string Method, string Path, Action<ReceivedRequest>? Check, Func<HttpResponseMessage> Response);

    private sealed class Protocol : HttpMessageHandler
    {
        private readonly Queue<Step> _steps = new();
        private bool _reported;
        internal readonly List<ReceivedRequest> Received = [];
        internal void File(string id, Item item, HttpStatusCode status = HttpStatusCode.OK) =>
            Add("GET", "/drive/v3/files/" + id, null, () => Json(item.Wire(), status));
        internal void List(string parent, string name, Item[] matches) => Add("GET", "/drive/v3/files", r =>
        {
            Require(r.Query.GetValueOrDefault("q") == "'" + parent + "' in parents and name = '" + name + "' and trashed = false",
                "name lookup must use the exact bound parent and deterministic name, never global/name-only search");
            Require(r.Query.GetValueOrDefault("pageSize") == "1000", "lookup remains bounded");
        }, () => Json(new { files = matches.Select(i => i.Wire()).ToArray(), incompleteSearch = false }));
        internal void Copy(string id, string parent, string name, Item result, HttpStatusCode status = HttpStatusCode.OK) =>
            Add("POST", "/drive/v3/files/" + id + "/copy", r =>
            {
                using var payload = JsonDocument.Parse(r.Body ?? "{}"); var value = payload.RootElement;
                Require(value.EnumerateObject().Count() == 2 && value.GetProperty("name").GetString() == name &&
                    value.GetProperty("parents").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { parent }),
                    "copy has only the exact new name and one bound destination parent; no overwrite or sharing changes");
            }, () => Json(result.Wire(), status));
        internal void Guard(Item item) => Add("GET", "/drive/v2/files/" + item.Id, null, () => Json(new
        {
            id = item.Id, version = item.Version, mimeType = item.Mime, etag = "\"fixture-guard\"",
            labels = new { trashed = item.Trashed }, parents = item.Parents.Select(id => new { id }).ToArray()
        }));
        internal void Trash(string id) => Add("PATCH", "/drive/v2/files/" + id, r =>
        {
            Require(r.IfMatch.SequenceEqual(new[] { "\"fixture-guard\"" }), "trash carries the exact strong server conditional guard");
            using var payload = JsonDocument.Parse(r.Body ?? "{}"); var value = payload.RootElement;
            Require(value.EnumerateObject().Count() == 1 && value.GetProperty("labels").EnumerateObject().Count() == 1 &&
                value.GetProperty("labels").GetProperty("trashed").GetBoolean(), "trash patch cannot move, rename or change content");
        }, () => Json(new { id }));
        private void Add(string method, string path, Action<ReceivedRequest>? check, Func<HttpResponseMessage> response) =>
            _steps.Enqueue(new(method, path, check, response));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); var uri = request.RequestUri!;
            Require(uri.Scheme == "https" && uri.Host == "www.googleapis.com" && uri.UserInfo.Length == 0,
                "every synthetic recovery request must target the exact official Google API origin");
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Split('=', 2)).ToDictionary(s => Uri.UnescapeDataString(s[0]),
                    s => s.Length == 2 ? Uri.UnescapeDataString(s[1]) : "", StringComparer.Ordinal);
            var received = new ReceivedRequest(request.Method.Method, uri.AbsolutePath, query,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.IfMatch.Select(v => v.ToString()).ToArray());
            Received.Add(received);
            Require(_steps.Count > 0, "unexpected request after required refusal: " + Describe(received));
            var step = _steps.Dequeue();
            Require(received.Method == step.Method && received.Path == step.Path,
                "request ordering: expected " + step.Method + " " + step.Path + ", received " + Describe(received));
            Require(query.GetValueOrDefault("supportsAllDrives") == "false",
                "My Drive fixtures cannot silently expand into shared drives");
            if (received.Method == "GET") Require(received.Body is null, "read-only lookups have no effect body");
            step.Check?.Invoke(received);
            return step.Response();
        }

        internal void Complete(int effects, bool requireAll = true)
        {
            Require(!requireAll || _steps.Count == 0, "production skipped required identity/readback requests");
            Require(Received.Count(r => r.Method != "GET") == effects, "unexpected recovery effect count");
            Report();
        }
        private void Report()
        {
            if (_reported) return;
            _reported = true;
            Console.WriteLine("Google recovery requests: " + string.Join(" -> ", Received.Select(Describe)) +
                "; effects=" + Received.Count(r => r.Method != "GET"));
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Report(); // Keep actual request order/effects even when a regression fails.
            base.Dispose(disposing);
        }
        private static string Describe(ReceivedRequest r) => r.Method + " " + r.Path;
        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
