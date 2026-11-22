namespace Massdex.SyncShark.Engine.Run;

/// <summary>One run's log file</summary>
public sealed class RunLog
{
    /// <summary>Write operation on the supplied facts.</summary>
    public void Write(RunEvent value) => throw new NotImplementedException();
    /// <summary>Read operation on the supplied facts.</summary>
    public ILogFiles.ReadStatus Read(ILogFiles files, string path, Action<string,bool> record, int bufferBytes, CancellationToken ct, out bool trailing) => throw new NotImplementedException();
}
