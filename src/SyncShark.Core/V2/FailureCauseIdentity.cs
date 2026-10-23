using System.Globalization;

namespace Massdex.SyncShark.V2;

/// <summary>Stable exception cause identity. Display messages and retry counters never affect suppression.</summary>
internal static class FailureCauseIdentity
{
    internal static string Of(Exception error) => "cause" + ShortId.Hex16(
        error.GetType().FullName ?? error.GetType().Name,
        error.HResult.ToString(CultureInfo.InvariantCulture),
        error switch
        {
            StoragePairException storage => storage.Cause,
            System.Net.Http.HttpRequestException http => http.StatusCode?.ToString() ?? string.Empty,
            System.ComponentModel.Win32Exception native => native.NativeErrorCode.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty
        });
}
