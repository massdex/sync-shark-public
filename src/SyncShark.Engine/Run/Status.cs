using System.Globalization;
using System.Text.Json;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Reads the last published Repair, not an in-flight scan.</summary>
public sealed class Status
{
    public Task<string> ReadAsync(IStore store, string? pairId, CancellationToken ct) => ReadAsync(store,pairId,ct,null);

    public async Task<string> ReadAsync(IStore store, string? pairId, CancellationToken ct, string? startupWarning)
    {
        var lines = new List<string>();
        if (startupWarning != null) lines.Add(startupWarning);
        if (pairId != null)
            await AddAsync(await store.PairAsync(pairId,ct) ?? throw new EngineRequestException(TextKey.PairUnknown,(TextArg.Pair,pairId)));
        else await foreach (var pair in store.PairsAsync(ct)) await AddAsync(pair);
        return lines.Count == 0 ? Text.Get(TextKey.NoPairsYet) : string.Join(Environment.NewLine,lines);

        async Task AddAsync(PairConfig pair)
        {
            var state = await store.RepairStateAsync(pair.PairId,ct);
            int count = 0;
            await foreach (var notice in store.NoticesAsync(pair.PairId,ct)) if (!notice.Dismissed && !notice.Ignored) count++;
            using var summary = state.LastRepairJson == null ? null : JsonDocument.Parse(state.LastRepairJson);
            string? finished = summary != null && summary.RootElement.TryGetProperty("Finished",out var value) ? value.GetString() : null;
            lines.Add(Text.Get(finished == null ? TextKey.ResultStatusLineNever : TextKey.ResultStatusLine,
                (TextArg.Pair,pair.PairId),(TextArg.Name,pair.Name),(TextArg.Finished,finished ?? string.Empty),
                (TextArg.Count,count.ToString(CultureInfo.InvariantCulture))));
            foreach(var e in pair.Exclusions) lines.Add(Text.Get(e.Keep?TextKey.ResultStatusExclusionKept:TextKey.ResultStatusExclusion,(TextArg.Side,e.Side.ToString()),(TextArg.Path,e.Path)));
        }
    }
}
