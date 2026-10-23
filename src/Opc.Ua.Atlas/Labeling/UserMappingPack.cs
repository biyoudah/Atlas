using System.Text.Json.Serialization;
using Opc.Ua.Atlas.Models;

namespace Opc.Ua.Atlas.Labeling;

/// <summary>
/// A user-defined rule mapping raw PLC tag identifiers or paths to standard semantic labels.
/// User rules take absolute precedence (100% confidence) over automated heuristic inference.
/// </summary>
public record UserMappingRule(
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("unit")] string? Unit = null,
    [property: JsonPropertyName("scale")] double? Scale = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("category")] SignalCategory? Category = null);

/// <summary>
/// A collection of machine/vendor specific mapping rules (e.g. for Siemens S7-1500 or Fanuc 31i).
/// </summary>
public class UserMappingPack
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("vendor")]
    public string? Vendor { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0.0";

    [JsonPropertyName("rules")]
    public List<UserMappingRule> Rules { get; init; } = [];
}
