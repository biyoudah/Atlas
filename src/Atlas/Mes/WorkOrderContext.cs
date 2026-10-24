using System.Text.Json.Serialization;

namespace Atlas.Mes;

/// <summary>
/// Manufacturing Execution System (MES) Work Order / Ordre de Fabrication (OF) context.
/// Reconciles declared IT production plans with physical OT machine signals.
/// </summary>
public sealed class WorkOrderContext
{
    [JsonPropertyName("orderId")]
    public string OrderId { get; set; } = "OF-DEFAULT";

    [JsonPropertyName("partNumber")]
    public string PartNumber { get; set; } = "PART-001";

    [JsonPropertyName("targetQuantity")]
    public int TargetQuantity { get; set; } = 500;

    [JsonPropertyName("nominalCycleTimeSeconds")]
    public double NominalCycleTimeSeconds { get; set; } = 45.0;

    [JsonPropertyName("plannedProductionMinutes")]
    public double PlannedProductionMinutes { get; set; } = 480.0;

    [JsonPropertyName("plannedDowntimeMinutes")]
    public double PlannedDowntimeMinutes { get; set; } = 30.0;

    [JsonPropertyName("startedAtUtc")]
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("declaredScrapParts")]
    public int DeclaredScrapParts { get; set; } = 0;
}
