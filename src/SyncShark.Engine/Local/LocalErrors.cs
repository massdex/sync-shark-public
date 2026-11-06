using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>The local file-system exception families (sections 4.2, 4.4).</summary>
public static class LocalErrors
{
    /// <summary>Whether the error is an operational local-file failure, including Win32 failures.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFileFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or Win32Exception;

    /// <summary>Whether the error is a managed I/O failure without a Win32 classification.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsManagedIoFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException;

    /// <summary>The full-volume or exact in-use cause when the error is one, otherwise fallback.</summary>
    public static TextKey Cause(Exception error, TextKey fallback) => error switch
    {
        Win32Exception win32 when win32.NativeErrorCode is ErrorDiskFull or ErrorHandleDiskFull
            => TextKey.FailLocalNoSpace,
        IOException io when io.HResult is DiskFullHResult or HandleDiskFullHResult
            => TextKey.FailLocalNoSpace,
        Win32Exception win32 when win32.NativeErrorCode is ErrorSharingViolation or ErrorLockViolation
            => TextKey.FailLocalInUse,
        _ when error.HResult == ShellAccessDeniedSource => TextKey.FailLocalInUseOrAccessDenied,
        _ when error.HResult is ShellSharingViolationSource or ShellSharingViolationDestination
            or SharingViolationHResult or LockViolationHResult => TextKey.FailLocalInUse,
        _ => fallback
    };

    /// <summary>The root-read cause for a classified local filesystem failure, or null when the error is not one.</summary>
    public static TextKey? RootReadCause(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!IsFileFailure(error)) return null;
        return error switch
        {
            UnsupportedNetworkRootException => TextKey.RootLocalNetworkUnsupported,
            UnsupportedRemovableRootFormatException => TextKey.RootLocalFormatUnsupported,
            WindowsDirectoryListing.DirectoryIdentityMismatchException => TextKey.RootLocalIdentityChanged,
            UnauthorizedAccessException => TextKey.RootLocalAccessDenied,
            _ => TextKey.RootLocalIdentityUnavailable
        };
    }

    // OS facts: Windows ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL, and their HRESULT_FROM_WIN32 forms on
    // IOException, mean the volume is full (section 4.2).
    private const int ErrorDiskFull = 112;
    private const int ErrorHandleDiskFull = 39;
    private const int DiskFullHResult = unchecked((int)0x80070070);
    private const int HandleDiskFullHResult = unchecked((int)0x80070027);

    // Sharing/lock facts: Win32 32/33 and their HRESULT forms, plus the Shell's exact per-item sharing
    // HRESULTs. The Shell source-access-denied 0x80270021 is not ERROR_LOCK_VIOLATION 0x80070021, so the
    // full value is compared, never only its low word (section 4.2).
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int SharingViolationHResult = unchecked((int)0x80070020);
    private const int LockViolationHResult = unchecked((int)0x80070021);
    private const int ShellSharingViolationSource = unchecked((int)0x80270027);
    private const int ShellSharingViolationDestination = unchecked((int)0x80270028);
    private const int ShellAccessDeniedSource = unchecked((int)0x80270021);
}
