using Opc.Ua.Atlas.Models;

namespace Opc.Ua.Atlas.Serving;

/// <summary>
/// A synthetic metric computed dynamically from multiple raw OPC UA signals.
/// </summary>
public record SyntheticMetric(
    string Key,
    string Label,
    string MetricName,
    double Value,
    string Unit,
    string Description,
    SignalCategory Category = SignalCategory.Sample);

/// <summary>
/// Operational & logistics synthetic telemetry engine:
/// 1. Time-to-starvation: Pallet stock × cycle time (buffer depletion horizon).
/// 2. Spindle thermal delta: Temperature difference relative to ambient reference baseline (ΔT = T_spindle - T_ref).
/// 3. Tool wear ratio: Tool wear offset or lifetime consumption percentage.
/// </summary>
public sealed class SyntheticMetricsEngine
{
    public double DefaultAmbientTemperatureCelsius { get; set; } = 22.0;
    public double MaxToolWearLimitMm { get; set; } = 0.50; // 500 microns max wear

    /// <summary>
    /// Computes synthetic metrics given a snapshot of live node values.
    /// </summary>
    public IReadOnlyList<SyntheticMetric> ComputeMetrics(
        IReadOnlyDictionary<string, (CatalogEntry Entry, object? Value)> snapshot)
    {
        var metrics = new List<SyntheticMetric>();

        double? spindleTemp = null;
        double? ambientTemp = null;
        double? cycleTimeSec = null;
        double? partsRemaining = null;
        double? toolWear = null;
        double? toolLifeRemainingPercent = null;

        foreach (var (_, (entry, val)) in snapshot)
        {
            if (val == null || !TryExtractDouble(val, out double num))
            {
                continue;
            }

            string label = entry.Inferred?.Label ?? entry.BrowseName ?? string.Empty;

            if (label.Equals("spindle_temperature", StringComparison.OrdinalIgnoreCase))
            {
                spindleTemp = num;
            }
            else if (label.Contains("ambient", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("cabin_temperature", StringComparison.OrdinalIgnoreCase))
            {
                ambientTemp = num;
            }
            else if (label.Equals("cycle_time", StringComparison.OrdinalIgnoreCase))
            {
                cycleTimeSec = num;
            }
            else if (label.Contains("stock", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("pallet_parts", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("parts_remaining", StringComparison.OrdinalIgnoreCase))
            {
                partsRemaining = num;
            }
            else if (label.Equals("tool_wear", StringComparison.OrdinalIgnoreCase))
            {
                toolWear = num;
            }
            else if (label.Contains("tool_life", StringComparison.OrdinalIgnoreCase))
            {
                toolLifeRemainingPercent = num;
            }
        }

        // 1. Spindle Thermal Delta: Spindle Temp - Ambient Temp
        if (spindleTemp.HasValue)
        {
            double refTemp = ambientTemp ?? DefaultAmbientTemperatureCelsius;
            double drift = Math.Round(spindleTemp.Value - refTemp, 2);

            metrics.Add(new SyntheticMetric(
                Key: "synthetic:spindle_thermal_drift",
                Label: "spindle_thermal_drift",
                MetricName: "opcua_spindle_thermal_drift_celsius",
                Value: drift,
                Unit: "°C",
                Description: "Spindle temperature delta relative to ambient reference baseline.",
                Category: SignalCategory.Sample));
        }

        // 2. Logistics: Time-To-Starvation (Stock Remaining × Cycle Time)
        if (partsRemaining.HasValue && cycleTimeSec.HasValue && cycleTimeSec.Value > 0)
        {
            double ttsSeconds = Math.Max(0, Math.Round(partsRemaining.Value * cycleTimeSec.Value, 1));

            metrics.Add(new SyntheticMetric(
                Key: "synthetic:time_to_starvation",
                Label: "time_to_starvation",
                MetricName: "opcua_time_to_starvation_seconds",
                Value: ttsSeconds,
                Unit: "s",
                Description: "Time remaining until pallet stock starvation based on active cycle time.",
                Category: SignalCategory.Event));
        }

        // 3. Tool Wear Urgency
        if (toolWear.HasValue)
        {
            double wearRatio = Math.Min(100.0, Math.Max(0.0, (toolWear.Value / MaxToolWearLimitMm) * 100.0));
            metrics.Add(new SyntheticMetric(
                Key: "synthetic:tool_wear_urgency",
                Label: "tool_wear_urgency",
                MetricName: "opcua_tool_wear_urgency_percent",
                Value: Math.Round(wearRatio, 1),
                Unit: "%",
                Description: "Tool wear offset consumption percentage against safety tolerance.",
                Category: SignalCategory.Sample));
        }
        else if (toolLifeRemainingPercent.HasValue)
        {
            double urgency = Math.Max(0.0, 100.0 - toolLifeRemainingPercent.Value);
            metrics.Add(new SyntheticMetric(
                Key: "synthetic:tool_wear_urgency",
                Label: "tool_wear_urgency",
                MetricName: "opcua_tool_wear_urgency_percent",
                Value: Math.Round(urgency, 1),
                Unit: "%",
                Description: "Tool wear urgency calculated from remaining life counter.",
                Category: SignalCategory.Sample));
        }

        return metrics;
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
