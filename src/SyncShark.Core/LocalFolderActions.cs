using System.IO;

namespace Massdex.SyncShark;

/// <summary>
/// The one shared local Create Folder action. The pair editor and the CLI both call this method, so the
/// parent rules, the name rules and the exclusive creation cannot drift apart.
///
/// It creates exactly one ordinary folder inside an existing folder on this computer. It never overwrites
/// or removes anything, never retries and never changes pair, account, provider or sync state. The parent
/// folder identity is pinned for the whole effect through the existing <see cref="PilotDirectoryLease"/>,
/// and the child is created with the lease's exclusive native creation rather than by checking and then
/// creating.
/// </summary>
public static class LocalFolderActions
{
    /// <summary>One plain folder name: no separators, no invalid or reserved character and no trailing space or dot.</summary>
    public const string NameRule =
        "Enter one plain folder name of at most 255 characters: no path separators, no colon or other " +
        "character Windows forbids, not a reserved name such as CON or PRN, and no trailing space or dot.";

    private const string ParentRule =
        "Choose an existing folder on this computer, such as C:\\Users\\You\\Documents. Device names, " +
        "network paths and relative paths are not supported.";

    /// <summary>
    /// Creates the folder <paramref name="name"/> inside the existing folder <paramref name="parentPath"/> and
    /// returns the exact created path. An existing file or folder with that name is refused without being
    /// replaced, and cancellation is observed before the effect can start.
    /// </summary>
    public static string Create(string parentPath, string name, CancellationToken cancellationToken = default)
    {
        var safeName = RequireExactFolderName(name);
        var parent = RequireExistingLocalParent(parentPath);
        // Cancellation is checked while nothing has been created yet.
        cancellationToken.ThrowIfCancellationRequested();
        using var parentLease = PilotDirectoryLease.Open(parent);
        var created = Path.Combine(parentLease.Path, safeName);
        if (File.Exists(created) || Directory.Exists(created))
            throw new IOException("'" + safeName + "' already exists in " + parentLease.Path +
                                  ". Nothing was created and nothing was replaced.");
        string createdPath;
        try
        {
            using var childLease = PilotDirectoryLease.CreateChild(parentLease, safeName, hidden: false);
            createdPath = childLease.Path;
        }
        catch (IOException error)
        {
            throw new IOException("Creation of '" + safeName + "' in " + parentLease.Path +
                                  " could not be confirmed. No existing item was replaced. Check that exact " +
                                  "location before trying again; a folder may have been created before verification failed.", error);
        }
        if (!string.Equals(createdPath, created, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(createdPath))
            throw new IOException("The new folder was not found at its exact requested path: " + created);
        return createdPath;
    }

    /// <summary>
    /// Returns the exact single ordinary folder name, or throws <see cref="ArgumentException"/>. The returned
    /// value is always identical to the requested name: a name that Windows normalization would silently
    /// alter (a leading or trailing separator, a repeated separator, a trailing space or dot, a reserved
    /// device name, an invalid character, an empty name, '.' or '..') is refused instead.
    /// </summary>
    public static string RequireExactFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255)
            throw new ArgumentException(NameRule);
        string normalized;
        try
        {
            normalized = PilotPath.NormalizeRelative(name);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException(NameRule);
        }
        if (normalized.Contains('/') || !string.Equals(normalized, name, StringComparison.Ordinal))
            throw new ArgumentException(NameRule);
        return normalized;
    }

    /// <summary>
    /// Returns the full path of an existing ordinary absolute folder on this computer, or throws. Device,
    /// UNC, relative and volume-root paths are refused, and the existing <see cref="PilotPath"/> product-state
    /// rule decides whether the folder can be used at all. Unsupported reparse or alias parents are refused
    /// by the pinned lease that <see cref="Create"/> opens.
    /// </summary>
    public static string RequireExistingLocalParent(string? parentPath)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
            throw new ArgumentException(ParentRule);
        if (!Path.IsPathFullyQualified(parentPath) || parentPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException(ParentRule);
        string full;
        try
        {
            full = Path.GetFullPath(parentPath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("That folder path is not valid Windows syntax: " + parentPath, error);
        }
        var root = Path.GetPathRoot(full) ?? string.Empty;
        if (full.Length == root.Length)
            throw new ArgumentException("Choose a folder below the drive root: " + full);
        try
        {
            PilotPath.RequireSeparatedFromProductState(full);
        }
        catch (InvalidDataException error)
        {
            throw new ArgumentException(error.Message + " Nothing was created.", error);
        }
        if (File.Exists(full))
            throw new DirectoryNotFoundException(full + " is a file, not a folder. Nothing was created.");
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException(full + " is not an existing folder. Nothing was created.");
        return full;
    }
}
