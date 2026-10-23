using Massdex.SyncShark;

internal static class WindowsPlaceholderCompatibilityChecks
{
    internal static void Run()
    {
        // Do not call Expose() first: LastResult proves the test host's own entry point ran the
        // exposure before routing here. Calling it here would hide a missing startup call.
        var startup = WindowsPlaceholderCompatibility.LastResult;
        if (!OperatingSystem.IsWindows())
        {
            Require(startup.Status == PlaceholderCompatibilityStatus.NotWindows,
                "the test host's entry point ran the non-Windows no-op before this check");
            Require(!startup.IsExposed, "a non-Windows host is never reported as exposed");
            Console.WriteLine("PASS: Cloud Files placeholder exposure is a no-op without a native call on non-Windows.");
            return;
        }

        Require(startup.Status != PlaceholderCompatibilityStatus.NotAttempted,
            "the test host's entry point ran the exposure before this check");
        Require(startup.Status == PlaceholderCompatibilityStatus.Exposed && startup.IsExposed,
            "process startup exposes placeholder attributes before file I/O: " + startup.Describe());
        Require(startup.ConfirmedMode == WindowsPlaceholderCompatibility.ExposePlaceholders,
            "the native process mode is confirmed as PHCM_EXPOSE_PLACEHOLDERS");
        Require(startup.PreviousMode is >= WindowsPlaceholderCompatibility.ApplicationDefault
                and <= WindowsPlaceholderCompatibility.ExposePlaceholders,
            "the API reported a valid previous process mode: " + startup.PreviousMode);

        var second = WindowsPlaceholderCompatibility.Expose();
        Require(second.IsExposed &&
                second.ConfirmedMode == WindowsPlaceholderCompatibility.ExposePlaceholders,
            "repeated exposure is idempotent and still exposes placeholders");

        Console.WriteLine("PASS: Windows process placeholder compatibility is PHCM_EXPOSE_PLACEHOLDERS " +
            $"(previous mode {startup.PreviousMode}, confirmed mode {startup.ConfirmedMode}).");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("Windows placeholder compatibility: " + message);
    }
}
