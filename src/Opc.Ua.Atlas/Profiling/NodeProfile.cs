using System.Text.Json.Serialization;

namespace Opc.Ua.Atlas.Profiling;

/// <summary>
/// Profiling summary and behavioral telemetry for a specific OPC UA node.
/// </summary>
public record NodeProfile(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("browseName")] string BrowseName,
    [property: JsonPropertyName("classification")] SignalClassification Classification,
    [property: JsonPropertyName("sampleCount")] int SampleCount,
    [property: JsonPropertyName("changeCount")] int ChangeCount,
    [property: JsonPropertyName("changeRatePerMinute")] double ChangeRatePerMinute,
    [property: JsonPropertyName("observedMin")] double? ObservedMin,
    [property: JsonPropertyName("observedMax")] double? ObservedMax,
    [property: JsonPropertyName("lastObservedValue")] object? LastObservedValue,
    [property: JsonPropertyName("distinctValuesCount")] int DistinctValuesCount)
{
    [JsonIgnore]
    public List<DateTime> ChangeTimestamps { get; init; } = [];

    [JsonIgnore]
    public List<(DateTime Timestamp, double? NumVal, object? RawVal)> History { get; init; } = [];
}
