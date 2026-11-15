namespace Massdex.SyncShark;

/// <summary>The handler that runs one Dropbox verb, keyed exactly as in <c>dropbox-cli.json</c>.</summary>
public enum DropboxCliHandler
{
    Route,
    Put,
    Changes,
    Api,
    Routes
}
