namespace Massdex.SyncShark.V2;

public interface IRecoveryAvailability
{
    string? RecoveryUnavailable { get; }
}

internal static class RecoverySafety
{
    internal static bool RequiresRecovery(SyncAction action) => action.RecoveryFirst ||
        action.Kind is ActionKind.Delete or ActionKind.Update or ActionKind.Move or ActionKind.KeepBoth ||
        action.ExpectedTarget is not null && action.Kind is not (ActionKind.Adopt or ActionKind.Forget or ActionKind.Hold);

    internal static string? Unavailable(IEndpointWriter a, IEndpointWriter b) =>
        (a as IRecoveryAvailability)?.RecoveryUnavailable ?? (b as IRecoveryAvailability)?.RecoveryUnavailable;
}
