namespace Massdex.SyncShark.V2;

/// <summary>Identifies the pair and roots of one preview; it grants cache reuse, never deletion authority.</summary>
public sealed record EndpointContinuation(string PairId, string RootA, string RootB, Guid PreviewId);

/// <summary>A revalidating preview and the optional ownership token for its opened endpoint classification.</summary>
internal sealed record ApplyPreview(Plan Plan, RebuildableResolution Rebuildables, EndpointContinuation? Continuation)
{
    public void Deconstruct(out Plan plan, out RebuildableResolution rebuildables)
    {
        plan = Plan;
        rebuildables = Rebuildables;
    }
}
