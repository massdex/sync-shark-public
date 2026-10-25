using System.Text.Json.Nodes;

namespace Massdex.SyncShark;

/// <summary>The changes handler: latest cursor, an optional long poll, then one change page.</summary>
public static class DropboxCliChangesVerb
{
    /// <summary>Reads the latest cursor, or long-polls and returns one continue page.</summary>
    public static async Task<object> RunAsync(IDropboxCliAccess access, DropboxCliArguments arguments,
        Action<string> progress, CancellationToken cancellationToken)
    {
        var account = await DropboxCliRouteRunner.OpenAsync(access, arguments, cancellationToken);
        var runner = new DropboxCliRouteRunner(account, arguments, access.DropboxCliSettings, progress, access.ResultFolder);
        if (!arguments.Present(DropboxCliOption.Cursor))
        {
            var body = new JsonObject
            {
                ["path"] = arguments.DropboxPath(DropboxCliOption.Path) ?? "",
                ["recursive"] = arguments.Present(DropboxCliOption.Recursive),
                ["include_deleted"] = true
            };
            using var latest = await runner.CallAsync(new DropboxRouteCall(
                arguments.Verb.Call(DropboxCliCall.LatestCursor).Route, body.ToJsonString(), arguments.NamespaceId),
                cancellationToken);
            return new DropboxCliChangesData(DropboxCliOutput.Str(latest.Result.RootElement, "cursor") ?? "",
                null, null, null, null);
        }

        var cursor = arguments.Value(DropboxCliOption.Cursor)!;
        if (arguments.Present(DropboxCliOption.Seconds))
        {
            var poll = new JsonObject { ["cursor"] = cursor, ["timeout"] = arguments.Int(DropboxCliOption.Seconds) };
            using var polled = await runner.CallAsync(new DropboxRouteCall(
                arguments.Verb.Call(DropboxCliCall.Longpoll).Route, poll.ToJsonString(), null), cancellationToken);
            var pollRoot = polled.Result.RootElement;
            if (DropboxCliOutput.Bool(pollRoot, "changes") == false)
                return new DropboxCliChangesData(cursor, false,
                    (int?)DropboxCliOutput.Long(pollRoot, "backoff"), null, null);
        }

        using var page = await runner.CallAsync(new DropboxRouteCall(
            arguments.Verb.Call(DropboxCliCall.Continue).Route, new JsonObject { ["cursor"] = cursor }.ToJsonString(),
            arguments.NamespaceId), cancellationToken);
        var root = page.Result.RootElement;
        var entries = new List<DropboxCliEntry>();
        if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
            root.TryGetProperty("entries", out var array) &&
            array.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var item in array.EnumerateArray()) entries.Add(DropboxCliOutput.Entry(item));
        return new DropboxCliChangesData(DropboxCliOutput.Str(root, "cursor") ?? cursor, null, null, entries,
            DropboxCliOutput.Bool(root, "has_more"));
    }
}
