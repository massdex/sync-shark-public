namespace Massdex.SyncShark;

/// <summary>Whether a Dropbox route reads, writes or is refused before any request (design §2.2).</summary>
public enum DropboxRoutePolicy
{
    Read,
    Write,
    Denied
}
