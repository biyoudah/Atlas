using System.Globalization;
using System.Text;
using Atlas.Models;

namespace Atlas.Exporters;

/// <summary>
/// Exports the discovery catalog into a flat CSV format for spreadsheet analysis by plant engineers.
/// </summary>
public static class CsvCatalogExporter
{
    /// <summary>
    /// Writes the catalog to a TextWriter.
    /// </summary>
    public static void Export(AtlasCatalog catalog, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine("Id,Path,BrowseName,DisplayName,NodeClass,DataType,ValueRank,AccessRead,AccessWrite,UserAccessRead,UserAccessWrite,Unit,RangeLow,RangeHigh,Description");

        foreach (CatalogEntry e in catalog.Entries)
        {
            writer.WriteLine(string.Join(",",
                EscapeCsv(e.Id),
                EscapeCsv(e.Path),
                EscapeCsv(e.BrowseName),
                EscapeCsv(e.DisplayName),
                EscapeCsv(e.NodeClass),
                EscapeCsv(e.DataType),
                e.ValueRank?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                e.Access?.Read == true ? "1" : "0",
                e.Access?.Write == true ? "1" : "0",
                e.UserAccess?.Read == true ? "1" : "0",
                e.UserAccess?.Write == true ? "1" : "0",
                EscapeCsv(e.Unit),
                e.Range?.Low.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                e.Range?.High.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                EscapeCsv(e.Description)
            ));
        }
    }

    /// <summary>
    /// Writes the catalog to a specified file path in CSV format.
    /// </summary>
    public static async Task ExportToFileAsync(AtlasCatalog catalog, string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(filePath);

        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        Export(catalog, writer);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
