namespace Massdex.SyncShark.Engine.Local;

/// <summary>RecycleBin operations for the local module.</summary>
public sealed class RecycleBin
{
    /// <summary>Removable volumes use the plain drive-root Recycle Bin; all other drives use shell recycle-or-abort. Never turn a veto into permanent deletion.</summary>
    public Task<RemovalResult> RemoveAsync(string root, DriveType driveType, Entry expected, RemovalSelection? selection, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Trim only top-level entries older than 30 days on participating removable drives; never trim the shell bin or follow links.</summary>
    public Task TrimAsync(string root, DriveType driveType, DateTimeOffset now, CancellationToken ct) => throw new NotImplementedException();
}
