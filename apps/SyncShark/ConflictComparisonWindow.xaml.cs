using System.Text;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Text.RegularExpressions;

namespace Massdex.SyncShark;

public partial class ConflictComparisonWindow : Window
{
    private readonly SyncConflictContent _content;
    private readonly EndpointReviewPresentation? _endpoints;
    private string? _localText;
    private string? _dropboxText;
    public SyncAuthorityConflictResolutionKind? RequestedResolution { get; private set; }

    public ConflictComparisonWindow(SyncConflictContent content, SyncPair? pair = null, bool allowChoices = true)
    {
        _content = content;
        if (pair is not null && pair.Id != content.PairId)
            throw new InvalidDataException("The comparison and endpoint context belong to different pairs.");
        _endpoints = pair is null ? null : EndpointReviewPresentation.For(pair);
        InitializeComponent();
        if (!allowChoices)
            ChooseLeftButton.Visibility = ChooseRightButton.Visibility = KeepBothButton.Visibility = Visibility.Collapsed;
        LeftTextTitle.Text = LeftImageTitle.Text = LeftTitle;
        RightTextTitle.Text = RightImageTitle.Text = RightTitle;
        ChooseLeftButton.Content = "Use " + LeftTitle;
        ChooseRightButton.Content = "Use " + RightTitle;
        MainWindow.DecorateCommands(this);
        ComparisonTabs.ItemContainerStyle = WorkspaceVisuals.WorkspaceTabStyle();
        PathText.Text = content.RelativePath;
        ComparisonSummary.Text = content.Note ?? "Both retained versions were loaded without changing either side.";
        MetadataText.Text = Metadata(content);
        if (content.MergeBaseText is not null)
        {
            MergeTab.Visibility = Visibility.Visible;
            MergedText.Text = content.MergedText ?? "Overlapping edits require manual resolution. No merged file has been saved.\n\nShared baseline:\n" + content.MergeBaseText;
        }
        var leftBytes = _endpoints is null ? content.LocalBytes : _endpoints.LeftValue(content.LocalBytes, content.DropboxBytes);
        var rightBytes = _endpoints is null ? content.DropboxBytes : _endpoints.RightValue(content.LocalBytes, content.DropboxBytes);
        _localText = DecodeText(leftBytes);
        _dropboxText = DecodeText(rightBytes);
        if ((content.SnapshotA is not null && content.SnapshotB is not null) ||
            ((_localText is null || _dropboxText is null) && leftBytes is not null && rightBytes is not null))
            OpenBothButton.Visibility = Visibility.Visible;
        if (_localText is not null && _dropboxText is not null)
        {
            LocalText.Text = ConflictTextDiff.BoundedSide(_localText);
            DropboxText.Text = ConflictTextDiff.BoundedSide(_dropboxText);
            if (ConflictTextDiff.IsCodePath(content.RelativePath))
            {
                DiffGranularityBox.SelectedIndex = 2;
                ComparisonSummary.Text = (content.Note ?? "Both retained versions were loaded without changing either side.") +
                                         " Syntax-aware token comparison is selected for this code file.";
            }
            RefreshDiff();
            ImageTab.Visibility = Visibility.Collapsed;
        }
        else if (TryImage(leftBytes, out var localImage) &&
                 TryImage(rightBytes, out var dropboxImage))
        {
            LocalImage.Source = localImage;
            DropboxImage.Source = dropboxImage;
            ComparisonSummary.Text = (content.Note ?? "Both retained versions were loaded without changing either side.") +
                                     $" Image dimensions: {LeftTitle} {localImage!.PixelWidth}×{localImage.PixelHeight}; {RightTitle} {dropboxImage!.PixelWidth}×{dropboxImage.PixelHeight}.";
            UnifiedTab.Visibility = Visibility.Collapsed;
            SideBySideTab.Visibility = Visibility.Collapsed;
            IgnoreWhitespaceBox.Visibility = Visibility.Collapsed;
            DiffGranularityBox.Visibility = Visibility.Collapsed;
            ImageTab.IsSelected = true;
        }
        else
        {
            UnifiedTab.Visibility = Visibility.Collapsed;
            SideBySideTab.Visibility = Visibility.Collapsed;
            ImageTab.Visibility = Visibility.Collapsed;
            IgnoreWhitespaceBox.Visibility = Visibility.Collapsed;
            DiffGranularityBox.Visibility = Visibility.Collapsed;
            ComparisonTabs.SelectedIndex = ComparisonTabs.Items.Count - 1;
        }
    }

