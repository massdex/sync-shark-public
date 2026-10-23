using System.IO;
using System.Reflection;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static class PairFailurePresentationChecks
{
    // Invoke the shared production formatter without constructing a window, authority or provider.
    internal static int Run()
    {
        var method = typeof(MainWindow).GetMethod("HumanizePairFailure",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(MainWindow), "HumanizePairFailure");
        var count = 0;
        foreach (var provider in new[] { SyncProviders.Dropbox, SyncProviders.GoogleDrive })
        {
            var pair = new SyncPair("presentation-only", "Presentation only", AppContext.BaseDirectory,
                0, 0, "/fixture", "fixture", Provider: provider);
            foreach (var failure in new[] { "Credential unavailable", "Token rejected", "Authorization failed" })
            {
                var text = (string)method.Invoke(null, [pair, failure])!;
                if (!text.StartsWith("The last check could not access " + SyncProviders.DisplayName(provider), StringComparison.Ordinal) ||
                    !text.Contains("Reconnect only if account verification fails", StringComparison.Ordinal) ||
                    !text.Contains("refresh this pair's Sync status", StringComparison.Ordinal) ||
                    text.Contains("is unavailable", StringComparison.Ordinal))
                    throw new InvalidDataException("Retained authorization failure must distinguish historical failure, account verification and folder verification.");
                count++;
            }
            var identity = (string)method.Invoke(null, [pair, "local identity changed"] )!;
            if (!identity.Contains("not the same folder", StringComparison.Ordinal))
                throw new InvalidDataException("An account check must not mask a folder identity failure.");
            count++;
        }
        const string shortFailure = "Completed 684 file transfers; 4 failed. Dropbox download refused (HTTP 409; unsupported_file).";
        if (WorkspaceVisuals.ProblemPreview(shortFailure) != shortFailure)
            throw new InvalidDataException("A short actionable failure was hidden or rewritten.");
        count++;
        var longFailure = shortFailure + " [application: " + new string('x', 9000) + "]";
        var preview = WorkspaceVisuals.ProblemPreview(longFailure);
        if (!preview.StartsWith(shortFailure, StringComparison.Ordinal) || preview.Length > 410 ||
            preview.Contains("[application:", StringComparison.Ordinal) || !preview.Contains("operation log", StringComparison.Ordinal))
            throw new InvalidDataException("The overview did not preserve counts/cause while linking the full trace.");
        count++;
        var longPath = shortFailure + " " + new string('p', 5000);
        if (WorkspaceVisuals.ProblemPreview(longPath).Length > 410 || longFailure.Length < 9000)
            throw new InvalidDataException("An unbroken path overflowed the overview or mutated retained detail.");
        count++;
        Console.WriteLine($"PASS {count} real production pair-failure presentation assertions; no window, state, provider or pause effects.");
        return 0;
    }
}
