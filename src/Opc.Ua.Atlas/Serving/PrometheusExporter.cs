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
        string machineName = "default_machine",
        QualityGuard? qualityGuard = null,
        SyntheticMetricsEngine? syntheticEngine = null,
        Mes.OeeResult? oeeResult = null,
        CollectorDiagnostics? diagnostics = null)
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

        // 1. Synthetic Metrics (e.g. Spindle Thermal Drift, Time-to-Starvation, Tool Wear)
        if (syntheticEngine != null)
        {
            var syntheticMetrics = syntheticEngine.ComputeMetrics(liveSnapshot);
            foreach (var syn in syntheticMetrics)
            {
                if (writtenHelp.Add(syn.MetricName))
                {
                    sb.AppendLine($"# HELP {syn.MetricName} {syn.Description}");
                    sb.AppendLine($"# TYPE {syn.MetricName} gauge");
                }
                string formatted = syn.Value.ToString("G", CultureInfo.InvariantCulture);
                sb.AppendLine($"{syn.MetricName}{{machine=\"{machineName}\"}} {formatted}");
            }
        }

        // 2. Telemetry Staleness / Quality Guard flags
        if (qualityGuard != null)
        {
            var records = qualityGuard.GetAllRecords();
            if (records.Count > 0)
            {
                if (writtenHelp.Add("opcua_data_stale"))
                {
                    sb.AppendLine("# HELP opcua_data_stale Staleness indicator (1=stale/timeout, 0=fresh)");
                    sb.AppendLine("# TYPE opcua_data_stale gauge");
                }

                foreach (var (nid, rec) in records)
                {
                    string safeId = nid.Replace("\"", "\\\"");
                    sb.AppendLine($"opcua_data_stale{{machine=\"{machineName}\",node_id=\"{safeId}\"}} {(rec.IsStale ? "1" : "0")}");
                }
            }
        }

        // 3. MES / Real-Time OEE Metrics
        if (oeeResult != null)
        {
            string orderLabel = $"machine=\"{machineName}\",order_id=\"{oeeResult.OrderId}\",part_number=\"{oeeResult.PartNumber}\"";

            if (writtenHelp.Add("opcua_oee_overall_ratio"))
            {
                sb.AppendLine("# HELP opcua_oee_overall_ratio Overall Equipment Effectiveness ratio (0.0 to 1.0)");
                sb.AppendLine("# TYPE opcua_oee_overall_ratio gauge");
            }
            sb.AppendLine($"opcua_oee_overall_ratio{{{orderLabel}}} {oeeResult.OeeOverall.ToString("F4", CultureInfo.InvariantCulture)}");

            if (writtenHelp.Add("opcua_oee_availability_ratio"))
            {
                sb.AppendLine("# HELP opcua_oee_availability_ratio Availability ratio (Operating Time / Planned Time)");
                sb.AppendLine("# TYPE opcua_oee_availability_ratio gauge");
            }
            sb.AppendLine($"opcua_oee_availability_ratio{{{orderLabel}}} {oeeResult.Availability.ToString("F4", CultureInfo.InvariantCulture)}");

            if (writtenHelp.Add("opcua_oee_performance_ratio"))
            {
                sb.AppendLine("# HELP opcua_oee_performance_ratio Performance ratio (Target Time / Operating Time)");
                sb.AppendLine("# TYPE opcua_oee_performance_ratio gauge");
            }
            sb.AppendLine($"opcua_oee_performance_ratio{{{orderLabel}}} {oeeResult.Performance.ToString("F4", CultureInfo.InvariantCulture)}");

            if (writtenHelp.Add("opcua_oee_quality_ratio"))
            {
                sb.AppendLine("# HELP opcua_oee_quality_ratio Quality ratio (Good Parts / Total Parts)");
                sb.AppendLine("# TYPE opcua_oee_quality_ratio gauge");
            }
            sb.AppendLine($"opcua_oee_quality_ratio{{{orderLabel}}} {oeeResult.Quality.ToString("F4", CultureInfo.InvariantCulture)}");

            if (writtenHelp.Add("opcua_work_order_completion_percent"))
            {
                sb.AppendLine("# HELP opcua_work_order_completion_percent Work order completion percentage");
                sb.AppendLine("# TYPE opcua_work_order_completion_percent gauge");
            }
            sb.AppendLine($"opcua_work_order_completion_percent{{{orderLabel}}} {oeeResult.CompletionPercent.ToString("F1", CultureInfo.InvariantCulture)}");
        }

        // 4. Gateway Self-Supervision Diagnostics
        if (diagnostics != null)
        {
            if (writtenHelp.Add("atlas_collector_uptime_seconds"))
            {
                sb.AppendLine("# HELP atlas_collector_uptime_seconds Atlas edge collector uptime in seconds");
                sb.AppendLine("# TYPE atlas_collector_uptime_seconds counter");
            }
            sb.AppendLine($"atlas_collector_uptime_seconds{{machine=\"{machineName}\"}} {diagnostics.UptimeSeconds.ToString("F1", CultureInfo.InvariantCulture)}");

            if (writtenHelp.Add("atlas_collector_scrape_latency_seconds"))
            {
                sb.AppendLine("# HELP atlas_collector_scrape_latency_seconds Latency of last OPC UA read/scrape cycle in seconds");
                sb.AppendLine("# TYPE atlas_collector_scrape_latency_seconds gauge");
            }
            sb.AppendLine($"atlas_collector_scrape_latency_seconds{{machine=\"{machineName}\"}} {(diagnostics.LastScrapeLatencyMs / 1000.0).ToString("F4", CultureInfo.InvariantCulture)}");

            if (writtenHelp.Add("atlas_collector_read_errors_total"))
            {
                sb.AppendLine("# HELP atlas_collector_read_errors_total Total failed OPC UA reads");
                sb.AppendLine("# TYPE atlas_collector_read_errors_total counter");
            }
            sb.AppendLine($"atlas_collector_read_errors_total{{machine=\"{machineName}\"}} {diagnostics.ReadErrorsTotal}");

            if (writtenHelp.Add("atlas_collector_reconnects_total"))
            {
                sb.AppendLine("# HELP atlas_collector_reconnects_total Total session reconnect attempts");
                sb.AppendLine("# TYPE atlas_collector_reconnects_total counter");
            }
            sb.AppendLine($"atlas_collector_reconnects_total{{machine=\"{machineName}\"}} {diagnostics.ReconnectsTotal}");

            if (writtenHelp.Add("atlas_collector_monitored_items_active"))
            {
                sb.AppendLine("# HELP atlas_collector_monitored_items_active Active monitored items subscribed");
                sb.AppendLine("# TYPE atlas_collector_monitored_items_active gauge");
            }
            sb.AppendLine($"atlas_collector_monitored_items_active{{machine=\"{machineName}\"}} {diagnostics.ActiveMonitoredItemsCount}");

            if (writtenHelp.Add("atlas_machine_connected"))
            {
                sb.AppendLine("# HELP atlas_machine_connected Machine connectivity status (1=connected, 0=disconnected)");
                sb.AppendLine("# TYPE atlas_machine_connected gauge");
            }
            sb.AppendLine($"atlas_machine_connected{{machine=\"{machineName}\"}} {(diagnostics.IsConnected ? "1" : "0")}");
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
