namespace Massdex.SyncShark.Engine.Planner;

/// <summary>ConflictName operations for the planner module.</summary>
public static class ConflictName
{
    /// <summary>Choose the label at apply time, not before locking. Format the conflict label for the observed side and computer. Ordinal 1 is the base label; later collisions use the ordinal suffix.</summary>
    public static string For(string path, Side side, DateTimeOffset modified, string computerName, EngineSettings settings, int ordinal) => throw new NotImplementedException();
}
