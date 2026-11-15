namespace Massdex.SyncShark;

/// <summary>One change-feed event: an add/update carrying the item, or a delete (Entry null) by path.</summary>
internal sealed record DropboxChange(string Path, PilotRemoteEntry? Entry);
