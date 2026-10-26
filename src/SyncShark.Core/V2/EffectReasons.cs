namespace Massdex.SyncShark.V2;

internal static class EffectReasons
{
    internal const string NotApplied = "Not applied; re-planned this run";
    internal const string RestoreAbandoned = "Restore attempt abandoned; any created item stays in place and Recovery remains available.";
    internal const string Superseded = "Superseded by a later change";
}
