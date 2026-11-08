using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine;

/// <summary>Names of the engine's data: embedded resources, and the files under the product folder
/// (&lt;ProductRoot&gt;/engine/state.db, settings.json, logs/).</summary>
public static class ResourceNames
{
    /// <summary>Embedded resource name of the complete engine settings defaults.</summary>
    public const string Settings = "SyncShark.EngineSettings.json";
    /// <summary>Embedded resource name of the user-visible text templates.</summary>
    public const string Strings = "SyncShark.EngineStrings.json";
    // The csproj LogicalName for the frozen case mapping matches this value.
    public const string PathCase = "SyncShark.EnginePathCase.json";

    /// <summary>Folder under the product root holding the engine database and logs.</summary>
    public const string EngineFolder = "engine";
    public const string DatabaseFile = "state.db";
    /// <summary>File name of the user's partial settings overlay.</summary>
    public const string UserSettingsFile = "settings.json";
    /// <summary>Owned replacement written and renamed into place so a settings publication is old-or-new.</summary>
    public const string UserSettingsReplacementFile = UserSettingsFile + ".replacement";
    public const string LogsFolder = "logs";
    public const string LogExtension = ".log";
    public const string LogPattern = "*" + LogExtension;
    /// <summary>Separates the pair id from the run id in a run-log file name.</summary>
    public const string LogNameSeparator = "-";
    /// <summary>Read chunk for the read-only log view; independent of the writer's settings buffer.</summary>
    public const int LogReadBufferCharacters = 4096;

    /// <summary>Guid format of pair and run ids: 32 hex digits (the same form as stage names).</summary>
    public const string IdFormat = "N";

    /// <summary>Length of an id in <see cref="IdFormat"/>; stage grammar reuses it.</summary>
    public const int IdLength = 32;

    /// <summary>A fresh pair, run or stage id in <see cref="IdFormat"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string NewId() => Guid.NewGuid().ToString(IdFormat);

    /// <summary>A typed transient attempt identity, minted by the common identity owner.</summary>
    public static Guid NewAttemptId() => Guid.NewGuid();

    /// <summary>A typed transient scan token, minted by the common identity owner.</summary>
    public static Guid NewScanToken() => Guid.NewGuid();
}
