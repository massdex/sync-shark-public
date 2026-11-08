namespace Massdex.SyncShark;

/// <summary>A named Dropbox route behaviour the typed verbs handle instead of the raw caller (design §2.2).</summary>
public enum DropboxRouteSpecial
{
    Continue,
    AsyncCheck,
    UploadSession,
    Longpoll
}
