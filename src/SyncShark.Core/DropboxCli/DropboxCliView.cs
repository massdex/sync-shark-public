namespace Massdex.SyncShark;

/// <summary>Every human-output template of the Dropbox group, keyed exactly as in <c>dropbox-cli.json</c> views.</summary>
public enum DropboxCliView
{
    EntryLine,
    EntryTime,
    Absent,
    None,
    PageFooter,
    BatchFailed,
    BatchFooter,
    RoutesHeader,
    RouteLine,
    RouteGranted,
    RouteSpecial
}
