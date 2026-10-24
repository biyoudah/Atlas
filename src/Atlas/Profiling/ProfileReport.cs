using System.Text.Json.Serialization;

namespace Atlas.Profiling;

/// <summary>
/// Represents a statistically significant correlation or synchronized transition
/// between two nodes discovered during passive profiling.
/// </summary>
public record CorrelatedTransition(
    [property: JsonPropertyName("sourceNodeId")] string SourceNodeId,
    [property: JsonPropertyName("targetNodeId")] string TargetNodeId,
    [property: JsonPropertyName("correlationCoefficient")] double CorrelationCoefficient,
    [property: JsonPropertyName("relationship")] string Relationship,
    [property: JsonPropertyName("confidence")] double Confidence);

/// <summary>
/// Aggregated report of a passive profiling session across observed address space nodes.
/// </summary>
public class ProfileReport
{
    [JsonPropertyName("startedAtUtc")]
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("completedAtUtc")]
    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("durationSeconds")]
    public double DurationSeconds => (CompletedAtUtc - StartedAtUtc).TotalSeconds;

    [JsonPropertyName("profiles")]
    public Dictionary<string, NodeProfile> Profiles { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("correlations")]
    public List<CorrelatedTransition> Correlations { get; init; } = [];
}
