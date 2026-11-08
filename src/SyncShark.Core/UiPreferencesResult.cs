using System.Text.Json;

namespace Massdex.SyncShark;

/// <summary>The saved preferences after a completed read or a verified committed save.</summary>
public sealed record UiPreferencesResult(UiPreferences Preferences) : IEngineCliData
{
    /// <summary>One name and value line per saved preference that has a value.</summary>
    public string RenderHuman() => string.Join(Environment.NewLine,
        JsonSerializer.SerializeToElement(Preferences, UiPreferencesUpdate.WireOptions).EnumerateObject()
            .Select(member => $"{member.Name}: {(member.Value.ValueKind == JsonValueKind.String ? member.Value.GetString() : member.Value.GetRawText())}"));

    public int ExitCode => (int)SyncCliExitCode.Success;
}
