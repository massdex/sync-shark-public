using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;
using Panel = System.Windows.Controls.Panel;
using Button = System.Windows.Controls.Button;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark;

/// <summary>Opt-in shared-owner control surface. No store, preferences authority or engine exists in this process.</summary>
internal sealed class SharedAutoSyncWindow : Window
{
    private readonly AutoSyncGuiClient _client = new(BackgroundHostEntry.Client());
    private readonly TextBox _output = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _pair = new() { MinWidth = 220, Margin = new Thickness(4) };
    private readonly TextBox _notice = new() { MinWidth = 200, Margin = new Thickness(4) };
    private readonly TextBox _path = new() { MinWidth = 160, Margin = new Thickness(4) };
    private readonly TextBox _account = new() { MinWidth = 200, Margin = new Thickness(4) };
    private readonly TextBox _interval = new() { Text = "15", Width = 50, Margin = new Thickness(4) };

    internal SharedAutoSyncWindow()
    {
        Title = "Sync Shark — shared AutoSync";
        Width = 850; Height = 620;
        var layout = new DockPanel { Margin = new Thickness(16) };
        var controls = new StackPanel();
        DockPanel.SetDock(controls, Dock.Top);
        controls.Children.Add(new TextBlock { Text = "Shared background host (preview). Closing this window leaves AutoSync running.", TextWrapping = TextWrapping.Wrap });
        var global = new WrapPanel();
        Add(global, "Refresh", () => _client.StatusAsync());
        Add(global, "Enable AutoSync", () => _client.SetEnabledAsync(null, true));
        Add(global, "Disable AutoSync", () => _client.SetEnabledAsync(null, false));
        global.Children.Add(_interval);
        Add(global, "Set interval (minutes)", () => _client.SetIntervalAsync(int.Parse(_interval.Text)));
        controls.Children.Add(global);
        var pairs = new WrapPanel();
        pairs.Children.Add(new TextBlock { Text = "Exact pair ID", VerticalAlignment = VerticalAlignment.Center });
        pairs.Children.Add(_pair);
        Add(pairs, "Enable", () => _client.SetEnabledAsync(_pair.Text, true));
        Add(pairs, "Disable", () => _client.SetEnabledAsync(_pair.Text, false));
        Add(pairs, "Sync now", () => _client.RunNowAsync(_pair.Text));
        Add(pairs, "History", () => _client.HistoryAsync(_pair.Text));
        controls.Children.Add(pairs);
        var notices = new WrapPanel();
        Add(notices, "List notices", () => Cli("conflicts", "list", "--pair", _pair.Text, "--json"));
        notices.Children.Add(new TextBlock { Text = "Notice key / path" });
        notices.Children.Add(_notice); notices.Children.Add(_path);
        Add(notices, "Show notice", () => Cli("conflicts", "show", "--pair", _pair.Text, "--path", _path.Text, "--json"));
        Add(notices, "Retry", () => Resolve("retry-now"));
        Add(notices, "Ignore", () => Resolve("ignore"));
        controls.Children.Add(notices);
        var accounts = new WrapPanel();
        Add(accounts, "List accounts", () => Cli("accounts", "list", "--json"));
        accounts.Children.Add(new TextBlock { Text = "Exact account ID" }); accounts.Children.Add(_account);
        Add(accounts, "Reconnect Dropbox", () => Cli("accounts", "reauthorize", "--account", _account.Text, "--yes"));
        controls.Children.Add(accounts);
        layout.Children.Add(controls); layout.Children.Add(_output); Content = layout;
        Loaded += async (_, _) =>
        {
            try { await BackgroundHostEntry.StartAsync(CancellationToken.None); await ShowReply(() => _client.StatusAsync()); }
            catch (Exception error) { _output.Text = error.Message + "\nClose any legacy GUI safely and reopen this window. No competing engine was created."; }
        };
    }

    private Task<AutoSyncHostReply?> Cli(params string[] args) => BackgroundHostEntry.Client().TrySendAsync(
        AutoSyncHostClient.Request("cli") with { CliArgs = new[] { "--cli" }.Concat(args).ToArray() }, TimeSpan.FromSeconds(2));

    private Task<AutoSyncHostReply?> Resolve(string choice)
    {
        var args = new List<string> { "conflicts", "resolve", "--pair", _pair.Text,
            "--key", _notice.Text, "--generation", "0", "--choice", choice, "--yes" };
        if (_path.Text.Length > 0) args.AddRange(["--path", _path.Text]);
        return Cli(args.ToArray());
    }

    private void Add(Panel panel, string label, Func<Task<AutoSyncHostReply?>> operation)
    {
        var button = new Button { Content = label, Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4) };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await ShowReply(operation); } finally { button.IsEnabled = true; } };
        panel.Children.Add(button);
    }

    private async Task ShowReply(Func<Task<AutoSyncHostReply?>> operation)
    {
        try
        {
            var reply = await operation();
            _output.Text = reply is null ? "Host not running. Reopen this window or use autosync start." :
                JsonSerializer.Serialize(reply, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception error) { _output.Text = error.Message; }
    }
}
