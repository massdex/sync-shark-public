namespace Massdex.SyncShark.V2;

/// <summary>A revalidating preview and the optional ownership token for its opened endpoint classification.</summary>
internal sealed record ApplyPreview(Plan Plan, RebuildableResolution Rebuildables, EndpointContinuation? Continuation)
{
    public void Deconstruct(out Plan plan, out RebuildableResolution rebuildables)
    {
        plan = Plan;
        rebuildables = Rebuildables;
    }
}
