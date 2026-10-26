using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal abstract class RunTrace
{
    internal static readonly AsyncLocal<RunTrace?> Slot = new();

    internal static void Emit(string line)
    {
        if (Slot.Value is { } log) log.WriteTrace(line);
        else RunOutput.Write(Console.Error.WriteLine, "Sync Shark trace: " + line);
    }

    internal static void EffectResult(SyncAction action, ActionResult result)
    {
        if (result.Outcome is not (Outcome.Failed or Outcome.Uncertain)) return;
        Emit($"effect outcome={result.Outcome} kind={action.Kind} side={action.Target} path={JsonSerializer.Serialize(action.Path)} category={result.FailureKind} reason={JsonSerializer.Serialize(result.Reason)}");
    }

    internal abstract void WriteTrace(string line);
}
