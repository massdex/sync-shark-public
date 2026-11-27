using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>A rename that is durable before it returns, refusing an existing target.</summary>
public static class WriteThroughMove
{
    private const int MoveFileWriteThrough = 0x8; // winbase.h MOVEFILE_WRITE_THROUGH
    private const uint HResultFromWin32Base = 0x80070000; // winerror.h HRESULT_FROM_WIN32: severity error, FACILITY_WIN32

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileExW(string existing, string target, int flags);

    // Write-through makes the rename itself durable before it returns; no replace flag, so an existing target refuses.
    public static void Move(string stage, string target)
    {
        if (MoveFileExW(stage, target, MoveFileWriteThrough)) return;
        var code = Marshal.GetLastPInvokeError();
        var hresult = unchecked((int)(HResultFromWin32Base | (uint)code));
        throw code switch
        {
            80 or 183 => new InvalidDataException(Text.Get(TextKey.RecoveryStageLocationBlocked, (TextArg.Place, target))),
            32 or 33 => new IOException(Text.Get(TextKey.StoreMoveInUse, (TextArg.Path, stage)), new IOException(null, hresult)),
            39 or 112 => new IOException(Text.Get(TextKey.StoreMoveNoSpace, (TextArg.Path, Path.GetDirectoryName(target)!)), null),
            _ => new IOException($"{new Win32Exception(code).Message} {stage}", hresult),
        };
    }
}
