using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine;

/// <summary>Names of the engine's data: embedded resources, and the files under the product folder
/// (&lt;ProductRoot&gt;/engine/state.db, logs/).</summary>
public static class ResourceNames
{
    /// <summary>Embedded resource name of the complete engine settings defaults.</summary>
    public const string Settings = "SyncShark.EngineSettings.json";
    /// <summary>Embedded resource name of the user-visible text templates.</summary>
    public const string Strings = "SyncShark.EngineStrings.json";
    // The csproj LogicalName for the frozen case mapping matches this value.
    public const string PathCase = "SyncShark.EnginePathCase.json";

    /// <summary>Product directory below the user’s local application data.</summary>
    public static readonly string ProductFolder=Path.Combine("Massdex","SyncShark");
    /// <summary>Absolute engine database path under this installation’s local application data.</summary>
    public static string DatabasePath(string localAppData) => Path.Combine(localAppData,ProductFolder,EngineFolder,DatabaseFile);
    /// <summary>Absolute log directory under the same product root.</summary>
    public static string LogDirectory(string localAppData) => Path.Combine(localAppData,ProductFolder,LogsFolder);
    /// <summary>Host executable filename.</summary>
    public const string HostExecutable = "Sync Shark.exe";
    public const string CliExecutable = "syncshark.exe";

    /// <summary>Exact version-relative files shared by packaging, setup and uninstall.</summary>
    public static IReadOnlyList<string> InstalledFiles { get; } = Array.AsReadOnly(new string[]
    {
        HostExecutable,
        "Sync Shark.dll",
        "Sync Shark.deps.json",
        "Sync Shark.runtimeconfig.json",
        CliExecutable,
        "syncshark.dll",
        "syncshark.deps.json",
        "syncshark.runtimeconfig.json",
        "SyncShark.Engine.dll",
        "Microsoft.Data.Sqlite.dll",
        "SQLitePCLRaw.batteries_v2.dll",
        "SQLitePCLRaw.core.dll",
        "SQLitePCLRaw.provider.e_sqlite3.dll",
        "e_sqlite3.dll",
        "tools/SyncShark.Uninstall.ps1",
        "tools/SyncShark.Registration.psm1",
        "tools/SetupFileSystem.cs",
        "tools/SetupMessages.cs",
        "tools/setup-strings.xml",
    });

    public const string RootBinFolder = "Recycle Bin";
    public const string Cli = "SyncShark.EngineCli.json";
    public const string Routes = "SyncShark.DropboxRoutes.json";
    public const string EngineFolder = "engine";
    public const string DatabaseFile = "state.db";
    public const string LogsFolder = "logs";
    public const string LogExtension = ".log";
    public const string LogPattern = "*" + LogExtension;
    /// <summary>Separates the pair id from the run id in a run-log file name.</summary>
    public const string LogNameSeparator = "-";
    /// <summary>Guid format of pair and run ids: 32 hex digits (the same form as stage names).</summary>
    public const string IdFormat = "N";

    /// <summary>Length of an id in <see cref="IdFormat"/>; stage grammar reuses it.</summary>
    public const int IdLength = 32;

    /// <summary>A fresh pair, run or stage id in <see cref="IdFormat"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string NewId() => Guid.NewGuid().ToString(IdFormat);

    /// <summary>A typed transient scan token, minted by the common identity owner.</summary>
    public static Guid NewScanToken() => Guid.NewGuid();
}

/// <summary>The single loader of embedded JSON catalogs and filler of {name} templates (engine design 1.3).</summary>
internal static class CliCatalogResource
{
    private const char PlaceholderOpen = '{';
    private const char PlaceholderClose = '}';

    /// <summary>Parses an embedded JSON resource; a missing resource throws, naming it.</summary>
    public static JsonDocument Load(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(resourceName);
        return JsonDocument.Parse(stream);
    }

    /// <summary>Fills each {name} placeholder (ordinal) in one pass over the original template, so text inserted for
    /// one placeholder is never read as another.</summary>
    public static string Fill(string template, IReadOnlyList<(string Name, string Value)> values)
    {
        var filled = new StringBuilder(template.Length);
        for (var index = default(int); index < template.Length; index++)
        {
            if (template[index] == PlaceholderOpen)
            {
                var close = template.IndexOf(PlaceholderClose, index + 1);
                if (close > index && ValueFor(values, template.AsSpan(index + 1, close - index - 1)) is { } value)
                {
                    filled.Append(value);
                    index = close;
                    continue;
                }
            }
            filled.Append(template[index]);
        }
        return filled.ToString();
    }

    private static string? ValueFor(IReadOnlyList<(string Name, string Value)> values, ReadOnlySpan<char> name)
    {
        foreach (var (candidate, value) in values)
            if (name.SequenceEqual(candidate)) return value;
        return null;
    }
}
