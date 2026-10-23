using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Opc.Ua.Atlas.Models;

namespace Opc.Ua.Atlas.Serving;

/// <summary>
/// Formats and serves OPC UA live catalog metrics in standard Prometheus exposition format.
/// Follows Prometheus best practices:
/// - Values are never stored in label dimensions (avoids series churn).
/// - Enums/states are exported as state-set boolean indicators (e.g. opcua_machine_state{state="ACTIVE"} 1).
/// - Metric names incorporate engineering units (e.g. _rpm, _bar, _celsius, _total).
/// </summary>
public static class PrometheusExporter
{
    private static readonly Regex s_sanitizeRegex = new(@"[^a-zA-Z0-9_]", RegexOptions.Compiled);

    /// <summary>
    /// Serializes current live values into Prometheus text exposition format (v0.0.4).
    /// </summary>
    public static string SerializeMetrics(
        IReadOnlyDictionary<string, (CatalogEntry Entry, object? Value)> liveSnapshot,
        string machineName = "default_machine")
    {
        var sb = new StringBuilder();
        var writtenHelp = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (id, (entry, val)) in liveSnapshot)
        {
            if (val == null)
            {
                continue;
            }

            string labelName = entry.Inferred?.Label ?? SanitizeMetricName(entry.BrowseName ?? "node");
            string metricName = FormatMetricName(labelName, entry.Unit);
            string safeId = entry.Id.Replace("\"", "\\\"");

            // State / Enum / String values -> State-Set metrics
            if (val is string strVal)
            {
                if (writtenHelp.Add(metricName))
                {
                    sb.AppendLine($"# HELP {metricName} Discrete state indicator (1=active, 0=inactive)");
                    sb.AppendLine($"# TYPE {metricName} gauge");
                }

                string safeState = strVal.ToUpperInvariant().Replace("\"", "\\\"");
                sb.AppendLine($"{metricName}{{machine=\"{machineName}\",node_id=\"{safeId}\",state=\"{safeState}\"}} 1");
                continue;
            }

            // Boolean values
            if (val is bool bVal)
            {
                if (writtenHelp.Add(metricName))
                {
                    sb.AppendLine($"# HELP {metricName} Boolean status flag (1=true, 0=false)");
                    sb.AppendLine($"# TYPE {metricName} gauge");
                }

                sb.AppendLine($"{metricName}{{machine=\"{machineName}\",node_id=\"{safeId}\"}} {(bVal ? "1" : "0")}");
                continue;
            }

            // Numeric values
            if (TryExtractDouble(val, out double numVal))
            {
                bool isCounter = metricName.EndsWith("_total", StringComparison.Ordinal);
                string metricType = isCounter ? "counter" : "gauge";

                if (writtenHelp.Add(metricName))
                {
                    string helpText = entry.Description ?? $"{labelName} telemetry reading";
                    sb.AppendLine($"# HELP {metricName} {helpText}");
                    sb.AppendLine($"# TYPE {metricName} {metricType}");
                }

                string formattedVal = numVal.ToString("G", CultureInfo.InvariantCulture);
                sb.AppendLine($"{metricName}{{machine=\"{machineName}\",node_id=\"{safeId}\"}} {formattedVal}");
            }
        }

        return sb.ToString();
    }

    private static string FormatMetricName(string label, string? unit)
    {
        string safeLabel = SanitizeMetricName(label);
        string name = $"opcua_{safeLabel}";

        if (label == "parts_produced" || label.EndsWith("_counter", StringComparison.OrdinalIgnoreCase))
        {
            if (!name.EndsWith("_total", StringComparison.Ordinal))
            {
                name += "_total";
            }
            return name;
        }

        string cleanUnit = unit?.Trim().ToLowerInvariant() ?? string.Empty;
        if (cleanUnit == "rpm" && !name.EndsWith("_rpm", StringComparison.Ordinal))
        {
            name += "_rpm";
        }
        else if (cleanUnit == "bar" && !name.EndsWith("_bar", StringComparison.Ordinal))
        {
            name += "_bar";
        }
        else if (cleanUnit is "°c" or "deg c" or "c" && !name.EndsWith("_celsius", StringComparison.Ordinal))
        {
            name += "_celsius";
        }
        else if (cleanUnit is "mm/min" && !name.EndsWith("_mm_per_min", StringComparison.Ordinal))
        {
            name += "_mm_per_min";
        }
        else if (cleanUnit is "s" or "sec" && !name.EndsWith("_seconds", StringComparison.Ordinal))
        {
            name += "_seconds";
        }

        return name;
    }

    private static string SanitizeMetricName(string input)
    {
        string sanitized = s_sanitizeRegex.Replace(input, "_").Trim('_');
        return string.IsNullOrEmpty(sanitized) ? "metric" : sanitized.ToLowerInvariant();
    }

    private static bool TryExtractDouble(object val, out double result)
    {
        switch (val)
        {
            case double d: result = d; return true;
            case float f: result = f; return true;
            case int i: result = i; return true;
            case uint u: result = u; return true;
            case long l: result = l; return true;
            case ulong ul: result = ul; return true;
            case short s: result = s; return true;
            case ushort us: result = us; return true;
            case byte b: result = b; return true;
            case sbyte sb: result = sb; return true;
            default:
                result = 0;
                return false;
        }
    }
}
