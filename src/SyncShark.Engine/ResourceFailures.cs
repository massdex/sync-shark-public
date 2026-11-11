using System.ComponentModel;

namespace Massdex.SyncShark.Engine;

/// <summary>The one context-free disk-full classifier (#36): the normalized exception and the Windows full-volume
/// codes, found through wrappers and every aggregate member, never only the first.</summary>
public static class ResourceFailures
{
    /// <summary>The first disk-full exception anywhere in the error, or null.</summary>
    public static Exception? Find(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate)
            {
                foreach (var member in aggregate.InnerExceptions)
                    if (Find(member) is { } found) return found;
                return null;
            }
            if (IsDiskFull(current)) return current;
        }
        return null;
    }

    /// <summary>Whether this exception itself, not its inner chain, reports a full volume.</summary>
    public static bool IsDiskFull(Exception error) => error switch
    {
        InsufficientSpaceException => true,
        Win32Exception native => native.NativeErrorCode is ErrorDiskFull or ErrorHandleDiskFull,
        _ => error.HResult is DiskFullHResult or HandleDiskFullHResult
    };

    /// <summary>The primary a join keeps: a genuine failure stays; disk-full outranks none, cancellation or an
    /// incomplete listing, which a full volume can itself cause.</summary>
    public static Exception? Prefer(Exception? primary, Exception? resource) =>
        resource is null || Find(primary) is not null ||
        primary is not (null or OperationCanceledException or InventoryIncompleteException) ? primary : resource;

    // OS facts: Windows ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL, and their HRESULT_FROM_WIN32 forms on
    // IOException and COM results, mean the volume is full (section 4.2).
    private const int ErrorDiskFull = 112;
    private const int ErrorHandleDiskFull = 39;
    private const int DiskFullHResult = unchecked((int)0x80070070);
    private const int HandleDiskFullHResult = unchecked((int)0x80070027);
}
