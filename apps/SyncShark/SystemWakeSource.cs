using System.Net.NetworkInformation;
using Massdex.SyncShark.V2;
using Microsoft.Win32;

namespace Massdex.SyncShark;

/// <summary>The platform's wake-up events (power resume, network available) forwarded to continuous sync.</summary>
internal sealed class SystemWakeSource : ISystemWakeSource
{
    public event Action? Woke;

    internal SystemWakeSource()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Woke?.Invoke();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) Woke?.Invoke();
    }
}
