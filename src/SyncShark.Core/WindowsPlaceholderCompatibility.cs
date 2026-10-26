using System.Runtime.InteropServices;

namespace Massdex.SyncShark;

/// <summary>Outcome of one process-wide placeholder compatibility attempt.</summary>
public enum PlaceholderCompatibilityStatus
{
    /// <summary>No attempt has been made in this process.</summary>
    NotAttempted,
    /// <summary>This is not a Windows process; the native API was not called.</summary>
    NotWindows,
    /// <summary>The process now exposes placeholder attributes and reparse points.</summary>
    Exposed,
    /// <summary>ntdll does not export the API; the process keeps each file's default attributes.</summary>
    Unavailable,
    /// <summary>The API ran but did not confirm PHCM_EXPOSE_PLACEHOLDERS.</summary>
    Failed
}

/// <summary>The native result, retained for diagnostics and the startup check.</summary>
public readonly record struct PlaceholderCompatibilityResult(
    PlaceholderCompatibilityStatus Status,
    int PreviousMode = -1,
    int ConfirmedMode = -1,
    string? Detail = null)
{
    public bool IsExposed => Status == PlaceholderCompatibilityStatus.Exposed;

    public string Describe() => Status switch
    {
        PlaceholderCompatibilityStatus.NotWindows => "not a Windows process; no native call was made",
        PlaceholderCompatibilityStatus.Exposed =>
            $"exposed (previous mode {PreviousMode}, confirmed mode {ConfirmedMode})",
        PlaceholderCompatibilityStatus.Unavailable =>
            "the placeholder compatibility API is unavailable: " + (Detail ?? "unknown reason"),
        PlaceholderCompatibilityStatus.Failed =>
            "placeholder exposure failed: " + (Detail ?? "unknown native result"),
        _ => "not attempted"
    };
}

/// <summary>
/// Sets the process-wide Windows Cloud Files placeholder compatibility mode to
/// PHCM_EXPOSE_PLACEHOLDERS (2) before the process performs file system I/O.
///
/// Windows' application-default mode (PHCM_APPLICATION_DEFAULT) can disguise placeholder attributes
/// for compatibility (PHCM_DISGUISE_PLACEHOLDER), which hides the reparse point and OFFLINE /
/// RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS bits from ordinary Win32 enumeration. Sync Shark
/// classifies pointers and foreign stubs from exactly those bits, so every process that enumerates a
/// sync root must request exposure explicitly.
///
/// The documented signature is <c>CHAR RtlSetProcessPlaceholderCompatibilityMode(CHAR Mode)</c>
/// (ntifs.h; Windows 10 version 1803 and later). The mode is marshalled as <c>sbyte</c>
/// rather than <c>int</c> because a CHAR return leaves the upper return-register bits
/// undefined, so reading a full int could turn -1 (0xFF) into an apparent positive value.
/// When the export is absent, the process keeps Windows' default mode: the helper returns
/// <see cref="PlaceholderCompatibilityStatus.Unavailable"/> and never throws, so an older Windows
/// version cannot take down unrelated synchronization. Callers that need certainty (the startup
/// check) must inspect the result rather than assume success.
/// </summary>
public static class WindowsPlaceholderCompatibility
{
    public const int ApplicationDefault = 0;
    public const int DisguisePlaceholder = 1;
    public const int ExposePlaceholders = 2;

    private const string Ntdll = "ntdll.dll";
    private const string EntryPoint = "RtlSetProcessPlaceholderCompatibilityMode";

    private static readonly object Gate = new();
    private static PlaceholderCompatibilityResult _last = new(PlaceholderCompatibilityStatus.NotAttempted);

    /// <summary>The result of the one-time exposure attempt, or NotAttempted before the first call.</summary>
    public static PlaceholderCompatibilityResult LastResult
    {
        get { lock (Gate) return _last; }
    }

    /// <summary>
    /// Exposes placeholder attributes once for this process. Safe to call from every entry point:
    /// non-Windows processes return <see cref="PlaceholderCompatibilityStatus.NotWindows"/> without a
    /// native call, and a native failure is reported rather than thrown.
    /// </summary>
    public static PlaceholderCompatibilityResult Expose()
    {
        lock (Gate)
        {
            if (_last.Status is PlaceholderCompatibilityStatus.Exposed
                or PlaceholderCompatibilityStatus.Unavailable
                or PlaceholderCompatibilityStatus.Failed
                or PlaceholderCompatibilityStatus.NotWindows)
                return _last;
            _last = OperatingSystem.IsWindows()
                ? Apply()
                : new(PlaceholderCompatibilityStatus.NotWindows,
                    Detail: "Windows Cloud Files placeholder modes do not exist on this platform.");
            // Keep an unavailable or failed exposure observable without writing a file before the
            // mode is set, and without turning it into a console line that a CLI caller cannot opt out of.
            if (_last.Status is PlaceholderCompatibilityStatus.Unavailable or PlaceholderCompatibilityStatus.Failed)
                System.Diagnostics.Trace.WriteLine("Windows placeholder compatibility: " + _last.Describe());
            return _last;
        }
    }

    private static PlaceholderCompatibilityResult Apply()
    {
        IntPtr module = IntPtr.Zero;
        try
        {
            if (!NativeLibrary.TryLoad(Ntdll, out module))
                return new(PlaceholderCompatibilityStatus.Unavailable,
                    Detail: Ntdll + " could not be loaded.");
            if (!NativeLibrary.TryGetExport(module, EntryPoint, out var entry))
                return new(PlaceholderCompatibilityStatus.Unavailable,
                    Detail: Ntdll + " does not export " + EntryPoint + "; this Windows version predates it.");
            var setMode = Marshal.GetDelegateForFunctionPointer<SetMode>(entry);
            // The function returns the mode that was in effect before this call; a negative value is
            // PHCM_ERROR_INVALID_PARAMETER (-1) or PHCM_ERROR_NO_TEB (-2).
            var previous = setMode((sbyte)ExposePlaceholders);
            if (previous < 0)
                return new(PlaceholderCompatibilityStatus.Failed, previous, -1,
                    EntryPoint + " rejected PHCM_EXPOSE_PLACEHOLDERS with " + previous + ".");
            // A second set returns the current mode. It changes nothing, and proves the first call
            // actually took effect instead of merely reporting a plausible previous mode.
            var confirmed = setMode((sbyte)ExposePlaceholders);
            if (confirmed != ExposePlaceholders)
                return new(PlaceholderCompatibilityStatus.Failed, previous, confirmed,
                    "the process mode remained " + confirmed + " after exposure.");
            return new(PlaceholderCompatibilityStatus.Exposed, previous, confirmed);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or MarshalDirectiveException or ArgumentException)
        {
            return new(PlaceholderCompatibilityStatus.Unavailable,
                Detail: EntryPoint + " could not be called: " + error.Message);
        }
        catch (Exception error)
        {
            // A platform helper must never turn an ordinary sync into a crash.
            return new(PlaceholderCompatibilityStatus.Failed,
                Detail: EntryPoint + " failed: " + error.Message);
        }
        finally
        {
            // Freeing is bookkeeping only; it must never turn an exposure failure into a crash.
            if (module != IntPtr.Zero)
            {
                try { NativeLibrary.Free(module); } catch (Exception) { }
            }
        }
    }

    // CHAR (sbyte), not int: see the ABI note on the type summary. The public mode constants stay
    // int for callers and tests; only the native boundary is narrow.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate sbyte SetMode(sbyte mode);
}
