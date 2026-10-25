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

    /// <summary>The tray branch's routing decision, made after the core switch removed its own arguments.</summary>
    internal enum Route { NotTray, Start, Invalid }

    /// <summary>The one tray-request predicate the startup branch uses, after the core switch removed its own args.</summary>
    internal static bool IsTrayRequest(IReadOnlyList<string> args) =>
        args.Any(value => string.Equals(value, "--tray", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The shared tray predicate's routing decision, used by the application's startup helper: a <c>--tray</c>
    /// left after the core switch is a start only when core v2 was selected and no other argument remains. This
    /// covers the predicate and its gate; it neither owns nor asserts the application's branch order.
    /// </summary>
    internal static Route Decide(bool useCoreV2, IReadOnlyList<string> args) =>
        !IsTrayRequest(args) ? Route.NotTray
        : StartsTray(useCoreV2, args) ? Route.Start
        : Route.Invalid;

    /// <summary>Whether these already-switched arguments are exactly the core v2 tray request.</summary>
    internal static bool StartsTray(bool useCoreV2, IReadOnlyList<string> args) =>
        useCoreV2 && args.Count == 1 && string.Equals(args[0], "--tray", StringComparison.OrdinalIgnoreCase);

    internal static Result Start(bool useCoreV2, IReadOnlyList<string> args, TrayHostSend send, Action exitTray,
        Action<string> diagnostic)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(exitTray);
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (!StartsTray(useCoreV2, args))
            return new Result(null, null, 2, "The background tray attaches to the core v2 AutoSync host.");
        var ownership = TrayClient.TryTakeOwnership();
        if (ownership is null)
        {
            const string duplicate = "Another tray icon already owns this user and session.";
            diagnostic(duplicate);
            return new Result(null, null, 0, duplicate);
        }
        try
        {
            // A full window owns the tray role. That observation is native synchronous work, so the client
            // performs it off the dispatcher immediately at startup instead of blocking this path.
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
