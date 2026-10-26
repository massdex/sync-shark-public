using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Massdex.SyncShark;

internal static class SyncLocalTransactionDirectory
{
    internal const string Prefix = ".syncshark-transaction-";
    internal const string StageName = "stage.partial";
    internal const string BackupName = "backup.backup";

    public static PilotDirectoryLease CreateAuthorized(PilotDirectoryLease controlRoot, string deterministicName,
        out string securityHash)
    {
        controlRoot.VerifyBinding();
        if (!deterministicName.StartsWith(Prefix, StringComparison.Ordinal) ||
            deterministicName.Length != Prefix.Length + 32 ||
            deterministicName[Prefix.Length..].Any(value => !char.IsAsciiHexDigit(value)) ||
            !string.Equals(Path.GetFileName(deterministicName), deterministicName, StringComparison.Ordinal))
            throw new InvalidDataException("The authorized transaction-directory name is not accepted.");
        var path = Path.Combine(controlRoot.Path, deterministicName);
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException("The authorized transaction-directory path is not absent.");
        var lease = PilotDirectoryLease.CreateChild(controlRoot, deterministicName);
        try
        {
            ApplyRestrictiveSecurity(path);
            lease.VerifyBinding();
            controlRoot.VerifyBinding();
            securityHash = ReadSecurityHash(path);
            return lease;
        }
        catch
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(path).Any()) lease.DeleteOnClose();
            }
            catch { }
            lease.Dispose();
            throw;
        }
    }

    public static PilotDirectoryLease OpenAuthorized(PilotDirectoryLease controlRoot, string deterministicName,
        string retainedIdentity, string retainedSecurityHash)
    {
        controlRoot.VerifyBinding();
        if (!deterministicName.StartsWith(Prefix, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(deterministicName), deterministicName, StringComparison.Ordinal))
            throw new InvalidDataException("The retained transaction-directory name is not accepted.");
        var path = Path.Combine(controlRoot.Path, deterministicName);
        var lease = PilotDirectoryLease.OpenOwnedDirectory(path, SyncLocalRecovery.ParseSourceIdentity(retainedIdentity));
        try
        {
            if (!string.Equals(ReadSecurityHash(path), retainedSecurityHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The retained transaction-directory security authority changed.");
            controlRoot.VerifyBinding();
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public static PilotDirectoryLease OpenCommitted(PilotDirectoryLease controlRoot, string deterministicName,
        string retainedIdentity, string retainedSecurityHash) =>
        OpenAuthorized(controlRoot, deterministicName, retainedIdentity, retainedSecurityHash);

    public static string SecurityPolicyId() => "current-user-protected-full-control-v1";

    public static SyncTransferIntent Prepare(PilotDirectoryLease controlRoot, SyncTransferIntent intent)
    {
        if (!SyncCapabilityGate.ExistingFileReplacement)
            throw new IOException(SyncCapabilityGate.ManualReviewReason("Private transaction preparation"));
        if (intent.Direction != SyncDirection.Download || intent.StageFileName != StageName ||
            intent.BackupFileName is not null && intent.BackupFileName != BackupName ||
            intent.TransactionDirectoryName is not null || intent.TransactionDirectoryId is not null ||
            intent.TransactionDirectorySecurityHash is not null || intent.ArtifactsBound)
            throw new InvalidDataException("The download intent is not an unbound private transaction.");

        controlRoot.VerifyBinding();
        var name = BuildName(intent.Id);
        var path = Path.Combine(controlRoot.Path, name);
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException("The private transfer transaction path already exists.");

        var lease = PilotDirectoryLease.CreateChild(controlRoot, name);
        try
        {
            controlRoot.VerifyBinding();
            ApplyRestrictiveSecurity(path);
            lease.VerifyBinding();
            controlRoot.VerifyBinding();
            var bound = intent with
            {
                TransactionDirectoryName = name,
                TransactionDirectoryId = FormatIdentity(lease.Identity),
                TransactionDirectorySecurityHash = ReadSecurityHash(path),
                StagingRootVolumeSerialNumber = controlRoot.Identity.VolumeSerialNumber,
                StagingRootFileId = controlRoot.Identity.FileId
            };
            lease.Dispose();
            return bound;
        }
        catch
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(path).Any()) lease.DeleteOnClose();
                lease.Dispose();
            }
            catch { }
            throw;
        }
    }

    public static PilotDirectoryLease Open(PilotDirectoryLease controlRoot, SyncTransferIntent intent)
    {
        controlRoot.VerifyBinding();
        if (intent.StagingRootVolumeSerialNumber != controlRoot.Identity.VolumeSerialNumber ||
            intent.StagingRootFileId != controlRoot.Identity.FileId)
            throw new IOException("The private transaction is not bound to the exact pair control root.");
        var path = PathFor(controlRoot, intent);
        if (!Directory.Exists(path) || File.Exists(path))
            throw new IOException("The private transfer transaction directory is absent.");
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || PilotPath.IsUnsafeAttributes(attributes))
            throw new IOException("The private transfer transaction path is not a regular directory.");
        var expectedIdentity = SyncLocalRecovery.ParseSourceIdentity(intent.TransactionDirectoryId!);
        var lease = PilotDirectoryLease.OpenOwnedDirectory(path, expectedIdentity);
        try
        {
            var securityHash = ReadSecurityHash(path);
            if (!string.Equals(securityHash, intent.TransactionDirectorySecurityHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException("The private transfer transaction security changed.");
            controlRoot.VerifyBinding();
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public static string PathFor(PilotDirectoryLease controlRoot, SyncTransferIntent intent)
    {
        var name = intent.TransactionDirectoryName;
        if (name is null || intent.TransactionDirectoryId is null || intent.TransactionDirectorySecurityHash is null ||
            !string.Equals(name, BuildName(intent.Id), StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            throw new InvalidDataException("The private transfer transaction binding is incomplete.");
        return Path.Combine(controlRoot.Path, name);
    }

    public static void DeleteIfEmpty(PilotDirectoryLease controlRoot, SyncTransferIntent intent)
    {
        var lease = Open(controlRoot, intent);
        var path = lease.Path;
        try
        {
            if (Directory.EnumerateFileSystemEntries(path).Any())
                throw new IOException("The private transfer transaction retains an unverified object.");
            lease.DeleteOnClose();
        }
        finally { lease.Dispose(); }
        controlRoot.VerifyBinding();
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException("The empty private transfer transaction directory was not removed.");
    }

    private static string BuildName(string intentId)
    {
        if (intentId.Length != 32 || intentId.Any(value => !Uri.IsHexDigit(value)))
            throw new InvalidDataException("The transfer intent identifier is not accepted.");
        return Prefix + intentId.ToLowerInvariant();
    }

    internal static void ApplyRestrictiveSecurity(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Private transfer transactions require Windows ACLs.");
        new DirectoryInfo(path).SetAccessControl(BuildRestrictiveSecurity());
    }

    private static DirectorySecurity BuildRestrictiveSecurity()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Private transfer transactions require Windows ACLs.");
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new IOException("The current Windows identity has no security identifier.");
        var security = new DirectorySecurity();
        security.SetOwner(sid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    internal static string ReadSecurityHash(string path)
    {
#pragma warning disable CA1416
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!security.AreAccessRulesProtected)
            throw new IOException("The private transfer transaction DACL is not protected.");
        var result = SecurityHash(security);
#pragma warning restore CA1416
        return result;
    }

    internal static string RequireRestrictiveSecurity(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Private storage requires Windows ACLs.");
#pragma warning disable CA1416
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new IOException("The current Windows identity has no security identifier.");
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false,
            typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToArray();
        if (!security.AreAccessRulesProtected || owner != sid || rules.Length != 1 ||
            rules[0].IdentityReference is not SecurityIdentifier ruleSid || ruleSid != sid ||
            rules[0].AccessControlType != AccessControlType.Allow ||
            rules[0].FileSystemRights != FileSystemRights.FullControl ||
            rules[0].InheritanceFlags != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) ||
            rules[0].PropagationFlags != PropagationFlags.None)
            throw new IOException("The private storage root is not restricted to the current Windows identity.");
        return SecurityHash(security);
#pragma warning restore CA1416
    }

    private static string SecurityHash(DirectorySecurity security)
    {
#pragma warning disable CA1416
        var sddl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sddl))).ToLowerInvariant();
#pragma warning restore CA1416
    }

    internal static string FormatIdentity(PilotDirectoryIdentity identity) =>
        $"{identity.VolumeSerialNumber:x8}:{identity.FileId:x16}";
}
