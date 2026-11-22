namespace Massdex.SyncShark.Engine.Planner;

/// <summary>The actions of a notice resolution</summary>
public static class Resolutions
{
    /// <summary>List the resolution choices permitted for this notice.</summary>
    public static IReadOnlyList<ResolutionChoice> Choices(Notice notice) => throw new NotImplementedException();
    /// <summary>Select the action for the reviewed notice, choice and observed row.</summary>
    public static SyncAction For(Notice notice, ResolutionChoice choice, Row row, string? name) => throw new NotImplementedException();

    /// <summary>For for the selected Resolutions operation.</summary>
    public static IReadOnlyList<SyncAction> For(Notice notice, ResolutionChoice choice) => throw new NotImplementedException();
    /// <summary>For for the selected Resolutions operation.</summary>
    public static IReadOnlyList<SyncAction> For(Notice notice, ResolutionChoice choice, string? name, Row localRow, Row dropboxRow, bool incomingBound = false) => throw new NotImplementedException();
}
