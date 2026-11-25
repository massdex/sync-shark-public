using System.Text;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Check launch, status and per-position terminal attribution against real wire shapes.</summary>
public static class DropboxReplyShapesChecks
{
    private static async Task<T> Read<T>(string json, DropboxReply<T> reply)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await DropboxReply.ReadAsync(input, reply, default);
    }

    private sealed class EmptyChunkedBody : MemoryStream
    {
        public int RequestedBytes { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            RequestedBytes = buffer.Length;
            return base.ReadAsync(buffer, ct);
        }
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    public static async Task RunAsync()
    {
        using var chunked = new EmptyChunkedBody();
        using var ack = new HttpResponseMessage { Content = new StreamContent(chunked) };
        ack.Headers.TransferEncodingChunked = true;
        Check.Require(ack.Content.Headers.ContentLength is null, "Chunked acknowledgement must not advertise length");
        var empty = await ack.Content.ReadAsStreamAsync();
        try { await DropboxReply.ReadAsync(empty, DropboxReplyShapes.Cursor(), default); throw new InvalidDataException("Empty cursor accepted"); }
        catch (System.Text.Json.JsonException) { }
        await DropboxReply.ReadAsync(empty, DropboxReplyShapes.Void(), default);
        Check.Require(chunked.RequestedBytes == System.Text.Json.JsonSerializerOptions.Default.DefaultBufferSize,
            "Reply reads must use the platform JSON stream buffer size");
        await Read("null", DropboxReplyShapes.Void());
        await Read("{}", DropboxReplyShapes.Void());
        foreach (var malformed in new[] { " ", "{" })
        {
            try { await Read(malformed, DropboxReplyShapes.Void()); throw new InvalidDataException("Malformed Void reply accepted"); }
            catch (System.Text.Json.JsonException) { }
        }
        var pair = DropboxEntriesChecks.Pair;
        var mount = await Read("""{"sharing_info":{"ignored":{"shared_folder_id":false},"shared_folder_id":"mount"},"id":"one","path_display":"/Parent/Child",".tag":"folder"}""", DropboxReplyShapes.Listed(pair));
        Check.Require(mount.Entry is { Path: "Child", SharedRoot: true }, "Streamed folder lost its direct shared_folder_id");
        var ordinary = await Read("""{".tag":"folder","path_display":"/Parent/Child","id":"one","sharing_info":{"ignored":{"shared_folder_id":"nested"}}}""", DropboxReplyShapes.Listed(pair));
        Check.Require(ordinary.Entry is { SharedRoot: false }, "Nested shared_folder_id made an ordinary folder a mount");
        foreach (var unproved in new[] { "{}", """{"id":7}""", """{"id":"id:replacement"}""" })
        {
            // No path_display: an ID mismatch must be decided before any path lookup or mapping.
            try
            {
                var result = await Read("{\"metadata\":" + unproved + "}", DropboxReplyShapes.SelectedDelete(pair, "id:selected"));
                Check.Require(result is null, "Single delete accepted an unproved identity");
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or NotImplementedException)
            { throw new InvalidDataException("Single delete mapped a location before verifying the selected ID", error); }
        }
        // A mismatched receipt has no location to map, even when the location provider is unavailable.
        const string wrongDelete = """{".tag":"complete","entries":[{".tag":"success","metadata":{"id":"id:replacement","path_display":"/Elsewhere/Moved",".tag":"folder"}}]}""";
        try
        {
            var refused = (JobReply.Terminal)(await Read(wrongDelete, DropboxReplyShapes.Submit(pair, "files/delete_batch", [new("id:selected", "Old", null)]))).Reply;
            Check.Require(refused.Members[0] is (null, null, FailureDisposition.UnknownEffect) && refused.AbsentOrdinals.Count == 0,
                "Wrong-ID delete receipt must stay unknown without absence");
        }
        catch (NotImplementedException ex) { throw new InvalidDataException("Wrong-ID delete receipt reached location mapping before identity verification", ex); }
        JobMember[] launch = [new(null, "child", null), new(null, "other", null)];
        const string create = "files/create_folder_batch";
        var (accepted, running) = await Read("""{".tag":"async_job_id","async_job_id":"token"}""", DropboxReplyShapes.Submit(pair, create, launch));
        Check.Require(accepted?.JobId == "token" && accepted.Launch.SequenceEqual(launch) && running is JobReply.Running, "Token and send order");
        var wrong = (JobReply.Terminal)await Read("""{".tag":"complete","entries":[]}""", DropboxReplyShapes.Job(pair, accepted!));
        Check.Require(wrong.Members.Count == 2 && wrong.Members.All(x => x.Entry is null && x.Failure == FailureDisposition.UnknownEffect), "Wrong count attributed a receipt");
        var awaiting = await Read("""{".tag":"in_progress"}""", DropboxReplyShapes.Job(pair, accepted!));
        Check.Require(awaiting is JobReply.Running, "Polling a live job must not indicate completion");
    }

    // The real classifier supplies absence evidence; a compile-only implementation cannot prove it.
    internal static async Task ClassifiedAbsenceAsync()
    {
        var pair = DropboxEntriesChecks.Pair;
        const string finish = "files/upload_session/finish_batch_v2";
        const string conflict = """{".tag":"failure","failure":{".tag":"path","path":{".tag":"conflict","conflict":{".tag":"file"}}}}""";
        const string closed = """{".tag":"failure","failure":{".tag":"lookup_failed","lookup_failed":{".tag":"closed"}}}""";
        const string unknown = """{".tag":"failure","failure":{".tag":"lookup_failed","lookup_failed":{".tag":"other"}}}""";
        var wire = "{\"entries\":[" + string.Join(",", conflict, closed, conflict, unknown) + "]}";
        JobMember[] uploads = [new(null, "Add", null), new(null, "Closed", null), new("id:selected", "Update", null), new(null, "Unknown", null)];
        var members = ((JobReply.Terminal)(await Read(wire, DropboxReplyShapes.Submit(pair, finish, uploads))).Reply).Members;
        Check.Require(members[1] is (null, TextKey.DropboxRefused, FailureDisposition.Permanent), "Closed finish lookup must be a definite refusal, not Unknown");
        Check.Require(members[0] is (null, TextKey.ClashNameTaken, FailureDisposition.Permanent) &&
            members[2] is (null, TextKey.FailChanged, FailureDisposition.Permanent) && members[3].Failure == FailureDisposition.UnknownEffect,
            "Finish conflicts must use each ordinal's add/update mode and retain unknown lookup outcomes");
        const string nonIdMiss = """{".tag":"complete","entries":[{".tag":"failure","failure":{".tag":"path_lookup","path_lookup":{".tag":"not_found"}}}]}""";
        var pathMiss = (JobReply.Terminal)(await Read(nonIdMiss, DropboxReplyShapes.Submit(pair, "files/delete_batch", [new(null, "Old", null)]))).Reply;
        Check.Require(pathMiss.AbsentOrdinals.Count == 0 && pathMiss.Members[0].Failure == FailureDisposition.UnknownEffect, "Path-only delete claimed ID absence");
        const string missingMove = """{".tag":"complete","entries":[{".tag":"failure","failure":{".tag":"relocation_error","relocation_error":{".tag":"from_lookup","from_lookup":{".tag":"not_found"}}}}]}""";
        var moveMiss = (JobReply.Terminal)(await Read(missingMove, DropboxReplyShapes.Submit(pair, "files/move_batch_v2", [new("id:selected", "Old", "New")]))).Reply;
        Check.Require(moveMiss.AbsentOrdinals.Count == 0 && moveMiss.Members[0].Entry is null, "A move lookup miss proved arrival or deletion");
        const string deletion = "files/delete_batch";
        JobMember[] deleted = [new("id:selected", "Old", null), new(null, "PathOnly", null), new("id:other", "Other", null)];
        const string missing = """{".tag":"failure","failure":{".tag":"path_lookup","path_lookup":{".tag":"not_found"}}}""";
        const string summaryOnly = """{".tag":"failure","failure":{".tag":"path_lookup","error_summary":"path_lookup/not_found"}}""";
        var absentJson = "{\".tag\":\"complete\",\"entries\":[" + missing + "," + missing + "," + summaryOnly + "]}";
        var submitted = (JobReply.Terminal)(await Read(absentJson, DropboxReplyShapes.Submit(pair, deletion, deleted))).Reply;
        var checkedJob = (JobReply.Terminal)await Read(absentJson, DropboxReplyShapes.Job(pair, new(pair.PairId, deletion, "token", deleted)));
        foreach (var observed in new[] { submitted, checkedJob })
            Check.Require(observed.AbsentOrdinals.SetEquals([0]) && observed.Members.All(x => x.Entry is null && x.Failure == FailureDisposition.UnknownEffect),
                "Only an exact by-ID miss proves absence, and none of the misses proves our delete removed it");
    }

    // Receipt paths are decoded by the Dropbox root mapper; its implementation is exercised at integration.
    internal static async Task MappedReceiptsAsync()
    {
        var pair = DropboxEntriesChecks.Pair;
        JobMember[] launch = [new(null, "child", null), new(null, "other", null)];
        const string create = "files/create_folder_batch";
        const string complete = """{".tag":"complete","entries":[{".tag":"success","metadata":{"path_display":"/Parent/Child","id":"one"}},{".tag":"unknown"}]}""";
        var (noToken, reply) = await Read(complete, DropboxReplyShapes.Submit(pair, create, launch));
        var members = ((JobReply.Terminal)reply).Members;
        Check.Require(noToken is null && members[0].Entry?.Path == "Child" && members[1].Failure == FailureDisposition.UnknownEffect, "Immediate result keeps actual spelling and isolates malformed member");
        using var raw = System.Text.Json.JsonDocument.Parse(complete);
        var direct = DropboxReplyShapes.Submit(raw.RootElement, pair, create, launch);
        Check.Require(((JobReply.Terminal)direct.Reply).Members.SequenceEqual(members), "Raw submit and streaming submit disagree");
        var job = new AcceptedJob(pair.PairId, create, "token", launch);
        var polled = (JobReply.Terminal)await Read(complete, DropboxReplyShapes.Job(pair, job));
        Check.Require(polled.Members.SequenceEqual(members), "Polling shares terminal attribution");
        var otherPair = (JobReply.Terminal)await Read(complete, DropboxReplyShapes.Job(pair with { PairId = "unrelated" }, job));
        Check.Require(otherPair.Members.All(x => x.Failure == FailureDisposition.UnknownEffect), "Unrelated pair attributed a receipt");
        const string move = "files/move_batch_v2";
        JobMember[] moved = [new("first", "source", "target"), new("second", "other", "end")];
        const string moveReply = """{".tag":"complete","entries":[{".tag":"success","success":{".tag":"folder","id":"wrong","path_display":"/Parent/target"}},{".tag":"success","success":{".tag":"folder","id":"second","path_display":"/Parent/End"}}]}""";
        var movedReply = (JobReply.Terminal)(await Read(moveReply, DropboxReplyShapes.Submit(pair, move, moved))).Reply;
        Check.Require(movedReply.Members[0].Failure == FailureDisposition.UnknownEffect && movedReply.Members[1].Entry is { Path: "End", Id: "second" }, "Move receipt lost its identity or destination");
        var finish = await Read("""{"entries":[{".tag":"success","id":"one","path_display":"/Parent/Child","rev":"rev","size":1,"client_modified":"2020-01-01T00:00:00Z"},{".tag":"other"}]}""", DropboxReplyShapes.Submit(pair, "files/upload_session/finish_batch_v2", launch));
        Check.Require(finish.Accepted is null && ((JobReply.Terminal)finish.Reply).Members[0].Entry?.Revision == "rev", "Untagged synchronous finish envelope");
    }
}
