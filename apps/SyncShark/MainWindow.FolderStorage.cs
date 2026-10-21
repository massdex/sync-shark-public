using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Button = System.Windows.Controls.Button;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

public sealed record FolderStorageChoice(string PairId, string RelativePath, bool Supported,
    bool CloudOnly, bool Inherited, bool Mixed = false, bool Pending = false)
{
    public bool Visible => true;
    public string Icon => Mixed ? "mixed" : CloudOnly ? "cloud" : "computer";
    public string Description => !Supported ? "This folder is managed by another provider. Use that provider's storage commands." :
        (CloudOnly ? "Cloud Only" : "Keep Offline") + (Inherited ? " (Inherited)" : " (Folder Override)") +
        (Mixed ? ". Child folders have different storage choices" : "") +
        (Pending ? ". Storage change is pending" : "") +
        ". Applies to this folder and future files. Save Changes applies your choices; synchronization stays selected.";
}

public partial class MainWindow
{
    private IReadOnlyList<SyncDirectoryStorageRule>? _draftDirectoryStorageRules;
    private bool _draftNeedsCloudFiles;
    private FolderStorageChoice? StorageChoiceForLocal(string path)
    {
        if (_editingNewPair || PairList.SelectedItem is not PairRow row) return null;
        var pair = _pairSet.Pairs.SingleOrDefault(pair => pair.Id == row.Id);
        if (pair is null || !PilotPath.IsPathInside(pair.LocalRoot, path)) return null;
        return StorageChoice(pair, PilotPath.NormalizeRelative(Path.GetRelativePath(pair.LocalRoot, path)));
    }

    private FolderStorageChoice? StorageChoiceForDropbox(string path)
    {
        if (_editingNewPair || PairList.SelectedItem is not PairRow row) return null;
        var pair = _pairSet.Pairs.SingleOrDefault(pair => pair.Id == row.Id);
        if (pair is null || !DropboxPathWithin(path, pair.DropboxRoot)) return null;
        var relative = RelativeDropboxPath(pair.DropboxRoot, path);
        return relative.Length == 0 ? null : StorageChoice(pair, relative);
    }

    private FolderStorageChoice StorageChoice(SyncPair pair, string relative)
    {
        var rules = SyncDirectoryStoragePolicy.Normalize(_draftDirectoryStorageRules ?? pair.DirectoryStorageRules);
        var effective = pair with { DirectoryStorageRules = rules, DefaultStorage = PairStorageBox.SelectedIndex == 1
            ? SyncDirectoryStorage.CloudOnly : SyncDirectoryStorage.AlwaysAvailable };
        var value = SyncDirectoryStoragePolicy.Resolve(effective, relative);
        var supported = pair.FilesOnDemandEnabled || !(WindowsCloudFilesProvider.IsCloudFilesPlaceholder(pair.LocalRoot) &&
            !WindowsCloudFilesProvider.IsOwnedSyncRoot(pair.LocalRoot));
        return new(pair.Id, relative, supported, value == SyncDirectoryStorage.CloudOnly,
            !rules.Any(rule => string.Equals(rule.FolderPath, relative, StringComparison.OrdinalIgnoreCase)),
            rules.Any(rule => rule.FolderPath != relative && SyncDirectoryStoragePolicy.Contains(relative, rule.FolderPath) && rule.Storage != value),
            _draftDirectoryStorageRules is not null || (pair.DirectoryStorageApplyPaths ?? []).Any(path =>
                SyncDirectoryStoragePolicy.Contains(path, relative) || SyncDirectoryStoragePolicy.Contains(relative, path)));
    }

    private void FolderStorage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var choice = button.DataContext switch
        {
            LocalFolderNode node => node.StorageChoice,
            DropboxFolderNode node => node.StorageChoice,
            _ => null
        };
        if (choice is null) return;
        var menu = WorkspaceMenu(); menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom;
        MenuAction(menu, "Cloud Only", (_, _) => SetDirectoryStorageChoice(choice, SyncDirectoryStorage.CloudOnly), choice.Supported);
        MenuAction(menu, "Keep Offline", (_, _) => SetDirectoryStorageChoice(choice, SyncDirectoryStorage.AlwaysAvailable), choice.Supported);
        MenuAction(menu, "Use Parent Setting", (_, _) => SetDirectoryStorageChoice(choice, null), choice.Supported && !choice.Inherited);
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            var title = System.Windows.Automation.AutomationProperties.GetName(item);
            item.IsCheckable = true;
            item.IsChecked = title == "Use Parent Setting" ? choice.Inherited :
                !choice.Inherited && (title == "Cloud Only") == choice.CloudOnly;
            item.Header = CommandIcons.Label(title, title == "Cloud Only" ? "cloud" : title == "Keep Offline" ? "computer" : "folder");
        }
        menu.IsOpen = true; e.Handled = true;
    }

    private void SetDirectoryStorageChoice(FolderStorageChoice choice, SyncDirectoryStorage? storage)
    {
        if (_runActive || !choice.Supported) return;
        var pair = _pairSet.Pairs.Single(item => item.Id == choice.PairId);
        var rules = SyncDirectoryStoragePolicy.Normalize(_draftDirectoryStorageRules ?? pair.DirectoryStorageRules)
            .Where(rule => !string.Equals(rule.FolderPath, choice.RelativePath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (storage is not null) rules.Add(new(choice.RelativePath, storage.Value));
        _draftDirectoryStorageRules = SyncDirectoryStoragePolicy.Normalize(rules);
        _draftNeedsCloudFiles = !pair.FilesOnDemandEnabled && rules.Any(rule => rule.Storage == SyncDirectoryStorage.CloudOnly);
        RefreshLocalGlobalSelectionTree(); RefreshDropboxGlobalSelectionTree(); UpdatePairSaveAvailability();
        ShowPairEditorStatus("Folder storage has unsaved changes. Save Changes applies them; Discard Changes keeps the current policy.");
    }
}
