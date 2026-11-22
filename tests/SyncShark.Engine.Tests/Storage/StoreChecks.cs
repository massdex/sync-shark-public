using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Massdex.SyncShark.Engine.Storage;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Checks the published store contract and its open-existing boundary.</summary>
public static class StoreChecks
{
    /// <summary>Every Store operation documents its behavior, and probing an absent image cannot create it.</summary>
    public static Task RunAsync()
    {
        var root = new DirectoryInfo(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SyncShark.sln"))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Source checkout not found.");
        var source = File.ReadAllText(Path.Combine(root.FullName, "src", "SyncShark.Engine", "Storage", "Store.cs"));
        foreach (var method in CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(token => token.RawKind == (int)SyntaxKind.PublicKeyword)) continue;
            var summary = method.GetLeadingTrivia().ToFullString();
            if (!summary.Contains("<summary>", StringComparison.Ordinal) ||
                summary.Contains("operation on the supplied facts", StringComparison.Ordinal))
                throw new InvalidDataException($"Missing behavior summary: {method.GetFirstToken().GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
        }
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        foreach (var readOnly in new[] { true, false })
        {
            try { using var store = new Store(path, readOnly, null!); }
            catch (SqliteException) when (!File.Exists(path)) { continue; }
            throw new InvalidDataException("Opening an absent store did not fail without creating it.");
        }
        return Task.CompletedTask;
    }
}
