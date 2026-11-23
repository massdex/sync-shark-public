using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DropboxWriter.</summary>
public static class DropboxWriterChecks
{
    /// <summary>Observe final move receipts and refusals through the production side and dispatcher.</summary>
    public static async Task RunAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        var folder = Scenarios.ItemChecks.File(Side.Dropbox, "old", 0, "id:selected", true);
        var selected = folder;
        const string destination = "new";
        var replies = new Queue<(System.Net.HttpStatusCode Status, string Body)>();
        foreach (var (id, name) in new[] { (folder.Id, "New"), ("id:neighbour", "New"), (folder.Id, "elsewhere") })
        {
            var metadata = new Dictionary<string,string> { [".tag"] = "folder", ["name"] = name, ["id"] = id,
                ["path_display"] = pair.DropboxRoot + "/" + name, ["path_lower"] = (pair.DropboxRoot + "/" + name).ToLowerInvariant() };
            replies.Enqueue((System.Net.HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new { metadata })));
        }
        replies.Enqueue((System.Net.HttpStatusCode.Conflict, """{"error":{".tag":"too_many_files"}}"""));
        replies.Enqueue((System.Net.HttpStatusCode.Conflict, """{"error":{".tag":"unrecognized"}}"""));
        using var http = new HttpClient(new DropboxSideChecks.ReplyWire(async (request, ct) =>
        {
            using var arg = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(ct));
            Check.Require(request.RequestUri!.AbsolutePath.EndsWith("/files/move_v2", StringComparison.Ordinal) &&
                arg.RootElement.GetProperty("from_path").GetString() == selected.Id &&
                arg.RootElement.GetProperty("to_path").GetString() == pair.DropboxRoot + "/" + destination,
                "Move did not send the selected ID and destination");
            var reply = replies.Dequeue();
            return new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body) };
        }));
        using var store = new TestDoubles.InMemoryStateStore();
        var api = DropboxSideChecks.BoundApi(http, TimeProvider.System);
        ISide side = new DropboxSide(pair, () => throw new InvalidOperationException(), api, new Dispatcher(api, store));
        Task<RemovalResult> Move() => side.MoveAsync(selected, destination, default);
        Check.Require(await Move() is { Outcome: RemovalOutcome.Published, Final: { Path: "New", Id: "id:selected" } },
            "Move discarded its attributed terminal receipt or changed the returned spelling");
        var wrongId = await Move(); var wrongPath = await Move();
        Check.Require(wrongId.Outcome == RemovalOutcome.Unknown && wrongPath.Outcome == RemovalOutcome.Unknown,
            "Move claimed success without a receipt matching the selected ID and destination");
        var refused = await Move(); var uncertain = await Move();
        Check.Require(refused is { Outcome: RemovalOutcome.NoEffect, Cause: TextKey.FailDropboxTooManyFiles } && refused.Final == folder &&
            uncertain.Outcome == RemovalOutcome.Unknown && replies.Count == 0,
            "Only the selected folder move's definite size refusal may authorize a split");
        selected = Scenarios.ItemChecks.File(Side.Dropbox, "old-file", 1, "id:selected-file");
        replies.Enqueue((System.Net.HttpStatusCode.Conflict, """{"error":{".tag":"too_many_files"}}"""));
        try { await Move(); }
        catch (ItemFailedException failure) when (failure.Cause == TextKey.FailDropboxTooManyFiles &&
            failure.Disposition == FailureDisposition.Permanent && failure.Path == selected.Path && replies.Count == 0) { return; }
        throw new InvalidDataException("A selected file's definite size refusal must remain a file failure, not a folder split");
    }
    /// <summary>Direct staging retains the source; a closed session owns a complete digest and no source stream.</summary>
    public static Task StagedAsync()
    {
        using var bytes=new MemoryStream(new byte[]{1});var file=Scenarios.ItemChecks.File(Side.Local,"item",1,"id");
        var direct=global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Single(file.Path,bytes,file);var session=global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Session(file.Path,"session",1,file,file.ContentHash);
        Check.Require(direct.ConsumesSourceOnPublish && direct.Content==bytes && !session.ConsumesSourceOnPublish && session.Content==null && session.ContentHash==file.ContentHash,"Staging confused a completed session with pending input");return Task.CompletedTask;
    }
}
