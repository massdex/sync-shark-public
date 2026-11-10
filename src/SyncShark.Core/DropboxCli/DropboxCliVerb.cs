namespace Massdex.SyncShark;

/// <summary>The 17 new Dropbox verbs, keyed exactly as in <c>dropbox-cli.json</c>.</summary>
public enum DropboxCliVerb
{
    Whoami,
    Usage,
    Ls,
    Stat,
    Get,
    Put,
    Mkdir,
    Rm,
    Mv,
    Cp,
    Search,
    Revisions,
    Restore,
    Changes,
    Thumbnail,
    Api,
    Routes
}
