using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Only a complete valid body transfers its ordered page and spool.</summary>
public static class DropboxReplyChecks
{
    public static async Task RunAsync()
    {
        var pair = DropboxEntriesChecks.Pair;
        const string item = """{".tag":"deleted","path_display":"/Parent/Child"}""";
        var json = "{\"entries\":[null],\"ignored\":\"" + new string('x', 70000) + "\",\"entries\":[" + string.Join(",", Enumerable.Repeat(item, 2001)) + "],\"cursor\":\"next\",\"has_more\":true}";
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var spool = new MemoryStream();
        await using (var page = await DropboxReply.ReadAsync(body, DropboxReplyShapes.Page(pair), default, () => spool))
        {
            Check.Require(page.Cursor == "next" && page.HasMore && page.ReadEntries().Count(x => x == new Listed("Child", null)) == 2001, "Replay all received events, including duplicates");
        }
        Check.Require(!spool.CanRead, "Sealed page owns its spool");
        using var bad = new MemoryStream(Encoding.UTF8.GetBytes(json + "!"));
        var abandoned = new MemoryStream();
        try { await DropboxReply.ReadAsync(bad, DropboxReplyShapes.Page(pair), default, () => abandoned); throw new InvalidDataException("Published before EOF"); }
        catch (JsonException) { Check.Require(!abandoned.CanRead, "Failed body leaked its spool"); }
        using var untagged = JsonDocument.Parse("""{"path_display":"/Parent/Child","size":2,"client_modified":"2020-01-01T00:00:00Z","id":"one","rev":"rev"}""");
        var live = DropboxReply.Metadata(untagged.RootElement, pair, "file") with { ContentHash = new string('a', 64) };
        DropboxReply.ValidateUploadResult(live, "child", 2, live.ContentHash);
        try { DropboxReply.ValidateUploadResult(live, "Other", 2, live.ContentHash); }
        catch (ItemFailedException) { return; }
        throw new InvalidDataException("Unrelated upload receipt accepted");
    }
}
