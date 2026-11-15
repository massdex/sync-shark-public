using System.Text;
using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks for LogFiles (`--engine-checks local`), written by LogFiles's writer.</summary>
public static class LogFilesChecks
{
    private const string UnicodeLine = "café 漢字";

    public static void Run()
    {
        var bufferCharacters = ShippedBufferCharacters();
        AppendAndFlush(bufferCharacters);
        PruneRetention();
    }

    // The direct writer: append, encoding, shared-reader visibility after flush and after disposal, folder creation.
    private static void AppendAndFlush(int bufferCharacters)
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(folder.FullName, nameof(LogFilesChecks));
            var expected = "";
            using (var writer = LogFiles.Open(path, bufferCharacters))
            {
                Check.Require(File.Exists(path), nameof(LogFiles.Open));
                Check.Require(writer.Encoding.CodePage == Encoding.UTF8.CodePage, nameof(Encoding.UTF8));
                Check.Require(ReadShared(path) == expected, nameof(FileMode.Append));
                foreach (var line in new[] { UnicodeLine, "" })
                {
                    writer.WriteLine(line);
                    expected += line + writer.NewLine;
                }
                // A shared reader sees the buffered lines once an explicit flush hands them to the file.
                writer.Flush();
                Check.Require(ReadShared(path) == expected, nameof(TextWriter.Flush));
            }
            Check.Require(File.ReadAllText(path) == expected, nameof(TextWriter.Dispose));

            // A later open appends; the final Dispose flushes the fresh write that was never explicitly flushed.
            using (var writer = LogFiles.Open(path, bufferCharacters))
            {
                Check.Require(ReadShared(path) == expected, nameof(FileMode.Append));
                foreach (var line in new[] { nameof(LogFiles), UnicodeLine })
                {
                    writer.WriteLine(line);
                    expected += line + writer.NewLine;
                }
            }
            Check.Require(File.ReadAllText(path) == expected, nameof(TextWriter.Dispose));

            var created = Path.Combine(folder.FullName, "new", "run.log");
            using (var writer = LogFiles.Open(created, bufferCharacters)) writer.WriteLine("x");
            Check.Require(File.Exists(created), "Open creates the logs folder");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static void PruneRetention()
    {
        var logs = Directory.CreateTempSubdirectory();
        try
        {
            var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
            var keep = TimeSpan.FromDays(30);
            string Make(string name, DateTimeOffset written)
            {
                var path = Path.Combine(logs.FullName, name);
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, written.UtcDateTime);
                return path;
            }
            var old = Make("old.log", now - keep - TimeSpan.FromMinutes(1));
            var fresh = Make("fresh.log", now - keep + TimeSpan.FromMinutes(1));
            var other = Make("old.txt", now - keep - TimeSpan.FromDays(1));
            var sub = logs.CreateSubdirectory("sub").FullName;
            var nested = Path.Combine(sub, "nested.log");
            File.WriteAllText(nested, "x");
            File.SetLastWriteTimeUtc(nested, (now - keep - TimeSpan.FromDays(1)).UtcDateTime);

            var locked = Make("locked.log", now - keep - TimeSpan.FromDays(1));
            var readOnly = Make("readonly.log", now - keep - TimeSpan.FromDays(1));
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            try
            {
                using var held = File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                LogFiles.Prune(logs.FullName, keep, now);
                Check.Require(File.Exists(locked) && File.Exists(readOnly),
                    "Pruning leaves locked and read-only logs without failing the run");
            }
            finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
            LogFiles.Prune(logs.FullName, keep, now);
            Check.Require(!File.Exists(locked) && !File.Exists(readOnly), "A later prune retries the retained logs");
            Check.Require(!File.Exists(old), "a log older than the retention is deleted");
            Check.Require(File.Exists(fresh), "a log inside the retention is kept");
            Check.Require(File.Exists(other), "a file without the log extension is never deleted");
            Check.Require(File.Exists(nested), "pruning is not recursive");
            var offsetNow = now.ToOffset(TimeSpan.FromHours(5));
            var offsetOld = Make("offset-old.log", offsetNow - keep - TimeSpan.FromMinutes(1));
            var offsetFresh = Make("offset-fresh.log", offsetNow - keep + TimeSpan.FromMinutes(1));
            LogFiles.Prune(logs.FullName, keep, offsetNow);
            Check.Require(!File.Exists(offsetOld), "a retention comparison uses the instant of a non-UTC now");
            Check.Require(File.Exists(offsetFresh), "a non-UTC now keeps a log inside the retention");
            LogFiles.Prune(Path.Combine(logs.FullName, "missing"), keep, now);
        }
        finally
        {
            logs.Delete(recursive: true);
        }
    }

    // The shipped settings, not a local literal, supply the buffer the direct writer opens with.
    private static int ShippedBufferCharacters()
    {
        using var document = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        return SettingsParser.Parse(document.RootElement.GetRawText(), null).LogBufferCharacters;
    }

    // The log stays open for writing, so a reader must share write access, as a log viewer would.
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd();
    }
}
