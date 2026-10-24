using System.Text.Json.Serialization;
using Atlas.Models;

namespace Atlas.Mes;

/// <summary>
/// Real-time Overall Equipment Effectiveness (OEE / TRS) metrics.
/// </summary>
public sealed class OeeResult
{
    [JsonPropertyName("orderId")]
    public string OrderId { get; init; } = string.Empty;

    [JsonPropertyName("partNumber")]
    public string PartNumber { get; init; } = string.Empty;

    [JsonPropertyName("availability")]
    public double Availability { get; init; }

    [JsonPropertyName("performance")]
    public double Performance { get; init; }

    [JsonPropertyName("quality")]
    public double Quality { get; init; }

    [JsonPropertyName("oeeOverall")]
    public double OeeOverall { get; init; }

    [JsonPropertyName("targetQuantity")]
    public int TargetQuantity { get; init; }

    [JsonPropertyName("totalPartsProduced")]
    public int TotalPartsProduced { get; init; }

    [JsonPropertyName("goodPartsProduced")]
    public int GoodPartsProduced { get; init; }

    [JsonPropertyName("scrapPartsProduced")]
    public int ScrapPartsProduced { get; init; }

    [JsonPropertyName("completionPercent")]
    public double CompletionPercent { get; init; }

    [JsonPropertyName("operatingTimeSeconds")]
    public double OperatingTimeSeconds { get; init; }

    [JsonPropertyName("plannedProductionSeconds")]
    public double PlannedProductionSeconds { get; init; }

    [JsonPropertyName("estimatedTimeToCompleteSeconds")]
    public double? EstimatedTimeToCompleteSeconds { get; init; }
}

/// <summary>
/// Reconciles live OT telemetry with MES work orders to calculate real-time OEE / TRS
/// in conformance with ISO 22400-2:2014 (Section 7.3 - Overall Equipment Effectiveness):
/// - Availability (A) = Operating Time / Planned Production Time (Net of planned breaks)
/// - Performance (P) = (Total Quantity Produced × Nominal Cycle Time) / Operating Time
/// - Quality (Q) = Good Quantity / Total Quantity Produced
/// - Overall OEE = A × P × Q
///
/// Handles boundary conditions:
/// - Division-by-zero protection on zero operating time or zero produced parts.
/// - OF transitions: automatically resets runtime accumulator upon encountering a new OrderId.
/// - Operator scrap entry anomalies (Scrap > Total) safely clamped to 0 Good parts.
/// </summary>
public sealed class OeeCalculator
{
    private DateTime _lastEvaluationTime = DateTime.UtcNow;
    private double _accumulatedOperatingTimeSeconds = 0;
    private bool _isCurrentlyRunning = false;
    private string? _activeOrderId;

    /// <summary>
    /// Updates the machine running state accumulator.
    /// </summary>
    public void UpdateMachineState(bool isRunning, DateTime? timestampUtc = null)
    {
        DateTime now = timestampUtc ?? DateTime.UtcNow;
        if (_isCurrentlyRunning)
        {
            double elapsed = (now - _lastEvaluationTime).TotalSeconds;
            if (elapsed > 0 && elapsed < 3600) // ignore huge leaps
            {
                _accumulatedOperatingTimeSeconds += elapsed;
            }
        }

        _isCurrentlyRunning = isRunning;
        _lastEvaluationTime = now;
    }

