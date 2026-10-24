using System.Text.Json.Serialization;

namespace Atlas.Decoding;

/// <summary>
/// Represents a decoded leaf member of a complex structured variable or ExtensionObject.
/// </summary>
public record FlattenedMember(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("dataType")] string? DataType,
    [property: JsonPropertyName("value")] object? Value = null,
    [property: JsonPropertyName("description")] string? Description = null);
