namespace Massdex.SyncShark.V2;

/// <summary>Shared GUI/headless event lifetime; platform wake sources remain at the app edge.</summary>
internal sealed class SystemWakeBinding : IDisposable
{
    private readonly ISystemWakeSource _source;
    private readonly Action _wake;
    internal SystemWakeBinding(ISystemWakeSource source, Action wake)
    {
        _source = source;
        _wake = wake;
        source.Woke += wake;
    }
    public void Dispose() => _source.Woke -= _wake;
}
