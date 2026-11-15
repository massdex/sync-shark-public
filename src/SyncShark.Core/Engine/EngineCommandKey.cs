namespace Massdex.SyncShark;

/// <summary>Stable keys of the engine commands; engine-cli.json names each exactly once (engine design 1.3).</summary>
public enum EngineCommandKey
{
    PairsList, PairsAdd, PairsRemove, PairsEnable, PairsDisable, PairsExclude, PairsInclude, Repair,
    Logs,
    Status, Issues, Resolve, SettingsList, SettingsSet, PreferencesShow, PreferencesSet,
    PairsBind, HostStart, HostStatus, HostPause, HostResume, HostStop, Recovery, RecoveryResolve,
    IgnoresList, IgnoresSet
}
