using System.ComponentModel;

namespace Massdex.SyncShark;

/// <summary>One exact-path diagnostic for an I/O exception, including when callers wrap and rethrow it.</summary>
internal static class NativeIoDiagnostic
{
    private const string LoggedKey = "SyncShark.NativeIo.Logged";
    private const string PathKey = "SyncShark.NativeIo.Path";

    internal static string Describe(string path, Exception error)
    {
        Exception? native = null;
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current.Data[PathKey] is string exactPath) path = exactPath;
            if (current is Win32Exception || ((uint)current.HResult & 0xffff0000u) == 0x80070000u)
                native = current;
        }
        var code = native is Win32Exception win32 ? win32.NativeErrorCode : native?.HResult & 0xffff;
        // IOException's generic 0x80131620 is NOT a Win32 error whose code is its low word.
        var hr = native is Win32Exception && code is > 0
            ? 0x80070000u | ((uint)code.Value & 0xffffu)
            : (uint)(native?.HResult ?? error.HResult);
        return $"I/O failure path='{OneLine(path)}', Win32={code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}, HRESULT=0x{hr:X8}: {OneLine(error.Message)}";
    }

    internal static void LogOnce(string path, Exception error, Action<string>? sink = null)
    {
        var anchor = error.GetBaseException();
        lock (anchor)
        {
            // Keep the deepest failure's actual path, not a caller's pair-relative operation path.
            anchor.Data[PathKey] ??= path;
            if (sink is null && !V2.RunOperations.Active) return;
            if (anchor.Data[LoggedKey] is true) return;
            anchor.Data[LoggedKey] = true;
        }
        var detail = Describe(path, error);
        if (sink is not null) sink(detail);
        else V2.RunOperations.Failure(detail);
    }

    private static string OneLine(string value) => value.Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}
