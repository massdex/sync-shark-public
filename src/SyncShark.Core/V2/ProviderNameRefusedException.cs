namespace Massdex.SyncShark.V2;

/// <summary>A definitive provider response that the destination name cannot be stored.</summary>
public sealed class ProviderNameRefusedException(Exception inner) : IOException(Detail, inner)
{
    public const string NoticeKey = "provider-name-refused";
    public const string Detail = "Dropbox refused this name (disallowed_name). The source item is ignored, not removed; " +
        "any existing Dropbox copy remains at its old path. No move or upload completed. Rename the source to sync it, or leave it ignored.";
}
