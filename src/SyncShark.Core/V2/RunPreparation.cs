namespace Massdex.SyncShark.V2;

internal abstract class RunPreparation
{
    internal static readonly AsyncLocal<RunPreparation?> Slot = new();

    internal static void Report(string? message) => Slot.Value?.SetPreparation(message);

    internal abstract void SetPreparation(string? message);
}
