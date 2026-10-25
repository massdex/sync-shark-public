using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class DropboxWriter
{
    // Spool is private product data. A bounded seekable stream lets the existing read-only route
    // transport retry safely without ever opening or truncating a user's destination.
    internal async Task<Stream> ExportDocumentAsync(ItemState item, string format, CancellationToken ct)
    {
        if (format is not ("docx" or "markdown" or "pdf" or "html")) throw new ArgumentException("Unsupported export format.", nameof(format));
        Directory.CreateDirectory(_spoolDirectory);
        var volume = Path.GetPathRoot(Path.GetFullPath(_spoolDirectory))!;
        var reservation = await SyncRecoverySpoolAdmission.ReserveAsync(volume, ExportBoundedStream.Limit,
            Guid.NewGuid().ToString("N"), new DriveInfo(volume).AvailableFreeSpace, _settings.FreeSpaceFloorBytes, ct).ConfigureAwait(false);
        FileStream? file = null;
        ExportBoundedStream? bounded = null;
        try
        {
            file = new FileStream(Path.Combine(_spoolDirectory, "export-" + Guid.NewGuid().ToString("N") + ".tmp"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            bounded = new ExportBoundedStream(file, reservation);
            var route = DropboxRouteTable.Embedded.Find("files/export") ?? throw new InvalidOperationException("Dropbox export route unavailable.");
            using var result = await _client.CallRouteAsync(new(route,
                JsonSerializer.Serialize(new { path = item.ObjectId, export_format = format }), ContentOut: bounded),
                new(2, 2, 2, TimeSpan.FromSeconds(30)), ct).ConfigureAwait(false);
            var metadata = result.Result.RootElement.GetProperty("file_metadata");
            if (metadata.GetProperty("id").GetString() != item.ObjectId ||
                metadata.GetProperty("rev").GetString() != item.VersionId)
                throw new IOException("The document changed during export. Retry to export the current version.");
            file.Position = 0;
            return bounded;
        }
        catch
        {
            if (bounded is not null) bounded.Dispose();
            else { file?.Dispose(); reservation.Dispose(); }
            throw;
        }
    }
}
