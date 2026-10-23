using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark;

/// <summary>Attended allowlisted OS changes. Call only after explicit user choice; tests inject a fake.</summary>
public interface IElevatedAction
{
    Task<int> RunAsync(string verb, IReadOnlyList<string> args, CancellationToken cancellationToken);
}

public sealed class AttendedElevatedAction : IElevatedAction
{
    public const string JournalGrow = "journal-grow";
    public const string DefenderExclude = "defender-exclude";
    public const string DefenderVerify = "defender-verify";
    public const int ExclusionsConfirmedAbsent = 23;

    public static string ValidatePath(string root, string relative)
    {
        if (!Path.IsPathFullyQualified(root) || root.Contains('%') || root.IndexOfAny(['*', '?', '\r', '\n']) >= 0 ||
            Path.IsPathRooted(relative) || string.IsNullOrWhiteSpace(relative) ||
            relative.Split('/', '\\').Any(s => s is "" or "." or ".." || s.IndexOfAny([':', '*', '?', '"', '<', '>', '|', '%', '\r', '\n']) >= 0 || s.EndsWith('.') || s.EndsWith(' ')))
            throw new ArgumentException("Choose one exact file below the local pair root; no wildcards, environment expansions or traversal.");
        var boundary = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(boundary, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The exclusion must be below the local pair root.");
        return path;
    }

    public static string DownloadStagePath(string targetPath)
    {
        var full = Path.GetFullPath(targetPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(full.ToUpperInvariant()))).ToLowerInvariant();
        return Path.Combine(Path.GetDirectoryName(full)!, SyncLocalTransfer.ArtifactPrefix + hash + ".partial");
    }

    // Only base64 DATA enters the fixed program. Encoding the whole program is not a quoting defence.
    internal static string Command(string verb, IReadOnlyList<string> args)
    {
        if (verb == JournalGrow) return JournalCommand(args);
        if (verb is not (DefenderExclude or DefenderVerify) || args.Count != (verb == DefenderVerify ? 3 : 2) || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]))
            throw new ArgumentException("Unsupported elevated action or missing root boundary.");
        var root = Path.GetFullPath(args[0]);
        var path = ValidatePath(root, Path.GetRelativePath(root, args[1]));
        if (!string.Equals(path, args[1], StringComparison.Ordinal)) throw new ArgumentException("Noncanonical exclusion path.");
        if (verb == DefenderVerify && !string.Equals(args[2], DownloadStagePath(path), StringComparison.Ordinal))
            throw new ArgumentException("Verification must use the recorded exact sibling stage.");
        static string Data(string value) => Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
        var script = "$ErrorActionPreference='Stop'; $root=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + Data(root) +
            "')); $p=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + Data(path) + "')); " +
            "if ($root.Contains('%') -or $p.Contains('%') -or $root.IndexOfAny([char[]]'*?') -ge 0 -or $p.IndexOfAny([char[]]'*?') -ge 0) { throw 'Nonliteral path' }; " +
            "$boundary=[IO.Path]::GetFullPath($root).TrimEnd([char[]]'\\/')+'\\'; " +
            "if (-not [IO.Path]::IsPathRooted($p) -or [IO.Path]::GetFullPath($p) -cne $p -or -not $p.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)) { throw 'Outside reviewed root' }; " +
            "$relative=$p.Substring($boundary.Length); foreach ($part in $relative.Split([char[]]'\\/')) { if ($part -eq '' -or $part -eq '.' -or $part -eq '..' -or $part.Contains(':') -or $part.EndsWith('.') -or $part.EndsWith(' ')) { throw 'Nonliteral component' } }; " +

            "$stage=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + Data(DownloadStagePath(path)) + "')); " +
            "if ([IO.Path]::GetDirectoryName($stage) -cne [IO.Path]::GetDirectoryName($p) -or [IO.Path]::GetFileName($stage) -cnotmatch '^\\.syncshark-transfer-[0-9a-f]{64}\\.partial$') { throw 'Invalid exact sibling stage' }; " +
            "Write-Host 'Sync Shark: exact Defender paths:'; Write-Host $p; Write-Host $stage; ";
        if (verb == DefenderVerify)
            return script + "Write-Host 'Read-only verification; no Defender settings will change.'; " +
                "$pref=Get-MpPreference -ErrorAction Stop; $v=$pref.PSObject.Properties['HideExclusionsFromLocalAdmins']; " +
                "if ($null -eq $v -or $v.Value -isnot [bool] -or $v.Value -ne $false) { exit 2 }; " +
                "if ($pref.ExclusionPath | Where-Object { $_ -eq $p -or $_ -eq $stage }) { exit 3 }; exit 23";
        return script + "if ([IO.Directory]::Exists($p) -or [IO.Directory]::Exists($stage)) { throw 'Folder exclusions are not supported' }; " +
            "Write-Host 'Adding these two exact Defender exclusions'; Add-MpPreference -ExclusionPath @($p,$stage); " +
            "$actual=(Get-MpPreference).ExclusionPath; " +
            "if (-not ($actual | Where-Object { $_ -eq $p }) -or -not ($actual | Where-Object { $_ -eq $stage })) { exit 2 }; exit 0";
    }

    internal static string JournalCommand(IReadOnlyList<string> args)
    {
        if (args.Count != 3) throw new ArgumentException("Expected volume, serial and maximum bytes.");
        var volume = SyncSharkJournalNative.Volume(args[0]);
        if (!uint.TryParse(args[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var serial) ||
            !long.TryParse(args[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var maximum) ||
            maximum <= 0 || maximum > V2.JournalSizing.Maximum) throw new ArgumentException("Invalid journal bounds.");
        using var stream = typeof(AttendedElevatedAction).Assembly.GetManifestResourceStream("SyncShark.JournalNative.cs")!;
        using var reader = new StreamReader(stream);
        using var compressed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.SmallestSize, true))
        {
            var bytes = Encoding.UTF8.GetBytes(reader.ReadToEnd().Replace("#nullable disable", ""));
            gzip.Write(bytes);
        }
        var source = Convert.ToBase64String(compressed.ToArray());
        // Embedded source and canonical bounded numeric data only, never a user-selected executable or script.
        return "$ErrorActionPreference='Stop'; $m=[IO.MemoryStream]::new([Convert]::FromBase64String('" + source +
            "')); $z=[IO.Compression.GZipStream]::new($m,[IO.Compression.CompressionMode]::Decompress); " +
            "$r=[IO.StreamReader]::new($z); Add-Type -TypeDefinition $r.ReadToEnd(); $r.Dispose(); $m.Dispose(); [SyncSharkJournalNative]::Grow('" + volume + "'," + serial.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            maximum.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",$false); exit 0";
    }

    public async Task<int> RunAsync(string verb, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var command = Command(verb, args);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)),
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Normal
        }) ?? throw new IOException("The elevation request did not start. The item remains held.");
        await process.WaitForExitAsync(verb == JournalGrow ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}
