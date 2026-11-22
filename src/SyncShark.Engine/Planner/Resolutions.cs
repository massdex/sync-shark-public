namespace Massdex.SyncShark.Engine.Planner;

/// <summary>The actions of a notice resolution</summary>
public static class Resolutions
{
    /// <summary>Choices operation on the supplied facts.</summary>
    public static IReadOnlyList<ResolutionChoice> Choices(Notice notice) => throw new NotImplementedException();
    /// <summary>For operation on the supplied facts.</summary>
    public static SyncAction For(Notice notice, ResolutionChoice choice, Row row, string? name) => throw new NotImplementedException();
}
