namespace Massdex.SyncShark.Engine.Storage;

/// <summary>The ordered boundary for completing wholly excluded work.</summary>
public sealed partial class StateStore
{
    /// <inheritdoc />
    public bool CompleteOutOfScope(WorkSelection selection, PathScope scope) =>
        false;
}
