namespace Massdex.SyncShark.Engine.Local;

/// <summary>DriveIdentity operations for the local module.</summary>
public static class DriveIdentity
{
    /// <summary>Convert an absolute path for native Windows calls.</summary>
    public static string ToExtendedWindowsPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + fullPath[2..]
            : "\\\\?\\" + fullPath;
    }

    /// <summary>Read operation on the supplied facts.</summary>
    public static (DirectoryIdentity Identity, DriveType Type) Read(string root) => throw new NotImplementedException();
}
