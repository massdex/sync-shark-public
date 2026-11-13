using Microsoft.CodeAnalysis;

internal sealed class TypeNode(string id, string project, INamedTypeSymbol symbol, bool product)
{
    internal string Id = id, Project = project;
    internal INamedTypeSymbol Symbol = symbol;
    internal bool Product = product;
    internal HashSet<string> Files = [], Risks = [], Roots = [], UnknownReasons = [];
    internal HashSet<Edge> Edges = [];
    internal sealed record Edge(string Target, string Path, int Position);
}
