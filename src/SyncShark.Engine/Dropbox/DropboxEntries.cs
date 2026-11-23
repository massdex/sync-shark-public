namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The one Dropbox metadata parser</summary>
public sealed class DropboxEntries
{
    /// <summary>Decode operation on the supplied facts.</summary>
    public Listed Decode(System.Text.Json.JsonElement entry, PairConfig pair) => throw new NotImplementedException();

    /// <summary>Parse for the selected DropboxEntries operation.</summary>
    public static Listed Parse(System.Text.Json.JsonElement metadata, PairConfig pair, string? untaggedKind = null) => throw new NotImplementedException();
}
