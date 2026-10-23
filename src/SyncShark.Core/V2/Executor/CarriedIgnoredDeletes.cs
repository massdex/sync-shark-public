namespace Massdex.SyncShark.V2;

/// <summary>Ignored content carried by a parent delete; counts are disclosure, never standalone actions.</summary>
public sealed record CarriedIgnoredContent(int Files, int Folders, long Bytes)
{
    /// <summary>At most twenty carried paths for notices; not identity-based review constraints.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];
    public bool TrivialFileNamesOnly { get; init; }
}

/// <summary>Per-folder disclosure and review ceilings; harmless ignored identity churn is not a veto.</summary>
internal static class CarriedIgnoredDeletes
{
    internal static CarriedIgnoredContent Snapshot(IStateStore store, PairDef pair, SyncAction action, ProviderRefusedPaths? refused = null)
    {
        if (action.Kind != ActionKind.Delete || action.ExpectedTarget?.Kind != ItemKind.Folder)
            return new(0, 0, 0);
        var rules = PairInclude.IgnoreRules(pair);
        var rootId = action.Target == Side.A ? pair.A.RootId : pair.B.RootId;
        refused ??= ProviderRefusedPaths.Load(store, pair, rootId);
        int files = 0, folders = 0;
        long bytes = 0;
        var examples = new List<string>();
        var trivialOnly = true;
        var fileNames = rules.Where(rule => rule.Kind == SyncIgnoreKind.FileName).ToArray();
        var foldersRules = rules.Where(rule => rule.Kind == SyncIgnoreKind.FolderName).ToArray();
        foreach (var item in FolderDelete.LiveDescendants(store, rootId, action.Path))
        {
            var providerRefused = refused.Contains(item);
            if (!providerRefused &&
                !SyncIgnorePolicy.IsIgnored(item.Path, item.Kind == ItemKind.Folder, rules)) continue;
            trivialOnly &= !providerRefused && item.Kind == ItemKind.File && item.Size <= 64 * 1024 &&
                SyncIgnorePolicy.IsIgnored(item.Path, false, fileNames) &&
                !SyncIgnorePolicy.IsIgnored(item.Path, false, foldersRules);
            if (examples.Count < 20) examples.Add(item.Path);
            if (item.Kind == ItemKind.Folder) folders++;
            else { files++; bytes += item.Size; }
        }
        return new(files, folders, bytes) { Examples = examples, TrivialFileNamesOnly = trivialOnly };
    }

    internal static bool WithinBudget(CarriedIgnoredContent current, CarriedIgnoredContent? budget) =>
        current.Files <= (budget?.Files ?? 0) && current.Folders <= (budget?.Folders ?? 0) &&
        current.Bytes <= (budget?.Bytes ?? 0);

    internal static string Describe(CarriedIgnoredContent content) =>
        $"Carried ignored content: {content.Files} files, {content.Folders} folders, {content.Bytes} bytes. " +
        "These items are removed with the parent folder, not synced separately." +
        (content.Examples.Count == 0 ? "" : " Paths: " + string.Join(", ", content.Examples) +
            (content.Files + content.Folders > content.Examples.Count
                ? $" (showing {content.Examples.Count} of {content.Files + content.Folders})" : "") + ".");

    internal static Notice Notice(string pairId, SyncAction action, CarriedIgnoredContent content) =>
        new(pairId, action.Rule, NoticeKind.Info, action.Path,
            "Folder deletion: " + action.Path + ". " + Describe(content), "folder-delete-carried-ignored");

    internal static Notice GrowthNotice(string pairId, SyncAction action, CarriedIgnoredContent content) =>
        new(pairId, action.Rule, NoticeKind.Info, action.Path,
            "Folder deletion skipped: carried ignored content exceeds the reviewed counts or bytes. " +
            Describe(content) + " Run prepare again to review this folder.", "folder-delete-ignored-growth");
}
