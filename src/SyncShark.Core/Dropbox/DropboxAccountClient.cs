namespace Massdex.SyncShark;

/// <summary>An opened Dropbox account: its retained binding and a client rooted at the account namespace.</summary>
public sealed record DropboxAccountClient(DropboxConnectionBinding Connection, DropboxPilotClient Client);
