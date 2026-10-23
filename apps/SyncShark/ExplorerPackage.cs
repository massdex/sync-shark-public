using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Windows.Management.Deployment;

namespace Massdex.SyncShark;

// Windows owns signature/trust enforcement and package registration. This class
// never imports a certificate, enables Developer Mode or changes another user.
internal static class ExplorerPackage
{
    internal const string Name = "Massdex.SyncShark";
    internal const string Publisher = "CN=Massdex Sync Shark Code Signing v1, O=Massdex";
    internal const string FileName = "SyncShark.msix";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // The exact shell-resource member inventory that SyncShark.IdentityPackage emits and
    // release preparation asserts before staging. The unplated artwork is what makes the
    // taskbar and Start surfaces use the product mark, so an indexed package must carry the
    // complete set. Legacy packages that predate the indexed resources keep the original
    // allowlist and validation, and no wider Assets allowance is admitted.
    private static readonly int[] ShellTargetSizes = { 16, 20, 24, 32, 48, 64, 256 };
    private static readonly string[] ShellTargetForms = { "", "_altform-unplated", "_altform-lightunplated" };
    private static readonly string[] IndexedShellMembers = BuildIndexedShellMembers();
    private static readonly string[] RequiredIndexedMembers = BuildRequiredIndexedMembers();
    private static readonly HashSet<string> KnownMembers = BuildKnownMembers();

    private static string[] BuildIndexedShellMembers()
    {
        var names = new List<string> { "resources.pri" };
        foreach (var size in ShellTargetSizes)
            foreach (var form in ShellTargetForms)
                names.Add("Assets/SyncShark-64.targetsize-" + size.ToString(CultureInfo.InvariantCulture) + form + ".png");
        return names.ToArray();
    }

    private static string[] BuildRequiredIndexedMembers()
    {
        var names = new List<string>(IndexedShellMembers) { "Assets/SyncShark-64.png" };
        return names.ToArray();
    }

