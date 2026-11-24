namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Counts named provider requests, never elapsed time.</summary>
public sealed class RequestCounters
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> counts = new();
    public void Count(string operation) => counts.AddOrUpdate(operation, 1, (_, n) => n + 1);
    public long this[string operation] => counts.GetValueOrDefault(operation);
}
