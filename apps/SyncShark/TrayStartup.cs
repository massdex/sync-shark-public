namespace Massdex.SyncShark;

/// <summary>
/// The one background-tray start decision, called by the application's <c>--core v2 --tray</c> startup branch
/// and drivable directly by its fixture. Like the client it starts, it never opens the store, constructs an
/// engine or takes the product lock.
/// </summary>
internal static class TrayStartup
{
    /// <summary><see cref="ExitCode"/> null means the client is running; otherwise the caller must shut down.</summary>
    internal sealed record Result(IDisposable? Ownership, TrayClient? Client, int? ExitCode, string Detail);

    /// <summary>The one tray-request predicate the startup branch uses, after the core switch removed its own args.</summary>
    internal static bool IsTrayRequest(IReadOnlyList<string> args) =>
        args.Any(value => string.Equals(value, "--tray", StringComparison.OrdinalIgnoreCase));

    internal static Result Start(bool useCoreV2, IReadOnlyList<string> args, TrayHostSend send, Action exitTray,
        Action<string> diagnostic)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(exitTray);
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (!useCoreV2 || args.Count != 1 || !string.Equals(args[0], "--tray", StringComparison.OrdinalIgnoreCase))
            return new Result(null, null, 2, "The background tray attaches to the core v2 AutoSync host.");
        var ownership = TrayClient.TryTakeOwnership();
        if (ownership is null)
        {
            const string duplicate = "Another tray icon already owns this user and session.";
            diagnostic(duplicate);
            return new Result(null, null, 0, duplicate);
        }
        if (ExistingWindowActivation.IsProductWindowPresent())
        {
            ownership.Dispose();
            const string standingDown = "A full Sync Shark window owns the tray; this client is standing down.";
            diagnostic(standingDown);
            return new Result(null, null, 0, standingDown);
        }
        try
        {
            var client = new TrayClient(send, exitTray, diagnostic);
            client.Start();
            const string attached = "Tray client attached; it never opens the store or takes the product lock.";
            diagnostic(attached);
            return new Result(ownership, client, null, attached);
        }
        catch (Exception error)
        {
            ownership.Dispose();
            var detail = "Tray client could not start: " + error.Message;
            diagnostic(detail);
            return new Result(null, null, 1, detail);
        }
    }
}
