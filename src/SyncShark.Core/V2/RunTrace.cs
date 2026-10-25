namespace Massdex.SyncShark.V2;

internal abstract class RunTrace
{
    internal static readonly AsyncLocal<RunTrace?> Slot = new();

    internal static void Emit(string line)
    {
        if (Slot.Value is { } log) log.WriteTrace(line);
        else RunOutput.Write(Console.Error.WriteLine, "Sync Shark trace: " + line);
    }

    internal abstract void WriteTrace(string line);
}