    /// <summary>
    /// Calculates real-time OEE metrics from the current work order and telemetry snapshot.
    /// </summary>
    public OeeResult Calculate(
        WorkOrderContext workOrder,
        IReadOnlyDictionary<string, (CatalogEntry Entry, object? Value)> snapshot,
        DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(workOrder);
        DateTime now = nowUtc ?? DateTime.UtcNow;

        // Reset runtime accumulator if work order changed
        if (_activeOrderId != null && !string.Equals(_activeOrderId, workOrder.OrderId, StringComparison.OrdinalIgnoreCase))
        {
            _accumulatedOperatingTimeSeconds = 0;
            _lastEvaluationTime = now;
        }
        _activeOrderId = workOrder.OrderId;

        int totalParts = 0;
        int scrapParts = Math.Max(0, workOrder.DeclaredScrapParts);

        // Extract parts count and machine state from snapshot
        foreach (var (_, (entry, val)) in snapshot)
        {
            if (val == null) continue;
            string label = entry.Inferred?.Label ?? entry.BrowseName ?? string.Empty;

            if (label.Equals("parts_produced", StringComparison.OrdinalIgnoreCase) ||
                label.Equals("part_counter", StringComparison.OrdinalIgnoreCase))
            {
                if (TryExtractInt(val, out int parts))
                {
                    totalParts = parts;
                }
            }
            else if (label.Equals("machine_state", StringComparison.OrdinalIgnoreCase) ||
                     label.Equals("operating_mode", StringComparison.OrdinalIgnoreCase))
            {
                var stdState = Standard.MachineToolStandards.ParseStandardState(val);
                bool running = stdState is Standard.MachineToolStandards.MachineToolState.Active or Standard.MachineToolStandards.MachineToolState.Moving;

                if (val is bool b)
                {
                    running = b;
                }

                UpdateMachineState(running, now);
            }
        }

        int goodParts = Math.Max(0, totalParts - scrapParts);

        // Availability calculation (ISO 22400-2): Actual Execution Time / Planned Production Time
        double netPlannedMinutes = Math.Max(1.0, workOrder.PlannedProductionMinutes - workOrder.PlannedDowntimeMinutes);
        double plannedSeconds = netPlannedMinutes * 60.0;
        double operatingSeconds = Math.Max(0.0, _accumulatedOperatingTimeSeconds);
        double availability = plannedSeconds > 0
            ? Math.Min(1.0, Math.Max(0.0, operatingSeconds / plannedSeconds))
            : 0.0;

        // Performance calculation (ISO 22400-2): (Actual Parts * Nominal Cycle Time) / Operating Time
        double idealOperatingTimeForProducedParts = totalParts * workOrder.NominalCycleTimeSeconds;
        double performance = operatingSeconds > 0
            ? Math.Min(1.0, Math.Max(0.0, idealOperatingTimeForProducedParts / operatingSeconds))
            : 0.0;

        // Quality calculation (ISO 22400-2): Good Parts / Total Parts
        double quality = totalParts > 0
            ? Math.Min(1.0, Math.Max(0.0, (double)goodParts / totalParts))
            : 1.0;

        // Overall OEE (ISO 22400-2)
        double oee = Math.Round(availability * performance * quality, 4);

        // Completion & ETA
        double completionPercent = workOrder.TargetQuantity > 0
            ? Math.Round(Math.Min(100.0, ((double)totalParts / workOrder.TargetQuantity) * 100.0), 1)
            : 0.0;

        int remainingParts = Math.Max(0, workOrder.TargetQuantity - totalParts);
        double? etaSeconds = remainingParts > 0 && workOrder.NominalCycleTimeSeconds > 0
            ? Math.Round(remainingParts * workOrder.NominalCycleTimeSeconds, 0)
            : 0.0;

        return new OeeResult
        {
            OrderId = workOrder.OrderId,
            PartNumber = workOrder.PartNumber,
            Availability = Math.Round(availability, 4),
            Performance = Math.Round(performance, 4),
            Quality = Math.Round(quality, 4),
            OeeOverall = oee,
            TargetQuantity = workOrder.TargetQuantity,
            TotalPartsProduced = totalParts,
            GoodPartsProduced = goodParts,
            ScrapPartsProduced = scrapParts,
            CompletionPercent = completionPercent,
            OperatingTimeSeconds = Math.Round(operatingSeconds, 1),
            PlannedProductionSeconds = plannedSeconds,
            EstimatedTimeToCompleteSeconds = etaSeconds
        };
    }

    /// <summary>
    /// Directly seeds the accumulated operating time (useful for unit testing or reconnect restores).
    /// </summary>
    public void SetOperatingTimeSeconds(double seconds)
    {
        _accumulatedOperatingTimeSeconds = Math.Max(0, seconds);
    }

    private static bool TryExtractInt(object val, out int result)
    {
        switch (val)
        {
            case int i: result = i; return true;
            case uint u: result = (int)u; return true;
            case long l: result = (int)l; return true;
            case ulong ul: result = (int)ul; return true;
            case short s: result = s; return true;
            case ushort us: result = us; return true;
            case byte b: result = b; return true;
            case double d: result = (int)d; return true;
            case float f: result = (int)f; return true;
            default:
                result = 0;
                return false;
        }
    }
}
