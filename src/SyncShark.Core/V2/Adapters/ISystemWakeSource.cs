namespace Massdex.SyncShark.V2;

/// <summary>
/// The machine's "wake up" notifications as an injectable source: the app binds the platform's power-resume and
/// network-availability events to <see cref="Woke"/>, and continuous sync kicks every pair when it fires.
/// </summary>
public interface ISystemWakeSource : IDisposable
{
    /// <summary>Raised when the computer resumed or the network became available again.</summary>
    event Action? Woke;
}
