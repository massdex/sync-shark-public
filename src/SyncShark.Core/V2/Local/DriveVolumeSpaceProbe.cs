namespace Massdex.SyncShark.V2;

/// <summary>The production probe over DriveInfo.</summary>
internal sealed class DriveVolumeSpaceProbe : IVolumeSpaceProbe
{
    public long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException)
        {
            return null;
        }
    }
}
