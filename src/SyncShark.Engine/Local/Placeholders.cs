namespace Massdex.SyncShark.Engine.Local;

/// <summary>Placeholders operations for the local module.</summary>
public static class Placeholders
{
    /// <summary>IsOnlineOnly operation on the supplied facts.</summary>
    public static bool IsOnlineOnly(FileAttributes attributes, uint tag) => throw new NotImplementedException();
    /// <summary>Classify a reparse tag without opening its contents.</summary>
    internal static TextKey? Skip(FileAttributes attrs, uint tag) => throw new NotImplementedException();
}
