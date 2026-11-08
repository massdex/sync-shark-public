using System.Buffers;
using System.Text;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>The only file I/O for run logs (section 4.7).</summary>
public static class LogFiles
{
    /// <summary>How a read-only log inspection ended; inspection never creates, prunes or changes anything.</summary>
    public enum ReadStatus { Found, NotFound, Unreadable }

    /// <summary>Appends UTF-8 with the supplied character buffer; creates the logs folder when absent.</summary>
    public static TextWriter Open(string path, int bufferCharacters)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new StreamWriter(path, append: true,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: bufferCharacters);
    }

    /// <summary>Resolves one exact pair/run log through the owned file-name grammar and a containment check, or
    /// returns null when an id is empty or cannot be a file name. No directory is created.</summary>
    public static string? Resolve(string folder, string pairId, string runId)
    {
        if (pairId.Length == 0 || runId.Length == 0) return null;
        var invalid = Path.GetInvalidFileNameChars();
        if (pairId.IndexOfAny(invalid) >= 0 || runId.IndexOfAny(invalid) >= 0) return null;
        var name = pairId + ResourceNames.LogNameSeparator + runId + ResourceNames.LogExtension;
        var directory = Path.GetFullPath(folder);
        var full = Path.GetFullPath(Path.Combine(directory, name));
        return string.Equals(Path.GetDirectoryName(full), directory, StringComparison.Ordinal) &&
            string.Equals(Path.GetFileName(full), name, StringComparison.Ordinal)
            ? full : null;
    }

    /// <summary>Streams complete records to `record` with shared read access, block by block and cancellable. A
    /// trailing record without its newline terminator is unfinished and is not delivered; the caller reports it.
    /// An undecodable record is reported as not well formed. One record buffers only until its own newline, so
    /// memory is O(one record) and no product cap is needed; nothing is buffered for the whole file.</summary>
    public static ReadStatus Read(string path, Action<string, bool> record, CancellationToken ct,
        out bool unfinishedTrailingRecord)
    {
        unfinishedTrailingRecord = false;
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return ReadStatus.NotFound;
        }
        catch (Exception error) when (LocalErrors.IsManagedIoFailure(error))
        {
            return ReadStatus.Unreadable;
        }
        try
        {
            using var owned = stream;
            var bytes = new byte[ResourceNames.LogReadBufferCharacters];
            var pending = new ArrayBufferWriter<byte>();
            var firstRecord = true;
            ct.ThrowIfCancellationRequested();
            while (true)
            {
                var read = stream.Read(bytes, 0, bytes.Length);
                ct.ThrowIfCancellationRequested();
                if (read == 0) break;
                for (var index = 0; index < read; index++)
                {
                    var value = bytes[index];
                    if (value != NewLineByte)
                    {
                        pending.Write([value]);
                        continue;
                    }
                    var buffer = pending.WrittenSpan;
                    var start = firstRecord && IsByteOrderMark(buffer) ? Utf8Preamble.Length : 0;
                    firstRecord = false;
                    var wellFormed = TryDecode(buffer, start, out var text);
                    record(text, wellFormed);
                    pending.Clear();
                }
            }
            unfinishedTrailingRecord = pending.WrittenCount > 0;
            return ReadStatus.Found;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (LocalErrors.IsManagedIoFailure(error))
        {
            return ReadStatus.Unreadable;
        }
    }

    private const byte NewLineByte = (byte)'\n';
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    // The platform's own UTF-8 preamble, skipped once at the start of a stored log; the writer never emits one.
    private static readonly byte[] Utf8Preamble = Encoding.UTF8.GetPreamble();

    private static bool IsByteOrderMark(ReadOnlySpan<byte> pending) =>
        pending.Length >= Utf8Preamble.Length && pending[..Utf8Preamble.Length].SequenceEqual(Utf8Preamble);

    private static bool TryDecode(ReadOnlySpan<byte> pending, int start, out string text)
    {
        var count = pending.Length - start;
        if (count > 0 && pending[^1] == (byte)'\r') count--;
        try
        {
            text = StrictUtf8.GetString(pending.Slice(start, count));
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary>Deletes the engine's run logs (files with the log extension directly in the folder, not recursive)
    /// whose last write is older than the retention period. A missing folder prunes nothing.</summary>
    public static void Prune(string folder, TimeSpan olderThan, DateTimeOffset now)
    {
        var logs = new DirectoryInfo(folder);
        if (!logs.Exists) return;
        try
        {
            foreach (var log in logs.EnumerateFiles(ResourceNames.LogPattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (now.UtcDateTime - log.LastWriteTimeUtc > olderThan) log.Delete();
                }
                catch (Exception error) when (LocalErrors.IsManagedIoFailure(error))
                {
                    // A locked or unwritable log can be pruned by the next run.
                }
            }
        }
        catch (Exception error) when (LocalErrors.IsManagedIoFailure(error))
        {
            // The directory can disappear or become inaccessible while enumerating, too.
        }
    }
}
