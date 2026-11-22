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

    public static async Task RunAsync()
    {
        var pair = DropboxEntriesChecks.Pair;
        const string deletion = "files/delete_batch";
        JobMember[] deleted = [new("id:selected", "Old", null), new(null, "PathOnly", null)];
        const string missing = """{".tag":"failure","failure":{".tag":"path_lookup","path_lookup":{".tag":"not_found"}}}""";
        var absentJson = "{\".tag\":\"complete\",\"entries\":[" + missing + "," + missing + "]}";
        var observed = (JobReply.Terminal)(await Read(absentJson, DropboxReplyShapes.Submit(pair, deletion, deleted))).Reply;
        Check.Require(observed.Members[0].Entry is null && observed.Members[0].Failure != FailureDisposition.Permanent &&
            observed.Members[1].Failure == FailureDisposition.UnknownEffect, "Exact by-ID absence is neither a success receipt nor a permanent refusal");
        JobMember[] launch = [new(null, "child", null), new(null, "other", null)];
        const string create = "files/create_folder_batch";
        var (accepted, running) = await Read("""{".tag":"async_job_id","async_job_id":"token"}""", DropboxReplyShapes.Submit(pair, create, launch));
        Check.Require(accepted?.JobId == "token" && accepted.Launch.SequenceEqual(launch) && running is JobReply.Running, "Token and send order");
        const string complete = """{".tag":"complete","entries":[{".tag":"success","metadata":{"path_display":"/Parent/Child","id":"one"}},{".tag":"unknown"}]}""";
        var (noToken, reply) = await Read(complete, DropboxReplyShapes.Submit(pair, create, launch));
        var members = ((JobReply.Terminal)reply).Members;
        Check.Require(noToken is null && members[0].Entry?.Path == "Child" && members[1].Failure == FailureDisposition.UnknownEffect, "Immediate result keeps actual spelling and isolates malformed member");
        using var raw = System.Text.Json.JsonDocument.Parse(complete);
        var direct = DropboxReplyShapes.Submit(raw.RootElement, pair, create, launch);
        Check.Require(((JobReply.Terminal)direct.Reply).Members.SequenceEqual(members), "Raw submit and streaming submit disagree");
        var job = accepted!;
        var polled = (JobReply.Terminal)await Read(complete, DropboxReplyShapes.Job(pair, job));
        Check.Require(polled.Members.SequenceEqual(members), "Polling shares terminal attribution");
        var otherPair = (JobReply.Terminal)await Read(complete, DropboxReplyShapes.Job(pair with { PairId = "unrelated" }, job));
        Check.Require(otherPair.Members.All(x => x.Failure == FailureDisposition.UnknownEffect), "Unrelated pair attributed a receipt");
        const string move = "files/move_batch_v2";
        JobMember[] moved = [new("first", "source", "target"), new("second", "other", "end")];
        const string moveReply = """{".tag":"complete","entries":[{".tag":"success","success":{".tag":"folder","id":"wrong","path_display":"/Parent/target"}},{".tag":"success","success":{".tag":"folder","id":"second","path_display":"/Parent/End"}}]}""";
        var movedReply = (JobReply.Terminal)(await Read(moveReply, DropboxReplyShapes.Submit(pair, move, moved))).Reply;
        Check.Require(movedReply.Members[0].Failure == FailureDisposition.UnknownEffect && movedReply.Members[1].Entry is { Path: "End", Id: "second" }, "Move receipt lost its identity or destination");
        var wrong = (JobReply.Terminal)await Read("""{".tag":"complete","entries":[]}""", DropboxReplyShapes.Job(pair, job));
        Check.Require(wrong.Members.Count == 2 && wrong.Members.All(x => x.Entry is null && x.Failure == FailureDisposition.UnknownEffect), "Wrong count attributed a receipt");
        var finish = await Read("""{"entries":[{".tag":"success","id":"one","path_display":"/Parent/Child","rev":"rev","size":1,"client_modified":"2020-01-01T00:00:00Z"},{".tag":"other"}]}""", DropboxReplyShapes.Submit(pair, "files/upload_session/finish_batch_v2", launch));
        Check.Require(finish.Accepted is null && ((JobReply.Terminal)finish.Reply).Members[0].Entry?.Revision == "rev", "Untagged synchronous finish envelope");
    }
}
