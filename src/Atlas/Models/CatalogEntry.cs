using System.Text.Json.Serialization;

namespace Atlas.Models;

/// <summary>
/// Read and write access permissions for a node.
/// </summary>
public record NodeAccessInfo(
    [property: JsonPropertyName("read")] bool Read,
    [property: JsonPropertyName("write")] bool Write,
    [property: JsonPropertyName("executable")] bool Executable = false);

/// <summary>
/// Engineering unit range (low / high).
/// </summary>
public record EURangeInfo(
    [property: JsonPropertyName("low")] double Low,
    [property: JsonPropertyName("high")] double High);

/// <summary>
/// Inferred semantic meaning with evidence and confidence score.
/// </summary>
public record InferredSemantics(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("evidence")] IReadOnlyList<string> Evidence);

/// <summary>
/// MTConnect / ANSI-standard signal classification.
/// Sample: continuously variable analog metric (e.g. speed, temp, pressure, vibration).
/// Event: discrete state change, counter, or operational flag (e.g. state, parts, tool number).
/// Condition: health status, interlock, or alarm (e.g. emergency stop, door locked, fault).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignalCategory
{
    Sample,
    Event,
    Condition
}

/// <summary>
/// A protocol-agnostic, stable catalog record of an OPC UA node.
/// </summary>
public class CatalogEntry
{
    /// <summary>
    /// Stable namespace-URI-based node identifier (e.g. nsu=http://vendor.example/plc/;s=DB10.Var_27).
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// MTConnect taxonomy category (Sample, Event, Condition).
    /// </summary>
    [JsonPropertyName("category")]
    public SignalCategory Category { get; set; } = SignalCategory.Sample;

    /// <summary>
    /// Hierarchical browse path (e.g. "Objects/Machine/DB10/Var_27").
    /// </summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("browseName")]
    public string? BrowseName { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("nodeClass")]
    public string NodeClass { get; init; } = "Variable";

    [JsonPropertyName("dataType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataType { get; init; }

    [JsonPropertyName("dataTypeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataTypeId { get; init; }

    [JsonPropertyName("valueRank")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ValueRank { get; init; }

    [JsonPropertyName("access")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeAccessInfo? Access { get; init; }

    [JsonPropertyName("userAccess")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeAccessInfo? UserAccess { get; init; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("range")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EURangeInfo? Range { get; set; }

    [JsonPropertyName("minimumSamplingInterval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinimumSamplingInterval { get; init; }

    [JsonPropertyName("historizing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Historizing { get; init; }

    [JsonPropertyName("enumStrings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? EnumStrings { get; set; }

    [JsonPropertyName("inferred")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InferredSemantics? Inferred { get; set; }

    [JsonPropertyName("members")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Decoding.FlattenedMember>? Members { get; set; }
}
