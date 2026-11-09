namespace Massdex.SyncShark.Engine.Storage;

/// <summary>The identity membership statement for a selection's shape.</summary>
internal sealed partial class ChangeTable
{
    // B-135: a top-level group seeks work_identity; a residual's parent and obligation name one row exactly.
    private static string IdentityShape(WorkRow anchor, string topLevel, string residual) =>
        anchor.Parent is null ? topLevel : residual;

    private static string ReadIdentity(WorkRow anchor) =>
        IdentityShape(anchor, StateSchema.ReadIdentitySelection, StateSchema.ReadResidualIdentitySelection);

    private static string CompleteIdentity(WorkRow anchor) =>
        IdentityShape(anchor, StateSchema.CompleteIdentitySelection, StateSchema.CompleteResidualIdentitySelection);

    private static string SaveBeforeIdentity(WorkRow anchor) =>
        IdentityShape(anchor, StateSchema.SaveBeforeIdentity, StateSchema.SaveBeforeResidualIdentity);
}
