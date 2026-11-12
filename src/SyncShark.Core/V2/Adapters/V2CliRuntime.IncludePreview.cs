namespace Massdex.SyncShark.V2;

public sealed partial class V2CliRuntime
{
    // Inventory-only seam: never obtain an endpoint writer or call a run/prepare command for a preview.
    internal Func<PairDef, string, CancellationToken, Task<SyncCliIncludePreviewData>>? IncludePreviewReader { get; set; }

    public async Task<SyncCliIncludePreviewData> PreviewIncludeAsync(string pairId, string path, CancellationToken ct)
    {
        var relative = CreateExclusions.ValidateSelection(path);
        var pair = RequirePair(pairId);
        if (!_store.FirstSyncDone(pairId))
            throw new SyncCliUsageException("This pair has not completed first sync. Use first-sync prepare to review it; nothing was changed.");
        var parent = pair.Excluded.FirstOrDefault(item => !PairPath.IsSelfOrUnder(item, relative) &&
            PairPath.IsSelfOrUnder(relative, item));
        if (parent is not null)
            throw new SyncCliUsageException($"'{relative}' is still excluded by the parent folder '{parent}'. " +
                "Preview or include that folder instead; nothing was changed.");
        if (!pair.Excluded.Any(item => PairPath.IsSelfOrUnder(item, relative)))
            throw new SyncCliUsageException($"'{relative}' is already included. Run the pair to sync its current selection; nothing was changed.");
        var reader = IncludePreviewReader;
        if (reader is null && _composition is not null) reader = _composition.Endpoints.PreviewIncludeAsync;
        if (reader is null)
            throw new SyncCliUsageException("A read-only folder inventory is unavailable here. Include the folder without preview to sync it on the next run; nothing was changed.");
        // No run gate: this reads its own bounded inventory, owns no shared cursor, and cannot publish effects.
        // A concurrent selection/run change does not turn this advisory snapshot into execution authority.
        var preview = await reader(pair, relative, ct).ConfigureAwait(false);
        return preview with { Warnings = preview.Warnings.Concat([
            "Including forgets this pair's old baselines and deletion evidence only inside the newly included folders. " +
            "Files deleted on one side while excluded are copied back, except items with an unresolved deletion hold are re-checked and need your decision first. Retained intents are verified without replay " +
            "and cannot restore the forgotten baselines; the newer differing file wins, with ties or unknown times held."]).ToArray() };
    }
}
