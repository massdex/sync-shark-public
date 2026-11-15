namespace Massdex.SyncShark.Engine.Local;

/// <summary>The engine's fixed stage-file name: <c>.syncshark-</c>, a fresh id and <c>.stage</c>, beside the target; the fixed grammar keeps an ordinary user file from ever being collected as an orphan.</summary>
public static class StageNames
{
    private const string Prefix = ".syncshark-";
    private const string Suffix = ".stage";

    /// <summary>Returns the fixed reserved name for a new stage in the target file's own directory.</summary>
    public static string For(string fullTargetPath) =>
        Path.Combine(Path.GetDirectoryName(fullTargetPath)!, Prefix + ResourceNames.NewId() + Suffix);

    /// <summary>True only for the exact reserved grammar, so an ordinary GUID-named file or another suffix is not a stage.</summary>
    public static bool IsStage(string fileName) =>
        fileName.Length == Prefix.Length + ResourceNames.IdLength + Suffix.Length &&
        fileName.StartsWith(Prefix, StringComparison.Ordinal) &&
        fileName.EndsWith(Suffix, StringComparison.Ordinal) &&
        Guid.TryParseExact(fileName.AsSpan(Prefix.Length, ResourceNames.IdLength),
            ResourceNames.IdFormat, out _);
}
