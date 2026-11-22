namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>An attempt-local decoder;</summary>
public static class DropboxReply
{
    /// <summary>Metadata operation on the supplied facts.</summary>
    public static Entry Metadata(System.Text.Json.JsonElement response, PairConfig pair, string? untaggedKind = null) => throw new NotImplementedException();

    /// <summary>ValidateUploadResult for the selected DropboxReply operation.</summary>
    public static void ValidateUploadResult(Entry returned, string requestedPath, long uploadedBytes, string uploadedHash) => throw new NotImplementedException();
    /// <summary>ReadAsync for the selected DropboxReply operation.</summary>
    public static Task<T> ReadAsync<T>(Stream body, DropboxReply<T> decoder, CancellationToken ct) => throw new NotImplementedException();
}

/// <summary>Decoder for the result of one provider attempt.</summary>
public sealed partial class DropboxReply<T> { }