    private async void OpenBoth_Click(object sender, RoutedEventArgs e)
    {
        if ((_content.LocalBytes is null || _content.DropboxBytes is null) &&
            (_content.SnapshotA is null || _content.SnapshotB is null)) return;
        OpenBothButton.IsEnabled = false;
        try
        {
            var folder = _content.SnapshotA is not null ? Path.GetDirectoryName(_content.SnapshotA)! :
                Path.Combine(Path.GetTempPath(), "SyncShark-compare-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var extension = Path.GetExtension(_content.RelativePath);
            if (extension.Length > 20 || extension.Any(ch => ch != '.' && !char.IsAsciiLetterOrDigit(ch))) extension = ".bin";
            foreach (var (side, bytes) in new[] { ("A", _content.LocalBytes), ("B", _content.DropboxBytes) })
            {
                var snapshot = side == "A" ? _content.SnapshotA : _content.SnapshotB;
                var path = snapshot ?? Path.Combine(folder, side + extension);
                if (snapshot is null)
                {
                    await File.WriteAllBytesAsync(path, bytes!);
                    File.SetAttributes(path, FileAttributes.ReadOnly);
                }
                // Always ask which viewer to use; do not execute an incoming .exe or script by association.
                var start = new System.Diagnostics.ProcessStartInfo("rundll32.exe") { UseShellExecute = false };
                start.ArgumentList.Add("shell32.dll,OpenAs_RunDLL");
                start.ArgumentList.Add(path);
                System.Diagnostics.Process.Start(start);
            }
            ComparisonSummary.Text = "Opened read-only snapshots. Originals are unchanged. Snapshot folder: " + folder;
        }
        catch (Exception error) { ComparisonSummary.Text = "Could not open both files: " + error.Message; }
        finally { OpenBothButton.IsEnabled = true; }
    }

    private void DiffOption_Changed(object sender, RoutedEventArgs e)
    {
        if (UnifiedText is not null) RefreshDiff();
    }

    private void RefreshDiff()
    {
        if (_localText is null || _dropboxText is null) return;
        var wordMode = DiffGranularityBox.SelectedIndex == 1;
        var syntaxMode = DiffGranularityBox.SelectedIndex == 2;
        UnifiedText.Text = ConflictTextDiff.Unified(_localText, _dropboxText,
            IgnoreWhitespaceBox.IsChecked == true, wordMode, syntaxMode, LeftTitle, RightTitle);
    }

    private static string? DecodeText(byte[]? bytes)
    {
        if (bytes is null || bytes.AsSpan().IndexOf((byte)0) >= 0) return null;
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    private static bool TryImage(byte[]? bytes, out BitmapImage? image)
    {
        image = null;
        if (bytes is null) return false;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var value = new BitmapImage();
            value.BeginInit();
            value.CacheOption = BitmapCacheOption.OnLoad;
            value.StreamSource = stream;
            value.EndInit();
            value.Freeze();
            image = value;
            return true;
        }
        catch { return false; }
    }

    private string LeftTitle => _endpoints?.LeftTitle ?? "First endpoint";
    private string RightTitle => _endpoints?.RightTitle ?? "Second endpoint";
    private string Metadata(SyncConflictContent value) =>
        $"Path: {value.RelativePath}{Environment.NewLine}" +
        SideMetadata(LeftTitle, _endpoints?.Context.Left,
            _endpoints is null || _endpoints.LeftUsesLocalPort, value) +
        SideMetadata(RightTitle, _endpoints?.Context.Right,
            _endpoints is not null && !_endpoints.LeftUsesLocalPort, value) +
        $"Conflict ID: {value.ConflictId}";

    private static string SideMetadata(string title, SyncEndpointContextSide? side, bool localPort,
        SyncConflictContent value) =>
        $"{title}:{Environment.NewLine}" +
        (side is null ? string.Empty : $"Location: {EndpointReviewPresentation.Location(side)}{Environment.NewLine}") +
        $"Size: {FormatSize(localPort ? value.LocalSize : value.DropboxSize)}{Environment.NewLine}" +
        $"Modified: {FormatTime(localPort ? value.LocalModifiedUtc : value.DropboxModifiedUtc)}{Environment.NewLine}" +
        $"Hash: {(localPort ? value.LocalHash : value.DropboxHash) ?? "Unavailable"}{Environment.NewLine}";

    private static string FormatSize(long? size) => size is null ? "Unavailable" : $"{size:N0} bytes";
    private static string FormatTime(DateTimeOffset? time) => time?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "Unavailable";
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ChooseLocal_Click(object sender, RoutedEventArgs e) => Choose(_endpoints?.Keep(SyncPairSide.Left) ?? SyncAuthorityConflictResolutionKind.KeepLocal);
    private void ChooseDropbox_Click(object sender, RoutedEventArgs e) => Choose(_endpoints?.Keep(SyncPairSide.Right) ?? SyncAuthorityConflictResolutionKind.KeepDropbox);
    private void ChooseBoth_Click(object sender, RoutedEventArgs e) => Choose(SyncAuthorityConflictResolutionKind.PreserveBoth);
    private void Choose(SyncAuthorityConflictResolutionKind resolution) { RequestedResolution = resolution; Close(); }
}

internal static class ConflictTextDiff
{
    private const int MaximumComparedCharacters = 2 * 1024 * 1024;
    private const int MaximumTokens = 8_000;
    private const int MaximumOutputCharacters = 2 * 1024 * 1024;
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".js", ".jsx", ".ts", ".tsx", ".py", ".rb", ".php", ".java",
        ".kt", ".kts", ".swift", ".go", ".rs", ".c", ".h", ".cpp", ".hpp", ".css", ".scss",
        ".html", ".htm", ".xml", ".xaml", ".json", ".yaml", ".yml", ".toml", ".sql", ".ps1",
        ".sh", ".cmd", ".bat"
    };

