namespace Massdex.SyncShark.Engine.Run;

/// <summary>One run's log file</summary>
public sealed class RunLog : IDisposable
{
    /// <summary>Write operation on the supplied facts.</summary>
    public void Write(RunEvent value) => throw new NotImplementedException();
    /// <summary>Read operation on the supplied facts.</summary>
    public static ILogFiles.ReadStatus Read(ILogFiles files, string path, Action<string,bool> record, int bufferBytes, CancellationToken ct, out bool trailing) => throw new NotImplementedException();

    public RunLog(ILogFiles files, string path, EngineSettings settings) => throw new NotImplementedException();


    /// <summary>Dispose for the selected RunLog operation.</summary>
    public void Dispose() => throw new NotImplementedException();
    /// <summary>EscapeField for the selected RunLog operation.</summary>
    public static string EscapeField(string value) => throw new NotImplementedException();
    /// <summary>Path of this run’s log.</summary>
    public string Path => throw new NotImplementedException();
}
