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
/// product lock, so the host keeps syncing when the tray exits. Every read is bounded and every action is
/// sent once, never replayed.
/// </summary>
internal sealed class TrayClient : IDisposable
{
    private const int ConflictLimit = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StatusDeadline = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActionDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConflictCacheLifetime = TimeSpan.FromSeconds(5);

    private readonly AutoSyncHostClient _client;
    private readonly Action _exitTray;
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
    private TrayStatusSnapshot? _lastSnapshot;
    private TrayStatusView _view = TrayStatusView.Starting;
    private TrayConflictList? _conflictList;
    private DateTimeOffset _conflictsFetchedUtc;
    private string _pairSignature = string.Empty;
    private string _iconState = string.Empty;
    private Icon? _stateIcon;
    private bool _refreshing;
    private bool _conflictsLoading;
    private bool _pendingPairs;
    private int _pollCount;
    private bool _disposed;

    private sealed record TrayConflictList(int Total, IReadOnlyList<string> Rows);

    internal TrayClient(AutoSyncHostClient client, Action exitTray)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(exitTray);
        _client = client;
        _exitTray = exitTray;
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
            _pauseItem.DropDownItems.Add(new ToolStripMenuItem(option.Label, null,
                (_, _) => _ = SendMutationAsync(PauseRequest(option), "Pause " + option.Label))
            { ToolTipText = option.Detail });
        _pauseItem.DropDownItems.Add(new ToolStripSeparator());
        _resumeItem = new ToolStripMenuItem("Resume Sync", null,
            (_, _) => _ = SendMutationAsync(ResumeRequest(), "Resume"));
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
        _menu.Opening += (_, _) =>
        {
            if (_pendingPairs) { _pendingPairs = false; RebuildPairs(_view.Pairs); }
            if (DateTimeOffset.UtcNow - _conflictsFetchedUtc > ConflictCacheLifetime) _ = LoadConflictsAsync();
        };
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
        _ = RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshing || _disposed) return;
        _refreshing = true;
        try
        {
            AutoSyncHostReply? reply = null;
            try
            {
                using var deadline = new CancellationTokenSource(StatusDeadline);
                reply = await _client.TrySendAsync(AutoSyncHostClient.Request("status"),
                    TimeSpan.FromMilliseconds(300), deadline.Token);
            }
            catch (Exception error) when (error is AutoSyncHostUncertainException or OperationCanceledException)
            {
                // A miss keeps the last known state below; a hung or slow read must never freeze the tray.
            }
            if (_disposed) return;
            var snapshot = TrayStatusView.Parse(reply);
            if (snapshot is not null) _lastSnapshot = snapshot;
            var updating = snapshot is null && _lastSnapshot is not null;
            Apply(TrayStatusView.Derive(_lastSnapshot ?? snapshot, updating, _pollCount % 2 == 1));
            _pollCount++;
            // A full window cannot coexist with the host, so this is only reachable after the host stops.
            if (_pollCount % 5 == 0 && ExistingWindowActivation.IsProductWindowPresent()) _exitTray();
        }
        catch (Exception)
        {
            // A display-only update must never terminate the client; the next poll retries the existing read.
        }
        finally { _refreshing = false; }
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
        // No mutation is offered while the host is unreachable, starting or stopping.
        _pauseItem.Enabled = view.CanAct;
        _resumeItem.Enabled = view.CanAct;
        foreach (var item in _syncNowItems) item.Enabled = view.CanAct;
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
            var syncNow = new ToolStripMenuItem("Sync now", null,
                (_, _) => _ = SendMutationAsync(AutoSyncHostClient.Request("now", pair.PairId), "Sync now"))
            { Enabled = _view.CanAct };
            item.DropDownItems.Add(syncNow);
            _syncNowItems.Add(syncNow);
            _menu.Items.Insert(index++, item);
            _pairItems.Add(item);
        }
    }

    private static ToolStripMenuItem ReadOnly(string text) => new(text) { Enabled = false };

    private void UpdateConflictText(int statusCount)
    {
        var fresh = _conflictList is not null && DateTimeOffset.UtcNow - _conflictsFetchedUtc <= ConflictCacheLifetime;
        var total = fresh ? _conflictList!.Total : statusCount;
        _conflictsItem.Text = total == 0 ? "Conflicts: none open"
            : "Conflicts: " + total.ToString(CultureInfo.InvariantCulture) + " open" +
              (fresh ? string.Empty : " — open the menu to load the first " + ConflictLimit);
    }

    private async Task LoadConflictsAsync()
    {
        if (_conflictsLoading || _disposed) return;
        _conflictsLoading = true;
        try
        {
            using var deadline = new CancellationTokenSource(StatusDeadline);
            var reply = await _client.TrySendAsync(AutoSyncHostClient.Request("cli") with
            {
                CliArgs = ["--cli", "conflicts", "list", "--limit",
                    ConflictLimit.ToString(CultureInfo.InvariantCulture), "--json"]
            }, TimeSpan.FromMilliseconds(300), deadline.Token);
            if (_disposed || reply is null || !reply.Ok || reply.Data is not { } element ||
                element.ValueKind != JsonValueKind.Object) return;
            var data = element;
            if (Int(data, "exitCode") != 0 || Text(data, "stdout") is not { Length: > 0 } output) return;
            if (!TryParseConflicts(output, out var list)) return;
            _conflictList = list;
            _conflictsFetchedUtc = DateTimeOffset.UtcNow;
            UpdateConflictText(_view.OpenConflicts);
            RebuildConflictRows(list);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or
            AutoSyncHostUncertainException or InvalidOperationException or JsonException)
        {
            // The list is display-only and reloaded next time the menu opens; the status count stays authoritative.
        }
        finally { _conflictsLoading = false; }
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
        try
        {
            using var deadline = new CancellationTokenSource(ActionDeadline);
            var reply = await _client.TrySendAsync(request, TimeSpan.FromMilliseconds(300), deadline.Token);
            if (reply is null) Notify(label + " was not sent", "Background sync is not reachable. Nothing was replayed.", true);
            else if (!reply.Ok)
                Notify(label + " failed", reply.Error?.Message ?? "The host refused the command; nothing was replayed.", true);
        }
        catch (AutoSyncHostUncertainException)
        {
            Notify(label + " was sent once", "The reply was uncertain. The host keeps admitted work; this tray never replays it.", true);
        }
        catch (Exception error)
        {
            // The mutation was not admitted. Report it once; a tray never retries a mutation.
            Notify(label + " was not sent", error.Message + " Nothing was replayed.", true);
        }
        if (!_disposed) await RefreshStatusAsync();
    }

    private void Notify(string title, string message, bool warning)
    {
        if (_disposed) return;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = warning ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
    }

    private static AutoSyncHostRequest PauseRequest(PauseOption option)
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

    private static AutoSyncHostRequest ResumeRequest() =>
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
