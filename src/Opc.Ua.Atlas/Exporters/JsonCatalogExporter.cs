using System.Text.Json;
using System.Text.Json.Serialization;
using Opc.Ua.Atlas.Models;

namespace Opc.Ua.Atlas.Exporters;

/// <summary>
/// Serializes the discovery catalog into human-readable, schema-valid JSON.
/// </summary>
public static class JsonCatalogExporter
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Serializes the catalog into a JSON string.
    /// </summary>
    public static string ToJson(AtlasCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return JsonSerializer.Serialize(catalog, s_options);
    }

    /// <summary>
    /// Serializes the catalog into a stream asynchronously.
    /// </summary>
    public static async Task ExportAsync(AtlasCatalog catalog, Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(stream);
        await JsonSerializer.SerializeAsync(stream, catalog, s_options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the catalog to a specified file path.
    /// </summary>
    public static async Task ExportToFileAsync(AtlasCatalog catalog, string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(filePath);

        using FileStream fs = File.Create(filePath);
        await ExportAsync(catalog, fs, ct).ConfigureAwait(false);
    }
}
