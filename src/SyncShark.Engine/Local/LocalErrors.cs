using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>The local file-system exception families (sections 4.2, 4.4).</summary>
public static class LocalErrors
{
    // winerror.h native status codes; preserve the exact legacy FileIdInfo fallback set.
    // ERROR_INVALID_FUNCTION, ERROR_INVALID_PARAMETER, ERROR_ACCESS_DENIED (winerror.h).
    internal const int ErrorInvalidFunction=1, ErrorInvalidParameter=87, ErrorAccessDenied=5;
    private const int ErrorNotSupported=50, ErrorCallNotImplemented=120, ErrorInvalidLevel=124;
    internal const int ErrorFileNotFound=2, ErrorPathNotFound=3, ErrorAlreadyExists=183;
    internal static bool FileIdInfoUnsupported(int code) => code is ErrorInvalidFunction or ErrorNotSupported
        or ErrorInvalidParameter or ErrorCallNotImplemented or ErrorInvalidLevel;
    internal static bool Missing(int hresult) => (hresult & ushort.MaxValue) is ErrorFileNotFound or ErrorPathNotFound; // winerror.h HRESULT_CODE

    /// <summary>Whether the error is an operational local-file failure, including Win32 failures.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFileFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or Win32Exception;

    /// <summary>Whether the error is a managed I/O failure without a Win32 classification.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsManagedIoFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException;

    /// <summary>Classifies exact native and managed causes without recording run state.</summary>
    public static TextKey Cause(Exception error, TextKey fallback) => error switch
    {
        IOException { InnerException: Win32Exception native } => Cause(native, fallback),
        InsufficientSpaceException or Win32Exception { NativeErrorCode: 112 or 39 } => TextKey.FailLocalNoSpace, // winerror.h ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL
        _ when error.HResult is DiskFullHResult or HandleFullHResult => TextKey.FailLocalNoSpace,
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

        return error switch
        {
            ItemFailedException { Cause: TextKey.RootLocalNetworkUnsupported or TextKey.RootLocalFormatUnsupported } f => f.Cause,
            ChangedUnderneathException => TextKey.RootLocalIdentityChanged,
            UnauthorizedAccessException or Win32Exception { NativeErrorCode: ErrorAccessDenied } => TextKey.RootLocalAccessDenied,
            _ when IsFileFailure(error) => TextKey.RootLocalIdentityUnavailable,
            _ => null
        };
    }

    // Sharing/lock facts: Win32 32/33 and their HRESULT forms, plus the Shell's exact per-item sharing
    // HRESULTs. The Shell source-access-denied 0x80270021 is not ERROR_LOCK_VIOLATION 0x80070021, so the
    // full value is compared, never only its low word (section 4.2).
    private const int DiskFullHResult = unchecked((int)0x80070070);
    private const int HandleFullHResult = unchecked((int)0x80070027);
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int SharingViolationHResult = unchecked((int)0x80070020);
    private const int LockViolationHResult = unchecked((int)0x80070021);
    private const int ShellSharingViolationSource = unchecked((int)0x80270027);
    private const int ShellSharingViolationDestination = unchecked((int)0x80270028);
    private const int ShellAccessDeniedSource = unchecked((int)0x80270021);
}
