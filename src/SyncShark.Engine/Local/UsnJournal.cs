namespace Massdex.SyncShark.Engine.Local;

/// <summary>UsnJournal operations for the local module.</summary>
public sealed class UsnJournal
{
    /// <summary>Current operation on the supplied facts.</summary>
    public JournalPosition Current(string root) => throw new NotImplementedException();
    /// <summary>Read operation on the supplied facts.</summary>
    public ChangePage Read(string root, JournalPosition position, int bufferBytes) => throw new NotImplementedException();
    /// <summary>Read only the publication record attributable to the selected held file; null when not proved. Never substitute the volume tail.</summary>
    public JournalPosition? Published(Microsoft.Win32.SafeHandles.SafeFileHandle file) => throw new NotImplementedException();
}
