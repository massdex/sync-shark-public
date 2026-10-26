using System.Globalization;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>Bounded explicit lifecycle events only; never captures runtime output or exception messages.</summary>
public static class AutoSyncLog
{
    internal const string FileName = "autosync.log";
    // RunLog retention selects dated run names, not this lifecycle log. Its separate budget
    // is one current and one previous generation, each at most ten MiB.
    internal const long FileBound = 10 * 1024 * 1024;
    internal const int LongestLine = 2048;
    private static long _dropped;

    /// <summary>The log file in the product logs directory that RunLog already owns.</summary>
    public static string FilePath(string productRoot) =>
        Path.Combine(RunLog.LogsDirectory(productRoot, autoSync: true), FileName);

    // Only the supervisor's fixed events and the typed methods below may call this sink.
    internal static void Append(string productRoot, string message)
    {
        var dropped = Interlocked.Exchange(ref _dropped, 0);
        try
        {
            var path = FilePath(productRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var line = message.ReplaceLineEndings(" ");
            if (line.Length > LongestLine) line = line[..LongestLine];
            if (dropped > 0) line = "dropped=" + dropped.ToString(CultureInfo.InvariantCulture) + " " + line;
            var bytes = new UTF8Encoding(false).GetBytes(stamp + " " + line + Environment.NewLine);
            // The current handle serializes size check, rotation and append across writers.
            // Contention never blocks shutdown; dropped lines are counted on a later write.
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete);
            if (stream.Length > FileBound - bytes.Length) Rotate(stream, path);
            stream.Position = stream.Length;
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        catch (Exception)
        {
            Interlocked.Add(ref _dropped, dropped + 1);
        }
    }

    /// <summary>Records a host shutdown using a fixed reason from the existing catalog.</summary>
    public static void HostShutdown(string productRoot, string reasonKey) =>
        Append(productRoot, "host shutdown: " + MessageCatalog.Default.Message(reasonKey));

    /// <summary>Records numeric assembly version, never a free-form informational version.</summary>
    public static void HostStarted(string productRoot, Version? version) =>
        Append(productRoot, string.Format(CultureInfo.InvariantCulture,
            MessageCatalog.Default.Message("autosync-host-start"), version));

    /// <summary>Records exception identity only, without message, data, inner exceptions or source paths.</summary>
    public static void Failure(string productRoot, Exception error) =>
        Append(productRoot, string.Format(CultureInfo.InvariantCulture,
            MessageCatalog.Default.Message("autosync-lifecycle-failure"),
            error.GetType().FullName, error.HResult, error.TargetSite?.Name));

    private static void Rotate(FileStream current, string path)
    {
        // Keep the current handle throughout rotation so another process cannot append between
        // the budget check and write. Failure propagates to Append's catch: never grow a full file.
        using var previous = new FileStream(path + ".previous", FileMode.Create, FileAccess.Write,
            FileShare.Read | FileShare.Delete);
        current.Position = current.Length > FileBound ? current.Length - FileBound : 0;
        current.CopyTo(previous);
        previous.Flush();
        current.SetLength(0);
    }
}
