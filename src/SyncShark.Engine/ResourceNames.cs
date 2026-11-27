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
    /// <summary>Embedded resource name of the SQL statements and shared fragments.</summary>
    public const string Sql = "SyncShark.EngineSql.json";
    // The csproj LogicalName for the frozen case mapping matches this value.
    public const string PathCase = "SyncShark.EnginePathCase.json";

    /// <summary>Product directory below the user’s local application data.</summary>
    public static readonly string ProductFolder=Path.Combine("Massdex","SyncShark");
    /// <summary>Absolute engine database path under this installation’s local application data.</summary>
    public static string DatabasePath(string localAppData) => Path.Combine(localAppData,ProductFolder,EngineFolder,DatabaseFile);
    /// <summary>Absolute log directory under the same product root.</summary>
    public static string LogDirectory(string localAppData) => Path.Combine(localAppData,ProductFolder,LogsFolder);
    /// <summary>Host assembly name, the stem of the host's installed files.</summary>
    public const string HostAssembly = "Sync Shark";
    /// <summary>Host executable filename.</summary>
    public const string HostExecutable = HostAssembly + ".exe";
    /// <summary>Launch argument asking the host to report readiness to its starter.</summary>
    public const string HostReadyArgument = "--notify-ready";
    public const string CliExecutable = "syncshark.exe";

    /// <summary>Exact version-relative files shared by packaging, setup and uninstall.</summary>
    public static IReadOnlyList<string> InstalledFiles { get; } = Array.AsReadOnly(new string[]
    {
        HostExecutable,
        HostAssembly + ".dll",
        HostAssembly + ".deps.json",
        HostAssembly + ".runtimeconfig.json",
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
    /// <summary>The environment variable that turns console colour off when present and not empty (no-color.org).</summary>
    public const string NoColorVariable = "NO_COLOR";
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
