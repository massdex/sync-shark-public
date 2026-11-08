namespace Massdex.SyncShark;

/// <summary>A result of an engine command: human text and its exit code (engine design 9.3).</summary>
public interface IEngineCliData
{
    string RenderHuman();
    int ExitCode { get; }
}
