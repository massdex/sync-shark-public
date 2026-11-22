namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Connections operations for the dropbox module.</summary>
public sealed class Connections
{
    /// <summary>Read operation on the supplied facts.</summary>
    public IReadOnlyList<string> Read(string productRoot) => throw new NotImplementedException();
    /// <summary>Verify the source and target lists and required connections before the store switch. On refusal remove only this attempt’s files; on success return cleanup for a failed switch, or null if nothing was created.</summary>
    public Action? Verify(string sourceRoot, string productRoot, IReadOnlyList<string> requiredConnectionIds) => throw new NotImplementedException();
    /// <summary>Save operation on the supplied facts.</summary>
    public void Save(string productRoot, IReadOnlyList<string> connections) => throw new NotImplementedException();
}
