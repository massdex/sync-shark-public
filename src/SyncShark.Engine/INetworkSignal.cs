namespace Massdex.SyncShark.Engine;

/// <summary>Whether Windows reports a network (D33 network drop).</summary>
public interface INetworkSignal
{
    bool Available { get; }
    Task WaitAvailableAsync(CancellationToken ct);
}
