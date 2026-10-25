namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    // Called by the inventory owner only after its awaited save/publication path has failed.
    // Completed siblings remain complete. Closing an incomplete generation never asserts absence.
    internal Task CloseFailedScanGenerationsAsync(IReadOnlyList<SyncAuthorityScanGeneration> scans)
    {
        if(scans.Count>2)throw new ArgumentOutOfRangeException(nameof(scans));
        return WriteAsync(async (connection,transaction)=>
        {
            var now=Utc(DateTimeOffset.UtcNow);
            foreach(var scan in scans)
                await ExecuteAsync(connection,transaction,"""
                    UPDATE scan_generation SET status='failed',ended_utc=$now
                    WHERE scan_generation_id=$id AND pair_id=$pair AND root_binding_id=$root
                      AND side=$side AND generation=$generation AND status='incomplete';
                    """,CancellationToken.None,("$id",scan.ScanGenerationId),("$pair",scan.PairId),
                    ("$root",scan.RootBindingId),("$side",scan.Side),("$generation",scan.Generation),("$now",now));
        },CancellationToken.None);
    }
}
