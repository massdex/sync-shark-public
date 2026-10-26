namespace Massdex.SyncShark.V2;

internal static class VerificationSql
{
    internal static string NeededSql(string alias) => $"({alias}.hash IS NULL OR ({alias}.flags & {(int)ItemFlags.NeedsRehash})!=0)";
    internal static string EligibleSql(string alias) => $"{alias}.kind={(int)ItemKind.File} AND ({alias}.flags & {(int)VerificationPolicy.BlockedFlags})=0";
}
