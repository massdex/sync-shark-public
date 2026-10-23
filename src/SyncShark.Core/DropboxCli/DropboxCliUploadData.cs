namespace Massdex.SyncShark;

/// <summary>The outcome of an upload: the committed entry, bytes, whether a session was used and hash proof.</summary>
public sealed record DropboxCliUploadData(DropboxCliEntry Entry, long Bytes, bool SessionUsed, bool Verified)
    : IDropboxCliData;
