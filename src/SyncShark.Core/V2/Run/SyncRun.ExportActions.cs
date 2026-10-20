using System.Globalization;

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    internal Func<RootDef, ItemState, string, CancellationToken, Task<Stream>>? ExportDocumentForTest { get; init; }

    private async Task<string> ExportNoticeDocumentAsync(PairDef pair, Notice notice, string choice, CancellationToken ct)
    {
        var format = NoticeChoices.ExportFormat(choice) ?? throw new ArgumentException("Unknown export format.");
        // The buttons came from the provider's own export_info. A stale or fabricated choice must not export a
        // format the provider never offered; the notice stays open with its remaining options.
        if (notice.ExportInfo is { } offered && !offered.Formats.Contains(format, StringComparer.OrdinalIgnoreCase))
            throw new SyncRunRefusedException(
                "That format is not offered for this document. Choose one of: " +
                (offered.Formats.Count > 0 ? string.Join(", ", offered.Formats) : "Open in Dropbox") +
                ". Nothing was changed.");
        var cloudSide = pair.A.Provider == SyncProviders.Dropbox ? Side.A : Side.B;
        var cloud = cloudSide == Side.A ? pair.A : pair.B;
        var local = cloudSide == Side.A ? pair.B : pair.A;
        if (!pair.Enabled || cloud.Provider != SyncProviders.Dropbox || local.Provider != SyncProviders.Local)
            throw new SyncRunRefusedException("Enable this local/Dropbox pair before exporting. You can also open the document in Dropbox.");
        RefuseLayout(pair);
        await _endpoints.PrepareAsync(pair, ct).ConfigureAwait(false);
        var cloudWriter = _endpoints.Writer(cloud);
        var localWriter = _endpoints.Writer(local);
        var original = await cloudWriter.ObserveAsync(notice.Path, ct).ConfigureAwait(false);
        if (original is null || !original.Flags.HasFlag(ItemFlags.RequiresExport))
            throw new SyncRunRefusedException("This document changed or no longer needs export. Retry sync to refresh the notice.");
        var extension = format == "markdown" ? "md" : format;
        // Separate local file, not a replacement for the cloud-native original. The exported name keeps the
        // document's name, so the user can tell where it came from; a suffix keeps every export create-only.
        var mapped = DropboxNameMapping.MatchPath(original);
        var cut = mapped.LastIndexOf('/');
        var parent = cut >= 0 ? mapped[..(cut + 1)] : "";
        var leaf = cut >= 0 ? mapped[(cut + 1)..] : mapped;
        var dot = leaf.LastIndexOf('.');
        var stem = dot > 0 ? leaf[..dot] : leaf;
        var baseName = stem + " (exported " + _time.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")";
        var destination = await UniqueExportNameAsync(parent, baseName, extension, cloudWriter, localWriter, ct).ConfigureAwait(false);
        var include = BuildPredicates(pair, PairInclude.ForAction);
        if (!(cloudSide == Side.A ? include.A : include.B)(notice.Path) ||
            !include.A(destination) || !include.B(destination) ||
            _store.ProtectionBatch(local.RootId, ItemFlags.Link | ItemFlags.Placeholder | ItemFlags.NeedsRehash, [destination]).Any(p => p.Covered || p.Contains))
            throw new SyncRunRefusedException("The source or destination is protected by selection, exclusions or a nested pair. Review folder settings, or export from Dropbox.");
        _store.AddNotices([notice with { Detail = "Exporting as " + format + "; the original stays untouched. Unsupported formats leave this notice open so you can choose another format or open Dropbox." }]);
        try
        {
            await using var content = ExportDocumentForTest is { } export
                ? await export(cloud, original, format, ct).ConfigureAwait(false)
                : cloudWriter is DropboxWriter writer ? await writer.ExportDocumentAsync(original, format, ct).ConfigureAwait(false)
                : throw new SyncRunRefusedException("This provider cannot export here. Open the document in Dropbox instead.");
            if (!content.CanSeek || content.Length > ExportBoundedStream.Limit)
                throw new IOException("The exported document exceeds the bounded staging limit.");
            if (!SameRepairItem(await cloudWriter.ObserveAsync(notice.Path, ct).ConfigureAwait(false), original))
                throw new IOException("The cloud document changed during export. Retry to export its current version.");
            if (await cloudWriter.ObserveAsync(destination, ct).ConfigureAwait(false) is not null ||
                await localWriter.ObserveAsync(destination, ct).ConfigureAwait(false) is not null)
                throw new IOException("The export destination is occupied. Choose Export again for a new unique name.");
            content.Position = 0;
            var hash = await DropboxContentHash.ComputeAsync(content, ct).ConfigureAwait(false);
            content.Position = 0;
            var source = new ItemState(destination, ItemKind.File, "export:" + Guid.NewGuid().ToString("N"), null,
                hash, content.Length, _time.GetUtcNow(), ItemFlags.None);
            var action = new SyncAction(ShortId.Make("exp", pair.PairId, destination), notice.Rule, ActionKind.CopyNew,
                cloudSide == Side.A ? Side.B : Side.A, destination, null, source, null, false, null);
            var admission = localWriter is ILocalFreeSpaceAdmission space
                ? await space.AdmitFreeSpaceAsync(action, ct).ConfigureAwait(false) : FreeSpaceAdmission.Granted;
            using var reservation = admission.Reservation;
            if (admission.Refusal is { } refusal) throw new IOException(refusal.Detail);
            // Queue observation before the create-only effect, so an interrupted reply never hides a
            // successfully published local export. No baseline is invented for the native document.
            _store.SaveSyncRetry(pair.PairId, new(destination, "sync", 0, "Observe the requested document export.", DateTimeOffset.MinValue));
            await localWriter.PutAsync(action, content, ct).ConfigureAwait(false);
            _store.AddNotices([notice with { Detail = "Exported a new local file: " + destination + ". It will sync normally on the next run. The original cloud document was not changed; Ignore leaves it excluded, or Export creates another copy." }]);
            return "Exported as " + destination + ". The next run syncs the new file; the original was not overwritten or deleted.";
        }
        catch (Exception error) when (RunFailure(error, ct))
        {
            // One notice, same identity and options: the failure only refreshes its display text. The action must
            // stay retryable and offer the provider's formats again, never a fabricated success or a second notice.
            _store.AddNotices([notice with { Detail = "Export was not verified: " + BlockerDiagnostic.Describe(error) + " Choose another export format, Retry, Ignore, or Open in Dropbox. Any already-created copy stays untouched." }]);
            return "Export was not verified. See this notice for the reason and available alternatives.";
        }
    }

    // Bounded unique-name probe on both sides; the ordinary create-only write below still checks again under the
    // executor, so a concurrent creation can never be overwritten.
    private static async Task<string> UniqueExportNameAsync(string parent, string baseName, string extension,
        IEndpointWriter cloudWriter, IEndpointWriter localWriter, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 100; attempt++)
        {
            var candidate = parent + baseName + (attempt == 1 ? string.Empty : " " + attempt.ToString(CultureInfo.InvariantCulture)) + "." + extension;
            if (await cloudWriter.ObserveAsync(candidate, ct).ConfigureAwait(false) is null &&
                await localWriter.ObserveAsync(candidate, ct).ConfigureAwait(false) is null)
                return candidate;
        }
        throw new SyncRunRefusedException(
            "No free export name is available beside this document. Remove or rename an older export, or open the document in Dropbox; nothing was changed.");
    }
}
