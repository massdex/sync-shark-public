using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>Page-sized durable replay for the existing parallel listing, never inventory authority.</summary>
internal sealed class ListingCheckpoint
{
    private readonly SqliteStateStore _store;
    private readonly RootDef _root;
    private readonly string _pair;
    internal ListingCheckpoint(SqliteStateStore store, RootDef root, string pair, string mode)
    {
        _store = store;
        _root = root;
        _pair = pair;
        store.BeginListing(root, mode);
    }

    // N=1: checkpoint each received page (including empty pages). Replay reads only one SQLite payload
    // at a time into the existing parser; no second million-item collection is retained in memory.
    internal async Task<JsonDocument> PageAsync(string route, object body, Func<Task<JsonDocument>> fetch)
    {
        CheckPause();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(route + "\n" + JsonSerializer.Serialize(body))));
        if (_store.ListingPage(_root.RootId, key) is { } saved) return JsonDocument.Parse(saved);
        var page = await fetch();
        try
        {
            var cursor = page.RootElement.GetProperty("cursor").GetString();
            if (string.IsNullOrEmpty(cursor)) throw new InvalidDataException("Dropbox cursor is absent.");
            _store.SaveListingPage(_root.RootId, key, cursor, page.RootElement.GetRawText());
            return page;
        }
        catch { page.Dispose(); throw; }
    }

    internal async Task<string> BasisAsync(Func<Task<string>> fetch)
    {
        using var page = await PageAsync("basis", new { }, async () =>
            JsonDocument.Parse(JsonSerializer.Serialize(new { cursor = await fetch() })));
        return page.RootElement.GetProperty("cursor").GetString()!;
    }

    internal void CheckPause()
    {
        PairPause.Check();
        if (_store.CachedPairPaused(_pair)) throw new PairPausedException(_pair);
    }
    internal void Complete() => _store.ForgetListing(_root.RootId);
}
