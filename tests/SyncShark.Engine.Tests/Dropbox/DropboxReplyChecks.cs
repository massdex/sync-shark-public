using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Only a complete valid body transfers its ordered page and spool.</summary>
public static class DropboxReplyChecks
{
    public static async Task RunAsync()
    {
        await DropboxReplyShapesChecks.ClassifiedAbsenceAsync();
        var pair = DropboxEntriesChecks.Pair;
        JobMember[] selected = [new("id:one", "Old", null), new("id:two", "Other", null)];
        const string route = "files/delete_batch";
        using var receipt = JsonDocument.Parse("""{".tag":"complete","entries":[{".tag":"success","metadata":{".tag":"folder","id":"id:one","path_display":"/Parent/New"}},{".tag":"success","metadata":{".tag":"folder","id":"id:replacement","path_display":"/Parent/Other"}}]}""");
        var immediate = DropboxReplyShapes.Submit(receipt.RootElement, pair, route, selected).Reply;
        var polled = DropboxReplyShapes.Job(receipt.RootElement, new AcceptedJob(pair.PairId, route, "token", selected), pair);
        foreach (var result in new[] { immediate, polled }.Cast<JobReply.Terminal>())
            Check.Require(result.Members[0].Entry is { Path: "New", Id: "id:one" } && result.Members[1].Entry is null && result.Members[1].Failure == FailureDisposition.UnknownEffect,
                "Relocated by-ID deletion must preserve the receipt and reject a replacement ID");
        await DropboxReplyShapesChecks.MappedReceiptsAsync();
        const string item = """{".tag":"deleted","path_display":"/Parent/Child"}""";
        var json = "{\"entries\":[null],\"ignored\":\"" + new string('x', 70000) + "\",\"entries\":[" + string.Join(",", Enumerable.Repeat(item, 2001)) + "],\"cursor\":\"next\",\"has_more\":true}";
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await using (var page = await DropboxReply.ReadAsync(body, DropboxReplyShapes.Page(pair), default))
        {
            Check.Require(page.Cursor == "next" && page.HasMore && page.ReadEntries().Count(x => x == new Listed("Child", null)) == 2001, "Replay all received events, including duplicates");
        }
        using var bad = new MemoryStream(Encoding.UTF8.GetBytes(json + "!"));
        try { await DropboxReply.ReadAsync(bad, DropboxReplyShapes.Page(pair), default); throw new InvalidDataException("Published before EOF"); }
        catch (JsonException) { /* Trailing content invalidated the complete page. */ }
        using var untagged = JsonDocument.Parse("""{"path_display":"/Parent/Child","size":2,"client_modified":"2020-01-01T00:00:00Z","id":"one","rev":"rev"}""");
        var live = DropboxReply.Metadata(untagged.RootElement, pair, "file") with { ContentHash = new string('a', 64) };
        DropboxReply.ValidateUploadResult(live, "child", 2, live.ContentHash);
        try { DropboxReply.ValidateUploadResult(live, "Other", 2, live.ContentHash); }
        catch (ItemFailedException) { return; }
        throw new InvalidDataException("Unrelated upload receipt accepted");
    }
}
