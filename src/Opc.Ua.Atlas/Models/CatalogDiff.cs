using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Opc.Ua.Atlas.Models;

/// <summary>
/// Represents a change in user access permissions for a specific node between two identities.
/// </summary>
public record PermissionElevation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("baseUserAccess")] NodeAccessInfo? BaseUserAccess,
    [property: JsonPropertyName("targetUserAccess")] NodeAccessInfo? TargetUserAccess,
    [property: JsonPropertyName("readGranted")] bool ReadGranted,
    [property: JsonPropertyName("writeGranted")] bool WriteGranted);

/// <summary>
/// Summary statistics of the identity comparison.
/// </summary>
public record DiffSummary(
    [property: JsonPropertyName("newlyVisibleNodes")] int NewlyVisibleNodes,
    [property: JsonPropertyName("hiddenNodes")] int HiddenNodes,
    [property: JsonPropertyName("elevatedPermissions")] int ElevatedPermissions,
    [property: JsonPropertyName("unchangedNodes")] int UnchangedNodes);

/// <summary>
/// Represents the delta between two discovery catalogs (e.g. Anonymous vs Authenticated).
/// Reveals paywalled, hidden, or privileged tags on PLCs such as Siemens S7-1500 or Fanuc.
/// </summary>
public class CatalogDiff
{
    [JsonPropertyName("baseIdentity")]
    public required string BaseIdentity { get; init; }

    [JsonPropertyName("targetIdentity")]
    public required string TargetIdentity { get; init; }

    [JsonPropertyName("comparedAtUtc")]
    public DateTime ComparedAtUtc { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("summary")]
    public required DiffSummary Summary { get; init; }

    /// <summary>
    /// Nodes that are visible to the target identity but hidden from the base identity.
    /// </summary>
    [JsonPropertyName("newlyVisibleEntries")]
    public IReadOnlyList<CatalogEntry> NewlyVisibleEntries { get; init; } = Array.Empty<CatalogEntry>();

    /// <summary>
    /// Nodes that were visible to the base identity but not visible to the target identity.
    /// </summary>
    [JsonPropertyName("hiddenEntries")]
    public IReadOnlyList<CatalogEntry> HiddenEntries { get; init; } = Array.Empty<CatalogEntry>();

    /// <summary>
    /// Nodes present in both catalogs where the target identity has elevated permissions (e.g. gained Read or Write).
    /// </summary>
    [JsonPropertyName("elevations")]
    public IReadOnlyList<PermissionElevation> Elevations { get; init; } = Array.Empty<PermissionElevation>();

    /// <summary>
    /// Formats the comparison as a JSON string.
    /// </summary>
    public string ToJson(bool writeIndented = true)
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }

    /// <summary>
    /// Formats the comparison into a markdown table report.
    /// </summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Atlas Identity Diff: `{BaseIdentity}` vs `{TargetIdentity}`");
        sb.AppendLine();
        sb.AppendLine($"- **Newly visible nodes**: {Summary.NewlyVisibleNodes}");
        sb.AppendLine($"- **Nodes with elevated permissions**: {Summary.ElevatedPermissions}");
        sb.AppendLine($"- **Hidden nodes**: {Summary.HiddenNodes}");
        sb.AppendLine($"- **Unchanged nodes**: {Summary.UnchangedNodes}");
        sb.AppendLine();

        if (NewlyVisibleEntries.Count > 0)
        {
            sb.AppendLine("## Newly Visible Nodes (Revealed by Target Identity)");
            sb.AppendLine();
            sb.AppendLine("| Path | NodeId | DataType | Access |");
            sb.AppendLine("|---|---|---|---|");
            foreach (CatalogEntry e in NewlyVisibleEntries)
            {
                string access = $"R:{(e.Access?.Read == true ? "✓" : "✗")} W:{(e.Access?.Write == true ? "✓" : "✗")}";
                sb.AppendLine($"| `{e.Path}` | `{e.Id}` | {e.DataType ?? "-"} | {access} |");
            }
            sb.AppendLine();
        }

        if (Elevations.Count > 0)
        {
            sb.AppendLine("## Elevated Permissions");
            sb.AppendLine();
            sb.AppendLine("| Path | NodeId | Base Access | Target Access | Granted |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (PermissionElevation el in Elevations)
            {
                string baseAcc = $"R:{(el.BaseUserAccess?.Read == true ? "✓" : "✗")} W:{(el.BaseUserAccess?.Write == true ? "✓" : "✗")}";
                string targetAcc = $"R:{(el.TargetUserAccess?.Read == true ? "✓" : "✗")} W:{(el.TargetUserAccess?.Write == true ? "✓" : "✗")}";
                string granted = (el.ReadGranted ? "[Read] " : "") + (el.WriteGranted ? "[Write]" : "");
                sb.AppendLine($"| `{el.Path}` | `{el.Id}` | {baseAcc} | {targetAcc} | **{granted.Trim()}** |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
