namespace Massdex.SyncShark;

/// <summary>The named purpose of one call inside a verb whose handler composes several routes.</summary>
public enum DropboxCliCall
{
    Upload,
    SessionStart,
    SessionAppend,
    SessionFinish,
    LatestCursor,
    Longpoll,
    Continue
}
