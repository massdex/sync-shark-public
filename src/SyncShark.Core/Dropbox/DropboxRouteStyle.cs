namespace Massdex.SyncShark;

/// <summary>How a Dropbox route carries its argument and content (design dropbox-cli.md §2.4).</summary>
public enum DropboxRouteStyle
{
    Rpc,
    Upload,
    Download
}
