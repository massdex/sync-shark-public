using System.Windows;

namespace Massdex.SyncShark;

internal enum AppDialogChoice
{
    Cancel,
    Primary,
    Secondary
}

public partial class AppDialogWindow : Window
{
    private AppDialogChoice _choice;

    internal AppDialogWindow(string title, string summary, string detail, string primaryText,
        string secondaryText, bool destructive, string? tertiaryText = null)
    {
        InitializeComponent();
        Title = title;
        DialogTitle.Text = title;
        DialogSummary.Text = summary;
        DialogDetail.Text = detail;
        PrimaryButton.Content = CommandIcons.Label(primaryText);
        SecondaryButton.Content = CommandIcons.Label(secondaryText);
        System.Windows.Automation.AutomationProperties.SetName(PrimaryButton, primaryText);
        System.Windows.Automation.AutomationProperties.SetName(SecondaryButton, secondaryText);
        if (!string.IsNullOrWhiteSpace(tertiaryText))
        {
            TertiaryButton.Content = CommandIcons.Label(tertiaryText, "close");
            System.Windows.Automation.AutomationProperties.SetName(TertiaryButton, tertiaryText);
            TertiaryButton.Visibility = Visibility.Visible;
            SecondaryButton.IsCancel = false;
            TertiaryButton.IsCancel = true;
        }
        if (destructive)
        {
            PrimaryButton.IsDefault = false;
            SecondaryButton.IsDefault = true;
        }
        if (destructive && System.Windows.Application.Current.Resources["ErrorBrush"] is
            System.Windows.Media.Brush error)
        {
            PrimaryButton.Background = error;
            PrimaryButton.BorderBrush = error;
            PrimaryButton.Foreground = (System.Windows.Media.Brush)FindResource("ErrorButtonInk");
        }
        ContentRendered += (_, _) =>
        {
            var workArea = SystemParameters.WorkArea;
            MaxWidth = Math.Max(320, workArea.Width - 32);
            MaxHeight = Math.Max(240, workArea.Height - 32);
            if (ActualWidth > MaxWidth) Width = MaxWidth;
            if (destructive) SecondaryButton.Focus();
            else PrimaryButton.Focus();
        };
    }

    public static bool Confirm(Window owner, string title, string summary, string detail,
        string primaryText, string secondaryText = "Cancel", bool destructive = false)
    {
        var dialog = new AppDialogWindow(title, summary, detail, primaryText, secondaryText, destructive)
        {
            Owner = owner
        };
        return dialog.ShowDialog() == true;
    }

    internal static AppDialogChoice Choose(Window owner, string title, string summary, string detail,
        string primaryText, string secondaryText, string cancelText, bool secondaryIsDiscard = false)
    {
        var dialog = new AppDialogWindow(title, summary, detail, primaryText, secondaryText,
            destructive: false, tertiaryText: cancelText)
        {
            Owner = owner
        };
        // The caller states the intent: a discard choice gets the one shared destructive presentation,
        // and the safe default focus stays with the primary action either way.
        if (secondaryIsDiscard)
            CommandIcons.SetUnsavedAction(dialog.SecondaryButton, secondaryText, UnsavedActionIntent.Discard);
        _ = dialog.ShowDialog();
        return dialog._choice;
    }

    public static void Inform(Window? owner, string title, string summary, string detail,
        string primaryText = "Close")
    {
        var dialog = new AppDialogWindow(title, summary, detail, primaryText, string.Empty, false)
        {
            Owner = owner
        };
        dialog.SecondaryButton.Visibility = Visibility.Collapsed;
        _ = dialog.ShowDialog();
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        _choice = AppDialogChoice.Primary;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _choice = AppDialogChoice.Cancel;
        DialogResult = false;
    }

    private void Tertiary_Click(object sender, RoutedEventArgs e)
    {
        _choice = AppDialogChoice.Cancel;
        DialogResult = false;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        _choice = AppDialogChoice.Secondary;
        DialogResult = false;
    }
}
