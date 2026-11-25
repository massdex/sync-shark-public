namespace Massdex.SyncShark.Engine.Tests;

/// <summary>Both inputs are mandatory: the checked-out source root and its public command inventory.</summary>
public sealed record CheckArguments(string Root, string Inventory)
{
    public static CheckArguments Parse(string[] args)
    {
        if (args.Length != 2 || !Directory.Exists(args[0]) || !File.Exists(args[1]))
            throw new ArgumentException("Usage: Engine.Tests <repository-root> <CLI-CAPABILITIES.md>");
        return new(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
    }
}
