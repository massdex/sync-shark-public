using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Massdex.SyncShark.V2;
using Microsoft.Win32;

namespace Massdex.SyncShark;

/// <summary>The platform's wake-up events (power resume, network available, drive arrival) forwarded to continuous sync.</summary>
/// <remarks>Registration completes in the constructor, so construct this before inspecting current drives.</remarks>
internal sealed class SystemWakeSource : ISystemWakeSource
{
    private const uint WmDeviceChange = 0x0219;
    private const uint WmQuit = 0x0012;
    private const uint DbtDeviceArrival = 0x8000;
    private const uint DbtDevTypDeviceInterface = 0x00000005;
    private const uint DeviceNotifyWindowHandle = 0;
    // GUID_DEVINTERFACE_VOLUME (winioctl.h): volume arrivals, so registering needs no drive list first.
    private static readonly Guid VolumeDeviceInterface = new("53f5630d-b6bf-11d0-94f2-00a0c91efb8b");

    private readonly object _callbackGate = new();
    private readonly ManualResetEventSlim _deviceReady = new();
    private readonly string _deviceClassName = "SyncSharkWakeWindow-" + Guid.NewGuid().ToString("N");
    private readonly Thread _deviceThread;
    private readonly WindowProcedure _windowProcedure;
    private nint _deviceWindow;
    private nint _deviceNotification;
    private uint _deviceThreadId;
    private Exception? _deviceFailure;
    private bool _disposed;

    public event Action? Woke;

    internal SystemWakeSource()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _windowProcedure = OnDeviceWindowMessage;
        _deviceThread = new Thread(RunDeviceMessageLoop) { IsBackground = true, Name = "Sync Shark wake" };
        try
        {
            _deviceThread.Start();
            _deviceReady.Wait();
            if (_deviceFailure is { } failure) ExceptionDispatchInfo.Throw(failure);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_callbackGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        if (_deviceThreadId != 0) PostThreadMessage(_deviceThreadId, WmQuit, 0, 0);
        if (_deviceThread.IsAlive && _deviceThread.ManagedThreadId != Environment.CurrentManagedThreadId)
            _deviceThread.Join();
        _deviceReady.Dispose();
    }

    // Drives the real device-change delivery path for the UI fixture, without a physical drive.
    internal void PostDeviceArrivalForTest() => PostMessage(_deviceWindow, WmDeviceChange, (nint)DbtDeviceArrival, 0);

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) RaiseWoke();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) RaiseWoke();
    }

    private void RaiseWoke()
    {
        lock (_callbackGate)
        {
            if (!_disposed) Woke?.Invoke();
        }
    }

    private void RunDeviceMessageLoop()
    {
        var instance = GetModuleHandle(null);
        var classRegistered = false;
        try
        {
            try
            {
                _deviceThreadId = GetCurrentThreadId();
                var windowClass = new WindowClass
                {
                    Size = (uint)Marshal.SizeOf<WindowClass>(),
                    Procedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                    Instance = instance,
                    ClassName = _deviceClassName,
                };
                if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                classRegistered = true;
                _deviceWindow = CreateWindowEx(0, _deviceClassName, string.Empty, 0, 0, 0, 0, 0,
                    0, 0, instance, 0);
                if (_deviceWindow == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                var filter = new DevBroadcastDeviceInterface
                {
                    Size = Marshal.SizeOf<DevBroadcastDeviceInterface>(),
                    DeviceType = DbtDevTypDeviceInterface,
                    ClassGuid = VolumeDeviceInterface,
                };
                _deviceNotification = RegisterDeviceNotification(_deviceWindow, ref filter, DeviceNotifyWindowHandle);
                if (_deviceNotification == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch (Exception error)
            {
                _deviceFailure = error;
                _deviceReady.Set();
                return;
            }
            _deviceReady.Set();
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            if (_deviceNotification != 0) UnregisterDeviceNotification(_deviceNotification);
            if (_deviceWindow != 0) DestroyWindow(_deviceWindow);
            if (classRegistered) UnregisterClass(_deviceClassName, instance);
            _deviceThreadId = 0;
        }
    }

    private nint OnDeviceWindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == WmDeviceChange && (uint)wParam == DbtDeviceArrival)
        {
            RaiseWoke();
            return 1;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Value;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string? ClassName;
        public nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevBroadcastDeviceInterface
    {
        public int Size;
        public uint DeviceType;
        public uint Reserved;
        public Guid ClassGuid;
        // Native TCHAR[1] tail; declared so Size matches the documented structure, never read.
        public ushort Name;
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Message message);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint RegisterDeviceNotification(nint receiver, ref DevBroadcastDeviceInterface filter, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(nint notification);
}
