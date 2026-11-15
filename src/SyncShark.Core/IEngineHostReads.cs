namespace Massdex.SyncShark;

/// <summary>The Engine host's read surface. The host already owns the product authority, so its reads acquire none.</summary>
public interface IEngineHostReads
{
    Task<object> ReadHostAsync(EngineHostReadKind kind, SyncCliInvocation invocation,
        CancellationToken cancellationToken);
}
