using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>A move batch that completes at once: each entry moves as move_v2 does, and a refusal is that entry's
/// documented relocation failure.</summary>
internal sealed partial class DropboxWire
{
    /// <summary>A destination's scripted batch refusal: "conflict" (name taken) or a relocation tag such as
    /// too_many_files; an undocumented tag leaves the member's effect unknown. Nothing moves.</summary>
    public Dictionary<string, string> MoveBatchRefusals { get; } = new(StringComparer.Ordinal);

    /// <summary>Each move batch's entry count, in arrival order.</summary>
    public List<int> MoveBatchSizes { get; } = [];

    /// <summary>When set, every move batch waits for it before answering.</summary>
    public Task? HoldMoveBatch { get; set; }

    private async Task<HttpResponseMessage> MoveBatchAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        lock (MoveBatchSizes) MoveBatchSizes.Add(document.RootElement.GetProperty("entries").GetArrayLength());
        if (HoldMoveBatch is { } hold) await hold.WaitAsync(token);
        var results = new List<object>();
        foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            var destination = ToRelative(entry.GetProperty("to_path").GetString()!);
            lock (AttemptedWritePaths) AttemptedWritePaths.Add(destination!);
            var node = FindNode(entry.GetProperty("from_path").GetString()!);
            var refusal = destination is not null && MoveBatchRefusals.TryGetValue(destination, out var scripted)
                ? scripted == "conflict" ? Tagged("to", Tagged("conflict", Tagged("file"))) : Tagged(scripted)
                : destination is not null && NameWriteErrors.TryGetValue(destination, out var name)
                ? Tagged("to", Tagged(name))
                : node is null || destination is null || destination.Length == 0
                    ? Tagged("from_lookup", Tagged("not_found"))
                    : _items.ContainsKey(destination) ? Tagged("to", Tagged("conflict", Tagged("file"))) : null;
            if (refusal is not null)
            {
                results.Add(Tagged("failure", Tagged("relocation_error", refusal)));
                continue;
            }
            Rebase(node!.Relative, destination!);
            results.Add(Tagged("success", Metadata(_items[destination!])));
        }
        return Json(new Dictionary<string, object> { [".tag"] = "complete", ["entries"] = results });
    }

    private static Dictionary<string, object> Tagged(string tag, object? value = null)
    {
        var union = new Dictionary<string, object> { [".tag"] = tag };
        if (value is not null) union[tag] = value;
        return union;
    }
}