    public static bool IsCodePath(string path) => CodeExtensions.Contains(Path.GetExtension(path));

    public static string BoundedSide(string value)
    {
        if (value.Length <= MaximumComparedCharacters) return value;
        var half = MaximumComparedCharacters / 2;
        return value[..half] + Environment.NewLine +
               $"… {value.Length - MaximumComparedCharacters:N0} characters omitted from this bounded preview …" +
               Environment.NewLine + value[^half..];
    }

    public static string Unified(string local, string dropbox, bool ignoreWhitespace, bool wordMode,
        bool syntaxMode = false, string leftLabel = "This PC", string rightLabel = "Dropbox")
    {
        var boundedLocal = BoundedSide(local);
        var boundedDropbox = BoundedSide(dropbox);
        var left = Tokens(boundedLocal, wordMode, syntaxMode);
        var right = Tokens(boundedDropbox, wordMode, syntaxMode);
        if (left.Length > 4_000 || right.Length > 4_000)
            return BoundedLinear(left, right, ignoreWhitespace, wordMode || syntaxMode, leftLabel, rightLabel);
        var lengths = new int[left.Length + 1, right.Length + 1];
        for (var i = left.Length - 1; i >= 0; i--)
            for (var j = right.Length - 1; j >= 0; j--)
                lengths[i, j] = Equal(left[i], right[j], ignoreWhitespace)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        var output = new StringBuilder();
        output.AppendLine("--- " + leftLabel).AppendLine("+++ " + rightLabel);
        var x = 0;
        var y = 0;
        while (x < left.Length || y < right.Length)
        {
            if (x < left.Length && y < right.Length && Equal(left[x], right[y], ignoreWhitespace))
            {
                AppendBounded(output, "  ", left[x++], wordMode || syntaxMode); y++;
            }
            else if (y < right.Length && (x == left.Length || lengths[x, y + 1] >= lengths[x + 1, y]))
                AppendBounded(output, "+ ", right[y++], wordMode || syntaxMode);
            else
                AppendBounded(output, "- ", left[x++], wordMode || syntaxMode);
            if (output.Length >= MaximumOutputCharacters) break;
        }
        if (x < left.Length || y < right.Length) output.AppendLine("… diff output truncated at the safe display limit …");
        return output.ToString();
    }

    private static string BoundedLinear(string[] left, string[] right, bool ignoreWhitespace, bool wordMode,
        string leftLabel, string rightLabel)
    {
        var output = new StringBuilder().AppendLine("--- " + leftLabel).AppendLine("+++ " + rightLabel);
        var count = Math.Max(left.Length, right.Length);
        for (var i = 0; i < count; i++)
        {
            if (i < left.Length && i < right.Length && Equal(left[i], right[i], ignoreWhitespace))
                AppendBounded(output, "  ", left[i], wordMode);
            else
            {
                if (i < left.Length) AppendBounded(output, "- ", left[i], wordMode);
                if (i < right.Length) AppendBounded(output, "+ ", right[i], wordMode);
            }
            if (output.Length >= MaximumOutputCharacters)
            {
                output.AppendLine("… diff output truncated at the safe display limit …");
                break;
            }
        }
        return output.ToString();
    }

    private static string[] Tokens(string value, bool words, bool syntax)
    {
        string[] tokens;
        if (syntax)
            tokens = Regex.Matches(value,
                    """(?s:/\*.*?\*/)|(?://[^\r\n]*)|(?:#[^\r\n]*)|(?:"(?:\\.|[^"\\])*")|(?:'(?:\\.|[^'\\])*')|(?:[\p{L}_][\p{L}\p{N}_]*)|(?:\d+(?:\.\d+)?)|(?:\s+)|(?:.)""")
                .Select(match => match.Value).ToArray();
        else if (words)
            tokens = Regex.Split(value, @"(\s+|\b)").Where(item => item.Length > 0).ToArray();
        else
            tokens = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (tokens.Length <= MaximumTokens) return tokens;
        var retained = MaximumTokens / 2;
        return tokens.Take(retained)
            .Append($"… {tokens.Length - MaximumTokens:N0} tokens omitted …")
            .Concat(tokens.TakeLast(retained)).ToArray();
    }

    private static bool Equal(string left, string right, bool ignoreWhitespace) =>
        string.Equals(ignoreWhitespace ? Normalize(left) : left, ignoreWhitespace ? Normalize(right) : right,
            StringComparison.Ordinal);

    private static string Normalize(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();

    private static void AppendBounded(StringBuilder output, string prefix, string value, bool words)
    {
        var remaining = MaximumOutputCharacters - output.Length;
        if (remaining <= prefix.Length) return;
        output.Append(prefix);
        remaining -= prefix.Length;
        output.Append(value.AsSpan(0, Math.Min(value.Length, remaining)));
        if (!words || value.Contains('\n')) output.AppendLine();
    }
}
