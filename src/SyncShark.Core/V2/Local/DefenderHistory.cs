using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>Bounded read-only evidence. Absence is useful only after a complete successful query.</summary>
public sealed class DefenderHistory : IDefenderHistory
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Lazy<Task<DefenderHistoryResult>>> Runs = new();
    public Task<DefenderHistoryResult> ReadAsync(CancellationToken ct) =>
        ReadCachedAsync(RunOperations.ContextIdentity, QueryAsync, ct);

    internal static Task<DefenderHistoryResult> ReadCachedAsync(object? run, Func<Task<DefenderHistoryResult>> query, CancellationToken ct) =>
        (run is null ? query() : Runs.GetValue(run, _ => new Lazy<Task<DefenderHistoryResult>>(query)).Value).WaitAsync(ct);

    private static async Task<DefenderHistoryResult> QueryAsync()
    {
        if (!OperatingSystem.IsWindows()) return DefenderHistoryResult.Unknown;
        // No sorting or truncation in the producer. Only file resources cross the pipe; a terminal marker proves enumeration finished.
        const string command = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); try { $s=Get-MpComputerStatus -ErrorAction Stop; if ($s.AMServiceEnabled -is [bool] -and $s.AMServiceEnabled -eq $false) { Write-Output 'STATUS:Inactive' } elseif ($s.AMServiceEnabled -is [bool] -and $s.AMServiceEnabled -eq $true) { Write-Output ('STATUS:' + $s.AMRunningMode) } } catch { }; Get-MpThreatDetection | ForEach-Object { $_.Resources | Where-Object { $_ -like 'file:_*' } | ForEach-Object { ConvertTo-Json -InputObject @{Resource=$_} -Compress } }; Write-Output 'COMPLETE'";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Process? process = null;
        var evidence = new List<DefenderDetection>();
        var limited = false;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            });
            if (process is null) return DefenderHistoryResult.Unknown;
            var drainError = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            var availability = DefenderAvailability.Unknown;
            var complete = await ReadStreamAsync(process.StandardOutput, evidence, timeout.Token,
                status: value => availability = value, limitReached: () => limited = true).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await drainError.ConfigureAwait(false);
            return new(evidence, complete && process.ExitCode == 0, availability, limited);
        }
        catch (Exception) { return new(evidence, false, LimitReached: limited); }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                process.Dispose();
            }
        }
    }

    // Bounded lines/total bytes/evidence; malformed or truncated input retains earlier positives but is UNKNOWN.
    internal static async Task<bool> ReadStreamAsync(TextReader input, List<DefenderDetection> evidence, CancellationToken ct,
        int maxChars = 1024 * 1024, int maxRecords = 8192, Action<DefenderAvailability>? status = null, Action? limitReached = null)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var total = 0;
        var complete = false;
        var valid = true;
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) break;
            for (var i = 0; i < count; i++)
            {
                if (++total > maxChars) { limitReached?.Invoke(); return false; }
                var c = buffer[i];
                if (c != '\n')
                {
                    if (line.Length >= 32768) { limitReached?.Invoke(); return false; }
                    if (c != '\r') line.Append(c);
                    continue;
                }
                if (!complete && line.ToString().StartsWith("STATUS:", StringComparison.Ordinal))
                    status?.Invoke(AvailabilityForMode(line.ToString()[7..]));
                else if (line.ToString() == "COMPLETE") complete = true;
                else if (line.Length != 0)
                {
                    if (complete) return false;
                    if (evidence.Count >= maxRecords) { limitReached?.Invoke(); return false; }
                    try
                    {
                        using var doc = JsonDocument.Parse(line.ToString());
                        var elements = doc.RootElement.ValueKind == JsonValueKind.Array
                            ? doc.RootElement.EnumerateArray().ToArray() : [doc.RootElement];
                        foreach (var element in elements)
                        {
                            var parsed = Parse(element.GetRawText());
                            if (parsed.Count != 1) { valid = false; continue; }
                            if (evidence.Count >= maxRecords) { limitReached?.Invoke(); return false; }
                            evidence.Add(parsed[0]);
                        }
                    }
                    catch (JsonException) { valid = false; }
                }
                line.Clear();
            }
        }
        return valid && complete && line.Length == 0;
    }

    // EDR block mode and limited periodic scanning (SxS passive) still remediate files, so they are never proof that
    // Defender cannot have removed one: they stay Unknown and reach the review instead of releasing automatically.
    internal static DefenderAvailability AvailabilityForMode(string mode) => mode.ToUpperInvariant() switch
    {
        "ACTIVE" or "NORMAL" => DefenderAvailability.Active,
        "INACTIVE" or "PASSIVE MODE" or "NOT RUNNING" => DefenderAvailability.Inactive,
        _ => DefenderAvailability.Unknown
    };

    internal static IReadOnlyList<DefenderDetection> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<DefenderDetection>();
        void Add(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("Resource", out var resource) &&
                resource.ValueKind == JsonValueKind.String && resource.GetString() is { Length: > 0 } text)
                result.Add(new(text, default)); // Timestamp is deliberately irrelevant: exact detections never expire here.
        }
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var element in document.RootElement.EnumerateArray()) Add(element);
        else Add(document.RootElement);
        return result;
    }

    internal static string? RelativePath(string root, string resource)
    {
        if (!resource.StartsWith("file:_", StringComparison.OrdinalIgnoreCase)) return null;
        var absolute = Normalize(resource[6..].Split("->", 2, StringSplitOptions.None)[0]);
        if (!Path.IsPathFullyQualified(absolute)) return null;
        try
        {
            var boundary = Path.GetFullPath(Normalize(root)).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(absolute);
            return path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)
                ? path[boundary.Length..].Replace(Path.DirectorySeparatorChar, '/') : null;
        }
        catch (ArgumentException) { return null; }
    }

    private static string Normalize(string path) => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
        ? @"\\" + path[8..] : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
}
