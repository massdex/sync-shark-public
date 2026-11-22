namespace Massdex.SyncShark.Engine.Local;

/// <summary>DriveIdentity operations for the local module.</summary>
public static class DriveIdentity
{
    /// <summary>Convert an absolute path for native Windows calls.</summary>
    public static string ToExtendedWindowsPath(string path) => throw new NotImplementedException();

    /// <summary>Read operation on the supplied facts.</summary>
    public static (DirectoryIdentity Identity, DriveType Type) Read(string root) => throw new NotImplementedException();
}
