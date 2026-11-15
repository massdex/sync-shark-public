namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>Section 9.3: ResultSettingLine, or ResultSettingLineOverridden for a value the user changed.</summary>
public sealed record SettingsResult(IReadOnlyList<SettingView> Settings) : IEngineCliData
{
    /// <summary>One line per setting, marking user overrides.</summary>
    public string RenderHuman() => string.Join(Environment.NewLine, Settings.Select(setting =>
        Text.Get(setting.Overridden ? TextKey.ResultSettingLineOverridden : TextKey.ResultSettingLine,
            (TextArg.Name, setting.Name), (TextArg.Value, setting.Value))));

    public int ExitCode => (int)SyncCliExitCode.Success;
}
