using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using System.Windows.Threading;
using Massdex.SyncShark.V2;
using static Massdex.SyncShark.TrayJson;

namespace Massdex.SyncShark;

/// <summary>
/// A pure client of the one background host. It never opens the store, constructs an engine or takes the
/// product lock, so the host keeps syncing when the tray exits. The data it returns and its own waits are
/// bounded; the host traversal behind a read is not necessarily row-bounded. Every action is sent once,
/// never replayed.
/// </summary>
internal sealed class TrayClient : IDisposable
{
    private const int ConflictLimit = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StatusDeadline = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActionDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConflictCacheLifetime = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions HostJson = new(JsonSerializerDefaults.Web);

    private readonly TrayHostSend _send;
    private readonly Action _exitTray;
    private readonly Action<string> _diagnostic;
    private readonly DispatcherTimer _timer;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _baseIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _hostItem;
    private readonly ToolStripSeparator _pairsSeparator;
    private readonly ToolStripMenuItem _waitingItem;
    private readonly ToolStripMenuItem _conflictsItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _resumeItem;
    private readonly List<ToolStripMenuItem> _pairItems = [];
    private readonly List<ToolStripMenuItem> _syncNowItems = [];
    private readonly List<ToolStripMenuItem> _conflictRows = [];
    // One outstanding activation per action: a second click while one is in flight does nothing, and the
    // action's own menu item stays disabled until its single reply (or timeout) has been handled.
    private readonly HashSet<string> _pendingActions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ToolStripMenuItem>> _actionItems = new(StringComparer.Ordinal);
    private TrayStatusSnapshot? _lastSnapshot;
    private TrayStatusView _view = TrayStatusView.Starting;
    private TrayConflictList? _conflictList;
    private DateTimeOffset _conflictsFetchedUtc;
    private string _pairSignature = string.Empty;
    private string _iconState = string.Empty;
    private Icon? _stateIcon;
    private bool _refreshing;
    private bool _conflictsLoading;
    private bool _observingWindow;
    private bool _pendingPairs;
    private int _pollCount;
    private bool _disposed;
    private Task _initialPoll = Task.CompletedTask;
    private Task _conflictsLoad = Task.CompletedTask;
    // The last logged failure kind per read, so a diagnostic is written on a transition only, never per poll.
    private readonly Dictionary<string, string?> _readFailures = new(StringComparer.Ordinal);

    private sealed record TrayConflictList(int Total, IReadOnlyList<string> Rows);

