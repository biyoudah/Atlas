using System.Text.Json.Serialization;

namespace Opc.Ua.Atlas.Models;

/// <summary>
/// Execution metrics and statistics for an address space crawl.
/// </summary>
public record CrawlStatistics(
    [property: JsonPropertyName("totalNodes")] int TotalNodes,
    [property: JsonPropertyName("totalVariables")] int TotalVariables,
    [property: JsonPropertyName("totalObjects")] int TotalObjects,
    [property: JsonPropertyName("totalMethods")] int TotalMethods,
    [property: JsonPropertyName("continuationPointsHandled")] int ContinuationPointsHandled,
    [property: JsonPropertyName("maxDepthReached")] int MaxDepthReached,
    [property: JsonPropertyName("durationMilliseconds")] long DurationMilliseconds);

/// <summary>
/// Server and session metadata associated with a generated catalog.
/// </summary>
public class CatalogMetadata
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "1.0.0";

    [JsonPropertyName("endpointUrl")]
    public required string EndpointUrl { get; init; }

    [JsonPropertyName("serverUri")]
    public string? ServerUri { get; init; }

    [JsonPropertyName("serverName")]
    public string? ServerName { get; init; }

    [JsonPropertyName("manufacturerName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManufacturerName { get; init; }

    [JsonPropertyName("softwareVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SoftwareVersion { get; init; }

    [JsonPropertyName("identityType")]
    public string IdentityType { get; init; } = "Anonymous";

    [JsonPropertyName("crawledAt")]
    public DateTime CrawledAtUtc { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("namespaces")]
    public IReadOnlyList<string> Namespaces { get; init; } = Array.Empty<string>();

    [JsonPropertyName("statistics")]
    public CrawlStatistics? Statistics { get; init; }
}

/// <summary>
/// Full discovery catalog document containing metadata and node entries.
/// </summary>
public class AtlasCatalog
{
    [JsonPropertyName("metadata")]
    public required CatalogMetadata Metadata { get; init; }

    [JsonPropertyName("entries")]
    public required IReadOnlyList<CatalogEntry> Entries { get; init; }
}
