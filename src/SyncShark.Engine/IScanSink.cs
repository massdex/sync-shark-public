namespace Massdex.SyncShark.Engine;

/// <summary>Receives one side's listing (section 3.3). AddAsync waits while the sink's bounded queue is full.
/// CompleteAsync and AbortAsync both end the writer loop.</summary>
public interface IScanSink
{
    Task AddAsync(Listed item, CancellationToken ct);
    Task CompleteAsync();
    Task AbortAsync();
}
