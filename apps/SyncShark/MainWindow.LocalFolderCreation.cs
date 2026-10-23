using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Massdex.SyncShark;

// UI29 remainder: the local Create Folder action of the pair editor.
//
// The editor is only a consumer. The parent rules, the name rules, the pinned parent and the exclusive
// creation all live in the shared LocalFolderActions call that the CLI uses too, so the two surfaces
// cannot drift apart. Nothing here ticks a folder for sync, binds or saves a pair, changes the account,
// chooses a cloud folder, starts a run or touches a provider.
public partial class MainWindow
{
    private string? _createLocalFolderParentPath;
    private string? _createLocalFolderAccount;
    private int _createLocalFolderGeneration;
    private bool _createLocalFolderPending;

    /// <summary>
    /// Opens the one local Create Folder dialog for the exact folder the user selected in the tree or
    /// typed in the path entry beside this button. The dialog is the only place a name is entered, so the
    /// editor itself keeps no extra permanent form field.
    /// </summary>
    private void CreateLocalFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!PairLocationDraftActive)
        {
            ShowStatus("Open a folder pair before creating a folder on this computer.");
            return;
        }
        if (!LocalPathEntryBox.IsEnabled)
        {
            ShowStatus("The folder tree is busy reading a path. Try Create Folder again when that finishes.");
            return;
        }
        var typed = LocalPathEntryBox.Text.Trim();
        string? parent = typed.Length > 0
            ? typed
            : LocalRootTree.SelectedItem is LocalFolderNode { IsPlaceholder: false } selected ? selected.Path : null;
        if (parent is null)
        {
            ShowStatus("Choose a folder in the tree, or type its full path, and then create the new folder inside it.");
            return;
        }
        try
        {
            // The same parent rules the shared action applies, so the dialog only ever names a usable folder.
            parent = LocalFolderActions.RequireExistingLocalParent(parent);
        }
        catch (Exception error) when (error is ArgumentException or IOException)
        {
            ShowStatus(error.Message);
            return;
        }
        if (!LocalRootNodes.Any(root => !root.IsPlaceholder && LocalPathWithin(parent, root.Path)))
        {
            ShowStatus(parent + " is not one of the folders shown here, so nothing was created. " +
                       "Open that folder in this tree first, or use File Explorer.");
            return;
        }
        _createLocalFolderParentPath = parent;
        _createLocalFolderGeneration = _pairEditorGeneration;
        _createLocalFolderAccount = SelectedPairConnectionId;
        _createLocalFolderPending = false;
        CreateLocalFolderParentText.Text = parent;
        CreateLocalFolderNameBox.Text = string.Empty;
        HideCreateLocalFolderError();
        CreateLocalFolderConfirmButton.IsEnabled = true;
        CreateLocalFolderCancelButton.IsEnabled = true;
        CreateLocalFolderDialogHost.Visibility = Visibility.Visible;
        CreateLocalFolderNameBox.Focus();
        Keyboard.Focus(CreateLocalFolderNameBox);
    }

    private void CreateLocalFolderCancel_Click(object sender, RoutedEventArgs e)
    {
        // A confirmed creation owns the effect until it finishes, so Cancel cannot race it.
        if (_createLocalFolderPending) return;
        CloseCreateLocalFolderDialog();
        ShowStatus("Create folder cancelled. Nothing was created and nothing changed.");
    }

    private void CreateLocalFolder_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CreateLocalFolderCancel_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ReferenceEquals(e.OriginalSource, CreateLocalFolderNameBox))
        {
            CreateLocalFolderConfirm_Click(sender, e);
            e.Handled = true;
        }
    }

    private async void CreateLocalFolderConfirm_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        // Duplicate submit: the first confirmed request owns the effect and the second is ignored.
        if (_createLocalFolderPending) return;
        var parent = _createLocalFolderParentPath;
        if (parent is null)
        {
            CloseCreateLocalFolderDialog();
            return;
        }
        if (!IsCurrentPairDraft(_createLocalFolderGeneration) ||
            !string.Equals(_createLocalFolderAccount, SelectedPairConnectionId, StringComparison.Ordinal))
        {
            CloseCreateLocalFolderDialog();
            ShowStatus("The pair editor changed. Open Create Folder again in the current pair. Nothing was created.");
            return;
        }
        string name;
        try
        {
            name = LocalFolderActions.RequireExactFolderName(CreateLocalFolderNameBox.Text);
        }
        catch (ArgumentException error)
        {
            ShowCreateLocalFolderError(error.Message);
            return;
        }
        // The editor generation, account and exact parent this completion belongs to.
        var generation = _createLocalFolderGeneration;
        var account = _createLocalFolderAccount;
        var cancellation = _pairDraftCancellation?.Token ?? CancellationToken.None;
        _createLocalFolderPending = true;
        CreateLocalFolderConfirmButton.IsEnabled = false;
        CreateLocalFolderCancelButton.IsEnabled = false;
        string? created = null;
        try
        {
            // The one shared action runs off the UI thread and needs neither the dispatcher nor a provider.
            created = await Task.Run(() => LocalFolderActions.Create(parent, name, cancellation), cancellation);
            var changed = !IsCurrentPairDraft(generation) ||
                          !string.Equals(account, SelectedPairConnectionId, StringComparison.Ordinal) ||
                          !string.Equals(_createLocalFolderParentPath, parent, StringComparison.Ordinal);
            CloseCreateLocalFolderDialog();
            if (changed)
            {
                // The folder really was created, but this completion no longer belongs to the open editor.
                ShowStatus("Created " + created + ". The pair editor changed while it was being created, " +
                           "so this editor was left as it is.");
                return;
            }
            RefreshLocalFolderAfterCreate(parent);
            await ExpandLocalTreeToSavedPathAsync(created, cancellation);
            if (!IsCurrentPairDraft(generation)) return;
            ShowStatus("Created " + created + ". Nothing was ticked for sync, and no pair, account or " +
                       "sync state changed.");
        }
        catch (OperationCanceledException)
        {
            if (created is not null)
                ShowStatus("Created " + created + ". Refreshing the folder tree was cancelled; the folder has been kept.");
            else if (IsCurrentPairDraft(generation))
                ShowCreateLocalFolderError("The folder was not created because this editor draft was cancelled.");
        }
        catch (Exception error)
        {
            if (created is not null)
            {
                CloseCreateLocalFolderDialog();
                ShowStatus("Created " + created + ". The folder tree could not be refreshed: " + error.Message);
                return;
            }
            // Every refusal is reported inside the dialog, and no path leaves the effect unexplained.
            if (!IsCurrentPairDraft(generation))
            {
                CloseCreateLocalFolderDialog();
                ShowStatus(error.Message);
                return;
            }
            ShowCreateLocalFolderError(error.Message);
        }
        finally
        {
            _createLocalFolderPending = false;
            if (CreateLocalFolderDialogHost.Visibility == Visibility.Visible)
            {
                CreateLocalFolderConfirmButton.IsEnabled = true;
                CreateLocalFolderCancelButton.IsEnabled = true;
            }
        }
    }

    /// <summary>
    /// Re-reads exactly one directory - the parent that just received the new folder - and merges any child
    /// row that is not shown yet. Existing rows keep their identity, tick, storage and expansion state, and
    /// no other branch of the tree is read, rebuilt or collapsed.
    /// </summary>
    private void RefreshLocalFolderAfterCreate(string parentPath)
    {
        var parentNode = FindLocalNode(parentPath);
        if (parentNode is null || parentNode.IsPlaceholder) return;
        // A row reads its directory once, so re-reading this single directory needs a fresh row. The read
        // itself is the tree's own production load, not a second folder-reading implementation.
        var snapshot = LocalFolderNode.CreateUnloaded(parentPath);
        snapshot.LoadChildren();
        if (snapshot.Status.Length != 0)
        {
            ShowStatus(parentPath + " could not be re-read, so the new folder is not listed yet. Use Refresh.");
            return;
        }
        parentNode.IsExpanded = true;
        foreach (var child in snapshot.Children)
        {
            if (parentNode.Children.Any(existing => !existing.IsPlaceholder &&
                    string.Equals(existing.Name, child.Name, StringComparison.OrdinalIgnoreCase))) continue;
            parentNode.Children.Insert(SortedLocalChildIndex(parentNode.Children, child.Name), child);
        }
        foreach (var placeholder in parentNode.Children.Where(item => item.IsPlaceholder).ToArray())
            parentNode.Children.Remove(placeholder);
        RefreshLocalGlobalSelectionTree();
        ApplyGlobalFolderTreeFilter();
    }

    private static int SortedLocalChildIndex(IReadOnlyList<LocalFolderNode> children, string name)
    {
        for (var index = 0; index < children.Count; index++)
            if (string.Compare(children[index].Name, name, StringComparison.OrdinalIgnoreCase) > 0) return index;
        return children.Count;
    }

    private void ShowCreateLocalFolderError(string message)
    {
        CreateLocalFolderErrorText.Text = message;
        CreateLocalFolderErrorText.Visibility = Visibility.Visible;
    }

    private void HideCreateLocalFolderError()
    {
        CreateLocalFolderErrorText.Text = string.Empty;
        CreateLocalFolderErrorText.Visibility = Visibility.Collapsed;
    }

    private void CloseCreateLocalFolderDialog()
    {
        CreateLocalFolderDialogHost.Visibility = Visibility.Collapsed;
        CreateLocalFolderNameBox.Text = string.Empty;
        HideCreateLocalFolderError();
        _createLocalFolderParentPath = null;
        _createLocalFolderAccount = null;
        if (CreateLocalFolderButton.IsVisible && CreateLocalFolderButton.IsEnabled)
            CreateLocalFolderButton.Focus();
    }
}
