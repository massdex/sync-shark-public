using System.Net.NetworkInformation;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>INetworkSignal over NetworkInterface.GetIsNetworkAvailable and NetworkChange (section 5.6).</summary>
public sealed class WindowsNetworkSignal : INetworkSignal
{
    /// <summary>Whether the operating system reports an available network.</summary>
    public bool Available => NetworkInterface.GetIsNetworkAvailable();

    /// <summary>Waits for network availability or cancellation.</summary>
    public async Task WaitAvailableAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Available) return;

        var available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args)
        {
            if (args.IsAvailable) available.TrySetResult();
        }

        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        try
        {
            // Recheck after subscribing so a transition between the checks is not missed.
            if (Available) available.TrySetResult();
            await available.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        }
    }
}
