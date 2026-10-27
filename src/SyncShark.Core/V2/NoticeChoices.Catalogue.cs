namespace Massdex.SyncShark.V2;

public static partial class NoticeChoices
{
    /// <summary>The one rendered catalogue for both CLI and UI.</summary>
    public static IReadOnlyList<(string Choice, string Label, bool RequestsName)> Catalogue(Notice notice)
    {
        if (notice.ReasonKey == "reconciliation-moving-too-large")
            return [(RepairHelp, MessageCatalog.Default.Template("reconciliation-moving-future"), false)];
        var options = CatalogueCore(notice).Select(option => IsIgnoreChoice(option.Choice)
            ? (option.Choice, Label: notice.Quarantine is not null
                ? "Ignore this cause (keep protection)" : "Ignore this cause (keep retrying safely)", option.RequestsName) : option).ToList();
        if (CanStopSyncing(notice) && options.All(option => option.Choice != Exclude))
            options.Add((Exclude, "Stop syncing this file", false));
        return options;
    }
}
