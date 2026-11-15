namespace Massdex.SyncShark.Engine;

/// <summary>One Repair invocation's ambient carrier (#36): its presence, and the first disk-full failure a
/// result-only or uncertain-effect route recorded instead of throwing. It holds no other fact.</summary>
public sealed class RepairScope : IDisposable
{
    private static readonly AsyncLocal<RepairScope?> Ambient = new();
    private readonly RepairScope? _previous = Ambient.Value;
    private Exception? _resource;

    private RepairScope() => Ambient.Value = this;

    /// <summary>Enters a scope around one awaited Repair; disposal restores the previous scope.</summary>
    public static RepairScope Enter() => new();

    /// <summary>Records the error's disk-full cause on the current Repair; outside a Repair it does nothing.</summary>
    public static void Record(Exception error)
    {
        if (Ambient.Value is { } scope && ResourceFailures.Find(error) is { } resource)
            Interlocked.CompareExchange(ref scope._resource, resource, null);
    }

    /// <summary>The current Repair's first recorded disk-full failure, or null.</summary>
    public static Exception? Recorded => Ambient.Value is { } scope ? Volatile.Read(ref scope._resource) : null;

    public void Dispose()
    {
        if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = _previous;
    }
}
