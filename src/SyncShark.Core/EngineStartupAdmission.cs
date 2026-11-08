using System.Security;
using System.Security.Principal;

namespace Massdex.SyncShark;

/// <summary>Checks the obsolete footprint once for one current-Engine acquisition lifetime.</summary>
public sealed class EngineStartupAdmission
{
    public static string RefusalMessage => V2.MessageCatalog.Default.Message("startup-admission-refused");

    private readonly string _productRoot;

    private EngineStartupAdmission(string productRoot) => _productRoot = productRoot;

    /// <summary>Inspects metadata only, using the intended user or the current Windows identity.</summary>
    public static EngineStartupAdmission Require(string productRoot, string? userSid = null) =>
        Require(productRoot, () => userSid ?? CurrentUserSid(), FindArtifact,
            StorageInventoryWindows.ReadOwnedRegistrationsForUser);

    internal static EngineStartupAdmission Require(string productRoot, Func<string?> resolveSid,
        Func<string, string, string?> findArtifact,
        Func<string, IReadOnlyList<StorageRootRegistration>> readRegistrations)
    {
        var location = productRoot;
        try
        {
            if (!Path.IsPathFullyQualified(productRoot)) throw new ArgumentException(nameof(productRoot));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(productRoot));
            foreach (var (folder, pattern) in new[] { ("v2", "state.db*"), (Path.Combine("vNext", "state"), "syncshark.db*") })
            {
                location = Path.Combine(root, folder);
                if (findArtifact(location, pattern) is { } artifact)
                    throw new EngineStartupRefusedException("startup-legacy-store", artifact);
            }
            location = nameof(WindowsIdentity);
            var sid = resolveSid();
            if (!SyncRootRegistrationIdentity.ValidSid(sid))
                throw new EngineStartupRefusedException("startup-user-unavailable", location);
            location = StorageInventoryWindows.SyncRootManagerPath;
            var roots = readRegistrations(sid!);
            if (roots.Count != 0)
                throw new EngineStartupRefusedException("startup-owned-registration", roots[0].Path);
            return new EngineStartupAdmission(root);
        }
        catch (EngineStartupRefusedException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            throw new EngineStartupRefusedException("startup-inspection-failed", location, error);
        }
    }

    /// <summary>Prevents a nested composition from borrowing clearance for a different product root.</summary>
    public void RequireRoot(string productRoot)
    {
        if (!string.Equals(_productRoot, Path.TrimEndingDirectorySeparator(Path.GetFullPath(productRoot)),
            StringComparison.OrdinalIgnoreCase))
            throw new EngineStartupRefusedException("startup-inspection-failed", productRoot);
    }

    private static string? CurrentUserSid()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    }

    internal static string? FindArtifact(string folder, string pattern) => null;
}
