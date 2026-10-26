using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark.V2;

internal static class ArtifactPaths
{
    internal const string CaseRenameArtifactPrefix = ".syncshark-case-";
    internal const string RecoveryFolderPrefix = ".syncshark-recovery-";
    internal const string LocalPrefix = ".syncshark-control-";
    internal const string ArtifactPrefix = ".syncshark-transfer-";
    internal const string AccessRecoverySuffix = ".syncshark-acl";

    public static bool IsArtifactPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0) continue;
            if (segment.StartsWith(CaseRenameArtifactPrefix, StringComparison.OrdinalIgnoreCase) ||
                IsRecoveryFolderName(segment) ||
                segment.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase) ||
                IsTransferArtifactName(segment))
                return true;
        }
        return false;
    }

    public static bool IsTransferArtifactName(string name)
    {
        var artifactName = name.EndsWith(AccessRecoverySuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^AccessRecoverySuffix.Length]
            : name;
        return artifactName.StartsWith(ArtifactPrefix, StringComparison.OrdinalIgnoreCase) &&
               (artifactName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
                artifactName.EndsWith(".backup", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsRecoveryFolderName(string name) =>
        name.StartsWith(RecoveryFolderPrefix, StringComparison.OrdinalIgnoreCase) && name.Length > RecoveryFolderPrefix.Length;

    internal static string DownloadStagePath(string targetPath)
    {
        var full = Path.GetFullPath(targetPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(full.ToUpperInvariant()))).ToLowerInvariant();
        return Path.Combine(Path.GetDirectoryName(full)!, ArtifactPrefix + hash + ".partial");
    }
}
