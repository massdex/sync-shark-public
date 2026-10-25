namespace Massdex.SyncShark.V2;

/// <summary>Observations of production decisions, scoped to the caller's async flow.</summary>
internal abstract class PlanProvenance : IDisposable
{
    internal static readonly AsyncLocal<PlanProvenance?> Current = new();
    internal abstract void BeginRun(int run);
    internal abstract void BeginScope(string scope);
    internal abstract void Visibility(Side side, ItemState item, bool hidden, string reason);
    internal abstract void Claim(string kind, ItemState item);
    internal abstract void Rejected(SyncAction action, string reason);
    internal abstract void Decided(SyncAction action, bool baseline);
    internal abstract void Final(Plan plan, IStateStore store);
    internal abstract void Summary(Action<string> print);
    public abstract void Dispose();
}
