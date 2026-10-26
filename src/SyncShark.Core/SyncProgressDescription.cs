namespace Massdex.SyncShark;

public sealed record SyncInventoryProgress(string Operation, string Target, int ObservedCount, string Detail);

// Shared by Home, first-sync review, tray, CLI and the retained operation log.
// A database/provider wait describes its actual process, never a fabricated current file.
public static class SyncProgressDescription
{
    public static string Describe(SyncComparisonProgress progress)
    {
        var detail = string.IsNullOrWhiteSpace(progress.Detail) ? progress.Phase : progress.Detail;
        if (!string.IsNullOrWhiteSpace(progress.RelativePath))
            detail += "\n" + progress.RelativePath;
        if (progress.TotalChecks > 0)
            detail += $"\n{progress.CompletedChecks:N0} of {progress.TotalChecks:N0} {progress.ProgressUnit}";
        else if (progress.CompletedChecks > 0)
            detail += $"\n{progress.CompletedChecks:N0} {progress.ProgressUnit} so far";
        return detail;
    }
}
