namespace Massdex.SyncShark.V2;

// Shared only across the bounded pages of one run. Standalone executor calls get
// their own scope. No provider outcome or handled anchor survives into a later run.
internal sealed class ReconcileRunScope : IDisposable
{
    private static readonly AsyncLocal<ReconcileRunScope?> Ambient = new();
    private readonly ReconcileRunScope? _previous = Ambient.Value;
    internal static ReconcileRunScope? Current => Ambient.Value;
    // Only tiny in_progress responses span pages; completed job entry arrays stay page-local.
    internal System.Collections.Concurrent.ConcurrentDictionary<(object, string, string),
        System.Text.Json.JsonElement> PendingJobs { get; } = new();
    internal HashSet<string> Anchors { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> MissingAnchors { get; } = new(StringComparer.Ordinal);
    internal ReconcileRunScope() => Ambient.Value = this;
    public void Dispose() => Ambient.Value = _previous;
}