    internal TrayClient(TrayHostSend send, Action exitTray, Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(exitTray);
        _send = send;
        _exitTray = exitTray;
        _diagnostic = diagnostic ?? (message => App.WriteStartupProgress("tray", message));
        _baseIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ??
            throw new InvalidOperationException("The executable path is unavailable.")) ?? (Icon)SystemIcons.Application.Clone();
        _menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem(TrayStatusView.Starting.Status) { Enabled = false };
        _hostItem = new ToolStripMenuItem(TrayStatusView.Starting.HostLine) { Enabled = false };
        _pairsSeparator = new ToolStripSeparator { Visible = false };
        _waitingItem = new ToolStripMenuItem("Work waiting: 0 (planning backlog 0)") { Enabled = false };
        _conflictsItem = new ToolStripMenuItem("Conflicts: checking…");
        _pauseItem = new ToolStripMenuItem("Pause Sync");
        foreach (var option in PauseOption.All)
        {
            var item = new ToolStripMenuItem(option.Label, null,
                (_, _) => _ = SendMutationAsync(PauseRequest(option), "Pause " + option.Label))
            { ToolTipText = option.Detail };
            RegisterAction(MutationKey(PauseRequest(option)), item);
            _pauseItem.DropDownItems.Add(item);
        }
        _pauseItem.DropDownItems.Add(new ToolStripSeparator());
        _resumeItem = new ToolStripMenuItem("Resume Sync", null, (_, _) => _ = SendMutationAsync(ResumeRequest(), "Resume"));
        RegisterAction(MutationKey(ResumeRequest()), _resumeItem);
        _pauseItem.DropDownItems.Add(_resumeItem);
        _menu.Items.AddRange([
            _statusItem, _hostItem, _pairsSeparator, _waitingItem, _conflictsItem,
            new ToolStripSeparator(), _pauseItem, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit tray", null, (_, _) => _exitTray())
        ]);
        RebuildConflictRows(null);
        _icon = new NotifyIcon
        {
            Icon = _baseIcon,
            Text = App.FormatTrayTooltip("Sync state: " + TrayStatusView.Starting.Status),
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left) _menu.Show(Cursor.Position);
        };
        _menu.Opening += (_, _) => OnMenuOpening();
        _menu.Closed += (_, _) =>
        {
            if (_pendingPairs) { _pendingPairs = false; RebuildPairs(_view.Pairs); }
        };
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        Apply(TrayStatusView.Starting);
    }

    /// <summary>The current derived view; exposed so the tray's decisions are testable without a host.</summary>
    internal TrayStatusView View => _view;

    /// <summary>The last balloon shown; exposed so a failed forwarded command's diagnostic is observable.</summary>
    internal (string Title, string Message, bool Warning)? LastNotification { get; private set; }

    /// <summary>The conflicts menu line, so the capped "99+" wording is observable.</summary>
    internal string ConflictStatusText => _conflictsItem.Text ?? string.Empty;

    /// <summary>The rendered conflict rows, so the menu text loaded at open time is observable.</summary>
    internal IReadOnlyList<string> ConflictRowsForTest =>
        _conflictRows.Select(item => item.Text ?? string.Empty).ToArray();

    /// <summary>The task of the first poll Start began, so a caller can await observable completion.</summary>
    internal Task InitialPoll => _initialPoll;

    /// <summary>The last menu-open conflict load, so a caller can await it instead of guessing a delay.</summary>
    internal Task ConflictsLoad => _conflictsLoad;

    /// <summary>Raises the production menu-open path without showing a window, for the focused fixture checks.</summary>
    internal void OpenMenuForTest() => OnMenuOpening();

    // The one menu-open path: deferred pair rows first, then a conflict read only when the cache is stale.
    private void OnMenuOpening()
    {
        if (_pendingPairs) { _pendingPairs = false; RebuildPairs(_view.Pairs); }
        if (DateTimeOffset.UtcNow - _conflictsFetchedUtc > ConflictCacheLifetime) _conflictsLoad = LoadConflictsAsync();
    }

    /// <summary>The tray's own single-instance owner. A null result means another tray already owns the icon.</summary>
    internal static IDisposable? TryTakeOwnership()
    {
        var mutex = new Mutex(false, @"Local\" + AutoSyncHostProtocol.PipeName(SyncSharkRuntimePaths.InstanceLockFile) + ".tray");
        try
        {
            if (mutex.WaitOne(0)) return new Ownership(mutex);
        }
        catch (AbandonedMutexException) { return new Ownership(mutex); }
        catch { mutex.Dispose(); throw; }
        mutex.Dispose();
        return null;
    }

    internal void Start()
    {
        _timer.Start();
        _initialPoll = RefreshStatusAsync();
        // A full window owns the tray role. The observation is synchronous native work, so it starts
        // immediately off the dispatcher; finding a window stands this client down without blocking startup.
        _ = ObserveProductWindowAsync();
    }

    /// <summary>Runs one poll now; the fixture awaits this instead of waiting for the dispatch timer.</summary>
    internal Task RefreshNowAsync() => RefreshStatusAsync();

    /// <summary>Stops the dispatch poll so a fixture can drive each poll explicitly without a timing race.</summary>
    internal void StopPollingForTest() => _timer.Stop();

    /// <summary>Sends one action through the same path a menu item uses, for the focused fixture checks.</summary>
    internal Task SendActionForTestAsync(AutoSyncHostRequest request, string label) => SendMutationAsync(request, label);

    /// <summary>Invokes the tray's own exit action; it never sends a host command.</summary>
    internal void ExitTrayForTest() => _exitTray();

    /// <summary>Whether the given action is currently outstanding (its item is disabled).</summary>
    internal bool IsActionPending(string key) => _pendingActions.Contains(key);

    /// <summary>Whether every menu item for the action is enabled.</summary>
    internal bool ActionEnabledForTest(string key) =>
        _actionItems.TryGetValue(key, out var items) && items.Count > 0 && items.All(item => item.Enabled);

    /// <summary>The stable identity of one action, so a repeated activation is recognised and ignored.</summary>
    internal static string MutationKey(AutoSyncHostRequest request) => request.Command + "\u001f" +
        (request.PairId ?? string.Empty) + "\u001f" + string.Join('\u001e', request.CliArgs ?? []);

    private async Task RefreshStatusAsync()
    {
        if (_refreshing || _disposed) return;
        _refreshing = true;
        try
        {
            var poll = _pollCount++;
            TrayStatusSnapshot? snapshot = null;
            string? failure = null;
            var detail = string.Empty;
            try
            {
                using var deadline = new CancellationTokenSource(StatusDeadline);
                var reply = await _send(AutoSyncHostClient.Request("status"), TimeSpan.FromMilliseconds(300),
                    deadline.Token);
                if (reply is { Ok: false })
                {
                    failure = "rejected";
                    detail = reply.Error?.Message ?? "the host refused the status read";
                }
                else snapshot = TrayStatusView.Parse(reply, _lastSnapshot?.OpenConflicts);
                if (snapshot is null && failure is null)
                {
                    failure = "unavailable";
                    detail = "the host did not answer";
                }
            }
            catch (Exception error)
            {
                // Every failed observation — I/O, peer verification, parse, timeout or cancellation — is a
                // miss, so the tray keeps the last known view and says Updating… with actions disabled.
                failure = FailureKind(error);
                detail = error.GetType().Name + ": " + error.Message;
            }
            NoteRead("status read", failure, detail);
            if (_disposed) return;
            if (snapshot is not null) _lastSnapshot = snapshot;
            Apply(TrayStatusView.Derive(_lastSnapshot, _lastSnapshot is not null && snapshot is null, poll % 2 == 1));
            // Every fifth completed poll, matching the previous cadence; never twice at once.
            if (poll % 5 == 4) await ObserveProductWindowAsync();
        }
        catch (Exception error)
        {
            // A display-only update must never terminate the client; the next poll retries the same read.
            _diagnostic("tray refresh failed: " + error.GetType().Name + ": " + error.Message);
        }
        finally { _refreshing = false; }
    }

    // Window and module inspection is synchronous and can stall; it runs off the UI dispatcher and only its
    // result is marshalled back. One observation at a time, and it never activates anything.
    private async Task ObserveProductWindowAsync()
    {
        if (_observingWindow || _disposed) return;
        _observingWindow = true;
        try
        {
            var present = await Task.Run(ExistingWindowActivation.IsProductWindowPresent).ConfigureAwait(true);
            if (!_disposed && present) _exitTray();
        }
        catch (Exception error)
        {
            _diagnostic("tray window observation failed: " + error.GetType().Name + ": " + error.Message);
        }
        finally { _observingWindow = false; }
    }

    private void Apply(TrayStatusView view)
    {
        if (_disposed) return;
        _view = view;
        // The icon is labelled as sync state, never as the full app's richer attention meaning.
        _statusItem.Text = "Sync state: " + view.Status;
        _hostItem.Text = view.HostLine;
        _waitingItem.Text = "Work waiting: " + view.UrgentPending.ToString(CultureInfo.InvariantCulture) +
            " (planning backlog " + view.PlanningBacklog.ToString(CultureInfo.InvariantCulture) + ")";
        UpdateConflictText(view.OpenConflicts);
        SetIcon(view.IconState);
        _icon.Text = App.FormatTrayTooltip("Sync state: " + view.Status);
        // No mutation is offered while the host is unreachable, starting, stopping or already pending.
        UpdateActionEnabled();
        var signature = PairSignature(view.Pairs);
        if (signature == _pairSignature) return;
        _pairSignature = signature;
        if (_menu.Visible) _pendingPairs = true;
        else RebuildPairs(view.Pairs);
    }

    private void SetIcon(string state)
    {
        if (state == _iconState && _stateIcon is not null) return;
        var previous = _stateIcon;
        var icon = TrayStatusIcons.Create(_baseIcon, state);
        _stateIcon = icon;
        _icon.Icon = icon;
        _iconState = state;
        previous?.Dispose();
    }

    private static string PairSignature(IReadOnlyList<TrayPairView> pairs) => string.Join('\u001f',
        pairs.Select(pair => string.Join('\u001e', pair.PairId, pair.StateLine, pair.LastResultLine, pair.WaitingLine)));

    private void RebuildPairs(IReadOnlyList<TrayPairView> pairs)
    {
        // Unregister the children before their parent rows dispose them.
        foreach (var item in _syncNowItems) UnregisterAction(item);
        foreach (var item in _pairItems) { _menu.Items.Remove(item); item.Dispose(); }
        _pairItems.Clear();
        _syncNowItems.Clear();
        _pairsSeparator.Visible = pairs.Count > 0;
        var index = _menu.Items.IndexOf(_pairsSeparator) + 1;
        foreach (var pair in pairs)
        {
            var item = new ToolStripMenuItem(pair.Name + " — " + pair.StateLine);
            item.DropDownItems.Add(ReadOnly(pair.LastResultLine));
            item.DropDownItems.Add(ReadOnly(pair.WaitingLine));
            item.DropDownItems.Add(new ToolStripSeparator());
            var request = AutoSyncHostClient.Request("now", pair.PairId);
            var syncNow = new ToolStripMenuItem("Sync now", null, (_, _) => _ = SendMutationAsync(request, "Sync now"));
            RegisterAction(MutationKey(request), syncNow);
            item.DropDownItems.Add(syncNow);
            _syncNowItems.Add(syncNow);
            _menu.Items.Insert(index++, item);
            _pairItems.Add(item);
        }
    }

    private static ToolStripMenuItem ReadOnly(string text) => new(text) { Enabled = false };

    private void RegisterAction(string key, ToolStripMenuItem item)
    {
        item.Tag = key;
        if (!_actionItems.TryGetValue(key, out var items)) _actionItems[key] = items = [];
        items.Add(item);
        item.Enabled = _view.CanAct && !_pendingActions.Contains(key);
    }

    private void UnregisterAction(ToolStripMenuItem item)
    {
        if (item.Tag is not string key || !_actionItems.TryGetValue(key, out var items)) return;
        items.Remove(item);
        if (items.Count == 0) _actionItems.Remove(key);
    }

    private void UpdateActionEnabled()
    {
        if (_disposed) return;
        // The parent submenu stays closed to an unreachable/starting/stopping host; each leaf additionally
        // carries its own pending state.
        _pauseItem.Enabled = _view.CanAct;
        foreach (var (key, items) in _actionItems)
        {
            var enabled = _view.CanAct && !_pendingActions.Contains(key);
            foreach (var item in items) item.Enabled = enabled;
        }
    }

    private void UpdateConflictText(int? statusCount)
    {
        var fresh = _conflictList is not null && DateTimeOffset.UtcNow - _conflictsFetchedUtc <= ConflictCacheLifetime;
        var total = fresh ? _conflictList!.Total : statusCount;
        _conflictsItem.Text = total is null ? "Conflicts: not yet known" : total == 0 ? "Conflicts: none open"
            : "Conflicts: " + TrayStatusView.ConflictCountShort(total.GetValueOrDefault()) + " open" +
              (fresh ? string.Empty : " — open the menu to load the first " + ConflictLimit);
    }

    private async Task LoadConflictsAsync()
    {
        if (_conflictsLoading || _disposed) return;
        _conflictsLoading = true;
        string? failure = null;
        var detail = string.Empty;
        try
        {
            using var deadline = new CancellationTokenSource(StatusDeadline);
            var reply = await _send(AutoSyncHostClient.Request("cli") with
            {
                CliArgs = ["--cli", "conflicts", "list", "--limit",
                    ConflictLimit.ToString(CultureInfo.InvariantCulture), "--json"]
            }, TimeSpan.FromMilliseconds(300), deadline.Token);
            if (reply is null)
            {
                failure = "unavailable";
                detail = "the host did not answer";
            }
            else if (!reply.Ok)
            {
                failure = "rejected";
                detail = reply.Error?.Message ?? "the host refused the conflict list";
            }
            else if (reply.Data is not { } element || element.ValueKind != JsonValueKind.Object)
            {
                failure = "rejected";
                detail = "the host reply carried no conflict list";
            }
            else if (Int(element, "exitCode") is not 0)
            {
                failure = "rejected";
                // The forwarded command's own stderr explains the refusal; the exit code alone does not.
                var stderr = Text(element, "stderr");
                detail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                    : "conflicts list exited with code " +
                      Int(element, "exitCode").ToString(CultureInfo.InvariantCulture);
            }
            else if (Text(element, "stdout") is not { Length: > 0 } output)
            {
                failure = "rejected";
                detail = "the conflict list was empty";
            }
            else if (!TryParseConflicts(output, out var list))
            {
                failure = "rejected";
                detail = "the conflict list could not be read";
            }
            else if (!_disposed)
            {
                _conflictList = list;
                _conflictsFetchedUtc = DateTimeOffset.UtcNow;
                UpdateConflictText(_view.OpenConflicts);
                RebuildConflictRows(list);
            }
        }
        catch (Exception error)
        {
            // The list is display-only and reloaded next time the menu opens; the status count stays authoritative.
            failure = FailureKind(error);
            detail = error.GetType().Name + ": " + error.Message;
        }
        finally { _conflictsLoading = false; }
        if (!_disposed) NoteRead("conflict list read", failure, detail);
    }

    // The distinct failure kinds the tray reports: uncertain (sent, no reply), timed out, rejected (the host
    // refused) and unavailable (nothing answered or the answer could not be read).
    private static string FailureKind(Exception error) => error switch
    {
        AutoSyncHostUncertainException => "uncertain",
        OperationCanceledException => "timed out",
        _ => "unavailable"
    };

    // One diagnostic per transition, never one per failed poll, so a healthy or sustained failure is quiet.
    private void NoteRead(string read, string? failure, string detail)
    {
        _readFailures.TryGetValue(read, out var last);
        if (string.Equals(failure, last, StringComparison.Ordinal)) return;
        if (failure is null) _diagnostic("tray " + read + " recovered after " + last + ".");
        else _diagnostic("tray " + read + " failed (" + failure + "): " + detail);
        _readFailures[read] = failure;
    }

    private static bool TryParseConflicts(string stdout, out TrayConflictList list)
    {
        list = new TrayConflictList(0, []);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !Bool(root, "ok") ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return false;
        var total = Int(data, "totalOpen");
        var rows = new List<string>();
        if (data.TryGetProperty("freeSpacePending", out var free) && free.ValueKind == JsonValueKind.Array)
            foreach (var item in free.EnumerateArray())
                if (Text(item, "detail") is { Length: > 0 } detail) rows.Add(detail);
        if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
                rows.Add(ConflictRow(Text(item, "relativePath"), Text(item, "group")));
        list = new TrayConflictList(total, rows.Take(ConflictLimit).ToArray());
        return true;
    }

    private static string ConflictRow(string? path, string? group)
    {
        var label = string.IsNullOrEmpty(path) ? "(no path)" : path;
        return string.IsNullOrEmpty(group) ? label : label + " — " + group;
    }

    private void RebuildConflictRows(TrayConflictList? list)
    {
        foreach (var item in _conflictRows) { _conflictsItem.DropDownItems.Remove(item); item.Dispose(); }
        _conflictRows.Clear();
        if (list is null)
        {
            AddConflictRow("Opening the menu loads the first " + ConflictLimit + ".");
            return;
        }
        if (list.Rows.Count == 0)
        {
            AddConflictRow("No open conflict rows.");
            return;
        }
        foreach (var row in list.Rows) AddConflictRow(row);
    }

    private void AddConflictRow(string text)
    {
        var item = ReadOnly(text);
        _conflictsItem.DropDownItems.Add(item);
        _conflictRows.Add(item);
    }

    private async Task SendMutationAsync(AutoSyncHostRequest request, string label)
    {
        if (_disposed) return;
        var key = MutationKey(request);
        // One activation per action: while this one is outstanding a repeat click does nothing at all.
        if (!_pendingActions.Add(key)) return;
        UpdateActionEnabled();
        try
        {
            using var deadline = new CancellationTokenSource(ActionDeadline);
            try
            {
                var reply = await _send(request, TimeSpan.FromMilliseconds(300), deadline.Token);
                if (reply is null)
                {
                    _diagnostic("tray action '" + label + "' not sent: background sync is not reachable");
                    Notify(label + " was not sent", "Background sync is not reachable. Nothing was replayed.", true);
                }
                else if (!reply.Ok)
                {
                    var message = reply.Error?.Message ?? "The host refused the command; nothing was replayed.";
                    _diagnostic("tray action '" + label + "' rejected: " + message);
                    Notify(label + " was rejected", message, true);
                }
                else if (RejectedForwardedCli(request, reply, out var diagnostic))
                {
                    _diagnostic("tray action '" + label + "' rejected: " + diagnostic);
                    Notify(label + " was rejected", diagnostic, true);
                }
                else _diagnostic("tray action '" + label + "' accepted");
            }
            catch (AutoSyncHostUncertainException)
            {
                // Sent, but the reply did not arrive: a distinct outcome from a rejection, never replayed.
                _diagnostic("tray action '" + label + "' uncertain: the reply did not arrive; nothing was replayed");
                Notify(label + " is uncertain", "The host may have accepted the command, but the reply did not " +
                    "arrive. The host keeps admitted work; this tray never replays it.", true);
            }
            catch (OperationCanceledException)
            {
                // The deadline expired before the host accepted the command.
                _diagnostic("tray action '" + label + "' cancelled before the deadline; nothing was replayed");
                Notify(label + " timed out", "The host did not accept the command before the deadline. " +
                    "Nothing was replayed.", true);
            }
            catch (Exception error)
            {
                _diagnostic("tray action '" + label + "' not sent: " + error.GetType().Name + ": " + error.Message);
                Notify(label + " was not sent", error.Message + " Nothing was replayed.", true);
            }
        }
        finally
        {
            _pendingActions.Remove(key);
            UpdateActionEnabled();
        }
        // On a timeout the view is refreshed from the host rather than assumed unchanged.
        if (!_disposed) await RefreshStatusAsync();
    }

    // A forwarded CLI result keeps its existing envelope: Ok carries the command's own exit code and stderr.
    private static bool RejectedForwardedCli(AutoSyncHostRequest request, AutoSyncHostReply reply, out string diagnostic)
    {
        diagnostic = string.Empty;
        if (!string.Equals(request.Command, "cli", StringComparison.Ordinal) ||
            reply.Data is not { ValueKind: JsonValueKind.Object } data) return false;
        HostCliOutput? output;
        try { output = data.Deserialize<HostCliOutput>(HostJson); }
        catch (JsonException) { return false; }
        if (output is null || output.ExitCode == 0) return false;
        diagnostic = !string.IsNullOrWhiteSpace(output.Stderr) ? output.Stderr.Trim()
            : !string.IsNullOrWhiteSpace(output.Stdout) ? output.Stdout.Trim()
            : "The forwarded command exited with code " + output.ExitCode.ToString(CultureInfo.InvariantCulture) +
              "; nothing was replayed.";
        return true;
    }

    private void Notify(string title, string message, bool warning)
    {
        if (_disposed) return;
        LastNotification = (title, message, warning);
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = warning ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
    }

    internal static AutoSyncHostRequest PauseRequest(PauseOption option)
    {
        string[] duration = option.Kind switch
        {
            PauseKind.Minutes => ["--minutes", option.Minutes!.Value.ToString(CultureInfo.InvariantCulture)],
            PauseKind.UntilTomorrow => ["--until-tomorrow"],
            PauseKind.Indefinitely => ["--until-resumed"],
            _ => throw new ArgumentOutOfRangeException(nameof(option), "Unknown pause option.")
        };
        return AutoSyncHostClient.Request("cli") with
        {
            CliArgs = ["--cli", "pause", .. duration, "--yes"]
        };
    }

    internal static AutoSyncHostRequest ResumeRequest() =>
        AutoSyncHostClient.Request("cli") with { CliArgs = ["--cli", "resume", "--yes"] };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _stateIcon?.Dispose();
        _baseIcon.Dispose();
    }

    private sealed class Ownership(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            var held = Interlocked.Exchange(ref _mutex, null);
            if (held is null) return;
            try { held.ReleaseMutex(); }
            catch (ApplicationException) { /* the owning thread is gone; disposal still ends the icon */ }
            finally { held.Dispose(); }
        }
    }
}
