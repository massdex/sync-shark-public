using System.Text.Json;
using System.Text.Json.Nodes;

namespace Massdex.SyncShark;

/// <summary>The put handler: one request or an upload session, then a content-hash check.</summary>
public static class DropboxCliPutVerb
{
    /// <summary>Uploads one file, choosing a single request or a session by size.</summary>
    public static async Task<object> RunAsync(IDropboxCliAccess access, DropboxCliArguments arguments,
        Action<string> progress, CancellationToken cancellationToken)
    {
        var settings = access.DropboxCliSettings;
        var account = await DropboxCliRouteRunner.OpenAsync(access, arguments, cancellationToken);
        var runner = new DropboxCliRouteRunner(account, arguments, settings, progress, access.ResultFolder);
        var commit = Commit(arguments);
        await using var source = DropboxCliLocalFiles.OpenInput(arguments.LocalPath(DropboxCliOption.In));
        var length = source.Length;
        var hash = await DropboxContentHash.ComputeAsync(source, cancellationToken);
        source.Position = 0;
        var session = length > settings.SingleUploadMaxBytes;
        JsonElement metadata;
        try
        {
            metadata = session
                ? await SessionAsync(runner, arguments, commit, source, length, settings, cancellationToken)
                : await SingleAsync(runner, arguments, commit, source, cancellationToken);
        }
        catch (Exception error)
        {
            // Cancellation or a local read failure between chunks must retain the logical write receipt too.
            var retained = runner.PreservePendingWrite(error);
            if (ReferenceEquals(retained, error)) throw;
            throw retained;
        }
        var returned = DropboxCliOutput.Str(metadata, "content_hash");
        if (returned is not null && !string.Equals(returned, hash, StringComparison.OrdinalIgnoreCase))
            throw new SyncCliException("verification_failed",
                DropboxCliCatalog.Message(DropboxCliMessage.HashMismatch), SyncCliExitCode.Failed);
        return new DropboxCliUploadData(DropboxCliOutput.Entry(metadata), length, session, returned is not null);
    }

    private static async Task<JsonElement> SingleAsync(DropboxCliRouteRunner runner, DropboxCliArguments arguments,
        JsonObject commit, FileStream source, CancellationToken cancellationToken)
    {
        var upload = arguments.Verb.Call(DropboxCliCall.Upload).Route;
        using var result = await runner.CallAsync(new DropboxRouteCall(upload,
            commit.ToJsonString(), arguments.NamespaceId, ContentIn: source), cancellationToken);
        return result.Result.RootElement.Clone();
    }

    private static async Task<JsonElement> SessionAsync(DropboxCliRouteRunner runner, DropboxCliArguments arguments,
        JsonObject commit, FileStream source, long length, DropboxCliSettings settings,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[settings.UploadChunkBytes];
        var first = await ReadChunkAsync(source, buffer, cancellationToken);
        string sessionId;
        using (var started = await runner.CallAsync(new DropboxRouteCall(
            arguments.Verb.Call(DropboxCliCall.SessionStart).Route,
            new JsonObject { ["close"] = false }.ToJsonString(), arguments.NamespaceId,
            ContentIn: new MemoryStream(buffer, 0, first, writable: false)), cancellationToken, completesWrite: false))
            sessionId = DropboxCliOutput.Str(started.Result.RootElement, "session_id")
                ?? throw new InvalidDataException("Dropbox returned no upload session id.");
        long offset = first;
        while (offset < length)
        {
            var read = await ReadChunkAsync(source, buffer, cancellationToken);
            var cursor = new JsonObject { ["session_id"] = sessionId, ["offset"] = offset };
            var body = new JsonObject { ["cursor"] = cursor, ["close"] = false }.ToJsonString();
            using var appended = await runner.CallAsync(new DropboxRouteCall(
                arguments.Verb.Call(DropboxCliCall.SessionAppend).Route, body, arguments.NamespaceId,
                ContentIn: new MemoryStream(buffer, 0, read, writable: false)), cancellationToken, completesWrite: false);
            offset += read;
        }
        var finish = new JsonObject
        {
            ["cursor"] = new JsonObject { ["session_id"] = sessionId, ["offset"] = offset },
            ["commit"] = commit
        };
        using var finished = await runner.CallAsync(new DropboxRouteCall(
            arguments.Verb.Call(DropboxCliCall.SessionFinish).Route,
            finish.ToJsonString(), arguments.NamespaceId, ContentIn: new MemoryStream(Array.Empty<byte>())),
            cancellationToken);
        return finished.Result.RootElement.Clone();
    }

    private static async Task<int> ReadChunkAsync(FileStream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static JsonObject Commit(DropboxCliArguments arguments)
    {
        var mode = arguments.Value(DropboxCliOption.Mode) ?? "add";
        var writeMode = mode == "update"
            ? new JsonObject { [".tag"] = "update", ["update"] = arguments.Value(DropboxCliOption.Rev) }
            : new JsonObject { [".tag"] = mode };
        var commit = new JsonObject
        {
            ["path"] = arguments.DropboxPath(DropboxCliOption.Path) ?? "",
            ["mode"] = writeMode
        };
        if (mode == "update") commit["strict_conflict"] = true;
        if (arguments.Present(DropboxCliOption.Autorename)) commit["autorename"] = true;
        if (arguments.Present(DropboxCliOption.Mute)) commit["mute"] = true;
        if (arguments.Present(DropboxCliOption.ClientModified))
            commit["client_modified"] = arguments.TemplateValue(DropboxCliOption.ClientModified)!.GetValue<string>();
        return commit;
    }
}
