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
        if (result.Outcome is not (Outcome.Failed or Outcome.Uncertain or Outcome.Refused)) return;
        Emit(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            MessageCatalog.Default.Template("effect-result-trace"), result.Outcome, action.Kind, action.Target,
            JsonSerializer.Serialize(action.Path), result.FailureKind, JsonSerializer.Serialize(result.Reason)));
    }

    internal static void EffectCommitFailed(SyncAction action, ActionResult result, Exception error) =>
        Emit(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            MessageCatalog.Default.Template("effect-uncommitted-trace"), result.Outcome, action.Kind, action.Target,
            JsonSerializer.Serialize(action.Path), result.FailureKind, JsonSerializer.Serialize(result.Reason),
            JsonSerializer.Serialize(error.GetType().Name + ": " + error.Message)));

    internal abstract void WriteTrace(string line);
}
