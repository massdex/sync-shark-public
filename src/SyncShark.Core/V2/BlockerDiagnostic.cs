using System.ComponentModel;

namespace Massdex.SyncShark.V2;

/// <summary>Repair guidance from structured errors, never from localized exception messages.
/// Antivirus classification remains owned by QuarantineFailure on the quarantine branch.</summary>
internal static class BlockerDiagnostic
{
    internal static string Describe(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is ProviderWriteBlockedException provider)
                return $"{provider.Message} Repair: {provider.Repair}";
            var code = current is Win32Exception native ? native.NativeErrorCode :
                ((uint)current.HResult & 0xffff0000u) == 0x80070000u ? current.HResult & 0xffff : 0;
            var repair = code switch
            {
                5 => "Check this item's permissions and allow access, then Retry. No permissions are changed automatically.",
                32 or 33 => "Close the application using this item, save your changes, then Retry.",
                2 or 3 => "Locate the moved folder or reconnect its drive, then Retry. Missing access is not deletion evidence.",
                21 or 53 or 67 or 1167 => "Reconnect the drive or network location, then Retry.",
                112 => "Free space on this volume or choose an available Recovery location, then Retry.",
                123 => "Rename the item to a name supported by the destination, then Retry.",
                206 => "Shorten the name or move the item to a shorter path, then Retry.",
                _ => null
            };
            if (repair is not null)
                return $"{error.GetType().Name}: {error.Message} (Win32={code}). Repair: {repair}";
        }
        return error.GetType().Name + ": " + error.Message;
    }
}
