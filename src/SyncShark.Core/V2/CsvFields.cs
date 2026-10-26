namespace Massdex.SyncShark.V2;

/// <summary>Shared CSV field encoding for plan provenance and CLI export, including spreadsheet safety.</summary>
internal static class CsvFields
{
    internal static string Encode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
