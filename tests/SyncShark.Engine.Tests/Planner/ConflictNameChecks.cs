using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Tests.Planner;

/// <summary>Checks the source and collision number on the actual label.</summary>
public static class ConflictNameChecks
{
    public static Task RunAsync()
    {
        var settings = SettingsParser.Load();
        var at = DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(3));
        string Label(Side side, int ordinal) => ConflictName.For("folder/item.txt", side, at, "Station-A", settings, ordinal);
        Check.Require(Label(Side.Local, 1) == "folder/item [Local version Station-A 1 Jan 00.00].txt", "Local label missing observed PC name");
        Check.Require(Label(Side.Dropbox, 2) == "folder/item [Dropbox version 1 Jan 00.00] (2).txt", "Dropbox source or collision suffix changed");
        Check.Require(ConflictName.For("{side}.txt", Side.Local, at, "Station-A", settings, 1) == "{side} [Local version Station-A 1 Jan 00.00].txt",
            "Token-shaped basename must remain literal in the conflict label");
        return Task.CompletedTask;
    }
}
