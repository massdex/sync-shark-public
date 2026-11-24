namespace Massdex.SyncShark.Engine.Run;

/// <summary>One pair's runner, queue, adapters and lifetime state inside the host.</summary>
internal sealed class HostMember(PairConfig pair)
{
    internal PairConfig Pair = pair;
    internal readonly PairRunner Runner = new();
    internal WorkQueue Queue = null!;
    internal PathScope Paths = null!;
    internal ISide Local = null!, Dropbox = null!;
    internal RunLog? Log;
    internal DriveType Type;
    internal VolumeTrim? Trim;
    internal JournalPosition? JournalWake;
    internal CancellationTokenSource? RepairFinished;
    internal int Closing;
    internal bool Admitted;
    internal CancellationTokenSource? Stop;
    internal volatile Task<bool> Boot=Task.FromResult(false);
    internal Task Live = Task.CompletedTask, Manual = Task.CompletedTask;
    internal CancellationTokenSource? ManualStop, Opening;
    internal Task Setup=Task.CompletedTask, DebtWake=Task.CompletedTask;
    internal bool DebtChanged;
    internal readonly AsyncLocal<Action<RunEvent>?> OperationLog=new();
    internal void Event(RunEvent value) { if(OperationLog.Value is {} sink) sink(value); else Log?.Write(value); }
}
