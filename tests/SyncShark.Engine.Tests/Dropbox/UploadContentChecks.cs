namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for UploadContent.</summary>
public static class UploadContentChecks
{
    /// <summary>Verifies the UploadContent contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        CheckFinishCursor();
        var issues = new List<string>();
        try { await StageWindowsAsync(string.Empty); } catch (InvalidDataException e) { issues.Add(e.Message); }
        try { await MalformedStageAsync(); } catch (InvalidDataException e) { issues.Add(e.Message); }
        Check.Require(issues.Count==0,string.Join("; ",issues));
    }

    private static void CheckFinishCursor()
    {
        const string path = "/pair/item";
        string[] wrong = ["other", "closed"];
        long[] offsets = [1, 0];
        for (int i = 0; i < wrong.Length; i++)
        {
            using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new {
                cursor = new { session_id=wrong[i], offset=offsets[i] }, commit = new { path, mode="add", strict_conflict=true, autorename=false }
            }));
            Check.Require(!ReplacementWire.MatchesFinish(json.RootElement,path,"closed",1), "F003 wire accepted a different session or wrong byte offset");
        }
    }
    public static async Task AppendAsync()
    {
        await StageWindowsAsync(null); // No content length; EOF is the only indication that the void reply is empty.
        await StageWindowsAsync("{}");
        await MalformedStageAsync();
    }
    private static async Task StageWindowsAsync(string? appendReply)
    {
        var writer = new global::Massdex.SyncShark.Engine.Dropbox.UploadContent();
        var file = Scenarios.ItemChecks.File(Side.Local, "item", 42, "source");
        var folder = Scenarios.ItemChecks.File(Side.Dropbox, "item", 0, "victim", true);
        var received = new List<byte>();
        bool closed = false;
        var windows = new List<(string Route, long Offset, int Count, bool Closed)>();
        async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            using var args = System.Text.Json.JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
            var chunk = await request.Content!.ReadAsByteArrayAsync(ct);
            received.AddRange(chunk);
            closed = args.RootElement.GetProperty("close").GetBoolean();
            var append = args.RootElement.TryGetProperty("cursor", out var cursor);
            if (append) Check.Require(cursor.GetProperty("session_id").GetString() == "receipt", "Append named a different session from start");
            windows.Add((request.RequestUri!.Segments[^1], append ? cursor.GetProperty("offset").GetInt64() : 0, chunk.Length, closed));
            return new(System.Net.HttpStatusCode.OK) { Content = append && appendReply is null ? new StreamContent(new Sequential([])) : new StringContent(append ? appendReply! : "{\"session_id\":\"receipt\"}") };
        }
        using var bytes = new MemoryStream([42]);
        static Uri Endpoint(string name) => new(new Uri("https://example.invalid/"), name);
        await using var staged = await writer.StageAsync(Send, Endpoint, 2, file.Path, bytes, file, folder, default);
        Check.Require(closed && received.SequenceEqual(new byte[]{42}) && staged.SessionId == "receipt" &&
            !staged.ConsumesSourceOnPublish && staged.ContentHash == file.ContentHash,
            "Folder replacement kept a live source instead of closing a session containing its bytes");
        received.Clear(); windows.Clear();
        using var recovery = new Sequential([1,2,3]);
        using var hash = new ContentHash(); hash.Append([1,2,3]);
        var recovered = file with { Size = 3, ContentHash = hash.Finish() };
        await using var restored = await writer.StageAsync(Send, Endpoint, 2, file.Path, recovery, recovered, null, default);
        Check.Require(windows.SequenceEqual(new[]{("start",0L,2,false),("append_v2",2L,1,true)}) &&
            restored.ContentHash == recovered.ContentHash && received.SequenceEqual(new byte[]{1,2,3}),
            "Nonseekable recovery did not stage all bytes in bounded windows");
        received.Clear(); windows.Clear();
        using var directBytes = new MemoryStream([42]);
        await using var direct = await writer.StageAsync(Send, Endpoint, 2, file.Path, directBytes, file, null, default);
        Check.Require(ReferenceEquals(direct.Content, directBytes) && direct.ConsumesSourceOnPublish && windows.Count == 0,
            "A sole small ordinary upload was sent before publication");
        using var shortRead = new Sequential([1]);
        try
        {
            await using var incomplete = await writer.StageAsync(Send, Endpoint, 2, file.Path, shortRead, recovered, folder, default);
        }
        catch (Exception error) when (error is EndOfStreamException || error is HttpRequestException { InnerException: EndOfStreamException })
        {
            return;
        }
        throw new InvalidDataException("Truncated folder replacement was returned as a closed stage");
    }
    private static async Task MalformedStageAsync()
    {
        foreach (bool badStart in new[]{true,false})
        {
            var file=Scenarios.ItemChecks.File(Side.Local,"item",42,"pc") with { Size=2 };
            using var source=new Sequential([42,42]);
            int sends=0;
            async Task<HttpResponseMessage> Send(HttpRequestMessage request,CancellationToken ct)
            {
                await request.Content!.CopyToAsync(Stream.Null,ct);
                var json=++sends==1 ? badStart ? "{}" : "{\"session_id\":\"receipt\"}" : "{";
                return new(System.Net.HttpStatusCode.OK){Content=new StringContent(json)};
            }
            try { await new global::Massdex.SyncShark.Engine.Dropbox.UploadContent().StageAsync(Send, name=>new Uri(new Uri("https://example.invalid/"),name),1,file.Path,source,file,null,default); }
            catch (ItemFailedException e) when(e.Path==file.Path && e.Cause==TextKey.DropboxRefused && sends==(badStart?1:2)) { continue; }
            catch (NotImplementedException) { throw; }
            catch (Exception e) { throw new InvalidDataException("Malformed session acknowledgement escaped the item's failure path",e); }
            throw new InvalidDataException("Malformed session acknowledgement returned a usable stage");
        }
    }
    /// <summary>Drive the real replace action, not StageAsync in isolation; the wire records any premature delete even if the executor catches its error.</summary>
    public static async Task ReplacementOrderAsync()
    {
        foreach (bool reject in new[] { false, true })
        {
            var pair = Scenarios.ItemChecks.Pair();
            var file = Scenarios.ItemChecks.File(Side.Local, "item", 42, "pc");
            var folder = Scenarios.ItemChecks.File(Side.Dropbox, "item", 0, "id:victim", true);
            var wire = new ReplacementWire(pair.DropboxRoot.TrimEnd('/') + "/item", file, folder, reject);
            var paths = new PathScope(pair, [pair], DriveType.Fixed);
            using var http = new HttpClient(wire);
            using var store = new TestDoubles.InMemoryStateStore();
            var api = new global::Massdex.SyncShark.Engine.Dropbox.DropboxApi(http, TimeProvider.System, Scenarios.ItemChecks.Settings, _ => {},
                _ => ValueTask.FromResult<string?>("synthetic"), "test-namespace");
            var target = new global::Massdex.SyncShark.Engine.Dropbox.DropboxSide(pair, () => paths, api,
                new global::Massdex.SyncShark.Engine.Dropbox.Dispatcher(api, store));
            var local = new TestDoubles.FakeSide(Side.Local); local.Seed(file, [42]);
            var row = new Row(PathNames.DropboxKey(folder.Path), file, folder, null, [], file.ContentHash, RowContext.Neutral with { ProtectedContents = false });
            var action = new global::Massdex.SyncShark.Engine.Planner.SyncAction(ActionKind.ReplaceKind, row, Side: Side.Dropbox);
            try
            {
                var outcome = await new global::Massdex.SyncShark.Engine.Executor.Effects().ApplyAsync(pair, Scenarios.ItemChecks.Settings, _ => {}, paths,
                    action, local, target, store, Scenarios.ItemChecks.Population(pair, paths, local, target, store),
                    Scenarios.ItemChecks.Hop(pair, paths, new global::Massdex.SyncShark.Engine.Run.WorkQueue(), local, target, store), default);
                if (!reject) Check.Require(outcome == global::Massdex.SyncShark.Engine.Executor.EffectOutcome.Applied, "Correct replacement was not applied");
            }
            catch (Exception e) when (reject && wire.StageAttempted && e is HttpRequestException or ItemFailedException) { }
            Check.Require(wire.StageAttempted && (reject ? wire.Deletes == 0 && wire.Finishes == 0 : wire.Deletes == 1 && wire.ClosedBeforeDelete && wire.Finishes == 1 && wire.CreateOnlyAfterDelete),
                "Replacement deleted before verified session closure, failed to attempt the selected delete, or deleted after staging failed");
        }
    }

    private sealed class ReplacementWire(string path, Entry file, Entry folder, bool reject) : HttpMessageHandler
    {
        internal bool StageAttempted, ClosedBeforeDelete, CreateOnlyAfterDelete;
        internal int Deletes, Finishes;
        private bool closed;
        internal static bool MatchesFinish(System.Text.Json.JsonElement arg, string path, string session, long offset)
        {
            var commit = arg.GetProperty("commit");
            var cursor = arg.GetProperty("cursor");
            return cursor.GetProperty("session_id").GetString() == session && cursor.GetProperty("offset").GetInt64() == offset && commit.GetProperty("mode").GetString() == "add" && commit.GetProperty("strict_conflict").GetBoolean() &&
                !commit.GetProperty("autorename").GetBoolean() && commit.GetProperty("path").GetString() == path;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check.Require(request.Headers.Authorization?.Parameter == "synthetic" && request.Headers.TryGetValues("Dropbox-API-Path-Root", out var roots) &&
                roots.Single().Contains("test-namespace", StringComparison.Ordinal), "Unbound replacement transport");
            var name = request.RequestUri!.AbsolutePath;
            var folderJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string,object>{[".tag"]="folder",["id"]=folder.Id,["name"]=folder.Path,["path_display"]=path,["path_lower"]=path.ToLowerInvariant()});
            var status = System.Net.HttpStatusCode.OK;
            string json;
            if (name.EndsWith("/upload_session/start", StringComparison.Ordinal))
            {
                StageAttempted = true;
                using var arg = System.Text.Json.JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
                closed = !reject && arg.RootElement.GetProperty("close").GetBoolean() &&
                    (await request.Content!.ReadAsByteArrayAsync(ct)).SequenceEqual(new byte[]{42});
                if (reject) status = System.Net.HttpStatusCode.BadRequest;
                json = reject ? "{}" : "{\"session_id\":\"closed\"}";
            }
            else if (name.EndsWith("/delete_v2", StringComparison.Ordinal))
            {
                Deletes++;
                using var arg = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                ClosedBeforeDelete = closed && arg.RootElement.GetProperty("path").GetString() == folder.Id;
                json = "{\"metadata\":" + folderJson + "}";
            }
            else if (name.EndsWith("/get_metadata", StringComparison.Ordinal))
            {
                json = folderJson;
                if (Deletes != 0) { status = System.Net.HttpStatusCode.Conflict; json = "{\"error_summary\":\"path/not_found/\",\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"not_found\"}}}"; }
            }
            else if (name.EndsWith("/list_folder", StringComparison.Ordinal)) json = "{\"entries\":[],\"cursor\":\"cursor\",\"has_more\":false}";
            else if (name.EndsWith("/upload_session/finish", StringComparison.Ordinal))
            {
                Finishes++;
                using var arg = System.Text.Json.JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
                CreateOnlyAfterDelete = Deletes == 1 && ClosedBeforeDelete && MatchesFinish(arg.RootElement,path,"closed",file.Size);
                json = System.Text.Json.JsonSerializer.Serialize(new { name=file.Path, path_lower=path.ToLowerInvariant(), path_display=path, id="id:result", rev="rev", size=file.Size, server_modified=file.Modified, client_modified=file.Modified, content_hash=file.ContentHash });
            }
            else throw new InvalidDataException(name);
            return new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Sequential(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }
    /// <summary>Read and seek only within the selected upload window.</summary>
    public static Task WindowAsync()
    {
        using var bytes=new MemoryStream(new byte[]{0,1,2,3});using var window=new global::Massdex.SyncShark.Engine.Dropbox.UploadSlice(bytes,1,2);
        Check.Require(window.ReadByte()==1 && window.ReadByte()==2 && window.ReadByte()==-1 && window.Seek(0,SeekOrigin.Begin)==0 && window.ReadByte()==1,"Upload window escaped its source range");
        return Task.CompletedTask;
    }
}
