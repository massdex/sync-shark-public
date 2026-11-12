using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SyncShark.UiFixture;

// Records which real WPF event routes the isolated fixture reached. This is
// coverage evidence only: each scenario's assertions still prove its outcome.
// No captions, text values, paths displayed in controls or account data are read.
internal sealed class UiInteractionEvidence : IDisposable
{
    private static UiInteractionEvidence? _active;
    private readonly string _root;
    private readonly string _route;
    private readonly Dictionary<string, int> _events = new(StringComparer.Ordinal);
    private UiInteractionEvidence(string root, string route) { _root = root; _route = route; }

    internal static UiInteractionEvidence? Start(string[] args)
    {
        if (args.Length != 2 || !args[0].EndsWith("-checks", StringComparison.Ordinal) ||
            File.Exists(args[1])) return null; // Bound CLI child receives a request file, not a fixture directory.
        var result = new UiInteractionEvidence(Path.GetFullPath(args[1]), args[0]);
        if (_active is not null) throw new InvalidOperationException("A UI evidence scope is already active.");
        _active = result;
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent,
            new RoutedEventHandler(Record), true);
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.ClickEvent,
            new RoutedEventHandler(Record), true);
        EventManager.RegisterClassHandler(typeof(Selector), Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler((sender, args) => Record(sender, args)), true);
        EventManager.RegisterClassHandler(typeof(TextBox), TextBox.TextChangedEvent,
            new TextChangedEventHandler((sender, args) => Record(sender, args)), true);
        return result;
    }

    private static void Record(object sender, RoutedEventArgs args)
    {
        if (_active is null || sender is not FrameworkElement element || !ReferenceEquals(args.Source, sender)) return;
        var owner = Window.GetWindow(element);
        var key = JsonSerializer.Serialize(new {
            owner = owner?.GetType().FullName,
            control = element.GetType().Name,
            name = element.Name,
            eventName = args.RoutedEvent.Name,
            attachedToWindow = owner is not null,
            visible = element.IsVisible,
            enabled = element.IsEnabled
        });
        _active._events[key] = _active._events.GetValueOrDefault(key) + 1;
    }

    public void Dispose()
    {
        _active = null;
        Directory.CreateDirectory(_root);
        var report = new {
            route = _route,
            meaning = "Observed WPF event routes only; scenario assertions establish outcomes. Anonymous controls remain unmapped.",
            events = _events.OrderBy(x => x.Key).Select(x => new {
                control = JsonSerializer.Deserialize<JsonElement>(x.Key), count = x.Value
            }).ToArray()
        };
        File.WriteAllText(Path.Combine(_root, "ui-interaction-evidence.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
