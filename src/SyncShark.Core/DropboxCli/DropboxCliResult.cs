using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>Writes an oversized result to a file so a large effect is never reported as a failure.</summary>
public static class DropboxCliResult
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    /// <summary>Returns the data unchanged when it fits the reply, else a spill receipt and the written file.</summary>
    public static async Task<object> FitAsync(IDropboxCliAccess access, string command, object data,
        Action<string> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (SyncCli.ResponseFits(data, command)) return data;
        var warnings = new DropboxCliWarnings(progress);
        var folder = access.ResultFolder;
        Directory.CreateDirectory(folder);
        Prune(folder, access.DropboxCliSettings.ResultRetention, warnings);
        var verb = command.StartsWith("dropbox ", StringComparison.Ordinal) ? command["dropbox ".Length..] : command;
        var file = Path.Combine(folder, DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'",
            CultureInfo.InvariantCulture) + "-" + verb + ".json");
        var encoding = new UTF8Encoding(false);
        var json = JsonSerializer.Serialize(data, Options);
        await File.WriteAllTextAsync(file, json, encoding, cancellationToken);
        return new DropboxCliSpilledData(true, file, encoding.GetByteCount(json), warnings.Result);
    }

    private static void Prune(string folder, TimeSpan retention, DropboxCliWarnings warnings)
    {
        var cutoff = (DateTimeOffset.UtcNow - retention).UtcDateTime;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
            }
            catch (IOException error) { DropboxCliLocalFiles.ReportCleanupFailure(path, error, warnings); }
            catch (UnauthorizedAccessException error) { DropboxCliLocalFiles.ReportCleanupFailure(path, error, warnings); }
    }
}