    private static HashSet<string> BuildKnownMembers()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "AppxManifest.xml", "AppxBlockMap.xml", "[Content_Types].xml", "AppxSignature.p7x",
            "Assets/SyncShark-64.png", "AppxMetadata/CodeIntegrity.cat", "SyncShark.Explorer.dll" };
        names.UnionWith(IndexedShellMembers);
        return names;
    }

    internal static string Status()
    {
        var packages = new PackageManager().FindPackagesForUser("").Where(package => package.Id.Name == Name).ToArray();
        if (packages.Any(package => package.Id.Publisher != Publisher)) return "Package Identity Conflict";
        return packages.Length == 0 ? "Main Menu Is Not Registered; Classic Menu Is Available" : "Main Explorer Menu Is Registered";
    }

    internal static Task RegisterAsync() => ChangeAsync(register: true);
    internal static Task RemoveAsync() => ChangeAsync(register: false);

    internal sealed record Registration(string FullName, string Publisher, Version Version, string Directory);

    // Both Settings and command-line maintenance use this decision before asking
    // Windows to mutate a package. Version upgrades belong to the bound installer,
    // not to an implicit repair of an unexpected existing registration.
    internal static async Task ApplyObservedRegistrationAsync(bool register, bool isInstalledProcess,
        string directory, Version version, IReadOnlyList<Registration> existing,
        Action verifyRegistered, Func<Task> add, Func<string, Task> remove, Action verifyAbsent)
    {
        if (!isInstalledProcess) throw new IOException("Only the active installed app can change its Explorer package.");
        if (existing.Count > 1 || existing.Any(package => package.Publisher != Publisher ||
                !MatchesRegistration(version, directory, package.Version, package.Directory)))
            throw new IOException("The existing Explorer package does not match this installed version and location. Nothing was changed; finish or repair the signed installation first.");
        if (register)
        {
            if (existing.Count == 0) await add();
            verifyRegistered();
        }
        else
        {
            if (existing.Count == 1)
            {
                verifyRegistered();
                await remove(existing[0].FullName);
            }
            verifyAbsent();
        }
    }

    private static async Task ChangeAsync(bool register)
    {
        if (!ExplorerIntegration.IsInstalledProcess) throw new InvalidOperationException("Use the signed installed app to set up the Explorer menu.");
        await Gate.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(ExistingWindowActivation.ResolveExecutable(Environment.ProcessPath!))!;
            using var root = PilotDirectoryLease.OpenInstalledRuntime(directory);
            var path = Path.Combine(directory, FileName);
            using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var version = typeof(App).Assembly.GetName().Version ?? throw new IOException("The installed version is unavailable.");
            Validate(held, version);
            var manager = new PackageManager();
            var existing = manager.FindPackagesForUser("").Where(package => package.Id.Name == Name)
                .Select(package => new Registration(package.Id.FullName, package.Id.Publisher,
                    new Version(package.Id.Version.Major, package.Id.Version.Minor,
                        package.Id.Version.Build, package.Id.Version.Revision), package.EffectiveExternalPath)).ToArray();
            root.VerifyBinding();
            await ApplyObservedRegistrationAsync(register, ExplorerIntegration.IsInstalledProcess, directory, version,
                existing, () => VerifyRegistered(directory, version), async () =>
                {
                    root.VerifyBinding();
                    var result = await manager.AddPackageByUriAsync(new Uri(path), new AddPackageOptions { ExternalLocationUri = new Uri(directory + "\\") });
                    if (result.ExtendedErrorCode is { } error && error.HResult < 0)
                        throw new IOException("Windows could not register the signed Explorer package: " + result.ErrorText, error);
                }, async fullName =>
                {
                    root.VerifyBinding();
                    var result = await manager.RemovePackageAsync(fullName);
                    if (result.ExtendedErrorCode is { } error && error.HResult < 0)
                        throw new IOException("Windows could not remove the Explorer package: " + result.ErrorText, error);
                }, VerifyAbsent);
            root.VerifyBinding();
        }
        finally { Gate.Release(); }
    }

    internal static void VerifyRegistered(string directory, Version version)
    {
        var registered = new PackageManager().FindPackagesForUser("").Where(package => package.Id.Name == Name).ToArray();
        if (registered.Length != 1 || registered[0].Id.Publisher != Publisher ||
            registered[0].Id.Version.Major != version.Major || registered[0].Id.Version.Minor != version.Minor ||
            registered[0].Id.Version.Build != version.Build || registered[0].Id.Version.Revision != version.Revision ||
            !string.Equals(registered[0].EffectiveExternalPath.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Windows did not return the expected Explorer package version and installed location.");
        // Sparse COM resolves the external runtime. Prove that exact root is
        // protected; the WindowsApps copy below is only an integrity cross-check.
        // Actual dllhost loaded-path proof remains an installed acceptance check.
        using var runtime = PilotDirectoryLease.OpenInstalledRuntime(directory);
        using var package = new FileStream(Path.Combine(directory, FileName), FileMode.Open, FileAccess.Read, FileShare.Read);
        Validate(package, version);
        using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        using var expected = zip.GetEntry("SyncShark.Explorer.dll")!.Open();
        var payload = Path.Combine(registered[0].InstalledLocation.Path, "SyncShark.Explorer.dll");
        using var actual = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (actual.Length is < 1 or > 1024 * 1024 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(expected), SHA256.HashData(actual)))
            throw new IOException("Windows registered different Explorer command bytes. The main menu is not verified.");
        actual.Position = 0;
        using var external = new FileStream(Path.Combine(directory, "SyncShark.Explorer.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(actual), SHA256.HashData(external)))
            throw new IOException("The protected external Explorer command differs from its package.");
        runtime.VerifyBinding();
    }

    internal static bool MatchesRegistration(Version expectedVersion, string expectedDirectory,
        Version actualVersion, string actualDirectory) => expectedVersion == actualVersion &&
        !string.IsNullOrWhiteSpace(actualDirectory) &&
        string.Equals(expectedDirectory.TrimEnd('\\'), actualDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    internal static void VerifyAbsent()
    {
        if (new PackageManager().FindPackagesForUser("").Any(package => package.Id.Name == Name))
            throw new IOException("The Explorer package is still registered. Removal has stopped.");
    }

    internal static void Validate(Stream stream, Version expectedVersion)
    {
        if (!stream.CanSeek || stream.Length > 4 * 1024 * 1024) throw new IOException("The Explorer identity package is not a bounded package.");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
            if (!KnownMembers.Contains(entry.FullName) || !seen.Add(entry.FullName) || entry.Length > 1024 * 1024)
                throw new IOException("Unexpected or duplicate Explorer package content.");
        if (!seen.Contains("AppxSignature.p7x")) throw new IOException("The Explorer package is unsigned.");
        // An indexed package declares the shell index or its artwork, so it must carry the
        // complete required set; a partial set is a stripped or malformed package, not a
        // legacy one. Legacy content stays admissible for the versions that predate it.
        if (IndexedShellMembers.Any(seen.Contains) && RequiredIndexedMembers.Any(name => !seen.Contains(name)))
            throw new IOException("The indexed Explorer package is missing a required shell resource.");
        if (zip.GetEntry("SyncShark.Explorer.dll") is not { Length: > 0 }) throw new IOException("The protected Explorer command is missing from the package.");
        var manifest = zip.GetEntry("AppxManifest.xml") ?? throw new IOException("The package manifest is missing.");
        using var input = manifest.Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
        var document = XDocument.Load(reader); XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var identity = document.Root?.Element(ns + "Identity");
        var application = document.Root?.Element(ns + "Applications")?.Elements(ns + "Application").SingleOrDefault();
        XNamespace com = "http://schemas.microsoft.com/appx/manifest/com/windows10";
        XNamespace uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
        var servers = application?.Descendants(com + "Extension")
            .Where(item => (string?)item.Attribute("Category") == "windows.comServer").ToArray();
        var classes = application?.Descendants(com + "Class").ToArray();
        if ((string?)identity?.Attribute("Name") != Name || (string?)identity.Attribute("Publisher") != Publisher ||
            !Version.TryParse((string?)identity.Attribute("Version"), out var version) || version != expectedVersion ||
            (string?)identity.Attribute("ProcessorArchitecture") != "x64" || (string?)application?.Attribute("Id") != "SyncShark" ||
            (string?)application.Attribute("Executable") != "SyncShark.exe" ||
            (string?)application.Attribute(uap10 + "RuntimeBehavior") != "win32App" ||
            servers is not { Length: 1 } ||
            (string?)servers[0].Attribute(uap10 + "RuntimeBehavior") != "packagedClassicApp" ||
            (string?)servers[0].Attribute(uap10 + "TrustLevel") != "mediumIL" ||
            classes is not { Length: 1 } || (string?)classes[0].Attribute("Id") != "B2251D9A-7803-4F1F-8FE8-CBDA284BD5E9" ||
            (string?)classes[0].Attribute("Path") != "SyncShark.Explorer.dll" || (string?)classes[0].Attribute("ThreadingModel") != "STA")
            throw new IOException("The Explorer package does not match this installed product version.");
    }
}
