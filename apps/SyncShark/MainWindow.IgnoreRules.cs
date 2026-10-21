using System.IO;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private IReadOnlyList<SyncIgnoreRule> _draftIgnoreRules = [];
    private StackPanel? _ignoreRuleRows;
    private TextBlock? _ignorePreview;
    private int _ignorePreviewGeneration;

    private void BuildIgnoreRules()
    {
        IgnoreRulesPanel.Children.Add(Text("Ignore Rules", 17, true));
        IgnoreRulesPanel.Children.Add(Text("Skip matching items in this folder pair, on both sides. Existing files stay where they are; nothing is deleted.",
            13, brush: "MutedInk", margin: new Thickness(0, 5, 0, 12)));
        var editor = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var kind = new ComboBox { ItemsSource = new[] { "File Type", "File Name", "Folder Name" }, SelectedIndex = 0,
            Width = 150, Margin = new Thickness(0, 0, 10, 8) };
        System.Windows.Automation.AutomationProperties.SetName(kind, "Ignore Rule Type");
        var value = new TextBox { Width = 240, Margin = new Thickness(0, 0, 10, 8),
            ToolTip = "Examples: .tmp, Thumbs.db, or node_modules. Separate several values with commas." };
        System.Windows.Automation.AutomationProperties.SetName(value, "Ignore Rule Name or File Type");
        var add = Button("Add Rule", (_, _) =>
        {
            try
            {
                var values = value.Text.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (values.Length == 0) throw new InvalidDataException("Enter a file type or name first.");
                _draftIgnoreRules = SyncIgnorePolicy.Normalize(_draftIgnoreRules.Concat(values.Select(text => new SyncIgnoreRule((SyncIgnoreKind)kind.SelectedIndex, text))).ToArray());
                value.Clear(); RenderIgnoreRules(); UpdatePairSaveAvailability();
            }
            catch (InvalidDataException error) { if (_ignorePreview is not null) _ignorePreview.Text = error.Message; }
        });
        add.Margin = new Thickness(0, 0, 10, 8);
        editor.Children.Add(kind); editor.Children.Add(value); editor.Children.Add(add);
        var presets = Button("Common Rules", (_, _) => { }, quiet: true);
        presets.Click += (_, _) =>
        {
            var menu = WorkspaceMenu(); menu.PlacementTarget = presets;
            void Preset(string title, params SyncIgnoreRule[] rules) => MenuAction(menu, title, (_, _) =>
            {
                try { _draftIgnoreRules = SyncIgnorePolicy.Normalize(_draftIgnoreRules.Concat(rules).ToArray()); RenderIgnoreRules(); UpdatePairSaveAvailability(); }
                catch (InvalidDataException error) { if (_ignorePreview is not null) _ignorePreview.Text = error.Message; }
            });
            Preset("Temporary Files (.tmp, .temp)", new(SyncIgnoreKind.FileExtension, ".tmp"), new(SyncIgnoreKind.FileExtension, ".temp"));
            Preset("Windows Thumbnail and Folder Settings", new(SyncIgnoreKind.FileName, "Thumbs.db"), new(SyncIgnoreKind.FileName, "desktop.ini"));
            Preset("Node Dependencies (node_modules)", new SyncIgnoreRule(SyncIgnoreKind.FolderName, "node_modules"));
            menu.IsOpen = true;
        };
        editor.Children.Add(presets); IgnoreRulesPanel.Children.Add(editor);
        var hint = Text("File types: enter .tmp or .bak. Separate several with commas.", 12, brush: "MutedInk", margin: new Thickness(0, 0, 0, 8));
        kind.SelectionChanged += (_, _) => hint.Text = kind.SelectedIndex switch
        {
            1 => "File names: enter an exact name such as Thumbs.db. Applies in every folder in this pair.",
            2 => "Folder names: enter a name such as node_modules. Matching folders and their contents are skipped.",
            _ => "File types: enter .tmp or .bak. Separate several with commas."
        };
        IgnoreRulesPanel.Children.Add(hint);
        _ignoreRuleRows = new StackPanel(); IgnoreRulesPanel.Children.Add(_ignoreRuleRows);
        var preview = AsyncButton("Preview Matches", async (_, _) => await PreviewIgnoreRulesFromUiAsync(), quiet: true);
        preview.HorizontalAlignment = HorizontalAlignment.Left;
        IgnoreRulesPanel.Children.Add(preview);
        _ignorePreview = Text("No rules added. Everything selected in Folders can sync.", 12, brush: "MutedInk", margin: new Thickness(0, 6, 0, 0));
        IgnoreRulesPanel.Children.Add(_ignorePreview);
        RenderIgnoreRules();
    }

    private void LoadIgnoreRules(IReadOnlyList<SyncIgnoreRule>? rules)
    { _draftIgnoreRules = SyncIgnorePolicy.Normalize(rules); RenderIgnoreRules(); }

    private void RenderIgnoreRules()
    {
        _ignorePreviewGeneration++;
        if (_ignoreRuleRows is null) return;
        _ignoreRuleRows.Children.Clear();
        foreach (var rule in _draftIgnoreRules)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
            var remove = Button("Remove Rule", (_, _) =>
            { _draftIgnoreRules = _draftIgnoreRules.Where(item => item != rule).ToArray(); RenderIgnoreRules(); UpdatePairSaveAvailability(); }, quiet: true);
            remove.Content = CommandIcons.Icon("close");
            remove.ToolTip = "Remove: " + rule.Description;
            System.Windows.Automation.AutomationProperties.SetName(remove, "Remove " + rule.Description);
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            row.Children.Add(Text(rule.Description, 13)); _ignoreRuleRows.Children.Add(row);
        }
        if (_ignorePreview is not null) _ignorePreview.Text = _draftIgnoreRules.Count == 0
            ? "No rules added. Everything selected in Folders can sync."
            : "Changes apply with Save Changes. Preview uses this pair's latest known inventory, without opening or downloading files.";
    }

    private async Task PreviewIgnoreRulesFromUiAsync()
    {
        if (_ignorePreview is null) return;
        if (_editingNewPair || _editingPairRootId is null)
        { _ignorePreview.Text = "Matching examples become available after this pair's first inventory. Rules also apply to future matching items."; return; }
        var generation = ++_ignorePreviewGeneration;
        var pairId = _editingPairRootId;
        var rules = _draftIgnoreRules.ToArray();
        _ignorePreview.Text = "Checking the saved inventory…";
        try
        {
            var result = await Task.Run(() => _authorityHost.PreviewIgnoreRulesAsync(pairId, rules));
            if (generation != _ignorePreviewGeneration) return;
            _ignorePreview.Text = $"{result.MatchCount:N0} matching items in the latest known inventory. Future matches will also be ignored." +
                (result.Examples.Count == 0 ? "" : "\n" + string.Join("\n", result.Examples));
        }
        catch (Exception error)
        { if (generation == _ignorePreviewGeneration) _ignorePreview.Text = "Preview unavailable: " + error.Message; }
    }
}
