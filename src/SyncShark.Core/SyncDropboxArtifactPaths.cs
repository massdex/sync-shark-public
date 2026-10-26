namespace Massdex.SyncShark;

public static class SyncDropboxArtifactPaths
{
    public const string ProductContainerPath = "/Apps/Sync Shark";
    public const string RecoveryContainerPath = ProductContainerPath + "/Recovery";
    public const string TestingContainerPath = ProductContainerPath + "/Testing";

    public static string RequireContained(string path)
    {
        var exact = PilotPath.NormalizeDropboxRoot(path);
        if (!exact.StartsWith(ProductContainerPath + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "A Sync Shark-generated Dropbox artifact must be inside /Apps/Sync Shark.");
        return exact;
    }

    public static string RequireRecoveryContainer(string path)
    {
        var exact = PilotPath.NormalizeDropboxRoot(path);
        if (!string.Equals(exact, RecoveryContainerPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The Sync Shark Recovery folder must be /Apps/Sync Shark/Recovery.");
        return RecoveryContainerPath;
    }

    public static SyncRemoteRecoveryRootBinding RequireBoundRecoveryContainer(SyncPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        var binding = pair.RecoveryRoot ??
                      throw new InvalidOperationException("This operation requires the pair's bound recovery folder.");
        // The Dropbox Recovery-container path rule is provider-scoped: a Google Drive pair binds its own
        // recovery folder identity and is not forced through a Dropbox artifact path.
        if (SyncProviders.Normalize(pair.Provider) is SyncProviders.GoogleDrive or SyncProviders.Local)
            return binding;
        RequireRecoveryContainer(binding.RootPath);
        return binding;
    }

    // Provider-scoped: a Google Drive pair binds its own recovery folder identity, so the Dropbox
    // artifact-path rule applies to Dropbox pairs only.
    public static void RequireRecoveryContainerIfDropbox(SyncPair pair, string path)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (SyncProviders.Normalize(pair.Provider) is SyncProviders.GoogleDrive or SyncProviders.Local) return;
        RequireRecoveryContainer(path);
    }

    public static void ValidateBoundRecoveryContainerIfPresent(SyncPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (pair.RecoveryRoot is not null) RequireBoundRecoveryContainer(pair);
    }

    public static string BuildTestRunRoot(string runId)
    {
        return TestingContainerPath + "/" + NormalizeIdentifier(runId, "test run", 6, 64);
    }

    public static string BuildRecoveryOperationPath(string pairId, string operationId)
    {
        var pair = NormalizeIdentifier(pairId, "pair", 8, 64);
        if (operationId.Length != 32 || operationId.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("The Sync Shark recovery operation identity is not accepted.");
        return RecoveryContainerPath + "/" + pair + "/" + operationId.ToLowerInvariant();
    }

    public static string NormalizeRecoveryOperationPath(string path)
    {
        var exact = RequireContained(path);
        var prefix = RecoveryContainerPath + "/";
        if (!exact.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Dropbox recovery destination is outside Recovery.");
        var tail = exact[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (tail.Length != 2)
            throw new InvalidDataException("The Dropbox recovery destination is not an exact product operation path.");
        return BuildRecoveryOperationPath(tail[0], tail[1]);
    }

    private static string NormalizeIdentifier(string value, string role, int minimumLength, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < minimumLength || value.Length > maximumLength ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidDataException($"The Sync Shark {role} identity is not accepted.");
        return value;
    }
}
