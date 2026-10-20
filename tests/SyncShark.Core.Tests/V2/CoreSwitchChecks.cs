namespace Massdex.SyncShark.V2;

/// <summary>Checks the --core switch and the SYNCSHARK_CORE setting that select the v1 or v2 core.</summary>
internal static class CoreSwitchChecks
{
    public static void Run()
    {
        var v2 = CoreSwitch.Select(["--cli", "--core", "v2", "status"], null);
        Require(v2.UseV2 && v2.Error is null && v2.Args.SequenceEqual(["--cli", "status"]),
            "--cli --core v2 selects v2 and removes the switch");

        var v1 = CoreSwitch.Select(["--cli", "--core", "v1", "status"], null);
        Require(!v1.UseV2 && v1.Error is null && v1.Args.SequenceEqual(["--cli", "status"]),
            "--cli --core v1 selects v1 and removes the switch");

        var unknown = CoreSwitch.Select(["--cli", "--core", "v3", "status"], null);
        Require(!unknown.UseV2 && unknown.Error is not null, "an unknown core token is a usage error");

        var app = CoreSwitch.Select(["--core", "v2", "--background"], null);
        Require(app.UseV2 && app.Error is null && app.Args.SequenceEqual(["--background"]),
            "the app form --core v2 selects v2 and removes the switch");

        var appUnknown = CoreSwitch.Select(["--core", "v3"], null);
        Require(!appUnknown.UseV2 && appUnknown.Error is not null, "an unknown app-form core token is a usage error");

        var missing = CoreSwitch.Select(["--cli", "--core"], null);
        Require(missing.Error is not null, "a missing core token is a usage error");

        Require(CoreSwitch.Select(["--cli", "status"], "2").UseV2, "the setting 2 selects v2");
        Require(!CoreSwitch.Select(["--cli", "status"], "1").UseV2, "the setting 1 selects v1");
        Require(!CoreSwitch.Select(["--cli", "status"], null).UseV2, "no switch and no setting selects v1");
        Require(CoreSwitch.Select(["--cli", "status"], "x").Error is not null, "an unknown setting is a usage error");
        Require(CoreSwitch.Select(["--cli", "status"], "x").Args.SequenceEqual(["--cli", "status"]),
            "a usage error leaves the arguments untouched");

        Console.WriteLine("v2 core-switch checks passed.");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
