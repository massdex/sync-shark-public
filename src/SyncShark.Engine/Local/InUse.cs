using System.Runtime.InteropServices;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Best-effort Restart Manager names for a read that already failed with a sharing violation.</summary>
public static class InUse
{
    public static IReadOnlyList<string> Processes(string path)
    {
        try
        {
            if (RmStartSession(out var session, 0, new System.Text.StringBuilder(33)) != 0) return []; // restartmanager.h CCH_RM_SESSION_KEY + terminator
            try
            {
                if (RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, IntPtr.Zero) != 0) return [];
                uint count = 0;
                if (RmGetList(session, out var needed, ref count, null, out _) != 234 || needed == 0) return []; // winerror.h ERROR_MORE_DATA
                var infos = new ProcessInfo[needed];
                count = needed;
                return RmGetList(session, out _, ref count, infos, out _) == 0
                    ? infos.Take(checked((int)count)).Select(p => p.Name).Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];
            }
            finally { RmEndSession(session); }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or
            ExternalException or IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    // RM_PROCESS_INFO contains a DWORD PID and a FILETIME, followed by fixed UTF-16 arrays.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public uint Pid;
        public System.Runtime.InteropServices.ComTypes.FILETIME Start;
        // RM_PROCESS_INFO: CCH_RM_MAX_APP_NAME+1 = 256 and CCH_RM_MAX_SVC_NAME+1 = 64 (restartmanager.h).
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public uint Kind, Status, Session;
        public int Restartable;
    }

    [DllImport("rstrtmgr", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int RmStartSession(out uint session, uint flags, System.Text.StringBuilder key);
    [DllImport("rstrtmgr", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int RmRegisterResources(uint session, uint files, string[] paths, uint apps, IntPtr app, uint services, IntPtr service);
    [DllImport("rstrtmgr")] private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? infos, out uint reboot);
    [DllImport("rstrtmgr")] private static extern int RmEndSession(uint session);
}
