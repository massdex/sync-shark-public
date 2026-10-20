using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class DropboxWriter
{
    // Spool is private product data. A bounded seekable stream lets the existing read-only route
    // transport retry safely without ever opening or truncating a user's destination.
    internal async Task<Stream> ExportDocumentAsync(ItemState item, string format, CancellationToken ct)
    {
        if (format is not ("docx" or "markdown" or "pdf")) throw new ArgumentException("Unsupported export format.", nameof(format));
        Directory.CreateDirectory(_spoolDirectory);
        var volume = Path.GetPathRoot(Path.GetFullPath(_spoolDirectory))!;
        var reservation = await SyncRecoverySpoolAdmission.ReserveAsync(volume, ExportBoundedStream.Limit,
            Guid.NewGuid().ToString("N"), new DriveInfo(volume).AvailableFreeSpace, _settings.FreeSpaceFloorBytes, ct).ConfigureAwait(false);
        ExportBoundedStream? bounded = null;
        try
        {
            var file = new FileStream(Path.Combine(_spoolDirectory, "export-" + Guid.NewGuid().ToString("N") + ".tmp"),
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
        catch { bounded?.Dispose(); reservation.Dispose(); throw; }
    }
}

/// <summary>Hard disk and memory bound for cloud-native document export, including retry seeks.</summary>
internal sealed class ExportBoundedStream(Stream inner, IDisposable? reservation = null) : Stream
{
    internal const long Limit = 256L * 1024 * 1024;
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => inner.CanSeek;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set { Check(value); inner.Position = value; } }
    private static void Check(long value)
    {
        if (value < 0 || value > Limit) throw new IOException("The export exceeds the 256 MiB limit. Export it in Dropbox, or choose another format.");
    }
    public override void SetLength(long value) { Check(value); inner.SetLength(value); }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var start = origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        Position = checked(start + offset); return Position;
    }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override void Write(byte[] buffer, int offset, int count) { Check(checked(Position + count)); inner.Write(buffer, offset, count); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    { Check(checked(Position + buffer.Length)); return inner.WriteAsync(buffer, ct); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && reservation is not null) { try { inner.Dispose(); } finally { reservation.Dispose(); } }
        base.Dispose(disposing);
    }
}
