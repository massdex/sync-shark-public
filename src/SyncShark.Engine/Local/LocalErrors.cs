namespace Massdex.SyncShark.Engine.Local;

/// <summary>The local file-system exception families</summary>
public static class LocalErrors
{
    /// <summary>IsFileFailure operation on the supplied facts.</summary>
    public static bool IsFileFailure(Exception error) => throw new NotImplementedException();
    /// <summary>Cause operation on the supplied facts.</summary>
    public static TextKey Cause(Exception error, TextKey fallback) => throw new NotImplementedException();
    /// <summary>Recognize managed filesystem failures.</summary>
    public static bool IsManagedIoFailure(Exception error) => throw new NotImplementedException();
    /// <summary>Return the typed failure for an unreadable root.</summary>
    public static TextKey? RootReadCause(Exception error) => throw new NotImplementedException();
}
