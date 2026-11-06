using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Massdex.SyncShark;

/// <summary>GitHub setup updates adapted from Resource Shark, independent of the sync runtime.</summary>
public static class SetupUpdates
{
    public const string Repository = "massdex/sync-shark-releases";
    // Resource Shark's feed envelope bound; installer bytes are streamed against GitHub's declared size.
    private const int MaximumFeedBytes = 2 * 1024 * 1024;

    public static SetupRelease Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("Only stable published releases are accepted.");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$") || !Version.TryParse(tag[1..], out _))
            throw new InvalidDataException("The release tag must be vX.Y.Z.");
        var name = $"SyncShark-Setup-{tag}.exe";
        var expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
        SetupRelease? result = null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var assetName = asset.GetProperty("name").GetString() ?? "";
            if (!names.Add(assetName)) throw new InvalidDataException("Duplicate release asset.");
            if (assetName != name) continue;
            var digest = asset.TryGetProperty("digest", out var node) ? node.GetString() ?? "" : "";
            var bytes = asset.GetProperty("size").GetInt64();
            if (!Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$") || bytes <= 0 ||
                asset.GetProperty("browser_download_url").GetString() != expected)
                throw new InvalidDataException("The setup asset needs its exact URL, size and GitHub SHA256 digest.");
            result = new SetupRelease(tag[1..], new Uri(expected), bytes, digest[7..]);
        }
        return result ?? throw new InvalidDataException("Exactly one versioned Sync Shark setup is required.");
    }

    public static async Task<SetupRelease> CheckAsync(CancellationToken cancellationToken)
    {
        using var client = Client();
        await using var source = await client.GetStreamAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken);
        using var buffer = new MemoryStream();
        await SyncPublicUpdates.CopyBoundedAsync(source, buffer, MaximumFeedBytes, cancellationToken);
        return Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    public static async Task<string> ApplyAsync(SetupRelease release, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SyncShark-Update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"SyncShark-Setup-v{release.Version}.exe");
        using var client = Client();
        await using (var source = await client.GetStreamAsync(release.Download, cancellationToken))
        await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            if (await SyncPublicUpdates.CopyBoundedAsync(source, destination, release.Bytes, cancellationToken) != release.Bytes)
                throw new InvalidDataException("The setup download is incomplete.");
        // Keep a deny-write/delete handle through signature verification and launch.
        await using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(held, cancellationToken));
        if (!digest.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The setup SHA256 differs from GitHub.");
        await VerifySignatureAsync(path, cancellationToken);
        using var setup = Process.Start(new ProcessStartInfo(path, "--wait-pid " + Environment.ProcessId) { UseShellExecute = false })
            ?? throw new IOException("Setup could not start.");
        return path;
    }

    private static HttpClient Client()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SyncShark-Setup-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static async Task VerifySignatureAsync(string path, CancellationToken cancellationToken)
    {
        // Windows validates Authenticode integrity; the repository identity pins the product, not another app.
        const string script = "$ErrorActionPreference='Stop';$s=Get-AuthenticodeSignature -LiteralPath $env:SYNCSHARK_SETUP;[pscustomobject]@{Status=[string]$s.Status;Signer=[Convert]::ToBase64String($s.SignerCertificate.RawData);Timestamp=[Convert]::ToBase64String($s.TimeStamperCertificate.RawData)}|ConvertTo-Json -Compress";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/WindowsPowerShell/v1.0/powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        start.Environment["SYNCSHARK_SETUP"] = path;
        start.Environment["PSModulePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/WindowsPowerShell/v1.0/Modules");
        using var process = Process.Start(start) ?? throw new IOException("Cannot start signature verification.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidDataException(await error);
        using var evidence = JsonDocument.Parse(await output);
        var signature = evidence.RootElement;
        using var identityStream = typeof(SetupUpdates).Assembly.GetManifestResourceStream("SyncShark.SignerIdentity.json")!;
        using var identity = JsonDocument.Parse(identityStream);
        var package = identity.RootElement.GetProperty("roles").GetProperty("package");
        var status = signature.GetProperty("Status").GetString();
        if (status is not ("Valid" or "UnknownError")) throw new InvalidDataException("Authenticode integrity failed.");
        using var signer = new X509Certificate2(Convert.FromBase64String(signature.GetProperty("Signer").GetString()!));
        using var timestamp = new X509Certificate2(Convert.FromBase64String(signature.GetProperty("Timestamp").GetString()!));
        using var rsa = signer.GetRSAPublicKey();
        if (rsa is null || signer.Subject != package.GetProperty("subject").GetString() || signer.Issuer != signer.Subject ||
            !package.GetProperty("thumbprints").EnumerateArray().Any(item => string.Equals(item.GetString(), signer.Thumbprint, StringComparison.OrdinalIgnoreCase)) ||
            !package.GetProperty("spki").EnumerateArray().Any(item => string.Equals(item.GetString(), Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The embedded signer differs from Sync Shark's pinned package identity.");
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        var trusted = chain.Build(signer);
        var statuses = chain.ChainStatus.Select(item => item.Status).Where(item => item != X509ChainStatusFlags.NoError).Distinct().ToArray();
        var onlyUntrustedRoot = statuses.Length == 1 && statuses[0] == X509ChainStatusFlags.UntrustedRoot;
        if ((!trusted && !onlyUntrustedRoot) || (status == "UnknownError" && !onlyUntrustedRoot))
            throw new InvalidDataException("Signer validation failed beyond the registered UntrustedRoot exception.");
        const string timestampEku = "1.3.6.1.5.5.7.3.8";
        if (!timestamp.Extensions.OfType<X509EnhancedKeyUsageExtension>().SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>()).Any(oid => oid.Value == timestampEku))
            throw new InvalidDataException("The timestamp certificate is not authorized for timestamp signing.");
        using var timestampChain = new X509Chain();
        timestampChain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        timestampChain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        if (!timestampChain.Build(timestamp) || timestampChain.ChainStatus.Any(item => item.Status != X509ChainStatusFlags.NoError))
            throw new InvalidDataException("The timestamp chain did not validate.");
    }
}
