using System.Collections;

namespace Massdex.SyncShark.V2;

// Planner asks by item key; a million retained handoffs never become a million-entry dictionary.
internal sealed class RetryMatches(IStateStore store, string pairId) : IReadOnlyDictionary<string, PrepareMatch>
{
    public PrepareMatch this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
    public bool ContainsKey(string key) => TryGetValue(key, out _);
    public bool TryGetValue(string key, out PrepareMatch value)
    {
        var retry = store.SyncRetryAt(pairId, key);
        value = retry?.Kind == InterruptedDelete.RestoreKind ? null! : retry?.Match!;
        return value is not null;
    }
    public IEnumerable<string> Keys => Entries().Select(item => item.Key);
    public IEnumerable<PrepareMatch> Values => Entries().Select(item => item.Value);
    public int Count => Entries().Count();
    public IEnumerator<KeyValuePair<string, PrepareMatch>> GetEnumerator() => Entries().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    private IEnumerable<KeyValuePair<string, PrepareMatch>> Entries() => store.ReadSyncRetries(pairId)
        .Where(item => item.Match is not null && item.Kind != InterruptedDelete.RestoreKind).Select(item => new KeyValuePair<string, PrepareMatch>(item.Path, item.Match!));
}
