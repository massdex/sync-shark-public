namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The closed reply shapes consumed by the Dropbox adapter, not an extensible decoder registry.</summary>
public static class DropboxReplyShapes
{
    /// <summary>Job operation on the supplied facts.</summary>
    public static JobReply Job(System.Text.Json.JsonElement response, AcceptedJob job) => throw new NotImplementedException();
}
