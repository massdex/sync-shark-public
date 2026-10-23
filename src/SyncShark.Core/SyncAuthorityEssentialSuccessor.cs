using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

internal sealed record SyncEssentialSourceIdentity(string Path, uint VolumeSerialNumber, ulong FileId,
    long Bytes, string Sha256, int SchemaVersion, string InstallationId, string DatabaseGeneration, string SidecarBindingSha256 = "");
internal sealed record SyncEssentialSuccessorOptions(string IsolatedRoot);
internal sealed record SyncEssentialTableDisposition(string Table, long SourceRows, long SelectedRows,
    long OriginalOnlyRows, string Reason, string? SelectedDigest = null, long? TargetRows = null,
    long? SourcePayloadBytes = null, long? SelectedPayloadBytes = null, long? OriginalOnlyPayloadBytes = null);
internal sealed record SyncEssentialSuccessorReceipt(SyncEssentialSourceIdentity Source, string OptionsSha256,
    string TargetGeneration, bool Complete, IReadOnlyList<SyncEssentialTableDisposition> Tables,
    IReadOnlyList<SyncEssentialCutReceipt>? Cuts = null, string? HistoryAsOfUtc = null,
    SyncActivityRetentionPolicy? HistoryPolicy = null);
internal sealed record SyncEssentialSuccessorReport(string TargetPath, string TargetGeneration, bool Complete,
    IReadOnlyList<SyncEssentialTableDisposition> Tables);

// Explicit offline/test surface. No startup, settings-switch, provider, scheduler
// or install consumer calls it. Configuration, complete/current observations and
// baseline state use explicit adapters. Every table has a current-state or
// original-only disposition; unimplemented safety state refuses preparation.
internal static class SyncAuthorityEssentialSuccessor
{
    internal static Task<SyncEssentialSourceIdentity> InspectSourceAsync(string sourcePath,
        SyncEssentialSuccessorOptions options, CancellationToken token = default) =>
        SyncAuthorityDatabase.InspectEssentialSourceAsync(sourcePath, options, token);

    internal static Task<SyncEssentialSuccessorReport> PrepareEssentialSuccessorAsync(
        string sourcePath, string targetPath, SyncEssentialSourceIdentity expectedSourceIdentity,
        SyncEssentialSuccessorOptions options, IProgress<string>? progress = null,
        CancellationToken token = default) =>
        SyncAuthorityDatabase.PrepareEssentialSuccessorCoreAsync(sourcePath, targetPath,
            expectedSourceIdentity, options, progress, token);

    internal static Task<SyncEssentialSuccessorReport> VerifyEssentialSuccessorAsync(
        string sourcePath, string targetPath, SyncEssentialSourceIdentity expectedSourceIdentity,
        SyncEssentialSuccessorOptions options, CancellationToken token = default) =>
        SyncAuthorityDatabase.VerifyEssentialSuccessorCoreAsync(sourcePath, targetPath,
            expectedSourceIdentity, options, token);
}


internal sealed record SyncEssentialCutReceipt(string ScanId,string Status,long Rows,string ProjectionSha256,
    string? SourceCutDigest,string? SourceInputDigest);
