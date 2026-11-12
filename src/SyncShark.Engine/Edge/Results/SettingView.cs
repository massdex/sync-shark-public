namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>One setting as listed by the CLI.</summary>
public sealed record SettingView(string Name, string Value, bool Overridden);
