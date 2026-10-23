namespace Massdex.SyncShark.V2;

internal static class CreateExclusions
{
    internal static IReadOnlyList<string> Validate(IReadOnlyList<string> paths)
    {
        try { return ExclusionPaths.Validate(paths); }
        catch (ExclusionPathValidationException error) { throw new SyncCliUsageException(error.Message); }
    }

    internal static string ValidateSelection(string path)
    {
        try { return ExclusionPaths.ValidateSelection(path); }
        catch (ExclusionPathValidationException error) { throw new SyncCliUsageException(error.Message); }
    }
}
